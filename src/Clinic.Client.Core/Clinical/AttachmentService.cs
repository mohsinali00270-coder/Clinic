using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Clinic.Client.Core.Local;
using Clinic.Client.Core.Sync;
using Clinic.Shared.Scheduling;

namespace Clinic.Client.Core.Clinical;

/// <summary>Moves attachment bytes; the metadata travels through normal sync.</summary>
public interface IFileTransport
{
    Task<UploadStatusDto> StatusAsync(Guid attachmentId, CancellationToken ct);
    Task PutChunkAsync(Guid attachmentId, int index, ReadOnlyMemory<byte> data, CancellationToken ct);
    Task<UploadStatusDto> CompleteAsync(Guid attachmentId, int totalChunks, CancellationToken ct);
    Task<byte[]> DownloadAsync(Guid attachmentId, CancellationToken ct);
}

public sealed class HttpFileTransport(HttpClient http, Func<CancellationToken, Task<string?>> accessTokenProvider) : IFileTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<UploadStatusDto> StatusAsync(Guid id, CancellationToken ct) =>
        SendAsync<UploadStatusDto>(() => new HttpRequestMessage(HttpMethod.Get, $"{SchedulingRoutes.Files}/{id}/status"), ct);

    public Task PutChunkAsync(Guid id, int index, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        SendAsync<object?>(() => new HttpRequestMessage(HttpMethod.Put, $"{SchedulingRoutes.Files}/{id}/chunks/{index}")
        {
            Content = new ReadOnlyMemoryContent(data) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
        }, ct);

    public Task<UploadStatusDto> CompleteAsync(Guid id, int totalChunks, CancellationToken ct) =>
        SendAsync<UploadStatusDto>(() => new HttpRequestMessage(HttpMethod.Post, $"{SchedulingRoutes.Files}/{id}/complete")
        {
            Content = JsonContent.Create(new CompleteUploadRequest(totalChunks), options: Json),
        }, ct);

    public async Task<byte[]> DownloadAsync(Guid id, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{SchedulingRoutes.Files}/{id}");
        using var response = await SendRawAsync(message, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        using var message = build();
        using var response = await SendRawAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default!;
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage message, CancellationToken ct)
    {
        if (await accessTokenProvider(ct) is { } token) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await http.SendAsync(message, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SyncTransportException(SyncFailureKind.Transient, "The server could not be reached.");
        }
        if (response.IsSuccessStatusCode) return response;

        var body = await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        var kind = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests
            ? SyncFailureKind.Transient : SyncFailureKind.Refused;
        throw new SyncTransportException(kind, $"File transfer failed ({(int)response.StatusCode}): {body}");
    }
}

/// <summary>
/// FR-PAT-06 / FR-OFF-23 / FR-SEC-02: attachments are saved to app storage first (AES-GCM encrypted), their
/// metadata syncs like any record, and the bytes upload in the background in resumable 1 MB chunks.
/// </summary>
public sealed class AttachmentService(LocalDatabase db, LocalStore store, string filesDirectory, byte[] fileKey)
{
    public const int MaxBytes = 25 * 1024 * 1024;

    public async Task<LocalAttachment> AddAsync(string entityType, Guid recordId, Guid? branchId, Stream content,
        string fileName, string contentType, string? caption, string category)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0) throw new ArgumentException("The file is empty.");
        if (bytes.Length > MaxBytes) throw new ArgumentException("Files must be smaller than 25 MB.");

        var attachment = new LocalAttachment
        {
            Id = Guid.NewGuid(), RecordId = recordId, EntityType = entityType, BranchId = branchId,
            FileName = Path.GetFileName(fileName), ContentType = contentType, SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Caption = caption, Category = category,
            UploadState = UploadState.Pending,
        };
        attachment.LocalPath = await WriteEncryptedAsync(attachment.Id, bytes);
        await store.InsertAsync(attachment);
        return attachment;
    }

    /// <summary>Decrypted bytes, downloading (and caching encrypted) when the file came from another device.</summary>
    public async Task<byte[]> ReadAsync(LocalAttachment attachment, IFileTransport? transport, CancellationToken ct = default)
    {
        if (attachment.LocalPath is { } path && File.Exists(path)) return Decrypt(await File.ReadAllBytesAsync(path, ct));
        if (transport is null || !attachment.IsUploaded)
            throw new InvalidOperationException("This file is not on this device yet and cannot be downloaded right now.");

        var bytes = await transport.DownloadAsync(attachment.Id, ct);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), attachment.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded file is damaged (checksum mismatch).");

        attachment.LocalPath = await WriteEncryptedAsync(attachment.Id, bytes);
        await db.Connection.ExecuteAsync("UPDATE Attachments SET LocalPath = ? WHERE Id = ?", attachment.LocalPath, attachment.Id);
        return bytes;
    }

    /// <summary>
    /// Uploads files whose metadata the server already has (RowVersion &gt; 0). Resumable: asks which chunks
    /// arrived and sends only the rest. Returns how many files finished.
    /// </summary>
    public async Task<int> UploadPendingAsync(IFileTransport transport, CancellationToken ct = default)
    {
        var pending = await db.Connection.QueryAsync<LocalAttachment>(
            "SELECT * FROM Attachments WHERE UploadState = ? AND RowVersion > 0 AND IsDeleted = 0", (int)UploadState.Pending);
        var done = 0;
        foreach (var a in pending)
        {
            if (a.LocalPath is null || !File.Exists(a.LocalPath)) continue;
            var bytes = Decrypt(await File.ReadAllBytesAsync(a.LocalPath, ct));

            var status = await transport.StatusAsync(a.Id, ct);
            if (!status.IsComplete)
            {
                var chunkSize = status.ChunkSize;
                var total = (int)Math.Ceiling(bytes.Length / (double)chunkSize);
                var have = status.ReceivedChunks.ToHashSet();
                for (var i = 0; i < total; i++)
                {
                    if (have.Contains(i)) continue;
                    var start = i * chunkSize;
                    await transport.PutChunkAsync(a.Id, i, bytes.AsMemory(start, Math.Min(chunkSize, bytes.Length - start)), ct);
                }
                await transport.CompleteAsync(a.Id, total, ct);
            }

            await db.Connection.ExecuteAsync("UPDATE Attachments SET UploadState = ?, IsUploaded = 1 WHERE Id = ?", (int)UploadState.Uploaded, a.Id);
            done++;
        }
        return done;
    }

    private async Task<string> WriteEncryptedAsync(Guid id, byte[] plain)
    {
        Directory.CreateDirectory(filesDirectory);
        var path = Path.Combine(filesDirectory, $"{id:N}.bin");
        await File.WriteAllBytesAsync(path, Encrypt(plain));
        return path;
    }

    // [12-byte nonce][16-byte tag][ciphertext]
    private byte[] Encrypt(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(fileKey, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    private byte[] Decrypt(byte[] data)
    {
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(fileKey, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return plain;
    }
}

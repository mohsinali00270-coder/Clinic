using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>FR-DATA-05: files live in blob storage (Azure Blob / S3 / local disk), never in SQL.</summary>
public interface IFileStore
{
    Task WriteChunkAsync(Guid tenantId, Guid fileId, int index, Stream data, CancellationToken ct);
    Task<IReadOnlyList<int>> ListChunksAsync(Guid tenantId, Guid fileId, CancellationToken ct);

    /// <summary>Joins chunks 0..total-1 into the final object and returns its key, size and SHA-256 (hex, lower case).</summary>
    Task<(string Key, long Size, string Sha256)> AssembleAsync(Guid tenantId, Guid fileId, int totalChunks, CancellationToken ct);
    Task DeleteChunksAsync(Guid tenantId, Guid fileId, CancellationToken ct);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct);
}

/// <summary>Raised after sync data changes so connected devices pull right away (FR-SYN-03).</summary>
public interface IChangeNotifier
{
    /// <summary>A null branch means a tenant-wide change (config).</summary>
    void Notify(Guid tenantId, IReadOnlyCollection<Guid?> branchIds);
}

/// <summary>
/// FR-OFF-23 / FR-PAT-06: chunked, resumable upload of an attachment whose metadata already synced.
/// The device asks which chunks arrived, sends the rest, then completes; the server checks size and SHA-256.
/// </summary>
public sealed class FileService(IAppDbContext db, ICurrentUser user, IFileStore store)
{
    public const int ChunkSize = 1024 * 1024;

    public async Task<UploadStatusDto> StatusAsync(Guid attachmentId, CancellationToken ct)
    {
        var a = await LoadForUploadAsync(attachmentId, ct);
        var chunks = a.IsUploaded ? [] : await store.ListChunksAsync(a.TenantId, a.Id, ct);
        return new UploadStatusDto(a.Id, ChunkSize, chunks, a.IsUploaded);
    }

    public async Task PutChunkAsync(Guid attachmentId, int index, Stream body, long? length, CancellationToken ct)
    {
        var a = await LoadForUploadAsync(attachmentId, ct);
        if (a.IsUploaded) throw new ConflictException("This file is already uploaded.");
        var maxChunks = (int)Math.Ceiling(a.SizeBytes / (double)ChunkSize);
        if (index < 0 || index >= maxChunks) throw new RequestValidationException("index", $"Chunk index must be 0–{maxChunks - 1}.");
        if (length is > ChunkSize) throw new RequestValidationException("body", $"Chunks are at most {ChunkSize} bytes.");
        await store.WriteChunkAsync(a.TenantId, a.Id, index, body, ct);
    }

    public async Task<UploadStatusDto> CompleteAsync(Guid attachmentId, int totalChunks, CancellationToken ct)
    {
        var a = await LoadForUploadAsync(attachmentId, ct);
        if (a.IsUploaded) return new UploadStatusDto(a.Id, ChunkSize, [], true);

        var expected = (int)Math.Ceiling(a.SizeBytes / (double)ChunkSize);
        if (totalChunks != expected) throw new RequestValidationException("totalChunks", $"Expected {expected} chunks.");
        var have = await store.ListChunksAsync(a.TenantId, a.Id, ct);
        var missing = Enumerable.Range(0, expected).Except(have).ToList();
        if (missing.Count > 0) throw new ConflictException($"Missing chunks: {string.Join(", ", missing.Take(10))}.");

        var (key, size, sha) = await store.AssembleAsync(a.TenantId, a.Id, expected, ct);
        if (size != a.SizeBytes || !string.Equals(sha, a.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await store.DeleteChunksAsync(a.TenantId, a.Id, ct);
            throw new ConflictException("The uploaded file does not match its size or checksum. Upload it again.");
        }

        a.IsUploaded = true;
        a.StorageKey = key;
        await db.SaveChangesAsync(ct);
        return new UploadStatusDto(a.Id, ChunkSize, [], true);
    }

    public async Task<(Stream Content, string ContentType, string FileName)> OpenAsync(Guid attachmentId, CancellationToken ct)
    {
        var a = await db.Attachments.FirstOrDefaultAsync(x => x.Id == attachmentId, ct) ?? throw new NotFoundException("File", attachmentId);
        RequireBranch(Permissions.Patients.View, a.BranchId);
        if (!a.IsUploaded || a.StorageKey is null) throw new NotFoundException("File", attachmentId);
        var stream = await store.OpenReadAsync(a.StorageKey, ct) ?? throw new NotFoundException("File", attachmentId);
        return (stream, a.ContentType, a.FileName);
    }

    private async Task<Domain.Clinical.Attachment> LoadForUploadAsync(Guid id, CancellationToken ct)
    {
        var a = await db.Attachments.FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("File", id); // metadata must sync before the bytes
        RequireBranch(Permissions.Attachments.Upload, a.BranchId);
        return a;
    }

    private void RequireBranch(string permission, Guid? branchId)
    {
        var scope = user.GetScope(permission) ?? throw new ForbiddenException($"You do not have the {permission} permission.");
        if (scope != PermissionScope.All && branchId is { } b && !user.BranchIds.Contains(b))
            throw new NotFoundException("File", branchId);
    }
}

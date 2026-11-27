using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Clinic.Shared.Contracts;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Sync;

/// <summary>Who is syncing: set after sign-in, used to stamp local writes and outbox rows.</summary>
public interface IClientContext
{
    Guid TenantId { get; }
    Guid UserId { get; }
    Guid DeviceId { get; }
    string AppVersion { get; }
}

public sealed record ClientContext(Guid TenantId, Guid UserId, Guid DeviceId, string AppVersion) : IClientContext;

public enum SyncFailureKind
{
    /// <summary>No connection, timeout, or a 5xx/429: retry later with back-off.</summary>
    Transient,

    /// <summary>The access token could not be renewed; the user has to sign in.</summary>
    Unauthorized,

    /// <summary>FR-OFF-31: the device was revoked; wipe local data.</summary>
    DeviceRevoked,

    /// <summary>FR-SYN-44: app too old; update, then push the outbox.</summary>
    UpdateRequired,

    /// <summary>Request refused (403/400); retrying the same request will not help.</summary>
    Refused,
}

public sealed class SyncTransportException(SyncFailureKind kind, string message) : Exception(message)
{
    public SyncFailureKind Kind { get; } = kind;
}

/// <summary>The device's view of the sync API, so the engine can be tested and the app can inject auth.</summary>
public interface ISyncTransport
{
    Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct);
    Task<SyncPullResponse> PullAsync(long since, int pageSize, CancellationToken ct);
    Task<NumberRangeDto> LeaseNumbersAsync(string kind, int size, CancellationToken ct);

    /// <summary>FR-SYN-30: report conflicts resolved on the device.</summary>
    Task ReportResolutionsAsync(IReadOnlyList<ConflictResolutionReport> reports, CancellationToken ct) => Task.CompletedTask;

    /// <summary>FR-SYN-23: a compressed NDJSON snapshot (one pull page per line) for a device starting from nothing; null when not offered.</summary>
    Task<Stream?> SnapshotAsync(CancellationToken ct) => Task.FromResult<Stream?>(null);
}

/// <summary>
/// HTTP transport. Push bodies are gzip-compressed (FR-SYN-10). The token provider is called per request
/// and is expected to refresh an expiring access token itself.
/// </summary>
public sealed class HttpSyncTransport(
    HttpClient http,
    IClientContext context,
    Func<CancellationToken, Task<string?>> accessTokenProvider) : ISyncTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, SyncRoutes.Push) { Content = Gzip(request) };
        return await SendAsync<SyncPushResponse>(message, ct);
    }

    public async Task<SyncPullResponse> PullAsync(long since, int pageSize, CancellationToken ct)
    {
        var url = $"{SyncRoutes.Pull}?deviceId={context.DeviceId}&appVersion={Uri.EscapeDataString(context.AppVersion)}&since={since}&pageSize={pageSize}";
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync<SyncPullResponse>(message, ct);
    }

    public async Task<NumberRangeDto> LeaseNumbersAsync(string kind, int size, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{ApiRoutes.Devices}/{context.DeviceId}/number-ranges")
        {
            Content = JsonContent.Create(new LeaseNumberRangeRequest(kind, size), options: Json),
        };
        return await SendAsync<NumberRangeDto>(message, ct);
    }

    public async Task ReportResolutionsAsync(IReadOnlyList<ConflictResolutionReport> reports, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, SyncRoutes.Resolutions) { Content = JsonContent.Create(reports, options: Json) };
        await SendAsync<object>(message, ct);
    }

    public async Task<Stream?> SnapshotAsync(CancellationToken ct)
    {
        var url = $"{SyncRoutes.Snapshot}?deviceId={context.DeviceId}&appVersion={Uri.EscapeDataString(context.AppVersion)}";
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        var bytes = await SendAsync<byte[]>(message, ct, raw: true);
        return new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage message, CancellationToken ct, bool raw = false)
    {
        string? token;
        try
        {
            token = await accessTokenProvider(ct);
        }
        catch (SyncTransportException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new SyncTransportException(SyncFailureKind.Transient, $"Could not renew the session: {ex.Message}");
        }

        if (token is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SyncTransportException(SyncFailureKind.Transient, "The server could not be reached.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (raw) return (T)(object)await response.Content.ReadAsByteArrayAsync(ct);
                if (response.StatusCode == HttpStatusCode.NoContent) return default!;
                return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
            }

            var problem = await ReadProblemAsync(response, ct);
            var kind = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized when problem.Type == ApiRoutes.Problems.DeviceRevoked => SyncFailureKind.DeviceRevoked,
                HttpStatusCode.Unauthorized => SyncFailureKind.Unauthorized,
                HttpStatusCode.UpgradeRequired => SyncFailureKind.UpdateRequired,
                HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.NotFound => SyncFailureKind.Refused,
                _ => SyncFailureKind.Transient,
            };
            throw new SyncTransportException(kind, problem.Detail ?? problem.Title ?? $"Sync failed ({(int)response.StatusCode}).");
        }
    }

    private static HttpContent Gzip<T>(T body)
    {
        var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            JsonSerializer.Serialize(gzip, body, Json);
        buffer.Position = 0;

        var content = new StreamContent(buffer);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    private static async Task<Problem> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<Problem>(Json, ct) ?? new Problem(null, null, null);
        }
        catch (Exception)
        {
            return new Problem(null, null, null);
        }
    }

    private sealed record Problem(string? Type, string? Title, string? Detail);
}

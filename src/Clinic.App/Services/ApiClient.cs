using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Clinic.Shared.Contracts;

namespace Clinic.App.Services;

/// <summary>An RFC 7807 problem from the API, or a network failure (StatusCode null).</summary>
public sealed class ApiException(
    HttpStatusCode? statusCode,
    string message,
    string? problemType = null,
    IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public string? ProblemType { get; } = problemType;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();

    public bool IsNetworkError => StatusCode is null;

    /// <summary>One readable line: field errors if any, otherwise the problem detail.</summary>
    public string UserMessage =>
        Errors.Count > 0 ? string.Join("\n", Errors.SelectMany(e => e.Value)) : Message;
}

/// <summary>
/// Thin typed HTTP client. Adds the bearer token, refreshes it once on expiry/401,
/// and turns ProblemDetails into <see cref="ApiException"/>.
/// Phase 1 admin screens call it directly; clinical screens will go through the local DB + sync engine.
/// </summary>
public sealed class ApiClient(AppSettings settings, SessionStore session, DeviceIdentity device)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private HttpClient? _http;
    private string? _httpBase;

    /// <summary>Raised when the server says this device was revoked (FR-OFF-31).</summary>
    public event EventHandler? DeviceRevoked;

    /// <summary>Raised when the session can no longer be refreshed and the user must sign in again.</summary>
    public event EventHandler? SessionExpired;

    private HttpClient Http
    {
        get
        {
            if (_http is null || _httpBase != settings.ServerUrl)
            {
                _http?.Dispose();
                _httpBase = settings.ServerUrl;
                _http = new HttpClient(SecureHttp.CreateHandler(settings))
                {
                    BaseAddress = new Uri(_httpBase + "/"),
                    Timeout = TimeSpan.FromSeconds(30),
                };
            }
            return _http;
        }
    }

    public Task<T> GetAsync<T>(string path, CancellationToken ct = default) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, path), ct);

    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Post, path) { Content = Body(body) }, ct);

    public Task<T> PutAsync<T>(string path, object body, CancellationToken ct = default) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Put, path) { Content = Body(body) }, ct);

    public Task PostAsync(string path, object? body, CancellationToken ct = default) =>
        SendAsync<object?>(() => new HttpRequestMessage(HttpMethod.Post, path) { Content = Body(body) }, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) =>
        SendAsync<object?>(() => new HttpRequestMessage(HttpMethod.Delete, path), ct);

    /// <summary>A valid access token for the sync transport, refreshed first when it is about to expire.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (session.AccessTokenExpiresAt <= DateTime.UtcNow.AddSeconds(30))
            await RefreshAsync(ct);
        return session.AccessToken;
    }

    /// <summary>Anonymous login; does not use or refresh the stored session.</summary>
    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Auth.Login) { Content = Body(request) };
        using var response = await SendRawAsync(message, ct);
        return await ReadAsync<TokenResponse>(response, ct);
    }

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        if (session.AccessTokenExpiresAt <= DateTime.UtcNow.AddSeconds(30))
            await RefreshAsync(ct);

        using (var first = build())
        {
            Authorize(first);
            using var response = await SendRawAsync(first, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                return await ReadAsync<T>(response, ct);
        }

        // Access token rejected (expired early, permissions changed): refresh once and retry.
        await RefreshAsync(ct, force: true);
        using var retry = build();
        Authorize(retry);
        using var second = await SendRawAsync(retry, ct);
        return await ReadAsync<T>(second, ct);
    }

    private async Task RefreshAsync(CancellationToken ct, bool force = false)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!force && session.AccessTokenExpiresAt > DateTime.UtcNow.AddSeconds(30)) return;
            if (session.RefreshToken is null) throw Expired();

            using var message = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Auth.Refresh)
            {
                Content = Body(new RefreshRequest(session.RefreshToken, device.DeviceId)),
            };
            using var response = await SendRawAsync(message, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var problem = await ReadProblemAsync(response, ct);
                if (problem.ProblemType == ApiRoutes.Problems.DeviceRevoked)
                    DeviceRevoked?.Invoke(this, EventArgs.Empty);
                else
                    SessionExpired?.Invoke(this, EventArgs.Empty);
                throw problem;
            }

            await session.SaveAsync(await ReadAsync<TokenResponse>(response, ct));
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void Authorize(HttpRequestMessage message)
    {
        if (session.AccessToken is { } token)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            return await Http.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ApiException(null, $"Cannot reach the server at {settings.ServerUrl}. Check the connection and server address.");
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw await ReadProblemAsync(response, ct);
        if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default!;
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static async Task<ApiException> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Json, ct);
            if (problem is not null)
            {
                var message = problem.Detail ?? problem.Title ?? response.ReasonPhrase ?? "Request failed.";
                return new ApiException(response.StatusCode, message, problem.Type, problem.Errors);
            }
        }
        catch (JsonException) { }

        var fallback = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "You do not have permission to do this.",
            HttpStatusCode.TooManyRequests => "Too many attempts. Wait a minute and try again.",
            _ => $"Request failed ({(int)response.StatusCode}).",
        };
        return new ApiException(response.StatusCode, fallback);
    }

    private ApiException Expired()
    {
        SessionExpired?.Invoke(this, EventArgs.Empty);
        return new ApiException(HttpStatusCode.Unauthorized, "Your session has ended. Please sign in again.");
    }

    private static JsonContent? Body(object? body) => body is null ? null : JsonContent.Create(body, body.GetType(), options: Json);

    private sealed record ProblemBody(string? Type, string? Title, string? Detail, Dictionary<string, string[]>? Errors);
}

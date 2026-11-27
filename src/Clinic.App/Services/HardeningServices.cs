using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Clinic.Shared.Contracts;

namespace Clinic.App.Services;

/// <summary>
/// FR-SEC-01: TLS for every call, optionally pinned. With pins configured (SHA-256 of a certificate's
/// SubjectPublicKeyInfo, base64 — the leaf or an intermediate), a chain that does not contain one of them is refused
/// even when the OS trusts it, so a mis-issued or proxy certificate cannot read patient data.
/// </summary>
public static class SecureHttp
{
    public static HttpMessageHandler CreateHandler(AppSettings settings)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var pins = settings.CertificatePins;
        if (pins.Count > 0)
            handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) => IsTrusted(cert, chain, errors, pins);
        return handler;
    }

    public static bool IsTrusted(X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors, IReadOnlyCollection<string> pins)
    {
        if (errors != SslPolicyErrors.None || cert is null) return false;
        if (pins.Contains(SpkiHash(cert))) return true;
        return chain?.ChainElements.Any(e => pins.Contains(SpkiHash(e.Certificate))) == true;
    }

    public static string SpkiHash(X509Certificate2 cert) =>
        Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
}

/// <summary>
/// FR-SEC-05: after a period without use — or that long in the background — the PIN screen covers the app.
/// It is shown modally, so the page underneath (and anything half typed) is still there after unlocking.
/// Sync keeps running while locked.
/// </summary>
public sealed class AppLock(SessionStore session, OfflinePinService pins, AppSettings settings, IServiceProvider services)
{
    private DateTime _lastActivity = DateTime.UtcNow;
    private DateTime? _backgroundSince;
    private IDispatcherTimer? _timer;

    public bool IsLocked { get; private set; }

    private TimeSpan Timeout => TimeSpan.FromMinutes(Math.Max(1, settings.AutoLockMinutes));

    private bool CanLock => session.Me is { } me && pins.HasPin(me.UserId) && !IsLocked;

    public void Start(IDispatcher dispatcher)
    {
        if (_timer is not null) return;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - _lastActivity >= Timeout) _ = LockAsync();
        };
        _timer.Start();
    }

    /// <summary>Navigation, taps and typing count as activity.</summary>
    public void Touch() => _lastActivity = DateTime.UtcNow;

    public void OnBackground() => _backgroundSince = DateTime.UtcNow;

    public void OnForeground()
    {
        if (_backgroundSince is { } since && DateTime.UtcNow - since >= Timeout) _ = LockAsync();
        _backgroundSince = null;
        Touch();
    }

    public async Task LockAsync()
    {
        if (!CanLock || Shell.Current is not { } shell) return;
        // Only while signed in (not on the login or startup unlock screens).
        var route = shell.CurrentState.Location.OriginalString;
        if (route.Contains(Routes.Login) || route.Contains(Routes.Unlock)) return;

        IsLocked = true;
        var page = services.GetRequiredService<Views.UnlockPage>();
        page.IsLockScreen = true;
        await MainThread.InvokeOnMainThreadAsync(() => shell.Navigation.PushModalAsync(page, animated: false));
    }

    public async Task UnlockedAsync()
    {
        IsLocked = false;
        Touch();
        if (Shell.Current?.Navigation.ModalStack.Count > 0) await Shell.Current.Navigation.PopModalAsync(animated: false);
    }
}

/// <summary>
/// Crash and sync-error reports (observability, §11). Recorded to a small file first — a crash may leave no time
/// to send — and posted to the server after the next successful sign-in or sync. Messages only: no patient data.
/// </summary>
public sealed class ClientErrorReporter(ApiClient api, DeviceIdentity identity)
{
    private const int MaxQueued = 50;
    private static readonly object Gate = new();
    private static string QueuePath => Path.Combine(FileSystem.AppDataDirectory, "client-errors.jsonl");

    private sealed record Queued(string Kind, string Message, string? Detail, DateTimeOffset OccurredAt);

    public static void Record(string kind, Exception ex) => Record(kind, ex.Message, ex.ToString());

    public static void Record(string kind, string message, string? detail = null)
    {
        try
        {
            lock (Gate)
            {
                var lines = File.Exists(QueuePath) ? File.ReadAllLines(QueuePath).ToList() : [];
                lines.Add(JsonSerializer.Serialize(new Queued(kind, Trim(message, 500)!, Trim(detail, 8000), DateTimeOffset.UtcNow)));
                File.WriteAllLines(QueuePath, lines.TakeLast(MaxQueued));
            }
        }
        catch (IOException) { /* never let reporting crash the app */ }
    }

    /// <summary>Sends what is queued; anything not sent stays for the next attempt.</summary>
    public async Task FlushAsync()
    {
        List<Queued> queued;
        lock (Gate)
        {
            if (!File.Exists(QueuePath)) return;
            queued = File.ReadAllLines(QueuePath).Select(l => JsonSerializer.Deserialize<Queued>(l)).OfType<Queued>().ToList();
        }
        var sent = 0;
        try
        {
            foreach (var q in queued)
            {
                await api.PostAsync(HardeningRoutes.ClientErrors, new ClientErrorReport(q.Kind, q.Message, q.Detail, AppInfo.Current.VersionString,
                    $"{DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString}", q.OccurredAt, identity.DeviceId));
                sent++;
            }
        }
        catch (Exception) { /* offline or signed out: try again later */ }
        finally
        {
            lock (Gate)
            {
                if (File.Exists(QueuePath))
                {
                    var remaining = File.ReadAllLines(QueuePath).Skip(sent).ToList();
                    if (remaining.Count == 0) File.Delete(QueuePath); else File.WriteAllLines(QueuePath, remaining);
                }
            }
        }
    }

    private static string? Trim(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

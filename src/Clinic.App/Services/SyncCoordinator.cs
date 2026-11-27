using System.Net;
using System.Security.Cryptography;
using Clinic.Client.Core.Local;
using Clinic.Client.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Clinic.App.Services;

/// <summary>
/// Owns the local database and sync engine for the signed-in session and decides when to sync
/// (FR-SYN-01): on start, when connectivity returns, every 5 minutes, 10 s after a local save, and on demand.
/// Screens read and write only <see cref="Store"/>; they never call the API for clinical data.
/// </summary>
public sealed partial class SyncCoordinator(AppSettings settings, SessionStore session, DeviceIdentity device, ApiClient api)
    : ObservableObject
{
    private static readonly TimeSpan Periodic = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AfterSaveDelay = TimeSpan.FromSeconds(10);

    private LocalDatabase? _db;
    private LocalStore? _store;
    private SyncEngine? _engine;
    private SyncCenter? _center;
    private Clinic.Client.Core.Forms.DynamicDataService? _forms;
    private Clinic.Client.Core.Clinical.PatientService? _patients;
    private Clinic.Client.Core.Clinical.SchedulingService? _scheduling;
    private Clinic.Client.Core.Clinical.AttachmentService? _attachments;
    private Clinic.Client.Core.Clinical.HttpFileTransport? _files;
    private Clinic.Client.Core.Clinical.EncounterService? _encounters;
    private Clinic.Client.Core.Clinical.PrescriptionService? _prescriptions;
    private Clinic.Client.Core.Clinical.CaseService? _cases;
    private Clinic.Client.Core.Clinical.ReferralService? _referrals;
    private Clinic.Client.Core.Clinical.FollowUpService? _followUps;
    private Clinic.Client.Core.Clinical.ProcedureService? _procedures;
    private Clinic.Client.Core.Clinical.DayCareService? _dayCare;
    private Clinic.Client.Core.Clinical.LabService? _labs;
    private Clinic.Client.Core.Clinical.BillingService? _billing;
    private Clinic.Client.Core.Clinical.MessagingService? _messaging;
    private Clinic.Client.Core.Clinical.ExpenseService? _expenses;
    private Clinic.Client.Core.Clinical.FeedbackService? _feedback;
    private Clinic.Client.Core.Clinical.InventoryService? _inventory;
    private Clinic.Client.Core.Clinical.ReportService? _reports;
    private SyncNudgeClient? _nudges;
    private HttpClient? _http;
    private CancellationTokenSource? _cts;
    private Timer? _debounce;

    public LocalStore Store => _store ?? throw new InvalidOperationException("Sign in first.");
    public SyncCenter Center => _center ?? throw new InvalidOperationException("Sign in first.");
    public SyncEngine Engine => _engine ?? throw new InvalidOperationException("Sign in first.");
    public LocalDatabase Database => _db ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Forms.DynamicDataService Forms => _forms ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.PatientService Patients => _patients ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.SchedulingService Scheduling => _scheduling ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.AttachmentService Attachments => _attachments ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.IFileTransport Files => _files ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.EncounterService Encounters => _encounters ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.PrescriptionService Prescriptions => _prescriptions ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.CaseService Cases => _cases ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.ReferralService Referrals => _referrals ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.FollowUpService FollowUps => _followUps ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.ProcedureService Procedures => _procedures ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.DayCareService DayCare => _dayCare ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.LabService Labs => _labs ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.BillingService Billing => _billing ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.MessagingService Messaging => _messaging ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.ExpenseService Expenses => _expenses ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.FeedbackService Feedback => _feedback ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.InventoryService Inventory => _inventory ?? throw new InvalidOperationException("Sign in first.");
    public Clinic.Client.Core.Clinical.ReportService Reports => _reports ?? throw new InvalidOperationException("Sign in first.");

    /// <summary>Branch the user is working at (first assigned branch unless changed on the Notes/Queue screens).</summary>
    public Guid CurrentBranchId => Guid.TryParse(Preferences.Default.Get("current_branch", ""), out var b) &&
                                   session.Me?.Branches.Any(x => x.Id == b) == true
        ? b : session.Me?.Branches.FirstOrDefault()?.Id ?? Guid.Empty;

    public void SetCurrentBranch(Guid id) => Preferences.Default.Set("current_branch", id.ToString());
    public bool IsStarted => _db is not null;

    [ObservableProperty] public partial string StatusText { get; set; } = "Not signed in";
    [ObservableProperty] public partial int Outstanding { get; set; }
    [ObservableProperty] public partial int NeedsAttention { get; set; }
    [ObservableProperty] public partial bool IsSyncing { get; set; }
    [ObservableProperty] public partial SyncState? State { get; set; }

    /// <summary>Local data changed (by this device or by a pull); screens reload.</summary>
    public event EventHandler<IReadOnlyCollection<string>>? DataChanged;

    /// <summary>The server revoked this device during sync.</summary>
    public event EventHandler? DeviceRevoked;

    public async Task StartAsync()
    {
        if (IsStarted) return;
        var me = session.Me ?? throw new InvalidOperationException("No signed-in user.");
        var tenantId = me.TenantId ?? throw new InvalidOperationException("The app needs a clinic account.");

        _db = await LocalDatabase.OpenAsync(DatabasePath(tenantId), await GetOrCreateKeyAsync(tenantId));

        var context = new ClientContext(tenantId, me.UserId, device.DeviceId, DeviceIdentity.AppVersion);
        _http = new HttpClient(SecureHttp.CreateHandler(settings))
        {
            BaseAddress = new Uri(settings.ServerUrl + "/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        var transport = new HttpSyncTransport(_http, context, api.GetAccessTokenAsync);

        _store = new LocalStore(_db, context);
        _engine = new SyncEngine(_db, transport, context);
        _center = new SyncCenter(_db, _engine);
        _forms = new Clinic.Client.Core.Forms.DynamicDataService(_db);
        _patients = new Clinic.Client.Core.Clinical.PatientService(_db, _store, device.DeviceCode ?? "D00");
        _scheduling = new Clinic.Client.Core.Clinical.SchedulingService(_store);
        _attachments = new Clinic.Client.Core.Clinical.AttachmentService(_db, _store,
            Path.Combine(FileSystem.AppDataDirectory, $"files_{tenantId:N}"), await GetOrCreateFileKeyAsync(tenantId));
        _files = new Clinic.Client.Core.Clinical.HttpFileTransport(_http, api.GetAccessTokenAsync);
        _encounters = new Clinic.Client.Core.Clinical.EncounterService(_store, _forms);
        _prescriptions = new Clinic.Client.Core.Clinical.PrescriptionService(_store, _forms, _encounters);
        _cases = new Clinic.Client.Core.Clinical.CaseService(_store, _forms);
        _referrals = new Clinic.Client.Core.Clinical.ReferralService(_store, _forms);
        _followUps = new Clinic.Client.Core.Clinical.FollowUpService(_store);
        _procedures = new Clinic.Client.Core.Clinical.ProcedureService(_store, _forms, _attachments);
        _dayCare = new Clinic.Client.Core.Clinical.DayCareService(_store);
        _labs = new Clinic.Client.Core.Clinical.LabService(_store, _forms);
        _billing = new Clinic.Client.Core.Clinical.BillingService(_db, _store, device.DeviceCode ?? "D00");
        _messaging = new Clinic.Client.Core.Clinical.MessagingService(_store);
        _expenses = new Clinic.Client.Core.Clinical.ExpenseService(_store);
        _feedback = new Clinic.Client.Core.Clinical.FeedbackService(_store, _forms);
        _inventory = new Clinic.Client.Core.Clinical.InventoryService(_store);
        _reports = new Clinic.Client.Core.Clinical.ReportService(_store, _forms);

        // FR-SYN-03: a SignalR nudge from the server triggers a pull within seconds.
        // With certificate pins, the hub uses only transports that go through the pinned handler (WebSockets do not).
        var pinned = settings.CertificatePins.Count > 0;
        _nudges = new SyncNudgeClient(new Uri(settings.ServerUrl + "/"), () => api.GetAccessTokenAsync(CancellationToken.None), () => RequestSync(),
            () => SecureHttp.CreateHandler(settings),
            pinned ? Microsoft.AspNetCore.Http.Connections.HttpTransportType.ServerSentEvents | Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling : null);
        _ = _nudges.TryStartAsync();

        _engine.StateChanged += OnStateChanged;
        _engine.DataChanged += OnEngineDataChanged;
        _engine.DeviceRevoked += OnDeviceRevoked;
        _store.Changed += OnLocalChange;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;

        _cts = new CancellationTokenSource();
        _ = PeriodicLoopAsync(_cts.Token);

        await _engine.RefreshStateAsync();
        RequestSync();
    }

    public async Task StopAsync()
    {
        if (!IsStarted) return;

        _cts?.Cancel();
        _debounce?.Dispose();
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        if (_engine is not null)
        {
            _engine.StateChanged -= OnStateChanged;
            _engine.DataChanged -= OnEngineDataChanged;
            _engine.DeviceRevoked -= OnDeviceRevoked;
        }
        if (_store is not null) _store.Changed -= OnLocalChange;

        // Let a running sync finish its current transaction before closing the database.
        for (var i = 0; i < 50 && _engine?.IsRunning == true; i++) await Task.Delay(100);

        if (_nudges is not null) await _nudges.DisposeAsync();
        _nudges = null;
        await _db!.DisposeAsync();
        _http?.Dispose();
        _db = null;
        _store = null;
        _engine = null;
        _center = null;
        _forms = null;
        _patients = null;
        _scheduling = null;
        _attachments = null;
        _files = null;
        _encounters = null;
        _prescriptions = null;
        StatusText = "Not signed in";
        Outstanding = NeedsAttention = 0;
    }

    /// <summary>FR-OFF-31: delete the local database and its key.</summary>
    public async Task WipeAsync()
    {
        var tenantId = session.Me?.TenantId;
        var db = _db;
        await StopAsync();
        if (tenantId is null) return;

        var path = db?.Path ?? DatabasePath(tenantId.Value);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); } catch (IOException) { }
        }
        SecureStorage.Default.Remove(KeyName(tenantId.Value));
        SecureStorage.Default.Remove($"file_key_{tenantId.Value:N}");
        var filesDir = Path.Combine(FileSystem.AppDataDirectory, $"files_{tenantId.Value:N}");
        try { if (Directory.Exists(filesDir)) Directory.Delete(filesDir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Starts a sync in the background. Automatic syncs are skipped while the device is offline.</summary>
    public void RequestSync(bool manual = false)
    {
        if (_engine is not { } engine) return;
        if (!manual && Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            _ = engine.RefreshStateAsync();
            return;
        }
        _ = Task.Run(async () =>
        {
            var state = await engine.SyncAsync(manual);
            // FR-OFF-23: after metadata synced, send pending attachment bytes in the background.
            if (state.Phase == SyncPhase.Idle && _attachments is { } files && _files is { } transport)
            {
                try
                {
                    if (await files.UploadPendingAsync(transport) > 0) await engine.SyncAsync();
                }
                catch (SyncTransportException) { /* retried on the next sync */ }
            }
        });
    }

    private void OnLocalChange(object? sender, string entityType)
    {
        _ = _engine?.RefreshStateAsync();
        MainThread.BeginInvokeOnMainThread(() => DataChanged?.Invoke(this, [entityType]));

        // FR-SYN-01: debounce — one sync 10 s after the last save.
        _debounce?.Dispose();
        _debounce = new Timer(_ => RequestSync(), null, AfterSaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnEngineDataChanged(object? sender, IReadOnlyCollection<string> types) =>
        MainThread.BeginInvokeOnMainThread(() => DataChanged?.Invoke(this, types));

    private void OnDeviceRevoked(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() => DeviceRevoked?.Invoke(this, EventArgs.Empty));

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess == NetworkAccess.Internet) RequestSync();
        else _ = _engine?.RefreshStateAsync();
    }

    private async Task PeriodicLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Periodic);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) RequestSync();
        }
        catch (OperationCanceledException) { }
    }

    private void OnStateChanged(object? sender, SyncState state) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            State = state;
            Outstanding = state.Outstanding;
            NeedsAttention = state.NeedsAttention;
            IsSyncing = state.Phase == SyncPhase.Syncing;
            StatusText = Describe(state);
        });

    /// <summary>FR-OFF-20: one line that always says where the data stands.</summary>
    public static string Describe(SyncState s)
    {
        var last = s.LastSuccessAt is { } t ? $"last synced {t.ToLocalTime():t}" : "never synced";
        var waiting = s.Pending + s.Failed;
        var queue = waiting > 0 ? $" · {waiting} change(s) waiting" : "";
        var attention = s.NeedsAttention > 0 ? $" · {s.NeedsAttention} need attention" : "";

        return s.Phase switch
        {
            SyncPhase.Syncing => "Syncing…",
            SyncPhase.Offline => $"Offline · {last}{queue}{attention}",
            SyncPhase.SignInRequired => "Sign in again to sync",
            SyncPhase.UpdateRequired => "Update the app to sync",
            SyncPhase.Error => $"Sync error: {s.Message}",
            _ => $"Online · {last}{queue}{attention}",
        };
    }

    private static string DatabasePath(Guid tenantId) =>
        Path.Combine(FileSystem.AppDataDirectory, $"clinic_{tenantId:N}.db3");

    private static string KeyName(Guid tenantId) => $"db_key_{tenantId:N}";

    /// <summary>FR-SEC-02: a separate AES-256 key for attachment files.</summary>
    private static async Task<byte[]> GetOrCreateFileKeyAsync(Guid tenantId)
    {
        var name = $"file_key_{tenantId:N}";
        var key = await SecureStorage.Default.GetAsync(name);
        if (!string.IsNullOrEmpty(key)) return Convert.FromBase64String(key);
        var bytes = RandomNumberGenerator.GetBytes(32);
        await SecureStorage.Default.SetAsync(name, Convert.ToBase64String(bytes));
        return bytes;
    }

    /// <summary>FR-OFF-01: a random SQLCipher key per tenant, kept in Keychain / Keystore / DPAPI.</summary>
    private static async Task<string> GetOrCreateKeyAsync(Guid tenantId)
    {
        var name = KeyName(tenantId);
        var key = await SecureStorage.Default.GetAsync(name);
        if (!string.IsNullOrEmpty(key)) return key;

        key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await SecureStorage.Default.SetAsync(name, key);
        return key;
    }
}

using Clinic.App.Services;
using Clinic.App.Views;

namespace Clinic.App;

public partial class AppShell : Shell
{
    private readonly SessionStore _session;
    private readonly AuthService _auth;
    private readonly SyncCoordinator _sync;
    private readonly OfflinePinService _pins;
    private readonly ClientErrorReporter _errors;
    private Clinic.Client.Core.Sync.SyncPhase? _lastPhase;
    private bool _started;
    private bool _wiping;

    public static AppShell Instance => (AppShell)Current;

    /// <summary>FR-SEC-05 auto-lock.</summary>
    public AppLock Lock { get; }

    public AppShell(SessionStore session, AuthService auth, ApiClient api, SyncCoordinator sync, OfflinePinService pins, AppLock appLock,
        ClientErrorReporter errors)
    {
        InitializeComponent();
        Lock = appLock;
        _errors = errors;
        Navigated += (_, _) => appLock.Touch();
        _session = session;
        _auth = auth;
        _sync = sync;
        _pins = pins;

        Routing.RegisterRoute(Routes.BranchEdit, typeof(BranchEditPage));
        Routing.RegisterRoute(Routes.UserEdit, typeof(UserEditPage));
        Routing.RegisterRoute(Routes.NoteEdit, typeof(NoteEditPage));
        Routing.RegisterRoute(Routes.FormDesigner, typeof(FormDesignerPage));
        Routing.RegisterRoute(Routes.FieldEditor, typeof(FieldEditorPage));
        Routing.RegisterRoute(Routes.FormPreview, typeof(FormPreviewPage));
        Routing.RegisterRoute(Routes.Lookups, typeof(LookupsPage));
        Routing.RegisterRoute(Routes.LookupEdit, typeof(LookupEditPage));
        Routing.RegisterRoute(Routes.Patient, typeof(PatientPage));
        Routing.RegisterRoute(Routes.PatientEdit, typeof(PatientEditPage));
        Routing.RegisterRoute(Routes.Booking, typeof(BookingPage));
        Routing.RegisterRoute(Routes.DoctorEdit, typeof(DoctorEditPage));
        Routing.RegisterRoute(Routes.SchedulingSetup, typeof(SchedulingSetupPage));
        Routing.RegisterRoute(Routes.Encounter, typeof(EncounterPage));
        Routing.RegisterRoute(Routes.Case, typeof(CasePage));
        Routing.RegisterRoute(Routes.Referral, typeof(ReferralPage));
        Routing.RegisterRoute(Routes.Procedure, typeof(ProcedurePage));
        Routing.RegisterRoute(Routes.Admission, typeof(AdmissionPage));
        Routing.RegisterRoute(Routes.LabResult, typeof(LabResultPage));
        Routing.RegisterRoute(Routes.PatientBilling, typeof(PatientBillingPage));
        Routing.RegisterRoute(Routes.Invoice, typeof(InvoicePage));
        Routing.RegisterRoute(Routes.Dispense, typeof(DispensePage));
        Routing.RegisterRoute(Routes.Conflict, typeof(ConflictPage));

        api.SessionExpired += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            // Local data and unsynced changes stay on the device; they sync after the next sign-in.
            await _sync.StopAsync();
            await _session.ClearAsync();
            await ShowLoginAsync("Your session has ended. Please sign in again.");
        });

        api.DeviceRevoked += (_, _) => MainThread.BeginInvokeOnMainThread(() => _ = WipeDeviceAsync());
        sync.DeviceRevoked += (_, _) => _ = WipeDeviceAsync();

        sync.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SyncCoordinator.StatusText) or nameof(SyncCoordinator.Outstanding))
            {
                FlyoutSync.Text = _sync.IsStarted ? _sync.StatusText : "";
                SyncItem.Title = _sync.NeedsAttention > 0 ? $"Sync Center ({_sync.NeedsAttention} need attention)"
                    : _sync.Outstanding > 0 ? $"Sync Center ({_sync.Outstanding})" : "Sync Center";
            }
            if (e.PropertyName == nameof(SyncCoordinator.StatusText) && _sync.State is { } state && state.Phase != _lastPhase)
            {
                _lastPhase = state.Phase;
                // Sync errors go to the server's error log; a successful sync sends what is queued.
                if (state.Phase == Clinic.Client.Core.Sync.SyncPhase.Error) ClientErrorReporter.Record("sync", state.Message ?? "Sync error");
                else if (state.Phase == Clinic.Client.Core.Sync.SyncPhase.Idle) _ = _errors.FlushAsync();
            }
        };

        ApplyPermissions();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;

        // FR-AUTH-05: a cached session inside the grace period unlocks with the offline PIN, no network needed.
        if (await _session.RestoreAsync() && WithinOfflineGrace() && _session.Me is { } me && _pins.HasPin(me.UserId))
            await GoToAsync($"//{Routes.Unlock}");
        else
            await ShowLoginAsync();
    }

    public async Task ShowSignedInAsync()
    {
        try
        {
            await _sync.StartAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Local data", $"The device database could not be opened: {ex.Message}", "OK");
        }
        ApplyPermissions();
        await GoToAsync($"//{Routes.Home}");
        Lock.Start(Dispatcher);
        Lock.Touch();
        _ = _errors.FlushAsync();
    }

    public async Task ShowLoginAsync(string? message = null)
    {
        await _sync.StopAsync();
        ApplyPermissions();
        await GoToAsync($"//{Routes.Login}");
        if (message is not null) await DisplayAlertAsync("Signed out", message, "OK");
    }

    /// <summary>
    /// FR-OFF-32: unsynced changes block sign-out, unless the user holds Sync.DiscardPending and confirms.
    /// </summary>
    public async Task SignOutAsync()
    {
        var pending = _sync.IsStarted ? await _sync.Center.CountAsync() : 0;
        if (pending > 0)
        {
            if (!_session.Has(Permissions.Sync.DiscardPending))
            {
                var syncNow = await DisplayAlertAsync("Unsynced changes",
                    $"{pending} change(s) on this device have not reached the server yet. Connect and sync before signing out.",
                    "Sync now", "OK");
                if (syncNow) _sync.RequestSync(manual: true);
                return;
            }

            if (!await DisplayAlertAsync("Unsynced changes",
                    $"{pending} change(s) have not reached the server. Signing out will discard them permanently.",
                    "Discard and sign out", "Cancel"))
                return;
            await _sync.Center.DiscardAllAsync();
        }
        else if (!await DisplayAlertAsync("Sign out", "Sign out of this device?", "Sign out", "Cancel"))
        {
            return;
        }

        await _sync.StopAsync();
        await _auth.LogoutAsync();
        await ShowLoginAsync();
    }

    /// <summary>FR-OFF-31: a revoked device deletes its local database, key, PIN and session.</summary>
    private async Task WipeDeviceAsync()
    {
        if (_wiping) return;
        _wiping = true;
        try
        {
            if (_session.Me is { } me) _pins.Clear(me.UserId);
            await _sync.WipeAsync();
            await _session.ClearAsync();
            await ShowLoginAsync("This device has been revoked by your administrator. Local data has been removed.");
        }
        finally
        {
            _wiping = false;
        }
    }

    /// <summary>FR-AUTH-05: after the tenant's grace period without an online login, require one.</summary>
    private bool WithinOfflineGrace()
    {
        var grace = _session.Me?.OfflineGraceDays is > 0 and var days ? days : 7;
        return _session.LastOnlineLogin is { } last && DateTime.UtcNow - last < TimeSpan.FromDays(grace);
    }

    private void ApplyPermissions()
    {
        var me = _session.Me;
        FlyoutUser.Text = me?.FullName ?? "";
        FlyoutInitials.Text = Initials(me?.FullName);
        FlyoutTenant.Text = me is null ? "" : $"{me.TenantName} · {string.Join(", ", me.Roles)}";
        FlyoutSync.Text = _sync.IsStarted ? _sync.StatusText : "";

        HomeItem.FlyoutItemIsVisible = me is not null;
        SyncItem.FlyoutItemIsVisible = me is not null;
        NotesItem.FlyoutItemIsVisible = _session.Has(Permissions.Notes.View);
        BranchesItem.FlyoutItemIsVisible = _session.Has(Permissions.Branches.View);
        UsersItem.FlyoutItemIsVisible = _session.Has(Permissions.Users.View);
        DevicesItem.FlyoutItemIsVisible = _session.Has(Permissions.Devices.View);
        DuplicatesItem.FlyoutItemIsVisible = _session.Has(Permissions.Patients.Merge);
        AuditItem.FlyoutItemIsVisible = _session.Has(Permissions.Audit.View);
        FormsItem.FlyoutItemIsVisible = _session.Has(Permissions.DynamicFields.Manage);
        PatientsItem.FlyoutItemIsVisible = _session.Has(Permissions.Patients.View);
        AppointmentsItem.FlyoutItemIsVisible = _session.Has(Permissions.Appointments.View);
        QueueItem.FlyoutItemIsVisible = _session.Has(Permissions.Queue.Manage);
        FollowUpsItem.FlyoutItemIsVisible = _session.Has(Permissions.Encounters.View) || _session.Has(Permissions.Cases.View);
        DayCareItem.FlyoutItemIsVisible = _session.Has(Permissions.Admissions.View);
        CareSetupItem.FlyoutItemIsVisible = _session.Has(Permissions.CareSetup.Manage);
        LabItem.FlyoutItemIsVisible = _session.Has(Permissions.Labs.EnterResults) || _session.Has(Permissions.Labs.Verify);
        CashItem.FlyoutItemIsVisible = _session.Has(Permissions.Billing.CashClose);
        ApprovalsItem.FlyoutItemIsVisible = _session.Has(Permissions.Billing.Approve);
        ExpensesItem.FlyoutItemIsVisible = _session.Has(Permissions.Expenses.Manage);
        ComplaintsItem.FlyoutItemIsVisible = _session.Has(Permissions.Feedback.Record);
        ReportsItem.FlyoutItemIsVisible = _session.Has(Permissions.Reports.View);
        StockItem.FlyoutItemIsVisible = _session.Has(Permissions.Inventory.View);
        DoctorsItem.FlyoutItemIsVisible = _session.Has(Permissions.Doctors.Manage) || _session.Has(Permissions.Scheduling.Manage);
    }

    private static string Initials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}",
        };
    }

    private async void OnSignOutClicked(object? sender, EventArgs e) => await SignOutAsync();
}

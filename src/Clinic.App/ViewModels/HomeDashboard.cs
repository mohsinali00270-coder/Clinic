using System.Collections.ObjectModel;
using Clinic.App.Services;
using Clinic.Client.Core.Local;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

/// <summary>A dashboard statistic tile.</summary>
public sealed record StatTile(string Icon, string Title, string Value, string Caption);

/// <summary>One of today's appointments on the dashboard agenda.</summary>
public sealed record AgendaRow(string Time, string Patient, string Detail, string Status, Color ChipBackground, Color ChipText);

/// <summary>A queue stage with its share of the queue (0–1).</summary>
public sealed record QueueStage(string Name, int Count, double Share, Color Color);

/// <summary>One day of the revenue chart; Height in device units.</summary>
public sealed record RevenueBar(string Day, double Height, string Amount, bool IsToday);

/// <summary>A quick-action tile.</summary>
public sealed record QuickAction(string Icon, string Title, string Caption, string Route);

/// <summary>
/// Home dashboard (FR-REP-01 at a glance): today's appointments and queue, new patients, revenue and dues.
/// Everything is computed from this device's data, so it works offline and says "as of last sync".
/// </summary>
public sealed partial class HomeViewModel
{
    private const double ChartHeight = 110;

    public ObservableCollection<StatTile> Stats { get; } = [];
    public ObservableCollection<AgendaRow> Agenda { get; } = [];
    public ObservableCollection<QueueStage> QueueStages { get; } = [];
    public ObservableCollection<RevenueBar> RevenueBars { get; } = [];
    public ObservableCollection<QuickAction> QuickActions { get; } = [];

    [ObservableProperty] public partial string TodayText { get; set; } = "";
    [ObservableProperty] public partial string BranchName { get; set; } = "";
    [ObservableProperty] public partial string Initials { get; set; } = "";
    [ObservableProperty] public partial bool HasAgenda { get; set; }
    [ObservableProperty] public partial bool ShowQueue { get; set; }
    [ObservableProperty] public partial bool ShowMoney { get; set; }
    [ObservableProperty] public partial string RevenueWeekText { get; set; } = "";
    [ObservableProperty] public partial string QueueTotalText { get; set; } = "";
    [ObservableProperty] public partial bool DashboardReady { get; set; }

    // Responsive layout (set by HomePage from its width): tiles per row and one or two panel columns.
    [ObservableProperty] public partial double TileWidth { get; set; } = 160;
    [ObservableProperty] public partial double ActionWidth { get; set; } = 220;
    [ObservableProperty] public partial double PanelWidth { get; set; } = 340;
    [ObservableProperty] public partial double BarSlotWidth { get; set; } = 40;

    public void ApplyWidth(double width)
    {
        if (width <= 0) return;
        var content = Math.Min(width, 1240) - 32;              // page padding
        var tiles = content >= 1100 ? 6 : content >= 760 ? 3 : 2;
        TileWidth = Math.Floor(content / tiles) - 12;            // 6 px margin each side
        var actions = content >= 1100 ? 4 : content >= 700 ? 3 : content >= 440 ? 2 : 1;
        ActionWidth = Math.Floor(content / actions) - 12;
        PanelWidth = content >= 900 ? Math.Floor(content / 2) - 16 : content;
        BarSlotWidth = Math.Floor((PanelWidth - 40) / 7);       // card padding
    }

    private bool _subscribed;

    private static string GreetingFor(DateTime now) => now.Hour switch { < 12 => "Good morning", < 17 => "Good afternoon", _ => "Good evening" };

    private void BuildQuickActions()
    {
        QuickActions.Clear();
        void Add(bool allowed, string icon, string title, string caption, string route)
        {
            if (allowed) QuickActions.Add(new QuickAction(icon, title, caption, route));
        }
        Add(_session.Has(Permissions.Patients.View), "ic_patients.png", "Patients", "Search and register", Routes.Patients);
        Add(_session.Has(Permissions.Appointments.View), "ic_calendar.png", "Appointments", "Book and manage", Routes.Appointments);
        Add(_session.Has(Permissions.Queue.Manage), "ic_queue.png", "Queue", "Live waiting room", Routes.Queue);
        Add(_session.Has(Permissions.Labs.EnterResults) || _session.Has(Permissions.Labs.Verify), "ic_lab.png", "Lab", "Samples and results", Routes.LabWork);
        Add(_session.Has(Permissions.Inventory.View), "ic_pill.png", "Pharmacy", "Stock and dispensing", Routes.Stock);
        Add(_session.Has(Permissions.Billing.CashClose), "ic_cash.png", "Cash closing", "End of shift", Routes.CashClosing);
        Add(_session.Has(Permissions.Reports.View), "ic_chart.png", "Reports", "Figures and exports", Routes.Reports);
        Add(_session.Has(Permissions.Notes.View), "ic_note.png", "Handover", "Shift notes", Routes.Notes);
        Add(_session.Has(Permissions.Doctors.Manage) || _session.Has(Permissions.Scheduling.Manage), "ic_doctor.png", "Doctors", "Schedules and leave", Routes.Doctors);
        Add(_session.Has(Permissions.Users.View), "ic_users.png", "Users", "Staff and roles", Routes.Users);
        Add(_session.Has(Permissions.Branches.View), "ic_branch.png", "Branches", "Locations", Routes.Branches);
        Add(_session.Has(Permissions.DynamicFields.Manage), "ic_form.png", "Forms", "Custom fields", Routes.Forms);
        Add(_session.Has(Permissions.Devices.View), "ic_device.png", "Devices", "Registered devices", Routes.Devices);
        Add(true, "ic_sync.png", "Sync Center", "Pending changes", Routes.SyncCenter);
    }

    public string ThemeText => (IPlatformApplication.Current?.Services.GetService<AppSettings>()?.Theme ?? "system") switch
    {
        "light" => "Appearance: Light",
        "dark" => "Appearance: Dark",
        _ => "Appearance: System",
    };

    /// <summary>Cycles System → Light → Dark. Code-built colours (chips, bars) are rebuilt for the new theme.</summary>
    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        if (IPlatformApplication.Current?.Services.GetService<AppSettings>() is not { } settings) return;
        settings.Theme = settings.Theme switch { "system" => "light", "light" => "dark", _ => "system" };
        OnPropertyChanged(nameof(ThemeText));
        await SafeLoadAsync();
    }

    [RelayCommand]
    private Task OpenQuickActionAsync(QuickAction action) => Ui.GoToAsync($"//{action.Route}");

    /// <summary>Recomputes the dashboard from the local database (cheap: today's rows and a 7-day report).</summary>
    public async Task LoadDashboardAsync()
    {
        var now = DateTime.Now;
        var me = _session.Me;
        TodayText = now.ToString("dddd, d MMMM yyyy");
        Greeting = me is null ? "" : $"{GreetingFor(now)}, {me.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? me.FullName}";
        var names = me?.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        Initials = names.Length switch
        {
            0 => "",
            1 => names[0][..1].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(names[0][0])}{char.ToUpperInvariant(names[^1][0])}",
        };
        ShowMoney = _session.Has(Permissions.Reports.Financial) || _session.Has(Permissions.Billing.View);
        ShowQueue = _session.Has(Permissions.Queue.Manage) || _session.Has(Permissions.Appointments.View);
        BuildQuickActions();

        if (!Sync.IsStarted) return;
        if (!_subscribed)
        {
            Sync.DataChanged += (_, _) => _ = SafeLoadAsync();
            _subscribed = true;
        }

        var branch = Sync.CurrentBranchId;
        BranchName = me?.Branches.FirstOrDefault(b => b.Id == branch)?.Name ?? "";
        var today = now.Date;

        // Today's appointments (clinic-local dates) and the live queue.
        var appointments = await Sync.Store.ListAsync<LocalAppointment>(a => a.BranchId == branch && a.Date == today);
        var active = appointments.Where(a => a.Status is not (AppointmentStatus.Cancelled or AppointmentStatus.Rescheduled)).ToList();
        var completed = active.Count(a => a.Status == AppointmentStatus.Completed);
        var inQueue = active.Where(a => AppointmentStatus.InQueue.Contains(a.Status)).ToList();
        var upcoming = active.Count(a => a.Status is AppointmentStatus.Requested or AppointmentStatus.Booked or AppointmentStatus.Confirmed);

        var summaryToday = await Sync.Reports.SummaryAsync(branch, today, today);
        var summaryWeek = ShowMoney ? await Sync.Reports.SummaryAsync(branch, today.AddDays(-6), today) : null;
        var totalPatients = await Sync.Database.Connection.Table<LocalPatient>().Where(p => !p.IsDeleted).CountAsync();

        Stats.Clear();
        if (_session.Has(Permissions.Appointments.View))
            Stats.Add(new StatTile("ic_calendar.png", "Appointments today", active.Count.ToString("N0"), $"{completed} done · {upcoming} upcoming"));
        if (ShowQueue)
            Stats.Add(new StatTile("ic_queue.png", "In the queue", inQueue.Count.ToString("N0"),
                $"{inQueue.Count(a => a.Status == AppointmentStatus.CheckedIn)} waiting · {inQueue.Count(a => a.Status == AppointmentStatus.WithDoctor)} with doctor"));
        if (_session.Has(Permissions.Patients.View))
            Stats.Add(new StatTile("ic_userplus.png", "New patients", summaryToday.NewPatients.ToString("N0"), $"{totalPatients:N0} on this device"));
        Stats.Add(new StatTile("ic_consult.png", "Consultations", summaryToday.Visits.ToString("N0"), "started today"));
        if (ShowMoney)
        {
            Stats.Add(new StatTile("ic_wallet.png", "Revenue today", $"Rs {summaryToday.RevenueNet:N0}", $"Rs {summaryWeek!.RevenueNet:N0} in 7 days"));
            Stats.Add(new StatTile("ic_receipt.png", "Pending dues", $"Rs {summaryToday.PendingAmount:N0}", $"{summaryToday.PendingInvoices} open invoice(s)"));
        }

        // Agenda: what is next today, in time / token order.
        var doctors = (await Sync.Store.ListAsync<LocalDoctor>()).ToDictionary(d => d.Id, d => d.FullName);
        var next = active
            .Where(a => !AppointmentStatus.Final.Contains(a.Status))
            .OrderBy(a => a.StartTime ?? TimeSpan.MaxValue).ThenBy(a => a.TokenNo ?? int.MaxValue)
            .Take(6).ToList();
        Agenda.Clear();
        foreach (var a in next)
        {
            var patient = await Sync.Store.GetAsync<LocalPatient>(a.PatientId);
            var (bg, fg) = ThemeColors.Chip(Tone(a.Status));
            var time = a.StartTime is { } t ? DateTime.Today.Add(t).ToString("h:mm tt") : a.TokenNo is { } tok ? $"Token {tok}" : "Walk-in";
            Agenda.Add(new AgendaRow(time, patient?.FullName ?? "Patient", doctors.GetValueOrDefault(a.DoctorId, "Doctor"),
                AppointmentStatus.Display(a.Status), bg, fg));
        }
        HasAgenda = Agenda.Count > 0;

        // Queue by stage.
        QueueStages.Clear();
        var total = Math.Max(1, inQueue.Count);
        void Stage(string name, string status, string colour)
        {
            var n = inQueue.Count(a => a.Status == status);
            QueueStages.Add(new QueueStage(name, n, (double)n / total, ThemeColors.Get(colour)));
        }
        Stage("Waiting", AppointmentStatus.CheckedIn, "Info");
        Stage("With the nurse", AppointmentStatus.Vitals, "Warning");
        Stage("With the doctor", AppointmentStatus.WithDoctor, "Success");
        Stage("On hold", AppointmentStatus.OnHold, "Danger");
        QueueTotalText = inQueue.Count == 0 ? "Nobody is waiting" : $"{inQueue.Count} patient(s) in the queue";

        // Revenue, last 7 days (bars scale to the best day).
        RevenueBars.Clear();
        if (summaryWeek is not null)
        {
            var byDay = summaryWeek.RevenueByDay.ToDictionary(r => r.Key, r => r.Net);
            var days = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).ToList();
            var max = Math.Max(1m, days.Max(d => byDay.GetValueOrDefault(d.ToString("yyyy-MM-dd"))));
            foreach (var d in days)
            {
                var amount = byDay.GetValueOrDefault(d.ToString("yyyy-MM-dd"));
                RevenueBars.Add(new RevenueBar(d.ToString("ddd"), Math.Max(4, (double)(amount / max) * ChartHeight),
                    amount >= 1000 ? $"{amount / 1000:0.#}k" : amount.ToString("N0"), d == today));
            }
            RevenueWeekText = $"Rs {summaryWeek.RevenueNet:N0} received in the last 7 days";
        }
        DashboardReady = true;
    }

    private async Task SafeLoadAsync()
    {
        try { await LoadDashboardAsync(); }
        catch (Exception ex) { ClientErrorReporter.Record("dashboard", ex); }
    }

    private static string Tone(string status) => status switch
    {
        AppointmentStatus.CheckedIn => "info",
        AppointmentStatus.Vitals or AppointmentStatus.OnHold => "warning",
        AppointmentStatus.WithDoctor or AppointmentStatus.Completed => "success",
        AppointmentStatus.NoShow or AppointmentStatus.LeftWithoutBeingSeen => "danger",
        _ => "neutral",
    };
}

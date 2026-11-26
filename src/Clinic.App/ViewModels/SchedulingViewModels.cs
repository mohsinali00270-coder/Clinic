using System.Collections.ObjectModel;
using Clinic.App.Services;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Local;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed record SlotOption(Slot Slot, string DoctorName)
{
    public string Text => Slot.Free > 1 ? $"{Slot.Start:hh\\:mm} ({Slot.Free})" : $"{Slot.Start:hh\\:mm}";
    public string Long => $"{DoctorName} · {Slot.Date:ddd dd MMM} {Slot.Start:hh\\:mm}";
}

/// <summary>FR-APT-02/12/13: book (or reschedule) offline from locally calculated slots.</summary>
public sealed partial class BookingViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase, IQueryAttributable
{
    private Guid _patientId;
    private Guid? _rescheduleId;

    public ObservableCollection<LocalDoctor> Doctors { get; } = [];
    public ObservableCollection<LocalAppointmentType> Types { get; } = [];
    public ObservableCollection<string> Specialties { get; } = [];
    public ObservableCollection<SlotOption> Slots { get; } = [];
    public ObservableCollection<SlotOption> NextAvailable { get; } = [];

    [ObservableProperty] public partial string PageTitle { get; set; } = "Book appointment";
    [ObservableProperty] public partial string PatientName { get; set; } = "";
    [ObservableProperty] public partial string? Specialty { get; set; }
    [ObservableProperty] public partial LocalDoctor? Doctor { get; set; }
    [ObservableProperty] public partial LocalAppointmentType? Type { get; set; }
    [ObservableProperty] public partial DateTime Date { get; set; } = DateTime.Today;
    [ObservableProperty] public partial string? Notes { get; set; }
    [ObservableProperty] public partial string? Preparation { get; set; }
    [ObservableProperty] public partial string? SlotsInfo { get; set; }
    [ObservableProperty] public partial bool IsReschedule { get; set; }

    public bool CanOverbook => session.Has(Permissions.Appointments.Overbook);

    partial void OnSpecialtyChanged(string? value) => _ = LoadDoctorsAsync();
    partial void OnDoctorChanged(LocalDoctor? value) => _ = LoadSlotsAsync();
    partial void OnDateChanged(DateTime value) => _ = LoadSlotsAsync();
    partial void OnTypeChanged(LocalAppointmentType? value)
    {
        Preparation = value?.PreparationInstructions;
        _ = LoadSlotsAsync();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("patientId", out var p) && p is Guid pid) _patientId = pid;
        _rescheduleId = query.TryGetValue("rescheduleId", out var r) && r is Guid rid ? rid : null;
        IsReschedule = _rescheduleId is not null;
        PageTitle = IsReschedule ? "Reschedule" : "Book appointment";
        _ = LoadAsync();
    }

    private Task LoadAsync() => RunAsync(async () =>
    {
        LocalAppointment? old = null;
        if (_rescheduleId is { } id)
        {
            old = await sync.Store.GetAsync<LocalAppointment>(id) ?? throw new InvalidOperationException("Appointment not found.");
            _patientId = old.PatientId;
        }
        PatientName = (await sync.Store.GetAsync<LocalPatient>(_patientId))?.FullName ?? "";

        Types.Clear();
        foreach (var t in (await sync.Scheduling.TypesAsync()).Where(t => !t.IsWalkIn)) Types.Add(t);
        Type = Types.FirstOrDefault(t => t.Id == old?.TypeId) ?? Types.FirstOrDefault();

        Specialties.Clear();
        Specialties.Add("All specialties");
        foreach (var s in (await sync.Scheduling.DoctorsAtBranchAsync(sync.CurrentBranchId)).Select(d => d.Specialty).Distinct().Order())
            Specialties.Add(s);
        await LoadDoctorsAsync();
        if (old is not null) Doctor = Doctors.FirstOrDefault(d => d.Id == old.DoctorId);
    });

    private async Task LoadDoctorsAsync()
    {
        if (!sync.IsStarted) return;
        var specialty = Specialty is null or "All specialties" ? null : Specialty;
        var doctors = await sync.Scheduling.DoctorsAtBranchAsync(sync.CurrentBranchId, specialty);
        Doctors.Clear();
        foreach (var d in doctors) Doctors.Add(d);
        if (Doctor is null || !Doctors.Contains(Doctor)) Doctor = Doctors.FirstOrDefault();
    }

    private async Task LoadSlotsAsync()
    {
        Slots.Clear();
        if (Doctor is not { } d || !sync.IsStarted) return;
        var slots = await sync.Scheduling.SlotsAsync(d.Id, sync.CurrentBranchId, Date, Type?.Id);
        foreach (var s in slots.Where(s => s.IsAvailable || CanOverbook)) Slots.Add(new SlotOption(s, d.FullName));
        SlotsInfo = slots.Count == 0 ? "No slots: not working, on leave, branch closed, or outside the booking window."
            : $"{slots.Count(s => s.IsAvailable)} of {slots.Count} slots free" +
              (d.LicenceExpiry is { } exp && exp.Date < DateTime.Today ? " · ⚠ doctor's licence has expired" : "");
    }

    [RelayCommand]
    private void PreviousDay() { if (Date > DateTime.Today) Date = Date.AddDays(-1); }

    [RelayCommand]
    private void NextDay() => Date = Date.AddDays(1);

    /// <summary>FR-APT-13: first free slot per doctor of the chosen specialty.</summary>
    [RelayCommand]
    private Task FindNextAsync() => RunAsync(async () =>
    {
        var specialty = Specialty is null or "All specialties" ? null : Specialty;
        var found = await sync.Scheduling.NextAvailableAsync(sync.CurrentBranchId, specialty, Date, 14, Type?.Id);
        var names = (await sync.Store.ListAsync<LocalDoctor>()).ToDictionary(d => d.Id, d => d.FullName);
        NextAvailable.Clear();
        foreach (var s in found) NextAvailable.Add(new SlotOption(s, names.GetValueOrDefault(s.DoctorId, "Doctor")));
        if (found.Count == 0) ErrorMessage = "No free slot in the next 14 days.";
    });

    [RelayCommand]
    private Task PickAsync(SlotOption option) => ConfirmAsync(option);

    private async Task ConfirmAsync(SlotOption option)
    {
        if (Type is null) { ErrorMessage = "Choose an appointment type."; return; }
        var overbook = !option.Slot.IsAvailable;
        var question = $"{(IsReschedule ? "Move" : "Book")} {PatientName} with {option.Long}?" + (overbook ? "\n\n⚠ This slot is full — this will overbook it." : "");
        if (!await Ui.ConfirmAsync(IsReschedule ? "Reschedule" : "Book", question, IsReschedule ? "Move" : "Book")) return;

        var ok = await RunAsync(async () =>
        {
            if (_rescheduleId is { } id)
                await sync.Scheduling.RescheduleAsync(id, option.Slot.Date, option.Slot.Start);
            else
                await sync.Scheduling.BookAsync(_patientId, option.Slot.DoctorId, sync.CurrentBranchId, Type.Id, option.Slot.Date, option.Slot.Start,
                    BookingChannel.FrontDesk, Notes, allowOverbook: overbook && CanOverbook);
        });
        if (ok)
        {
            await Ui.AlertAsync("Done", $"{(IsReschedule ? "Moved to" : "Booked:")} {option.Long}.{(Preparation is { } p ? $"\n\nPreparation: {p}" : "")}");
            await Ui.GoBackAsync();
        }
    }
}

public sealed partial class AppointmentRow(LocalAppointment a, string patient, string doctor) : ObservableObject
{
    public LocalAppointment Appointment { get; } = a;
    public string Time => Appointment.StartTime is { } t ? t.ToString(@"hh\:mm") : $"Token {Appointment.TokenNo}";
    public string Title => patient;
    public string Subtitle => $"{doctor} · {AppointmentStatus.Display(Appointment.Status)}" + (Appointment.Channel == BookingChannel.WalkIn ? " · walk-in" : "");
    public bool Conflict => Appointment.SlotConflict;
    public bool Pending => Appointment.SyncStatus != SyncStatus.Synced;
    public bool CanCheckIn => Appointment.Status is AppointmentStatus.Booked or AppointmentStatus.Confirmed && Appointment.Date.Date == DateTime.Today;
    public bool CanChange => Appointment.Status is AppointmentStatus.Booked or AppointmentStatus.Confirmed or AppointmentStatus.Requested;
}

/// <summary>FR-APT-12 day view at this branch with check-in, cancel, no-show and reschedule.</summary>
public sealed partial class AppointmentsViewModel(SyncCoordinator sync) : ViewModelBase, IDisposable
{
    private bool _subscribed;

    public ObservableCollection<AppointmentRow> Appointments { get; } = [];

    [ObservableProperty] public partial DateTime Date { get; set; } = DateTime.Today;
    [ObservableProperty] public partial string Summary { get; set; } = "";

    partial void OnDateChanged(DateTime value) => LoadCommand.Execute(null);

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        if (!sync.IsStarted) return;
        if (!_subscribed) { sync.DataChanged += OnDataChanged; _subscribed = true; }
        var list = await sync.Scheduling.DayAsync(sync.CurrentBranchId, Date);
        var doctors = (await sync.Store.ListAsync<LocalDoctor>()).ToDictionary(d => d.Id, d => d.FullName);
        Appointments.Clear();
        foreach (var a in list)
        {
            var p = await sync.Store.GetAsync<LocalPatient>(a.PatientId);
            Appointments.Add(new AppointmentRow(a, p?.FullName ?? "Unknown", doctors.GetValueOrDefault(a.DoctorId, "Doctor")));
        }
        var conflicts = list.Count(a => a.SlotConflict);
        Summary = $"{list.Count} appointment(s)" + (conflicts > 0 ? $" · ⚠ {conflicts} double-booked — reschedule one" : "");
    });

    private void OnDataChanged(object? sender, IReadOnlyCollection<string> types)
    {
        if (types.Contains(SyncEntityNames.Appointment) || types.Contains(SyncEntityNames.Patient)) LoadCommand.Execute(null);
    }

    [RelayCommand] private void PreviousDay() => Date = Date.AddDays(-1);
    [RelayCommand] private void NextDay() => Date = Date.AddDays(1);
    [RelayCommand] private void Today() => Date = DateTime.Today;

    [RelayCommand]
    private async Task CheckInAsync(AppointmentRow row)
    {
        var priority = await Shell.Current.DisplayActionSheetAsync("Check in · priority", "Cancel", null, "Normal", "Priority (elderly / pregnant)", "Emergency");
        if (priority is null or "Cancel") return;
        var code = priority.StartsWith("Emergency") ? QueuePriority.Emergency : priority.StartsWith("Priority") ? QueuePriority.Priority : QueuePriority.Normal;
        LocalAppointment? a = null;
        if (await RunAsync(async () => a = await sync.Scheduling.CheckInAsync(row.Appointment.Id, code)))
            await Ui.AlertAsync("Checked in", $"Token {a!.TokenNo}.");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task MoreAsync(AppointmentRow row)
    {
        var choice = await Shell.Current.DisplayActionSheetAsync(row.Title, "Close", null,
            "Open patient", "Reschedule", "Cancel appointment", "Mark no-show", "Confirm");
        switch (choice)
        {
            case "Open patient":
                await Ui.GoToAsync(Routes.Patient, new Dictionary<string, object> { ["patientId"] = row.Appointment.PatientId });
                break;
            case "Reschedule":
                await Ui.GoToAsync(Routes.Booking, new Dictionary<string, object> { ["rescheduleId"] = row.Appointment.Id });
                break;
            case "Cancel appointment":
                var reasons = (await sync.Store.ListAsync<LocalLookupItem>(i => i.ListKey == "cancel_reasons" && i.IsActive))
                    .OrderBy(i => i.SortOrder).ToList();
                var reason = await Shell.Current.DisplayActionSheetAsync("Reason", "Back", null, reasons.Select(r => r.LabelEn).ToArray());
                var code = reasons.FirstOrDefault(r => r.LabelEn == reason)?.Code;
                if (code is null) return;
                var by = code == "doctor_unavailable" ? "clinic" : "patient";
                await RunAsync(() => sync.Scheduling.CancelAsync(row.Appointment.Id, code, by));
                break;
            case "Mark no-show":
                await RunAsync(() => sync.Scheduling.NoShowAsync(row.Appointment.Id));
                break;
            case "Confirm":
                await RunAsync(() => sync.Scheduling.ConfirmAsync(row.Appointment.Id));
                break;
        }
        await LoadAsync();
    }

    public void Dispose()
    {
        if (_subscribed) sync.DataChanged -= OnDataChanged;
    }
}

public sealed record QueueRow(QueueEntry Entry)
{
    public string Token => Entry.Token;
    public string Name => Entry.PatientName;
    public string Status => Entry.StatusText + (Entry.Appointment.Priority != QueuePriority.Normal ? $" · {Entry.Appointment.Priority}" : "");
    public string Wait => Entry.Appointment.Status == AppointmentStatus.WithDoctor ? "In consultation"
        : $"Waiting {Entry.WaitingMinutes} min · est. {Entry.EstimatedWaitMinutes} min";
    public bool IsWithDoctor => Entry.Appointment.Status == AppointmentStatus.WithDoctor;
    public bool IsEmergency => Entry.Appointment.Priority == QueuePriority.Emergency;
}

/// <summary>FR-APT-03/23/25: one doctor's live queue. Updates from other devices arrive through SignalR + sync.</summary>
public sealed partial class QueueViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase, IDisposable
{
    private bool _subscribed;
    private IDispatcherTimer? _timer;

    public ObservableCollection<LocalDoctor> Doctors { get; } = [];
    public ObservableCollection<QueueRow> Queue { get; } = [];

    [ObservableProperty] public partial LocalDoctor? Doctor { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string? NowServing { get; set; }

    partial void OnDoctorChanged(LocalDoctor? value)
    {
        if (value is not null) Preferences.Default.Set("queue_doctor", value.Id.ToString());
        _ = RefreshAsync();
    }

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        if (!sync.IsStarted) return;
        if (!_subscribed) { sync.DataChanged += OnDataChanged; _subscribed = true; }
        StartTimer();

        var doctors = await sync.Scheduling.DoctorsAtBranchAsync(sync.CurrentBranchId);
        Doctors.Clear();
        foreach (var d in doctors) Doctors.Add(d);

        // A doctor sees their own queue first; others get the last one they looked at.
        var mine = doctors.FirstOrDefault(d => d.UserId == session.Me?.UserId);
        var last = Guid.TryParse(Preferences.Default.Get("queue_doctor", ""), out var id) ? doctors.FirstOrDefault(d => d.Id == id) : null;
        var pick = mine ?? last ?? doctors.FirstOrDefault();
        if (pick?.Id != Doctor?.Id) Doctor = pick;
        else await RefreshAsync();
    });

    private async Task RefreshAsync()
    {
        if (Doctor is not { } d || !sync.IsStarted) return;
        var queue = await sync.Scheduling.QueueAsync(d.Id, DateTime.Today);
        Queue.Clear();
        foreach (var q in queue) Queue.Add(new QueueRow(q));
        var serving = queue.FirstOrDefault(q => q.Appointment.Status == AppointmentStatus.WithDoctor);
        NowServing = serving is null ? "No one with the doctor" : $"Now serving token {serving.Token} · {serving.PatientName}";
        Summary = $"{queue.Count(q => q.Appointment.Status is AppointmentStatus.CheckedIn or AppointmentStatus.Vitals)} waiting · " +
                  $"avg {await sync.Scheduling.AverageConsultMinutesAsync(d.Id, DateTime.Today)} min per patient";
    }

    private void StartTimer()
    {
        if (_timer is not null || Application.Current?.Dispatcher is not { } dispatcher) return;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30); // waiting times tick even without new data
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    private void OnDataChanged(object? sender, IReadOnlyCollection<string> types)
    {
        if (types.Contains(SyncEntityNames.Appointment) || types.Contains(SyncEntityNames.Patient))
            MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());
    }

    [RelayCommand]
    private async Task CallNextAsync()
    {
        if (Doctor is not { } d) return;
        LocalAppointment? next = null;
        await RunAsync(async () => next = await sync.Scheduling.CallNextAsync(d.Id));
        if (next is null && !HasError) await Ui.AlertAsync("Queue", "No one is waiting.");
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ActAsync(QueueRow row)
    {
        var a = row.Entry.Appointment;
        var options = new List<string>();
        if (session.Has(Permissions.Encounters.Edit) && a.Status is AppointmentStatus.CheckedIn or AppointmentStatus.Vitals or AppointmentStatus.WithDoctor)
            options.Add("Open consultation");
        if (a.Status is AppointmentStatus.CheckedIn or AppointmentStatus.OnHold) options.Add("Send to vitals");
        if (a.Status is AppointmentStatus.CheckedIn or AppointmentStatus.Vitals or AppointmentStatus.OnHold) options.Add("Start consultation");
        if (a.Status == AppointmentStatus.WithDoctor) { options.Add("Complete"); options.Add("Send back to nurse"); }
        if (a.Status != AppointmentStatus.OnHold) options.Add("Hold / skip");
        if (a.Status == AppointmentStatus.OnHold) options.Add("Back to queue");
        options.Add("Transfer to another doctor");
        options.Add("Left without being seen");
        options.Add("Open patient");

        var choice = await Shell.Current.DisplayActionSheetAsync($"Token {row.Token} · {row.Name}", "Close", null, options.ToArray());
        switch (choice)
        {
            case "Send to vitals":
            case "Send back to nurse": await RunAsync(() => sync.Scheduling.SendToVitalsAsync(a.Id)); break;
            case "Start consultation": await RunAsync(() => sync.Scheduling.StartConsultationAsync(a.Id)); break;
            case "Complete": await RunAsync(() => sync.Scheduling.CompleteAsync(a.Id)); break;
            case "Hold / skip": await RunAsync(() => sync.Scheduling.HoldAsync(a.Id)); break;
            case "Back to queue": await RunAsync(() => sync.Scheduling.ResumeAsync(a.Id)); break;
            case "Left without being seen": await RunAsync(() => sync.Scheduling.LeftWithoutBeingSeenAsync(a.Id)); break;
            case "Open patient": await Ui.GoToAsync(Routes.Patient, new Dictionary<string, object> { ["patientId"] = a.PatientId }); break;
            case "Open consultation": await Ui.GoToAsync(Routes.Encounter, new Dictionary<string, object> { ["appointmentId"] = a.Id }); return;
            case "Transfer to another doctor":
                var others = Doctors.Where(d => d.Id != a.DoctorId).ToList();
                var pick = await Shell.Current.DisplayActionSheetAsync("Transfer to", "Cancel", null, others.Select(d => d.FullName).ToArray());
                if (others.FirstOrDefault(d => d.FullName == pick) is { } to)
                    await RunAsync(() => sync.Scheduling.TransferAsync(a.Id, to.Id));
                break;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void SyncNow() => sync.RequestSync(manual: true);

    public void Dispose()
    {
        _timer?.Stop();
        if (_subscribed) sync.DataChanged -= OnDataChanged;
    }
}

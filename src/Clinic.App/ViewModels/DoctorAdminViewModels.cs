using System.Collections.ObjectModel;
using System.Globalization;
using Clinic.App.Services;
using Clinic.Shared.Contracts;
using Clinic.Shared.Scheduling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed record DoctorRow(DoctorDto Doctor)
{
    public string Subtitle => $"{Doctor.Specialty} · {Doctor.DoctorType.Replace('_', ' ')}" + (Doctor.IsActive ? "" : " · inactive");
    public string Licence => Doctor.LicenceStatus switch
    {
        LicenceStatus.Expired => "⚠ Licence expired",
        LicenceStatus.Expiring => $"Licence expires {Doctor.LicenceExpiry:dd MMM}",
        LicenceStatus.Unknown => "No licence date",
        _ => "",
    };
}

/// <summary>FR-DOC-01/07: doctors with licence alerts (admin, online).</summary>
public sealed partial class DoctorsViewModel(AdminApi api) : ViewModelBase
{
    public ObservableCollection<DoctorRow> Doctors { get; } = [];

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        var list = await api.GetDoctorsAsync();
        Doctors.Clear();
        foreach (var d in list) Doctors.Add(new DoctorRow(d));
    });

    [RelayCommand] private Task AddAsync() => Ui.GoToAsync(Routes.DoctorEdit);

    [RelayCommand]
    private Task OpenAsync(DoctorRow row) => Ui.GoToAsync(Routes.DoctorEdit, new Dictionary<string, object> { ["doctor"] = row.Doctor });

    [RelayCommand] private Task SetupAsync() => Ui.GoToAsync(Routes.SchedulingSetup);
}

public sealed record ScheduleRow(ScheduleDto Schedule, string BranchCode)
{
    public string Text => $"{CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)Schedule.DayOfWeek)} " +
                          $"{Schedule.StartTime:hh\\:mm}–{Schedule.EndTime:hh\\:mm} at {BranchCode} · {Schedule.SlotMinutes} min" +
                          (Schedule.Capacity > 1 ? $" × {Schedule.Capacity}" : "") + (Schedule.BufferMinutes > 0 ? $" + {Schedule.BufferMinutes} buffer" : "");
}

public sealed record LeaveRow(LeaveDto Leave)
{
    public string Text => $"{Leave.StartDate:dd MMM} – {Leave.EndDate:dd MMM yyyy}" + (Leave.Reason is { } r ? $" · {r}" : "");
}

/// <summary>FR-DOC-01/02/08, FR-APT-01/08: profile, weekly sessions and leave.</summary>
public sealed partial class DoctorEditViewModel(AdminApi api) : ViewModelBase, IQueryAttributable
{
    private Guid? _id;
    private List<BranchDto> _branches = [];

    public IReadOnlyList<string> Types { get; } = [DoctorTypes.Permanent, DoctorTypes.Visiting, DoctorTypes.OnCall];
    public ObservableCollection<string> Specialties { get; } = [];
    public ObservableCollection<UserDto> Users { get; } = [];
    public ObservableCollection<ScheduleRow> Schedules { get; } = [];
    public ObservableCollection<LeaveRow> Leaves { get; } = [];

    [ObservableProperty] public partial string PageTitle { get; set; } = "New doctor";
    [ObservableProperty] public partial string FullName { get; set; } = "";
    [ObservableProperty] public partial string? Specialty { get; set; }
    [ObservableProperty] public partial string? Gender { get; set; }
    [ObservableProperty] public partial string? RegistrationNo { get; set; }
    [ObservableProperty] public partial DateTime LicenceExpiry { get; set; } = DateTime.Today.AddYears(1);
    [ObservableProperty] public partial string DoctorType { get; set; } = DoctorTypes.Permanent;
    [ObservableProperty] public partial string? Fee { get; set; }
    [ObservableProperty] public partial string AverageMinutes { get; set; } = "10";
    [ObservableProperty] public partial bool IsActive { get; set; } = true;
    [ObservableProperty] public partial UserDto? LinkedUser { get; set; }
    [ObservableProperty] public partial bool IsExisting { get; set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var doctor = query.TryGetValue("doctor", out var d) ? d as DoctorDto : null;
        _id = doctor?.Id;
        IsExisting = doctor is not null;
        PageTitle = doctor?.FullName ?? "New doctor";
        if (doctor is not null)
        {
            FullName = doctor.FullName;
            Specialty = doctor.Specialty;
            Gender = doctor.Gender;
            RegistrationNo = doctor.RegistrationNo;
            LicenceExpiry = doctor.LicenceExpiry ?? DateTime.Today.AddYears(1);
            DoctorType = doctor.DoctorType;
            Fee = doctor.ConsultationFee?.ToString(CultureInfo.InvariantCulture);
            AverageMinutes = doctor.AverageConsultMinutes.ToString();
            IsActive = doctor.IsActive;
        }
        _ = LoadAsync(doctor?.UserId);
    }

    private Task LoadAsync(Guid? userId) => RunAsync(async () =>
    {
        var lists = await api.GetLookupListsAsync();
        var specialties = await api.GetLookupListAsync(lists.Single(l => l.Key == "specialties").Id);
        Specialties.Clear();
        foreach (var s in specialties.Items.Where(i => i.IsActive)) Specialties.Add(s.Code);

        Users.Clear();
        foreach (var u in (await api.GetUsersAsync(null)).Items) Users.Add(u);
        LinkedUser = Users.FirstOrDefault(u => u.Id == userId);
        _branches = await api.GetBranchesAsync();
        await LoadScheduleAsync();
    });

    private async Task LoadScheduleAsync()
    {
        if (_id is not { } id) return;
        Schedules.Clear();
        foreach (var s in await api.GetSchedulesAsync(id))
            Schedules.Add(new ScheduleRow(s, _branches.FirstOrDefault(b => b.Id == s.BranchId)?.Code ?? "?"));
        Leaves.Clear();
        foreach (var l in await api.GetLeavesAsync(id)) Leaves.Add(new LeaveRow(l));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Specialty is null) { ErrorMessage = "Choose a specialty."; return; }
        DoctorDto? saved = null;
        var ok = await RunAsync(async () => saved = await api.SaveDoctorAsync(_id, new SaveDoctorRequest(
            FullName, Specialty, Gender, RegistrationNo, LicenceExpiry, DoctorType,
            decimal.TryParse(Fee, NumberStyles.Number, CultureInfo.InvariantCulture, out var fee) ? fee : null,
            int.TryParse(AverageMinutes, out var avg) ? avg : 10, IsActive, LinkedUser?.Id)));
        if (!ok) return;
        _id = saved!.Id;
        IsExisting = true;
        PageTitle = saved.FullName;
        await Ui.AlertAsync("Saved", "Doctor saved. Devices receive the change on their next sync.");
    }

    /// <summary>Adds a weekly session, e.g. Monday 09:00–13:00, 15-minute slots.</summary>
    [RelayCommand]
    private async Task AddScheduleAsync()
    {
        if (_id is not { } id) { await Ui.AlertAsync("Schedule", "Save the doctor first."); return; }
        var days = CultureInfo.InvariantCulture.DateTimeFormat.DayNames;
        var day = await Shell.Current.DisplayActionSheetAsync("Day", "Cancel", null, ["Every weekday (Mon–Sat)", .. days]);
        if (day is null or "Cancel") return;
        var branch = await BranchPicker.PickAsync(_branches);
        if (branch is null) return;
        var times = await Ui.PromptAsync("Session", "Start-end, e.g. 09:00-13:00", "09:00-13:00");
        if (times?.Split('-') is not [var s, var e] || !TimeSpan.TryParse(s, out var start) || !TimeSpan.TryParse(e, out var end))
        {
            if (times is not null) await Ui.AlertAsync("Session", "Use the form 09:00-13:00.");
            return;
        }
        var slot = await Ui.PromptAsync("Session", "Slot length in minutes", "15");
        var capacity = await Ui.PromptAsync("Session", "Patients per slot", "1");
        if (!int.TryParse(slot, out var slotMinutes) || !int.TryParse(capacity, out var cap)) return;

        var dayNumbers = day.StartsWith("Every") ? [1, 2, 3, 4, 5, 6] : new[] { Array.IndexOf(days, day) };
        await RunAsync(async () =>
        {
            foreach (var d in dayNumbers)
                await api.AddScheduleAsync(id, new SaveScheduleRequest(branch.Id, d, start, end, slotMinutes, cap));
        });
        await LoadScheduleAsync();
    }

    [RelayCommand]
    private async Task RemoveScheduleAsync(ScheduleRow row)
    {
        if (!await Ui.ConfirmAsync("Remove session", row.Text + "?", "Remove")) return;
        await RunAsync(() => api.DeleteScheduleAsync(row.Schedule.DoctorId, row.Schedule.Id));
        await LoadScheduleAsync();
    }

    [RelayCommand]
    private async Task AddLeaveAsync()
    {
        if (_id is not { } id) return;
        var from = await Ui.PromptAsync("Leave", "From (yyyy-mm-dd)", DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"));
        var to = await Ui.PromptAsync("Leave", "To (yyyy-mm-dd)", from);
        if (!DateTime.TryParse(from, out var f) || !DateTime.TryParse(to, out var t)) return;
        var reason = await Ui.PromptAsync("Leave", "Reason (optional)");
        await RunAsync(() => api.AddLeaveAsync(id, new SaveLeaveRequest(f, t, reason, null)));
        await LoadScheduleAsync();
    }

    [RelayCommand]
    private async Task RemoveLeaveAsync(LeaveRow row)
    {
        await RunAsync(() => api.DeleteLeaveAsync(row.Leave.DoctorId, row.Leave.Id));
        await LoadScheduleAsync();
    }
}

internal static class BranchPicker
{
    public static async Task<BranchDto?> PickAsync(List<BranchDto> branches)
    {
        if (branches.Count == 1) return branches[0];
        var name = await Shell.Current.DisplayActionSheetAsync("Branch", "Cancel", null, branches.Select(x => x.Name).ToArray());
        return branches.FirstOrDefault(b => b.Name == name);
    }
}

public sealed record ClosureRow(BranchClosureDto Closure, string Branch)
{
    public string Text => $"{Closure.Date:ddd dd MMM yyyy} · {Branch}" + (Closure.Reason is { } r ? $" · {r}" : "");
}

/// <summary>FR-APT-06/09/10: appointment types and branch holidays.</summary>
public sealed partial class SchedulingSetupViewModel(AdminApi api) : ViewModelBase
{
    private List<BranchDto> _branches = [];

    public ObservableCollection<AppointmentTypeDto> Types { get; } = [];
    public ObservableCollection<ClosureRow> Closures { get; } = [];

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        _branches = await api.GetBranchesAsync();
        Types.Clear();
        foreach (var t in await api.GetAppointmentTypesAsync()) Types.Add(t);
        Closures.Clear();
        foreach (var c in (await api.GetClosuresAsync()).Where(c => c.Date >= DateTime.Today.AddDays(-7)))
            Closures.Add(new ClosureRow(c, _branches.FirstOrDefault(b => b.Id == c.BranchId)?.Name ?? "?"));
    });

    [RelayCommand] private Task AddTypeAsync() => EditTypeAsync(null);

    [RelayCommand]
    private async Task EditTypeAsync(AppointmentTypeDto? type)
    {
        var name = await Ui.PromptAsync("Appointment type", "Name", type?.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var code = type?.Code ?? await Ui.PromptAsync("Appointment type", "Code (e.g. ultrasound)",
            new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()));
        if (string.IsNullOrWhiteSpace(code)) return;
        var minutes = await Ui.PromptAsync("Appointment type", "Duration (minutes)", (type?.DurationMinutes ?? 15).ToString());
        var fee = await Ui.PromptAsync("Appointment type", "Fee (optional)", type?.Fee?.ToString(CultureInfo.InvariantCulture));
        var prep = await Ui.PromptAsync("Appointment type", "Preparation instructions (optional)", type?.PreparationInstructions);
        var notice = await Ui.PromptAsync("Booking limits", "Minimum notice (minutes)", (type?.MinNoticeMinutes ?? 0).ToString());
        if (!int.TryParse(minutes, out var m)) return;

        await RunAsync(() => api.SaveAppointmentTypeAsync(type?.Id, new SaveAppointmentTypeRequest(code, name, m, type?.Color,
            decimal.TryParse(fee, NumberStyles.Number, CultureInfo.InvariantCulture, out var f) ? f : null,
            string.IsNullOrWhiteSpace(prep) ? null : prep, int.TryParse(notice, out var n) ? n : 0,
            type?.MaxAdvanceDays ?? 60, type?.MaxReschedules ?? 2, type?.IsWalkIn ?? false, type?.IsActive ?? true, type?.SortOrder ?? 50)));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task AddClosureAsync()
    {
        var branch = await BranchPicker.PickAsync(_branches);
        if (branch is null) return;
        var date = await Ui.PromptAsync("Holiday", "Date (yyyy-mm-dd)", DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"));
        if (!DateTime.TryParse(date, out var d)) return;
        var reason = await Ui.PromptAsync("Holiday", "Reason (e.g. Eid)");
        await RunAsync(() => api.AddClosureAsync(new SaveBranchClosureRequest(branch.Id, d, reason)));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RemoveClosureAsync(ClosureRow row)
    {
        await RunAsync(() => api.DeleteClosureAsync(row.Closure.Id));
        await LoadAsync();
    }
}

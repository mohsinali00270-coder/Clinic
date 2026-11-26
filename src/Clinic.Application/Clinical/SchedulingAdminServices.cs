using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Clinical;
using Clinic.Shared.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>FR-DOC-01/02/07/08 and FR-APT-01/08: doctor profiles, weekly schedules and leave (admin, online).</summary>
public sealed class DoctorService(IAppDbContext db, TimeProvider clock)
{
    public async Task<IReadOnlyList<DoctorDto>> ListAsync(CancellationToken ct)
    {
        var today = clock.GetLocalNow().Date;
        var rows = await db.DoctorProfiles.OrderBy(d => d.FullName).ToListAsync(ct);
        return rows.Select(d => ToDto(d, today)).ToList();
    }

    public async Task<DoctorDto> GetAsync(Guid id, CancellationToken ct) =>
        ToDto(await LoadAsync(id, ct), clock.GetLocalNow().Date);

    public async Task<DoctorDto> SaveAsync(Guid? id, SaveDoctorRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.FullName)) throw new RequestValidationException("fullName", "Name is required.");
        if (string.IsNullOrWhiteSpace(r.Specialty)) throw new RequestValidationException("specialty", "Specialty is required.");
        if (!await db.LookupItems.AnyAsync(i => i.ListKey == "specialties" && i.Code == r.Specialty, ct))
            throw new RequestValidationException("specialty", $"'{r.Specialty}' is not in the specialties list.");
        if (!DoctorTypes.All.Contains(r.DoctorType)) throw new RequestValidationException("doctorType", "Type must be permanent, visiting or on_call.");
        if (r.AverageConsultMinutes is < 1 or > 240) throw new RequestValidationException("averageConsultMinutes", "Average consultation must be 1–240 minutes.");
        if (r.ConsultationFee < 0) throw new RequestValidationException("consultationFee", "Fee cannot be negative.");
        if (r.UserId is { } uid)
        {
            if (!await db.Users.AnyAsync(u => u.Id == uid, ct)) throw new RequestValidationException("userId", "User not found.");
            if (await db.DoctorProfiles.AnyAsync(d => d.UserId == uid && d.Id != id, ct))
                throw new RequestValidationException("userId", "That user is already linked to another doctor profile.");
        }

        var doctor = id is { } existing ? await LoadAsync(existing, ct) : new DoctorProfile { Id = Guid.CreateVersion7() };
        doctor.FullName = r.FullName.Trim();
        doctor.Specialty = r.Specialty;
        doctor.Gender = Normalize.Optional(r.Gender);
        doctor.RegistrationNo = Normalize.Optional(r.RegistrationNo);
        doctor.LicenceExpiry = r.LicenceExpiry?.Date;
        doctor.DoctorType = r.DoctorType;
        doctor.ConsultationFee = r.ConsultationFee;
        doctor.AverageConsultMinutes = r.AverageConsultMinutes;
        doctor.IsActive = r.IsActive;
        doctor.UserId = r.UserId;
        if (id is null) db.DoctorProfiles.Add(doctor);
        await db.SaveChangesAsync(ct);
        return ToDto(doctor, clock.GetLocalNow().Date);
    }

    // ---- schedules

    public async Task<IReadOnlyList<ScheduleDto>> SchedulesAsync(Guid doctorId, CancellationToken ct)
    {
        await LoadAsync(doctorId, ct);
        return await db.DoctorSchedules.Where(s => s.DoctorId == doctorId)
            .OrderBy(s => s.DayOfWeek).ThenBy(s => s.StartTime)
            .Select(s => new ScheduleDto(s.Id, s.DoctorId, s.BranchId, s.DayOfWeek, s.StartTime, s.EndTime, s.SlotMinutes, s.Capacity, s.BufferMinutes, s.ValidFrom, s.ValidTo))
            .ToListAsync(ct);
    }

    public async Task<ScheduleDto> SaveScheduleAsync(Guid doctorId, Guid? scheduleId, SaveScheduleRequest r, CancellationToken ct)
    {
        await LoadAsync(doctorId, ct);
        if (r.DayOfWeek is < 0 or > 6) throw new RequestValidationException("dayOfWeek", "Day must be 0 (Sunday) to 6 (Saturday).");
        if (r.EndTime <= r.StartTime || r.EndTime > TimeSpan.FromHours(24)) throw new RequestValidationException("endTime", "End must be after start.");
        if (r.SlotMinutes is < 5 or > 240) throw new RequestValidationException("slotMinutes", "Slot length must be 5–240 minutes.");
        if (r.Capacity is < 1 or > 50) throw new RequestValidationException("capacity", "Capacity must be 1–50 patients per slot.");
        if (r.BufferMinutes is < 0 or > 60) throw new RequestValidationException("bufferMinutes", "Buffer must be 0–60 minutes.");
        if (!await db.Branches.AnyAsync(b => b.Id == r.BranchId, ct)) throw new RequestValidationException("branchId", "Branch not found.");

        // A doctor cannot be in two places at once.
        // (TimeSpan comparisons are done in memory: a doctor has only a few sessions per day, and SQLite cannot compare them.)
        var sameDay = await db.DoctorSchedules.Where(s => s.DoctorId == doctorId && s.Id != scheduleId && s.DayOfWeek == r.DayOfWeek).ToListAsync(ct);
        var overlap = sameDay.Any(s => s.StartTime < r.EndTime && r.StartTime < s.EndTime);
        if (overlap) throw new RequestValidationException("startTime", "This overlaps another session of the same doctor on that day.");

        DoctorSchedule s;
        if (scheduleId is { } sid)
            s = await db.DoctorSchedules.FirstOrDefaultAsync(x => x.Id == sid && x.DoctorId == doctorId, ct) ?? throw new NotFoundException("Schedule", sid);
        else
        {
            s = new DoctorSchedule { Id = Guid.CreateVersion7(), DoctorId = doctorId };
            db.DoctorSchedules.Add(s);
        }
        s.BranchId = r.BranchId;
        s.DayOfWeek = r.DayOfWeek;
        s.StartTime = r.StartTime;
        s.EndTime = r.EndTime;
        s.SlotMinutes = r.SlotMinutes;
        s.Capacity = r.Capacity;
        s.BufferMinutes = r.BufferMinutes;
        s.ValidFrom = r.ValidFrom?.Date;
        s.ValidTo = r.ValidTo?.Date;
        await db.SaveChangesAsync(ct);
        return new ScheduleDto(s.Id, s.DoctorId, s.BranchId, s.DayOfWeek, s.StartTime, s.EndTime, s.SlotMinutes, s.Capacity, s.BufferMinutes, s.ValidFrom, s.ValidTo);
    }

    public async Task DeleteScheduleAsync(Guid doctorId, Guid scheduleId, CancellationToken ct)
    {
        var s = await db.DoctorSchedules.FirstOrDefaultAsync(x => x.Id == scheduleId && x.DoctorId == doctorId, ct)
                ?? throw new NotFoundException("Schedule", scheduleId);
        s.IsDeleted = true;
        await db.SaveChangesAsync(ct);
    }

    // ---- leave

    public async Task<IReadOnlyList<LeaveDto>> LeavesAsync(Guid doctorId, CancellationToken ct) =>
        await db.DoctorLeaves.Where(l => l.DoctorId == doctorId).OrderByDescending(l => l.StartDate)
            .Select(l => new LeaveDto(l.Id, l.DoctorId, l.StartDate, l.EndDate, l.Reason, l.SubstituteDoctorId)).ToListAsync(ct);

    public async Task<LeaveDto> AddLeaveAsync(Guid doctorId, SaveLeaveRequest r, CancellationToken ct)
    {
        await LoadAsync(doctorId, ct);
        if (r.EndDate.Date < r.StartDate.Date) throw new RequestValidationException("endDate", "End date is before the start date.");
        if (r.SubstituteDoctorId is { } sub && (sub == doctorId || !await db.DoctorProfiles.AnyAsync(d => d.Id == sub, ct)))
            throw new RequestValidationException("substituteDoctorId", "Substitute doctor not found.");

        var leave = new DoctorLeave
        {
            Id = Guid.CreateVersion7(), DoctorId = doctorId, StartDate = r.StartDate.Date, EndDate = r.EndDate.Date,
            Reason = Normalize.Optional(r.Reason), SubstituteDoctorId = r.SubstituteDoctorId,
        };
        db.DoctorLeaves.Add(leave);
        await db.SaveChangesAsync(ct);
        return new LeaveDto(leave.Id, doctorId, leave.StartDate, leave.EndDate, leave.Reason, leave.SubstituteDoctorId);
    }

    public async Task DeleteLeaveAsync(Guid doctorId, Guid leaveId, CancellationToken ct)
    {
        var l = await db.DoctorLeaves.FirstOrDefaultAsync(x => x.Id == leaveId && x.DoctorId == doctorId, ct) ?? throw new NotFoundException("Leave", leaveId);
        l.IsDeleted = true;
        await db.SaveChangesAsync(ct);
    }

    private async Task<DoctorProfile> LoadAsync(Guid id, CancellationToken ct) =>
        await db.DoctorProfiles.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Doctor", id);

    private static DoctorDto ToDto(DoctorProfile d, DateTime today) => new(d.Id, d.UserId, d.FullName, d.Specialty, d.Gender,
        d.RegistrationNo, d.LicenceExpiry, d.DoctorType, d.ConsultationFee, d.AverageConsultMinutes, d.IsActive,
        LicenceStatus.Of(d.LicenceExpiry, today));
}

/// <summary>FR-APT-06/09/10: appointment types and branch holidays.</summary>
public sealed class SchedulingSetupService(IAppDbContext db)
{
    public async Task<IReadOnlyList<AppointmentTypeDto>> TypesAsync(CancellationToken ct) =>
        (await db.AppointmentTypes.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<AppointmentTypeDto> SaveTypeAsync(Guid? id, SaveAppointmentTypeRequest r, CancellationToken ct)
    {
        var code = r.Code?.Trim().ToLowerInvariant() ?? "";
        if (code.Length is 0 or > 40) throw new RequestValidationException("code", "Code is required (max 40).");
        if (string.IsNullOrWhiteSpace(r.Name)) throw new RequestValidationException("name", "Name is required.");
        if (r.DurationMinutes is < 5 or > 480) throw new RequestValidationException("durationMinutes", "Duration must be 5–480 minutes.");
        if (r.MinNoticeMinutes is < 0 or > 10080) throw new RequestValidationException("minNoticeMinutes", "Minimum notice must be 0–10080 minutes.");
        if (r.MaxAdvanceDays is < 0 or > 365) throw new RequestValidationException("maxAdvanceDays", "Maximum advance must be 0–365 days.");
        if (r.MaxReschedules is < 0 or > 10) throw new RequestValidationException("maxReschedules", "Reschedule limit must be 0–10.");
        if (r.Fee < 0) throw new RequestValidationException("fee", "Fee cannot be negative.");
        if (await db.AppointmentTypes.AnyAsync(t => t.Code == code && t.Id != id, ct))
            throw new RequestValidationException("code", "Another type uses this code.");

        AppointmentType t;
        if (id is { } tid)
            t = await db.AppointmentTypes.FirstOrDefaultAsync(x => x.Id == tid, ct) ?? throw new NotFoundException("Appointment type", tid);
        else
        {
            t = new AppointmentType { Id = Guid.CreateVersion7() };
            db.AppointmentTypes.Add(t);
        }
        t.Code = code;
        t.Name = r.Name.Trim();
        t.DurationMinutes = r.DurationMinutes;
        t.Color = Normalize.Optional(r.Color);
        t.Fee = r.Fee;
        t.PreparationInstructions = Normalize.Optional(r.PreparationInstructions);
        t.MinNoticeMinutes = r.MinNoticeMinutes;
        t.MaxAdvanceDays = r.MaxAdvanceDays;
        t.MaxReschedules = r.MaxReschedules;
        t.IsWalkIn = r.IsWalkIn;
        t.IsActive = r.IsActive;
        t.SortOrder = r.SortOrder;
        await db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    public async Task<IReadOnlyList<BranchClosureDto>> ClosuresAsync(CancellationToken ct) =>
        await db.BranchClosures.OrderBy(c => c.Date)
            .Select(c => new BranchClosureDto(c.Id, c.BranchId, c.Date, c.Reason)).ToListAsync(ct);

    public async Task<BranchClosureDto> AddClosureAsync(SaveBranchClosureRequest r, CancellationToken ct)
    {
        if (!await db.Branches.AnyAsync(b => b.Id == r.BranchId, ct)) throw new RequestValidationException("branchId", "Branch not found.");
        var date = r.Date.Date;
        if (await db.BranchClosures.AnyAsync(c => c.BranchId == r.BranchId && c.Date == date, ct))
            throw new RequestValidationException("date", "That day is already marked closed.");
        var c = new BranchClosure { Id = Guid.CreateVersion7(), BranchId = r.BranchId, Date = date, Reason = Normalize.Optional(r.Reason) };
        db.BranchClosures.Add(c);
        await db.SaveChangesAsync(ct);
        return new BranchClosureDto(c.Id, c.BranchId, c.Date, c.Reason);
    }

    public async Task DeleteClosureAsync(Guid id, CancellationToken ct)
    {
        var c = await db.BranchClosures.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Closure", id);
        c.IsDeleted = true;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Default appointment types for a new (or upgraded) tenant.</summary>
    public static async Task EnsureDefaultsAsync(IAppDbContext db, CancellationToken ct = default)
    {
        (string Code, string Name, int Minutes, string Color, bool WalkIn, int Order)[] defaults =
        [
            ("consultation", "Consultation", 15, "#0F766E", false, 10),
            ("follow_up", "Follow-up", 10, "#2563EB", false, 20),
            ("procedure", "Procedure", 30, "#B45309", false, 30),
            ("walk_in", "Walk-in", 15, "#6B7280", true, 40),
        ];
        var existing = (await db.AppointmentTypes.IgnoreQueryFilters().Where(t => t.TenantId == db.CurrentTenantId)
            .Select(t => t.Code).ToListAsync(ct)).ToHashSet();
        foreach (var d in defaults.Where(d => !existing.Contains(d.Code)))
        {
            db.AppointmentTypes.Add(new AppointmentType
            {
                Id = Guid.CreateVersion7(), Code = d.Code, Name = d.Name, DurationMinutes = d.Minutes, Color = d.Color,
                IsWalkIn = d.WalkIn, SortOrder = d.Order, MinNoticeMinutes = 0, MaxAdvanceDays = 60, MaxReschedules = 2,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private static AppointmentTypeDto ToDto(AppointmentType t) => new(t.Id, t.Code, t.Name, t.DurationMinutes, t.Color, t.Fee,
        t.PreparationInstructions, t.MinNoticeMinutes, t.MaxAdvanceDays, t.MaxReschedules, t.IsWalkIn, t.IsActive, t.SortOrder);
}

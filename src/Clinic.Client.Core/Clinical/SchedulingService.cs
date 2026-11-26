using Clinic.Client.Core.Local;
using Clinic.Shared.Scheduling;

namespace Clinic.Client.Core.Clinical;

public sealed record QueueEntry(
    LocalAppointment Appointment,
    string PatientName,
    string Mrn,
    int Position,
    int WaitingMinutes,
    int EstimatedWaitMinutes)
{
    public string StatusText => AppointmentStatus.Display(Appointment.Status);
    public string Token => Appointment.TokenNo is { } t ? t.ToString() : "–";
}

public sealed class SchedulingException(string message) : Exception(message);

/// <summary>
/// §4.2 on the device: slots, booking, walk-ins, check-in, the live queue and its transitions. Works offline;
/// every status change also writes a time-stamped history row (FR-APT-19).
/// </summary>
public sealed class SchedulingService(LocalStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private DateTime LocalNow => _clock.GetLocalNow().DateTime;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ reference data

    public async Task<List<LocalDoctor>> DoctorsAtBranchAsync(Guid branchId, string? specialty = null)
    {
        var scheduled = (await store.ListAsync<LocalDoctorSchedule>(s => s.BranchId == branchId)).Select(s => s.DoctorId).ToHashSet();
        return (await store.ListAsync<LocalDoctor>(d => d.IsActive))
            .Where(d => scheduled.Contains(d.Id) && (specialty is null || d.Specialty == specialty))
            .OrderBy(d => d.FullName).ToList();
    }

    public async Task<List<LocalAppointmentType>> TypesAsync() =>
        (await store.ListAsync<LocalAppointmentType>(t => t.IsActive)).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToList();

    // ------------------------------------------------------------------ slots

    public async Task<IReadOnlyList<Slot>> SlotsAsync(Guid doctorId, Guid branchId, DateTime date, Guid? typeId = null, bool ignoreNotice = false)
    {
        var type = typeId is { } t ? await store.GetAsync<LocalAppointmentType>(t) : null;
        var limits = new BookingLimits(ignoreNotice ? 0 : type?.MinNoticeMinutes ?? 0, type?.MaxAdvanceDays ?? 60);

        var schedules = (await store.ListAsync<LocalDoctorSchedule>(s => s.DoctorId == doctorId))
            .Select(s => new ScheduleBlock(s.DoctorId, s.BranchId, s.DayOfWeek, s.StartTime, s.EndTime, s.SlotMinutes, s.Capacity, s.BufferMinutes, s.ValidFrom, s.ValidTo));
        var leave = (await store.ListAsync<LocalDoctorLeave>(l => l.DoctorId == doctorId)).Select(l => new DateRange(l.StartDate, l.EndDate));
        var closures = (await store.ListAsync<LocalBranchClosure>(c => c.BranchId == branchId)).Select(c => c.Date);
        var day = date.Date;
        var booked = (await store.ListAsync<LocalAppointment>(a => a.DoctorId == doctorId && a.Date == day))
            .Where(a => a.StartTime is not null && AppointmentStatus.OccupiesSlot.Contains(a.Status))
            .Select(a => new BookedSlot(a.DoctorId, a.Date, a.StartTime!.Value, a.Id));

        return SlotCalculator.SlotsFor(doctorId, branchId, day, schedules, leave, closures, booked, LocalNow, limits);
    }

    /// <summary>FR-APT-13: the first free slot of each doctor (optionally of one specialty) at this branch.</summary>
    public async Task<IReadOnlyList<Slot>> NextAvailableAsync(Guid branchId, string? specialty, DateTime from, int days = 14, Guid? typeId = null)
    {
        var doctors = await DoctorsAtBranchAsync(branchId, specialty);
        var slots = new List<Slot>();
        foreach (var d in doctors)
        {
            for (var i = 0; i < days; i++)
            {
                var free = (await SlotsAsync(d.Id, branchId, from.Date.AddDays(i), typeId)).FirstOrDefault(s => s.IsAvailable);
                if (free is null) continue;
                slots.Add(free);
                break;
            }
        }
        return slots.OrderBy(s => s.StartsAt).ToList();
    }

    // ------------------------------------------------------------------ booking

    /// <summary>
    /// FR-APT-02/04: book a slot. A full slot is refused unless overbooking is allowed (FR-APT-17);
    /// a doctor on leave or a closed branch has no slots at all.
    /// </summary>
    public async Task<LocalAppointment> BookAsync(Guid patientId, Guid doctorId, Guid branchId, Guid typeId, DateTime date, TimeSpan start,
        string channel = BookingChannel.FrontDesk, string? notes = null, bool allowOverbook = false)
    {
        var type = await store.GetAsync<LocalAppointmentType>(typeId) ?? throw new SchedulingException("Unknown appointment type.");
        await RequirePatientAsync(patientId);
        await RequireDoctorAsync(doctorId);

        var slot = (await SlotsAsync(doctorId, branchId, date, typeId)).FirstOrDefault(s => s.Start == start)
                   ?? throw new SchedulingException("That time is not available for booking (outside the schedule, on leave, closed, or too soon/too far ahead).");
        if (!slot.IsAvailable && !allowOverbook)
            throw new SchedulingException("That slot is already full. Choose another time.");

        var day = date.Date;
        var clash = (await store.ListAsync<LocalAppointment>(a => a.PatientId == patientId && a.Date == day))
            .Any(a => a.StartTime == start && AppointmentStatus.OccupiesSlot.Contains(a.Status));
        if (clash) throw new SchedulingException("This patient already has an appointment at that time.");

        var appointment = new LocalAppointment
        {
            Id = Guid.NewGuid(), PatientId = patientId, DoctorId = doctorId, BranchId = branchId, TypeId = typeId,
            Date = day, StartTime = start, DurationMinutes = type.DurationMinutes,
            Status = AppointmentStatus.Booked, Channel = channel, Notes = notes,
        };
        await store.WriteAsync(b =>
        {
            b.Insert(appointment);
            History(b, appointment, null, AppointmentStatus.Booked, null);
        });
        return appointment;
    }

    /// <summary>FR-APT-02/21: walk-in straight into the queue with a token.</summary>
    public async Task<LocalAppointment> WalkInAsync(Guid patientId, Guid doctorId, Guid branchId, string priority = QueuePriority.Normal, string? notes = null)
    {
        await RequirePatientAsync(patientId);
        var doctor = await RequireDoctorAsync(doctorId);
        var type = (await TypesAsync()).FirstOrDefault(t => t.IsWalkIn) ?? (await TypesAsync()).First();
        var today = LocalNow.Date;

        var appointment = new LocalAppointment
        {
            Id = Guid.NewGuid(), PatientId = patientId, DoctorId = doctorId, BranchId = branchId, TypeId = type.Id,
            Date = today, StartTime = null, DurationMinutes = type.DurationMinutes > 0 ? type.DurationMinutes : doctor.AverageConsultMinutes,
            Status = AppointmentStatus.CheckedIn, Channel = BookingChannel.WalkIn, Priority = priority,
            CheckedInAt = UtcNow, Notes = notes,
        };
        await store.WriteAsync(b =>
        {
            appointment.TokenNo = NextToken(b, doctorId, today);
            b.Insert(appointment);
            History(b, appointment, null, AppointmentStatus.CheckedIn, "walk-in");
        });
        return appointment;
    }

    /// <summary>FR-APT-20/21: check in a booked patient, give a token, set queue priority.</summary>
    public Task<LocalAppointment> CheckInAsync(Guid appointmentId, string priority = QueuePriority.Normal) =>
        ChangeAsync(appointmentId, AppointmentStatus.CheckedIn, null, (b, a) =>
        {
            if (a.Date.Date != LocalNow.Date) throw new SchedulingException("Only today's appointments can be checked in.");
            a.Priority = priority;
            a.CheckedInAt = UtcNow;
            a.TokenNo ??= NextToken(b, a.DoctorId, a.Date);
        });

    public Task<LocalAppointment> SendToVitalsAsync(Guid id) => ChangeAsync(id, AppointmentStatus.Vitals, null);

    public Task<LocalAppointment> StartConsultationAsync(Guid id) =>
        ChangeAsync(id, AppointmentStatus.WithDoctor, null, (_, a) => a.CalledAt = UtcNow);

    public Task<LocalAppointment> CompleteAsync(Guid id) =>
        ChangeAsync(id, AppointmentStatus.Completed, null, (_, a) => a.CompletedAt = UtcNow);

    public Task<LocalAppointment> HoldAsync(Guid id, string? reason = null) => ChangeAsync(id, AppointmentStatus.OnHold, reason);

    public Task<LocalAppointment> ResumeAsync(Guid id) => ChangeAsync(id, AppointmentStatus.CheckedIn, "back in queue");

    public Task<LocalAppointment> LeftWithoutBeingSeenAsync(Guid id) => ChangeAsync(id, AppointmentStatus.LeftWithoutBeingSeen, null);

    public Task<LocalAppointment> ConfirmAsync(Guid id) => ChangeAsync(id, AppointmentStatus.Confirmed, null);

    /// <summary>FR-APT-26: cancel with a reason from the lookup list (by patient or clinic).</summary>
    public Task<LocalAppointment> CancelAsync(Guid id, string reason, string cancelledBy = "patient") =>
        ChangeAsync(id, AppointmentStatus.Cancelled, reason, (_, a) =>
        {
            a.CancelReason = reason;
            a.CancelledBy = cancelledBy;
        });

    public Task<LocalAppointment> NoShowAsync(Guid id) => ChangeAsync(id, AppointmentStatus.NoShow, null, (_, a) =>
    {
        if (a.Date.Date + (a.StartTime ?? TimeSpan.Zero) > LocalNow) throw new SchedulingException("The appointment time has not passed yet.");
    });

    /// <summary>FR-APT-02/26: move to a new slot; the old one is kept as "rescheduled". Limited per type (default 2).</summary>
    public async Task<LocalAppointment> RescheduleAsync(Guid id, DateTime newDate, TimeSpan newStart, string? reason = null)
    {
        var old = await store.GetAsync<LocalAppointment>(id) ?? throw new SchedulingException("Appointment not found.");
        var type = await store.GetAsync<LocalAppointmentType>(old.TypeId);
        if (old.RescheduleCount >= (type?.MaxReschedules ?? 2))
            throw new SchedulingException($"This appointment was already rescheduled {old.RescheduleCount} time(s), the limit for {type?.Name ?? "this type"}.");
        if (!AppointmentStatus.CanMove(old.Status, AppointmentStatus.Rescheduled))
            throw new SchedulingException($"A {AppointmentStatus.Display(old.Status).ToLowerInvariant()} appointment cannot be rescheduled.");

        var slot = (await SlotsAsync(old.DoctorId, old.BranchId, newDate, old.TypeId)).FirstOrDefault(s => s.Start == newStart)
                   ?? throw new SchedulingException("That time is not available.");
        if (!slot.IsAvailable) throw new SchedulingException("That slot is already full.");

        var fresh = new LocalAppointment
        {
            Id = Guid.NewGuid(), PatientId = old.PatientId, DoctorId = old.DoctorId, BranchId = old.BranchId, TypeId = old.TypeId,
            Date = newDate.Date, StartTime = newStart, DurationMinutes = old.DurationMinutes, Status = AppointmentStatus.Booked,
            Channel = old.Channel, Notes = old.Notes, RescheduleCount = old.RescheduleCount + 1, RescheduledFromId = old.Id,
        };
        await store.WriteAsync(b =>
        {
            var from = old.Status;
            old.Status = AppointmentStatus.Rescheduled;
            b.Update(old);
            History(b, old, from, AppointmentStatus.Rescheduled, reason);
            b.Insert(fresh);
            History(b, fresh, null, AppointmentStatus.Booked, "rescheduled");
        });
        return fresh;
    }

    /// <summary>FR-APT-25: transfer a waiting patient to another doctor (new token in that queue).</summary>
    public async Task<LocalAppointment> TransferAsync(Guid id, Guid toDoctorId)
    {
        await RequireDoctorAsync(toDoctorId);
        var a = await store.GetAsync<LocalAppointment>(id) ?? throw new SchedulingException("Appointment not found.");
        if (!AppointmentStatus.InQueue.Contains(a.Status)) throw new SchedulingException("Only patients in the queue can be transferred.");
        if (a.DoctorId == toDoctorId) return a;

        await store.WriteAsync(b =>
        {
            var from = a.Status;
            a.DoctorId = toDoctorId;
            a.StartTime = null;
            a.Status = AppointmentStatus.CheckedIn;
            a.TokenNo = NextToken(b, toDoctorId, a.Date);
            b.Update(a);
            History(b, a, from, AppointmentStatus.CheckedIn, "transferred");
        });
        return a;
    }

    // ------------------------------------------------------------------ queue

    /// <summary>
    /// FR-APT-03/23: the doctor's queue today: with doctor, then waiting (emergency → priority → normal, then
    /// check-in time), vitals, on hold. Estimated wait = place in line × today's average consultation time.
    /// </summary>
    public async Task<List<QueueEntry>> QueueAsync(Guid doctorId, DateTime date)
    {
        var day = date.Date;
        var appointments = (await store.ListAsync<LocalAppointment>(a => a.DoctorId == doctorId && a.Date == day))
            .Where(a => AppointmentStatus.InQueue.Contains(a.Status)).ToList();
        var average = await AverageConsultMinutesAsync(doctorId, day);
        var now = UtcNow;

        static int Group(string s) => s switch
        {
            AppointmentStatus.WithDoctor => 0,
            AppointmentStatus.CheckedIn or AppointmentStatus.Vitals => 1,
            _ => 2,
        };

        var ordered = appointments
            .OrderBy(a => Group(a.Status))
            .ThenBy(a => QueuePriority.Rank(a.Priority))
            .ThenBy(a => a.CheckedInAt ?? DateTime.MaxValue)
            .ThenBy(a => a.TokenNo ?? int.MaxValue)
            .ToList();

        var patients = new Dictionary<Guid, LocalPatient?>();
        var result = new List<QueueEntry>();
        var ahead = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i];
            if (!patients.TryGetValue(a.PatientId, out var p)) patients[a.PatientId] = p = await store.GetAsync<LocalPatient>(a.PatientId);
            var waiting = a.CheckedInAt is { } ci ? (int)Math.Max(0, (now - ci).TotalMinutes) : 0;
            var estimate = a.Status == AppointmentStatus.WithDoctor ? 0 : ahead * average;
            if (a.Status != AppointmentStatus.OnHold) ahead++;
            result.Add(new QueueEntry(a, p?.FullName ?? "Unknown patient", p?.Mrn ?? "", i + 1, waiting, estimate));
        }
        return result;
    }

    /// <summary>FR-APT-25 "call next": finish whoever is with the doctor, then call the first waiting patient.</summary>
    public async Task<LocalAppointment?> CallNextAsync(Guid doctorId)
    {
        var queue = await QueueAsync(doctorId, LocalNow.Date);
        foreach (var current in queue.Where(q => q.Appointment.Status == AppointmentStatus.WithDoctor))
            await CompleteAsync(current.Appointment.Id);
        var next = queue.FirstOrDefault(q => q.Appointment.Status is AppointmentStatus.CheckedIn or AppointmentStatus.Vitals);
        return next is null ? null : await StartConsultationAsync(next.Appointment.Id);
    }

    public async Task<int> AverageConsultMinutesAsync(Guid doctorId, DateTime day)
    {
        var done = (await store.ListAsync<LocalAppointment>(a => a.DoctorId == doctorId && a.Date == day))
            .Where(a => a.CalledAt is not null && a.CompletedAt is not null)
            .Select(a => (a.CompletedAt!.Value - a.CalledAt!.Value).TotalMinutes)
            .Where(m => m is > 0 and < 180).ToList();
        if (done.Count > 0) return (int)Math.Ceiling(done.Average());
        return (await store.GetAsync<LocalDoctor>(doctorId))?.AverageConsultMinutes ?? 10;
    }

    public async Task<List<LocalAppointment>> DayAsync(Guid branchId, DateTime date, Guid? doctorId = null)
    {
        var day = date.Date;
        return (await store.ListAsync<LocalAppointment>(a => a.BranchId == branchId && a.Date == day))
            .Where(a => doctorId is null || a.DoctorId == doctorId)
            .OrderBy(a => a.StartTime ?? TimeSpan.MaxValue).ThenBy(a => a.TokenNo).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<LocalAppointment> ChangeAsync(Guid id, string to, string? reason, Action<LocalWriteBatch, LocalAppointment>? apply = null)
    {
        var a = await store.GetAsync<LocalAppointment>(id) ?? throw new SchedulingException("Appointment not found.");
        if (a.Status == to) return a;
        if (!AppointmentStatus.CanMove(a.Status, to))
            throw new SchedulingException($"Cannot change a {AppointmentStatus.Display(a.Status).ToLowerInvariant()} appointment to {AppointmentStatus.Display(to).ToLowerInvariant()}.");

        await store.WriteAsync(b =>
        {
            var from = a.Status;
            apply?.Invoke(b, a);
            a.Status = to;
            b.Update(a);
            History(b, a, from, to, reason);
        });
        return a;
    }

    private void History(LocalWriteBatch b, LocalAppointment a, string? from, string to, string? reason) =>
        b.Insert(new LocalAppointmentStatusHistory
        {
            Id = Guid.NewGuid(), AppointmentId = a.Id, BranchId = a.BranchId, FromStatus = from, ToStatus = to,
            ChangedAt = UtcNow, ChangedBy = store.Context.UserId, Reason = reason,
        });

    /// <summary>Daily per-doctor token. Two offline devices can clash; the server then hands out the next free one.</summary>
    private static int NextToken(LocalWriteBatch b, Guid doctorId, DateTime day) =>
        b.Connection.ExecuteScalar<int>(
            "SELECT COALESCE(MAX(TokenNo), 0) FROM Appointments WHERE DoctorId = ? AND Date = ? AND IsDeleted = 0",
            doctorId, day.Date) + 1;

    private async Task RequirePatientAsync(Guid id)
    {
        if (await store.GetAsync<LocalPatient>(id) is null) throw new SchedulingException("Patient not found on this device.");
    }

    private async Task<LocalDoctor> RequireDoctorAsync(Guid id)
    {
        var d = await store.GetAsync<LocalDoctor>(id) ?? throw new SchedulingException("Doctor not found.");
        if (!d.IsActive) throw new SchedulingException($"{d.FullName} is not active.");
        return d;
    }
}

using Clinic.Domain.Forms;
using Clinic.Domain.Sync;

namespace Clinic.Domain.Clinical;

/// <summary>
/// FR-PAT-01: core patient fields; everything else (CNIC, address, allergies…) lives in the Patient dynamic form.
/// Created offline on devices with an MRN from a leased number range (FR-PAT-02).
/// </summary>
public class Patient : SyncEntity, IHasDynamicData
{
    public string Mrn { get; set; } = "";
    public string FullName { get; set; } = "";

    /// <summary>M, F or O.</summary>
    public string Gender { get; set; } = "";
    public DateTime? DateOfBirth { get; set; }

    /// <summary>True when only an age was given and the DOB was estimated from it.</summary>
    public bool DobEstimated { get; set; }
    public string? Phone { get; set; }

    /// <summary>Branch where the patient was registered (scopes which devices hold the record).</summary>
    public Guid BranchId { get; set; }

    public Guid? FormVersionId { get; set; }
    public string? DynamicData { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;

    public IReadOnlyDictionary<string, object?> GetFormVariables() => PatientVariables(Gender, DateOfBirth);

    public static IReadOnlyDictionary<string, object?> PatientVariables(string? gender, DateTime? dob) =>
        Clinic.Shared.Scheduling.PatientRules.FormVariables(gender, dob, DateTime.Today);
}

/// <summary>FR-DOC-01/02: doctor profile (core fields) plus the Doctor dynamic form.</summary>
public class DoctorProfile : SyncEntity, IHasDynamicData
{
    /// <summary>The staff login of this doctor, if any (used for "own records" scope and "my queue").</summary>
    public Guid? UserId { get; set; }
    public string FullName { get; set; } = "";

    /// <summary>Code from the 'specialties' lookup list.</summary>
    public string Specialty { get; set; } = "";
    public string? Gender { get; set; }
    public string? RegistrationNo { get; set; }
    public DateTime? LicenceExpiry { get; set; }
    public string DoctorType { get; set; } = "permanent";
    public decimal? ConsultationFee { get; set; }

    /// <summary>Used for estimated waiting time when there is no history yet today.</summary>
    public int AverageConsultMinutes { get; set; } = 10;
    public bool IsActive { get; set; } = true;

    public Guid? FormVersionId { get; set; }
    public string? DynamicData { get; set; }

    public IReadOnlyDictionary<string, object?> GetFormVariables() => new Dictionary<string, object?>();
}

/// <summary>FR-APT-01/08: a weekly working block of a doctor at a branch.</summary>
public class DoctorSchedule : SyncEntity
{
    public Guid DoctorId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday.</summary>
    public int DayOfWeek { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public int SlotMinutes { get; set; } = 15;

    /// <summary>Patients per slot.</summary>
    public int Capacity { get; set; } = 1;
    public int BufferMinutes { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-DOC-08: leave blocks booking; a substitute can be named.</summary>
public class DoctorLeave : SyncEntity
{
    public Guid DoctorId { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string? Reason { get; set; }
    public Guid? SubstituteDoctorId { get; set; }
}

/// <summary>FR-APT-10: branch holiday or closure.</summary>
public class BranchClosure : SyncEntity
{
    public Guid BranchId { get; set; }
    public DateTime Date { get; set; }
    public string? Reason { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-APT-06/09: consultation, follow-up, procedure… with duration, fee and booking limits.</summary>
public class AppointmentType : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int DurationMinutes { get; set; } = 15;
    public string? Color { get; set; }
    public decimal? Fee { get; set; }
    public string? PreparationInstructions { get; set; }
    public int MinNoticeMinutes { get; set; }
    public int MaxAdvanceDays { get; set; } = 60;
    public int MaxReschedules { get; set; } = 2;
    public bool IsWalkIn { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>FR-APT-02/19: a booking or walk-in, and its place in the day's queue.</summary>
public class Appointment : SyncEntity, IHasDynamicData
{
    public Guid PatientId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid BranchId { get; set; }
    public Guid TypeId { get; set; }

    /// <summary>Clinic-local date and start time (null start = walk-in without a slot).</summary>
    public DateTime Date { get; set; }
    public TimeSpan? StartTime { get; set; }
    public int DurationMinutes { get; set; }

    public string Status { get; set; } = "booked";
    public string Channel { get; set; } = "front_desk";
    public string Priority { get; set; } = "normal";
    public int? TokenNo { get; set; }

    public DateTime? CheckedInAt { get; set; }
    public DateTime? CalledAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CancelReason { get; set; }
    public string? CancelledBy { get; set; }
    public int RescheduleCount { get; set; }
    public Guid? RescheduledFromId { get; set; }
    public string? Notes { get; set; }

    /// <summary>FR-APT-04: set by the server when two offline devices booked the same full slot.</summary>
    public bool SlotConflict { get; set; }

    public Guid? FormVersionId { get; set; }
    public string? DynamicData { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;

    public IReadOnlyDictionary<string, object?> GetFormVariables() => new Dictionary<string, object?>();
}

/// <summary>FR-APT-19: every status change, time-stamped (append-only), for wait-time reports.</summary>
public class AppointmentStatusHistory : SyncEntity
{
    public Guid AppointmentId { get; set; }
    public Guid BranchId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public DateTime ChangedAt { get; set; }
    public Guid? ChangedBy { get; set; }
    public string? Reason { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>
/// FR-PAT-06 / FR-DATA-05: attachment metadata (synced). The file itself is uploaded separately in chunks and
/// kept in the file store, never in SQL.
/// </summary>
public class Attachment : SyncEntity
{
    public Guid RecordId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid? BranchId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Caption { get; set; }

    /// <summary>photo, document, report…</summary>
    public string Category { get; set; } = "document";

    /// <summary>Set by the server once every chunk arrived and the hash matched.</summary>
    public bool IsUploaded { get; set; }
    public string? StorageKey { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

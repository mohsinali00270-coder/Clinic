using System.Text.Json.Serialization;
using SQLite;

namespace Clinic.Client.Core.Local;

[Table("Patients")]
public sealed class LocalPatient : LocalEntity, ILocalDynamic
{
    [Indexed]
    public string Mrn { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Gender { get; set; } = "";
    public DateTime? DateOfBirth { get; set; }
    public bool DobEstimated { get; set; }
    public string? Phone { get; set; }

    [Indexed]
    public Guid BranchId { get; set; }
    public Guid? FormVersionId { get; set; }

    [JsonIgnore, LocalOnly]
    public string? DynamicData { get; set; }
}

[Table("Doctors")]
public sealed class LocalDoctor : LocalEntity, ILocalDynamic
{
    public Guid? UserId { get; set; }
    public string FullName { get; set; } = "";
    public string Specialty { get; set; } = "";
    public string? Gender { get; set; }
    public string? RegistrationNo { get; set; }
    public DateTime? LicenceExpiry { get; set; }
    public string DoctorType { get; set; } = "permanent";
    public decimal? ConsultationFee { get; set; }
    public int AverageConsultMinutes { get; set; } = 10;
    public bool IsActive { get; set; } = true;
    public Guid? FormVersionId { get; set; }

    [JsonIgnore, LocalOnly]
    public string? DynamicData { get; set; }
}

[Table("DoctorSchedules")]
public sealed class LocalDoctorSchedule : LocalEntity
{
    [Indexed]
    public Guid DoctorId { get; set; }
    public Guid BranchId { get; set; }
    public int DayOfWeek { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public int SlotMinutes { get; set; } = 15;
    public int Capacity { get; set; } = 1;
    public int BufferMinutes { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
}

[Table("DoctorLeaves")]
public sealed class LocalDoctorLeave : LocalEntity
{
    [Indexed]
    public Guid DoctorId { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string? Reason { get; set; }
    public Guid? SubstituteDoctorId { get; set; }
}

[Table("BranchClosures")]
public sealed class LocalBranchClosure : LocalEntity
{
    public Guid BranchId { get; set; }
    public DateTime Date { get; set; }
    public string? Reason { get; set; }
}

[Table("AppointmentTypes")]
public sealed class LocalAppointmentType : LocalEntity
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

[Table("Appointments")]
public sealed class LocalAppointment : LocalEntity, ILocalDynamic
{
    [Indexed]
    public Guid PatientId { get; set; }

    [Indexed]
    public Guid DoctorId { get; set; }
    public Guid BranchId { get; set; }
    public Guid TypeId { get; set; }

    [Indexed]
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

    /// <summary>Set by the server only (FR-APT-04).</summary>
    public bool SlotConflict { get; set; }
    public Guid? FormVersionId { get; set; }

    [JsonIgnore, LocalOnly]
    public string? DynamicData { get; set; }
}

[Table("AppointmentStatusHistory")]
public sealed class LocalAppointmentStatusHistory : LocalEntity
{
    [Indexed]
    public Guid AppointmentId { get; set; }
    public Guid BranchId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public DateTime ChangedAt { get; set; }
    public Guid? ChangedBy { get; set; }
    public string? Reason { get; set; }
}

public enum UploadState
{
    /// <summary>Waiting for the metadata to sync, then for the bytes to upload.</summary>
    Pending = 0,
    Uploaded = 1,

    /// <summary>Created on another device; the file is downloaded when someone opens it.</summary>
    Remote = 2,
}

[Table("Attachments")]
public sealed class LocalAttachment : LocalEntity
{
    [Indexed]
    public Guid RecordId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid? BranchId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Caption { get; set; }
    public string Category { get; set; } = "document";
    public bool IsUploaded { get; set; }

    /// <summary>Encrypted copy in app storage (FR-SEC-02); null until downloaded on devices that did not take it.</summary>
    [JsonIgnore, LocalOnly]
    public string? LocalPath { get; set; }

    [JsonIgnore, LocalOnly]
    public UploadState UploadState { get; set; } = UploadState.Remote;
}

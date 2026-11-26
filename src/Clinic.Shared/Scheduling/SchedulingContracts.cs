namespace Clinic.Shared.Scheduling;

public static class SchedulingRoutes
{
    public const string Doctors = "api/v1/doctors";
    public const string AppointmentTypes = "api/v1/appointment-types";
    public const string BranchClosures = "api/v1/branch-closures";
    public const string Files = "api/v1/files";
    public const string Hub = "/hubs/clinic";
}

public sealed record DoctorDto(
    Guid Id,
    Guid? UserId,
    string FullName,
    string Specialty,
    string? Gender,
    string? RegistrationNo,
    DateTime? LicenceExpiry,
    string DoctorType,
    decimal? ConsultationFee,
    int AverageConsultMinutes,
    bool IsActive,
    string LicenceStatus);

public sealed record SaveDoctorRequest(
    string FullName,
    string Specialty,
    string? Gender,
    string? RegistrationNo,
    DateTime? LicenceExpiry,
    string DoctorType,
    decimal? ConsultationFee,
    int AverageConsultMinutes,
    bool IsActive,
    Guid? UserId);

public sealed record ScheduleDto(Guid Id, Guid DoctorId, Guid BranchId, int DayOfWeek, TimeSpan StartTime, TimeSpan EndTime,
    int SlotMinutes, int Capacity, int BufferMinutes, DateTime? ValidFrom, DateTime? ValidTo);

public sealed record SaveScheduleRequest(Guid BranchId, int DayOfWeek, TimeSpan StartTime, TimeSpan EndTime,
    int SlotMinutes = 15, int Capacity = 1, int BufferMinutes = 0, DateTime? ValidFrom = null, DateTime? ValidTo = null);

public sealed record LeaveDto(Guid Id, Guid DoctorId, DateTime StartDate, DateTime EndDate, string? Reason, Guid? SubstituteDoctorId);

public sealed record SaveLeaveRequest(DateTime StartDate, DateTime EndDate, string? Reason, Guid? SubstituteDoctorId);

public sealed record AppointmentTypeDto(Guid Id, string Code, string Name, int DurationMinutes, string? Color, decimal? Fee,
    string? PreparationInstructions, int MinNoticeMinutes, int MaxAdvanceDays, int MaxReschedules, bool IsWalkIn, bool IsActive, int SortOrder);

public sealed record SaveAppointmentTypeRequest(string Code, string Name, int DurationMinutes, string? Color, decimal? Fee,
    string? PreparationInstructions, int MinNoticeMinutes = 0, int MaxAdvanceDays = 60, int MaxReschedules = 2,
    bool IsWalkIn = false, bool IsActive = true, int SortOrder = 0);

public sealed record BranchClosureDto(Guid Id, Guid BranchId, DateTime Date, string? Reason);

public sealed record SaveBranchClosureRequest(Guid BranchId, DateTime Date, string? Reason);

/// <summary>FR-DOC-07: licence state shown on doctor lists and dashboards.</summary>
public static class LicenceStatus
{
    public const string Ok = "ok";
    public const string Expiring = "expiring";
    public const string Expired = "expired";
    public const string Unknown = "unknown";

    public static string Of(DateTime? expiry, DateTime today) => expiry switch
    {
        null => Unknown,
        { } e when e.Date < today.Date => Expired,
        { } e when e.Date <= today.Date.AddDays(30) => Expiring,
        _ => Ok,
    };
}

// ---------- files (FR-OFF-23 chunked, resumable) ----------

public sealed record UploadStatusDto(Guid AttachmentId, int ChunkSize, IReadOnlyList<int> ReceivedChunks, bool IsComplete);

public sealed record CompleteUploadRequest(int TotalChunks);

/// <summary>SignalR events (FR-SYN-03).</summary>
public static class HubEvents
{
    /// <summary>Payload: list of branch ids (empty = tenant-wide) whose data changed. Devices pull.</summary>
    public const string ChangesAvailable = "ChangesAvailable";
}

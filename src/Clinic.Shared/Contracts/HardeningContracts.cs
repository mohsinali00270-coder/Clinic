namespace Clinic.Shared.Contracts;

public static class HardeningRoutes
{
    public const string Duplicates = "api/v1/patients/duplicates";
    public const string Merge = "api/v1/patients/merge";
    public const string Audit = "api/v1/audit";
    public const string ClientErrors = "api/v1/telemetry/errors";
}

/// <summary>FR-SYN-32: a likely duplicate pair, for an admin to confirm.</summary>
public sealed record DuplicatePairDto(PatientBrief A, PatientBrief B, IReadOnlyList<string> Reasons);

public sealed record PatientBrief(Guid Id, string Mrn, string FullName, string Gender, DateTime? DateOfBirth, string? Phone, DateTime CreatedAt, int Visits);

public sealed record MergePatientsRequest(Guid SurvivorId, Guid DuplicateId, string? Reason);

public sealed record MergeResultDto(Guid SurvivorId, Guid DuplicateId, IReadOnlyDictionary<string, int> Moved, int FieldValuesCopied);

/// <summary>FR-SEC-07 audit viewer row.</summary>
public sealed record AuditEntryDto(long Id, DateTime Timestamp, Guid? UserId, string? UserName, Guid? DeviceId, string Action, string EntityType,
    string? EntityId, string? OldValues, string? NewValues);

/// <summary>Crash and sync-error report from the app (observability without a third-party service).</summary>
public sealed record ClientErrorReport(string Kind, string Message, string? Detail, string AppVersion, string Platform, DateTimeOffset OccurredAt, Guid? DeviceId);

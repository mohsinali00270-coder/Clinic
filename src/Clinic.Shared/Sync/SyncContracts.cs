using System.Text.Json.Nodes;

namespace Clinic.Shared.Sync;

/// <summary>Entity type names used on the wire. Device and server must agree on these.</summary>
public static class SyncEntityNames
{
    public const string Branch = "Branch";
    public const string BranchNote = "BranchNote";
    public const string LookupList = "LookupList";
    public const string LookupItem = "LookupItem";
    public const string FormVersion = "FormVersion";
    public const string FieldValue = "FieldValue";
    public const string Patient = "Patient";
    public const string DoctorProfile = "DoctorProfile";
    public const string DoctorSchedule = "DoctorSchedule";
    public const string DoctorLeave = "DoctorLeave";
    public const string BranchClosure = "BranchClosure";
    public const string AppointmentType = "AppointmentType";
    public const string Appointment = "Appointment";
    public const string AppointmentStatusHistory = "AppointmentStatusHistory";
    public const string Attachment = "Attachment";
    public const string Encounter = "Encounter";
    public const string Vitals = "Vitals";
    public const string Diagnosis = "Diagnosis";
    public const string Icd10Code = "Icd10Code";
    public const string EncounterAmendment = "EncounterAmendment";
    public const string Drug = "Drug";
    public const string Prescription = "Prescription";
    public const string PrescriptionItem = "PrescriptionItem";
    public const string PrescriptionTemplate = "PrescriptionTemplate";

    // Phase 5b: cases, referrals, procedures and day care (§4.10–4.12)
    public const string CaseType = "CaseType";
    public const string Case = "Case";
    public const string CarePlanStep = "CarePlanStep";
    public const string CaseMember = "CaseMember";
    public const string CaseNote = "CaseNote";
    public const string Referral = "Referral";
    public const string ProcedureType = "ProcedureType";
    public const string Procedure = "Procedure";
    public const string Bed = "Bed";
    public const string Admission = "Admission";
    public const string BedTransfer = "BedTransfer";
    public const string NursingNote = "NursingNote";
    public const string MedicationOrder = "MedicationOrder";
    public const string MedicationDose = "MedicationDose";

    // Phase 6: labs and billing (§4.5, §4.6)
    public const string LabTest = "LabTest";
    public const string LabOrder = "LabOrder";
    public const string LabResult = "LabResult";
    public const string ServiceItem = "ServiceItem";
    public const string Invoice = "Invoice";
    public const string InvoiceLine = "InvoiceLine";
    public const string Payment = "Payment";
    public const string CashClosing = "CashClosing";

    // Phase 6b: billing extensions, communication, feedback (§4.13–4.15)
    public const string PackagePlan = "PackagePlan";
    public const string PatientPackage = "PatientPackage";
    public const string PackageUsage = "PackageUsage";
    public const string Payer = "Payer";
    public const string PatientPanel = "PatientPanel";
    public const string CreditNote = "CreditNote";
    public const string Expense = "Expense";
    public const string MessageTemplate = "MessageTemplate";
    public const string ContactPreference = "ContactPreference";
    public const string OutboundMessage = "OutboundMessage";
    public const string Feedback = "Feedback";
    public const string Complaint = "Complaint";

    // Phase 7: inventory (§4.7)
    public const string InventoryItem = "InventoryItem";
    public const string StockBatch = "StockBatch";
    public const string StockMovement = "StockMovement";
    public const string Dispensation = "Dispensation";

    // Phase 8
    public const string AccessEvent = "AccessEvent";
}

public enum SyncOperation
{
    Insert = 0,
    Update = 1,
    Delete = 2,
}

/// <summary>FR-SYN-11 per-change outcome.</summary>
public enum SyncChangeStatus
{
    /// <summary>Written (possibly auto-merged; see Note).</summary>
    Applied = 0,

    /// <summary>This ChangeId was already applied earlier; nothing was written again.</summary>
    Duplicate = 1,

    /// <summary>Another user changed the same field; a person has to choose (see ServerData).</summary>
    Conflict = 2,

    /// <summary>Refused for validation or permission reasons (see Error). Retrying will not help.</summary>
    Rejected = 3,
}

/// <summary>One outbox entry (FR-OFF-10). Field names in ChangedFields/Data are camelCase entity properties.</summary>
public sealed record SyncChange(
    Guid ChangeId,
    string EntityType,
    Guid RecordId,
    SyncOperation Operation,
    long? BaseRowVersion,
    IReadOnlyList<string> ChangedFields,
    JsonObject? Data,
    DateTimeOffset ClientTimestamp);

public sealed record SyncPushRequest(Guid DeviceId, string AppVersion, IReadOnlyList<SyncChange> Changes);

public sealed record SyncChangeResult(
    Guid ChangeId,
    SyncChangeStatus Status,
    long? RowVersion = null,
    string? Error = null,
    string? Note = null,
    JsonObject? ServerData = null,
    IReadOnlyDictionary<string, string>? IdMap = null,
    Guid? ConflictId = null);

public sealed record SyncPushResponse(IReadOnlyList<SyncChangeResult> Results);

public sealed record SyncTombstone(string EntityType, Guid RecordId, long RowVersion);

/// <summary>
/// FR-SYN-20: one page of changes after a checkpoint. Entities are keyed by type and listed in
/// dependency order (config → masters → patients → …). Save the checkpoint only after applying the page.
/// </summary>
public sealed record SyncPullResponse(
    long Checkpoint,
    bool HasMore,
    IReadOnlyDictionary<string, List<JsonObject>> Entities,
    IReadOnlyList<SyncTombstone> Tombstones,
    bool ResetRequired = false);

/// <summary>FR-SYN-30: how a person settled a conflict on the device.</summary>
public static class ConflictResolution
{
    public const string Mine = "mine";
    public const string Theirs = "theirs";
    public const string Merged = "merged";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Mine, Theirs, Merged };
}

/// <summary>Reported after the resolving change (if any) was pushed; the server closes the conflict and audits both values.</summary>
public sealed record ConflictResolutionReport(Guid ConflictId, string Resolution, JsonObject? Values, DateTimeOffset ResolvedAt);

public sealed record ConflictDto(Guid Id, string EntityType, Guid RecordId, IReadOnlyList<string> Fields, JsonObject? Mine, JsonObject? Theirs,
    Guid? DeviceId, Guid? UserId, DateTime CreatedAt, string Status, string? Resolution, JsonObject? Resolved, DateTime? ResolvedAt);

public sealed record SyncStatusResponse(long ServerCheckpoint, string MinAppVersion, DateTime ServerTimeUtc, int MaxPushBatch, int MaxPullPage);

// ---------- Number ranges (FR-OFF-22) ----------

public static class NumberRangeKinds
{
    public const string Mrn = "MRN";
    public const string Token = "Token";
    public const string Invoice = "Invoice";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Mrn, Token, Invoice };
}

public sealed record LeaseNumberRangeRequest(string Kind, int Size = 500);

public sealed record NumberRangeDto(Guid Id, string Kind, long Start, long End, DateTime LeasedAt);

public static class SyncRoutes
{
    public const string Root = "api/v1/sync";
    public const string Push = Root + "/push";
    public const string Pull = Root + "/pull";
    public const string Status = Root + "/status";
    public const string Snapshot = Root + "/snapshot";
    public const string Conflicts = Root + "/conflicts";
    public const string Resolutions = Root + "/conflicts/resolutions";

    public const string UpdateRequiredProblem = "https://clinic.app/problems/update-required";
}

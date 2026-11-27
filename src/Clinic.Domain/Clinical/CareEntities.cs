using Clinic.Domain.Forms;
using Clinic.Domain.Sync;

namespace Clinic.Domain.Clinical;

// =====================================================================================================
// §4.10 Patient cases (episodes of care)
// =====================================================================================================

/// <summary>FR-CASE-02: tenant-configured case type (master, server wins).</summary>
public class CaseType : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameUr { get; set; }

    /// <summary>Specialty this case type usually belongs to (filters the picker; optional).</summary>
    public string? Specialty { get; set; }
    public int? ExpectedSessions { get; set; }
    public int? FollowUpIntervalDays { get; set; }

    /// <summary>Default services / package (free text until billing packages arrive in Phase 6).</summary>
    public string? DefaultServices { get; set; }

    /// <summary>JSON array of <c>CasePlanTemplateStep</c> created when a case of this type opens.</summary>
    public string DefaultPlanJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
}

/// <summary>FR-CASE-01/07/08: one clinical problem of a patient, grouping visits, procedures and admissions.</summary>
public class PatientCase : SyncEntity, IHasDynamicData
{
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid CaseTypeId { get; set; }

    /// <summary>Copied from the type (form context and variables).</summary>
    public string CaseTypeCode { get; set; } = "";
    public string Title { get; set; } = "";
    public Guid? PrimaryDoctorId { get; set; }
    public DateTime OpenedAt { get; set; }
    public string Status { get; set; } = "open";
    public int? PlannedSessions { get; set; }
    public int? FollowUpIntervalDays { get; set; }

    public DateTime? ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }

    /// <summary>FR-CASE-07: code from the 'case_outcomes' lookup list.</summary>
    public string? Outcome { get; set; }
    public string? ClosingSummary { get; set; }

    public string? PatientGender { get; set; }
    public DateTime? PatientDateOfBirth { get; set; }

    public Guid? FormVersionId { get; set; }
    public string? DynamicData { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;

    public IReadOnlyDictionary<string, object?> GetFormVariables() =>
        new Dictionary<string, object?>(Clinic.Shared.Scheduling.PatientRules.FormVariables(PatientGender, PatientDateOfBirth, DateTime.Today))
        {
            ["caseType"] = CaseTypeCode,
        };
}

/// <summary>FR-CASE-05: an ordered, dated step of a case's care plan.</summary>
public class CarePlanStep : SyncEntity
{
    public Guid CaseId { get; set; }
    public Guid BranchId { get; set; }
    public int SortOrder { get; set; }
    public string Kind { get; set; } = "visit";
    public string Title { get; set; } = "";
    public DateTime? DueDate { get; set; }
    public string Status { get; set; } = "planned";
    public DateTime? CompletedAt { get; set; }
    public Guid? CompletedBy { get; set; }

    /// <summary>The encounter / procedure that fulfilled this step, if any.</summary>
    public Guid? LinkedRecordId { get; set; }
    public string? Notes { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-CASE-06: a doctor, nurse or therapist assigned to a case.</summary>
public class CaseMember : SyncEntity
{
    public Guid CaseId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? DoctorId { get; set; }
    public Guid? UserId { get; set; }
    public string Name { get; set; } = "";
    public string Role { get; set; } = "consulting";

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-CASE-06: a free-text note on the case (progress, team communication).</summary>
public class CaseNote : SyncEntity
{
    public Guid CaseId { get; set; }
    public Guid BranchId { get; set; }
    public string Body { get; set; } = "";
    public DateTime WrittenAt { get; set; }
    public Guid? WrittenBy { get; set; }
    public string? AuthorName { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

// =====================================================================================================
// §4.11 Referrals
// =====================================================================================================

/// <summary>FR-REF-01…03: internal (to a doctor/specialty), external (letter) or incoming referral.</summary>
public class Referral : SyncEntity
{
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public string Direction { get; set; } = "internal";
    public Guid? EncounterId { get; set; }
    public Guid? CaseId { get; set; }

    /// <summary>Referring doctor of this clinic (internal / external).</summary>
    public Guid? FromDoctorId { get; set; }

    /// <summary>Internal: a specific doctor, or any doctor of a specialty.</summary>
    public Guid? ToDoctorId { get; set; }
    public string? ToSpecialty { get; set; }

    /// <summary>External: hospital / specialist / imaging centre.</summary>
    public string? ExternalFacility { get; set; }
    public string? ExternalDoctor { get; set; }

    /// <summary>Incoming: code from the 'referrers' lookup (for reports and commissions) and/or the name.</summary>
    public string? SourceCode { get; set; }
    public string? SourceName { get; set; }

    public string Reason { get; set; } = "";
    public string Priority { get; set; } = "routine";
    public string Status { get; set; } = "pending";
    public DateTime ReferredAt { get; set; }
    public Guid? ReferredBy { get; set; }
    public DateTime? RespondedAt { get; set; }
    public Guid? RespondedBy { get; set; }
    public string? ResponseNote { get; set; }

    /// <summary>Clinical summary printed with an external referral letter.</summary>
    public string? Summary { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

// =====================================================================================================
// §4.12 Procedures, day care and admissions
// =====================================================================================================

/// <summary>FR-PRC-01: procedure master (server wins).</summary>
public class ProcedureType : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "other";
    public decimal? Fee { get; set; }
    public int DurationMinutes { get; set; } = 15;
    public bool RequiresConsent { get; set; }
    public string? ConsentTextEn { get; set; }
    public string? ConsentTextUr { get; set; }

    /// <summary>JSON array of <c>Consumable</c>.</summary>
    public string ConsumablesJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
}

/// <summary>FR-PRC-01/02: a procedure done (or planned) for a patient, with its note form and consent.</summary>
public class ProcedureRecord : SyncEntity, IHasDynamicData
{
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid ProcedureTypeId { get; set; }

    /// <summary>Snapshot of the type (the master may change later).</summary>
    public string ProcedureCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "other";
    public decimal? Fee { get; set; }
    public string ConsumablesJson { get; set; } = "[]";

    public Guid? EncounterId { get; set; }
    public Guid? CaseId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? DoctorId { get; set; }
    public Guid? PerformedBy { get; set; }

    public DateTime? PlannedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "planned";
    public string? CancelReason { get; set; }

    /// <summary>FR-PRC-02: copied from the type; the server re-derives it.</summary>
    public bool ConsentRequired { get; set; }
    public DateTime? ConsentSignedAt { get; set; }
    public string? ConsentSignerName { get; set; }
    public string? ConsentRelation { get; set; }
    public string? ConsentWitness { get; set; }

    /// <summary>The signature image (an Attachment of this procedure, category "consent").</summary>
    public Guid? ConsentAttachmentId { get; set; }

    public string? PatientGender { get; set; }
    public DateTime? PatientDateOfBirth { get; set; }

    public Guid? FormVersionId { get; set; }
    public string? DynamicData { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;

    public IReadOnlyDictionary<string, object?> GetFormVariables() =>
        new Dictionary<string, object?>(Clinic.Shared.Scheduling.PatientRules.FormVariables(PatientGender, PatientDateOfBirth, DateTime.Today))
        {
            ["procedure"] = ProcedureCode,
        };
}

/// <summary>FR-PRC-03/04: bed, room or chair of a branch (master, server wins).</summary>
public class Bed : SyncEntity
{
    public Guid BranchId { get; set; }
    public string Code { get; set; } = "";
    public string? Ward { get; set; }
    public string Kind { get; set; } = "bed";
    public decimal ChargeAmount { get; set; }
    public string ChargeUnit { get; set; } = "day";
    public bool IsActive { get; set; } = true;

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-PRC-03: a day-care / short-stay admission.</summary>
public class Admission : SyncEntity
{
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>The current bed; earlier beds are in <see cref="BedTransfer"/> rows.</summary>
    public Guid BedId { get; set; }
    public Guid? CaseId { get; set; }
    public Guid? DoctorId { get; set; }
    public DateTime AdmittedAt { get; set; }
    public Guid? AdmittedBy { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = "admitted";
    public DateTime? DischargedAt { get; set; }
    public Guid? DischargedBy { get; set; }
    public string? DischargeSummary { get; set; }
    public string? DischargeAdvice { get; set; }

    /// <summary>Set by the server when two devices put different patients in the same bed while offline.</summary>
    public bool BedConflict { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-PRC-03: move to another bed (keeps the bed history for room charges).</summary>
public class BedTransfer : SyncEntity
{
    public Guid AdmissionId { get; set; }
    public Guid BranchId { get; set; }
    public Guid FromBedId { get; set; }
    public Guid ToBedId { get; set; }
    public DateTime MovedAt { get; set; }
    public Guid? MovedBy { get; set; }
    public string? Reason { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

public class NursingNote : SyncEntity
{
    public Guid AdmissionId { get; set; }
    public Guid BranchId { get; set; }
    public string Kind { get; set; } = "note";
    public string Body { get; set; } = "";
    public DateTime WrittenAt { get; set; }
    public Guid? WrittenBy { get; set; }
    public string? AuthorName { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-PRC-03 medication chart: what to give during the stay.</summary>
public class MedicationOrder : SyncEntity
{
    public Guid AdmissionId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? DrugId { get; set; }
    public string DrugName { get; set; } = "";
    public string? Dose { get; set; }
    public string? Route { get; set; }
    public string? Frequency { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime? StopAt { get; set; }
    public string Status { get; set; } = "active";
    public string? Instructions { get; set; }
    public Guid? OrderedBy { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-PRC-03 medication chart: one administration (given, held or refused).</summary>
public class MedicationDose : SyncEntity
{
    public Guid OrderId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid BranchId { get; set; }
    public DateTime GivenAt { get; set; }
    public string Status { get; set; } = "given";
    public string? Note { get; set; }
    public Guid? GivenBy { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

using System.Text.Json.Serialization;
using SQLite;

namespace Clinic.Client.Core.Local;

// §4.10 cases ---------------------------------------------------------------------------------------

[Table("CaseTypes")]
public sealed class LocalCaseType : LocalEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameUr { get; set; }
    public string? Specialty { get; set; }
    public int? ExpectedSessions { get; set; }
    public int? FollowUpIntervalDays { get; set; }
    public string? DefaultServices { get; set; }
    public string DefaultPlanJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
}

[Table("Cases")]
public sealed class LocalCase : LocalEntity, ILocalDynamic
{
    [Indexed]
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid CaseTypeId { get; set; }
    public string CaseTypeCode { get; set; } = "";
    public string Title { get; set; } = "";
    [Indexed]
    public Guid? PrimaryDoctorId { get; set; }
    public DateTime OpenedAt { get; set; }
    public string Status { get; set; } = "open";
    public int? PlannedSessions { get; set; }
    public int? FollowUpIntervalDays { get; set; }
    public DateTime? ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }
    public string? Outcome { get; set; }
    public string? ClosingSummary { get; set; }
    public string? PatientGender { get; set; }
    public DateTime? PatientDateOfBirth { get; set; }
    public Guid? FormVersionId { get; set; }

    [JsonIgnore, LocalOnly]
    public string? DynamicData { get; set; }
}

[Table("CarePlanSteps")]
public sealed class LocalCarePlanStep : LocalEntity
{
    [Indexed]
    public Guid CaseId { get; set; }
    public Guid BranchId { get; set; }
    public int SortOrder { get; set; }
    public string Kind { get; set; } = "visit";
    public string Title { get; set; } = "";
    public DateTime? DueDate { get; set; }
    public string Status { get; set; } = "planned";
    public DateTime? CompletedAt { get; set; }
    public Guid? CompletedBy { get; set; }
    public Guid? LinkedRecordId { get; set; }
    public string? Notes { get; set; }
}

[Table("CaseMembers")]
public sealed class LocalCaseMember : LocalEntity
{
    [Indexed]
    public Guid CaseId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? DoctorId { get; set; }
    public Guid? UserId { get; set; }
    public string Name { get; set; } = "";
    public string Role { get; set; } = "consulting";
}

[Table("CaseNotes")]
public sealed class LocalCaseNote : LocalEntity
{
    [Indexed]
    public Guid CaseId { get; set; }
    public Guid BranchId { get; set; }
    public string Body { get; set; } = "";
    public DateTime WrittenAt { get; set; }
    public Guid? WrittenBy { get; set; }
    public string? AuthorName { get; set; }
}

// §4.11 referrals -----------------------------------------------------------------------------------

[Table("Referrals")]
public sealed class LocalReferral : LocalEntity
{
    [Indexed]
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public string Direction { get; set; } = "internal";
    public Guid? EncounterId { get; set; }
    public Guid? CaseId { get; set; }
    public Guid? FromDoctorId { get; set; }
    [Indexed]
    public Guid? ToDoctorId { get; set; }
    public string? ToSpecialty { get; set; }
    public string? ExternalFacility { get; set; }
    public string? ExternalDoctor { get; set; }
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
    public string? Summary { get; set; }
}

// §4.12 procedures and day care ---------------------------------------------------------------------

[Table("ProcedureTypes")]
public sealed class LocalProcedureType : LocalEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "other";
    public decimal? Fee { get; set; }
    public int DurationMinutes { get; set; } = 15;
    public bool RequiresConsent { get; set; }
    public string? ConsentTextEn { get; set; }
    public string? ConsentTextUr { get; set; }
    public string ConsumablesJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
}

[Table("Procedures")]
public sealed class LocalProcedure : LocalEntity, ILocalDynamic
{
    [Indexed]
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid ProcedureTypeId { get; set; }
    public string ProcedureCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "other";
    public decimal? Fee { get; set; }
    public string ConsumablesJson { get; set; } = "[]";
    public Guid? EncounterId { get; set; }
    [Indexed]
    public Guid? CaseId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? DoctorId { get; set; }
    public Guid? PerformedBy { get; set; }
    public DateTime? PlannedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "planned";
    public string? CancelReason { get; set; }
    public bool ConsentRequired { get; set; }
    public DateTime? ConsentSignedAt { get; set; }
    public string? ConsentSignerName { get; set; }
    public string? ConsentRelation { get; set; }
    public string? ConsentWitness { get; set; }
    public Guid? ConsentAttachmentId { get; set; }
    public string? PatientGender { get; set; }
    public DateTime? PatientDateOfBirth { get; set; }
    public Guid? FormVersionId { get; set; }

    [JsonIgnore, LocalOnly]
    public string? DynamicData { get; set; }
}

[Table("Beds")]
public sealed class LocalBed : LocalEntity
{
    [Indexed]
    public Guid BranchId { get; set; }
    public string Code { get; set; } = "";
    public string? Ward { get; set; }
    public string Kind { get; set; } = "bed";
    public decimal ChargeAmount { get; set; }
    public string ChargeUnit { get; set; } = "day";
    public bool IsActive { get; set; } = true;
}

[Table("Admissions")]
public sealed class LocalAdmission : LocalEntity
{
    [Indexed]
    public Guid PatientId { get; set; }
    [Indexed]
    public Guid BranchId { get; set; }
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

    /// <summary>Server-maintained: another admission was in this bed first (offline double admission).</summary>
    public bool BedConflict { get; set; }
}

[Table("BedTransfers")]
public sealed class LocalBedTransfer : LocalEntity
{
    [Indexed]
    public Guid AdmissionId { get; set; }
    public Guid BranchId { get; set; }
    public Guid FromBedId { get; set; }
    public Guid ToBedId { get; set; }
    public DateTime MovedAt { get; set; }
    public Guid? MovedBy { get; set; }
    public string? Reason { get; set; }
}

[Table("NursingNotes")]
public sealed class LocalNursingNote : LocalEntity
{
    [Indexed]
    public Guid AdmissionId { get; set; }
    public Guid BranchId { get; set; }
    public string Kind { get; set; } = "note";
    public string Body { get; set; } = "";
    public DateTime WrittenAt { get; set; }
    public Guid? WrittenBy { get; set; }
    public string? AuthorName { get; set; }
}

[Table("MedicationOrders")]
public sealed class LocalMedicationOrder : LocalEntity
{
    [Indexed]
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
}

[Table("MedicationDoses")]
public sealed class LocalMedicationDose : LocalEntity
{
    [Indexed]
    public Guid OrderId { get; set; }
    [Indexed]
    public Guid AdmissionId { get; set; }
    public Guid BranchId { get; set; }
    public DateTime GivenAt { get; set; }
    public string Status { get; set; } = "given";
    public string? Note { get; set; }
    public Guid? GivenBy { get; set; }
}

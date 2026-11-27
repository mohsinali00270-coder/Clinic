using System.Text.Json.Serialization;
using SQLite;

namespace Clinic.Client.Core.Local;

[Table("Encounters")]
public sealed class LocalEncounter : LocalEntity, ILocalDynamic
{
    [Indexed]
    public Guid PatientId { get; set; }
    [Indexed]
    public Guid DoctorId { get; set; }
    public Guid BranchId { get; set; }
    [Indexed]
    public Guid? AppointmentId { get; set; }
    [Indexed]
    public Guid? CaseId { get; set; }
    public string Specialty { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string Status { get; set; } = "draft";
    public DateTime? SignedAt { get; set; }
    public Guid? SignedBy { get; set; }
    public DateTime? FollowUpDate { get; set; }
    public Guid? FormVersionId { get; set; }
    public string? PatientGender { get; set; }
    public DateTime? PatientDateOfBirth { get; set; }

    [JsonIgnore, LocalOnly]
    public string? DynamicData { get; set; }
}

[Table("Vitals")]
public sealed class LocalVitals : LocalEntity, ILocalDynamic
{
    [Indexed]
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid BranchId { get; set; }
    public DateTime RecordedAt { get; set; }
    public Guid? RecordedBy { get; set; }
    public string? PatientGender { get; set; }
    public DateTime? PatientDateOfBirth { get; set; }
    public Guid? FormVersionId { get; set; }

    [JsonIgnore, LocalOnly]
    public string? DynamicData { get; set; }
}

[Table("Diagnoses")]
public sealed class LocalDiagnosis : LocalEntity
{
    [Indexed]
    public Guid EncounterId { get; set; }
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public string? Code { get; set; }
    public string Description { get; set; } = "";
    public bool IsPrimary { get; set; }
    public string? Notes { get; set; }
}

[Table("Icd10Codes")]
public sealed class LocalIcd10 : LocalEntity
{
    [Indexed]
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsCommon { get; set; }
}

[Table("EncounterAmendments")]
public sealed class LocalAmendment : LocalEntity
{
    [Indexed]
    public Guid EncounterId { get; set; }
    public Guid BranchId { get; set; }
    public string FieldKey { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Reason { get; set; } = "";
    public DateTime AmendedAt { get; set; }
    public Guid? AmendedBy { get; set; }
}

[Table("Drugs")]
public sealed class LocalDrug : LocalEntity
{
    public string Name { get; set; } = "";
    public string? Generic { get; set; }
    public string? Strength { get; set; }
    public string? Form { get; set; }
    public string? Route { get; set; }
    public string? DefaultDose { get; set; }
    public string? DefaultFrequency { get; set; }
    public int? DefaultDurationDays { get; set; }
    public string? AllergyClass { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("Prescriptions")]
public sealed class LocalPrescription : LocalEntity
{
    [Indexed]
    public Guid EncounterId { get; set; }
    [Indexed]
    public Guid PatientId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid BranchId { get; set; }
    public string? Notes { get; set; }
    public DateTime IssuedAt { get; set; }
}

[Table("PrescriptionItems")]
public sealed class LocalPrescriptionItem : LocalEntity
{
    [Indexed]
    public Guid PrescriptionId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? DrugId { get; set; }
    public string DrugName { get; set; } = "";
    public string? Strength { get; set; }
    public string? Form { get; set; }
    public string? Dose { get; set; }
    public string? Frequency { get; set; }
    public int? DurationDays { get; set; }
    public string? Route { get; set; }
    public string? Instructions { get; set; }
    public int SortOrder { get; set; }
}

[Table("PrescriptionTemplates")]
public sealed class LocalPrescriptionTemplate : LocalEntity
{
    [Indexed]
    public Guid DoctorId { get; set; }
    public string Name { get; set; } = "";
    public string ItemsJson { get; set; } = "[]";
}

using Clinic.Domain.Forms;
using Clinic.Domain.Sync;

namespace Clinic.Domain.Clinical;

/// <summary>
/// FR-ENC-01/02: a consultation. Its notes are the specialty's dynamic form (FieldValues), so the core only holds
/// what logic and sync need. Signing locks it; later changes go through <see cref="EncounterAmendment"/>.
/// </summary>
public class Encounter : SyncEntity, IHasDynamicData
{
    public Guid PatientId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? AppointmentId { get; set; }

    /// <summary>Specialty code used to pick the form (e.g. "dental").</summary>
    public string Specialty { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string Status { get; set; } = "draft";
    public DateTime? SignedAt { get; set; }
    public Guid? SignedBy { get; set; }

    /// <summary>FR-ENC-06: a follow-up date suggests an appointment.</summary>
    public DateTime? FollowUpDate { get; set; }

    public Guid? FormVersionId { get; set; }
    public string? DynamicData { get; set; }

    /// <summary>Copied from the patient when the encounter starts, for form conditions (gender/age).</summary>
    public string? PatientGender { get; set; }
    public DateTime? PatientDateOfBirth { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;

    public IReadOnlyDictionary<string, object?> GetFormVariables()
    {
        var vars = new Dictionary<string, object?>(Clinic.Shared.Scheduling.PatientRules.FormVariables(PatientGender, PatientDateOfBirth, DateTime.Today))
        {
            ["specialty"] = Specialty,
        };
        return vars;
    }
}

/// <summary>FR-ENC-03: a vitals record (Vitals dynamic form), usually taken by the nurse before the consultation.</summary>
public class VitalsRecord : SyncEntity, IHasDynamicData
{
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid BranchId { get; set; }
    public DateTime RecordedAt { get; set; }
    public Guid? RecordedBy { get; set; }

    public string? PatientGender { get; set; }
    public DateTime? PatientDateOfBirth { get; set; }

    public Guid? FormVersionId { get; set; }
    public string? DynamicData { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;

    public IReadOnlyDictionary<string, object?> GetFormVariables() =>
        Clinic.Shared.Scheduling.PatientRules.FormVariables(PatientGender, PatientDateOfBirth, DateTime.Today);
}

/// <summary>FR-ENC-04: ICD-10 coded (or free-text) diagnosis of an encounter.</summary>
public class Diagnosis : SyncEntity
{
    public Guid EncounterId { get; set; }
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public string? Code { get; set; }
    public string Description { get; set; } = "";
    public bool IsPrimary { get; set; }
    public string? Notes { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>ICD-10 code list (cached offline, FR-OFF-24).</summary>
public class Icd10Code : SyncEntity
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsCommon { get; set; }
}

/// <summary>FR-ENC-05: a change to a signed encounter, with reason, user and time.</summary>
public class EncounterAmendment : SyncEntity
{
    public Guid EncounterId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>Dynamic field key, or "diagnoses" / "prescription" / "followUpDate".</summary>
    public string FieldKey { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Reason { get; set; } = "";
    public DateTime AmendedAt { get; set; }
    public Guid? AmendedBy { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-RX-01: tenant drug master (cached offline).</summary>
public class Drug : SyncEntity
{
    public string Name { get; set; } = "";
    public string? Generic { get; set; }
    public string? Strength { get; set; }
    public string? Form { get; set; }
    public string? Route { get; set; }
    public string? DefaultDose { get; set; }
    public string? DefaultFrequency { get; set; }
    public int? DefaultDurationDays { get; set; }

    /// <summary>Links to patient allergy codes for warnings (e.g. "penicillin").</summary>
    public string? AllergyClass { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Prescription : SyncEntity
{
    public Guid EncounterId { get; set; }
    public Guid PatientId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid BranchId { get; set; }
    public string? Notes { get; set; }
    public DateTime IssuedAt { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

public class PrescriptionItem : SyncEntity
{
    public Guid PrescriptionId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? DrugId { get; set; }

    /// <summary>Snapshot of the drug as prescribed (the master may change later).</summary>
    public string DrugName { get; set; } = "";
    public string? Strength { get; set; }
    public string? Form { get; set; }
    public string? Dose { get; set; }
    public string? Frequency { get; set; }
    public int? DurationDays { get; set; }
    public string? Route { get; set; }
    public string? Instructions { get; set; }
    public int SortOrder { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-RX-02: a doctor's favourite prescription, applied in one tap.</summary>
public class PrescriptionTemplate : SyncEntity
{
    public Guid DoctorId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>JSON array of items (drugId, drugName, strength, dose, frequency, durationDays, route, instructions).</summary>
    public string ItemsJson { get; set; } = "[]";
}

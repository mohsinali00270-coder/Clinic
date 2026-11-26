namespace Clinic.Shared.Forms;

/// <summary>A value the record provides to expressions besides its own fields (e.g. the patient's gender).</summary>
public sealed record ContextVariable(string Name, string Description, FieldType Type);

/// <summary>§5.1 entity type: a record kind a form can attach to.</summary>
public sealed record DynamicEntityType(
    string Name,
    string DisplayName,
    IReadOnlyList<ContextVariable> Variables,
    string AvailableFrom);

public static class DynamicEntityTypes
{
    private static readonly ContextVariable Gender = new("gender", "Patient gender code: M, F or O", FieldType.SingleSelect);
    private static readonly ContextVariable AgeYears = new("ageYears", "Patient age in whole years", FieldType.Integer);
    private static readonly ContextVariable AgeMonths = new("ageMonths", "Patient age in whole months", FieldType.Integer);
    private static readonly ContextVariable Specialty = new("specialty", "Specialty code of the encounter", FieldType.Text);

    public static IReadOnlyList<DynamicEntityType> All { get; } =
    [
        new("BranchNote", "Handover note", [], "Phase 3"),
        new("Patient", "Patient registration", [Gender, AgeYears, AgeMonths], "Phase 4"),
        new("Appointment", "Appointment / booking form", [Gender, AgeYears], "Phase 4"),
        new("Doctor", "Doctor profile", [], "Phase 4"),
        new("Encounter", "Consultation (encounter)", [Gender, AgeYears, AgeMonths, Specialty], "Phase 5"),
        new("Vitals", "Vitals", [Gender, AgeYears, AgeMonths], "Phase 5"),
        new("Case", "Patient case (episode of care)", [Gender, AgeYears], "Phase 5"),
        new("Procedure", "Procedure note", [Gender, AgeYears], "Phase 5"),
        new("LabTest", "Lab result parameters", [Gender, AgeYears], "Phase 6"),
        new("Invoice", "Invoice", [], "Phase 6"),
        new("InventoryItem", "Inventory item", [], "Phase 7"),
        new("Feedback", "Patient feedback", [], "Phase 6"),
    ];

    public static DynamicEntityType? Find(string name) =>
        All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Names every expression may use regardless of entity type.</summary>
    public static readonly IReadOnlySet<string> BuiltInIdentifiers = new HashSet<string> { "true", "false", "null" };
}

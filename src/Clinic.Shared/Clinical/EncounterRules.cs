namespace Clinic.Shared.Clinical;

/// <summary>FR-ENC-01: Draft → In progress → Signed → (Amended).</summary>
public static class EncounterStatus
{
    public const string Draft = "draft";
    public const string InProgress = "in_progress";
    public const string Signed = "signed";

    /// <summary>Signed, then changed through amendments (still locked).</summary>
    public const string Amended = "amended";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Draft, InProgress, Signed, Amended };
    public static bool IsLocked(string status) => status is Signed or Amended;

    public static string Display(string status) => status switch
    {
        InProgress => "In progress",
        _ => char.ToUpperInvariant(status[0]) + status[1..],
    };
}

/// <summary>Dose frequency codes with English/Urdu wording for prescriptions (FR-RX-01/04).</summary>
public sealed record Frequency(string Code, string English, string Urdu, double PerDay);

public static class Frequencies
{
    public static readonly IReadOnlyList<Frequency> All =
    [
        new("od", "Once a day", "دن میں ایک بار", 1),
        new("bd", "Twice a day", "دن میں دو بار", 2),
        new("tds", "Three times a day", "دن میں تین بار", 3),
        new("qid", "Four times a day", "دن میں چار بار", 4),
        new("hs", "At bedtime", "رات سونے سے پہلے", 1),
        new("ac_bd", "Twice a day before meals", "دن میں دو بار کھانے سے پہلے", 2),
        new("pc_tds", "Three times a day after meals", "دن میں تین بار کھانے کے بعد", 3),
        new("q8h", "Every 8 hours", "ہر آٹھ گھنٹے بعد", 3),
        new("weekly", "Once a week", "ہفتے میں ایک بار", 1.0 / 7),
        new("sos", "When needed", "ضرورت کے وقت", 0),
        new("stat", "Once, immediately", "فوری طور پر ایک بار", 0),
    ];

    public static Frequency? Find(string? code) => All.FirstOrDefault(f => f.Code == code);
}

/// <summary>Administration routes for prescription items.</summary>
public static class DrugRoutes
{
    public static readonly IReadOnlyList<string> All = ["oral", "topical", "inhaled", "iv", "im", "sc", "eye", "ear", "nasal", "rectal", "sublingual"];
}

/// <summary>
/// Allergy classes linking the Patient form's "allergies" codes to drugs (FR-RX-03). A drug in class X warns
/// when the patient's allergies contain X.
/// </summary>
public static class AllergyClasses
{
    public const string Penicillin = "penicillin";
    public const string Sulfa = "sulfa";
    public const string Nsaids = "nsaids";
    public const string Latex = "latex";
}

public sealed record DrugWarning(string Kind, string Message, bool Critical);

/// <summary>
/// The exact text recorded in an amendment for a diagnosis or prescription line. Device and server build it the
/// same way, so the server can check that a change to a signed encounter matches its amendment.
/// </summary>
public static class AmendmentText
{
    public static string Diagnosis(string? code, string description) => $"{code} {description}".Trim();

    public static string PrescriptionItem(string drugName, string? strength, string? dose, string? frequency, int? days) =>
        $"{drugName} {strength} · {dose} · {Frequencies.Find(frequency)?.English ?? frequency} · {days} days".Trim();
}

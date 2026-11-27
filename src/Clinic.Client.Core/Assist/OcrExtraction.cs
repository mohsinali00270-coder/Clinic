using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Clinic.Shared.Forms;

namespace Clinic.Client.Core.Assist;

/// <summary>
/// One value read from a scanned document, proposed for a field. It is a suggestion: the screen shows it with its
/// source line and a person ticks it before anything is saved (§13 "AI assists, never decides").
/// </summary>
public sealed record OcrProposal(string FieldKey, string Label, JsonNode Value, string SourceLine, double Confidence)
{
    public string Display => Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : Value.ToJsonString();
}

/// <summary>Identity details read from a CNIC / B-form / old prescription header.</summary>
public sealed record IdentityProposal(string? FullName, string? FatherOrHusbandName, string? Cnic, DateTime? DateOfBirth, string? Gender, string? Phone)
{
    public bool IsEmpty => FullName is null && Cnic is null && DateOfBirth is null && Gender is null && Phone is null;
}

/// <summary>
/// FR-AI-21, the part that does not need a model: turns the text the platform OCR engine (ML Kit, Apple Vision,
/// Windows.Media.Ocr) recognised into proposals for patient fields and for any dynamic form's numeric fields
/// (vitals today, lab result parameters when labs arrive in Phase 6). Pure and deterministic, so it is tested
/// without a camera and behaves the same on every platform.
/// </summary>
public static partial class OcrExtraction
{
    [GeneratedRegex(@"\b(\d{5})[-\s]?(\d{7})[-\s]?(\d)\b")]
    private static partial Regex CnicPattern();

    [GeneratedRegex(@"(?<!\d)(?:\+?92[-\s]?|0)3\d{2}[-\s]?\d{7}(?!\d)")]
    private static partial Regex MobilePattern();

    [GeneratedRegex(@"\b(\d{1,2})[./-](\d{1,2})[./-](\d{4})\b")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?<![\d.])(\d{2,3})\s*/\s*(\d{2,3})(?![\d.])")]
    private static partial Regex BloodPressurePattern();

    [GeneratedRegex(@"-?\d+(?:[.,]\d+)?")]
    private static partial Regex NumberPattern();

    /// <summary>Extra words a document may use for a field, besides its label and key (matched case-insensitively).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Synonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["pulse"] = ["pulse", "heart rate", "hr", "p/m"],
        ["temperature"] = ["temperature", "temp", "t°"],
        ["spo2"] = ["spo2", "sp02", "o2 sat", "oxygen saturation", "saturation"],
        ["respiratory_rate"] = ["respiratory rate", "resp rate", "rr"],
        ["weight_kg"] = ["weight", "wt"],
        ["height_cm"] = ["height", "ht"],
        ["hb"] = ["hb", "haemoglobin", "hemoglobin", "hgb"],
        ["hba1c"] = ["hba1c", "glycated", "a1c"],
        ["fbs"] = ["fbs", "fasting blood sugar", "fasting glucose"],
        ["rbs"] = ["rbs", "random blood sugar", "random glucose"],
        ["wbc"] = ["wbc", "tlc", "white cell", "total leucocyte"],
        ["platelets"] = ["platelet", "plt"],
        ["creatinine"] = ["creatinine", "s. creatinine", "serum creatinine"],
        ["tsh"] = ["tsh"],
    };

    // ------------------------------------------------------------------ identity

    public static IdentityProposal ExtractIdentity(string text)
    {
        var lines = Lines(text);
        string? cnic = null, phone = null, gender = null, name = null, guardian = null;
        DateTime? dob = null;

        if (CnicPattern().Match(text) is { Success: true } m) cnic = $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}";
        if (MobilePattern().Match(text) is { Success: true } ph) phone = NormaliseMobile(ph.Value);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var lower = line.ToLowerInvariant();
            if (name is null && LabelValue(lines, i, ["name", "patient name", "patient"]) is { } n && !n.Any(char.IsDigit)) name = Title(n);
            if (guardian is null && LabelValue(lines, i, ["father name", "father's name", "husband name", "s/o", "d/o", "w/o"]) is { } g) guardian = Title(g);
            if (dob is null && (lower.Contains("birth") || lower.Contains("dob")) && FirstDate(line + " " + (i + 1 < lines.Count ? lines[i + 1] : "")) is { } d) dob = d;
            if (gender is null && (lower.Contains("gender") || lower.Contains("sex")))
                gender = Regex.IsMatch(lower, @"\b(f|female)\b") ? "F" : Regex.IsMatch(lower, @"\b(m|male)\b") ? "M" : null;
        }
        // On a CNIC the 13th digit is odd for men and even for women.
        if (gender is null && cnic is not null) gender = (cnic[^1] - '0') % 2 == 1 ? "M" : "F";
        return new IdentityProposal(name, guardian, cnic, dob, gender, phone);
    }

    // ------------------------------------------------------------------ form fields

    /// <summary>
    /// Proposes values for the form's fields: numbers next to a field's label (or a synonym), blood pressure as
    /// "120/80", CNIC / phone / date fields by pattern. Values outside the field's min–max are dropped.
    /// </summary>
    public static IReadOnlyList<OcrProposal> ProposeForForm(string text, FormDefinition form)
    {
        var lines = Lines(text);
        var proposals = new List<OcrProposal>();
        var fields = form.AllFields.Where(f => f.HoldsValue && f.Type != FieldType.Calculated).ToList();

        // Blood pressure is written as one reading.
        var sys = fields.FirstOrDefault(f => f.Key.Contains("systolic", StringComparison.OrdinalIgnoreCase));
        var dia = fields.FirstOrDefault(f => f.Key.Contains("diastolic", StringComparison.OrdinalIgnoreCase));
        if (sys is not null && dia is not null)
        {
            foreach (var line in lines)
            {
                if (!Regex.IsMatch(line, @"\b(bp|b\.p|blood pressure)\b", RegexOptions.IgnoreCase) && lines.Count > 1) continue;
                if (BloodPressurePattern().Match(line) is not { Success: true } bp) continue;
                var s = decimal.Parse(bp.Groups[1].Value, CultureInfo.InvariantCulture);
                var d = decimal.Parse(bp.Groups[2].Value, CultureInfo.InvariantCulture);
                if (InRange(sys, s) && InRange(dia, d) && s > d)
                {
                    proposals.Add(new OcrProposal(sys.Key, sys.Label.En, JsonValue.Create(s), line, 0.9));
                    proposals.Add(new OcrProposal(dia.Key, dia.Label.En, JsonValue.Create(d), line, 0.9));
                }
                break;
            }
        }

        foreach (var f in fields)
        {
            if (proposals.Any(p => p.FieldKey == f.Key)) continue;
            switch (f.Type)
            {
                case FieldType.IdNumber when CnicPattern().Match(text) is { Success: true } m:
                    proposals.Add(new OcrProposal(f.Key, f.Label.En, JsonValue.Create($"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}"), Line(lines, m.Value), 0.95));
                    break;
                case FieldType.Phone when MobilePattern().Match(text) is { Success: true } ph:
                    proposals.Add(new OcrProposal(f.Key, f.Label.En, JsonValue.Create(NormaliseMobile(ph.Value)), Line(lines, ph.Value), 0.85));
                    break;
                case FieldType.Integer or FieldType.Decimal when NumberNearLabel(lines, f) is { } hit:
                    proposals.Add(new OcrProposal(f.Key, f.Label.En,
                        f.Type == FieldType.Integer ? JsonValue.Create(Math.Round(hit.Value)) : JsonValue.Create(hit.Value), hit.Line, hit.Confidence));
                    break;
                case FieldType.Date when Terms(f).Any(t => lines.Any(l => Contains(l, t))):
                    var source = lines.First(l => Terms(f).Any(t => Contains(l, t)));
                    if (FirstDate(source) is { } date)
                        proposals.Add(new OcrProposal(f.Key, f.Label.En, JsonValue.Create(date.ToString("yyyy-MM-dd")), source, 0.7));
                    break;
            }
        }
        return proposals;
    }

    /// <summary>Applies the proposals a person accepted onto a form's values (never the rejected ones).</summary>
    public static void Apply(JsonObject values, IEnumerable<OcrProposal> accepted)
    {
        foreach (var p in accepted) values[p.FieldKey] = p.Value.DeepClone();
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Hit(decimal Value, string Line, double Confidence);

    private static Hit? NumberNearLabel(IReadOnlyList<string> lines, FieldDefinition f)
    {
        var terms = Terms(f);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var term = terms.FirstOrDefault(t => Contains(line, t));
            if (term is null) continue;

            // The value is after the label on the same line ("Pulse: 88 /min"), else on the next line (table layouts).
            var after = line[(line.IndexOf(term, StringComparison.OrdinalIgnoreCase) + term.Length)..];
            foreach (var (candidate, confidence) in new[] { (after, 0.85), (i + 1 < lines.Count ? lines[i + 1] : "", 0.6) })
            {
                if (NumberPattern().Match(candidate) is not { Success: true } m) continue;
                if (!decimal.TryParse(m.Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) continue;
                if (!InRange(f, value)) continue;
                return new Hit(value, confidence > 0.7 ? line : $"{line} {candidate}".Trim(), confidence);
            }
        }
        return null;
    }

    private static List<string> Terms(FieldDefinition f)
    {
        var terms = new List<string> { f.Label.En, f.Key.Replace('_', ' ') };
        if (Synonyms.TryGetValue(f.Key, out var extra)) terms.AddRange(extra);
        return terms.Where(t => t.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Length).ToList();
    }

    /// <summary>Whole-word match, so "ht" does not match inside "weight".</summary>
    private static bool Contains(string line, string term) =>
        Regex.IsMatch(line, $@"(?<![\p{{L}}\d]){Regex.Escape(term)}(?![\p{{L}}])", RegexOptions.IgnoreCase);

    private static bool InRange(FieldDefinition f, decimal v) => (f.Min is null || v >= f.Min) && (f.Max is null || v <= f.Max);

    private static string? LabelValue(IReadOnlyList<string> lines, int i, string[] labels)
    {
        var line = lines[i];
        foreach (var label in labels.OrderByDescending(l => l.Length))
        {
            var m = Regex.Match(line, $@"^\s*{Regex.Escape(label)}\s*[:\-]?\s*(.*)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var value = m.Groups[1].Value.Trim();
            if (value.Length == 0 && i + 1 < lines.Count) value = lines[i + 1].Trim();
            return value.Length is > 1 and < 80 ? value : null;
        }
        return null;
    }

    private static DateTime? FirstDate(string text)
    {
        foreach (Match m in DatePattern().Matches(text))
        {
            var (d, mo, y) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            if (y is < 1900 or > 2100 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) continue;
            return new DateTime(y, mo, d); // Pakistani documents write day first
        }
        return null;
    }

    private static string NormaliseMobile(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("92")) digits = "0" + digits[2..];
        return digits.Length == 11 ? $"{digits[..4]}-{digits[4..]}" : digits;
    }

    private static string Line(IReadOnlyList<string> lines, string fragment) =>
        lines.FirstOrDefault(l => l.Contains(fragment, StringComparison.Ordinal)) ?? fragment;

    private static string Title(string s) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Trim().ToLowerInvariant());

    private static List<string> Lines(string text) =>
        text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}

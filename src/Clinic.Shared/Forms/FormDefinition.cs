using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Clinic.Shared.Forms;

/// <summary>§5.2 supported field types.</summary>
public enum FieldType
{
    Text,
    MultilineText,
    RichNote,
    Integer,
    Decimal,
    Date,
    Time,
    DateTime,
    YesNo,
    SingleSelect,
    MultiSelect,
    Lookup,
    Phone,
    Email,
    IdNumber,
    Calculated,
    File,
    Image,
    Signature,
    BodyDiagram,
    RepeatingGroup,
    Label,
}

/// <summary>English text with an optional Urdu translation (FR-DYN-01).</summary>
public sealed class LocalizedText
{
    public string En { get; set; } = "";
    public string? Ur { get; set; }

    public LocalizedText() { }
    public LocalizedText(string en, string? ur = null) { En = en; Ur = ur; }

    public string Get(string? language) =>
        language == "ur" && !string.IsNullOrWhiteSpace(Ur) ? Ur! : En;

    public override string ToString() => En;
}

public sealed class FieldOption
{
    public string Code { get; set; } = "";
    public LocalizedText Label { get; set; } = new();

    public FieldOption() { }
    public FieldOption(string code, string en, string? ur = null) { Code = code; Label = new LocalizedText(en, ur); }
}

/// <summary>FR-DYN-05: e.g. <c>value &lt; 94</c> → critical (red).</summary>
public sealed class AbnormalRule
{
    /// <summary>Expression; <c>value</c> is this field's value.</summary>
    public string When { get; set; } = "";

    /// <summary>"warning" (amber) or "critical" (red).</summary>
    public string Severity { get; set; } = "warning";
    public LocalizedText? Message { get; set; }
}

/// <summary>FR-AUTH-03 / FR-DYN-06: role names that may see / edit the field. Empty = everyone.</summary>
public sealed class FieldPermissions
{
    public List<string> ViewRoles { get; set; } = [];
    public List<string> EditRoles { get; set; } = [];
}

public sealed class FieldDefinition
{
    /// <summary>Immutable key used in storage, API and reports (lower_snake_case).</summary>
    public string Key { get; set; } = "";
    public FieldType Type { get; set; }
    public LocalizedText Label { get; set; } = new();
    public LocalizedText? Placeholder { get; set; }
    public LocalizedText? Help { get; set; }

    /// <summary>Literal default for new records, or an expression when the string starts with "=" (e.g. "=today()").</summary>
    public JsonNode? Default { get; set; }

    /// <summary>Width in a 4-column grid on tablet/desktop (1–4). Phones always use one column.</summary>
    public int Columns { get; set; } = 1;

    // ---- Validation (FR-DYN-02)
    public bool Required { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public string? Pattern { get; set; }
    public LocalizedText? PatternMessage { get; set; }

    /// <summary>ID mask: # digit, A letter, * any; other characters literal. E.g. "#####-#######-#".</summary>
    public string? Mask { get; set; }
    public bool NotInFuture { get; set; }

    /// <summary>Unique within the tenant for this entity type (checked on device and server).</summary>
    public bool Unique { get; set; }
    public List<string>? AllowedFileTypes { get; set; }
    public int? MaxFileSizeKb { get; set; }

    // ---- Logic (FR-DYN-03/04)
    public string? VisibleWhen { get; set; }
    public string? RequiredWhen { get; set; }
    public string? EnabledWhen { get; set; }

    /// <summary>Calculated fields only, e.g. <c>round(weight_kg / (height_cm/100)^2, 1)</c>.</summary>
    public string? Formula { get; set; }
    public List<AbnormalRule>? Abnormal { get; set; }

    // ---- Presentation
    public string? Unit { get; set; }
    public int? Decimals { get; set; }

    /// <summary>"dropdown" | "radio" for single select; "checkboxes" | "chips" for multi select.</summary>
    public string? Display { get; set; }
    public List<FieldOption>? Options { get; set; }

    /// <summary>Lookup list key (Lookup type).</summary>
    public string? LookupList { get; set; }

    /// <summary>Cascading lookup: items are filtered by this field's selected code (e.g. area by city).</summary>
    public string? LookupParentField { get; set; }

    // ---- Repeating group
    public List<FieldDefinition>? Fields { get; set; }
    public int? MinRows { get; set; }
    public int? MaxRows { get; set; }

    // ---- Access and flags (FR-DYN-06/07)
    public FieldPermissions? Permissions { get; set; }
    public bool Sensitive { get; set; }
    public bool Searchable { get; set; }
    public bool Reportable { get; set; }
    public bool ShowInList { get; set; }
    public bool PrintOnPrescription { get; set; }
    public bool CarryForward { get; set; }

    /// <summary>FR-DYN-12: kept for old records, hidden on new ones.</summary>
    public bool Retired { get; set; }

    [JsonIgnore]
    public bool HoldsValue => Type is not (FieldType.Label);

    [JsonIgnore]
    public bool IsNumeric => Type is FieldType.Integer or FieldType.Decimal;
}

public sealed class SectionDefinition
{
    public string Key { get; set; } = "";
    public LocalizedText Title { get; set; } = new();
    public bool Collapsible { get; set; }
    public bool StartCollapsed { get; set; }
    public string? VisibleWhen { get; set; }
    public List<FieldDefinition> Fields { get; set; } = [];
}

/// <summary>A form: sections and fields for one entity type. Published versions are immutable snapshots.</summary>
public sealed class FormDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string EntityType { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    public List<SectionDefinition> Sections { get; set; } = [];

    /// <summary>Top-level fields in display order (repeating group children are not included).</summary>
    [JsonIgnore]
    public IEnumerable<FieldDefinition> AllFields => Sections.SelectMany(s => s.Fields);

    public FieldDefinition? FindField(string key) =>
        AllFields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));

    public FormDefinition Clone() => FormJson.Deserialize(FormJson.Serialize(this));
}

public static class FormJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Serialize(FormDefinition definition, bool indented = false) =>
        JsonSerializer.Serialize(definition, indented ? Indented : Options);

    public static FormDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<FormDefinition>(json, Options)
        ?? throw new JsonException("The form definition is empty.");
}

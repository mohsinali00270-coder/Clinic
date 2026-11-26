using System.Text.Json.Nodes;
using Clinic.Client.Core.Local;
using Clinic.Shared.Forms;

namespace Clinic.Client.Core.Forms;

/// <summary>
/// UI-independent state of one filled-in form (§5.5): values, evaluation after every change, lookup options
/// (cascading), and whether errors should show yet. The MAUI DynamicFormView renders it; tests drive it directly.
/// </summary>
public sealed class DynamicFormModel
{
    private readonly Dictionary<string, List<LocalLookupItem>> _lookups;

    public DynamicFormModel(FormVersionInfo version, JsonObject values, EvaluationContext context,
        Dictionary<string, List<LocalLookupItem>>? lookups = null, bool isNewRecord = true)
    {
        Version = version;
        _lookups = lookups ?? new(StringComparer.Ordinal);
        Context = new EvaluationContext
        {
            Roles = context.Roles,
            Today = context.Today,
            Variables = context.Variables,
            Language = context.Language,
            IsNewRecord = isNewRecord,
            Lookups = (list, parent) => _lookups.TryGetValue(list, out var items)
                ? items.Select(i => new FieldOption(i.Code, i.LabelEn, i.LabelUr)).ToList()
                : null,
        };
        Values = (JsonObject)values.DeepClone();
        if (isNewRecord) version.Evaluator.ApplyDefaults(Values, Context);
        Evaluation = version.Evaluator.Evaluate(Values, Context);
    }

    public FormVersionInfo Version { get; }
    public FormDefinition Definition => Version.Definition;
    public EvaluationContext Context { get; private set; }
    public JsonObject Values { get; private set; }
    public FormEvaluation Evaluation { get; private set; }

    /// <summary>Errors stay hidden until the user touches a field or tries to save.</summary>
    public bool ShowAllErrors { get; private set; }
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);

    public string Language
    {
        get => Context.Language;
        set
        {
            Context = new EvaluationContext
            {
                Roles = Context.Roles, Today = Context.Today, Variables = Context.Variables,
                IsNewRecord = Context.IsNewRecord, Lookups = Context.Lookups, Language = value,
            };
            Reevaluate();
        }
    }

    /// <summary>Raised after every re-evaluation; the view refreshes visibility, errors, calculations.</summary>
    public event EventHandler? Evaluated;

    public bool IsDirty { get; private set; }

    public JsonNode? GetValue(string key) => Evaluation.Values[key];

    public JsonNode? GetRowValue(string group, int row, string key) =>
        (Evaluation.Values[group] as JsonArray)?.ElementAtOrDefault(row)?[key];

    public int RowCount(string group) => (Values[group] as JsonArray)?.Count ?? 0;

    public void SetValue(string key, JsonNode? value)
    {
        Values[key] = value?.DeepClone();
        // Cascading lookups: clear children whose parent changed.
        foreach (var child in Definition.AllFields.Where(f => f.LookupParentField == key))
            Values[child.Key] = null;
        _touched.Add(key);
        IsDirty = true;
        Reevaluate();
    }

    public void SetRowValue(string group, int row, string key, JsonNode? value)
    {
        if (Values[group] is not JsonArray rows || row >= rows.Count || rows[row] is not JsonObject obj) return;
        obj[key] = value?.DeepClone();
        _touched.Add($"{group}[{row}].{key}");
        IsDirty = true;
        Reevaluate();
    }

    public void AddRow(string group)
    {
        if (Values[group] is not JsonArray rows) Values[group] = rows = [];
        rows.Add(new JsonObject());
        IsDirty = true;
        Reevaluate();
    }

    public void RemoveRow(string group, int row)
    {
        if (Values[group] is JsonArray rows && row < rows.Count) rows.RemoveAt(row);
        IsDirty = true;
        Reevaluate();
    }

    /// <summary>Shows every error and returns whether the form can be saved.</summary>
    public bool Validate()
    {
        ShowAllErrors = true;
        Reevaluate();
        return Evaluation.IsValid;
    }

    public FieldState State(string path) => Evaluation.State(path);

    /// <summary>Errors to display for a field (only once touched or after a save attempt).</summary>
    public IReadOnlyList<string> VisibleErrors(string path) =>
        ShowAllErrors || _touched.Contains(path) ? Evaluation.State(path).Errors : [];

    /// <summary>Options for a select or lookup field; lookups are filtered by their parent field's value.</summary>
    public IReadOnlyList<FieldOption> OptionsFor(FieldDefinition field, int? row = null, string? group = null)
    {
        if (field.Type is FieldType.SingleSelect or FieldType.MultiSelect) return field.Options ?? [];
        if (field.Type != FieldType.Lookup || field.LookupList is null || !_lookups.TryGetValue(field.LookupList, out var items)) return [];

        string? parentCode = null;
        if (field.LookupParentField is { } parentKey)
        {
            var parentNode = row is not null && group is not null ? GetRowValue(group, row.Value, parentKey) : GetValue(parentKey);
            parentCode = FormValues.AsString(parentNode);
            if (parentCode is null) return [];
        }
        return items
            .Where(i => parentCode is null || i.ParentCode == parentCode)
            .Select(i => new FieldOption(i.Code, i.LabelEn, i.LabelUr))
            .ToList();
    }

    /// <summary>Values to persist: the evaluated values, so calculated fields are stored too.</summary>
    public JsonObject ValuesToSave() => (JsonObject)Evaluation.Values.DeepClone();

    public void Reevaluate()
    {
        Evaluation = Version.Evaluator.Evaluate(Values, Context);
        Evaluated?.Invoke(this, EventArgs.Empty);
    }
}

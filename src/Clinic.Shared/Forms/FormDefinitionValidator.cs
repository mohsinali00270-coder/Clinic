using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Clinic.Shared.Forms.Expressions;

namespace Clinic.Shared.Forms;

/// <summary>One problem in a form definition. Path looks like "sections[1].fields[3].formula".</summary>
public sealed record DefinitionError(string Path, string Message);

/// <summary>
/// Checks a form definition before it is saved or published: keys, types, options, expressions (syntax and the
/// names they use), calculation loops, regex patterns and ranges. FR-DYN-12/FR-DYN-01 version rules are checked
/// by <see cref="CheckCompatibility"/>.
/// </summary>
public static partial class FormDefinitionValidator
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,49}$")]
    private static partial Regex KeyRegex();

    private static readonly HashSet<string> SingleDisplays = ["dropdown", "radio"];
    private static readonly HashSet<string> MultiDisplays = ["checkboxes", "chips"];

    public static List<DefinitionError> Validate(FormDefinition def, Func<string, bool>? lookupExists = null)
    {
        var errors = new List<DefinitionError>();
        void Add(string path, string message) => errors.Add(new DefinitionError(path, message));

        if (string.IsNullOrWhiteSpace(def.Name.En)) Add("name", "The form needs a name.");
        var entity = DynamicEntityTypes.Find(def.EntityType);
        if (entity is null) Add("entityType", $"Unknown entity type '{def.EntityType}'.");
        if (def.Sections.Count == 0) Add("sections", "Add at least one section.");

        var variables = entity?.Variables.Select(v => v.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var topKeys = new HashSet<string>(StringComparer.Ordinal);
        var sectionKeys = new HashSet<string>(StringComparer.Ordinal);
        var groupChildKeys = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: keys.
        for (var s = 0; s < def.Sections.Count; s++)
        {
            var section = def.Sections[s];
            var sp = $"sections[{s}]";
            if (!KeyRegex().IsMatch(section.Key ?? "")) Add($"{sp}.key", $"Section key '{section.Key}' must be lower_snake_case (a–z, 0–9, _), starting with a letter.");
            else if (!sectionKeys.Add(section.Key!)) Add($"{sp}.key", $"Section key '{section.Key}' is used twice.");
            if (string.IsNullOrWhiteSpace(section.Title.En)) Add($"{sp}.title", "Section needs a title.");

            for (var i = 0; i < section.Fields.Count; i++)
            {
                var f = section.Fields[i];
                if (!KeyRegex().IsMatch(f.Key ?? "")) Add($"{sp}.fields[{i}].key", $"Field key '{f.Key}' must be lower_snake_case (a–z, 0–9, _), starting with a letter, max 50 characters.");
                else if (variables.Contains(f.Key!)) Add($"{sp}.fields[{i}].key", $"'{f.Key}' is reserved for the {def.EntityType} context variable.");
                else if (!topKeys.Add(f.Key!)) Add($"{sp}.fields[{i}].key", $"Field key '{f.Key}' is used twice in this form.");

                if (f.Type == FieldType.RepeatingGroup)
                    foreach (var c in f.Fields ?? []) groupChildKeys.Add(c.Key);
            }
        }

        // Pass 2: field rules and expressions.
        for (var s = 0; s < def.Sections.Count; s++)
        {
            var section = def.Sections[s];
            var sp = $"sections[{s}]";
            CheckExpression(section.VisibleWhen, $"{sp}.visibleWhen", topKeys, variables, groupChildKeys, Add);

            for (var i = 0; i < section.Fields.Count; i++)
                CheckField(section.Fields[i], $"{sp}.fields[{i}]", topKeys, null, variables, groupChildKeys, lookupExists, Add);
        }

        // Pass 3: let the evaluator try (catches calculation loops and bad regexes).
        if (errors.Count == 0)
        {
            try { _ = new FormEvaluator(def); }
            catch (FormDefinitionException ex) { Add("", ex.Message); }
        }

        return errors;
    }

    private static void CheckField(FieldDefinition f, string p, HashSet<string> topKeys, HashSet<string>? rowKeys,
        HashSet<string> variables, HashSet<string> groupChildKeys, Func<string, bool>? lookupExists, Action<string, string> add)
    {
        if (!Enum.IsDefined(f.Type)) add($"{p}.type", "Unknown field type.");
        if (string.IsNullOrWhiteSpace(f.Label.En) && f.Type != FieldType.Label) add($"{p}.label", $"Field '{f.Key}' needs a label.");
        if (f.Columns is < 1 or > 4) add($"{p}.columns", "Columns must be between 1 and 4.");

        if (f.Min is { } min && f.Max is { } max && min > max) add($"{p}.min", "Minimum is greater than maximum.");
        if (f.MinLength is { } minL && f.MaxLength is { } maxL && minL > maxL) add($"{p}.minLength", "Minimum length is greater than maximum length.");
        if (f.MinLength < 0 || f.MaxLength < 0) add($"{p}.maxLength", "Lengths cannot be negative.");
        if (f.Decimals is < 0 or > 10) add($"{p}.decimals", "Decimals must be between 0 and 10.");

        if (!string.IsNullOrEmpty(f.Pattern))
        {
            try { _ = new Regex(f.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException ex) { add($"{p}.pattern", $"Invalid pattern: {ex.Message}"); }
        }
        if (!string.IsNullOrEmpty(f.Mask) && f.Type is not (FieldType.IdNumber or FieldType.Text or FieldType.Phone))
            add($"{p}.mask", "A mask only applies to text, phone and ID number fields.");

        switch (f.Type)
        {
            case FieldType.SingleSelect or FieldType.MultiSelect:
                if (f.Options is not { Count: > 0 }) add($"{p}.options", $"'{f.Key}' needs at least one option.");
                else
                {
                    var dup = f.Options.GroupBy(o => o.Code).FirstOrDefault(g => g.Count() > 1);
                    if (dup is not null) add($"{p}.options", $"Option code '{dup.Key}' is used twice.");
                    if (f.Options.Any(o => string.IsNullOrWhiteSpace(o.Code))) add($"{p}.options", "Every option needs a code.");
                }
                var displays = f.Type == FieldType.SingleSelect ? SingleDisplays : MultiDisplays;
                if (f.Display is not null && !displays.Contains(f.Display))
                    add($"{p}.display", $"Display must be one of: {string.Join(", ", displays)}.");
                break;

            case FieldType.Lookup:
                if (string.IsNullOrWhiteSpace(f.LookupList)) add($"{p}.lookupList", $"'{f.Key}' needs a lookup list.");
                else if (lookupExists is not null && !lookupExists(f.LookupList)) add($"{p}.lookupList", $"Lookup list '{f.LookupList}' does not exist.");
                if (f.LookupParentField is { } parentKey)
                {
                    var scope = rowKeys ?? topKeys;
                    if (!scope.Contains(parentKey)) add($"{p}.lookupParentField", $"Parent field '{parentKey}' is not in this form.");
                    if (parentKey == f.Key) add($"{p}.lookupParentField", "A field cannot be its own parent.");
                }
                break;

            case FieldType.Calculated:
                if (string.IsNullOrWhiteSpace(f.Formula)) add($"{p}.formula", $"Calculated field '{f.Key}' needs a formula.");
                break;

            case FieldType.RepeatingGroup:
                if (rowKeys is not null) add($"{p}.type", "Repeating groups cannot be nested.");
                if (f.Fields is not { Count: > 0 }) add($"{p}.fields", $"Repeating group '{f.Key}' needs at least one column.");
                if (f.MinRows is { } minR && f.MaxRows is { } maxR && minR > maxR) add($"{p}.minRows", "Minimum rows is greater than maximum rows.");
                break;
        }

        if (f.Type != FieldType.Calculated && !string.IsNullOrWhiteSpace(f.Formula))
            add($"{p}.formula", "Only calculated fields have a formula.");
        if (f.Type != FieldType.RepeatingGroup && f.Fields is { Count: > 0 })
            add($"{p}.fields", "Only repeating groups have child fields.");

        // Expressions: names must be fields in scope, context variables, or 'value' in abnormal rules.
        var known = rowKeys is null ? topKeys : [.. rowKeys, .. topKeys];
        CheckExpression(f.VisibleWhen, $"{p}.visibleWhen", known, variables, groupChildKeys, add);
        CheckExpression(f.RequiredWhen, $"{p}.requiredWhen", known, variables, groupChildKeys, add);
        CheckExpression(f.EnabledWhen, $"{p}.enabledWhen", known, variables, groupChildKeys, add);
        CheckExpression(f.Formula, $"{p}.formula", known, variables, groupChildKeys, add);
        if (f.Formula is not null && CompileOrNull(f.Formula)?.Identifiers.Contains(f.Key) == true)
            add($"{p}.formula", $"'{f.Key}' cannot use its own value.");

        for (var r = 0; r < (f.Abnormal?.Count ?? 0); r++)
        {
            var rule = f.Abnormal![r];
            CheckExpression(rule.When, $"{p}.abnormal[{r}].when", [.. known, "value"], variables, groupChildKeys, add, required: true);
            if (rule.Severity is not ("warning" or "critical")) add($"{p}.abnormal[{r}].severity", "Severity must be 'warning' or 'critical'.");
        }

        if (f.Default is JsonValue dv && dv.TryGetValue<string>(out var ds) && ds.StartsWith('='))
            CheckExpression(ds[1..], $"{p}.default", known, variables, groupChildKeys, add, required: true);

        if (f.Type == FieldType.RepeatingGroup && rowKeys is null && f.Fields is { Count: > 0 })
        {
            var childKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var c = 0; c < f.Fields.Count; c++)
            {
                var child = f.Fields[c];
                if (!KeyRegex().IsMatch(child.Key ?? "")) add($"{p}.fields[{c}].key", $"Column key '{child.Key}' must be lower_snake_case.");
                else if (!childKeys.Add(child.Key!)) add($"{p}.fields[{c}].key", $"Column key '{child.Key}' is used twice.");
            }
            for (var c = 0; c < f.Fields.Count; c++)
                CheckField(f.Fields[c], $"{p}.fields[{c}]", topKeys, childKeys, variables, groupChildKeys, lookupExists, add);
        }
    }

    private static void CheckExpression(string? source, string path, ICollection<string> known, HashSet<string> variables,
        HashSet<string> groupChildKeys, Action<string, string> add, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            if (required) add(path, "The expression is empty.");
            return;
        }

        if (!ExpressionCompiler.TryCompile(source, out var compiled, out var error))
        {
            add(path, error!);
            return;
        }

        foreach (var id in compiled!.Identifiers)
        {
            if (known.Contains(id) || variables.Contains(id)) continue;
            add(path, groupChildKeys.Contains(id)
                ? $"'{id}' is a column of a repeating group and can only be used inside that group."
                : $"Unknown name '{id}'. Use a field key from this form{(variables.Count > 0 ? $" or one of: {string.Join(", ", variables)}" : "")}.");
        }
    }

    private static CompiledExpression? CompileOrNull(string source) =>
        ExpressionCompiler.TryCompile(source, out var c, out _) ? c : null;

    /// <summary>
    /// FR-DYN-01/12 rules across versions: a key that existed keeps a compatible type, and a key that holds
    /// data cannot disappear (it must be retired instead).
    /// </summary>
    public static List<DefinitionError> CheckCompatibility(IEnumerable<FormDefinition> previousVersions, FormDefinition draft,
        IReadOnlySet<string> keysWithData)
    {
        var errors = new List<DefinitionError>();
        var current = draft.AllFields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var seen = new Dictionary<string, FieldType>(StringComparer.Ordinal);
        foreach (var v in previousVersions)
            foreach (var f in v.AllFields)
                seen.TryAdd(f.Key, f.Type);

        foreach (var (key, oldType) in seen)
        {
            if (current.TryGetValue(key, out var now))
            {
                if (!Compatible(oldType, now.Type))
                    errors.Add(new DefinitionError(key, $"'{key}' was {oldType} in an earlier version; it cannot become {now.Type}. Add a new field instead."));
            }
            else if (keysWithData.Contains(key))
            {
                errors.Add(new DefinitionError(key, $"'{key}' holds data on existing records, so it cannot be removed. Mark it as retired instead."));
            }
        }
        return errors;
    }

    private static bool Compatible(FieldType from, FieldType to)
    {
        if (from == to) return true;
        FieldType[] text = [FieldType.Text, FieldType.MultilineText, FieldType.RichNote, FieldType.Phone, FieldType.Email, FieldType.IdNumber];
        if (text.Contains(from) && text.Contains(to)) return true;
        if (from == FieldType.Integer && to == FieldType.Decimal) return true;
        if (from is FieldType.SingleSelect or FieldType.Lookup && to is FieldType.SingleSelect or FieldType.Lookup) return true;
        return false;
    }
}

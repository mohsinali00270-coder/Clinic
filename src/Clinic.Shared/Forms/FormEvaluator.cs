using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Clinic.Shared.Forms.Expressions;

namespace Clinic.Shared.Forms;

public enum AbnormalSeverity
{
    None,
    Warning,
    Critical,
}

/// <summary>Everything outside the record's own fields that a form evaluation needs.</summary>
public sealed class EvaluationContext
{
    /// <summary>Role names of the current user, for field permissions (FR-AUTH-03).</summary>
    public IReadOnlyCollection<string> Roles { get; init; } = [];
    public DateTime Today { get; init; } = DateTime.Today;

    /// <summary>Context variables of the entity type (gender, ageYears…).</summary>
    public IReadOnlyDictionary<string, object?> Variables { get; init; } = new Dictionary<string, object?>();

    /// <summary>Retired fields stay visible on records that already have a value, hidden on new ones.</summary>
    public bool IsNewRecord { get; init; } = true;

    /// <summary>Optional: options of a lookup list (filtered by parent code) to validate lookup values.</summary>
    public Func<string, string?, IReadOnlyList<FieldOption>?>? Lookups { get; init; }

    /// <summary>Validation language for messages ("en" / "ur").</summary>
    public string Language { get; init; } = "en";
}

public sealed class FieldState
{
    public bool Visible { get; set; } = true;
    public bool Required { get; set; }
    public bool Enabled { get; set; } = true;
    public bool CanView { get; set; } = true;
    public bool CanEdit { get; set; } = true;
    public AbnormalSeverity Abnormal { get; set; }
    public string? AbnormalMessage { get; set; }
    public List<string> Errors { get; } = [];
}

public sealed class FormEvaluation
{
    /// <summary>Values with calculated fields filled in.</summary>
    public required JsonObject Values { get; init; }

    /// <summary>State by field path: "key", or "group[0].child" for repeating-group cells.</summary>
    public required Dictionary<string, FieldState> Fields { get; init; }
    public required Dictionary<string, bool> SectionVisible { get; init; }

    public bool IsValid => Fields.Values.All(f => f.Errors.Count == 0);

    public IEnumerable<(string Path, string Error)> Errors =>
        Fields.SelectMany(kv => kv.Value.Errors.Select(e => (kv.Key, e)));

    public FieldState State(string path) => Fields.TryGetValue(path, out var s) ? s : new FieldState();
}

/// <summary>Thrown when a definition cannot be evaluated (bad expression, calculation cycle).</summary>
public sealed class FormDefinitionException(string message) : Exception(message);

/// <summary>
/// Runs a form definition against values: calculations (in dependency order), visibility, required/enabled
/// conditions, role permissions, abnormal ranges and validation. Identical on device (offline, FR-DYN-21)
/// and server (re-check, FR-DYN-04). Construct once per definition; Evaluate is cheap.
/// </summary>
public sealed class FormEvaluator
{
    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex PhoneRegex = new(@"^\+?[0-9 ()\-]{5,20}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex TimeRegex = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly Dictionary<string, CompiledExpression> _expressions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FieldDefinition>> _calcOrder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, FieldDefinition>> _scopeFields = new(StringComparer.Ordinal);

    public FormEvaluator(FormDefinition definition)
    {
        Definition = definition;

        foreach (var section in definition.Sections)
            Prepare(section.VisibleWhen);

        PrepareFields("", definition.AllFields.ToList());
    }

    public FormDefinition Definition { get; }

    private void PrepareFields(string scope, List<FieldDefinition> fields)
    {
        foreach (var f in fields)
        {
            Prepare(f.VisibleWhen);
            Prepare(f.RequiredWhen);
            Prepare(f.EnabledWhen);
            Prepare(f.Formula);
            foreach (var rule in f.Abnormal ?? []) Prepare(rule.When);
            if (f.Default is JsonValue dv && dv.TryGetValue<string>(out var ds) && ds.StartsWith('=')) Prepare(ds[1..]);

            if (!string.IsNullOrEmpty(f.Pattern) && !_patterns.ContainsKey(f.Pattern))
            {
                try { _patterns[f.Pattern] = new Regex(f.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException ex) { throw new FormDefinitionException($"Field '{f.Key}': invalid pattern ({ex.Message})."); }
            }

            if (f.Type == FieldType.RepeatingGroup && f.Fields is { Count: > 0 } children)
                PrepareFields(f.Key, children);
        }

        _calcOrder[scope] = OrderCalculations(scope, fields);
        _scopeFields[scope] = fields.GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private void Prepare(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || _expressions.ContainsKey(source)) return;
        try { _expressions[source] = ExpressionCompiler.Compile(source); }
        catch (ExpressionException ex) { throw new FormDefinitionException($"Expression '{source}': {ex.Message}"); }
    }

    /// <summary>Topological order so a calculation that uses another calculation runs after it.</summary>
    private List<FieldDefinition> OrderCalculations(string scope, List<FieldDefinition> fields)
    {
        var calcs = fields.Where(f => f.Type == FieldType.Calculated && !string.IsNullOrWhiteSpace(f.Formula))
            .ToDictionary(f => f.Key, StringComparer.Ordinal);
        var ordered = new List<FieldDefinition>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done

        void Visit(FieldDefinition f, Stack<string> path)
        {
            if (state.TryGetValue(f.Key, out var s))
            {
                if (s == 1) throw new FormDefinitionException($"Calculated fields depend on each other in a loop: {string.Join(" → ", path.Reverse())} → {f.Key}.");
                return;
            }
            state[f.Key] = 1;
            path.Push(f.Key);
            foreach (var dep in _expressions[f.Formula!].Identifiers)
                if (calcs.TryGetValue(dep, out var d)) Visit(d, path);
            path.Pop();
            state[f.Key] = 2;
            ordered.Add(f);
        }

        foreach (var f in calcs.Values) Visit(f, new Stack<string>());
        _ = scope;
        return ordered;
    }

    /// <summary>Applies defaults (literal or "=expression") to empty fields of a new record.</summary>
    public void ApplyDefaults(JsonObject values, EvaluationContext context)
    {
        foreach (var f in Definition.AllFields.Where(f => f.Default is not null && !f.Retired))
        {
            if (!FormValues.IsEmpty(values[f.Key])) continue;
            if (f.Default is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('='))
            {
                var result = _expressions[s[1..]].Evaluate(n => Resolve(n, values, null, context, ""), context.Today);
                values[f.Key] = ToNode(f, result);
            }
            else
            {
                values[f.Key] = f.Default!.DeepClone();
            }
        }
    }

    public FormEvaluation Evaluate(JsonObject input, EvaluationContext context)
    {
        var values = (JsonObject)input.DeepClone();
        var states = new Dictionary<string, FieldState>(StringComparer.Ordinal);
        var sections = new Dictionary<string, bool>(StringComparer.Ordinal);

        EvaluateScope("", Definition.AllFields.ToList(), values, null, context, states, prefix: "");

        foreach (var section in Definition.Sections)
        {
            var visible = section.VisibleWhen is null || Condition(section.VisibleWhen, values, null, context, "");
            sections[section.Key] = visible;
            if (visible) continue;
            foreach (var f in section.Fields)
            {
                var st = states[f.Key];
                st.Visible = false;
                st.Errors.Clear();
                foreach (var child in states.Where(kv => kv.Key.StartsWith(f.Key + "[", StringComparison.Ordinal)))
                    child.Value.Errors.Clear();
            }
        }

        return new FormEvaluation { Values = values, Fields = states, SectionVisible = sections };
    }

    private void EvaluateScope(string scope, List<FieldDefinition> fields, JsonObject values, JsonObject? parent,
        EvaluationContext ctx, Dictionary<string, FieldState> states, string prefix)
    {
        // 1. Calculations, in dependency order. Device-supplied values for calculated fields are ignored.
        foreach (var f in _calcOrder[scope])
        {
            var result = _expressions[f.Formula!].Evaluate(n => Resolve(n, values, parent, ctx, scope), ctx.Today);
            values[f.Key] = ToNode(f, result);
        }

        // 2. State + validation per field.
        foreach (var f in fields)
        {
            var path = prefix + f.Key;
            var st = new FieldState();
            states[path] = st;
            var value = values[f.Key];

            st.CanView = f.Permissions is null || Allowed(f.Permissions.ViewRoles, ctx.Roles) || Allowed(f.Permissions.EditRoles, ctx.Roles, emptyMeansAll: false);
            st.CanEdit = st.CanView && (f.Permissions is null || Allowed(f.Permissions.EditRoles, ctx.Roles))
                         && f.Type is not (FieldType.Calculated or FieldType.Label);

            st.Visible = st.CanView
                         && (f.VisibleWhen is null || Condition(f.VisibleWhen, values, parent, ctx, scope))
                         && !(f.Retired && (ctx.IsNewRecord || FormValues.IsEmpty(value)));
            st.Enabled = f.EnabledWhen is null || Condition(f.EnabledWhen, values, parent, ctx, scope);
            st.Required = f.HoldsValue && f.Type != FieldType.Calculated &&
                          (f.Required || (f.RequiredWhen is not null && Condition(f.RequiredWhen, values, parent, ctx, scope)));

            if (f.Abnormal is { Count: > 0 } rules && !FormValues.IsEmpty(value))
            {
                foreach (var rule in rules)
                {
                    var hit = _expressions[rule.When].EvaluateBool(n => n == "value"
                        ? ToExprValue(f, values[f.Key])
                        : Resolve(n, values, parent, ctx, scope), ctx.Today);
                    if (!hit) continue;
                    st.Abnormal = rule.Severity == "critical" ? AbnormalSeverity.Critical : AbnormalSeverity.Warning;
                    st.AbnormalMessage = rule.Message?.Get(ctx.Language);
                    break;
                }
            }

            if (f.Type == FieldType.RepeatingGroup)
            {
                var rows = value as JsonArray ?? [];
                if (st.Visible && st.CanEdit)
                {
                    if (st.Required && rows.Count == 0) st.Errors.Add($"{Label(f, ctx)} needs at least one row.");
                    if (f.MinRows is { } minRows && rows.Count < minRows) st.Errors.Add($"{Label(f, ctx)} needs at least {minRows} row(s).");
                    if (f.MaxRows is { } maxRows && rows.Count > maxRows) st.Errors.Add($"{Label(f, ctx)} allows at most {maxRows} row(s).");
                }
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i] is not JsonObject row) continue;
                    EvaluateScope(f.Key, f.Fields ?? [], row, values, ctx, states, $"{path}[{i}].");
                    if (!st.Visible || !st.CanEdit)
                    {
                        foreach (var child in states.Where(kv => kv.Key.StartsWith($"{path}[{i}].", StringComparison.Ordinal)))
                            child.Value.Errors.Clear();
                    }
                }
                continue;
            }

            if (!st.Visible || !st.CanEdit || !st.Enabled) continue;
            Validate(f, value, st, ctx);
        }
    }

    private void Validate(FieldDefinition f, JsonNode? value, FieldState st, EvaluationContext ctx)
    {
        var label = Label(f, ctx);
        if (FormValues.IsEmpty(value))
        {
            if (st.Required) st.Errors.Add($"{label} is required.");
            return;
        }

        switch (f.Type)
        {
            case FieldType.Text or FieldType.MultilineText or FieldType.RichNote or FieldType.Phone or FieldType.Email or FieldType.IdNumber:
            {
                var s = FormValues.AsString(value) ?? "";
                if (f.MinLength is { } minLen && s.Length < minLen) st.Errors.Add($"{label} must be at least {minLen} characters.");
                if (f.MaxLength is { } maxLen && s.Length > maxLen) st.Errors.Add($"{label} must be at most {maxLen} characters.");
                if (f.Type == FieldType.Email && !SafeMatch(EmailRegex, s)) st.Errors.Add($"{label} is not a valid email address.");
                if (f.Type == FieldType.Phone && !SafeMatch(PhoneRegex, s)) st.Errors.Add($"{label} is not a valid phone number.");
                if (!string.IsNullOrEmpty(f.Mask) && !MatchesMask(s, f.Mask)) st.Errors.Add($"{label} must look like {f.Mask.Replace('#', '0')}.");
                if (!string.IsNullOrEmpty(f.Pattern) && !SafeMatch(_patterns[f.Pattern], s))
                    st.Errors.Add(f.PatternMessage?.Get(ctx.Language) ?? $"{label} is not in the expected format.");
                break;
            }

            case FieldType.Integer or FieldType.Decimal:
            {
                if (FormValues.AsDecimal(value) is not { } n)
                {
                    st.Errors.Add($"{label} must be a number.");
                    break;
                }
                if (f.Type == FieldType.Integer && n != decimal.Truncate(n)) st.Errors.Add($"{label} must be a whole number.");
                if (f.Min is { } min && n < min) st.Errors.Add($"{label} must be at least {min.ToString(CultureInfo.InvariantCulture)}.");
                if (f.Max is { } max && n > max) st.Errors.Add($"{label} must be at most {max.ToString(CultureInfo.InvariantCulture)}.");
                break;
            }

            case FieldType.Date or FieldType.DateTime:
            {
                if (FormValues.AsDate(value) is not { } d)
                {
                    st.Errors.Add($"{label} is not a valid date.");
                    break;
                }
                if (f.NotInFuture && (f.Type == FieldType.Date ? d.Date > ctx.Today.Date : d > ctx.Today.Date.AddDays(1)))
                    st.Errors.Add($"{label} cannot be in the future.");
                break;
            }

            case FieldType.Time:
                if (!SafeMatch(TimeRegex, FormValues.AsString(value) ?? "")) st.Errors.Add($"{label} must be a time like 14:30.");
                break;

            case FieldType.YesNo:
                if (FormValues.AsBool(value) is null) st.Errors.Add($"{label} must be yes or no.");
                break;

            case FieldType.SingleSelect:
            {
                var code = FormValues.AsString(value);
                if (f.Options is { Count: > 0 } options && options.All(o => o.Code != code))
                    st.Errors.Add($"{label}: '{code}' is not one of the options.");
                break;
            }

            case FieldType.MultiSelect:
            {
                if (value is not JsonArray)
                {
                    st.Errors.Add($"{label} must be a list of choices.");
                    break;
                }
                var codes = FormValues.AsStringList(value);
                if (f.Options is { Count: > 0 } options)
                {
                    var unknown = codes.Where(c => options.All(o => o.Code != c)).ToList();
                    if (unknown.Count > 0) st.Errors.Add($"{label}: {string.Join(", ", unknown)} not in the options.");
                }
                if (f.MinRows is { } minSel && codes.Count < minSel) st.Errors.Add($"{label}: choose at least {minSel}.");
                if (f.MaxRows is { } maxSel && codes.Count > maxSel) st.Errors.Add($"{label}: choose at most {maxSel}.");
                break;
            }

            case FieldType.Lookup:
            {
                var code = FormValues.AsString(value);
                if (ctx.Lookups is not null && f.LookupList is not null)
                {
                    var items = ctx.Lookups(f.LookupList, null);
                    if (items is not null && items.All(o => o.Code != code))
                        st.Errors.Add($"{label}: '{code}' is not in the list.");
                }
                break;
            }
        }
    }

    // ---- helpers ---------------------------------------------------------

    private bool Condition(string expression, JsonObject values, JsonObject? parent, EvaluationContext ctx, string scope) =>
        _expressions[expression].EvaluateBool(n => Resolve(n, values, parent, ctx, scope), ctx.Today);

    /// <summary>Row values first (inside a repeating group), then the record, then context variables.</summary>
    private object? Resolve(string name, JsonObject values, JsonObject? parent, EvaluationContext ctx, string scope)
    {
        var fields = _scopeFields[scope];
        if (fields.TryGetValue(name, out var field)) return ToExprValue(field, values[name]);
        if (parent is not null && _scopeFields[""].TryGetValue(name, out var top)) return ToExprValue(top, parent[name]);
        if (ctx.Variables.TryGetValue(name, out var v)) return v;
        return values.TryGetPropertyValue(name, out var raw) ? ToExprValue(null, raw) : null;
    }

    /// <summary>JSON value → expression value, using the field type when known.</summary>
    public static object? ToExprValue(FieldDefinition? field, JsonNode? node)
    {
        if (node is null) return null;
        switch (field?.Type)
        {
            case FieldType.Integer or FieldType.Decimal:
                return FormValues.AsDecimal(node) is { } d ? (double)d : FormValues.AsString(node);
            case FieldType.Date or FieldType.DateTime:
                return FormValues.AsDate(node);
            case FieldType.YesNo:
                return FormValues.AsBool(node);
            case FieldType.MultiSelect:
                return FormValues.AsStringList(node);
            case FieldType.RepeatingGroup:
                return node is JsonArray a ? a.Select(x => (object?)x).ToList() : new List<object?>();
        }

        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b;
            if (FormValues.AsDecimal(v) is { } n) return (double)n;
        }
        return node is JsonArray arr ? FormValues.AsStringList(arr) : node.ToJsonString();
    }

    /// <summary>Expression result → JSON value for storage, rounded to the field's decimals.</summary>
    public static JsonNode? ToNode(FieldDefinition field, object? result) => result switch
    {
        null => null,
        double d when field.Decimals is { } places => JsonValue.Create(Math.Round((decimal)d, Math.Clamp(places, 0, 10), MidpointRounding.AwayFromZero)),
        double d => JsonValue.Create(Math.Round((decimal)d, 6)),
        bool b => JsonValue.Create(b),
        DateTime dt => JsonValue.Create(dt.TimeOfDay == TimeSpan.Zero
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
        IEnumerable<string> list => new JsonArray(list.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        _ => JsonValue.Create(result.ToString()),
    };

    private static bool Allowed(List<string>? roles, IReadOnlyCollection<string> userRoles, bool emptyMeansAll = true) =>
        roles is null || roles.Count == 0 ? emptyMeansAll : roles.Any(r => userRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

    private static string Label(FieldDefinition f, EvaluationContext ctx) => f.Label.Get(ctx.Language) is { Length: > 0 } l ? l : f.Key;

    private static bool SafeMatch(Regex regex, string input)
    {
        try { return regex.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static bool MatchesMask(string value, string mask)
    {
        if (value.Length != mask.Length) return false;
        for (var i = 0; i < mask.Length; i++)
        {
            var ok = mask[i] switch
            {
                '#' => char.IsAsciiDigit(value[i]),
                'A' => char.IsLetter(value[i]),
                '*' => true,
                _ => value[i] == mask[i],
            };
            if (!ok) return false;
        }
        return true;
    }
}

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Clinic.Application.Abstractions;
using Clinic.Application.Sync;
using Clinic.Domain.Forms;
using Clinic.Domain.Notes;
using Clinic.Domain.Sync;
using Clinic.Shared.Forms;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Forms;

/// <summary>Published form versions are immutable, so their parsed definition and evaluator are cached for good.</summary>
public static class FormDefinitionCache
{
    public sealed record Entry(Guid VersionId, string EntityType, FormDefinition Definition, FormEvaluator Evaluator);

    private static readonly ConcurrentDictionary<(Guid Tenant, Guid Version), Entry> Cache = new();

    public static async Task<Entry?> GetAsync(IAppDbContext db, Guid versionId, CancellationToken ct = default)
    {
        var tenant = db.CurrentTenantId ?? Guid.Empty;
        if (Cache.TryGetValue((tenant, versionId), out var hit)) return hit;

        var row = await db.FormVersions.AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => new { v.EntityType, v.DefinitionJson })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var definition = FormJson.Deserialize(row.DefinitionJson);
        var entry = new Entry(versionId, row.EntityType, definition, new FormEvaluator(definition));
        Cache[(tenant, versionId)] = entry;
        return entry;
    }
}

/// <summary>Entity types that carry dynamic data, and where their records live.</summary>
public static class DynamicParents
{
    private static readonly Dictionary<string, Type> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [SyncEntityNames.BranchNote] = typeof(BranchNote),
        [SyncEntityNames.Patient] = typeof(Domain.Clinical.Patient),
        [SyncEntityNames.DoctorProfile] = typeof(Domain.Clinical.DoctorProfile),
        [SyncEntityNames.Appointment] = typeof(Domain.Clinical.Appointment),
        [SyncEntityNames.Encounter] = typeof(Domain.Clinical.Encounter),
        [SyncEntityNames.Vitals] = typeof(Domain.Clinical.VitalsRecord),
        [SyncEntityNames.Case] = typeof(Domain.Clinical.PatientCase),
        [SyncEntityNames.Procedure] = typeof(Domain.Clinical.ProcedureRecord),
        [SyncEntityNames.LabResult] = typeof(Domain.Clinical.LabResult),
        [SyncEntityNames.Feedback] = typeof(Domain.Clinical.Feedback),
    };

    private static readonly MethodInfo FindMethod =
        typeof(DynamicParents).GetMethod(nameof(FindGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static bool Supports(string entityType) => Types.ContainsKey(entityType);

    public static Task<IHasDynamicData?> FindAsync(IAppDbContext db, string entityType, Guid id) =>
        Types.TryGetValue(entityType, out var type)
            ? (Task<IHasDynamicData?>)FindMethod.MakeGenericMethod(type).Invoke(null, [db, id])!
            : Task.FromResult<IHasDynamicData?>(null);

    private static async Task<IHasDynamicData?> FindGenericAsync<T>(IAppDbContext db, Guid id) where T : class, IHasDynamicData
    {
        var tenant = db.CurrentTenantId;
        return await db.Set<T>().IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenant && !e.IsDeleted);
    }
}

/// <summary>
/// Server rules for pushed dynamic values (FR-SYN-13, FR-DYN-04, FR-AUTH-03): the parent must exist and be writable
/// by the caller, the field must exist in the form version, the caller's role must be allowed to edit it, the value
/// must pass the field's own validation, and calculated fields are recomputed by the server.
/// </summary>
public static class FieldValueRules
{
    /// <summary>Marker: FieldValue writes are authorised through the parent record's permission.</summary>
    public const string WritePermission = SyncEntityDescriptor.CustomPermission;

    public static async Task<string?> CheckFormVersionAsync(IAppDbContext db, Guid? versionId, string entityType)
    {
        if (versionId is null) return null;
        var version = await FormDefinitionCache.GetAsync(db, versionId.Value);
        if (version is null) return "The form version does not exist. Sync to get the latest forms.";
        return string.Equals(version.EntityType, entityType, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"That form is for {version.EntityType}, not {entityType}.";
    }

    public static async Task<string?> AuthorizeAsync(SyncWriteContext ctx)
    {
        var fv = (FieldValue)ctx.Entity;

        if (!DynamicParents.Supports(fv.EntityType))
            return $"{fv.EntityType} records do not have dynamic fields yet.";

        var parent = await DynamicParents.FindAsync(ctx.Db, fv.EntityType, fv.RecordId);
        if (parent is null) return "The record these values belong to was not found on the server.";

        // Writing a value means editing the parent record.
        var parentDescriptor = SyncRegistry.ForType(parent.GetType());
        if (parentDescriptor.WritePermission is { } perm && perm != WritePermission && ctx.User.GetScope(perm) is null)
            return $"You do not have the {perm} permission.";
        var parentError = await parentDescriptor.Authorize(new SyncWriteContext(parent, false, ctx.User, ctx.Db));
        if (parentError is not null) return parentError;

        fv.BranchId = parent.GetSyncBranchId();
        if (ctx.Stage != SyncStage.BeforeChange &&
            await Clinical.EncounterSyncRules.CheckLockedParentAsync(ctx.Db, ctx.User, parent, fv) is { } locked) return locked;

        var version = await FormDefinitionCache.GetAsync(ctx.Db, fv.FormVersionId);
        if (version is null) return "The form version does not exist. Sync to get the latest forms.";
        if (!string.Equals(version.EntityType, fv.EntityType, StringComparison.OrdinalIgnoreCase))
            return $"That form is for {version.EntityType}, not {fv.EntityType}.";

        var field = version.Definition.FindField(fv.FieldKey);
        if (field is null) return $"Field '{fv.FieldKey}' is not part of form version {version.Definition.Name.En}.";
        if (field.Type == FieldType.Label) return $"'{fv.FieldKey}' is a label and holds no value.";

        if (field.Permissions is { EditRoles.Count: > 0 } p &&
            !p.EditRoles.Any(r => ctx.User.Roles.Contains(r, StringComparer.OrdinalIgnoreCase)))
            return $"Your role may not edit '{field.Label.En}'.";

        var node = FieldValueCodec.Decode(fv.ValueKind, fv.ValueText, fv.ValueNumber, fv.ValueDate, fv.ValueBool, fv.ValueJson);
        if (field.Type == FieldType.Calculated) return null; // recomputed in AfterApplyAsync

        var error = CheckValue(field, node, version.Definition.EntityType);
        if (error is not null) return error;

        if (field.Type == FieldType.Lookup && field.LookupList is { } list && FormValues.AsString(node) is { } code)
        {
            if (!await ctx.Db.LookupItems.AnyAsync(i => i.ListKey == list && i.Code == code))
                return $"{field.Label.En}: '{code}' is not in the '{list}' list.";
        }

        if (field.Unique && fv.ValueText is { Length: > 0 } text)
        {
            var taken = await ctx.Db.FieldValues.AnyAsync(v =>
                v.EntityType == fv.EntityType && v.FieldKey == fv.FieldKey && v.ValueText == text && v.RecordId != fv.RecordId);
            if (taken) return $"{field.Label.En} '{text}' is already used by another record.";
        }

        // Normalise the typed columns to the field type (e.g. a numeric string becomes a number).
        var normalised = FieldValueCodec.Encode(field.Type, node);
        fv.ValueKind = normalised.Kind;
        fv.ValueText = normalised.Text;
        fv.ValueNumber = normalised.Number;
        fv.ValueDate = normalised.Date;
        fv.ValueBool = normalised.Bool;
        fv.ValueJson = normalised.Json;
        return null;
    }

    /// <summary>Type, range, length, pattern, mask and option checks for one value, without cross-field conditions.</summary>
    public static string? CheckValue(FieldDefinition field, JsonNode? node, string entityType)
    {
        var probe = FormJson.Deserialize(FormJson.Serialize(new FormDefinition
        {
            EntityType = entityType,
            Name = new("probe"),
            Sections = [new SectionDefinition { Key = "s", Title = new("s"), Fields = [field] }],
        }));
        var f = probe.Sections[0].Fields[0];
        f.Required = false;
        f.VisibleWhen = f.RequiredWhen = f.EnabledWhen = null;
        f.Permissions = null;
        f.Abnormal = null;
        foreach (var child in f.Fields ?? [])
        {
            child.VisibleWhen = child.RequiredWhen = child.EnabledWhen = null;
            child.Permissions = null;
        }

        var result = new FormEvaluator(probe).Evaluate(new JsonObject { [f.Key] = node?.DeepClone() }, new EvaluationContext());
        var first = result.Errors.FirstOrDefault();
        return first.Error;
    }

    /// <summary>
    /// After a value is applied: recompute calculated fields from all of the record's values (the server's copy
    /// wins), and refresh the parent's DynamicData snapshot (FR-DYN-31; hidden, so it does not re-sync the parent).
    /// </summary>
    public static async Task AfterApplyAsync(SyncWriteContext ctx)
    {
        var fv = (FieldValue)ctx.Entity;
        var db = ctx.Db;
        var parent = await DynamicParents.FindAsync(db, fv.EntityType, fv.RecordId);
        if (parent is null) return;

        var version = await FormDefinitionCache.GetAsync(db, parent.FormVersionId ?? fv.FormVersionId);
        if (version is null) return;

        var rows = await db.FieldValues.Where(v => v.RecordId == fv.RecordId && v.EntityType == fv.EntityType).ToListAsync();
        var values = new JsonObject();
        foreach (var r in rows)
            values[r.FieldKey] = FieldValueCodec.Decode(r.ValueKind, r.ValueText, r.ValueNumber, r.ValueDate, r.ValueBool, r.ValueJson);

        var evaluation = version.Evaluator.Evaluate(values, new EvaluationContext
        {
            Roles = ctx.User.Roles,
            Variables = parent.GetFormVariables(),
            IsNewRecord = false,
        });

        foreach (var field in version.Definition.AllFields.Where(NeedsServerCalculation))
        {
            var computed = evaluation.Values[field.Key];
            var row = rows.FirstOrDefault(r => r.FieldKey == field.Key);
            var stored = row is null ? null
                : FieldValueCodec.Decode(row.ValueKind, row.ValueText, row.ValueNumber, row.ValueDate, row.ValueBool, row.ValueJson);
            if (SameValue(stored, computed)) continue;

            var typed = FieldValueCodec.Encode(field.Type, computed);
            if (row is null)
            {
                row = new FieldValue
                {
                    Id = DeterministicGuid.ForFieldValue(fv.RecordId, field.Key),
                    RecordId = fv.RecordId,
                    EntityType = fv.EntityType,
                    FieldKey = field.Key,
                    FormVersionId = version.VersionId,
                    BranchId = parent.GetSyncBranchId(),
                };
                db.FieldValues.Add(row);
            }
            row.ValueKind = typed.Kind;
            row.ValueText = typed.Text;
            row.ValueNumber = typed.Number;
            row.ValueDate = typed.Date;
            row.ValueBool = typed.Bool;
            row.ValueJson = typed.Json;
        }

        var snapshot = new JsonObject();
        foreach (var (key, value) in evaluation.Values)
            if (!FormValues.IsEmpty(value)) snapshot[key] = value?.DeepClone();
        parent.DynamicData = snapshot.ToJsonString(FormJson.Options);

        await db.SaveChangesAsync();
    }

    /// <summary>Numbers compare by value (the database returns 31.000000 for 31.0).</summary>
    public static bool SameValue(JsonNode? a, JsonNode? b)
    {
        if (FormValues.IsEmpty(a) && FormValues.IsEmpty(b)) return true;
        if (a is JsonValue && b is JsonValue && FormValues.AsDecimal(a) is { } x && FormValues.AsDecimal(b) is { } y
            && a.GetValueKind() == System.Text.Json.JsonValueKind.Number && b.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            return x == y;
        if (a is JsonArray aa && b is JsonArray ba)
            return aa.Count == ba.Count && aa.Zip(ba).All(p => SameValue(p.First, p.Second));
        if (a is JsonObject ao && b is JsonObject bo)
            return ao.Select(kv => kv.Key).Union(bo.Select(kv => kv.Key)).All(k => SameValue(ao[k], bo[k]));
        return JsonNode.DeepEquals(a, b);
    }

    private static bool NeedsServerCalculation(FieldDefinition f) =>
        f.Type == FieldType.Calculated ||
        (f.Type == FieldType.RepeatingGroup && f.Fields?.Any(c => c.Type == FieldType.Calculated) == true);

    /// <summary>FR-DYN-06 / FR-AUTH-03: values of fields the caller's role may not view never reach the device.</summary>
    public static async Task<IReadOnlyList<ISyncEntity>> FilterPullAsync(IReadOnlyList<ISyncEntity> rows, SyncReadContext ctx)
    {
        var result = new List<ISyncEntity>(rows.Count);
        foreach (var row in rows)
        {
            var fv = (FieldValue)row;
            var version = await FormDefinitionCache.GetAsync(ctx.Db, fv.FormVersionId);
            var p = version?.Definition.FindField(fv.FieldKey)?.Permissions;
            var restricted = p is { ViewRoles.Count: > 0 }; // same rule as FormEvaluator: no view roles = everyone
            if (restricted && !p!.ViewRoles.Concat(p.EditRoles).Any(r => ctx.User.Roles.Contains(r, StringComparer.OrdinalIgnoreCase)))
                continue;
            result.Add(row);
        }
        return result;
    }
}

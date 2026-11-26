using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Clinic.Client.Core.Local;
using Clinic.Shared.Forms;
using Clinic.Shared.Sync;
using SQLite;

namespace Clinic.Client.Core.Forms;

public sealed record FormVersionInfo(Guid Id, int VersionNo, bool IsCurrent, DateTime PublishedAt, FormDefinition Definition, FormEvaluator Evaluator);

/// <summary>
/// Device side of the dynamic forms engine (§5.5/5.6): picks the form for a record, loads and saves its
/// FieldValues through the outbox, serves lookup options offline, and keeps autosaved drafts.
/// </summary>
public sealed class DynamicDataService(LocalDatabase db)
{
    private static readonly ConcurrentDictionary<Guid, FormVersionInfo> Versions = new();

    /// <summary>
    /// The current published version for a new record: most specific context wins (branch + specialty,
    /// then branch, then specialty, then tenant-wide), newest publication breaks ties.
    /// </summary>
    public async Task<FormVersionInfo?> ResolveFormAsync(string entityType, Guid? branchId, string? specialty = null)
    {
        var candidates = await db.Connection.Table<LocalFormVersion>()
            .Where(v => v.EntityType == entityType && v.IsCurrent && v.IsActive && !v.IsDeleted)
            .ToListAsync();

        var best = candidates
            .Where(v => (v.ContextBranchId == null || v.ContextBranchId == branchId) &&
                        (v.ContextSpecialty == null || string.Equals(v.ContextSpecialty, specialty, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(v => (v.ContextBranchId != null ? 2 : 0) + (v.ContextSpecialty != null ? 1 : 0))
            .ThenByDescending(v => v.PublishedAt)
            .FirstOrDefault();

        return best is null ? null : Info(best);
    }

    /// <summary>FR-DYN-11/24: an existing record always renders with the version it was captured with.</summary>
    public async Task<FormVersionInfo?> GetVersionAsync(Guid versionId)
    {
        if (Versions.TryGetValue(versionId, out var hit)) return hit with { IsCurrent = await IsCurrentAsync(versionId) };
        var row = await db.Connection.FindAsync<LocalFormVersion>(versionId);
        return row is null ? null : Info(row);
    }

    private async Task<bool> IsCurrentAsync(Guid versionId) =>
        (await db.Connection.FindAsync<LocalFormVersion>(versionId))?.IsCurrent ?? false;

    private static FormVersionInfo Info(LocalFormVersion v)
    {
        var cached = Versions.GetOrAdd(v.Id, _ =>
        {
            var def = FormJson.Deserialize(v.DefinitionJson);
            return new FormVersionInfo(v.Id, v.VersionNo, v.IsCurrent, v.PublishedAt, def, new FormEvaluator(def));
        });
        return cached with { IsCurrent = v.IsCurrent };
    }

    public async Task<JsonObject> LoadValuesAsync(string entityType, Guid recordId)
    {
        var rows = await db.Connection.Table<LocalFieldValue>()
            .Where(v => v.RecordId == recordId && v.EntityType == entityType && !v.IsDeleted)
            .ToListAsync();
        return ToJson(rows);
    }

    internal static JsonObject ToJson(IEnumerable<LocalFieldValue> rows)
    {
        var values = new JsonObject();
        foreach (var r in rows)
            values[r.FieldKey] = FieldValueCodec.Decode(r.ValueKind, r.ValueText, r.ValueNumber, r.ValueDate, r.ValueBool, r.ValueJson);
        return values;
    }

    /// <summary>
    /// Writes changed values as FieldValue rows (one outbox entry each) inside the caller's batch, and returns
    /// the snapshot JSON for the parent's DynamicData. Ids are deterministic per (record, field).
    /// </summary>
    public static string StageValues(LocalWriteBatch batch, string entityType, Guid recordId, Guid? branchId,
        FormVersionInfo version, JsonObject values)
    {
        var snapshot = new JsonObject();
        foreach (var field in version.Definition.AllFields.Where(f => f.HoldsValue))
        {
            var node = values[field.Key];
            if (!FormValues.IsEmpty(node)) snapshot[field.Key] = node!.DeepClone();

            var typed = FieldValueCodec.Encode(field.Type, node);
            var id = DeterministicGuid.ForFieldValue(recordId, field.Key);
            var existing = batch.Find<LocalFieldValue>(id);

            if (existing is null)
            {
                if (typed.IsEmpty) continue;
                var row = new LocalFieldValue
                {
                    Id = id, RecordId = recordId, EntityType = entityType, FieldKey = field.Key,
                    FormVersionId = version.Id, BranchId = branchId,
                };
                Apply(row, typed);
                batch.Insert(row);
                continue;
            }

            if (!existing.IsDeleted && Same(existing, typed)) continue;
            Apply(existing, typed);
            existing.FormVersionId = version.Id;
            batch.Update(existing);
        }
        return snapshot.ToJsonString(FormJson.Options);
    }

    /// <summary>
    /// Saves a record together with its dynamic values in one transaction. Order matters (FR-OFF-11): the parent
    /// row is written first so its outbox entry precedes the FieldValue entries that need it on the server.
    /// </summary>
    public static async Task SaveRecordAsync<T>(LocalStore store, T record, bool isNew, FormVersionInfo version,
        JsonObject values, string entityType, Guid? branchId) where T : LocalEntity, ILocalDynamic, new()
    {
        await store.WriteAsync(b =>
        {
            record.FormVersionId = version.Id;
            if (isNew) b.Insert(record);
            else b.Update(record);

            record.DynamicData = StageValues(b, entityType, record.Id, branchId, version, values);
            b.Update(record); // snapshot is local-only: no outbox entry
        });
    }

    private static void Apply(LocalFieldValue row, TypedValue v)
    {
        row.ValueKind = v.Kind;
        row.ValueText = v.Text;
        row.ValueNumber = v.Number;
        row.ValueDate = v.Date;
        row.ValueBool = v.Bool;
        row.ValueJson = v.Json;
    }

    private static bool Same(LocalFieldValue r, TypedValue v) =>
        r.ValueKind == v.Kind && r.ValueText == v.Text && r.ValueNumber == v.Number &&
        r.ValueDate == v.Date && r.ValueBool == v.Bool && r.ValueJson == v.Json;

    // ---- lookups --------------------------------------------------------

    /// <summary>Every active item of the lists a form uses, for offline dropdowns and validation.</summary>
    public async Task<Dictionary<string, List<LocalLookupItem>>> LoadLookupsAsync(FormDefinition definition)
    {
        var keys = definition.AllFields
            .SelectMany(f => f.Fields is { } children ? children.Append(f) : [f])
            .Where(f => f.Type == FieldType.Lookup && f.LookupList is not null)
            .Select(f => f.LookupList!)
            .Distinct()
            .ToList();

        var result = new Dictionary<string, List<LocalLookupItem>>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            result[key] = (await db.Connection.Table<LocalLookupItem>()
                    .Where(i => i.ListKey == key && i.IsActive && !i.IsDeleted)
                    .ToListAsync())
                .OrderBy(i => i.SortOrder).ThenBy(i => i.LabelEn).ToList();
        }
        return result;
    }

    // ---- uniqueness (FR-DYN-02, checked offline against local data) ------

    public async Task<bool> IsTakenAsync(string entityType, string fieldKey, string value, Guid recordId) =>
        await db.Connection.Table<LocalFieldValue>()
            .Where(v => v.EntityType == entityType && v.FieldKey == fieldKey && v.ValueText == value && v.RecordId != recordId && !v.IsDeleted)
            .CountAsync() > 0;

    // ---- drafts (FR-DYN-23) ----------------------------------------------

    public static string DraftKey(string entityType, Guid recordId) => $"{entityType}:{recordId}";

    public Task SaveDraftAsync(string key, JsonObject values) =>
        db.Connection.InsertOrReplaceAsync(new LocalFormDraft { Key = key, ValuesJson = values.ToJsonString(), SavedAt = DateTime.UtcNow });

    public async Task<JsonObject?> LoadDraftAsync(string key) =>
        await db.Connection.FindAsync<LocalFormDraft>(key) is { } d ? JsonNode.Parse(d.ValuesJson) as JsonObject : null;

    public Task DeleteDraftAsync(string key) => db.Connection.DeleteAsync<LocalFormDraft>(key);
}

/// <summary>Keeps each record's DynamicData snapshot in step with its FieldValues after a pull.</summary>
internal static class DynamicSnapshots
{
    public static void Rebuild(SQLiteConnection c, string entityType, Guid recordId)
    {
        var entry = Sync.LocalSyncRegistry.Find(entityType);
        if (entry is null || !typeof(ILocalDynamic).IsAssignableFrom(entry.LocalType)) return;
        if (c.Find(recordId, c.GetMapping(entry.LocalType)) is not LocalEntity parent) return;

        var rows = c.Query<LocalFieldValue>("SELECT * FROM FieldValues WHERE RecordId = ? AND IsDeleted = 0", recordId);
        var values = DynamicDataService.ToJson(rows);
        var snapshot = new JsonObject();
        foreach (var (k, v) in values)
            if (!FormValues.IsEmpty(v)) snapshot[k] = v?.DeepClone();

        ((ILocalDynamic)parent).DynamicData = snapshot.ToJsonString(FormJson.Options);
        c.Update(parent, entry.LocalType);
    }

    public static void RebuildAll(SQLiteConnection c, IEnumerable<(string EntityType, Guid RecordId)> records)
    {
        foreach (var (type, id) in records.Distinct()) Rebuild(c, type, id);
    }
}

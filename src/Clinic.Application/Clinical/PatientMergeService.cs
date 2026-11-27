using System.Reflection;
using System.Text.Json;
using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Application.Sync;
using Clinic.Domain.Auditing;
using Clinic.Domain.Clinical;
using Clinic.Domain.Forms;
using Clinic.Domain.Sync;
using Clinic.Shared.Contracts;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>
/// FR-SYN-32: duplicates registered offline on two devices. The server suggests pairs (same phone, similar name,
/// compatible DOB — the FR-PAT-04 rule); an admin confirms. Every record that points at the duplicate is re-pointed to
/// the survivor through the normal save pipeline, so the change reaches all devices with the next pull; the duplicate
/// becomes a tombstone. Pushes that arrive later for the duplicate are re-pointed by the sync service.
/// </summary>
public sealed class PatientMergeService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    public async Task<IReadOnlyList<DuplicatePairDto>> SuggestAsync(Guid? branchId, CancellationToken ct)
    {
        var q = db.Patients.AsQueryable();
        if (branchId is { } b) q = q.Where(p => p.BranchId == b);
        var patients = await q.Select(p => new Row(p.Id, p.Mrn, p.FullName, p.Gender, p.DateOfBirth, p.Phone, p.CreatedAt)).ToListAsync(ct);
        var visits = (await db.Encounters.GroupBy(e => e.PatientId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.Count);
        PatientBrief Brief(Row p) => new(p.Id, p.Mrn, p.FullName, p.Gender, p.DateOfBirth, p.Phone, p.CreatedAt, visits.GetValueOrDefault(p.Id));

        var pairs = new List<DuplicatePairDto>();
        foreach (var group in patients.Where(p => PatientRules.NormalizePhone(p.Phone).Length >= 7).GroupBy(p => PatientRules.NormalizePhone(p.Phone)))
        {
            var list = group.OrderBy(p => p.CreatedAt).ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var (a, c) = (list[i], list[j]);
                    if (!PatientRules.LooksLikeDuplicate(a.FullName, a.Phone, a.DateOfBirth, c.FullName, c.Phone, c.DateOfBirth)) continue;
                    if (a.Gender != c.Gender && a.Gender != "O" && c.Gender != "O") continue;
                    var reasons = new List<string> { "Same phone number" };
                    reasons.Add(PatientRules.NormalizeName(a.FullName) == PatientRules.NormalizeName(c.FullName) ? "Same name" : "Similar name");
                    if (a.DateOfBirth is { } da && c.DateOfBirth is { } dc && da.Date == dc.Date) reasons.Add("Same date of birth");
                    pairs.Add(new DuplicatePairDto(Brief(a), Brief(c), reasons));
                }
        }
        return pairs;
    }

    public async Task<MergeResultDto> MergeAsync(MergePatientsRequest r, CancellationToken ct)
    {
        if (r.SurvivorId == r.DuplicateId) throw new RequestValidationException("duplicateId", "Choose two different patients.");
        var survivor = await db.Patients.FirstOrDefaultAsync(p => p.Id == r.SurvivorId, ct) ?? throw new NotFoundException("Patient", r.SurvivorId);
        var duplicate = await db.Patients.FirstOrDefaultAsync(p => p.Id == r.DuplicateId, ct) ?? throw new NotFoundException("Patient", r.DuplicateId);

        var moved = new Dictionary<string, int>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1. Every synced record with a PatientId pointing at the duplicate.
        foreach (var d in SyncRegistry.All)
        {
            var prop = d.ClrType.GetProperty("PatientId", BindingFlags.Public | BindingFlags.Instance);
            if (prop is null || d.ClrType == typeof(Patient)) continue;
            var count = await (Task<int>)RepointMethod.MakeGenericMethod(d.ClrType).Invoke(null, [db, prop, duplicate.Id, survivor.Id, ct])!;
            if (count > 0) moved[d.Name] = count;
        }

        // 2. Files and dynamic values attached to the patient record itself.
        var files = await db.Attachments.Where(a => a.RecordId == duplicate.Id && a.EntityType == SyncEntityNames.Patient).ToListAsync(ct);
        foreach (var f in files) f.RecordId = survivor.Id;
        if (files.Count > 0) moved[SyncEntityNames.Attachment] = files.Count;

        var survivorKeys = (await db.FieldValues.Where(v => v.RecordId == survivor.Id && v.EntityType == SyncEntityNames.Patient).Select(v => v.FieldKey).ToListAsync(ct)).ToHashSet();
        var copied = 0;
        foreach (var v in await db.FieldValues.Where(v => v.RecordId == duplicate.Id && v.EntityType == SyncEntityNames.Patient).ToListAsync(ct))
        {
            if (!survivorKeys.Contains(v.FieldKey))
            {
                // Values only the duplicate had (e.g. CNIC captured on the other device) are kept on the survivor.
                db.FieldValues.Add(new FieldValue
                {
                    Id = DeterministicGuid.ForFieldValue(survivor.Id, v.FieldKey), RecordId = survivor.Id, EntityType = v.EntityType, FieldKey = v.FieldKey,
                    FormVersionId = v.FormVersionId, BranchId = survivor.BranchId, ValueKind = v.ValueKind, ValueText = v.ValueText, ValueNumber = v.ValueNumber,
                    ValueDate = v.ValueDate, ValueBool = v.ValueBool, ValueJson = v.ValueJson, RowIndex = v.RowIndex,
                });
                copied++;
            }
            v.IsDeleted = true;
        }

        // 3. Core fields the survivor lacks, then the duplicate becomes a tombstone.
        survivor.Phone ??= duplicate.Phone;
        if (survivor.DateOfBirth is null || (survivor.DobEstimated && !duplicate.DobEstimated && duplicate.DateOfBirth is not null))
        {
            survivor.DateOfBirth = duplicate.DateOfBirth;
            survivor.DobEstimated = duplicate.DobEstimated;
        }
        duplicate.MergedIntoId = survivor.Id;
        duplicate.IsDeleted = true;

        db.PatientMerges.Add(new PatientMerge
        {
            Id = Guid.CreateVersion7(), SurvivorId = survivor.Id, MergedId = duplicate.Id, MergedAt = clock.GetUtcNow().UtcDateTime, MergedBy = user.UserId,
            Reason = Normalize.Optional(r.Reason), SummaryJson = JsonSerializer.Serialize(moved),
        });
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = db.CurrentTenantId, UserId = user.UserId, DeviceId = user.DeviceId, Action = "PatientMerged", EntityType = SyncEntityNames.Patient,
            EntityId = survivor.Id.ToString(), OldValues = JsonSerializer.Serialize(new { duplicate.Id, duplicate.Mrn, duplicate.FullName, duplicate.Phone }),
            NewValues = JsonSerializer.Serialize(new { Survivor = survivor.Mrn, Moved = moved, FieldValuesCopied = copied, r.Reason }),
            Timestamp = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // The survivor's DynamicData snapshot now includes the copied values.
        if (copied > 0 && await db.FieldValues.FirstOrDefaultAsync(v => v.RecordId == survivor.Id && v.EntityType == SyncEntityNames.Patient, ct) is { } any)
            await Forms.FieldValueRules.AfterApplyAsync(new SyncWriteContext(any, false, user, db));
        return new MergeResultDto(survivor.Id, duplicate.Id, moved, copied);
    }

    /// <summary>The survivor of a merged patient (following chains), or null when the patient was never merged.</summary>
    public static async Task<Guid?> SurvivorOfAsync(IAppDbContext db, Guid patientId)
    {
        Guid? current = null;
        var id = patientId;
        for (var hops = 0; hops < 5; hops++)
        {
            var next = await db.PatientMerges.Where(m => m.MergedId == id).Select(m => (Guid?)m.SurvivorId).FirstOrDefaultAsync();
            if (next is null) break;
            current = id = next.Value;
        }
        return current;
    }

    private sealed record Row(Guid Id, string Mrn, string FullName, string Gender, DateTime? DateOfBirth, string? Phone, DateTime CreatedAt);

    private static readonly MethodInfo RepointMethod = typeof(PatientMergeService).GetMethod(nameof(RepointAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static async Task<int> RepointAsync<T>(IAppDbContext db, PropertyInfo prop, Guid from, Guid to, CancellationToken ct) where T : class
    {
        var rows = prop.PropertyType == typeof(Guid?)
            ? await db.Set<T>().Where(e => EF.Property<Guid?>(e, "PatientId") == from).ToListAsync(ct)
            : await db.Set<T>().Where(e => EF.Property<Guid>(e, "PatientId") == from).ToListAsync(ct);
        foreach (var row in rows) prop.SetValue(row, to);
        return rows.Count;
    }
}

/// <summary>FR-SEC-07 audit viewer (online, Audit.View).</summary>
public sealed class AuditService(IAppDbContext db)
{
    public async Task<PagedResult<AuditEntryDto>> SearchAsync(string? entityType, string? entityId, Guid? userId, string? action, DateTime? from, DateTime? to,
        int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var tenant = db.CurrentTenantId;
        var q = db.AuditLogs.Where(a => a.TenantId == tenant);
        if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(a => a.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(entityId)) q = q.Where(a => a.EntityId == entityId);
        if (userId is { } u) q = q.Where(a => a.UserId == u);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
        if (from is { } f) q = q.Where(a => a.Timestamp >= f);
        if (to is { } t) q = q.Where(a => a.Timestamp < t.Date.AddDays(1));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = rows.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).Distinct().ToList();
        var names = await db.Users.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName, ct);
        return new PagedResult<AuditEntryDto>(rows.Select(r => new AuditEntryDto(r.Id, r.Timestamp, r.UserId, r.UserId is { } id ? names.GetValueOrDefault(id) : null,
            r.DeviceId, r.Action, r.EntityType, r.EntityId, r.OldValues, r.NewValues)).ToList(), page, pageSize, total);
    }

    public async Task RecordClientErrorAsync(ClientErrorReport r, ICurrentUser user, CancellationToken ct)
    {
        db.ClientErrors.Add(new ClientError
        {
            Id = Guid.CreateVersion7(), TenantId = user.TenantId, UserId = user.UserId, DeviceId = r.DeviceId ?? user.DeviceId,
            Kind = r.Kind.Length > 40 ? r.Kind[..40] : r.Kind, Message = r.Message.Length > 1000 ? r.Message[..1000] : r.Message,
            Detail = r.Detail is { Length: > 8000 } d ? d[..8000] : r.Detail, AppVersion = r.AppVersion, Platform = r.Platform,
            OccurredAt = r.OccurredAt.UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The AccessEvent sync rule: insert-only, the device's own user, and an audit row per record opened.</summary>
    public static async Task<string?> AuthorizeAccessAsync(SyncWriteContext ctx)
    {
        var e = (AccessEvent)ctx.Entity;
        if (!ctx.IsNew) return "Access events cannot be changed.";
        if (e.UserId != ctx.User.UserId) return "Access is recorded for the signed-in user.";
        if (!await ctx.Db.Branches.AnyAsync(b => b.Id == e.BranchId)) return "Branch not found.";
        return null;
    }

    public static async Task AccessAfterApplyAsync(SyncWriteContext ctx)
    {
        var e = (AccessEvent)ctx.Entity;
        ctx.Db.AuditLogs.Add(new AuditLog
        {
            TenantId = ctx.Db.CurrentTenantId, UserId = e.UserId, DeviceId = ctx.User.DeviceId, Action = $"Read:{e.Action}", EntityType = e.EntityType,
            EntityId = e.RecordId.ToString(), Timestamp = e.At,
        });
        await ctx.Db.SaveChangesAsync();
    }
}

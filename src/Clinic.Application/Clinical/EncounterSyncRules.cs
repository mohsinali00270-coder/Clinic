using Clinic.Application.Abstractions;
using Clinic.Application.Forms;
using Clinic.Application.Sync;
using Clinic.Domain.Clinical;
using Clinic.Shared.Clinical;
using Clinic.Shared.Security;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>
/// FR-ENC-05 and §7.4 on the server: signing locks an encounter; afterwards only its signing doctor may change it,
/// and every change must come with an amendment (reason, user, time) pushed first.
/// </summary>
public static class EncounterSyncRules
{
    public const string DiagnosesKey = "diagnoses";
    public const string PrescriptionKey = "prescription";
    public const string FollowUpKey = "followUpDate";

    // ---------------------------------------------------------------- shared checks

    /// <summary>The caller may write this encounter's clinical content (permission, branch, own records).</summary>
    public static async Task<string?> CanWriteEncounterAsync(IAppDbContext db, ICurrentUser user, Encounter e, string permission)
    {
        if (user.GetScope(permission) is not { } scope) return $"You do not have the {permission} permission.";
        if (scope != PermissionScope.All && !user.BranchIds.Contains(e.BranchId)) return "This encounter belongs to another branch.";
        if (scope == PermissionScope.OwnRecords)
        {
            var mine = await db.DoctorProfiles.AnyAsync(d => d.Id == e.DoctorId && d.UserId == user.UserId);
            if (!mine) return "You can only work on your own encounters.";
        }
        return null;
    }

    /// <summary>
    /// For a locked encounter: the caller must be its signing doctor, and an amendment for this key must match the
    /// change exactly (<paramref name="matches"/> receives each candidate amendment). An earlier amendment of the
    /// same field does not cover a later, silent change.
    /// </summary>
    public static async Task<string?> RequireAmendmentAsync(IAppDbContext db, ICurrentUser user, Encounter e, string key,
        Func<EncounterAmendment, bool>? matches = null)
    {
        if (!EncounterStatus.IsLocked(e.Status)) return null;
        if (e.SignedBy != user.UserId) return "This encounter is signed. Only the signing doctor can amend it.";
        var candidates = await db.EncounterAmendments
            .Where(a => a.EncounterId == e.Id && a.FieldKey == key && a.AmendedBy == user.UserId && a.AmendedAt >= e.SignedAt)
            .ToListAsync();
        return candidates.Any(a => matches?.Invoke(a) ?? true)
            ? null
            : "This encounter is signed. Record an amendment (with a reason) for exactly this change.";
    }

    private static bool TextMatches(EncounterAmendment a, string text, bool deleted) =>
        string.Equals(deleted ? a.OldValue : a.NewValue, text, StringComparison.Ordinal);

    private static async Task<Encounter?> FindEncounterAsync(IAppDbContext db, Guid id) =>
        await db.Encounters.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == id && e.TenantId == db.CurrentTenantId && !e.IsDeleted);

    // ---------------------------------------------------------------- encounters

    public static string? ValidateEncounter(object o)
    {
        var e = (Encounter)o;
        if (!EncounterStatus.All.Contains(e.Status)) return $"Unknown status '{e.Status}'.";
        if (e.Specialty.Length > 50) return "Specialty code is too long.";
        if (EncounterStatus.IsLocked(e.Status) && (e.SignedAt is null || e.SignedBy is null)) return "A signed encounter needs the signing time and doctor.";
        return null;
    }

    public static async Task<string?> AuthorizeEncounterAsync(SyncWriteContext ctx)
    {
        var e = (Encounter)ctx.Entity;
        var db = ctx.Db;

        if (!await db.Patients.AnyAsync(p => p.Id == e.PatientId)) return "Patient not found on the server.";
        if (!await db.DoctorProfiles.AnyAsync(d => d.Id == e.DoctorId)) return "Doctor not found.";
        if (await CanWriteEncounterAsync(db, ctx.User, e, Permissions.Encounters.Edit) is { } denied) return denied;
        if (await CareSyncRules.CheckCaseLinkAsync(ctx, e, e.CaseId, e.PatientId) is { } caseError) return caseError;

        if (ctx.IsNew)
            return EncounterStatus.IsLocked(e.Status) && await SignCheckAsync(ctx, e) is { } signError ? signError : null;

        var entry = db.Entry(e);
        var originalStatus = (string)entry.Property(nameof(Encounter.Status)).OriginalValue!;
        var changed = entry.Properties
            .Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue))
            .Select(p => p.Metadata.Name)
            .Where(n => n is not (nameof(Encounter.UpdatedAt) or nameof(Encounter.UpdatedBy)))
            .ToList();

        if (EncounterStatus.IsLocked(originalStatus))
        {
            // Locked: only the status may move to "amended", and the follow-up date may change through an amendment.
            // The case link is grouping, not clinical content, so a signed visit can still be filed under a case.
            var allowed = new HashSet<string> { nameof(Encounter.Status), nameof(Encounter.FollowUpDate), nameof(Encounter.CaseId) };
            if (changed.Any(c => !allowed.Contains(c)))
                return "This encounter is signed and cannot be edited. Record an amendment instead.";
            if (changed.Contains(nameof(Encounter.Status)) && e.Status != EncounterStatus.Amended)
                return "A signed encounter can only become 'amended'.";

            var original = new Encounter { Id = e.Id, Status = originalStatus, SignedBy = e.SignedBy, SignedAt = e.SignedAt };
            if (changed.Contains(nameof(Encounter.FollowUpDate)) && await RequireAmendmentAsync(db, ctx.User, original, FollowUpKey) is { } f) return f;
            if (changed.Any(c => c != nameof(Encounter.CaseId)) && e.SignedBy != ctx.User.UserId) return "Only the signing doctor can amend this encounter.";
            if (changed.Contains(nameof(Encounter.Status)) &&
                !await db.EncounterAmendments.AnyAsync(a => a.EncounterId == e.Id && a.AmendedBy == ctx.User.UserId))
                return "Record at least one amendment before marking the encounter amended.";
            return null;
        }

        if (EncounterStatus.IsLocked(e.Status)) return await SignCheckAsync(ctx, e);
        return null;
    }

    /// <summary>FR-ENC-05 / FR-DOC-07: signing needs Encounters.Sign, the signer's own identity, and a valid licence.</summary>
    private static async Task<string?> SignCheckAsync(SyncWriteContext ctx, Encounter e)
    {
        if (await CanWriteEncounterAsync(ctx.Db, ctx.User, e, Permissions.Encounters.Sign) is { } denied) return denied;
        if (e.SignedBy != ctx.User.UserId) return "An encounter can only be signed by the signed-in doctor.";
        var doctor = await ctx.Db.DoctorProfiles.FirstAsync(d => d.Id == e.DoctorId);
        if (doctor.LicenceExpiry is { } exp && exp.Date < DateTime.Today)
            return $"{doctor.FullName}'s licence expired on {exp:dd MMM yyyy}; the encounter cannot be signed.";
        return null;
    }

    // ---------------------------------------------------------------- vitals

    public static async Task<string?> AuthorizeVitalsAsync(SyncWriteContext ctx)
    {
        var v = (VitalsRecord)ctx.Entity;
        if (ctx.User.GetScope(Permissions.Vitals.Record) is not { } scope) return "You do not have the Vitals.Record permission.";
        if (scope != PermissionScope.All && !ctx.User.BranchIds.Contains(v.BranchId)) return "You can only record vitals in your own branches.";
        if (!await ctx.Db.Patients.AnyAsync(p => p.Id == v.PatientId)) return "Patient not found on the server.";
        if (v.EncounterId is { } eid && await FindEncounterAsync(ctx.Db, eid) is { } enc && EncounterStatus.IsLocked(enc.Status) && ctx.IsNew)
            return "The encounter is already signed.";
        return await FieldValueRules.CheckFormVersionAsync(ctx.Db, v.FormVersionId, SyncEntityNames.Vitals);
    }

    // ---------------------------------------------------------------- diagnoses, prescriptions, amendments

    public static string? ValidateDiagnosis(object o)
    {
        var d = (Diagnosis)o;
        if (string.IsNullOrWhiteSpace(d.Description) || d.Description.Length > 300) return "Diagnosis text is required (max 300).";
        if (d.Code?.Length > 10) return "ICD-10 code is too long.";
        return null;
    }

    public static async Task<string?> AuthorizeDiagnosisAsync(SyncWriteContext ctx)
    {
        var d = (Diagnosis)ctx.Entity;
        var e = await FindEncounterAsync(ctx.Db, d.EncounterId);
        if (e is null) return "Encounter not found on the server.";
        d.BranchId = e.BranchId;
        d.PatientId = e.PatientId;
        var text = AmendmentText.Diagnosis(d.Code, d.Description);
        if (await CanWriteEncounterAsync(ctx.Db, ctx.User, e, Permissions.Encounters.Edit) is { } denied) return denied;
        return ctx.Stage == SyncStage.BeforeChange
            ? null // values are matched on the final state
            : await RequireAmendmentAsync(ctx.Db, ctx.User, e, DiagnosesKey, a => TextMatches(a, text, ctx.Stage == SyncStage.Delete));
    }

    public static async Task<string?> AuthorizePrescriptionAsync(SyncWriteContext ctx)
    {
        var p = (Prescription)ctx.Entity;
        var e = await FindEncounterAsync(ctx.Db, p.EncounterId);
        if (e is null) return "Encounter not found on the server.";
        p.BranchId = e.BranchId;
        p.PatientId = e.PatientId;
        p.DoctorId = e.DoctorId;
        if (await CanWriteEncounterAsync(ctx.Db, ctx.User, e, Permissions.Prescriptions.Create) is { } denied) return denied;
        return ctx.Stage == SyncStage.BeforeChange ? null : await RequireAmendmentAsync(ctx.Db, ctx.User, e, PrescriptionKey);
    }

    public static string? ValidatePrescriptionItem(object o)
    {
        var i = (PrescriptionItem)o;
        if (string.IsNullOrWhiteSpace(i.DrugName) || i.DrugName.Length > 200) return "Medicine name is required.";
        if (i.Frequency is { } f && Frequencies.Find(f) is null) return $"Unknown frequency '{f}'.";
        if (i.DurationDays is < 0 or > 365) return "Duration must be 0–365 days.";
        if (i.Instructions?.Length > 500) return "Instructions are too long.";
        return null;
    }

    public static async Task<string?> AuthorizePrescriptionItemAsync(SyncWriteContext ctx)
    {
        var item = (PrescriptionItem)ctx.Entity;
        var rx = await ctx.Db.Prescriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == item.PrescriptionId && p.TenantId == ctx.Db.CurrentTenantId);
        if (rx is null) return "Prescription not found on the server.";
        var e = await FindEncounterAsync(ctx.Db, rx.EncounterId);
        if (e is null) return "Encounter not found on the server.";
        item.BranchId = e.BranchId;
        var text = AmendmentText.PrescriptionItem(item.DrugName, item.Strength, item.Dose, item.Frequency, item.DurationDays);
        if (await CanWriteEncounterAsync(ctx.Db, ctx.User, e, Permissions.Prescriptions.Create) is { } denied) return denied;
        return ctx.Stage == SyncStage.BeforeChange
            ? null
            : await RequireAmendmentAsync(ctx.Db, ctx.User, e, PrescriptionKey, a => TextMatches(a, text, ctx.Stage == SyncStage.Delete));
    }

    public static string? ValidateAmendment(object o)
    {
        var a = (EncounterAmendment)o;
        if (string.IsNullOrWhiteSpace(a.Reason) || a.Reason.Length > 500) return "An amendment needs a reason (max 500).";
        if (string.IsNullOrWhiteSpace(a.FieldKey)) return "An amendment needs the changed field.";
        return null;
    }

    public static async Task<string?> AuthorizeAmendmentAsync(SyncWriteContext ctx)
    {
        var a = (EncounterAmendment)ctx.Entity;
        if (!ctx.IsNew) return "Amendments cannot be edited.";
        var e = await FindEncounterAsync(ctx.Db, a.EncounterId);
        if (e is null) return "Encounter not found on the server.";
        if (!EncounterStatus.IsLocked(e.Status)) return "Only signed encounters are amended; edit the draft directly.";
        if (e.SignedBy != ctx.User.UserId || a.AmendedBy != ctx.User.UserId) return "Only the signing doctor can amend this encounter.";
        a.BranchId = e.BranchId;
        return null;
    }

    public static async Task<string?> AuthorizeTemplateAsync(SyncWriteContext ctx)
    {
        var t = (PrescriptionTemplate)ctx.Entity;
        if (ctx.User.GetScope(Permissions.Prescriptions.Create) is null) return "You do not have the Prescriptions.Create permission.";
        if (string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 100) return "Template name is required (max 100).";
        var mine = await ctx.Db.DoctorProfiles.AnyAsync(d => d.Id == t.DoctorId && d.UserId == ctx.User.UserId);
        return mine ? null : "Templates belong to a doctor; you can only save your own.";
    }

    /// <summary>FieldValues of a locked encounter need an amendment whose new value equals the pushed value.</summary>
    public static async Task<string?> CheckLockedParentAsync(IAppDbContext db, ICurrentUser user, object parent, Domain.Forms.FieldValue fv)
    {
        if (parent is not Encounter e) return null;
        var pushed = Clinic.Shared.Forms.FieldValueCodec.Decode(fv.ValueKind, fv.ValueText, fv.ValueNumber, fv.ValueDate, fv.ValueBool, fv.ValueJson);
        return await RequireAmendmentAsync(db, user, e, fv.FieldKey, a =>
            FieldValueRules.SameValue(a.NewValue is null ? null : System.Text.Json.Nodes.JsonNode.Parse(a.NewValue), pushed));
    }

    /// <summary>§7.4: clinical values are never overwritten automatically.</summary>
    public static ConflictPolicy FieldValuePolicy(object fieldValue) =>
        ((Domain.Forms.FieldValue)fieldValue).EntityType is SyncEntityNames.Encounter or SyncEntityNames.Vitals
            or SyncEntityNames.Case or SyncEntityNames.Procedure or SyncEntityNames.LabResult
            ? ConflictPolicy.Manual
            : ConflictPolicy.LastWriterWins;
}

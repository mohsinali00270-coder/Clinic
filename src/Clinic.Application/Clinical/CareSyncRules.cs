using System.Text.Json;
using Clinic.Application.Abstractions;
using Clinic.Application.Forms;
using Clinic.Application.Sync;
using Clinic.Domain.Clinical;
using Clinic.Domain.Sync;
using Clinic.Shared.Clinical;
using Clinic.Shared.Security;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>
/// Server re-checks (FR-SYN-13) for cases, referrals, procedures and day care (§4.10–4.12). Child rows take their
/// branch from their parent, so a device cannot move a row into another branch by sending a different BranchId.
/// </summary>
public static class CareSyncRules
{
    // ---------------------------------------------------------------- helpers

    private static async Task<T?> FindAsync<T>(IAppDbContext db, Guid id) where T : SyncEntity =>
        await db.Set<T>().IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == id && e.TenantId == db.CurrentTenantId && !e.IsDeleted);

    private static bool Changed<T>(SyncWriteContext ctx, T entity, string property) where T : class
    {
        if (ctx.IsNew) return true;
        var p = ctx.Db.Entry(entity).Property(property);
        return !Equals(p.OriginalValue, p.CurrentValue);
    }

    private static TValue Original<T, TValue>(SyncWriteContext ctx, T entity, string property) where T : class =>
        ctx.IsNew ? (TValue)ctx.Db.Entry(entity).Property(property).CurrentValue! : (TValue)ctx.Db.Entry(entity).Property(property).OriginalValue!;

    /// <summary>FR-CASE-03: a link to a case must point at an open case of the same patient.</summary>
    public static async Task<string?> CheckCaseLinkAsync(SyncWriteContext ctx, object entity, Guid? caseId, Guid patientId, string property = "CaseId")
    {
        if (caseId is not { } id) return null;
        var c = await FindAsync<PatientCase>(ctx.Db, id);
        if (c is null) return "The case was not found on the server.";
        if (c.PatientId != patientId) return "The case belongs to another patient.";
        if (CaseStatus.IsFinished(c.Status) && Changed(ctx, entity, property)) return "That case is closed. Reopen it before linking records to it.";
        return null;
    }

    private static string? Length(string? value, int max, string label) =>
        value is { } v && v.Length > max ? $"{label} must be {max} characters or fewer." : null;

    // ---------------------------------------------------------------- cases (FR-CASE-01…08)

    public static string? ValidateCase(object o)
    {
        var c = (PatientCase)o;
        if (!CaseStatus.All.Contains(c.Status)) return $"Unknown case status '{c.Status}'.";
        if (string.IsNullOrWhiteSpace(c.Title)) return "A case needs a title.";
        if (c.PlannedSessions is < 0 or > 500) return "Planned sessions must be 0–500.";
        if (c.FollowUpIntervalDays is < 0 or > 730) return "Follow-up interval must be 0–730 days.";
        if (CaseStatus.IsFinished(c.Status))
        {
            if (c.ClosedAt is null) return "A closed case needs the closing date.";
            if (c.Status == CaseStatus.Closed && string.IsNullOrWhiteSpace(c.Outcome)) return "Closing a case needs an outcome (FR-CASE-07).";
        }
        return Length(c.Title, 200, "Title") ?? Length(c.ClosingSummary, 4000, "Closing summary") ?? Length(c.Outcome, 50, "Outcome");
    }

    public static async Task<string?> AuthorizeCaseAsync(SyncWriteContext ctx)
    {
        var c = (PatientCase)ctx.Entity;
        var db = ctx.Db;
        if (await SyncRegistry.AuthorizeBranchScopedAsync(ctx, c.BranchId, Permissions.Cases.Manage) is { } denied) return denied;
        if (!await db.Patients.AnyAsync(p => p.Id == c.PatientId)) return "Patient not found on the server.";

        var type = await FindAsync<CaseType>(db, c.CaseTypeId);
        if (type is null) return "Case type not found. Sync to get the latest case types.";
        if (ctx.IsNew && !type.IsActive) return $"Case type '{type.Name}' is no longer in use.";
        c.CaseTypeCode = type.Code;

        if (c.PrimaryDoctorId is { } doc && !await db.DoctorProfiles.AnyAsync(d => d.Id == doc)) return "Primary doctor not found.";
        if (!ctx.IsNew && Changed(ctx, c, nameof(PatientCase.PatientId))) return "A case cannot be moved to another patient.";

        if (c.Outcome is { } outcome && !await db.LookupItems.AnyAsync(i => i.ListKey == "case_outcomes" && i.Code == outcome))
            return $"Outcome '{outcome}' is not in the case outcomes list.";

        return await FieldValueRules.CheckFormVersionAsync(db, c.FormVersionId, SyncEntityNames.Case);
    }

    /// <summary>Plan steps, team and notes: the case must exist and be open; branch comes from the case.</summary>
    private static async Task<string?> AuthorizeCaseChildAsync(SyncWriteContext ctx, Guid caseId, Action<PatientCase> stamp)
    {
        var c = await FindAsync<PatientCase>(ctx.Db, caseId);
        if (c is null) return "The case was not found on the server.";
        stamp(c);
        if (await SyncRegistry.AuthorizeBranchScopedAsync(ctx, c.BranchId, Permissions.Cases.Manage) is { } denied) return denied;
        if (CaseStatus.IsFinished(c.Status)) return "This case is closed. Reopen it to change its plan, team or notes.";
        return null;
    }

    public static string? ValidateStep(object o)
    {
        var s = (CarePlanStep)o;
        if (!CarePlanStepKind.All.Contains(s.Kind)) return $"Unknown step kind '{s.Kind}'.";
        if (!CarePlanStepStatus.All.Contains(s.Status)) return $"Unknown step status '{s.Status}'.";
        if (string.IsNullOrWhiteSpace(s.Title)) return "A care-plan step needs a title.";
        if (s.Status == CarePlanStepStatus.Done && s.CompletedAt is null) return "A completed step needs the completion time.";
        return Length(s.Title, 200, "Title") ?? Length(s.Notes, 500, "Notes");
    }

    public static Task<string?> AuthorizeStepAsync(SyncWriteContext ctx)
    {
        var s = (CarePlanStep)ctx.Entity;
        return AuthorizeCaseChildAsync(ctx, s.CaseId, c => s.BranchId = c.BranchId);
    }

    public static string? ValidateMember(object o)
    {
        var m = (CaseMember)o;
        if (!CaseMemberRole.All.Contains(m.Role)) return $"Unknown team role '{m.Role}'.";
        if (string.IsNullOrWhiteSpace(m.Name)) return "A team member needs a name.";
        return Length(m.Name, 150, "Name");
    }

    public static async Task<string?> AuthorizeMemberAsync(SyncWriteContext ctx)
    {
        var m = (CaseMember)ctx.Entity;
        if (await AuthorizeCaseChildAsync(ctx, m.CaseId, c => m.BranchId = c.BranchId) is { } e) return e;
        if (m.DoctorId is { } d && !await ctx.Db.DoctorProfiles.AnyAsync(x => x.Id == d)) return "Doctor not found.";
        return null;
    }

    public static string? ValidateCaseNote(object o)
    {
        var n = (CaseNote)o;
        if (string.IsNullOrWhiteSpace(n.Body)) return "The note is empty.";
        return Length(n.Body, 4000, "Note") ?? Length(n.AuthorName, 150, "Author");
    }

    public static async Task<string?> AuthorizeCaseNoteAsync(SyncWriteContext ctx)
    {
        var n = (CaseNote)ctx.Entity;
        if (await AuthorizeCaseChildAsync(ctx, n.CaseId, c => n.BranchId = c.BranchId) is { } e) return e;
        if (!ctx.IsNew && n.WrittenBy != ctx.User.UserId) return "Only the author can change a case note.";
        return null;
    }

    // ---------------------------------------------------------------- referrals (FR-REF-01…03)

    public static string? ValidateReferral(object o)
    {
        var r = (Referral)o;
        if (!ReferralDirection.All.Contains(r.Direction)) return $"Unknown referral direction '{r.Direction}'.";
        if (!ReferralStatus.All.Contains(r.Status)) return $"Unknown referral status '{r.Status}'.";
        if (!ReferralPriority.All.Contains(r.Priority)) return $"Unknown priority '{r.Priority}'.";
        if (string.IsNullOrWhiteSpace(r.Reason)) return "A referral needs a reason.";
        switch (r.Direction)
        {
            case ReferralDirection.Internal when r.ToDoctorId is null && string.IsNullOrWhiteSpace(r.ToSpecialty):
                return "Choose the doctor or specialty to refer to.";
            case ReferralDirection.External when string.IsNullOrWhiteSpace(r.ExternalFacility):
                return "Enter the hospital, specialist or centre the patient is referred to.";
            case ReferralDirection.Incoming when string.IsNullOrWhiteSpace(r.SourceCode) && string.IsNullOrWhiteSpace(r.SourceName):
                return "Record who referred the patient.";
        }
        return Length(r.Reason, 1000, "Reason") ?? Length(r.Summary, 4000, "Summary") ?? Length(r.ResponseNote, 1000, "Response")
               ?? Length(r.ExternalFacility, 200, "Facility") ?? Length(r.SourceName, 200, "Referrer");
    }

    public static async Task<string?> AuthorizeReferralAsync(SyncWriteContext ctx)
    {
        var r = (Referral)ctx.Entity;
        var db = ctx.Db;
        if (ctx.User.GetScope(Permissions.Referrals.Manage) is not { } scope) return "You do not have the Referrals.Manage permission.";
        if (!await db.Branches.AnyAsync(b => b.Id == r.BranchId)) return "Branch not found.";
        if (scope != PermissionScope.All && !ctx.User.BranchIds.Contains(r.BranchId)) return "You can only refer patients of your own branches.";
        if (!await db.Patients.AnyAsync(p => p.Id == r.PatientId)) return "Patient not found on the server.";
        if (!ctx.IsNew && Changed(ctx, r, nameof(Referral.Direction))) return "The direction of a referral cannot change.";

        DoctorProfile? to = null;
        if (r.ToDoctorId is { } toId && (to = await db.DoctorProfiles.FirstOrDefaultAsync(d => d.Id == toId)) is null) return "Receiving doctor not found.";
        if (r.FromDoctorId is { } fromId && !await db.DoctorProfiles.AnyAsync(d => d.Id == fromId)) return "Referring doctor not found.";
        if (r.EncounterId is { } eid && (await FindAsync<Encounter>(db, eid))?.PatientId != r.PatientId) return "The encounter was not found for this patient.";
        if (await CheckCaseLinkAsync(ctx, r, r.CaseId, r.PatientId) is { } caseError) return caseError;
        if (r.SourceCode is { } src && !await db.LookupItems.AnyAsync(i => (i.ListKey == "referrers" || i.ListKey == "referral_source") && i.Code == src))
            return $"Referrer '{src}' is not in the referrers list.";

        // FR-REF-01: the receiving doctor accepts, declines or completes an internal referral addressed to them.
        if (!ctx.IsNew && Changed(ctx, r, nameof(Referral.Status)) && r.Direction == ReferralDirection.Internal &&
            r.Status is ReferralStatus.Accepted or ReferralStatus.Declined or ReferralStatus.Completed &&
            to?.UserId is { } receiver && receiver != ctx.User.UserId && scope != PermissionScope.All)
            return $"Only {to.FullName} can respond to this referral.";
        if (!ctx.IsNew && Changed(ctx, r, nameof(Referral.Status)) && r.Status == ReferralStatus.Cancelled &&
            r.ReferredBy != ctx.User.UserId && scope != PermissionScope.All)
            return "Only the referring user can cancel a referral.";
        if (!ctx.IsNew && !ReferralStatus.IsOpen(Original<Referral, string>(ctx, r, nameof(Referral.Status))) && Changed(ctx, r, nameof(Referral.Status)))
            return "This referral is already finished.";
        return null;
    }

    // ---------------------------------------------------------------- procedures (FR-PRC-01/02)

    public static string? ValidateProcedure(object o)
    {
        var p = (ProcedureRecord)o;
        if (!ProcedureStatus.All.Contains(p.Status)) return $"Unknown procedure status '{p.Status}'.";
        if (string.IsNullOrWhiteSpace(p.Name)) return "A procedure needs a name.";
        if (p.Fee is < 0) return "The fee cannot be negative.";
        if (p.ConsentRelation is { } rel && !ConsentRelation.All.Contains(rel)) return $"Unknown consent relation '{rel}'.";
        if (p.ConsentSignedAt is not null && string.IsNullOrWhiteSpace(p.ConsentSignerName)) return "A signed consent needs the signer's name.";
        if (p.Status == ProcedureStatus.Done && p.CompletedAt is null) return "A completed procedure needs the completion time.";
        try { JsonSerializer.Deserialize<List<Consumable>>(p.ConsumablesJson, Clinic.Shared.Forms.FormJson.Options); }
        catch (JsonException) { return "The consumables list is not valid."; }
        return Length(p.Name, 150, "Name") ?? Length(p.CancelReason, 300, "Cancel reason") ?? Length(p.ConsentSignerName, 150, "Signer");
    }

    public static async Task<string?> AuthorizeProcedureAsync(SyncWriteContext ctx)
    {
        var p = (ProcedureRecord)ctx.Entity;
        var db = ctx.Db;
        if (await SyncRegistry.AuthorizeBranchScopedAsync(ctx, p.BranchId, Permissions.Procedures.Perform) is { } denied) return denied;
        if (!await db.Patients.AnyAsync(x => x.Id == p.PatientId)) return "Patient not found on the server.";

        var type = await FindAsync<ProcedureType>(db, p.ProcedureTypeId);
        if (type is null) return "Procedure type not found. Sync to get the latest procedure list.";
        // FR-PRC-02: whether consent is needed is the master's decision, not the device's.
        p.ConsentRequired = type.RequiresConsent;
        p.ProcedureCode = type.Code;

        if (p.EncounterId is { } eid && (await FindAsync<Encounter>(db, eid))?.PatientId != p.PatientId) return "The encounter was not found for this patient.";
        if (p.AdmissionId is { } aid && (await FindAsync<Admission>(db, aid))?.PatientId != p.PatientId) return "The admission was not found for this patient.";
        if (p.DoctorId is { } did && !await db.DoctorProfiles.AnyAsync(d => d.Id == did)) return "Doctor not found.";
        if (await CheckCaseLinkAsync(ctx, p, p.CaseId, p.PatientId) is { } caseError) return caseError;

        if (p.ConsentAttachmentId is { } att &&
            !await db.Attachments.AnyAsync(a => a.Id == att && a.RecordId == p.Id && a.EntityType == SyncEntityNames.Procedure))
            return "The consent signature was not found. Sync again after the signature is saved.";
        if (p.ConsentRequired && ProcedureStatus.NeedsConsent(p.Status) && (p.ConsentSignedAt is null || p.ConsentAttachmentId is null))
            return $"{type.Name} needs a signed consent before it starts (FR-PRC-02).";

        if (!ctx.IsNew && Original<ProcedureRecord, string>(ctx, p, nameof(ProcedureRecord.Status)) is ProcedureStatus.Done or ProcedureStatus.Cancelled &&
            Changed(ctx, p, nameof(ProcedureRecord.Status)))
            return "This procedure is already finished.";

        return await FieldValueRules.CheckFormVersionAsync(db, p.FormVersionId, SyncEntityNames.Procedure);
    }

    // ---------------------------------------------------------------- day care (FR-PRC-03/04)

    public static string? ValidateAdmission(object o)
    {
        var a = (Admission)o;
        if (!AdmissionStatus.All.Contains(a.Status)) return $"Unknown admission status '{a.Status}'.";
        if (a.Status == AdmissionStatus.Discharged)
        {
            if (a.DischargedAt is null) return "A discharge needs the discharge time.";
            if (string.IsNullOrWhiteSpace(a.DischargeSummary)) return "A discharge needs a discharge summary.";
            if (a.DischargedAt < a.AdmittedAt) return "Discharge cannot be before admission.";
        }
        return Length(a.Reason, 500, "Reason") ?? Length(a.DischargeSummary, 4000, "Discharge summary") ?? Length(a.DischargeAdvice, 2000, "Advice");
    }

    public static async Task<string?> AuthorizeAdmissionAsync(SyncWriteContext ctx)
    {
        var a = (Admission)ctx.Entity;
        var db = ctx.Db;
        if (await SyncRegistry.AuthorizeBranchScopedAsync(ctx, a.BranchId, Permissions.Admissions.Manage) is { } denied) return denied;
        if (!await db.Patients.AnyAsync(x => x.Id == a.PatientId)) return "Patient not found on the server.";
        if (await CheckCaseLinkAsync(ctx, a, a.CaseId, a.PatientId) is { } caseError) return caseError;

        var bed = await FindAsync<Bed>(db, a.BedId);
        if (bed is null || bed.BranchId != a.BranchId) return "That bed does not exist in this branch.";
        if (ctx.IsNew && !bed.IsActive) return $"Bed {bed.Code} is out of use.";
        if (ctx.IsNew) return null;

        if (Changed(ctx, a, nameof(Admission.PatientId)) || Changed(ctx, a, nameof(Admission.BranchId)))
            return "An admission cannot move to another patient or branch.";

        var originalStatus = Original<Admission, string>(ctx, a, nameof(Admission.Status));
        if (originalStatus != AdmissionStatus.Admitted)
        {
            var allowed = new[] { nameof(Admission.DischargeSummary), nameof(Admission.DischargeAdvice) };
            var entry = db.Entry(a);
            var changed = entry.Properties.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)).Select(p => p.Metadata.Name)
                .Where(n => n is not (nameof(Admission.UpdatedAt) or nameof(Admission.UpdatedBy) or nameof(Admission.BedConflict)));
            if (changed.Any(c => !allowed.Contains(c))) return "This patient has been discharged; only the discharge summary can still be edited.";
        }

        // Room charges follow the bed history, so a bed change needs its transfer row.
        if (Changed(ctx, a, nameof(Admission.BedId)) &&
            !await db.BedTransfers.AnyAsync(t => t.AdmissionId == a.Id && t.ToBedId == a.BedId))
            return "Record a bed transfer to move the patient.";
        return null;
    }

    /// <summary>
    /// Two offline devices may put different patients in the same bed. Both admissions are kept (nothing clinical is
    /// lost) and the later one is flagged, like a double-booked slot (Phase 4).
    /// </summary>
    public static async Task AdmissionAfterApplyAsync(SyncWriteContext ctx)
    {
        var a = (Admission)ctx.Entity;
        var conflict = a.Status == AdmissionStatus.Admitted && await ctx.Db.Admissions.AnyAsync(x =>
            x.Id != a.Id && x.BedId == a.BedId && x.Status == AdmissionStatus.Admitted && !x.BedConflict && x.AdmittedAt <= a.AdmittedAt);
        if (a.BedConflict != conflict)
        {
            a.BedConflict = conflict;
            await ctx.Db.SaveChangesAsync();
        }
    }

    private static async Task<(Admission? Admission, string? Error)> ActiveAdmissionAsync(SyncWriteContext ctx, Guid admissionId, string permission, bool requireActive)
    {
        var a = await FindAsync<Admission>(ctx.Db, admissionId);
        if (a is null) return (null, "The admission was not found on the server.");
        if (await SyncRegistry.AuthorizeBranchScopedAsync(ctx, a.BranchId, permission) is { } denied) return (a, denied);
        if (requireActive && a.Status != AdmissionStatus.Admitted) return (a, "This patient has already been discharged.");
        return (a, null);
    }

    public static async Task<string?> AuthorizeTransferAsync(SyncWriteContext ctx)
    {
        var t = (BedTransfer)ctx.Entity;
        if (!ctx.IsNew) return "Bed transfers cannot be edited.";
        var (a, error) = await ActiveAdmissionAsync(ctx, t.AdmissionId, Permissions.Admissions.Manage, requireActive: true);
        if (error is not null) return error;
        t.BranchId = a!.BranchId;
        if (t.FromBedId != a.BedId) return "The patient is no longer in that bed; sync and try again.";
        if (t.ToBedId == t.FromBedId) return "Choose a different bed.";
        var bed = await FindAsync<Bed>(ctx.Db, t.ToBedId);
        if (bed is null || bed.BranchId != a.BranchId || !bed.IsActive) return "That bed is not available in this branch.";
        if (t.MovedAt < a.AdmittedAt) return "A transfer cannot be before the admission.";
        return null;
    }

    public static string? ValidateNursingNote(object o)
    {
        var n = (NursingNote)o;
        if (!NursingNoteKind.All.Contains(n.Kind)) return $"Unknown note kind '{n.Kind}'.";
        if (string.IsNullOrWhiteSpace(n.Body)) return "The note is empty.";
        return Length(n.Body, 4000, "Note");
    }

    public static async Task<string?> AuthorizeNursingNoteAsync(SyncWriteContext ctx)
    {
        var n = (NursingNote)ctx.Entity;
        var (a, error) = await ActiveAdmissionAsync(ctx, n.AdmissionId, Permissions.Nursing.Record, requireActive: ctx.IsNew);
        if (error is not null) return error;
        n.BranchId = a!.BranchId;
        if (!ctx.IsNew && n.WrittenBy != ctx.User.UserId) return "Only the author can change a nursing note.";
        return null;
    }

    public static string? ValidateMedicationOrder(object o)
    {
        var m = (MedicationOrder)o;
        if (string.IsNullOrWhiteSpace(m.DrugName)) return "Medicine name is required.";
        if (!MedicationOrderStatus.All.Contains(m.Status)) return $"Unknown order status '{m.Status}'.";
        if (m.Frequency is { } f && Frequencies.Find(f) is null) return $"Unknown frequency '{f}'.";
        if (m.StopAt is { } stop && stop < m.StartAt) return "The stop time is before the start time.";
        return Length(m.DrugName, 200, "Medicine") ?? Length(m.Instructions, 500, "Instructions");
    }

    /// <summary>Doctors write orders (Prescriptions.Create); stopping one is also open to whoever runs the stay (discharge).</summary>
    public static async Task<string?> AuthorizeMedicationOrderAsync(SyncWriteContext ctx)
    {
        var m = (MedicationOrder)ctx.Entity;
        var onlyStops = !ctx.IsNew && ctx.Db.Entry(m).Properties
            .Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue))
            .All(p => p.Metadata.Name is nameof(MedicationOrder.Status) or nameof(MedicationOrder.StopAt)
                or nameof(MedicationOrder.UpdatedAt) or nameof(MedicationOrder.UpdatedBy));
        var permission = onlyStops && ctx.User.GetScope(Permissions.Prescriptions.Create) is null
            ? Permissions.Admissions.Manage
            : Permissions.Prescriptions.Create;
        if (ctx.User.GetScope(permission) is null) return $"You do not have the {permission} permission.";
        if (onlyStops && ctx.Stage == SyncStage.Final && m.Status != MedicationOrderStatus.Stopped) return "Only a doctor can restart a medication order.";
        var (a, error) = await ActiveAdmissionAsync(ctx, m.AdmissionId, permission, requireActive: ctx.IsNew);
        if (error is not null) return error;
        m.BranchId = a!.BranchId;
        return null;
    }

    public static string? ValidateDose(object o)
    {
        var d = (MedicationDose)o;
        if (!MedicationDoseStatus.All.Contains(d.Status)) return $"Unknown dose status '{d.Status}'.";
        return Length(d.Note, 500, "Note");
    }

    public static async Task<string?> AuthorizeDoseAsync(SyncWriteContext ctx)
    {
        var d = (MedicationDose)ctx.Entity;
        var order = await FindAsync<MedicationOrder>(ctx.Db, d.OrderId);
        if (order is null) return "The medication order was not found on the server.";
        d.AdmissionId = order.AdmissionId;
        var (a, error) = await ActiveAdmissionAsync(ctx, order.AdmissionId, Permissions.Nursing.Record, requireActive: ctx.IsNew);
        if (error is not null) return error;
        d.BranchId = a!.BranchId;
        if (ctx.IsNew && order.Status != MedicationOrderStatus.Active) return $"{order.DrugName} has been stopped.";
        if (!ctx.IsNew && d.GivenBy != ctx.User.UserId) return "Only the nurse who charted a dose can correct it.";
        return null;
    }
}

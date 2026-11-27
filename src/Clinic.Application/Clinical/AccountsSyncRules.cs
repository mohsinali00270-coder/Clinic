using System.Text.Json;
using Clinic.Application.Abstractions;
using Clinic.Application.Forms;
using Clinic.Application.Sync;
using Clinic.Domain.Clinical;
using Clinic.Domain.Sync;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Security;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>Server re-checks (FR-SYN-13) for packages, panels, credit notes, expenses, messages and feedback (§4.13–4.15).</summary>
public static class AccountsSyncRules
{
    private static async Task<T?> FindAsync<T>(IAppDbContext db, Guid id) where T : SyncEntity =>
        await db.Set<T>().IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == id && e.TenantId == db.CurrentTenantId && !e.IsDeleted);

    private static bool Changed(SyncWriteContext ctx, object entity, string property)
    {
        if (ctx.IsNew) return true;
        var p = ctx.Db.Entry(entity).Property(property);
        return !Equals(p.OriginalValue, p.CurrentValue);
    }

    private static T Original<T>(SyncWriteContext ctx, object entity, string property)
    {
        var p = ctx.Db.Entry(entity).Property(property);
        return (T)(ctx.IsNew ? p.CurrentValue : p.OriginalValue)!;
    }

    private static async Task<string?> ScopeAsync(SyncWriteContext ctx, Guid? branchId, string permission)
    {
        if (ctx.User.GetScope(permission) is not { } scope) return $"You do not have the {permission} permission.";
        if (branchId is not { } b) return null;
        if (!await ctx.Db.Branches.AnyAsync(x => x.Id == b)) return "Branch not found.";
        return scope != PermissionScope.All && !ctx.User.BranchIds.Contains(b) ? "You can only work in your own branches." : null;
    }

    private static string? Length(string? value, int max, string label) =>
        value is { } v && v.Length > max ? $"{label} must be {max} characters or fewer." : null;

    public static List<PackageItem> Items(string json) => JsonSerializer.Deserialize<List<PackageItem>>(json, FormJson.Options) ?? [];

    // ================================================================ packages (FR-BIL-10)

    public static async Task<string?> AuthorizePatientPackageAsync(SyncWriteContext ctx)
    {
        var p = (PatientPackage)ctx.Entity;
        var db = ctx.Db;
        if (await ScopeAsync(ctx, p.BranchId, Permissions.Billing.Collect) is { } denied) return denied;
        if (!await db.Patients.AnyAsync(x => x.Id == p.PatientId)) return "Patient not found on the server.";
        if (!PackageStatus.All.Contains(p.Status)) return $"Unknown package status '{p.Status}'.";
        var plan = await FindAsync<PackagePlan>(db, p.PackagePlanId);
        if (plan is null) return "Package not found. Sync to get the latest packages.";
        if (ctx.IsNew)
        {
            if (!plan.IsActive) return $"{plan.Name} is no longer sold.";
            // The entitlements and price are the master's, not the device's.
            p.ItemsJson = plan.ItemsJson;
            p.Price = plan.Price;
            p.Name = plan.Name;
            p.ExpiresAt = p.PurchasedAt.AddDays(plan.ValidityDays);
            return await CareSyncRules.CheckCaseLinkAsync(ctx, p, p.CaseId, p.PatientId);
        }
        if (Changed(ctx, p, nameof(PatientPackage.ItemsJson)) || Changed(ctx, p, nameof(PatientPackage.Price)) || Changed(ctx, p, nameof(PatientPackage.PatientId)))
            return "A sold package's entitlements, price and patient cannot change.";
        if (Original<string>(ctx, p, nameof(PatientPackage.Status)) == PackageStatus.Cancelled && Changed(ctx, p, nameof(PatientPackage.Status)))
            return "This package was cancelled.";
        return null;
    }

    public static async Task<string?> AuthorizeUsageAsync(SyncWriteContext ctx)
    {
        var u = (PackageUsage)ctx.Entity;
        var db = ctx.Db;
        if (!ctx.IsNew) return ctx.Stage == SyncStage.Delete ? null : "A package use cannot be edited.";
        var pkg = await FindAsync<PatientPackage>(db, u.PatientPackageId);
        if (pkg is null) return "The package was not found on the server.";
        u.BranchId = pkg.BranchId;
        if (await ScopeAsync(ctx, pkg.BranchId, Permissions.Billing.Collect) is { } denied) return denied;
        if (pkg.Status != PackageStatus.Active) return $"The package is {pkg.Status}.";
        if (u.UsedAt > pkg.ExpiresAt) return $"The package expired on {pkg.ExpiresAt:dd MMM yyyy}.";

        Guid? owner = u.Source switch
        {
            ChargeSource.Appointment => (await FindAsync<Appointment>(db, u.SourceId))?.PatientId,
            ChargeSource.Procedure => (await FindAsync<ProcedureRecord>(db, u.SourceId))?.PatientId,
            ChargeSource.LabResult => (await FindAsync<LabResult>(db, u.SourceId))?.PatientId,
            _ => null,
        };
        if (owner != pkg.PatientId) return "The charge does not belong to the package's patient.";
        if (await db.PackageUsages.AnyAsync(x => x.Source == u.Source && x.SourceId == u.SourceId && x.Id != u.Id))
            return "This charge already used a package session.";

        var used = await db.PackageUsages.Where(x => x.PatientPackageId == pkg.Id && x.Id != u.Id).Select(x => new { x.Kind, x.Code }).ToListAsync();
        var left = Packages.Remaining(Items(pkg.ItemsJson), used.Select(x => (x.Kind, x.Code)), u.Kind, u.Code);
        return left > 0 ? null : $"No {u.Kind} sessions are left on {pkg.Name}.";
    }

    // ================================================================ panels (FR-BIL-12)

    public static async Task<string?> AuthorizePanelAsync(SyncWriteContext ctx)
    {
        var p = (PatientPanel)ctx.Entity;
        if (await ScopeAsync(ctx, null, Permissions.Billing.Collect) is { } denied) return denied;
        if (!await ctx.Db.Patients.AnyAsync(x => x.Id == p.PatientId)) return "Patient not found on the server.";
        if (await FindAsync<Payer>(ctx.Db, p.PayerId) is not { IsActive: true }) return "That panel / insurer is not active.";
        if (string.IsNullOrWhiteSpace(p.MemberNo)) return "Enter the member / policy number.";
        if (p.CoPayPercent is < 0 or > 100) return "Co-pay must be 0–100%.";
        if (p.ValidFrom is { } f && p.ValidTo is { } t && t < f) return "The panel ends before it starts.";
        return Length(p.MemberNo, 60, "Member number");
    }

    /// <summary>
    /// Called from the invoice rule: a panel invoice must name an active, valid panel of the patient, and the payer /
    /// patient split must be the co-pay arithmetic. Without a payer the patient pays the total.
    /// </summary>
    public static async Task<string?> CheckPanelSplitAsync(IAppDbContext db, Invoice i)
    {
        if (i.PayerId is null)
        {
            i.PanelId = null;
            i.PayerAmount = 0;
            i.PatientAmount = i.Total;
            return null;
        }
        if (i.PanelId is not { } pid || await FindAsync<PatientPanel>(db, pid) is not { } panel || panel.PatientId != i.PatientId || panel.PayerId != i.PayerId)
            return "The panel does not belong to this patient.";
        if (!panel.IsActive || (panel.ValidFrom is { } f && i.IssuedAt.Date < f.Date) || (panel.ValidTo is { } t && i.IssuedAt.Date > t.Date))
            return "The patient's panel is not valid on the invoice date.";
        var payer = await FindAsync<Payer>(db, panel.PayerId);
        if (payer is null) return "Payer not found.";
        var (payerAmount, patientAmount) = PanelBilling.Split(i.Total, panel.CoPayPercent ?? payer.DefaultCoPayPercent);
        if (payerAmount != i.PayerAmount || patientAmount != i.PatientAmount)
            return $"The panel split should be payer Rs {payerAmount:N0} / patient Rs {patientAmount:N0}.";
        return null;
    }

    // ================================================================ credit notes (FR-BIL-14)

    public static string? ValidateCredit(object o)
    {
        var c = (CreditNote)o;
        if (!CreditKind.All.Contains(c.Kind)) return $"Unknown kind '{c.Kind}'.";
        if (!ApprovalStatus.All.Contains(c.Status)) return $"Unknown status '{c.Status}'.";
        if (c.Amount <= 0) return "The amount must be more than zero.";
        if (string.IsNullOrWhiteSpace(c.Reason)) return "A credit note or write-off needs a reason.";
        return Length(c.Reason, 300, "Reason") ?? Length(c.DecisionNote, 300, "Decision note");
    }

    /// <summary>Requested by billing staff; approved or rejected by someone else with Billing.Approve; then locked.</summary>
    public static async Task<string?> AuthorizeCreditAsync(SyncWriteContext ctx)
    {
        var c = (CreditNote)ctx.Entity;
        var db = ctx.Db;
        var invoice = await FindAsync<Invoice>(db, c.InvoiceId);
        if (invoice is null) return "The invoice was not found on the server.";
        c.BranchId = invoice.BranchId;
        c.PatientId = invoice.PatientId;
        if (invoice.Status != InvoiceStatus.Final) return "Credit notes are raised on final invoices.";

        if (ctx.IsNew)
        {
            if (await ScopeAsync(ctx, invoice.BranchId, Permissions.Billing.Collect) is { } denied) return denied;
            if (c.Status != ApprovalStatus.Requested) return "A credit note starts as a request.";
            if (c.RequestedBy != ctx.User.UserId) return "Requests are made by the signed-in user.";
            var due = await PatientDueAsync(db, invoice);
            return c.Amount > due ? $"Only Rs {due:N2} is still due on this invoice." : null;
        }

        var from = Original<string>(ctx, c, nameof(CreditNote.Status));
        if (from != ApprovalStatus.Requested) return "This request has already been decided.";
        var changed = ctx.Db.Entry(c).Properties.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)).Select(p => p.Metadata.Name)
            .Where(n => n is not ("UpdatedAt" or "UpdatedBy" or nameof(CreditNote.BranchId) or nameof(CreditNote.PatientId))).ToList();
        if (changed.Any(n => n is not (nameof(CreditNote.Status) or nameof(CreditNote.DecidedAt) or nameof(CreditNote.DecidedBy) or nameof(CreditNote.DecisionNote))))
            return "Only the decision can change on a request.";
        if (c.Status == ApprovalStatus.Requested) return null;
        if (await ScopeAsync(ctx, invoice.BranchId, Permissions.Billing.Approve) is { } noApprove) return noApprove;
        if (c.DecidedBy != ctx.User.UserId) return "Decisions are made by the signed-in user.";
        if (c.RequestedBy == ctx.User.UserId) return "You cannot approve your own request.";
        if (c.Status == ApprovalStatus.Approved && ctx.Stage == SyncStage.Final)
        {
            var due = await PatientDueAsync(db, invoice);
            if (c.Amount > due) return $"Only Rs {due:N2} is still due on this invoice.";
        }
        return null;
    }

    /// <summary>What the patient still owes on an invoice: their share − settled + refunded − approved credits.</summary>
    public static async Task<decimal> PatientDueAsync(IAppDbContext db, Invoice invoice)
    {
        var pays = await db.Payments.Where(p => p.InvoiceId == invoice.Id).Select(p => new { p.Kind, p.Amount }).ToListAsync();
        var credits = (await db.CreditNotes.Where(c => c.InvoiceId == invoice.Id && c.Status == ApprovalStatus.Approved).Select(c => c.Amount).ToListAsync()).Sum();
        var patientShare = invoice.PayerId is null ? invoice.Total : invoice.PatientAmount;
        return Math.Max(0, patientShare - pays.Where(p => PaymentKind.Settles(p.Kind)).Sum(p => p.Amount) + pays.Where(p => p.Kind == PaymentKind.Refund).Sum(p => p.Amount) - credits);
    }

    // ================================================================ expenses (FR-BIL-17)

    public static string? ValidateExpense(object o)
    {
        var e = (Expense)o;
        if (e.Amount <= 0) return "The amount must be more than zero.";
        if (string.IsNullOrWhiteSpace(e.Category)) return "Choose a category.";
        return Length(e.PaidTo, 150, "Paid to") ?? Length(e.Notes, 500, "Notes");
    }

    public static async Task<string?> AuthorizeExpenseAsync(SyncWriteContext ctx)
    {
        var e = (Expense)ctx.Entity;
        if (await ScopeAsync(ctx, e.BranchId, Permissions.Expenses.Manage) is { } denied) return denied;
        if (ctx.IsNew && e.RecordedBy != ctx.User.UserId) return "Expenses are recorded by the signed-in user.";
        if (!ctx.IsNew && Changed(ctx, e, nameof(Expense.RecordedBy))) return "The recorder of an expense cannot change.";
        if (!await ctx.Db.LookupItems.AnyAsync(i => i.ListKey == "expense_categories" && i.Code == e.Category)) return $"Unknown expense category '{e.Category}'.";
        return null;
    }

    // ================================================================ communication (FR-COM-01…03)

    public static async Task<string?> AuthorizePreferenceAsync(SyncWriteContext ctx)
    {
        var p = (ContactPreference)ctx.Entity;
        if (ctx.User.GetScope(Permissions.Patients.Edit) is null && ctx.User.GetScope(Permissions.Messages.Send) is null)
            return "You do not have the Patients.Edit permission.";
        if (!await ctx.Db.Patients.AnyAsync(x => x.Id == p.PatientId)) return "Patient not found on the server.";
        if (p.Language is not ("en" or "ur")) return "Language must be English or Urdu.";
        if (!MessageChannel.All.Contains(p.Channel)) return $"Unknown channel '{p.Channel}'.";
        if (!ctx.IsNew && Changed(ctx, p, nameof(ContactPreference.PatientId))) return "A preference belongs to one patient.";
        return null;
    }

    public static string? ValidateMessage(object o)
    {
        var m = (OutboundMessage)o;
        if (!MessageChannel.All.Contains(m.Channel)) return $"Unknown channel '{m.Channel}'.";
        if (string.IsNullOrWhiteSpace(m.To)) return "The message has no recipient.";
        if (string.IsNullOrWhiteSpace(m.Body)) return "The message is empty.";
        return Length(m.Body, 1600, "Message") ?? Length(m.To, 200, "Recipient");
    }

    public static async Task<string?> AuthorizeMessageAsync(SyncWriteContext ctx)
    {
        var m = (OutboundMessage)ctx.Entity;
        if (!ctx.IsNew) return "A queued message cannot be changed.";
        if (await ScopeAsync(ctx, m.BranchId, Permissions.Messages.Send) is { } denied) return denied;
        if (!await ctx.Db.Patients.AnyAsync(x => x.Id == m.PatientId)) return "Patient not found on the server.";
        m.Status = MessageStatus.Queued; // the sender decides from here
        return null;
    }

    // ================================================================ feedback (FR-FBK-01/02)

    public static string? ValidateFeedback(object o)
    {
        var f = (Feedback)o;
        if (f.OverallRating is < 1 or > 5 || f.DoctorRating is < 1 or > 5 || f.ServiceRating is < 1 or > 5) return "Ratings are 1–5 stars.";
        if (f.Channel is not ("in_clinic" or "link")) return $"Unknown feedback channel '{f.Channel}'.";
        return Length(f.Comment, 2000, "Comment");
    }

    public static async Task<string?> AuthorizeFeedbackAsync(SyncWriteContext ctx)
    {
        var f = (Feedback)ctx.Entity;
        if (await ScopeAsync(ctx, f.BranchId, Permissions.Feedback.Record) is { } denied) return denied;
        if (!await ctx.Db.Patients.AnyAsync(x => x.Id == f.PatientId)) return "Patient not found on the server.";
        if (f.DoctorId is { } d && !await ctx.Db.DoctorProfiles.AnyAsync(x => x.Id == d)) return "Doctor not found.";
        if (!ctx.IsNew) return "Feedback cannot be edited.";
        return await FieldValueRules.CheckFormVersionAsync(ctx.Db, f.FormVersionId, SyncEntityNames.Feedback);
    }

    public static string? ValidateComplaint(object o)
    {
        var c = (Complaint)o;
        if (!ComplaintStatus.All.Contains(c.Status)) return $"Unknown status '{c.Status}'.";
        if (string.IsNullOrWhiteSpace(c.Subject)) return "A complaint needs a subject.";
        if (c.Status is ComplaintStatus.Resolved or ComplaintStatus.Closed && string.IsNullOrWhiteSpace(c.Resolution)) return "Record how the complaint was resolved.";
        return Length(c.Subject, 200, "Subject") ?? Length(c.Details, 4000, "Details") ?? Length(c.Resolution, 4000, "Resolution");
    }

    /// <summary>Anyone recording feedback may log a complaint; assigning, working and resolving it needs Feedback.ManageComplaints.</summary>
    public static async Task<string?> AuthorizeComplaintAsync(SyncWriteContext ctx)
    {
        var c = (Complaint)ctx.Entity;
        if (ctx.IsNew)
        {
            if (await ScopeAsync(ctx, c.BranchId, Permissions.Feedback.Record) is { } denied) return denied;
            if (c.Status != ComplaintStatus.Open) return "A complaint starts open.";
        }
        else if (await ScopeAsync(ctx, c.BranchId, Permissions.Feedback.ManageComplaints) is { } denied) return denied;
        if (c.PatientId is { } p && !await ctx.Db.Patients.AnyAsync(x => x.Id == p)) return "Patient not found on the server.";
        if (!ctx.IsNew && Original<string>(ctx, c, nameof(Complaint.Status)) == ComplaintStatus.Closed && Changed(ctx, c, nameof(Complaint.Status)))
            return "This complaint is closed.";
        return null;
    }
}

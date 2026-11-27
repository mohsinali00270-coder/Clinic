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
/// Server re-checks (FR-SYN-13) for labs (§4.5) and billing (§4.6). Money rows are a ledger: payments are never
/// edited, a final invoice's amounts never change, and every total is recomputed from the lines the server holds.
/// </summary>
public static class LabBillingSyncRules
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

    private static IEnumerable<string> ChangedProperties(SyncWriteContext ctx, object entity) =>
        ctx.IsNew ? [] : ctx.Db.Entry(entity).Properties
            .Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue))
            .Select(p => p.Metadata.Name)
            .Where(n => n is not ("UpdatedAt" or "UpdatedBy"));

    private static string? Length(string? value, int max, string label) =>
        value is { } v && v.Length > max ? $"{label} must be {max} characters or fewer." : null;

    private static async Task<string?> BranchScopeAsync(SyncWriteContext ctx, Guid branchId, string permission)
    {
        if (ctx.User.GetScope(permission) is not { } scope) return $"You do not have the {permission} permission.";
        if (!await ctx.Db.Branches.AnyAsync(b => b.Id == branchId)) return "Branch not found.";
        return scope != PermissionScope.All && !ctx.User.BranchIds.Contains(branchId) ? "You can only work in your own branches." : null;
    }

    // ================================================================ labs (FR-LAB-01…03)

    public static string? ValidateLabOrder(object o)
    {
        var x = (LabOrder)o;
        if (!LabPriority.All.Contains(x.Priority)) return $"Unknown priority '{x.Priority}'.";
        if (x.Status is not ("open" or "complete")) return $"Unknown order status '{x.Status}'.";
        return Length(x.ClinicalNotes, 1000, "Clinical notes");
    }

    public static async Task<string?> AuthorizeLabOrderAsync(SyncWriteContext ctx)
    {
        var x = (LabOrder)ctx.Entity;
        var db = ctx.Db;
        // Doctors place orders; the lab may only mark the order complete.
        var onlyStatus = !ctx.IsNew && ChangedProperties(ctx, x).All(n => n == nameof(LabOrder.Status));
        var permission = onlyStatus && ctx.User.GetScope(Permissions.Labs.Order) is null ? Permissions.Labs.EnterResults : Permissions.Labs.Order;
        if (await BranchScopeAsync(ctx, x.BranchId, permission) is { } denied) return denied;
        if (!await db.Patients.AnyAsync(p => p.Id == x.PatientId)) return "Patient not found on the server.";
        if (!ctx.IsNew && (Changed(ctx, x, nameof(LabOrder.PatientId)) || Changed(ctx, x, nameof(LabOrder.BranchId))))
            return "An order cannot move to another patient or branch.";
        if (x.EncounterId is { } eid && (await FindAsync<Encounter>(db, eid))?.PatientId != x.PatientId) return "The encounter was not found for this patient.";
        if (x.DoctorId is { } did && !await db.DoctorProfiles.AnyAsync(d => d.Id == did)) return "Doctor not found.";
        return await CareSyncRules.CheckCaseLinkAsync(ctx, x, x.CaseId, x.PatientId);
    }

    public static string? ValidateLabResult(object o)
    {
        var r = (LabResult)o;
        if (!LabStatus.All.Contains(r.Status)) return $"Unknown lab status '{r.Status}'.";
        if (string.IsNullOrWhiteSpace(r.TestName)) return "The test needs a name.";
        if (r.Status is LabStatus.Collected or LabStatus.Resulted or LabStatus.Verified && r.CollectedAt is null) return "The sample collection time is missing.";
        if (r.Status is LabStatus.Resulted or LabStatus.Verified && r.ResultedAt is null) return "The result time is missing.";
        if (r.Status == LabStatus.Verified && (r.VerifiedAt is null || r.VerifiedBy is null)) return "A verified result needs the verifier and time.";
        if (r.Fee is < 0) return "The fee cannot be negative.";
        return Length(r.Comment, 2000, "Comment") ?? Length(r.SampleNo, 40, "Sample number") ?? Length(r.CancelReason, 300, "Cancel reason");
    }

    /// <summary>
    /// FR-LAB-02: the order's doctor creates the line; the lab collects and enters results (Labs.EnterResults);
    /// verification needs Labs.Verify and the verifier's own identity; a verified or cancelled result is locked —
    /// including its parameter values, which reach this rule through the FieldValue parent check.
    /// </summary>
    public static async Task<string?> AuthorizeLabResultAsync(SyncWriteContext ctx)
    {
        var r = (LabResult)ctx.Entity;
        var db = ctx.Db;
        var order = await FindAsync<LabOrder>(db, r.OrderId);
        if (order is null) return "The lab order was not found on the server.";
        r.BranchId = order.BranchId;
        r.PatientId = order.PatientId;

        var test = await FindAsync<LabTest>(db, r.LabTestId);
        if (test is null) return "Lab test not found. Sync to get the latest test menu.";
        r.TestCode = test.Code;

        if (ctx.IsNew)
        {
            if (r.Status != LabStatus.Ordered) return "A new test starts as 'ordered'.";
            if (!test.IsActive) return $"{test.Name} is no longer offered.";
            return await BranchScopeAsync(ctx, r.BranchId, Permissions.Labs.Order)
                   ?? await FieldValueRules.CheckFormVersionAsync(db, r.FormVersionId, SyncEntityNames.LabResult);
        }

        var from = Original<string>(ctx, r, nameof(LabResult.Status));
        if (LabStatus.IsLocked(from))
            return from == LabStatus.Verified ? "This result is verified and locked." : "This test was cancelled.";
        if (!LabStatus.CanMove(from, r.Status)) return $"A test cannot go from '{from}' to '{r.Status}'.";
        if (Changed(ctx, r, nameof(LabResult.OrderId)) || Changed(ctx, r, nameof(LabResult.LabTestId))) return "The test of a result cannot change.";

        var permission = r.Status switch
        {
            LabStatus.Verified when from != LabStatus.Verified => Permissions.Labs.Verify,
            LabStatus.Cancelled => ctx.User.GetScope(Permissions.Labs.Order) is not null ? Permissions.Labs.Order : Permissions.Labs.EnterResults,
            _ => Permissions.Labs.EnterResults,
        };
        if (await BranchScopeAsync(ctx, r.BranchId, permission) is { } denied) return denied;
        if (r.Status == LabStatus.Verified && r.VerifiedBy != ctx.User.UserId) return "A result can only be verified by the signed-in user.";
        return await FieldValueRules.CheckFormVersionAsync(db, r.FormVersionId, SyncEntityNames.LabResult);
    }

    // ================================================================ billing (FR-BIL-01…06)

    public static string? ValidateInvoice(object o)
    {
        var i = (Invoice)o;
        if (!InvoiceStatus.All.Contains(i.Status)) return $"Unknown invoice status '{i.Status}'.";
        if (string.IsNullOrWhiteSpace(i.InvoiceNo)) return "An invoice needs a number.";
        if (i.Subtotal < 0 || i.Discount < 0 || i.Tax < 0 || i.Total < 0) return "Invoice amounts cannot be negative.";
        if (i.Discount > i.Subtotal + i.Tax) return "The discount is larger than the invoice.";
        if (i.Status == InvoiceStatus.Void && string.IsNullOrWhiteSpace(i.VoidReason)) return "Voiding an invoice needs a reason.";
        if (i.Discount > 0 && string.IsNullOrWhiteSpace(i.DiscountReason)) return "A discount needs a reason.";
        return Length(i.InvoiceNo, 40, "Invoice number") ?? Length(i.Notes, 1000, "Notes") ?? Length(i.DiscountReason, 300, "Discount reason");
    }

    public static async Task<string?> AuthorizeInvoiceAsync(SyncWriteContext ctx)
    {
        var i = (Invoice)ctx.Entity;
        var db = ctx.Db;
        if (await BranchScopeAsync(ctx, i.BranchId, Permissions.Billing.Collect) is { } denied) return denied;
        if (!await db.Patients.AnyAsync(p => p.Id == i.PatientId)) return "Patient not found on the server.";
        if (await db.Invoices.AnyAsync(x => x.InvoiceNo == i.InvoiceNo && x.Id != i.Id)) return $"Invoice number {i.InvoiceNo} is already used.";
        if (!ctx.IsNew && (Changed(ctx, i, nameof(Invoice.PatientId)) || Changed(ctx, i, nameof(Invoice.BranchId)) || Changed(ctx, i, nameof(Invoice.InvoiceNo))))
            return "An invoice's patient, branch and number cannot change.";

        // FR-BIL-03: discounts need their own permission.
        if (Changed(ctx, i, nameof(Invoice.Discount)) && i.Discount > (ctx.IsNew ? 0 : Original<decimal>(ctx, i, nameof(Invoice.Discount))) &&
            ctx.User.GetScope(Permissions.Billing.Discount) is null)
            return "You do not have the Billing.Discount permission.";

        var from = Original<string>(ctx, i, nameof(Invoice.Status));
        if (from == InvoiceStatus.Void) return "This invoice is void.";
        if (from == InvoiceStatus.Final)
        {
            var allowed = new[] { nameof(Invoice.Status), nameof(Invoice.VoidReason), nameof(Invoice.Notes) };
            if (ChangedProperties(ctx, i).Any(c => !allowed.Contains(c))) return "This invoice is final; void it and issue a new one to change it.";
            if (i.Status == InvoiceStatus.Draft) return "A final invoice cannot go back to draft.";
        }

        if (i.Status == InvoiceStatus.Void && from != InvoiceStatus.Void)
        {
            var settled = (await db.Payments.Where(p => p.InvoiceId == i.Id).Select(p => new { p.Kind, p.Amount }).ToListAsync())
                .Sum(p => PaymentKind.Settles(p.Kind) ? p.Amount : p.Kind == PaymentKind.Refund ? -p.Amount : 0);
            if (settled > 0) return "Refund the payments on this invoice before voiding it.";
        }

        // Totals are the server's arithmetic, not the device's word for it.
        if (i.Status == InvoiceStatus.Final && (from != InvoiceStatus.Final || ctx.IsNew) && ctx.Stage == SyncStage.Final)
        {
            var lines = await db.InvoiceLines.Where(l => l.InvoiceId == i.Id).ToListAsync();
            if (lines.Count == 0) return "A final invoice needs at least one line.";
            var amounts = lines.Select(l => new LineAmounts(l.Quantity, l.UnitPrice, l.Discount, l.TaxPercent)).ToList();
            var invoiceDiscount = i.Discount - amounts.Sum(a => a.Discount);
            if (invoiceDiscount < 0) return "The invoice discount does not match its lines.";
            var totals = Billing.Totals(amounts, invoiceDiscount);
            if (totals.Subtotal != i.Subtotal || totals.Tax != i.Tax || totals.Total != i.Total)
                return $"The invoice totals do not match its lines (server: {totals.Total:N2}). Sync and try again.";
        }
        // FR-BIL-12: who pays what (without a payer the patient pays the total).
        return ctx.Stage == SyncStage.Final && from != InvoiceStatus.Void ? await AccountsSyncRules.CheckPanelSplitAsync(db, i) : null;
    }

    public static async Task<(decimal Paid, decimal Refunded)> PaidAsync(IAppDbContext db, Guid invoiceId)
    {
        var rows = await db.Payments.Where(p => p.InvoiceId == invoiceId).Select(p => new { p.Kind, p.Amount }).ToListAsync();
        return (rows.Where(r => r.Kind == PaymentKind.Payment).Sum(r => r.Amount), rows.Where(r => r.Kind == PaymentKind.Refund).Sum(r => r.Amount));
    }

    public static string? ValidateLine(object o)
    {
        var l = (InvoiceLine)o;
        if (!ChargeSource.All.Contains(l.Source)) return $"Unknown charge source '{l.Source}'.";
        if (!ServiceKind.All.Contains(l.Kind)) return $"Unknown service kind '{l.Kind}'.";
        if (string.IsNullOrWhiteSpace(l.Description)) return "A line needs a description.";
        if (l.Quantity <= 0 || l.UnitPrice < 0 || l.Discount < 0 || l.TaxPercent is < 0 or > 100) return "Check the quantity, price, discount and tax.";
        if (ChargeSource.IsTracked(l.Source) && l.SourceId is null) return "The line does not say what it charges for.";
        var amounts = new LineAmounts(l.Quantity, l.UnitPrice, l.Discount, l.TaxPercent);
        if (l.Discount > amounts.Gross) return "The line discount is larger than the line.";
        if (amounts.Total != l.Amount) return $"The line amount should be {amounts.Total:N2}.";
        return Length(l.Description, 200, "Description");
    }

    public static async Task<string?> AuthorizeLineAsync(SyncWriteContext ctx)
    {
        var l = (InvoiceLine)ctx.Entity;
        var db = ctx.Db;
        var invoice = await FindAsync<Invoice>(db, l.InvoiceId);
        if (invoice is null) return "The invoice was not found on the server.";
        l.BranchId = invoice.BranchId;
        if (await BranchScopeAsync(ctx, invoice.BranchId, Permissions.Billing.Collect) is { } denied) return denied;
        if (invoice.Status != InvoiceStatus.Draft) return "The invoice is final; its lines cannot change.";
        if (!ctx.IsNew && Changed(ctx, l, nameof(InvoiceLine.InvoiceId))) return "A line cannot move to another invoice.";
        if (Changed(ctx, l, nameof(InvoiceLine.Discount)) && l.Discount > 0 && ctx.User.GetScope(Permissions.Billing.Discount) is null)
            return "You do not have the Billing.Discount permission.";
        if (ctx.Stage == SyncStage.Delete || !ChargeSource.IsTracked(l.Source)) return null;

        // FR-BIL-02: the charged record belongs to this patient and is not on another live invoice.
        var sourceId = l.SourceId!.Value;
        Guid? patient = l.Source switch
        {
            ChargeSource.Appointment => (await FindAsync<Appointment>(db, sourceId))?.PatientId,
            ChargeSource.Procedure => (await FindAsync<ProcedureRecord>(db, sourceId))?.PatientId,
            ChargeSource.LabResult => (await FindAsync<LabResult>(db, sourceId))?.PatientId,
            ChargeSource.Admission => (await FindAsync<Admission>(db, sourceId))?.PatientId,
            ChargeSource.Package => (await FindAsync<PatientPackage>(db, sourceId))?.PatientId,
            ChargeSource.Dispense => (await FindAsync<Dispensation>(db, sourceId))?.PatientId,
            _ => null,
        };
        if (patient is null) return "The charged record was not found on the server.";
        if (patient != invoice.PatientId) return "The charged record belongs to another patient.";
        var billedElsewhere = await (from other in db.InvoiceLines
                                     join inv in db.Invoices on other.InvoiceId equals inv.Id
                                     where other.Source == l.Source && other.SourceId == sourceId && other.Id != l.Id && inv.Status != InvoiceStatus.Void
                                     select inv.InvoiceNo).FirstOrDefaultAsync();
        return billedElsewhere is null ? null : $"This is already billed on invoice {billedElsewhere}.";
    }

    public static string? ValidatePayment(object o)
    {
        var p = (Payment)o;
        if (!PaymentKind.All.Contains(p.Kind)) return $"Unknown payment kind '{p.Kind}'.";
        if (p.Amount <= 0) return "The amount must be more than zero.";
        if (p.Kind == PaymentKind.Deposit && p.InvoiceId is not null) return "A deposit is not tied to an invoice.";
        if (p.Kind == PaymentKind.Allocation && p.InvoiceId is null) return "Credit is applied to an invoice.";
        if (p.Kind == PaymentKind.Refund && string.IsNullOrWhiteSpace(p.Reason)) return "A refund needs a reason.";
        return Length(p.Reference, 100, "Reference") ?? Length(p.Reason, 300, "Reason");
    }

    /// <summary>
    /// FR-BIL-03/04: insert-only. A payment on an invoice may exceed what is due (two offline desks collecting the
    /// same bill must not lose money): the extra stays as patient credit. A refund cannot exceed what was paid.
    /// </summary>
    public static async Task<string?> AuthorizePaymentAsync(SyncWriteContext ctx)
    {
        var p = (Payment)ctx.Entity;
        var db = ctx.Db;
        if (!ctx.IsNew) return ctx.Stage == SyncStage.Delete ? "Payments cannot be deleted; record a refund." : "Payments cannot be edited; record a refund instead.";
        var permission = p.Kind == PaymentKind.Refund ? Permissions.Billing.Refund : Permissions.Billing.Collect;
        if (await BranchScopeAsync(ctx, p.BranchId, permission) is { } denied) return denied;
        if (p.ReceivedBy != ctx.User.UserId) return "Payments are recorded by the user who handles the money.";
        if (!await db.Patients.AnyAsync(x => x.Id == p.PatientId)) return "Patient not found on the server.";
        if (!await db.LookupItems.AnyAsync(i => i.ListKey == "payment_methods" && i.Code == p.Method)) return $"Payment method '{p.Method}' is not in the list.";

        if (p.InvoiceId is { } iid)
        {
            var invoice = await FindAsync<Invoice>(db, iid);
            if (invoice is null || invoice.PatientId != p.PatientId) return "The invoice was not found for this patient.";
            if (invoice.Status == InvoiceStatus.Void && PaymentKind.Settles(p.Kind)) return "This invoice is void.";
            if (p.Kind == PaymentKind.Allocation)
            {
                // FR-BIL-13: credit can only be applied up to what the patient holds and what the invoice still needs.
                if (await CreditAsync(db, p.PatientId) is var credit && p.Amount > credit) return $"The patient has only Rs {credit:N2} credit.";
                if (await AccountsSyncRules.PatientDueAsync(db, invoice) is var due && p.Amount > due) return $"Only Rs {due:N2} is due on this invoice.";
            }
            if (p.Kind == PaymentKind.Refund)
            {
                var (paid, refunded) = await PaidAsync(db, iid);
                if (p.Amount > paid - refunded) return $"Only Rs {paid - refunded:N2} was paid on this invoice.";
            }
        }
        else if (p.Kind == PaymentKind.Refund)
        {
            var credit = await CreditAsync(db, p.PatientId);
            if (p.Amount > credit) return $"Only Rs {credit:N2} of deposits can be refunded.";
        }
        else if (p.Kind == PaymentKind.Payment) return "A payment belongs to an invoice; record money in advance as a deposit.";
        return null;
    }

    /// <summary>Deposit credit still unused: deposits − deposit refunds − credit applied to invoices.</summary>
    public static async Task<decimal> CreditAsync(IAppDbContext db, Guid patientId)
    {
        var rows = await db.Payments.Where(x => x.PatientId == patientId).Select(x => new { x.Kind, x.Amount, x.InvoiceId }).ToListAsync();
        return rows.Where(r => r.Kind == PaymentKind.Deposit).Sum(r => r.Amount)
               - rows.Where(r => r.Kind == PaymentKind.Refund && r.InvoiceId == null).Sum(r => r.Amount)
               - rows.Where(r => r.Kind == PaymentKind.Allocation).Sum(r => r.Amount);
    }

    /// <summary>FR-BIL-06: insert-only; the server records its own expected cash from the payments it holds.</summary>
    public static async Task<string?> AuthorizeCashClosingAsync(SyncWriteContext ctx)
    {
        var c = (CashClosing)ctx.Entity;
        if (!ctx.IsNew) return "A cash closing cannot be changed; close again if needed.";
        if (await BranchScopeAsync(ctx, c.BranchId, Permissions.Billing.CashClose) is { } denied) return denied;
        if (c.UserId != ctx.User.UserId) return "You can only close your own cash.";
        if (c.CountedCash < 0) return "Counted cash cannot be negative.";

        var summary = await DaySummaryAsync(ctx.Db, c.BranchId, c.BusinessDate, c.UserId);
        c.ServerExpectedCash = summary.Cash;
        c.Mismatch = summary.Cash != c.ExpectedCash;
        return null;
    }

    public static async Task<DaySummary> DaySummaryAsync(IAppDbContext db, Guid branchId, DateTime date, Guid? userId)
    {
        // Load a two-day UTC window around the local day and let the shared calculator pick the local day.
        var from = DateTime.SpecifyKind(date.Date.AddDays(-1), DateTimeKind.Utc);
        var to = from.AddDays(3);
        var q = db.Payments.Where(p => p.BranchId == branchId && p.ReceivedAt >= from && p.ReceivedAt < to);
        if (userId is { } u) q = q.Where(p => p.ReceivedBy == u);
        var rows = await q.Select(p => new PaymentEntry(p.Id, p.ReceivedBy, p.BranchId, p.ReceivedAt, p.Kind, p.Method, p.Amount)).ToListAsync();
        return CashClosingCalculator.Summarise(rows, date, branchId, userId);
    }
}

using System.Text.Json;
using Clinic.Client.Core.Local;
using Clinic.Client.Core.Sync;
using Clinic.Shared.Clinical;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Clinical;

public sealed class BillingException(string message) : Exception(message);

/// <summary>
/// Something the patient can be charged for that is not on a live invoice yet (FR-BIL-02). When a package covers it
/// (FR-BIL-10), <see cref="PackageId"/> names the package and the line is billed at zero.
/// </summary>
public sealed record ChargeCandidate(string Source, Guid? SourceId, string Kind, string Description, decimal UnitPrice, decimal Quantity = 1,
    decimal TaxPercent = 0, Guid? DoctorId = null, DateTime? At = null, string? Code = null, Guid? PackageId = null, string? PackageName = null,
    Guid? CaseId = null);

public sealed record InvoiceSummary(LocalInvoice Invoice, IReadOnlyList<LocalInvoiceLine> Lines, IReadOnlyList<LocalPayment> Payments,
    IReadOnlyList<LocalCreditNote> Credits)
{
    public decimal Paid => Payments.Where(p => PaymentKind.Settles(p.Kind)).Sum(p => p.Amount);
    public decimal Refunded => Payments.Where(p => p.Kind == PaymentKind.Refund).Sum(p => p.Amount);
    public decimal Credited => Credits.Where(c => c.Status == ApprovalStatus.Approved).Sum(c => c.Amount);

    /// <summary>The patient's part (the co-pay on a panel invoice).</summary>
    public decimal PatientShare => Invoice.PayerId is null ? Invoice.Total : Invoice.PatientAmount;

    public decimal Due => Invoice.Status == InvoiceStatus.Void ? 0 : Math.Max(0, Billing.Due(PatientShare, Paid, Refunded) - Credited);
    public bool IsPaid => Invoice.Status == InvoiceStatus.Final && Due == 0;
}

public sealed record PatientBalance(decimal Invoiced, decimal Paid, decimal Refunded, decimal Deposits, decimal Credit)
{
    /// <summary>Positive: the patient owes; negative: credit (advance) left. Applying credit moves no money, so it does not change this.</summary>
    public decimal Balance => Invoiced - Paid - Deposits + Refunded;
}

public sealed record PackageStatusView(LocalPatientPackage Package, IReadOnlyList<(PackageItem Item, int Used)> Items)
{
    public bool IsUsable(DateTime now) => Package.Status == PackageStatus.Active && Package.ExpiresAt >= now;
}

/// <summary>
/// §4.6 / §4.13 on the device: invoices from what was done plus services and manual lines, discounts, final invoices
/// numbered from the device's leased range, panels with co-pay and payer prices, packages used session by session,
/// payments / deposits / refunds / credit applied as insert-only ledger rows, credit notes and write-offs for
/// approval, receipts, running bills and the day's cash closing. Works offline.
/// </summary>
public sealed class BillingService(LocalDatabase db, LocalStore store, string deviceCode, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private Guid UserId => store.Context.UserId;

    // ------------------------------------------------------------------ what can be billed

    /// <summary>Visits, procedures, lab tests and finished day-care stays of the patient that no live invoice charges yet.</summary>
    public async Task<List<ChargeCandidate>> UnbilledAsync(Guid patientId, bool includeRunningStays = false)
    {
        var billed = await BilledSourcesAsync();
        var doctors = (await store.ListAsync<LocalDoctor>()).ToDictionary(d => d.Id);
        var types = (await store.ListAsync<LocalAppointmentType>()).ToDictionary(t => t.Id);
        var encounters = (await store.ListAsync<LocalEncounter>(e => e.PatientId == patientId)).Where(e => e.AppointmentId is not null)
            .ToDictionary(e => e.AppointmentId!.Value, e => e.CaseId);
        var orders = (await store.ListAsync<LocalLabOrder>(o => o.PatientId == patientId)).ToDictionary(o => o.Id, o => o.CaseId);
        var result = new List<ChargeCandidate>();

        foreach (var a in await store.ListAsync<LocalAppointment>(a => a.PatientId == patientId))
        {
            if (a.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow or AppointmentStatus.Rescheduled or AppointmentStatus.Requested) continue;
            if (billed.Contains((ChargeSource.Appointment, a.Id))) continue;
            var doctor = doctors.GetValueOrDefault(a.DoctorId);
            var type = types.GetValueOrDefault(a.TypeId);
            var fee = type?.Fee ?? doctor?.ConsultationFee ?? 0;
            result.Add(new ChargeCandidate(ChargeSource.Appointment, a.Id, ServiceKind.Consultation,
                $"{type?.Name ?? "Consultation"} · {doctor?.FullName ?? "Doctor"} · {a.Date:dd MMM}", fee, DoctorId: a.DoctorId, At: a.Date,
                CaseId: encounters.GetValueOrDefault(a.Id)));
        }
        foreach (var p in await store.ListAsync<LocalProcedure>(p => p.PatientId == patientId))
        {
            if (p.Status is ProcedureStatus.Cancelled or ProcedureStatus.Planned || p.Fee is null || billed.Contains((ChargeSource.Procedure, p.Id))) continue;
            result.Add(new ChargeCandidate(ChargeSource.Procedure, p.Id, ServiceKind.Procedure, p.Name, p.Fee.Value, DoctorId: p.DoctorId,
                At: p.CompletedAt ?? p.StartedAt, Code: p.ProcedureCode, CaseId: p.CaseId));
        }
        foreach (var r in await store.ListAsync<LocalLabResult>(r => r.PatientId == patientId))
        {
            if (r.Status == LabStatus.Cancelled || r.Fee is null || billed.Contains((ChargeSource.LabResult, r.Id))) continue;
            result.Add(new ChargeCandidate(ChargeSource.LabResult, r.Id, ServiceKind.Lab, r.TestName, r.Fee.Value, At: r.CreatedAt, Code: r.TestCode,
                CaseId: orders.GetValueOrDefault(r.OrderId)));
        }
        foreach (var d in await store.ListAsync<LocalDispensation>(d => d.PatientId == patientId))
        {
            if (billed.Contains((ChargeSource.Dispense, d.Id))) continue;
            result.Add(new ChargeCandidate(ChargeSource.Dispense, d.Id, ServiceKind.Medicine, $"{d.ItemName} × {d.Quantity:0.##}", d.UnitPrice, d.Quantity, At: d.DispensedAt));
        }
        var dayCare = new DayCareService(store, _clock);
        foreach (var a in await store.ListAsync<LocalAdmission>(a => a.PatientId == patientId))
        {
            if (a.Status == AdmissionStatus.Cancelled || (a.Status == AdmissionStatus.Admitted && !includeRunningStays)) continue;
            if (billed.Contains((ChargeSource.Admission, a.Id))) continue;
            var charges = await dayCare.ChargesAsync(a.Id);
            if (charges.Total <= 0) continue;
            var units = string.Join(", ", charges.Lines.Select(l => $"{l.Units} {l.Unit}(s)"));
            result.Add(new ChargeCandidate(ChargeSource.Admission, a.Id, ServiceKind.Room,
                $"Room charges · {units}" + (a.Status == AdmissionStatus.Admitted ? " (so far)" : ""), charges.Total, At: a.DischargedAt ?? UtcNow, CaseId: a.CaseId));
        }

        // FR-BIL-10: offer package cover where an active package still has sessions of that kind.
        var packages = await PackagesAsync(patientId);
        var claimed = new Dictionary<(Guid, string, string?), int>();
        for (var i = 0; i < result.Count; i++)
        {
            var c = result[i];
            if (c.Source is not (ChargeSource.Appointment or ChargeSource.Procedure or ChargeSource.LabResult)) continue;
            foreach (var p in packages.Where(p => p.IsUsable(c.At ?? UtcNow)))
            {
                var entry = p.Items.FirstOrDefault(x => Packages.Matches(x.Item, c.Kind, c.Code));
                if (entry.Item is null) continue;
                var key = (p.Package.Id, entry.Item.Kind, entry.Item.Code);
                var already = claimed.GetValueOrDefault(key);
                if (entry.Used + already >= entry.Item.Count) continue;
                claimed[key] = already + 1;
                result[i] = c with { PackageId = p.Package.Id, PackageName = p.Package.Name };
                break;
            }
        }
        return result.OrderBy(c => c.At).ToList();
    }

    private async Task<HashSet<(string, Guid)>> BilledSourcesAsync()
    {
        var live = (await store.ListAsync<LocalInvoice>(i => i.Status != InvoiceStatus.Void)).Select(i => i.Id).ToHashSet();
        return (await store.ListAsync<LocalInvoiceLine>(l => l.SourceId != null))
            .Where(l => live.Contains(l.InvoiceId)).Select(l => (l.Source, l.SourceId!.Value)).ToHashSet();
    }

    public async Task<List<LocalServiceItem>> ServicesAsync(Guid branchId)
    {
        // A branch price overrides the tenant-wide one with the same code (FR-BIL-01).
        var all = await store.ListAsync<LocalServiceItem>(s => s.IsActive);
        return all.Where(s => s.BranchId is null || s.BranchId == branchId)
            .GroupBy(s => s.Code).Select(g => g.OrderByDescending(s => s.BranchId is not null).First())
            .OrderBy(s => s.Kind).ThenBy(s => s.Name).ToList();
    }

    // ------------------------------------------------------------------ invoices

    public async Task<LocalInvoice> CreateInvoiceAsync(Guid patientId, Guid branchId, IEnumerable<ChargeCandidate>? charges = null,
        Guid? encounterId = null, Guid? caseId = null, Guid? admissionId = null, Guid? panelId = null)
    {
        _ = await store.GetAsync<LocalPatient>(patientId) ?? throw new BillingException("Patient not found.");
        var branch = await store.GetAsync<LocalBranch>(branchId) ?? throw new BillingException("Branch not found.");
        var seq = await NumberSource.NextAsync(db, NumberRangeKinds.Invoice);
        var invoice = new LocalInvoice
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, InvoiceNo = Billing.InvoiceNo(branch.Code, deviceCode, seq),
            IssuedAt = UtcNow, Status = InvoiceStatus.Draft, EncounterId = encounterId, CaseId = caseId, AdmissionId = admissionId,
        };
        var panel = panelId is { } pid ? await RequirePanelAsync(patientId, pid) : null;
        invoice.PanelId = panel?.Panel.Id;
        invoice.PayerId = panel?.Panel.PayerId;

        var billed = await BilledSourcesAsync();
        var list = (charges ?? []).Where(c => c.SourceId is null || !billed.Contains((c.Source, c.SourceId.Value))).ToList();
        var lines = list.Select((c, i) => Line(invoice, c, (i + 1) * 10, panel?.Prices)).ToList();
        var usages = list.Where(c => c.PackageId is not null).Select(c => Usage(c, invoice.BranchId)).ToList();
        await store.WriteAsync(b =>
        {
            b.Insert(invoice);
            foreach (var u in usages) b.Insert(u);
            foreach (var l in lines) b.Insert(l);
            ApplyTotals(invoice, lines, 0, panel?.CoPay);
            b.Update(invoice);
        });
        return invoice;
    }

    public async Task<LocalInvoiceLine> AddChargeAsync(Guid invoiceId, ChargeCandidate charge)
    {
        if (charge.SourceId is { } sid && (await BilledSourcesAsync()).Contains((charge.Source, sid))) throw new BillingException("That is already billed.");
        var (invoice, lines) = await RequireDraftAsync(invoiceId);
        var panel = await PanelOfAsync(invoice);
        var invoiceLevel = InvoiceLevelDiscount(invoice, lines);
        var line = Line(invoice, charge, (lines.LastOrDefault()?.SortOrder ?? 0) + 10, panel?.Prices);
        lines.Add(line);
        await store.WriteAsync(b =>
        {
            if (charge.PackageId is not null) b.Insert(Usage(charge, invoice.BranchId));
            b.Insert(line);
            ApplyTotals(invoice, lines, invoiceLevel, panel?.CoPay);
            b.Update(invoice);
        });
        return line;
    }

    public async Task<LocalInvoiceLine> AddServiceAsync(Guid invoiceId, Guid serviceItemId, decimal quantity = 1)
    {
        var s = await store.GetAsync<LocalServiceItem>(serviceItemId) ?? throw new BillingException("Service not found.");
        return await AddChargeAsync(invoiceId, new ChargeCandidate(ChargeSource.Service, s.Id, s.Kind, s.Name, s.Price, quantity, s.TaxPercent, Code: s.Code));
    }

    public Task<LocalInvoiceLine> AddManualAsync(Guid invoiceId, string description, decimal price, decimal quantity = 1) =>
        string.IsNullOrWhiteSpace(description) ? throw new BillingException("Describe the charge.")
            : AddChargeAsync(invoiceId, new ChargeCandidate(ChargeSource.Manual, null, ServiceKind.Other, description.Trim(), price, quantity));

    public Task RemoveLineAsync(Guid lineId) => ChangeLineAsync(lineId, (line, lines, b) =>
    {
        lines.Remove(line);
        b.Delete<LocalInvoiceLine>(line.Id);
        // Removing a package-covered charge gives the session back.
        if (line.SourceId is { } sid)
            foreach (var u in b.Connection.Table<LocalPackageUsage>().Where(u => u.SourceId == sid && !u.IsDeleted).ToList())
                b.Delete<LocalPackageUsage>(u.Id);
    });

    /// <summary>FR-BIL-03: a line discount (the server checks Billing.Discount).</summary>
    public Task SetLineDiscountAsync(Guid lineId, decimal discount) => ChangeLineAsync(lineId, (line, _, b) =>
    {
        var gross = new LineAmounts(line.Quantity, line.UnitPrice, 0, line.TaxPercent).Gross;
        if (discount < 0 || discount > gross) throw new BillingException($"The discount must be between 0 and {gross:N0}.");
        line.Discount = LineAmounts.Round(discount);
        line.Amount = new LineAmounts(line.Quantity, line.UnitPrice, line.Discount, line.TaxPercent).Total;
        b.Update(line);
    });

    /// <summary>FR-BIL-03: an invoice-level discount on top of line discounts, with a reason (lookup 'discount_reasons' or text).</summary>
    public async Task SetInvoiceDiscountAsync(Guid invoiceId, decimal discount, string reason)
    {
        if (discount > 0 && string.IsNullOrWhiteSpace(reason)) throw new BillingException("A discount needs a reason.");
        var (invoice, lines) = await RequireDraftAsync(invoiceId);
        var max = Billing.Totals(lines.Select(Amounts), 0).Total;
        if (discount < 0 || discount > max) throw new BillingException($"The discount must be between 0 and {max:N0}.");
        invoice.DiscountReason = discount > 0 || lines.Any(l => l.Discount > 0) ? reason?.Trim() : null;
        ApplyTotals(invoice, lines, LineAmounts.Round(discount), (await PanelOfAsync(invoice))?.CoPay);
        await store.UpdateAsync(invoice);
    }

    /// <summary>
    /// FR-BIL-12: bill this draft to the patient's panel (or back to the patient with <paramref name="panelId"/> null).
    /// Lines take the payer's price where its price list has one; the patient pays the co-pay.
    /// </summary>
    public async Task SetPanelAsync(Guid invoiceId, Guid? panelId)
    {
        var (invoice, lines) = await RequireDraftAsync(invoiceId);
        var panel = panelId is { } pid ? await RequirePanelAsync(invoice.PatientId, pid) : null;
        var invoiceLevel = InvoiceLevelDiscount(invoice, lines);
        var services = (await store.ListAsync<LocalServiceItem>()).ToDictionary(s => s.Id);
        await store.WriteAsync(b =>
        {
            invoice.PanelId = panel?.Panel.Id;
            invoice.PayerId = panel?.Panel.PayerId;
            foreach (var l in lines.Where(l => l.UnitPrice > 0 || l.Source == ChargeSource.Service))
            {
                var code = l.Source == ChargeSource.Service && l.SourceId is { } sid ? services.GetValueOrDefault(sid)?.Code : null;
                if (panel is not null && PanelBilling.PriceFor(panel.Prices, l.Kind, code) is { } price && price != l.UnitPrice)
                {
                    l.UnitPrice = price;
                    l.Amount = new LineAmounts(l.Quantity, l.UnitPrice, Math.Min(l.Discount, LineAmounts.Round(l.Quantity * price)), l.TaxPercent).Total;
                    b.Update(l);
                }
            }
            ApplyTotals(invoice, lines, invoiceLevel, panel?.CoPay);
            b.Update(invoice);
        });
    }

    /// <summary>Issues the invoice: totals are fixed and lines locked; payments continue (FR-BIL-05).</summary>
    public async Task<LocalInvoice> FinaliseAsync(Guid invoiceId)
    {
        var (invoice, lines) = await RequireDraftAsync(invoiceId);
        if (lines.Count == 0) throw new BillingException("Add at least one charge.");
        var panel = await PanelOfAsync(invoice);
        if (panel is not null && !IsValid(panel.Panel, UtcNow)) throw new BillingException("The patient's panel is not valid today.");
        var invoiceLevel = InvoiceLevelDiscount(invoice, lines);
        invoice.Status = InvoiceStatus.Final;
        invoice.IssuedAt = UtcNow;
        ApplyTotals(invoice, lines, invoiceLevel, panel?.CoPay);
        await store.UpdateAsync(invoice);
        return invoice;
    }

    public async Task<LocalInvoice> VoidAsync(Guid invoiceId, string reason)
    {
        var summary = await SummaryAsync(invoiceId);
        if (summary.Invoice.Status == InvoiceStatus.Void) return summary.Invoice;
        if (string.IsNullOrWhiteSpace(reason)) throw new BillingException("Give a reason for voiding.");
        if (summary.Paid - summary.Refunded > 0) throw new BillingException($"Refund Rs {summary.Paid - summary.Refunded:N0} first.");
        var usages = new List<LocalPackageUsage>();
        foreach (var l in summary.Lines.Where(l => l.SourceId is not null))
            usages.AddRange(await store.ListAsync<LocalPackageUsage>(u => u.SourceId == l.SourceId!.Value));
        await store.WriteAsync(b =>
        {
            foreach (var u in usages) b.Delete<LocalPackageUsage>(u.Id); // the sessions return to the package
            summary.Invoice.Status = InvoiceStatus.Void;
            summary.Invoice.VoidReason = reason.Trim();
            b.Update(summary.Invoice);
        });
        return summary.Invoice;
    }

    public async Task<InvoiceSummary> SummaryAsync(Guid invoiceId)
    {
        var invoice = await store.GetAsync<LocalInvoice>(invoiceId) ?? throw new BillingException("Invoice not found.");
        return new InvoiceSummary(invoice, await LinesAsync(invoiceId),
            (await store.ListAsync<LocalPayment>(p => p.InvoiceId == invoiceId)).OrderBy(p => p.ReceivedAt).ToList(),
            (await store.ListAsync<LocalCreditNote>(c => c.InvoiceId == invoiceId)).OrderBy(c => c.RequestedAt).ToList());
    }

    public async Task<List<LocalInvoice>> ForPatientAsync(Guid patientId) =>
        (await store.ListAsync<LocalInvoice>(i => i.PatientId == patientId)).OrderByDescending(i => i.IssuedAt).ToList();

    private async Task<List<LocalInvoiceLine>> LinesAsync(Guid invoiceId) =>
        (await store.ListAsync<LocalInvoiceLine>(l => l.InvoiceId == invoiceId)).OrderBy(l => l.SortOrder).ToList();

    // ------------------------------------------------------------------ packages (FR-BIL-10)

    public async Task<List<LocalPackagePlan>> PlansAsync() =>
        (await store.ListAsync<LocalPackagePlan>(p => p.IsActive)).OrderBy(p => p.Name).ToList();

    public async Task<List<PackageStatusView>> PackagesAsync(Guid patientId)
    {
        var result = new List<PackageStatusView>();
        foreach (var p in (await store.ListAsync<LocalPatientPackage>(x => x.PatientId == patientId)).OrderByDescending(x => x.PurchasedAt))
        {
            var used = (await store.ListAsync<LocalPackageUsage>(u => u.PatientPackageId == p.Id)).Select(u => (u.Kind, u.Code)).ToList();
            var items = Items(p.ItemsJson).Select(i => (i, used.Count(u => Packages.Matches(i, u.Kind, u.Code)))).ToList();
            result.Add(new PackageStatusView(p, items));
        }
        return result;
    }

    /// <summary>
    /// Sells a package: the patient's package and a line charging its price on a draft invoice (a new one unless
    /// <paramref name="invoiceId"/> is given). Its sessions are then used by later charges at no cost.
    /// </summary>
    public async Task<(LocalPatientPackage Package, LocalInvoice Invoice)> SellPackageAsync(Guid patientId, Guid branchId, Guid planId, Guid? caseId = null, Guid? invoiceId = null)
    {
        var plan = await store.GetAsync<LocalPackagePlan>(planId) ?? throw new BillingException("Package not found.");
        if (!plan.IsActive) throw new BillingException($"{plan.Name} is no longer sold.");
        var package = new LocalPatientPackage
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, PackagePlanId = plan.Id, Name = plan.Name, Price = plan.Price,
            ItemsJson = plan.ItemsJson, PurchasedAt = UtcNow, ExpiresAt = UtcNow.AddDays(plan.ValidityDays), CaseId = caseId, Status = PackageStatus.Active,
        };
        await store.InsertAsync(package);
        var charge = new ChargeCandidate(ChargeSource.Package, package.Id, ServiceKind.Other, $"Package: {plan.Name}", plan.Price, CaseId: caseId);
        if (invoiceId is { } iid)
        {
            await AddChargeAsync(iid, charge);
            return (package, (await store.GetAsync<LocalInvoice>(iid))!);
        }
        return (package, await CreateInvoiceAsync(patientId, branchId, [charge], caseId: caseId));
    }

    private static List<PackageItem> Items(string json) => JsonSerializer.Deserialize<List<PackageItem>>(json, LocalJson.Options) ?? [];

    private LocalPackageUsage Usage(ChargeCandidate c, Guid branchId) => new()
    {
        Id = Guid.NewGuid(), PatientPackageId = c.PackageId!.Value, BranchId = branchId, Kind = c.Kind, Code = c.Code, Source = c.Source,
        SourceId = c.SourceId ?? throw new BillingException("Only recorded visits, procedures and tests use package sessions."), UsedAt = c.At ?? UtcNow, UsedBy = UserId,
    };

    // ------------------------------------------------------------------ panels (FR-BIL-12)

    public sealed record PanelInfo(LocalPatientPanel Panel, LocalPayer Payer, decimal CoPay, IReadOnlyList<PayerPrice> Prices);

    public async Task<List<PanelInfo>> PanelsAsync(Guid patientId)
    {
        var payers = (await store.ListAsync<LocalPayer>()).ToDictionary(p => p.Id);
        return (await store.ListAsync<LocalPatientPanel>(p => p.PatientId == patientId && p.IsActive))
            .Where(p => payers.ContainsKey(p.PayerId)).Select(p => Info(p, payers[p.PayerId])).ToList();
    }

    public async Task<LocalPatientPanel> AddPanelAsync(Guid patientId, Guid payerId, string memberNo, decimal? coPayPercent = null,
        string? employer = null, string? relation = "self", DateTime? validTo = null)
    {
        var payer = await store.GetAsync<LocalPayer>(payerId) ?? throw new BillingException("Payer not found.");
        if (!payer.IsActive) throw new BillingException($"{payer.Name} is not active.");
        if (string.IsNullOrWhiteSpace(memberNo)) throw new BillingException("Enter the member / policy number.");
        return await store.InsertAsync(new LocalPatientPanel
        {
            Id = Guid.NewGuid(), PatientId = patientId, PayerId = payerId, MemberNo = memberNo.Trim(), CoPayPercent = coPayPercent, Employer = employer,
            Relation = relation, ValidFrom = DateTime.Today, ValidTo = validTo, IsActive = true,
        });
    }

    private static bool IsValid(LocalPatientPanel p, DateTime at) =>
        p.IsActive && (p.ValidFrom is null || p.ValidFrom.Value.Date <= at.ToLocalTime().Date) && (p.ValidTo is null || p.ValidTo.Value.Date >= at.ToLocalTime().Date);

    private static PanelInfo Info(LocalPatientPanel p, LocalPayer payer) =>
        new(p, payer, p.CoPayPercent ?? payer.DefaultCoPayPercent, JsonSerializer.Deserialize<List<PayerPrice>>(payer.PricesJson, LocalJson.Options) ?? []);

    private async Task<PanelInfo> RequirePanelAsync(Guid patientId, Guid panelId)
    {
        var panel = await store.GetAsync<LocalPatientPanel>(panelId) ?? throw new BillingException("Panel not found.");
        if (panel.PatientId != patientId) throw new BillingException("That panel belongs to another patient.");
        var payer = await store.GetAsync<LocalPayer>(panel.PayerId) ?? throw new BillingException("Payer not found.");
        if (!IsValid(panel, UtcNow)) throw new BillingException($"The {payer.Name} panel is not valid today.");
        return Info(panel, payer);
    }

    private async Task<PanelInfo?> PanelOfAsync(LocalInvoice invoice)
    {
        if (invoice.PanelId is not { } pid) return null;
        var panel = await store.GetAsync<LocalPatientPanel>(pid);
        var payer = panel is null ? null : await store.GetAsync<LocalPayer>(panel.PayerId);
        return panel is null || payer is null ? null : Info(panel, payer);
    }

    // ------------------------------------------------------------------ payments (insert-only ledger)

    public async Task<LocalPayment> PayAsync(Guid invoiceId, decimal amount, string method = PaymentMethods.Cash, string? reference = null)
    {
        var summary = await SummaryAsync(invoiceId);
        if (summary.Invoice.Status == InvoiceStatus.Void) throw new BillingException("This invoice is void.");
        if (amount <= 0) throw new BillingException("Enter an amount.");
        if (amount > summary.Due && summary.Invoice.Status == InvoiceStatus.Final)
            throw new BillingException($"Only Rs {summary.Due:N0} is due. Record the rest as a deposit.");
        var n = summary.Payments.Count + 1;
        return await InsertPaymentAsync(summary.Invoice.PatientId, summary.Invoice.BranchId, invoiceId, PaymentKind.Payment, method, amount, reference,
            $"{summary.Invoice.InvoiceNo}-R{n}", null);
    }

    /// <summary>FR-BIL-13: an advance; it lowers the patient balance until it is applied to invoices.</summary>
    public async Task<LocalPayment> DepositAsync(Guid patientId, Guid branchId, decimal amount, string method = PaymentMethods.Cash, string? reference = null)
    {
        if (amount <= 0) throw new BillingException("Enter an amount.");
        _ = await store.GetAsync<LocalPatient>(patientId) ?? throw new BillingException("Patient not found.");
        return await InsertPaymentAsync(patientId, branchId, null, PaymentKind.Deposit, method, amount, reference, null, null);
    }

    /// <summary>FR-BIL-13: settles (part of) an invoice from the patient's deposit credit. No cash moves.</summary>
    public async Task<LocalPayment> ApplyCreditAsync(Guid invoiceId, decimal? amount = null)
    {
        var summary = await SummaryAsync(invoiceId);
        if (summary.Invoice.Status != InvoiceStatus.Final) throw new BillingException("Finalise the invoice first.");
        var credit = (await BalanceAsync(summary.Invoice.PatientId)).Credit;
        var use = Math.Min(amount ?? summary.Due, Math.Min(credit, summary.Due));
        if (use <= 0) throw new BillingException(credit <= 0 ? "The patient has no credit." : "Nothing is due.");
        return await InsertPaymentAsync(summary.Invoice.PatientId, summary.Invoice.BranchId, invoiceId, PaymentKind.Allocation, PaymentMethods.Wallet, use,
            null, $"{summary.Invoice.InvoiceNo}-C{summary.Payments.Count + 1}", null);
    }

    /// <summary>FR-BIL-03: refunds a payment on an invoice (or unused deposit when <paramref name="invoiceId"/> is null).</summary>
    public async Task<LocalPayment> RefundAsync(Guid patientId, Guid branchId, Guid? invoiceId, decimal amount, string reason, string method = PaymentMethods.Cash)
    {
        if (amount <= 0) throw new BillingException("Enter an amount.");
        if (string.IsNullOrWhiteSpace(reason)) throw new BillingException("A refund needs a reason.");
        decimal available;
        if (invoiceId is { } iid)
        {
            var s = await SummaryAsync(iid);
            available = s.Payments.Where(p => p.Kind == PaymentKind.Payment).Sum(p => p.Amount) - s.Refunded;
            branchId = s.Invoice.BranchId;
        }
        else available = (await BalanceAsync(patientId)).Credit;
        if (amount > available) throw new BillingException($"Only Rs {available:N0} can be refunded.");
        return await InsertPaymentAsync(patientId, branchId, invoiceId, PaymentKind.Refund, method, amount, null, null, reason.Trim());
    }

    private Task<LocalPayment> InsertPaymentAsync(Guid patientId, Guid branchId, Guid? invoiceId, string kind, string method, decimal amount,
        string? reference, string? receiptNo, string? reason) =>
        store.InsertAsync(new LocalPayment
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, InvoiceId = invoiceId, Kind = kind, Method = method,
            Amount = LineAmounts.Round(amount), ReceivedAt = UtcNow, ReceivedBy = UserId, Reference = reference, ReceiptNo = receiptNo, Reason = reason,
        });

    public async Task<PatientBalance> BalanceAsync(Guid patientId)
    {
        var finals = await store.ListAsync<LocalInvoice>(i => i.PatientId == patientId && i.Status == InvoiceStatus.Final);
        var ids = finals.Select(i => i.Id).ToHashSet();
        var credits = (await store.ListAsync<LocalCreditNote>(c => c.PatientId == patientId && c.Status == ApprovalStatus.Approved))
            .Where(c => ids.Contains(c.InvoiceId)).Sum(c => c.Amount);
        var invoiced = finals.Sum(i => i.PayerId is null ? i.Total : i.PatientAmount) - credits;
        var pays = await store.ListAsync<LocalPayment>(p => p.PatientId == patientId);
        var deposits = pays.Where(p => p.Kind == PaymentKind.Deposit).Sum(p => p.Amount);
        var depositRefunds = pays.Where(p => p.Kind == PaymentKind.Refund && p.InvoiceId == null).Sum(p => p.Amount);
        var applied = pays.Where(p => p.Kind == PaymentKind.Allocation).Sum(p => p.Amount);
        return new PatientBalance(invoiced, pays.Where(p => p.Kind == PaymentKind.Payment).Sum(p => p.Amount),
            pays.Where(p => p.Kind == PaymentKind.Refund).Sum(p => p.Amount), deposits, deposits - depositRefunds - applied);
    }

    // ------------------------------------------------------------------ credit notes and write-offs (FR-BIL-14)

    public async Task<LocalCreditNote> RequestCreditAsync(Guid invoiceId, string kind, decimal amount, string reason)
    {
        if (!CreditKind.All.Contains(kind)) throw new BillingException($"Unknown kind '{kind}'.");
        if (string.IsNullOrWhiteSpace(reason)) throw new BillingException("Give a reason.");
        var s = await SummaryAsync(invoiceId);
        if (s.Invoice.Status != InvoiceStatus.Final) throw new BillingException("Credit notes are raised on final invoices.");
        var pending = s.Credits.Where(c => c.Status == ApprovalStatus.Requested).Sum(c => c.Amount);
        if (amount <= 0 || amount > s.Due - pending) throw new BillingException($"The amount must be between 1 and {s.Due - pending:N0}.");
        return await store.InsertAsync(new LocalCreditNote
        {
            Id = Guid.NewGuid(), InvoiceId = invoiceId, PatientId = s.Invoice.PatientId, BranchId = s.Invoice.BranchId, Kind = kind,
            Amount = LineAmounts.Round(amount), Reason = reason.Trim(), Status = ApprovalStatus.Requested, RequestedAt = UtcNow, RequestedBy = UserId,
        });
    }

    public async Task<LocalCreditNote> DecideCreditAsync(Guid creditId, bool approve, string? note = null)
    {
        var c = await store.GetAsync<LocalCreditNote>(creditId) ?? throw new BillingException("Request not found.");
        if (c.Status != ApprovalStatus.Requested) throw new BillingException("This request has already been decided.");
        if (c.RequestedBy == UserId) throw new BillingException("Someone else must approve your request.");
        c.Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        c.DecidedAt = UtcNow;
        c.DecidedBy = UserId;
        c.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await store.UpdateAsync(c);
        return c;
    }

    public async Task<List<LocalCreditNote>> PendingCreditsAsync(Guid branchId) =>
        (await store.ListAsync<LocalCreditNote>(c => c.BranchId == branchId && c.Status == ApprovalStatus.Requested)).OrderBy(c => c.RequestedAt).ToList();

    // ------------------------------------------------------------------ cash closing (FR-BIL-06)

    public async Task<DaySummary> DaySummaryAsync(Guid branchId, DateTime date, Guid? userId = null)
    {
        var rows = (await store.ListAsync<LocalPayment>(p => p.BranchId == branchId))
            .Select(p => new PaymentEntry(p.Id, p.ReceivedBy, p.BranchId, p.ReceivedAt, p.Kind, p.Method, p.Amount));
        return CashClosingCalculator.Summarise(rows, date, branchId, userId);
    }

    /// <summary>Closes the signed-in user's cash for the day: expected from their payments, counted from the drawer.</summary>
    public async Task<LocalCashClosing> CloseCashAsync(Guid branchId, DateTime date, decimal countedCash, string? notes = null)
    {
        if (countedCash < 0) throw new BillingException("Counted cash cannot be negative.");
        var summary = await DaySummaryAsync(branchId, date, UserId);
        return await store.InsertAsync(new LocalCashClosing
        {
            Id = Guid.NewGuid(), BranchId = branchId, UserId = UserId, BusinessDate = date.Date, ClosedAt = UtcNow, ExpectedCash = summary.Cash,
            CountedCash = LineAmounts.Round(countedCash), TotalsJson = JsonSerializer.Serialize(summary.Methods, LocalJson.Options),
            PaymentCount = summary.Count, Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        });
    }

    // ------------------------------------------------------------------ receipt (FR-BIL-05) and running bill (FR-BIL-11)

    public async Task<ClinicLetter> BuildReceiptAsync(Guid invoiceId, string clinicName, string branchLine)
    {
        var s = await SummaryAsync(invoiceId);
        var patient = await store.GetAsync<LocalPatient>(s.Invoice.PatientId) ?? throw new BillingException("Patient not found.");
        var inv = s.Invoice;
        var lines = s.Lines.Select(l =>
            $"{l.Description}{(l.Quantity != 1 ? $" × {l.Quantity:0.##}" : "")} — Rs {l.Amount:N0}" + (l.Discount > 0 ? $" (less Rs {l.Discount:N0})" : "")).ToList();
        var totals = new List<string> { $"Subtotal: Rs {inv.Subtotal:N0}" };
        if (inv.Discount > 0) totals.Add($"Discount: Rs {inv.Discount:N0}" + (inv.DiscountReason is null ? "" : $" ({inv.DiscountReason})"));
        if (inv.Tax > 0) totals.Add($"Tax: Rs {inv.Tax:N0}");
        totals.Add($"Total: Rs {inv.Total:N0}");
        if (inv.PayerId is { } payerId)
        {
            var payer = await store.GetAsync<LocalPayer>(payerId);
            var panel = inv.PanelId is { } pid ? await store.GetAsync<LocalPatientPanel>(pid) : null;
            totals.Add($"Billed to {payer?.Name} (member {panel?.MemberNo}): Rs {inv.PayerAmount:N0} · patient pays Rs {inv.PatientAmount:N0}");
        }
        if (s.Credited > 0) totals.Add($"Credit notes / write-offs: Rs {s.Credited:N0}");
        totals.Add($"Paid: Rs {s.Paid - s.Refunded:N0} · Due: Rs {s.Due:N0}");
        var sections = new List<LetterSection> { new("Charges", lines), new("", totals) };
        if (s.Payments.Count > 0)
            sections.Add(new LetterSection("Payments", s.Payments.Select(p =>
                $"{p.ReceivedAt.ToLocalTime():dd MMM HH:mm} · {p.Kind} · {p.Method} · Rs {p.Amount:N0}{(p.ReceiptNo is null ? "" : $" · {p.ReceiptNo}")}").ToList()));
        var title = inv.Status switch { InvoiceStatus.Void => "Invoice (VOID)", InvoiceStatus.Draft => "Estimate (draft)", _ => s.Due == 0 ? "Receipt" : "Invoice" };
        return new ClinicLetter($"{title} {inv.InvoiceNo}", clinicName, branchLine, "", "", patient.FullName, $"MRN {patient.Mrn}",
            inv.IssuedAt.ToLocalTime(), sections, SignatureLabel: "Cashier");
    }

    public sealed record RunningBill(decimal Billed, decimal Paid, decimal Unbilled, IReadOnlyList<ChargeCandidate> Pending, IReadOnlyList<InvoiceSummary> Invoices)
    {
        public decimal Outstanding => Billed - Paid + Unbilled;
    }

    /// <summary>
    /// FR-BIL-11: the interim bill of a case or a day-care stay — what is already invoiced and paid, plus what has been
    /// done and not invoiced yet (room charges so far included). It is finalised by invoicing the pending charges.
    /// </summary>
    public async Task<RunningBill> RunningBillAsync(Guid patientId, Guid? caseId = null, Guid? admissionId = null)
    {
        if (caseId is null && admissionId is null) throw new BillingException("Choose a case or an admission.");
        var admission = admissionId is { } aid ? await store.GetAsync<LocalAdmission>(aid) : null;
        var procedureIdsOfStay = admission is null ? [] : (await store.ListAsync<LocalProcedure>(p => p.AdmissionId == admission.Id)).Select(p => p.Id).ToHashSet();
        bool InScope(ChargeCandidate c) =>
            (caseId is not null && c.CaseId == caseId) ||
            (admission is not null && ((c.Source == ChargeSource.Admission && c.SourceId == admission.Id) || (c.SourceId is { } s && procedureIdsOfStay.Contains(s))));

        var pending = (await UnbilledAsync(patientId, includeRunningStays: true)).Where(InScope).ToList();
        var invoices = new List<InvoiceSummary>();
        foreach (var inv in await ForPatientAsync(patientId))
        {
            if (inv.Status == InvoiceStatus.Void) continue;
            if ((caseId is not null && inv.CaseId == caseId) || (admissionId is not null && inv.AdmissionId == admissionId)) invoices.Add(await SummaryAsync(inv.Id));
        }
        return new RunningBill(invoices.Sum(i => i.PatientShare - i.Credited), invoices.Sum(i => i.Paid - i.Refunded),
            pending.Where(c => c.PackageId is null).Sum(c => c.UnitPrice * c.Quantity), pending, invoices);
    }

    // ------------------------------------------------------------------ helpers

    private static LocalInvoiceLine Line(LocalInvoice invoice, ChargeCandidate c, int sort, IReadOnlyList<PayerPrice>? payerPrices)
    {
        var price = c.PackageId is not null ? 0 : payerPrices is not null && PanelBilling.PriceFor(payerPrices, c.Kind, c.Code) is { } pp ? pp : c.UnitPrice;
        var description = c.PackageId is not null ? $"{c.Description} (package: {c.PackageName})" : c.Description;
        return new LocalInvoiceLine
        {
            Id = Guid.NewGuid(), InvoiceId = invoice.Id, BranchId = invoice.BranchId, Source = c.Source, SourceId = c.SourceId, Kind = c.Kind,
            Description = description.Length > 200 ? description[..200] : description, Quantity = c.Quantity, UnitPrice = LineAmounts.Round(price),
            TaxPercent = c.TaxPercent, Amount = new LineAmounts(c.Quantity, LineAmounts.Round(price), 0, c.TaxPercent).Total, DoctorId = c.DoctorId, SortOrder = sort,
        };
    }

    private static LineAmounts Amounts(LocalInvoiceLine l) => new(l.Quantity, l.UnitPrice, l.Discount, l.TaxPercent);

    /// <summary>The part of the stored discount that is not on lines (kept when lines change).</summary>
    private static decimal InvoiceLevelDiscount(LocalInvoice invoice, IEnumerable<LocalInvoiceLine> lines) =>
        Math.Max(0, invoice.Discount - lines.Sum(l => l.Discount));

    /// <summary>Totals from the lines, then the payer / patient split (FR-BIL-12) — the same arithmetic the server checks.</summary>
    private static void ApplyTotals(LocalInvoice invoice, IReadOnlyCollection<LocalInvoiceLine> lines, decimal invoiceDiscount, decimal? coPay)
    {
        var totals = Billing.Totals(lines.Select(Amounts), invoiceDiscount);
        invoice.Subtotal = totals.Subtotal;
        invoice.Discount = totals.Discount;
        invoice.Tax = totals.Tax;
        invoice.Total = totals.Total;
        if (invoice.PayerId is null || coPay is null)
        {
            invoice.PayerAmount = 0;
            invoice.PatientAmount = totals.Total;
        }
        else (invoice.PayerAmount, invoice.PatientAmount) = PanelBilling.Split(totals.Total, coPay.Value);
    }

    private async Task<(LocalInvoice, List<LocalInvoiceLine>)> RequireDraftAsync(Guid invoiceId)
    {
        var invoice = await store.GetAsync<LocalInvoice>(invoiceId) ?? throw new BillingException("Invoice not found.");
        if (invoice.Status != InvoiceStatus.Draft) throw new BillingException("This invoice is final; void it and issue a new one to change it.");
        return (invoice, await LinesAsync(invoiceId));
    }

    private async Task ChangeLineAsync(Guid lineId, Action<LocalInvoiceLine, List<LocalInvoiceLine>, LocalWriteBatch> change)
    {
        var line = await store.GetAsync<LocalInvoiceLine>(lineId) ?? throw new BillingException("Line not found.");
        var (invoice, lines) = await RequireDraftAsync(line.InvoiceId);
        var panel = await PanelOfAsync(invoice);
        var target = lines.Single(l => l.Id == lineId);
        var invoiceLevel = InvoiceLevelDiscount(invoice, lines);
        await store.WriteAsync(b =>
        {
            change(target, lines, b);
            ApplyTotals(invoice, lines, invoiceLevel, panel?.CoPay);
            b.Update(invoice);
        });
    }
}

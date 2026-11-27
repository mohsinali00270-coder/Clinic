using System.Globalization;
using System.Text;
using System.Text.Json;
using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Clinical;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>
/// §4.13 accounts on the server (online): package and payer masters, message templates, doctor share rules and payout
/// statements (FR-DOC-04/05), payer settlements, claim batches, payer statements with ageing (FR-BIL-12), the tax
/// summary (FR-BIL-15) and the profit view (FR-BIL-17).
/// </summary>
public sealed class AccountsService(IAppDbContext db, ICurrentUser user)
{
    private static readonly JsonSerializerOptions Json = FormJson.Options;

    // ---------------------------------------------------------------- packages

    public async Task<IReadOnlyList<PackagePlanDto>> PackagesAsync(CancellationToken ct) =>
        (await db.PackagePlans.OrderBy(p => p.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<PackagePlanDto> SavePackageAsync(Guid? id, SavePackagePlanRequest r, CancellationToken ct)
    {
        var code = Code(r.Code);
        if (string.IsNullOrWhiteSpace(r.Name)) throw new RequestValidationException("name", "Name is required.");
        if (r.Price < 0) throw new RequestValidationException("price", "Price cannot be negative.");
        if (r.ValidityDays is < 1 or > 1095) throw new RequestValidationException("validityDays", "Validity must be 1–1095 days.");
        if (r.Items.Count == 0 || r.Items.Any(i => !ServiceKind.All.Contains(i.Kind) || i.Count is < 1 or > 365))
            throw new RequestValidationException("items", "Each item needs a known service kind and a count of 1–365.");
        if (await db.PackagePlans.AnyAsync(p => p.Code == code && p.Id != id, ct)) throw new RequestValidationException("code", "Another package uses this code.");
        PackagePlan p;
        if (id is { } pid) p = await db.PackagePlans.FirstOrDefaultAsync(x => x.Id == pid, ct) ?? throw new NotFoundException("Package", pid);
        else { p = new PackagePlan { Id = Guid.CreateVersion7() }; db.PackagePlans.Add(p); }
        p.Code = code;
        p.Name = r.Name.Trim();
        p.Price = r.Price;
        p.ValidityDays = r.ValidityDays;
        p.CaseTypeCode = Normalize.Optional(r.CaseTypeCode);
        p.ItemsJson = JsonSerializer.Serialize(r.Items, Json);
        p.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(p);
    }

    // ---------------------------------------------------------------- payers

    public async Task<IReadOnlyList<PayerDto>> PayersAsync(CancellationToken ct) =>
        (await db.Payers.OrderBy(p => p.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<PayerDto> SavePayerAsync(Guid? id, SavePayerRequest r, CancellationToken ct)
    {
        var code = Code(r.Code);
        if (string.IsNullOrWhiteSpace(r.Name)) throw new RequestValidationException("name", "Name is required.");
        if (!PayerKind.All.Contains(r.Kind)) throw new RequestValidationException("kind", $"Kind must be one of: {string.Join(", ", PayerKind.All)}.");
        if (r.DefaultCoPayPercent is < 0 or > 100) throw new RequestValidationException("defaultCoPayPercent", "Co-pay must be 0–100%.");
        if (r.PaymentTermsDays is < 0 or > 365) throw new RequestValidationException("paymentTermsDays", "Terms must be 0–365 days.");
        if ((r.Prices ?? []).Any(p => !ServiceKind.All.Contains(p.Kind) || p.Price < 0)) throw new RequestValidationException("prices", "Each price needs a known kind and a non-negative price.");
        if (await db.Payers.AnyAsync(p => p.Code == code && p.Id != id, ct)) throw new RequestValidationException("code", "Another payer uses this code.");
        Payer p;
        if (id is { } pid) p = await db.Payers.FirstOrDefaultAsync(x => x.Id == pid, ct) ?? throw new NotFoundException("Payer", pid);
        else { p = new Payer { Id = Guid.CreateVersion7() }; db.Payers.Add(p); }
        p.Code = code;
        p.Name = r.Name.Trim();
        p.Kind = r.Kind;
        p.DefaultCoPayPercent = r.DefaultCoPayPercent;
        p.PaymentTermsDays = r.PaymentTermsDays;
        p.PricesJson = JsonSerializer.Serialize(r.Prices ?? [], Json);
        p.ContactEmail = Normalize.Optional(r.ContactEmail);
        p.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(p);
    }

    public async Task<PayerSettlementDto> AddSettlementAsync(SavePayerSettlementRequest r, CancellationToken ct)
    {
        if (!await db.Payers.AnyAsync(p => p.Id == r.PayerId, ct)) throw new RequestValidationException("payerId", "Payer not found.");
        if (r.Amount <= 0) throw new RequestValidationException("amount", "The amount must be more than zero.");
        var s = new PayerSettlement { Id = Guid.CreateVersion7(), PayerId = r.PayerId, Amount = r.Amount, ReceivedOn = r.ReceivedOn.Date,
            Reference = Normalize.Optional(r.Reference), Notes = Normalize.Optional(r.Notes) };
        db.PayerSettlements.Add(s);
        await db.SaveChangesAsync(ct);
        return new PayerSettlementDto(s.Id, s.PayerId, s.Amount, s.ReceivedOn, s.Reference, s.Notes);
    }

    public async Task<PayerStatementDto> StatementAsync(Guid payerId, DateTime from, DateTime to, CancellationToken ct)
    {
        var payer = await db.Payers.FirstOrDefaultAsync(p => p.Id == payerId, ct) ?? throw new NotFoundException("Payer", payerId);
        var (start, endUtc) = UtcBounds(from, to);
        var end = to.Date.AddDays(1);
        var all = await ClaimsQuery(payerId, DateTime.MinValue, endUtc).ToListAsync(ct);
        var settled = (await db.PayerSettlements.Where(s => s.PayerId == payerId && s.ReceivedOn < end).Select(s => s.Amount).ToListAsync(ct)).Sum();
        var claimed = all.Sum(c => c.PayerAmount);
        var inPeriod = all.Where(c => c.IssuedAt >= start).ToList();
        var ageing = Ageing.Calculate(all.Select(c => (c.IssuedAt, c.PayerAmount)), settled, to.Date);
        return new PayerStatementDto(payerId, payer.Name, from.Date, to.Date, inPeriod, inPeriod.Sum(c => c.PayerAmount), settled,
            Math.Max(0, claimed - settled), ageing);
    }

    /// <summary>FR-BIL-12 claim batch export: CSV of the payer's invoices in the period.</summary>
    public async Task<string> ClaimsCsvAsync(Guid payerId, DateTime from, DateTime to, CancellationToken ct)
    {
        var (start, end) = UtcBounds(from, to);
        var rows = await ClaimsQuery(payerId, start, end).ToListAsync(ct);
        var sb = new StringBuilder("invoice_no,date,patient,mrn,member_no,total,payer_amount,patient_amount\n");
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", Csv(r.InvoiceNo), r.IssuedAt.ToString("yyyy-MM-dd"), Csv(r.PatientName), Csv(r.Mrn), Csv(r.MemberNo),
                r.Total.ToString(CultureInfo.InvariantCulture), r.PayerAmount.ToString(CultureInfo.InvariantCulture), r.PatientAmount.ToString(CultureInfo.InvariantCulture)));
        return sb.ToString();
    }

    private IQueryable<ClaimLineDto> ClaimsQuery(Guid payerId, DateTime from, DateTime toExclusive) =>
        from i in db.Invoices
        join p in db.Patients on i.PatientId equals p.Id
        join pan in db.PatientPanels on i.PanelId equals pan.Id into panels
        from pan in panels.DefaultIfEmpty()
        where i.PayerId == payerId && i.Status == InvoiceStatus.Final && i.IssuedAt >= @from && i.IssuedAt < toExclusive
        orderby i.IssuedAt
        select new ClaimLineDto(i.Id, i.InvoiceNo, i.IssuedAt, p.FullName, p.Mrn, pan == null ? null : pan.MemberNo, i.Total, i.PayerAmount, i.PatientAmount);

    private static string Csv(string? v) => v is null ? "" : v.Contains(',') || v.Contains('"') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    // ---------------------------------------------------------------- message templates

    public async Task<IReadOnlyList<MessageTemplateDto>> TemplatesAsync(CancellationToken ct) =>
        (await db.MessageTemplates.OrderBy(t => t.Key).ThenBy(t => t.Channel).ToListAsync(ct))
        .Select(t => new MessageTemplateDto(t.Id, t.Key, t.Name, t.Channel, t.BodyEn, t.BodyUr, t.IsActive)).ToList();

    public async Task<MessageTemplateDto> SaveTemplateAsync(Guid? id, SaveMessageTemplateRequest r, CancellationToken ct)
    {
        var key = Code(r.Key);
        if (!MessageChannel.All.Contains(r.Channel)) throw new RequestValidationException("channel", "Channel must be sms, whatsapp or email.");
        if (string.IsNullOrWhiteSpace(r.BodyEn) || r.BodyEn.Length > 1000) throw new RequestValidationException("bodyEn", "The English text is required (max 1000).");
        if (await db.MessageTemplates.AnyAsync(t => t.Key == key && t.Channel == r.Channel && t.Id != id, ct))
            throw new RequestValidationException("key", "This template already exists for that channel.");
        MessageTemplate t;
        if (id is { } tid) t = await db.MessageTemplates.FirstOrDefaultAsync(x => x.Id == tid, ct) ?? throw new NotFoundException("Template", tid);
        else { t = new MessageTemplate { Id = Guid.CreateVersion7() }; db.MessageTemplates.Add(t); }
        t.Key = key;
        t.Name = string.IsNullOrWhiteSpace(r.Name) ? key : r.Name.Trim();
        t.Channel = r.Channel;
        t.BodyEn = r.BodyEn.Trim();
        t.BodyUr = Normalize.Optional(r.BodyUr);
        t.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return new MessageTemplateDto(t.Id, t.Key, t.Name, t.Channel, t.BodyEn, t.BodyUr, t.IsActive);
    }

    // ---------------------------------------------------------------- doctor share (FR-DOC-04/05, FR-BIL-18)

    public async Task<IReadOnlyList<ShareRuleDto>> ShareRulesAsync(Guid? doctorId, CancellationToken ct)
    {
        var q = db.DoctorShareRules.AsQueryable();
        if (doctorId is { } d) q = q.Where(r => r.DoctorId == d);
        return await q.Select(r => new ShareRuleDto(r.Id, r.DoctorId, r.Kind, r.Percent, r.FixedAmount)).ToListAsync(ct);
    }

    public async Task<ShareRuleDto> SaveShareRuleAsync(SaveShareRuleRequest r, CancellationToken ct)
    {
        if (!await db.DoctorProfiles.AnyAsync(d => d.Id == r.DoctorId, ct)) throw new RequestValidationException("doctorId", "Doctor not found.");
        if (!ServiceKind.All.Contains(r.Kind)) throw new RequestValidationException("kind", $"Unknown kind '{r.Kind}'.");
        if ((r.Percent is null) == (r.FixedAmount is null)) throw new RequestValidationException("percent", "Give either a percentage or a fixed amount.");
        if (r.Percent is < 0 or > 100 || r.FixedAmount is < 0) throw new RequestValidationException("percent", "Check the share.");
        var rule = await db.DoctorShareRules.FirstOrDefaultAsync(x => x.DoctorId == r.DoctorId && x.Kind == r.Kind, ct);
        if (rule is null) { rule = new DoctorShareRule { Id = Guid.CreateVersion7(), DoctorId = r.DoctorId, Kind = r.Kind }; db.DoctorShareRules.Add(rule); }
        rule.Percent = r.Percent;
        rule.FixedAmount = r.FixedAmount;
        await db.SaveChangesAsync(ct);
        return new ShareRuleDto(rule.Id, rule.DoctorId, rule.Kind, rule.Percent, rule.FixedAmount);
    }

    /// <summary>
    /// The doctor's share of money collected in the period on invoices with their lines: payments and applied credit
    /// in the period, less refunds in the period (FR-BIL-18).
    /// </summary>
    public async Task<(decimal Collected, IReadOnlyList<ShareLine> Lines)> ComputeShareAsync(Guid doctorId, DateTime from, DateTime to, CancellationToken ct)
    {
        var rules = (await db.DoctorShareRules.Where(r => r.DoctorId == doctorId).ToListAsync(ct)).Select(r => new ShareRule(r.Kind, r.Percent, r.FixedAmount)).ToList();
        var (start, end) = UtcBounds(from, to);
        var invoiceIds = await db.InvoiceLines.Where(l => l.DoctorId == doctorId).Select(l => l.InvoiceId).Distinct().ToListAsync(ct);
        var invoices = await db.Invoices.Where(i => invoiceIds.Contains(i.Id) && i.Status == InvoiceStatus.Final).ToListAsync(ct);
        var ids = invoices.Select(i => i.Id).ToList();
        var lines = (await db.InvoiceLines.Where(l => ids.Contains(l.InvoiceId)).ToListAsync(ct)).ToLookup(l => l.InvoiceId);
        var pays = (await db.Payments.Where(p => p.InvoiceId != null && ids.Contains(p.InvoiceId.Value) && p.ReceivedAt >= start && p.ReceivedAt < end)
            .Select(p => new { p.InvoiceId, p.Kind, p.Amount }).ToListAsync(ct)).ToLookup(p => p.InvoiceId!.Value);

        var input = invoices.Select(i =>
        {
            var net = pays[i.Id].Sum(p => PaymentKind.Settles(p.Kind) ? p.Amount : p.Kind == PaymentKind.Refund ? -p.Amount : 0);
            IReadOnlyList<(string, string, decimal, Guid?)> ls = lines[i.Id].Select(l => (l.Description, l.Kind, l.Amount, l.DoctorId)).ToList();
            return (i.Id, i.Total, net, ls);
        }).ToList();
        // Refunds can make a period's net negative for an invoice: reverse the share in proportion (FR-BIL-18).
        var positive = DoctorShare.Calculate(input.Where(x => x.net > 0).Select(x => (x.Id, x.Total, x.net, x.ls)), doctorId, rules);
        var negative = DoctorShare.Calculate(input.Where(x => x.net < 0).Select(x => (x.Id, x.Total, -x.net, x.ls)), doctorId, rules)
            .Select(l => l with { Collected = -l.Collected, Share = -l.Share });
        var all = positive.Concat(negative).ToList();
        return (all.Sum(l => l.Collected), all);
    }

    public async Task<IReadOnlyList<PayoutDto>> PayoutsAsync(Guid? doctorId, CancellationToken ct)
    {
        var q = db.DoctorPayouts.AsQueryable();
        if (doctorId is { } d) q = q.Where(p => p.DoctorId == d);
        var list = await q.OrderByDescending(p => p.From).ToListAsync(ct);
        var names = await db.DoctorProfiles.ToDictionaryAsync(d => d.Id, d => d.FullName, ct);
        return list.Select(p => ToDto(p, names.GetValueOrDefault(p.DoctorId, ""))).ToList();
    }

    public async Task<PayoutDto> CreatePayoutAsync(CreatePayoutRequest r, CancellationToken ct)
    {
        var doctor = await db.DoctorProfiles.FirstOrDefaultAsync(d => d.Id == r.DoctorId, ct) ?? throw new NotFoundException("Doctor", r.DoctorId);
        if (r.To.Date < r.From.Date) throw new RequestValidationException("to", "The period ends before it starts.");
        if (await db.DoctorPayouts.AnyAsync(p => p.DoctorId == r.DoctorId && p.Status != PayoutStatus.Draft && p.From <= r.To.Date && p.To >= r.From.Date, ct))
            throw new RequestValidationException("from", "This period overlaps an approved payout.");
        var (collected, lines) = await ComputeShareAsync(r.DoctorId, r.From, r.To, ct);
        var p = new DoctorPayout
        {
            Id = Guid.CreateVersion7(), DoctorId = r.DoctorId, From = r.From.Date, To = r.To.Date, Collected = collected, Share = lines.Sum(l => l.Share),
            Status = PayoutStatus.Draft, LinesJson = JsonSerializer.Serialize(lines, Json),
        };
        db.DoctorPayouts.Add(p);
        await db.SaveChangesAsync(ct);
        return ToDto(p, doctor.FullName);
    }

    public async Task<PayoutDto> ApprovePayoutAsync(Guid id, CancellationToken ct)
    {
        var p = await db.DoctorPayouts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Payout", id);
        if (p.Status != PayoutStatus.Draft) throw new RequestValidationException("status", "Only a draft statement can be approved.");
        // Recalculate at approval so late payments and refunds are included; then the figures are frozen.
        var (collected, lines) = await ComputeShareAsync(p.DoctorId, p.From, p.To, ct);
        p.Collected = collected;
        p.Share = lines.Sum(l => l.Share);
        p.LinesJson = JsonSerializer.Serialize(lines, Json);
        p.Status = PayoutStatus.Approved;
        p.ApprovedAt = DateTime.UtcNow;
        p.ApprovedBy = user.UserId;
        await db.SaveChangesAsync(ct);
        return ToDto(p, (await db.DoctorProfiles.FirstAsync(d => d.Id == p.DoctorId, ct)).FullName);
    }

    public async Task<PayoutDto> PayPayoutAsync(Guid id, PayPayoutRequest r, CancellationToken ct)
    {
        var p = await db.DoctorPayouts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Payout", id);
        if (p.Status != PayoutStatus.Approved) throw new RequestValidationException("status", "Approve the statement before paying it.");
        p.Status = PayoutStatus.Paid;
        p.PaidAt = DateTime.UtcNow;
        p.PaidBy = user.UserId;
        p.PaymentReference = Normalize.Optional(r.Reference);
        await db.SaveChangesAsync(ct);
        return ToDto(p, (await db.DoctorProfiles.FirstAsync(d => d.Id == p.DoctorId, ct)).FullName);
    }

    // ---------------------------------------------------------------- reports

    /// <summary>
    /// Periods are clinic-local days (as on the device and in the Phase 7 reports); instants are stored in UTC,
    /// so a day starts at local midnight converted to UTC, not at UTC midnight.
    /// </summary>
    private static (DateTime Start, DateTime End) UtcBounds(DateTime from, DateTime to) =>
        (TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(from.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local),
         TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(to.Date.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local));

    public async Task<TaxSummaryDto> TaxAsync(DateTime from, DateTime to, Guid? branchId, CancellationToken ct)
    {
        var (start, end) = UtcBounds(from, to);
        var q = from l in db.InvoiceLines
                join i in db.Invoices on l.InvoiceId equals i.Id
                where i.Status == InvoiceStatus.Final && i.IssuedAt >= start && i.IssuedAt < end && (branchId == null || i.BranchId == branchId)
                select new { l.Quantity, l.UnitPrice, l.Discount, l.TaxPercent };
        var rows = await q.ToListAsync(ct);
        var lines = rows.GroupBy(r => r.TaxPercent).OrderBy(g => g.Key).Select(g =>
        {
            var amounts = g.Select(r => new LineAmounts(r.Quantity, r.UnitPrice, r.Discount, r.TaxPercent)).ToList();
            return new TaxLineDto(g.Key, amounts.Sum(a => a.Net), amounts.Sum(a => a.Tax));
        }).ToList();
        return new TaxSummaryDto(from.Date, to.Date, branchId, lines, lines.Sum(l => l.Tax));
    }

    public async Task<ProfitDto> ProfitAsync(DateTime from, DateTime to, Guid? branchId, CancellationToken ct)
    {
        var (start, endUtc) = UtcBounds(from, to);
        var end = to.Date.AddDays(1); // date-only fields (settlements, expenses)
        var pays = await db.Payments.Where(p => p.ReceivedAt >= start && p.ReceivedAt < endUtc && (branchId == null || p.BranchId == branchId))
            .Select(p => new { p.Kind, p.Amount }).ToListAsync(ct);
        var collected = pays.Where(p => p.Kind is PaymentKind.Payment or PaymentKind.Deposit).Sum(p => p.Amount);
        var refunded = pays.Where(p => p.Kind == PaymentKind.Refund).Sum(p => p.Amount);
        var settlements = branchId is null
            ? (await db.PayerSettlements.Where(s => s.ReceivedOn >= from.Date && s.ReceivedOn < end).Select(s => s.Amount).ToListAsync(ct)).Sum()
            : 0;
        var expenses = await db.Expenses.Where(e => e.SpentOn >= from.Date && e.SpentOn < end && (branchId == null || e.BranchId == branchId))
            .Select(e => new { e.Category, e.Amount }).ToListAsync(ct);
        var payouts = branchId is null
            ? (await db.DoctorPayouts.Where(p => p.Status == PayoutStatus.Paid && p.PaidAt >= start && p.PaidAt < endUtc).Select(p => p.Share).ToListAsync(ct)).Sum()
            : 0;
        return new ProfitDto(from.Date, to.Date, branchId, collected, refunded, settlements, expenses.Sum(e => e.Amount), payouts,
            expenses.GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount)));
    }

    // ---------------------------------------------------------------- defaults

    public static async Task EnsureDefaultsAsync(IAppDbContext db, CancellationToken ct = default)
    {
        var tenant = db.CurrentTenantId;
        var keys = (await db.MessageTemplates.IgnoreQueryFilters().Where(t => t.TenantId == tenant).Select(t => t.Key + "|" + t.Channel).ToListAsync(ct)).ToHashSet();
        foreach (var (key, name, en, ur) in AccountsSeed.Templates)
            foreach (var channel in new[] { MessageChannel.Sms, MessageChannel.WhatsApp })
                if (!keys.Contains(key + "|" + channel))
                    db.MessageTemplates.Add(new MessageTemplate { Id = Guid.CreateVersion7(), Key = key, Name = name, Channel = channel, BodyEn = en, BodyUr = ur });

        var plans = (await db.PackagePlans.IgnoreQueryFilters().Where(p => p.TenantId == tenant).Select(p => p.Code).ToListAsync(ct)).ToHashSet();
        foreach (var (code, name, price, days, caseType, items) in AccountsSeed.Packages.Where(p => !plans.Contains(p.Code)))
            db.PackagePlans.Add(new PackagePlan { Id = Guid.CreateVersion7(), Code = code, Name = name, Price = price, ValidityDays = days, CaseTypeCode = caseType,
                ItemsJson = JsonSerializer.Serialize(items, Json) });
        await db.SaveChangesAsync(ct);
    }

    private static string Code(string? raw)
    {
        var code = raw?.Trim().ToLowerInvariant() ?? "";
        if (code.Length is 0 or > 40 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new RequestValidationException("code", "Code is required (letters, digits, '_' or '-', max 40).");
        return code;
    }

    private static PackagePlanDto ToDto(PackagePlan p) => new(p.Id, p.Code, p.Name, p.Price, p.ValidityDays, p.CaseTypeCode,
        JsonSerializer.Deserialize<List<PackageItem>>(p.ItemsJson, Json) ?? [], p.IsActive);

    private static PayerDto ToDto(Payer p) => new(p.Id, p.Code, p.Name, p.Kind, p.DefaultCoPayPercent, p.PaymentTermsDays,
        JsonSerializer.Deserialize<List<PayerPrice>>(p.PricesJson, Json) ?? [], p.ContactEmail, p.IsActive);

    private static PayoutDto ToDto(DoctorPayout p, string name) => new(p.Id, p.DoctorId, name, p.From, p.To, p.Collected, p.Share, p.Status,
        p.ApprovedAt, p.PaidAt, p.PaymentReference, JsonSerializer.Deserialize<List<ShareLine>>(p.LinesJson, Json) ?? []);
}

public static class AccountsSeed
{
    public static readonly IReadOnlyList<(string Key, string Name, string En, string Ur)> Templates =
    [
        (MessageTemplateKeys.AppointmentReminder, "Appointment reminder",
            "Dear {patient}, your appointment with {doctor} at {clinic} is on {date} at {time}. Reply or call {phone} to change it.",
            "محترم {patient}، {clinic} میں {doctor} کے ساتھ آپ کی ملاقات {date} کو {time} بجے ہے۔"),
        (MessageTemplateKeys.FollowUp, "Follow-up due",
            "Dear {patient}, your follow-up at {clinic} was due on {date}. Please call {phone} to book a time.",
            "محترم {patient}، {clinic} میں آپ کا فالو اَپ {date} کو تھا۔ براہِ کرم وقت لینے کے لیے {phone} پر رابطہ کریں۔"),
        (MessageTemplateKeys.LabReady, "Lab results ready",
            "Dear {patient}, your {test} results are ready at {clinic}.",
            "محترم {patient}، آپ کے {test} کے نتائج {clinic} میں تیار ہیں۔"),
        (MessageTemplateKeys.PaymentDue, "Payment due",
            "Dear {patient}, Rs {amount} is due on invoice {invoice} at {clinic}.",
            "محترم {patient}، {clinic} کے بل {invoice} پر {amount} روپے واجب الادا ہیں۔"),
        (MessageTemplateKeys.Birthday, "Birthday greeting",
            "Happy birthday {patient}! Best wishes from all of us at {clinic}.",
            "سالگرہ مبارک {patient}! {clinic} کی طرف سے نیک تمنائیں۔"),
        (MessageTemplateKeys.Feedback, "Feedback request",
            "Dear {patient}, thank you for visiting {clinic}. How was your visit with {doctor}? Reply with 1 (poor) to 5 (excellent).",
            "محترم {patient}، {clinic} آنے کا شکریہ۔ {doctor} کے ساتھ آپ کا تجربہ کیسا رہا؟ 1 (خراب) سے 5 (بہترین) تک جواب دیں۔"),
    ];

    public static readonly IReadOnlyList<(string Code, string Name, decimal Price, int Days, string? CaseType, PackageItem[] Items)> Packages =
    [
        ("physio_10", "10 physiotherapy sessions", 12000, 120, "physio_course", [new(ServiceKind.Procedure, "physio_session", 10)]),
        ("antenatal", "Antenatal package", 15000, 280, "pregnancy",
            [new(ServiceKind.Consultation, null, 6), new(ServiceKind.Lab, "cbc", 2), new(ServiceKind.Lab, "fbs", 1)]),
    ];
}

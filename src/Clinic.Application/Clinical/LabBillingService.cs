using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Clinical;
using Clinic.Shared.Clinical;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>FR-LAB-01 test menu, FR-BIL-01 fee master (admin, online) and the server's billing queries.</summary>
public sealed class LabBillingService(IAppDbContext db)
{
    // ---------------------------------------------------------------- lab tests

    public async Task<IReadOnlyList<LabTestDto>> LabTestsAsync(CancellationToken ct) =>
        (await db.LabTests.OrderBy(t => t.Department).ThenBy(t => t.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<LabTestDto> SaveLabTestAsync(Guid? id, SaveLabTestRequest r, CancellationToken ct)
    {
        var code = Code(r.Code);
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 150) throw new RequestValidationException("name", "Name is required (max 150).");
        if (!SampleTypes.All.Contains(r.SampleType)) throw new RequestValidationException("sampleType", $"Sample type must be one of: {string.Join(", ", SampleTypes.All)}.");
        if (r.Fee is < 0) throw new RequestValidationException("fee", "Fee cannot be negative.");
        if (r.TurnaroundHours is < 0 or > 720) throw new RequestValidationException("turnaroundHours", "Turnaround must be 0–720 hours.");
        if (await db.LabTests.AnyAsync(t => t.Code == code && t.Id != id, ct)) throw new RequestValidationException("code", "Another test uses this code.");

        LabTest t;
        if (id is { } tid) t = await db.LabTests.FirstOrDefaultAsync(x => x.Id == tid, ct) ?? throw new NotFoundException("Lab test", tid);
        else { t = new LabTest { Id = Guid.CreateVersion7() }; db.LabTests.Add(t); }
        t.Code = code;
        t.Name = r.Name.Trim();
        t.Department = Normalize.Optional(r.Department);
        t.SampleType = r.SampleType;
        t.Fee = r.Fee;
        t.TurnaroundHours = r.TurnaroundHours;
        t.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    // ---------------------------------------------------------------- services (fee master)

    public async Task<IReadOnlyList<ServiceItemDto>> ServicesAsync(CancellationToken ct) =>
        (await db.ServiceItems.OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<ServiceItemDto> SaveServiceAsync(Guid? id, SaveServiceItemRequest r, CancellationToken ct)
    {
        var code = Code(r.Code);
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 150) throw new RequestValidationException("name", "Name is required (max 150).");
        if (!ServiceKind.All.Contains(r.Kind)) throw new RequestValidationException("kind", $"Unknown kind '{r.Kind}'.");
        if (r.Price < 0) throw new RequestValidationException("price", "Price cannot be negative.");
        if (r.TaxPercent is < 0 or > 100) throw new RequestValidationException("taxPercent", "Tax must be 0–100%.");
        if (r.BranchId is { } b && !await db.Branches.AnyAsync(x => x.Id == b, ct)) throw new RequestValidationException("branchId", "Branch not found.");
        if (await db.ServiceItems.AnyAsync(s => s.Code == code && s.BranchId == r.BranchId && s.Id != id, ct))
            throw new RequestValidationException("code", "This code already has a price for that branch.");

        ServiceItem s;
        if (id is { } sid) s = await db.ServiceItems.FirstOrDefaultAsync(x => x.Id == sid, ct) ?? throw new NotFoundException("Service", sid);
        else { s = new ServiceItem { Id = Guid.CreateVersion7() }; db.ServiceItems.Add(s); }
        s.Code = code;
        s.Name = r.Name.Trim();
        s.Kind = r.Kind;
        s.BranchId = r.BranchId;
        s.Price = r.Price;
        s.TaxPercent = r.TaxPercent;
        s.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(s);
    }

    // ---------------------------------------------------------------- queries

    /// <summary>FR-BIL-06 server side of the day closing: the same calculation the devices run.</summary>
    public Task<DaySummary> DaySummaryAsync(Guid branchId, DateTime date, Guid? userId, CancellationToken ct) =>
        LabBillingSyncRules.DaySummaryAsync(db, branchId, date, userId);

    /// <summary>FR-BIL-03/13: what the patient owes (positive) or holds as credit (negative).</summary>
    public async Task<PatientBalanceDto> BalanceAsync(Guid patientId, CancellationToken ct)
    {
        // The patient's share of final invoices less approved credit notes / write-offs (FR-BIL-12/14).
        var finals = await db.Invoices.Where(i => i.PatientId == patientId && i.Status == InvoiceStatus.Final)
            .Select(i => new { i.Id, i.Total, i.PayerId, i.PatientAmount }).ToListAsync(ct);
        var ids = finals.Select(f => f.Id).ToList();
        var credits = (await db.CreditNotes.Where(c => ids.Contains(c.InvoiceId) && c.Status == ApprovalStatus.Approved).Select(c => c.Amount).ToListAsync(ct)).Sum();
        var invoiced = finals.Sum(f => f.PayerId is null ? f.Total : f.PatientAmount) - credits;
        var pays = await db.Payments.Where(p => p.PatientId == patientId).Select(p => new { p.Kind, p.Amount }).ToListAsync(ct);
        var paid = pays.Where(p => p.Kind == PaymentKind.Payment).Sum(p => p.Amount);
        var refunded = pays.Where(p => p.Kind == PaymentKind.Refund).Sum(p => p.Amount);
        var deposits = pays.Where(p => p.Kind == PaymentKind.Deposit).Sum(p => p.Amount);
        return new PatientBalanceDto(patientId, invoiced, paid, refunded, deposits, invoiced - paid - deposits + refunded);
    }

    // ---------------------------------------------------------------- defaults

    public static async Task EnsureDefaultsAsync(IAppDbContext db, CancellationToken ct = default)
    {
        var tenant = db.CurrentTenantId;
        var tests = (await db.LabTests.IgnoreQueryFilters().Where(t => t.TenantId == tenant).Select(t => t.Code).ToListAsync(ct)).ToHashSet();
        foreach (var (code, name, dept, sample, fee, hours) in LabBillingSeed.LabTests.Where(t => !tests.Contains(t.Code)))
            db.LabTests.Add(new LabTest { Id = Guid.CreateVersion7(), Code = code, Name = name, Department = dept, SampleType = sample, Fee = fee, TurnaroundHours = hours });

        var services = (await db.ServiceItems.IgnoreQueryFilters().Where(s => s.TenantId == tenant).Select(s => s.Code).ToListAsync(ct)).ToHashSet();
        foreach (var (code, name, kind, price) in LabBillingSeed.Services.Where(s => !services.Contains(s.Code)))
            db.ServiceItems.Add(new ServiceItem { Id = Guid.CreateVersion7(), Code = code, Name = name, Kind = kind, Price = price });
        await db.SaveChangesAsync(ct);
    }

    private static string Code(string? raw)
    {
        var code = raw?.Trim().ToLowerInvariant() ?? "";
        if (code.Length is 0 or > 40 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new RequestValidationException("code", "Code is required (letters, digits, '_' or '-', max 40).");
        return code;
    }

    private static LabTestDto ToDto(LabTest t) => new(t.Id, t.Code, t.Name, t.Department, t.SampleType, t.Fee, t.TurnaroundHours, t.IsActive);
    private static ServiceItemDto ToDto(ServiceItem s) => new(s.Id, s.Code, s.Name, s.Kind, s.BranchId, s.Price, s.TaxPercent, s.IsActive);
}

/// <summary>A starter test menu and fee list (Pakistani clinic prices; admins edit them).</summary>
public static class LabBillingSeed
{
    public static readonly IReadOnlyList<(string Code, string Name, string Dept, string Sample, decimal Fee, int Hours)> LabTests =
    [
        ("cbc", "Complete blood count (CBC)", "Haematology", "blood", 800, 6),
        ("esr", "ESR", "Haematology", "blood", 300, 6),
        ("fbs", "Fasting blood sugar", "Chemistry", "plasma", 250, 2),
        ("rbs", "Random blood sugar", "Chemistry", "plasma", 250, 2),
        ("hba1c", "HbA1c", "Chemistry", "blood", 1500, 24),
        ("lipid", "Lipid profile", "Chemistry", "serum", 1800, 24),
        ("lft", "Liver function tests", "Chemistry", "serum", 1500, 24),
        ("rft", "Renal function (urea, creatinine)", "Chemistry", "serum", 1200, 24),
        ("urine_re", "Urine routine examination", "Microscopy", "urine", 400, 4),
        ("hbsag", "HBsAg (screening)", "Serology", "serum", 600, 24),
        ("anti_hcv", "Anti-HCV (screening)", "Serology", "serum", 700, 24),
        ("tsh", "TSH", "Endocrinology", "serum", 1400, 48),
    ];

    public static readonly IReadOnlyList<(string Code, string Name, string Kind, decimal Price)> Services =
    [
        ("registration", "Registration / file fee", ServiceKind.Other, 200),
        ("medical_certificate", "Medical certificate", ServiceKind.Other, 500),
        ("ecg", "ECG", ServiceKind.Procedure, 800),
        ("nebulisation", "Nebulisation", ServiceKind.Procedure, 500),
        ("bp_check", "BP / sugar check", ServiceKind.Other, 100),
    ];
}

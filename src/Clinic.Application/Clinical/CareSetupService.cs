using System.Text.Json;
using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Clinical;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>FR-CASE-02, FR-PRC-01, FR-PRC-03: case types, procedure master and beds (admin, online; devices sync them).</summary>
public sealed class CareSetupService(IAppDbContext db)
{
    private static readonly JsonSerializerOptions Json = FormJson.Options;

    // ---------------------------------------------------------------- case types

    public async Task<IReadOnlyList<CaseTypeDto>> CaseTypesAsync(CancellationToken ct) =>
        (await db.CaseTypes.OrderBy(t => t.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<CaseTypeDto> SaveCaseTypeAsync(Guid? id, SaveCaseTypeRequest r, CancellationToken ct)
    {
        var code = Code(r.Code);
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 150) throw new RequestValidationException("name", "Name is required (max 150).");
        if (r.ExpectedSessions is < 0 or > 500) throw new RequestValidationException("expectedSessions", "Expected sessions must be 0–500.");
        if (r.FollowUpIntervalDays is < 0 or > 730) throw new RequestValidationException("followUpIntervalDays", "Follow-up interval must be 0–730 days.");
        foreach (var step in r.DefaultPlan ?? [])
        {
            if (!CarePlanStepKind.All.Contains(step.Kind)) throw new RequestValidationException("defaultPlan", $"Unknown step kind '{step.Kind}'.");
            if (string.IsNullOrWhiteSpace(step.Title)) throw new RequestValidationException("defaultPlan", "Every plan step needs a title.");
            if (step.OffsetDays is < 0 or > 730) throw new RequestValidationException("defaultPlan", "Plan offsets must be 0–730 days.");
        }
        if (await db.CaseTypes.AnyAsync(t => t.Code == code && t.Id != id, ct)) throw new RequestValidationException("code", "Another case type uses this code.");

        CaseType t;
        if (id is { } tid) t = await db.CaseTypes.FirstOrDefaultAsync(x => x.Id == tid, ct) ?? throw new NotFoundException("Case type", tid);
        else { t = new CaseType { Id = Guid.CreateVersion7() }; db.CaseTypes.Add(t); }
        t.Code = code;
        t.Name = r.Name.Trim();
        t.NameUr = Normalize.Optional(r.NameUr);
        t.Specialty = Normalize.Optional(r.Specialty);
        t.ExpectedSessions = r.ExpectedSessions;
        t.FollowUpIntervalDays = r.FollowUpIntervalDays;
        t.DefaultServices = Normalize.Optional(r.DefaultServices);
        t.DefaultPlanJson = JsonSerializer.Serialize(r.DefaultPlan ?? [], Json);
        t.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    // ---------------------------------------------------------------- procedure types

    public async Task<IReadOnlyList<ProcedureTypeDto>> ProcedureTypesAsync(CancellationToken ct) =>
        (await db.ProcedureTypes.OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<ProcedureTypeDto> SaveProcedureTypeAsync(Guid? id, SaveProcedureTypeRequest r, CancellationToken ct)
    {
        var code = Code(r.Code);
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 150) throw new RequestValidationException("name", "Name is required (max 150).");
        if (!ProcedureCategory.All.Contains(r.Category)) throw new RequestValidationException("category", $"Unknown category '{r.Category}'.");
        if (r.Fee is < 0) throw new RequestValidationException("fee", "Fee cannot be negative.");
        if (r.DurationMinutes is < 1 or > 1440) throw new RequestValidationException("durationMinutes", "Duration must be 1–1440 minutes.");
        if (r.RequiresConsent && string.IsNullOrWhiteSpace(r.ConsentTextEn))
            throw new RequestValidationException("consentTextEn", "A procedure that needs consent needs the consent wording.");
        if ((r.Consumables ?? []).Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Quantity <= 0))
            throw new RequestValidationException("consumables", "Each consumable needs a name and a positive quantity.");
        if (await db.ProcedureTypes.AnyAsync(t => t.Code == code && t.Id != id, ct)) throw new RequestValidationException("code", "Another procedure uses this code.");

        ProcedureType t;
        if (id is { } tid) t = await db.ProcedureTypes.FirstOrDefaultAsync(x => x.Id == tid, ct) ?? throw new NotFoundException("Procedure type", tid);
        else { t = new ProcedureType { Id = Guid.CreateVersion7() }; db.ProcedureTypes.Add(t); }
        t.Code = code;
        t.Name = r.Name.Trim();
        t.Category = r.Category;
        t.Fee = r.Fee;
        t.DurationMinutes = r.DurationMinutes;
        t.RequiresConsent = r.RequiresConsent;
        t.ConsentTextEn = Normalize.Optional(r.ConsentTextEn);
        t.ConsentTextUr = Normalize.Optional(r.ConsentTextUr);
        t.ConsumablesJson = JsonSerializer.Serialize(r.Consumables ?? [], Json);
        t.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    // ---------------------------------------------------------------- beds

    public async Task<IReadOnlyList<BedDto>> BedsAsync(Guid? branchId, CancellationToken ct)
    {
        var q = db.Beds.AsQueryable();
        if (branchId is { } b) q = q.Where(x => x.BranchId == b);
        return (await q.OrderBy(x => x.Ward).ThenBy(x => x.Code).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<BedDto> SaveBedAsync(Guid? id, SaveBedRequest r, CancellationToken ct)
    {
        if (!await db.Branches.AnyAsync(b => b.Id == r.BranchId, ct)) throw new RequestValidationException("branchId", "Branch not found.");
        var code = r.Code?.Trim().ToUpperInvariant() ?? "";
        if (code.Length is 0 or > 30) throw new RequestValidationException("code", "Code is required (max 30).");
        if (!BedKind.All.Contains(r.Kind)) throw new RequestValidationException("kind", $"Unknown kind '{r.Kind}'.");
        if (!ChargeUnit.All.Contains(r.ChargeUnit)) throw new RequestValidationException("chargeUnit", "Charge per 'day' or 'hour'.");
        if (r.ChargeAmount < 0) throw new RequestValidationException("chargeAmount", "Charge cannot be negative.");
        if (await db.Beds.AnyAsync(x => x.BranchId == r.BranchId && x.Code == code && x.Id != id, ct))
            throw new RequestValidationException("code", "This branch already has a bed with that code.");

        Bed bed;
        if (id is { } bid)
        {
            bed = await db.Beds.FirstOrDefaultAsync(x => x.Id == bid, ct) ?? throw new NotFoundException("Bed", bid);
            if (bed.BranchId != r.BranchId && await db.Admissions.AnyAsync(a => a.BedId == bid && a.Status == AdmissionStatus.Admitted, ct))
                throw new RequestValidationException("branchId", "A bed with a patient in it cannot move to another branch.");
        }
        else { bed = new Bed { Id = Guid.CreateVersion7() }; db.Beds.Add(bed); }
        bed.BranchId = r.BranchId;
        bed.Code = code;
        bed.Ward = Normalize.Optional(r.Ward);
        bed.Kind = r.Kind;
        bed.ChargeAmount = r.ChargeAmount;
        bed.ChargeUnit = r.ChargeUnit;
        bed.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(bed);
    }

    // ---------------------------------------------------------------- defaults

    /// <summary>Starter case types and procedures for a new or upgraded tenant (idempotent, by code).</summary>
    public static async Task EnsureDefaultsAsync(IAppDbContext db, CancellationToken ct = default)
    {
        var tenant = db.CurrentTenantId;
        var caseCodes = (await db.CaseTypes.IgnoreQueryFilters().Where(t => t.TenantId == tenant).Select(t => t.Code).ToListAsync(ct)).ToHashSet();
        foreach (var d in CareSeed.CaseTypes.Where(d => !caseCodes.Contains(d.Code)))
        {
            db.CaseTypes.Add(new CaseType
            {
                Id = Guid.CreateVersion7(), Code = d.Code, Name = d.Name, NameUr = d.NameUr, Specialty = d.Specialty,
                ExpectedSessions = d.Sessions, FollowUpIntervalDays = d.Interval, DefaultPlanJson = JsonSerializer.Serialize(d.Plan, Json),
            });
        }

        var procCodes = (await db.ProcedureTypes.IgnoreQueryFilters().Where(t => t.TenantId == tenant).Select(t => t.Code).ToListAsync(ct)).ToHashSet();
        foreach (var d in CareSeed.Procedures.Where(d => !procCodes.Contains(d.Code)))
        {
            db.ProcedureTypes.Add(new ProcedureType
            {
                Id = Guid.CreateVersion7(), Code = d.Code, Name = d.Name, Category = d.Category, Fee = d.Fee, DurationMinutes = d.Minutes,
                RequiresConsent = d.Consent, ConsentTextEn = d.Consent ? CareSeed.ConsentEn(d.Name) : null,
                ConsentTextUr = d.Consent ? CareSeed.ConsentUr : null, ConsumablesJson = JsonSerializer.Serialize(d.Consumables, Json),
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private static string Code(string? raw)
    {
        var code = raw?.Trim().ToLowerInvariant() ?? "";
        if (code.Length is 0 or > 40 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new RequestValidationException("code", "Code is required (letters, digits, '_' or '-', max 40).");
        return code;
    }

    private static CaseTypeDto ToDto(CaseType t) => new(t.Id, t.Code, t.Name, t.NameUr, t.Specialty, t.ExpectedSessions,
        t.FollowUpIntervalDays, t.DefaultServices, JsonSerializer.Deserialize<List<CasePlanTemplateStep>>(t.DefaultPlanJson, Json) ?? [], t.IsActive);

    private static ProcedureTypeDto ToDto(ProcedureType t) => new(t.Id, t.Code, t.Name, t.Category, t.Fee, t.DurationMinutes, t.RequiresConsent,
        t.ConsentTextEn, t.ConsentTextUr, JsonSerializer.Deserialize<List<Consumable>>(t.ConsumablesJson, Json) ?? [], t.IsActive);

    private static BedDto ToDto(Bed b) => new(b.Id, b.BranchId, b.Code, b.Ward, b.Kind, b.ChargeAmount, b.ChargeUnit, b.IsActive);
}

/// <summary>Starter masters so a clinic can open cases and record procedures on day one. Admins edit them freely.</summary>
public static class CareSeed
{
    public sealed record SeedCaseType(string Code, string Name, string? NameUr, string? Specialty, int? Sessions, int? Interval, CasePlanTemplateStep[] Plan);

    public static readonly IReadOnlyList<SeedCaseType> CaseTypes =
    [
        new("general", "General episode", "عمومی علاج", null, null, 14, []),
        new("pregnancy", "Pregnancy (antenatal care)", "حمل کی نگہداشت", "gynae", 8, 28,
        [
            new(CarePlanStepKind.Visit, "Booking visit", 0),
            new(CarePlanStepKind.Test, "Booking bloods (CBC, blood group, HBsAg, HCV)", 0),
            new(CarePlanStepKind.Test, "Dating ultrasound", 7),
            new(CarePlanStepKind.Visit, "Antenatal review", 28),
            new(CarePlanStepKind.Test, "Anomaly scan", 70),
            new(CarePlanStepKind.Visit, "Antenatal review", 84),
        ]),
        new("physio_course", "Physiotherapy course", "فزیو تھراپی کورس", "physio", 10, 2,
        [
            new(CarePlanStepKind.Visit, "Assessment", 0),
            new(CarePlanStepKind.Procedure, "Session 2", 2), new(CarePlanStepKind.Procedure, "Session 3", 4),
            new(CarePlanStepKind.Procedure, "Session 4", 6), new(CarePlanStepKind.Visit, "Progress review", 14),
        ]),
        new("diabetes", "Diabetes follow-up", "ذیابیطس فالو اَپ", "general", null, 90,
        [
            new(CarePlanStepKind.Test, "HbA1c and fasting glucose", 0),
            new(CarePlanStepKind.Visit, "Diabetes review", 30),
            new(CarePlanStepKind.Test, "Repeat HbA1c", 90),
            new(CarePlanStepKind.Visit, "Diabetes review", 92),
        ]),
        new("root_canal", "Root canal treatment", "روٹ کینال", "dental", 3, 7,
        [
            new(CarePlanStepKind.Procedure, "Access and cleaning", 0),
            new(CarePlanStepKind.Procedure, "Obturation", 7),
            new(CarePlanStepKind.Procedure, "Crown", 21),
        ]),
    ];

    public sealed record SeedProcedure(string Code, string Name, string Category, decimal Fee, int Minutes, bool Consent, Consumable[] Consumables);

    public static readonly IReadOnlyList<SeedProcedure> Procedures =
    [
        new("dressing", "Wound dressing", ProcedureCategory.Dressing, 500, 15, false,
            [new("Gauze pack", 2), new("Crepe bandage", 1), new("Povidone-iodine", 10, "ml")]),
        new("suturing", "Suturing of laceration", ProcedureCategory.MinorSurgery, 2500, 30, true,
            [new("Suture 3-0", 1), new("Lignocaine 2%", 5, "ml"), new("Sterile gloves", 2, "pair")]),
        new("i_and_d", "Incision and drainage of abscess", ProcedureCategory.MinorSurgery, 3000, 30, true,
            [new("Scalpel blade", 1), new("Lignocaine 2%", 5, "ml"), new("Gauze pack", 3)]),
        new("im_injection", "IM injection", ProcedureCategory.Injection, 200, 5, false, [new("Syringe 5 ml", 1)]),
        new("iv_cannula", "IV cannulation and drip", ProcedureCategory.Injection, 800, 20, false,
            [new("IV cannula", 1), new("Drip set", 1), new("Normal saline 500 ml", 1)]),
        new("scaling", "Scaling and polishing", ProcedureCategory.Dental, 3000, 30, false, []),
        new("extraction", "Tooth extraction", ProcedureCategory.Dental, 2500, 30, true, [new("Lignocaine cartridge", 1)]),
        new("rct_session", "Root canal session", ProcedureCategory.Dental, 6000, 45, true, [new("Files set", 1), new("Lignocaine cartridge", 1)]),
        new("physio_session", "Physiotherapy session", ProcedureCategory.Physiotherapy, 1500, 45, false, []),
    ];

    public static string ConsentEn(string procedure) =>
        $"I agree to undergo {procedure.ToLowerInvariant()}. The doctor has explained the purpose, the benefits, the likely risks " +
        "(including pain, bleeding, infection and reaction to the anaesthetic) and the alternatives. I have been able to ask questions " +
        "and I understand that I may withdraw my consent before the procedure starts.";

    public const string ConsentUr =
        "میں اس طریقۂ علاج کے لیے رضامند ہوں۔ ڈاکٹر نے مجھے اس کا مقصد، فوائد، ممکنہ خطرات اور متبادل طریقے سمجھا دیے ہیں۔ " +
        "مجھے سوال پوچھنے کا موقع دیا گیا اور میں جانتا/جانتی ہوں کہ میں علاج شروع ہونے سے پہلے اپنی رضامندی واپس لے سکتا/سکتی ہوں۔";
}

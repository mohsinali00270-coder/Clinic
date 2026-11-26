using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Application.Forms;
using Clinic.Domain.Clinical;
using Clinic.Shared.Clinical;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

public static class ClinicalRoutes
{
    public const string Drugs = "api/v1/drugs";
    public const string Icd10 = "api/v1/icd10";
}

public sealed record DrugDto(Guid Id, string Name, string? Generic, string? Strength, string? Form, string? Route,
    string? DefaultDose, string? DefaultFrequency, int? DefaultDurationDays, string? AllergyClass, bool IsActive);

public sealed record SaveDrugRequest(string Name, string? Generic, string? Strength, string? Form, string? Route,
    string? DefaultDose, string? DefaultFrequency, int? DefaultDurationDays, string? AllergyClass, bool IsActive = true);

public sealed record Icd10Dto(Guid Id, string Code, string Description, bool IsCommon);

public sealed record CsvImportRequest(string Csv);

public sealed record CsvImportResult(int Added, int Updated, IReadOnlyList<string> Errors);

/// <summary>FR-RX-01 drug master and FR-ENC-04 ICD-10 list (admin, online; devices get them through sync).</summary>
public sealed class ClinicalMasterService(IAppDbContext db)
{
    public async Task<IReadOnlyList<DrugDto>> DrugsAsync(string? search, CancellationToken ct)
    {
        var q = db.Drugs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(d => d.Name.Contains(search) || (d.Generic != null && d.Generic.Contains(search)));
        return (await q.OrderBy(d => d.Name).Take(500).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<DrugDto> SaveDrugAsync(Guid? id, SaveDrugRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) throw new RequestValidationException("name", "Name is required (max 200).");
        if (r.DefaultFrequency is { } f && Frequencies.Find(f) is null) throw new RequestValidationException("defaultFrequency", $"Unknown frequency '{f}'.");
        if (r.DefaultDurationDays is < 0 or > 365) throw new RequestValidationException("defaultDurationDays", "Duration must be 0–365 days.");

        Drug d;
        if (id is { } did) d = await db.Drugs.FirstOrDefaultAsync(x => x.Id == did, ct) ?? throw new NotFoundException("Drug", did);
        else { d = new Drug { Id = Guid.CreateVersion7() }; db.Drugs.Add(d); }
        d.Name = r.Name.Trim();
        d.Generic = Normalize.Optional(r.Generic);
        d.Strength = Normalize.Optional(r.Strength);
        d.Form = Normalize.Optional(r.Form);
        d.Route = Normalize.Optional(r.Route);
        d.DefaultDose = Normalize.Optional(r.DefaultDose);
        d.DefaultFrequency = Normalize.Optional(r.DefaultFrequency);
        d.DefaultDurationDays = r.DefaultDurationDays;
        d.AllergyClass = Normalize.Optional(r.AllergyClass)?.ToLowerInvariant();
        d.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(d);
    }

    /// <summary>CSV: name,generic,strength,form,route,defaultDose,defaultFrequency,defaultDurationDays,allergyClass. Matched by name+strength.</summary>
    public async Task<CsvImportResult> ImportDrugsAsync(string csv, CancellationToken ct)
    {
        var rows = LookupService.ParseCsv(csv);
        if (rows.Count < 2) throw new RequestValidationException("csv", "The file needs a header row and at least one drug.");
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string n) => header.IndexOf(n.ToLowerInvariant());
        if (Col("name") < 0) throw new RequestValidationException("csv", "The header must include 'name'.");

        var existing = await db.Drugs.ToListAsync(ct);
        int added = 0, updated = 0;
        var errors = new List<string>();
        for (var r = 1; r < rows.Count; r++)
        {
            string? Cell(string name) => Col(name) is var c and >= 0 && c < rows[r].Count && rows[r][c].Trim() is { Length: > 0 } v ? v : null;
            var name = Cell("name");
            if (name is null) continue;
            var freq = Cell("defaultFrequency");
            if (freq is not null && Frequencies.Find(freq) is null) { errors.Add($"Line {r + 1}: unknown frequency '{freq}'."); continue; }

            var strength = Cell("strength");
            var d = existing.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.Strength == strength);
            if (d is null) { d = new Drug { Id = Guid.CreateVersion7(), Name = name, Strength = strength }; db.Drugs.Add(d); existing.Add(d); added++; }
            else updated++;
            d.Generic = Cell("generic") ?? d.Generic;
            d.Form = Cell("form") ?? d.Form;
            d.Route = Cell("route") ?? d.Route;
            d.DefaultDose = Cell("defaultDose") ?? d.DefaultDose;
            d.DefaultFrequency = freq ?? d.DefaultFrequency;
            d.DefaultDurationDays = int.TryParse(Cell("defaultDurationDays"), out var days) ? days : d.DefaultDurationDays;
            d.AllergyClass = Cell("allergyClass")?.ToLowerInvariant() ?? d.AllergyClass;
            d.IsActive = true;
        }
        await db.SaveChangesAsync(ct);
        return new CsvImportResult(added, updated, errors);
    }

    public async Task<IReadOnlyList<Icd10Dto>> IcdAsync(string? search, CancellationToken ct)
    {
        var q = db.Icd10Codes.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(c => c.Code.StartsWith(search) || c.Description.Contains(search));
        return await q.OrderBy(c => c.Code).Take(200).Select(c => new Icd10Dto(c.Id, c.Code, c.Description, c.IsCommon)).ToListAsync(ct);
    }

    /// <summary>CSV: code,description[,isCommon]. Load the full ICD-10 list your country licenses (e.g. WHO or ICD-10-CM).</summary>
    public async Task<CsvImportResult> ImportIcdAsync(string csv, CancellationToken ct)
    {
        var rows = LookupService.ParseCsv(csv);
        if (rows.Count < 2) throw new RequestValidationException("csv", "The file needs a header row and at least one code.");
        var existing = (await db.Icd10Codes.ToListAsync(ct)).ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        int added = 0, updated = 0;
        var errors = new List<string>();
        for (var r = 1; r < rows.Count; r++)
        {
            if (rows[r].Count < 2 || rows[r][0].Trim() is not { Length: > 0 } code) continue;
            var description = rows[r][1].Trim();
            if (code.Length > 10 || description.Length is 0 or > 300) { errors.Add($"Line {r + 1}: invalid code or description."); continue; }
            var common = rows[r].Count > 2 && rows[r][2].Trim() is "1" or "true" or "yes";
            if (!existing.TryGetValue(code, out var c))
            {
                c = new Icd10Code { Id = Guid.CreateVersion7(), Code = code.ToUpperInvariant() };
                db.Icd10Codes.Add(c);
                existing[code] = c;
                added++;
            }
            else updated++;
            c.Description = description;
            c.IsCommon = common || c.IsCommon;
        }
        await db.SaveChangesAsync(ct);
        return new CsvImportResult(added, updated, errors);
    }

    /// <summary>Starter masters for a new or upgraded tenant (idempotent).</summary>
    public static async Task EnsureDefaultsAsync(IAppDbContext db, CancellationToken ct = default)
    {
        var tenant = db.CurrentTenantId;
        var codes = (await db.Icd10Codes.IgnoreQueryFilters().Where(c => c.TenantId == tenant).Select(c => c.Code).ToListAsync(ct)).ToHashSet();
        foreach (var (code, description) in ClinicalSeed.CommonIcd10.Where(x => !codes.Contains(x.Code)))
            db.Icd10Codes.Add(new Icd10Code { Id = Guid.CreateVersion7(), Code = code, Description = description, IsCommon = true });

        var hasDrugs = await db.Drugs.IgnoreQueryFilters().AnyAsync(d => d.TenantId == tenant, ct);
        if (!hasDrugs)
        {
            foreach (var d in ClinicalSeed.Drugs)
            {
                db.Drugs.Add(new Drug
                {
                    Id = Guid.CreateVersion7(), Name = d.Name, Generic = d.Generic, Strength = d.Strength, Form = d.Form, Route = d.Route,
                    DefaultDose = d.Dose, DefaultFrequency = d.Frequency, DefaultDurationDays = d.Days, AllergyClass = d.Allergy,
                });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static DrugDto ToDto(Drug d) => new(d.Id, d.Name, d.Generic, d.Strength, d.Form, d.Route, d.DefaultDose,
        d.DefaultFrequency, d.DefaultDurationDays, d.AllergyClass, d.IsActive);
}

/// <summary>
/// A starter set so a new clinic can prescribe and code diagnoses on day one. The full ICD-10 list should be
/// imported from the edition the clinic is licensed to use.
/// </summary>
public static class ClinicalSeed
{
    public static readonly IReadOnlyList<(string Code, string Description)> CommonIcd10 =
    [
        ("A09", "Infectious gastroenteritis and colitis, unspecified"),
        ("A01.0", "Typhoid fever"),
        ("A15.0", "Tuberculosis of lung"),
        ("A90", "Dengue fever [classical dengue]"),
        ("B34.9", "Viral infection, unspecified"),
        ("B54", "Unspecified malaria"),
        ("B35.4", "Tinea corporis"),
        ("B86", "Scabies"),
        ("B18.1", "Chronic viral hepatitis B without delta-agent"),
        ("B18.2", "Chronic viral hepatitis C"),
        ("D50.9", "Iron deficiency anaemia, unspecified"),
        ("D64.9", "Anaemia, unspecified"),
        ("E03.9", "Hypothyroidism, unspecified"),
        ("E05.9", "Thyrotoxicosis, unspecified"),
        ("E11.9", "Type 2 diabetes mellitus without complications"),
        ("E10.9", "Type 1 diabetes mellitus without complications"),
        ("E55.9", "Vitamin D deficiency, unspecified"),
        ("E66.9", "Obesity, unspecified"),
        ("E78.5", "Hyperlipidaemia, unspecified"),
        ("E86", "Volume depletion"),
        ("F32.9", "Depressive episode, unspecified"),
        ("F41.1", "Generalized anxiety disorder"),
        ("F51.0", "Nonorganic insomnia"),
        ("G43.9", "Migraine, unspecified"),
        ("G44.2", "Tension-type headache"),
        ("G40.9", "Epilepsy, unspecified"),
        ("H10.9", "Conjunctivitis, unspecified"),
        ("H66.9", "Otitis media, unspecified"),
        ("H61.2", "Impacted cerumen"),
        ("I10", "Essential (primary) hypertension"),
        ("I20.9", "Angina pectoris, unspecified"),
        ("I21.9", "Acute myocardial infarction, unspecified"),
        ("I48", "Atrial fibrillation and flutter"),
        ("I50.9", "Heart failure, unspecified"),
        ("I63.9", "Cerebral infarction, unspecified"),
        ("I83.9", "Varicose veins of lower extremities without ulcer or inflammation"),
        ("J00", "Acute nasopharyngitis [common cold]"),
        ("J01.9", "Acute sinusitis, unspecified"),
        ("J02.9", "Acute pharyngitis, unspecified"),
        ("J03.9", "Acute tonsillitis, unspecified"),
        ("J06.9", "Acute upper respiratory infection, unspecified"),
        ("J11.1", "Influenza with other respiratory manifestations, virus not identified"),
        ("J18.9", "Pneumonia, unspecified"),
        ("J20.9", "Acute bronchitis, unspecified"),
        ("J30.4", "Allergic rhinitis, unspecified"),
        ("J44.9", "Chronic obstructive pulmonary disease, unspecified"),
        ("J45.9", "Asthma, unspecified"),
        ("K02.9", "Dental caries, unspecified"),
        ("K04.0", "Pulpitis"),
        ("K04.7", "Periapical abscess without sinus"),
        ("K05.1", "Chronic gingivitis"),
        ("K08.1", "Loss of teeth due to accident, extraction or local periodontal disease"),
        ("K21.9", "Gastro-oesophageal reflux disease without oesophagitis"),
        ("K25.9", "Gastric ulcer, unspecified"),
        ("K29.7", "Gastritis, unspecified"),
        ("K30", "Dyspepsia"),
        ("K35.8", "Acute appendicitis, other and unspecified"),
        ("K40.9", "Unilateral or unspecified inguinal hernia, without obstruction or gangrene"),
        ("K58.9", "Irritable bowel syndrome without diarrhoea"),
        ("K59.0", "Constipation"),
        ("K64.9", "Haemorrhoids, unspecified"),
        ("K80.2", "Calculus of gallbladder without cholecystitis"),
        ("L02.9", "Cutaneous abscess, furuncle and carbuncle, unspecified"),
        ("L20.9", "Atopic dermatitis, unspecified"),
        ("L30.9", "Dermatitis, unspecified"),
        ("L50.9", "Urticaria, unspecified"),
        ("L70.0", "Acne vulgaris"),
        ("M17.9", "Gonarthrosis, unspecified"),
        ("M25.5", "Pain in joint"),
        ("M54.2", "Cervicalgia"),
        ("M54.5", "Low back pain"),
        ("M62.8", "Other specified disorders of muscle"),
        ("M75.1", "Rotator cuff syndrome"),
        ("M79.1", "Myalgia"),
        ("M81.9", "Osteoporosis, unspecified"),
        ("N18.9", "Chronic kidney disease, unspecified"),
        ("N20.0", "Calculus of kidney"),
        ("N39.0", "Urinary tract infection, site not specified"),
        ("N76.0", "Acute vaginitis"),
        ("N92.6", "Irregular menstruation, unspecified"),
        ("N94.6", "Dysmenorrhoea, unspecified"),
        ("N97.9", "Female infertility, unspecified"),
        ("O21.0", "Mild hyperemesis gravidarum"),
        ("O24.4", "Diabetes mellitus arising in pregnancy"),
        ("O13", "Gestational [pregnancy-induced] hypertension without significant proteinuria"),
        ("O99.0", "Anaemia complicating pregnancy, childbirth and the puerperium"),
        ("Z34.9", "Supervision of normal pregnancy, unspecified"),
        ("Z00.0", "General medical examination"),
        ("Z00.1", "Routine child health examination"),
        ("Z23", "Need for immunization against single bacterial diseases"),
        ("Z30.0", "General counselling and advice on contraception"),
        ("P59.9", "Neonatal jaundice, unspecified"),
        ("R05", "Cough"),
        ("R06.0", "Dyspnoea"),
        ("R10.4", "Other and unspecified abdominal pain"),
        ("R11", "Nausea and vomiting"),
        ("R42", "Dizziness and giddiness"),
        ("R50.9", "Fever, unspecified"),
        ("R51", "Headache"),
        ("R53", "Malaise and fatigue"),
        ("S06.0", "Concussion"),
        ("S52.5", "Fracture of lower end of radius"),
        ("S93.4", "Sprain and strain of ankle"),
        ("T14.1", "Open wound of unspecified body region"),
        ("T78.4", "Allergy, unspecified"),
    ];

    public sealed record SeedDrug(string Name, string? Generic, string Strength, string Form, string Route, string? Dose, string? Frequency, int? Days, string? Allergy = null);

    public static readonly IReadOnlyList<SeedDrug> Drugs =
    [
        new("Paracetamol", "Paracetamol", "500 mg", "Tablet", "oral", "1 tablet", "tds", 3),
        new("Paracetamol syrup", "Paracetamol", "120 mg/5 ml", "Syrup", "oral", "5 ml", "tds", 3),
        new("Ibuprofen", "Ibuprofen", "400 mg", "Tablet", "oral", "1 tablet", "pc_tds", 5, AllergyClasses.Nsaids),
        new("Diclofenac sodium", "Diclofenac", "50 mg", "Tablet", "oral", "1 tablet", "bd", 5, AllergyClasses.Nsaids),
        new("Mefenamic acid", "Mefenamic acid", "250 mg", "Capsule", "oral", "1 capsule", "pc_tds", 3, AllergyClasses.Nsaids),
        new("Aspirin", "Acetylsalicylic acid", "75 mg", "Tablet", "oral", "1 tablet", "od", 30, AllergyClasses.Nsaids),
        new("Tramadol", "Tramadol", "50 mg", "Capsule", "oral", "1 capsule", "bd", 3),
        new("Amoxicillin", "Amoxicillin", "500 mg", "Capsule", "oral", "1 capsule", "tds", 5, AllergyClasses.Penicillin),
        new("Amoxicillin suspension", "Amoxicillin", "250 mg/5 ml", "Suspension", "oral", "5 ml", "tds", 5, AllergyClasses.Penicillin),
        new("Co-amoxiclav", "Amoxicillin + clavulanic acid", "625 mg", "Tablet", "oral", "1 tablet", "bd", 5, AllergyClasses.Penicillin),
        new("Azithromycin", "Azithromycin", "500 mg", "Tablet", "oral", "1 tablet", "od", 3),
        new("Clarithromycin", "Clarithromycin", "500 mg", "Tablet", "oral", "1 tablet", "bd", 7),
        new("Ciprofloxacin", "Ciprofloxacin", "500 mg", "Tablet", "oral", "1 tablet", "bd", 5),
        new("Cefixime", "Cefixime", "400 mg", "Capsule", "oral", "1 capsule", "od", 7),
        new("Doxycycline", "Doxycycline", "100 mg", "Capsule", "oral", "1 capsule", "bd", 7),
        new("Metronidazole", "Metronidazole", "400 mg", "Tablet", "oral", "1 tablet", "tds", 5),
        new("Co-trimoxazole", "Sulfamethoxazole + trimethoprim", "960 mg", "Tablet", "oral", "1 tablet", "bd", 5, AllergyClasses.Sulfa),
        new("Omeprazole", "Omeprazole", "20 mg", "Capsule", "oral", "1 capsule", "ac_bd", 14),
        new("Esomeprazole", "Esomeprazole", "40 mg", "Capsule", "oral", "1 capsule", "od", 14),
        new("Famotidine", "Famotidine", "20 mg", "Tablet", "oral", "1 tablet", "bd", 14),
        new("Domperidone", "Domperidone", "10 mg", "Tablet", "oral", "1 tablet", "tds", 3),
        new("Ondansetron", "Ondansetron", "4 mg", "Tablet", "oral", "1 tablet", "tds", 3),
        new("Hyoscine butylbromide", "Hyoscine butylbromide", "10 mg", "Tablet", "oral", "1 tablet", "tds", 3),
        new("ORS", "Oral rehydration salts", "1 sachet", "Sachet", "oral", "1 sachet in 1 litre water", "sos", 3),
        new("Zinc sulfate", "Zinc", "20 mg", "Tablet", "oral", "1 tablet", "od", 10),
        new("Loratadine", "Loratadine", "10 mg", "Tablet", "oral", "1 tablet", "od", 5),
        new("Cetirizine", "Cetirizine", "10 mg", "Tablet", "oral", "1 tablet", "hs", 5),
        new("Fexofenadine", "Fexofenadine", "120 mg", "Tablet", "oral", "1 tablet", "od", 7),
        new("Montelukast", "Montelukast", "10 mg", "Tablet", "oral", "1 tablet", "hs", 30),
        new("Salbutamol inhaler", "Salbutamol", "100 mcg/puff", "Inhaler", "inhaled", "2 puffs", "sos", 30),
        new("Prednisolone", "Prednisolone", "5 mg", "Tablet", "oral", "As directed", "od", 5),
        new("Metformin", "Metformin", "500 mg", "Tablet", "oral", "1 tablet", "bd", 30),
        new("Glimepiride", "Glimepiride", "2 mg", "Tablet", "oral", "1 tablet", "od", 30),
        new("Sitagliptin", "Sitagliptin", "50 mg", "Tablet", "oral", "1 tablet", "od", 30),
        new("Insulin glargine", "Insulin glargine", "100 IU/ml", "Injection", "sc", "As directed", "hs", 30),
        new("Amlodipine", "Amlodipine", "5 mg", "Tablet", "oral", "1 tablet", "od", 30),
        new("Losartan", "Losartan", "50 mg", "Tablet", "oral", "1 tablet", "od", 30),
        new("Bisoprolol", "Bisoprolol", "5 mg", "Tablet", "oral", "1 tablet", "od", 30),
        new("Atorvastatin", "Atorvastatin", "20 mg", "Tablet", "oral", "1 tablet", "hs", 30),
        new("Clopidogrel", "Clopidogrel", "75 mg", "Tablet", "oral", "1 tablet", "od", 30),
        new("Furosemide", "Furosemide", "40 mg", "Tablet", "oral", "1 tablet", "od", 7),
        new("Hydrochlorothiazide", "Hydrochlorothiazide", "25 mg", "Tablet", "oral", "1 tablet", "od", 30, AllergyClasses.Sulfa),
        new("Levothyroxine", "Levothyroxine", "50 mcg", "Tablet", "oral", "1 tablet before breakfast", "od", 30),
        new("Folic acid", "Folic acid", "5 mg", "Tablet", "oral", "1 tablet", "od", 30),
        new("Ferrous sulfate", "Ferrous sulfate", "200 mg", "Tablet", "oral", "1 tablet", "bd", 30),
        new("Calcium + vitamin D3", "Calcium carbonate + cholecalciferol", "500 mg/400 IU", "Tablet", "oral", "1 tablet", "od", 30),
        new("Clotrimazole cream", "Clotrimazole", "1%", "Cream", "topical", "Apply thinly", "bd", 14),
        new("Fusidic acid cream", "Fusidic acid", "2%", "Cream", "topical", "Apply thinly", "tds", 7),
        new("Chlorhexidine mouthwash", "Chlorhexidine", "0.2%", "Mouthwash", "oral", "10 ml rinse", "bd", 7),
    ];
}

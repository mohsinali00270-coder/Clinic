using System.Text.Json;
using System.Text.Json.Nodes;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Clinical;

public sealed record TemplateItem(Guid? DrugId, string DrugName, string? Strength, string? Form, string? Dose, string? Frequency,
    int? DurationDays, string? Route, string? Instructions);

/// <summary>Everything printed on a prescription (FR-RX-04), assembled offline from local data.</summary>
public sealed record PrescriptionDocument(
    string ClinicName,
    string BranchLine,
    string DoctorName,
    string DoctorDetails,
    string PatientName,
    string PatientDetails,
    DateTime Date,
    IReadOnlyList<string> Vitals,
    IReadOnlyList<string> Diagnoses,
    IReadOnlyList<PrescriptionLine> Lines,
    IReadOnlyList<string> Advice,
    DateTime? FollowUp,
    bool IsDraft,
    bool IncludeUrdu);

public sealed record PrescriptionLine(string Medicine, string Dose, string FrequencyEnglish, string FrequencyUrdu, string Duration, string? Instructions);

/// <summary>§4.4 on the device: drug master search, items with defaults, favourites, warnings, and the printable document.</summary>
public sealed class PrescriptionService(LocalStore store, DynamicDataService forms, EncounterService encounters)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<List<LocalDrug>> SearchDrugsAsync(string query, int limit = 30)
    {
        var q = query.Trim();
        var drugs = await store.ListAsync<LocalDrug>(d => d.IsActive);
        return drugs
            .Where(d => q.Length == 0 || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (d.Generic?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(d => d.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(d => d.Name)
            .Take(limit).ToList();
    }

    public async Task<LocalPrescription?> ForEncounterAsync(Guid encounterId) =>
        (await store.ListAsync<LocalPrescription>(p => p.EncounterId == encounterId)).FirstOrDefault();

    public async Task<List<LocalPrescriptionItem>> ItemsAsync(Guid encounterId)
    {
        var rx = await ForEncounterAsync(encounterId);
        if (rx is null) return [];
        var id = rx.Id;
        return (await store.ListAsync<LocalPrescriptionItem>(i => i.PrescriptionId == id)).OrderBy(i => i.SortOrder).ToList();
    }

    /// <summary>FR-RX-01: adds a medicine with the drug's default dose, frequency, duration and route.</summary>
    public async Task<LocalPrescriptionItem> AddDrugAsync(Guid encounterId, LocalDrug drug, string? amendReason = null) =>
        await AddItemAsync(encounterId, new TemplateItem(drug.Id, drug.Name, drug.Strength, drug.Form, drug.DefaultDose,
            drug.DefaultFrequency, drug.DefaultDurationDays, drug.Route, null), amendReason);

    public async Task<LocalPrescriptionItem> AddItemAsync(Guid encounterId, TemplateItem t, string? amendReason = null)
    {
        var e = await encounters.RequireEncounterAsync(encounterId);
        if (t.Frequency is { } f && Frequencies.Find(f) is null) throw new EncounterException($"Unknown frequency '{f}'.");
        var rx = await ForEncounterAsync(encounterId);
        var items = await ItemsAsync(encounterId);

        var item = new LocalPrescriptionItem
        {
            Id = Guid.NewGuid(), BranchId = e.BranchId, DrugId = t.DrugId, DrugName = t.DrugName, Strength = t.Strength, Form = t.Form,
            Dose = t.Dose, Frequency = t.Frequency, DurationDays = t.DurationDays, Route = t.Route, Instructions = t.Instructions,
            SortOrder = items.Count == 0 ? 10 : items.Max(i => i.SortOrder) + 10,
        };
        await encounters.WriteClinicalAsync(e, EncounterAmendKeys.Prescription, null, Describe(item), amendReason, b =>
        {
            if (rx is null)
            {
                rx = new LocalPrescription
                {
                    Id = Guid.NewGuid(), EncounterId = e.Id, PatientId = e.PatientId, DoctorId = e.DoctorId, BranchId = e.BranchId,
                    IssuedAt = DateTime.UtcNow,
                };
                b.Insert(rx);
            }
            item.PrescriptionId = rx.Id;
            b.Insert(item);
        });
        return item;
    }

    public async Task UpdateItemAsync(LocalPrescriptionItem item, string? amendReason = null)
    {
        var e = await EncounterOfAsync(item);
        if (item.Frequency is { } f && Frequencies.Find(f) is null) throw new EncounterException($"Unknown frequency '{f}'.");
        var before = await store.GetAsync<LocalPrescriptionItem>(item.Id);
        await encounters.WriteClinicalAsync(e, EncounterAmendKeys.Prescription, before is null ? null : Describe(before), Describe(item), amendReason,
            b => b.Update(item));
    }

    public async Task RemoveItemAsync(Guid itemId, string? amendReason = null)
    {
        var item = await store.GetAsync<LocalPrescriptionItem>(itemId) ?? throw new EncounterException("Item not found.");
        var e = await EncounterOfAsync(item);
        await encounters.WriteClinicalAsync(e, EncounterAmendKeys.Prescription, Describe(item), null, amendReason,
            b => b.Delete<LocalPrescriptionItem>(itemId));
    }

    private async Task<LocalEncounter> EncounterOfAsync(LocalPrescriptionItem item)
    {
        var rx = await store.GetAsync<LocalPrescription>(item.PrescriptionId) ?? throw new EncounterException("Prescription not found.");
        return await encounters.RequireEncounterAsync(rx.EncounterId);
    }

    public static string Describe(LocalPrescriptionItem i) => AmendmentText.PrescriptionItem(i.DrugName, i.Strength, i.Dose, i.Frequency, i.DurationDays);

    // ------------------------------------------------------------------ warnings (FR-RX-03)

    /// <summary>
    /// Allergy warnings (patient's recorded allergies vs the drug's allergy class or name) and duplicate therapy
    /// (same generic twice in this prescription, or still running from another prescription in the last 60 days).
    /// </summary>
    public async Task<List<DrugWarning>> WarningsAsync(Guid encounterId)
    {
        var e = await encounters.RequireEncounterAsync(encounterId);
        var items = await ItemsAsync(encounterId);
        var drugs = (await store.ListAsync<LocalDrug>()).ToDictionary(d => d.Id);
        var warnings = new List<DrugWarning>();

        var patient = await store.GetAsync<LocalPatient>(e.PatientId);
        var allergies = new List<string>();
        var allergyText = "";
        if (patient?.DynamicData is { Length: > 0 } dd && JsonNode.Parse(dd) is JsonObject values)
        {
            allergies = FormValues.AsStringList(values["allergies"]).Select(a => a.ToLowerInvariant()).ToList();
            allergyText = FormValues.AsString(values["allergy_notes"])?.ToLowerInvariant() ?? "";
        }

        string Generic(LocalPrescriptionItem i) =>
            (i.DrugId is { } id && drugs.TryGetValue(id, out var d) ? d.Generic ?? d.Name : i.DrugName).ToLowerInvariant();

        foreach (var item in items)
        {
            var drug = item.DrugId is { } id ? drugs.GetValueOrDefault(id) : null;
            var cls = drug?.AllergyClass?.ToLowerInvariant();
            var generic = Generic(item);
            if (cls is not null && allergies.Contains(cls))
                warnings.Add(new DrugWarning("allergy", $"{item.DrugName}: patient is allergic to {cls}.", true));
            else if (allergyText.Length > 0 && (allergyText.Contains(generic) || allergyText.Contains(item.DrugName.ToLowerInvariant())))
                warnings.Add(new DrugWarning("allergy", $"{item.DrugName}: mentioned in the patient's allergy notes ({allergyText}).", true));
        }

        foreach (var dup in items.GroupBy(Generic).Where(g => g.Count() > 1))
            warnings.Add(new DrugWarning("duplicate", $"{dup.Key} is prescribed {dup.Count()} times in this prescription.", false));

        // Still-running medicines from other prescriptions of this patient.
        var today = DateTime.UtcNow.Date;
        var others = (await store.ListAsync<LocalPrescription>(p => p.PatientId == e.PatientId)).Where(p => p.EncounterId != encounterId && p.IssuedAt > today.AddDays(-60));
        var current = items.Select(Generic).ToHashSet();
        foreach (var other in others)
        {
            var otherId = other.Id;
            foreach (var i in await store.ListAsync<LocalPrescriptionItem>(x => x.PrescriptionId == otherId))
            {
                var g = Generic(i);
                if (current.Contains(g) && other.IssuedAt.Date.AddDays(i.DurationDays ?? 0) >= today)
                    warnings.Add(new DrugWarning("duplicate", $"{i.DrugName} is still running from a prescription of {other.IssuedAt:dd MMM}.", false));
            }
        }
        return warnings.DistinctBy(w => w.Message).ToList();
    }

    // ------------------------------------------------------------------ favourites (FR-RX-02)

    public async Task<List<LocalPrescriptionTemplate>> TemplatesAsync(Guid doctorId) =>
        (await store.ListAsync<LocalPrescriptionTemplate>(t => t.DoctorId == doctorId)).OrderBy(t => t.Name).ToList();

    public async Task<LocalPrescriptionTemplate> SaveAsTemplateAsync(Guid encounterId, string name)
    {
        var e = await encounters.RequireEncounterAsync(encounterId);
        var items = await ItemsAsync(encounterId);
        if (items.Count == 0) throw new EncounterException("Add medicines before saving a favourite.");
        var template = new LocalPrescriptionTemplate
        {
            Id = Guid.NewGuid(), DoctorId = e.DoctorId, Name = name.Trim(),
            ItemsJson = JsonSerializer.Serialize(items.Select(i => new TemplateItem(i.DrugId, i.DrugName, i.Strength, i.Form, i.Dose,
                i.Frequency, i.DurationDays, i.Route, i.Instructions)).ToList(), Json),
        };
        await store.InsertAsync(template);
        return template;
    }

    /// <summary>One tap: adds every medicine of the favourite.</summary>
    public async Task<int> ApplyTemplateAsync(Guid encounterId, Guid templateId, string? amendReason = null)
    {
        var template = await store.GetAsync<LocalPrescriptionTemplate>(templateId) ?? throw new EncounterException("Favourite not found.");
        var items = JsonSerializer.Deserialize<List<TemplateItem>>(template.ItemsJson, Json) ?? [];
        foreach (var item in items) await AddItemAsync(encounterId, item, amendReason);
        return items.Count;
    }

    // ------------------------------------------------------------------ printable document (FR-RX-04)

    public async Task<PrescriptionDocument> BuildDocumentAsync(Guid encounterId, string clinicName, bool includeUrdu = true)
    {
        var e = await encounters.RequireEncounterAsync(encounterId);
        var patient = await store.GetAsync<LocalPatient>(e.PatientId);
        var doctor = await store.GetAsync<LocalDoctor>(e.DoctorId);
        var branch = await store.GetAsync<LocalBranch>(e.BranchId);
        var specialty = (await store.ListAsync<LocalLookupItem>(i => i.ListKey == "specialties"))
            .FirstOrDefault(i => i.Code == doctor?.Specialty)?.LabelEn ?? doctor?.Specialty;

        var age = PatientRules.AgeYears(patient?.DateOfBirth, DateTime.Today);
        var patientDetails = $"MRN {patient?.Mrn} · {patient?.Gender}" + (age is { } a ? $" · {a} y" : "") +
                             (patient?.Phone is { } ph ? $" · {ph}" : "");

        // Vitals: the latest record of this encounter (or today's) with its form labels.
        var vitalsLines = new List<string>();
        if (await encounters.LatestVitalsAsync(e.PatientId) is { } v && (v.Record.EncounterId == e.Id || v.Record.RecordedAt.Date == e.StartedAt.Date) &&
            v.Record.FormVersionId is { } vv && await forms.GetVersionAsync(vv) is { } vform)
        {
            foreach (var f in vform.Definition.AllFields.Where(f => !FormValues.IsEmpty(v.Values[f.Key])))
                vitalsLines.Add($"{f.Label.En}: {FormValues.AsString(v.Values[f.Key])}{(f.Unit is { } u ? " " + u : "")}");
        }

        var diagnoses = (await encounters.DiagnosesAsync(e.Id)).Select(d => (d.Code is null ? "" : d.Code + " — ") + d.Description).ToList();

        var lines = (await ItemsAsync(e.Id)).Select(i =>
        {
            var freq = Frequencies.Find(i.Frequency);
            return new PrescriptionLine(
                $"{i.DrugName} {i.Strength} {(i.Form is { } f ? $"({f})" : "")}".Trim(),
                i.Dose ?? "",
                freq?.English ?? i.Frequency ?? "",
                freq?.Urdu ?? "",
                i.DurationDays is { } d && d > 0 ? $"{d} days" : "",
                i.Instructions);
        }).ToList();

        // FR-DYN-07: encounter fields flagged "print on prescription" (e.g. plan / advice).
        var advice = new List<string>();
        if (await encounters.FormAsync(e) is { } form)
        {
            var values = await forms.LoadValuesAsync(SyncEntityNames.Encounter, e.Id);
            foreach (var f in form.Definition.AllFields.Where(f => f.PrintOnPrescription && !FormValues.IsEmpty(values[f.Key])))
                advice.Add(FormValues.AsString(values[f.Key]) is { } text && values[f.Key] is not JsonArray
                    ? text
                    : string.Join(", ", FormValues.AsStringList(values[f.Key])));
        }

        return new PrescriptionDocument(
            clinicName,
            string.Join(" · ", new[] { branch?.Name, branch?.Address, branch?.Phone }.Where(s => !string.IsNullOrWhiteSpace(s))),
            doctor?.FullName ?? "",
            string.Join(" · ", new[] { specialty, doctor?.RegistrationNo is { } r ? $"Reg. {r}" : null }.Where(s => s is not null)),
            patient?.FullName ?? "",
            patientDetails,
            (e.SignedAt ?? e.StartedAt).ToLocalTime(),
            vitalsLines, diagnoses, lines, advice, e.FollowUpDate,
            IsDraft: !EncounterStatus.IsLocked(e.Status),
            includeUrdu);
    }
}

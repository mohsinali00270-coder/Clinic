using System.Text.Json;
using System.Text.Json.Nodes;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Clinical;

public sealed record ProcedureLinks(Guid? EncounterId = null, Guid? CaseId = null, Guid? AdmissionId = null, Guid? DoctorId = null);

/// <summary>
/// §4.12 procedures on the device (FR-PRC-01/02): plan from the master (fee and consumables snapshot), write the
/// procedure-note form, capture consent with a signature before starting, complete. Works offline.
/// </summary>
public sealed class ProcedureService(LocalStore store, DynamicDataService forms, AttachmentService attachments, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private Guid UserId => store.Context.UserId;

    public const string ConsentCategory = "consent";

    public async Task<List<LocalProcedureType>> TypesAsync(string? category = null) =>
        (await store.ListAsync<LocalProcedureType>(t => t.IsActive))
        .Where(t => category is null || t.Category == category)
        .OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();

    public static IReadOnlyList<Consumable> Consumables(string json) =>
        JsonSerializer.Deserialize<List<Consumable>>(json, LocalJson.Options) ?? [];

    public static EvaluationContext ContextFor(LocalProcedure p, IReadOnlyCollection<string> roles, string language = "en") => new()
    {
        Roles = roles,
        Language = language,
        IsNewRecord = p.Status is not (ProcedureStatus.Done or ProcedureStatus.Cancelled),
        Variables = new Dictionary<string, object?>(PatientRules.FormVariables(p.PatientGender, p.PatientDateOfBirth, DateTime.Today))
        {
            ["procedure"] = p.ProcedureCode,
        },
    };

    public async Task<LocalProcedure> PlanAsync(Guid patientId, Guid branchId, Guid procedureTypeId, ProcedureLinks? links = null, DateTime? plannedAt = null)
    {
        links ??= new ProcedureLinks();
        var patient = await store.GetAsync<LocalPatient>(patientId) ?? throw new CareException("Patient not found.");
        var type = await store.GetAsync<LocalProcedureType>(procedureTypeId) ?? throw new CareException("Procedure not found.");
        if (!type.IsActive) throw new CareException($"'{type.Name}' is no longer offered.");

        var caseId = links.CaseId;
        if (caseId is null && links.EncounterId is { } eid && await store.GetAsync<LocalEncounter>(eid) is { } enc) caseId = enc.CaseId;
        if (caseId is { } cid && await store.GetAsync<LocalCase>(cid) is { } c && (c.PatientId != patientId || CaseStatus.IsFinished(c.Status)))
            throw new CareException("That case is closed or belongs to another patient.");

        var version = await forms.ResolveFormAsync(SyncEntityNames.Procedure, branchId, type.Code);
        var p = new LocalProcedure
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, ProcedureTypeId = type.Id, ProcedureCode = type.Code, Name = type.Name,
            Category = type.Category, Fee = type.Fee, ConsumablesJson = type.ConsumablesJson, EncounterId = links.EncounterId, CaseId = caseId,
            AdmissionId = links.AdmissionId, DoctorId = links.DoctorId, PlannedAt = plannedAt ?? UtcNow, Status = ProcedureStatus.Planned,
            ConsentRequired = type.RequiresConsent, PatientGender = patient.Gender, PatientDateOfBirth = patient.DateOfBirth,
            FormVersionId = version?.Id,
        };
        return await store.InsertAsync(p);
    }

    public Task<LocalProcedure?> GetAsync(Guid id) => store.GetAsync<LocalProcedure>(id);

    public async Task<List<LocalProcedure>> ForPatientAsync(Guid patientId) =>
        (await store.ListAsync<LocalProcedure>(p => p.PatientId == patientId)).OrderByDescending(p => p.PlannedAt ?? p.CreatedAt).ToList();

    public async Task<List<LocalProcedure>> ForEncounterAsync(Guid encounterId) =>
        (await store.ListAsync<LocalProcedure>(p => p.EncounterId == encounterId)).OrderBy(p => p.CreatedAt).ToList();

    public Task<FormVersionInfo?> FormAsync(LocalProcedure p) =>
        p.FormVersionId is { } v ? forms.GetVersionAsync(v) : Task.FromResult<FormVersionInfo?>(null);

    public async Task SaveNoteAsync(Guid procedureId, JsonObject values)
    {
        var p = await RequireActiveAsync(procedureId);
        var version = await FormAsync(p) ?? throw new CareException("No procedure-note form is published for this procedure.");
        await DynamicDataService.SaveRecordAsync(store, p, isNew: false, version, values, SyncEntityNames.Procedure, p.BranchId);
    }

    public async Task UpdateConsumablesAsync(Guid procedureId, IReadOnlyList<Consumable> used)
    {
        var p = await RequireActiveAsync(procedureId);
        if (used.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Quantity <= 0)) throw new CareException("Each consumable needs a name and a quantity.");
        p.ConsumablesJson = JsonSerializer.Serialize(used, LocalJson.Options);
        await store.UpdateAsync(p);
    }

    // ------------------------------------------------------------------ consent (FR-PRC-02)

    /// <summary>
    /// Saves the signature image as an encrypted attachment of the procedure (it uploads like any file) and records
    /// who signed. The attachment is written first so the server can verify it when the consent arrives.
    /// </summary>
    public async Task<LocalProcedure> RecordConsentAsync(Guid procedureId, string signerName, string relation, byte[] signaturePng, string? witness = null)
    {
        var p = await RequireActiveAsync(procedureId);
        if (p.Status != ProcedureStatus.Planned) throw new CareException("Consent is taken before the procedure starts.");
        if (string.IsNullOrWhiteSpace(signerName)) throw new CareException("Enter the name of the person signing.");
        if (!ConsentRelation.All.Contains(relation)) throw new CareException($"Unknown relation '{relation}'.");
        if (signaturePng.Length < 100) throw new CareException("Ask the patient to sign in the box.");

        using var stream = new MemoryStream(signaturePng);
        var file = await attachments.AddAsync(SyncEntityNames.Procedure, p.Id, p.BranchId, stream, $"consent-{p.ProcedureCode}.png", "image/png",
            $"Consent · {p.Name} · {signerName.Trim()}", ConsentCategory);

        p.ConsentAttachmentId = file.Id;
        p.ConsentSignedAt = UtcNow;
        p.ConsentSignerName = signerName.Trim();
        p.ConsentRelation = relation;
        p.ConsentWitness = string.IsNullOrWhiteSpace(witness) ? null : witness.Trim();
        await store.UpdateAsync(p);
        return p;
    }

    public async Task<LocalProcedure> StartAsync(Guid procedureId)
    {
        var p = await RequireActiveAsync(procedureId);
        if (p.ConsentRequired && p.ConsentSignedAt is null)
            throw new CareException($"{p.Name} needs the patient's signed consent before it starts.");
        p.Status = ProcedureStatus.InProgress;
        p.StartedAt ??= UtcNow;
        p.PerformedBy ??= UserId;
        await store.UpdateAsync(p);
        return p;
    }

    /// <summary>Completes the procedure (starting it first if needed) and saves the final note.</summary>
    public async Task<LocalProcedure> CompleteAsync(Guid procedureId, JsonObject? note = null)
    {
        var p = await RequireActiveAsync(procedureId);
        if (p.ConsentRequired && p.ConsentSignedAt is null)
            throw new CareException($"{p.Name} needs the patient's signed consent before it is done.");
        if (note is not null)
        {
            var version = await FormAsync(p);
            if (version is not null)
            {
                var check = version.Evaluator.Evaluate(note, ContextFor(p, []));
                if (check.Errors.FirstOrDefault() is { Error: { } error }) throw new CareException(error);
                await DynamicDataService.SaveRecordAsync(store, p, isNew: false, version, check.Values, SyncEntityNames.Procedure, p.BranchId);
                p = (await store.GetAsync<LocalProcedure>(procedureId))!;
            }
        }
        p.StartedAt ??= UtcNow;
        p.PerformedBy ??= UserId;
        p.Status = ProcedureStatus.Done;
        p.CompletedAt = UtcNow;

        // FR-CASE-05: the first matching planned procedure step of the case is done with it.
        LocalCarePlanStep? step = null;
        if (p.CaseId is { } cid)
            step = (await store.ListAsync<LocalCarePlanStep>(s => s.CaseId == cid && s.Status == CarePlanStepStatus.Planned && s.Kind == CarePlanStepKind.Procedure))
                .OrderBy(s => s.SortOrder).FirstOrDefault();
        await store.WriteAsync(b =>
        {
            b.Update(p);
            if (step is null) return;
            step.Status = CarePlanStepStatus.Done;
            step.CompletedAt = UtcNow;
            step.CompletedBy = UserId;
            step.LinkedRecordId = p.Id;
            b.Update(step);
        });
        return p;
    }

    public async Task<LocalProcedure> CancelAsync(Guid procedureId, string reason)
    {
        var p = await RequireActiveAsync(procedureId);
        if (string.IsNullOrWhiteSpace(reason)) throw new CareException("Give a reason for cancelling.");
        p.Status = ProcedureStatus.Cancelled;
        p.CancelReason = reason.Trim();
        await store.UpdateAsync(p);
        return p;
    }

    /// <summary>A printable consent form (procedure, wording in English and Urdu, the signature) — FR-PRC-02.</summary>
    public async Task<ClinicLetter> BuildConsentAsync(Guid procedureId, string clinicName, string branchLine, IFileTransport? transport = null)
    {
        var p = await store.GetAsync<LocalProcedure>(procedureId) ?? throw new CareException("Procedure not found.");
        var patient = await store.GetAsync<LocalPatient>(p.PatientId) ?? throw new CareException("Patient not found.");
        var type = await store.GetAsync<LocalProcedureType>(p.ProcedureTypeId);
        var doctor = p.DoctorId is { } d ? await store.GetAsync<LocalDoctor>(d) : null;
        byte[]? signature = null;
        if (p.ConsentAttachmentId is { } aid && await store.GetAsync<LocalAttachment>(aid) is { } file)
        {
            try { signature = await attachments.ReadAsync(file, transport); } catch (Exception) { /* not downloaded yet: print the line only */ }
        }

        var sections = new List<LetterSection> { new("Procedure", [p.Name + (p.Fee is { } fee ? $" · fee Rs {fee:N0}" : "")]) };
        if (type?.ConsentTextEn is { Length: > 0 } en) sections.Add(new("Consent", [en]));
        if (type?.ConsentTextUr is { Length: > 0 } ur) sections.Add(new("رضامندی", [ur], Urdu: true));
        if (p.ConsentSignedAt is { } at)
            sections.Add(new("Signed", [$"{p.ConsentSignerName} ({p.ConsentRelation}) on {at.ToLocalTime():dd MMM yyyy HH:mm}" + (p.ConsentWitness is null ? "" : $", witness {p.ConsentWitness}")]));

        return new ClinicLetter("Consent form", clinicName, branchLine, doctor?.FullName ?? "", doctor is null ? "" : doctor.Specialty,
            patient.FullName, $"MRN {patient.Mrn}", p.ConsentSignedAt?.ToLocalTime() ?? DateTime.Now, sections,
            SignatureLabel: p.ConsentSignerName ?? "Patient / guardian", SignaturePng: signature, SignatureCaption: p.ConsentSignedAt is null ? "Not signed yet" : "Signed on the device");
    }

    private async Task<LocalProcedure> RequireActiveAsync(Guid id)
    {
        var p = await store.GetAsync<LocalProcedure>(id) ?? throw new CareException("Procedure not found.");
        if (p.Status is ProcedureStatus.Done or ProcedureStatus.Cancelled) throw new CareException("This procedure is finished.");
        return p;
    }
}

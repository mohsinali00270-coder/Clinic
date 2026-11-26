using System.Text.Json.Nodes;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Clinical;

public sealed class EncounterException(string message) : Exception(message);

/// <summary>
/// §4.3 on the device: start a consultation, write its specialty form, vitals and diagnoses, sign it, and amend it
/// with a reason afterwards (FR-ENC-01…06). Everything works offline.
/// </summary>
public sealed class EncounterService(LocalStore store, DynamicDataService forms, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private Guid UserId => store.Context.UserId;

    // ------------------------------------------------------------------ start / read

    /// <summary>Opens the consultation for an appointment (creating it the first time) and moves the queue to "with doctor".</summary>
    public async Task<LocalEncounter> StartFromAppointmentAsync(Guid appointmentId)
    {
        var existing = (await store.ListAsync<LocalEncounter>(e => e.AppointmentId == appointmentId)).FirstOrDefault();
        if (existing is not null) return existing;
        var a = await store.GetAsync<LocalAppointment>(appointmentId) ?? throw new EncounterException("Appointment not found.");
        return await StartAsync(a.PatientId, a.DoctorId, a.BranchId, a.Id);
    }

    public async Task<LocalEncounter> StartAsync(Guid patientId, Guid doctorId, Guid branchId, Guid? appointmentId = null)
    {
        var patient = await store.GetAsync<LocalPatient>(patientId) ?? throw new EncounterException("Patient not found.");
        var doctor = await store.GetAsync<LocalDoctor>(doctorId) ?? throw new EncounterException("Doctor not found.");
        // FR-DOC-07: an expired licence blocks new encounters.
        if (doctor.LicenceExpiry is { } exp && exp.Date < _clock.GetLocalNow().Date)
            throw new EncounterException($"{doctor.FullName}'s licence expired on {exp:dd MMM yyyy}. New consultations are blocked until it is renewed.");

        var version = await forms.ResolveFormAsync(SyncEntityNames.Encounter, branchId, doctor.Specialty);
        var encounter = new LocalEncounter
        {
            Id = Guid.NewGuid(), PatientId = patientId, DoctorId = doctorId, BranchId = branchId, AppointmentId = appointmentId,
            Specialty = doctor.Specialty, StartedAt = UtcNow, Status = EncounterStatus.InProgress,
            FormVersionId = version?.Id, PatientGender = patient.Gender, PatientDateOfBirth = patient.DateOfBirth,
        };
        await store.WriteAsync(b =>
        {
            b.Insert(encounter);
            // FR-APT-03: opening the consultation moves the appointment to "with doctor".
            if (appointmentId is { } aid && b.Find<LocalAppointment>(aid) is { } a &&
                AppointmentStatus.CanMove(a.Status, AppointmentStatus.WithDoctor))
            {
                var from = a.Status;
                a.Status = AppointmentStatus.WithDoctor;
                a.CalledAt ??= UtcNow;
                b.Update(a);
                b.Insert(new LocalAppointmentStatusHistory
                {
                    Id = Guid.NewGuid(), AppointmentId = a.Id, BranchId = a.BranchId, FromStatus = from,
                    ToStatus = AppointmentStatus.WithDoctor, ChangedAt = UtcNow, ChangedBy = UserId, Reason = "consultation opened",
                });
            }
        });
        return encounter;
    }

    public Task<LocalEncounter?> GetAsync(Guid id) => store.GetAsync<LocalEncounter>(id);

    public async Task<List<LocalEncounter>> ForPatientAsync(Guid patientId) =>
        (await store.ListAsync<LocalEncounter>(e => e.PatientId == patientId)).OrderByDescending(e => e.StartedAt).ToList();

    public Task<FormVersionInfo?> FormAsync(LocalEncounter e) =>
        e.FormVersionId is { } v ? forms.GetVersionAsync(v) : Task.FromResult<FormVersionInfo?>(null);

    public static EvaluationContext ContextFor(LocalEncounter e, IReadOnlyCollection<string> roles, string language = "en") => new()
    {
        Roles = roles,
        Language = language,
        IsNewRecord = !EncounterStatus.IsLocked(e.Status),
        Variables = new Dictionary<string, object?>(PatientRules.FormVariables(e.PatientGender, e.PatientDateOfBirth, DateTime.Today))
        {
            ["specialty"] = e.Specialty,
        },
    };

    // ------------------------------------------------------------------ notes (dynamic form)

    public async Task SaveNotesAsync(Guid encounterId, JsonObject values, DateTime? followUp)
    {
        var e = await RequireOpenAsync(encounterId);
        var version = await FormAsync(e);
        e.FollowUpDate = followUp?.Date;
        if (version is null)
        {
            await store.UpdateAsync(e);
            return;
        }
        await DynamicDataService.SaveRecordAsync(store, e, isNew: false, version, values, SyncEntityNames.Encounter, e.BranchId);
    }

    // ------------------------------------------------------------------ vitals

    public async Task<LocalVitals> RecordVitalsAsync(Guid patientId, Guid branchId, Guid? encounterId, JsonObject values)
    {
        var patient = await store.GetAsync<LocalPatient>(patientId) ?? throw new EncounterException("Patient not found.");
        var version = await forms.ResolveFormAsync(SyncEntityNames.Vitals, branchId)
                      ?? throw new EncounterException("No vitals form is published. Ask the administrator to publish one (e.g. from the 'Vitals (standard)' template).");
        var vitals = new LocalVitals
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, EncounterId = encounterId, RecordedAt = UtcNow,
            RecordedBy = UserId, PatientGender = patient.Gender, PatientDateOfBirth = patient.DateOfBirth,
        };
        await DynamicDataService.SaveRecordAsync(store, vitals, isNew: true, version, values, SyncEntityNames.Vitals, branchId);
        return vitals;
    }

    public async Task<(LocalVitals Record, JsonObject Values)?> LatestVitalsAsync(Guid patientId)
    {
        var latest = (await store.ListAsync<LocalVitals>(v => v.PatientId == patientId)).MaxBy(v => v.RecordedAt);
        return latest is null ? null : (latest, await forms.LoadValuesAsync(SyncEntityNames.Vitals, latest.Id));
    }

    // ------------------------------------------------------------------ diagnoses (FR-ENC-04)

    public async Task<List<LocalIcd10>> SearchIcdAsync(string query, int limit = 30)
    {
        var q = query.Trim();
        var all = await store.ListAsync<LocalIcd10>();
        if (q.Length == 0) return all.Where(c => c.IsCommon).OrderBy(c => c.Code).Take(limit).ToList();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return all
            .Where(c => c.Code.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
                        words.All(w => c.Description.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Code.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(c => c.Code)
            .Take(limit).ToList();
    }

    public async Task<List<LocalDiagnosis>> DiagnosesAsync(Guid encounterId) =>
        (await store.ListAsync<LocalDiagnosis>(d => d.EncounterId == encounterId)).OrderByDescending(d => d.IsPrimary).ThenBy(d => d.CreatedAt).ToList();

    /// <summary>Adds a coded or free-text diagnosis. On a signed encounter an amendment reason is required.</summary>
    public async Task<LocalDiagnosis> AddDiagnosisAsync(Guid encounterId, string? code, string description, bool primary, string? amendReason = null)
    {
        var e = await RequireEncounterAsync(encounterId);
        if (string.IsNullOrWhiteSpace(description)) throw new EncounterException("Describe the diagnosis.");
        var existing = await DiagnosesAsync(encounterId);
        primary = primary || existing.Count == 0;

        var d = new LocalDiagnosis
        {
            Id = Guid.NewGuid(), EncounterId = encounterId, PatientId = e.PatientId, BranchId = e.BranchId,
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant(), Description = description.Trim(), IsPrimary = primary,
        };
        await WriteClinicalAsync(e, EncounterAmendKeys.Diagnoses, null, AmendmentText.Diagnosis(d.Code, d.Description), amendReason, b =>
        {
            if (primary)
                foreach (var other in existing.Where(x => x.IsPrimary)) { other.IsPrimary = false; b.Update(other); }
            b.Insert(d);
        });
        return d;
    }

    public async Task RemoveDiagnosisAsync(Guid diagnosisId, string? amendReason = null)
    {
        var d = await store.GetAsync<LocalDiagnosis>(diagnosisId) ?? throw new EncounterException("Diagnosis not found.");
        var e = await RequireEncounterAsync(d.EncounterId);
        await WriteClinicalAsync(e, EncounterAmendKeys.Diagnoses, AmendmentText.Diagnosis(d.Code, d.Description), null, amendReason, b => b.Delete<LocalDiagnosis>(d.Id));
    }

    // ------------------------------------------------------------------ signing and amendments (FR-ENC-05)

    public async Task<LocalEncounter> SignAsync(Guid encounterId)
    {
        var e = await RequireOpenAsync(encounterId);
        var doctor = await store.GetAsync<LocalDoctor>(e.DoctorId);
        if (doctor?.LicenceExpiry is { } exp && exp.Date < _clock.GetLocalNow().Date)
            throw new EncounterException("The doctor's licence has expired; the encounter cannot be signed.");
        e.Status = EncounterStatus.Signed;
        e.SignedAt = UtcNow;
        e.SignedBy = UserId;
        await store.UpdateAsync(e);
        return e;
    }

    /// <summary>
    /// Changes dynamic-form fields of a signed encounter. Each changed field gets an amendment row (old/new value,
    /// reason) written before the change, so the server can verify it; the encounter becomes "amended".
    /// </summary>
    public async Task<int> AmendNotesAsync(Guid encounterId, JsonObject newValues, string reason)
    {
        var e = await RequireEncounterAsync(encounterId);
        if (!EncounterStatus.IsLocked(e.Status)) throw new EncounterException("Only signed encounters are amended; save the draft instead.");
        RequireSigner(e, reason);
        var version = await FormAsync(e) ?? throw new EncounterException("This encounter has no form.");
        var current = await forms.LoadValuesAsync(SyncEntityNames.Encounter, encounterId);

        var changed = version.Definition.AllFields
            .Where(f => f.HoldsValue && f.Type != FieldType.Calculated)
            .Where(f => !JsonNode.DeepEquals(Normalize(current[f.Key]), Normalize(newValues[f.Key])))
            .ToList();
        if (changed.Count == 0) return 0;

        // Calculated fields may change as a consequence; amend those too so the server accepts them.
        var evaluated = version.Evaluator.Evaluate(newValues, ContextFor(e, []));
        var calcChanged = version.Definition.AllFields
            .Where(f => f.Type == FieldType.Calculated && !JsonNode.DeepEquals(Normalize(current[f.Key]), Normalize(evaluated.Values[f.Key])));

        await store.WriteAsync(b =>
        {
            foreach (var f in changed.Concat(calcChanged))
                b.Insert(Amendment(e, f.Key, current[f.Key]?.ToJsonString(), (evaluated.Values[f.Key] ?? newValues[f.Key])?.ToJsonString(), reason));
            e.Status = EncounterStatus.Amended;
            b.Update(e);
            e.DynamicData = DynamicDataService.StageValues(b, SyncEntityNames.Encounter, e.Id, e.BranchId, version, evaluated.Values);
            b.Update(e);
        });
        return changed.Count;
    }

    public async Task<List<LocalAmendment>> AmendmentsAsync(Guid encounterId) =>
        (await store.ListAsync<LocalAmendment>(a => a.EncounterId == encounterId)).OrderBy(a => a.AmendedAt).ToList();

    // ------------------------------------------------------------------ helpers shared with prescriptions

    /// <summary>
    /// Runs a clinical write. Open encounter: just the write. Signed encounter: the signing doctor must give a
    /// reason; an amendment row is written first and the encounter becomes "amended".
    /// </summary>
    internal async Task WriteClinicalAsync(LocalEncounter e, string key, string? oldText, string? newText, string? amendReason, Action<LocalWriteBatch> write)
    {
        if (!EncounterStatus.IsLocked(e.Status))
        {
            await store.WriteAsync(write);
            return;
        }
        RequireSigner(e, amendReason);
        await store.WriteAsync(b =>
        {
            b.Insert(Amendment(e, key, oldText, newText, amendReason!));
            if (e.Status != EncounterStatus.Amended)
            {
                e.Status = EncounterStatus.Amended;
                b.Update(e);
            }
            write(b);
        });
    }

    private void RequireSigner(LocalEncounter e, string? reason)
    {
        if (e.SignedBy != UserId) throw new EncounterException("This encounter is signed. Only the signing doctor can amend it.");
        if (string.IsNullOrWhiteSpace(reason)) throw new EncounterException("This encounter is signed. Give a reason to amend it.");
    }

    private LocalAmendment Amendment(LocalEncounter e, string key, string? oldValue, string? newValue, string reason) => new()
    {
        Id = Guid.NewGuid(), EncounterId = e.Id, BranchId = e.BranchId, FieldKey = key, OldValue = oldValue, NewValue = newValue,
        Reason = reason.Trim(), AmendedAt = UtcNow, AmendedBy = UserId,
    };

    internal async Task<LocalEncounter> RequireEncounterAsync(Guid id) =>
        await store.GetAsync<LocalEncounter>(id) ?? throw new EncounterException("Encounter not found.");

    private async Task<LocalEncounter> RequireOpenAsync(Guid id)
    {
        var e = await RequireEncounterAsync(id);
        if (EncounterStatus.IsLocked(e.Status)) throw new EncounterException("This encounter is signed. Use Amend to change it.");
        return e;
    }

    private static JsonNode? Normalize(JsonNode? n) => FormValues.IsEmpty(n) ? null
        : n is JsonValue v && FormValues.AsDecimal(v) is { } d && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? JsonValue.Create(d / 1.000000000000000000000000000000000m) : n;
}

/// <summary>Amendment keys for non-form parts of an encounter (must match the server's EncounterSyncRules).</summary>
public static class EncounterAmendKeys
{
    public const string Diagnoses = "diagnoses";
    public const string Prescription = "prescription";
    public const string FollowUp = "followUpDate";
}

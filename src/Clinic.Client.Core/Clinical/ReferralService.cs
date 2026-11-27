using System.Text.Json.Nodes;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Clinical;

public sealed record ReferralInput(
    Guid PatientId,
    Guid BranchId,
    string Reason,
    string Priority = ReferralPriority.Routine,
    Guid? EncounterId = null,
    Guid? CaseId = null,
    Guid? FromDoctorId = null);

/// <summary>
/// §4.11 on the device: internal referrals reach the receiving doctor's dashboard after sync (FR-REF-01); external
/// referrals print as a letter with a clinical summary (FR-REF-02); incoming referrals record who sent the patient
/// (FR-REF-03). Everything works offline.
/// </summary>
public sealed class ReferralService(LocalStore store, DynamicDataService forms, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private Guid UserId => store.Context.UserId;

    public async Task<LocalReferral> ReferInternalAsync(ReferralInput input, Guid? toDoctorId, string? toSpecialty)
    {
        if (toDoctorId is null && string.IsNullOrWhiteSpace(toSpecialty)) throw new CareException("Choose a doctor or a specialty to refer to.");
        if (toDoctorId is { } d && d == input.FromDoctorId) throw new CareException("You cannot refer a patient to yourself.");
        var to = toDoctorId is { } id ? await store.GetAsync<LocalDoctor>(id) ?? throw new CareException("Doctor not found.") : null;
        return await CreateAsync(input, ReferralDirection.Internal, r =>
        {
            r.ToDoctorId = to?.Id;
            r.ToSpecialty = to?.Specialty ?? toSpecialty?.Trim();
        });
    }

    public async Task<LocalReferral> ReferExternalAsync(ReferralInput input, string facility, string? toDoctor, string? summary = null)
    {
        if (string.IsNullOrWhiteSpace(facility)) throw new CareException("Enter the hospital, specialist or centre.");
        summary ??= input.EncounterId is { } eid ? await SummaryFromEncounterAsync(eid) : null;
        return await CreateAsync(input, ReferralDirection.External, r =>
        {
            r.ExternalFacility = facility.Trim();
            r.ExternalDoctor = string.IsNullOrWhiteSpace(toDoctor) ? null : toDoctor.Trim();
            r.Summary = summary;
        });
    }

    /// <summary>FR-REF-03: <paramref name="sourceCode"/> from the 'referrers' lookup (reports, commissions) and/or a free-text name.</summary>
    public Task<LocalReferral> RecordIncomingAsync(ReferralInput input, string? sourceCode, string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceCode) && string.IsNullOrWhiteSpace(sourceName)) throw new CareException("Record who referred the patient.");
        return CreateAsync(input, ReferralDirection.Incoming, r =>
        {
            r.SourceCode = string.IsNullOrWhiteSpace(sourceCode) ? null : sourceCode.Trim();
            r.SourceName = string.IsNullOrWhiteSpace(sourceName) ? null : sourceName.Trim();
            r.Status = ReferralStatus.Accepted; // the patient is already here
            r.RespondedAt = UtcNow;
            r.RespondedBy = UserId;
        });
    }

    private async Task<LocalReferral> CreateAsync(ReferralInput input, string direction, Action<LocalReferral> fill)
    {
        if (string.IsNullOrWhiteSpace(input.Reason)) throw new CareException("Give the reason for the referral.");
        if (!ReferralPriority.All.Contains(input.Priority)) throw new CareException($"Unknown priority '{input.Priority}'.");
        _ = await store.GetAsync<LocalPatient>(input.PatientId) ?? throw new CareException("Patient not found.");
        var r = new LocalReferral
        {
            Id = Guid.NewGuid(), PatientId = input.PatientId, BranchId = input.BranchId, Direction = direction, EncounterId = input.EncounterId,
            CaseId = input.CaseId, FromDoctorId = input.FromDoctorId, Reason = input.Reason.Trim(), Priority = input.Priority,
            Status = ReferralStatus.Pending, ReferredAt = UtcNow, ReferredBy = UserId,
        };
        fill(r);
        return await store.InsertAsync(r);
    }

    // ------------------------------------------------------------------ responses

    public Task<LocalReferral> AcceptAsync(Guid id, string? note = null) => RespondAsync(id, ReferralStatus.Accepted, note);
    public Task<LocalReferral> DeclineAsync(Guid id, string note) =>
        string.IsNullOrWhiteSpace(note) ? throw new CareException("Say why the referral is declined.") : RespondAsync(id, ReferralStatus.Declined, note);
    public Task<LocalReferral> CompleteAsync(Guid id, string? note = null) => RespondAsync(id, ReferralStatus.Completed, note);
    public Task<LocalReferral> CancelAsync(Guid id, string? note = null) => RespondAsync(id, ReferralStatus.Cancelled, note);

    private async Task<LocalReferral> RespondAsync(Guid id, string status, string? note)
    {
        var r = await store.GetAsync<LocalReferral>(id) ?? throw new CareException("Referral not found.");
        if (!ReferralStatus.IsOpen(r.Status)) throw new CareException("This referral is already finished.");
        if (status == ReferralStatus.Accepted && r.Status == ReferralStatus.Accepted) return r;
        r.Status = status;
        r.RespondedAt = UtcNow;
        r.RespondedBy = UserId;
        if (!string.IsNullOrWhiteSpace(note)) r.ResponseNote = note.Trim();
        await store.UpdateAsync(r);
        return r;
    }

    // ------------------------------------------------------------------ lists

    /// <summary>FR-REF-01 / FR-DOC-06: open referrals to this doctor, or to their specialty with no doctor named.</summary>
    public async Task<List<LocalReferral>> InboxAsync(Guid doctorId)
    {
        var doctor = await store.GetAsync<LocalDoctor>(doctorId);
        return (await store.ListAsync<LocalReferral>(r => r.Direction == ReferralDirection.Internal))
            .Where(r => ReferralStatus.IsOpen(r.Status) && r.FromDoctorId != doctorId &&
                        (r.ToDoctorId == doctorId ||
                         (r.ToDoctorId is null && doctor is not null && string.Equals(r.ToSpecialty, doctor.Specialty, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(r => r.Priority switch { ReferralPriority.Emergency => 0, ReferralPriority.Urgent => 1, _ => 2 })
            .ThenBy(r => r.ReferredAt).ToList();
    }

    public async Task<List<LocalReferral>> ForPatientAsync(Guid patientId) =>
        (await store.ListAsync<LocalReferral>(r => r.PatientId == patientId)).OrderByDescending(r => r.ReferredAt).ToList();

    public static string Describe(LocalReferral r, IReadOnlyDictionary<Guid, string> doctors) => r.Direction switch
    {
        ReferralDirection.Internal => "To " + (r.ToDoctorId is { } d ? doctors.GetValueOrDefault(d, "doctor") : $"{r.ToSpecialty} (any doctor)"),
        ReferralDirection.External => "To " + r.ExternalFacility + (r.ExternalDoctor is null ? "" : $" ({r.ExternalDoctor})"),
        _ => "From " + (r.SourceName ?? r.SourceCode),
    };

    // ------------------------------------------------------------------ letter (FR-REF-02)

    /// <summary>A clinical summary from the encounter: complaint, vitals, diagnoses, current medicines, allergies.</summary>
    public async Task<string> SummaryFromEncounterAsync(Guid encounterId)
    {
        var e = await store.GetAsync<LocalEncounter>(encounterId) ?? throw new CareException("Encounter not found.");
        var lines = new List<string>();

        if (e.FormVersionId is { } fv && await forms.GetVersionAsync(fv) is { } version)
        {
            var values = await forms.LoadValuesAsync(SyncEntityNames.Encounter, e.Id);
            foreach (var key in new[] { "chief_complaint", "history", "examination", "findings", "plan" })
                if (version.Definition.FindField(key) is { } f && FormValues.AsString(values[key]) is { Length: > 0 } text)
                    lines.Add($"{f.Label.En}: {text}");
        }
        var vitals = (await store.ListAsync<LocalVitals>(v => v.EncounterId == e.Id)).MaxBy(v => v.RecordedAt);
        if (vitals is not null && vitals.FormVersionId is { } vv && await forms.GetVersionAsync(vv) is { } vVersion)
        {
            var vValues = await forms.LoadValuesAsync(SyncEntityNames.Vitals, vitals.Id);
            var parts = vVersion.Definition.AllFields
                .Where(f => !FormValues.IsEmpty(vValues[f.Key]) && (f.IsNumeric || f.Type == FieldType.Calculated))
                .Select(f => $"{f.Label.En} {FormValues.AsDecimal(vValues[f.Key]):0.##}{(f.Unit is null ? "" : " " + f.Unit)}");
            var joined = string.Join(", ", parts);
            if (joined.Length > 0) lines.Add("Vitals: " + joined);
        }
        var dx = await store.ListAsync<LocalDiagnosis>(d => d.EncounterId == e.Id);
        if (dx.Count > 0) lines.Add("Diagnosis: " + string.Join("; ", dx.OrderByDescending(d => d.IsPrimary).Select(d => AmendmentText.Diagnosis(d.Code, d.Description))));
        var rx = (await store.ListAsync<LocalPrescription>(p => p.EncounterId == e.Id)).FirstOrDefault();
        if (rx is not null)
        {
            var items = await store.ListAsync<LocalPrescriptionItem>(i => i.PrescriptionId == rx.Id);
            if (items.Count > 0)
                lines.Add("Medicines: " + string.Join("; ", items.OrderBy(i => i.SortOrder).Select(i => AmendmentText.PrescriptionItem(i.DrugName, i.Strength, i.Dose, i.Frequency, i.DurationDays))));
        }
        if (await store.GetAsync<LocalPatient>(e.PatientId) is { DynamicData: { Length: > 0 } dd } && JsonNode.Parse(dd) is JsonObject pv)
        {
            var allergies = FormValues.AsStringList(pv["allergies"]).Where(a => a != "none").ToList();
            if (allergies.Count > 0) lines.Add("Allergies: " + string.Join(", ", allergies));
        }
        return string.Join("\n", lines);
    }

    public async Task<ClinicLetter> BuildLetterAsync(Guid referralId, string clinicName, string branchLine)
    {
        var r = await store.GetAsync<LocalReferral>(referralId) ?? throw new CareException("Referral not found.");
        var patient = await store.GetAsync<LocalPatient>(r.PatientId) ?? throw new CareException("Patient not found.");
        var from = r.FromDoctorId is { } d ? await store.GetAsync<LocalDoctor>(d) : null;
        var to = r.ToDoctorId is { } t ? await store.GetAsync<LocalDoctor>(t) : null;
        var addressee = r.Direction == ReferralDirection.External
            ? string.Join(", ", new[] { r.ExternalDoctor, r.ExternalFacility }.Where(s => !string.IsNullOrWhiteSpace(s)))
            : to?.FullName ?? $"{r.ToSpecialty} team";

        var sections = new List<LetterSection>
        {
            new("", [$"Dear {addressee},", $"Thank you for seeing {patient.FullName}, whom I am referring for: {r.Reason}"]),
        };
        if (!string.IsNullOrWhiteSpace(r.Summary)) sections.Add(new("Clinical summary", r.Summary!.Split('\n')));
        sections.Add(new("", [$"Priority: {r.Priority}.", "Please do not hesitate to contact us for further information."]));

        return new ClinicLetter(
            Title: r.Direction == ReferralDirection.External ? "Referral letter" : "Internal referral",
            ClinicName: clinicName, BranchLine: branchLine,
            DoctorName: from?.FullName ?? "", DoctorDetails: from is null ? "" : $"{from.Specialty} · Reg. {from.RegistrationNo}",
            PatientName: patient.FullName, PatientDetails: $"MRN {patient.Mrn}" + (patient.Phone is null ? "" : $" · {patient.Phone}"),
            Date: r.ReferredAt.ToLocalTime(), Sections: sections, SignatureLabel: from?.FullName);
    }
}

/// <summary>FR-REF-04: a patient due or overdue for follow-up.</summary>
public sealed record FollowUpItem(
    Guid PatientId,
    string PatientName,
    string? Phone,
    DateTime DueDate,
    string Source,
    string Reason,
    Guid? DoctorId,
    string? DoctorName,
    Guid? CaseId,
    string? CaseTitle,
    Guid RecordId)
{
    public int DaysOverdue(DateTime today) => Math.Max(0, (today.Date - DueDate.Date).Days);
}

/// <summary>
/// FR-REF-04 follow-up list from local data: encounter follow-up dates and planned care-plan steps. A follow-up is
/// settled when the patient has had a visit since, or has an appointment booked from the due date onwards.
/// </summary>
public sealed class FollowUpService(LocalStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<List<FollowUpItem>> DueAsync(DateTime until, Guid? doctorId = null, Guid? caseId = null, Guid? branchId = null)
    {
        var today = _clock.GetLocalNow().Date;
        var patients = (await store.ListAsync<LocalPatient>()).ToDictionary(p => p.Id);
        var doctors = (await store.ListAsync<LocalDoctor>()).ToDictionary(d => d.Id, d => d.FullName);
        var cases = (await store.ListAsync<LocalCase>()).ToDictionary(c => c.Id);
        var encounters = await store.ListAsync<LocalEncounter>();
        var appointments = (await store.ListAsync<LocalAppointment>())
            .Where(a => a.Status is not (Clinic.Shared.Scheduling.AppointmentStatus.Cancelled or Clinic.Shared.Scheduling.AppointmentStatus.NoShow
                or Clinic.Shared.Scheduling.AppointmentStatus.Rescheduled))
            .ToLookup(a => a.PatientId);
        var visits = encounters.ToLookup(e => e.PatientId);

        bool Settled(Guid patientId, DateTime since, DateTime due) =>
            visits[patientId].Any(e => e.StartedAt > since.AddHours(1)) ||
            appointments[patientId].Any(a => a.Date.Date >= due.Date.AddDays(-3) && a.CreatedAt > since);

        var items = new List<FollowUpItem>();

        // 1. Follow-up dates set at the end of a consultation (FR-ENC-06), latest visit per patient only.
        foreach (var e in encounters.Where(e => e.FollowUpDate is not null).GroupBy(e => e.PatientId).Select(g => g.MaxBy(e => e.StartedAt)!))
        {
            if (e.FollowUpDate is not { } due || due.Date > until.Date || !patients.TryGetValue(e.PatientId, out var p)) continue;
            if (Settled(e.PatientId, e.StartedAt, due)) continue;
            var c = e.CaseId is { } cid ? cases.GetValueOrDefault(cid) : null;
            items.Add(new FollowUpItem(p.Id, p.FullName, p.Phone, due.Date, "visit", "Follow-up visit", e.DoctorId,
                doctors.GetValueOrDefault(e.DoctorId), c?.Id, c?.Title, e.Id));
        }

        // 2. Planned care-plan steps of open cases (FR-CASE-05 reminders).
        foreach (var s in await store.ListAsync<LocalCarePlanStep>(s => s.Status == CarePlanStepStatus.Planned))
        {
            if (s.DueDate is not { } due || due.Date > until.Date || !cases.TryGetValue(s.CaseId, out var c) || CaseStatus.IsFinished(c.Status)) continue;
            if (c.Status == CaseStatus.OnHold || !patients.TryGetValue(c.PatientId, out var p)) continue;
            if (s.Kind == CarePlanStepKind.Visit && Settled(c.PatientId, DateTime.SpecifyKind(due.Date.AddDays(-7), DateTimeKind.Local).ToUniversalTime(), due)) continue;
            items.Add(new FollowUpItem(p.Id, p.FullName, p.Phone, due.Date, "care_plan", s.Title, c.PrimaryDoctorId,
                c.PrimaryDoctorId is { } d ? doctors.GetValueOrDefault(d) : null, c.Id, c.Title, s.Id));
        }

        return items
            .Where(i => doctorId is null || i.DoctorId == doctorId)
            .Where(i => caseId is null || i.CaseId == caseId)
            .Where(i => branchId is null || patients[i.PatientId].BranchId == branchId)
            .OrderBy(i => i.DueDate).ThenBy(i => i.PatientName).ToList();
    }

    /// <summary>A polite reminder text for SMS / WhatsApp (English, with the Urdu line underneath).</summary>
    public static string ReminderMessage(FollowUpItem item, string clinicName) =>
        $"Dear {item.PatientName}, this is a reminder from {clinicName}: your {item.Reason.ToLowerInvariant()} was due on {item.DueDate:dd MMM yyyy}. " +
        "Please call us to book a time.\n" +
        $"محترم {item.PatientName}، {clinicName} کی جانب سے یاد دہانی: آپ کا فالو اَپ {item.DueDate:dd MMM yyyy} کو تھا۔ براہِ کرم وقت لینے کے لیے رابطہ کریں۔";
}

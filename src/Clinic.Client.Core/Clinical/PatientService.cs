using System.Text.Json.Nodes;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Client.Core.Sync;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Clinical;

/// <summary>Core fields typed on the registration screen (FR-PAT-01).</summary>
public sealed record PatientInput(string FullName, string Gender, DateTime? DateOfBirth, bool DobEstimated, string? Phone, Guid BranchId);

/// <summary>One timeline entry; At is always UTC.</summary>
public sealed record TimelineItem(DateTime At, string Kind, string Title, string? Detail, Guid? RecordId);

/// <summary>
/// FR-PAT-01…05 on the device: register (offline MRN), edit, search, duplicate check and timeline.
/// Everything reads and writes the local database only.
/// </summary>
public sealed class PatientService(LocalDatabase db, LocalStore store, string deviceCode, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>FR-PAT-02: {BranchCode}-{DeviceCode}-{Sequence} from this device's leased MRN range.</summary>
    public async Task<string> NextMrnAsync(Guid branchId)
    {
        var branch = await db.Connection.FindAsync<LocalBranch>(branchId)
                     ?? throw new InvalidOperationException("The branch is not on this device yet. Sync once, then try again.");
        var seq = await NumberSource.NextAsync(db, NumberRangeKinds.Mrn);
        return PatientRules.Mrn(branch.Code, deviceCode, seq);
    }

    public async Task<LocalPatient> RegisterAsync(PatientInput input, FormVersionInfo? form, JsonObject? dynamicValues)
    {
        Validate(input);
        var patient = new LocalPatient
        {
            Id = Guid.NewGuid(),
            Mrn = await NextMrnAsync(input.BranchId),
            FullName = input.FullName.Trim(),
            Gender = input.Gender,
            DateOfBirth = input.DateOfBirth?.Date,
            DobEstimated = input.DobEstimated,
            Phone = string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone.Trim(),
            BranchId = input.BranchId,
        };
        await SaveAsync(patient, isNew: true, form, dynamicValues);
        return patient;
    }

    public async Task<LocalPatient> UpdateAsync(Guid patientId, PatientInput input, FormVersionInfo? form, JsonObject? dynamicValues)
    {
        Validate(input);
        var patient = await store.GetAsync<LocalPatient>(patientId) ?? throw new InvalidOperationException("Patient not found on this device.");
        patient.FullName = input.FullName.Trim();
        patient.Gender = input.Gender;
        patient.DateOfBirth = input.DateOfBirth?.Date;
        patient.DobEstimated = input.DobEstimated;
        patient.Phone = string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone.Trim();
        await SaveAsync(patient, isNew: false, form, dynamicValues);
        return patient;
    }

    private async Task SaveAsync(LocalPatient patient, bool isNew, FormVersionInfo? form, JsonObject? values)
    {
        await store.WriteAsync(b =>
        {
            if (form is not null && values is not null)
            {
                patient.FormVersionId = form.Id;
                if (isNew) b.Insert(patient); else b.Update(patient);
                patient.DynamicData = DynamicDataService.StageValues(b, SyncEntityNames.Patient, patient.Id, patient.BranchId, form, values);
                b.Update(patient);
            }
            else if (isNew) b.Insert(patient);
            else b.Update(patient);

            PatientSearchIndex.Reindex(b.Connection, [patient.Id]);
        });
    }

    private static void Validate(PatientInput input)
    {
        if (string.IsNullOrWhiteSpace(input.FullName)) throw new ArgumentException("Patient name is required.");
        if (!PatientRules.Genders.Contains(input.Gender)) throw new ArgumentException("Choose a gender.");
        if (input.DateOfBirth?.Date > DateTime.Today) throw new ArgumentException("Date of birth cannot be in the future.");
        if (input.Phone is { Length: > 0 } p && PatientRules.NormalizePhone(p).Length is < 7 or > 15)
            throw new ArgumentException("Phone number is not valid.");
    }

    /// <summary>Estimated date of birth from an age (FR-PAT-01 "date of birth or age").</summary>
    public static DateTime DobFromAge(int years, DateTime today) => new(today.Year - years, 7, 1);

    public async Task<List<LocalPatient>> SearchAsync(string query, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (await db.Connection.Table<LocalPatient>().Where(p => !p.IsDeleted).ToListAsync())
                .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt).Take(limit).ToList();
        }

        var ids = await PatientSearchIndex.SearchAsync(db, query, limit);
        var result = new List<LocalPatient>();
        foreach (var id in ids)
            if (await store.GetAsync<LocalPatient>(id) is { } p) result.Add(p);
        return result;
    }

    /// <summary>FR-PAT-04: likely duplicates already on this device (same phone, similar name, compatible DOB).</summary>
    public async Task<List<LocalPatient>> FindDuplicatesAsync(string name, string? phone, DateTime? dob, Guid? excludeId = null)
    {
        var digits = PatientRules.NormalizePhone(phone);
        if (digits.Length < 7) return [];
        var candidates = await SearchAsync(digits.TrimStart('0'), 200);
        return candidates
            .Where(p => p.Id != excludeId && PatientRules.LooksLikeDuplicate(name, phone, dob, p.FullName, p.Phone, p.DateOfBirth))
            .ToList();
    }

    /// <summary>FR-PAT-05: registration, appointments and files in date order (encounters, labs and bills join later phases).</summary>
    public async Task<List<TimelineItem>> TimelineAsync(Guid patientId)
    {
        var items = new List<TimelineItem>();
        if (await store.GetAsync<LocalPatient>(patientId) is { } p)
            items.Add(new TimelineItem(p.CreatedAt, "registered", $"Registered · MRN {p.Mrn}", null, p.Id));

        var doctors = (await store.ListAsync<LocalDoctor>()).ToDictionary(d => d.Id, d => d.FullName);
        foreach (var a in await store.ListAsync<LocalAppointment>(a => a.PatientId == patientId))
        {
            // Timeline times are UTC. Appointment dates are clinic-local, so convert them; walk-ins use their check-in time.
            var at = a.StartTime is { } start
                ? DateTime.SpecifyKind(a.Date.Date + start, DateTimeKind.Local).ToUniversalTime()
                : a.CheckedInAt ?? DateTime.SpecifyKind(a.Date.Date, DateTimeKind.Local).ToUniversalTime();
            items.Add(new TimelineItem(at, "appointment",
                $"{AppointmentStatus.Display(a.Status)} · {doctors.GetValueOrDefault(a.DoctorId, "Doctor")}",
                a.TokenNo is { } t ? $"Token {t}" : a.CancelReason, a.Id));
        }

        foreach (var e in await store.ListAsync<LocalEncounter>(x => x.PatientId == patientId))
            items.Add(new TimelineItem(e.StartedAt, "encounter",
                $"Consultation · {Clinic.Shared.Clinical.EncounterStatus.Display(e.Status)} · {doctors.GetValueOrDefault(e.DoctorId, "Doctor")}", e.Specialty, e.Id));

        foreach (var f in await store.ListAsync<LocalAttachment>(x => x.RecordId == patientId))
            items.Add(new TimelineItem(f.CreatedAt, "file", f.Caption ?? f.FileName, f.Category, f.Id));

        return items.OrderByDescending(i => i.At).ToList();
    }

    public DateTime Today => _clock.GetLocalNow().Date;
}

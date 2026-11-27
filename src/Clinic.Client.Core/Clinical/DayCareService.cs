using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;

namespace Clinic.Client.Core.Clinical;

/// <summary>A bed on the board with the patient in it, if any (and a second one when two devices clashed offline).</summary>
public sealed record BedOccupancy(LocalBed Bed, LocalAdmission? Admission, string? PatientName, bool Conflict);

public sealed record MedicationChartRow(LocalMedicationOrder Order, IReadOnlyList<LocalMedicationDose> Doses)
{
    public LocalMedicationDose? LastDose => Doses.Count > 0 ? Doses[^1] : null;
}

public sealed record RunningCharges(IReadOnlyList<RoomChargeLine> Lines, decimal Total);

/// <summary>
/// §4.12 day care / short stay on the device (FR-PRC-03/04): bed board, admit, transfer, nursing notes,
/// medication chart, room charges as they accrue, and discharge with a printable summary. Works offline.
/// </summary>
public sealed class DayCareService(LocalStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private Guid UserId => store.Context.UserId;

    public async Task<List<LocalBed>> BedsAsync(Guid branchId) =>
        (await store.ListAsync<LocalBed>(b => b.BranchId == branchId && b.IsActive)).OrderBy(b => b.Ward).ThenBy(b => b.Code).ToList();

    public async Task<List<BedOccupancy>> BoardAsync(Guid branchId)
    {
        var active = (await store.ListAsync<LocalAdmission>(a => a.BranchId == branchId && a.Status == AdmissionStatus.Admitted)).ToLookup(a => a.BedId);
        var patients = (await store.ListAsync<LocalPatient>()).ToDictionary(p => p.Id, p => p.FullName);
        return (await BedsAsync(branchId)).Select(b =>
        {
            var here = active[b.Id].OrderBy(a => a.AdmittedAt).ToList();
            var first = here.FirstOrDefault();
            return new BedOccupancy(b, first, first is null ? null : patients.GetValueOrDefault(first.PatientId), here.Count > 1 || here.Any(a => a.BedConflict));
        }).ToList();
    }

    public async Task<LocalAdmission> AdmitAsync(Guid patientId, Guid branchId, Guid bedId, string? reason, Guid? doctorId = null, Guid? caseId = null)
    {
        _ = await store.GetAsync<LocalPatient>(patientId) ?? throw new CareException("Patient not found.");
        var bed = await store.GetAsync<LocalBed>(bedId) ?? throw new CareException("Bed not found.");
        if (bed.BranchId != branchId || !bed.IsActive) throw new CareException($"Bed {bed.Code} is not available here.");
        if ((await store.ListAsync<LocalAdmission>(a => a.BedId == bedId && a.Status == AdmissionStatus.Admitted)).Count > 0)
            throw new CareException($"Bed {bed.Code} is occupied.");
        if ((await store.ListAsync<LocalAdmission>(a => a.PatientId == patientId && a.Status == AdmissionStatus.Admitted)).Count > 0)
            throw new CareException("This patient is already admitted.");
        if (caseId is { } cid && await store.GetAsync<LocalCase>(cid) is { } c && (c.PatientId != patientId || CaseStatus.IsFinished(c.Status)))
            throw new CareException("That case is closed or belongs to another patient.");

        return await store.InsertAsync(new LocalAdmission
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, BedId = bedId, CaseId = caseId, DoctorId = doctorId,
            AdmittedAt = UtcNow, AdmittedBy = UserId, Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), Status = AdmissionStatus.Admitted,
        });
    }

    public Task<LocalAdmission?> GetAsync(Guid id) => store.GetAsync<LocalAdmission>(id);

    public async Task<LocalAdmission?> ActiveForPatientAsync(Guid patientId) =>
        (await store.ListAsync<LocalAdmission>(a => a.PatientId == patientId && a.Status == AdmissionStatus.Admitted)).FirstOrDefault();

    public async Task<List<LocalAdmission>> ForPatientAsync(Guid patientId) =>
        (await store.ListAsync<LocalAdmission>(a => a.PatientId == patientId)).OrderByDescending(a => a.AdmittedAt).ToList();

    /// <summary>The transfer row is written before the admission update, so the server sees the bed history first.</summary>
    public async Task<LocalBedTransfer> TransferAsync(Guid admissionId, Guid toBedId, string? reason = null)
    {
        var a = await RequireAdmittedAsync(admissionId);
        if (a.BedId == toBedId) throw new CareException("The patient is already in that bed.");
        var bed = await store.GetAsync<LocalBed>(toBedId) ?? throw new CareException("Bed not found.");
        if (bed.BranchId != a.BranchId || !bed.IsActive) throw new CareException($"Bed {bed.Code} is not available here.");
        if ((await store.ListAsync<LocalAdmission>(x => x.BedId == toBedId && x.Status == AdmissionStatus.Admitted)).Count > 0)
            throw new CareException($"Bed {bed.Code} is occupied.");

        var t = new LocalBedTransfer
        {
            Id = Guid.NewGuid(), AdmissionId = a.Id, BranchId = a.BranchId, FromBedId = a.BedId, ToBedId = toBedId, MovedAt = UtcNow, MovedBy = UserId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        };
        await store.WriteAsync(b =>
        {
            b.Insert(t);
            a.BedId = toBedId;
            b.Update(a);
        });
        return t;
    }

    public async Task<List<LocalBedTransfer>> TransfersAsync(Guid admissionId) =>
        (await store.ListAsync<LocalBedTransfer>(t => t.AdmissionId == admissionId)).OrderBy(t => t.MovedAt).ToList();

    // ------------------------------------------------------------------ nursing notes and medication chart

    public async Task<LocalNursingNote> AddNursingNoteAsync(Guid admissionId, string body, string kind = NursingNoteKind.Note, string? authorName = null)
    {
        var a = await RequireAdmittedAsync(admissionId);
        if (!NursingNoteKind.All.Contains(kind)) throw new CareException($"Unknown note kind '{kind}'.");
        if (string.IsNullOrWhiteSpace(body)) throw new CareException("The note is empty.");
        return await store.InsertAsync(new LocalNursingNote
        {
            Id = Guid.NewGuid(), AdmissionId = a.Id, BranchId = a.BranchId, Kind = kind, Body = body.Trim(), WrittenAt = UtcNow, WrittenBy = UserId, AuthorName = authorName,
        });
    }

    public async Task<List<LocalNursingNote>> NursingNotesAsync(Guid admissionId) =>
        (await store.ListAsync<LocalNursingNote>(n => n.AdmissionId == admissionId)).OrderByDescending(n => n.WrittenAt).ToList();

    public async Task<LocalMedicationOrder> OrderMedicationAsync(Guid admissionId, string drugName, string? dose, string? route, string? frequency,
        string? instructions = null, Guid? drugId = null)
    {
        var a = await RequireAdmittedAsync(admissionId);
        if (string.IsNullOrWhiteSpace(drugName)) throw new CareException("Choose a medicine.");
        if (frequency is not null && Frequencies.Find(frequency) is null) throw new CareException($"Unknown frequency '{frequency}'.");
        return await store.InsertAsync(new LocalMedicationOrder
        {
            Id = Guid.NewGuid(), AdmissionId = a.Id, BranchId = a.BranchId, DrugId = drugId, DrugName = drugName.Trim(), Dose = dose, Route = route,
            Frequency = frequency, StartAt = UtcNow, Status = MedicationOrderStatus.Active, Instructions = instructions, OrderedBy = UserId,
        });
    }

    public async Task StopMedicationAsync(Guid orderId)
    {
        var o = await store.GetAsync<LocalMedicationOrder>(orderId) ?? throw new CareException("Order not found.");
        if (o.Status == MedicationOrderStatus.Stopped) return;
        o.Status = MedicationOrderStatus.Stopped;
        o.StopAt = UtcNow;
        await store.UpdateAsync(o);
    }

    public async Task<LocalMedicationDose> RecordDoseAsync(Guid orderId, string status = MedicationDoseStatus.Given, string? note = null)
    {
        var o = await store.GetAsync<LocalMedicationOrder>(orderId) ?? throw new CareException("Order not found.");
        if (o.Status != MedicationOrderStatus.Active) throw new CareException($"{o.DrugName} has been stopped.");
        await RequireAdmittedAsync(o.AdmissionId);
        if (!MedicationDoseStatus.All.Contains(status)) throw new CareException($"Unknown dose status '{status}'.");
        if (status != MedicationDoseStatus.Given && string.IsNullOrWhiteSpace(note)) throw new CareException("Say why the dose was not given.");
        return await store.InsertAsync(new LocalMedicationDose
        {
            Id = Guid.NewGuid(), OrderId = o.Id, AdmissionId = o.AdmissionId, BranchId = o.BranchId, GivenAt = UtcNow, Status = status, Note = note, GivenBy = UserId,
        });
    }

    public async Task<List<MedicationChartRow>> ChartAsync(Guid admissionId)
    {
        var doses = (await store.ListAsync<LocalMedicationDose>(d => d.AdmissionId == admissionId)).ToLookup(d => d.OrderId);
        return (await store.ListAsync<LocalMedicationOrder>(o => o.AdmissionId == admissionId))
            .OrderBy(o => o.Status == MedicationOrderStatus.Active ? 0 : 1).ThenBy(o => o.StartAt)
            .Select(o => new MedicationChartRow(o, doses[o.Id].OrderBy(d => d.GivenAt).ToList())).ToList();
    }

    // ------------------------------------------------------------------ charges (FR-PRC-04) and discharge

    public async Task<RunningCharges> ChargesAsync(Guid admissionId)
    {
        var a = await store.GetAsync<LocalAdmission>(admissionId) ?? throw new CareException("Admission not found.");
        var transfers = await TransfersAsync(admissionId);
        var beds = (await store.ListAsync<LocalBed>()).ToDictionary(b => b.Id);
        var end = a.Status == AdmissionStatus.Admitted ? (DateTime?)null : a.DischargedAt ?? a.UpdatedAt;
        var stays = RoomCharges.Stays(a.AdmittedAt, a.BedId, end, transfers.Select(t => (t.FromBedId, t.ToBedId, t.MovedAt)));
        var lines = RoomCharges.Calculate(stays, id => beds.TryGetValue(id, out var b) ? new BedRate(b.ChargeAmount, b.ChargeUnit) : null, UtcNow);
        return new RunningCharges(lines, lines.Sum(l => l.Amount));
    }

    public async Task<LocalAdmission> DischargeAsync(Guid admissionId, string summary, string? advice = null)
    {
        var a = await RequireAdmittedAsync(admissionId);
        if (string.IsNullOrWhiteSpace(summary)) throw new CareException("Write the discharge summary.");
        var active = (await store.ListAsync<LocalMedicationOrder>(o => o.AdmissionId == admissionId && o.Status == MedicationOrderStatus.Active));
        await store.WriteAsync(b =>
        {
            foreach (var o in active)
            {
                o.Status = MedicationOrderStatus.Stopped;
                o.StopAt = UtcNow;
                b.Update(o);
            }
            a.Status = AdmissionStatus.Discharged;
            a.DischargedAt = UtcNow;
            a.DischargedBy = UserId;
            a.DischargeSummary = summary.Trim();
            a.DischargeAdvice = string.IsNullOrWhiteSpace(advice) ? null : advice.Trim();
            b.Update(a);
        });
        return a;
    }

    public async Task<ClinicLetter> BuildDischargeSummaryAsync(Guid admissionId, string clinicName, string branchLine)
    {
        var a = await store.GetAsync<LocalAdmission>(admissionId) ?? throw new CareException("Admission not found.");
        var patient = await store.GetAsync<LocalPatient>(a.PatientId) ?? throw new CareException("Patient not found.");
        var doctor = a.DoctorId is { } d ? await store.GetAsync<LocalDoctor>(d) : null;
        var beds = (await store.ListAsync<LocalBed>()).ToDictionary(b => b.Id, b => b.Code);
        var charges = await ChargesAsync(admissionId);
        var chart = await ChartAsync(admissionId);
        var procedures = await store.ListAsync<LocalProcedure>(p => p.AdmissionId == admissionId && p.Status == ProcedureStatus.Done);

        var stay = $"Admitted {a.AdmittedAt.ToLocalTime():dd MMM yyyy HH:mm}" +
                   (a.DischargedAt is { } out_ ? $", discharged {out_.ToLocalTime():dd MMM yyyy HH:mm}" : " (still admitted)") +
                   $" · bed {beds.GetValueOrDefault(a.BedId, "?")}";
        var sections = new List<LetterSection> { new("Stay", [stay, a.Reason is null ? "" : $"Reason: {a.Reason}"]) };
        if (procedures.Count > 0) sections.Add(new("Procedures", procedures.Select(p => $"{p.Name} ({p.CompletedAt?.ToLocalTime():dd MMM HH:mm})").ToList()));
        if (chart.Count > 0)
            sections.Add(new("Medicines given", chart.Select(r =>
                $"{r.Order.DrugName} {r.Order.Dose} {Frequencies.Find(r.Order.Frequency)?.English ?? r.Order.Frequency} — {r.Doses.Count(x => x.Status == MedicationDoseStatus.Given)} dose(s)").ToList()));
        sections.Add(new("Discharge summary", [a.DischargeSummary ?? "—"]));
        if (a.DischargeAdvice is { } adv) sections.Add(new("Advice", [adv]));
        if (charges.Total > 0) sections.Add(new("Room charges", [$"Rs {charges.Total:N0} ({string.Join(", ", charges.Lines.Select(l => $"{l.Units} {l.Unit}(s) × Rs {l.Rate:N0}"))})"]));

        return new ClinicLetter("Discharge summary", clinicName, branchLine, doctor?.FullName ?? "", doctor?.Specialty ?? "",
            patient.FullName, $"MRN {patient.Mrn}" + (patient.Phone is null ? "" : $" · {patient.Phone}"), a.DischargedAt?.ToLocalTime() ?? DateTime.Now,
            sections, SignatureLabel: doctor?.FullName);
    }

    private async Task<LocalAdmission> RequireAdmittedAsync(Guid id)
    {
        var a = await store.GetAsync<LocalAdmission>(id) ?? throw new CareException("Admission not found.");
        if (a.Status != AdmissionStatus.Admitted) throw new CareException("This patient has been discharged.");
        return a;
    }
}

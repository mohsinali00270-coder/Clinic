using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Contracts;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;
using SkiaSharp;

namespace Clinic.Api.Tests;

/// <summary>A clinic with the Phase 5 forms, the case and procedure-note forms, and four beds at MAIN.</summary>
public sealed class CareFixture : IAsyncLifetime
{
    public ClinicApiFactory Factory { get; } = new();
    public Dictionary<string, BedDto> Beds { get; } = [];

    public async Task InitializeAsync()
    {
        using var admin = await Factory.DemoAdminAsync();
        foreach (var (template, specialty) in new[]
                 {
                     ("patient_standard", (string?)null), ("vitals_standard", null), ("opd_general", "general"),
                     ("case_standard", null), ("procedure_note", null),
                 })
        {
            var t = (await admin.GetFromJsonAsync<List<FormTemplateDto>>(FormRoutes.FormTemplates))!.Single(x => x.Key == template);
            var form = await (await admin.PostAsJsonAsync(FormRoutes.Forms, new CreateFormRequest(t.Name, t.EntityType, null, specialty, template))).ReadAsync<FormDto>();
            await (await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null)).EnsureSuccessAsync();
        }

        var main = (await admin.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches))!.Single(b => b.Code == "MAIN");
        foreach (var (code, kind, amount, unit) in new[] { ("B1", BedKind.Bed, 3000m, ChargeUnit.Day), ("B2", BedKind.Room, 500m, ChargeUnit.Hour),
                     ("CH1", BedKind.Chair, 1000m, ChargeUnit.Day), ("B3", BedKind.Bed, 3000m, ChargeUnit.Day) })
            Beds[code] = await (await admin.PostAsJsonAsync(CareRoutes.Beds, new SaveBedRequest(main.Id, code, "Day care", kind, amount, unit))).ReadAsync<BedDto>();
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>A controllable clock for charges that accrue over hours and days.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class CareTests(CareFixture fixture) : IClassFixture<CareFixture>
{
    private ClinicApiFactory Factory => fixture.Factory;

    private sealed record DoctorSetup(DoctorDto Profile, string UserName);

    private async Task<DoctorSetup> DoctorAsync(string specialty = "general")
    {
        var userName = await Factory.CreateUserAsync("Doctor");
        using var admin = await Factory.DemoAdminAsync();
        var user = (await admin.GetFromJsonAsync<PagedResult<UserDto>>($"{ApiRoutes.Users}?search={userName}"))!.Items.Single();
        var main = (await admin.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches))!.Single(b => b.Code == "MAIN");
        var doctor = await (await admin.PostAsJsonAsync(SchedulingRoutes.Doctors, new SaveDoctorRequest(
            $"Dr {userName}", specialty, "F", "PMDC-88", DateTime.Today.AddYears(1), DoctorTypes.Permanent, 1000, 10, true, user.Id))).ReadAsync<DoctorDto>();
        for (var day = 0; day < 7; day++)
            await (await admin.PostAsJsonAsync($"{SchedulingRoutes.Doctors}/{doctor.Id}/schedules",
                new SaveScheduleRequest(main.Id, day, TimeSpan.FromHours(8), TimeSpan.FromHours(20)))).EnsureSuccessAsync();
        return new DoctorSetup(doctor, userName);
    }

    private async Task<ClientDevice> DeviceForAsync(string userName)
    {
        var d = await ClientDevice.CreateAsync(await Factory.DeviceAsync("demo", userName, SyncTestExtensions.TestPassword));
        await d.Engine.SyncAsync();
        return d;
    }

    private async Task<(LocalPatient Patient, LocalAppointment WalkIn)> WalkInAsync(DoctorSetup doctor, string gender = "F", string? phone = null)
    {
        await using var reception = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var forms = new DynamicDataService(reception.Db);
        var version = (await forms.ResolveFormAsync(SyncEntityNames.Patient, reception.Session.BranchId))!;
        var model = new DynamicFormModel(version, [], new EvaluationContext { Variables = new Dictionary<string, object?> { ["gender"] = gender, ["ageYears"] = 29d } });
        model.SetValue("allergies", new JsonArray(JsonValue.Create("none")));
        Assert.True(model.Validate());
        var patient = await reception.Patients.RegisterAsync(
            new PatientInput($"Patient {Guid.NewGuid():N}"[..16], gender, new DateTime(1997, 2, 1), false, phone, reception.Session.BranchId),
            version, model.ValuesToSave());
        var walkIn = await reception.Scheduling.WalkInAsync(patient.Id, doctor.Profile.Id, reception.Session.BranchId);
        Assert.Equal(0, (await reception.Engine.SyncAsync()).Outstanding);
        return (patient, walkIn);
    }

    private static async Task<string> ProblemsAsync(ClientDevice d) =>
        string.Join(" | ", (await d.Center.ListAsync()).Select(x => $"{x.Summary}: {x.Status} {x.LastError}"));

    private static async Task SyncCleanAsync(ClientDevice d)
    {
        var result = await d.Engine.SyncAsync();
        Assert.True(result.Outstanding == 0, await ProblemsAsync(d));
    }

    private static async Task<LocalEncounter> ConsultAsync(ClientDevice d, EncounterService encounters, LocalAppointment walkIn, DateTime? followUp = null, decimal weight = 62)
    {
        var forms = new DynamicDataService(d.Db);
        var e = await encounters.StartFromAppointmentAsync(walkIn.Id);
        var vitalsForm = (await forms.ResolveFormAsync(SyncEntityNames.Vitals, e.BranchId))!;
        var vitals = new DynamicFormModel(vitalsForm, [], EncounterService.ContextFor(e, ["Doctor"]));
        vitals.SetValue("weight_kg", weight);
        vitals.SetValue("height_cm", 160);
        await encounters.RecordVitalsAsync(e.PatientId, e.BranchId, e.Id, vitals.ValuesToSave());
        var notes = new DynamicFormModel((await encounters.FormAsync(e))!, [], EncounterService.ContextFor(e, ["Doctor"]));
        notes.SetValue("chief_complaint", "Booking visit, 8 weeks amenorrhoea");
        notes.SetValue("plan", "Folic acid. Booking bloods. Dating scan next week.");
        await encounters.SaveNotesAsync(e.Id, notes.ValuesToSave(), followUp);
        return e;
    }

    private static byte[] SignaturePng()
    {
        using var bitmap = new SKBitmap(300, 100);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var pen = new SKPaint { Color = SKColors.Black, StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
            canvas.DrawLine(20, 70, 120, 30, pen);
            canvas.DrawLine(120, 30, 280, 75, pen);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    // ---------------------------------------------------------------------
    // §4.10 cases
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Doctor_runs_a_pregnancy_case_offline_from_booking_to_closing()
    {
        var doctor = await DoctorAsync();
        var (patient, walkIn) = await WalkInAsync(doctor);
        await using var d = await DeviceForAsync(doctor.UserName);
        var forms = new DynamicDataService(d.Db);
        var cases = new CaseService(d.Store, forms);
        var encounters = new EncounterService(d.Store, forms);
        var procedures = new ProcedureService(d.Store, forms, d.Attachments);
        d.Transport.Offline = true; // ---- offline from here

        // FR-CASE-01/02: open from the type; the form adds pregnancy fields for this case type only.
        var type = (await cases.TypesAsync("gynae")).First();
        Assert.Equal("pregnancy", type.Code);
        var caseForm = (await cases.ResolveFormAsync(type, d.Session.BranchId))!;
        Assert.Equal("Case", caseForm.Definition.EntityType);
        var details = new JsonObject { ["presenting_problem"] = "First pregnancy, booking visit" };
        var missingLmp = await Assert.ThrowsAsync<CareException>(() => cases.OpenAsync(patient.Id, type.Id, d.Session.BranchId, doctor.Profile.Id, null, details));
        Assert.Contains("LMP", missingLmp.Message, StringComparison.OrdinalIgnoreCase);
        details["lmp"] = DateTime.Today.AddDays(-56).ToString("yyyy-MM-dd");
        details["gravida"] = 1;
        var c = await cases.OpenAsync(patient.Id, type.Id, d.Session.BranchId, doctor.Profile.Id, "Pregnancy 2026", details);

        var values = await forms.LoadValuesAsync(SyncEntityNames.Case, c.Id);
        Assert.Equal(DateTime.Today.AddDays(224), FormValues.AsDate(values["edd"])); // LMP + 280 days
        var plan = await cases.StepsAsync(c.Id);
        Assert.Equal(6, plan.Count);
        Assert.Equal(DateTime.Today.AddDays(7), plan.Single(s => s.Title == "Dating ultrasound").DueDate);
        Assert.Equal(CaseMemberRole.Primary, (await cases.TeamAsync(c.Id)).Single().Role);

        // FR-CASE-03: the visit and a procedure belong to the case; the booking-visit step is done with the visit.
        var e = await ConsultAsync(d, encounters, walkIn, weight: 61);
        await cases.LinkEncounterAsync(e.Id, c.Id);
        Assert.Equal(CarePlanStepStatus.Done, (await cases.StepsAsync(c.Id)).First(s => s.Title == "Booking visit").Status);
        var injection = (await procedures.TypesAsync(ProcedureCategory.Injection)).Single(t => t.Code == "im_injection");
        var p = await procedures.PlanAsync(patient.Id, d.Session.BranchId, injection.Id, new ProcedureLinks(CaseId: c.Id, DoctorId: doctor.Profile.Id));
        await procedures.CompleteAsync(p.Id);

        // FR-CASE-05/06: plan, team, notes.
        var late = await cases.AddStepAsync(c.Id, CarePlanStepKind.Test, "Urine routine", DateTime.Today.AddDays(-1));
        var colleague = await DoctorAsync();
        await cases.AddNoteAsync(c.Id, "Counselled about diet and danger signs.", doctor.Profile.FullName);

        // FR-CASE-04: progress — sessions done vs planned, overdue steps, weight trend.
        var progress = await cases.ProgressAsync(c.Id);
        Assert.Equal(2, progress.SessionsDone);
        Assert.Equal(8, progress.SessionsPlanned);
        Assert.Contains(progress.OverdueSteps, s => s.Id == late.Id);
        Assert.Contains(progress.Trends, t => t.FieldKey == "weight_kg" && t.Last == 61);
        Assert.Equal(200m, progress.ProceduresTotal);
        var timeline = await cases.TimelineAsync(c.Id);
        Assert.Contains(timeline, i => i.Kind == "encounter");
        Assert.Contains(timeline, i => i.Kind == "procedure");
        Assert.Contains(timeline, i => i.Kind == "note");

        // ---- back online: everything syncs, nothing rejected.
        d.Transport.Offline = false;
        await SyncCleanAsync(d);
        await d.Engine.SyncAsync(); // pull the colleague's profile
        await cases.AddDoctorAsync(c.Id, colleague.Profile.Id);
        await SyncCleanAsync(d);

        // The team sees the case on their own devices.
        await using var nurse = await DeviceForAsync(await Factory.CreateUserAsync("Nurse / Assistant"));
        Assert.NotNull(await nurse.Store.GetAsync<LocalCase>(c.Id));
        Assert.Equal(7, (await nurse.Store.ListAsync<LocalCarePlanStep>(s => s.CaseId == c.Id)).Count);
        Assert.Equal(c.Id, (await nurse.Store.GetAsync<LocalEncounter>(e.Id))!.CaseId);
        await using var other = await DeviceForAsync(colleague.UserName);
        Assert.Contains(await new CaseService(other.Store, new DynamicDataService(other.Db)).OpenForDoctorAsync(colleague.Profile.Id), x => x.Id == c.Id);

        // FR-CASE-07: closing needs an outcome; open steps are skipped.
        await Assert.ThrowsAsync<CareException>(() => cases.CloseAsync(c.Id, "", null));
        var summary = await cases.DraftClosingSummaryAsync(c.Id);
        Assert.Contains("2 session(s) of 8 planned", summary);
        await cases.CloseAsync(c.Id, "delivered", summary);
        Assert.DoesNotContain(await cases.StepsAsync(c.Id), s => s.Status == CarePlanStepStatus.Planned);
        await Assert.ThrowsAsync<CareException>(() => cases.AddNoteAsync(c.Id, "late note", null));
        await SyncCleanAsync(d);

        // A device that bypasses the service cannot change a closed case's plan.
        await d.Store.InsertAsync(new LocalCarePlanStep { CaseId = c.Id, BranchId = c.BranchId, Kind = CarePlanStepKind.Visit, Title = "Sneaky", Status = CarePlanStepStatus.Planned });
        Assert.Equal(1, (await d.Engine.SyncAsync()).Rejected);
        Assert.Contains("closed", (await d.Center.ListAsync()).Single().LastError);

        // Reopen is allowed.
        await cases.ReopenAsync(c.Id);
        Assert.Equal(CaseStatus.Open, (await cases.GetAsync(c.Id))!.Status);
    }

    // ---------------------------------------------------------------------
    // §4.11 referrals and follow-ups
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Internal_referral_reaches_the_receiving_doctor_and_external_letter_prints()
    {
        var a = await DoctorAsync();
        var b = await DoctorAsync("derma");
        var (patient, walkIn) = await WalkInAsync(a);

        await using var da = await DeviceForAsync(a.UserName);
        var formsA = new DynamicDataService(da.Db);
        var encA = new EncounterService(da.Store, formsA);
        var refA = new ReferralService(da.Store, formsA);
        da.Transport.Offline = true;
        var e = await ConsultAsync(da, encA, walkIn);
        await encA.AddDiagnosisAsync(e.Id, "L30.9", "Dermatitis, unspecified", primary: true);

        var input = new ReferralInput(patient.Id, e.BranchId, "Chronic rash not settling on emollients", ReferralPriority.Urgent, e.Id, null, a.Profile.Id);
        await Assert.ThrowsAsync<CareException>(() => refA.ReferInternalAsync(input, a.Profile.Id, null)); // not to yourself
        var internalRef = await refA.ReferInternalAsync(input, b.Profile.Id, null);
        Assert.Equal("derma", internalRef.ToSpecialty);
        var external = await refA.ReferExternalAsync(input with { Reason = "Skin biopsy", Priority = ReferralPriority.Routine }, "City Hospital Dermatology", "Dr Hamid");
        Assert.Contains("Diagnosis: L30.9 Dermatitis, unspecified", external.Summary);
        Assert.Contains("Weight 62", external.Summary);

        // FR-REF-02: the letter renders offline.
        var letter = await refA.BuildLetterAsync(external.Id, "Demo Clinic", "Main branch");
        var pdf = ClinicLetterPdf.Render(letter);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Contains(letter.Sections, s => s.Title == "Clinical summary");

        da.Transport.Offline = false;
        await SyncCleanAsync(da);

        // FR-REF-01: it appears on the receiving doctor's dashboard.
        await using var db = await DeviceForAsync(b.UserName);
        var refB = new ReferralService(db.Store, new DynamicDataService(db.Db));
        var inbox = await refB.InboxAsync(b.Profile.Id);
        Assert.Equal(internalRef.Id, Assert.Single(inbox).Id);

        // Only the receiving doctor may accept it: a tampered acceptance by the referrer is refused.
        var tampered = (await da.Store.GetAsync<LocalReferral>(internalRef.Id))!;
        tampered.Status = ReferralStatus.Accepted;
        await da.Store.UpdateAsync(tampered);
        Assert.Equal(1, (await da.Engine.SyncAsync()).Rejected);
        Assert.Contains("Only", (await da.Center.ListAsync()).Single().LastError);

        await refB.AcceptAsync(internalRef.Id, "Will see this week");
        await SyncCleanAsync(db);
        Assert.Equal(ReferralStatus.Accepted, Assert.Single(await refB.InboxAsync(b.Profile.Id)).Status);
        await refB.CompleteAsync(internalRef.Id, "Seen; started topical steroid");
        await SyncCleanAsync(db);
        Assert.Empty(await refB.InboxAsync(b.Profile.Id));

        // FR-REF-03: reception records an incoming referral.
        await using var reception = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var refR = new ReferralService(reception.Store, new DynamicDataService(reception.Db));
        await Assert.ThrowsAsync<CareException>(() => refR.RecordIncomingAsync(new ReferralInput(patient.Id, reception.Session.BranchId, "Diabetes review"), null, null));
        var incoming = await refR.RecordIncomingAsync(new ReferralInput(patient.Id, reception.Session.BranchId, "Diabetes review"), "doctor", "Dr Saleem, Gulberg Clinic");
        Assert.Equal(ReferralStatus.Accepted, incoming.Status);
        await SyncCleanAsync(reception);
    }

    [Fact]
    public async Task Follow_up_list_shows_due_patients_until_they_are_booked()
    {
        var doctor = await DoctorAsync();
        var (patient, walkIn) = await WalkInAsync(doctor, phone: "0300-1234567");
        await using var d = await DeviceForAsync(doctor.UserName);
        var forms = new DynamicDataService(d.Db);
        var encounters = new EncounterService(d.Store, forms);
        var followUps = new FollowUpService(d.Store);

        var e = await ConsultAsync(d, encounters, walkIn, followUp: DateTime.Today.AddDays(-2));
        await encounters.SignAsync(e.Id);

        var due = await followUps.DueAsync(DateTime.Today, doctorId: doctor.Profile.Id);
        var item = Assert.Single(due, i => i.PatientId == patient.Id);
        Assert.Equal(2, item.DaysOverdue(DateTime.Today));
        Assert.Equal("tel:+923001234567", ContactLinks.Tel(item.Phone));
        Assert.StartsWith("https://wa.me/923001234567?text=", ContactLinks.WhatsApp(item.Phone, FollowUpService.ReminderMessage(item, "Demo Clinic")));

        // Booking the follow-up settles it.
        var type = (await d.Scheduling.TypesAsync()).First(t => t.Code == "follow_up");
        var slot = (await d.Scheduling.SlotsAsync(doctor.Profile.Id, d.Session.BranchId, DateTime.Today.AddDays(1), type.Id)).First(s => s.IsAvailable);
        await d.Scheduling.BookAsync(patient.Id, doctor.Profile.Id, d.Session.BranchId, type.Id, slot.Date, slot.Start);
        Assert.DoesNotContain(await followUps.DueAsync(DateTime.Today, doctorId: doctor.Profile.Id), i => i.PatientId == patient.Id);
        await SyncCleanAsync(d);
    }

    // ---------------------------------------------------------------------
    // §4.12 procedures and day care
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Procedures_need_a_signed_consent_before_they_start()
    {
        var doctor = await DoctorAsync();
        var (patient, walkIn) = await WalkInAsync(doctor, "M");
        await using var d = await DeviceForAsync(doctor.UserName);
        var forms = new DynamicDataService(d.Db);
        var encounters = new EncounterService(d.Store, forms);
        var procedures = new ProcedureService(d.Store, forms, d.Attachments);
        d.Transport.Offline = true;

        var e = await ConsultAsync(d, encounters, walkIn);
        var suturing = (await procedures.TypesAsync()).Single(t => t.Code == "suturing");
        Assert.True(suturing.RequiresConsent);
        var p = await procedures.PlanAsync(patient.Id, e.BranchId, suturing.Id, new ProcedureLinks(EncounterId: e.Id, DoctorId: doctor.Profile.Id));
        Assert.Equal(2500m, p.Fee);
        Assert.Equal(3, ProcedureService.Consumables(p.ConsumablesJson).Count);
        Assert.StartsWith("Procedure note", (await procedures.FormAsync(p))!.Definition.Name.En);

        var blocked = await Assert.ThrowsAsync<CareException>(() => procedures.StartAsync(p.Id));
        Assert.Contains("consent", blocked.Message);

        // FR-PRC-02: consent with the signature pad, then the procedure and its note.
        await procedures.RecordConsentAsync(p.Id, patient.FullName, ConsentRelation.Self, SignaturePng(), "Nurse Amina");
        await procedures.StartAsync(p.Id);
        var note = new JsonObject { ["indication"] = "3 cm laceration left forearm", ["anaesthesia"] = "local", ["complications"] = false, ["aftercare"] = "Keep dry. Sutures out in 7 days." };
        await procedures.CompleteAsync(p.Id, note);
        Assert.Equal(ProcedureStatus.Done, (await procedures.GetAsync(p.Id))!.Status);

        var consent = await procedures.BuildConsentAsync(p.Id, "Demo Clinic", "Main branch");
        Assert.NotNull(consent.SignaturePng);
        Assert.Contains(consent.Sections, s => s.Urdu);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(ClinicLetterPdf.Render(consent), 0, 4));

        d.Transport.Offline = false;
        await SyncCleanAsync(d);
        Assert.Equal(1, await d.Attachments.UploadPendingAsync(d.Files));

        // The server decides whether consent is needed: a device that claims otherwise is refused.
        await d.Store.InsertAsync(new LocalProcedure
        {
            PatientId = patient.Id, BranchId = e.BranchId, ProcedureTypeId = suturing.Id, ProcedureCode = "suturing", Name = "Suturing",
            Status = ProcedureStatus.InProgress, StartedAt = DateTime.UtcNow, ConsentRequired = false,
        });
        Assert.Equal(1, (await d.Engine.SyncAsync()).Rejected);
        Assert.Contains("consent", (await d.Center.ListAsync()).Single().LastError);
    }

    [Fact]
    public async Task Day_care_admission_accrues_room_charges_and_discharges_offline()
    {
        var doctor = await DoctorAsync();
        var (patient, _) = await WalkInAsync(doctor, "M");
        var clock = new TestClock(DateTimeOffset.UtcNow.AddDays(-2));
        await using var nurse = await DeviceForAsync(await Factory.CreateUserAsync("Nurse / Assistant"));
        await using var doc = await DeviceForAsync(doctor.UserName);
        var care = new DayCareService(nurse.Store, clock);
        var doctorCare = new DayCareService(doc.Store, clock);
        nurse.Transport.Offline = true;

        var board = await care.BoardAsync(nurse.Session.BranchId);
        Assert.True(board.Count >= 4);
        var b1 = board.Single(x => x.Bed.Code == "B1").Bed;
        var b2 = board.Single(x => x.Bed.Code == "B2").Bed;

        var a = await care.AdmitAsync(patient.Id, nurse.Session.BranchId, b1.Id, "Dehydration, IV fluids", doctor.Profile.Id);
        await Assert.ThrowsAsync<CareException>(() => care.AdmitAsync(patient.Id, nurse.Session.BranchId, b2.Id, "again"));
        await care.AddNursingNoteAsync(a.Id, "Admitted, IV line in situ, obs stable.", NursingNoteKind.Observation, "Nurse");
        clock.Advance(TimeSpan.FromHours(26));
        await care.TransferAsync(a.Id, b2.Id, "Moved to private room");
        clock.Advance(TimeSpan.FromHours(3));

        // FR-PRC-04: 26 h in a day-rate bed = 2 days × 3000; 3 h in an hourly room = 3 × 500.
        var charges = await care.ChargesAsync(a.Id);
        Assert.Equal(7500m, charges.Total);
        Assert.Equal([2, 3], charges.Lines.Select(l => l.Units));

        nurse.Transport.Offline = false;
        await SyncCleanAsync(nurse);

        // FR-PRC-03 medication chart: the doctor orders, the nurse gives.
        await doc.Engine.SyncAsync();
        var order = await doctorCare.OrderMedicationAsync(a.Id, "Inj. Ondansetron", "4 mg", "iv", "tds");
        await SyncCleanAsync(doc);
        await nurse.Engine.SyncAsync();
        await care.RecordDoseAsync(order.Id);
        await Assert.ThrowsAsync<CareException>(() => care.RecordDoseAsync(order.Id, MedicationDoseStatus.Refused));
        await care.RecordDoseAsync(order.Id, MedicationDoseStatus.Held, "Vomiting settled");
        Assert.Equal(2, (await care.ChartAsync(a.Id)).Single().Doses.Count);

        // A nurse cannot write medication orders (server re-check).
        await nurse.Store.InsertAsync(new LocalMedicationOrder { AdmissionId = a.Id, BranchId = a.BranchId, DrugName = "Inj. Ceftriaxone", StartAt = DateTime.UtcNow, Status = MedicationOrderStatus.Active });
        var result = await nurse.Engine.SyncAsync();
        Assert.Equal(1, result.Rejected);
        await nurse.Center.DiscardAsync((await nurse.Center.ListAsync()).Single().Seq);

        await Assert.ThrowsAsync<CareException>(() => care.DischargeAsync(a.Id, " "));
        await care.DischargeAsync(a.Id, "Rehydrated, tolerating orally.", "ORS after each loose stool. Return if drowsy.");
        Assert.All((await care.ChartAsync(a.Id)), r => Assert.Equal(MedicationOrderStatus.Stopped, r.Order.Status));
        var discharge = await care.BuildDischargeSummaryAsync(a.Id, "Demo Clinic", "Main branch");
        Assert.Contains(discharge.Sections, s => s.Title == "Room charges" && s.Lines[0].StartsWith("Rs 7,500"));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(ClinicLetterPdf.Render(discharge), 0, 4));
        await SyncCleanAsync(nurse);

        // After discharge, nothing more is charted against the stay.
        await nurse.Store.InsertAsync(new LocalNursingNote { AdmissionId = a.Id, BranchId = a.BranchId, Body = "late", WrittenAt = DateTime.UtcNow });
        Assert.Equal(1, (await nurse.Engine.SyncAsync()).Rejected);
        Assert.Contains("discharged", (await nurse.Center.ListAsync()).Single().LastError);
    }

    [Fact]
    public async Task Two_offline_devices_admitting_to_the_same_bed_keep_both_and_flag_the_later_one()
    {
        var doctor = await DoctorAsync();
        var (p1, _) = await WalkInAsync(doctor, "M");
        var (p2, _) = await WalkInAsync(doctor, "F");
        await using var n1 = await DeviceForAsync(await Factory.CreateUserAsync("Nurse / Assistant"));
        await using var n2 = await DeviceForAsync(await Factory.CreateUserAsync("Nurse / Assistant"));
        var chair = fixture.Beds["CH1"].Id;
        n1.Transport.Offline = n2.Transport.Offline = true;

        var first = await new DayCareService(n1.Store).AdmitAsync(p1.Id, n1.Session.BranchId, chair, "Iron infusion");
        await Task.Delay(20);
        var second = await new DayCareService(n2.Store).AdmitAsync(p2.Id, n2.Session.BranchId, chair, "Nebulisation");

        n1.Transport.Offline = n2.Transport.Offline = false;
        await SyncCleanAsync(n1);
        await SyncCleanAsync(n2);
        await n1.Engine.SyncAsync();

        Assert.False((await n1.Store.GetAsync<LocalAdmission>(first.Id))!.BedConflict);
        Assert.True((await n1.Store.GetAsync<LocalAdmission>(second.Id))!.BedConflict);
        Assert.True((await new DayCareService(n1.Store).BoardAsync(n1.Session.BranchId)).Single(b => b.Bed.Id == chair).Conflict);
    }
}

/// <summary>Pure rules shared by device and server (no API needed).</summary>
public sealed class CareRulesTests
{
    [Fact]
    public void Room_charges_count_started_days_and_hours_with_a_minimum_of_one()
    {
        var day = Guid.NewGuid();
        var hour = Guid.NewGuid();
        var t0 = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        var stays = RoomCharges.Stays(t0, hour, null, [(day, hour, t0.AddHours(49))]);
        var lines = RoomCharges.Calculate(stays, id => id == day ? new BedRate(2000, ChargeUnit.Day) : new BedRate(300, ChargeUnit.Hour), t0.AddHours(49).AddMinutes(10));
        Assert.Equal(3, lines[0].Units);          // 49 h → 3 started days
        Assert.Equal(6000m, lines[0].Amount);
        Assert.Equal(1, lines[1].Units);          // 10 minutes still costs one hour
        Assert.Equal(300m, lines[1].Amount);
    }

    [Fact]
    public void Ocr_text_becomes_field_proposals_within_the_fields_ranges()
    {
        var vitals = Clinic.Application.Forms.FormTemplates.Find("vitals_standard")!.Definition;
        var text = """
                   CITY DIAGNOSTIC CENTRE
                   Patient: Ayesha Khan     Age: 34
                   BP 130/85 mmHg
                   Pulse: 92 /min   Temp 99.1 F
                   SpO2 97 %
                   Weight 68.5 kg  Height 162 cm
                   Ref no 8812345
                   """;
        var proposals = Clinic.Client.Core.Assist.OcrExtraction.ProposeForForm(text, vitals);
        decimal? Of(string key) => proposals.SingleOrDefault(p => p.FieldKey == key) is { } p ? FormValues.AsDecimal(p.Value) : null;
        Assert.Equal(130, Of("bp_systolic"));
        Assert.Equal(85, Of("bp_diastolic"));
        Assert.Equal(92, Of("pulse"));
        Assert.Equal(97, Of("spo2"));
        Assert.Equal(68.5m, Of("weight_kg"));
        Assert.Equal(162, Of("height_cm"));
        Assert.DoesNotContain(proposals, p => p.FieldKey == "bmi"); // calculated fields are never proposed

        var values = new JsonObject();
        Clinic.Client.Core.Assist.OcrExtraction.Apply(values, proposals.Where(p => p.FieldKey == "pulse"));
        Assert.Single(values);

        var id = Clinic.Client.Core.Assist.OcrExtraction.ExtractIdentity("""
            PAKISTAN National Identity Card
            Name
            AYESHA KHAN
            Father Name: Imran Khan
            Gender F   Country of Stay Pakistan
            Identity Number 35202-1234567-8   Date of Birth 14.03.1992
            """);
        Assert.Equal("Ayesha Khan", id.FullName);
        Assert.Equal("Imran Khan", id.FatherOrHusbandName);
        Assert.Equal("35202-1234567-8", id.Cnic);
        Assert.Equal(new DateTime(1992, 3, 14), id.DateOfBirth);
        Assert.Equal("F", id.Gender);
    }

    [Fact]
    public void Patient_instructions_read_in_english_and_urdu()
    {
        var lines = new[] { new PatientInstructions.Line("Paracetamol 500 mg", "1 tablet", "tds", 3, "After meals") };
        var en = PatientInstructions.Build(lines, "en", new DateTime(2026, 12, 1));
        Assert.Equal("Paracetamol 500 mg: take 1 tablet, three times a day, for 3 days.", en[0]);
        Assert.Equal("After meals", en[1]);
        var ur = PatientInstructions.Build(lines, "ur");
        Assert.Contains("دن میں تین بار", ur[0]);
        Assert.Contains("3 دن تک", ur[0]);
    }

    [Fact]
    public void Contact_links_normalise_pakistani_numbers()
    {
        Assert.Equal("+923001234567", ContactLinks.International("0300 1234567"));
        Assert.Equal("+923001234567", ContactLinks.International("+92 300 1234567"));
        Assert.Equal("+923001234567", ContactLinks.International("0092-300-1234567"));
        Assert.Null(ContactLinks.Tel(" "));
        Assert.Equal("sms:+923001234567", ContactLinks.Sms("03001234567"));
    }

}

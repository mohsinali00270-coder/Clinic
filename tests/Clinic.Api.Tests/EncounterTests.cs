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

namespace Clinic.Api.Tests;

/// <summary>A test clinic with the Phase 5 forms published once per test class.</summary>
public sealed class EncounterFixture : IAsyncLifetime
{
    public ClinicApiFactory Factory { get; } = new();

    public async Task InitializeAsync()
    {
        using var admin = await Factory.DemoAdminAsync();
        foreach (var (template, specialty) in new[] { ("patient_standard", (string?)null), ("vitals_standard", null), ("opd_general", "general") })
        {
            var t = (await admin.GetFromJsonAsync<List<FormTemplateDto>>(FormRoutes.FormTemplates))!.Single(x => x.Key == template);
            var form = await (await admin.PostAsJsonAsync(FormRoutes.Forms,
                new CreateFormRequest(t.Name, t.EntityType, null, specialty, template))).ReadAsync<FormDto>();
            await (await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null)).EnsureSuccessAsync();
        }
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

public sealed class EncounterTests(EncounterFixture fixture) : IClassFixture<EncounterFixture>
{
    private ClinicApiFactory Factory => fixture.Factory;

    private sealed record DoctorSetup(DoctorDto Profile, string UserName);

    /// <summary>A Doctor-role user linked to a doctor profile with a schedule at MAIN.</summary>
    private async Task<DoctorSetup> DoctorAsync(DateTime? licenceExpiry = null)
    {
        var userName = await Factory.CreateUserAsync("Doctor");
        using var admin = await Factory.DemoAdminAsync();
        var user = (await admin.GetFromJsonAsync<PagedResult<UserDto>>($"{ApiRoutes.Users}?search={userName}"))!.Items.Single();
        var main = (await admin.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches))!.Single(b => b.Code == "MAIN");
        var doctor = await (await admin.PostAsJsonAsync(SchedulingRoutes.Doctors, new SaveDoctorRequest(
            $"Dr {userName}", "general", "M", "PMDC-77", licenceExpiry ?? DateTime.Today.AddYears(1), DoctorTypes.Permanent, 1000, 10, true, user.Id)))
            .ReadAsync<DoctorDto>();
        await (await admin.PostAsJsonAsync($"{SchedulingRoutes.Doctors}/{doctor.Id}/schedules",
            new SaveScheduleRequest(main.Id, (int)DateTime.Today.DayOfWeek, TimeSpan.FromHours(8), TimeSpan.FromHours(20)))).EnsureSuccessAsync();
        return new DoctorSetup(doctor, userName);
    }

    private async Task<ClientDevice> DeviceForAsync(string userName)
    {
        var d = await ClientDevice.CreateAsync(await Factory.DeviceAsync("demo", userName, SyncTestExtensions.TestPassword));
        await d.Engine.SyncAsync();
        return d;
    }

    private static (EncounterService Encounters, PrescriptionService Rx, DynamicDataService Forms) Services(ClientDevice d)
    {
        var forms = new DynamicDataService(d.Db);
        var encounters = new EncounterService(d.Store, forms);
        return (encounters, new PrescriptionService(d.Store, forms, encounters), forms);
    }

    /// <summary>Reception registers a patient (penicillin allergy) and sends them to the doctor as a walk-in.</summary>
    private async Task<(LocalPatient Patient, LocalAppointment WalkIn)> WalkInAsync(DoctorSetup doctor, string[]? allergies = null)
    {
        await using var reception = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var forms = new DynamicDataService(reception.Db);
        var version = (await forms.ResolveFormAsync(SyncEntityNames.Patient, reception.Session.BranchId))!;
        var model = new DynamicFormModel(version, [], new EvaluationContext
        {
            Variables = new Dictionary<string, object?> { ["gender"] = "F", ["ageYears"] = 34d },
        });
        model.SetValue("allergies", new JsonArray((allergies ?? ["penicillin"]).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()));
        Assert.True(model.Validate());

        var patient = await reception.Patients.RegisterAsync(
            new PatientInput($"Patient {Guid.NewGuid():N}"[..16], "F", new DateTime(1992, 4, 1), false, null, reception.Session.BranchId),
            version, model.ValuesToSave());
        var walkIn = await reception.Scheduling.WalkInAsync(patient.Id, doctor.Profile.Id, reception.Session.BranchId);
        Assert.Equal(0, (await reception.Engine.SyncAsync()).Outstanding);
        return (patient, walkIn);
    }

    // ---------------------------------------------------------------------
    // Phase 5 acceptance
    // ---------------------------------------------------------------------

    /// <summary>"Doctor completes and prints a prescription fully offline."</summary>
    [Fact]
    public async Task Doctor_completes_and_prints_a_prescription_fully_offline()
    {
        var doctor = await DoctorAsync();
        var (patient, walkIn) = await WalkInAsync(doctor);

        await using var d = await DeviceForAsync(doctor.UserName);
        var (encounters, rx, forms) = Services(d);
        d.Transport.Offline = true; // ---- no network from here on

        // From the queue: open the consultation (the patient moves to "with doctor").
        var queue = await d.Scheduling.QueueAsync(doctor.Profile.Id, DateTime.Today);
        Assert.Contains(queue, q => q.Appointment.Id == walkIn.Id);
        var e = await encounters.StartFromAppointmentAsync(walkIn.Id);
        Assert.Equal(EncounterStatus.InProgress, e.Status);
        Assert.Equal(AppointmentStatus.WithDoctor, (await d.Store.GetAsync<LocalAppointment>(walkIn.Id))!.Status);
        Assert.Equal(e.Id, (await encounters.StartFromAppointmentAsync(walkIn.Id)).Id); // reopening returns the same one

        // Vitals with BMI and an abnormal SpO2.
        var vitalsForm = (await forms.ResolveFormAsync(SyncEntityNames.Vitals, e.BranchId))!;
        var vitals = new DynamicFormModel(vitalsForm, [], EncounterService.ContextFor(e, ["Doctor"]));
        vitals.SetValue("weight_kg", 70);
        vitals.SetValue("height_cm", 175);
        vitals.SetValue("spo2", 92);
        vitals.SetValue("temperature", 38.4m);
        Assert.Equal(22.9m, FormValues.AsDecimal(vitals.GetValue("bmi")));
        Assert.Equal(AbnormalSeverity.Critical, vitals.State("spo2").Abnormal);
        Assert.Equal("Fever", vitals.State("temperature").AbnormalMessage);
        await encounters.RecordVitalsAsync(patient.Id, e.BranchId, e.Id, vitals.ValuesToSave());

        // The specialty form (General OPD), validated offline.
        var form = (await encounters.FormAsync(e))!;
        Assert.Equal("General OPD consultation", form.Definition.Name.En);
        var notes = new DynamicFormModel(form, [], EncounterService.ContextFor(e, ["Doctor"]));
        Assert.False(notes.Validate());
        notes.SetValue("chief_complaint", "Sore throat and fever for 3 days");
        notes.SetValue("plan", "Warm saline gargles. Plenty of fluids. Return if breathing difficulty.");
        Assert.True(notes.Validate());
        await encounters.SaveNotesAsync(e.Id, notes.ValuesToSave(), DateTime.Today.AddDays(5));

        // ICD-10 from the offline list.
        var icd = await encounters.SearchIcdAsync("upper respiratory");
        Assert.Contains(icd, c => c.Code == "J06.9");
        await encounters.AddDiagnosisAsync(e.Id, "J06.9", "Acute upper respiratory infection, unspecified", primary: true);

        // Prescription from the drug master; the penicillin allergy is caught.
        var amoxicillin = (await rx.SearchDrugsAsync("amox")).First(x => x.Name == "Amoxicillin");
        var amoxItem = await rx.AddDrugAsync(e.Id, amoxicillin);
        Assert.Equal("tds", amoxItem.Frequency);
        var warnings = await rx.WarningsAsync(e.Id);
        Assert.Contains(warnings, w => w is { Kind: "allergy", Critical: true } && w.Message.Contains("penicillin"));
        await rx.RemoveItemAsync(amoxItem.Id);
        Assert.Empty(await rx.WarningsAsync(e.Id));

        await rx.AddDrugAsync(e.Id, (await rx.SearchDrugsAsync("azithro")).Single());
        var para = await rx.AddDrugAsync(e.Id, (await rx.SearchDrugsAsync("paracetamol")).First(x => x.Form == "Tablet"));
        para.Instructions = "Take only if temperature is above 38 °C";
        para.Frequency = "sos";
        await rx.UpdateItemAsync(para);
        var favourite = await rx.SaveAsTemplateAsync(e.Id, "Viral fever adult");

        // Sign and print.
        var draft = await rx.BuildDocumentAsync(e.Id, "Demo Clinic");
        Assert.True(draft.IsDraft);
        await encounters.SignAsync(e.Id);
        var document = await rx.BuildDocumentAsync(e.Id, "Demo Clinic");
        Assert.False(document.IsDraft);
        Assert.Equal(2, document.Lines.Count);
        Assert.Contains(document.Lines, l => l.FrequencyUrdu == "دن میں ایک بار" && l.Medicine.StartsWith("Azithromycin"));
        Assert.Contains(document.Diagnoses, x => x.StartsWith("J06.9"));
        Assert.Contains(document.Advice, x => x.Contains("saline gargles"));
        Assert.Contains(document.Vitals, x => x.StartsWith("BMI: 22.9"));
        Assert.Equal(DateTime.Today.AddDays(5), document.FollowUp);

        var pdf = PrescriptionPdf.Render(document);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(pdf.Length > 3000);
        var outDir = Path.Combine(Path.GetTempPath(), "clinic-test-output");
        Directory.CreateDirectory(outDir);
        await File.WriteAllBytesAsync(Path.Combine(outDir, "prescription.pdf"), pdf);

        // ---- Back online: everything syncs, nothing rejected.
        d.Transport.Offline = false;
        var state = await d.Engine.SyncAsync(manual: true);
        Assert.True(state.Outstanding == 0,
            string.Join(" | ", (await d.Center.ListAsync()).Select(x => $"{x.Summary}: {x.Status} {x.LastError}")));

        // A nurse at the branch receives the encounter; reception does not (no Encounters.View).
        await using var nurse = await DeviceForAsync(await Factory.CreateUserAsync("Nurse / Assistant"));
        Assert.Equal(EncounterStatus.Signed, (await nurse.Store.GetAsync<LocalEncounter>(e.Id))!.Status);
        Assert.Equal(2, (await Services(nurse).Rx.ItemsAsync(e.Id)).Count);
        await using var reception = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        Assert.Null(await reception.Store.GetAsync<LocalEncounter>(e.Id));
        Assert.NotNull(await reception.Store.GetAsync<LocalPatient>(patient.Id));

        // The favourite reaches the doctor's other devices and applies in one tap.
        await using var tablet = await DeviceForAsync(doctor.UserName);
        var (tabletEncounters, tabletRx, _) = Services(tablet);
        Assert.Contains(await tabletRx.TemplatesAsync(doctor.Profile.Id), t => t.Id == favourite.Id);
        var e2 = await tabletEncounters.StartAsync(patient.Id, doctor.Profile.Id, tablet.Session.BranchId);
        Assert.Equal(2, await tabletRx.ApplyTemplateAsync(e2.Id, favourite.Id));
        Assert.Contains(await tabletRx.WarningsAsync(e2.Id), w => w.Kind == "duplicate" && w.Message.Contains("still running"));
    }

    [Fact]
    public async Task Signed_encounters_change_only_through_amendments_by_the_signing_doctor()
    {
        var doctor = await DoctorAsync();
        var (patient, walkIn) = await WalkInAsync(doctor, ["none"]);
        await using var d = await DeviceForAsync(doctor.UserName);
        var (encounters, rx, _) = Services(d);

        var e = await encounters.StartFromAppointmentAsync(walkIn.Id);
        var form = (await encounters.FormAsync(e))!;
        var notes = new DynamicFormModel(form, [], EncounterService.ContextFor(e, []));
        notes.SetValue("chief_complaint", "Headache");
        await encounters.SaveNotesAsync(e.Id, notes.ValuesToSave(), null);
        await encounters.AddDiagnosisAsync(e.Id, "R51", "Headache", true);
        await encounters.SignAsync(e.Id);
        Assert.Equal(0, (await d.Engine.SyncAsync()).Outstanding);

        // Locked on the device.
        await Assert.ThrowsAsync<EncounterException>(() => encounters.SaveNotesAsync(e.Id, notes.ValuesToSave(), null));
        await Assert.ThrowsAsync<EncounterException>(() => encounters.AddDiagnosisAsync(e.Id, "G43.9", "Migraine", false));

        // Amend with a reason: diagnosis + note + prescription line, each recorded.
        await encounters.AddDiagnosisAsync(e.Id, "G43.9", "Migraine, unspecified", false, amendReason: "Aura reported after signing");
        var changed = (JsonObject)notes.ValuesToSave().DeepClone();
        changed["chief_complaint"] = "Headache with visual aura";
        Assert.Equal(1, await encounters.AmendNotesAsync(e.Id, changed, "Aura reported after signing"));
        await rx.AddDrugAsync(e.Id, (await rx.SearchDrugsAsync("paracetamol")).First(), amendReason: "Analgesia added");

        var amendments = await encounters.AmendmentsAsync(e.Id);
        Assert.Contains(amendments, a => a.FieldKey == "chief_complaint" && a.OldValue!.Contains("Headache") && a.NewValue!.Contains("aura"));
        Assert.Contains(amendments, a => a.FieldKey == EncounterAmendKeys.Diagnoses);
        Assert.Contains(amendments, a => a.FieldKey == EncounterAmendKeys.Prescription);
        Assert.Equal(EncounterStatus.Amended, (await encounters.GetAsync(e.Id))!.Status);
        var amended = await d.Engine.SyncAsync();
        Assert.True(amended.Outstanding == 0, string.Join(" | ", (await d.Center.ListAsync()).Select(x => $"{x.Summary}: {x.LastError}")));

        // A device that bypasses the service (no amendment) is refused by the server.
        var tamper = (await d.Store.ListAsync<LocalFieldValue>(v => v.RecordId == e.Id && v.FieldKey == "chief_complaint")).Single();
        tamper.ValueText = "Silently changed";
        await d.Store.UpdateAsync(tamper);
        var refused = await d.Engine.SyncAsync();
        Assert.Equal(1, refused.Rejected);
        Assert.Contains("amendment", (await d.Center.ListAsync()).Single().LastError);

        // Another doctor cannot amend it.
        var other = await DoctorAsync();
        await using var od = await DeviceForAsync(other.UserName);
        var (otherEncounters, _, _) = Services(od);
        var seen = await od.Store.GetAsync<LocalEncounter>(e.Id);
        Assert.NotNull(seen); // same branch: other doctors can read it
        var notSigner = await Assert.ThrowsAsync<EncounterException>(() => otherEncounters.AddDiagnosisAsync(seen!.Id, "R51", "x", false, "reason"));
        Assert.Contains("signing doctor", notSigner.Message);
        _ = patient;
    }

    [Fact]
    public async Task Clinical_edits_from_two_devices_on_the_same_field_become_a_conflict_not_an_overwrite()
    {
        var doctor = await DoctorAsync();
        var (_, walkIn) = await WalkInAsync(doctor, ["none"]);
        await using var a = await DeviceForAsync(doctor.UserName);
        await using var b = await DeviceForAsync(doctor.UserName);
        var (encA, _, _) = Services(a);

        var e = await encA.StartFromAppointmentAsync(walkIn.Id);
        var dx = await encA.AddDiagnosisAsync(e.Id, null, "Viral illness", true);
        await a.Engine.SyncAsync();
        await b.Engine.SyncAsync();

        a.Transport.Offline = b.Transport.Offline = true;
        var onA = (await a.Store.GetAsync<LocalDiagnosis>(dx.Id))!;
        onA.Description = "Dengue fever";
        await a.Store.UpdateAsync(onA);
        var onB = (await b.Store.GetAsync<LocalDiagnosis>(dx.Id))!;
        onB.Description = "Typhoid fever";
        await b.Store.UpdateAsync(onB);

        a.Transport.Offline = b.Transport.Offline = false;
        await a.Engine.SyncAsync(manual: true);
        var stateB = await b.Engine.SyncAsync(manual: true);
        Assert.Equal(1, stateB.Conflicts);
        var entry = (await b.Center.ListAsync()).Single();
        Assert.Equal(OutboxStatus.Conflict, entry.Status);
        Assert.Contains("Dengue", entry.ServerData);

        // The doctor reviews and keeps their own version deliberately.
        await b.Center.KeepMineAsync(entry.Seq);
        Assert.Equal(0, (await b.Engine.SyncAsync(manual: true)).Outstanding);
        await a.Engine.SyncAsync();
        Assert.Equal("Typhoid fever", (await a.Store.GetAsync<LocalDiagnosis>(dx.Id))!.Description);
    }

    [Fact]
    public async Task Nurse_records_vitals_but_cannot_start_or_sign_and_expired_licences_block_consultations()
    {
        var doctor = await DoctorAsync();
        var (patient, walkIn) = await WalkInAsync(doctor, ["none"]);
        await using var nurse = await DeviceForAsync(await Factory.CreateUserAsync("Nurse / Assistant"));
        var (encounters, _, forms) = Services(nurse);

        var vitalsForm = (await forms.ResolveFormAsync(SyncEntityNames.Vitals, nurse.Session.BranchId))!;
        var model = new DynamicFormModel(vitalsForm, [], new EvaluationContext());
        model.SetValue("bp_systolic", 150);
        model.SetValue("bp_diastolic", 95);
        await encounters.RecordVitalsAsync(patient.Id, nurse.Session.BranchId, null, model.ValuesToSave());
        await nurse.Scheduling.SendToVitalsAsync(walkIn.Id);
        Assert.Equal(0, (await nurse.Engine.SyncAsync()).Outstanding);

        await encounters.StartAsync(patient.Id, doctor.Profile.Id, nurse.Session.BranchId);
        Assert.Equal(1, (await nurse.Engine.SyncAsync()).Rejected); // nurses cannot write encounters

        var expired = await DoctorAsync(DateTime.Today.AddDays(-1));
        await using var d = await DeviceForAsync(expired.UserName);
        var ex = await Assert.ThrowsAsync<EncounterException>(() => Services(d).Encounters.StartAsync(patient.Id, expired.Profile.Id, d.Session.BranchId));
        Assert.Contains("licence expired", ex.Message);
    }

    [Fact]
    public async Task Admin_imports_icd_codes_and_drugs()
    {
        using var admin = await Factory.DemoAdminAsync();
        var icd = await (await admin.PostAsJsonAsync("api/v1/icd10/import",
            new { csv = "code,description\nU07.1,COVID-19 virus identified\nJ06.9,Acute upper respiratory infection, unspecified\n,\n" })).ReadAsync<JsonObject>();
        Assert.Equal(1, icd["added"]!.GetValue<int>());
        var drugs = await (await admin.PostAsJsonAsync("api/v1/drugs/import",
            new { csv = "name,generic,strength,form,route,defaultDose,defaultFrequency,defaultDurationDays,allergyClass\nCefuroxime,Cefuroxime,500 mg,Tablet,oral,1 tablet,bd,5,\nBad,,1,Tablet,oral,1,thrice,5,\n" })).ReadAsync<JsonObject>();
        Assert.Equal(1, drugs["added"]!.GetValue<int>());
        Assert.Single(drugs["errors"]!.AsArray());

        await using var d = await DeviceForAsync(await Factory.CreateUserAsync("Doctor"));
        Assert.Contains(await Services(d).Encounters.SearchIcdAsync("U07"), c => c.Code == "U07.1");
        Assert.Contains(await Services(d).Rx.SearchDrugsAsync("cefur"), x => x.DefaultFrequency == "bd");
    }

    [Fact]
    public void Pdf_renders_long_prescriptions_over_several_pages_with_urdu()
    {
        var lines = Enumerable.Range(1, 25).Select(i => new PrescriptionLine($"Medicine {i} 500 mg (Tablet)", "1 tablet", "Twice a day",
            "دن میں دو بار", "5 days", i % 3 == 0 ? "After meals with a full glass of water; stop if a rash appears." : null)).ToList();
        var doc = new PrescriptionDocument("Demo Clinic", "Main Branch · 1 Main Rd", "Dr Test", "General OPD · Reg. 1", "Ayesha Khan",
            "MRN MAIN-D01-00001 · F · 34 y", DateTime.Now, ["BP: 120/80 mmHg"], ["J06.9 — Acute URI"], lines, ["Rest"], null, false, true);
        var pdf = PrescriptionPdf.Render(doc);
        var text = Encoding.ASCII.GetString(pdf);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page\b").Count >= 2, "expected at least two pages");
    }
}

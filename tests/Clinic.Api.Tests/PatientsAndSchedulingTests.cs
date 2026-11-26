using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Client.Core.Sync;
using Clinic.Shared.Contracts;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;
using Microsoft.AspNetCore.Http.Connections;

namespace Clinic.Api.Tests;

public sealed class PatientsAndSchedulingTests(ClinicApiFactory factory) : IClassFixture<ClinicApiFactory>
{
    private static readonly DateTime Tomorrow = DateTime.Today.AddDays(1);

    /// <summary>A doctor working at MAIN every day 08:00–20:00 in 15-minute slots.</summary>
    private async Task<DoctorDto> CreateDoctorAsync(string specialty = "general", int capacity = 1, Guid? userId = null)
    {
        using var admin = await factory.DemoAdminAsync();
        var main = (await admin.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches))!.Single(b => b.Code == "MAIN");
        var doctor = await (await admin.PostAsJsonAsync(SchedulingRoutes.Doctors, new SaveDoctorRequest(
            $"Dr {Guid.NewGuid():N}"[..10], specialty, "F", "PMDC-1", DateTime.Today.AddYears(1), DoctorTypes.Permanent, 1500, 10, true, userId)))
            .ReadAsync<DoctorDto>();
        for (var day = 0; day < 7; day++)
        {
            await (await admin.PostAsJsonAsync($"{SchedulingRoutes.Doctors}/{doctor.Id}/schedules", new SaveScheduleRequest(
                main.Id, day, TimeSpan.FromHours(8), TimeSpan.FromHours(20), 15, capacity))).EnsureSuccessAsync();
        }
        return doctor;
    }

    private async Task<ClientDevice> ReceptionAsync()
    {
        var user = await factory.CreateUserAsync("Receptionist");
        var device = await ClientDevice.CreateAsync(await factory.DeviceAsync("demo", user, SyncTestExtensions.TestPassword));
        await device.Engine.SyncAsync();
        return device;
    }

    private static Task<LocalPatient> RegisterAsync(ClientDevice d, string name, string? phone = null, string gender = "M") =>
        d.Patients.RegisterAsync(new PatientInput(name, gender, new DateTime(1985, 3, 1), false, phone, d.Session.BranchId), null, null);

    private static async Task<Guid> ConsultationTypeAsync(ClientDevice d) =>
        (await d.Scheduling.TypesAsync()).Single(t => t.Code == "consultation").Id;

    // ---------------------------------------------------------------------
    // Phase 4 acceptance
    // ---------------------------------------------------------------------

    /// <summary>"Receptionist registers and books offline; queue updates across devices within 10 s online."</summary>
    [Fact]
    public async Task Receptionist_registers_and_books_offline_and_the_queue_reaches_other_devices_within_10_seconds()
    {
        var doctor = await CreateDoctorAsync();
        await using var reception = await ReceptionAsync();
        await using var doctorDevice = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await doctorDevice.Engine.SyncAsync();

        // ---- Offline at the front desk.
        reception.Transport.Offline = true;
        var patient = await RegisterAsync(reception, "Ayesha Siddiqui", "0301-5550001", "F");
        Assert.Matches(@"^MAIN-D[0-9A-Z]+-\d{5}$", patient.Mrn);

        var type = await ConsultationTypeAsync(reception);
        var slots = await reception.Scheduling.SlotsAsync(doctor.Id, reception.Session.BranchId, Tomorrow, type);
        Assert.Equal(48, slots.Count); // 08:00–20:00 in 15-minute slots
        var booked = await reception.Scheduling.BookAsync(patient.Id, doctor.Id, reception.Session.BranchId, type, Tomorrow, TimeSpan.FromHours(9));
        Assert.False((await reception.Scheduling.SlotsAsync(doctor.Id, reception.Session.BranchId, Tomorrow, type)).Single(s => s.Start == TimeSpan.FromHours(9)).IsAvailable);

        var walkIn = await reception.Scheduling.WalkInAsync(patient.Id, doctor.Id, reception.Session.BranchId);
        Assert.Equal(1, walkIn.TokenNo);
        Assert.Single(await reception.Scheduling.QueueAsync(doctor.Id, DateTime.Today));

        // ---- Back online: the doctor's device listens for nudges.
        reception.Transport.Offline = false;
        Assert.Equal(0, (await reception.Engine.SyncAsync(manual: true)).Outstanding);

        await using var nudges = new SyncNudgeClient(factory.Server.BaseAddress, () => Task.FromResult<string?>(doctorDevice.Session.Token.AccessToken),
            () => _ = doctorDevice.Engine.SyncAsync(), factory.Server.CreateHandler, HttpTransportType.LongPolling);
        Assert.True(await nudges.TryStartAsync());
        await doctorDevice.Engine.SyncAsync();
        Assert.Contains(await doctorDevice.Scheduling.QueueAsync(doctor.Id, DateTime.Today), q => q.Appointment.Id == walkIn.Id);
        Assert.NotNull(await doctorDevice.Store.GetAsync<LocalAppointment>(booked.Id));

        // A second walk-in at reception shows up on the doctor's queue by itself, within 10 seconds.
        var second = await RegisterAsync(reception, "Bilal Ahmed", "0302-5550002");
        var walkIn2 = await reception.Scheduling.WalkInAsync(second.Id, doctor.Id, reception.Session.BranchId, QueuePriority.Emergency);
        var clock = Stopwatch.StartNew();
        await reception.Engine.SyncAsync();

        List<QueueEntry> queue = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            queue = await doctorDevice.Scheduling.QueueAsync(doctor.Id, DateTime.Today);
            if (queue.Any(q => q.Appointment.Id == walkIn2.Id)) break;
            await Task.Delay(100);
        }
        Assert.True(queue.Any(q => q.Appointment.Id == walkIn2.Id), "queue did not update within 10 s");
        Assert.Equal(walkIn2.Id, queue[0].Appointment.Id); // emergency goes first
        Assert.Equal("Bilal Ahmed", queue[0].PatientName);
    }

    // ---------------------------------------------------------------------

    [Fact]
    public async Task Patients_are_found_by_name_parts_phone_mrn_and_dynamic_id_offline_and_duplicates_are_flagged()
    {
        using (var admin = await factory.DemoAdminAsync())
        {
            var form = await (await admin.PostAsJsonAsync(FormRoutes.Forms,
                new CreateFormRequest("Patient registration", "Patient", TemplateKey: "patient_standard"))).ReadAsync<FormDto>();
            await (await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null)).EnsureSuccessAsync();
        }

        await using var d = await ReceptionAsync();
        d.Transport.Offline = true;

        var forms = new DynamicDataService(d.Db);
        var version = (await forms.ResolveFormAsync(SyncEntityNames.Patient, d.Session.BranchId))!;
        var model = new DynamicFormModel(version, [], new EvaluationContext
        {
            Variables = new Dictionary<string, object?> { ["gender"] = "M", ["ageYears"] = 40d },
        });
        model.SetValue("cnic", "42101-7654321-3");
        Assert.True(model.Validate());

        var p = await d.Patients.RegisterAsync(new PatientInput("Muhammad Ali Khan", "M", new DateTime(1986, 1, 1), false, "0300-1234567", d.Session.BranchId),
            version, model.ValuesToSave());

        Assert.Contains(await d.Patients.SearchAsync("muh kha"), x => x.Id == p.Id);
        Assert.Contains(await d.Patients.SearchAsync("Khan Ali"), x => x.Id == p.Id);
        Assert.Contains(await d.Patients.SearchAsync("3001234567"), x => x.Id == p.Id);
        Assert.Contains(await d.Patients.SearchAsync(p.Mrn), x => x.Id == p.Id);
        Assert.Contains(await d.Patients.SearchAsync("4210176543213"), x => x.Id == p.Id);
        Assert.DoesNotContain(await d.Patients.SearchAsync("zzzz"), x => x.Id == p.Id);

        var dup = await d.Patients.FindDuplicatesAsync("Mohammad Ali Khan", "+92 300 1234567", new DateTime(1986, 6, 1));
        Assert.Contains(dup, x => x.Id == p.Id);
        Assert.Empty(await d.Patients.FindDuplicatesAsync("Sara Bibi", "0300-1234567", null));

        // Synced to another device, the patient is searchable there too.
        d.Transport.Offline = false;
        await d.Engine.SyncAsync(manual: true);
        await using var other = await ReceptionAsync();
        Assert.Contains(await other.Patients.SearchAsync("ali khan"), x => x.Id == p.Id);
        Assert.Contains(await other.Patients.SearchAsync("42101"), x => x.Id == p.Id);
    }

    [Fact]
    public async Task Slots_respect_capacity_leave_and_branch_closures()
    {
        var doctor = await CreateDoctorAsync(capacity: 2);
        await using var d = await ReceptionAsync();
        var type = await ConsultationTypeAsync(d);
        var a = await RegisterAsync(d, "Patient A", "0303-1110001");
        var b = await RegisterAsync(d, "Patient B", "0303-1110002");
        var c = await RegisterAsync(d, "Patient C", "0303-1110003");
        var at = TimeSpan.FromHours(10);

        await d.Scheduling.BookAsync(a.Id, doctor.Id, d.Session.BranchId, type, Tomorrow, at);
        await d.Scheduling.BookAsync(b.Id, doctor.Id, d.Session.BranchId, type, Tomorrow, at);
        await Assert.ThrowsAsync<SchedulingException>(() => d.Scheduling.BookAsync(c.Id, doctor.Id, d.Session.BranchId, type, Tomorrow, at));
        await Assert.ThrowsAsync<SchedulingException>(() => d.Scheduling.BookAsync(a.Id, doctor.Id, d.Session.BranchId, type, Tomorrow, at));
        await d.Scheduling.BookAsync(c.Id, doctor.Id, d.Session.BranchId, type, Tomorrow, at, allowOverbook: true);

        using (var admin = await factory.DemoAdminAsync())
        {
            await (await admin.PostAsJsonAsync($"{SchedulingRoutes.Doctors}/{doctor.Id}/leaves",
                new SaveLeaveRequest(Tomorrow.AddDays(1), Tomorrow.AddDays(2), "Conference", null))).EnsureSuccessAsync();
            await (await admin.PostAsJsonAsync(SchedulingRoutes.BranchClosures,
                new SaveBranchClosureRequest(d.Session.BranchId, Tomorrow.AddDays(5), "Eid"))).EnsureSuccessAsync();
        }
        await d.Engine.SyncAsync();

        Assert.Empty(await d.Scheduling.SlotsAsync(doctor.Id, d.Session.BranchId, Tomorrow.AddDays(1), type));
        Assert.Empty(await d.Scheduling.SlotsAsync(doctor.Id, d.Session.BranchId, Tomorrow.AddDays(5), type));
        Assert.NotEmpty(await d.Scheduling.SlotsAsync(doctor.Id, d.Session.BranchId, Tomorrow.AddDays(3), type));
        Assert.Empty(await d.Scheduling.SlotsAsync(doctor.Id, d.Session.BranchId, DateTime.Today.AddDays(90), type)); // beyond 60 days

        var next = await d.Scheduling.NextAvailableAsync(d.Session.BranchId, "general", Tomorrow.AddDays(1), typeId: type);
        Assert.Contains(next, s => s.DoctorId == doctor.Id && s.Date == Tomorrow.AddDays(3));
    }

    [Fact]
    public async Task Same_slot_booked_on_two_offline_devices_is_kept_and_flagged_as_a_conflict()
    {
        var doctor = await CreateDoctorAsync();
        await using var a = await ReceptionAsync();
        await using var b = await ReceptionAsync();
        var type = await ConsultationTypeAsync(a);
        var pa = await RegisterAsync(a, "Offline One", "0304-2220001");
        var pb = await RegisterAsync(b, "Offline Two", "0304-2220002");

        a.Transport.Offline = b.Transport.Offline = true;
        var first = await a.Scheduling.BookAsync(pa.Id, doctor.Id, a.Session.BranchId, type, Tomorrow, TimeSpan.FromHours(11));
        var second = await b.Scheduling.BookAsync(pb.Id, doctor.Id, b.Session.BranchId, type, Tomorrow, TimeSpan.FromHours(11));

        // Two walk-ins for the same doctor offline both get token 1 locally…
        var wa = await a.Scheduling.WalkInAsync(pa.Id, doctor.Id, a.Session.BranchId);
        var wb = await b.Scheduling.WalkInAsync(pb.Id, doctor.Id, b.Session.BranchId);
        Assert.Equal(wa.TokenNo, wb.TokenNo);

        a.Transport.Offline = b.Transport.Offline = false;
        await a.Engine.SyncAsync(manual: true);
        Assert.Equal(0, (await b.Engine.SyncAsync(manual: true)).Outstanding); // nothing rejected
        await a.Engine.SyncAsync();
        await b.Engine.SyncAsync();

        foreach (var d in new[] { a, b })
        {
            Assert.False((await d.Store.GetAsync<LocalAppointment>(first.Id))!.SlotConflict);
            Assert.True((await d.Store.GetAsync<LocalAppointment>(second.Id))!.SlotConflict);
        }

        // …and the server hands the later one the next free token.
        var tokens = new[] { (await a.Store.GetAsync<LocalAppointment>(wa.Id))!.TokenNo, (await a.Store.GetAsync<LocalAppointment>(wb.Id))!.TokenNo };
        Assert.Equal(2, tokens.Distinct().Count());
    }

    [Fact]
    public async Task Queue_moves_through_vitals_doctor_and_completion_with_history_and_estimates()
    {
        var doctor = await CreateDoctorAsync();
        await using var d = await ReceptionAsync();
        var type = await ConsultationTypeAsync(d);
        var p1 = await RegisterAsync(d, "Queue One", "0305-3330001");
        var p2 = await RegisterAsync(d, "Queue Two", "0305-3330002");
        var p3 = await RegisterAsync(d, "Queue Three", "0305-3330003");

        var w1 = await d.Scheduling.WalkInAsync(p1.Id, doctor.Id, d.Session.BranchId);
        var w2 = await d.Scheduling.WalkInAsync(p2.Id, doctor.Id, d.Session.BranchId);
        var w3 = await d.Scheduling.WalkInAsync(p3.Id, doctor.Id, d.Session.BranchId, QueuePriority.Priority);

        var queue = await d.Scheduling.QueueAsync(doctor.Id, DateTime.Today);
        Assert.Equal([w3.Id, w1.Id, w2.Id], queue.Select(q => q.Appointment.Id));
        Assert.Equal([0, 10, 20], queue.Select(q => q.EstimatedWaitMinutes)); // doctor average 10 min

        await d.Scheduling.SendToVitalsAsync(w3.Id);
        var called = await d.Scheduling.CallNextAsync(doctor.Id);
        Assert.Equal(w3.Id, called!.Id);
        Assert.Equal(AppointmentStatus.WithDoctor, called.Status);

        await d.Scheduling.HoldAsync(w1.Id, "Went for X-ray");
        var next = await d.Scheduling.CallNextAsync(doctor.Id); // completes w3, calls w2 (w1 on hold)
        Assert.Equal(w2.Id, next!.Id);
        Assert.Equal(AppointmentStatus.Completed, (await d.Store.GetAsync<LocalAppointment>(w3.Id))!.Status);

        await Assert.ThrowsAsync<SchedulingException>(() => d.Scheduling.CheckInAsync(w3.Id)); // completed is final

        var history = await d.Store.ListAsync<LocalAppointmentStatusHistory>(h => h.AppointmentId == w3.Id);
        Assert.Equal(
            [AppointmentStatus.CheckedIn, AppointmentStatus.Vitals, AppointmentStatus.WithDoctor, AppointmentStatus.Completed],
            history.OrderBy(h => h.ChangedAt).ThenBy(h => h.CreatedAt).Select(h => h.ToStatus));

        // Transfer to another doctor puts the patient in that queue with a new token.
        var doctor2 = await CreateDoctorAsync();
        await d.Engine.SyncAsync();
        await d.Scheduling.ResumeAsync(w1.Id);
        var moved = await d.Scheduling.TransferAsync(w1.Id, doctor2.Id);
        Assert.Equal(1, moved.TokenNo);
        Assert.Single(await d.Scheduling.QueueAsync(doctor2.Id, DateTime.Today));

        Assert.Equal(0, (await d.Engine.SyncAsync()).Outstanding);
        var timeline = await d.Patients.TimelineAsync(p3.Id);
        var completed = Assert.Single(timeline, t => t.Kind == "appointment" && t.Title.StartsWith("Completed"));
        // Regression: walk-in times are real UTC instants (check-in), not a local midnight mislabelled as UTC.
        Assert.True(Math.Abs((completed.At - DateTime.UtcNow).TotalMinutes) < 5, $"timeline time {completed.At:o} is off");
    }

    [Fact]
    public async Task Reschedule_is_limited_and_cancellation_keeps_the_reason()
    {
        var doctor = await CreateDoctorAsync();
        await using var d = await ReceptionAsync();
        var type = await ConsultationTypeAsync(d);
        var p = await RegisterAsync(d, "Reschedule Me", "0306-4440001");

        var a1 = await d.Scheduling.BookAsync(p.Id, doctor.Id, d.Session.BranchId, type, Tomorrow, TimeSpan.FromHours(12));
        var a2 = await d.Scheduling.RescheduleAsync(a1.Id, Tomorrow, TimeSpan.FromHours(13), "patient_request");
        var a3 = await d.Scheduling.RescheduleAsync(a2.Id, Tomorrow.AddDays(1), TimeSpan.FromHours(13));
        Assert.Equal(2, a3.RescheduleCount);
        var ex = await Assert.ThrowsAsync<SchedulingException>(() => d.Scheduling.RescheduleAsync(a3.Id, Tomorrow.AddDays(2), TimeSpan.FromHours(13)));
        Assert.Contains("limit", ex.Message);
        Assert.Equal(AppointmentStatus.Rescheduled, (await d.Store.GetAsync<LocalAppointment>(a1.Id))!.Status);
        Assert.True((await d.Scheduling.SlotsAsync(doctor.Id, d.Session.BranchId, Tomorrow, type)).Single(s => s.Start == TimeSpan.FromHours(12)).IsAvailable);

        var cancelled = await d.Scheduling.CancelAsync(a3.Id, "feeling_better");
        Assert.Equal("feeling_better", cancelled.CancelReason);
        await Assert.ThrowsAsync<SchedulingException>(() => d.Scheduling.NoShowAsync(a3.Id));
        Assert.Equal(0, (await d.Engine.SyncAsync()).Outstanding);
    }

    [Fact]
    public async Task Attachments_are_encrypted_locally_uploaded_in_resumable_chunks_and_downloaded_elsewhere()
    {
        await using var a = await ReceptionAsync();
        var p = await RegisterAsync(a, "File Owner", "0307-5550001");
        var bytes = RandomNumberGenerator.GetBytes(2_500_000); // 3 chunks

        var file = await a.Attachments.AddAsync(SyncEntityNames.Patient, p.Id, p.BranchId, new MemoryStream(bytes),
            "old-report.pdf", "application/pdf", "Old lab report", "report");
        var stored = await File.ReadAllBytesAsync(file.LocalPath!);
        Assert.NotEqual(bytes.Take(64), stored.Skip(28).Take(64)); // not plain text on disk

        await a.Engine.SyncAsync(); // metadata first
        // Simulate an interrupted upload: only chunk 0 made it.
        await a.Files.PutChunkAsync(file.Id, 0, bytes.AsMemory(0, 1024 * 1024), default);
        var status = await a.Files.StatusAsync(file.Id, default);
        Assert.Equal([0], status.ReceivedChunks);

        Assert.Equal(1, await a.Attachments.UploadPendingAsync(a.Files));
        Assert.Equal(0, await a.Attachments.UploadPendingAsync(a.Files)); // nothing left
        await a.Engine.SyncAsync();
        Assert.True((await a.Store.GetAsync<LocalAttachment>(file.Id))!.IsUploaded);

        await using var b = await ReceptionAsync();
        var remote = (await b.Store.GetAsync<LocalAttachment>(file.Id))!;
        Assert.Equal(UploadState.Remote, remote.UploadState);
        Assert.Null(remote.LocalPath);
        var downloaded = await b.Attachments.ReadAsync(remote, b.Files);
        Assert.Equal(bytes, downloaded);
        Assert.Equal(bytes, await b.Attachments.ReadAsync((await b.Store.GetAsync<LocalAttachment>(file.Id))!, null)); // now cached

        Assert.Contains(await b.Patients.TimelineAsync(p.Id), t => t.Kind == "file" && t.Title == "Old lab report");
    }

    [Fact]
    public async Task Server_rejects_patients_from_roles_that_may_not_register_and_bad_attachments()
    {
        var nurseName = await factory.CreateUserAsync("Nurse / Assistant");
        await using var nurse = await ClientDevice.CreateAsync(await factory.DeviceAsync("demo", nurseName, SyncTestExtensions.TestPassword));
        await nurse.Engine.SyncAsync();
        await RegisterAsync(nurse, "Not Allowed", "0308-6660001");
        var state = await nurse.Engine.SyncAsync();
        Assert.Equal(1, state.Rejected);
        Assert.Contains("Patients.Create", (await nurse.Center.ListAsync()).Single().LastError);

        await using var r = await ReceptionAsync();
        var p = await RegisterAsync(r, "Has Files", "0308-6660002");
        await r.Attachments.AddAsync(SyncEntityNames.Patient, p.Id, p.BranchId, new MemoryStream([1, 2, 3]), "virus.exe", "application/x-msdownload", null, "document");
        Assert.Equal(1, (await r.Engine.SyncAsync()).Rejected);
    }

    [Fact]
    public async Task Admin_manages_doctors_schedules_and_types_with_validation()
    {
        using var admin = await factory.DemoAdminAsync();
        var bad = await admin.PostAsJsonAsync(SchedulingRoutes.Doctors, new SaveDoctorRequest("Dr X", "astrology", null, null, null, "permanent", null, 10, true, null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var doctor = await CreateDoctorAsync("dental");
        var overlap = await admin.PostAsJsonAsync($"{SchedulingRoutes.Doctors}/{doctor.Id}/schedules",
            new SaveScheduleRequest(doctor.Id == Guid.Empty ? Guid.Empty : (await admin.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches))!.First().Id,
                1, TimeSpan.FromHours(10), TimeSpan.FromHours(12)));
        Assert.Equal(HttpStatusCode.BadRequest, overlap.StatusCode);

        var expiring = await (await admin.PutAsJsonAsync($"{SchedulingRoutes.Doctors}/{doctor.Id}", new SaveDoctorRequest(
            doctor.FullName, "dental", "F", "PMDC-1", DateTime.Today.AddDays(10), DoctorTypes.Visiting, 2000, 15, true, null))).ReadAsync<DoctorDto>();
        Assert.Equal(LicenceStatus.Expiring, expiring.LicenceStatus);

        var types = await admin.GetFromJsonAsync<List<AppointmentTypeDto>>(SchedulingRoutes.AppointmentTypes);
        Assert.Contains(types!, t => t.Code == "walk_in" && t.IsWalkIn);
        var dup = await admin.PostAsJsonAsync(SchedulingRoutes.AppointmentTypes, new SaveAppointmentTypeRequest("consultation", "Again", 15, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);

        var receptionist = await factory.ClientForAsync("demo", await factory.CreateUserAsync("Receptionist"), SyncTestExtensions.TestPassword);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await receptionist.PostAsJsonAsync(SchedulingRoutes.AppointmentTypes, new SaveAppointmentTypeRequest("x1", "X", 15, null, null, null))).StatusCode);
    }

    [Fact]
    public void Shared_rules_for_names_phones_and_slots()
    {
        Assert.Equal("03001234567", PatientRules.NormalizePhone("+92 300 1234567"));
        Assert.True(PatientRules.SimilarNames("Muhammad Ali", "Mohammad Ali"));
        Assert.True(PatientRules.SimilarNames("Khan Ayesha", "Ayesha Khan"));
        Assert.False(PatientRules.SimilarNames("Ayesha Khan", "Bilal Ahmed"));
        Assert.Equal("MAIN-D07-00042", PatientRules.Mrn("MAIN", "D07", 42));

        var doctor = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var monday = new DateTime(2026, 12, 7);
        var block = new ScheduleBlock(doctor, branch, 1, TimeSpan.FromHours(9), TimeSpan.FromHours(10), 20, 1, 10);
        var slots = SlotCalculator.SlotsFor(doctor, branch, monday, [block], [], [], [], new DateTime(2026, 12, 1));
        Assert.Equal([TimeSpan.FromHours(9), new TimeSpan(9, 30, 0)], slots.Select(s => s.Start)); // 20 min + 10 buffer
        Assert.Empty(SlotCalculator.SlotsFor(doctor, branch, monday, [block], [], [], [], new DateTime(2026, 12, 7, 9, 45, 0))); // past
    }
}

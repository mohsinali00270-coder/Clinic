using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Clinic.Application.Sync;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Client.Core.Sync;
using Clinic.Infrastructure.Persistence;
using Clinic.Shared.Contracts;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Api.Tests;

public sealed class HardeningFixture : IAsyncLifetime
{
    public ClinicApiFactory Factory { get; } = new();

    public async Task InitializeAsync()
    {
        using var admin = await Factory.DemoAdminAsync();
        foreach (var (template, context) in new[] { ("patient_standard", (string?)null), ("opd_general", "general") })
        {
            var t = (await admin.GetFromJsonAsync<List<FormTemplateDto>>(FormRoutes.FormTemplates))!.Single(x => x.Key == template);
            var form = await (await admin.PostAsJsonAsync(FormRoutes.Forms, new CreateFormRequest(t.Name, t.EntityType, null, context, template))).ReadAsync<FormDto>();
            await (await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null)).EnsureSuccessAsync();
        }
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>Phase 8: conflicts, patient merge, snapshot, retention, audit.</summary>
public sealed class HardeningTests(HardeningFixture fixture) : IClassFixture<HardeningFixture>
{
    private ClinicApiFactory Factory => fixture.Factory;

    private async Task<ClientDevice> DeviceForAsync(string userName, SyncEngineOptions? options = null)
    {
        var d = await ClientDevice.CreateAsync(await Factory.DeviceAsync("demo", userName, SyncTestExtensions.TestPassword), options);
        await d.Engine.SyncAsync();
        return d;
    }

    private async Task<(DoctorDto Doctor, string UserName)> DoctorAsync()
    {
        var userName = await Factory.CreateUserAsync("Doctor");
        using var admin = await Factory.DemoAdminAsync();
        var user = (await admin.GetFromJsonAsync<PagedResult<UserDto>>($"{ApiRoutes.Users}?search={userName}"))!.Items.Single();
        var main = (await admin.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches))!.Single(b => b.Code == "MAIN");
        var doctor = await (await admin.PostAsJsonAsync(SchedulingRoutes.Doctors, new SaveDoctorRequest(
            $"Dr {userName}", "general", "M", "PMDC-31", DateTime.Today.AddYears(1), DoctorTypes.Permanent, 1000, 10, true, user.Id))).ReadAsync<DoctorDto>();
        await (await admin.PostAsJsonAsync($"{SchedulingRoutes.Doctors}/{doctor.Id}/schedules",
            new SaveScheduleRequest(main.Id, (int)DateTime.Today.DayOfWeek, TimeSpan.Zero, new TimeSpan(23, 59, 0)))).EnsureSuccessAsync();
        return (doctor, userName);
    }

    private static async Task<LocalPatient> RegisterAsync(ClientDevice d, string name, string phone)
    {
        var forms = new DynamicDataService(d.Db);
        var version = (await forms.ResolveFormAsync(SyncEntityNames.Patient, d.Session.BranchId))!;
        var model = new DynamicFormModel(version, [], new EvaluationContext { Variables = new Dictionary<string, object?> { ["gender"] = "M", ["ageYears"] = 40d } });
        model.SetValue("allergies", new JsonArray(JsonValue.Create("none")));
        return await d.Patients.RegisterAsync(new PatientInput(name, "M", new DateTime(1986, 5, 5), false, phone, d.Session.BranchId), version, model.ValuesToSave());
    }

    private static async Task SyncCleanAsync(ClientDevice d)
    {
        var r = await d.Engine.SyncAsync(manual: true);
        Assert.True(r.Outstanding == 0, string.Join(" | ", (await d.Center.ListAsync()).Select(x => $"{x.Summary}: {x.Status} {x.LastError}")));
        Assert.True(r.Phase == SyncPhase.Idle, $"Sync ended in {r.Phase}: {r.Message}");
    }

    // ---------------------------------------------------------------------
    // FR-SYN-30/31 conflicts
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_conflict_is_recorded_on_the_server_resolved_field_by_field_and_audited()
    {
        var (doctor, userName) = await DoctorAsync();
        await using var reception = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var patient = await RegisterAsync(reception, $"Conflict {Guid.NewGuid():N}"[..18], "0300-5550001");
        var walkIn = await reception.Scheduling.WalkInAsync(patient.Id, doctor.Id, reception.Session.BranchId);
        await SyncCleanAsync(reception);

        await using var a = await DeviceForAsync(userName);
        await using var b = await DeviceForAsync(userName);
        var encounters = new EncounterService(a.Store, new DynamicDataService(a.Db));
        var e = await encounters.StartFromAppointmentAsync(walkIn.Id);
        var dx = await encounters.AddDiagnosisAsync(e.Id, null, "Viral illness", true);
        await SyncCleanAsync(a);
        await b.Engine.SyncAsync();

        a.Transport.Offline = b.Transport.Offline = true;
        var onA = (await a.Store.GetAsync<LocalDiagnosis>(dx.Id))!;
        onA.Description = "Dengue fever";
        onA.Notes = "NS1 positive";
        await a.Store.UpdateAsync(onA);
        var onB = (await b.Store.GetAsync<LocalDiagnosis>(dx.Id))!;
        onB.Description = "Typhoid fever";
        onB.Notes = "Widal pending";
        await b.Store.UpdateAsync(onB);
        a.Transport.Offline = b.Transport.Offline = false;
        await SyncCleanAsync(a);
        Assert.Equal(1, (await b.Engine.SyncAsync(manual: true)).Conflicts);

        // The server keeps the conflict with both sides.
        using var admin = await Factory.DemoAdminAsync();
        var open = (await admin.GetFromJsonAsync<List<ConflictDto>>($"{SyncRoutes.Conflicts}?status=open"))!.Single(c => c.RecordId == dx.Id);
        Assert.Equal(["description", "notes"], open.Fields.Order());
        Assert.Equal("Typhoid fever", open.Mine!["description"]!.GetValue<string>());
        Assert.Equal("Dengue fever", open.Theirs!["description"]!.GetValue<string>());

        // FR-SYN-30: Mine / Theirs per field; the doctor takes theirs for one field and writes a merged note.
        var entry = (await b.Center.ListAsync()).Single();
        Assert.Equal(open.Id, entry.ConflictId);
        var fields = await b.Center.ConflictFieldsAsync(entry.Seq);
        Assert.All(fields, f => Assert.True(f.Differs));
        await b.Center.ResolveAsync(entry.Seq, new Dictionary<string, JsonNode?>
        {
            ["description"] = fields.Single(f => f.Field == "description").Theirs,
            ["notes"] = JsonValue.Create("NS1 positive; Widal pending"),
        });
        Assert.Equal("Dengue fever", (await b.Store.GetAsync<LocalDiagnosis>(dx.Id))!.Description);
        await SyncCleanAsync(b);
        await a.Engine.SyncAsync();
        var merged = (await a.Store.GetAsync<LocalDiagnosis>(dx.Id))!;
        Assert.Equal("Dengue fever", merged.Description);
        Assert.Equal("NS1 positive; Widal pending", merged.Notes);

        // FR-SYN-31: closed, and audited with both values.
        var closed = (await admin.GetFromJsonAsync<List<ConflictDto>>(SyncRoutes.Conflicts))!.Single(c => c.Id == open.Id);
        Assert.Equal("resolved", closed.Status);
        Assert.Equal(ConflictResolution.Merged, closed.Resolution);
        var audit = (await admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"{HardeningRoutes.Audit}?action=ConflictResolved&entityId={dx.Id}"))!;
        var row = Assert.Single(audit.Items);
        Assert.Contains("Typhoid", row.OldValues);
        Assert.Contains("Dengue", row.OldValues);
        Assert.Contains("Widal pending", row.NewValues);
    }

    // ---------------------------------------------------------------------
    // FR-SYN-32 patient merge
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Duplicates_registered_offline_are_suggested_merged_and_late_pushes_follow_the_survivor()
    {
        var (doctor, _) = await DoctorAsync();
        await using var a = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        await using var b = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        await using var late = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var phone = $"0321-{Random.Shared.Next(1000000, 9999999)}";
        a.Transport.Offline = b.Transport.Offline = true;
        var first = await RegisterAsync(a, "Muhammad Imran Khan", phone);
        var second = await RegisterAsync(b, "Mohammad Imran Khan", phone);
        await a.Scheduling.WalkInAsync(first.Id, doctor.Id, a.Session.BranchId);
        var bVisit = await b.Scheduling.WalkInAsync(second.Id, doctor.Id, b.Session.BranchId);
        a.Transport.Offline = b.Transport.Offline = false;
        await SyncCleanAsync(a);
        await SyncCleanAsync(b);
        await late.Engine.SyncAsync();
        late.Transport.Offline = true; // this desk will push work on the duplicate after the merge

        using var admin = await Factory.DemoAdminAsync();
        var pair = (await admin.GetFromJsonAsync<List<DuplicatePairDto>>(HardeningRoutes.Duplicates))!
            .Single(p => new[] { p.A.Id, p.B.Id }.ToHashSet().SetEquals([first.Id, second.Id]));
        Assert.Contains("Same phone number", pair.Reasons);
        Assert.Contains("Similar name", pair.Reasons);

        var lateVisit = await late.Scheduling.WalkInAsync(second.Id, doctor.Id, late.Session.BranchId, notes: "Booked before the merge");

        var result = await (await admin.PostAsJsonAsync(HardeningRoutes.Merge, new MergePatientsRequest(first.Id, second.Id, "Same person, registered twice"))).ReadAsync<MergeResultDto>();
        Assert.Equal(1, result.Moved[SyncEntityNames.Appointment]);

        // Devices: the duplicate disappears and its visit belongs to the survivor.
        await a.Engine.SyncAsync();
        Assert.Null(await a.Store.GetAsync<LocalPatient>(second.Id));
        Assert.Equal(first.Id, (await a.Store.GetAsync<LocalAppointment>(bVisit.Id))!.PatientId);
        Assert.DoesNotContain(await a.Patients.SearchAsync("Mohammad Imran"), p => p.Id == second.Id);

        // A push made before the merge is pulled lands on the survivor.
        late.Transport.Offline = false;
        await SyncCleanAsync(late);
        Assert.Equal(first.Id, (await late.Store.GetAsync<LocalAppointment>(lateVisit.Id))!.PatientId);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
            var serverVisit = await db.Appointments.IgnoreQueryFilters().SingleAsync(x => x.Id == lateVisit.Id);
            Assert.Equal(first.Id, serverVisit.PatientId);
        }

        // The merge is in the audit log.
        var audit = (await admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"{HardeningRoutes.Audit}?action=PatientMerged&entityId={first.Id}"))!;
        Assert.Contains("Same person", Assert.Single(audit.Items).NewValues);
    }

    // ---------------------------------------------------------------------
    // FR-SYN-23/24 snapshot and retention
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_new_device_starts_from_one_snapshot_and_holds_the_same_data_as_paging()
    {
        var userName = await Factory.CreateUserAsync("Receptionist");
        await using var seed = await DeviceForAsync(userName);
        for (var i = 0; i < 30; i++) await seed.AddNoteAsync($"Snapshot note {i}");
        await SyncCleanAsync(seed);

        await using var snap = await ClientDevice.CreateAsync(await Factory.DeviceAsync("demo", userName, SyncTestExtensions.TestPassword),
            new SyncEngineOptions { LeaseSize = 50, PullPageSize = 50 });
        await using var paged = await ClientDevice.CreateAsync(await Factory.DeviceAsync("demo", userName, SyncTestExtensions.TestPassword),
            new SyncEngineOptions { LeaseSize = 50, PullPageSize = 50, UseSnapshot = false });
        await SyncCleanAsync(snap);
        await SyncCleanAsync(paged);

        Assert.Equal(1, snap.Transport.SnapshotCalls);
        Assert.True(snap.Transport.PullCalls <= 2, $"Expected the snapshot to replace paging, but pulled {snap.Transport.PullCalls} pages.");
        Assert.True(paged.Transport.PullCalls > 2);
        foreach (var table in new[] { "BranchNotes", "Patients", "LookupItems", "FormVersions", "Drugs", "Icd10Codes", "FieldValues" })
        {
            var x = await snap.Db.Connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM \"{table}\" WHERE IsDeleted = 0");
            var y = await paged.Db.Connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM \"{table}\" WHERE IsDeleted = 0");
            Assert.True(x == y, $"{table}: snapshot {x}, paged {y}");
        }
        Assert.Equal(30, (await snap.NotesAsync()).Count(n => n.Title.StartsWith("Snapshot note")));
    }

    [Fact]
    public async Task Retention_compacts_the_change_log_and_a_long_offline_device_resets_after_pushing()
    {
        var userName = await Factory.CreateUserAsync("Receptionist");
        await using var d = await DeviceForAsync(userName);
        var keep = await d.AddNoteAsync("Kept, edited twice");
        var gone = await d.AddNoteAsync("Deleted later");
        await SyncCleanAsync(d);

        await using var other = await DeviceForAsync(userName);
        var k = (await other.Store.GetAsync<LocalBranchNote>(keep.Id))!;
        k.Body = "edit 1";
        await other.Store.UpdateAsync(k);
        k.Body = "edit 2";
        await other.Store.UpdateAsync(k);
        await other.Store.DeleteAsync<LocalBranchNote>(gone.Id);
        await SyncCleanAsync(other);

        // d was offline all along and wrote something meanwhile.
        d.Transport.Offline = true;
        var pending = await d.AddNoteAsync("Written while away");

        // 100 days pass on the server: compact.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
            await db.ChangeLogs.IgnoreQueryFilters().Where(c => c.RecordId == keep.Id || c.RecordId == gone.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.At, DateTime.UtcNow.AddDays(-100)));
            var results = await scope.ServiceProvider.GetRequiredService<ChangeLogRetention>().RunAsync(90);
            var mine = results.Single(r => r.TenantId == d.Session.TenantId);
            Assert.True(mine.Superseded >= 3, $"superseded {mine.Superseded}");
            Assert.True(mine.Tombstones >= 1);
            Assert.True(mine.PurgedThrough > 0);
            Assert.Equal(1, await db.ChangeLogs.IgnoreQueryFilters().CountAsync(c => c.RecordId == keep.Id)); // only the newest row
        }

        // Back online: the outbox is pushed first, then the device is told to reset and re-downloads.
        d.Transport.Offline = false;
        await SyncCleanAsync(d);
        Assert.True(d.Transport.SnapshotCalls >= 1);
        Assert.Equal("edit 2", (await d.Store.GetAsync<LocalBranchNote>(keep.Id))!.Body);
        Assert.Null(await d.Store.GetAsync<LocalBranchNote>(gone.Id)); // the deletion it would otherwise have missed
        Assert.NotNull(await d.Store.GetAsync<LocalBranchNote>(pending.Id));
        await other.Engine.SyncAsync();
        Assert.NotNull(await other.Store.GetAsync<LocalBranchNote>(pending.Id));
    }

    // ---------------------------------------------------------------------
    // FR-SEC-07 audit viewer, read-access log, client error reports
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Record_opens_are_audited_and_app_errors_are_collected()
    {
        await using var d = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var p = await RegisterAsync(d, $"Audited {Guid.NewGuid():N}"[..16], "0300-1112223");
        d.Transport.Offline = true;
        await d.Patients.RecordOpenAsync(p.Id, d.Session.BranchId);
        d.Transport.Offline = false;
        await SyncCleanAsync(d);

        using var admin = await Factory.DemoAdminAsync();
        var reads = (await admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"{HardeningRoutes.Audit}?action=Read&entityId={p.Id}"))!;
        var read = Assert.Single(reads.Items);
        Assert.Equal(d.Session.Token.Me.UserId, read.UserId);
        Assert.NotNull(read.UserName);
        var writes = (await admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"{HardeningRoutes.Audit}?entityType=Patient&entityId={p.Id}"))!;
        Assert.True(writes.TotalCount >= 2);

        var report = await d.Session.Http.PostAsJsonAsync(HardeningRoutes.ClientErrors,
            new ClientErrorReport("sync", "Push failed: timeout", "at SyncEngine.PushAsync", "1.0.0", "Android 14", DateTimeOffset.UtcNow, d.Session.DeviceId));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, report.StatusCode);

        // Only auditors read the log.
        var denied = await d.Session.Http.GetAsync(HardeningRoutes.Audit);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
    }
}

/// <summary>§11 targets and the §12 global acceptance list, measured in-process (SQLite server, real device engine).</summary>
/// <summary>Timing tests run on their own, after the rest, so the numbers measure the code and not 120 parallel tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NfrCollection : ICollectionFixture<HardeningFixture>
{
    public const string Name = "NFR timings";
}

[Collection(NfrCollection.Name)]
public sealed class NfrTests(HardeningFixture fixture)
{
    private ClinicApiFactory Factory => fixture.Factory;

    private async Task<ClientDevice> DeviceAsync(SyncEngineOptions? options = null)
    {
        var user = await Factory.CreateUserAsync("Receptionist");
        return await ClientDevice.CreateAsync(await Factory.DeviceAsync("demo", user, SyncTestExtensions.TestPassword), options);
    }

    /// <summary>"Patient search over 100,000 local records &lt; 300 ms."</summary>
    [Fact]
    public async Task Patient_search_over_100000_local_records_is_under_300_ms()
    {
        await using var d = await DeviceAsync();
        await d.Engine.SyncAsync();
        string[] first = ["Muhammad", "Ahmed", "Ali", "Fatima", "Ayesha", "Hassan", "Zainab", "Bilal", "Sana", "Usman"];
        string[] last = ["Khan", "Malik", "Qureshi", "Sheikh", "Butt", "Chaudhry", "Raza", "Siddiqui", "Mirza", "Awan"];
        for (var chunk = 0; chunk < 20; chunk++)
        {
            var start = chunk * 5_000;
            await d.Db.Connection.RunInTransactionAsync(c =>
            {
                var batch = Enumerable.Range(start, 5_000).Select(i => new LocalPatient
                {
                    Id = Guid.NewGuid(), TenantId = d.Session.TenantId, Mrn = $"MAIN-T{i:D6}", FullName = $"{first[i % 10]} {last[i / 10 % 10]} {i}",
                    Gender = i % 2 == 0 ? "M" : "F", Phone = $"03{i:D9}", BranchId = d.Session.BranchId, CreatedAt = DateTime.UtcNow, SyncStatus = SyncStatus.Synced,
                }).ToList();
                c.InsertAll(batch, runInTransaction: false);
                PatientSearchIndex.IndexNew(c, batch);
            });
        }

        await d.Patients.SearchAsync("warm up");
        foreach (var query in new[] { "Fatima Raza 4", "03000012345", "MAIN-T055555", "khan ali" })
        {
            var sw = Stopwatch.StartNew();
            var hits = await d.Patients.SearchAsync(query);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 300, $"'{query}' took {sw.ElapsedMilliseconds} ms");
            Assert.NotEmpty(hits);
        }
    }

    /// <summary>"Sync push of 200 changes &lt; 3 s; pull page of 500 rows &lt; 2 s."</summary>
    [Fact]
    public async Task Push_of_200_changes_and_pull_of_500_rows_meet_the_api_targets()
    {
        await using var d = await DeviceAsync(new SyncEngineOptions { LeaseSize = 50, UseSnapshot = false });
        await d.Engine.SyncAsync();
        // Warm-up: the first push JIT-compiles the pipeline and EF model, which a running server has long done.
        await d.Store.WriteAsync(b => b.Insert(new LocalBranchNote { BranchId = d.Session.BranchId, Title = "Warm up", Body = "x" }));
        await d.Engine.SyncAsync();
        await d.Store.WriteAsync(b =>
        {
            for (var i = 0; i < 200; i++) b.Insert(new LocalBranchNote { BranchId = d.Session.BranchId, Title = $"Load {i}", Body = new string('x', 300) });
        });
        var entries = await d.Center.ListAsync();
        var request = new SyncPushRequest(d.Session.DeviceId, "1.0.0", entries.Select(e => new SyncChange(e.ChangeId, e.EntityType, e.RecordId,
            (SyncOperation)e.Operation, e.BaseRowVersion, LocalJson.Fields(e.ChangedFields), LocalJson.Parse(e.Payload), DateTimeOffset.UtcNow)).ToList());

        var sw = Stopwatch.StartNew();
        var pushed = await d.Session.Transport.PushAsync(request, default);
        sw.Stop();
        Assert.All(pushed.Results, r => Assert.Equal(SyncChangeStatus.Applied, r.Status));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"push of 200 took {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var page = await d.Session.Transport.PullAsync(0, 500, default);
        sw.Stop();
        Assert.Equal(500, page.Entities.Sum(e => e.Value.Count) + page.Tombstones.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"pull of 500 took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>"Form with 150 dynamic fields renders &lt; 1.5 s": the model (evaluation of every rule) is well inside that.</summary>
    [Fact]
    public void A_150_field_form_evaluates_well_inside_the_render_budget()
    {
        var fields = new List<FieldDefinition>();
        for (var i = 0; i < 140; i++)
            fields.Add(new FieldDefinition
            {
                Key = $"f{i}", Type = i % 3 == 0 ? FieldType.Decimal : FieldType.Text, Label = new($"Field {i}"), Required = i % 10 == 0,
                VisibleWhen = i > 0 && i % 7 == 0 ? $"!isEmpty(f{i - 1})" : null, Min = i % 3 == 0 ? 0 : null, Max = i % 3 == 0 ? 1000 : null,
                Abnormal = i % 3 == 0 ? [new AbnormalRule { When = "value > 500", Severity = "warning" }] : null,
            });
        for (var i = 0; i < 10; i++)
            fields.Add(new FieldDefinition { Key = $"c{i}", Type = FieldType.Calculated, Label = new($"Calc {i}"), Formula = $"f{i * 3} * 2 + f{i * 3 + 3}" });
        var definition = new FormDefinition { EntityType = "Encounter", Name = new("Big form"), Sections = [new SectionDefinition { Key = "s", Title = new("S"), Fields = fields }] };
        Assert.Empty(FormDefinitionValidator.Validate(definition));

        var values = new JsonObject();
        for (var i = 0; i < 140; i++) values[$"f{i}"] = i % 3 == 0 ? JsonValue.Create(i * 5m) : JsonValue.Create($"text {i}");
        var evaluator = new FormEvaluator(definition);
        evaluator.Evaluate(values, new EvaluationContext()); // compile
        var sw = Stopwatch.StartNew();
        FormEvaluation? result = null;
        for (var n = 0; n < 10; n++) result = evaluator.Evaluate(values, new EvaluationContext()); // ten keystrokes
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds / 10.0 < 50, $"one evaluation of 150 fields took {sw.ElapsedMilliseconds / 10.0} ms");
        Assert.Equal(150, result!.Fields.Count);
    }

    /// <summary>"Kill the app mid-sync 20 times: no corrupt state, sync resumes from checkpoint" + "zero lost or duplicated records".</summary>
    [Fact]
    public async Task Interrupting_sync_20_times_loses_and_duplicates_nothing()
    {
        await using var d = await DeviceAsync(new SyncEngineOptions { LeaseSize = 50, PushBatchSize = 7, PullPageSize = 9, UseSnapshot = false });
        await d.Engine.SyncAsync();
        await using var other = await DeviceAsync(new SyncEngineOptions { LeaseSize = 50, PullPageSize = 11, UseSnapshot = false });
        await other.Engine.SyncAsync();
        var ids = new List<Guid>();

        for (var round = 0; round < 20; round++)
        {
            for (var i = 0; i < 5; i++) ids.Add((await d.AddNoteAsync($"Round {round} note {i}")).Id);
            await other.AddNoteAsync($"Other {round}");
            // Alternate between a response lost after the server applied the push, and a connection dropped mid-pull.
            if (round % 2 == 0) d.Transport.LoseNextPushResponse = true;
            else d.Transport.FailPullCall = d.Transport.PullCalls + 2;
            await other.Engine.SyncAsync(manual: true);
            await d.Engine.SyncAsync(manual: true); // interrupted
        }
        d.Transport.FailPullCall = 0;
        Assert.Equal(0, (await d.Engine.SyncAsync(manual: true)).Outstanding);
        Assert.Equal(0, (await d.Engine.SyncAsync(manual: true)).Outstanding);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var server = await db.BranchNotes.IgnoreQueryFilters().Where(n => ids.Contains(n.Id)).CountAsync();
        Assert.Equal(100, server);                                   // nothing lost
        Assert.Equal(100, await db.SyncLogs.IgnoreQueryFilters().CountAsync(l => ids.Contains(l.RecordId))); // each applied once
        var local = await d.NotesAsync();
        Assert.Equal(local.Count, local.Select(n => n.Id).Distinct().Count());
        Assert.Equal(20, local.Count(n => n.Title.StartsWith("Other ")));  // every pull resumed from its checkpoint
    }

    /// <summary>"7 days fully offline with no data loss; outbox up to 50,000 changes."</summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task A_week_offline_with_50000_queued_changes_syncs_without_loss()
    {
        await using var d = await DeviceAsync(new SyncEngineOptions { LeaseSize = 50, UseSnapshot = false });
        await d.Engine.SyncAsync();
        // Warm-up: the first push JIT-compiles the pipeline and EF model, which a running server has long done.
        await d.Store.WriteAsync(b => b.Insert(new LocalBranchNote { BranchId = d.Session.BranchId, Title = "Warm up", Body = "x" }));
        await d.Engine.SyncAsync();
        d.Transport.Offline = true;
        // A busy week: 50,000 changes — 25,000 notes created and each edited once.
        var notes = new List<LocalBranchNote>();
        for (var day = 0; day < 7; day++)
        {
            await d.Store.WriteAsync(b =>
            {
                for (var i = 0; i < 25_000 / 7 + (day < 25_000 % 7 ? 1 : 0); i++)
                    notes.Add(b.Insert(new LocalBranchNote { BranchId = d.Session.BranchId, Title = $"Day {day} #{i}" }));
            });
        }
        await d.Store.WriteAsync(b =>
        {
            foreach (var n in notes) { n.Body = "updated"; b.Update(n); }
        });
        Assert.Equal(50_000, await d.Center.CountAsync());

        d.Transport.Offline = false;
        var sw = Stopwatch.StartNew();
        var state = await d.Engine.SyncAsync(manual: true);
        sw.Stop();
        Assert.Equal(0, state.Outstanding);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var ids = notes.Select(n => n.Id).ToHashSet();
        var tenant = d.Session.TenantId;
        var onServer = await db.BranchNotes.IgnoreQueryFilters().Where(n => n.TenantId == tenant && n.Title.StartsWith("Day ") && n.Body == "updated").Select(n => n.Id).ToListAsync();
        Assert.Equal(25_000, onServer.Count(ids.Contains));
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(10), $"took {sw.Elapsed}");
    }
}

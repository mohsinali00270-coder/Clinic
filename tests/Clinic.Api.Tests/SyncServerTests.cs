using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Clinic.Client.Core.Sync;
using Clinic.Infrastructure.Persistence;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Api.Tests;

/// <summary>§7 server behaviour, driven through the same HTTP transport the app uses.</summary>
public sealed class SyncServerTests(ClinicApiFactory factory) : IClassFixture<ClinicApiFactory>
{
    private static SyncChange Insert(Guid branchId, string title, Guid? id = null, string? body = null) => new(
        Guid.NewGuid(), SyncEntityNames.BranchNote, id ?? Guid.NewGuid(), SyncOperation.Insert, null,
        ["branchId", "title", "body"],
        new JsonObject { ["branchId"] = branchId, ["title"] = title, ["body"] = body },
        DateTimeOffset.UtcNow);

    private static SyncChange Update(Guid id, long baseVersion, JsonObject data) => new(
        Guid.NewGuid(), SyncEntityNames.BranchNote, id, SyncOperation.Update, baseVersion,
        data.Select(kv => kv.Key).ToList(), data, DateTimeOffset.UtcNow);

    private static SyncChange Delete(Guid id, long baseVersion) => new(
        Guid.NewGuid(), SyncEntityNames.BranchNote, id, SyncOperation.Delete, baseVersion, [], null, DateTimeOffset.UtcNow);

    private static async Task<SyncChangeResult> PushOne(DeviceSession d, SyncChange change)
    {
        var response = await d.Transport.PushAsync(new SyncPushRequest(d.DeviceId, "1.0.0", [change]), default);
        return response.Results.Single();
    }

    private static async Task<(List<JsonObject> Notes, List<SyncTombstone> Tombstones, List<JsonObject> Branches)> PullAll(DeviceSession d, int pageSize = 500)
    {
        var notes = new Dictionary<Guid, JsonObject>();
        var branches = new List<JsonObject>();
        var tombstones = new List<SyncTombstone>();
        long since = 0;
        while (true)
        {
            var page = await d.Transport.PullAsync(since, pageSize, default);
            foreach (var n in page.Entities.GetValueOrDefault(SyncEntityNames.BranchNote) ?? []) notes[n["id"]!.GetValue<Guid>()] = n;
            branches.AddRange(page.Entities.GetValueOrDefault(SyncEntityNames.Branch) ?? []);
            tombstones.AddRange(page.Tombstones);
            Assert.True(page.Checkpoint >= since);
            since = page.Checkpoint;
            if (!page.HasMore) break;
        }
        return (notes.Values.ToList(), tombstones, branches);
    }

    [Fact]
    public async Task Insert_is_applied_once_even_when_the_same_batch_is_resent()
    {
        var d = await factory.DemoAdminDeviceAsync();
        var change = Insert(d.BranchId, "Fridge in room 2 is broken");

        var first = await PushOne(d, change);
        var again = await PushOne(d, change);

        Assert.Equal(SyncChangeStatus.Applied, first.Status);
        Assert.Equal(SyncChangeStatus.Duplicate, again.Status);
        Assert.Equal(first.RowVersion, again.RowVersion);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        Assert.Equal(1, await db.BranchNotes.IgnoreQueryFilters().CountAsync(n => n.Id == change.RecordId));
        Assert.Equal(1, await db.ChangeLogs.IgnoreQueryFilters().CountAsync(c => c.RecordId == change.RecordId));
        Assert.Equal(1, await db.SyncLogs.CountAsync(l => l.ChangeId == change.ChangeId));
    }

    [Fact]
    public async Task Edits_to_different_fields_from_two_devices_merge()
    {
        var a = await factory.DemoAdminDeviceAsync();
        var b = await factory.DemoAdminDeviceAsync();
        var insert = Insert(a.BranchId, "Original title", body: "Original body");
        var v1 = (await PushOne(a, insert)).RowVersion!.Value;

        var ra = await PushOne(a, Update(insert.RecordId, v1, new JsonObject { ["title"] = "Title from A" }));
        var rb = await PushOne(b, Update(insert.RecordId, v1, new JsonObject { ["body"] = "Body from B" }));

        Assert.Equal(SyncChangeStatus.Applied, ra.Status);
        Assert.Equal(SyncChangeStatus.Applied, rb.Status);
        Assert.Null(rb.Note);

        var note = (await PullAll(b)).Notes.Single(n => n["id"]!.GetValue<Guid>() == insert.RecordId);
        Assert.Equal("Title from A", note["title"]!.GetValue<string>());
        Assert.Equal("Body from B", note["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task Same_field_from_two_devices_is_last_writer_wins_with_a_note()
    {
        var a = await factory.DemoAdminDeviceAsync();
        var b = await factory.DemoAdminDeviceAsync();
        var insert = Insert(a.BranchId, "Shared");
        var v1 = (await PushOne(a, insert)).RowVersion!.Value;

        await PushOne(a, Update(insert.RecordId, v1, new JsonObject { ["title"] = "A wrote this" }));
        var rb = await PushOne(b, Update(insert.RecordId, v1, new JsonObject { ["title"] = "B wrote this" }));

        Assert.Equal(SyncChangeStatus.Applied, rb.Status);
        Assert.Contains("title", rb.Note);
        var note = (await PullAll(a)).Notes.Single(n => n["id"]!.GetValue<Guid>() == insert.RecordId);
        Assert.Equal("B wrote this", note["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Same_device_sending_two_edits_of_one_field_from_the_same_base_is_not_a_conflict()
    {
        var a = await factory.DemoAdminDeviceAsync();
        var insert = Insert(a.BranchId, "v0");
        var v1 = (await PushOne(a, insert)).RowVersion!.Value;

        await PushOne(a, Update(insert.RecordId, v1, new JsonObject { ["title"] = "v1" }));
        var second = await PushOne(a, Update(insert.RecordId, v1, new JsonObject { ["title"] = "v2" }));
        Assert.Equal(SyncChangeStatus.Applied, second.Status);
        Assert.Null(second.Note);
    }

    [Fact]
    public async Task Delete_loses_to_a_newer_edit_from_another_device()
    {
        var a = await factory.DemoAdminDeviceAsync();
        var b = await factory.DemoAdminDeviceAsync();
        var insert = Insert(a.BranchId, "Keep me");
        var v1 = (await PushOne(a, insert)).RowVersion!.Value;

        await PushOne(a, Update(insert.RecordId, v1, new JsonObject { ["title"] = "Edited" }));
        var delete = await PushOne(b, Delete(insert.RecordId, v1));

        Assert.Equal(SyncChangeStatus.Applied, delete.Status);
        Assert.NotNull(delete.ServerData);
        Assert.False(delete.ServerData!["isDeleted"]!.GetValue<bool>());
        Assert.DoesNotContain((await PullAll(a)).Tombstones, t => t.RecordId == insert.RecordId);
    }

    [Fact]
    public async Task Edit_restores_a_record_deleted_elsewhere()
    {
        var a = await factory.DemoAdminDeviceAsync();
        var b = await factory.DemoAdminDeviceAsync();
        var insert = Insert(a.BranchId, "Soon deleted");
        var v1 = (await PushOne(a, insert)).RowVersion!.Value;

        Assert.Equal(SyncChangeStatus.Applied, (await PushOne(a, Delete(insert.RecordId, v1))).Status);
        var edit = await PushOne(b, Update(insert.RecordId, v1, new JsonObject { ["body"] = "Still needed" }));

        Assert.Equal(SyncChangeStatus.Applied, edit.Status);
        Assert.Contains("restored", edit.Note);
        Assert.Contains((await PullAll(a)).Notes, n => n["id"]!.GetValue<Guid>() == insert.RecordId);
    }

    [Fact]
    public async Task Server_revalidates_and_rejects_bad_or_forbidden_changes()
    {
        var a = await factory.DemoAdminDeviceAsync();

        var empty = await PushOne(a, Insert(a.BranchId, "  "));
        Assert.Equal(SyncChangeStatus.Rejected, empty.Status);
        Assert.Contains("Title", empty.Error);

        var unknownBranch = await PushOne(a, Insert(Guid.NewGuid(), "Nowhere"));
        Assert.Equal(SyncChangeStatus.Rejected, unknownBranch.Status);

        var branchEdit = await PushOne(a, new SyncChange(Guid.NewGuid(), SyncEntityNames.Branch, a.BranchId, SyncOperation.Update, 0,
            ["name"], new JsonObject { ["name"] = "Hacked" }, DateTimeOffset.UtcNow));
        Assert.Equal(SyncChangeStatus.Rejected, branchEdit.Status);

        // Bookkeeping fields sent by a device are ignored, not trusted.
        var sneaky = Insert(a.BranchId, "Sneaky");
        sneaky.Data!["tenantId"] = Guid.NewGuid();
        sneaky.Data!["rowVersion"] = 999999;
        var ok = await PushOne(a, sneaky with { ChangedFields = [.. sneaky.ChangedFields, "tenantId", "rowVersion"] });
        Assert.Equal(SyncChangeStatus.Applied, ok.Status);
        Assert.NotEqual(999999, ok.RowVersion);
    }

    [Fact]
    public async Task A_role_without_notes_permission_cannot_push_or_see_notes()
    {
        using (var admin = await factory.DemoAdminAsync())
        {
            await (await admin.PostAsJsonAsync(ApiRoutes.Roles, new SaveRoleRequest("No notes " + Guid.NewGuid().ToString("N")[..6], null,
                [new GrantedPermission(Permissions.Patients.View, PermissionScope.OwnBranch)]))).EnsureSuccessAsync();
        }
        using var adminClient = await factory.DemoAdminAsync();
        var role = (await adminClient.GetFromJsonAsync<List<RoleDto>>(ApiRoutes.Roles))!.Last(r => r.Name.StartsWith("No notes"));
        var userName = await factory.CreateUserAsync(role.Name);
        var d = await factory.DeviceAsync("demo", userName, SyncTestExtensions.TestPassword);

        var result = await PushOne(d, Insert(d.BranchId, "Not allowed"));
        Assert.Equal(SyncChangeStatus.Rejected, result.Status);
        Assert.Contains("Notes.Manage", result.Error);

        var admin2 = await factory.DemoAdminDeviceAsync();
        await PushOne(admin2, Insert(admin2.BranchId, "Admins only see this"));
        Assert.Empty((await PullAll(d)).Notes);
    }

    [Fact]
    public async Task Pull_is_scoped_to_the_devices_branches_and_writes_to_other_branches_are_rejected()
    {
        BranchDto north;
        using (var admin = await factory.DemoAdminAsync())
        {
            north = await (await admin.PostAsJsonAsync(ApiRoutes.Branches,
                new SaveBranchRequest(HttpExtensions.Unique("N").ToUpperInvariant()[..8], "North", null, null))).ReadAsync<BranchDto>();
        }

        var userName = await factory.CreateUserAsync("Receptionist", north.Id);
        var northDevice = await factory.DeviceAsync("demo", userName, SyncTestExtensions.TestPassword);
        var mainDevice = await factory.DemoAdminDeviceAsync();

        var mainNote = Insert(mainDevice.BranchId, "Main branch only");
        await PushOne(mainDevice, mainNote);
        var northNote = Insert(north.Id, "North branch note");
        Assert.Equal(SyncChangeStatus.Applied, (await PushOne(northDevice, northNote)).Status);

        var pulled = await PullAll(northDevice);
        Assert.DoesNotContain(pulled.Notes, n => n["id"]!.GetValue<Guid>() == mainNote.RecordId);
        Assert.Contains(pulled.Notes, n => n["id"]!.GetValue<Guid>() == northNote.RecordId);

        // Config (branches) reaches every device in the tenant.
        Assert.Contains(pulled.Branches, b => b["code"]!.GetValue<string>() == "MAIN");

        var intrusion = await PushOne(northDevice, Insert(mainDevice.BranchId, "Writing into Main"));
        Assert.Equal(SyncChangeStatus.Rejected, intrusion.Status);
    }

    [Fact]
    public async Task Admin_edits_to_config_are_pulled_incrementally()
    {
        var d = await factory.DemoAdminDeviceAsync();
        var start = await d.Transport.PullAsync(0, 500, default);
        var checkpoint = start.Checkpoint;
        while ((await d.Transport.PullAsync(checkpoint, 500, default)) is { HasMore: true } more) checkpoint = more.Checkpoint;

        using (var admin = await factory.DemoAdminAsync())
        {
            await (await admin.PostAsJsonAsync(ApiRoutes.Branches,
                new SaveBranchRequest(HttpExtensions.Unique("C").ToUpperInvariant()[..8], "Config branch", null, null))).EnsureSuccessAsync();
        }

        var delta = await d.Transport.PullAsync(checkpoint, 500, default);
        Assert.Contains(delta.Entities[SyncEntityNames.Branch], b => b["name"]!.GetValue<string>() == "Config branch");
        Assert.True(delta.Checkpoint > checkpoint);
    }

    [Fact]
    public async Task Pull_pages_resume_from_each_checkpoint()
    {
        var a = await factory.DemoAdminDeviceAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            var c = Insert(a.BranchId, $"Paged {i}");
            ids.Add(c.RecordId);
            await PushOne(a, c);
        }

        var all = await PullAll(a, pageSize: 3);
        Assert.All(ids, id => Assert.Contains(all.Notes, n => n["id"]!.GetValue<Guid>() == id));
    }

    [Fact]
    public async Task Too_old_app_version_must_update_before_syncing()
    {
        var old = await factory.DeviceAsync("demo", "admin", ClinicApiFactory.DemoAdminPassword, appVersion: "0.9.0");
        var ex = await Assert.ThrowsAsync<SyncTransportException>(() => old.Transport.PullAsync(0, 10, default));
        Assert.Equal(SyncFailureKind.UpdateRequired, ex.Kind);
    }

    [Fact]
    public async Task Revoked_device_cannot_sync()
    {
        var d = await factory.DemoAdminDeviceAsync();
        using (var admin = await factory.DemoAdminAsync())
            await (await admin.PostAsync($"{ApiRoutes.Devices}/{d.DeviceId}/revoke", null)).EnsureSuccessAsync();

        var ex = await Assert.ThrowsAsync<SyncTransportException>(() => d.Transport.PushAsync(
            new SyncPushRequest(d.DeviceId, "1.0.0", [Insert(d.BranchId, "after revoke")]), default));
        Assert.Equal(SyncFailureKind.DeviceRevoked, ex.Kind);
    }

    [Fact]
    public async Task Pushing_for_another_device_id_is_refused()
    {
        var a = await factory.DemoAdminDeviceAsync();
        var b = await factory.DemoAdminDeviceAsync();
        var ex = await Assert.ThrowsAsync<SyncTransportException>(() => a.Transport.PushAsync(
            new SyncPushRequest(b.DeviceId, "1.0.0", [Insert(a.BranchId, "spoofed")]), default));
        Assert.Equal(SyncFailureKind.Refused, ex.Kind);
    }

    [Fact]
    public async Task Number_ranges_never_overlap()
    {
        var a = await factory.DemoAdminDeviceAsync();
        var b = await factory.DemoAdminDeviceAsync();

        var ranges = new List<NumberRangeDto>();
        for (var i = 0; i < 3; i++)
        {
            ranges.Add(await a.Transport.LeaseNumbersAsync(NumberRangeKinds.Invoice, 100, default));
            ranges.Add(await b.Transport.LeaseNumbersAsync(NumberRangeKinds.Invoice, 100, default));
        }

        var ordered = ranges.OrderBy(r => r.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i].Start > ordered[i - 1].End, "ranges overlap");
        Assert.All(ranges, r => Assert.Equal(99, r.End - r.Start));

        var bad = await Assert.ThrowsAsync<SyncTransportException>(() => a.Transport.LeaseNumbersAsync("Bogus", 100, default));
        Assert.Equal(SyncFailureKind.Refused, bad.Kind);
    }
}

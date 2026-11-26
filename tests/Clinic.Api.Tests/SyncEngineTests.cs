using Clinic.Client.Core.Local;
using Clinic.Client.Core.Sync;
using Clinic.Infrastructure.Persistence;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Api.Tests;

/// <summary>Device-side engine against the real API: offline work, reconnect, retries, resume.</summary>
public sealed class SyncEngineTests(ClinicApiFactory factory) : IClassFixture<ClinicApiFactory>
{
    /// <summary>Phase 2 acceptance: a record created offline on two devices appears on both after reconnect.</summary>
    [Fact]
    public async Task Notes_created_offline_on_two_devices_reach_both_after_reconnect()
    {
        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await using var b = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        a.Transport.Offline = b.Transport.Offline = true;

        var fromA = await a.AddNoteAsync("Written on A while offline");
        var fromB = await b.AddNoteAsync("Written on B while offline");

        var offline = await a.Engine.SyncAsync();
        Assert.Equal(SyncPhase.Offline, offline.Phase);
        Assert.Equal(1, offline.Failed);
        Assert.Equal(SyncStatus.Pending, (await a.Store.GetAsync<LocalBranchNote>(fromA.Id))!.SyncStatus);

        a.Transport.Offline = b.Transport.Offline = false;
        await a.Engine.SyncAsync(manual: true); // A pushes its note
        await b.Engine.SyncAsync(manual: true); // B pushes its note and pulls A's
        await a.Engine.SyncAsync(manual: true); // A pulls B's

        foreach (var device in new[] { a, b })
        {
            var notes = await device.NotesAsync();
            Assert.Contains(notes, n => n.Id == fromA.Id && n.Title == fromA.Title);
            Assert.Contains(notes, n => n.Id == fromB.Id && n.Title == fromB.Title);
            Assert.All(notes.Where(n => n.Id == fromA.Id || n.Id == fromB.Id), n =>
            {
                Assert.Equal(SyncStatus.Synced, n.SyncStatus);
                Assert.True(n.RowVersion > 0);
            });
            Assert.Equal(0, device.Engine.State.Outstanding);
            Assert.Equal(SyncPhase.Idle, device.Engine.State.Phase);
        }
    }

    [Fact]
    public async Task Lost_push_response_is_retried_and_applied_exactly_once()
    {
        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        var note = await a.AddNoteAsync("Lost response");

        a.Transport.LoseNextPushResponse = true;
        var first = await a.Engine.SyncAsync();
        Assert.Equal(SyncPhase.Offline, first.Phase);
        Assert.Equal(1, first.Failed);

        var second = await a.Engine.SyncAsync(manual: true);
        Assert.Equal(0, second.Outstanding);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        Assert.Equal(1, await db.BranchNotes.IgnoreQueryFilters().CountAsync(n => n.Id == note.Id));
        Assert.Equal(1, await db.ChangeLogs.IgnoreQueryFilters().CountAsync(c => c.RecordId == note.Id));
    }

    [Fact]
    public async Task Failed_pushes_back_off_until_a_manual_sync()
    {
        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await a.AddNoteAsync("Backing off");
        a.Transport.Offline = true;
        await a.Engine.SyncAsync();

        a.Transport.Offline = false;
        var pushesBefore = a.Transport.PushCalls;
        var automatic = await a.Engine.SyncAsync(); // still inside the back-off window
        Assert.Equal(pushesBefore, a.Transport.PushCalls);
        Assert.Equal(1, automatic.Failed);

        var manual = await a.Engine.SyncAsync(manual: true);
        Assert.Equal(0, manual.Outstanding);
        Assert.Equal(TimeSpan.FromMinutes(5), a.Engine.Backoff(20));
    }

    [Fact]
    public async Task Interrupted_pull_resumes_from_the_last_committed_page()
    {
        await using var writer = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        var titles = Enumerable.Range(0, 6).Select(i => $"Resume {Guid.NewGuid():N}"[..20]).ToList();
        foreach (var t in titles) await writer.AddNoteAsync(t);
        await writer.Engine.SyncAsync();

        await using var reader = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync(),
            new SyncEngineOptions { PullPageSize = 2, LeaseKinds = [] });
        reader.Transport.FailPullCall = 3;

        var broken = await reader.Engine.SyncAsync();
        Assert.Equal(SyncPhase.Offline, broken.Phase);
        var checkpoint = (await reader.Db.Connection.FindAsync<SyncCheckpoint>(SyncEngine.CheckpointScope))!.Value;
        Assert.True(checkpoint > 0, "first pages should have been committed");

        await reader.Engine.SyncAsync();
        var notes = await reader.NotesAsync();
        Assert.All(titles, t => Assert.Contains(notes, n => n.Title == t));
        Assert.True((await reader.Db.Connection.FindAsync<SyncCheckpoint>(SyncEngine.CheckpointScope))!.Value > checkpoint);
    }

    [Fact]
    public async Task Edits_and_deletes_flow_between_devices_field_by_field()
    {
        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await using var b = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());

        var note = await a.AddNoteAsync("Shared note", "body v1");
        await a.Engine.SyncAsync();
        await b.Engine.SyncAsync();

        var onA = (await a.Store.GetAsync<LocalBranchNote>(note.Id))!;
        onA.Title = "Title edited on A";
        await a.Store.UpdateAsync(onA);

        var onB = (await b.Store.GetAsync<LocalBranchNote>(note.Id))!;
        onB.Body = "Body edited on B";
        onB.IsPinned = true;
        await b.Store.UpdateAsync(onB);

        await a.Engine.SyncAsync();
        await b.Engine.SyncAsync();
        await a.Engine.SyncAsync();

        foreach (var d in new[] { a, b })
        {
            var n = (await d.Store.GetAsync<LocalBranchNote>(note.Id))!;
            Assert.Equal("Title edited on A", n.Title);
            Assert.Equal("Body edited on B", n.Body);
            Assert.True(n.IsPinned);
            Assert.Equal(SyncStatus.Synced, n.SyncStatus);
        }

        await b.Store.DeleteAsync<LocalBranchNote>(note.Id);
        await b.Engine.SyncAsync();
        await a.Engine.SyncAsync();
        Assert.Null(await a.Store.GetAsync<LocalBranchNote>(note.Id));
    }

    [Fact]
    public async Task A_change_that_never_left_the_device_is_dropped_on_delete()
    {
        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        var note = await a.AddNoteAsync("Typo");
        await a.Store.DeleteAsync<LocalBranchNote>(note.Id);

        Assert.Equal(0, await a.Center.CountAsync());
        Assert.Null(await a.Db.Connection.FindAsync<LocalBranchNote>(note.Id));
    }

    [Fact]
    public async Task Rejected_change_waits_in_the_sync_center_and_can_be_discarded()
    {
        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        var bad = await a.Store.InsertAsync(new LocalBranchNote { BranchId = Guid.NewGuid(), Title = "Unknown branch" });
        var good = await a.AddNoteAsync("Fine");

        var state = await a.Engine.SyncAsync();
        Assert.Equal(1, state.Rejected);
        Assert.Equal(SyncStatus.Error, (await a.Store.GetAsync<LocalBranchNote>(bad.Id))!.SyncStatus);
        Assert.Equal(SyncStatus.Synced, (await a.Store.GetAsync<LocalBranchNote>(good.Id))!.SyncStatus);

        var entry = (await a.Center.ListAsync()).Single();
        Assert.Equal(OutboxStatus.Rejected, entry.Status);
        Assert.Contains("Branch", entry.LastError);

        await a.Center.DiscardAsync(entry.Seq);
        Assert.Equal(0, await a.Center.CountAsync());
        Assert.Null(await a.Db.Connection.FindAsync<LocalBranchNote>(bad.Id));
        Assert.Equal(0, a.Engine.State.Outstanding);
    }

    [Fact]
    public async Task Device_keeps_a_stock_of_leased_numbers_and_hands_them_out_offline()
    {
        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await using var b = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await a.Engine.SyncAsync();
        await b.Engine.SyncAsync();

        a.Transport.Offline = b.Transport.Offline = true;
        var fromA = new List<long>();
        var fromB = new List<long>();
        for (var i = 0; i < 20; i++)
        {
            fromA.Add(await NumberSource.NextAsync(a.Db, NumberRangeKinds.Mrn));
            fromB.Add(await NumberSource.NextAsync(b.Db, NumberRangeKinds.Mrn));
        }

        Assert.Equal(20, fromA.Distinct().Count());
        Assert.Empty(fromA.Intersect(fromB));
        Assert.Equal(30, await NumberSource.RemainingAsync(a.Db, NumberRangeKinds.Mrn));

        // Below 20% the next online sync leases another block.
        for (var i = 0; i < 25; i++) await NumberSource.NextAsync(a.Db, NumberRangeKinds.Mrn);
        a.Transport.Offline = false;
        await a.Engine.SyncAsync();
        Assert.Equal(55, await NumberSource.RemainingAsync(a.Db, NumberRangeKinds.Mrn));
    }

    [Fact]
    public async Task Revoked_device_raises_the_wipe_event()
    {
        var session = await factory.DemoAdminDeviceAsync();
        await using var a = await ClientDevice.CreateAsync(session);
        var revoked = false;
        a.Engine.DeviceRevoked += (_, _) => revoked = true;

        using (var admin = await factory.DemoAdminAsync())
            await (await admin.PostAsync($"api/v1/devices/{session.DeviceId}/revoke", null)).EnsureSuccessAsync();

        await a.Engine.SyncAsync();
        Assert.True(revoked);
    }
}

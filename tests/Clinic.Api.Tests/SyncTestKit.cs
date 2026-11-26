using System.Net.Http.Headers;
using System.Net.Http.Json;
using Clinic.Client.Core.Local;
using Clinic.Client.Core.Sync;
using Clinic.Shared.Contracts;
using Clinic.Shared.Sync;

namespace Clinic.Api.Tests;

/// <summary>A signed-in, registered device talking to the test server.</summary>
public sealed record DeviceSession(Guid DeviceId, TokenResponse Token, HttpSyncTransport Transport, HttpClient Http, ClientContext Context, string DeviceCode = "D00")
{
    public Guid TenantId => Token.Me.TenantId!.Value;
    public Guid BranchId => Token.Me.Branches[0].Id;
}

/// <summary>Wraps the real transport so tests can simulate going offline and lost responses.</summary>
public sealed class SwitchableTransport(ISyncTransport inner) : ISyncTransport
{
    public bool Offline { get; set; }

    /// <summary>Deliver the next push to the server, then pretend the response was lost.</summary>
    public bool LoseNextPushResponse { get; set; }

    /// <summary>Fail the Nth pull call (1-based) as if the connection dropped.</summary>
    public int FailPullCall { get; set; }

    public int PullCalls { get; private set; }
    public int PushCalls { get; private set; }

    public async Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct)
    {
        PushCalls++;
        ThrowIfOffline();
        var response = await inner.PushAsync(request, ct);
        if (LoseNextPushResponse)
        {
            LoseNextPushResponse = false;
            throw new SyncTransportException(SyncFailureKind.Transient, "Connection dropped (simulated).");
        }
        return response;
    }

    public Task<SyncPullResponse> PullAsync(long since, int pageSize, CancellationToken ct)
    {
        PullCalls++;
        ThrowIfOffline();
        if (PullCalls == FailPullCall)
            throw new SyncTransportException(SyncFailureKind.Transient, "Connection dropped mid-pull (simulated).");
        return inner.PullAsync(since, pageSize, ct);
    }

    public Task<NumberRangeDto> LeaseNumbersAsync(string kind, int size, CancellationToken ct)
    {
        ThrowIfOffline();
        return inner.LeaseNumbersAsync(kind, size, ct);
    }

    private void ThrowIfOffline()
    {
        if (Offline) throw new SyncTransportException(SyncFailureKind.Transient, "Offline (simulated).");
    }
}

/// <summary>A full simulated device: encrypted-capable local DB, store, sync engine and Sync Center.</summary>
public sealed class ClientDevice : IAsyncDisposable
{
    private ClientDevice(DeviceSession session, LocalDatabase db, SwitchableTransport transport, SyncEngineOptions options)
    {
        Session = session;
        Db = db;
        Transport = transport;
        Store = new LocalStore(db, session.Context);
        Engine = new SyncEngine(db, transport, session.Context, options);
        Center = new SyncCenter(db, Engine);
        Patients = new Clinic.Client.Core.Clinical.PatientService(db, Store, session.DeviceCode);
        Scheduling = new Clinic.Client.Core.Clinical.SchedulingService(Store);
        FilesDirectory = Path.Combine(Path.GetTempPath(), $"clinic-files-{Guid.NewGuid():N}");
        Attachments = new Clinic.Client.Core.Clinical.AttachmentService(db, Store, FilesDirectory, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Files = new Clinic.Client.Core.Clinical.HttpFileTransport(session.Http, _ => Task.FromResult<string?>(session.Token.AccessToken));
    }

    public Clinic.Client.Core.Clinical.PatientService Patients { get; }
    public Clinic.Client.Core.Clinical.SchedulingService Scheduling { get; }
    public Clinic.Client.Core.Clinical.AttachmentService Attachments { get; }
    public Clinic.Client.Core.Clinical.HttpFileTransport Files { get; }
    public string FilesDirectory { get; }

    public DeviceSession Session { get; }
    public LocalDatabase Db { get; }
    public SwitchableTransport Transport { get; }
    public LocalStore Store { get; }
    public SyncEngine Engine { get; }
    public SyncCenter Center { get; }

    public static async Task<ClientDevice> CreateAsync(DeviceSession session, SyncEngineOptions? options = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"clinic-device-{Guid.NewGuid():N}.db3");
        var db = await LocalDatabase.OpenAsync(path, encryptionKey: null);
        return new ClientDevice(session, db, new SwitchableTransport(session.Transport),
            options ?? new SyncEngineOptions { LeaseSize = 50 });
    }

    public Task<List<LocalBranchNote>> NotesAsync() => Store.ListAsync<LocalBranchNote>();

    public Task<LocalBranchNote> AddNoteAsync(string title, string? body = null) =>
        Store.InsertAsync(new LocalBranchNote { BranchId = Session.BranchId, Title = title, Body = body });

    public async ValueTask DisposeAsync()
    {
        await Db.WipeAsync();
        Session.Http.Dispose();
        try { if (Directory.Exists(FilesDirectory)) Directory.Delete(FilesDirectory, true); } catch (IOException) { }
    }
}

public static class SyncTestExtensions
{
    public static async Task<DeviceSession> DeviceAsync(this ClinicApiFactory factory, string tenant, string user, string password, string appVersion = "1.0.0")
    {
        var deviceId = Guid.NewGuid();
        var token = await factory.LoginAsync(tenant, user, password, deviceId);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var registered = await (await http.PostAsJsonAsync($"{ApiRoutes.Devices}/register",
            new RegisterDeviceRequest(deviceId, $"Test {user}", "Test", null, appVersion, token.Me.Branches.FirstOrDefault()?.Id))).ReadAsync<DeviceDto>();

        var context = new ClientContext(token.Me.TenantId!.Value, token.Me.UserId, deviceId, appVersion);
        var transport = new HttpSyncTransport(http, context, _ => Task.FromResult<string?>(token.AccessToken));
        return new DeviceSession(deviceId, token, transport, http, context, registered.DeviceCode);
    }

    public static Task<DeviceSession> DemoAdminDeviceAsync(this ClinicApiFactory factory) =>
        factory.DeviceAsync("demo", "admin", ClinicApiFactory.DemoAdminPassword);

    /// <summary>Creates a demo-tenant user with the given role and branches; returns the user name.</summary>
    public static async Task<string> CreateUserAsync(this ClinicApiFactory factory, string roleName, params Guid[] branchIds)
    {
        using var admin = await factory.DemoAdminAsync();
        var roles = await admin.GetFromJsonAsync<List<RoleDto>>(ApiRoutes.Roles);
        if (branchIds.Length == 0)
            branchIds = [(await admin.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches))!.Single(b => b.Code == "MAIN").Id];

        var userName = HttpExtensions.Unique("u");
        await (await admin.PostAsJsonAsync(ApiRoutes.Users, new CreateUserRequest(
            userName, $"User {userName}", null, null, "Passw0rd!x", [roles!.Single(r => r.Name == roleName).Id], branchIds))).EnsureSuccessAsync();
        return userName;
    }

    public const string TestPassword = "Passw0rd!x";
}

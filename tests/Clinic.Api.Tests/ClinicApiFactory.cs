using System.Net.Http.Headers;
using System.Net.Http.Json;
using Clinic.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Clinic.Api.Tests;

/// <summary>
/// Runs the real API against a throw-away SQLite file, with the same seed data as development:
/// Super Admin "superadmin" and the "demo" tenant with admin "admin".
/// </summary>
public sealed class ClinicApiFactory : WebApplicationFactory<Program>
{
    public const string SuperAdminPassword = "SuperAdmin@123";
    public const string DemoAdminPassword = "Admin@123";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"clinic-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Clinic", $"Data Source={_dbPath};Pooling=False");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-0123456789abcdef-0123456789");
        builder.UseSetting("RateLimiting:AuthPermitsPerMinute", "10000");
        builder.UseSetting("Seed:Enabled", "true");
        builder.UseSetting("Seed:SuperAdminUserName", "superadmin");
        builder.UseSetting("Seed:SuperAdminPassword", SuperAdminPassword);
        builder.UseSetting("Seed:DemoTenant", "true");
        builder.UseSetting("Seed:DemoTenantCode", "demo");
        builder.UseSetting("Seed:DemoAdminPassword", DemoAdminPassword);
    }

    public async Task<TokenResponse> LoginAsync(string? tenant, string user, string password, Guid? deviceId = null)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login, new LoginRequest(tenant, user, password, deviceId));
        await response.EnsureSuccessAsync();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    public async Task<HttpClient> ClientForAsync(string? tenant, string user, string password, Guid? deviceId = null)
    {
        var token = await LoginAsync(tenant, user, password, deviceId);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    public Task<HttpClient> DemoAdminAsync() => ClientForAsync("demo", "admin", DemoAdminPassword);
    public Task<HttpClient> SuperAdminAsync() => ClientForAsync(null, "superadmin", SuperAdminPassword);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}

public static class HttpExtensions
{
    public static async Task EnsureSuccessAsync(this HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException($"{(int)response.StatusCode} {response.StatusCode}: {body}");
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        await response.EnsureSuccessAsync();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 8, 40)];
}

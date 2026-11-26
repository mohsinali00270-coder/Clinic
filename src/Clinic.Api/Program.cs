using System.IO.Compression;
using System.Threading.RateLimiting;
using Clinic.Api.Controllers;
using Clinic.Api.Infrastructure;
using Clinic.Api.Security;
using Clinic.Application;
using Clinic.Application.Abstractions;
using Clinic.Application.Sync;
using Clinic.Infrastructure;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Security;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddApplication();
builder.Services.AddInfrastructure(config);
builder.Services.Configure<SyncOptions>(config.GetSection(SyncOptions.Section));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// ---- Auth: JWT bearer + permission policies (FR-API-02, FR-AUTH-01) ----
var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.GetKey(),
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClinicClaimTypes.UserName,
        };
        // SignalR websockets cannot send headers: the token comes in the query string for the hub only.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.HttpContext.Request.Path.StartsWithSegments(Clinic.Shared.Scheduling.SchedulingRoutes.Hub) &&
                    ctx.Request.Query["access_token"] is { Count: > 0 } token)
                    ctx.Token = token;
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddAuthorization();

// ---- Errors as RFC 7807 ProblemDetails (FR-API-04) ----
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

// ---- Hardening (FR-API-06) ----
var authPermitsPerMinute = config.GetValue("RateLimiting:AuthPermitsPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(RateLimitPolicies.Auth, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermitsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddRequestDecompression(); // FR-SYN-10: compressed push batches
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 50 * 1024 * 1024);

builder.Services.AddHealthChecks().AddDbContextCheck<ClinicDbContext>();
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddSingleton<Clinic.Application.Clinical.IChangeNotifier, Clinic.Api.Realtime.SignalRChangeNotifier>();
builder.Services.AddOpenApi();

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRequestDecompression();
app.UseResponseCompression();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();
else
    app.UseHsts();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapHub<Clinic.Api.Realtime.ClinicHub>(Clinic.Shared.Scheduling.SchedulingRoutes.Hub);
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;

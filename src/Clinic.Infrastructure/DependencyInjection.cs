using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Database:Provider = SqlServer (default, production) or Sqlite (integration tests).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));

        var provider = config["Database:Provider"] ?? "SqlServer";
        var connection = config.GetConnectionString("Clinic")
                         ?? throw new InvalidOperationException("ConnectionStrings:Clinic is not configured.");

        services.AddDbContext<ClinicDbContext>(o =>
        {
            if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
                o.UseSqlite(connection).AddInterceptors(SqliteTuning.Instance);
            else
                o.UseSqlServer(connection, sql => sql.EnableRetryOnFailure());
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<ClinicDbContext>());

        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<Application.Clinical.IFileStore, Files.LocalDiskFileStore>();
        services.AddSingleton<Application.Clinical.IMessageSender, Messaging.LoggingMessageSender>();
        return services;
    }
}

/// <summary>
/// SQLite (tests only): WAL with synchronous=NORMAL, as a server would run it. The default rollback journal fsyncs
/// several times per commit, which on Windows costs ~25 ms and would dominate any timing of the sync pipeline.
/// </summary>
internal sealed class SqliteTuning : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
{
    public static readonly SqliteTuning Instance = new();

    public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) =>
        Tune(connection);

    public override Task ConnectionOpenedAsync(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Tune(connection);
        return Task.CompletedTask;
    }

    private static void Tune(System.Data.Common.DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000;";
        cmd.ExecuteNonQuery();
    }
}

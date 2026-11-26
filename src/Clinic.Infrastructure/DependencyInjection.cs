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
                o.UseSqlite(connection);
            else
                o.UseSqlServer(connection, sql => sql.EnableRetryOnFailure());
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<ClinicDbContext>());

        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<Application.Clinical.IFileStore, Files.LocalDiskFileStore>();
        return services;
    }
}

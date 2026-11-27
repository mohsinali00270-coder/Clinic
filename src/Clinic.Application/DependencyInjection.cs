using Clinic.Application.Devices;
using Clinic.Application.Identity;
using Clinic.Application.Tenancy;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, ServiceLifetime.Singleton);
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<AuthService>();
        services.AddScoped<TenantService>();
        services.AddScoped<BranchService>();
        services.AddScoped<RoleService>();
        services.AddScoped<UserService>();
        services.AddScoped<DeviceService>();
        services.AddScoped<Sync.SyncService>();
        services.AddScoped<Sync.NumberRangeService>();
        services.AddScoped<Forms.FormService>();
        services.AddScoped<Forms.LookupService>();
        services.AddScoped<Clinical.DoctorService>();
        services.AddScoped<Clinical.SchedulingSetupService>();
        services.AddScoped<Clinical.FileService>();
        services.AddScoped<Clinical.ClinicalMasterService>();
        services.AddScoped<Clinical.CareSetupService>();
        services.AddScoped<Clinical.LabBillingService>();
        services.AddScoped<Clinical.AccountsService>();
        services.AddScoped<Clinical.MessageDispatcher>();
        services.AddScoped<Clinical.InventoryReportService>();
        services.AddScoped<Sync.ChangeLogRetention>();
        services.AddScoped<Clinical.PatientMergeService>();
        services.AddScoped<Clinical.AuditService>();
        return services;
    }
}

using Clinic.Domain.Auditing;
using Clinic.Domain.Devices;
using Clinic.Domain.Identity;
using Clinic.Domain.Notes;
using Clinic.Domain.Sync;
using Clinic.Domain.Tenancy;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Clinic.Application.Abstractions;

/// <summary>The caller, as described by the validated JWT.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    Guid? TenantId { get; }
    bool IsSuperAdmin { get; }
    Guid? DeviceId { get; }
    IReadOnlyList<Guid> BranchIds { get; }

    /// <summary>Role names, for field-level permissions (FR-AUTH-03).</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>The scope the caller holds for a permission, or null if not granted.</summary>
    PermissionScope? GetScope(string permission);
}

/// <summary>
/// Tenant-filtered unit of work. Every tenant entity set is automatically limited to
/// <see cref="CurrentTenantId"/>; writes for any other tenant are refused at save time.
/// </summary>
public interface IAppDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<Branch> Branches { get; }
    DbSet<User> Users { get; }
    DbSet<PlatformAdmin> PlatformAdmins { get; }
    DbSet<Role> Roles { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<UserBranch> UserBranches { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Device> Devices { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<ChangeLog> ChangeLogs { get; }
    DbSet<SyncCounter> SyncCounters { get; }
    DbSet<SyncLog> SyncLogs { get; }
    DbSet<NumberRange> NumberRanges { get; }
    DbSet<BranchNote> BranchNotes { get; }
    DbSet<Domain.Forms.Form> Forms { get; }
    DbSet<Domain.Forms.FormVersion> FormVersions { get; }
    DbSet<Domain.Forms.LookupList> LookupLists { get; }
    DbSet<Domain.Forms.LookupItem> LookupItems { get; }
    DbSet<Domain.Forms.FieldValue> FieldValues { get; }
    DbSet<Domain.Clinical.Patient> Patients { get; }
    DbSet<Domain.Clinical.DoctorProfile> DoctorProfiles { get; }
    DbSet<Domain.Clinical.DoctorSchedule> DoctorSchedules { get; }
    DbSet<Domain.Clinical.DoctorLeave> DoctorLeaves { get; }
    DbSet<Domain.Clinical.BranchClosure> BranchClosures { get; }
    DbSet<Domain.Clinical.AppointmentType> AppointmentTypes { get; }
    DbSet<Domain.Clinical.Appointment> Appointments { get; }
    DbSet<Domain.Clinical.AppointmentStatusHistory> AppointmentStatusHistory { get; }
    DbSet<Domain.Clinical.Attachment> Attachments { get; }
    DbSet<Domain.Clinical.Encounter> Encounters { get; }
    DbSet<Domain.Clinical.VitalsRecord> Vitals { get; }
    DbSet<Domain.Clinical.Diagnosis> Diagnoses { get; }
    DbSet<Domain.Clinical.Icd10Code> Icd10Codes { get; }
    DbSet<Domain.Clinical.EncounterAmendment> EncounterAmendments { get; }
    DbSet<Domain.Clinical.Drug> Drugs { get; }
    DbSet<Domain.Clinical.Prescription> Prescriptions { get; }
    DbSet<Domain.Clinical.PrescriptionItem> PrescriptionItems { get; }
    DbSet<Domain.Clinical.PrescriptionTemplate> PrescriptionTemplates { get; }

    DbSet<TEntity> Set<TEntity>() where TEntity : class;
    EntityEntry Entry(object entity);
    ChangeTracker ChangeTracker { get; }
    DatabaseFacade Database { get; }
    IModel Model { get; }

    /// <summary>
    /// Reserves <paramref name="count"/> consecutive numbers of a kind for the current tenant and returns the first.
    /// Must run inside a transaction; the counter row stays locked until it commits.
    /// </summary>
    Task<long> ReserveNumbersAsync(string kind, int count, CancellationToken ct = default);

    Guid? CurrentTenantId { get; }

    /// <summary>
    /// Act as a specific tenant for the lifetime of the returned scope (login, tenant creation, seeding).
    /// Only trusted server code calls this; it is never driven by request input without checks.
    /// </summary>
    IDisposable BeginTenantScope(Guid? tenantId);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
}

public sealed record TokenSubject(
    Guid UserId,
    string UserName,
    string FullName,
    Guid? TenantId,
    string? TenantCode,
    bool IsSuperAdmin,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<GrantedPermission> Permissions,
    Guid? DeviceId,
    IReadOnlyList<string>? Roles = null);

public interface ITokenService
{
    (string Token, DateTime ExpiresAt) CreateAccessToken(TokenSubject subject);
    string CreateRefreshToken();
    string HashRefreshToken(string token);
}

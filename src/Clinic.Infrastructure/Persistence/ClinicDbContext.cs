using System.Linq.Expressions;
using System.Text.Json;
using Clinic.Application.Abstractions;
using Clinic.Application.Sync;
using Clinic.Domain.Auditing;
using Clinic.Domain.Common;
using Clinic.Domain.Devices;
using Clinic.Domain.Identity;
using Clinic.Domain.Notes;
using Clinic.Domain.Sync;
using Clinic.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Clinic.Infrastructure.Persistence;

public sealed class ClinicDbContext(
    DbContextOptions<ClinicDbContext> options,
    ICurrentUser currentUser,
    TimeProvider clock,
    IEnumerable<Application.Clinical.IChangeNotifier> notifiers) : DbContext(options), IAppDbContext
{
    private static readonly HashSet<string> MaskedProperties = [nameof(User.PasswordHash), nameof(RefreshToken.TokenHash)];

    /// <summary>Properties that never appear in audit rows or in ChangeLog field lists.</summary>
    private static readonly HashSet<string> BookkeepingProperties =
    [
        nameof(Entity.CreatedAt), nameof(Entity.UpdatedAt), nameof(Entity.CreatedBy), nameof(Entity.UpdatedBy),
        nameof(ISyncEntity.RowVersion),
    ];

    /// <summary>Device heartbeat columns: an update touching only these is not worth an audit row.</summary>
    private static readonly HashSet<string> HeartbeatProperties =
    [
        nameof(Device.LastSeenAt), nameof(Device.LastSyncAt), nameof(Device.LastPushCount), nameof(Device.LastPullCount),
    ];

    /// <summary>Infrastructure tables that are never audited.</summary>
    private static readonly HashSet<Type> UnauditedTypes =
    [
        typeof(AuditLog), typeof(RefreshToken), typeof(ChangeLog), typeof(SyncConflict), typeof(SyncCounter), typeof(SyncLog), typeof(NumberCounter),
        typeof(Domain.Clinical.ClientError),
    ];

    private bool _hasTenantOverride;
    private Guid? _tenantOverride;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<User> Users => Set<User>();
    public DbSet<PlatformAdmin> PlatformAdmins => Set<PlatformAdmin>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<UserBranch> UserBranches => Set<UserBranch>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ChangeLog> ChangeLogs => Set<ChangeLog>();
    public DbSet<SyncConflict> SyncConflicts => Set<SyncConflict>();
    public DbSet<SyncCounter> SyncCounters => Set<SyncCounter>();
    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();
    public DbSet<NumberCounter> NumberCounters => Set<NumberCounter>();
    public DbSet<NumberRange> NumberRanges => Set<NumberRange>();
    public DbSet<BranchNote> BranchNotes => Set<BranchNote>();
    public DbSet<Domain.Forms.Form> Forms => Set<Domain.Forms.Form>();
    public DbSet<Domain.Forms.FormVersion> FormVersions => Set<Domain.Forms.FormVersion>();
    public DbSet<Domain.Forms.LookupList> LookupLists => Set<Domain.Forms.LookupList>();
    public DbSet<Domain.Forms.LookupItem> LookupItems => Set<Domain.Forms.LookupItem>();
    public DbSet<Domain.Forms.FieldValue> FieldValues => Set<Domain.Forms.FieldValue>();
    public DbSet<Domain.Clinical.Patient> Patients => Set<Domain.Clinical.Patient>();
    public DbSet<Domain.Clinical.DoctorProfile> DoctorProfiles => Set<Domain.Clinical.DoctorProfile>();
    public DbSet<Domain.Clinical.DoctorSchedule> DoctorSchedules => Set<Domain.Clinical.DoctorSchedule>();
    public DbSet<Domain.Clinical.DoctorLeave> DoctorLeaves => Set<Domain.Clinical.DoctorLeave>();
    public DbSet<Domain.Clinical.BranchClosure> BranchClosures => Set<Domain.Clinical.BranchClosure>();
    public DbSet<Domain.Clinical.AppointmentType> AppointmentTypes => Set<Domain.Clinical.AppointmentType>();
    public DbSet<Domain.Clinical.Appointment> Appointments => Set<Domain.Clinical.Appointment>();
    public DbSet<Domain.Clinical.AppointmentStatusHistory> AppointmentStatusHistory => Set<Domain.Clinical.AppointmentStatusHistory>();
    public DbSet<Domain.Clinical.Attachment> Attachments => Set<Domain.Clinical.Attachment>();
    public DbSet<Domain.Clinical.Encounter> Encounters => Set<Domain.Clinical.Encounter>();
    public DbSet<Domain.Clinical.VitalsRecord> Vitals => Set<Domain.Clinical.VitalsRecord>();
    public DbSet<Domain.Clinical.Diagnosis> Diagnoses => Set<Domain.Clinical.Diagnosis>();
    public DbSet<Domain.Clinical.Icd10Code> Icd10Codes => Set<Domain.Clinical.Icd10Code>();
    public DbSet<Domain.Clinical.EncounterAmendment> EncounterAmendments => Set<Domain.Clinical.EncounterAmendment>();
    public DbSet<Domain.Clinical.Drug> Drugs => Set<Domain.Clinical.Drug>();
    public DbSet<Domain.Clinical.Prescription> Prescriptions => Set<Domain.Clinical.Prescription>();
    public DbSet<Domain.Clinical.PrescriptionItem> PrescriptionItems => Set<Domain.Clinical.PrescriptionItem>();
    public DbSet<Domain.Clinical.PrescriptionTemplate> PrescriptionTemplates => Set<Domain.Clinical.PrescriptionTemplate>();
    public DbSet<Domain.Clinical.CaseType> CaseTypes => Set<Domain.Clinical.CaseType>();
    public DbSet<Domain.Clinical.PatientCase> Cases => Set<Domain.Clinical.PatientCase>();
    public DbSet<Domain.Clinical.CarePlanStep> CarePlanSteps => Set<Domain.Clinical.CarePlanStep>();
    public DbSet<Domain.Clinical.CaseMember> CaseMembers => Set<Domain.Clinical.CaseMember>();
    public DbSet<Domain.Clinical.CaseNote> CaseNotes => Set<Domain.Clinical.CaseNote>();
    public DbSet<Domain.Clinical.Referral> Referrals => Set<Domain.Clinical.Referral>();
    public DbSet<Domain.Clinical.ProcedureType> ProcedureTypes => Set<Domain.Clinical.ProcedureType>();
    public DbSet<Domain.Clinical.ProcedureRecord> Procedures => Set<Domain.Clinical.ProcedureRecord>();
    public DbSet<Domain.Clinical.Bed> Beds => Set<Domain.Clinical.Bed>();
    public DbSet<Domain.Clinical.Admission> Admissions => Set<Domain.Clinical.Admission>();
    public DbSet<Domain.Clinical.BedTransfer> BedTransfers => Set<Domain.Clinical.BedTransfer>();
    public DbSet<Domain.Clinical.NursingNote> NursingNotes => Set<Domain.Clinical.NursingNote>();
    public DbSet<Domain.Clinical.MedicationOrder> MedicationOrders => Set<Domain.Clinical.MedicationOrder>();
    public DbSet<Domain.Clinical.MedicationDose> MedicationDoses => Set<Domain.Clinical.MedicationDose>();
    public DbSet<Domain.Clinical.LabTest> LabTests => Set<Domain.Clinical.LabTest>();
    public DbSet<Domain.Clinical.LabOrder> LabOrders => Set<Domain.Clinical.LabOrder>();
    public DbSet<Domain.Clinical.LabResult> LabResults => Set<Domain.Clinical.LabResult>();
    public DbSet<Domain.Clinical.ServiceItem> ServiceItems => Set<Domain.Clinical.ServiceItem>();
    public DbSet<Domain.Clinical.Invoice> Invoices => Set<Domain.Clinical.Invoice>();
    public DbSet<Domain.Clinical.InvoiceLine> InvoiceLines => Set<Domain.Clinical.InvoiceLine>();
    public DbSet<Domain.Clinical.Payment> Payments => Set<Domain.Clinical.Payment>();
    public DbSet<Domain.Clinical.CashClosing> CashClosings => Set<Domain.Clinical.CashClosing>();
    public DbSet<Domain.Clinical.PackagePlan> PackagePlans => Set<Domain.Clinical.PackagePlan>();
    public DbSet<Domain.Clinical.PatientPackage> PatientPackages => Set<Domain.Clinical.PatientPackage>();
    public DbSet<Domain.Clinical.PackageUsage> PackageUsages => Set<Domain.Clinical.PackageUsage>();
    public DbSet<Domain.Clinical.Payer> Payers => Set<Domain.Clinical.Payer>();
    public DbSet<Domain.Clinical.PatientPanel> PatientPanels => Set<Domain.Clinical.PatientPanel>();
    public DbSet<Domain.Clinical.PayerSettlement> PayerSettlements => Set<Domain.Clinical.PayerSettlement>();
    public DbSet<Domain.Clinical.CreditNote> CreditNotes => Set<Domain.Clinical.CreditNote>();
    public DbSet<Domain.Clinical.Expense> Expenses => Set<Domain.Clinical.Expense>();
    public DbSet<Domain.Clinical.DoctorShareRule> DoctorShareRules => Set<Domain.Clinical.DoctorShareRule>();
    public DbSet<Domain.Clinical.DoctorPayout> DoctorPayouts => Set<Domain.Clinical.DoctorPayout>();
    public DbSet<Domain.Clinical.MessageTemplate> MessageTemplates => Set<Domain.Clinical.MessageTemplate>();
    public DbSet<Domain.Clinical.ContactPreference> ContactPreferences => Set<Domain.Clinical.ContactPreference>();
    public DbSet<Domain.Clinical.OutboundMessage> OutboundMessages => Set<Domain.Clinical.OutboundMessage>();
    public DbSet<Domain.Clinical.Feedback> Feedback => Set<Domain.Clinical.Feedback>();
    public DbSet<Domain.Clinical.Complaint> Complaints => Set<Domain.Clinical.Complaint>();
    public DbSet<Domain.Clinical.InventoryItem> InventoryItems => Set<Domain.Clinical.InventoryItem>();
    public DbSet<Domain.Clinical.StockBatch> StockBatches => Set<Domain.Clinical.StockBatch>();
    public DbSet<Domain.Clinical.StockMovement> StockMovements => Set<Domain.Clinical.StockMovement>();
    public DbSet<Domain.Clinical.Dispensation> Dispensations => Set<Domain.Clinical.Dispensation>();
    public DbSet<Domain.Clinical.PatientMerge> PatientMerges => Set<Domain.Clinical.PatientMerge>();
    public DbSet<Domain.Clinical.AccessEvent> AccessEvents => Set<Domain.Clinical.AccessEvent>();
    public DbSet<Domain.Clinical.ClientError> ClientErrors => Set<Domain.Clinical.ClientError>();

    /// <summary>
    /// Read by the global query filters on every query. EF re-evaluates it per execution,
    /// so one compiled model serves every tenant.
    /// </summary>
    public Guid? CurrentTenantId => _hasTenantOverride ? _tenantOverride : currentUser.TenantId;

    public IDisposable BeginTenantScope(Guid? tenantId)
    {
        var previous = (_hasTenantOverride, _tenantOverride);
        _hasTenantOverride = true;
        _tenantOverride = tenantId;
        return new Restore(() => (_hasTenantOverride, _tenantOverride) = previous);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ClinicDbContext).Assembly);
        ApplyGlobalFilters(modelBuilder);

        // Join rows follow their tenant-owned parent so direct queries cannot see another tenant's links.
        modelBuilder.Entity<UserRole>().HasQueryFilter(ur => !ur.Role!.IsDeleted && ur.Role.TenantId == CurrentTenantId);
    }

    /// <summary>
    /// FR-API-03: TenantId == current tenant AND not soft-deleted, on every tenant entity.
    /// With no tenant (anonymous or Super Admin) the filter matches nothing.
    /// </summary>
    private void ApplyGlobalFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;
            var isTenant = typeof(ITenantEntity).IsAssignableFrom(clr);
            var isSoftDelete = typeof(ISoftDelete).IsAssignableFrom(clr);
            if (!isTenant && !isSoftDelete) continue;

            var e = Expression.Parameter(clr, "e");
            Expression? body = null;

            if (isTenant)
            {
                var tenantId = Expression.Convert(Expression.Property(e, nameof(ITenantEntity.TenantId)), typeof(Guid?));
                var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
                body = Expression.Equal(tenantId, current);
            }

            if (isSoftDelete)
            {
                var notDeleted = Expression.Not(Expression.Property(e, nameof(ISoftDelete.IsDeleted)));
                body = body is null ? notDeleted : Expression.AndAlso(body, notDeleted);
            }

            modelBuilder.Entity(clr).HasQueryFilter(Expression.Lambda(body!, e));
        }
    }

    // =====================================================================
    // Save pipeline: tenant guard → stamps/soft delete → sync sequence + ChangeLog → audit → save
    // =====================================================================

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        ChangeTracker.DetectChanges();

        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            GuardTenant(entry);
            StampAndSoftDelete(entry, now);
        }

        var syncEntries = ChangeTracker.Entries()
            .Where(e => e.Entity is ISyncEntity && e.State is EntityState.Added or EntityState.Modified && !OnlyHiddenChanges(e))
            .ToList();

        if (syncEntries.Count == 0)
        {
            AddAuditRows(now);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        // ChangeLog rows describe what is being saved, so compute them before anything else changes state.
        var changes = syncEntries.Select(Describe).ToList();

        var branches = changes.Select(c => c.Entity.GetSyncBranchId()).Distinct().ToList();
        var tenantForNotify = CurrentTenantId;

        if (Database.CurrentTransaction is not null)
        {
            var saved = await SaveWithSequencesAsync(changes, now, acceptAllChangesOnSuccess, cancellationToken);
            Notify(tenantForNotify, branches); // notifier waits a moment, so the outer transaction can commit first
            return saved;
        }

        // The sequence must be reserved and used in one transaction so sequences commit in order.
        var added = new List<object>();
        var strategy = Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            foreach (var row in added) Entry(row).State = EntityState.Detached; // from a failed earlier attempt
            added.Clear();

            await using var tx = await Database.BeginTransactionAsync(cancellationToken);
            var result = await SaveWithSequencesAsync(changes, now, acceptAllChangesOnSuccess, cancellationToken, added);
            await tx.CommitAsync(cancellationToken);
            Notify(tenantForNotify, branches);
            return result;
        });
    }

    /// <summary>FR-SYN-03: tell connected devices of these branches that there is something to pull.</summary>
    private void Notify(Guid? tenantId, IReadOnlyCollection<Guid?> branches)
    {
        if (tenantId is not { } t) return;
        foreach (var n in notifiers) n.Notify(t, branches);
    }

    private sealed record PendingChange(ISyncEntity Entity, string EntityType, string Operation, string Fields);

    private async Task<int> SaveWithSequencesAsync(
        List<PendingChange> changes, DateTime now, bool acceptAll, CancellationToken ct, List<object>? added = null)
    {
        var tenantId = CurrentTenantId ?? throw new InvalidOperationException("Sync entities need a tenant.");
        var first = await ReserveSequencesAsync(tenantId, changes.Count, ct);

        for (var i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            var sequence = first + i;
            c.Entity.RowVersion = sequence;

            var log = new ChangeLog
            {
                TenantId = tenantId,
                Sequence = sequence,
                EntityType = c.EntityType,
                RecordId = c.Entity.Id,
                BranchId = c.Entity.GetSyncBranchId(),
                Operation = c.Operation,
                ChangedFields = c.Fields,
                DeviceId = currentUser.DeviceId,
                UserId = currentUser.UserId,
                At = now,
            };
            ChangeLogs.Add(log);
            added?.Add(log);
        }

        var audits = AddAuditRows(now);
        added?.AddRange(audits);
        return await base.SaveChangesAsync(acceptAll, ct);
    }

    /// <summary>
    /// A save that only touches properties devices never see (e.g. the DynamicData snapshot) needs no new
    /// sequence: devices would pull the row and find nothing new.
    /// </summary>
    private static bool OnlyHiddenChanges(EntityEntry entry)
    {
        if (entry.State != EntityState.Modified) return false;
        var hidden = SyncRegistry.ForType(entry.Metadata.ClrType).HiddenFields;
        if (hidden.Count == 0) return false;

        var changed = entry.Properties
            .Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue) && !BookkeepingProperties.Contains(p.Metadata.Name))
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Metadata.Name))
            .ToList();
        return changed.Count > 0 && changed.All(hidden.Contains);
    }

    private static PendingChange Describe(EntityEntry entry)
    {
        var entity = (ISyncEntity)entry.Entity;
        var name = SyncRegistry.ForType(entry.Metadata.ClrType).Name;

        IEnumerable<PropertyEntry> props = entry.State == EntityState.Added
            ? entry.Properties
            : entry.Properties.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue));

        var fields = props
            .Select(p => p.Metadata.Name)
            .Where(n => !BookkeepingProperties.Contains(n) && n != nameof(ITenantEntity.TenantId))
            .Select(JsonNamingPolicy.CamelCase.ConvertName);

        var operation = entry.State == EntityState.Added ? "Insert"
            : entity.IsDeleted && entry.Property(nameof(ISoftDelete.IsDeleted)).IsModified ? "Delete"
            : "Update";

        return new PendingChange(entity, name, operation, string.Join(",", fields));
    }

    /// <summary>
    /// Adds <paramref name="count"/> to the tenant's ChangeSequence and returns the first new value.
    /// On SQL Server the UPDATE takes a row lock held until commit, so concurrent savers queue up and
    /// sequences become visible in order (a pull checkpoint can never skip a late commit).
    /// </summary>
    private async Task<long> ReserveSequencesAsync(Guid tenantId, int count, CancellationToken ct)
    {
        if (Database.IsSqlServer())
        {
            for (var attempt = 0; ; attempt++)
            {
                var updated = await Database
                    .SqlQuery<long>($"UPDATE dbo.SyncCounters SET Value = Value + {count} OUTPUT inserted.Value WHERE TenantId = {tenantId}")
                    .ToListAsync(ct);
                if (updated.Count == 1) return updated[0] - count + 1;

                try
                {
                    await Database.ExecuteSqlAsync($"INSERT INTO dbo.SyncCounters (TenantId, Value, PurgedThrough) VALUES ({tenantId}, {count}, 0)", ct);
                    return 1;
                }
                catch (Exception) when (attempt == 0)
                {
                    // Another transaction created the row first; loop and increment it instead.
                }
            }
        }

        // SQLite (tests, single writer): read-modify-write inside the transaction.
        var counter = await SyncCounters.FindAsync([tenantId], ct);
        if (counter is null)
        {
            counter = new SyncCounter { TenantId = tenantId };
            SyncCounters.Add(counter);
        }
        var firstValue = counter.Value + 1;
        counter.Value += count;
        return firstValue;
    }

    public async Task<long> ReserveNumbersAsync(string kind, int count, CancellationToken ct = default)
    {
        var tenantId = CurrentTenantId ?? throw new InvalidOperationException("Number ranges need a tenant.");
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException("ReserveNumbersAsync must run inside a transaction.");

        if (Database.IsSqlServer())
        {
            for (var attempt = 0; ; attempt++)
            {
                var updated = await Database
                    .SqlQuery<long>($"UPDATE dbo.NumberCounters SET NextValue = NextValue + {count} OUTPUT deleted.NextValue AS Value WHERE TenantId = {tenantId} AND Kind = {kind}")
                    .ToListAsync(ct);
                if (updated.Count == 1) return updated[0];

                try
                {
                    await Database.ExecuteSqlAsync($"INSERT INTO dbo.NumberCounters (TenantId, Kind, NextValue) VALUES ({tenantId}, {kind}, {1 + count})", ct);
                    return 1;
                }
                catch (Exception) when (attempt == 0) { }
            }
        }

        var counter = await NumberCounters.FindAsync([tenantId, kind], ct);
        if (counter is null)
        {
            counter = new NumberCounter { TenantId = tenantId, Kind = kind, NextValue = 1 };
            NumberCounters.Add(counter);
        }
        var start = counter.NextValue;
        counter.NextValue += count;
        await base.SaveChangesAsync(ct);
        return start;
    }

    /// <summary>Refuse any insert, update or delete for a tenant other than the current one.</summary>
    private void GuardTenant(EntityEntry entry)
    {
        if (entry.Entity is not ITenantEntity tenantEntity) return;
        if (entry.State is EntityState.Unchanged or EntityState.Detached) return;

        var current = CurrentTenantId;

        if (entry.State == EntityState.Added)
        {
            if (tenantEntity.TenantId == Guid.Empty)
            {
                tenantEntity.TenantId = current
                    ?? throw new InvalidOperationException($"Cannot create {entry.Metadata.ClrType.Name} without a tenant.");
            }
            else if (tenantEntity.TenantId != current)
            {
                throw new InvalidOperationException($"Cross-tenant insert of {entry.Metadata.ClrType.Name} refused.");
            }
            return;
        }

        var original = entry.Property(nameof(ITenantEntity.TenantId)).OriginalValue as Guid?;
        if (original != current || tenantEntity.TenantId != current)
            throw new InvalidOperationException($"Cross-tenant write to {entry.Metadata.ClrType.Name} refused.");
    }

    private void StampAndSoftDelete(EntityEntry entry, DateTime now)
    {
        if (entry.State == EntityState.Deleted && entry.Entity is ISoftDelete soft)
        {
            entry.State = EntityState.Modified;
            soft.IsDeleted = true;
        }

        if (entry.Entity is not Entity entity) return;

        switch (entry.State)
        {
            case EntityState.Added:
                if (entity.CreatedAt == default) entity.CreatedAt = now;
                entity.CreatedBy ??= currentUser.UserId;
                break;
            case EntityState.Modified:
                entity.UpdatedAt = now;
                entity.UpdatedBy = currentUser.UserId;
                entry.Property(nameof(Entity.CreatedAt)).IsModified = false;
                entry.Property(nameof(Entity.CreatedBy)).IsModified = false;
                break;
        }
    }

    /// <summary>FR-API-07 / FR-SEC-07: every write is audited with old and new values.</summary>
    private List<AuditLog> AddAuditRows(DateTime now)
    {
        var rows = new List<AuditLog>();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (UnauditedTypes.Contains(entry.Metadata.ClrType)) continue;
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            var old = new Dictionary<string, object?>();
            var @new = new Dictionary<string, object?>();

            foreach (var p in entry.Properties)
            {
                var name = p.Metadata.Name;
                if (BookkeepingProperties.Contains(name)) continue;

                object? Mask(object? v) => MaskedProperties.Contains(name) && v is not null ? "***" : v;

                switch (entry.State)
                {
                    case EntityState.Added:
                        @new[name] = Mask(p.CurrentValue);
                        break;
                    case EntityState.Deleted:
                        old[name] = Mask(p.OriginalValue);
                        break;
                    case EntityState.Modified when p.IsModified && !Equals(p.OriginalValue, p.CurrentValue):
                        old[name] = Mask(p.OriginalValue);
                        @new[name] = Mask(p.CurrentValue);
                        break;
                }
            }

            if (entry.State == EntityState.Modified && @new.Keys.All(HeartbeatProperties.Contains)) continue;

            var action = entry.State switch
            {
                EntityState.Added => "Create",
                EntityState.Deleted => "Delete",
                _ when entry.Entity is ISoftDelete { IsDeleted: true } && @new.ContainsKey(nameof(ISoftDelete.IsDeleted)) => "Delete",
                _ => "Update",
            };

            var key = entry.Metadata.FindPrimaryKey()!.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString());

            rows.Add(new AuditLog
            {
                TenantId = (entry.Entity as ITenantEntity)?.TenantId ?? CurrentTenantId,
                UserId = currentUser.UserId,
                DeviceId = currentUser.DeviceId,
                Action = action,
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = string.Join("|", key),
                OldValues = old.Count == 0 ? null : JsonSerializer.Serialize(old),
                NewValues = @new.Count == 0 ? null : JsonSerializer.Serialize(@new),
                Timestamp = now,
            });
        }

        AuditLogs.AddRange(rows);
        return rows;
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose()
        {
            _restore?.Invoke();
            _restore = null;
        }
    }
}

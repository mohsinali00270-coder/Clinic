using Clinic.Domain.Devices;
using Clinic.Domain.Notes;
using Clinic.Domain.Sync;
using Clinic.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Infrastructure.Persistence;

internal sealed class ChangeLogConfiguration : IEntityTypeConfiguration<ChangeLog>
{
    public void Configure(EntityTypeBuilder<ChangeLog> b)
    {
        b.HasKey(x => new { x.TenantId, x.Sequence });
        b.Property(x => x.Sequence).ValueGeneratedNever();
        b.Property(x => x.EntityType).HasMaxLength(60).IsRequired();
        b.Property(x => x.Operation).HasMaxLength(10).IsRequired();
        b.Property(x => x.ChangedFields).HasMaxLength(4000).IsRequired();
        // Field-level conflict detection looks up a record's changes after a base version.
        b.HasIndex(x => new { x.TenantId, x.RecordId, x.Sequence });
    }
}

internal sealed class SyncCounterConfiguration : IEntityTypeConfiguration<SyncCounter>
{
    public void Configure(EntityTypeBuilder<SyncCounter> b) => b.HasKey(x => x.TenantId);
}

internal sealed class SyncLogConfiguration : IEntityTypeConfiguration<SyncLog>
{
    public void Configure(EntityTypeBuilder<SyncLog> b)
    {
        b.HasKey(x => x.ChangeId);
        b.Property(x => x.ChangeId).ValueGeneratedNever();
        b.Property(x => x.EntityType).HasMaxLength(60).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.DeviceId, x.ProcessedAt });
    }
}

internal sealed class NumberCounterConfiguration : IEntityTypeConfiguration<NumberCounter>
{
    public void Configure(EntityTypeBuilder<NumberCounter> b)
    {
        b.HasKey(x => new { x.TenantId, x.Kind });
        b.Property(x => x.Kind).HasMaxLength(20);
    }
}

internal sealed class NumberRangeConfiguration : IEntityTypeConfiguration<NumberRange>
{
    public void Configure(EntityTypeBuilder<NumberRange> b)
    {
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Kind, x.StartValue }).IsUnique();
    }
}

internal sealed class BranchNoteConfiguration : IEntityTypeConfiguration<BranchNote>
{
    public void Configure(EntityTypeBuilder<BranchNote> b)
    {
        // Created on devices: the device's GUID is the key.
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Body).HasMaxLength(4000);
        b.Property(x => x.Color).HasMaxLength(20);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.BranchId });
    }
}

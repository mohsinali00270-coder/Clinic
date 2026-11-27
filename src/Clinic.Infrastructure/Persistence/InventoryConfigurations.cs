using Clinic.Domain.Clinical;
using Clinic.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Infrastructure.Persistence;

internal sealed class InventoryItemConfiguration : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> b)
    {
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Category).HasMaxLength(20).IsRequired();
        b.Property(x => x.Unit).HasMaxLength(20).IsRequired();
        b.Property(x => x.ReorderLevel).HasPrecision(12, 2);
        b.Property(x => x.SalePrice).HasPrecision(12, 2);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.DrugId });
    }
}

internal sealed class StockBatchConfiguration : IEntityTypeConfiguration<StockBatch>
{
    public void Configure(EntityTypeBuilder<StockBatch> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.BatchNo).HasMaxLength(40).IsRequired();
        b.Property(x => x.UnitCost).HasPrecision(12, 2);
        b.Property(x => x.Supplier).HasMaxLength(150);
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.ItemId });
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(12, 2);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasOne<StockBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.ItemId });
        b.HasIndex(x => new { x.TenantId, x.BatchId });
    }
}

internal sealed class DispensationConfiguration : IEntityTypeConfiguration<Dispensation>
{
    public void Configure(EntityTypeBuilder<Dispensation> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ItemName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(12, 2);
        b.Property(x => x.UnitPrice).HasPrecision(12, 2);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.PatientId });
        b.HasIndex(x => new { x.TenantId, x.PrescriptionItemId });
    }
}

internal sealed class PatientMergeConfiguration : IEntityTypeConfiguration<PatientMerge>
{
    public void Configure(EntityTypeBuilder<PatientMerge> b)
    {
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasIndex(x => new { x.TenantId, x.MergedId }).IsUnique();
    }
}

internal sealed class AccessEventConfiguration : IEntityTypeConfiguration<AccessEvent>
{
    public void Configure(EntityTypeBuilder<AccessEvent> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.EntityType).HasMaxLength(60).IsRequired();
        b.Property(x => x.Action).HasMaxLength(30).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.RecordId });
    }
}

internal sealed class ClientErrorConfiguration : IEntityTypeConfiguration<ClientError>
{
    public void Configure(EntityTypeBuilder<ClientError> b)
    {
        b.Property(x => x.Kind).HasMaxLength(40).IsRequired();
        b.Property(x => x.Message).HasMaxLength(1000).IsRequired();
        b.Property(x => x.AppVersion).HasMaxLength(30);
        b.Property(x => x.Platform).HasMaxLength(30);
        b.HasIndex(x => new { x.TenantId, x.OccurredAt });
    }
}

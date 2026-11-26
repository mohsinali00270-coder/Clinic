using Clinic.Domain.Forms;
using Clinic.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Infrastructure.Persistence;

internal sealed class FormConfiguration : IEntityTypeConfiguration<Form>
{
    public void Configure(EntityTypeBuilder<Form> b)
    {
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(60).IsRequired();
        b.Property(x => x.ContextSpecialty).HasMaxLength(60);
        b.Property(x => x.DraftJson).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.ContextBranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.EntityType });
    }
}

internal sealed class FormVersionConfiguration : IEntityTypeConfiguration<FormVersion>
{
    public void Configure(EntityTypeBuilder<FormVersion> b)
    {
        b.Property(x => x.EntityType).HasMaxLength(60).IsRequired();
        b.Property(x => x.ContextSpecialty).HasMaxLength(60);
        b.Property(x => x.DefinitionJson).IsRequired();
        b.Property(x => x.PublishedByName).HasMaxLength(200);
        b.HasOne<Form>().WithMany().HasForeignKey(x => x.FormId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.FormId, x.VersionNo }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.EntityType, x.IsCurrent });
    }
}

internal sealed class LookupListConfiguration : IEntityTypeConfiguration<LookupList>
{
    public void Configure(EntityTypeBuilder<LookupList> b)
    {
        b.Property(x => x.Key).HasMaxLength(50).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.ParentListKey).HasMaxLength(50);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class LookupItemConfiguration : IEntityTypeConfiguration<LookupItem>
{
    public void Configure(EntityTypeBuilder<LookupItem> b)
    {
        b.Property(x => x.ListKey).HasMaxLength(50).IsRequired();
        b.Property(x => x.Code).HasMaxLength(50).IsRequired();
        b.Property(x => x.LabelEn).HasMaxLength(200).IsRequired();
        b.Property(x => x.LabelUr).HasMaxLength(200);
        b.Property(x => x.ParentCode).HasMaxLength(50);
        b.HasOne<LookupList>().WithMany().HasForeignKey(x => x.ListId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.ListKey, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class FieldValueConfiguration : IEntityTypeConfiguration<FieldValue>
{
    public void Configure(EntityTypeBuilder<FieldValue> b)
    {
        // Deterministic ids from (record, field) are generated on devices and the server alike.
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.EntityType).HasMaxLength(60).IsRequired();
        b.Property(x => x.FieldKey).HasMaxLength(50).IsRequired();
        b.Property(x => x.ValueKind).HasMaxLength(10).IsRequired();
        b.Property(x => x.ValueText).HasMaxLength(4000);
        b.Property(x => x.ValueNumber).HasPrecision(18, 6);
        b.HasOne<FormVersion>().WithMany().HasForeignKey(x => x.FormVersionId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.RecordId });
        // FR-DATA-04: searchable / reportable values by field.
        b.HasIndex(x => new { x.TenantId, x.EntityType, x.FieldKey, x.ValueNumber });
        b.HasIndex(x => new { x.TenantId, x.EntityType, x.FieldKey, x.ValueDate });
    }
}

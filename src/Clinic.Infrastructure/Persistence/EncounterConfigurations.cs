using Clinic.Domain.Clinical;
using Clinic.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Infrastructure.Persistence;

internal sealed class EncounterConfiguration : IEntityTypeConfiguration<Encounter>
{
    public void Configure(EntityTypeBuilder<Encounter> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Specialty).HasMaxLength(50);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.PatientGender).HasMaxLength(1);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DoctorProfile>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.PatientId });
        b.HasIndex(x => new { x.TenantId, x.DoctorId, x.StartedAt });
    }
}

internal sealed class VitalsConfiguration : IEntityTypeConfiguration<VitalsRecord>
{
    public void Configure(EntityTypeBuilder<VitalsRecord> b)
    {
        b.ToTable("Vitals");
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PatientGender).HasMaxLength(1);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.PatientId, x.RecordedAt });
    }
}

internal sealed class DiagnosisConfiguration : IEntityTypeConfiguration<Diagnosis>
{
    public void Configure(EntityTypeBuilder<Diagnosis> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).HasMaxLength(10);
        b.Property(x => x.Description).HasMaxLength(300).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<Encounter>().WithMany().HasForeignKey(x => x.EncounterId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Code }); // top diagnoses report (FR-REP-01)
    }
}

internal sealed class Icd10Configuration : IEntityTypeConfiguration<Icd10Code>
{
    public void Configure(EntityTypeBuilder<Icd10Code> b)
    {
        b.Property(x => x.Code).HasMaxLength(10).IsRequired();
        b.Property(x => x.Description).HasMaxLength(300).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class AmendmentConfiguration : IEntityTypeConfiguration<EncounterAmendment>
{
    public void Configure(EntityTypeBuilder<EncounterAmendment> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.FieldKey).HasMaxLength(60).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.HasOne<Encounter>().WithMany().HasForeignKey(x => x.EncounterId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.EncounterId });
    }
}

internal sealed class DrugConfiguration : IEntityTypeConfiguration<Drug>
{
    public void Configure(EntityTypeBuilder<Drug> b)
    {
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Generic).HasMaxLength(200);
        b.Property(x => x.Strength).HasMaxLength(50);
        b.Property(x => x.Form).HasMaxLength(50);
        b.Property(x => x.Route).HasMaxLength(30);
        b.Property(x => x.DefaultDose).HasMaxLength(100);
        b.Property(x => x.DefaultFrequency).HasMaxLength(20);
        b.Property(x => x.AllergyClass).HasMaxLength(50);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Name });
    }
}

internal sealed class PrescriptionConfiguration : IEntityTypeConfiguration<Prescription>
{
    public void Configure(EntityTypeBuilder<Prescription> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasOne<Encounter>().WithMany().HasForeignKey(x => x.EncounterId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.PatientId });
    }
}

internal sealed class PrescriptionItemConfiguration : IEntityTypeConfiguration<PrescriptionItem>
{
    public void Configure(EntityTypeBuilder<PrescriptionItem> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.DrugName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Strength).HasMaxLength(50);
        b.Property(x => x.Form).HasMaxLength(50);
        b.Property(x => x.Dose).HasMaxLength(100);
        b.Property(x => x.Frequency).HasMaxLength(20);
        b.Property(x => x.Route).HasMaxLength(30);
        b.Property(x => x.Instructions).HasMaxLength(500);
        b.HasOne<Prescription>().WithMany().HasForeignKey(x => x.PrescriptionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PrescriptionTemplateConfiguration : IEntityTypeConfiguration<PrescriptionTemplate>
{
    public void Configure(EntityTypeBuilder<PrescriptionTemplate> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasOne<DoctorProfile>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
    }
}

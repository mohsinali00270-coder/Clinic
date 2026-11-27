using Clinic.Domain.Clinical;
using Clinic.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Infrastructure.Persistence;

internal sealed class CaseTypeConfiguration : IEntityTypeConfiguration<CaseType>
{
    public void Configure(EntityTypeBuilder<CaseType> b)
    {
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.NameUr).HasMaxLength(150);
        b.Property(x => x.Specialty).HasMaxLength(50);
        b.Property(x => x.DefaultServices).HasMaxLength(500);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class PatientCaseConfiguration : IEntityTypeConfiguration<PatientCase>
{
    public void Configure(EntityTypeBuilder<PatientCase> b)
    {
        b.ToTable("Cases");
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.CaseTypeCode).HasMaxLength(40);
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Outcome).HasMaxLength(50);
        b.Property(x => x.ClosingSummary).HasMaxLength(4000);
        b.Property(x => x.PatientGender).HasMaxLength(1);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CaseType>().WithMany().HasForeignKey(x => x.CaseTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.PatientId });
        b.HasIndex(x => new { x.TenantId, x.Status });
    }
}

internal sealed class CarePlanStepConfiguration : IEntityTypeConfiguration<CarePlanStep>
{
    public void Configure(EntityTypeBuilder<CarePlanStep> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<PatientCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Status, x.DueDate }); // overdue steps / follow-up list
    }
}

internal sealed class CaseMemberConfiguration : IEntityTypeConfiguration<CaseMember>
{
    public void Configure(EntityTypeBuilder<CaseMember> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Role).HasMaxLength(20).IsRequired();
        b.HasOne<PatientCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CaseNoteConfiguration : IEntityTypeConfiguration<CaseNote>
{
    public void Configure(EntityTypeBuilder<CaseNote> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Body).HasMaxLength(4000).IsRequired();
        b.Property(x => x.AuthorName).HasMaxLength(150);
        b.HasOne<PatientCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReferralConfiguration : IEntityTypeConfiguration<Referral>
{
    public void Configure(EntityTypeBuilder<Referral> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Direction).HasMaxLength(20).IsRequired();
        b.Property(x => x.ToSpecialty).HasMaxLength(50);
        b.Property(x => x.ExternalFacility).HasMaxLength(200);
        b.Property(x => x.ExternalDoctor).HasMaxLength(150);
        b.Property(x => x.SourceCode).HasMaxLength(50);
        b.Property(x => x.SourceName).HasMaxLength(200);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Priority).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.ResponseNote).HasMaxLength(1000);
        b.Property(x => x.Summary).HasMaxLength(4000);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.ToDoctorId, x.Status }); // receiving doctor's dashboard
        b.HasIndex(x => new { x.TenantId, x.Direction, x.SourceCode }); // referral reports (FR-REF-03)
    }
}

internal sealed class ProcedureTypeConfiguration : IEntityTypeConfiguration<ProcedureType>
{
    public void Configure(EntityTypeBuilder<ProcedureType> b)
    {
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Category).HasMaxLength(30).IsRequired();
        b.Property(x => x.Fee).HasPrecision(12, 2);
        b.Property(x => x.ConsentTextEn).HasMaxLength(4000);
        b.Property(x => x.ConsentTextUr).HasMaxLength(4000);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class ProcedureRecordConfiguration : IEntityTypeConfiguration<ProcedureRecord>
{
    public void Configure(EntityTypeBuilder<ProcedureRecord> b)
    {
        b.ToTable("Procedures");
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ProcedureCode).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Category).HasMaxLength(30);
        b.Property(x => x.Fee).HasPrecision(12, 2);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.ConsentSignerName).HasMaxLength(150);
        b.Property(x => x.ConsentRelation).HasMaxLength(20);
        b.Property(x => x.ConsentWitness).HasMaxLength(150);
        b.Property(x => x.PatientGender).HasMaxLength(1);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProcedureType>().WithMany().HasForeignKey(x => x.ProcedureTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.PatientId });
        b.HasIndex(x => new { x.TenantId, x.CaseId });
    }
}

internal sealed class BedConfiguration : IEntityTypeConfiguration<Bed>
{
    public void Configure(EntityTypeBuilder<Bed> b)
    {
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Ward).HasMaxLength(100);
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.ChargeAmount).HasPrecision(12, 2);
        b.Property(x => x.ChargeUnit).HasMaxLength(10).IsRequired();
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class AdmissionConfiguration : IEntityTypeConfiguration<Admission>
{
    public void Configure(EntityTypeBuilder<Admission> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.DischargeSummary).HasMaxLength(4000);
        b.Property(x => x.DischargeAdvice).HasMaxLength(2000);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bed>().WithMany().HasForeignKey(x => x.BedId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Status });
    }
}

internal sealed class BedTransferConfiguration : IEntityTypeConfiguration<BedTransfer>
{
    public void Configure(EntityTypeBuilder<BedTransfer> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class NursingNoteConfiguration : IEntityTypeConfiguration<NursingNote>
{
    public void Configure(EntityTypeBuilder<NursingNote> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.Body).HasMaxLength(4000).IsRequired();
        b.Property(x => x.AuthorName).HasMaxLength(150);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MedicationOrderConfiguration : IEntityTypeConfiguration<MedicationOrder>
{
    public void Configure(EntityTypeBuilder<MedicationOrder> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.DrugName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Dose).HasMaxLength(100);
        b.Property(x => x.Route).HasMaxLength(30);
        b.Property(x => x.Frequency).HasMaxLength(20);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Instructions).HasMaxLength(500);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MedicationDoseConfiguration : IEntityTypeConfiguration<MedicationDose>
{
    public void Configure(EntityTypeBuilder<MedicationDose> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasOne<MedicationOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

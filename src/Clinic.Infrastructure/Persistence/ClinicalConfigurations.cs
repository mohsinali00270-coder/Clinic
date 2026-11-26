using Clinic.Domain.Clinical;
using Clinic.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Infrastructure.Persistence;

internal sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever(); // created on devices
        b.Property(x => x.Mrn).HasMaxLength(40).IsRequired();
        b.Property(x => x.FullName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Gender).HasMaxLength(1).IsRequired();
        b.Property(x => x.Phone).HasMaxLength(30);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Mrn }).IsUnique().HasFilter("[IsDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.Phone });
        b.HasIndex(x => new { x.TenantId, x.FullName });
    }
}

internal sealed class DoctorProfileConfiguration : IEntityTypeConfiguration<DoctorProfile>
{
    public void Configure(EntityTypeBuilder<DoctorProfile> b)
    {
        b.Property(x => x.FullName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Specialty).HasMaxLength(50).IsRequired();
        b.Property(x => x.Gender).HasMaxLength(1);
        b.Property(x => x.RegistrationNo).HasMaxLength(50);
        b.Property(x => x.DoctorType).HasMaxLength(20).IsRequired();
        b.Property(x => x.ConsultationFee).HasPrecision(18, 2);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.UserId });
    }
}

internal sealed class DoctorScheduleConfiguration : IEntityTypeConfiguration<DoctorSchedule>
{
    public void Configure(EntityTypeBuilder<DoctorSchedule> b)
    {
        b.HasOne<DoctorProfile>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.DoctorId, x.DayOfWeek });
    }
}

internal sealed class DoctorLeaveConfiguration : IEntityTypeConfiguration<DoctorLeave>
{
    public void Configure(EntityTypeBuilder<DoctorLeave> b)
    {
        b.Property(x => x.Reason).HasMaxLength(200);
        b.HasOne<DoctorProfile>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.DoctorId, x.StartDate });
    }
}

internal sealed class BranchClosureConfiguration : IEntityTypeConfiguration<BranchClosure>
{
    public void Configure(EntityTypeBuilder<BranchClosure> b)
    {
        b.Property(x => x.Reason).HasMaxLength(200);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Date });
    }
}

internal sealed class AppointmentTypeConfiguration : IEntityTypeConfiguration<AppointmentType>
{
    public void Configure(EntityTypeBuilder<AppointmentType> b)
    {
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Color).HasMaxLength(20);
        b.Property(x => x.Fee).HasPrecision(18, 2);
        b.Property(x => x.PreparationInstructions).HasMaxLength(1000);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Channel).HasMaxLength(20).IsRequired();
        b.Property(x => x.Priority).HasMaxLength(20).IsRequired();
        b.Property(x => x.CancelReason).HasMaxLength(200);
        b.Property(x => x.CancelledBy).HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DoctorProfile>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppointmentType>().WithMany().HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.DoctorId, x.Date, x.StartTime });
        b.HasIndex(x => new { x.TenantId, x.PatientId });
    }
}

internal sealed class AppointmentStatusHistoryConfiguration : IEntityTypeConfiguration<AppointmentStatusHistory>
{
    public void Configure(EntityTypeBuilder<AppointmentStatusHistory> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.FromStatus).HasMaxLength(20);
        b.Property(x => x.ToStatus).HasMaxLength(20).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(200);
        b.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.AppointmentId });
    }
}

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.EntityType).HasMaxLength(60).IsRequired();
        b.Property(x => x.FileName).HasMaxLength(200).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.Caption).HasMaxLength(200);
        b.Property(x => x.Category).HasMaxLength(30).IsRequired();
        b.Property(x => x.StorageKey).HasMaxLength(300);
        b.HasIndex(x => new { x.TenantId, x.RecordId });
    }
}

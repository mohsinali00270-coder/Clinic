using Clinic.Domain.Clinical;
using Clinic.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Infrastructure.Persistence;

internal sealed class PackagePlanConfiguration : IEntityTypeConfiguration<PackagePlan>
{
    public void Configure(EntityTypeBuilder<PackagePlan> b)
    {
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Price).HasPrecision(12, 2);
        b.Property(x => x.CaseTypeCode).HasMaxLength(40);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class PatientPackageConfiguration : IEntityTypeConfiguration<PatientPackage>
{
    public void Configure(EntityTypeBuilder<PatientPackage> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Price).HasPrecision(12, 2);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PackagePlan>().WithMany().HasForeignKey(x => x.PackagePlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.PatientId });
    }
}

internal sealed class PackageUsageConfiguration : IEntityTypeConfiguration<PackageUsage>
{
    public void Configure(EntityTypeBuilder<PackageUsage> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();
        b.HasOne<PatientPackage>().WithMany().HasForeignKey(x => x.PatientPackageId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Source, x.SourceId });
    }
}

internal sealed class PayerConfiguration : IEntityTypeConfiguration<Payer>
{
    public void Configure(EntityTypeBuilder<Payer> b)
    {
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.DefaultCoPayPercent).HasPrecision(5, 2);
        b.Property(x => x.ContactEmail).HasMaxLength(200);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class PatientPanelConfiguration : IEntityTypeConfiguration<PatientPanel>
{
    public void Configure(EntityTypeBuilder<PatientPanel> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.MemberNo).HasMaxLength(60).IsRequired();
        b.Property(x => x.Employer).HasMaxLength(150);
        b.Property(x => x.Relation).HasMaxLength(20);
        b.Property(x => x.CoPayPercent).HasPrecision(5, 2);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Payer>().WithMany().HasForeignKey(x => x.PayerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.PatientId });
    }
}

internal sealed class PayerSettlementConfiguration : IEntityTypeConfiguration<PayerSettlement>
{
    public void Configure(EntityTypeBuilder<PayerSettlement> b)
    {
        b.Property(x => x.Amount).HasPrecision(14, 2);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<Payer>().WithMany().HasForeignKey(x => x.PayerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.PayerId });
    }
}

internal sealed class CreditNoteConfiguration : IEntityTypeConfiguration<CreditNote>
{
    public void Configure(EntityTypeBuilder<CreditNote> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.Amount).HasPrecision(14, 2);
        b.Property(x => x.Reason).HasMaxLength(300).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.DecisionNote).HasMaxLength(300);
        b.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Status });
    }
}

internal sealed class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Category).HasMaxLength(50).IsRequired();
        b.Property(x => x.Amount).HasPrecision(14, 2);
        b.Property(x => x.Method).HasMaxLength(30).IsRequired();
        b.Property(x => x.PaidTo).HasMaxLength(150);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.SpentOn });
    }
}

internal sealed class DoctorShareRuleConfiguration : IEntityTypeConfiguration<DoctorShareRule>
{
    public void Configure(EntityTypeBuilder<DoctorShareRule> b)
    {
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.Percent).HasPrecision(5, 2);
        b.Property(x => x.FixedAmount).HasPrecision(12, 2);
        b.HasOne<DoctorProfile>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.DoctorId, x.Kind });
    }
}

internal sealed class DoctorPayoutConfiguration : IEntityTypeConfiguration<DoctorPayout>
{
    public void Configure(EntityTypeBuilder<DoctorPayout> b)
    {
        b.Property(x => x.Collected).HasPrecision(14, 2);
        b.Property(x => x.Share).HasPrecision(14, 2);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.PaymentReference).HasMaxLength(100);
        b.HasOne<DoctorProfile>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.DoctorId, x.From });
    }
}

internal sealed class MessageTemplateConfiguration : IEntityTypeConfiguration<MessageTemplate>
{
    public void Configure(EntityTypeBuilder<MessageTemplate> b)
    {
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Channel).HasMaxLength(20).IsRequired();
        b.Property(x => x.BodyEn).HasMaxLength(1000).IsRequired();
        b.Property(x => x.BodyUr).HasMaxLength(1000);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Key, x.Channel }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class ContactPreferenceConfiguration : IEntityTypeConfiguration<ContactPreference>
{
    public void Configure(EntityTypeBuilder<ContactPreference> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Language).HasMaxLength(5).IsRequired();
        b.Property(x => x.Channel).HasMaxLength(20).IsRequired();
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.PatientId });
    }
}

internal sealed class OutboundMessageConfiguration : IEntityTypeConfiguration<OutboundMessage>
{
    public void Configure(EntityTypeBuilder<OutboundMessage> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Channel).HasMaxLength(20).IsRequired();
        b.Property(x => x.TemplateKey).HasMaxLength(40);
        b.Property(x => x.To).HasMaxLength(200).IsRequired();
        b.Property(x => x.Body).HasMaxLength(1600).IsRequired();
        b.Property(x => x.Language).HasMaxLength(5);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.ProviderMessageId).HasMaxLength(100);
        b.Property(x => x.Error).HasMaxLength(500);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.Status, x.QueuedAt }); // the sender's queue (all tenants)
        b.HasIndex(x => new { x.TenantId, x.PatientId });
    }
}

internal sealed class FeedbackConfiguration : IEntityTypeConfiguration<Feedback>
{
    public void Configure(EntityTypeBuilder<Feedback> b)
    {
        b.ToTable("Feedback");
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Comment).HasMaxLength(2000);
        b.Property(x => x.Channel).HasMaxLength(20).IsRequired();
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.DoctorId, x.SubmittedAt });
    }
}

internal sealed class ComplaintConfiguration : IEntityTypeConfiguration<Complaint>
{
    public void Configure(EntityTypeBuilder<Complaint> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        b.Property(x => x.Details).HasMaxLength(4000);
        b.Property(x => x.Category).HasMaxLength(50);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.AssignedName).HasMaxLength(150);
        b.Property(x => x.Resolution).HasMaxLength(4000);
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Status });
    }
}

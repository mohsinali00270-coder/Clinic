using Clinic.Domain.Clinical;
using Clinic.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Infrastructure.Persistence;

internal sealed class LabTestConfiguration : IEntityTypeConfiguration<LabTest>
{
    public void Configure(EntityTypeBuilder<LabTest> b)
    {
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Department).HasMaxLength(60);
        b.Property(x => x.SampleType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Fee).HasPrecision(12, 2);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class LabOrderConfiguration : IEntityTypeConfiguration<LabOrder>
{
    public void Configure(EntityTypeBuilder<LabOrder> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Priority).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.ClinicalNotes).HasMaxLength(1000);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.PatientId });
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Status });
    }
}

internal sealed class LabResultConfiguration : IEntityTypeConfiguration<LabResult>
{
    public void Configure(EntityTypeBuilder<LabResult> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TestCode).HasMaxLength(40);
        b.Property(x => x.TestName).HasMaxLength(150).IsRequired();
        b.Property(x => x.SampleType).HasMaxLength(20);
        b.Property(x => x.Fee).HasPrecision(12, 2);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.SampleNo).HasMaxLength(40);
        b.Property(x => x.Comment).HasMaxLength(2000);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.PatientGender).HasMaxLength(1);
        b.HasOne<LabOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LabTest>().WithMany().HasForeignKey(x => x.LabTestId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Status }); // lab work list
        b.HasIndex(x => new { x.TenantId, x.PatientId });
    }
}

internal sealed class ServiceItemConfiguration : IEntityTypeConfiguration<ServiceItem>
{
    public void Configure(EntityTypeBuilder<ServiceItem> b)
    {
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.Price).HasPrecision(12, 2);
        b.Property(x => x.TaxPercent).HasPrecision(5, 2);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Code, x.BranchId });
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.InvoiceNo).HasMaxLength(40).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Subtotal).HasPrecision(14, 2);
        b.Property(x => x.Discount).HasPrecision(14, 2);
        b.Property(x => x.Tax).HasPrecision(14, 2);
        b.Property(x => x.Total).HasPrecision(14, 2);
        b.Property(x => x.DiscountReason).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.VoidReason).HasMaxLength(300);
        b.Property(x => x.PayerAmount).HasPrecision(14, 2);
        b.Property(x => x.PatientAmount).HasPrecision(14, 2);
        b.HasIndex(x => new { x.TenantId, x.PayerId, x.IssuedAt }); // claims and payer statements
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.InvoiceNo }).IsUnique().HasFilter("[IsDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.PatientId });
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasMaxLength(200).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(10, 2);
        b.Property(x => x.UnitPrice).HasPrecision(12, 2);
        b.Property(x => x.Discount).HasPrecision(12, 2);
        b.Property(x => x.TaxPercent).HasPrecision(5, 2);
        b.Property(x => x.Amount).HasPrecision(14, 2);
        b.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Source, x.SourceId }); // no double billing (FR-BIL-02)
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.Method).HasMaxLength(30).IsRequired();
        b.Property(x => x.Amount).HasPrecision(14, 2);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.ReceiptNo).HasMaxLength(40);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.ReceivedAt }); // day closing (FR-BIL-06)
        b.HasIndex(x => new { x.TenantId, x.InvoiceId });
    }
}

internal sealed class CashClosingConfiguration : IEntityTypeConfiguration<CashClosing>
{
    public void Configure(EntityTypeBuilder<CashClosing> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ExpectedCash).HasPrecision(14, 2);
        b.Property(x => x.CountedCash).HasPrecision(14, 2);
        b.Property(x => x.ServerExpectedCash).HasPrecision(14, 2);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.UserId, x.BusinessDate });
    }
}

using System.Text.Json.Serialization;
using SQLite;

namespace Clinic.Client.Core.Local;

// §4.5 labs ------------------------------------------------------------------------------------------

[Table("LabTests")]
public sealed class LocalLabTest : LocalEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Department { get; set; }
    public string SampleType { get; set; } = "blood";
    public decimal? Fee { get; set; }
    public int TurnaroundHours { get; set; } = 24;
    public bool IsActive { get; set; } = true;
}

[Table("LabOrders")]
public sealed class LocalLabOrder : LocalEntity
{
    [Indexed]
    public Guid PatientId { get; set; }
    [Indexed]
    public Guid BranchId { get; set; }
    [Indexed]
    public Guid? EncounterId { get; set; }
    public Guid? CaseId { get; set; }
    public Guid? DoctorId { get; set; }
    public DateTime OrderedAt { get; set; }
    public Guid? OrderedBy { get; set; }
    public string Priority { get; set; } = "routine";
    public string? ClinicalNotes { get; set; }
    public string Status { get; set; } = "open";
}

[Table("LabResults")]
public sealed class LocalLabResult : LocalEntity, ILocalDynamic
{
    [Indexed]
    public Guid OrderId { get; set; }
    [Indexed]
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid LabTestId { get; set; }
    public string TestCode { get; set; } = "";
    public string TestName { get; set; } = "";
    public string SampleType { get; set; } = "blood";
    public decimal? Fee { get; set; }
    [Indexed]
    public string Status { get; set; } = "ordered";
    public string? SampleNo { get; set; }
    public DateTime? CollectedAt { get; set; }
    public Guid? CollectedBy { get; set; }
    public DateTime? ResultedAt { get; set; }
    public Guid? ResultedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public Guid? VerifiedBy { get; set; }
    public string? Comment { get; set; }
    public bool IsAbnormal { get; set; }
    public string? CancelReason { get; set; }
    public string? PatientGender { get; set; }
    public DateTime? PatientDateOfBirth { get; set; }
    public Guid? FormVersionId { get; set; }

    [JsonIgnore, LocalOnly]
    public string? DynamicData { get; set; }
}

// §4.6 billing ---------------------------------------------------------------------------------------

[Table("ServiceItems")]
public sealed class LocalServiceItem : LocalEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "other";
    public Guid? BranchId { get; set; }
    public decimal Price { get; set; }
    public decimal TaxPercent { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("Invoices")]
public sealed class LocalInvoice : LocalEntity
{
    [Indexed]
    public Guid PatientId { get; set; }
    [Indexed]
    public Guid BranchId { get; set; }
    public string InvoiceNo { get; set; } = "";
    public DateTime IssuedAt { get; set; }
    public string Status { get; set; } = "draft";
    public Guid? EncounterId { get; set; }
    public Guid? CaseId { get; set; }
    public Guid? AdmissionId { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public string? DiscountReason { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public string? VoidReason { get; set; }
    public Guid? PayerId { get; set; }
    public Guid? PanelId { get; set; }
    public decimal PayerAmount { get; set; }
    public decimal PatientAmount { get; set; }
}

[Table("InvoiceLines")]
public sealed class LocalInvoiceLine : LocalEntity
{
    [Indexed]
    public Guid InvoiceId { get; set; }
    public Guid BranchId { get; set; }
    public string Source { get; set; } = "manual";
    [Indexed]
    public Guid? SourceId { get; set; }
    public string Kind { get; set; } = "other";
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal Amount { get; set; }
    public Guid? DoctorId { get; set; }
    public int SortOrder { get; set; }
}

[Table("Payments")]
public sealed class LocalPayment : LocalEntity
{
    [Indexed]
    public Guid PatientId { get; set; }
    [Indexed]
    public Guid BranchId { get; set; }
    [Indexed]
    public Guid? InvoiceId { get; set; }
    public string Kind { get; set; } = "payment";
    public string Method { get; set; } = "cash";
    public decimal Amount { get; set; }
    public DateTime ReceivedAt { get; set; }
    public Guid ReceivedBy { get; set; }
    public string? Reference { get; set; }
    public string? ReceiptNo { get; set; }
    public string? Reason { get; set; }
}

[Table("CashClosings")]
public sealed class LocalCashClosing : LocalEntity
{
    public Guid BranchId { get; set; }
    [Indexed]
    public Guid UserId { get; set; }
    public DateTime BusinessDate { get; set; }
    public DateTime ClosedAt { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal CountedCash { get; set; }
    public string TotalsJson { get; set; } = "[]";
    public int PaymentCount { get; set; }
    public string? Notes { get; set; }

    /// <summary>Server-maintained after sync.</summary>
    public decimal ServerExpectedCash { get; set; }
    public bool Mismatch { get; set; }
}

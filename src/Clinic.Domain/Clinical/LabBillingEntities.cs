using Clinic.Domain.Forms;
using Clinic.Domain.Sync;

namespace Clinic.Domain.Clinical;

// =====================================================================================================
// §4.5 Labs
// =====================================================================================================

/// <summary>FR-LAB-01: test master (server wins). Parameters live in the LabResult form whose context is <see cref="Code"/>.</summary>
public class LabTest : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Department { get; set; }
    public string SampleType { get; set; } = "blood";
    public decimal? Fee { get; set; }
    public int TurnaroundHours { get; set; } = 24;
    public bool IsActive { get; set; } = true;
}

/// <summary>FR-LAB-02: an order of one or more tests, usually from a consultation.</summary>
public class LabOrder : SyncEntity
{
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid? CaseId { get; set; }
    public Guid? DoctorId { get; set; }
    public DateTime OrderedAt { get; set; }
    public Guid? OrderedBy { get; set; }
    public string Priority { get; set; } = "routine";
    public string? ClinicalNotes { get; set; }

    /// <summary>"open" until every test is verified or cancelled, then "complete".</summary>
    public string Status { get; set; } = "open";

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-LAB-01/02: one test of an order — sample, result values (dynamic form), verification.</summary>
public class LabResult : SyncEntity, IHasDynamicData
{
    public Guid OrderId { get; set; }
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid LabTestId { get; set; }
    public string TestCode { get; set; } = "";
    public string TestName { get; set; } = "";
    public string SampleType { get; set; } = "blood";
    public decimal? Fee { get; set; }
    public string Status { get; set; } = "ordered";
    public string? SampleNo { get; set; }
    public DateTime? CollectedAt { get; set; }
    public Guid? CollectedBy { get; set; }
    public DateTime? ResultedAt { get; set; }
    public Guid? ResultedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public Guid? VerifiedBy { get; set; }
    public string? Comment { get; set; }

    /// <summary>Any parameter outside its reference range (from the form's abnormal rules).</summary>
    public bool IsAbnormal { get; set; }
    public string? CancelReason { get; set; }

    public string? PatientGender { get; set; }
    public DateTime? PatientDateOfBirth { get; set; }
    public Guid? FormVersionId { get; set; }
    public string? DynamicData { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;

    public IReadOnlyDictionary<string, object?> GetFormVariables() =>
        new Dictionary<string, object?>(Clinic.Shared.Scheduling.PatientRules.FormVariables(PatientGender, PatientDateOfBirth, DateTime.Today))
        {
            ["test"] = TestCode,
        };
}

// =====================================================================================================
// §4.6 Billing
// =====================================================================================================

/// <summary>FR-BIL-01: a billable service, tenant-wide or for one branch (server wins).</summary>
public class ServiceItem : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "other";
    public Guid? BranchId { get; set; }
    public decimal Price { get; set; }
    public decimal TaxPercent { get; set; }
    public bool IsActive { get; set; } = true;

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-BIL-02/05: an invoice, numbered offline from the device's leased range.</summary>
public class Invoice : SyncEntity
{
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public string InvoiceNo { get; set; } = "";
    public DateTime IssuedAt { get; set; }
    public string Status { get; set; } = "draft";
    public Guid? EncounterId { get; set; }
    public Guid? CaseId { get; set; }
    public Guid? AdmissionId { get; set; }
    public decimal Subtotal { get; set; }

    /// <summary>All discounts (line + invoice); FR-BIL-03 needs Billing.Discount.</summary>
    public decimal Discount { get; set; }
    public string? DiscountReason { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public string? VoidReason { get; set; }

    /// <summary>FR-BIL-12: panel / insurance billing — who pays what of the total.</summary>
    public Guid? PayerId { get; set; }
    public Guid? PanelId { get; set; }
    public decimal PayerAmount { get; set; }

    /// <summary>What the patient pays (the total when there is no payer).</summary>
    public decimal PatientAmount { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

public class InvoiceLine : SyncEntity
{
    public Guid InvoiceId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>appointment / procedure / lab / admission / service / manual (FR-BIL-02).</summary>
    public string Source { get; set; } = "manual";
    public Guid? SourceId { get; set; }
    public string Kind { get; set; } = "other";
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal Amount { get; set; }

    /// <summary>The doctor the charge belongs to (doctor share, Phase 6b).</summary>
    public Guid? DoctorId { get; set; }
    public int SortOrder { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>
/// FR-BIL-03/04/13: a payment, refund or deposit. Insert-only, like a ledger: corrections are new rows, so offline
/// devices never overwrite each other's money.
/// </summary>
public class Payment : SyncEntity
{
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? InvoiceId { get; set; }
    public string Kind { get; set; } = "payment";
    public string Method { get; set; } = "cash";
    public decimal Amount { get; set; }
    public DateTime ReceivedAt { get; set; }
    public Guid ReceivedBy { get; set; }
    public string? Reference { get; set; }
    public string? ReceiptNo { get; set; }
    public string? Reason { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-BIL-06: a user's end-of-day closing at a branch. The server records its own expected figure too.</summary>
public class CashClosing : SyncEntity
{
    public Guid BranchId { get; set; }
    public Guid UserId { get; set; }
    public DateTime BusinessDate { get; set; }
    public DateTime ClosedAt { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal CountedCash { get; set; }

    /// <summary>JSON array of MethodTotal as the device computed it.</summary>
    public string TotalsJson { get; set; } = "[]";
    public int PaymentCount { get; set; }
    public string? Notes { get; set; }

    /// <summary>Server-maintained: what the server's payments say, and whether the device agreed.</summary>
    public decimal ServerExpectedCash { get; set; }
    public bool Mismatch { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

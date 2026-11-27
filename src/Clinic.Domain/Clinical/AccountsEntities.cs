using Clinic.Domain.Common;
using Clinic.Domain.Forms;
using Clinic.Domain.Sync;

namespace Clinic.Domain.Clinical;

// =====================================================================================================
// FR-BIL-10 packages
// =====================================================================================================

/// <summary>Package master (server wins).</summary>
public class PackagePlan : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int ValidityDays { get; set; } = 90;

    /// <summary>Optional: the case type it is usually sold with (e.g. 'pregnancy' for an antenatal package).</summary>
    public string? CaseTypeCode { get; set; }

    /// <summary>JSON array of PackageItem.</summary>
    public string ItemsJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
}

/// <summary>A package sold to a patient; billed once (an invoice line with source 'package'), then used session by session.</summary>
public class PatientPackage : SyncEntity
{
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid PackagePlanId { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }

    /// <summary>Snapshot of the plan's entitlements at sale.</summary>
    public string ItemsJson { get; set; } = "[]";
    public DateTime PurchasedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public Guid? CaseId { get; set; }
    public string Status { get; set; } = "active";

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>One use of a package entitlement by a charge (the charge is then billed at zero).</summary>
public class PackageUsage : SyncEntity
{
    public Guid PatientPackageId { get; set; }
    public Guid BranchId { get; set; }
    public string Kind { get; set; } = "";
    public string? Code { get; set; }
    public string Source { get; set; } = "";
    public Guid SourceId { get; set; }
    public DateTime UsedAt { get; set; }
    public Guid? UsedBy { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

// =====================================================================================================
// FR-BIL-12 panels / insurance
// =====================================================================================================

/// <summary>Payer master (server wins).</summary>
public class Payer : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "corporate";
    public decimal DefaultCoPayPercent { get; set; }
    public int PaymentTermsDays { get; set; } = 30;

    /// <summary>JSON array of PayerPrice (per-payer price list).</summary>
    public string PricesJson { get; set; } = "[]";
    public string? ContactEmail { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A patient's membership of a panel (employee of a company, insurance policy).</summary>
public class PatientPanel : SyncEntity
{
    public Guid PatientId { get; set; }
    public Guid PayerId { get; set; }
    public string MemberNo { get; set; } = "";
    public string? Employer { get; set; }

    /// <summary>self / spouse / child / parent.</summary>
    public string? Relation { get; set; }

    /// <summary>Overrides the payer's default co-pay when set.</summary>
    public decimal? CoPayPercent { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Money received from a payer (server-only, accounts).</summary>
public class PayerSettlement : TenantEntity
{
    public Guid PayerId { get; set; }
    public decimal Amount { get; set; }
    public DateTime ReceivedOn { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
}

// =====================================================================================================
// FR-BIL-14 credit notes and write-offs
// =====================================================================================================

public class CreditNote : SyncEntity
{
    public Guid InvoiceId { get; set; }
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public string Kind { get; set; } = "credit_note";
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "requested";
    public DateTime RequestedAt { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

// =====================================================================================================
// FR-BIL-17 expenses
// =====================================================================================================

public class Expense : SyncEntity
{
    public Guid BranchId { get; set; }

    /// <summary>Code from the 'expense_categories' lookup.</summary>
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime SpentOn { get; set; }
    public string Method { get; set; } = "cash";
    public string? PaidTo { get; set; }
    public string? Notes { get; set; }
    public Guid RecordedBy { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

// =====================================================================================================
// FR-DOC-04/05 doctor share (server-only, accounts)
// =====================================================================================================

public class DoctorShareRule : TenantEntity
{
    public Guid DoctorId { get; set; }
    public string Kind { get; set; } = "consultation";
    public decimal? Percent { get; set; }
    public decimal? FixedAmount { get; set; }
}

/// <summary>A payout statement; once approved its amounts are frozen (LinesJson), and paying it is a ledger entry.</summary>
public class DoctorPayout : TenantEntity
{
    public Guid DoctorId { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public decimal Collected { get; set; }
    public decimal Share { get; set; }
    public string Status { get; set; } = "draft";
    public string LinesJson { get; set; } = "[]";
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTime? PaidAt { get; set; }
    public Guid? PaidBy { get; set; }
    public string? PaymentReference { get; set; }
}

// =====================================================================================================
// §4.14 communication
// =====================================================================================================

/// <summary>FR-COM-01 template master (server wins).</summary>
public class MessageTemplate : SyncEntity
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Channel { get; set; } = "sms";
    public string BodyEn { get; set; } = "";
    public string? BodyUr { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>FR-COM-02: one row per patient (deterministic id from the patient id).</summary>
public class ContactPreference : SyncEntity
{
    public Guid PatientId { get; set; }
    public string Language { get; set; } = "en";
    public string Channel { get; set; } = "sms";
    public bool OptOut { get; set; }
}

/// <summary>FR-COM-03: a message queued on a device and sent by the server after sync; its status syncs back.</summary>
public class OutboundMessage : SyncEntity
{
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public string Channel { get; set; } = "sms";
    public string? TemplateKey { get; set; }
    public string To { get; set; } = "";
    public string Body { get; set; } = "";
    public string Language { get; set; } = "en";
    public DateTime QueuedAt { get; set; }
    public Guid? QueuedBy { get; set; }

    /// <summary>The appointment / lab order / invoice the message is about.</summary>
    public Guid? RelatedId { get; set; }

    // Server-maintained:
    public string Status { get; set; } = "queued";
    public DateTime? SentAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

// =====================================================================================================
// §4.15 feedback and complaints
// =====================================================================================================

/// <summary>FR-FBK-01: post-visit feedback (core ratings plus the Feedback dynamic form).</summary>
public class Feedback : SyncEntity, IHasDynamicData
{
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid? DoctorId { get; set; }
    public int OverallRating { get; set; }
    public int? DoctorRating { get; set; }
    public int? ServiceRating { get; set; }
    public string? Comment { get; set; }
    public DateTime SubmittedAt { get; set; }

    /// <summary>in_clinic (tablet at reception) or link.</summary>
    public string Channel { get; set; } = "in_clinic";
    public Guid? FormVersionId { get; set; }
    public string? DynamicData { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
    public IReadOnlyDictionary<string, object?> GetFormVariables() => new Dictionary<string, object?>();
}

/// <summary>FR-FBK-02: complaint register.</summary>
public class Complaint : SyncEntity
{
    public Guid? PatientId { get; set; }
    public Guid BranchId { get; set; }
    public string Subject { get; set; } = "";
    public string? Details { get; set; }

    /// <summary>Code from the 'complaint_categories' lookup.</summary>
    public string? Category { get; set; }
    public string Status { get; set; } = "open";
    public DateTime RaisedAt { get; set; }
    public Guid? RaisedBy { get; set; }
    public Guid? AssignedTo { get; set; }
    public string? AssignedName { get; set; }
    public string? Resolution { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

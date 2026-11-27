using System.Text.Json.Serialization;
using SQLite;

namespace Clinic.Client.Core.Local;

[Table("PackagePlans")]
public sealed class LocalPackagePlan : LocalEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int ValidityDays { get; set; } = 90;
    public string? CaseTypeCode { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
}

[Table("PatientPackages")]
public sealed class LocalPatientPackage : LocalEntity
{
    [Indexed]
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid PackagePlanId { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public DateTime PurchasedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public Guid? CaseId { get; set; }
    public string Status { get; set; } = "active";
}

[Table("PackageUsages")]
public sealed class LocalPackageUsage : LocalEntity
{
    [Indexed]
    public Guid PatientPackageId { get; set; }
    public Guid BranchId { get; set; }
    public string Kind { get; set; } = "";
    public string? Code { get; set; }
    public string Source { get; set; } = "";
    [Indexed]
    public Guid SourceId { get; set; }
    public DateTime UsedAt { get; set; }
    public Guid? UsedBy { get; set; }
}

[Table("Payers")]
public sealed class LocalPayer : LocalEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "corporate";
    public decimal DefaultCoPayPercent { get; set; }
    public int PaymentTermsDays { get; set; } = 30;
    public string PricesJson { get; set; } = "[]";
    public string? ContactEmail { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("PatientPanels")]
public sealed class LocalPatientPanel : LocalEntity
{
    [Indexed]
    public Guid PatientId { get; set; }
    public Guid PayerId { get; set; }
    public string MemberNo { get; set; } = "";
    public string? Employer { get; set; }
    public string? Relation { get; set; }
    public decimal? CoPayPercent { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("CreditNotes")]
public sealed class LocalCreditNote : LocalEntity
{
    [Indexed]
    public Guid InvoiceId { get; set; }
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public string Kind { get; set; } = "credit_note";
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    [Indexed]
    public string Status { get; set; } = "requested";
    public DateTime RequestedAt { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }
}

[Table("Expenses")]
public sealed class LocalExpense : LocalEntity
{
    [Indexed]
    public Guid BranchId { get; set; }
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime SpentOn { get; set; }
    public string Method { get; set; } = "cash";
    public string? PaidTo { get; set; }
    public string? Notes { get; set; }
    public Guid RecordedBy { get; set; }
}

[Table("MessageTemplates")]
public sealed class LocalMessageTemplate : LocalEntity
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Channel { get; set; } = "sms";
    public string BodyEn { get; set; } = "";
    public string? BodyUr { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("ContactPreferences")]
public sealed class LocalContactPreference : LocalEntity
{
    [Indexed]
    public Guid PatientId { get; set; }
    public string Language { get; set; } = "en";
    public string Channel { get; set; } = "sms";
    public bool OptOut { get; set; }
}

[Table("OutboundMessages")]
public sealed class LocalOutboundMessage : LocalEntity
{
    [Indexed]
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public string Channel { get; set; } = "sms";
    public string? TemplateKey { get; set; }
    public string To { get; set; } = "";
    public string Body { get; set; } = "";
    public string Language { get; set; } = "en";
    public DateTime QueuedAt { get; set; }
    public Guid? QueuedBy { get; set; }
    public Guid? RelatedId { get; set; }

    /// <summary>Server-maintained after sync (FR-COM-03).</summary>
    public string Status { get; set; } = "queued";
    public DateTime? SentAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
}

[Table("Feedback")]
public sealed class LocalFeedback : LocalEntity, ILocalDynamic
{
    [Indexed]
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? EncounterId { get; set; }
    [Indexed]
    public Guid? DoctorId { get; set; }
    public int OverallRating { get; set; }
    public int? DoctorRating { get; set; }
    public int? ServiceRating { get; set; }
    public string? Comment { get; set; }
    public DateTime SubmittedAt { get; set; }
    public string Channel { get; set; } = "in_clinic";
    public Guid? FormVersionId { get; set; }

    [JsonIgnore, LocalOnly]
    public string? DynamicData { get; set; }
}

[Table("Complaints")]
public sealed class LocalComplaint : LocalEntity
{
    public Guid? PatientId { get; set; }
    [Indexed]
    public Guid BranchId { get; set; }
    public string Subject { get; set; } = "";
    public string? Details { get; set; }
    public string? Category { get; set; }
    public string Status { get; set; } = "open";
    public DateTime RaisedAt { get; set; }
    public Guid? RaisedBy { get; set; }
    public Guid? AssignedTo { get; set; }
    public string? AssignedName { get; set; }
    public string? Resolution { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
}

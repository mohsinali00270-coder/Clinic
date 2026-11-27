namespace Clinic.Shared.Clinical;

public static class AccountsRoutes
{
    public const string Packages = "api/v1/packages";
    public const string Payers = "api/v1/payers";
    public const string MessageTemplates = "api/v1/message-templates";
    public const string ShareRules = "api/v1/doctor-share-rules";
    public const string Payouts = "api/v1/doctor-payouts";
    public const string PayerSettlements = "api/v1/payer-settlements";
    public const string TaxSummary = "api/v1/reports/tax";
    public const string Profit = "api/v1/reports/profit";
}

/// <summary>FR-BIL-10: a package (e.g. "10 physio sessions") at a fixed price, valid for N days.</summary>
public sealed record PackagePlanDto(Guid Id, string Code, string Name, decimal Price, int ValidityDays, string? CaseTypeCode,
    IReadOnlyList<PackageItem> Items, bool IsActive);

public sealed record SavePackagePlanRequest(string Code, string Name, decimal Price, int ValidityDays, string? CaseTypeCode,
    IReadOnlyList<PackageItem> Items, bool IsActive = true);

/// <summary>FR-BIL-12: a corporate panel, insurer or government scheme with its default co-pay and price list.</summary>
public sealed record PayerDto(Guid Id, string Code, string Name, string Kind, decimal DefaultCoPayPercent, int PaymentTermsDays,
    IReadOnlyList<PayerPrice> Prices, string? ContactEmail, bool IsActive);

public sealed record SavePayerRequest(string Code, string Name, string Kind, decimal DefaultCoPayPercent, int PaymentTermsDays,
    IReadOnlyList<PayerPrice>? Prices, string? ContactEmail, bool IsActive = true);

/// <summary>Money received from a payer against its claims (a cheque or transfer for many invoices).</summary>
public sealed record PayerSettlementDto(Guid Id, Guid PayerId, decimal Amount, DateTime ReceivedOn, string? Reference, string? Notes);

public sealed record SavePayerSettlementRequest(Guid PayerId, decimal Amount, DateTime ReceivedOn, string? Reference, string? Notes);

public sealed record ClaimLineDto(Guid InvoiceId, string InvoiceNo, DateTime IssuedAt, string PatientName, string Mrn, string? MemberNo,
    decimal Total, decimal PayerAmount, decimal PatientAmount);

/// <summary>Payer statement: claims in the period, settlements, what is still owed and how old it is.</summary>
public sealed record PayerStatementDto(Guid PayerId, string PayerName, DateTime From, DateTime To, IReadOnlyList<ClaimLineDto> Claims,
    decimal Claimed, decimal Settled, decimal Outstanding, AgeingBuckets Ageing);

/// <summary>FR-COM-01: a message template with English/Urdu bodies and {placeholders}.</summary>
public sealed record MessageTemplateDto(Guid Id, string Key, string Name, string Channel, string BodyEn, string? BodyUr, bool IsActive);

public sealed record SaveMessageTemplateRequest(string Key, string Name, string Channel, string BodyEn, string? BodyUr, bool IsActive = true);

/// <summary>FR-DOC-04: a doctor's share of a service kind.</summary>
public sealed record ShareRuleDto(Guid Id, Guid DoctorId, string Kind, decimal? Percent, decimal? FixedAmount);

public sealed record SaveShareRuleRequest(Guid DoctorId, string Kind, decimal? Percent, decimal? FixedAmount);

/// <summary>FR-DOC-05: a payout statement for a period: draft → approved → paid. Amounts are fixed once approved.</summary>
public sealed record PayoutDto(Guid Id, Guid DoctorId, string DoctorName, DateTime From, DateTime To, decimal Collected, decimal Share,
    string Status, DateTime? ApprovedAt, DateTime? PaidAt, string? PaymentReference, IReadOnlyList<ShareLine> Lines);

public sealed record CreatePayoutRequest(Guid DoctorId, DateTime From, DateTime To);

public sealed record PayPayoutRequest(string? Reference);

/// <summary>FR-BIL-15: tax collected by rate in a period (final invoices only).</summary>
public sealed record TaxLineDto(decimal TaxPercent, decimal Taxable, decimal Tax);

public sealed record TaxSummaryDto(DateTime From, DateTime To, Guid? BranchId, IReadOnlyList<TaxLineDto> Lines, decimal TotalTax);

/// <summary>FR-BIL-17: collected income minus expenses (and approved doctor payouts) for a simple profit view.</summary>
public sealed record ProfitDto(DateTime From, DateTime To, Guid? BranchId, decimal Collected, decimal Refunded, decimal PayerSettlements,
    decimal Expenses, decimal DoctorPayouts, IReadOnlyDictionary<string, decimal> ExpensesByCategory)
{
    public decimal Profit => Collected - Refunded + PayerSettlements - Expenses - DoctorPayouts;
}

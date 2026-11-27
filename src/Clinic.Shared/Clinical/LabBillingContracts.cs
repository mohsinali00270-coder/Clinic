namespace Clinic.Shared.Clinical;

public static class LabBillingRoutes
{
    public const string LabTests = "api/v1/lab-tests";
    public const string Services = "api/v1/services";
    public const string DaySummary = "api/v1/billing/day-summary";
    public const string PatientBalance = "api/v1/billing/balance";
}

/// <summary>
/// FR-LAB-01: a test of the menu. Its result parameters (value, unit, reference range by gender/age) are the
/// LabResult dynamic form whose context is the test code; abnormal rules use gender/ageYears.
/// </summary>
public sealed record LabTestDto(Guid Id, string Code, string Name, string? Department, string SampleType, decimal? Fee,
    int TurnaroundHours, bool IsActive);

public sealed record SaveLabTestRequest(string Code, string Name, string? Department, string SampleType, decimal? Fee,
    int TurnaroundHours = 24, bool IsActive = true);

/// <summary>FR-BIL-01: a billable service; branch-specific when BranchId is set (overrides the tenant-wide price).</summary>
public sealed record ServiceItemDto(Guid Id, string Code, string Name, string Kind, Guid? BranchId, decimal Price, decimal TaxPercent, bool IsActive);

public sealed record SaveServiceItemRequest(string Code, string Name, string Kind, Guid? BranchId, decimal Price, decimal TaxPercent = 0, bool IsActive = true);

public sealed record PatientBalanceDto(Guid PatientId, decimal Invoiced, decimal Paid, decimal Refunded, decimal Deposits, decimal Balance);

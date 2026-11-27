namespace Clinic.Shared.Clinical;

/// <summary>Admin API for the masters of §4.10–4.12 (devices receive them through sync).</summary>
public static class CareRoutes
{
    public const string CaseTypes = "api/v1/case-types";
    public const string ProcedureTypes = "api/v1/procedure-types";
    public const string Beds = "api/v1/beds";
}

/// <summary>FR-CASE-02: a configurable case type with its own form (Case form whose context is the type code).</summary>
public sealed record CaseTypeDto(
    Guid Id,
    string Code,
    string Name,
    string? NameUr,
    string? Specialty,
    int? ExpectedSessions,
    int? FollowUpIntervalDays,
    string? DefaultServices,
    IReadOnlyList<CasePlanTemplateStep> DefaultPlan,
    bool IsActive);

public sealed record SaveCaseTypeRequest(
    string Code,
    string Name,
    string? NameUr,
    string? Specialty,
    int? ExpectedSessions,
    int? FollowUpIntervalDays,
    string? DefaultServices,
    IReadOnlyList<CasePlanTemplateStep>? DefaultPlan,
    bool IsActive = true);

/// <summary>FR-PRC-01: procedure master with consent, consumables and fee.</summary>
public sealed record ProcedureTypeDto(
    Guid Id,
    string Code,
    string Name,
    string Category,
    decimal? Fee,
    int DurationMinutes,
    bool RequiresConsent,
    string? ConsentTextEn,
    string? ConsentTextUr,
    IReadOnlyList<Consumable> Consumables,
    bool IsActive);

public sealed record SaveProcedureTypeRequest(
    string Code,
    string Name,
    string Category,
    decimal? Fee,
    int DurationMinutes,
    bool RequiresConsent,
    string? ConsentTextEn,
    string? ConsentTextUr,
    IReadOnlyList<Consumable>? Consumables,
    bool IsActive = true);

/// <summary>FR-PRC-03/04: a bed, room or day-care chair of a branch, with its charge per day or hour.</summary>
public sealed record BedDto(Guid Id, Guid BranchId, string Code, string? Ward, string Kind, decimal ChargeAmount, string ChargeUnit, bool IsActive);

public sealed record SaveBedRequest(Guid BranchId, string Code, string? Ward, string Kind, decimal ChargeAmount, string ChargeUnit, bool IsActive = true);

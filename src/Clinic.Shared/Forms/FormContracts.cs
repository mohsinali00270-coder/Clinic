namespace Clinic.Shared.Forms;

public static class FormRoutes
{
    public const string EntityTypes = "api/v1/entity-types";
    public const string Forms = "api/v1/forms";
    public const string FormVersions = "api/v1/form-versions";
    public const string FormTemplates = "api/v1/form-templates";
    public const string Lookups = "api/v1/lookups";
}

public sealed record EntityTypeDto(string Name, string DisplayName, IReadOnlyList<ContextVariable> Variables, string AvailableFrom);

public sealed record FormSummaryDto(
    Guid Id,
    string Name,
    string EntityType,
    Guid? ContextBranchId,
    string? ContextSpecialty,
    bool IsActive,
    int? CurrentVersionNo,
    bool HasUnpublishedChanges,
    DateTime UpdatedAt);

public sealed record FormDto(
    Guid Id,
    string Name,
    string EntityType,
    Guid? ContextBranchId,
    string? ContextSpecialty,
    bool IsActive,
    int? CurrentVersionNo,
    Guid? CurrentVersionId,
    bool HasUnpublishedChanges,
    FormDefinition Draft,
    IReadOnlyList<DefinitionError> Problems);

public sealed record CreateFormRequest(
    string Name,
    string EntityType,
    Guid? ContextBranchId = null,
    string? ContextSpecialty = null,
    string? TemplateKey = null,
    FormDefinition? Definition = null);

/// <summary>Drafts may be incomplete; problems are returned, and publishing requires none.</summary>
public sealed record SaveFormDraftRequest(
    string Name,
    Guid? ContextBranchId,
    string? ContextSpecialty,
    bool IsActive,
    FormDefinition Definition);

public sealed record FormVersionDto(
    Guid Id,
    Guid FormId,
    int VersionNo,
    DateTime PublishedAt,
    string? PublishedBy,
    bool IsCurrent,
    FormDefinition Definition);

public sealed record FormTemplateDto(string Key, string Name, string EntityType, string Description);

public sealed record ValidateFormRequest(FormDefinition Definition, Guid? FormId = null);

public sealed record FormValidationResult(bool IsValid, IReadOnlyList<DefinitionError> Errors);

// ---------- Lookups (FR-DYN-14) ----------

public sealed record LookupListDto(Guid Id, string Key, string Name, string? ParentListKey, bool IsSystem, int ItemCount);

public sealed record LookupItemDto(Guid Id, string Code, string LabelEn, string? LabelUr, string? ParentCode, int SortOrder, bool IsActive);

public sealed record LookupListDetailDto(Guid Id, string Key, string Name, string? ParentListKey, bool IsSystem, IReadOnlyList<LookupItemDto> Items);

public sealed record SaveLookupListRequest(string Key, string Name, string? ParentListKey);

public sealed record SaveLookupItemRequest(string Code, string LabelEn, string? LabelUr, string? ParentCode, int SortOrder = 0, bool IsActive = true);

/// <summary>CSV with a header row: code,labelEn,labelUr,parentCode,sortOrder (labelUr/parentCode/sortOrder optional).</summary>
public sealed record ImportLookupItemsRequest(string Csv);

public sealed record ImportLookupItemsResult(int Added, int Updated, IReadOnlyList<string> Errors);

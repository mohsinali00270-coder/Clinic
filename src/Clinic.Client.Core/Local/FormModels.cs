using SQLite;

namespace Clinic.Client.Core.Local;

/// <summary>A device-only column: never sent to the server and kept when a pulled row replaces the local one.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LocalOnlyAttribute : Attribute;

/// <summary>A local table whose rows carry dynamic form values (FR-DYN-31 snapshot kept on the device).</summary>
public interface ILocalDynamic
{
    Guid Id { get; }
    Guid? FormVersionId { get; set; }
    string? DynamicData { get; set; }
}

[Table("LookupLists")]
public sealed class LocalLookupList : LocalEntity
{
    [Indexed]
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentListKey { get; set; }
    public bool IsSystem { get; set; }
}

[Table("LookupItems")]
public sealed class LocalLookupItem : LocalEntity
{
    public Guid ListId { get; set; }

    [Indexed]
    public string ListKey { get; set; } = "";
    public string Code { get; set; } = "";
    public string LabelEn { get; set; } = "";
    public string? LabelUr { get; set; }
    public string? ParentCode { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Published, immutable form snapshot pulled from the server (FR-OFF-24).</summary>
[Table("FormVersions")]
public sealed class LocalFormVersion : LocalEntity
{
    public Guid FormId { get; set; }
    public int VersionNo { get; set; }

    [Indexed]
    public string EntityType { get; set; } = "";
    public Guid? ContextBranchId { get; set; }
    public string? ContextSpecialty { get; set; }
    public string DefinitionJson { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public string? PublishedByName { get; set; }
    public bool IsCurrent { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>FR-DYN-30/32: one dynamic value; the unit of sync.</summary>
[Table("FieldValues")]
public sealed class LocalFieldValue : LocalEntity
{
    [Indexed]
    public Guid RecordId { get; set; }
    public string EntityType { get; set; } = "";
    public string FieldKey { get; set; } = "";
    public Guid FormVersionId { get; set; }
    public Guid? BranchId { get; set; }
    public string ValueKind { get; set; } = "text";
    public string? ValueText { get; set; }
    public decimal? ValueNumber { get; set; }
    public DateTime? ValueDate { get; set; }
    public bool? ValueBool { get; set; }
    public string? ValueJson { get; set; }
    public int RowIndex { get; set; }
}

/// <summary>FR-DYN-23: autosaved, unsynced form drafts so a crash or flat battery loses nothing.</summary>
[Table("FormDrafts")]
public sealed class LocalFormDraft
{
    [PrimaryKey]
    public string Key { get; set; } = "";
    public string ValuesJson { get; set; } = "{}";
    public DateTime SavedAt { get; set; }
}

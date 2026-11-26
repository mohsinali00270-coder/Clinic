using Clinic.Domain.Common;
using Clinic.Domain.Sync;

namespace Clinic.Domain.Forms;

/// <summary>
/// An admin-editable form (FR-DYN-10/11). The draft lives here and never leaves the server;
/// publishing snapshots it into an immutable <see cref="FormVersion"/> that devices pull.
/// </summary>
public class Form : TenantEntity
{
    public string Name { get; set; } = "";
    public string EntityType { get; set; } = "";

    /// <summary>§5.1 form context: null = every branch / specialty.</summary>
    public Guid? ContextBranchId { get; set; }
    public string? ContextSpecialty { get; set; }

    /// <summary>The draft FormDefinition as JSON.</summary>
    public string DraftJson { get; set; } = "";
    public Guid? CurrentVersionId { get; set; }
    public int? CurrentVersionNo { get; set; }
    public bool HasUnpublishedChanges { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Immutable published snapshot (FR-DYN-11). Records keep the version they were captured with and render
/// with it forever; only IsCurrent/IsActive flip when a newer version is published or the form is switched off.
/// </summary>
public class FormVersion : SyncEntity
{
    public Guid FormId { get; set; }
    public int VersionNo { get; set; }
    public string EntityType { get; set; } = "";
    public Guid? ContextBranchId { get; set; }
    public string? ContextSpecialty { get; set; }
    public string DefinitionJson { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public string? PublishedByName { get; set; }
    public bool IsCurrent { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>A branch-specific form only goes to that branch's devices.</summary>
    public override Guid? GetSyncBranchId() => ContextBranchId;
}

/// <summary>§5.1 lookup list: reusable, editable, optionally hierarchical (e.g. cities → areas).</summary>
public class LookupList : SyncEntity
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Items of this list have a ParentCode from this other list (cascading dropdowns).</summary>
    public string? ParentListKey { get; set; }
    public bool IsSystem { get; set; }
}

public class LookupItem : SyncEntity
{
    public Guid ListId { get; set; }

    /// <summary>Denormalised so devices can query items by list key directly.</summary>
    public string ListKey { get; set; } = "";
    public string Code { get; set; } = "";
    public string LabelEn { get; set; } = "";
    public string? LabelUr { get; set; }
    public string? ParentCode { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// FR-DYN-30/32: one dynamic value of one record — the unit of sync, so two users editing different fields of
/// the same record merge cleanly. The id is derived from (RecordId, FieldKey) so offline creations on two devices
/// meet as one row. Values sit in typed columns for search and reports.
/// </summary>
public class FieldValue : SyncEntity
{
    public Guid RecordId { get; set; }
    public string EntityType { get; set; } = "";
    public string FieldKey { get; set; } = "";
    public Guid FormVersionId { get; set; }

    /// <summary>Copied from the parent record so pulls are branch-scoped like the record itself.</summary>
    public Guid? BranchId { get; set; }

    public string ValueKind { get; set; } = "text";
    public string? ValueText { get; set; }
    public decimal? ValueNumber { get; set; }
    public DateTime? ValueDate { get; set; }
    public bool? ValueBool { get; set; }
    public string? ValueJson { get; set; }

    /// <summary>Reserved for per-row storage; repeating groups are stored as one JSON value today.</summary>
    public int RowIndex { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>A record that carries a dynamic form (FR-DYN-31).</summary>
public interface IHasDynamicData : ISyncEntity
{
    /// <summary>The form version the record was captured with.</summary>
    Guid? FormVersionId { get; set; }

    /// <summary>JSON snapshot of all dynamic values for fast list/detail reads. FieldValues are the source of truth.</summary>
    string? DynamicData { get; set; }

    /// <summary>Context variables the record offers to expressions (gender, ageYears…).</summary>
    IReadOnlyDictionary<string, object?> GetFormVariables();
}

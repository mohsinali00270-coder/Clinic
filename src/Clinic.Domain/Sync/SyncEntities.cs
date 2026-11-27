using Clinic.Domain.Common;

namespace Clinic.Domain.Sync;

/// <summary>
/// FR-OFF-02 / FR-DATA-02: a record that syncs to devices. RowVersion is the tenant's ChangeSequence at the
/// last save (a bigint, portable to SQLite), assigned by the DbContext together with a ChangeLog row.
/// </summary>
public interface ISyncEntity : ITenantEntity, ISoftDelete
{
    Guid Id { get; }
    long RowVersion { get; set; }

    /// <summary>Branch that scopes who pulls this row; null = every device in the tenant.</summary>
    Guid? GetSyncBranchId();
}

public abstract class SyncEntity : TenantEntity, ISyncEntity
{
    public long RowVersion { get; set; }

    public virtual Guid? GetSyncBranchId() => null;
}

/// <summary>
/// One row per saved change of a sync entity. Sequence is per tenant, strictly increasing in commit order;
/// it drives pull (FR-SYN-20) and field-level conflict detection (FR-SYN-30).
/// </summary>
public class ChangeLog : ITenantEntity
{
    public Guid TenantId { get; set; }
    public long Sequence { get; set; }
    public string EntityType { get; set; } = "";
    public Guid RecordId { get; set; }
    public Guid? BranchId { get; set; }
    public string Operation { get; set; } = "";

    /// <summary>Comma-separated camelCase property names changed by this save.</summary>
    public string ChangedFields { get; set; } = "";
    public Guid? DeviceId { get; set; }
    public Guid? UserId { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Per-tenant ChangeSequence. Updating the row locks it, so sequences commit in order.</summary>
public class SyncCounter
{
    public Guid TenantId { get; set; }
    public long Value { get; set; }

    /// <summary>FR-SYN-24: ChangeLog rows up to here were purged; older checkpoints must re-snapshot.</summary>
    public long PurgedThrough { get; set; }
}

/// <summary>FR-SYN-11 / FR-SYN-40: every processed ChangeId, so a re-sent batch applies exactly once.</summary>
public class SyncLog
{
    public Guid ChangeId { get; set; }
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? UserId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid RecordId { get; set; }
    public string Status { get; set; } = "";
    public long? RowVersion { get; set; }
    public DateTime ClientTimestamp { get; set; }
    public DateTime ProcessedAt { get; set; }
}

/// <summary>FR-OFF-22: next free number per tenant and kind (MRN, Token, Invoice).</summary>
public class NumberCounter
{
    public Guid TenantId { get; set; }
    public string Kind { get; set; } = "";
    public long NextValue { get; set; } = 1;
}

/// <summary>A block of numbers leased to one device for offline use.</summary>
public class NumberRange : TenantEntity
{
    public Guid DeviceId { get; set; }
    public string Kind { get; set; } = "";
    public long StartValue { get; set; }
    public long EndValue { get; set; }
    public DateTime LeasedAt { get; set; }
}

/// <summary>
/// FR-SYN-30: a change that collided with someone else's edit of the same fields (Manual policy). Kept until a person
/// resolves it on the device; the resolution is audited with both values (FR-SYN-31).
/// </summary>
public class SyncConflict : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ChangeId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid RecordId { get; set; }

    /// <summary>Comma-separated camelCase fields both sides changed.</summary>
    public string Fields { get; set; } = "";
    public string? MineJson { get; set; }
    public string? TheirsJson { get; set; }
    public long? BaseRowVersion { get; set; }
    public long ServerRowVersion { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? UserId { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>open / resolved.</summary>
    public string Status { get; set; } = "open";
    public string? Resolution { get; set; }
    public string? ResolvedJson { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
}

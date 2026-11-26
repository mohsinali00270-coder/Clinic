using System.Text.Json.Serialization;
using SQLite;

namespace Clinic.Client.Core.Local;

/// <summary>FR-OFF-02 SyncStatus, device-side only.</summary>
public enum SyncStatus
{
    Synced = 0,
    Pending = 1,
    Conflict = 2,
    Error = 3,
}

/// <summary>
/// Base for every syncable local table (FR-OFF-02). Property names match the server entity so a row
/// serialises to the same camelCase JSON the sync API uses. SyncStatus/LastSyncedAt never leave the device.
/// </summary>
public abstract class LocalEntity
{
    [PrimaryKey]
    public Guid Id { get; set; }

    [Indexed]
    public Guid TenantId { get; set; }

    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>Server ChangeSequence of the version this row is based on (0 = never synced).</summary>
    public long RowVersion { get; set; }

    public bool IsDeleted { get; set; }

    [JsonIgnore]
    public SyncStatus SyncStatus { get; set; }

    [JsonIgnore]
    public DateTime? LastSyncedAt { get; set; }
}

[Table("Branches")]
public sealed class LocalBranch : LocalEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("BranchNotes")]
public sealed class LocalBranchNote : LocalEntity, ILocalDynamic
{
    public Guid? FormVersionId { get; set; }

    /// <summary>Snapshot of this note's dynamic values, rebuilt on the device from FieldValues.</summary>
    [JsonIgnore, LocalOnly]
    public string? DynamicData { get; set; }

    [Indexed]
    public Guid BranchId { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public bool IsPinned { get; set; }
    public string? Color { get; set; }
}

// ---------------------------------------------------------------------------
// Device-only bookkeeping tables
// ---------------------------------------------------------------------------

public enum OutboxStatus
{
    /// <summary>Waiting to be pushed.</summary>
    Pending = 0,

    /// <summary>Push failed for a transient reason; retried with back-off.</summary>
    Failed = 1,

    /// <summary>Server reported a conflict; a person must choose (Sync Center).</summary>
    Conflict = 2,

    /// <summary>Server refused it (validation/permission); retry only after fixing the cause.</summary>
    Rejected = 3,
}

/// <summary>FR-OFF-10: one pending local change, written in the same transaction as the row itself.</summary>
[Table("Outbox")]
public sealed class OutboxEntry
{
    /// <summary>Local insertion order; pushed in this order so parents precede children (FR-OFF-11).</summary>
    [PrimaryKey, AutoIncrement]
    public long Seq { get; set; }

    [Unique]
    public Guid ChangeId { get; set; }

    public string EntityType { get; set; } = "";

    [Indexed]
    public Guid RecordId { get; set; }

    public int Operation { get; set; }

    /// <summary>JSON array of camelCase field names.</summary>
    public string ChangedFields { get; set; } = "[]";

    /// <summary>JSON object with the changed values.</summary>
    public string? Payload { get; set; }

    public long? BaseRowVersion { get; set; }
    public Guid DeviceId { get; set; }
    public Guid UserId { get; set; }
    public DateTime ClientTimestamp { get; set; }

    [Indexed]
    public OutboxStatus Status { get; set; }

    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Server's copy of the record, sent back with a conflict or rejection.</summary>
    public string? ServerData { get; set; }

    /// <summary>Short human description for the Sync Center, e.g. "Note: Fridge broken".</summary>
    public string? Summary { get; set; }
}

[Table("SyncCheckpoints")]
public sealed class SyncCheckpoint
{
    [PrimaryKey]
    public string Scope { get; set; } = "";
    public long Value { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>FR-OFF-22: a block of numbers leased from the server.</summary>
[Table("NumberRanges")]
public sealed class LeasedRange
{
    [PrimaryKey]
    public Guid Id { get; set; }

    [Indexed]
    public string Kind { get; set; } = "";
    public long Start { get; set; }
    public long End { get; set; }

    /// <summary>Next unused number in this block (End + 1 when exhausted).</summary>
    public long Next { get; set; }
}

[Table("LocalMeta")]
public sealed class LocalMeta
{
    [PrimaryKey]
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}

using Clinic.Domain.Common;
using Clinic.Domain.Sync;

namespace Clinic.Domain.Clinical;

/// <summary>FR-SYN-32: a confirmed merge; later pushes that still point at the duplicate are re-pointed to the survivor.</summary>
public class PatientMerge : TenantEntity
{
    public Guid SurvivorId { get; set; }
    public Guid MergedId { get; set; }
    public DateTime MergedAt { get; set; }
    public Guid? MergedBy { get; set; }
    public string? Reason { get; set; }
    public string SummaryJson { get; set; } = "{}";
}

/// <summary>
/// FR-SEC-07 read-access log: a device records who opened which patient record (offline too); the server turns each
/// into an audit row. Insert-only; only auditors pull them.
/// </summary>
public class AccessEvent : SyncEntity
{
    public Guid BranchId { get; set; }
    public string EntityType { get; set; } = "Patient";
    public Guid RecordId { get; set; }
    public string Action { get; set; } = "open";
    public DateTime At { get; set; }
    public Guid? UserId { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>A crash or sync error reported by the app.</summary>
public class ClientError : Entity
{
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Detail { get; set; }
    public string AppVersion { get; set; } = "";
    public string Platform { get; set; } = "";
    public DateTime OccurredAt { get; set; }
}

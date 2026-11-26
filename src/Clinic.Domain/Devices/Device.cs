using Clinic.Domain.Common;

namespace Clinic.Domain.Devices;

/// <summary>FR-OFF-30: a registered app install. Id is the GUID the device generated itself.</summary>
public class Device : TenantEntity
{
    /// <summary>Short per-tenant code used in offline numbers, e.g. "D07".</summary>
    public string DeviceCode { get; set; } = "";
    public int DeviceNumber { get; set; }
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string? Model { get; set; }
    public string AppVersion { get; set; } = "";
    public Guid? BranchId { get; set; }
    public Guid? RegisteredByUserId { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime? LastSeenAt { get; set; }

    /// <summary>FR-SYN-43 telemetry, updated on every push/pull.</summary>
    public DateTime? LastSyncAt { get; set; }
    public int LastPushCount { get; set; }
    public int LastPullCount { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
}

namespace Clinic.Domain.Common;

/// <summary>
/// Base for every persisted record. GUID keys everywhere (FR-API-07, FR-DATA-01);
/// the server generates sequential GUIDs when Id is empty, devices supply their own.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>Soft delete (FR-DATA-03). Hidden by the global query filter.</summary>
public interface ISoftDelete
{
    bool IsDeleted { get; set; }
}

/// <summary>
/// FR-API-03: every tenant-owned row carries TenantId. The DbContext stamps it on insert,
/// filters by it on every query, and refuses writes that cross tenants.
/// </summary>
public interface ITenantEntity
{
    Guid TenantId { get; set; }
}

public abstract class TenantEntity : Entity, ITenantEntity, ISoftDelete
{
    public Guid TenantId { get; set; }
    public bool IsDeleted { get; set; }
}

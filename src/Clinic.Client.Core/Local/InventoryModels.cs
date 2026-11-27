using SQLite;

namespace Clinic.Client.Core.Local;

[Table("InventoryItems")]
public sealed class LocalInventoryItem : LocalEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "medicine";
    [Indexed]
    public Guid? DrugId { get; set; }
    public string Unit { get; set; } = "tablet";
    public decimal ReorderLevel { get; set; }
    public decimal SalePrice { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("StockBatches")]
public sealed class LocalStockBatch : LocalEntity
{
    [Indexed]
    public Guid ItemId { get; set; }
    [Indexed]
    public Guid BranchId { get; set; }
    public string BatchNo { get; set; } = "";
    public DateTime? Expiry { get; set; }
    public decimal UnitCost { get; set; }
    public string? Supplier { get; set; }
    public DateTime ReceivedAt { get; set; }
}

[Table("StockMovements")]
public sealed class LocalStockMovement : LocalEntity
{
    [Indexed]
    public Guid ItemId { get; set; }
    [Indexed]
    public Guid BatchId { get; set; }
    public Guid BranchId { get; set; }
    public string Kind { get; set; } = "purchase";
    public decimal Quantity { get; set; }
    public DateTime MovedAt { get; set; }
    public Guid? MovedBy { get; set; }
    public string? Reference { get; set; }
    public string? Reason { get; set; }
    [Indexed]
    public Guid? SourceId { get; set; }
}

[Table("Dispensations")]
public sealed class LocalDispensation : LocalEntity
{
    [Indexed]
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = "";
    [Indexed]
    public Guid? PrescriptionItemId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public DateTime DispensedAt { get; set; }
    public Guid? DispensedBy { get; set; }
    public string? Notes { get; set; }
}

[Table("AccessEvents")]
public sealed class LocalAccessEvent : LocalEntity
{
    public Guid BranchId { get; set; }
    public string EntityType { get; set; } = "Patient";
    [Indexed]
    public Guid RecordId { get; set; }
    public string Action { get; set; } = "open";
    public DateTime At { get; set; }
    public Guid? UserId { get; set; }
}

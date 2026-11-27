using Clinic.Domain.Sync;

namespace Clinic.Domain.Clinical;

/// <summary>FR-INV-01: an item of stock (medicine, consumable, reagent), tenant-wide master (server wins).</summary>
public class InventoryItem : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "medicine";

    /// <summary>The drug master entry this item dispenses (prescriptions link through it).</summary>
    public Guid? DrugId { get; set; }
    public string Unit { get; set; } = "tablet";
    public decimal ReorderLevel { get; set; }
    public decimal SalePrice { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A batch received at a branch (batch number, expiry, cost). Its quantity is the sum of its movements.</summary>
public class StockBatch : SyncEntity
{
    public Guid ItemId { get; set; }
    public Guid BranchId { get; set; }
    public string BatchNo { get; set; } = "";
    public DateTime? Expiry { get; set; }
    public decimal UnitCost { get; set; }
    public string? Supplier { get; set; }
    public DateTime ReceivedAt { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>FR-INV-02: one signed change of stock (insert-only ledger).</summary>
public class StockMovement : SyncEntity
{
    public Guid ItemId { get; set; }
    public Guid BatchId { get; set; }
    public Guid BranchId { get; set; }
    public string Kind { get; set; } = "purchase";
    public decimal Quantity { get; set; }
    public DateTime MovedAt { get; set; }
    public Guid? MovedBy { get; set; }
    public string? Reference { get; set; }
    public string? Reason { get; set; }

    /// <summary>The dispensation, procedure or transfer this movement belongs to.</summary>
    public Guid? SourceId { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

/// <summary>Medicine handed to a patient (from a prescription line or over the counter); billed as one charge.</summary>
public class Dispensation : SyncEntity
{
    public Guid PatientId { get; set; }
    public Guid BranchId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = "";
    public Guid? PrescriptionItemId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public DateTime DispensedAt { get; set; }
    public Guid? DispensedBy { get; set; }
    public string? Notes { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;
}

using Clinic.Application.Abstractions;
using Clinic.Application.Sync;
using Clinic.Domain.Clinical;
using Clinic.Domain.Sync;
using Clinic.Shared.Clinical;
using Clinic.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>
/// Server re-checks (FR-SYN-13) for stock (FR-INV-01/02). Movements are insert-only: a mistake is corrected with a
/// new movement, so two offline devices never overwrite each other's stock. Negative stock is accepted (dispensing
/// already happened at the counter) and shown as an alert to count and adjust.
/// </summary>
public static class InventorySyncRules
{
    private static async Task<T?> FindAsync<T>(IAppDbContext db, Guid id) where T : SyncEntity =>
        await db.Set<T>().IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == id && e.TenantId == db.CurrentTenantId && !e.IsDeleted);

    private static async Task<string?> ScopeAsync(SyncWriteContext ctx, Guid branchId, params string[] permissions)
    {
        var granted = permissions.Select(p => (p, ctx.User.GetScope(p))).FirstOrDefault(x => x.Item2 is not null);
        if (granted.Item2 is not { } scope) return $"You do not have the {permissions[0]} permission.";
        if (!await ctx.Db.Branches.AnyAsync(b => b.Id == branchId)) return "Branch not found.";
        return scope != PermissionScope.All && !ctx.User.BranchIds.Contains(branchId) ? "You can only work with stock of your own branches." : null;
    }

    public static async Task<string?> AuthorizeBatchAsync(SyncWriteContext ctx)
    {
        var b = (StockBatch)ctx.Entity;
        if (await ScopeAsync(ctx, b.BranchId, Permissions.Inventory.StockIn) is { } denied) return denied;
        if (string.IsNullOrWhiteSpace(b.BatchNo) || b.BatchNo.Length > 40) return "A batch needs a number (max 40).";
        if (b.UnitCost < 0) return "The cost cannot be negative.";
        var item = await FindAsync<InventoryItem>(ctx.Db, b.ItemId);
        if (item is null) return "Item not found. Sync to get the latest item list.";
        if (ctx.IsNew && !item.IsActive) return $"{item.Name} is no longer stocked.";
        if (!ctx.IsNew && ctx.Db.Entry(b).Properties.Any(p => p.Metadata.Name is nameof(StockBatch.ItemId) or nameof(StockBatch.BranchId) &&
                                                             !Equals(p.OriginalValue, p.CurrentValue)))
            return "A batch cannot move to another item or branch; transfer the stock instead.";
        return null;
    }

    public static string? ValidateMovement(object o)
    {
        var m = (StockMovement)o;
        if (!MovementKind.All.Contains(m.Kind)) return $"Unknown movement '{m.Kind}'.";
        if (MovementKind.CheckSign(m.Kind, m.Quantity) is { } sign) return sign;
        if (m.Kind is MovementKind.Adjust or MovementKind.Expired && string.IsNullOrWhiteSpace(m.Reason)) return "Adjustments need a reason.";
        return m.Reason?.Length > 300 ? "The reason is too long." : null;
    }

    public static async Task<string?> AuthorizeMovementAsync(SyncWriteContext ctx)
    {
        var m = (StockMovement)ctx.Entity;
        if (!ctx.IsNew) return "Stock movements cannot be changed; record a correcting movement.";
        var batch = await FindAsync<StockBatch>(ctx.Db, m.BatchId);
        if (batch is null) return "The batch was not found on the server.";
        if (batch.ItemId != m.ItemId) return "The batch belongs to another item.";
        m.BranchId = batch.BranchId;

        string[] permissions = m.Kind switch
        {
            MovementKind.Purchase or MovementKind.Return or MovementKind.TransferIn => [Permissions.Inventory.StockIn],
            MovementKind.Dispense => [Permissions.Prescriptions.Dispense],
            MovementKind.Consume => [Permissions.Inventory.StockOut, Permissions.Procedures.Perform],
            _ => [Permissions.Inventory.StockOut],
        };
        if (await ScopeAsync(ctx, m.BranchId, permissions) is { } denied) return denied;
        if (m.Kind == MovementKind.Dispense && (m.SourceId is not { } d || await FindAsync<Dispensation>(ctx.Db, d) is not { } disp || disp.ItemId != m.ItemId))
            return "A dispensing movement must belong to a dispensation of the same item.";
        return null;
    }

    public static string? ValidateDispensation(object o)
    {
        var d = (Dispensation)o;
        if (d.Quantity <= 0) return "The quantity must be more than zero.";
        if (d.UnitPrice < 0) return "The price cannot be negative.";
        if (string.IsNullOrWhiteSpace(d.ItemName)) return "The medicine needs a name.";
        return d.Notes?.Length > 300 ? "The notes are too long." : null;
    }

    public static async Task<string?> AuthorizeDispensationAsync(SyncWriteContext ctx)
    {
        var d = (Dispensation)ctx.Entity;
        if (!ctx.IsNew) return "A dispensation cannot be changed; return the stock instead.";
        if (await ScopeAsync(ctx, d.BranchId, Permissions.Prescriptions.Dispense) is { } denied) return denied;
        if (!await ctx.Db.Patients.AnyAsync(p => p.Id == d.PatientId)) return "Patient not found on the server.";
        if (await FindAsync<InventoryItem>(ctx.Db, d.ItemId) is null) return "Item not found.";
        if (d.PrescriptionItemId is { } pid)
        {
            var item = await FindAsync<PrescriptionItem>(ctx.Db, pid);
            var rx = item is null ? null : await FindAsync<Prescription>(ctx.Db, item.PrescriptionId);
            if (rx?.PatientId != d.PatientId) return "The prescription line belongs to another patient.";
        }
        return null;
    }
}

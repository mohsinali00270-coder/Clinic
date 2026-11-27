using Clinic.Api.Security;
using Clinic.Application.Clinical;
using Clinic.Shared.Clinical;
using Clinic.Shared.Reports;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

/// <summary>FR-INV-01 item master (admin). Devices receive it through sync.</summary>
[ApiController]
[Route(InventoryRoutes.Items)]
public sealed class InventoryItemsController(InventoryReportService service) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<InventoryItemDto>> List(CancellationToken ct) => service.ItemsAsync(ct);

    [HttpPost, HasPermission(Permissions.CareSetup.Manage)]
    public Task<InventoryItemDto> Create(SaveInventoryItemRequest r, CancellationToken ct) => service.SaveItemAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.CareSetup.Manage)]
    public Task<InventoryItemDto> Update(Guid id, SaveInventoryItemRequest r, CancellationToken ct) => service.SaveItemAsync(id, r, ct);
}

/// <summary>FR-REP-01…03 on the server (FR-REP-02: devices compute the same reports offline).</summary>
[ApiController]
public sealed class ReportsController(InventoryReportService service) : ControllerBase
{
    [HttpGet(ReportRoutes.Summary), HasPermission(Permissions.Reports.View)]
    public Task<ReportSummary> Summary(Guid branchId, DateTime from, DateTime to, CancellationToken ct) => service.SummaryAsync(branchId, from, to, ct);

    [HttpGet(ReportRoutes.Breakdown), HasPermission(Permissions.Reports.View)]
    public Task<FieldBreakdown> Breakdown(string entityType, string fieldKey, Guid branchId, DateTime from, DateTime to, CancellationToken ct) =>
        service.BreakdownAsync(entityType, fieldKey, branchId, from, to, ct);

    [HttpGet(ReportRoutes.Stock), HasPermission(Permissions.Inventory.View)]
    public async Task<object> Stock(Guid branchId, CancellationToken ct)
    {
        var (batches, alerts) = await service.StockAsync(branchId, DateTime.Today, ct);
        return new
        {
            Items = batches.GroupBy(b => b.ItemId).Select(g => new { ItemId = g.Key, g.First().ItemName, OnHand = g.Sum(b => b.Stock.OnHand) }).OrderBy(x => x.ItemName),
            Alerts = alerts,
        };
    }
}

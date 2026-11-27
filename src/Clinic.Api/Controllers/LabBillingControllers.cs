using Clinic.Api.Security;
using Clinic.Application.Clinical;
using Clinic.Shared.Clinical;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

/// <summary>FR-LAB-01: the test menu (admin). Devices receive it through sync.</summary>
[ApiController]
[Route(LabBillingRoutes.LabTests)]
public sealed class LabTestsController(LabBillingService service) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<LabTestDto>> List(CancellationToken ct) => service.LabTestsAsync(ct);

    [HttpPost, HasPermission(Permissions.CareSetup.Manage)]
    public Task<LabTestDto> Create(SaveLabTestRequest r, CancellationToken ct) => service.SaveLabTestAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.CareSetup.Manage)]
    public Task<LabTestDto> Update(Guid id, SaveLabTestRequest r, CancellationToken ct) => service.SaveLabTestAsync(id, r, ct);
}

/// <summary>FR-BIL-01: services and fees, tenant-wide or per branch (admin).</summary>
[ApiController]
[Route(LabBillingRoutes.Services)]
public sealed class ServicesController(LabBillingService service) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<ServiceItemDto>> List(CancellationToken ct) => service.ServicesAsync(ct);

    [HttpPost, HasPermission(Permissions.CareSetup.Manage)]
    public Task<ServiceItemDto> Create(SaveServiceItemRequest r, CancellationToken ct) => service.SaveServiceAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.CareSetup.Manage)]
    public Task<ServiceItemDto> Update(Guid id, SaveServiceItemRequest r, CancellationToken ct) => service.SaveServiceAsync(id, r, ct);
}

/// <summary>FR-BIL-03/06: server figures to check device closings and balances against.</summary>
[ApiController]
public sealed class BillingController(LabBillingService service) : ControllerBase
{
    [HttpGet(LabBillingRoutes.DaySummary), HasPermission(Permissions.Billing.View)]
    public Task<DaySummary> DaySummary(Guid branchId, DateTime date, Guid? userId, CancellationToken ct) =>
        service.DaySummaryAsync(branchId, date, userId, ct);

    [HttpGet(LabBillingRoutes.PatientBalance + "/{patientId:guid}"), HasPermission(Permissions.Billing.View)]
    public Task<PatientBalanceDto> Balance(Guid patientId, CancellationToken ct) => service.BalanceAsync(patientId, ct);
}

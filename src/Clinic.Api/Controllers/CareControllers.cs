using Clinic.Api.Security;
using Clinic.Application.Clinical;
using Clinic.Shared.Clinical;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

/// <summary>FR-CASE-02: case types (admin). Devices receive them through sync.</summary>
[ApiController]
[Route(CareRoutes.CaseTypes)]
public sealed class CaseTypesController(CareSetupService setup) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<CaseTypeDto>> List(CancellationToken ct) => setup.CaseTypesAsync(ct);

    [HttpPost, HasPermission(Permissions.CareSetup.Manage)]
    public Task<CaseTypeDto> Create(SaveCaseTypeRequest r, CancellationToken ct) => setup.SaveCaseTypeAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.CareSetup.Manage)]
    public Task<CaseTypeDto> Update(Guid id, SaveCaseTypeRequest r, CancellationToken ct) => setup.SaveCaseTypeAsync(id, r, ct);
}

/// <summary>FR-PRC-01: procedure master (admin).</summary>
[ApiController]
[Route(CareRoutes.ProcedureTypes)]
public sealed class ProcedureTypesController(CareSetupService setup) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<ProcedureTypeDto>> List(CancellationToken ct) => setup.ProcedureTypesAsync(ct);

    [HttpPost, HasPermission(Permissions.CareSetup.Manage)]
    public Task<ProcedureTypeDto> Create(SaveProcedureTypeRequest r, CancellationToken ct) => setup.SaveProcedureTypeAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.CareSetup.Manage)]
    public Task<ProcedureTypeDto> Update(Guid id, SaveProcedureTypeRequest r, CancellationToken ct) => setup.SaveProcedureTypeAsync(id, r, ct);
}

/// <summary>FR-PRC-03/04: beds, rooms and day-care chairs with their charges (admin).</summary>
[ApiController]
[Route(CareRoutes.Beds)]
public sealed class BedsController(CareSetupService setup) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<BedDto>> List(Guid? branchId, CancellationToken ct) => setup.BedsAsync(branchId, ct);

    [HttpPost, HasPermission(Permissions.CareSetup.Manage)]
    public Task<BedDto> Create(SaveBedRequest r, CancellationToken ct) => setup.SaveBedAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.CareSetup.Manage)]
    public Task<BedDto> Update(Guid id, SaveBedRequest r, CancellationToken ct) => setup.SaveBedAsync(id, r, ct);
}

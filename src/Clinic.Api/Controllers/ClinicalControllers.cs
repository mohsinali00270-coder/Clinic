using Clinic.Api.Security;
using Clinic.Application.Clinical;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

/// <summary>FR-RX-01 drug master (admin). Devices receive it through sync.</summary>
[ApiController]
[Route(ClinicalRoutes.Drugs)]
public sealed class DrugsController(ClinicalMasterService masters) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<DrugDto>> List(string? search, CancellationToken ct) => masters.DrugsAsync(search, ct);

    [HttpPost, HasPermission(Permissions.DynamicFields.Manage)]
    public Task<DrugDto> Create(SaveDrugRequest r, CancellationToken ct) => masters.SaveDrugAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.DynamicFields.Manage)]
    public Task<DrugDto> Update(Guid id, SaveDrugRequest r, CancellationToken ct) => masters.SaveDrugAsync(id, r, ct);

    [HttpPost("import"), HasPermission(Permissions.DynamicFields.Manage)]
    public Task<CsvImportResult> Import(CsvImportRequest r, CancellationToken ct) => masters.ImportDrugsAsync(r.Csv, ct);
}

/// <summary>FR-ENC-04 ICD-10 list (admin import; devices search their synced copy offline).</summary>
[ApiController]
[Route(ClinicalRoutes.Icd10)]
public sealed class Icd10Controller(ClinicalMasterService masters) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<Icd10Dto>> List(string? search, CancellationToken ct) => masters.IcdAsync(search, ct);

    [HttpPost("import"), HasPermission(Permissions.DynamicFields.Manage)]
    public Task<CsvImportResult> Import(CsvImportRequest r, CancellationToken ct) => masters.ImportIcdAsync(r.Csv, ct);
}

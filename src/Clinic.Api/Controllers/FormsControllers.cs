using Clinic.Api.Security;
using Clinic.Application.Forms;
using Clinic.Shared.Forms;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

/// <summary>§5.1 entity types a form can attach to.</summary>
[ApiController]
[Route(FormRoutes.EntityTypes)]
public sealed class EntityTypesController : ControllerBase
{
    [HttpGet, Authorize]
    public IEnumerable<EntityTypeDto> List() =>
        DynamicEntityTypes.All.Select(e => new EntityTypeDto(e.Name, e.DisplayName, e.Variables, e.AvailableFrom));
}

/// <summary>FR-DYN-10…13: form builder. Online-only admin tool; devices receive published versions through sync.</summary>
[ApiController]
[Route(FormRoutes.Forms)]
[HasPermission(Permissions.DynamicFields.Manage)]
public sealed class FormsController(FormService forms) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<FormSummaryDto>> List(string? entityType, CancellationToken ct) => forms.ListAsync(entityType, ct);

    [HttpGet("{id:guid}")]
    public Task<FormDto> Get(Guid id, CancellationToken ct) => forms.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<FormDto>> Create(CreateFormRequest request, CancellationToken ct)
    {
        var created = await forms.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public Task<FormDto> SaveDraft(Guid id, SaveFormDraftRequest request, CancellationToken ct) => forms.SaveDraftAsync(id, request, ct);

    [HttpPost("validate")]
    public Task<FormValidationResult> Validate(ValidateFormRequest request, CancellationToken ct) => forms.ValidateAsync(request, ct);

    [HttpPost("{id:guid}/publish")]
    public Task<FormVersionDto> Publish(Guid id, CancellationToken ct) => forms.PublishAsync(id, ct);

    [HttpGet("{id:guid}/versions")]
    public Task<IReadOnlyList<FormVersionDto>> Versions(Guid id, CancellationToken ct) => forms.VersionsAsync(id, ct);

    [HttpPost("{id:guid}/clone")]
    public Task<FormDto> Clone(Guid id, CancellationToken ct) => forms.CloneAsync(id, ct);

    /// <summary>FR-DYN-13 export as JSON (the draft definition).</summary>
    [HttpGet("{id:guid}/export")]
    public Task<FormDefinition> Export(Guid id, CancellationToken ct) => forms.ExportAsync(id, ct);

    [HttpPost("import")]
    public Task<FormDto> Import(FormDefinition definition, CancellationToken ct) => forms.ImportAsync(definition, ct);
}

[ApiController]
[Route(FormRoutes.FormVersions)]
public sealed class FormVersionsController(FormService forms) : ControllerBase
{
    [HttpGet("{id:guid}"), HasPermission(Permissions.DynamicFields.Manage)]
    public Task<FormVersionDto> Get(Guid id, CancellationToken ct) => forms.GetVersionAsync(id, ct);
}

[ApiController]
[Route(FormRoutes.FormTemplates)]
public sealed class FormTemplatesController : ControllerBase
{
    [HttpGet, HasPermission(Permissions.DynamicFields.Manage)]
    public IReadOnlyList<FormTemplateDto> List(string? entityType) => FormService.Templates(entityType);
}

[ApiController]
[Route(FormRoutes.Lookups)]
[HasPermission(Permissions.DynamicFields.Manage)]
public sealed class LookupsController(LookupService lookups) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<LookupListDto>> List(CancellationToken ct) => lookups.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<LookupListDetailDto> Get(Guid id, CancellationToken ct) => lookups.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<LookupListDto>> Create(SaveLookupListRequest request, CancellationToken ct)
    {
        var created = await lookups.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public Task<LookupListDto> Update(Guid id, SaveLookupListRequest request, CancellationToken ct) => lookups.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await lookups.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/items")]
    public Task<LookupItemDto> AddItem(Guid id, SaveLookupItemRequest request, CancellationToken ct) => lookups.SaveItemAsync(id, null, request, ct);

    [HttpPut("{id:guid}/items/{itemId:guid}")]
    public Task<LookupItemDto> UpdateItem(Guid id, Guid itemId, SaveLookupItemRequest request, CancellationToken ct) =>
        lookups.SaveItemAsync(id, itemId, request, ct);

    [HttpPost("{id:guid}/import")]
    public Task<ImportLookupItemsResult> Import(Guid id, ImportLookupItemsRequest request, CancellationToken ct) =>
        lookups.ImportCsvAsync(id, request, ct);
}

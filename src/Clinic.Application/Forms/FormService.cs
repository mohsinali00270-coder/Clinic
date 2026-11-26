using System.Text.Json;
using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Forms;
using Clinic.Shared.Forms;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Forms;

/// <summary>
/// FR-DYN-10…13: form builder back end. Drafts are saved freely (problems are reported, not blocking);
/// publishing requires a valid definition that is compatible with earlier versions and creates an immutable,
/// synced <see cref="FormVersion"/>.
/// </summary>
public sealed class FormService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    public async Task<IReadOnlyList<FormSummaryDto>> ListAsync(string? entityType, CancellationToken ct)
    {
        var query = db.Forms.AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(f => f.EntityType == entityType);
        return await query
            .OrderBy(f => f.EntityType).ThenBy(f => f.Name)
            .Select(f => new FormSummaryDto(f.Id, f.Name, f.EntityType, f.ContextBranchId, f.ContextSpecialty, f.IsActive,
                f.CurrentVersionNo, f.HasUnpublishedChanges, f.UpdatedAt ?? f.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<FormDto> GetAsync(Guid id, CancellationToken ct) => await ToDtoAsync(await LoadAsync(id, ct), ct);

    public async Task<FormDto> CreateAsync(CreateFormRequest request, CancellationToken ct)
    {
        var entity = DynamicEntityTypes.Find(request.EntityType)
                     ?? throw new RequestValidationException("entityType", $"Unknown entity type '{request.EntityType}'.");
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RequestValidationException("name", "The form needs a name.");
        await CheckBranchAsync(request.ContextBranchId, ct);

        FormDefinition definition;
        if (request.TemplateKey is { } key)
        {
            var template = FormTemplates.Find(key) ?? throw new RequestValidationException("templateKey", $"Unknown template '{key}'.");
            if (template.EntityType != entity.Name)
                throw new RequestValidationException("templateKey", $"Template '{key}' is for {template.EntityType}, not {entity.Name}.");
            definition = template.Definition.Clone();
        }
        else
        {
            definition = request.Definition?.Clone() ?? new FormDefinition
            {
                Sections = [new SectionDefinition { Key = "main", Title = new LocalizedText("Details") }],
            };
        }

        definition.EntityType = entity.Name;
        definition.Name.En = request.Name.Trim();

        var form = new Form
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name.Trim(),
            EntityType = entity.Name,
            ContextBranchId = request.ContextBranchId,
            ContextSpecialty = Normalize.Optional(request.ContextSpecialty),
            DraftJson = FormJson.Serialize(definition),
        };
        db.Forms.Add(form);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(form, ct);
    }

    public async Task<FormDto> SaveDraftAsync(Guid id, SaveFormDraftRequest request, CancellationToken ct)
    {
        var form = await LoadAsync(id, ct);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new RequestValidationException("name", "The form needs a name.");
        await CheckBranchAsync(request.ContextBranchId, ct);

        var definition = request.Definition.Clone();
        definition.EntityType = form.EntityType; // a form never changes entity type
        definition.Name.En = request.Name.Trim();

        form.Name = request.Name.Trim();
        form.ContextBranchId = request.ContextBranchId;
        form.ContextSpecialty = Normalize.Optional(request.ContextSpecialty);
        form.DraftJson = FormJson.Serialize(definition);
        form.HasUnpublishedChanges = true;

        if (form.IsActive != request.IsActive)
        {
            form.IsActive = request.IsActive;
            // Switching a form off removes it from devices (they ignore inactive versions) without touching records.
            var versions = await db.FormVersions.Where(v => v.FormId == id && v.IsActive != request.IsActive).ToListAsync(ct);
            foreach (var v in versions) v.IsActive = request.IsActive;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(form, ct);
    }

    public async Task<FormValidationResult> ValidateAsync(ValidateFormRequest request, CancellationToken ct)
    {
        var errors = await ProblemsAsync(request.Definition, request.FormId, ct);
        return new FormValidationResult(errors.Count == 0, errors);
    }

    /// <summary>FR-DYN-11: Draft → Publish creates a new immutable version; the previous one stops being current.</summary>
    public async Task<FormVersionDto> PublishAsync(Guid id, CancellationToken ct)
    {
        var form = await LoadAsync(id, ct);
        var definition = FormJson.Deserialize(form.DraftJson);
        var problems = await ProblemsAsync(definition, id, ct);
        if (problems.Count > 0)
        {
            throw new RequestValidationException(problems
                .GroupBy(p => string.IsNullOrEmpty(p.Path) ? "definition" : p.Path)
                .ToDictionary(g => g.Key, g => g.Select(p => p.Message).ToArray()));
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            form = await LoadAsync(id, ct);

            var previous = await db.FormVersions.Where(v => v.FormId == id && v.IsCurrent).ToListAsync(ct);
            foreach (var p in previous) p.IsCurrent = false;

            var lastNo = await db.FormVersions.Where(v => v.FormId == id).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0;
            var publisher = await db.Users.Where(u => u.Id == user.UserId).Select(u => u.FullName).FirstOrDefaultAsync(ct);

            var version = new FormVersion
            {
                Id = Guid.CreateVersion7(),
                FormId = id,
                VersionNo = lastNo + 1,
                EntityType = form.EntityType,
                ContextBranchId = form.ContextBranchId,
                ContextSpecialty = form.ContextSpecialty,
                DefinitionJson = form.DraftJson,
                PublishedAt = clock.GetUtcNow().UtcDateTime,
                PublishedByName = publisher,
                IsCurrent = true,
                IsActive = form.IsActive,
            };
            db.FormVersions.Add(version);

            form.CurrentVersionId = version.Id;
            form.CurrentVersionNo = version.VersionNo;
            form.HasUnpublishedChanges = false;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToDto(version);
        });
    }

    public async Task<IReadOnlyList<FormVersionDto>> VersionsAsync(Guid formId, CancellationToken ct)
    {
        await LoadAsync(formId, ct);
        var versions = await db.FormVersions.Where(v => v.FormId == formId).OrderByDescending(v => v.VersionNo).ToListAsync(ct);
        return versions.Select(ToDto).ToList();
    }

    public async Task<FormVersionDto> GetVersionAsync(Guid versionId, CancellationToken ct) =>
        ToDto(await db.FormVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct) ?? throw new NotFoundException("Form version", versionId));

    /// <summary>FR-DYN-13: clone a form as a new draft (e.g. to make a branch-specific variant).</summary>
    public async Task<FormDto> CloneAsync(Guid id, CancellationToken ct)
    {
        var source = await LoadAsync(id, ct);
        return await CreateAsync(new CreateFormRequest($"Copy of {source.Name}", source.EntityType, source.ContextBranchId,
            source.ContextSpecialty, null, FormJson.Deserialize(source.DraftJson)), ct);
    }

    public async Task<FormDefinition> ExportAsync(Guid id, CancellationToken ct) => FormJson.Deserialize((await LoadAsync(id, ct)).DraftJson);

    public Task<FormDto> ImportAsync(FormDefinition definition, CancellationToken ct) =>
        CreateAsync(new CreateFormRequest(string.IsNullOrWhiteSpace(definition.Name.En) ? "Imported form" : definition.Name.En,
            definition.EntityType, Definition: definition), ct);

    public static IReadOnlyList<FormTemplateDto> Templates(string? entityType) =>
        FormTemplates.All
            .Where(t => entityType is null || string.Equals(t.EntityType, entityType, StringComparison.OrdinalIgnoreCase))
            .Select(t => new FormTemplateDto(t.Key, t.Name, t.EntityType, t.Description))
            .ToList();

    // ---------------------------------------------------------------------

    private async Task<List<DefinitionError>> ProblemsAsync(FormDefinition definition, Guid? formId, CancellationToken ct)
    {
        var lookupKeys = (await db.LookupLists.Select(l => l.Key).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var problems = FormDefinitionValidator.Validate(definition, lookupKeys.Contains);

        if (formId is { } id)
        {
            var versions = await db.FormVersions.Where(v => v.FormId == id)
                .Select(v => new { v.Id, v.DefinitionJson }).ToListAsync(ct);
            if (versions.Count > 0)
            {
                var ids = versions.Select(v => v.Id).ToList();
                var keysWithData = (await db.FieldValues.Where(v => ids.Contains(v.FormVersionId))
                    .Select(v => v.FieldKey).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
                problems.AddRange(FormDefinitionValidator.CheckCompatibility(
                    versions.Select(v => FormJson.Deserialize(v.DefinitionJson)), definition, keysWithData));
            }
        }
        return problems;
    }

    private async Task CheckBranchAsync(Guid? branchId, CancellationToken ct)
    {
        if (branchId is { } b && !await db.Branches.AnyAsync(x => x.Id == b, ct))
            throw new RequestValidationException("contextBranchId", "Branch not found.");
    }

    private async Task<Form> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Forms.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Form", id);

    private async Task<FormDto> ToDtoAsync(Form f, CancellationToken ct)
    {
        FormDefinition draft;
        try { draft = FormJson.Deserialize(f.DraftJson); }
        catch (JsonException) { draft = new FormDefinition { EntityType = f.EntityType, Name = new(f.Name) }; }

        var problems = await ProblemsAsync(draft, f.Id, ct);
        return new FormDto(f.Id, f.Name, f.EntityType, f.ContextBranchId, f.ContextSpecialty, f.IsActive,
            f.CurrentVersionNo, f.CurrentVersionId, f.HasUnpublishedChanges, draft, problems);
    }

    private static FormVersionDto ToDto(FormVersion v) => new(
        v.Id, v.FormId, v.VersionNo, v.PublishedAt, v.PublishedByName, v.IsCurrent, FormJson.Deserialize(v.DefinitionJson));
}

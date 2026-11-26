using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Domain.Tenancy;
using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Tenancy;

public sealed class BranchService(IAppDbContext db, ICurrentUser user, IValidator<SaveBranchRequest> validator)
{
    public async Task<IReadOnlyList<BranchDto>> ListAsync(CancellationToken ct) =>
        await Visible(Permissions.Branches.View)
            .OrderBy(b => b.Code)
            .Select(b => new BranchDto(b.Id, b.Code, b.Name, b.Address, b.Phone, b.IsActive))
            .ToListAsync(ct);

    public async Task<BranchDto> GetAsync(Guid id, CancellationToken ct)
    {
        var b = await Visible(Permissions.Branches.View).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Branch", id);
        return ToDto(b);
    }

    public async Task<BranchDto> CreateAsync(SaveBranchRequest request, CancellationToken ct)
    {
        if (user.GetScope(Permissions.Branches.Manage) != PermissionScope.All)
            throw new ForbiddenException("Creating branches needs the Branches.Manage permission for all branches.");

        await validator.ValidateOrThrowAsync(request, ct);
        var code = Normalize.Key(request.Code);
        await EnsureCodeFreeAsync(code, null, ct);

        var branch = new Branch();
        Apply(branch, request, code);
        db.Branches.Add(branch);
        await db.SaveChangesAsync(ct);
        return ToDto(branch);
    }

    public async Task<BranchDto> UpdateAsync(Guid id, SaveBranchRequest request, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(request, ct);
        var branch = await Visible(Permissions.Branches.Manage).FirstOrDefaultAsync(x => x.Id == id, ct)
                     ?? throw new NotFoundException("Branch", id);

        var code = Normalize.Key(request.Code);
        await EnsureCodeFreeAsync(code, id, ct);
        Apply(branch, request, code);
        await db.SaveChangesAsync(ct);
        return ToDto(branch);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (user.GetScope(Permissions.Branches.Manage) != PermissionScope.All)
            throw new ForbiddenException();

        var branch = await db.Branches.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Branch", id);
        if (await db.UserBranches.AnyAsync(ub => ub.BranchId == id, ct))
            throw new ConflictException("Users are still assigned to this branch. Deactivate it instead, or move the users first.");

        branch.IsDeleted = true;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>FR-AUTH-02: OwnBranch scope limits the caller to their assigned branches.</summary>
    private IQueryable<Branch> Visible(string permission)
    {
        var scope = user.GetScope(permission);
        if (scope == PermissionScope.All) return db.Branches;
        var mine = user.BranchIds;
        return db.Branches.Where(b => mine.Contains(b.Id));
    }

    private async Task EnsureCodeFreeAsync(string code, Guid? exceptId, CancellationToken ct)
    {
        if (await db.Branches.AnyAsync(b => b.Code == code && b.Id != exceptId, ct))
            throw new RequestValidationException("code", "Another branch already uses this code.");
    }

    private static void Apply(Branch b, SaveBranchRequest r, string code)
    {
        b.Code = code;
        b.Name = r.Name.Trim();
        b.Address = Normalize.Optional(r.Address);
        b.Phone = Normalize.Optional(r.Phone);
        b.IsActive = r.IsActive;
    }

    private static BranchDto ToDto(Branch b) => new(b.Id, b.Code, b.Name, b.Address, b.Phone, b.IsActive);
}

public sealed class SaveBranchValidator : AbstractValidator<SaveBranchRequest>
{
    public SaveBranchValidator()
    {
        RuleFor(x => x.Code).Code(10);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Address).MaximumLength(500);
        RuleFor(x => x.Phone).MaximumLength(30);
    }
}

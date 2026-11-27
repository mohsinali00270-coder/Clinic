using System.Text;
using Clinic.Api.Security;
using Clinic.Application.Clinical;
using Clinic.Shared.Clinical;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

/// <summary>FR-BIL-10 package master.</summary>
[ApiController]
[Route(AccountsRoutes.Packages)]
public sealed class PackagesController(AccountsService accounts) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<PackagePlanDto>> List(CancellationToken ct) => accounts.PackagesAsync(ct);

    [HttpPost, HasPermission(Permissions.CareSetup.Manage)]
    public Task<PackagePlanDto> Create(SavePackagePlanRequest r, CancellationToken ct) => accounts.SavePackageAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.CareSetup.Manage)]
    public Task<PackagePlanDto> Update(Guid id, SavePackagePlanRequest r, CancellationToken ct) => accounts.SavePackageAsync(id, r, ct);
}

/// <summary>FR-BIL-12 payers, settlements, statements with ageing, claim batches.</summary>
[ApiController]
[Route(AccountsRoutes.Payers)]
public sealed class PayersController(AccountsService accounts) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<PayerDto>> List(CancellationToken ct) => accounts.PayersAsync(ct);

    [HttpPost, HasPermission(Permissions.Billing.Accounts)]
    public Task<PayerDto> Create(SavePayerRequest r, CancellationToken ct) => accounts.SavePayerAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.Billing.Accounts)]
    public Task<PayerDto> Update(Guid id, SavePayerRequest r, CancellationToken ct) => accounts.SavePayerAsync(id, r, ct);

    [HttpGet("{id:guid}/statement"), HasPermission(Permissions.Billing.Accounts)]
    public Task<PayerStatementDto> Statement(Guid id, DateTime from, DateTime to, CancellationToken ct) => accounts.StatementAsync(id, from, to, ct);

    [HttpGet("{id:guid}/claims.csv"), HasPermission(Permissions.Billing.Accounts)]
    public async Task<IActionResult> Claims(Guid id, DateTime from, DateTime to, CancellationToken ct) =>
        File(Encoding.UTF8.GetBytes(await accounts.ClaimsCsvAsync(id, from, to, ct)), "text/csv", $"claims-{from:yyyyMMdd}-{to:yyyyMMdd}.csv");
}

[ApiController]
[Route(AccountsRoutes.PayerSettlements)]
public sealed class PayerSettlementsController(AccountsService accounts) : ControllerBase
{
    [HttpPost, HasPermission(Permissions.Billing.Accounts)]
    public Task<PayerSettlementDto> Create(SavePayerSettlementRequest r, CancellationToken ct) => accounts.AddSettlementAsync(r, ct);
}

/// <summary>FR-COM-01 message templates.</summary>
[ApiController]
[Route(AccountsRoutes.MessageTemplates)]
public sealed class MessageTemplatesController(AccountsService accounts) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<MessageTemplateDto>> List(CancellationToken ct) => accounts.TemplatesAsync(ct);

    [HttpPost, HasPermission(Permissions.CareSetup.Manage)]
    public Task<MessageTemplateDto> Create(SaveMessageTemplateRequest r, CancellationToken ct) => accounts.SaveTemplateAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.CareSetup.Manage)]
    public Task<MessageTemplateDto> Update(Guid id, SaveMessageTemplateRequest r, CancellationToken ct) => accounts.SaveTemplateAsync(id, r, ct);
}

/// <summary>FR-DOC-04/05 doctor share rules and payout statements (draft → approved → paid).</summary>
[ApiController]
public sealed class DoctorShareController(AccountsService accounts) : ControllerBase
{
    [HttpGet(AccountsRoutes.ShareRules), HasPermission(Permissions.Billing.Accounts)]
    public Task<IReadOnlyList<ShareRuleDto>> Rules(Guid? doctorId, CancellationToken ct) => accounts.ShareRulesAsync(doctorId, ct);

    [HttpPost(AccountsRoutes.ShareRules), HasPermission(Permissions.Billing.Accounts)]
    public Task<ShareRuleDto> SaveRule(SaveShareRuleRequest r, CancellationToken ct) => accounts.SaveShareRuleAsync(r, ct);

    [HttpGet(AccountsRoutes.Payouts), HasPermission(Permissions.Billing.Accounts)]
    public Task<IReadOnlyList<PayoutDto>> Payouts(Guid? doctorId, CancellationToken ct) => accounts.PayoutsAsync(doctorId, ct);

    [HttpPost(AccountsRoutes.Payouts), HasPermission(Permissions.Billing.Accounts)]
    public Task<PayoutDto> Create(CreatePayoutRequest r, CancellationToken ct) => accounts.CreatePayoutAsync(r, ct);

    [HttpPost(AccountsRoutes.Payouts + "/{id:guid}/approve"), HasPermission(Permissions.Billing.Approve)]
    public Task<PayoutDto> Approve(Guid id, CancellationToken ct) => accounts.ApprovePayoutAsync(id, ct);

    [HttpPost(AccountsRoutes.Payouts + "/{id:guid}/pay"), HasPermission(Permissions.Billing.Accounts)]
    public Task<PayoutDto> Pay(Guid id, PayPayoutRequest r, CancellationToken ct) => accounts.PayPayoutAsync(id, r, ct);
}

/// <summary>FR-BIL-15 tax summary and FR-BIL-17 profit view.</summary>
[ApiController]
public sealed class FinanceReportsController(AccountsService accounts) : ControllerBase
{
    [HttpGet(AccountsRoutes.TaxSummary), HasPermission(Permissions.Reports.Financial)]
    public Task<TaxSummaryDto> Tax(DateTime from, DateTime to, Guid? branchId, CancellationToken ct) => accounts.TaxAsync(from, to, branchId, ct);

    [HttpGet(AccountsRoutes.Profit), HasPermission(Permissions.Reports.Financial)]
    public Task<ProfitDto> Profit(DateTime from, DateTime to, Guid? branchId, CancellationToken ct) => accounts.ProfitAsync(from, to, branchId, ct);
}

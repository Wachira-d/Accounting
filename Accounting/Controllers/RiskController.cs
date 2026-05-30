using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Risk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Risk endpoints — customer payment risk (AR), vendor risk (AP),
/// smart approval routing. All scoped to a single company; all return
/// scored + tiered + reasoned output that the UI surfaces directly and
/// the corresponding AI feature can wrap with prose.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/risk")]
[Authorize]
public class RiskController : ControllerBase
{
    private readonly ICustomerPaymentRiskService _customer;
    private readonly IVendorRiskScoringService _vendor;
    private readonly ISmartApprovalRoutingService _approval;

    public RiskController(ICustomerPaymentRiskService customer,
        IVendorRiskScoringService vendor,
        ISmartApprovalRoutingService approval)
    {
        _customer = customer; _vendor = vendor; _approval = approval;
    }

    /// <summary>Customer payment risk — predicts which customers will
    /// pay late so AR can prioritise collection effort. Lookback in
    /// months (default 12; capped 1-36).</summary>
    [HttpGet("customer-payment")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<CustomerPaymentRisk>>>> GetCustomerPaymentRisk(
        Guid companyId, [FromQuery] int lookbackMonths = 12, CancellationToken ct = default)
    {
        var rows = await _customer.AnalyzeAsync(companyId, lookbackMonths, ct);
        return Ok(new ApiResponse<IReadOnlyList<CustomerPaymentRisk>>(true, rows));
    }

    /// <summary>Vendor risk — surface vendors with high void rate,
    /// price volatility, or declining recent activity. Proactive AP
    /// + procurement signal.</summary>
    [HttpGet("vendor")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<VendorRisk>>>> GetVendorRisk(
        Guid companyId, [FromQuery] int lookbackMonths = 12, CancellationToken ct = default)
    {
        var rows = await _vendor.ScoreAllAsync(companyId, lookbackMonths, ct);
        return Ok(new ApiResponse<IReadOnlyList<VendorRisk>>(true, rows));
    }

    /// <summary>Smart approval routing — for a draft document, returns
    /// the most-likely approver chain based on past approvals for
    /// similar shape (type + amount bucket + requester).</summary>
    [HttpGet("approval-suggestion")]
    public async Task<ActionResult<ApiResponse<SmartApprovalSuggestion?>>> GetApprovalSuggestion(
        Guid companyId,
        [FromQuery] DocumentType docType,
        [FromQuery] decimal amount,
        [FromQuery] Guid requestingUserId,
        [FromQuery] int lookbackMonths = 6,
        CancellationToken ct = default)
    {
        var s = await _approval.SuggestForDocumentAsync(companyId, docType, amount,
            requestingUserId, lookbackMonths, ct);
        return Ok(new ApiResponse<SmartApprovalSuggestion?>(true, s));
    }
}

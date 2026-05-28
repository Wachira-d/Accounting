using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/sensitivity")]
[Authorize]
public class SensitivityController : ControllerBase
{
    private readonly ISensitivityService _sensitivity;
    public SensitivityController(ISensitivityService sensitivity) { _sensitivity = sensitivity; }

    /// <summary>Full rule matrix (kind × role × canView) for the settings page.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<SensitivityRuleDto>>>> GetRules(Guid companyId)
        => Ok(new ApiResponse<List<SensitivityRuleDto>>(true, await _sensitivity.GetRulesAsync(companyId)));

    public record UpdateRuleRequest(SensitivityKind Kind, UserRole Role, bool CanView);

    /// <summary>Toggle one role's access to one Kind. Owner only.</summary>
    [HttpPost("rules")]
    public async Task<ActionResult<ApiResponse<string>>> SetRule(Guid companyId, [FromBody] UpdateRuleRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _sensitivity.SetRuleAsync(companyId, req.Kind, req.Role, req.CanView, userId);
        return Ok(new ApiResponse<string>(true, null, "บันทึกสิทธิ์สำเร็จ"));
    }

    /// <summary>Current user's allow-list — convenience for the UI so it can
    /// hide menus/buttons it knows the user can't reach.</summary>
    [HttpGet("my-access")]
    public async Task<ActionResult<ApiResponse<object>>> GetMyAccess(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var visible = await _sensitivity.GetVisibleKindsAsync(companyId, userId);
        return Ok(new ApiResponse<object>(true, new
        {
            visibleKinds = visible.Select(k => k.ToString()).ToList(),
            canViewPayroll = visible.Contains(SensitivityKind.Payroll),
            canViewExecutivePay = visible.Contains(SensitivityKind.ExecutivePay),
            canViewHrPersonal = visible.Contains(SensitivityKind.HrPersonal),
            canViewConfidential = visible.Contains(SensitivityKind.Confidential),
        }));
    }
}

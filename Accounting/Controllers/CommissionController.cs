using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Commission;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/commissions")]
[Authorize]
public class CommissionController : ControllerBase
{
    private readonly ICommissionService _service;
    public CommissionController(ICommissionService service) => _service = service;

    [HttpPost("plans")]
    public async Task<ActionResult<ApiResponse<CommissionPlanResponse>>> CreatePlan(Guid companyId, [FromBody] CreateCommissionPlanRequest request)
        => StatusCode(201, new ApiResponse<CommissionPlanResponse>(true, await _service.CreatePlanAsync(companyId, request)));

    /// <summary>ตัวเลือกฐานคำนวณ/วิธีคำนวณพร้อมป้ายไทย — หน้าเว็บสร้าง dropdown + ป้ายในตารางจากที่นี่
    /// (ตัวตั้ง = <see cref="Accounting.Helpers.CommissionPlanRules"/>) แทนการพิมพ์ชุดค่าซ้ำใน JS</summary>
    [HttpGet("options")]
    public ActionResult<ApiResponse<object>> GetOptions(Guid companyId)
        => Ok(new ApiResponse<object>(true, new
        {
            bases = Accounting.Helpers.CommissionPlanRules.Bases.Select(x => new { value = x.Value, label = x.Label }),
            methods = Accounting.Helpers.CommissionPlanRules.Methods.Select(x => new { value = x.Value, label = x.Label }),
            defaultBasis = Accounting.Helpers.CommissionPlanRules.DefaultBasis,
            defaultMethod = Accounting.Helpers.CommissionPlanRules.DefaultMethod,
        }));

    [HttpGet("plans/{planId:guid}")]
    public async Task<ActionResult<ApiResponse<CommissionPlanResponse>>> GetPlan(Guid companyId, Guid planId)
        => Ok(new ApiResponse<CommissionPlanResponse>(true, await _service.GetPlanByIdAsync(companyId, planId)));

    [HttpGet("plans")]
    public async Task<ActionResult<ApiResponse<List<CommissionPlanResponse>>>> GetPlans(Guid companyId)
        => Ok(new ApiResponse<List<CommissionPlanResponse>>(true, await _service.GetPlansAsync(companyId)));

    [HttpPut("plans/{planId:guid}")]
    public async Task<ActionResult<ApiResponse<CommissionPlanResponse>>> UpdatePlan(Guid companyId, Guid planId, [FromBody] UpdateCommissionPlanRequest request)
        => Ok(new ApiResponse<CommissionPlanResponse>(true, await _service.UpdatePlanAsync(companyId, planId, request)));

    [HttpDelete("plans/{planId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeletePlan(Guid companyId, Guid planId)
    {
        await _service.DeletePlanAsync(companyId, planId);
        return Ok(new ApiResponse<bool>(true, true, "ลบแผนค่าคอมมิชชันสำเร็จ"));
    }

    [HttpPost("plans/{planId:guid}/assign")]
    public async Task<ActionResult<ApiResponse<bool>>> Assign(Guid companyId, Guid planId, [FromBody] AssignCommissionRequest request)
    { await _service.AssignPlanAsync(companyId, planId, request); return Ok(new ApiResponse<bool>(true, true)); }

    [HttpDelete("assignments/{assignmentId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Unassign(Guid companyId, Guid assignmentId)
    {
        await _service.UnassignPlanAsync(companyId, assignmentId);
        return Ok(new ApiResponse<bool>(true, true, "ยกเลิกการกำหนดสำเร็จ"));
    }

    [HttpPost("calculate/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<List<CommissionCalcResponse>>>> Calculate(Guid companyId, int year, int month)
        => Ok(new ApiResponse<List<CommissionCalcResponse>>(true, await _service.CalculateAsync(companyId, year, month)));

    [HttpGet("calculations/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<List<CommissionCalcResponse>>>> GetCalculations(Guid companyId, int year, int month)
        => Ok(new ApiResponse<List<CommissionCalcResponse>>(true, await _service.GetCalculationsAsync(companyId, year, month)));

    [HttpPost("approve/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<bool>>> Approve(Guid companyId, int year, int month)
    { await _service.ApproveCalculationsAsync(companyId, year, month, User.Identity?.Name ?? ""); return Ok(new ApiResponse<bool>(true, true)); }
}

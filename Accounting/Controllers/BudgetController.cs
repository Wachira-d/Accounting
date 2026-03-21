using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Budget;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class BudgetController : ControllerBase
{
    private readonly IBudgetService _budgetService;

    public BudgetController(IBudgetService budgetService)
    {
        _budgetService = budgetService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<BudgetResponse>>>> GetAll(
        Guid companyId, [FromQuery] int? fiscalYear = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
        var result = await _budgetService.GetAllPagedAsync(companyId, fiscalYear, new PagedRequest(page, pageSize, search));
        return Ok(new ApiResponse<PagedResponse<BudgetResponse>>(true, result));
    }

    [HttpGet("{budgetId:guid}")]
    public async Task<ActionResult<ApiResponse<BudgetResponse>>> GetById(Guid companyId, Guid budgetId)
    {
        var result = await _budgetService.GetByIdAsync(companyId, budgetId);
        return Ok(new ApiResponse<BudgetResponse>(true, result));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<BudgetResponse>>> Create(
        Guid companyId, [FromBody] CreateBudgetRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _budgetService.CreateAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<BudgetResponse>(true, result, "สร้างงบประมาณสำเร็จ"));
    }

    [HttpPut("{budgetId:guid}")]
    public async Task<ActionResult<ApiResponse<BudgetResponse>>> Update(
        Guid companyId, Guid budgetId, [FromBody] UpdateBudgetRequest request)
    {
        var result = await _budgetService.UpdateAsync(companyId, budgetId, request);
        return Ok(new ApiResponse<BudgetResponse>(true, result));
    }

    [HttpDelete("{budgetId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid budgetId)
    {
        await _budgetService.DeleteAsync(companyId, budgetId);
        return NoContent();
    }

    [HttpGet("{budgetId:guid}/vs-actual")]
    public async Task<ActionResult<ApiResponse<BudgetVsActualResponse>>> GetBudgetVsActual(Guid companyId, Guid budgetId)
    {
        var result = await _budgetService.GetBudgetVsActualAsync(companyId, budgetId);
        return Ok(new ApiResponse<BudgetVsActualResponse>(true, result));
    }
}

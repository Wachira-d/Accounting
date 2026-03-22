using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Recurring;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class RecurringController : ControllerBase
{
    private readonly IRecurringTransactionService _recurringService;

    public RecurringController(IRecurringTransactionService recurringService)
    {
        _recurringService = recurringService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<RecurringTransactionResponse>>>> GetAll(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
        var result = await _recurringService.GetAllAsync(companyId, new PagedRequest(page, pageSize, search));
        return Ok(new ApiResponse<PagedResponse<RecurringTransactionResponse>>(true, result));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> GetById(Guid companyId, Guid id)
    {
        var result = await _recurringService.GetByIdAsync(companyId, id);
        return Ok(new ApiResponse<RecurringTransactionResponse>(true, result));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> Create(
        Guid companyId, [FromBody] CreateRecurringTransactionRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _recurringService.CreateAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<RecurringTransactionResponse>(true, result, "สร้างรายการที่เกิดซ้ำสำเร็จ"));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> Update(
        Guid companyId, Guid id, [FromBody] UpdateRecurringTransactionRequest request)
    {
        var result = await _recurringService.UpdateAsync(companyId, id, request);
        return Ok(new ApiResponse<RecurringTransactionResponse>(true, result));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid id)
    {
        await _recurringService.DeleteAsync(companyId, id);
        return NoContent();
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> Pause(Guid companyId, Guid id)
    {
        var result = await _recurringService.PauseAsync(companyId, id);
        return Ok(new ApiResponse<RecurringTransactionResponse>(true, result, "หยุดชั่วคราวสำเร็จ"));
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> Resume(Guid companyId, Guid id)
    {
        var result = await _recurringService.ResumeAsync(companyId, id);
        return Ok(new ApiResponse<RecurringTransactionResponse>(true, result, "เปิดใช้งานอีกครั้งสำเร็จ"));
    }

    [HttpPost("{id:guid}/run-now")]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> RunNow(Guid companyId, Guid id)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _recurringService.RunNowAsync(companyId, id, userId);
        return Ok(new ApiResponse<RecurringTransactionResponse>(true, result, "ดำเนินการสำเร็จ"));
    }
}

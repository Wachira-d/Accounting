using Accounting.Models.DTOs;
using Accounting.Models.DTOs.AdvancedArAp;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/ar-ap")]
[Authorize]
public class AdvancedArApController : ControllerBase
{
    private readonly IAdvancedArApService _service;
    public AdvancedArApController(IAdvancedArApService service) => _service = service;

    // Credit
    [HttpPost("contacts/{contactId:guid}/credit")]
    public async Task<ActionResult<ApiResponse<CreditSettingResponse>>> SetCredit(Guid companyId, Guid contactId, [FromBody] SetCreditLimitRequest request)
        => Ok(new ApiResponse<CreditSettingResponse>(true, await _service.SetCreditLimitAsync(companyId, contactId, request)));

    [HttpGet("contacts/{contactId:guid}/credit")]
    public async Task<ActionResult<ApiResponse<CreditSettingResponse>>> GetCredit(Guid companyId, Guid contactId)
        => Ok(new ApiResponse<CreditSettingResponse>(true, await _service.GetCreditSettingAsync(companyId, contactId)));

    [HttpGet("credit-settings")]
    public async Task<ActionResult<ApiResponse<List<CreditSettingResponse>>>> GetAllCredits(Guid companyId)
        => Ok(new ApiResponse<List<CreditSettingResponse>>(true, await _service.GetAllCreditSettingsAsync(companyId)));

    [HttpGet("contacts/{contactId:guid}/credit-check")]
    public async Task<ActionResult<ApiResponse<CreditCheckResponse>>> CheckCredit(Guid companyId, Guid contactId, [FromQuery] decimal amount)
        => Ok(new ApiResponse<CreditCheckResponse>(true, await _service.CheckCreditAsync(companyId, contactId, amount)));

    [HttpPost("contacts/{contactId:guid}/hold")]
    public async Task<ActionResult<ApiResponse<bool>>> Hold(Guid companyId, Guid contactId, [FromQuery] string reason)
    { await _service.HoldContactAsync(companyId, contactId, reason); return Ok(new ApiResponse<bool>(true, true)); }

    [HttpPost("contacts/{contactId:guid}/release")]
    public async Task<ActionResult<ApiResponse<bool>>> Release(Guid companyId, Guid contactId)
    { await _service.ReleaseHoldAsync(companyId, contactId); return Ok(new ApiResponse<bool>(true, true)); }

    // Dunning
    [HttpPost("contacts/{contactId:guid}/dunning")]
    public async Task<ActionResult<ApiResponse<DunningLetterResponse>>> GenerateDunning(Guid companyId, Guid contactId, [FromQuery] int level = 1)
        => Ok(new ApiResponse<DunningLetterResponse>(true, await _service.GenerateDunningLetterAsync(companyId, contactId, level)));

    [HttpPost("dunning/{letterId:guid}/send")]
    public async Task<ActionResult<ApiResponse<DunningLetterResponse>>> SendDunning(Guid companyId, Guid letterId, [FromQuery] string channel = "Email")
        => Ok(new ApiResponse<DunningLetterResponse>(true, await _service.SendDunningLetterAsync(companyId, letterId, channel)));

    [HttpGet("dunning")]
    public async Task<ActionResult<ApiResponse<PagedResponse<DunningLetterResponse>>>> GetDunning(Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<DunningLetterResponse>>(true, await _service.GetDunningLettersAsync(companyId, new PagedRequest(page, pageSize))));

    // Reminders
    [HttpPost("send-reminders")]
    public async Task<ActionResult<ApiResponse<int>>> SendReminders(Guid companyId, [FromQuery] int daysBefore = 3)
        => Ok(new ApiResponse<int>(true, await _service.SendPaymentRemindersAsync(companyId, daysBefore)));

    [HttpGet("reminders")]
    public async Task<ActionResult<ApiResponse<List<PaymentReminderResponse>>>> GetReminders(Guid companyId, [FromQuery] Guid? documentId)
        => Ok(new ApiResponse<List<PaymentReminderResponse>>(true, await _service.GetRemindersAsync(companyId, documentId)));

    // Statement
    [HttpGet("contacts/{contactId:guid}/statement")]
    public async Task<ActionResult<ApiResponse<StatementResponse>>> GetStatement(Guid companyId, Guid contactId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
        => Ok(new ApiResponse<StatementResponse>(true, await _service.GenerateStatementAsync(companyId, contactId, fromDate, toDate)));
}

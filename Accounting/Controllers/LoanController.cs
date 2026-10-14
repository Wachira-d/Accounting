using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Loan;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/loans")]
[Authorize]
public class LoanController : ControllerBase
{
    private readonly ILoanService _service;
    public LoanController(ILoanService service) => _service = service;

    [HttpPost]
    public async Task<ActionResult<ApiResponse<LoanResponse>>> Create(Guid companyId, [FromBody] CreateLoanRequest request)
        => StatusCode(201, new ApiResponse<LoanResponse>(true, await _service.CreateAsync(companyId, request)));

    [HttpGet("{loanId:guid}")]
    public async Task<ActionResult<ApiResponse<LoanResponse>>> GetById(Guid companyId, Guid loanId)
        => Ok(new ApiResponse<LoanResponse>(true, await _service.GetByIdAsync(companyId, loanId)));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<LoanResponse>>>> GetAll(Guid companyId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<LoanResponse>>(true, await _service.GetAllAsync(companyId, status, new PagedRequest(page, pageSize))));

    [HttpPut("{loanId:guid}")]
    public async Task<ActionResult<ApiResponse<LoanResponse>>> Update(Guid companyId, Guid loanId, [FromBody] UpdateLoanRequest request)
    {
        var result = await _service.UpdateAsync(companyId, loanId, request);
        return Ok(new ApiResponse<LoanResponse>(true, result, result.Notice ?? "บันทึกสินเชื่อแล้ว"));
    }

    [HttpPost("{loanId:guid}/generate-schedule")]
    public async Task<ActionResult<ApiResponse<List<LoanScheduleResponse>>>> GenerateSchedule(Guid companyId, Guid loanId)
        => Ok(new ApiResponse<List<LoanScheduleResponse>>(true, await _service.GenerateScheduleAsync(companyId, loanId)));

    [HttpGet("{loanId:guid}/schedule")]
    public async Task<ActionResult<ApiResponse<List<LoanScheduleResponse>>>> GetSchedule(Guid companyId, Guid loanId)
        => Ok(new ApiResponse<List<LoanScheduleResponse>>(true, await _service.GetScheduleAsync(companyId, loanId)));

    [HttpPost("{loanId:guid}/payments")]
    public async Task<ActionResult<ApiResponse<LoanPaymentResponse>>> MakePayment(Guid companyId, Guid loanId, [FromBody] MakeLoanPaymentRequest request)
        => Ok(new ApiResponse<LoanPaymentResponse>(true, await _service.MakePaymentAsync(companyId, loanId, request, User.Identity?.Name ?? "")));

    [HttpGet("{loanId:guid}/payments")]
    public async Task<ActionResult<ApiResponse<List<LoanPaymentResponse>>>> GetPayments(Guid companyId, Guid loanId)
        => Ok(new ApiResponse<List<LoanPaymentResponse>>(true, await _service.GetPaymentsAsync(companyId, loanId)));

    /// <summary>ผังที่เสนอเป็นค่าตั้งต้นของฟอร์ม (ผังเงินกู้ตามชนิด/ระยะ · ผังดอกเบี้ยจ่าย) — เซิร์ฟเวอร์ตัดสิน หน้าแค่เติมให้</summary>
    [HttpGet("account-defaults")]
    public async Task<ActionResult<ApiResponse<LoanAccountDefaultsResponse>>> GetAccountDefaults(Guid companyId,
        [FromQuery] string? loanType, [FromQuery] int termMonths = 0)
        => Ok(new ApiResponse<LoanAccountDefaultsResponse>(true, await _service.GetAccountDefaultsAsync(companyId, loanType, termMonths)));

    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<LoanSummaryResponse>>> GetSummary(Guid companyId)
        => Ok(new ApiResponse<LoanSummaryResponse>(true, await _service.GetSummaryAsync(companyId)));
}

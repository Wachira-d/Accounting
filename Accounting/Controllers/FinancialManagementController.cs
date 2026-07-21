using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.FinancialManagement;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/financial")]
[Authorize]
[RequirePermission(PermissionKeys.ReportsDashboard)]
public class FinancialManagementController : ControllerBase
{
    private readonly IFinancialManagementService _svc;
    public FinancialManagementController(IFinancialManagementService svc) => _svc = svc;

    private string UserId => JwtHelper.GetUserIdFromClaims(User).ToString();

    // ===== 1. Prepaid Expenses =====
    [HttpPost("prepaid")]
    public async Task<ActionResult<ApiResponse<PrepaidExpenseResponse>>> CreatePrepaid(Guid companyId, [FromBody] CreatePrepaidExpenseRequest req)
    {
        var r = await _svc.CreatePrepaidExpenseAsync(companyId, req, UserId);
        return Ok(new ApiResponse<PrepaidExpenseResponse>(true, r, "สร้างรายการจ่ายล่วงหน้าสำเร็จ"));
    }

    [HttpGet("prepaid")]
    public async Task<ActionResult<ApiResponse<List<PrepaidExpenseResponse>>>> GetPrepaids(Guid companyId)
        => Ok(new ApiResponse<List<PrepaidExpenseResponse>>(true, await _svc.GetPrepaidExpensesAsync(companyId)));

    [HttpGet("prepaid/{id:guid}")]
    public async Task<ActionResult<ApiResponse<PrepaidExpenseResponse>>> GetPrepaidDetail(Guid companyId, Guid id)
        => Ok(new ApiResponse<PrepaidExpenseResponse>(true, await _svc.GetPrepaidExpenseDetailAsync(companyId, id)));

    [HttpPost("prepaid/process")]
    public async Task<ActionResult<ApiResponse<object>>> ProcessAmortization(Guid companyId, [FromQuery] DateTime asOfDate)
    {
        var count = await _svc.ProcessPrepaidAmortizationAsync(companyId, asOfDate, UserId);
        return Ok(new ApiResponse<object>(true, new { processedCount = count }, $"ตัดจ่ายสำเร็จ {count} รายการ"));
    }

    // ===== 2. Deposit Management =====
    [HttpPost("deposits")]
    public async Task<ActionResult<ApiResponse<DepositResponse>>> CreateDeposit(Guid companyId, [FromBody] CreateDepositRequest req)
        => Ok(new ApiResponse<DepositResponse>(true, await _svc.CreateDepositAsync(companyId, req, UserId), "บันทึกเงินมัดจำสำเร็จ"));

    [HttpGet("deposits")]
    public async Task<ActionResult<ApiResponse<List<DepositResponse>>>> GetDeposits(Guid companyId, [FromQuery] string? direction)
        => Ok(new ApiResponse<List<DepositResponse>>(true, await _svc.GetDepositsAsync(companyId, direction)));

    [HttpPost("deposits/{id:guid}/refund")]
    public async Task<ActionResult<ApiResponse<DepositResponse>>> RefundDeposit(Guid companyId, Guid id, [FromBody] DepositRefundRequest req)
        => Ok(new ApiResponse<DepositResponse>(true, await _svc.RefundDepositAsync(companyId, id, req, UserId), "คืนเงินมัดจำสำเร็จ"));

    // ===== 3. Bad Debt Allowance =====
    [HttpPost("bad-debt")]
    public async Task<ActionResult<ApiResponse<BadDebtAllowanceResponse>>> CreateBadDebt(Guid companyId, [FromBody] CreateBadDebtAllowanceRequest req)
        => Ok(new ApiResponse<BadDebtAllowanceResponse>(true, await _svc.CreateBadDebtAllowanceAsync(companyId, req, UserId)));

    [HttpGet("bad-debt")]
    public async Task<ActionResult<ApiResponse<List<BadDebtAllowanceResponse>>>> GetBadDebts(Guid companyId)
        => Ok(new ApiResponse<List<BadDebtAllowanceResponse>>(true, await _svc.GetBadDebtAllowancesAsync(companyId)));

    [HttpPost("bad-debt/{id:guid}/post")]
    public async Task<ActionResult<ApiResponse<BadDebtAllowanceResponse>>> PostBadDebt(Guid companyId, Guid id)
        => Ok(new ApiResponse<BadDebtAllowanceResponse>(true, await _svc.PostBadDebtAllowanceAsync(companyId, id, UserId), "บันทึกบัญชีสำเร็จ"));

    // ===== 4. Inventory Obsolescence =====
    [HttpPost("inventory-obsolescence")]
    public async Task<ActionResult<ApiResponse<InventoryObsolescenceResponse>>> CreateObsolescence(Guid companyId, [FromBody] CreateInventoryObsolescenceRequest req)
        => Ok(new ApiResponse<InventoryObsolescenceResponse>(true, await _svc.CreateInventoryObsolescenceAsync(companyId, req, UserId)));

    [HttpGet("inventory-obsolescence")]
    public async Task<ActionResult<ApiResponse<List<InventoryObsolescenceResponse>>>> GetObsolescences(Guid companyId)
        => Ok(new ApiResponse<List<InventoryObsolescenceResponse>>(true, await _svc.GetInventoryObsolescencesAsync(companyId)));

    [HttpPost("inventory-obsolescence/{id:guid}/post")]
    public async Task<ActionResult<ApiResponse<InventoryObsolescenceResponse>>> PostObsolescence(Guid companyId, Guid id)
        => Ok(new ApiResponse<InventoryObsolescenceResponse>(true, await _svc.PostInventoryObsolescenceAsync(companyId, id, UserId), "บันทึกบัญชีสำเร็จ"));

    // ===== 5. Accrued Expenses =====
    [HttpPost("accrued")]
    public async Task<ActionResult<ApiResponse<AccruedExpenseResponse>>> CreateAccrued(Guid companyId, [FromBody] CreateAccruedExpenseRequest req)
        => Ok(new ApiResponse<AccruedExpenseResponse>(true, await _svc.CreateAccruedExpenseAsync(companyId, req, UserId), "บันทึกค้างจ่ายสำเร็จ"));

    [HttpGet("accrued")]
    public async Task<ActionResult<ApiResponse<List<AccruedExpenseResponse>>>> GetAccrueds(Guid companyId)
        => Ok(new ApiResponse<List<AccruedExpenseResponse>>(true, await _svc.GetAccruedExpensesAsync(companyId)));

    [HttpPost("accrued/{id:guid}/pay")]
    public async Task<ActionResult<ApiResponse<AccruedExpenseResponse>>> PayAccrued(Guid companyId, Guid id, [FromBody] PayAccruedExpenseRequest req)
        => Ok(new ApiResponse<AccruedExpenseResponse>(true, await _svc.PayAccruedExpenseAsync(companyId, id, req, UserId), "จ่ายค้างจ่ายสำเร็จ"));

    // ===== 6. Corporate Income Tax =====
    [HttpPost("cit")]
    public async Task<ActionResult<ApiResponse<CITResponse>>> CalculateCIT(Guid companyId, [FromBody] CalculateCITRequest req)
        => Ok(new ApiResponse<CITResponse>(true, await _svc.CalculateCITAsync(companyId, req, UserId)));

    [HttpGet("cit")]
    public async Task<ActionResult<ApiResponse<List<CITResponse>>>> GetCITs(Guid companyId)
        => Ok(new ApiResponse<List<CITResponse>>(true, await _svc.GetCITListAsync(companyId)));

    [HttpPost("cit/{id:guid}/post")]
    public async Task<ActionResult<ApiResponse<CITResponse>>> PostCIT(Guid companyId, Guid id)
        => Ok(new ApiResponse<CITResponse>(true, await _svc.PostCITAsync(companyId, id, UserId), "บันทึกภาษีสำเร็จ"));

    // ===== 7. Profit Appropriation =====
    [HttpPost("profit-appropriation")]
    public async Task<ActionResult<ApiResponse<ProfitAppropriationResponse>>> CreateAppropriation(Guid companyId, [FromBody] CreateProfitAppropriationRequest req)
        => Ok(new ApiResponse<ProfitAppropriationResponse>(true, await _svc.CreateProfitAppropriationAsync(companyId, req, UserId)));

    [HttpGet("profit-appropriation")]
    public async Task<ActionResult<ApiResponse<List<ProfitAppropriationResponse>>>> GetAppropriations(Guid companyId)
        => Ok(new ApiResponse<List<ProfitAppropriationResponse>>(true, await _svc.GetProfitAppropriationsAsync(companyId)));

    [HttpPost("profit-appropriation/{id:guid}/approve")]
    public async Task<ActionResult<ApiResponse<ProfitAppropriationResponse>>> ApproveAppropriation(Guid companyId, Guid id)
        => Ok(new ApiResponse<ProfitAppropriationResponse>(true, await _svc.ApproveProfitAppropriationAsync(companyId, id, UserId), "อนุมัติสำเร็จ"));

    // ===== 8. Capital Transactions =====
    [HttpPost("capital")]
    public async Task<ActionResult<ApiResponse<CapitalTransactionResponse>>> CreateCapital(Guid companyId, [FromBody] CreateCapitalTransactionRequest req)
        => Ok(new ApiResponse<CapitalTransactionResponse>(true, await _svc.CreateCapitalTransactionAsync(companyId, req, UserId)));

    [HttpGet("capital")]
    public async Task<ActionResult<ApiResponse<List<CapitalTransactionResponse>>>> GetCapitals(Guid companyId)
        => Ok(new ApiResponse<List<CapitalTransactionResponse>>(true, await _svc.GetCapitalTransactionsAsync(companyId)));

    [HttpPost("capital/{id:guid}/complete")]
    public async Task<ActionResult<ApiResponse<CapitalTransactionResponse>>> CompleteCapital(Guid companyId, Guid id)
        => Ok(new ApiResponse<CapitalTransactionResponse>(true, await _svc.CompleteCapitalTransactionAsync(companyId, id, UserId), "ดำเนินการสำเร็จ"));

    // ===== 9. Short-term Investment =====
    [HttpPost("investments")]
    public async Task<ActionResult<ApiResponse<InvestmentResponse>>> CreateInvestment(Guid companyId, [FromBody] CreateInvestmentRequest req)
        => Ok(new ApiResponse<InvestmentResponse>(true, await _svc.CreateInvestmentAsync(companyId, req, UserId), "บันทึกเงินลงทุนสำเร็จ"));

    [HttpGet("investments")]
    public async Task<ActionResult<ApiResponse<List<InvestmentResponse>>>> GetInvestments(Guid companyId)
        => Ok(new ApiResponse<List<InvestmentResponse>>(true, await _svc.GetInvestmentsAsync(companyId)));

    [HttpPost("investments/{id:guid}/sell")]
    public async Task<ActionResult<ApiResponse<InvestmentResponse>>> SellInvestment(Guid companyId, Guid id, [FromBody] SellInvestmentRequest req)
        => Ok(new ApiResponse<InvestmentResponse>(true, await _svc.SellInvestmentAsync(companyId, id, req, UserId), "ขายเงินลงทุนสำเร็จ"));
}

using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Compliance;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/compliance")]
[Authorize]
public class ComplianceController : ControllerBase
{
    private readonly IComplianceService _service;
    public ComplianceController(IComplianceService service) => _service = service;

    [HttpPost("filings")]
    public async Task<ActionResult<ApiResponse<ComplianceFilingResponse>>> Create(Guid companyId, [FromBody] CreateComplianceFilingRequest request)
        => StatusCode(201, new ApiResponse<ComplianceFilingResponse>(true, await _service.CreateFilingAsync(companyId, request)));

    [HttpGet("filings/{filingId:guid}")]
    public async Task<ActionResult<ApiResponse<ComplianceFilingResponse>>> GetById(Guid companyId, Guid filingId)
        => Ok(new ApiResponse<ComplianceFilingResponse>(true, await _service.GetFilingAsync(companyId, filingId)));

    [HttpGet("filings")]
    public async Task<ActionResult<ApiResponse<PagedResponse<ComplianceFilingResponse>>>> GetAll(Guid companyId, [FromQuery] int? year, [FromQuery] string? filingType, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<ComplianceFilingResponse>>(true, await _service.GetFilingsAsync(companyId, year, filingType, new PagedRequest(page, pageSize))));

    [HttpPut("filings/{filingId:guid}")]
    public async Task<ActionResult<ApiResponse<ComplianceFilingResponse>>> Update(Guid companyId, Guid filingId, [FromBody] UpdateComplianceFilingRequest request)
        => Ok(new ApiResponse<ComplianceFilingResponse>(true, await _service.UpdateFilingAsync(companyId, filingId, request)));

    [HttpDelete("filings/{filingId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid companyId, Guid filingId)
    {
        await _service.DeleteFilingAsync(companyId, filingId);
        return Ok(new ApiResponse<bool>(true, true, "ลบสำเร็จ"));
    }

    [HttpPost("filings/{filingId:guid}/validate")]
    public async Task<ActionResult<ApiResponse<ComplianceFilingResponse>>> Validate(Guid companyId, Guid filingId)
        => Ok(new ApiResponse<ComplianceFilingResponse>(true, await _service.ValidateFilingAsync(companyId, filingId)));

    /// <summary>บันทึกว่า "ยื่นแบบนี้แล้ว" — ระบบ**ไม่ได้**ส่งอะไรไปหน่วยงาน
    ///
    /// <para>D2-B1b: เดิมเมธอดปลายทางคอมเมนต์ตัวเองว่า "Simulate submission"
    /// แล้วแต่งเลขอ้างอิง/เลขยืนยันจาก GUID · ตอนนี้เลขยืนยันมาจากผู้ใช้กรอก
    /// (<c>ConfirmationNumber</c> ในบอดี้) หรือไม่มีเลย และข้อความตอบกลับบอก
    /// ตามสิ่งที่เกิดจริง</para></summary>
    [HttpPost("filings/{filingId:guid}/submit")]
    public async Task<ActionResult<ApiResponse<ComplianceFilingResponse>>> Submit(
        Guid companyId, Guid filingId,
        [FromServices] Data.AccountingDbContext db,
        [FromBody] FileComplianceRequest? request = null,
        CancellationToken ct = default)
    {
        var result = await _service.SubmitFilingAsync(companyId, filingId, User.Identity?.Name ?? "");

        var confirmation = request?.ConfirmationNumber?.Trim();
        if (!string.IsNullOrWhiteSpace(confirmation) || !string.IsNullOrWhiteSpace(request?.Notes))
        {
            var filing = await db.Set<Models.Entities.ComplianceFiling>()
                .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Id == filingId && !f.IsDeleted, ct);
            if (filing != null)
            {
                if (!string.IsNullOrWhiteSpace(confirmation))
                {
                    filing.ConfirmationNumber = confirmation;
                    filing.SubmissionReference = confirmation;
                }
                if (!string.IsNullOrWhiteSpace(request?.Notes))
                    filing.Notes = string.IsNullOrWhiteSpace(filing.Notes)
                        ? request!.Notes : filing.Notes + "\n" + request!.Notes;
                await db.SaveChangesAsync(ct);
                result = result with
                {
                    ConfirmationNumber = filing.ConfirmationNumber,
                    SubmissionReference = filing.SubmissionReference,
                };
            }
        }

        var message = string.IsNullOrWhiteSpace(result.ConfirmationNumber)
            ? "บันทึกว่ายื่นแบบแล้ว (ไม่มีเลขยืนยันจากหน่วยงาน) — ระบบไม่ได้ส่งแบบไปหน่วยงานให้"
            : $"บันทึกการยื่นพร้อมเลขยืนยัน {result.ConfirmationNumber} แล้ว";
        return Ok(new ApiResponse<ComplianceFilingResponse>(true, result, message));
    }

    [HttpGet("pending")]
    public async Task<ActionResult<ApiResponse<List<ComplianceFilingResponse>>>> GetPending(Guid companyId)
        => Ok(new ApiResponse<List<ComplianceFilingResponse>>(true, await _service.GetPendingFilingsAsync(companyId)));

    [HttpPost("initialize/{year:int}")]
    public async Task<ActionResult<ApiResponse<bool>>> Initialize(Guid companyId, int year)
    { await _service.InitializeFilingCalendarAsync(companyId, year); return Ok(new ApiResponse<bool>(true, true)); }
}

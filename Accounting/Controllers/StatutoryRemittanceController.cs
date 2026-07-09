using System.Security.Claims;
using Accounting.Data;
using Accounting.Filters;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>หน้านำส่งภาษี/ประกันสังคมรวม — สปส.1-10 + ภงด.1/3/53 + ภพ.30.
/// แสดงยอดรอนำส่งทุกประเภท + ทำจ่าย (post JE) + แนบใบเสร็จ.</summary>
[ApiController]
[Route("api/companies/{companyId:guid}/remittances")]
[Authorize]
public class StatutoryRemittanceController : ControllerBase
{
    private readonly IStatutoryRemittanceService _service;
    private readonly IFileAttachmentService _files;
    private readonly AccountingDbContext _db;

    public StatutoryRemittanceController(IStatutoryRemittanceService service,
        IFileAttachmentService files, AccountingDbContext db)
    {
        _service = service; _files = files; _db = db;
    }

    /// <summary>ยอดรอนำส่งทุกประเภท + ประวัติ.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<RemittanceDashboardResponse>>> Get(
        Guid companyId, [FromQuery] int monthsBack = 12)
        => Ok(new ApiResponse<RemittanceDashboardResponse>(true,
            await _service.GetDashboardAsync(companyId, monthsBack)));

    /// <summary>preview ยอด + เงินเพิ่มของงวดหนึ่ง (ตามวันที่จ่ายที่เลือก).</summary>
    [HttpGet("preview")]
    public async Task<ActionResult<ApiResponse<PendingRemittanceItem>>> Preview(
        Guid companyId, [FromQuery] string type, [FromQuery] int year,
        [FromQuery] int month, [FromQuery] DateTime? payDate = null)
    {
        var item = await _service.PreviewAsync(companyId, type, year, month, payDate ?? DateTime.UtcNow);
        return item == null
            ? NotFound(new ApiResponse<PendingRemittanceItem>(false, null, "ไม่มียอดค้างของงวดนี้"))
            : Ok(new ApiResponse<PendingRemittanceItem>(true, item));
    }

    /// <summary>นำส่ง 1 งวด — post JE (Dr หนี้ค้างจ่าย / Cr ธนาคาร) + บันทึก.
    /// จำกัดเฉพาะผู้มีสิทธิ์ยื่นภาษี/นำส่ง (Owner/Accountant auto-pass) — เป็นการ
    /// จ่ายเงิน+ลง GL จริง ไม่ควรให้ Staff/Viewer ทำ.</summary>
    [HttpPost]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<RemitResult>>> Remit(
        Guid companyId, [FromBody] RemitRequest request)
    {
        try
        {
            var res = await _service.RemitAsync(companyId, request, User.Identity?.Name ?? "");
            return Ok(new ApiResponse<RemitResult>(true, res, res.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<RemitResult>(false, null, ex.Message));
        }
    }

    /// <summary>แนบไฟล์ใบเสร็จ/หลักฐานการนำส่งเข้ารายการที่นำส่งแล้ว.</summary>
    [HttpPost("{remittanceId:guid}/receipt")]
    public async Task<ActionResult<ApiResponse<object>>> UploadReceipt(
        Guid companyId, Guid remittanceId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ไม่พบไฟล์"));
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var uid = await ResolveUserIdAsync(companyId);
        var att = await _files.UploadBytesAsync(companyId, "StatutoryRemittance", remittanceId,
            file.FileName, file.ContentType ?? "application/octet-stream", ms.ToArray(), uid);
        await _service.AttachReceiptAsync(companyId, remittanceId, att.Id);
        return Ok(new ApiResponse<object>(true, new { attachmentId = att.Id }, "แนบใบเสร็จแล้ว"));
    }

    // resolve user GUID จริงสำหรับ FK (claim NameIdentifier → ไม่ใช่ user ก็ fallback เจ้าของบริษัท)
    private async Task<Guid> ResolveUserIdAsync(Guid companyId)
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(sub, out var uid)
            && await _db.Users.AsNoTracking().AnyAsync(u => u.Id == uid))
            return uid;
        var owner = await _db.Set<Models.Entities.CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.Role == Models.Enums.UserRole.Owner)
            .Select(cu => (Guid?)cu.UserId).FirstOrDefaultAsync();
        return owner ?? throw new InvalidOperationException("ไม่พบผู้ใช้สำหรับแนบไฟล์");
    }
}

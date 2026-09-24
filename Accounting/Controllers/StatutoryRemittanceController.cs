using System.Security.Claims;
using Accounting.Data;
using Accounting.Filters;
using Accounting.Helpers;
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

    /// <summary>ปฏิทินนำส่ง "แบบ × เดือน" สำหรับ dashboard — ตอบว่าเดือนไหนยื่นแล้ว/ยัง
    /// รวมงวดที่ยอด 0 (ต้องยื่นแบบเปล่า) และงวดที่ระบบยังไม่ทราบยอด.</summary>
    [HttpGet("calendar")]
    public async Task<ActionResult<ApiResponse<FilingCalendarResponse>>> Calendar(
        Guid companyId, [FromQuery] int months = 12)
        => Ok(new ApiResponse<FilingCalendarResponse>(true,
            await _service.GetFilingCalendarAsync(companyId, months)));

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

    public sealed record RecognizePp36Request(int PeriodYear, int PeriodMonth, DateTime? RecognizeDate,
        // เลขที่ใบเสร็จกรมสรรพากร (ถ้าไม่ได้กรอกตอนนำส่ง หรือต้องการแก้) —
        // จะถูก stamp ลงเอกสารเป็นเลขใบกำกับ §86/14 และ backfill ลง remittance
        string? RdReceiptNumber = null);

    /// <summary>รับรู้ภาษีซื้อ ภ.พ.36 หลังได้ใบเสร็จกรมสรรพากร (§77/2) —
    /// Dr 11610 / Cr 11640 + stamp เอกสาร → เข้า ภ.พ.30 เดือนที่รับรู้.
    /// ต้องนำส่ง ภ.พ.36 งวดนั้นก่อน.</summary>
    /// <summary>รายละเอียดใบที่รับรู้แล้วของงวด ภ.พ.36 — เดือนเคลมต่อใบ +
    /// สถานะใน ภ.พ.30 (ตอบ "เข้า ภ.พ.30 แล้วแต่เปิดรายงานไม่เจอ")</summary>
    [HttpGet("pp36/recognized")]
    public async Task<ActionResult<ApiResponse<List<Pp36RecognizedDocItem>>>> GetPp36Recognized(
        Guid companyId, [FromQuery] int periodYear, [FromQuery] int periodMonth)
    {
        var res = await _service.GetPp36RecognizedDocsAsync(companyId, periodYear, periodMonth);
        return Ok(new ApiResponse<List<Pp36RecognizedDocItem>>(true, res));
    }

    [HttpPost("pp36/recognize")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<RemitResult>>> RecognizePp36(
        Guid companyId, [FromBody] RecognizePp36Request request)
    {
        try
        {
            // RecognizeDate null = ใช้ "วันที่ใบกำกับผู้ขาย" ต่อใบเป็นวันเคลม ภ.พ.30
            // (default ที่ผู้ใช้เลือก); มีค่า = override ทั้งชุด
            var res = await _service.RecognizePp36InputVatAsync(companyId,
                request.PeriodYear, request.PeriodMonth,
                request.RecognizeDate, User.Identity?.Name ?? "",
                request.RdReceiptNumber);
            return Ok(new ApiResponse<RemitResult>(true, res, res.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<RemitResult>(false, null, ex.Message));
        }
    }

    /// <summary>แนบไฟล์ใบเสร็จ/หลักฐานการนำส่งเข้ารายการที่นำส่งแล้ว.
    ///
    /// <para>═══ ที่มา (ฝ่ายค้านรอบ 193 · C2) ═══ เส้นนี้เป็นทางเข้าที่สองของไฟล์ชนิด <c>StatutoryRemittance</c> (ทางแรกคือ
    /// <c>FileAttachmentController</c> ซึ่งมีด่านแล้ว) แต่มีแค่ <c>[Authorize]</c> · เขียนไฟล์<b>ก่อน</b>ตรวจว่ารายการนำส่งเป็นของ
    /// บริษัทนี้ (ไฟล์กำพร้าค้างในตารางเมื่อ id ผิด) · และเชื่อ Content-Type/นามสกุลจาก client (HTML ที่ตั้งชื่อ .pdf ผ่านได้)
    /// ⇒ ตอนนี้: (1) รายการต้องมีอยู่ในบริษัทนี้ (2) ด่านเดียวกับไฟล์แนบ (<see cref="IAttachmentAccessGate"/> ⇒ Tax.File ตาม
    /// <see cref="Accounting.Helpers.AttachmentPermissionScope"/>) (3) ชนิดไฟล์ตัดสินจากไบต์จริง
    /// (<see cref="Accounting.Helpers.UploadFileType.SniffAttachment"/> ตัวเดียวกับเส้นไฟล์แนบ) — นามสกุลที่เก็บและ Content-Type
    /// มาจากผลตรวจไบต์เท่านั้น</para></summary>
    [HttpPost("{remittanceId:guid}/receipt")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<object>>> UploadReceipt(
        Guid companyId, Guid remittanceId, IFormFile file, [FromServices] IAttachmentAccessGate gate)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ไม่พบไฟล์"));

        // ด่านของโมดูล + รายการต้องมีอยู่จริงในบริษัทนี้ (ตรวจใน gate — 404 ถ้าไม่พบ) — ก่อนแตะไฟล์ใด ๆ
        var deny = await gate.DenyAttachmentAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
            "StatutoryRemittance", remittanceId, AttachmentAccess.Write, "แนบใบเสร็จนำส่ง");
        if (deny is { } d) return StatusCode(d.Status, new ApiResponse<object>(false, null, d.Message));

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        // ไบต์ตัดสินชนิด — UnsupportedUploadException = 400 ข้อความไทย (ExceptionMiddleware)
        var kind = UploadFileType.SniffAttachment(
                bytes.AsSpan(0, Math.Min(bytes.Length, UploadFileType.HeaderBytes)), file.FileName)
            ?? throw new UnsupportedUploadException(UploadFileType.AttachmentRejectMessage);
        var baseName = Path.GetFileNameWithoutExtension(file.FileName);
        var displayName = (string.IsNullOrWhiteSpace(baseName) ? "receipt" : baseName) + kind.Extension;

        var uid = await ResolveUserIdAsync(companyId);
        var att = await _files.UploadBytesAsync(companyId, "StatutoryRemittance", remittanceId,
            displayName, kind.ContentType, bytes, uid);
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

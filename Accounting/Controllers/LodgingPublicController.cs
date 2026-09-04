using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>หน้าเว็บสาธารณะของที่พัก (storefront) — ไม่ต้องล็อกอิน scope ด้วย siteId
/// (ที่พักผูกกับ Site 1:1; ทุกเมธอดของบริการกรอง CompanyId + SiteId) ·
/// การจองที่สร้างแล้วเข้าถึงได้ด้วย PublicToken เท่านั้น (ไม่มี id เดาได้)</summary>
[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/lodging")]
[AllowAnonymous]
public class LodgingPublicController : ControllerBase
{
    private readonly ILodgingService _svc;
    private readonly IEntitlementService _entitlement;
    public LodgingPublicController(ILodgingService svc, IEntitlementService entitlement)
    {
        _svc = svc;
        _entitlement = entitlement;
    }

    /// <summary>402 พร้อมทางไปต่อ — ห้ามคืน 403 เปล่า ๆ ให้ผู้ใช้เดาเองว่าต้องทำอะไร
    /// (ที่พักเป็นคนต้องเปิด add-on ไม่ใช่แขก จึงเขียนข้อความให้แขกเข้าใจว่าไม่ใช่ความผิดเขา)</summary>
    private ActionResult<ApiResponse<T>> AddOnRequired<T>(EntitlementResult ent)
        => StatusCode(402, new ApiResponse<T>(false, default,
            "ที่พักยังไม่ได้เปิดใช้บริการนี้ — กรุณาติดต่อที่พักโดยตรง",
            new List<string> { ent.Reason ?? "", ent.UpgradeHint ?? "" }));

    [HttpGet("info")]
    public async Task<ActionResult<ApiResponse<LodgingPublicInfo>>> Info(Guid companyId, Guid siteId)
    {
        var info = await _svc.GetPublicInfoAsync(companyId, siteId);
        if (info == null) return NotFound(new ApiResponse<LodgingPublicInfo>(false, null, "เว็บไซต์นี้ไม่มีที่พักเปิดให้จอง"));
        return Ok(new ApiResponse<LodgingPublicInfo>(true, info));
    }

    [HttpPost("search")]
    public async Task<ActionResult<ApiResponse<List<LodgingSearchResult>>>> Search(Guid companyId, Guid siteId, [FromBody] LodgingSearchRequest req)
    {
        var pid = await _svc.ResolvePropertyIdForSiteAsync(companyId, siteId);
        if (pid == null) return NotFound(new ApiResponse<List<LodgingSearchResult>>(false, null, "ไม่พบที่พัก"));
        return Ok(new ApiResponse<List<LodgingSearchResult>>(true, await _svc.SearchAsync(companyId, pid.Value, req)));
    }

    [HttpPost("quote")]
    public async Task<ActionResult<ApiResponse<LodgingQuoteResponse>>> Quote(Guid companyId, Guid siteId, [FromBody] LodgingQuoteRequest req)
    {
        var pid = await _svc.ResolvePropertyIdForSiteAsync(companyId, siteId);
        if (pid == null) return NotFound(new ApiResponse<LodgingQuoteResponse>(false, null, "ไม่พบที่พัก"));
        return Ok(new ApiResponse<LodgingQuoteResponse>(true, await _svc.QuoteAsync(companyId, pid.Value, req)));
    }

    [HttpPost("reservations")]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> Create(Guid companyId, Guid siteId, [FromBody] LodgingCreateReservationRequest req)
    {
        var pid = await _svc.ResolvePropertyIdForSiteAsync(companyId, siteId);
        if (pid == null) return NotFound(new ApiResponse<LodgingReservationResponse>(false, null, "ไม่พบที่พัก"));
        // ช่องฝั่งพนักงานถูกตัดทิ้งเสมอ — แขกกำหนด source/contact/ยืนยันทันทีเองไม่ได้
        var clean = req with { Source = null, SourceReference = null, ContactId = null, InternalNotes = null, ConfirmImmediately = false };
        var r = await _svc.CreateReservationAsync(companyId, pid.Value, clean, LodgingReservationSource.Web, "storefront-guest", siteId);
        var msg = r.Status == LodgingReservationStatus.Confirmed
            ? $"จองสำเร็จ เลขที่ {r.ReservationNumber}"
            : $"รับคำขอจอง {r.ReservationNumber} แล้ว — กรุณาชำระมัดจำ {r.DepositRequired:N2} บาท และอัปโหลดสลิป";
        return StatusCode(201, new ApiResponse<LodgingReservationResponse>(true, r, msg));
    }

    [HttpGet("reservations/{token}")]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> Get(Guid companyId, Guid siteId, string token)
    {
        var r = await _svc.GetReservationByTokenAsync(companyId, siteId, token);
        if (r == null) return NotFound(new ApiResponse<LodgingReservationResponse>(false, null, "ไม่พบการจอง"));
        return Ok(new ApiResponse<LodgingReservationResponse>(true, r));
    }

    /// <summary>
    /// **หลักฐานการจองให้แขกโหลดเก็บ** (LDG-P1-04) — PDF พิมพ์ได้ ใช้แสดงตอนเช็คอิน
    ///
    /// <para>เดิมหน้าการจองมีแค่ปุ่ม "กลับหน้าหลัก" กับ "ยกเลิก" — ไม่มีอะไรให้เก็บเลย</para>
    ///
    /// <para><b>ไม่ใช่เอกสารภาษี</b> — ใบเสร็จมัดจำ/ใบกำกับตอนเช็คเอาต์เป็นคนละใบ
    /// และออกผ่าน <c>IDocumentService</c> ตามเลข gap-free §86/4 · ตัวประกอบ HTML
    /// (<c>LodgingVoucherBuilder</c>) เขียนคำเตือนนี้ไว้ท้ายเอกสารและมีเทสต์ล็อกว่า
    /// คำว่า "ใบกำกับภาษี"/"ใบเสร็จรับเงิน" ต้องไม่โผล่บนหัว</para>
    /// </summary>
    [HttpGet("reservations/{token}/voucher.pdf")]
    public async Task<IActionResult> Voucher(Guid companyId, Guid siteId, string token,
        [FromServices] IPdfGenerationService pdf, CancellationToken ct)
    {
        var r = await _svc.GetReservationByTokenAsync(companyId, siteId, token);
        if (r == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบการจอง"));

        var html = Accounting.Helpers.LodgingVoucherBuilder.BuildHtml(r);
        var bytes = await pdf.RenderHtmlToPdfAsync(html);
        return File(bytes, "application/pdf", Accounting.Helpers.LodgingVoucherBuilder.FileName(r));
    }

    /// <summary>แขกเปิดดูสลิปของ**ตัวเอง** — พิสูจน์สิทธิ์ด้วย token ที่เขาถืออยู่
    ///
    /// <para>ก่อนหน้านี้ไฟล์อยู่ใต้ static path สาธารณะ ⇒ ใครได้ URL ไปก็เปิดได้
    /// ตลอดกาล · ตอนนี้ต้องมี token ของการจองใบนั้นเท่านั้น</para></summary>
    [HttpGet("reservations/{token}/slip")]
    public async Task<IActionResult> Slip(Guid companyId, Guid siteId, string token)
    {
        var f = await _svc.GetSlipFileByTokenAsync(companyId, siteId, token);
        if (f == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบไฟล์สลิป"));
        return PhysicalFile(f.Value.Path, f.Value.ContentType);
    }

    [HttpPost("reservations/{token}/slip")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> UploadSlip(Guid companyId, Guid siteId, string token, IFormFile? file, [FromForm] string? reference)
    {
        if (file == null || file.Length == 0) return BadRequest(new ApiResponse<LodgingReservationResponse>(false, null, "กรุณาเลือกไฟล์สลิป"));
        var r = await _svc.UploadSlipByTokenAsync(companyId, siteId, token, file, reference);
        if (r == null) return NotFound(new ApiResponse<LodgingReservationResponse>(false, null, "ไม่พบการจอง"));
        return Ok(new ApiResponse<LodgingReservationResponse>(true, r, "ส่งสลิปแล้ว — ที่พักจะตรวจสอบและยืนยันการจอง"));
    }

    [HttpPost("reservations/{token}/cancel")]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> Cancel(Guid companyId, Guid siteId, string token, [FromBody] LodgingCancelRequest? req)
    {
        var r = await _svc.CancelByTokenAsync(companyId, siteId, token, req ?? new LodgingCancelRequest());
        if (r == null) return NotFound(new ApiResponse<LodgingReservationResponse>(false, null, "ไม่พบการจอง"));
        var msg = r.RefundAmount > 0 ? $"ยกเลิกแล้ว · ที่พักจะคืนเงิน {r.RefundAmount:N2} บาท" : (r.CancellationFee > 0 ? $"ยกเลิกแล้ว · ค่าปรับตามนโยบาย {r.CancellationFee:N2} บาท" : "ยกเลิกแล้ว");
        return Ok(new ApiResponse<LodgingReservationResponse>(true, r, msg));
    }

    /// <summary>แขกส่งคำขอถึงที่พัก (แม่บ้าน/ซ่อม/รูมเซอร์วิส/คอนเซียร์จ) —
    /// อยู่ใต้ add-on **Guest Portal Pro** (LODGING_LICENSING_PLAN §3.1)
    ///
    /// ส่วนที่ยังฟรีเสมอคือหน้าการจองด้วย token: ดูรายละเอียด · อัปโหลดสลิป · ยกเลิก
    /// (สามอย่างนี้คือ Aha-moment ที่ทำให้ลูกค้าติด และการยกเลิก/คืนเงินเป็นเส้นแดง
    /// ที่ทีมลูกค้าทั้งสามรายบอกตรงกันว่าห้ามล็อก)</summary>
    [HttpPost("reservations/{token}/requests")]
    public async Task<ActionResult<ApiResponse<LodgingGuestRequestDto>>> GuestRequest(Guid companyId, Guid siteId, string token, [FromBody] LodgingGuestRequestCreate req)
    {
        var ent = await _entitlement.CheckAsync(companyId, AddOnCodes.LodgingGuestPortal);
        if (!ent.Allowed) return AddOnRequired<LodgingGuestRequestDto>(ent);

        var r = await _svc.CreateGuestRequestByTokenAsync(companyId, siteId, token, req);
        if (r == null) return NotFound(new ApiResponse<LodgingGuestRequestDto>(false, null, "ไม่พบการจอง"));
        return Ok(new ApiResponse<LodgingGuestRequestDto>(true, r, "ส่งคำขอถึงที่พักแล้ว"));
    }
}

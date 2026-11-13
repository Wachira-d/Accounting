using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Accounting.Controllers;

/// <summary>ลิงก์สาธารณะให้ลูกค้า "ดู + ดาวน์โหลด PDF + ยอมรับ (พร้อมลายเซ็น)" ใบเสนอราคาโดยไม่ต้อง login
/// (capability URL — token 64 hex บน Document.QuotationAcceptToken).
/// อ่านได้เฉพาะ field ที่จำเป็นต่อการตัดสินใจ + PDF ของใบนั้นใบเดียว; เขียนได้อย่างเดียวคือ accept.
/// ฝั่งผู้ขายสร้าง/เพิกถอนลิงก์ผ่าน DocumentController (ต้อง login).
/// <para>รอบ 239: กติกาการยอมรับ/หลักฐาน/ที่เก็บลายเซ็น อยู่ที่ <see cref="QuotationAcceptance"/> ตัวเดียว · ลายเซ็นตรวจด้วย
/// <see cref="SignatureImageValidator"/> (ตัวเดียวกับเส้นเซ็นรับสินค้า) · ทุกเส้นตรวจ token ด้วย <c>FindByTokenAsync</c> เดียว</para>
/// <para>รอบ 267 P2: แกนของการยอมรับ/เซ็นรับ + ด่าน token ย้ายไป <see cref="Accounting.Services.Implementations.CustomerDocumentResponseService"/>
/// (ผู้เขียนเดียว — หน้าเอกสารใช้ตัวเดียวกัน) · controller นี้เหลือแค่อ่านคำขอ → เรียก service → แปลงผลเป็น HTTP · เส้นนี้คงไว้ให้ลิงก์ที่ออกก่อนรอบ 267
/// จนหมดอายุ (ลบในแพ็กเก็บกวาด)</para></summary>
[ApiController]
[Route("api/public/quotation")]
public class PublicQuotationController : ControllerBase
{
    private readonly AccountingDbContext _db;
    /// <summary>รอบ 239: PDF ของใบ (ตัวเรนเดอร์เดียวกับที่ผู้ขายดาวน์โหลด/ส่งอีเมล) · null = ไม่มีเส้น PDF (ตอบ 404)</summary>
    private readonly IPdfGenerationService? _pdf;
    /// <summary>รอบ 239: ที่เก็บไฟล์กลาง (Local/S3) ของลายเซ็น · null (นอก DI) = ดิสก์ค่าเริ่มต้น</summary>
    private readonly Accounting.Services.Storage.IFileStorage? _storage;
    /// <summary>รอบ 239: เพดานต่อ IP (ตัวนับใน PostgreSQL ข้ามเครื่อง — ตัวเดียวกับ chatbot/จองที่พัก) · null = ไม่จำกัด (เทสต์)</summary>
    private readonly IChatRateLimiter? _rate;

    private readonly Accounting.Services.Implementations.CustomerDocumentResponseService _responses;

    public PublicQuotationController(AccountingDbContext db, ILogger<PublicQuotationController> logger,
        Accounting.Services.Implementations.CmsLeadService? leads = null, IPdfGenerationService? pdf = null,
        Accounting.Services.Storage.IFileStorage? storage = null, IChatRateLimiter? rate = null,
        Accounting.Services.Implementations.CustomerDocumentResponseService? responses = null)
    {
        _db = db;
        _pdf = pdf;
        _storage = storage;
        _rate = rate;
        // สร้างนอก DI (เทสต์) ⇒ ประกอบ service จากของที่ได้รับ — ผู้เขียนเดียวตัวเดิม ไม่มีทางเขียนที่สอง
        _responses = responses ?? new Accounting.Services.Implementations.CustomerDocumentResponseService(db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Accounting.Services.Implementations.CustomerDocumentResponseService>.Instance,
            leads, pdf, storage, rate);
    }

    private Accounting.Services.Storage.IFileStorage SignatureStore => _storage ?? Accounting.Services.Storage.FileStorageDefaults.Local;

    private Accounting.Services.Implementations.CustomerDocumentResponseService.Caller Caller()
        => new(HttpContext?.Connection.RemoteIpAddress?.ToString(), HttpContext?.Request.Headers.UserAgent.ToString());

    /// <summary>แปลงผลของ service เป็น HTTP ตรงตัว (ไม่ตัดสินเพิ่ม)</summary>
    private IActionResult Http(Accounting.Services.Implementations.CustomerDocumentResponseService.Outcome r)
    {
        if (r.Status == 429) Response.Headers.Append("Retry-After", "60");
        var fail = new ApiResponse<string>(false, null, r.Message);
        return r.Status switch
        {
            200 => Ok(new ApiResponse<object>(true, r.Data, r.Message)),
            400 => BadRequest(fail),
            404 => NotFound(fail),
            _ => StatusCode(r.Status, fail),
        };
    }

    /// <summary>เพดานต่อ IP ของเส้นสาธารณะ (PDF เรนเดอร์หนัก · รูปลายเซ็น) — ตัวนับร่วมข้าม instance · true = เกินเพดาน</summary>
    private async Task<bool> OverRateAsync(string bucket, int perMinute, int perDay)
    {
        if (_rate == null) return false;
        var ip = HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return !await _rate.TryConsumeAsync($"qt-public:{bucket}:{ip}", perMinute, perDay, HttpContext?.RequestAborted ?? default);
    }

    private ObjectResult TooMany()
    {
        Response.Headers.Append("Retry-After", "60");
        return StatusCode(429, new ApiResponse<string>(false, null, "เรียกถี่เกินไป — กรุณารอสักครู่แล้วลองใหม่"));
    }

    private Task<Models.Entities.Document?> FindByTokenAsync(string token) => _responses.FindByTokenAsync(token);
    private Task<Models.Entities.Document?> FindDeliveryByTokenAsync(string token) => _responses.FindDeliveryByTokenAsync(token);

    [HttpGet("{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> View(string token)
    {
        var doc = await FindByTokenAsync(token);
        if (doc == null) return NotFound(new ApiResponse<string>(false, null, "ลิงก์ไม่ถูกต้องหรือหมดอายุ"));

        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == doc.CompanyId)
            .Select(c => new { c.Name })
            .FirstOrDefaultAsync();
        var requireSignature = await _responses.RequireSignatureAsync(doc.CompanyId);
        await _responses.MarkOpenedAsync(doc);

        return Ok(new ApiResponse<object>(true, new
        {
            companyName = company?.Name,
            documentNumber = doc.DocumentNumber,
            revisionNumber = doc.RevisionNumber,
            documentDate = doc.DocumentDate,
            contactName = doc.Contact?.Name,
            currency = doc.Currency,
            subTotal = doc.SubTotal,
            vatAmount = doc.VatAmount,
            totalAmount = doc.TotalAmount,
            notes = doc.Notes,
            expiresAt = Accounting.Services.Implementations.CustomerDocumentResponseService.RespondEnd(doc, token),
            acceptedAt = doc.QuotationAcceptedAt,
            acceptedBy = doc.QuotationAcceptedBy,
            // รอบ 239: สถานะ/ค่าที่หน้าลูกค้าแสดง — ข้อความยินยอมมาจากเซิร์ฟเวอร์ (หน้าเว็บไม่มีสำเนา)
            acceptedNote = doc.QuotationAcceptNote,
            hasSignature = !string.IsNullOrWhiteSpace(doc.QuotationAcceptSignaturePath),
            requireSignature,
            // รอบ 267 ฝ่ายค้าน H1: hash เนื้อหาที่หน้านี้แสดง — หน้าส่งกลับพร้อมเลข Rev ตอนกดยอมรับ (ยอมรับได้เฉพาะฉบับที่ลูกค้าเห็น)
            contentHash = DocumentSignedContent.Hash(doc, doc.Lines),
            consentText = QuotationAcceptance.ConsentText,
            pdfAvailable = _pdf != null,
            lines = doc.Lines
                .OrderBy(l => l.LineOrder)
                .Select(l => new { l.Description, l.Quantity, l.Unit, l.UnitPrice, l.Amount })
                .ToList(),
        }));
    }

    /// <summary>รอบ 239: ดาวน์โหลด PDF ของใบเสนอราคาใบนี้ — สิทธิ์มาจาก token อย่างเดียว (ตรวจผ่าน <c>FindByTokenAsync</c> ตัวเดียวกับหน้า/ยอมรับ ·
    /// หมดอายุ/เพิกถอน/ร่าง/ยกเลิก = 404) · ไฟล์เดียวกับที่ผู้ขายดาวน์โหลด (ภาษาเอกสารผ่าน resolver กลางใน <c>GenerateDocumentPdfAsync</c>) ·
    /// ยอมรับแล้วมีช่องลูกค้าอนุมัติ/ลายเซ็น · ไม่มีพารามิเตอร์อื่น ⇒ เอื้อมถึงเอกสารอื่นไม่ได้</summary>
    [HttpGet("{token}/pdf")]
    [AllowAnonymous]
    public async Task<IActionResult> Pdf(string token)
    {
        if (await OverRateAsync("pdf", 10, 120)) return TooMany();
        var doc = await FindByTokenAsync(token);
        if (doc == null || _pdf == null) return NotFound(new ApiResponse<string>(false, null, "ลิงก์ไม่ถูกต้องหรือหมดอายุ"));
        var gen = await _pdf.GenerateDocumentPdfAsync(doc.CompanyId,
            new Models.DTOs.DocumentTemplate.GeneratePdfRequest(doc.Id, null, null, null, null));
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(gen.PdfData, "application/pdf", gen.FileName);
    }

    /// <summary>รอบ 239: รูปลายเซ็นที่ลูกค้าเซ็นไว้ (หน้าลูกค้าแสดงสถานะ "ยอมรับแล้ว") — token ของใบนี้เท่านั้น · ตำแหน่งไฟล์ต้องอยู่ใต้โฟลเดอร์ของ
    /// บริษัท/เอกสารนี้ (<see cref="QuotationAcceptance.IsOwnSignatureLocation"/>) ⇒ token ของบริษัทหนึ่งอ่านไฟล์ของอีกบริษัทไม่ได้แม้แถวถูกแก้</summary>
    [HttpGet("{token}/signature")]
    [AllowAnonymous]
    public async Task<IActionResult> Signature(string token)
    {
        if (await OverRateAsync("signature", 30, 300)) return TooMany();
        var doc = await FindByTokenAsync(token);
        if (doc == null || doc.QuotationAcceptedAt == null
            || !QuotationAcceptance.IsOwnSignatureLocation(doc.QuotationAcceptSignaturePath, SignatureStore.Locator.AttachmentsRoot, doc.CompanyId, doc.Id))
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบลายเซ็น"));
        var bytes = await SignatureStore.ReadAllBytesAsync(doc.QuotationAcceptSignaturePath);
        // ไบต์นี้ผ่านตัวตรวจเต็มตอนรับแล้ว — ตอนอ่านตรวจแค่ magic + ขนาด (ไม่ถอด IDAT ซ้ำทุกครั้งที่เปิดลิงก์ · ฝ่ายค้าน N)
        if (!SignatureImageValidator.LooksLikeStoredPng(bytes))
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบลายเซ็น"));
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes!, "image/png");
    }

    /// <summary>รอบ 239: เพิ่ม <c>SignatureBase64</c> (data:image/png;base64,… จากช่องวาด — ไม่บังคับ เว้นบริษัทตั้งให้บังคับ)</summary>
    /// <param name="RevisionNumber">รอบ 267 ฝ่ายค้าน H1: เลข Rev ที่หน้าแสดงตอนลูกค้ากด (บังคับเมื่อเปิดด้วย token ของหน้าเอกสาร)</param>
    /// <param name="ContentHash">hash เนื้อหาที่หน้าแสดง (<c>contentHash</c> จากเส้นดู) — ส่งมาแล้วไม่ตรงเนื้อหาปัจจุบัน = ปฏิเสธ</param>
    /// <param name="Signer">รอบ 267 P2: ผู้ลงนาม (ชื่อ · นามสกุล · ตำแหน่ง · ติ๊กรับรอง) — บังคับเมื่อเปิดด้วย token ของหน้าเอกสาร ·
    /// ลิงก์ยอมรับที่ออกก่อนรอบ 267 ใช้ <paramref name="AcceptedBy"/> ตามเดิม</param>
    /// <param name="OtpCode">รหัสยืนยันอีเมล (เฉพาะเมื่อบริษัทตั้งยอดขั้นต่ำ — 267-1-6)</param>
    public record AcceptRequest(string AcceptedBy, string? Note = null, string? SignatureBase64 = null, int? RevisionNumber = null, string? ContentHash = null,
        CustomerSignRule.SignerInput? Signer = null, string? OtpCode = null);

    /// <summary>ยอมรับใบเสนอราคา — ผู้เขียนเดียว <c>CustomerDocumentResponseService.Accept</c> (ที่นี่ไม่มีตรรกะ)</summary>
    [HttpPost("{token}/accept")]
    [AllowAnonymous]
    [RequestSizeLimit(512 * 1024)]
    public async Task<IActionResult> Accept(string token, [FromBody] AcceptRequest request)
    {
        return Http(await _responses.Accept(token, request, Caller()));
    }

    // ==================== Delivery e-sign (Proof of Delivery) ====================

    [HttpGet("~/api/public/delivery/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> ViewDelivery(string token)
    {
        var doc = await FindDeliveryByTokenAsync(token);
        if (doc == null) return NotFound(new ApiResponse<string>(false, null, "ลิงก์ไม่ถูกต้องหรือหมดอายุ"));

        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == doc.CompanyId)
            .Select(c => new { c.Name })
            .FirstOrDefaultAsync();
        await _responses.MarkOpenedAsync(doc);

        return Ok(new ApiResponse<object>(true, new
        {
            companyName = company?.Name,
            documentNumber = doc.DocumentNumber,
            documentDate = doc.DocumentDate,
            contactName = doc.Contact?.Name,
            signedAt = doc.DeliverySignedAt,
            signedBy = doc.DeliverySignedBy,
            // รอบ 267 ฝ่ายค้าน H1: ฉบับที่หน้านี้แสดง — หน้าส่งกลับตอนเซ็นรับ
            revisionNumber = doc.RevisionNumber,
            contentHash = DocumentSignedContent.Hash(doc, doc.Lines),
            lines = doc.Lines
                .OrderBy(l => l.LineOrder)
                .Select(l => new { l.Description, l.Quantity, l.Unit })
                .ToList(),
        }));
    }

    /// <param name="RevisionNumber">รอบ 267 ฝ่ายค้าน H1: เลข Rev ที่หน้าแสดง (บังคับเมื่อเปิดด้วย token ของหน้าเอกสาร) · <paramref name="ContentHash"/> = hash เนื้อหาที่เห็น</param>
    /// <param name="Signer">รอบ 267 P2: ผู้รับของ (ชื่อ · นามสกุล · ตำแหน่ง · ติ๊กรับรอง) — บังคับเมื่อเปิดด้วย token ของหน้าเอกสาร</param>
    public record DeliverySignRequest(string SignedBy, string SignatureBase64, string? Note = null, int? RevisionNumber = null, string? ContentHash = null,
        CustomerSignRule.SignerInput? Signer = null);

    /// <summary>เซ็นรับสินค้า — ผู้เขียนเดียว <c>CustomerDocumentResponseService.SignDelivery</c> (ที่นี่ไม่มีตรรกะ)</summary>
    [HttpPost("~/api/public/delivery/{token}/sign")]
    [AllowAnonymous]
    public async Task<IActionResult> SignDelivery(string token, [FromBody] DeliverySignRequest request)
    {
        return Http(await _responses.SignDelivery(token, request, Caller()));
    }

    /// <summary>สร้าง token แบบ crypto-random — รอบ 267: ระบบไม่ออก token ยอมรับ/เซ็นรับของตัวเองแล้ว (ลิงก์ = ลิงก์หน้าเอกสาร) ·
    /// คงไว้สำหรับเทสต์ที่จำลองลิงก์รุ่นก่อน (เก็บลงฐานด้วย <c>CustomerDocPage.LegacyStored</c> เท่านั้น)</summary>
    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

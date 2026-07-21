using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Accounting.Controllers;

/// <summary>ลิงก์สาธารณะให้ลูกค้า "ดู + กดยอมรับ" ใบเสนอราคาโดยไม่ต้อง login
/// (capability URL — token 64 hex บน Document.QuotationAcceptToken).
/// อ่านได้เฉพาะ field ที่จำเป็นต่อการตัดสินใจ; เขียนได้อย่างเดียวคือ accept.
/// ฝั่งผู้ขายสร้าง/เพิกถอนลิงก์ผ่าน DocumentController (ต้อง login).</summary>
[ApiController]
[Route("api/public/quotation")]
public class PublicQuotationController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<PublicQuotationController> _logger;

    public PublicQuotationController(AccountingDbContext db, ILogger<PublicQuotationController> logger)
    {
        _db = db;
        _logger = logger;
    }

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

        return Ok(new ApiResponse<object>(true, new
        {
            companyName = company?.Name,
            documentNumber = doc.DocumentNumber,
            documentDate = doc.DocumentDate,
            contactName = doc.Contact?.Name,
            currency = doc.Currency,
            subTotal = doc.SubTotal,
            vatAmount = doc.VatAmount,
            totalAmount = doc.TotalAmount,
            notes = doc.Notes,
            expiresAt = doc.QuotationAcceptTokenExpiresAt,
            acceptedAt = doc.QuotationAcceptedAt,
            acceptedBy = doc.QuotationAcceptedBy,
            lines = doc.Lines
                .OrderBy(l => l.LineOrder)
                .Select(l => new { l.Description, l.Quantity, l.Unit, l.UnitPrice, l.Amount })
                .ToList(),
        }));
    }

    public record AcceptRequest(string AcceptedBy, string? Note = null);

    [HttpPost("{token}/accept")]
    [AllowAnonymous]
    public async Task<IActionResult> Accept(string token, [FromBody] AcceptRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AcceptedBy))
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาระบุชื่อผู้ยอมรับ"));

        var doc = await FindByTokenAsync(token, track: true);
        if (doc == null) return NotFound(new ApiResponse<string>(false, null, "ลิงก์ไม่ถูกต้องหรือหมดอายุ"));
        if (doc.QuotationAcceptedAt != null)
            return Ok(new ApiResponse<object>(true,
                new { acceptedAt = doc.QuotationAcceptedAt, acceptedBy = doc.QuotationAcceptedBy },
                "ใบเสนอราคานี้ถูกยอมรับไปแล้ว"));

        doc.QuotationAcceptedAt = DateTime.UtcNow;
        doc.QuotationAcceptedBy = request.AcceptedBy.Trim().Length > 200
            ? request.AcceptedBy.Trim()[..200] : request.AcceptedBy.Trim();
        // เก็บหลักฐานลง Notes (append-only trail — ไม่ทับของเดิม)
        var stamp = $"\n[ลูกค้ายอมรับออนไลน์] {doc.QuotationAcceptedBy} — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"
            + (string.IsNullOrWhiteSpace(request.Note) ? "" : $" · หมายเหตุ: {request.Note.Trim()}");
        doc.Notes = (doc.Notes ?? "") + stamp;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Quotation {DocNo} accepted online by {By} (company {Cid})",
            doc.DocumentNumber, doc.QuotationAcceptedBy, doc.CompanyId);

        return Ok(new ApiResponse<object>(true,
            new { acceptedAt = doc.QuotationAcceptedAt, acceptedBy = doc.QuotationAcceptedBy },
            "ยอมรับใบเสนอราคาเรียบร้อย — ผู้ขายจะติดต่อกลับเพื่อดำเนินการต่อ"));
    }

    private async Task<Models.Entities.Document?> FindByTokenAsync(string token, bool track = false)
    {
        // token = 64 hex — ตรวจรูปแบบก่อน กัน probing ด้วย input แปลก ๆ
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 80
            || !token.All(Uri.IsHexDigit))
            return null;

        var q = track ? _db.Documents.AsQueryable() : _db.Documents.AsNoTracking();
        var doc = await q
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.QuotationAcceptToken == token
                && d.DocumentType == DocumentType.Quotation
                && !d.IsDeleted
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Draft);
        if (doc == null) return null;
        if (doc.QuotationAcceptTokenExpiresAt is { } exp && exp < DateTime.UtcNow) return null;
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate ผ่าน doc.CompanyId
        await _db.HydrateContactAsync(doc.CompanyId, doc);
        return doc;
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

        return Ok(new ApiResponse<object>(true, new
        {
            companyName = company?.Name,
            documentNumber = doc.DocumentNumber,
            documentDate = doc.DocumentDate,
            contactName = doc.Contact?.Name,
            signedAt = doc.DeliverySignedAt,
            signedBy = doc.DeliverySignedBy,
            lines = doc.Lines
                .OrderBy(l => l.LineOrder)
                .Select(l => new { l.Description, l.Quantity, l.Unit })
                .ToList(),
        }));
    }

    public record DeliverySignRequest(string SignedBy, string SignatureBase64, string? Note = null);

    [HttpPost("~/api/public/delivery/{token}/sign")]
    [AllowAnonymous]
    public async Task<IActionResult> SignDelivery(string token, [FromBody] DeliverySignRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SignedBy))
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาระบุชื่อผู้รับสินค้า"));
        if (string.IsNullOrWhiteSpace(request.SignatureBase64))
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาวาดลายเซ็น"));
        // cap ~512KB base64 (ภาพ canvas PNG ปกติ < 50KB) กัน payload บวม
        if (request.SignatureBase64.Length > 700_000)
            return BadRequest(new ApiResponse<string>(false, null, "ภาพลายเซ็นใหญ่เกินไป"));

        var doc = await FindDeliveryByTokenAsync(token, track: true);
        if (doc == null) return NotFound(new ApiResponse<string>(false, null, "ลิงก์ไม่ถูกต้องหรือหมดอายุ"));
        if (doc.DeliverySignedAt != null)
            return Ok(new ApiResponse<object>(true,
                new { signedAt = doc.DeliverySignedAt, signedBy = doc.DeliverySignedBy },
                "ใบส่งของนี้ถูกเซ็นรับไปแล้ว"));

        doc.DeliverySignedAt = DateTime.UtcNow;
        doc.DeliverySignedBy = request.SignedBy.Trim().Length > 200
            ? request.SignedBy.Trim()[..200] : request.SignedBy.Trim();
        doc.DeliverySignatureBase64 = request.SignatureBase64.Trim();
        var stamp = $"\n[ลูกค้าเซ็นรับสินค้าออนไลน์] {doc.DeliverySignedBy} — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"
            + (string.IsNullOrWhiteSpace(request.Note) ? "" : $" · หมายเหตุ: {request.Note.Trim()}");
        doc.Notes = (doc.Notes ?? "") + stamp;
        await _db.SaveChangesAsync();

        _logger.LogInformation("DeliveryNote {DocNo} signed online by {By} (company {Cid})",
            doc.DocumentNumber, doc.DeliverySignedBy, doc.CompanyId);

        return Ok(new ApiResponse<object>(true,
            new { signedAt = doc.DeliverySignedAt, signedBy = doc.DeliverySignedBy },
            "เซ็นรับสินค้าเรียบร้อย — ลายเซ็นถูกประทับลงเอกสารแล้ว"));
    }

    private async Task<Models.Entities.Document?> FindDeliveryByTokenAsync(string token, bool track = false)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 80
            || !token.All(Uri.IsHexDigit))
            return null;

        var q = track ? _db.Documents.AsQueryable() : _db.Documents.AsNoTracking();
        var doc = await q
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.DeliverySignToken == token
                && d.DocumentType == DocumentType.DeliveryNote
                && !d.IsDeleted
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Draft);
        if (doc == null) return null;
        if (doc.DeliverySignTokenExpiresAt is { } exp && exp < DateTime.UtcNow) return null;
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate ผ่าน doc.CompanyId
        await _db.HydrateContactAsync(doc.CompanyId, doc);
        return doc;
    }

    /// <summary>สร้าง token แบบ crypto-random (helper กลางให้ DocumentController ใช้)</summary>
    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

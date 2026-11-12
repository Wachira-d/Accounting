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
/// <see cref="SignatureImageValidator"/> (ตัวเดียวกับเส้นเซ็นรับสินค้า) · ทุกเส้นตรวจ token ด้วย <c>FindByTokenAsync</c> เดียว</para></summary>
[ApiController]
[Route("api/public/quotation")]
public class PublicQuotationController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<PublicQuotationController> _logger;
    /// <summary>รอบ 209 P3 (E2): ยอมรับแล้ว ⇒ lead ที่ผูกใบ = ได้งาน + แจ้งร้าน · null (ประกอบเองนอก DI) = ไม่ทำขั้นนี้</summary>
    private readonly Accounting.Services.Implementations.CmsLeadService? _leads;
    /// <summary>รอบ 239: PDF ของใบ (ตัวเรนเดอร์เดียวกับที่ผู้ขายดาวน์โหลด/ส่งอีเมล) · null = ไม่มีเส้น PDF (ตอบ 404)</summary>
    private readonly IPdfGenerationService? _pdf;
    /// <summary>รอบ 239: ที่เก็บไฟล์กลาง (Local/S3) ของลายเซ็น · null (นอก DI) = ดิสก์ค่าเริ่มต้น</summary>
    private readonly Accounting.Services.Storage.IFileStorage? _storage;
    /// <summary>รอบ 239: เพดานต่อ IP (ตัวนับใน PostgreSQL ข้ามเครื่อง — ตัวเดียวกับ chatbot/จองที่พัก) · null = ไม่จำกัด (เทสต์)</summary>
    private readonly IChatRateLimiter? _rate;

    public PublicQuotationController(AccountingDbContext db, ILogger<PublicQuotationController> logger,
        Accounting.Services.Implementations.CmsLeadService? leads = null, IPdfGenerationService? pdf = null,
        Accounting.Services.Storage.IFileStorage? storage = null, IChatRateLimiter? rate = null)
    {
        _db = db;
        _logger = logger;
        _leads = leads;
        _pdf = pdf;
        _storage = storage;
        _rate = rate;
    }

    private Accounting.Services.Storage.IFileStorage SignatureStore => _storage ?? Accounting.Services.Storage.FileStorageDefaults.Local;

    /// <summary>เพดานต่อ IP ของเส้นสาธารณะ (PDF เรนเดอร์หนัก · ยอมรับ = เขียน) — ตัวนับร่วมข้าม instance · true = เกินเพดาน</summary>
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

    private static object AcceptedState(Models.Entities.Document doc) => new
    {
        acceptedAt = doc.QuotationAcceptedAt,
        acceptedBy = doc.QuotationAcceptedBy,
        acceptedNote = doc.QuotationAcceptNote,
        hasSignature = !string.IsNullOrWhiteSpace(doc.QuotationAcceptSignaturePath),
    };

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
        var requireSignature = await RequireSignatureAsync(doc.CompanyId);
        await MarkOpenedAsync(doc);

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
            expiresAt = RespondEnd(doc, token),
            acceptedAt = doc.QuotationAcceptedAt,
            acceptedBy = doc.QuotationAcceptedBy,
            // รอบ 239: สถานะ/ค่าที่หน้าลูกค้าแสดง — ข้อความยินยอมมาจากเซิร์ฟเวอร์ (หน้าเว็บไม่มีสำเนา)
            acceptedNote = doc.QuotationAcceptNote,
            hasSignature = !string.IsNullOrWhiteSpace(doc.QuotationAcceptSignaturePath),
            requireSignature,
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
    public record AcceptRequest(string AcceptedBy, string? Note = null, string? SignatureBase64 = null);

    [HttpPost("{token}/accept")]
    [AllowAnonymous]
    [RequestSizeLimit(512 * 1024)]
    public async Task<IActionResult> Accept(string token, [FromBody] AcceptRequest request)
    {
        if (await OverRateAsync("accept", 10, 60)) return TooMany();
        var doc = await FindByTokenAsync(token);
        if (doc == null) return NotFound(new ApiResponse<string>(false, null, "ลิงก์ไม่ถูกต้องหรือหมดอายุ"));

        // ตัวตัดสินเดียว: ยอมรับแล้ว (idempotent) · ชื่อบังคับ · ลายเซ็นบังคับตามค่าตั้ง · ลายเซ็นที่ส่งมาต้องผ่านตัวตรวจ
        var requireSignature = await RequireSignatureAsync(doc.CompanyId);
        var decision = QuotationAcceptance.DecideAccept(doc.QuotationAcceptedAt, request?.AcceptedBy, request?.SignatureBase64, requireSignature);
        if (decision.Outcome == QuotationAcceptance.Outcome.AlreadyAccepted)
            return Ok(new ApiResponse<object>(true, AcceptedState(doc), decision.Message));
        // รอบ 267 P1: เปิดด้วย token ของหน้าเอกสาร ⇒ ช่วงตอบรับจากตัวตัดสินเดียวกับปุ่มบนหน้าเอกสาร (พ้นวันยืนราคา = ปฏิเสธพร้อมเหตุ ไม่ใช่ยอมรับเงียบ)
        var viaPage = ViaPageToken(doc.QuotationAcceptToken, token);
        if (viaPage && RespondClosedReason(doc) is { } closed)
            return StatusCode(409, new ApiResponse<string>(false, null, closed));
        if (!decision.Accept)
            return BadRequest(new ApiResponse<string>(false, null, decision.Message));

        var now = DateTime.UtcNow;
        var name = QuotationAcceptance.CleanText(request!.AcceptedBy, QuotationAcceptance.NameMax)!;
        var note = QuotationAcceptance.CleanText(request.Note, QuotationAcceptance.NoteMax);
        // hash เนื้อหาที่ลูกค้าเห็นตอนยอมรับ — สูตรเดียวกับลายเซ็นลูกค้าทุกเส้น (ไม่ต่อข้อความลง Notes อีก ⇒ เนื้อหาไม่เปลี่ยนหลังยอมรับ)
        var contentHash = DocumentSignedContent.Hash(doc, doc.Lines);
        var ip = HttpContext?.Connection.RemoteIpAddress?.ToString();
        var uaHash = QuotationAcceptance.Sha256Hex(Request.Headers.UserAgent.ToString());
        var tokenHash = QuotationAcceptance.Sha256Hex(token.ToLowerInvariant());
        var pageHash = DocumentPayLinkPolicy.TokenHash(token);

        // ลายเซ็น: เขียนไบต์ที่ผ่านตัวตรวจแล้วลงที่เก็บกลางก่อน (ไม่ใช่ข้อความดิบจากคำขอ) — ยอมรับไม่สำเร็จ ⇒ ลบทิ้ง
        string? sigPath = null;
        string? sigSha = null;
        if (decision.Signature?.Png is { } png)
        {
            sigPath = QuotationAcceptance.SignatureLocation(SignatureStore.Locator.AttachmentsRoot, doc.CompanyId, doc.Id, Guid.NewGuid());
            await SignatureStore.WriteAsync(sigPath, png, "image/png");
            sigSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant();
        }

        // จองการยอมรับแบบอะตอมมิก (เฉพาะแถวที่ยังไม่ถูกยอมรับ + token เดิม) + แถว audit chain ในธุรกรรมเดียว — กดพร้อมกันสองเครื่อง = ยอมรับครั้งเดียว
        var claimed = 0;
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            claimed = await _db.Documents
                .Where(d => d.Id == doc.Id && d.CompanyId == doc.CompanyId
                    && d.QuotationAcceptedAt == null && d.QuotationAcceptToken == doc.QuotationAcceptToken
                    // รอบ 267: token ของหน้าเอกสาร ⇒ ลิงก์ต้องยังเป็น token นี้และไม่ถูกเพิกถอน ณ ตอนจอง (เพิกถอนระหว่างทาง = ไม่จอง)
                    && (!viaPage || (d.PayLinkTokenHash == pageHash && d.PayLinkRevokedAt == null)))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(d => d.QuotationAcceptedAt, now)
                    .SetProperty(d => d.QuotationAcceptedBy, name)
                    .SetProperty(d => d.QuotationAcceptNote, note)
                    .SetProperty(d => d.QuotationAcceptSignaturePath, sigPath)
                    .SetProperty(d => d.QuotationAcceptContentHash, contentHash)
                    .SetProperty(d => d.QuotationAcceptIp, ip)
                    .SetProperty(d => d.QuotationAcceptUserAgentHash, uaHash)
                    .SetProperty(d => d.QuotationAcceptTokenHash, tokenHash)
                    .SetProperty(d => d.UpdatedAt, now));
            if (claimed == 1)
            {
                _db.AddChainedAuditLog(new Models.Entities.AuditLog
                {
                    CompanyId = doc.CompanyId, UserId = null, UserEmail = null, Action = AuditAction.Approve,
                    EntityType = nameof(Models.Entities.Document), EntityId = doc.Id.ToString(),
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new { quotationAcceptedAt = (DateTime?)null }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        @event = "QuotationAcceptedOnline", documentNumber = doc.DocumentNumber, revisionNumber = doc.RevisionNumber,
                        acceptedBy = name, acceptedAt = now, note, hasSignature = sigPath != null, signatureSha256 = sigSha,
                        contentHash, tokenSha256 = tokenHash, userAgentSha256 = uaHash,
                        ruleCode = "ETA-9", legalReference = "พ.ร.บ.ว่าด้วยธุรกรรมทางอิเล็กทรอนิกส์ พ.ศ. 2544 ม.9",
                    }),
                    IpAddress = ip, Timestamp = now,
                });
                // รอบ 240 (คำตัดสิน 240-3): เหตุการณ์ quotation.accepted ลง outbox ในธุรกรรมเดียวกัน — ส่งจริงในงานเบื้องหลัง
                // (callback ต่อเอกสาร · webhook ที่ลงทะเบียน · แจ้งผู้ขาย) ⇒ ปลายทางล่มไม่ทำให้การยอมรับล้ม/ช้า
                SignEventOutbox.Enqueue(_db, doc, SignRequestKinds.QuotationAccept, now, name, sigPath != null, contentHash, tokenHash);
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
        }
        catch
        {
            if (sigPath != null) await TryDeleteSignatureAsync(sigPath);
            throw;
        }

        if (claimed == 0)
        {
            // อีกคำขอยอมรับไปก่อน (หรือลิงก์ถูกเพิกถอนระหว่างนี้) — ไม่ทับ · ทิ้งไฟล์ที่เพิ่งเขียน · ตอบสถานะจริง
            if (sigPath != null) await TryDeleteSignatureAsync(sigPath);
            var fresh = await FindByTokenAsync(token);
            if (fresh?.QuotationAcceptedAt == null)
                return NotFound(new ApiResponse<string>(false, null, "ลิงก์ไม่ถูกต้องหรือหมดอายุ"));
            return Ok(new ApiResponse<object>(true, AcceptedState(fresh), "ใบเสนอราคานี้ถูกยอมรับไปแล้ว"));
        }

        _logger.LogInformation("Quotation {DocNo} accepted online (signature: {HasSig}) (company {Cid})",
            doc.DocumentNumber, sigPath != null, doc.CompanyId);
        // รอบ 209 P3 (E2): lead ที่ผูกใบนี้ ⇒ ได้งาน + แจ้งร้าน (หลังบันทึกการยอมรับ · ไม่โยน — ล้มประทับบน lead/บันทึกระบบ)
        if (_leads != null)
            await _leads.OnQuotationAcceptedAsync(doc.CompanyId, doc.Id, doc.DocumentNumber, name, doc.TotalAmount);

        doc.QuotationAcceptedAt = now;
        doc.QuotationAcceptedBy = name;
        doc.QuotationAcceptNote = note;
        doc.QuotationAcceptSignaturePath = sigPath;
        return Ok(new ApiResponse<object>(true, AcceptedState(doc), decision.Message));
    }

    /// <summary>รอบ 240: ประทับ "ลูกค้าเปิดลิงก์แล้ว" ครั้งแรก (ป้ายสถานะบนรายการเอกสาร) — นับจากการโหลดหน้าสาธารณะเท่านั้น (ไม่มี tracking pixel ในอีเมล) ·
    /// เขียนแบบมีเงื่อนไข (แถวของใบนี้ที่ยังไม่ประทับ) · ล้มไม่กระทบการเปิดดู</summary>
    private async Task MarkOpenedAsync(Models.Entities.Document doc)
    {
        if (doc.SignLinkOpenedAt != null || doc.QuotationAcceptedAt != null || doc.DeliverySignedAt != null) return;
        try
        {
            var now = DateTime.UtcNow;
            await _db.Documents
                .Where(d => d.Id == doc.Id && d.CompanyId == doc.CompanyId && d.SignLinkOpenedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.SignLinkOpenedAt, now));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ประทับเวลาเปิดลิงก์ไม่สำเร็จ doc={DocId}", doc.Id);
        }
    }

    /// <summary>ค่าตั้ง "ต้องเซ็นเพื่อยอมรับใบเสนอราคา" ของบริษัทเจ้าของใบ (ไม่มีแถว = ไม่บังคับ)</summary>
    private async Task<bool> RequireSignatureAsync(Guid companyId)
        => await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.RequireSignatureForQuotationAccept)
            .FirstOrDefaultAsync();

    /// <summary>ลบไฟล์ลายเซ็นที่เขียนแล้วแต่การยอมรับไม่เกิด (ไม่ใช่เส้นเงิน · ล้มแค่บันทึก — ไฟล์กำพร้าไม่มีแถวชี้ ไม่มีใครเปิดได้)</summary>
    private async Task TryDeleteSignatureAsync(string location)
    {
        try { await SignatureStore.DeleteAsync(location); }
        catch (Exception ex) { _logger.LogWarning(ex, "ลบไฟล์ลายเซ็นที่ไม่ได้ใช้ไม่สำเร็จ {Location}", location); }
    }

    /// <summary>ด่าน token ตัวเดียวของทุกเส้นใบเสนอราคาสาธารณะ (ดู · PDF · ลายเซ็น · ยอมรับ) — รูปแบบ hex · ใบเสนอราคาที่ออกแล้ว (ไม่ร่าง/ไม่ยกเลิก/ไม่ถูกลบ) ·
    /// ไม่หมดอายุ · เรียกซ้ำสร้าง token ใหม่ = ลิงก์เดิมใช้ไม่ได้ (เพิกถอน) · การเทียบ token ทำในฐานด้วยดัชนี (ค้นแบบ equality ไม่ใช่วนเทียบในหน่วยความจำ)</summary>
    private async Task<Models.Entities.Document?> FindByTokenAsync(string token)
    {
        // token = 64 hex — ตรวจรูปแบบก่อน กัน probing ด้วย input แปลก ๆ
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 80
            || !token.All(Uri.IsHexDigit))
            return null;

        // รอบ 267 P1 (267-1-3): ฐานไม่เก็บ token ดิบ — ค้นด้วย hash สองรุ่น: (ก) ลิงก์ยอมรับที่ออกก่อนรอบ 267 (คอลัมน์เดิมเก็บ h1:hash · หมดเองตามอายุ)
        // (ข) token ของหน้าเอกสารของใบ (ลิงก์เดียวต่อใบ — ปุ่ม "ยอมรับ" บนหน้าเอกสารพามาที่หน้านี้ในช่วงเปลี่ยนผ่าน)
        var legacyKey = CustomerDocPage.LegacyStored(token);
        var pageHash = DocumentPayLinkPolicy.TokenHash(token);
        var doc = await _db.Documents.AsNoTracking()
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => (d.QuotationAcceptToken == legacyKey || (d.PayLinkTokenHash == pageHash && d.PayLinkRevokedAt == null))
                && d.DocumentType == DocumentType.Quotation
                && !d.IsDeleted
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Draft);
        if (doc == null) return null;
        if (ViaPageToken(doc.QuotationAcceptToken, token))
        {
            if (!PageLinkLive(doc, token)) return null;
            await _db.HydrateContactAsync(doc.CompanyId, doc);
            return doc;
        }
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
        await MarkOpenedAsync(doc);

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
        // รอบ 239 ฝ่ายค้าน S2: เพดานต่อ IP → หาใบจาก token → เซ็นแล้ว = สถานะเดิม (idempotent) → ค่อยถอด/ตรวจรูป
        // (เดิมถอดรูปก่อนรู้ว่า token ใช้ได้ ⇒ token มั่วก็กิน CPU ถอด PNG ได้)
        if (await OverRateAsync("delivery", 10, 60)) return TooMany();
        var doc = await FindDeliveryByTokenAsync(token, track: true);
        if (doc == null) return NotFound(new ApiResponse<string>(false, null, "ลิงก์ไม่ถูกต้องหรือหมดอายุ"));
        if (doc.DeliverySignedAt != null)
            return Ok(new ApiResponse<object>(true,
                new { signedAt = doc.DeliverySignedAt, signedBy = doc.DeliverySignedBy },
                "ใบส่งของนี้ถูกเซ็นรับไปแล้ว"));
        // รอบ 267 P1: token ของหน้าเอกสาร ⇒ ช่วงเซ็นรับจากตัวตัดสินเดียวกับปุ่มบนหน้าเอกสาร
        var viaPage = ViaPageToken(doc.DeliverySignToken, token);
        if (viaPage && RespondClosedReason(doc) is { } closed)
            return StatusCode(409, new ApiResponse<string>(false, null, closed));
        if (string.IsNullOrWhiteSpace(request.SignedBy))
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาระบุชื่อผู้รับสินค้า"));
        // รอบ 239: ตัวตรวจลายเซ็นตัวเดียวกับการยอมรับใบเสนอราคา (PNG จริง · ≤ 200KB · ขนาดภาพ · ไม่ว่าง) — เดิมรับ base64 อะไรก็ได้ ≤ 700KB
        var sig = SignatureImageValidator.Validate(request.SignatureBase64);
        if (!sig.Ok)
            return BadRequest(new ApiResponse<string>(false, null, sig.Message ?? "กรุณาวาดลายเซ็น"));

        // รอบ 240: จองการเซ็นแบบอะตอมมิก (เฉพาะแถวที่ยังไม่เซ็น + token เดิม) + เหตุการณ์ delivery.signed ลง outbox ในธุรกรรมเดียว —
        // กดพร้อมกันสองเครื่อง = เซ็นครั้งเดียว · เหตุการณ์เดียว (เดิมเขียนทับกันได้)
        var now = DateTime.UtcNow;
        var contentHash = DocumentSignedContent.Hash(doc, doc.Lines);   // เนื้อหาที่ลูกค้าเห็นตอนเซ็น (ก่อนต่อหมายเหตุ)
        var tokenHash = QuotationAcceptance.Sha256Hex(token.ToLowerInvariant());
        var pageHash = DocumentPayLinkPolicy.TokenHash(token);
        await using var tx = await _db.Database.BeginTransactionAsync();
        var claimed = await _db.Documents
            .Where(d => d.Id == doc.Id && d.CompanyId == doc.CompanyId
                && d.DeliverySignedAt == null && d.DeliverySignToken == doc.DeliverySignToken
                && (!viaPage || (d.PayLinkTokenHash == pageHash && d.PayLinkRevokedAt == null)))
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.DeliverySignedAt, now));
        if (claimed == 0)
        {
            await tx.RollbackAsync();
            var fresh = await FindDeliveryByTokenAsync(token);
            if (fresh?.DeliverySignedAt == null)
                return NotFound(new ApiResponse<string>(false, null, "ลิงก์ไม่ถูกต้องหรือหมดอายุ"));
            return Ok(new ApiResponse<object>(true,
                new { signedAt = fresh.DeliverySignedAt, signedBy = fresh.DeliverySignedBy },
                "ใบส่งของนี้ถูกเซ็นรับไปแล้ว"));
        }

        doc.DeliverySignedAt = now;
        doc.DeliverySignedBy = request.SignedBy.Trim().Length > 200
            ? request.SignedBy.Trim()[..200] : request.SignedBy.Trim();
        doc.DeliverySignatureBase64 = sig.DataUri;   // ไบต์ที่ผ่านตัวตรวจแล้ว (ไม่ใช่ข้อความดิบจากคำขอ)
        var stamp = $"\n[ลูกค้าเซ็นรับสินค้าออนไลน์] {doc.DeliverySignedBy} — {now:yyyy-MM-dd HH:mm} UTC"
            + (string.IsNullOrWhiteSpace(request.Note) ? "" : $" · หมายเหตุ: {request.Note.Trim()}");
        doc.Notes = (doc.Notes ?? "") + stamp;
        SignEventOutbox.Enqueue(_db, doc, SignRequestKinds.DeliverySign, now, doc.DeliverySignedBy, hasSignature: true, contentHash, tokenHash);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

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
        // รอบ 267 P1 (267-1-3): ค้นด้วย hash สองรุ่น เหมือน FindByTokenAsync (ลิงก์เซ็นรับรุ่นก่อน · token ของหน้าเอกสาร)
        var legacyKey = CustomerDocPage.LegacyStored(token);
        var pageHash = DocumentPayLinkPolicy.TokenHash(token);
        var doc = await q
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => (d.DeliverySignToken == legacyKey || (d.PayLinkTokenHash == pageHash && d.PayLinkRevokedAt == null))
                && d.DocumentType == DocumentType.DeliveryNote
                && !d.IsDeleted
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Draft);
        if (doc == null) return null;
        if (ViaPageToken(doc.DeliverySignToken, token))
        {
            if (!PageLinkLive(doc, token)) return null;
            await _db.HydrateContactAsync(doc.CompanyId, doc);
            return doc;
        }
        if (doc.DeliverySignTokenExpiresAt is { } exp && exp < DateTime.UtcNow) return null;
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate ผ่าน doc.CompanyId
        await _db.HydrateContactAsync(doc.CompanyId, doc);
        return doc;
    }

    // ═══════════════ รอบ 267 P1 — token ของหน้าเอกสารบนหน้าตอบรับเดิม (ช่วงเปลี่ยนผ่าน · ลบพร้อมหน้าเดิมในแพ็กเก็บกวาด) ═══════════════

    /// <summary>คำขอนี้มาด้วย token ของหน้าเอกสาร (ไม่ใช่ลิงก์ยอมรับ/เซ็นรับรุ่นก่อน) — ค่าในคอลัมน์รุ่นก่อนไม่ตรง hash ของ token ที่ถือมา</summary>
    private static bool ViaPageToken(string? legacyStored, string token)
    {
        return !string.Equals(legacyStored, CustomerDocPage.LegacyStored(token), StringComparison.Ordinal);
    }

    /// <summary>token ของหน้าเอกสารยังใช้ได้กับใบนี้ไหม — ยืนยัน hash เวลาคงที่ · ไม่เพิกถอน/ไม่หมดอายุ · ใบออกเลขแล้ว (ตัวตัดสินลิงก์เดียวกับหน้าเอกสาร)</summary>
    private static bool PageLinkLive(Models.Entities.Document doc, string token)
    {
        return DocumentPayLinkPolicy.TokenMatches(token, doc.PayLinkTokenHash)
               && DocumentPayLinkPolicy.LinkUsable(doc.PayLinkTokenHash, doc.PayLinkExpiresAt, doc.PayLinkRevokedAt, DateTime.UtcNow, null)
               && DocumentStatusRules.IsIssued(doc.Status);
    }

    /// <summary>เหตุที่ตอบรับไม่ได้เพราะพ้นช่วง (token ของหน้าเอกสาร) — ตัวตัดสินเดียว <see cref="CustomerDocPage.RespondWindowClosedReason"/></summary>
    private static string? RespondClosedReason(Models.Entities.Document doc)
    {
        return CustomerDocPage.RespondWindowClosedReason(false, doc.DocumentType, doc.DueDate, doc.SignLinkIssuedAt ?? doc.PayLinkIssuedAt, DateTime.UtcNow);
    }

    /// <summary>วันสิ้นสุดที่หน้าตอบรับแสดง — ลิงก์รุ่นก่อน = วันหมดอายุของตัวเอง · token ของหน้าเอกสาร = สิ้นสุดช่วงตอบรับ</summary>
    private static DateTime? RespondEnd(Models.Entities.Document doc, string token)
        => ViaPageToken(doc.QuotationAcceptToken, token)
            ? CustomerDocPage.RespondWindowEnd(doc.DocumentType, doc.DueDate, doc.SignLinkIssuedAt ?? doc.PayLinkIssuedAt)
            : doc.QuotationAcceptTokenExpiresAt;

    /// <summary>สร้าง token แบบ crypto-random — รอบ 267: ระบบไม่ออก token ยอมรับ/เซ็นรับของตัวเองแล้ว (ลิงก์ = ลิงก์หน้าเอกสาร) ·
    /// คงไว้สำหรับเทสต์ที่จำลองลิงก์รุ่นก่อน (เก็บลงฐานด้วย <c>CustomerDocPage.LegacyStored</c> เท่านั้น)</summary>
    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

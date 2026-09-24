using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.FileAttachment;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/attachments")]
[Authorize]
public class FileAttachmentController : ControllerBase
{
    private readonly IFileAttachmentService _attachmentService;
    private readonly IImageProcessingService _images;
    private readonly AccountingDbContext _db;
    private readonly IAttachmentAccessGate _gate;
    private readonly ISubscriptionService _subscriptions;

    public FileAttachmentController(IFileAttachmentService attachmentService, IImageProcessingService images,
        AccountingDbContext db, IAttachmentAccessGate gate, ISubscriptionService subscriptions)
    {
        _attachmentService = attachmentService;
        _images = images;
        _db = db;
        _gate = gate;
        _subscriptions = subscriptions;
    }

    /// <summary>
    /// **ด่านของไฟล์แนบทุกชนิด** — คืน <c>null</c> = ผ่าน · ตัวตัดสินจริงอยู่ที่ <see cref="IAttachmentAccessGate"/>
    /// (service ตัวเดียวที่ทุกทางเข้าเรียก — ย้ายออกจาก controller นี้ในรอบ 193 S2 เพราะทางเข้าอื่นที่แตะไฟล์ชุดเดียวกัน
    /// เรียกเมธอด private ไม่ได้ จึงไม่มีด่านเลย) · ตาราง "ชนิด → คีย์" อยู่ที่ <see cref="AttachmentPermissionScope"/>
    /// </summary>
    private Task<AttachmentDenial?> DenyAttachmentAsync(
        Guid companyId, string? entityType, Guid entityId, AttachmentAccess access, string verb, Guid? attachmentId = null)
        => _gate.DenyAttachmentAsync(companyId, JwtHelper.GetUserIdFromClaims(User), entityType, entityId,
            access, verb, attachmentId);

    /// <summary>อ่านหัวไฟล์ให้ได้ครบ <see cref="UploadFileType.HeaderBytes"/> (หรือจนจบไฟล์) —
    /// <c>ReadAsync</c> ครั้งเดียวอาจคืนน้อยกว่าที่ขอ</summary>
    private static async Task<byte[]> ReadHeadAsync(IFormFile file)
    {
        var buf = new byte[UploadFileType.HeaderBytes];
        var total = 0;
        await using var s = file.OpenReadStream();
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total, buf.Length - total));
            if (n <= 0) break;
            total += n;
        }
        return buf.AsSpan(0, total).ToArray();
    }

    [HttpPost("{entityType}/{entityId:guid}")]
    [RequestSizeLimit(25 * 1024 * 1024)] // 25MB max
    public async Task<ActionResult<ApiResponse<FileAttachmentResponse>>> Upload(
        Guid companyId, string entityType, Guid entityId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<FileAttachmentResponse>(false, null!, "กรุณาอัพโหลดไฟล์"));

        // ชนิดที่รับจากหน้าเว็บ + สิทธิ์ต่อชนิด อยู่ในตารางเดียว (AttachmentPermissionScope) — เดิมเป็นอาร์เรย์ในเมธอดนี้
        // และมีด่านสิทธิ์แค่ "Document" · "ExpenseClaim" = หลักฐาน §65 ทวิ "ไม่มีใบเสร็จ" ที่พนักงานแนบเอง ·
        // "PayrollRun" = สลิปโอนเงิน/ใบเสร็จ สปส./ภ.ง.ด.1 (เก็บ 5 ปี — พ.ร.บ.การบัญชี ม.10)
        var rule = AttachmentPermissionScope.Resolve(entityType);
        if (!rule.ClientUploadAllowed)
            return BadRequest(new ApiResponse<FileAttachmentResponse>(false, null!, rule.IsKnown
                ? $"แนบไฟล์ของ{rule.ModuleTh}จากหน้านี้ไม่ได้ — ระบบแนบให้เองผ่านหน้าของรายการนั้น"
                : AttachmentPermissionScope.UnknownTypeMessage(entityType)));
        entityType = rule.CanonicalType ?? entityType;

        // ด่านสิทธิ์ของโมดูลนั้น + เจ้าของต้องมีอยู่จริงในบริษัทนี้ — ไม่ดูสถานะเอกสาร:
        // แนบไฟล์เพิ่มบนใบที่อนุมัติแล้วได้ เพราะไม่แตะเลขที่/ยอด/JE
        var deny = await DenyAttachmentAsync(companyId, entityType, entityId, AttachmentAccess.Write, "แนบไฟล์");
        if (deny is { } d) return StatusCode(d.Status, new ApiResponse<FileAttachmentResponse>(false, null!, d.Message));

        var userId = JwtHelper.GetUserIdFromClaims(User);

        // ═══ ชนิดไฟล์ตัดสินจาก **ไบต์จริง** (รอบ 190 ข้อ 7) ═══
        // เดิมเชื่อ Content-Type + นามสกุลจาก client แล้วเก็บ Content-Type นั้นไว้ตอบตอนดาวน์โหลด
        // ⇒ HTML ที่ตั้งชื่อ .csv / Content-Type ปลอมผ่านได้ · ตอนนี้: นามสกุลที่เซฟและ
        // Content-Type ที่เก็บมาจากผลตรวจไบต์เท่านั้น (UnsupportedUploadException → 400 ข้อความไทย)
        var head = await ReadHeadAsync(file);
        var kind = UploadFileType.SniffAttachment(head, file.FileName)
            ?? throw new UnsupportedUploadException(UploadFileType.AttachmentRejectMessage);

        var storageDir = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "attachments", companyId.ToString());
        Directory.CreateDirectory(storageDir);

        string fileName; string storagePath; long finalSize;
        if (kind.IsImage)
        {
            // Choose profile by entity type — slip-like things stay readable, the rest get the generic cap.
            var profile = entityType is "Payment" or "Document" or "PayrollRun" ? ImageProfile.Slip : ImageProfile.Generic;
            await using var s = file.OpenReadStream();
            var processed = await _images.ProcessAndSaveAsync(s, kind.ContentType, file.FileName, storageDir, $"/uploads/attachments/{companyId}", profile);
            fileName = Path.GetFileName(processed.AbsolutePath);
            storagePath = Path.Combine("uploads", "attachments", companyId.ToString(), fileName);
            finalSize = processed.FinalBytes;
        }
        else
        {
            var shortGuid = Guid.NewGuid().ToString("N")[..8];
            fileName = $"{entityType}_{entityId}_{DateTime.UtcNow:yyyyMMddHHmmss}_{shortGuid}{kind.Extension}";
            storagePath = Path.Combine("uploads", "attachments", companyId.ToString(), fileName);
            var fullPath = Path.Combine(Directory.GetCurrentDirectory(), storagePath);
            using (var stream = new FileStream(fullPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }
            finalSize = file.Length;
        }

        // ชื่อที่ผู้ใช้เห็น = ชื่อเดิม + นามสกุลของไฟล์ที่เก็บจริง (รูปที่ถูกย่ออาจกลายเป็น .jpg) ·
        // service ใช้นามสกุลของชื่อนี้ตั้งชื่อไฟล์ปลายทาง ⇒ ต้องตรงกับไบต์ ไม่ใช่คำบอกของ client
        var finalExt = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(file.FileName);
        var displayName = (string.IsNullOrWhiteSpace(baseName) ? "attachment" : baseName) + finalExt;
        var contentType = UploadFileType.ContentTypeForExtension(finalExt);

        var result = await _attachmentService.UploadAsync(companyId, entityType, entityId,
            fileName, displayName, contentType, finalSize, storagePath, userId);

        // ═══ เพดานพื้นที่ = เตือน ไม่บล็อก (คำตัดสินเจ้าของ รอบ 193 ข้อ 30) ═══
        // ไฟล์ถูกบันทึกแล้ว (หลักฐานบัญชีสำคัญกว่าโควตา IT) · การใช้งานนับจาก Σ ขนาดไฟล์ในตารางจริง ⇒ แถวที่เพิ่ง
        // บันทึกถูกนับทันที (ไม่มี counter ให้ลืมเขียน) · ไม่รู้เพดาน (ไม่มี subscription) = ไม่เตือน ไม่ใช่ "0 จาก 0"
        var storage = await _subscriptions.GetStorageStatusAsync(companyId);
        var storageWarning = AttachmentStorageNotice.Message(storage?.UsedBytes, storage?.MaxBytes);
        if (storageWarning != null) result = result with { StorageWarning = storageWarning };

        return StatusCode(201, new ApiResponse<FileAttachmentResponse>(true, result,
            storageWarning == null ? "อัพโหลดไฟล์สำเร็จ" : "อัพโหลดไฟล์สำเร็จ — " + storageWarning));
    }

    [HttpGet("{entityType}/{entityId:guid}")]
    public async Task<ActionResult<ApiResponse<List<FileAttachmentResponse>>>> GetByEntity(
        Guid companyId, string entityType, Guid entityId)
    {
        // ดูรายการไฟล์ = สิทธิ์อ่านของโมดูลนั้น (รอบ 193 ข้อ 29) — สลิปเงินเดือนต้องผ่านด่านความลับ Payroll ·
        // ใบเบิกของคนอื่นต้องเป็นผู้ตรวจ · เอกสารต้องอยู่ฝั่งที่มองเห็น
        var deny = await DenyAttachmentAsync(companyId, entityType, entityId, AttachmentAccess.Read, "ดูไฟล์แนบ");
        if (deny is { } d) return StatusCode(d.Status, new ApiResponse<List<FileAttachmentResponse>>(false, null, d.Message));
        var canonical = AttachmentPermissionScope.Resolve(entityType).CanonicalType ?? entityType;
        var result = await _attachmentService.GetByEntityAsync(companyId, canonical, entityId);
        return Ok(new ApiResponse<List<FileAttachmentResponse>>(true, result));
    }

    [HttpDelete("{attachmentId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid attachmentId)
    {
        var att = await _attachmentService.GetByIdAsync(companyId, attachmentId);
        if (att == null) return NotFound(new ApiResponse<string>(false, null!, "ไม่พบไฟล์แนบ"));
        // ทุกชนิดผ่านด่านของโมดูลเจ้าของ (รอบ 193 ข้อ 29) · ชนิดที่ไม่รู้จัก = ห้ามลบ
        var deny = await DenyAttachmentAsync(companyId, att.EntityType, att.EntityId, AttachmentAccess.Write, "ลบไฟล์แนบ", att.Id);
        if (deny is { } d) return StatusCode(d.Status, new ApiResponse<string>(false, null!, d.Message));
        // ★ หลักฐานประกอบรายการบัญชีต้องเก็บ 5 ปี (พ.ร.บ.การบัญชี ม.10 · §87/3) — ฝ่ายค้านรอบ 190:
        // เปิดให้แนบ/ลบบนใบที่อนุมัติแล้ว แต่ DeleteAsync ลบไฟล์จริง ⇒ ผู้มีสิทธิ์สร้างใบลบหลักฐานของใบที่ยื่น
        // ภ.พ.30 ไปแล้วได้ถาวร · ตอนนี้: ใบร่าง = ลบจริงได้ (ยังไม่ใช่รายการบัญชี) · นอกนั้นถอดจากรายการแต่
        // เก็บไฟล์จริงไว้ (soft-delete) — ผู้ใช้ยังถอดไฟล์ที่แนบผิดได้เหมือนเดิม
        DocumentStatus? docStatus = att.EntityType == "Document"
            ? await _db.Documents.AsNoTracking()
                .Where(x => x.Id == att.EntityId && x.CompanyId == companyId)
                .Select(x => (DocumentStatus?)x.Status)
                .FirstOrDefaultAsync()
            : null;
        var keepFile = AttachmentRetention.MustKeepPhysicalFile(att.EntityType, docStatus);
        await _attachmentService.DeleteAsync(companyId, attachmentId, keepFile);
        return NoContent();
    }

    /// <summary>
    /// Authenticated download — streams the physical file ONLY when the JWT belongs to
    /// a user with access to the owning company. Static-file serving via /uploads/ is
    /// kept for backward compat but UI should prefer this endpoint for sensitive
    /// financial documents to prevent URL leaks (e.g. logged in browser history).
    /// </summary>
    [HttpGet("{attachmentId:guid}/download")]
    public async Task<IActionResult> Download(Guid companyId, Guid attachmentId)
    {
        var attachment = await _attachmentService.GetByIdAsync(companyId, attachmentId);
        if (attachment == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบไฟล์"));
        // ดาวน์โหลด = สิทธิ์อ่านของโมดูลเจ้าของไฟล์ (รอบ 193 ข้อ 29) — เดิมสมาชิกทุกคนโหลดสลิปเงินเดือนได้ถ้ารู้ id
        var deny = await DenyAttachmentAsync(companyId, attachment.EntityType, attachment.EntityId, AttachmentAccess.Read, "ดาวน์โหลดไฟล์แนบ", attachment.Id);
        if (deny is { } d) return StatusCode(d.Status, new ApiResponse<object>(false, null, d.Message));

        var fullPath = Path.IsPathRooted(attachment.StoragePath)
            ? attachment.StoragePath
            : Path.Combine(Directory.GetCurrentDirectory(), attachment.StoragePath);
        if (!System.IO.File.Exists(fullPath))
            return NotFound(new ApiResponse<object>(false, null, "ไฟล์ถูกลบหรือย้ายแล้ว"));

        var contentType = string.IsNullOrEmpty(attachment.ContentType)
            ? "application/octet-stream" : attachment.ContentType;
        return PhysicalFile(fullPath, contentType, attachment.OriginalFileName);
    }
}

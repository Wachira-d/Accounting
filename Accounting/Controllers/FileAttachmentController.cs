using Accounting.Data;
using Accounting.Helpers;
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
    private readonly IPermissionService _permissions;

    public FileAttachmentController(IFileAttachmentService attachmentService, IImageProcessingService images,
        AccountingDbContext db, IPermissionService permissions)
    {
        _attachmentService = attachmentService;
        _images = images;
        _db = db;
        _permissions = permissions;
    }

    /// <summary>
    /// **ด่านของไฟล์แนบเอกสาร** — คืน <c>null</c> = ผ่าน · คืน (สถานะ HTTP, ข้อความไทย) = ปฏิเสธ
    ///
    /// <para>═══ ที่มา (รอบ 190 ข้อ 7) ═══ เดิม endpoint นี้มีแค่ <c>[Authorize]</c> ระดับคลาส
    /// (ตอบแค่ "ล็อกอินไหม") + <c>TenantAccessMiddleware</c> (ตอบแค่ "เป็นสมาชิกบริษัทไหม")
    /// ⇒ สมาชิกทุกคนแนบ/ลบไฟล์ของเอกสารใดก็ได้ และ <c>entityId</c> ไม่ถูกตรวจว่ามีอยู่จริงในบริษัท</para>
    ///
    /// <para>ใช้สิทธิ์ "สร้าง <b>หรือ</b> อนุมัติ เอกสารประเภทนั้น" — คนทำเอกสารและคนอนุมัติต้องแนบ
    /// หลักฐานได้ · <b>ไม่ดูสถานะเอกสาร</b>: การแนบไฟล์ไม่แก้เลขที่ ยอดเงิน หรือ JE จึงทำได้ทุกสถานะ
    /// (รวมเอกสารที่อนุมัติ/ยกเลิกแล้ว — หลักฐานมักมาถึงทีหลัง)</para>
    ///
    /// <para>⚠️ ครอบเฉพาะ <c>entityType = "Document"</c> — ชนิดอื่น (ผู้ติดต่อ · ใบเบิกของพนักงาน ·
    /// รอบเงินเดือน …) ยังไม่มีด่านสิทธิ์ต่อชนิด (คำถามเจ้าของ ใน erp-review/2026-09-24/team-U.md)</para>
    /// </summary>
    private async Task<(int Status, string Message)?> DenyDocAsync(Guid companyId, Guid documentId, string verb)
    {
        var type = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => (DocumentType?)d.DocumentType)
            .FirstOrDefaultAsync();
        if (type == null) return (404, "ไม่พบเอกสารนี้ในบริษัท");
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userId, type.Value)
            || await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, type.Value))
            return null;
        return (403, $"ไม่มีสิทธิ์{verb}ของเอกสาร {type.Value} (ต้องมีสิทธิ์สร้างหรืออนุมัติเอกสารประเภทนี้)");
    }

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

        // "ExpenseClaim" added 2026 for the §65 ทวิ "ไม่มีใบเสร็จ" flow
        // — employee must attach evidence (photo of goods, taxi meter,
        // CC slip, etc.) before Submit can fire when NoReceipt = true.
        // "PayrollRun" — สลิปโอนเงิน/ใบเสร็จ สปส. + ใบเสร็จ ภ.ง.ด.1 ของงวดเงินเดือน
        // เป็นหลักฐานการจ่ายที่ต้องเก็บ 5 ปี (พ.ร.บ.การบัญชี ม.10)
        var allowedEntityTypes = new[] { "Document", "Contact", "Payment", "JournalEntry", "FixedAsset", "Expense", "ExpenseClaim", "Product", "Project", "PayrollRun" };
        if (!allowedEntityTypes.Contains(entityType))
            return BadRequest(new ApiResponse<FileAttachmentResponse>(false, null!, "ประเภทไม่ถูกต้อง"));

        // ด่านสิทธิ์ + เอกสารต้องมีอยู่จริงในบริษัทนี้ (ดู DenyDocAsync) — ไม่ดูสถานะเอกสาร:
        // แนบไฟล์เพิ่มบนใบที่อนุมัติแล้วได้ เพราะไม่แตะเลขที่/ยอด/JE
        if (entityType == "Document")
        {
            var deny = await DenyDocAsync(companyId, entityId, "แนบไฟล์");
            if (deny is { } d) return StatusCode(d.Status, new ApiResponse<FileAttachmentResponse>(false, null!, d.Message));
        }

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

        return StatusCode(201, new ApiResponse<FileAttachmentResponse>(true, result, "อัพโหลดไฟล์สำเร็จ"));
    }

    [HttpGet("{entityType}/{entityId:guid}")]
    public async Task<ActionResult<ApiResponse<List<FileAttachmentResponse>>>> GetByEntity(
        Guid companyId, string entityType, Guid entityId)
    {
        var result = await _attachmentService.GetByEntityAsync(companyId, entityType, entityId);
        return Ok(new ApiResponse<List<FileAttachmentResponse>>(true, result));
    }

    [HttpDelete("{attachmentId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid attachmentId)
    {
        var att = await _attachmentService.GetByIdAsync(companyId, attachmentId);
        if (att == null) return NotFound(new ApiResponse<string>(false, null!, "ไม่พบไฟล์แนบ"));
        if (att.EntityType == "Document")
        {
            var deny = await DenyDocAsync(companyId, att.EntityId, "ลบไฟล์แนบ");
            if (deny is { } d) return StatusCode(d.Status, new ApiResponse<string>(false, null!, d.Message));
        }
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

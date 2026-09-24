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
    private readonly IPermissionService _permissions;
    private readonly ISensitivityService _sensitivity;
    private readonly ISubscriptionService _subscriptions;

    public FileAttachmentController(IFileAttachmentService attachmentService, IImageProcessingService images,
        AccountingDbContext db, IPermissionService permissions, ISensitivityService sensitivity,
        ISubscriptionService subscriptions)
    {
        _attachmentService = attachmentService;
        _images = images;
        _db = db;
        _permissions = permissions;
        _sensitivity = sensitivity;
        _subscriptions = subscriptions;
    }

    private enum Access { Read, Write }

    /// <summary>
    /// **ด่านของไฟล์แนบทุกชนิด** — คืน <c>null</c> = ผ่าน · คืน (สถานะ HTTP, ข้อความไทย) = ปฏิเสธ
    ///
    /// <para>═══ ที่มา ═══ รอบ 190 (ข้อ 7) ใส่ด่านให้ <c>"Document"</c> ชนิดเดียว — ชนิดอื่นมีแค่ <c>[Authorize]</c>
    /// ระดับคลาส (ตอบแค่ "ล็อกอินไหม") · รอบ 193 (คำตัดสินเจ้าของข้อ 29): <b>ทุกชนิดผูกกับสิทธิ์ของโมดูลนั้น</b>
    /// ทั้งแนบ/ลบ (<see cref="Access.Write"/>) และดูรายการ/ดาวน์โหลด (<see cref="Access.Read"/>) —
    /// ตาราง "ชนิด → คีย์" อยู่ที่ <see cref="AttachmentPermissionScope"/> ตัวเดียว</para>
    ///
    /// <para>ชนิดที่ไม่รู้จัก: <b>ห้ามเขียน</b> (ทิศปลอดภัย — ไม่รู้ว่าใครมีสิทธิ์ = ไม่ให้แก้หลักฐาน) · อ่านได้ระดับสมาชิก
    /// (พฤติกรรมเดิม — ไฟล์ถูกกรอง CompanyId อยู่แล้ว) · <b>ไม่ดูสถานะเอกสาร</b>: การแนบไฟล์ไม่แก้เลขที่/ยอด/JE
    /// จึงทำได้ทุกสถานะ (หลักฐานมักมาถึงทีหลัง)</para>
    /// </summary>
    private async Task<(int Status, string Message)?> DenyAttachmentAsync(
        Guid companyId, string? entityType, Guid entityId, Access access, string verb)
    {
        var rule = AttachmentPermissionScope.Resolve(entityType);
        if (!rule.IsKnown)
        {
            if (access == Access.Write) return (403, AttachmentPermissionScope.UnknownTypeMessage(entityType));
            return null;
        }

        var userId = JwtHelper.GetUserIdFromClaims(User);
        switch (rule.Kind)
        {
            case AttachmentOwnerKind.Document:
                return await DenyDocAsync(companyId, entityId, access, verb, rule);

            case AttachmentOwnerKind.Payment:
            {
                // การชำระ = ด่านของเอกสารที่ถูกชำระ (ชุดเดียวกับ CreatePayment/VoidPayment ใน DocumentController)
                var docId = await _db.Payments.AsNoTracking()
                    .Where(p => p.Id == entityId && p.CompanyId == companyId)
                    .Select(p => (Guid?)p.DocumentId)
                    .FirstOrDefaultAsync();
                if (docId == null)
                {
                    if (access == Access.Write) return (404, "ไม่พบรายการชำระเงินนี้ในบริษัท");
                    return null;
                }
                return await DenyDocAsync(companyId, docId.Value, access, verb, rule);
            }

            case AttachmentOwnerKind.ExpenseClaim:
            {
                // เจ้าของใบเบิกแนบ/ดูหลักฐานของตัวเองได้เสมอ (flow §65 ทวิ "ไม่มีใบเสร็จ" บังคับให้พนักงานแนบเอง)
                // · ของคนอื่นต้องเป็นผู้ตรวจ (HR.Admin / Expense.Approve — ชุดเดียวกับ ExpenseClaimController.GetAll)
                var owner = await _db.ExpenseClaims.AsNoTracking()
                    .Where(c => c.Id == entityId && c.CompanyId == companyId)
                    .Select(c => (Guid?)c.SubmittedByUserId)
                    .FirstOrDefaultAsync();
                if (owner == null && access == Access.Write) return (404, "ไม่พบใบเบิกนี้ในบริษัท");
                if (owner != null && owner.Value == userId) return null;
                return await DenyKeysAsync(companyId, userId, rule,
                    access == Access.Write ? rule.WriteAnyOf : rule.ReadAnyOf, verb);
            }

            case AttachmentOwnerKind.PayrollRun:
            {
                // สลิปโอนเงินเดือน/ใบนำส่ง ปกส. มีเงินเดือนรายคน (PDPA ม.26) ⇒ ด่านความลับ Payroll ทั้งอ่านและเขียน
                // (ชุดเดียวกับ PayrollController.CheckPayrollAccessAsync) · เขียนต้องมี Payroll.Run เพิ่ม
                if (!await _sensitivity.CanViewAsync(companyId, userId, SensitivityKind.Payroll))
                    return (403, $"ไม่มีสิทธิ์{verb}ของ{rule.ModuleTh} (ต้องมีสิทธิ์ดูข้อมูลเงินเดือน — "
                        + PermissionKeys.PayrollView.Replace("perm:", "") + ")");
                if (access == Access.Read) return null;
                if (!await _db.PayrollRuns.AsNoTracking().AnyAsync(r => r.Id == entityId && r.CompanyId == companyId))
                    return (404, "ไม่พบรอบเงินเดือนนี้ในบริษัท");
                return await DenyKeysAsync(companyId, userId, rule, rule.WriteAnyOf, verb);
            }

            case AttachmentOwnerKind.JournalEntry:
            {
                var sens = await _db.JournalEntries.AsNoTracking()
                    .Where(j => j.Id == entityId && j.CompanyId == companyId)
                    .Select(j => (SensitivityKind?)j.Sensitivity)
                    .FirstOrDefaultAsync();
                if (sens == null && access == Access.Write) return (404, "ไม่พบใบสำคัญนี้ในบริษัท");
                if (sens is { } k && k != SensitivityKind.None
                    && !await _sensitivity.CanViewAsync(companyId, userId, k))
                    return (403, $"ไม่มีสิทธิ์{verb}ของใบสำคัญที่เป็นข้อมูลลับ ({k})");
                if (access == Access.Read) return null;
                return await DenyKeysAsync(companyId, userId, rule, rule.WriteAnyOf, verb);
            }

            default: // AttachmentOwnerKind.KeyGated
            {
                if (access == Access.Read)
                    return await DenyKeysAsync(companyId, userId, rule, rule.ReadAnyOf, verb);
                if (!(rule.AllowsUnsavedOwner && entityId == Guid.Empty)
                    && !await OwnerExistsAsync(rule.CanonicalType, companyId, entityId))
                    return (404, $"ไม่พบ{rule.ModuleTh}นี้ในบริษัท");
                return await DenyKeysAsync(companyId, userId, rule, rule.WriteAnyOf, verb);
            }
        }
    }

    /// <summary>ผ่านเมื่อมีคีย์อย่างน้อยหนึ่งตัว · รายการว่าง = ไม่มีเงื่อนไขคีย์ (ผ่าน)</summary>
    private async Task<(int Status, string Message)?> DenyKeysAsync(
        Guid companyId, Guid userId, AttachmentScopeRule rule, IReadOnlyList<string> anyOf, string verb)
    {
        if (anyOf.Count == 0) return null;
        foreach (var key in anyOf)
            if (await _permissions.HasPermissionAsync(companyId, userId, key)) return null;
        return (403, AttachmentPermissionScope.DeniedMessage(rule, verb, anyOf));
    }

    /// <summary>เจ้าของไฟล์มีอยู่จริงในบริษัทนี้ไหม (ชนิดที่ตัดสินด้วยคีย์) — กัน entityId มั่ว/ข้ามบริษัท ·
    /// ชนิดที่ไม่มีตารางให้ค้นตรงนี้ (แนบโดยระบบ) ⇒ ถือว่ามี (เส้นอัปโหลดจากหน้าเว็บไม่รับชนิดเหล่านั้นอยู่แล้ว ·
    /// เส้นลบใช้แถวไฟล์ที่มีอยู่จริง)</summary>
    private Task<bool> OwnerExistsAsync(string? canonicalType, Guid companyId, Guid entityId) => canonicalType switch
    {
        "Contact" => _db.Contacts.AsNoTracking().AnyAsync(x => x.Id == entityId && x.CompanyId == companyId),
        "Product" => _db.Products.AsNoTracking().AnyAsync(x => x.Id == entityId && x.CompanyId == companyId),
        "Project" => _db.Projects.AsNoTracking().AnyAsync(x => x.Id == entityId && x.CompanyId == companyId),
        "FixedAsset" => _db.FixedAssets.AsNoTracking().AnyAsync(x => x.Id == entityId && x.CompanyId == companyId),
        "WhtCredit" => _db.WhtCreditsReceived.AsNoTracking().AnyAsync(x => x.Id == entityId && x.CompanyId == companyId),
        _ => Task.FromResult(true),
    };

    /// <summary>ด่านของเอกสาร (รวมเจ้าของที่เป็นการชำระของเอกสาร) —
    /// เขียน = สิทธิ์ "สร้าง <b>หรือ</b> อนุมัติ" ชนิดนั้น (<see cref="DocumentPermissionHelper"/>) — คนทำเอกสารและคนอนุมัติ
    /// ต้องแนบหลักฐานได้ · อ่าน = ฝั่งเอกสารที่ผู้ใช้มองเห็น (<see cref="DocumentPermissionHelper.VisibleDirectionsAsync"/>)
    /// · ทั้งสองทิศต้องผ่านชั้นความลับของใบ (โบนัสผู้บริหาร/เงินเดือน)</summary>
    private async Task<(int Status, string Message)?> DenyDocAsync(
        Guid companyId, Guid documentId, Access access, string verb, AttachmentScopeRule rule)
    {
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => new { d.DocumentType, d.Sensitivity })
            .FirstOrDefaultAsync();
        if (doc == null)
        {
            // อ่าน: ไฟล์ถูกกรองตามบริษัทอยู่แล้ว และเอกสารที่ถูกลบไม่มีด่านให้เทียบ ⇒ ไม่เพิ่มเงื่อนไข
            if (access == Access.Write) return (404, "ไม่พบเอกสารนี้ในบริษัท");
            return null;
        }
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (doc.Sensitivity != SensitivityKind.None
            && !await _sensitivity.CanViewAsync(companyId, userId, doc.Sensitivity))
            return (403, $"ไม่มีสิทธิ์{verb}ของเอกสารที่เป็นข้อมูลลับ ({doc.Sensitivity})");
        if (access == Access.Read)
        {
            var vis = await DocumentPermissionHelper.VisibleDirectionsAsync(_permissions, companyId, userId);
            if (vis.Allows(doc.DocumentType)) return null;
            return (403, $"ไม่มีสิทธิ์{verb}ของเอกสาร {doc.DocumentType} (ต้องมีสิทธิ์ดูเอกสาร"
                + (DocumentPermissionHelper.IsRevenue(doc.DocumentType) ? "ฝั่งรายรับ" : "ฝั่งรายจ่าย") + ")");
        }
        if (await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userId, doc.DocumentType)
            || await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, doc.DocumentType))
            return null;
        return (403, $"ไม่มีสิทธิ์{verb}ของ{rule.ModuleTh} {doc.DocumentType} (ต้องมีสิทธิ์สร้างหรืออนุมัติเอกสารประเภทนี้)");
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
        var deny = await DenyAttachmentAsync(companyId, entityType, entityId, Access.Write, "แนบไฟล์");
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
        var deny = await DenyAttachmentAsync(companyId, entityType, entityId, Access.Read, "ดูไฟล์แนบ");
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
        var deny = await DenyAttachmentAsync(companyId, att.EntityType, att.EntityId, Access.Write, "ลบไฟล์แนบ");
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
        var deny = await DenyAttachmentAsync(companyId, attachment.EntityType, attachment.EntityId, Access.Read, "ดาวน์โหลดไฟล์แนบ");
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

using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **ด่านของไฟล์แนบทุกชนิด ทุกทางเข้า** — คืน <c>null</c> = ผ่าน · คืน <see cref="AttachmentDenial"/> = ปฏิเสธ
///
/// <para>═══ ที่มา ═══ รอบ 190 (ข้อ 7) ใส่ด่านให้ <c>"Document"</c> ชนิดเดียว · รอบ 193 U2 (คำตัดสินเจ้าของข้อ 29):
/// <b>ทุกชนิดผูกกับสิทธิ์ของโมดูลนั้น</b> ทั้งแนบ/ลบ และดูรายการ/ดาวน์โหลด แต่ด่านอยู่เป็นเมธอด private ใน
/// <c>FileAttachmentController</c> · ฝ่ายค้านรอบ 193 พบทางเข้าอื่นที่แตะไฟล์ชุดเดียวกันแต่ไม่มีด่าน (C2 ใบเสร็จนำส่ง ·
/// C3 รูปสแกนที่ย้ายไปเป็นของเอกสารแล้ว) ⇒ ย้ายมาเป็น service ตัวเดียว (S2) · ตาราง "ชนิด → คีย์" ยังอยู่ที่
/// <see cref="AttachmentPermissionScope"/> ตัวเดียว</para>
///
/// <para>ชนิดที่ไม่รู้จัก: <b>ห้ามเขียน</b> (ทิศปลอดภัย) · อ่านได้ระดับสมาชิก (พฤติกรรมเดิม — ไฟล์ถูกกรอง CompanyId อยู่แล้ว)
/// · เอกสาร<b>ไม่ดูสถานะ</b>: การแนบไฟล์ไม่แก้เลขที่/ยอด/JE จึงทำได้ทุกสถานะ (หลักฐานมักมาถึงทีหลัง) ·
/// ใบเบิก<b>ดูสถานะ</b>สำหรับผู้ยื่น (<see cref="ExpenseClaimEvidencePolicy"/>)</para>
/// </summary>
public class AttachmentAccessGate : IAttachmentAccessGate
{
    private readonly AccountingDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly ISensitivityService _sensitivity;

    public AttachmentAccessGate(AccountingDbContext db, IPermissionService permissions, ISensitivityService sensitivity)
    {
        _db = db;
        _permissions = permissions;
        _sensitivity = sensitivity;
    }

    public async Task<AttachmentDenial?> DenyAttachmentAsync(Guid companyId, Guid userId, string? entityType,
        Guid entityId, AttachmentAccess access, string verb, Guid? attachmentId = null)
    {
        var rule = AttachmentPermissionScope.Resolve(entityType);
        if (!rule.IsKnown)
        {
            if (access == AttachmentAccess.Write) return new(403, AttachmentPermissionScope.UnknownTypeMessage(entityType));
            return null;
        }

        // ═══ ถังไฟล์ก่อนบันทึก (เจ้าของว่าง) — แยกตามผู้อัปโหลด (ฝ่ายค้านรอบ 193 · P7) ═══
        // เดิมทุกไฟล์ของทุกคนทั้งบริษัทกองที่ WhtCredit/0000… และ GET รายการคืนทั้งถังให้สมาชิกทุกคน
        if (access == AttachmentAccess.Read && AttachmentPermissionScope.IsUnsavedBucket(rule, entityId))
        {
            if (attachmentId is not { } fid)
                return new(403, AttachmentPermissionScope.UnsavedFileDeniedMessage);
            var uploader = await _db.FileAttachments.AsNoTracking()
                .Where(f => f.Id == fid && f.CompanyId == companyId)
                .Select(f => (Guid?)f.UploadedByUserId)
                .FirstOrDefaultAsync();
            if (uploader is { } u && AttachmentPermissionScope.UnsavedFileVisible(u, userId)) return null;
            return new(403, AttachmentPermissionScope.UnsavedFileDeniedMessage);
        }

        switch (rule.Kind)
        {
            case AttachmentOwnerKind.Document:
                return await DenyDocAsync(companyId, userId, entityId, access, verb, rule);

            case AttachmentOwnerKind.Payment:
            {
                // การชำระ = ด่านของเอกสารที่ถูกชำระ (ชุดเดียวกับ CreatePayment/VoidPayment ใน DocumentController)
                var docId = await _db.Payments.AsNoTracking()
                    .Where(p => p.Id == entityId && p.CompanyId == companyId)
                    .Select(p => (Guid?)p.DocumentId)
                    .FirstOrDefaultAsync();
                if (docId == null)
                {
                    if (access == AttachmentAccess.Write) return new(404, "ไม่พบรายการชำระเงินนี้ในบริษัท");
                    return null;
                }
                return await DenyDocAsync(companyId, userId, docId.Value, access, verb, rule);
            }

            case AttachmentOwnerKind.ExpenseClaim:
            {
                // เจ้าของใบเบิกดูหลักฐานของตัวเองได้เสมอ · แนบ/ถอดเองได้เฉพาะก่อนอนุมัติ (P1 — กันสลับใบเสร็จหลังผู้อนุมัติ
                // ตรวจแล้ว) · ของคนอื่น หรือหลังอนุมัติ ต้องเป็นผู้ตรวจ (HR.Admin / Expense.Approve — ชุดเดียวกับ
                // ExpenseClaimController.GetAll)
                var claim = await _db.ExpenseClaims.AsNoTracking()
                    .Where(c => c.Id == entityId && c.CompanyId == companyId)
                    .Select(c => new { c.SubmittedByUserId, c.Status })
                    .FirstOrDefaultAsync();
                if (claim == null && access == AttachmentAccess.Write) return new(404, "ไม่พบใบเบิกนี้ในบริษัท");
                var isOwner = claim != null && claim.SubmittedByUserId == userId;
                if (isOwner && access == AttachmentAccess.Read) return null;
                if (isOwner && ExpenseClaimEvidencePolicy.OwnerMayChange(claim!.Status)) return null;

                var keys = access == AttachmentAccess.Write ? rule.WriteAnyOf : rule.ReadAnyOf;
                var denied = await DenyKeysAsync(companyId, userId, rule, keys, verb);
                if (denied != null && isOwner)
                    return new(403, ExpenseClaimEvidencePolicy.LockedMessage(claim!.Status, verb));
                return denied;
            }

            case AttachmentOwnerKind.PayrollRun:
            {
                // สลิปโอนเงินเดือน/ใบนำส่ง ปกส. มีเงินเดือนรายคน (PDPA ม.26) ⇒ ด่านความลับ Payroll ทั้งอ่านและเขียน
                // (ชุดเดียวกับ PayrollController.CheckPayrollAccessAsync) · เขียนต้องมี Payroll.Run เพิ่ม
                if (!await _sensitivity.CanViewAsync(companyId, userId, SensitivityKind.Payroll))
                    return new(403, $"ไม่มีสิทธิ์{verb}ของ{rule.ModuleTh} (ต้องมีสิทธิ์ดูข้อมูลเงินเดือน — "
                        + PermissionKeys.PayrollView.Replace("perm:", "") + ")");
                if (access == AttachmentAccess.Read) return null;
                if (!await _db.PayrollRuns.AsNoTracking().AnyAsync(r => r.Id == entityId && r.CompanyId == companyId))
                    return new(404, "ไม่พบรอบเงินเดือนนี้ในบริษัท");
                return await DenyKeysAsync(companyId, userId, rule, rule.WriteAnyOf, verb);
            }

            case AttachmentOwnerKind.JournalEntry:
            {
                var sens = await _db.JournalEntries.AsNoTracking()
                    .Where(j => j.Id == entityId && j.CompanyId == companyId)
                    .Select(j => (SensitivityKind?)j.Sensitivity)
                    .FirstOrDefaultAsync();
                if (sens == null && access == AttachmentAccess.Write) return new(404, "ไม่พบใบสำคัญนี้ในบริษัท");
                if (sens is { } k && k != SensitivityKind.None
                    && !await _sensitivity.CanViewAsync(companyId, userId, k))
                    return new(403, $"ไม่มีสิทธิ์{verb}ของใบสำคัญที่เป็นข้อมูลลับ ({k})");
                if (access == AttachmentAccess.Read) return null;
                return await DenyKeysAsync(companyId, userId, rule, rule.WriteAnyOf, verb);
            }

            case AttachmentOwnerKind.OcrScan:
            {
                // ไฟล์ของสแกน: EntityId ของแถวไฟล์เป็น Guid สุ่ม (ไม่ใช่ scanId) ⇒ หาสแกนจาก FileAttachmentId
                var fileIds = attachmentId is { } one
                    ? new List<Guid> { one }
                    : await _db.FileAttachments.AsNoTracking()
                        .Where(f => f.CompanyId == companyId && f.EntityType == "OcrScan" && f.EntityId == entityId)
                        .Select(f => f.Id)
                        .ToListAsync();
                var scanId = fileIds.Count == 0 ? (Guid?)null : await _db.OcrScanResults.AsNoTracking()
                    .Where(s => s.CompanyId == companyId && s.FileAttachmentId != null && fileIds.Contains(s.FileAttachmentId.Value))
                    .Select(s => (Guid?)s.Id)
                    .FirstOrDefaultAsync();
                if (scanId is { } sid) return await DenyScanCoreAsync(companyId, userId, sid, access, verb, rule);
                return await DenyKeysAsync(companyId, userId, rule,
                    access == AttachmentAccess.Write ? rule.WriteAnyOf : rule.ReadAnyOf, verb);
            }

            default: // AttachmentOwnerKind.KeyGated
            {
                if (access == AttachmentAccess.Read)
                    return await DenyKeysAsync(companyId, userId, rule, rule.ReadAnyOf, verb);
                if (!AttachmentPermissionScope.IsUnsavedBucket(rule, entityId)
                    && !await OwnerExistsAsync(rule.CanonicalType, companyId, entityId))
                    return new(404, $"ไม่พบ{rule.ModuleTh}นี้ในบริษัท");
                return await DenyKeysAsync(companyId, userId, rule, rule.WriteAnyOf, verb);
            }
        }
    }

    public Task<AttachmentDenial?> DenyScanAsync(Guid companyId, Guid userId, Guid scanId, AttachmentAccess access, string verb)
        => DenyScanCoreAsync(companyId, userId, scanId, access, verb, AttachmentPermissionScope.Resolve("OcrScan"));

    private async Task<AttachmentDenial?> DenyScanCoreAsync(Guid companyId, Guid userId, Guid scanId,
        AttachmentAccess access, string verb, AttachmentScopeRule ocrRule)
    {
        var scan = await _db.OcrScanResults.AsNoTracking()
            .Where(s => s.Id == scanId && s.CompanyId == companyId)
            .Select(s => new { s.FileAttachmentId, s.CreatedDocumentId })
            .FirstOrDefaultAsync();
        if (scan == null) return null;   // ไม่พบสแกน — เส้นนั้นตอบ 404 เอง

        var file = scan.FileAttachmentId is { } fid
            ? await _db.FileAttachments.AsNoTracking()
                .Where(f => f.Id == fid && f.CompanyId == companyId)
                .Select(f => new { f.EntityType, f.EntityId })
                .FirstOrDefaultAsync()
            : null;
        var createdAlive = scan.CreatedDocumentId is { } createdId
            && await _db.Documents.AsNoTracking().AnyAsync(x => x.Id == createdId && x.CompanyId == companyId);

        var ownerDocId = AttachmentPermissionScope.ScanFileOwner(file?.EntityType, file?.EntityId,
            scan.CreatedDocumentId, createdAlive);
        if (ownerDocId is { } docId)
            return await DenyDocAsync(companyId, userId, docId, access, verb, AttachmentPermissionScope.Resolve("Document"));
        return await DenyKeysAsync(companyId, userId, ocrRule,
            access == AttachmentAccess.Write ? ocrRule.WriteAnyOf : ocrRule.ReadAnyOf, verb);
    }

    public async Task<HashSet<Guid>> HiddenScanIdsAsync(Guid companyId, Guid userId, IReadOnlyCollection<Guid> scanIds)
    {
        var hidden = new HashSet<Guid>();
        if (scanIds.Count == 0) return hidden;

        var scans = await _db.OcrScanResults.AsNoTracking()
            .Where(s => s.CompanyId == companyId && scanIds.Contains(s.Id))
            .Select(s => new { s.Id, s.FileAttachmentId, s.CreatedDocumentId })
            .ToListAsync();
        var fileIds = scans.Where(s => s.FileAttachmentId != null).Select(s => s.FileAttachmentId!.Value).Distinct().ToList();
        var files = fileIds.Count == 0
            ? new Dictionary<Guid, (string EntityType, Guid EntityId)>()
            : (await _db.FileAttachments.AsNoTracking()
                .Where(f => f.CompanyId == companyId && fileIds.Contains(f.Id))
                .Select(f => new { f.Id, f.EntityType, f.EntityId })
                .ToListAsync())
                .ToDictionary(f => f.Id, f => (f.EntityType, f.EntityId));

        var candidateDocIds = scans.Where(s => s.CreatedDocumentId != null).Select(s => s.CreatedDocumentId!.Value)
            .Concat(files.Values.Where(f => string.Equals(f.EntityType, "Document", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.EntityId))
            .Distinct().ToList();
        if (candidateDocIds.Count == 0) return hidden;   // ไม่มีใบไหนผูกเอกสาร — ด่าน OCR อ่านได้ระดับสมาชิก

        var docs = (await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && candidateDocIds.Contains(d.Id))
                .Select(d => new { d.Id, d.DocumentType, d.Sensitivity })
                .ToListAsync())
            .ToDictionary(d => d.Id);

        var vis = await DocumentPermissionHelper.VisibleDirectionsAsync(_permissions, companyId, userId);
        var kinds = await _sensitivity.GetVisibleKindsAsync(companyId, userId);
        foreach (var row in scans)
        {
            (string EntityType, Guid EntityId)? file = row.FileAttachmentId is { } rowFileId
                && files.TryGetValue(rowFileId, out var found) ? found : null;
            var createdAlive = row.CreatedDocumentId is { } createdId && docs.ContainsKey(createdId);
            var owner = AttachmentPermissionScope.ScanFileOwner(file?.EntityType, file?.EntityId,
                row.CreatedDocumentId, createdAlive);
            if (owner is not { } ownerId || !docs.TryGetValue(ownerId, out var doc)) continue;   // เอกสารถูกลบ = ไม่มีด่านให้เทียบ
            if (!AttachmentPermissionScope.DocumentReadable(vis, doc.DocumentType, doc.Sensitivity, kinds))
                hidden.Add(row.Id);
        }
        return hidden;
    }

    /// <summary>ผ่านเมื่อมีคีย์อย่างน้อยหนึ่งตัว · รายการว่าง = ไม่มีเงื่อนไขคีย์ (ผ่าน)</summary>
    private async Task<AttachmentDenial?> DenyKeysAsync(
        Guid companyId, Guid userId, AttachmentScopeRule rule, IReadOnlyList<string> anyOf, string verb)
    {
        if (anyOf.Count == 0) return null;
        foreach (var key in anyOf)
            if (await _permissions.HasPermissionAsync(companyId, userId, key)) return null;
        return new(403, AttachmentPermissionScope.DeniedMessage(rule, verb, anyOf));
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
        // ใบเสร็จนำส่ง (C2) — เดิมเขียนไฟล์ก่อนแล้วค่อยพบว่า remittance ไม่ใช่ของบริษัทนี้ (ไฟล์กำพร้าค้างในตาราง)
        "StatutoryRemittance" => _db.StatutoryRemittances.AsNoTracking()
            .AnyAsync(x => x.Id == entityId && x.CompanyId == companyId && !x.IsDeleted),
        _ => Task.FromResult(true),
    };

    /// <summary>ด่านของเอกสาร (รวมเจ้าของที่เป็นการชำระของเอกสาร และสแกนที่ผูกเอกสารแล้ว) —
    /// เขียน = สิทธิ์ "สร้าง <b>หรือ</b> อนุมัติ" ชนิดนั้น (<see cref="DocumentPermissionHelper"/>) — คนทำเอกสารและคนอนุมัติ
    /// ต้องแนบหลักฐานได้ · อ่าน = ฝั่งเอกสารที่ผู้ใช้มองเห็น (<see cref="DocumentPermissionHelper.VisibleDirectionsAsync"/>)
    /// · ทั้งสองทิศต้องผ่านชั้นความลับของใบ (โบนัสผู้บริหาร/เงินเดือน)</summary>
    private async Task<AttachmentDenial?> DenyDocAsync(
        Guid companyId, Guid userId, Guid documentId, AttachmentAccess access, string verb, AttachmentScopeRule rule)
    {
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => new { d.DocumentType, d.Sensitivity })
            .FirstOrDefaultAsync();
        if (doc == null)
        {
            // อ่าน: ไฟล์ถูกกรองตามบริษัทอยู่แล้ว และเอกสารที่ถูกลบไม่มีด่านให้เทียบ ⇒ ไม่เพิ่มเงื่อนไข
            if (access == AttachmentAccess.Write) return new(404, "ไม่พบเอกสารนี้ในบริษัท");
            return null;
        }
        if (doc.Sensitivity != SensitivityKind.None
            && !await _sensitivity.CanViewAsync(companyId, userId, doc.Sensitivity))
            return new(403, $"ไม่มีสิทธิ์{verb}ของเอกสารที่เป็นข้อมูลลับ ({doc.Sensitivity})");
        if (access == AttachmentAccess.Read)
        {
            var vis = await DocumentPermissionHelper.VisibleDirectionsAsync(_permissions, companyId, userId);
            if (vis.Allows(doc.DocumentType)) return null;
            return new(403, $"ไม่มีสิทธิ์{verb}ของเอกสาร {doc.DocumentType} (ต้องมีสิทธิ์ดูเอกสาร"
                + (DocumentPermissionHelper.IsRevenue(doc.DocumentType) ? "ฝั่งรายรับ" : "ฝั่งรายจ่าย") + ")");
        }
        if (await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userId, doc.DocumentType)
            || await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, doc.DocumentType))
            return null;
        return new(403, $"ไม่มีสิทธิ์{verb}ของ{rule.ModuleTh} {doc.DocumentType} (ต้องมีสิทธิ์สร้างหรืออนุมัติเอกสารประเภทนี้)");
    }
}

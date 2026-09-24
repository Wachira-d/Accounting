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
/// C3 รูปสแกนที่ย้ายไปเป็นของเอกสารแล้ว) ⇒ ย้ายมาเป็น service ตัวเดียว (S2) · ฝ่ายค้านรอบถัดมา (S2-C1..C3) พบว่าเส้น
/// OCR อื่นยังเปลี่ยน "เจ้าของไฟล์" หรือคืนเนื้อหาสแกนโดยไม่เดินด่าน ⇒ สแกนตัดสินด้วย<b>เจ้าของไฟล์</b>เสมอ
/// (<see cref="AttachmentPermissionScope.ScanOwner"/>) · ตาราง "ชนิด → คีย์" อยู่ที่ <see cref="AttachmentPermissionScope"/></para>
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

        // ═══ ถังไฟล์ก่อนบันทึก (เจ้าของว่าง) — รอบ 193 S2 (P7) + ฝ่ายค้าน (S2-P6) ═══
        // เดิมทุกไฟล์ของทุกคนทั้งบริษัทกองที่ WhtCredit/0000… และ GET รายการคืนทั้งถังให้สมาชิกทุกคน ·
        // ผู้ถือคีย์ของโมดูล (Tax.File · Owner) เปิดดู/ดูรายการได้ (เก็บกวาดไฟล์ค้าง) · ลบได้เฉพาะผู้อัปโหลด
        if (AttachmentPermissionScope.IsUnsavedBucket(rule, entityId))
        {
            var holdsKey = await HoldsAnyAsync(companyId, userId, rule.WriteAnyOf);
            if (access == AttachmentAccess.Read)
            {
                if (attachmentId is not { } fid)
                    return holdsKey ? null : new(403, AttachmentPermissionScope.UnsavedFileDeniedMessage);
                var uploader = await UploaderOfAsync(companyId, fid);
                if (uploader is { } u && AttachmentPermissionScope.UnsavedFileVisible(u, userId, holdsKey)) return null;
                return new(403, AttachmentPermissionScope.UnsavedFileDeniedMessage);
            }
            if (!holdsKey) return new(403, AttachmentPermissionScope.DeniedMessage(rule, verb, rule.WriteAnyOf));
            if (attachmentId is { } delId)   // ลบ (Delete ส่ง id ของไฟล์มา) — อัปโหลดไม่มี id
            {
                var uploader = await UploaderOfAsync(companyId, delId);
                if (uploader is not { } u || !AttachmentPermissionScope.UnsavedFileRemovable(u, userId))
                    return new(403, AttachmentPermissionScope.UnsavedFileNotRemovableMessage);
            }
            return null;
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
                // เจ้าของใบเบิกดูหลักฐานของตัวเองได้เสมอ · แนบ/ถอดเองตามสถานะใบ (ExpenseClaimEvidencePolicy — Draft ได้ทั้งคู่ ·
                // Submitted เพิ่มได้ถอดไม่ได้ · หลังอนุมัติไม่ได้) · หลังล็อก เจ้าของใบใช้คีย์ผู้ตรวจของตัวเองแก้ใบตัวเองไม่ได้ (SoD) ·
                // ใบของคนอื่นต้องเป็นผู้ตรวจ (HR.Admin / Expense.Approve — ชุดเดียวกับ ExpenseClaimController.GetAll)
                var claim = await _db.ExpenseClaims.AsNoTracking()
                    .Where(c => c.Id == entityId && c.CompanyId == companyId)
                    .Select(c => new { c.SubmittedByUserId, c.Status })
                    .FirstOrDefaultAsync();
                if (claim == null && access == AttachmentAccess.Write) return new(404, "ไม่พบใบเบิกนี้ในบริษัท");
                var isOwner = claim != null && claim.SubmittedByUserId == userId;
                if (isOwner && access == AttachmentAccess.Read) return null;
                if (isOwner)
                {
                    // Delete ส่ง id ของไฟล์มา = ถอด · Upload ไม่มี id = เพิ่ม
                    var isRemoval = attachmentId != null;
                    if (ExpenseClaimEvidencePolicy.OwnerMayChange(claim!.Status, isRemoval)) return null;
                    if (!ExpenseClaimEvidencePolicy.ReviewerKeyApplies(isClaimOwner: true))
                        return new(403, ExpenseClaimEvidencePolicy.LockedMessage(claim.Status, verb));
                }
                return await DenyKeysAsync(companyId, userId, rule,
                    access == AttachmentAccess.Write ? rule.WriteAnyOf : rule.ReadAnyOf, verb);
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
                if (scanId is { } sid)
                    return await DenyScanCoreAsync(companyId, userId, sid, access, verb, rule, unlinkedEditIsMemberLevel: false);
                return await DenyKeysAsync(companyId, userId, rule,
                    access == AttachmentAccess.Write ? rule.WriteAnyOf : rule.ReadAnyOf, verb);
            }

            default: // AttachmentOwnerKind.KeyGated
            {
                if (access == AttachmentAccess.Read)
                    return await DenyKeysAsync(companyId, userId, rule, rule.ReadAnyOf, verb);
                if (!await OwnerExistsAsync(rule.CanonicalType, companyId, entityId))
                    return new(404, $"ไม่พบ{rule.ModuleTh}นี้ในบริษัท");
                return await DenyKeysAsync(companyId, userId, rule, rule.WriteAnyOf, verb);
            }
        }
    }

    public Task<AttachmentDenial?> DenyScanAsync(Guid companyId, Guid userId, Guid scanId, AttachmentAccess access,
        string verb, bool unlinkedEditIsMemberLevel = false)
        => DenyScanCoreAsync(companyId, userId, scanId, access, verb, AttachmentPermissionScope.Resolve("OcrScan"),
            unlinkedEditIsMemberLevel);

    /// <summary>ด่านของสแกนหนึ่งใบ — เจ้าของตัดสินด้วย <see cref="AttachmentPermissionScope.ScanOwner"/> ตัวเดียว:
    /// ไฟล์เป็นของรายการอื่น → ด่านของรายการนั้น (<see cref="DenyAttachmentAsync"/> — ครอบเอกสาร · สลิปเงินเดือน · ใบเบิก ·
    /// ถังก่อนบันทึก ฯลฯ) · สแกนชี้เอกสาร/JE ที่ยังอยู่ → ด่านเอกสาร/JE · ยังไม่ผูก → คีย์ของ OCR
    /// <para><paramref name="unlinkedEditIsMemberLevel"/>: การแก้ผลอ่านของสแกนที่<b>ยังไม่ผูก</b>จากหน้ารีวิว (แก้บรรทัด ·
    /// จับคู่ผู้ติดต่อ · ลบสแกนทิ้ง) เป็นพื้นที่ทำงานก่อนลงบัญชีที่สมาชิกทำได้มาตลอด (อัปโหลดสแกนก็ระดับสมาชิก) — ไม่ปิดเพิ่ม ·
    /// ลบไฟล์ OcrScan ผ่านหน้าไฟล์แนบยังใช้คีย์สร้างเอกสารตามตาราง U2</para></summary>
    private async Task<AttachmentDenial?> DenyScanCoreAsync(Guid companyId, Guid userId, Guid scanId,
        AttachmentAccess access, string verb, AttachmentScopeRule ocrRule, bool unlinkedEditIsMemberLevel)
    {
        var scan = await _db.OcrScanResults.AsNoTracking()
            .Where(s => s.Id == scanId && s.CompanyId == companyId)
            .Select(s => new { s.FileAttachmentId, s.CreatedDocumentId, s.CreatedJournalEntryId })
            .FirstOrDefaultAsync();
        if (scan == null) return null;   // ไม่พบสแกน — เส้นนั้นตอบ 404 เอง

        var file = scan.FileAttachmentId is { } fid
            ? await _db.FileAttachments.AsNoTracking()
                .Where(f => f.Id == fid && f.CompanyId == companyId)
                .Select(f => new { f.EntityType, f.EntityId })
                .FirstOrDefaultAsync()
            : null;
        // สแกนอื่นที่ใช้ไฟล์เดียวกัน (retry สร้างแถวใหม่ที่ชี้ไฟล์เดิม · สแกนซ้ำผ่าน POST ocr/scan/{fileId}) ⇒ ถ้าใบไหนผูกเอกสาร/JE
        // แล้ว ไฟล์นั้นคือหลักฐานของรายการนั้น — ทุกแถวที่ชี้ไฟล์เดียวกันต้องได้ด่านเดียวกัน (ไม่งั้นสแกนซ้ำ = ประตูหลัง)
        var siblings = scan.FileAttachmentId is { } sharedFid
            ? await _db.OcrScanResults.AsNoTracking()
                .Where(s => s.CompanyId == companyId && s.FileAttachmentId == sharedFid)
                .Select(s => new { s.CreatedDocumentId, s.CreatedJournalEntryId })
                .ToListAsync()
            : new[] { new { scan.CreatedDocumentId, scan.CreatedJournalEntryId } }.ToList();
        var docCandidates = siblings.Where(x => x.CreatedDocumentId != null).Select(x => x.CreatedDocumentId!.Value)
            .Prepend(scan.CreatedDocumentId ?? Guid.Empty).Where(x => x != Guid.Empty).Distinct().ToList();
        var jeCandidates = siblings.Where(x => x.CreatedJournalEntryId != null).Select(x => x.CreatedJournalEntryId!.Value)
            .Prepend(scan.CreatedJournalEntryId ?? Guid.Empty).Where(x => x != Guid.Empty).Distinct().ToList();
        var aliveDoc = docCandidates.Count == 0 ? (Guid?)null : await _db.Documents.AsNoTracking()
            .Where(x => x.CompanyId == companyId && docCandidates.Contains(x.Id))
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync();
        var aliveJe = jeCandidates.Count == 0 ? (Guid?)null : await _db.JournalEntries.AsNoTracking()
            .Where(x => x.CompanyId == companyId && jeCandidates.Contains(x.Id))
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync();

        var owner = AttachmentPermissionScope.ScanOwner(file?.EntityType, file?.EntityId,
            aliveDoc, aliveDoc != null, aliveJe, aliveJe != null);
        if (owner is { } o)
            // อ่านส่ง id ของไฟล์ไปด้วย (ถังก่อนบันทึกตรวจผู้อัปโหลด) · เขียนไม่ส่ง — การแก้สแกนไม่ใช่การถอดไฟล์ของเจ้าของ
            return await DenyAttachmentAsync(companyId, userId, o.EntityType, o.EntityId, access, verb,
                access == AttachmentAccess.Read ? scan.FileAttachmentId : null);
        var keys = access == AttachmentAccess.Write && !unlinkedEditIsMemberLevel ? ocrRule.WriteAnyOf : ocrRule.ReadAnyOf;
        return await DenyKeysAsync(companyId, userId, ocrRule, keys, verb);
    }

    public async Task<HashSet<Guid>> HiddenScanIdsAsync(Guid companyId, Guid userId, IReadOnlyCollection<Guid> scanIds)
    {
        var hidden = new HashSet<Guid>();
        if (scanIds.Count == 0) return hidden;

        var scans = await _db.OcrScanResults.AsNoTracking()
            .Where(s => s.CompanyId == companyId && scanIds.Contains(s.Id))
            .Select(s => new { s.Id, s.FileAttachmentId, s.CreatedDocumentId, s.CreatedJournalEntryId })
            .ToListAsync();
        var fileIds = scans.Where(s => s.FileAttachmentId != null).Select(s => s.FileAttachmentId!.Value).Distinct().ToList();
        // สแกนอื่นที่ใช้ไฟล์เดียวกัน (retry / สแกนซ้ำ) — ถ้าใบไหนผูกเอกสาร/JE แล้ว ทุกแถวของไฟล์นั้นได้ด่านเดียวกัน (ชุดเดียวกับ DenyScanCoreAsync)
        var siblingLinks = fileIds.Count == 0
            ? new List<(Guid FileId, Guid? DocId, Guid? JeId)>()
            : (await _db.OcrScanResults.AsNoTracking()
                .Where(s => s.CompanyId == companyId && s.FileAttachmentId != null && fileIds.Contains(s.FileAttachmentId.Value)
                    && (s.CreatedDocumentId != null || s.CreatedJournalEntryId != null))
                .Select(s => new { s.FileAttachmentId, s.CreatedDocumentId, s.CreatedJournalEntryId })
                .ToListAsync())
                .Select(s => (FileId: s.FileAttachmentId!.Value, DocId: s.CreatedDocumentId, JeId: s.CreatedJournalEntryId))
                .ToList();
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
            .Concat(siblingLinks.Where(x => x.DocId != null).Select(x => x.DocId!.Value))
            .Distinct().ToList();
        var docs = candidateDocIds.Count == 0
            ? new Dictionary<Guid, (DocumentType Type, SensitivityKind Sensitivity)>()
            : (await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && candidateDocIds.Contains(d.Id))
                .Select(d => new { d.Id, d.DocumentType, d.Sensitivity })
                .ToListAsync())
                .ToDictionary(d => d.Id, d => (Type: d.DocumentType, Sensitivity: d.Sensitivity));
        var jeIds = scans.Where(s => s.CreatedJournalEntryId != null).Select(s => s.CreatedJournalEntryId!.Value)
            .Concat(siblingLinks.Where(x => x.JeId != null).Select(x => x.JeId!.Value))
            .Distinct().ToList();
        var jes = jeIds.Count == 0
            ? new Dictionary<Guid, SensitivityKind>()
            : await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && jeIds.Contains(j.Id))
                .ToDictionaryAsync(j => j.Id, j => j.Sensitivity);

        DocumentVisibility? vis = null;
        HashSet<SensitivityKind>? kinds = null;
        foreach (var row in scans)
        {
            (string EntityType, Guid EntityId)? file = row.FileAttachmentId is { } rowFileId
                && files.TryGetValue(rowFileId, out var found) ? found : null;
            var sib = row.FileAttachmentId is { } sibFileId
                ? siblingLinks.Where(x => x.FileId == sibFileId).ToList()
                : new List<(Guid FileId, Guid? DocId, Guid? JeId)>();
            Guid? aliveDoc = row.CreatedDocumentId is { } createdId && docs.ContainsKey(createdId)
                ? createdId
                : sib.Where(x => x.DocId != null && docs.ContainsKey(x.DocId.Value)).Select(x => x.DocId).FirstOrDefault();
            Guid? aliveJe = row.CreatedJournalEntryId is { } rowJeId && jes.ContainsKey(rowJeId)
                ? rowJeId
                : sib.Where(x => x.JeId != null && jes.ContainsKey(x.JeId.Value)).Select(x => x.JeId).FirstOrDefault();
            var owner = AttachmentPermissionScope.ScanOwner(file?.EntityType, file?.EntityId,
                aliveDoc, aliveDoc != null, aliveJe, aliveJe != null);
            if (owner is not { } o) continue;   // ยังไม่ผูก — ด่าน OCR อ่านได้ระดับสมาชิก
            if (o.EntityType == "Document")
            {
                if (!docs.TryGetValue(o.EntityId, out var doc)) continue;   // เอกสารถูกลบ = ไม่มีด่านให้เทียบ
                vis ??= await DocumentPermissionHelper.VisibleDirectionsAsync(_permissions, companyId, userId);
                kinds ??= await _sensitivity.GetVisibleKindsAsync(companyId, userId);
                if (!AttachmentPermissionScope.DocumentReadable(vis.Value, doc.Type, doc.Sensitivity, kinds))
                    hidden.Add(row.Id);
            }
            else if (o.EntityType == "JournalEntry" && jes.TryGetValue(o.EntityId, out var jeSens))
            {
                kinds ??= await _sensitivity.GetVisibleKindsAsync(companyId, userId);
                if (jeSens != SensitivityKind.None && !kinds.Contains(jeSens)) hidden.Add(row.Id);
            }
            else if (await DenyAttachmentAsync(companyId, userId, o.EntityType, o.EntityId,
                         AttachmentAccess.Read, "ดูสแกน", row.FileAttachmentId) != null)
            {
                // ไฟล์ของรายการชนิดอื่น (สแกนที่สร้างจากไฟล์แนบเดิม — หายาก) ⇒ ตัดสินรายใบด้วยด่านเดียวกับไฟล์แนบ
                hidden.Add(row.Id);
            }
        }
        return hidden;
    }

    public async Task<AttachmentDenial?> DenyScanSourceAsync(Guid companyId, Guid userId, Guid fileAttachmentId)
    {
        var file = await _db.FileAttachments.AsNoTracking()
            .Where(f => f.Id == fileAttachmentId && f.CompanyId == companyId)
            .Select(f => new { f.EntityType, f.EntityId })
            .FirstOrDefaultAsync();
        if (file == null) return new(404, "ไม่พบไฟล์แนบนี้ในบริษัท");
        // ไฟล์ของรายการอื่น (สลิปเงินเดือน · ไฟล์แนบเอกสาร · ใบเบิก · ถัง 50 ทวิ ฯลฯ) ⇒ ต้องอ่านไฟล์นั้นได้ก่อนจึงจะสแกนได้
        // (ผลสแกนคืน RawText ของไฟล์ทั้งใบ = อ่านไฟล์) · สแกนใหม่จะได้ด่านของเจ้าของไฟล์ต่อเอง (ScanOwner)
        if (!string.Equals(file.EntityType, "OcrScan", StringComparison.OrdinalIgnoreCase))
            return await DenyAttachmentAsync(companyId, userId, file.EntityType, file.EntityId,
                AttachmentAccess.Read, "สแกนไฟล์แนบ", fileAttachmentId);
        // ไฟล์ของสแกนเดิม ⇒ ด่านของสแกนเดิม (รวมสแกนพี่น้องที่ผูกเอกสาร/JE แล้ว)
        var existing = await _db.OcrScanResults.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.FileAttachmentId == fileAttachmentId)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync();
        if (existing is { } sid)
            return await DenyScanAsync(companyId, userId, sid, AttachmentAccess.Read, "สแกนไฟล์ซ้ำ");
        return null;
    }

    public async Task<HashSet<Guid>> HiddenDocumentIdsAsync(Guid companyId, Guid userId, IReadOnlyCollection<Guid> documentIds)
    {
        var hidden = new HashSet<Guid>();
        if (documentIds.Count == 0) return hidden;
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && documentIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentType, d.Sensitivity })
            .ToListAsync();
        if (docs.Count == 0) return hidden;
        var vis = await DocumentPermissionHelper.VisibleDirectionsAsync(_permissions, companyId, userId);
        var kinds = await _sensitivity.GetVisibleKindsAsync(companyId, userId);
        foreach (var d in docs)
            if (!AttachmentPermissionScope.DocumentReadable(vis, d.DocumentType, d.Sensitivity, kinds))
                hidden.Add(d.Id);
        return hidden;
    }

    private async Task<bool> HoldsAnyAsync(Guid companyId, Guid userId, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
            if (await _permissions.HasPermissionAsync(companyId, userId, key)) return true;
        return false;
    }

    private Task<Guid?> UploaderOfAsync(Guid companyId, Guid fileId)
        => _db.FileAttachments.AsNoTracking()
            .Where(f => f.Id == fileId && f.CompanyId == companyId)
            .Select(f => (Guid?)f.UploadedByUserId)
            .FirstOrDefaultAsync();

    /// <summary>ผ่านเมื่อมีคีย์อย่างน้อยหนึ่งตัว · รายการว่าง = ไม่มีเงื่อนไขคีย์ (ผ่าน)</summary>
    private async Task<AttachmentDenial?> DenyKeysAsync(
        Guid companyId, Guid userId, AttachmentScopeRule rule, IReadOnlyList<string> anyOf, string verb)
    {
        if (anyOf.Count == 0) return null;
        if (await HoldsAnyAsync(companyId, userId, anyOf)) return null;
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

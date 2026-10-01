using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รายงาน "ผู้ติดต่อข้อมูลเสีย" — <b>อ่านอย่างเดียว ไม่แก้ข้อมูล</b> (รอบ 193 · คำตัดสินเจ้าของข้อ 19)
///
/// <para>สองกลุ่ม: (1) ที่อยู่ขึ้นต้น <c>/เลข</c> (เลขบ้านส่วนหน้าถูกตัด — บั๊กตัวอ่านก่อนรอบ 190)
/// (2) แถว<b>สำนักงานใหญ่</b>ที่ OCR สร้าง/แก้ แล้วอาจถูกที่อยู่ของใบสาขาทับ — ตัดสินด้วย
/// <see cref="ContactDataHygiene.JudgeHeadOffice"/> (ทะเบียนบอกรหัสไปรษณีย์ต่าง · หรือใบสาขาพิมพ์ที่อยู่เดียวกัน)</para>
/// <para>(3) <b>ผู้ติดต่อซ้ำ</b> — เลขภาษี + สาขาเดียวกันมากกว่าหนึ่งแถว (รอบ 200 · คำตัดสินเจ้าของข้อ 19 · K-5): ล็อกสร้างผู้ติดต่อจาก OCR
/// กันแถวซ้ำใหม่แล้ว แต่แถวซ้ำที่เกิดไปก่อนต้องให้คนรวมเอง (เครื่องมือรวมที่หน้าผู้ติดต่อ) — ไม่รวมอัตโนมัติ · ตัวจัดกลุ่ม
/// <see cref="ContactDataHygiene.DuplicateKeyGroups"/> (คีย์เดียวกับแถบเตือนหน้าผู้ติดต่อ)</para>
/// <para>(4) <b>แถวสาขาที่ OCR สร้างแล้วไม่มีอะไรอ้าง</b> (รอบ 201 ทีม OC · A-OC2 · team-K2 R4) — ผู้ใช้พิมพ์รหัสสาขาผิดในหน้ารีวิว ⇒ แถวของรหัสผิด
/// ค้างถาวรหลังแก้กลับ · รายงาน + ปุ่มลบ (soft) <b>ทีละแถว</b> ที่ตรวจซ้ำที่เซิร์ฟเวอร์ (<see cref="ContactDataHygiene.OrphanRetireBlock"/>) · ไม่ลบอัตโนมัติ ·
/// endpoint เดียวของไฟล์นี้ที่เขียนข้อมูล ⇒ ด่านสิทธิ์ <c>Contact.Edit</c> ชุดเดียวกับลบผู้ติดต่อที่หน้าผู้ติดต่อ</para>
/// <para>ทะเบียนเป็นเครือข่ายภายนอก (ช้า) ⇒ ตรวจได้ไม่เกิน <c>registryLimit</c> แถวต่อครั้ง (แคช 24 ชม.) ·
/// แถวที่ยังไม่ได้ตรวจนับแยกเป็น <c>headOfficeUnchecked</c> — "ยังไม่ได้ตรวจ" ≠ "ไม่มีปัญหา"</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/contact-hygiene")]
[Authorize]
public class ContactHygieneController : ControllerBase
{
    private const int MaxRegistryLimit = 50;
    private readonly AccountingDbContext _db;
    private readonly IDbdLookupService _dbd;
    private readonly ILogger<ContactHygieneController> _logger;
    private readonly IPermissionService _permissions;

    public ContactHygieneController(AccountingDbContext db, IDbdLookupService dbd, ILogger<ContactHygieneController> logger,
        IPermissionService permissions)
    {
        _db = db;
        _dbd = dbd;
        _logger = logger;
        _permissions = permissions;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(Guid companyId, [FromQuery] int registryLimit = 20,
        [FromQuery] int registryOffset = 0, CancellationToken ct = default)
    {
        registryLimit = Math.Clamp(registryLimit, 0, MaxRegistryLimit);
        registryOffset = Math.Max(0, registryOffset);

        var contacts = await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.Address != null && c.Address != "")
            .Select(c => new
            {
                c.Id, c.Name, c.TaxId, c.BranchCode, c.Address, c.BuildingNumber, c.PostalCode,
                c.CreatedBy, c.UpdatedBy, c.CreatedAt, c.UpdatedAt,
            })
            .ToListAsync(ct);

        // (1) ที่อยู่ถูกตัดหัว
        var truncated = contacts
            .Where(c => ContactDataHygiene.IsTruncatedAddress(c.Address) || ContactDataHygiene.IsTruncatedAddress(c.BuildingNumber))
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                contactId = c.Id, c.Name, c.TaxId, branchCode = c.BranchCode, c.Address,
                ocrManaged = ContactDataHygiene.IsOcrManaged(c.CreatedBy, c.UpdatedBy),
                c.CreatedBy, c.UpdatedBy,
                reason = "ที่อยู่ขึ้นต้นด้วย \"/เลข\" — เลขบ้านส่วนหน้าน่าจะหายไป (ตรวจกับใบจริงหรือกด DBD ที่หน้าผู้ติดต่อ)",
            })
            .ToList();

        // (2) แถวสำนักงานใหญ่ที่ OCR จัดการ + มีเลขผู้เสียภาษี 13 หลัก
        var hq = contacts
            .Where(c => TaxBranchCode.IsHeadOffice(c.BranchCode)
                && ContactDataHygiene.IsOcrManaged(c.CreatedBy, c.UpdatedBy)
                && ThaiTaxId.Normalize(c.TaxId) is { Length: 13 })
            .ToList();
        var hqIds = hq.Select(c => c.Id).ToList();
        var scans = hqIds.Count == 0
            ? new Dictionary<Guid, List<ContactScanEvidence>>()
            : (await _db.OcrScanResults.AsNoTracking()
                    .Where(s => s.CompanyId == companyId && s.MatchedContactId != null && hqIds.Contains(s.MatchedContactId.Value)
                        && s.VendorBranchCode != null && s.VendorBranchCode != "" && s.VendorBranchCode != "00000")
                    .Select(s => new { ContactId = s.MatchedContactId!.Value, s.VendorBranchCode, s.VendorAddress })
                    .ToListAsync(ct))
                .GroupBy(s => s.ContactId)
                .ToDictionary(g => g.Key, g => g.Select(s => new ContactScanEvidence(s.VendorBranchCode, s.VendorAddress)).ToList());

        // ตรวจทะเบียนเฉพาะแถวที่มีหลักฐานจากใบสาขาก่อน แล้วค่อยแถวที่แก้ล่าสุด
        var ordered = hq
            .OrderByDescending(c => scans.ContainsKey(c.Id))
            .ThenByDescending(c => c.UpdatedAt ?? c.CreatedAt)
            .ToList();
        var suspects = new List<object>();
        var registryOk = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            string? registryAddress = null;
            var registryChecked = false;
            // หน้าต่างของรอบนี้ [offset, offset + limit) — ลำดับแน่นอน ⇒ "ตรวจชุดถัดไป" ได้แถวใหม่จริง
            if (i >= registryOffset && i < registryOffset + registryLimit)
            {
                registryChecked = true;
                try { registryAddress = (await _dbd.GetByJuristicIdAsync(ThaiTaxId.Normalize(c.TaxId)))?.Address; }
                catch (Exception ex)
                {
                    registryChecked = false;
                    _logger.LogWarning(ex, "Contact hygiene: registry lookup failed for {TaxId}", c.TaxId);
                }
            }
            if (registryChecked) registryOk++;

            var verdict = ContactDataHygiene.JudgeHeadOffice(
                c.BranchCode, c.CreatedBy, c.UpdatedBy, c.Address, c.PostalCode, registryAddress,
                scans.TryGetValue(c.Id, out var ev) ? ev : null);
            if (!verdict.Suspect) continue;
            suspects.Add(new
            {
                contactId = c.Id, c.Name, c.TaxId, branchCode = c.BranchCode, c.Address,
                registryAddress, registryChecked,
                registryDiffers = verdict.RegistryDiffers,
                branchPaperCode = verdict.BranchPaperCode,
                branchPaperLabel = verdict.BranchPaperCode == null ? null : TaxBranchCode.Label(verdict.BranchPaperCode),
                c.CreatedBy, c.UpdatedBy,
                reason = verdict.Reason,
            });
        }

        // (3) ผู้ติดต่อซ้ำ (เลขภาษี + สาขา) — ทุกแถว ไม่เฉพาะที่มีที่อยู่
        var keyRows = await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.TaxId != null && c.TaxId != "")
            .Select(c => new ContactKeyRow(c.Id, c.Name, c.TaxId, c.BranchCode, c.CreatedBy, c.CreatedAt))
            .ToListAsync(ct);
        var duplicateGroups = ContactDataHygiene.DuplicateKeyGroups(keyRows)
            .Select(g => new
            {
                key = g.Key,
                taxId = g.TaxId,
                branchCode = g.BranchCode,
                branchLabel = TaxBranchCode.Label(g.BranchCode),
                ocrCreated = g.OcrCreated,
                contacts = g.Rows.Select(r => new
                {
                    contactId = r.Id, name = r.Name, createdBy = r.CreatedBy, createdAt = r.CreatedAt,
                    ocrCreated = ContactDataHygiene.IsOcrManaged(r.CreatedBy, null),
                }).ToList(),
            })
            .ToList();

        // (4) แถวสาขาที่ OCR สร้างแล้วไม่มีอะไรอ้าง (รอบ 201 ทีม OC · A-OC2)
        var ocrBranchRows = await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.CreatedBy == ContactDataHygiene.OcrBranchAutoCreateTag)
            .Select(c => new ContactKeyRow(c.Id, c.Name, c.TaxId, c.BranchCode, c.CreatedBy, c.CreatedAt))
            .ToListAsync(ct);
        var ocrBranchIds = ocrBranchRows.Select(r => r.Id).ToList();
        var referenced = ocrBranchIds.Count == 0 ? new HashSet<Guid>() : await ReferencedContactIdsAsync(companyId, ocrBranchIds, ct);
        var ocrBranchOrphans = ContactDataHygiene.OrphanOcrBranchRows(ocrBranchRows, referenced)
            .Select(r => new
            {
                contactId = r.Id, name = r.Name, taxId = r.TaxId, branchCode = r.BranchCode,
                branchLabel = TaxBranchCode.Label(r.BranchCode), createdAt = r.CreatedAt,
            })
            .ToList();

        return Ok(new ApiResponse<object>(true, new
        {
            ocrBranchOrphans,
            ocrBranchCreated = ocrBranchRows.Count,
            truncatedAddresses = truncated,
            duplicateKeyGroups = duplicateGroups,
            headOfficeSuspects = suspects,
            headOfficeCandidates = hq.Count,
            headOfficeRegistryChecked = registryOk,
            headOfficeUnchecked = hq.Count - registryOk,
            registryLimit,
            registryOffset,
            nextRegistryOffset = registryOffset + registryLimit < hq.Count ? registryOffset + registryLimit : (int?)null,
            note = "รายงานอย่างเดียว — ระบบไม่แก้ข้อมูลผู้ติดต่อให้ (เปิดผู้ติดต่อแล้วแก้ หรือกดดึงข้อมูลจาก DBD เอง) · ผู้ติดต่อซ้ำรวมที่หน้าผู้ติดต่อ (ไม่รวมอัตโนมัติ)",
        }));
    }

    /// <summary>
    /// **ลบ (soft) แถวสาขาที่ OCR สร้างแล้วไม่มีอะไรอ้าง — ทีละแถว** (รอบ 201 ทีม OC · A-OC2) · ด่านสิทธิ์ <c>Contact.Edit</c> (ชุดเดียวกับลบผู้ติดต่อ) ·
    /// ตรวจซ้ำที่เซิร์ฟเวอร์ทุกครั้ง (<see cref="ContactDataHygiene.OrphanRetireBlock"/>) — หน้ารายงานอาจค้างขณะมีเอกสารผูกแถวนี้ไปแล้ว ·
    /// soft = <c>IsDeleted</c> + <c>IsActive = false</c> (ไม่ลบจริง · audit chain บันทึกการเปลี่ยนจาก ChangeTracker)
    /// </summary>
    [HttpPost("ocr-branch-orphans/{contactId:guid}/retire")]
    public async Task<ActionResult<ApiResponse<object>>> RetireOcrBranchOrphan(Guid companyId, Guid contactId, CancellationToken ct = default)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        if (!await _permissions.HasPermissionAsync(companyId, uid, Models.Constants.PermissionKeys.ContactEdit))
            return StatusCode(403, new ApiResponse<object>(false, null,
                $"ไม่มีสิทธิ์ลบผู้ติดต่อ (ต้องการ {Models.Constants.PermissionKeys.ContactEdit.Replace("perm:", "")})"));
        var contact = await _db.Contacts
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == contactId, ct);
        if (contact == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบผู้ติดต่อ (อาจถูกลบไปแล้ว) — กดตรวจอีกครั้ง"));
        var stillReferenced = (await ReferencedContactIdsAsync(companyId, new List<Guid> { contactId }, ct)).Contains(contactId);
        if (ContactDataHygiene.OrphanRetireBlock(contact.CreatedBy, stillReferenced) is string block)
            return BadRequest(new ApiResponse<object>(false, null, block));
        contact.IsDeleted = true;
        contact.IsActive = false;
        contact.UpdatedBy = "contact-hygiene";
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, new { contactId },
            $"ลบแถว {contact.Name} ({TaxBranchCode.Label(contact.BranchCode)}) แล้ว — ไม่มีเอกสาร/สแกนอ้างถึง"));
    }

    /// <summary>id ผู้ติดต่อ (ในชุดที่ถาม) ที่มีข้อมูลอ้างถึง — เอกสาร · สแกน (ผูก/AI เสนอ) · 50 ทวิ · เครดิตภาษีถูกหัก · alias สินค้า · รายการประจำ ·
    /// ทุก query กรอง <c>CompanyId</c> · นับแถวที่ลบแล้วด้วย (ห้ามลบผู้ติดต่อใต้ประวัติ — ทิศปลอดภัย)</summary>
    private async Task<HashSet<Guid>> ReferencedContactIdsAsync(Guid companyId, List<Guid> ids, CancellationToken ct)
    {
        var refs = new HashSet<Guid>();
        refs.UnionWith(await _db.Documents.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.CompanyId == companyId && ids.Contains(d.ContactId)).Select(d => d.ContactId).Distinct().ToListAsync(ct));
        refs.UnionWith(await _db.OcrScanResults.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.MatchedContactId != null && ids.Contains(s.MatchedContactId.Value))
            .Select(s => s.MatchedContactId!.Value).Distinct().ToListAsync(ct));
        refs.UnionWith(await _db.OcrScanResults.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.AiSuggestedContactId != null && ids.Contains(s.AiSuggestedContactId.Value))
            .Select(s => s.AiSuggestedContactId!.Value).Distinct().ToListAsync(ct));
        refs.UnionWith(await _db.WithholdingTaxCerts.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.CompanyId == companyId && ids.Contains(w.PayeeContactId))
            .Select(w => w.PayeeContactId).Distinct().ToListAsync(ct));
        refs.UnionWith(await _db.WhtCreditsReceived.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.PayerContactId != null && ids.Contains(w.PayerContactId.Value))
            .Select(w => w.PayerContactId!.Value).Distinct().ToListAsync(ct));
        refs.UnionWith(await _db.ProductAliases.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.ContactId != null && ids.Contains(a.ContactId.Value))
            .Select(a => a.ContactId!.Value).Distinct().ToListAsync(ct));
        refs.UnionWith(await _db.RecurringTransactions.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.ContactId != null && ids.Contains(r.ContactId.Value))
            .Select(r => r.ContactId!.Value).Distinct().ToListAsync(ct));
        return refs;
    }
}

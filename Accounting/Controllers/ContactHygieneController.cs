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

    public ContactHygieneController(AccountingDbContext db, IDbdLookupService dbd, ILogger<ContactHygieneController> logger)
    {
        _db = db;
        _dbd = dbd;
        _logger = logger;
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

        return Ok(new ApiResponse<object>(true, new
        {
            truncatedAddresses = truncated,
            headOfficeSuspects = suspects,
            headOfficeCandidates = hq.Count,
            headOfficeRegistryChecked = registryOk,
            headOfficeUnchecked = hq.Count - registryOk,
            registryLimit,
            registryOffset,
            nextRegistryOffset = registryOffset + registryLimit < hq.Count ? registryOffset + registryLimit : (int?)null,
            note = "รายงานอย่างเดียว — ระบบไม่แก้ข้อมูลผู้ติดต่อให้ (เปิดผู้ติดต่อแล้วแก้ หรือกดดึงข้อมูลจาก DBD เอง)",
        }));
    }
}

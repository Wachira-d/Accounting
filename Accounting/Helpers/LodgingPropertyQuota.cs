using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ด่าน "ที่พักหลายแห่ง" ตัวเดียว — ที่พักแห่งแรกใช้ฟรี (มากับเว็บ Hotel template) · แห่งที่ 2 ขึ้นไปต้องเปิด add-on
/// <c>lodging.multi-property</c> (LODGING_LICENSING_PLAN §5 — hard block ได้เพราะเป็น "ของใหม่ที่ยังไม่เคยเปิด" ไม่กระทบงานที่ทำอยู่)
///
/// <para>ที่มา (รอบ 202 ทีม LW · W-05 · คำตัดสินข้อ 118): ด่านนี้เคยอยู่ inline ใน <c>LodgingService.CreatePropertyAsync</c> เท่านั้น ⇒
/// "สร้างเว็บที่พัก/เติมเทมเพลต" (<c>LodgingSeeder</c>) สร้างที่พักแห่งที่สองยึดเว็บได้โดยไม่ผ่านด่าน (ทางเข้าอื่นไม่เดินด่าน — R5) ·
/// ทั้งสองทางเข้าต้องเรียกตัวนี้ (ล็อกจุดเรียกด้วย <c>tools/required_call_site_check.py</c>)</para></summary>
public static class LodgingPropertyQuota
{
    /// <summary>RuleCode ที่แนบไปกับ BusinessRuleException (หน้าเว็บอ่านเพื่อพาไปหน้าส่วนเสริม)</summary>
    public const string RuleCode = "ADDON-REQUIRED:" + AddOnCodes.LodgingMultiProperty;

    /// <summary>null = สร้างที่พักเพิ่มได้ · ข้อความไทย = ถูกกัน (บอกทางไปต่อ) · <paramref name="entitlement"/> null = ไม่มีตัวตรวจสิทธิ์ (เทสต์/ไม่ได้ลงทะเบียน) ⇒ ผ่าน
    /// ตามพฤติกรรมเดิมของ CreatePropertyAsync</summary>
    public static async Task<string?> BlockReasonAsync(AccountingDbContext db, IEntitlementService? entitlement, Guid companyId)
    {
        var existing = await db.LodgingProperties.CountAsync(x => x.CompanyId == companyId && !x.IsDeleted);
        if (existing < 1 || entitlement == null) return null;
        var ent = await entitlement.CheckAsync(companyId, AddOnCodes.LodgingMultiProperty);
        if (ent.Allowed) return null;
        return $"เปิดที่พักได้ 1 แห่งในแพ็กเกจปัจจุบัน — {ent.UpgradeHint ?? "เปิดส่วนเสริม \"ที่พักหลายแห่ง\""}"
            + " (ที่หน้า \"ส่วนเสริมของฉัน\")";
    }
}

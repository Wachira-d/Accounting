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
    /// <remarks>ฝ่ายค้านรอบ 229 S6: มี <paramref name="gate"/> ⇒ ตัดสินด้วยด่านโควตาตัวเดียว (override · เพดานแพ็กเกจ/ทดลองจริง · ที่ซื้อ · สวิตช์หลายแห่ง = ไม่จำกัด) ·
    /// ผู้เรียกอยู่ในธุรกรรม ⇒ ล็อกต่อ (บริษัท, ทรัพยากร) ก่อนนับ (สองคำขอพร้อมกันได้แค่หนึ่ง — ผู้เรียกต้อง insert ในธุรกรรมเดียวกัน) · null (เทสต์เดิม) = สูตรเดิมด้านล่าง</remarks>
    public static async Task<string?> BlockReasonAsync(AccountingDbContext db, IEntitlementService? entitlement, Guid companyId,
        Accounting.Services.Implementations.IQuotaGate? gate = null)
    {
        if (gate != null)
        {
            if (db.Database.CurrentTransaction != null) await gate.LockAsync(companyId, QuotaResources.LodgingProperties);
            var d = await gate.CheckAsync(companyId, QuotaResources.LodgingProperties, 1);
            if (d.Allowed) return null;
            var hint = entitlement == null ? null : (await entitlement.CheckAsync(companyId, AddOnCodes.LodgingMultiProperty)).UpgradeHint;
            return $"{d.BlockReason} · หรือ{hint ?? "เปิดส่วนเสริม \"ที่พักหลายแห่ง\""} (ที่หน้า \"ส่วนเสริมของฉัน\")";
        }
        var existing = await db.LodgingProperties.CountAsync(x => x.CompanyId == companyId && !x.IsDeleted);
        if (existing < 1 || entitlement == null) return null;
        // รอบ 229 (QUOTA_ADDON แพ็ก 5): จำนวนที่พักที่มีผล = override รายบริษัท ?? เพดานแพ็กเกจ (allowance) ?? 1 (แห่งแรกฟรี — พฤติกรรมเดิม)
        // + ที่พักที่ซื้อเพิ่ม (ตัวตัดสินเดียว QuotaLimitPolicy) · ส่วนเสริมสวิตช์ "ที่พักหลายแห่ง" ยังเปิดได้ไม่จำกัดตามเดิม
        var now = DateTime.UtcNow;
        var limit = QuotaLimitPolicy.Effective(
            await Accounting.Services.Implementations.QuotaStore.OverrideAsync(db, companyId, QuotaResources.LodgingProperties),
            await Accounting.Services.Implementations.QuotaStore.AllowanceAsync(db,
                await Accounting.Services.Implementations.QuotaStore.PlanTemplateIdAsync(db, companyId), QuotaResources.LodgingProperties),
            inTrial: false, legacyLimit: 1,
            await Accounting.Services.Implementations.QuotaStore.PurchasesAsync(db, companyId, QuotaResources.LodgingProperties), now);
        if (limit.Effective is not int max || existing + 1 <= max) return null;
        var ent = await entitlement.CheckAsync(companyId, AddOnCodes.LodgingMultiProperty);
        if (ent.Allowed) return null;
        return $"เปิดที่พักได้ {max:N0} แห่งในแพ็กเกจปัจจุบัน — {ent.UpgradeHint ?? "เปิดส่วนเสริม \"ที่พักหลายแห่ง\""}"
            + " (ที่หน้า \"ส่วนเสริมของฉัน\") หรือซื้อที่พักเพิ่มเป็นรายแห่งที่หน้า \"แพ็กเกจและการชำระเงิน\" · ที่พักเดิมใช้งานได้ตามปกติ";
    }
}

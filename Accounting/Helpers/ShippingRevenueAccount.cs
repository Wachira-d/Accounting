using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 228 (BOOKING_PAYMENT_AUDIT §8 "ยังเปิด" · §9) — <b>บัญชีรายได้ค่าจัดส่ง</b>ของเว็บ (<see cref="SiteCommerceConfig.ShippingRevenueAccountId"/>):
/// ตัวตรวจตอนบันทึก + ตัวอ่านเดียวของผู้ออกเอกสาร (ใบขายของคำสั่งซื้อ <c>CmsCommerceService.BuildOrderErpLines</c> ·
/// ใบกำกับ/ใบเสนอราคา/ใบส่วนที่เหลือของการจองแบบจัดส่ง <c>CmsBookingService.DeliveryErpLines</c>)
///
/// <para><b>ไม่ตั้ง (null)</b> = บรรทัดค่าจัดส่ง <c>AccountId: null</c> ⇒ บัญชีรายได้ตั้งต้นของเอกสาร (พฤติกรรมก่อนรอบ 228 ทุกตัวอักษร) ·
/// <b>ใบรับมัดจำไม่ใช้บัญชีนี้</b> (ลงเงินรับล่วงหน้า 217xx ผ่าน IsDeposit — รับรู้รายได้ตอนออกใบส่วนที่เหลือ/ตัดมัดจำ) ·
/// บัญชีที่ตั้งไว้ถูกปิด/ลบภายหลัง ⇒ ผู้อ่านคืน null (ไม่ส่งบัญชีที่ใช้ไม่ได้เข้าเอกสาร) + หน้าตั้งค่าแสดงคำเตือน <see cref="StaleWarning"/></para>
/// </summary>
public static class ShippingRevenueAccount
{
    public const string RuleCode = "CMS-SHIPPING-REVENUE-ACCOUNT";

    /// <summary>ตรวจบัญชีก่อนบันทึก (null = ผ่าน) — ของบริษัทนี้ · ไม่ถูกลบ · ใช้งานอยู่ · หมวดรายได้</summary>
    public static string? Problem(ChartOfAccount? account, Guid companyId)
    {
        if (account == null || account.CompanyId != companyId || account.IsDeleted) return "ไม่พบบัญชีที่เลือกในผังบัญชีของบริษัทนี้";
        if (!account.IsActive) return $"บัญชี {account.AccountCode} ปิดใช้งานอยู่ — เปิดใช้ก่อน หรือเลือกบัญชีอื่น";
        if (account.AccountType != AccountType.Revenue)
            return $"บัญชี {account.AccountCode} ไม่ใช่หมวดรายได้ — ค่าจัดส่งที่เรียกเก็บจากลูกค้าเป็นรายได้ (เลือกบัญชีหมวด 4 เช่น รายได้ค่าขนส่ง)";
        return null;
    }

    /// <summary>ใช้ได้ไหม (ตัวเดียวกับ <see cref="Problem"/>)</summary>
    public static bool Usable(ChartOfAccount? account, Guid companyId) => Problem(account, companyId) == null;

    /// <summary>คำเตือนบนหน้าตั้งค่าเมื่อบัญชีที่ตั้งไว้ใช้ไม่ได้แล้ว (null = ไม่ต้องเตือน)</summary>
    public static string? StaleWarning(Guid? configuredId, ChartOfAccount? account, Guid companyId)
        => configuredId is null || Usable(account, companyId) ? null
            : "⚠️ บัญชีรายได้ค่าจัดส่งที่ตั้งไว้ใช้ไม่ได้แล้ว (" + (Problem(account, companyId) ?? "") + ") — เอกสารใหม่ลงบัญชีรายได้ตั้งต้นจนกว่าจะเลือกใหม่";

    /// <summary>บัญชีที่บรรทัดค่าจัดส่ง (ใบขาย/ใบกำกับ — ไม่ใช่ใบรับมัดจำ) ใช้ · null = บัญชีรายได้ตั้งต้นของเอกสาร</summary>
    public static async Task<Guid?> ResolveAsync(AccountingDbContext db, Guid companyId, Guid siteId, CancellationToken ct = default)
    {
        var id = await db.SiteCommerceConfigs.AsNoTracking()
            .Where(c => c.SiteId == siteId && c.CompanyId == companyId)
            .Select(c => c.ShippingRevenueAccountId).FirstOrDefaultAsync(ct);
        if (id is not Guid accountId) return null;
        var acc = await db.ChartOfAccounts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId, ct);
        return Usable(acc, companyId) ? accountId : null;
    }
}

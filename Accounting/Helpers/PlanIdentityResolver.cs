using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// "บริษัทนี้ใช้แพ็กเกจอะไร" (ระดับ · ชื่อ · สถานะ · วันหมดอายุ · ที่มา) — <b>ตัวตัดสินตัวเดียว</b>ของทุกจุดที่แสดงแพ็กเกจ (รอบ 205 ทีม I · ข้อ 3)
///
/// <para>═══ ที่มา (บั๊กจริง: หน้าจอโชว์ทั้ง Free และ Enterprise) ═══ แพ็กเกจมีสองชั้น — แถว <c>Subscription</c> ของบริษัท (ชั้นบริษัท ·
/// มักค้างเป็น FreeTrial ตั้งแต่สมัคร) กับ <c>AccountSubscription</c> (License ระดับผู้ใช้ ที่บริษัทผูกอยู่ผ่าน <c>Subscription.AccountSubscriptionId</c>) ·
/// หน้าแพ็กเกจ/ด่าน gate อ่าน overlay (License ชนะ) แต่แดชบอร์ด (<c>DashboardService</c>) และรายการบริษัท (<c>CompanyResponse.Subscription</c>)
/// อ่านแถวบริษัทตรง ๆ ⇒ บริษัทที่ผูก License Enterprise เห็น "Free" บนแดชบอร์ดและ "Enterprise" บนหน้าแพ็กเกจ</para>
///
/// <para>กติกา: บริษัทผูก License ที่ยังไม่ถูกลบ ⇒ ใช้ระดับ/ชื่อ/สถานะ/วันหมดอายุของ License (ที่มา <see cref="SourceLicense"/>) ·
/// ไม่ผูก ⇒ ของแถวบริษัท (<see cref="SourceCompany"/>) · ชื่อแพ็กเกจ = ชื่อ PlanTemplate ที่แอดมินตั้ง (ไม่แปล enum เอง) ·
/// ผู้เรียกที่ต้องใช้ฟีเจอร์/ลิมิตยังผ่าน <c>SubscriptionService</c> ตามเดิม — ตัวนี้ตอบเฉพาะ "ตัวตน" ของแพ็กเกจที่แสดงบนจอ</para>
/// </summary>
public static class PlanIdentityResolver
{
    public const string SourceCompany = "Company";
    public const string SourceLicense = "License";

    /// <summary>ฝั่ง License (null = บริษัทไม่ได้ผูก / License ถูกลบ)</summary>
    public sealed record LicenseSide(SubscriptionPlan Plan, string? PlanName, SubscriptionStatus Status, DateTime EndDate);

    public sealed record PlanIdentity(SubscriptionPlan Plan, string? PlanName, SubscriptionStatus Status, DateTime EndDate, string Source);

    /// <param name="companyPlanName">ชื่อ PlanTemplate ของระดับแพ็กเกจบริษัท (null = ไม่รู้ ⇒ หน้าเว็บแสดงชื่อ enum)</param>
    public static PlanIdentity Resolve(SubscriptionPlan companyPlan, SubscriptionStatus companyStatus, DateTime companyEndDate,
        string? companyPlanName, LicenseSide? license)
        => license is { } l
            ? new PlanIdentity(l.Plan, l.PlanName, l.Status, l.EndDate, SourceLicense)
            : new PlanIdentity(companyPlan, companyPlanName, companyStatus, companyEndDate, SourceCompany);
}

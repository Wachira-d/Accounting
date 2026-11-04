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
///
/// <para>═══ รอบ 236 (คำตัดสิน 236-1…236-6 · เจ้าของ: "ตกลงบริษัทนี้เป็น Enterprise หรือ Free — ใช้ข้อมูลที่ถูกต้องเพียงหนึ่งเดียว") ═══
/// บริษัท "เน็ก แอค" ผูก License Enterprise (ถึง 2593) แต่ป้ายหัวโมดัลแอดมิน/แท็บ Subscription/ดรอปดาวน์หน้าซื้อ อ่านแถวบริษัท (Free Edition · enum "FreeTrial")
/// และหน้าซื้อยอมให้จ่าย ฿10,000 ซื้อ Enterprise 12 เดือน — ทั้งที่แพ็กเกจระดับบริษัทที่ซื้อจะ<b>ไม่มีผล</b> (resolver ใช้ License) · ตอนนี้ตัวนี้ตอบเพิ่ม:</para>
/// <list type="bullet">
/// <item><see cref="LicenseActive"/> — สูตร "License ยังใช้งานได้" ตัวเดียว (ผู้เรียก: <c>SubscriptionService.GetEffectivePlanAsync</c> ⇒ IsActive ของสาย License)</item>
/// <item><see cref="CompanyPurchase"/> — <b>บริษัทซื้อ/ต่ออายุ/อัปเกรดแพ็กเกจระดับบริษัทเองได้ไหม</b> (ด่านเงิน) — ผูก License อยู่ ⇒ ไม่ได้ (บอกชื่อ License ·
/// ผู้ถือ · วันหมดอายุ · ทางไปต่อ) · ถอด/ลบ License แล้ว ⇒ ได้ตามปกติ · ผู้เรียกทุกทางเข้าผ่าน <c>SubscriptionService.EnsureCompanyMayPurchaseAsync</c>
/// (ล็อกด้วย <c>tools/required_call_site_check.py</c> กติกา R236)</item>
/// <item><see cref="Label"/> — ป้ายแพ็กเกจตัวเดียว (ดรอปดาวน์หน้าซื้อ · หัวโมดัลแอดมิน) — เลิกพิมพ์ชื่อ enum ("FreeTrial · Active")</item>
/// </list>
/// <para>ขอบเขต: License ที่หมดอายุ/ระงับแล้วแต่บริษัทยังผูกอยู่ ⇒ <b>ยังเป็นตัวคุม</b> (ตรงกับ <c>GetEffectivePlanAsync</c> — สิทธิ์อ่านอย่างเดียวตาม License) ·
/// ซื้อของบริษัทเองจึงยังไม่มีผล ⇒ ปฏิเสธพร้อมทาง "ถอดบริษัทออกจาก License ก่อน" (คำตัดสิน 236-3)</para>
/// </summary>
public static class PlanIdentityResolver
{
    public const string SourceCompany = "Company";
    public const string SourceLicense = "License";

    /// <summary>รหัสกฎของด่าน "บริษัทอยู่ใต้ License ซื้อแพ็กเกจระดับบริษัทไม่ได้" (log/audit/ข้อความ error)</summary>
    public const string LicenseCoveredRuleCode = "PLATFORM-LICENSE-COVERED";

    /// <summary>หน้า License ของผู้ถือ (ต่ออายุ/ถอดบริษัท)</summary>
    public const string LicensePageUrl = "/pages/account-subscription.html";

    /// <summary>ฝั่ง License (null = บริษัทไม่ได้ผูก / License ถูกลบ) · รอบ 236: + รหัส/ผู้ถือ/ระยะผ่อนผัน (ค่าตั้งต้นให้ผู้เรียกเดิมคอมไพล์ได้)</summary>
    public sealed record LicenseSide(SubscriptionPlan Plan, string? PlanName, SubscriptionStatus Status, DateTime EndDate,
        Guid? LicenseId = null, string? OwnerName = null, string? OwnerEmail = null, int GracePeriodDays = 0, Guid? OwnerUserId = null);

    /// <summary>ผลการตัดสิน · <see cref="License"/> = ฝั่ง License เมื่อที่มาเป็น License (null เมื่อเป็นของบริษัท)</summary>
    public sealed record PlanIdentity(SubscriptionPlan Plan, string? PlanName, SubscriptionStatus Status, DateTime EndDate, string Source,
        LicenseSide? License = null)
    {
        public bool ViaLicense => Source == SourceLicense;
    }

    /// <summary>ผลด่านซื้อ — ไม่ได้ ⇒ <see cref="Reason"/> (ข้อความไทยพร้อมโชว์) + <see cref="NextStep"/> (ทางไปต่อ) ·
    /// <see cref="LicenseRenewal"/> = ยอมเพราะเป็น<b>การต่ออายุ License</b> (ผู้ถือจ่ายแพ็กระดับเดียวกับ License — คำตัดสิน 236-6 แก้ตามฝ่ายค้าน B1) ·
    /// <see cref="HolderMayRenew"/> = ผู้ทำรายการเป็นผู้ถือ License (หน้าเว็บไม่ซ่อนตัวเลือกแพ็ก — เลือกแพ็กระดับ License เพื่อต่ออายุได้)</summary>
    public sealed record PurchaseVerdict(bool Allowed, string? Reason, string? NextStep, string? NextStepUrl,
        bool LicenseRenewal = false, bool HolderMayRenew = false)
    {
        public static readonly PurchaseVerdict Ok = new(true, null, null, null);
    }

    /// <param name="companyPlanName">ชื่อ PlanTemplate ของระดับแพ็กเกจบริษัท (null = ไม่รู้ ⇒ หน้าเว็บแสดงชื่อ enum)</param>
    public static PlanIdentity Resolve(SubscriptionPlan companyPlan, SubscriptionStatus companyStatus, DateTime companyEndDate,
        string? companyPlanName, LicenseSide? license)
        => license is { } l
            ? new PlanIdentity(l.Plan, l.PlanName, l.Status, l.EndDate, SourceLicense, l)
            : new PlanIdentity(companyPlan, companyPlanName, companyStatus, companyEndDate, SourceCompany);

    /// <summary><b>License ยังใช้งานได้</b> (ทดลอง/ใช้งาน/ค้างชำระ และยังไม่พ้นวันหมดอายุ + ระยะผ่อนผัน) — สูตรเดียวกับ IsActive ของสาย License
    /// ใน <c>GetEffectivePlanAsync</c> (ย้ายมาที่นี่รอบ 236 · ผู้เรียกทั้งสองเห็นคำตอบเดียว)</summary>
    public static bool LicenseActive(SubscriptionStatus status, DateTime endUtc, int gracePeriodDays, DateTime nowUtc)
        => (status == SubscriptionStatus.Trial || status == SubscriptionStatus.Active || status == SubscriptionStatus.PastDue)
           && nowUtc <= endUtc.AddDays(gracePeriodDays);

    /// <summary>รอบ 236 (ฝ่ายค้าน S3): ใช้ทรัพยากรตามแพ็กได้ไหม (ด่านที่เดิมกรองสถานะแถวบริษัทตรง ๆ เช่นโควตา OCR) — ผูก License ⇒ ตามสถานะ License
    /// (<see cref="LicenseActive"/> สูตรเดียวกับ IsActive ของ <c>GetEffectivePlanAsync</c>) แม้แถวบริษัทค้าง Suspended/Cancelled · ไม่ผูก ⇒ แถวบริษัทต้องไม่ถูกยกเลิก/ระงับ (กติกาเดิม)</summary>
    public static bool CompanyRowUsable(SubscriptionStatus companyRowStatus, PlanIdentity id, DateTime nowUtc)
        => id.ViaLicense && id.License is { } l
            ? LicenseActive(l.Status, l.EndDate, l.GracePeriodDays, nowUtc)
            : companyRowStatus is not (SubscriptionStatus.Cancelled or SubscriptionStatus.Suspended);

    /// <summary>รอบ 236 (ฝ่ายค้าน B1): "ต่ออายุ License ทำที่ไหน" — ข้อความเดียวของอีเมลเตือน License (<c>AccountPlanExpiryReminderJob</c>) · ทางที่มีอยู่จริง =
    /// หน้าแพ็กเกจของบริษัทใต้ License ที่ผู้ถือเป็นเจ้าของ (เลือกแพ็กระดับเดียวกับ License — <see cref="CompanyPurchase"/> ยอมเป็นการต่ออายุ License)</summary>
    public static string LicenseRenewalHowTo(string planName)
        => $"ต่ออายุได้ที่ \"แพ็กเกจและการชำระเงิน\" ({PlanPurchasePreview.PlanTabUrl}) ของบริษัทใต้ License ที่คุณเป็นเจ้าของ โดยเลือกแพ็ก {planName} รอบเดิม "
           + "· เปลี่ยนระดับ License ติดต่อผู้ดูแลระบบ NextAcc";

    /// <summary>หัวข้อ/ป้ายของการต่ออายุ License ผ่านหน้าซื้อของบริษัท — ข้อความเดียวทุกจอ</summary>
    public const string LicenseRenewalHeadline = "ต่ออายุ License (ยืดวันหมดอายุของ License · ครอบทุกบริษัทใต้ License)";

    /// <summary><b>บริษัทซื้อ/ต่ออายุ/อัปเกรด/เปลี่ยนแพ็กเกจระดับบริษัทเองได้ไหม</b> (รอบ 236 · คำตัดสิน 236-2/236-3/236-6) — pure
    /// <para>ผูก License ⇒ ไม่ได้: แพ็กเกจระดับบริษัทที่จ่ายจะไม่มีผล (สิทธิ์/โควตามาจาก License) = เก็บเงินซ้ำ/ขายของที่ลูกค้ามีอยู่แล้ว ·
    /// <b>ยกเว้น</b> (ฝ่ายค้าน B1 — ทางเดิมรอบ 219 S5 ที่ใช้ได้จริง): ผู้ทำรายการ<b>เป็นผู้ถือ License</b> และจ่ายแพ็กระดับเดียวกับ License
    /// (<see cref="PlatformPlanPricing.MayCascadeToAccountPlan"/> — ตัวเดียวกับตอนอนุมัติที่ยืด License) ⇒ ยอมในฐานะ "ต่ออายุ License" ·
    /// License ที่ถูกระงับ/ยกเลิก การจ่ายไม่ปลดสถานะ ⇒ ไม่ยอม · ทางไปต่อแยกผู้ถือ (เลือกแพ็กระดับ License) กับคนอื่น (ให้ผู้ถือต่ออายุ หรือถอดบริษัทออก)</para></summary>
    /// <param name="actorIsLicenseHolder">ผู้ทำรายการ (หรือเจ้าของบริษัทในงานระบบ) คือผู้ถือ License</param>
    /// <param name="targetPlan">แพ็กที่จะจ่าย · null = ถามระดับหน้า (ยังไม่เลือกแพ็ก)</param>
    public static PurchaseVerdict CompanyPurchase(PlanIdentity id, DateTime nowUtc, bool actorIsLicenseHolder = false, SubscriptionPlan? targetPlan = null)
    {
        if (!id.ViaLicense || id.License is not { } l) return PurchaseVerdict.Ok;
        var name = string.IsNullOrWhiteSpace(l.PlanName) ? l.Plan.ToString() : l.PlanName!;
        var owner = !string.IsNullOrWhiteSpace(l.OwnerName) ? l.OwnerName!
            : !string.IsNullOrWhiteSpace(l.OwnerEmail) ? l.OwnerEmail! : "ผู้ถือ License";
        var renewable = l.Status is not (SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled);
        if (actorIsLicenseHolder && renewable && targetPlan is SubscriptionPlan t && PlatformPlanPricing.MayCascadeToAccountPlan(t, l.Plan))
            return new PurchaseVerdict(true, null, LicenseRenewalHeadline, null, LicenseRenewal: true, HolderMayRenew: true);
        var until = SubscriptionExpiry.IsOpenEnded(l.EndDate, nowUtc) ? "ไม่มีวันหมดอายุ" : $"ใช้ได้ถึง {PlatformPurchaseEmailText.BeDate(l.EndDate)}";
        var reason = LicenseActive(l.Status, l.EndDate, l.GracePeriodDays, nowUtc)
            ? $"บริษัทนี้ใช้แพ็กเกจ {name} ผ่าน License ของ {owner} ({until}) อยู่แล้ว — ซื้อ/เปลี่ยนแพ็กเกจระดับบริษัทไม่ได้ "
              + "เพราะสิทธิ์และโควตาของบริษัทมาจาก License (แพ็กเกจระดับบริษัทที่จ่ายจะไม่มีผล)"
            : $"บริษัทนี้ผูกกับ License {name} ของ {owner} ซึ่ง{StatusText(l.Status, l.EndDate, nowUtc)} — สิทธิ์ของบริษัทยังมาจาก License นี้ "
              + "จึงซื้อแพ็กเกจระดับบริษัทไม่ได้ (จ่ายแล้วจะไม่มีผล)";
        if (actorIsLicenseHolder && renewable)
            return new PurchaseVerdict(false, reason,
                $"คุณเป็นผู้ถือ License — ต่ออายุ License ได้ที่หน้านี้โดยเลือกแพ็ก {name} รอบเดิม · เปลี่ยนระดับ License ติดต่อผู้ดูแลระบบ NextAcc",
                PlanPurchasePreview.PlanTabUrl, HolderMayRenew: true);
        if (actorIsLicenseHolder)
            return new PurchaseVerdict(false, reason,
                $"License นี้{StatusText(l.Status, l.EndDate, nowUtc)} — ติดต่อผู้ดูแลระบบ NextAcc เพื่อเปิดใช้ License อีกครั้ง หรือถอดบริษัทออกจาก License",
                LicensePageUrl);
        return new PurchaseVerdict(false, reason, $"ให้ผู้ถือ License {owner} ต่ออายุ หรือถอดบริษัทออกจาก License", null);
    }

    /// <summary>ป้ายแพ็กเกจตัวเดียว (ดรอปดาวน์หน้าซื้อ · หัวโมดัลแอดมิน) — ชื่อแพ็กเกจที่แอดมินตั้ง + ที่มา/สถานะเป็นภาษาไทย · ไม่พิมพ์ชื่อ enum</summary>
    public static string Label(PlanIdentity id, bool companyIsPermanentFree = false)
    {
        var name = string.IsNullOrWhiteSpace(id.PlanName) ? id.Plan.ToString() : id.PlanName!;
        if (id.ViaLicense) return $"{name} · ผ่าน License";
        if (companyIsPermanentFree && id.Status == SubscriptionStatus.Active) return $"{name} · ฟรีถาวร";
        return $"{name} · {StatusLabel(id.Status)}";
    }

    /// <summary>สถานะแพ็กเกจเป็นภาษาไทย — <b>ตารางเดียว</b> (รอบ 236 ย้ายมาจาก <c>PlatformBillingCenterService.StatusLabel</c> ซึ่งตอนนี้เรียกตัวนี้)</summary>
    public static string StatusLabel(SubscriptionStatus s) => s switch
    {
        SubscriptionStatus.Trial => "ทดลองใช้",
        SubscriptionStatus.Active => "ใช้งานอยู่",
        SubscriptionStatus.PastDue => "เกินกำหนดต่ออายุ",
        SubscriptionStatus.Cancelled => "ยกเลิกแล้ว",
        SubscriptionStatus.Expired => "หมดอายุ",
        SubscriptionStatus.Suspended => "ถูกระงับ",
        _ => s.ToString(),
    };

    private static string StatusText(SubscriptionStatus s, DateTime endUtc, DateTime nowUtc) => s switch
    {
        SubscriptionStatus.Suspended => "ถูกระงับ",
        SubscriptionStatus.Cancelled => "ถูกยกเลิก",
        _ => endUtc < nowUtc ? $"หมดอายุแล้ว ({PlatformPurchaseEmailText.BeDate(endUtc)})" : StatusLabel(s),
    };
}

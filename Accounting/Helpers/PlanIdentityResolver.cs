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
/// <para>รอบ 238 (236-3 ทำจริง): ไม่มีการถอยไปใช้แถวบริษัทอัตโนมัติ แต่ (1) ระยะผ่อนผันมีจริง (<see cref="LicenseStatusAfter"/>) (2) งานเตือนเลิกเขียนแถวบริษัท
/// (3) License ระงับ/ยกเลิก ⇒ ด่านเห็นเป็นหมดอายุ = อ่าน/ส่งออกได้ (<see cref="GateStatus"/>) (4) สร้างไฟล์ยื่นแบบ/ส่งออกผ่านด่าน "หมดอายุ" (<c>SubscriptionGatePolicy.IsComplianceOutputPath</c>)
/// (5) แจ้งเจ้าของบริษัทใต้ License (<see cref="LicenseLapseCompanyNotice"/>) (6) เจ้าของบริษัท/ผู้ถือถอดเองได้พร้อมพรีวิว (<see cref="DetachPreview"/>)
/// (7) ผูกบริษัทที่จ่ายเอง ⇒ เตือนว่าเวลาเดินต่อ (<see cref="AttachPaidTimeWarning"/> · 236-9 รอเจ้าของยืนยัน)</para>
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
           && nowUtc <= LicenseGraceEndsAt(endUtc, gracePeriodDays);

    /// <summary>รอบ 238 (คำตัดสิน 236-3): วันสิ้นระยะผ่อนผันของ License — สูตรเดียวของ <see cref="LicenseActive"/> · <see cref="LicenseStatusAfter"/> ·
    /// อีเมลเตือน · แบนเนอร์หน้าแพ็กเกจ (<c>graceEndsAt</c>) · ชื่อไม่ชน <c>AddOnPaymentPolicy.GraceEndsAt</c> (ส่วนเสริม — คนละกติกา)</summary>
    public static DateTime LicenseGraceEndsAt(DateTime endUtc, int gracePeriodDays) => endUtc.AddDays(Math.Max(0, gracePeriodDays));

    /// <summary><b>สถานะ License หลังเวลาผ่านไป</b> (รอบ 238 · คำตัดสิน 236-3 ข้อ 1) — pure · ผู้เรียกเดียว: <c>AccountPlanExpiryReminderJob</c>
    /// <para>ที่มา: งานเตือนตั้ง License เป็น Expired ทันทีที่ถึงวันหมดอายุ ขณะที่ <see cref="LicenseActive"/> นับเฉพาะ Trial/Active/PastDue ⇒ ระยะผ่อนผันที่อีเมลสัญญา
    /// "ผ่อนผัน N วัน" ไม่เคยมีจริง (0 วัน) · ตอนนี้: ก่อนวันหมดอายุ ⇒ คงเดิม · พ้นวันหมดอายุแต่ยังในระยะผ่อนผัน ⇒ PastDue (เขียนได้เต็ม) ·
    /// พ้นระยะผ่อนผัน ⇒ Expired (อ่าน/ส่งออก/สร้างไฟล์ยื่นแบบได้ · เขียน 402) · Suspended/Cancelled (แอดมินตั้ง) ⇒ ไม่แตะ ·
    /// Expired ที่ตั้งไว้แล้ว ⇒ ไม่ปลุกกลับ (ข้อมูลเก่าในระยะผ่อนผันแก้ครั้งเดียวด้วย <c>LicenseLapseMigration</c>)</para></summary>
    public static SubscriptionStatus LicenseStatusAfter(SubscriptionStatus status, DateTime endUtc, int gracePeriodDays, DateTime nowUtc)
    {
        if (status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled or SubscriptionStatus.Expired) return status;
        if (nowUtc <= endUtc) return status;
        return nowUtc <= LicenseGraceEndsAt(endUtc, gracePeriodDays) ? SubscriptionStatus.PastDue : SubscriptionStatus.Expired;
    }

    /// <summary><b>สถานะที่ด่านแพ็กเกจ (gate) เห็น</b> (รอบ 238 · คำตัดสิน 236-3 ข้อ 3) — pure · ผู้เรียกเดียว: <c>SubscriptionService.ResolveGateOverlayAsync</c>
    /// <para>License ถูกระงับ/ยกเลิก (ผ่าน License) ⇒ แสดงต่อด่านเป็น <see cref="SubscriptionStatus.Expired"/> = โหมดอ่านอย่างเดียว (อ่าน + ส่งออก + สร้างไฟล์ยื่นแบบได้ ·
    /// เขียน 402) แทน 403 ทุกคำขอ — บริษัทใต้ License ไม่ได้ทำผิดเอง ข้อมูลบัญชีของตัวเองต้องเปิดดู/ส่งออก/ยื่นภาษีได้เสมอ · <b>ไม่เก็บลงฐาน</b> (สถานะจริงของ License คงเดิม
    /// หน้าแพ็กเกจยังแสดง "ถูกระงับ") · ระงับระดับบริษัท (<c>Company.Status</c> หรือแถวบริษัทที่ไม่ผูก License เป็น Suspended/Cancelled) ⇒ คงเดิม (บล็อกตามเดิม)</para></summary>
    public static SubscriptionStatus GateStatus(PlanIdentity id)
        => LicenseInactiveReadOnly(id) ? SubscriptionStatus.Expired : id.Status;

    /// <summary>รอบ 238 ฝ่ายค้าน S1: บริษัทอยู่ใต้ License ที่ถูกระงับ/ยกเลิก ⇒ อ่านอย่างเดียว<b>ทุกโหมดด่านเขียน</b> (ธง <c>SubscriptionGateFacts.InactiveReadOnly</c>) —
    /// เดิมสถานะนี้ได้ 403 ไม่ขึ้นกับโหมด ⇒ หลัง <see cref="GateStatus"/> แปลงเป็นหมดอายุ ต้องไม่หลวมลงเป็น "log อย่างเดียว" ของ partner/LogOnly
    /// และไม่พึ่ง <c>GetEffectivePlanAsync</c> (ล้ม = fail-open)</summary>
    public static bool LicenseInactiveReadOnly(PlanIdentity id)
        => id.ViaLicense && id.Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled;

    /// <summary><b>สถานะแถวบริษัทเมื่อถอดออกจาก License</b> (รอบ 238 ฝ่ายค้าน B2) — pure · ผู้เรียก: <c>PlanIdentityReader.AfterDetachAsync</c> (พรีวิว) +
    /// <c>AccountSubscriptionController.Detach</c> (ประทับจริง) — ผลเดียวกัน
    /// <para>แถวที่พักไว้ใต้ License อาจยังเป็น Active/PastDue/Trial ทั้งที่วันหมดอายุผ่านไปนานแล้ว (งานหมดอายุข้ามแถวที่ License คุม) ⇒ ถอดแล้วงานรอบถัดไปจะเดิน
    /// Active→PastDue→<b>Suspended</b> ในรอบเดียว = 403 เงียบ ๆ · ตอนนี้: เลยวันหมดอายุ (ไม่ใช่ฟรีถาวร/ไม่มีรอบ) ⇒ <b>Expired</b> = อ่านอย่างเดียว + ซื้อได้ทันที ·
    /// ยังไม่หมด / ฟรีถาวร / Expired/Suspended/Cancelled อยู่แล้ว ⇒ คงเดิม</para></summary>
    public static SubscriptionStatus CompanyStatusAfterDetach(SubscriptionStatus status, DateTime endUtc, bool isPermanentFree, DateTime nowUtc)
    {
        if (status is not (SubscriptionStatus.Active or SubscriptionStatus.PastDue or SubscriptionStatus.Trial)) return status;
        if (isPermanentFree || SubscriptionExpiry.IsOpenEnded(endUtc, nowUtc) || endUtc >= nowUtc) return status;
        return SubscriptionStatus.Expired;
    }

    /// <summary>รอบ 238 ฝ่ายค้าน S2: ผูกบริษัทเข้า License ที่พ้นวันหมดอายุแล้ว (อยู่ในระยะผ่อนผัน) ⇒ เตือนในผลตอบ/พรีวิว · null = ยังไม่หมดอายุ</summary>
    public static string? AttachLicenseGraceWarning(DateTime licenseEndUtc, int gracePeriodDays, DateTime nowUtc)
        => nowUtc <= licenseEndUtc ? null
            : $"License พ้นวันหมดอายุแล้ว ({PlatformPurchaseEmailText.BeDate(licenseEndUtc)}) อยู่ในช่วงผ่อนผันถึง {PlatformPurchaseEmailText.BeDate(LicenseGraceEndsAt(licenseEndUtc, gracePeriodDays))} "
              + "— หลังจากนั้นบริษัทจะเป็นโหมดอ่านอย่างเดียวจนกว่าจะต่ออายุ License";

    /// <summary>รอบ 238 ฝ่ายค้าน S2: ผูกบริษัทเข้า License <b>อัตโนมัติ</b> (สร้างบริษัทใหม่) ได้ไหม — เฉพาะ License ที่ใช้งานได้และยังไม่พ้นวันหมดอายุ
    /// (ช่วงผ่อนผันไม่ผูกอัตโนมัติ — ผูกเองได้พร้อมคำเตือน)</summary>
    public static bool MayAutoAttach(SubscriptionStatus status, DateTime licenseEndUtc, DateTime nowUtc)
        => status is SubscriptionStatus.Trial or SubscriptionStatus.Active or SubscriptionStatus.PastDue && nowUtc <= licenseEndUtc;

    /// <summary>ผลพรีวิว "ถอดบริษัทออกจาก License แล้วจะใช้อะไร" (รอบ 238 · 236-3 ข้อ 6)</summary>
    /// <param name="Label">ป้ายแพ็กเกจของแถวบริษัท (<see cref="Label"/>)</param>
    /// <param name="UsableUntilUtc">ใช้ได้ถึง (null = ไม่มีวันหมดอายุ หรือใช้ไม่ได้แล้ว)</param>
    /// <param name="Resumes">แพ็กเกจของบริษัทกลับมามีผลทันที (ยังไม่หมดอายุ)</param>
    /// <param name="CanBuyNow">ซื้อ/ต่ออายุแพ็กเกจระดับบริษัทได้ทันทีหลังถอด (<see cref="CompanyPurchase"/> บนตัวตนหลังถอด)</param>
    /// <param name="Text">ข้อความเดียวที่หน้าเว็บแสดงในกล่องยืนยัน</param>
    public sealed record DetachPreviewResult(string Label, DateTime? UsableUntilUtc, bool Resumes, bool CanBuyNow, string Text);

    /// <summary><b>ถอดแล้วจะใช้อะไร</b> — pure · <paramref name="afterDetach"/> = ตัวตนของแถวบริษัทเมื่อไม่ผูก License
    /// (<c>PlanIdentityReader.AfterDetachAsync</c>) · ไม่มีการแต่งสถานะใหม่: แถวที่หมดอายุแล้วบอกตรง ๆ ว่าหมดอายุ + ซื้อได้ทันที</summary>
    public static DetachPreviewResult DetachPreview(PlanIdentity afterDetach, bool companyIsPermanentFree, DateTime nowUtc)
    {
        var label = Label(afterDetach, companyIsPermanentFree);
        var openEnded = SubscriptionExpiry.IsOpenEnded(companyIsPermanentFree, afterDetach.EndDate, nowUtc);
        var resumes = afterDetach.Status is SubscriptionStatus.Trial or SubscriptionStatus.Active
                      && (openEnded || nowUtc <= afterDetach.EndDate);
        var canBuy = CompanyPurchase(afterDetach, nowUtc).Allowed;
        string text;
        if (resumes && openEnded) text = $"หลังถอดจะใช้ {label} (ไม่มีวันหมดอายุ)";
        else if (resumes) text = $"หลังถอดจะใช้ {label} ถึง {PlatformPurchaseEmailText.BeDate(afterDetach.EndDate)}";
        else text = $"หลังถอดจะใช้ {label} ซึ่ง{StatusText(afterDetach.Status, afterDetach.EndDate, nowUtc)} — ซื้อ/ต่ออายุแพ็กเกจของบริษัทได้ทันทีหลังถอด";
        return new DetachPreviewResult(label, resumes && !openEnded ? afterDetach.EndDate : null, resumes, canBuy, text);
    }

    /// <summary><b>คำเตือนตอนผูกบริษัทที่จ่ายแพ็กเกจเองเข้า License</b> (รอบ 238 · คำตัดสิน 236-9 "ค่าตั้งต้น รอเจ้าของยืนยัน") — pure
    /// <para>ค่าตั้งต้น: เวลาที่บริษัทจ่ายไว้<b>เดินต่อ ไม่หยุด ไม่คืนเงิน ไม่เป็นเครดิต</b> ⇒ บอกก่อนผูก (ไม่ใช่เงียบ) · null = ไม่มีเวลาที่จ่ายเหลือ
    /// (ทดลองใช้ · ฟรีถาวร · ไม่มีรอบ · หมดอายุแล้ว) · ผู้เรียก: <c>AccountSubscriptionController.Attach/AttachPreview</c> · <c>AdminAccountSubscriptionController.AdminAttach</c></para></summary>
    public static string? AttachPaidTimeWarning(SubscriptionKind kind, bool isPermanentFree, SubscriptionStatus status, DateTime endUtc,
        DateTime? lastPaymentDate, DateTime nowUtc)
    {
        var paid = !isPermanentFree && (kind == SubscriptionKind.Paid || (kind == SubscriptionKind.Unknown && lastPaymentDate != null));
        if (!paid || status is not (SubscriptionStatus.Active or SubscriptionStatus.PastDue)) return null;
        if (SubscriptionExpiry.IsOpenEnded(endUtc, nowUtc) || endUtc <= nowUtc) return null;
        var days = (int)Math.Ceiling((endUtc - nowUtc).TotalDays);
        return $"เวลาที่จ่ายไว้ {days} วันยังเดินอยู่ ไม่คืนเงิน — แพ็กเกจของบริษัทหมดอายุ {PlatformPurchaseEmailText.BeDate(endUtc)} ตามเดิมระหว่างอยู่ใต้ License "
               + "(ถอดออกก่อนวันนั้นจะกลับมาใช้ได้ถึงวันเดิม)";
    }

    /// <summary>ข้อความแจ้งเจ้าของบริษัทใต้ License (ไม่ใช่ผู้ถือ) ก่อน/เมื่อ License หมดอายุ (รอบ 238 · 236-3 ข้อ 5) — pure · ข้อความล้วน (ผู้เรียก HtmlEncode)
    /// <para>บอกวันสิ้นระยะผ่อนผัน (พ.ศ.) · ข้อมูลเปิดดู/ส่งออก/สร้างไฟล์ยื่นแบบได้เสมอ · ทางถอดออก</para></summary>
    public static (string Subject, IReadOnlyList<string> Lines) LicenseLapseCompanyNotice(string companyName, string planName, string holderName,
        DateTime endUtc, int gracePeriodDays, bool expiryDay, int daysLeft)
    {
        var graceEnd = PlatformPurchaseEmailText.BeDate(LicenseGraceEndsAt(endUtc, gracePeriodDays));
        var subject = expiryDay
            ? $"License {planName} ที่บริษัท {companyName} ใช้อยู่หมดอายุแล้ว — ใช้งานเต็มได้ถึง {graceEnd}"
            : $"License {planName} ที่บริษัท {companyName} ใช้อยู่จะหมดอายุใน {daysLeft} วัน";
        var lines = new List<string>
        {
            expiryDay
                ? $"License {planName} ของ {holderName} ซึ่งบริษัท {companyName} ใช้อยู่ หมดอายุเมื่อ {PlatformPurchaseEmailText.BeDate(endUtc)}"
                : $"License {planName} ของ {holderName} ซึ่งบริษัท {companyName} ใช้อยู่ จะหมดอายุ {PlatformPurchaseEmailText.BeDate(endUtc)}",
            $"บริษัทยังใช้งานได้เต็มถึงสิ้นระยะผ่อนผัน {graceEnd} — หลังจากนั้นเป็นโหมดอ่านอย่างเดียว (สร้าง/แก้ไขเอกสารไม่ได้)",
            "ข้อมูลของบริษัทเปิดดูและส่งออกได้เสมอ และสร้างไฟล์ยื่นแบบภาษีได้เสมอ แม้ License หมดอายุ",
            $"ทางไปต่อ: ให้ {holderName} ต่ออายุ License หรือเจ้าของบริษัทถอดบริษัทออกจาก License ที่หน้า \"แพ็กเกจและการสมัครสมาชิก\" ({DetachPageUrl}) "
                + "แล้วใช้/ซื้อแพ็กเกจของบริษัทเอง (หน้าจะบอกก่อนกดว่าหลังถอดจะใช้แพ็กเกจอะไร)",
        };
        return (subject, lines);
    }

    /// <summary>รอบ 238 (236-3 ข้อ 6): บรรทัดเตือนบนแบนเนอร์หน้าแพ็กเกจของบริษัทใต้ License ที่หมดอายุ/ระงับ — pure · null = License ยังไม่หมดอายุ/ไม่ได้ผูก ·
    /// หน้าเว็บแสดงข้อความนี้ตรง ๆ (ไม่ประกอบเงื่อนไข/ข้อความเอง)</summary>
    public static string? LicenseLapseNote(PlanIdentity id, DateTime nowUtc)
    {
        if (!id.ViaLicense || id.License is not { } l) return null;
        var graceEnd = PlatformPurchaseEmailText.BeDate(LicenseGraceEndsAt(l.EndDate, l.GracePeriodDays));
        const string always = "ดูและส่งออกข้อมูล และสร้างไฟล์ยื่นแบบภาษีได้เสมอ";
        if (l.Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled)
            return $"License {StatusText(l.Status, l.EndDate, nowUtc)} — โหมดอ่านอย่างเดียว (สร้าง/แก้ไขเอกสารไม่ได้) · {always}";
        if (nowUtc <= l.EndDate) return null;
        return LicenseActive(l.Status, l.EndDate, l.GracePeriodDays, nowUtc)
            ? $"License หมดอายุแล้ว — ใช้งานได้เต็มถึงสิ้นระยะผ่อนผัน {graceEnd} · {always}"
            : $"License หมดอายุและพ้นระยะผ่อนผัน ({graceEnd}) แล้ว — โหมดอ่านอย่างเดียว (สร้าง/แก้ไขเอกสารไม่ได้) · {always}";
    }

    /// <summary>รอบ 238 (236-3 ข้อ 6): ใครถอดบริษัทออกจาก License ได้ — <b>เจ้าของบริษัท (Owner)</b> หรือ<b>ผู้ถือ License ที่บริษัทผูกอยู่</b> (เดิมเฉพาะ Owner ⇒ ผู้ถือที่ไม่ได้เป็น Owner
    /// ถอดบริษัทของคนอื่นออกจาก License ตัวเองไม่ได้) · ผู้เรียก: <c>AccountSubscriptionController.Detach/DetachPreview/Effective</c> (canSelfDetach)</summary>
    public static bool MayDetach(bool viaLicense, bool isCompanyOwner, bool isLicenseHolder) => viaLicense && (isCompanyOwner || isLicenseHolder);

    /// <summary>ข้อความปฏิเสธเมื่อผู้ทำรายการไม่ใช่เจ้าของบริษัทหรือผู้ถือ License</summary>
    public const string DetachForbiddenText = "ถอดบริษัทออกจาก License ได้เฉพาะเจ้าของบริษัท (Owner) หรือผู้ถือ License ที่บริษัทผูกอยู่";

    /// <summary>หน้าที่เจ้าของบริษัทถอดบริษัทออกจาก License ได้เอง (แบนเนอร์ + ปุ่มถอด)</summary>
    public const string DetachPageUrl = "/pages/subscription.html";

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

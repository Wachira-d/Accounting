namespace Accounting.Helpers;

/// <summary>เป้าหมายของการแจ้งลูกค้า เพื่อหา LINE ที่ลูกค้าผูกไว้เอง (รอบ 211): ระเบียน (การจอง/ออเดอร์) ชนะ สมาชิก ชนะ ผู้ติดต่อ ERP ·
/// <c>RecordKind</c> = <see cref="CustomerLineLinkRule.KindBooking"/>/<see cref="CustomerLineLinkRule.KindOrder"/></summary>
public sealed record CustomerLineTarget(Guid? SiteCustomerId, string? RecordKind, Guid? RecordId, Guid? ContactId = null)
{
    public static CustomerLineTarget ForBooking(Guid bookingId, Guid? customerId) => new(customerId, CustomerLineLinkRule.KindBooking, bookingId);
    public static CustomerLineTarget ForOrder(Guid orderId, Guid? customerId) => new(customerId, CustomerLineLinkRule.KindOrder, orderId);
    public static CustomerLineTarget ForContact(Guid contactId) => new(null, null, null, contactId);
}

/// <summary>ผลตัดสินช่องทาง LINE ของบริษัท — <c>Scope</c> = OA ที่ "ส่งจริง" (ตรงกับ <c>LineNotifyService.ResolveConfigAsync</c>) ·
/// <c>CanLink</c> = ลูกค้าผูกเองได้ (ต้องมี token + secret ของ webhook + basic id สำหรับปุ่มเพิ่มเพื่อน)</summary>
public sealed record CustomerLineChannel(string Scope, string? BasicId, bool CanPush, bool CanLink, string? Problem, string? CurrentDestination);

/// <summary>รอบ 212: ตัวเลือก "ถอยไปอีเมลของระบบ" (ค่า + ป้ายไทย)</summary>
public sealed record CustomerEmailFallbackOption(string Value, string Label);

/// <summary>
/// **ตัวตัดสินช่องทางแจ้งลูกค้าปลายทาง (LINE/อีเมล) ตัวเดียว** — รอบ 211 · คำตัดสินข้อ 170
/// <list type="bullet">
/// <item>LINE: บริษัทเปิด LINE + มี token ของตัวเอง ⇒ OA ของบริษัท · ไม่งั้น ⇒ OA ของ NextAcc (หน้าแอดมิน → config <c>Line:*</c> · <c>INextAccLineConfig</c>) — <b>กติกาเดียวกับที่ push ใช้อยู่เดิม</b>
///   (<c>LineNotifyService.ResolveConfigAsync</c>) ⇒ ค่าเริ่มต้นไม่เปลี่ยนพฤติกรรมการส่ง</item>
/// <item>userId ของ LINE ผูกกับ OA (provider) ⇒ ลิงก์ที่ผูกกับ OA อื่นใช้ไม่ได้ (<see cref="LinkUsable"/>) — ร้านสลับ OA = ลูกค้าต้องผูกใหม่</item>
/// <item>อีเมล: ร้านตั้ง + ทดสอบผ่าน (<c>CompanySettings.EmailConfigured</c>) ⇒ ผู้ส่งของร้าน แล้วถอยไปอีเมลของระบบเมื่อล้ม (ฝ่ายค้าน) · ไม่งั้น ⇒ อีเมลของระบบ (เดิม)</item>
/// <item>ลำดับ: ลูกค้าผูก LINE เอง (opt-in) ⇒ LINE ก่อน แล้วอีเมล · ไม่ได้ผูก ⇒ อีเมลก่อน (เดิม)</item>
/// </list>
/// </summary>
public static class CustomerNotifyChannel
{
    public const string ScopeNextAcc = "nextacc";
    public const string ScopeCompany = "company";
    public const string ChannelEmail = "email";
    public const string ChannelLine = "line";
    public const string EmailTenant = "tenant";
    public const string EmailSystem = "system";

    public static CustomerLineChannel ResolveLine(
        bool companyLineEnabled, bool companyHasToken, bool companyHasSecret, string? companyBasicId, string? companyDestination,
        bool nextAccHasToken, bool nextAccHasSecret, string? nextAccBasicId, string? nextAccDestination)
    {
        if (companyLineEnabled && companyHasToken)
        {
            var basic = CustomerLineLinkRule.NormalizeBasicId(companyBasicId);
            string? problem = null;
            if (!companyHasSecret) problem = "ร้านใช้ LINE OA ของตัวเอง แต่ยังไม่ได้ใส่ Channel Secret (ต้องใช้ตรวจ webhook) — ตั้งที่ ตั้งค่า → LINE";
            else if (basic == null) problem = "ร้านใช้ LINE OA ของตัวเอง แต่ยังไม่ได้ใส่ Basic ID (@xxxx) สำหรับปุ่มเพิ่มเพื่อน — ตั้งที่ ตั้งค่า → LINE";
            return new CustomerLineChannel(ScopeCompany, basic, true, problem == null, problem, companyDestination);
        }
        var nb = CustomerLineLinkRule.NormalizeBasicId(nextAccBasicId);
        string? p2 = null;
        if (!nextAccHasToken) p2 = NextAccLineConfigRule.NotConfiguredMessage + " และร้านยังไม่ได้ตั้ง LINE OA ของตัวเอง";
        else if (!nextAccHasSecret || nb == null) p2 = "ระบบยังไม่ได้ตั้งค่า LINE OA กลางให้ผูกได้ (ขาด Channel Secret หรือ Basic ID — ผู้ดูแลระบบ → ตั้งค่าเว็บไซต์ → LINE OA กลาง)";
        return new CustomerLineChannel(ScopeNextAcc, nb, nextAccHasToken, p2 == null, p2, nextAccDestination);
    }

    /// <summary>ลิงก์ที่ผูกกับ OA <paramref name="linkScope"/>/<paramref name="linkDestination"/> ใช้ส่งผ่านช่องปัจจุบันได้ไหม ·
    /// ปลายทาง (bot userId จาก webhook <c>destination</c>) ไม่รู้ข้างใดข้างหนึ่ง ⇒ ตัดสินจาก scope อย่างเดียว</summary>
    public static bool LinkUsable(string linkScope, string? linkDestination, string currentScope, string? currentDestination)
        => string.Equals(linkScope, currentScope, StringComparison.Ordinal)
           && (string.IsNullOrEmpty(linkDestination) || string.IsNullOrEmpty(currentDestination)
               || string.Equals(linkDestination, currentDestination, StringComparison.Ordinal));

    /// <summary>รอบ 211 ฝ่ายค้าน ข้อ 4: ปลายทางของ webhook (<c>destination</c>) ใช้ได้ไหมกับ bot userId ที่ตั้งไว้ (<c>Line:BotUserId</c>) —
    /// ไม่ได้ตั้ง/ไม่มีใน webhook ⇒ ยอมรับ (ตัดสินด้วย scope) · ตั้งไว้แล้วไม่ตรง ⇒ ปฏิเสธ (ค่าตั้งผิด ลิงก์ที่เก็บจะใช้ไม่ได้)</summary>
    public static bool AcceptDestination(string? configuredBotUserId, string? receivedDestination)
        => string.IsNullOrWhiteSpace(configuredBotUserId) || string.IsNullOrWhiteSpace(receivedDestination)
           || string.Equals(configuredBotUserId.Trim(), receivedDestination.Trim(), StringComparison.Ordinal);

    /// <summary>ลำดับช่องที่จะลอง (หยุดที่ช่องแรกที่ส่งสำเร็จ)</summary>
    public static IReadOnlyList<string> Order(bool hasEmail, bool hasLine, bool lineOptedIn)
    {
        var list = new List<string>(2);
        if (lineOptedIn && hasLine) list.Add(ChannelLine);
        if (hasEmail) list.Add(ChannelEmail);
        if (!lineOptedIn && hasLine) list.Add(ChannelLine);
        return list;
    }

    /// <summary>รอบ 212 (คำตัดสินข้อ 171 · <c>CompanySettings.CustomerEmailFallback</c>): ร้านตั้ง+ล้ม หรือไม่ตั้ง ⇒ ถอยไปอีเมลของระบบ (ค่าว่าง = ค่านี้ = พฤติกรรมรอบ 211)</summary>
    public const string FallbackSystemOnFailOrUnset = "system-on-fail-or-unset";
    /// <summary>ร้านตั้งแล้วล้ม ⇒ ไม่ถอย (ไม่ส่งอีเมลในนามระบบ) · ไม่ได้ตั้ง ⇒ อีเมลของระบบ</summary>
    public const string FallbackSystemWhenUnsetOnly = "system-when-unset-only";
    /// <summary>ใช้ผู้ส่งของร้านเท่านั้น — ไม่ได้ตั้ง ⇒ ไม่ส่งอีเมล</summary>
    public const string FallbackTenantOnly = "tenant-only";

    /// <summary>ตัวเลือกของหน้าเว็บ (เซิร์ฟเวอร์ส่งป้าย · F2 #5)</summary>
    public static readonly IReadOnlyList<CustomerEmailFallbackOption> FallbackOptions = new[]
    {
        new CustomerEmailFallbackOption(FallbackSystemOnFailOrUnset, "ใช้อีเมลของร้าน · ล้มหรือยังไม่ตั้ง ⇒ ส่งด้วยอีเมลของระบบ NextAcc แทน (ค่าเริ่มต้น)"),
        new CustomerEmailFallbackOption(FallbackSystemWhenUnsetOnly, "ใช้อีเมลของร้าน · ล้ม ⇒ ไม่ส่งแทน · ยังไม่ตั้ง ⇒ ใช้อีเมลของระบบ NextAcc"),
        new CustomerEmailFallbackOption(FallbackTenantOnly, "ใช้อีเมลของร้านเท่านั้น (ยังไม่ตั้ง/ล้ม ⇒ ไม่ส่งอีเมล)"),
    };

    /// <summary>ค่าที่บันทึกได้: ว่าง (= ค่าเริ่มต้น) หรือหนึ่งใน <see cref="FallbackOptions"/></summary>
    public static bool IsValidFallback(string? value)
        => string.IsNullOrWhiteSpace(value) || FallbackOptions.Any(o => o.Value == value.Trim());

    /// <summary>ค่าที่มีผล (ว่าง/ไม่รู้จัก ⇒ <see cref="FallbackSystemOnFailOrUnset"/>)</summary>
    public static string EffectiveFallback(string? value)
        => !string.IsNullOrWhiteSpace(value) && FallbackOptions.Any(o => o.Value == value.Trim()) ? value.Trim() : FallbackSystemOnFailOrUnset;

    /// <summary>ลำดับผู้ส่งอีเมล (รอบ 211 ฝ่ายค้าน + รอบ 212 ข้อ 2) — ค่าเริ่มต้น: ร้านตั้ง+ทดสอบผ่าน ⇒ ของร้าน แล้ว<b>ถอยไปของระบบ</b>เมื่อล้ม · ไม่ได้ตั้ง ⇒ ของระบบ ·
    /// <see cref="FallbackSystemWhenUnsetOnly"/>: ตั้งแล้ว ⇒ ของร้านอย่างเดียว · <see cref="FallbackTenantOnly"/>: ไม่ได้ตั้ง ⇒ ว่าง (ไม่ส่งอีเมล)</summary>
    public static IReadOnlyList<string> EmailPlan(bool tenantEmailConfigured, string? fallback = null)
        => EffectiveFallback(fallback) switch
        {
            FallbackTenantOnly => tenantEmailConfigured ? new[] { EmailTenant } : Array.Empty<string>(),
            FallbackSystemWhenUnsetOnly => tenantEmailConfigured ? new[] { EmailTenant } : new[] { EmailSystem },
            _ => tenantEmailConfigured ? new[] { EmailTenant, EmailSystem } : new[] { EmailSystem },
        };

    /// <summary>ข้อความเตือนเมื่อส่งถึงด้วยอีเมลของระบบหลังผู้ส่งของร้านล้ม (ประทับบนรายการผ่าน <see cref="CmsNotificationText.FailureNote"/>)</summary>
    public static string TenantEmailFallbackWarning(string tenantError)
        => $"{CmsNotificationText.FallbackTag} ผู้ส่งอีเมลของร้านล้ม ({tenantError}) — ส่งถึงลูกค้าทางอีเมลของระบบแทน กรุณาตรวจ ตั้งค่า → อีเมล";

}

namespace Accounting.Helpers;

/// <summary>ผู้รับที่เหตุการณ์ "ตั้งใจ" ส่งถึง — จำแนกจากตัวเหตุการณ์ ไม่ใช่จากตัวบุคคล (คนเดียวเป็นได้ทั้งเจ้าของผู้เช่าและแอดมินแพลตฟอร์ม
/// แต่เขาได้รับ<b>เฉพาะตามบทบาทที่เหตุการณ์กำหนด</b>)</summary>
public enum NotificationAudience
{
    /// <summary>เหตุการณ์ธุรกิจของ<b>ผู้เช่า</b> (เอกสาร · รับเงิน · การจอง · ออเดอร์ · สต็อก · เงินเดือน · OCR · ธนาคาร) — ถึงสมาชิก/ลูกค้าของบริษัทนั้นเท่านั้น</summary>
    TenantBusiness,
    /// <summary>เรื่อง<b>บัญชีของผู้เช่ากับแพลตฟอร์ม</b> (ต่ออายุ · ชำระค่าแพ็กเกจ · ใกล้หมดอายุ · โควตา/ส่วนเกิน · ใบแจ้งหนี้ค่าบริการ · ความปลอดภัยบัญชี · ประกาศระบบ) — ถึงเจ้าของบริษัทนั้นผ่านช่องกลางได้</summary>
    PlatformAccount,
    /// <summary>เรื่องที่<b>ผู้ดูแลแพลตฟอร์ม</b>ต้องรู้ (เงินเข้าไม่ตรง · webhook ล้ม · gateway ดับ · งานกลางคืนพัง · ฟอร์มติดต่อ/แชทหน้าเว็บ NextAcc) — ถึงแอดมินแพลตฟอร์มเท่านั้น</summary>
    PlatformOps,
}

/// <summary>OA ที่ใช้ยิง push</summary>
public enum LineOaKind
{
    /// <summary>OA ของบริษัทผู้เช่า (<c>CompanySettings.LineChannelAccessToken</c>)</summary>
    CompanyOa,
    /// <summary>OA กลางของ NextAcc (<c>SiteSettings.LineMessaging*</c> / config <c>Line:*</c>)</summary>
    NextAccOa,
}

/// <summary>ปลายทางของ push</summary>
public enum LineTargetKind
{
    /// <summary>กลุ่ม LINE ของบริษัท (<c>CompanySettings.LineDefaultGroupId</c>)</summary>
    CompanyGroup,
    /// <summary>userId 1:1 ของสมาชิก/พนักงาน/ลูกค้า<b>ของบริษัทนั้น</b> (ผูกผ่าน webhook ของ OA ที่ใช้ส่ง)</summary>
    TenantMember,
    /// <summary>กลุ่ม LINE กลางของแพลตฟอร์ม (<c>SiteSettings.LineMessagingGroupId</c>) — ห้องของผู้ดูแลระบบ · (ฝ่ายค้าน 232 N3: ไม่มีปลายทาง "แอดมิน 1:1" —
    /// กระดิ่งถึงแอดมินเดินทาง <c>PlatformPaymentAttention</c> ไม่ใช่ LINE · ทางที่ไม่มีผู้เรียกถูกลบ)</summary>
    PlatformGroup,
}

/// <summary>ผลตัดสินเส้นทาง — <see cref="Reason"/> เป็นข้อความถึงผู้ใช้/แอดมิน (ไม่มี userId)</summary>
public sealed record LineRouteDecision(bool Allowed, string RuleCode, string? Reason)
{
    public static LineRouteDecision Ok(string rule) => new(true, rule, null);
    public static LineRouteDecision Deny(string rule, string reason) => new(false, rule, reason);
}

/// <summary>แถวค่าตั้งต้นของตารางแจ้งเตือน (ใช้เมื่อบริษัทยังไม่ได้ตั้งแถวของเหตุการณ์นั้นเลย)</summary>
public sealed record NotificationDefaultRow(string RecipientRole, bool EnableSystem, bool EnableEmail, bool EnableLine, bool EnableLineGroup);

/// <summary>รอบ 232 (คำตัดสิน 232-Notify) — <b>ตัวตัดสินเดียว</b>ว่าเหตุการณ์หนึ่งส่งทาง LINE ไปที่ใครผ่าน OA ไหนได้ · pure
/// <para>ที่มา: OA กลางของแพลตฟอร์มส่ง "✅ เอกสารอนุมัติแล้ว PI-… บริษัท บุญทรัพย์ ถาวร จำกัด" เข้ากลุ่มของเจ้าของแพลตฟอร์ม — เพราะ
/// <c>LineNotifyService.ResolveConfigAsync</c> เดิม "บริษัทไม่มี OA ⇒ ใช้ token + GroupId ของ OA กลาง" ⇒ เหตุการณ์ธุรกิจของผู้เช่าทุกบริษัทที่ไม่ได้ตั้ง OA เอง
/// ไหลเข้าห้องของแอดมินแพลตฟอร์ม</para>
/// <list type="bullet">
/// <item><b>TenantBusiness</b> ส่งได้เฉพาะ "ช่องทางของบริษัทนั้น": OA ของบริษัท (กลุ่ม/1:1) หรือ 1:1 ถึงสมาชิก/ลูกค้าของบริษัทนั้นที่ผูกผ่าน OA กลาง
/// (userId ของ LINE แยกตาม provider — พนักงานผูก "ผูก 123456" ได้กับ OA กลางเท่านั้น จึงเป็นช่องส่วนตัวของเขา ไม่ใช่ช่องของแพลตฟอร์ม) ·
/// <b>ห้าม</b>เข้ากลุ่มกลาง/ถึงแอดมินแพลตฟอร์ม · ไม่มี OA ของบริษัท ⇒ <b>ไม่ถอยไป OA กลาง</b> (ตกไปกระดิ่ง/อีเมล + หน้าตั้งค่าบอกให้ผูก OA)</item>
/// <item><b>PlatformAccount</b> ถึงเจ้าของบริษัทนั้น 1:1 ผ่าน OA กลาง (เรื่องบัญชีของเขา) · ไม่เข้ากลุ่มกลาง · ไม่ผ่าน OA ของร้าน</item>
/// <item><b>PlatformOps</b> ถึงกลุ่มกลางของผู้ดูแลแพลตฟอร์มเท่านั้น (กระดิ่งแอดมินเดินทาง <c>PlatformPaymentAttention</c>) · ไม่ถึงผู้เช่า</item>
/// </list></summary>
public static class NotificationAudiencePolicy
{
    public const string RuleTenantCompanyOa = "NOTIFY-TENANT-COMPANY-OA";
    public const string RuleTenantMemberViaNextAcc = "NOTIFY-TENANT-MEMBER-NEXTACC";
    public const string RuleTenantNoCompanyOa = "NOTIFY-TENANT-NO-COMPANY-OA";
    public const string RuleTenantNotPlatform = "NOTIFY-TENANT-NOT-PLATFORM";
    public const string RuleAccountOwnerViaNextAcc = "NOTIFY-ACCOUNT-OWNER-NEXTACC";
    public const string RuleAccountNotPlatformGroup = "NOTIFY-ACCOUNT-NOT-PLATFORM-GROUP";
    public const string RuleAccountNotCompanyOa = "NOTIFY-ACCOUNT-NOT-COMPANY-OA";
    public const string RuleOpsPlatform = "NOTIFY-OPS-PLATFORM";
    public const string RuleOpsNotTenant = "NOTIFY-OPS-NOT-TENANT";

    /// <summary>ข้อความเมื่อบริษัทยังไม่ผูก OA ของตัวเอง — ประโยคเดียวใช้ทั้งผลส่ง + หน้าตั้งค่า</summary>
    public const string NoCompanyOaMessage =
        "บริษัทยังไม่ได้ผูก LINE OA ของตัวเอง — แจ้งเตือนธุรกิจของบริษัทจึงส่งทางกระดิ่งในแอป/อีเมลแทน (ไม่ส่งผ่าน LINE OA กลางของ NextAcc) · ผูกได้ที่ ตั้งค่า → แจ้งเตือนทาง LINE → ใช้ LINE OA ของร้านเอง";

    public const string TenantNotPlatformMessage =
        "เหตุการณ์ธุรกิจของบริษัทผู้เช่าไม่ส่งเข้ากลุ่ม LINE กลาง/ถึงผู้ดูแลแพลตฟอร์ม (คำตัดสิน 232-Notify)";

    /// <summary>คีย์เหตุการณ์ที่เป็นเรื่องบัญชีของผู้เช่ากับแพลตฟอร์ม — ขึ้นต้นด้วย prefix เหล่านี้</summary>
    private static readonly string[] PlatformAccountPrefixes = { "subscription.", "quota.", "notify.quota.", "overage.", "platform.account." };

    private static readonly string[] PlatformOpsPrefixes = { "platform.ops." };

    /// <summary>จำแนกจากคีย์เหตุการณ์ของ <c>NotificationEvents</c> — ไม่รู้จัก = <see cref="NotificationAudience.TenantBusiness"/> (เข้มสุด: ไม่มีทางเข้ากลุ่มกลาง)</summary>
    public static NotificationAudience Classify(string? eventKey)
    {
        var k = (eventKey ?? "").Trim();
        if (PlatformOpsPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))) return NotificationAudience.PlatformOps;
        if (PlatformAccountPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))) return NotificationAudience.PlatformAccount;
        return NotificationAudience.TenantBusiness;
    }

    /// <summary>ตัดสินเส้นทาง LINE หนึ่งครั้งต่อการยิง — <paramref name="companyOaConfigured"/> = บริษัทเปิด LINE + มี token ที่ถอดได้ (ตัวอ่านคือ <c>LineNotifyService</c>)</summary>
    public static LineRouteDecision LineRoute(NotificationAudience audience, LineOaKind oa, LineTargetKind target, bool companyOaConfigured)
    {
        switch (audience)
        {
            case NotificationAudience.TenantBusiness:
                if (target == LineTargetKind.PlatformGroup)
                    return LineRouteDecision.Deny(RuleTenantNotPlatform, TenantNotPlatformMessage);
                if (oa == LineOaKind.CompanyOa)
                    return companyOaConfigured
                        ? LineRouteDecision.Ok(RuleTenantCompanyOa)
                        : LineRouteDecision.Deny(RuleTenantNoCompanyOa, NoCompanyOaMessage);
                // NextAccOa + TenantMember: ช่องส่วนตัวของสมาชิก/ลูกค้าที่ผูกกับบอทกลางเอง
                return target == LineTargetKind.TenantMember
                    ? LineRouteDecision.Ok(RuleTenantMemberViaNextAcc)
                    : LineRouteDecision.Deny(RuleTenantNoCompanyOa, NoCompanyOaMessage);   // กลุ่มของบริษัทผ่าน OA กลาง = ไม่มี

            case NotificationAudience.PlatformAccount:
                if (oa == LineOaKind.CompanyOa)
                    return LineRouteDecision.Deny(RuleAccountNotCompanyOa, "ข้อความของแพลตฟอร์มไม่ส่งผ่าน LINE OA ของร้าน");
                if (target == LineTargetKind.TenantMember)
                    return LineRouteDecision.Ok(RuleAccountOwnerViaNextAcc);
                return LineRouteDecision.Deny(RuleAccountNotPlatformGroup, "เรื่องบัญชีของผู้เช่าส่งถึงเจ้าของบริษัทนั้นเท่านั้น ไม่เข้ากลุ่ม LINE กลาง");

            default: // PlatformOps
                if (oa == LineOaKind.NextAccOa && target == LineTargetKind.PlatformGroup)
                    return LineRouteDecision.Ok(RuleOpsPlatform);
                return LineRouteDecision.Deny(RuleOpsNotTenant, "เรื่องของผู้ดูแลแพลตฟอร์มไม่ส่งถึงผู้เช่า/ผ่าน OA ของร้าน");
        }
    }

    /// <summary>คีย์เหตุการณ์ที่รอบ 232 ย้ายจาก "ประกาศกลุ่ม LINE แบบไม่มีค่าตั้ง" (<c>LineNotifyService.Notify*Async</c> เดิม) มาเดินเครื่องแจ้งเตือนกลาง —
/// บริษัทที่ยังไม่มีแถวค่าตั้งของคีย์เหล่านี้ได้ <see cref="DefaultMatrix"/></summary>
    public static readonly IReadOnlySet<string> MigratedBroadcastKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "document.approved", "document.payment_received", "ar.invoice_overdue", "bank.sync_completed", "ecommerce.sync_completed",
        "lodging.booking_created", "lodging.slip_uploaded",
    };

    /// <summary>ค่าตั้งต้นเมื่อบริษัทยังไม่ได้ตั้งแถวของคีย์นั้น: คีย์ที่ย้ายมา = กระดิ่งถึงเจ้าของ (+ กลุ่ม LINE ของบริษัทเมื่อบริษัทผูกกลุ่มไว้แล้ว —
/// คงสิ่งที่บริษัทที่ตั้ง OA เองเคยได้รับ) · คีย์อื่น = ว่าง (พฤติกรรมเดิม: ไม่ตั้ง = ไม่ส่ง)</summary>
    public static IReadOnlyList<NotificationDefaultRow> DefaultMatrix(string eventKey, bool companyLineGroupConfigured)
    {
        if (!MigratedBroadcastKeys.Contains(eventKey)) return Array.Empty<NotificationDefaultRow>();
        return new[] { new NotificationDefaultRow("Owner", EnableSystem: true, EnableEmail: false, EnableLine: false, EnableLineGroup: companyLineGroupConfigured) };
    }

    /// <summary>"บริษัทผูกกลุ่ม LINE ของตัวเองไว้แล้ว" — จาก <c>CompanySettings.LineEnabled/LineChannelAccessToken/LineDefaultGroupId</c> (ยังไม่ถอดรหัส token — แค่มีค่า) ·
    /// ผู้อ่าน: <c>NotificationEngine</c> (ค่าตั้งต้น) · <c>NotificationConfigService</c> (แถวตั้งต้นบนหน้า)</summary>
    public static bool CompanyLineGroupConfigured(bool lineEnabled, string? storedToken, string? groupId)
        => lineEnabled && !string.IsNullOrWhiteSpace(storedToken) && !string.IsNullOrWhiteSpace(groupId);

    /// <summary>ป้ายสถานะช่อง "กลุ่ม LINE ของบริษัท" บนหน้าตั้งค่า (เซิร์ฟเวอร์ประกอบ · หน้าเว็บแสดงอย่างเดียว)</summary>
    public static string CompanyGroupNotice(bool companyOaConfigured, bool hasGroupId)
    {
        if (!companyOaConfigured) return NoCompanyOaMessage;
        if (!hasGroupId) return "ผูก LINE OA ของร้านแล้ว แต่ยังไม่ได้ใส่ \"LINE กลุ่มของทีม\" — ช่อง \"กลุ่ม LINE\" ในตารางแจ้งเตือนจะยังไม่ส่ง";
        return "แจ้งเตือนธุรกิจของบริษัทส่งเข้ากลุ่ม LINE ของทีมได้ — เลือกเหตุการณ์ในตารางแจ้งเตือน ช่อง \"กลุ่ม LINE\"";
    }
}

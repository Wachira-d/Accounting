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

/// <summary>
/// **ตัวตัดสินช่องทางแจ้งลูกค้าปลายทาง (LINE/อีเมล) ตัวเดียว** — รอบ 211 · คำตัดสินข้อ 170
/// <list type="bullet">
/// <item>LINE: บริษัทเปิด LINE + มี token ของตัวเอง ⇒ OA ของบริษัท · ไม่งั้น ⇒ OA ของ NextAcc (config <c>Line:*</c>) — <b>กติกาเดียวกับที่ push ใช้อยู่เดิม</b>
///   (<c>LineNotifyService.ResolveConfigAsync</c>) ⇒ ค่าเริ่มต้นไม่เปลี่ยนพฤติกรรมการส่ง</item>
/// <item>userId ของ LINE ผูกกับ OA (provider) ⇒ ลิงก์ที่ผูกกับ OA อื่นใช้ไม่ได้ (<see cref="LinkUsable"/>) — ร้านสลับ OA = ลูกค้าต้องผูกใหม่</item>
/// <item>อีเมล: ร้านตั้ง + ทดสอบผ่าน (<c>CompanySettings.EmailConfigured</c>) ⇒ ผู้ส่งของร้าน · ไม่งั้น ⇒ อีเมลของระบบ (เดิม)</item>
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
        if (!nextAccHasToken) p2 = "ระบบยังไม่ได้ตั้งค่า LINE OA กลาง (Line:ChannelAccessToken) และร้านยังไม่ได้ตั้ง LINE OA ของตัวเอง";
        else if (!nextAccHasSecret || nb == null) p2 = "ระบบยังไม่ได้ตั้งค่า LINE OA กลางให้ผูกได้ (Line:ChannelSecret / Line:BasicId)";
        return new CustomerLineChannel(ScopeNextAcc, nb, nextAccHasToken, p2 == null, p2, nextAccDestination);
    }

    /// <summary>ลิงก์ที่ผูกกับ OA <paramref name="linkScope"/>/<paramref name="linkDestination"/> ใช้ส่งผ่านช่องปัจจุบันได้ไหม ·
    /// ปลายทาง (bot userId จาก webhook <c>destination</c>) ไม่รู้ข้างใดข้างหนึ่ง ⇒ ตัดสินจาก scope อย่างเดียว</summary>
    public static bool LinkUsable(string linkScope, string? linkDestination, string currentScope, string? currentDestination)
        => string.Equals(linkScope, currentScope, StringComparison.Ordinal)
           && (string.IsNullOrEmpty(linkDestination) || string.IsNullOrEmpty(currentDestination)
               || string.Equals(linkDestination, currentDestination, StringComparison.Ordinal));

    /// <summary>ลำดับช่องที่จะลอง (หยุดที่ช่องแรกที่ส่งสำเร็จ)</summary>
    public static IReadOnlyList<string> Order(bool hasEmail, bool hasLine, bool lineOptedIn)
    {
        var list = new List<string>(2);
        if (lineOptedIn && hasLine) list.Add(ChannelLine);
        if (hasEmail) list.Add(ChannelEmail);
        if (!lineOptedIn && hasLine) list.Add(ChannelLine);
        return list;
    }

    /// <summary>อีเมลส่งผ่านใคร — ร้านตั้งค่าและทดสอบผ่านแล้ว ⇒ ของร้าน · ไม่งั้น ⇒ ของระบบ (พฤติกรรมเดิม)</summary>
    public static string EmailSource(bool tenantEmailConfigured) => tenantEmailConfigured ? EmailTenant : EmailSystem;
}

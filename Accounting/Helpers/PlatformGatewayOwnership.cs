namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 ส่วน B ฝ่ายค้าน (S1) — ช่องทางรับชำระของบริษัทผู้ให้บริการ NextAcc (<c>SiteSettings.PlatformCompanyId</c>) แก้ได้จากหน้าแอดมินเท่านั้น · pure
/// <para>ที่มา: หน้าตั้งค่าร้าน (<c>payment-settings</c>) ของบริษัทผู้ให้บริการสลับ live ได้ด้วยด่านร้าน ⇒ ข้ามด่านแพลตฟอร์ม (นโยบายคืนเงินเผยแพร่ + พิมพ์ยืนยัน)</para>
/// </summary>
public static class PlatformGatewayOwnership
{
    public const string TenantSideMessage =
        "ช่องทางรับชำระของบริษัทผู้ให้บริการ NextAcc ตั้งค่าที่หน้าผู้ดูแลระบบ → ช่องทางรับชำระของ NextAcc เท่านั้น";

    /// <summary>null = หน้าร้านแก้ได้ · ข้อความ = บริษัทนี้คือบริษัทผู้ให้บริการ</summary>
    public static string? TenantEndpointRefusal(Guid companyId, Guid? platformCompanyId)
        => platformCompanyId is Guid p && p == companyId ? TenantSideMessage : null;
}

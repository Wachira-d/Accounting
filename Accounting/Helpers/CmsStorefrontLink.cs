using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// <b>ลิงก์หน้าเว็บของร้านถึงลูกค้า — ตัวประกอบเดียว</b> (รอบ 210 · คำตัดสินข้อ 169) · ใช้ในข้อความแจ้งเตือนทุกฉบับที่ถึงลูกค้า
/// (ยืนยัน/ยกเลิก/เลื่อนการจอง · ยืนยันคำสั่งซื้อ · จัดส่งแล้ว · พร้อมให้รับ · ส่งมอบแล้ว)
/// <para><b>ฐาน URL</b> (ลำดับชั้น): โดเมนของร้านที่อนุมัติ + ยืนยันแล้ว (<c>SiteDomain</c> ชนิด custom · ตัวหลักก่อน) ⇒ <c>https://{โดเมน}</c> ·
/// ไม่มี ⇒ โดเมนของระบบ (<c>App:BaseUrl</c>) + <c>/site/{subdomain}</c> (เส้น path-based ของ <c>CmsSiteRoutingMiddleware</c> ซึ่งไม่ต้องใช้ wildcard cert) ·
/// ไม่มีทั้งคู่ ⇒ null — ผู้เรียก<b>ไม่เงียบ</b>: ใส่เลขที่ + บอกให้ไปหน้า "ตรวจสอบการจอง / คำสั่งซื้อ" แทน</para>
/// <para>โดเมนชนิด subdomain (<c>{sub}.nextacc.net</c>) ไม่ใช้ — ยังไม่มี wildcard cert (ลิงก์จะเปิดไม่ได้) · ข้อความเป็น text ล้วน ช่องอีเมล HtmlEncode ทั้งก้อน (กฎ #4 C)</para>
/// </summary>
public static class CmsStorefrontLink
{
    /// <summary>หน้า "ตรวจสอบการจอง / คำสั่งซื้อ" (เลขที่ + เบอร์/อีเมล) บนหน้าร้าน</summary>
    public const string LookupSlug = "check";
    public const string BookingSlugPrefix = "my-booking/";
    public const string OrderSlugPrefix = "track-order/";
    public const string LookupPageName = "ตรวจสอบการจอง / คำสั่งซื้อ";
    /// <summary>หน้า "บัญชีของฉัน" ของสมาชิก (storefront.html slug <c>account</c>) — มีปุ่ม "รับแจ้งเตือนทาง LINE" ของสมาชิก (รอบ 211)</summary>
    public const string AccountSlug = "account";

    private static readonly Regex HostRx = new(@"^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SiteKeyRx = new(@"^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant);

    /// <summary>ฐาน URL ของหน้าร้าน (ไม่มี / ท้าย) · <paramref name="verifiedCustomDomains"/> = โดเมนของร้านที่ผ่านการอนุมัติ+ยืนยันแล้ว เรียงตัวหลักก่อน ·
    /// ค่าที่รูปแบบผิดถูกข้าม (ไม่ประกอบลิงก์เสีย) · ไม่ได้สักทาง ⇒ null</summary>
    public static string? BaseUrl(IEnumerable<string?> verifiedCustomDomains, string? appBaseUrl, string? siteKey)
    {
        foreach (var d in verifiedCustomDomains)
        {
            var host = (d ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (HostRx.IsMatch(host)) return "https://" + host;
        }
        var key = (siteKey ?? "").Trim().ToLowerInvariant();
        if (!SiteKeyRx.IsMatch(key)) return null;
        if (!Uri.TryCreate((appBaseUrl ?? "").Trim(), UriKind.Absolute, out var app)
            || (app.Scheme != Uri.UriSchemeHttps && app.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(app.Query) || !string.IsNullOrEmpty(app.Fragment))
            return null;
        return app.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/site/" + key;
    }

    /// <summary>URL ของหน้าบนหน้าร้าน · ฐาน null = null · ส่วนท้าย (โทเคน) ถูก escape</summary>
    private static string? PageUrl(string? baseUrl, string slugPrefix, string? tail = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        return baseUrl.TrimEnd('/') + "/" + slugPrefix + (string.IsNullOrEmpty(tail) ? "" : Uri.EscapeDataString(tail));
    }

    /// <summary>บรรทัด "จัดการการจอง" ในข้อความถึงลูกค้า — มีลิงก์ ⇒ ลิงก์ my-booking · ไม่มี ⇒ เลขที่ + หน้าตรวจสอบ (ไม่เงียบ)</summary>
    public static string BookingManageLine(string? baseUrl, string? publicToken, string bookingNumber)
        => !string.IsNullOrWhiteSpace(publicToken) && PageUrl(baseUrl, BookingSlugPrefix, publicToken.Trim()) is string url
            ? $"ดู/จัดการการจอง (ชำระมัดจำ · เลื่อน · ยกเลิก): {url}"
            : FallbackLine(baseUrl, "การจอง", bookingNumber);

    /// <summary>บรรทัด "ติดตามคำสั่งซื้อ" ในข้อความถึงลูกค้า — มีลิงก์ ⇒ ลิงก์ track-order · ไม่มี ⇒ เลขที่ + หน้าตรวจสอบ (ไม่เงียบ)</summary>
    public static string OrderTrackLine(string? baseUrl, string? publicToken, string orderNumber)
        => !string.IsNullOrWhiteSpace(publicToken) && PageUrl(baseUrl, OrderSlugPrefix, publicToken.Trim()) is string url
            ? $"ติดตามสถานะคำสั่งซื้อ: {url}"
            : FallbackLine(baseUrl, "คำสั่งซื้อ", orderNumber);

    /// <summary>รอบ 212 แพ็ก B: บรรทัด "ผูก LINE ใหม่" ในคำเชิญ — สมาชิก ⇒ หน้า "บัญชีของฉัน" · การจอง/ออเดอร์ ⇒ หน้าของรายการ (my-booking / track-order ด้วยโทเคน) ·
    /// ไม่มีลิงก์ ⇒ บอกทางไปบนเว็บของร้าน (ไม่เงียบ)</summary>
    public static string RelinkLine(string? baseUrl, string targetKind, string? publicToken)
    {
        string? url = targetKind switch
        {
            CustomerLineLinkRule.KindMember => PageUrl(baseUrl, AccountSlug),
            CustomerLineLinkRule.KindBooking when !string.IsNullOrWhiteSpace(publicToken) => PageUrl(baseUrl, BookingSlugPrefix, publicToken.Trim()),
            CustomerLineLinkRule.KindOrder when !string.IsNullOrWhiteSpace(publicToken) => PageUrl(baseUrl, OrderSlugPrefix, publicToken.Trim()),
            _ => null,
        };
        if (url != null) return url;
        var where = PageUrl(baseUrl, LookupSlug) is string lookup ? lookup : "เว็บไซต์ของร้าน";
        return targetKind == CustomerLineLinkRule.KindMember
            ? $"หน้า \"บัญชีของฉัน\" บน{where}"
            : $"หน้าการจอง/คำสั่งซื้อของคุณ (ค้นได้ที่หน้า \"{LookupPageName}\" {where})";
    }

    private static string FallbackLine(string? baseUrl, string what, string number)
    {
        var where = PageUrl(baseUrl, LookupSlug) is string lookup
            ? $"หน้า \"{LookupPageName}\" {lookup}"
            : $"หน้า \"{LookupPageName}\" บนเว็บไซต์ของร้าน";
        return $"ตรวจสอบ{what}ได้ที่{where} ด้วยเลขที่ {number} และเบอร์โทรหรืออีเมลที่ใช้";
    }
}

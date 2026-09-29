using System.Net;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ค่า <c>src</c> ของ &lt;img&gt; ใน HTML renderer (<c>PdfGenerationService</c>) — ตัวตัดสินเดียว (รอบ 200 ทีม R · G2-02)
///
/// ═══ ที่มา ═══
/// renderer ต่อ URL โลโก้ (<c>DocumentBrand.LogoUrl</c> ที่ผู้ใช้กรอกเอง) · URL ตราประทับ · ลายเซ็นผู้จัดทำที่<b>คู่ค้าส่งมาทาง API</b>
/// (<c>Document.PreparerSignatureBase64</c>) เข้า <c>src='…'</c> ดิบ ⇒ ค่า <c>x' onerror='…</c> แตก attribute แล้วสคริปต์รันในหน้าแอป
/// ตอนผู้ใช้เปิดพรีวิว (<c>documents.html</c> ใส่ HTML ลง <c>innerHTML</c> · CSP เปิด <c>unsafe-inline</c>) ⇒ ขโมย JWT ใน localStorage
///
/// ═══ กติกา ═══
/// รับเฉพาะ (1) <c>data:image/&lt;ชนิดภาพ&gt;;base64,&lt;base64 ล้วน&gt;</c> (2) URL <c>https://</c>/<c>http://</c> (3) path ภายในที่ขึ้นต้น <c>/</c>
/// (ไม่ใช่ <c>//</c>) — อย่างอื่น (<c>javascript:</c> · <c>data:text/html</c> · ข้อความที่มี quote/วงเล็บเหลี่ยม) ⇒ <c>null</c> = ไม่พิมพ์รูป
/// (มองเห็นได้: รูปหายจากกระดาษ ผู้ใช้แก้ค่าที่ตั้งไว้ได้) · ค่าที่ผ่านถูก HtmlEncode เสมอ (กัน <c>'</c> ปิด attribute)</summary>
public static class HtmlImageSource
{
    private static readonly Regex DataUri = new(
        @"^data:image/(png|jpe?g|gif|webp|bmp|svg\+xml);base64,[A-Za-z0-9+/=\r\n ]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>true = ใส่เป็น src ของ &lt;img&gt; ได้ (ยังต้องผ่าน <see cref="Attribute"/> เพื่อ encode)</summary>
    public static bool IsAllowed(string? src)
    {
        var s = src?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return DataUri.IsMatch(s);
        // ห้ามอักขระที่ไม่มีใน URL จริง — กันการต่อ attribute/แท็กแม้หลัง encode จะปลอดภัยแล้วก็ตาม
        if (s.IndexOfAny(new[] { '\'', '"', '<', '>', '`', ' ', '\t', '\r', '\n' }) >= 0) return false;
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return true;
        return s.StartsWith('/') && !s.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>ค่าที่พร้อมวางใน <c>src='…'</c> (HtmlEncode แล้ว) หรือ <c>null</c> เมื่อไม่ผ่าน <see cref="IsAllowed"/></summary>
    public static string? Attribute(string? src)
        => IsAllowed(src) ? WebUtility.HtmlEncode(src!.Trim()) : null;
}

namespace Accounting.Helpers;

/// <summary>รอบ 249 — ลิงก์ในแจ้งเตือนที่ออกนอกแอป (LINE · อีเมล) ต้องเป็น URL เต็ม · pure · ตัวตั้งตัวเดียว
/// <para>ที่มา (เจ้าของรายงาน 2026-11-06 · บริษัท MANGKORN): ข้อความ "💵 รับชำระเงินแล้ว" ในกลุ่ม LINE ลงท้าย
/// "🔗 /pages/documents.html?id=…" — path สัมพัทธ์ของกระดิ่งในแอป ⇒ ใน LINE/อีเมลกดไม่ได้ (ไม่มีโดเมน)</para>
/// <list type="bullet">
/// <item><paramref name="origin"/> = ผลของ <see cref="SystemBaseUrl.ResolveAsync"/> (ค่าที่ผู้ดูแลตั้ง → <c>App:BaseUrl</c>) — ตัดสิน "ตั้งแล้วจริง" ด้วย
/// <see cref="SystemBaseUrl.IsConfigured"/> ตัวเดียว (ค่าที่มากับซอร์ส app.nextacc.com ไม่ใช่โดเมนของเรา ≠ ตั้งแล้ว)</item>
/// <item>ไม่รู้โดเมน ⇒ คืน null = <b>ไม่ใส่บรรทัดลิงก์</b> (ลิงก์ที่กดไม่ได้/พาไปโดเมนคนอื่น แย่กว่าไม่มีลิงก์ — F2 ข้อ 3)</item>
/// <item>รับเฉพาะ path ภายใน ("/…") หรือ URL เต็ม http(s) · "//host" (protocol-relative) / "javascript:" / scheme อื่น ⇒ null</item>
/// </list>
/// กระดิ่งในแอปยังเก็บ path สัมพัทธ์เดิม (นำทางในแอป) — helper นี้ใช้เฉพาะช่องที่ออกนอกแอป</summary>
public static class NotificationLink
{
    public static string? Absolute(string? origin, string? actionUrl)
    {
        var url = (actionUrl ?? "").Trim();
        if (url.Length == 0) return null;

        // URL เต็มอยู่แล้ว — รับเฉพาะ http/https
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && !url.StartsWith("/", StringComparison.Ordinal))
            return abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp ? url : null;

        // path ภายในเท่านั้น — "//evil.example" คือ URL ไปโดเมนอื่น ไม่ใช่ path
        if (!url.StartsWith("/", StringComparison.Ordinal) || url.StartsWith("//", StringComparison.Ordinal) || url.Contains('\\'))
            return null;

        if (!SystemBaseUrl.IsConfigured(null, origin)) return null;
        var o = origin!.Trim().TrimEnd('/');
        if (!Uri.TryCreate(o, UriKind.Absolute, out var b)
            || (b.Scheme != Uri.UriSchemeHttps && b.Scheme != Uri.UriSchemeHttp))
            return null;
        return o + url;
    }
}

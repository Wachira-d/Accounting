namespace Accounting.Helpers;

/// <summary>
/// **ขอบเขตสิทธิ์ของ API key (`/api/v1`) — ตัวตัดสินเดียว** (รอบ 255 WP-12B ข้อ 6 · A H-06 · DECISIONS 240-8 ทางไปต่อ) · pure
/// <para>scope ตรงตัว · <c>*</c> · <c>{หมวด}:*</c> · และ <b><c>{หมวด}:write</c> ครอบ <c>{หมวด}:read</c></b> (สัญญา v1 ต้องนิ่ง — คีย์เดิมที่มี <c>documents:write</c>
/// อ่านสถานะ/PDF ได้ต่อ) · ทิศตรงข้าม: <c>documents:read</c> <b>ไม่</b>ครอบการเขียน (คีย์อ่านอย่างเดียวสร้าง/อนุมัติเอกสารไม่ได้)</para>
/// </summary>
public static class ApiScopePolicy
{
    public const string DocumentsRead = "documents:read";

    public static bool Satisfies(string? granted, string? required)
    {
        if (string.IsNullOrWhiteSpace(granted) || string.IsNullOrWhiteSpace(required)) return false;
        var parts = granted.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var colon = required.IndexOf(':');
        var domain = colon > 0 ? required[..colon] : required;
        var isRead = colon > 0 && required[(colon + 1)..].Equals("read", StringComparison.OrdinalIgnoreCase);
        return parts.Any(p =>
            p.Equals(required, StringComparison.OrdinalIgnoreCase)
            || p.Equals("*", StringComparison.Ordinal)
            || p.Equals($"{domain}:*", StringComparison.OrdinalIgnoreCase)
            || (isRead && p.Equals($"{domain}:write", StringComparison.OrdinalIgnoreCase)));
    }
}

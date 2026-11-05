namespace Accounting.Helpers;

/// <summary>
/// **กติกา URL เรียกกลับ (webhook ต่อเอกสาร) ที่ระบบต้นทางแนบมาตอนสร้างเอกสาร/ขอลิงก์เซ็น — ตัวตั้งเดียว** (รอบ 240 · คำตัดสิน 240-2)
///
/// <para>ชั้นที่ 1 (ตอนรับค่า): รูปแบบผ่าน <see cref="OutboundUrlGuard.CheckFormat"/> (https เท่านั้น · พอร์ต 443/8443 · IP ตรงต้องเป็นสาธารณะ) ·
/// ยาว ≤ <see cref="MaxUrlLength"/> · ห้ามมี user:pass@ ใน URL (ความลับรั่วลง log/หน้าจอ) · บริษัทตั้งรายชื่อโฮสต์ที่อนุญาตได้
/// (<c>CompanySettings.SignCallbackAllowedHosts</c> · ว่าง = โฮสต์สาธารณะใดก็ได้) ·
/// ชั้นที่ 2 (ตอนส่งจริงในงานเบื้องหลัง): <see cref="OutboundUrlGuard.CheckResolvedAsync"/> + การเชื่อมต่อที่ตรวจ IP จริงทุกครั้ง
/// (<c>OutboundUrlGuard.ConnectPublicOnlyAsync</c> — กัน DNS rebinding) · ไม่ตาม redirect</para>
///
/// <para>ตัดสินใจ: <b>ไม่</b>เปิด http ในโหมด Development (OutboundUrlGuard เป็นตัวตั้งเดียวของ URL ขาออก — ทดสอบผ่าน tunnel ที่เป็น https ได้)</para>
/// </summary>
public static class SignCallbackUrlRule
{
    public const int MaxUrlLength = 500;
    public const int MaxRefLength = 200;
    public const int MaxAllowedHosts = 20;

    /// <summary>เหตุที่รับ URL นี้ไม่ได้ (null = ใช้ได้ หรือไม่ได้ส่งมา)</summary>
    public static string? Problem(string? url, string? allowedHostsCsv)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        if (u.Length > MaxUrlLength) return $"callbackUrl ยาวเกิน {MaxUrlLength} ตัวอักษร";
        var fmt = OutboundUrlGuard.CheckFormat(u);
        if (!fmt.Ok) return "callbackUrl: " + fmt.Reason;
        var uri = new Uri(u);
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "callbackUrl ห้ามมีชื่อผู้ใช้/รหัสผ่านใน URL — ใช้ลายเซ็น HMAC ตรวจที่ปลายทางแทน";
        var hosts = ParseHosts(allowedHostsCsv);
        if (hosts.Count > 0 && !HostAllowed(uri.Host, hosts))
            return $"callbackUrl: โฮสต์ {uri.Host} ไม่อยู่ในรายชื่อที่บริษัทอนุญาต (ตั้งค่า → ลิงก์ให้ลูกค้าเซ็น → โฮสต์ที่รับ webhook)";
        return null;
    }

    /// <summary>รหัสอ้างอิงของระบบต้นทาง (ส่งกลับใน payload) — ตัดอักขระควบคุม/ความยาว · ว่าง = null</summary>
    public static string? CleanRef(string? callbackRef) => QuotationAcceptance.CleanText(callbackRef, MaxRefLength);

    /// <summary>รายชื่อโฮสต์ (คั่นด้วย , ; ช่องว่าง หรือขึ้นบรรทัด) → ตัวพิมพ์เล็ก ไม่ซ้ำ · รองรับ "*.example.com" = โดเมนย่อย</summary>
    private static IReadOnlyList<string> ParseHosts(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => h.ToLowerInvariant().TrimEnd('.'))
                .Distinct(StringComparer.Ordinal)
                .ToList();

    /// <summary>ค่าที่บันทึกได้ไหม (null = ได้) — แต่ละตัวต้องเป็นชื่อโฮสต์ (ไม่ใช่ URL/IP ภายใน)</summary>
    public static string? HostsProblem(string? csv)
    {
        var hosts = ParseHosts(csv);
        if (hosts.Count > MaxAllowedHosts) return $"ใส่ได้ไม่เกิน {MaxAllowedHosts} โฮสต์";
        foreach (var h in hosts)
        {
            var bare = h.StartsWith("*.", StringComparison.Ordinal) ? h[2..] : h;
            if (bare.Length == 0 || bare.Length > 253 || Uri.CheckHostName(bare) != UriHostNameType.Dns || !bare.Contains('.'))
                return $"\"{h}\" ไม่ใช่ชื่อโฮสต์ที่ใช้ได้ (ตัวอย่าง: erp.example.com หรือ *.example.com)";
        }
        return null;
    }

    public static string? NormalizeHosts(string? csv)
    {
        var hosts = ParseHosts(csv);
        return hosts.Count == 0 ? null : string.Join(",", hosts);
    }

    /// <summary>โฮสต์ตรงรายชื่อ — ตรงตัว หรือเป็นโดเมนย่อยของรายการ "*.x" (ไม่นับ "x" เอง และไม่นับ "evilx")</summary>
    private static bool HostAllowed(string host, IReadOnlyList<string> allowed)
    {
        var h = host.ToLowerInvariant().TrimEnd('.');
        foreach (var a in allowed)
        {
            if (a.StartsWith("*.", StringComparison.Ordinal))
            {
                if (h.EndsWith(a[1..], StringComparison.Ordinal) && h.Length > a.Length - 1) return true;
            }
            else if (string.Equals(h, a, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

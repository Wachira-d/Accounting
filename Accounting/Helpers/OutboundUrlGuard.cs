using System.Net;
using System.Net.Sockets;

namespace Accounting.Helpers;

/// <summary>
/// **ด่านตรวจ URL ปลายทางของคำขอที่เซิร์ฟเวอร์ยิงออกไปเอง — ตัวเดียวของระบบ**
///
/// ═══ ที่มา (บั๊กจริง · ผลตรวจ F-04) ═══
/// ผู้เช่าตั้ง webhook URL อะไรก็ได้แล้วเซิร์ฟเวอร์ POST ไปให้ โดย
/// <b>ไม่ตรวจ scheme / host / private IP / redirect</b> และคืน
/// <c>HttpStatusCode</c> + เวลาที่ใช้ + ข้อความ error กลับให้ผู้เช่าอ่าน ⇒
/// <list type="bullet">
/// <item>ชี้ไป <c>169.254.169.254</c> (metadata endpoint ของ cloud) เพื่อดึง
///   credential ของเครื่อง</item>
/// <item>ชี้ไป <c>localhost:5432</c> / โฮสต์ภายใน VPC แล้วใช้ status + เวลา
///   ที่คืนมาเป็น <b>oracle สแกนพอร์ต</b>จากข้างในเครือข่าย</item>
/// <item>POST เข้า endpoint ภายในที่ไม่มี auth ได้ตรง ๆ</item>
/// </list>
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item><b>https เท่านั้น</b> — http ธรรมดาส่ง secret ผ่านเน็ตแบบเปิด และเป็น
///   ทางเดียวที่จะคุยกับพอร์ตภายในส่วนใหญ่</item>
/// <item><b>ตรวจตอนสมัคร และตรวจซ้ำตอนส่งจริง</b> — โดเมนที่ตอนสมัครชี้ไป IP
///   สาธารณะ ตอนส่งอาจชี้ไป 127.0.0.1 แล้ว (DNS rebinding) ⇒ ด่านตอนสมัคร
///   อย่างเดียวคือด่านที่หลอกได้</item>
/// <item><b>ปฏิเสธทุก IP ที่ไม่ใช่สาธารณะ</b> — loopback · private · link-local
///   (ครอบ metadata endpoint) · CGNAT · multicast · unspecified · IPv6
///   ที่ map v4 มา (<c>::ffff:127.0.0.1</c> เป็นวิธีเลี่ยงยอดนิยม)</item>
/// </list>
/// </summary>
public static class OutboundUrlGuard
{
    /// <summary>ผลตรวจ — <c>Ok=false</c> พร้อมเหตุผลภาษาไทยที่โชว์ให้ผู้ใช้ได้</summary>
    public readonly record struct Result(bool Ok, string? Reason)
    {
        public static Result Pass { get; } = new(true, null);
        public static Result Fail(string reason) => new(false, reason);
    }

    /// <summary>ตรวจรูปแบบ URL (ไม่แตะ DNS) — ใช้ตอนรับค่าจากฟอร์ม</summary>
    public static Result CheckFormat(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Result.Fail("กรุณาระบุ URL ปลายทาง");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Result.Fail("URL ไม่ถูกต้อง");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return Result.Fail("รองรับเฉพาะ https (http ธรรมดาส่งข้อมูลแบบไม่เข้ารหัส)");
        if (uri.IsDefaultPort == false && uri.Port is not (443 or 8443))
            return Result.Fail("รองรับเฉพาะพอร์ต 443 หรือ 8443");
        if (string.IsNullOrWhiteSpace(uri.Host))
            return Result.Fail("URL ไม่มีชื่อโฮสต์");

        // โฮสต์ที่เป็น IP ตรง ๆ ตรวจได้ทันทีโดยไม่ต้อง resolve
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !IsPublic(literal))
            return Result.Fail($"ปลายทาง {literal} เป็นที่อยู่ภายใน — ไม่อนุญาต");

        return Result.Pass;
    }

    /// <summary>ตรวจซ้ำตอนจะส่งจริง — resolve DNS แล้วดูว่าทุก IP ที่ได้เป็นสาธารณะ
    /// (กัน DNS rebinding: ชื่อเดิม ตอบ IP ใหม่)</summary>
    public static async Task<Result> CheckResolvedAsync(string? url, CancellationToken ct = default)
    {
        var format = CheckFormat(url);
        if (!format.Ok) return format;

        var uri = new Uri(url!);
        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out _)) return Result.Pass;   // ตรวจไปแล้วใน CheckFormat

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (Exception ex)
        {
            return Result.Fail($"หาที่อยู่ของ {host} ไม่ได้: {ex.Message}");
        }

        if (addresses.Length == 0)
            return Result.Fail($"หาที่อยู่ของ {host} ไม่ได้");

        // ต้องสาธารณะ **ทุกตัว** — ตัวเดียวที่เป็นภายในก็พอให้ยิงเข้าไปได้แล้ว
        foreach (var ip in addresses)
            if (!IsPublic(ip))
                return Result.Fail($"ปลายทาง {host} ชี้ไปที่อยู่ภายใน ({ip}) — ไม่อนุญาต");

        return Result.Pass;
    }

    /// <summary>IP นี้เป็นที่อยู่สาธารณะที่ยิงออกไปได้ไหม</summary>
    public static bool IsPublic(IPAddress ip)
    {
        // IPv6 ที่ห่อ IPv4 ไว้ (::ffff:127.0.0.1) — คลี่ก่อนเสมอ ไม่งั้นด่าน v4 ถูกข้าม
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return false;                                   // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;      // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return false;                   // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return false;                   // 169.254.0.0/16 — รวม metadata endpoint
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;     // 100.64.0.0/10 CGNAT
            if (b[0] == 127) return false;                                  // 127.0.0.0/8
            if (b[0] == 0) return false;                                    // 0.0.0.0/8
            if (b[0] >= 224) return false;                                  // multicast + reserved
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                        // fc00::/7 unique-local
            return true;
        }

        return false;   // ตระกูลอื่นไม่รู้จัก = ไม่อนุญาต
    }
}

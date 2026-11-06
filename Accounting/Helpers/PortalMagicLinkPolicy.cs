using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **พอร์ทัลลูกค้าแบบไม่ต้องจำรหัสผ่าน — ลิงก์วิเศษทางอีเมล** (รอบ 255 WP-12B ข้อ 8 · AUTOPILOT_BILLING_DESIGN §8.1) · pure
/// <para>ลูกค้าที่ไม่ใช้ NextAcc ขอลิงก์ด้วยอีเมลที่<b>ผู้ขายเปิดสิทธิ์พอร์ทัลไว้แล้ว</b> (<c>PortalAccess</c> ใช้งานอยู่ — ผู้ขายเป็นผู้ให้สิทธิ์ ไม่ใช่ลูกค้าสมัครเอง) ·
/// คำตอบเหมือนกันทุกกรณี (<see cref="AckMessage"/> — ไม่บอกว่าอีเมลนี้มีสิทธิ์ไหม) · โทเคนสุ่ม 32 ไบต์ เก็บเป็น SHA-256 · อายุ <see cref="Ttl"/> · ใช้ครั้งเดียว ·
/// เพดานการขอต่ออีเมล/ต่อบริษัท (ตัวนับในฐาน) · แลกแล้วได้เซสชันพอร์ทัลเดิม (โทเคนลงนามแบบเดียวกับล็อกอินด้วยรหัสผ่าน) · รหัสผ่านเดิมยังใช้ได้</para>
/// </summary>
public static class PortalMagicLinkPolicy
{
    public const string RuleCode = "PORTAL-MAGIC-LINK";
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    /// <summary>เพดานต่อ (บริษัท + อีเมล): ต่อนาที / ต่อวัน</summary>
    public const int PerEmailPerMinute = 1;
    public const int PerEmailPerDay = 5;
    /// <summary>รอบ 255 ฝ่ายค้าน M7: เพดานต่อ IP (กันไล่อีเมลจำนวนมาก) — แทนถังต่อบริษัทเดิม (fail-closed ทั้งบริษัท ⇒ คนนอกยิง 10 ครั้ง/นาที
    /// ก็ทำให้ลูกค้าจริงทุกคนของผู้ขายขอลิงก์ไม่ได้ = DoS ต่อผู้ขาย)</summary>
    public const int PerIpPerMinute = 5;
    public const int PerIpPerDay = 60;
    /// <summary>เพดานแลกลิงก์: ต่อ IP / ต่อโทเคน (ต่อนาที · ต่อวัน)</summary>
    public const int RedeemPerIpPerMinute = 10;
    public const int RedeemPerIpPerDay = 200;
    public const int RedeemPerTokenPerMinute = 3;
    public const int RedeemPerTokenPerDay = 10;

    /// <summary>รอบ 255 ฝ่ายค้าน M6: อีเมลลิงก์วิเศษเข้าคิว <c>EmailQueue</c> (ผู้ส่งในนามบริษัท + โควตาเดิม) — โทเคนออกตอนส่ง (คิว/บันทึกไม่เก็บ token)</summary>
    public const string QueueEntityType = "PortalMagicLink";
    public const string LinkPlaceholder = "{PortalMagicLink}";
    /// <summary>คำขอที่ค้างในคิวนานกว่านี้ ⇒ ข้าม (ผู้ขอเลิกรอแล้ว · ขอใหม่ได้)</summary>
    public static readonly TimeSpan QueueStaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>คีย์ตัวนับ (ค่าที่ถูก hash — ไม่เก็บอีเมล/โทเคน/IP ดิบในตารางตัวนับ)</summary>
    public static string EmailBucket(Guid companyId, string? normalizedEmail) => $"portal-magic:{companyId:N}:{Hash(normalizedEmail ?? "")[..16]}";
    public static string IpBucket(string scope, string? ip) => $"portal-magic-{scope}-ip:{Hash(ip ?? "unknown")[..16]}";
    public static string TokenBucket(string? token) => $"portal-magic-redeem-tk:{Hash(token ?? "")[..16]}";

    /// <summary>ข้ามแถวคิวตอนส่งไหม · null = ส่ง</summary>
    public static string? QueueSkipReason(bool accessActive, DateTime queuedAtUtc, DateTime nowUtc)
        => !accessActive ? "สิทธิ์พอร์ทัลถูกปิดแล้ว — ไม่ส่งลิงก์"
            : nowUtc - queuedAtUtc > QueueStaleAfter ? "คำขอลิงก์ค้างในคิวนานเกิน — ไม่ส่ง (ผู้ขอขอใหม่ได้)" : null;

    public const string AckMessage = "ถ้าอีเมลนี้มีสิทธิ์เข้าพอร์ทัลลูกค้าของบริษัทนี้ ระบบส่งลิงก์เข้าสู่ระบบให้แล้ว — ลิงก์ใช้ได้ 15 นาทีและครั้งเดียว · ไม่ได้รับ ให้ติดต่อผู้ขาย";
    public const string InvalidMessage = "ลิงก์เข้าสู่ระบบไม่ถูกต้อง หมดอายุ หรือถูกใช้ไปแล้ว — ขอลิงก์ใหม่ที่หน้าเข้าสู่ระบบ";
    public const string RateMessage = "ขอลิงก์ถี่เกินไป — รอสักครู่แล้วลองใหม่";

    /// <summary>โทเคนใหม่ (base64url ของ 32 ไบต์สุ่ม = 43 ตัวอักษร)</summary>
    public static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? ""))).ToLowerInvariant();

    /// <summary>รูปโทเคนถูกไหม (กันค้นฐานด้วยค่าขยะ · รูปผิด = "ไม่ถูกต้อง" เหมือนโทเคนผิด)</summary>
    public static bool LooksValid(string? token)
        => token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    /// <summary>อีเมลที่ใช้ค้น (ตัดช่องว่าง · ตัวพิมพ์เล็ก) · รูปผิด = null (ตอบ Ack เหมือนเดิม ไม่ส่ง)</summary>
    public static string? NormalizeEmail(string? email)
    {
        var e = (email ?? "").Trim().ToLowerInvariant();
        return e.Length is < 3 or > 254 || !e.Contains('@') || e.StartsWith('@') || e.EndsWith('@') ? null : e;
    }

    /// <summary>แลกได้ไหม · null = ได้ · ข้อความ = ไม่ได้ (ข้อความเดียวทุกเหตุ — ไม่บอกว่าหมดอายุหรือถูกใช้แล้ว)</summary>
    public static string? RedeemProblem(DateTime expiresAtUtc, DateTime? usedAtUtc, bool accessActive, DateTime nowUtc)
        => usedAtUtc != null || nowUtc >= expiresAtUtc || !accessActive ? InvalidMessage : null;

    /// <summary>ลิงก์ในอีเมล — ต้องมี URL ของระบบ (ไม่แต่งโดเมน · ไม่รู้ = ไม่ส่ง)</summary>
    public static string? Link(string? baseUrl, Guid companyId, string token)
        => string.IsNullOrWhiteSpace(baseUrl) ? null
            : $"{baseUrl.TrimEnd('/')}/portal.html?company={companyId}&magic={Uri.EscapeDataString(token)}";
}

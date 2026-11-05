using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **ลายเซ็น HMAC-SHA256 ของ webhook ขาออก — ฟังก์ชัน canonical ตัวเดียว ทั้งฝั่งเขียนและฝั่งตรวจ** (รอบ 240 · กฎ #4 C "Hash/signature มี canonical
/// function เดียว" · คำตัดสิน 240-3)
///
/// <para>สองหัว:
/// (1) <see cref="HeaderName"/> <c>X-NextAcc-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;</c> — HMAC ของ <c>"{t}.{body}"</c> ⇒ ผู้รับตรวจได้ทั้ง "มาจากเรา"
/// และ "ไม่ใช่ของเก่าที่ถูกยิงซ้ำ" (เวลาอยู่ในลายเซ็น · ผู้รับปฏิเสธเมื่อห่างเกิน <see cref="DefaultToleranceSeconds"/>) ·
/// (2) <see cref="LegacyHeaderName"/> <c>sha256=&lt;hex&gt;</c> — HMAC ของ body ล้วน (สัญญาเดิมของ <c>WebhookService</c> ก่อนรอบ 240 · คงไว้ให้ผู้เชื่อมต่อเดิมไม่พัง)</para>
///
/// <para>สูตรสำหรับผู้เชื่อมต่อ (เอกสาร DOCUMENT_FLOW §2.12f): <c>expected = hex(HMAC_SHA256(secret, t + "." + rawBody))</c> · เทียบแบบเวลาคงที่ ·
/// <c>|now − t| ≤ 300</c> วินาที · กันซ้ำด้วย <c>eventId</c> ใน payload/หัว <c>X-NextAcc-Event-Id</c></para>
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-NextAcc-Signature";
    public const string LegacyHeaderName = "X-Webhook-Signature";
    public const string EventHeader = "X-NextAcc-Event";
    public const string EventIdHeader = "X-NextAcc-Event-Id";
    public const int DefaultToleranceSeconds = 300;

    /// <summary>HMAC-SHA256 hex ตัวพิมพ์เล็ก — แกนเดียวของทุกหัว</summary>
    private static string HmacHex(string secret, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
    }

    /// <summary>ข้อความที่ถูกเซ็นของหัวแบบมีเวลา</summary>
    private static string SignedPayload(long unixSeconds, string body) => $"{unixSeconds}.{body}";

    /// <summary>ค่าหัว <see cref="HeaderName"/></summary>
    public static string Sign(string secret, long unixSeconds, string body)
        => $"t={unixSeconds},v1={HmacHex(secret, SignedPayload(unixSeconds, body))}";

    /// <summary>ค่าหัว <see cref="LegacyHeaderName"/> (body ล้วน — ไม่กันการยิงซ้ำ)</summary>
    public static string LegacySign(string secret, string body) => $"sha256={HmacHex(secret, body)}";

    /// <summary>ตัวตรวจคู่กัน (ผู้รับ/เทสต์ใช้) — รูปหัวถูก · เวลาไม่ห่างเกิน · v1 ตัวใดตัวหนึ่งตรง (รองรับช่วงเปลี่ยนรหัสลับ) · เทียบเวลาคงที่</summary>
    public static bool Verify(string secret, string? header, string body, long nowUnixSeconds, int toleranceSeconds = DefaultToleranceSeconds)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(header)) return false;
        long? t = null;
        var sigs = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var val = part[(eq + 1)..];
            if (key == "t" && long.TryParse(val, out var parsed)) t = parsed;
            else if (key == "v1" && val.Length == 64) sigs.Add(val.ToLowerInvariant());
        }
        if (t == null || sigs.Count == 0) return false;
        if (Math.Abs(nowUnixSeconds - t.Value) > toleranceSeconds) return false;
        var expected = Encoding.ASCII.GetBytes(HmacHex(secret, SignedPayload(t.Value, body)));
        return sigs.Any(s => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(s)));
    }

    /// <summary>รหัสลับใหม่ (32 ไบต์สุ่ม · hex 64 ตัว · ขึ้นต้น "whsec_" ให้ผู้ใช้แยกออกจาก API key)</summary>
    public static string NewSecret() => "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

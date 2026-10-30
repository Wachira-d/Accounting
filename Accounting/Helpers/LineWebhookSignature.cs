using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตรวจลายเซ็น webhook ของ LINE Messaging API ตัวเดียวของระบบ** (รอบ 211 · กฎเหล็ก #4 C "hash/signature มี canonical function เดียว")
///
/// <para>LINE เซ็น <c>X-Line-Signature</c> = Base64(HMAC-SHA256(channelSecret, body ดิบเป็น UTF-8)) ·
/// เดิมสูตรนี้อยู่ใน <c>LineBotService.VerifySignature</c> (OA ของ NextAcc) ที่เดียว — รอบ 211 เพิ่ม webhook ต่อบริษัท
/// (<c>CustomerLineWebhookController</c>) ⇒ ย้ายสูตรมาที่นี่แล้วให้ทั้งสองทางเข้าเรียกตัวนี้ (ห้ามเขียน HMAC ซ้ำที่อื่น)</para>
///
/// <para>ไม่มี secret / ไม่มีลายเซ็น ⇒ <c>false</c> เสมอ (ไม่มี "ข้ามการตรวจเมื่อยังไม่ตั้งค่า") · เทียบแบบ constant-time</para>
/// </summary>
public static class LineWebhookSignature
{
    /// <summary>ลายเซ็นที่ควรเป็นของ body นี้ (Base64) — ใช้ในเทสต์/เครื่องมือ · ฝั่งตรวจใช้ <see cref="Verify"/></summary>
    public static string Compute(string channelSecret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(channelSecret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body ?? "")));
    }

    /// <summary>true เฉพาะเมื่อมี secret + ลายเซ็น และลายเซ็นตรงกับ body ดิบ (constant-time)</summary>
    public static bool Verify(string? channelSecret, string? body, string? headerSignature)
    {
        if (string.IsNullOrEmpty(channelSecret) || string.IsNullOrEmpty(headerSignature)) return false;
        var expected = Compute(channelSecret, body ?? "");
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(headerSignature));
    }
}

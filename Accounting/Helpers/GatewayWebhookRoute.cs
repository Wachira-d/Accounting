using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของ config หนึ่งที่ตัวเลือกปลายทาง webhook ต้องใช้ (ตัดจาก <c>PaymentProviderConfig</c>)</summary>
/// <param name="ConfigId">id ของ config</param>
/// <param name="WebhookToken">token ลับต่อ config (null = แถวที่ยังไม่ได้ออก token)</param>
/// <param name="LastTokenWebhookAt">ครั้งล่าสุดที่ webhook มาทาง URL ใหม่ (มี token)</param>
/// <param name="LastTokenWebhookMode">โหมดของ config ตอนที่ webhook ทาง URL ใหม่ครั้งล่าสุดผ่านการยืนยัน — แดชบอร์ดโหมดทดสอบกับใช้งานจริง
/// ของผู้ให้บริการตั้ง URL แยกกัน ⇒ "ย้ายแล้ว" ต้องนับต่อโหมด</param>
/// <param name="CurrentMode">โหมดปัจจุบันของ config</param>
public readonly record struct GatewayWebhookConfigFacts(Guid ConfigId, string? WebhookToken, DateTime? LastTokenWebhookAt,
    PaymentProviderMode? LastTokenWebhookMode = null, PaymentProviderMode CurrentMode = PaymentProviderMode.Test)
{
    /// <summary>แดชบอร์ดของโหมดปัจจุบันส่งมาทาง URL ใหม่แล้ว (ได้รับทาง URL ใหม่ในโหมดนี้อย่างน้อยหนึ่งครั้ง)</summary>
    public bool MovedToTokenUrl => LastTokenWebhookAt != null && LastTokenWebhookMode == CurrentMode;
}

/// <summary>
/// **ปลายทาง webhook ของผู้ให้บริการรับชำระเงิน — token ลับต่อ config** (รอบ 201 ทีม GW · A-GW1 · team-G G8-5)
///
/// <para>═══ ที่มา ═══ ปลายทางเดิม <c>POST api/pay/webhooks/{providerCode}</c> เป็นนิรนาม (ผู้ให้บริการยิงเข้ามาโดยไม่ล็อกอิน) และ
/// <b>ไม่รู้ว่าเป็นของบริษัทไหน</b> ⇒ ลองยืนยันกับ config ที่เปิดใช้<b>ทุกบริษัท</b> · adapter บางเจ้ายืนยันด้วยการ fetch event กลับด้วย secret key
/// ของ config นั้น ⇒ POST นิรนาม 1 ครั้ง = N คำขอออกไปผู้ให้บริการด้วยคีย์ของทุกร้าน (เพดานรวม 600/นาที/IP) — ใช้โควตาผู้ให้บริการของทุกร้านได้</para>
///
/// <para>═══ กติกา ═══
/// <list type="bullet">
/// <item><b>URL ใหม่</b> <c>api/pay/webhooks/{providerCode}/{token}</c> — token สุ่มต่อ config (256 บิต) · ลองเฉพาะ config ที่ token ตรง
/// (เทียบแบบเวลาคงที่) · token ผิด/รูปผิด = <b>ไม่ลอง config ใดเลย ⇒ ไม่มีคำขอออก</b></item>
/// <item><b>URL เดิมยังทำงาน</b> (ผู้ใช้ตั้งไว้ในแดชบอร์ดผู้ให้บริการ — ตัดทิ้ง = ลูกค้าจ่ายแล้วออเดอร์ไม่อัปเดต) แต่ลองเฉพาะ config ที่
/// <b>ยังไม่เคย</b>ได้รับ webhook ทาง URL ใหม่<b>ในโหมดปัจจุบัน</b> (<see cref="GatewayWebhookConfigFacts.MovedToTokenUrl"/> — แดชบอร์ดทดสอบ/ใช้จริงตั้ง URL
/// แยกกัน: ย้ายในโหมดทดสอบแล้วสลับเป็นใช้จริงโดยแดชบอร์ดใช้จริงยังเป็น URL เดิม ต้องยังรับได้) ⇒ ยิ่งร้านย้าย URL มาก จำนวนคำขอออกต่อ POST
/// นิรนามยิ่งลด · หน้าตั้งค่าเตือน "ยังใช้ URL เดิม"</item>
/// <item>ไม่เพิ่มเพดานต่อ IP บนเส้นเดิม — ผู้ให้บริการยิง webhook ของทุกร้านจาก IP ชุดเดียวกัน ⇒ เพดานเข้มขึ้นตัด webhook จริงของร้านอื่นทิ้ง
/// (การลดจำนวนคำขอออกต่อ POST คือตัวแก้ที่ตรงเหตุ · งานถามสถานะสดเป็นตาข่ายรับเมื่อ webhook หายอยู่แล้ว)</item>
/// </list></para>
///
/// <para>G6: pure · ไม่มี I/O (ยกเว้น <see cref="NewToken"/> ที่ใช้ตัวสุ่มเชิงรหัสลับ)</para>
/// </summary>
public static class GatewayWebhookRoute
{
    /// <summary>ความยาว token (ตัวอักษร base64url ของ 32 ไบต์ = 43 ตัว) — migration ใช้ hex 64 ตัว ⇒ รับ 32–128 ตัว</summary>
    public const int MinTokenLength = 32;
    public const int MaxTokenLength = 128;

    /// <summary>token ใหม่ — 32 ไบต์จากตัวสุ่มเชิงรหัสลับ เข้ารหัส base64url (ใส่ใน path ได้โดยไม่ต้อง escape)</summary>
    public static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>รูป token ถูกไหม (ก่อนแตะฐาน) — ตัวอักษรที่ใช้ได้: A–Z a–z 0–9 - _ · ยาว 32–128</summary>
    public static bool IsWellFormedToken(string? token)
        => token is { Length: >= MinTokenLength and <= MaxTokenLength }
           && token.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>token ที่ยื่นมาตรงกับของ config ไหม — เทียบแบบเวลาคงที่ · ฝั่งใดว่าง = ไม่ตรง</summary>
    internal static bool TokenMatches(string? stored, string? presented)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(presented)) return false;
        var a = Encoding.UTF8.GetBytes(stored);
        var b = Encoding.UTF8.GetBytes(presented);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>config ที่จะลองยืนยัน webhook ครั้งนี้ — <b>ตัวตัดสินตัวเดียวของทั้งสองเส้นทาง</b>
    /// <para><paramref name="presentedToken"/> มีค่า (URL ใหม่) ⇒ เฉพาะ config ที่ token ตรง · รูปผิด = ว่าง ·
    /// null (URL เดิม) ⇒ เฉพาะ config ที่ยังไม่เคยได้รับ webhook ทาง URL ใหม่ในโหมดปัจจุบัน</para></summary>
    public static IReadOnlyList<Guid> ConfigsToTry(IEnumerable<GatewayWebhookConfigFacts> activeConfigs, string? presentedToken)
    {
        if (presentedToken != null)
        {
            if (!IsWellFormedToken(presentedToken)) return Array.Empty<Guid>();
            return activeConfigs.Where(c => TokenMatches(c.WebhookToken, presentedToken)).Select(c => c.ConfigId).ToList();
        }
        return activeConfigs.Where(c => !c.MovedToTokenUrl).Select(c => c.ConfigId).ToList();
    }

    /// <summary>path ของ URL ใหม่ (ต่อท้าย scheme+host ที่ผู้เรียกรู้) · ไม่มี token = URL เดิม</summary>
    public static string Path(string providerCode, string? token)
        => string.IsNullOrEmpty(token)
            ? $"/api/pay/webhooks/{providerCode}"
            : $"/api/pay/webhooks/{providerCode}/{token}";

    /// <summary>คำเตือนบนหน้าตั้งค่า — <c>null</c> = ไม่มีอะไรต้องทำ
    /// <para>ยังไม่เคยได้รับทาง URL ใหม่ แต่ได้รับทาง URL เดิม ⇒ "ยังใช้ URL เดิม ให้เปลี่ยน" · ได้รับทาง URL เดิมหลังจากที่ URL ใหม่ใช้งานแล้ว
    /// ⇒ แดชบอร์ดอาจตั้งไว้สองที่ (URL เดิมของร้านนี้ไม่ถูกลองแล้ว — webhook ที่มาทางนั้นจะไม่ถูกรับ)</para></summary>
    public static string? LegacyUrlWarning(DateTime? lastLegacyWebhookAt, GatewayWebhookConfigFacts facts)
    {
        if (lastLegacyWebhookAt == null) return null;
        if (!facts.MovedToTokenUrl)
            return "การแจ้งเตือนยังมาทาง URL แบบเดิม (ไม่มีรหัสลับของร้าน) — ให้เปลี่ยน URL แจ้งเตือนในแดชบอร์ดผู้ให้บริการเป็น URL ด้านบน "
                   + "(ทั้งแดชบอร์ดโหมดทดสอบและโหมดใช้งานจริง · URL เดิมยังใช้ได้ระหว่างเปลี่ยน แต่ทุกการแจ้งเตือนต้องถูกลองกับทุกร้านในระบบ — "
                   + "ช้ากว่าและใช้โควตาผู้ให้บริการของร้าน)";
        return lastLegacyWebhookAt > facts.LastTokenWebhookAt
            ? "มีการแจ้งเตือนมาทาง URL แบบเดิมหลังจากที่ร้านเปลี่ยนเป็น URL ใหม่แล้ว — ระบบไม่รับทาง URL เดิมของร้านนี้อีก "
              + "ตรวจแดชบอร์ดผู้ให้บริการว่าไม่ได้ตั้ง URL ไว้สองที่ (ถ้ามีรายการค้าง กด \"ตรวจสถานะสด\" ที่หน้ารายการรับชำระออนไลน์)"
            : null;
    }
}

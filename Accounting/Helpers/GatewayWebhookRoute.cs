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
/// <param name="LegacyEligible">มีหลักฐานว่าร้านนี้ใช้ URL เดิมก่อนมีรหัสลับ (ฝ่ายค้าน GWO-1 · migration ตั้ง true เฉพาะแถวที่เคยได้รับ webhook
/// ก่อนรอบนี้ · แถวใหม่ false เสมอ) — false = URL เดิม<b>ไม่</b>ลอง config นี้เลย</param>
public readonly record struct GatewayWebhookConfigFacts(Guid ConfigId, string? WebhookToken, DateTime? LastTokenWebhookAt,
    PaymentProviderMode? LastTokenWebhookMode = null, PaymentProviderMode CurrentMode = PaymentProviderMode.Test,
    bool LegacyEligible = false)
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
/// <item><b>URL เดิมยังทำงานชั่วคราว</b> (ผู้ใช้ตั้งไว้ในแดชบอร์ดผู้ให้บริการ — ตัดทิ้งทันที = ลูกค้าจ่ายแล้วออเดอร์ไม่อัปเดต) แต่ลองเฉพาะ config ที่
/// (ก) <b>มีหลักฐานว่าใช้ URL เดิม</b> (<see cref="GatewayWebhookConfigFacts.LegacyEligible"/> — ฝ่ายค้าน GWO-1: เดิมลองทุกแถวที่ "ยังไม่ย้าย" = ทุกแถว ณ วัน deploy
/// รวมร้านที่ไม่เคยรับ webhook และร้านที่สร้างหลังรอบนี้ ⇒ POST นิรนามยังพาคีย์ของร้านเหล่านั้นออกไป) (ข) <b>ยังไม่เคย</b>ได้รับ webhook ทาง URL ใหม่
/// <b>ในโหมดปัจจุบัน</b> (<see cref="GatewayWebhookConfigFacts.MovedToTokenUrl"/> — แดชบอร์ดทดสอบ/ใช้จริงตั้ง URL แยกกัน) (ค) <b>ก่อนวันปิด</b>
/// <see cref="LegacyRouteSunsetUtc"/> — หลังวันนั้น URL เดิมไม่ลอง config ใดเลย ⇒ POST นิรนามทาง URL เดิมไม่ทำให้เกิดคำขอออก ·
/// <b>ก่อนวันปิด POST นิรนามทาง URL เดิมยังพาคีย์ของร้านกลุ่ม (ก)∩(ข) ออกไปได้</b> (ราคาของการไม่ตัด webhook ของร้านที่ยังไม่เปลี่ยน) · หน้าตั้งค่าเตือนพร้อมวันปิด</item>
/// <item>ไม่เพิ่มเพดานต่อ IP บนเส้นเดิม — ผู้ให้บริการยิง webhook ของทุกร้านจาก IP ชุดเดียวกัน ⇒ เพดานเข้มขึ้นตัด webhook จริงของร้านอื่นทิ้ง
/// (การลดจำนวนคำขอออกต่อ POST คือตัวแก้ที่ตรงเหตุ · งานถามสถานะสดเป็นตาข่ายรับเมื่อ webhook หายอยู่แล้ว)</item>
/// </list></para>
///
/// <para>G6: pure · ไม่มี I/O (ยกเว้น <see cref="NewToken"/> ที่ใช้ตัวสุ่มเชิงรหัสลับ)</para>
/// </summary>
public static class GatewayWebhookRoute
{
    /// <summary>วันปิด URL เดิม (ไม่มีรหัสลับ) — 00:00 น. 1 ม.ค. 2570 เวลาไทย (ฝ่ายค้าน GWO-1 · คำตัดสิน main agent ข้อ Q5) · ตั้งแต่เวลานี้
    /// <see cref="ConfigsToTry"/> คืนชุดว่างให้ URL เดิมเสมอ · หน้าตั้งค่าแสดงวันนี้ใน <see cref="LegacyUrlWarning"/></summary>
    public static readonly DateTime LegacyRouteSunsetUtc = new(2026, 12, 31, 17, 0, 0, DateTimeKind.Utc);

    /// <summary>วันปิด URL เดิมในรูปที่แสดงบนหน้า (พ.ศ.) — คำนวณจาก <see cref="LegacyRouteSunsetUtc"/> ตัวเดียว</summary>
    internal static string LegacyRouteSunsetDisplay => ThaiDate.ToThaiDisplayString(LegacyRouteSunsetUtc);

    /// <summary>URL เดิมยังเปิดอยู่ไหม ณ เวลานี้</summary>
    internal static bool LegacyRouteOpen(DateTime nowUtc) => nowUtc < LegacyRouteSunsetUtc;

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
    /// null (URL เดิม) ⇒ เฉพาะ config ที่ <see cref="AcceptsLegacy"/> (มีหลักฐานใช้ URL เดิม · โหมดนี้ยังไม่ย้าย · ก่อนวันปิด)</para></summary>
    public static IReadOnlyList<Guid> ConfigsToTry(IEnumerable<GatewayWebhookConfigFacts> activeConfigs, string? presentedToken, DateTime nowUtc)
    {
        if (presentedToken != null)
        {
            if (!IsWellFormedToken(presentedToken)) return Array.Empty<Guid>();
            return activeConfigs.Where(c => TokenMatches(c.WebhookToken, presentedToken)).Select(c => c.ConfigId).ToList();
        }
        return activeConfigs.Where(c => AcceptsLegacy(c, nowUtc)).Select(c => c.ConfigId).ToList();
    }

    /// <summary>URL เดิมลอง config นี้ไหม — ตัวตัดสินเดียวของ <see cref="ConfigsToTry"/> (เส้น URL เดิม) · หน้าตั้งค่า (แสดง URL เดิมหรือไม่) · การประทับ "ถูกข้าม"</summary>
    public static bool AcceptsLegacy(GatewayWebhookConfigFacts c, DateTime nowUtc)
        => c.LegacyEligible && !c.MovedToTokenUrl && LegacyRouteOpen(nowUtc);

    /// <summary>ระยะห่างขั้นต่ำของการประทับ "คำขอทาง URL เดิมถูกข้าม" (GWO-7) — กันการเขียนฐานทุก POST นิรนาม</summary>
    internal static readonly TimeSpan LegacySkipStampInterval = TimeSpan.FromMinutes(10);

    /// <summary>ควรประทับเวลา "มีคำขอทาง URL เดิมแต่ config นี้ถูกข้าม" ไหม (ฝ่ายค้าน GWO-7) — config ต้องถูกข้ามจริง (ไม่อยู่ในชุดที่ลอง) ·
    /// ไม่ประทับถี่กว่า <see cref="LegacySkipStampInterval"/> · ผู้เรียกระบุ config จากเลขรายการในเนื้อคำขอที่<b>ยังไม่ยืนยัน</b> (ผลมีแค่คำเตือนบนหน้าตั้งค่า
    /// ของร้านนั้น — ไม่เปลี่ยนสถานะเงิน · ไม่ยิงคำขอออก)</summary>
    public static bool ShouldStampLegacySkip(GatewayWebhookConfigFacts c, DateTime? lastSkippedAtUtc, DateTime nowUtc)
        => !AcceptsLegacy(c, nowUtc)
           && (lastSkippedAtUtc is not DateTime last || nowUtc - last >= LegacySkipStampInterval);

    /// <summary>URL ที่แสดงให้ผู้ที่ไม่ใช่เจ้าของ (ฝ่ายค้าน GWO-6) — รหัสลับเหลือ 4 ตัวท้าย · ไม่มีรหัส = path เดิม</summary>
    public static string MaskedPath(string providerCode, string? token)
        => string.IsNullOrEmpty(token)
            ? Path(providerCode, null)
            : $"/api/pay/webhooks/{providerCode}/…{token[^Math.Min(4, token.Length)..]}";

    private const string WebhookPathPrefix = "/api/pay/webhooks/";

    /// <summary>ปิดบังรหัสลับใน path ก่อนเขียน log (ฝ่ายค้าน GWO-6 · <c>RequestLoggingMiddleware</c>) — <c>/api/pay/webhooks/{code}/{token}…</c> ⇒
    /// segment รหัสเป็น <c>[redacted]</c> · path อื่นคืนเดิม</summary>
    public static string RedactPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith(WebhookPathPrefix, StringComparison.OrdinalIgnoreCase)) return path ?? "";
        var rest = path[WebhookPathPrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0 || slash == rest.Length - 1) return path;
        var afterToken = rest.IndexOf('/', slash + 1);
        var tail = afterToken < 0 ? "" : rest[afterToken..];
        return path[..WebhookPathPrefix.Length] + rest[..(slash + 1)] + "[redacted]" + tail;
    }

    /// <summary>path ของ URL ใหม่ (ต่อท้าย scheme+host ที่ผู้เรียกรู้) · ไม่มี token = URL เดิม</summary>
    public static string Path(string providerCode, string? token)
        => string.IsNullOrEmpty(token)
            ? $"/api/pay/webhooks/{providerCode}"
            : $"/api/pay/webhooks/{providerCode}/{token}";

    /// <summary>คำเตือนบนหน้าตั้งค่า — <c>null</c> = ไม่มีอะไรต้องทำ
    /// <para><paramref name="lastLegacyWebhookAt"/> = ครั้งล่าสุดที่ URL เดิม<b>รับ</b>สำเร็จ · <paramref name="lastLegacySkippedAt"/> = ครั้งล่าสุดที่มีคำขอทาง URL เดิม
    /// ของร้านนี้แต่<b>ถูกข้าม</b> (GWO-7 — เดิมไม่มีการบันทึก ⇒ กิ่ง "ตั้ง URL ไว้สองที่" ไม่มีวันขึ้น)</para>
    /// <list type="bullet">
    /// <item>URL เดิมยังรับร้านนี้ (มีหลักฐานใช้ · โหมดนี้ยังไม่ย้าย) ⇒ "ให้เปลี่ยน + วันปิด" · เลยวันปิดแล้ว ⇒ "ปิดแล้ว ต้องตั้ง URL ใหม่"</item>
    /// <item>ไม่รับแล้ว แต่มีคำขอทาง URL เดิมหลังการรับทาง URL ใหม่ครั้งล่าสุด ⇒ "ระบบไม่รับทาง URL เดิมของร้านนี้ ตรวจแดชบอร์ด"</item>
    /// </list></summary>
    public static string? LegacyUrlWarning(DateTime? lastLegacyWebhookAt, DateTime? lastLegacySkippedAt, GatewayWebhookConfigFacts facts,
        DateTime nowUtc)
    {
        var sunset = LegacyRouteSunsetDisplay;
        if (facts.LegacyEligible && !facts.MovedToTokenUrl)
            return LegacyRouteOpen(nowUtc)
                ? $"ร้านนี้ยังรับการแจ้งเตือนทาง URL แบบเดิม (ไม่มีรหัสลับของร้าน) — URL แบบเดิมจะปิดวันที่ {sunset} · ให้เปลี่ยน URL แจ้งเตือนในแดชบอร์ด"
                  + "ผู้ให้บริการเป็น URL ด้านบนก่อนวันนั้น (ทั้งแดชบอร์ดโหมดทดสอบและโหมดใช้งานจริง)"
                : $"URL แจ้งเตือนแบบเดิมปิดแล้วตั้งแต่วันที่ {sunset} — ตั้ง URL ด้านบนในแดชบอร์ดผู้ให้บริการ (ทั้งสองโหมด) มิฉะนั้นลูกค้าจ่ายเงินแล้ว"
                  + "รายการจะอัปเดตช้า (รองานตรวจสถานะอัตโนมัติ) · รายการที่ค้างกด \"ตรวจสถานะสด\" ที่หน้ารายการรับชำระออนไลน์";
        var lastLegacyActivity = Later(lastLegacyWebhookAt, lastLegacySkippedAt);
        if (lastLegacyActivity is not DateTime legacy) return null;
        if (facts.LastTokenWebhookAt is DateTime viaToken && legacy <= viaToken) return null;
        return "มีการแจ้งเตือนมาทาง URL แบบเดิม (ไม่มีรหัสลับ) ซึ่งระบบไม่รับสำหรับร้านนี้"
               + (facts.MovedToTokenUrl ? " (ร้านเปลี่ยนเป็น URL ใหม่แล้ว)" : "")
               + " — ตรวจแดชบอร์ดผู้ให้บริการว่าตั้ง URL ด้านบนครบทั้งโหมดทดสอบและโหมดใช้งานจริง และไม่ได้ตั้ง URL เดิมค้างไว้ "
               + "(ถ้ามีรายการค้าง กด \"ตรวจสถานะสด\" ที่หน้ารายการรับชำระออนไลน์)";
    }

    private static DateTime? Later(DateTime? a, DateTime? b)
        => a is DateTime x && b is DateTime y ? (x > y ? x : y) : a ?? b;
}

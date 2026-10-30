using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **ผูก LINE ของลูกค้าปลายทาง (สมาชิกเว็บ / แขกของการจอง-ออเดอร์) — ตัวตัดสินเดียว** (รอบ 211 · คำตัดสินข้อ 170 ·
/// <c>erp-review/2026-10-30/LINE_CUSTOMER_NOTIFY_DESIGN.md</c>)
///
/// <para><b>กลไก (ไม่ใช้ LINE Login)</b>: หน้าเว็บขอรหัสใช้ครั้งเดียว → ลูกค้ากด "เพิ่มเพื่อน" (<see cref="AddFriendUrl"/>) แล้วกด "ส่งรหัส"
/// (<see cref="SendCodeUrl"/> เปิดแชต OA พร้อมข้อความ <c>ผูก LN-XXXXXXXX</c>) → webhook ของ OA นั้นได้ <c>userId</c> ที่ใช้ push ได้จริง
/// (userId ของ LINE เป็นค่า "ต่อ provider" ⇒ ต้องได้จาก OA เดียวกับที่จะส่งเท่านั้น)</para>
///
/// <para><b>แยกจากการผูกบัญชีผู้ใช้ ERP</b> (<c>ผูก 123456</c> ตัวเลข 6 หลัก · <c>LineBotService</c>): รหัสลูกค้าขึ้นต้น <c>LN</c> + ตัวอักษร/ตัวเลข 8 ตัว
/// (ไม่มี 0/1/I/O) ⇒ <see cref="IsLinkAttempt"/> แยกได้จากข้อความล้วน ๆ · ข้อความ ERP เดิม (ไม่มี LN) ไม่ถูกแตะ</para>
///
/// <para><b>ความปลอดภัย</b>: เก็บเฉพาะ SHA-256 ของรหัส (<see cref="Hash"/>) · อายุ <see cref="CodeLifetime"/> · ใช้ครั้งเดียว (ผู้เรียก update แบบมีเงื่อนไข
/// <c>UsedAt IS NULL</c>) · จำกัดความพยายามต่อ LINE user ที่ผู้เรียก (IChatRateLimiter · DB) · 32^8 ≈ 1.1×10¹² ชุด</para>
/// </summary>
public static class CustomerLineLinkRule
{
    public const string RuleCode = "CMS-211-LINE-LINK";
    public const string Prefix = "LN";
    public const int CodeLength = 8;
    /// <summary>ตัวอักษรที่ใช้ (32 ตัว · ตัด 0 O 1 I ที่อ่านสับสน)</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(30);

    public const string KindMember = "member";
    public const string KindBooking = "booking";
    public const string KindOrder = "order";

    /// <summary>ชนิดเป้าหมายที่รับ — นอกนี้ = ปฏิเสธ</summary>
    public static bool IsKnownKind(string? kind) => kind is KindMember or KindBooking or KindOrder;

    private static readonly Regex AttemptRe = new(@"^\s*(?:ผูก|bind|link)\s*LN", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CodeRe = new(@"^\s*(?:ผูก|bind|link)\s*LN\s*-?\s*([A-Za-z0-9]{8})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>สุ่มรหัส 8 ตัวจาก <see cref="Alphabet"/> ด้วย CSPRNG</summary>
    public static string NewCode()
    {
        var sb = new StringBuilder(CodeLength);
        for (var i = 0; i < CodeLength; i++) sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        return sb.ToString();
    }

    /// <summary>ข้อความที่ลูกค้าส่งให้ OA (เติมให้อัตโนมัติผ่าน <see cref="SendCodeUrl"/>)</summary>
    public static string MessageText(string code) => $"ผูก {Prefix}-{code}";

    /// <summary>ข้อความนี้ "ตั้งใจผูก LINE ของลูกค้า" ไหม (ขึ้นต้น ผูก/bind/link ตามด้วย LN) — ใช้แยกเส้นก่อนการผูกบัญชี ERP</summary>
    public static bool IsLinkAttempt(string? text) => !string.IsNullOrWhiteSpace(text) && AttemptRe.IsMatch(text);

    /// <summary>ดึงรหัส (ตัวพิมพ์ใหญ่) จากข้อความ · รูปแบบผิด/มีตัวนอก <see cref="Alphabet"/> ⇒ null</summary>
    public static string? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = CodeRe.Match(text);
        if (!m.Success) return null;
        var code = m.Groups[1].Value.ToUpperInvariant();
        return code.All(c => Alphabet.IndexOf(c) >= 0) ? code : null;
    }

    /// <summary>ค่าที่เก็บใน DB — SHA-256 hex ของ "LN-" + รหัสตัวใหญ่ (ห้ามเก็บรหัสดิบ)</summary>
    public static string Hash(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Prefix}-{(code ?? "").Trim().ToUpperInvariant()}"))).ToLowerInvariant();

    /// <summary>ใช้ได้ไหม: ยังไม่ถูกใช้ + ยังไม่หมดอายุ</summary>
    public static bool IsRedeemable(DateTime expiresAtUtc, DateTime? usedAtUtc, DateTime nowUtc) => usedAtUtc == null && expiresAtUtc > nowUtc;

    private static readonly Regex BasicIdRe = new(@"^@?[A-Za-z0-9._-]{2,40}$", RegexOptions.CultureInvariant);

    /// <summary>LINE OA Basic ID → รูป "@xxxx" · ว่าง/รูปแบบผิด ⇒ null</summary>
    public static string? NormalizeBasicId(string? basicId)
    {
        if (string.IsNullOrWhiteSpace(basicId)) return null;
        var t = basicId.Trim();
        if (!BasicIdRe.IsMatch(t)) return null;
        return "@" + t.TrimStart('@');
    }

    /// <summary>ลิงก์เพิ่มเพื่อน OA · basic id ใช้ไม่ได้ ⇒ null</summary>
    public static string? AddFriendUrl(string? basicId)
        => NormalizeBasicId(basicId) is string id ? $"https://line.me/R/ti/p/{Uri.EscapeDataString(id)}" : null;

    /// <summary>ลิงก์เปิดแชต OA พร้อมข้อความผูก (ลูกค้ากดส่งเอง) · basic id ใช้ไม่ได้ ⇒ null</summary>
    public static string? SendCodeUrl(string? basicId, string code)
        => NormalizeBasicId(basicId) is string id
            ? $"https://line.me/R/oaMessage/{Uri.EscapeDataString(id)}/?{Uri.EscapeDataString(MessageText(code))}"
            : null;

    /// <summary>แสดง userId แบบปิดบัง (U123••••ab) — ห้ามแสดง/บันทึก log เต็ม</summary>
    public static string Mask(string? lineUserId)
        => string.IsNullOrEmpty(lineUserId) ? "" : lineUserId.Length > 8 ? $"{lineUserId[..4]}••••{lineUserId[^2..]}" : "••••";

    /// <summary>ข้อความยินยอม (PDPA ม.19) ที่แสดงข้างปุ่ม — เวอร์ชันบันทึกผ่าน <see cref="CmsDataConsent.Version"/></summary>
    public static string ConsentText(string? shopName)
        => $"การกดเชื่อมต่อ LINE = ยินยอมให้ {(string.IsNullOrWhiteSpace(shopName) ? "ร้าน" : shopName!.Trim())} ส่งข้อความแจ้งสถานะการจอง/คำสั่งซื้อ/บริการ "
           + "ถึงบัญชี LINE นี้ · ยกเลิกได้ทุกเมื่อที่หน้าเดียวกันนี้ หรือบล็อกบัญชี LINE ของร้าน";

    public static string KindLabel(string kind) => kind switch
    {
        KindBooking => "การจองนี้",
        KindOrder => "คำสั่งซื้อนี้",
        _ => "บัญชีสมาชิกนี้",
    };

    /// <summary>ข้อความตอบกลับใน LINE (server ประกอบ · ไม่มีข้อมูลส่วนบุคคล)</summary>
    public static class Replies
    {
        public static string Linked(string? siteName, string kind)
            => $"✅ เชื่อมต่อ LINE กับ {(string.IsNullOrWhiteSpace(siteName) ? "ร้าน" : siteName)} สำเร็จ ({KindLabel(kind)})\n"
               + "ต่อจากนี้การแจ้งเตือนจะส่งมาที่ LINE นี้ · ยกเลิกได้ที่หน้าเว็บเดิม (ปุ่ม \"ยกเลิกการเชื่อมต่อ LINE\")";
        public const string BadFormat = "❌ รูปแบบรหัสไม่ถูกต้อง — กรุณากดปุ่ม \"รับแจ้งเตือนทาง LINE\" ที่หน้าเว็บอีกครั้ง แล้วกดส่งข้อความโดยไม่แก้ไข";
        public const string Invalid = "❌ รหัสไม่ถูกต้องหรือหมดอายุแล้ว — กรุณากดปุ่ม \"รับแจ้งเตือนทาง LINE\" ที่หน้าเว็บเพื่อรับรหัสใหม่";
        public const string WrongChannel = "❌ ร้านเปลี่ยนบัญชี LINE ที่ใช้แจ้งเตือนแล้ว — กรุณากดปุ่ม \"รับแจ้งเตือนทาง LINE\" ที่หน้าเว็บใหม่อีกครั้ง";
        public const string Throttled = "⛔ ลองผูกบ่อยเกินไป — กรุณารอสักครู่แล้วลองใหม่";
        public const string FollowHint = "สวัสดีค่ะ 👋 ถ้าต้องการรับแจ้งเตือนการจอง/คำสั่งซื้อทาง LINE: เปิดหน้าการจอง/คำสั่งซื้อ หรือ \"บัญชีของฉัน\" บนเว็บของร้าน "
            + "แล้วกด \"รับแจ้งเตือนทาง LINE\" → \"ส่งรหัส\"";
        public const string WelcomeBack = "ยินดีต้อนรับกลับ 👋 เปิดรับแจ้งเตือนทาง LINE ตามที่เคยเชื่อมต่อไว้อีกครั้งแล้ว";
    }
}

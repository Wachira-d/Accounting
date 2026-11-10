using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>คำสั่งในกลุ่ม LINE ที่บอทรับ (นอกนี้ = ไม่ทำอะไรและไม่เก็บ)</summary>
public enum LineGroupCommand
{
    None,
    HeyNextAcc,
    ByeNextAcc,
}

/// <summary>
/// รอบ 254 WP-11 L3 (LINE_MULTICHANNEL_DESIGN §3.c · คำตอบเจ้าของ Q2/Q5/Q8 · Q1(b)) — <b>ตัวตั้งเดียว</b> (pure) ของการผูกกลุ่ม LINE กับผู้ติดต่อ
/// <list type="bullet">
/// <item>คำสั่ง (<see cref="Parse"/>): <c>#HeyNextAcc</c> ⇒ ออกรหัส · <c>#ByeNextAcc</c> ⇒ ยกเลิกการผูกกลุ่มนี้ (บอทออกเมื่อไม่เหลือการผูก — Q8) · ไม่สนตัวพิมพ์ ·
/// รับ <c>＃</c> เต็มความกว้าง · ข้อความอื่นทั้งหมด ⇒ <see cref="LineGroupCommand.None"/> (บอทไม่ประมวล/ไม่เก็บข้อความกลุ่ม)</item>
/// <item>รหัส <c>GB-XXXXXXXX</c> (ตัวอักษรชุดเดียวกับ <see cref="CustomerLineLinkRule.Alphabet"/>) · เก็บ SHA-256 · อายุ 30 นาที · ใช้ครั้งเดียว ·
/// ผูกด้วย groupId อย่างเดียวไม่ได้ (กันวาง groupId ผิดกลุ่ม — G9)</item>
/// <item>ผูก/ส่งไม่ได้: กลุ่มกลางของแอดมิน (Q1(b)) · เกิน 200 คน (Q2) — ตัวตัดสินเดียว <see cref="ContactRecipientRule.GroupProblem"/></item>
/// </list>
/// </summary>
public static class LineGroupBindRule
{
    public const string RuleCode = "NOTIFY-LINE-GROUP-BIND";
    public const string Prefix = "GB";
    public const string SourceGroupCode = "group-code";
    public const string UnlinkBotRemoved = "bot-removed";
    public const string UnlinkGroupBye = "group-bye";
    public const string AttestPurpose = "contact-line-group-attest";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(30);
    /// <summary>webhookEventId ที่เห็นแล้วเก็บไว้กันประมวลซ้ำ (ลบเมื่อเก่ากว่านี้)</summary>
    public static readonly TimeSpan SeenRetention = TimeSpan.FromDays(7);

    private static readonly Regex HeyRe = new(@"^\s*[#＃]\s*heynextacc\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ByeRe = new(@"^\s*[#＃]\s*byenextacc\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CodeRe = new(@"^\s*GB\s*-?\s*([A-Za-z0-9]{8})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static LineGroupCommand Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LineGroupCommand.None;
        if (HeyRe.IsMatch(text)) return LineGroupCommand.HeyNextAcc;
        if (ByeRe.IsMatch(text)) return LineGroupCommand.ByeNextAcc;
        return LineGroupCommand.None;
    }

    public static string NewCode() => CustomerLineLinkRule.NewCode();

    public static string Display(string code) => $"{Prefix}-{code}";

    /// <summary>รหัสที่ผู้ดูแลพิมพ์ (GB-XXXXXXXX หรือ 8 ตัว) ⇒ ตัวพิมพ์ใหญ่ · ผิดรูป/มีตัวนอกชุด ⇒ null</summary>
    public static string? TryParseCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var t = input.Trim();
        var m = CodeRe.Match(t);
        var code = m.Success ? m.Groups[1].Value : (t.Length == 8 ? t : null);
        if (code == null) return null;
        code = code.ToUpperInvariant();
        return code.All(c => CustomerLineLinkRule.Alphabet.IndexOf(c) >= 0) ? code : null;
    }

    public static string Hash(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Prefix}-{(code ?? "").Trim().ToUpperInvariant()}"))).ToLowerInvariant();

    public static bool IsRedeemable(DateTime expiresAt, DateTime? usedAt, DateTime nowUtc) => usedAt == null && expiresAt > nowUtc;

    /// <summary>ข้อความรับรองของผู้ดูแลบริษัท (PDPA ม.23 · DESIGN §6.3) — เวอร์ชัน = hash ของข้อความ</summary>
    public static string AttestText(string? contactName, string? groupName)
        => $"ข้าพเจ้ายืนยันว่าสมาชิกทุกคนในกลุ่ม LINE \"{(string.IsNullOrWhiteSpace(groupName) ? "กลุ่มนี้" : groupName!.Trim())}\" มีสิทธิ์เห็นเอกสารของ "
           + $"{(string.IsNullOrWhiteSpace(contactName) ? "ผู้ติดต่อนี้" : contactName!.Trim())} และบอทจะประกาศการผูกในกลุ่มทันที";

    public static class Replies
    {
        public const string Join = "สวัสดีครับ — บอทพร้อมส่งการแจ้งเอกสารเข้ากลุ่มนี้ เมื่อผู้ดูแลบริษัทผูกกลุ่มกับผู้ติดต่อแล้ว\n"
            + "พิมพ์ #HeyNextAcc เพื่อรับรหัสผูก · บอทไม่อ่านหรือเก็บข้อความอื่นในกลุ่ม";
        public static string Code(string code, string? groupName, DateTime expiresUtc)
            => $"🔗 รหัสผูกกลุ่ม \"{(string.IsNullOrWhiteSpace(groupName) ? "กลุ่มนี้" : groupName)}\": {Display(code)}\n"
               + $"หมดอายุ {expiresUtc.AddHours(7):HH:mm} น. (ใช้ได้ครั้งเดียว)\n"
               + "ขั้นต่อไป: ผู้ดูแลบริษัทเปิด ผู้ติดต่อ › ช่องทางแจ้งเตือน › เพิ่มกลุ่ม LINE › ใส่รหัสนี้";
        /// <summary>รอบ 257 ชุด 4 (C-M4): คำตอบ <c>#HeyNextAcc</c> ของ <b>LINE OA กลาง</b> — รหัสผูกได้เฉพาะบริษัทผู้ให้บริการ (256-2) ⇒ บอกชื่อบริษัทนั้น
        /// + ทางของบริษัทอื่น (เชิญ OA ของบริษัทตัวเอง) ตั้งแต่ตอนออกรหัส (เดิมตอบเหมือน OA บริษัท ⇒ บริษัทอื่นรู้ว่าใช้ไม่ได้ตอนยืนยันเท่านั้น) ·
        /// ฝ่ายค้าน LOW-6: ประกอบข้อความตรง ๆ (ไม่พึ่งการแทนที่ข้อความของ <see cref="Code"/>) — บรรทัดรหัส/หมดอายุใช้รูปเดียวกับ OA บริษัท</summary>
        public static string CentralCode(string code, string? groupName, DateTime expiresUtc, string? platformCompanyName)
            => $"🔗 รหัสผูกกลุ่ม \"{(string.IsNullOrWhiteSpace(groupName) ? "กลุ่มนี้" : groupName)}\": {Display(code)}\n"
               + $"หมดอายุ {expiresUtc.AddHours(7):HH:mm} น. (ใช้ได้ครั้งเดียว)\n"
               + $"ใช้ได้เฉพาะ {(string.IsNullOrWhiteSpace(platformCompanyName) ? "บริษัทผู้ให้บริการ NextAcc" : platformCompanyName!.Trim())} (บอทนี้คือ LINE OA กลางของ NextAcc)\n"
               + "ขั้นต่อไป: ผู้ดูแลบริษัทนั้นเปิด ผู้ติดต่อ › ช่องทางแจ้งเตือน › เพิ่มกลุ่ม LINE › ใส่รหัสนี้\n"
               + "บริษัทอื่น: รหัสนี้ใช้ไม่ได้ — เชิญ LINE OA ของบริษัทตัวเองเข้ากลุ่มแล้วพิมพ์ #HeyNextAcc ใหม่";
        public static string Refused(string problem) => "❌ ผูกกลุ่มนี้ไม่ได้ — " + problem;
        public const string Throttled = "⛔ ขอรหัสบ่อยเกินไป — กรุณารอสักครู่แล้วลองใหม่";
        public static string Bound(string? companyName, string? contactName, IEnumerable<string> topics)
            => $"✅ กลุ่มนี้ผูกกับ {(string.IsNullOrWhiteSpace(companyName) ? "บริษัท" : companyName)} สำหรับผู้ติดต่อ {(string.IsNullOrWhiteSpace(contactName) ? "-" : contactName)} แล้ว\n"
               + $"เรื่องที่ส่ง: {string.Join(", ", topics)} · พิมพ์ #ByeNextAcc เพื่อยกเลิก";
        public const string Bye = "ยกเลิกการผูกกลุ่มนี้แล้ว — บอทจะไม่ส่งการแจ้งเอกสารเข้ากลุ่มนี้อีกและจะออกจากกลุ่ม";
        public const string ByeNothing = "กลุ่มนี้ยังไม่ได้ผูกกับผู้ติดต่อใด";
        /// <summary>รอบ 256: คำสั่งกลุ่มที่ส่งถึง LINE OA กลางของ NextAcc — เดิม webhook กลางทิ้ง event กลุ่มเงียบ ⇒ ผู้ใช้พิมพ์แล้วไม่มีอะไรตอบ ·
        /// การส่งเข้ากลุ่มผ่าน OA กลางยังไม่เปิด (คำตัดสิน N8 · 254-WP11-11) ⇒ บอกเหตุ + ทางไปต่อ</summary>
        public const string CentralOaNotSupported = "ℹ️ บอทนี้คือ LINE OA กลางของ NextAcc — ยังผูกกลุ่มผ่าน OA กลางไม่ได้\n"
            + "ทางที่ใช้ได้: ผู้ขายตั้ง LINE OA ของบริษัทเอง (NextAcc › ตั้งค่า › LINE › ตั้งค่าขั้นสูง: รหัสเชื่อม · รหัสลับ · Webhook URL) "
            + "แล้วเชิญ OA ของบริษัทเข้ากลุ่มนี้และพิมพ์ #HeyNextAcc ใหม่";
        /// <summary>รอบ 256 (คำตัดสิน 256-2): ผู้ดูแลบริษัทที่<b>ไม่ใช่</b>บริษัทผู้ให้บริการใส่รหัสที่ OA กลางออก ⇒ ปฏิเสธด้วยประโยคเดิม (N8 · 254-WP11-11 คงอยู่)</summary>
        public const string CentralCodeRefused = "รหัสนี้มาจาก LINE OA กลางของ NextAcc — การส่งเข้ากลุ่มผ่าน OA กลางยังไม่เปิด · เชิญ LINE OA ของบริษัทเข้ากลุ่มแทนแล้วพิมพ์ #HeyNextAcc ใหม่";
        /// <summary>รอบ 256-2 (ฝ่ายค้าน L2): ยกเลิกกลุ่มที่ผูกผ่าน OA กลางจากบริษัทที่ไม่ใช่ (หรือไม่ใช่แล้ว) บริษัทผู้ให้บริการ — สั่งบอทกลางออกไม่ได้ ⇒ บอกทางเอาออกเอง</summary>
        public const string CentralBotStays = "ยกเลิกปลายทางแล้ว — แต่บอท LINE OA กลางของ NextAcc ยังอยู่ในกลุ่ม (บริษัทนี้สั่งบอทกลางออกไม่ได้) · "
            + "เอาออกได้โดยพิมพ์ #ByeNextAcc ในกลุ่ม หรือให้แอดมินกลุ่มลบบอทออกจากกลุ่ม";
        /// <summary>รอบ 256-2: บริษัทผู้ให้บริการ แต่ผู้ดูแลระบบยังไม่ได้ตั้ง token ของ OA กลาง</summary>
        public const string CentralOaNotConfigured = "LINE OA กลางของ NextAcc ยังไม่ได้ตั้งค่า (ผู้ดูแลระบบ → ตั้งค่าเว็บไซต์ → LINE OA กลาง)";
    }

    /// <summary>รอบ 256 (คำตัดสินเจ้าของ 256-2 · 2026-11-09) — <b>ด่านเดียว</b>ของ "กลุ่ม LINE ของผู้ติดต่อผ่าน OA กลาง": ใช้ได้เฉพาะ<b>บริษัทผู้ให้บริการ</b>
    /// (<c>SiteSettings.PlatformCompanyId</c> ที่ตั้งไว้และตรงกับบริษัทนี้) · บริษัทอื่นคง N8/254-WP11-11 (ปิด) · ผู้เรียก: ออกรหัส (webhook กลาง) · ยืนยันรหัส ·
    /// ตัวหาผู้รับ (<c>LineGate</c>) · ตอนส่ง (<c>LineNotifyService</c>) · กล่องผูกกลุ่มบนหน้าผู้ติดต่อ · บันทึกกฎ</summary>
    public static bool CentralScopeAllowed(Guid companyId, Guid? platformCompanyId)
        => companyId != Guid.Empty && platformCompanyId is Guid p && p != Guid.Empty && p == companyId;

    /// <summary>รอบ 256-2 (ฝ่ายค้าน M2) — event กลุ่มของ OA กลางที่ต้องทำอะไร (pure): คำสั่ง #HeyNextAcc/#ByeNextAcc · บอทถูกเตะ (leave) · สมาชิกเข้า/ออก ·
    /// นอกนั้น (คุยกันในกลุ่ม · join · อื่น ๆ) ⇒ false = ไม่แตะฐาน ไม่ประทับกันซ้ำ ไม่อ่านบริษัทผู้ให้บริการ (เหมือนก่อนรอบนี้)</summary>
    public static bool CentralGroupEventActionable(string? eventType, string? text) => eventType switch
    {
        "leave" or "memberJoined" or "memberLeft" => true,
        "message" => Parse(text) != LineGroupCommand.None,
        _ => false,
    };

    /// <summary>รอบ 256 — คำตอบของ OA กลางต่อ event ในกลุ่ม/ห้อง<b>เมื่อยังไม่ได้ตั้งบริษัทผู้ให้บริการ</b> (pure): ตอบเฉพาะคำสั่ง #HeyNextAcc/#ByeNextAcc · ข้อความอื่น/เข้ากลุ่ม ⇒ null
    /// (ไม่ทักทายตอนเข้ากลุ่ม — OA กลางอยู่ในกลุ่มทีมของแอดมินด้วย · ไม่อ่าน/ไม่เก็บข้อความกลุ่ม PDPA ม.22) · มีบริษัทผู้ให้บริการ ⇒ <c>ContactLineGroupService.HandleCentralGroupEventAsync</c> (256-2)</summary>
    public static string? CentralOaGroupReply(string? eventType, string? text)
        => eventType == "message" && Parse(text) != LineGroupCommand.None ? Replies.CentralOaNotSupported : null;
}

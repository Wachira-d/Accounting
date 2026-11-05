using System.Net.Mail;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **ตัดสิน "อีเมลถึงลูกค้าของบริษัทจะส่งจากใคร" — ตัวตั้งเดียว** (รอบ 240 · คำตัดสิน 240-4/240-5 · ข้อวิเคราะห์กฎหมายใน DECISIONS 240-5)
///
/// <list type="number">
/// <item><b>บริษัทตั้งผู้ส่งของตัวเองแล้วและทดสอบผ่าน</b> (<c>CompanySettings.EmailConfigured</c> + มีข้อมูลผู้ส่งครบ) ⇒ ส่งด้วยผู้ส่งของบริษัท
///   จากที่อยู่ของบริษัท (โดเมนของบริษัทเอง — SPF/DKIM ของบริษัทรับรอง)</item>
/// <item><b>ไม่ได้ตั้ง/ยังไม่ผ่านการทดสอบ</b> ⇒ ส่งด้วย SMTP ของ NextAcc: <c>From</c> = ที่อยู่ของแพลตฟอร์ม (เช่น no-reply@nextacc.net) ·
///   ชื่อผู้ส่ง = "{ชื่อบริษัท} ผ่าน NextAcc" · <c>Reply-To</c> = อีเมลติดต่อของบริษัท (ลูกค้าตอบกลับถึงบริษัท ไม่ใช่ถึงเรา) ·
///   เนื้ออีเมลต้องบอกชัดว่า "ส่งในนามบริษัท… ผ่านระบบ NextAcc" (<see cref="SignRequestEmailText"/>) ·
///   <b>ห้ามใส่โดเมนของบริษัทใน From เด็ดขาด</b> — ปลอมโดเมน (spoofing) ผิด SPF/DKIM/DMARC ถูกตีกลับ/เข้าสแปม และเป็นการแสดงตัวที่ไม่ตรงความจริง</item>
/// </list>
/// <para>ผู้เรียก: <c>SignRequestService</c> (อีเมลขอเซ็น/เตือน) · <c>DocumentEmailService.SendDocumentEmailAsync</c> (อีเมลเอกสาร — เดิมใส่ From
/// ของบริษัทแม้ส่งผ่าน SMTP ของระบบ)</para>
/// </summary>
public static class OnBehalfEmailSender
{
    public const string ModeCompany = "company";
    public const string ModePlatform = "platform";
    /// <summary>ที่อยู่ผู้ส่งของแพลตฟอร์มเมื่อผู้ดูแลยังไม่ได้ตั้ง (โดเมนของ NextAcc เอง)</summary>
    public const string DefaultPlatformFrom = "no-reply@nextacc.net";
    public const string PlatformBrand = "NextAcc";
    public const int DisplayNameMax = 70;

    public sealed record Input(
        bool CompanySenderVerified,
        string? CompanyFromAddress,
        string? CompanyFromName,
        string? CompanyReplyTo,
        string? CompanyContactEmail,
        string? CompanyName,
        string? PlatformFromAddress);

    /// <summary>ผล — <see cref="OnBehalf"/> = ส่งผ่านแพลตฟอร์มในนามบริษัท (เนื้อหาต้องมีคำเปิดเผย)</summary>
    public sealed record Plan(string Mode, string FromAddress, string FromName, string? ReplyTo)
    {
        public bool OnBehalf => Mode == ModePlatform;
    }

    public static Plan Resolve(Input i)
    {
        var name = SafeDisplayName(i.CompanyName);
        if (i.CompanySenderVerified && IsPlausibleEmail(i.CompanyFromAddress))
        {
            var fromName = SafeDisplayName(string.IsNullOrWhiteSpace(i.CompanyFromName) ? i.CompanyName : i.CompanyFromName);
            return new Plan(ModeCompany, i.CompanyFromAddress!.Trim(), fromName,
                IsPlausibleEmail(i.CompanyReplyTo) ? i.CompanyReplyTo!.Trim() : null);
        }

        var platformFrom = IsPlausibleEmail(i.PlatformFromAddress) ? i.PlatformFromAddress!.Trim() : DefaultPlatformFrom;
        // ฝ่ายค้าน: ผู้ดูแลตั้งที่อยู่ของแพลตฟอร์มให้ตรงโดเมนบริษัทลูกค้าโดยบังเอิญ — ถือว่าไม่ใช่ที่อยู่ของแพลตฟอร์ม
        if (SameDomain(platformFrom, i.CompanyFromAddress) || SameDomain(platformFrom, i.CompanyContactEmail))
            platformFrom = DefaultPlatformFrom;
        var replyTo = new[] { i.CompanyReplyTo, i.CompanyFromAddress, i.CompanyContactEmail }
            .FirstOrDefault(IsPlausibleEmail)?.Trim();
        return new Plan(ModePlatform, platformFrom, $"{name} ผ่าน {PlatformBrand}", replyTo);
    }

    /// <summary>ป้ายผู้ส่งบนหน้าต่างส่งอีเมล (เซิร์ฟเวอร์ประกอบ · หน้าเว็บแสดงอย่างเดียว)</summary>
    public static string Describe(Plan p) => p.OnBehalf
        ? $"ส่งผ่านอีเมลของระบบ NextAcc ในนามบริษัท — ผู้ส่ง: {p.FromName} <{p.FromAddress}>"
          + (p.ReplyTo != null ? $" · ลูกค้าตอบกลับถึง {p.ReplyTo}" : " · ยังไม่มีอีเมลตอบกลับของบริษัท")
          + " · ข้อความที่แก้จะส่งเป็นข้อความล้วน (ไม่รับ HTML) · มีเพดานต่อวัน · ตั้งค่า → อีเมล (ทดสอบผ่าน) เพื่อส่งจากอีเมลของบริษัทเอง"
        : $"ส่งจากอีเมลของบริษัท — ผู้ส่ง: {p.FromName} <{p.FromAddress}>";

    /// <summary>ชื่อผู้ส่งที่ปลอดภัย — ตัดอักขระควบคุม/ขึ้นบรรทัด (header injection) · ตัด <c>&lt; &gt; " @</c> (ชื่อที่หน้าตาเหมือนที่อยู่อีเมลของคนอื่น) ·
    /// ยาว ≤ <see cref="DisplayNameMax"/> · ว่าง = "ผู้ขาย"</summary>
    public static string SafeDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "ผู้ขาย";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsControl(ch)) { sb.Append(' '); continue; }
            if (ch is '<' or '>' or '"' or '@' or '\\') continue;
            sb.Append(ch);
        }
        var t = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (t.Length > DisplayNameMax) t = t[..DisplayNameMax].TrimEnd();
        return t.Length == 0 ? "ผู้ขาย" : t;
    }

    /// <summary>ที่อยู่อีเมลรูปแบบใช้ได้ (ตัวเดียว · ไม่มีชื่อแสดง · ไม่มีอักขระควบคุม)</summary>
    public static bool IsPlausibleEmail(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.Length > 254 || t.Any(char.IsControl) || t.Contains(' ') || t.Contains(',') || t.Contains(';')) return false;
        try
        {
            var a = new MailAddress(t);
            return string.Equals(a.Address, t, StringComparison.OrdinalIgnoreCase) && a.Host.Contains('.');
        }
        catch (FormatException) { return false; }
    }

    /// <summary>โดเมนของที่อยู่ (ตัวพิมพ์เล็ก) · ใช้ไม่ได้ = null</summary>
    private static string? DomainOf(string? address)
        => IsPlausibleEmail(address) ? address!.Trim()[(address!.Trim().LastIndexOf('@') + 1)..].ToLowerInvariant() : null;

    private static bool SameDomain(string a, string? b)
    {
        var db = DomainOf(b);
        return db != null && string.Equals(DomainOf(a), db, StringComparison.Ordinal);
    }
}

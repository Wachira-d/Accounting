namespace Accounting.Helpers;

/// <summary>แถวผู้สมัครของการค้น (การจองหรือคำสั่งซื้อ) — อีเมล/เบอร์ทุกช่องบนรายการ (ของแขก + ของบัญชีลูกค้าที่ผูก)</summary>
public sealed record GuestLookupCandidate(Guid CompanyId, Guid SiteId, string Number, string? PublicToken,
    IReadOnlyList<string?> Emails, IReadOnlyList<string?> Phones);

/// <summary>
/// <b>ตัวตัดสินเดียว "แขกเปิดดูการจอง/คำสั่งซื้อด้วยเลขที่ + เบอร์โทรหรืออีเมล"</b> (รอบ 210 · คำตัดสินข้อ 169) — pure ไม่มี I/O
/// <para>ผู้เรียก (<c>StorefrontCustomerService</c>) โหลดแถวที่ <b>บริษัท + เว็บ + เลขที่</b> ตรงแล้ว ⇒ ตัวนี้ตัดสินว่าข้อมูลติดต่อที่แขกกรอก
/// ตรงกับข้อมูลบนรายการไหม: เบอร์ (<see cref="CmsContactResolver.NormalizePhone"/>) หรืออีเมล (<see cref="CmsContactResolver.NormalizeEmail"/>)
/// <b>เท่ากันทุกตัวอักษรหลัง normalize</b> — ไม่ fuzzy (F2 #3) · ช่องว่าง/รูปแบบผิด ⇒ ไม่ตรง</para>
/// <para>ผลสำเร็จคืนเฉพาะโทเคนสาธารณะ · ผิด = ข้อความกลางเดียว (<see cref="NotFoundMessage"/>) ไม่บอกว่าผิดที่เลขหรือที่เบอร์ · จำกัดความถี่ต่อ IP และต่อเลขที่ที่ controller</para>
/// </summary>
public static class GuestRecordLookup
{
    public const string RuleCode = "CMS-GUEST-LOOKUP";
    public const string NotFoundMessage = "ไม่พบรายการที่ตรงกับเลขที่และเบอร์โทร/อีเมลที่ระบุ — ตรวจสอบอีกครั้ง หรือติดต่อร้าน";
    public const string InputMessage = "กรอกเลขที่การจอง/คำสั่งซื้อ และเบอร์โทรหรืออีเมลที่ใช้ตอนจอง/สั่งซื้อ";
    public const string RateLimitedMessage = "ตรวจสอบถี่เกินไป — รอสักครู่แล้วลองใหม่";
    public const int MaxNumberLength = 64;

    /// <summary>เลขที่เทียบกันได้ — ตัดช่องว่างหัวท้าย + ตัวพิมพ์ใหญ่ · ว่าง/ยาวเกิน ⇒ null (ไม่ค้น)</summary>
    public static string? NormalizeNumber(string? number)
    {
        var n = (number ?? "").Trim().ToUpperInvariant();
        return n.Length == 0 || n.Length > MaxNumberLength ? null : n;
    }

    /// <summary>คีย์โควตาต่อเลขที่ (ตาราง <c>ChatRateBuckets.BucketKey</c> varchar(120) — เลขยาว 64 ตัวต่อ GUID แล้วเกิน ⇒ insert ล้ม ⇒ limiter fail-open เงียบ) ·
    /// = SHA-256 ของเลขหลัง normalize 16 ตัวแรก · <paramref name="normalizedNumber"/> null ⇒ null</summary>
    /// <summary>ฝ่ายค้านรอบ 210 ข้อ 1: ถังต่อ "ผู้ติดต่อ" (เบอร์/อีเมลที่ normalize แล้ว · แฮช 16 ตัว) — กันทิศกลับ: รู้เบอร์เหยื่อแล้วไล่เลขที่ทุกใบ ·
    /// กรอกไม่ใช่เบอร์/อีเมล ⇒ null (ไม่มีถัง — คำขอนั้นไม่ตรงอะไรอยู่แล้ว)</summary>
    public static string? ContactBucket(string? contact)
    {
        var key = CmsContactResolver.NormalizeEmail(contact) ?? CmsContactResolver.NormalizePhone(contact);
        if (key == null) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>คีย์ IP ของถังต่อผู้เรียก — IPv6 รวมเป็น /64 (เครื่องเดียวมีที่อยู่ในวงเดียวกันได้มหาศาล) · IPv4 (รวม IPv4-mapped) = ตัวมันเอง</summary>
    public static string IpBucket(System.Net.IPAddress? ip)
    {
        if (ip == null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.ToString();
        var b = ip.GetAddressBytes();
        return Convert.ToHexString(b, 0, 8).ToLowerInvariant() + "::/64";
    }

    public static string? NumberBucket(string? normalizedNumber)
    {
        if (string.IsNullOrEmpty(normalizedNumber)) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedNumber));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>ข้อมูลติดต่อที่แขกกรอก → (อีเมล, เบอร์) ที่เทียบได้ · มี @ = อีเมล · ไม่งั้น = เบอร์ · ใช้ไม่ได้ทั้งคู่ ⇒ (null, null)</summary>
    private static (string? Email, string? Phone) NormalizeContact(string? contact)
    {
        var c = (contact ?? "").Trim();
        if (c.Length == 0 || c.Length > 254) return (null, null);
        return c.Contains('@') ? (CmsContactResolver.NormalizeEmail(c), null) : (null, CmsContactResolver.NormalizePhone(c));
    }

    /// <summary>คำขอใช้ได้ไหม (มีเลขที่ + ข้อมูลติดต่อที่เทียบได้)</summary>
    public static bool IsValidRequest(string? number, string? contact)
    {
        var (e, p) = NormalizeContact(contact);
        return NormalizeNumber(number) != null && (e != null || p != null);
    }

    /// <summary>ข้อมูลติดต่อที่แขกกรอกตรงกับรายการไหม — <paramref name="recordEmails"/>/<paramref name="recordPhones"/> = ทุกช่องบนรายการ
    /// (ช่องของแขก + ของบัญชีลูกค้าที่ผูก) · ตรงเมื่ออีเมลเท่ากัน หรือเบอร์เท่ากัน (หลัง normalize) เท่านั้น</summary>
    private static bool ContactMatches(string? contact, IEnumerable<string?> recordEmails, IEnumerable<string?> recordPhones)
    {
        var (email, phone) = NormalizeContact(contact);
        if (email != null) return recordEmails.Any(x => CmsContactResolver.NormalizeEmail(x) == email);
        if (phone != null) return recordPhones.Any(x => CmsContactResolver.NormalizePhone(x) == phone);
        return false;
    }

    /// <summary>ผลรวม (ด่านซ้ำทุกข้อแม้ผู้เรียกกรองมาแล้ว): บริษัท + เว็บ + เลขที่ (หลัง normalize) ตรง · มีโทเคน · ข้อมูลติดต่อตรง ⇒ โทเคน · ไม่มี ⇒ null</summary>
    public static string? Match(IEnumerable<GuestLookupCandidate> candidates, Guid companyId, Guid siteId, string? number, string? contact)
    {
        var n = NormalizeNumber(number);
        if (n == null) return null;
        return candidates
            .Where(c => c.CompanyId == companyId && c.SiteId == siteId && NormalizeNumber(c.Number) == n
                && !string.IsNullOrWhiteSpace(c.PublicToken) && ContactMatches(contact, c.Emails, c.Phones))
            .Select(c => c.PublicToken).FirstOrDefault();
    }
}

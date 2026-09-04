using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **ข้อมูลติดต่อบนเว็บไซต์ต้องมาจากที่ตั้งค่า ไม่ใช่ข้อความที่พิมพ์ค้างไว้ในเทมเพลต**
///
/// <para>ที่มา (บั๊กจริง): `CmsSiteTemplateSeeder` ฝัง <c>02-XXX-XXXX</c> ·
/// <c>info@example.com</c> · <c>@yourshop</c> ลงในเนื้อหาบล็อกของหน้า "ติดต่อเรา"
/// ~20 เทมเพลต ⇒ ลูกค้ากรอกเบอร์/อีเมลจริงในหน้าตั้งค่าบริษัทแล้ว **หน้าเว็บยังโชว์
/// ตัวอย่างปลอมอยู่เหมือนเดิม** และไม่มีอะไรบอกว่าต้องไปแก้ที่ไหน (ต้องไล่แก้เนื้อหา
/// ทีละหน้าเอง ซึ่งผู้ใช้ส่วนใหญ่ไม่รู้)</para>
///
/// <para>วิธีแก้เชิงโครงสร้าง: เนื้อหาเก็บเป็น **โทเคน** แล้วแทนค่าตอนเรนเดอร์ —
/// แก้ที่ตั้งค่าครั้งเดียว ทุกหน้าทุกภาษาตามทันที · drift เป็นศูนย์โดยโครงสร้าง
/// (กลไกเดียวกับที่ใช้กับ `MENU_SECTIONS` / `complianceIssues` / `DocumentTitle`)</para>
///
/// <para><b>ลำดับค่า</b>: ค่าที่ตั้งไว้ระดับ**เว็บไซต์**ชนะค่าระดับ**บริษัท** —
/// บริษัทเดียวอาจมีหลายเว็บ (คนละแบรนด์/คนละสาขา) ที่ใช้เบอร์ติดต่อคนละเบอร์</para>
///
/// <para><b>โทเคนที่ยังไม่มีค่า</b> จะถูกแทนด้วย **สตริงว่าง** ไม่ใช่คงข้อความ
/// <c>{{...}}</c> ไว้ — ปล่อยไว้ผู้ชมเว็บจะเห็นโค้ดดิบซึ่งแย่กว่าไม่เห็นอะไรเลย
/// (และห้ามแทนด้วยค่าตัวอย่าง — นั่นคือต้นตอของบั๊กนี้ตั้งแต่แรก)</para>
/// </summary>
public static class CmsContentTokens
{
    /// <summary>ชื่อโทเคนที่รองรับ + คำอธิบายไทย — ใช้ทั้งฝั่งแทนค่าและฝั่งแสดง
    /// รายการช่วยเหลือในหน้าแก้ไขเนื้อหา (ห้ามพิมพ์รายชื่อซ้ำอีกที่)</summary>
    public static readonly IReadOnlyList<(string Token, string Label)> Supported = new[]
    {
        ("company.name",    "ชื่อกิจการ"),
        ("company.nameEn",  "ชื่อกิจการ (อังกฤษ)"),
        ("company.phone",   "เบอร์โทรติดต่อ"),
        ("company.email",   "อีเมลติดต่อ"),
        ("company.address", "ที่อยู่"),
        ("company.taxId",   "เลขประจำตัวผู้เสียภาษี"),
        ("company.website", "เว็บไซต์"),
        ("site.name",       "ชื่อเว็บไซต์"),
        ("site.lineId",     "LINE ID"),
        ("site.facebook",   "เพจ Facebook"),
        ("site.instagram",  "Instagram"),
    };

    private static readonly Regex TokenPattern =
        new(@"\{\{\s*([A-Za-z]+\.[A-Za-z]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>แทนค่าโทเคนทุกตัวในข้อความ — ปลอดภัยกับ null/ข้อความที่ไม่มีโทเคน
    /// (คืนค่าเดิมทันทีเมื่อไม่มี <c>{{</c> เพื่อไม่ให้เสียเวลากับเนื้อหายาว ๆ)</summary>
    public static string? Apply(string? content, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(content) || content.IndexOf("{{", StringComparison.Ordinal) < 0)
            return content;

        return TokenPattern.Replace(content, m =>
        {
            var key = m.Groups[1].Value;
            // โทเคนที่ไม่รู้จัก = คงข้อความเดิมไว้ (อาจเป็นไวยากรณ์ของระบบอื่นที่ผู้ใช้
            // ตั้งใจใส่) · โทเคนที่รู้จักแต่ยังไม่มีค่า = ว่าง (ห้ามโชว์ {{...}} ให้ผู้ชมเว็บ)
            if (!values.TryGetValue(key, out var v)) return m.Value;
            return v ?? "";
        });
    }

    /// <summary>ประกอบตารางค่าจากบริษัท + เว็บไซต์ (ค่าระดับเว็บชนะ)
    ///
    /// รับเป็นพารามิเตอร์ล้วน ๆ ไม่แตะ DB — ให้ทดสอบได้ตามกฎเหล็ก #4 G</summary>
    public static Dictionary<string, string?> BuildValues(
        string? companyName, string? companyNameEn, string? companyPhone, string? companyEmail,
        string? companyAddress, string? companyTaxId, string? companyWebsite,
        string? siteName = null, string? sitePhone = null, string? siteEmail = null,
        string? siteLineId = null, string? siteFacebook = null, string? siteInstagram = null)
    {
        static string? Pick(string? preferred, string? fallback)
            => !string.IsNullOrWhiteSpace(preferred) ? preferred!.Trim()
             : !string.IsNullOrWhiteSpace(fallback) ? fallback!.Trim() : null;

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["company.name"]    = Pick(null, companyName),
            ["company.nameEn"]  = Pick(null, companyNameEn),
            ["company.phone"]   = Pick(sitePhone, companyPhone),
            ["company.email"]   = Pick(siteEmail, companyEmail),
            ["company.address"] = Pick(null, companyAddress),
            ["company.taxId"]   = Pick(null, companyTaxId),
            ["company.website"] = Pick(null, companyWebsite),
            ["site.name"]       = Pick(siteName, companyName),
            ["site.lineId"]     = Pick(siteLineId, null),
            ["site.facebook"]   = Pick(siteFacebook, null),
            ["site.instagram"]  = Pick(siteInstagram, null),
        };
    }

    /// <summary>ข้อความตัวอย่างที่เคย seed ไว้ → โทเคน
    ///
    /// ใช้ทั้งใน migration (ล้างของที่ค้างในฐาน) และเป็นเอกสารว่า placeholder
    /// หน้าตาแบบไหนบ้างที่เคยหลุดออกไป — **แก้โค้ดอย่างเดียวไม่พอเมื่อของเสีย
    /// ถูก persist ไว้แล้ว** (บทเรียนเดียวกับ `OcrLearnedPatterns.ExtractionRegex`)</summary>
    public static readonly IReadOnlyList<(string Placeholder, string Token)> LegacyPlaceholders = new[]
    {
        ("02-XXX-XXXX",       "{{company.phone}}"),
        ("081-XXX-XXXX",      "{{company.phone}}"),
        ("086-XXX-XXXX",      "{{company.phone}}"),
        ("info@example.com",  "{{company.email}}"),
        ("hello@shop.com",    "{{company.email}}"),
        ("contact@hospital.com", "{{company.email}}"),
        ("hello@consulting.com", "{{company.email}}"),
        ("contact@construct.com", "{{company.email}}"),
        ("hello@realestate.com", "{{company.email}}"),
        ("hello@school.com",  "{{company.email}}"),
        ("ops@logistics.com", "{{company.email}}"),
        ("reservations@hotel.com", "{{company.email}}"),
    };
}

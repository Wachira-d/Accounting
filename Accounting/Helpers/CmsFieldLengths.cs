namespace Accounting.Helpers;

/// <summary>ความยาวคอลัมน์ของช่องที่ CMS ใช้เป็น "คีย์ไม่ซ้ำ" — **ตัวตั้งตัวเดียว**
/// ที่ทั้ง <c>AccountingDbContext</c> (HasMaxLength) และโค้ดที่ตัดข้อความให้พอดี
/// (<see cref="CmsRetiredSlug"/> · <see cref="CmsSlugUniquifier"/>) อ่านร่วมกัน
///
/// ทำไมต้องมี: ตัวตัดข้อความที่ถือเลขของตัวเองจะเพี้ยนทันทีที่มีคนแก้ HasMaxLength
/// ในไฟล์คนละไฟล์ — ตัดยาวไป = insert ล้ม CS ไม่จับ · ตัดสั้นไป = ชื่อเดิมหายจากร่องรอย</summary>
public static class CmsFieldLengths
{
    /// <summary>Site.Slug — คีย์ routing สำรองของเว็บ (unique ต่อบริษัท)</summary>
    public const int SiteSlug = 128;

    /// <summary>Site.Subdomain — ตามข้อจำกัด DNS label (63 ตัวอักษร)</summary>
    public const int SiteSubdomain = 63;

    /// <summary>Site.CustomDomain และ SiteDomain.Domain</summary>
    public const int Domain = 256;

    /// <summary>SitePage.Slug</summary>
    public const int PageSlug = 256;
}

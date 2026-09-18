using System.Globalization;

namespace Accounting.Helpers;

/// <summary>คีย์ CMS ที่ถูก "ปลดออก" (soft-delete / ถูกแทนด้วยของใหม่).
/// unique index ของ CMS **ไม่มีตัวไหนกรอง IsDeleted** (<c>IX_SitePages_SiteId_Slug</c> ·
/// <c>IX_Sites_CompanyId_Slug</c> · <c>IX_Sites_CompanyId_Subdomain</c> ·
/// <c>IX_SiteDomains_Domain</c>) ⇒ แถวเดิมต้องย้ายคีย์ไปให้พ้นทางก่อน ไม่งั้นของใหม่
/// ที่ใช้ชื่อเดิม insert ไม่ได้ และผู้ใช้ได้ 23505 เป็น 500 ที่อ่านไม่ออก.
/// ตัวตัดสินอยู่ที่เดียว — DeletePageAsync · ApplyTemplateAsync · DeleteSiteAsync
/// เรียกตัวนี้ (ห้ามเขียน format string ซ้ำ)</summary>
public static class CmsRetiredSlug
{
    public const string Marker = "--retired-";
    /// <summary>ความยาวปริยาย = SitePage.Slug (ผู้เรียกกลุ่มแรกของ helper ตัวนี้)</summary>
    public const int MaxLength = CmsFieldLengths.PageSlug;

    /// <param name="maxLength">ความยาวคอลัมน์ปลายทาง — **ต้องส่งมาจาก
    /// <see cref="CmsFieldLengths"/> เสมอ** เพราะแต่ละคีย์ยาวไม่เท่ากัน
    /// (Subdomain 63 · SiteSlug 128 · Domain/PageSlug 256) การใช้ค่าปริยายกับ
    /// คอลัมน์ที่สั้นกว่าจะได้สตริงยาวเกินแล้ว insert ล้มโดยคอมไพเลอร์ไม่จับ</param>
    public static string For(string slug, DateTime utcNow, int maxLength = MaxLength)
    {
        var stamp = utcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var suffix = Marker + stamp;
        var head = slug ?? "";
        // ปลดซ้ำ (หน้าที่เคยถูกปลดแล้วถูกปลดอีก เช่น เติมเทมเพลตทับซ้ำ) ต้อง **ไม่**
        // ต่อป้ายซ้อนกันเป็น `x--retired-A--retired-B` — ป้ายซ้อนทำให้ slug ยาวขึ้น
        // เรื่อย ๆ จนถูกตัดหัวทิ้ง แล้วชื่อเดิมของหน้าหายไปจากร่องรอย (กู้ยากตอนสอบสวน)
        if (IsRetired(head))
        {
            var at = head.IndexOf(Marker, StringComparison.Ordinal);
            head = head[..at];
        }
        var room = Math.Max(0, maxLength - suffix.Length);
        if (head.Length > room) head = head[..room];
        return head + suffix;
    }

    public static bool IsRetired(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Contains(Marker, StringComparison.Ordinal);
}

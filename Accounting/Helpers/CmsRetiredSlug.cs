using System.Globalization;

namespace Accounting.Helpers;

/// <summary>slug ของหน้า CMS ที่ถูก "ปลดออก" (soft-delete / ถูกแทนด้วยหน้าเทมเพลตใหม่).
/// unique index <c>IX_SitePages_SiteId_Slug</c> ไม่ได้กรอง IsDeleted ⇒ หน้าเดิมต้องย้าย slug
/// ไปให้พ้นทางก่อน ไม่งั้นหน้าใหม่ slug เดิม insert ไม่ได้. ตัวตัดสินอยู่ที่เดียว —
/// ทั้ง DeletePageAsync และ ApplyTemplateAsync เรียกตัวนี้ (ห้ามเขียน format string ซ้ำ)</summary>
public static class CmsRetiredSlug
{
    public const string Marker = "--retired-";
    /// <summary>ความยาวคอลัมน์ Slug (AccountingDbContext: HasMaxLength(256))</summary>
    public const int MaxLength = 256;

    public static string For(string slug, DateTime utcNow)
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
        var room = MaxLength - suffix.Length;
        if (head.Length > room) head = head[..room];
        return head + suffix;
    }

    public static bool IsRetired(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Contains(Marker, StringComparison.Ordinal);
}

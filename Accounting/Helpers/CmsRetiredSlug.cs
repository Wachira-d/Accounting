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
        var room = MaxLength - suffix.Length;
        if (head.Length > room) head = head[..room];
        return head + suffix;
    }

    public static bool IsRetired(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Contains(Marker, StringComparison.Ordinal);
}

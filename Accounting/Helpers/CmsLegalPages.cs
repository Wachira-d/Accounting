namespace Accounting.Helpers;

/// <summary>
/// รอบ 214 (คำตัดสินข้อ 173 ข้อ B) — นโยบายความเป็นส่วนตัว / ข้อกำหนดการใช้บริการของ<b>ร้าน</b> (เว็บ CMS ของลูกค้า) — ตัวตัดสินเดียว:
/// ลำดับ <b>ลิงก์ภายนอกที่ร้านตั้ง &gt; ข้อความที่ร้านเขียนเอง &gt; แม่แบบของระบบ</b> (<see cref="LegalDocumentDefaults.ShopTemplate"/>)
/// <para>ทุกจุดที่วาดลิงก์ (footer · ช่องยินยอมบนฟอร์มสั่งซื้อ/จอง/ติดต่อ/สมัครสมาชิก · แบนเนอร์คุกกี้) และหน้าแสดงเนื้อความ ใช้ผลจากที่นี่
/// ห้าม inline <c>url ?? ...</c> เอง (กฎเหล็ก #4 A "Resolver กลาง") · ไม่มีทาง "ไม่มีลิงก์" อีกต่อไป — ร้านที่ไม่ได้ตั้งอะไรได้หน้าแม่แบบ
/// ที่มีชื่อ/ที่อยู่/เลขผู้เสียภาษี/ช่องทางติดต่อของร้านจริง (PDPA ม.23 — แจ้งผู้ควบคุมข้อมูลก่อน/ขณะเก็บ)</para>
/// </summary>
public static class CmsLegalPages
{
    public const string ModeExternal = "external";
    public const string ModeCustom = "custom";
    public const string ModeDefault = "default";

    /// <summary>slug ของหน้าภายในบน storefront (<c>/site/{key}/legal/privacy</c> · <c>{โดเมนร้าน}/?slug=legal/privacy</c>) —
    /// อยู่ใต้ <c>legal/</c> ⇒ ไม่ชนหน้า CMS ชื่อ "privacy" ที่ร้านอาจสร้างเองไว้แล้ว (ร้านชี้ไปหน้านั้นได้ด้วยลิงก์ <c>/privacy</c>)</summary>
    public static string Slug(string kind) => kind == LegalDocumentDefaults.KindTerms ? "legal/terms" : "legal/privacy";

    /// <summary>ผลการตัดสินต่อเอกสาร — <c>ExternalUrl</c> มีค่าเฉพาะโหมด external · โหมดอื่นใช้ <c>Slug</c> (หน้าเว็บประกอบเป็น URL ตามแบบเส้นทางของเว็บ)</summary>
    public sealed record Link(string Kind, string Mode, string? ExternalUrl, string Slug);

    public static Link Resolve(string kind, string? externalUrl, string? customMarkdown)
    {
        // ลิงก์ที่ร้านตั้ง: URL เต็ม http(s) หรือ path ในเว็บร้านเอง ("/privacy" = หน้า CMS ที่ร้านทำไว้) — ค่าไม่ปลอดภัย = เหมือนไม่ได้ตั้ง
        if (CmsGalleryBlock.IsSafeUrl(externalUrl?.Trim()))
            return new Link(kind, ModeExternal, externalUrl!.Trim(), Slug(kind));
        if (!string.IsNullOrWhiteSpace(customMarkdown))
            return new Link(kind, ModeCustom, null, Slug(kind));
        return new Link(kind, ModeDefault, null, Slug(kind));
    }

    /// <summary>เนื้อความที่จะแสดงบนหน้าภายใน — ข้อความของร้าน หรือแม่แบบ (โหมด external ก็คืนแม่แบบ — หน้าภายในยังเปิดได้
    /// เผื่อลิงก์ภายนอกล่ม แต่ลิงก์ทุกจุดชี้ไปภายนอก)</summary>
    public static string EffectiveMarkdown(string kind, string? customMarkdown)
        => string.IsNullOrWhiteSpace(customMarkdown) ? LegalDocumentDefaults.ShopTemplate(kind) : customMarkdown;

    /// <summary>ตรวจลิงก์ที่ร้านกรอก — "" = ล้าง (ใช้หน้าของระบบ) · รับ URL เต็ม http(s) หรือ path ในเว็บร้าน (<c>/privacy</c>) ·
    /// คืนข้อความผิด (ไทย) หรือ null = ใช้ได้ (ก่อนรอบ 214 เก็บค่าดิบอะไรก็ได้ — storefront กรองตอนแสดงเท่านั้น)</summary>
    public static string? ValidateExternalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!CmsGalleryBlock.IsSafeUrl(url.Trim()))
            return "ลิงก์นโยบาย/ข้อกำหนดต้องเป็น URL เต็มที่ขึ้นต้นด้วย https:// หรือ path ในเว็บนี้ เช่น /privacy (หรือเว้นว่างเพื่อใช้หน้าของระบบ)";
        return null;
    }
}

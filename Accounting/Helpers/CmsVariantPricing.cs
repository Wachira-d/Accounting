using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวเลือกย่อยของสินค้าเว็บ (variant: ไซซ์/สี/รุ่น) — ราคา · สต็อก · SKU · การเลือก** — รอบ 209 P6 · R-209-E5/D9 · คำตัดสิน D-Q5
///
/// <para><b>ที่มา</b>: <c>SiteProductVariant</c> มีแค่ CRUD (ราคา/สต็อก/SKU ต่อ variant ไม่มีผู้อ่านเลย) — หน้าร้านไม่มีตัวเลือก ·
/// ตะกร้าเก็บข้อความอิสระ ⇒ ร้านกรอก "M-ดำ ราคา 390 สต็อก 5" ได้แต่ระบบขายราคาแม่และตัดสต็อกแม่เสมอ (silent no-op)</para>
///
/// <para><b>กติกา</b> (สินค้าที่ไม่มี variant ใช้งานอยู่ = เส้นเดิมทุกตัวอักษร):</para>
/// <list type="bullet">
/// <item><b>ราคา</b>: <c>variant.Price</c> ตั้งไว้ ⇒ <b>แทน</b>ราคาฐาน (ราคาเว็บ/ERP ของแม่) · ไม่ตั้ง ⇒ ราคาฐาน · ตัวเลือกตามสั่ง (modifier) บวกทับทีหลังเหมือนเดิม</item>
/// <item><b>สต็อก</b> (D-Q5): หนึ่ง variant ที่ผูกสินค้า ERP (<c>ErpProductId</c>) = ตัดสต็อกสินค้านั้นผ่าน <c>IStockLedger</c> · ไม่ผูก ⇒ ตัดสต็อกสินค้าแม่ ·
///   ช่อง <c>SiteProductVariant.Stock</c> <b>ไม่ใช่สต็อกจริง</b> (ไม่มีใครเขียนตามการขาย ⇒ ถ้าอ่านจะขายเกิน) — หน้าแอดมินแสดงสต็อกจาก ERP แทน</item>
/// <item><b>SKU</b>: variant ⇒ SKU ของ variant บนบรรทัดออเดอร์ + ในคำอธิบายบรรทัดเอกสาร (ไม่ส่งเป็นรหัสสินค้าของบรรทัด — เอกสารเว็บไม่ตัดสต็อกซ้ำตอนอนุมัติ)</item>
/// <item><b>การเลือก</b>: สินค้าที่มี variant ใช้งานอยู่ต้องเลือก 1 ตัว (ด่านที่เซิร์ฟเวอร์ — หน้าเว็บแสดงตามผลเดียวกัน) · variant ต้องเป็นของสินค้านั้น · ใช้งานอยู่ ·
///   สินค้าที่ไม่มี variant ส่ง VariantId มา ⇒ ปฏิเสธ (ไม่ทิ้งเงียบ)</item>
/// </list>
/// </summary>
public static class CmsVariantPricing
{
    public const string RuleCode = "CMS-VARIANT";

    /// <summary>ราคาต่อหน่วยก่อนตัวเลือกตามสั่ง — variant ตั้งราคา ⇒ ราคานั้น · ไม่ตั้ง ⇒ ราคาฐาน</summary>
    public static decimal UnitPrice(decimal basePrice, decimal? variantPrice)
        => variantPrice is decimal v && v >= 0m ? v : basePrice;

    /// <summary>สินค้า ERP ที่ถูกตัด/คืนสต็อก — variant ผูกสินค้า ERP ⇒ สินค้านั้น · ไม่ผูก/ไม่มี variant ⇒ สินค้าแม่</summary>
    public static Guid StockProductId(Guid parentProductId, Guid? variantErpProductId)
        => variantErpProductId is Guid g && g != Guid.Empty ? g : parentProductId;

    /// <summary>SKU บนบรรทัดออเดอร์ — variant ⇒ SKU ของ variant · ไม่มี ⇒ SKU สินค้าแม่ (เดิม)</summary>
    public static string? Sku(string? parentSku, SiteProductVariant? variant)
        => variant != null && !string.IsNullOrWhiteSpace(variant.Sku) ? variant.Sku.Trim() : parentSku;

    /// <summary>ชื่อบรรทัด (ไหลไปคำอธิบายบรรทัดเอกสาร ERP) — ไม่มี variant ⇒ ชื่อเดิมทุกตัวอักษร</summary>
    public static string LineName(string baseName, SiteProductVariant? variant)
    {
        if (variant == null) return baseName;
        var label = string.IsNullOrWhiteSpace(variant.Name) ? variant.Sku : variant.Name.Trim();
        var sku = string.IsNullOrWhiteSpace(variant.Sku) ? "" : $" [SKU {variant.Sku.Trim()}]";
        return string.IsNullOrWhiteSpace(label) ? baseName + sku : $"{baseName} - {label}{sku}";
    }

    /// <summary>variant ที่เลือกได้ (ใช้งานอยู่ · ไม่ลบ)</summary>
    public static bool IsSelectable(SiteProductVariant v) => v.IsActive && !v.IsDeleted;

    /// <summary>
    /// ด่านการเลือก — คืนข้อความไทยเมื่อผิด · null = ผ่าน
    /// </summary>
    /// <param name="productHasSelectableVariants">สินค้านี้มี variant ที่เลือกได้อย่างน้อย 1 ตัว</param>
    /// <param name="requestedVariantId">variant ที่ลูกค้าส่งมา</param>
    /// <param name="found">แถวที่หาได้จาก id (กรองบริษัท/สินค้าแล้ว) · null = ไม่พบ</param>
    public static string? SelectionProblem(bool productHasSelectableVariants, Guid? requestedVariantId, SiteProductVariant? found)
    {
        if (requestedVariantId is not Guid vid || vid == Guid.Empty)
            return productHasSelectableVariants ? "กรุณาเลือกตัวเลือกสินค้า (ไซซ์/สี/รุ่น) ก่อนใส่ตะกร้า" : null;
        if (!productHasSelectableVariants || found == null)
            return "ไม่พบตัวเลือกสินค้าที่เลือก — กรุณาเปิดหน้าสินค้าใหม่แล้วเลือกอีกครั้ง";
        if (!IsSelectable(found))
            return $"ตัวเลือก \"{found.Name}\" ปิดการขายแล้ว — กรุณาเลือกตัวเลือกอื่น";
        return null;
    }

    /// <summary>มีของพอขายไหม (ใช้แสดงบนหน้าร้าน) — สินค้าที่ไม่ติดตามสต็อก/สั่งจองล่วงหน้า (<paramref name="inStockOnly"/> = false) ขายได้เสมอ</summary>
    public static bool InStock(decimal available, bool inStockOnly) => !inStockOnly || available > 0m;
}

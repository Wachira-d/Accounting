using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียวของ "สินค้าตัวนี้ใช้ผังบัญชีไหนเป็น **บัญชีคุมสต็อก**" —
/// บัญชีที่ถูก **Dr ตอนรับของเข้า** และ **Cr ตอนตัดออก** ต้องเป็นใบเดียวกัน
/// มิฉะนั้นสองฝั่งไม่มีวันหักล้าง
///
/// <para><b>ที่มา (DECISION_AUDIT_2026-09-18 D5-2):</b> วัสดุสิ้นเปลือง
/// (<see cref="ProductType.Supplies"/>) ถูกบังคับ <c>TrackStock=true</c>
/// (<c>ProductService.CreateAsync</c>) ⇒ ตอน **ซื้อ** ตัว resolver ฝั่งซื้อใน
/// <c>DocumentService</c> เห็นแค่ว่า "TrackStock" แล้ว Dr
/// <c>InventoryAccountId ?? 11500 (สินค้าคงเหลือ)</c> · แต่ตอน **เบิกใช้**
/// <c>ProductService.UseSuppliesAsync</c> Cr <c>SuppliesAccountId ?? 118xx
/// (วัสดุสิ้นเปลือง)</c> ⇒ 11500 มีแต่ฝั่ง Dr และ 118xx มีแต่ฝั่ง Cr —
/// **บวม/ติดลบถาวรทั้งคู่** และไม่มีใครเห็นเพราะงบดุลยังบาลานซ์</para>
///
/// <para>กติกา: วัสดุสิ้นเปลืองอยู่บัญชี "วัสดุสิ้นเปลือง" (118xx) · ที่เหลืออยู่
/// "สินค้าคงเหลือ" (115xx) · ค่าที่ผู้ใช้เลือกไว้บนสินค้าชนะค่า default เสมอ</para>
/// </summary>
public static class InventoryControlAccount
{
    /// <summary>
    /// คืนบัญชีคุมสต็อกของสินค้าตัวนี้ (null = ผังบัญชีไม่มีใบที่ใช้ได้ —
    /// ผู้เรียกต้องตัดสินใจเองว่าจะ fallback เป็นค่าใช้จ่ายหรือหยุด **ห้ามแต่งเลขผังเอง**)
    /// </summary>
    /// <param name="productType">ชนิดสินค้า</param>
    /// <param name="inventoryAccountId"><c>Product.InventoryAccountId</c> (ผู้ใช้เลือกเอง)</param>
    /// <param name="suppliesAccountId"><c>Product.SuppliesAccountId</c> (ผู้ใช้เลือกเอง)</param>
    /// <param name="defaultInventoryAccountId">บัญชีสินค้าคงเหลือ default ของบริษัท (11500/115)</param>
    /// <param name="defaultSuppliesAccountId">บัญชีวัสดุสิ้นเปลือง default ของบริษัท (118xx)</param>
    public static Guid? Resolve(ProductType productType, Guid? inventoryAccountId,
        Guid? suppliesAccountId, Guid? defaultInventoryAccountId,
        Guid? defaultSuppliesAccountId = null)
    {
        if (productType == ProductType.Supplies)
        {
            // ผู้ใช้เลือกบัญชีวัสดุไว้ → ชนะ · ไม่งั้นใช้ default 118xx
            // fallback สุดท้าย = บัญชีสินค้าคงเหลือ: ผังบัญชีที่ไม่มี 118 เลย
            // ยังต้องให้ **ทั้งสองฝั่งลงใบเดียวกัน** ได้ (ผิดหมวดดีกว่าไม่หักล้าง
            // — ผิดหมวดเห็นในงบและแก้ได้ครั้งเดียว ส่วนไม่หักล้างสะสมทุกเดือน)
            return suppliesAccountId ?? defaultSuppliesAccountId
                ?? inventoryAccountId ?? defaultInventoryAccountId;
        }

        return inventoryAccountId ?? defaultInventoryAccountId;
    }

    /// <summary>คำนำหน้ารหัสบัญชีที่ควรใช้ค้น default ของแต่ละชนิด — ให้ผู้เรียก
    /// ทุกฝั่งค้นด้วยเกณฑ์เดียวกัน (ห้ามพิมพ์ "118"/"115" กระจายในแต่ละไฟล์)</summary>
    public static string DefaultAccountPrefix(ProductType productType)
        => productType == ProductType.Supplies ? "118" : "115";
}

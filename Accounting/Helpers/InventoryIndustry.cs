using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// "กิจการแบบนี้ ซื้อของมาเก็บเป็นสินค้าคงเหลือหรือเปล่า" — ตัวตัดสิน<b>ตัวเดียว</b>
/// ของทั้งระบบ
///
/// ═══ ทำไมต้องมีที่เดียว (ผลตรวจ OCR 2026-09-06 · T1-16 + T1-19) ═══
/// สองที่ถามคำถามเดียวกันแต่ไม่เคยถามเลย:
///  • <c>ExpenseCategoryResolver</c> เห็นชื่อผู้ขายเป็นแบรนด์ค้าส่ง (Makro/HomePro/
///    ไทวัสดุ/Tops…) แล้วบังคับผัง <c>51110 ต้นทุนสินค้า</c> ⇒ บริษัทซอฟต์แวร์ที่
///    ซื้อกาแฟ/กระดาษ A4 ที่ Makro ได้ COGS ทุกใบ ⇒ กำไรขั้นต้นเพี้ยน
///  • <c>SuggestedEntryMode</c> ตัดสิน Stock/Expense จาก "ผู้ขายรายนี้เคยนำเข้า
///    สต๊อกมาก่อนไหม" อย่างเดียว ⇒ บริษัทค้าขายที่<b>เพิ่งเริ่มใช้ระบบ</b>
///    ทุกใบซื้อสินค้าลงเป็นค่าใช้จ่าย (cold-start ผิดทิศเสมอ — ขัดกฎเหล็ก #1 ข้อ 3)
///
/// ═══ หมายเหตุความถูกต้องของผลตรวจ ═══
/// รายงาน T1 เขียนว่าให้ดู <c>BusinessType ∈ {Trading, Manufacturing}</c> — **ผิด**
/// <c>BusinessType</c> ในเรพนี้คือ<b>รูปแบบนิติบุคคล</b> (บุคคลธรรมดา/นิติบุคคล/
/// ห้างหุ้นส่วน) แกนที่ถูกคือ <c>IndustryType</c> (ตรวจแล้วบันทึกไว้ตามกติกา
/// "ผลตรวจผิดได้ทั้งสองทาง — ต้องบันทึกว่าผิด ไม่ใช่ข้ามเงียบ")
/// </summary>
public static class InventoryIndustry
{
    /// <summary>คืน true เมื่อ "ซื้อของจากร้านค้าส่ง = ของเข้าร้าน/วัตถุดิบ" เป็นเรื่องปกติ
    /// ของกิจการแบบนั้น · <b>null / General = ไม่รู้</b> → คืน false แต่ผู้เรียกต้อง
    /// ปฏิบัติกับ "ไม่รู้" ต่างจาก "รู้ว่าไม่ใช่" (ห้ามลงโทษกิจการที่ยังไม่ได้ตั้งค่า)</summary>
    public static bool KeepsInventory(IndustryType? industry) => industry switch
    {
        IndustryType.Trading => true,          // ซื้อมาขายไป
        IndustryType.Manufacturing => true,    // วัตถุดิบ → §87(3) รายงานสินค้าและวัตถุดิบ
        IndustryType.Retail => true,           // ค้าปลีก
        IndustryType.Ecommerce => true,        // ขายออนไลน์ = ถือสต๊อก
        IndustryType.Restaurant => true,       // วัตถุดิบอาหาร
        IndustryType.Cafe => true,             // เมล็ดกาแฟ/นม/บรรจุภัณฑ์
        IndustryType.Agriculture => true,      // ปัจจัยการผลิต
        IndustryType.Construction => true,     // วัสดุก่อสร้าง → ต้นทุนงาน
        _ => false,
    };

    /// <summary>true เมื่อระบบ<b>ยังไม่รู้</b>ว่ากิจการเป็นแบบไหน — ต่างจาก
    /// "รู้ว่าไม่ถือสต๊อก" ตรงที่ห้ามใช้เป็นเหตุผลในการล้มกฎใด</summary>
    public static bool IsUnknown(IndustryType? industry)
        => industry is null or IndustryType.General or IndustryType.Other;
}

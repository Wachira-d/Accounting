using System;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>โหมดบันทึกที่ระบบ<b>แนะนำ</b> (ผู้ใช้ยังเลือกเองได้เสมอ)</summary>
public enum OcrEntryMode { Expense = 0, Stock = 1 }

/// <param name="Mode">โหมดที่แนะนำ</param>
/// <param name="Reason">เหตุผลภาษาไทยที่เอาไปโชว์ได้ตรง ๆ — ห้ามคืน bool เปล่า
/// แล้วให้แต่ละหน้าจอไปแต่งคำเอง (= สำเนามือชุดที่สองรอ drift)</param>
public sealed record OcrEntryModeAdvice(OcrEntryMode Mode, string Reason);

/// <summary>
/// "ใบซื้อใบนี้ควรบันทึกเข้าสต๊อก หรือลงเป็นค่าใช้จ่าย" — pure, ทดสอบได้
///
/// ═══ ทำไมต้องมี (ผลตรวจ OCR 2026-09-06 · T1-19) ═══
/// เดิม: <c>SuggestedEntryMode = (hasLines &amp;&amp; vendorHasProductHistory) ? "Stock" : "Expense"</c>
/// — ใช้สัญญาณเดียวคือ "ผู้ขายรายนี้เคยมี <c>ProductAliases</c> ไหม" ⇒ บริษัท
/// ค้าขายที่<b>เพิ่งเริ่มใช้ระบบ</b> ยังไม่เคย import อะไรเลย ⇒ ทุกใบซื้อสินค้า
/// ถูกแนะนำเป็นค่าใช้จ่าย ⇒ COGS/สินค้าคงเหลือผิดจนกว่าจะมีคน import ครั้งแรก
/// (cold-start ผิดทิศเสมอ — ขัดกฎเหล็ก #1 ข้อ 3) · ที่แย่กว่าคือ
/// <c>ApplyProductCrossReferenceAsync</c> จับคู่บรรทัดกับ Product master ได้อยู่แล้ว
/// แต่**ผลไม่ถูกใช้ตัดสินตรงนี้เลย** (defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้")
///
/// ═══ กติกา ═══
/// เรียงตามความแข็งของหลักฐาน — ตัวที่ยืนยันจาก<b>ข้อมูลจริงของบริษัท</b> ชนะ
/// การอนุมานจากประเภทกิจการเสมอ · ไม่มีหลักฐานเลย = Expense (ทิศที่แก้ง่ายกว่า
/// เมื่อผิด: ลงค่าใช้จ่ายผิดแก้ด้วยการโอนบัญชี ส่วนนำเข้าสต๊อกผิดต้องกลับรายการ
/// ทั้งการเคลื่อนไหวสินค้า)
/// </summary>
public static class OcrEntryModeAdvisor
{
    /// <param name="lineCount">จำนวนบรรทัดที่ OCR อ่านได้</param>
    /// <param name="linesMatchedToProductMaster">บรรทัดที่จับคู่กับ Product master ของบริษัทได้</param>
    /// <param name="vendorHasProductHistory">ผู้ขายรายนี้เคยมี ProductAliases (เคยนำเข้าสต๊อกมาก่อน)</param>
    /// <param name="industry">ประเภทกิจการของบริษัท (null = ยังไม่ได้ตั้งค่า)</param>
    public static OcrEntryModeAdvice Decide(
        int lineCount, int linesMatchedToProductMaster,
        bool vendorHasProductHistory, IndustryType? industry)
    {
        if (lineCount <= 0)
            return new(OcrEntryMode.Expense, "เอกสารไม่มีรายการสินค้าให้นำเข้าสต๊อก");

        // 1) หลักฐานแข็งที่สุด: เคยนำเข้าสต๊อกจากผู้ขายรายนี้มาก่อนจริง
        if (vendorHasProductHistory)
            return new(OcrEntryMode.Stock, "ผู้ขายรายนี้เคยนำเข้าสินค้าเข้าสต๊อกมาก่อน");

        // 2) บรรทัดตรงกับสินค้าใน Product master ของบริษัทเอง — ใช้ได้ตั้งแต่ใบแรก
        //    (cold-start) แต่ต้องประกอบกับ "กิจการแบบนี้ถือสต๊อกจริง" เพราะบริษัท
        //    บริการก็มี Product master ไว้ขายบริการเป็นรายการได้เหมือนกัน
        var half = (lineCount + 1) / 2;   // ปัดขึ้น — 1 ใน 1 บรรทัดก็ต้องผ่าน
        if (linesMatchedToProductMaster >= half && InventoryIndustry.KeepsInventory(industry))
            return new(OcrEntryMode.Stock,
                $"{linesMatchedToProductMaster}/{lineCount} บรรทัดตรงกับสินค้าในระบบ "
                + $"และกิจการเป็นประเภท{IndustryLabel(industry)} — แนะนำบันทึกเข้าสต๊อก");

        if (linesMatchedToProductMaster >= half)
            return new(OcrEntryMode.Expense,
                $"{linesMatchedToProductMaster}/{lineCount} บรรทัดตรงกับสินค้าในระบบ แต่ประเภทกิจการ"
                + (InventoryIndustry.IsUnknown(industry)
                    ? "ยังไม่ได้ตั้งค่า — ตั้งค่าประเภทกิจการเพื่อให้ระบบแนะนำได้แม่นขึ้น"
                    : "ไม่ใช่กิจการที่ถือสินค้าคงเหลือ"));

        return new(OcrEntryMode.Expense,
            linesMatchedToProductMaster > 0
                ? $"ตรงกับสินค้าในระบบเพียง {linesMatchedToProductMaster}/{lineCount} บรรทัด — ยังไม่พอชี้ว่าเป็นการซื้อเข้าสต๊อก"
                : "ไม่มีบรรทัดใดตรงกับสินค้าในระบบ และผู้ขายไม่เคยนำเข้าสต๊อก");
    }

    /// <summary>ชื่อไทยของประเภทกิจการสำหรับข้อความอธิบาย</summary>
    public static string IndustryLabel(IndustryType? industry) => industry switch
    {
        IndustryType.Trading => "ซื้อมาขายไป",
        IndustryType.Manufacturing => "ผลิต/โรงงาน",
        IndustryType.Retail => "ค้าปลีก",
        IndustryType.Ecommerce => "อีคอมเมิร์ซ",
        IndustryType.Restaurant => "ร้านอาหาร",
        IndustryType.Cafe => "คาเฟ่/เครื่องดื่ม",
        IndustryType.Agriculture => "เกษตร",
        IndustryType.Construction => "รับเหมาก่อสร้าง",
        _ => "อื่น ๆ",
    };
}

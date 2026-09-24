using System;

namespace Accounting.Helpers;

/// <summary>ผลปรับยอดบรรทัดให้เป็น "จำนวน × ราคาต่อหน่วย" ตาม §86/4 — ส่วนต่างกับยอดที่กระดาษพิมพ์ไปอยู่ที่ผลต่างปัดเศษ</summary>
/// <param name="LineGross">ยอดก่อนส่วนลดของบรรทัดที่จะเขียน = round(จำนวน × ราคาต่อหน่วย, 2)</param>
/// <param name="Shift">ส่วนที่บรรทัดเพิ่มขึ้นจากยอดที่พิมพ์ (+0.01 = บรรทัดโตขึ้น 1 สตางค์) · 0 = ไม่แตะ</param>
public readonly record struct LinePriceRounding(decimal LineGross, decimal Shift);

/// <summary>
/// **ผลต่างจากการปัดเศษระดับเอกสาร — ตัวตั้งตัวเดียว** (pure · ไม่ throw)
///
/// ═══ ที่มา (คำตัดสินเจ้าของ รอบ 193 ข้อ 8) ═══
/// <para>ใบ Lazada: ราคาต่อหน่วย 1,228.04 × 4 = 4,912.16 แต่กระดาษพิมพ์ยอดบรรทัด 4,912.15 (ราคาต้นทางคือ 1,314 รวม VAT แล้วถอดกลับ)
/// ⇒ ถ้าบันทึกยอดบรรทัดตามกระดาษ บรรทัด "จำนวน × ราคา − ส่วนลด ≠ ยอด" และพอผู้ใช้เปิดแก้แล้วบันทึก ระบบคิดใหม่เป็น 5,024.01
/// ≠ กระดาษ 5,024.00 · เจ้าของตัดสิน: <b>คงราคา 1,228.04</b> · ยอดบรรทัด = จำนวน × ราคา (§86/4) · ส่วนต่าง −0.01 เป็น
/// <b>ผลต่างจากการปัดเศษ</b> ลงบัญชี <see cref="AccountCode"/> · ห้ามทศนิยม 4 ตำแหน่ง · ห้ามบรรทัดติดลบ (<see cref="DocumentLineKind"/>)</para>
///
/// <para><b>สัญญาของช่อง <c>Document.RoundingAdjustment</c></b> (มีเครื่องหมาย):
/// <c>SubTotal = Σ Line.Amount + RoundingAdjustment</c> · <c>TotalAmount = SubTotal + VAT − WHT</c> (สูตรเดิม) ⇒ ฐานภาษีบนหัวเอกสาร
/// และยอดรวมตรงกระดาษ ขณะที่ทุกบรรทัดยังเป็น จำนวน × ราคา · JE: บรรทัดลงตามยอดบรรทัด ส่วนต่างที่เหลือลง <see cref="AccountCode"/>
/// ในทิศที่ปิดสมดุล (ฝั่งซื้อ −0.01 = Cr · ฝั่งขาย −0.01 = Dr) — <see cref="JournalLine"/></para>
/// </summary>
public static class DocumentRounding
{
    /// <summary>ผังบัญชีผลต่างจากการปัดเศษ (ผังมาตรฐาน — เพิ่มรอบ 193 · migration ใส่ให้บริษัทเดิม)</summary>
    public const string AccountCode = "54960";
    public const string AccountName = "ผลต่างจากการปัดเศษ";

    /// <summary>ผลต่างปัดเศษต้องเล็กกว่า 1 บาท (ค่าเผื่อยอดรวม ±1 บาท — คำตัดสินข้อ 13 คงเดิม) — มากกว่านี้ไม่ใช่เศษ</summary>
    public const decimal MaxAbs = 1.00m;

    /// <summary>รหัสกฎสำหรับ audit/ข้อความ</summary>
    public const string RuleCode = "DOC-ROUNDING";

    /// <summary>ตรวจค่าที่ผู้ใช้/ระบบส่งมา — null = ผ่าน · ข้อความไทยพร้อมทางไปต่อ</summary>
    public static string? Validate(decimal rounding)
    {
        if (decimal.Round(rounding, 2) != rounding)
            return "ผลต่างจากการปัดเศษต้องเป็นทศนิยมไม่เกิน 2 ตำแหน่ง";
        if (Math.Abs(rounding) >= MaxAbs)
            return $"ผลต่างจากการปัดเศษ {rounding:N2} ไม่ใช่เศษสตางค์ (ต้องน้อยกว่า {MaxAbs:N2} บาท) — "
                + "ถ้าเป็นส่วนลด/ค่าบริการจริง ให้บันทึกเป็นส่วนลดหรือบรรทัดของตัวเอง";
        return null;
    }

    /// <summary>ยอดก่อนส่วนลดของบรรทัดจากสแกน: ถ้ากระดาษพิมพ์ยอดที่ต่างจาก round(จำนวน × ราคา) แค่เศษปัดของราคาต่อหน่วย
    /// (≤ <see cref="OcrTotalDecomposer.LinePrintedTol"/>) ⇒ บรรทัดใช้ จำนวน × ราคา และคืนส่วนที่บรรทัดโตขึ้นไว้ให้หัวเอกสารหักกลับ
    /// เป็นผลต่างปัดเศษ · ไม่มีราคาต่อหน่วย/ต่างมากกว่านั้น (ส่วนลดรายบรรทัด) ⇒ ไม่แตะ (Shift = 0)</summary>
    /// <param name="quantity">จำนวน</param>
    /// <param name="unitPrice">ราคาต่อหน่วยที่พิมพ์</param>
    /// <param name="printedGross">ยอดก่อนส่วนลดที่ใช้กระทบยอด (<see cref="OcrTotalDecomposer.LineGross"/> — ยอดที่พิมพ์ชนะเศษปัด)</param>
    public static LinePriceRounding FromPrintedLine(decimal? quantity, decimal? unitPrice, decimal printedGross)
    {
        if (unitPrice is not decimal up) return new(printedGross, 0m);
        var q = quantity ?? 1m;
        var calc = Math.Round(q * up, 2, MidpointRounding.AwayFromZero);
        var diff = calc - printedGross;
        if (diff == 0m || Math.Abs(diff) > OcrTotalDecomposer.LinePrintedTol || calc <= 0m)
            return new(printedGross, 0m);
        return new(calc, diff);
    }

    /// <summary>ขา JE ของผลต่างปัดเศษ: <paramref name="imbalance"/> = Σ เดบิต − Σ เครดิต ก่อนลงขานี้ ·
    /// ลงเฉพาะเมื่อความไม่สมดุล "เท่ากับ" ผลต่างที่ประกาศไว้บนเอกสารพอดี (ไม่งั้นเป็นความผิดอื่น ให้ด่านสมดุลเดิมฟ้อง) ·
    /// คืน (เดบิต, เครดิต) หรือ null เมื่อไม่ต้อง/ไม่ควรลง</summary>
    /// <param name="imbalance">Σ Dr − Σ Cr ของ JE ที่สร้างแล้ว (สกุลบาท)</param>
    /// <param name="roundingThb">ผลต่างปัดเศษของเอกสารแปลงเป็นบาทแล้ว</param>
    public static (decimal Debit, decimal Credit)? JournalLine(decimal imbalance, decimal roundingThb)
    {
        if (roundingThb == 0m || imbalance == 0m) return null;
        // สัญญา: SubTotal = Σ บรรทัด + ปัดเศษ ⇒ ขาที่ลงตามยอดรวม (เจ้าหนี้/ลูกหนี้/เงินสด) ต่างจากขาบรรทัดเท่า −ปัดเศษ
        if (Math.Abs(Math.Abs(imbalance) - Math.Abs(roundingThb)) > DocumentSettlementState.Tolerance) return null;
        return imbalance > 0m ? (0m, imbalance) : (-imbalance, 0m);
    }
}

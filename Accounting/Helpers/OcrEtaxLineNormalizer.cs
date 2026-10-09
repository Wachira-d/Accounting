using System;

namespace Accounting.Helpers;

/// <summary>ราคาต่อหน่วยของบรรทัด e-Tax XML รวม VAT หรือยังไม่รวม — พิสูจน์ด้วยเลขของบรรทัดเอง (<see cref="OcrEtaxLineNormalizer.Normalize"/>)</summary>
public enum OcrEtaxPriceBasis
{
    /// <summary>พิสูจน์ไม่ได้ (ไม่มีจำนวน/ราคา/ยอด · มีค่าบริการรายบรรทัด · ตัวเลขไม่ลงตัวทั้งสองทาง) ⇒ พฤติกรรมเดิมทุกตัวอักษร</summary>
    Unknown = 0,
    /// <summary>จำนวน × ราคา − ส่วนลด ≈ <c>NetLineTotalAmount</c> (ยอดก่อน VAT) — ราคาก่อน VAT (Shopee)</summary>
    ExclusiveOfVat = 1,
    /// <summary>จำนวน × ราคา − ส่วนลด ≈ <c>NetIncludingTaxesLineTotalAmount</c> (ยอดรวม VAT) — ราคารวม VAT (CRC ไทวัสดุ)</summary>
    InclusiveOfVat = 2,
}

/// <summary>ตัวเลขของบรรทัดหนึ่งตามที่ e-Tax XML (ขมธอ.3-2560) ประกาศ — ไม่มีการคำนวณใด ๆ</summary>
/// <param name="BilledQuantity"><c>SpecifiedLineTradeDelivery/BilledQuantity</c></param>
/// <param name="GrossUnitPrice"><c>GrossPriceProductTradePrice/ChargeAmount</c> (ผู้ขายบางรายรวม VAT · บางรายไม่รวม)</param>
/// <param name="LineAllowance">Σ <c>SpecifiedTradeAllowanceCharge/ActualAmount</c> ระดับบรรทัดที่ <c>ChargeIndicator=false</c></param>
/// <param name="LineCharge">Σ ระดับบรรทัดที่ <c>ChargeIndicator=true</c> (ค่าบริการเพิ่ม) — มี ⇒ ไม่ตัดสิน</param>
/// <param name="VatRatePercent"><c>ApplicableTradeTax/CalculatedRate</c> ของบรรทัด (7 · 0)</param>
/// <param name="NetAmount"><c>NetLineTotalAmount</c> — ยอดก่อน VAT หลังส่วนลดบรรทัด</param>
/// <param name="NetIncludingVatAmount"><c>NetIncludingTaxesLineTotalAmount</c> — ยอดรวม VAT หลังส่วนลดบรรทัด</param>
public readonly record struct OcrEtaxLineFacts(
    decimal? BilledQuantity, decimal? GrossUnitPrice, decimal? LineAllowance, decimal? LineCharge,
    decimal? VatRatePercent, decimal? NetAmount, decimal? NetIncludingVatAmount);

/// <summary>บรรทัดที่เอกสาร (ราคาก่อน VAT) ควรถือ: <c>round(จำนวน × ราคา, 2) − ส่วนลด = ยอด</c></summary>
/// <param name="Basis">ผลพิสูจน์ราคารวม/ไม่รวม VAT</param>
/// <param name="Quantity">จำนวนตาม XML — <b>ไม่เคยหารจากยอด</b></param>
/// <param name="UnitPrice">ราคาต่อหน่วย <b>ก่อน VAT</b> (ทศนิยม 2 ตำแหน่ง — คำตัดสินรอบ 193 ข้อ 8 ห้าม 4 ตำแหน่ง)</param>
/// <param name="LineDiscount">ส่วนลดรายบรรทัด <b>ก่อน VAT</b> (null = ไม่มี) — ตัวที่ทำให้สมการบรรทัดลงตัวพอดี</param>
/// <param name="Amount">= <c>NetLineTotalAmount</c> ของ XML</param>
/// <param name="QuantityFromDocument">true = จำนวนพิสูจน์แล้วจากเอกสารที่ลงนาม ⇒ ตัวกัน "จำนวนระเบิด" ห้ามเขียนทับ</param>
public readonly record struct OcrEtaxLine(
    OcrEtaxPriceBasis Basis, decimal? Quantity, decimal? UnitPrice, decimal? LineDiscount, decimal? Amount,
    bool QuantityFromDocument);

/// <summary>
/// <b>ตัวตัดสินตัวเดียว: บรรทัด e-Tax XML → บรรทัดเอกสาร (ราคาก่อน VAT)</b> (pure · ไม่ throw)
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-10-09 · สแกน f1690d11 · ใบ CRC ไทวัสดุ SRCIE26100075384) ═══
/// <para>ผู้ขายรายนี้ประกาศ <c>GrossPriceProductTradePrice</c> เป็นราคา<b>รวม VAT</b> และส่วนลดบรรทัด
/// (<c>SpecifiedTradeAllowanceCharge</c>) เป็นยอด<b>รวม VAT</b> — บรรทัด 1: 3 × 37.00 − 26.68 = 84.32 (รวม VAT) ⇒ 78.80 ก่อน VAT.
/// ตัวสกัดเดิมเก็บแค่ จำนวน/ราคา/ยอด แล้วตัวกัน "จำนวนระเบิด" (<c>SanitizeVatSplitArtifacts</c>) เห็น 3 × 37 ≠ 78.80
/// จึง "แก้" จำนวน = 78.80 ÷ 37 = 2.13 · ค่าขนส่ง 120 × 1.00 กลายเป็นจำนวน 37.38 · ราคา 37 (รวม VAT) อยู่ในเอกสารราคาก่อน VAT ·
/// ส่วนลด 0% · ด่าน [Gateway] ฟ้อง 9 บรรทัด + ผลต่างปัดเศษ −0.03 ที่ไม่มีบนกระดาษ</para>
///
/// <para><b>กติกา</b> (ตัวเลขของบรรทัดเองเป็นหลักฐาน — ไม่เดาจากชื่อผู้ขาย):
/// round(จำนวน × ราคา) − ส่วนลด ≈ ยอดก่อน VAT ⇒ ราคาก่อน VAT · ≈ ยอดรวม VAT ⇒ ราคารวม VAT ⇒ ถอด VAT จากราคา
/// (round(ราคา × 100/(100+อัตรา), 2)) แล้วส่วนลดก่อน VAT = round(จำนวน × ราคาก่อน VAT) − ยอดก่อน VAT (ลงตัวพอดีโดยการสร้าง) ·
/// ไม่ลงตัวทั้งสองทาง / ไม่มี BilledQuantity / มีค่าบริการรายบรรทัด ⇒ <see cref="OcrEtaxPriceBasis.Unknown"/> คงค่าเดิม (จำนวน·ราคา·ยอด ตาม XML)</para>
///
/// <para><b>ส่วนลดรวมหัวใบ</b> (<c>AllowanceTotalAmount</c> 1,080.00 = Σ ส่วนลดบรรทัดรวม VAT) <b>อยู่ในยอดบรรทัดแล้ว</b> —
/// ห้ามหักซ้ำ (หัวใบใช้ LineTotal − TaxBasis = 0 ตามเดิม)</para>
/// </summary>
public static class OcrEtaxLineNormalizer
{
    /// <summary>ความคลาดของ จำนวน × ราคา กับยอดที่ XML ประกาศ — เศษปัดของราคาต่อหน่วย 1 สตางค์ (ตัวเดียวกับ <see cref="OcrTotalDecomposer.LinePrintedTol"/>)</summary>
    public const decimal Tol = OcrTotalDecomposer.LinePrintedTol;

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>ตัดสินบรรทัดเดียว — ดูกติกาที่หัวคลาส</summary>
    public static OcrEtaxLine Normalize(OcrEtaxLineFacts f)
    {
        var keep = new OcrEtaxLine(OcrEtaxPriceBasis.Unknown, f.BilledQuantity, f.GrossUnitPrice, null, f.NetAmount, false);
        if (f.BilledQuantity is not decimal q || q <= 0m) return keep;
        if (f.GrossUnitPrice is not decimal g || g < 0m) return keep;
        if (f.NetAmount is not decimal net || net < 0m) return keep;
        if (f.LineCharge is > 0m) return keep;
        var allowance = f.LineAllowance ?? 0m;
        if (allowance < 0m) return keep;

        var afterAllowance = R2(q * g) - allowance;
        var matchesNet = Math.Abs(afterAllowance - net) <= Tol;
        var matchesIncl = f.NetIncludingVatAmount is decimal ni && ni != net && Math.Abs(afterAllowance - ni) <= Tol;
        if (matchesNet == matchesIncl) return keep;   // ไม่ลงตัวทั้งคู่ หรือกำกวม (ยอดเล็กจน VAT < 2 สตางค์)

        if (matchesNet)
        {
            // ราคาก่อน VAT อยู่แล้ว — จำนวน/ราคาตาม XML · ไม่มีส่วนลด = พฤติกรรมเดิม (เศษ 1 สตางค์ไปทางผลต่างปัดเศษตามเดิม)
            decimal? disc = allowance > 0m && R2(q * g) - net > 0m ? R2(q * g) - net : null;
            return new OcrEtaxLine(OcrEtaxPriceBasis.ExclusiveOfVat, q, g, disc, net, true);
        }

        // ราคารวม VAT — อัตราจากบรรทัด ไม่มีก็อนุมานจากยอดสองตัวของบรรทัดเอง (ยอดรวม VAT ÷ ยอดก่อน VAT)
        var rate = f.VatRatePercent is decimal vr && vr > 0m
            ? vr
            : net > 0m ? Math.Round((f.NetIncludingVatAmount!.Value / net - 1m) * 100m, 0, MidpointRounding.AwayFromZero) : 0m;
        if (rate <= 0m) return keep;
        var unitEx = R2(g * 100m / (100m + rate));
        var grossEx = R2(q * unitEx);
        decimal? discEx = allowance > 0m && grossEx - net > 0m ? grossEx - net : null;
        return new OcrEtaxLine(OcrEtaxPriceBasis.InclusiveOfVat, q, unitEx, discEx, net, true);
    }

    /// <summary>ส่วนลดรายบรรทัดที่ "อธิบายยอดได้จริง": round(จำนวน × ราคา) − ส่วนลด ≈ ยอด (±<see cref="Tol"/>) ⇒ คืนส่วนลด · ไม่งั้น 0
    /// <para>ตัวเดียวที่ตัวกันจำนวนระเบิด · ด่าน [Gateway] · ตัวสร้างบรรทัดเอกสาร ใช้ตัดสินว่า "จำนวน × ราคา ≠ ยอด" เป็นส่วนลด ไม่ใช่จำนวนผิด</para></summary>
    public static decimal ProvenLineDiscount(decimal? quantity, decimal? unitPrice, decimal? amount, decimal? lineDiscount)
    {
        if (lineDiscount is not decimal d || d <= 0m) return 0m;
        if (unitPrice is not decimal up || up <= 0m || amount is not decimal a) return 0m;
        var gross = R2((quantity ?? 1m) * up);
        if (d > gross) return 0m;
        return Math.Abs(gross - d - a) <= Tol ? d : 0m;
    }

    /// <summary>ยอดบรรทัดหลังผู้ใช้แก้จำนวน/ราคาในหน้ารีวิว: round(จำนวน × ราคา) − ส่วนลดบรรทัด · ส่วนลดเกินยอดก่อนลด ⇒ คืน <c>Discount = null</c>
    /// (ทิ้งส่วนลด — ยอดติดลบไม่มีจริง) · เดิมคำนวณ round(จำนวน × ราคา) ตรง ๆ ⇒ บรรทัด e-Tax ที่มีส่วนลดกลับไปเป็นยอดก่อนลดเงียบ ๆ</summary>
    public static (decimal Amount, decimal? Discount) AmountAfterLineDiscount(decimal quantity, decimal unitPrice, decimal? lineDiscount)
    {
        var gross = R2(quantity * unitPrice);
        if (lineDiscount is decimal d && d > 0m && d <= gross) return (gross - d, d);
        return (gross, null);
    }
}

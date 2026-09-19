namespace Accounting.Helpers;

/// <summary>บรรทัดสินค้า/บริการบนบิล POS ตามที่ตรึงไว้ตอนปิดบิล (ยอด**ก่อน**ส่วนลดระดับบิล)</summary>
public readonly record struct PosInvoiceSourceLine(
    string Description,
    string? ProductCode,
    string? Unit,
    decimal Quantity,
    decimal LineGross);

/// <summary>บรรทัดที่จะพิมพ์ลงใบกำกับภาษีเต็มรูป — เป็นยอด**ไม่รวม VAT** (Amount)
/// คู่กับ VAT ของบรรทัดนั้น ตามคอนเวนชันของ <c>DocumentLine</c></summary>
public readonly record struct PosInvoiceLine(
    string Description,
    string? ProductCode,
    string? Unit,
    decimal Quantity,
    decimal UnitPriceNet,
    decimal AmountNet,
    decimal VatAmount,
    decimal VatRate);

/// <summary>
/// **แปลงบิล POS → บรรทัดใบกำกับภาษีเต็มรูป** (§86/4)
///
/// ═══ ที่มา (DECISION_AUDIT_2026-09-18 · D8-1 · P0) ═══
/// <c>IssueTaxInvoiceAsync</c> เดิมสร้างบรรทัดจาก <c>item.TotalAmount</c>/<c>item.VatAmount</c>
/// ตรง ๆ ซึ่งเป็นยอด**ก่อน**หักส่วนลดท้ายบิล/คูปอง และ**ไม่มี**ค่าบริการ
/// (<c>RecalculateOrder</c> หักและบวกที่ระดับออเดอร์) ⇒
/// <b>ยอดบนใบกำกับ ≠ เงินที่ลูกค้าจ่าย ≠ ยอดใน JE</b> — บิล 1,000 ลด 10% ลูกค้าจ่าย 900
/// แต่ใบกำกับเขียน 1,000 ⇒ ผู้ซื้อเคลมภาษีซื้อเกิน · ผู้ขายรายงานภาษีขายไม่ตรง GL
///
/// ═══ กติกาของตัวสร้างนี้ ═══
/// <list type="number">
/// <item><b>Σ บรรทัด (รวม VAT) = ยอดที่ลูกค้าจ่ายสำหรับสินค้า/บริการ</b>
///   (<c>TotalAmount + RoundingAmount</c> = <c>NetAmount − TipAmount</c>) — ถ้าไม่ลงตัว
///   <b>โยนทิ้ง ไม่ออกใบ</b> (ใบกำกับที่ยอดไม่ตรงคือเอกสารเท็จ ห้ามเงียบ)</item>
/// <item><b>ทิปไม่อยู่บนใบกำกับ</b> — ทิปเป็นเงินที่ถือแทนพนักงาน (Cr หนี้สิน ไม่ใช่รายได้)
///   ไม่ใช่ค่าตอบแทนการขาย จึงไม่มีฐาน VAT (ตรงกับ JE ที่หักทิปออกจากรายได้แล้ว)</item>
/// <item><b>ส่วนลด/คูปองเป็นบรรทัดติดลบที่ "แบ่ง VAT ติดลบ" ไปด้วย</b> — ไม่ใช่ VAT 0%
///   เพราะส่วนลดทำให้<b>ฐานภาษีลดลงจริง</b> (§79 "มูลค่าที่ได้รับ") · การใส่ VAT 0%
///   ให้บรรทัดส่วนลด (แบบที่ CMS ทำอยู่ — D8-5) ทำให้ฐาน VAT สูงกว่าเงินที่รับจริง</item>
/// <item><b>บรรทัดปัดเศษไม่มี VAT</b> — เป็นการปัดเงินทอนระดับบิล (≤ 0.50 บาท) ไม่ใช่
///   ค่าสินค้า และ VAT หัวใบถูกตรึงไปกับ JE แล้ว ห้ามขยับ</item>
/// <item><b>Σ VAT รายบรรทัด = VAT หัวใบเป๊ะ</b> — แบ่งตามสัดส่วนแล้วโยน "เศษสตางค์"
///   ให้บรรทัดที่ยอดใหญ่ที่สุด (ปัดรายบรรทัดอิสระจะเพี้ยน 1-2 สตางค์เสมอ)</item>
/// </list>
///
/// <para>ทำไมไม่ใช้ <c>item.VatAmount</c> ที่ตรึงไว้: ค่านั้นคิดจากยอด**ก่อน**ส่วนลด
/// ระดับบิล จึงบวกกันแล้วไม่มีวันเท่ากับ <c>order.VatAmount</c> (ซึ่งคิดจากยอดหลังส่วนลด
/// + ค่าบริการ) — ใบกำกับที่ Σ VAT ไม่ตรงหัวใบ ผู้ตรวจอ่านแล้วตีเป็นใบไม่สมบูรณ์</para>
/// </summary>
public static class PosTaxInvoiceLines
{
    public const string BillDiscountLabel = "ส่วนลดท้ายบิล";
    public const string CouponLabel = "ส่วนลดคูปอง";
    public const string ServiceChargeLabel = "ค่าบริการ";
    public const string RoundingLabel = "ปัดเศษ";

    private const string AdjustmentUnit = "ครั้ง";

    /// <summary>ยอดรวมที่ใบกำกับต้องแสดง (รวม VAT) = เงินที่ลูกค้าจ่ายสำหรับสินค้า/บริการ
    /// <b>ไม่รวมทิป</b> — ตัวเดียวกับที่ JE เครดิตเป็นรายได้+ภาษีขาย</summary>
    public static decimal GrossPayableForGoods(decimal orderTotalAmount, decimal roundingAmount)
        => orderTotalAmount + roundingAmount;

    /// <param name="items">บรรทัดสินค้า/บริการที่ยังอยู่บนบิล (ยอดก่อนส่วนลดระดับบิล รวม VAT)</param>
    /// <param name="billDiscountAmount"><c>PosOrder.DiscountAmount</c> — **รวมคูปองแล้ว**
    /// (ห้ามบวก <c>CouponDiscountAmount</c> ซ้ำ — บั๊กเดียวกับ D8-2)</param>
    /// <param name="couponAmount"><c>PosOrder.CouponDiscountAmount</c> — แยกออกมาเป็นบรรทัดของตัวเอง
    /// เพื่อให้ผู้ซื้อ/ผู้ตรวจเห็นว่าส่วนลดมาจากคูปองใบไหน</param>
    /// <param name="headGrossPayable">ยอดรวมที่ต้องลงตัว (ดู <see cref="GrossPayableForGoods"/>)</param>
    /// <param name="headVat"><c>PosOrder.VatAmount</c> ที่ตรึงไว้ตอนปิดบิล (ตัวเดียวกับใน JE)</param>
    public static IReadOnlyList<PosInvoiceLine> Build(
        IEnumerable<PosInvoiceSourceLine> items,
        decimal billDiscountAmount,
        decimal couponAmount,
        string? couponCode,
        decimal serviceChargeAmount,
        decimal roundingAmount,
        decimal headGrossPayable,
        decimal headVat)
    {
        // ── 1. รวมทุกบรรทัดเป็น "ยอดรวม VAT" ก่อน แล้วค่อยแยก VAT ทีเดียว ──
        var gross = new List<(PosInvoiceSourceLine Src, decimal Gross, bool VatBearing)>();
        foreach (var it in items)
            gross.Add((it, it.LineGross, true));

        // คูปองแยกบรรทัดของตัวเอง · ส่วนลด% คือส่วนที่เหลือของ DiscountAmount
        var coupon = couponAmount > 0m ? couponAmount : 0m;
        if (coupon > billDiscountAmount) coupon = billDiscountAmount > 0m ? billDiscountAmount : 0m;
        var pctDiscount = billDiscountAmount - coupon;

        if (pctDiscount > 0m)
            gross.Add((new PosInvoiceSourceLine(BillDiscountLabel, null, AdjustmentUnit, 1m, -pctDiscount),
                -pctDiscount, true));
        if (coupon > 0m)
        {
            var label = string.IsNullOrWhiteSpace(couponCode)
                ? CouponLabel : $"{CouponLabel} {couponCode!.Trim()}";
            gross.Add((new PosInvoiceSourceLine(label, null, AdjustmentUnit, 1m, -coupon), -coupon, true));
        }
        if (serviceChargeAmount != 0m)
            gross.Add((new PosInvoiceSourceLine(ServiceChargeLabel, null, AdjustmentUnit, 1m, serviceChargeAmount),
                serviceChargeAmount, true));
        if (roundingAmount != 0m)
            gross.Add((new PosInvoiceSourceLine(RoundingLabel, null, AdjustmentUnit, 1m, roundingAmount),
                roundingAmount, false));

        // ── 2. ยอดต้องลงตัวกับเงินที่ลูกค้าจ่าย — ไม่ลงตัว = ห้ามออกใบ ──
        var sum = gross.Sum(g => g.Gross);
        if (Math.Abs(sum - headGrossPayable) > 0.005m)
            throw new BusinessRuleException(
                $"ยอดรวมรายบรรทัด ({sum:N2}) ไม่ตรงกับยอดที่ลูกค้าจ่าย ({headGrossPayable:N2}) — "
                + "ออกใบกำกับภาษีไม่ได้ (ใบที่ยอดไม่ตรงคือเอกสารที่ผู้ซื้อเคลมไม่ได้และผู้ขายรายงานผิด) "
                + "· ตรวจรายการบนบิลแล้วลองใหม่",
                "RD-86/4-POS-LINE-SUM");

        // ── 3. แบ่ง VAT หัวใบลงบรรทัดที่มีฐานภาษี · เศษไปที่บรรทัดใหญ่สุด ──
        var vatBase = gross.Where(g => g.VatBearing).Sum(g => g.Gross);
        var vatByIndex = new decimal[gross.Count];
        if (headVat != 0m && vatBase != 0m)
        {
            var residualIndex = -1;
            decimal biggest = -1m;
            for (var i = 0; i < gross.Count; i++)
            {
                if (!gross[i].VatBearing) continue;
                var abs = Math.Abs(gross[i].Gross);
                if (abs > biggest) { biggest = abs; residualIndex = i; }
            }
            decimal allocated = 0m;
            for (var i = 0; i < gross.Count; i++)
            {
                if (!gross[i].VatBearing || i == residualIndex) continue;
                var v = Math.Round(headVat * gross[i].Gross / vatBase, 2, MidpointRounding.AwayFromZero);
                vatByIndex[i] = v;
                allocated += v;
            }
            if (residualIndex >= 0) vatByIndex[residualIndex] = headVat - allocated;
        }

        // ── 4. แปลงเป็นบรรทัดเอกสาร (ยอดไม่รวม VAT + VAT ของบรรทัด) ──
        var result = new List<PosInvoiceLine>(gross.Count);
        for (var i = 0; i < gross.Count; i++)
        {
            var (src, lineGross, _) = gross[i];
            var vat = vatByIndex[i];
            var net = lineGross - vat;
            var qty = src.Quantity > 0m ? src.Quantity : 1m;
            result.Add(new PosInvoiceLine(
                Description: string.IsNullOrWhiteSpace(src.Description) ? "รายการ" : src.Description,
                ProductCode: src.ProductCode,
                Unit: string.IsNullOrWhiteSpace(src.Unit) ? "ชิ้น" : src.Unit,
                Quantity: qty,
                UnitPriceNet: Math.Round(net / qty, 4, MidpointRounding.AwayFromZero),
                AmountNet: net,
                VatAmount: vat,
                VatRate: net != 0m ? Math.Round(vat * 100m / net, 2, MidpointRounding.AwayFromZero) : 0m));
        }
        return result;
    }
}

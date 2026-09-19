namespace Accounting.Helpers;

/// <summary>บรรทัดสินค้า/บริการบนบิล POS ตามที่ตรึงไว้ตอนปิดบิล (ยอด**ก่อน**ส่วนลดระดับบิล)</summary>
public readonly record struct PosInvoiceSourceLine(
    string Description,
    string? ProductCode,
    string? Unit,
    decimal Quantity,
    decimal LineGross);

/// <summary>บรรทัดที่จะพิมพ์ลงใบกำกับภาษีเต็มรูป — เป็นยอด**ไม่รวม VAT** (Amount)
/// คู่กับ VAT ของบรรทัดนั้น ตามคอนเวนชันของ <c>DocumentLine</c> (DOCUMENT_FLOW §6.2b)
///
/// <para><c>DiscountNet</c> = ส่วนแบ่งของ "ส่วนลดระดับบิล" (ส่วนลด% · คูปอง · ปัดเศษลง)
/// ที่ถูกเฉลี่ยลงบรรทัดนี้ คิดเป็นยอด **ก่อน VAT** — <c>AmountNet</c> หักค่านี้ออกไป
/// **แล้ว**. มีไว้ให้ผู้เรียกตั้ง <c>Document.BillDiscountAmount = Σ DiscountNet</c>
/// เพื่อให้ renderer พิมพ์แถว "ส่วนลดท้ายบิล" ให้ผู้ซื้อเห็น (ดูสเปกใน doc-comment
/// ของ <see cref="PosTaxInvoiceLines"/>) — <b>ห้าม</b>เอาไปใส่ <c>DocumentLine.DiscountAmount</c>
/// พร้อมกัน มิฉะนั้นผู้ซื้อจะเห็นส่วนลดสองครั้ง</para></summary>
public readonly record struct PosInvoiceLine(
    string Description,
    string? ProductCode,
    string? Unit,
    decimal Quantity,
    decimal UnitPriceNet,
    decimal AmountNet,
    decimal VatAmount,
    decimal VatRate,
    decimal DiscountNet = 0m);

/// <summary>
/// **แปลงบิล POS → บรรทัดใบกำกับภาษีเต็มรูป** (§86/4)
///
/// ═══ ที่มา (DECISION_AUDIT_2026-09-18 · D8-1 · P0) ═══
/// <c>IssueTaxInvoiceAsync</c> เดิมสร้างบรรทัดจาก <c>item.TotalAmount</c>/<c>item.VatAmount</c>
/// ตรง ๆ ซึ่งเป็นยอด**ก่อน**หักส่วนลดท้ายบิล/คูปอง และ**ไม่มี**ค่าบริการ ⇒
/// <b>ยอดบนใบกำกับ ≠ เงินที่ลูกค้าจ่าย ≠ ยอดใน JE</b>
///
/// ═══ แก้รอบ 184: เลิกใช้บรรทัดติดลบ ═══
/// รอบ 183 ตัวสร้างนี้แก้ยอดให้ตรงด้วยการใส่ <b>บรรทัดติดลบ</b> ("ส่วนลดท้ายบิล −100.00")
/// แล้วประกอบ <c>Document</c> เองโดย<b>ไม่ผ่าน</c> <c>ValidateDocumentLinesAsync</c> ⇒
/// ระบบเดียวมีกติกาสองชุด: เส้นเอกสารกลางห้ามติดลบ (และทำให้ออเดอร์ CMS ที่มีส่วนลด
/// สร้างเอกสารไม่ได้เลย) ส่วน POS ติดลบได้เพราะเลี่ยงด่าน ⇒ POS ย้ายเข้าเส้นกลางไม่ได้
/// ตลอดกาล. คำตัดสิน + หลักฐาน (§86/4(5) · e-Tax <c>SpecifiedTradeAllowanceCharge</c> ·
/// เครดิตติดลบใน JE · <c>DepositAppliedAmount</c> ที่ประกาศว่า "ห้าม line ติดลบ")
/// อยู่ที่ <see cref="DocumentLineKind"/> ซึ่งเป็น**ตัวตั้งตัวเดียว**ของทั้งเรพ
///
/// ═══ กติกาของตัวสร้างนี้ ═══
/// <list type="number">
/// <item><b>Σ บรรทัด (รวม VAT) = ยอดที่ลูกค้าจ่ายสำหรับสินค้า/บริการ</b>
///   (<c>TotalAmount + RoundingAmount</c> = <c>NetAmount − TipAmount</c>) — ถ้าไม่ลงตัว
///   <b>โยนทิ้ง ไม่ออกใบ</b> (ใบกำกับที่ยอดไม่ตรงคือเอกสารเท็จ ห้ามเงียบ)</item>
/// <item><b>ทุกบรรทัดเป็นบวก</b> — ส่วนลดท้ายบิล/คูปอง/ปัดเศษ**ลง** ไม่เป็นบรรทัดของตัวเอง
///   แต่ถูกเฉลี่ย pro-rata ลงบรรทัดที่มีฐานภาษี ผ่าน
///   <see cref="DocumentLineKind.AllocateDeduction"/> ⇒ <b>ฐาน VAT ลดลงจริงตาม §79</b>
///   เหมือนเดิมทุกบาท เปลี่ยนแค่ "ที่อยู่" ของยอดหัก</item>
/// <item><b>ทิปไม่อยู่บนใบกำกับ</b> — ทิปเป็นเงินที่ถือแทนพนักงาน (Cr หนี้สิน ไม่ใช่รายได้)
///   ไม่ใช่ค่าตอบแทนการขาย จึงไม่มีฐาน VAT (ตรงกับ JE ที่หักทิปออกจากรายได้แล้ว)</item>
/// <item><b>ปัดเศษขึ้นเป็นบรรทัดที่ไม่มี VAT</b> — เป็นการปัดเงินทอนระดับบิล (≤ 0.50 บาท)
///   ไม่ใช่ค่าสินค้า · ปัดเศษ**ลง**เข้าไปรวมกับยอดหักระดับบิล (บรรทัดติดลบไม่มีแล้ว)</item>
/// <item><b>Σ VAT รายบรรทัด = VAT หัวใบเป๊ะ</b> — แบ่งตามสัดส่วนแล้วโยน "เศษสตางค์"
///   ให้บรรทัดที่ยอดใหญ่ที่สุด (ปัดรายบรรทัดอิสระจะเพี้ยน 1-2 สตางค์เสมอ)</item>
/// <item><b><c>VatRate</c> ต้องเป็นอัตราตามกฎหมาย (0 หรือ 7) ไม่ใช่ผลหารที่คำนวณได้</b> —
///   เดิมบรรทัดที่รับเศษ VAT ได้อัตรา 6.99/7.01 ซึ่งไหลตรงเข้า
///   <c>TaxService</c> (<c>Lines.Where(l =&gt; l.VatRate &gt; 0).Max(...)</c> = อัตราที่พิมพ์
///   ในรายงานภาษีขาย/ภ.พ.30) และ <c>EtaxInvoiceService</c> (<c>CalculatedRate</c> ทั้งหัวใบ
///   และรายบรรทัดของ XML ที่ยื่น RD) ⇒ ใบกำกับที่ประกาศอัตรา 7.01% ต่อสรรพากร</item>
/// </list>
///
/// <para>ทำไมไม่ใช้ <c>item.VatAmount</c> ที่ตรึงไว้: ค่านั้นคิดจากยอด**ก่อน**ส่วนลด
/// ระดับบิล จึงบวกกันแล้วไม่มีวันเท่ากับ <c>order.VatAmount</c> (ซึ่งคิดจากยอดหลังส่วนลด
/// + ค่าบริการ) — ใบกำกับที่ Σ VAT ไม่ตรงหัวใบ ผู้ตรวจอ่านแล้วตีเป็นใบไม่สมบูรณ์</para>
///
/// <para><b>สเปกที่ผู้เรียก (<c>PosService.IssueTaxInvoiceAsync</c>) ต้องต่อให้ครบ</b> —
/// ตั้ง <c>doc.BillDiscountAmount = lines.Sum(l =&gt; l.DiscountNet)</c> เพื่อให้ renderer
/// พิมพ์ "รวมก่อนหักท้ายบิล" + "ส่วนลดท้ายบิล (x)" ให้ผู้ซื้อเห็น (เส้นเดียวกับที่
/// <c>DocumentService</c> ใช้กับใบที่คีย์มือ) · ถ้ายังไม่ต่อ ยอดทุกตัวบนใบยัง**ถูกต้อง**
/// แค่ผู้ซื้อไม่เห็นว่าส่วนลดอยู่ตรงไหน</para>
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
    /// <param name="couponAmount"><c>PosOrder.CouponDiscountAmount</c> — ใช้อธิบายที่มาของ
    /// ยอดหักในข้อความ error เมื่อยอดไม่ลงตัว (ยอดรวมอยู่ใน <paramref name="billDiscountAmount"/> แล้ว)</param>
    /// <param name="couponCode">รหัสคูปองบนบิล — ใช้ในข้อความ error เพื่อให้แคชเชียร์ไล่ที่มาได้</param>
    /// <param name="headGrossPayable">ยอดรวมที่ต้องลงตัว (ดู <see cref="GrossPayableForGoods"/>)</param>
    /// <param name="headVat"><c>PosOrder.VatAmount</c> ที่ตรึงไว้ตอนปิดบิล (ตัวเดียวกับใน JE)</param>
    /// <param name="statutoryVatRate">อัตราภาษีขายตามกฎหมายที่บิลนี้ใช้ — เขียนลงทุกบรรทัด
    /// ที่มีฐานภาษี (ห้ามเป็นผลหารที่คำนวณได้) · ค่าตั้งต้น = อัตราตามกฎหมายไทยปัจจุบัน
    /// ⇒ ผู้เรียกเดิมพฤติกรรมไม่เปลี่ยน · บิลที่ <c>headVat = 0</c> ได้ 0 เสมอ</param>
    public static IReadOnlyList<PosInvoiceLine> Build(
        IEnumerable<PosInvoiceSourceLine> items,
        decimal billDiscountAmount,
        decimal couponAmount,
        string? couponCode,
        decimal serviceChargeAmount,
        decimal roundingAmount,
        decimal headGrossPayable,
        decimal headVat,
        decimal statutoryVatRate = PartnerVatRate.StatutoryRate)
    {
        const MidpointRounding R = MidpointRounding.AwayFromZero;

        // ── 1. รวม "บรรทัดที่เป็นบวก" เป็นกองเดียว · ทุกอย่างที่ลดยอดไปอยู่ใน deduction ──
        var pool = new List<(PosInvoiceSourceLine Src, decimal Gross, bool VatBearing)>();
        var deduction = billDiscountAmount > 0m ? billDiscountAmount : 0m;

        foreach (var it in items)
        {
            // บิลที่มี "รายการติดลบ" มาแต่ต้น (ของแถม/ปรับยอดที่หน้าร้านคีย์เป็นราคาติดลบ)
            // ต้องกลายเป็นยอดหักระดับบิล ไม่ใช่บรรทัดติดลบบนใบกำกับ (DocumentLineKind)
            if (it.LineGross < 0m) { deduction += -it.LineGross; continue; }
            pool.Add((it, it.LineGross, true));
        }

        if (serviceChargeAmount > 0m)
            pool.Add((new PosInvoiceSourceLine(ServiceChargeLabel, null, AdjustmentUnit, 1m, serviceChargeAmount),
                serviceChargeAmount, true));
        else if (serviceChargeAmount < 0m) deduction += -serviceChargeAmount;

        // ปัดเศษ**ขึ้น** = บรรทัดที่ไม่มี VAT · ปัดเศษ**ลง** = ยอดหักระดับบิล
        if (roundingAmount > 0m)
            pool.Add((new PosInvoiceSourceLine(RoundingLabel, null, AdjustmentUnit, 1m, roundingAmount),
                roundingAmount, false));
        else if (roundingAmount < 0m) deduction += -roundingAmount;

        // ── 2. เฉลี่ยยอดหักลงบรรทัดที่มีฐานภาษี (ปัดเศษขึ้นไม่ถูกหัก — ไม่ใช่ค่าสินค้า) ──
        var deductionBase = new decimal[pool.Count];
        for (var i = 0; i < pool.Count; i++)
            deductionBase[i] = pool[i].VatBearing ? pool[i].Gross : 0m;
        var alloc = DocumentLineKind.AllocateDeduction(deductionBase, deduction);

        var finalGross = new decimal[pool.Count];
        for (var i = 0; i < pool.Count; i++) finalGross[i] = pool[i].Gross - alloc[i];

        // ── 3. ยอดต้องลงตัวกับเงินที่ลูกค้าจ่าย — ไม่ลงตัว = ห้ามออกใบ ──
        // (ตรวจ**หลัง**เฉลี่ย เพราะยอดหักที่เกินฐานจะถูก clamp — ต้องจับตรงนี้ ไม่ใช่เดาก่อน)
        decimal sum = 0m;
        for (var i = 0; i < pool.Count; i++) sum += finalGross[i];
        if (Math.Abs(sum - headGrossPayable) > 0.005m)
        {
            var origin = couponAmount > 0m
                ? $" (ยอดหักรวม {deduction:N2} — มีคูปอง {couponAmount:N2}"
                    + (string.IsNullOrWhiteSpace(couponCode) ? "" : $" รหัส {couponCode.Trim()}") + ")"
                : deduction > 0m ? $" (ยอดหักรวม {deduction:N2})" : "";
            throw new BusinessRuleException(
                $"ยอดรวมรายบรรทัด ({sum:N2}) ไม่ตรงกับยอดที่ลูกค้าจ่าย ({headGrossPayable:N2}){origin} — "
                + "ออกใบกำกับภาษีไม่ได้ (ใบที่ยอดไม่ตรงคือเอกสารที่ผู้ซื้อเคลมไม่ได้และผู้ขายรายงานผิด) "
                + "· ตรวจรายการบนบิลแล้วลองใหม่",
                "RD-86/4-POS-LINE-SUM");
        }

        // ── 4. แบ่ง VAT หัวใบลงบรรทัดที่มีฐานภาษี · เศษไปที่บรรทัดใหญ่สุด ──
        decimal vatBase = 0m;
        for (var i = 0; i < pool.Count; i++) if (pool[i].VatBearing) vatBase += finalGross[i];
        var vatByIndex = new decimal[pool.Count];
        if (headVat != 0m && vatBase != 0m)
        {
            var residualIndex = -1;
            decimal biggest = -1m;
            for (var i = 0; i < pool.Count; i++)
            {
                if (!pool[i].VatBearing) continue;
                var abs = Math.Abs(finalGross[i]);
                if (abs > biggest) { biggest = abs; residualIndex = i; }
            }
            decimal allocated = 0m;
            for (var i = 0; i < pool.Count; i++)
            {
                if (!pool[i].VatBearing || i == residualIndex) continue;
                var v = Math.Round(headVat * finalGross[i] / vatBase, 2, R);
                vatByIndex[i] = v;
                allocated += v;
            }
            if (residualIndex >= 0) vatByIndex[residualIndex] = headVat - allocated;
        }

        // อัตราที่เขียนลงบรรทัด = อัตรา**ตามกฎหมาย** ไม่ใช่ vat/net ที่คำนวณได้
        var lineRate = headVat == 0m ? 0m : statutoryVatRate;

        // ── 5. แปลงเป็นบรรทัดเอกสาร (ยอดไม่รวม VAT + VAT ของบรรทัด) ──
        var result = new List<PosInvoiceLine>(pool.Count);
        for (var i = 0; i < pool.Count; i++)
        {
            var (src, _, vatBearing) = pool[i];
            var vat = vatByIndex[i];
            var net = finalGross[i] - vat;
            var qty = src.Quantity > 0m ? src.Quantity : 1m;
            // ส่วนแบ่งยอดหักของบรรทัดนี้เป็นยอดก่อน VAT — ใช้แสดง "ส่วนลดท้ายบิล" เท่านั้น
            var discountNet = alloc[i] <= 0m
                ? 0m
                : (vatBearing && lineRate > 0m
                    ? alloc[i] - Math.Round(alloc[i] * lineRate / (100m + lineRate), 2, R)
                    : alloc[i]);
            result.Add(new PosInvoiceLine(
                Description: string.IsNullOrWhiteSpace(src.Description) ? "รายการ" : src.Description,
                ProductCode: src.ProductCode,
                Unit: string.IsNullOrWhiteSpace(src.Unit) ? "ชิ้น" : src.Unit,
                Quantity: qty,
                UnitPriceNet: Math.Round(net / qty, 4, R),
                AmountNet: net,
                VatAmount: vat,
                VatRate: vatBearing ? lineRate : 0m,
                DiscountNet: discountNet));
        }
        return result;
    }
}

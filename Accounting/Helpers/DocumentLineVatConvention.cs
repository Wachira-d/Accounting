namespace Accounting.Helpers;

/// <summary>
/// ด่านตรวจ/ซ่อม convention ของ <c>DocumentLine.Amount</c> — **ต้องเป็นยอด
/// "ก่อน VAT" (net) เสมอ** ไม่ว่าราคาที่ผู้ใช้กรอกจะรวม VAT มาแล้วหรือไม่
/// (<c>DocumentService.ComputeLineAmounts</c> คืน <c>NetAmount</c> ลงช่องนี้)
///
/// ที่มา — เคสจริง IKEA ผ่าน OCR: ใบราคารวม VAT ยอดสุทธิ 1,396.00
/// (ก่อน VAT 1,304.68 + VAT 91.32) แต่ OCR รุ่นก่อน 2026-08-14 เก็บ
/// <c>Line.Amount</c> เป็นยอด**รวม VAT** ⇒ ตอนอนุมัติ JE ฝั่งซื้อลง
/// <code>
///   Dr ค่าใช้จ่าย  Σ Line.Amount = 1,396.00   (รวม VAT อยู่แล้ว)
///   Dr ภาษีซื้อ                     91.32   (บวก VAT ซ้ำอีกรอบ)
///   Cr เจ้าหนี้                   1,396.00
/// </code>
/// ⇒ เดบิตเกินเครดิต **เท่ายอด VAT พอดี** = "การบันทึกบัญชีอัตโนมัติไม่สมดุล:
/// เดบิต 1,487.32 ≠ เครดิต 1,396.00" — ผู้ใช้อ่านแล้วทำอะไรต่อไม่ได้เลย
///
/// ต้นทาง (<c>OcrService</c>) แก้แล้วใน 9a0a882 แต่**แถวที่สร้างไปก่อนหน้านั้น
/// ยังค้างของเสียอยู่** และไม่มีใครซ่อมให้ — คลาสนี้คือกลไกที่ทำให้เคสนี้
/// "ตายที่หน้างาน" ไม่ได้อีก: ตรวจเจอ = ซ่อมเงียบ ๆ ตอนอนุมัติ + backfill
/// ตอน migrate + ถ้าไม่เข้าเงื่อนไขซ่อมก็ยังแปลข้อความ error ให้ทำต่อได้
///
/// ⚠️ ซ่อมเฉพาะ "ส่วนแบ่งภายในบรรทัด" — <c>SubTotal</c> / <c>VatAmount</c> /
/// <c>TotalAmount</c> ของเอกสาร **ไม่ขยับแม้แต่สตางค์เดียว** (ยอดบนใบเท่าเดิม)
/// </summary>
public static class DocumentLineVatConvention
{
    /// <summary>
    /// แยก (ฐานก่อน VAT, VAT) ของหนึ่งบรรทัด — <b>ตัวคำนวณกลางของ integration</b>
    ///
    /// ═══ ที่มา (ฟีเจอร์ซ้อนที่ทำให้ JE หายทั้งใบ) ═══
    /// ธง <c>IncludeVat</c> ถูก implement <b>3 แบบ</b> ในไฟล์เดียวกัน:
    /// <list type="bullet">
    /// <item>ฝั่งขาย + มี <c>line.VatAmount</c> → หัก VAT ออกจาก net
    ///   แต่ <c>totalAmount = subTotal</c> ⇒ <b>ยอดรวมขาด VAT</b></item>
    /// <item>ฝั่งขาย + ไม่มี <c>line.VatAmount</c> → คำนวณ VAT แต่<b>ไม่หัก</b>
    ///   net ⇒ ยอดรวมถูก แต่ <b>SubTotal เกินจริง</b> (ฐานภาษีขายใน ภ.พ.30
    ///   และ e-Tax เกินเท่ายอด VAT)</item>
    /// <item>ฝั่งซื้อ (<c>BuildDocumentLinesAsync</c>) → <b>ไม่รู้จักธงนี้เลย</b>
    ///   คิด exclusive เสมอ แล้วผู้เรียกใช้ <c>totalAmount = subTotal − wht</c>
    ///   ⇒ VAT ไม่เคยถูกบวก ⇒ JE ที่ประกอบขึ้นมี Dr เกิน Cr เท่ายอด VAT พอดี
    ///   ⇒ ด่าน Dr==Cr ตีตกแล้ว <c>return null</c> เงียบ ๆ = <b>เอกสารขึ้นสถานะ
    ///   "อนุมัติแล้ว" โดยไม่มีรายการบัญชีเลย</b> ทั้งที่ ภ.พ.30 ยังนับใบนี้</item>
    /// </list>
    /// (<c>IncludeVat = true</c> เป็น <b>ค่า default</b> ของ request DTO ทั้ง 4 ตัว
    /// ⇒ นี่คือเส้นทางปกติ ไม่ใช่ edge case)
    ///
    /// ═══ กติกาเดียวหลังแก้ ═══
    /// <c>SubTotal</c> = ฐานก่อน VAT <b>เสมอ</b> · <c>TotalAmount = SubTotal +
    /// VatAmount − WHT</c> — ตรงกับ <c>DocumentService</c> ที่เป็นเจ้าของกฎตัวจริง
    /// </summary>
    /// <param name="amount">ยอดบรรทัดหลังหักส่วนลด — รวม VAT แล้วเมื่อ <paramref name="includeVat"/></param>
    public static (decimal Net, decimal Vat) SplitLine(
        decimal amount, decimal vatRate, decimal? explicitVat, bool includeVat)
    {
        if (explicitVat.HasValue)
        {
            var v = Math.Round(explicitVat.Value, 2, MidpointRounding.AwayFromZero);
            var n = includeVat ? amount - v : amount;
            return (Math.Round(n, 2, MidpointRounding.AwayFromZero), v);
        }
        if (includeVat && vatRate > 0m)
        {
            var net = Math.Round(amount * 100m / (100m + vatRate), 2, MidpointRounding.AwayFromZero);
            return (net, Math.Round(amount - net, 2, MidpointRounding.AwayFromZero));
        }
        var baseNet = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        return (baseNet, Math.Round(baseNet * vatRate / 100m, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>ผลต่างที่ยอมรับได้ตอนเทียบยอด (สตางค์จากการปัดเศษรายบรรทัด)</summary>
    public const decimal Tolerance = 0.02m;

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// จริง = บรรทัดในเอกสารนี้เก็บยอด **รวม VAT** ลง <c>Amount</c> (ผิด convention)
    /// จึงซ่อมได้ด้วย <see cref="RepairedAmount"/> รายบรรทัด
    ///
    /// เงื่อนไขแคบโดยตั้งใจ — ต้องเข้าครบทุกข้อจึงจะแตะข้อมูลผู้ใช้:
    /// <list type="number">
    /// <item>เอกสารตั้งค่า "ราคารวม VAT" ไว้ (เคสเดียวที่ OCR เก็บผิด)</item>
    /// <item>มี VAT จริงและมากกว่า tolerance (ไม่งั้นแยก net/gross ไม่ออก)</item>
    /// <item>Σ Line.Amount ≈ SubTotal + VatAmount → นี่คือลายเซ็นของ "เก็บ gross"</item>
    /// <item>Σ Line.VatAmount ≈ VatAmount ของหัวเอกสาร → หัก VAT รายบรรทัดแล้ว
    ///       ผลรวมจะลงตัวกับ SubTotal พอดี ไม่ต้องเดาการปัดเศษใหม่</item>
    /// </list>
    /// ถ้า Σ Line.Amount ≈ SubTotal อยู่แล้ว (net ถูกต้อง) จะคืน false เสมอ
    /// ⇒ เรียกซ้ำกี่ครั้งก็ปลอดภัย (idempotent)
    /// </summary>
    public static bool LinesStoredGross(
        bool pricesIncludeVat, decimal subTotal, decimal vatAmount,
        decimal lineAmountSum, decimal lineVatSum)
    {
        if (!pricesIncludeVat) return false;
        if (vatAmount <= Tolerance) return false;

        var sumAmt = R2(lineAmountSum);
        // ซ่อมแล้ว/ถูกอยู่แล้ว → ไม่แตะ (ด่านนี้ต้องมาก่อน กัน tolerance ทับกัน
        // ตอน VAT เล็กมากจน net กับ gross ห่างกันไม่ถึง 0.02)
        if (Math.Abs(sumAmt - R2(subTotal)) <= Tolerance) return false;

        if (Math.Abs(sumAmt - R2(subTotal + vatAmount)) > Tolerance) return false;
        return Math.Abs(R2(lineVatSum) - R2(vatAmount)) <= Tolerance;
    }

    /// <summary>ยอด net ของบรรทัดหลังซ่อม = ยอดรวม VAT − VAT ของบรรทัดนั้น
    /// (สูตรเดียวกับที่ <c>OcrService</c> ใช้ตอนสร้างเอกสาร — ห้ามคำนวณใหม่
    /// จากอัตรา ไม่งั้นจะได้เลขที่ต่างจากที่พิมพ์บนใบ 1 สตางค์)</summary>
    public static decimal RepairedAmount(decimal lineAmount, decimal lineVatAmount)
        => R2(lineAmount - lineVatAmount);

    /// <summary>ข้อความอธิบายสาเหตุ เมื่อ JE ไม่สมดุลด้วย "ส่วนต่าง = ยอด VAT พอดี"
    /// — คืน null เมื่อไม่ใช่อาการนี้ (ให้ผู้เรียกใช้ข้อความเดิม)</summary>
    public static string? ExplainImbalance(decimal totalDebit, decimal totalCredit, decimal docVatAmount)
    {
        var diff = R2(totalDebit - totalCredit);
        if (docVatAmount <= Tolerance || Math.Abs(diff - R2(docVatAmount)) > Tolerance)
            return null;
        return $"การบันทึกบัญชีอัตโนมัติไม่สมดุล: เดบิต {totalDebit:N2} ≠ เครดิต {totalCredit:N2} "
             + $"— ส่วนต่าง {diff:N2} เท่ากับยอด VAT ของใบนี้พอดี แปลว่ายอดรายบรรทัดถูกเก็บเป็น "
             + "\"ราคารวม VAT\" ทั้งที่ต้องเก็บเป็นยอดก่อน VAT "
             + "→ เปิด \"แก้ไข\" เอกสารแล้วกดบันทึกหนึ่งครั้ง ระบบจะคำนวณยอดรายบรรทัดใหม่ให้ "
             + "(ยอดรวมบนใบไม่เปลี่ยน) แล้วจึงกดอนุมัติอีกครั้ง";
    }
}

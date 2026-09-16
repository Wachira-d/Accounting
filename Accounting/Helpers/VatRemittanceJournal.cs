namespace Accounting.Helpers;

/// <summary>
/// ประกอบรายการบัญชีตอน "นำส่ง ภ.พ.30" — **ตรรกะล้วน** แยกออกจากการหาผังบัญชี
/// เพื่อให้เทสต์ล็อก invariant ได้ (Dr ต้องเท่ากับ Cr เสมอ ทุกชุดตัวเลข)
///
/// <para><b>ที่มา</b> (2026-09-16): เดิม <c>RemitAsync</c> ประกอบ JE ว่า
/// <c>Dr 21911 = ภาษีขาย / Cr 11610 = ภาษีซื้อ / Cr ธนาคาร = ยอดสุทธิ</c>
/// แต่ยอดสุทธิที่ <c>TaxService.GenerateVatReport</c> คำนวณคือ
/// <c>ภาษีขาย − ภาษีซื้อ − <b>เครดิตภาษีซื้อยกมา §82/3</b></c>
/// ⇒ ทุกงวดที่มีเครดิตยกมา <c>Dr − Cr = เครดิตยกมา</c> ⇒ ตัวสร้าง JE โยน
/// "ยอดเดบิต (X) ไม่เท่ากับยอดเครดิต (Y)" ซึ่งผู้ใช้อ่านไม่ออกและ
/// <b>นำส่งงวดนั้นไม่ได้เลย</b>
/// (จำลองด้วย <c>tools/vat_remittance_je_sim.py</c>: ก่อนแก้ไม่สมดุล 3/5 · หลังแก้ 0/5)</para>
///
/// <para><b>ทำไมเครดิตยกมาต้องไปปิด 11610</b>: งวดก่อนที่ภาษีซื้อมากกว่าภาษีขาย
/// ไม่มีเงินต้องนำส่งจึงไม่เคยมี JE มาปิด ⇒ ยอดนั้นยังค้างอยู่ใน 11610 ถึงงวดนี้.
/// พองวดนี้เอามาหักกับภาษีขาย มันถูกใช้ไปแล้วจริง ⇒ ต้องล้างออกจาก 11610 ด้วย
/// ไม่งั้น 11610 จะบวมขึ้นเรื่อย ๆ ทุกงวดที่เคยมีเครดิตยกมา</para>
///
/// <para><b>ไม่เก็บเครดิตยกมาเป็นฟิลด์</b> — <c>TaxService.cs</c> เก็บเป็น
/// <c>TaxReportLine</c> (TaxAmount ติดลบ) แล้วรวมเข้า <c>NetVat</c> เลย จึงหาย้อน
/// แบบ **เป๊ะ** จากสามค่าที่มีอยู่แล้ว: <c>CF = ภาษีขาย − ภาษีซื้อ − สุทธิ</c>
/// (ไม่ใช่การประมาณ — เป็นนิยามเดียวกับที่ฝั่งรายงานใช้)</para>
/// </summary>
public static class VatRemittanceJournal
{
    /// <summary>ยอดของแต่ละบรรทัดใน JE นำส่ง ภ.พ.30 — ทุกค่าปัดเป็นสตางค์แล้ว</summary>
    /// <param name="ClearOutputVat">Dr 21911 — ล้างภาษีขายค้างจ่ายของงวด</param>
    /// <param name="ClearInputVat">Cr 11610 — ภาษีซื้อของงวด **บวก** เครดิตยกมา</param>
    /// <param name="CarryForwardUsed">ส่วนของ <see cref="ClearInputVat"/> ที่มาจากงวดก่อน (ใช้เขียนคำอธิบายบรรทัด)</param>
    /// <param name="PayFromBank">Cr ธนาคาร — เงินที่จ่ายจริง</param>
    public readonly record struct Plan(
        decimal ClearOutputVat,
        decimal ClearInputVat,
        decimal CarryForwardUsed,
        decimal PayFromBank);

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// คืนแผนบรรทัด JE ที่ <b>Dr = Cr เสมอ</b> · โยน <see cref="BusinessRuleException"/>
    /// เมื่อตัวเลขที่ส่งมาขัดกันเอง — <b>ห้ามเดา/ห้ามเงียบ</b> เพราะปลายทางคือเงินจริง
    /// </summary>
    /// <param name="outputVat">ภาษีขายของงวด (<c>TaxReport.OutputVat</c>)</param>
    /// <param name="inputVat">ภาษีซื้อของงวด (<c>TaxReport.InputVat</c>)</param>
    /// <param name="amountToPay">ยอดที่จะจ่ายจริงงวดนี้ (= <c>NetVat</c> หักที่นำส่งไปแล้ว)</param>
    public static Plan Build(decimal outputVat, decimal inputVat, decimal amountToPay)
    {
        var output = R(outputVat);
        var input = R(inputVat);
        var pay = R(amountToPay);

        if (output < 0 || input < 0)
            throw new BusinessRuleException(
                $"ยอดภาษีขาย/ภาษีซื้อของงวดติดลบ (ขาย {output:N2} · ซื้อ {input:N2}) — "
                + "สร้างรายงาน ภ.พ.30 ของงวดนี้ใหม่ก่อนนำส่ง",
                "VAT-REMIT-NEGATIVE");

        if (pay <= 0)
            throw new BusinessRuleException(
                "งวดนี้ไม่มียอดต้องชำระ (ภาษีซื้อมากกว่าภาษีขาย) — ยังต้องยื่นแบบ "
                + "แต่ไม่ต้องบันทึกการนำส่งเงิน",
                "VAT-REMIT-NO-AMOUNT");

        // เครดิตยกมา §82/3 — นิยามเดียวกับ TaxService.GenerateVatReport
        var carryForward = R(output - input - pay);

        if (carryForward < -0.005m)
            throw new BusinessRuleException(
                $"ยอดที่จะนำส่ง ({pay:N2}) มากกว่าภาษีขายหักภาษีซื้อ ({R(output - input):N2}) — "
                + "ตัวเลขของรายงาน ภ.พ.30 งวดนี้ขัดกันเอง กรุณาสร้างรายงานใหม่ก่อนนำส่ง",
                "VAT-REMIT-MISMATCH");

        if (carryForward < 0) carryForward = 0m;   // เศษปัดระดับสตางค์

        var clearInput = R(input + carryForward);

        // invariant — ห้ามคืนแผนที่ไม่สมดุลออกไปเด็ดขาด (ตาข่ายรับสุดท้าย)
        if (R(output) != R(clearInput + pay))
            throw new BusinessRuleException(
                $"ประกอบรายการบัญชีนำส่ง ภ.พ.30 ไม่สมดุล (Dr {output:N2} ≠ Cr {R(clearInput + pay):N2}) — "
                + "กรุณาแจ้งผู้ดูแลระบบ",
                "VAT-REMIT-UNBALANCED", 500);

        return new Plan(output, clearInput, carryForward, pay);
    }
}

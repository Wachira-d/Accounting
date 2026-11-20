using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>ตัวตัดสินเดียว: "ใบเสร็จของการรับเงินครั้งนี้ถือบทบาทใบกำกับภาษี ณ วันรับเงิน (ม.78/1) หรือไม่ และถือ VAT เท่าใด"</b> — รอบ 275 แพ็ก C
/// (ปิด T3 HIGH-1 · MED-1 · MED-2 · MED-3 · คำตัดสิน 4.1 / 275-C-1)
///
/// <para>═══ ที่มา ═══ เดิมเกณฑ์ "รับครบงวดเดียว" มี 4 สำเนาที่ให้ผลต่างกัน: บันทึกรับชำระ (<c>PaidAmount − เงิน − ภาษีหัก</c> — ไม่หักค่าธรรมเนียม/บรรทัดปรับ
/// แต่หักภาษีหักที่ไม่เคยถูกบวกเข้า PaidAmount) · ออกใบย้อนหลัง (<c>นับจำนวนการชำระ == 1</c> — ไม่เห็นใบลดหนี้) · ย้ายการรับชำระ · ตัวประมาณของงานหลังรับชำระ ⇒
/// ใบแจ้งหนี้บริการที่จ่ายเต็มแต่ธนาคารหักค่าธรรมเนียม 30 บาท ไม่ได้ใบกำกับภาษี (HIGH-1) · ใบเดียวกันถือ VAT หรือไม่ขึ้นกับปุ่มที่กด ·
/// ภาษีหักบังงวดก่อนหน้า (MED-1) · ออกย้อนหลังหลังใบลดหนี้คัดลอก VAT เต็มใบ (MED-2) · รับหลายงวดแล้วไม่มีใบใดเป็นใบกำกับ (MED-3)</para>
///
/// <para>═══ กติกา (คำตัดสิน 4.1) ═══</para>
/// <list type="number">
/// <item>ใบต้นทางต้องเป็น<b>ใบแจ้งหนี้</b> (ไม่ใช่ใบกำกับ) ที่มี VAT และยังไม่ถูกใบกำกับภาษีแทน · VAT ที่ใบเสร็จอื่นของใบนี้ถือไปแล้วครบ ⇒ ไม่ออกใบกำกับซ้ำ</item>
/// <item>ยอดคงค้างไล่ด้วยตัวไล่ตัวเดียว <see cref="SettlementReceiptTotals.Walk"/> — "ยอดที่ครั้งนี้ตัด" = เงิน + ค่าธรรมเนียม + บรรทัดปรับ (ตัวเดียวกับที่บวกเข้า PaidAmount)</item>
/// <item>ใบลดหนี้ก่อนรับ · การตัดยอดที่ไล่ไม่ได้ (เช่น หักมัดจำ) ⇒ <b>ไม่เดา</b> — ไม่ออกใบกำกับอัตโนมัติ ขึ้นคิว "รับเงินแล้วยังไม่มีใบกำกับ" ให้ผู้ทำบัญชีตัดสิน</item>
/// <item>รับครบในงวดเดียว (ไม่มีการตัดยอดใดก่อน และปิดยอด) ⇒ ใบเสร็จคัดลอกบรรทัด/ยอดของใบแจ้งหนี้ (<see cref="Mode.FullCopy"/> — พฤติกรรมเดิม ทั้งงานบริการและขายสินค้า)</item>
/// <item>รับเป็นงวดของ<b>งานบริการ</b> (ภาษีขายพัก 21913 จนรับเงิน — ตัวจำแนกเดียวกับตอนลงบัญชีใบแจ้งหนี้ <c>InvoiceHasTrackedGoodsAsync</c>) ⇒ ใบเสร็จทุกงวดเป็นใบกำกับ
/// ณ วันรับเงิน · VAT ตามส่วน = ถอดจากยอดที่ตัดหนี้รวมภาษีหักของงวดนี้ ด้วยอัตราส่วน VAT/(มูลค่า+VAT) ของใบแจ้งหนี้ (7/107 เมื่อทั้งใบ 7%) ·
/// งวดที่ปิดยอดถือ VAT ที่เหลือทั้งหมด (ผลรวมทุกงวด = VAT ของใบแจ้งหนี้ทุกสตางค์) (<see cref="Mode.Installment"/>)</item>
/// <item>ขายสินค้า (ภาษีขายไม่ได้พัก — จุดความรับผิด ม.78 เกิดตอนส่งมอบ) รับเป็นงวด ⇒ คงเดิม (ใบรับเปล่า) · ไม่รู้ว่าพักหรือไม่ (ผังไม่มี 21913) ⇒ คิวให้คนตัดสิน</item>
/// </list>
/// <para>G6: pure · ผู้เรียก (ล็อกใน <c>tools/required_call_site_check.py</c> กติกา <c>_R275C_</c>): <c>CreatePaymentAsync</c> · <c>IssueReceiptForPaymentAsync</c> ·
/// ย้ายการรับชำระ (<c>DocumentService.ReceiptLink</c>) · คิว/ปุ่มออกใบกำกับเพิ่ม (<c>DocumentService.SettlementTaxInvoice</c>) · ตัวประมาณ <see cref="MayCarryVat"/> ของงานหลังรับชำระ</para>
/// </summary>
public static class SettlementTaxInvoiceRole
{
    public const string RuleCode = "RD-78/1-SETTLEMENT-TAX-INVOICE";
    public const string LegalReference = "ป.รัษฎากร ม.78/1(1) · ม.86 · ม.86/4";

    private const decimal Eps = 0.005m;
    /// <summary>เศษที่ยอมให้ "ยังไม่มีการตัดยอดก่อนหน้า" (ค่าเดิมของสูตรงวดเดียว)</summary>
    private const decimal PriorTolerance = 0.05m;

    public enum Mode
    {
        /// <summary>ใบรับเปล่า (VAT 0) — ไม่ถือบทบาทใบกำกับ</summary>
        None,
        /// <summary>รับครบงวดเดียว — ใบเสร็จคัดลอกบรรทัด/ยอดของใบแจ้งหนี้</summary>
        FullCopy,
        /// <summary>งวดของงานบริการ — ใบเสร็จถือ VAT ตามส่วนของงวดนี้ (บรรทัดเดียว)</summary>
        Installment,
    }

    /// <summary>เหตุของผล (ชื่อ enum ออกไปหน้าจอ · ข้อความจาก <see cref="ReasonText"/>)</summary>
    public enum Why
    {
        NotInvoiceWithVat,
        SupersededByTaxInvoice,
        FullVatAlreadyIssued,
        NothingSettled,
        CreditNoteBeforeReceipt,
        UnexplainedPriorSettlement,
        SingleShotFull,
        Installment,
        VatNotParked,
        /// <summary>ใบเก่า: ภาษีขายทั้งใบย้ายเข้า ภ.พ.30 ไปแล้วตอนรับงวดแรก และยังรับไม่ครบ — ออกใบกำกับรายงวดตอนนี้ = ภาษีขายของเดือนนั้นลดลง (ยื่นแล้ว)</summary>
        WholesaleVatNotFullyPaid,
        /// <summary>มีการรับงวดก่อนหน้าที่ยังไม่มีใบกำกับถือ VAT ของงวดนั้น — งวดนี้ (โดยเฉพาะงวดปิด) จะถือ VAT ผิดส่วน</summary>
        PriorInstallmentWithoutTaxInvoice,
    }

    /// <summary>ข้อเท็จจริงของใบต้นทาง + การรับครั้งนี้ (ผู้เรียกโหลด — ตัวตัดสินไม่รับ DbContext)</summary>
    /// <param name="SourceTotalAmount">ยอดที่ลูกค้าต้องจ่ายตามใบ (สุทธิหลังภาษีหัก ณ ที่จ่าย)</param>
    /// <param name="SupersededByTaxInvoice">มีใบกำกับภาษี (ยังมีผล) อ้างใบแจ้งหนี้นี้แล้ว</param>
    /// <param name="VatParkedUntilPayment">ภาษีขายของใบนี้พักไว้จนรับเงิน (งานบริการ — ลง 21913 ตอนอนุมัติ: ยังพักอยู่ หรือย้ายไปแล้วบางส่วน/ทั้งหมด)</param>
    /// <param name="OtherLiveReceiptVat">VAT ที่ใบเสร็จอื่นของใบนี้ (ยังมีผล · ไม่ใช่ใบของการรับครั้งนี้) ถือไปแล้ว</param>
    /// <param name="Events">รายการตัดยอดอื่นของใบต้นทาง (ไม่รวมครั้งนี้ · ตัวโหลดเดียวกับแถวยอดใบเสร็จ)</param>
    /// <param name="SourcesOutstandingNow">ยอดค้างปัจจุบันของใบต้นทาง (หลังรับครั้งนี้) · null = ไม่รู้ (ไม่ตรวจการตัดยอดที่ไล่ไม่ได้)</param>
    /// <param name="VatReportedWholesale">ใบเก่า (ก่อนรอบ 275): ภาษีขายทั้งใบย้าย 21913 → 21911 ครั้งเดียวไปแล้ว (ไม่มี JE ย้ายตามส่วนของงวด) — ใบแจ้งหนี้เป็นเจ้าของภาษีใน ภ.พ.30</param>
    public sealed record Facts(
        DocumentType SourceType,
        decimal SourceSubTotal,
        decimal SourceVatAmount,
        decimal SourceTotalAmount,
        bool SupersededByTaxInvoice,
        bool VatParkedUntilPayment,
        decimal OtherLiveReceiptVat,
        SettlementReceiptTotals.ThisSettlement This,
        IReadOnlyList<SettlementReceiptTotals.LedgerEvent> Events,
        decimal? SourcesOutstandingNow,
        bool VatReportedWholesale = false);

    /// <param name="SubTotal">มูลค่าก่อน VAT ที่ใบเสร็จถือ (Installment) · FullCopy = ของใบแจ้งหนี้</param>
    /// <param name="VatAmount">VAT ที่ใบเสร็จถือ = ภาษีขายที่ถึงกำหนดจากการรับครั้งนี้ (ใช้ย้าย 21913 → 21911 ตามส่วน)</param>
    /// <param name="WithholdingTaxAmount">ภาษีหัก ณ ที่จ่ายบนใบเสร็จ</param>
    /// <param name="TotalAmount">ยอดสุทธิบนใบเสร็จ (= มูลค่า + VAT − ภาษีหัก)</param>
    /// <param name="NeedsHumanDecision">การรับครั้งนี้ควรมีใบกำกับแต่ระบบไม่เดา (ใบลดหนี้ก่อนรับ · การตัดยอดที่ไล่ไม่ได้ · ไม่รู้ว่าภาษีพักหรือไม่) ⇒ คิว</param>
    public sealed record Decision(Mode Mode, Why Reason, decimal SubTotal, decimal VatAmount, decimal WithholdingTaxAmount, decimal TotalAmount,
        bool NeedsHumanDecision)
    {
        public bool CarriesRole => Mode != Mode.None;
    }

    private static Decision NotRole(Why why, bool human = false) => new(Mode.None, why, 0m, 0m, 0m, 0m, human);

    private static decimal R2(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    public static Decision Decide(Facts f)
    {
        if (f.SourceType != DocumentType.Invoice || f.SourceVatAmount <= Eps) return NotRole(Why.NotInvoiceWithVat);
        if (f.SupersededByTaxInvoice) return NotRole(Why.SupersededByTaxInvoice);
        var remainingVat = f.SourceVatAmount - Math.Max(0m, f.OtherLiveReceiptVat);
        if (remainingVat <= Eps) return NotRole(Why.FullVatAlreadyIssued);
        var me = f.This;
        var grossThis = me.Settled + me.WithholdingTaxAmount;
        if (grossThis <= Eps) return NotRole(Why.NothingSettled);

        var events = f.Events ?? Array.Empty<SettlementReceiptTotals.LedgerEvent>();
        var w = SettlementReceiptTotals.Walk(f.SourceTotalAmount, events, me, f.SourcesOutstandingNow);
        // MED-2: ใบลดหนี้ก่อนรับ — ใบกำกับ ณ วันรับเงินต้องสะท้อนยอดหลังลดหนี้ ขณะที่ใบลดหนี้ก็ถูกนับใน ภ.พ.30 แยก ⇒ ระบบไม่เดา (ผู้ทำบัญชีตัดสิน)
        if (w.PriorCreditNotes.Count > 0) return NotRole(Why.CreditNoteBeforeReceipt, human: true);
        // การตัดยอดที่ตัวไล่ไม่รู้จัก (หักมัดจำ ฯลฯ): ยอดที่ลดจริง − ยอดที่ไล่ได้ (ทุกรายการ + ครั้งนี้) > เศษ ⇒ ไม่เดา
        if (f.SourcesOutstandingNow is decimal now)
        {
            var reduced = f.SourceTotalAmount - now;
            var known = events.Sum(e => e.Amount) + me.Settled;
            if (reduced - known > PriorTolerance) return NotRole(Why.UnexplainedPriorSettlement, human: true);
        }

        var closes = w.BalanceAfterRaw <= Eps;
        if (NothingBefore(f, w) && f.OtherLiveReceiptVat <= Eps && closes)
            return new Decision(Mode.FullCopy, Why.SingleShotFull, f.SourceSubTotal, f.SourceVatAmount, 0m, f.SourceTotalAmount, false);

        // งวดของการขายสินค้า (ภาษีขายลง 21911 ตั้งแต่ส่งมอบ) ⇒ คงเดิม · ผู้เรียกที่ไม่รู้ว่าภาษีพัก (ผังไม่มี 21913) ส่ง false ⇒ คิวจาก caller
        if (!f.VatParkedUntilPayment) return NotRole(Why.VatNotParked);

        var ratio = f.SourceSubTotal + f.SourceVatAmount <= Eps ? 0m : f.SourceVatAmount / (f.SourceSubTotal + f.SourceVatAmount);
        // ใบเก่าที่ภาษีทั้งใบรายงานแล้วตอนรับงวดแรก: ออกใบกำกับรายงวดได้เมื่อรับครบทุกงวดแล้วเท่านั้น (ออกพร้อมกันทุกงวด — ภ.พ.30 ของเดือนนั้นไม่เปลี่ยน)
        if (f.VatReportedWholesale && (f.SourcesOutstandingNow ?? 0m) > Eps) return NotRole(Why.WholesaleVatNotFullyPaid, human: true);
        // งวดก่อนหน้าไม่มีใบกำกับถือ VAT ของมัน (ประมาณขั้นต่ำ: ภาษีหักของงวดก่อน = 0) ⇒ ไม่เดาส่วนของงวดนี้
        if (w.PriorSettled > PriorTolerance && f.OtherLiveReceiptVat < R2(w.PriorSettled * ratio) - 1.00m)
            return NotRole(Why.PriorInstallmentWithoutTaxInvoice, human: true);
        var vat = closes ? remainingVat : Math.Min(remainingVat, R2(grossThis * ratio));
        vat = R2(Math.Max(0m, vat));
        var sub = R2(grossThis - vat);
        return new Decision(Mode.Installment, Why.Installment, sub, vat, R2(me.WithholdingTaxAmount), R2(sub + vat - me.WithholdingTaxAmount), false);
    }

    private static bool NothingBefore(Facts f, SettlementReceiptTotals.LedgerWalk w)
        => w.PriorSettled <= PriorTolerance && w.BalanceBefore >= f.SourceTotalAmount - PriorTolerance;

    /// <summary>"รับครบในงวดเดียว" ของการรับครั้งนี้ (ไม่มีการตัดยอดใดก่อน — การรับ · ใบลดหนี้ · ช่องทางอื่นที่ยอดค้างสะท้อน — และครั้งนี้ปิดยอด) — ตัวเดียวของ
    /// ใบเสร็จถือ VAT (<see cref="Decide"/>) และใบกำกับรวม "ใบแจ้งหนี้/ใบกำกับภาษี" ที่ยกหัวเป็นใบเสร็จในตัว (<c>CreatePaymentAsync</c> เคส A) · ไม่ดู VAT/ชนิดใบ</summary>
    public static bool IsSingleShotFull(Facts f)
    {
        var w = SettlementReceiptTotals.Walk(f.SourceTotalAmount, f.Events, f.This, f.SourcesOutstandingNow);
        return w.PriorCreditNotes.Count == 0 && NothingBefore(f, w) && w.BalanceAfterRaw <= Eps;
    }

    /// <summary>ใบเสร็จที่ออก<b>แทน</b>ใบเสร็จเดิมของการรับเดียวกัน (ยกเลิกและออกใบต้นทางแทน — <c>DocumentService.Reissue</c>) ถือบทบาทเท่าใบเดิม:
    /// ใบเดิมไม่ใช่ใบกำกับ ⇒ None · ถือ VAT เท่าใบต้นทาง ⇒ FullCopy (คัดลอกใบใหม่) · ถือ VAT บางส่วน (ใบกำกับรายงวด) ⇒ Installment ยอดเดิมทุกสตางค์ (ไม่คิดใหม่)</summary>
    public static Decision CarryIssued(bool issuedWasTaxInvoice, decimal issuedSubTotal, decimal issuedVat, decimal issuedWht, decimal issuedTotal, decimal sourceVat)
    {
        if (!issuedWasTaxInvoice || issuedVat <= Eps) return NotRole(Why.NothingSettled);
        if (Math.Abs(issuedVat - sourceVat) < Eps)
            return new Decision(Mode.FullCopy, Why.SingleShotFull, issuedSubTotal, issuedVat, issuedWht, issuedTotal, false);
        return new Decision(Mode.Installment, Why.Installment, issuedSubTotal, issuedVat, issuedWht, issuedTotal, false);
    }

    /// <summary>ตัวประมาณแบบระวังของผู้ที่ยังไม่มีใบเสร็จในมือ (งานหลังรับชำระ — เลือกช่องทาง e-Tax by Email ก่อน): การรับเงินของใบแจ้งหนี้ที่มี VAT
    /// <b>อาจ</b>ได้ใบกำกับ ณ วันรับเงิน (รับครบหรือรับเป็นงวดของงานบริการ) — ไม่ใช่สำเนาของเกณฑ์งวดเดียว</summary>
    public static bool MayCarryVat(DocumentType sourceType, decimal sourceVatAmount)
        => sourceType == DocumentType.Invoice && sourceVatAmount > Eps;

    /// <summary>ข้อความไทยของเหตุ (โชว์บนคิว/คำตอบ API) · null = ไม่มีอะไรต้องอธิบาย</summary>
    public static string? ReasonText(Why why) => why switch
    {
        Why.CreditNoteBeforeReceipt => "มีใบลดหนี้ลดยอดใบแจ้งหนี้ก่อนรับเงินครั้งนี้ — ระบบไม่ออกใบกำกับภาษี ณ วันรับเงินเอง (ยอดบนใบกำกับต้องสะท้อนยอดหลังลดหนี้) · "
            + "ผู้ทำบัญชีตัดสิน: ออกใบกำกับภาษีจากใบแจ้งหนี้ตามยอดที่ถูก หรือบันทึกว่าไม่ต้องออก",
        Why.UnexplainedPriorSettlement => "ยอดค้างของใบแจ้งหนี้ลดลงด้วยรายการที่ระบบไล่ลำดับไม่ได้ (เช่น หักมัดจำ) — ระบบไม่เดายอด VAT ของการรับครั้งนี้ · ผู้ทำบัญชีตัดสิน",
        Why.VatNotParked => "ภาษีขายของใบแจ้งหนี้นี้ไม่ได้พักไว้จนรับเงิน (ขายสินค้า หรือผังบัญชีไม่มี 21913) — ระบบไม่ออกใบกำกับรายงวดเอง · ผู้ทำบัญชีตัดสิน",
        Why.WholesaleVatNotFullyPaid => "ใบแจ้งหนี้นี้ (ก่อนรอบ 275) รายงานภาษีขายทั้งใบไปแล้วตอนรับเงินงวดแรก — ออกใบกำกับรายงวดตอนนี้จะลดภาษีขายของเดือนนั้นที่ยื่นแล้ว · "
            + "ระบบออกใบกำกับให้ทุกงวดพร้อมกันเมื่อรับครบ หรือผู้ทำบัญชีตัดสิน (เช่น ออกใบกำกับภาษีเต็มใบอ้างใบแจ้งหนี้)",
        Why.PriorInstallmentWithoutTaxInvoice => "การรับงวดก่อนหน้าของใบแจ้งหนี้นี้ยังไม่มีใบกำกับภาษี ณ วันรับเงิน — ออกใบกำกับของงวดก่อนให้ครบก่อน (คิว \"รับเงินแล้วยังไม่มีใบกำกับ\") "
            + "ระบบจึงจะคิดส่วน VAT ของงวดนี้ได้ถูก",
        Why.FullVatAlreadyIssued => "ใบเสร็จที่ทำหน้าที่ใบกำกับภาษีของใบแจ้งหนี้นี้ถือ VAT ครบแล้ว",
        Why.SupersededByTaxInvoice => "ออกใบกำกับภาษีอ้างใบแจ้งหนี้นี้แล้ว",
        _ => null,
    };

    /// <summary>หมายเหตุภายในของใบเสร็จรายงวด (ไม่พิมพ์ — ผู้ทำบัญชีตรวจที่มาของตัวเลขได้)</summary>
    public static string InstallmentAuditNote(Decision d, decimal sourceVat, string sourceNumber)
    {
        var inv = CultureInfo.InvariantCulture;
        return $"[{RuleCode}] ใบกำกับภาษี ณ วันรับเงิน (งวด · {LegalReference}) ของใบแจ้งหนี้ {sourceNumber}: มูลค่า {d.SubTotal.ToString("N2", inv)} · "
            + $"VAT {d.VatAmount.ToString("N2", inv)} (จาก VAT ทั้งใบ {sourceVat.ToString("N2", inv)} ตามส่วนของยอดที่ตัดหนี้รวมภาษีหักงวดนี้ · คำตัดสิน 275-C-1)";
    }
}

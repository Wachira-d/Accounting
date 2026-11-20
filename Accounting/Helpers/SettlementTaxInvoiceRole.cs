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
/// <para>คอมมิตหลัก 96bb5262 · ชุดแก้ฝ่ายค้าน 0e43cd18 (DOCUMENT_FLOW §3.4b-275) · G6: pure · ผู้เรียก (ล็อกใน <c>tools/required_call_site_check.py</c> กติกา <c>_R275C_</c>): <c>CreatePaymentAsync</c> · <c>IssueReceiptForPaymentAsync</c> ·
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
        SingleShotFull,
        Installment,
        VatNotParked,
    }

    /// <summary>บรรทัดตามอัตรา VAT (ใบผสม 7% + 0% + ยกเว้น — ฝ่ายค้าน 275-C MED-2): <paramref name="VatRate"/> = อัตราของบรรทัด (−1 = ยกเว้น)</summary>
    public sealed record RateLine(decimal VatRate, decimal Base, decimal Vat);

    /// <summary>ข้อเท็จจริงของใบต้นทาง + การรับครั้งนี้ (ผู้เรียกโหลด — ตัวตัดสินไม่รับ DbContext)</summary>
    /// <param name="SourceTotalAmount">ยอดที่ลูกค้าต้องจ่ายตามใบ (สุทธิหลังภาษีหัก ณ ที่จ่าย)</param>
    /// <param name="SupersededByTaxInvoice">มีใบกำกับภาษี (ยังมีผล) อ้างใบแจ้งหนี้นี้แล้ว</param>
    /// <param name="VatParkedUntilPayment">ภาษีขายของใบนี้พักไว้จนรับเงิน (งานบริการ — ลง 21913 ตอนอนุมัติ: ยังพักอยู่ หรือย้ายไปแล้วบางส่วน/ทั้งหมด)</param>
    /// <param name="OtherLiveReceiptVat">VAT ที่ใบเสร็จอื่นของใบนี้ (ยังมีผล · ไม่ใช่ใบของการรับครั้งนี้) ถือไปแล้ว</param>
    /// <param name="Events">รายการตัดยอดอื่นของใบต้นทาง (ไม่รวมครั้งนี้ · ตัวโหลดเดียวกับแถวยอดใบเสร็จ)</param>
    /// <param name="SourcesOutstandingNow">ยอดค้างปัจจุบันของใบต้นทาง (หลังรับครั้งนี้) · null = ไม่รู้ (ไม่ตรวจการตัดยอดที่ไล่ไม่ได้)</param>
    /// <param name="UndueVatRemaining">ภาษีขายของใบ (+ ใบลดหนี้/เพิ่มหนี้ลูก) ที่ยังพัก 21913 <b>ก่อน</b>ย้ายของการรับครั้งนี้ — งวดปิดถือเท่านี้ (ปิดยอดแล้ว 21913 ของใบ = 0 เสมอ)</param>
    /// <param name="TaggedVatThisPayment">VAT ที่ JE ย้ายภาษีตามส่วนของการรับครั้งนี้ย้ายไปแล้ว (ออกใบกำกับทีหลัง — ใบถือเท่าที่ย้ายจริง)</param>
    /// <param name="RateGroups">ยอดของใบแจ้งหนี้แยกตามอัตรา VAT (มูลค่า · VAT) — ใบกำกับรายงวดแยกบรรทัดตามอัตราตามสัดส่วน</param>
    /// <param name="SourceWithholdingTaxAmount">ภาษีหัก ณ ที่จ่ายทั้งใบ — การรับที่ไม่ได้บันทึกภาษีหักของงวด ⇒ ถอดยอดรวมภาษีหักด้วยสัดส่วนของใบ</param>
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
        bool VatReportedWholesale = false,
        decimal UndueVatRemaining = 0m,
        decimal TaggedVatThisPayment = 0m,
        IReadOnlyList<RateLine>? RateGroups = null,
        decimal SourceWithholdingTaxAmount = 0m);

    /// <param name="SubTotal">มูลค่าก่อน VAT ที่ใบเสร็จถือ (Installment) · FullCopy = ของใบแจ้งหนี้</param>
    /// <param name="VatAmount">VAT ที่ใบเสร็จถือ = ภาษีขายที่ถึงกำหนดจากการรับครั้งนี้ (ใช้ย้าย 21913 → 21911 ตามส่วน)</param>
    /// <param name="WithholdingTaxAmount">ภาษีหัก ณ ที่จ่ายบนใบเสร็จ</param>
    /// <param name="TotalAmount">ยอดสุทธิบนใบเสร็จ (= มูลค่า + VAT − ภาษีหัก)</param>
    /// <param name="NeedsHumanDecision">การรับครั้งนี้ควรมีใบกำกับแต่ระบบไม่เดา (ใบลดหนี้ก่อนรับ · การตัดยอดที่ไล่ไม่ได้ · ไม่รู้ว่าภาษีพักหรือไม่) ⇒ คิว</param>
    /// <param name="ClosesSource">การรับครั้งนี้ปิดยอดใบ (ข้อความบรรทัดไม่พิมพ์ "(บางส่วน)")</param>
    /// <param name="VatReportedOnSource">ใบเก่าที่ภาษีขายทั้งใบรายงานแล้วที่ใบแจ้งหนี้ — ใบกำกับนี้เป็นหลักฐานกระดาษ ไม่เป็นเจ้าของแถว ภ.พ.30/§87 (คำตัดสิน 275-C-10)</param>
    /// <param name="Lines">บรรทัดตามอัตรา (Installment) — มูลค่า × อัตรา = VAT ต่อบรรทัด</param>
    public sealed record Decision(Mode Mode, Why Reason, decimal SubTotal, decimal VatAmount, decimal WithholdingTaxAmount, decimal TotalAmount,
        bool NeedsHumanDecision, bool ClosesSource = false, bool VatReportedOnSource = false, IReadOnlyList<RateLine>? Lines = null)
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
        if (remainingVat <= Eps && f.TaggedVatThisPayment <= Eps) return NotRole(Why.FullVatAlreadyIssued);
        var me = f.This;
        var gross = f.SourceSubTotal + f.SourceVatAmount;
        // ยอดที่ตัดหนี้รวมภาษีหัก: บันทึกภาษีหักของงวด ⇒ เงิน + ค่าธรรมเนียม + บรรทัดปรับ + ภาษีหัก · ไม่ได้บันทึกแต่ใบมีภาษีหัก ⇒ ถอดด้วยสัดส่วนยอดรวม/ยอดสุทธิของใบ
        var grossThis = me.WithholdingTaxAmount <= Eps && f.SourceWithholdingTaxAmount > Eps && f.SourceTotalAmount > Eps
            ? R2(me.Settled * gross / f.SourceTotalAmount)
            : me.Settled + me.WithholdingTaxAmount;
        if (grossThis <= Eps) return NotRole(Why.NothingSettled);

        var events = f.Events ?? Array.Empty<SettlementReceiptTotals.LedgerEvent>();
        var w = SettlementReceiptTotals.Walk(f.SourceTotalAmount, events, me, f.SourcesOutstandingNow);
        var closes = w.BalanceAfterRaw <= Eps;
        if (w.PriorCreditNotes.Count == 0 && NothingBefore(f, w) && f.OtherLiveReceiptVat <= Eps && closes)
            return new Decision(Mode.FullCopy, Why.SingleShotFull, f.SourceSubTotal, f.SourceVatAmount, 0m, f.SourceTotalAmount, false,
                ClosesSource: true, VatReportedOnSource: f.VatReportedWholesale);

        // งวดของการขายสินค้า (ภาษีขายลง 21911 ตั้งแต่ส่งมอบ) ⇒ คงเดิม · ไม่รู้ว่าพัก (ผังไม่มี 21913) ⇒ คิว (ผู้เรียกตัดสินจากบรรทัดสินค้า)
        if (!f.VatParkedUntilPayment) return NotRole(Why.VatNotParked, human: true);

        var ratio = gross <= Eps ? 0m : f.SourceVatAmount / gross;
        var proRata = R2(grossThis * ratio);
        decimal vat;
        if (f.VatReportedWholesale)
            // ใบเก่า: ภาษีรายงานที่ใบแจ้งหนี้แล้ว — ใบกำกับเป็นหลักฐานกระดาษ VAT ตามส่วน · งวดปิดถือ VAT ที่ใบอื่นยังไม่ถือ
            vat = closes ? remainingVat : Math.Min(remainingVat, proRata);
        else if (f.TaggedVatThisPayment > Eps)
            vat = f.TaggedVatThisPayment;                     // ออกทีหลัง: ถือเท่าที่ย้ายจริงของการรับนี้
        else if (closes && f.UndueVatRemaining > Eps)
            vat = f.UndueVatRemaining;                        // งวดปิด: ภาษีที่ยังพักทั้งหมด (หลังใบลดหนี้/มัดจำ) — ปิดยอดแล้ว 21913 = 0
        else
            vat = Math.Min(proRata, f.UndueVatRemaining > Eps ? f.UndueVatRemaining : remainingVat);
        vat = R2(Math.Max(0m, vat));
        var lines = SplitByRate(f.RateGroups, grossThis, vat);
        var sub = R2(lines.Sum(l => l.Base));
        var wht = me.WithholdingTaxAmount <= Eps && f.SourceWithholdingTaxAmount > Eps ? R2(grossThis - me.Settled) : R2(me.WithholdingTaxAmount);
        return new Decision(Mode.Installment, Why.Installment, sub, vat, wht, R2(sub + vat - wht), false,
            ClosesSource: closes, VatReportedOnSource: f.VatReportedWholesale, Lines: lines);
    }

    /// <summary>แบ่งยอดรวม VAT ของงวด (<paramref name="grossThis"/>) เป็นบรรทัดตามอัตราของใบตามสัดส่วนยอดรวมของแต่ละอัตรา · VAT ต่อบรรทัด = มูลค่า × อัตรา (ปัด 2 ตำแหน่ง) ·
    /// ผลต่างเศษสตางค์/ยอดงวดปิดไปที่บรรทัดที่ถือ VAT มากที่สุด (มูลค่าบรรทัดนั้นปรับให้ยอดรวมเท่าเดิม) · ไม่มีกลุ่ม ⇒ บรรทัดเดียว 7%</summary>
    private static IReadOnlyList<RateLine> SplitByRate(IReadOnlyList<RateLine>? groups, decimal grossThis, decimal vatTotal)
    {
        var g = (groups ?? Array.Empty<RateLine>()).Where(x => x.Base + x.Vat > Eps).ToList();
        if (g.Count == 0) g = new List<RateLine> { new(7m, 100m, 7m) };
        var totalGross = g.Sum(x => x.Base + x.Vat);
        var outLines = new List<RateLine>();
        var allocated = 0m;
        for (var i = 0; i < g.Count; i++)
        {
            var share = i == g.Count - 1 ? grossThis - allocated : R2(grossThis * (g[i].Base + g[i].Vat) / totalGross);
            allocated += share;
            var rate = g[i].VatRate;
            if (rate > 0m)
            {
                var b = R2(share / (1m + rate / 100m));
                outLines.Add(new RateLine(rate, b, R2(share - b)));
            }
            else outLines.Add(new RateLine(rate, share, 0m));
        }
        var diff = vatTotal - outLines.Sum(l => l.Vat);
        if (Math.Abs(diff) >= Eps)
        {
            var idx = outLines.FindIndex(l => l.Vat == outLines.Max(x => x.Vat));
            var t = outLines[idx];
            if (t.VatRate > 0m) outLines[idx] = t with { Vat = R2(t.Vat + diff), Base = R2(t.Base - diff) };
        }
        return outLines;
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
        Why.VatNotParked => "ภาษีขายของใบแจ้งหนี้นี้ไม่ได้พักไว้จนรับเงิน (ผังบัญชีไม่มี 21913 ตอนอนุมัติ) — ระบบไม่ออกใบกำกับรายงวดเอง · ผู้ทำบัญชีตัดสิน "
            + "(ออกใบกำกับภาษีอ้างใบแจ้งหนี้ตามยอดที่รับ)",
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

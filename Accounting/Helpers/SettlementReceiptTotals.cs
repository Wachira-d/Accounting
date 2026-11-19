using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>แถวยอดท้ายของ "ใบเสร็จรับชำระ" ที่ไม่ถือ VAT ของตัวเอง</b> — ตัวตัดสินเดียวของ renderer ทั้งสอง (HTML <c>BuildDocumentHtml</c> ·
/// QuestPDF <c>DocumentRenderer</c>) และหน้ารายละเอียด (<c>documents.html</c> อ่านผลจาก <c>DocumentResponse.SettlementTotals</c>) — รอบ 273
///
/// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-11-19) ═══ ใบกำกับภาษี TIV-20261026-0001 มูลค่า 700,000.00 + VAT 49,000.00 = 749,000.00 → บันทึกรับชำระ →
/// ใบเสร็จ REC-20261029-0001 บรรทัดเดียว "รับชำระเงินตามใบกำกับภาษีเลขที่ …" 749,000.00 · กระดาษและหน้ารายละเอียดพิมพ์
/// <b>"ยอดรวมก่อน VAT 749,000.00 · VAT 0.00 · ยอดสุทธิ 749,000.00"</b> — ผู้อ่านเข้าใจว่าเงิน 749,000 ยังไม่รวม VAT ทั้งที่รวมแล้ว และ VAT ถูกเรียกเก็บบนใบกำกับไปแล้ว</para>
///
/// <para>═══ ข้อมูลที่เก็บถูกอยู่แล้ว ═══ ใบเสร็จนี้<b>ไม่เป็นเจ้าของภาษีขาย</b> (VatAmount = 0 · <see cref="SettlementReceiptPolicy.CarriesTaxInvoiceRole"/> = false ·
/// รอบ 269 <see cref="OutputVatOwnership"/> ให้อยู่นอก ภ.พ.30/§87) — ตัวเลขที่ persist ถูก ผิดแค่ "ป้าย" ⇒ ตัวนี้เปลี่ยนเฉพาะการแสดงผล ไม่แตะยอดที่เก็บ/ภาษี</para>
///
/// <para>═══ การแสดงผลที่ถูก ═══ ซ่อนแถว "ยอดรวมก่อน VAT" + "VAT" (ใบนี้ไม่ได้เรียกเก็บ VAT — ห้ามบอกว่ามีฐานก่อน VAT เท่ายอดที่รับ) ·
/// แถวสุดท้ายเป็น "ยอดรับชำระ" · ใต้ยอดพิมพ์บรรทัดอ้างอิงต่อใบต้นทาง "(ตามใบกำกับภาษีเลขที่ … ลงวันที่ …: มูลค่า … · VAT … · รวม …)" + หมายเหตุว่า
/// ยอดรับชำระรวม VAT ตามเอกสารอ้างอิงแล้ว · รับบางส่วน ⇒ บอกว่าเป็นการรับบางส่วนของยอดใด · มีภาษีหัก ณ ที่จ่ายบนใบเสร็จ ⇒ คงแถว "ยอดตัดชำระ"
/// (ก่อนหัก) + แถวภาษีหัก ให้ตัวเลขบวกลงยอดรับชำระได้</para>
///
/// <para>ขอบเขตจงใจแคบ — ใบที่ <b>ถือ VAT ของตัวเอง</b> (ใบเสร็จขายสด · ใบกำกับ ณ วันรับเงิน §78/1 · มัดจำ) ไม่เข้า ⇒ "ยอดรวมก่อน VAT / VAT" เหมือนเดิมทุกตัวอักษร ·
/// ใบต้นทางที่ไม่มี VAT ก็ไม่เข้า (ไม่มี VAT ให้อธิบาย · กระดาษเดิมไม่ถูกแตะ)</para>
///
/// <para>═══ รอบ 273 ชุดแก้ฝ่ายค้าน ═══ <b>MED-1</b> "รับบางส่วน" ตัดสินจาก<b>ยอดคงค้างก่อนรับครั้งนี้</b> (ยอดใบต้นทาง − ใบลดหนี้ − การรับก่อนหน้า ตามลำดับเวลา ·
/// ใบลดหนี้ลดหนี้ได้เฉพาะเมื่อยังมียอดค้าง — กติกาเดียวกับ <c>UpdateSourceDocumentSettlementAsync</c>) เทียบกับ<b>ยอดที่การรับครั้งนี้ตัด</b>
/// (เงินรับ + ค่าธรรมเนียมที่ถูกหัก + บรรทัดปรับ — สามตัวเดียวกับที่บวกเข้า <c>PaidAmount</c> ตอนบันทึก) · ไม่มีรายการหลังครั้งนี้ ⇒ ยึดยอดค้างปัจจุบันของใบต้นทาง (ตรงที่สุด ·
/// ครอบการตัดชำระช่องทางอื่นที่ตัวนี้ไม่ได้ไล่ เช่น หักมัดจำ) · ใบลดหนี้ก่อนรับพิมพ์เป็นบรรทัดอ้างอิง ·
/// <b>L1</b> ภาษีหัก ณ ที่จ่ายที่พิมพ์ = ของ<b>การรับครั้งนี้</b> (<c>Payment.WithholdingTaxAmount</c>/การจัดสรร) ไม่ใช่ทั้งใบ — งวดผ่อนบวกกันลง ·
/// <b>L2</b> ใบต้นทางเป็น<b>ใบแจ้งหนี้</b> (ไม่ใช่ใบกำกับ) ⇒ ห้ามพิมพ์ "รวม VAT ที่เรียกเก็บตามเอกสารอ้างอิงแล้ว" (ภาษีขายของใบแจ้งหนี้ยังไม่ถูกเรียกเก็บด้วยใบกำกับ —
/// tax point §78/1 = วันรับเงิน) ⇒ ข้อความ <see cref="VatNote.InvoiceNotTaxInvoice"/> ·
/// <b>L4</b> ใบเสร็จรุ่นเก่าที่แปลงจากใบวางบิลรวมใบแจ้งหนี้ ⇒ ตัวโหลดส่งใบในบิลเป็นใบต้นทาง + <see cref="Facts.ViaBillingNote"/> (แสดงผลอย่างเดียว ไม่แตะค่าที่เก็บ)</para>
/// </summary>
public static class SettlementReceiptTotals
{
    public const string RuleCode = "RCPT-SETTLEMENT-TOTALS";

    private const decimal Eps = 0.005m;

    /// <summary>ใบต้นทางหนึ่งใบที่ใบเสร็จนี้รับชำระ (ค่าตามที่ persist บนใบต้นทาง · สกุลของใบต้นทาง)</summary>
    /// <param name="SubTotal">มูลค่าก่อน VAT (หลังส่วนลด)</param>
    /// <param name="WithholdingTaxAmount">ภาษีหักทั้งใบ — <b>ไม่พิมพ์</b>บนใบเสร็จแล้ว (L1: พิมพ์ของการรับครั้งนี้ <see cref="ThisSettlement.WithholdingTaxAmount"/>)</param>
    /// <param name="TotalAmount">ยอดที่ลูกค้าต้องจ่ายตามใบ (สุทธิหลังภาษีหัก ณ ที่จ่าย — <c>WhtGrossUp.PayeeTotal</c>)</param>
    public sealed record Source(DocumentType Type, string Number, DateTime Date,
        decimal SubTotal, decimal VatAmount, decimal WithholdingTaxAmount, decimal TotalAmount);

    /// <summary>ชนิดรายการที่ตัดยอดใบต้นทาง (นอกเหนือจากการรับครั้งนี้)</summary>
    public enum EventKind { Payment, CreditNote, Receipt }

    /// <summary>รายการตัดยอดใบต้นทางหนึ่งรายการ — เรียงด้วย (<paramref name="Date"/> วันที่ทางธุรกิจ, <paramref name="RecordedAt"/> เวลาบันทึก)</summary>
    /// <param name="Amount">ยอดที่ตัดหนี้ (การรับ = เงิน + ค่าธรรมเนียม + บรรทัดปรับ · ใบลดหนี้ = ยอดใบ)</param>
    public sealed record LedgerEvent(EventKind Kind, DateTime Date, DateTime RecordedAt, decimal Amount, string? Number = null);

    /// <summary>การรับชำระที่ใบเสร็จนี้เป็นหลักฐาน</summary>
    /// <param name="CashAmount">เงินที่รับจริง (= ยอดรับชำระบนใบ)</param>
    /// <param name="FeeAmount">ค่าธรรมเนียมที่ถูกหักจากยอดโอน (ลูกค้าจ่ายเต็ม · <c>Payment.FeeAmount</c>)</param>
    /// <param name="AdjustmentAmount">หนี้ที่ปิดด้วยบรรทัดปรับ (<c>Payment.SettlementAdjustmentAmount</c>)</param>
    /// <param name="WithholdingTaxAmount">ภาษีหัก ณ ที่จ่ายของการรับครั้งนี้ (ไม่ใช่ทั้งใบ — L1)</param>
    public sealed record ThisSettlement(DateTime Date, DateTime RecordedAt, decimal CashAmount, decimal FeeAmount,
        decimal AdjustmentAmount, decimal WithholdingTaxAmount)
    {
        /// <summary>ยอดที่การรับครั้งนี้ตัดหนี้ (สามตัวเดียวกับที่บวกเข้า <c>PaidAmount</c> ตอนบันทึกรับชำระ)</summary>
        public decimal Settled => CashAmount + FeeAmount + AdjustmentAmount;
    }

    /// <summary>ใบวางบิลที่ใบเสร็จรุ่นเก่าแปลงมา (L4)</summary>
    public sealed record BillingNoteRef(string Number, DateTime Date);

    /// <summary>ข้อเท็จจริงของใบเสร็จ (entity) + ใบต้นทางที่อ้าง — ผู้เรียกโหลดมา (ตัวตัดสินไม่รับ DbContext)</summary>
    /// <param name="This">การรับครั้งนี้ · <c>null</c> = ไม่รู้ลำดับ/ยอดคงค้าง (เทียบยอดรับกับยอดใบต้นทางรวมแบบเดิม — ใบรุ่นเก่าจากใบวางบิล)</param>
    /// <param name="Events">รายการตัดยอดใบต้นทางอื่นทั้งหมด (ไม่รวมครั้งนี้)</param>
    /// <param name="SourcesOutstandingNow">ยอดค้างปัจจุบันของใบต้นทางรวม (<c>BalanceDue</c>) · <c>null</c> = ไม่ใช้</param>
    public sealed record Facts(
        DocumentType ReceiptType,
        bool IsTaxInvoiceByLaw,
        bool IsDeposit,
        decimal ReceiptVatAmount,
        decimal ReceiptWithholdingTaxAmount,
        decimal ReceiptTotalAmount,
        IReadOnlyList<Source> Sources,
        ThisSettlement? This = null,
        IReadOnlyList<LedgerEvent>? Events = null,
        decimal? SourcesOutstandingNow = null,
        BillingNoteRef? ViaBillingNote = null);

    /// <summary>หมายเหตุ VAT ท้ายบรรทัดอ้างอิง (L2)</summary>
    public enum VatNote
    {
        /// <summary>ใบต้นทางเป็นใบกำกับภาษี/ใบเพิ่มหนี้ — VAT ถูกเรียกเก็บด้วยเอกสารนั้นแล้ว</summary>
        TaxInvoiced,
        /// <summary>มีใบแจ้งหนี้ (ไม่ใช่ใบกำกับ) ที่มี VAT — ยอดรวม VAT ตามใบแจ้งหนี้ แต่ใบเสร็จนี้ไม่ใช่ใบกำกับภาษี (ห้ามบอกว่าเรียกเก็บแล้ว)</summary>
        InvoiceNotTaxInvoice,
    }

    /// <param name="AmountReceived">ยอดแถวสุดท้าย "ยอดรับชำระ" (= <c>TotalAmount</c> ของใบเสร็จ — ค่าเดิม ไม่คิดใหม่)</param>
    /// <param name="ShowSettledGrossRow">ใบเสร็จมีภาษีหัก ณ ที่จ่าย ⇒ คงแถวยอดตัดชำระก่อนหัก (ป้าย "ยอดตัดชำระ") ให้บวกลงยอดรับชำระได้ · ไม่มี ⇒ ไม่มีแถวนี้</param>
    /// <param name="IsPartial">การรับครั้งนี้ไม่ปิดยอดคงค้างของใบต้นทาง</param>
    /// <param name="SourcesTotal">ยอดตามใบต้นทางรวม (สุทธิ)</param>
    /// <param name="References">บรรทัดอ้างอิง (หนึ่งบรรทัดต่อใบต้นทาง · ตามลำดับที่ส่งมา)</param>
    /// <param name="BalanceKnown">รู้ยอดคงค้างก่อน/หลังรับ (มี <see cref="Facts.This"/>) · false = ข้อความแบบเดิม "บางส่วนของยอด …"</param>
    /// <param name="BalanceBefore">ยอดคงค้างก่อนรับครั้งนี้</param>
    /// <param name="BalanceAfter">ยอดคงค้างหลังรับครั้งนี้ (ไม่ติดลบ)</param>
    /// <param name="PriorCreditNotes">ใบลดหนี้ที่ลดหนี้ก่อนรับครั้งนี้ (ยอดที่ลดจริง)</param>
    /// <param name="PriorSettled">การรับ/ตัดชำระก่อนครั้งนี้รวม (เท่าที่ไล่ได้)</param>
    public sealed record View(decimal AmountReceived, bool ShowSettledGrossRow, bool IsPartial, decimal SourcesTotal,
        IReadOnlyList<Source> References,
        bool BalanceKnown = false, decimal BalanceBefore = 0m, decimal BalanceAfter = 0m,
        IReadOnlyList<LedgerEvent>? PriorCreditNotes = null, decimal PriorSettled = 0m,
        ThisSettlement? This = null, VatNote Note = VatNote.TaxInvoiced, BillingNoteRef? ViaBillingNote = null);

    /// <summary>ชนิดใบเสร็จที่ตัวนี้ดูแล (ใบรับเงินฝั่งขาย)</summary>
    public static bool IsReceiptType(DocumentType t) => t is DocumentType.Receipt or DocumentType.ReceiptVoucher;

    private static bool Before(DateTime d1, DateTime r1, DateTime d2, DateTime r2)
        => d1.Date < d2.Date || (d1.Date == d2.Date && r1 < r2);

    /// <summary><c>null</c> = ใบนี้ใช้แถวยอดปกติ ("ยอดรวมก่อน VAT / VAT / ยอดสุทธิ") · มีค่า = ใบเสร็จรับชำระที่ไม่ถือ VAT ของใบต้นทางที่มี VAT</summary>
    public static View? Decide(Facts f)
    {
        if (!IsReceiptType(f.ReceiptType)) return null;
        // ใบที่ถือ VAT ของตัวเอง (ใบกำกับ ณ วันรับเงิน §78/1 · ขายสด · มัดจำ) = แถวปกติ — ทิศตรงข้ามที่เทสต์ล็อกไว้
        if (f.IsTaxInvoiceByLaw || f.IsDeposit || Math.Abs(f.ReceiptVatAmount) >= Eps) return null;
        var sources = (f.Sources ?? Array.Empty<Source>())
            .Where(s => SettlementReceiptPolicy.IsReceivableSource(s.Type)).ToList();
        if (sources.Count == 0) return null;
        // ใบต้นทางไม่มี VAT ⇒ ไม่มีอะไรต้องอธิบาย (กระดาษเดิม)
        if (sources.Sum(s => s.VatAmount) < Eps) return null;
        var sourcesTotal = sources.Sum(s => s.TotalAmount);
        // L2: ใบแจ้งหนี้ที่มี VAT ยังไม่ได้เรียกเก็บ VAT ด้วยใบกำกับ ⇒ ห้ามพิมพ์ว่า "เรียกเก็บแล้ว"
        var note = sources.Any(s => s.Type == DocumentType.Invoice && s.VatAmount >= Eps)
            ? VatNote.InvoiceNotTaxInvoice : VatNote.TaxInvoiced;
        var gross = f.ReceiptWithholdingTaxAmount > 0m;

        if (f.This is not { } me)
        {
            // ไม่รู้ลำดับ (ใบรุ่นเก่า) — เทียบยอดรับกับยอดใบต้นทางรวมแบบเดิม
            var partialLegacy = f.ReceiptTotalAmount < sourcesTotal - Eps;
            return new View(f.ReceiptTotalAmount, gross, partialLegacy, sourcesTotal, sources,
                Note: note, ViaBillingNote: f.ViaBillingNote);
        }

        // MED-1: ไล่ยอดคงค้างตามลำดับเวลา — ใบลดหนี้ลดหนี้ได้เฉพาะตอนยังค้าง (ใบที่ออกหลังชำระครบ = คืนเงิน ไม่ลดหนี้ · ไม่พิมพ์)
        var events = (f.Events ?? Array.Empty<LedgerEvent>())
            .OrderBy(e => e.Date.Date).ThenBy(e => e.RecordedAt).ToList();
        var bal = sourcesTotal;
        var priorCn = new List<LedgerEvent>();
        var priorSettled = 0m;
        var later = false;
        foreach (var e in events)
        {
            if (!Before(e.Date, e.RecordedAt, me.Date, me.RecordedAt)) { later = true; continue; }
            if (e.Kind == EventKind.CreditNote)
            {
                if (bal <= Eps) continue;
                var applied = Math.Min(e.Amount, bal);
                bal -= applied;
                priorCn.Add(e with { Amount = applied });
            }
            else
            {
                bal -= e.Amount;
                priorSettled += e.Amount;
            }
        }
        var before = Math.Max(0m, bal);
        var after = before - me.Settled;
        // ไม่มีรายการหลังครั้งนี้ ⇒ ยอดค้างปัจจุบันของใบต้นทาง = ยอดหลังรับครั้งนี้พอดี (ครอบการตัดชำระช่องทางที่ไม่ได้ไล่ข้างบน เช่น หักมัดจำ)
        if (!later && f.SourcesOutstandingNow is decimal now)
        {
            after = now;
            before = Math.Max(0m, now + me.Settled);
        }
        var partial = after > Eps;
        return new View(f.ReceiptTotalAmount, gross, partial, sourcesTotal, sources,
            BalanceKnown: true, BalanceBefore: Round2(before), BalanceAfter: Round2(Math.Max(0m, after)),
            PriorCreditNotes: priorCn, PriorSettled: Round2(priorSettled), This: me, Note: note, ViaBillingNote: f.ViaBillingNote);
    }

    private static decimal Round2(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    /// <summary>"รวม" ในบรรทัดอ้างอิง = มูลค่า + VAT (ยอดบนใบกำกับก่อนหัก ณ ที่จ่าย — ตรงกับตัวเลขที่ลูกค้าเห็นบนใบต้นทาง)</summary>
    public static decimal GrossOf(Source s) => s.SubTotal + s.VatAmount;
}

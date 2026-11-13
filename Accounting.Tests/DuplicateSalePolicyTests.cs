using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 266 แพ็ก C (erp-review/2026-11-12/team-266-manual-doc-link.md §4.7 แพ็ก C · คำตัดสิน 266-1-5 · 266-1-36…40) —
/// ตัวช่วยใบซ้ำ <see cref="DuplicateSalePolicy"/> · ใบแจ้งการนำเงินไปตัดชำระ <see cref="PaymentApplicationAdvice"/> · สายเอกสารเห็นเงิน <see cref="DocumentChainMoney"/> ·
/// ทั้งสองทิศ: ใบที่ยกเลิกได้ถูกยกเลิก / ใบที่ยื่นภาษีแล้วได้ใบลดหนี้ / ทำไม่ได้ทั้งคู่ = ผู้ทำบัญชีตัดสิน · คู่ที่ไม่ใช่ใบซ้ำ (คนละราย · ยอดต่าง · จ่ายแล้ว) ไม่ถูกแตะ
/// </summary>
public class DuplicateSalePolicyTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly DateTime D0 = new(2026, 11, 3);

    private static DuplicateSaleDocFacts Cash(DocumentStatus status = DocumentStatus.Approved, Guid? contact = null, decimal total = 1_070m,
        string? taxLock = null, string? block = null, DateTime? date = null, DocumentType type = DocumentType.Receipt)
        => new(Guid.NewGuid(), "RC003", type, status, false, false, contact ?? A, "THB", 1m, total, total, 0m, date ?? D0, taxLock, block);

    private static DuplicateSaleDocFacts Inv(DocumentStatus status = DocumentStatus.Approved, Guid? contact = null, decimal total = 1_070m,
        decimal paid = 0m, string? taxLock = null, string? block = null, DateTime? date = null, DocumentType type = DocumentType.TaxInvoice,
        string currency = "THB")
        => new(Guid.NewGuid(), "INV001", type, status, false, false, contact ?? A, currency, 1m, total, paid, total - paid, date ?? D0, taxLock, block);

    private static DuplicateSaleContext Ctx(ReceiptLinkKind kind = ReceiptLinkKind.StandaloneSale, bool creditNoteAllowed = true,
        bool invoicePayments = false, string? liveCn = null, decimal cashWht = 0m, decimal invWht = 0m, string? periodProblem = null,
        bool fromCredit = false, bool branch = false)
        => new(kind, fromCredit, cashWht, false, invoicePayments, liveCn, creditNoteAllowed, periodProblem, branch, invWht);

    // ───────── คงใบขายสด ─────────

    [Fact]
    public void KeepCashSale_VoidsInvoiceWhenAllowed()
    {
        var v = DuplicateSalePolicy.Decide(Cash(), Inv(), Ctx(), DuplicateSaleKeep.CashSale);
        Assert.Equal((true, DuplicateSaleAction.VoidInvoice), (v.Allowed, v.Action));
        var effects = DuplicateSalePolicy.Effects(v, Cash(), Inv());
        Assert.Contains(effects, e => e.Detail.Contains("ยกเลิก INV001", StringComparison.Ordinal));
        // ใบขายสดไม่ถูกแตะ
        Assert.Contains(effects, e => e.Label == "ใบที่คงไว้" && e.Detail.Contains("RC003", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepCashSale_CreditNoteWhenFiled()
    {
        var inv = Inv(taxLock: "ภ.พ.30 เดือน 11/2026 ที่ยื่น/ประกาศว่ายื่น/ล็อกแล้ว");
        var v = DuplicateSalePolicy.Decide(Cash(), inv, Ctx(), DuplicateSaleKeep.CashSale);
        Assert.Equal((true, DuplicateSaleAction.CreditNoteInvoice), (v.Allowed, v.Action));
        Assert.Contains(DuplicateSalePolicy.Effects(v, Cash(), inv), e => e.Label == "ใบลดหนี้" && e.Detail.Contains("งวดปัจจุบัน", StringComparison.Ordinal));
        // ทิศตรงข้าม: ใบแจ้งหนี้ (ไม่ใช่ใบกำกับ) ที่ติดภาษี — ออกใบลดหนี้อ้างไม่ได้ ⇒ ผู้ทำบัญชีตัดสิน ไม่ใช่ยกเลิกเงียบ
        var plain = DuplicateSalePolicy.Decide(Cash(), inv with { Type = DocumentType.Invoice }, Ctx(creditNoteAllowed: false), DuplicateSaleKeep.CashSale);
        Assert.Equal((false, DuplicateSaleAction.RequiresAccountant), (plain.Allowed, plain.Action));
    }

    [Fact]
    public void KeepCashSale_OtherVoidBlock_RequiresAccountant_NotCreditNote()
    {
        var v = DuplicateSalePolicy.Decide(Cash(), Inv(block: "มีเอกสารลูก RC009 อ้างอยู่"), Ctx(), DuplicateSaleKeep.CashSale);
        Assert.Equal((false, DuplicateSaleAction.RequiresAccountant, DuplicateSalePolicy.AccountantCode), (v.Allowed, v.Action, v.RuleCode));
        Assert.Contains("RC009", v.Message, StringComparison.Ordinal);
    }

    // ───────── คงใบแจ้งหนี้ ─────────

    [Fact]
    public void KeepInvoice_VoidsCashSaleAndRecordsPaymentOnSameDate()
    {
        var cash = Cash(date: D0);
        var inv = Inv(date: D0.AddDays(-2));   // ใบแจ้งหนี้ออกก่อน — เงินรับวันใบขายสด
        var v = DuplicateSalePolicy.Decide(cash, inv, Ctx(), DuplicateSaleKeep.Invoice);
        Assert.Equal((true, DuplicateSaleAction.VoidCashSaleAndRecordPayment), (v.Allowed, v.Action));
        Assert.Equal(D0, DuplicateSalePolicy.PaymentDate(cash.DocumentDate, inv.DocumentDate));
        Assert.Contains(DuplicateSalePolicy.Effects(v, cash, inv), e => e.Detail.Contains("ลงวันที่ 03/11/2026", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepInvoice_InvoiceAfterCashSale_PaysOnInvoiceDate()
    {
        // 266-1-38: ลูกหนี้ของใบแจ้งหนี้ยังไม่เกิดก่อนวันที่ใบ — รับชำระลงวันที่ใบแจ้งหนี้ (สูตรเดียวกับวันที่ลงของเส้นย้าย 266-1-24)
        Assert.Equal(D0.AddDays(5), DuplicateSalePolicy.PaymentDate(D0, D0.AddDays(5)));
        var v = DuplicateSalePolicy.Decide(Cash(date: D0), Inv(date: D0.AddDays(5)), Ctx(), DuplicateSaleKeep.Invoice);
        Assert.Contains(DuplicateSalePolicy.Effects(v, Cash(date: D0), Inv(date: D0.AddDays(5))),
            e => e.Detail.Contains("ลงวันที่หลัง", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepInvoice_WithholdingTax_RequiresAccountant()
    {
        Assert.Equal(DuplicateSalePolicy.WhtCode, DuplicateSalePolicy.Decide(Cash(), Inv(), Ctx(cashWht: 30m), DuplicateSaleKeep.Invoice).RuleCode);
        Assert.Equal(DuplicateSalePolicy.WhtCode, DuplicateSalePolicy.Decide(Cash(), Inv(), Ctx(invWht: 30m), DuplicateSaleKeep.Invoice).RuleCode);
        // ทิศตรงข้าม: คงใบขายสดไม่ติดภาษีถูกหัก (ไม่มีการรับชำระใหม่)
        Assert.True(DuplicateSalePolicy.Decide(Cash(), Inv(), Ctx(cashWht: 30m), DuplicateSaleKeep.CashSale).Allowed);
    }

    [Fact]
    public void KeepInvoice_ClosedPeriodOnMoneyDate_RequiresAccountant()
    {
        var v = DuplicateSalePolicy.Decide(Cash(), Inv(), Ctx(periodProblem: "งวดบัญชี ต.ค. ปิดแล้ว"), DuplicateSaleKeep.Invoice);
        Assert.Equal((false, DuplicateSaleAction.RequiresAccountant, DuplicateSalePolicy.PeriodCode), (v.Allowed, v.Action, v.RuleCode));
    }

    [Fact]
    public void NeitherVoidable_RequiresAccountant()
    {
        var cash = Cash(taxLock: "e-Tax ถึงกรมสรรพากรแล้ว");
        var inv = Inv(type: DocumentType.Invoice, taxLock: "รายงานภาษีที่ยื่น/ล็อกแล้ว");
        var options = DuplicateSalePolicy.Options(cash, inv, Ctx(creditNoteAllowed: false));
        Assert.All(options, o => Assert.Equal((false, DuplicateSaleAction.RequiresAccountant), (o.Allowed, o.Action)));
        Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.WayForward)));
        Assert.All(options, o => Assert.Empty(DuplicateSalePolicy.Effects(o, cash, inv)));   // ไม่มีพรีวิวของทางที่ไม่ทำ
    }

    // ───────── คู่ที่ไม่ใช่ใบซ้ำ (ทิศตรงข้าม — ไม่ถูกแตะ) ─────────

    [Fact]
    public void NotDuplicatePairs_Refused_WithWayForward()
    {
        var refusals = new[]
        {
            (DuplicateSalePolicy.Decide(Cash(), Inv(contact: B), Ctx(), DuplicateSaleKeep.CashSale), DuplicateSalePolicy.ContactCode),
            (DuplicateSalePolicy.Decide(Cash(), Inv(contact: B), Ctx(branch: true), DuplicateSaleKeep.CashSale), DuplicateSalePolicy.BranchCode),
            (DuplicateSalePolicy.Decide(Cash(), Inv(total: 1_200m), Ctx(), DuplicateSaleKeep.CashSale), DuplicateSalePolicy.AmountCode),
            (DuplicateSalePolicy.Decide(Cash(), Inv(paid: 1_070m), Ctx(invoicePayments: true), DuplicateSaleKeep.Invoice), DuplicateSalePolicy.InvoicePaidCode),
            (DuplicateSalePolicy.Decide(Cash(), Inv(), Ctx(liveCn: "CN002"), DuplicateSaleKeep.CashSale), DuplicateSalePolicy.CreditNoteExistsCode),
            (DuplicateSalePolicy.Decide(Cash(), Inv(status: DocumentStatus.Draft), Ctx(), DuplicateSaleKeep.CashSale), DuplicateSalePolicy.DraftTargetCode),
            (DuplicateSalePolicy.Decide(Cash(), Inv(status: DocumentStatus.Voided), Ctx(), DuplicateSaleKeep.CashSale), DuplicateSalePolicy.VoidedCode),
            (DuplicateSalePolicy.Decide(Cash(), Inv(currency: "USD"), Ctx(), DuplicateSaleKeep.CashSale), DuplicateSalePolicy.CurrencyCode),
            (DuplicateSalePolicy.Decide(Cash(), Inv(type: DocumentType.DebitNote), Ctx(), DuplicateSaleKeep.CashSale), DuplicateSalePolicy.TargetCode),
            (DuplicateSalePolicy.Decide(Cash(status: DocumentStatus.Draft), Inv(), Ctx(), DuplicateSaleKeep.CashSale), DuplicateSalePolicy.NotCashSaleCode),
            (DuplicateSalePolicy.Decide(Cash(), Inv(), Ctx(kind: ReceiptLinkKind.SettlementOfOther), DuplicateSaleKeep.CashSale), DuplicateSalePolicy.NotCashSaleCode),
            (DuplicateSalePolicy.Decide(Cash(), Inv(), Ctx(fromCredit: true), DuplicateSaleKeep.Invoice), DuplicateSalePolicy.FromCreditCode),
            (DuplicateSalePolicy.Decide(Cash(contact: Guid.Empty), Inv(), Ctx(), DuplicateSaleKeep.CashSale), DuplicateSalePolicy.ContactCode),
        };
        foreach (var (v, code) in refusals)
        {
            Assert.Equal((false, DuplicateSaleAction.Refused, code), (v.Allowed, v.Action, v.RuleCode));
            Assert.False(string.IsNullOrWhiteSpace(v.Message));
            Assert.False(string.IsNullOrWhiteSpace(v.WayForward));   // F2 ข้อ 8 — ห้าม "ไม่ได้" เปล่า ๆ
        }
        // ยอดต่างไม่เกิน 0.01 = เท่ากัน (ตัวเทียบเดียวกับคำเตือน G-2)
        Assert.True(DuplicateSalePolicy.Decide(Cash(total: 1_070m), Inv(total: 1_070.01m), Ctx(), DuplicateSaleKeep.CashSale).Allowed);
    }

    [Fact]
    public void PreviewToken_ChangesWhenFactsChange_StableOtherwise()
    {
        var cash = Cash();
        var inv = Inv();
        var t = DuplicateSalePolicy.PreviewToken(DuplicateSaleKeep.CashSale, DuplicateSaleAction.VoidInvoice, cash, inv);
        Assert.Equal(t, DuplicateSalePolicy.PreviewToken(DuplicateSaleKeep.CashSale, DuplicateSaleAction.VoidInvoice, cash, inv));
        Assert.NotEqual(t, DuplicateSalePolicy.PreviewToken(DuplicateSaleKeep.CashSale, DuplicateSaleAction.VoidInvoice, cash, inv with { PaidAmount = 100m }));
        Assert.NotEqual(t, DuplicateSalePolicy.PreviewToken(DuplicateSaleKeep.Invoice, DuplicateSaleAction.VoidInvoice, cash, inv));
        Assert.NotEqual(t, DuplicateSalePolicy.PreviewToken(DuplicateSaleKeep.CashSale, DuplicateSaleAction.CreditNoteInvoice, cash, inv with { TaxLockReason = "x" }));
    }

    [Fact]
    public void OffersWizardButton_OnlyIssuedCashSaleWithContact()
    {
        static ReceiptLinkSourceFacts Src(DocumentStatus st, Guid contact, Guid? related = null, bool deposit = false, bool settlement = false,
            DocumentType type = DocumentType.Receipt, bool cashTaxInvoice = false)
            => new(false, type, st, false, false, deposit, settlement, settlement, related, cashTaxInvoice, contact, "THB", 1m, 1_070m, "RC003");
        Assert.True(DuplicateSalePolicy.OffersWizardButton(Src(DocumentStatus.Approved, A)));
        Assert.True(DuplicateSalePolicy.OffersWizardButton(Src(DocumentStatus.Paid, A, type: DocumentType.TaxInvoice, cashTaxInvoice: true)));
        Assert.False(DuplicateSalePolicy.OffersWizardButton(Src(DocumentStatus.Draft, A)));
        Assert.False(DuplicateSalePolicy.OffersWizardButton(Src(DocumentStatus.Approved, Guid.Empty)));
        Assert.False(DuplicateSalePolicy.OffersWizardButton(Src(DocumentStatus.Approved, A, related: Guid.NewGuid())));   // ใบเสร็จที่รับชำระใบอื่น
        Assert.False(DuplicateSalePolicy.OffersWizardButton(Src(DocumentStatus.Approved, A, deposit: true)));
        Assert.False(DuplicateSalePolicy.OffersWizardButton(Src(DocumentStatus.Approved, A, settlement: true)));
        Assert.False(DuplicateSalePolicy.OffersWizardButton(Src(DocumentStatus.Approved, A, type: DocumentType.TaxInvoice)));   // ใบกำกับเครดิต ไม่ใช่ขายสด
    }

    // ───────── ใบแจ้งการนำเงินไปตัดชำระ (266-1-6) ─────────

    private static PaymentApplicationAdviceFacts Advice(bool late = true, bool moved = false, bool released = false, bool voided = false)
        => new("RV-0001", D0, 5_000m, "RC-0001", "INV001", D0.AddDays(3), 1_070m, 1_070m, 0m, D0.AddDays(4), "RV-0001/A1",
            late, moved, released, false, voided, 3_930m);

    [Fact]
    public void Advice_NotInVatReport_NoTaxSeries()
    {
        var m = PaymentApplicationAdvice.Build(Advice());
        Assert.False(m.IsTaxDocument);
        Assert.Null(m.TaxNumberSeries);
        Assert.False(m.IncludedInVatReport);
        // เลขอ้างอิง = เลขสมุดรายวันของการตัดชำระ (ไม่กินเลขเล่มเอกสาร) · คงที่ — พิมพ์ซ้ำได้เลขเดิม
        Assert.Equal("ADV-RV-0001/A1", m.Reference);
        Assert.Equal(m.Reference, PaymentApplicationAdvice.Build(Advice()).Reference);
        Assert.Equal(D0.AddDays(4), m.AppliedOn);
    }

    [Fact]
    public void Advice_OnlyForLiveAllocationsMadeAfterReceipt()
    {
        Assert.Null(PaymentApplicationAdvice.Refusal(Advice(late: false, moved: true)));   // ย้ายการรับชำระ
        Assert.NotNull(PaymentApplicationAdvice.Refusal(Advice(late: false)));             // ตัดพร้อมรับเงิน — ใบเสร็จของการรับชำระคือหลักฐาน
        Assert.NotNull(PaymentApplicationAdvice.Refusal(Advice(released: true)));
        Assert.NotNull(PaymentApplicationAdvice.Refusal(Advice(voided: true)));
        var ex = Assert.Throws<BusinessRuleException>(() => PaymentApplicationAdvice.Build(Advice(released: true)));
        Assert.Equal(PaymentApplicationAdvice.NotEligibleCode, ex.RuleCode);
    }

    // ───────── สายเอกสารเห็นเงิน (266-1-40) ─────────

    [Fact]
    public void Chain_ShowsCreditAllocationNode()
    {
        var inv = Guid.NewGuid();
        var dep = Guid.NewGuid();
        var alloc = Guid.NewGuid();
        var nodes = DocumentChainMoney.Nodes(new[]
        {
            new ChainMoneyFact(ChainMoneyKind.CreditAllocation, alloc, inv, "RV-0001", D0.AddDays(4), 1_070m, PaymentId: Guid.NewGuid(), AllocationId: alloc),
            new ChainMoneyFact(ChainMoneyKind.DepositApplied, dep, inv, "DP-0001", D0, 500m, OpenDocumentId: dep),
            new ChainMoneyFact(ChainMoneyKind.CreditAllocation, Guid.NewGuid(), Guid.NewGuid(), "RV-0009", D0, 10m),   // ใบนอกสาย — ไม่แสดง
        }, new[] { inv });
        Assert.Equal(2, nodes.Count);
        Assert.Equal(ChainMoneyKind.DepositApplied, nodes[0].Fact.Kind);   // เรียงตามวันที่
        var credit = Assert.Single(nodes, n => n.Fact.Kind == ChainMoneyKind.CreditAllocation);
        Assert.True(credit.HasAdvice);
        Assert.Contains("RV-0001", credit.Label, StringComparison.Ordinal);
        Assert.Contains("1,070.00", credit.Label, StringComparison.Ordinal);
        Assert.False(nodes[0].HasAdvice);   // มัดจำ — ใบมัดจำคือกระดาษ (เปิดใบได้) ไม่มีใบแจ้ง
        Assert.Equal(dep, nodes[0].Fact.OpenDocumentId);
    }

    [Fact]
    public void Chain_MovedPaymentNode_NamesSourceDocument()
    {
        var inv = Guid.NewGuid();
        var n = Assert.Single(DocumentChainMoney.Nodes(new[]
        {
            new ChainMoneyFact(ChainMoneyKind.MovedPayment, Guid.NewGuid(), inv, "RV-0002", D0, 700m, FromDocumentNumber: "INV002"),
        }, new[] { inv }));
        Assert.Contains("INV002", n.Label, StringComparison.Ordinal);
        Assert.True(n.HasAdvice);
        // ไม่มีข้อมูล = ไม่มีโหนด (ทิศตรงข้าม — สายเดิมไม่เปลี่ยน)
        Assert.Empty(DocumentChainMoney.Nodes(Array.Empty<ChainMoneyFact>(), new[] { inv }));
    }
}

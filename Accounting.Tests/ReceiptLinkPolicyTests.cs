using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 266 แพ็ก A — ตัวตัดสินเดียว <see cref="ReceiptLinkPolicy"/> (erp-review/2026-11-12/team-266-manual-doc-link.md §4.1/§4.7 · คำตัดสิน 266-1-x):
/// จัดชนิดใบเสร็จ R0–R7 · เจ้าเดียวกัน = ContactId เท่านั้น · ทุกการปฏิเสธมีทางไปต่อ · ทิศตรงข้าม: ใบค้างของเจ้าเดียวกันผูกได้
/// </summary>
public class ReceiptLinkPolicyTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid InvId = Guid.NewGuid();

    private static ReceiptLinkSourceFacts Rc(DocumentStatus status = DocumentStatus.Draft, Guid? contact = null, Guid? related = null,
        bool deposit = false, bool settlement = false, string currency = "THB", decimal total = 1_000m,
        DocumentType type = DocumentType.Receipt, bool cashTaxInvoice = false, bool deleted = false, bool replaced = false,
        bool fromCredit = false, string? previousSource = null)
        => new(false, type, status, deleted, replaced, deposit, settlement, settlement, related, cashTaxInvoice,
            contact ?? A, currency, 1m, total, "RC003", fromCredit, previousSource);

    private static ReceiptLinkInvoiceFacts Inv(DocumentStatus status = DocumentStatus.Approved, Guid? contact = null, decimal balance = 1_000m,
        decimal vat = 0m, DocumentType type = DocumentType.TaxInvoice, string currency = "THB", bool servesAsReceipt = false)
        => new(InvId, type, status, false, false, contact ?? A, currency, 1m, balance, vat, servesAsReceipt, "INV001");

    private static readonly ReceiptLinkSourceFacts BareCredit = new(true, DocumentType.Receipt, DocumentStatus.Approved, false, false, false, false, true,
        null, false, A, "THB", 1m, 600m, "PAY-1");

    private static ReceiptLinkContext Ctx(decimal amount = 1_000m, decimal available = 0m, bool branch = false)
        => new(amount, branch, null, null, available, null);

    [Fact]
    public void Classify_DraftReceipt()
    {
        Assert.Equal(ReceiptLinkKind.DraftReceipt, ReceiptLinkPolicy.Classify(Rc()));
        Assert.Equal(ReceiptLinkKind.DraftReceipt, ReceiptLinkPolicy.Classify(Rc(DocumentStatus.Rejected, related: Guid.NewGuid())));
        // ใบร่างของการชำระ (ผู้กดไม่มีสิทธิ์อนุมัติ) ไม่ใช่ใบร่างที่สร้างเอง — เงินอยู่ที่การชำระ
        Assert.Equal(ReceiptLinkKind.SettlementOfOther, ReceiptLinkPolicy.Classify(Rc(settlement: true, related: Guid.NewGuid())));
        Assert.Equal(ReceiptLinkKind.DepositReceipt, ReceiptLinkPolicy.Classify(Rc(deposit: true)));
    }

    [Fact]
    public void Classify_SettlementOfOther()
    {
        Assert.Equal(ReceiptLinkKind.SettlementOfOther, ReceiptLinkPolicy.Classify(Rc(DocumentStatus.Paid, settlement: true, related: Guid.NewGuid())));
        Assert.Equal(ReceiptLinkKind.ManualSettlementOfOther, ReceiptLinkPolicy.Classify(Rc(DocumentStatus.Paid, related: Guid.NewGuid())));
        // ฝ่ายค้าน L2: ใบเสร็จสร้างเองที่รออนุมัติ ยังไม่ลงบัญชี — ไม่ใช่ขายสด/ไม่ใช่ของการชำระใบอื่น
        Assert.Equal(ReceiptLinkKind.PendingApprovalReceipt, ReceiptLinkPolicy.Classify(Rc(DocumentStatus.WaitingApproval)));
        Assert.Equal(ReceiptLinkKind.PendingApprovalReceipt, ReceiptLinkPolicy.Classify(Rc(DocumentStatus.WaitingApproval, related: Guid.NewGuid())));
        var pending = ReceiptLinkPolicy.Decide(Rc(DocumentStatus.WaitingApproval), Inv(), Ctx());
        Assert.Equal((false, ReceiptLinkPolicy.PendingCode), (pending.Allowed, pending.RuleCode));
        Assert.DoesNotContain("ลงรายได้", pending.Message, StringComparison.Ordinal);
        Assert.Equal(ReceiptLinkKind.NotLinkable, ReceiptLinkPolicy.Classify(Rc(DocumentStatus.Voided, settlement: true, related: Guid.NewGuid())));
        Assert.Equal(ReceiptLinkKind.NotLinkable, ReceiptLinkPolicy.Classify(Rc(type: DocumentType.Quotation)));
    }

    [Fact]
    public void Classify_StandaloneSale_RoutesToDuplicateWizard()
    {
        var sale = Rc(DocumentStatus.Paid);
        Assert.Equal(ReceiptLinkKind.StandaloneSale, ReceiptLinkPolicy.Classify(sale));
        var cashTaxInvoice = Rc(DocumentStatus.Paid, type: DocumentType.TaxInvoice, cashTaxInvoice: true);
        Assert.Equal(ReceiptLinkKind.StandaloneSale, ReceiptLinkPolicy.Classify(cashTaxInvoice));
        foreach (var r in new[] { sale, cashTaxInvoice })
        {
            var v = ReceiptLinkPolicy.Decide(r, Inv(), Ctx());
            // ไม่ใช่การผูก — ชี้หาตัวช่วยใบซ้ำ (แพ็ก C) และห้ามเขียน FK (266-1-5)
            Assert.Equal((false, ReceiptLinkRoute.DuplicateSaleWizard, ReceiptLinkPolicy.DuplicateSaleCode), (v.Allowed, v.Route, v.RuleCode));
        }
        // ฝ่ายค้าน L1: ใบเสร็จที่ออกจากเงินรอจัดสรร (รอบ 265) — ปฏิเสธเหมือนกัน แต่ทางไปต่อ = ยกเลิกใบเสร็จ เงินกลับเป็นเงินรอจัดสรร แล้วจัดสรร
        var fromCredit = ReceiptLinkPolicy.Decide(Rc(DocumentStatus.Paid, fromCredit: true), Inv(), Ctx());
        Assert.Equal((false, ReceiptLinkPolicy.DuplicateSaleCode), (fromCredit.Allowed, fromCredit.RuleCode));
        Assert.Contains("เงินรอจัดสรร", fromCredit.WayForward, StringComparison.Ordinal);
        Assert.DoesNotContain("ใบลดหนี้", fromCredit.WayForward, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_RefusesOtherContact()
    {
        var v = ReceiptLinkPolicy.Decide(Rc(), Inv(contact: B), Ctx());
        Assert.Equal((false, ReceiptLinkPolicy.ContactCode), (v.Allowed, v.RuleCode));
        // ใบเสร็จไม่มีผู้ติดต่อ ≠ เจ้าเดียวกัน (ไม่มีการเทียบชื่อ · F2 ข้อ 3) — ฝ่ายค้าน L3: เหตุที่บอกต้องจริง ("ไม่ระบุผู้ติดต่อ" ไม่ใช่ "คนละราย")
        var walkIn = ReceiptLinkPolicy.Decide(Rc(contact: Guid.Empty), Inv(contact: Guid.Empty), Ctx());
        Assert.Equal(ReceiptLinkPolicy.ContactCode, walkIn.RuleCode);
        Assert.Contains("ไม่ระบุผู้ติดต่อ", walkIn.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("คนละราย", walkIn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_RefusesBranchContact()
    {
        var v = ReceiptLinkPolicy.Decide(Rc(), Inv(contact: B), Ctx(branch: true));
        Assert.Equal((false, ReceiptLinkPolicy.BranchCode), (v.Allowed, v.RuleCode));
        Assert.Contains("รวมผู้ติดต่อ", v.WayForward, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_RefusesForeignCurrency()
    {
        Assert.Equal(ReceiptLinkPolicy.CurrencyCode, ReceiptLinkPolicy.Decide(Rc(), Inv(currency: "USD"), Ctx()).RuleCode);
        Assert.Equal(ReceiptLinkPolicy.CurrencyCode, ReceiptLinkPolicy.Decide(Rc(currency: "USD"), Inv(), Ctx()).RuleCode);
    }

    [Fact]
    public void Decide_RefusesDraftInvoice()
    {
        Assert.Equal(ReceiptLinkPolicy.DraftTargetCode, ReceiptLinkPolicy.Decide(Rc(), Inv(DocumentStatus.Draft), Ctx()).RuleCode);
        Assert.Equal(ReceiptLinkPolicy.DraftTargetCode, ReceiptLinkPolicy.Decide(Rc(), Inv(DocumentStatus.WaitingApproval), Ctx()).RuleCode);
        Assert.Equal(ReceiptLinkPolicy.VoidedCode, ReceiptLinkPolicy.Decide(Rc(), Inv(DocumentStatus.Voided), Ctx()).RuleCode);
    }

    [Fact]
    public void Decide_RefusesServedAsReceiptTarget()
    {
        var v = ReceiptLinkPolicy.Decide(Rc(), Inv(servesAsReceipt: true), Ctx());
        Assert.Equal((false, ReceiptLinkPolicy.ServedAsReceiptCode), (v.Allowed, v.RuleCode));
    }

    [Fact]
    public void Decide_RefusesPartialVatInvoice()
    {
        // ใบแจ้งหนี้ (ไม่ใช่ใบกำกับ) มี VAT พัก — รับชำระบางส่วนไม่ได้ (ม.78/1 · ตัวตัดสินเดิม VatInvoiceSettlementGate)
        var v = ReceiptLinkPolicy.Decide(Rc(total: 500m), Inv(type: DocumentType.Invoice, vat: 70m, balance: 1_070m), Ctx(500m));
        Assert.Equal((false, ReceiptLinkPolicy.PartialVatCode), (v.Allowed, v.RuleCode));
        // ทิศตรงข้าม: ใบกำกับภาษี (VAT ถึงกำหนดแล้ว) รับบางส่วนได้ · ใบแจ้งหนี้ปิดเต็มยอดได้
        Assert.True(ReceiptLinkPolicy.Decide(Rc(total: 500m), Inv(type: DocumentType.TaxInvoice, vat: 70m, balance: 1_070m), Ctx(500m)).Allowed);
        Assert.True(ReceiptLinkPolicy.Decide(Rc(total: 1_070m), Inv(type: DocumentType.Invoice, vat: 70m, balance: 1_070m), Ctx(1_070m)).Allowed);
        // เงินรอจัดสรรบางส่วนกับใบแจ้งหนี้ VAT = ปฏิเสธเหมือนกัน
        var credit = new ReceiptLinkSourceFacts(true, DocumentType.Receipt, DocumentStatus.Approved, false, false, false, false, true, null, false,
            A, "THB", 1m, 500m, "PAY-1");
        Assert.Equal(ReceiptLinkPolicy.PartialVatCode,
            ReceiptLinkPolicy.Decide(credit, Inv(type: DocumentType.Invoice, vat: 70m, balance: 1_070m), Ctx(500m, available: 500m)).RuleCode);
    }

    [Fact]
    public void Decide_AllowsSameContactOpenInvoice()
    {
        var v = ReceiptLinkPolicy.Decide(Rc(), Inv(), Ctx());
        Assert.Equal((true, ReceiptLinkRoute.SetDraftSource, ReceiptLinkKind.DraftReceipt), (v.Allowed, v.Route, v.Kind));
        var credit = new ReceiptLinkSourceFacts(true, DocumentType.Receipt, DocumentStatus.Approved, false, false, false, false, true, null, false,
            A, "THB", 1m, 600m, "PAY-1");
        Assert.Equal(ReceiptLinkRoute.AllocateCredit, ReceiptLinkPolicy.Decide(credit, Inv(), Ctx(600m, available: 600m)).Route);
        Assert.Equal(ReceiptLinkRoute.ApplyDeposit,
            ReceiptLinkPolicy.Decide(Rc(DocumentStatus.Paid, deposit: true), Inv(), Ctx(400m, available: 400m)).Route);
        // ใบเสร็จร่างที่อ้างใบอื่นของเจ้าเดียวกัน — เปลี่ยนใบต้นทางได้ (ยังไม่ลงบัญชี)
        Assert.True(ReceiptLinkPolicy.Decide(Rc(related: Guid.NewGuid()), Inv(), Ctx()).Allowed);
    }

    [Fact]
    public void Decide_EveryRefusalHasWayForward()
    {
        var cases = new (ReceiptLinkSourceFacts R, ReceiptLinkInvoiceFacts I, ReceiptLinkContext C)[]
        {
            (Rc(type: DocumentType.Quotation), Inv(), Ctx()),
            (Rc(DocumentStatus.Voided), Inv(), Ctx()),
            (Rc(), Inv(type: DocumentType.PurchaseInvoice), Ctx()),
            (Rc(), Inv(DocumentStatus.Voided), Ctx()),
            (Rc(), Inv(DocumentStatus.Draft), Ctx()),
            (Rc(), Inv(contact: B), Ctx()),
            (Rc(), Inv(contact: B), Ctx(branch: true)),
            (Rc(), Inv(currency: "USD"), Ctx()),
            (Rc(), Inv(servesAsReceipt: true), Ctx()),
            (Rc(), Inv(balance: 0m), Ctx()),
            (Rc(related: InvId), Inv(), Ctx()),
            (Rc(total: 2_000m), Inv(), Ctx(2_000m)),
            (Rc(total: 500m), Inv(type: DocumentType.Invoice, vat: 70m, balance: 1_070m), Ctx(500m)),
            (Rc(DocumentStatus.Draft, deposit: true), Inv(), Ctx(400m, 400m)),
            (Rc(DocumentStatus.Paid, deposit: true), Inv(), Ctx(400m, 0m)),
            (Rc(DocumentStatus.Paid, deposit: true), Inv(), Ctx(5_000m, 5_000m)),
            (Rc(DocumentStatus.Paid, deposit: true), Inv(), new ReceiptLinkContext(400m, false, null, null, 400m, "มัดจำนี้ออกใบกำกับแล้ว")),
            (BareCredit, Inv(), new ReceiptLinkContext(600m, false, "เงินก้อนนี้ต้องตรวจ", "CC-LEGACY-REVIEW", 600m)),
            (BareCredit, Inv(), Ctx(900m, 600m)),
            (Rc(DocumentStatus.WaitingApproval), Inv(), Ctx()),
            (Rc(DocumentStatus.Paid, fromCredit: true), Inv(), Ctx()),
            (Rc(contact: Guid.Empty), Inv(contact: Guid.Empty), Ctx()),
            (Rc(DocumentStatus.Paid, settlement: true, related: Guid.NewGuid()), Inv(), Ctx()),
            (Rc(DocumentStatus.Paid, related: Guid.NewGuid()), Inv(), Ctx()),
            (Rc(DocumentStatus.Paid), Inv(), Ctx()),
        };
        var codes = new HashSet<string>();
        foreach (var (r, i, c) in cases)
        {
            var v = ReceiptLinkPolicy.Decide(r, i, c);
            Assert.False(v.Allowed, $"{r.Type}/{r.Status} → {i.Type}/{i.Status} ควรถูกปฏิเสธ");
            Assert.False(string.IsNullOrWhiteSpace(v.Message));
            Assert.False(string.IsNullOrWhiteSpace(v.WayForward), $"{v.RuleCode} ไม่มีทางไปต่อ");
            codes.Add(v.RuleCode);
        }
        Assert.True(codes.Count >= 15, "ทุกเหตุปฏิเสธมีรหัสของตัวเอง");
    }

    // ───────── G-1 / G-2 / พรีวิว ─────────

    [Fact]
    public void SourceRefusal_OtherContactRefused_SameContactAndWalkInPass()
    {
        Assert.NotNull(ReceiptLinkPolicy.SourceRefusal(DocumentType.Receipt, B, "THB", DocumentType.Invoice, "INV001", A, "THB", true, false));
        Assert.Contains("สาขา", ReceiptLinkPolicy.SourceRefusal(DocumentType.Receipt, B, "THB", DocumentType.Invoice, "INV001", A, "THB", true, true));
        Assert.Null(ReceiptLinkPolicy.SourceRefusal(DocumentType.Receipt, A, "THB", DocumentType.Invoice, "INV001", A, "THB", true, false));
        // ใบแปลงของลูกค้าทั่วไป (ทั้งสองใบไม่มีผู้ติดต่อ) คงพฤติกรรมเดิม
        Assert.Null(ReceiptLinkPolicy.SourceRefusal(DocumentType.Receipt, Guid.Empty, "THB", DocumentType.Invoice, "INV001", Guid.Empty, "THB", true, false));
        // ชนิดที่แปลงเป็นใบเสร็จไม่ได้ · สกุลต่าง (เว้นใบเสร็จของการชำระ)
        Assert.NotNull(ReceiptLinkPolicy.SourceRefusal(DocumentType.Receipt, A, "THB", DocumentType.PurchaseInvoice, "PI1", A, "THB", false, false));
        Assert.NotNull(ReceiptLinkPolicy.SourceRefusal(DocumentType.Receipt, A, "THB", DocumentType.Invoice, "INV001", A, "USD", true, false));
        Assert.Null(ReceiptLinkPolicy.SourceRefusal(DocumentType.Receipt, A, "THB", DocumentType.Invoice, "INV001", A, "USD", true, false, checkCurrency: false));
        // ไม่ใช่ใบเสร็จ = ไม่ใช่เรื่องของด่านนี้ (ใบลดหนี้มีด่านของตัวเอง)
        Assert.Null(ReceiptLinkPolicy.SourceRefusal(DocumentType.CreditNote, B, "THB", DocumentType.TaxInvoice, "TIV1", A, "THB", false, false));
    }

    [Fact]
    public void SourceStateRefusal_ที่สร้างแก้ไขอนุมัติ_ใบต้นทางยกเลิกร่างขายสดบางส่วนVat_ปฏิเสธ_ทิศตรงข้ามใบเสนอราคาและเต็มยอดผ่าน()
    {
        // ฝ่ายค้าน M2: ด่านฝั่งใบปลายทางของปุ่มผูก ต้องตรวจซ้ำตอนสร้าง/แก้ไข/อนุมัติใบเสร็จที่สร้างเอง
        Assert.Equal(ReceiptLinkPolicy.VoidedCode, ReceiptLinkPolicy.SourceStateRefusal(Inv(DocumentStatus.Voided), 1_000m)?.Code);
        Assert.Equal(ReceiptLinkPolicy.DraftTargetCode, ReceiptLinkPolicy.SourceStateRefusal(Inv(DocumentStatus.Draft), null)?.Code);
        Assert.Equal(ReceiptLinkPolicy.ServedAsReceiptCode, ReceiptLinkPolicy.SourceStateRefusal(Inv(servesAsReceipt: true), null)?.Code);
        var replaced = new ReceiptLinkInvoiceFacts(InvId, DocumentType.TaxInvoice, DocumentStatus.Approved, false, true, A, "THB", 1m, 1_000m, 0m, false, "INV001");
        Assert.Equal(ReceiptLinkPolicy.VoidedCode, ReceiptLinkPolicy.SourceStateRefusal(replaced, null)?.Code);
        // รับบางส่วนของใบแจ้งหนี้ที่มี VAT: ตรวจเฉพาะเมื่อส่งยอดมา (ตอนอนุมัติ) — ผูกเต็มยอดแล้วแก้ยอดลงก่อนอนุมัติ = ปฏิเสธ
        var vatInvoice = Inv(type: DocumentType.Invoice, vat: 70m, balance: 1_070m);
        Assert.Equal(ReceiptLinkPolicy.PartialVatCode, ReceiptLinkPolicy.SourceStateRefusal(vatInvoice, 500m)?.Code);
        Assert.Null(ReceiptLinkPolicy.SourceStateRefusal(vatInvoice, null));          // ตอนสร้าง/แก้ไข ยอดร่างยังแก้ได้
        // ทิศตรงข้าม: เต็มยอด · ใบกำกับรับบางส่วน · ใบที่ชำระแล้ว (ใบแปลงของใบที่ปิดแล้ว) · ใบเสนอราคา/ใบวางบิลของใบที่แปลงมา = ไม่ใช่เรื่องของด่านนี้
        Assert.Null(ReceiptLinkPolicy.SourceStateRefusal(vatInvoice, 1_070m));
        Assert.Null(ReceiptLinkPolicy.SourceStateRefusal(Inv(vat: 70m, balance: 1_070m), 500m));
        Assert.Null(ReceiptLinkPolicy.SourceStateRefusal(Inv(DocumentStatus.Paid, balance: 0m), 1_000m));
        Assert.Null(ReceiptLinkPolicy.SourceStateRefusal(Inv(DocumentStatus.Draft, type: DocumentType.Quotation), 1_000m));
        Assert.Null(ReceiptLinkPolicy.SourceStateRefusal(Inv(DocumentStatus.Voided, type: DocumentType.BillingNote), 1_000m));
    }

    [Fact]
    public void OffersLinkButton_ตัดสินจากข้อมูลบนใบ_ใบค้างและใบร่างและมัดจำ_ไม่ใช่ทุกเอกสาร()
    {
        // ฝ่ายค้าน M6: ปุ่มไม่เรียกผู้สมัครตอนเปิดเอกสาร — ธงจากข้อมูลบนใบล้วน
        static bool Offers(ReceiptLinkSourceFacts r, ReceiptLinkInvoiceFacts i) => ReceiptLinkPolicy.OffersLinkButton(r, i);
        ReceiptLinkInvoiceFacts AsInv(ReceiptLinkSourceFacts r) => new(InvId, r.Type, r.Status, r.IsDeleted, r.IsReplaced, r.ContactId, r.Currency, 1m, 0m, 0m,
            r.Type == DocumentType.TaxInvoice && r.IssuedAsCashReceipt, r.Number);
        var openInvoice = Rc(DocumentStatus.Approved, type: DocumentType.TaxInvoice);
        Assert.True(Offers(openInvoice, Inv()));
        Assert.False(Offers(openInvoice, Inv(balance: 0m)));                                   // ชำระครบ
        Assert.False(Offers(openInvoice, Inv(DocumentStatus.Draft)));                          // ยังไม่อนุมัติ
        Assert.False(Offers(openInvoice, Inv(contact: Guid.Empty)));                           // ลูกค้าทั่วไป (L3)
        Assert.True(Offers(Rc(), AsInv(Rc())));                                                // ใบเสร็จร่าง
        var dep = Rc(DocumentStatus.Paid, deposit: true);
        Assert.True(Offers(dep, AsInv(dep)));
        foreach (var no in new[] { Rc(DocumentStatus.Paid), Rc(DocumentStatus.Paid, settlement: true, related: Guid.NewGuid()),
                     Rc(DocumentStatus.WaitingApproval), Rc(DocumentStatus.Draft, deposit: true), Rc(type: DocumentType.PurchaseInvoice) })
            Assert.False(Offers(no, AsInv(no)), $"{no.Type}/{no.Status} ไม่ควรมีปุ่ม");
    }

    [Fact]
    public void G2_ยอดประจำของลูกค้าไม่เตือน_ยอดไม่ประจำเตือน()
    {
        // ฝ่ายค้าน L8 (266-1-21): ค่าบริการรายเดือนยอดคงที่ — เตือนทุกเดือน = คำเตือนที่ฟ้องใบถูกทุกใบ
        Assert.True(ReceiptLinkPolicy.ShouldWarnDuplicateCashSale(1, recurringAmountForContact: false));
        Assert.False(ReceiptLinkPolicy.ShouldWarnDuplicateCashSale(1, recurringAmountForContact: true));
        Assert.False(ReceiptLinkPolicy.ShouldWarnDuplicateCashSale(0, recurringAmountForContact: false));
    }

    [Fact]
    public void DuplicateCashSaleWarning_HasPrefixAndNumbers()
    {
        var w = ReceiptLinkPolicy.DuplicateCashSaleWarning(new[] { "RC-001" }, 2_140m);
        Assert.StartsWith("[" + ReceiptLinkPolicy.DuplicateWarningCode + "]", w, StringComparison.Ordinal);
        Assert.Contains("RC-001", w, StringComparison.Ordinal);
        Assert.Contains("2,140.00", w, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewToken_ChangesWhenFactsChange_StableOtherwise()
    {
        var id = Guid.NewGuid();
        var t1 = ReceiptLinkPolicy.PreviewToken(ReceiptLinkRoute.SetDraftSource, id, Rc(), Inv(), 1_000m, null);
        Assert.Equal(t1, ReceiptLinkPolicy.PreviewToken(ReceiptLinkRoute.SetDraftSource, id, Rc(), Inv(), 1_000m, null));
        Assert.NotEqual(t1, ReceiptLinkPolicy.PreviewToken(ReceiptLinkRoute.SetDraftSource, id, Rc(), Inv(balance: 900m), 1_000m, null));
        Assert.NotEqual(t1, ReceiptLinkPolicy.PreviewToken(ReceiptLinkRoute.SetDraftSource, id, Rc(related: InvId), Inv(), 1_000m, null));
        Assert.NotEqual(t1, ReceiptLinkPolicy.PreviewToken(ReceiptLinkRoute.AllocateCredit, id, Rc(), Inv(), 1_000m, 1_000m));
    }

    [Fact]
    public void Effects_ServerComputesBalanceAfter()
    {
        var e = ReceiptLinkPolicy.Effects(ReceiptLinkRoute.AllocateCredit, Rc(), Inv(balance: 1_000m), 600m, 0m);
        Assert.Contains(e, x => x.Detail.Contains("1,000.00 → 400.00", StringComparison.Ordinal));
        Assert.Contains(e, x => x.Detail.Contains("21630", StringComparison.Ordinal));
        // ฝ่ายค้าน L9: ไม่อ้างปุ่มที่ไม่มีบนหน้า · L6: ใบร่างที่ผูกใบอื่นอยู่ — พรีวิวบอกว่าจะเลิกอ้างใบเดิม
        Assert.DoesNotContain(e, x => x.Detail.Contains("ที่ปุ่มนี้", StringComparison.Ordinal));
        var over = ReceiptLinkPolicy.Effects(ReceiptLinkRoute.SetDraftSource, Rc(related: Guid.NewGuid(), previousSource: "QT-0007"), Inv(), 1_000m, null);
        Assert.Contains(over, x => x.Detail.Contains("QT-0007", StringComparison.Ordinal));
        Assert.DoesNotContain(ReceiptLinkPolicy.Effects(ReceiptLinkRoute.SetDraftSource, Rc(), Inv(), 1_000m, null), x => x.Label == "ใบต้นทางเดิม");
        Assert.Equal(1_070m, ReceiptLinkPolicy.DepositGrossOutstanding(1_000m, 1_070m, 1_000m));
        Assert.Equal(500m, ReceiptLinkPolicy.DepositGrossOutstanding(500m, 500m, 500m));
    }
}

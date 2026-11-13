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
        // รอบ 266 แพ็ก B: ใบเสร็จของการรับชำระที่ออกเลขแล้ว = มีปุ่ม (ย้ายไปใบที่ถูกได้) · ฉบับร่างของการรับชำระยังไม่ใช่กระดาษที่ลูกค้าถือ = ไม่มีปุ่ม
        var settled = Rc(DocumentStatus.Paid, settlement: true, related: Guid.NewGuid());
        Assert.True(Offers(settled, AsInv(settled)));
        foreach (var no in new[] { Rc(DocumentStatus.Paid), Rc(DocumentStatus.Draft, settlement: true, related: Guid.NewGuid()),
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

    // ───────── รอบ 266 แพ็ก B: ย้ายการรับชำระระหว่างใบ (คำตัดสิน 266-1-23…30) ─────────

    private static readonly Guid FromId = Guid.NewGuid();

    /// <summary>การรับชำระ 1,000 บาทที่ตัดใบ INV002 (ผิดใบ) มีใบเสร็จ REC003 — ทุกธงอยู่ในสภาพ "ย้ายได้"</summary>
    private static ReceiptLinkMoveFacts Mv(decimal amount = 1_000m, string? receipt = "REC003") => new(
        PaymentNumber: "PAY-202611-0007", PaymentDate: new DateTime(2026, 11, 3), Amount: amount, FromDocumentId: FromId, FromDocumentNumber: "INV002",
        FromBalanceDue: 0m, PaymentVoided: false, IsThb: true, HasWithholdingTax: false, HasFeeOrAdjustment: false, FromSettlementBatch: false,
        FromGateway: false, HasUnappliedCredit: false, LiveAllocationDocuments: 0, FromLateAllocation: false, PartnerLinked: false,
        LiveReceiptNumber: receipt, ReceiptEtaxReachedRd: false, KeptOriginalReceiptNumber: null, SourceVatLockReason: null,
        ReplacementReceiptBlock: null, ApprovalControlProblem: null, JournalProblem: null, JournalDate: new DateTime(2026, 11, 3),
        ClosedPeriodNote: null, ReplacementCarriesVat: false);

    private static ReceiptLinkSourceFacts SettlementReceipt => Rc(DocumentStatus.Paid, settlement: true, related: FromId);

    private static ReceiptLinkVerdict Move(ReceiptLinkMoveFacts? m, ReceiptLinkInvoiceFacts? inv = null)
        => ReceiptLinkPolicy.Decide(SettlementReceipt, inv ?? Inv(), new ReceiptLinkContext(m?.Amount ?? 1_000m, Move: m));

    [Fact]
    public void Move_AllowsSameContactOpenInvoice_ReissueWhenReceiptExists_PlainWhenNot()
    {
        var withReceipt = Move(Mv());
        Assert.Equal((true, ReceiptLinkRoute.MoveSettlementWithReissue), (withReceipt.Allowed, withReceipt.Route));
        var noReceipt = Move(Mv(receipt: null));
        Assert.Equal((true, ReceiptLinkRoute.MoveSettlement), (noReceipt.Allowed, noReceipt.Route));
        // ยอดน้อยกว่ายอดค้างของใบกำกับสินค้า (ไม่มีภาษีพัก) = รับบางส่วนได้
        Assert.True(Move(Mv(400m)).Allowed);
        Assert.True(ReceiptLinkPolicy.RequiresReason(withReceipt.Route));
        Assert.False(ReceiptLinkPolicy.RequiresReason(ReceiptLinkRoute.AllocateCredit));
    }

    [Fact]
    public void Move_RefusesWithoutPaymentFacts_ไม่รู้ไม่ย้าย()
    {
        var v = Move(null);
        Assert.Equal((false, ReceiptLinkPolicy.MoveNoPaymentCode), (v.Allowed, v.RuleCode));
        Assert.False(string.IsNullOrWhiteSpace(v.WayForward));
    }

    [Fact]
    public void Move_EveryGateRefusesWithWayForward_AndOppositeDirectionAllowed()
    {
        var gates = new (string Code, ReceiptLinkMoveFacts Facts, ReceiptLinkInvoiceFacts Target)[]
        {
            (ReceiptLinkPolicy.VoidedCode, Mv() with { PaymentVoided = true }, Inv()),
            (ReceiptLinkPolicy.AlreadyLinkedCode, Mv() with { FromDocumentId = InvId }, Inv()),
            (ReceiptLinkPolicy.CurrencyCode, Mv() with { IsThb = false }, Inv()),
            (ReceiptLinkPolicy.MoveSourceCode, Mv() with { FromSettlementBatch = true }, Inv()),
            (ReceiptLinkPolicy.MoveSourceCode, Mv() with { FromGateway = true }, Inv()),
            (ReceiptLinkPolicy.CombinedCode, Mv() with { HasUnappliedCredit = true }, Inv()),
            (ReceiptLinkPolicy.CombinedCode, Mv() with { LiveAllocationDocuments = 2 }, Inv()),
            (ReceiptLinkPolicy.MoveLateAllocationCode, Mv() with { LiveAllocationDocuments = 1, FromLateAllocation = true }, Inv()),
            (ReceiptLinkPolicy.MoveWhtCode, Mv() with { HasWithholdingTax = true }, Inv()),
            (ReceiptLinkPolicy.MoveFeeCode, Mv() with { HasFeeOrAdjustment = true }, Inv()),
            (ReceiptLinkPolicy.PartnerCode, Mv() with { PartnerLinked = true }, Inv()),
            (ReceiptLinkPolicy.EtaxSentCode, Mv() with { ReceiptEtaxReachedRd = true }, Inv()),
            (ReceiptLinkPolicy.EtaxSentCode, Mv() with { KeptOriginalReceiptNumber = "REC001" }, Inv()),
            (ReceiptLinkPolicy.VatFiledCode, Mv() with { SourceVatLockReason = "ภ.พ.30 เดือน 10/2026 ที่ยื่นแล้ว" }, Inv()),
            (ReceiptLinkPolicy.OverpayCode, Mv(1_500m), Inv()),
            (ReceiptLinkPolicy.PartialVatCode, Mv(400m), Inv(type: DocumentType.Invoice, vat: 70m)),
            (ReceiptLinkPolicy.MoveReceiptCode, Mv() with { ReplacementReceiptBlock = "ผู้กดไม่มีสิทธิ์อนุมัติใบเสร็จรับเงิน" }, Inv()),
            (ReceiptLinkPolicy.MoveApprovalCode, Mv() with { ApprovalControlProblem = "บริษัทเปิดการแบ่งแยกหน้าที่" }, Inv()),
            (ReceiptLinkPolicy.MoveJournalCode, Mv() with { JournalProblem = "ไม่พบรายการบัญชีตั้งลูกหนี้" }, Inv()),
            // ฝ่ายค้าน B-2: สิทธิ์ของเอกสารตัดสินจากข้อเท็จจริง (ใต้ล็อก) · B-1: ใบเดิมเป็นใบกำกับภาษี/ใบเสร็จรับเงินในตัวของเงินก้อนนี้
            (ReceiptLinkPolicy.MovePermissionCode, Mv() with { PermissionProblem = "ผู้กดไม่มีสิทธิ์ยกเลิกใบกำกับภาษี" }, Inv()),
            (ReceiptLinkPolicy.ServedAsReceiptCode, Mv(receipt: null) with { FromServedAsReceipt = true }, Inv()),
        };
        foreach (var g in gates)
        {
            var v = Move(g.Facts, g.Target);
            Assert.False(v.Allowed, g.Code);
            Assert.Equal(g.Code, v.RuleCode);
            Assert.False(string.IsNullOrWhiteSpace(v.Message), g.Code);
            Assert.False(string.IsNullOrWhiteSpace(v.WayForward), g.Code + " ต้องมีทางไปต่อ");
        }
        // ทิศตรงข้าม: ธงทุกตัวปกติ = ย้ายได้ · ใบแจ้งหนี้บริการ (มีภาษีพัก) ย้ายเต็มยอดค้างได้
        Assert.True(Move(Mv()).Allowed);
        Assert.True(Move(Mv(1_070m), Inv(type: DocumentType.Invoice, vat: 70m, balance: 1_070m)).Allowed);
    }

    [Fact]
    public void Move_TargetAndContactGatesStillApply()
    {
        // ด่านเจ้าเดียวกัน/สกุลเงิน/สถานะใบปลายทางของแพ็ก A ครอบเส้นย้ายด้วย (ตัวตัดสินเดียว)
        Assert.Equal(ReceiptLinkPolicy.ContactCode, Move(Mv(), Inv(contact: B)).RuleCode);
        Assert.Equal(ReceiptLinkPolicy.CurrencyCode, Move(Mv(), Inv(currency: "USD")).RuleCode);
        Assert.Equal(ReceiptLinkPolicy.DraftTargetCode, Move(Mv(), Inv(DocumentStatus.Draft)).RuleCode);
        Assert.Equal(ReceiptLinkPolicy.ServedAsReceiptCode, Move(Mv(), Inv(servesAsReceipt: true)).RuleCode);
        Assert.Equal(ReceiptLinkPolicy.AlreadyPaidCode, Move(Mv(), Inv(balance: 0m)).RuleCode);
    }

    [Fact]
    public void ManualReceiptOfOtherInvoice_StaysRefusedWithWayForward()
    {
        // 266-1-28: ใบเสร็จที่สร้างเอง (สมุดรายวันของตัวเอง) ไม่มีการรับชำระให้ย้าย — แม้ส่งข้อเท็จจริงของการย้ายมาก็ไม่เปิดเส้น
        var v = ReceiptLinkPolicy.Decide(Rc(DocumentStatus.Paid, related: FromId), Inv(), new ReceiptLinkContext(1_000m, Move: Mv()));
        Assert.Equal((false, ReceiptLinkPolicy.MoveNotYetCode), (v.Allowed, v.RuleCode));
        Assert.Contains("ยกเลิกใบเสร็จนี้", v.WayForward, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveEffects_PreviewTellsBothBalances_JournalThrough21630_BankUntouched_Paper_Dunning()
    {
        var reissue = ReceiptLinkPolicy.MoveEffects(ReceiptLinkRoute.MoveSettlementWithReissue, Mv(), Inv());
        string Detail(string label) => reissue.Single(x => x.Label == label).Detail;
        Assert.Contains("1,000.00 → 0.00", Detail("ยอดค้างของ INV001"), StringComparison.Ordinal);
        Assert.Contains("0.00 → 1,000.00", Detail("ยอดค้างของ INV002"), StringComparison.Ordinal);
        Assert.Contains("21630", Detail("รายการบัญชี"), StringComparison.Ordinal);
        Assert.Contains("03/11/2026", Detail("รายการบัญชี"), StringComparison.Ordinal);
        Assert.Contains("ไม่ถูกแตะ", Detail("เงินสด/ธนาคาร"), StringComparison.Ordinal);
        Assert.Contains("ออกแทนใบเลขที่ REC003", Detail("กระดาษ"), StringComparison.Ordinal);
        Assert.Contains("พักหนังสือทวง", Detail("การทวงเงิน"), StringComparison.Ordinal);
        Assert.Contains("ไม่มีปุ่มถอด", Detail("ยกเลิกภายหลัง"), StringComparison.Ordinal);
        // ไม่มีใบเสร็จแยก: ไม่อ้างว่าจะออกใบแทน · บอกว่าย้ายกลับได้ · งวดปิด = บอกว่าลงวันที่ทำรายการ
        var plain = ReceiptLinkPolicy.MoveEffects(ReceiptLinkRoute.MoveSettlement,
            Mv(receipt: null) with { ClosedPeriodNote = "งวดบัญชีของวันที่ 03/11/2026 ปิดแล้ว — ลงวันที่ทำรายการ" }, Inv());
        Assert.DoesNotContain("ออกแทน", plain.Single(x => x.Label == "กระดาษ").Detail, StringComparison.Ordinal);
        Assert.Contains("ย้ายกลับใบเดิมได้", plain.Single(x => x.Label == "ยกเลิกภายหลัง").Detail, StringComparison.Ordinal);
        Assert.Contains("ปิดแล้ว", plain.Single(x => x.Label == "รายการบัญชี").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Move_FromSelfReceiptTaxInvoice_Refused_WithWayForward_OppositeAllowed()
    {
        // ฝ่ายค้าน B-1: ใบกำกับภาษีที่เป็นใบเสร็จในตัวของการรับเงินก้อนนี้ (ไม่มี REC แยก) — ย้ายออก = หัวกระดาษที่ลูกค้าถือเปลี่ยนย้อนหลัง ⇒ ปฏิเสธพร้อมทางไปต่อ
        var refused = Move(Mv(receipt: null) with { FromServedAsReceipt = true });
        Assert.Equal((false, ReceiptLinkPolicy.ServedAsReceiptCode), (refused.Allowed, refused.RuleCode));
        Assert.Contains("ยกเลิกการรับชำระ PAY-202611-0007", refused.WayForward, StringComparison.Ordinal);
        Assert.Contains("INV002", refused.Message, StringComparison.Ordinal);
        // ทิศตรงข้าม: ใบเดิมไม่ได้เป็นใบรับของเงินก้อนนี้ (มี REC แยก/รับคนละวัน) = ย้ายได้ตามเดิม
        Assert.True(Move(Mv(receipt: null)).Allowed);
        Assert.True(Move(Mv()).Allowed);
    }

    [Fact]
    public void MoveEffects_PaperLineMatchesWhatTheMoveDoes()
    {
        string Paper(ReceiptLinkRoute route, ReceiptLinkMoveFacts m) => ReceiptLinkPolicy.MoveEffects(route, m, Inv()).Single(x => x.Label == "กระดาษ").Detail;
        string Undo(ReceiptLinkRoute route, ReceiptLinkMoveFacts m) => ReceiptLinkPolicy.MoveEffects(route, m, Inv()).Single(x => x.Label == "ยกเลิกภายหลัง").Detail;
        // ไม่มีใบเสร็จแยก + ใบปลายทางถือ VAT ณ วันรับเงิน ⇒ ระบบออกใบใหม่ (ไม่ใช่ "ไม่มีกระดาษ") · ถอดไม่ได้
        var issue = Mv(receipt: null) with { IssueReceiptForTarget = true, ReplacementCarriesVat = true };
        Assert.Contains("ออกใบเสร็จรับเงิน/ใบกำกับภาษีใบใหม่ให้ INV001", Paper(ReceiptLinkRoute.MoveSettlement, issue), StringComparison.Ordinal);
        Assert.Contains("ไม่มีปุ่มถอด", Undo(ReceiptLinkRoute.MoveSettlement, issue), StringComparison.Ordinal);
        // ใบปลายทางจะเป็นใบรับในตัว ⇒ บอกว่าหัวของใบนั้นเปลี่ยน
        var selfAfter = Mv(receipt: null) with { TargetServesAfterMove = true };
        Assert.Contains("ใบกำกับภาษี/ใบเสร็จรับเงิน", Paper(ReceiptLinkRoute.MoveSettlement, selfAfter), StringComparison.Ordinal);
        Assert.Contains("ไม่มีปุ่มถอด", Undo(ReceiptLinkRoute.MoveSettlement, selfAfter), StringComparison.Ordinal);
        // มีใบเสร็จเดิม + ใบปลายทางเป็นใบรับในตัว ⇒ ยกเลิกโดยไม่ออกใบแทน (ไม่อ้าง "ออกแทนใบเลขที่")
        var voidOnly = Mv() with { TargetServesAfterMove = true };
        Assert.Contains("โดยไม่ออกใบเสร็จแยกใบใหม่", Paper(ReceiptLinkRoute.MoveSettlementWithReissue, voidOnly), StringComparison.Ordinal);
        Assert.DoesNotContain("ออกแทนใบเลขที่", Paper(ReceiptLinkRoute.MoveSettlementWithReissue, voidOnly), StringComparison.Ordinal);
        // ทิศตรงข้าม: ไม่มีกระดาษจริง ⇒ พูดว่าไม่มี + ถอดได้
        Assert.Contains("ไม่มีกระดาษใดถูกแก้", Paper(ReceiptLinkRoute.MoveSettlement, Mv(receipt: null)), StringComparison.Ordinal);
        Assert.Contains("ย้ายกลับใบเดิมได้", Undo(ReceiptLinkRoute.MoveSettlement, Mv(receipt: null)), StringComparison.Ordinal);
        // ฝ่ายค้าน B-4: พรีวิวบอกว่าค่าปรับล่าช้านับจากวันที่ย้าย
        Assert.Contains("ค่าปรับล่าช้า", ReceiptLinkPolicy.MoveEffects(ReceiptLinkRoute.MoveSettlement, Mv(receipt: null), Inv()).Single(x => x.Label == "การทวงเงิน").Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LateFeeDaysOverdue_CountsFromMoveDate_UnmovedUnchanged()
    {
        var today = new DateTime(2026, 11, 14);
        var due = new DateTime(2026, 8, 31);
        // ไม่เคยย้าย = สูตรเดิม (วันนี้ − ครบกำหนด − ผ่อนผัน)
        Assert.Equal((int)(today - due).TotalDays - 3, ReceiptLinkPolicy.LateFeeDaysOverdue(today, due, null, 3));
        // ฝ่ายค้าน B-4: เงินถูกย้ายออก 13 พ.ย. ⇒ นับจากวันที่ย้าย (ไม่ใช่ 74 วันย้อนหลัง) · ผ่อนผัน 0 ⇒ 1 วัน · ผ่อนผัน 3 ⇒ ยังไม่คิด
        Assert.Equal(1, ReceiptLinkPolicy.LateFeeDaysOverdue(today, due, new DateTime(2026, 11, 13, 9, 30, 0), 0));
        Assert.True(ReceiptLinkPolicy.LateFeeDaysOverdue(today, due, new DateTime(2026, 11, 13), 3) <= 0);
        // ย้ายก่อนครบกำหนด ⇒ ครบกำหนดเดิมยังเป็นตัวตั้ง
        Assert.Equal((int)(today - due).TotalDays, ReceiptLinkPolicy.LateFeeDaysOverdue(today, due, new DateTime(2026, 8, 1), 0));
    }

    [Fact]
    public void MoveUndoRefusal_NullWithoutPaper_RefusesWhenReceiptVoidedOrIssued()
    {
        Assert.Null(ReceiptLinkPolicy.MoveUndoRefusal(null, null));
        var voided = ReceiptLinkPolicy.MoveUndoRefusal("REC003", null);
        Assert.Contains("REC003", voided, StringComparison.Ordinal);
        Assert.Contains("ทางไปต่อ", voided, StringComparison.Ordinal);
        Assert.Contains("REC009", ReceiptLinkPolicy.MoveUndoRefusal(null, "REC009"), StringComparison.Ordinal);
        Assert.Contains("REC009", ReceiptLinkPolicy.MoveUndoRefusal("REC003", "REC009"), StringComparison.Ordinal);
    }

    [Fact]
    public void MoveJournalReference_StartsWithPaymentNumberSlash_SoPaymentVoidReversesBothLegs()
    {
        Assert.Equal("PAY-202611-0007/M1-OUT", ReceiptLinkPolicy.MoveJournalReference("PAY-202611-0007", 1, outLeg: true));
        Assert.Equal("PAY-202611-0007/M2-IN", ReceiptLinkPolicy.MoveJournalReference("PAY-202611-0007", 2, outLeg: false));
        Assert.StartsWith("PAY-202611-0007/", ReceiptLinkPolicy.MoveJournalReference("PAY-202611-0007", 3, outLeg: true), StringComparison.Ordinal);
    }

    [Fact]
    public void PaymentEffectiveDocument_MovedWins_MoveBackClearsPointer()
    {
        var original = Guid.NewGuid();
        var target = Guid.NewGuid();
        Assert.Equal(original, PaymentEffectiveDocument.Of(original, null));          // ไม่เคยย้าย = ใบเดิม (ข้อมูลก่อนรอบ 266 ทุกแถว)
        Assert.Equal(target, PaymentEffectiveDocument.Of(original, target));
        Assert.Equal((Guid?)target, PaymentEffectiveDocument.Stamp(original, target));
        Assert.Null(PaymentEffectiveDocument.Stamp(original, original));              // ย้ายกลับใบเดิม = ไม่มีตัวชี้ค้าง
    }

    [Fact]
    public void ReplacementLine_SameTextForBothRenderers_ThaiAndEnglish_NullWhenNotAReplacement()
    {
        var th = Accounting.Services.Implementations.Pdf.DocumentLabels.For("th");
        var en = Accounting.Services.Implementations.Pdf.DocumentLabels.For("en");
        var doc = new Accounting.Models.Entities.Document { ReplacementOfNumber = "REC003", ReplacementOfDate = new DateTime(2026, 11, 3) };
        Assert.Equal("ออกแทนใบเลขที่ REC003 ลงวันที่ 03/11/2026 (ใบเดิมถูกยกเลิก)",
            Accounting.Services.Implementations.PdfGenerationService.ReplacementReferenceText(doc, th));
        Assert.Equal("Issued in replacement of No. REC003 dated 03 Nov 2026 (original cancelled)",
            Accounting.Services.Implementations.PdfGenerationService.ReplacementReferenceText(doc, en));
        // ทิศตรงข้าม: ใบที่ไม่ได้ออกแทนใคร ไม่มีบรรทัดนี้ (กระดาษของใบอื่นไม่เปลี่ยน)
        Assert.Null(Accounting.Services.Implementations.PdfGenerationService.ReplacementReferenceText(new Accounting.Models.Entities.Document(), th));
    }
}

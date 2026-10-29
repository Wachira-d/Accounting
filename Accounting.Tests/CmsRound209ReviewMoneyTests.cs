using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 209 ฝ่ายค้าน (เงิน) — ใบลดหนี้ของออเดอร์ที่หักมัดจำ (ข้อ 1) · คืนบางส่วนแล้วย้อนสถานะ/ยกเลิก (ข้อ 2) · เก็บเงินปลายทางส่งมอบก่อนมีใบขาย (ข้อ 3) ·
/// ตัวหาผู้ติดต่อเมื่อขอใบกำกับพร้อมเลขภาษี (ข้อ 5) · ลดหนี้ใบขายของบัตรคอร์สที่โอนแล้ว (ข้อ 6) · ข้อความคืนสต็อกที่ไม่มีผล (ข้อ 7) ·
/// ทุกกลุ่มล็อกทั้ง "เคสที่พังกลับมาถูก" และ "เคสเดิมที่ถูกอยู่แล้วไม่ถูกแตะ" (ข้อ 4 สาขาใบส่วนที่เหลือ ล็อกด้วย required_call_site_check)
/// </summary>
public class CmsRound209ReviewMoneyTests
{
    private static readonly Guid Co = Guid.NewGuid();

    // ═════════ ข้อ 1: ใบลดหนี้ของใบส่วนที่เหลือ (บรรทัดเต็มราคา + หักฐานมัดจำ) ═════════
    // ออเดอร์ 10,700 (รวม VAT 7%) · มัดจำ 3,210 (ฐาน 3,000) ⇒ ใบส่วนที่เหลือ = บรรทัด 10,700 − ฐานมัดจำ 3,000 = 7,000 + VAT 490 = 7,490

    private static readonly RefundSourceLine Cake = new("เค้กงานแต่ง", 1m, "ชิ้น", 10700m, 0m, 0m, 7m, null, null);
    private const decimal DepositBase = 3000m;

    private static decimal CreditNoteTotal(RefundCreditPlan p)
    {
        var lines = p.Lines.Select(l => new DocumentLineRequest(Description: l.Description, Quantity: l.Quantity, Unit: l.Unit,
            UnitPrice: l.UnitPrice, DiscountPercent: l.DiscountPercent, VatRate: l.VatRate, WithholdingTaxRate: 0m, AccountId: l.AccountId,
            ProductCode: l.ProductCode, DiscountAmount: l.DiscountAmount)).ToList();
        return DocumentService.PreviewTotals(lines, true, p.BillDiscount).Total;
    }

    [Fact]
    public void Remainder_document_total_is_7490()
    {
        var lines = new List<DocumentLineRequest>
            { new(Description: "เค้กงานแต่ง", Quantity: 1m, Unit: "ชิ้น", UnitPrice: 10700m, DiscountPercent: 0m, VatRate: 7m, WithholdingTaxRate: 0m, AccountId: null) };
        Assert.Equal(7490m, DocumentService.PreviewTotals(lines, true, 0m, DepositBase).Total);
    }

    [Fact]
    public void Partial_refund_on_deposit_deducted_invoice_equals_refund_amount()
    {
        var p = SiteOrderRefundPlan.Plan(new[] { Cake }, 7490m, 0m, Array.Empty<(string, decimal)>(), false,
            alreadyCredited: 0m, paid: 10700m, requestedAmount: 1070m, fullRefund: false, depositBaseDeducted: DepositBase);
        Assert.Null(p.Problem);
        Assert.Equal(1070m, p.Amount);
        Assert.Equal(1528.57m, p.Lines[0].UnitPrice);
        Assert.Equal(428.57m, p.BillDiscount);   // ฐานมัดจำตามสัดส่วน ⇒ ส่วนลดท้ายบิลของใบลดหนี้
        Assert.Equal(1070.00m, CreditNoteTotal(p));   // เดิม 1,528.57
    }

    [Fact]
    public void Full_refund_on_deposit_deducted_invoice_never_exceeds_invoice()
    {
        var p = SiteOrderRefundPlan.Plan(new[] { Cake }, 7490m, 0m, Array.Empty<(string, decimal)>(), false,
            alreadyCredited: 0m, paid: 10700m, requestedAmount: null, fullRefund: true, depositBaseDeducted: DepositBase);
        Assert.Null(p.Problem);
        Assert.Equal(7490m, p.Amount);
        Assert.Equal(1m, p.Ratio);
        Assert.Equal(DepositBase, p.BillDiscount);
        Assert.Equal(7490.00m, CreditNoteTotal(p));   // เดิม 10,700 > ใบเดิม (อนุมัติไม่ได้)
    }

    [Fact]
    public void Order_without_deposit_unchanged()
    {
        var full = SiteOrderRefundPlan.Plan(new[] { Cake }, 10700m, 0m, Array.Empty<(string, decimal)>(), false, 0m, 10700m, null, fullRefund: true);
        Assert.Equal(0m, full.BillDiscount);
        Assert.Equal(10700m, full.Lines[0].UnitPrice);
        Assert.Equal(10700.00m, CreditNoteTotal(full));
        var part = SiteOrderRefundPlan.Plan(new[] { Cake }, 10700m, 0m, Array.Empty<(string, decimal)>(), false, 0m, 10700m, 1070m, fullRefund: false);
        Assert.Equal(1070m, part.Lines[0].UnitPrice);
        Assert.Equal(1070.00m, CreditNoteTotal(part));
    }

    [Fact]
    public void Deposit_portion_note_points_to_deposit_receipt_only_when_deducted()
    {
        Assert.Contains("ใบรับเงินมัดจำ", SiteOrderRefundPlan.DepositPortionNote(3000m, "RCD-0001"));
        Assert.Contains("RCD-0001", SiteOrderRefundPlan.DepositPortionNote(3000m, "RCD-0001"));
        Assert.Equal("", SiteOrderRefundPlan.DepositPortionNote(0m, null));
    }

    // ═════════ ข้อ 2: คืนบางส่วนแล้ว ⇒ ไปได้แค่คืนเต็ม · ยกเลิกหักใบลดหนี้เดิม ═════════

    [Theory]
    [InlineData(SiteOrderStatus.Confirmed)]
    [InlineData(SiteOrderStatus.Processing)]
    [InlineData(SiteOrderStatus.ReadyForPickup)]
    [InlineData(SiteOrderStatus.Shipped)]
    [InlineData(SiteOrderStatus.Delivered)]
    [InlineData(SiteOrderStatus.Cancelled)]
    [InlineData(SiteOrderStatus.Pending)]
    public void Partially_refunded_cannot_go_back_to_work_stages(SiteOrderStatus to)
        => Assert.Equal(BookingTransitionResult.Rejected, SiteOrderStatusTransition.Check(SiteOrderStatus.PartiallyRefunded, to).Result);

    [Fact]
    public void Partially_refunded_to_refunded_still_allowed_and_same_is_noop()
    {
        Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(SiteOrderStatus.PartiallyRefunded, SiteOrderStatus.Refunded).Result);
        Assert.Equal(BookingTransitionResult.NoOp, SiteOrderStatusTransition.Check(SiteOrderStatus.PartiallyRefunded, SiteOrderStatus.PartiallyRefunded).Result);
        // ทิศตรงข้าม: ขั้นงานปกติยังเดินได้ (ไม่ถูกแตะ)
        Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(SiteOrderStatus.Processing, SiteOrderStatus.Confirmed).Result);
        Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(SiteOrderStatus.Delivered, SiteOrderStatus.PartiallyRefunded).Result);
    }

    [Fact]
    public void Partially_refunded_next_actions_offer_only_full_refund()
    {
        var acts = SiteOrderStatusTransition.NextActions(SiteOrderStatus.PartiallyRefunded, null, null);
        var a = Assert.Single(acts);
        Assert.Equal(nameof(SiteOrderStatus.Refunded), a.Status);
        Assert.Null(a.DisabledReason);
    }

    [Fact]
    public void Cancel_plan_subtracts_already_credited()
    {
        // ใบลดหนี้คืนบางส่วน 1,070 ออกแล้ว ⇒ ยกเลิก/คืนเต็มได้แค่ส่วนที่เหลือ (Σ ใบลดหนี้ ≤ ใบเดิม §86/10)
        var rest = SiteOrderRefundPlan.Plan(new[] { Cake }, 7490m, 0m, Array.Empty<(string, decimal)>(), false,
            alreadyCredited: 1070m, paid: 7490m, requestedAmount: null, fullRefund: true, depositBaseDeducted: DepositBase);
        Assert.Null(rest.Problem);
        Assert.Equal(6420m, rest.Amount);
        Assert.Equal(6420.00m, CreditNoteTotal(rest));
        var none = SiteOrderRefundPlan.Plan(new[] { Cake }, 7490m, 0m, Array.Empty<(string, decimal)>(), false,
            alreadyCredited: 7490m, paid: 7490m, requestedAmount: null, fullRefund: true, depositBaseDeducted: DepositBase);
        Assert.NotNull(none.Problem);
    }

    // ═════════ ข้อ 3: โหมดเดิมส่งมอบก่อนมีใบขาย (COD/ชำระเมื่อรับ) ⇒ ออกใบขาย + ตัดสต็อกตอนส่งมอบ ═════════

    [Theory]
    [InlineData(null)]
    [InlineData(DepositInvoicingMode.FullAtFirstPayment)]
    public void Legacy_mode_handover_without_sale_document_settles(string? mode)
    {
        Assert.True(SiteOrderDepositInvoicing.SettlesOnHandover(mode, SiteOrderStatus.Delivered, hasSalesDocument: false));
        Assert.True(SiteOrderDepositInvoicing.SettlesOnHandover(mode, SiteOrderStatus.Shipped, hasSalesDocument: false));
        // ทิศตรงข้าม: มีใบขายแล้ว (จ่ายก่อนส่ง) = พฤติกรรมเดิม · ยังไม่ส่งมอบ = ไม่ออกใบ
        Assert.False(SiteOrderDepositInvoicing.SettlesOnHandover(mode, SiteOrderStatus.Delivered, hasSalesDocument: true));
        Assert.False(SiteOrderDepositInvoicing.SettlesOnHandover(mode, SiteOrderStatus.Processing, hasSalesDocument: false));
        Assert.False(SiteOrderDepositInvoicing.SettlesOnHandover(mode, SiteOrderStatus.ReadyForPickup, hasSalesDocument: false));
    }

    [Fact]
    public void Deposit_mode_handover_unchanged()
    {
        Assert.True(SiteOrderDepositInvoicing.SettlesOnHandover(DepositInvoicingMode.DepositTaxInvoice, SiteOrderStatus.Delivered, hasSalesDocument: true));
        Assert.True(SiteOrderDepositInvoicing.SettlesOnHandover(DepositInvoicingMode.DepositTaxInvoice, SiteOrderStatus.Delivered, hasSalesDocument: false));
    }

    [Fact]
    public void Cod_pay_on_pickup_can_be_delivered_before_payment()
    {
        var due = MtoPaymentTerms.FromSnapshot(PaymentTermMode.PayOnPickup, 1070m, 0m, 0m);
        Assert.Equal(0m, due.DueNow);
        Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(SiteOrderStatus.Processing, SiteOrderStatus.Delivered, due).Result);
    }

    // ═════════ ข้อ 5: ขอใบกำกับพร้อมเลขภาษี ⇒ จับด้วยเลข+สาขาเท่านั้น ═════════

    private const string TaxA = "0105550123451";
    private const string TaxB = "0105550999993";

    private static Contact C(string name, string? email = null, string? phone = null, string? taxId = null, string? branch = null)
        => new() { Id = Guid.NewGuid(), CompanyId = Co, Name = name, Email = email, Phone = phone, TaxId = taxId, BranchCode = branch,
            IsActive = true, CreatedAt = DateTime.UtcNow.AddDays(-5) };

    private static CmsContactInput In(string? email = null, string? phone = null, string? taxId = null, string? branch = null,
        Guid? linked = null, bool explicitTax = false)
        => new(linked, taxId, branch, email, phone, "ลูกค้า", null, null, "test", BuyerTaxIdExplicit: explicitTax);

    [Fact]
    public void Strict_tax_key_does_not_fall_back_to_email_or_phone_nor_adopt_tax_id()
    {
        var byEmail = C("คนอื่น", email: "a@x.com");
        var byPhone = C("คนอื่น 2", phone: "0812345678");
        var d = CmsContactResolver.Decide(new[] { byEmail, byPhone },
            In(email: "a@x.com", phone: "081-234-5678", taxId: TaxA, branch: "00000", explicitTax: true));
        Assert.Null(d.Match);
        Assert.Equal(CmsContactMatchKind.None, d.Kind);
        Assert.Null(byEmail.TaxId);   // ไม่เติมเลขภาษีลงแถวของคนอื่น
        Assert.Null(byPhone.TaxId);
    }

    [Fact]
    public void Strict_tax_key_matches_by_tax_and_branch()
    {
        var hq = C("บริษัท เอ", taxId: TaxA, branch: "00000");
        var d = CmsContactResolver.Decide(new[] { C("อื่น", email: "a@x.com"), hq }, In(email: "a@x.com", taxId: TaxA, branch: "00000", explicitTax: true));
        Assert.Same(hq, d.Match);
        Assert.Equal(CmsContactMatchKind.TaxKey, d.Kind);
    }

    [Fact]
    public void Linked_contact_loses_to_explicit_different_tax_id()
    {
        var linked = C("ผูกไว้", taxId: TaxA, branch: "00000");
        var other = C("บริษัท บี", taxId: TaxB, branch: "00000");
        var d = CmsContactResolver.Decide(new[] { linked, other }, In(taxId: TaxB, branch: "00000", linked: linked.Id, explicitTax: true));
        Assert.Same(other, d.Match);
        // ไม่มีแถวของเลขนั้น ⇒ ไม่ได้แถวที่ผูก (ผู้เรียกสร้างใหม่)
        Assert.Null(CmsContactResolver.Decide(new[] { linked }, In(taxId: TaxB, branch: "00000", linked: linked.Id, explicitTax: true)).Match);
        // สาขาต่างกันก็ไม่ใช่ผู้ซื้อคนเดียวกัน
        Assert.Null(CmsContactResolver.Decide(new[] { linked }, In(taxId: TaxA, branch: "00003", linked: linked.Id, explicitTax: true)).Match);
    }

    [Fact]
    public void Linked_contact_with_same_tax_key_still_wins()
    {
        var linked = C("ผูกไว้", taxId: TaxA, branch: "00000");
        var d = CmsContactResolver.Decide(new[] { linked, C("ซ้ำ", taxId: TaxA, branch: "00000") },
            In(taxId: TaxA, branch: "00000", linked: linked.Id, explicitTax: true));
        Assert.Same(linked, d.Match);
        Assert.Equal(CmsContactMatchKind.Linked, d.Kind);
    }

    [Fact]
    public void No_tax_invoice_path_unchanged()
    {
        // ไม่ได้กรอกเลขภาษีในช่องใบกำกับ ⇒ เส้นเดิม: ผูกไว้ชนะ · อีเมลจับได้และเติมเลขจากบัญชีลูกค้า
        var linked = C("ผูกไว้", taxId: TaxA, branch: "00000");
        Assert.Same(linked, CmsContactResolver.Decide(new[] { linked }, In(taxId: TaxB, linked: linked.Id)).Match);
        var row = C("A", email: "a@x.com");
        var d = CmsContactResolver.Decide(new[] { row }, In(email: "a@x.com", taxId: TaxA));
        Assert.Same(row, d.Match);
        Assert.Equal(TaxA, row.TaxId);
        Assert.False(In(taxId: null, explicitTax: true).StrictTaxKey);
    }

    [Fact]
    public void For_order_marks_billing_tax_id_explicit()
    {
        Assert.True(CmsContactResolver.ForOrder(new SiteOrder { OrderNumber = "W-1", BillingTaxId = TaxA, BillingBranchCode = "00000" }, "t").StrictTaxKey);
        Assert.False(CmsContactResolver.ForOrder(new SiteOrder { OrderNumber = "W-2" }, "t").StrictTaxKey);
    }

    // ═════════ ข้อ 6: ลดหนี้ใบขายของบัตรที่โอนแล้ว ⇒ ตัดสินกับบัตรปลายทาง ═════════

    [Fact]
    public void Transfer_chain_follows_only_transferred_passes_and_stops()
    {
        var to = Guid.NewGuid();
        Assert.Equal(to, CreditPassDocumentRule.NextTransferHop(CreditPassStatus.Transferred, to, 0));
        Assert.Null(CreditPassDocumentRule.NextTransferHop(CreditPassStatus.Active, to, 0));
        Assert.Null(CreditPassDocumentRule.NextTransferHop(CreditPassStatus.Transferred, null, 0));
        Assert.Null(CreditPassDocumentRule.NextTransferHop(CreditPassStatus.Transferred, to, CreditPassDocumentRule.MaxTransferHops));
    }

    [Fact]
    public void Destination_pass_caps_credit_note_at_its_remaining_value()
    {
        // บัตรปลายทาง: สัญญา 6,000 (ย้ายมา) · ยังไม่ใช้ ⇒ ลดหนี้ได้ไม่เกิน 6,000
        var dest = new CreditPassDocumentRule.PassFacts("CP-2", CreditPassStatus.Active, 0m, 0m, 6000m, 0m, 0m, 6m);
        Assert.Equal(CreditPassDocumentRule.DocAction.CancelPass, CreditPassDocumentRule.OnCreditNote(dest, "INV-1", 6000m, true).Action);
        Assert.Equal(CreditPassDocumentRule.DocAction.Block, CreditPassDocumentRule.OnCreditNote(dest, "INV-1", 6500m, true).Action);
        var partial = CreditPassDocumentRule.OnCreditNote(dest, "INV-1", 2000m, true);
        Assert.Equal(CreditPassDocumentRule.DocAction.ReduceCredits, partial.Action);
        Assert.Equal(2m, partial.CreditsToRemove);
    }

    [Fact]
    public void Transferred_void_block_gives_credit_note_way_forward()
    {
        var src = new CreditPassDocumentRule.PassFacts("CP-1", CreditPassStatus.Transferred, 2m, 0m, 10000m, 4000m, 0m, 10m);
        var v = CreditPassDocumentRule.OnVoidSale(src, "INV-1");
        Assert.Equal(CreditPassDocumentRule.DocAction.Block, v.Action);
        Assert.Contains("ออกใบลดหนี้อ้างใบนี้", v.Message);
        // ตามสายไม่ถึง (ผู้เรียกส่งบัตรที่ยังเป็น Transferred) ⇒ ยังบล็อก + บอกให้ปรับปรุงด้วย JE
        var cn = CreditPassDocumentRule.OnCreditNote(src, "INV-1", 1000m, true);
        Assert.Equal(CreditPassDocumentRule.DocAction.Block, cn.Action);
        Assert.Contains("JE", cn.Message);
        Assert.Contains("CP-9", CreditPassDocumentRule.TransferredTargetClosedMessage("INV-1", "CP-1", "CP-9"));
    }

    // ═════════ ข้อ 7: ใบลดหนี้ของออเดอร์เว็บไม่คืนสต็อก ⇒ ข้อความไม่สัญญา no-op ═════════

    [Fact]
    public void Draft_notes_do_not_promise_stock_return_via_reason_change()
    {
        var notes = SiteOrderRefundPlan.DraftNotes("W-1", "INV-1", DateTime.UtcNow, 1070m, true, false, null);
        Assert.Contains(SiteOrderRefundPlan.StockReturnHint, notes);
        Assert.DoesNotContain("เปลี่ยนเหตุผลเป็น", notes);
        // บรรทัดใบขายของออเดอร์เว็บไม่ผูกรหัสสินค้า (สต็อกตัดจากออเดอร์) — ถ้าวันหนึ่งผูก ต้องทบทวนข้อความนี้และการตัดสต็อกซ้ำ
        var o = new SiteOrder { OrderNumber = "W-1" };
        o.Lines.Add(new SiteOrderLine { ProductName = "เค้ก", Quantity = 1m, UnitPrice = 100m, VatRate = 7m });
        Assert.All(CmsCommerceService.BuildOrderErpLines(o), l => Assert.Null(l.ProductCode));
    }
}

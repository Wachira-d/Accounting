using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 288b (ผลฝ่ายค้านของรอบ 288 · คำตัดสิน 288-9…288-14): ตัวตัดสิน pure — (1) ตราเนื้อหาเอกสารตัวเดียวของ "ตรวจแล้วอนุมัติ" (<see cref="ApprovalContentStamp"/>) ·
/// (4) ใบกำกับอย่างย่อจากผู้ขายบล็อกการอนุมัติเอง (<see cref="PartnerExchangeRules.IsAbbreviatedTaxInvoice"/> → <see cref="PartnerAutoApprovePolicy"/>) ·
/// (3)(6) คิวสำเนาสลิป: ผลรอเลื่อนรอบ · ใบซื้อหาย/ไม่ใช่คู่ค้า ⇒ จบพร้อมเหตุ (<see cref="PartnerSlipCopyRule"/>) — ทุกข้อมีทิศตรงข้าม
/// </summary>
public class PartnerAutomationRound288bTests
{
    private static readonly DateTime D = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    private static Document Doc()
    {
        var d = new Document
        {
            CompanyId = Guid.NewGuid(), ContactId = Guid.NewGuid(), DocumentType = DocumentType.PurchaseInvoice, DocumentDate = D, Currency = "THB",
            SubTotal = 1000m, VatAmount = 70m, TotalAmount = 1070m, HasTaxInvoiceReference = true, SupplierInvoiceNumber = "INV-1",
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1m, UnitPrice = 1000m, Amount = 1000m, VatRate = 7m, VatAmount = 70m,
            IsVatClaimable = true, AccountId = Guid.NewGuid() });
        d.Lines.Add(new DocumentLine { LineOrder = 2, Description = "ค่าส่ง", Quantity = 1m, UnitPrice = 0m, Amount = 0m });
        return d;
    }

    [Fact]
    public void ตราเนื้อหา_เปลี่ยนเมื่อยอด_ผัง_VAT_บรรทัดเปลี่ยน_ไม่เปลี่ยนเมื่อแตะหมายเหตุหรือเลขศูนย์ท้าย()
    {
        var d = Doc();
        var stamp = ApprovalContentStamp.Of(d);
        Assert.Equal(64, stamp.Length);

        // ทิศตรงข้าม (ใบที่ไม่ถูกแก้เนื้อหาไม่ถูกกัน): หมายเหตุภายใน/เวลาแก้ไข (งานอัตโนมัติเขียนเอง) · ศูนย์ท้ายจาก numeric · ลำดับบรรทัดในหน่วยความจำ
        d.InternalNotes = "[PARTNER-AUTO-APPROVE-HOLD] x";
        d.UpdatedAt = D.AddHours(3);
        d.TotalAmount = 1070.00m;
        d.Lines.First().Amount = 1000.0000m;
        var reordered = Enumerable.Reverse(d.Lines).ToList();
        d.Lines.Clear();
        foreach (var l in reordered) d.Lines.Add(l);
        Assert.Equal(stamp, ApprovalContentStamp.Of(d));

        // ทุกค่าที่ด่านอนุมัติเองอ่าน ⇒ ตราเปลี่ยน (ใบถูกแก้ระหว่างตรวจ = ไม่อนุมัติ)
        void Changes(Action<Document> edit)
        {
            var x = Doc();
            var baseline = Doc();
            // บรรทัด/ใบใหม่ได้ Id ใหม่ ⇒ ยก Id ให้ตรงก่อนเทียบ
            x.Id = baseline.Id;
            x.CompanyId = baseline.CompanyId;
            x.ContactId = baseline.ContactId;
            var bl = baseline.Lines.ToList();
            var xl = x.Lines.ToList();
            for (var i = 0; i < bl.Count; i++) { xl[i].Id = bl[i].Id; xl[i].AccountId = bl[i].AccountId; }
            Assert.Equal(ApprovalContentStamp.Of(baseline), ApprovalContentStamp.Of(x));
            edit(x);
            Assert.NotEqual(ApprovalContentStamp.Of(baseline), ApprovalContentStamp.Of(x));
        }
        Changes(x => x.TotalAmount = 99_999m);                          // ยอดเกินเพดาน
        Changes(x => x.Lines.First().Amount = 50_000m);
        Changes(x => x.Lines.First().AccountId = Guid.NewGuid());       // ผังที่ประเมินว่ามั่นใจ
        Changes(x => x.Lines.First().IsVatClaimable = false);
        Changes(x => x.Lines.First().WithholdingTaxRate = 3m);
        Changes(x => x.WithholdingTaxAmount = 30m);
        Changes(x => x.HasTaxInvoiceReference = false);
        Changes(x => x.Lines.Last().IsDeleted = true);                   // ลบบรรทัด
        Changes(x => x.Lines.Add(new DocumentLine { LineOrder = 3, Description = "เพิ่ม", Quantity = 1, UnitPrice = 1, Amount = 1 }));
        Changes(x => x.Currency = "USD");
    }

    [Fact]
    public void ใบกำกับอย่างย่อจากผู้ขาย_ไม่อนุมัติเอง_แม้บรรทัดยังตั้งว่าเคลมได้_ใบเต็มรูปอนุมัติได้()
    {
        Assert.True(PartnerExchangeRules.IsAbbreviatedTaxInvoice(ReceiptForm.ReceiptTaxInvoiceAbbreviated));
        foreach (var f in new ReceiptForm?[] { null, ReceiptForm.ReceiptTaxInvoiceFull, ReceiptForm.PlainReceipt, ReceiptForm.PaymentEvidenceOnly })
            Assert.False(PartnerExchangeRules.IsAbbreviatedTaxInvoice(f));
        // ตัวตัดสินเดียว: ใบอย่างย่อไม่เป็นผู้ถือใบกำกับเต็มรูปของฝั่งผู้รับ
        Assert.False(PartnerExchangeRules.IsTaxInvoiceCarrier(DocumentType.Receipt, true, 70m, ReceiptForm.ReceiptTaxInvoiceAbbreviated));
        Assert.True(PartnerExchangeRules.IsTaxInvoiceCarrier(DocumentType.Receipt, true, 70m, ReceiptForm.ReceiptTaxInvoiceFull));

        var facts = new PartnerAutoApprovePolicy.Facts(
            ConsentOk: true, ConsentReason: "ใช้ได้", GrantorMayFinalize: true, IsDraft: true, SourceVoided: false, Currency: "THB",
            IsTaxInvoice: false, TaxInvoiceMissing: Array.Empty<string>(),
            Lines: new[] { new PartnerAutoApprovePolicy.Line(1, "ค่าอาหาร", true, true, false, InputVatBlocked: false) },
            WhtProblem: null, ApprovalWarnings: Array.Empty<string>(), AbbreviatedTaxInvoice: true);
        var v = PartnerAutoApprovePolicy.Decide(facts);
        Assert.False(v.Auto);
        Assert.Equal(PartnerAutoApprovePolicy.Codes.AbbreviatedTaxInvoice, v.Code);
        Assert.Contains("ม.82/5(2)", v.Reason);
        Assert.Contains("อนุมัติเอง", v.Reason);   // ทางไปต่อของผู้ใช้
        // ทิศตรงข้าม: ใบเดียวกันที่ไม่ใช่อย่างย่อ ⇒ อนุมัติเอง
        Assert.True(PartnerAutoApprovePolicy.Decide(facts with { AbbreviatedTaxInvoice = false }).Auto);
    }

    private static PartnerSlipCopyRule.Facts Slip() => new(
        ConsentOk: true, ConsentGrantedAt: D, NoticeCreatedAt: D.AddHours(1), HasIntent: true, IntentIsSlip: true, IntentSucceeded: true,
        SlipAttachmentId: Guid.NewGuid(), AttachmentFound: true, AttachmentChannel: "portal-customer");

    [Fact]
    public void สำเนาสลิป_ใบซื้อหาย_หรือไม่ใช่คู่ค้า_จบพร้อมเหตุ_ไม่รอตลอดไป_ผลรอเลื่อนรอบ()
    {
        var lost = PartnerSlipCopyRule.Decide(Slip() with { PurchaseFound = false });
        Assert.Equal(PartnerSlipCopyRule.Outcome.Done, lost.Outcome);
        Assert.Equal(PartnerSlipCopyRule.PurchaseMissingReason, lost.Reason);
        // แม้ยังไม่ยินยอม/ยังไม่ยืนยัน ก็จบ (เดิม purchase == null ถูกรวมเป็น "ยังไม่ยินยอม" ⇒ รอตลอดไป)
        Assert.Equal(PartnerSlipCopyRule.Outcome.Done, PartnerSlipCopyRule.Decide(Slip() with { PurchaseFound = false, ConsentOk = false }).Outcome);
        var foreign = PartnerSlipCopyRule.Decide(Slip() with { PartnerPairOk = false });
        Assert.Equal(PartnerSlipCopyRule.Outcome.Done, foreign.Outcome);
        Assert.Equal(PartnerSlipCopyRule.PartnerMismatchReason, foreign.Reason);
        // ทิศตรงข้าม: ค่าตั้งต้น (ใบซื้อมี · คู่ถูก) ⇒ คัดสำเนา · ยังไม่ยืนยัน ⇒ รอ
        Assert.Equal(PartnerSlipCopyRule.Outcome.Copy, PartnerSlipCopyRule.Decide(Slip()).Outcome);
        Assert.Equal(PartnerSlipCopyRule.Outcome.Wait, PartnerSlipCopyRule.Decide(Slip() with { IntentSucceeded = false }).Outcome);
        Assert.Equal(D + PartnerSlipCopyRule.WaitRecheckEvery, PartnerSlipCopyRule.NextCheckAt(D));
        Assert.True(PartnerSlipCopyRule.WaitRecheckEvery > TimeSpan.Zero);

        // ป้าย (server computes)
        Assert.Equal(PartnerSlipCopyRule.CopiedLabel, PartnerSlipCopyRule.StatusLabel(Guid.NewGuid(), D, null));
        Assert.Equal("ไม่ได้สำเนาสลิป: " + PartnerSlipCopyRule.PurchaseMissingReason, PartnerSlipCopyRule.StatusLabel(null, D, PartnerSlipCopyRule.PurchaseMissingReason));
        Assert.Null(PartnerSlipCopyRule.StatusLabel(null, null, null));
        Assert.Null(PartnerSlipCopyRule.StatusLabel(null, D, null));
    }
}

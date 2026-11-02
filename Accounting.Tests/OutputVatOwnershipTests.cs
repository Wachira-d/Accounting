using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 225 B1 — "ใบไหนเป็นเจ้าของภาษีขาย" ย้ายจาก inline ใน <c>TaxService.GenerateVatReport</c> มาเป็น <see cref="OutputVatOwnership"/>
/// (ตัวเดียวกับปุ่ม "ดึงเอกสาร") — <b>loop หลักต้องได้ผลเดิมทุกกรณี</b>: เทสต์นี้เทียบกับสำเนาเงื่อนไขเดิม (ลอกจาก
/// <c>git show HEAD:Accounting/Services/Implementations/TaxService.cs</c> ก่อนรอบ 225 — สาขาใบกำกับ/ใบแจ้งหนี้ + สาขาใบเสร็จ/มัดจำ)
/// บนตารางข้อเท็จจริงทุกทาง (ชนิด × VAT × FK × ใบกำกับแทน × ใบเสร็จถือ VAT × 21913 × มัดจำ × ธง)
/// </summary>
public class OutputVatOwnershipTests
{
    private static readonly DateTime Tp = new(2026, 10, 5), Due = new(2026, 11, 3), Rec = new(2026, 12, 1);

    /// <summary>ผลของโค้ดเดิม: (สาขา, "ข้ามด้วยเหตุความเป็นเจ้าของ", วันที่ที่ใช้เทียบงวด) — ไม่รวมการเทียบงวด (คงอยู่ใน loop)</summary>
    private static (OutputVatOwnership.Branch Branch, bool Skip, DateTime? DueDate) Legacy(OutputVatOwnership.Facts f)
    {
        // ── สาขาที่ 1 (เดิม): TaxInvoice || (Invoice && VatAmount > 0 && !superseded) ──
        if (f.Type == DocumentType.TaxInvoice
            || (f.Type == DocumentType.Invoice && f.VatAmount > 0 && !f.SupersededByTaxInvoice))
        {
            if (f.Type == DocumentType.Invoice)
            {
                if (f.OutputVatDueAt != null)
                {
                    if (f.OwnedByVatReceipt) return (OutputVatOwnership.Branch.Invoice, true, null);
                    return (OutputVatOwnership.Branch.Invoice, false, f.OutputVatDueAt);
                }
                if (f.Gl21913Net > 0.005m) return (OutputVatOwnership.Branch.Invoice, true, null);
            }
            return (OutputVatOwnership.Branch.Invoice, false, f.TaxPoint);
        }
        // ── สาขาที่ 2 (เดิม): Receipt/RV ที่ standalone / จาก QT,BN / ถือ VAT ของใบแจ้งหนี้ที่ถึงกำหนดแล้ว ──
        if ((f.Type == DocumentType.Receipt || f.Type == DocumentType.ReceiptVoucher)
            && (!f.HasRelated
                || (f.RelatedType != null && (f.RelatedType == DocumentType.Quotation || f.RelatedType == DocumentType.BillingNote))
                || (f.VatAmount > 0.005m && f.RelatedType == DocumentType.Invoice && f.RelatedOutputVatDueAt != null)))
        {
            var effectivelyDeferred = f.IsDeposit && (f.DepositOutputVatDeferred || f.Gl21913Net > 0.005m);
            if (effectivelyDeferred)
            {
                if (f.DepositApplied) return (OutputVatOwnership.Branch.Receipt, true, null);
                if (f.DepositOutputVatRecognizedAt == null) return (OutputVatOwnership.Branch.Receipt, true, null);
                return (OutputVatOwnership.Branch.Receipt, false, f.DepositOutputVatRecognizedAt);
            }
            if (f.IsDeposit && f.DepositApplied) return (OutputVatOwnership.Branch.Receipt, true, null);
            return (OutputVatOwnership.Branch.Receipt, false, f.TaxPoint);
        }
        return (OutputVatOwnership.Branch.None, true, null);
    }

    public static IEnumerable<object[]> Matrix()
    {
        var types = new[] { DocumentType.TaxInvoice, DocumentType.Invoice, DocumentType.Receipt, DocumentType.ReceiptVoucher,
            DocumentType.PurchaseInvoice, DocumentType.CreditNote };
        var related = new (bool Has, DocumentType? Type, DateTime? Due)[]
        {
            (false, null, null), (true, null, null), (true, DocumentType.Quotation, null), (true, DocumentType.BillingNote, null),
            (true, DocumentType.TaxInvoice, null), (true, DocumentType.Invoice, null), (true, DocumentType.Invoice, Due),
        };
        var i = 0;
        foreach (var t in types)
        foreach (var vat in new[] { 70m, 0m, -70m, 0.004m })
        foreach (var rel in related)
        foreach (var dueAt in new DateTime?[] { null, Due })
        foreach (var sup in new[] { false, true })
        foreach (var ownRc in new[] { false, true })
        foreach (var gl in new[] { 0m, 70m })
        foreach (var dep in new[] { false, true })
        foreach (var deferred in new[] { false, true })
        foreach (var applied in new[] { false, true })
        foreach (var rec in new DateTime?[] { null, Rec })
        {
            // ตัดชุดใหญ่ด้วยการสุ่มแบบกำหนดได้ (ทุกตัวที่ 7) — ยังครอบทุกค่าทุกมิติ
            if (i++ % 7 != 0) continue;
            yield return new object[] { new OutputVatOwnership.Facts(t, vat, Tp, dueAt, rel.Has, rel.Type, rel.Due,
                dep, deferred, applied, rec, sup, ownRc, gl) };
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ผลเท่ากับโค้ดเดิมของ_loop_หลักทุกกรณี(OutputVatOwnership.Facts f)
    {
        var legacy = Legacy(f);
        var d = OutputVatOwnership.Decide(f);
        Assert.Equal(legacy.Branch, d.Branch);
        Assert.Equal(legacy.Skip, !d.Owns);
        Assert.Equal(legacy.DueDate, d.VatDueDate);
        if (!d.Owns) { Assert.False(string.IsNullOrWhiteSpace(d.Reason)); Assert.NotNull(d.RuleCode); }
    }

    [Fact]
    public void ใบกำกับเดี่ยวเป็นเจ้าของ_ใบแจ้งหนี้ที่ถูกแทนไม่เป็น()
    {
        var ti = new OutputVatOwnership.Facts(DocumentType.TaxInvoice, 70m, Tp, null, false, null, null, false, false, false, null, false, false, 0m);
        Assert.True(OutputVatOwnership.Decide(ti).Owns);
        var inv = ti with { Type = DocumentType.Invoice, SupersededByTaxInvoice = true };
        var d = OutputVatOwnership.Decide(inv);
        Assert.Equal(OutputVatOwnership.Branch.None, d.Branch);
        Assert.Equal(OutputVatOwnership.RuleSuperseded, d.RuleCode);
    }
}

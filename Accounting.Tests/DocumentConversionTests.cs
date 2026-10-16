using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// Covers DocumentService.GetValidConversionTargets — the Thai-workflow
/// conversion map that both the convert API and the convert UI rely on.
/// Pure static logic, no database or DI required.
/// </summary>
public class DocumentConversionTests
{
    [Fact]
    public void Quotation_converts_to_invoice_taxinvoice_and_receipt()
    {
        var targets = DocumentService.GetValidConversionTargets(DocumentType.Quotation);

        Assert.Contains(DocumentType.Invoice, targets);
        Assert.Contains(DocumentType.TaxInvoice, targets);
        Assert.Contains(DocumentType.Receipt, targets);
    }

    [Fact]
    public void Receipt_converts_only_to_credit_or_debit_note()
    {
        // เดิม "ใบเสร็จเป็นปลายทาง" — ตารางแปลง (DocumentService.ValidConversions) ให้ใบเสร็จที่เป็นใบกำกับในตัว
        // ออกใบลด/เพิ่มหนี้อ้างได้ตาม §86/9–10 (CN/DN ต้องอ้างกระดาษใบกำกับจริง · ด่านใบเสร็จเปล่า/มัดจำอยู่ที่ ValidateConversionAsync)
        // · ห้ามย้อนกลับเป็นใบขายอื่นเหมือนเดิม
        var targets = DocumentService.GetValidConversionTargets(DocumentType.Receipt);

        Assert.Equal(new HashSet<DocumentType> { DocumentType.CreditNote, DocumentType.DebitNote }, targets.ToHashSet());
        Assert.DoesNotContain(DocumentType.Invoice, targets);
        Assert.DoesNotContain(DocumentType.TaxInvoice, targets);
        Assert.DoesNotContain(DocumentType.Receipt, targets);
    }

    [Fact]
    public void Invoice_converts_to_taxinvoice_and_receipt_but_not_back_to_quotation()
    {
        var targets = DocumentService.GetValidConversionTargets(DocumentType.Invoice);

        Assert.Contains(DocumentType.TaxInvoice, targets);
        Assert.Contains(DocumentType.Receipt, targets);
        Assert.DoesNotContain(DocumentType.Quotation, targets);
    }

    [Fact]
    public void PurchaseOrder_converts_to_grn_and_purchase_invoice_but_not_expense()
    {
        var targets = DocumentService.GetValidConversionTargets(DocumentType.PurchaseOrder);

        // PR→PO→GRN→PurchaseInvoice = trade chain (เจ้าหนี้การค้า 21210).
        Assert.Contains(DocumentType.PurchaseInvoice, targets);   // ข้าม GRN เมื่อซื้อบริการ
        Assert.Contains(DocumentType.GoodsReceiptNote, targets);  // รับของบางส่วน
        // ใบบันทึกค่าใช้จ่าย (Expense) = รายจ่ายนอกการค้า (เจ้าหนี้อื่น 21220)
        // แยกออกจาก PO chain โดยเจตนา เพื่อให้ 2 บัญชีเจ้าหนี้กระทบยอดแยกกันได้
        Assert.DoesNotContain(DocumentType.Expense, targets);
    }

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.PurchaseOrder)]
    [InlineData(DocumentType.PurchaseInvoice)]
    public void No_document_type_lists_itself_as_a_valid_target(DocumentType source)
    {
        // ConvertDocumentAsync blocks self-conversion explicitly; the map
        // must never offer it either, or the UI would surface a no-op.
        var targets = DocumentService.GetValidConversionTargets(source);

        Assert.DoesNotContain(source, targets);
    }
}

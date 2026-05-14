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
    public void Receipt_is_a_terminal_document()
    {
        var targets = DocumentService.GetValidConversionTargets(DocumentType.Receipt);

        Assert.Empty(targets);
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
    public void PurchaseOrder_converts_to_purchase_invoice_and_expense()
    {
        var targets = DocumentService.GetValidConversionTargets(DocumentType.PurchaseOrder);

        Assert.Contains(DocumentType.PurchaseInvoice, targets);
        Assert.Contains(DocumentType.Expense, targets);
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

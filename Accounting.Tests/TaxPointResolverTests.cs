using Accounting.Models.Entities;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตรวจ TaxPointResolver.Resolve — จุดความรับผิด VAT §78 / §78/1 (pure function).
/// สำคัญ: VAT period ของ ภ.พ.30 ใช้เดือนของ tax point ไม่ใช่ DocumentDate.
/// </summary>
public class TaxPointResolverTests
{
    private static readonly DateTime Base = new(2026, 6, 15);

    [Fact]
    public void Goods_takes_earliest_of_delivery_ownership_payment_issue()
    {
        var doc = new Document
        {
            DocumentDate = Base,
            DeliveryDate = new DateTime(2026, 6, 10),
            OwnershipTransferDate = new DateTime(2026, 6, 12),
            PaymentDate = new DateTime(2026, 6, 20),
        };
        Assert.Equal(new DateTime(2026, 6, 10), TaxPointResolver.Resolve(doc));
    }

    [Fact]
    public void Service_takes_earliest_of_payment_issue_serviceUsed()
    {
        var doc = new Document
        {
            DocumentDate = Base,
            ServiceUsedDate = new DateTime(2026, 6, 5),
            PaymentDate = new DateTime(2026, 6, 25),
        };
        // ServiceUsedDate present → §78/1 branch; MIN(payment 25, issue 15, used 5) = 5
        Assert.Equal(new DateTime(2026, 6, 5), TaxPointResolver.Resolve(doc));
    }

    [Fact]
    public void Falls_back_to_issue_date_when_no_signals()
    {
        var doc = new Document { DocumentDate = Base };
        Assert.Equal(Base, TaxPointResolver.Resolve(doc));
    }

    [Fact]
    public void Purchase_uses_supplier_invoice_date_as_issue()
    {
        var doc = new Document
        {
            DocumentDate = new DateTime(2026, 7, 1),
            SupplierTaxInvoiceDate = new DateTime(2026, 6, 28),
        };
        // no delivery/payment → issue = supplier invoice date (not document date)
        Assert.Equal(new DateTime(2026, 6, 28), TaxPointResolver.Resolve(doc));
    }

    [Fact]
    public void Payment_earlier_than_issue_wins_for_goods()
    {
        var doc = new Document
        {
            DocumentDate = Base,
            PaymentDate = new DateTime(2026, 5, 30),   // จ่ายก่อนออกใบ → tax point เดือน พ.ค.
        };
        var tp = TaxPointResolver.Resolve(doc);
        Assert.Equal(new DateTime(2026, 5, 30), tp);
        Assert.True(TaxPointResolver.DiffersFromDocumentMonth(doc, tp));
    }

    [Fact]
    public void Same_month_reports_no_difference()
    {
        var doc = new Document { DocumentDate = Base, DeliveryDate = new DateTime(2026, 6, 1) };
        var tp = TaxPointResolver.Resolve(doc);
        Assert.False(TaxPointResolver.DiffersFromDocumentMonth(doc, tp));
    }
}

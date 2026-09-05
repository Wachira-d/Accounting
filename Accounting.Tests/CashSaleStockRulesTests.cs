using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ERP_REVIEW_2026-09-05 A-06 — ใบเสร็จ standalone ที่ขายสินค้าคงคลัง เดิมไม่ตัดสต๊อก/ไม่ลง COGS เสมอ
/// (`_ => 0`) ตอนนี้ตามนโยบายบริษัท 3 ทาง · กติกาอยู่ที่ CashSaleStockRules ตัวเดียว
/// ทั้ง ApplyStockMovementsAsync (ทิศสต๊อก) และ AutoPost (COGS) ต้องตอบตรงกัน
/// </summary>
public class CashSaleStockRulesTests
{
    private static Document Doc(DocumentType t, Guid? related = null, bool deposit = false)
        => new() { DocumentType = t, RelatedDocumentId = related, IsDeposit = deposit };

    [Theory]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.ReceiptVoucher)]
    public void ใบเสร็จและใบสำคัญรับ_standalone_อยู่ใต้กติกา(DocumentType t)
        => Assert.True(CashSaleStockRules.AppliesTo(Doc(t)));

    [Fact]
    public void ใบเสร็จที่อ้างใบแจ้งหนี้_ไม่อยู่ใต้กติกา_เพราะใบแจ้งหนี้ตัดสต๊อกแล้ว()
        => Assert.False(CashSaleStockRules.AppliesTo(Doc(DocumentType.Receipt, related: Guid.NewGuid())));

    [Fact]
    public void ใบมัดจำ_ไม่อยู่ใต้กติกา_เพราะยังไม่ส่งมอบของ()
        => Assert.False(CashSaleStockRules.AppliesTo(Doc(DocumentType.Receipt, deposit: true)));

    [Theory]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.PaymentVoucher)]
    public void ชนิดอื่น_ไม่อยู่ใต้กติกา(DocumentType t)
        => Assert.False(CashSaleStockRules.AppliesTo(Doc(t)));

    [Fact]
    public void ทิศสต๊อกและ_COGS_ต้องตอบตรงกันทุกนโยบาย()
    {
        foreach (var p in Enum.GetValues<CashSaleStockPolicy>())
        {
            var moves = CashSaleStockRules.StockDirection(p) != 0;
            Assert.Equal(moves, CashSaleStockRules.PostsCogs(p));   // สต๊อกลดแต่ GL ไม่ลด = ห้ามเกิด
        }
    }

    [Fact]
    public void MoveStockAndCogs_ขายออก_และลง_COGS()
    {
        Assert.Equal(-1, CashSaleStockRules.StockDirection(CashSaleStockPolicy.MoveStockAndCogs));
        Assert.True(CashSaleStockRules.PostsCogs(CashSaleStockPolicy.MoveStockAndCogs));
        Assert.False(CashSaleStockRules.BlocksApproval(CashSaleStockPolicy.MoveStockAndCogs));
    }

    [Fact]
    public void Ignore_คือพฤติกรรมเดิม_ไม่แตะอะไร()
    {
        Assert.Equal(0, CashSaleStockRules.StockDirection(CashSaleStockPolicy.Ignore));
        Assert.False(CashSaleStockRules.PostsCogs(CashSaleStockPolicy.Ignore));
        Assert.False(CashSaleStockRules.BlocksApproval(CashSaleStockPolicy.Ignore));
    }

    [Fact]
    public void Block_ไม่แตะสต๊อกแต่บล็อกการอนุมัติ()
    {
        Assert.Equal(0, CashSaleStockRules.StockDirection(CashSaleStockPolicy.Block));
        Assert.True(CashSaleStockRules.BlocksApproval(CashSaleStockPolicy.Block));
    }

    [Fact]
    public void ทุกนโยบายมีคำอธิบายที่ไม่ว่าง()
    {
        foreach (var p in Enum.GetValues<CashSaleStockPolicy>())
            Assert.False(string.IsNullOrWhiteSpace(CashSaleStockRules.Describe(p)));
    }
}

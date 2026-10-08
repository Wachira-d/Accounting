using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีมตรวจงานค้าง 2026-10-08 (C-01): รายได้รวมของใบเสนอราคานับทุกทาง — ตรง · ผ่านใบส่งของ · ผ่านใบวางบิล · ใบเสร็จขายสด
/// (เดิม QT → ใบวางบิล → ใบแจ้งหนี้ + QT → ใบส่งของ → ใบแจ้งหนี้ เรียกเก็บเกินยอดได้โดยไม่มีอะไรเตือน)
/// </summary>
public class RootRevenueLedgerTests
{
    [Theory]
    [InlineData(DocumentType.Invoice, DocumentType.Quotation)]
    [InlineData(DocumentType.TaxInvoice, DocumentType.DeliveryNote)]
    [InlineData(DocumentType.Invoice, DocumentType.BillingNote)]
    [InlineData(DocumentType.Receipt, DocumentType.Quotation)]
    [InlineData(DocumentType.Receipt, DocumentType.BillingNote)]
    public void ใบที่รับรู้รายได้ใต้ใบเสนอราคา_นับ(DocumentType child, DocumentType parent)
        => Assert.True(RootRevenueLedger.CountsAsRevenue(child, parent, isDeposit: false));

    [Fact]
    public void ทิศตรงข้าม_ใบมัดจำไม่นับ()
        => Assert.False(RootRevenueLedger.CountsAsRevenue(DocumentType.Invoice, DocumentType.Quotation, isDeposit: true));

    [Fact]
    public void ทิศตรงข้าม_ใบวางบิลเองไม่ใช่รายได้()
        => Assert.False(RootRevenueLedger.CountsAsRevenue(DocumentType.BillingNote, DocumentType.Quotation, false));

    [Fact]
    public void ทิศตรงข้าม_ใบเสร็จใต้ใบส่งของไม่ใช่ขายสดของใบเสนอราคา()
        => Assert.False(RootRevenueLedger.CountsAsRevenue(DocumentType.Receipt, DocumentType.DeliveryNote, false));

    [Fact]
    public void ใบกลางที่ยกรายได้ขึ้นไปได้_คือใบส่งของและใบวางบิล()
    {
        Assert.True(RootRevenueLedger.IsMidDocument(DocumentType.DeliveryNote));
        Assert.True(RootRevenueLedger.IsMidDocument(DocumentType.BillingNote));
        Assert.False(RootRevenueLedger.IsMidDocument(DocumentType.Invoice));
    }

    [Fact]
    public void แบ่งพอดี100เปอร์เซ็นต์_ไม่เตือน_เกินสตางค์ปัดเศษ_ไม่เตือน_เกินจริง_เตือน()
    {
        // ใช้ตัวคิดเกินยอดตัวเดียวกับแปลงบางส่วน (ไม่มีสูตรที่สอง)
        Assert.False(PartialConvertPolicy.IsOverAmount(5_000m, 5_000m, 10_000m, 2));
        Assert.False(PartialConvertPolicy.IsOverAmount(5_000m, 5_000.01m, 10_000m, 2));
        Assert.True(PartialConvertPolicy.IsOverAmount(10_000m, 5_000m, 10_000m, 2));
        Assert.Contains("ตรวจว่าไม่ได้เรียกเก็บซ้ำ", RootRevenueLedger.OverMessage("QT-1", 10_000m, 5_000m, 10_000m));
    }
}

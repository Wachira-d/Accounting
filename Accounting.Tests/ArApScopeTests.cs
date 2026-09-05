using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ERP_REVIEW_2026-09-05 F-08/F-09 — ชุดชนิด "ลูกหนี้/เจ้าหนี้" ต้องมาจากที่เดียว และ **ใบวางบิลไม่ใช่ลูกหนี้**
/// (ไม่มี JE · ครอบใบแจ้งหนี้ที่ตั้งลูกหนี้ไว้แล้ว ⇒ นับซ้ำ) — เจ้าของโปรเจกต์ยืนยัน "ปรับให้ถูกต้อง"
/// </summary>
public class ArApScopeTests
{
    [Fact]
    public void ใบวางบิล_ไม่นับเป็นลูกหนี้()
    {
        Assert.DoesNotContain(DocumentType.BillingNote, ArApScope.ReceivableTypes);
        Assert.False(ArApScope.IsReceivable(DocumentType.BillingNote));
    }

    [Theory]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.DebitNote)]
    public void เอกสารที่ตั้งลูกหนี้จริง_อยู่ในชุด(DocumentType t)
        => Assert.True(ArApScope.IsReceivable(t));

    [Theory]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.ReceiptVoucher)]
    [InlineData(DocumentType.CreditNote)]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.DeliveryNote)]
    public void เอกสารที่ตัด_ลด_หรือไม่ตั้งลูกหนี้_ไม่อยู่ในชุด(DocumentType t)
        => Assert.False(ArApScope.IsReceivable(t));

    [Fact]
    public void ชุดลูกหนี้ทุกตัวเป็นฝั่งขายตาม_DocumentPermissionHelper()
    {
        foreach (var t in ArApScope.ReceivableTypes)
            Assert.True(DocumentPermissionHelper.IsRevenue(t) || DocumentSide.IsAmbiguous(t));
        foreach (var t in ArApScope.PayableTypes)
            Assert.True(DocumentPermissionHelper.IsPurchase(t) || DocumentSide.IsAmbiguous(t));
    }
}

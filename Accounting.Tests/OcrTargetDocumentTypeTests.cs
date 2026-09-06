using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่านสิทธิ์ "สร้างเอกสารจากสแกน" กับเส้นที่สร้างเอกสารจริง ต้องตัดสินชนิดเอกสาร
/// ด้วยฟังก์ชันเดียวกัน — ตัวแปลงที่ "คืน null เมื่อแปลงไม่ได้" ทำให้ด่านถูกข้าม
/// ในเคสที่พบบ่อยที่สุด (ผลตรวจย้อน 2026-09-06 · คอมมิต 4fd8dd6)
/// </summary>
public class OcrTargetDocumentTypeTests
{
    [Fact]
    public void สแกนที่ยังไม่มีชนิดเป้าหมาย_ต้องได้ชนิดจากกระดาษ_ไม่ใช่ไม่ตอบ()
    {
        // ใบเสร็จร้านค้าที่ role-inferrer ยังไม่ได้เขียน TargetDocumentType
        var r = OcrTargetDocumentType.Resolve(null, null, "Receipt");
        Assert.Equal(DocumentType.PaymentVoucher, r.Type);
        Assert.False(r.IsDeposit);
    }

    [Theory]
    [InlineData("Invoice", DocumentType.PurchaseInvoice)]
    [InlineData("TaxInvoice", DocumentType.PurchaseInvoice)]
    [InlineData("Receipt", DocumentType.PaymentVoucher)]
    [InlineData("CertificateInLieu", DocumentType.CertificateInLieu)]
    [InlineData("CreditNote", DocumentType.Expense)]
    [InlineData(null, DocumentType.Expense)]
    public void fallback_ตามชนิดกระดาษ_ตรงกับที่เส้นสร้างเอกสารใช้(string? paper, DocumentType expected)
        => Assert.Equal(expected, OcrTargetDocumentType.Resolve(null, null, paper).Type);

    [Fact]
    public void ใบมัดจำ_เป็น_Receipt_ฝั่งขาย_ไม่ใช่ค่าว่าง()
    {
        // "Deposit" ไม่ใช่สมาชิกของ enum → ตัวแปลงเดิมคืน null ⇒ ข้ามด่านสิทธิ์
        // ทั้งที่เอกสารที่เกิดจริงเป็น Receipt (ฝั่งขาย) ซึ่งเป็นสิทธิ์คนละคีย์
        var fromScan = OcrTargetDocumentType.Resolve(null, "Deposit", "Receipt");
        Assert.Equal(DocumentType.Receipt, fromScan.Type);
        Assert.True(fromScan.IsDeposit);
        Assert.True(DocumentPermissionHelper.IsRevenue(fromScan.Type));

        var fromOverride = OcrTargetDocumentType.Resolve("deposit", null, "Invoice");
        Assert.Equal(DocumentType.Receipt, fromOverride.Type);
        Assert.True(fromOverride.IsDeposit);
    }

    [Fact]
    public void ลำดับความสำคัญ_override_ชนะค่าที่อนุมานไว้_ชนะชนิดกระดาษ()
    {
        Assert.Equal(DocumentType.Expense,
            OcrTargetDocumentType.Resolve("Expense", "PurchaseInvoice", "TaxInvoice").Type);
        Assert.Equal(DocumentType.PurchaseInvoice,
            OcrTargetDocumentType.Resolve(null, "PurchaseInvoice", "Receipt").Type);
        Assert.Equal(DocumentType.PaymentVoucher,
            OcrTargetDocumentType.Resolve("   ", "  ", "Receipt").Type);
    }

    [Fact]
    public void ค่าที่แปลงไม่ได้_ต้องไม่ทำให้ไม่ตอบ()
    {
        // ชนิดที่ค้างมาจากรุ่นเก่า/สะกดผิด — ต้องตกไป fallback ไม่ใช่คืน "ไม่รู้"
        var r = OcrTargetDocumentType.Resolve("ไม่มีชนิดนี้", "ก็ไม่มีเหมือนกัน", "TaxInvoice");
        Assert.Equal(DocumentType.PurchaseInvoice, r.Type);
    }

    [Fact]
    public void ผูกใบสั่งซื้อแล้ว_เป็นใบซื้อเสมอ_ตรงกับที่เส้นสร้างเอกสารบังคับ()
    {
        var r = OcrTargetDocumentType.Resolve("Receipt", "Deposit", "Receipt",
            hasLinkedPurchaseOrder: true);
        Assert.Equal(DocumentType.PurchaseInvoice, r.Type);
        Assert.False(r.IsDeposit);
    }
}

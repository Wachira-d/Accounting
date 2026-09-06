using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ชนิดกระดาษต้องตัดสินจาก **สิ่งที่พิมพ์อยู่บนใบ** ไม่ใช่ชื่อไฟล์/โมเดลที่เราเลือกเอง
/// (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T2-14: ใบกำกับเต็มรูปที่ตั้งชื่อไฟล์ว่า
/// "receipt-2026-09.pdf" ถูกตีเป็นใบเสร็จ แล้วโดนปิดเคลมภาษีซื้อ §82/5(1))
/// </summary>
public class AzureDiFieldMappingTests
{
    [Fact]
    public void ใบกำกับภาษี_ถึงโมเดลจะเป็น_receipt_ก็ต้องไม่ใช่_Receipt()
    {
        var t = OcrService.MapAzureDocTypeForTest(
            azureDocType: "receipt", modelId: "prebuilt-receipt",
            rawText: "ใบกำกับภาษี/ใบเสร็จรับเงิน\nบริษัท ทดสอบ จำกัด\nเลขประจำตัวผู้เสียภาษี 0105561012345");
        Assert.Equal("Invoice", t);
    }

    [Fact]
    public void Tax_Invoice_ภาษาอังกฤษก็ต้องได้ผลเดียวกัน()
        => Assert.Equal("Invoice", OcrService.MapAzureDocTypeForTest("receipt", "prebuilt-receipt", "TAX INVOICE No. IV-001"));

    [Fact]
    public void ใบลดหนี้กับใบเพิ่มหนี้แยกออกจากกัน()
    {
        Assert.Equal("CreditNote", OcrService.MapAzureDocTypeForTest("invoice", "prebuilt-invoice", "ใบลดหนี้ เลขที่ CN-001"));
        Assert.Equal("DebitNote", OcrService.MapAzureDocTypeForTest("invoice", "prebuilt-invoice", "ใบเพิ่มหนี้ เลขที่ DN-001"));
    }

    [Fact]
    public void ใบเสร็จจริงที่ไม่มีคำว่าใบกำกับ_ยังเป็น_Receipt()
        => Assert.Equal("Receipt", OcrService.MapAzureDocTypeForTest("receipt", "prebuilt-receipt", "ใบเสร็จรับเงิน\nร้านกาแฟ"));

    [Fact]
    public void ไม่มีข้อความเลย_ตกไปใช้ชนิดที่โมเดลบอก()
        => Assert.Equal("Receipt", OcrService.MapAzureDocTypeForTest("receipt", "prebuilt-receipt", null));
}

using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กติกา "หัวมีคำว่าใบกำกับภาษี → เลขชุด TIV เสมอ"
///
/// ═══ ที่มา ═══
/// ผู้ใช้รายงานว่า "เดี๋ยว TIV เดี๋ยว REC งง" — ใบที่หัวพิมพ์
/// "ใบกำกับภาษี/ใบเสร็จรับเงิน" เหมือนกันเป๊ะ ได้เลขคนละชุดแล้วแต่ทางที่กดเข้า
/// (ขายสด/จ่ายครบ → TIV · ใบเสร็จ standalone มี VAT / ใบกำกับ ณ วันรับเงิน
/// §78/1 → REC) และมีเคสกลับด้าน: TaxInvoice ที่ VAT=0 หัวพิมพ์ "ใบเสร็จรับเงิน"
/// แต่ได้เลข TIV-
///
/// เทสต์นี้ล็อกสิ่งที่ทำให้ regression กลับมาไม่ได้:
///   1. เลือก series จาก **บทบาททางกฎหมาย** ไม่ใช่ชนิดข้อมูล
///   2. **ไม่ยุบใบสำคัญรับ (RV) เข้า REC** — เป็นกระดาษคนละอย่าง
///   3. TaxInvoice ที่มี VAT ต้องเป็นใบกำกับเสมอ **แม้หัวถูก override** —
///      กันผู้ใช้ตั้งหัวเองแล้วเผลอลบคำนั้น ทำให้ใบกำกับตัวจริงหลุดเล่มหลัก
/// </summary>
public class TaxInvoiceSeriesPolicyTests
{
    private static Document Doc(DocumentType type, decimal vat = 107m) =>
        new() { DocumentType = type, VatAmount = vat };

    [Fact]
    public void ใบเสร็จที่เป็นใบกำกับณวันรับเงินต้องเข้าเล่ม_TIV()
    {
        // §78/1 — รับชำระใบแจ้งหนี้บริการครบงวดเดียว ใบเสร็จนั้นคือใบกำกับภาษี
        // เดิมได้เลข REC- ทั้งที่หัวพิมพ์ "ใบกำกับภาษี/ใบเสร็จรับเงิน"
        var d = Doc(DocumentType.Receipt);
        var carries = TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, "ใบกำกับภาษี/ใบเสร็จรับเงิน");
        Assert.True(carries);
        Assert.Equal(DocumentType.TaxInvoice, TaxInvoiceSeriesPolicy.SeriesTypeOverride(d, carries));
    }

    [Fact]
    public void ใบเสร็จเปล่าที่รับชำระใบกำกับต้องอยู่เล่ม_REC_ตามเดิม()
    {
        // VAT รายงานที่ใบกำกับต้นทางแล้ว หัวจึงเป็น "ใบเสร็จรับเงิน" เปล่า ๆ
        var d = Doc(DocumentType.Receipt, vat: 0m);
        var carries = TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, "ใบเสร็จรับเงิน");
        Assert.False(carries);
        // null = ไม่ override → ใช้ชุดของตัวเอง (REC)
        Assert.Null(TaxInvoiceSeriesPolicy.SeriesTypeOverride(d, carries));
    }

    [Fact]
    public void ใบกำกับที่_VAT_เป็นศูนย์ต้องหลุดออกจากเล่ม_TIV()
    {
        // เคสกลับด้าน: หัวพิมพ์ "ใบเสร็จรับเงิน" แต่เดิมได้เลข TIV-
        var d = Doc(DocumentType.TaxInvoice, vat: 0m);
        var carries = TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, "ใบเสร็จรับเงิน");
        Assert.False(carries);
        Assert.Equal(DocumentType.Receipt, TaxInvoiceSeriesPolicy.SeriesTypeOverride(d, carries));
    }

    [Fact]
    public void ใบกำกับที่มี_VAT_เป็นใบกำกับเสมอแม้หัวถูกตั้งเอง()
    {
        // พื้นบังคับ — ผู้ใช้ตั้ง template.CustomTitle เป็นอย่างอื่นแล้วเผลอลบคำว่า
        // "ใบกำกับภาษี" ต้องไม่ทำให้ใบกำกับตัวจริงหลุดออกจากเล่มหลัก
        var d = Doc(DocumentType.TaxInvoice);
        Assert.True(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, "บิลเงินสด"));
    }

    [Fact]
    public void ใบกำกับอย่างย่อยังเป็นใบกำกับ_อยู่เล่มเดียวกัน()
    {
        // §86/6 ใบกำกับอย่างย่อก็คือใบกำกับภาษีรูปแบบหนึ่ง
        var d = Doc(DocumentType.Receipt);
        Assert.True(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, "ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ"));
    }

    [Fact]
    public void ใบสำคัญรับที่ไม่ใช่ใบกำกับต้องไม่ถูกยุบเข้าเล่ม_REC()
    {
        // ใบสำคัญรับ (RV) เป็นกระดาษคนละอย่างกับใบเสร็จ — กติกานี้ไม่ควรไปยุบ
        // เล่มที่ไม่เกี่ยวข้องกัน
        var d = Doc(DocumentType.ReceiptVoucher, vat: 0m);
        var carries = TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, "ใบสำคัญรับ");
        Assert.False(carries);
        Assert.Null(TaxInvoiceSeriesPolicy.SeriesTypeOverride(d, carries));
    }

    [Fact]
    public void ใบสำคัญรับที่เก็บ_VAT_จริงต้องเข้าเล่ม_TIV()
    {
        var d = Doc(DocumentType.ReceiptVoucher);
        var carries = TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, "ใบกำกับภาษี/ใบเสร็จรับเงิน");
        Assert.True(carries);
        Assert.Equal(DocumentType.TaxInvoice, TaxInvoiceSeriesPolicy.SeriesTypeOverride(d, carries));
    }

    [Theory]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.BillingNote)]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.CreditNote)]
    public void ชนิดที่ไม่เกี่ยวกับกติกานี้ต้องไม่ถูกย้ายเล่ม(DocumentType type)
    {
        // ใบลดหนี้มีคำว่า "ใบกำกับ" ในเนื้อหาอ้างอิงได้ แต่เล่มของมันคือ CN
        // — กติกาครอบเฉพาะ TaxInvoice/Receipt/ReceiptVoucher เท่านั้น
        var d = Doc(type);
        Assert.Null(TaxInvoiceSeriesPolicy.SeriesTypeOverride(d, carriesTaxInvoiceRole: true));
        Assert.Null(TaxInvoiceSeriesPolicy.SeriesTypeOverride(d, carriesTaxInvoiceRole: false));
    }

    [Fact]
    public void ยังไม่เคยตั้งค่าต้องแปลว่าเปิด_ไม่ใช่ปิด()
    {
        // null = ยังไม่เคยตั้ง → ค่าแนะนำคือเปิด (ผู้ใช้ไม่ต้องไปหาสวิตช์เอง)
        Assert.True(TaxInvoiceSeriesPolicy.IsUnifiedSeriesEnabled(null));
        // ผู้ใช้ปลดติ๊กเอง → เก็บ false และต้องถูกเคารพตลอด ห้ามถูกทับกลับเป็นเปิด
        Assert.False(TaxInvoiceSeriesPolicy.IsUnifiedSeriesEnabled(false));
        Assert.True(TaxInvoiceSeriesPolicy.IsUnifiedSeriesEnabled(true));
    }

    [Fact]
    public void หัวที่ยังไม่ได้คำนวณ_ห้ามตีความว่าเป็นใบกำกับ()
    {
        // resolver ล้ม → null: ต้องตกกลับไปใช้ชนิดเอกสารตามเดิม ไม่ใช่เดาว่าใช่
        var d = Doc(DocumentType.Receipt);
        Assert.False(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, null));
    }

    // ═══ ด่าน "ใบแจ้งหนี้ที่หัวประกาศเป็นใบกำกับ" (รอบ 122) ═══
    // ที่มา: ApproveDocumentAsync ตรึง IsTaxInvoiceByLaw จากหัวให้ทุกชนิด แต่
    // SeriesTypeOverride จงใจข้าม Invoice ⇒ ใบแจ้งหนี้ที่หัวถูก override เป็น
    // "ใบแจ้งหนี้/ใบกำกับภาษี" จะประกาศตัวเป็นใบกำกับโดยถือเลข INV- นอกเล่ม

    [Fact]
    public void ใบแจ้งหนี้ที่หัวมีคำว่าใบกำกับ_ต้องถูกบล็อก()
    {
        Assert.True(TaxInvoiceSeriesPolicy.IsTaxTitleOnPlainInvoice(
            DocumentType.Invoice, "ใบแจ้งหนี้/ใบกำกับภาษี"));
        Assert.True(TaxInvoiceSeriesPolicy.IsTaxTitleOnPlainInvoice(
            DocumentType.Invoice, "ใบกำกับภาษี"));
    }

    [Fact]
    public void ใบแจ้งหนี้หัวปกติ_ไม่ถูกบล็อก()
    {
        Assert.False(TaxInvoiceSeriesPolicy.IsTaxTitleOnPlainInvoice(
            DocumentType.Invoice, "ใบแจ้งหนี้"));
        // resolver ล้ม (null) → ห้ามบล็อกจากการเดา
        Assert.False(TaxInvoiceSeriesPolicy.IsTaxTitleOnPlainInvoice(
            DocumentType.Invoice, null));
    }

    [Theory]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.ReceiptVoucher)]
    public void ชนิดที่เข้าเล่ม_TIV_ได้_ไม่เข้าด่านนี้(DocumentType type)
        => Assert.False(TaxInvoiceSeriesPolicy.IsTaxTitleOnPlainInvoice(
            type, "ใบกำกับภาษี/ใบเสร็จรับเงิน"));

    [Fact]
    public void ใบลดหนี้ที่พิมพ์คำว่าใบกำกับ_ห้ามถูกบล็อก()
    {
        // §86/9-10 ให้ถือว่า CN/DN เป็นใบกำกับภาษีอยู่แล้ว — บางกิจการพิมพ์หัว
        // "ใบลดหนี้ (ใบกำกับภาษี)" ซึ่งถูกกฎหมาย ด่านนี้ต้องไม่ไปยุ่ง
        Assert.False(TaxInvoiceSeriesPolicy.IsTaxTitleOnPlainInvoice(
            DocumentType.CreditNote, "ใบลดหนี้ (ใบกำกับภาษี)"));
        Assert.False(TaxInvoiceSeriesPolicy.IsTaxTitleOnPlainInvoice(
            DocumentType.DebitNote, "ใบเพิ่มหนี้/ใบกำกับภาษี"));
    }

    [Fact]
    public void ข้อความบล็อก_ต้องบอกทางไปต่อทั้งสองทาง()
    {
        // "สถานะปลายทางที่ผู้ใช้ไปต่อไม่ได้ = ฟีเจอร์ที่ยังไม่จบ"
        Assert.Contains("ใบแจ้งหนี้/ใบกำกับภาษี", TaxInvoiceSeriesPolicy.PlainInvoiceTaxTitleBlockedMessage);
        Assert.Contains("ตั้งค่า", TaxInvoiceSeriesPolicy.PlainInvoiceTaxTitleBlockedMessage);
    }
}

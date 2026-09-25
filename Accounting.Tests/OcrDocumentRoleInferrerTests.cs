using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "สแกนเอกสารแบบนี้ ควรสร้างเป็นเอกสารอะไร" — ตัวอนุมานบทบาท + ชนิดเป้าหมาย
///
/// ═══ ที่มา: ผลตรวจของทีมวิเคราะห์เอกสาร ═══
/// เคสที่เจ้าของระบบยกเป็นตัวอย่าง (ใบกำกับที่เรามีชื่อเป็นลูกค้า = ซื้อ →
/// ใบสำคัญจ่าย · ใบลดหนี้ที่เรามีชื่อเป็นลูกค้า → ใบลดหนี้ซื้อ) ทำงานอยู่แล้ว
/// แต่รอบตรวจพบเอกสารอีก 6 ชนิดที่ตกหลุม default ผิด และ 50 ทวิ ที่กลับทิศ
/// ทั้งสองทาง เทสต์ชุดนี้ล็อกพฤติกรรมที่ถูกต้องของทุกเคส
/// </summary>
public class OcrDocumentRoleInferrerTests
{
    private const string Us = "0205565017741";      // บจก.มังกร เซอร์วิส เอ็นจิเนียริ่ง
    private const string UsName = "บริษัท มังกร เซอร์วิส เอ็นจิเนียริ่ง จำกัด";
    private const string Vendor = "0105556012341";
    private const string VendorName = "บริษัท ผู้ขาย จำกัด";

    private static OcrDocumentRoleInferrer.InferenceResult Infer(
        string rawText, string? vendorTaxId = Vendor, string? buyerTaxId = Us,
        string? vendorName = VendorName, string? buyerName = UsName,
        int? paymentTermsDays = null)
        => OcrDocumentRoleInferrer.Infer(
            rawText, vendorTaxId, buyerTaxId, vendorName, buyerName,
            companyTaxId: Us, companyName: UsName, paymentTermsDays: paymentTermsDays);

    // ═══ บทบาทที่ชั้นบน (AI ผ่านด่าน) ตัดสินมาแล้ว ═══
    [Fact]
    public void roleOverride_ชนะการเดาจากชื่อ_แต่แพ้เลขภาษีที่ตรงกับเรา()
    {
        // ไม่มีเลขภาษีเลย · ชื่อเราอยู่ช่องผู้ขาย · AI บอกว่าเราเป็นผู้ซื้อ → ใช้ AI
        var byAi = OcrDocumentRoleInferrer.Infer("บิลเงินสด\n" + UsName + "\nรวม 3,500",
            vendorTaxId: null, buyerTaxId: null, vendorName: UsName, buyerName: null,
            companyTaxId: Us, companyName: UsName, roleOverride: "Buyer");
        Assert.Equal("Buyer", byAi.OurRole);
        Assert.True(byAi.RoleConfidence >= 0.9m);

        // เลขภาษีผู้ขายคือเรา (ตัวตนทางกฎหมาย) → AI เปลี่ยนไม่ได้
        var pinned = OcrDocumentRoleInferrer.Infer("ใบกำกับภาษี\nผู้ขาย " + UsName,
            vendorTaxId: Us, buyerTaxId: null, vendorName: UsName, buyerName: null,
            companyTaxId: Us, companyName: UsName, roleOverride: "Buyer");
        Assert.Equal("Seller", pinned.OurRole);
        Assert.Equal(1.0m, pinned.RoleConfidence);
    }

    // ═══ เคสที่เจ้าของระบบยกเป็นตัวอย่าง ═══

    [Fact]
    public void ใบกำกับภาษีที่เรามีชื่อเป็นลูกค้า_คือฝั่งซื้อ_สร้างใบสำคัญจ่าย()
    {
        var r = Infer("ใบกำกับภาษี\nผู้ขาย บริษัท ผู้ขาย จำกัด\nลูกค้า " + UsName + "\nรวม 1,070.00");
        Assert.Equal("Buyer", r.OurRole);
        Assert.Equal(1.0m, r.RoleConfidence);
        Assert.Equal(DocumentType.TaxInvoice, r.ScannedDocType);
        Assert.Equal(DocumentType.PaymentVoucher, r.TargetDocType);
        Assert.True(r.InputVatClaimable);
    }

    [Fact]
    public void ใบกำกับที่มีเครดิตเทอม_ยังไม่จ่าย_ต้องตั้งหนี้ไม่ใช่ใบสำคัญจ่าย()
    {
        var r = Infer("ใบกำกับภาษี\nลูกค้า " + UsName + "\nเครดิต 30 วัน\nรวม 1,070.00",
            paymentTermsDays: 30);
        Assert.Equal(DocumentType.PurchaseInvoice, r.TargetDocType);
    }

    [Fact]
    public void ใบลดหนี้ที่เรามีชื่อเป็นลูกค้า_คือใบลดหนี้ฝั่งซื้อแน่นอน()
    {
        var r = Infer("ใบลดหนี้\nผู้ขาย บริษัท ผู้ขาย จำกัด\nลูกค้า " + UsName + "\nลดหนี้ 500.00");
        Assert.Equal("Buyer", r.OurRole);
        Assert.Equal(DocumentType.CreditNote, r.TargetDocType);
        // ชนิด CN ใช้ enum เดียวทั้งสองฝั่ง — ฝั่งถูกตัดสินจากบทบาท
        Assert.True(DocumentSide.IsPurchase(r.TargetDocType, r.OurRole));
        Assert.False(DocumentSide.IsSales(r.TargetDocType, r.OurRole));
    }

    // ═══ 50 ทวิ — เดิมกลับทิศทั้งสองทาง ═══

    [Fact]
    public void หนังสือรับรองหักณที่จ่าย_เราอยู่ช่องผู้ถูกหัก_คือเราถูกหัก()
    {
        var r = Infer($"""
            หนังสือรับรองการหักภาษี ณ ที่จ่าย
            ผู้มีหน้าที่หักภาษี ณ ที่จ่าย
            บริษัท ผู้ว่าจ้าง จำกัด  เลขประจำตัวผู้เสียภาษีอากร {Vendor}
            ผู้ถูกหักภาษี ณ ที่จ่าย
            {UsName}  เลขประจำตัวผู้เสียภาษีอากร {Us}
            จำนวนเงินที่จ่าย 100,000.00  ภาษีที่หักและนำส่ง 3,000.00
            """, buyerTaxId: null, buyerName: null);
        Assert.True(r.IsWhtCertificate);
        Assert.True(r.WeAreWithheld);
        Assert.Equal("Seller", r.OurRole);
        // เครดิตภาษีของใบขายที่ออกไปแล้ว — **ห้าม**สร้างใบขายใบใหม่
        Assert.Equal(DocumentType.ReceiptVoucher, r.TargetDocType);
        Assert.NotEqual(DocumentType.Invoice, r.TargetDocType);
    }

    [Fact]
    public void หนังสือรับรองหักณที่จ่าย_เราอยู่ช่องผู้หัก_คือเราหักเขา()
    {
        var r = Infer($"""
            หนังสือรับรองการหักภาษี ณ ที่จ่าย
            ผู้มีหน้าที่หักภาษี ณ ที่จ่าย
            {UsName}  เลขประจำตัวผู้เสียภาษีอากร {Us}
            ผู้ถูกหักภาษี ณ ที่จ่าย
            บริษัท ผู้รับเหมา จำกัด  เลขประจำตัวผู้เสียภาษีอากร {Vendor}
            จำนวนเงินที่จ่าย 100,000.00  ภาษีที่หักและนำส่ง 3,000.00
            """, buyerTaxId: null, buyerName: null);
        Assert.True(r.IsWhtCertificate);
        Assert.False(r.WeAreWithheld);
        Assert.Equal("Buyer", r.OurRole);          // เราเป็นผู้จ่าย
        Assert.Equal(DocumentType.PaymentVoucher, r.TargetDocType);
    }

    [Fact]
    public void พบ50ทวิแต่แยกทิศไม่ได้_ห้ามเดาเป็นSeller()
    {
        // negative test — พฤติกรรมเดิมคือ "เจอ 50 ทวิ ⇒ Seller" เสมอ ซึ่งผิดครึ่งหนึ่ง
        var r = Infer("หนังสือรับรองการหักภาษี ณ ที่จ่าย\nยอดจ่าย 100,000\nภาษี 3,000",
            vendorTaxId: null, buyerTaxId: null, vendorName: null, buyerName: null);
        Assert.True(r.IsWhtCertificate);
        Assert.Null(r.WeAreWithheld);
        Assert.Equal("Buyer", r.OurRole);          // default เดิม ไม่ใช่การเดาจาก 50 ทวิ
    }

    // ═══ เอกสาร 4 ชนิดที่เดิมตกหลุม Expense ═══

    [Fact]
    public void ใบเสนอราคาที่ได้รับ_ยังไม่ใช่รายจ่าย_ห้ามสร้างเป็นค่าใช้จ่าย()
    {
        var r = Infer("ใบเสนอราคา\nผู้ขาย บริษัท ผู้ขาย จำกัด\nถึง " + UsName + "\nรวม 50,000.00");
        Assert.Equal("Buyer", r.OurRole);
        Assert.NotEqual(DocumentType.Expense, r.TargetDocType);
        Assert.Equal(DocumentType.PurchaseRequisition, r.TargetDocType);
    }

    [Fact]
    public void ใบวางบิลที่ได้รับ_คือเรียกเก็บของที่ส่งแล้ว_ตั้งหนี้()
    {
        var r = Infer("ใบวางบิล\nผู้ขาย บริษัท ผู้ขาย จำกัด\nลูกค้า " + UsName + "\nรวม 80,000.00");
        Assert.Equal(DocumentType.PurchaseInvoice, r.TargetDocType);
    }

    [Fact]
    public void ใบส่งของจากผู้ขาย_คือใบรับสินค้าสำหรับ3wayMatch()
    {
        var r = Infer("ใบส่งของ\nผู้ขาย บริษัท ผู้ขาย จำกัด\nผู้รับ " + UsName + "\nจำนวน 10 ชิ้น");
        Assert.Equal(DocumentType.GoodsReceiptNote, r.TargetDocType);
    }

    [Fact]
    public void สลิปโอนเงิน_เดิมไม่มีตัวจับเลย_ต้องเป็นใบสำคัญจ่าย()
    {
        var r = Infer("โอนเงินสำเร็จ\nพร้อมเพย์\nจำนวน 5,350.00 บาท\nไปยัง บริษัท ผู้ขาย จำกัด");
        Assert.Equal(DocumentType.PaymentVoucher, r.TargetDocType);
        Assert.NotEqual(DocumentType.Expense, r.TargetDocType);
    }

    // ═══ สแกนใบขายของเราเองกลับเข้ามา ═══

    [Fact]
    public void ใบกำกับที่เลขผู้ขายคือเราเอง_ต้องติดธงเตือนว่าอาจเป็นสำเนาของเรา()
    {
        var r = OcrDocumentRoleInferrer.Infer(
            "ใบกำกับภาษี\nผู้ขาย " + UsName + "\nลูกค้า บริษัท ลูกค้า จำกัด\nรวม 10,700.00",
            vendorTaxId: Us, buyerTaxId: "0994000158378",
            vendorName: UsName, buyerName: "บริษัท ลูกค้า จำกัด",
            companyTaxId: Us, companyName: UsName);
        Assert.Equal("Seller", r.OurRole);
        Assert.True(r.LikelyOurOwnIssuedDocument);
        Assert.Contains(r.Reasons, x => x.Contains("สำเนาเอกสารที่เราออกไปแล้ว"));
    }

    [Fact]
    public void ใบซื้อปกติ_ต้องไม่ติดธงเอกสารของเราเอง()
    {
        // negative test — ธงนี้ต้องไม่ขึ้นพร่ำเพรื่อ ไม่งั้นผู้ใช้เลิกอ่านคำเตือน
        var r = Infer("ใบกำกับภาษี\nลูกค้า " + UsName + "\nรวม 1,070.00");
        Assert.False(r.LikelyOurOwnIssuedDocument);
    }

    // ═══ ตัวตัดสินฝั่งกลาง (เดิมมี 3 ชุดที่ตอบไม่ตรงกัน) ═══

    [Theory]
    [InlineData(DocumentType.TaxInvoice, null, true)]
    [InlineData(DocumentType.Receipt, null, true)]
    [InlineData(DocumentType.PurchaseInvoice, null, false)]
    [InlineData(DocumentType.PaymentVoucher, null, false)]
    [InlineData(DocumentType.GoodsReceiptNote, null, false)]
    [InlineData(DocumentType.CertificateInLieu, null, false)]
    public void ชนิดที่ไม่กำกวม_ตอบฝั่งได้โดยไม่ต้องรู้บทบาท(
        DocumentType type, string? role, bool expectSales)
    {
        Assert.False(DocumentSide.IsAmbiguous(type));
        Assert.Equal(expectSales, DocumentSide.IsSales(type, role));
    }

    [Theory]
    [InlineData(DocumentType.CreditNote)]
    [InlineData(DocumentType.DebitNote)]
    [InlineData(DocumentType.DeliveryNote)]
    public void ชนิดสองฝั่ง_ต้องใช้บทบาทตัดสิน(DocumentType type)
    {
        Assert.True(DocumentSide.IsAmbiguous(type));
        Assert.True(DocumentSide.IsSales(type, "Seller"));
        Assert.False(DocumentSide.IsSales(type, "Buyer"));
        // ไม่รู้บทบาท → ถือเป็นฝั่งซื้อ (default ที่ปลอดภัยกว่า: ตีเป็นฝั่งขายผิด
        // จะไปโผล่ในรายงานภาษีขายที่ยื่นไปแล้ว)
        Assert.False(DocumentSide.IsSales(type, null));
    }

    [Fact]
    public void ด่านกันความรู้จากประวัติข้ามฝั่ง()
    {
        // prior ของ vendor รู้แค่ "มักลงเป็นอะไร" ไม่รู้ว่าใบนี้เราอยู่ฝั่งไหน
        Assert.False(DocumentSide.MatchesRole(DocumentType.TaxInvoice, "Buyer"));
        Assert.True(DocumentSide.MatchesRole(DocumentType.PaymentVoucher, "Buyer"));
        Assert.True(DocumentSide.MatchesRole(DocumentType.TaxInvoice, "Seller"));
        // ชนิดสองฝั่งไม่ขวาง · ไม่รู้บทบาทก็ไม่ขวาง
        Assert.True(DocumentSide.MatchesRole(DocumentType.CreditNote, "Buyer"));
        Assert.True(DocumentSide.MatchesRole(DocumentType.TaxInvoice, null));
    }

    // ── รอบ 195 (ใบ Scommerce): หมายเหตุ "ยกเลิกใบกำกับภาษีอย่างย่อ … ออกใบใหม่แทน" ไม่ใช่หลักฐานว่าเป็นใบอย่างย่อ ──
    private const string ScommerceCancelNote =
        "หมายเหตุ :เป็นการยกเลิกใบกำกับภาษีอย่างย่อเลขที่ D20260918000097 วันที่ 18/09/2026 และออกใบกำกับภาษีอิเล็กทรอนิกส์ฉบับใหม่แทน";

    [Fact]
    public void หมายเหตุยกเลิกใบอย่างย่อแล้วออกใบใหม่แทน_นับเป็นคำปฏิเสธ()
        => Assert.False(OcrDocumentRoleInferrer.ContainsAnyNotNegated(ScommerceCancelNote.ToLowerInvariant(), "ใบกำกับภาษีอย่างย่อ"));

    [Fact]
    public void ใบScommerce_ไม่มีเลขผู้ซื้อ_หมายเหตุยกเลิกใบอย่างย่อ_ยังเคลมภาษีซื้อได้()
    {
        // ไม่ส่งเลขผู้ซื้อ ⇒ ทางรอดเดิม (mod-11 ของเลขผู้ซื้อ) ปิด — เหลือแค่ตัวตรวจคำปฏิเสธ
        var r = OcrDocumentRoleInferrer.Infer(OcrPaperSamples.ScommerceLazada, "0105560113122", buyerTaxId: null,
            vendorName: "Scommerce (Thailand) Co., Ltd.", buyerName: "หจก.แอม แฮปปี้เนส",
            companyTaxId: "0203562005871", companyName: "หจก.แอม แฮปปี้เนส", roleOverride: "Buyer");
        Assert.Equal("Buyer", r.OurRole);
        Assert.True(r.InputVatClaimable);
        Assert.Null(r.InputVatClaimWarning);
    }

    [Fact]
    public void ทิศตรงข้าม_ใบอย่างย่อจริงยังถูกจับ()
    {
        Assert.True(OcrDocumentRoleInferrer.ContainsAnyNotNegated("ใบกำกับภาษีอย่างย่อ/ใบเสร็จรับเงิน\ntax# 0105556012341", "ใบกำกับภาษีอย่างย่อ"));
        Assert.True(OcrDocumentRoleInferrer.ContainsAnyNotNegated("abbreviated tax invoice\npos# 0012", "abbreviated tax invoice"));
        var r = OcrDocumentRoleInferrer.Infer("ใบกำกับภาษีอย่างย่อ/ใบเสร็จรับเงิน\nร้าน ก\nรวม 107.00", Vendor, buyerTaxId: null,
            vendorName: VendorName, buyerName: null, companyTaxId: Us, companyName: UsName, roleOverride: "Buyer");
        Assert.False(r.InputVatClaimable);
        Assert.Contains("อย่างย่อ", r.InputVatClaimWarning);
    }

    // ── รอบ 195 ฝ่ายค้าน C2 (§82/5(2)): "ยกเลิก" เป็นคำปฏิเสธเฉพาะประโยค "ยกเลิกใบนั้น … ออกฉบับใหม่แทน" ──────────
    [Fact]
    public void ยกเลิกไม่ได้บรรทัดก่อนหัวใบอย่างย่อ_ยังจับเป็นอย่างย่อ()
    {
        // ส่วนนำหน้า 13 ตัวอักษร "ยกเลิกไม่ได้\n" — รุ่นแรกนับเป็นคำปฏิเสธ ⇒ สลิปอย่างย่อจริงหลุดไปเคลมภาษีซื้อ
        const string slip = "สินค้าซื้อแล้วไม่รับคืน/ยกเลิกไม่ได้\nใบกำกับภาษีอย่างย่อ";
        Assert.True(OcrDocumentRoleInferrer.ContainsAnyNotNegated(slip, "ใบกำกับภาษีอย่างย่อ"));
        var r = OcrDocumentRoleInferrer.Infer(slip + "\nร้าน ก\nรวม 107.00", Vendor, buyerTaxId: null,
            vendorName: VendorName, buyerName: null, companyTaxId: Us, companyName: UsName, roleOverride: "Buyer");
        Assert.False(r.InputVatClaimable);
    }

    [Fact]
    public void ยกเลิกติดคำแต่ไม่มีคำว่าแทนหรือฉบับใหม่_ยังจับเป็นอย่างย่อ()
        => Assert.True(OcrDocumentRoleInferrer.ContainsAnyNotNegated(
            "ยกเลิกใบกำกับภาษีอย่างย่อได้ที่เคาน์เตอร์ภายใน 7 วัน", "ใบกำกับภาษีอย่างย่อ"));

    [Fact]
    public void ประโยคScommerceจริง_ยังนับเป็นคำปฏิเสธ_ทั้งไทยและอังกฤษ()
    {
        Assert.False(OcrDocumentRoleInferrer.ContainsAnyNotNegated(ScommerceCancelNote.ToLowerInvariant(), "ใบกำกับภาษีอย่างย่อ"));
        Assert.False(OcrDocumentRoleInferrer.ContainsAnyNotNegated(
            "remark: cancellation of abbreviated tax invoice no. d2026 and replaced by e-tax invoice", "abbreviated tax invoice"));
        // ยกเลิก…แทน ต้องอยู่บรรทัดเดียวกัน — "แทน" บรรทัดอื่นไม่นับ
        Assert.True(OcrDocumentRoleInferrer.ContainsAnyNotNegated(
            "ยกเลิกใบกำกับภาษีอย่างย่อได้ภายใน 7 วัน\nตัวแทนจำหน่าย", "ใบกำกับภาษีอย่างย่อ"));
    }
}

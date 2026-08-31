using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// จำลองใบกำกับ PI-20260820-0005 (Hardwarehouse → บจก.มังกร เซอร์วิส
/// เอ็นจิเนียริ่ง) ที่ผู้ใช้รายงานว่า "OCR เอกสารมาแล้วขึ้นเตือน ทั้ง ๆ ที่
/// เอกสารมีข้อมูลครบถูกต้อง"
///
/// ═══ คำเตือนสามข้อที่ขึ้นบนใบที่ถูกต้อง (reproduce ก่อนแก้ ตามกฎเหล็ก #4 G) ═══
///   1. "ไม่พบคำว่า ใบกำกับภาษี บนเอกสาร" — ทั้งที่หัวกระดาษพิมพ์
///      "ต้นฉบับใบส่งสินค้า/ต้นฉบับใบกำกับภาษี" (OCR คืนนิคหิตแยก + แทรกช่องว่าง)
///   2. "เลขผู้ซื้อในเอกสารไม่ตรงกับบริษัทนี้"
///   3. "ผู้ซื้อในเอกสาร (8885009199627) ไม่ตรงกับบริษัท (0205565017741) —
///      อาจอัพโหลดผิดบริษัท" ← ขัดกับกฎที่ 3 ที่บอกว่าเจอเลขผู้ซื้อแล้ว
///
/// ต้นเหตุ: เลข 8885009199627 คือบาร์โค้ดสินค้าที่ OCR อ่านเพี้ยน ซึ่งบังเอิญ
/// ผ่าน mod-11 ไทย และถูกหยิบมาเป็น "เลขผู้ซื้อ" เพราะโค้ดเดิมสมมติว่า
/// "ไม่เจอคำว่าผู้ซื้อ = ผู้ซื้ออยู่ท้ายหน้า"
/// </summary>
public class RdComplianceOcrNoiseTests
{
    private const string OurTaxId = "0205565017741";
    private const string GhostBuyer = "8885009199627";   // บาร์โค้ดที่ผ่าน mod-11 ไทย
    private const string SellerTaxId = "0105556012341";  // เลขผู้ขาย (ผ่าน checksum)

    /// <summary>ข้อความ OCR แบบที่เครื่องอ่านคืนมาจริง — หัวกระดาษใช้นิคหิต+สระอา
    /// แยก ("ใบกํากับภาษี") และมีช่องว่างแทรกกลางคำแบบ Tesseract ไทย</summary>
    private const string RawPaper = """
        ต้นฉบับใบส่งสินค้า/ต้นฉบับใบกํากับภาษี
        บริษัท ฮาร์ดแวร์เฮ้าส์ จำกัด
        เลขประจำตัวผู้เสียภาษีอากร 0105556012341 สำนักงานใหญ่
        เลขที่ HW-2569-0812   วันที่ 20/08/2569
        ชื่อ บริษัท มังกร เซอร์วิส เอ็นจิเนียริ่ง จำกัด
        เลขประจำตัวผู้เสียภาษีอากร 0205565017741 สำนักงานใหญ่
        รายการสินค้า
        8859991940534 สกรูเกลียวปล่อย 3x25 มม.        10  12.00   120.00
        8859991966695 พุกพลาสติก 6 มม.                20   3.50    70.00
        8859991446166 เทปพันเกลียว PTFE               15   9.00   135.00
        8859172200587 ข้องอ PVC 1/2 นิ้ว              30   7.00   210.00
        8885009199627 น็อตหัวหกเหลี่ยม M8             25  18.00   450.00
        รวมเป็นเงิน 985.00  ภาษีมูลค่าเพิ่ม 7% 68.95  จำนวนเงินรวมทั้งสิ้น 1,053.95
        """;

    private static Company Us() => new()
    {
        Id = Guid.NewGuid(),
        Name = "บริษัท มังกร เซอร์วิส เอ็นจิเนียริ่ง จำกัด",
        TaxId = OurTaxId,
        BranchCode = "00000",
    };

    private static Document PurchaseDoc() => new()
    {
        Id = Guid.NewGuid(),
        DocumentType = DocumentType.PurchaseInvoice,
        VatAmount = 68.95m,
    };

    private static OcrResultResponse Scan(string? buyerTaxId) => new(
        Id: Guid.NewGuid(), OriginalFileName: "hardwarehouse.jpg", ScanStatus: "Completed",
        DocumentType: "PurchaseInvoice", Confidence: 0.75m,
        ExtractedVendorName: "บริษัท ฮาร์ดแวร์เฮ้าส์ จำกัด", ExtractedVendorTaxId: SellerTaxId,
        ExtractedDocumentNumber: "HW-2569-0812",
        ExtractedDate: new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc),
        ExtractedSubTotal: 985.00m, ExtractedVatAmount: 68.95m, ExtractedTotalAmount: 1053.95m,
        MatchedContactId: null, CreatedDocumentId: null, ProcessedAt: DateTime.UtcNow,
        BuyerTaxId: buyerTaxId, RawTextContent: RawPaper);

    private static RdComplianceValidator.RuleResult Rule(
        List<RdComplianceValidator.RuleResult> rs, string code)
        => rs.Single(r => r.RuleCode == code);

    private static bool Has(List<RdComplianceValidator.RuleResult> rs, string code)
        => rs.Any(r => r.RuleCode == code);

    // ═══ คำเตือนที่ 1: หัวเอกสาร ═══

    [Fact]
    public void หัวกระดาษที่OCRคืนนิคหิตแยก_ต้องนับว่าพบคำว่าใบกำกับภาษี()
    {
        var r = RdComplianceValidator.RunRules(Scan(GhostBuyer), RawPaper, Us(), PurchaseDoc());
        var kw = Rule(r, "TAX_INVOICE_KEYWORD");
        Assert.True(kw.IsValid);
        Assert.Equal("Info", kw.Severity);
    }

    [Fact]
    public void หัวเอกสารภาษาอังกฤษที่มีช่องว่าง_ยังหาเจอหลังย่อข้อความ()
    {
        // ย่อทั้งสองฝั่ง ("tax invoice" → "taxinvoice") ไม่งั้นคำที่มีช่องว่างจะหลุด
        const string english = "ORIGINAL TAX INVOICE / RECEIPT\nSeller Tax ID 0105556012341\n"
            + "Buyer Tax ID 0205565017741\nTotal 1,053.95";
        var r = RdComplianceValidator.RunRules(Scan(null), english, Us(), PurchaseDoc());
        Assert.True(Rule(r, "TAX_INVOICE_KEYWORD").IsValid);
    }

    [Fact]
    public void กระดาษที่ไม่มีคำว่าใบกำกับภาษีจริงๆ_ยังต้องเตือนเหมือนเดิม()
    {
        // negative test — ถ้าผ่อนจนเตือนไม่ได้เลย ด่าน §86/4 ก็ไร้ค่า
        const string plain = "ใบส่งของชั่วคราว\nบริษัท ก จำกัด\nรวมเงิน 1,053.95 บาท\n"
            + "รายการสินค้าตามใบสั่งซื้อ เลขที่ PO-001 ส่งของวันที่ 20/08/2569 ครบถ้วน";
        var r = RdComplianceValidator.RunRules(Scan(null), plain, Us(), PurchaseDoc());
        var kw = Rule(r, "TAX_INVOICE_KEYWORD");
        Assert.False(kw.IsValid);
        Assert.Equal("Warning", kw.Severity);
    }

    // ═══ คำเตือนที่ 3: ห้ามฟันธงว่าอัพโหลดผิดบริษัท ═══

    [Fact]
    public void บาร์โค้ดหลุดเข้าช่องผู้ซื้อ_ห้ามเตือนว่าอัพโหลดผิดบริษัท()
    {
        var r = RdComplianceValidator.RunRules(Scan(GhostBuyer), RawPaper, Us(), PurchaseDoc());

        // ต้องไม่มี Error ที่บอกว่า "อาจอัพโหลดผิดบริษัท"
        Assert.DoesNotContain(r, x => x.RuleCode == "TENANT_BUYER_MISMATCH" && !x.IsValid);
        // แต่ต้องไม่เงียบสนิท — บอกตรง ๆ ว่าอ่านผิดช่อง พร้อมเลขที่ถูกต้อง
        var misread = Rule(r, "BUYER_TAX_ID_MISREAD");
        Assert.Equal("Warning", misread.Severity);
        Assert.Contains(OurTaxId, misread.Message!);
        Assert.Contains(OurTaxId, misread.Action!);
    }

    [Fact]
    public void กฎที่3กับกฎที่7ต้องไม่ขัดกันเองบนใบเดียวกัน()
    {
        var r = RdComplianceValidator.RunRules(Scan(GhostBuyer), RawPaper, Us(), PurchaseDoc());
        // กฎที่ 3 บอกว่าเจอเลขบริษัทเราบนกระดาษ ⇒ กฎที่ 7 จะบอกว่าผิดบริษัทไม่ได้
        Assert.True(Rule(r, "BUYER_TAX_ID_FORMAT").IsValid);
        Assert.False(Has(r, "TENANT_BUYER_MISMATCH") && !Rule(r, "TENANT_BUYER_MISMATCH").IsValid);
    }

    [Fact]
    public void เลขผู้ซื้อตรงกับบริษัท_ผ่านสะอาดไม่มีคำเตือนเลย()
    {
        var r = RdComplianceValidator.RunRules(Scan(OurTaxId), RawPaper, Us(), PurchaseDoc());
        Assert.True(Rule(r, "TENANT_BUYER_MISMATCH").IsValid);
        Assert.False(Has(r, "BUYER_TAX_ID_MISREAD"));
        Assert.DoesNotContain(r, x => !x.IsValid);      // ใบที่ถูกต้อง = ไม่มีคำเตือนสักข้อ
    }

    [Fact]
    public void อัพโหลดผิดบริษัทของจริง_ยังต้องเตือนเป็นError()
    {
        // negative test — เลขผู้ซื้อเป็นบริษัทอื่นจริง และเลขเราไม่อยู่บนกระดาษเลย
        const string otherPaper = """
            ต้นฉบับใบกำกับภาษี
            เลขประจำตัวผู้เสียภาษีอากร 0105556012341
            ชื่อผู้ซื้อ บริษัท อื่น จำกัด
            เลขประจำตัวผู้เสียภาษีอากร 0994000158378
            รวมทั้งสิ้น 1,053.95
            """;
        var r = RdComplianceValidator.RunRules(
            Scan("0994000158378"), otherPaper, Us(), PurchaseDoc());
        var mismatch = Rule(r, "TENANT_BUYER_MISMATCH");
        Assert.False(mismatch.IsValid);
        Assert.Equal("Error", mismatch.Severity);
    }

    [Fact]
    public void เอกสารฝั่งขาย_ผู้ซื้อคือลูกค้า_ห้ามเตือนว่าผิดบริษัท()
    {
        // ใบกำกับที่เราออกให้ลูกค้า — เลขผู้ซื้อไม่มีวันตรงกับเรา และนั่นถูกต้องแล้ว
        var salesDoc = new Document
        {
            Id = Guid.NewGuid(), DocumentType = DocumentType.TaxInvoice, VatAmount = 68.95m,
        };
        var r = RdComplianceValidator.RunRules(
            Scan("0994000158378"), RawPaper, Us(), salesDoc);
        Assert.True(Rule(r, "TENANT_BUYER_MISMATCH").IsValid);
    }

    // ═══ ต้นเหตุ: ตัวสกัดต้องไม่หยิบบาร์โค้ดมาเป็นเลขผู้เสียภาษีตั้งแต่แรก ═══

    [Fact]
    public void บาร์โค้ดที่ผ่านmod11ไทยด้วย_ต้องถูกคัดออกตั้งแต่ชั้นผู้สมัคร()
    {
        var ids = SmartFieldExtractor.ExtractValidThaiTaxIds(RawPaper).Select(x => x.Id).ToList();
        Assert.Contains(SellerTaxId, ids);
        Assert.Contains(OurTaxId, ids);
        // 8859991446166 ผ่านทั้ง mod-11 ไทยและ EAN-13 + prefix 885 = บาร์โค้ดชัดเจน
        Assert.DoesNotContain("8859991446166", ids);
    }

    [Fact]
    public void เลขที่มีป้ายนำหน้าชนะเลขที่ไม่มีป้าย_นี่คือด่านที่กันบาร์โค้ดที่checksumตรงบังเอิญ()
    {
        // 8885009199627 ผ่าน mod-11 ไทยแต่ไม่ผ่าน EAN-13 ⇒ ด่านบาร์โค้ดจับไม่ได้
        // (ตั้งใจไม่ตัดกว้างกว่านี้ เพราะจะไปทิ้งเลขบุคคลที่ถูกต้อง) — ตัวที่กันคือ
        // "มีป้ายเลขประจำตัวผู้เสียภาษีนำหน้าไหม" ซึ่งหนักกว่าคณิตศาสตร์
        var cands = SmartFieldExtractor.ExtractTaxIdCandidates(RawPaper);
        Assert.True(cands.Single(c => c.Id == OurTaxId).Labelled);
        Assert.True(cands.Single(c => c.Id == SellerTaxId).Labelled);
        Assert.False(cands.Single(c => c.Id == GhostBuyer).Labelled);
    }

    [Fact]
    public void ผลลัพธ์สุดท้ายของใบที่เกิดปัญหา_บาร์โค้ดต้องไม่กลายเป็นเลขผู้ซื้อ()
    {
        // เทสต์ปลายทาง: ให้ข้อความกระดาษจริงเข้าไป แล้วดูว่าช่องผู้ซื้อได้เลขอะไร
        var data = new Accounting.Services.Implementations.OcrExtractedData();
        SmartFieldExtractor.Enrich(data, RawPaper);
        Assert.Equal(SellerTaxId, data.VendorTaxId);
        Assert.NotEqual(GhostBuyer, data.BuyerTaxId);    // ⬅ บั๊กเดิมอยู่ตรงนี้
    }

    [Fact]
    public void ไม่มีคำว่าผู้ซื้อบนกระดาษ_ห้ามเดาเลขท้ายหน้าเป็นเลขผู้ซื้อ()
    {
        // กระดาษมีเลขผู้ขายอย่างเดียว + มีบาร์โค้ดท้ายหน้า — เดิม buyerPos ถูก
        // ปลอมเป็น text.Length ⇒ เลขท้ายสุดกลายเป็นเลขผู้ซื้อทุกครั้ง
        const string noBuyer = """
            ใบกำกับภาษี
            เลขประจำตัวผู้เสียภาษีอากร 0105556012341
            8885009199627 น็อตหัวหกเหลี่ยม M8   25  18.00  450.00
            รวมทั้งสิ้น 1,053.95
            """;
        var data = new Accounting.Services.Implementations.OcrExtractedData();
        SmartFieldExtractor.Enrich(data, noBuyer);
        Assert.Equal(SellerTaxId, data.VendorTaxId);
        Assert.Null(data.BuyerTaxId);
    }

    [Fact]
    public void มีคำว่าผู้ซื้อบนกระดาษ_ยังแยกผู้ขายกับผู้ซื้อได้ถูกต้องเหมือนเดิม()
    {
        var data = new Accounting.Services.Implementations.OcrExtractedData();
        SmartFieldExtractor.Enrich(data, """
            ใบกำกับภาษี
            ผู้ขาย บริษัท ฮาร์ดแวร์เฮ้าส์ จำกัด
            เลขประจำตัวผู้เสียภาษีอากร 0105556012341
            ผู้ซื้อ บริษัท มังกร เซอร์วิส เอ็นจิเนียริ่ง จำกัด
            เลขประจำตัวผู้เสียภาษีอากร 0205565017741
            รวมทั้งสิ้น 1,053.95
            """);
        Assert.Equal(SellerTaxId, data.VendorTaxId);
        Assert.Equal(OurTaxId, data.BuyerTaxId);
    }
}

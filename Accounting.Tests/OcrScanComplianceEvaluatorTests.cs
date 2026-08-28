using Accounting.Models.DTOs.Ocr;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำเตือนบนการ์ดผลสแกน — ต้องพูดตรงกับ <c>RdComplianceValidator</c> เสมอ
///
/// ═══ ที่มา: ใบเสร็จ/ใบกำกับภาษี หจก.สหกลชลบุรี เล่ม 007 เลขที่ 0339 ═══
/// กระดาษพิมพ์เลขผู้ซื้อ "0 2055 65017 74 1" ไว้เต็ม ๆ แต่การ์ดเตือนว่า
/// "ใบกำกับภาษีตั้งแต่ 1,000 บาท ควรระบุเลขผู้เสียภาษีของผู้ซื้อ" — เพราะกฎ
/// ชุดนี้ถูกคัดลอกไปเขียนใหม่ใน JS ของ document-scan.html แล้วตัดสินจากช่อง
/// ที่ OCR เดามาช่องเดียว ไม่เคยเปิดดูข้อความบนกระดาษ (defect class เดียวกับ
/// Rule 7 — กฎที่มองข้อมูลคนละชุดจะเถียงกันเองต่อหน้าผู้ใช้)
/// </summary>
public class OcrScanComplianceEvaluatorTests
{
    private const string OurTaxId = "0205565017741";
    private const string SellerTaxId = "0203514000083";   // หจก.สหกลชลบุรี (เลขจริงบนใบ)

    /// <summary>ข้อความอย่างที่ OCR อ่านได้จากแบบฟอร์มจริง — เลขผู้ซื้ออยู่
    /// **บรรทัดเหนือ**ป้าย เพราะแบบฟอร์มมีเส้นประให้เขียนแล้วป้ายอยู่ใต้เส้น</summary>
    private const string RawPaper = """
        ห้างหุ้นส่วนจำกัด สหกลชลบุรี
        สำนักงานใหญ่ : 66/17 หมู่ 4 ถนนสุขุมวิท ก.ม.101 ต.เสม็ด อ.เมืองชลบุรี จ.ชลบุรี
        เลขประจำตัวผู้เสียภาษีอากร 0203514000083
        เล่มที่ 007   ใบเสร็จรับเงิน/ใบกำกับภาษี   เลขที่ 0339
        วันที่ 27 สิงหาคม 2569
        นามผู้ซื้อ บริษัท มังกร เซอร์วิส เอ็นจิเนียริ่ง จำกัด (สำนักงานใหญ่)
        ที่อยู่ 44 หมู่ที่ 9 ตำบลหนองเหียง อำเภอพนัสนิคม จังหวัดชลบุรี 20140
        0 2055 65017 74 1
        .............................เลขประจำตัวผู้เสียภาษีอากร.............................
        แผ่นอลูมิเนียม   2   3,000.00   6,000.00
        รวมราคาสินค้า 6,000.00  ภาษีมูลค่าเพิ่ม 7% 420.00  จำนวนเงินรวมทั้งสิ้น 6,420.00
        """;

    private static OcrResultResponse Scan(
        string? buyerTaxId = null, string? rawText = RawPaper,
        string docType = "TaxInvoice", decimal? total = 6420.00m,
        string? ourRole = "Buyer") => new(
            Id: Guid.NewGuid(), OriginalFileName: "CCF_000076_page-0003.jpg",
            ScanStatus: "Completed", DocumentType: docType, Confidence: 0.75m,
            ExtractedVendorName: "ห้างหุ้นส่วนจำกัด สหกลชลบุรี",
            ExtractedVendorTaxId: SellerTaxId, ExtractedDocumentNumber: "0339",
            ExtractedDate: new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc),
            ExtractedSubTotal: 6000m, ExtractedVatAmount: 420m, ExtractedTotalAmount: total,
            MatchedContactId: null, CreatedDocumentId: null, ProcessedAt: DateTime.UtcNow,
            RawTextContent: rawText, BuyerTaxId: buyerTaxId, OurRole: ourRole);

    // ═══ เคสที่ผู้ใช้รายงาน ═══

    [Fact]
    public void เลขผู้ซื้ออยู่บนกระดาษแต่OCRไม่ได้แยกช่อง_ห้ามเตือนว่าควรระบุเลขผู้ซื้อ()
    {
        var issues = OcrScanComplianceEvaluator.Evaluate(Scan(), OurTaxId);
        Assert.DoesNotContain(issues, i => i.Message.Contains("ควรระบุเลขผู้เสียภาษีของผู้ซื้อ"));
    }

    [Fact]
    public void ใบที่ครบถ้วนจริง_ไม่มีคำเตือนสักข้อ()
        => Assert.Empty(OcrScanComplianceEvaluator.Evaluate(Scan(), OurTaxId));

    [Fact]
    public void กระดาษไม่มีเลขผู้ซื้อจริง_ยังต้องเตือนเหมือนเดิม()
    {
        // negative test — ถ้าผ่อนจนเตือนไม่ได้เลย ด่าน §86/4 ก็ไร้ค่า
        const string noBuyerId = """
            ห้างหุ้นส่วนจำกัด สหกลชลบุรี
            เลขประจำตัวผู้เสียภาษีอากร 0203514000083
            ใบเสร็จรับเงิน/ใบกำกับภาษี เลขที่ 0339
            นามผู้ซื้อ บริษัท มังกร เซอร์วิส เอ็นจิเนียริ่ง จำกัด
            จำนวนเงินรวมทั้งสิ้น 6,420.00
            """;
        var issues = OcrScanComplianceEvaluator.Evaluate(Scan(rawText: noBuyerId), OurTaxId);
        Assert.Contains(issues, i => i.Message.Contains("ควรระบุเลขผู้เสียภาษีของผู้ซื้อ"));
    }

    // ═══ ต้องพูดตรงกับ RdComplianceValidator ═══

    [Fact]
    public void บาร์โค้ดหลุดเข้าช่องผู้ซื้อ_เตือนว่าอ่านผิดช่องไม่ใช่ผิดบริษัท()
    {
        var issues = OcrScanComplianceEvaluator.Evaluate(Scan(buyerTaxId: "8885009199627"), OurTaxId);
        Assert.DoesNotContain(issues, i => i.Severity == "error");
        Assert.Contains(issues, i => i.Message.Contains("อ่านผิดช่อง"));
    }

    [Fact]
    public void อัพโหลดผิดบริษัทของจริง_ยังต้องเป็นerror()
    {
        const string otherPaper = """
            ห้างหุ้นส่วนจำกัด สหกลชลบุรี
            เลขประจำตัวผู้เสียภาษีอากร 0203514000083
            ใบเสร็จรับเงิน/ใบกำกับภาษี เลขที่ 0339
            นามผู้ซื้อ บริษัท อื่น จำกัด
            เลขประจำตัวผู้เสียภาษีอากร 0994000158378
            จำนวนเงินรวมทั้งสิ้น 6,420.00
            """;
        var issues = OcrScanComplianceEvaluator.Evaluate(
            Scan(buyerTaxId: "0994000158378", rawText: otherPaper), OurTaxId);
        Assert.Contains(issues, i => i.Severity == "error" && i.Message.Contains("บริษัทอื่น"));
    }

    [Fact]
    public void เอกสารฝั่งขาย_ผู้ซื้อคือลูกค้า_ห้ามเตือนว่าผิดบริษัท()
    {
        var issues = OcrScanComplianceEvaluator.Evaluate(
            Scan(buyerTaxId: "0994000158378", ourRole: "Seller"), OurTaxId);
        Assert.DoesNotContain(issues, i => i.Severity == "error");
    }

    // ═══ ช่องพื้นฐาน — ต้องยังเตือนได้ตามเดิม ═══

    [Fact]
    public void ยอดต่ำกว่า1000_ไม่บังคับเลขผู้ซื้อ()
    {
        const string cheap = "ใบกำกับภาษี\nเลขประจำตัวผู้เสียภาษีอากร 0203514000083\nรวม 500.00";
        var issues = OcrScanComplianceEvaluator.Evaluate(
            Scan(rawText: cheap, total: 500m), OurTaxId);
        Assert.DoesNotContain(issues, i => i.Message.Contains("ควรระบุเลขผู้เสียภาษีของผู้ซื้อ"));
    }

    [Fact]
    public void สแกนยังไม่เสร็จ_ยังไม่ต้องเตือนอะไร()
    {
        var pending = Scan() with { ScanStatus = "Processing" };
        Assert.Empty(OcrScanComplianceEvaluator.Evaluate(pending, OurTaxId));
    }

    // ═══ ต้นเหตุ: ป้ายกำกับอยู่ "หลัง" ค่าบนแบบฟอร์มพิมพ์สำเร็จ ═══

    [Fact]
    public void เลขที่มีป้ายอยู่บรรทัดถัดไป_ต้องนับว่ามีป้ายด้วย()
    {
        // บั๊กที่เกิดจากการแก้รอบก่อน: มองย้อนหลังอย่างเดียว ⇒ เลขผู้ซื้อบน
        // แบบฟอร์มที่ป้ายอยู่ใต้เส้นประ ถูกตัดสินว่า "ไม่มีป้าย" แล้วแพ้เลขผู้ขาย
        var cands = SmartFieldExtractor.ExtractTaxIdCandidates(RawPaper);
        Assert.True(cands.Single(c => c.Id == OurTaxId).Labelled);       // ป้ายอยู่ข้างหลัง
        Assert.True(cands.Single(c => c.Id == SellerTaxId).Labelled);    // ป้ายอยู่ข้างหน้า
    }

    [Fact]
    public void แยกผู้ขายกับผู้ซื้อบนแบบฟอร์มนี้ได้ถูกต้อง()
    {
        var data = new Accounting.Services.Implementations.OcrExtractedData();
        SmartFieldExtractor.Enrich(data, RawPaper);
        Assert.Equal(SellerTaxId, data.VendorTaxId);
        Assert.Equal(OurTaxId, data.BuyerTaxId);   // "นามผู้ซื้อ" เป็นสมอ
    }
}

using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตาราง <c>OcrLearnedPatterns</c> — เทสต์ที่พิสูจน์ว่า <b>ฝั่งอ่าน</b> ทำงาน
///
/// ═══ ที่มา (defect class ที่ใหญ่ที่สุดในเรพ) ═══
/// แพตเทิร์นถูก<b>เขียน</b>ทุกครั้งที่ผู้ใช้แก้ผลสแกน และทุกครั้งที่สแกนผ่าน
/// Azure DI (<c>AzureDiPatternLearner</c> ที่ doc-comment ของตัวเองเขียนว่า
/// "DocumentZoneAnalyzer.ApplyLearnedPatterns picks them up") — แต่ทางเข้าเดียว
/// ที่อ่านตารางนี้ (<c>DocumentZoneAnalyzer.Analyze</c>) <b>ไม่มี call site
/// ทั้งเรพ</b> ⇒ write-only มาตลอด: จ่ายค่าเขียนทุกการแก้ แต่ความแม่นไม่เคย
/// ดีขึ้นเลย
///
/// CLAUDE.md ระบุไว้ตรง ๆ ว่า "เมื่อเขียนตัวเรียนรู้ตัวใหม่ ต้องเขียนเทสต์ที่
/// พิสูจน์ว่า**ฝั่งอ่านถูกเรียกจริง** ไม่ใช่แค่ฝั่งเขียนทำงาน" — นี่คือเทสต์นั้น
/// </summary>
public class LearnedPatternReadPathTests
{
    private static OcrLearnedPattern P(string field, string keyword, string regex) => new()
    {
        FieldName = field,
        ContextKeyword = keyword,
        ExtractionRegex = regex,
        SearchRadius = 300,
        TimesConfirmed = 5,
    };

    [Fact]
    public void เติมเลขที่เอกสารที่_engine_อ่านไม่ได้()
    {
        var data = new OcrExtractedData();     // engine อ่านอะไรไม่ได้เลย
        var raw = "ใบกำกับภาษี\nเลขที่เอกสาร RT69/00013\nวันที่ 15/09/2569";

        var filled = DocumentZoneAnalyzer.ApplyLearnedPatternsTo(
            data, raw, new[] { P("DocumentNumber", "เลขที่เอกสาร", @"เลขที่เอกสาร\s*([A-Z0-9/\-]+)") });

        Assert.Equal("RT69/00013", data.DocumentNumber);
        Assert.Contains("เลขที่เอกสาร", filled);
    }

    [Fact]
    public void ห้ามทับค่าที่_engine_อ่านได้แล้ว()
    {
        // แพตเทิร์นเป็นตัวเติมช่องว่าง ไม่ใช่ตัวตัดสินที่ชนะ OCR
        var data = new OcrExtractedData { DocumentNumber = "INV-001" };
        var raw = "เลขที่เอกสาร RT69/00013";

        var filled = DocumentZoneAnalyzer.ApplyLearnedPatternsTo(
            data, raw, new[] { P("DocumentNumber", "เลขที่เอกสาร", @"เลขที่เอกสาร\s*([A-Z0-9/\-]+)") });

        Assert.Equal("INV-001", data.DocumentNumber);
        Assert.Empty(filled);
    }

    [Fact]
    public void เลขผู้เสียภาษีที่ไม่ผ่าน_checksum_ต้องไม่ถูกเติม()
    {
        // แพตเทิร์นที่เรียนมาผิด/บาร์โค้ดที่อ่านเพี้ยน ต้องไม่หลุดเข้าช่องภาษี
        var data = new OcrExtractedData();
        var raw = "เลขประจำตัวผู้เสียภาษี 1234567890123";

        var filled = DocumentZoneAnalyzer.ApplyLearnedPatternsTo(
            data, raw, new[] { P("SellerTaxId", "เลขประจำตัวผู้เสียภาษี", @"(\d{13})") });

        Assert.Null(data.VendorTaxId);
        Assert.Empty(filled);
    }

    [Fact]
    public void เลขผู้เสียภาษีที่ถูกต้องถูกเติมและ_normalize()
    {
        var data = new OcrExtractedData();
        // เลขจริงของบริษัทในเคสทดสอบชุด ThaiTaxIdTests
        var raw = "เลขประจำตัวผู้เสียภาษี 0-2055-65017-74-1";

        var filled = DocumentZoneAnalyzer.ApplyLearnedPatternsTo(
            data, raw, new[] { P("SellerTaxId", "เลขประจำตัวผู้เสียภาษี", @"([0-9\- ]{13,20})") });

        Assert.Equal("0205565017741", data.VendorTaxId);
        Assert.Contains("เลขผู้เสียภาษีผู้ขาย", filled);
    }

    [Fact]
    public void negative_example_ต้องไม่ถูกใช้เติมค่า()
    {
        // แถว negative แปลว่า "ค่านี้เคยผิด" ไม่ใช่ "ค่านี้ถูก"
        var data = new OcrExtractedData();
        var p = P("DocumentNumber", "เลขที่", @"เลขที่\s*([A-Z0-9/\-]+)");
        p.IsNegativeExample = true;

        var filled = DocumentZoneAnalyzer.ApplyLearnedPatternsTo(
            data, "เลขที่ AB-1", new[] { p });

        Assert.Null(data.DocumentNumber);
        Assert.Empty(filled);
    }

    [Fact]
    public void regex_ที่เก็บไว้เสียต้องไม่ทำให้ทั้งการสแกนล้ม()
    {
        var data = new OcrExtractedData();
        var filled = DocumentZoneAnalyzer.ApplyLearnedPatternsTo(
            data, "เลขที่ AB-1", new[] { P("DocumentNumber", "เลขที่", "([unclosed") });
        Assert.Empty(filled);
    }

    [Fact]
    public void ยอดรวมที่อ่านได้ต้องมากกว่าศูนย์()
    {
        var data = new OcrExtractedData();
        var filled = DocumentZoneAnalyzer.ApplyLearnedPatternsTo(
            data, "รวมทั้งสิ้น 0.00", new[] { P("TotalAmount", "รวมทั้งสิ้น", @"รวมทั้งสิ้น\s*([\d,\.]+)") });
        Assert.Null(data.TotalAmount);
        Assert.Empty(filled);
    }
}

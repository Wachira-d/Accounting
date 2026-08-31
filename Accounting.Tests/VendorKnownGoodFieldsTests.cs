using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คลังค่า "ที่รู้ว่าถูก" ต่อผู้ขาย — ช่องไหนเอาไปทับผลอ่าน OCR ได้
///
/// ═══ ที่มา ═══
/// <c>VendorKnownGoodCorrector</c> เคย fuzzy-match <b>เลขที่เอกสาร</b> ของใบที่
/// กำลังสแกน กับเลขที่ของใบ<b>ก่อนหน้า</b>ของผู้ขายรายเดียวกัน แล้วแทนที่เมื่อ
/// similarity ≥ 0.80 — แต่เลขที่เอกสารเป็นค่า "ต่อใบ" ไม่ใช่ "ต่อผู้ขาย"
/// เลขรันติดกันต่างกันหลักเดียวจึงเกินเกณฑ์เสมอ ⇒ ใบใหม่ถือเลขของใบเก่า
/// ⇒ รายงานภาษีซื้อ §87 ยื่นเลขใบกำกับผิด + ด่านกันสแกนซ้ำตีว่าเป็นใบเดิม
///
/// เทสต์ชุดนี้ทำสองอย่าง: (1) พิสูจน์ว่า similarity ของเลขจริง**เกินเกณฑ์จริง**
/// (ถ้าข้อนี้ไม่แดงตอนเอา guard ออก แปลว่าจำลองผิด) (2) ล็อกว่า guard ปิดช่อง
/// เหล่านั้นไว้
/// </summary>
public class VendorKnownGoodFieldsTests
{
    private const double Threshold = 0.80;   // ตรงกับ VendorKnownGoodCorrector

    [Theory]
    // เลขที่เอกสารของผู้ขายรายเดียวกัน ใบก่อนหน้า vs ใบที่กำลังสแกน
    [InlineData("INV-2026-00123", "INV-2026-00124")]
    [InlineData("IV6808/0041", "IV6808/0042")]
    [InlineData("2026/08/0156", "2026/08/0157")]
    [InlineData("A00122", "A00123")]
    [InlineData("6808-00041", "6808-00042")]
    public void เลขที่เอกสารใบติดกัน_similarity_เกินเกณฑ์เสมอ_จึงห้ามใช้แก้(string previous, string current)
    {
        var sim = FuzzyMatcher.Similarity(previous, current);
        Assert.True(sim >= Threshold,
            $"เลข {previous} vs {current} ได้ {sim:F3} — ถ้าต่ำกว่าเกณฑ์แปลว่าเคสนี้ไม่ใช่ตัวแทนบั๊ก");
        Assert.NotEqual(previous, current);   // คนละใบจริง ๆ
    }

    [Theory]
    // รหัสสาขา §86/4 5 หลัก: ต่างกัน 1 ตัว ⇒ ratio = 1 − 1/5 = 0.80 พอดี
    [InlineData("00000", "00001")]
    [InlineData("00001", "00002")]
    [InlineData("00011", "00012")]
    public void รหัสสาขาต่างกันหลักเดียว_ก็เกินเกณฑ์_จึงห้ามใช้แก้(string a, string b)
        => Assert.True(FuzzyMatcher.Similarity(a, b) >= Threshold);

    [Fact]
    public void ช่องที่แก้ได้ต้องเป็นข้อความที่คงที่ต่อผู้ขายเท่านั้น()
    {
        Assert.True(VendorKnownGoodFields.IsCorrectable("SellerName"));
        Assert.True(VendorKnownGoodFields.IsCorrectable("VendorAddress"));
    }

    [Theory]
    [InlineData("DocumentNumber")]      // ต่อใบ — ต้นเหตุของบั๊ก
    [InlineData("VendorBranchCode")]    // ต่อสาขา + ตัวเลขล้วนที่ 5 หลักชนเกณฑ์พอดี
    [InlineData("VendorPhone")]         // ตัวเลขล้วน — ไม่มี "การสะกดผิด" ให้ซ่อม
    [InlineData("VendorEmail")]
    [InlineData("SellerTaxId")]
    [InlineData("BuyerName")]
    [InlineData("BuyerTaxId")]
    public void ช่องที่เหลือห้ามเอาไปทับผลอ่าน(string field)
        => Assert.False(VendorKnownGoodFields.IsCorrectable(field));

    [Fact]
    public void เลขที่เอกสารห้ามเก็บลงคลังค่าประจำผู้ขายตั้งแต่แรก()
    {
        // ฝั่งเขียนต้องข้าม — ไม่งั้นได้ 1 แถวต่อ 1 ใบตลอดไป (คลังบวม + เชื้อบั๊ก)
        Assert.True(VendorKnownGoodFields.IsPerDocument("DocumentNumber"));
        Assert.False(VendorKnownGoodFields.IsPerDocument("SellerName"));
        Assert.False(VendorKnownGoodFields.IsPerDocument("VendorAddress"));
    }

    [Fact]
    public void การแก้ที่ถูกต้องต้องยังทำงาน_ชื่อผู้ขายที่_OCR_อ่านเพี้ยน()
    {
        // เคสที่ threshold 0.80 ถูกตั้งมาเพื่อจับ (ระบุใน doc-comment ของ corrector)
        var sim = FuzzyMatcher.Similarity("หจก. แอมแฮปปี๊เนส", "หจก . แอมแฮปปี้เนสล");
        Assert.True(sim >= Threshold);
        Assert.True(VendorKnownGoodFields.IsCorrectable("SellerName"));
    }
}

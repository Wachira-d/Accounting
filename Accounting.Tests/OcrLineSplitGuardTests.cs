using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่านตรวจก่อนรับ "บรรทัดที่ AI แตกให้" เข้าเป็นรายการทางบัญชี
///
/// ═══ ทำไมต้องเข้ม ═══
/// บรรทัดที่ผ่านด่านนี้จะกลายเป็น <c>DocumentLine</c> → JE → รายงานภาษี
/// การรับ "บางบรรทัดที่ดูเข้าท่า" อันตรายกว่าการไม่รับเลย เพราะระบบมี local
/// path อยู่แล้ว (บรรทัดสรุปใบเดียวจากยอดหัวกระดาษ) ที่ลงบัญชีถูกต้องเสมอ
/// </summary>
public class OcrLineSplitGuardTests
{
    private const string ThreeLines = """
    {"lines":[
      {"description":"ปูนซีเมนต์","quantity":10,"unit":"ถุง","unit_price":150,"amount":1500},
      {"description":"ทราย","quantity":2,"unit":"คิว","unit_price":400,"amount":800},
      {"description":"ค่าขนส่ง","quantity":1,"unit":"เที่ยว","unit_price":700,"amount":700}
    ]}
    """;

    [Fact]
    public void ผลรวมตรงยอดก่อนภาษี_รับ()
    {
        var r = OcrLineSplitGuard.Evaluate(ThreeLines, subTotal: 3000m, totalAmount: 3210m);
        Assert.True(r.Accepted);
        Assert.Equal(3, r.Lines.Count);
        Assert.Equal(3000m, r.Sum);
        Assert.Null(r.Reason);
        Assert.Equal("ถุง", r.Lines[0].Unit);
    }

    [Fact]
    public void ผลรวมตรงยอดรวม_กรณีราคารวม_VAT_ก็รับ()
    {
        // ใบที่ราคาบนกระดาษรวม VAT แล้ว — ไม่มียอดก่อนภาษีให้เทียบ
        var r = OcrLineSplitGuard.Evaluate(ThreeLines, subTotal: 0m, totalAmount: 3000m);
        Assert.True(r.Accepted);
        Assert.Equal(3000m, r.Sum);
    }

    [Fact]
    public void ผลรวมไม่ตรง_ต้องทิ้งทั้งชุด_ไม่ใช่รับบางบรรทัด()
    {
        var r = OcrLineSplitGuard.Evaluate(ThreeLines, subTotal: 5000m, totalAmount: 5350m);
        Assert.False(r.Accepted);
        Assert.Empty(r.Lines);              // ← หัวใจของด่าน
        Assert.Equal(3000m, r.Sum);         // ยังบอกผลรวมไว้ให้เขียนเหตุผล
        Assert.Contains("ไม่ตรงกับยอดบนกระดาษ", r.Reason);
    }

    [Theory]
    [InlineData(3000.00, true)]     // ตรงเป๊ะ
    [InlineData(3001.00, true)]     // ต่าง 1 บาท = ขอบเขตที่ยอมรับ
    [InlineData(2999.00, true)]
    [InlineData(3001.01, false)]    // เกินขอบเขต
    [InlineData(2998.99, false)]
    public void ขอบเขตความคลาดเคลื่อนหนึ่งบาท(double subTotal, bool expected)
    {
        var r = OcrLineSplitGuard.Evaluate(ThreeLines, (decimal)subTotal, totalAmount: 0m);
        Assert.Equal(expected, r.Accepted);
    }

    [Fact]
    public void ไม่มียอดบนหัวกระดาษ_ไม่รับเลย_เพราะตรวจไม่ได้()
    {
        var r = OcrLineSplitGuard.Evaluate(ThreeLines, subTotal: 0m, totalAmount: 0m);
        Assert.False(r.Accepted);
        Assert.Contains("ไม่มียอดบนหัวกระดาษ", r.Reason);
    }

    [Fact]
    public void บรรทัดที่ไม่มียอดหรือไม่มีคำอธิบาย_ถูกตัดออกก่อนรวม()
    {
        var json = """
        {"lines":[
          {"description":"ปูนซีเมนต์","quantity":10,"unit_price":150,"amount":1500},
          {"description":"","amount":999},
          {"description":"แถวไม่มียอด"},
          {"description":"ทราย","quantity":2,"unit_price":400,"amount":800}
        ]}
        """;
        var r = OcrLineSplitGuard.Evaluate(json, subTotal: 2300m, totalAmount: 0m);
        Assert.True(r.Accepted);
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(2300m, r.Sum);
    }

    [Fact]
    public void ไม่มีคีย์_lines_ต้องไม่รับ()
    {
        var r = OcrLineSplitGuard.Evaluate("""{"primary":"ok","reasoning":"อ่านไม่ออก"}""",
            subTotal: 1000m, totalAmount: 1070m);
        Assert.False(r.Accepted);
        Assert.Contains("ไม่มีรายการบรรทัด", r.Reason);
    }

    [Fact]
    public void lines_ว่าง_ต้องไม่รับ()
    {
        var r = OcrLineSplitGuard.Evaluate("""{"lines":[]}""", subTotal: 1000m, totalAmount: 1070m);
        Assert.False(r.Accepted);
        Assert.Contains("ไม่มีบรรทัดที่ใช้ได้", r.Reason);
    }

    [Fact]
    public void JSON_เสีย_ต้องไม่โยน_exception()
    {
        var r = OcrLineSplitGuard.Evaluate("{ไม่ใช่ json", subTotal: 1000m, totalAmount: 0m);
        Assert.False(r.Accepted);
        Assert.Contains("JSON", r.Reason);
    }

    [Fact]
    public void ไม่ส่งราคาต่อหน่วยมา_คำนวณให้จากยอดหารจำนวน()
    {
        var json = """{"lines":[{"description":"บริการรายเดือน","quantity":4,"amount":1000}]}""";
        var r = OcrLineSplitGuard.Evaluate(json, subTotal: 1000m, totalAmount: 0m);
        Assert.True(r.Accepted);
        Assert.Equal(250m, r.Lines[0].UnitPrice);
    }

    [Fact]
    public void จำนวนเป็นศูนย์หรือติดลบ_ถือเป็นหนึ่ง()
    {
        var json = """{"lines":[{"description":"ค่าบริการ","quantity":0,"amount":500}]}""";
        var r = OcrLineSplitGuard.Evaluate(json, subTotal: 500m, totalAmount: 0m);
        Assert.True(r.Accepted);
        Assert.Equal(1m, r.Lines[0].Quantity);
        Assert.Equal(500m, r.Lines[0].UnitPrice);
    }
}

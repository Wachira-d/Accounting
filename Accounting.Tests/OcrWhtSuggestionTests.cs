using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "กระดาษไม่พิมพ์ WHT" ≠ "ไม่ต้องหัก" — หน้าที่หักเป็นของ**ผู้จ่าย** (ท.ป.4/2528)
/// ระบบต้องเสนออัตรา + ประเภทเงินได้ ม.40 ให้ครบ ไม่ใช่เงียบ
/// (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T1-07 · T1-24 · T4-06)
/// </summary>
public class OcrWhtSuggestionTests
{
    [Theory]
    [InlineData("8", 3)]      // ค่าจ้างทำของ/บริการอื่น 40(8)
    [InlineData("8ad", 2)]    // ค่าโฆษณา
    [InlineData("8tr", 1)]    // ค่าขนส่งไม่ใช่สาธารณะ
    [InlineData("5", 5)]      // ค่าเช่าอสังหา 40(5)
    [InlineData("6", 3)]      // วิชาชีพอิสระ 40(6)
    public void รหัสที่ตัวจัดหมวดใช้_ต้องมีอยู่จริงและอัตราตรงกฎหมาย(string code, decimal juristicRate)
    {
        var t = ThaiWhtRateTable.Find(code);
        Assert.NotNull(t);
        Assert.Equal(juristicRate, t!.JuristicRate);
        Assert.NotEmpty(t.ApplicableForms);
    }

    [Fact]
    public void ดอกเบี้ย_บุคคลกับนิติบุคคลคนละอัตรา()
    {
        Assert.Equal(15m, ThaiWhtRateTable.RateFor("4a", payeeIsJuristic: false));
        Assert.Equal(1m, ThaiWhtRateTable.RateFor("4a", payeeIsJuristic: true));
    }

    [Fact]
    public void เงินเดือน_ไม่มีอัตราคงที่_ต้องเป็น_null_ห้ามใส่เลขปลอม()
    {
        var t = ThaiWhtRateTable.Find("1");
        Assert.NotNull(t);
        Assert.Null(t!.IndividualRate);
        Assert.Null(t.JuristicRate);
    }

    [Theory]
    [InlineData(999, 0, false)]     // ต่ำกว่าเกณฑ์ ฿1,000 → ไม่ต้องหัก
    [InlineData(1000, 0, true)]     // ถึงเกณฑ์พอดี
    [InlineData(800, 300, true)]    // สะสมทั้งสัญญาถึงเกณฑ์ → ต้องหักทุกงวด
    [InlineData(800, 0, false)]
    public void เกณฑ์หนึ่งพันบาท_นับสะสมต่อสัญญา(decimal thisPayment, decimal already, bool expected)
        => Assert.Equal(expected, ThaiWhtRateTable.ShouldWithhold(thisPayment, already));
}

using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตารางอัตราหัก ณ ที่จ่ายต้องมี **ที่เดียว** — ล็อกค่าที่ ท.ป.4/2528 กำหนด
/// และล็อกรหัสที่เคยชนกัน
///
/// ═══ ที่มา (ผลตรวจทีม D · SYSTEM_AUDIT_2026-09-07.md D-01) ═══
/// พบตารางอัตราชุดที่ 3 (<c>TaxService.WhtRateTable</c>) และชุดที่ 4
/// (<c>ThaiGovIntegrationService.GetWhtRatesAsync</c>) — ชุดที่ 3 ผิดกฎหมายและ
/// <b>รหัสชนกับตารางกลาง</b>: คีย์ <c>"5"</c> ใส่ 1% "ค่าขนส่ง" แต่รหัส 5 ของ
/// ตารางกลางคือ <b>ค่าเช่า 5%</b> · คีย์ <c>"6"</c> ใส่ 2% "ค่าประกันภัย" แต่
/// รหัส 6 คือ <b>วิชาชีพอิสระ 3%</b> ⇒ บรรทัดที่ระบบเองสร้างด้วยรหัสกลางจะถูก
/// รายงานด้วยอัตราของประเภทอื่น
/// </summary>
public class WhtRateSingleSourceTests
{
    [Theory]
    // รหัสที่เคยชนกันโดยตรง — ค่าที่ถูกคือของตารางกลาง
    [InlineData("5", 5.0, 5.0)]      // ค่าเช่าทรัพย์สิน 40(5) — ไม่ใช่ค่าขนส่ง 1%
    [InlineData("6", 3.0, 3.0)]      // วิชาชีพอิสระ 40(6) — ไม่ใช่ค่าประกันภัย 2%
    [InlineData("7", 3.0, 3.0)]      // ค่ารับเหมา 40(7)
    [InlineData("3", 3.0, 3.0)]      // ค่าสิทธิ 40(3) — เคยมีที่ใส่ 5%
    [InlineData("4a", 15.0, 1.0)]    // ดอกเบี้ย: บุคคล 15% · นิติบุคคล 1%
    [InlineData("4b", 10.0, 10.0)]   // เงินปันผล
    [InlineData("8ad", 2.0, 2.0)]    // ค่าโฆษณา
    [InlineData("8tr", 1.0, 1.0)]    // ค่าขนส่ง (ไม่ใช่ขนส่งสาธารณะ)
    public void อัตราตามกฎหมายต้องมาจากตารางกลางเท่านั้น(
        string code, double individual, double juristic)
    {
        Assert.Equal((decimal)individual, ThaiWhtRateTable.RateFor(code, payeeIsJuristic: false));
        Assert.Equal((decimal)juristic, ThaiWhtRateTable.RateFor(code, payeeIsJuristic: true));
    }

    [Fact]
    public void เงินเดือน40_1_ต้องไม่มีอัตราคงที่()
    {
        // กฎหมายใช้อัตราขั้นบันได — ตารางที่ถูกลบไปใส่ 3% คงที่
        // "ค่าที่กฎหมายไม่ได้กำหนดคงที่ ต้องเก็บ null ห้ามใส่ตัวเลขปลอม"
        Assert.Null(ThaiWhtRateTable.RateFor("1", payeeIsJuristic: false));
        Assert.Null(ThaiWhtRateTable.RateFor("1", payeeIsJuristic: true));
        Assert.Null(ThaiWhtRateTable.RateFor("40(1)", payeeIsJuristic: true));
    }

    [Fact]
    public void รหัสที่ไม่รู้จักต้องคืนnull_ไม่ใช่อัตราเริ่มต้น3เปอร์เซ็นต์()
    {
        Assert.Null(ThaiWhtRateTable.RateFor("advertising", payeeIsJuristic: true));
        Assert.Null(ThaiWhtRateTable.RateFor("default", payeeIsJuristic: true));
        Assert.Null(ThaiWhtRateTable.RateFor(null, payeeIsJuristic: true));
    }

    [Theory]
    [InlineData("1", "1")]        // เงินเดือน 40(1)
    [InlineData("2", "2")]        // ค่านายหน้า 40(2)
    [InlineData("3", "3")]        // ค่าสิทธิ 40(3)
    [InlineData("4a", "4a")]      // ดอกเบี้ย 40(4)(ก)
    [InlineData("4b", "4b")]      // เงินปันผล 40(4)(ข)
    [InlineData("5", "5")]        // ค่าเช่า 40(5)
    [InlineData("6", "5")]        // วิชาชีพอิสระ 40(6) → แถว 5 (ม.3 เตรส)
    [InlineData("7", "5")]        // ค่ารับเหมา 40(7)
    [InlineData("8", "5")]        // ค่าจ้างทำของ 40(8)
    [InlineData("8ad", "5")]      // ★ ค่าโฆษณา — เดิมหายจากทุกแถว
    [InlineData("8tr", "5")]      // ★ ค่าขนส่ง — เดิมหายจากทุกแถว
    public void ทุกประเภทเงินได้ต้องมีแถวบนแบบ50ทวิ(string code, string expectedRow)
    {
        Assert.Equal(expectedRow, ThaiWhtRateTable.CertificateRow(code));
    }

    [Fact]
    public void รหัสที่ไม่รู้จักต้องตกแถวอื่นๆ_ไม่ใช่หายเงียบ()
    {
        // ทิศของความผิดพลาดต้องเป็น "โผล่ผิดแถว" ไม่ใช่ "หายจากทุกแถวแต่ยอดรวมเต็ม"
        // (ซึ่งทำให้ผู้รับเงินยื่นเครดิตภาษีไม่ได้)
        Assert.Equal("6", ThaiWhtRateTable.CertificateRow("zzz"));
        Assert.Equal("6", ThaiWhtRateTable.CertificateRow("99"));
        Assert.Equal("6", ThaiWhtRateTable.CertificateRow(null));
        // 40(4) ที่ไม่ระบุวงเล็บ ชี้ขาดไม่ได้ว่าดอกเบี้ย (15%) หรือปันผล (10%) — ห้ามเดา
        Assert.Equal("6", ThaiWhtRateTable.CertificateRow("40(4)"));
    }

    [Fact]
    public void ค้นด้วยมาตราก็ได้ผลเดียวกับค้นด้วยรหัส()
    {
        Assert.Equal(ThaiWhtRateTable.Find("5")?.Code, ThaiWhtRateTable.Find("40(5)")?.Code);
        Assert.Equal(ThaiWhtRateTable.Find("4a")?.Code, ThaiWhtRateTable.Find("40(4)(ก)")?.Code);
    }
}

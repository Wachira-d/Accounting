using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กติกา §82/5(1)(4) ของ "บรรทัดที่ผู้ใช้พิมพ์เอง" — ย้ายจาก regex ใน documents.html (รอบ 193 ฝ่ายค้านรอบสอง C-4)
///
/// สองครึ่ง (กฎ #4 H): ครึ่งแรกล็อกว่าคำที่ JS เคยจับยังถูกปิดเคลม · ครึ่งหลังล็อกว่าคำที่ regex เดิม<b>กว้างเกิน</b>
/// ("รับรอง"/"เลี้ยง" เดี่ยว) ไม่ถูกปิดเคลมอีก
/// </summary>
public class ManualInputVatLineRuleTests
{
    [Theory]
    [InlineData("ค่าเลี้ยงลูกค้า ร้านอาหาร")]
    [InlineData("เลี้ยงรับรองคู่ค้า")]
    [InlineData("ค่ารับรองลูกค้าต่างประเทศ")]
    [InlineData("พาลูกค้าดูงาน")]
    [InlineData("กระเช้าปีใหม่")]
    [InlineData("ค่ากรีนฟีกอล์ฟ")]
    public void ค่ารับรองแบบกว้าง_ปิดเคลม_82_5_4(string line)
    {
        var hit = ManualInputVatLineRule.Judge(line);
        Assert.NotNull(hit);
        Assert.Equal("RD-82/5(4)", hit!.Value.RuleCode);
    }

    [Theory]
    [InlineData("ซื้อของตามบิลเงินสด")]
    [InlineData("ใบเสร็จเงินสด ร้านวัสดุ")]
    public void บิลเงินสด_ปิดเคลม_82_5_1(string line)
        => Assert.Equal("RD-82/5(1)", ManualInputVatLineRule.Judge(line)!.Value.RuleCode);

    [Theory]
    [InlineData("ค่าหนังสือรับรองบริษัท")]            // regex เดิม /รับรอง/ ปิดเคลมผิด
    [InlineData("ค่าตรวจรับรองมาตรฐาน ISO 9001")]
    [InlineData("อาหารเลี้ยงสัตว์ 20 กก.")]           // regex เดิม /เลี้ยง/ ปิดเคลมผิด
    [InlineData("ค่าเลี้ยงสังสรรค์พนักงาน")]            // สวัสดิการพนักงาน — ไม่ใช่ค่ารับรอง
    [InlineData("กระดาษ A4 80 แกรม")]
    [InlineData("")]
    [InlineData(null)]
    public void คำทั่วไป_และคำที่_regex_เดิมจับผิด_ไม่แตะ(string? line)
        => Assert.Null(ManualInputVatLineRule.Judge(line));
}

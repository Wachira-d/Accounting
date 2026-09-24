using System.Text.RegularExpressions;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กติกา §82/5(1)(4) ของ "บรรทัดที่ผู้ใช้พิมพ์เอง" — ย้ายจาก regex ใน documents.html (รอบ 193 ฝ่ายค้านรอบสอง C-4)
///
/// <para><b>baseline มาจาก git ไม่ใช่ความจำ</b> (F2 ข้อ 6 · ฝ่ายค้านรอบสาม R3-4): regex สองตัวข้างล่างคัดลอกตรงตัวจาก
/// <c>git show 7601891:Accounting/wwwroot/pages/documents.html</c> บรรทัด 7816-7817 (คอมมิตสุดท้ายที่ JS ยังตัดสินเอง).
/// รุ่นแรกของกติกาฝั่งเซิร์ฟเวอร์เขียนจากความจำเป็นรายการคำบวก ⇒ ค่ารับรองจริงที่มีคำคั่นหลุดไปเคลม
/// ("ค่าอาหารรับรองแขก" · "เลี้ยงอาหารลูกค้า") = ยื่น ภ.พ.30 เกินสิทธิ์</para>
///
/// สามกลุ่ม: (1) ทุกข้อความที่ baseline ปิดเคลมและเป็นค่ารับรอง/บิลเงินสดจริง → ต้องยังปิด (และ baseline ต้องปิดจริง — กันชุดทดสอบ
/// ที่ไม่ได้ทดสอบอะไร) · (2) คำที่ baseline ปิดผิดและตั้งใจปล่อย → ไม่ปิด · (3) คำทั่วไป → ไม่ปิดทั้งคู่
/// </summary>
public class ManualInputVatLineRuleTests
{
    // ตรงตัวจาก git show 7601891:Accounting/wwwroot/pages/documents.html (localRules)
    private static readonly Regex BaselineEntertainment =
        new("รับรอง|เลี้ยง(?:ลูกค้า|รับรอง)?|กระเช้า|ของขวัญลูกค้า|กอล์ฟ|พาลูกค้า", RegexOptions.IgnoreCase);
    private static readonly Regex BaselineCashBill = new("ใบเสร็จเงินสด|บิลเงินสด", RegexOptions.IgnoreCase);

    [Theory]
    // ฝ่ายค้านรอบสาม R3-4 — สี่ข้อความที่รุ่นก่อนปล่อยหลุด
    [InlineData("ค่าอาหารรับรองแขก")]
    [InlineData("รับรองแขกต่างประเทศ")]
    [InlineData("เลี้ยงอาหารลูกค้า")]
    [InlineData("เลี้ยงสังสรรค์ลูกค้า")]
    // ที่เคยปิดและต้องยังปิด
    [InlineData("ค่าอาหารเลี้ยงลูกค้า")]
    [InlineData("ค่ารับรอง")]
    [InlineData("ของขวัญลูกค้า")]
    [InlineData("ค่าเลี้ยงรับรองคู่ค้า")]
    [InlineData("ค่ารับรองลูกค้าต่างประเทศ")]
    [InlineData("ค่ารับรองผู้บริหาร")]
    [InlineData("พาลูกค้าดูงาน")]
    [InlineData("กระเช้าปีใหม่")]
    [InlineData("ค่ากรีนฟีกอล์ฟ")]
    [InlineData("ค่าอาหารเลี้ยงผู้บริหารบริษัทคู่ค้า")]
    [InlineData("ค่าเลี้ยงอาหารแขกและพนักงาน")]      // มีแขกด้วย = ค่ารับรอง แม้มีคำว่าพนักงาน
    [InlineData("เลี้ยงข้าวแขก VIP")]
    [InlineData("งานเลี้ยงขอบคุณลูกค้า")]
    public void ค่ารับรองที่_baseline_ปิดเคลม_ต้องยังปิด(string line)
    {
        Assert.Matches(BaselineEntertainment, line);   // ชุดทดสอบต้องเป็นสิ่งที่ baseline ปิดจริง
        var hit = ManualInputVatLineRule.Judge(line);
        Assert.NotNull(hit);
        Assert.Equal("RD-82/5(4)", hit!.Value.RuleCode);
    }

    [Theory]
    [InlineData("ซื้อของตามบิลเงินสด")]
    [InlineData("ใบเสร็จเงินสด ร้านวัสดุ")]
    public void บิลเงินสด_ที่_baseline_ปิดเคลม_ต้องยังปิด_82_5_1(string line)
    {
        Assert.Matches(BaselineCashBill, line);
        Assert.Equal("RD-82/5(1)", ManualInputVatLineRule.Judge(line)!.Value.RuleCode);
    }

    [Theory]
    [InlineData("ค่าหนังสือรับรองบริษัท")]
    [InlineData("ค่าตรวจรับรองมาตรฐาน ISO 9001")]
    [InlineData("ค่าใบรับรองแพทย์")]
    [InlineData("ค่ารับรองสำเนาเอกสาร")]
    [InlineData("ค่ารับรองงบการเงิน ผู้สอบบัญชี")]
    [InlineData("อาหารเลี้ยงสัตว์ 20 กก.")]
    [InlineData("อาหารเลี้ยงปลา")]
    [InlineData("ค่าเลี้ยงสังสรรค์พนักงาน")]            // สวัสดิการพนักงาน — ไม่ใช่ค่ารับรอง
    [InlineData("งานเลี้ยงปีใหม่พนักงาน")]
    [InlineData("เบี้ยเลี้ยงพนักงานต่างจังหวัด")]
    public void คำที่_baseline_ปิดผิด_ตั้งใจปล่อย(string line)
    {
        Assert.Matches(BaselineEntertainment, line);   // ยืนยันว่าเป็นการเปลี่ยนพฤติกรรมโดยเจตนา ไม่ใช่เคสที่เดิมก็ไม่ปิด
        Assert.Null(ManualInputVatLineRule.Judge(line));
    }

    [Theory]
    [InlineData("กระดาษ A4 80 แกรม")]
    [InlineData("ค่าน้ำประปา")]
    [InlineData("")]
    [InlineData(null)]
    public void คำทั่วไป_ไม่แตะ(string? line)
        => Assert.Null(ManualInputVatLineRule.Judge(line));
}

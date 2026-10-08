using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำตัดสินข้อ 113 (ทีมตรวจงานค้าง 2026-10-08): ปุ่ม "ยกเลิกการนำส่ง" + บั๊ก สปส. นำส่งทั้งเดือนใช้ JE เดียว แต่กลับรายรอบกลับ JE ทั้งก้อนแล้วปลดธงรอบเดียว
/// </summary>
public class RemittanceVoidPolicyTests
{
    [Fact]
    public void มีเหตุผล_มีJE_ไม่มีภพ36ที่รับรู้แล้ว_ยกเลิกได้()
        => Assert.Null(RemittanceVoidPolicy.BlockReason("บันทึกผิดงวด", hasJournal: true, hasRecognizedPp36Docs: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ทิศตรงข้าม_ไม่มีเหตุผล_ถูกกัน(string? reason)
        => Assert.Equal(RemittanceVoidPolicy.RuleReasonRequired, RemittanceVoidPolicy.BlockReason(reason, true, false)!.Value.RuleCode);

    [Fact]
    public void ทิศตรงข้าม_ภพ36ที่รับรู้ภาษีซื้อแล้ว_ถูกกัน_บอกทางไปต่อ()
    {
        var b = RemittanceVoidPolicy.BlockReason("ผิด", true, hasRecognizedPp36Docs: true)!.Value;
        Assert.Equal(RemittanceVoidPolicy.RulePp36Recognized, b.RuleCode);
        Assert.Contains("ยกเลิกการรับรู้ภาษีซื้อก่อน", b.Message);
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่มีJE_ถูกกัน_ไม่แต่งว่ากลับแล้ว()
        => Assert.Equal(RemittanceVoidPolicy.RuleNoJournal, RemittanceVoidPolicy.BlockReason("ผิด", hasJournal: false, false)!.Value.RuleCode);

    [Fact]
    public void กลับรายรอบ_JEของรอบเดียว_ทำได้()
        => Assert.Null(RemittanceVoidPolicy.SsoPerRunReverseBlock("PR-202609-01", otherRunsSharingJe: 0, monthlyRemittanceOwnsJe: false));

    [Theory]
    [InlineData(1, false)]   // อีกรอบใช้ JE เดียวกัน — กลับ = รอบนั้นค้างธงนำส่งทั้งที่ JE ถูกกลับ
    [InlineData(0, true)]    // นำส่งทั้งเดือนจากหน้านำส่ง — ต้องยกเลิกที่รายการนำส่ง
    public void ทิศตรงข้าม_JEที่ใช้ร่วม_กลับรายรอบไม่ได้(int others, bool monthly)
    {
        var b = RemittanceVoidPolicy.SsoPerRunReverseBlock("PR-202609-01", others, monthly)!.Value;
        Assert.Equal(RemittanceVoidPolicy.RuleSsoShared, b.RuleCode);
        Assert.Contains("ยกเลิกการนำส่ง", b.Message);
    }
}

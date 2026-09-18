using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **อัตราที่ขึ้นกับชนิดผู้รับ ห้ามเดาเมื่อยังไม่รู้ชนิดผู้รับ** (ผลตรวจรอบ 180)
///
/// ═══ บั๊กที่ล็อกไว้ ═══ ดอกเบี้ย 40(4)(ก) = บุคคลธรรมดา **15%** · นิติบุคคลไทย **1%**
/// (ต่างกัน 15 เท่า) แต่ระบบมีสองคำตอบที่ขัดกันเองสำหรับรหัสเดียวกัน:
/// · `TaxService` ตั้ง `payeeIsJuristic: true` ตายตัว ⇒ รายงานของบุคคลธรรมดาได้ 1%
/// · `ExpenseCategoryResolver` ฝัง `StatutoryWhtRate: 15m` ในแถวกฎ ⇒ ดอกเบี้ยธนาคารได้ 15%
/// </summary>
public class WhtRatePayeeKindTests
{
    [Fact]
    public void ดอกเบี้ยเป็นอัตราที่ขึ้นกับชนิดผู้รับ()
        => Assert.True(ThaiWhtRateTable.RateDependsOnPayeeKind("4a"));

    [Theory]
    [InlineData("3")]     // ค่าสิทธิ 3% ทั้งคู่
    [InlineData("5")]     // ค่าเช่า 5% ทั้งคู่
    [InlineData("4b")]    // เงินปันผล 10% ทั้งคู่
    public void ประเภทที่อัตราเท่ากันทั้งสองชนิด_ไม่กำกวม(string code)
        => Assert.False(ThaiWhtRateTable.RateDependsOnPayeeKind(code));

    [Fact]
    public void รหัสที่ไม่มีในตาราง_ไม่ใช่ความกำกวม()
        // "ไม่รู้จักรหัส" กับ "รหัสนี้มีสองอัตรา" เป็นคนละเรื่อง — ผู้เรียกต้องแยกได้
        => Assert.False(ThaiWhtRateTable.RateDependsOnPayeeKind("ไม่มีรหัสนี้"));

    [Fact]
    public void อัตราดอกเบี้ยต้องต่างกันจริงตามกฎหมาย()
    {
        Assert.Equal(15m, ThaiWhtRateTable.RateFor("4a", payeeIsJuristic: false));
        Assert.Equal(1m, ThaiWhtRateTable.RateFor("4a", payeeIsJuristic: true));
    }

    [Fact]
    public void ค่าบริการอัตราเดียวกันทั้งสองชนิด()
        // ทิศตรงข้าม: ถ้าทำให้ "กำกวม" ไปหมด ระบบจะเลิกเสนออัตราทุกหมวด = ด่านที่พังอีกทาง
        => Assert.Equal(
            ThaiWhtRateTable.RateFor("8", payeeIsJuristic: true),
            ThaiWhtRateTable.RateFor("8", payeeIsJuristic: false));
}

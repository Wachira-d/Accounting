using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "คำนวณ/คำนวณใหม่ทั้งรอบ" ได้เมื่อไร — คำตัดสินเจ้าของ #35 (รอบ 193):
/// <b>คำนวณใหม่เฉพาะรอบที่ยังไม่จ่าย · รอบที่จ่าย/ยื่นแล้วห้ามแก้</b>
///
/// <para>เดิมคำนวณได้เฉพาะ Draft ⇒ รอบที่คำนวณ/อนุมัติไปก่อนแก้สูตร D-02 ติดตัวเลขผิดถาวร
/// (ทางเดียวคือแก้มือทีละคน). สองครึ่ง: (1) รอบที่ยังไม่จ่ายต้องคำนวณใหม่ได้ — ไม่งั้นการแก้
/// D-02 ไม่ถึงข้อมูลที่ค้างอยู่ (2) รอบที่จ่าย/ยกเลิก/เคยจ่ายแล้วกลับรายการ/นำเข้าจากระบบนอก
/// ต้องถูกปฏิเสธ <b>พร้อมเหตุผลที่ชี้ทางไปต่อ</b> (ห้าม silent no-op)</para>
/// </summary>
public class PayrollRunRecalculatePolicyTests
{
    [Theory]
    [InlineData("Draft")]
    [InlineData("Calculated")]
    [InlineData("Approved")]
    public void รอบที่ยังไม่จ่ายคำนวณใหม่ได้(string status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate(status, null, null);
        Assert.True(can);
        Assert.Null(reason);
    }

    [Fact]
    public void รอบที่จ่ายแล้วห้ามคำนวณใหม่_และชี้ทางกลับรายการจ่าย()
    {
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate("Paid", null, null);
        Assert.False(can);
        Assert.Contains("กลับรายการจ่าย", reason);
    }

    [Theory]
    [InlineData("Voided")]
    [InlineData("อะไรก็ไม่รู้")]
    [InlineData(null)]
    public void สถานะอื่นห้ามคำนวณและต้องมีเหตุผล(string? status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate(status, null, null);
        Assert.False(can);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("Calculated")]
    [InlineData("Approved")]
    public void เคยจ่ายแล้วถูกกลับรายการ_ห้ามคำนวณใหม่ทั้งรอบ_แก้รายคนแทน(string status)
    {
        // ยอดชุดเดิมอาจอยู่ใน ภ.ง.ด.1/สปส.1-10 ที่ยื่นไปแล้ว — "จ่าย/ยื่นแล้วห้ามแก้"
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate(status, null, new DateTime(2026, 9, 20));
        Assert.False(can);
        Assert.Contains("แก้ยอด", reason);
        Assert.Contains("20/09/2026", reason);   // ค.ศ. เสมอ ไม่ขึ้นกับ culture ของเครื่อง
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Calculated")]
    public void รอบที่นำเข้าจากระบบนอก_ห้ามคำนวณทับตัวเลขต้นทาง(string status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate(status, "TakeTime", null);
        Assert.False(can);
        Assert.Contains("TakeTime", reason);
    }

    [Fact]
    public void ระบบนอกว่าง_ถือว่าสร้างในระบบ()
    {
        Assert.True(PayrollRunEditPolicy.CanRecalculate("Calculated", "  ", null).Can);
    }
}

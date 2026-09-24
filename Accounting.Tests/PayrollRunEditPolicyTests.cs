using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กติกา "รอบเงินเดือนนี้แก้ได้ไหม / กลับรายการจ่ายได้ไหม" — ตัวเดียวของระบบ
///
/// ═══ ที่มา ═══
/// เงื่อนไขเดิมถูกเขียนซ้ำ 3 ชุด (แก้ยอดรายคน · แก้แหล่งจ่าย · <c>payEditable</c>
/// ใน payroll.html) และเมื่อแก้ไม่ได้ หน้าเว็บก็แค่**ซ่อนปุ่ม** ผู้ใช้เปิดรอบที่
/// จ่ายแล้วจะเจอตารางที่ทำอะไรไม่ได้เลย ไม่มีคำอธิบาย ไม่มีทางไปต่อ
///
/// เทสต์นี้ล็อกสองอย่างที่ทำให้ regression กลับมาไม่ได้:
///   1. **ทุกสถานะที่แก้ไม่ได้ ต้องมีเหตุผลไม่ว่าง** (ห้าม silent no-op)
///   2. เหตุผลของสถานะ Paid ต้อง**ชี้ทางแก้** คือคำว่า "กลับรายการจ่าย"
///      — ไม่ใช่แค่บอกว่า "ทำไม่ได้"
/// </summary>
public class PayrollRunEditPolicyTests
{
    [Theory]
    [InlineData("Calculated")]
    [InlineData("Approved")]
    public void แก้ยอดได้เฉพาะรอบที่ยังไม่ลงบัญชี(string status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanEditAmounts(status, PayrollRunLockEvidence.None);
        Assert.True(can);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Paid")]
    [InlineData("Voided")]
    [InlineData("อะไรก็ไม่รู้")]
    [InlineData(null)]
    public void แก้ยอดไม่ได้ต้องมีเหตุผลเสมอ(string? status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanEditAmounts(status, PayrollRunLockEvidence.None);
        Assert.False(can);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void เหตุผลของรอบที่จ่ายแล้วต้องชี้ทางแก้ไปที่กลับรายการจ่าย()
    {
        var (_, reason) = PayrollRunEditPolicy.CanEditAmounts("Paid", PayrollRunLockEvidence.None);
        Assert.Contains("กลับรายการจ่าย", reason);
    }

    [Fact]
    public void รอบที่จ่ายแล้วและยังไม่นำส่งสปสกลับรายการได้()
    {
        var (can, reason) = PayrollRunEditPolicy.CanReopen("Paid", ssoSettledAt: null);
        Assert.True(can);
        Assert.Null(reason);
    }

    [Fact]
    public void นำส่งประกันสังคมแล้วห้ามกลับรายการจ่าย()
    {
        // JE ก้อนที่สอง (Dr 21815 / Cr Bank) ออกไปแล้วและมีเลขรับจาก สปส. บน
        // กระดาษ — กลับรายการต้นทางโดยไม่แตะก้อนนั้น = หนี้สินถูกล้างทั้งที่
        // ต้นทางหายไป และยอดที่ยื่นจริงต่างจากในระบบโดยไม่มีใครรู้
        var settled = new DateTime(2026, 9, 15);
        var (can, reason) = PayrollRunEditPolicy.CanReopen("Paid", settled);
        Assert.False(can);
        Assert.Contains("ประกันสังคม", reason);
        Assert.Contains("15/09/2026", reason);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Calculated")]
    [InlineData("Approved")]
    [InlineData("Voided")]
    public void รอบที่ยังไม่จ่ายหรือถูกยกเลิกกลับรายการไม่ได้และต้องบอกเหตุผล(string status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanReopen(status, ssoSettledAt: null);
        Assert.False(can);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void รอบที่ยังไม่จ่ายต้องไม่ถูกบอกให้ไปกลับรายการก่อน()
    {
        // กับดักที่เจอบ่อย: ข้อความ "ต้องกลับรายการก่อน" ถูกเขียนรวม ๆ แล้วโผล่
        // บนรอบที่ **แก้ได้อยู่แล้ว** ⇒ ผู้ใช้ไปกดปุ่มที่ไม่มีทางสำเร็จ
        foreach (var status in new[] { "Calculated", "Approved" })
        {
            var (_, reason) = PayrollRunEditPolicy.CanReopen(status, null);
            Assert.Contains("ยังไม่ได้จ่าย", reason);
        }
    }
}

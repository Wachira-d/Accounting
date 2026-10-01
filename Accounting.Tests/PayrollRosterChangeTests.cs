using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม PR2 — ผลข้างเคียงของการเปลี่ยนรายชื่อคนรับเงิน (คำตัดสินข้อ 69 · X4) + ด่านธงประกันสังคมของแถวที่กรอกมือ (X1)
/// · ทุกเรื่องล็อกสองทิศ: ทิศที่ต้องเปลี่ยน/ปฏิเสธ และทิศที่ต้อง<b>ไม่ถูกแตะ</b>
/// </summary>
public class PayrollRosterChangeTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 3, 0, 0, DateTimeKind.Utc);

    // ── ข้อ 69: เพิ่ม/เอาออกจากรอบ Approved ⇒ ต้องอนุมัติใหม่ ──
    [Fact]
    public void รอบที่อนุมัติแล้ว_เปลี่ยนรายชื่อ_กลับเป็นคำนวณแล้ว_ล้างผู้อนุมัติ_และบอกผู้ใช้()
    {
        var run = new PayrollRun { Status = "Approved", ApprovedBy = "hr.manager", ApprovedAt = Now.AddDays(-1) };
        var notice = PayrollRosterChange.Apply(run, Now);
        Assert.Equal("Calculated", run.Status);
        Assert.Null(run.ApprovedBy);
        Assert.Null(run.ApprovedAt);
        Assert.Equal(Now, run.ManualRosterChangedAt);
        Assert.Equal(PayrollRosterChange.ReapprovalNotice, notice);
        Assert.Contains("อนุมัติ", notice);
    }

    [Fact]
    public void รอบที่ยังไม่อนุมัติ_เปลี่ยนรายชื่อ_สถานะไม่ขยับ_ไม่มีข้อความ_แต่ประทับเวลา()
    {
        var run = new PayrollRun { Status = "Calculated" };
        Assert.Null(PayrollRosterChange.Apply(run, Now));
        Assert.Equal("Calculated", run.Status);
        Assert.Equal(Now, run.ManualRosterChangedAt);
    }

    // ── X4: คำเตือนก่อนคำนวณใหม่ ──
    [Fact]
    public void คำเตือนคำนวณใหม่_มีร่องรอยแก้รายชื่อ_บอกว่าจะทับ_พร้อมวันที่แบบคศ()
    {
        var w = PayrollRunEditPolicy.RecalculateWarning(PayrollRunLockEvidence.None, Now);
        Assert.NotNull(w);
        Assert.Contains("เพิ่ม/เอาพนักงานออกด้วยมือ", w);
        Assert.Contains("30/09/2026", w);                       // InvariantCulture — ไม่กลายเป็น พ.ศ. บนเครื่อง th-TH
    }

    [Fact]
    public void คำเตือนคำนวณใหม่_ไม่มีร่องรอยและไม่มีไฟล์ยื่น_เป็นnull_และมีไฟล์ยื่นอย่างเดียวไม่พูดเรื่องรายชื่อ()
    {
        Assert.Null(PayrollRunEditPolicy.RecalculateWarning(PayrollRunLockEvidence.None, null));
        var filedFile = PayrollRunLockEvidence.From(Array.Empty<PayrollFilingMark>(), false, 0, pnd1FileGenerated: true);
        var w = PayrollRunEditPolicy.RecalculateWarning(filedFile, null);
        Assert.Contains("ภ.ง.ด.1", w);
        Assert.DoesNotContain("ด้วยมือ", w);
        var both = PayrollRunEditPolicy.RecalculateWarning(filedFile, Now);
        Assert.Contains("ภ.ง.ด.1", both);
        Assert.Contains("ด้วยมือ", both);
    }

    // ── X1: ธงประกันสังคม × ฐานที่กรอก ──
    [Fact]
    public void ไม่อยู่ในม33_แต่กรอกฐาน_ปฏิเสธพร้อมทางไปต่อ()
    {
        var r = PayrollSsoFlagGuard.Check(employeeSubjectToSso: false, ssoBase: 15_000m, grossIncome: 15_000m, "สมชาย (E001)");
        Assert.NotNull(r);
        Assert.Contains("สมชาย (E001)", r);
        Assert.Contains("หน้าพนักงาน", r);
    }

    [Fact]
    public void อยู่ในม33_มีรายได้_แต่ฐาน0_ปฏิเสธพร้อมทางไปต่อ()
    {
        var r = PayrollSsoFlagGuard.Check(employeeSubjectToSso: true, ssoBase: 0m, grossIncome: 12_000m, "สมหญิง (E002)");
        Assert.NotNull(r);
        Assert.Contains("ฐานค่าจ้าง", r);
        Assert.Contains("ม.49", r);
    }

    [Theory]
    [InlineData(true, 15_000, 15_000)]     // อยู่ในระบบ + ฐาน > 0
    [InlineData(true, 1_000, 1_000)]       // ฐานต่ำกว่าขั้นต่ำ — ผ่าน (ตัวเติมยอดคิดขั้นต่ำ 1,650 ให้)
    [InlineData(false, 0, 30_000)]         // ไม่อยู่ในระบบ + ฐาน 0
    [InlineData(true, 0, 0)]               // อยู่ในระบบแต่งวดนี้ไม่มีรายได้ — ฐาน 0 ถูกต้อง
    [InlineData(false, 0, 0)]
    public void คู่ที่สอดคล้องกัน_ผ่าน(bool subject, int ssoBase, int gross)
        => Assert.Null(PayrollSsoFlagGuard.Check(subject, ssoBase, gross, "x"));
}

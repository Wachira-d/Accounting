using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ERP_REVIEW_2026-09-05 H-01 — Void รอบที่นำส่ง สปส. แล้วต้องถูกบล็อกด้วยกติกาเดียวกับ Reopen
/// (เดิม VoidPayrollAsync ตรวจแค่ "Voided ซ้ำ" ⇒ กลับ JE จ่ายแต่ JE นำส่งอยู่ ⇒ 21815 ติดลบถาวร)
/// </summary>
public class PayrollRunVoidPolicyTests
{
    [Theory]
    [InlineData(PayrollRunEditPolicy.Draft)]
    [InlineData(PayrollRunEditPolicy.Calculated)]
    [InlineData(PayrollRunEditPolicy.Approved)]
    [InlineData(PayrollRunEditPolicy.Paid)]
    public void ยังไม่นำส่ง_สปส_ยกเลิกได้ทุกสถานะที่ไม่ใช่_Voided(string status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanVoid(status, ssoSettledAt: null, PayrollRunLockEvidence.None);
        Assert.True(can);
        Assert.Null(reason);
    }

    [Fact]
    public void ยกเลิกซ้ำไม่ได้()
    {
        var (can, reason) = PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Voided, null, PayrollRunLockEvidence.None);
        Assert.False(can);
        Assert.Contains("ถูกยกเลิกแล้ว", reason);
    }

    [Fact]
    public void นำส่ง_สปส_แล้ว_ยกเลิกไม่ได้_และบอกทางไปต่อ()
    {
        var settled = new DateTime(2026, 9, 15, 3, 0, 0, DateTimeKind.Utc);
        var (can, reason) = PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Paid, settled, PayrollRunLockEvidence.None);
        Assert.False(can);
        Assert.Contains("15/09/2026", reason);          // ค.ศ. เสมอ (InvariantCulture)
        Assert.Contains("กลับรายการนำส่ง สปส. ก่อน", reason);
    }

    [Fact]
    public void กติกา_สปส_ของ_Void_และ_Reopen_ต้องตรงกัน()
    {
        var settled = new DateTime(2026, 9, 15);
        var (canVoid, _) = PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Paid, settled, PayrollRunLockEvidence.None);
        var (canReopen, _) = PayrollRunEditPolicy.CanReopen(PayrollRunEditPolicy.Paid, settled);
        Assert.Equal(canReopen, canVoid);   // ด่านครอบทางเดียว = ด่านที่ไม่มี — ล็อกให้เท่ากันเสมอ
    }

    // ── รอบ 201 ฝ่ายค้าน PR2 (V-1/V-3): หลักฐานยื่นแล้ว/ปันต้นทุนแล้ว ──
    private static PayrollRunLockEvidence Filed(PayrollFilingSource src)
        => PayrollRunLockEvidence.From(new[] { new PayrollFilingMark(PayrollRunLockEvidence.Pnd1Label, src) }, false, 0);

    [Theory]
    [InlineData(PayrollRunEditPolicy.Approved)]
    [InlineData(PayrollRunEditPolicy.Paid)]
    public void ยื่น_ภงด1_แล้วแต่ยังไม่นำส่ง_สปส_ยกเลิกไม่ได้_พร้อมทางปลด(string status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanVoid(status, null, Filed(PayrollFilingSource.TaxCalendar));
        Assert.False(can);
        Assert.Contains("ภ.ง.ด.1", reason);
        Assert.Contains("ยกเลิกรอบ", reason);
        Assert.Contains("ปฏิทินภาษี", reason);              // ทางปลดตามแหล่งที่บันทึกจริง
    }

    [Theory]
    [InlineData(PayrollRunEditPolicy.Draft)]
    [InlineData(PayrollRunEditPolicy.Calculated)]
    public void รอบที่ไม่เคยอยู่ในไฟล์ยื่น_หลักฐานของงวดไม่ล็อกการยกเลิก(string status)
    {
        // ทิศตรงข้าม: รอบที่สองของเดือน (Draft/Calculated) ไม่อยู่ในแบบที่ยื่น — ต้องยังยกเลิกได้
        var (can, reason) = PayrollRunEditPolicy.CanVoid(status, null, Filed(PayrollFilingSource.TaxCalendar));
        Assert.True(can);
        Assert.Null(reason);
    }

    [Fact]
    public void ต้นทุนแรงงานถูกปันเข้าโครงการแล้ว_ยกเลิกไม่ได้_และไม่มีหลักฐาน_ยกเลิกได้()
    {
        var allocated = PayrollRunLockEvidence.From(Array.Empty<PayrollFilingMark>(), false, allocatedRows: 3);
        var (can, reason) = PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Calculated, null, allocated);
        Assert.False(can);
        Assert.Contains("ปันเข้าโครงการ", reason);
        Assert.Contains("3 แถว", reason);
        Assert.True(PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Calculated, null, PayrollRunLockEvidence.None).Can);
    }

    [Fact]
    public void ไม่มีหลักฐาน_null_โยน_ไม่ใช่ผ่าน()
        => Assert.Throws<ArgumentNullException>(() => PayrollRunEditPolicy.CanVoid(PayrollRunEditPolicy.Paid, null, null!));
}

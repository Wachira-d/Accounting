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
    private static readonly PayrollRunLockEvidence None = PayrollRunLockEvidence.None;

    [Theory]
    [InlineData("Draft")]
    [InlineData("Calculated")]
    [InlineData("Approved")]
    public void รอบที่ยังไม่จ่ายคำนวณใหม่ได้(string status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate(status, null, null, None);
        Assert.True(can);
        Assert.Null(reason);
    }

    [Fact]
    public void รอบที่จ่ายแล้วห้ามคำนวณใหม่_และชี้ทางกลับรายการจ่าย()
    {
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate("Paid", null, null, None);
        Assert.False(can);
        Assert.Contains("กลับรายการจ่าย", reason);
    }

    [Theory]
    [InlineData("Voided")]
    [InlineData("อะไรก็ไม่รู้")]
    [InlineData(null)]
    public void สถานะอื่นห้ามคำนวณและต้องมีเหตุผล(string? status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate(status, null, null, None);
        Assert.False(can);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("Calculated")]
    [InlineData("Approved")]
    public void เคยจ่ายแล้วถูกกลับรายการ_ห้ามคำนวณใหม่ทั้งรอบ_แก้รายคนแทน(string status)
    {
        // ยอดชุดเดิมอาจอยู่ใน ภ.ง.ด.1/สปส.1-10 ที่ยื่นไปแล้ว — "จ่าย/ยื่นแล้วห้ามแก้"
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate(status, null, new DateTime(2026, 9, 20), None);
        Assert.False(can);
        Assert.Contains("แก้ยอด", reason);
        Assert.Contains("20/09/2026", reason);   // ค.ศ. เสมอ ไม่ขึ้นกับ culture ของเครื่อง
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Calculated")]
    public void รอบที่นำเข้าจากระบบนอก_ห้ามคำนวณทับตัวเลขต้นทาง(string status)
    {
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate(status, "TakeTime", null, None);
        Assert.False(can);
        Assert.Contains("TakeTime", reason);
    }

    [Fact]
    public void ระบบนอกว่าง_ถือว่าสร้างในระบบ()
    {
        Assert.True(PayrollRunEditPolicy.CanRecalculate("Calculated", "  ", null, None).Can);
    }

    // ═══════════ รอบ 193 (ฝ่ายค้าน M2) — "ยื่นแล้วห้ามแก้" ครอบรอบ Approved ═══════════
    // รอบ Approved นับเข้าไฟล์ยื่นแล้ว (PayrollRunFilingScope.FilingStatuses) · ครึ่งแรก: Approved ที่
    // ยังไม่ยื่น/นำส่ง/ปันต้นทุน ต้องยังคำนวณใหม่ได้ (ไม่งั้นการแก้ D-02 ไม่ถึงข้อมูลที่ค้าง) ·
    // ครึ่งหลัง: มีหลักฐานออกนอกระบบแล้ว = ปฏิเสธพร้อมทางไปต่อ

    [Fact]
    public void Approvedที่ยังไม่ยื่น_ยังคำนวณใหม่ได้()
    {
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate("Approved", null, null, None);
        Assert.True(can);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Approvedที่งวดถูกบันทึกว่ายื่นแล้ว_ห้ามคำนวณใหม่_และบอกทางไปต่อ(bool pnd1, bool sso)
    {
        var ev = PayrollRunLockEvidence.From(pnd1Filed: pnd1, ssoFiled: sso,
            pnd1Remitted: false, ssoRemitted: false, allocatedRows: 0);
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate("Approved", null, null, ev);
        Assert.False(can);
        Assert.Contains("ยื่น", reason);
        Assert.Contains("ยื่นแบบเพิ่มเติม", reason);           // ทางไปต่อ ไม่ใช่แค่ "ห้าม"
        if (pnd1) Assert.Contains(PayrollRunLockEvidence.Pnd1Label, reason);
        if (sso) Assert.Contains(PayrollRunLockEvidence.SsoLabel, reason);
    }

    [Fact]
    public void Approvedที่นำส่งแล้ว_ห้ามคำนวณใหม่_ชี้ให้กลับรายการนำส่งก่อน()
    {
        var ev = PayrollRunLockEvidence.From(false, false, pnd1Remitted: false, ssoRemitted: true, allocatedRows: 0);
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate("Approved", null, null, ev);
        Assert.False(can);
        Assert.Contains("กลับรายการนำส่ง", reason);
        Assert.Contains(PayrollRunLockEvidence.SsoLabel, reason);
    }

    [Fact]
    public void ปันต้นทุนแรงงานเข้าโครงการแล้ว_ห้ามคำนวณใหม่()
    {
        var ev = PayrollRunLockEvidence.From(false, false, false, false, allocatedRows: 12);
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate("Approved", null, null, ev);
        Assert.False(can);
        Assert.Contains("ปันเข้าโครงการ", reason);
        Assert.Contains("12", reason);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Calculated")]
    public void รอบที่ยังไม่อยู่ในไฟล์ยื่น_หลักฐานการยื่นของงวดไม่ล็อก(string status)
    {
        // Draft/Calculated ไม่เคยอยู่ในไฟล์ ภ.ง.ด.1/สปส.1-10 (FilingStatuses = Approved/Paid) ⇒ รอบที่สอง
        // ของเดือนเดียวกัน (เช่นรอบโบนัส) ต้องคำนวณได้แม้งวดนั้นยื่นรอบแรกไปแล้ว — ห้ามเข้มเกินเหตุ
        var ev = PayrollRunLockEvidence.From(true, true, true, true, allocatedRows: 0);
        Assert.True(PayrollRunEditPolicy.CanRecalculate(status, null, null, ev).Can);
    }

    [Fact]
    public void ปันต้นทุนแล้ว_ล็อกทุกสถานะที่ยังคำนวณได้()
    {
        var ev = PayrollRunLockEvidence.From(false, false, false, false, allocatedRows: 1);
        Assert.False(PayrollRunEditPolicy.CanRecalculate("Calculated", null, null, ev).Can);
    }

    [Fact]
    public void หลักฐานเรียงป้ายคงที่_ไม่ขึ้นกับลำดับแถวที่ค้นเจอ()
    {
        var ev = PayrollRunLockEvidence.From(true, true, true, true, -3);
        Assert.Equal(new[] { "ภ.ง.ด.1", "สปส.1-10" }, ev.FiledForms);
        Assert.Equal(new[] { "ภ.ง.ด.1", "สปส.1-10" }, ev.RemittedForms);
        Assert.Equal(0, ev.ProjectCostAllocatedRows);
        Assert.Empty(PayrollRunLockEvidence.None.FiledForms);
    }

    [Fact]
    public void ไม่ส่งหลักฐาน_ล้มดังไม่ใช่ผ่าน()
    {
        // "ยังไม่ได้ตรวจ" ต้องไม่กลายเป็น "ไม่มีหลักฐาน = คำนวณได้" (DOCTRINE §1)
        Assert.Throws<ArgumentNullException>(() =>
            PayrollRunEditPolicy.CanRecalculate("Approved", null, null, null!));
    }
}

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

    // ═══════════ รอบ 193 (ฝ่ายค้าน M2 + หลังฝ่ายค้าน C1/C2/C3) — "ยื่นแล้วห้ามแก้" ครอบรอบ Approved ═══════════
    // สองครึ่ง: Approved ที่ยังไม่ยื่น/นำส่ง/ปันต้นทุน ยังคำนวณใหม่/แก้ยอดได้ · มีหลักฐาน = ปฏิเสธพร้อมทางไปต่อที่มีจริง

    private static PayrollFilingMark Mark(string form, PayrollFilingSource src) => new(form, src);
    private static PayrollRunLockEvidence Filed(params PayrollFilingMark[] marks)
        => PayrollRunLockEvidence.From(marks, runSsoSettled: false, allocatedRows: 0);

    [Fact]
    public void Approvedที่ยังไม่ยื่น_ยังคำนวณใหม่และแก้ยอดได้()
    {
        Assert.Equal((true, (string?)null), PayrollRunEditPolicy.CanRecalculate("Approved", null, null, None));
        Assert.Equal((true, (string?)null), PayrollRunEditPolicy.CanEditAmounts("Approved", None));
    }

    [Theory]
    [InlineData("ภ.ง.ด.1")]
    [InlineData("สปส.1-10")]
    public void บันทึกยื่นที่ปฏิทินภาษี_ล็อกทั้งคำนวณใหม่และแก้ยอด_และบอกให้กลับไปที่ปฏิทินภาษี(string form)
    {
        // ฝ่ายค้าน C1: ปฏิทินภาษีคือทางเดียวบนจอที่ผู้ใช้บันทึกการยื่น — เดิมไม่ถูกอ่าน ⇒ ล็อกไม่เคยทำงานกับข้อมูลใหม่
        var ev = Filed(Mark(form, PayrollFilingSource.TaxCalendar));
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate("Approved", null, null, ev);
        Assert.False(can);
        Assert.Contains(form, reason);
        Assert.Contains("ปฏิทินภาษี", reason);
        Assert.Contains("รอยื่น", reason);                // ทางปลดที่มีจริงบนจอ
        Assert.Contains("ยื่นแบบเพิ่มเติม", reason);
        var (canEdit, editReason) = PayrollRunEditPolicy.CanEditAmounts("Approved", ev);
        Assert.False(canEdit);                            // C3: ✏️ ถูกล็อกด้วยหลักฐานชุดเดียวกัน
        Assert.DoesNotContain("✏️", editReason);          // ข้อความล็อกห้ามชี้ไปที่ปุ่มที่ถูกล็อก
        Assert.DoesNotContain("✏️", reason);
    }

    [Fact]
    public void ยื่นผ่านปฏิทินcompliance_ทางไปต่อต้องบอกว่าไม่มีหน้าจอ_ไม่ใช่ชี้ปฏิทินภาษี()
    {
        var (_, reason) = PayrollRunEditPolicy.CanRecalculate("Approved", null, null,
            Filed(Mark(PayrollRunLockEvidence.Pnd1Label, PayrollFilingSource.ComplianceFiling)));
        Assert.Contains("compliance", reason);
        Assert.DoesNotContain("หน้า \"ปฏิทินภาษี\"", reason);
    }

    [Fact]
    public void รอบนี้นำส่งปกสแล้ว_ล็อก_และชี้ปุ่มกลับรายการนำส่งที่มีจริง()
    {
        var ev = PayrollRunLockEvidence.From(Array.Empty<PayrollFilingMark>(), runSsoSettled: true, allocatedRows: 0);
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate("Approved", null, null, ev);
        Assert.False(can);
        Assert.Contains("↩️ กลับรายการนำส่ง", reason);   // ป้ายปุ่มจริงใน payroll.html
        Assert.False(PayrollRunEditPolicy.CanEditAmounts("Approved", ev).Can);
    }

    [Fact]
    public void รอบโบนัสApprovedในเดือนที่รอบอื่นนำส่งแล้ว_ไม่ถูกล็อก()
    {
        // ฝ่ายค้าน C2: หลักฐานนำส่งผูกกับรอบ (SsoSettledAt ของรอบนี้) ไม่ใช่เดือน — รอบนี้ยังไม่อยู่ในเงินที่นำส่ง
        var ev = PayrollRunLockEvidence.From(Array.Empty<PayrollFilingMark>(), runSsoSettled: false, allocatedRows: 0);
        Assert.True(PayrollRunEditPolicy.CanRecalculate("Approved", null, null, ev).Can);
        Assert.True(PayrollRunEditPolicy.CanEditAmounts("Approved", ev).Can);
    }

    [Fact]
    public void ปันต้นทุนแรงงานเข้าโครงการแล้ว_ห้ามคำนวณใหม่_แต่แก้ยอดรายคนได้ตามที่ข้อความแนะนำ()
    {
        var ev = PayrollRunLockEvidence.From(Array.Empty<PayrollFilingMark>(), false, allocatedRows: 12);
        var (can, reason) = PayrollRunEditPolicy.CanRecalculate("Approved", null, null, ev);
        Assert.False(can);
        Assert.Contains("ปันเข้าโครงการ", reason);
        Assert.Contains("12", reason);
        Assert.Contains("✏️", reason);
        Assert.True(PayrollRunEditPolicy.CanEditAmounts("Approved", ev).Can);   // ปุ่มที่ข้อความชี้ต้องใช้ได้จริง
        Assert.False(PayrollRunEditPolicy.CanRecalculate("Calculated", null, null, ev).Can);
    }

    [Fact]
    public void ยื่นแล้วและเคยจ่ายแล้วกลับรายการ_ข้อความยื่นแล้วชนะ_ไม่แนะนำปุ่มที่ถูกล็อก()
    {
        var ev = Filed(Mark(PayrollRunLockEvidence.SsoLabel, PayrollFilingSource.TaxCalendar));
        var (_, reason) = PayrollRunEditPolicy.CanRecalculate("Approved", "TakeTime", new DateTime(2026, 9, 20), ev);
        Assert.DoesNotContain("✏️", reason);
        Assert.Contains("ปฏิทินภาษี", reason);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Calculated")]
    public void รอบที่ยังไม่อยู่ในไฟล์ยื่น_หลักฐานการยื่นของงวดไม่ล็อก(string status)
    {
        // Draft/Calculated ไม่เคยอยู่ในไฟล์ ภ.ง.ด.1/สปส.1-10 (FilingStatuses = Approved/Paid) ⇒ รอบที่สองของเดือน
        // (เช่นรอบโบนัส) ต้องคำนวณ/แก้ได้แม้งวดนั้นยื่นรอบแรกไปแล้ว — ห้ามเข้มเกินเหตุ
        var ev = Filed(Mark(PayrollRunLockEvidence.Pnd1Label, PayrollFilingSource.TaxCalendar),
            Mark(PayrollRunLockEvidence.SsoLabel, PayrollFilingSource.ComplianceFiling));
        Assert.True(PayrollRunEditPolicy.CanRecalculate(status, null, null, ev).Can);
        if (status == "Calculated") Assert.True(PayrollRunEditPolicy.CanEditAmounts(status, ev).Can);
    }

    [Fact]
    public void ไฟล์eFilingที่ระบบสร้าง_เป็นแค่คำเตือน_ไม่ล็อก()
    {
        // ฝ่ายค้าน P1: "สร้างไฟล์ ≠ ยื่น" และแถว EFilingExport ลบไม่ได้ ⇒ ถ้าล็อก = ล็อกถาวร
        var ev = PayrollRunLockEvidence.From(Array.Empty<PayrollFilingMark>(), false, 0, pnd1FileGenerated: true);
        Assert.True(PayrollRunEditPolicy.CanRecalculate("Approved", null, null, ev).Can);
        Assert.Contains("ภ.ง.ด.1", PayrollRunEditPolicy.RecalculateWarning(ev));
        Assert.Null(PayrollRunEditPolicy.RecalculateWarning(None));
    }

    [Fact]
    public void แหล่งจ่ายไม่อยู่ในแบบยื่น_เปลี่ยนได้แม้ยื่นแล้ว()
    {
        Assert.True(PayrollRunEditPolicy.CanSetPaymentAccount("Approved").Can);
        Assert.False(PayrollRunEditPolicy.CanSetPaymentAccount("Paid").Can);
    }

    [Fact]
    public void หลักฐานเรียงป้ายคงที่_ตัดซ้ำ_ไม่ขึ้นกับลำดับแถวที่ค้นเจอ()
    {
        var ev = PayrollRunLockEvidence.From(new[]
        {
            Mark("สปส.1-10", PayrollFilingSource.ComplianceFiling),
            Mark("ภ.ง.ด.1", PayrollFilingSource.LegacyTaxReport),
            Mark("ภ.ง.ด.1", PayrollFilingSource.TaxCalendar),
            Mark("ภ.ง.ด.1", PayrollFilingSource.TaxCalendar),
        }, false, -3);
        Assert.Equal(new[] { "ภ.ง.ด.1", "ภ.ง.ด.1", "สปส.1-10" }, ev.FiledMarks.Select(m => m.Form));
        Assert.Equal(PayrollFilingSource.TaxCalendar, ev.FiledMarks[0].Source);
        Assert.Equal(0, ev.ProjectCostAllocatedRows);
        Assert.Empty(PayrollRunLockEvidence.None.FiledMarks);
    }

    [Fact]
    public void ไม่ส่งหลักฐาน_ล้มดังไม่ใช่ผ่าน()
    {
        // "ยังไม่ได้ตรวจ" ต้องไม่กลายเป็น "ไม่มีหลักฐาน = คำนวณได้" (DOCTRINE §1)
        Assert.Throws<ArgumentNullException>(() =>
            PayrollRunEditPolicy.CanRecalculate("Approved", null, null, null!));
        Assert.Throws<ArgumentNullException>(() =>
            PayrollRunEditPolicy.CanEditAmounts("Approved", null!));
    }
}

using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กฎที่ปฏิทินนำส่งบนแดชบอร์ดใช้ตัดสิน — กำหนดยื่นต่อแบบ และ "แบบไหนต้องยื่น
/// ทุกเดือนแม้ยอด 0".
///
/// สองเรื่องนี้พลาดแล้วเสียเงินจริง:
///   • วันครบกำหนดคลาดไป 1 วัน → เตือนช้า → เบี้ยปรับ/เงินเพิ่ม
///   • ตกหล่นข้อ "ยื่นแบบเปล่า" → ผู้ใช้เห็นจอว่างแล้วไม่ยื่น → ปรับอาญา
///     (ภ.พ.30 §83 ยื่นทุกเดือนแม้ไม่มีรายรับ; สปส.1-10 ทุกเดือนแม้ไม่มีค่าจ้าง)
/// </summary>
public class FilingCalendarRulesTests
{
    // ── กำหนดยื่น (เดือนถัดจากงวด) ──────────────────────────────────
    //
    // ⚠️ งวด 08/2026 จงใจเลือกให้วันที่ 7/15/23 ก.ย. 2569 เป็นวันทำการทั้งหมด (จ/อ/พ · ไม่มีวันหยุดราชการ
    // — ต.ค. ใช้ไม่ได้เพราะ 23 ต.ค. = วันปิยมหาราช) ⇒ ตรวจ "เลขวันตามกฎหมาย" ล้วน ๆ โดยไม่ปนการเลื่อนวันหยุด · เดิมเทสต์ใช้งวด 07/2026
    // ซึ่ง 15 ส.ค. 2569 = เสาร์ — ตั้งแต่ 0393d2f8 (ยุบตารางกำหนดยื่นมาที่ TaxFilingDeadline)
    // ระบบเลื่อนวันครบกำหนดที่ตรงวันหยุดไปวันทำการถัดไปตาม ป.พ.พ. §193/8 (CLAUDE.md กฎ #2 E
    // "ถ้าตรงเสาร์/อาทิตย์/วันหยุด → next business day") — โค้ดถูก เทสต์ค้างจากตารางเก่า
    // การเลื่อนวันหยุดล็อกแยกไว้ที่ Due_date_on_a_weekend_rolls_to_monday

    [Fact]
    public void Sso_is_due_on_the_15th_of_the_following_month_for_both_channels()
    {
        var (paper, efiling) = StatutoryRemittanceService.DueDates("SsoSps110", 2026, 8);
        Assert.Equal(new DateTime(2026, 9, 15), paper);
        Assert.Equal(new DateTime(2026, 9, 15), efiling);   // ปกส. ไม่มีส่วนขยาย e-Filing
    }

    [Fact]
    public void Vat_paper_is_the_15th_and_efiling_gets_eight_extra_days()
    {
        var (paper, efiling) = StatutoryRemittanceService.DueDates("VatPp30", 2026, 8);
        Assert.Equal(new DateTime(2026, 9, 15), paper);
        Assert.Equal(new DateTime(2026, 9, 23), efiling);
    }

    [Theory]
    [InlineData("WhtPnd1")]
    [InlineData("WhtPnd3")]
    [InlineData("WhtPnd53")]
    public void Withholding_forms_are_due_on_the_7th_paper_and_15th_efiling(string type)
    {
        var (paper, efiling) = StatutoryRemittanceService.DueDates(type, 2026, 8);
        Assert.Equal(new DateTime(2026, 9, 7), paper);
        Assert.Equal(new DateTime(2026, 9, 15), efiling);
    }

    [Fact]
    public void Due_date_on_a_weekend_rolls_to_monday()
    {
        // งวด 07/2026: 7 ส.ค. = ศุกร์ (ไม่เลื่อน) · 15 ส.ค. = เสาร์ → จันทร์ 17 · 23 ส.ค. = อาทิตย์ → จันทร์ 24
        var (wPaper, wEfiling) = StatutoryRemittanceService.DueDates("WhtPnd3", 2026, 7);
        Assert.Equal(new DateTime(2026, 8, 7), wPaper);
        Assert.Equal(new DateTime(2026, 8, 17), wEfiling);
        var (vPaper, vEfiling) = StatutoryRemittanceService.DueDates("VatPp30", 2026, 7);
        Assert.Equal(new DateTime(2026, 8, 17), vPaper);
        Assert.Equal(new DateTime(2026, 8, 24), vEfiling);   // +8 นับจาก 15 ก่อนเลื่อน (23) แล้วค่อยเลื่อน — ไม่ใช่ 17+8
    }

    [Fact]
    public void December_period_rolls_into_january_of_the_next_year()
    {
        var (paper, efiling) = StatutoryRemittanceService.DueDates("VatPp30", 2026, 12);
        Assert.Equal(new DateTime(2027, 1, 15), paper);      // ศุกร์
        Assert.Equal(new DateTime(2027, 1, 25), efiling);    // 23 ม.ค. 2570 = เสาร์ → จันทร์ 25 (§193/8)
    }

    // ── ต้องยื่นทุกเดือนแม้ยอด 0 หรือไม่ ────────────────────────────

    [Theory]
    [InlineData("VatPp30")]    // §83 — จด VAT แล้วต้องยื่นทุกเดือนแม้ไม่มีรายรับ
    [InlineData("SsoSps110")]  // นายจ้างขึ้นทะเบียนแล้วต้องยื่นทุกเดือน
    [InlineData("WhtPnd1")]    // จ่ายเงินเดือนแล้วต้องยื่นแม้ภาษีหัก = 0
    public void Nil_returns_are_still_mandatory_for_monthly_forms(string type)
    {
        var (always, legal) = StatutoryRemittanceService.FilingRule(type);
        Assert.True(always);
        Assert.False(string.IsNullOrWhiteSpace(legal));   // ต้องอ้างกฎให้ผู้ใช้ตรวจได้
    }

    [Theory]
    [InlineData("WhtPnd3")]    // ไม่มีการหัก = ไม่ต้องยื่น
    [InlineData("WhtPnd53")]
    [InlineData("VatPp36")]    // ไม่ได้จ่ายค่าบริการ ตปท. = ไม่ต้องยื่น
    public void Event_driven_forms_are_not_filed_on_empty_months(string type)
    {
        var (always, _) = StatutoryRemittanceService.FilingRule(type);
        Assert.False(always);
    }
}

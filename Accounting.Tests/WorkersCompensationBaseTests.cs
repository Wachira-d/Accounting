using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **กองทุนเงินทดแทน (กท.20ก) — ปัดเศษและฐานต้องถูก** (D6-6 · ยืนยันเองที่
/// <c>PayrollService.cs:1899-1902</c> ก่อนแก้)
///
/// <para>═══ บั๊กที่ล็อกไว้ ═══
/// <list type="number">
/// <item><c>Math.Round(x, 2)</c> ไม่ระบุ <c>MidpointRounding.AwayFromZero</c>
///   ⇒ banker's rounding (กฎเหล็ก #4 E ห้ามชัดเจน) — 12,345 × 0.5% = 61.725
///   ได้ 61.72 แทน 61.73</item>
/// <item>ฐานใช้ <c>emp.BaseSalary</c> <b>เต็มเดือน</b> ขณะที่ฐาน ปกส. ข้างกัน
///   เฉลี่ยตามวันที่เป็นลูกจ้างจริงไปแล้ว (D-S3) ⇒ คนเข้างานวันที่ 16 ถูกคิด
///   สมทบเต็มเดือน ทั้งที่ค่าจ้างที่จ่ายจริงเป็นครึ่งเดือน</item>
/// <item>เพดาน 20,000/เดือน เป็น literal กลางเมธอด และกรอบอัตรา 0.2–1.0%
///   เป็น literal ใน <c>SettingsService</c> — ตารางกฎหมายสองสำเนา</item>
/// </list></para>
///
/// <para>ครึ่งตรงข้าม: คนที่ทำงานเต็มเดือนและเงินเดือนไม่เกินเพดาน <b>ต้องได้
/// ยอดเท่าเดิมทุกบาท</b> — การย้ายสูตรออกมาห้ามขยับตัวเลขของใครที่ถูกอยู่แล้ว</para>
/// </summary>
public class WorkersCompensationBaseTests
{
    // ═══════════ 1. ปัดเศษ ═══════════

    [Fact]
    public void ปัดครึ่งต้องขึ้น_ไม่ใช่_bankers_rounding()
    {
        // 12,345 × 0.5% = 61.725 — banker's ได้ 61.72 (2 เป็นเลขคู่)
        Assert.Equal(61.73m, WorkersCompensationBase.Contribution(12_345m, 0.5m));
    }

    [Fact]
    public void เศษทั่วไปต้องปัด_2_ตำแหน่ง()
        => Assert.Equal(33.33m, WorkersCompensationBase.Contribution(16_666.67m, 0.2m));

    // ═══════════ 2. ฐาน + เพดาน ═══════════

    [Fact]
    public void เพดานรายเดือน_20000_ตรงกับเพดานรายปี_240000()
    {
        Assert.Equal(240_000m, WorkersCompensationBase.AnnualWageCap);
        Assert.Equal(20_000m, WorkersCompensationBase.MonthlyWageCap);
        // ล็อกความสัมพันธ์ไว้ — เพดานรายเดือนเป็น literal (const decimal
        // หารกันในนิยาม const เสี่ยงเรื่องคอมไพล์) จึงต้องมีเทสต์คุม
        Assert.Equal(WorkersCompensationBase.AnnualWageCap / 12m,
            WorkersCompensationBase.MonthlyWageCap);
    }

    [Fact]
    public void เงินเดือนเกินเพดาน_ต้องคิดจาก_20000()
        => Assert.Equal(40m, WorkersCompensationBase.Contribution(85_000m, 0.2m));

    [Fact]
    public void ค่าจ้างติดลบ_ต้องเป็นศูนย์_ไม่ใช่ยอดติดลบ()
        => Assert.Equal(0m, WorkersCompensationBase.Contribution(-3_000m, 0.2m));

    [Fact]
    public void เข้างานกลางเดือน_ฐานต้องเป็นค่าจ้างที่จ่ายจริง()
    {
        // เงินเดือน 14,000 เข้างานวันที่ 16 ของเดือน 30 วัน ⇒ จ่ายจริง 7,000
        var full = WorkersCompensationBase.Contribution(14_000m, 0.2m);
        var half = WorkersCompensationBase.Contribution(7_000m, 0.2m);
        Assert.Equal(28m, full);    // ตัวเลขที่ระบบคิดมาตลอด (ผิดสำหรับคนเข้ากลางเดือน)
        Assert.Equal(14m, half);    // ตัวเลขที่ถูกหลังแก้
    }

    // ═══════════ 3. กรอบอัตรา ═══════════

    [Theory]
    [InlineData(0.2, true)]
    [InlineData(0.5, true)]
    [InlineData(1.0, true)]
    [InlineData(0.1, false)]
    [InlineData(1.5, false)]
    [InlineData(0, false)]
    public void อัตราต้องอยู่ในกรอบ_0_2_ถึง_1_0(double rate, bool expected)
        => Assert.Equal(expected, WorkersCompensationBase.IsRateInRange((decimal)rate));

    [Fact]
    public void อัตรานอกกรอบ_ต้องยังคิดเงินให้_ไม่ใช่คืนศูนย์เงียบ()
    {
        // ★ ทิศตรงข้าม (G5): คืน 0 เงียบ ๆ = นำส่งขาดแบบมองไม่เห็น
        //   ด่านที่ต้องปฏิเสธอยู่ที่หน้าตั้งค่า ไม่ใช่กลางเครื่องคำนวณ
        Assert.Equal(14m, WorkersCompensationBase.Contribution(14_000m, 0.1m));
        Assert.Equal(0m, WorkersCompensationBase.Contribution(14_000m, 0m));
    }

    // ═══════════ 4. ครึ่งตรงข้าม — เคสปกติห้ามขยับ ═══════════

    [Theory]
    [InlineData(10_000, 0.2, 20.00)]
    [InlineData(15_000, 0.2, 30.00)]
    [InlineData(20_000, 0.2, 40.00)]
    [InlineData(18_000, 1.0, 180.00)]
    public void ทำงานเต็มเดือน_ยอดต้องเท่าเดิมทุกบาท(
        double wage, double rate, double expected)
        => Assert.Equal((decimal)expected,
            WorkersCompensationBase.Contribution((decimal)wage, (decimal)rate));
}

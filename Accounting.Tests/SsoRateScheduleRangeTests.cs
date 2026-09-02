using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>อัตรา/เพดานประกันสังคมมีผลเป็น **ช่วงเดือน** ไม่ใช่ทั้งปี
///
/// <para>ที่มา: ประกาศลดอัตราสมทบของไทยออกเป็นช่วงเดือนเสมอ (1% เดือน พ.ค.–ก.ค.
/// 2563 · 2.5% เดือน ม.ค.–ก.พ. 2565 ฯลฯ) แต่ <c>SsoYearConfig</c> เดิมเก็บได้
/// ปีละค่าเดียว ⇒ ผู้ใช้ต้องแก้แถวเดิมกลางปี ซึ่ง**เปลี่ยนอัตราของเดือนที่ยื่น
/// สปส. ไปแล้วย้อนหลังด้วย** ⇒ สร้างไฟล์ สปส.1-10 ใหม่แล้วไม่ตรงกับที่ยื่นจริง
/// โดยไม่มีอะไรเตือน</para>
///
/// <para>กติกาที่ล็อกไว้: ช่วงที่ **แคบกว่า** ชนะเสมอ ⇒ ผลลัพธ์ไม่ขึ้นกับลำดับแถว
/// (ห้าม "ใครมาก่อนชนะ") · ทับกันแบบกว้างเท่ากัน = กำกวม ต้องปฏิเสธ</para>
/// </summary>
public class SsoRateScheduleRangeTests
{
    [Fact]
    public void ไม่ระบุช่วง_แปลว่าทั้งปี_แถวเก่าต้องทำงานเหมือนเดิม()
    {
        Assert.Equal((1, 12), SsoRateSchedule.NormalizeRange(null, null));
        Assert.Equal((1, 12), SsoRateSchedule.NormalizeRange(0, 0));      // แถวเก่าก่อนมีคอลัมน์
        for (var m = 1; m <= 12; m++)
            Assert.True(SsoRateSchedule.CoversMonth(null, null, m));
    }

    [Theory]
    [InlineData(5, 7, 4, false)]
    [InlineData(5, 7, 5, true)]
    [InlineData(5, 7, 6, true)]
    [InlineData(5, 7, 7, true)]   // ปลายทั้งสองข้างรวมอยู่ด้วย
    [InlineData(5, 7, 8, false)]
    public void ครอบเดือนถูกต้องรวมปลายทั้งสองข้าง(int from, int to, int month, bool expected)
        => Assert.Equal(expected, SsoRateSchedule.CoversMonth(from, to, month));

    [Fact]
    public void ใส่กลับด้าน_ต้องสลับให้ถูก_ไม่ใช่กลายเป็นช่วงว่าง()
    {
        Assert.Equal((5, 7), SsoRateSchedule.NormalizeRange(7, 5));
        Assert.True(SsoRateSchedule.CoversMonth(7, 5, 6));
    }

    [Fact]
    public void เดือนนอกกรอบ_ต้องถูกดึงกลับเป็นทั้งปี_ไม่ใช่ครอบศูนย์เดือน()
    {
        // ค่าที่หลุดกรอบ (0/13/-1) ต้องไม่ทำให้แถวนั้น "ไม่ครอบเดือนไหนเลย"
        // ซึ่งจะกลายเป็น override ที่มีอยู่แต่ไม่มีผล = ตั้งค่าแล้วไม่เกิดอะไร
        Assert.Equal((1, 12), SsoRateSchedule.NormalizeRange(13, -1));
        Assert.True(SsoRateSchedule.CoversMonth(13, -1, 6));
    }

    [Fact]
    public void ช่วงที่แคบกว่าชนะ_ประกาศลดชั่วคราวชนะอัตราทั้งปี()
    {
        // อัตราปกติทั้งปี (1–12) + ประกาศลดเฉพาะ พ.ค.–ก.ค. (5–7)
        Assert.Equal(12, SsoRateSchedule.SpanWidth(1, 12));
        Assert.Equal(3, SsoRateSchedule.SpanWidth(5, 7));
        Assert.True(SsoRateSchedule.SpanWidth(5, 7) < SsoRateSchedule.SpanWidth(1, 12));

        // เดือนที่อยู่นอกช่วงลด ต้องตกกลับไปใช้แถวทั้งปี
        Assert.False(SsoRateSchedule.CoversMonth(5, 7, 8));
        Assert.True(SsoRateSchedule.CoversMonth(1, 12, 8));
    }

    [Fact]
    public void ซ้อนอยู่ข้างในช่วงกว้างกว่า_ไม่ถือว่ากำกวม()
    {
        // 5–7 อยู่ใน 1–12 → ทับกัน แต่ตัดสินได้ (แคบกว่าชนะ) ⇒ ต้องบันทึกได้
        Assert.True(SsoRateSchedule.RangesOverlap(1, 12, 5, 7));
        Assert.False(SsoRateSchedule.RangesAmbiguous(1, 12, 5, 7));
    }

    [Fact]
    public void ทับกันโดยกว้างเท่ากัน_กำกวม_ต้องปฏิเสธ()
    {
        // 1–6 กับ 4–9 กว้าง 6 เดือนเท่ากันและทับกัน → ไม่มีเกณฑ์ตัดสิน
        Assert.Equal(SsoRateSchedule.SpanWidth(1, 6), SsoRateSchedule.SpanWidth(4, 9));
        Assert.True(SsoRateSchedule.RangesAmbiguous(1, 6, 4, 9));

        // ช่วงเดียวกันเป๊ะ = แถวเดิม (ผู้เรียกต้องถือว่าเป็นการ "แก้ไข")
        Assert.True(SsoRateSchedule.RangesAmbiguous(5, 7, 5, 7));
    }

    [Fact]
    public void ไม่ทับกันเลย_ไม่กำกวม()
    {
        Assert.False(SsoRateSchedule.RangesOverlap(1, 4, 5, 7));
        Assert.False(SsoRateSchedule.RangesAmbiguous(1, 4, 5, 7));
    }

    [Fact]
    public void เพดานตามกฎหมายยังเป็นค่าเดิม_การเพิ่มช่วงเดือนต้องไม่เปลี่ยนอะไร()
    {
        Assert.Equal((15_000m, 0.05m), SsoRateSchedule.GetDefault(2025));
        Assert.Equal((17_500m, 0.05m), SsoRateSchedule.GetDefault(2026));
        Assert.Equal((17_500m, 0.05m), SsoRateSchedule.GetDefault(2569));  // พ.ศ.
        Assert.Equal(875m, SsoRateSchedule.GetMaxContribution(2026));
    }
}

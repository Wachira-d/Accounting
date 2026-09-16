using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เงินเพิ่มประกันสังคม §49 — 2%/เดือน · เศษของเดือนนับเป็นหนึ่งเดือน · เพดาน 100%
///
/// เดิมนับเดือนด้วย <c>Math.Ceiling(daysLate / 30.0)</c> ⇒ ช้าพอดีหนึ่งเดือนปฏิทิน
/// ที่มี 31 วัน ได้ 2 เดือน = **คิดเงินเพิ่มเกินไป 1 งวดทุกรอยต่อเดือน 31 วัน**
/// และวันครบกำหนดไม่เลื่อนวันหยุด ⇒ คนที่จ่ายวันจันทร์เพราะวันที่ 15 ตรงเสาร์
/// ถูกคิดเงินเพิ่มทั้งที่จ่ายตรงกำหนด (ป.พ.พ. §193/8)
/// </summary>
public class SsoLateFeeTests
{
    private const decimal Total = 10_000m;   // เงินเพิ่ม 2% = 200 ต่อเดือน

    // ═══ ทิศที่เคยคิดเกิน ═══

    [Fact]
    public void ช้าพอดีหนึ่งเดือนปฏิทินที่มี31วัน_ต้องเป็น_1_เดือน_ไม่ใช่_2()
    {
        // งวด ก.พ. 2569 → ครบกำหนด 15 มี.ค. · จ่าย 15 เม.ย. = ช้า 31 วัน
        // สูตรเดิม ceil(31/30) = 2 เดือน (400 บาท) — ของจริงคือ 1 เดือน (200 บาท)
        var fee = PayrollService.ComputeSsoLateFee(2026, 2, new DateTime(2026, 4, 15), Total);
        Assert.Equal(200m, fee);
    }

    [Fact]
    public void ช้าเกินหนึ่งเดือนแม้วันเดียว_ต้องขึ้นเป็น_2_เดือน()
    {
        var fee = PayrollService.ComputeSsoLateFee(2026, 2, new DateTime(2026, 4, 16), Total);
        Assert.Equal(400m, fee);
    }

    // ═══ ทิศที่ต้องไม่เปลี่ยน ═══

    [Fact]
    public void ช้าแม้วันเดียว_ยังต้องคิด_1_เดือนเต็ม_เศษของเดือนนับเป็นหนึ่ง()
    {
        var fee = PayrollService.ComputeSsoLateFee(2026, 2, new DateTime(2026, 3, 17), Total);
        Assert.Equal(200m, fee);
    }

    [Fact]
    public void จ่ายภายในกำหนด_ต้องเป็นศูนย์เป๊ะ()
    {
        // งวด ก.พ. 2569 ครบกำหนด 15 มี.ค. ซึ่งเป็น **วันอาทิตย์** ⇒ เลื่อนเป็น 16 มี.ค.
        Assert.Equal(DayOfWeek.Sunday, new DateTime(2026, 3, 15).DayOfWeek);
        Assert.Equal(0m, PayrollService.ComputeSsoLateFee(2026, 2, new DateTime(2026, 3, 16), Total));
        Assert.Equal(0m, PayrollService.ComputeSsoLateFee(2026, 2, new DateTime(2026, 3, 10), Total));
    }

    [Fact]
    public void ไม่มียอดนำส่ง_ต้องเป็นศูนย์()
        => Assert.Equal(0m, PayrollService.ComputeSsoLateFee(2026, 2, new DateTime(2027, 1, 1), 0m));

    [Fact]
    public void เพดานไม่เกินยอดเงินสมทบเอง_§49_วรรคท้าย()
    {
        // ช้ามาก ๆ (หลายปี) — 2% × 60 เดือน = 120% ต้องถูกตัดที่ 100%
        var fee = PayrollService.ComputeSsoLateFee(2026, 2, new DateTime(2031, 6, 30), Total);
        Assert.Equal(Total, fee);
    }

    [Fact]
    public void จำนวนเดือนต้องไม่ถอยหลังเมื่อจ่ายช้าขึ้น()
    {
        // invariant: ยิ่งจ่ายช้า เงินเพิ่มต้องไม่ลดลง (จับสูตรนับเดือนที่เพี้ยน
        // ในเดือนที่มีจำนวนวันต่างกัน — 28/29/30/31)
        var prev = 0m;
        for (var d = new DateTime(2026, 3, 16); d <= new DateTime(2028, 3, 16); d = d.AddDays(1))
        {
            var fee = PayrollService.ComputeSsoLateFee(2026, 2, d, Total);
            Assert.True(fee >= prev, $"เงินเพิ่มลดลงที่วันที่ {d:yyyy-MM-dd}: {fee} < {prev}");
            prev = fee;
        }
    }
}

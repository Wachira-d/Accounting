using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 258 (B-H2 · B-M7) — ตัวตัดสิน pure ของ "คิวอีเมลค้างกำลังส่ง" และ "กฎตามวันที่พลาดวันเป้าหมาย" (สองทิศ):
/// ค้างเกินเส้นแบ่ง = ลองใหม่/ยกเลิกได้ · กำลังส่งจริง/ส่งแล้ว = ไม่รับ · พลาดวัน = ย้อนเก็บไม่เกิน 3 วัน · ไม่มีลายน้ำ/กฎเพิ่งแก้ = ไม่ย้อน
/// </summary>
public class QueueRecoveryRound258Tests
{
    private static readonly DateTime Now = new(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Created = Now.AddHours(-5);

    // ───── B-H2 ─────

    [Fact]
    public void กำลังส่งเกิน30นาที_ค้าง_ลองใหม่และยกเลิกได้()
    {
        var touched = Now.AddMinutes(-31);
        Assert.True(EmailQueueStaleSending.IsStale("Sending", touched, Created, Now));
        Assert.True(EmailQueueStaleSending.CanRetry("Sending", touched, Created, Now));
        Assert.True(EmailQueueStaleSending.CanCancel("Sending", touched, Created, Now));
    }

    [Fact]
    public void ทิศตรงข้าม_กำลังส่งจริง_ไม่ค้าง_ลองใหม่และยกเลิกไม่ได้()
    {
        var touched = Now.AddMinutes(-29);
        Assert.False(EmailQueueStaleSending.IsStale("Sending", touched, Created, Now));
        Assert.False(EmailQueueStaleSending.CanRetry("Sending", touched, Created, Now));
        Assert.False(EmailQueueStaleSending.CanCancel("Sending", touched, Created, Now));
    }

    [Fact]
    public void UpdatedAtว่าง_ใช้เวลาสร้างแทน()
    {
        Assert.True(EmailQueueStaleSending.IsStale("Sending", null, Now.AddMinutes(-45), Now));
        Assert.False(EmailQueueStaleSending.IsStale("Sending", null, Now.AddMinutes(-5), Now));
    }

    [Theory]
    [InlineData("Failed", true, false)]
    [InlineData("Pending", false, true)]
    [InlineData("Sent", false, false)]        // ส่งแล้วห้ามกลับไปส่งซ้ำ แม้เก่ามาก
    [InlineData("Cancelled", false, false)]
    [InlineData("Skipped", false, false)]
    public void สถานะอื่น_ตามเดิม(string status, bool canRetry, bool canCancel)
    {
        var old = Now.AddDays(-2);
        Assert.False(EmailQueueStaleSending.IsStale(status, old, old, Now));
        Assert.Equal(canRetry, EmailQueueStaleSending.CanRetry(status, old, old, Now));
        Assert.Equal(canCancel, EmailQueueStaleSending.CanCancel(status, old, old, Now));
    }

    [Fact]
    public void เหตุของแถวค้าง_บอกสาเหตุและทางไปต่อ_ไม่อ้างว่าส่งแล้วหรือยังไม่ส่ง()
    {
        Assert.Contains("ค้างระหว่างส่ง", EmailQueueStaleSending.StaleReason, StringComparison.Ordinal);
        Assert.Contains("ไม่ทราบว่าอีเมลออกไปแล้วหรือยัง", EmailQueueStaleSending.StaleReason, StringComparison.Ordinal);
        Assert.Contains("ลองใหม่", EmailQueueStaleSending.StaleReason, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(30), EmailQueueStaleSending.StaleAfter);
    }

    // ───── B-M7 ─────

    private static readonly DateTime Today = new(2026, 11, 11);

    [Fact]
    public void พลาดวัน_ย้อนเก็บเฉพาะวันหลังลายน้ำ_ไม่เกิน3วันล่าสุด()
    {
        Assert.Equal(new[] { Today.AddDays(-1) }, ScheduleCatchUp.MissedDays(Today, Today.AddDays(-2)));
        // หยุดไป 10 วัน ⇒ เก็บแค่ 3 วันล่าสุด (ไม่ท่วม)
        Assert.Equal(new[] { Today.AddDays(-3), Today.AddDays(-2), Today.AddDays(-1) }, ScheduleCatchUp.MissedDays(Today, Today.AddDays(-10)));
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่พลาด_หรือไม่มีลายน้ำ_ไม่ย้อน()
    {
        Assert.Empty(ScheduleCatchUp.MissedDays(Today, Today.AddDays(-1)));   // สแกนเมื่อวานแล้ว
        Assert.Empty(ScheduleCatchUp.MissedDays(Today, Today));                // สแกนวันนี้แล้ว
        Assert.Empty(ScheduleCatchUp.MissedDays(Today, null));                 // กฎใหม่/วันแรกหลังอัปเดตระบบ
        Assert.Empty(ScheduleCatchUp.MissedDays(Today, Today.AddDays(2)));     // นาฬิกาเพี้ยน — ไม่ย้อน
    }

    [Fact]
    public void ใกล้ครบกำหนด_ย้อนเฉพาะใบที่ยังไม่เลยกำหนด()
    {
        var last = Today.AddDays(-4);
        // เตือนล่วงหน้า 3 วัน: วันที่พลาดเมื่อวาน ⇒ ใบครบกำหนดอีก 2 วัน = ยังเตือนได้
        Assert.True(ScheduleCatchUp.DueRuleCatchesUp("DocumentDueSoon", -3, 0, Today.AddDays(-1), Today, last));
        // เตือนล่วงหน้า 1 วัน: วันที่พลาด 3 วันก่อน ⇒ ใบเลยกำหนดไปแล้ว 2 วัน = ไม่ส่งคำว่า "ใกล้ครบ"
        Assert.False(ScheduleCatchUp.DueRuleCatchesUp("DocumentDueSoon", -1, 0, Today.AddDays(-3), Today, last));
        // วันที่ไม่ได้พลาด (ก่อนลายน้ำ) ⇒ ไม่ย้อน
        Assert.False(ScheduleCatchUp.DueRuleCatchesUp("DocumentDueSoon", -3, 0, Today.AddDays(-1), Today, Today.AddDays(-1)));
    }

    [Fact]
    public void เกินกำหนด_ย้อนได้_ยกเว้นกฎที่ทวงซ้ำทุกวัน_และทริกเกอร์อื่นไม่ย้อน()
    {
        var last = Today.AddDays(-3);
        Assert.True(ScheduleCatchUp.DueRuleCatchesUp("DocumentOverdue", 7, 0, Today.AddDays(-1), Today, last));
        Assert.True(ScheduleCatchUp.DueRuleCatchesUp("DocumentOverdue", 7, 7, Today.AddDays(-2), Today, last));
        Assert.False(ScheduleCatchUp.DueRuleCatchesUp("DocumentOverdue", 7, 1, Today.AddDays(-1), Today, last));   // วันนี้ทวงอยู่แล้ว
        Assert.False(ScheduleCatchUp.DueRuleCatchesUp("DocumentOverdue", 7, 1, Today.AddDays(-2), Today, last));
        Assert.False(ScheduleCatchUp.DueRuleCatchesUp("DocumentApproved", 0, 0, Today.AddDays(-1), Today, last));
    }

    [Theory]
    // รอบ 258 ฝ่ายค้าน R-M2: (ซ้ำทุก N วัน, วันที่พลาดห่างจากวันนี้กี่วัน, ย้อนเก็บไหม)
    [InlineData(2, 2, false)]   // ครั้งแรกเมื่อ 2 วันก่อน ⇒ วันนี้ถึงครั้งซ้ำพอดี — ย้อนด้วย = สองฉบับ
    [InlineData(2, 1, true)]    // เมื่อวาน ⇒ วันนี้ยังไม่ถึงครั้งซ้ำ — ย้อนเก็บครั้งแรก
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]
    [InlineData(3, 1, true)]
    [InlineData(3, 2, true)]
    [InlineData(1, 1, false)]   // ทุกวัน: เหมือนเดิม — ไม่ย้อนทุกกรณี
    [InlineData(1, 2, false)]
    [InlineData(1, 3, false)]
    [InlineData(0, 1, true)]    // ไม่ส่งซ้ำ: ย้อนเก็บครั้งเดียวของใบ
    [InlineData(0, 3, true)]
    [InlineData(7, 2, true)]    // รอบยาวกว่าช่วงย้อน ⇒ ย้อนได้เสมอ
    public void เกินกำหนดส่งซ้ำทุกNวัน_ไม่ย้อนวันที่ครั้งซ้ำของวันนี้ครอบแล้ว(int repeatEvery, int daysAgo, bool expected)
    {
        var last = Today.AddDays(-4);   // พลาด 3 วันเต็ม
        Assert.Equal(expected, ScheduleCatchUp.DueRuleCatchesUp("DocumentOverdue", 7, repeatEvery, Today.AddDays(-daysAgo), Today, last));
    }

    [Fact]
    public void ใบแจ้งยอดรายเดือน_วันที่ตั้ง_ส่งตามปกติ()
    {
        var plan = ScheduleCatchUp.Monthly(new DateTime(2026, 11, 5), 5, null);
        Assert.True(plan.Send);
        Assert.False(plan.Late);
        Assert.Equal(new DateTime(2026, 11, 5), plan.TargetDay);
    }

    [Theory]
    [InlineData(6, 4, true)]     // ระบบหยุดวันที่ 5 (สแกนล่าสุดวันที่ 4) กลับมาวันที่ 6 ⇒ ส่งย้อนหลัง
    [InlineData(8, 4, true)]     // กลับมาวันที่ 8 (ช้า 3 วัน) ⇒ ยังส่ง
    [InlineData(9, 4, false)]    // ช้าเกิน 3 วัน ⇒ ไม่ส่ง (ไม่ส่งใบแจ้งยอดเก่ากลางเดือน)
    [InlineData(6, 5, false)]    // วันที่ 5 สแกนแล้ว ⇒ ไม่ส่งซ้ำ
    [InlineData(4, 3, false)]    // ยังไม่ถึงวันที่ตั้ง
    public void ใบแจ้งยอดรายเดือน_ย้อนหลังเฉพาะเมื่อพลาดจริงและไม่เกิน3วัน(int todayDay, int lastScanDay, bool expected)
    {
        var plan = ScheduleCatchUp.Monthly(new DateTime(2026, 11, todayDay), 5, new DateTime(2026, 11, lastScanDay));
        Assert.Equal(expected, plan.Send);
        Assert.Equal(expected, plan.Late);
    }

    [Fact]
    public void ใบแจ้งยอดรายเดือน_ไม่มีลายน้ำ_ไม่ย้อนหลัง()
        => Assert.False(ScheduleCatchUp.Monthly(new DateTime(2026, 11, 6), 5, null).Send);

    [Fact]
    public void แก้ตารางหรือเปิดกฎที่ปิดไว้_ล้างลายน้ำ_แก้อย่างอื่นคงเดิม()
    {
        var mark = (DateTime?)Today.AddDays(-5);
        Assert.Null(ScheduleCatchUp.WatermarkAfterEdit(mark, false, true, "DocumentOverdue", "DocumentOverdue", 7, 7, 5, 5));   // เปิดกลับ
        Assert.Null(ScheduleCatchUp.WatermarkAfterEdit(mark, true, true, "DocumentOverdue", "DocumentOverdue", 7, 14, 5, 5));   // เปลี่ยนระยะ
        Assert.Null(ScheduleCatchUp.WatermarkAfterEdit(mark, true, true, "MonthlyStatement", "MonthlyStatement", 0, 0, 5, 10)); // เปลี่ยนวันของเดือน
        Assert.Null(ScheduleCatchUp.WatermarkAfterEdit(mark, true, true, "DocumentDueSoon", "DocumentOverdue", 3, 3, 5, 5));    // เปลี่ยนเหตุการณ์
        Assert.Equal(mark, ScheduleCatchUp.WatermarkAfterEdit(mark, true, true, "DocumentOverdue", "DocumentOverdue", 7, 7, 5, 5));
        Assert.Equal(mark, ScheduleCatchUp.WatermarkAfterEdit(mark, true, false, "DocumentOverdue", "DocumentOverdue", 7, 7, 5, 5));   // ปิดกฎ = คงไว้
    }

    [Fact]
    public void บันทึกการย้อนเก็บ_บอกช่วงวันและวันที่เก็บ_ไม่มีวันพลาดไม่มีบันทึก()
    {
        Assert.Null(ScheduleCatchUp.Note(Array.Empty<DateTime>(), Today));
        var one = ScheduleCatchUp.Note(new[] { Today.AddDays(-1) }, Today)!;
        Assert.Contains("10/11/2026", one, StringComparison.Ordinal);
        Assert.Contains("11/11/2026", one, StringComparison.Ordinal);
        var range = ScheduleCatchUp.Note(new[] { Today.AddDays(-3), Today.AddDays(-2), Today.AddDays(-1) }, Today)!;
        Assert.Contains("08/11/2026–10/11/2026", range, StringComparison.Ordinal);
        Assert.Contains("05/11/2026", ScheduleCatchUp.MonthlyNote(new DateTime(2026, 11, 5), Today), StringComparison.Ordinal);
    }
}

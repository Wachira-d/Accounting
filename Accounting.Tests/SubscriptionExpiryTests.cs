using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 232 — บั๊กจริง: หน้า "แพ็กเกจและการชำระเงิน" แสดง "ใช้ได้ถึง 2 เม.ย. 2669 · ออกใบแจ้งหนี้ต่ออายุ 18 มี.ค. 2669"
/// (ค่าแทน "ไม่หมดอายุ" = ค.ศ. 2126 หลุดถึงผู้ใช้เพราะธง <c>IsPermanentFree</c> ถูกถอด) ·
/// ครึ่งแรกล็อกว่าค่าแทนถูกจำแนกเป็น "ไม่หมดอายุ" แม้ธงหลุด · ครึ่งหลัง (ทิศตรงข้าม) ล็อกว่าแพ็กเกจจริงยังแสดงวันหมดอายุ/วันออกใบ/วันที่เหลือตามเดิม ·
/// ทุกการเทียบเป็น ค.ศ. UTC (ไม่มี +543 ในตัวตัดสิน)
/// </summary>
public class SubscriptionExpiryTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);

    // ════════ ครึ่งแรก: ค่าแทน 2126 ต้องไม่กลายเป็น "ปี 2669" บนจอ ════════

    [Fact]
    public void ค่าแทน_2126_โดยธงฟรีถาวรหลุด_ยังเป็นไม่หมดอายุ()
    {
        var sentinel = new DateTime(2126, 4, 2, 0, 0, 0, DateTimeKind.Utc);   // ตามภาพหน้าจอ: "2 เม.ย. 2669" = ค.ศ. 2126
        Assert.True(SubscriptionExpiry.IsOpenEnded(sentinel, Now));
        Assert.True(SubscriptionExpiry.IsOpenEnded(isPermanentFree: false, sentinel, Now));
        Assert.Null(SubscriptionExpiry.DisplayEnd(isPermanentFree: false, sentinel, Now));
        Assert.Null(SubscriptionExpiry.NextRenewalInvoiceAt(isPermanentFree: false, sentinel, Now, isActive: true,
            renewalAlreadyIssued: false, cancelAtPeriodEnd: false, leadDays: 15));
    }

    [Fact]
    public void ตัวเขียนค่าแทนกับตัวอ่าน_ตรงกัน()
    {
        var written = SubscriptionExpiry.OpenEnded(Now);
        Assert.Equal(Now.AddYears(100), written);
        Assert.True(SubscriptionExpiry.IsOpenEnded(written, Now));
        // ค่าแทนที่เขียนไว้เมื่อ 40 ปีก่อน (เหลือ 60 ปี) ยังถูกจำแนกถูก — เกณฑ์ 50 ปี < 100 ปี
        Assert.True(SubscriptionExpiry.IsOpenEnded(written, Now.AddYears(40)));
    }

    [Fact]
    public void ธงฟรีถาวร_ชนะแม้วันหมดอายุเป็นวันจริง()
    {
        var realEnd = new DateTime(2027, 4, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(SubscriptionExpiry.IsOpenEnded(isPermanentFree: true, realEnd, Now));
        Assert.Null(SubscriptionExpiry.DisplayEnd(isPermanentFree: true, realEnd, Now));
    }

    // ════════ ครึ่งหลัง (ทิศตรงข้าม): แพ็กเกจที่ขายจริงไม่ถูกแตะ ════════

    [Theory]
    [InlineData(2026, 12, 3)]   // รายเดือน
    [InlineData(2027, 11, 3)]   // รายปี
    [InlineData(2036, 11, 3)]   // 10 ปี (ขยายทดลองใช้/ของแถมสุดโต่ง) ยังเป็นวันจริง
    [InlineData(2076, 11, 2)]   // ก่อนเกณฑ์ 50 ปี 1 วัน
    public void แพ็กเกจจริง_แสดงวันหมดอายุและวันที่เหลือตามเดิม(int y, int m, int d)
    {
        var end = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(SubscriptionExpiry.IsOpenEnded(end, Now));
        Assert.Equal(end, SubscriptionExpiry.DisplayEnd(isPermanentFree: false, end, Now));
        Assert.True(UsageBarRule.DaysLeft(end, Now) > 0);   // ผู้เรียกใน service: ไม่ open-ended ⇒ UsageBarRule.DaysLeft ตามเดิม
        Assert.Equal(end.AddDays(-15), SubscriptionExpiry.NextRenewalInvoiceAt(isPermanentFree: false, end, Now, isActive: true,
            renewalAlreadyIssued: false, cancelAtPeriodEnd: false, leadDays: 15));
    }

    [Fact]
    public void เกณฑ์_50ปีพอดี_เป็นค่าแทน()
    {
        Assert.True(SubscriptionExpiry.IsOpenEnded(Now.AddYears(50), Now));
        Assert.False(SubscriptionExpiry.IsOpenEnded(Now.AddYears(50).AddDays(-1), Now));
    }

    [Fact]
    public void วันออกใบต่ออายุ_ไม่มีเมื่อ_ไม่Active_หรือออกแล้ว_หรือขอยกเลิกสิ้นรอบ()
    {
        var end = new DateTime(2027, 4, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(SubscriptionExpiry.NextRenewalInvoiceAt(false, end, Now, isActive: false, renewalAlreadyIssued: false, cancelAtPeriodEnd: false, 15));
        Assert.Null(SubscriptionExpiry.NextRenewalInvoiceAt(false, end, Now, isActive: true, renewalAlreadyIssued: true, cancelAtPeriodEnd: false, 15));
        Assert.Null(SubscriptionExpiry.NextRenewalInvoiceAt(false, end, Now, isActive: true, renewalAlreadyIssued: false, cancelAtPeriodEnd: true, 15));
    }

    // ════════ ฝ่ายค้าน 232-hub: N1 เกณฑ์เดียวกัน · B1 ปิดรอบผ่านตัวตั้งเดียว · แถวรอตรวจ ════════

    [Fact]
    public void N1_ThresholdFrom_กับ_IsOpenEnded_ใช้เกณฑ์เดียวกัน()
    {
        var t = SubscriptionExpiry.ThresholdFrom(Now);
        Assert.Equal(Now.AddYears(SubscriptionExpiry.OpenEndedThresholdYears), t);
        Assert.True(SubscriptionExpiry.IsOpenEnded(t, Now));                    // = เกณฑ์ ⇒ ค่าแทน (สูตรใน EF projection: EndDate >= ThresholdFrom)
        Assert.False(SubscriptionExpiry.IsOpenEnded(t.AddTicks(-1), Now));
    }

    [Theory]
    [InlineData(Accounting.Models.Enums.BillingCycle.Monthly, 1)]
    [InlineData(Accounting.Models.Enums.BillingCycle.Quarterly, 3)]
    [InlineData(Accounting.Models.Enums.BillingCycle.SemiAnnual, 6)]
    [InlineData(Accounting.Models.Enums.BillingCycle.Annual, 12)]
    public void B1_ปิดรอบ_EndDate_เท่ากับวันนี้บวกหนึ่งรอบ_และไม่ใช่ค่าแทนอีก(Accounting.Models.Enums.BillingCycle cycle, int months)
    {
        var (end, next) = SubscriptionExpiry.CloseOpenEnded(Now, cycle);
        Assert.Equal(Now.AddMonths(months), end);
        Assert.Equal(end, next);
        Assert.False(SubscriptionExpiry.IsOpenEnded(end, Now));
        Assert.Equal(end, SubscriptionExpiry.DisplayEnd(false, end, Now));   // จอกลับมาแสดงวันจริง
    }

    [Fact]
    public void B1_รอบที่ไม่รู้จัก_ปิดเป็นหนึ่งเดือน_ไม่ใช่ศูนย์()
    {
        var (end, _) = SubscriptionExpiry.CloseOpenEnded(Now, (Accounting.Models.Enums.BillingCycle)99);
        Assert.Equal(Now.AddMonths(1), end);
    }

    [Fact]
    public void B1ข_แถวรอตรวจ_เฉพาะแผนจ่ายเงินที่ธงหลุดและค้างค่าแทน()
    {
        var sentinel = Now.AddYears(100);
        Assert.True(SubscriptionExpiry.NeedsReview(isPermanentFree: false, Accounting.Models.Enums.SubscriptionPlan.Professional, sentinel, Now));
        // ทิศตรงข้าม: ฟรีถาวรจริง / FreeTrial (ฟรีถาวรตามคำตัดสินเดิม) / แผนจ่ายเงินที่มีรอบจริง — ไม่ต้องตรวจ
        Assert.False(SubscriptionExpiry.NeedsReview(isPermanentFree: true, Accounting.Models.Enums.SubscriptionPlan.Professional, sentinel, Now));
        Assert.False(SubscriptionExpiry.NeedsReview(isPermanentFree: false, Accounting.Models.Enums.SubscriptionPlan.FreeTrial, sentinel, Now));
        Assert.False(SubscriptionExpiry.NeedsReview(isPermanentFree: false, Accounting.Models.Enums.SubscriptionPlan.Professional, Now.AddMonths(1), Now));
    }

    [Fact]
    public void หมดอายุแล้ว_ไม่ใช่ไม่หมดอายุ_และวันที่เหลือเป็นศูนย์()
    {
        var past = Now.AddDays(-3);
        Assert.False(SubscriptionExpiry.IsOpenEnded(past, Now));
        Assert.Equal(0, UsageBarRule.DaysLeft(past, Now));
    }
}

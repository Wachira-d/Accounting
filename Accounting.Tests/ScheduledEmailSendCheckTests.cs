using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 2026-10-14 ทีม F4 (ID#12 · ID#25) — ตรวจเอกสารซ้ำตอนส่งอีเมลตั้งเวลา + สรุปผลรายงานตั้งเวลา (pure) ·
/// เส้นจริงบนฐาน: Db/ScheduledEmailRecheckDbTests</summary>
public class ScheduledEmailSendCheckTests
{
    [Theory]
    [InlineData("DocumentApproved", DocumentStatus.Voided, 1000, "ยกเลิก")]
    [InlineData("DocumentOverdue", DocumentStatus.Voided, 1000, "ยกเลิก")]
    [InlineData("DocumentApproved", DocumentStatus.Draft, 1000, "ร่าง")]
    [InlineData("DocumentOverdue", DocumentStatus.Paid, 0, "ชำระครบ")]
    [InlineData("DocumentDueSoon", DocumentStatus.Approved, 0, "ชำระครบ")]
    public void ข้ามพร้อมเหตุผล(string trigger, DocumentStatus status, int balance, string expected)
        => Assert.Contains(expected, ScheduledEmailSendCheck.SkipReason(trigger, true, status, balance));

    [Theory]
    [InlineData("DocumentApproved", DocumentStatus.Paid, 0)]           // ส่งใบหลังอนุมัติ — ชำระแล้วก็ยังส่งตัวเอกสารได้
    [InlineData("DocumentOverdue", DocumentStatus.PartiallyPaid, 400)]
    [InlineData("RecurringInvoiceCreated", DocumentStatus.Approved, 1000)]
    [InlineData(null, DocumentStatus.Approved, 1000)]
    public void ส่งได้(string? trigger, DocumentStatus status, int balance)
        => Assert.Null(ScheduledEmailSendCheck.SkipReason(trigger, true, status, balance));

    [Fact]
    public void ไม่พบเอกสาร_ข้าม()
        => Assert.NotNull(ScheduledEmailSendCheck.SkipReason("DocumentApproved", false, DocumentStatus.Draft, 0m));

    [Theory]
    [InlineData("doc:6f1c2a52-0000-0000-0000-000000000000:DocumentOverdue:overdue-3", "DocumentOverdue")]
    [InlineData("doc:x:RecurringInvoiceCreated:autosend-y", "RecurringInvoiceCreated")]
    [InlineData("payslip:x:PayrollPaid:y", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ทริกเกอร์จากคีย์กันซ้ำ(string? key, string? expected)
        => Assert.Equal(expected, ScheduledEmailSendCheck.TriggerFromIdempotencyKey(key));

    [Fact]
    public void หนังสือรับรอง50ทวิ_ยกเลิกหรือไม่พบ_ข้าม_ปกติส่ง()
    {
        Assert.Contains("ยกเลิก", ScheduledEmailSendCheck.WhtCertSkipReason(true, true));
        Assert.NotNull(ScheduledEmailSendCheck.WhtCertSkipReason(false, false));
        Assert.Null(ScheduledEmailSendCheck.WhtCertSkipReason(true, false));
    }

    [Fact]
    public void ไฟล์แนบล้ม_บอกว่ายังไม่ได้ส่ง_และสิ่งที่ระบบ_ผู้ใช้ทำต่อ()
    {
        var retry = ScheduledEmailSendCheck.AttachmentFailure("timeout", willRetry: true);
        Assert.Contains("ยังไม่ได้ส่ง", retry);
        Assert.Contains("ลองใหม่อัตโนมัติ", retry);
        var final = ScheduledEmailSendCheck.AttachmentFailure(null, willRetry: false);
        Assert.Contains("ลองใหม่", final);
        Assert.Contains("ไม่ทราบสาเหตุ", final);
    }

    [Fact]
    public void ป้ายสถานะไทย()
    {
        Assert.Equal("ข้าม (ไม่ส่ง)", ScheduledEmailSendCheck.StatusLabel(ScheduledEmailSendCheck.SkippedStatus));
        Assert.Equal("ส่งแล้ว", ScheduledEmailSendCheck.StatusLabel("Sent"));
    }

    // ── ID#25 รายงานตั้งเวลา ──

    [Fact]
    public void รายงาน_ส่งครบ_OK_ประทับว่าส่งแล้ว()
    {
        var r = ScheduledReportOutcome.Summarize(3, Array.Empty<(string, string?)>(), null);
        Assert.StartsWith("OK", r.LastResult);
        Assert.True(r.AnyDelivered);
    }

    [Fact]
    public void รายงาน_ล้มบางราย_PARTIAL_ไม่ใช่OK_ระบุผู้รับที่ล้ม()
    {
        var r = ScheduledReportOutcome.Summarize(3, new List<(string, string?)> { ("b@x.test", "mailbox full") }, null);
        Assert.StartsWith("PARTIAL", r.LastResult);
        Assert.Contains("2/3", r.LastResult);
        Assert.Contains("b@x.test", r.LastResult);
        Assert.True(r.AnyDelivered);
    }

    [Fact]
    public void รายงาน_ล้มทุกราย_ERROR_ไม่ประทับว่าส่งแล้ว()
    {
        var r = ScheduledReportOutcome.Summarize(2, new List<(string, string?)> { ("a@x.test", null), ("b@x.test", "x") }, null);
        Assert.StartsWith("ERROR", r.LastResult);
        Assert.False(r.AnyDelivered);
        Assert.Contains("ส่งทันที", r.LastResult);                          // ทางไปต่อ
    }

    [Fact]
    public void รายงาน_ไม่ได้เริ่มส่ง_SKIPPED_และข้อความยาวถูกตัดที่รายละเอียดไม่ใช่ทางไปต่อ()
    {
        var skipped = ScheduledReportOutcome.Summarize(0, Array.Empty<(string, string?)>(), "ยังไม่มีอีเมลผู้รับ");
        Assert.StartsWith("SKIPPED", skipped.LastResult);
        Assert.False(skipped.AnyDelivered);

        var many = Enumerable.Range(0, 40).Select(i => ($"user{i}@example.test", (string?)"timeout")).ToList();
        var r = ScheduledReportOutcome.Summarize(40, many, null);
        Assert.True(r.LastResult.Length <= 480);
        Assert.Contains("ส่งทันที", r.LastResult);
    }
}

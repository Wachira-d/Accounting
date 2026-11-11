using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 259 ชุด D (Autopilot ส่วนที่เหลือ) — ตัวตัดสิน pure ทั้งสองทิศ:
/// B-M3 <see cref="RecurringPolicyRestamp"/> · B-M4 <see cref="RecurringSchedule.JournalEntryDateUtc"/> · B-L2/L3 <see cref="RecurringRunKey"/> ·
/// B-L1 <see cref="DocumentFeatureDenial"/> · B-L4 <see cref="SystemActor"/> · B-M6 <see cref="EtaxRdSubmission"/>
/// </summary>
public class AutopilotRound259Tests
{
    // ───── B-M3 ─────

    [Fact]
    public void BM3_ผู้แก้มีสิทธิ์อนุมัติ_แก้ค่าที่ผูก_ประทับใหม่()
        => Assert.Equal(RecurringPolicyRestamp.Outcome.Restamp, RecurringPolicyRestamp.Decide(liveBeforeEdit: true, bindingChanged: true, editorCanApprove: true));

    [Fact]
    public void BM3_ทิศตรงข้าม_ผู้แก้ไม่มีสิทธิ์_หยุดมีผล_และข้อความบอกใครเปิดคืนได้()
    {
        Assert.Equal(RecurringPolicyRestamp.Outcome.Suspend, RecurringPolicyRestamp.Decide(true, true, editorCanApprove: false));
        var named = RecurringPolicyRestamp.SuspendedNotice(hadCertificate: false, new[] { "คุณสมชาย" });
        Assert.Contains("หยุดมีผล", named);
        Assert.Contains("คุณสมชาย", named);
        Assert.Contains("ขั้น \"อนุมัติ\"", named);
        var unnamed = RecurringPolicyRestamp.SuspendedNotice(hadCertificate: true, Array.Empty<string>());
        Assert.Contains("Document.Approve", unnamed);
        Assert.Contains("นโยบายอนุมัติอัตโนมัติ", unnamed);
    }

    [Theory]
    [InlineData(false, true, true)]    // ค่าที่ประทับไม่ตรงอยู่ก่อนแก้ — ไม่ชุบชีวิต
    [InlineData(true, false, true)]    // แก้แค่ชื่อ/คำอธิบาย — ไม่แตะ
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void BM3_ไม่ตรงอยู่ก่อนหรือไม่ได้แก้ค่าที่ผูก_ไม่ทำอะไร(bool live, bool changed, bool canApprove)
        => Assert.Equal(RecurringPolicyRestamp.Outcome.None, RecurringPolicyRestamp.Decide(live, changed, canApprove));

    // ───── B-M4 ─────

    [Fact]
    public void BM4_รอบที่ใช้งวด_ใบสำคัญลงวันที่ของงวด_ไม่ใช่วันนี้()
    {
        var now = new DateTime(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc);
        var period = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 8, 31), RecurringSchedule.JournalEntryDateUtc(period, now, consumesPeriod: true).Date);
    }

    [Fact]
    public void BM4_ทิศตรงข้าม_รอบพิเศษก่อนกำหนด_ลงวันนี้ตามปฏิทินไทย()
    {
        var now = new DateTime(2026, 11, 10, 18, 0, 0, DateTimeKind.Utc);   // 01:00 น. 11 พ.ย. เวลาไทย
        var future = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 11), RecurringSchedule.JournalEntryDateUtc(future, now, consumesPeriod: false).Date);
    }

    [Fact]
    public void BM4_ข้อความข้ามงวดปิด_บอกงวดเป็นพศ_และไม่ลงงวดอื่น()
    {
        var note = RecurringSchedule.LockedPeriodSkipNote(new DateTime(2026, 9, 30), "งวด 2569/09 ปิดแล้ว");
        Assert.Contains("30/09/2569", note);
        Assert.Contains("ไม่ลงรายการของงวดนี้ไปไว้งวดอื่น", note);
    }

    // ───── B-L2/L3 ─────

    [Fact]
    public void BL2_กุญแจงวดคงที่ต่องวด_ต่างกันระหว่างงวด_และระหว่างรายการ()
    {
        var a = Guid.NewGuid();
        var p = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(RecurringRunKey.ForPeriod(a, p), RecurringRunKey.For(a, p, consumesPeriod: true, confirmedExtra: false));
        Assert.NotEqual(RecurringRunKey.ForPeriod(a, p), RecurringRunKey.ForPeriod(a, p.AddMonths(1)));
        Assert.NotEqual(RecurringRunKey.ForPeriod(a, p), RecurringRunKey.ForPeriod(Guid.NewGuid(), p));
        Assert.True(RecurringRunKey.For(a, p, consumesPeriod: false, confirmedExtra: false)!.Length <= RecurringRunKey.MaxLength);
    }

    [Fact]
    public void BL3_รอบพิเศษใช้กุญแจล่วงหน้า_ยืนยันแล้วไม่มีกุญแจ()
    {
        var a = Guid.NewGuid();
        var p = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var early = RecurringRunKey.For(a, p, consumesPeriod: false, confirmedExtra: false);
        Assert.StartsWith(RecurringRunKey.ForPeriod(a, p), early);
        Assert.NotEqual(RecurringRunKey.ForPeriod(a, p), early);   // ใบล่วงหน้าไม่กินงวด (คำตัดสินข้อ 150)
        Assert.Equal(early, RecurringRunKey.For(a, p, consumesPeriod: false, confirmedExtra: false));   // กดซ้ำ = กุญแจเดิม ⇒ unique index กัน
        Assert.Null(RecurringRunKey.For(a, p, consumesPeriod: false, confirmedExtra: true));
        // ทิศตรงข้าม: รอบที่ใช้งวดมีกุญแจเสมอ แม้ส่ง confirmedExtra มา (กันซ้ำของงวดห้ามปิดได้)
        Assert.NotNull(RecurringRunKey.For(a, p, consumesPeriod: true, confirmedExtra: true));
    }

    [Fact]
    public void BL2_ใบเดิมเป็นใบล่าสุดของรายการแล้ว_ไม่นับครั้งซ้ำ()
    {
        var d = Guid.NewGuid();
        Assert.False(RecurringRunKey.CountsAsNewRun(d, d));
        Assert.True(RecurringRunKey.CountsAsNewRun(Guid.NewGuid(), d));
        Assert.True(RecurringRunKey.CountsAsNewRun(null, d));
    }

    // ───── B-L1 ─────

    [Theory]
    [InlineData(SubscriptionStatus.Expired, "หมดอายุ")]
    [InlineData(SubscriptionStatus.Suspended, "ระงับ")]
    [InlineData(SubscriptionStatus.Cancelled, "ยกเลิก")]
    public void BL1_แพ็กเกจหยุด_บอกให้ต่ออายุ_และรายการประจำออกงวดล่าสุด(SubscriptionStatus s, string word)
    {
        var (msg, code) = DocumentFeatureDenial.Describe(s);
        Assert.Equal(DocumentFeatureDenial.RuleCodeInactive, code);
        Assert.Contains(word, msg);
        Assert.Contains("ต่ออายุ", msg);
        Assert.Contains("งวดล่าสุด", msg);
    }

    [Fact]
    public void BL1_ทิศตรงข้าม_แพ็กเกจใช้งานอยู่แต่ไม่มีฟีเจอร์_บอกให้อัปเกรด_ไม่บอกว่าหมดอายุ()
    {
        var (msg, code) = DocumentFeatureDenial.Describe(SubscriptionStatus.Active);
        Assert.Equal(DocumentFeatureDenial.RuleCodeNoFeature, code);
        Assert.Contains("อัปเกรด", msg);
        Assert.DoesNotContain("หมดอายุ", msg);
        Assert.Equal(DocumentFeatureDenial.RuleCodeInactive, DocumentFeatureDenial.Describe(SubscriptionStatus.Active, inactiveReadOnly: true).RuleCode);
        Assert.DoesNotContain("หมดอายุ", DocumentFeatureDenial.Describe(null).Message);   // ไม่รู้สถานะ = ไม่เดา
    }

    // ───── B-L4 ─────

    [Fact]
    public void BL4_ผู้กระทำของระบบทุกตัว_ขึ้นต้นเดียวกัน_และค่าเดิมยังรู้จัก()
    {
        foreach (var a in new[] { SystemActor.Recurring, SystemActor.QuotationAutopilot, SystemActor.BankAutoSettle, SystemActor.PostPayment, SystemActor.EmailSchedule })
        {
            Assert.StartsWith(SystemActor.Prefix, a);
            Assert.True(SystemActor.IsSystem(a));
            Assert.StartsWith("ระบบ", SystemActor.Display(a));
        }
        foreach (var legacy in SystemActor.Legacy) Assert.True(SystemActor.IsSystem(legacy));
        Assert.Equal(BankMatchAttribution.BankAutoSettle, SystemActor.BankAutoSettle);
    }

    [Fact]
    public void BL4_ทิศตรงข้าม_ผู้ใช้จริงไม่ใช่ระบบ_และแสดงค่าเดิม()
    {
        var user = Guid.NewGuid().ToString();
        Assert.False(SystemActor.IsSystem(user));
        Assert.False(SystemActor.IsSystem("someone@example.com"));
        Assert.False(SystemActor.IsSystem(null));
        Assert.Equal(user, SystemActor.Display(user));
    }

    // ───── B-M6 ─────

    [Theory]
    [InlineData(true, 200, "accepted", EtaxRdSubmission.Outcome.Accepted)]
    [InlineData(true, 202, "pending", EtaxRdSubmission.Outcome.Submitted)]
    [InlineData(true, 200, null, EtaxRdSubmission.Outcome.Submitted)]
    [InlineData(false, 400, null, EtaxRdSubmission.Outcome.Rejected)]
    [InlineData(false, 422, null, EtaxRdSubmission.Outcome.Rejected)]
    [InlineData(false, 409, null, EtaxRdSubmission.Outcome.Rejected)]
    [InlineData(false, 401, null, EtaxRdSubmission.Outcome.ConfigError)]
    [InlineData(false, 403, null, EtaxRdSubmission.Outcome.ConfigError)]
    [InlineData(false, 404, null, EtaxRdSubmission.Outcome.ConfigError)]
    [InlineData(false, 405, null, EtaxRdSubmission.Outcome.ConfigError)]
    [InlineData(false, 410, null, EtaxRdSubmission.Outcome.Transient)]
    [InlineData(false, 429, null, EtaxRdSubmission.Outcome.Transient)]
    [InlineData(false, 408, null, EtaxRdSubmission.Outcome.Transient)]
    [InlineData(false, 503, null, EtaxRdSubmission.Outcome.Transient)]
    public void BM6_จัดผลตอบกลับของกรม(bool ok, int http, string? rd, EtaxRdSubmission.Outcome expected)
        => Assert.Equal(expected, EtaxRdSubmission.Classify(ok, http, rd));

    [Fact]
    public void BM6_ประทับส่งแล้วเฉพาะเมื่อกรมตอบรับ_ปฏิเสธหรือล้มไม่ประทับ()
    {
        Assert.Equal((EtaxStatus.Accepted, true), EtaxRdSubmission.StatusFor(EtaxRdSubmission.Outcome.Accepted));
        Assert.Equal((EtaxStatus.Submitted, true), EtaxRdSubmission.StatusFor(EtaxRdSubmission.Outcome.Submitted));
        Assert.Equal((EtaxStatus.Rejected, false), EtaxRdSubmission.StatusFor(EtaxRdSubmission.Outcome.Rejected));
        Assert.Equal((EtaxStatus.Error, false), EtaxRdSubmission.StatusFor(EtaxRdSubmission.Outcome.Transient));
    }

    [Fact]
    public void BM6_ลองใหม่ได้เฉพาะลงนามแล้วหรือส่งล้มทางเทคนิค()
    {
        var at = DateTime.UtcNow;
        Assert.True(EtaxRdSubmission.CanSubmit(EtaxStatus.Signed, at, "sig", null));
        Assert.True(EtaxRdSubmission.CanSubmit(EtaxStatus.Error, at, "sig", EtaxRdSubmission.ErrorSubmit));
        Assert.True(EtaxRdSubmission.CanSubmit(EtaxStatus.Error, at, "sig", EtaxRdSubmission.ErrorNotConfigured));
        // ทิศตรงข้าม: ลงนามล้ม · ยังไม่ลงนาม · กรมปฏิเสธ · ส่งแล้ว · ยกเลิก
        Assert.False(EtaxRdSubmission.CanSubmit(EtaxStatus.Error, at, "sig", EtaxRdSubmission.ErrorSign));
        Assert.False(EtaxRdSubmission.CanSubmit(EtaxStatus.Error, null, null, EtaxRdSubmission.ErrorSubmit));
        Assert.False(EtaxRdSubmission.CanSubmit(EtaxStatus.Generated, null, null, null));
        Assert.False(EtaxRdSubmission.CanSubmit(EtaxStatus.Rejected, at, "sig", "RD_400"));
        Assert.False(EtaxRdSubmission.CanSubmit(EtaxStatus.Submitted, at, "sig", null));
        Assert.False(EtaxRdSubmission.CanSubmit(EtaxStatus.Voided, at, "sig", null));
    }

    [Fact]
    public void BM6_ช่วงงานวันที่1ถึง15เวลาไทย_และกำหนดส่งวันที่15เดือนถัดไป()
    {
        Assert.True(EtaxRdSubmission.InSubmissionWindow(new DateTime(2026, 11, 14, 18, 0, 0, DateTimeKind.Utc)));   // 15 พ.ย. 01:00 น.
        Assert.False(EtaxRdSubmission.InSubmissionWindow(new DateTime(2026, 11, 15, 18, 0, 0, DateTimeKind.Utc)));  // 16 พ.ย. 01:00 น.
        Assert.Equal(new DateTime(2026, 12, 15), EtaxRdSubmission.DeadlineFor(new DateTime(2026, 11, 30, 3, 0, 0, DateTimeKind.Utc)).Date);
        Assert.Equal(new DateTime(2026, 11, 1), EtaxRdSubmission.CurrentMonthStart(new DateTime(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc)).Date);
    }

    [Fact]
    public void BM6_คิวเว้นช่วงยาวขึ้น_และสรุปงานไม่มีงาน_ไม่แจ้ง()
    {
        var now = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(EtaxRdSubmission.NextAttemptAt(1, now) < EtaxRdSubmission.NextAttemptAt(3, now));
        Assert.Equal(now.AddHours(24), EtaxRdSubmission.NextAttemptAt(9, now));
        Assert.Null(EtaxRdSubmission.JobSummary(0, 0, 0, now));
        Assert.Contains("ไม่สำเร็จ 2", EtaxRdSubmission.JobSummary(5, 3, 2, now));
    }

    // ───── ฝ่ายค้าน 259-D ─────

    [Fact]
    public void H2_คีย์หมดอายุ_401_เป็นข้อผิดพลาดตั้งค่า_ส่งซ้ำได้หลังแก้_ไม่ใช่กรมปฏิเสธ()
    {
        var o = EtaxRdSubmission.Classify(false, 401, null);
        var (status, stamp) = EtaxRdSubmission.StatusFor(o);
        Assert.Equal(EtaxStatus.Error, status);
        Assert.False(stamp);
        Assert.True(EtaxRdSubmission.CanSubmit(status, DateTime.UtcNow, "sig", EtaxRdSubmission.ErrorAuth));
        var msg = EtaxRdSubmission.FailureMessage("HTTP 401", new DateTime(2026, 10, 28), new DateTime(2026, 11, 2), o);
        Assert.Contains("RD API Key", msg);
        Assert.DoesNotContain("แก้ข้อมูลเอกสาร", msg);
        // ทิศตรงข้าม: 422 ยังเป็นการปฏิเสธเนื้อเอกสาร (ไม่ลองซ้ำเอง)
        Assert.Equal(EtaxStatus.Rejected, EtaxRdSubmission.StatusFor(EtaxRdSubmission.Classify(false, 422, null)).Status);
        Assert.Contains("หยุดส่งฉบับที่เหลือ", EtaxRdSubmission.JobSummary(1, 0, 1, new DateTime(2026, 11, 15), msg));
    }

    [Fact]
    public void M3_เดือนภาษีและกำหนดส่งตามวันที่เอกสาร_ไม่ใช่วันลงนาม()
    {
        var docDate = new DateTime(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 10, 1), EtaxRdSubmission.FilingMonthOf(docDate).Date);
        Assert.Equal(new DateTime(2026, 11, 15), EtaxRdSubmission.DeadlineFor(docDate).Date);
        Assert.Contains("กำหนดส่ง 15/11/2569", EtaxRdSubmission.DeadlineLabel(docDate, new DateTime(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void M4_เลยวันที่15_เป็นเลยกำหนด_ลองวันละครั้ง_ทิศตรงข้ามยังไม่ถึงกำหนดใช้คิวปกติ()
    {
        var docDate = new DateTime(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc);
        var after = new DateTime(2026, 11, 16, 3, 0, 0, DateTimeKind.Utc);
        var before = new DateTime(2026, 11, 14, 3, 0, 0, DateTimeKind.Utc);
        Assert.True(EtaxRdSubmission.IsOverdue(docDate, after));
        Assert.Contains("เลยกำหนดส่ง", EtaxRdSubmission.DeadlineLabel(docDate, after));
        Assert.Equal(after.AddHours(24), EtaxRdSubmission.NextAttemptAt(1, after, docDate));
        Assert.False(EtaxRdSubmission.IsOverdue(docDate, before));
        Assert.Equal(before.AddHours(1), EtaxRdSubmission.NextAttemptAt(1, before, docDate));
    }

    [Fact]
    public void H1_ซ่อมแถวเก่าเฉพาะเลขส่งที่แต่งขึ้น()
    {
        var sql = EtaxRdSubmission.LegacyStampRepairSql();
        Assert.Contains(sql, s => s.Contains("LIKE 'ERR-%'") && s.Contains($"\"Status\" = {(int)EtaxStatus.Submitted}") && s.Contains("\"SubmittedAt\" = NULL"));
        Assert.Contains(sql, s => s.Contains("^RD-[0-9]+$") && s.Contains("= 17") && !s.Contains("\"Status\" ="));
        Assert.All(sql, s => Assert.DoesNotContain("{", s));   // ExecuteSqlRaw อ่านวงเล็บปีกกาเป็นตัวแทนค่า (ล้มตอนบูต)
    }

    [Fact]
    public void M1_ปีบัญชี_ข้ามปีต้องหยุด_ปีเดียวกันไม่หยุด_และข้อความลงย้อนหลัง()
    {
        var now = new DateTime(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc);
        Assert.True(RecurringSchedule.CrossesFiscalYear(new DateTime(2025, 12, 31), now, 1));
        Assert.False(RecurringSchedule.CrossesFiscalYear(new DateTime(2026, 1, 31), now, 1));
        // ปีบัญชีเริ่ม ต.ค.: ก.ย. 2026 อยู่ปีก่อน · ต.ค. 2026 ปีเดียวกัน
        Assert.True(RecurringSchedule.CrossesFiscalYear(new DateTime(2026, 9, 30), now, 10));
        Assert.False(RecurringSchedule.CrossesFiscalYear(new DateTime(2026, 10, 1), now, 10));
        Assert.True(RecurringSchedule.IsBackfill(new DateTime(2026, 10, 31), now));
        Assert.False(RecurringSchedule.IsBackfill(new DateTime(2026, 11, 1), now));
        Assert.Null(RecurringSchedule.BackfillNote(Array.Empty<DateTime>()));
        var note = RecurringSchedule.BackfillNote(new[] { new DateTime(2026, 9, 30), new DateTime(2026, 8, 31) });
        Assert.Contains("ย้อนหลัง 2 งวด", note);
        Assert.Contains("31/08/2569", note);
    }
}

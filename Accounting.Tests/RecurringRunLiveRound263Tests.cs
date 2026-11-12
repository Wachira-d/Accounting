using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 263 (คำร้องผู้ใช้): หน้ารายละเอียดรายการประจำยังขึ้นกล่องเหลือง "รอบล่าสุดสร้างเอกสารแล้ว แต่มีเรื่องต้องทำต่อ" + "อนุมัติอัตโนมัติ … ไม่สำเร็จ:
/// ผู้ติดต่อนี้ยังค้างชำระ 3 ใบ … DRAFT-43a2828d ยังเป็นร่าง … ไม่ได้ส่งอีเมล … ข้าม 2 งวด" ทั้งที่ผู้ใช้อนุมัติใบนั้นแล้ว (INV-20261026-0001) และรับชำระครบ
/// (REC-20261026-0002) ⇒ ข้อความถูกแช่แข็งตอนรัน (แถวก่อนรอบ 247 มีแต่ LastRunNotice — ตัวประกอบโชว์ข้อความดิบตลอดไป) ·
/// ตอนนี้ตัวประเมินสดตัวเดียว <see cref="RecurringRunOutcome.Build"/> ตรวจทุกเรื่องกับสถานะ "ตอนนี้" — ล็อกทั้งสองทิศ
/// </summary>
public class RecurringRunLiveRound263Tests
{
    private static readonly Guid DocId = Guid.Parse("43a2828d-0000-4000-8000-000000000001");
    private static readonly Guid ContactId = Guid.Parse("c0000000-0000-4000-8000-000000000002");
    private const string CreditWarning = "ผู้ติดต่อนี้ยังค้างชำระ 3 ใบ รวม 9,750.00 THB — ตรวจวงเงินเครดิตก่อนอนุมัติเอกสารใหม่";
    private static readonly DateTime PeriodUtc = new(2026, 10, 25, 17, 0, 0, DateTimeKind.Utc);   // 26/10/2569 เวลาไทย

    private static RecurringRunDoc Doc(DocumentStatus status, string? no = "INV-20261026-0001")
        => new(DocId, no, status, DocumentType.Invoice, PeriodUtc);

    /// <summary>ข้อความแถวเก่าแบบที่ผู้ใช้เห็น — ประกอบจากตัวเขียนจริง (AutoApproveFailure.Message + ประโยคอีเมล/ข้ามงวดของ RecurringTransactionService)</summary>
    private static string LegacyNotice()
        => string.Join(" · ", new[]
        {
            AutoApproveFailure.Message("รายการประจำ (อนุมัติอัตโนมัติ)", "DRAFT-43a2828d", AutoApproveFailureKind.StillDraft,
                "เอกสารมีจุดที่ต้องตรวจก่อนยืนยันการอนุมัติ (1 รายการ): " + CreditWarning, DocumentStatus.Draft),
            "ยังไม่ได้ส่งอีเมล — เอกสารไม่ได้ถูกอนุมัติ (ใบร่างยังไม่มีเลขที่จริงตาม §86/4) อนุมัติที่หน้าเอกสารแล้วส่งจากที่นั่น",
            "ข้าม 2 งวดที่ค้าง (ตั้งแต่งวด 26/08/2569) ระหว่างที่ระบบสร้างเอกสารไม่ได้ — ออกเฉพาะงวดล่าสุด · ถ้างวดที่ข้ามยังไม่ได้ออกเอง กด \"สร้างทันที\" เพื่อออกเพิ่มทีละใบ",
        });

    private static RecurringRunOutcomeView? Build(RecurringRunFacts? facts, RecurringRunDoc? doc, RecurringRunLive? live = null, string? legacy = null)
        => RecurringRunOutcome.Build(facts, legacy, doc, ContactId, "ap@customer.test", false, true, live);

    [Fact]
    public void แถวเก่า_อ่านข้อความเป็นข้อเท็จจริงได้ครบ_อนุมัติ_อีเมล_ข้ามงวด()
    {
        var f = RecurringRunOutcome.FactsFromLegacyNotice(LegacyNotice())!;
        Assert.NotNull(f);
        Assert.Contains(CreditWarning, f.ApproveBlockedReason);
        Assert.True(f.EmailHeldForDraft);
        Assert.Equal(2, f.SkippedPeriods);
        Assert.Equal(new DateTime(2026, 8, 26), f.SkippedFrom!.Value.Date);
        Assert.Null(f.LegacyOther);   // ทุกส่วนรู้จัก (รวมท้ายประโยค " · แก้ตามเหตุผล…" และ " · ถ้างวดที่ข้าม…")
    }

    [Fact]
    public void ทิศเดิม_ใบยังเป็นร่าง_ขั้นอนุมัติยังค้าง_ไม่ใช่แก้ครบ()
    {
        var v = Build(null, Doc(DocumentStatus.Draft, "DRAFT-43a2828d"), legacy: LegacyNotice())!;
        Assert.False(v.AllResolved);
        Assert.Contains(v.Steps, s => s.Contains("เปิดใบร่างเพื่ออนุมัติ") && s.Contains("ค้างชำระ 3 ใบ"));
        Assert.Contains(v.Actions, a => a.Kind == "openDocument");
        Assert.Contains(v.Actions, a => a.Kind == "dismissSkipped");
        Assert.DoesNotContain(v.Resolved!, r => r.StartsWith("อนุมัติแล้ว"));
    }

    [Fact]
    public void รายงานผู้ใช้_อนุมัติแล้วรับชำระครบ_ขั้นอนุมัติและอีเมลจบ_บอกใครเมื่อไร_เหลือแค่งวดที่ข้าม()
    {
        var live = new RecurringRunLive { Approval = new AutoApproveFailure.Resolution("2026-10-26 22:37", "สมชาย ใจดี") };
        var v = Build(null, Doc(DocumentStatus.Paid), live, LegacyNotice())!;
        Assert.DoesNotContain("รอบล่าสุดสร้างเอกสารแล้ว แต่มีเรื่องต้องทำต่อ", v.Headline);
        Assert.DoesNotContain(v.Steps, s => s.Contains("อนุมัติ") && s.Contains("ค้างชำระ"));
        Assert.DoesNotContain(v.Steps, s => s.StartsWith("อีเมล"));
        Assert.DoesNotContain(v.Steps, s => s.Contains("DRAFT-43a2828d"));
        var ap = Assert.Single(v.Resolved!, r => r.StartsWith("อนุมัติแล้ว"));
        Assert.Contains("INV-20261026-0001", ap);
        Assert.Contains("2026-10-26 22:37", ap);
        Assert.Contains("สมชาย ใจดี", ap);
        Assert.Contains("รับชำระครบแล้ว", ap);
        Assert.Contains(v.Resolved!, r => r.Contains("ไม่ต้องส่งเอกสารเรียกเก็บทางอีเมล"));
        // คำตัดสิน 263-1-3: งวดที่ข้ามยังค้างจนออกครบหรือปิดเรื่อง
        Assert.Contains(v.Steps, s => s.Contains("ข้าม 2 งวด") && s.Contains("26/08/2569"));
        Assert.Contains(v.Actions, a => a.Kind == "dismissSkipped");
        Assert.False(v.AllResolved);
    }

    [Fact]
    public void ปิดเรื่องงวดที่ข้ามแล้ว_ทุกเรื่องจบ_ไม่มีกล่องเตือน()
    {
        var facts = RecurringRunOutcome.FactsFromLegacyNotice(LegacyNotice())! with
        {
            SkippedDismissedAt = new DateTime(2026, 10, 27, 3, 0, 0, DateTimeKind.Utc), SkippedDismissedBy = "สมชาย ใจดี",
        };
        var v = Build(facts, Doc(DocumentStatus.Paid), legacy: LegacyNotice())!;
        Assert.True(v.AllResolved);
        Assert.Empty(v.Steps);
        Assert.Empty(v.Actions);
        Assert.Contains(v.Resolved!, r => r.Contains("ปิดเรื่องแล้ว") && r.Contains("27/10/2569 10:00") && r.Contains("สมชาย ใจดี"));
    }

    [Fact]
    public void ออกงวดที่ข้ามครบด้วยสร้างงวดที่ข้าม_เรื่องจบ_ระหว่างทางบอกเหลือกี่งวด()
    {
        var f = new RecurringRunFacts { SkippedPeriods = 2, SkippedFrom = PeriodUtc.AddMonths(-2) };
        var one = f.SkipCarry(issuedOneNow: true)!;
        Assert.Equal(1, one.SkippedRemaining);
        var mid = Build(one, Doc(DocumentStatus.Approved))!;
        Assert.Equal("ยังเหลืองวดที่ข้าม 1 งวด", mid.Headline);
        Assert.Contains(mid.Steps, s => s.Contains("ออกเพิ่มแล้ว 1 จาก 2 งวด"));
        var two = one.SkipCarry(issuedOneNow: true)!;
        Assert.Equal(0, two.SkippedRemaining);
        var done = Build(two, Doc(DocumentStatus.Approved))!;
        Assert.True(done.AllResolved);
        Assert.Contains(done.Resolved!, r => r.Contains("ออกเพิ่มครบแล้ว"));
        // ปิดเรื่องแล้ว ⇒ ไม่พาไปรอบถัดไป
        Assert.Null((f with { SkippedDismissedAt = DateTime.UtcNow }).SkipCarry(false));
    }

    [Fact]
    public void ใบถูกยกเลิก_ข้อความต่างจากอนุมัติแล้ว_ไม่บอกว่าอนุมัติ()
    {
        var facts = new RecurringRunFacts { ApproveBlockedReason = CreditWarning, ApproveWarnings = new[] { CreditWarning }, EmailHeldForDraft = true };
        var v = Build(facts, Doc(DocumentStatus.Voided))!;
        Assert.True(v.AllResolved);
        Assert.Contains(v.Resolved!, r => r.Contains("ถูกยกเลิก/ปฏิเสธแล้ว"));
        Assert.DoesNotContain(v.Resolved!, r => r.StartsWith("อนุมัติแล้ว"));
    }

    [Fact]
    public void อนุมัติแล้วแต่ยังไม่ชำระ_ไม่มีบันทึกส่งอีเมล_ยังเตือนส่ง_มีบันทึกส่งแล้วจบ()
    {
        var facts = new RecurringRunFacts { ApproveBlockedReason = CreditWarning, EmailHeldForDraft = true };
        var pending = Build(facts, Doc(DocumentStatus.Approved))!;
        Assert.False(pending.AllResolved);
        Assert.Contains(pending.Steps, s => s.StartsWith("อีเมล:"));
        var sent = Build(facts, Doc(DocumentStatus.Approved), new RecurringRunLive { EmailSentAtUtc = new DateTime(2026, 10, 26, 16, 0, 0, DateTimeKind.Utc) })!;
        Assert.True(sent.AllResolved);
        Assert.Contains(sent.Resolved!, r => r.Contains("ส่งอีเมล") && r.Contains("26/10/2569 23:00"));
        var line = Build(facts, Doc(DocumentStatus.Approved), new RecurringRunLive { LineSent = true })!;
        Assert.True(line.AllResolved);
    }

    [Fact]
    public void สมุดรายวันค้างร่าง_ค้างจนลงบัญชี_ลงแล้วจบ_ไม่พบใบยังค้าง()
    {
        var jid = Guid.NewGuid();
        var facts = new RecurringRunFacts { JournalPostBlockedReason = "งวดบัญชีปิดแล้ว", JournalEntryId = jid, JournalEntryNumber = "JV-0007" };
        var draft = Build(facts, null, new RecurringRunLive { Journal = new RecurringRunJournal(jid, "JV-0007", JournalEntryStatus.Draft) })!;
        Assert.False(draft.AllResolved);
        Assert.Contains(draft.Actions, a => a.Kind == "openJournal" && a.Url == "/pages/journals.html?entryId=" + jid.ToString("D"));
        var posted = Build(facts, null, new RecurringRunLive { Journal = new RecurringRunJournal(jid, "JV-0007", JournalEntryStatus.Posted) })!;
        Assert.True(posted.AllResolved);
        Assert.False(Build(facts, null)!.AllResolved);   // ไม่มีหลักฐาน = ยังค้าง (ไม่เดาว่าจบ)
        // แถวเก่า: ข้อความสมุดรายวันอ่านเลขที่ได้
        var legacy = RecurringRunOutcome.FactsFromLegacyNotice("ลงบัญชีสมุดรายวัน JV-0007 อัตโนมัติไม่สำเร็จ (ค้างเป็นร่าง): งวดปิด — เปิดสมุดรายวันแล้วกดลงบัญชีเอง")!;
        Assert.Equal("JV-0007", legacy.JournalEntryNumber);
    }

    [Fact]
    public void ข้อความแถวเก่าที่ไม่รู้จัก_แสดงแบบเดิม_ไม่บอกว่าแก้แล้ว()
    {
        Assert.Null(RecurringRunOutcome.FactsFromLegacyNotice("ข้อความเก่าที่ไม่รู้จัก"));
        var v = Build(null, Doc(DocumentStatus.Paid), legacy: "ข้อความเก่าที่ไม่รู้จัก")!;
        Assert.False(v.AllResolved);
        Assert.Contains(v.Steps, s => s.Contains("ข้อความเก่าที่ไม่รู้จัก"));
    }

    [Fact]
    public void ข้ามงวดซ้ำในรอบเดียวไม่นับซ้ำ_ข้ามช่วงใหม่รวมกับที่ยังค้าง()
    {
        var from1 = new DateTime(2026, 6, 25, 17, 0, 0, DateTimeKind.Utc);
        var a = RecurringRunOutcome.MergeSkip(new RecurringRunFacts(), 2, from1);
        Assert.Equal(2, RecurringRunOutcome.MergeSkip(a, 2, from1).SkippedPeriods);
        var partly = a with { SkippedIssued = 1 };
        var b = RecurringRunOutcome.MergeSkip(partly, 3, from1.AddMonths(4));
        Assert.Equal(4, b.SkippedPeriods);
        Assert.Equal(0, b.SkippedIssued);
        Assert.Equal(from1, b.SkippedFrom);
    }

    [Fact]
    public void ข้อเท็จจริงใหม่_บันทึกและอ่านกลับได้_ฟิลด์ภายในไม่ถูกเก็บ()
    {
        var f = new RecurringRunFacts { SkippedPeriods = 2, SkippedIssued = 1, JournalEntryNumber = "JV-1", LegacyOther = new[] { "x" } };
        var json = JsonSerializer.Serialize(f);
        Assert.DoesNotContain("LegacyOther", json);
        Assert.DoesNotContain("SkippedRemaining", json);
        var back = JsonSerializer.Deserialize<RecurringRunFacts>(json)!;
        Assert.Equal(1, back.SkippedIssued);
        Assert.Equal("JV-1", back.JournalEntryNumber);
    }

    [Fact]
    public void หมายเหตุแก้แล้ว_อ่านใครเมื่อไร_แปลงidเป็นชื่อ_ไม่พบชื่อคงเดิม()
    {
        var uid = Guid.Parse("5ca33982-1111-4222-8333-444455556666");
        var notes = AutoApproveFailure.MarkResolved(LegacyNotice(), new DateTime(2026, 10, 26, 15, 37, 0, DateTimeKind.Utc), uid.ToString());
        var res = AutoApproveFailure.LastResolution(notes)!;
        Assert.Equal("2026-10-26 22:37", res.AtBangkok);
        Assert.Equal(uid.ToString(), res.By);
        Assert.Equal(uid, Assert.Single(AutoApproveFailure.ApproverIds(notes)));
        var named = AutoApproveFailure.WithApproverNames(notes, new Dictionary<Guid, string> { [uid] = "สมชาย ใจดี" })!;
        Assert.Contains("โดย สมชาย ใจดี", named);
        Assert.DoesNotContain(uid.ToString(), named);
        Assert.Equal(notes, AutoApproveFailure.WithApproverNames(notes, new Dictionary<Guid, string>()));
        Assert.Null(AutoApproveFailure.LastResolution(LegacyNotice()));   // ยังไม่ปิด = ไม่มีหลักฐานอนุมัติ
        Assert.True(AutoApproveFailure.HasOpenFailure(LegacyNotice()));
        Assert.False(AutoApproveFailure.HasOpenFailure(notes));
    }
}

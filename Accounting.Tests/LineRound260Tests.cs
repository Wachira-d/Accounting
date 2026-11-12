using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 260-4 (คำตัดสิน 260-4-1…260-4-6) — ตัวตัดสิน pure: ชั่วโมงเงียบ · กระดิ่ง LINE ล้มซ้ำ · key ปลายทางทดสอบ · migration</summary>
public class LineRound260Tests
{
    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void ชั่วโมงเงียบข้ามเที่ยงคืน_เลื่อนไปสิ้นช่วงเวลาไทย_นอกช่วงส่งเลย()
    {
        // 22:00 น. ไทย (15:00 UTC) ⇒ เลื่อนถึง 08:00 น. ไทยวันถัดไป (01:00 UTC)
        Assert.Equal(Utc(2026, 11, 13, 1), LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 15), true, 21, 8, "DocumentOverdue"));
        // 03:00 น. ไทย (20:00 UTC วันก่อน) ⇒ 08:00 น. ไทยวันเดียวกัน
        Assert.Equal(Utc(2026, 11, 13, 1), LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 20), true, 21, 8, "DocumentDueSoon"));
        // ขอบ: 21:00 พอดี = เงียบ · 08:00 พอดี = ส่งได้ · 20:59 = ส่งได้
        Assert.NotNull(LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 14), true, 21, 8, "DocumentApproved"));
        Assert.Null(LineQuietHours.DeferUntilUtc(Utc(2026, 11, 13, 1), true, 21, 8, "DocumentApproved"));
        Assert.Null(LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 13, 59), true, 21, 8, "DocumentApproved"));
        // ทิศตรงข้าม: กลางวันส่งเลย · ปิดอยู่ส่งเลย · ค่าตั้งเสีย (เริ่ม=สิ้นสุด) ไม่เลื่อนตลอดกาล
        Assert.Null(LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 5), true, 21, 8, "DocumentOverdue"));
        Assert.Null(LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 15), false, 21, 8, "DocumentOverdue"));
        Assert.Null(LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 15), true, 8, 8, "DocumentOverdue"));
    }

    [Fact]
    public void ช่วงไม่ข้ามเที่ยงคืน_และ_ทวง50ทวิถูกเลื่อน()
    {
        // 01:00–05:00 ไทย · 02:30 ไทย = 19:30 UTC วันก่อน ⇒ 05:00 ไทย = 22:00 UTC วันก่อน
        Assert.Equal(Utc(2026, 11, 12, 22), LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 19, 30), true, 1, 5, LineWhtCertCard.QueueTrigger));
        Assert.Null(LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 23), true, 1, 5, LineWhtCertCard.QueueTrigger));   // 06:00 ไทย
        Assert.True(LineQuietHours.InWindow(23, 21, 8) && LineQuietHours.InWindow(0, 21, 8) && !LineQuietHours.InWindow(8, 21, 8));
    }

    [Fact]
    public void ยกเว้นเฉพาะใบเสร็จที่ลูกค้าชำระเองและส่งเอง()
    {
        // ฝ่ายค้าน 260-4 L4: ใบเสร็จยกเว้นเฉพาะลูกค้าชำระเอง (ลิงก์/ผู้ให้บริการรับชำระ) · ใบเสร็จจากพนักงาน/จับคู่ธนาคาร/นำเข้าตอนกลางคืนถูกเลื่อน
        Assert.Null(LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 15), true, 21, 8, ContactLineChannel.ReceiptTrigger, customerInitiated: true));
        Assert.NotNull(LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 15), true, 21, 8, ContactLineChannel.ReceiptTrigger, customerInitiated: false));
        Assert.Null(LineQuietHours.DeferUntilUtc(Utc(2026, 11, 12, 15), true, 21, 8, ContactLineChannel.ManualTrigger));
        foreach (var t in ContactLineChannel.LineTriggers)
            Assert.False(LineQuietHours.IsExempt(t));
        Assert.False(LineQuietHours.IsExempt(LineWhtCertCard.QueueTrigger));
        Assert.False(LineQuietHours.IsExempt(null));
        Assert.False(LineQuietHours.IsExempt(LineWhtCertCard.QueueTrigger, customerInitiated: true));   // ธงลูกค้ามีผลกับใบเสร็จเท่านั้น
    }

    [Fact]
    public void คำเตือนฟอร์มกฎ_ต่อชั่วโมง_เฉพาะช่วงเงียบเปิด_บอกอีเมลกับLINEคนละเวลา()
    {
        var w = LineQuietHours.RuleWarningsByHour(true, 21, 8);
        Assert.Equal(24, w.LineOnly.Count);
        Assert.NotNull(w.LineOnly[22]);
        Assert.NotNull(w.LineOnly[7]);
        Assert.Null(w.LineOnly[8]);
        Assert.Null(w.LineOnly[20]);
        Assert.Contains("08:00", w.LineOnly[22]);
        Assert.Contains("อีเมล", w.LineAndEmail[22]);
        Assert.DoesNotContain("อีเมล", w.LineOnly[22]);
        // ทิศตรงข้าม: ปิดอยู่/ค่าเสีย ⇒ ไม่มีคำเตือนสักชั่วโมง
        Assert.All(LineQuietHours.RuleWarningsByHour(false, 21, 8).LineOnly, x => Assert.Null(x));
        Assert.All(LineQuietHours.RuleWarningsByHour(true, 8, 8).LineAndEmail, x => Assert.Null(x));
    }

    [Fact]
    public void กระดิ่งแยกชนิด_กันซ้ำต่อชนิด_ถอดรหัสไม่ได้ดังทันที()
    {
        Assert.Equal(LineFailureAlert.Kind.ChannelBroken, LineFailureAlert.AlertKind(1, channelBroken: true));
        Assert.Equal(LineFailureAlert.Kind.Decrypt, LineFailureAlert.AlertKind(0, channelBroken: false, decryptFailed: true));
        Assert.Equal(LineFailureAlert.Kind.Repeated, LineFailureAlert.AlertKind(LineFailureAlert.Threshold, false));
        Assert.Null(LineFailureAlert.AlertKind(LineFailureAlert.Threshold - 1, false));
        var ids = Enum.GetValues<LineFailureAlert.Kind>().Select(LineFailureAlert.KindId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains("ENCRYPTION_KEY", LineFailureAlert.Message(LineFailureAlert.Kind.Decrypt, 1, null));
        Assert.DoesNotContain("ผูกใหม่", LineFailureAlert.DecryptRetryNote);
        // M2: ข้อความของแต่ละทางเข้าที่เข้าคิวอัตโนมัติ
        Assert.Contains("สลิป", LineFailureAlert.EnqueueMessage(LineFailureAlert.EnqueueSource.PayrollPaid, "มี.ค.", null));
        Assert.Contains("50 ทวิ", LineFailureAlert.EnqueueTitle(LineFailureAlert.EnqueueSource.WhtCertIssued));
        Assert.Contains("openDoc=", LineFailureAlert.EnqueueActionUrl(LineFailureAlert.EnqueueSource.DocumentApproved, Guid.NewGuid()));
    }

    [Fact]
    public void ข้อความทดสอบไม่มี_GUID()
    {
        var m = LineOaHealth.TestMessage("บริษัท ก จำกัด", "12/11/2026 10:00");
        Assert.Contains("บริษัท ก จำกัด", m);
        Assert.DoesNotMatch("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-", m);
        Assert.DoesNotContain("Messaging API", m);
        Assert.Contains("ร้านค้า", LineOaHealth.TestMessage(null, "x"));
    }

    [Fact]
    public void ด่านบันทึก_และ_ประโยคสถานะ()
    {
        Assert.Null(LineQuietHours.Problem(21, 8));
        Assert.NotNull(LineQuietHours.Problem(8, 8));
        Assert.NotNull(LineQuietHours.Problem(24, 8));
        Assert.NotNull(LineQuietHours.Problem(21, -1));
        Assert.Contains("21:00–08:00", LineQuietHours.Sentence(true, 21, 8));
        Assert.Contains("ใบเสร็จ", LineQuietHours.Sentence(true, 21, 8));
        Assert.StartsWith("ปิด", LineQuietHours.Sentence(false, 21, 8));
        Assert.Contains("08:00", LineQuietHours.DeferNote(Utc(2026, 11, 13, 1), 21, 8));
        Assert.False(LineQuietHours.DefaultEnabled);   // คำตัดสิน 260-4-1: ไม่เปลี่ยนเวลาส่งของบริษัทเดิมเงียบ ๆ
    }

    [Fact]
    public void กระดิ่งล้มซ้ำ_เกณฑ์และช่องเสีย()
    {
        Assert.Null(LineFailureAlert.AlertKind(LineFailureAlert.Threshold - 1, channelBroken: false));
        Assert.NotNull(LineFailureAlert.AlertKind(LineFailureAlert.Threshold, channelBroken: false));
        Assert.NotNull(LineFailureAlert.AlertKind(1, channelBroken: true));
        Assert.Contains("Token", LineFailureAlert.Message(true, 1, "401"));
        Assert.Contains("5 ครั้ง", LineFailureAlert.Message(false, 5, null));
        Assert.Contains("INV-1", LineFailureAlert.EnqueueMessage(LineFailureAlert.EnqueueSource.DocumentApproved, "INV-1", "boom"));
        Assert.Contains("ส่งเอง", LineFailureAlert.EnqueueMessage(LineFailureAlert.EnqueueSource.DocumentApproved, null, null));
    }

    [Fact]
    public void key_ปลายทางทดสอบ_ไปกลับ_และไม่ชนกลุ่มทีม()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, LineOaHealth.ParseEndpointKey(LineOaHealth.EndpointTargetKey(id)));
        Assert.Null(LineOaHealth.ParseEndpointKey(LineOaHealth.TeamGroupTargetKey));
        Assert.Null(LineOaHealth.ParseEndpointKey("ep:not-a-guid"));
        Assert.Null(LineOaHealth.ParseEndpointKey(null));
        // key ปลายทางไม่เข้าเส้นกลุ่มทีม (ไม่รู้จัก ⇒ เหตุ)
        Assert.NotNull(LineOaHealth.TestTarget(LineOaHealth.EndpointTargetKey(id), null, "C1").Problem);
        Assert.Equal("ลูกค้า ก · กลุ่ม LINE \"บัญชี\"", LineOaHealth.EndpointTargetLabel("ลูกค้า ก", NotifyEndpointKind.LineGroup, "บัญชี", null));
        Assert.Equal("ผู้ติดต่อ · LINE ผู้ใช้ (คุณเอ)", LineOaHealth.EndpointTargetLabel(null, NotifyEndpointKind.LineUser, null, "คุณเอ"));
    }

    [Fact]
    public void migration_ชั่วโมงเงียบ_idempotent_ค่าตั้งต้นตรงตัวตัดสิน()
    {
        var sql = string.Join("\n", Round260LineMigration.Statements());
        Assert.All(Round260LineMigration.Statements(), s => Assert.Contains("ADD COLUMN IF NOT EXISTS", s));
        Assert.Contains("\"LineQuietHoursEnabled\" boolean NOT NULL DEFAULT false", sql);
        Assert.Contains("\"LineQuietStartHour\" integer NOT NULL DEFAULT 21", sql);
        Assert.Contains("\"LineQuietEndHour\" integer NOT NULL DEFAULT 8", sql);
        Assert.Contains("\"LineQueues\" ADD COLUMN IF NOT EXISTS \"CustomerInitiated\" boolean NOT NULL DEFAULT false", sql);
    }
}

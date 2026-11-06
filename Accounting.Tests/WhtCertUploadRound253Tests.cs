using System.Text;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §3.2 · §4.3 · §6.3 · AUDIT_249 §3.3 N4) — ตัวตัดสิน pure ของลิงก์อัปโหลด 50 ทวิ · จังหวะทวง · "ไม่หัก ณ ที่จ่าย" ·
/// หลักฐาน e-WHT — ทุกด่านมีทิศตรงข้าม (ไฟล์ถูกผ่าน/ไฟล์ปลอมถูกกัน · ขั้นถึงกำหนดส่ง/ส่งแล้วไม่ส่งซ้ำ · นิติบุคคลเห็นคำเตือน/บุคคลธรรมดาไม่เห็นตัวเลือก)
/// </summary>
public class WhtCertUploadRound253Tests
{
    private static readonly DateTime Now = new(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);

    private static byte[] Pdf(string body = "1 0 obj << /Type /Catalog >> endobj") => Encoding.ASCII.GetBytes("%PDF-1.7\n" + body + "\n%%EOF");
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0 };
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D };

    // ═══════════ ลิงก์อัปโหลด ═══════════

    [Fact]
    public void ไฟล์_PDF_JPG_PNG_ผ่าน()
    {
        Assert.Equal("application/pdf", WhtCertUploadLinkPolicy.Inspect(Pdf()).Kind?.ContentType);
        Assert.Equal("image/jpeg", WhtCertUploadLinkPolicy.Inspect(Jpeg).Kind?.ContentType);
        Assert.Equal("image/png", WhtCertUploadLinkPolicy.Inspect(Png).Kind?.ContentType);
        Assert.Null(WhtCertUploadLinkPolicy.Inspect(Pdf()).Problem);
    }

    [Fact]
    public void ไฟล์_exe_เปลี่ยนนามสกุล_HTML_ว่าง_เกินขนาด_ถูกปฏิเสธ()
    {
        var exe = Encoding.ASCII.GetBytes("MZ\x90\0 this program cannot be run in DOS mode");
        Assert.Null(WhtCertUploadLinkPolicy.Inspect(exe).Kind);
        Assert.Equal(WhtCertUploadLinkPolicy.RejectTypeMessage, WhtCertUploadLinkPolicy.Inspect(exe).Problem);
        Assert.Null(WhtCertUploadLinkPolicy.Inspect(Encoding.ASCII.GetBytes("<html><script>alert(1)</script>")).Kind);
        Assert.NotNull(WhtCertUploadLinkPolicy.Inspect(Array.Empty<byte>()).Problem);
        // GIF เป็นรูปที่ระบบรู้จักแต่ไม่ใช่ชนิดที่หน้าอัปโหลดรับ (PDF/JPG/PNG เท่านั้น)
        Assert.Null(WhtCertUploadLinkPolicy.Inspect(Encoding.ASCII.GetBytes("GIF89a....")).Kind);
        var big = new byte[WhtCertUploadLinkPolicy.MaxBytes + 1];
        Pdf().CopyTo(big, 0);
        Assert.Contains("10 MB", WhtCertUploadLinkPolicy.Inspect(big).Problem);
    }

    [Fact]
    public void PDF_ที่มีสคริปต์หรือไฟล์ฝัง_ถูกปฏิเสธ_แต่ชื่อที่ขึ้นต้นด้วย_JS_ไม่ฟ้องผิด()
    {
        Assert.Contains("/JavaScript", WhtCertUploadLinkPolicy.Inspect(Pdf("<< /S /JavaScript /JS (app.alert(1)) >>")).Problem);
        Assert.Contains("/JS", WhtCertUploadLinkPolicy.Inspect(Pdf("<< /JS (x) >>")).Problem);
        Assert.Contains("/EmbeddedFile", WhtCertUploadLinkPolicy.Inspect(Pdf("<< /Type /EmbeddedFile >>")).Problem);
        Assert.Contains("/Launch", WhtCertUploadLinkPolicy.Inspect(Pdf("<< /S /Launch >>")).Problem);
        // ทิศตรงข้าม: ชื่ออื่นที่ขึ้นต้นด้วย JS (เช่น /JSONData) ไม่ใช่สคริปต์ — ไม่ฟ้อง
        Assert.Null(WhtCertUploadLinkPolicy.Inspect(Pdf("<< /JSONData 1 >>")).Problem);
    }

    [Fact]
    public void token_ฐานเก็บ_hash_เทียบเวลาคงที่_และลิงก์หมดอายุหรือถูกเพิกถอนใช้ไม่ได้()
    {
        var t = WhtCertUploadLinkPolicy.NewToken();
        var h = WhtCertUploadLinkPolicy.TokenHash(t);
        Assert.True(WhtCertUploadLinkPolicy.LooksValid(t));
        Assert.True(WhtCertUploadLinkPolicy.TokenMatches(t, h));
        Assert.False(WhtCertUploadLinkPolicy.TokenMatches(WhtCertUploadLinkPolicy.NewToken(), h));
        Assert.False(WhtCertUploadLinkPolicy.TokenMatches(h, h));   // ถือ hash ที่รั่ว ≠ ถือลิงก์
        Assert.Equal(DocumentPayLinkPolicy.TokenHash(t), h);        // ฟังก์ชัน hash เดียวกับลิงก์ชำระ
        var exp = WhtCertUploadLinkPolicy.ExpiresAt(Now);
        Assert.Equal(180d, (exp - Now).TotalDays);
        Assert.True(WhtCertUploadLinkPolicy.Usable(exp, null, Now));
        Assert.False(WhtCertUploadLinkPolicy.Usable(exp, Now, Now));
        Assert.False(WhtCertUploadLinkPolicy.Usable(Now.AddSeconds(-1), null, Now));
        var cid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("https://app.x/wht-upload.html?c=11111111-2222-3333-4444-555555555555&t=ab", WhtCertUploadLinkPolicy.Url("https://app.x/", cid, "ab"));
    }

    [Fact]
    public void เอกสารที่ลูกค้าติ๊ก_เหลือเฉพาะของลิงก์นี้_และชื่อผู้ส่งถูกทำความสะอาด()
    {
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.Equal(new[] { mine }, WhtCertUploadLinkPolicy.FilterHints(new[] { mine, other, mine }, new[] { mine }));
        Assert.Null(WhtCertUploadLinkPolicy.FilterHints(new[] { other }, new[] { mine }));
        Assert.Null(WhtCertUploadLinkPolicy.FilterHints(null, new[] { mine }));
        Assert.Null(WhtCertUploadLinkPolicy.SanitizeSenderName("  \u0001 "));
        Assert.Equal("สมชาย", WhtCertUploadLinkPolicy.SanitizeSenderName(" สม\u0000ชาย "));
        Assert.Equal(WhtCertUploadLinkPolicy.MaxSenderNameLength, WhtCertUploadLinkPolicy.SanitizeSenderName(new string('ก', 300))!.Length);
        Assert.Equal("ลูกค้า (ลิงก์อัปโหลด)", WhtCertUploadLinkPolicy.SubmittedBy(null));
        Assert.Equal(64, WhtCertUploadLinkPolicy.Sha256Hex(Pdf()).Length);
    }

    // ═══════════ จังหวะทวง ═══════════

    [Fact]
    public void ขั้นทวง_7_21_45_แล้วรายเดือน_6_ครั้ง()
    {
        Assert.Equal(new[] { 7, 21, 45, 75, 105, 135, 165, 195, 225 }, WhtCertReminderCadence.Steps);
        Assert.Null(WhtCertReminderCadence.DueStep(6));
        Assert.Equal(7, WhtCertReminderCadence.DueStep(7));
        Assert.Equal(7, WhtCertReminderCadence.DueStep(20));
        Assert.Equal(21, WhtCertReminderCadence.DueStep(21));
        Assert.Equal(45, WhtCertReminderCadence.DueStep(74));
        Assert.Equal(225, WhtCertReminderCadence.DueStep(900));
    }

    [Fact]
    public void ถึงขั้นแรก_ส่ง_และขั้นเดียวกันไม่ส่งซ้ำ()
    {
        var anchor = Now.AddDays(-8);
        var d = WhtCertReminderCadence.Decide(true, anchor, Array.Empty<WhtCertReminderCadence.Sent>(), Now);
        Assert.True(d.Send);
        Assert.Equal(7, d.Step);
        var sent = new[] { new WhtCertReminderCadence.Sent(7, anchor, Now.AddHours(-2)) };
        Assert.False(WhtCertReminderCadence.Decide(true, anchor, sent, Now).Send);
        // ทิศตรงข้าม: ยังไม่ถึงวันที่ 7 · ปิดค่าตั้ง · ไม่มีรายการค้าง ⇒ ไม่ส่ง
        Assert.False(WhtCertReminderCadence.Decide(true, Now.AddDays(-6), Array.Empty<WhtCertReminderCadence.Sent>(), Now).Send);
        Assert.False(WhtCertReminderCadence.Decide(false, anchor, Array.Empty<WhtCertReminderCadence.Sent>(), Now).Send);
        Assert.False(WhtCertReminderCadence.Decide(true, null, Array.Empty<WhtCertReminderCadence.Sent>(), Now).Send);
    }

    [Fact]
    public void ขั้นถัดไปส่งเมื่อถึง_เปิดใช้ทีหลังส่งเฉพาะขั้นล่าสุดครั้งเดียว_เว้นอย่างน้อย_7_วัน()
    {
        var anchor = Now.AddDays(-22);
        var after7 = new[] { new WhtCertReminderCadence.Sent(7, anchor, Now.AddDays(-15)) };
        var d21 = WhtCertReminderCadence.Decide(true, anchor, after7, Now);
        Assert.True(d21.Send);
        Assert.Equal(21, d21.Step);
        // เปิดค่าตั้งตอนรายการค้าง 50 วัน ⇒ ส่งขั้น 45 ครั้งเดียว (ไม่ไล่ส่ง 7/21 ย้อนหลัง)
        var old = Now.AddDays(-50);
        var first = WhtCertReminderCadence.Decide(true, old, Array.Empty<WhtCertReminderCadence.Sent>(), Now);
        Assert.Equal(45, first.Step);
        Assert.False(WhtCertReminderCadence.Decide(true, old, new[] { new WhtCertReminderCadence.Sent(45, old, Now) }, Now.AddDays(1)).Send);
        // รายการเก่าสุดถูกปิด รายการถัดไปขึ้นมาเป็นฐาน (คนละวันถูกหัก) ⇒ ยังต้องเว้น 7 วันจากฉบับล่าสุด
        var next = Now.AddDays(-30);
        Assert.False(WhtCertReminderCadence.Decide(true, next, new[] { new WhtCertReminderCadence.Sent(45, old, Now.AddDays(-2)) }, Now).Send);
        Assert.True(WhtCertReminderCadence.Decide(true, next, new[] { new WhtCertReminderCadence.Sent(45, old, Now.AddDays(-8)) }, Now).Send);
    }

    [Fact]
    public void เพดาน_6_ฉบับต่อ_30_วัน_และกุญแจกันซ้ำคงที่()
    {
        var anchor = Now.AddDays(-80);
        var many = Enumerable.Range(0, 6).Select(i => new WhtCertReminderCadence.Sent(7, Now.AddDays(-100 - i), Now.AddDays(-8 - i * 3))).ToList();
        Assert.False(WhtCertReminderCadence.Decide(true, anchor, many, Now).Send);
        Assert.True(WhtCertReminderCadence.Decide(true, anchor, many.Take(5).ToList(), Now).Send);
        var c = Guid.NewGuid();
        Assert.Equal(WhtCertReminderCadence.QueueKey(c, 7, anchor), WhtCertReminderCadence.QueueKey(c, 7, anchor));
        Assert.NotEqual(WhtCertReminderCadence.QueueKey(c, 7, anchor), WhtCertReminderCadence.QueueKey(c, 21, anchor));
    }

    [Fact]
    public void เนื้ออีเมลทวง_หนีค่า_มีที่หมายลิงก์_และขั้น_45_บอกว่าต้องใช้ยื่น_ภงด50()
    {
        var body = WhtCertReminderCadence.BodyHtml("<b>บริษัท เอ</b>", "0105556000007", "สำนักงานใหญ่", "ลูกค้า <script>",
            45, new[] { new WhtCertReminderCadence.Item("INV-<1>", new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc), 300m) });
        Assert.Contains(WhtCertReminderCadence.UploadLinkPlaceholder, body);
        Assert.DoesNotContain("<script>", body);
        Assert.Contains("&lt;b&gt;บริษัท เอ&lt;/b&gt;", body);
        Assert.Contains("INV-&lt;1&gt;", body);
        Assert.Contains("01/09/2569", body);
        Assert.Contains("300.00", body);
        Assert.Contains("ภ.ง.ด.50", body);
        Assert.Contains("ภ.ง.ด.50", WhtCertReminderCadence.Subject("เอ", 45));
        Assert.DoesNotContain("ภ.ง.ด.50", WhtCertReminderCadence.Subject("เอ", 7));
        Assert.DoesNotContain("ภ.ง.ด.50", WhtCertReminderCadence.BodyHtml("เอ", null, "สำนักงานใหญ่", "ลูกค้า", 7, Array.Empty<WhtCertReminderCadence.Item>()));
    }

    // ═══════════ ไม่หัก ณ ที่จ่าย ═══════════

    private static readonly DocumentPayLinkPolicy.Amounts WithWht = DocumentPayLinkPolicy.AmountsFor(1_040m, 30m, 0m);

    [Fact]
    public void นิติบุคคล_และไม่รู้ชนิด_เห็นตัวเลือกพร้อมคำเตือน_ยอดเต็มจากตัวคิดยอดเดิม()
    {
        var o = NoWhtChoicePolicy.OfferFor(WithWht, buyerWithholds: true);
        Assert.True(o.Offered);
        Assert.True(o.Warn);
        Assert.Equal(1_070m, o.GrossAmount);
        Assert.Equal(1_070m, NoWhtChoicePolicy.AmountDue(WithWht, o, noWithholding: true));
        Assert.Equal(1_040m, NoWhtChoicePolicy.AmountDue(WithWht, o, noWithholding: false));
        Assert.True(NoWhtChoicePolicy.OfferFor(WithWht, null).Warn);
    }

    [Fact]
    public void บุคคลธรรมดา_หรือไม่มีภาษีหัก_ไม่เห็นตัวเลือก_และส่งตัวเลือกมาก็ได้ยอดเดิม()
    {
        var ind = NoWhtChoicePolicy.OfferFor(WithWht, buyerWithholds: false);
        Assert.False(ind.Offered);
        Assert.Equal(1_040m, NoWhtChoicePolicy.AmountDue(WithWht, ind, noWithholding: true));
        var none = NoWhtChoicePolicy.OfferFor(DocumentPayLinkPolicy.AmountsFor(1_070m, 0m, 0m), true);
        Assert.False(none.Offered);
        Assert.NotNull(NoWhtChoicePolicy.AcceptRefusal(ind, accepted: true, NoWhtChoicePolicy.NoticeVersion));
    }

    [Fact]
    public void ยอมรับได้เฉพาะเมื่อติ๊กยืนยัน_และ_version_ตรงกับข้อความที่แสดง()
    {
        var o = NoWhtChoicePolicy.OfferFor(WithWht, true);
        Assert.Null(NoWhtChoicePolicy.AcceptRefusal(o, true, NoWhtChoicePolicy.NoticeVersion));
        Assert.NotNull(NoWhtChoicePolicy.AcceptRefusal(o, false, NoWhtChoicePolicy.NoticeVersion));
        Assert.NotNull(NoWhtChoicePolicy.AcceptRefusal(o, true, "NOWHT-OLD"));
        Assert.NotNull(NoWhtChoicePolicy.AcceptRefusal(o, true, null));
    }

    [Fact]
    public void hash_หลักฐาน_คำนวณซ้ำได้_และเปลี่ยนเมื่อยอดหรือข้อความเปลี่ยน_IP_ไม่เก็บดิบ()
    {
        var c = Guid.NewGuid(); var d = Guid.NewGuid();
        var at = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
        var a = NoWhtChoicePolicy.EvidenceHash(c, d, at, 1_070m, 30m, 1_040m, NoWhtChoicePolicy.NoticeVersion, NoWhtChoicePolicy.NoticeHash(), "ip", null);
        Assert.Equal(a, NoWhtChoicePolicy.EvidenceHash(c, d, at, 1_070m, 30m, 1_040m, NoWhtChoicePolicy.NoticeVersion, NoWhtChoicePolicy.NoticeHash(), "ip", null));
        Assert.NotEqual(a, NoWhtChoicePolicy.EvidenceHash(c, d, at, 1_071m, 30m, 1_041m, NoWhtChoicePolicy.NoticeVersion, NoWhtChoicePolicy.NoticeHash(), "ip", null));
        var ip = NoWhtChoicePolicy.IpHash(c, "203.0.113.9");
        Assert.DoesNotContain("203.0.113.9", ip);
        Assert.NotEqual(ip, NoWhtChoicePolicy.IpHash(Guid.NewGuid(), "203.0.113.9"));   // ผูกบริษัท
        Assert.Null(NoWhtChoicePolicy.IpHash(c, null));
        Assert.Contains("ท.ป.4/2528", NoWhtChoicePolicy.NoticeText);
    }

    [Fact]
    public void เงินเข้ายอดเต็มเกินยอดค้าง_ตัวบันทึกรับชำระไม่ลงเอง_ยอดไม่เกินลงได้()
    {
        Assert.Contains(NoWhtChoicePolicy.RuleCode, NoWhtChoicePolicy.HandlerRefusal(1_070m, 1_040m, "INV-1"));
        Assert.Null(NoWhtChoicePolicy.HandlerRefusal(1_040m, 1_040m, "INV-1"));
        Assert.Null(NoWhtChoicePolicy.HandlerRefusal(1_070.01m, 1_070m, "INV-1"));
    }

    // ═══════════ e-WHT ขั้นแรก (N4) ═══════════

    [Fact]
    public void eWHT_แนบเป็นใบ50ทวิธรรมดา_เลขอ้างอิงเป็นข้อความ_ไม่มีอัตราอัตโนมัติ()
    {
        Assert.Equal(WhtEvidenceKind.EWithholding, WhtEvidenceReference.KindFor(true));
        Assert.Equal(WhtEvidenceKind.PaperCertificate, WhtEvidenceReference.KindFor(false));
        Assert.Equal("EW-123", WhtEvidenceReference.Normalize("  EW-123 \n"));
        Assert.Null(WhtEvidenceReference.Normalize("   "));
        Assert.Equal(WhtEvidenceReference.MaxLength, WhtEvidenceReference.Normalize(new string('9', 500))!.Length);
        Assert.Equal("หลักฐาน e-WHT · อ้างอิง EW-123", WhtEvidenceReference.Label(WhtEvidenceKind.EWithholding, "EW-123"));
        Assert.Equal("หลักฐาน e-WHT", WhtEvidenceReference.Label(WhtEvidenceKind.EWithholding, null));
        Assert.Null(WhtEvidenceReference.Label(WhtEvidenceKind.PaperCertificate, "EW-123"));   // ใบกระดาษไม่มีป้ายเพิ่ม
    }
}

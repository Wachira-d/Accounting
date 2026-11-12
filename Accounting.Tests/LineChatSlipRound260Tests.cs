using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 260 (คำตัดสิน 260-1-1) — สลิปจากแชต LINE OA ของบริษัท: ตัวอ่านข้อความสลิป <see cref="OcrSlipText"/> + ตัวตัดสินการจับคู่ <see cref="LineChatSlipRule"/>
/// ทั้งสองทิศ (ใบเดียว/หลายใบ/ไม่ตรง · ยอดหลังหัก ณ ที่จ่าย · ปัดเศษ AwayFromZero · ใบของผู้ติดต่ออื่นไม่เป็นผู้สมัคร · ร่าง/ยกเลิก/ชำระครบถูกตัด ·
/// ลายเซ็นปุ่มเลือกใบ · แถวความพร้อม)
/// </summary>
public class LineChatSlipRound260Tests
{
    private static readonly Guid Me = Guid.NewGuid(), Other = Guid.NewGuid();

    /// <param name="wht">WHT ที่คาดว่าลูกค้าจะหัก — ยอดค้างในระบบเป็นยอดหลังหักแล้ว ⇒ ยอดก่อนหัก = ยอดค้าง + wht</param>
    private static LineChatSlipRule.Candidate Doc(string no, decimal balance, decimal wht = 0m, Guid? contact = null,
        DocumentStatus status = DocumentStatus.Approved, int day = 1)
        => new(Guid.NewGuid(), contact ?? Me, no, new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc), status, balance, balance, balance + wht);

    private static OcrSlipText.Reading Sure(decimal amount) => new(new[] { amount }, amount, null, null, LooksLikeSlip: true);
    private static OcrSlipText.Reading Unsure(params decimal[] amounts) => new(amounts, null, null, null);

    // ─────────── OcrSlipText ───────────

    [Fact]
    public void อ่านสลิป_ยอดบรรทัดจำนวนเงิน_มั่นใจ_ค่าธรรมเนียมศูนย์ไม่นับ_เลขอ้างอิง_วันที่ไทย()
    {
        var r = OcrSlipText.Parse("โอนเงินสำเร็จ\n12 พ.ย. 69 14:05 น.\nจำนวนเงิน\n1,040.00 บาท\nค่าธรรมเนียม 0.00 บาท\nรหัสอ้างอิง 016317140512ABC1234\nยอดคงเหลือ 25,300.50");
        Assert.Equal(1040.00m, r.Amount);
        Assert.Contains(25300.50m, r.Amounts);
        Assert.DoesNotContain(0m, r.Amounts);
        Assert.Equal("016317140512ABC1234", r.Ref);
        Assert.Equal(new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc), r.Date);
    }

    [Fact]
    public void อ่านสลิป_ยอดสี่หลักไม่มีจุลภาค_ไม่ถูกตัดเป็นสามหลัก()
    {
        // เดิม regex จับ "500.00" จาก "1500.00" (ตัวแยกเก่าใน SlipOcrAssistService) — ล็อกไว้
        var r = OcrSlipText.Parse("Amount 1500.00 THB");
        Assert.Equal(1500.00m, r.Amount);
        Assert.Single(r.Amounts);
    }

    [Fact]
    public void ทิศตรงข้าม_อ่านสลิป_หลายยอดไม่มีคำว่าจำนวน_ไม่เดายอดสูงสุด()
    {
        var r = OcrSlipText.Parse("รายการ 300.00\nรายการ 700.00");
        Assert.Null(r.Amount);
        Assert.Equal(new[] { 300.00m, 700.00m }, r.Amounts);
        Assert.Same(OcrSlipText.Reading.Empty, OcrSlipText.Parse("   "));
        Assert.Null(OcrSlipText.Parse("สวัสดีค่ะ ไม่มีตัวเลข").Amount);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), OcrSlipText.ExtractDate("01/02/2569"));
        Assert.Null(OcrSlipText.ExtractDate("31/02/2026"));
    }

    // ─────────── ฝ่ายค้าน 260-1 ───────────

    [Fact]
    public void ฝ่ายค้าน_M3_ภาพหน้าชำระที่มียอดเดียว_ไม่ดูเป็นสลิป_ไม่ผูกเอง_ให้ลูกค้ายืนยัน()
    {
        var payPage = OcrSlipText.Parse("ชำระเงิน ใบแจ้งหนี้ INV-001\nยอดที่ต้องชำระ 1,070.00 บาท\nสแกน QR พร้อมเพย์", 2026);
        Assert.Equal(1070m, payPage.Amount);
        Assert.False(payPage.LooksLikeSlip);
        var d = LineChatSlipRule.Decide(payPage, new[] { Doc("INV-001", 1070m) }, new[] { Me });
        Assert.Equal(LineChatSlipRule.Outcome.Many, d.Outcome);   // ปุ่มยืนยัน 1 ใบ (คำตัดสิน 260-1-7)
        Assert.Single(d.Matches);
        // ทิศตรงข้าม: สลิปจริง (โอนเงินสำเร็จ + รหัสอ้างอิง) ยังผูกเอง
        var slip = OcrSlipText.Parse("โอนเงินสำเร็จ\nจำนวนเงิน 1,070.00\nรหัสอ้างอิง 2026111214050123", 2026);
        Assert.True(slip.LooksLikeSlip);
        Assert.Equal(LineChatSlipRule.Outcome.One, LineChatSlipRule.Decide(slip, new[] { Doc("INV-001", 1070m) }, new[] { Me }).Outcome);
    }

    [Theory]
    [InlineData("12/11/69", 2026, 11, 12)]   // พ.ศ. สองหลัก (69 > 26 + 10)
    [InlineData("12/11/26", 2026, 11, 12)]   // ค.ศ. สองหลัก
    [InlineData("12 พ.ย. 69", 2026, 11, 12)]
    [InlineData("12 พ.ย. 26", 2026, 11, 12)]
    [InlineData("01/02/2569", 2026, 2, 1)]
    [InlineData("01/02/2026", 2026, 2, 1)]
    public void ฝ่ายค้าน_L8_ปีสองหลัก_สองรูปแบบตีความตรงกัน(string text, int y, int m, int d)
        => Assert.Equal(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc), OcrSlipText.ExtractDate(text, 2026));

    [Fact]
    public void ฝ่ายค้าน_L7_badgeสลิปค่าบริการ_ยอดที่แจ้งอยู่บนสลิปตรง_ไม่อยู่ได้ยอดสูงสุด()
    {
        // ข้อความสลิปค่าบริการแบบที่ระบบเคยอ่าน (KBank/SCB) — ใบที่ถูกอยู่แล้วต้องไม่ถูกแตะ
        var kbank = OcrSlipText.Parse("โอนเงินสำเร็จ\n12 พ.ย. 69 10:21 น.\nนาย ก ข\nxxx-x-x1234-x\nบจก. เน็กแอค\nจำนวน: 1,605.00 บาท\nค่าธรรมเนียม: 0.00 บาท", 2026);
        Assert.Equal(((decimal?)1605.00m, (bool?)true), OcrSlipText.BadgeAmount(kbank.Amounts, 1605m));
        var scb = OcrSlipText.Parse("Transfer successful\nAmount 1500.00\nFee 0.00\nRef No. 202611120001ABC", 2026);
        Assert.Equal(((decimal?)1500.00m, (bool?)true), OcrSlipText.BadgeAmount(scb.Amounts, 1500m));   // เดิม regex ตัด 1500.00 เป็น 500.00 ⇒ badge เป็น "ไม่ตรง"
        // ทิศตรงข้าม: ยอดบนสลิปไม่ใช่ยอดที่แจ้ง ⇒ ไม่ตรง + ยอดสูงสุด · ไม่มียอดเลย ⇒ ไม่มี badge
        Assert.Equal(((decimal?)1605.00m, (bool?)false), OcrSlipText.BadgeAmount(kbank.Amounts, 999m));
        Assert.Equal(((decimal?)null, (bool?)null), OcrSlipText.BadgeAmount(Array.Empty<decimal>(), 1605m));
        Assert.Equal(new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc), kbank.Date);
    }

    // ─────────── LineChatSlipRule.Decide ───────────

    [Fact]
    public void ยอดมั่นใจตรงใบเดียว_ผลOne_ยอดค้าง()
    {
        var a = Doc("INV-001", 1070m);
        var d = LineChatSlipRule.Decide(Sure(1070m), new[] { a, Doc("INV-002", 500m) }, new[] { Me });
        Assert.Equal(LineChatSlipRule.Outcome.One, d.Outcome);
        var m = Assert.Single(d.Matches);
        Assert.Equal(a.DocumentId, m.Doc.DocumentId);
        Assert.Equal(1070m, m.Amount);
    }

    [Fact]
    public void ยอดหลังหักณที่จ่าย_ตรงใบเดียว_แจ้งด้วยยอดที่ต้องโอน()
    {
        // ใบ 1,070 (ก่อนหัก) หัก 3% ของ 1,000 = 30 ⇒ ยอดค้าง/ยอดที่ต้องโอน 1,040
        var a = Doc("INV-001", 1040m, wht: 30m);
        var d = LineChatSlipRule.Decide(Sure(1040m), new[] { a }, new[] { Me });
        Assert.Equal(LineChatSlipRule.Outcome.One, d.Outcome);
        Assert.Equal(1040m, d.Matches[0].Amount);
    }

    [Fact]
    public void ทิศตรงข้าม_ลูกค้าโอนยอดเต็มก่อนหัก_ไม่ผูกเอง_เข้าคิวพร้อมเหตุ()
    {
        var d = LineChatSlipRule.Decide(Sure(1070m), new[] { Doc("INV-001", 1040m, wht: 30m) }, new[] { Me });
        Assert.Equal(LineChatSlipRule.Outcome.None, d.Outcome);
        Assert.Equal(LineChatSlipRule.Reasons.GrossBeforeWht, d.Reason);
        // ไม่มี WHT ⇒ ยอดก่อนหัก = ยอดค้าง ⇒ ไม่ใช่เหตุนี้
        Assert.Equal(LineChatSlipRule.Reasons.NoMatch, LineChatSlipRule.Decide(Sure(1070m), new[] { Doc("INV-002", 1040m) }, new[] { Me }).Reason);
    }

    [Fact]
    public void ปัดเศษ_AwayFromZero_ตรงถึงสตางค์_ต่างหนึ่งสตางค์ไม่ตรง()
    {
        var a = Doc("INV-001", 100.005m);   // ปัด AwayFromZero = 100.01 (banker's = 100.00)
        Assert.Equal(LineChatSlipRule.Outcome.One, LineChatSlipRule.Decide(Sure(100.01m), new[] { a }, new[] { Me }).Outcome);
        var d = LineChatSlipRule.Decide(Sure(100.00m), new[] { a }, new[] { Me });
        Assert.Equal(LineChatSlipRule.Outcome.None, d.Outcome);
        Assert.Equal(LineChatSlipRule.Reasons.NoMatch, d.Reason);
    }

    [Fact]
    public void หลายใบยอดเท่ากัน_ผลMany_เรียงวันที่_ไม่เกินสิบใบ()
    {
        var docs = Enumerable.Range(1, 12).Select(i => Doc($"INV-{i:000}", 500m, day: 13 - i)).ToList();
        var d = LineChatSlipRule.Decide(Sure(500m), docs, new[] { Me });
        Assert.Equal(LineChatSlipRule.Outcome.Many, d.Outcome);
        Assert.Equal(LineChatSlipRule.MaxChoices, d.Matches.Count);
        Assert.Equal("INV-012", d.Matches[0].Doc.Number);   // วันที่เก่าสุดก่อน
    }

    [Fact]
    public void ยอดไม่มั่นใจแต่ตรงใบเดียว_ให้ลูกค้ายืนยัน_ไม่ผูกเอง()
    {
        var d = LineChatSlipRule.Decide(Unsure(300m, 1070m), new[] { Doc("INV-001", 1070m) }, new[] { Me });
        Assert.Equal(LineChatSlipRule.Outcome.Many, d.Outcome);
        Assert.Single(d.Matches);
    }

    [Fact]
    public void ใบของผู้ติดต่ออื่นไม่เคยเป็นผู้สมัคร_แม้ยอดตรง()
    {
        var mine = Doc("INV-001", 800m);
        var theirs = Doc("INV-900", 800m, contact: Other);
        var d = LineChatSlipRule.Decide(Sure(800m), new[] { theirs, mine }, new[] { Me });
        Assert.Equal(LineChatSlipRule.Outcome.One, d.Outcome);
        Assert.Equal(mine.DocumentId, d.Matches[0].Doc.DocumentId);
        // ทิศตรงข้าม: เหลือแต่ใบของคนอื่น ⇒ ไม่ตรง (ไม่ใช่ผูกกับใบคนอื่น)
        Assert.Equal(LineChatSlipRule.Outcome.None, LineChatSlipRule.Decide(Sure(800m), new[] { theirs }, new[] { Me }).Outcome);
    }

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Voided)]
    [InlineData(DocumentStatus.Rejected)]
    [InlineData(DocumentStatus.Paid)]
    public void ร่าง_รออนุมัติ_ยกเลิก_ปฏิเสธ_ชำระครบ_ถูกตัดออก(DocumentStatus status)
    {
        var d = LineChatSlipRule.Decide(Sure(800m), new[] { Doc("INV-001", 800m, status: status) }, new[] { Me });
        Assert.Equal(LineChatSlipRule.Outcome.None, d.Outcome);
    }

    [Fact]
    public void ยอดค้างศูนย์ถูกตัด_ใบอนุมัติแล้วยังรับ()
    {
        Assert.Equal(LineChatSlipRule.Outcome.None, LineChatSlipRule.Decide(Sure(0.01m), new[] { Doc("INV-001", 0m) }, new[] { Me }).Outcome);
        Assert.Equal(LineChatSlipRule.Outcome.One,
            LineChatSlipRule.Decide(Sure(800m), new[] { Doc("INV-001", 800m, status: DocumentStatus.PartiallyPaid) }, new[] { Me }).Outcome);
    }

    [Fact]
    public void ไม่ได้ผูก_อ่านไม่ได้_เหตุถูกต้อง()
    {
        Assert.Equal(LineChatSlipRule.Reasons.Unbound, LineChatSlipRule.Decide(Sure(800m), new[] { Doc("INV-001", 800m) }, Array.Empty<Guid>()).Reason);
        Assert.Equal(LineChatSlipRule.Reasons.Unreadable, LineChatSlipRule.Decide(OcrSlipText.Reading.Empty, new[] { Doc("INV-001", 800m) }, new[] { Me }).Reason);
        Assert.Equal(LineChatSlipRule.Reasons.Unreadable, LineChatSlipRule.Decide(null, new[] { Doc("INV-001", 800m) }, new[] { Me }).Reason);
    }

    [Fact]
    public void ไม่จับคู่ผลรวมหลายใบ()
    {
        var d = LineChatSlipRule.Decide(Sure(1300m), new[] { Doc("INV-001", 800m), Doc("INV-002", 500m) }, new[] { Me });
        Assert.Equal(LineChatSlipRule.Outcome.None, d.Outcome);
    }

    // ─────────── ลายเซ็นปุ่มเลือกใบ ───────────

    [Fact]
    public void ปุ่มเลือกใบ_ลายเซ็นตรง_และทุกส่วนที่ผูกไว้เปลี่ยนแล้วไม่ผ่าน()
    {
        var key = LineChatSlipRule.KeyFrom("channel-secret-260");
        Guid company = Guid.NewGuid(), slip = Guid.NewGuid(), doc = Guid.NewGuid();
        const string src = "abc123";
        var data = LineChatSlipRule.PostbackData(key, company, src, slip, doc);
        Assert.True(data.Length <= 300);
        Assert.True(LineChatSlipRule.IsOurs(data));
        var pb = LineChatSlipRule.TryParse(data)!;
        Assert.Equal(slip, pb.SlipId);
        Assert.Equal(doc, pb.DocumentId);
        Assert.True(LineChatSlipRule.Verify(key, company, src, slip, doc, pb.Signature));

        Assert.False(LineChatSlipRule.Verify(key, Guid.NewGuid(), src, slip, doc, pb.Signature));          // บริษัทอื่น
        Assert.False(LineChatSlipRule.Verify(key, company, "other-chat", slip, doc, pb.Signature));       // แหล่งแชตอื่น
        Assert.False(LineChatSlipRule.Verify(key, company, src, Guid.NewGuid(), doc, pb.Signature));      // สลิปอื่น
        Assert.False(LineChatSlipRule.Verify(key, company, src, slip, Guid.NewGuid(), pb.Signature));     // ใบอื่น (แก้ data)
        Assert.False(LineChatSlipRule.Verify(LineChatSlipRule.KeyFrom("other-secret"), company, src, slip, doc, pb.Signature));
        Assert.False(LineChatSlipRule.Verify(key, company, src, slip, doc, ""));

        var decline = LineChatSlipRule.TryParse(LineChatSlipRule.PostbackData(key, company, src, slip, null))!;
        Assert.Null(decline.DocumentId);
        Assert.True(LineChatSlipRule.Verify(key, company, src, slip, null, decline.Signature));
        Assert.False(LineChatSlipRule.Verify(key, company, src, slip, doc, decline.Signature));
    }

    [Fact]
    public void ทิศตรงข้าม_postback_อื่นไม่ใช่ของเรา_รูปผิดคืนnull()
    {
        Assert.False(LineChatSlipRule.IsOurs("approve:123"));
        Assert.Null(LineChatSlipRule.TryParse("approve:123"));
        Assert.Null(LineChatSlipRule.TryParse("ls1|not-a-guid|none|sig"));
        Assert.Null(LineChatSlipRule.TryParse("ls1|" + Guid.NewGuid().ToString("N") + "|none|"));
    }

    [Fact]
    public void ตัวเลือก_หมดอายุ_ใช้แล้ว_กดไม่ได้()
    {
        var now = DateTime.UtcNow;
        Assert.Null(LineChatSlipRule.ChoiceProblem(LineChatSlipRule.Statuses.AwaitingChoice, now.AddHours(1), now));
        Assert.NotNull(LineChatSlipRule.ChoiceProblem(LineChatSlipRule.Statuses.AwaitingChoice, now.AddSeconds(-1), now));
        Assert.NotNull(LineChatSlipRule.ChoiceProblem(LineChatSlipRule.Statuses.Submitted, now.AddHours(1), now));
        Assert.NotNull(LineChatSlipRule.ChoiceProblem(LineChatSlipRule.Statuses.AwaitingChoice, null, now));
    }

    [Fact]
    public void คำตอบ_แสดงยอดเฉพาะเมื่อปลายทางเปิดแสดงยอด_ป้ายปุ่มไม่เกิน20()
    {
        Assert.Contains("1,040.00", LineChatSlipRule.Replies.Submitted("INV-001", 1040m, showAmounts: true));
        Assert.DoesNotContain("1,040.00", LineChatSlipRule.Replies.Submitted("INV-001", 1040m, showAmounts: false));
        Assert.Contains("INV-001", LineChatSlipRule.Replies.Submitted("INV-001", 1040m, showAmounts: false));
        Assert.DoesNotContain("500.00", LineChatSlipRule.Replies.ChoiceDisplay("INV-001", DateTime.UtcNow, 500m, showAmounts: false));
        Assert.True(LineChatSlipRule.Replies.ChoiceLabel("INV-2026-11-000000000123").Length <= 20);
        Assert.Equal("INV-1", LineChatSlipRule.Replies.ChoiceLabel("INV-1"));
    }

    [Fact]
    public void แถวความพร้อม_ปิด_ไม่พร้อม_อ่านรูปไม่ได้_พร้อม()
    {
        Assert.Equal(LineOaHealth.LevelWarn, LineChatSlipRule.ReadinessRow(false, true, true, true).Level);
        Assert.Equal(LineOaHealth.LevelWarn, LineChatSlipRule.ReadinessRow(true, false, true, true).Level);
        Assert.Equal(LineOaHealth.LevelWarn, LineChatSlipRule.ReadinessRow(true, true, false, true).Level);
        Assert.Equal(LineOaHealth.LevelWarn, LineChatSlipRule.ReadinessRow(true, true, true, false).Level);
        var ok = LineChatSlipRule.ReadinessRow(true, true, true, true);
        Assert.Equal(LineOaHealth.LevelOk, ok.Level);
        Assert.Equal("chat-slip", ok.Code);
    }
}

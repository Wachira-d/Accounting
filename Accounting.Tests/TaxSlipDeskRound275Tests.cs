using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;
using C = Accounting.Tests.SlipCorpusRound264;

namespace Accounting.Tests;

/// <summary>
/// รอบ 275 แพ็ก D (ผลตรวจ T1) — ตัวตัดสิน pure ของโต๊ะสลิปภาษี: ผู้สมัครที่ล้มแน่นอน (M2) · ทางบันทึกเอง (H1 · คำตัดสิน 4.3) · ผูกรายการนำส่ง ·
/// อายุคิว (M4) · วงจรใบขอชำระ (M7) · ไฟล์เดิมจากช่องทางใหม่ (M3) · เส้น 260 ไม่จับสลิปรัฐกับใบแจ้งหนี้ (M6) · "สมทบ" (L8) · เหตุ "นำส่งแล้ว" (L3) · ตรวจทาน (M8) ·
/// ทุกด่านมีทิศตรงข้าม (ของที่ถูกอยู่แล้วไม่ถูกแตะ)
/// </summary>
public class TaxSlipDeskRound275Tests
{
    private static readonly Guid Co = Guid.NewGuid();
    private static TaxSlipMatch.Candidate Cand(decimal tax, decimal s = 0m, decimal p = 0m, Guid? req = null, string type = "WhtPnd53")
        => new(Co, type, 2026, 9, tax, s, p, req);
    private static readonly DateTime Today = new(2026, 11, 20);

    [Fact]
    public void M2_ผู้สมัครที่ล้มแน่นอน_ถูกบล็อก_ตัวที่ถูก_ผ่าน()
    {
        Assert.Contains("ไม่ตรง", TaxSlipDeskRule.CandidateBlocker(10_500m, Cand(10_000m), null, false));                 // จ่ายเกิน (AmountMismatch)
        Assert.NotNull(TaxSlipDeskRule.CandidateBlocker(10_000m, Cand(10_000m, req: Guid.NewGuid()), TaxPaymentRequestLifecycle.Cancelled, false));
        Assert.NotNull(TaxSlipDeskRule.CandidateBlocker(10_000m, Cand(10_000m, req: Guid.NewGuid()), null, false));        // ใบหาไม่พบ
        Assert.Contains("ผูกกับรายการนำส่ง", TaxSlipDeskRule.CandidateBlocker(10_000m, Cand(10_000m), null, true));         // AlreadyRemitted
        // ทิศตรงข้าม
        Assert.Null(TaxSlipDeskRule.CandidateBlocker(10_000m, Cand(10_000m), null, false));
        Assert.Null(TaxSlipDeskRule.CandidateBlocker(10_250m, Cand(10_000m, 150m, 100m, Guid.NewGuid()), TaxPaymentRequestLifecycle.Open, false));
        Assert.Null(TaxSlipDeskRule.CandidateBlocker(413.56m, Cand(413.56m, type: "VatPp36"), null, true));               // ภ.พ.36 นำส่งหลายครั้งต่องวด
        Assert.Null(TaxSlipDeskRule.CandidateBlocker(null, Cand(10_000m), null, false));                                    // ยอดอ่านไม่ได้ ไม่ใช่เหตุที่นี่
    }

    [Fact]
    public void H1_บันทึกเอง_ยอดรวมต้องเท่าสลิปทุกสตางค์()
    {
        Assert.Contains("ไม่ตรงยอดบนสลิป", TaxSlipDeskRule.ManualProblem("WhtPnd53", 10_000m, 150m, 0m, 10_150.01m, null, Today, Today));
        Assert.Null(TaxSlipDeskRule.ManualProblem("WhtPnd53", 10_000m, 150m, 0m, 10_150m, null, Today, Today));
        // ปกส. (ยังไม่มีแถวผู้รับชำระที่ยืนยัน) — พนักงานบันทึกได้
        Assert.Null(TaxSlipDeskRule.ManualProblem("SsoSps110", 8_558m, 0m, 0m, 8_558m, null, Today, Today));
        // OCR ล่ม: ไม่รู้ยอด ⇒ ต้องกรอก · กรอกแล้วต้องตรง
        Assert.NotNull(TaxSlipDeskRule.ManualProblem("WhtPnd53", 10_000m, 0m, 0m, null, null, Today, Today));
        Assert.NotNull(TaxSlipDeskRule.ManualProblem("WhtPnd53", 10_000m, 0m, 0m, null, 9_999m, Today, Today));
        Assert.Null(TaxSlipDeskRule.ManualProblem("WhtPnd53", 10_000m, 0m, 0m, null, 10_000m, Today, Today));
        // ระบบอ่านได้แล้ว ⇒ ยอดที่พนักงานกรอกไม่ทับยอดที่อ่านได้
        Assert.NotNull(TaxSlipDeskRule.ManualProblem("WhtPnd53", 10_000m, 0m, 0m, 9_000m, 10_000m, Today, Today));
        Assert.NotNull(TaxSlipDeskRule.ManualProblem("WhtPnd53", 10_000m, 0m, 0m, 10_000m, null, null, Today));             // ไม่มีวันที่
        Assert.NotNull(TaxSlipDeskRule.ManualProblem("WhtPnd53", 10_000m, 0m, 0m, 10_000m, null, Today.AddDays(1), Today)); // อนาคต
        Assert.NotNull(TaxSlipDeskRule.ManualProblem("WhtPnd53", 10_000m, -1m, 1m, 10_000m, null, Today, Today));
        Assert.NotNull(TaxSlipDeskRule.ManualProblem("Bogus", 10_000m, 0m, 0m, 10_000m, null, Today, Today));
        Assert.NotNull(TaxSlipDeskRule.ManualProblem("WhtPnd53", 10_000m, 0m, 0m, 10_000m, null, Today, Today, slipTaxIdOtherCompany: true));
    }

    [Fact]
    public void H1_ผูกกับรายการนำส่งที่มีอยู่()
    {
        Assert.Null(TaxSlipDeskRule.LinkProblem(1_500m, 1_500m, false, null));
        Assert.NotNull(TaxSlipDeskRule.LinkProblem(1_500m, 1_500.01m, false, null));
        Assert.NotNull(TaxSlipDeskRule.LinkProblem(1_500m, 1_500m, true, null));
        Assert.NotNull(TaxSlipDeskRule.LinkProblem(1_500m, 1_500m, false, Guid.NewGuid()));
        Assert.NotNull(TaxSlipDeskRule.LinkProblem(null, 1_500m, false, null));
    }

    [Fact]
    public void ฝ่ายค้านMED1_หน่วยงานบนสลิปกับแบบ_และเลขผู้เสียภาษี_สองทิศ()
    {
        const string sso = nameof(GovPaymentSlip.Agency.SocialSecurityOffice), rd = nameof(GovPaymentSlip.Agency.RevenueDepartment);
        Assert.NotNull(TaxSlipDeskRule.AgencyTypeProblem(sso, "WhtPnd53"));      // สลิป สปส. ลงเป็น ภ.ง.ด.53 ไม่ได้
        Assert.Null(TaxSlipDeskRule.AgencyTypeProblem(sso, "SsoSps110"));
        Assert.NotNull(TaxSlipDeskRule.AgencyTypeProblem(rd, "SsoSps110"));
        Assert.Null(TaxSlipDeskRule.AgencyTypeProblem(rd, "WhtPnd53"));
        Assert.Null(TaxSlipDeskRule.AgencyTypeProblem(null, "WhtPnd53"));       // ไม่รู้หน่วยงาน ⇒ ไม่บล็อก
        Assert.NotNull(TaxSlipDeskRule.ManualProblem("WhtPnd53", 8_558m, 0m, 0m, 8_558m, null, Today, Today, slipAgency: sso));
        Assert.Null(TaxSlipDeskRule.ManualProblem("SsoSps110", 8_558m, 0m, 0m, 8_558m, null, Today, Today, slipAgency: sso));
        Assert.NotNull(TaxSlipDeskRule.LinkProblem(8_558m, 8_558m, false, null, false, sso, "WhtPnd53"));
        Assert.Null(TaxSlipDeskRule.LinkProblem(8_558m, 8_558m, false, null, false, sso, "SsoSps110"));
        Assert.NotNull(TaxSlipDeskRule.LinkProblem(8_558m, 8_558m, false, null, slipTaxIdOtherCompany: true));
        var masked = TaxSlipReplyCard.MaskTaxId(SlipCorpusRound272.ClientTaxId);
        Assert.False(TaxSlipDeskRule.SlipTaxIdOther(masked, SlipCorpusRound272.ClientTaxId));
        Assert.True(TaxSlipDeskRule.SlipTaxIdOther(masked, "0105536000011"));   // 7 หลักแรกต่าง (เลขที่เก็บเปิดแค่ 7 หลักแรก — PiiMask.TaxId)
        Assert.False(TaxSlipDeskRule.SlipTaxIdOther(null, SlipCorpusRound272.OtherClientTaxId));   // สลิปไม่มีเลข ⇒ ไม่รู้
        // หน่วยงานจากข้อความ: สลิป สปส. จริง (Biller ID + "สมทบ") ⇒ สปส. · สลิปโอนทั่วไป ⇒ ไม่รู้
        Assert.Equal(GovPaymentSlip.Agency.SocialSecurityOffice, GovPaymentSlip.AgencyHint(OcrSlipText.Parse(C.KbizBillSso.Text, 2026, C.KbizBillSso.Qr)));
        Assert.Null(GovPaymentSlip.AgencyHint(OcrSlipText.Parse(C.KbizTransferMemo.Text, 2026, C.KbizTransferMemo.Qr)));
        Assert.Equal(GovPaymentSlip.Agency.RevenueDepartment, GovPaymentSlip.AgencyHint(OcrSlipText.Parse(SlipCorpusRound272.KbizRd(), 2026)));
    }

    [Fact]
    public void ฝ่ายค้านLOW3_ใบขอชำระปิดเฉพาะเมื่อยอดและControlCodeตรง()
    {
        Assert.True(TaxPaymentRequestLifecycle.PaidBy(10_150m, "h1", 10_150m, null));
        Assert.True(TaxPaymentRequestLifecycle.PaidBy(10_150m, "h1", 10_150m, "h1"));
        Assert.False(TaxPaymentRequestLifecycle.PaidBy(10_150m, "h1", 10_000m, null));   // บางส่วน
        Assert.False(TaxPaymentRequestLifecycle.PaidBy(10_150m, "h1", 10_150m, "h2"));   // สลิปของใบอื่น
        Assert.Contains("รอชำระ", TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.PaymentRequestReopened, new(FormLabel: "ภ.ง.ด.53 งวด ก.ย. 2569"), false));
        Assert.Contains("3 ใบ", TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.OpenExpired, new(HeldCount: 3, RecordedAt: Today), false));   // LOW-1 การ์ดสรุป
    }

    [Theory]
    [InlineData("ToStaff", 1, null, TaxSlipOpenAge.AgeAction.None)]
    [InlineData("ToStaff", 3, null, TaxSlipOpenAge.AgeAction.Remind)]
    [InlineData("ToStaff", 3, 2, TaxSlipOpenAge.AgeAction.None)]       // เตือนไปแล้ว 2 ชม.ก่อน
    [InlineData("ToStaff", 3, 30, TaxSlipOpenAge.AgeAction.Remind)]    // เตือนเกิน 24 ชม. ⇒ เตือนซ้ำ
    [InlineData("ToStaff", 91, 1, TaxSlipOpenAge.AgeAction.Expire)]
    [InlineData("AwaitingConsent", 91, null, TaxSlipOpenAge.AgeAction.None)]   // นาฬิกาของตัวเอง (30 วัน)
    [InlineData("Remitted", 400, null, TaxSlipOpenAge.AgeAction.None)]
    public void M4_อายุแถวรอพนักงาน(string status, int ageDays, int? remindedHoursAgo, TaxSlipOpenAge.AgeAction expected)
    {
        var now = new DateTime(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, TaxSlipOpenAge.Decide(status, now.AddDays(-ageDays), remindedHoursAgo is int h ? now.AddHours(-h) : null, now));
    }

    [Fact]
    public void M7_วงจรใบขอชำระ()
    {
        var now = new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc);   // = 21 พ.ย. 01:00 น. เวลาไทย
        Assert.True(TaxPaymentRequestLifecycle.IsExpired(new DateTime(2026, 11, 20), now));
        Assert.False(TaxPaymentRequestLifecycle.IsExpired(new DateTime(2026, 11, 21), now));   // วันหมดอายุเอง ยังใช้ได้
        Assert.False(TaxPaymentRequestLifecycle.IsExpired(null, now));
        Assert.Equal(TaxPaymentRequestLifecycle.Open, TaxPaymentRequestLifecycle.StatusAfterVoid(null, now));
        Assert.Equal(TaxPaymentRequestLifecycle.Expired, TaxPaymentRequestLifecycle.StatusAfterVoid(new DateTime(2026, 11, 1), now));
        Assert.True(TaxSlipDeskRule.MultiPerPeriod("VatPp36"));
        Assert.False(TaxSlipDeskRule.MultiPerPeriod("WhtPnd53"));
        Assert.True(TaxPaymentRequestLifecycle.ClosesOnRemit("WhtPnd3"));
        Assert.False(TaxPaymentRequestLifecycle.ClosesOnRemit("VatPp36"));
        Assert.False(TaxPaymentRequestLifecycle.ClosesOnRemit(WorkersCompensationBase.RemittanceType));
        Assert.Equal("ชำระแล้ว", TaxPaymentRequestLifecycle.Label(TaxPaymentRequestLifecycle.Paid));   // เดิม "ชำระแล้ว (สลิป)" — ตอนนี้ปิดได้จากเส้นมือด้วย
    }

    [Fact]
    public void M3_ไฟล์เดิมจากกลุ่มอื่น_ประมวลใหม่เฉพาะแถวที่รอพนักงาน()
    {
        Assert.True(TaxSlipChannelRedirect.ShouldReprocess("ToStaff", "line", "groupA", "line", "groupB"));
        Assert.False(TaxSlipChannelRedirect.ShouldReprocess("ToStaff", "line", "groupA", "line", "groupA"));   // กลุ่มเดิม = ส่งซ้ำ
        Assert.False(TaxSlipChannelRedirect.ShouldReprocess("Remitted", "line", "groupA", "line", "groupB"));
        Assert.False(TaxSlipChannelRedirect.ShouldReprocess("AwaitingConsent", "line", "groupA", "line", "groupB"));
        Assert.False(TaxSlipChannelRedirect.ShouldReprocess("ToStaff", "email", "x", "line", "groupB"));
    }

    private static readonly Guid Me = Guid.NewGuid();
    private static LineChatSlipRule.Candidate Doc(string no, decimal balance)
        => new(Guid.NewGuid(), Me, no, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), DocumentStatus.Approved, balance, balance, balance);

    [Fact]
    public void M6_เส้น260_สลิปจ่ายหน่วยงานรัฐ_ไม่จับกับใบแจ้งหนี้ยอดเท่ากัน_สลิปโอนทั่วไปยังจับได้()
    {
        var sso = OcrSlipText.Parse(C.KbizBillSso.Text, 2026, C.KbizBillSso.Qr);
        Assert.True(GovPaymentSlip.Identify(sso).IsGovernment);
        var d = LineChatSlipRule.Decide(sso, new[] { Doc("INV-001", 8_558m) }, new[] { Me });
        Assert.Equal((LineChatSlipRule.Outcome.None, LineChatSlipRule.Reasons.GovernmentSlip), (d.Outcome, d.Reason));
        var rd = OcrSlipText.Parse(SlipCorpusRound272.KbizRd(amount: "5,350.00"), 2026);
        Assert.Equal(LineChatSlipRule.Outcome.None, LineChatSlipRule.Decide(rd, new[] { Doc("INV-002", 5_350m) }, new[] { Me }).Outcome);
        // ทิศตรงข้าม: สลิปโอนทั่วไป (ไม่ใช่รัฐ) ยอดเท่าใบ ⇒ แจ้งโอนเข้าใบเหมือนเดิม
        var transfer = OcrSlipText.Parse(C.KbizTransferMemo.Text, 2026, C.KbizTransferMemo.Qr);
        Assert.False(GovPaymentSlip.Identify(transfer).IsGovernment);
        Assert.NotEqual(LineChatSlipRule.Reasons.GovernmentSlip, LineChatSlipRule.Decide(transfer, new[] { Doc("INV-003", 6_305m) }, new[] { Me }).Reason);
    }

    [Fact]
    public void L8_สมทบ_คำเดียวในบันทึกช่วยจำ_ไม่ใช่สลิปรัฐ_คู่ป้ายจ่ายบิล_ยังเป็น()
    {
        var memo = OcrSlipText.Parse(C.KbizTransferMemo.Text.Replace("<MEMO>", "เงินสมทบงานบุญ"), 2026);
        Assert.Equal(GovPaymentSlip.Kind.NotGovernment, GovPaymentSlip.Identify(memo).Kind);
        var sso = OcrSlipText.Parse(C.KbizBillSso.Text, 2026, C.KbizBillSso.Qr);   // "ชําระเงินบสมทบมาตรา 33" + Biller ID
        Assert.Equal(GovPaymentSlip.Kind.UnknownGovernment, GovPaymentSlip.Identify(sso).Kind);
    }

    [Fact]
    public void L3_งวดที่บันทึกนำส่งแล้วด้วยยอดเท่าสลิป_เหตุตามจริง()
    {
        var reading = OcrSlipText.Parse(SlipCorpusRound272.KbizRd(control: "999999999999999", amount: "1,500.00"), 2026);
        var agency = GovPaymentSlip.Identify(reading);
        Assert.Equal(GovPaymentSlip.Kind.Identified, agency.Kind);
        var slip = new TaxSlipMatch.Slip(agency, 1_500m, 0m, new DateTime(2026, 10, 7), TaxSlipRefHash.ControlCode("999999999999999"));
        var cos = new[] { new TaxSlipMatch.CompanyKey(Co, SlipCorpusRound272.ClientTaxId) };
        var remitted = new HashSet<(Guid, string, int, int)> { (Co, "WhtPnd53", 2026, 9) };
        var empty = Array.Empty<TaxSlipMatch.RequestKey>();
        var none = Array.Empty<TaxSlipMatch.PendingKey>();
        var d = TaxSlipMatch.Decide(slip, cos, empty, none, remitted, false, new[] { new TaxSlipMatch.RemittedKey(Co, "WhtPnd53", 2026, 9, 1_500m) });
        Assert.Equal((TaxSlipMatch.Outcome.AlreadyRemitted, TaxSlipMatch.Codes.Remitted), (d.Outcome, d.RuleCode));
        // ทิศตรงข้าม: ยอดไม่เท่า / ไม่ส่งชุดยอด ⇒ เหมือนเดิม (None)
        Assert.Equal(TaxSlipMatch.Outcome.None, TaxSlipMatch.Decide(slip, cos, empty, none, remitted, false, new[] { new TaxSlipMatch.RemittedKey(Co, "WhtPnd53", 2026, 9, 1_400m) }).Outcome);
        Assert.Equal(TaxSlipMatch.Outcome.None, TaxSlipMatch.Decide(slip, cos, empty, none, remitted, false).Outcome);
    }

    [Fact]
    public void M8_ยังไม่มีรายการเดินบัญชีหลังช่วงตรวจ_ยังไม่ได้ตรวจ_ไม่ใช่ไม่พบ()
    {
        var now = new DateTime(2026, 12, 3);
        var paid = new DateTime(2026, 11, 7);
        Assert.Null(TaxSlipReviewService.Verdict(false, false, false, paid, now, statementCovered: false));
        Assert.Equal(TaxSlipRemitView.Review.NotFound, TaxSlipReviewService.Verdict(false, false, false, paid, now, statementCovered: true));
        Assert.Equal(TaxSlipRemitView.Review.StatementFound, TaxSlipReviewService.Verdict(false, true, false, paid, now, statementCovered: false));
    }

    [Fact]
    public void การ์ดใหม่_ตรงความจริง_ไม่ว่าง_ปิดตัวเลขเมื่อปิดยอด()
    {
        var f = new TaxSlipReplyCard.Facts(CompanyName: "บริษัท ทดสอบ จำกัด", FormLabel: "ภ.ง.ด.53 งวด ก.ย. 2569", ControlLast4: "1234", PayDate: new DateTime(2026, 10, 7),
            RecordedAt: new DateTime(2026, 8, 1));
        Assert.StartsWith("✅ บันทึกการชำระแล้ว", TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.StaffLinked, f, false));
        Assert.Contains("ยังไม่ได้บันทึก", TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.OpenExpired, f, false));
        Assert.Contains("อย่าชำระ", TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.PaymentRequestCancelled, f, false));
        Assert.Contains("หมดอายุ", TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.PaymentRequestExpired, f, false));
        Assert.Contains("ค่าบริการ", TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.TransferNotTax, f, false));
        foreach (var k in new[] { TaxSlipReplyCard.Kind.StaffLinked, TaxSlipReplyCard.Kind.OpenExpired, TaxSlipReplyCard.Kind.PaymentRequestCancelled,
                     TaxSlipReplyCard.Kind.PaymentRequestExpired, TaxSlipReplyCard.Kind.TransferNotTax })
        {
            var t = TaxSlipReplyCard.Text(k, f, false);
            Assert.DoesNotContain("ทดสอบ จำกัด", t);   // ชื่อบริษัทปิดบาง
            Assert.NotEqual("ได้รับสลิปแล้ว — พนักงาน NextAcc จะตรวจและแจ้งผล", t);   // ไม่ตกข้อความกลาง
        }
    }
}

using Accounting.Helpers;
using Xunit;
using Origin = Accounting.Helpers.PartnerPaySourceAccount.Origin;
using SlipKind = Accounting.Helpers.PartnerPaySourceAccount.SlipKind;

namespace Accounting.Tests;

/// <summary>
/// รอบ 264 P4 (คำตัดสิน 264-5-10 · team-264-a2b-design §5.4) — ตัวตัดสินรายการเงินออกฝั่งผู้ซื้อ + ทางเดียวที่ชั้นสลิปถูกยกเป็นบันทึกเองได้ ·
/// ทั้งสองทิศ: ตรงเป๊ะทุกข้อ ⇒ Match · ขาดข้อใดข้อหนึ่ง ⇒ ไม่ใช่หลักฐาน · สลิปไม่มีรายการเดินบัญชี ⇒ ยัง preselect
/// </summary>
public class BankPaymentLineMatchTests
{
    private static readonly Guid AccB = Guid.NewGuid();
    private static readonly Guid AccB2 = Guid.NewGuid();
    private const string Payee = "1234567890";   // บัญชีรับเงินของผู้ขาย (ตัวแทน — ไม่ใช่เลขจริง)
    private static readonly DateTime Paid = new(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc);   // 10:00 เวลาไทย

    private static BankPaymentLineMatch.Target T(decimal cash = 1_040m, string? payee = Payee, Guid? id = null) => new(id ?? Guid.NewGuid(), cash, Paid, payee);

    private static BankPaymentLineMatch.Line L(decimal amount = 1_040m, string? text = "TRANSFER TO X7890 ผู้ขาย", int dayOffset = 0, bool outflow = true,
        bool unmatched = true, string? problem = null, Guid? account = null)
        => new(Guid.NewGuid(), account ?? AccB, Paid.Date.AddDays(dayOffset), outflow, amount, text, unmatched, problem);

    private static readonly IReadOnlyList<BankPaymentLineMatch.Target> None = Array.Empty<BankPaymentLineMatch.Target>();

    [Fact]
    public void ตรงทุกข้อ_รายการเดียว_เป็นหลักฐาน_Match()
    {
        var line = L();
        var r = BankPaymentLineMatch.Decide(T(), None, new[] { line });
        Assert.Equal(BankPaymentLineMatch.Kind.Match, r.Kind);
        Assert.True(r.IsEvidence);
        Assert.Equal(line.Id, r.LineId);
        Assert.Equal(AccB, r.BankAccountId);
        Assert.False(r.UsedAi);
    }

    [Theory]
    [InlineData(1_040.01)]   // ยอดไม่ตรงแม้หนึ่งสตางค์ ⇒ ไม่ใช่ (ไม่มีช่วงเผื่อ)
    [InlineData(1_039.99)]
    public void ยอดต่างแม้สตางค์เดียว_ไม่ตรง(double amount)
        => Assert.Equal(BankPaymentLineMatch.Kind.None, BankPaymentLineMatch.Decide(T(), None, new[] { L((decimal)amount) }).Kind);

    [Theory]
    [InlineData(3, BankPaymentLineMatch.Kind.Match)]
    [InlineData(-3, BankPaymentLineMatch.Kind.Match)]
    [InlineData(4, BankPaymentLineMatch.Kind.None)]
    [InlineData(-4, BankPaymentLineMatch.Kind.None)]
    public void กรอบวันบวกลบสามวัน(int offset, BankPaymentLineMatch.Kind expected)
        => Assert.Equal(expected, BankPaymentLineMatch.Decide(T(), None, new[] { L(dayOffset: offset) }).Kind);

    [Theory]
    [InlineData("TRANSFER TO X7891")]          // เลขท้ายไม่ตรง
    [InlineData("TRANSFER 1040.00")]           // ไม่มีบัญชีปลายทาง (จับจากยอดอย่างเดียวไม่ได้)
    [InlineData("TRANSFER TO X890")]           // เห็นแค่ 3 หลัก (ต่ำกว่าเกณฑ์ statement)
    [InlineData("REF 7890 ค่าสินค้า")]          // ตัวเลขล้วนสั้น (เลขอ้างอิง) ไม่ใช่บัญชี
    [InlineData(null)]
    public void บัญชีปลายทางไม่ปรากฏหรือไม่ตรง_ไม่ใช่หลักฐาน(string? text)
        => Assert.Equal(BankPaymentLineMatch.Kind.None, BankPaymentLineMatch.Decide(T(), None, new[] { L(text: text) }).Kind);

    [Theory]
    [InlineData("โอนไป xxx-x-x7890-x")]   // ตัวปิดแบบสลิปไทย (หลักที่เห็นอยู่ตำแหน่ง 1–4 นับจากขวา) ⇒ ตรงตำแหน่งกับ 1234567890? ไม่ (หลักสุดท้ายคือ 0)
    public void ปิดบางแบบเลื่อนตำแหน่ง_เทียบตามตำแหน่งจริง(string text)
        => Assert.Equal(BankPaymentLineMatch.Kind.None, BankPaymentLineMatch.Decide(T(), None, new[] { L(text: text) }).Kind);

    [Theory]
    [InlineData("โอนไป xxx-x-x6789-x")]   // ตำแหน่ง 1–4 = 6789 ✓
    [InlineData("TO ACC 1234567890")]     // เลขเต็มตรงทั้งหมด
    [InlineData("TO *****7890")]
    public void รูปเลขบัญชีที่รับ(string text)
        => Assert.Equal(BankPaymentLineMatch.Kind.Match, BankPaymentLineMatch.Decide(T(), None, new[] { L(text: text) }).Kind);

    [Fact]
    public void ไม่รู้บัญชีรับเงินของผู้ขาย_ไม่ตัดสิน()
    {
        var r = BankPaymentLineMatch.Decide(T(payee: null), None, new[] { L() });
        Assert.Equal(BankPaymentLineMatch.Kind.None, r.Kind);
        Assert.Equal(BankPaymentLineMatch.NoPayeeReason, r.Reason);
    }

    [Fact]
    public void ตรงหลายรายการ_เป็นคำแนะนำเท่านั้น()
    {
        var r = BankPaymentLineMatch.Decide(T(), None, new[] { L(), L(account: AccB2) });
        Assert.Equal(BankPaymentLineMatch.Kind.Suggest, r.Kind);
        Assert.False(r.IsEvidence);
        Assert.Null(r.BankAccountId);
    }

    [Fact]
    public void รายการเดียวตรงการแจ้งสองรายการ_เป็นคำแนะนำ_ทั้งสองทิศ()
    {
        var a = T(); var b = T();
        var line = L();
        var r = BankPaymentLineMatch.Decide(a, new[] { a, b }, new[] { line });
        Assert.Equal(BankPaymentLineMatch.Kind.Suggest, r.Kind);
        Assert.Equal(BankPaymentLineMatch.SharedLineReason, r.Reason);
        // การแจ้งอื่นที่บัญชีรับเงินต่างกัน ⇒ ไม่แย่ง ⇒ Match
        Assert.Equal(BankPaymentLineMatch.Kind.Match, BankPaymentLineMatch.Decide(a, new[] { a, T(payee: "9999999999") }, new[] { line }).Kind);
    }

    [Fact]
    public void แหล่งของบรรทัดไม่ใช่หลักฐาน_H1_เสนอเท่านั้น()
    {
        var manual = BankReceiptAutoSettle.EvidenceProblem(BankReceiptAutoSettle.SourceManual, true);
        Assert.NotNull(manual);
        var r = BankPaymentLineMatch.Decide(T(), None, new[] { L(problem: manual) });
        Assert.Equal(BankPaymentLineMatch.Kind.Suggest, r.Kind);
        Assert.False(r.IsEvidence);
        // ทิศตรงข้าม: ไฟล์ statement ที่ผู้อัปโหลดมีสิทธิ์อนุมัติ = หลักฐาน
        Assert.Null(BankReceiptAutoSettle.EvidenceProblem(BankReceiptAutoSettle.SourceStatement, true));
        Assert.NotNull(BankReceiptAutoSettle.EvidenceProblem(BankReceiptAutoSettle.SourceStatement, false));
        Assert.NotNull(BankReceiptAutoSettle.EvidenceProblem(BankReceiptAutoSettle.SourceFeed, true));
    }

    [Fact]
    public void ไม่แย่งบรรทัดฝั่งเงินเข้า_และบรรทัดที่จับคู่แล้ว()
    {
        Assert.Equal(BankPaymentLineMatch.Kind.None, BankPaymentLineMatch.Decide(T(), None, new[] { L(outflow: false) }).Kind);
        Assert.Equal(BankPaymentLineMatch.Kind.None, BankPaymentLineMatch.Decide(T(), None, new[] { L(unmatched: false) }).Kind);
    }

    // ═══════════ ทางเดียวที่ชั้นสลิปถูกยกจาก preselect เป็นบันทึกเองได้ (264-5-10 · 264-4-9) ═══════════

    private static IReadOnlyDictionary<Guid, string> Usable => new Dictionary<Guid, string> { [AccB] = "กสิกร ••3334", [AccB2] = "กสิกร ••0821" };

    [Fact]
    public void สลิปตรงบัญชีเดียว_ไม่มีรายการเดินบัญชี_ยัง_preselect_ไม่บันทึกเอง()
    {
        var f = new PartnerPaySourceAccount.Facts(Usable, SlipMatch: new PartnerPaySourceAccount.Slip(SlipKind.Unique, AccB));
        var d = PartnerPaySourceAccount.Decide(f);
        Assert.Equal(Origin.SlipUnique, d.Origin);
        Assert.Equal(AccB, d.BankAccountId);
        Assert.False(d.AllowsAuto);
        Assert.False(PartnerPaySourceAccount.SlipConfirmedByBankLine(f));
        Assert.False(PartnerPaySourceAccount.Row(Origin.SlipUnique).AllowsAuto);   // ตารางไม่ถูกแก้ — การยกเกิดผ่านชั้นที่ 1 เท่านั้น
    }

    [Fact]
    public void สลิปตรงบัญชีเดียวกับรายการเดินบัญชี_ยกเป็นบันทึกเองได้_ผ่านชั้นที่หนึ่ง()
    {
        var f = new PartnerPaySourceAccount.Facts(Usable, BankLine: AccB, BankLineLabel: "วันที่ 10/11/2026",
            SlipMatch: new PartnerPaySourceAccount.Slip(SlipKind.Unique, AccB));
        var d = PartnerPaySourceAccount.Decide(f);
        Assert.True(PartnerPaySourceAccount.SlipConfirmedByBankLine(f));
        Assert.Equal(Origin.BankLine, d.Origin);
        Assert.True(d.AllowsAuto);
        Assert.Contains("ยืนยันกัน", d.Reason);
        Assert.Empty(d.Losers);
    }

    [Fact]
    public void สลิปกับรายการเดินบัญชีคนละบัญชี_รายการเดินบัญชีชนะ_สลิปเป็นผู้แพ้_ไม่ใช่การยก()
    {
        var f = new PartnerPaySourceAccount.Facts(Usable, BankLine: AccB2, SlipMatch: new PartnerPaySourceAccount.Slip(SlipKind.Unique, AccB));
        var d = PartnerPaySourceAccount.Decide(f);
        Assert.False(PartnerPaySourceAccount.SlipConfirmedByBankLine(f));
        Assert.Equal(Origin.BankLine, d.Origin);
        Assert.Equal(AccB2, d.BankAccountId);
        Assert.Contains(d.Losers, l => l.Origin == Origin.SlipUnique && l.BankAccountId == AccB);
    }

    [Fact]
    public void รายการเดินบัญชีเป็นบัญชีนอกชุดที่ใช้ได้_ไม่ยกสลิป()
    {
        var outside = Guid.NewGuid();
        var f = new PartnerPaySourceAccount.Facts(Usable, BankLine: outside, SlipMatch: new PartnerPaySourceAccount.Slip(SlipKind.Unique, AccB));
        Assert.False(PartnerPaySourceAccount.SlipConfirmedByBankLine(f));
        Assert.False(PartnerPaySourceAccount.Decide(f).AllowsAuto);
    }

    // ═══════════ กติกาแผน "ผู้ซื้อเป็นผู้เริ่มจ่าย" + L3 ═══════════

    [Fact]
    public void แจ้งโอน_บัญชีต้องเลือกเอง_สลิปตรวจไขว้_สองทิศ()
    {
        Assert.Equal(PartnerPaymentPlanRule.NoAccountReason, PartnerPaymentPlanRule.TransferAccount(Usable, null, new PartnerPaySourceAccount.Slip(SlipKind.Unique, AccB), "ก").Problem);
        Assert.Equal(PartnerPaymentPlanRule.AccountUnusableReason, PartnerPaymentPlanRule.TransferAccount(Usable, Guid.NewGuid(), null, "ก").Problem);
        var ok = PartnerPaymentPlanRule.TransferAccount(Usable, AccB, new PartnerPaySourceAccount.Slip(SlipKind.Unique, AccB), "ก");
        Assert.Equal(AccB, ok.BankAccountId);
        Assert.Null(ok.Problem);
        var noSlip = PartnerPaymentPlanRule.TransferAccount(Usable, AccB, null, "ก");
        Assert.Equal(AccB, noSlip.BankAccountId);
        Assert.Equal(Origin.PayerDeclared, noSlip.Origin);
        var conflict = PartnerPaymentPlanRule.TransferAccount(Usable, AccB2, new PartnerPaySourceAccount.Slip(SlipKind.Unique, AccB), "ก");
        Assert.Null(conflict.BankAccountId);
        Assert.Equal(PartnerPaySourceAccount.PayerVsSlipReason, conflict.Problem);
        var notOurs = PartnerPaymentPlanRule.TransferAccount(Usable, AccB, new PartnerPaySourceAccount.Slip(SlipKind.NotOurs), "ก");
        Assert.Equal(PartnerPaySourceAccount.SlipNotOursReason, notOurs.Problem);
    }

    [Fact]
    public void ด่านเริ่มจ่าย_ลำดับเหตุ_และทิศผ่าน()
    {
        Assert.Equal(PartnerPaymentPlanRule.NoLinkReason, PartnerPaymentPlanRule.StartProblem(false, true, true, 100m));
        Assert.Equal(PartnerPaymentPlanRule.PartnerNotAcceptedReason, PartnerPaymentPlanRule.StartProblem(true, false, true, 100m));
        Assert.Equal(PartnerPaymentPlanRule.NotApprovedReason, PartnerPaymentPlanRule.StartProblem(true, true, false, 100m));
        Assert.Equal(PartnerPaymentPlanRule.NotApprovedReason, PartnerPaymentPlanRule.StartProblem(true, true, true, 0m));
        Assert.Null(PartnerPaymentPlanRule.StartProblem(true, true, true, 100m));
    }

    [Theory]
    [InlineData(SlipKind.NotOurs, PartnerPaymentNoticeRule.StatusOpen, true)]
    [InlineData(SlipKind.Unique, PartnerPaymentNoticeRule.StatusOpen, false)]
    [InlineData(SlipKind.Many, PartnerPaymentNoticeRule.StatusOpen, false)]
    [InlineData(SlipKind.Unreadable, PartnerPaymentNoticeRule.StatusOpen, false)]
    [InlineData(SlipKind.Unique, PartnerPaymentNoticeRule.StatusRecorded, true)]
    [InlineData(SlipKind.Unique, PartnerPaymentNoticeRule.StatusDismissed, true)]
    [InlineData(SlipKind.Unique, PartnerPaymentNoticeRule.StatusWithdrawn, true)]
    [InlineData(SlipKind.Unique, PartnerPaymentNoticeRule.StatusRecording, false)]
    public void L3_ล้างเลขผู้โอนเมื่อไม่ใช่บัญชีเราหรือการแจ้งจบแล้ว(SlipKind kind, string status, bool clear)
        => Assert.Equal(clear, PartnerPaymentPlanRule.ShouldClearSlipPayer(kind, status));

    [Fact]
    public void L3_ผลที่เก็บแทนเลข_NotOurs_คงผล_อื่นไม่มีชั้น()
    {
        Assert.Equal(SlipKind.NotOurs, PartnerPaymentPlanRule.SlipFromStoredKind("NotOurs")!.Kind);
        Assert.Null(PartnerPaymentPlanRule.SlipFromStoredKind("Unique"));
        Assert.Null(PartnerPaymentPlanRule.SlipFromStoredKind(null));
    }

    [Fact]
    public void เลขอ้างอิงแผน_canonical_และลิงก์กลับกัน_open_redirect()
    {
        var id = Guid.NewGuid();
        Assert.Equal("PARTNER-PLAN-" + id.ToString("N"), PartnerPaymentPlanRule.BuyerReference(id));
        Assert.Equal("/pages/documents.html", Accounting.Services.Implementations.PartnerOutboundPaymentService.SafeReturnUrl("/pages/documents.html"));
        Assert.Null(Accounting.Services.Implementations.PartnerOutboundPaymentService.SafeReturnUrl("https://evil.example/x"));
        Assert.Null(Accounting.Services.Implementations.PartnerOutboundPaymentService.SafeReturnUrl("/pages//evil.example"));
    }
}

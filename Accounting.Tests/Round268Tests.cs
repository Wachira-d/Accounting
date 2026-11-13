using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 268 (backlog cleanup · คำตัดสิน 268-1-2…6) — ตัวตัดสิน pure: สลิปออเดอร์หน้าเว็บใช้ตัวตัดสินบัญชีรับเงินไหม · แผนย้ายเงินมัดจำต่องวด ·
/// เลขอ้างอิงต่อแถวจัดสรร (ชำระหลายใบ) · ข้อความ LINE ที่เป็นสลิป/ข้อความแจ้งหลังผูก · ป้ายเหตุยกเลิกปลายทาง
/// </summary>
public class Round268Tests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000268");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000268");
    private static readonly Guid Gw = Guid.Parse("cccccccc-0000-0000-0000-000000000268");

    // ═══ 268-1-2 ═══

    [Fact]
    public void สลิปออเดอร์หน้าเว็บ_แนบสลิปหรือโอน_ใช้ตัวตัดสิน_ทิศตรงข้าม_มีรหัสธุรกรรมผู้ให้บริการหรือเงินสด_ไม่ใช้()
    {
        Assert.True(SlipReceivingAccount.AppliesToSiteOrderPayment(hasSlip: true, hasGatewayTransaction: false, PaymentMethod.Other));
        Assert.True(SlipReceivingAccount.AppliesToSiteOrderPayment(false, false, PaymentMethod.BankTransfer));
        Assert.True(SlipReceivingAccount.AppliesToSiteOrderPayment(false, false, PaymentMethod.PromptPay));
        Assert.False(SlipReceivingAccount.AppliesToSiteOrderPayment(true, hasGatewayTransaction: true, PaymentMethod.BankTransfer));
        Assert.False(SlipReceivingAccount.AppliesToSiteOrderPayment(false, false, PaymentMethod.Cash));
        Assert.False(SlipReceivingAccount.AppliesToSiteOrderPayment(false, false, PaymentMethod.CreditCard));
    }

    // ═══ 268-1-3 ═══

    private static BookingDepositInstalmentChannel.Instalment I(int n, decimal amount, bool stamped, Guid? bank, Guid? moneyIn = null)
        => new(Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), amount, stamped, bank, moneyIn);

    [Fact]
    public void มัดจำหลายงวด_งวดก่อนที่ช่องทางต่างจากใบ_ต้องย้าย_งวดสุดท้ายและช่องทางเดียวกันไม่ย้าย()
    {
        var rows = new[] { I(1, 60m, true, B), I(2, 30m, true, A), I(3, 60m, true, A) };
        var plan = BookingDepositInstalmentChannel.For(rows, rows[2].PaymentId, A, null);
        var move = Assert.Single(plan.Moves);
        Assert.Equal(rows[0].PaymentId, move.PaymentId);
        Assert.Equal(60m, move.Amount);
        Assert.Equal(B, move.BankAccountId);
        Assert.Equal(0, plan.UnknownCount);
        // ทิศตรงข้าม: ทุกงวดช่องทางเดียวกับใบ ⇒ ไม่มีอะไรย้าย (พฤติกรรมเดิมทุกสตางค์)
        Assert.Empty(BookingDepositInstalmentChannel.For(new[] { I(1, 60m, true, A), I(2, 90m, true, A) }, I(2, 0m, true, A).PaymentId, A, null).Moves);
    }

    [Fact]
    public void มัดจำหลายงวด_ผังตรงชนะธนาคาร_เงินสดเป็นช่องทางหนึ่ง_งวดที่ไม่ได้ประทับไม่เดา()
    {
        // ใบลงผังพักของผู้ให้บริการ (ผังตรง) · งวดแรกเงินสด (ไม่มีทั้งสอง) ⇒ ย้ายไปเงินสดตั้งต้น (ผู้เรียกแปลงผัง)
        var rows = new[] { I(1, 50m, true, null), I(2, 100m, true, A, Gw) };
        var plan = BookingDepositInstalmentChannel.For(rows, rows[1].PaymentId, B, Gw);   // ใบ: ธนาคาร B + ผังตรง Gw ⇒ Gw ชนะ
        var m = Assert.Single(plan.Moves);
        Assert.Null(m.BankAccountId);
        Assert.Null(m.MoneyInAccountId);
        // งวดก่อนรอบ 268 (ไม่ประทับ) ⇒ นับเป็นไม่รู้ ไม่ย้าย
        var legacy = BookingDepositInstalmentChannel.For(new[] { I(1, 50m, false, null), I(2, 100m, true, A) }, I(2, 0m, true, A).PaymentId, A, null);
        Assert.Empty(legacy.Moves);
        Assert.Equal(1, legacy.UnknownCount);
        Assert.Contains("1 งวด", BookingDepositInstalmentChannel.UnknownNote(1));
        Assert.NotEqual(BookingDepositInstalmentChannel.ReferenceFor(rows[0].PaymentId), BookingDepositInstalmentChannel.ReferenceFor(rows[1].PaymentId));
    }

    // ═══ 268-1-6 ═══

    [Fact]
    public void ชำระหลายใบ_เลขอ้างอิงต่อแถวจัดสรร_ไม่ชนรูปต่อการรับชำระ_ทิศตรงข้าม_ใบเดียวรูปเดิม()
    {
        var pay = Guid.NewGuid();
        var alloc = Guid.NewGuid();
        Assert.Equal("PARTNER-PAID-A" + alloc.ToString("N"), PartnerPaymentNoticeRule.PaymentReferenceFor(pay, null, alloc));
        Assert.Equal(new[] { "PARTNER-PAID-A" + alloc.ToString("N") }, PartnerPaymentNoticeRule.PaymentReferencesToCheck(pay, null, alloc));
        Assert.Equal("PARTNER-PAID-P" + pay.ToString("N"), PartnerPaymentNoticeRule.PaymentReferenceFor(pay, null));
        Assert.Equal(PartnerPaymentNoticeRule.PaymentReferenceFor(pay, null), PartnerPaymentNoticeRule.PaymentReferenceFor(pay, null, null));
        Assert.True(PartnerPaymentNoticeRule.PaymentReferenceFor(pay, null, alloc)!.Length <= 64);
    }

    // ═══ 268-1-4 / 268-1-5 ═══

    [Fact]
    public void ข้อความ_LINE_ที่เป็นสลิป_รูปและไฟล์PDF_ข้อความแจ้งหลังผูก_ซ่อนยอดเมื่อปิดแสดงยอด()
    {
        Assert.True(LineChatSlipRule.IsSlipMessage("image", null));
        Assert.True(LineChatSlipRule.IsSlipMessage("file", " slip.pdf "));
        Assert.False(LineChatSlipRule.IsSlipMessage("file", null));
        Assert.False(LineChatSlipRule.IsSlipMessage("file", "invoice.docx"));
        Assert.False(LineChatSlipRule.IsSlipMessage("audio", "x.pdf"));
        var shown = LineChatSlipRule.Replies.LinkedByStaff("INV-268", 1_070m, showAmounts: true);
        Assert.Contains("INV-268", shown);
        Assert.Contains("1,070.00", shown);
        Assert.DoesNotContain("1,070.00", LineChatSlipRule.Replies.LinkedByStaff("INV-268", 1_070m, showAmounts: false));
        Assert.All(new[] { LineChatSlipRule.LinkedNotice.Sent, LineChatSlipRule.LinkedNotice.CrossContact, LineChatSlipRule.LinkedNotice.Unbound,
            LineChatSlipRule.LinkedNotice.NoLine, LineChatSlipRule.LinkedNotice.Failed("x") }, t => Assert.False(string.IsNullOrWhiteSpace(t)));
    }

    // ═══ ชุดแก้ฝ่ายค้าน (268-1-7…14) ═══

    [Fact]
    public void M3_รู้ช่องทางเฉพาะเมื่อมีบัญชีหรือพนักงานบันทึกเงินสดเอง_สลิปหรือเงินออนไลน์ที่ไม่มีบัญชี_ไม่รู้()
    {
        Assert.True(BookingDepositInstalmentChannel.ChannelKnown(null, A, fromOnlinePayment: false, isSlipMoney: true));
        Assert.True(BookingDepositInstalmentChannel.ChannelKnown(Gw, null, true, false));
        Assert.True(BookingDepositInstalmentChannel.ChannelKnown(null, null, fromOnlinePayment: false, isSlipMoney: false));   // พนักงานรับเงินสดเอง
        Assert.False(BookingDepositInstalmentChannel.ChannelKnown(null, null, fromOnlinePayment: false, isSlipMoney: true));
        Assert.False(BookingDepositInstalmentChannel.ChannelKnown(null, null, fromOnlinePayment: true, isSlipMoney: false));
    }

    [Fact]
    public void M5_JE_ย้ายเงินลงวันที่เงินงวดนั้นเข้าเมื่องวดเปิด_ทิศตรงข้าม_งวดปิดลงวันที่ใบพร้อมบอกวันเข้าจริง()
    {
        var paid = new DateTime(2026, 1, 28, 3, 0, 0, DateTimeKind.Utc);
        var doc = new DateTime(2026, 2, 3, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(paid, BookingDepositInstalmentChannel.EntryDate(paid, doc, paidPeriodOpen: true));
        Assert.Equal(doc, BookingDepositInstalmentChannel.EntryDate(paid, doc, paidPeriodOpen: false));
        Assert.Equal(doc, BookingDepositInstalmentChannel.EntryDate(null, doc, true));
        Assert.Contains("เงินเข้าจริง 28/01/2026", BookingDepositInstalmentChannel.MovedNote("RD-1", 60m, "11220", paid, doc));
        Assert.DoesNotContain("เงินเข้าจริง", BookingDepositInstalmentChannel.MovedNote("RD-1", 60m, "11220", paid, paid));
    }

    [Fact]
    public void L5_ผูกภายใน30นาทีส่งเลย_นานกว่านั้นในชั่วโมงเงียบเลื่อน_นอกชั่วโมงเงียบส่งเลย()
    {
        var now = new DateTime(2026, 11, 13, 16, 0, 0, DateTimeKind.Utc);   // 23:00 เวลาไทย
        Assert.Null(LineChatSlipRule.LinkedNoticeDeferUntil(now, now.AddMinutes(-20), true, 21, 8));
        var until = LineChatSlipRule.LinkedNoticeDeferUntil(now, now.AddHours(-5), true, 21, 8);
        Assert.Equal(new DateTime(2026, 11, 14, 1, 0, 0, DateTimeKind.Utc), until);    // 08:00 เวลาไทย
        Assert.Null(LineChatSlipRule.LinkedNoticeDeferUntil(now, now.AddHours(-5), false, 21, 8));
        Assert.Null(LineChatSlipRule.LinkedNoticeDeferUntil(now.AddHours(-10), now.AddHours(-15), true, 21, 8));   // 13:00 ไทย = นอกช่วง
    }

    [Fact]
    public void L6_นับหน้า_PDF_ไม่นับ_Pages_เกินเพดานไม่ใช่สลิป()
    {
        var one = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 1 0 obj << /Type /Pages /Kids [2 0 R] >> 2 0 obj << /Type /Page >>");
        var four = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 << /Type /Pages >> " + string.Concat(Enumerable.Repeat("<< /Type/Page /MediaBox [0 0 1 1] >> ", 4)));
        Assert.Equal(1, LineChatSlipRule.PdfPageCount(one));
        Assert.Equal(4, LineChatSlipRule.PdfPageCount(four));
        Assert.True(LineChatSlipRule.PdfPageCount(four) > LineChatSlipRule.MaxPdfPages);
    }

    [Fact]
    public void L1_ค่าธรรมเนียมกระจายตามสัดส่วน_รวมเท่าเดิม_ไม่มีค่าธรรมเนียมศูนย์ทุกแถว()
    {
        var split = PartnerPaymentNoticeRule.SplitFee(10m, new[] { 1_000m, 2_000m });
        Assert.Equal(new[] { 3.33m, 6.67m }, split);
        Assert.Equal(10m, split.Sum());
        Assert.All(PartnerPaymentNoticeRule.SplitFee(0m, new[] { 1m, 2m }), x => Assert.Equal(0m, x));
        Assert.Empty(PartnerPaymentNoticeRule.SplitFee(5m, Array.Empty<decimal>()));
    }

    [Fact]
    public void M6_ป้ายไม่เลือก_ตามผลตัดสินจริง_ต้องเลือก_แนะนำ_ไม่เดา_ผู้ให้บริการ()
    {
        var must = new SlipReceivingAccount.Suggestion(null, null, SlipReceivingAccount.Origin.None, "x", RequiresChoice: true);
        var shown = new SlipReceivingAccount.Suggestion(A, A, SlipReceivingAccount.Origin.ShownToCustomer, "x");
        var none = new SlipReceivingAccount.Suggestion(null, null, SlipReceivingAccount.Origin.None, "x");
        Assert.Contains("ต้องเลือก", SlipReceivingAccount.NoChoiceLabel(true, must, null));
        Assert.Equal("ตามคำแนะนำ: กสิกร", SlipReceivingAccount.NoChoiceLabel(true, shown, "กสิกร"));
        Assert.Contains("เงินสดตั้งต้น", SlipReceivingAccount.NoChoiceLabel(true, none, null));
        Assert.Contains("ผู้ให้บริการ", SlipReceivingAccount.NoChoiceLabel(false, none, null));
        Assert.Contains("ไม่ได้ยกเลิกเอง", ProviderCentralOa.MemberRemovedReviewMessage(1, 2));
        Assert.Contains("เพิกถอนลิงก์", ProviderCentralOa.MemberRemovedReviewMessage(1, 0));
    }
}

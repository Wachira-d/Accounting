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
        Assert.Equal("ผู้ผูกถูกนำออกจากบริษัทผู้เช่า", NotifyEndpointKind.UnlinkReasonLabel(NotifyEndpointKind.UnlinkMemberRemoved));
    }
}

using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 198 ทีม E (settlement เฟส 0) — G-1..G-8 · I-1 · P-1 · S-1
///
/// <para>ทุกกลุ่มมีสองครึ่ง: ครึ่งที่พิสูจน์ว่าเคสที่พังกลับมาถูก และครึ่งที่พิสูจน์ว่าเคสที่ถูกอยู่แล้ว<b>ไม่ถูกแตะ</b>
/// (รายการสำเร็จธรรมดา · โหมด VAT ค่าเริ่มต้น · ผังลูกหนี้การค้า 11310)</para>
/// </summary>
public class SettlementPhase0Tests
{
    private static SettlementIntentInput I(decimal amount, decimal? fee, decimal refunded = 0m,
        bool alreadySettled = false, decimal refundSettled = 0m)
        => new(Guid.NewGuid(), amount, fee, 0m, refunded, alreadySettled, refundSettled);

    private static decimal Dr(SettlementPlan p) => p.Lines.Sum(l => l.Debit);
    private static decimal Cr(SettlementPlan p) => p.Lines.Sum(l => l.Credit);
    private static decimal Line(SettlementPlan p, SettlementLineRole r)
        => p.Lines.Where(l => l.Role == r).Sum(l => l.Debit + l.Credit);

    // ═══ G-2 คืนบางส่วน/คืนหลังรอบโอน ═══

    [Fact]
    public void G2_คืนบางส่วน_นับยอดหลังคืน_ไม่บล็อกถาวร()
    {
        // รับ 1,000 ค่าธรรมเนียม 36.50 คืนไป 300 ⇒ 11340 เหลือ 700 · โอนเข้า 663.50
        var p = GatewaySettlementMath.Plan(new[] { I(1000m, 36.50m, refunded: 300m) }, 663.50m,
            GatewayFeeWhtMode.None, "T1");
        Assert.True(p.Ok, p.Message);
        Assert.Equal(700m, p.Gross);
        Assert.Equal(700m, Line(p, SettlementLineRole.Clearing));
        Assert.Equal(663.50m, Line(p, SettlementLineRole.Bank));
        Assert.Equal(Dr(p), Cr(p));
    }

    [Fact]
    public void G2_คืนเต็มก่อนรอบโอน_นับศูนย์แต่ค่าธรรมเนียมยังถูกหัก()
    {
        var p = GatewaySettlementMath.Plan(new[] { I(1000m, 30m), I(500m, 15m, refunded: 500m) }, 955m,
            GatewayFeeWhtMode.None, "T1");
        Assert.True(p.Ok, p.Message);
        Assert.Equal(1000m, p.Gross);
        Assert.Equal(45m, p.FeeNetPaid);
        Assert.Equal(Dr(p), Cr(p));
    }

    [Fact]
    public void G2_คืนหลังรอบโอนก่อน_หักในรอบถัดไปเป็นยอดติดลบ()
    {
        // รอบนี้มีรายการใหม่ 2,000 (ค่าธรรมเนียม 60) + คืนเงินของรายการที่โอนไปแล้ว 250 ⇒ โอนเข้า 1,690
        var p = GatewaySettlementMath.Plan(new[]
        {
            I(2000m, 60m),
            I(1000m, 30m, refunded: 250m, alreadySettled: true, refundSettled: 0m),
        }, 1690m, GatewayFeeWhtMode.None, "T2");
        Assert.True(p.Ok, p.Message);
        Assert.Equal(1750m, p.Gross);
        Assert.Equal(250m, p.RefundDeducted);
        Assert.Equal(1690m, Line(p, SettlementLineRole.Bank));
        Assert.Equal(Dr(p), Cr(p));
    }

    [Fact]
    public void G2_คืนหลังรอบโอนที่ถูกหักไปแล้ว_ไม่นับซ้ำ()
    {
        var c = GatewaySettlementMath.Contribution(
            I(1000m, 30m, refunded: 250m, alreadySettled: true, refundSettled: 250m), GatewayFeeVatMode.None);
        Assert.Equal(0m, c.Clearing);
        Assert.Equal(0m, c.FeeDeducted);
    }

    [Fact]
    public void G2_ทิศตรงข้าม_รายการสำเร็จธรรมดา_ผลเท่าเดิม()
    {
        var p = GatewaySettlementMath.Plan(new[] { I(1000m, 32.10m), I(500m, 16.05m) }, 1451.85m,
            GatewayFeeWhtMode.None, "T1");
        Assert.True(p.Ok, p.Message);
        Assert.Equal(1500m, p.Gross);
        Assert.Equal(3, p.Lines.Count);
        Assert.Equal(0m, p.FeeVat);
        Assert.Equal(0m, p.RefundDeducted);
    }

    // ═══ G-3 VAT ของค่าธรรมเนียม → 11630 ═══

    [Fact]
    public void G3_ค่าธรรมเนียมรวมVAT_แยกขา11630()
    {
        // ค่าธรรมเนียม 42.80 รวม VAT ⇒ VAT 2.80 · ก่อน VAT 40.00
        var p = GatewaySettlementMath.Plan(new[] { I(1000m, 42.80m) }, 957.20m, GatewayFeeWhtMode.None, "T1",
            GatewayFeeVatMode.IncludedInFee, companyVatRegistered: true);
        Assert.True(p.Ok, p.Message);
        Assert.Equal(2.80m, Line(p, SettlementLineRole.FeeInputVatDeferred));
        Assert.Equal(40.00m, Line(p, SettlementLineRole.FeeExpense));
        Assert.Equal(2.80m, p.FeeVat);
        Assert.Equal(Dr(p), Cr(p));
    }

    [Fact]
    public void G3_VATเพิ่มจากค่าธรรมเนียม_ยอดสุทธิหักทั้งสองก้อน()
    {
        // ตัวอย่าง S1 §2: ขาย 1,070 · ค่าธรรมเนียม 39.06 · VAT 2.73 · โอน 1,028.21
        var p = GatewaySettlementMath.Plan(new[] { I(1070m, 39.06m) }, 1028.21m, GatewayFeeWhtMode.None, "T1",
            GatewayFeeVatMode.AddedOnTop, companyVatRegistered: true);
        Assert.True(p.Ok, p.Message);
        Assert.Equal(1028.21m, Line(p, SettlementLineRole.Bank));
        Assert.Equal(39.06m, Line(p, SettlementLineRole.FeeExpense));
        Assert.Equal(2.73m, Line(p, SettlementLineRole.FeeInputVatDeferred));
        Assert.Equal(1070m, Line(p, SettlementLineRole.Clearing));
    }

    [Fact]
    public void G3_บริษัทไม่จดVAT_ไม่มีขา11630_VATเป็นค่าใช้จ่าย()
    {
        var p = GatewaySettlementMath.Plan(new[] { I(1000m, 42.80m) }, 957.20m, GatewayFeeWhtMode.None, "T1",
            GatewayFeeVatMode.IncludedInFee, companyVatRegistered: false);
        Assert.True(p.Ok, p.Message);
        Assert.DoesNotContain(p.Lines, l => l.Role == SettlementLineRole.FeeInputVatDeferred);
        Assert.Equal(42.80m, Line(p, SettlementLineRole.FeeExpense));
        Assert.Equal(0m, p.FeeVat);
    }

    [Fact]
    public void G3_ทิศตรงข้าม_โหมดค่าเริ่มต้นไม่แยกVAT()
    {
        var p = GatewaySettlementMath.Plan(new[] { I(1000m, 42.80m) }, 957.20m, GatewayFeeWhtMode.None, "T1");
        Assert.True(p.Ok, p.Message);
        Assert.DoesNotContain(p.Lines, l => l.Role == SettlementLineRole.FeeInputVatDeferred);
        Assert.Equal(42.80m, Line(p, SettlementLineRole.FeeExpense));
    }

    // ═══ G-4 ฐาน WHT = ก่อน VAT + บล็อกจนกว่าจะออก 50 ทวิ ได้ ═══

    [Fact]
    public void G4_ฐานภาษีหักณที่จ่ายคือยอดก่อนVAT()
    {
        Assert.Equal(3.09m, GatewaySettlementMath.WhtOnFee(100m));      // 100 × 3/97
        // ค่าธรรมเนียม 107 รวม VAT ⇒ ฐาน 100 ⇒ ภาษี 3.09 (เดิม gross-up จาก 107 = 3.31)
        var p = GatewaySettlementMath.Plan(new[] { I(10000m, 107m) }, 9893m, GatewayFeeWhtMode.Withhold3Percent,
            "T1", GatewayFeeVatMode.IncludedInFee, companyVatRegistered: true);
        Assert.Equal(3.09m, p.WhtOnFee);
        Assert.Equal(Dr(p), Cr(p));
    }

    [Fact]
    public void G4_โหมดหักถูกบล็อก_ห้ามลง21917ที่ไม่มี50ทวิ_บอกทางไปต่อ()
    {
        var p = GatewaySettlementMath.Plan(new[] { I(1000m, 32.10m) }, 967.90m, GatewayFeeWhtMode.Withhold3Percent, "T1");
        Assert.False(p.Ok);
        Assert.Equal(SettlementBlockReason.WhtCertificateRequired, p.Reason);
        Assert.Contains("ตั้งค่าการรับชำระเงินออนไลน์", p.Message);
    }

    [Fact]
    public void G5_บล็อกงวดปิด_ล้างบรรทัดแต่คงตัวเลข()
    {
        var p = GatewaySettlementMath.Plan(new[] { I(1000m, 30m) }, 970m, GatewayFeeWhtMode.None, "T1");
        var b = GatewaySettlementMath.Block(p, SettlementBlockReason.PeriodClosed, "งวดปิด");
        Assert.False(b.Ok);
        Assert.Empty(b.Lines);
        Assert.Equal(p.Gross, b.Gross);
        Assert.True(p.Ok);   // แผนเดิมไม่ถูกแก้ (record with)
    }

    // ═══ G-1 คืนเงิน ═══

    [Fact]
    public void G1_คืนบางส่วนสองครั้งรวมเกินยอดรับไม่ได้()
    {
        var first = GatewayRefundMath.Check(PaymentIntentStatus.Succeeded, 1000m, 0m, 600m);
        Assert.True(first.Ok);
        Assert.False(first.IsFullRefund);
        Assert.Equal(600m, first.NewRefundedTotal);

        var second = GatewayRefundMath.Check(PaymentIntentStatus.PartiallyRefunded, 1000m, 600m, 600m);
        Assert.False(second.Ok);   // เดิมเทียบแค่ 600 ≤ 1,000 ⇒ ผ่าน = คืนรวม 1,200
        Assert.Contains("400.00", second.Message);

        var rest = GatewayRefundMath.Check(PaymentIntentStatus.PartiallyRefunded, 1000m, 600m, null);
        Assert.True(rest.Ok);
        Assert.Equal(400m, rest.Amount);
        Assert.True(rest.IsFullRefund);   // ครบยอดจากยอดสะสม ไม่ใช่ยอดครั้งนี้
    }

    [Fact]
    public void G1_คืนบางส่วนก่อนระบบบันทึกยอดคืน_ไม่รู้ยอด_ห้ามคืนเพิ่ม()
    {
        var c = GatewayRefundMath.Check(PaymentIntentStatus.PartiallyRefunded, 1000m, 0m, 100m);
        Assert.False(c.Ok);
    }

    [Fact]
    public void G1_สถานะยังไม่รับเงิน_คืนไม่ได้_และทิศตรงข้ามคืนเต็มครั้งเดียวผ่าน()
    {
        Assert.False(GatewayRefundMath.Check(PaymentIntentStatus.Pending, 1000m, 0m, null).Ok);
        Assert.False(GatewayRefundMath.Check(PaymentIntentStatus.Refunded, 1000m, 1000m, null).Ok);
        var full = GatewayRefundMath.Check(PaymentIntentStatus.Succeeded, 1000m, 0m, null);
        Assert.True(full.Ok);
        Assert.True(full.IsFullRefund);
        Assert.Equal(1000m, full.Amount);
    }

    [Fact]
    public void G1_สถานะใบลดหนี้_ไม่รู้ต้องไม่ตีเป็นครบ()
    {
        Assert.Equal(GatewayRefundCreditNoteState.NotRefunded, GatewayRefundMath.CreditNoteState(0m, true, 0m));
        Assert.Equal(GatewayRefundCreditNoteState.CreditNoteMissing, GatewayRefundMath.CreditNoteState(300m, true, 100m));
        Assert.Equal(GatewayRefundCreditNoteState.CreditNoteCovered, GatewayRefundMath.CreditNoteState(300m, true, 300m));
        Assert.Equal(GatewayRefundCreditNoteState.CannotTrace, GatewayRefundMath.CreditNoteState(300m, false, 999m));
        Assert.True(GatewayRefundMath.NeedsCreditNote(GatewayRefundCreditNoteState.CannotTrace));
        Assert.False(GatewayRefundMath.NeedsCreditNote(GatewayRefundCreditNoteState.CreditNoteCovered));
    }

    [Fact]
    public void G2_กระทบยอด_คืนบางส่วนไม่เป็นผลต่างอธิบายไม่ได้()
    {
        // รับ 1,000 คืน 300 ค่าธรรมเนียม 30 ยังไม่โอน ⇒ ควรได้ 670 = ที่ยังไม่ถึงรอบ 670
        var r = GatewayReconciliation.Compute(new[]
        {
            new GatewayIntentAmounts(1000m, 30m, 0m, null, false, false, 300m),
        });
        Assert.Equal(300m, r.RefundedAmount);
        Assert.Equal(670m, r.UnsettledAmount);
        Assert.True(r.IsBalanced);
    }

    // ═══ G-8 สิทธิ์ ═══

    [Fact]
    public void G8_คืนเงินต้องใช้สิทธิ์สั่งโอนเงินออก_ต้นทางไม่รู้จักใช้สิทธิ์เข้มสุด()
    {
        Assert.Equal(PermissionKeys.BankPaymentInit, PaymentGatewayPermissionScope.Refund);
        Assert.Equal(PermissionKeys.JournalManage, PaymentGatewayPermissionScope.PostSettlement);
        Assert.Equal(PermissionKeys.BankReconcile, PaymentGatewayPermissionScope.ConfirmManually);
        Assert.Equal(PermissionKeys.BillingManage, PaymentGatewayPermissionScope.StartKeyFor(PaymentSourceKind.AddOnPurchase));
        Assert.Equal(PermissionKeys.DocumentRevenueCreate, PaymentGatewayPermissionScope.StartKeyFor(PaymentSourceKind.Document));
        Assert.Equal(PermissionKeys.BankPaymentInit, PaymentGatewayPermissionScope.StartKeyFor((PaymentSourceKind)999));
        foreach (var k in Enum.GetValues<PaymentSourceKind>())
            Assert.StartsWith("perm:", PaymentGatewayPermissionScope.StartKeyFor(k));
    }

    // ═══ P-1 / I-1 ผังขาเงิน ═══

    [Fact]
    public void P1_ผังสำรองเป็นรหัสเต็มที่มีจริงในผังมาตรฐาน_ไม่ใช่เงินลงทุนชั่วคราว()
    {
        var names = ChartOfAccountTemplates.GetCommonAccounts().GroupBy(a => a.Code).ToDictionary(g => g.Key, g => g.First().NameTh);
        foreach (var m in Enum.GetValues<PaymentMethod>())
        {
            var code = MoneyAccountFallback.StandardCode(MoneyAccountFallback.KindOf(m));
            if (code == null) continue;   // เงินฝากธนาคาร — ต้องผูก BankAccount
            Assert.True(names.ContainsKey(code), $"{m} → {code} ไม่มีในผังมาตรฐาน");
            Assert.False(code.StartsWith("112"), $"{m} → {code} เป็นหมวดเงินลงทุนชั่วคราว");
        }
        Assert.Equal("11111", MoneyAccountFallback.StandardCode(MoneyAccountFallback.KindOf(PaymentMethod.Cash)));
        Assert.Equal("11340", MoneyAccountFallback.StandardCode(MoneyAccountFallback.KindOf(PaymentMethod.CreditCard)));
        Assert.Null(MoneyAccountFallback.StandardCode(MoneyAccountFallback.KindOf(PaymentMethod.PromptPay)));
        Assert.Null(MoneyAccountFallback.StandardCode(MoneyAccountFallback.KindOf(PaymentMethod.BankTransfer)));
    }

    [Fact]
    public void P1_บัญชีธนาคาร_บัญชีเดียวใช้ได้_ไม่มีหรือหลายบัญชีต้องไม่เดา()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal(BankAccountPickOutcome.Single, MoneyAccountFallback.PickBank(new[] { a }, out var p1));
        Assert.Equal(a, p1);
        Assert.Equal(BankAccountPickOutcome.Single, MoneyAccountFallback.PickBank(new[] { a, a }, out _));
        Assert.Equal(BankAccountPickOutcome.None, MoneyAccountFallback.PickBank(Array.Empty<Guid>(), out var p0));
        Assert.Null(p0);
        Assert.Equal(BankAccountPickOutcome.Ambiguous, MoneyAccountFallback.PickBank(new[] { a, b }, out var p2));
        Assert.Null(p2);
        Assert.Contains("หลายบัญชี", MoneyAccountFallback.BankNotResolvedMessage(BankAccountPickOutcome.Ambiguous, "x"));
    }

    // ═══ S-1 / ผังลูกหนี้ ═══

    [Fact]
    public void S1_บัญชีพักผู้ให้บริการไม่ใช่ลูกหนี้การค้า_ทิศตรงข้าม11310ยังนับ()
    {
        var none = Array.Empty<Guid>();
        Assert.False(TradeReceivableAccount.IsTradeReceivableControl(Guid.NewGuid(), "11340",
            "ลูกหนี้ผู้ให้บริการรับชำระเงิน", none));
        var custom = Guid.NewGuid();
        Assert.False(TradeReceivableAccount.IsTradeReceivableControl(custom, "11345", "ลูกหนี้ Omise", new[] { custom }));
        Assert.True(TradeReceivableAccount.IsTradeReceivableControl(Guid.NewGuid(), "11310", "ลูกหนี้การค้า", none));
        Assert.False(TradeReceivableAccount.IsTradeReceivableControl(Guid.NewGuid(), "11500", "สินค้าคงเหลือ", none));
        var names = ChartOfAccountTemplates.GetCommonAccounts().GroupBy(x => x.Code).ToDictionary(g => g.Key, g => g.First().NameTh);
        Assert.Equal("ลูกหนี้การค้า", names[TradeReceivableAccount.StandardCode]);
    }
}

using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม GF — แก้ผลฝ่ายค้านของทีม G (<c>erp-review/2026-09-29/review200-G.md</c>)
///
/// <para>ทุกกลุ่มสองทิศ: เคสที่เคยพัง (บิลบัตรเครดิตลง Dr ธนาคารที่ปักบนเครื่อง · ป้ายเตือนเงียบเมื่อปักผังที่ใช้ไม่ได้ ·
/// ขอบช่วงรอบโอนจาก PaymentIntent เลื่อน 7 ชม. · สี/รูปร่างโต๊ะแตก attribute) และเคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ
/// (โอน/พร้อมเพย์ยังลงบัญชีที่ปัก · เงินสดยังลงลิ้นชักที่ปัก · สีจากจานสียังผ่าน)</para>
/// </summary>
public class GatewayTeamGFRound200Tests
{
    private static readonly Guid CashPin = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BankPin = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0)
        => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    // ═══ R200G-2: บัญชีธนาคารที่ปักบนเครื่องใช้เฉพาะโอน/พร้อมเพย์/หักบัญชี ═══

    [Theory]
    [InlineData(PaymentMethod.CreditCard, MoneyAccountFallback.CardAcquirerClearingCode)]
    [InlineData(PaymentMethod.EWallet, MoneyAccountFallback.DigitalWalletCode)]
    [InlineData(PaymentMethod.Cheque, MoneyAccountFallback.ChequeOnHandCode)]
    public void PIN_บัตรEWalletเช็ค_ไม่ใช้บัญชีธนาคารที่ปัก_ไปผังของชนิดนั้น(PaymentMethod method, string expectedCode)
    {
        // เดิม: method != Cash ⇒ BankAccountId ⇒ บิลบัตรเครดิตของเครื่อง EDC ลง Dr ธนาคารเต็มยอด (ข้าม 11340)
        Assert.Null(MoneyAccountFallback.TerminalPinFor(method, CashPin, BankPin));
        Assert.Equal(expectedCode, MoneyAccountFallback.StandardCode(MoneyAccountFallback.KindOf(method)));
    }

    [Theory]
    [InlineData(PaymentMethod.BankTransfer)]
    [InlineData(PaymentMethod.PromptPay)]
    [InlineData(PaymentMethod.DirectDebit)]
    public void PIN_ทิศตรงข้าม_โอนพร้อมเพย์หักบัญชี_ยังลงบัญชีธนาคารที่ปัก(PaymentMethod method)
    {
        Assert.Equal(BankPin, MoneyAccountFallback.TerminalPinFor(method, CashPin, BankPin));
        // ไม่ได้ปัก ⇒ null ⇒ ผู้เรียกไป PickBank (ไม่มีรหัสมาตรฐานของเงินฝาก)
        Assert.Null(MoneyAccountFallback.TerminalPinFor(method, CashPin, null));
        Assert.Null(MoneyAccountFallback.StandardCode(MoneyAccountFallback.KindOf(method)));
    }

    [Fact]
    public void PIN_ทิศตรงข้าม_เงินสดยังลงลิ้นชักที่ปัก_และไม่หยิบบัญชีธนาคาร()
    {
        Assert.Equal(CashPin, MoneyAccountFallback.TerminalPinFor(PaymentMethod.Cash, CashPin, BankPin));
        Assert.Null(MoneyAccountFallback.TerminalPinFor(PaymentMethod.Cash, null, BankPin));
        // "อื่น ๆ" = ชนิดเงินสด (ผังสำรอง 11111) ⇒ ใช้ลิ้นชักที่ปัก ไม่ใช่ธนาคาร — ตรงกับผังสำรองของชนิดเดียวกัน
        Assert.Equal(CashPin, MoneyAccountFallback.TerminalPinFor(PaymentMethod.Other, CashPin, BankPin));
        Assert.Equal(MoneyAccountFallback.CashCode, MoneyAccountFallback.StandardCode(MoneyAccountFallback.KindOf(PaymentMethod.Other)));
    }

    // ═══ R200G-7: ป้ายเตือนหน้าเครื่องรู้ว่า "ปักผังที่ใช้ไม่ได้" ═══

    [Theory]
    [InlineData(BankAccountPickOutcome.Ambiguous, "ปิดไม่ได้")]
    [InlineData(BankAccountPickOutcome.None, "ปิดไม่ได้")]
    [InlineData(BankAccountPickOutcome.Single, "บัญชีธนาคารเดียวของบริษัทแทน")]
    public void WARN_ปักผังที่ใช้ไม่ได้_ป้ายไม่เงียบ(BankAccountPickOutcome banks, string expectedFragment)
    {
        // เดิม TerminalBankWarning(BankAccountId != null, …) ⇒ ปักไว้ = null เสมอ แม้ผังถูกลบ/ปิดใช้ (ตอนนี้ = pinUsable:true)
        Assert.Null(MoneyAccountFallback.TerminalPinWarning(true, true, banks));
        var w = MoneyAccountFallback.TerminalPinWarning(true, false, banks);
        Assert.NotNull(w);
        Assert.Contains("ใช้ไม่ได้แล้ว", w);
        Assert.Contains(expectedFragment, w);
    }

    [Theory]
    [InlineData(BankAccountPickOutcome.Ambiguous)]
    [InlineData(BankAccountPickOutcome.None)]
    [InlineData(BankAccountPickOutcome.Single)]
    public void WARN_ทิศตรงข้าม_ปักผังที่ใช้ได้_เงียบ_และไม่ได้ปักยังได้ข้อความเดิม(BankAccountPickOutcome banks)
    {
        Assert.Null(MoneyAccountFallback.TerminalPinWarning(true, true, banks));
        // ไม่ได้ปัก: ธง pinUsable ไม่มีผล (ไม่มีผังที่ปักให้ตรวจ) — ข้อความเดิมของ E-3
        Assert.Equal(MoneyAccountFallback.TerminalPinWarning(false, false, banks), MoneyAccountFallback.TerminalPinWarning(false, true, banks));
        Assert.Equal(banks == BankAccountPickOutcome.Single, MoneyAccountFallback.TerminalPinWarning(false, false, banks) == null);
    }

    // ═══ R200G-3: ขอบช่วงเวลาไทยของตัวประกอบรอบโอนจาก PaymentIntent (ช่วงเปิดปลายได้) ═══

    [Fact]
    public void RANGE_ขอบเปิดปลาย_ตรงกับช่วงเต็ม_และตีหนึ่งของวันถัดไปไม่ถูกดึงเข้า()
    {
        var from = new DateTime(2026, 9, 14);
        var to = new DateTime(2026, 9, 20);
        var (start, end) = GatewaySettlementMath.ConfirmedRangeUtc(from, to);
        Assert.Equal(start, GatewaySettlementMath.ConfirmedFromUtc(from));
        Assert.Equal(end, GatewaySettlementMath.ConfirmedToExclusiveUtc(to));
        Assert.Equal(Utc(2026, 9, 20, 17), GatewaySettlementMath.ConfirmedToExclusiveUtc(to));

        var earlyNextDay = Utc(2026, 9, 20, 18);   // 21/09 01:00 เวลาไทย
        var oldTo = ThaiDate.CalendarDateUtc(to).AddDays(1);   // สูตรเดิม = 21/09 00:00 UTC
        Assert.True(earlyNextDay < oldTo);                     // เดิมถูกดึงเข้ารอบ 20/09
        Assert.False(earlyNextDay < GatewaySettlementMath.ConfirmedToExclusiveUtc(to));

        var earlyFirstDay = Utc(2026, 9, 13, 18);  // 14/09 01:00 เวลาไทย
        Assert.False(earlyFirstDay >= ThaiDate.CalendarDateUtc(from));   // เดิมหลุด
        Assert.True(earlyFirstDay >= GatewaySettlementMath.ConfirmedFromUtc(from));
    }

    [Fact]
    public void RANGE_ทิศตรงข้าม_รับเงินกลางวันของวันสุดท้ายยังอยู่ในช่วง()
    {
        var to = new DateTime(2026, 9, 20);
        Assert.True(Utc(2026, 9, 20, 9) < GatewaySettlementMath.ConfirmedToExclusiveUtc(to));      // 16:00 ไทย
        Assert.True(Utc(2026, 9, 20, 16, 59) < GatewaySettlementMath.ConfirmedToExclusiveUtc(to)); // 23:59 ไทย
        Assert.True(Utc(2026, 9, 14, 5) >= GatewaySettlementMath.ConfirmedFromUtc(new DateTime(2026, 9, 14)));
    }

    // ═══ R200G-6: ค่าธรรมเนียม 0 จาก charge ที่ยังรอจ่ายไม่ใช่ "รู้แล้ว" ═══

    [Theory]
    [InlineData(PaymentIntentStatus.Pending)]
    [InlineData(PaymentIntentStatus.Created)]
    [InlineData(PaymentIntentStatus.Failed)]
    [InlineData(PaymentIntentStatus.Expired)]
    public void FEE_chargeที่เงินยังไม่เคลื่อน_ค่าธรรมเนียมไม่ถูกเก็บ(PaymentIntentStatus chargeStatus)
    {
        // ฉาก: StartAsync ได้ charge pending fee:0 ⇒ เดิม FeeActual = 0 ⇒ ยืนยันมือ → Succeeded ⇒ webhook successful (duplicate)
        // รับค่าเฉพาะ FeeActual == null ⇒ ค่าจริงหาย · ตอนนี้ pending ไม่ถูกเก็บ ⇒ FeeActual ยัง null ⇒ duplicate รับค่าจริงได้
        Assert.False(PaymentIntentPolicy.IsProviderFeeFinal(chargeStatus));
        Assert.True(PaymentIntentPolicy.ShouldTakeProviderFee(PaymentIntentStatus.Succeeded, null, alreadySettled: false));
    }

    [Theory]
    [InlineData(PaymentIntentStatus.Succeeded)]
    [InlineData(PaymentIntentStatus.PartiallyRefunded)]
    [InlineData(PaymentIntentStatus.Refunded)]
    public void FEE_ทิศตรงข้าม_chargeที่สำเร็จแล้ว_ยังรับค่าธรรมเนียม_และค่าที่แก้มือไม่ถูกทับ(PaymentIntentStatus chargeStatus)
    {
        Assert.True(PaymentIntentPolicy.IsProviderFeeFinal(chargeStatus));
        Assert.False(PaymentIntentPolicy.ShouldTakeProviderFee(PaymentIntentStatus.Succeeded, 39.06m, alreadySettled: false));
        Assert.False(PaymentIntentPolicy.ShouldTakeProviderFee(PaymentIntentStatus.Succeeded, null, alreadySettled: true));
    }

    // ═══ R200G-1: สี/รูปร่างโต๊ะที่ต่อเข้า CSS ═══

    [Theory]
    [InlineData("#fff\" onmouseover=\"alert(1)")]
    [InlineData("red;background:url(javascript:alert(1))")]
    [InlineData("#38bdf8\"><img src=x onerror=alert(1)>")]
    [InlineData("expression(alert(1))")]
    public void STYLE_สีข้อความอิสระ_ถูกปฏิเสธตอนบันทึก_และไม่ถูกส่งออก(string color)
    {
        Assert.NotNull(PosTableStyle.RejectReason("T1", "circle", color));
        Assert.Null(PosTableStyle.SafeColor(color));
    }

    [Theory]
    [InlineData("rectangle\" onclick=\"alert(1)")]
    [InlineData("hexagon")]
    public void STYLE_รูปร่างนอกชุด_ถูกปฏิเสธ_และส่งออกเป็นค่าเริ่มต้น(string shape)
    {
        Assert.NotNull(PosTableStyle.RejectReason("T1", shape, null));
        Assert.Equal(PosTableStyle.DefaultShape, PosTableStyle.SafeShape(shape));
    }

    [Theory]
    [InlineData("#38bdf8")]
    [InlineData("#FB7185")]
    [InlineData("#fff")]
    [InlineData("#38bdf880")]
    [InlineData(null)]
    [InlineData("")]
    public void STYLE_ทิศตรงข้าม_สีจากจานสีและค่าว่างผ่าน(string? color)
    {
        Assert.Null(PosTableStyle.RejectReason("T1", "rectangle", color));
        Assert.Equal(string.IsNullOrEmpty(color) ? null : color, PosTableStyle.SafeColor(color));
    }

    [Theory]
    [InlineData("rectangle")]
    [InlineData("square")]
    [InlineData("circle")]
    [InlineData(null)]
    public void STYLE_ทิศตรงข้าม_รูปร่างที่หน้าผังร้านรู้จักผ่าน(string? shape)
    {
        Assert.Null(PosTableStyle.RejectReason("T1", shape, "#38bdf8"));
        Assert.Equal(shape ?? PosTableStyle.DefaultShape, PosTableStyle.SafeShape(shape));
    }
}

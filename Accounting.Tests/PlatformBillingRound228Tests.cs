using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 228 — ของค้างจาก PLATFORM_OMISE_AUDIT §5 (บัตร + 3-D Secure · ใบเสร็จในศูนย์บิล · ใบลดหนี้อัตโนมัติเมื่อคืนเงิน · ข้อพิพาท · รายงาน MRR) ·
/// ทุกกลุ่มมีสองทิศ: เคสที่เปิด/เปลี่ยน และเคสที่ต้องไม่ถูกแตะ
/// </summary>
public class PlatformBillingRound228Tests
{
    private static OmisePaymentProvider Omise() => new(null!, null!, NullLogger<OmisePaymentProvider>.Instance);

    private static readonly PaymentMethodKind[] OmiseMethods =
    {
        PaymentMethodKind.PromptPay, PaymentMethodKind.Card, PaymentMethodKind.MobileBanking, PaymentMethodKind.InternetBanking, PaymentMethodKind.TrueMoney,
    };

    // ═══ §5.1 บัตร: สิ่งที่หน้าชำระต้องรู้ (สคริปต์ + คีย์สาธารณะของโหมดนี้) ═══

    [Fact]
    public void Card1_มีคีย์สาธารณะของโหมดนี้_รับบัตรได้_ไม่มี_รับไม่ได้()
    {
        var cfg = new PaymentProviderConfig { ProviderCode = OmisePaymentProvider.Code, Mode = PaymentProviderMode.Test, TestPublicKey = "pkey_test_x", LivePublicKey = null };
        var setup = PaymentClientSetup.For(Omise(), cfg);
        Assert.NotNull(setup);
        Assert.Equal("pkey_test_x", setup!.PublicKey);
        Assert.True(setup.IsTestMode);
        Assert.Equal(OmisePaymentProvider.Code, setup.ProviderCode);
        Assert.False(string.IsNullOrWhiteSpace(setup.ScriptUrl));

        // โหมดจริงแต่ยังไม่ใส่คีย์สาธารณะจริง ⇒ รับบัตรไม่ได้ (ไม่หยิบคีย์ทดสอบมาใช้ข้ามโหมด)
        cfg.Mode = PaymentProviderMode.Live;
        Assert.Null(PaymentClientSetup.For(Omise(), cfg));
        // ผู้ให้บริการที่ไม่รองรับบัตร ⇒ null
        Assert.Null(PaymentClientSetup.For(null, cfg));
    }

    [Fact]
    public void Card2_รับบัตรไม่ได้_ตัดปุ่มบัตรออก_รับได้_คงไว้()
    {
        var all = new[] { "PromptPay", "Card", "TrueMoney" };
        Assert.Equal(new[] { "PromptPay", "TrueMoney" }, PaymentClientSetup.WithoutUnusableCard(all, null));
        var setup = new PaymentClientSetup("omise", "https://x/s.js", "pkey_test_x", true);
        Assert.Equal(all, PaymentClientSetup.WithoutUnusableCard(all, setup));
    }

    [Fact]
    public void Card3_วิธีที่ผู้เช่าเห็น_บัตรเปิดเมื่อพร้อม_วิธีอื่นยังปิด()
    {
        Assert.Equal(new[] { "PromptPay", "Card" }, PlatformPayMethods.Effective(OmiseMethods, Array.Empty<string>(), cardClientReady: true));
        Assert.Equal(new[] { "Card" }, PlatformPayMethods.Effective(OmiseMethods, new[] { "card" }, cardClientReady: true));
        // ทิศตรงข้าม: ไม่มีคีย์ ⇒ เหมือนรอบ 227 · วอลเล็ท/แบงก์กิ้งยังปิดแม้บัตรพร้อม
        Assert.Equal(new[] { "PromptPay" }, PlatformPayMethods.Effective(OmiseMethods, Array.Empty<string>(), cardClientReady: false));
        Assert.Null(PlatformPayMethods.NotPayableReason(PaymentMethodKind.Card, cardClientReady: true));
        Assert.NotNull(PlatformPayMethods.NotPayableReason(PaymentMethodKind.TrueMoney, cardClientReady: true));
        Assert.NotNull(PlatformPayMethods.NotPayableReason(PaymentMethodKind.MobileBanking, cardClientReady: true));
    }

    // ═══ §5.5 คืนเงินค่าบริการ + ใบลดหนี้ §86/10 ═══

    [Fact]
    public void Cn1_ยอดใบลดหนี้_รวม_VAT_แยกเหมือนใบเสร็จต้นทาง()
    {
        var p = PlatformRefundCreditNote.Decide(107m, true, false, 535m, 0m, originalHasVat: true);
        Assert.True(p.Ok);
        Assert.Equal(100m, p.BaseAmount);
        Assert.Equal(7m, p.VatAmount);
        var nonVat = PlatformRefundCreditNote.Decide(107m, true, false, 535m, 0m, originalHasVat: false);
        Assert.Equal(107m, nonVat.BaseAmount);
        Assert.Equal(0m, nonVat.VatAmount);
    }

    [Theory]
    [InlineData(300, 300, 0, true)]     // คืนเต็มใบพอดี — ผ่าน
    [InlineData(100, 300, 200, true)]   // รวมพอดียอดเดิม — ผ่าน
    [InlineData(100.01, 300, 200, false)] // รวมเกินใบเดิม — ปฏิเสธ (§86/10)
    [InlineData(0, 300, 0, false)]      // ยอดศูนย์ — ปฏิเสธ
    public void Cn2_ผลรวมใบลดหนี้ห้ามเกินใบเดิม(double refund, double original, double credited, bool ok)
        => Assert.Equal(ok, PlatformRefundCreditNote.Decide((decimal)refund, true, false, (decimal)original, (decimal)credited, true).Ok);

    [Fact]
    public void Cn3_ไม่มีใบเดิม_หรือใบเดิมถูกยกเลิก_ไม่ออก_พร้อมทางไปต่อ()
    {
        var none = PlatformRefundCreditNote.Decide(50m, false, false, 0m, 0m, true);
        Assert.False(none.Ok);
        Assert.Contains("ออกใบเสร็จอีกครั้ง", none.Problem);
        var voided = PlatformRefundCreditNote.Decide(50m, true, true, 100m, 0m, true);
        Assert.False(voided.Ok);
        Assert.Contains("ยกเลิก", voided.Problem);
    }

    [Fact]
    public void Cn4_ลงบัญชีเฉพาะรายการใต้บริษัทผู้ให้บริการ_รายการเก่าใต้ร้านไม่แตะ()
    {
        var platform = Guid.NewGuid();
        var store = Guid.NewGuid();
        Assert.True(PlatformRevenueSource.RefundBooksInPlatformTenant(PaymentSourceKind.AddOnPurchase, platform, platform));
        Assert.True(PlatformRevenueSource.RefundBooksInPlatformTenant(PaymentSourceKind.SubscriptionPayment, platform, platform));
        // ทิศตรงข้าม: รายการเก่าใต้บริษัทร้าน (ก่อนเฟส 0) · ยังไม่ตั้งบริษัทผู้ให้บริการ · รายการของร้าน
        Assert.False(PlatformRevenueSource.RefundBooksInPlatformTenant(PaymentSourceKind.AddOnPurchase, store, platform));
        Assert.False(PlatformRevenueSource.RefundBooksInPlatformTenant(PaymentSourceKind.AddOnPurchase, platform, null));
        Assert.False(PlatformRevenueSource.RefundBooksInPlatformTenant(PaymentSourceKind.SiteOrder, platform, platform));
    }

    [Fact]
    public void Cn5_เลขอ้างอิงใบลดหนี้คงที่ต่อการคืนเงิน_และหมายเหตุอ้างใบเดิมเป็นพศ()
    {
        Assert.Equal("REFUND-rfnd_test_1", PlatformRefundCreditNote.Reference("rfnd_test_1"));
        Assert.Equal(PlatformRefundCreditNote.Reference("rfnd_test_1"), PlatformRefundCreditNote.Reference(" rfnd_test_1 "));
        Assert.True(PlatformRefundCreditNote.Reference(new string('x', 300)).Length <= 100);
        var notes = PlatformRefundCreditNote.Notes("RE-0001", new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), 535m, 107m, "เก็บซ้อน");
        Assert.Contains("RE-0001", notes);
        Assert.Contains("2569", notes);
        Assert.Contains("107.00", notes);
    }

    // ═══ §5.4 ข้อพิพาท/chargeback ═══

    [Fact]
    public void Dp1_อ่าน_dispute_ของผู้ให้บริการ_สถานะไม่รู้จัก_เป็นเปิด()
    {
        using var doc = JsonDocument.Parse("""{"object":"dispute","id":"dspt_test_1","status":"lost","amount":53500,"reason_message":"fraud","charge":"chrg_test_1"}""");
        var d = OmisePaymentProvider.ParseDispute(doc.RootElement);
        Assert.Equal("dspt_test_1", d.ProviderRef);
        Assert.Equal(PaymentDisputeState.Lost, d.State);
        Assert.Equal(535m, d.Amount);
        Assert.Equal("fraud", d.Reason);
        using var odd = JsonDocument.Parse("""{"object":"dispute","id":"dspt_x","status":"something_new","amount":100}""");
        Assert.Equal(PaymentDisputeState.Open, OmisePaymentProvider.ParseDispute(odd.RootElement).State);   // ไม่เดาว่าจบแล้ว
    }

    [Fact]
    public void Dp2_เลข_charge_ต้องเป็นรูปเลข_charge_ก่อนต่อ_path()
    {
        Assert.True(OmisePaymentProvider.IsWellFormedChargeId("chrg_test_5abc"));
        Assert.False(OmisePaymentProvider.IsWellFormedChargeId("../events/x"));
        Assert.False(OmisePaymentProvider.IsWellFormedChargeId("chrg_a/b"));
        Assert.False(OmisePaymentProvider.IsWellFormedChargeId(null));
    }

    [Fact]
    public void Dp3_ต้องให้แอดมินจัดการ_และ_webhook_ซ้ำไม่นับเป็นการเปลี่ยน()
    {
        Assert.True(PaymentDisputeRules.NeedsAttention(PaymentDisputeState.Open));
        Assert.True(PaymentDisputeRules.NeedsAttention(PaymentDisputeState.Lost));
        Assert.False(PaymentDisputeRules.NeedsAttention(PaymentDisputeState.Won));
        Assert.False(PaymentDisputeRules.NeedsAttention(null));
        // ส่งซ้ำ (redelivery) = ไม่บันทึก · เปลี่ยนสถานะ = บันทึก · ครั้งแรก = บันทึก
        Assert.False(PaymentDisputeRules.ShouldApply("dspt_1", PaymentDisputeState.Open, 100m, "dspt_1", PaymentDisputeState.Open, 100m));
        Assert.True(PaymentDisputeRules.ShouldApply("dspt_1", PaymentDisputeState.Open, 100m, "dspt_1", PaymentDisputeState.Lost, 100m));
        Assert.True(PaymentDisputeRules.ShouldApply(null, null, null, "dspt_1", PaymentDisputeState.Open, 100m));
        Assert.NotNull(PaymentDisputeRules.NextStep(PaymentDisputeState.Lost));
        Assert.Null(PaymentDisputeRules.NextStep(PaymentDisputeState.Won));
    }

    [Fact]
    public void Dp4_ลำดับสลับ_ผลออกแล้วห้ามถอย_เว้นแต่เป็นข้อพิพาทใหม่()
    {
        // event "เปิด" มาช้ากว่า "แพ้" ของข้อพิพาทเดียวกัน ⇒ ไม่ถอย
        Assert.False(PaymentDisputeRules.ShouldApply("dspt_1", PaymentDisputeState.Lost, 100m, "dspt_1", PaymentDisputeState.Open, 100m));
        Assert.False(PaymentDisputeRules.ShouldApply("dspt_1", PaymentDisputeState.Won, 100m, "dspt_1", PaymentDisputeState.UnderReview, 100m));
        // เลขว่าง = ข้อพิพาทเดิม (ไม่ถอยเช่นกัน)
        Assert.False(PaymentDisputeRules.ShouldApply("dspt_1", PaymentDisputeState.Closed, 100m, "", PaymentDisputeState.Open, 100m));
        // ทิศตรงข้าม: ข้อพิพาทใหม่ (เลขต่าง) หลังข้อแรกจบ ⇒ บันทึก · ผลปลายทางเปลี่ยนเป็นปลายทางอื่น ⇒ บันทึก
        Assert.True(PaymentDisputeRules.ShouldApply("dspt_1", PaymentDisputeState.Won, 100m, "dspt_2", PaymentDisputeState.Open, 50m));
        Assert.True(PaymentDisputeRules.ShouldApply("dspt_1", PaymentDisputeState.Closed, 100m, "dspt_1", PaymentDisputeState.Lost, 100m));
    }

    [Fact]
    public void Dp5_รับทราบแล้ว_ไม่ต้องจัดการจนกว่าข้อพิพาทเปลี่ยน()
    {
        var t = new DateTime(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc);
        Assert.True(PaymentDisputeRules.NeedsAttention(PaymentDisputeState.Open, t, null));
        Assert.False(PaymentDisputeRules.NeedsAttention(PaymentDisputeState.Open, t, t.AddMinutes(5)));
        Assert.True(PaymentDisputeRules.NeedsAttention(PaymentDisputeState.Lost, t.AddMinutes(10), t.AddMinutes(5)));   // เปลี่ยนหลังรับทราบ
        Assert.False(PaymentDisputeRules.NeedsAttention(PaymentDisputeState.Won, t, null));
    }

    [Fact]
    public void Cn6_เลขกันซ้ำต่อการคืนเงิน_คำอธิบายพกเหตุผล_ป้ายเรื่องค้าง()
    {
        // ตรวจผลซ้ำได้เลขภายในเดิม แต่ยอดสะสมต่างกัน ⇒ คนละใบ
        Assert.NotEqual(PlatformRefundCreditNote.DedupeKey("VERIFY-1234", 100m), PlatformRefundCreditNote.DedupeKey("VERIFY-1234", 150m));
        Assert.Equal(PlatformRefundCreditNote.DedupeKey("rfnd_1", 107m), PlatformRefundCreditNote.DedupeKey(" rfnd_1 ", 107.00m));
        Assert.Contains("เก็บซ้อน", PlatformRefundCreditNote.LineDescription("TIV-1", "เก็บซ้อน"));
        Assert.Equal("คืนเงินค่าบริการ อ้างใบ TIV-1", PlatformRefundCreditNote.LineDescription("TIV-1", "  "));
        Assert.Null(PlatformReceiptGap.CreditNoteReason(null, 107m));
        Assert.Contains("ออกใบลดหนี้อีกครั้ง", PlatformReceiptGap.CreditNoteReason("งวดปิด", 107m));
    }

    [Fact]
    public void Cn7_ร้าน_ใบลดหนี้หลังคืนเงินgateway_ใช้ผังลูกหนี้_ทิศตรงข้ามคงเดิม()
    {
        Assert.True(GatewayRefundCreditNoteCounter.UsesRefundReceivable(true, true, 0m, 107m, 107m));
        Assert.True(GatewayRefundCreditNoteCounter.UsesRefundReceivable(true, true, 0m, 200m, 107m));
        // ทิศตรงข้าม: ไม่มีคืนเงิน/คืนน้อยกว่าใบลดหนี้ · ฝั่งซื้อ · ไม่ใช่ขาเงิน (ใบยังค้างชำระ) · มีภาษีหัก ณ ที่จ่าย ⇒ พฤติกรรมเดิม
        Assert.False(GatewayRefundCreditNoteCounter.UsesRefundReceivable(true, true, 0m, 0m, 107m));
        Assert.False(GatewayRefundCreditNoteCounter.UsesRefundReceivable(true, true, 0m, 50m, 107m));
        Assert.False(GatewayRefundCreditNoteCounter.UsesRefundReceivable(false, true, 0m, 107m, 107m));
        Assert.False(GatewayRefundCreditNoteCounter.UsesRefundReceivable(true, false, 0m, 107m, 107m));
        Assert.False(GatewayRefundCreditNoteCounter.UsesRefundReceivable(true, true, 3m, 107m, 107m));
        Assert.Equal(7m, GatewayRefundCreditNoteCounter.Uncovered(107m, 100m));
        Assert.Equal(0m, GatewayRefundCreditNoteCounter.Uncovered(107m, 200m));
    }

    // ═══ งานข้อ 6: รายงาน MRR/ลูกหนี้ ═══

    [Fact]
    public void Mrr1_ราคาต่อรอบเป็นรายเดือน_ปัดครึ่งขึ้น()
    {
        Assert.Equal(100m, PlatformRevenueMetrics.MonthlyEquivalent(1200m, BillingCycle.Annual));
        Assert.Equal(333.33m, PlatformRevenueMetrics.MonthlyEquivalent(1000m, BillingCycle.Quarterly));
        Assert.Equal(0.17m, PlatformRevenueMetrics.MonthlyEquivalent(1.00m, BillingCycle.SemiAnnual));   // 0.1666… ⇒ 0.17
        Assert.Equal(499m, PlatformRevenueMetrics.MonthlyEquivalent(499m, BillingCycle.Monthly));
    }

    [Fact]
    public void Mrr2_นับเฉพาะแพ็กเกจจ่ายเงินที่ใช้งาน()
    {
        Assert.True(PlatformRevenueMetrics.CountsTowardMrr(SubscriptionStatus.Active, false, 499m));
        Assert.True(PlatformRevenueMetrics.CountsTowardMrr(SubscriptionStatus.PastDue, false, 499m));
        Assert.False(PlatformRevenueMetrics.CountsTowardMrr(SubscriptionStatus.Trial, false, 499m));
        Assert.False(PlatformRevenueMetrics.CountsTowardMrr(SubscriptionStatus.Active, true, 499m));
        Assert.False(PlatformRevenueMetrics.CountsTowardMrr(SubscriptionStatus.Active, false, 0m));
        Assert.False(PlatformRevenueMetrics.CountsTowardMrr(SubscriptionStatus.Suspended, false, 499m));
    }

    [Fact]
    public void Mrr4_ส่วนเสริมนับ_MRR_เฉพาะจ่ายแล้ว_พ้นทดลอง_บริษัทใช้งาน()
    {
        var now = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(PlatformRevenueMetrics.AddOnCountsTowardMrr(AddOnPaymentStatus.Paid, null, true, now));
        Assert.True(PlatformRevenueMetrics.AddOnCountsTowardMrr(AddOnPaymentStatus.Paid, now.AddDays(-1), true, now));
        Assert.False(PlatformRevenueMetrics.AddOnCountsTowardMrr(AddOnPaymentStatus.AwaitingPayment, null, true, now));
        Assert.False(PlatformRevenueMetrics.AddOnCountsTowardMrr(AddOnPaymentStatus.Paid, now.AddDays(3), true, now));   // ยังทดลอง
        Assert.False(PlatformRevenueMetrics.AddOnCountsTowardMrr(AddOnPaymentStatus.Paid, null, false, now));            // บริษัทหมดอายุ/ระงับ
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(30, 1)]
    [InlineData(31, 2)]
    [InlineData(90, 3)]
    [InlineData(91, 4)]
    public void Mrr3_ช่วงอายุลูกหนี้(int? days, int bucketIndex)
        => Assert.Equal(PlatformRevenueMetrics.AgingBuckets[bucketIndex], PlatformRevenueMetrics.AgingBucket(days));
}

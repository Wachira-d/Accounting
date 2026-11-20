using Accounting.Helpers;
using Accounting.Models.Constants;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 281 (คำตัดสิน 281-2 · 281-3) — เส้น A↔B ระหว่างคู่ค้า NextAcc: ตัวตัดสิน "เส้นทางของใบนี้" (<see cref="PartnerJourney"/>) ·
/// คีย์แจ้งเตือนต่อขั้น (<see cref="PartnerFlowNotificationSplit"/>) — ไม่แต่งสถานะ "เสร็จ" · อีกฝั่งไม่เห็นเลข/เหตุภายใน · ค่าตั้งเดิมไม่หาย
/// </summary>
public class PartnerFlowRound281Tests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>ใบแจ้งหนี้ค่าบริการ 10,700 (VAT 700) หัก 3% = 300 · ครบทั้งเส้น</summary>
    private static PartnerJourney.Facts Full(bool seller) => new(
        ViewerIsSeller: seller, InvoiceSentAt: T0, SellerDocumentNumber: "INV-001", SourceIsTaxInvoice: false, SourceVoided: false,
        PurchaseCreatedAt: T0, PurchaseNumber: "PI-009", PurchaseProblem: null,
        Total: 10_700m, SellerReceived: 10_700m, SellerSettled: true, LastReceivedAt: T0.AddDays(1),
        ReceiptIssuedAt: T0.AddDays(1), ReceiptNumber: "RE-001", ReceiptDeliveredAt: T0.AddDays(1), InputVatEvidenceLabel: "ต้องได้ต้นฉบับ",
        BuyerRecordedAt: T0.AddDays(1), BuyerPaymentNumber: "PV-001", BuyerBlockedReason: null,
        PurchaseWht: 300m, WhtCertIssuedAt: T0.AddDays(1), WhtCertNumber: "WHT-001", WhtCertAmount: 300m,
        WhtDeliveredAt: T0.AddDays(1), WhtDeliveryError: null, SendWhtEnabled: true, WhtCreditedAt: T0.AddDays(1), WhtAwaitingConfirm: false);

    [Fact]
    public void ครบทั้งเส้น_ทุกขั้นเสร็จ_ไม่มีขั้นปัจจุบัน_ลำดับ8ขั้น()
    {
        var rows = PartnerJourney.Build(Full(seller: false));
        Assert.Equal(8, rows.Count);
        Assert.All(rows, r => Assert.Equal(nameof(PartnerJourney.State.Done), r.State));
        Assert.Null(PartnerJourney.CurrentStep(rows));
        Assert.Equal(Enum.GetNames<PartnerJourney.Step>(), rows.Select(r => r.Step).ToArray());
        Assert.Contains("PV-001", rows.Single(r => r.Step == nameof(PartnerJourney.Step.BuyerRecorded)).Detail);   // ผู้ซื้อเห็นเลขของตัวเอง
    }

    [Fact]
    public void ผู้ขายดู_ไม่เห็นเลขใบซื้อ_ใบสำคัญจ่าย_50ทวิ_ของผู้ซื้อ_เห็นใบเสร็จของตัวเอง()
    {
        var rows = PartnerJourney.Build(Full(seller: true));
        var all = string.Join(" | ", rows.Select(r => r.Detail));
        Assert.DoesNotContain("PI-009", all);
        Assert.DoesNotContain("PV-001", all);
        Assert.DoesNotContain("WHT-001", all);
        Assert.Contains("RE-001", all);
        Assert.DoesNotContain("ต้องได้ต้นฉบับ", all);   // หลักฐานภาษีซื้อเป็นเรื่องในสมุดผู้ซื้อ
    }

    [Fact]
    public void ไม่แต่งสถานะ_ยังไม่ชำระ_ขั้นหลังทั้งหมดรอ_ไม่ใช่เสร็จ()
    {
        var f = Full(false) with
        {
            SellerReceived = 0m, SellerSettled = false, LastReceivedAt = null, ReceiptIssuedAt = null, ReceiptNumber = null, ReceiptDeliveredAt = null,
            BuyerRecordedAt = null, BuyerPaymentNumber = null, WhtCertIssuedAt = null, WhtCertNumber = null, WhtCertAmount = 0m, WhtDeliveredAt = null, WhtCreditedAt = null,
        };
        var rows = PartnerJourney.Build(f);
        Assert.Equal(nameof(PartnerJourney.Step.Payment), PartnerJourney.CurrentStep(rows));
        Assert.All(rows.Skip(2), r => Assert.Equal(nameof(PartnerJourney.State.Waiting), r.State));
    }

    [Fact]
    public void รับเงินแล้วแต่ไม่มีใบกำกับ_หยุด_บอกทางไปต่อ_ทิศตรงข้ามใบที่ส่งเป็นใบกำกับอยู่แล้วเสร็จ()
    {
        var f = Full(true) with { ReceiptIssuedAt = null, ReceiptNumber = null, ReceiptDeliveredAt = null };
        var row = PartnerJourney.Build(f).Single(r => r.Step == nameof(PartnerJourney.Step.TaxInvoice));
        Assert.Equal(nameof(PartnerJourney.State.Blocked), row.State);
        Assert.Contains("รับเงินแล้วยังไม่มีใบกำกับ", row.Detail);
        var ti = PartnerJourney.Build(f with { SourceIsTaxInvoice = true }).Single(r => r.Step == nameof(PartnerJourney.Step.TaxInvoice));
        Assert.Equal(nameof(PartnerJourney.State.Done), ti.State);
    }

    [Fact]
    public void ไม่มีภาษีหัก_สามขั้น50ทวิไม่เกี่ยว_ครบเส้นได้()
    {
        var f = Full(false) with { PurchaseWht = 0m, WhtCertIssuedAt = null, WhtCertNumber = null, WhtCertAmount = 0m, WhtDeliveredAt = null, WhtCreditedAt = null };
        var rows = PartnerJourney.Build(f);
        Assert.Equal(3, rows.Count(r => r.State == nameof(PartnerJourney.State.NotApplicable)));
        Assert.Null(PartnerJourney.CurrentStep(rows));
    }

    [Fact]
    public void ท่อผู้ซื้อหยุด_ผู้ซื้อเห็นเหตุ_ผู้ขายเห็นแค่รอ()
    {
        var f = Full(false) with { BuyerRecordedAt = null, BuyerPaymentNumber = null, BuyerBlockedReason = "ไม่รู้ว่าเงินออกจากบัญชีไหน", WhtCertIssuedAt = null, WhtDeliveredAt = null, WhtCreditedAt = null };
        var buyer = PartnerJourney.Build(f).Single(r => r.Step == nameof(PartnerJourney.Step.BuyerRecorded));
        Assert.Equal(nameof(PartnerJourney.State.Blocked), buyer.State);
        Assert.Contains("บัญชีไหน", buyer.Detail);
        var seller = PartnerJourney.Build(f with { ViewerIsSeller = true }).Single(r => r.Step == nameof(PartnerJourney.Step.BuyerRecorded));
        Assert.DoesNotContain("บัญชีไหน", seller.Detail);
    }

    [Fact]
    public void ห้าสิบทวิ_ไม่เปิดส่งให้คู่ค้า_หยุดพร้อมชื่อค่าตั้ง_ส่งล้ม_หยุดพร้อมเหตุ_รอยืนยัน_รอ()
    {
        var notSent = Full(false) with { WhtDeliveredAt = null, WhtCreditedAt = null, SendWhtEnabled = false };
        var r1 = PartnerJourney.Build(notSent).Single(r => r.Step == nameof(PartnerJourney.Step.WhtCertDelivered));
        Assert.Equal(nameof(PartnerJourney.State.Blocked), r1.State);
        Assert.Contains("ส่งหนังสือรับรอง 50 ทวิ ที่เราออกให้คู่ค้าเข้าระบบของเขาอัตโนมัติ", r1.Detail);
        var failed = notSent with { SendWhtEnabled = true, WhtDeliveryError = "ที่อยู่ผู้ถูกหักว่าง" };
        Assert.Contains("ที่อยู่", PartnerJourney.Build(failed).Single(r => r.Step == nameof(PartnerJourney.Step.WhtCertDelivered)).Detail);
        var awaiting = Full(true) with { WhtCreditedAt = null, WhtAwaitingConfirm = true };
        var r3 = PartnerJourney.Build(awaiting).Single(r => r.Step == nameof(PartnerJourney.Step.WhtCertCredited));
        Assert.Equal(nameof(PartnerJourney.State.Waiting), r3.State);
        Assert.Contains("ยืนยัน", r3.Detail);
    }

    [Fact]
    public void ผู้ขายยกเลิกใบ_ขั้นแรกหยุด()
    {
        var rows = PartnerJourney.Build(Full(false) with { SourceVoided = true });
        Assert.Equal(nameof(PartnerJourney.Step.InvoiceSent), PartnerJourney.CurrentStep(rows));
    }

    // ───────── 281-1: ยอดค้างหลังหัก ⇒ ยอดก่อนหักผ่าน AmountsFor ก่อนเข้าตัวคิด (สูตรของ PartnerPaymentNoticeService.PlanForAsync) ─────────

    [Fact]
    public void ใบ10700หัก300_ยอดค้างหลังหัก10400_ผู้ขายรับ10400_แผนจ่าย10400หัก300_ไม่มีคำเตือน_ทิศตรงข้ามบั๊กเดิม()
    {
        var gross = DocumentPayLinkPolicy.AmountsFor(10_400m, 300m, 0m).Gross;
        Assert.Equal(10_700m, gross);
        var p = PartnerPaymentNoticeRule.For(Accounting.Models.Enums.DocumentStatus.Approved, gross, 300m, 10_400m, "PI-281",
            sellerRecordedWht: 300m, singleWhtRatePercent: 3m, purchaseHasVat: true);
        Assert.Equal(10_400m, p.Amount);
        Assert.Equal(300m, p.Wht);
        Assert.Null(p.Warning);
        Assert.Null(p.Problem);
        // บั๊กเดิม (ส่งยอดค้างหลังหักตรง ๆ): หัก 0 + คำเตือน "ผู้ขายรับเต็ม" ⇒ ไม่มี 50 ทวิ · ท่อไม่บันทึกเอง
        var old = PartnerPaymentNoticeRule.For(Accounting.Models.Enums.DocumentStatus.Approved, 10_400m, 300m, 10_400m, "PI-281");
        Assert.Equal(0m, old.Wht);
        Assert.NotNull(old.Warning);
        // ยอดค้าง 0 ⇒ ยอดก่อนหัก 0 ⇒ "ชำระครบแล้ว" เหมือนเดิม (ไม่บวกภาษีหักค้างให้ใบที่จ่ายแล้ว)
        Assert.Equal(0m, DocumentPayLinkPolicy.AmountsFor(0m, 300m, 0m).Gross);
        // ใบไม่มีภาษีหัก ⇒ ตัวเลขเดิมทุกสตางค์
        Assert.Equal(1_070m, DocumentPayLinkPolicy.AmountsFor(1_070m, 0m, 0m).Gross);
    }

    // ───────── คีย์แจ้งเตือนต่อขั้น (281-2) ─────────

    [Fact]
    public void คีย์ต่อขั้น_อยู่ในแค็ตตาล็อก_ค่าตั้งต้นเหมือน_partner_exchange_ไม่ซ้ำกัน()
    {
        var catalog = NotificationEvents.All.Select(e => e.Key).ToHashSet();
        Assert.Equal(7, PartnerFlowNotificationSplit.Keys.Distinct().Count());
        foreach (var k in PartnerFlowNotificationSplit.Keys)
        {
            Assert.Contains(k, catalog);
            Assert.Contains(k, NotificationAudiencePolicy.EmailDefaultKeys);
            Assert.NotEqual(NotificationEvents.AutopilotPartnerExchange, k);
            Assert.Equal(NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.AutopilotPartnerExchange, true),
                NotificationAudiencePolicy.DefaultMatrix(k, true));
        }
    }

    [Fact]
    public void คัดลอกค่าตั้ง_เฉพาะบริษัทที่ยังไม่มีแถวคีย์ใหม่_จากคีย์เดิม_ทุกคีย์()
    {
        Assert.Equal(NotificationEvents.AutopilotPartnerExchange, PartnerFlowNotificationSplit.FromKey);
        var sql = PartnerFlowNotificationSplit.MigrationSql;
        foreach (var k in PartnerFlowNotificationSplit.Keys)
        {
            Assert.Contains($"x.\"EventKey\" = '{k}'", sql);
        }
        Assert.Contains("x.\"CompanyId\" = s.\"CompanyId\"", PartnerFlowNotificationSplit.SettingsCopySql(NotificationEvents.PartnerSellerPaid));
        Assert.Contains("x.\"UserId\" = p.\"UserId\"", PartnerFlowNotificationSplit.PreferencesCopySql(NotificationEvents.PartnerSellerPaid));
        Assert.Contains("ON CONFLICT DO NOTHING", sql);
    }
}

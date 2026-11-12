using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 260-3 (pure) — 260-3-1 ลูกค้าเห็นเงินรอจัดสรรเมื่อผ่านด่านใบแจ้งยอดเท่านั้น · 260-3-3 กระดิ่งเงินรอจัดสรรค้างนาน (สรุป/แจ้งครั้งเดียว · กระดิ่งเจ้าของ) ·
/// 260-3-4 การ์ดความพร้อมรับเงิน (ประกอบจากแถวเดิม · PAY-LIVE ตัวเดียวกับรายการประจำ) · 260-3-5 ตัวจัดทางเข้าของกล่องใบซื้อร่าง
/// </summary>
public class CreditInboxRound260Tests
{
    // ───── 260-3-1 ─────

    [Fact]
    public void เงินรอจัดสรร_ลูกค้าเห็นเมื่อธงใบแจ้งยอดเปิด_มียอด_สิทธิ์ยังใช้ได้_ทิศตรงข้ามไม่เห็น()
    {
        var all = new PortalAccessFlags(true, true, true, true);
        Assert.True(PortalAccessPermissions.CreditShownToCustomer(all, isActive: true, 500m));
        Assert.False(PortalAccessPermissions.CreditShownToCustomer(all with { CanViewStatements = false }, true, 500m));
        Assert.False(PortalAccessPermissions.CreditShownToCustomer(all, isActive: false, 500m));
        Assert.False(PortalAccessPermissions.CreditShownToCustomer(all, true, 0.004m));
        Assert.False(PortalAccessPermissions.CreditShownToCustomer(null, true, 500m));
        // ธงอื่นปิดหมดแต่ใบแจ้งยอดเปิด ⇒ ยังเห็น (ด่านเดียวกับเส้น statement ไม่ใช่ธงรวม)
        Assert.True(PortalAccessPermissions.CreditShownToCustomer(new PortalAccessFlags(false, true, false, false), true, 1m));
    }

    // ───── 260-3-3 ─────

    private static CustomerCreditWorkQueue.AgedRow Aged(string no, int daysAgo, decimal amt, DateTime? noticed = null)
        => new(Guid.NewGuid(), no, DateTime.UtcNow.Date.AddDays(-daysAgo), amt, null, Guid.NewGuid(), noticed);

    [Fact]
    public void กระดิ่งค้างนาน_สรุปเฉพาะแถวที่ยังไม่แจ้ง_ทั้งหมดแจ้งแล้ว_ไม่ส่ง()
    {
        var rows = new[] { Aged("RC-2", 40, 100m), Aged("RC-1", 90, 1_150m), Aged("RC-0", 120, 999m, DateTime.UtcNow.AddDays(-3)) };
        var s = CustomerCreditAgedNotice.Compose(rows)!;
        Assert.Equal(2, s.Count);
        Assert.Equal(1_250m, s.Total);
        Assert.Equal(2, s.PaymentIds.Count);
        Assert.DoesNotContain(rows[2].PaymentId, s.PaymentIds);   // แจ้งแล้ว ⇒ ไม่นับ/ไม่ประทับซ้ำ
        Assert.Contains("2 รายการ", s.Title);
        Assert.Contains("1,250.00", s.Title);
        Assert.Contains("RC-1", s.Message);                       // เก่าสุดที่ยังไม่แจ้ง
        Assert.Null(CustomerCreditAgedNotice.Compose(new[] { rows[2] }));
        Assert.Null(CustomerCreditAgedNotice.Compose(Array.Empty<CustomerCreditWorkQueue.AgedRow>()));
    }

    [Fact]
    public void กระดิ่งค้างนาน_อยู่ในแคตตาล็อก_ค่าตั้งต้นกระดิ่งเจ้าของอย่างเดียว()
    {
        Assert.Contains(NotificationEvents.All, e => e.Key == NotificationEvents.CustomerCreditAged);
        var row = Assert.Single(NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.CustomerCreditAged, companyLineGroupConfigured: true));
        Assert.Equal("Owner", row.RecipientRole);
        Assert.True(row.EnableSystem);
        Assert.False(row.EnableEmail);
        Assert.False(row.EnableLineGroup);
        Assert.Equal(30, CustomerCreditWorkQueue.AgingDays);
    }

    // ───── 260-3-4 ─────

    private static IReadOnlyList<AutopilotSettingsHub.Item> Hub(DocumentPayLinkPolicy.CompanyFacts? pay, bool payLink = true, int slips = 0)
        => AutopilotSettingsHub.Build(new AutopilotSettingsHub.Facts(payLink, false, false, pay, 0, slips, "/pages/payment-intents.html?status=Pending"));

    private static AutopilotReadiness.Item Row(IReadOnlyList<AutopilotReadiness.Item> items, string code) => items.Single(i => i.Code == code);

    [Fact]
    public void การ์ดความพร้อม_พร้อมครบ_ทุกแถวสำคัญ_Ok()
    {
        var items = PaymentChannelReadiness.Build(new(Hub(new DocumentPayLinkPolicy.CompanyFacts(true, new[] { "PromptPay", "Card" }, true), slips: 2),
            GatewayLive: true, SystemBaseUrlConfigured: true, LineOaReady: true, LineOaProblem: null));
        foreach (var code in new[] { "GATEWAY", "PAY-LIVE", "PAY-TRANSFER", "PAY-LINK", "LINE-PAY" })
            Assert.Equal(AutopilotReadiness.Ok, Row(items, code).Verdict);
        Assert.Contains("2", Row(items, "SLIP-QUEUE").Title);
        // PAY-LIVE = แถวตัวเดียวกับความพร้อมของรายการประจำ (ถ้อยคำ/ลิงก์ไม่ drift)
        Assert.Equal(AutopilotReadiness.PayLiveItem(true), Row(items, "PAY-LIVE"));
        var recurring = AutopilotReadiness.Evaluate(new AutopilotReadiness.Facts { SaleSide = true, DocumentType = DocumentType.Invoice, GatewayLive = true });
        Assert.Equal(AutopilotReadiness.PayLiveItem(true), recurring.Items.Single(i => i.Code == "PAY-LIVE"));
    }

    [Fact]
    public void การ์ดความพร้อม_รับโอนอย่างเดียว_ลิงก์ชำระเตือน_LINE_ไม่มีปุ่มชำระ_ไม่แต่งว่าพร้อม()
    {
        var items = PaymentChannelReadiness.Build(new(Hub(new DocumentPayLinkPolicy.CompanyFacts(false, Array.Empty<string>(), true)),
            GatewayLive: false, SystemBaseUrlConfigured: true, LineOaReady: true, LineOaProblem: null));
        Assert.Equal(AutopilotReadiness.Info, Row(items, "GATEWAY").Verdict);        // ไม่บังคับมีผู้ให้บริการ
        Assert.Equal(AutopilotReadiness.Info, Row(items, "PAY-LIVE").Verdict);
        Assert.Equal(AutopilotReadiness.Ok, Row(items, "PAY-TRANSFER").Verdict);
        Assert.Equal(AutopilotReadiness.Warn, Row(items, "PAY-LINK").Verdict);
        Assert.Equal(AutopilotReadiness.PayLinkNoOnlineTitle, Row(items, "PAY-LINK").Title);
        Assert.Equal(AutopilotReadiness.Info, Row(items, "LINE-PAY").Verdict);
        Assert.NotEqual(AutopilotReadiness.Ok, Row(items, "LINE-PAY").Verdict);
    }

    [Fact]
    public void การ์ดความพร้อม_URL_ระบบไม่ตั้ง_LINE_ไม่พร้อม_ตรวจช่องทางไม่ได้()
    {
        var pay = new DocumentPayLinkPolicy.CompanyFacts(true, new[] { "PromptPay" }, false);
        var items = PaymentChannelReadiness.Build(new(Hub(pay), GatewayLive: true, SystemBaseUrlConfigured: false, LineOaReady: false, LineOaProblem: "ยังไม่ได้เปิดใช้ LINE OA ของบริษัท"));
        Assert.Equal(AutopilotReadiness.SystemBaseUrlMissingTitle, Row(items, "PAY-LINK").Title);
        Assert.Equal(AutopilotReadiness.Info, Row(items, "PAY-TRANSFER").Verdict);   // มีออนไลน์แล้ว ⇒ ไม่มีบัญชีรับโอน = ข้อมูล ไม่ใช่คำเตือน
        Assert.Equal("ยังไม่ได้เปิดใช้ LINE OA ของบริษัท", Row(items, "LINE-PAY").Why);
        Assert.Equal(PaymentChannelReadiness.LineSettingsUrl, Row(items, "LINE-PAY").FixUrl);
        // ตรวจช่องทางไม่ได้ (null) ⇒ ⚠️ ไม่ใช่ ✅
        var unknown = PaymentChannelReadiness.Build(new(Hub(null), false, true, true, null));
        Assert.Equal(AutopilotReadiness.Warn, Row(unknown, "GATEWAY").Verdict);
        Assert.Equal(AutopilotReadiness.Warn, Row(unknown, "PAY-TRANSFER").Verdict);
        // ปิดแนบลิงก์ชำระ ⇒ ⓘ พร้อมลิงก์ไปบ้านของค่า
        var off = PaymentChannelReadiness.Build(new(Hub(pay, payLink: false), true, true, true, null));
        Assert.Equal(AutopilotReadiness.Info, Row(off, "PAY-LINK").Verdict);
        Assert.Equal(AutopilotSettingsHub.AutoSendHref, Row(off, "PAY-LINK").FixUrl);
    }

    // ───── 260-3-5 ─────

    [Theory]
    [InlineData(true, true, "{\"sourceChannel\":\"line\"}", null, PurchaseDraftSource.Partner)]   // คู่ค้ามาก่อนเสมอ
    [InlineData(false, true, "{\"sourceChannel\":\"line\",\"lineUserId\":\"U1\"}", null, PurchaseDraftSource.Line)]
    [InlineData(false, true, "{\"lineUserId\":\"U1\"}", "Azure", PurchaseDraftSource.Line)]
    [InlineData(false, true, "{not json", "Azure", PurchaseDraftSource.Ocr)]                     // JSON เสีย ≠ LINE
    [InlineData(false, true, "{\"sourceChannel\":\"partner-api\"}", "Azure", PurchaseDraftSource.Ocr)]
    [InlineData(false, true, null, "EtaxXml", PurchaseDraftSource.EtaxXml)]
    [InlineData(false, true, null, null, PurchaseDraftSource.Ocr)]
    [InlineData(false, false, null, null, PurchaseDraftSource.Manual)]
    public void กล่องใบซื้อร่าง_จัดทางเข้า(bool partner, bool fromScan, string? meta, string? engine, PurchaseDraftSource expected)
        => Assert.Equal(expected, PurchaseDraftInbox.Classify(partner, fromScan, meta, engine));

    [Fact]
    public void กล่องใบซื้อร่าง_ลิงก์งานเฉพาะทาง_สร้างเองไม่มีลิงก์_ชื่อเอนจิน_eTax_ตัวเดียวกัน()
    {
        Assert.Equal("/pages/partners.html#ptDraftBody", PurchaseDraftInbox.Href(PurchaseDraftSource.Partner).Href);
        Assert.Equal("/pages/review-queue.html", PurchaseDraftInbox.Href(PurchaseDraftSource.Line).Href);
        Assert.Null(PurchaseDraftInbox.Href(PurchaseDraftSource.Manual).Href);
        Assert.All(Enum.GetValues<PurchaseDraftSource>(), s => Assert.False(string.IsNullOrWhiteSpace(PurchaseDraftInbox.Label(s))));
        Assert.Equal(EtaxXmlEngine, Accounting.Helpers.OcrEtaxLineNormalizer.EtaxEngine);
    }

    private const string EtaxXmlEngine = OcrHeaderVatEvidence.SignedXmlEngine;
}

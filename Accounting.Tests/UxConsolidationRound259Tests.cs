using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 259-3 (ทีม E UX "รวมบ้าน · ไม่ซ้อน") — ตัวตัดสินแบบ pure (สองทิศ):
/// ลิงก์พอร์ทัลผู้ขาย (บทบาท · สถานะ · เพิกถอน · ผลส่งอีเมล) · ป้าย/เกินกำหนดของพอร์ทัลลูกค้า
/// </summary>
public class UxConsolidationRound259Tests
{
    private static readonly DateTime Now = new(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc);   // 10:00 เวลาไทย

    // ───── E-H2 พอร์ทัลผู้ขาย ─────

    [Theory]
    [InlineData("Vendor")]
    [InlineData("vendor")]
    [InlineData(null)]
    [InlineData("")]
    public void ออกลิงก์ผู้ขายได้(string? role) => Assert.Null(VendorPortalTokenRules.IssueRefusal(role));

    [Fact]
    public void ทิศตรงข้าม_บทบาทลูกค้า_ปฏิเสธพร้อมชี้ไปพอร์ทัลลูกค้า()
    {
        var m = VendorPortalTokenRules.IssueRefusal("Customer");
        Assert.NotNull(m);
        Assert.Contains("พอร์ทัลลูกค้า", m);
    }

    [Fact]
    public void บทบาทที่ไม่รู้จัก_ปฏิเสธ() => Assert.NotNull(VendorPortalTokenRules.IssueRefusal("Admin"));

    [Fact]
    public void สถานะ_ใช้ได้_หมดอายุ_เพิกถอน_เพิกถอนชนะหมดอายุ()
    {
        Assert.Equal(VendorPortalTokenRules.StatusActive, VendorPortalTokenRules.Status(null, Now.AddDays(1), Now));
        Assert.Equal(VendorPortalTokenRules.StatusExpired, VendorPortalTokenRules.Status(null, Now.AddDays(-1), Now));
        Assert.Equal(VendorPortalTokenRules.StatusRevoked, VendorPortalTokenRules.Status(Now.AddDays(-3), Now.AddDays(-1), Now));
        Assert.True(VendorPortalTokenRules.CanRevoke(VendorPortalTokenRules.StatusActive));
        Assert.False(VendorPortalTokenRules.CanRevoke(VendorPortalTokenRules.StatusExpired));
        Assert.False(VendorPortalTokenRules.CanRevoke(VendorPortalTokenRules.StatusRevoked));
    }

    [Fact]
    public void ป้ายบทบาท_ลิงก์ลูกค้าเดิมบอกให้ใช้พอร์ทัลลูกค้า()
    {
        Assert.Equal("ผู้ขาย", VendorPortalTokenRules.RoleLabel("Vendor"));
        Assert.Contains("พอร์ทัลลูกค้า", VendorPortalTokenRules.RoleLabel("Customer"));
    }

    [Fact]
    public void ผลส่งอีเมล_ไม่บอกว่าส่งแล้วถ้าไม่ได้ส่งจริง()
    {
        Assert.Equal("ส่งลิงก์ทางอีเมลแล้ว", VendorPortalTokenRules.InviteNote(true, true, true));
        Assert.DoesNotContain("ส่งลิงก์ทางอีเมลแล้ว", VendorPortalTokenRules.InviteNote(true, false, false));
        Assert.DoesNotContain("ส่งลิงก์ทางอีเมลแล้ว", VendorPortalTokenRules.InviteNote(true, true, false));
        Assert.Contains("ไม่ได้ระบุอีเมล", VendorPortalTokenRules.InviteNote(false, true, false));
    }

    // ───── E 4.2 พอร์ทัลลูกค้า: เกินกำหนด/ป้ายที่เซิร์ฟเวอร์ ─────

    [Fact]
    public void ครบกำหนดวันนี้_ยังไม่เกินกำหนด_เมื่อวาน_เกินแล้ว()
    {
        var todayTh = Now.AddHours(7).Date;
        Assert.False(PortalDocumentStatus.IsOverdue("Approved", 100m, todayTh, Now));
        Assert.Equal("รอชำระ", PortalDocumentStatus.Label("Approved", 100m, todayTh, Now));
        Assert.True(PortalDocumentStatus.IsOverdue("Approved", 100m, todayTh.AddDays(-1), Now));
        Assert.Equal("เกินกำหนด", PortalDocumentStatus.Label("Approved", 100m, todayTh.AddDays(-1), Now));
    }

    [Fact]
    public void ทิศตรงข้าม_ชำระครบ_ยกเลิก_ไม่มีวันครบกำหนด_ไม่เกินกำหนด()
    {
        var past = Now.AddDays(-10);
        Assert.False(PortalDocumentStatus.IsOverdue("Approved", 0m, past, Now));
        Assert.Equal("ชำระแล้ว", PortalDocumentStatus.Label("Approved", 0m, past, Now));
        Assert.False(PortalDocumentStatus.IsOverdue("Voided", 100m, past, Now));
        Assert.Equal("ยกเลิก", PortalDocumentStatus.Label("Voided", 100m, past, Now));
        Assert.False(PortalDocumentStatus.IsOverdue("Approved", 100m, null, Now));
    }

    // ───── E-M2 ศูนย์รวมค่าตั้ง Autopilot ─────

    private static AutopilotSettingsHub.Facts HubFacts(DocumentPayLinkPolicy.CompanyFacts? pay, int rules = 0, int slips = 0, bool wht = false)
        => new(PayLinkDefaultEnabled: true, PostPayEmailReceipt: false, WhtCertReminderEnabled: wht, pay, rules, slips, "/pages/payment-intents.html?status=Pending");

    [Fact]
    public void ศูนย์รวม_สถานะตามความจริง_ลิงก์ไปบ้านของค่า()
    {
        var items = AutopilotSettingsHub.Build(HubFacts(new DocumentPayLinkPolicy.CompanyFacts(true, new[] { "PromptPay", "Card" }, true), rules: 2, slips: 3, wht: true));
        var byKey = items.ToDictionary(i => i.Key);
        Assert.True(byKey["payLink"].On);
        Assert.False(byKey["postPayReceipt"].On);
        Assert.True(byKey["whtReminder"].On);
        Assert.Equal(AutopilotSettingsHub.AutoSendHref, byKey["whtReminder"].Href);
        Assert.Equal("พร้อม — 2 วิธี", byKey["gateway"].StatusLabel);
        Assert.True(byKey["bankTransfer"].On);
        Assert.Equal("รอยืนยัน 3 รายการ", byKey["slipQueue"].StatusLabel);
        Assert.Equal("/pages/payment-intents.html?status=Pending", byKey["slipQueue"].Href);
        Assert.Equal("เปิดอยู่ 2 กฎ", byKey["schedule"].StatusLabel);
        Assert.All(items, i => Assert.StartsWith("/pages/", i.Href));
    }

    [Fact]
    public void ทิศตรงข้าม_ยังไม่ตั้งช่องทาง_บอกยังไม่ได้ตั้ง_ตรวจไม่ได้ไม่แต่งค่า()
    {
        var none = AutopilotSettingsHub.Build(HubFacts(new DocumentPayLinkPolicy.CompanyFacts(false, Array.Empty<string>(), false))).ToDictionary(i => i.Key);
        Assert.Equal("ยังไม่ได้ตั้ง", none["gateway"].StatusLabel);
        Assert.False(none["gateway"].On);
        Assert.Equal("ยังไม่มีบัญชีรับโอน", none["bankTransfer"].StatusLabel);
        Assert.Equal("ไม่มีสลิปรอ", none["slipQueue"].StatusLabel);
        Assert.Equal("ยังไม่มีกฎที่เปิด", none["schedule"].StatusLabel);
        var unknown = AutopilotSettingsHub.Build(HubFacts(null)).ToDictionary(i => i.Key);
        Assert.Null(unknown["gateway"].On);
        Assert.Equal("ตรวจไม่ได้", unknown["bankTransfer"].StatusLabel);
    }

    [Fact]
    public void ชุดสถานะรอผล_ตรงกับIsOpen()
    {
        foreach (var st in Enum.GetValues<Accounting.Models.Enums.PaymentIntentStatus>())
            Assert.Equal(PaymentIntentPolicy.IsOpen(st), PaymentIntentPolicy.OpenStatuses.Contains(st));
    }

    // ───── 3ก-1 เช็กลิสต์เริ่มต้นใช้งาน (เซิร์ฟเวอร์) ─────

    private static GettingStartedChecklist.Facts Gs(bool info = true, int accounts = 40, bool vat = true, decimal rate = 7m, bool email = true,
        bool online = false, bool transfer = true, int docs = 1, bool line = false)
        => new(info, 12, accounts, vat, rate, 1, 3, 0, docs, email, online, transfer, line);

    [Fact]
    public void เริ่มต้นใช้งาน_ครบทุกขั้นบังคับ_100เปอร์เซ็นต์_ขั้นไม่บังคับไม่นับ()
    {
        var r = GettingStartedChecklist.Build(Gs());
        Assert.Equal(100, r.Percent);
        Assert.Equal(r.RequiredTotal, r.RequiredDone);
        Assert.False(r.Steps.Single(x => x.Key == "product").Done);   // ไม่บังคับ — ไม่ดึงเปอร์เซ็นต์ลง
        Assert.True(r.Steps.Single(x => x.Key == "product").Optional);
        Assert.Contains(r.Steps, x => x.Key == "senderEmail" && x.Href.Contains("tab=email"));
        Assert.Contains(r.Steps, x => x.Key == "receiveMoney" && x.Done);
    }

    [Fact]
    public void ทิศตรงข้าม_ขาดข้อมูล_ไม่เสร็จพร้อมคำใบ้_ไม่จดVATบอกเกณฑ์()
    {
        var r = GettingStartedChecklist.Build(Gs(info: false, accounts: 5, vat: false, email: false, transfer: false, docs: 0));
        Assert.True(r.Percent < 100);
        Assert.False(r.Steps.Single(x => x.Key == "company").Done);
        Assert.NotNull(r.Steps.Single(x => x.Key == "coa").Hint);
        var vat = r.Steps.Single(x => x.Key == "vat");
        Assert.False(vat.Done);
        Assert.Contains("1.8 ล้าน", vat.Hint);
        Assert.False(r.Steps.Single(x => x.Key == "receiveMoney").Done);
        Assert.False(r.Steps.Single(x => x.Key == "senderEmail").Done);
        // รับชำระออนไลน์อย่างเดียวก็นับเป็นมีช่องทางรับเงิน (ลิงก์ไปหน้ารับชำระออนไลน์)
        var onlineOnly = GettingStartedChecklist.Build(Gs(online: true, transfer: false)).Steps.Single(x => x.Key == "receiveMoney");
        Assert.True(onlineOnly.Done);
        Assert.Equal("/pages/payment-settings.html", onlineOnly.Href);
        // จด VAT แต่อัตรา 0 = ยังไม่เสร็จ
        Assert.False(GettingStartedChecklist.Build(Gs(rate: 0m)).Steps.Single(x => x.Key == "vat").Done);
    }
}

using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 249 — แจ้งเตือน "รับ/จ่ายชำระเงินแล้ว" (เจ้าของรายงาน 2026-11-06 · บริษัท MANGKORN):
/// กลุ่ม LINE ได้ "💵 รับชำระเงินแล้ว 📄 PI-20260917-0007 💰 1,733.40 บาท 🔗 /pages/documents.html?id=…" —
/// (1) PI เป็นเงินที่เราจ่าย แต่หัวข้อบอกรับ (2) ไม่บอกคู่ค้า/รายการ (3) ลิงก์ path สัมพัทธ์กดไม่ได้
/// ล็อกทั้ง "ใบที่พังกลับมาถูก" (PI ⇒ จ่ายชำระ/ให้ผู้ขาย · ลิงก์เต็ม) และ "ที่ถูกอยู่แล้วไม่ถูกแตะ" (ขาย ⇒ รับชำระ คีย์เดิม · URL เต็มเดิมผ่าน)
/// </summary>
public class PaymentNotificationRound249Tests
{
    private static PaymentNotificationFacts Facts(bool inflow, DocumentType type = DocumentType.PurchaseInvoice,
        string? contact = "บริษัท ผู้ขาย จำกัด", IReadOnlyList<string?>? lines = null, decimal amount = 1733.40m,
        string? currency = "THB", PaymentMethod method = PaymentMethod.BankTransfer, decimal balance = 0m)
        => new(inflow, type, "PI-20260917-0007", contact, lines ?? new string?[] { "ค่าวัตถุดิบ" }, amount, currency, method, balance);

    // ───────── PaymentNotificationText: ฝั่งเงิน ─────────

    [Fact]
    public void จ่ายชำระใบตั้งหนี้ซื้อ_ไม่ขึ้นรับชำระ_คีย์payment_made_และบอกให้ผู้ขาย()
    {
        var n = PaymentNotificationText.Build(Facts(inflow: false));
        Assert.Equal(NotificationEvents.PaymentMade, n.EventKey);
        Assert.Equal("💸 จ่ายชำระเงินแล้ว", n.Title);
        Assert.DoesNotContain("รับชำระ", n.Title);
        Assert.Contains("👤 ให้ บริษัท ผู้ขาย จำกัด", n.Message);
        Assert.Contains("📄 " + DocumentTypeNames.Title(DocumentType.PurchaseInvoice, "th") + " PI-20260917-0007", n.Message);
        Assert.Contains("🧾 ค่าวัตถุดิบ", n.Message);
        Assert.Contains("💰 1,733.40 บาท · โอนเงิน", n.Message);
        Assert.Contains("✅ ชำระครบแล้ว", n.Message);
    }

    [Fact]
    public void ทิศตรงข้าม_รับชำระใบขาย_คงคีย์payment_received_และบอกจากลูกค้า()
    {
        var n = PaymentNotificationText.Build(Facts(inflow: true, type: DocumentType.TaxInvoice, contact: "คุณลูกค้า", method: PaymentMethod.Cash, balance: 500m));
        Assert.Equal(NotificationEvents.PaymentReceived, n.EventKey);
        Assert.Equal("💵 รับชำระเงินแล้ว", n.Title);
        Assert.Contains("👤 จาก คุณลูกค้า", n.Message);
        Assert.DoesNotContain("👤 ให้", n.Message);
        Assert.Contains("💰 1,733.40 บาท · เงินสด", n.Message);
        Assert.Contains("⏳ คงค้าง 500.00 บาท", n.Message);
    }

    [Fact]
    public void ไม่มีชื่อคู่ค้าหรือบรรทัด_ไม่แต่งค่า_ไม่มีบรรทัดนั้น()
    {
        var n = PaymentNotificationText.Build(Facts(inflow: false, contact: "  ", lines: new string?[] { null, " ", "" }));
        Assert.DoesNotContain("👤", n.Message);
        Assert.DoesNotContain("🧾", n.Message);
        Assert.Contains("💰", n.Message);
    }

    private static string Msg(IReadOnlyList<string?>? lines = null, decimal amount = 1733.40m, string? currency = "THB")
        => PaymentNotificationText.Build(Facts(inflow: false, lines: lines, amount: amount, currency: currency)).Message;

    [Fact]
    public void สกุลเงินต่างประเทศ_แสดงรหัสสกุล_ไม่ใช่บาท()
        => Assert.Contains("💰 1,200.00 USD · ", Msg(amount: 1200m, currency: "usd"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("THB")]
    public void ยอดบาท_รูปแบบตายตัว(string? cur)
        => Assert.Contains("💰 1,733.40 บาท · ", Msg(currency: cur));

    // ───────── สรุปรายการ ─────────

    [Fact]
    public void หลายบรรทัด_บรรทัดแรก_และอีกNรายการ()
        => Assert.Contains("\n🧾 ค่าเช่า และอีก 2 รายการ\n", Msg(new string?[] { "ค่าเช่า", "ค่าน้ำ", "ค่าไฟ" }));

    [Fact]
    public void บรรทัดว่างไม่นับ_และยุบขึ้นบรรทัด()
        => Assert.Contains("\n🧾 ค่าบริการ เดือน ต.ค.\n", Msg(new string?[] { "", "ค่าบริการ\nเดือน   ต.ค.", null }));

    [Fact]
    public void คำอธิบายยาว_ตัดพร้อมจุดไข่ปลา_ไม่เกินเพดาน()
    {
        var line = Msg(new string?[] { new string('ก', 200) }).Split('\n').Single(l => l.StartsWith("🧾 "));
        var s = line["🧾 ".Length..];
        Assert.EndsWith("…", s);
        Assert.Equal(PaymentNotificationText.ItemSummaryMaxChars, s.Length);
    }

    [Fact]
    public void ไม่มีบรรทัด_ไม่มีบรรทัดรายการ()
        => Assert.DoesNotContain("🧾", Msg(Array.Empty<string?>()));

    // ───────── PaymentMethodNames ─────────

    [Theory]
    [InlineData(PaymentMethod.Cash, "เงินสด")]
    [InlineData(PaymentMethod.BankTransfer, "โอนเงิน")]
    [InlineData(PaymentMethod.PromptPay, "พร้อมเพย์")]
    [InlineData(PaymentMethod.Other, "อื่น ๆ")]
    public void ชื่อวิธีชำระ_ตารางเดียว(PaymentMethod m, string expected)
        => Assert.Equal(expected, PaymentMethodNames.Thai(m));

    // ───────── NotificationLink ─────────

    [Fact]
    public void pathสัมพัทธ์_ตั้งURLระบบแล้ว_ได้ลิงก์เต็ม()
        => Assert.Equal("https://acc.example.co.th/pages/documents.html?id=eff5",
            NotificationLink.Absolute("https://acc.example.co.th/", "/pages/documents.html?id=eff5"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(SystemBaseUrl.ShippedDefault)]   // ค่าที่มากับซอร์ส ไม่ใช่โดเมนของเรา ≠ ตั้งแล้ว
    [InlineData("ftp://files.example")]
    public void ไม่รู้โดเมนของระบบ_ไม่ใส่ลิงก์_แทนที่จะส่งpathกดไม่ได้(string? origin)
        => Assert.Null(NotificationLink.Absolute(origin, "/pages/documents.html?id=1"));

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("pages/documents.html")]
    [InlineData("/\\evil.example")]
    public void ไม่ใช่pathภายใน_ไม่ใส่ลิงก์(string actionUrl)
        => Assert.Null(NotificationLink.Absolute("https://acc.example.co.th", actionUrl));

    [Fact]
    public void ทิศตรงข้าม_URLเต็มhttpsเดิม_ผ่านไม่ถูกแตะ_แม้ไม่ได้ตั้งURLระบบ()
        => Assert.Equal("https://pay.example.com/x?y=1", NotificationLink.Absolute(null, "https://pay.example.com/x?y=1"));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void ไม่มีActionUrl_คืนnull(string? actionUrl)
        => Assert.Null(NotificationLink.Absolute("https://acc.example.co.th", actionUrl));

    // ───────── คีย์ใหม่: แคตตาล็อก · ค่าตั้งต้น · migration ─────────

    [Fact]
    public void คีย์จ่ายชำระอยู่ในแคตตาล็อก_และได้ค่าตั้งต้นเดียวกับฝั่งรับ()
    {
        Assert.Contains(NotificationEvents.All, e => e.Key == NotificationEvents.PaymentMade);
        Assert.Contains(NotificationEvents.PaymentMade, NotificationAudiencePolicy.MigratedBroadcastKeys);
        Assert.Equal(NotificationAudience.TenantBusiness, NotificationAudiencePolicy.Classify(NotificationEvents.PaymentMade));
        Assert.Equal(NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.PaymentReceived, true),
            NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.PaymentMade, true));
    }

    [Fact]
    public void migrationคัดลอกจากคีย์รับไปคีย์จ่าย_ตรงค่าคงที่_และกันต่อบริษัท()
    {
        Assert.Equal(NotificationEvents.PaymentReceived, NotificationEventSplit.FromKey);
        Assert.Equal(NotificationEvents.PaymentMade, NotificationEventSplit.ToKey);
        Assert.Contains("x.\"CompanyId\" = s.\"CompanyId\"", NotificationEventSplit.SettingsCopySql);
        Assert.Contains("x.\"UserId\" = p.\"UserId\"", NotificationEventSplit.PreferencesCopySql);
        Assert.Contains("ON CONFLICT DO NOTHING", NotificationEventSplit.MigrationSql);
    }
}

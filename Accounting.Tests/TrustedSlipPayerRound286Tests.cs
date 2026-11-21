using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 286 (คำสั่งเจ้าของ · คำตัดสิน 286-1…286-9) — ตัวตัดสินเดียวของ "ผู้จ่ายที่ไว้ใจได้" (<see cref="TrustedSlipPayerRule"/>) · pure · ไม่มี AI
/// </summary>
public class TrustedSlipPayerRound286Tests
{
    private static readonly Guid Company = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Contact = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Recv = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly byte[] Key = TrustedSlipPayerRule.DeriveHmacKey("unit-test-secret-286");

    private static OcrSlipText.SlipParty Payer(string bank = "014", string mask = "xxx-x-x1234-x", string? name = "นาย สมชาย ใจดี",
        OcrSlipText.PartyKind kind = OcrSlipText.PartyKind.BankAccount)
        => new(kind, bank, false, mask, name, null);

    [Fact]
    public void กุญแจ_ต้องครบ_ธนาคาร_เลขอย่างน้อย4หลัก_ชื่อ_ผู้ติดต่อ_บัญชีผู้รับ()
    {
        Assert.NotNull(TrustedSlipPayerRule.KeyFrom(Contact, Payer(), Recv));
        Assert.Null(TrustedSlipPayerRule.KeyFrom(Contact, Payer(mask: "xxx-x-xx234-x"), Recv));   // เห็น 3 หลัก
        Assert.Null(TrustedSlipPayerRule.KeyFrom(Contact, Payer(name: "  "), Recv));
        Assert.Null(TrustedSlipPayerRule.KeyFrom(Contact, Payer(bank: "SCB"), Recv));
        Assert.Null(TrustedSlipPayerRule.KeyFrom(Contact, Payer(kind: OcrSlipText.PartyKind.PromptPay), Recv));
        Assert.Null(TrustedSlipPayerRule.KeyFrom(null, Payer(), Recv));
        Assert.Null(TrustedSlipPayerRule.KeyFrom(Contact, Payer(), null));
    }

    [Fact]
    public void ลายนิ้วมือ_ไม่ขึ้นกับช่องว่าง_ตัวพิมพ์_แต่ต่างทุกชิ้นของกุญแจ_และผูกบริษัท()
    {
        string F(OcrSlipText.SlipParty p, Guid? contact = null, Guid? recv = null, Guid? company = null)
            => TrustedSlipPayerRule.Fingerprint(Key, company ?? Company, TrustedSlipPayerRule.KeyFrom(contact ?? Contact, p, recv ?? Recv)!);
        var baseFp = F(Payer());
        Assert.Equal(baseFp, F(Payer(name: "นาย  สมชาย​ ใจดี ")));                    // ช่องว่าง/zero-width ไม่ใช่ความต่าง
        Assert.Equal(F(Payer(name: "MR JOHN DOE")), F(Payer(name: "Mr John Doe")));         // ตัวพิมพ์
        Assert.NotEqual(baseFp, F(Payer(name: "นาย สมชาย ใจดีมาก")));                       // ไม่ fuzzy
        Assert.NotEqual(baseFp, F(Payer(name: "สมชาย ใจดี")));                              // ไม่ตัดคำนำหน้า
        Assert.NotEqual(baseFp, F(Payer(mask: "xxx-x-x1235-x")));
        Assert.NotEqual(baseFp, F(Payer(bank: "004")));
        Assert.NotEqual(baseFp, F(Payer(), contact: Guid.NewGuid()));
        Assert.NotEqual(baseFp, F(Payer(), recv: Guid.NewGuid()));
        Assert.NotEqual(baseFp, F(Payer(), company: Guid.NewGuid()));
        Assert.Equal(64, baseFp.Length);
        Assert.DoesNotContain("สมชาย", baseFp);
    }

    [Fact]
    public void กุญแจHMAC_ไม่มีความลับ_โยน()
        => Assert.Throws<InvalidOperationException>(() => TrustedSlipPayerRule.DeriveHmacKey(" "));

    private static TrustedSlipPayerRule.Facts F(bool on = true, bool ready = true, bool key = true, TrustedSlipPayerRule.Trust? trust = null, bool noTrust = false,
        decimal amount = 1_000m, decimal today = 0m, decimal cap = 50_000m)
        => new(on, ready, key ? TrustedSlipPayerRule.KeyFrom(Contact, Payer(), Recv) : null, noTrust ? null : trust ?? new TrustedSlipPayerRule.Trust(false, 1_000m),
            amount, today, cap);

    [Fact]
    public void ตัดสิน_ทุกทางที่ไม่ใช้ความไว้ใจ_และทางที่ใช้()
    {
        Assert.Equal(TrustedSlipPayerRule.Codes.SettingOff, TrustedSlipPayerRule.Decide(F(on: false)).Code);
        Assert.Equal(TrustedSlipPayerRule.Codes.NoKey, TrustedSlipPayerRule.Decide(F(ready: false)).Code);
        Assert.Equal(TrustedSlipPayerRule.Codes.NoKey, TrustedSlipPayerRule.Decide(F(key: false)).Code);
        Assert.Equal(TrustedSlipPayerRule.Codes.NotTrusted, TrustedSlipPayerRule.Decide(F(noTrust: true)).Code);
        Assert.Equal(TrustedSlipPayerRule.Codes.Revoked, TrustedSlipPayerRule.Decide(F(trust: new(true, 1_000m))).Code);
        Assert.Equal(TrustedSlipPayerRule.Codes.NoCap, TrustedSlipPayerRule.Decide(F(cap: 0m)).Code);
        // เพดานต่อครั้ง = 1,000 × 1.5 = 1,500 (ขอบรวม)
        Assert.True(TrustedSlipPayerRule.Decide(F(amount: 1_500m)).Provisional);
        Assert.Equal(TrustedSlipPayerRule.Codes.OverMax, TrustedSlipPayerRule.Decide(F(amount: 1_500.01m)).Code);
        // เพดานต่อวัน (รวมรายการวันนี้ของกุญแจเดียวกัน)
        Assert.True(TrustedSlipPayerRule.Decide(F(amount: 1_000m, today: 1_000m, cap: 2_000m)).Provisional);
        Assert.Equal(TrustedSlipPayerRule.Codes.OverDaily, TrustedSlipPayerRule.Decide(F(amount: 1_000m, today: 1_000.01m, cap: 2_000m)).Code);
        var ok = TrustedSlipPayerRule.Decide(F());
        Assert.True(ok.Provisional);
        Assert.False(ok.UsedAi);
        Assert.Contains("ล่วงหน้า", ok.Reason);
    }

    [Fact]
    public void ฝ่ายค้านHIGH3_ยอดค้างยืนยันต่อผู้ติดต่อและทั้งบริษัท_ผู้ติดต่อถูกล็อก_เกินแล้วหน้ายืนยัน()
    {
        var baseF = F() with { ContactMaxConfirmed = 1_000m, CompanyOutstandingCap = 100_000m };
        Assert.True(TrustedSlipPayerRule.Decide(baseF).Provisional);                                                    // 0 + 1,000 ≤ 1,000
        Assert.Equal(TrustedSlipPayerRule.Codes.OverOutstanding, TrustedSlipPayerRule.Decide(baseF with { ContactOutstanding = 0.01m }).Code);
        Assert.Equal(TrustedSlipPayerRule.Codes.OverOutstanding, TrustedSlipPayerRule.Decide(baseF with { CompanyOutstanding = 99_000.01m }).Code);
        Assert.True(TrustedSlipPayerRule.Decide(baseF with { CompanyOutstanding = 99_000m }).Provisional);              // ขอบรวม
        Assert.Equal(TrustedSlipPayerRule.Codes.ContactBlocked, TrustedSlipPayerRule.Decide(baseF with { ContactBlocked = true }).Code);
        Assert.Equal(TrustedSlipPayerRule.Codes.NoCap, TrustedSlipPayerRule.Decide(baseF with { CompanyOutstandingCap = 0m }).Code);
        Assert.Equal("รับล่วงหน้า — รอยืนยันจากธนาคาร", TrustedSlipPayerRule.StatusLabel(DateTime.UtcNow, null, null));
        Assert.Null(TrustedSlipPayerRule.StatusLabel(null, null, null));
        Assert.True(TrustedSlipPayerRule.Unverified(DateTime.UtcNow, null));
        Assert.False(TrustedSlipPayerRule.Unverified(DateTime.UtcNow, DateTime.UtcNow));
    }

    [Fact]
    public void เลขอ้างอิงรายการชำระ_ถอดกลับตรงตัวเดียวกับตัวเขียน()
    {
        var id = Guid.NewGuid();
        Assert.True(GatewayPaymentRecord.TryParseIntentId(GatewayPaymentRecord.Reference(id), out var back));
        Assert.Equal(id, back);
        Assert.False(GatewayPaymentRecord.TryParseIntentId("BANK:" + id.ToString("N"), out _));
        Assert.False(GatewayPaymentRecord.TryParseIntentId(null, out _));
    }

    [Fact]
    public void กำหนดตรวจย้อน_นับวันทำการ_ข้ามเสาร์อาทิตย์และวันหยุด_สิ้นวันไทย()
    {
        var fri = new DateTime(2026, 11, 20);   // ศุกร์
        // 3 วันทำการหลังศุกร์ = จันทร์ อังคาร พุธ ⇒ สิ้นวันพุธ 25 (ไทย) = 25 17:00 UTC
        Assert.Equal(new DateTime(2026, 11, 25, 17, 0, 0), TrustedSlipPayerRule.ReviewDueUtc(fri, 3, null));
        var holidays = BusinessDayCalendar.ToSet(new[] { new DateTime(2026, 11, 24) });
        Assert.Equal(new DateTime(2026, 11, 26, 17, 0, 0), TrustedSlipPayerRule.ReviewDueUtc(fri, 3, holidays));
        Assert.Equal(TrustedSlipPayerRule.ReviewDueUtc(fri, TrustedSlipPayerRule.MaxReviewBusinessDays, null), TrustedSlipPayerRule.ReviewDueUtc(fri, 99, null));
    }

    [Fact]
    public void ค่าตั้ง_ช่วงผิดปฏิเสธพร้อมเหตุ_ช่วงถูกผ่าน()
    {
        Assert.Null(TrustedSlipPayerRule.SettingsProblem(50_000m, 3));
        Assert.Null(TrustedSlipPayerRule.SettingsProblem(null, null));
        Assert.NotNull(TrustedSlipPayerRule.SettingsProblem(-1m, null));
        Assert.NotNull(TrustedSlipPayerRule.SettingsProblem(null, 0));
        Assert.NotNull(TrustedSlipPayerRule.SettingsProblem(null, 6));   // ฝ่ายค้าน HIGH-3: สูงสุด 5 วันทำการ
        Assert.Null(TrustedSlipPayerRule.SettingsProblem(null, 5));
        Assert.NotNull(TrustedSlipPayerRule.SettingsProblem(null, null, -1m));
    }
}

using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 252 WP-13 ฝ่ายค้าน — ทะเบียนสินทรัพย์:
/// MED-HIGH-1 รหัสอัตโนมัติ + กดบันทึกซ้ำ ⇒ ต้องส่งครั้งเดียว (ปุ่มล็อก + Idempotency-Key ต่อการเปิดฟอร์ม ผ่าน IdempotencyMiddleware) ·
/// MED-3 งวดค่าเสื่อม: เดือน 1–12 · ไม่เกินเดือนปัจจุบันเวลาไทย · วิธียอดลดลงต้องลงตามลำดับ
/// </summary>
public class FixedAssetRound252AdversarialTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);   // 10:00 น. ไทย

    [Theory]
    [InlineData(2026, 11)]   // เดือนปัจจุบัน
    [InlineData(2026, 1)]    // ย้อนหลัง
    [InlineData(2025, 12)]
    public void Current_or_past_period_is_allowed(int y, int m)
        => Assert.Null(DepreciationPeriodPolicy.PeriodProblem(y, m, Now));

    [Theory]
    [InlineData(2026, 12)]   // อนาคต ⇒ ห้ามลงล่วงหน้า
    [InlineData(2027, 1)]
    [InlineData(2026, 13)]   // เดิม new DateTime ล้มเป็น 500
    [InlineData(2026, 0)]
    [InlineData(1999, 5)]
    public void Future_or_invalid_period_is_refused_with_thai_reason(int y, int m)
        => Assert.False(string.IsNullOrWhiteSpace(DepreciationPeriodPolicy.PeriodProblem(y, m, Now)));

    [Fact]
    public void Bangkok_month_boundary_decides_not_utc()
    {
        // 31 ต.ค. 18:00Z = 1 พ.ย. 01:00 น. ไทย ⇒ งวด พ.ย. ใช้ได้แล้ว
        var lateUtc = new DateTime(2026, 10, 31, 18, 0, 0, DateTimeKind.Utc);
        Assert.Null(DepreciationPeriodPolicy.PeriodProblem(2026, 11, lateUtc));
        Assert.NotNull(DepreciationPeriodPolicy.PeriodProblem(2026, 11, lateUtc.AddHours(-2)));   // 31 ต.ค. 23:00 น. ไทย
    }

    [Fact]
    public void Only_declining_methods_are_order_sensitive()
    {
        Assert.True(DepreciationPeriodPolicy.IsOrderSensitive(DepreciationMethod.DecliningBalance));
        Assert.True(DepreciationPeriodPolicy.IsOrderSensitive(DepreciationMethod.DoubleDecliningBalance));
        Assert.False(DepreciationPeriodPolicy.IsOrderSensitive(DepreciationMethod.StraightLine));
        Assert.True(DepreciationPeriodPolicy.IsBefore(2026, 2, 2026, 3));
        Assert.False(DepreciationPeriodPolicy.IsBefore(2026, 3, 2026, 3));
    }

    [Fact]
    public void Asset_create_endpoint_goes_through_idempotency_middleware()
    {
        // ไม่ใช่เส้น credential ⇒ middleware เก็บ/replay คีย์ · คีย์ UUID ผ่านรูป
        Assert.False(IdempotencyKeyPolicy.IsCredentialPath("/api/companies/00000000-0000-0000-0000-000000000001/fixedasset"));
        Assert.NotNull(IdempotencyKeyPolicy.Normalize(Guid.NewGuid().ToString()).Key);
    }

    [Fact]
    public void Asset_form_sends_one_idempotency_key_per_open_and_locks_save_button()
    {
        var root = RepoRoot();
        var page = File.ReadAllText(Path.Combine(root, "Accounting", "wwwroot", "pages", "fixed-assets.html"));
        var api = File.ReadAllText(Path.Combine(root, "Accounting", "wwwroot", "js", "api.js"));
        Assert.Contains("/js/submit-once.js", page);
        Assert.Contains("SubmitOnce.run(", page);                   // ปุ่มล็อกระหว่างส่ง (คลิก/Enter ซ้ำ = ไม่ส่งซ้ำ)
        Assert.Contains("SubmitOnce.key(", page);                   // คีย์เดิมตอนลองใหม่
        Assert.Contains("SubmitOnce.reset(", page);                 // เปิดฟอร์มใหม่ = คีย์ใหม่
        Assert.Matches(@"createAsset:\s*\(d,\s*idemKey\)[^\n]*Idempotency-Key", api);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }
}

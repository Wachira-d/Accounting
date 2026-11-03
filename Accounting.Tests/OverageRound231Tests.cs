using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 231 (erp-review/2026-11-03/LICENSE_USAGE_OVERAGE_AUDIT.md · คำตัดสินข้อ 231-1..7) — ส่วนเกินโควตา + เกณฑ์เตือนเดียว + ประวัติการใช้งาน:
/// ตัวตัดสิน <see cref="OveragePolicy"/> (ล็อกยังล็อก · เปิดแล้วผ่านและคิด · ถึง cap กลับไปล็อก · ไม่มีราคา = ล็อกพร้อมเหตุ · ถูกล็อกเพราะค้าง) ·
/// <see cref="OverageRateResolver"/> (แพ็กเกจชนะ · ช่วงวัน) · ขั้น 1 ค้างชำระ · <see cref="QuotaWarnPolicy"/> (เกณฑ์ตั้งได้) · <see cref="UsageHistoryRule"/> · ข้อความเพดาน "ไม่จำกัด" ที่เดียว
/// <para>ทุกด่านมีทั้งทิศ "ถูกบล็อก" และทิศตรงข้าม "ไม่ถูกแตะ" (F2 ข้อ 6/8)</para>
/// </summary>
public class OverageRound231Tests
{
    private static readonly DateTime Now = new(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc);   // 15/11/2569 10:00 ไทย
    private static readonly Guid Pro = Guid.NewGuid();

    private static OverageRateRow Rate(decimal price, Guid? plan = null, int unitSize = 1, DateTime? from = null, DateTime? to = null, bool active = true)
        => new(Guid.NewGuid(), OverageResources.Documents, plan, unitSize, price, from ?? Now.AddDays(-30), to, active);

    private static OveragePolicyView Allow(decimal? cap = null, string? csv = null, DateTime? locked = null)
        => new(OverageMode.AllowOverage, cap, csv, locked);

    // ═════════ OveragePolicy.Decide — ตารางพฤติกรรม §B.4 ═════════

    [Fact]
    public void ค่าตั้งต้น_HardLock_ไม่ผ่าน_พร้อมเหตุและรหัส()
    {
        var d = OveragePolicy.Decide(OverageResources.Documents, 1, OveragePolicyView.None, Rate(5m), 0m, Now);
        Assert.False(d.Allowed);
        Assert.Equal(OverageResources.RuleNotEnabled, d.RuleCode);
        Assert.Contains("หยุดส่วนที่เกิน", d.Reason);
        Assert.Contains("฿5.00", d.Reason);   // บอกราคาที่จะได้ถ้าเปิด (ทางไปต่อ)
    }

    [Fact]
    public void เปิดแล้ว_มีราคา_ผ่านและคิดตามหน่วย()
    {
        var d = OveragePolicy.Decide(OverageResources.Documents, 1, Allow(), Rate(5m), 0m, Now);
        Assert.True(d.Allowed);
        Assert.Equal(1, d.Units);
        Assert.Equal(5m, d.UnitPrice);
        Assert.Equal(5m, d.Amount);
        Assert.Null(d.RuleCode);
    }

    [Fact]
    public void ขนาดหน่วยมากกว่า1_ปัดขึ้น_ยอดปัดสองตำแหน่ง()
    {
        var d = OveragePolicy.Decide(OverageResources.StorageGb, 23, Allow(), new OverageRateRow(Guid.NewGuid(), OverageResources.StorageGb, null, 10, 33.333m, Now.AddDays(-1), null, true), 0m, Now);
        Assert.True(d.Allowed);
        Assert.Equal(3, d.Units);          // 23 GB / 10 = 3 หน่วย
        Assert.Equal(100.00m, d.Amount);   // 3 × 33.333 = 99.999 ⇒ 100.00 (AwayFromZero)
    }

    [Fact]
    public void เปิดแต่ไม่มีราคา_ไม่ผ่าน_ไม่คิดศูนย์()
    {
        Assert.Equal(OverageResources.RuleNoRate, OveragePolicy.Decide(OverageResources.Documents, 1, Allow(), null, 0m, Now).RuleCode);
        Assert.Equal(OverageResources.RuleNoRate, OveragePolicy.Decide(OverageResources.Documents, 1, Allow(), Rate(0m), 0m, Now).RuleCode);
        Assert.Equal(OverageResources.RuleNoRate, OveragePolicy.Decide(OverageResources.Documents, 1, Allow(), Rate(5m, to: Now.AddDays(-1)), 0m, Now).RuleCode);
    }

    [Fact]
    public void ถึงเพดานเงิน_กลับไปล็อก_ยังไม่ถึงผ่าน()
    {
        var cap = OveragePolicy.Decide(OverageResources.Documents, 1, Allow(cap: 100m), Rate(5m), 96m, Now);
        Assert.False(cap.Allowed);
        Assert.Equal(OverageResources.RuleCap, cap.RuleCode);
        var ok = OveragePolicy.Decide(OverageResources.Documents, 1, Allow(cap: 100m), Rate(5m), 95m, Now);
        Assert.True(ok.Allowed);   // 95 + 5 = 100 พอดี ไม่เกิน
    }

    [Fact]
    public void ถูกล็อกเพราะค้างชำระ_ไม่ผ่านแม้เปิดและมีราคา()
    {
        var d = OveragePolicy.Decide(OverageResources.Documents, 1, Allow(locked: Now.AddDays(-2)), Rate(5m), 0m, Now);
        Assert.False(d.Allowed);
        Assert.Equal(OverageResources.RuleLocked, d.RuleCode);
        Assert.Contains("ชำระ", d.Reason);
    }

    [Fact]
    public void เลือกบางทรัพยากร_ที่ไม่เลือกไม่ผ่าน_ที่เลือกผ่าน()
    {
        var policy = Allow(csv: "documents");
        Assert.True(OveragePolicy.Decide(OverageResources.Documents, 1, policy, Rate(5m), 0m, Now).Allowed);
        var ocr = OveragePolicy.Decide(OverageResources.OcrPages, 1, policy, new OverageRateRow(Guid.NewGuid(), OverageResources.OcrPages, null, 1, 2m, Now.AddDays(-1), null, true), 0m, Now);
        Assert.False(ocr.Allowed);
        Assert.Equal(OverageResources.RuleNotEnabled, ocr.RuleCode);
    }

    [Fact]
    public void ทรัพยากรนอกรายการ_ที่นั่ง_ไม่คิดส่วนเกินเสมอ()
    {
        var d = OveragePolicy.Decide(QuotaResources.Users, 1, Allow(), null, 0m, Now);
        Assert.False(d.Allowed);
        Assert.Equal(OverageResources.RuleNotEnabled, d.RuleCode);
        Assert.False(OverageResources.IsKnown(QuotaResources.Users));
        Assert.False(OverageResources.IsKnown(QuotaResources.CmsSites));
    }

    // ═════════ OverageRateResolver ═════════

    [Fact]
    public void ราคาแพ็กเกจชนะราคาทั่วไป_ไม่มีแพ็กเกจใช้ทั่วไป()
    {
        var rates = new[] { Rate(5m), Rate(3m, Pro) };
        Assert.Equal(3m, OverageRateResolver.Pick(rates, OverageResources.Documents, Pro, Now)!.UnitPrice);
        Assert.Equal(5m, OverageRateResolver.Pick(rates, OverageResources.Documents, Guid.NewGuid(), Now)!.UnitPrice);
        Assert.Equal(5m, OverageRateResolver.Pick(rates, OverageResources.Documents, null, Now)!.UnitPrice);
    }

    [Fact]
    public void แถวหมดช่วง_ปิด_ไม่ถูกเลือก_แถวใหม่สุดชนะ()
    {
        var old = Rate(4m, from: Now.AddDays(-60), to: Now.AddDays(-30));
        var inactive = Rate(9m, active: false);
        var cur = Rate(5m, from: Now.AddDays(-30));
        var newer = Rate(6m, from: Now.AddDays(-1));
        Assert.Equal(6m, OverageRateResolver.Pick(new[] { old, inactive, cur, newer }, OverageResources.Documents, null, Now)!.UnitPrice);
        Assert.Null(OverageRateResolver.Pick(new[] { old, inactive }, OverageResources.Documents, null, Now));
        Assert.Null(OverageRateResolver.Pick(new[] { Rate(5m, from: Now.AddDays(1)) }, OverageResources.Documents, null, Now));   // ยังไม่ถึงวันมีผล
    }

    // ═════════ ขั้น 1 ค้างชำระ (คำตัดสินข้อ 231-4) ═════════

    private static DunningInvoice Inv(int daysPastDue, decimal balance = 120m, bool excluded = false)
        => new(Guid.NewGuid(), "USAGE-1", Now.AddDays(-daysPastDue), Now.AddDays(-daysPastDue - 1), balance, excluded);

    [Fact]
    public void เกินวันครบกำหนด_ล็อกทันที_ยังไม่ถึงกำหนดไม่ทำอะไร()
    {
        // ฝ่ายค้าน N1: ครบกำหนด = วันออก + grace อยู่แล้ว ⇒ ล็อกเมื่อเกินกำหนด 1 วัน (ไม่ใช่รอ 2×grace)
        Assert.Equal(OveragePolicy.UnpaidLockAction.Lock, OveragePolicy.UnpaidLock(new[] { Inv(1) }, Now, currentlyLocked: false));
        Assert.Equal(OveragePolicy.UnpaidLockAction.Lock, OveragePolicy.UnpaidLock(new[] { Inv(8) }, Now, currentlyLocked: false));
        Assert.Equal(OveragePolicy.UnpaidLockAction.None, OveragePolicy.UnpaidLock(new[] { Inv(0) }, Now, currentlyLocked: false));    // วันครบกำหนดพอดี ยังไม่เกิน
        Assert.Equal(OveragePolicy.UnpaidLockAction.None, OveragePolicy.UnpaidLock(new[] { Inv(-3) }, Now, currentlyLocked: false));   // ยังไม่ถึงกำหนด
        Assert.Equal(OveragePolicy.UnpaidLockAction.None, OveragePolicy.UnpaidLock(new[] { Inv(8) }, Now, currentlyLocked: true));     // ล็อกอยู่แล้ว
    }

    [Fact]
    public void ชำระครบ_ปลดล็อก_ใบที่สลิปรอตรวจไม่นับ()
    {
        Assert.Equal(OveragePolicy.UnpaidLockAction.Unlock, OveragePolicy.UnpaidLock(Array.Empty<DunningInvoice>(), Now, currentlyLocked: true));
        Assert.Equal(OveragePolicy.UnpaidLockAction.Unlock, OveragePolicy.UnpaidLock(new[] { Inv(30, excluded: true), Inv(30, balance: 0m) }, Now, currentlyLocked: true));
        Assert.Equal(OveragePolicy.UnpaidLockAction.Unlock, OveragePolicy.UnpaidLock(new[] { Inv(-2) }, Now, currentlyLocked: true));   // มีใบใหม่ที่ยังไม่ถึงกำหนด ⇒ ปลดได้
        Assert.Equal(OveragePolicy.UnpaidLockAction.None, OveragePolicy.UnpaidLock(Array.Empty<DunningInvoice>(), Now, currentlyLocked: false));
    }

    [Fact]
    public void LockOverageOnly_ใบUSAGEไม่เข้าขั้นระงับ_LockThenDunningนับทุกใบ()
    {
        var all = new List<DunningInvoice> { Inv(20), Inv(20) };
        var refs = new List<string?> { "USAGE-2026-10-abc", "RENEW-xyz" };
        var only = OveragePolicy.ForSuspension(all, refs, OverageUnpaidAction.LockOverageOnly);
        Assert.Single(only);
        Assert.Same(all[1], only[0]);
        Assert.Same(all, OveragePolicy.ForSuspension(all, refs, OverageUnpaidAction.LockThenDunning));
    }

    // ═════════ ValidateTenantUpdate ═════════

    [Fact]
    public void เปิดต้องยอมรับฉบับปัจจุบัน_เพดานติดลบไม่ได้_ปิดไม่ต้องยอมรับ()
    {
        Assert.NotNull(OveragePolicy.ValidateTenantUpdate(OverageMode.AllowOverage, null, null, accepted: false, OveragePolicy.PolicyVersion));
        Assert.NotNull(OveragePolicy.ValidateTenantUpdate(OverageMode.AllowOverage, null, null, accepted: true, "2020-01"));
        Assert.Null(OveragePolicy.ValidateTenantUpdate(OverageMode.AllowOverage, 500m, new[] { "documents" }, accepted: true, OveragePolicy.PolicyVersion));
        Assert.NotNull(OveragePolicy.ValidateTenantUpdate(OverageMode.HardLock, -1m, null, accepted: false, null));
        Assert.Null(OveragePolicy.ValidateTenantUpdate(OverageMode.HardLock, null, null, accepted: false, null));
        Assert.NotNull(OveragePolicy.ValidateTenantUpdate(OverageMode.AllowOverage, null, new[] { "users" }, accepted: true, OveragePolicy.PolicyVersion));
    }

    // ═════════ พื้นที่ GB-เดือน ═════════

    [Fact]
    public void พื้นที่เกินเฉลี่ยรายวัน_ปัดขึ้น_ไม่เกินหรือไม่จำกัดเป็นศูนย์()
    {
        const long gb = 1024L * 1024L * 1024L;
        var samples = new List<(long, long?)>();
        for (var i = 0; i < 30; i++) samples.Add((i < 15 ? 12 * gb : 13 * gb, 10 * gb));   // เกิน 2 GB 15 วัน + 3 GB 15 วัน = เฉลี่ย 2.5 ⇒ 3
        Assert.Equal(3, OveragePolicy.StorageOverageGbMonth(samples, 30));
        Assert.Equal(0, OveragePolicy.StorageOverageGbMonth(new List<(long, long?)> { (5 * gb, 10 * gb), (9 * gb, 10 * gb) }, 30));
        Assert.Equal(0, OveragePolicy.StorageOverageGbMonth(new List<(long, long?)> { (50 * gb, null), (50 * gb, QuotaResources.UnlimitedBytes) }, 30));
        Assert.Equal(0, OveragePolicy.StorageOverageGbMonth(new List<(long, long?)>(), 30));
        // ฝ่ายค้าน S5: เปิดส่วนเกิน 6 วันจาก 30 (ตัวอย่างมีแค่ 6) เกิน 10 GB ทุกวัน ⇒ 60/30 = 2 GB-เดือน (ไม่ใช่ 10)
        var six = Enumerable.Range(0, 6).Select(_ => (20 * gb, (long?)(10 * gb))).ToList();
        Assert.Equal(2, OveragePolicy.StorageOverageGbMonth(six, 30));
        Assert.Equal(10, OveragePolicy.StorageOverageGbMonth(six, 6));   // (เทียบ) หารด้วยจำนวนตัวอย่างจะได้ 10 — ผิดทิศ
    }

    // ═════════ QuotaWarnPolicy — เกณฑ์เดียว ตั้งได้ (คำตัดสินข้อ 231-5) ═════════

    [Fact]
    public void เกณฑ์ตั้งต้น_80_90_100_และอ่านค่าเพี้ยนกลับเป็นตั้งต้น()
    {
        Assert.Equal(new[] { 80, 90, 100 }, QuotaWarnPolicy.Default);
        Assert.Equal(new[] { 80, 90, 100 }, QuotaWarnPolicy.Parse("abc"));
        Assert.Equal(new[] { 50, 100 }, QuotaWarnPolicy.Parse("100, 50, 50, 0, 101"));
        Assert.Null(QuotaWarnPolicy.Validate("70,90,100", out var norm));
        Assert.Equal("70,90,100", norm);
        Assert.NotNull(QuotaWarnPolicy.Validate("", out _));
        Assert.NotNull(QuotaWarnPolicy.Validate("80,x", out _));
        Assert.NotNull(QuotaWarnPolicy.Validate("10,20,30,40,50,60", out _));
    }

    [Fact]
    public void ระดับและเกณฑ์ที่ข้าม_ตามเกณฑ์ที่แอดมินตั้ง()
    {
        var p = QuotaWarnPolicy.Parse("70,90,100");
        // ข้ามจาก 0 = เกณฑ์สูงสุดที่ใช้ไปถึง
        Assert.Equal(0, QuotaWarnPolicy.Crossed(0, 100, 69, 100, p));
        Assert.Equal(70, QuotaWarnPolicy.Crossed(0, 100, 70, 100, p));
        Assert.Equal(90, QuotaWarnPolicy.Crossed(0, 100, 95, 100, p));
        Assert.Equal(100, QuotaWarnPolicy.Crossed(0, 100, 100, 100, p));
        Assert.Equal(100, QuotaWarnPolicy.Crossed(0, 100, 150, 100, p));
        Assert.Equal(0, QuotaWarnPolicy.Crossed(0, null, 150, null, p));   // ไม่จำกัด
        Assert.Equal(100, QuotaWarnPolicy.Crossed(0, 100, 100, 100, QuotaWarnPolicy.Parse("70,90")));   // ฝ่ายค้าน N4: เต็ม = 100 แม้รายการไม่มี 100 (ไม่ใช่ 90)
        Assert.Equal(90, QuotaWarnPolicy.Crossed(0, 100, 95, 100, QuotaWarnPolicy.Parse("70,90")));
        Assert.Equal(1, QuotaWarnPolicy.Severity(70, 100, p));
        Assert.Equal(0, QuotaWarnPolicy.Severity(70, 100));   // ค่าตั้งต้น 80 ⇒ 70% ยังปกติ (สัญญาเดิมของเอกสาร)
        Assert.Equal(2, QuotaWarnPolicy.Severity(100, 100));
        Assert.Equal(0, QuotaWarnPolicy.Severity(5, null));
    }

    [Fact]
    public void ข้ามครั้งเดียวต่อเกณฑ์_และงานรายวันแจ้งเฉพาะเกณฑ์ที่สูงกว่าที่เคยแจ้ง()
    {
        Assert.Equal(80, QuotaWarnPolicy.Crossed(79, 100, 80, 100));
        Assert.Equal(0, QuotaWarnPolicy.Crossed(80, 100, 85, 100));      // ยังเกณฑ์เดิม
        Assert.Equal(100, QuotaWarnPolicy.Crossed(89, 100, 100, 100));   // กระโดดข้าม 90 ⇒ แจ้งเกณฑ์สูงสุดที่ถึง
        Assert.Equal(0, QuotaWarnPolicy.Crossed(0, 0, 5, 0));            // ไม่จำกัด
        Assert.Equal(90, QuotaWarnPolicy.DueAfterWarned(92, 100, warnedPercent: 80));
        Assert.Equal(0, QuotaWarnPolicy.DueAfterWarned(92, 100, warnedPercent: 90));
        Assert.Equal(0, QuotaWarnPolicy.DueAfterWarned(50, 100, warnedPercent: 90));   // ลดลงแล้ว ไม่แจ้งซ้ำ
        Assert.Equal(80, DocumentQuotaPolicy.CrossedWarnLevel(70, 100, 80, 100, QuotaWarnPolicy.Default));
        Assert.Equal(0, DocumentQuotaPolicy.CrossedWarnLevel(70, 0, 80, 0, QuotaWarnPolicy.Default));
    }

    [Fact]
    public void ตัวตัดสินเดิมยังให้สัญญาเดิม_80เป็นระดับ1_เต็มระดับ2()
    {
        Assert.Equal(0, DocumentQuotaPolicy.WarnLevel(79, 100));
        Assert.Equal(1, DocumentQuotaPolicy.WarnLevel(80, 100));
        Assert.Equal(2, DocumentQuotaPolicy.WarnLevel(100, 100));
        Assert.Equal(0, DocumentQuotaPolicy.WarnLevel(500, 0));
        Assert.Equal(1, QuotaLimitPolicy.WarnLevel(8, 10));
        Assert.Equal(2, QuotaLimitPolicy.WarnLevel(10, 10));
        Assert.Equal(0, QuotaLimitPolicy.WarnLevel(10, null));
        Assert.Equal(1, QuotaLimitPolicy.WarnLevel(7, 10, QuotaWarnPolicy.Parse("70,100")));
    }

    // ═════════ UsageBarRule — "ไม่จำกัด" ที่เดียว (P0 จ) ═════════

    [Fact]
    public void ข้อความเพดานของแพ็กเกจ_คิดที่เซิร์ฟเวอร์_เกณฑ์เดิมของJS()
    {
        var t = UsageBarRule.LimitTexts(999, 99, 99999, 500, -1, QuotaResources.UnlimitedBytes, null);
        Assert.Equal("ไม่จำกัด", t["users"]); Assert.Equal("ไม่จำกัด", t["companies"]); Assert.Equal("ไม่จำกัด", t["docs"]);
        Assert.Equal("500", t["journals"]); Assert.Equal("ไม่จำกัด", t["ocr"]); Assert.Equal("ไม่จำกัด", t["storage"]); Assert.Equal("ไม่จำกัด", t["messages"]);
        var f = UsageBarRule.UnlimitedFlags(5, 1, 300, 500, 200, 10L * 1024 * 1024 * 1024, 100);
        Assert.All(f.Values, v => Assert.False(v));
        var t2 = UsageBarRule.LimitTexts(5, 1, 300, 500, 200, 10L * 1024 * 1024 * 1024, 100);
        Assert.Equal("5", t2["users"]); Assert.Equal("10.0 GB", t2["storage"]); Assert.Equal("100", t2["messages"]);
    }

    [Fact]
    public void หลอดใช้เกณฑ์ของแอดมิน_และหมายเหตุโบนัส()
    {
        var b = UsageBarRule.Documents(70, 100, QuotaWarnPolicy.Parse("70,100"), "รวมโบนัสคงเหลือ 20");
        Assert.Equal(1, b.Level);
        Assert.Equal("รวมโบนัสคงเหลือ 20", b.Extra);
        Assert.Equal(0, UsageBarRule.Documents(70, 100).Level);
        Assert.Equal(2, UsageBarRule.Users(5, 5).Level);
        Assert.True(UsageBarRule.Users(3, -1).Unlimited);
        Assert.Equal(0, UsageBarRule.Users(3, -1).Level);
    }

    // ═════════ UsageHistoryRule ═════════

    [Fact]
    public void งวดปิด_จากวันรีเซ็ต_รายการงวดย้อนหลัง_ป้ายไทย()
    {
        Assert.Equal("2026-10", UsageHistoryRule.ClosingPeriod(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-08", UsageHistoryRule.ClosingPeriod(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)));   // ค้าง 2 เดือนก็ยังเป็นเดือนที่ตัวนับเริ่ม
        var periods = UsageHistoryRule.Periods(Now, 3);
        Assert.Equal(new[] { "2026-11", "2026-10", "2026-09" }, periods);
        Assert.Equal(UsageHistoryRule.RetentionMonths, UsageHistoryRule.Periods(Now, 99).Count);
        Assert.Equal("พ.ย. 2569", UsageHistoryRule.Label("2026-11"));
        Assert.False(UsageHistoryRule.TryParsePeriod("2026-13", out _, out _));
        var (start, end) = UsageHistoryRule.PeriodRangeUtc("2026-11");
        Assert.Equal(new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc), end);
    }

    [Fact]
    public void ชนิดทรัพยากร_Capacityเก็บสูงสุด_เพดานที่เก็บ_ทรัพยากรที่งานรายวันเตือน()
    {
        Assert.True(UsageHistoryRule.IsCapacity(QuotaResources.Users));
        Assert.False(UsageHistoryRule.IsCapacity(QuotaResources.OcrPagesMonth));
        Assert.False(UsageHistoryRule.IsCapacity(OverageResources.Documents));
        Assert.Null(UsageHistoryRule.StoredLimit(99999, unlimited: true));
        Assert.Null(UsageHistoryRule.StoredLimit(0, unlimited: false));
        Assert.Equal(300, UsageHistoryRule.StoredLimit(300, unlimited: false));
        Assert.True(UsageHistoryRule.DailyWarnApplies(QuotaResources.Users));
        Assert.False(UsageHistoryRule.DailyWarnApplies(OverageResources.Documents));      // แจ้งแบบทันทีอยู่แล้ว
        Assert.False(UsageHistoryRule.DailyWarnApplies(OverageResources.NotifyMessages));
        Assert.False(UsageHistoryRule.DailyWarnApplies(QuotaResources.CmsPagesPerSite));
    }

    // ═════════ OverageResources + ค่าตั้ง ═════════

    [Fact]
    public void รหัสมิเตอร์_อ่านรหัสเดิมได้_เขียนใหม่ใช้overage_และเป็นมิเตอร์ระบบ()
    {
        Assert.Equal("overage.documents", OverageResources.FeatureCode(OverageResources.Documents));
        Assert.Equal(OverageResources.Documents, OverageResources.FromFeatureCode(AddOnCodes.DocumentsOverage)!.Key);
        Assert.Equal(OverageResources.OcrPages, OverageResources.FromFeatureCode("overage.ocr.pages.month")!.Key);
        Assert.Null(OverageResources.FromFeatureCode("quota.users"));
        Assert.Equal("ส่วนเกินโควตาเอกสาร", OverageResources.InvoiceName("overage.documents"));
        Assert.Null(OverageResources.InvoiceName("lodging.stay"));
        Assert.True(AddOnCodes.IsSystemMeter("overage.notify.messages"));
        Assert.True(AddOnCodes.IsSystemMeter(AddOnCodes.DocumentsOverage));
        Assert.False(AddOnCodes.IsSystemMeter("ocr.scan"));
        Assert.Equal(QuotaResources.UsageUrl, OverageResources.BuyUrl(OverageResources.Documents));
        Assert.Equal(QuotaResources.BuyUrl(QuotaResources.OcrPagesMonth), OverageResources.BuyUrl(OverageResources.OcrPages));
    }

    [Fact]
    public void ค่าตั้งส่วนเกิน_ตรวจเข้ม_อ่านแถวเพี้ยนถูกบีบเข้าช่วง()
    {
        Assert.Null(PlatformBillingSettingsRules.ValidateOverage(7, 50m, "80,90,100", out var w));
        Assert.Equal("80,90,100", w);
        Assert.NotNull(PlatformBillingSettingsRules.ValidateOverage(0, 50m, "80", out _));
        Assert.NotNull(PlatformBillingSettingsRules.ValidateOverage(7, -1m, "80", out _));
        Assert.NotNull(PlatformBillingSettingsRules.ValidateOverage(7, 50m, "x", out _));
        var p = PlatformBillingSettingsRules.FromRow(new PlatformBillingSettings { OverageGraceDays = 999, OverageMinInvoiceAmount = -5m, QuotaWarnPercents = "", OverageUnpaidAction = (OverageUnpaidAction)42 });
        Assert.Equal(PlatformBillingSettingsRules.MaxOverageGraceDays, p.OverageGraceDays);
        Assert.Equal(0m, p.OverageMinInvoiceAmount);
        Assert.Equal(QuotaWarnPolicy.Default, p.WarnPercents);
        Assert.Equal(OverageUnpaidAction.LockThenDunning, p.OverageUnpaidAction);
        var d = PlatformBillingSettingsRules.FromRow(null);
        Assert.Equal(7, d.OverageGraceDays); Assert.Equal(50m, d.OverageMinInvoiceAmount);
        Assert.False(d.OverageAutoChargeSavedCard);   // ฝ่ายค้าน S3: ตั้งต้นไม่ตัดบัตรอัตโนมัติ
        Assert.Equal("2026-11.2", OveragePolicy.PolicyVersion);   // bump เพราะเงื่อนไขเพิ่มประโยคตัดบัตร
    }

    // ═════════ ใบแจ้งหนี้ค่าใช้งาน/ส่วนเกินรายเดือน — Helpers/UsageInvoiceLines (§B.5 · 231-4) ═════════

    [Fact]
    public void ใบส่วนเกิน_บรรทัดต่อรหัส_ชื่ออ่านออก_งวดไทย_Qtyคูณราคาเท่ายอดบรรทัด()
    {
        var rows = new[]
        {
            new UsageInvoiceLines.Row("2026-11", "overage.documents", "ฉบับ", 5m, 30, 150m),
            new UsageInvoiceLines.Row("2026-11", "overage.documents", "ฉบับ", 5m, 7, 35m),
            new UsageInvoiceLines.Row("2026-11", "overage.documents", "ฉบับ", 4m, 10, 40m),   // ฝ่ายค้าน S2: ราคาเปลี่ยนกลางเดือน ⇒ คนละบรรทัด (Qty × ราคา = ยอด ทุกบรรทัด)
            new UsageInvoiceLines.Row("2026-11", "overage.ocr.pages.month", "หน้า", 2m, 4, 8m),
            new UsageInvoiceLines.Row("2026-11", "documents.overage", "ฉบับ (ไม่คิดเงิน)", 0m, 2, 0m),   // รหัสเดิม = เอกสาร (อ่านย้อนหลังได้)
            new UsageInvoiceLines.Row("2026-11", "lodging.stay", "คืน", 20m, 1, 20m),
        };
        var lines = UsageInvoiceLines.Build(rows, code => code == "lodging.stay" ? ("ค่าที่พักผ่านระบบ", "คืน") : null);

        Assert.Equal(5, lines.Count);
        var doc = lines.Single(l => l.Unit == "ฉบับ" && l.Description.EndsWith("@ ฿5.00"));
        Assert.StartsWith("ส่วนเกินโควตาเอกสาร · งวด พ.ย. 2569", doc.Description);
        Assert.Equal(37m, doc.Quantity); Assert.Equal(185m, doc.AmountNet); Assert.Equal("ฉบับ", doc.Unit);
        Assert.Equal(185m, doc.Quantity * 5m);   // Qty × ราคา/หน่วย = ยอดบรรทัด (§86/4)
        var doc4 = lines.Single(l => l.Unit == "ฉบับ" && l.Description.EndsWith("@ ฿4.00"));
        Assert.Equal(10m, doc4.Quantity); Assert.Equal(40m, doc4.AmountNet); Assert.Equal(40m, doc4.Quantity * 4m);
        var ocr = lines.Single(l => l.Description.StartsWith("ส่วนเกินโควตาหน้าสแกน OCR"));
        Assert.Equal(4m, ocr.Quantity); Assert.Equal(8m, ocr.AmountNet); Assert.EndsWith("@ ฿2.00", ocr.Description);
        var legacy = lines.Single(l => l.Unit == "ฉบับ (ไม่คิดเงิน)");
        Assert.StartsWith("ส่วนเกินโควตาเอกสาร", legacy.Description); Assert.Equal(0m, legacy.AmountNet); Assert.DoesNotContain("@", legacy.Description);
        Assert.Contains(lines, l => l.Description.StartsWith("ค่าที่พักผ่านระบบ · งวด พ.ย. 2569"));
        Assert.Equal(185m, lines[0].AmountNet);   // งวดเดียวกัน ⇒ ยอดมากก่อน
        Assert.DoesNotContain(lines, l => l.Description.Contains("overage."));   // ไม่มีรหัสดิบบนใบ
    }

    [Fact]
    public void ใบส่วนเกิน_ครบกำหนดตามวันผ่อนผัน_ขั้นต่ำ_และการปิดงวด()
    {
        var issue = new DateTime(2026, 12, 1, 2, 0, 0, DateTimeKind.Utc);
        Assert.Equal(issue.AddDays(7), UsageInvoiceLines.DueDate(issue, 7));
        Assert.Equal(issue.AddDays(30), UsageInvoiceLines.DueDate(issue, 30));
        Assert.Equal(issue.AddDays(1), UsageInvoiceLines.DueDate(issue, 0));   // ผ่อนผันอย่างน้อย 1 วัน
        Assert.Equal(80m, UsageInvoiceLines.MinInvoice(80m, 50m));
        Assert.Equal(50m, UsageInvoiceLines.MinInvoice(0m, 50m));

        Assert.Equal(UsageInvoiceLines.Disposition.ClosePrepaid, UsageInvoiceLines.Dispose(true, 500m, 50m));
        Assert.Equal(UsageInvoiceLines.Disposition.CloseZero, UsageInvoiceLines.Dispose(false, 0m, 50m));   // ใบกฎหมายบังคับโหมดล็อก = ปิดงวดไม่มีใบ (เดิมยกยอดตลอดไป)
        Assert.Equal(UsageInvoiceLines.Disposition.Carry, UsageInvoiceLines.Dispose(false, 49.99m, 50m));
        Assert.Equal(UsageInvoiceLines.Disposition.Issue, UsageInvoiceLines.Dispose(false, 50m, 50m));   // พอดีขั้นต่ำ = ออกใบ
        Assert.Equal(UsageInvoiceLines.Disposition.Issue, UsageInvoiceLines.Dispose(false, 185m, 50m));
    }

    [Fact]
    public void ยกยอดสองงวด_30บวก30_ออกใบเดียว60บาท_2บรรทัด_อ้างอิงงวดล่าสุด()
    {
        // ฝ่ายค้าน B3: งวดละ 30 ต่ำกว่าขั้นต่ำ 50 — เดิมจัดกลุ่มต่องวดจึงยกยอดตลอดไป · ตอนนี้รวมต่อบริษัท ⇒ 60 ≥ 50 ออกใบเดียว คนละบรรทัดต่องวด
        var rows = new[]
        {
            new UsageInvoiceLines.Row("2026-10", "overage.documents", "ฉบับ", 5m, 6, 30m),
            new UsageInvoiceLines.Row("2026-11", "overage.documents", "ฉบับ", 5m, 6, 30m),
        };
        Assert.Equal(UsageInvoiceLines.Disposition.Carry, UsageInvoiceLines.Dispose(false, 30m, 50m));   // งวดเดียวยังยกยอด
        Assert.Equal(UsageInvoiceLines.Disposition.Issue, UsageInvoiceLines.Dispose(false, rows.Sum(r => r.ChargedAmount), 50m));
        var lines = UsageInvoiceLines.Build(rows, _ => null);
        Assert.Equal(2, lines.Count);
        Assert.Equal(60m, lines.Sum(l => l.AmountNet));
        Assert.Contains("งวด ต.ค. 2569", lines[0].Description);   // งวดเก่าก่อน
        Assert.Contains("งวด พ.ย. 2569", lines[1].Description);
        Assert.All(lines, l => Assert.Equal(30m, l.AmountNet));
        Assert.Equal("2026-11", UsageInvoiceLines.LatestPeriod(rows));
        var cid = Guid.Parse("12345678-aaaa-bbbb-cccc-000000000000");
        Assert.Equal("USAGE-2026-11-12345678", UsageInvoiceLines.Reference(cid, "2026-11"));
        Assert.Equal(PlatformInvoiceKind.Usage, PlatformInvoiceRules.KindOf(UsageInvoiceLines.Reference(cid, "2026-11")));
    }

    // ═════════ ราคาส่วนเกิน — ขนาดหน่วย (ฝ่ายค้าน S1) ═════════

    [Fact]
    public void ขนาดหน่วย_realtimeต้องเป็น1_พื้นที่ตั้งได้()
    {
        Assert.NotNull(OveragePolicy.ValidateRate(OverageResources.Documents, 2));
        Assert.NotNull(OveragePolicy.ValidateRate(OverageResources.OcrPages, 10));
        Assert.NotNull(OveragePolicy.ValidateRate(OverageResources.NotifyMessages, 100));
        Assert.Null(OveragePolicy.ValidateRate(OverageResources.Documents, 1));
        Assert.Null(OveragePolicy.ValidateRate(OverageResources.StorageGb, 10));   // สรุปสิ้นเดือน ⇒ ขนาดหน่วยได้
        Assert.NotNull(OveragePolicy.ValidateRate(OverageResources.StorageGb, 0));
        Assert.NotNull(OveragePolicy.ValidateRate("seats", 1));   // นอก closed list
    }

    // ═════════ ตัดบัตรอัตโนมัติใบส่วนเกิน — ผู้อ่าน OverageAutoChargeSavedCard (F2 ข้อ 2) ═════════

    [Fact]
    public void ใบUSAGE_ตัดบัตรอัตโนมัติเฉพาะเมื่อเปิดค่าตั้ง_ใบชนิดอื่นไม่ขึ้นกับค่าตั้ง()
    {
        Assert.True(PlatformAutoChargePolicy.AutoChargeAllowedForKind(PlatformInvoiceKind.Usage, true));
        Assert.False(PlatformAutoChargePolicy.AutoChargeAllowedForKind(PlatformInvoiceKind.Usage, false));
        Assert.True(PlatformAutoChargePolicy.AutoChargeAllowedForKind(PlatformInvoiceKind.Renewal, false));
        Assert.True(PlatformAutoChargePolicy.AutoChargeAllowedForKind(PlatformInvoiceKind.Other, false));
        Assert.Equal(PlatformInvoiceKind.Usage, PlatformInvoiceRules.KindOf("USAGE-2026-11-abcd1234"));
    }
}

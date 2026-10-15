using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบส่งออก 2026-10-15 <b>รอบ 2</b> — ตัวตัดสิน pure ของแต่ละข้อ (เคสที่พังกลับมาถูก + เคสที่ถูกอยู่แล้วไม่ถูกแตะ) ·
/// หลักฐานบนฐานจริงอยู่ที่ Db/AdversarialExportRound2DbTests
/// </summary>
public class AdversarialExportRound2Tests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid Capital = Guid.NewGuid();
    private static readonly Guid Re = Guid.NewGuid();
    private static readonly Guid Rev = Guid.NewGuid();
    private static readonly Guid Exp = Guid.NewGuid();

    private static readonly Dictionary<Guid, AccountType> Types = new()
    {
        [Cash] = AccountType.Asset, [Capital] = AccountType.Equity, [Re] = AccountType.Equity,
        [Rev] = AccountType.Revenue, [Exp] = AccountType.Expense,
    };

    // ───────── P1-2 ช่วงที่ใบปิดปีปิด ─────────

    [Fact]
    public void ใบปิดปี_ฐานเริ่มในรอบ_ปิดจากวันฐานรวมกำไรขาดทุนของฐาน_ฐานก่อนรอบใช้ต้นรอบ()
    {
        var fyStart = new DateTime(2025, 1, 1);
        Assert.Equal((new DateTime(2025, 7, 1), true), OpeningRoll.PnlCloseWindow(fyStart, new DateTime(2025, 7, 1)));
        // ทิศตรงข้าม: ยอดยกมาจากปิดปี (วันต้นรอบ) = ช่วงเดิม · ฐานของรอบก่อน / ไม่มีฐาน = ต้นรอบ ไม่รวมฐาน
        Assert.Equal((fyStart, true), OpeningRoll.PnlCloseWindow(fyStart, fyStart));
        Assert.Equal((fyStart, false), OpeningRoll.PnlCloseWindow(fyStart, new DateTime(2024, 7, 1)));
        Assert.Equal((fyStart, false), OpeningRoll.PnlCloseWindow(fyStart, null));
    }

    // ───────── P1-2 ยอดยกมา = งบทดลองสิ้นรอบ (ยกเฉพาะ A/L/E · สุทธิฝั่งเดียว) ─────────

    [Fact]
    public void ยกยอด_ไม่มีแถวกำไรขาดทุน_ยอดค้างรวมเข้ากำไรสะสม_สุทธิฝั่งเดียว()
    {
        var tb = new Dictionary<Guid, (decimal Debit, decimal Credit)>
        {
            [Cash] = (110_000m, 3_000m), [Capital] = (0m, 100_000m), [Re] = (0m, 2_000m),
            [Rev] = (0m, 10_000m), [Exp] = (5_000m, 0m),   // ยังไม่ถูกปิด (ปีก่อนที่ไม่ได้ปิด / แถวรุ่นเก่า)
        };
        var r = OpeningRoll.CarryForward(tb, Types, Re);
        Assert.False(r.ContainsKey(Rev));
        Assert.False(r.ContainsKey(Exp));
        Assert.Equal((107_000m, 0m), r[Cash]);                 // เดิมเก็บ 110,000 / 3,000
        Assert.Equal((0m, 7_000m), r[Re]);                     // 2,000 + กำไรค้าง 5,000
        Assert.Equal(0m, r.Values.Sum(v => v.Debit - v.Credit));
        // ทิศตรงข้าม: ไม่มีกำไรขาดทุนค้าง ⇒ ไม่ต้องรู้ผังกำไรสะสม · บัญชีศูนย์ถูกตัด
        var closed = OpeningRoll.CarryForward(new Dictionary<Guid, (decimal, decimal)> { [Cash] = (5m, 5m), [Capital] = (0m, 0m) }, Types, null);
        Assert.Empty(closed);
        // ค้างแต่ไม่รู้ผังกำไรสะสม = ล้มดัง (ไม่ทิ้งยอดเงียบ)
        Assert.Throws<BusinessRuleException>(() => OpeningRoll.CarryForward(tb, Types, null));
    }

    // ───────── P2-5 ส่วนที่ปันเข้ามิติ + ต้นรอบของรายงานแบบกรอง ─────────

    [Fact]
    public void ส่วนที่ปัน_จำนวนเงินก่อน_ร้อยละปัดAwayFromZero_ไม่มีทั้งสอง_เต็มบรรทัด()
    {
        Assert.Equal((300m, 0m), LedgerFilterRule.AllocationShare(1_000m, 0m, null, 30m));
        Assert.Equal((0m, 400m), LedgerFilterRule.AllocationShare(0m, 2_000m, 400m, null));      // ฝั่งเครดิตของบรรทัดรายได้
        Assert.Equal((0m, 400m), LedgerFilterRule.AllocationShare(0m, 2_000m, -400m, 99m));      // จำนวนเงินชนะร้อยละ · ไม่สนเครื่องหมาย
        Assert.Equal((0.03m, 0m), LedgerFilterRule.AllocationShare(0.05m, 0m, null, 50m));       // 0.025 ⇒ 0.03 (AwayFromZero · banker's ได้ 0.02)
        Assert.Equal((1_000m, 0m), LedgerFilterRule.AllocationShare(1_000m, 0m, null, null));    // ทิศตรงข้าม: ไม่ระบุ = เต็มบรรทัด (เดิม)
    }

    [Fact]
    public void รายงานแบบกรอง_รายได้ค่าใช้จ่ายเริ่มต้นรอบบัญชี()
    {
        Assert.Equal(new DateTime(2026, 1, 1), LedgerFilterRule.PnlWindowStart(new DateTime(2026, 3, 31), 1));
        Assert.Equal(new DateTime(2025, 4, 1), LedgerFilterRule.PnlWindowStart(new DateTime(2026, 3, 31), 4));
        Assert.Equal(new DateTime(2026, 4, 1), LedgerFilterRule.PnlWindowStart(new DateTime(2026, 4, 1), 4));
        Assert.Equal(new DateTime(2026, 1, 1), LedgerFilterRule.PnlWindowStart(new DateTime(2026, 6, 30), 0));   // ยังไม่ตั้งค่า = ปีปฏิทิน
    }

    // ───────── P1-3 ผังเฉพาะกิจ ─────────

    [Fact]
    public void ผังเฉพาะกิจ_รหัสจากเจ้าของแต่ละผัง_ผังทั่วไปไม่ถูกนับ()
    {
        foreach (var code in new[] { "54960", "21240", "11340", "11341", "11349", "11350", "53170", "57140", "21915", "21923" })
            Assert.Contains(code, SpecialPurposeAccount.Codes);
        foreach (var code in new[] { "11310", "11320", "21210", "51110", "41000", "1134", "113410" })
            Assert.DoesNotContain(code, SpecialPurposeAccount.Codes);
    }

    // ───────── P1-4 / P2-8 ผูกบัญชีแม่ ─────────

    private static ChartNode N(string code, AccountType t = AccountType.Asset, Guid? parent = null, bool system = false, int level = 1)
        => new(Guid.NewGuid(), code, t, parent, level, system);

    [Fact]
    public void ผังลงรายการของแม่แบบไม่เป็นบัญชีแม่_ข้ามไปหัวบัญชีที่สั้นกว่า()
    {
        var g113 = N("113", system: true, level: 3);
        var leaf = N("11310", parent: g113.Id, system: true, level: 4);
        var peak = N("113101");
        var plan = ChartParentLink.Plan(new List<ChartNode> { g113, leaf, peak }, new[] { peak.Id }, new HashSet<Guid>(), allowRehome: true);
        var p = Assert.Single(plan);
        Assert.Equal(g113.Id, p.ParentId);                     // เดิม 11310 ⇒ 11310 กลายเป็นบัญชีคุม
        Assert.Equal(4, p.NewLevel);
        // ทิศตรงข้าม: ผังของผู้ใช้ที่ยังไม่มีลูกและไม่ถูกใช้ เป็นหัวบัญชีได้ (หัวบัญชีนำเข้า)
        var userHeader = N("1131");
        var plan2 = ChartParentLink.Plan(new List<ChartNode> { g113, leaf, userHeader, peak }, new[] { peak.Id }, new HashSet<Guid>(), allowRehome: false);
        Assert.Equal(userHeader.Id, Assert.Single(plan2.Where(x => x.AccountId == peak.Id)).ParentId);
        // ผังของผู้ใช้ที่ถูกใช้อยู่ (ยอดยกมา/ค่าตั้ง) ⇒ ไม่ผูก บอกเหตุผล
        var plan3 = ChartParentLink.Plan(new List<ChartNode> { g113, leaf, userHeader, peak }, new[] { peak.Id }, new HashSet<Guid> { userHeader.Id }, allowRehome: false);
        Assert.NotNull(Assert.Single(plan3).BlockedReason);
    }

    [Fact]
    public void ผูกหัวบัญชี_ลูกหลานทุกชั้นปรับระดับ_ผังที่ไม่เกี่ยวไม่ถูกแตะ()
    {
        var g113 = N("113", system: true, level: 3);
        var leaf = N("11310", parent: g113.Id, system: true, level: 4);
        var head = N("1130");
        var child = N("113001", parent: head.Id, level: 2);
        var grand = N("1130011", parent: child.Id, level: 3);
        var other = N("2000", AccountType.Liability, level: 1);
        var chart = new List<ChartNode> { g113, leaf, head, child, grand, other };
        var plan = ChartParentLink.Plan(chart, new[] { head.Id }, new HashSet<Guid>(), allowRehome: false);
        var levels = ChartParentLink.Relevel(chart, plan);
        Assert.Equal(4, levels[head.Id]);
        Assert.Equal(5, levels[child.Id]);                     // เดิมค้าง 2
        Assert.Equal(6, levels[grand.Id]);
        Assert.False(levels.ContainsKey(other.Id));
        Assert.False(levels.ContainsKey(g113.Id));
        Assert.False(levels.ContainsKey(leaf.Id));
    }
}

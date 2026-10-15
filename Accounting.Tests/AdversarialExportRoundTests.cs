using Accounting.Helpers;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบส่งออก 2026-10-15 (erp-review/2026-10-14/EXPORT_AUDIT.md §"ฝ่ายค้านรอบส่งออก") — ตัวตัดสิน pure ของแต่ละข้อ ·
/// ทุกเทสต์ล็อกทั้ง "เคสที่พังกลับมาถูก" และ "เคสที่ถูกอยู่แล้วไม่ถูกแตะ" · หลักฐานบนฐานจริงอยู่ที่ Db/AdversarialExportRoundDbTests
/// </summary>
public class AdversarialExportRoundTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid Re = Guid.NewGuid();

    // ───────── P1-1 OpeningRoll ─────────

    [Fact]
    public void ยอดยกมา_ฐานบวกเคลื่อนไหว_ตัดบัญชีศูนย์()
    {
        var anchor = new Dictionary<Guid, (decimal Debit, decimal Credit)> { [Cash] = (108_000m, 0m), [Re] = (0m, 8_000m) };
        var movements = new Dictionary<Guid, (decimal Debit, decimal Credit)> { [Cash] = (20_000m, 0m), [Re] = (0m, 20_000m) };
        var r = OpeningRoll.Combine(anchor, movements);
        Assert.Equal((128_000m, 0m), r[Cash]);
        Assert.Equal((0m, 28_000m), r[Re]);
        var zero = OpeningRoll.Combine(new Dictionary<Guid, (decimal, decimal)> { [Cash] = (0m, 0m) }, new Dictionary<Guid, (decimal, decimal)>());
        Assert.Empty(zero);
    }

    [Fact]
    public void ยอดยกมา_เทียบสุทธิ_เดบิตเครดิตรวมต่างแต่สุทธิเท่ากันไม่ผิด()
    {
        var stored = new Dictionary<Guid, (decimal Debit, decimal Credit)> { [Cash] = (130_000m, 2_000m), [Re] = (0m, 36_000m) };
        var expected = new Dictionary<Guid, (decimal Debit, decimal Credit)> { [Cash] = (128_000m, 0m), [Re] = (0m, 28_000m) };
        var diff = OpeningRoll.Diff(stored, expected);
        Assert.Equal(new[] { Re }, diff.Select(d => d.AccountId).ToArray());   // เงินสด: 130,000−2,000 = 128,000 ⇒ ไม่ผิด
        // บัญชีที่มีแต่ฝั่งเก็บ (ควรเป็นศูนย์) = ผิด
        var extra = OpeningRoll.Diff(new Dictionary<Guid, (decimal, decimal)> { [Cash] = (5m, 0m) }, new Dictionary<Guid, (decimal, decimal)>());
        Assert.Single(extra);
    }

    [Fact]
    public void ยอดยกมา_แถวนำเข้าแยกจากแถวปิดปีด้วยหมายเหตุ()
    {
        Assert.True(OpeningRoll.IsImported(OpeningRoll.ImportedNotePrefix + " 1a2b3c4d"));
        Assert.False(OpeningRoll.IsImported(OpeningRoll.RolledNote(2025)));
        Assert.False(OpeningRoll.IsImported(null));
    }

    // ───────── P1-2 ChartParentLink ─────────

    private static ChartNode N(string code, AccountType t = AccountType.Asset, Guid? parent = null, bool system = false, int level = 1)
        => new(Guid.NewGuid(), code, t, parent, level, system);

    [Fact]
    public void ผูกแม่_prefixยาวสุด_ประเภทเดียวกัน_และรับลูกที่มาก่อนหัว()
    {
        var root = N("1", system: true);
        var g11 = N("11", parent: root.Id, system: true, level: 2);
        var g113 = N("113", parent: g11.Id, system: true, level: 3);
        var leaf = N("11310", parent: g113.Id, system: true, level: 4);
        var child = N("113001");                 // นำเข้ามาก่อนหัว — ผูกใต้ 113 ไปแล้ว
        child = child with { ParentId = g113.Id, Level = 4 };
        var header = N("1130");
        var liab = N("1130X", AccountType.Liability);   // ประเภทต่าง ⇒ ไม่ผูกใต้ 1130
        var chart = new List<ChartNode> { root, g11, g113, leaf, child, header, liab };

        var plan = ChartParentLink.Plan(chart, new[] { header.Id }, new HashSet<Guid>(), allowRehome: true);
        var h = plan.Single(p => p.AccountId == header.Id);
        Assert.Equal(g113.Id, h.ParentId);
        Assert.Equal(4, h.NewLevel);
        var c = plan.Single(p => p.AccountId == child.Id);   // รับลูกที่มาก่อนหัว
        Assert.Equal(header.Id, c.ParentId);
        Assert.DoesNotContain(plan, p => p.AccountId == leaf.Id);   // ผังระบบไม่ถูกย้ายแม่
        Assert.DoesNotContain(plan, p => p.AccountId == liab.Id);

        // ทิศตรงข้าม: ตอนซ่อม (allowRehome=false) ไม่ย้ายผังที่มีแม่แล้ว
        var repair = ChartParentLink.Plan(chart, new[] { header.Id }, new HashSet<Guid>(), allowRehome: false);
        Assert.Single(repair);
    }

    [Fact]
    public void ไม่ผูกใต้ผังที่มีรายการแล้วและยังไม่มีลูก_บอกเหตุผล()
    {
        var used = N("4100", AccountType.Revenue);
        var child = N("410001", AccountType.Revenue);
        var plan = ChartParentLink.Plan(new List<ChartNode> { used, child }, new[] { child.Id }, new HashSet<Guid> { used.Id }, allowRehome: false);
        var p = Assert.Single(plan);
        Assert.Null(p.ParentId);
        Assert.Contains("4100", p.BlockedReason);
        // ทิศตรงข้าม: ผังแม่ที่มีลูกอยู่แล้ว (เป็นบัญชีคุมอยู่แล้ว) ผูกได้แม้มีรายการ
        var other = N("410002", AccountType.Revenue, parent: used.Id);
        var ok = ChartParentLink.Plan(new List<ChartNode> { used, child, other }, new[] { child.Id }, new HashSet<Guid> { used.Id }, allowRehome: false);
        Assert.Equal(used.Id, Assert.Single(ok).ParentId);
    }

    // ───────── P2-7 LedgerRedaction ─────────

    [Fact]
    public void บัญชีแยกประเภท_ใบเงินเดือนที่ไม่มีสิทธิ์_รวมแถวเดียวต่อใบ_ยอดยกไปไม่เปลี่ยน()
    {
        var payrollJe = Guid.NewGuid();
        var normalJe = Guid.NewGuid();
        var d = new DateTime(2026, 9, 30);
        var acc = new GeneralLedgerAccount(Guid.NewGuid(), "54111", "เงินเดือน", AccountType.Expense, 5, 1_000m, 60_000m, 0m, 61_000m,
            new List<GeneralLedgerTransaction>
            {
                new(payrollJe, "PV-1", d, JournalType.General, "เงินเดือน นาย ก", 30_000m, 0m, 31_000m, JournalLineId: Guid.NewGuid(), EntryReference: "PR-1"),
                new(normalJe, "JV-9", d, JournalType.General, "ค่าอื่น", 5_000m, 0m, 36_000m),
                new(payrollJe, "PV-1", d, JournalType.General, "เงินเดือน นาย ข", 25_000m, 0m, 61_000m, JournalLineId: Guid.NewGuid()),
            });
        var gl = new GeneralLedgerResponse(new List<GeneralLedgerAccount> { acc }, d, d);
        var red = LedgerRedaction.Redact(gl, new Dictionary<Guid, SensitivityKind> { [payrollJe] = SensitivityKind.Payroll });
        var tx = red.Accounts[0].Transactions;
        Assert.Equal(2, tx.Count);
        Assert.Equal(55_000m, tx[0].DebitAmount);                       // รวมทั้งใบ — ไม่เห็นรายคน
        Assert.StartsWith(LedgerRedaction.HiddenPrefix, tx[0].Description);
        Assert.Null(tx[0].EntryReference);
        Assert.Null(tx[0].JournalLineId);
        Assert.Equal(61_000m, tx[^1].RunningBalance);                   // ยอดยกไป = งบทดลอง
        Assert.Equal(acc.ClosingBalance, red.Accounts[0].ClosingBalance);
        Assert.DoesNotContain(tx, t => t.Description!.Contains("นาย"));

        // ทิศตรงข้าม: ผู้มีสิทธิ์ (ไม่มีใบที่ต้องซ่อน) ได้ของเดิมทุกแถว
        Assert.Same(gl, LedgerRedaction.Redact(gl, new Dictionary<Guid, SensitivityKind>()));
    }

    // ───────── P2-8 ThaiDate ขอบวันเวลาไทยบนคอลัมน์ instant ─────────

    [Fact]
    public void วันที่ล้วน_เป็นเที่ยงคืนเวลาไทย_มีเวลาหรือUTC_ตรงตัว()
    {
        Assert.Equal(new DateTime(2026, 10, 13, 17, 0, 0), ThaiDate.InstantFromUtc(new DateTime(2026, 10, 14)));
        var (to, exclusive) = ThaiDate.InstantToUtc(new DateTime(2026, 10, 14));
        Assert.True(exclusive);
        Assert.Equal(new DateTime(2026, 10, 14, 17, 0, 0), to);
        var exact = new DateTime(2026, 10, 14, 9, 30, 0);
        Assert.Equal((exact, false), ThaiDate.InstantToUtc(exact));
        var utcMidnight = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utcMidnight, ThaiDate.InstantFromUtc(utcMidnight));
    }
}

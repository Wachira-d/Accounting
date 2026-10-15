using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบส่งออก 2026-10-15 <b>รอบ 3</b> (ฝ่ายค้านบน de823512) — ตัวตัดสิน pure ของแต่ละข้อ (เคสที่พังกลับมาถูก + เคสที่ถูกอยู่แล้วไม่ถูกแตะ) ·
/// หลักฐานบนฐานจริงอยู่ที่ Db/AdversarialExportRound3DbTests
/// </summary>
public class AdversarialExportRound3Tests
{
    // ───────── P1 ผังเฉพาะกิจ = ผังที่ระบบสร้าง (ไม่ใช่รหัส/ตัวชี้ใน config) · เลื่อนไปท้าย ไม่ตัดทิ้ง ─────────

    private static ChartOfAccount Acc(string code, AccountType type, bool system, bool hasParent = true)
        => new() { Id = Guid.NewGuid(), AccountCode = code, AccountName = "ผัง " + code, AccountType = type, IsSystemAccount = system,
            ParentAccountId = hasParent ? Guid.NewGuid() : null, Level = 4 };

    private static string? First(IEnumerable<ChartOfAccount> chart, Func<ChartOfAccount, bool> where)
        => chart.AsQueryable().OrderByGeneralPurposeFirst().FirstOrDefault(where)?.AccountCode;

    [Fact]
    public void ผังเฉพาะกิจ_ผังของลูกค้าที่ใช้รหัส54960ไม่ใช่ผังระบบ_เป็นผังตั้งต้นได้()
    {
        // ผังของลูกค้า: 54960 = ค่าใช้จ่ายทั่วไปของเขาเอง (ไม่ใช่ผังระบบ) — รอบ 2 ตัดด้วยรหัสอย่างเดียว ⇒ ถูกข้าม
        var customer = new[] { Acc("54960", AccountType.Expense, system: false, hasParent: false), Acc("59000", AccountType.Expense, system: false, hasParent: false) };
        Assert.Equal("54960", First(customer, a => a.AccountType == AccountType.Expense));
        // ทิศตรงข้าม: บริษัทผังแม่แบบ — 54960 ที่ระบบใส่ (ผังระบบ ไม่มีแม่) ยังไม่ใช่ค่าใช้จ่ายตั้งต้น
        var template = new[] { Acc("54960", AccountType.Expense, system: true, hasParent: false), Acc("51110", AccountType.Expense, system: true) };
        Assert.Equal("51110", First(template, a => a.AccountType == AccountType.Expense));
    }

    [Fact]
    public void ผังเฉพาะกิจ_ถ้ามีแต่ผังเฉพาะกิจ_ยังหาเจอ_ไม่กลายเป็นnull()
    {
        var onlySpecial = new[] { Acc("54960", AccountType.Expense, system: true, hasParent: false) };
        Assert.Equal("54960", First(onlySpecial, a => a.AccountType == AccountType.Expense));   // รอบ 2 ได้ null ⇒ ล้ม "ไม่พบบัญชี"
        var bankOnly = new[] { Acc(SpecialPurposeAccount.BankSubAccountCode("11122", 1), AccountType.Asset, system: true) };
        Assert.Equal("11122-001", First(bankOnly, a => a.AccountCode.StartsWith("111")));
    }

    [Fact]
    public void ผังย่อยธนาคารที่ระบบสร้างอยู่ท้าย_ผังแม่แบบ11111ที่ผูกธนาคารยังเป็นเงินสดตั้งต้น()
    {
        // 11111 ผูกบัญชีธนาคารได้ (ผู้ใช้เลือก) แต่ไม่ใช่ผังที่ระบบสร้างเพื่อธนาคาร ⇒ ยังเป็นเงินสดตั้งต้น (รอบ 2 ได้ 11112)
        var chart = new[]
        {
            Acc(SpecialPurposeAccount.BankSubAccountCode("11111", 1), AccountType.Asset, system: true),
            Acc("11112", AccountType.Asset, system: true),
            Acc("11111", AccountType.Asset, system: true),
        };
        Assert.Equal("11111", First(chart, a => a.AccountCode.StartsWith("111")));
        Assert.Equal("11111-001", SpecialPurposeAccount.BankSubAccountCode("11111", 1));
    }

    // ───────── P2-2 นำเข้าผังทีละแถว: ไม่ไล่ทุก FK เมื่อไม่มีผังเดิมให้ตรวจ ─────────

    private static ChartNode N(string code, AccountType t = AccountType.Asset, Guid? parent = null, bool system = false, int level = 1)
        => new(Guid.NewGuid(), code, t, parent, level, system);

    [Fact]
    public void ผังที่ต้องตรวจว่าถูกใช้_ไม่รวมผังที่เพิ่งสร้าง_ไม่มีผังเดิมเป็นแม่ได้คือว่าง()
    {
        var g113 = N("113", system: true, level: 3);
        var leaf = N("11310", parent: g113.Id, system: true, level: 4);
        var fresh = N("113101");
        var chart = new List<ChartNode> { g113, leaf, fresh };
        // ผังใหม่ที่ยังไม่บันทึก + ผังเดิมที่เป็นแม่ได้ไม่มี (ผังระบบมีลูก/ผังลงรายการของแม่แบบ) ⇒ ว่าง ⇒ ผู้เรียกไม่ยิงคิวรี (เดิม 41 คิวรีต่อแถว)
        Assert.Empty(ChartParentLink.InUseCheckCandidates(chart, new[] { fresh.Id }, new[] { fresh.Id }));
        // ทิศตรงข้าม: มีหัวบัญชีของผู้ใช้ที่ยังไม่มีลูก (อาจถูกทำเป็นบัญชีแม่) ⇒ ต้องตรวจตัวนั้น — แต่ไม่ตรวจผังใหม่
        var userHeader = N("1131");
        var withHeader = new List<ChartNode> { g113, leaf, userHeader, fresh };
        Assert.Equal(new[] { userHeader.Id }, ChartParentLink.InUseCheckCandidates(withHeader, new[] { fresh.Id }, new[] { fresh.Id }));
        // ซ่อม/รายงาน (ผู้สมัครบันทึกแล้ว) ⇒ ผู้สมัครเองถูกตรวจด้วย (อาจเป็นแม่ของผู้สมัครอีกตัว)
        Assert.Contains(fresh.Id, ChartParentLink.InUseCheckCandidates(chart, new[] { fresh.Id }, Array.Empty<Guid>()));
    }

    // ───────── P2-4 / P2-6 ยอดยกมา ─────────

    [Fact]
    public void แถวยกยอด_หมายเหตุปิดปีหรือรุ่นเก่าnull_แถวนำเข้าไม่ใช่()
    {
        Assert.True(OpeningRoll.IsRolled(OpeningRoll.RolledNote(2026)));
        Assert.True(OpeningRoll.IsRolled(null));                                         // ยกยอดก่อน 825f5c0e ไม่ประทับหมายเหตุ
        Assert.False(OpeningRoll.IsRolled(OpeningRoll.ImportedNotePrefix + " abc"));
        Assert.False(OpeningRoll.IsRolled("ผู้ใช้ลงเอง"));
    }

    [Fact]
    public void งวดมกรารุ่นเก่า_เฉพาะรอบที่ไม่ใช่ปีปฏิทิน_และทุกแถวเป็นแถวยกยอด()
    {
        var rolled = new[] { OpeningRoll.RolledNote(2026), null };
        Assert.True(OpeningRoll.IsLegacyCalendarRollPeriod(1, 4, rolled));
        // ทิศตรงข้าม: ปีปฏิทิน (ม.ค. = ต้นรอบ) · เดือนอื่น · มีแถวนำเข้าปน (ผู้ใช้ลงเอง) · งวดว่าง ⇒ ไม่แตะ
        Assert.False(OpeningRoll.IsLegacyCalendarRollPeriod(1, 1, rolled));
        Assert.False(OpeningRoll.IsLegacyCalendarRollPeriod(1, 0, rolled));             // ยังไม่ตั้งค่า = ปีปฏิทิน
        Assert.False(OpeningRoll.IsLegacyCalendarRollPeriod(4, 4, rolled));
        Assert.False(OpeningRoll.IsLegacyCalendarRollPeriod(1, 4, new[] { OpeningRoll.RolledNote(2026), OpeningRoll.ImportedNotePrefix + " x" }));
        Assert.False(OpeningRoll.IsLegacyCalendarRollPeriod(1, 4, Array.Empty<string?>()));
    }

    [Fact]
    public void ผังที่ถูกลบแต่ยังมียอด_ล้มดัง_ศูนย์ไม่ล้ม()
    {
        var cash = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        var zeroDeleted = Guid.NewGuid();
        var types = new Dictionary<Guid, AccountType> { [cash] = AccountType.Asset };
        var tb = new Dictionary<Guid, (decimal Debit, decimal Credit)>
        {
            [cash] = (1_000m, 0m), [deleted] = (5_000m, 0m), [zeroDeleted] = (300m, 300m),
        };
        Assert.Equal(new[] { deleted }, OpeningRoll.UnknownAccountsWithBalance(tb, types));
        Assert.Throws<BusinessRuleException>(() => OpeningRoll.CarryForward(tb, types, null));   // เดิมตัดทิ้งเงียบ ⇒ ยอดยกมาไม่ลงตัว
        var msg = OpeningRoll.DeletedWithBalanceMessage(new DateTime(2026, 1, 1), new[] { ("11500", "ลูกหนี้อื่น", 5_000m) });
        Assert.Contains("11500", msg);
        Assert.Contains("กู้คืน", msg);
        // ทิศตรงข้าม: ผังที่ถูกลบแต่สุทธิศูนย์ ไม่ทำให้ยกยอดล้ม
        tb.Remove(deleted);
        Assert.Equal(1_000m, OpeningRoll.CarryForward(tb, types, null)[cash].Debit);
    }

    // ───────── P2-7 ผังที่ถูกผูกใต้ผังลงรายการของแม่แบบ ─────────

    [Fact]
    public void ผังใต้ผังลงรายการของแม่แบบ_ย้ายไปหัวบัญชีถัดไป_ลูกที่ระบบสร้างและหัวบัญชีผู้ใช้ไม่ถูกแตะ()
    {
        var g113 = N("113", system: true, level: 3);
        var leaf = N("11310", parent: g113.Id, system: true, level: 4);
        var peak = N("113101", parent: leaf.Id, level: 5);
        var grand = N("1131011", parent: peak.Id, level: 6);
        var g111 = N("111", system: true, level: 3);
        var bankGroup = N("11122", parent: g111.Id, system: true, level: 4);
        var bankSub = N("11122-001", parent: bankGroup.Id, system: true, level: 5);   // ระบบสร้าง — ไม่ใช่ผลของการนำเข้า
        var userHead = N("1140");
        var userChild = N("114001", parent: userHead.Id, level: 2);
        var chart = new List<ChartNode> { g113, leaf, peak, grand, g111, bankGroup, bankSub, userHead, userChild };

        var plan = ChartParentLink.PlanTemplateLeafRescue(chart, null, new HashSet<Guid>());
        var p = Assert.Single(plan);
        Assert.Equal(peak.Id, p.AccountId);
        Assert.Equal(g113.Id, p.ParentId);
        Assert.Equal(4, p.NewLevel);
        Assert.Null(p.BlockedReason);
        var levels = ChartParentLink.Relevel(chart, plan);
        Assert.Equal(5, levels[grand.Id]);                 // ลูกหลานปรับระดับตาม

        // หัวบัญชีของผู้ใช้ที่ยังไม่มีลูกและถูกใช้อยู่ ⇒ ไม่ย้าย บอกเหตุผล
        var userGroup = N("1131");
        var chart2 = new List<ChartNode> { g113, leaf, peak, userGroup };
        var blocked = Assert.Single(ChartParentLink.PlanTemplateLeafRescue(chart2, null, new HashSet<Guid> { userGroup.Id }));
        Assert.NotNull(blocked.BlockedReason);
        Assert.Null(blocked.ParentId);
        // เลือกเฉพาะผังอื่น ⇒ ไม่มีข้อเสนอ
        Assert.Empty(ChartParentLink.PlanTemplateLeafRescue(chart, new[] { userChild.Id }, new HashSet<Guid>()));
    }
}

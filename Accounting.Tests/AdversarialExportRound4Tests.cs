using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบส่งออก 2026-10-15 <b>รอบ 4</b> (ฝ่ายค้านบน 774ea7bc · erp-review/2026-10-14/EXPORT_AUDIT.md §"รอบ 4") — ตัวตัดสิน pure ของแต่ละข้อ
/// (เคสที่พังกลับมาถูก + เคสที่ถูกอยู่แล้วไม่ถูกแตะ) · หลักฐานบนฐานจริงอยู่ที่ Db/AdversarialExportRound4DbTests
/// </summary>
public class AdversarialExportRound4Tests
{
    // ───────── P2-a งวดยกยอดรุ่นเก่าไป ม.ค. — ปีที่ซ่อม (ตัวตั้งเดียวของรายงานและด่านปิดปี) + ข้อความด่าน ─────────

    [Fact]
    public void งวดยกยอดรุ่นเก่า_ปีที่ซ่อม_คือปีถัดจากรอบที่งวดนั้นอยู่()
    {
        // รอบ เม.ย.–มี.ค.: การปิดปี 2025 รุ่นเก่าเขียน ม.ค. 2026 (อยู่ในรอบ 2025) ⇒ ซ่อมปี 2026 (เท่ากับ OpeningRollCheckYear.Year ของการปิดปี 2025)
        Assert.Equal(2026, OpeningRoll.LegacyRepairYear(new DateTime(2026, 1, 1), 4));
        // รอบ ก.ค.–มิ.ย.: ม.ค. 2026 อยู่ในรอบ 2025 เช่นกัน
        Assert.Equal(2026, OpeningRoll.LegacyRepairYear(new DateTime(2026, 1, 1), 7));
        // รอบ ต.ค.–ก.ย.: ม.ค. 2026 อยู่ในรอบ 2025
        Assert.Equal(2026, OpeningRoll.LegacyRepairYear(new DateTime(2026, 1, 1), 10));
    }

    [Fact]
    public void ข้อความด่านปิดปี_บอกงวด_ปีที่ต้องซ่อม_และทางไปต่อ()
    {
        var msg = OpeningRoll.LegacyRollBlockMessage(new[] { (new DateTime(2026, 1, 1), 2026, 2) });
        Assert.Contains("ม.ค. 2569", msg);
        Assert.Contains("ซ่อมปี 2569", msg);
        Assert.Contains("ยังไม่มีอะไรถูกบันทึก", msg);
        Assert.Contains("ตรวจข้อมูลจากบั๊กที่แก้แล้ว", msg);   // ทางไปต่อชี้หน้าที่มีปุ่มจริง
    }

    // ───────── P2-b ผังที่ถูกลบ: รายได้/ค่าใช้จ่าย = ล้าง/รวมเข้ากำไรสะสม · สินทรัพย์/หนี้สิน/ทุนที่มียอด = ล้มดัง ─────────

    [Fact]
    public void ผังที่ถูกลบ_ค่าใช้จ่ายรวมเข้ากำไรสะสม_สินทรัพย์ที่มียอดล้มดัง()
    {
        var cash = Guid.NewGuid();
        var re = Guid.NewGuid();
        var deletedExpense = Guid.NewGuid();
        var deletedAsset = Guid.NewGuid();
        var types = new Dictionary<Guid, AccountType>
        {
            [cash] = AccountType.Asset, [re] = AccountType.Equity, [deletedExpense] = AccountType.Expense, [deletedAsset] = AccountType.Asset,
        };
        var deleted = new HashSet<Guid> { deletedExpense, deletedAsset };
        var tb = new Dictionary<Guid, (decimal Debit, decimal Credit)>
        {
            [cash] = (10_000m, 0m), [re] = (0m, 9_000m), [deletedExpense] = (1_000m, 0m), [deletedAsset] = (0m, 0m),
        };
        // ผังค่าใช้จ่ายที่ลบ (รู้ประเภท) ไม่ใช่เหตุให้ล้ม — เดิมไม่รู้ประเภท ⇒ ล้มทุกครั้งหลังลบ (ใบปิดปีไม่ล้างมันด้วย)
        Assert.Empty(OpeningRoll.UnknownAccountsWithBalance(tb, types, deleted));
        var rolled = OpeningRoll.CarryForward(tb, types, re, deleted);
        Assert.Equal(-8_000m, rolled[re].Debit - rolled[re].Credit);   // 1,000 ค่าใช้จ่ายรวมเข้ากำไรสะสม
        Assert.False(rolled.ContainsKey(deletedExpense));

        // ผังสินทรัพย์ที่ลบแต่มียอด ⇒ ยกไม่ได้ (ยอดยกมาที่ไม่มีใครเห็น) — ล้มดัง
        tb[deletedAsset] = (5_000m, 0m);
        Assert.Equal(new[] { deletedAsset }, OpeningRoll.UnknownAccountsWithBalance(tb, types, deleted));
        Assert.Throws<BusinessRuleException>(() => OpeningRoll.CarryForward(tb, types, re, deleted));
        // ทิศตรงข้าม: ผังเดียวกันที่ยังไม่ถูกลบ ⇒ ยกตามปกติ
        Assert.Equal(5_000m, OpeningRoll.CarryForward(tb, types, re, new HashSet<Guid> { deletedExpense })[deletedAsset].Debit);
    }

    [Fact]
    public void ข้อความผังที่ถูกลบยังมียอด_ชี้ปุ่มกู้คืนที่มีจริง_และบอกว่ายังไม่บันทึก()
    {
        var msg = OpeningRoll.DeletedWithBalanceMessage(new DateTime(2026, 1, 1), new[] { ("11500", "ลูกหนี้อื่น", 5_000m) });
        Assert.Contains("11500", msg);
        Assert.Contains("กู้คืน", msg);
        Assert.Contains("ยังไม่มีอะไรถูกบันทึก", msg);
        Assert.Contains("ตรวจข้อมูลจากบั๊กที่แก้แล้ว", msg);
        Assert.DoesNotContain("(ยกเลิกการลบ) แล้วโอนยอด", msg);   // ข้อความรอบ 3 ชี้ทางที่ไม่มีอยู่จริง
    }

    // ───────── P2-c หลักฐานว่าระบบผูกบัญชีแม่ (ไม่ใช่ผู้ใช้สร้างใต้ผังแม่แบบเอง) ─────────

    [Fact]
    public void ผังที่สร้างจากการนำเข้า_มีเครื่องหมาย_ผังที่ผู้ใช้สร้างไม่มี()
    {
        Assert.True(ChartParentLink.IsImportCreated(ChartParentLink.MigrateActorPrefix + "PEAK:manual"));
        Assert.True(ChartParentLink.IsImportCreated(ChartParentLink.FileImportActor));
        // ทิศตรงข้าม: ผังที่ผู้ใช้สร้างที่หน้าผังบัญชี (CreatedBy ว่าง/รหัสผู้ใช้) ไม่ใช่หลักฐาน
        Assert.False(ChartParentLink.IsImportCreated(null));
        Assert.False(ChartParentLink.IsImportCreated(Guid.NewGuid().ToString()));
        Assert.False(ChartParentLink.IsImportCreated("migrate:peak"));
    }

    [Fact]
    public void เครื่องหมายauditการผูก_ตรงทั้งผูกจากรายงานและผูกตอนนำเข้า_ไม่ตรงการย้ายผัง()
    {
        string Json(string action) => System.Text.Json.JsonSerializer.Serialize(new { action, accountCode = "113101", level = 5 });
        Assert.Contains(ChartParentLink.ParentLinkAuditMarker, Json(ChartParentLink.ParentLinkAction));
        Assert.Contains(ChartParentLink.ParentLinkAuditMarker, Json(ChartParentLink.ParentLinkImportAction));
        Assert.DoesNotContain(ChartParentLink.ParentLinkAuditMarker, Json(ChartParentLink.LeafRescueAction));
        Assert.DoesNotContain(ChartParentLink.ParentLinkAuditMarker, Json("coa-relevel"));
    }
}

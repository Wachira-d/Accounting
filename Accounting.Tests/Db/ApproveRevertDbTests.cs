using Accounting.Helpers;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// PP36_REVIEW P0-2 (2026-10-02) — กลไกของ "ใบอนุมัติแล้วที่ไม่มี JE" ผ่าน PostgreSQL จริง: ธุรกรรมอนุมัติ SaveChanges ไปบางส่วนแล้วล้ม ⇒ rollback ฝั่ง DB
/// แต่ค่าที่แก้หลัง save สุดท้าย + ของที่ Added ค้างใน context ⇒ SaveChanges ถัดไปของผู้เรียก (หมายเหตุ · sync log · AuditMiddleware) บันทึกครึ่งทาง ·
/// <c>ApproveDocumentAsync</c> เรียก <see cref="TrackedChangeRevert.RevertAsync"/> ใน catch ของธุรกรรม (ล็อกด้วย <c>tools/required_call_site_check.py</c>)
/// <para>ใช้แถว <c>Companies</c> (ไม่มี FK บังคับ · แยกด้วย Id สุ่ม) แทนเอกสาร+JE — กลไก change tracker เดียวกัน · สองทิศ: ไม่ถอย = ครึ่งทางถูกบันทึก
/// (พิสูจน์ว่ากลไกจริง) · ถอย = ไม่มีครึ่งทาง และงานค้างของผู้เรียกที่ยังไม่บันทึกไม่หาย</para>
/// <para>ทำไมไม่สร้าง <c>DocumentService</c> จริง: constructor ต้องการบริการจริง 7 ตัว (รวมคลาสคอนกรีต VendorIntelligenceService ·
/// CrossTenantWorkflowService ที่ต้องการบริการต่ออีกหลายชั้น) — env นี้ไม่มี SDK ให้คอมไพล์ตรวจ ⇒ golden ระดับ service เสี่ยงทำให้ทั้งโปรเจกต์เทสต์คอมไพล์ไม่ผ่าน ·
/// ตัวถอยที่เทสต์นี้เรียกคือฟังก์ชันเดียวกับที่ service เรียก</para>
/// </summary>
[Trait("Category", "Db")]
public class ApproveRevertDbTests
{
    private readonly ITestOutputHelper _out;
    public ApproveRevertDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seeded(Company Company, Guid StrayId);

    /// <summary>จำลองธุรกรรมอนุมัติที่ล้ม: save บางส่วนในธุรกรรม → แก้ต่อ + Add ของใหม่ → rollback (ไม่ commit)</summary>
    private static async Task<Seeded> FailApprovalMidTransactionAsync(Accounting.Data.AccountingDbContext db, bool revert)
    {
        var co = new Company { Name = "ทดสอบถอยค่าค้างหลังอนุมัติล้ม", TaxId = "0105556000001" };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        co.NameEn = "caller-pending";                          // งานค้างของผู้เรียก (ยังไม่บันทึก)
        var snap = TrackedChangeRevert.Capture(db);
        var strayId = Guid.NewGuid();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            co.BranchName = "approve-saved-in-rolled-back-tx"; // เทียบได้กับ เลขเอกสาร/Status ที่ save ในธุรกรรม
            await db.SaveChangesAsync();
            co.SuspendReason = "approve-pending-at-throw";     // เทียบได้กับ ค่าที่แก้หลัง save สุดท้ายก่อนด่านโยน
            db.Companies.Add(new Company { Id = strayId, Name = "ของค้างจากขั้นที่ล้ม", TaxId = "0105556000002" });  // เทียบได้กับ JE ที่ Added
            await tx.RollbackAsync();
        }
        if (revert)
            Assert.Equal(0, await TrackedChangeRevert.RevertAsync(db, snap));
        await db.SaveChangesAsync();                           // SaveChanges ถัดไปของผู้เรียก
        return new Seeded(co, strayId);
    }

    [Fact]
    public async Task ไม่ถอย_SaveChangesถัดไปบันทึกครึ่งทางของการอนุมัติที่ล้ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await FailApprovalMidTransactionAsync(db, revert: false);
        using var check = DbTestDatabase.TryCreateContext()!;
        var row = await check.Companies.AsNoTracking().SingleAsync(c => c.Id == s.Company.Id);
        // ครึ่งทาง: ค่าที่แก้หลัง save สุดท้ายถูกบันทึก + ของที่ Added โผล่ (= ใบ Paid ที่ไม่มี JE · หรือ JE ลอย)
        Assert.Equal("approve-pending-at-throw", row.SuspendReason);
        Assert.True(await check.Companies.AnyAsync(c => c.Id == s.StrayId));
    }

    [Fact]
    public async Task ถอยแล้ว_ไม่มีครึ่งทาง_และงานค้างของผู้เรียกไม่หาย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await FailApprovalMidTransactionAsync(db, revert: true);
        using var check = DbTestDatabase.TryCreateContext()!;
        var row = await check.Companies.AsNoTracking().SingleAsync(c => c.Id == s.Company.Id);
        Assert.Null(row.SuspendReason);
        Assert.Null(row.BranchName);
        Assert.False(await check.Companies.AnyAsync(c => c.Id == s.StrayId));
        Assert.Equal("caller-pending", row.NameEn);           // หมายเหตุ/ค่าที่ผู้เรียกแก้ไว้ก่อนเรียกอนุมัติ ยังถูกบันทึก
    }
    [Fact]
    public async Task ถอยแล้ว_ช่องที่ผู้เรียกไม่ได้แตะได้ค่าจากฐาน_ไม่ถูกทับด้วยsnapshotเก่า()
    {
        // ฝ่ายค้าน P2-1: ระหว่างที่การอนุมัติทำงาน ผู้ใช้อีกคนแก้ช่องอื่นของแถวเดียวกัน — การถอยต้องไม่เขียนค่าเก่าทับ
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var co = new Company { Name = "ทดสอบถอยเฉพาะช่องที่แก้", TaxId = "0105556000001" };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        co.NameEn = "caller-pending";
        var snap = TrackedChangeRevert.Capture(db);
        using (var other = DbTestDatabase.TryCreateContext()!)
        {
            var row = await other.Companies.SingleAsync(c => c.Id == co.Id);
            row.TaxId = "0105556099999";                       // คนอื่นแก้ช่องที่ผู้เรียกไม่ได้แตะ
            await other.SaveChangesAsync();
        }
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            co.SuspendReason = "approve-pending-at-throw";
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await TrackedChangeRevert.RevertAsync(db, snap));
        await db.SaveChangesAsync();
        using var check = DbTestDatabase.TryCreateContext()!;
        var saved = await check.Companies.AsNoTracking().SingleAsync(c => c.Id == co.Id);
        Assert.Equal("0105556099999", saved.TaxId);
        Assert.Equal("caller-pending", saved.NameEn);
        Assert.Null(saved.SuspendReason);
    }
}

using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// PP36_REVIEW P0-2 + ซ่อมข้อมูลเดิม (2026-10-02) — PV-20260901-0001 (Booking.com 5,908 · ภ.พ.36 413.56):
/// ด่าน JE ตีตก → ApproveDocumentAsync rollback ธุรกรรม แต่ค่าที่แก้ (เลขเอกสาร · Status=Paid · JE ที่ Added) ค้างใน context →
/// SaveChanges ถัดไปของผู้เรียกบันทึก "ใบอนุมัติแล้วที่ไม่มี JE" · ผู้เรียก LogInformation "staying Draft" ซึ่งไม่จริง
/// <para>ไฟล์นี้ล็อก (ก) ตัวถอยแบบจำงานค้างของผู้เรียก (<see cref="TrackedChangeRevert.Capture"/> · DbContext ออฟไลน์ ไม่ต้องมีฐาน)
/// (ข) ข้อความล้มดังตัวเดียว (<see cref="AutoApproveFailure"/>) (ค) ตัวตัดสินเครื่องมือลงบัญชีย้อนหลัง (<see cref="MissingJournalRepair"/>) สองทิศ ·
/// เทสต์ผ่านฐานจริงอยู่ที่ <c>Db/ApproveRevertDbTests</c></para>
/// </summary>
public class Pp36ApprovalLoudFailureTests
{
    private static AccountingDbContext NewContext()
        => new(new DbContextOptionsBuilder<AccountingDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none").Options);

    // ── (ก) ตัวถอย: จำงานค้างของผู้เรียก ไม่ต้องบังคับ SaveChanges ก่อน ─────────────────────────────

    [Fact]
    public void Revert_restores_caller_pending_work_and_drops_the_failed_approval_leftovers()
    {
        using var db = NewContext();
        var co = new Company { Name = "บริษัทเดิม", TaxId = "0105556000001" };
        db.Attach(co);                                         // แถวที่บันทึกแล้ว (Unchanged)
        var dbValues = db.Entry(co).CurrentValues.Clone();     // ค่าที่อยู่ในฐานข้อมูล
        co.NameEn = "caller-pending";                          // งานค้างของผู้เรียก (ยังไม่บันทึก)
        var callerAdded = new Company { Name = "ผู้เรียกเพิ่มค้าง", TaxId = "0105556000002" };
        db.Companies.Add(callerAdded);

        var snap = TrackedChangeRevert.Capture(db);
        Assert.Equal(2, snap.PendingCount);

        // ขั้นอนุมัติที่ล้ม: แก้ค่า + เพิ่ม JE + SaveChanges ในธุรกรรมที่ rollback ⇒ EF ยอมรับค่า (Unchanged) ทั้งที่ฐานข้อมูลไม่มี
        co.BranchName = "approve-stale";
        var staleJe = new JournalEntry { CompanyId = co.Id, EntryNumber = "JV-STALE" };
        db.JournalEntries.Add(staleJe);
        db.ChangeTracker.AcceptAllChanges();

        var kept = TrackedChangeRevert.DetachSince(db, snap.Entities);
        Assert.Equal(EntityState.Detached, db.Entry(staleJe).State);
        Assert.False(TrackedChangeRevert.NeedsReload(snap, db.Entry(callerAdded)));   // ยังไม่มีแถวในฐาน — reload จะปลดทิ้ง
        Assert.True(TrackedChangeRevert.NeedsReload(snap, db.Entry(co)));
        Assert.Equal(2, kept.Count);

        // จำลอง ReloadAsync: ค่าจริงจากฐานข้อมูล
        var e = db.Entry(co);
        e.CurrentValues.SetValues(dbValues);
        e.OriginalValues.SetValues(dbValues);
        e.State = EntityState.Unchanged;

        TrackedChangeRevert.ReapplyPending(db, snap);

        Assert.Equal("caller-pending", co.NameEn);            // งานค้างของผู้เรียกกลับมา
        Assert.Null(co.BranchName);                            // ครึ่งทางของการอนุมัติหาย
        Assert.Equal(EntityState.Modified, e.State);
        Assert.True(e.Property(nameof(Company.NameEn)).IsModified);
        Assert.False(e.Property(nameof(Company.BranchName)).IsModified);
        Assert.Equal(EntityState.Added, db.Entry(callerAdded).State);   // ของที่ผู้เรียก Add ค้าง ยังจะถูก INSERT
        Assert.Equal(0, TrackedChangeRevert.DetachStrays(db, snap.Entities));
    }

    [Fact]
    public void Without_reapply_the_reload_alone_would_silently_lose_the_callers_pending_work()
    {
        // ทิศตรงข้าม (เหตุผลที่ต้องมี Capture แทน "จำแค่ชุด entity"): reload อย่างเดียว ⇒ งานค้างของผู้เรียกหายเงียบ
        using var db = NewContext();
        var co = new Company { Name = "บริษัทเดิม", TaxId = "0105556000001" };
        db.Attach(co);
        var dbValues = db.Entry(co).CurrentValues.Clone();
        co.NameEn = "caller-pending";
        var snap = TrackedChangeRevert.Capture(db);
        db.ChangeTracker.AcceptAllChanges();
        TrackedChangeRevert.DetachSince(db, snap.Entities);
        var e = db.Entry(co);
        e.CurrentValues.SetValues(dbValues);
        e.OriginalValues.SetValues(dbValues);
        e.State = EntityState.Unchanged;
        Assert.Null(co.NameEn);                                // ← สิ่งที่จะเกิดถ้าไม่ ReapplyPending
        TrackedChangeRevert.ReapplyPending(db, snap);
        Assert.Equal("caller-pending", co.NameEn);
    }

    [Fact]
    public void Revert_with_no_pending_work_only_drops_new_entities()
    {
        using var db = NewContext();
        var co = new Company { Name = "บริษัทเดิม", TaxId = "0105556000001" };
        db.Attach(co);
        var snap = TrackedChangeRevert.Capture(db);
        Assert.Equal(0, snap.PendingCount);
        db.JournalEntries.Add(new JournalEntry { CompanyId = co.Id, EntryNumber = "JV-1" });
        TrackedChangeRevert.DetachSince(db, snap.Entities);
        TrackedChangeRevert.ReapplyPending(db, snap);
        Assert.Equal(EntityState.Unchanged, db.Entry(co).State);
        Assert.Single(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Caller_pending_delete_survives_a_rolled_back_save()
    {
        using var db = NewContext();
        var co = new Company { Name = "จะถูกลบโดยผู้เรียก", TaxId = "0105556000001" };
        db.Attach(co);
        db.Remove(co);
        var snap = TrackedChangeRevert.Capture(db);
        db.ChangeTracker.AcceptAllChanges();                   // SaveChanges ที่ rollback ⇒ ปลดออกจาก tracker
        Assert.Equal(EntityState.Detached, db.Entry(co).State);
        TrackedChangeRevert.ReapplyPending(db, snap);
        Assert.Equal(EntityState.Deleted, db.Entry(co).State);
    }

    // ── (ข) ข้อความล้มดังตัวเดียว ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DocumentStatus.Draft, true)]
    [InlineData(DocumentStatus.WaitingApproval, true)]
    [InlineData(DocumentStatus.Approved, false)]
    [InlineData(DocumentStatus.Paid, false)]
    public void Still_draft_is_decided_from_the_actual_status(DocumentStatus status, bool expected)
        => Assert.Equal(expected, AutoApproveFailure.IsStillDraft(status));

    [Fact]
    public void Message_says_draft_and_gives_the_next_step_when_nothing_was_posted()
    {
        var m = AutoApproveFailure.Message("สร้างใบสำคัญจ่ายเงินสด", "DRAFT-1", stillDraft: true,
            "JE ของ PV ไม่ผ่านการตรวจโครงสร้างบัญชี — [JE-NO-COUNTERPART]");
        Assert.Contains("ไม่สำเร็จ", m);
        Assert.Contains("JE-NO-COUNTERPART", m);
        Assert.Contains("ยังไม่ลงบัญชี", m);
        Assert.Contains("อนุมัติ", m);
    }

    [Fact]
    public void Message_never_claims_draft_when_the_approval_actually_committed()
    {
        // ทิศตรงข้าม: ขั้นหลัง commit ล้ม (แจ้งเตือน/e-Tax) — ห้ามบอก "ยังเป็นร่าง" (ผู้ใช้จะอนุมัติซ้ำ/สร้างใบใหม่)
        var m = AutoApproveFailure.Message("LINE ปุ่มอนุมัติ", "PV-202609-0001", stillDraft: false, "LINE timeout");
        Assert.DoesNotContain("ยังเป็นร่าง", m);
        Assert.Contains("ลงบัญชีแล้ว", m);
        Assert.Contains("ไม่ต้องอนุมัติซ้ำ", m);
        Assert.Contains("ไม่ทราบสาเหตุ", AutoApproveFailure.Message("x", "y", true, "  "));
    }

    // ── (ค) ตัวตัดสินเครื่องมือลงบัญชีย้อนหลัง ─────────────────────────────────────────────────

    private static MissingJournalFacts Facts(
        DocumentType t = DocumentType.PaymentVoucher, DocumentStatus s = DocumentStatus.Paid,
        bool hasJe = false, string? locked = null, string? pp36 = null, bool remitted = false, bool recognized = false,
        bool settlementReceipt = false, bool replaces = false)
        => new(t, s, settlementReceipt, replaces, hasJe, locked, pp36, remitted, recognized);

    [Fact]
    public void Approved_foreign_pv_without_je_in_open_period_before_remittance_can_be_repaired()
    {
        var d = MissingJournalRepair.Decide(Facts(pp36: "09/2026"));
        Assert.True(d.Missing);
        Assert.True(d.CanRepair);
        Assert.Contains("ลงบัญชีให้ใบนี้", d.Message);
    }

    [Theory]
    [InlineData(DocumentStatus.Draft)]        // ยังไม่อนุมัติ — ไม่ใช่ "ขาด JE"
    [InlineData(DocumentStatus.Voided)]       // ยกเลิกแล้ว — ไม่มี JE ที่มีผลโดยถูกต้อง
    public void Unissued_or_voided_documents_are_not_reported_missing(DocumentStatus status)
    {
        var d = MissingJournalRepair.Decide(Facts(s: status));
        Assert.False(d.Missing);
        Assert.False(d.CanRepair);
    }

    [Fact]
    public void Documents_that_never_post_or_already_have_a_live_je_are_left_alone()
    {
        Assert.False(MissingJournalRepair.Decide(Facts(t: DocumentType.Quotation, s: DocumentStatus.Approved)).Missing);
        Assert.False(MissingJournalRepair.Decide(Facts(hasJe: true)).CanRepair);
        Assert.False(MissingJournalRepair.Decide(Facts(t: DocumentType.Receipt, settlementReceipt: true)).Missing);
        Assert.False(MissingJournalRepair.Decide(Facts(t: DocumentType.TaxInvoice, replaces: true)).Missing);
    }

    [Fact]
    public void Closed_period_is_refused_with_a_way_forward()
    {
        var d = MissingJournalRepair.Decide(Facts(locked: "ก.ย. 2569"));
        Assert.True(d.Missing);
        Assert.False(d.CanRepair);
        Assert.Contains("ก.ย. 2569", d.Message);
        Assert.Contains("เปิดงวด", d.Message);
    }

    [Fact]
    public void Remitted_or_recognized_pp36_period_is_refused_not_silently_posted()
    {
        var remitted = MissingJournalRepair.Decide(Facts(pp36: "09/2026", remitted: true));
        Assert.True(remitted.Missing);
        Assert.False(remitted.CanRepair);
        Assert.Contains("นำส่งแล้ว", remitted.Message);
        Assert.Contains("ใบสำคัญทั่วไป", remitted.Message);

        var recognized = MissingJournalRepair.Decide(Facts(pp36: "09/2026", recognized: true));
        Assert.False(recognized.CanRepair);
        Assert.Contains("รับรู้", recognized.Message);
    }

    [Fact]
    public void Scanner_fix_points_to_the_repair_tool_not_void_and_reapprove()
    {
        Assert.Contains("ลงบัญชีให้ใบนี้", MissingJournalRepair.ScannerFix);
        Assert.DoesNotContain("ยกเลิกเอกสารแล้วอนุมัติใหม่", MissingJournalRepair.ScannerFix);
    }
}

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
    public void Reapply_restores_only_the_fields_the_caller_changed_not_a_stale_copy_of_the_whole_row()
    {
        // ฝ่ายค้าน P2-1: ช่องที่ผู้เรียกไม่ได้แตะ แต่คนอื่นเปลี่ยนในฐานระหว่างนั้น ต้องได้ค่าจากฐาน ไม่ใช่ค่าเก่าใน snapshot
        using var db = NewContext();
        var co = new Company { Name = "บริษัทเดิม", TaxId = "0105556000001" };
        db.Attach(co);
        co.NameEn = "caller-pending";
        var snap = TrackedChangeRevert.Capture(db);
        db.ChangeTracker.AcceptAllChanges();
        TrackedChangeRevert.DetachSince(db, snap.Entities);
        // จำลอง reload: ฐานมี TaxId ใหม่จากผู้ใช้อีกคน (ผู้เรียกไม่ได้แตะ TaxId)
        var e = db.Entry(co);
        e.CurrentValues[nameof(Company.NameEn)] = null;
        e.CurrentValues[nameof(Company.TaxId)] = "0105556099999";
        e.OriginalValues.SetValues(e.CurrentValues);
        e.State = EntityState.Unchanged;

        TrackedChangeRevert.ReapplyPending(db, snap);

        Assert.Equal("caller-pending", co.NameEn);
        Assert.Equal("0105556099999", co.TaxId);               // ไม่ถูกทับด้วยค่าเก่า "0105556000001"
        Assert.False(e.Property(nameof(Company.TaxId)).IsModified);
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

    // ── (ข) ข้อความล้มดังตัวเดียว — สถานะจริงสี่แบบ (ฝ่ายค้าน P1-B) ─────────────────────────────

    [Theory]
    [InlineData(DocumentStatus.Draft, true, true, AutoApproveFailureKind.StillDraft)]
    [InlineData(DocumentStatus.WaitingApproval, false, true, AutoApproveFailureKind.StillDraft)]
    [InlineData(DocumentStatus.Paid, true, true, AutoApproveFailureKind.Committed)]
    [InlineData(DocumentStatus.Approved, false, false, AutoApproveFailureKind.Committed)]          // ชนิดที่ไม่ลงบัญชี
    [InlineData(DocumentStatus.Paid, false, true, AutoApproveFailureKind.CommittedWithoutJournal)] // ใบค้างก่อนรอบแก้
    [InlineData(DocumentStatus.Voided, false, true, AutoApproveFailureKind.Closed)]
    [InlineData(DocumentStatus.Rejected, false, true, AutoApproveFailureKind.Closed)]
    public void Kind_is_decided_from_the_actual_status_and_journal(DocumentStatus status, bool hasJe, bool expects, AutoApproveFailureKind expected)
        => Assert.Equal(expected, AutoApproveFailure.Classify(status, hasJe, expects));

    [Fact]
    public void Message_says_draft_and_gives_the_next_step_when_nothing_was_posted()
    {
        var m = AutoApproveFailure.Message("สร้างใบสำคัญจ่ายเงินสด", "DRAFT-1", AutoApproveFailureKind.StillDraft,
            "JE ของ PV ไม่ผ่านการตรวจโครงสร้างบัญชี — [JE-NO-COUNTERPART]", DocumentStatus.Draft);
        Assert.Contains("ไม่สำเร็จ", m);
        Assert.Contains("JE-NO-COUNTERPART", m);
        Assert.Contains("ยังไม่ลงบัญชี", m);
        Assert.Contains("อนุมัติ", m);
    }

    [Fact]
    public void Message_never_claims_draft_or_posted_when_it_is_not_true()
    {
        // ขั้นหลัง commit ล้ม — ห้ามบอก "ยังเป็นร่าง" (ผู้ใช้จะอนุมัติซ้ำ/สร้างใบใหม่)
        var committed = AutoApproveFailure.Message("LINE ปุ่มอนุมัติ", "PV-202609-0001", AutoApproveFailureKind.Committed, "LINE timeout", DocumentStatus.Paid);
        Assert.DoesNotContain("ยังเป็นร่าง", committed);
        Assert.Contains("ลงบัญชีแล้ว", committed);
        Assert.Contains("ไม่ต้องอนุมัติซ้ำ", committed);
        // สถานะอนุมัติแต่ไม่มี JE — ห้ามบอก "ลงบัญชีแล้ว" · ชี้เครื่องมือซ่อม
        var noJe = AutoApproveFailure.Message("อนุมัติหลายใบ", "PV-202609-0001", AutoApproveFailureKind.CommittedWithoutJournal, "x", DocumentStatus.Paid);
        Assert.DoesNotContain("อนุมัติและลงบัญชีแล้ว", noJe);
        Assert.Contains("ลงบัญชีให้ใบนี้", noJe);
        // ยกเลิก/ปฏิเสธ — ไม่ใช่ทั้ง "ยังเป็นร่าง" และ "ลงบัญชีแล้ว"
        var closed = AutoApproveFailure.Message("อนุมัติหลายใบ", "PV-202609-0001", AutoApproveFailureKind.Closed, "x", DocumentStatus.Voided);
        Assert.DoesNotContain("ยังเป็นร่าง", closed);
        Assert.DoesNotContain("ลงบัญชีแล้ว", closed);
        Assert.Contains("Voided", closed);
        Assert.Contains("ไม่ทราบสาเหตุ", AutoApproveFailure.Message("x", "y", AutoApproveFailureKind.StillDraft, "  ", DocumentStatus.Draft));
        Assert.True(new AutoApproveFailureOutcome(AutoApproveFailureKind.CommittedWithoutJournal, "x", "m").Committed);
        Assert.False(new AutoApproveFailureOutcome(AutoApproveFailureKind.Closed, "x", "m").Committed);
        Assert.False(new AutoApproveFailureOutcome(AutoApproveFailureKind.Closed, "x", "m").StillDraft);
    }

    // P2-6 — อนุมัติสำเร็จภายหลัง: ปิดหมายเหตุด้วย "แก้แล้ว" (ไม่ลบ) · ไม่มีหมายเหตุล้ม ⇒ ไม่แตะ
    [Fact]
    public void Later_successful_approval_appends_resolved_once_and_keeps_the_trace()
    {
        var at = new DateTime(2026, 10, 2, 3, 0, 0, DateTimeKind.Utc);
        var fail = AutoApproveFailure.Message("สร้างใบสำคัญจ่ายเงินสด", "DRAFT-1", AutoApproveFailureKind.StillDraft, "JE-NO-COUNTERPART", DocumentStatus.Draft);
        var notes = "หมายเหตุเดิม · " + fail;
        var resolved = AutoApproveFailure.MarkResolved(notes, at, "นักบัญชี");
        Assert.StartsWith(notes, resolved);                     // ร่องรอยเดิมอยู่ครบ
        Assert.Contains("แก้แล้ว", resolved);
        Assert.Contains("2026-10-02 10:00", resolved);          // เวลาไทย
        Assert.Equal(resolved, AutoApproveFailure.MarkResolved(resolved, at, "นักบัญชี"));   // ซ้ำ = ไม่ต่ออีก
        // ล้มใหม่หลังแก้แล้ว ⇒ ปิดได้อีกรอบ
        var again = resolved + " · " + fail;
        Assert.NotEqual(again, AutoApproveFailure.MarkResolved(again, at, "นักบัญชี"));
        // หมายเหตุคำเตือนของ PV เงินสด (รูปเดิม) ก็นับเป็นหมายเหตุล้ม
        Assert.Contains("แก้แล้ว", AutoApproveFailure.MarkResolved("— อนุมัติอัตโนมัติไม่สำเร็จ: มีคำเตือนที่ต้องมีคนรับทราบ —\n• x", at, "a"));
    }

    [Fact]
    public void Notes_without_a_failure_are_untouched_by_mark_resolved()
    {
        var at = DateTime.UtcNow;
        Assert.Null(AutoApproveFailure.MarkResolved(null, at, "a"));
        Assert.Equal("ปกติ", AutoApproveFailure.MarkResolved("ปกติ", at, "a"));
        Assert.Equal("อนุมัติอัตโนมัติสำเร็จ", AutoApproveFailure.MarkResolved("อนุมัติอัตโนมัติสำเร็จ", at, "a"));
    }

    // ── (ค) ตัวตัดสินเครื่องมือลงบัญชีย้อนหลัง ─────────────────────────────────────────────────

    private static MissingJournalFacts Facts(
        DocumentType t = DocumentType.PaymentVoucher, DocumentStatus s = DocumentStatus.Paid,
        bool hasJe = false, string? locked = null, string? pp36 = null, bool remitted = false, bool recognized = false,
        bool settlementReceipt = false, bool replaces = false, IReadOnlyList<string>? sideEffects = null)
        => new(t, s, settlementReceipt, replaces, hasJe, locked, pp36, remitted, recognized, sideEffects ?? Array.Empty<string>());

    // ── P1-A (ฝ่ายค้าน): ใบที่การอนุมัติมีผลนอก JE ต้องถูกปฏิเสธ — ใบเดี่ยว (PV-20260901-0001) ยังซ่อมได้ ─────────

    [Fact]
    public void Standalone_cash_pv_like_the_real_case_has_no_side_effects_and_can_be_repaired()
    {
        var none = MissingJournalRepair.SideEffectsOf(false, false, false, false, false, false, false);
        Assert.Empty(none);
        Assert.True(MissingJournalRepair.Decide(Facts(pp36: "09/2026", sideEffects: none)).CanRepair);
    }

    [Theory]
    [InlineData(0, "ใบต้นทาง")]
    [InlineData(1, "หัก ณ ที่จ่าย")]
    [InlineData(2, "สต็อก")]
    [InlineData(3, "สินทรัพย์ถาวร")]
    [InlineData(4, "ใบแทน")]
    [InlineData(5, "โครงการ")]
    [InlineData(6, "มัดจำ")]
    public void Each_side_effect_outside_the_journal_is_refused_with_a_way_forward(int which, string label)
    {
        var flags = new bool[7];
        flags[which] = true;
        var effects = MissingJournalRepair.SideEffectsOf(flags[0], flags[1], flags[2], flags[3], flags[4], flags[5], flags[6]);
        Assert.Single(effects);
        Assert.Contains(label, effects[0]);
        var d = MissingJournalRepair.Decide(Facts(sideEffects: effects));
        Assert.True(d.Missing);
        Assert.False(d.CanRepair);
        Assert.Contains(label, d.Message);
        Assert.Contains("ใบสำคัญทั่วไป", d.Message);
    }

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

using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบส่งออก 2026-10-15 <b>รอบ 5</b> (ฝ่ายค้านบน eec6947d · erp-review/2026-10-14/EXPORT_AUDIT.md §"รอบ 5") — ตัวตัดสิน pure ของแต่ละข้อ
/// (เคสที่พังกลับมาถูก + เคสที่ถูกอยู่แล้วไม่ถูกแตะ) · หลักฐานบนฐานจริงอยู่ที่ Db/AdversarialExportRound5DbTests
/// </summary>
public class AdversarialExportRound5Tests
{
    // ───────── P1 บรรทัดผังที่ลบ + ตัวกลับรายการ + ใบปิดปี ─────────

    [Fact]
    public void ตัวกลับรายการ_บรรทัดหายระหว่างโหลด_ล้มก่อนเขียน_บรรทัดครบลงตัว_ผ่าน()
    {
        // ใบปิดปีรอบ 4: Dr รายได้ 8,000 · Cr ค่าใช้จ่าย(ผังที่ลบ) 1,000 · Cr กำไรสะสม 7,000 — ThenInclude ผังทิ้งบรรทัดผังที่ลบ ⇒ โหลดได้ 2 บรรทัด 8,000/7,000
        var dropped = JournalLineAccounts.ReversalMismatch("JV-0001", 2, 8_000m, 7_000m, 3, 8_000m, 8_000m);
        Assert.NotNull(dropped);
        Assert.Contains("ยังไม่มีอะไรถูกบันทึก", dropped);
        Assert.Contains("3 บรรทัด", dropped);
        // ทิศตรงข้าม: โหลดครบทุกบรรทัด + ลงตัว ⇒ กลับรายการได้ (ใบปกติไม่ถูกแตะ)
        Assert.Null(JournalLineAccounts.ReversalMismatch("JV-0001", 3, 8_000m, 8_000m, 3, 8_000m, 8_000m));
        // ใบเดิมไม่ลงตัว (ข้อมูลเสีย) ⇒ ไม่เขียนตัวกลับที่ไม่ลงตัวซ้ำ
        Assert.NotNull(JournalLineAccounts.ReversalMismatch("JV-0002", 2, 1_000m, 900m, 2, 1_000m, 900m));
        // ปัดเศษ 2 ตำแหน่ง (AwayFromZero) ไม่ฟ้องเท็จ
        Assert.Null(JournalLineAccounts.ReversalMismatch("JV-0003", 2, 100.004m, 100.001m, 2, 100.001m, 100.004m));
    }

    [Fact]
    public void ชื่อผังที่ลบ_มีป้าย_ผังปกติไม่มี()
    {
        Assert.Equal("ค่าเดินทาง" + JournalLineAccounts.DeletedSuffix,
            JournalLineAccounts.DisplayName(new ChartOfAccount { AccountName = "ค่าเดินทาง", IsDeleted = true }));
        Assert.Equal("เงินสด", JournalLineAccounts.DisplayName(new ChartOfAccount { AccountName = "เงินสด" }));
    }

    [Fact]
    public void ข้อความด่านใบปิดปี_บอกปีพศ_เหตุผล_และทางไปต่อ()
    {
        var msg = ClosingEntryLock.BlockMessage("กลับรายการ", new (string, int?)[] { ("JV-2024-12-001", 2024), ("CL-202305", null) });
        Assert.StartsWith("กลับรายการใบปิดบัญชีไม่ได้", msg);
        Assert.Contains("ใบปิดบัญชีสิ้นปี 2567", msg);
        Assert.Contains("CL-202305 (ใบปิดบัญชี)", msg);
        Assert.Contains("ยังไม่มีอะไรถูกบันทึก", msg);
        Assert.Contains("ใบสำคัญปรับปรุงกำไรสะสม", msg);   // ทางไปต่อที่ทำได้จริงวันนี้
        Assert.Contains("ม.11", msg);
    }

    // ───────── P2-2 งวด ม.ค. ที่ยกตอนบริษัทยังเป็นปีปฏิทิน ≠ ยกยอดรุ่นเก่า ─────────

    private static DateTime U(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ยกยอดรุ่นเก่า_ปิดก่อนการแก้และรอบไม่ใช่ปีปฏิทินตอนปิด_เท่านั้น()
    {
        var none = Array.Empty<(DateTime, int)>();
        // รุ่นเก่าจริง: ปิดก่อน de823512 · รอบ เม.ย. ตอนนี้ · ไม่มีประวัติเปลี่ยน ⇒ ถือว่ารอบ เม.ย. ตอนปิด ⇒ ยังถูกบล็อก/รายงาน
        Assert.True(OpeningRoll.IsLegacyCalendarRoll(U(2025, 1, 15), 4, none));
        // บริษัทปีปฏิทินที่ปิดปีแล้วค่อยเปลี่ยนเป็น เม.ย. (audit: ค่าเดิม 1 หลังการปิด) ⇒ ไม่ใช่รุ่นเก่า (เดิมถูกบล็อก + ปุ่มซ่อมเขียนประวัติใหม่)
        Assert.False(OpeningRoll.IsLegacyCalendarRoll(U(2025, 1, 15), 4, new[] { (U(2025, 2, 1), 1) }));
        // ทิศตรงข้าม: ตอนปิดเป็นรอบ เม.ย. แล้วค่อยเปลี่ยนเป็น ก.ค. (ค่าเดิม 4) ⇒ ยังเป็นรุ่นเก่า
        Assert.True(OpeningRoll.IsLegacyCalendarRoll(U(2025, 1, 15), 7, new[] { (U(2025, 2, 1), 4) }));
        // การเปลี่ยน<b>ก่อน</b>การปิดไม่ใช่หลักฐานของตอนปิด (ใช้ค่าปัจจุบัน/การเปลี่ยนครั้งแรกหลังปิด)
        Assert.True(OpeningRoll.IsLegacyCalendarRoll(U(2025, 1, 15), 4, new[] { (U(2024, 6, 1), 1) }));
        // ยกหลังการแก้รอบ 2 ⇒ แถว ม.ค. = บริษัทปีปฏิทินตอนยก ⇒ ไม่ใช่รุ่นเก่า
        Assert.False(OpeningRoll.IsLegacyCalendarRoll(OpeningRoll.CalendarRollFixedAtUtc.AddMinutes(1), 4, none));
    }

    [Fact]
    public void เดือนเริ่มรอบตอนปิด_ใช้ค่าเดิมของการเปลี่ยนครั้งแรกหลังการปิด()
    {
        var changes = new[] { (U(2025, 3, 1), 1), (U(2025, 5, 1), 4) };   // 1→4 (มี.ค.) แล้ว 4→7 (พ.ค.) · ตอนนี้ 7
        Assert.False(OpeningRoll.IsLegacyCalendarRoll(U(2025, 1, 1), 7, changes));   // ปิด ม.ค. ตอนเป็นปีปฏิทิน (ค่าเดิมของการเปลี่ยนครั้งแรกหลังปิด = 1)
        Assert.True(OpeningRoll.IsLegacyCalendarRoll(U(2025, 4, 1), 7, changes));    // ปิด เม.ย. ตอนเป็นรอบ เม.ย. (ค่าเดิม = 4)
        Assert.True(OpeningRoll.IsLegacyCalendarRoll(U(2025, 6, 1), 7, changes));    // ปิดหลังการเปลี่ยนทั้งหมด = ค่าปัจจุบัน 7
        Assert.False(OpeningRoll.IsLegacyCalendarRoll(U(2025, 6, 1), 1, changes));   // ค่าปัจจุบันเป็นปีปฏิทิน
    }

    [Fact]
    public void ข้อความด่านเปลี่ยนเดือนเริ่มรอบ_อ้างมาตรา_บอกปีที่ปิด_และทางไปต่อ()
    {
        var msg = OpeningRoll.FiscalYearStartMonthLockedMessage(1, 4, new[] { 2024, 2023 });
        Assert.Contains("เดือน 1 เป็นเดือน 4", msg);
        Assert.Contains("2566, 2567", msg);
        Assert.Contains("ม.11", msg);
        Assert.Contains("ยังไม่มีอะไรถูกบันทึก", msg);
        Assert.Contains("ผู้ดูแลระบบ", msg);
    }

    // ───────── P2-3 กู้คืนผังใต้ผังแม่ที่กลายเป็นผังลงรายการ ─────────

    private static readonly Guid H113 = Guid.NewGuid(), L11310 = Guid.NewGuid(), Rev = Guid.NewGuid();

    private static List<ChartNode> Chart(bool leafHasChild = false)
    {
        var chart = new List<ChartNode>
        {
            new(H113, "113", AccountType.Asset, null, 3, false),
            new(L11310, "11310", AccountType.Asset, H113, 4, true),
            new(Rev, "41000", AccountType.Revenue, null, 4, false),
        };
        if (leafHasChild) chart.Add(new(Guid.NewGuid(), "113102", AccountType.Asset, L11310, 5, false));
        return chart;
    }

    [Fact]
    public void กู้คืนใต้ผังลงรายการ_ถูกบล็อก_เสนอหัวบัญชีถัดไป()
    {
        var chart = Chart();
        var leaf = chart.Single(c => c.Id == L11310);
        // ผังลงรายการของแม่แบบ (ไม่มีลูก) ⇒ รับลูกไม่ได้
        Assert.NotNull(ChartParentLink.RestoreParentBlockedReason(leaf, false, false, "113101"));
        // ผังของผู้ใช้ที่ไม่มีลูกแต่ถูกใช้ลงรายการแล้ว ⇒ รับลูกไม่ได้
        var userLeaf = new ChartNode(Guid.NewGuid(), "11500", AccountType.Asset, H113, 4, false);
        Assert.Contains("ถูกใช้อยู่แล้ว", ChartParentLink.RestoreParentBlockedReason(userLeaf, false, true, "115001"));
        // หัวบัญชีที่เสนอ = 113 (prefix ถัดไป · ไม่ใช่ผังลงรายการ)
        var child = new ChartNode(Guid.NewGuid(), "113101", AccountType.Asset, L11310, 5, false);
        Assert.Equal(H113, ChartParentLink.RestoreAlternativeParent(child, chart, new HashSet<Guid> { L11310 }, L11310)?.Id);
        // ไม่มีหัวบัญชี prefix ⇒ ไม่เสนอ (ผู้ใช้เลือกเอง)
        var orphan = new ChartNode(Guid.NewGuid(), "119001", AccountType.Asset, null, 5, false);
        Assert.Null(ChartParentLink.RestoreAlternativeParent(orphan, chart, new HashSet<Guid>(), null));
    }

    [Fact]
    public void กู้คืนใต้ผังแม่ที่ยังมีลูกหรือไม่ถูกใช้_ไม่ถูกบล็อก()
    {
        var chart = Chart(leafHasChild: true);
        var leaf = chart.Single(c => c.Id == L11310);
        // ผังแม่มีลูกอยู่แล้ว (เป็นบัญชีคุมอยู่แล้ว) ⇒ รับได้ แม้เป็นผังระบบ/ถูกใช้
        Assert.Null(ChartParentLink.RestoreParentBlockedReason(leaf, true, true, "113101"));
        // ผังของผู้ใช้ที่ไม่มีลูกและไม่ถูกใช้ ⇒ รับได้ (เดิม)
        var userLeaf = new ChartNode(Guid.NewGuid(), "11500", AccountType.Asset, H113, 4, false);
        Assert.Null(ChartParentLink.RestoreParentBlockedReason(userLeaf, false, false, "115001"));
    }
}

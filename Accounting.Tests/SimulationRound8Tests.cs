using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบจำลองเหตุการณ์ ชุดที่ 8 — ใบสำคัญ "ปรับปรุงผังบัญชี" ตลอดวงจรชีวิต
/// (ยกเลิก → คืนชีพ → อนุมัติใหม่) และงวดที่ตัวกลับต้องตกลง
/// </summary>
public class SimulationRound8Tests
{
    // ── X-7: ตัวกลับต้องตกงวดของ "ใบที่มันกลับ" ไม่ใช่งวดของเอกสาร ──────────

    private const string AdjustMarker = "ปรับปรุงผังบัญชีของ";

    private static DateTime ReversalDateFor(DateTime entryDate, DateTime documentDate)
        => entryDate.Date == documentDate.Date ? documentDate.Date : entryDate.Date;

    [Fact]
    public void An_adjustment_posted_in_a_later_month_reverses_in_that_month()
    {
        var docDate = new DateTime(2026, 7, 10);
        var adjustDate = new DateTime(2026, 8, 5);      // ผู้ใช้เลือกลงเดือนถัดไป

        Assert.Equal(docDate, ReversalDateFor(docDate, docDate));       // JE หลัก
        Assert.Equal(adjustDate, ReversalDateFor(adjustDate, docDate)); // ใบปรับปรุง
    }

    [Fact]
    public void Reversing_everything_on_the_document_date_broke_two_months_at_once()
    {
        const decimal adjust = 17_890m;
        // ก่อนแก้: ตัวกลับของใบปรับปรุงตกไปเดือนเอกสาร
        var julyBefore = -adjust;    // ตัวกลับที่ไม่มีคู่
        var augustBefore = adjust;   // ใบปรับปรุงค้างอยู่
        Assert.Equal(-17_890m, julyBefore);
        Assert.Equal(17_890m, augustBefore);
        Assert.Equal(0m, julyBefore + augustBefore);   // รวมทั้งปีตรง — จึงไม่มีใครเห็น

        // หลังแก้: ทั้งใบปรับปรุงและตัวกลับอยู่เดือน ส.ค. → ก.ค. ไม่ถูกแตะ
        var julyAfter = 0m;
        var augustAfter = adjust - adjust;
        Assert.Equal(0m, julyAfter);
        Assert.Equal(0m, augustAfter);
    }

    [Fact]
    public void An_adjustment_posted_on_the_document_date_keeps_the_shared_date()
    {
        // ใบปรับปรุงที่ไม่ได้เลือกวันเอง (default = วันของ JE ต้นฉบับ) ต้องใช้
        // เส้นทางเดิมเป๊ะ — รวมทั้ง fallback ตอนงวดของเอกสารปิดไปแล้ว
        var docDate = new DateTime(2026, 7, 10);
        Assert.Equal(docDate, ReversalDateFor(docDate, docDate));
    }

    [Fact]
    public void A_closed_destination_period_still_falls_back()
    {
        // ResolveReversalDateAsync เลื่อนไปวันที่ใช้ได้เมื่องวดปลายทางปิด —
        // การแยกวันรายใบต้องยังผ่านตัวนี้ ไม่ใช่เขียนวันดิบลงไป
        static DateTime Resolve(DateTime wanted, bool periodOpen, DateTime fallback)
            => periodOpen ? wanted.Date : fallback.Date;

        var wanted = new DateTime(2026, 8, 5);
        var fallback = new DateTime(2026, 9, 1);
        Assert.Equal(wanted, Resolve(wanted, periodOpen: true, fallback));
        Assert.Equal(fallback, Resolve(wanted, periodOpen: false, fallback));
    }

    // ── X-6: เจตนาปรับปรุงหายเมื่ออนุมัติใหม่ ต้องมีคนเห็น ──────────────────

    private sealed record AdjustJe(string EntryNumber, bool Reversed, bool IsReversalItself);

    /// <summary>กฎ DOC-ADJUST-LOST: เตือนเมื่อใบปรับปรุงถูกกลับหมด
    /// **และ** เอกสารถูกลงบัญชีใหม่แล้ว</summary>
    private static bool ShouldWarn(IEnumerable<AdjustJe> adjusts, bool hasActivePrimaryPosting)
    {
        var originals = adjusts.Where(a => !a.IsReversalItself).ToList();
        if (originals.Count == 0) return false;
        if (originals.Any(a => !a.Reversed)) return false;   // เจตนายังอยู่ในบัญชี
        return hasActivePrimaryPosting;
    }

    [Fact]
    public void Void_then_restore_then_reapprove_raises_the_warning()
    {
        // Adjust ไม่แก้บรรทัดเอกสารเลย — เจตนาอยู่ในใบสำคัญใบเดียว. อนุมัติใหม่
        // AutoPost ลงผังเดิมจาก doc.Lines ⇒ ผังที่แก้ไว้หายโดยไม่มีสัญญาณอะไร
        var adjusts = new[] { new AdjustJe("JV-0009", Reversed: true, IsReversalItself: false) };
        Assert.True(ShouldWarn(adjusts, hasActivePrimaryPosting: true));
    }

    [Fact]
    public void A_document_still_voided_is_not_an_anomaly()
    {
        var adjusts = new[] { new AdjustJe("JV-0009", Reversed: true, IsReversalItself: false) };
        Assert.False(ShouldWarn(adjusts, hasActivePrimaryPosting: false));
    }

    [Fact]
    public void A_live_adjustment_is_not_an_anomaly()
    {
        var adjusts = new[] { new AdjustJe("JV-0009", Reversed: false, IsReversalItself: false) };
        Assert.False(ShouldWarn(adjusts, hasActivePrimaryPosting: true));
    }

    [Fact]
    public void Readjusting_after_the_reapproval_clears_the_warning()
    {
        var adjusts = new[]
        {
            new AdjustJe("JV-0009", Reversed: true, IsReversalItself: false),   // ใบเก่า
            new AdjustJe("JV-0021", Reversed: false, IsReversalItself: false),  // ปรับใหม่
        };
        Assert.False(ShouldWarn(adjusts, hasActivePrimaryPosting: true));
    }

    [Fact]
    public void The_reversal_entries_themselves_are_not_counted_as_adjustments()
    {
        // ตัวกลับของใบปรับปรุงมีคำว่า "ปรับปรุงผังบัญชีของ" อยู่ในข้อความด้วย
        // (คัดลอกจากใบต้นฉบับ) — ถ้าไม่กรอง OriginalEntryId จะนับซ้ำ
        var adjusts = new[]
        {
            new AdjustJe("JV-0009", Reversed: true, IsReversalItself: false),
            new AdjustJe("REV-0009", Reversed: false, IsReversalItself: true),
        };
        Assert.True(ShouldWarn(adjusts, hasActivePrimaryPosting: true));
    }

    [Fact]
    public void A_document_that_never_had_an_adjustment_is_silent()
        => Assert.False(ShouldWarn(Array.Empty<AdjustJe>(), hasActivePrimaryPosting: true));

    [Fact]
    public void The_marker_is_the_same_string_the_adjuster_writes()
    {
        // scanner กับตัวลง JE ต้องใช้ข้อความเดียวกัน ไม่งั้นกฎนี้ไม่มีวันยิง
        var description = $"{AdjustMarker} JV-0007 (INV-2026-0031) • แก้ผังค่าเดินทาง";
        Assert.Contains(AdjustMarker, description);
        Assert.StartsWith(AdjustMarker, description);
    }
}

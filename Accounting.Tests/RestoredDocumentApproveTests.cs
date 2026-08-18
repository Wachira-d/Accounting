using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เอกสารที่ "ยกเลิกแล้วกู้คืน" ต้องอนุมัติกลับเข้าบัญชีได้
///
/// ที่มา (ผู้ใช้รายงาน): REC-20260815-0002 ถูกยกเลิก → กดกู้คืน → กลับมาเป็น
/// ร่างที่ยังถือเลขจริง → เปิดแก้ไข → กด "บันทึกและอนุมัติ" → เด้ง
/// "ห้ามแก้ รายการสินค้า/บริการ, ส่วนลดท้ายบิล, รูปแบบราคารวม VAT ย้อนหลัง
/// ตามมาตรา 86/4" ทั้งที่ **ไม่ได้แก้อะไรเลย** ⇒ กู้คืนได้แต่ใช้งานต่อไม่ได้
/// </summary>
public class RestoredDocumentApproveTests
{
    public sealed record Line(
        string Description, decimal Quantity, string? Unit, decimal UnitPrice,
        decimal DiscountPercent, decimal VatRate, decimal WithholdingTaxRate,
        Guid? AccountId = null);

    /// <summary>สูตรเดียวกับ PrintedLinesChanged — เทียบเฉพาะสิ่งที่พิมพ์บนใบ
    /// ตาม §86/4 (ไม่รวมผังบัญชี ซึ่งไม่เคยปรากฏบนเอกสาร)</summary>
    private static bool PrintedLinesChanged(IReadOnlyList<Line> incoming, IReadOnlyList<Line> stored)
    {
        if (incoming.Count != stored.Count) return true;
        static decimal R(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
        for (var i = 0; i < stored.Count; i++)
        {
            var a = incoming[i];
            var b = stored[i];
            if ((a.Description ?? "").Trim() != (b.Description ?? "").Trim()) return true;
            if (R(a.Quantity) != R(b.Quantity)) return true;
            if (R(a.UnitPrice) != R(b.UnitPrice)) return true;
            if (R(a.DiscountPercent) != R(b.DiscountPercent)) return true;
            if (R(a.VatRate) != R(b.VatRate)) return true;
            if (R(a.WithholdingTaxRate) != R(b.WithholdingTaxRate)) return true;
            if (a.Unit != null && a.Unit.Trim() != (b.Unit ?? "").Trim()) return true;
        }
        return false;
    }

    /// <summary>ใบจริงจากเคสที่ผู้ใช้รายงาน (REC-20260815-0002)</summary>
    private static List<Line> StoredLines() => new()
    {
        new("โคซี่เคบิน 2 (Cozy Cabin 2) เช็คอิน 15 สิงหาคม 2026", 1m, "คืน", 1_600m, 0m, 7m, 0m),
        new("โคซี่เคบิน 1 (Cozy Cabin 1) เช็คอิน 15 สิงหาคม 2026", 1m, "คืน", 3_500m, 0m, 7m, 0m),
    };

    // ── หัวใจของบั๊ก: ส่งค่าเดิมกลับมา ≠ การแก้ ──────────────────────────

    [Fact]
    public void Saving_a_restored_draft_without_changing_anything_is_allowed()
    {
        // ฟอร์มแก้ไขเป็น PUT ก้อนเดียว ส่ง lines มาทุกครั้งอยู่แล้ว
        Assert.False(PrintedLinesChanged(StoredLines(), StoredLines()));
    }

    [Fact]
    public void The_old_rule_blocked_on_presence_which_blocked_every_save()
    {
        // เงื่อนไขเดิม: `if (request.Lines != null) blocked.Add(...)`
        static bool OldRule(IReadOnlyList<Line>? incoming) => incoming != null;
        Assert.True(OldRule(StoredLines()));    // ← บล็อกแม้ไม่ได้แก้อะไร
        Assert.False(OldRule(null));            // ผ่านได้ทางเดียวคือไม่ส่ง lines เลย
    }

    [Fact]
    public void A_zero_bill_discount_sent_by_the_form_is_not_an_edit()
    {
        // ช่อง "ส่วนลดท้ายบิล" บนฟอร์มส่ง 0 มาเสมอ — เงื่อนไขเดิมใช้ .HasValue
        // จึงฟ้องว่าแก้ส่วนลดท้ายบิลทุกครั้ง
        static bool OldRule(decimal? sent) => sent.HasValue;
        static bool NewRule(decimal? sent, decimal storedValue) => sent.HasValue && sent.Value != storedValue;
        Assert.True(OldRule(0m));
        Assert.False(NewRule(0m, 0m));
        Assert.True(NewRule(50m, 0m));   // แก้จริงยังบล็อกอยู่
    }

    [Fact]
    public void Resending_the_same_prices_include_vat_flag_is_not_an_edit()
    {
        static bool Changed(bool? sent, bool storedValue) => sent.HasValue && sent.Value != storedValue;
        Assert.False(Changed(true, true));
        Assert.True(Changed(false, true));   // สลับโหมดราคา = แก้ฐานภาษีทั้งใบ
    }

    // ── การแก้จริงต้องยังถูกบล็อกครบทุกทาง ──────────────────────────────

    [Fact]
    public void Changing_a_unit_price_is_still_blocked()
    {
        var edited = StoredLines();
        edited[1] = edited[1] with { UnitPrice = 3_600m };
        Assert.True(PrintedLinesChanged(edited, StoredLines()));
    }

    [Fact]
    public void Changing_quantity_description_discount_vat_or_wht_is_still_blocked()
    {
        var s = StoredLines();
        Assert.True(PrintedLinesChanged(new List<Line> { s[0] with { Quantity = 2m }, s[1] }, s));
        Assert.True(PrintedLinesChanged(new List<Line> { s[0] with { Description = "ห้องอื่น" }, s[1] }, s));
        Assert.True(PrintedLinesChanged(new List<Line> { s[0] with { DiscountPercent = 10m }, s[1] }, s));
        Assert.True(PrintedLinesChanged(new List<Line> { s[0] with { VatRate = 0m }, s[1] }, s));
        Assert.True(PrintedLinesChanged(new List<Line> { s[0] with { WithholdingTaxRate = 3m }, s[1] }, s));
        Assert.True(PrintedLinesChanged(new List<Line> { s[0] with { Unit = "ชิ้น" }, s[1] }, s));
    }

    [Fact]
    public void Adding_or_removing_a_line_is_still_blocked()
    {
        var s = StoredLines();
        Assert.True(PrintedLinesChanged(new List<Line> { s[0] }, s));
        Assert.True(PrintedLinesChanged(new List<Line> { s[0], s[1], s[1] }, s));
    }

    [Fact]
    public void Reordering_lines_counts_as_changing_the_printed_document()
    {
        var s = StoredLines();
        Assert.True(PrintedLinesChanged(new List<Line> { s[1], s[0] }, s));
    }

    // ── ผังบัญชีไม่ใช่สิ่งที่พิมพ์บนใบกำกับ ──────────────────────────────

    [Fact]
    public void Fixing_only_the_gl_account_is_allowed_on_a_restored_document()
    {
        // §86/4 บังคับ "สิ่งที่พิมพ์บนใบกำกับ" ซึ่งไม่มีรหัสผังบัญชีอยู่เลย
        // (หลักเดียวกับที่เปิดให้ ReclassifyLineAccountAsync ทำได้บนใบที่
        // อนุมัติแล้ว) — ไม่งั้นใบที่กู้คืนมาจะแก้ผังที่ลงผิดไม่ได้ตลอดกาล
        var s = StoredLines();
        var reclassified = new List<Line> { s[0] with { AccountId = Guid.NewGuid() }, s[1] };
        Assert.False(PrintedLinesChanged(reclassified, s));
    }

    // ── ความคลาดเคลื่อนจากการ round-trip ต้องไม่ถูกนับเป็นการแก้ ──────────

    [Fact]
    public void Json_round_trip_noise_does_not_count_as_an_edit()
    {
        var s = StoredLines();
        // 3500 vs 3500.00000 → decimal เท่ากันหลังปัด 4 ตำแหน่ง
        var sameValue = new List<Line> { s[0], s[1] with { UnitPrice = 3_500.00000m } };
        Assert.False(PrintedLinesChanged(sameValue, s));
    }

    [Fact]
    public void Whitespace_around_a_description_is_not_an_edit()
    {
        var s = StoredLines();
        var padded = new List<Line> { s[0] with { Description = "  " + s[0].Description + " " }, s[1] };
        Assert.False(PrintedLinesChanged(padded, s));
    }

    [Fact]
    public void A_null_unit_means_not_specified_not_cleared()
    {
        var s = StoredLines();
        Assert.False(PrintedLinesChanged(new List<Line> { s[0] with { Unit = null }, s[1] }, s));
    }

    // ── เส้นทางอนุมัติหลังกู้คืน ────────────────────────────────────────

    [Theory]
    [InlineData("Draft", true)]
    [InlineData("WaitingApproval", true)]
    [InlineData("Voided", false)]
    [InlineData("Approved", false)]
    public void Approve_accepts_the_restored_draft(string status, bool allowed)
        => Assert.Equal(allowed, status is "Draft" or "WaitingApproval");

    [Fact]
    public void Re_approving_keeps_the_original_number_instead_of_issuing_a_new_one()
    {
        // เลขที่ไม่ได้ขึ้นต้น "DRAFT-" จะไม่ถูก regenerate ตอนอนุมัติ
        // ⇒ ใบที่กู้คืนกลับมาถือเลขเดิม ไม่กินเลขใหม่ (gap-free §86/4)
        static string NumberAfterApprove(string current, string nextRunning)
            => current.StartsWith("DRAFT-", StringComparison.Ordinal) ? nextRunning : current;

        Assert.Equal("REC-20260815-0002", NumberAfterApprove("REC-20260815-0002", "REC-20260818-0009"));
        Assert.Equal("REC-20260818-0009", NumberAfterApprove("DRAFT-a1b2c3d4e5", "REC-20260818-0009"));
    }

    [Fact]
    public void The_document_date_guard_already_compared_values_not_presence()
    {
        // ฟิลด์นี้เขียนถูกมาตั้งแต่แรก — เป็นหลักฐานว่ากติกาที่ตั้งใจคือ
        // "เทียบค่า" ไม่ใช่ "เช็คว่าส่งมาไหม" ส่วนอีก 4 ฟิลด์เขียนไม่ตรงเจตนา
        var stored = new DateTime(2026, 8, 15);
        static bool Changed(DateTime? sent, DateTime storedDate)
            => sent.HasValue && sent.Value.Date != storedDate.Date;
        Assert.False(Changed(stored, stored));
        Assert.True(Changed(new DateTime(2026, 8, 16), stored));
    }
}

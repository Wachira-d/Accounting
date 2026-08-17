using Xunit;

namespace Accounting.Tests;

/// <summary>
/// วันที่ลงรายการกลับบัญชีตอนยกเลิกเอกสาร — ต้องอยู่ **งวดเดียวกับเอกสาร**
/// ไม่ใช่วันที่กดยกเลิก
///
/// ที่มา: ยกเลิกใบของเดือนก่อน แล้ว reversal JE ไปตกเดือนปัจจุบัน ⇒ ผิดสองเดือน
/// พร้อมกัน — เดือนเก่าค้างยอดที่ไม่มีอยู่จริง (ขาย/ลูกหนี้/ภาษีขายเกิน) และ
/// เดือนใหม่มียอดติดลบที่ไม่มีที่มา (งบเปรียบเทียบรายเดือนอ่านไม่ได้)
/// </summary>
public class VoidReversalDateTests
{
    private enum PeriodState { Open, Closed, NoPeriod }

    /// <summary>mirror ของ DocumentService.ResolveReversalDateAsync</summary>
    private static (DateTime Date, bool Fallback) Resolve(
        DateTime wanted, PeriodState state, DateTime today)
        => state == PeriodState.Closed ? (today.Date, true) : (wanted.Date, false);

    private static readonly DateTime Today = new(2026, 8, 17);

    [Fact]
    public void Default_uses_document_date_not_click_date()
    {
        var docDate = new DateTime(2026, 7, 24);
        var (date, fallback) = Resolve(docDate, PeriodState.Open, Today);
        Assert.Equal(docDate, date);          // ไม่ใช่ 17/08
        Assert.False(fallback);
        Assert.Equal(7, date.Month);          // อยู่งวดเดียวกับเอกสาร
    }

    [Fact]
    public void Explicit_date_wins_over_document_date()
    {
        var chosen = new DateTime(2026, 8, 1);
        var (date, _) = Resolve(chosen, PeriodState.Open, Today);
        Assert.Equal(chosen, date);
    }

    [Fact]
    public void Closed_period_falls_back_to_today_and_reports_it()
    {
        // ห้ามยัดเข้างวดปิด (TAS 1 งวดที่ปิดแล้วแก้ไม่ได้) — แต่ก็ห้ามบล็อกการ
        // ยกเลิกทั้งหมด ⇒ ตกกลับวันนี้ **พร้อมบอกเหตุผล** (ห้ามเงียบ)
        var docDate = new DateTime(2026, 1, 15);
        var (date, fallback) = Resolve(docDate, PeriodState.Closed, Today);
        Assert.Equal(Today, date);
        Assert.True(fallback);
    }

    [Fact]
    public void No_fiscal_period_defined_still_uses_document_date()
    {
        // บริษัทที่ยังไม่ตั้งงวดบัญชี — ไม่มีอะไรให้บล็อก ใช้วันที่เอกสารตามปกติ
        var docDate = new DateTime(2026, 6, 30);
        var (date, fallback) = Resolve(docDate, PeriodState.NoPeriod, Today);
        Assert.Equal(docDate, date);
        Assert.False(fallback);
    }

    [Fact]
    public void Time_component_is_dropped()
    {
        var docDate = new DateTime(2026, 7, 24, 23, 59, 58);
        var (date, _) = Resolve(docDate, PeriodState.Open, Today);
        Assert.Equal(new DateTime(2026, 7, 24), date);
        Assert.Equal(TimeSpan.Zero, date.TimeOfDay);
    }

    /// <summary>เครื่องมือแก้ย้อนหลัง (RedateVoidReversalAsync) — ย้ายเฉพาะ
    /// "ตัวกลับ" ยอดสุทธิของเอกสารต้องไม่เปลี่ยน</summary>
    [Fact]
    public void Redating_reversal_never_changes_net_amount()
    {
        var original = new { Debit = 1000m, Credit = 0m, Date = new DateTime(2026, 7, 24) };
        var reversalBefore = new { Debit = 0m, Credit = 1000m, Date = new DateTime(2026, 8, 17) };
        var reversalAfter = new { reversalBefore.Debit, reversalBefore.Credit, Date = original.Date };

        // ยอดสุทธิ (Dr − Cr) เท่าเดิมทั้งก่อนและหลังย้ายวันที่
        var netBefore = original.Debit - original.Credit + reversalBefore.Debit - reversalBefore.Credit;
        var netAfter = original.Debit - original.Credit + reversalAfter.Debit - reversalAfter.Credit;
        Assert.Equal(0m, netBefore);
        Assert.Equal(netBefore, netAfter);

        // แต่ "งวด" เปลี่ยน: ก่อนแก้ กระจายสองเดือน / หลังแก้ อยู่เดือนเดียว
        Assert.NotEqual(original.Date.Month, reversalBefore.Date.Month);
        Assert.Equal(original.Date.Month, reversalAfter.Date.Month);
    }

    [Theory]
    [InlineData(7, 7, 0)]    // ต้นฉบับ ก.ค. + ตัวกลับ ก.ค. → ผลกระทบสุทธิเดือน ก.ค. = 0
    [InlineData(7, 8, 1000)] // ตัวกลับข้ามไป ส.ค. → ก.ค. ค้าง 1000 (และ ส.ค. −1000)
    public void Cross_month_reversal_leaves_a_phantom_balance(
        int origMonth, int revMonth, decimal julyResidual)
    {
        const decimal amount = 1000m;
        var julyNet = (origMonth == 7 ? amount : 0m) - (revMonth == 7 ? amount : 0m);
        Assert.Equal(julyResidual, julyNet);
    }

    // ── เอกสารใบเดียวมีตัวกลับหลายใบ ─────────────────────────────────────
    // ที่มา (คำถามผู้ใช้): "แก้วันที่กลับบัญชี ระบบไปแก้วันอันไหน มีหลายรายการ"
    // ใบซื้อลง 1 ก.ค. แล้วจ่ายชำระ 17 ก.ค. → ยกเลิกทีเดียวได้ตัวกลับ 2 ใบ
    // ที่ควรกลับไปอยู่ "วันของใบต้นฉบับตัวเอง" คนละวัน ไม่ใช่ยัดรวมวันเดียว

    private sealed record Rev(string Number, DateTime Current, DateTime? OriginalDate);

    /// <summary>mirror ของ DocumentService.ResolveRedateTarget</summary>
    private static DateTime Target(Rev r, DateTime? explicitDate, DateTime docDate)
        => explicitDate?.Date ?? r.OriginalDate?.Date ?? docDate.Date;

    [Fact]
    public void Each_reversal_defaults_to_its_own_original_date()
    {
        var docDate = new DateTime(2026, 7, 1);
        var uv = new Rev("UV-202608-0032", new DateTime(2026, 8, 17), new DateTime(2026, 7, 1));
        var pv = new Rev("PV-202608-0203", new DateTime(2026, 8, 17), new DateTime(2026, 7, 17));

        Assert.Equal(new DateTime(2026, 7, 1), Target(uv, null, docDate));
        Assert.Equal(new DateTime(2026, 7, 17), Target(pv, null, docDate));   // ไม่ใช่ 1 ก.ค.
    }

    [Fact]
    public void Explicit_date_forces_every_reversal_onto_the_same_day()
    {
        var docDate = new DateTime(2026, 7, 1);
        var chosen = new DateTime(2026, 7, 31);
        var uv = new Rev("UV", new DateTime(2026, 8, 17), new DateTime(2026, 7, 1));
        var pv = new Rev("PV", new DateTime(2026, 8, 17), new DateTime(2026, 7, 17));

        Assert.Equal(chosen, Target(uv, chosen, docDate));
        Assert.Equal(chosen, Target(pv, chosen, docDate));
    }

    [Fact]
    public void Orphan_reversal_without_an_original_falls_back_to_document_date()
    {
        var docDate = new DateTime(2026, 7, 1);
        var orphan = new Rev("JV", new DateTime(2026, 8, 17), null);
        Assert.Equal(docDate, Target(orphan, null, docDate));
    }

    [Fact]
    public void Only_posted_reversal_entries_are_candidates()
    {
        // ตัวเลือกต้องเป็น OriginalEntryId != null && Status == Posted เท่านั้น
        // ใบต้นฉบับ (Reversed) ห้ามถูกย้าย ไม่งั้นยอดเดือนเพี้ยนทั้งคู่
        static bool IsCandidate(bool hasOriginalId, string status)
            => hasOriginalId && status == "Posted";

        Assert.True(IsCandidate(true, "Posted"));       // ตัวกลับ
        Assert.False(IsCandidate(false, "Reversed"));   // ใบต้นฉบับที่ถูกกลับแล้ว
        Assert.False(IsCandidate(false, "Posted"));     // JE ปกติที่ไม่ใช่ตัวกลับ
        Assert.False(IsCandidate(true, "Voided"));      // ตัวกลับที่ถูกยกเลิกไปแล้ว
    }

    [Fact]
    public void Per_entry_dates_keep_every_month_net_zero()
    {
        // ใบซื้อ 6,955 (1 ก.ค.) + ใบจ่าย 6,955 (17 ก.ค.) — หลังย้ายตามต้นฉบับ
        // ทั้งเดือน ก.ค. และ ส.ค. ต้องสุทธิ = 0 (ไม่มียอดผีค้างเดือนไหน)
        const decimal amt = 6955m;
        var july = amt + amt - amt - amt;   // ต้นฉบับ 2 ใบ + ตัวกลับ 2 ใบ อยู่ ก.ค. หมด
        var august = 0m;
        Assert.Equal(0m, july);
        Assert.Equal(0m, august);

        // ก่อนแก้: ตัวกลับทั้งคู่อยู่ ส.ค. → ก.ค. ค้าง +13,910 / ส.ค. −13,910
        var julyBefore = amt + amt;
        var augustBefore = -(amt + amt);
        Assert.Equal(13910m, julyBefore);
        Assert.Equal(-13910m, augustBefore);
    }

    /// <summary>ผู้ใช้พิมพ์วันที่เองรายบรรทัดในตาราง — ต้องชนะทุกค่าเริ่มต้น</summary>
    private static DateTime TargetWithOverride(
        Rev r, DateTime? perRow, DateTime? explicitAll, DateTime docDate)
        => perRow?.Date ?? Target(r, explicitAll, docDate);

    [Fact]
    public void Per_row_date_beats_both_defaults()
    {
        var docDate = new DateTime(2026, 7, 1);
        var pv = new Rev("PV", new DateTime(2026, 8, 17), new DateTime(2026, 7, 17));
        var typed = new DateTime(2026, 7, 20);

        Assert.Equal(typed, TargetWithOverride(pv, typed, null, docDate));
        // แม้จะส่งวันเดียวทั้งชุดมาด้วย ค่ารายบรรทัดก็ยังชนะ
        Assert.Equal(typed, TargetWithOverride(pv, typed, new DateTime(2026, 7, 31), docDate));
        // ไม่พิมพ์เอง → ตกไปใช้ลำดับเดิม (วันเดียวทั้งชุด → ใบต้นฉบับ → เอกสาร)
        Assert.Equal(new DateTime(2026, 7, 31),
            TargetWithOverride(pv, null, new DateTime(2026, 7, 31), docDate));
        Assert.Equal(new DateTime(2026, 7, 17), TargetWithOverride(pv, null, null, docDate));
    }

    /// <summary>ปุ่ม "📌 วันที่ใบแรก" = ใบสำคัญต้นฉบับที่เก่าที่สุดในชุดนี้</summary>
    private static DateTime FirstEntryDate(IEnumerable<Rev> revs, DateTime docDate)
    {
        var dates = revs.Where(r => r.OriginalDate.HasValue)
            .Select(r => r.OriginalDate!.Value.Date).OrderBy(d => d).ToList();
        return dates.Count > 0 ? dates[0] : docDate.Date;
    }

    [Fact]
    public void First_entry_button_picks_the_oldest_original()
    {
        var docDate = new DateTime(2026, 7, 5);
        var revs = new[]
        {
            new Rev("PV", new DateTime(2026, 8, 17), new DateTime(2026, 7, 17)),
            new Rev("UV", new DateTime(2026, 8, 17), new DateTime(2026, 7, 1)),
        };
        Assert.Equal(new DateTime(2026, 7, 1), FirstEntryDate(revs, docDate));
    }

    [Fact]
    public void First_entry_button_falls_back_to_document_date()
    {
        var docDate = new DateTime(2026, 7, 5);
        var revs = new[] { new Rev("JV", new DateTime(2026, 8, 17), null) };
        Assert.Equal(docDate, FirstEntryDate(revs, docDate));
    }

    [Fact]
    public void Setting_every_reversal_to_the_first_date_still_nets_to_zero_per_month()
    {
        // ผู้ใช้เลือก "ทุกใบ = วันที่ใบแรก (1 ก.ค.)" — ต้นฉบับยังอยู่ 1 ก.ค. และ
        // 17 ก.ค. แต่ทั้งคู่อยู่ในเดือน ก.ค. ⇒ งบรายเดือนสุทธิยังเป็น 0
        const decimal amt = 6955m;
        var julyOriginals = amt + amt;
        var julyReversals = amt + amt;   // ทั้งคู่ถูกตั้งเป็น 1 ก.ค.
        Assert.Equal(0m, julyOriginals - julyReversals);
        // (ต่างจากค่าเริ่มต้นตรงที่ "วัน" ไม่ตรงกับเหตุการณ์เท่านั้น — เดือนตรงเหมือนกัน)
    }

    [Fact]
    public void Explicit_entry_ids_must_belong_to_this_document()
    {
        // กันไม่ให้ payload รายบรรทัดกลายเป็นช่องแก้วันที่ JE ใบไหนก็ได้
        var candidates = new[] { Guid.NewGuid(), Guid.NewGuid() };
        static bool Accepts(Guid[] candidates, Guid requested) => candidates.Contains(requested);
        Assert.True(Accepts(candidates, candidates[0]));
        Assert.False(Accepts(candidates, Guid.NewGuid()));
    }

    [Fact]
    public void Preview_marks_rows_that_are_already_on_target_as_no_move()
    {
        // ใบที่วันที่ตรงอยู่แล้วต้องขึ้นว่า "ไม่ย้าย" ไม่ใช่เงียบหายไปจากตาราง
        var r = new Rev("UV", new DateTime(2026, 7, 1), new DateTime(2026, 7, 1));
        var target = Target(r, null, new DateTime(2026, 7, 1));
        Assert.Equal(r.Current, target);
        Assert.False(r.Current != target);   // WillMove = false
    }
}

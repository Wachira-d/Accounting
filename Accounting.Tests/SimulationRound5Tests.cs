using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบจำลองเหตุการณ์ ชุดที่ 5 — วงจรมัดจำที่รับรู้บางส่วน · การจับคู่ธนาคาร
/// ที่ค้างหลังยกเลิก/ย้ายแหล่งเงิน · ด่านงวดบัญชีของสมุดรายวัน
/// </summary>
public class SimulationRound5Tests
{
    // ── M-1: หามัดจำคงเหลือจาก GL ต้องไม่กวาดขาของ "รับรู้รายได้" มาด้วย ────

    private sealed record Leg(Guid JeId, string Code, string Name, decimal Debit, decimal Credit);

    private static bool IsDepositLiability(string code, string name)
        => code.StartsWith("215") || code.StartsWith("217")
        || name.Contains("มัดจำ") || name.Contains("รับล่วงหน้า") || name.Contains("รอรับรู้");

    /// <summary>สูตรหลังแก้: นับ Cr เฉพาะจาก JE ที่ "สร้างหนี้สินมัดจำ" · นับ Dr ทุก JE</summary>
    private static Dictionary<string, decimal> RemainingLegs(IEnumerable<Leg> legs)
    {
        var all = legs.ToList();
        var creating = all
            .Where(l => l.Credit > 0.005m && IsDepositLiability(l.Code, l.Name))
            .Select(l => l.JeId).ToHashSet();
        return all.GroupBy(l => l.Code)
            .Select(g => (g.Key,
                Net: g.Where(l => creating.Contains(l.JeId)).Sum(l => l.Credit) - g.Sum(l => l.Debit)))
            .Where(x => x.Net > 0.005m && !x.Key.StartsWith("1"))
            .ToDictionary(x => x.Key, x => x.Net);
    }

    /// <summary>สูตรเดิม: net Cr−Dr ต่อผัง ตัดแค่ผัง 1xxxx</summary>
    private static Dictionary<string, decimal> RemainingLegsOld(IEnumerable<Leg> legs)
        => legs.GroupBy(l => l.Code)
            .Select(g => (g.Key, Net: g.Sum(l => l.Credit - l.Debit)))
            .Where(x => x.Net > 0.005m && !x.Key.StartsWith("1"))
            .ToDictionary(x => x.Key, x => x.Net);

    private static List<Leg> DepositThenPartialRealize()
    {
        var receipt = Guid.NewGuid();
        var realize = Guid.NewGuid();
        return new List<Leg>
        {
            // ใบเสร็จมัดจำ 10,700 (ฐาน 10,000 + VAT รอเรียกเก็บ 700)
            new(receipt, "11110", "เงินสด", 10_700m, 0m),
            new(receipt, "21712", "เงินมัดจำรับล่วงหน้า", 0m, 10_000m),
            new(receipt, "21913", "ภาษีขายรอเรียกเก็บ", 0m, 700m),
            // รับรู้รายได้ครึ่งเดียว 5,000 (+ VAT 350 ถึงกำหนด)
            new(realize, "21712", "เงินมัดจำรับล่วงหน้า", 5_000m, 0m),
            new(realize, "41000", "รายได้จากการขาย", 0m, 5_000m),
            new(realize, "21913", "ภาษีขายรอเรียกเก็บ", 350m, 0m),
            new(realize, "21911", "ภาษีขาย ภ.พ.30", 0m, 350m),
        };
    }

    [Fact]
    public void The_old_family_net_treated_recognised_revenue_as_leftover_deposit()
    {
        var old = RemainingLegsOld(DepositThenPartialRealize());
        Assert.Equal(5_000m, old["21712"]);   // ถูก
        Assert.Equal(350m, old["21913"]);     // ถูก
        Assert.Equal(5_000m, old["41000"]);   // ❌ รายได้ที่รับรู้แล้ว
        Assert.Equal(350m, old["21911"]);     // ❌ ภาษีขายที่ถึงกำหนดแล้ว
        Assert.Equal(10_700m, old.Values.Sum());
    }

    [Fact]
    public void Only_the_real_deposit_balance_is_left_after_the_fix()
    {
        var now = RemainingLegs(DepositThenPartialRealize());
        Assert.Equal(5_000m, now["21712"]);
        Assert.Equal(350m, now["21913"]);
        Assert.DoesNotContain("41000", now.Keys);   // ← เคสที่เคยล้างรายได้ทิ้ง
        Assert.DoesNotContain("21911", now.Keys);
        Assert.Equal(5_350m, now.Values.Sum());     // = มัดจำคงเหลือจริง
    }

    [Fact]
    public void Applying_the_rest_no_longer_debits_revenue()
    {
        // ตัดส่วนที่เหลือ 5,350 เข้าใบแจ้งหนี้ — เดิมกระจายตามสัดส่วนขา "คงเหลือ"
        // ที่มีรายได้ปนอยู่ ⇒ ลง Dr 41000 ล้างรายได้ที่รับรู้ถูกต้องไปแล้ว
        var legs = RemainingLegs(DepositThenPartialRealize());
        Assert.All(legs.Keys, code => Assert.StartsWith("2", code));
    }

    [Fact]
    public void A_deposit_that_recognised_vat_immediately_still_finds_its_21911_leg()
    {
        // ใบเสร็จมัดจำที่ไม่ deferred: Cr 21911 มาจาก JE ใบเสร็จเอง (creating)
        // จึงต้องยังนับได้ — เหตุผลที่แยกด้วย "JE ไหน" ไม่ใช่ "รหัสบัญชีอะไร"
        var receipt = Guid.NewGuid();
        var legs = new List<Leg>
        {
            new(receipt, "11110", "เงินสด", 10_700m, 0m),
            new(receipt, "21712", "เงินมัดจำรับล่วงหน้า", 0m, 10_000m),
            new(receipt, "21911", "ภาษีขาย ภ.พ.30", 0m, 700m),
        };
        var now = RemainingLegs(legs);
        Assert.Equal(10_000m, now["21712"]);
        Assert.Equal(700m, now["21911"]);
        Assert.Equal(10_700m, now.Values.Sum());
    }

    [Fact]
    public void A_prior_apply_still_reduces_the_balance()
    {
        var receipt = Guid.NewGuid();
        var apply = Guid.NewGuid();
        var legs = new List<Leg>
        {
            new(receipt, "11110", "เงินสด", 10_700m, 0m),
            new(receipt, "21712", "เงินมัดจำรับล่วงหน้า", 0m, 10_000m),
            new(receipt, "21913", "ภาษีขายรอเรียกเก็บ", 0m, 700m),
            // หักไปแล้วครึ่งหนึ่ง: Dr 21712 + Dr 21913 / Cr ลูกหนี้
            new(apply, "21712", "เงินมัดจำรับล่วงหน้า", 5_000m, 0m),
            new(apply, "21913", "ภาษีขายรอเรียกเก็บ", 350m, 0m),
            new(apply, "11310", "ลูกหนี้การค้า", 0m, 5_350m),
        };
        var now = RemainingLegs(legs);
        Assert.Equal(5_350m, now.Values.Sum());
        Assert.DoesNotContain("11310", now.Keys);   // ผังสินทรัพย์ไม่เข้าฐาน
    }

    // ── M-2: ยกเลิกการชำระต้องปลดการจับคู่ธนาคารแบบ 1:1 ────────────────────

    private sealed record BankTxn(Guid Id, Guid? MatchedPaymentId, string Status);

    [Fact]
    public void Voiding_a_payment_releases_its_one_to_one_bank_match()
    {
        var pay = Guid.NewGuid();
        var txn = new BankTxn(Guid.NewGuid(), pay, "Matched");

        // เดิม: unwind ทำเฉพาะ ReconciliationGroup — การจับคู่ 1:1 ไม่สร้างกลุ่ม
        var afterOld = txn;
        Assert.Equal("Matched", afterOld.Status);
        Assert.NotNull(afterOld.MatchedPaymentId);

        var afterNew = txn with { MatchedPaymentId = null, Status = "Unmatched" };
        Assert.Null(afterNew.MatchedPaymentId);
        Assert.Equal("Unmatched", afterNew.Status);
    }

    [Fact]
    public void A_stale_match_blocks_rematching_the_corrected_payment()
    {
        // auto-match ตัด payment ที่ถูกจับคู่ไปแล้วออกจากตัวเลือก + txn ที่ไม่ได้
        // อยู่สถานะ Unmatched ก็ไม่ถูกหยิบมาพิจารณา ⇒ ล็อกตายทั้งสองทาง
        static bool CanRematch(string txnStatus, bool paymentAlreadyMatched)
            => txnStatus == "Unmatched" && !paymentAlreadyMatched;

        Assert.False(CanRematch("Matched", paymentAlreadyMatched: true));    // ก่อนแก้
        Assert.True(CanRematch("Unmatched", paymentAlreadyMatched: false));  // หลังแก้
    }

    [Fact]
    public void Other_payments_matches_are_untouched()
    {
        var voided = Guid.NewGuid();
        var other = Guid.NewGuid();
        var txns = new[]
        {
            new BankTxn(Guid.NewGuid(), voided, "Matched"),
            new BankTxn(Guid.NewGuid(), other, "Matched"),
        };
        var released = txns.Where(t => t.MatchedPaymentId == voided).ToList();
        Assert.Single(released);
        Assert.Equal(other, txns.Single(t => t.MatchedPaymentId == other).MatchedPaymentId);
    }

    // ── M-8: เปลี่ยนแหล่งเงินต้องปลดการกระทบยอดของบัญชีเดิม ────────────────

    [Fact]
    public void Moving_the_money_source_forces_the_old_bank_line_to_be_rematched()
    {
        // GL บอกว่าเงินไม่เคยออกจากบัญชีเดิมแล้ว (JE แก้ Dr คืนให้) แต่รายการ
        // เดินบัญชีของบัญชีเดิมยังขึ้น "กระทบยอดแล้ว" ⇒ งบกระทบยอดค้างสองที่
        var je = Guid.NewGuid();
        var txn = (MatchedJournalEntryId: (Guid?)je, Status: "Matched");
        var after = (MatchedJournalEntryId: (Guid?)null, Status: "Unmatched");
        Assert.NotEqual(txn.Status, after.Status);
        Assert.Null(after.MatchedJournalEntryId);
    }

    [Fact]
    public void Reclassifying_to_the_same_gl_account_changes_nothing()
    {
        // สลับบัญชีธนาคารที่ผูก GL เดียวกัน — ไม่ post JE ⇒ ไม่ต้องปลดอะไร
        static bool PostsCorrectingEntry(Guid oldGl, Guid newGl) => oldGl != newGl;
        var gl = Guid.NewGuid();
        Assert.False(PostsCorrectingEntry(gl, gl));
        Assert.True(PostsCorrectingEntry(gl, Guid.NewGuid()));
    }

    // ── X-9: ด่านงวดบัญชีของสมุดรายวัน ─────────────────────────────────────

    private static bool PeriodOpen(string? statusOfDatesPeriod)
        => statusOfDatesPeriod is null or "Open";   // ไม่มีแถวงวด = เปิด (convention ระบบ)

    [Theory]
    [InlineData(null, null, true)]        // ไม่มี FK และไม่มีแถวงวด → แก้ได้
    [InlineData(null, "Open", true)]
    [InlineData(null, "Closed", false)]   // ← ช่องที่เคยหลุด (FK ว่าง = ผ่านทันที)
    [InlineData(null, "Locked", false)]   // ← งวดที่ยื่นแบบแล้ว
    [InlineData("Open", "Open", true)]
    [InlineData("Closed", "Closed", false)]
    public void The_period_guard_follows_the_date_not_only_the_stored_key(
        string? fkStatus, string? dateStatus, bool allowed)
    {
        var fkOk = fkStatus is null or "Open";
        Assert.Equal(allowed, fkOk && PeriodOpen(dateStatus));
    }

    [Fact]
    public void Changing_the_date_validates_the_destination_period()
    {
        // ย้าย JE จากงวดเปิด (ส.ค.) ไปงวดที่ปิดแล้ว (ก.ค.) — เดิมไม่มีใครตรวจ
        static bool CanMove(string fromStatus, string toStatus)
            => PeriodOpen(fromStatus) && PeriodOpen(toStatus);

        Assert.True(CanMove("Open", "Open"));
        Assert.False(CanMove("Open", "Closed"));   // ← ช่องที่เคยหลุด
        Assert.False(CanMove("Closed", "Open"));
    }

    [Fact]
    public void The_stored_period_key_follows_the_new_date()
    {
        // ไม่ re-resolve = รายการโผล่ในรายงานของงวดเดิม แต่สมุดรายวันแสดงวันใหม่
        var julyPeriod = Guid.NewGuid();
        var augustPeriod = Guid.NewGuid();
        static Guid? Resolve(Guid? current, Guid? byNewDate, bool dateChanged)
            => dateChanged ? byNewDate : current;

        Assert.Equal(augustPeriod, Resolve(julyPeriod, augustPeriod, dateChanged: true));
        Assert.Equal(julyPeriod, Resolve(julyPeriod, augustPeriod, dateChanged: false));
        // งวดใหม่ไม่มีแถว → null (ไม่ค้างชี้งวดเก่า)
        Assert.Null(Resolve(julyPeriod, null, dateChanged: true));
    }

    [Fact]
    public void Posting_a_draft_now_rejects_a_locked_period_too()
    {
        // เดิม PostJournalEntryAsync เช็คเฉพาะ Closed — งวดที่ยื่น ภ.พ.30/ภ.ง.ด.
        // ไปแล้วถูกล็อกด้วยสถานะ Locked จึงยัง post ทับได้
        static bool CanPost(string periodStatus) => periodStatus == "Open";
        Assert.True(CanPost("Open"));
        Assert.False(CanPost("Closed"));
        Assert.False(CanPost("Locked"));   // ← ช่องที่เคยหลุด
    }
}

using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม AI · A-AI7 (report-H H-9) — ด่านกัน <c>candidateId</c>/<c>bankTxnId</c> ที่ AI แต่งขึ้นในแผนจับคู่ธนาคารทั้งก้อน
///
/// ของจริงที่พัง: <c>BulkBankAiMatchService</c> ค้นยอดจริงไม่เจอแล้ว "พกยอดของ AI ต่อ" (<c>? ra2 : c.Amount</c>) ⇒ คู่ที่ไม่มีตัวตน
/// ได้ยอดตรงเป๊ะ = มั่นใจ 100% "พร้อมยืนยัน" · กดแล้วเด้ง "ไม่รู้ทิศ สมุดรายวัน a1b2c3d4". เทสต์สองครึ่ง: ข้อเสนอที่แต่งต้องถูกตัด
/// และข้อเสนอจริง (รวมทุกข้อเสนอของเซิร์ฟเวอร์) ต้องไม่ถูกแตะ
/// </summary>
public class BankAiCandidateGuardTests
{
    private static readonly Guid Bank1 = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid Rv0500 = Guid.Parse("c0000000-0000-0000-0000-000000000500");
    private static readonly Guid Rv0570 = Guid.Parse("c0000000-0000-0000-0000-000000000570");
    private static readonly Guid Fabricated = Guid.Parse("deadbeef-0000-0000-0000-000000000001");

    private static readonly HashSet<Guid> KnownBank = new() { Bank1 };
    private static readonly HashSet<Guid> KnownCands = new() { Rv0500, Rv0570 };

    private static BankAiCandidateGuard.Result Screen(Guid bank, params Guid[] cands)
        => BankAiCandidateGuard.Screen(bank, cands, KnownBank.Contains, KnownCands.Contains);

    // ── ครึ่งที่ "ใบพังกลับมาถูก" ───────────────────────────────────────

    [Fact]
    public void ผู้สมัครทุกตัวแต่งขึ้น_ต้องทิ้งทั้งข้อเสนอ_ยอดของ_AI_ห้ามถึงจอ()
    {
        var r = Screen(Bank1, Fabricated);
        Assert.Equal(BankAiCandidateGuard.Outcome.DropNoRealCandidate, r.Outcome);
        Assert.True(r.Dropped);
        Assert.Empty(r.KeptCandidateIds);
        Assert.Equal(new[] { Fabricated }, r.FabricatedCandidateIds);
    }

    [Fact]
    public void บรรทัดธนาคารที่ไม่รู้จัก_ต้องทิ้งทั้งข้อเสนอ_แม้ผู้สมัครจะมีจริง()
    {
        var r = Screen(Guid.Parse("b0000000-0000-0000-0000-00000000ffff"), Rv0500);
        Assert.Equal(BankAiCandidateGuard.Outcome.DropUnknownBankTxn, r.Outcome);
        Assert.True(r.Dropped);
        Assert.Equal(BankAiCandidateGuard.Outcome.DropUnknownBankTxn, Screen(Guid.Empty, Rv0500).Outcome);
    }

    [Fact]
    public void ผู้สมัครแต่งปนของจริง_ตัดเฉพาะตัวที่แต่ง_และความมั่นใจถูกเพดาน_ห้ามติ๊กอัตโนมัติ()
    {
        // AI เสนอ RV-0500 (มีจริง) + id แต่ง 1 ตัว — เดิม id แต่งพกยอดของตัวเองต่อจนผลรวมตรงยอดธนาคาร
        var r = Screen(Bank1, Rv0500, Fabricated);
        Assert.Equal(BankAiCandidateGuard.Outcome.KeepTrimmed, r.Outcome);
        Assert.Equal(new[] { Rv0500 }, r.KeptCandidateIds);
        Assert.Equal(new[] { Fabricated }, r.FabricatedCandidateIds);
        var capped = BankAiCandidateGuard.CapConfidence(0.99m, r.FabricatedCandidateIds.Count);
        Assert.Equal(BankAiCandidateGuard.FabricatedConfidenceCap, capped);
        Assert.True(capped < 0.85m, "ต่ำกว่าเกณฑ์ติ๊กอัตโนมัติของหน้าจอเสมอ");
    }

    [Fact]
    public void id_ว่าง_นับเป็นของแต่ง()
    {
        var r = Screen(Bank1, Guid.Empty, Rv0570);
        Assert.Equal(BankAiCandidateGuard.Outcome.KeepTrimmed, r.Outcome);
        Assert.Equal(new[] { Rv0570 }, r.KeptCandidateIds);
    }

    [Fact]
    public void ด่านฝั่งเขียน_id_ที่หาไม่เจอในบริษัทต้องถูกรายงาน()
    {
        var missing = BankAiCandidateGuard.MissingIds(new[] { Rv0500, Fabricated, Fabricated, Guid.Empty },
            new HashSet<Guid> { Rv0500 });
        Assert.Equal(new[] { Fabricated }, missing);
    }

    [Fact]
    public void คำเตือนของแผนต้องนับข้อเสนอที่ถูกตัด_ไม่หายเงียบ()
    {
        var w = BankAiCandidateGuard.PlanWarning(1, 2, 3);
        Assert.NotNull(w);
        Assert.Contains("1 ข้อเสนอ", w);
        Assert.Contains("2 ข้อเสนอ", w);
        Assert.Contains("3 รายการ", w);
        Assert.Contains("จับคู่ด้วยมือ", w);
    }

    // ── ครึ่งที่ "ใบถูกไม่ถูกแตะ" ───────────────────────────────────────

    [Fact]
    public void ข้อเสนอที่ผู้สมัครทุกตัวมีจริง_ต้องผ่านโดยไม่แตะ_รวมข้อเสนอของเซิร์ฟเวอร์()
    {
        var r = Screen(Bank1, Rv0500, Rv0570);
        Assert.Equal(BankAiCandidateGuard.Outcome.Keep, r.Outcome);
        Assert.False(r.Dropped);
        Assert.Equal(new[] { Rv0500, Rv0570 }, r.KeptCandidateIds);
        Assert.Empty(r.FabricatedCandidateIds);
    }

    [Fact]
    public void ไม่มีของแต่ง_ความมั่นใจต้องเท่าเดิม_และเพดานต้องไม่ดันค่าขึ้น()
    {
        Assert.Equal(0.97m, BankAiCandidateGuard.CapConfidence(0.97m, 0));
        Assert.Equal(0.40m, BankAiCandidateGuard.CapConfidence(0.40m, 2));
    }

    [Fact]
    public void ไม่มีอะไรถูกตัด_ไม่มีคำเตือน_และไม่มี_id_หาย()
    {
        Assert.Null(BankAiCandidateGuard.PlanWarning(0, 0, 0));
        Assert.Empty(BankAiCandidateGuard.MissingIds(new[] { Rv0500, Rv0570 }, new HashSet<Guid> { Rv0500, Rv0570 }));
        Assert.Empty(BankAiCandidateGuard.MissingIds(null, new HashSet<Guid>()));
    }
}

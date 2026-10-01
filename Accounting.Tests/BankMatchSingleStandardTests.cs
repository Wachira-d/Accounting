using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม AI · A-AI3 (report-H H-5) — "มาตรฐานเดียว" ระหว่างตัวเลขที่คนเห็นกับตัวเลขที่เครื่องใช้ประทับ
///
/// ของเดิม: หน้าจับคู่ด้วยมือบวก/ลบคะแนนตาม "เงินลงที่ไหน" (Payment +5/−5 · JE +8/+4/−8) <b>เฉพาะบนจอ</b> ⇒ arbiter
/// (AutoMatch · BankFeed · OpenBanking) ประทับด้วยตัวเลขอีกชุด · ทางที่เลือก: คะแนน = <see cref="BankMatchScorer.Score"/>
/// ตัวเดียวทุกเส้น และหลักฐาน "เงินลงที่ไหน" เป็นลำดับรอง (<see cref="BankMatchScorer.DepositPreference"/>) — <b>ไม่เปลี่ยนคำตัดสิน
/// ของเส้นเครื่องแม้แต่ใบเดียว</b> (BankMatchGoldenTests ทุกเคสไม่ผ่านตัวนี้ ⇒ ก่อน/หลังเท่ากันโดยโครงสร้าง)
/// </summary>
public class BankMatchSingleStandardTests
{
    private static readonly DateTime D = new(2026, 3, 10);

    [Theory]
    // ── ค่าเดิมที่หน้าจอเคยบวก/ลบ ต้องเป็นลำดับรองเท่าเดิมทุกกิ่ง (ครึ่ง "ใบถูกไม่ถูกแตะ") ──
    [InlineData("Payment", "Bank", true, 1000, 5)]
    [InlineData("Payment", "Cash", true, 4999, -5)]
    [InlineData("Payment", "Cash", true, 5000, 0)]
    [InlineData("JournalEntry", "Bank", true, 1000, 8)]
    [InlineData("JournalEntry", "Mixed", true, 1000, 4)]
    [InlineData("JournalEntry", "Cash", true, 1000, -8)]
    // ── เงินออก / ไม่รู้ว่าลงที่ไหน = ไม่มีหลักฐาน ──
    [InlineData("Payment", "Bank", false, 1000, 0)]
    [InlineData("JournalEntry", "Cash", false, 1000, 0)]
    [InlineData("JournalEntry", null, true, 1000, 0)]
    public void ลำดับรองจากเงินลงที่ไหน_เท่าค่าเดิมของหน้าจอ(string kind, string? cat, bool inflow, int amount, int expected)
        => Assert.Equal(expected, BankMatchScorer.DepositPreference(kind, cat, inflow, amount));

    private sealed record Row(string Name, string Kind, string? Deposit, int Score, decimal Amount);

    private static List<Row> Rank(IEnumerable<Row> rows) => rows
        .OrderByDescending(r => r.Score)
        .ThenByDescending(r => BankMatchScorer.DepositPreference(r.Kind, r.Deposit, true, r.Amount))
        .ToList();

    [Fact]
    public void คะแนนเท่ากัน_รายการที่ลงธนาคารต้องมาก่อน_เหมือนเดิม()
    {
        var ranked = Rank(new[]
        {
            new Row("JV-เงินสด", "JournalEntry", "Cash", 90, 1000m),
            new Row("RV-ธนาคาร", "JournalEntry", "Bank", 90, 1000m),
        });
        Assert.Equal("RV-ธนาคาร", ranked[0].Name);
    }

    [Fact]
    public void อันดับที่เปลี่ยน_คะแนนจริงชนะหลักฐานเงินลงที่ไหน_เพราะตัวเลขบนจอต้องเท่าตัวเลขที่เครื่องประทับ()
    {
        // ใบที่อันดับเปลี่ยนจากรอบนี้ (อธิบายตาม CLAUDE §H): เดิมจอแสดง JV-เงินสด 85−8 = 77 · RV-ธนาคาร 80+8 = 88 ⇒ RV ขึ้นก่อน
        // แต่ AutoMatch ประทับด้วย 85 กับ 80 (ไม่มีโบนัส) ⇒ คนเห็นอันดับหนึ่งแบบ เครื่องคิดอีกแบบ · ตอนนี้ทั้งคู่เห็น 85 > 80
        var ranked = Rank(new[]
        {
            new Row("RV-ธนาคาร", "JournalEntry", "Bank", 80, 1000m),
            new Row("JV-เงินสด", "JournalEntry", "Cash", 85, 1000m),
        });
        Assert.Equal("JV-เงินสด", ranked[0].Name);

        // และ arbiter (ตัวตัดสินของเส้นเครื่อง) เห็นตัวเลขชุดเดียวกับจอ — ห่างกัน 5 < 10 ⇒ เสนอ ไม่ประทับ
        var d = BankMatchArbiter.Decide(ranked.Select(r =>
            new BankMatchArbiter.Option(Guid.NewGuid(), r.Kind, r.Score, false, r.Name)).ToList());
        Assert.Equal(BankMatchVerdict.Suggest, d.Verdict);
        Assert.Equal("JV-เงินสด", d.Chosen!.Reason);
    }

    [Fact]
    public void คะแนนของตัวให้คะแนนกลาง_ไม่ขึ้นกับหลักฐานเงินลงที่ไหน()
    {
        // DepositPreference ไม่ใช่อินพุตของ Score ⇒ AutoMatch/BankFeed/OpenBanking/หน้าจอ ได้คะแนนเดียวกันจากอินพุตเดียวกัน
        var x = new BankMatchScorer.Input(12500m, 12500m, D, D, CandidateName: "สมชาย ศิริพร",
            BankDescription: "TRANSFER FROM SOMCHAI SIRIPORN");
        var a = BankMatchScorer.Score(x);
        var b = BankMatchScorer.Score(x);
        Assert.Equal(a.Score, b.Score);
        Assert.True(a.Score <= BankMatchScorer.MaxScore);
    }
    // ── ฝ่ายค้าน X-6: SuggestMatchAsync (V1 `/api/v1` + ปุ่ม "AI วิเคราะห์" ในหน้าต่างจับคู่) หยิบ "ใบแรกที่ยอดตรง" ตามลำดับเดียวกับจอ
    //    ⇒ เปลี่ยนลำดับ = เปลี่ยนคำแนะนำของสองทางเข้านั้นด้วย — ล็อกด้วยโค้ดจริง (BankService.RankCandidates + PickSubset)

    private static Accounting.Models.DTOs.Bank.MatchCandidate Pay(string number, int score, string deposit, int dateDiff = 0)
        => new("Payment", Guid.NewGuid(), number, D, 1000m, "", null, null, dateDiff, 0m, score, null,
            DepositLabel: null, DepositCategory: deposit);

    [Fact]
    public void X6_คำแนะนำ_SuggestMatch_อันดับที่เปลี่ยน_ใบคะแนนจริงสูงกว่าถูกหยิบ_แม้ลงเงินสด()
    {
        // เดิม: PV-เงินสด 85−5 = 80 · PV-ธนาคาร 80+5 = 85 ⇒ V1/ปุ่ม AI แนะนำ PV-ธนาคาร
        // ตอนนี้: คะแนนจริง 85 > 80 ⇒ แนะนำ PV-เงินสด (เท่ากับที่ arbiter เห็น) — ผู้ใช้ยังเห็นป้าย "💵 เงินสด" ก่อนกดยืนยัน
        var cash = Pay("PV-เงินสด", 85, "Cash");
        var bank = Pay("PV-ธนาคาร", 80, "Bank");
        var ranked = Accounting.Services.Implementations.BankService.RankCandidates(new[] { bank, cash }, bankIsInflow: true);
        var (ids, _, exact) = Accounting.Services.Implementations.BankService.PickSubset(ranked, 1000m, 0.01m);
        Assert.True(exact);
        Assert.Equal(cash.Id, Assert.Single(ids));
    }

    [Fact]
    public void X6_คำแนะนำ_SuggestMatch_คะแนนเท่ากัน_ยังหยิบใบที่ลงธนาคารเหมือนเดิม()
    {
        var cash = Pay("PV-เงินสด", 85, "Cash");
        var bank = Pay("PV-ธนาคาร", 85, "Bank", dateDiff: 2);
        var ranked = Accounting.Services.Implementations.BankService.RankCandidates(new[] { cash, bank }, bankIsInflow: true);
        var (ids, _, _) = Accounting.Services.Implementations.BankService.PickSubset(ranked, 1000m, 0.01m);
        Assert.Equal(bank.Id, Assert.Single(ids));
    }
}

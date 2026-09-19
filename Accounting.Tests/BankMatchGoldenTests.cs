using System;
using System.Collections.Generic;
using System.Linq;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ตาข่ายเทสต์ของโดเมนธนาคาร** — ก่อนรอบนี้ `BankService*` / `Bulk*` /
/// `BankMatchScoring` / `BankMatchVerification` / `BankFlowClassifier` /
/// `BankFeedService` / `OpenBankingService` มี **0 ไฟล์เทสต์**
/// (`DECISION_AUDIT_2026-09-18.md` §3 D4-T) ⇒ ไม่มีอะไรฟ้องเมื่อสูตรจับคู่เปลี่ยน
///
/// เคสในไฟล์นี้มาจาก §3 D4-E9 ตรง ๆ. ทุกหมวดมี **สองครึ่ง** ตามกฎเหล็ก #4 H:
/// ครึ่งที่พิสูจน์ว่า "ใบที่พังกลับมาถูก" และครึ่งที่พิสูจน์ว่า
/// "ใบที่ถูกอยู่แล้วไม่ถูกแตะ" — เทสต์ที่มีแต่ครึ่งแรกผ่านได้ทั้งตอนแก้ถูก
/// และตอนปิดด่านทิ้ง
/// </summary>
public class BankMatchGoldenTests
{
    private static readonly DateTime D = new(2026, 3, 10);

    private static BankMatchScorer.Input Candidate(
        decimal amount, DateTime date, string? name = null,
        string? bankDesc = null, string? bankPayee = null,
        string? candidateRef = null, string? bankRef = null,
        decimal bankAmount = 0m, DateTime? bankDate = null)
        => new(
            CandidateAmount: amount,
            BankAmount: bankAmount == 0m ? amount : bankAmount,
            CandidateDate: date,
            BankDate: bankDate ?? D,
            CandidateRef: candidateRef,
            CandidateNotes: null,
            CandidateDocNumber: null,
            CandidateName: name,
            BankDescription: bankDesc,
            BankReference: bankRef,
            BankPayee: bankPayee);

    private static BankMatchArbiter.Option ToOption(Guid id, BankMatchScorer.Result r)
        => new(id, "Payment", r.Score, r.HasIdentitySignal, r.Reason);

    // ══════════════════════════════════════════════════════════════════
    //  (ก) ยอดเท่ากัน วันเดียวกัน 2 ใบ ชื่อผู้โอนต่างกัน → เลือกตามชื่อ
    // ══════════════════════════════════════════════════════════════════
    // เดิม `AutoMatchAsync` ให้ 50 (ยอด) + 30 (วันเดียวกัน) = 80 เท่ากันทั้งคู่
    // แล้วใช้ `score > bestScore` ⇒ **ใบที่ DB คืนมาก่อนชนะ** และ **ไม่ดูชื่อ
    // ผู้โอนเลย** (`BankService.cs:579-757`)

    [Fact]
    public void ยอดเท่ากันวันเดียวกัน_ชื่อผู้โอนตรงใบใดใบหนึ่ง_ต้องเลือกใบที่ชื่อตรงและประทับได้()
    {
        var somchai = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var wachira = Guid.Parse("22222222-2222-2222-2222-222222222222");

        // statement: "โอนเงินจาก SOMCHAI SIRIPORN" — ชื่ออังกฤษ ฝั่งเรามีไทย
        const string desc = "TRANSFER FROM SOMCHAI SIRIPORN";

        var a = BankMatchScorer.Score(Candidate(12500m, D, "สมชาย ศิริพร", bankDesc: desc));
        var b = BankMatchScorer.Score(Candidate(12500m, D, "วชิระ ดิลกสัมพันธ์", bankDesc: desc));

        Assert.True(a.NameConfident, $"ชื่อควรตรงข้ามภาษา แต่ได้ {a.Reason}");
        Assert.False(b.NameConfident);
        Assert.True(a.Score > b.Score, $"ใบที่ชื่อตรงต้องคะแนนสูงกว่า ({a.Score} vs {b.Score})");

        var d = BankMatchArbiter.Decide(new[] { ToOption(somchai, a), ToOption(wachira, b) });

        Assert.Equal(BankMatchVerdict.Apply, d.Verdict);
        Assert.Equal(somchai, d.Chosen!.Id);
    }

    [Fact]
    public void ยอดวันเท่ากันทั้งคู่_ชื่อผู้โอนตรงทั้งคู่_ต้องไม่ประทับ()
    {
        // ครึ่งตรงข้ามของกฎ "หลักฐานคนละชั้น": ลูกค้ารายเดียวกันมี 2 ใบ
        // ยอดเท่ากัน วันเดียวกัน — ชื่อช่วยอะไรไม่ได้ ⇒ ห้ามเดา
        var a = Guid.Parse("aa111111-1111-1111-1111-111111111111");
        var b = Guid.Parse("bb222222-2222-2222-2222-222222222222");
        const string desc = "TRANSFER FROM SOMCHAI SIRIPORN";

        var s1 = BankMatchScorer.Score(Candidate(12500m, D, "สมชาย ศิริพร", bankDesc: desc));
        var s2 = BankMatchScorer.Score(Candidate(12500m, D, "สมชาย ศิริพร", bankDesc: desc));

        var d = BankMatchArbiter.Decide(new[] { ToOption(a, s1), ToOption(b, s2) });

        Assert.Equal(BankMatchVerdict.Suggest, d.Verdict);
        Assert.Equal("BANK-MATCH-TIE", d.RuleCode);
    }

    [Fact]
    public void ลำดับผู้สมัครสลับกัน_ผลต้องเหมือนเดิม_ห้ามใครมาก่อนชนะ()
    {
        var somchai = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var wachira = Guid.Parse("22222222-2222-2222-2222-222222222222");
        const string desc = "TRANSFER FROM SOMCHAI SIRIPORN";

        var a = ToOption(somchai, BankMatchScorer.Score(Candidate(12500m, D, "สมชาย ศิริพร", bankDesc: desc)));
        var b = ToOption(wachira, BankMatchScorer.Score(Candidate(12500m, D, "วชิระ ดิลกสัมพันธ์", bankDesc: desc)));

        var forward = BankMatchArbiter.Decide(new[] { a, b });
        var reversed = BankMatchArbiter.Decide(new[] { b, a });

        Assert.Equal(forward.Verdict, reversed.Verdict);
        Assert.Equal(forward.Chosen!.Id, reversed.Chosen!.Id);
    }

    // ══════════════════════════════════════════════════════════════════
    //  (ข) ยอดเท่ากัน ไม่มีชื่อผู้โอน → ไม่ประทับ (เสมอกัน = ไม่ตัดสิน)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ยอดเท่ากันวันเดียวกัน_ไม่มีชื่อผู้โอน_ห้ามประทับ_ให้เสนอเท่านั้น()
    {
        var p1 = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var p2 = Guid.Parse("44444444-4444-4444-4444-444444444444");

        // statement ไม่มีชื่อใครเลย — แบงก์ส่งมาแต่ช่องทาง
        var s1 = BankMatchScorer.Score(Candidate(12500m, D, null, bankDesc: "THAI QR PAYMENT"));
        var s2 = BankMatchScorer.Score(Candidate(12500m, D, null, bankDesc: "THAI QR PAYMENT"));

        Assert.Equal(s1.Score, s2.Score);

        var d = BankMatchArbiter.Decide(new[] { ToOption(p1, s1), ToOption(p2, s2) });

        Assert.Equal(BankMatchVerdict.Suggest, d.Verdict);
        Assert.Equal("BANK-MATCH-TIE", d.RuleCode);
        // แม้จะ "เสนอ" ก็ต้องมี id ของคู่ที่เสนอเสมอ — สถานะกับความว่าง = D4-2
        Assert.NotNull(d.Chosen);
    }

    [Fact]
    public void ผู้สมัครรายเดียวยอดตรงวันเดียวกัน_ยังต้องประทับได้เหมือนเดิม()
    {
        // ครึ่ง "ใบที่ถูกอยู่แล้วต้องไม่ถูกแตะ" — การเพิ่มด่านห้ามทำให้
        // เคสธรรมดา (ผู้สมัครรายเดียว ยอดตรงเป๊ะ วันเดียวกัน) กลายเป็น "เสนอ"
        var only = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var s = BankMatchScorer.Score(Candidate(7000m, D, null, bankDesc: "TRANSFER"));

        var d = BankMatchArbiter.Decide(new[] { ToOption(only, s) });

        Assert.Equal(BankMatchVerdict.Apply, d.Verdict);
        Assert.Equal(only, d.Chosen!.Id);
    }

    [Fact]
    public void ผู้สมัครที่สองยอดต่างกันมาก_ที่หนึ่งยังประทับได้()
    {
        var best = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var other = Guid.Parse("77777777-7777-7777-7777-777777777777");

        var s1 = BankMatchScorer.Score(Candidate(7000m, D, null, bankDesc: "TRANSFER"));
        // ยอดต่าง 30% → ไม่ได้คะแนนยอดเลย, วันเดียวกันได้ 30
        var s2 = BankMatchScorer.Score(Candidate(9100m, D, null, bankDesc: "TRANSFER", bankAmount: 7000m));

        var d = BankMatchArbiter.Decide(new[] { ToOption(best, s1), ToOption(other, s2) });

        Assert.Equal(BankMatchVerdict.Apply, d.Verdict);
        Assert.Equal(best, d.Chosen!.Id);
        Assert.True(d.Margin >= BankMatchArbiter.DefaultApplyMinMargin);
    }

    [Fact]
    public void คะแนนต่ำกว่าเกณฑ์เสนอ_ต้องไม่ตอบอะไรเลย()
    {
        // ยอดต่าง 10% + ห่าง 20 วัน = 0 + 3 คะแนน → ต่ำกว่า 60
        var far = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var s = BankMatchScorer.Score(Candidate(7700m, D.AddDays(-20), null,
            bankDesc: "TRANSFER", bankAmount: 7000m));

        var d = BankMatchArbiter.Decide(new[] { ToOption(far, s) });

        Assert.Equal(BankMatchVerdict.None, d.Verdict);
        Assert.Null(d.Chosen);
    }

    [Fact]
    public void ยอดตรงแต่ห่างเจ็ดวัน_ลดชั้นเป็นเสนอ_ไม่ประทับเอง()
    {
        // 60 (ยอดตรง) + 15 (≤7 วัน) = 75 < 80 → เสนอ
        // เดิม AutoMatch ให้ 50 + 10 = 60 ≥ 60 → **ประทับเลย**
        var id = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var s = BankMatchScorer.Score(Candidate(7000m, D.AddDays(-7), null, bankDesc: "TRANSFER"));

        Assert.Equal(75, s.Score);
        var d = BankMatchArbiter.Decide(new[] { ToOption(id, s) });
        Assert.Equal(BankMatchVerdict.Suggest, d.Verdict);
        Assert.Equal("BANK-MATCH-LOW", d.RuleCode);
    }

    // ══════════════════════════════════════════════════════════════════
    //  (ฉ) AI อ้าง candidateId ที่ไม่มีอยู่ → ทิ้ง
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Aiอ้างIdที่ไม่มีในชุดผู้สมัคร_ต้องถูกทิ้ง()
    {
        var real = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var invented = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var screened = BankMatchArbiter.ScreenAiProposals(
            new[]
            {
                new BankMatchArbiter.AiProposal(invented, "Payment", 0.99m, "มั่นใจมาก"),
                new BankMatchArbiter.AiProposal(real, "Payment", 0.88m, "ชื่อตรง"),
            },
            new[] { real });

        Assert.Single(screened.Accepted);
        Assert.Equal(real, screened.Accepted[0].CandidateId);
        Assert.Contains(invented, screened.RejectedUnknownIds);
    }

    [Fact]
    public void AiตอบIdว่าง_ต้องถูกทิ้ง_ไม่ใช่จับคู่กับGuidEmpty()
    {
        var real = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var screened = BankMatchArbiter.ScreenAiProposals(
            new[] { new BankMatchArbiter.AiProposal(Guid.Empty, "Payment", 0.95m) },
            new[] { real });

        Assert.Empty(screened.Accepted);
        Assert.Single(screened.RejectedUnknownIds);
    }

    [Fact]
    public void AiตอบIdที่มีจริงและมั่นใจพอ_ต้องผ่านด่าน()
    {
        // ครึ่งตรงข้าม — ด่านที่ปฏิเสธทุกใบ = ปิดด่านโดยไม่ตั้งใจ
        var real = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var screened = BankMatchArbiter.ScreenAiProposals(
            new[] { new BankMatchArbiter.AiProposal(real, "Payment", 0.80m) },
            new[] { real });

        Assert.Single(screened.Accepted);
        Assert.Empty(screened.RejectedUnknownIds);
        Assert.Empty(screened.RejectedLowConfidence);
    }

    [Fact]
    public void Aiมั่นใจต่ำกว่าเกณฑ์_ต้องไม่ถูกนำไปใช้()
    {
        var real = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var screened = BankMatchArbiter.ScreenAiProposals(
            new[] { new BankMatchArbiter.AiProposal(real, "Payment", 0.50m) },
            new[] { real });

        Assert.Empty(screened.Accepted);
        Assert.Contains(real, screened.RejectedLowConfidence);
    }

    [Fact]
    public void ข้อเสนอจากเซิร์ฟเวอร์ต้องมาก่อนAiแม้confidenceต่ำกว่า()
    {
        // D4-8: `DeduplicateMatches` เดิมเรียงด้วย Confidence อย่างเดียว
        // ⇒ AI ที่แต่ง conf 0.99 ชนะเซิร์ฟเวอร์ที่คำนวณจาก DB ได้ 0.90
        var rows = new[]
        {
            (Src: BankMatchArbiter.MatchSource.Ai, Conf: 0.99m, Tag: "ai"),
            (Src: BankMatchArbiter.MatchSource.Server, Conf: 0.90m, Tag: "server"),
            (Src: BankMatchArbiter.MatchSource.LocalModel, Conf: 0.95m, Tag: "local"),
        };

        var ranked = BankMatchArbiter
            .RankBySourceThenConfidence(rows, r => r.Src, r => r.Conf)
            .Select(r => r.Tag).ToList();

        Assert.Equal(new[] { "server", "local", "ai" }, ranked);
    }
}

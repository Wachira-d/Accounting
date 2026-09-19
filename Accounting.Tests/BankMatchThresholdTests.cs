using System;
using System.Collections.Generic;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ล็อกเกณฑ์ของ `BankMatchArbiter`** — รอบ 183 ยกเกณฑ์ประทับอัตโนมัติเป็น 80
/// ทำให้กลุ่ม "ยอดตรงเป๊ะ + ห่าง 4–7 วัน + ไม่มีชื่อผู้โอน/เลขอ้างอิง"
/// เปลี่ยนจาก `Matched` เป็น `Suggested` (`DECISION_AUDIT_2026-09-18.md` §10.3 ข้อ 3)
///
/// รอบนี้ทบทวนแล้ว **คงไว้ที่ 80** — ไฟล์นี้คือด่านที่ทำให้การผ่อน/เข้ม
/// ครั้งหน้าเป็นการตัดสินใจ ไม่ใช่ผลข้างเคียง: แก้ค่าคงที่เมื่อไร เทสต์นี้ล้มทันที
/// </summary>
public class BankMatchThresholdTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void ค่าคงที่ของเกณฑ์ต้องไม่ขยับโดยไม่ตั้งใจ()
    {
        Assert.Equal(80, BankMatchArbiter.DefaultApplyMinScore);
        Assert.Equal(60, BankMatchArbiter.DefaultSuggestMinScore);
        Assert.Equal(10, BankMatchArbiter.DefaultApplyMinMargin);
        Assert.Equal(0.70m, BankMatchArbiter.DefaultAiMinConfidence);
    }

    private static readonly DateTime Bank = new(2026, 5, 10);

    private static int ScoreOf(decimal amount, DateTime candidateDate, string? name = null,
        string? bankPayee = null)
        => BankMatchScorer.Score(new BankMatchScorer.Input(
            CandidateAmount: amount, BankAmount: 5000m,
            CandidateDate: candidateDate, BankDate: Bank,
            CandidateName: name, BankPayee: bankPayee)).Score;

    // ── กลุ่มที่ถูกลดชั้น (ผลข้างเคียงที่ทบทวนแล้วคงไว้) ────────────────
    [Fact]
    public void ยอดตรงเป๊ะห่าง5วันไม่มีสัญญาณระบุตัวตน_ต้องเป็นเสนอไม่ใช่ประทับ()
    {
        var score = ScoreOf(5000m, Bank.AddDays(-5));
        Assert.Equal(75, score);          // 60 ยอด + 15 (≤7 วัน)

        var d = BankMatchArbiter.Decide(new List<BankMatchArbiter.Option>
        {
            new(A, "Payment", score, HasIdentitySignal: false, "ยอดตรงเป๊ะ, ห่างกัน ≤7 วัน"),
        });
        Assert.Equal(BankMatchVerdict.Suggest, d.Verdict);
        Assert.Equal("BANK-MATCH-LOW", d.RuleCode);
        Assert.Equal(A, d.Chosen!.Id);    // **ต้องมีคู่ที่เสนอเสมอ** (ราก R1)
    }

    [Fact]
    public void กลุ่มเดียวกันแต่มีชื่อผู้โอนตรง_ยังประทับได้เหมือนเดิม()
    {
        // ทางไปต่อของผู้ใช้กลุ่มที่ถูกลดชั้น: statement ที่มีชื่อผู้โอน (แบงก์ไทย
        // ส่งมาเกือบทุกบรรทัด) ยังได้คำตอบอัตโนมัติ
        var score = ScoreOf(5000m, Bank.AddDays(-5), name: "บริษัท ทดสอบ จำกัด",
            bankPayee: "บริษัท ทดสอบ จำกัด");
        Assert.True(score >= BankMatchArbiter.DefaultApplyMinScore,
            $"คาดว่า ≥80 แต่ได้ {score}");

        var d = BankMatchArbiter.Decide(new List<BankMatchArbiter.Option>
        {
            new(A, "Payment", score, HasIdentitySignal: true, "ชื่อผู้โอนตรงกัน"),
        });
        Assert.Equal(BankMatchVerdict.Apply, d.Verdict);
    }

    // ── ครึ่ง "ของเดิมที่ถูกอยู่แล้วต้องไม่ถูกแตะ" ─────────────────────
    [Fact]
    public void ยอดตรงเป๊ะวันเดียวกัน_ยังประทับอัตโนมัติ()
    {
        var score = ScoreOf(5000m, Bank);
        Assert.Equal(90, score);
        var d = BankMatchArbiter.Decide(new List<BankMatchArbiter.Option>
        {
            new(A, "Payment", score, false, "ยอดตรงเป๊ะ, วันเดียวกัน"),
        });
        Assert.Equal(BankMatchVerdict.Apply, d.Verdict);
    }

    [Fact]
    public void ยอดตรงเป๊ะห่าง3วัน_ยังประทับอัตโนมัติพอดีที่เกณฑ์()
    {
        var score = ScoreOf(5000m, Bank.AddDays(-3));
        Assert.Equal(80, score);
        Assert.Equal(BankMatchVerdict.Apply, BankMatchArbiter.Decide(
            new List<BankMatchArbiter.Option> { new(A, "Payment", score, false, "") }).Verdict);
    }

    [Fact]
    public void สองใบเหมือนกันทุกอย่าง_ห้ามประทับแม้คะแนนจะสูง()
    {
        var d = BankMatchArbiter.Decide(new List<BankMatchArbiter.Option>
        {
            new(A, "Payment", 90, false, ""),
            new(B, "Payment", 90, false, ""),
        });
        Assert.Equal(BankMatchVerdict.Suggest, d.Verdict);
        Assert.Equal("BANK-MATCH-TIE", d.RuleCode);
    }

    [Fact]
    public void คะแนนต่ำกว่าเกณฑ์เสนอ_ต้องไม่ตอบเลยไม่ใช่เสนอมั่ว()
    {
        var d = BankMatchArbiter.Decide(new List<BankMatchArbiter.Option>
        {
            new(A, "Payment", 59, false, ""),
        });
        Assert.Equal(BankMatchVerdict.None, d.Verdict);
        Assert.Null(d.Chosen);
    }
}

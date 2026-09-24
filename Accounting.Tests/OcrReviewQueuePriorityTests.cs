using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// สูตรจัดลำดับ "คิวรอตรวจ (OCR)" — <see cref="OcrReviewQueuePriority"/>
///
/// <para>ที่มา: เจ้าของรายงาน รอบ 190 ข้อ 1 — หน้าคิวรอตรวจเป็นภาษาอังกฤษ ("uncertain (40%)",
/// "new vendor", "stale") และสูตรฝังใน <c>ActiveLearningRanker</c> โดยไม่มีเทสต์ · ธงลายมือ
/// ที่ entity เขียนว่า "ดันขึ้นคิว" ไม่เคยถูกอ่าน</para>
///
/// <para>สองครึ่ง (CLAUDE.md §H): ครึ่งแรกล็อกว่าใบที่<b>ไม่มีธงใหม่</b>ได้คะแนน<b>เท่าสูตรเดิม
/// ทุกประการ</b> (ตัวเลขจากตัวอย่างในคอมเมนต์ของ ranker เดิม) · ครึ่งหลังล็อกวิธีคิดที่เพิ่ม</para>
/// </summary>
public class OcrReviewQueuePriorityTests
{
    private static OcrReviewQueueSignals S(decimal conf, int seen = 0, bool matched = true, bool recent = true,
        bool asset = false, bool hand = false, bool dup = false, bool corrected = false)
        => new(conf, matched, seen, recent, asset, hand, dup, corrected);

    // ───────── ครึ่งที่ 1: ใบที่ไม่มีธงใหม่ต้องได้เท่าสูตรเดิม ─────────

    [Theory]
    // ตัวอย่างในคอมเมนต์ ActiveLearningRanker เดิม: "PEA bill #1 uncertainty=0.4 novelty=1.0 → 0.80"
    [InlineData(0.6, 0, true, 0.80)]
    // "HomePro uncertainty=0.5 novelty=0.5 → 0.75" (ไม่รวม asset boost)
    [InlineData(0.5, 1, true, 0.75)]
    // เก่ากว่า 7 วัน = ×0.5 เหมือนเดิม
    [InlineData(0.5, 1, false, 0.375)]
    // มั่นใจเต็ม = คะแนน 0 (ยังอยู่ในคิว แต่ท้ายสุด)
    [InlineData(1.0, 0, true, 0.0)]
    public void NoNewFlags_ScoreEqualsLegacyFormula(double conf, int seen, bool recent, double expected)
    {
        var r = OcrReviewQueuePriority.Score(S((decimal)conf, seen, recent: recent));
        Assert.Equal((decimal)expected, r.Priority);
    }

    [Fact]
    public void AssetBoost_Unchanged_1_3()
    {
        var plain = OcrReviewQueuePriority.Score(S(0.6m));
        var asset = OcrReviewQueuePriority.Score(S(0.6m, asset: true));
        Assert.Equal(plain.Priority * 1.3m, asset.Priority);
    }

    [Fact]
    public void UnmatchedContact_TreatedAsNewVendor_LikeLegacy()
    {
        // เดิม: MatchedContactId == null ⇒ seen = 0 ⇒ novelty = 1
        var r = OcrReviewQueuePriority.Score(S(0.6m, seen: 5, matched: false));
        Assert.Equal(1m, r.Novelty);
        Assert.Contains("ยังจับคู่ผู้ติดต่อไม่ได้", r.Reasons);
    }

    // ───────── ครึ่งที่ 2: วิธีคิดที่เพิ่ม ─────────

    [Fact]
    public void Handwriting_PushesUp()
    {
        var plain = OcrReviewQueuePriority.Score(S(0.6m));
        var hand = OcrReviewQueuePriority.Score(S(0.6m, hand: true));
        Assert.True(hand.Priority > plain.Priority);
        Assert.Contains(hand.Reasons, x => x.Contains("ลายมือ"));
    }

    [Fact]
    public void UserAlreadyCorrected_MovesDown_ButStaysInQueue()
    {
        // ระบบได้บทเรียนจากใบนี้แล้ว — ใบที่ยังไม่มีใครแตะควรขึ้นก่อน
        var untouched = OcrReviewQueuePriority.Score(S(0.6m));
        var corrected = OcrReviewQueuePriority.Score(S(0.6m, corrected: true));
        Assert.True(corrected.Priority < untouched.Priority);
        Assert.True(corrected.Priority > 0m);
        Assert.Contains(corrected.Reasons, x => x.Contains("รอสร้างเอกสาร"));
    }

    [Fact]
    public void Reasons_AreThai_NoLegacyEnglish()
    {
        var r = OcrReviewQueuePriority.Score(S(0.6m, recent: false, asset: true, dup: true));
        var all = string.Join(" | ", r.Reasons);
        Assert.Contains("ระบบไม่มั่นใจ 40%", all);
        Assert.Contains("ผู้ขายใหม่", all);
        Assert.Contains("สินทรัพย์", all);
        Assert.Contains("ซ้ำ", all);
        Assert.Contains("เกิน 7 วัน", all);
        foreach (var eng in new[] { "uncertain", "new vendor", "stale", "asset alert", "seen" })
            Assert.DoesNotContain(eng, all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfidentKnownVendor_NoNoiseReasons()
    {
        // ผู้ขายที่อนุมัติมาแล้ว 10 ใบ + ระบบมั่นใจ 95% + สแกนวันนี้ ⇒ ไม่มีเหตุผลให้ฟ้อง
        var r = OcrReviewQueuePriority.Score(S(0.95m, seen: 10));
        Assert.Empty(r.Reasons);
    }

    [Fact]
    public void ConfidenceOutOfRange_Clamped_NeverNegative()
    {
        Assert.Equal(0m, OcrReviewQueuePriority.Score(S(85m)).Priority);          // เก็บเป็นเปอร์เซ็นต์ผิดหน่วย
        Assert.Equal(1m, OcrReviewQueuePriority.Score(S(-0.2m)).Uncertainty);
    }
}

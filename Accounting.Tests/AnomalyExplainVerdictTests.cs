using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · H-3 — คำอธิบายรายการผิดปกติ: นักเรียนตอบต้องถูกบันทึก (kill-switch) · คำตอบนอกชุดห้ามเขียน</summary>
public class AnomalyExplainVerdictTests
{
    [Fact]
    public void ปิด_provider_แล้วนักเรียนตอบ_ต้องบันทึกได้()
        => Assert.True(AnomalyExplainVerdict.ShouldPersist(usedAi: false, fromLocalModel: true, "NeedReview"));

    [Fact]
    public void ครูตอบในชุด_บันทึกได้เหมือนเดิม()
        => Assert.True(AnomalyExplainVerdict.ShouldPersist(usedAi: true, fromLocalModel: false, "LikelyError"));

    [Theory]
    [InlineData("Probably fine")]
    [InlineData("")]
    [InlineData(null)]
    public void คำตอบนอกชุดหรือว่าง_ห้ามเขียน(string? primary)
        => Assert.False(AnomalyExplainVerdict.ShouldPersist(usedAi: true, fromLocalModel: false, primary));

    [Fact]
    public void ไม่มีผู้ตอบจริง_ห้ามเขียน()
        => Assert.False(AnomalyExplainVerdict.ShouldPersist(usedAi: false, fromLocalModel: false, "LikelyLegit"));

    [Fact]
    public void ตัวพิมพ์ต่าง_normalize_เป็นค่าในชุด()
        => Assert.Equal("LikelyLegit", AnomalyExplainVerdict.Normalize(" likelylegit "));
}

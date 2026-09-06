using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กฎเหล็ก #1 ข้อ 5 (kill-switch): ปิด provider ทุกตัวแล้ว feature ต้องทำงานครบ
/// ⇒ คำตอบของ "นักเรียน" ต้องถูก **ใช้จริง** ในทุกเส้นที่ AI ไม่พร้อม ไม่ใช่เฉพาะ
/// เส้น short-circuit ที่นักเรียนมั่นใจพอ
///
/// <para>ที่มา: ตัวตรวจเดิมเดาจาก <c>Status == Skipped &amp;&amp; ProviderModel
/// เริ่มด้วย "local:"</c> ซึ่งเป็นลายเซ็นของเส้น short-circuit เส้นเดียว — เส้น
/// degradation ตั้ง <c>ProviderModel = null</c> และสถานะเป็น NoProvider/
/// BudgetExceeded/Failed/InvalidResponse ⇒ คำตอบนักเรียนถูกทิ้งทุกครั้งที่ AI ไม่พร้อม
/// (ผลตรวจย้อน 2026-09-06 · คอมมิต 4fd8dd6)</para>
/// </summary>
public class StudentAnswerSourceTests
{
    private static AiResponse Local(AiCallStatus status, string? providerModel) => new()
    {
        Status = status,
        PrimaryAnswer = "51010",
        Confidence = 0.9m,
        UsedAi = false,
        FromLocalModel = true,
        ProviderModel = providerModel,
    };

    [Theory]
    // เส้น short-circuit (นักเรียนมั่นใจพอ) — เคยผ่านอยู่แล้ว
    [InlineData(AiCallStatus.Skipped, "local:GlAccount-v3")]
    // เส้น degradation ที่ "เคยตกทั้งหมด" — ต้องผ่านหลังแก้
    [InlineData(AiCallStatus.NoProvider, null)]          // ปิด provider ทุกตัว = kill-switch
    [InlineData(AiCallStatus.BudgetExceeded, null)]      // เกินงบรายวัน/รายเดือน
    [InlineData(AiCallStatus.Failed, null)]              // provider ล่ม/เน็ตขาด
    [InlineData(AiCallStatus.InvalidResponse, null)]     // ตอบไม่เข้า schema
    [InlineData(AiCallStatus.Skipped, null)]             // admin ปิด feature
    public void คำตอบนักเรียนต้องถูกใช้ในทุกเส้นที่AIไม่พร้อม(AiCallStatus status, string? providerModel)
        => Assert.True(OcrAiAugmenter.IsStudentAnswer(Local(status, providerModel)));

    [Fact]
    public void ไม่มีนักเรียนตอบ_ต้องไม่ถูกนับเป็นคำตอบของโมเดล()
    {
        // heuristic ของผู้เรียกเองที่ส่งมาใน LocalPrimaryAnswer — ไม่ใช่นักเรียน
        var heuristicOnly = new AiResponse
        {
            Status = AiCallStatus.NoProvider,
            PrimaryAnswer = "51010",
            UsedAi = false,
            FromLocalModel = false,
        };
        Assert.False(OcrAiAugmenter.IsStudentAnswer(heuristicOnly));

        // นักเรียนไม่มีคำตอบ
        var empty = new AiResponse
        {
            Status = AiCallStatus.NoProvider,
            PrimaryAnswer = null,
            UsedAi = false,
            FromLocalModel = true,
        };
        Assert.False(OcrAiAugmenter.IsStudentAnswer(empty));
    }

    [Fact]
    public void ครูตอบ_ไม่ใช่คำตอบของนักเรียน()
    {
        var teacher = new AiResponse
        {
            Status = AiCallStatus.Success,
            PrimaryAnswer = "51010",
            UsedAi = true,
            FromLocalModel = false,
            ProviderModel = "deepseek-chat",
        };
        Assert.False(OcrAiAugmenter.IsStudentAnswer(teacher));
    }
}

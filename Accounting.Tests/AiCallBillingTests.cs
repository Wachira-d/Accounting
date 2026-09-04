using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "แถวไหนคือ AI call ที่เสียเงินจริง" (AI-02 / AI-03)
///
/// ═══ บั๊กจริงที่เทสต์ชุดนี้ล็อกไว้ ═══
/// 27 endpoint heuristic บันทึกแถวเป็น <c>Success</c> + <c>ProviderUsed=DeepSeek</c>
/// ทั้งที่ไม่เคยยิง provider ⇒ <c>AiBudgetGuard</c> นับเป็น call ที่เสียเงิน ⇒
/// <b>วันที่ผู้ใช้กดปุ่มแนะนำ (ฟรี) เยอะ daily cap เต็มแล้วบล็อก AI ของจริงทั้ง tenant</b>
/// และแถวลูกสังเคราะห์ของ bulk call 1 ครั้งถูกนับซ้ำหลายสิบครั้ง
/// </summary>
public class AiCallBillingTests
{
    private static readonly Func<AiSuggestionFeedback, bool> Billable =
        AiCallBilling.BillableRow.Compile();

    private static AiSuggestionFeedback Row(
        AiCallStatus status = AiCallStatus.Success,
        AiProviderType provider = AiProviderType.DeepSeek,
        Guid? parent = null)
        => new() { Status = status, ProviderUsed = provider, CacheHitOfFeedbackId = parent };

    // ── ที่ต้องนับ ──

    [Fact]
    public void เรียก_provider_สำเร็จ_ต้องนับ()
        => Assert.True(Billable(Row()));

    [Fact]
    public void เรียกแล้วล้มเหลว_ก็ยังนับ_เพราะจ่ายค่า_token_ไปแล้ว()
    {
        Assert.True(Billable(Row(AiCallStatus.Failed)));
        Assert.True(Billable(Row(AiCallStatus.InvalidResponse)));
    }

    // ── ที่ต้องไม่นับ ──

    [Fact]
    public void heuristic_ในบ้าน_ต้องไม่นับ()
        => Assert.False(Billable(Row(AiCallStatus.LocalServed, AiProviderType.None)));

    [Fact]
    public void แถวเก่าที่ติดป้าย_Success_แต่ผู้ให้บริการเป็น_None_ต้องไม่นับ()
    {
        // ★ ด่านนี้จำเป็นแม้แก้ต้นทางแล้ว — แถวที่สะสมไว้ยังเป็น Success
        Assert.False(Billable(Row(AiCallStatus.Success, AiProviderType.None)));
    }

    [Fact]
    public void แถวลูกสังเคราะห์ของ_bulk_call_ต้องไม่นับ()
    {
        // ★ bulk 1 ครั้งแตกเป็นลูกหลายสิบแถว — แม่ถูกนับไปแล้ว
        Assert.False(Billable(Row(parent: Guid.NewGuid())));
    }

    [Fact]
    public void cache_hit_และ_skip_ไม่นับ()
    {
        Assert.False(Billable(Row(AiCallStatus.Cached)));
        Assert.False(Billable(Row(AiCallStatus.Skipped)));
        Assert.False(Billable(Row(AiCallStatus.BudgetExceeded)));
        Assert.False(Billable(Row(AiCallStatus.NoProvider)));
    }

    [Fact]
    public void เกณฑ์เดิม_ดูแค่สถานะ_ปล่อยของฟรีเข้ามานับ()
    {
        // negative test ของเกณฑ์เดิม: พิสูจน์ว่า "ดูแค่สถานะ" ปล่อยผ่านกี่แบบ
        var leaked = new[]
        {
            Row(AiCallStatus.Success, AiProviderType.None),   // heuristic แถวเก่า
            Row(parent: Guid.NewGuid()),                      // ลูกของ bulk
        };
        foreach (var r in leaked)
        {
            Assert.True(AiCallBilling.IsProviderAttemptStatus(r.Status)); // เกณฑ์เดิมผ่าน
            Assert.False(Billable(r));                                    // เกณฑ์ใหม่ตัดออก
        }
    }

    // ── ลายเซ็นของแถวเก่าที่ติดป้ายผิด (ต้องตรงกับ SQL ใน DatabaseMigrationHelper) ──

    [Fact]
    public void แถว_heuristic_เก่า_เข้าเกณฑ์ที่ต้องล้าง()
        => Assert.True(AiCallBilling.LooksLikeMislabelledLocalRow(
            AiCallStatus.Success, AiProviderType.DeepSeek,
            latencyMs: 0, inputTokens: 0, outputTokens: 0, costUsd: 0m,
            cacheHitOfFeedbackId: null));

    [Fact]
    public void call_จริงที่มี_latency_หรือ_token_หรือต้นทุน_ต้องไม่ถูกล้าง()
    {
        // ★ ทิศที่อันตรายกว่า: ตีแถวจริงเป็น local = นับต้นทุนขาดถาวร
        Assert.False(AiCallBilling.LooksLikeMislabelledLocalRow(
            AiCallStatus.Success, AiProviderType.DeepSeek, 850, 0, 0, 0m, null));
        Assert.False(AiCallBilling.LooksLikeMislabelledLocalRow(
            AiCallStatus.Success, AiProviderType.DeepSeek, 0, 1200, 0, 0m, null));
        Assert.False(AiCallBilling.LooksLikeMislabelledLocalRow(
            AiCallStatus.Success, AiProviderType.DeepSeek, 0, 0, 300, 0m, null));
        Assert.False(AiCallBilling.LooksLikeMislabelledLocalRow(
            AiCallStatus.Success, AiProviderType.DeepSeek, 0, 0, 0, 0.0004m, null));
    }

    [Fact]
    public void แถวลูกของ_bulk_ต้องไม่ถูกล้าง_เพราะ_0_โดยชอบธรรม()
        => Assert.False(AiCallBilling.LooksLikeMislabelledLocalRow(
            AiCallStatus.Success, AiProviderType.DeepSeek, 0, 0, 0, 0m, Guid.NewGuid()));

    [Fact]
    public void แถวที่ติดป้ายถูกอยู่แล้ว_ไม่ต้องล้างซ้ำ()
        => Assert.False(AiCallBilling.LooksLikeMislabelledLocalRow(
            AiCallStatus.LocalServed, AiProviderType.None, 0, 0, 0, 0m, null));
}

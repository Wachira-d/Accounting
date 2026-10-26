using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// W9 (<c>docs/BACKLOG_REGISTER_2026-10-26.md</c> §3) — ตัวตัดสิน pure ของแพ็ก hygiene:
/// R-58 ช่องทาง LINE bot · R-59 แถว feedback ที่ห้ามสอน · R-61 ผู้ลงนามที่ออกจากบริษัทแล้ว
/// ทุกข้อมีทิศตรงข้าม (ของที่ถูกอยู่แล้วไม่ถูกแตะ)
/// </summary>
public class W9HygieneRuleTests
{
    // ── R-58 ──
    [Theory]
    [InlineData("LineBot")]
    [InlineData("linebot")]
    public void LINE_ประทับ_OriginModule_แล้วขึ้นช่องทาง_LINE(string origin)
    {
        // CreatedBy ของ LINE เป็นอีเมลผู้ใช้ (MarkerEmail) — เดิมตกไป "ไม่ทราบ"
        var c = DocumentCreationChannelRule.Classify(new DocumentOriginProbe(origin, DocumentCreationChannelRule.MarkerEmail));
        Assert.Equal(DocumentCreationChannel.LineBot, c);
        Assert.Equal("LINE bot", DocumentCreationChannelRule.Label(c));
        Assert.Null(DocumentCreationChannelRule.Caveat(c));   // หลักฐานตรง ไม่ต้องมีหมายเหตุ
    }

    [Fact]
    public void LINE_ที่ยังไม่backfill_ยังเป็นไม่ทราบ_ไม่เดา()
        => Assert.Equal(DocumentCreationChannel.Unknown,
            DocumentCreationChannelRule.Classify(new DocumentOriginProbe(null, DocumentCreationChannelRule.MarkerEmail)));

    [Fact]
    public void SQL_backfill_LINE_แตะเฉพาะแถว_OriginModule_NULL_และ_Reference_ตรงตัว()
    {
        var sql = DocumentCreationChannelRule.BackfillLineBotOriginSql();
        Assert.Contains("\"OriginModule\" IS NULL", sql);
        Assert.Contains($"\"Reference\" = '{DocumentCreationChannelRule.LineBotReference}'", sql);
        Assert.Contains($"\"OriginModule\" = '{DocumentCreationChannelRule.LineBotOrigin}'", sql);
        Assert.Equal("LINE-bot", DocumentCreationChannelRule.LineBotReference);   // ค่าที่ LineBotService เขียนมาตั้งแต่ก่อน W9
        Assert.DoesNotContain("LIKE", sql);   // ตรงตัวเท่านั้น — Reference อื่นไม่ถูกแตะ
    }

    // ── R-59 ──
    private static readonly DateTime Cutoff = AiFeedbackTrainingExclusion.DocTypePromptV2CutoffUtc;

    [Fact]
    public void แถว_DocType_ก่อน_cutoff_ที่มีแค่คำตอบครู_ถูกตัดออกจากการสอน()
        => Assert.True(AiFeedbackTrainingExclusion.IsLegacyDocTypeTeacherOnlyRow(
            nameof(AiFeatureKey.DocumentTypeClassification), Cutoff.AddDays(-1), userChosenAt: null));

    [Fact]
    public void แถวที่ผู้ใช้เลือกแล้ว_ไม่ถูกตัด_แม้ก่อน_cutoff()
        => Assert.False(AiFeedbackTrainingExclusion.IsLegacyDocTypeTeacherOnlyRow(
            nameof(AiFeatureKey.DocumentTypeClassification), Cutoff.AddDays(-1), userChosenAt: Cutoff.AddHours(-1)));

    [Fact]
    public void แถวหลัง_cutoff_ไม่ถูกแตะ_ทิศตรงข้าม()
    {
        Assert.False(AiFeedbackTrainingExclusion.IsLegacyDocTypeTeacherOnlyRow(
            nameof(AiFeatureKey.DocumentTypeClassification), Cutoff, null));   // ตรง cutoff = prompt v2 แล้ว
        Assert.False(AiFeedbackTrainingExclusion.IsLegacyDocTypeTeacherOnlyRow(
            nameof(AiFeatureKey.DocumentTypeClassification), Cutoff.AddMinutes(1), null));
    }

    [Fact]
    public void feature_อื่น_ไม่ถูกแตะ()
        => Assert.False(AiFeedbackTrainingExclusion.IsLegacyDocTypeTeacherOnlyRow(
            nameof(AiFeatureKey.DocumentRoleInference), Cutoff.AddDays(-30), null));

    [Fact]
    public void SQL_ตัดแถว_ไม่ลบ_ไม่แก้คำตอบ_และใช้เงื่อนไขเดียวกับตัวตัดสิน()
    {
        var sql = AiFeedbackTrainingExclusion.DocTypePromptV1BackfillSql();
        Assert.StartsWith("UPDATE \"AiSuggestionFeedbacks\" SET \"ExcludedFromTraining\" = true", sql);
        Assert.DoesNotContain("DELETE", sql);
        Assert.DoesNotContain("AiPrimaryAnswer", sql);
        Assert.DoesNotContain("UserChosenAnswer\" =", sql);
        Assert.Contains("\"FeatureKey\" = 'DocumentTypeClassification'", sql);
        Assert.Contains("\"UserChosenAt\" IS NULL", sql);
        Assert.Contains("TIMESTAMP '2026-10-22 15:54:58'", sql);
        Assert.Contains("\"ExcludedFromTraining\" = false", sql);   // idempotent
        Assert.Contains(AiFeedbackTrainingExclusion.ReasonDocTypePromptV1, sql);
    }

    // ── R-61 (2) ──
    [Fact]
    public void ผู้ใช้ที่ไม่มีแถวสมาชิกในบริษัท_คือผู้ลงนามที่ออกแล้ว()
    {
        Assert.True(DepartedSignerRule.IsDeparted(Guid.NewGuid(), null));
        Assert.True(DepartedSignerRule.IsDeparted(Guid.NewGuid(), ""));
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Staff")]
    [InlineData("Viewer")]
    public void สมาชิกปัจจุบันทุกบทบาท_วาดเหมือนเดิม(string role)
        => Assert.False(DepartedSignerRule.IsDeparted(Guid.NewGuid(), role));

    [Fact]
    public void ช่องที่ไม่ใช่ผู้ใช้ในระบบ_ไม่เกี่ยวกับด่านนี้()
        => Assert.False(DepartedSignerRule.IsDeparted(null, null));   // ลูกค้าเซ็นรับ/คู่ค้าส่งมา

    [Fact]
    public void กฎตราที่ไม่ตั้ง_ปล่อยผ่านทุกคน_จึงต้องมีด่านผู้ลงนามที่ออกแล้วแยก()
    {
        // ยืนยันเหตุผลที่ DepartedSignerRule ต้องอยู่: Decision.SignerAllowed(role=null) = true เมื่อไม่มีกฎ
        var d = DocumentStampPolicy.Resolve(DocumentStampPolicy.KeyOf(DocumentType.TaxInvoice), DocumentStampPolicy.Stage.Approved,
            (string?)null, new DocumentStampPolicy.Legacy(32m, 32m, null));
        Assert.True(d.SignerAllowed(Guid.NewGuid(), null));
    }

    [Fact]
    public void ป้ายผู้ลงนามที่ออกแล้ว_มีทั้งไทยและอังกฤษ()
    {
        Assert.Equal("ผู้ลงนามไม่อยู่ในบริษัทแล้ว", Accounting.Services.Implementations.Pdf.DocumentLabels.For("th").SigDeparted);
        Assert.Equal("Signer no longer with the company", Accounting.Services.Implementations.Pdf.DocumentLabels.For("en").SigDeparted);
    }
}

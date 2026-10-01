using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Distillation;
using Accounting.Services.Ai.Prompts;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม AI · A-AI4 / A-AI2 / A-AI5 (report-H H-6 / H-4 / H-7) — ใบสำคัญจ่าย "AI แนะนำผังบัญชี" ทั้งใบ
///
/// ของจริงที่พัง: ปิด provider แล้วกดปุ่ม → นักเรียน generic ตอบรหัสยอดฮิตคำเดียว → parser แปลงเป็นโครงไม่ได้ →
/// "เติมให้ 0/N บรรทัด" + "AI ตอบกลับ JSON ไม่ valid" ทั้งที่ไม่ได้เรียก AI · ป้าย "⚙️ ระบบ (local model)" ขึ้นทั้งที่ไม่มีใครตอบ ·
/// เกณฑ์เติมให้ 0.70 อยู่ใน JS ตัวเดียว. เทสต์เรียก <b>จุดที่ใช้คำตอบจริง</b> (<c>ParseBulkPvResponse</c>) ไม่ใช่แค่ helper
/// </summary>
public class BulkPvStudentTests
{
    private static readonly Guid L1 = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid L2 = Guid.Parse("11111111-0000-0000-0000-000000000002");

    private static readonly IReadOnlyList<(Guid LineId, string Description, decimal Amount, string? CurrentAccountCode)> Lines =
        new List<(Guid, string, decimal, string?)>
        {
            (L1, "ค่าน้ำมันรถกระบะ", 1500m, null),
            (L2, "ค่าทางด่วน", 120m, null),
        };

    /// <summary>คำถามทั้งใบจากตัวสร้าง prompt <b>ตัวจริง</b> — ผูกรูปคำถามกับนักเรียน (เปลี่ยนรูป prompt แล้วเทสต์ต้องล้ม)</summary>
    private static string BulkPrompt() => BulkPvAccountingPrompt.Build(
        Guid.NewGuid(), Guid.NewGuid(),
        new BulkPvAccountingPrompt.VendorContext("ปตท. สาขา 1", "0107544000108", null, "Fuel", null),
        new BulkPvAccountingPrompt.SourceInvoiceContext("PI-001", "PurchaseInvoice", 1620m, null, null, 1620m),
        Lines.Select(l => new BulkPvAccountingPrompt.LineInput(l.LineId.ToString(), l.Description, l.Amount, null)).ToList(),
        Array.Empty<BulkPvAccountingPrompt.AccountCandidate>(),
        Array.Empty<BulkPvAccountingPrompt.VendorHistoricalAccount>(),
        "THB").UserPromptJson;

    // ── นักเรียน: แตกคำถามทั้งใบเป็นรายบรรทัดด้วยกุญแจรูปเดียวกับแถว feedback ลูก ─────────────

    [Fact]
    public async Task นักเรียนแตกคำถามทั้งใบเป็นรายบรรทัด_กุญแจต้องตรงกับแถว_feedback_ลูก_และตอบเป็นโครงทั้งใบ()
    {
        var asked = new List<string>();
        var expected1 = PaymentVoucherAccountingDistillationModel.BuildPerLineInputJson(
            "ปตท. สาขา 1", "0107544000108", "Fuel", "ค่าน้ำมันรถกระบะ", 1500m);
        var pred = await PaymentVoucherAccountingDistillationModel.PredictWithAsync(Guid.NewGuid(), BulkPrompt(),
            (cid, json, ct) =>
            {
                asked.Add(json);
                var code = json.Contains("น้ำมัน") ? "53110" : "53120";
                return Task.FromResult<LocalPrediction?>(new LocalPrediction(code, 0.90m, Array.Empty<string>(), 4, "v1"));
            }, CancellationToken.None);

        Assert.Equal(2, asked.Count);
        Assert.Equal(expected1, asked[0]);
        Assert.NotNull(pred);
        Assert.Equal(0.90m, pred!.Confidence);
        using var doc = JsonDocument.Parse(pred.StructuredJson!);
        var lines = doc.RootElement.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(L1.ToString(), lines[0].GetProperty("lineId").GetString());
        Assert.Equal("53110", lines[0].GetProperty("accountCode").GetString());
    }

    [Fact]
    public async Task นักเรียนตอบไม่ครบทุกบรรทัด_ความมั่นใจรวมต้องเป็นศูนย์_ไม่ข้ามครู()
    {
        var pred = await PaymentVoucherAccountingDistillationModel.PredictWithAsync(Guid.NewGuid(), BulkPrompt(),
            (cid, json, ct) => Task.FromResult<LocalPrediction?>(json.Contains("น้ำมัน")
                ? new LocalPrediction("53110", 0.95m, Array.Empty<string>(), 9, "v1") : null),
            CancellationToken.None);
        Assert.NotNull(pred);
        Assert.Equal(0m, pred!.Confidence);
        Assert.Contains("1/2", pred.StructuredJson);
    }

    [Fact]
    public async Task คำถามรายบรรทัด_ต้องส่งต่อให้นักเรียนตัวในตรง_ๆ_พฤติกรรมเดิม()
    {
        var single = PaymentVoucherAccountingDistillationModel.BuildPerLineInputJson("A", null, null, "ค่าไฟ", 100m);
        string? seen = null;
        var pred = await PaymentVoucherAccountingDistillationModel.PredictWithAsync(Guid.NewGuid(), single,
            (cid, json, ct) => { seen = json; return Task.FromResult<LocalPrediction?>(
                new LocalPrediction("53300", 0.6m, Array.Empty<string>(), 1, "v1")); }, CancellationToken.None);
        Assert.Equal(single, seen);
        Assert.Equal("53300", pred!.PrimaryAnswer);
        Assert.Null(pred.StructuredJson);
    }

    // ── จุดใช้คำตอบ (ParseBulkPvResponse): kill-switch + ข้อความซื่อสัตย์ ─────────────────────

    private static string Structured(params (Guid Id, string Code, decimal Conf)[] rows) => JsonSerializer.Serialize(new
    {
        lines = rows.Select(r => new { lineId = r.Id.ToString(), accountCode = r.Code, confidence = r.Conf }),
    });

    [Fact]
    public void ปิด_provider_นักเรียนตอบเป็นโครง_ต้องเติมได้ทุกบรรทัด_และไม่โทษ_AI()
    {
        var resp = new AiResponse
        {
            Status = AiCallStatus.NoProvider, UsedAi = false, FromLocalModel = true,
            PrimaryAnswer = "53110", RawResponseJson = Structured((L1, "53110", 0.9m), (L2, "53120", 0.8m)),
        };
        var r = DocumentAiAugmenter.ParseBulkPvResponse(resp, Lines);
        Assert.True(r.FromLocalModel);
        Assert.False(r.UsedAi);
        Assert.Equal("53110", r.ByLineId[L1].Answer);
        Assert.True(r.ByLineId[L1].FromStudent);
        Assert.True(r.ByLineId[L1].HasModelAnswer);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("AI"));
    }

    [Fact]
    public void นักเรียนตอบคำเดี่ยว_ห้ามเอาไป_parse_เป็น_JSON_และต้องบอกว่าโมเดลในบ้านยังไม่มีคำตอบ()
    {
        var resp = new AiResponse
        {
            Status = AiCallStatus.NoProvider, UsedAi = false, FromLocalModel = true, PrimaryAnswer = "53110",
        };
        var r = DocumentAiAugmenter.ParseBulkPvResponse(resp, Lines);
        Assert.False(r.ByLineId[L1].HasModelAnswer);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("AI"));
        Assert.Contains(r.Warnings, w => w.Contains("โมเดลในบ้าน"));
    }

    [Fact]
    public void ไม่มีใครตอบเลย_ข้อความเดียว_ไม่โทษ_AI_ที่ไม่ได้ถูกถาม_และป้ายไม่บอกว่าระบบแนะนำ()
    {
        var resp = new AiResponse { Status = AiCallStatus.NoProvider, UsedAi = false, FromLocalModel = false };
        var r = DocumentAiAugmenter.ParseBulkPvResponse(resp, Lines);
        Assert.Single(r.Warnings);
        Assert.Contains("ยังไม่มีคำแนะนำ", r.Warnings[0]);
        Assert.DoesNotContain("JSON", r.Warnings[0]);
        Assert.False(r.ByLineId[L1].HasAnswer);
        Assert.Equal("ยังไม่มีคำแนะนำ", AiAnswerSource.Label(AiAnswerSource.Of(r.UsedAi, r.FromLocalModel)));
    }

    [Fact]
    public void AI_ตอบจริง_ต้องยังใช้ได้เหมือนเดิม_และป้ายต้องเป็น_AI()
    {
        var resp = new AiResponse
        {
            Status = AiCallStatus.Success, UsedAi = true,
            RawResponseJson = Structured((L1, "53110", 0.92m), (L2, "53120", 0.88m)),
        };
        var r = DocumentAiAugmenter.ParseBulkPvResponse(resp, Lines);
        Assert.True(r.UsedAi);
        Assert.False(r.ByLineId[L2].FromStudent);
        Assert.Equal("53120", r.ByLineId[L2].Answer);
        Assert.Empty(r.Warnings);
        Assert.Equal("🤖 AI", AiAnswerSource.Label(AiAnswerSource.Of(r.UsedAi, r.FromLocalModel)));
    }

    [Fact]
    public void AI_ตอบรูปที่อ่านไม่ได้_ข้อความต้องระบุว่าเป็น_AI_จริง()
    {
        var resp = new AiResponse { Status = AiCallStatus.Success, UsedAi = true, RawResponseJson = "53110" };
        var r = DocumentAiAugmenter.ParseBulkPvResponse(resp, Lines);
        Assert.Contains(r.Warnings, w => w.StartsWith("AI ตอบกลับในรูปที่อ่านไม่ได้"));
    }

    // ── write-gate ฝั่งเซิร์ฟเวอร์ (A-AI5) ──────────────────────────────────────────────

    [Theory]
    [InlineData("53110", 0.70, true, true, true)]
    [InlineData("53110", 0.69, true, true, false)]   // ต่ำกว่าเกณฑ์
    [InlineData("53110", 0.95, true, false, false)]  // ไม่อยู่ในผังของบริษัท (candidate set)
    [InlineData("53110", 0.95, false, true, false)]  // ไม่มีผู้ตอบจริง (ค่าเดิมของบรรทัดที่ส่งคืน)
    [InlineData("", 0.95, true, true, false)]
    public void เติมผังให้เองได้เมื่อครบ_ความมั่นใจ_070_ผังของบริษัท_และมีผู้ตอบจริง(
        string code, double conf, bool hasAnswer, bool inCoa, bool expected)
        => Assert.Equal(expected, GlSuggestionApplyPolicy.MayAutoFill(code, (decimal)conf, hasAnswer, inCoa));

    [Fact]
    public void คำตอบ_tier2_ของนักเรียน_generic_เพดาน_045_ต้องเติมให้เองไม่ได้()
        => Assert.False(GlSuggestionApplyPolicy.MayAutoFill("53110", 0.45m, hasAnswer: true, inChartOfAccounts: true));

    [Fact]
    public void ธงนักเรียนต้องเดินทางผ่าน_DocumentAiSuggestion_และ_HasModelAnswer_ไม่นับกฎ()
    {
        var student = new Accounting.Services.Ai.DocumentAiSuggestion("53110", 0.8m, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<string>(), UsedAi: false, FeedbackId: null,
            FromStudent: true);
        Assert.True(student.HasModelAnswer);
        var rule = student with { FromStudent = false, FromRule = true };
        Assert.False(rule.HasModelAnswer);
        Assert.True(rule.HasAnswer);
        var nobody = student with { FromStudent = false };
        Assert.False(nobody.HasAnswer);
    }
}

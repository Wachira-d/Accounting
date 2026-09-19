using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **Kill-switch test — "ถ้าตัด DeepSeek/Claude ออกวันนี้ feature ยังครบไหม"**
/// (กฎเหล็ก #1 ข้อ 5 + "🛡️ Local-First Sovereignty")
///
/// <para>เทสต์ชุดนี้<b>ไม่แตะ provider เลย</b> โดยตั้งใจ — นั่นคือประเด็นทั้งหมด:
/// ทุกคำตอบด้านล่างผลิตจาก pure helper ล้วน ⇒ ถ้ามันผ่านตอนไม่มี orchestrator
/// อยู่ในภาพเลย แปลว่ามันผ่านตอน <c>AiProviderConfig.IsActive = false</c> ทุกตัวด้วย</para>
///
/// <para><b>ของจริงที่ไม่ผ่านก่อนรอบ 184</b>: <c>AiFeatureKey.ReorderForecast</c>
/// ยิง provider โดยไม่มี <c>ILocalDistillationModel</c> ⇒ ปิด provider แล้ว endpoint
/// ตอบ "AI ปิดอยู่หรือไม่พร้อมใช้งาน" = ผู้ใช้ไม่ได้อะไรเลย · D-5 แก้ด้วยการ
/// <b>ถอดการเรียก AI ทิ้ง</b> ไม่ใช่การ register นักเรียนให้ร้อยแก้ว</para>
/// </summary>
public class AiKillSwitchTests
{
    [Fact]
    public void รายงานจุดสั่งซื้อ_ต้องได้ประโยคจริงโดยไม่ต้องมี_AI()
    {
        var s = ReorderNarrative.Build(new[]
        {
            new ReorderRow("A-001", "กระดาษ A4", 5m, 2m, 2.5m, 50m, "Critical"),
        });
        Assert.False(string.IsNullOrWhiteSpace(s));
        Assert.Contains("A-001", s);
        // ห้ามมีข้อความ "AI ปิดอยู่" เป็นคำตอบสุดท้ายอีก
        Assert.DoesNotContain("AI ปิดอยู่", s);
        Assert.DoesNotContain("ไม่พร้อมใช้งาน", s);
    }

    [Fact]
    public void ไปป์ไลน์_OCR_ชั้นตัดสิน_ตอบได้ครบทุกใบโดยไม่มี_AI()
    {
        var answers = OcrReplayHarness.Run();
        Assert.NotEmpty(answers);
        foreach (var p in OcrReplayHarness.Corpus)
        {
            // ทุกใบต้องมีคำตอบของ "ธงมัดจำ" และ "ป้ายยอดสลับ" เสมอ — สองช่องนี้
            // เป็น bool จึงห้ามเป็น null ไม่ว่ากรณีใด (ไม่รู้ต้องเป็นค่า ไม่ใช่ว่าง)
            Assert.NotNull(answers.Single(a => a.Paper == p.Name && a.Field == "IsDeposit").Value);
            Assert.NotNull(answers.Single(a => a.Paper == p.Name && a.Field == "HeaderSwapped").Value);
        }
    }

    [Fact]
    public void ข้อเสนอหัก_ณ_ที่จ่าย_ตัดสินได้จากตารางกฎหมาย_ไม่ต้องถาม_AI()
    {
        // ชั้น Statute มาจาก ThaiWhtRateTable — ตารางกฎหมาย ไม่ใช่โมเดล
        Assert.NotNull(ThaiWhtRateTable.Find("8"));
        var v = OcrWhtSuggestionGate.Judge(false, true, 3m, 10, 0.95m);
        Assert.True(v.Suggest);
    }

    [Fact]
    public void สมุดที่มา_ยังตอบได้เมื่อไม่มีผู้เสนอที่เป็น_AI_เลย()
    {
        var rep = OcrFieldProvenance.Build(
            new[]
            {
                new OcrFieldCandidate(OcrFieldKeys.TotalAmount, "1070.00", OcrFieldSource.PaperLabel, 0.95m),
                new OcrFieldCandidate(OcrFieldKeys.SuggestedWhtRate, "3.00", OcrFieldSource.Statute, 0.8m),
            },
            new Dictionary<string, string?>
            {
                [OcrFieldKeys.TotalAmount] = "1070.00",
                [OcrFieldKeys.SuggestedWhtRate] = "3.00",
            });
        Assert.Equal(1.0m, rep.ArbiterAgreed);
        Assert.DoesNotContain(rep.Fields, f => f.Source == OcrFieldSource.Ai);
    }

    /// <summary>ตัวชี้วัดต้อง<b>ไม่</b>ตีความ "ไม่มีการเรียกครูเลย" ว่าสุขภาพดี —
    /// นี่คือกับดักที่ทำให้ kill-switch ที่ไม่ผ่านดูเหมือนผ่าน</summary>
    [Fact]
    public void ปิด_provider_ทั้งระบบ_แล้วนักเรียนก็ตอบไม่ได้_ต้องถูกรายงานว่าเงียบ_ไม่ใช่โต()
    {
        var v = LocalGrowthVerdict.Judge(
            samples30d: 200, localSamples30d: 10, aiSamples30d: 0,
            localAccuracy30d: 1.0m, explicitLabels30d: 40);
        Assert.Equal(LocalGrowthState.GoingQuiet, v.State);
    }
}

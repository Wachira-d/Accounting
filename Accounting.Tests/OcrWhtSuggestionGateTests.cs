using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// D-3 — "ประวัติใบเดียวไม่ใช่ประวัติ"
///
/// <para>เทสต์มี<b>สองครึ่ง</b>ตามกฎเหล็ก #4 H/G7:</para>
/// <list type="bullet">
/// <item>ครึ่งที่พิสูจน์ว่า<b>ใบที่พังกลับมาถูก</b> — ผู้ขายที่มีเอกสาร 1–2 ใบ
///   ไม่ถูกเสนออัตราอีกต่อไป (เดิม <c>WhtConfidence = 1.0</c> ทันทีที่ใบแรก)</item>
/// <item>ครึ่งที่พิสูจน์ว่า<b>ใบที่ถูกอยู่แล้วไม่ถูกแตะ</b> — กระดาษที่พิมพ์ส่วนหักเอง
///   และผู้ขายที่มีประวัติจริง 5 ใบ ยังได้ผลเหมือนเดิม</item>
/// </list>
/// </summary>
public class OcrWhtSuggestionGateTests
{
    // ── ครึ่งที่ 1: ใบที่เคยพัง (ประวัติบาง) ต้องไม่ถูกเสนออีก ────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ประวัติน้อยกว่าสามใบ_ห้ามเสนอ_แม้ความเด่นจะเป็นหนึ่งเต็ม(int docs)
    {
        var v = OcrWhtSuggestionGate.Judge(
            paperAlreadyAnswered: false, historyLeansToWithhold: true,
            historyRate: 3m, totalDocuments: docs, dominance: 1.0m);
        Assert.False(v.Suggest);
        Assert.Contains("ประวัติเพียง", v.Reason);
    }

    [Fact]
    public void ความเด่น_0_65_ที่เคยผ่านเกณฑ์เดิม_ตอนนี้ไม่ผ่าน()
    {
        // 0.65 = VendorIntelligenceService.MediumConfidence (เกณฑ์เดิมก่อนรอบ 184)
        var v = OcrWhtSuggestionGate.Judge(false, true, 3m, totalDocuments: 10, dominance: 0.65m);
        Assert.False(v.Suggest);
        Assert.Contains("ต่ำกว่าเกณฑ์", v.Reason);
    }

    [Fact]
    public void ไม่มีอัตราที่ใช้บ่อย_ห้ามเดาอัตราให้()
    {
        var v = OcrWhtSuggestionGate.Judge(false, true, historyRate: null,
            totalDocuments: 20, dominance: 0.95m);
        Assert.False(v.Suggest);
        Assert.Contains("ไม่เดาอัตรา", v.Reason);
    }

    [Fact]
    public void ประวัติเอนไปทางไม่หัก_ห้ามเสนอ()
        => Assert.False(OcrWhtSuggestionGate.Judge(false, false, 3m, 50, 0.99m).Suggest);

    // ── ครึ่งที่ 2: ใบที่ถูกอยู่แล้ว ต้องไม่ถูกแตะ ────────────────────────────

    [Fact]
    public void ประวัติห้าใบความเด่น_0_9_ยังเสนอได้เหมือนเดิม()
    {
        var v = OcrWhtSuggestionGate.Judge(false, true, 3m, totalDocuments: 5, dominance: 0.9m);
        Assert.True(v.Suggest);
        Assert.Contains("3", v.Reason);
    }

    [Fact]
    public void กระดาษพิมพ์ส่วนหักเอง_ไม่ต้องเสนอจากประวัติ_แต่ค่าบนกระดาษยังอยู่()
    {
        // ใบที่ผู้ขายพิมพ์ "ภาษีหัก ณ ที่จ่าย 3% 300.00" มาเอง — ด่านนี้แค่ "ไม่เสนอซ้ำ"
        // ค่าที่อ่านจากกระดาษเป็นคนละช่อง (WhtRate) และไม่ถูกแตะโดยด่านนี้เลย
        var v = OcrWhtSuggestionGate.Judge(
            paperAlreadyAnswered: true, historyLeansToWithhold: true,
            historyRate: 5m, totalDocuments: 40, dominance: 0.99m);
        Assert.False(v.Suggest);
        Assert.Contains("กระดาษ", v.Reason);
    }

    [Fact]
    public void เกณฑ์ที่พอดีเส้น_ผ่าน()
        => Assert.True(OcrWhtSuggestionGate.Judge(
            false, true, 3m,
            OcrWhtSuggestionGate.MinHistoryDocuments,
            OcrWhtSuggestionGate.MinHistoryConfidence).Suggest);

    // ── ความมั่นใจรายช่องต้องทำให้เกิดไฮไลต์เหลืองเสมอ (กฎเหล็ก #3 ข้อ 3) ──

    [Fact]
    public void ความมั่นใจรายช่องของข้อเสนอ_ต้องต่ำกว่าเกณฑ์ไฮไลต์เหลือง()
        => Assert.True(OcrWhtSuggestionGate.SuggestionFieldConfidence < 0.85,
            "ข้อเสนอที่ไม่ได้อยู่บนกระดาษต้องขึ้นไฮไลต์ 'ตรวจสอบอีกครั้ง' เสมอ");

    [Fact]
    public void เกณฑ์การเสนอ_กับ_ความมั่นใจรายช่อง_ต้องเป็นคนละตัวเลข()
        => Assert.NotEqual((double)OcrWhtSuggestionGate.MinHistoryConfidence,
            OcrWhtSuggestionGate.SuggestionFieldConfidence);

    // ── ที่มาต้องเห็นได้ ไม่ใช่เดา ───────────────────────────────────────────

    [Theory]
    [InlineData(WhtEvidenceSource.Paper)]
    [InlineData(WhtEvidenceSource.Statute)]
    [InlineData(WhtEvidenceSource.VendorHistory)]
    [InlineData(WhtEvidenceSource.User)]
    [InlineData(WhtEvidenceSource.None)]
    public void ทุกที่มามีคำอธิบายไทย(WhtEvidenceSource s)
        => Assert.False(string.IsNullOrWhiteSpace(OcrWhtSuggestionGate.Describe(s)));

    [Theory]
    [InlineData(OcrFieldSource.Statute, WhtEvidenceSource.Statute)]
    [InlineData(OcrFieldSource.VendorHistory, WhtEvidenceSource.VendorHistory)]
    [InlineData(OcrFieldSource.PaperLabel, WhtEvidenceSource.Paper)]
    [InlineData(OcrFieldSource.UserConfirmed, WhtEvidenceSource.User)]
    public void ผู้เสนอของ_arbiter_แปลงเป็นที่มาได้ตรง(OcrFieldSource src, WhtEvidenceSource expected)
        => Assert.Equal(expected, OcrWhtSuggestionGate.FromFieldSource(src));

    [Fact]
    public void ผู้เสนอที่ไม่ใช่สามชั้นนี้_ต้องได้_None_ห้ามเดา()
    {
        Assert.Equal(WhtEvidenceSource.None, OcrWhtSuggestionGate.FromFieldSource(OcrFieldSource.Ai));
        Assert.Equal(WhtEvidenceSource.None, OcrWhtSuggestionGate.FromFieldSource(OcrFieldSource.Guess));
        Assert.Equal(WhtEvidenceSource.None, OcrWhtSuggestionGate.FromFieldSource(OcrFieldSource.Rule));
    }

    // ── ลำดับชั้น: กฎหมายชนะนิสัยผู้ขาย · และการเพิ่มชั้นใหม่ไม่สลับของเดิม ──

    [Fact]
    public void ตารางกฎหมายต้องชนะประวัติผู้ขาย()
        => Assert.True(OcrFieldArbiter.Rank(OcrFieldSource.Statute)
            > OcrFieldArbiter.Rank(OcrFieldSource.VendorHistory));

    [Fact]
    public void ชั้นกฎหมายต้องต่ำกว่าสิ่งที่อ่านจากกระดาษ()
    {
        Assert.True(OcrFieldArbiter.Rank(OcrFieldSource.PaperLabel)
            > OcrFieldArbiter.Rank(OcrFieldSource.Statute));
        Assert.True(OcrFieldArbiter.Rank(OcrFieldSource.Engine)
            > OcrFieldArbiter.Rank(OcrFieldSource.Statute));
    }

    [Fact]
    public void เพิ่มชั้น_Statute_แล้วอันดับเดิมทุกคู่ต้องไม่สลับ()
    {
        // ลำดับเดิมก่อนรอบ 184 (ยกมาจาก git) — ทุกคู่ที่ติดกันต้องยังเรียงเหมือนเดิม
        var before = new[]
        {
            OcrFieldSource.EtaxXml, OcrFieldSource.AzureHighConfidence, OcrFieldSource.PaperLabel,
            OcrFieldSource.Engine, OcrFieldSource.LearnedPattern, OcrFieldSource.Student,
            OcrFieldSource.VendorHistory, OcrFieldSource.Rule, OcrFieldSource.Ai,
            OcrFieldSource.Guess, OcrFieldSource.Unknown,
        };
        for (var i = 1; i < before.Length; i++)
            Assert.True(OcrFieldArbiter.Rank(before[i - 1]) > OcrFieldArbiter.Rank(before[i]),
                $"{before[i - 1]} ต้องยังน่าเชื่อกว่า {before[i]} หลังเพิ่มชั้นใหม่");
    }
}

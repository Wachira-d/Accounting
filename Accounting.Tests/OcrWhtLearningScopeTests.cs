using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ห้ามเรียนจากคำตอบของตัวเอง** (ผลตรวจ 2026-09-18 · D3-2 · DECISION_DOCTRINE §3)
///
/// <para>วงจรที่ต้องตัด: ประวัติผู้ขายเติม <c>HasWht</c> ให้ใบใหม่ → เส้นสร้างเอกสารหัก
/// ณ ที่จ่ายจริง → ด่านอนุมัติอ่าน <c>scan.HasWht</c> แล้วสรุปว่า "กระดาษประกาศ" →
/// <c>VendorIntelligenceService</c> เรียนกลับจาก <c>doc.WithholdingTaxAmount &gt; 0</c>
/// ⇒ ความมั่นใจโตขึ้นเรื่อย ๆ โดยไม่มีหลักฐานใหม่สักชิ้น</para>
///
/// <para>สองครึ่ง: ครึ่งแรกพิสูจน์ว่าเคสที่ "ระบบเสนอเองแล้วไม่มีใครแตะ" ถูกตัดออกจริง ·
/// ครึ่งหลังพิสูจน์ว่าหลักฐานจริง (กระดาษ · คนคีย์ · คนแก้) **ยังเรียนได้เหมือนเดิม**
/// — ด่านที่ตัดทุกใบ = ปิดการเรียนรู้โดยไม่ตั้งใจ</para>
/// </summary>
public class OcrWhtLearningScopeTests
{
    // ── ครึ่งที่ 1: ตัดวงจรสอนตัวเอง ──────────────────────────────────────

    [Fact]
    public void ระบบเสนอเองแล้วไม่มีใครแตะ_ห้ามเรียน()
    {
        var d = OcrWhtLearningScope.Decide(
            hasScan: true, paperShowsWht: false, userCorrectedFields: "VendorName,TotalAmount");
        Assert.False(d.Learn);
        Assert.Equal(WhtLearningEvidence.SystemSuggestedOnly, d.Evidence);
        Assert.Contains("สอนตัวเอง", OcrWhtLearningScope.Explain(d.Evidence));
    }

    [Fact]
    public void สแกนที่ผู้ใช้ไม่ได้แก้อะไรเลย_ก็ห้ามเรียน()
    {
        var d = OcrWhtLearningScope.Decide(true, false, null);
        Assert.False(d.Learn);
        Assert.Equal(WhtLearningEvidence.SystemSuggestedOnly, d.Evidence);
    }

    // ── ครึ่งที่ 2: หลักฐานจริงต้องยังเรียนได้ ────────────────────────────

    [Fact]
    public void กระดาษพิมพ์ส่วนหักไว้เอง_เรียนได้()
    {
        var d = OcrWhtLearningScope.Decide(true, paperShowsWht: true, userCorrectedFields: null);
        Assert.True(d.Learn);
        Assert.Equal(WhtLearningEvidence.Paper, d.Evidence);
    }

    [Fact]
    public void เอกสารคีย์มือ_ไม่มีสแกน_เรียนได้()
    {
        var d = OcrWhtLearningScope.Decide(hasScan: false, paperShowsWht: false, userCorrectedFields: null);
        Assert.True(d.Learn);
        Assert.Equal(WhtLearningEvidence.NoScan, d.Evidence);
    }

    [Theory]
    [InlineData("HasWht")]
    [InlineData("WhtRate")]
    [InlineData("WhtIncomeTypeCode")]
    [InlineData("VendorName,WhtRate,TotalAmount")]
    [InlineData("vendorname,whtrate")]   // ตัวพิมพ์เล็ก/ใหญ่ต้องไม่ทำให้หลักฐานหาย
    public void ผู้ใช้ลงมือแก้ช่อง_WHT_เอง_เรียนได้(string corrected)
    {
        var d = OcrWhtLearningScope.Decide(true, false, corrected);
        Assert.True(d.Learn);
        Assert.Equal(WhtLearningEvidence.UserEdited, d.Evidence);
    }

    [Fact]
    public void ช่องอื่นที่ผู้ใช้แก้_ไม่นับเป็นหลักฐานเรื่อง_WHT()
    {
        // แก้ชื่อผู้ขาย/ยอดรวม ไม่ได้แปลว่าผู้ใช้ตรวจส่วนหัก ณ ที่จ่ายแล้ว
        Assert.False(OcrWhtLearningScope.Decide(true, false, "VendorName,SubTotal,TotalAmount,Lines").Learn);
        Assert.False(OcrWhtLearningScope.Decide(true, false, "").Learn);
        Assert.False(OcrWhtLearningScope.Decide(true, false, null).Learn);
    }

    [Fact]
    public void ทุกชนิดหลักฐานมีคำอธิบาย_ห้ามให้ผู้เรียกแต่งคำเอง()
    {
        foreach (var e in Enum.GetValues<WhtLearningEvidence>())
            Assert.False(string.IsNullOrWhiteSpace(OcrWhtLearningScope.Explain(e)));
    }
}

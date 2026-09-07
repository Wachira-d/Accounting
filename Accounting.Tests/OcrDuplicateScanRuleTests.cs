using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่านกันสร้างเอกสารซ้ำจากผลสแกน (<see cref="OcrDuplicateScanRule"/>)
///
/// <para>ที่มา (ผลตรวจ OCR 2026-09-06 · T1-14): เดิมต้อง <b>เลขที่ + ยอด
/// ตรงถึงสตางค์</b> ถึงจะเตือน — ยอดต่างแม้บาทเดียวก็เงียบ ⇒ OCR อ่าน 1,070
/// เป็น 1,010 (หลักเดียวเพี้ยน = ความผิดพลาดที่พบบ่อยที่สุด) = ใบซ้ำหลุด ⇒
/// <b>เคลมภาษีซื้อสองครั้ง</b> จากใบกำกับใบเดียว</para>
///
/// <para>§86/4 บังคับให้เลขใบกำกับ unique ต่อผู้ขาย ⇒ "ผู้ขายเดียวกัน +
/// เลขเดียวกัน" = ใบเดียวกันเสมอ</para>
/// </summary>
public class OcrDuplicateScanRuleTests
{
    private static readonly DateTime Aug = new(2026, 8, 15);

    private static DuplicateCandidate Doc(decimal amount, string taxId = "0105558123456",
        DateTime? date = null)
        => new(Guid.NewGuid(), "PI-2026-0001", date ?? Aug, amount, taxId);

    [Fact]
    public void เลขที่และยอดตรงกัน_ต้องเตือนเหมือนเดิม()
    {
        var d = OcrDuplicateScanRule.Decide(new[] { Doc(1070m) }, 1070m, "0105558123456", Aug);
        Assert.Equal(OcrDuplicateVerdict.SameNumberAndAmount, d.Verdict);
    }

    [Fact]
    public void เลขที่ตรงแต่_OCR_อ่านยอดผิดหลักเดียว_ต้องเตือนพร้อมบอกส่วนต่าง()
    {
        // นี่คือเคสที่ตรรกะเดิมปล่อยหลุด: ใบเดิม 1,070 · OCR อ่าน 1,010
        var d = OcrDuplicateScanRule.Decide(new[] { Doc(1070m) }, 1010m, "0105558123456", Aug);
        Assert.Equal(OcrDuplicateVerdict.SameNumberDifferentAmount, d.Verdict);
        Assert.Equal(60m, d.AmountGap);
    }

    [Fact]
    public void ยอดต่างระดับปัดเศษ_ยังถือว่าเป็นยอดเดียวกัน()
    {
        var d = OcrDuplicateScanRule.Decide(new[] { Doc(1070m) }, 1070.01m, "0105558123456", Aug);
        Assert.Equal(OcrDuplicateVerdict.SameNumberAndAmount, d.Verdict);
    }

    [Fact]
    public void ผู้ขายคนละรายที่บังเอิญใช้เลขรันเดียวกัน_ต้องไม่เตือน()
    {
        var d = OcrDuplicateScanRule.Decide(
            new[] { Doc(1070m, taxId: "0994000158441") }, 1070m, "0105558123456", Aug);
        Assert.Equal(OcrDuplicateVerdict.None, d.Verdict);
    }

    [Fact]
    public void ไม่ทราบเลขผู้ขายฝั่งใดฝั่งหนึ่ง_ต้องยังเตือน_ไม่ใช่ปล่อยผ่าน()
    {
        // "ไม่ทราบ" ≠ "ไม่ตรง" — ปล่อยใบซ้ำหลุดเพราะข้อมูลไม่ครบคือทิศที่แพงกว่า
        Assert.Equal(OcrDuplicateVerdict.SameNumberAndAmount,
            OcrDuplicateScanRule.Decide(new[] { Doc(1070m, taxId: "") }, 1070m, "0105558123456", Aug).Verdict);
        Assert.Equal(OcrDuplicateVerdict.SameNumberAndAmount,
            OcrDuplicateScanRule.Decide(new[] { Doc(1070m) }, 1070m, null, Aug).Verdict);
    }

    [Fact]
    public void เลขเดียวกันแต่ห่างกันเกินหนึ่งปี_ถือเป็นคนละใบ()
    {
        // ผู้ขายจำนวนมากรีเซ็ตเลขวิ่งทุกปี — ใบปีก่อนไม่ใช่ใบซ้ำ
        var old = Doc(1070m, date: new DateTime(2024, 8, 15));
        var d = OcrDuplicateScanRule.Decide(new[] { old }, 1070m, "0105558123456", Aug);
        Assert.Equal(OcrDuplicateVerdict.None, d.Verdict);
    }

    [Fact]
    public void OCR_อ่านยอดไม่ได้เลย_เลขที่ตรงก็ยังต้องเตือน()
    {
        var d = OcrDuplicateScanRule.Decide(new[] { Doc(1070m) }, null, "0105558123456", Aug);
        Assert.Equal(OcrDuplicateVerdict.SameNumberDifferentAmount, d.Verdict);
    }

    [Fact]
    public void ไม่มีใบไหนใช้เลขที่นี้_ต้องผ่าน()
        => Assert.Equal(OcrDuplicateVerdict.None,
            OcrDuplicateScanRule.Decide(Array.Empty<DuplicateCandidate>(), 1070m, "0105558123456", Aug).Verdict);

    [Fact]
    public void มีหลายใบใช้เลขเดียวกัน_ต้องเลือกใบที่ยอดใกล้ที่สุดมาอธิบาย()
    {
        var d = OcrDuplicateScanRule.Decide(
            new[] { Doc(5000m), Doc(1070m), Doc(900m) }, 1010m, "0105558123456", Aug);
        Assert.Equal(OcrDuplicateVerdict.SameNumberDifferentAmount, d.Verdict);
        Assert.Equal(1070m, d.Match!.TotalAmount);
        Assert.Equal(60m, d.AmountGap);
    }

    [Fact]
    public void ไม่รู้วันที่บนเอกสาร_ต้องไม่กรองเวลาทิ้ง()
    {
        var old = Doc(1070m, date: new DateTime(2024, 8, 15));
        Assert.Equal(OcrDuplicateVerdict.SameNumberAndAmount,
            OcrDuplicateScanRule.Decide(new[] { old }, 1070m, "0105558123456", null).Verdict);
    }
}

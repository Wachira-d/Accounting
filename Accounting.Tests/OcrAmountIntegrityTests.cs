using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 190 ข้อ 9 — "ยอดตอน OCR มายอดรวมไม่ตรงกับในเอกสาร <b>ไม่ควรปล่อยผ่านมาได้</b>" ·
/// <see cref="OcrAmountIntegrity"/> ตรวจ<b>บรรทัดที่ตัวสร้างเอกสารจะเขียนจริง</b>
///
/// <para>บรรทัดในเทสต์คือผลของขั้นตัวเลขใน <c>OcrService.BuildScanLinesAsync</c> บนกระดาษใน
/// <see cref="OcrPaperSamples"/> — VAT รายบรรทัดคำนวณด้วยตัวเฉลี่ยตัวจริง
/// (<see cref="ThaiVatTypeRule.SpreadHeaderVat"/>) · ยอดหลังกระจายส่วนลดคิดตามสูตรของเคส C/E
/// (ตามสัดส่วน · เศษลงบรรทัดสุดท้าย) แล้วจดเป็นตัวเลขไว้</para>
///
/// <para><b>ครึ่งที่ 1</b> ใบที่ต้องถูกจับ (ส่วนลดอ่านผิด · ใบผสมที่ติดอัตราผิด · บรรทัดติดลบ · Makro ไม่มีรายการ)
/// · <b>ครึ่งที่ 2</b> ใบที่ต้องเงียบ (Wine Pro ใบ A · ใบส่วนลดที่ลงตัวแล้ว · ใบผสมที่อัตราถูก · เศษปัดรายบรรทัด)</para>
/// </summary>
public class OcrAmountIntegrityTests
{
    /// <summary>บรรทัดราคารวม VAT: VAT เฉลี่ยจากหัวใบ แล้วถอดออกจากยอด (สูตรเดียวกับตัวสร้างเอกสาร)</summary>
    private static List<OcrPlannedLine> InclVat(decimal[] amounts, decimal[] rates, decimal headerVat)
    {
        var spread = ThaiVatTypeRule.SpreadHeaderVat(
            amounts.Select((a, i) => (a, rates[i])).ToList(), headerVat);
        return amounts.Select((a, i) => new OcrPlannedLine(Math.Round(a - spread[i], 2, MidpointRounding.AwayFromZero), rates[i], spread[i])).ToList();
    }

    /// <summary>บรรทัดราคาก่อน VAT: VAT เฉลี่ยจากหัวใบลงบรรทัดที่มีอัตรา</summary>
    private static List<OcrPlannedLine> ExclVat(decimal[] amounts, decimal[] rates, decimal headerVat)
    {
        var spread = ThaiVatTypeRule.SpreadHeaderVat(
            amounts.Select((a, i) => (a, rates[i])).ToList(), headerVat);
        return amounts.Select((a, i) => new OcrPlannedLine(a, rates[i], spread[i])).ToList();
    }

    private static readonly decimal[] Seven3 = { 7m, 7m, 7m };

    // ── ครึ่งที่ 1: ต้องถูกจับ ──────────────────────────────────────────────

    [Fact]
    public void ส่วนลดอ่านผิดเป็นยอดหลังหักส่วนลด_เอกสารเกินกระดาษ6975_ต้องฟ้องเป็นตัวเลข()
    {
        // ตัวอ่านเดิมได้ "ส่วนลด" 1,325.25 ⇒ ตัวกระทบยอดตกเคส B (Σ 1,395 = "รวมเงิน") ⇒ ไม่มีช่องว่าง
        // ⇒ เอกสาร 1,395 + VAT 92.77 = 1,487.77 ทั้งที่กระดาษ 1,418.02 — เดิมผ่านเงียบถึงตอนอนุมัติ
        var lines = ExclVat(new[] { 370m, 890m, 135m }, Seven3, 92.77m);
        var r = OcrAmountIntegrity.Check(lines, 92.77m, 1418.02m);
        var total = Assert.Single(r.Problems, p => p.Kind == OcrAmountIntegrityKind.TotalMismatch);
        Assert.Contains("+69.75", total.Message);
        Assert.Contains("ส่วนลด", total.Message);            // บอกสาเหตุที่น่าจะเป็น
        Assert.Equal(1487.77m, r.LinesTotal);
        Assert.False(r.Ok);
    }

    [Fact]
    public void ใบผสมที่ตัวเดาจากชื่อติด7เปอร์เซ็นต์ให้ไข่และหมู_ยอดรวมตรงแต่อัตราผิด_ต้องฟ้อง()
    {
        // Σ VAT ตรงกระดาษ (28.00) เสมอเพราะเฉลี่ยจากหัวใบ ⇒ ด่านเดิมไม่มีทางเห็น · แต่ 7% ของฐาน 714
        // = 49.98 ≠ 28.00 ⇒ เปิดแก้แล้วบันทึกเมื่อไร VAT กลายเป็น 49.98 และยอดเอกสาร ≠ กระดาษ
        var rates = new[] { 7m, 7m, ThaiVatTypeRule.ExemptRate, 7m, 7m, 7m };
        var lines = InclVat(OcrPaperSamples.WholesaleLineAmounts, rates, 28m);
        var r = OcrAmountIntegrity.Check(lines, 28m, 792m, paperTaxable: 400m, paperNonTaxable: 364m);
        Assert.Equal(792m, r.LinesTotal);                                        // ยอดรวม "ดูเหมือนตรง"
        var p = Assert.Single(r.Problems);
        Assert.Equal(OcrAmountIntegrityKind.VatRateMismatch, p.Kind);
        Assert.Contains("49.98", p.Message);     // VAT ที่อัตรา × ยอดให้
        Assert.Contains("28.00", p.Message);     // VAT บนกระดาษ
        Assert.Contains("400.00", p.Message);    // ยอดที่มี VAT ตาม VAT บนกระดาษ
        Assert.Contains("314.00", p.Message);    // ยอดยกเว้นที่ถูกติด 7%
        Assert.Contains("364.00", p.Message);    // ยอดแยกที่กระดาษพิมพ์ไว้
    }

    [Fact]
    public void ใบผสมไม่มีรายการ_บรรทัดสรุป7เปอร์เซ็นต์_ฐานภาษีเกินจริง_ต้องฟ้อง()
    {
        // Makro 951 / 49 / 1,000 — บรรทัดสรุปใบเดียว 7% ⇒ ฐานภาษีซื้อ §87 = 951 ทั้งที่มี VAT แค่ ≈ 700
        var r = OcrAmountIntegrity.Check(new[] { new OcrPlannedLine(951m, 7m, 49m) }, 49m, 1000m);
        var p = Assert.Single(r.Problems);
        Assert.Equal(OcrAmountIntegrityKind.VatRateMismatch, p.Kind);
        Assert.Contains("700.00", p.Message);
    }

    [Fact]
    public void กระดาษมี_VAT_แต่ทุกบรรทัดถูกตั้งเป็นไม่มี_VAT_ต้องฟ้อง()
    {
        var lines = ExclVat(new[] { 600m, 400m }, new[] { ThaiVatTypeRule.ExemptRate, 0m }, 70m);
        var r = OcrAmountIntegrity.Check(lines, 70m, 1070m);
        Assert.Contains(r.Problems, p => p.Kind == OcrAmountIntegrityKind.VatMismatch
                                         && p.Message.Contains("70.00"));
    }

    [Fact]
    public void บรรทัดติดลบ_ต้องฟ้อง_แม้ยอดรวมตรง()
    {
        // engine อ่านแถว "ส่วนลด -50.00" มาเป็นรายการ ⇒ Σ ตรง แต่บรรทัดติดลบขัด §86/4(5)
        var lines = ExclVat(new[] { 1000m, -50m }, new[] { 7m, 7m }, 66.50m);
        var r = OcrAmountIntegrity.Check(lines, 66.50m, 1016.50m);
        Assert.Contains(r.Problems, p => p.Kind == OcrAmountIntegrityKind.NegativeLine && p.Message.Contains("บรรทัดที่ 2"));
    }

    [Fact]
    public void คำเตือนต้องเป็นตัวหยุดอนุมัติอัตโนมัติจริง_ไม่ใช่ข้อความลอย()
    {
        var r = OcrAmountIntegrity.Check(new[] { new OcrPlannedLine(951m, 7m, 49m) }, 49m, 1000m);
        var notes = "[Tier] Azure DI สำเร็จ\n[Σ-GAP] " + r.Problems[0].Message;
        Assert.False(OcrPostingReadiness.Evaluate(notes, hasUsableDate: true).CanAutoApprove);
    }

    // ── ครึ่งที่ 2: ต้องเงียบ ──────────────────────────────────────────────

    [Fact]
    public void ใบ_WinePro_ราคารวม_VAT_มีบรรทัดศูนย์_ต้องเงียบ()
    {
        var lines = InclVat(new[] { 524m, 3069m, 0m }, Seven3, 235.06m);
        var r = OcrAmountIntegrity.Check(lines, 235.06m, 3593m);
        Assert.True(r.Ok, string.Join(" | ", r.Problems.Select(p => p.Message)));
        Assert.Equal(3357.94m, r.LinesNet);                 // = Net.Amt บนกระดาษ
        Assert.Equal(new[] { 489.72m, 2868.22m, 0m }, lines.Select(l => l.NetAmount));
    }

    [Fact]
    public void ใบร้านวัสดุหลังกระจายส่วนลด_ต้องเงียบ()
    {
        // เคส C: 1,325.25 กระจายตามสัดส่วน 370/890/135 ⇒ 351.50 · 845.50 · 128.25
        var lines = ExclVat(new[] { 351.50m, 845.50m, 128.25m }, Seven3, 92.77m);
        var r = OcrAmountIntegrity.Check(lines, 92.77m, 1418.02m);
        Assert.True(r.Ok, string.Join(" | ", r.Problems.Select(p => p.Message)));
        Assert.Equal(1325.25m, r.LinesNet);
    }

    [Fact]
    public void ใบซูเปอร์มาร์เก็ตราคารวม_VAT_หลังกระจายส่วนลดสมาชิก_ต้องเงียบ()
    {
        // เคส E: 735.30 กระจายตามสัดส่วน 159/438/177 ⇒ 151.05 · 416.10 · 168.15
        var lines = InclVat(new[] { 151.05m, 416.10m, 168.15m }, Seven3, 48.10m);
        var r = OcrAmountIntegrity.Check(lines, 48.10m, 735.30m);
        Assert.True(r.Ok, string.Join(" | ", r.Problems.Select(p => p.Message)));
        Assert.Equal(687.20m, r.LinesNet);                  // = มูลค่าสินค้าบนกระดาษ
    }

    [Fact]
    public void ใบผสมที่อัตรามาจากสัญลักษณ์บนกระดาษ_ต้องเงียบ()
    {
        var rates = new[] { ThaiVatTypeRule.ExemptRate, ThaiVatTypeRule.ExemptRate, ThaiVatTypeRule.ExemptRate, 7m, 7m, 7m };
        var lines = InclVat(OcrPaperSamples.WholesaleLineAmounts, rates, 28m);
        var r = OcrAmountIntegrity.Check(lines, 28m, 792m, 400m, 364m);
        Assert.True(r.Ok, string.Join(" | ", r.Problems.Select(p => p.Message)));
        Assert.Equal(764m, r.LinesNet);
    }

    [Fact]
    public void เศษปัด_VAT_รายบรรทัด_ไม่ใช่ความผิด()
    {
        // ผู้ขายคิด VAT รายบรรทัดแล้วรวม: 2.33 × 3 = 6.99 · 7% ของฐานรวม 99.99 = 7.00 (ต่าง 0.01)
        var lines = new[] { new OcrPlannedLine(33.33m, 7m, 2.33m), new OcrPlannedLine(33.33m, 7m, 2.33m), new OcrPlannedLine(33.33m, 7m, 2.33m) };
        Assert.True(OcrAmountIntegrity.Check(lines, 6.99m, 106.98m).Ok);
    }

    [Fact]
    public void ไม่รู้ยอดรวมบนกระดาษ_เทียบยอดรวมไม่ได้_ไม่ใช่ตรง()
    {
        var r = OcrAmountIntegrity.Check(ExclVat(new[] { 1000m }, new[] { 7m }, 70m), 70m, 0m);
        Assert.False(r.TotalComparable);
        Assert.DoesNotContain(r.Problems, p => p.Kind == OcrAmountIntegrityKind.TotalMismatch);
    }
}

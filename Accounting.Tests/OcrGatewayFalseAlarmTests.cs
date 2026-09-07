using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่าน 3 (อัตรา VAT) และด่าน 7 (Σ บรรทัด ↔ หัวใบ) ของ
/// <see cref="OcrConfidenceGateway"/> — ล็อกไว้ว่า **ใบที่ถูกกฎหมายต้องเงียบ**
///
/// <para>ที่มา (ผลตรวจ 2026-09-06 · T2-03/T2-16): ด่าน 3 ฟ้อง "อัตรา VAT
/// ผิดปกติ" กับบิลผสม 7% + ยกเว้น §81 (Makro: สินค้ามี VAT 700 + ยกเว้น 300
/// → VAT 49 → 4.9%) ทุกใบ และด่าน 7 ฟ้อง "ผลรวมรายการ ≠ ยอดก่อน VAT" กับ
/// ทุกใบที่ราคาต่อหน่วยรวม VAT แล้ว (IKEA 1,396 vs sub 1,304.67) ⇒ ใบที่
/// ตัวเลขถูกทุกช่องโดน −0.25 รวม ⇒ ตกเกณฑ์ auto-create 0.85 และผู้ใช้
/// เรียนรู้ที่จะเมินคำเตือน แล้วคำเตือนจริงถูกเมินตามไปด้วย</para>
///
/// <para>เทสต์ชุดนี้ล็อก**ช่องว่างระหว่างสองกลุ่ม** ทั้งสองทิศ: ใบถูกกฎหมาย
/// ต้องอยู่ใน <c>Notes</c> (ไม่หักคะแนน) · ใบที่อ่านผิดจริงต้องอยู่ใน
/// <c>Warnings</c> (หักคะแนน) — ไม่ใช่แค่ "เงียบลง"</para>
/// </summary>
public class OcrGatewayFalseAlarmTests
{
    private static OcrConfidenceGateway.GatewayResult Run(
        decimal? sub, decimal? vat, decimal? total,
        decimal[]? lineAmounts = null, decimal? discount = null)
        => OcrConfidenceGateway.Validate(
            modelConfidence: 0.95m,
            documentDate: new DateTime(2026, 8, 1),
            subTotal: sub, vatAmount: vat, total: total,
            vendorTaxId: null, buyerTaxId: null,
            lineItems: lineAmounts?
                .Select(a => new OcrConfidenceGateway.LineItemForValidation(null, null, a))
                .ToList(),
            documentNumber: "INV-001", vendorName: "ผู้ขายทดสอบ",
            documentDiscount: discount);

    private static bool Warned(OcrConfidenceGateway.GatewayResult r, string marker)
        => r.Warnings.Any(w => w.Contains(marker, StringComparison.Ordinal));

    private static bool Noted(OcrConfidenceGateway.GatewayResult r, string marker)
        => r.Notes.Any(n => n.Contains(marker, StringComparison.Ordinal));

    // ── ด่าน 3: อัตรา VAT ──────────────────────────────────────────────

    [Fact]
    public void บิลผสมเจ็ดเปอร์เซ็นต์กับยกเว้น_เป็นข้อสังเกตไม่ใช่คำเตือน()
    {
        // Makro: สินค้ามี VAT 700 (VAT 49) + สินค้ายกเว้น §81 300 ⇒ อัตรารวม 4.9%
        // แต่ 1,000 + 49 = 1,049 เป๊ะ (ผู้ขายพิมพ์ให้สอดคล้องกัน)
        var r = Run(1000m, 49m, 1049m);
        Assert.False(Warned(r, "อัตรา VAT ผิดปกติ"));
        Assert.True(Noted(r, "ยกเว้น §81"));
        Assert.Equal(0.95m, r.AdjustedConfidence);
    }

    [Fact]
    public void อัตราสูงกว่าเจ็ดเปอร์เซ็นต์_ยังต้องฟ้องเพราะกฎหมายไม่มีอัตรานั้น()
    {
        // VAT 150 บนยอด 1,000 = 15% — เกินอัตราสูงสุดตามกฎหมายไทย
        var r = Run(1000m, 150m, 1150m);
        Assert.True(Warned(r, "อัตรา VAT ผิดปกติ"));
        Assert.True(r.AdjustedConfidence < 0.95m);
    }

    [Fact]
    public void อัตราต่ำและตัวเลขหัวใบไม่ลงตัว_ยังต้องฟ้องเพราะอาจอ่าน_VAT_ขาดหลัก()
    {
        // 1,000 + 7 = 1,007 ≠ 1,070 ⇒ VAT 70 ถูกอ่านเหลือ 7 (ขาดหลัก)
        var r = Run(1000m, 7m, 1070m);
        Assert.True(Warned(r, "อัตรา VAT ผิดปกติ"));
        Assert.False(r.MathConsistent);
    }

    // ── ด่าน 7: Σ บรรทัด ↔ หัวใบ ───────────────────────────────────────

    [Fact]
    public void ราคาต่อหน่วยรวม_VAT_แล้ว_ต้องไม่ถูกฟ้องและไม่เสียคะแนน()
    {
        // IKEA: บรรทัด 1,396 (รวม VAT) · sub 1,304.67 · VAT 91.33 · total 1,396
        var r = Run(1304.67m, 91.33m, 1396m, new[] { 1396m });
        Assert.False(Warned(r, "ผลรวมรายการ"));
        Assert.False(Warned(r, "Σ บรรทัด"));
        Assert.True(r.MathConsistent);
        Assert.Equal(0.95m, r.AdjustedConfidence);
    }

    [Fact]
    public void ราคาแยก_VAT_ตามปกติ_ต้องเงียบ()
    {
        var r = Run(1000m, 70m, 1070m, new[] { 600m, 400m });
        Assert.Empty(r.Warnings);
        Assert.True(r.MathConsistent);
    }

    [Fact]
    public void ส่วนลดที่กระดาษพิมพ์ไว้_เป็นข้อสังเกตไม่ใช่คำเตือน()
    {
        // สินค้า 1,000 · ส่วนลดท้ายบิล 50 · sub 950 · VAT 66.50 · total 1,016.50
        var r = Run(950m, 66.50m, 1016.50m, new[] { 1000m }, discount: 50m);
        Assert.False(Warned(r, "Σ บรรทัด"));
        Assert.True(Noted(r, "ส่วนลดท้ายบิล"));
        Assert.True(r.MathConsistent);
    }

    [Fact]
    public void OCR_อ่านบรรทัดขาด_ยังต้องฟ้อง()
    {
        // กระดาษ sub 1,000 แต่ OCR อ่านได้บรรทัดเดียว 400
        var r = Run(1000m, 70m, 1070m, new[] { 400m });
        Assert.True(Warned(r, "อ่านบรรทัดขาด"));
        Assert.False(r.MathConsistent);
        Assert.True(r.AdjustedConfidence < 0.95m);
    }

    [Fact]
    public void บรรทัดเกินยอดโดยกระดาษไม่บอกส่วนลด_ยังต้องฟ้อง()
    {
        // Σ บรรทัด 1,200 > sub 1,000 และไม่ตรง total 1,070 · กระดาษไม่มีช่องส่วนลด
        var r = Run(1000m, 70m, 1070m, new[] { 1200m });
        Assert.True(Warned(r, "ตรวจสอบราคาต่อหน่วย"));
        Assert.False(r.MathConsistent);
    }
}

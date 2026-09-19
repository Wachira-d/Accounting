using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ด่านคณิตศาสตร์ต้องมีผู้บริโภค** (ผลตรวจ 2026-09-18 · D3-3)
///
/// <para>บั๊ก 2 ตัวที่เสริมกัน: (ก) <c>GatewayResult.MathConsistent</c> ไม่มีผู้อ่านทั้งเรพ
/// ⇒ ใบที่ระบบเองบอกว่า "อย่างน้อยหนึ่งช่องอ่านผิด" ยังอนุมัติอัตโนมัติได้ · (ข)
/// ไปป์ไลน์ยกคะแนนทั้งใบด้วย <c>Math.Max(Confidence, vendorPred.DocumentTypeConfidence)</c>
/// ⇒ penalty ที่ด่านหักไว้ถูกลบล้างด้วย "ความคุ้นเคยกับผู้ขาย" ซึ่งไม่ใช่หลักฐานว่า
/// ตัวเลขบนใบนี้อ่านถูก</para>
///
/// <para>สองครึ่ง: ใบที่ขัดกันเองจริงต้องติดแท็ก <c>[MATH]</c> และถูกบล็อกการอนุมัติเอง ·
/// ใบที่ถูกกฎหมายอยู่แล้ว (Makro ผสมยกเว้น §81 · IKEA ราคารวม VAT · ใบส่งออก 0%)
/// ต้อง <b>ไม่</b> ติดแท็กและอนุมัติอัตโนมัติได้เหมือนเดิม</para>
/// </summary>
public class OcrMathGateBlockingTests
{
    private static OcrConfidenceGateway.GatewayResult Run(
        decimal? sub, decimal? vat, decimal? total, decimal[]? lineAmounts = null,
        decimal? discount = null)
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

    /// <summary>ไปป์ไลน์เขียนแท็กเมื่อ <c>MathConsistent == false</c> — จำลองบรรทัดนั้น
    /// (ตัวเขียนจริงอยู่ใน OcrService หลังเรียก Gateway)</summary>
    private static string NotesFor(OcrConfidenceGateway.GatewayResult r)
        => r.MathConsistent ? "[Tier] Azure DI สำเร็จ" : "[MATH] ตัวเลขบนหัวใบขัดกันเอง";

    // ── ครึ่งที่ 1: ใบที่ขัดกันเองจริง ต้องหยุด ──────────────────────────

    [Fact]
    public void ใบที่สามช่องขัดกันเอง_ติดแท็ก_MATH_และห้ามอนุมัติเอง()
    {
        // ใบจริงที่ผู้ใช้ส่งมา: 922.44 + 68.00 = 990.44 ≠ 990.00 และ VAT 7% ควรเป็น 64.57
        var r = Run(922.44m, 68.00m, 990.00m);
        Assert.False(r.MathConsistent);

        var v = OcrPostingReadiness.Evaluate(NotesFor(r), hasUsableDate: true);
        Assert.False(v.CanAutoApprove);
        Assert.Contains("ขัดกันเอง", v.Reason);
    }

    [Fact]
    public void ยอดห่างเกิน_tolerance_ก็ต้องหยุด()
    {
        var r = Run(1000m, 70m, 1500m);
        Assert.False(r.MathConsistent);
        Assert.False(OcrPostingReadiness.Evaluate(NotesFor(r), true).CanAutoApprove);
    }

    [Fact]
    public void แท็ก_MATH_ต้องอยู่ในรายการห้ามอนุมัติเอง_ไม่ใช่แค่ข้อความลอย()
    {
        Assert.Contains(OcrPostingReadiness.BlockingTags, t => t.Tag == "[MATH]");
        Assert.All(OcrPostingReadiness.BlockingTags, t => Assert.False(string.IsNullOrWhiteSpace(t.Why)));
    }

    // ── ครึ่งที่ 2: ใบที่ถูกอยู่แล้ว ต้องไม่ถูกแตะ ───────────────────────

    [Theory]
    // Makro: สินค้ามี VAT 700 (VAT 49) + ยกเว้น §81 300 ⇒ อัตรารวม 4.9% แต่ยอดลงตัวเป๊ะ
    [InlineData(1000, 49, 1049)]
    // IKEA: ราคาต่อหน่วยรวม VAT แล้ว — หัวใบยังลงตัว
    [InlineData(1304.67, 91.33, 1396)]
    // ใบส่งออก §80/1 อัตราศูนย์ — ไม่มี VAT เลย
    [InlineData(50000, 0, 50000)]
    // ลักกี้เวย์: ยอดเล็ก ปัดเศษ 1 สตางค์ — ต้องไม่ฟ้อง
    [InlineData(93.46, 6.54, 100)]
    public void ใบที่ถูกกฎหมายอยู่แล้ว_ต้องไม่ติดแท็ก_และอนุมัติอัตโนมัติได้(
        double sub, double vat, double total)
    {
        var r = Run((decimal)sub, (decimal)vat, (decimal)total);
        Assert.True(r.MathConsistent);
        Assert.DoesNotContain("[MATH]", NotesFor(r));
        Assert.True(OcrPostingReadiness.Evaluate(NotesFor(r), hasUsableDate: true).CanAutoApprove);
    }

    [Fact]
    public void ใบราคารวม_VAT_แล้ว_ที่เคยถูกฟ้องผิด_ต้องไม่ติดแท็ก()
    {
        // IKEA: บรรทัด 1,396 (รวม VAT) · sub 1,304.67 · VAT 91.33 · total 1,396
        // — ด่าน Σ บรรทัดเคยฟ้องใบทรงนี้ทุกใบ (T2-16) ถ้ากลับมาฟ้อง = ปิดปุ่มอนุมัติ
        // ของใบที่ถูกต้องทั้งกอง
        var r = Run(1304.67m, 91.33m, 1396m, new[] { 1396m });
        Assert.True(r.MathConsistent);
        Assert.True(OcrPostingReadiness.Evaluate(NotesFor(r), true).CanAutoApprove);
    }

    [Fact]
    public void ใบที่กระดาษพิมพ์ส่วนลดไว้จริง_ต้องไม่ติดแท็ก()
    {
        // สินค้า 1,000 · ส่วนลดท้ายบิล 50 · sub 950 · VAT 66.50 · total 1,016.50
        var r = Run(950m, 66.50m, 1016.50m, new[] { 1000m }, discount: 50m);
        Assert.True(r.MathConsistent);
        Assert.True(OcrPostingReadiness.Evaluate(NotesFor(r), true).CanAutoApprove);
    }

    [Fact]
    public void OCR_อ่านบรรทัดขาด_ยังต้องหยุด_ทิศตรงข้ามของสองเคสข้างบน()
    {
        // กระดาษ sub 1,000 แต่ OCR อ่านได้บรรทัดเดียว 400 ⇒ ตัวเลขที่จะลงบัญชีขาดหาย
        var r = Run(1000m, 70m, 1070m, new[] { 400m });
        Assert.False(r.MathConsistent);
        Assert.False(OcrPostingReadiness.Evaluate(NotesFor(r), true).CanAutoApprove);
    }

    [Fact]
    public void อ่านยอดไม่ครบ_ไม่ใช่การขัดกันเอง_ห้ามฟ้อง()
    {
        // ไม่มี SubTotal/VAT บนกระดาษ = "ไม่รู้" ไม่ใช่ "ผิด" — ด่านนี้ต้องเงียบ
        // (ช่องที่หายไปมีด่านอื่นของมันเอง)
        var r = Run(null, null, 1070m);
        Assert.True(r.MathConsistent);
    }
}

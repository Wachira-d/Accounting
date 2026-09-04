using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่าน "ตัวเลขสามช่องขัดกันเอง" ของ <see cref="OcrConfidenceGateway"/>
///
/// <para>ที่มา: ใบจริงที่ผู้ใช้ส่งมา — SubTotal 922.44 · VAT 68.00 · Total 990.00
/// ผ่านทั้งด่านผลรวม (ห่าง ฿0.44 &lt; tolerance ฿2.00) และด่านอัตรา VAT
/// (7.37% ยังอยู่ในกรอบ 6.5–7.5%) แล้วโชว์ confidence 95% ทั้งที่อย่างน้อย
/// สองช่องอ่านผิด</para>
///
/// <para>เทสต์ชุดนี้ล็อก**ช่องว่างระหว่างสองกลุ่ม**: กระดาษที่ขัดกันเอง ต้องฟ้อง ·
/// กระดาษที่สอดคล้องกันเอง (รวมใบหลายอัตรา VAT) ต้องเงียบ</para>
/// </summary>
public class OcrAmountTriangleTests
{
    private const string Marker = "ตัวเลขสามช่องขัดกันเอง";

    private static OcrConfidenceGateway.GatewayResult Run(
        decimal? sub, decimal? vat, decimal? total,
        IReadOnlyList<OcrConfidenceGateway.LineItemForValidation>? lines = null)
        => OcrConfidenceGateway.Validate(
            modelConfidence: 0.95m, documentDate: null,
            subTotal: sub, vatAmount: vat, total: total,
            vendorTaxId: null, buyerTaxId: null, lineItems: lines);

    private static bool Fired(OcrConfidenceGateway.GatewayResult r)
        => r.Warnings.Any(w => w.Contains(Marker, StringComparison.Ordinal));

    [Fact]
    public void ใบจริงที่หลุดทุกด่านมาก่อน_ต้องถูกจับได้()
    {
        var r = Run(922.44m, 68m, 990m);
        Assert.True(Fired(r));
        Assert.False(r.MathConsistent);
        Assert.True(r.AdjustedConfidence < 0.95m);
    }

    [Fact]
    public void ใบที่ตัวเลขสอดคล้องกันเป๊ะ_ต้องเงียบ()
    {
        // 925.23 + 64.77 = 990.00 · VAT = 7% ของ 925.23 = 64.77 พอดี
        Assert.False(Fired(Run(925.23m, 64.77m, 990m)));
    }

    [Fact]
    public void ใบหลายอัตราภาษี_ต้องไม่ถูกฟ้องผิด()
    {
        // 7% เฉพาะ 1,000 · อีก 500 เป็น 0%/ยกเว้น ⇒ VAT 70 = 4.67% ของยอดรวม
        // (ต่างจาก 7% มาก) แต่ผลรวมเป๊ะ ⇒ ด่านต้องไม่ฟ้อง
        var r = Run(1500m, 70m, 1570m);
        Assert.False(Fired(r));
        Assert.True(r.MathConsistent);
    }

    [Fact]
    public void ผลรวมเพี้ยนแต่_VAT_ตรง_7_เปอร์เซ็นต์_ต้องไม่ฟ้องด่านนี้()
    {
        // 1,000 + 70 = 1,070 แต่กระดาษพิมพ์ 1,071 (ยอดรวมอ่านผิดตัวเดียว)
        // VAT ยังเป็น 7% เป๊ะ ⇒ ด่านนี้ไม่ใช่ตัวที่ควรฟ้อง (ปล่อยให้ด่านผลรวมทำงาน)
        Assert.False(Fired(Run(1000m, 70m, 1071m)));
    }

    [Theory]
    // การปัดเศษให้ผลต่างได้ไม่เกิน 1 สตางค์ต่อการปัด — ต้องไม่ฟ้อง
    [InlineData(100.00, 7.00, 107.01)]
    [InlineData(100.00, 7.00, 106.99)]
    public void คลาดเคลื่อนระดับปัดเศษ_ต้องไม่ฟ้อง(double sub, double vat, double total)
        => Assert.False(Fired(Run((decimal)sub, (decimal)vat, (decimal)total)));

    [Fact]
    public void ใบหลายบรรทัด_ผ่อนค่าคลาดเคลื่อนของ_VAT_ตามจำนวนบรรทัด()
    {
        // 10 บรรทัด → ยอมให้ VAT ต่างจาก 7% ได้ถึง ฿0.10
        var lines = Enumerable.Range(0, 10)
            .Select(_ => new OcrConfidenceGateway.LineItemForValidation(1m, 100m, 100m))
            .ToList();
        // 1,000.00 → VAT ควรเป็น 70.00 · กระดาษพิมพ์ 70.08 (สะสมจากปัดรายบรรทัด)
        // และผลรวมเพี้ยน 0.08 ⇒ ต้องไม่ฟ้อง เพราะอยู่ในกรอบการปัดของ 10 บรรทัด
        Assert.False(Fired(Run(1000m, 70.08m, 1070m, lines)));
        // แต่ถ้าเพี้ยนเกินกรอบ (VAT 71.00 = ห่าง ฿1.00) ต้องฟ้อง
        Assert.True(Fired(Run(1000m, 71m, 1070m, lines)));
    }

    [Fact]
    public void ช่องไม่ครบหรือ_VAT_เป็นศูนย์_ต้องไม่ฟ้อง()
    {
        Assert.False(Fired(Run(null, 68m, 990m)));
        Assert.False(Fired(Run(922.44m, null, 990m)));
        Assert.False(Fired(Run(922.44m, 68m, null)));
        // ใบยกเว้น/0% — ไม่มี VAT ให้เทียบ
        Assert.False(Fired(Run(990m, 0m, 990m)));
    }

    [Fact]
    public void ไม่ฟ้องซ้ำกับด่านผลรวมเดิม()
    {
        // ห่างเกิน tolerance ฿2.00 ⇒ ด่านข้อ 2 ฟ้องไปแล้ว ด่านนี้ต้องไม่ซ้อนโทษอีก
        var r = Run(900m, 63m, 990m);
        Assert.False(Fired(r));
        Assert.Contains(r.Warnings, w => w.Contains("คณิตศาสตร์ไม่ตรง", StringComparison.Ordinal));
    }
}

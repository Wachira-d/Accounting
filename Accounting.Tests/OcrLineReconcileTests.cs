using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตรวจ SanitizeVatSplitArtifacts ขั้น "reconcile จำนวน" — กันเคสที่ OCR อ่านยอด
/// บรรทัด (Amount) ถูก แต่อ่านจำนวนผิด (มักอ่านตัวเลขคอลัมน์ยอดมาเป็นจำนวน) →
/// line-building คิด qty×unitPrice → ยอดพุ่งไกล. reconcile เชื่อ Amount เป็นหลัก
/// แก้ qty ให้ qty×price = Amount (ยอดบรรทัดถูกเสมอ).
/// </summary>
public class OcrLineReconcileTests
{
    private static OcrExtractedLineItem Line(decimal? qty, decimal? up, decimal? amt, string desc = "สินค้า A")
        => new() { Description = desc, Quantity = qty, UnitPrice = up, Amount = amt };

    private static OcrExtractedData WithLine(OcrExtractedLineItem l)
    {
        var d = new OcrExtractedData();
        d.Items.Add(l);
        return d;
    }

    [Fact]
    public void Qty_misread_as_amount_reconciles_to_line_total()
    {
        // OCR อ่านยอด 1,180 มาใส่เป็นจำนวน (qty=1180) → qty×price = 1.39M (ระเบิด)
        // Amount ที่อ่านถูก = 1,180 → ต้องแก้ qty=1 → line = 1,180
        var d = WithLine(Line(qty: 1180m, up: 1180m, amt: 1180m));
        OcrService.SanitizeVatSplitArtifacts(d);
        var it = d.Items[0];
        Assert.Equal(1m, it.Quantity);
        Assert.Equal(1180m, (it.UnitPrice ?? 0m) * (it.Quantity ?? 0m));
    }

    [Fact]
    public void Qty_ten_but_amount_hundred_reconciles()
    {
        // qty อ่านผิดเป็น 10, price 100, amount จริง 100 → qty×price=1000≠100 → qty=1
        var d = WithLine(Line(qty: 10m, up: 100m, amt: 100m));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(1m, d.Items[0].Quantity);
        Assert.Equal(100m, (d.Items[0].UnitPrice ?? 0m) * (d.Items[0].Quantity ?? 0m));
    }

    [Fact]
    public void Correct_line_untouched()
    {
        // qty×price = amount เป๊ะ → ไม่แตะ
        var d = WithLine(Line(qty: 3m, up: 100m, amt: 300m));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(3m, d.Items[0].Quantity);
        Assert.Equal(100m, d.Items[0].UnitPrice);
    }

    [Fact]
    public void Within_tolerance_untouched()
    {
        // 3×100 = 300.00 vs Amount 300.02 → ต่าง ฿0.02 ≤ tol (฿1) → ไม่แตะ
        var d = WithLine(Line(qty: 3m, up: 100m, amt: 300.02m));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(3m, d.Items[0].Quantity);
    }

    [Fact]
    public void Missing_price_or_amount_or_qty_skipped_safely()
    {
        var noPrice = WithLine(Line(qty: 5m, up: null, amt: 500m));
        OcrService.SanitizeVatSplitArtifacts(noPrice);
        Assert.Equal(5m, noPrice.Items[0].Quantity);   // ไม่มี price → skip (ไม่ crash)

        var noAmt = WithLine(Line(qty: 5m, up: 100m, amt: null));
        OcrService.SanitizeVatSplitArtifacts(noAmt);
        Assert.Equal(5m, noAmt.Items[0].Quantity);      // ไม่มี amount → skip
    }

    [Fact]
    public void Large_but_consistent_qty_kept()
    {
        // ขายจริง 100 หน่วย × 5 = 500 → qty×price ตรง amount → เก็บ qty=100 ไว้
        var d = WithLine(Line(qty: 100m, up: 5m, amt: 500m));
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(100m, d.Items[0].Quantity);
    }
}

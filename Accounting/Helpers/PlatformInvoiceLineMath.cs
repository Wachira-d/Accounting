namespace Accounting.Helpers;

/// <summary>ฝ่ายค้านรอบ 229 (S9): ยอดของ 1 บรรทัดบนใบแจ้งหนี้ค่าบริการหลายบรรทัด (ใบค่าใช้งาน · ใบต่ออายุที่มีโควตาเสริม) — <b>ตัวเดียว</b>ของ
/// <c>PlatformBillingDocumentIssuer.IssueUsageInvoiceAsync</c> (ถอด VAT ต่อบรรทัด → ราคาต่อหน่วย 4 ตำแหน่ง) และสูตรบรรทัดของเอกสาร
/// (<c>DocumentService.ComputeLineAmounts</c> ราคาไม่รวม VAT: net = round(qty × unit, 2) · VAT = round(net × 7%, 2)) · pure — เทสต์ "ยอดใบ ≈ ยอดที่คิด" ได้</summary>
public static class PlatformInvoiceLineMath
{
    public sealed record LineResult(decimal UnitPrice, decimal Net, decimal Vat, decimal Gross);

    /// <param name="amount">ยอดบรรทัดที่คิดไว้ (ราคาตั้ง — รวม VAT เมื่อ <paramref name="priceIncludesVat"/>)</param>
    public static LineResult Line(decimal amount, decimal quantity, bool isVat, bool priceIncludesVat)
    {
        const MidpointRounding R = MidpointRounding.AwayFromZero;
        var qty = quantity > 0m ? quantity : 1m;
        var baseAmount = isVat && priceIncludesVat ? decimal.Round(amount * 100m / 107m, 2, R) : amount;
        var unit = decimal.Round(baseAmount / qty, 4, R);
        var net = Math.Round(qty * unit, 2, R);
        var vat = isVat ? Math.Round(net * 7m / 100m, 2, R) : 0m;
        return new LineResult(unit, net, vat, net + vat);
    }
}

using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เคสจริงที่ผู้ใช้รายงาน (ใบ IKEA ผ่าน OCR ระบบ taketime):
/// ยอดก่อน VAT 1,304.68 · VAT 91.32 · ยอดสุทธิ 1,396.00
/// แต่ Line.Amount ถูกเก็บเป็นยอด**รวม VAT** ⇒ ตอนอนุมัติได้
/// "เดบิต 1,487.32 ≠ เครดิต 1,396.00" — ส่วนต่าง 91.32 = ยอด VAT พอดี
///
/// เทสต์ชุดนี้ reproduce ตัวเลขที่ผู้ใช้เห็นก่อน แล้วจึงยืนยันว่าซ่อมถูกตัว
/// (กฎเหล็ก #4 G — "แก้บั๊กที่ผู้ใช้รายงาน → reproduce ให้เห็นตัวเลขตรงก่อน")
/// </summary>
public class DocumentLineVatConventionTests
{
    // ยอดรายบรรทัดจริงบนใบ (รวม VAT) + VAT ที่เฉลี่ยลงบรรทัด — รวมแล้วตรงหัวเอกสาร
    private const decimal SubTotal = 1304.68m;
    private const decimal VatAmount = 91.32m;
    private const decimal Total = 1396.00m;

    private static (decimal amount, decimal vat)[] GrossLines() => new[]
    {
        (500.00m, 32.71m),
        (300.00m, 19.63m),
        (400.00m, 26.17m),
        (196.00m, 12.81m),   // บรรทัดสุดท้ายดูดเศษ → Σ vat = 91.32 พอดี
    };

    private static decimal SumAmt((decimal amount, decimal vat)[] l) => l.Sum(x => x.amount);
    private static decimal SumVat((decimal amount, decimal vat)[] l) => l.Sum(x => x.vat);

    [Fact]
    public void ตัวเลขตั้งต้นตรงกับใบจริงที่ผู้ใช้รายงาน()
    {
        var lines = GrossLines();
        Assert.Equal(Total, SumAmt(lines));              // บรรทัดเก็บ gross
        Assert.Equal(VatAmount, SumVat(lines));
        Assert.Equal(Total, SubTotal + VatAmount);
        // JE ที่เกิดจริง: Dr ค่าใช้จ่าย(gross) + Dr ภาษีซื้อ = 1,487.32
        Assert.Equal(1487.32m, SumAmt(lines) + VatAmount);
    }

    [Fact]
    public void ตรวจเจอว่าบรรทัดเก็บยอดรวม_VAT()
    {
        var lines = GrossLines();
        Assert.True(DocumentLineVatConvention.LinesStoredGross(
            pricesIncludeVat: true, SubTotal, VatAmount, SumAmt(lines), SumVat(lines)));
    }

    [Fact]
    public void ซ่อมแล้วผลรวมตรงกับยอดก่อน_VAT_และ_JE_สมดุล()
    {
        var repaired = GrossLines()
            .Select(l => DocumentLineVatConvention.RepairedAmount(l.amount, l.vat)).ToArray();
        Assert.Equal(SubTotal, repaired.Sum());
        // Dr ค่าใช้จ่าย(net) + Dr ภาษีซื้อ = Cr เจ้าหนี้
        Assert.Equal(Total, repaired.Sum() + VatAmount);
    }

    [Fact]
    public void ซ่อมแล้วเรียกซ้ำต้องไม่ซ่อมอีก()
    {
        // idempotent — self-heal ตอนอนุมัติ + backfill ตอน migrate ทำงานทับกันได้
        var lines = GrossLines()
            .Select(l => (amount: DocumentLineVatConvention.RepairedAmount(l.amount, l.vat), l.vat))
            .ToArray();
        Assert.False(DocumentLineVatConvention.LinesStoredGross(
            true, SubTotal, VatAmount, SumAmt(lines), SumVat(lines)));
    }

    [Fact]
    public void ใบราคาแยก_VAT_ปกติต้องไม่ถูกแตะ()
    {
        // PricesIncludeVat = false → ไม่ใช่เคสนี้ ห้ามซ่อม
        Assert.False(DocumentLineVatConvention.LinesStoredGross(
            pricesIncludeVat: false, SubTotal, VatAmount, Total, VatAmount));
    }

    [Fact]
    public void ใบที่บรรทัดเก็บ_net_ถูกต้องอยู่แล้วต้องไม่ถูกแตะ()
    {
        Assert.False(DocumentLineVatConvention.LinesStoredGross(
            true, SubTotal, VatAmount, SubTotal, VatAmount));
    }

    [Fact]
    public void ใบไม่มี_VAT_ต้องไม่ถูกแตะ()
    {
        // VAT = 0 → net กับ gross เท่ากัน แยกไม่ออก ห้ามเดา
        Assert.False(DocumentLineVatConvention.LinesStoredGross(true, 1000m, 0m, 1000m, 0m));
    }

    [Fact]
    public void ผลรวม_VAT_รายบรรทัดไม่ตรงหัวเอกสาร_ต้องไม่ซ่อม()
    {
        // หัก VAT รายบรรทัดแล้วจะไม่ลงตัวกับ SubTotal → ปล่อยให้คนตรวจเอง
        Assert.False(DocumentLineVatConvention.LinesStoredGross(
            true, SubTotal, VatAmount, Total, 50.00m));
    }

    [Fact]
    public void ยอดไม่เข้าลายเซ็น_gross_ต้องไม่ซ่อม()
    {
        // Σ Amount ไม่เท่า SubTotal + VAT (เช่นใบที่ผิดเรื่องอื่น) → ไม่ใช่เคสนี้
        Assert.False(DocumentLineVatConvention.LinesStoredGross(
            true, SubTotal, VatAmount, 1500.00m, VatAmount));
    }

    [Fact]
    public void ข้อความ_error_บอกสาเหตุเมื่อส่วนต่างเท่ายอด_VAT()
    {
        var msg = DocumentLineVatConvention.ExplainImbalance(1487.32m, 1396.00m, VatAmount);
        Assert.NotNull(msg);
        Assert.Contains("1,487.32", msg);
        Assert.Contains("1,396.00", msg);
        Assert.Contains("แก้ไข", msg);          // ต้องบอกทางแก้ ไม่ใช่แค่ตัวเลข
    }

    [Fact]
    public void ส่วนต่างไม่เท่ายอด_VAT_ต้องใช้ข้อความเดิม()
    {
        // imbalance จากสาเหตุอื่น — ห้ามเดาว่าเป็นเคส VAT
        Assert.Null(DocumentLineVatConvention.ExplainImbalance(1500.00m, 1396.00m, VatAmount));
        Assert.Null(DocumentLineVatConvention.ExplainImbalance(1487.32m, 1396.00m, 0m));
    }
}

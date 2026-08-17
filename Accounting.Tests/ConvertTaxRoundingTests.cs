using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ยอดภาษีของ "ใบที่แปลงมา" ต้องตรงกับใบต้นทางทุกสตางค์
///
/// ที่มา (ผู้ใช้): แปลงใบแจ้งหนี้เป็นใบกำกับ แล้ว VAT เปลี่ยนจาก 44,942.29 →
/// 44,942.28 (ยอดสุทธิ 667,713.95 → 667,713.94). ลูกค้าถือใบแจ้งหนี้อยู่แล้ว
/// ใบกำกับที่ยอดไม่ตรง = เอกสารสองใบของรายการเดียวกันขัดกันเอง
///
/// สาเหตุ: ใบต้นทางเก็บ VAT ที่ปัด **รายบรรทัด** (Σ = .29) แต่การแปลงคิดใหม่
/// จากอัตราแล้วกระทบยอด **รายกลุ่มอัตรา** (round(Σฐาน × 7%) = .28)
/// </summary>
public class ConvertTaxRoundingTests
{
    private const MidpointRounding R = MidpointRounding.AwayFromZero;
    private static decimal R2(decimal v) => Math.Round(v, 2, R);

    private static decimal PerLineVat(IEnumerable<decimal> nets, decimal rate)
        => nets.Sum(n => R2(n * rate / 100m));

    private static decimal GroupVat(IEnumerable<decimal> nets, decimal rate)
        => R2(nets.Sum() * rate / 100m);

    [Fact]
    public void Per_line_and_group_rounding_can_differ_by_one_satang()
    {
        // ตัวอย่างขั้นต่ำที่สร้างส่วนต่างเดียวกับที่ผู้ใช้เจอ
        // 1.07 × 7% = 0.0749 → 0.07 (สามบรรทัด = 0.21)
        // (1.07 × 3) × 7% = 3.21 × 7% = 0.2247 → 0.22
        var nets = new[] { 1.07m, 1.07m, 1.07m };
        Assert.Equal(0.21m, PerLineVat(nets, 7m));
        Assert.Equal(0.22m, GroupVat(nets, 7m));
        Assert.Equal(0.01m, GroupVat(nets, 7m) - PerLineVat(nets, 7m));
    }

    /// <summary>mirror ของ ConvertCoreAsync — ยกทั้งบรรทัด = ใช้ VAT ที่บันทึกไว้</summary>
    private static decimal? CarriedVat(
        decimal storedVat, decimal lineQty, decimal carryQty, decimal vatRate,
        bool pricesIncludeVat, bool hasBillDiscount)
    {
        var carryStoredVat = !(pricesIncludeVat && hasBillDiscount);
        return carryStoredVat && carryQty >= lineQty && vatRate > 0 ? storedVat : null;
    }

    [Fact]
    public void Full_convert_carries_the_stored_vat_so_the_child_matches()
    {
        var stored = new[] { 0.07m, 0.07m, 0.07m };      // ที่บันทึกไว้จริงในใบต้นทาง
        var carried = stored.Select((v, i) =>
            CarriedVat(v, 1m, 1m, 7m, pricesIncludeVat: false, hasBillDiscount: false)).ToList();

        Assert.All(carried, v => Assert.NotNull(v));
        Assert.Equal(stored.Sum(), carried.Sum(v => v!.Value));   // ใบลูก = ใบแม่เป๊ะ
        Assert.NotEqual(GroupVat(new[] { 1.07m, 1.07m, 1.07m }, 7m), carried.Sum(v => v!.Value));
    }

    [Fact]
    public void Partial_convert_recomputes_instead_of_prorating()
    {
        // เฉลี่ย VAT ตามสัดส่วนจะสร้างเศษของตัวเอง และผลรวมของใบย่อยหลายใบ
        // ก็ไม่ตรงต้นทางอยู่ดี → ปล่อยคิดใหม่ + กระทบยอดตามปกติ
        Assert.Null(CarriedVat(70m, lineQty: 10m, carryQty: 4m, 7m, false, false));
        Assert.NotNull(CarriedVat(70m, lineQty: 10m, carryQty: 10m, 7m, false, false));
    }

    [Fact]
    public void Zero_rate_lines_are_not_carried()
    {
        // ไม่มี VAT ให้รักษา — และการส่ง override 0 จะทำให้ ReconcileTaxRounding
        // ข้ามบรรทัดโดยไม่จำเป็น
        Assert.Null(CarriedVat(0m, 1m, 1m, vatRate: 0m, false, false));
        Assert.Null(CarriedVat(0m, 1m, 1m, vatRate: -1m, false, false));   // ยกเว้น
    }

    [Fact]
    public void Vat_inclusive_pricing_with_a_bill_discount_keeps_the_old_recompute()
    {
        // โหมดราคารวม VAT + ส่วนลดท้ายบิล: การถอด VAT ออกจากราคาทำให้ net ของ
        // ใบลูกไม่ตรงต้นทางอยู่ดี — carry override จะได้ VAT ตรงแต่ net เพี้ยน
        Assert.Null(CarriedVat(70m, 1m, 1m, 7m, pricesIncludeVat: true, hasBillDiscount: true));
        // อีกสามชุดยังยกได้ตามปกติ
        Assert.NotNull(CarriedVat(70m, 1m, 1m, 7m, pricesIncludeVat: true, hasBillDiscount: false));
        Assert.NotNull(CarriedVat(70m, 1m, 1m, 7m, pricesIncludeVat: false, hasBillDiscount: true));
        Assert.NotNull(CarriedVat(70m, 1m, 1m, 7m, pricesIncludeVat: false, hasBillDiscount: false));
    }

    [Fact]
    public void Carrying_vat_reproduces_the_users_numbers()
    {
        // ฐานรวมจากหน้าจอผู้ใช้
        const decimal subTotal = 642_032.64m;
        const decimal storedVat = 44_942.29m;      // ที่บันทึกไว้ในใบแจ้งหนี้
        const decimal wht = 19_260.98m;

        var recomputed = R2(subTotal * 7m / 100m);
        Assert.Equal(44_942.28m, recomputed);       // สิ่งที่การแปลงเดิมคิดได้
        Assert.Equal(0.01m, storedVat - recomputed);

        // หลังแก้: ใบกำกับใช้ยอดที่บันทึกไว้ → ยอดสุทธิเท่าใบแจ้งหนี้เป๊ะ
        Assert.Equal(667_713.95m, subTotal + storedVat - wht);
        Assert.Equal(667_713.94m, subTotal + recomputed - wht);   // ยอดที่ผู้ใช้เห็นว่าเพี้ยน
    }

    /// <summary>mirror ของฟอร์ม: VAT ที่บันทึกไว้ยังใช้ได้ตราบใดที่ผู้ใช้ยังไม่
    /// แตะ "ฐาน" ของบรรทัด (จำนวน/ราคา/ส่วนลด/อัตรา/โหมดราคารวม VAT)</summary>
    private static string BasisKey(decimal qty, decimal price, decimal disc,
        string discMode, decimal vatRate, bool inclVat)
        => string.Join("|", qty, price, disc, discMode, vatRate, inclVat ? "1" : "0");

    [Fact]
    public void Editing_form_keeps_stored_vat_until_the_basis_changes()
    {
        var saved = BasisKey(1m, 67_048.14m, 0m, "pct", 7m, false);

        // เปิดฟอร์มแล้วกดบันทึกเฉย ๆ — ฐานเดิม ⇒ ใช้ยอดที่บันทึกไว้ ยอดไม่ขยับ
        Assert.Equal(saved, BasisKey(1m, 67_048.14m, 0m, "pct", 7m, false));

        // แก้ราคา / จำนวน / ส่วนลด / อัตรา / โหมดราคารวม VAT ⇒ คิดใหม่ทุกกรณี
        Assert.NotEqual(saved, BasisKey(1m, 67_048.15m, 0m, "pct", 7m, false));
        Assert.NotEqual(saved, BasisKey(2m, 67_048.14m, 0m, "pct", 7m, false));
        Assert.NotEqual(saved, BasisKey(1m, 67_048.14m, 5m, "pct", 7m, false));
        Assert.NotEqual(saved, BasisKey(1m, 67_048.14m, 0m, "amt", 7m, false));
        Assert.NotEqual(saved, BasisKey(1m, 67_048.14m, 0m, "pct", 0m, false));
        Assert.NotEqual(saved, BasisKey(1m, 67_048.14m, 0m, "pct", 7m, true));
    }

    [Fact]
    public void Line_vat_on_the_users_biggest_line_matches_the_screen()
    {
        // บรรทัด 10: 1 × 67,048.14 @ 7% → 4,693.37 (ตรงกับที่หน้าจอแสดง)
        Assert.Equal(4_693.37m, R2(67_048.14m * 7m / 100m));
        // WHT 3% ของฐานรวม
        Assert.Equal(19_260.98m, R2(642_032.64m * 3m / 100m));
    }
}

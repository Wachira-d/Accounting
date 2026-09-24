using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 193 ฝ่ายค้านรอบสาม R3-1 — ส่วนลดท้ายบิลแบบบาทที่ผู้ใช้กรอกเอง + หักมัดจำ "ออกใบกำกับแล้ว" ในใบเดียว.
/// เดิมตัวแปลงรวมสองอย่างไว้ใน <c>BillDiscountAmount</c> ช่องเดียว แล้วตอนอนุมัติรับรู้ทั้งก้อนเป็นมัดจำ ⇒
/// (ก) มัดจำใช้เต็ม: อนุมัติไม่ได้ "ขาด 100.00" · (ข) มัดจำใช้บางส่วน: รับรู้ 2,100 แทน 2,000 เงียบ ๆ (ลูกค้าเสียมัดจำ 107 ·
/// รายได้เกิน 100 · VAT เกิน 7 · กระดาษพิมพ์หักมัดจำ 2,100 ส่วนลดหาย).
/// เทสต์เดินผ่าน <see cref="DocumentService.PreviewTotals"/> (ตัวคำนวณเดียวกับสร้าง/แก้เอกสาร) แล้วป้อนฐานมัดจำที่ได้
/// เข้าตัวกระจายการรับรู้ (<see cref="DepositPolicyResolver.AllocateBaseDeduction"/>) ตัวเดียวกับตอนอนุมัติ</summary>
public class TaxedDepositDeductionTests
{
    // ขายเงินสด 7,450 รวม VAT ⇒ ฐาน 6,962.62
    private static List<DocumentLineRequest> Sale(decimal gross) => new()
    {
        new(Description: "สินค้า", Quantity: 1, Unit: "รายการ", UnitPrice: gross, DiscountPercent: 0, VatRate: 7m,
            WithholdingTaxRate: 0, AccountId: null),
    };

    [Fact]
    public void ส่วนลด100_มัดจำ2000ใช้เต็ม_ใบถูก_และรับรู้มัดจำเท่าฐานมัดจำเท่านั้น()
    {
        var depositId = Guid.NewGuid();
        // มัดจำ 2,000 (ฐาน 1,869.16) ใช้เต็ม ⇒ ตัวแปลงได้ฐานคงเหลือทั้งก้อน
        var t = DocumentService.PreviewTotals(Sale(7450m), true, 100m, 1869.16m);
        Assert.Equal(100m, t.TradeDiscount);          // ส่วนลดการค้าคงอยู่ในช่องของตัวเอง
        Assert.Equal(1869.16m, t.DepositBase);        // ฐานมัดจำอยู่ช่องแยก
        Assert.Equal(4993.46m, t.Net);                // 6,962.62 − 100 − 1,869.16
        Assert.Equal(349.54m, t.Vat);
        Assert.Equal(5343.00m, t.Total);

        // ตอนอนุมัติ: รับรู้ = ฐานมัดจำ (เดิม 1,969.16 ⇒ ขาด 100.00 ⇒ การขายที่ถูกต้องอนุมัติไม่ได้)
        var (lines, shortfall) = DepositPolicyResolver.AllocateBaseDeduction(t.DepositBase, new[] { (depositId, 1869.16m) });
        Assert.Equal(0m, shortfall);
        Assert.Equal(1869.16m, Assert.Single(lines).Base);
        // รายได้รวม = ฐานของยอดขาย − ส่วนลดการค้า (ไม่ใช่ราคาเต็ม)
        Assert.Equal(6862.62m, t.Net + lines.Sum(l => l.Base));
    }

    [Fact]
    public void ส่วนลด100_มัดจำ10700ใช้บางส่วน2140_รับรู้2000ไม่ใช่2100()
    {
        var depositId = Guid.NewGuid();
        // มัดจำ 10,700 (ฐาน 10,000 · VAT 700) ใช้ 2,140 ⇒ ฐาน 2,000 (ตัวแปลงตัวเดียวกับที่ ConvertTaxedDrivesAsync ใช้)
        var depBase = DepositPolicyResolver.TaxedDepositBase(2140m, 10000m, 10700m, 10000m);
        Assert.Equal(2000m, depBase);
        var t = DocumentService.PreviewTotals(Sale(7450m), true, 100m, depBase);
        Assert.Equal(4862.62m, t.Net);
        Assert.Equal(340.38m, t.Vat);
        Assert.Equal(5203.00m, t.Total);
        Assert.Equal(100m, t.TradeDiscount);
        Assert.Equal(2000m, t.DepositBase);

        var (lines, shortfall) = DepositPolicyResolver.AllocateBaseDeduction(t.DepositBase, new[] { (depositId, 10000m) });
        Assert.Equal(0m, shortfall);
        Assert.Equal(2000m, Assert.Single(lines).Base);   // เดิม 2,100 ⇒ มัดจำคงเหลือ 7,900 แทน 8,000
        // รายได้รวม 6,862.62 (เดิม 6,962.62 = เกิน 100) · VAT สุทธิเมื่อคืนมัดจำที่เหลือ = 140 + 340.38 = 480.38 (เดิม 487.38)
        Assert.Equal(6862.62m, t.Net + lines.Sum(l => l.Base));
        Assert.Equal(480.38m, 140m + t.Vat);
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่มีส่วนลด_ตัวเลขเดิม1869_16ไม่ถูกแตะ()
    {
        var t = DocumentService.PreviewTotals(Sale(7450m), true, 0m, 1869.16m);
        Assert.Equal(0m, t.TradeDiscount);
        Assert.Equal(1869.16m, t.DepositBase);
        Assert.Equal(5093.46m, t.Net);
        Assert.Equal(356.54m, t.Vat);
        Assert.Equal(5450.00m, t.Total);
        // เท่ากับเส้นเดิม (ก่อนแยกช่อง) ที่ส่งฐานมัดจำเป็นส่วนหักท้ายบิลก้อนเดียว
        var legacy = DocumentService.PreviewTotals(Sale(7450m), true, 1869.16m);
        Assert.Equal((legacy.Net, legacy.Vat, legacy.Total), (t.Net, t.Vat, t.Total));
    }

    [Fact]
    public void ทิศตรงข้าม_ส่วนลดอย่างเดียว_ไม่มีฐานมัดจำให้รับรู้()
    {
        var t = DocumentService.PreviewTotals(Sale(7450m), true, 100m);
        Assert.Equal(100m, t.TradeDiscount);
        Assert.Equal(0m, t.DepositBase);
        Assert.Equal(6862.62m, t.Net);
        Assert.False(DepositPolicyResolver.TaxedDepositDeducted(t.DepositBase, "TIV-0001"));
    }

    [Fact]
    public void ส่วนลดบวกมัดจำเกินยอดขาย_ล้มดัง_ไม่ตัดส่วนใดทิ้งเงียบ()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => DocumentService.PreviewTotals(Sale(1070m), true, 100m, 950m));
        Assert.Contains("เกินยอดขาย", ex.Message);
    }

    [Fact]
    public void ส่วนลดแบบเปอร์เซ็นต์กับฐานมัดจำ_ล้มดังพร้อมทางไปต่อ()
    {
        var ex = Assert.Throws<BusinessRuleException>(() =>
            DocumentService.AllocateBillDeductions(Sale(7450m), true, 5m, 0m, 1869.16m));
        Assert.Equal(DepositPolicyResolver.PercentWithTaxedDepositMessage, ex.Message);
        // ทิศตรงข้าม: เปอร์เซ็นต์อย่างเดียว (ไม่มีมัดจำ) ใช้ได้ตามเดิม
        var (alloc, trade, dep) = DocumentService.AllocateBillDeductions(Sale(7450m), true, 10m, 0m, 0m);
        Assert.Equal(696.26m, trade);
        Assert.Equal(0m, dep);
        Assert.Equal(trade, alloc.Sum());
    }
}

using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ยอดขายที่นับให้ช่างรายบรรทัด (ฐานคอมมิชชันแผน PosStaffSales) — ต้องเป็น "เงินที่ร้านได้จริงก่อน VAT"
/// ไม่ใช่ราคาป้าย · ต้องใช้สัดส่วนส่วนลดตัวเดียวกับการคืนเงิน (PosRefundMath) · ไม่นับค่าบริการ/ทิป · หักที่คืนแล้ว
/// </summary>
public class PosStaffAttributionTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    /// <summary>บิลที่เดินสูตรจริงของระบบ (PosService.RecalculateOrder) — ไม่แต่งตัวเลขเอง</summary>
    private static PosStaffOrder Bill(decimal discountPercent, decimal servicePct, params (Guid? Staff, decimal Gross, decimal Qty, decimal Refunded)[] lines)
    {
        var o = new PosOrder { DiscountPercent = discountPercent, ServiceChargePercent = servicePct };
        foreach (var l in lines)
            o.Items.Add(new PosOrderItem { ItemName = "x", Quantity = l.Qty, UnitPrice = l.Gross / l.Qty, SubTotal = l.Gross, TotalAmount = l.Gross });
        PosService.RecalculateOrder(o, 7m);
        return new PosStaffOrder(o.Items.Sum(i => i.TotalAmount), o.DiscountAmount, o.TotalAmount, o.VatAmount,
            lines.Select(l => new PosStaffLine(l.Staff, l.Gross, l.Qty, l.Refunded)).ToList());
    }

    [Fact]
    public void บิลไม่มีส่วนลด_ยอดช่าง_คือยอดบรรทัดก่อนVAT()
    {
        var bill = Bill(0m, 0m, (A, 1070m, 1m, 0m));
        Assert.Equal(1000m, PosStaffAttribution.NetSalesFor(A, new[] { bill }));
    }

    [Fact]
    public void ส่วนลดท้ายบิลเฉลี่ยตามสัดส่วน_และสองช่างรวมกันเท่ารายได้ก่อนVATของบิล()
    {
        // 642 + 428 = 1070 · ลด 10% ⇒ จ่าย 963 · ก่อน VAT 900
        var bill = Bill(10m, 0m, (A, 642m, 1m, 0m), (B, 428m, 1m, 0m));
        var shares = PosStaffAttribution.ByStaff(new[] { bill });
        Assert.Equal(540m, shares.Single(s => s.StaffId == A).NetSales);
        Assert.Equal(360m, shares.Single(s => s.StaffId == B).NetSales);
        Assert.Equal(bill.TotalAmount - bill.VatAmount, shares.Sum(s => s.NetSales));
    }

    [Fact]
    public void ค่าบริการไม่นับเป็นยอดของช่าง()
    {
        // บิล 1070 + ค่าบริการ 10% = 1177 · ช่างได้ฐานเท่าบิลที่ไม่มีค่าบริการ
        var withSc = Bill(0m, 10m, (A, 1070m, 1m, 0m));
        var noSc = Bill(0m, 0m, (A, 1070m, 1m, 0m));
        Assert.Equal(PosStaffAttribution.NetSalesFor(A, new[] { noSc }), PosStaffAttribution.NetSalesFor(A, new[] { withSc }));
    }

    [Fact]
    public void คืนเงินบางส่วน_หักออกจากยอดช่างตามจำนวน()
    {
        var bill = Bill(0m, 0m, (A, 1070m, 2m, 1m));
        Assert.Equal(500m, PosStaffAttribution.NetSalesFor(A, new[] { bill }));
        var allBack = Bill(0m, 0m, (A, 1070m, 2m, 2m));
        Assert.Equal(0m, PosStaffAttribution.NetSalesFor(A, new[] { allBack }));
    }

    [Fact]
    public void บรรทัดไม่ระบุช่าง_ไม่ถูกนับให้ใคร_และบรรทัดติดลบนับ0()
    {
        var bill = Bill(0m, 0m, (null, 535m, 1m, 0m), (A, 535m, 1m, 0m));
        var shares = PosStaffAttribution.ByStaff(new[] { bill });
        Assert.Single(shares);
        Assert.Equal(500m, shares[0].NetSales);
        Assert.Equal(0m, PosStaffAttribution.LineNetSales(bill, new PosStaffLine(A, -50m, 1m, 0m)));
    }

    [Fact]
    public void บริษัทไม่จดVAT_ยอดช่างเต็มบรรทัด()
    {
        var o = new PosStaffOrder(1000m, 0m, 1000m, 0m, new[] { new PosStaffLine(A, 1000m, 1m, 0m) });
        Assert.Equal(1000m, PosStaffAttribution.NetSalesFor(A, new[] { o }));
    }

    [Fact]
    public void ผลเรียงคงที่_ไม่ขึ้นกับลำดับบิล()
    {
        var b1 = Bill(0m, 0m, (A, 107m, 1m, 0m));
        var b2 = Bill(0m, 0m, (B, 214m, 1m, 0m));
        var x = PosStaffAttribution.ByStaff(new[] { b1, b2 });
        var y = PosStaffAttribution.ByStaff(new[] { b2, b1 });
        Assert.Equal(x.Select(s => s.StaffId), y.Select(s => s.StaffId));
        Assert.Equal(B, x[0].StaffId);
    }
}

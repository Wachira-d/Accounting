using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีมตรวจงานค้าง 2026-10-08 (C-03/C-09 · E-05): ใบแจ้งหนี้ซื้อที่ผูก PO ตรง ทั้งที่บรรทัดนั้นรับผ่าน GRN แล้ว ⇒ สต็อกเข้าสองรอบ + 21240 ค้าง
/// · PO ผสม (สินค้าผ่าน GRN + บริการตั้งหนี้ตรง) ต้องทำได้
/// </summary>
public class PurchaseReceiptRouteTests
{
    private static readonly Guid Goods = Guid.NewGuid(), Service = Guid.NewGuid();

    [Fact]
    public void บิลบรรทัดสินค้าที่GRNรับแล้ว_จากPOตรง_ถูกกัน()
        => Assert.True(PurchaseReceiptRoute.PoBillBlockedByGrn(true, new[] { Goods }, new[] { Goods }, grnWithoutLineLink: false));

    [Fact]
    public void ใบแจ้งหนี้ไม่ระบุบรรทัดPO_ถือว่าทั้งใบ_ถูกกันเมื่อมีGRN()
        => Assert.True(PurchaseReceiptRoute.PoBillBlockedByGrn(true, Array.Empty<Guid>(), new[] { Goods }, false));

    [Fact]
    public void GRNที่ไม่ได้ยกบรรทัด_ถือว่ารับทั้งใบ()
        => Assert.True(PurchaseReceiptRoute.PoBillBlockedByGrn(true, new[] { Service }, Array.Empty<Guid>(), grnWithoutLineLink: true));

    [Fact]
    public void ทิศตรงข้าม_POผสม_บิลเฉพาะบรรทัดบริการที่GRNไม่ได้รับ_ผ่าน()
        => Assert.False(PurchaseReceiptRoute.PoBillBlockedByGrn(true, new[] { Service }, new[] { Goods }, false));

    [Fact]
    public void ทิศตรงข้าม_ไม่มีGRNที่ยังมีผล_บิลจากPOได้()
        => Assert.False(PurchaseReceiptRoute.PoBillBlockedByGrn(false, Array.Empty<Guid>(), Array.Empty<Guid>(), false));
}

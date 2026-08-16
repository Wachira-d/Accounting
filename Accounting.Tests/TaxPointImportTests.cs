using Accounting.Models.Entities;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// §78/2 การนำเข้า — tax point = วันชำระอากรขาเข้า (ไม่ใช่ MIN ของหลายวัน
/// เหมือนสินค้า/บริการ). เดิม resolver ไม่รองรับเลย (comment ในโค้ดเขียนว่า
/// "handled แยก" แต่ไม่มีที่ไหน handle) ⇒ VAT นำเข้าตกไปใช้ issueDate =
/// เข้า ภ.พ.30 ผิดงวดได้
/// </summary>
public class TaxPointImportTests
{
    private static Document Doc(
        DateTime docDate, DateTime? customs = null, DateTime? payment = null,
        DateTime? delivery = null, DateTime? serviceUsed = null)
        => new()
        {
            DocumentDate = docDate,
            CustomsDutyPaidDate = customs,
            PaymentDate = payment,
            DeliveryDate = delivery,
            ServiceUsedDate = serviceUsed,
        };

    [Fact]
    public void นำเข้า_ใช้วันชำระอากรขาเข้าตรงๆ_ไม่ใช่_MIN()
    {
        // วันจ่ายเงินมาก่อน แต่ §78/2 ยึดวันชำระอากร ไม่ใช่วันที่เร็วที่สุด
        var doc = Doc(new DateTime(2026, 3, 31),
            customs: new DateTime(2026, 4, 5),
            payment: new DateTime(2026, 3, 1));
        Assert.Equal(new DateTime(2026, 4, 5), TaxPointResolver.Resolve(doc));
    }

    [Fact]
    public void มี_CustomsDutyPaidDate_ถือเป็นนำเข้าอัตโนมัติ()
    {
        var doc = Doc(new DateTime(2026, 1, 10), customs: new DateTime(2026, 2, 20));
        Assert.Equal(new DateTime(2026, 2, 20), TaxPointResolver.Resolve(doc));
    }

    [Fact]
    public void ระบุ_kind_เป็นนำเข้าแต่ไม่มีวันอากร_ตกกลับ_issueDate()
    {
        var doc = Doc(new DateTime(2026, 5, 9));
        Assert.Equal(new DateTime(2026, 5, 9),
            TaxPointResolver.Resolve(doc, TaxPointResolver.SupplyKind.Import));
    }

    [Fact]
    public void ระบุ_kind_ชัด_ต้องชนะการเดา()
    {
        // เอกสารขายสินค้าที่ผู้ใช้เผลอกรอก ServiceUsedDate ไว้ — เดาแล้วจะไป
        // ทางบริการและข้าม DeliveryDate; ระบุ Goods ต้องได้ DeliveryDate
        var doc = Doc(new DateTime(2026, 6, 30),
            delivery: new DateTime(2026, 6, 2),
            serviceUsed: new DateTime(2026, 6, 20));

        Assert.Equal(new DateTime(2026, 6, 20), TaxPointResolver.Resolve(doc));   // Auto → บริการ
        Assert.Equal(new DateTime(2026, 6, 2),
            TaxPointResolver.Resolve(doc, TaxPointResolver.SupplyKind.Goods));    // ระบุชัด → สินค้า
    }

    [Fact]
    public void ไม่กระทบพฤติกรรมเดิมเมื่อไม่มีข้อมูลนำเข้า()
    {
        var doc = Doc(new DateTime(2026, 7, 31),
            payment: new DateTime(2026, 7, 5), delivery: new DateTime(2026, 7, 10));
        Assert.Equal(new DateTime(2026, 7, 5), TaxPointResolver.Resolve(doc));
    }
}

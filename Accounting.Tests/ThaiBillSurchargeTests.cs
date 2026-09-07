using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวอ่าน "ค่าบริการ / ปัดเศษ" จากบิล (<see cref="ThaiBillSurcharge"/>)
///
/// <para>ที่มา (ผลตรวจ OCR 2026-09-06 · T2-18): บิลร้านอาหาร/โรงแรมเป็นงาน
/// ประจำวันของ SME — อาหาร 1,000 · SC 10% = 100 · ยอดก่อน VAT 1,100 —
/// แต่ทั้งเรพไม่มีจุดไหนอ่านค่าบริการเลย ⇒ Σ บรรทัด 1,000 ไม่ตรงหัวใบ 1,100
/// ทุกใบ ⇒ เอกสารที่สร้างขัดกันเองในใบเดียว</para>
///
/// <para>เทสต์ชุดนี้ล็อกทั้งสองทิศ: อ่านได้เมื่อกระดาษบอก · <b>คืน null เมื่อ
/// กระดาษไม่บอก</b> (ห้ามคำนวณให้เองจากส่วนต่าง)</para>
/// </summary>
public class ThaiBillSurchargeTests
{
    [Fact]
    public void บิลร้านอาหาร_อ่านได้ทั้งค่าบริการอัตราและปัดเศษ()
    {
        var r = ThaiBillSurcharge.Read(
            "อาหาร                     1,000.00\n" +
            "Service Charge 10%          100.00\n" +
            "ยอดก่อนภาษี               1,100.00\n" +
            "ภาษีมูลค่าเพิ่ม 7%            77.00\n" +
            "รวมทั้งสิ้น               1,177.00\n" +
            "ปัดเศษ                       -0.25");
        Assert.Equal(100m, r.ServiceChargeAmount);
        Assert.Equal(10m, r.ServiceChargePercent);
        Assert.Equal(-0.25m, r.RoundingAdjustment);
    }

    [Fact]
    public void ป้ายภาษาไทยมีช่องว่างก่อนเปอร์เซ็นต์_ยังอ่านได้()
    {
        var r = ThaiBillSurcharge.Read(
            "ค่าห้องพัก 2 คืน          4,000.00\nค่าบริการ 10 %              400.00");
        Assert.Equal(400m, r.ServiceChargeAmount);
        Assert.Equal(10m, r.ServiceChargePercent);
    }

    [Fact]
    public void กระดาษบอกยอดแต่ไม่บอกอัตรา_อัตราต้องเป็น_null_ห้ามคำนวณให้เอง()
    {
        var r = ThaiBillSurcharge.Read("SERVICE CHARGE              250.00");
        Assert.Equal(250m, r.ServiceChargeAmount);
        Assert.Null(r.ServiceChargePercent);
    }

    [Fact]
    public void เลขของเปอร์เซ็นต์ต้องไม่ถูกหยิบมาเป็นยอด()
    {
        // ถ้าตัวจับยอดไม่กัน "10" ของ "10%" ไว้ จะได้ยอด 10 บาทแทน 100 บาท
        var r = ThaiBillSurcharge.Read("Service Charge 10%    100.00");
        Assert.Equal(100m, r.ServiceChargeAmount);
    }

    [Fact]
    public void บิลไม่มีค่าบริการ_ต้องคืน_null_ทุกช่อง()
    {
        var r = ThaiBillSurcharge.Read("สินค้า ก   500.00\nรวม   535.00");
        Assert.Null(r.ServiceChargeAmount);
        Assert.Null(r.ServiceChargePercent);
        Assert.Null(r.RoundingAdjustment);
    }

    [Fact]
    public void ข้อความว่างหรือ_null_ต้องไม่ระเบิด()
    {
        Assert.Null(ThaiBillSurcharge.Read(null).ServiceChargeAmount);
        Assert.Null(ThaiBillSurcharge.Read("   ").RoundingAdjustment);
    }

    [Fact]
    public void ปัดเศษต้องน้อยกว่าหนึ่งบาท_ไม่งั้นไม่ใช่เศษสตางค์()
    {
        // "rounding 12.00" = อ่านผิดช่อง ไม่ใช่การปัดเศษ ⇒ ต้องไม่รับ
        Assert.Null(ThaiBillSurcharge.Read("Rounding    12.00").RoundingAdjustment);
        Assert.Equal(0.40m, ThaiBillSurcharge.Read("เศษสตางค์   0.40").RoundingAdjustment);
    }

    [Fact]
    public void ใบแจ้งหนี้บริการอ่านเข้าป้ายเดียวกัน_ตัวอ่านรายงานตามจริง_ผู้เรียกเป็นคนกรอง()
    {
        // "ค่าบริการรายเดือน" เป็นชื่อรายการปกติ ไม่ใช่ service charge —
        // ตัวอ่านบอกตามที่เห็นบนกระดาษ ส่วนด่านกันการนับซ้ำอยู่ที่ผู้เรียก
        // (ต้องให้ Σ บรรทัด + ค่านี้ = ยอดก่อน VAT ถึงจะเติมเป็นบรรทัด)
        var r = ThaiBillSurcharge.Read("ค่าบริการรายเดือน กันยายน  5,000.00");
        Assert.Equal(5000m, r.ServiceChargeAmount);
    }
}

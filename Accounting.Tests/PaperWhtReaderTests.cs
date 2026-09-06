using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่าน "ยอดหัก ณ ที่จ่ายตรงสูตรไหม" จะมีความหมายก็ต่อเมื่อยอดที่ป้อนเข้าไปเป็น
/// **ยอดที่พิมพ์บนกระดาษ** — เดิมผู้เรียกคำนวณยอดจากสูตรเดียวกันแล้วส่งเข้าไปตรวจ
/// ⇒ ผ่านทุกครั้งตลอดกาล = ด่านที่ไม่มีอยู่จริง (ผลตรวจ 2026-09-06 · T2-02)
/// </summary>
public class PaperWhtReaderTests
{
    [Fact]
    public void อ่านยอดและอัตราจากใบบริการทั่วไป()
    {
        var w = PaperWhtReader.Read(
            "ใบแจ้งหนี้ค่าบริการ\nรวมเงิน 10,000.00\nภาษีมูลค่าเพิ่ม 7% 700.00\n"
            + "หัก ณ ที่จ่าย 3% 300.00\nยอดสุทธิ 10,400.00", 10000m);
        Assert.Equal(300m, w.Amount);
        Assert.Equal(3m, w.RatePercent);
    }

    [Fact]
    public void อัตราในวงเล็บ_และป้ายแบบเต็ม()
    {
        var w = PaperWhtReader.Read("ค่าบริการ 5,000.00\nภาษีหัก ณ ที่จ่าย (3%) 150.00", 5000m);
        Assert.Equal(150m, w.Amount);
        Assert.Equal(3m, w.RatePercent);
    }

    [Fact]
    public void ป้ายภาษาอังกฤษ()
    {
        var w = PaperWhtReader.Read("WHT 3% 45.00", 1500m);
        Assert.Equal(45m, w.Amount);
        Assert.Equal(3m, w.RatePercent);
    }

    [Fact]
    public void กระดาษบอกยอดแต่ไม่บอกอัตรา()
    {
        var w = PaperWhtReader.Read("หัก ณ ที่จ่าย 300.00", 10000m);
        Assert.Equal(300m, w.Amount);
        Assert.Null(w.RatePercent);
    }

    [Fact]
    public void กระดาษไม่พูดถึงหักณที่จ่ายเลย_ต้องไม่แต่งยอด()
    {
        var w = PaperWhtReader.Read("รวมทั้งสิ้น 10,700.00", 10000m);
        Assert.Null(w.Amount);
        Assert.Null(w.RatePercent);
    }

    [Fact]
    public void ยอดหักมากกว่าฐาน_ต้องไม่ถูกหยิบ()
    {
        // ป้ายอยู่ท้ายใบแล้วเลขถัดไปคือยอดรวม — หยิบมาเป็นยอดหักไม่ได้
        var w = PaperWhtReader.Read("หัก ณ ที่จ่าย\n10,700.00", 10000m);
        Assert.Null(w.Amount);
    }

    [Fact]
    public void ยอดที่ผู้ขายคิดผิดฐาน_ต้องอ่านออกมาให้ด่านจับได้()
    {
        // 3% ของ 10,700 (รวม VAT) = 321 — ผิด เพราะต้องคิดจาก 10,000
        // ตัวอ่านต้องคืน 321 ตามกระดาษ **ไม่ใช่** 300 ตามสูตร ไม่งั้นด่านไม่มีอะไรจับ
        var w = PaperWhtReader.Read("รวมเงิน 10,000.00\nVAT 700.00\nหัก ณ ที่จ่าย 3% 321.00", 10000m);
        Assert.Equal(321m, w.Amount);
        Assert.Equal(3m, w.RatePercent);
    }

    [Fact]
    public void ข้อความว่าง_ต้องไม่ระเบิด()
    {
        Assert.Null(PaperWhtReader.Read(null).Amount);
        Assert.Null(PaperWhtReader.Read("   ").RatePercent);
    }
}

/// <summary>อัตราที่อนุมานจากยอดบนกระดาษต้อง "snap" เข้าอัตราตามกฎหมายเท่านั้น —
/// อัตราที่ snap ไม่ได้ (ผู้ขายคิดผิดฐาน) ต้องไม่ถูกเขียนลงเอกสาร</summary>
public class StatutoryWhtRateSnapTests
{
    [Theory]
    [InlineData(3.00, 3)]
    [InlineData(2.95, 3)]
    [InlineData(5.10, 5)]
    [InlineData(1.02, 1)]
    [InlineData(15.00, 15)]
    public void อัตราที่ใกล้ค่าตามกฎหมาย_ต้อง_snap(double raw, int expected)
        => Assert.Equal((decimal)expected, ThaiWhtRateTable.SnapToStatutory((decimal)raw));

    [Theory]
    [InlineData(3.21)]   // 3% ของยอดรวม VAT — ผู้ขายคิดผิดฐาน
    [InlineData(7.00)]   // ไม่ใช่อัตราหัก ณ ที่จ่ายของไทย (นี่คือ VAT)
    [InlineData(0.50)]
    [InlineData(0)]
    public void อัตราที่ไม่ใช่ของไทย_ต้องคืน_null_ไม่ใช่ปัดเข้าใกล้ที่สุด(double raw)
        => Assert.Null(ThaiWhtRateTable.SnapToStatutory((decimal)raw));

    [Fact]
    public void ไม่มีอัตรา_ต้องคืน_null()
        => Assert.Null(ThaiWhtRateTable.SnapToStatutory(null));
}

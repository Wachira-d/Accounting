using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม IN — C-5 (คำตัดสินข้อ 78): เครื่องมือแอดมิน "ตรวจยอดสต็อกรวม ↔ ผลรวมคลัง" รายงานก่อน → ซ่อมเมื่อกด + audit
///
/// <para>เคสจริงจากผลตรวจ E-10: สินค้าที่ตอน migration เฟส 0 ยังไม่ติดตามสต็อก ⇒ ไม่มีแถวคลัง แต่ <c>CurrentStock</c> ค้าง 40 ⇒
/// หน้าสินค้าโชว์คงเหลือ 40 ขณะที่ขายถูกบล็อก "คงเหลือ 0" · สองทิศ: แถวที่ตรงกันต้องไม่อยู่ในรายงาน/ไม่ถูกแตะ ·
/// แถวที่ประวัติไม่ตรงผลรวมคลัง ("ไม่รู้" ว่าตัวไหนถูก) ห้ามซ่อมจนกว่าผู้ใช้ยืนยันว่าตรวจนับแล้ว</para>
/// </summary>
public class StockTotalsReconciliationTests
{
    private static readonly Guid P1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid P2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid P3 = Guid.Parse("00000000-0000-0000-0000-000000000003");

    private static StockTotalsEvidence E(Guid id, string code, decimal current, decimal wh, decimal mv)
        => new(id, code, "สินค้า " + code, current, wh, mv);

    [Fact]
    public void ยอดตรงกัน_ไม่อยู่ในรายงาน()
        => Assert.Empty(StockTotalsReconciliation.Find(new[] { E(P1, "A", 10m, 10m, 10m) }));

    [Fact]
    public void สินค้าก่อนเฟส0_คงเหลือ40_คลัง0_ประวัติ0_อยู่ในรายงานและประวัติยืนยันฝั่งคลัง()
    {
        var m = Assert.Single(StockTotalsReconciliation.Find(new[] { E(P1, "A", 40m, 0m, 0m) }));
        Assert.True(m.HistoryAgrees);
        Assert.Equal(-40m, m.Difference);
        Assert.Equal(StockTotalsReconciliation.HistoryAgreesNote, m.Note);
    }

    [Fact]
    public void ประวัติไม่ตรงผลรวมคลัง_บอกว่าไม่รู้และให้ตรวจนับก่อน()
    {
        var m = Assert.Single(StockTotalsReconciliation.Find(new[] { E(P2, "B", 40m, 0m, 40m) }));
        Assert.False(m.HistoryAgrees);
        Assert.Contains("ตรวจนับ", m.Note);
    }

    [Fact]
    public void รายงานเรียงตามรหัส_และกรองเฉพาะแถวที่ไม่ตรง()
    {
        var list = StockTotalsReconciliation.Find(new[]
        {
            E(P3, "C", 5m, 6m, 6m), E(P1, "A", 1m, 1m, 1m), E(P2, "B", 2m, 0m, 0m),
        });
        Assert.Equal(new[] { "B", "C" }, list.Select(x => x.Code));
    }

    [Fact]
    public void ซ่อมได้เมื่อค่ายังเท่าที่เห็นและประวัติยืนยัน()
        => Assert.Null(StockTotalsReconciliation.SkipReason(40m, 0m, E(P1, "A", 40m, 0m, 0m), confirmPhysicalCount: false));

    [Fact]
    public void ยอดขยับหลังเปิดรายงาน_ข้าม_ไม่ทับยอดใหม่()
        => Assert.Contains("เปลี่ยนไปหลังเปิดรายงาน",
            StockTotalsReconciliation.SkipReason(40m, 0m, E(P1, "A", 38m, -2m, -2m), confirmPhysicalCount: true));

    [Fact]
    public void ประวัติไม่ตรง_ไม่ยืนยันตรวจนับ_ข้าม()
        => Assert.Equal(StockTotalsReconciliation.HistoryDisagreesNote,
            StockTotalsReconciliation.SkipReason(40m, 0m, E(P2, "B", 40m, 0m, 40m), confirmPhysicalCount: false));

    [Fact]
    public void ประวัติไม่ตรง_ยืนยันตรวจนับแล้ว_ซ่อมได้()
        => Assert.Null(StockTotalsReconciliation.SkipReason(40m, 0m, E(P2, "B", 40m, 0m, 40m), confirmPhysicalCount: true));

    [Fact]
    public void ตรงกันแล้วตอนกดซ่อม_ไม่ซ่อมซ้ำ()
        => Assert.NotNull(StockTotalsReconciliation.SkipReason(40m, 0m, E(P1, "A", 0m, 0m, 0m), confirmPhysicalCount: true));

    [Fact]
    public void ยอดจากประวัติ_OUTใช้ค่าสัมบูรณ์_ชนิดอื่นคงเครื่องหมาย_คู่โอนรวมเป็นศูนย์()
    {
        var balance = StockTotalsReconciliation.MovementBalance(new (string?, decimal, decimal)[]
        {
            ("IN", 30m, 30m),
            ("OUT", -4m, 8m),           // ledger −6 + แถวเก่าก่อนเฟส 0 +2 ⇒ ขาออกจริง 6+2 = 8
            ("ADJUST", -2m, 4m),        // +1 −3
            ("OPENING", 5m, 5m),
            ("TRANSFER_OUT", -7m, 7m),
            ("TRANSFER_IN", 7m, 7m),
        });
        Assert.Equal(30m - 8m - 2m + 5m, balance);
    }
}

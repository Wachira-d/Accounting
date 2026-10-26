using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 แพ็ก W5 (R-51) — เปิดให้เพิ่ม folio ก่อนเช็คอินได้ จึงต้องมีกติกาเมื่อการจองถูกยกเลิก/ไม่มา:
/// Pending = ล้าง · Paid (ออกใบแล้ว) ไม่แตะ · <b>ทิศตรงข้าม</b>: folio ของแขก CheckedIn/CheckedOut ไม่ถูกล้างจากเส้นนี้เสมอ
/// </summary>
public class LodgingFolioOnCancelTests
{
    private static readonly Guid Pending1 = Guid.NewGuid(), Pending2 = Guid.NewGuid(), Paid = Guid.NewGuid(), Cancelled = Guid.NewGuid();

    private static (Guid, LodgingChargeStatus)[] Mixed() => new[]
    {
        (Pending1, LodgingChargeStatus.Pending), (Paid, LodgingChargeStatus.Paid),
        (Pending2, LodgingChargeStatus.Pending), (Cancelled, LodgingChargeStatus.Cancelled),
    };

    [Theory]
    [InlineData(LodgingReservationStatus.Pending)]
    [InlineData(LodgingReservationStatus.Confirmed)]
    public void ยกเลิกก่อนเช็คอิน_ล้างเฉพาะPending_Paidและที่ยกเลิกแล้วไม่แตะ(LodgingReservationStatus before)
    {
        var ids = LodgingFolioOnCancel.ToVoid(before, Mixed());
        Assert.Equal(new[] { Pending1, Pending2 }, ids);
        Assert.DoesNotContain(Paid, ids);
        Assert.DoesNotContain(Cancelled, ids);
    }

    [Theory]
    [InlineData(LodgingReservationStatus.CheckedIn)]
    [InlineData(LodgingReservationStatus.CheckedOut)]
    public void ทิศตรงข้าม_แขกเช็คอินแล้ว_folioไม่ถูกล้าง(LodgingReservationStatus before)
        => Assert.Empty(LodgingFolioOnCancel.ToVoid(before, Mixed()));

    [Fact]
    public void ไม่มีfolio_ว่าง() => Assert.Empty(LodgingFolioOnCancel.ToVoid(LodgingReservationStatus.Confirmed, Array.Empty<(Guid, LodgingChargeStatus)>()));

    [Fact]
    public void หมายเหตุบอกจำนวนยอดและเหตุ_และบอกว่าใบที่ออกแล้วไม่ถูกแตะ()
    {
        var n = LodgingFolioOnCancel.Note(2, 1200m, noShow: true);
        Assert.Contains("2 รายการ", n);
        Assert.Contains("1,200.00", n);
        Assert.Contains("ไม่มาเข้าพัก", n);
        Assert.Contains("ใบลดหนี้", n);
        Assert.Contains("ยกเลิกการจอง", LodgingFolioOnCancel.Note(1, 100m, noShow: false));
    }
}

using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 W6 — สายต่อเครื่องจอง CMS ↔ สมุดเครดิต (<c>Helpers/CreditBookingLink</c> · R-39) และขายคอร์สผ่านร้านออนไลน์
/// (<c>CourseCreditOnlineSale</c> · R-41 B2): การจองที่ไม่ใช้บัตรไม่แตะสมุด/เดินเส้น ERP เดิม · ใบเดียว = บัตรเดียว
/// </summary>
public class CreditBookingLinkTests
{
    [Fact]
    public void การจองไม่ใช้บัตร_ไม่แตะสมุด_และยังออกเอกสารERPตามเดิม()
    {
        Assert.Equal(CreditBookingLink.LedgerAction.None, CreditBookingLink.ActionFor(BookingStatus.Confirmed, BookingStatus.Completed, paidWithCredit: false));
        Assert.Equal(CreditBookingLink.LedgerAction.None, CreditBookingLink.ActionFor(BookingStatus.Confirmed, BookingStatus.Cancelled, paidWithCredit: false));
        Assert.True(CreditBookingLink.ShouldSyncErp(paidWithCredit: false));
    }

    [Fact]
    public void การจองใช้บัตร_เสร็จ_ไม่มา_ยกเลิก_ตรงกับSettle_Release_และไม่ออกเอกสารERP()
    {
        Assert.Equal(CreditBookingLink.LedgerAction.SettleAttended, CreditBookingLink.ActionFor(BookingStatus.Confirmed, BookingStatus.Completed, true));
        Assert.Equal(CreditBookingLink.LedgerAction.SettleNoShow, CreditBookingLink.ActionFor(BookingStatus.Confirmed, BookingStatus.NoShow, true));
        Assert.Equal(CreditBookingLink.LedgerAction.Release, CreditBookingLink.ActionFor(BookingStatus.Pending, BookingStatus.Cancelled, true));
        // ยืนยัน / เลื่อนคิว / สถานะเดิมซ้ำ ⇒ ไม่แตะสมุด
        Assert.Equal(CreditBookingLink.LedgerAction.None, CreditBookingLink.ActionFor(BookingStatus.Pending, BookingStatus.Confirmed, true));
        Assert.Equal(CreditBookingLink.LedgerAction.None, CreditBookingLink.ActionFor(BookingStatus.Completed, BookingStatus.Completed, true));
        Assert.False(CreditBookingLink.ShouldSyncErp(paidWithCredit: true));
    }

    [Fact]
    public void เลขอ้างอิงและเวลาเริ่มUTC_รูปแบบเดียว()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("SiteBooking:11111111-2222-3333-4444-555555555555", CreditBookingLink.Ref(id));
        // 2026-10-26 09:30 ไทย = 02:30 UTC
        var utc = CreditBookingLink.StartUtc(new DateTime(2026, 10, 26), new TimeOnly(9, 30));
        Assert.Equal(new DateTime(2026, 10, 26, 2, 30, 0), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Contains("CP-1", CreditBookingLink.PaidNote("CP-1", 1m, 500m));
        Assert.Contains("[CREDIT-PASS-FAILED]", CreditBookingLink.LedgerFailureNote(CreditBookingLink.LedgerAction.Release, "x"));
    }

    // ===== R-41 B2 ขายคอร์สผ่านร้านออนไลน์ =====

    [Fact]
    public void ออเดอร์คอร์ส1หน่วย_เปิดบัตร_หลายหน่วยไม่เปิดเอง_เปิดแล้วข้าม_ไม่มีคอร์สไม่ทำอะไร()
    {
        Assert.Equal(CourseCreditOnlineSale.Verdict.Activate, CourseCreditOnlineSale.Judge(1m, passAlreadyOpen: false));
        Assert.Equal(CourseCreditOnlineSale.Verdict.TooMany, CourseCreditOnlineSale.Judge(2m, false));
        Assert.Equal(CourseCreditOnlineSale.Verdict.AlreadyOpen, CourseCreditOnlineSale.Judge(1m, true));
        Assert.Equal(CourseCreditOnlineSale.Verdict.None, CourseCreditOnlineSale.Judge(0m, false));
        Assert.Contains("[COURSE-PASS-FAILED]", CourseCreditOnlineSale.TooManyNote("SO-1", 2m));
    }

    [Fact]
    public void สินค้าคอร์สสั่งได้ครั้งละ1_สินค้าปกติคงค่าเดิม()
    {
        Assert.Equal(1, CourseCreditOnlineSale.MaxOrderQuantityFor(Guid.NewGuid(), 5));
        Assert.Equal(1, CourseCreditOnlineSale.MaxOrderQuantityFor(Guid.NewGuid(), null));
        Assert.Equal(5, CourseCreditOnlineSale.MaxOrderQuantityFor(null, 5));
        Assert.Null(CourseCreditOnlineSale.MaxOrderQuantityFor(null, null));
    }
}

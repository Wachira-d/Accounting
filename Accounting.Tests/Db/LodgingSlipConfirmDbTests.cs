using Accounting.Helpers;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Lodging;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 202 (คำตัดสินข้อ 128) — ที่พักตั้ง "ต้องส่งสลิปก่อนการจองจึงสำเร็จ" · รันเส้นจริงบน PostgreSQL (ล็อก · ตัวยกเลิกอัตโนมัติ · การบันทึกไฟล์สลิป)
///
/// <para>(1) RequireSlip + ส่งสลิปแล้วยืนยันทันที: จองจากเว็บ ⇒ รอชำระ (hold = กำหนดส่งสลิป · ยอดที่ต้องโอน = ยอดเต็มเมื่อมัดจำ 0) ⇒ ส่งสลิป ⇒ ยืนยันแล้ว
/// แต่ <b>DepositPaid/PaidAmount ยัง 0</b> (ส่งสลิป ≠ รับเงิน) ⇒ ปฏิเสธสลิป ⇒ กลับรอชำระ + hold 24 ชม.</para>
/// <para>(2) ไม่ส่งสลิปจนหมดเวลา ⇒ แขกเปิดหน้าการจอง ⇒ ระบบยกเลิกตามกติกากันห้องเดิม + ป้าย "หมดเวลาส่งสลิป"</para>
/// <para>(3) ทิศตรงข้าม: ปิด "ยืนยันทันที" ⇒ ส่งสลิปแล้วยังรอที่พักตรวจ · เส้นพนักงานของที่พักเดียวกันไม่ถูกบังคับส่งสลิป</para>
/// </summary>
[Trait("Category", "Db")]
public class LodgingSlipConfirmDbTests
{
    private readonly ITestOutputHelper _out;
    public LodgingSlipConfirmDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<(Guid CompanyId, Guid PropertyId, Guid RoomTypeId, Guid SiteId)> SeedAsync(
        Accounting.Data.AccountingDbContext db, bool autoConfirmOnSlip, decimal depositPercent = 0m)
    {
        var company = new Company { Name = "รีสอร์ททดสอบส่งสลิปก่อน", TaxId = "0105556000001" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var prop = new LodgingProperty
        {
            CompanyId = company.Id, Name = "บ้านริมน้ำ", Code = "Q" + Random.Shared.Next(1000, 9999), IsActive = true,
            AccountingMode = LodgingAccountingMode.Off,
            GuestConfirmMode = LodgingGuestConfirmMode.RequireSlip, SlipDeadlineMinutes = 30, AutoConfirmOnSlip = autoConfirmOnSlip,
            DepositPercent = depositPercent,
        };
        db.LodgingProperties.Add(prop);
        await db.SaveChangesAsync();
        var rt = new LodgingRoomType
        {
            CompanyId = company.Id, PropertyId = prop.Id, Name = "Deluxe", Code = "DLX", Slug = "deluxe", BaseRate = 1000m,
            MaxAdults = 2, StandardOccupancy = 2, IsActive = true,
        };
        db.LodgingRoomTypes.Add(rt);
        await db.SaveChangesAsync();
        db.LodgingUnits.Add(new LodgingUnit { CompanyId = company.Id, RoomTypeId = rt.Id, Number = "A1", IsActive = true });
        await db.SaveChangesAsync();
        return (company.Id, prop.Id, rt.Id, Guid.NewGuid());
    }

    private static LodgingCreateReservationRequest GuestRequest(Guid roomTypeId)
    {
        var checkIn = DateTime.UtcNow.AddHours(7).Date.AddDays(7);
        return new LodgingCreateReservationRequest(checkIn, checkIn.AddDays(1), new List<LodgingQuoteRoomRequest> { new(roomTypeId, 2) },
            "แขกทดสอบ", null, "0861489669");
    }

    private static IFormFile PngSlip()
    {
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "slip.png")
        {
            Headers = new HeaderDictionary(), ContentType = "image/png",
        };
    }

    [Fact]
    public async Task ส่งสลิปก่อน_ยืนยันทันที_แต่ไม่ประทับยอดเงิน_ปฏิเสธแล้วกลับรอชำระ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, pid, rtId, siteId) = await SeedAsync(db, autoConfirmOnSlip: true);
        var svc = new LodgingService(db, NullLogger<LodgingService>.Instance, null!);

        var created = await svc.CreateReservationAsync(cid, pid, GuestRequest(rtId), LodgingReservationSource.Web, "storefront-guest", siteId);
        Assert.Equal(LodgingReservationStatus.Pending, created.Status);
        Assert.True(created.SlipRequired);
        Assert.Equal(created.TotalAmount, created.DepositRequired);          // มัดจำ 0% ⇒ ยอดที่ต้องโอน = ยอดเต็ม
        Assert.Equal(created.TotalAmount, created.AmountToTransfer);
        Assert.NotNull(created.HoldExpiresAt);
        Assert.InRange(created.HoldExpiresAt!.Value, DateTime.UtcNow.AddMinutes(25), DateTime.UtcNow.AddMinutes(35));
        Assert.Equal(LodgingGuestConfirmMode.RequireSlip, created.GuestConfirmMode);

        var token = (await db.LodgingReservations.AsNoTracking().SingleAsync(r => r.Id == created.Id && r.CompanyId == cid)).PublicToken;
        var afterSlip = await svc.UploadSlipByTokenAsync(cid, siteId, token, PngSlip(), "โอน 12:05");
        Assert.NotNull(afterSlip);
        Assert.Equal(LodgingReservationStatus.Confirmed, afterSlip!.Status);
        Assert.Contains("จองสำเร็จ", afterSlip.GuestMessage);

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var row = await check.LodgingReservations.AsNoTracking().SingleAsync(r => r.Id == created.Id && r.CompanyId == cid);
            Assert.Equal(LodgingReservationStatus.Confirmed, row.Status);
            Assert.Equal(0m, row.DepositPaid);                               // ส่งสลิป ≠ รับเงิน
            Assert.Equal(0m, row.PaidAmount);
            Assert.Equal(LodgingGuestConfirmPolicy.SlipConfirmActor, row.ConfirmedBy);
            Assert.Null(row.HoldExpiresAt);
            Assert.Null(row.DepositDocumentId);
        }

        // พนักงานเห็นในคิว "มีสลิปรอตรวจ" (ใบยืนยันเพราะสลิปที่ยังไม่บันทึกรับเงิน)
        var queue = await svc.ListReservationsAsync(cid, pid, null, null, null, null, "slips", 1, 50);
        var item = Assert.Single(queue.Items.Where(x => x.Id == created.Id));
        Assert.Equal("ยืนยันจากสลิป · ยังไม่บันทึกรับเงิน", item.SlipStateLabel);

        var rejected = await svc.RejectSlipAsync(cid, created.Id, new LodgingRejectSlipRequest("ยอดไม่ตรง"), "staff-1");
        Assert.NotNull(rejected);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var row = await check.LodgingReservations.AsNoTracking().SingleAsync(r => r.Id == created.Id && r.CompanyId == cid);
            Assert.Equal(LodgingReservationStatus.Pending, row.Status);
            Assert.Null(row.ConfirmedBy);
            Assert.NotNull(row.HoldExpiresAt);
            Assert.InRange(row.HoldExpiresAt!.Value, DateTime.UtcNow.AddHours(23), DateTime.UtcNow.AddHours(25));
            Assert.Equal(0m, row.DepositPaid);
        }
    }

    [Fact]
    public async Task ไม่ส่งสลิปจนหมดเวลา_เปิดหน้าการจอง_ระบบยกเลิกตามกติกาเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, pid, rtId, siteId) = await SeedAsync(db, autoConfirmOnSlip: true);
        var svc = new LodgingService(db, NullLogger<LodgingService>.Instance, null!);
        var created = await svc.CreateReservationAsync(cid, pid, GuestRequest(rtId), LodgingReservationSource.Web, "storefront-guest", siteId);

        var row = await db.LodgingReservations.SingleAsync(r => r.Id == created.Id && r.CompanyId == cid);
        row.HoldExpiresAt = DateTime.UtcNow.AddMinutes(-1);              // จำลองเวลาผ่านไปเกินกำหนดส่งสลิป
        await db.SaveChangesAsync();

        var seen = await svc.GetReservationByTokenAsync(cid, siteId, row.PublicToken);
        Assert.NotNull(seen);
        Assert.Equal(LodgingReservationStatus.Cancelled, seen!.Status);
        Assert.Equal("หมดเวลาส่งสลิป — การจองถูกยกเลิก", seen.GuestStatusLabel);
        Assert.True(seen.CanUploadSlip);                                  // ข้อ 127: โอนแล้วส่งสลิปได้ (รับไว้ + ธง)
        using var check = DbTestDatabase.TryCreateContext()!;
        var saved = await check.LodgingReservations.AsNoTracking().SingleAsync(r => r.Id == created.Id && r.CompanyId == cid);
        Assert.Equal(LodgingHoldRule.AutoExpireReason, saved.CancellationReason);
    }

    [Fact]
    public async Task ทิศตรงข้าม_ปิดยืนยันทันที_ส่งสลิปแล้วรอตรวจ_และพนักงานไม่ถูกบังคับส่งสลิป()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, pid, rtId, siteId) = await SeedAsync(db, autoConfirmOnSlip: false, depositPercent: 50m);
        var svc = new LodgingService(db, NullLogger<LodgingService>.Instance, null!);

        var guest = await svc.CreateReservationAsync(cid, pid, GuestRequest(rtId), LodgingReservationSource.Web, "storefront-guest", siteId);
        Assert.True(guest.DepositRequired < guest.TotalAmount);           // มัดจำ 50% ⇒ ยอดที่ต้องโอน = มัดจำ (ไม่ใช่ยอดเต็ม)
        var token = (await db.LodgingReservations.AsNoTracking().SingleAsync(r => r.Id == guest.Id && r.CompanyId == cid)).PublicToken;
        var afterSlip = await svc.UploadSlipByTokenAsync(cid, siteId, token, PngSlip(), null);
        Assert.Equal(LodgingReservationStatus.Pending, afterSlip!.Status);
        Assert.Equal("ส่งคำขอจองสำเร็จ — รอที่พักตรวจสลิป", afterSlip.GuestStatusLabel);
        Assert.Equal(0m, afterSlip.DepositPaid);

        // เส้นพนักงาน (โทร) ของที่พักเดียวกัน: ConfirmImmediately = ยืนยันทันที ไม่ต้องสลิป · ไม่ติ๊ก = รอมัดจำแบบเดิม (hold ตาม PaymentHoldMinutes)
        var checkIn = DateTime.UtcNow.AddHours(7).Date.AddDays(20);
        var staffNow = await svc.CreateReservationAsync(cid, pid, new LodgingCreateReservationRequest(checkIn, checkIn.AddDays(1),
            new List<LodgingQuoteRoomRequest> { new(rtId, 2) }, "แขกโทรมา", null, "0811111111", ConfirmImmediately: true),
            LodgingReservationSource.Phone, "staff-1");
        Assert.Equal(LodgingReservationStatus.Confirmed, staffNow.Status);
        Assert.False(staffNow.SlipRequired);
        Assert.Equal(LodgingGuestConfirmMode.RequireDeposit, staffNow.GuestConfirmMode);
        var staffPending = await svc.CreateReservationAsync(cid, pid, new LodgingCreateReservationRequest(checkIn.AddDays(3), checkIn.AddDays(4),
            new List<LodgingQuoteRoomRequest> { new(rtId, 2) }, "แขกโทรมา 2", null, "0822222222"),
            LodgingReservationSource.Phone, "staff-1");
        Assert.Equal(LodgingReservationStatus.Pending, staffPending.Status);
        Assert.False(staffPending.SlipRequired);
        Assert.InRange(staffPending.HoldExpiresAt!.Value, DateTime.UtcNow.AddHours(23), DateTime.UtcNow.AddHours(25));
    }
}

using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Lodging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 202 ทีม LO (O-P0-1) — <b>ห้องสุดท้ายต้องถูกจองได้ใบเดียว</b> แม้หลายคำขอยิงพร้อมกันผ่านคนละ DbContext (= คนละ request/เครื่อง)
///
/// <para>เดิมตรวจห้องว่าง + คิดราคานอกล็อก แล้วล็อกเฉพาะตอนออกเลขจอง ⇒ ทุกคำขอเห็น "ว่าง 1" แล้วได้เลขจองคนละเลข = จองซ้อน ·
/// ตอนนี้ <c>CreateReservationAsync</c> ทำทั้งก้อนใต้ <c>pg_advisory_xact_lock</c> ของที่พัก ⇒ ใบแรกได้ ใบที่เหลือได้ข้อความ "ว่าง 0 ห้อง"</para>
///
/// <para>ทิศตรงข้าม (ด่านไม่ได้ปิดการจองทั้งหมด): ที่พักที่มี 2 ห้อง ยิง 2 คำขอพร้อมกัน ⇒ ได้ครบทั้งสองใบ</para>
/// </summary>
[Trait("Category", "Db")]
public class LodgingDoubleBookingDbTests
{
    private readonly ITestOutputHelper _out;
    public LodgingDoubleBookingDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<(Guid CompanyId, Guid PropertyId, Guid RoomTypeId)> SeedAsync(Accounting.Data.AccountingDbContext db, int units)
    {
        var company = new Company { Name = "รีสอร์ททดสอบจองซ้อน", TaxId = "0105556000001" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var prop = new LodgingProperty { CompanyId = company.Id, Name = "บ้านริมน้ำ", Code = "T" + Random.Shared.Next(1000, 9999), IsActive = true };
        db.LodgingProperties.Add(prop);
        await db.SaveChangesAsync();
        var rt = new LodgingRoomType
        {
            CompanyId = company.Id, PropertyId = prop.Id, Name = "Deluxe", Code = "DLX", Slug = "deluxe", BaseRate = 1500m,
            MaxAdults = 2, StandardOccupancy = 2, IsActive = true,
        };
        db.LodgingRoomTypes.Add(rt);
        await db.SaveChangesAsync();
        for (var i = 0; i < units; i++)
            db.LodgingUnits.Add(new LodgingUnit { CompanyId = company.Id, RoomTypeId = rt.Id, Number = $"10{i + 1}", IsActive = true });
        await db.SaveChangesAsync();
        return (company.Id, prop.Id, rt.Id);
    }

    private static async Task<bool> TryBookAsync(Guid companyId, Guid propertyId, Guid roomTypeId, int n)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        var svc = new LodgingService(db, NullLogger<LodgingService>.Instance, null!);
        var checkIn = DateTime.UtcNow.AddHours(7).Date.AddDays(10);
        try
        {
            await svc.CreateReservationAsync(companyId, propertyId, new LodgingCreateReservationRequest(
                checkIn, checkIn.AddDays(2), new List<LodgingQuoteRoomRequest> { new(roomTypeId, 2) },
                $"แขกคนที่ {n}", null, $"08100000{n:00}"), LodgingReservationSource.Phone, "db-test");
            return true;
        }
        catch (Accounting.Helpers.BusinessRuleException) { return false; }   // "ว่าง 0 ห้อง" — ทางที่ถูกต้องของคำขอที่แพ้
    }

    private static async Task<int> BlockingCountAsync(Guid companyId, Guid propertyId)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        return await db.LodgingReservations.AsNoTracking().CountAsync(r => r.CompanyId == companyId && r.PropertyId == propertyId
            && (r.Status == LodgingReservationStatus.Pending || r.Status == LodgingReservationStatus.Confirmed));
    }

    [Fact]
    public async Task ห้องสุดท้าย_ยิงพร้อมกันหกคำขอ_ได้ใบเดียว()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, pid, rtId) = await SeedAsync(seedDb, units: 1);

        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(n => Task.Run(() => TryBookAsync(cid, pid, rtId, n))));

        Assert.Equal(1, results.Count(ok => ok));
        Assert.Equal(1, await BlockingCountAsync(cid, pid));
    }

    [Fact]
    public async Task สองห้อง_สองคำขอพร้อมกัน_ได้ครบทั้งสอง()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, pid, rtId) = await SeedAsync(seedDb, units: 2);

        var results = await Task.WhenAll(Enumerable.Range(1, 2).Select(n => Task.Run(() => TryBookAsync(cid, pid, rtId, n))));

        Assert.All(results, ok => Assert.True(ok));
        Assert.Equal(2, await BlockingCountAsync(cid, pid));
    }
}

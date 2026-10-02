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
/// 2026-10-02 ผู้ใช้รายงาน: แขกที่พักกด "ส่งสลิป" แล้วได้ 500 (<c>DbUpdateException</c> REF:35F0C2D7) —
/// ทางเข้าของคนนอกระบบ (สลิปแขกที่พัก · สลิปหน้าร้าน · พอร์ทัลลูกค้า) บันทึก <c>FileAttachment</c> ด้วย
/// <c>UploadedByUserId = Guid.Empty</c> แต่คอลัมน์มี FK → <c>Users</c> ⇒ ชน FK ทุกครั้ง · ตอนนี้ "ไม่มีผู้ใช้" = null
///
/// <para>เทสต์ pure จับไม่ได้ (FK อยู่ในฐานจริงเท่านั้น) ⇒ เทสต์นี้รันเส้นจริง <c>UploadSlipByTokenAsync</c> บน PostgreSQL ·
/// ทิศตรงข้าม: FK ยังอยู่ — ใส่ id ผู้ใช้ที่ไม่มีจริง (รวม Guid.Empty) ต้องล้ม ไม่ใช่ถอด FK ทิ้งเพื่อให้ผ่าน</para>
/// </summary>
[Trait("Category", "Db")]
public class GuestUploadFileAttachmentDbTests
{
    private readonly ITestOutputHelper _out;
    public GuestUploadFileAttachmentDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<(Guid CompanyId, Guid PropertyId, Guid RoomTypeId)> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "รีสอร์ททดสอบสลิป", TaxId = "0105556000001" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var prop = new LodgingProperty
        {
            CompanyId = company.Id, Name = "บ้านริมน้ำ", Code = "S" + Random.Shared.Next(1000, 9999), IsActive = true,
            AccountingMode = LodgingAccountingMode.Off,
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
        return (company.Id, prop.Id, rt.Id);
    }

    private static IFormFile PngSlip()
    {
        // PNG 1×1 จริง (signature + IHDR + IDAT + IEND) — เส้นไม่มีตัวประมวลผลรูปจะเขียนไบต์ตามตัว
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "slip.png")
        {
            Headers = new HeaderDictionary(), ContentType = "image/png",
        };
    }

    [Fact]
    public async Task แขกส่งสลิปผ่านลิงก์การจอง_บันทึกได้_ไฟล์ไม่มีผู้ใช้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, pid, rtId) = await SeedAsync(db);
        var siteId = Guid.NewGuid();

        var svc = new LodgingService(db, NullLogger<LodgingService>.Instance, null!);
        var checkIn = DateTime.UtcNow.AddHours(7).Date.AddDays(7);
        var created = await svc.CreateReservationAsync(cid, pid, new LodgingCreateReservationRequest(
            checkIn, checkIn.AddDays(1), new List<LodgingQuoteRoomRequest> { new(rtId, 2) }, "test", null, "0861489669"),
            LodgingReservationSource.Phone, "db-test");
        var row = await db.LodgingReservations.SingleAsync(r => r.Id == created.Id && r.CompanyId == cid);
        row.SiteId = siteId;   // การจองจากหน้าเว็บ (token ผูกกับเว็บ)
        await db.SaveChangesAsync();

        var res = await svc.UploadSlipByTokenAsync(cid, siteId, row.PublicToken, PngSlip(), "โอน 14:45");

        Assert.NotNull(res);
        using var check = DbTestDatabase.TryCreateContext()!;
        var saved = await check.LodgingReservations.AsNoTracking().SingleAsync(r => r.Id == created.Id && r.CompanyId == cid);
        Assert.NotNull(saved.SlipUploadedAt);
        Assert.False(string.IsNullOrEmpty(saved.PaymentSlipUrl));
        var file = await check.FileAttachments.AsNoTracking()
            .SingleAsync(f => f.CompanyId == cid && f.EntityType == "LodgingReservation" && f.EntityId == created.Id);
        Assert.Null(file.UploadedByUserId);
    }

    [Fact]
    public async Task ทิศตรงข้าม_FK_ผู้อัปโหลดยังอยู่_id_ที่ไม่มีจริงต้องล้ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบ FK ไฟล์แนบ", TaxId = "0105556000001" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        // ค่าเดิมของทางเข้าคนนอก (Guid.Empty) = สาเหตุของ 500 ที่ผู้ใช้เห็น — ต้องยังถูกฐานปฏิเสธ (FK ไม่ได้ถูกถอดเพื่อให้ผ่าน)
        db.FileAttachments.Add(new FileAttachment
        {
            CompanyId = company.Id, FileName = "x.png", OriginalFileName = "x.png", ContentType = "image/png",
            StoragePath = "/tmp/x.png", EntityType = "LodgingReservation", EntityId = Guid.NewGuid(),
            UploadedByUserId = Guid.Empty, CreatedBy = "db-test",
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}

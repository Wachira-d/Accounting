using System.Text;
using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs.FileAttachment;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Background;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Handlers;
using Accounting.Services.Payments.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 218 (คำตัดสินข้อ 177 · TEST_PLAN BKPAY-20..26) — นโยบายชำระล่วงหน้า + สลิปของการจองนัดหมาย บนฐานจริง:
/// <list type="bullet">
///   <item>มัดจำบังคับของเว็บ ⇒ ลูกค้าจองเอง: ยอด 30% · รอยืนยัน (ชนะ AutoConfirm) · ถือรอบถึงกำหนดชำระ · คำตอบมีเลขบัญชี/ยอดค้าง</item>
///   <item>ทิศตรงข้าม: พนักงานจองแทน ⇒ ไม่บังคับรอยืนยัน/ไม่ถือ · เว็บที่ไม่เคยตั้ง (แบบเดิม) ⇒ พฤติกรรมเดิม</item>
///   <item>สลิป: แนบด้วยโทเคน ⇒ PaymentIntent manual-slip ยอดจากเซิร์ฟเวอร์ · โทเคน/บริษัทอื่นเปิดไม่ได้ · ยืนยัน ⇒ handler บันทึกรับเงิน + ยืนยันการจอง ·
///     ปฏิเสธ ⇒ intent Failed + เหตุผล + ต่อเวลา</item>
///   <item>พ้นกำหนดชำระ ⇒ job ยกเลิก · มีสลิปรอตรวจ ⇒ ไม่ยกเลิก (ทิศตรงข้าม)</item>
///   <item>บันทึกค่าตั้งโหมดบังคับโดยไม่มีช่องทาง ⇒ ปฏิเสธ · บริการชนิดอื่นตั้งเก็บเงินเอง ⇒ ปฏิเสธ</item>
/// </list>
/// ไม่มีฐาน (ไม่ตั้ง <c>ACCOUNTING_TEST_PG</c>) ⇒ ข้ามพร้อมข้อความ — หลักฐานคือ job <c>db-test</c>
/// </summary>
[Trait("Category", "Db")]
public class BookingPaymentPolicyDbTests
{
    private readonly ITestOutputHelper _out;
    public BookingPaymentPolicyDbTests(ITestOutputHelper output) => _out = output;

    private sealed class MemFiles : IFileAttachmentService
    {
        private readonly Dictionary<Guid, (Guid CompanyId, FileAttachmentResponse Meta, byte[] Bytes)> _files = new();
        public Task<FileAttachmentResponse> UploadAsync(Guid companyId, string entityType, Guid entityId, string fileName, string originalFileName,
            string contentType, long fileSize, string storagePath, Guid uploadedByUserId) => throw new NotSupportedException();
        public Task<FileAttachmentResponse> UploadBytesAsync(Guid companyId, string entityType, Guid entityId, string originalFileName,
            string contentType, byte[] content, Guid uploadedByUserId) => throw new NotSupportedException();
        public Task<FileAttachmentResponse> UploadAnonymousBytesAsync(Guid companyId, string entityType, Guid entityId, string originalFileName,
            string contentType, byte[] content)
        {
            var meta = new FileAttachmentResponse(Guid.NewGuid(), originalFileName, originalFileName, contentType, content.LongLength, "mem",
                null, entityType, entityId, "guest", DateTime.UtcNow);
            _files[meta.Id] = (companyId, meta, content);
            return Task.FromResult(meta);
        }
        public Task<List<FileAttachmentResponse>> GetByEntityAsync(Guid companyId, string entityType, Guid entityId)
            => Task.FromResult(_files.Values.Where(f => f.CompanyId == companyId && f.Meta.EntityId == entityId).Select(f => f.Meta).ToList());
        public Task<FileAttachmentResponse?> GetByIdAsync(Guid companyId, Guid attachmentId)
            => Task.FromResult(_files.TryGetValue(attachmentId, out var f) && f.CompanyId == companyId ? f.Meta : null);
        public Task<byte[]?> ReadBytesAsync(Guid companyId, Guid attachmentId, CancellationToken ct = default)
            => Task.FromResult(_files.TryGetValue(attachmentId, out var f) && f.CompanyId == companyId ? f.Bytes : null);
        public Task DeleteAsync(Guid companyId, Guid attachmentId, bool keepPhysicalFile = false) => Task.CompletedTask;
    }

    private static readonly MemFiles Files = new();

    private static DateOnly TargetDate() => DateOnly.FromDateTime(BookingSlotEngine.BangkokNow(DateTime.UtcNow)).AddDays(10);

    private static CmsBookingService Svc(Accounting.Data.AccountingDbContext db) => new(db, NullLogger<CmsBookingService>.Instance);

    private static (BookingSlipService Slips, CmsBookingService Bookings) Slips(Accounting.Data.AccountingDbContext db)
    {
        var bookings = Svc(db);
        var providers = new IPaymentProvider[] { new ManualSlipPaymentProvider() };
        var handler = new SiteBookingPaymentHandler(db, bookings, new GatewayAccountResolver(db, providers, NullLogger<GatewayAccountResolver>.Instance),
            NullLogger<SiteBookingPaymentHandler>.Instance);
        var intents = new PaymentIntentService(db, providers, new IPaymentCompletionHandler[] { handler }, NullLogger<PaymentIntentService>.Instance);
        return (new BookingSlipService(db, bookings, intents, Files, NullLogger<BookingSlipService>.Instance), bookings);
    }

    private static IFormFile PdfSlip()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n% slip test\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "slip.pdf") { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
    }

    private static async Task<(Guid CompanyId, Guid SiteId, Guid ServiceId)> SeedAsync(Accounting.Data.AccountingDbContext db,
        BookingPaymentMode? siteMode, bool withBank = true, BookingType type = BookingType.PrePayment, decimal? serviceDeposit = null)
    {
        var company = new Company { Name = "คลินิกทดสอบมัดจำ", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = company.Id, Name = "คลินิกบ้านสวน", Slug = "cl-" + tag, Subdomain = "cl-" + tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        var svc = new SiteBookingService
        {
            CompanyId = company.Id, SiteId = site.Id, Name = "ทำฟัน", Slug = "dent-" + tag,
            SlotMode = BookingSlotMode.Interval, DurationMinutes = 60, SlotIntervalMinutes = 60, MaxCapacity = 5,
            MinAdvanceBookingHours = 0, MaxAdvanceBookingDays = 30, BookingType = type, DepositAmount = serviceDeposit,
            AutoConfirm = true, IsActive = true, Price = 500m,
        };
        db.SiteBookingServices.Add(svc);
        db.SiteBookingSlots.Add(new SiteBookingSlot
        {
            CompanyId = company.Id, BookingServiceId = svc.Id, DayOfWeek = TargetDate().DayOfWeek,
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0), MaxCapacity = 5, IsActive = true,
        });
        db.SiteBookingSettings.Add(new SiteBookingSettings
        {
            CompanyId = company.Id, SiteId = site.Id, PaymentMode = siteMode, PaymentDepositPercent = 30m, PaymentDeadlineMinutes = 30,
        });
        if (withBank)
            db.SitePaymentGateways.Add(new SitePaymentGateway
            {
                CompanyId = company.Id, SiteId = site.Id, GatewayType = PaymentGatewayType.BankTransfer, Name = "กสิกรไทย",
                BankName = "กสิกรไทย", BankAccountNumber = "1234567890", BankAccountName = "คลินิกบ้านสวน", BankBranch = "สยาม", IsTestMode = false,
            });
        await db.SaveChangesAsync();
        return (company.Id, site.Id, svc.Id);
    }

    private static CreateBookingRequest Req(Guid serviceId, string start = "10:00") => new()
    {
        BookingServiceId = serviceId, BookingDate = TargetDate().ToDateTime(TimeOnly.MinValue), StartTime = start,
        GuestName = "คุณลูกค้า", GuestEmail = "guest@example.test", GuestPhone = "0812345678", GuestCount = 1,
    };

    [Fact]
    public async Task มัดจำบังคับของเว็บ_ลูกค้าจองเอง_รอยืนยัน_ถือรอบ_มีเลขบัญชี()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var before = DateTime.UtcNow;

        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId), userId: null);

        Assert.Equal(BookingStatus.Pending, created.Status);   // AutoConfirm = true แต่โหมดบังคับชนะ
        Assert.Equal(150m, created.DepositAmount);
        var row = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id && b.CompanyId == cid);
        Assert.NotNull(row.PaymentHoldUntil);
        Assert.InRange(row.PaymentHoldUntil!.Value, before.AddMinutes(29), DateTime.UtcNow.AddMinutes(31));
        var view = await Svc(db).GetBookingByTokenAsync(cid, sid, created.PublicToken!);
        Assert.Equal(150m, view!.Payment!.AmountDue);
        Assert.True(view.Payment.TransferAvailable);
        Assert.Equal("1234567890", view.Payment.Transfer!.AccountNumber);
        Assert.True(view.Payment.CanUploadSlip);
        Assert.Null(view.Payment.SlipIntentId);   // ข้อมูลพนักงานไม่ถึงลูกค้า
        Assert.StartsWith("รอชำระ ฿150.00", view.Payment.StatusLabel);
    }

    [Fact]
    public async Task ทิศตรงข้าม_พนักงานจองแทน_ไม่บังคับรอยืนยัน_และเว็บที่ไม่เคยตั้ง_พฤติกรรมเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var staff = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "11:00"), userId: null, staffOnBehalf: true);
        Assert.Equal(BookingStatus.Confirmed, staff.Status);
        Assert.Null((await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == staff.Id)).PaymentHoldUntil);

        var (cid2, sid2, svcId2) = await SeedAsync(db, siteMode: null, serviceDeposit: 100m);
        var legacy = await Svc(db).CreateBookingAsync(cid2, sid2, Req(svcId2), userId: null);
        Assert.Equal(BookingStatus.Confirmed, legacy.Status);   // แบบเดิม: AutoConfirm ตามเดิม
        Assert.Equal(100m, legacy.DepositAmount);               // สูตรเดิม (ช่องมัดจำของบริการ)
        Assert.Null((await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == legacy.Id)).PaymentHoldUntil);
    }

    [Fact]
    public async Task สลิป_แนบด้วยโทเคน_บริษัทอื่นเปิดไม่ได้_ยืนยันแล้วรับเงินและยืนยันการจอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var (otherCid, otherSid, _) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId), userId: null);
        var (slips, _) = Slips(db);

        // โทเคนผิด/บริษัทอื่น ⇒ ไม่พบ (ไม่เขียนไฟล์ ไม่สร้างรายการชำระ)
        Assert.Null(await slips.UploadByTokenAsync(otherCid, otherSid, created.PublicToken!, PdfSlip()));
        Assert.Null(await slips.UploadByTokenAsync(cid, sid, "not-a-token", PdfSlip()));

        var afterUpload = await slips.UploadByTokenAsync(cid, sid, created.PublicToken!, PdfSlip());
        Assert.True(afterUpload!.Payment!.SlipPending);
        Assert.Equal("รอตรวจสลิป", afterUpload.Payment.StatusLabel);
        Assert.False(afterUpload.Payment.GatewayAvailable);   // สลิปรอตรวจ ⇒ ปิดจ่ายออนไลน์ชั่วคราว
        var row = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id);
        var intent = await db.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == row.SlipIntentId && i.CompanyId == cid);
        Assert.Equal(ManualSlipPaymentProvider.Code, intent.ProviderCode);
        Assert.Equal(150m, intent.Amount);   // ยอดจากเซิร์ฟเวอร์
        Assert.Equal(PaymentSourceKind.SiteBooking, intent.SourceKind);

        // เปิดสลิป: โทเคนของตัวเองได้ · บริษัทอื่นที่ถือโทเคนเดียวกันไม่ได้
        Assert.NotNull(await slips.ReadSlipByTokenAsync(cid, sid, created.PublicToken!));
        Assert.Null(await slips.ReadSlipByTokenAsync(otherCid, otherSid, created.PublicToken!));
        // แนบซ้ำขณะรอตรวจ ⇒ ปฏิเสธ
        await Assert.ThrowsAsync<BusinessRuleException>(() => slips.UploadByTokenAsync(cid, sid, created.PublicToken!, PdfSlip()));
        // บริษัทอื่นยืนยันสลิปของการจองนี้ไม่ได้
        await Assert.ThrowsAsync<KeyNotFoundException>(() => slips.VerifyAsync(otherCid, otherSid, created.Id, new VerifyBookingSlipRequest(), "staff-x"));

        var verified = await slips.VerifyAsync(cid, sid, created.Id, new VerifyBookingSlipRequest { Note = "ตรวจยอดแล้ว" }, "staff-1");
        Assert.Equal(BookingStatus.Confirmed, verified.Status);
        Assert.Equal(150m, verified.PaidAmount);
        Assert.False(verified.Payment!.SlipPending);
        Assert.Empty(verified.ErpNotices.Where(n => n.Contains("ส่งต่อใหม่")));
        var after = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id);
        Assert.Null(after.PaymentHoldUntil);
        Assert.Equal(PaymentIntentStatus.Succeeded, (await db.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == intent.Id)).Status);
        // ยืนยันซ้ำ ⇒ ไม่มีสลิปรอตรวจ (ไม่รับเงินซ้ำ)
        await Assert.ThrowsAsync<BusinessRuleException>(() => slips.VerifyAsync(cid, sid, created.Id, new VerifyBookingSlipRequest(), "staff-1"));
        Assert.Equal(150m, (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id)).PaidAmount);
    }

    [Fact]
    public async Task สลิป_ปฏิเสธต้องมีเหตุผล_intentFailed_ต่อเวลา_แนบใหม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId), userId: null);
        var (slips, _) = Slips(db);
        await slips.UploadByTokenAsync(cid, sid, created.PublicToken!, PdfSlip());
        var intentId = (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id)).SlipIntentId!.Value;

        await Assert.ThrowsAsync<BusinessRuleException>(() => slips.RejectAsync(cid, sid, created.Id, new RejectBookingSlipRequest { Reason = "  " }, "staff-1"));
        var rejected = await slips.RejectAsync(cid, sid, created.Id, new RejectBookingSlipRequest { Reason = "ยอดไม่ตรง" }, "staff-1");

        Assert.False(rejected.Payment!.SlipPending);
        Assert.Equal("ยอดไม่ตรง", rejected.Payment.SlipRejectedReason);
        Assert.Equal(PaymentIntentStatus.Failed, (await db.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == intentId)).Status);
        var row = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id);
        Assert.True(row.PaymentHoldUntil > DateTime.UtcNow.AddHours(23));   // ต่อเวลาให้ส่งใหม่ 24 ชม.
        Assert.Equal(0m, row.PaidAmount);
        var again = await slips.UploadByTokenAsync(cid, sid, created.PublicToken!, PdfSlip());
        Assert.True(again!.Payment!.SlipPending);
        Assert.Null(again.Payment.SlipRejectedReason);
    }

    [Fact]
    public async Task พ้นกำหนดชำระ_jobยกเลิก_แต่ใบที่มีสลิปรอตรวจไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var unpaid = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "10:00"), userId: null);
        var withSlip = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "11:00"), userId: null);
        var (slips, _) = Slips(db);
        await slips.UploadByTokenAsync(cid, sid, withSlip.PublicToken!, PdfSlip());

        var job = new BookingReminderJob(null!, NullLogger<BookingReminderJob>.Instance);
        // ยังไม่ถึงเวลา ⇒ ไม่แตะ
        await job.ProcessAsync(db, null, Svc(db), DateTime.UtcNow, CancellationToken.None, onlyCompanyId: cid);
        Assert.Equal(BookingStatus.Pending, (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == unpaid.Id)).Status);
        // พ้นกำหนด 30 นาที
        await job.ProcessAsync(db, null, Svc(db), DateTime.UtcNow.AddMinutes(45), CancellationToken.None, onlyCompanyId: cid);
        var u = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == unpaid.Id);
        Assert.Equal(BookingStatus.Cancelled, u.Status);
        Assert.Equal(BookingPaymentPolicy.HoldExpiredReason, u.CancellationReason);
        Assert.Equal(BookingStatus.Pending, (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == withSlip.Id)).Status);
    }

    [Fact]
    public async Task บันทึกค่าตั้ง_โหมดบังคับไม่มีช่องทาง_ปฏิเสธ_บริการชนิดอื่นตั้งเก็บเงินเอง_ปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, siteMode: null, withBank: false);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).UpdatePaymentSettingsAsync(cid, sid,
            new BookingPaymentSettingsDto { Mode = "FullRequired", BankTransferEnabled = true }, "owner"));
        Assert.Contains("ช่องทาง", ex.Message);
        Assert.Null((await db.SiteBookingSettings.AsNoTracking().SingleAsync(s => s.SiteId == sid && s.CompanyId == cid)).PaymentMode);

        // ทิศตรงข้าม: ใส่บัญชีพร้อมกัน ⇒ ผ่าน + echo กลับครบ
        var ok = await Svc(db).UpdatePaymentSettingsAsync(cid, sid, new BookingPaymentSettingsDto
        {
            Mode = "FullRequired", BankTransferEnabled = true, UpdateTransfer = true,
            BankName = "กรุงเทพ", BankAccountNumber = "123-4-56789-0", BankAccountName = "คลินิก", BankBranch = "สีลม",
        }, "owner");
        Assert.Equal("FullRequired", ok.Mode);
        Assert.Equal("1234567890", ok.BankAccountNumber);
        Assert.Equal("สีลม", ok.BankBranch);
        Assert.True(ok.TransferReady);

        var svc = await db.SiteBookingServices.AsNoTracking().SingleAsync(s => s.Id == svcId);
        var req = new UpdateBookingServiceRequest
        {
            Name = svc.Name, Price = svc.Price, BookingType = BookingType.Appointment, DurationMinutes = 60, MaxCapacity = 5,
            SlotMode = BookingSlotMode.Interval, SlotIntervalMinutes = 60, MinAdvanceBookingHours = 0, MaxAdvanceBookingDays = 30,
            PaymentMode = "DepositRequired",
        };
        var svcEx = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).UpdateServiceAsync(cid, sid, svcId, req, "owner"));
        Assert.Contains("ชำระมัดจำล่วงหน้า", svcEx.Message);
    }
}

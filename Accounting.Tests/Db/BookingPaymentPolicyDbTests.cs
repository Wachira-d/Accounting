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
/// รอบ 218 (คำตัดสินข้อ 177 · TEST_PLAN BKPAY-20..23) — นโยบายชำระล่วงหน้า + สลิปของการจองนัดหมาย บนฐานจริง:
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

    /// <summary>ผู้ให้บริการ DI จิ๋ว — CmsBookingService resolve IPaymentIntentService ตอนใช้ (ฝ่ายค้านรอบ 218 S1 · กันวงกลม DI)</summary>
    private sealed class LateProvider : IServiceProvider
    {
        public IPaymentIntentService? Intents { get; set; }
        public object? GetService(Type serviceType) => serviceType == typeof(IPaymentIntentService) ? Intents : null;
    }

    private static (BookingSlipService Slips, CmsBookingService Bookings) Slips(Accounting.Data.AccountingDbContext db)
    {
        var sp = new LateProvider();
        var bookings = new CmsBookingService(db, NullLogger<CmsBookingService>.Instance, services: sp);
        var providers = new IPaymentProvider[] { new ManualSlipPaymentProvider() };
        var handler = new SiteBookingPaymentHandler(db, bookings, new GatewayAccountResolver(db, providers, NullLogger<GatewayAccountResolver>.Instance),
            NullLogger<SiteBookingPaymentHandler>.Instance);
        var intents = new PaymentIntentService(db, providers, new IPaymentCompletionHandler[] { handler }, NullLogger<PaymentIntentService>.Instance);
        sp.Intents = intents;
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
        PolicyAccepted = true,   // รอบ 219 ส่วน B
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

    // ─────────────── ฝ่ายค้านรอบ 218 ───────────────

    [Fact]
    public async Task B1_แถวOmiseมาก่อน_บันทึกค่าตั้งการจองไม่แตะแถวOmise_และอ่านเฉพาะแถวโอน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, _) = await SeedAsync(db, siteMode: null, withBank: false);
        var omise = new SitePaymentGateway
        {
            CompanyId = cid, SiteId = sid, GatewayType = PaymentGatewayType.Omise, Name = "Omise", SortOrder = 0, IsTestMode = true,
            MerchantId = "m_1", WebhookSecret = "whsec",
        };
        var bank = new SitePaymentGateway
        {
            CompanyId = cid, SiteId = sid, GatewayType = PaymentGatewayType.BankTransfer, Name = "กสิกรไทย", SortOrder = 1, IsTestMode = false,
            BankName = "กสิกรไทย", BankAccountNumber = "1234567890", BankAccountName = "คลินิก",
        };
        db.SitePaymentGateways.AddRange(omise, bank);
        await db.SaveChangesAsync();

        var got = await Svc(db).GetPaymentSettingsAsync(cid, sid);
        Assert.Equal("1234567890", got.BankAccountNumber);   // ข้ามแถว Omise ที่มาก่อน

        await Svc(db).UpdatePaymentSettingsAsync(cid, sid, new BookingPaymentSettingsDto
        {
            Mode = "DepositRequired", DepositPercent = 30, BankTransferEnabled = true, UpdateTransfer = true,
            BankName = "กสิกรไทย", BankAccountNumber = "999-9-99999-9", BankAccountName = "คลินิก", BankBranch = "สยาม",
        }, "owner");

        using var check = DbTestDatabase.TryCreateContext()!;
        var o = await check.SitePaymentGateways.AsNoTracking().SingleAsync(g => g.Id == omise.Id);
        Assert.Equal(PaymentGatewayType.Omise, o.GatewayType);
        Assert.Equal("Omise", o.Name);
        Assert.Equal("m_1", o.MerchantId);
        Assert.Null(o.BankAccountNumber);
        // webhook ของคำสั่งซื้อหาแถวด้วยชนิด — ยังเจอ
        Assert.True(await check.SitePaymentGateways.AnyAsync(g => g.CompanyId == cid && g.SiteId == sid && g.GatewayType == PaymentGatewayType.Omise && g.IsActive));
        var b = await check.SitePaymentGateways.AsNoTracking().SingleAsync(g => g.Id == bank.Id);
        Assert.Equal("9999999999", b.BankAccountNumber);
        Assert.Equal(PaymentGatewayType.BankTransfer, b.GatewayType);
        Assert.Equal("กสิกรไทย", b.Name);   // ไม่เปลี่ยนชื่อแถวเดิม
    }

    [Fact]
    public async Task B1_ล้างเลขบัญชีเดิมต้องยืนยัน_ยืนยันแล้วล้างได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, _) = await SeedAsync(db, siteMode: null);
        var req = new BookingPaymentSettingsDto { BankTransferEnabled = true, UpdateTransfer = true, PromptPayId = "0812345678" };
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).UpdatePaymentSettingsAsync(cid, sid, req, "owner"));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("เลขบัญชี", ex.Message);
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal("1234567890", (await c1.SitePaymentGateways.AsNoTracking().SingleAsync(g => g.SiteId == sid && g.CompanyId == cid)).BankAccountNumber);
        req.ConfirmClearTransfer = true;
        using var db2 = DbTestDatabase.TryCreateContext()!;
        var ok = await Svc(db2).UpdatePaymentSettingsAsync(cid, sid, req, "owner");
        Assert.Null(ok.BankAccountNumber);
        Assert.Equal("0812345678", ok.PromptPayId);
    }

    [Fact]
    public async Task B2_แนบสลิปเมื่อสองวันก่อน_ยืนยันวันนี้_วันรับเงินเป็นวันที่โอน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId), userId: null);
        var (slips, _) = Slips(db);
        await slips.UploadByTokenAsync(cid, sid, created.PublicToken!, PdfSlip());
        var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"SiteBookings\" SET \"CreatedAt\" = {0}, \"SlipUploadedAt\" = {1} WHERE \"Id\" = {2}",
            twoDaysAgo.AddHours(-1), twoDaysAgo, created.Id);
        db.ChangeTracker.Clear();

        await slips.VerifyAsync(cid, sid, created.Id, new VerifyBookingSlipRequest(), "staff-1");

        var row = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id);
        Assert.Equal(DateOnly.FromDateTime(twoDaysAgo.AddHours(7)), DateOnly.FromDateTime(row.PaidAt!.Value.AddHours(7)));   // ไม่ใช่วันกดยืนยัน
        var pay = await db.SiteBookingPayments.AsNoTracking().SingleAsync(p => p.BookingId == created.Id && p.CompanyId == cid);
        Assert.Equal(PaymentMethod.BankTransfer, pay.PaymentMethod);   // โอนเงิน ไม่ใช่ "ออนไลน์"
        var intent = await db.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == row.SlipIntentId);
        Assert.Equal("manual:staff-1", intent.ConfirmedBy);           // S3: ไม่มีบันทึกอิสระใน ConfirmedBy
        Assert.NotNull(intent.PaidAt);
    }

    [Fact]
    public async Task B2_วันที่โอนอนาคต_ปฏิเสธ_ไม่รับเงิน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId), userId: null);
        var (slips, _) = Slips(db);
        await slips.UploadByTokenAsync(cid, sid, created.PublicToken!, PdfSlip());
        await Assert.ThrowsAsync<BusinessRuleException>(() => slips.VerifyAsync(cid, sid, created.Id,
            new VerifyBookingSlipRequest { TransferDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(3) }, "staff-1"));
        Assert.Equal(0m, (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id)).PaidAmount);
    }

    [Fact]
    public async Task S1_พนักงานบันทึกรับเงินครบระหว่างสลิปรอตรวจ_ปิดรายการของสลิป_ยืนยันสลิปไม่ได้อีก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId), userId: null);
        var (slips, bookings) = Slips(db);
        await slips.UploadByTokenAsync(cid, sid, created.PublicToken!, PdfSlip());
        var intentId = (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id)).SlipIntentId!.Value;

        await bookings.RecordDepositPaymentAsync(cid, sid, created.Id, new RecordBookingPaymentRequest(150m, DateTime.UtcNow, "เงินสดหน้าร้าน", null, null), "staff-1");

        Assert.Equal(PaymentIntentStatus.Failed, (await db.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == intentId)).Status);
        await Assert.ThrowsAsync<BusinessRuleException>(() => slips.VerifyAsync(cid, sid, created.Id, new VerifyBookingSlipRequest(), "staff-1"));
        Assert.Equal(150m, (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id)).PaidAmount);   // ไม่รับซ้ำ
    }

    [Fact]
    public async Task S2_ยกเลิกขณะสลิปรอตรวจ_ปิดสลิป_ยืนยันไม่ได้_ทิศตรงข้ามใบที่ไม่ยกเลิกยืนยันได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var cancelled = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "10:00"), userId: null);
        var kept = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "11:00") , userId: null, staffOnBehalf: false);
        var (slips, bookings) = Slips(db);
        await slips.UploadByTokenAsync(cid, sid, cancelled.PublicToken!, PdfSlip());
        await slips.UploadByTokenAsync(cid, sid, kept.PublicToken!, PdfSlip());
        var intentId = (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == cancelled.Id)).SlipIntentId!.Value;

        await bookings.UpdateBookingStatusAsync(cid, sid, cancelled.Id, new UpdateBookingStatusRequest { Status = BookingStatus.Cancelled, CancellationReason = "ลูกค้าแจ้ง" }, "staff-1");

        var row = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == cancelled.Id);
        Assert.Null(row.SlipUploadedAt);
        Assert.Equal(PaymentIntentStatus.Failed, (await db.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == intentId)).Status);
        await Assert.ThrowsAsync<BusinessRuleException>(() => slips.VerifyAsync(cid, sid, cancelled.Id, new VerifyBookingSlipRequest(), "staff-1"));
        var ok = await slips.VerifyAsync(cid, sid, kept.Id, new VerifyBookingSlipRequest(), "staff-1");
        Assert.Equal(BookingStatus.Confirmed, ok.Status);
    }

    // ─────────────── ฝ่ายค้านรอบ 218 รอบ 2 ───────────────

    /// <summary>ผู้จองอีกคน (ด่านกดซ้ำรอบ 217 ไม่รวมเป็นใบเดิม)</summary>
    private static CreateBookingRequest Other(CreateBookingRequest r, int n)
    {
        r.GuestName = $"ลูกค้าคิว {n}"; r.GuestEmail = $"wait{n}@example.test"; r.GuestPhone = $"08900000{n:D2}";
        return r;
    }

    private static async Task MakeWaitlistOneSeatAsync(Accounting.Data.AccountingDbContext db, Guid svcId)
    {
        var svc = await db.SiteBookingServices.Include(x => x.Slots).SingleAsync(x => x.Id == svcId);
        svc.MaxCapacity = 1; svc.WaitlistEnabled = true;
        foreach (var sl in svc.Slots) sl.MaxCapacity = 1;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task F3_คิวสำรองที่พนักงานจองแทน_ได้ที่แล้วไม่ถูกบังคับรอชำระ_ทิศตรงข้ามลูกค้าจองเองถูกบังคับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        await MakeWaitlistOneSeatAsync(db, svcId);
        var staff = new User { Email = $"bkpay-staff-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "พนักงานหน้าร้าน" };
        db.Users.Add(staff);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = staff.Id, CompanyId = cid, Role = UserRole.Staff });
        await db.SaveChangesAsync();

        var firstA = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "10:00"), userId: null);
        var firstB = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "11:00"), userId: null);
        var waitCustomer = await Svc(db).CreateBookingAsync(cid, sid, Other(Req(svcId, "10:00"), 2), userId: null);
        var waitStaff = await Svc(db).CreateBookingAsync(cid, sid, Other(Req(svcId, "11:00"), 3), userId: staff.Id.ToString(), staffOnBehalf: true);
        Assert.Equal(BookingStatus.Waitlisted, waitCustomer.Status);
        Assert.Equal(BookingStatus.Waitlisted, waitStaff.Status);
        // ฟิกซ์เจอร์: เทสต์นี้ล็อก "ตัวตัดสินการชำระตอนได้ที่" ไม่ใช่เส้นเอกสาร ERP — ฐานเทสต์ไม่มี IDocumentService ⇒ ผูกใบคิวกับเอกสารตั้งต้น
        // (SyncBookingToErpAsync คืนทันทีเมื่อมี ErpDocumentId · production: ได้ที่แล้วออกเอกสารตามเดิม — เส้นนั้นไม่เปลี่ยนในรอบนี้)
        var contact = new Contact { CompanyId = cid, Name = "ลูกค้าคิว", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        foreach (var wid in new[] { waitCustomer.Id, waitStaff.Id })
        {
            var doc = new Document
            {
                CompanyId = cid, ContactId = contact.Id, DocumentType = DocumentType.Quotation, DocumentNumber = "QT-" + Guid.NewGuid().ToString("N")[..8],
                Status = DocumentStatus.Draft, DocumentDate = DateTime.UtcNow.Date, SubTotal = 150m, TotalAmount = 150m, BalanceDue = 150m, Currency = "THB",
            };
            db.Documents.Add(doc);
            await db.SaveChangesAsync();
            var row = await db.SiteBookings.SingleAsync(b => b.Id == wid && b.CompanyId == cid);
            row.ErpDocumentId = doc.Id;
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();

        await Svc(db).UpdateBookingStatusAsync(cid, sid, firstA.Id, new UpdateBookingStatusRequest { Status = BookingStatus.Cancelled, CancellationReason = "เทสต์" }, "staff-1");
        await Svc(db).UpdateBookingStatusAsync(cid, sid, firstB.Id, new UpdateBookingStatusRequest { Status = BookingStatus.Cancelled, CancellationReason = "เทสต์" }, "staff-1");

        var s = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == waitStaff.Id);
        Assert.Equal(BookingStatus.Confirmed, s.Status);   // พนักงานจองแทน ⇒ ไม่บังคับรอชำระ (เหมือนจองใหม่)
        Assert.Null(s.PaymentHoldUntil);
        var c = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == waitCustomer.Id);
        Assert.Equal(BookingStatus.Pending, c.Status);     // ทิศตรงข้าม: ลูกค้าจองเอง ⇒ รอชำระ + กำหนดเวลา
        Assert.NotNull(c.PaymentHoldUntil);
    }

    [Fact]
    public async Task F4_วันที่โอนอยู่ในงวดปิด_ปฏิเสธก่อนรับเงิน_รายการชำระยังเปิด_เลือกวันในงวดเปิดผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId), userId: null);
        var (slips, _) = Slips(db);
        await slips.UploadByTokenAsync(cid, sid, created.PublicToken!, PdfSlip());
        var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"SiteBookings\" SET \"CreatedAt\" = {0}, \"SlipUploadedAt\" = {1} WHERE \"Id\" = {2}",
            twoDaysAgo.AddHours(-1), twoDaysAgo, created.Id);
        var closedDay = DateTime.SpecifyKind(twoDaysAgo.AddHours(7).Date, DateTimeKind.Utc);
        db.FiscalPeriods.Add(new FiscalPeriod
        {
            CompanyId = cid, Name = "งวดปิดทดสอบ", Year = closedDay.Year, Month = closedDay.Month,
            StartDate = closedDay, EndDate = closedDay, Status = FiscalPeriodStatus.Closed,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => slips.VerifyAsync(cid, sid, created.Id,
            new VerifyBookingSlipRequest { TransferDate = DateOnly.FromDateTime(closedDay) }, "staff-1"));
        Assert.Contains("งวดปิดทดสอบ", ex.Message);
        var row = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id);
        Assert.Equal(0m, row.PaidAmount);
        Assert.NotNull(row.SlipUploadedAt);                                     // ยังรอตรวจ — แก้วันแล้วยืนยันใหม่ได้
        Assert.Equal(PaymentIntentStatus.Pending, (await db.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == row.SlipIntentId)).Status);
        Assert.DoesNotContain("ยืนยันสลิปโดย", row.InternalNotes ?? "");      // หมายเหตุเขียนหลังรับเงินสำเร็จเท่านั้น

        db.ChangeTracker.Clear();
        var ok = await slips.VerifyAsync(cid, sid, created.Id,
            new VerifyBookingSlipRequest { TransferDate = DateOnly.FromDateTime(closedDay).AddDays(1) }, "staff-1");   // ทิศตรงข้าม: งวดเปิด
        Assert.Equal(BookingStatus.Confirmed, ok.Status);
    }

    [Fact]
    public async Task F5_รับเงินสดครบระหว่างสลิปรอตรวจ_หมายเหตุเตือนจ่ายซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId), userId: null);
        var (slips, bookings) = Slips(db);
        await slips.UploadByTokenAsync(cid, sid, created.PublicToken!, PdfSlip());
        await bookings.RecordDepositPaymentAsync(cid, sid, created.Id, new RecordBookingPaymentRequest(150m, DateTime.UtcNow, "เงินสดหน้าร้าน", null, null), "staff-1");
        Assert.Contains("จ่ายซ้ำ", (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id)).InternalNotes ?? "");
    }

    [Fact]
    public async Task F6_ปิดรายการของสลิปไม่ได้_หมายเหตุบนการจองบอกพนักงาน_ไม่เงียบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId), userId: null);
        var (slips, _) = Slips(db);
        await slips.UploadByTokenAsync(cid, sid, created.PublicToken!, PdfSlip());
        // booking service ที่ไม่มีตัว resolve IPaymentIntentService (สาย DI ขาด) ⇒ ต้องบอกบนการจอง
        await Svc(db).UpdateBookingStatusAsync(cid, sid, created.Id, new UpdateBookingStatusRequest { Status = BookingStatus.Cancelled, CancellationReason = "ลูกค้าแจ้ง" }, "staff-1");
        Assert.Contains("รายการรับชำระเงินออนไลน์", (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id)).InternalNotes ?? "");
    }

    private static FileAttachment OldFile(Guid cid, string entityType, Guid entityId, DateTime createdAt, bool deleted) => new()
    {
        CompanyId = cid, FileName = "f", OriginalFileName = "f.pdf", ContentType = "application/pdf", FileSize = 1, StoragePath = "mem",
        EntityType = entityType, EntityId = entityId, CreatedAt = createdAt, IsDeleted = deleted,
    };

    [Fact]
    public async Task F1_งานล้าง_แถวที่ลบแล้วเกิน200_ไม่ค้าง_ได้ไฟล์ที่ครบอายุ_สลิปรอตรวจไม่ถูกหยิบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(db, BookingPaymentMode.DepositRequired);
        var pendingBooking = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId), userId: null);
        var veryOld = DateTime.UtcNow.AddYears(-12);
        for (var i = 0; i < 205; i++)   // เกิน BatchSize — แถวที่ลบแล้วต้องไม่ถูกหยิบซ้ำทุกรอบ
        {
            db.FileAttachments.Add(OldFile(cid, BookingSlipRule.AttachmentEntityType, Guid.NewGuid(), veryOld.AddMinutes(-i), deleted: true));
            db.FileAttachments.Add(OldFile(cid, CmsMediaUrl.OrderEntityType, Guid.NewGuid(), veryOld.AddMinutes(-i), deleted: true));
        }
        var pendingSlip = OldFile(cid, BookingSlipRule.AttachmentEntityType, pendingBooking.Id, veryOld.AddYears(-1), deleted: false);
        var purgeSlip = OldFile(cid, BookingSlipRule.AttachmentEntityType, Guid.NewGuid(), veryOld, deleted: false);
        var youngSlip = OldFile(cid, BookingSlipRule.AttachmentEntityType, Guid.NewGuid(), DateTime.UtcNow.AddYears(-5), deleted: false);
        var purgeCart = OldFile(cid, CmsMediaUrl.OrderEntityType, Guid.NewGuid(), veryOld, deleted: false);
        db.FileAttachments.AddRange(pendingSlip, purgeSlip, youngSlip, purgeCart);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE \"SiteBookings\" SET \"SlipAttachmentId\" = {0}, \"SlipUploadedAt\" = {1} WHERE \"Id\" = {2}",
            pendingSlip.Id, DateTime.UtcNow, pendingBooking.Id);
        db.ChangeTracker.Clear();

        var slipIds = await Accounting.Services.Implementations.Jobs.CartImageCleanupJob
            .SlipPurgeCandidates(db, BookingSlipRetention.Cutoff(DateTime.UtcNow))
            .Where(f => f.CompanyId == cid).Take(Accounting.Services.Implementations.Jobs.CartImageCleanupJob.BatchSize).Select(f => f.Id).ToListAsync();
        Assert.Equal(new[] { purgeSlip.Id }, slipIds);   // ไม่มีแถวลบแล้ว · ไม่มีสลิปรอตรวจ · ไม่มีไฟล์อายุยังไม่ครบ
        var cartIds = await Accounting.Services.Implementations.Jobs.CartImageCleanupJob
            .CartImageCandidates(db, DateTime.UtcNow.AddDays(-14))
            .Where(f => f.CompanyId == cid).Take(Accounting.Services.Implementations.Jobs.CartImageCleanupJob.BatchSize).Select(f => f.Id).ToListAsync();
        Assert.Equal(new[] { purgeCart.Id }, cartIds);
    }

    // ═══════════════ รอบ 226 (BOOKING_PAYMENT_AUDIT §6 · TEST_PLAN BKPAY-60/61/62) ═══════════════

    /// <summary>ค่าตั้งตรงตามภาพของเจ้าของ: เว็บ "มัดจำ (บังคับ)" 50% · ต้องชำระภายใน ว่าง · โอน+สลิป กสิกรไทย 1111111111 ·
    /// บริการ "Sourdough สังขยา" ฿35 ชนิดตั้งต้นของฟอร์ม (นัดหมาย) + ยืนยันอัตโนมัติ</summary>
    private static async Task<(Guid CompanyId, Guid SiteId, Guid ServiceId)> SeedOwnerScreenshotAsync(Accounting.Data.AccountingDbContext db,
        BookingType type = BookingType.Appointment, BookingPaymentMode siteMode = BookingPaymentMode.DepositRequired)
    {
        var company = new Company { Name = "ร้านขนมทดสอบ", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = company.Id, Name = "ร้านขนมปังบ้านสวน", Slug = "bk-" + tag, Subdomain = "bk-" + tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        var svc = new SiteBookingService
        {
            CompanyId = company.Id, SiteId = site.Id, Name = "Sourdough สังขยา", Slug = "sd-" + tag,
            SlotMode = BookingSlotMode.Interval, DurationMinutes = 60, SlotIntervalMinutes = 60, MaxCapacity = 5,
            MinAdvanceBookingHours = 0, MaxAdvanceBookingDays = 30, BookingType = type, AutoConfirm = true, IsActive = true, Price = 35m,
        };
        db.SiteBookingServices.Add(svc);
        db.SiteBookingSlots.Add(new SiteBookingSlot
        {
            CompanyId = company.Id, BookingServiceId = svc.Id, DayOfWeek = TargetDate().DayOfWeek,
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0), MaxCapacity = 5, IsActive = true,
        });
        db.SiteBookingSettings.Add(new SiteBookingSettings
        {
            CompanyId = company.Id, SiteId = site.Id, PaymentMode = siteMode, PaymentDepositPercent = 50m, PaymentDeadlineMinutes = null,
            BankTransferEnabled = true,
        });
        db.SitePaymentGateways.Add(new SitePaymentGateway
        {
            CompanyId = company.Id, SiteId = site.Id, GatewayType = PaymentGatewayType.BankTransfer, Name = "กสิกรไทย",
            BankName = "กสิกรไทย", BankAccountNumber = "1111111111", BankAccountName = "ร้านขนมปังบ้านสวน", IsTestMode = false,
        });
        await db.SaveChangesAsync();
        return (company.Id, site.Id, svc.Id);
    }

    [Fact]
    public async Task R226_บั๊กBK2611_0004_มัดจำบังคับ_บริการนัดหมาย_ไม่ยืนยันอัตโนมัติ_หน้าตั้งค่ากล่องแดง_แก้คลิกเดียวแล้วเก็บมัดจำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedOwnerScreenshotAsync(db);

        // 1) ตัวอย่างก่อนกดจอง: ไม่มียอด แต่บอกว่าจะ "รอร้านยืนยัน"
        var preview = await Svc(db).PreviewPaymentAsync(cid, sid, new BookingPaymentPreviewRequest { BookingServiceId = svcId });
        Assert.False(preview!.Collects);
        Assert.True(preview.ShopArrangesPayment);
        Assert.Equal(BookingPaymentPolicy.ShopArrangesPaymentPreview, preview.NextStepNote);

        // 2) จอง (ลูกค้าเอง): เดิม = Confirmed "จองสำเร็จ" · ตอนนี้ = รอร้านยืนยัน + ไม่ถือรอบ
        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId), userId: null);
        Assert.Equal(BookingStatus.Pending, created.Status);
        Assert.Equal(0m, created.DepositAmount);
        Assert.Equal("ได้รับการจองแล้ว — รอร้านยืนยัน", created.Payment!.Headline);
        Assert.Equal("รอร้านยืนยัน", created.CustomerStatusLabel);
        Assert.Null(created.Payment.ShopPolicyWarning);   // คำตอบลูกค้าไม่มีข้อความถึงพนักงาน
        Assert.Null((await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id && b.CompanyId == cid)).PaymentHoldUntil);
        var staffView = await Svc(db).GetBookingAsync(cid, sid, created.Id);
        Assert.Contains("ไม่ยืนยันอัตโนมัติ", staffView!.Payment!.ShopPolicyWarning);

        // 3) หน้าตั้งค่า: กล่องแดง + บริการในรายการ · บันทึกโหมดบังคับซ้ำ ⇒ 409 (บล็อกจนแก้)
        var settings = await Svc(db).GetPaymentSettingsAsync(cid, sid);
        Assert.Contains("Sourdough สังขยา", settings.UncollectableProblem);
        Assert.True(settings.Services.Single(x => x.Id == svcId).RequiredButUncollectable);
        Assert.Equal(1440, settings.DefaultDeadlineMinutes);
        var blocked = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).UpdatePaymentSettingsAsync(cid, sid, new BookingPaymentSettingsDto
        {
            Mode = "DepositRequired", DepositPercent = 50m, BankTransferEnabled = true,
        }, "owner"));
        Assert.Equal(409, blocked.StatusCode);
        Assert.Equal(BookingPaymentPolicy.UncollectableRuleCode, blocked.RuleCode);

        // 4) แก้คลิกเดียว: เปลี่ยนชนิด ⇒ ไม่มีกล่องแดง · บันทึกผ่าน
        var fixedDto = await Svc(db).FixUncollectableServicesAsync(cid, sid, new FixUncollectableServicesRequest { Action = "convert", ServiceIds = { svcId } }, "owner");
        Assert.Null(fixedDto.UncollectableProblem);
        Assert.Equal(BookingType.PrePayment, (await db.SiteBookingServices.AsNoTracking().SingleAsync(x => x.Id == svcId)).BookingType);

        // 5) จองใหม่: มัดจำ 50% ของ ฿35 = ฿17.50 · รอชำระเงิน · ถือรอบตามค่าตั้งต้น 1440 นาที · มีเลขบัญชี + แนบสลิปได้
        var before = DateTime.UtcNow;
        var paid = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "11:00"), userId: null);
        Assert.Equal(BookingStatus.Pending, paid.Status);
        Assert.Equal(17.50m, paid.DepositAmount);
        Assert.Equal("รอชำระเงิน", paid.CustomerStatusLabel);
        Assert.True(paid.Payment!.CanUploadSlip);
        Assert.Equal("1111111111", paid.Payment.Transfer!.AccountNumber);
        var hold = (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == paid.Id)).PaymentHoldUntil;
        Assert.NotNull(hold);
        Assert.InRange(hold!.Value, before.AddMinutes(1439), DateTime.UtcNow.AddMinutes(1441));   // นัดอีก 10 วัน ⇒ ไม่ถูกตัดที่เวลานัด

        // 6) แนบสลิป ⇒ รอร้านตรวจ (ยังไม่ยืนยัน) · ร้านยืนยัน ⇒ ยืนยันแล้ว + คิว "ชำระแล้ว — ยืนยันการจอง" หนึ่งแถว
        var (slips, _) = Slips(db);
        var afterSlip = await slips.UploadByTokenAsync(cid, sid, paid.PublicToken!, PdfSlip());
        Assert.Equal(BookingStatus.Pending, afterSlip!.Status);
        Assert.Equal("รอร้านตรวจสลิป", afterSlip.CustomerStatusLabel);
        var verified = await slips.VerifyAsync(cid, sid, paid.Id, new VerifyBookingSlipRequest(), "staff-1");
        Assert.Equal(BookingStatus.Confirmed, verified.Status);
        Assert.Equal(1, await db.DeferredNotifications.CountAsync(d => d.CompanyId == cid && d.EntityId == paid.Id
            && d.Kind == DeferredNotifyPolicy.BookingPaidConfirmed));
        // ใบแรก (ก่อนแก้) ไม่ถูกแตะ — ยังรอร้านยืนยัน
        Assert.Equal(BookingStatus.Pending, (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == created.Id)).Status);
    }

    [Fact]
    public async Task R226_ทิศตรงข้าม_ยกเว้นบริการ_ยืนยันอัตโนมัติตามเดิม_โหมดไม่บังคับไม่ถูกกัน_พนักงานจองแทนไม่บังคับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedOwnerScreenshotAsync(db);
        // พนักงานจองแทน ⇒ ยืนยันได้ (รับเงินหน้างาน)
        Assert.Equal(BookingStatus.Confirmed, (await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "09:00"), userId: null, staffOnBehalf: true)).Status);
        // ยกเว้น ⇒ บริการนี้ไม่รับชำระล่วงหน้า ⇒ AutoConfirm เดิม · หน้าตั้งค่าไม่มีกล่องแดง
        var exempt = await Svc(db).FixUncollectableServicesAsync(cid, sid, new FixUncollectableServicesRequest { Action = "exempt", ServiceIds = { svcId } }, "owner");
        Assert.Null(exempt.UncollectableProblem);
        Assert.Equal(BookingStatus.Confirmed, (await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "10:00"), userId: null)).Status);

        // โหมดไม่บังคับ + บริการนัดหมาย ⇒ บันทึกได้ (ไม่ใช่ด่านนี้) · การจองยืนยันตาม AutoConfirm
        var (cid2, sid2, svcId2) = await SeedOwnerScreenshotAsync(db, siteMode: BookingPaymentMode.DepositOptional);
        Assert.Null((await Svc(db).GetPaymentSettingsAsync(cid2, sid2)).UncollectableProblem);
        await Svc(db).UpdatePaymentSettingsAsync(cid2, sid2, new BookingPaymentSettingsDto { Mode = "DepositOptional", DepositPercent = 50m, BankTransferEnabled = true }, "owner");
        Assert.Equal(BookingStatus.Confirmed, (await Svc(db).CreateBookingAsync(cid2, sid2, Req(svcId2), userId: null)).Status);
    }

    [Fact]
    public async Task R226_จัดส่ง_ค่าส่งคิดที่เซิร์ฟเวอร์จากโซนของร้าน_มัดจำไม่รวมค่าส่ง_รับเองไม่มีค่าส่ง_ที่อยู่ไม่ครบปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedOwnerScreenshotAsync(db, BookingType.PrePayment);
        var zone = new SiteShippingZone { CompanyId = cid, SiteId = sid, Name = "กทม.", Provinces = "กรุงเทพมหานคร", IsActive = true };
        zone.Rates.Add(new SiteShippingRate { CompanyId = cid, Name = "ส่งด่วนในเมือง", RateType = ShippingRateType.Flat, BaseRate = 50m, IsActive = true });
        db.SiteShippingZones.Add(zone);
        await db.SaveChangesAsync();
        await Svc(db).UpdateFulfillmentSettingsAsync(cid, sid, new BookingFulfillmentSettingsDto
        {
            DeliveryEnabled = true, PickupEnabled = true, PickupNote = "รับที่ร้านสาขาสยาม 10:00–18:00",
        }, "owner");

        // ตัวอย่างก่อนจอง = ตอนจอง (ตัวคิดเดียว)
        var preview = await Svc(db).PreviewPaymentAsync(cid, sid, new BookingPaymentPreviewRequest
        {
            BookingServiceId = svcId, FulfillmentMethod = "delivery", DeliveryProvince = "กรุงเทพมหานคร", DeliveryPostalCode = "10330",
        });
        Assert.Equal(50m, preview!.DeliveryFee);
        Assert.Equal(85m, preview.GrandTotal);
        Assert.Equal(17.50m, preview.AmountDue);   // มัดจำ 50% ของค่าบริการ ไม่รวมค่าส่ง

        // เปิดสองแบบ + ไม่ส่งวิธี ⇒ ต้องเลือก (ไม่เดา) · ที่อยู่ไม่ครบ ⇒ ปฏิเสธก่อนบันทึก
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "09:00"), userId: null));
        var noAddr = Req(svcId, "09:00"); noAddr.FulfillmentMethod = "delivery";
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).CreateBookingAsync(cid, sid, noAddr, userId: null));
        Assert.Equal(0, await db.SiteBookings.CountAsync(b => b.CompanyId == cid));

        var del = Req(svcId, "10:00");
        del.FulfillmentMethod = "delivery"; del.DeliveryAddress = "99/1 ถนนพระราม 1 แขวงปทุมวัน"; del.DeliveryProvince = "กรุงเทพมหานคร"; del.DeliveryPostalCode = "10330";
        var booked = await Svc(db).CreateBookingAsync(cid, sid, del, userId: null);
        Assert.Equal("delivery", booked.FulfillmentMethod);
        Assert.Equal(50m, booked.DeliveryFee);
        Assert.Equal(85m, booked.GrandTotal);
        Assert.Equal(35m, booked.TotalAmount);
        Assert.Equal(17.50m, booked.DepositAmount);
        Assert.Contains("ค่าจัดส่ง ฿50.00", booked.FulfillmentSummary);
        var row = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == booked.Id && b.CompanyId == cid);
        Assert.Equal("ส่งด่วนในเมือง", row.DeliveryRateName);

        // พื้นที่นอกโซน ⇒ ปฏิเสธพร้อมทางไปต่อ (รับเอง)
        var far = Req(svcId, "12:00");
        far.FulfillmentMethod = "delivery"; far.DeliveryAddress = "1 ถนนนิมมาน"; far.DeliveryProvince = "เชียงใหม่"; far.DeliveryPostalCode = "50200";
        var farEx = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).CreateBookingAsync(cid, sid, far, userId: null));
        Assert.Contains("รับเอง", farEx.Message);

        // รับเอง ⇒ ค่าส่ง 0 · ที่รับจากค่าตั้งเว็บ · ไม่เก็บที่อยู่
        var pick = Req(svcId, "13:00"); pick.FulfillmentMethod = "pickup"; pick.DeliveryAddress = "ไม่ควรถูกเก็บ";
        var picked = await Svc(db).CreateBookingAsync(cid, sid, pick, userId: null);
        Assert.Equal(0m, picked.DeliveryFee);
        Assert.Equal("รับที่ร้านสาขาสยาม 10:00–18:00", picked.PickupNote);
        Assert.Null((await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == picked.Id)).DeliveryAddress);

        // ชำระเต็มจำนวน ⇒ ค่าบริการ + ค่าจัดส่ง
        var s = await db.SiteBookingSettings.SingleAsync(x => x.CompanyId == cid && x.SiteId == sid);
        s.PaymentMode = BookingPaymentMode.FullRequired;
        await db.SaveChangesAsync();
        var full = Req(svcId, "14:00");
        full.FulfillmentMethod = "delivery"; full.DeliveryAddress = "99/1 ถนนพระราม 1 แขวงปทุมวัน"; full.DeliveryProvince = "กรุงเทพมหานคร"; full.DeliveryPostalCode = "10330";
        var fullBooked = await Svc(db).CreateBookingAsync(cid, sid, full, userId: null);
        Assert.Equal(85m, fullBooked.DepositAmount);
        Assert.Equal(85m, fullBooked.Payment!.AmountDue);
    }

    /// <summary>ฝ่ายค้านรอบ 226 ข้อ 1: ต่อสายเส้นเอกสารจริง (DocumentService) — การจองนัดหมายที่ออกใบเสนอราคาแล้ว + เปลี่ยนชนิดบริการ ⇒ ไม่ 409 ·
    /// ใบเดิมคงชนิดที่ตรึงไว้ (นัดหมาย) · ใบใหม่เป็นชำระมัดจำล่วงหน้า</summary>
    [Fact]
    public async Task R226_ฝ่ายค้าน1_เปลี่ยนชนิดเมื่อมีการจองเปิดที่ออกเอกสารแล้ว_ได้_ใบเดิมคงชนิดเดิม_ใบใหม่ชนิดใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedOwnerScreenshotAsync(db);
        var acc = new AccountingService(db);
        var docs = new DocumentService(db, acc, AllowAll<ISubscriptionService>.Create(), null!, null!,
            NullLogger<DocumentService>.Instance, null!, null!, null!);
        var withDocs = new CmsBookingService(db, NullLogger<CmsBookingService>.Instance, docService: docs);

        var old = await withDocs.CreateBookingAsync(cid, sid, Req(svcId, "09:00"), userId: null);
        var oldRow = await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == old.Id && b.CompanyId == cid);
        Assert.Equal(BookingType.Appointment, oldRow.BookingType);
        Assert.True(oldRow.ErpDocumentId != null, "การจองนัดหมายต้องออกใบเสนอราคา (เส้นเอกสารจริง) — " + oldRow.InternalNotes);
        Assert.Equal(BookingStatus.Pending, oldRow.Status);

        // เดิม (รอบ 226 ก่อนฝ่ายค้าน) = 409 เพราะมีการจองเปิดที่ออกเอกสารแล้ว · ตอนนี้ผ่าน (มีผลกับการจองใหม่เท่านั้น)
        var fixedDto = await withDocs.FixUncollectableServicesAsync(cid, sid, new FixUncollectableServicesRequest { Action = "convert", ServiceIds = { svcId } }, "owner");
        Assert.Null(fixedDto.UncollectableProblem);

        var oldAfter = await withDocs.GetBookingAsync(cid, sid, old.Id);
        Assert.Equal(BookingType.Appointment, oldAfter!.BookingType);   // ตรึง ณ ตอนจอง
        Assert.Equal(oldRow.ErpDocumentId, oldAfter.ErpDocumentId);
        var fresh = await withDocs.CreateBookingAsync(cid, sid, Req(svcId, "10:00"), userId: null);
        Assert.Equal(BookingType.PrePayment, fresh.BookingType);
        Assert.Equal(17.50m, fresh.DepositAmount);

        // ข้อ 8: id ที่ไม่อยู่ในรายการ "ต้องแก้" ⇒ ปฏิเสธ (แก้ไปแล้ว/บริการอื่น)
        await Assert.ThrowsAsync<BusinessRuleException>(() => withDocs.FixUncollectableServicesAsync(cid, sid,
            new FixUncollectableServicesRequest { Action = "exempt", ServiceIds = { svcId } }, "owner"));
    }

    [Fact]
    public async Task R226_ฝ่ายค้าน3_เว็บเดิมกำหนดชำระ0_คงไม่กำหนด_มีคำแนะนำ_บันทึกแล้วหาย_ว่างได้ค่าตั้งต้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedOwnerScreenshotAsync(db, BookingType.PrePayment);
        var row = await db.SiteBookingSettings.SingleAsync(x => x.CompanyId == cid && x.SiteId == sid);
        row.PaymentDeadlineMinutes = 0; row.PaymentDeadlineLegacyNote = true;   // สภาพหลัง migration ของเว็บเดิม
        await db.SaveChangesAsync();
        var s = await Svc(db).GetPaymentSettingsAsync(cid, sid);
        Assert.Equal(BookingPaymentPolicy.LegacyDeadlineNote, s.DeadlineNote);
        Assert.Equal(0, s.DeadlineMinutes);
        var legacy = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "09:00"), userId: null);
        Assert.Null((await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == legacy.Id)).PaymentHoldUntil);   // 0 = ไม่ถือรอบ (เดิม)

        // บันทึก 0 โดยตั้งใจ ⇒ เก็บ 0 + คำแนะนำหาย
        var saved = await Svc(db).UpdatePaymentSettingsAsync(cid, sid, new BookingPaymentSettingsDto
        { Mode = "DepositRequired", DepositPercent = 50m, DeadlineMinutes = 0, BankTransferEnabled = true }, "owner");
        Assert.Equal(0, saved.DeadlineMinutes);
        Assert.Null(saved.DeadlineNote);
        // ว่าง ⇒ เก็บ null ⇒ ค่าตั้งต้น 1440
        await Svc(db).UpdatePaymentSettingsAsync(cid, sid, new BookingPaymentSettingsDto
        { Mode = "DepositRequired", DepositPercent = 50m, DeadlineMinutes = null, BankTransferEnabled = true }, "owner");
        var before = DateTime.UtcNow;
        var fresh = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "10:00"), userId: null);
        var hold = (await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == fresh.Id)).PaymentHoldUntil;
        Assert.InRange(hold!.Value, before.AddMinutes(1439), DateTime.UtcNow.AddMinutes(1441));
        Assert.Null(fresh.Payment!.ShopPolicyWarning);
    }
}


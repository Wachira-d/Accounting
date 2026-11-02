using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 228 (TEST_PLAN CMSUX-A3 · <c>erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md</c> แพ็ก A + รีวิว S-3/S-4/S-7) — บัญชีรับโอนของเว็บ บนฐานจริง:
/// <list type="bullet">
///   <item>ล้างช่องที่มีค่าโดยไม่ยืนยัน ⇒ 409 (ยืนยันการล้าง) · ไม่มีอะไรถูกเขียน</item>
///   <item>ล้างแล้วการจองที่บังคับชำระเก็บเงินไม่ได้ ⇒ 409 · ล้างแล้วคำสั่งซื้อที่รับโอนรับเงินไม่ได้ ⇒ 409 (เหตุ "คำสั่งซื้อ") · ล้างแล้วแขกที่พักต้องโอนแต่ไม่มีบัญชี ⇒ 409 (เหตุ "ที่พัก")</item>
///   <item>ทางเข้าเดิม <c>PUT commerce/payment-gateways/{id}</c> ไม่ส่งรูป QR/ชื่อธนาคาร ⇒ คงค่าเดิม · สร้างแถวโอนซ้ำ ⇒ แก้แถวเดิม (ไม่เกิดแถวที่สอง) · ลง audit</item>
/// </list>
/// ไม่มีฐาน (ไม่ตั้ง <c>ACCOUNTING_TEST_PG</c>) ⇒ ข้ามพร้อมข้อความ — หลักฐานคือ job <c>db-test</c>
/// </summary>
[Trait("Category", "Db")]
public class SiteTransferAccountDbTests
{
    private readonly ITestOutputHelper _out;
    public SiteTransferAccountDbTests(ITestOutputHelper output) => _out = output;

    private static CmsBookingService Bookings(Accounting.Data.AccountingDbContext db) => new(db, NullLogger<CmsBookingService>.Instance);

    private static CmsCommerceService Commerce(Accounting.Data.AccountingDbContext db) => new(db, NullLogger<CmsCommerceService>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

    private static async Task<(Guid CompanyId, Guid SiteId, Guid GatewayId)> SeedAsync(Accounting.Data.AccountingDbContext db,
        BookingPaymentMode? siteMode = null, bool prePaymentService = false, bool lodgingRequireSlip = false)
    {
        var company = new Company { Name = "ร้านทดสอบบัญชีรับโอน", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = company.Id, Name = "ร้านบ้านสวน", Slug = "ta-" + tag, Subdomain = "ta-" + tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        if (prePaymentService)
            db.SiteBookingServices.Add(new SiteBookingService
            {
                CompanyId = company.Id, SiteId = site.Id, Name = "ทำฟัน", Slug = "dent-" + tag, BookingType = BookingType.PrePayment,
                SlotMode = BookingSlotMode.Interval, DurationMinutes = 60, SlotIntervalMinutes = 60, MaxCapacity = 5, IsActive = true, Price = 500m,
            });
        db.SiteBookingSettings.Add(new SiteBookingSettings { CompanyId = company.Id, SiteId = site.Id, PaymentMode = siteMode, PaymentDepositPercent = 30m });
        if (lodgingRequireSlip)
            db.LodgingProperties.Add(new LodgingProperty
            {
                CompanyId = company.Id, SiteId = site.Id, Name = "บ้านพักริมน้ำ", Code = "RM" + tag[..4], GuestConfirmMode = LodgingGuestConfirmMode.RequireSlip, IsActive = true,
            });
        var gw = new SitePaymentGateway
        {
            CompanyId = company.Id, SiteId = site.Id, GatewayType = PaymentGatewayType.BankTransfer, Name = "กสิกรไทย", IsTestMode = false,
            BankName = "กสิกรไทย", BankAccountNumber = "1234567890", BankAccountName = "ร้านบ้านสวน", PromptPayQrUrl = "https://example.test/qr.png",
        };
        db.SitePaymentGateways.Add(gw);
        await db.SaveChangesAsync();
        return (company.Id, site.Id, gw.Id);
    }

    private static SiteTransferAccountDto Empty(bool confirm) => new() { ConfirmClear = confirm };

    [Fact]
    public async Task ล้างบัญชีโดยไม่ยืนยัน_409_ไม่เขียนอะไร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, sid, gwId) = await SeedAsync(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Bookings(db).UpdateTransferAccountAsync(cid, sid, Empty(confirm: false), Guid.NewGuid().ToString()));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(BookingPaymentChannels.ClearConfirmRuleCode, ex.RuleCode);
        db.ChangeTracker.Clear();
        var row = await db.SitePaymentGateways.AsNoTracking().SingleAsync(g => g.Id == gwId && g.CompanyId == cid);
        Assert.Equal("1234567890", row.BankAccountNumber);
        Assert.Equal("https://example.test/qr.png", row.PromptPayQrUrl);
    }

    [Fact]
    public async Task ล้างบัญชีที่การจองบังคับชำระใช้อยู่_409_แม้ยืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, gwId) = await SeedAsync(db, BookingPaymentMode.DepositRequired, prePaymentService: true);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Bookings(db).UpdateTransferAccountAsync(cid, sid, Empty(confirm: true), Guid.NewGuid().ToString()));
        Assert.Equal(409, ex.StatusCode);
        db.ChangeTracker.Clear();
        Assert.Equal("1234567890", (await db.SitePaymentGateways.AsNoTracking().SingleAsync(g => g.Id == gwId)).BankAccountNumber);
    }

    [Fact]
    public async Task ล้างบัญชีที่แขกที่พักต้องโอน_409_เหตุที่พัก_ทิศตรงข้ามไม่มีงานอื่นล้างได้และลงaudit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, gwId) = await SeedAsync(db, lodgingRequireSlip: true);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Bookings(db).UpdateTransferAccountAsync(cid, sid, Empty(confirm: true), Guid.NewGuid().ToString()));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("ที่พัก", ex.Message);

        // ทิศตรงข้าม: เว็บที่ไม่มีงานใดพึ่งบัญชี ⇒ ล้างได้เมื่อยืนยัน + audit (ปิดเลข)
        db.ChangeTracker.Clear();   // แถวของเว็บแรกถูกล้างในหน่วยความจำ (ไม่ถูกบันทึก) — ห้ามให้ seed ถัดไปบันทึกตามไปด้วย
        var (cid2, sid2, _) = await SeedAsync(db);
        db.ChangeTracker.Clear();
        var after = await Bookings(db).UpdateTransferAccountAsync(cid2, sid2, Empty(confirm: true), Guid.NewGuid().ToString());
        Assert.False(after.Payable);
        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == cid2 && a.EntityType == "SiteTransferAccount").ToListAsync();
        Assert.Single(audit);
        Assert.DoesNotContain("1234567890", audit[0].OldValues ?? "");
        Assert.Contains("7890", audit[0].OldValues ?? "");
    }

    [Fact]
    public async Task ล้างบัญชีที่คำสั่งซื้อรับโอนอยู่_409_เหตุคำสั่งซื้อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, gwId) = await SeedAsync(db);
        var product = new Product { CompanyId = cid, Code = "P-" + Guid.NewGuid().ToString("N")[..6], Name = "ผ้าทอ", Unit = "ชิ้น" };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.SiteProducts.Add(new SiteProduct { CompanyId = cid, SiteId = sid, ProductId = product.Id });
        await db.SaveChangesAsync();   // ไม่มี SiteCommerceConfig ⇒ EnableBankTransfer ตั้งต้น = เปิด
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Bookings(db).UpdateTransferAccountAsync(cid, sid, Empty(confirm: true), Guid.NewGuid().ToString()));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(SiteTransferAccount.RuleCode, ex.RuleCode);
        Assert.Contains("คำสั่งซื้อ", ex.Message);
        db.ChangeTracker.Clear();
        Assert.Equal("1234567890", (await db.SitePaymentGateways.AsNoTracking().SingleAsync(g => g.Id == gwId)).BankAccountNumber);
    }

    [Fact]
    public async Task ทางเข้าเดิมPUTไม่ส่งรูปQRและชื่อธนาคาร_คงค่าเดิม_สร้างซ้ำไม่เกิดแถวที่สอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, gwId) = await SeedAsync(db);
        var req = new CreatePaymentGatewayRequest
        {
            GatewayType = PaymentGatewayType.BankTransfer, Name = "Bank Transfer",
            BankAccountNumber = "9876543210", BankAccountName = "ร้านบ้านสวน", IsTestMode = false,
            // modal เดิมของหน้าคำสั่งซื้อ: ไม่ส่ง promptPayQrUrl / bankName (null)
        };
        await Commerce(db).UpdatePaymentGatewayAsync(cid, sid, gwId, req, Guid.NewGuid().ToString());
        db.ChangeTracker.Clear();
        var row = await db.SitePaymentGateways.AsNoTracking().SingleAsync(g => g.Id == gwId && g.CompanyId == cid);
        Assert.Equal("9876543210", row.BankAccountNumber);
        Assert.Equal("https://example.test/qr.png", row.PromptPayQrUrl);   // เดิม: ถูกล้างเงียบ
        Assert.Equal("กสิกรไทย", row.BankName);

        // สร้าง "ใหม่" ชนิดโอนบนเว็บที่มีแถวแล้ว ⇒ แก้แถวเดิม (ไม่มีแถวโอนที่สอง)
        await Commerce(db).CreatePaymentGatewayAsync(cid, sid, req, Guid.NewGuid().ToString());
        db.ChangeTracker.Clear();
        var rows = await SiteTransferAccount.Rows(db.SitePaymentGateways.AsNoTracking(), cid, sid).ToListAsync();
        Assert.Single(rows);
        Assert.True(await db.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == cid && a.EntityType == "SiteTransferAccount"));
    }
}

using System.Text.Encodings.Web;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Portal;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 260-3 — เงินรอจัดสรรบนพอร์ทัล/สิทธิ์พอร์ทัล (260-3-1) · กระดิ่งเงินรอจัดสรรค้างนาน (260-3-3) · กล่องใบซื้อร่างรอตรวจ (260-3-5) บนฐานจริง
/// ทั้งสองทิศ: เห็นเฉพาะของผู้ติดต่อในโทเคน/บริษัทตัวเอง · ธงใบแจ้งยอดปิด = ไม่เห็น · ตัวนับตรงกับแถวต่อทางเข้า · แจ้งครั้งเดียว
/// </summary>
[Trait("Category", "Db")]
public class CreditInboxRound260DbTests
{
    private readonly ITestOutputHelper _out;
    public CreditInboxRound260DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Portal:SigningKey"] = "r260-portal-signing-key-for-db-tests-0123456789",
        ["App:BaseUrl"] = "https://acc.r260.test",
    }).Build();

    private sealed class SchemeOptions : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed class RecordingEngine : INotificationEngine
    {
        private readonly NotificationDispatchOutcome _outcome;
        private readonly bool _throw;
        public RecordingEngine(NotificationDispatchOutcome outcome = NotificationDispatchOutcome.Delivered, bool @throw = false) { _outcome = outcome; _throw = @throw; }
        public List<(Guid CompanyId, string Key, NotificationContext Ctx)> Calls { get; } = new();
        public Task DispatchAsync(Guid companyId, string eventKey, NotificationContext context) => DispatchWithOutcomeAsync(companyId, eventKey, context);
        public Task<NotificationDispatchOutcome> DispatchWithOutcomeAsync(Guid companyId, string eventKey, NotificationContext context)
        {
            Calls.Add((companyId, eventKey, context));
            if (_throw) throw new InvalidOperationException("ช่องแจ้งเตือนล้ม (เทสต์)");
            return Task.FromResult(_outcome);
        }
    }

    /// <summary>engine เดิมที่ไม่ได้เขียน DispatchWithOutcomeAsync (ค่าตั้งต้นของ interface = Unknown)</summary>
    private sealed class LegacyEngine : INotificationEngine
    {
        public int Calls;
        public Task DispatchAsync(Guid companyId, string eventKey, NotificationContext context) { Calls++; return Task.CompletedTask; }
    }

    private static string TaxId() => "09" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static async Task<(Company Shop, Contact A, Contact B)> CompanyAsync(AccountingDbContext db)
    {
        var shop = new Company { Name = "ร้าน 260-3 " + Guid.NewGuid().ToString("N")[..6], TaxId = TaxId() };
        db.Companies.Add(shop);
        await db.SaveChangesAsync();
        var a = new Contact { CompanyId = shop.Id, Name = "ลูกค้า A 260", IsCustomer = true };
        var b = new Contact { CompanyId = shop.Id, Name = "ลูกค้า B 260", IsCustomer = true };
        db.Contacts.AddRange(a, b);
        await db.SaveChangesAsync();
        return (shop, a, b);
    }

    private static Document Doc(Guid companyId, Guid contactId, DocumentType t, DocumentStatus status, DateTime? date = null)
    {
        var d = new Document
        {
            CompanyId = companyId, ContactId = contactId, DocumentType = t, DocumentNumber = (status == DocumentStatus.Draft ? "DRAFT-" : "D260-") + Guid.NewGuid().ToString("N")[..10],
            Status = status, DocumentDate = date ?? DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m,
            BalanceDue = 1_070m, Currency = "THB",
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "รายการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatAmount = 70m });
        return d;
    }

    /// <summary>การรับชำระที่มีเงินรอจัดสรรค้าง (ผูกผังพัก · รู้เจ้าของ)</summary>
    private static Payment Credit(Guid companyId, Guid docId, Guid? contactId, decimal unapplied, DateTime paymentDate, DateTime? noticeAt = null) => new()
    {
        CompanyId = companyId, PaymentNumber = "RC260-" + Random.Shared.Next(100000, 999999), DocumentId = docId, PaymentDate = paymentDate,
        Amount = unapplied + 100m, PaymentMethod = PaymentMethod.BankTransfer, ContactId = contactId, UnappliedAmount = unapplied,
        UnappliedAccountId = Guid.NewGuid(), UnappliedReason = nameof(UnappliedReason.Unknown), UnappliedAgedNoticeAt = noticeAt,
    };

    private static async Task<PortalPublicController> ControllerAsync(IPortalService portal, string token, Guid companyId)
    {
        var services = new ServiceCollection().AddSingleton(portal).BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Headers.Authorization = "Bearer " + token;
        ctx.Request.RouteValues["companyId"] = companyId.ToString();
        var handler = new PortalAuthenticationHandler(new SchemeOptions(), NullLoggerFactory.Instance, UrlEncoder.Default);
        await handler.InitializeAsync(new AuthenticationScheme(PortalTokenPolicy.Scheme, null, typeof(PortalAuthenticationHandler)), ctx);
        var auth = await handler.AuthenticateAsync();
        Assert.True(auth.Succeeded);
        return new PortalPublicController(portal)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = auth.Principal!, RequestServices = services } },
        };
    }

    [Fact]
    public async Task R260_1_ใบแจ้งยอดพอร์ทัล_เงินรอจัดสรรเฉพาะผู้ติดต่อในโทเคน_ธงปิดแล้วไม่เห็น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (shop, a, b) = await CompanyAsync(db);
        var (other, otherContact, _) = await CompanyAsync(db);
        var invA = Doc(shop.Id, a.Id, DocumentType.Invoice, DocumentStatus.Approved);
        var invB = Doc(shop.Id, b.Id, DocumentType.Invoice, DocumentStatus.Approved);
        var invOther = Doc(other.Id, otherContact.Id, DocumentType.Invoice, DocumentStatus.Approved);
        db.Documents.AddRange(invA, invB, invOther);
        await db.SaveChangesAsync();
        var today = DateTime.UtcNow.Date;
        db.Payments.AddRange(
            Credit(shop.Id, invA.Id, a.Id, 500m, today),
            Credit(shop.Id, invB.Id, b.Id, 300m, today),                 // ผู้ติดต่ออื่นในบริษัทเดียวกัน — ห้ามนับ
            Credit(other.Id, invOther.Id, a.Id, 900m, today));          // บริษัทอื่นที่ประทับ ContactId ของ A (ปลอม) — ห้ามนับ (tenant)
        await db.SaveChangesAsync();

        var email = $"portal260-{Guid.NewGuid():N}@cust.test";
        var access = new PortalAccess
        {
            CompanyId = shop.Id, ContactId = a.Id, Email = email, IsActive = true,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Portal#260-pass", workFactor: 4),
            CanViewInvoices = true, CanViewStatements = true, CanDownloadPdf = true, CanMakePayment = true,
        };
        db.PortalAccesses.Add(access);
        await db.SaveChangesAsync();
        var svc = new PortalService(db, Config());
        var session = await svc.LoginAsync(new PortalLoginRequest(email, "Portal#260-pass", shop.Id));

        var st = await svc.GetMyStatementAsync(shop.Id, a.Id, today.AddDays(-1), today.AddDays(1));
        Assert.Equal(500m, st.UnappliedCredit);
        Assert.Equal(300m, (await svc.GetMyStatementAsync(shop.Id, b.Id, today.AddDays(-1), today.AddDays(1))).UnappliedCredit);

        // หน้าสิทธิ์ของพนักงาน: ยอด + "ลูกค้าเห็น" (ด่านเดียวกับเส้นใบแจ้งยอด)
        var row = Assert.Single(await svc.GetAccessesAsync(shop.Id, includeCredit: true), x => x.Id == access.Id);
        Assert.Equal(500m, row.UnappliedCredit);
        Assert.True(row.UnappliedCreditShownToCustomer);
        // ฝ่ายค้าน L1: ผู้เรียกที่ไม่เห็นฝั่งขาย ⇒ null (ไม่ได้คำนวณ · ไม่แต่ง 0)
        var hidden = Assert.Single(await svc.GetAccessesAsync(shop.Id), x => x.Id == access.Id);
        Assert.Null(hidden.UnappliedCredit);
        Assert.Null(hidden.UnappliedCreditShownToCustomer);
        // ฝ่ายค้าน L2: คำตอบของแก้สิทธิ์ echo ค่าจริง
        var upd = await svc.UpdateAccessAsync(shop.Id, access.Id, new UpdatePortalAccessRequest(null, null, null, null, null), Guid.NewGuid(), includeCredit: true);
        Assert.Equal(500m, upd.UnappliedCredit);
        Assert.True(upd.UnappliedCreditShownToCustomer);

        // เส้นพอร์ทัลจริง: ธงเปิด = 200 พร้อมยอด
        var ok = await (await ControllerAsync(svc, session.AccessToken, shop.Id)).GetStatement(shop.Id, today.AddDays(-1), today.AddDays(1));
        var okBody = Assert.IsType<OkObjectResult>(ok.Result).Value as Accounting.Models.DTOs.ApiResponse<PortalStatementResponse>;
        Assert.Equal(500m, okBody!.Data!.UnappliedCredit);

        // ทิศตรงข้าม: ปิดธง "ดูใบแจ้งยอด" ⇒ เส้นใบแจ้งยอด 403 (ยอดไม่ถึงลูกค้า) + หน้าพนักงานบอกว่าลูกค้าไม่เห็น
        await db.PortalAccesses.IgnoreQueryFilters().Where(x => x.Id == access.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.CanViewStatements, false));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var svc2 = new PortalService(c2, Config());
        var denied = await (await ControllerAsync(svc2, session.AccessToken, shop.Id)).GetStatement(shop.Id, today.AddDays(-1), today.AddDays(1));
        Assert.Equal(403, (denied.Result as ObjectResult)?.StatusCode);
        var row2 = Assert.Single(await svc2.GetAccessesAsync(shop.Id, includeCredit: true), x => x.Id == access.Id);
        Assert.Equal(500m, row2.UnappliedCredit);
        Assert.False(row2.UnappliedCreditShownToCustomer);
    }

    [Fact]
    public async Task R260_3_กระดิ่งเงินรอจัดสรรค้างเกิน30วัน_แจ้งครั้งเดียว_ไม่แจ้งของใหม่หรือที่แจ้งแล้ว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (shop, a, b) = await CompanyAsync(db);
        var inv = Doc(shop.Id, a.Id, DocumentType.Invoice, DocumentStatus.Approved);
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        var today = ThaiDate.CalendarDateUtc(now).Date;
        var aged = Credit(shop.Id, inv.Id, a.Id, 1_250m, today.AddDays(-45));
        var alreadyNoticed = Credit(shop.Id, inv.Id, b.Id, 400m, today.AddDays(-60), noticeAt: now.AddDays(-20));
        var fresh = Credit(shop.Id, inv.Id, a.Id, 80m, today.AddDays(-5));
        db.Payments.AddRange(aged, alreadyNoticed, fresh);
        await db.SaveChangesAsync();

        // ตัวเลขเดียวกับ issue ของ Autopilot (ตัวอ่านเดียว)
        var issues = await CustomerCreditWorkQueue.AutopilotIssuesAsync(db, shop.Id, now);
        Assert.Equal(2, issues.Count(i => i.Kind == "CustomerCreditAged"));

        // ฝ่ายค้าน M1 ทิศตรงข้าม: ส่งล้ม/ไม่มีผู้รับ/engine ไม่รายงานผล/engine โยน ⇒ ไม่ประทับ
        foreach (var bad in new[] { NotificationDispatchOutcome.Failed, NotificationDispatchOutcome.NoRecipient })
        {
            var e = new RecordingEngine(bad);
            await CustomerCreditAgedNotice.RunAsync(db, e, DateTime.UtcNow, null);
            Assert.Single(e.Calls, c => c.CompanyId == shop.Id);
        }
        var legacy = new LegacyEngine();
        await CustomerCreditAgedNotice.RunAsync(db, legacy, DateTime.UtcNow, null);
        Assert.True(legacy.Calls >= 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CustomerCreditAgedNotice.RunAsync(db, new RecordingEngine(@throw: true), DateTime.UtcNow, null));
        using (var c0 = DbTestDatabase.TryCreateContext()!)
            Assert.Null((await c0.Payments.AsNoTracking().SingleAsync(p => p.Id == aged.Id)).UnappliedAgedNoticeAt);

        var engine = new RecordingEngine();
        await CustomerCreditAgedNotice.RunAsync(db, engine, DateTime.UtcNow, null);
        var mine = engine.Calls.Where(c => c.CompanyId == shop.Id).ToList();
        var call = Assert.Single(mine);
        Assert.Equal(NotificationEvents.CustomerCreditAged, call.Key);
        Assert.Contains("1 รายการ", call.Ctx.Title);       // เฉพาะแถวที่ยังไม่เคยแจ้ง (ของที่แจ้งแล้ว/ยังไม่ถึง 30 วัน ไม่นับ)
        Assert.Contains("1,250.00", call.Ctx.Title);
        Assert.Equal(CustomerCreditAgedNotice.ActionUrl, call.Ctx.ActionUrl);

        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.NotNull((await c2.Payments.AsNoTracking().SingleAsync(p => p.Id == aged.Id)).UnappliedAgedNoticeAt);
        Assert.Null((await c2.Payments.AsNoTracking().SingleAsync(p => p.Id == fresh.Id)).UnappliedAgedNoticeAt);

        // รอบถัดไป: ไม่แจ้งซ้ำ
        var engine2 = new RecordingEngine();
        await CustomerCreditAgedNotice.RunAsync(c2, engine2, DateTime.UtcNow, null);
        Assert.DoesNotContain(engine2.Calls, c => c.CompanyId == shop.Id);
    }

    [Fact]
    public async Task R260_5_กล่องใบซื้อร่าง_ตัวนับตรงกับแถวต่อทางเข้า_ไม่รวมใบขาย_ใบอนุมัติ_บริษัทอื่น_ชั้นความลับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (shop, a, _) = await CompanyAsync(db);
        var (other, oc, _) = await CompanyAsync(db);
        var manual = Doc(shop.Id, a.Id, DocumentType.PurchaseInvoice, DocumentStatus.Draft);
        var ocr = Doc(shop.Id, a.Id, DocumentType.Expense, DocumentStatus.Draft);
        var line = Doc(shop.Id, a.Id, DocumentType.PurchaseInvoice, DocumentStatus.Draft);
        var etax = Doc(shop.Id, a.Id, DocumentType.PurchaseInvoice, DocumentStatus.Draft);
        var partner = Doc(shop.Id, a.Id, DocumentType.PurchaseInvoice, DocumentStatus.Draft);
        partner.PartnerSourceLinkId = Guid.NewGuid();
        var payroll = Doc(shop.Id, a.Id, DocumentType.Expense, DocumentStatus.Draft);
        payroll.Sensitivity = SensitivityKind.Payroll;
        var approved = Doc(shop.Id, a.Id, DocumentType.PurchaseInvoice, DocumentStatus.Approved);
        var salesDraft = Doc(shop.Id, a.Id, DocumentType.Invoice, DocumentStatus.Draft);
        var deleted = Doc(shop.Id, a.Id, DocumentType.PurchaseInvoice, DocumentStatus.Draft);
        deleted.IsDeleted = true;
        var otherDraft = Doc(other.Id, oc.Id, DocumentType.PurchaseInvoice, DocumentStatus.Draft);
        db.Documents.AddRange(manual, ocr, line, etax, partner, payroll, approved, salesDraft, deleted, otherDraft);
        await db.SaveChangesAsync();
        OcrScanResult Scan(Guid companyId, Guid docId, string? meta = null, string? engine = null) => new()
        {
            CompanyId = companyId, OriginalFileName = "scan-260.jpg", ScanStatus = "Completed", CreatedDocumentId = docId,
            ExternalMetadataJson = meta, OcrEngine = engine,
        };
        db.Set<OcrScanResult>().AddRange(
            Scan(shop.Id, ocr.Id, engine: "Azure"),
            Scan(shop.Id, line.Id, meta: "{\"sourceChannel\":\"line\",\"lineUserId\":\"U260\"}"),
            Scan(shop.Id, etax.Id, engine: OcrHeaderVatEvidence.SignedXmlEngine),
            Scan(other.Id, otherDraft.Id, meta: "{\"sourceChannel\":\"line\"}"));
        await db.SaveChangesAsync();

        var r = await PurchaseDraftInbox.LoadAsync(db, shop.Id, new HashSet<SensitivityKind>());
        int N(PurchaseDraftSource s) => r.Counts.Single(c => c.Source == s.ToString()).Count;
        Assert.Equal(1, N(PurchaseDraftSource.Manual));
        Assert.Equal(1, N(PurchaseDraftSource.Ocr));
        Assert.Equal(1, N(PurchaseDraftSource.Line));
        Assert.Equal(1, N(PurchaseDraftSource.EtaxXml));
        Assert.Equal(1, N(PurchaseDraftSource.Partner));
        Assert.Equal(5, r.Total);
        Assert.Equal(r.Total, r.Counts.Sum(c => c.Count));       // ตัวนับ = แถว
        Assert.Equal(r.Total, r.Items.Count);
        Assert.False(r.Truncated);
        var ids = r.Items.Select(i => i.DocumentId).ToHashSet();
        Assert.DoesNotContain(approved.Id, ids);
        Assert.DoesNotContain(salesDraft.Id, ids);
        Assert.DoesNotContain(deleted.Id, ids);
        Assert.DoesNotContain(otherDraft.Id, ids);
        Assert.DoesNotContain(payroll.Id, ids);
        Assert.Equal(PurchaseDraftSource.Line.ToString(), r.Items.Single(i => i.DocumentId == line.Id).Source);

        // ทิศตรงข้าม: ผู้ดูที่เห็นชั้นเงินเดือน ⇒ ใบนั้นเข้ากล่อง (ตัวนับขยับตาม)
        var r2 = await PurchaseDraftInbox.LoadAsync(db, shop.Id, new HashSet<SensitivityKind> { SensitivityKind.Payroll });
        Assert.Equal(6, r2.Total);
        Assert.Equal(2, r2.Counts.Single(c => c.Source == nameof(PurchaseDraftSource.Manual)).Count);
        // ตัดรายการที่ limit แต่ตัวนับนับครบ
        var r3 = await PurchaseDraftInbox.LoadAsync(db, shop.Id, new HashSet<SensitivityKind>(), limit: 2);
        Assert.Equal(5, r3.Total);
        Assert.Equal(2, r3.Items.Count);
        Assert.True(r3.Truncated);
        // ฝ่ายค้าน M3: กรองทางเข้าก่อนตัดจำนวน — ทุกทางเข้า: แถวที่ได้ = ตัวนับ และป้ายตรงทางเข้า (นิยามเดียวกัน)
        foreach (var src in Enum.GetValues<PurchaseDraftSource>())
        {
            var f = await PurchaseDraftInbox.LoadAsync(db, shop.Id, new HashSet<SensitivityKind>(), src, limit: 1);
            Assert.Equal(src.ToString(), f.Source);
            var only = Assert.Single(f.Items);
            Assert.Equal(src.ToString(), only.Source);
            Assert.False(f.Truncated);
        }
        // 260-3-8: ใบขอซื้อ/ใบสั่งซื้อร่าง ไม่อยู่ในกล่อง
        db.Documents.AddRange(Doc(shop.Id, a.Id, DocumentType.PurchaseOrder, DocumentStatus.Draft), Doc(shop.Id, a.Id, DocumentType.PurchaseRequisition, DocumentStatus.Draft));
        await db.SaveChangesAsync();
        Assert.Equal(5, (await PurchaseDraftInbox.LoadAsync(db, shop.Id, new HashSet<SensitivityKind>())).Total);
    }

    [Fact]
    public async Task R260_6_M2_แจ้งแล้ว500แถว_แถวใหม่1แถวยังถูกแจ้ง_นับครบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (shop, a, _) = await CompanyAsync(db);
        var inv = Doc(shop.Id, a.Id, DocumentType.Invoice, DocumentStatus.Approved);
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow).Date;
        // 500 แถวที่แจ้งแล้ว เก่ากว่าแถวใหม่ (อยู่ก่อนใน OrderBy PaymentDate ของ AgedAsync ที่ตัด 500)
        db.Payments.AddRange(Enumerable.Range(0, CustomerCreditWorkQueue.Cap).Select(i =>
            Credit(shop.Id, inv.Id, a.Id, 1m, today.AddDays(-200 - (i % 50)), noticeAt: DateTime.UtcNow.AddDays(-10))));
        var fresh = Credit(shop.Id, inv.Id, a.Id, 777m, today.AddDays(-40));
        db.Payments.Add(fresh);
        await db.SaveChangesAsync();

        var engine = new RecordingEngine();
        await CustomerCreditAgedNotice.RunAsync(db, engine, DateTime.UtcNow, null);
        var call = Assert.Single(engine.Calls, c => c.CompanyId == shop.Id);
        Assert.Contains("1 รายการ", call.Ctx.Title);
        Assert.Contains("777.00", call.Ctx.Title);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.NotNull((await c2.Payments.AsNoTracking().SingleAsync(p => p.Id == fresh.Id)).UnappliedAgedNoticeAt);
    }

    [Fact]
    public async Task R260_7_L4_โหมดเงินจริง_ตามconfigที่ทางรับเงินเลือก_ไม่ใช่มีตัวไหนLive()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (shop, _, _) = await CompanyAsync(db);
        Assert.False(await PaymentChannelReadiness.GatewayLiveAsync(db, shop.Id));
        db.PaymentProviderConfigs.AddRange(
            new PaymentProviderConfig { CompanyId = shop.Id, ProviderCode = "omise", Mode = PaymentProviderMode.Test, SortOrder = 0 },
            new PaymentProviderConfig { CompanyId = shop.Id, ProviderCode = "omise", Mode = PaymentProviderMode.Live, SortOrder = 1 });
        await db.SaveChangesAsync();
        Assert.False(await PaymentChannelReadiness.GatewayLiveAsync(db, shop.Id));   // ตัวแรก (ที่ลิงก์ชำระใช้) = ทดสอบ
        await db.PaymentProviderConfigs.Where(c => c.CompanyId == shop.Id && c.Mode == PaymentProviderMode.Test)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.IsActive, false));
        Assert.True(await PaymentChannelReadiness.GatewayLiveAsync(db, shop.Id));    // ตัวที่เหลือ = เงินจริง
    }
}

using System.Security.Claims;
using System.Text.Json;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 250 WP-02b บน PostgreSQL จริง (trait Db · job <c>db-test</c>) — AUDIT_249_COVERAGE M-1/M-2 · AUTOPILOT_BILLING_DESIGN §6.1 P-1/P-2/P-6 · G-T7
/// <para>(1) P-1 เชิญด้วยเลขที่มี/ไม่มีในระบบ ⇒ สถานะ + ไบต์ของคำตอบเท่ากัน · แถวคำเชิญเกิดเฉพาะบริษัทที่มีจริง · ผู้เชิญไม่เห็นแถวรอตอบ ·
/// ทิศตรงข้าม: ผู้รับเชิญเห็นและยอมรับได้ ⇒ คู่ Accepted ผู้เชิญเห็น</para>
/// <para>(2) P-2 เลขเดียวกันสองสาขา ⇒ เชิญ/ส่งเอกสารเข้าสาขาที่ระบุ · ไม่ระบุสาขา ⇒ สำนักงานใหญ่ · คู่เดิมที่ไม่กรอกสาขา (บริษัทเดียว) ยังส่งได้</para>
/// <para>(3) P-6 กำกวม ⇒ ส่งไม่ได้แต่ไม่เงียบ: ป้ายบนเอกสาร + เหตุ · แก้แล้วกดส่งใหม่ ⇒ ส่งได้ + ป้ายหาย</para>
/// <para>(4) M-2 สลิปพอร์ทัล ⇒ PaymentIntent รอยืนยัน ไม่มีแถว Payment · ใบแจ้งยอดไม่ลด · แถวรับชำระจริงยังลด (ทิศตรงข้าม) · migration แถวเก่า</para>
/// </summary>
[Trait("Category", "Db")]
public class PartnerSecurityRound250DbTests
{
    private readonly ITestOutputHelper _out;
    public PartnerSecurityRound250DbTests(ITestOutputHelper output) => _out = output;

    private static readonly Random Rng = new();
    /// <summary>เลขภาษี 13 หลักสุ่ม (ฐานเทสต์ใช้ร่วม — เลขคงที่จะชนบริษัทของเทสต์อื่นแล้วกลายเป็น "กำกวม")</summary>
    private static string RandTax()
    {
        lock (Rng) return "09" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Rng.Next(10))));
    }

    private static CrossTenantWorkflowService Svc(AccountingDbContext db) => new(db, NullLogger<CrossTenantWorkflowService>.Instance);

    private static async Task<Guid> OwnerAsync(AccountingDbContext db, Guid companyId)
    {
        var u = new User { Email = $"r250-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ทดสอบ 250" };
        db.Users.Add(u);
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = companyId, Role = UserRole.Owner });
        await db.SaveChangesAsync();
        return u.Id;
    }

    private static CrossTenantWorkflowController Controller(AccountingDbContext db, Guid userId)
    {
        var ctl = new CrossTenantWorkflowController(Svc(db), db, new PermissionService(db));
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test")),
            },
        };
        return ctl;
    }

    private static int? Status<T>(ActionResult<T> r) => r.Result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    private static string Body<T>(ActionResult<T> r)
        => JsonSerializer.Serialize((r.Result as ObjectResult)?.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static int DataCount<T>(ActionResult<T> r)
        => JsonDocument.Parse(Body(r)).RootElement.GetProperty("data").GetArrayLength();

    private static async Task<Company> CompanyAsync(AccountingDbContext db, string taxId, string? branch, string name)
    {
        var c = new Company { Name = name, TaxId = taxId, BranchCode = branch };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    private static async Task AcceptedAsync(AccountingDbContext db, Guid a, Guid b, TradingPartnershipStatus status = TradingPartnershipStatus.Accepted)
    {
        var (x, y) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
        db.TradingPartnerships.Add(new TradingPartnership { CompanyAId = x, CompanyBId = y, Status = status, InvitedByCompanyId = a });
        await db.SaveChangesAsync();
    }

    private static async Task<Document> InvoiceAsync(AccountingDbContext db, Guid companyId, string taxId, string? branch)
    {
        var contact = new Contact { CompanyId = companyId, Name = "ลูกค้าคู่ค้า 250", TaxId = taxId, BranchCode = branch, IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var doc = new Document
        {
            CompanyId = companyId, ContactId = contact.Id, DocumentType = DocumentType.Invoice, DocumentNumber = "INV-" + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m,
            BalanceDue = 1_070m, Currency = "THB",
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "บริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatAmount = 70m });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    // ───── (1) P-1 ─────

    [Fact]
    public async Task เชิญ_เลขที่มีกับไม่มีในระบบ_คำตอบเท่ากันทุกไบต์_แถวคำเชิญเกิดเฉพาะที่มีจริง_ผู้เชิญไม่เห็นแถวรอตอบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var inviter = await CompanyAsync(db, RandTax(), "00000", "ผู้เชิญ 250");
        var target = await CompanyAsync(db, RandTax(), "00000", "ปลายทาง 250");
        var uid = await OwnerAsync(db, inviter.Id);
        var ctl = Controller(db, uid);

        var known = await ctl.Invite(inviter.Id, new CrossTenantWorkflowController.InviteRequest(target.TaxId, "สวัสดี"));
        var unknownTax = RandTax();
        var unknown = await ctl.Invite(inviter.Id, new CrossTenantWorkflowController.InviteRequest(unknownTax, "สวัสดี"));
        var self = await ctl.Invite(inviter.Id, new CrossTenantWorkflowController.InviteRequest(inviter.TaxId, "สวัสดี"));

        Assert.Equal(202, Status(known));
        Assert.Equal(Status(known), Status(unknown));
        Assert.Equal(Status(known), Status(self));
        Assert.Equal(Body(known), Body(unknown));                      // ไม่มี id/สถานะ/ข้อความ "ไม่พบ" ที่บอกสมาชิกภาพ
        Assert.Equal(Body(known), Body(self));
        Assert.Contains(PartnerCompanyMatch.InviteAckMessage, Body(known));

        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.TradingPartnerships.AsNoTracking()
            .Where(p => p.CompanyAId == inviter.Id || p.CompanyBId == inviter.Id).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(TradingPartnershipStatus.Pending, row.Status);
        Assert.Contains(target.Id, new[] { row.CompanyAId, row.CompanyBId });
        // audit ของผู้เชิญ: 3 แถวรูปเดียวกัน (ไม่มีผลการค้น)
        var audits = await check.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == inviter.Id && a.EntityType == "TradingPartnerInvite").Select(a => a.NewValues!).ToListAsync();
        Assert.Equal(3, audits.Count);
        Assert.All(audits, a => Assert.DoesNotContain(target.Id.ToString(), a));

        // ผู้เชิญไม่เห็นแถวที่ยังไม่ตอบรับ (เห็น = รู้ว่าเลขนี้ใช้ NextAcc)
        Assert.Equal(0, DataCount(await Controller(check, uid).List(inviter.Id)));
        // ทิศตรงข้าม: ปลายทางเห็นคำเชิญ ยอมรับได้ ⇒ ผู้เชิญเห็นคู่ค้า
        var targetUid = await OwnerAsync(check, target.Id);
        Assert.Equal(1, DataCount(await Controller(check, targetUid).List(target.Id)));
        Assert.Equal(200, Status(await Controller(check, targetUid).Accept(target.Id, row.Id)));
        using var after = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, DataCount(await Controller(after, uid).List(inviter.Id)));
    }

    [Fact]
    public async Task เชิญ_เลขภาษีผิดรูป_ปฏิเสธจากรูปแบบ_ไม่ดูฐาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var inviter = await CompanyAsync(db, RandTax(), "00000", "ผู้เชิญผิดรูป 250");
        var uid = await OwnerAsync(db, inviter.Id);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Controller(db, uid).Invite(inviter.Id, new CrossTenantWorkflowController.InviteRequest("-", null)));
        Assert.Equal(PartnerCompanyMatch.InvalidTaxIdMessage, ex.Message);
    }

    // ───── (2) P-2 ─────

    [Fact]
    public async Task เลขเดียวกันสองสาขา_เชิญระบุสาขา_ได้สาขานั้น_ไม่ใช่สำนักงานใหญ่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var tax = RandTax();
        var hq = await CompanyAsync(db, tax, "00000", "คู่ค้า สนญ. 250");
        var br = await CompanyAsync(db, tax, "00001", "คู่ค้า สาขา 1 250");
        var inviter = await CompanyAsync(db, RandTax(), "00000", "ผู้เชิญสาขา 250");
        var uid = await OwnerAsync(db, inviter.Id);

        await Controller(db, uid).Invite(inviter.Id, new CrossTenantWorkflowController.InviteRequest(tax, null, "00001"));

        using var check = DbTestDatabase.TryCreateContext()!;
        var row = await check.TradingPartnerships.AsNoTracking().SingleAsync(p => p.CompanyAId == inviter.Id || p.CompanyBId == inviter.Id);
        Assert.Contains(br.Id, new[] { row.CompanyAId, row.CompanyBId });
        Assert.DoesNotContain(hq.Id, new[] { row.CompanyAId, row.CompanyBId });
    }

    [Fact]
    public async Task ส่งเอกสาร_เลขเดียวกันสองสาขา_ตามสาขาผู้ติดต่อ_ไม่ระบุสาขาได้สำนักงานใหญ่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var tax = RandTax();
        var seller = await CompanyAsync(db, RandTax(), "00000", "ผู้ขาย 250");
        var hq = await CompanyAsync(db, tax, "00000", "ผู้ซื้อ สนญ. 250");
        var br = await CompanyAsync(db, tax, "00001", "ผู้ซื้อ สาขา 1 250");
        await AcceptedAsync(db, seller.Id, hq.Id);
        await AcceptedAsync(db, seller.Id, br.Id);

        var toBranch = await InvoiceAsync(db, seller.Id, tax, "00001");
        var toHq = await InvoiceAsync(db, seller.Id, tax, null);
        var r1 = await Svc(db).RouteSentDocumentAsync(toBranch);
        var r2 = await Svc(db).RouteSentDocumentAsync(toHq);
        Assert.True(r1.Routed); Assert.Null(r1.FailureReason);
        Assert.True(r2.Routed); Assert.Null(r2.FailureReason);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(br.Id, (await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.SourceDocumentId == toBranch.Id)).TargetCompanyId);
        Assert.Equal(hq.Id, (await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.SourceDocumentId == toHq.Id)).TargetCompanyId);
    }

    [Fact]
    public async Task ทิศตรงข้าม_คู่เดิมผู้ติดต่อไม่กรอกสาขา_บริษัทเดียวของเลข_ยังส่งได้_ไม่มีป้าย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var tax = RandTax();
        var seller = await CompanyAsync(db, RandTax(), "00000", "ผู้ขายคู่เดิม 250");
        var buyer = await CompanyAsync(db, tax, "00002", "ผู้ซื้อคู่เดิม 250");
        await AcceptedAsync(db, seller.Id, buyer.Id);
        var doc = await InvoiceAsync(db, seller.Id, tax, null);

        var r = await Svc(db).RouteSentDocumentAsync(doc);
        Assert.True(r.Routed);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(PartnerRoutingFailedNote.Has((await check.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id)).InternalNotes));
    }

    // ───── (3) P-6 ─────

    [Fact]
    public async Task ส่งเข้าคู่ค้ากำกวม_ป้ายบนเอกสารพร้อมเหตุ_แก้แล้วส่งใหม่_ส่งได้และป้ายหาย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var tax = RandTax();
        var seller = await CompanyAsync(db, RandTax(), "00000", "ผู้ขายกำกวม 250");
        var a = await CompanyAsync(db, tax, "00001", "ผู้ซื้อ ก 250");
        var b = await CompanyAsync(db, tax, "00001", "ผู้ซื้อ ข 250");   // สองผู้เช่าเลข+สาขาเดียวกัน (สมัครซ้ำ)
        await AcceptedAsync(db, seller.Id, a.Id);
        await AcceptedAsync(db, seller.Id, b.Id);
        var doc = await InvoiceAsync(db, seller.Id, tax, "00001");
        doc.InternalNotes = "หมายเหตุเดิม";
        await db.SaveChangesAsync();

        var r = await Svc(db).RouteSentDocumentAsync(doc);
        Assert.False(r.Routed);
        Assert.Equal(PartnerCompanyMatch.AmbiguousRouteMessage(tax), r.FailureReason);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var notes = (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id)).InternalNotes;
            Assert.True(PartnerRoutingFailedNote.Has(notes));
            Assert.StartsWith("หมายเหตุเดิม", notes);
            Assert.False(await check.CrossTenantDocumentLinks.AnyAsync(l => l.SourceDocumentId == doc.Id));   // ไม่สุ่มส่งเข้าบริษัทใดบริษัทหนึ่ง
        }

        // ทางไปต่อ: เลิกคู่ที่ซ้ำ แล้วกดส่งใหม่
        var dup = await db.TradingPartnerships.SingleAsync(p => (p.CompanyAId == b.Id || p.CompanyBId == b.Id));
        dup.Status = TradingPartnershipStatus.Rejected;
        await db.SaveChangesAsync();
        var retry = await Svc(db).RetryRoutingAsync(seller.Id, doc.Id);
        Assert.True(retry.Routed);
        Assert.Null(retry.FailureReason);
        using var after = DbTestDatabase.TryCreateContext()!;
        var saved = await after.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id);
        Assert.False(PartnerRoutingFailedNote.Has(saved.InternalNotes));
        Assert.Equal("หมายเหตุเดิม", saved.InternalNotes);
        Assert.Equal(a.Id, (await after.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.SourceDocumentId == doc.Id)).TargetCompanyId);
    }

    // ───── (4) M-2 ─────

    private static PortalService Portal(AccountingDbContext db)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Portal:SigningKey"] = "r250-portal-signing-key-for-db-tests-0123456789",
        }).Build();
        var storage = F4Proxy<Accounting.Services.Storage.IFileStorage>.Create((name, args) =>
        {
            if (name == "get_" + nameof(Accounting.Services.Storage.IFileStorage.Locator))
            {
                var root = Path.GetTempPath();
                return new Accounting.Services.Storage.FileStorageLocator(root, root, root, root);
            }
            if (name == nameof(Accounting.Services.Storage.IFileStorage.WriteAsync)) return Task.CompletedTask;
            return null;
        });
        var intents = new PaymentIntentService(db, new IPaymentProvider[] { new ManualSlipPaymentProvider() },
            Array.Empty<IPaymentCompletionHandler>(), NullLogger<PaymentIntentService>.Instance);
        return new PortalService(db, cfg, storage: storage, intents: intents);
    }

    private static IFormFile Slip()
    {
        var bytes = "%PDF-1.4 r250"u8.ToArray();
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "slip.pdf")
        {
            Headers = new HeaderDictionary(), ContentType = "application/pdf",
        };
    }

    [Fact]
    public async Task สลิปพอร์ทัล_สร้างรายการรอยืนยัน_ไม่มีแถวPayment_ใบแจ้งยอดไม่ลด_แถวรับชำระจริงยังลด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var shop = await CompanyAsync(db, RandTax(), "00000", "ร้านพอร์ทัล 250");
        var doc = await InvoiceAsync(db, shop.Id, RandTax(), null);

        var res = await Portal(db).UploadDocumentSlipAsync(shop.Id, doc.ContactId, doc.Id, Slip(), 500m);
        var again = await Portal(db).UploadDocumentSlipAsync(shop.Id, doc.ContactId, doc.Id, Slip(), 500m);   // ยอดเดิม ⇒ รายการเดิม (ไม่ซ้อน)
        Assert.True(res.AwaitingReview);
        Assert.Equal(res.PaymentIntentId, again.PaymentIntentId);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.Payments.IgnoreQueryFilters().AnyAsync(p => p.DocumentId == doc.Id));   // ไม่มีเงินรับก่อนตรวจ
        var intent = await check.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == shop.Id && i.SourceId == doc.Id);
        Assert.Equal(PaymentSourceKind.Document, intent.SourceKind);
        Assert.Equal(ManualSlipPaymentProvider.Code, intent.ProviderCode);
        Assert.Equal(PaymentMethodKind.ManualSlip, intent.MethodKind);
        Assert.Equal(PaymentIntentStatus.Pending, intent.Status);
        Assert.Equal(500m, intent.Amount);
        Assert.Equal(1_070m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id)).BalanceDue);

        var from = DateTime.UtcNow.Date.AddDays(-1);
        var to = DateTime.UtcNow.Date.AddDays(1);
        Assert.Equal(0m, (await Portal(check).GetMyStatementAsync(shop.Id, doc.ContactId, from, to)).TotalPaid);

        // แถวสลิปรุ่นเก่าที่ยังไม่ตรวจ ⇒ ไม่นับ · แถวรับชำระจริง ⇒ นับ (ทิศตรงข้าม)
        check.Payments.Add(new Payment
        {
            CompanyId = shop.Id, DocumentId = doc.Id, PaymentNumber = "PAY-R250L-" + Guid.NewGuid().ToString("N")[..6], PaymentDate = DateTime.UtcNow.Date,
            PaymentMethod = PaymentMethod.BankTransfer, Amount = 300m, Notes = PortalSlipPending.LegacyMarker + " แนบสลิปจาก Portal ลูกค้า", CreatedBy = PortalSlipPending.LegacyCreatedBy,
        });
        check.Payments.Add(new Payment
        {
            CompanyId = shop.Id, DocumentId = doc.Id, PaymentNumber = "PAY-R250R-" + Guid.NewGuid().ToString("N")[..6], PaymentDate = DateTime.UtcNow.Date,
            PaymentMethod = PaymentMethod.BankTransfer, Amount = 200m, Notes = "รับชำระโอน",
        });
        await check.SaveChangesAsync();
        using var after = DbTestDatabase.TryCreateContext()!;
        var st = await Portal(after).GetMyStatementAsync(shop.Id, doc.ContactId, from, to);
        Assert.Equal(200m, st.TotalPaid);
        var mine = await Portal(after).GetMyPaymentsAsync(shop.Id, doc.ContactId);
        Assert.Equal(200m, Assert.Single(mine).Amount);
    }

    [Fact]
    public async Task migration_แถวสลิปเก่า_ย้ายเป็นรายการรอยืนยัน_แถวที่ต้องให้คนตัดสินไม่แตะ_รันซ้ำไม่ทำซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var shop = await CompanyAsync(db, RandTax(), "00000", "ร้าน migration 250");
        var doc = await InvoiceAsync(db, shop.Id, RandTax(), null);
        var goneDoc = await InvoiceAsync(db, shop.Id, RandTax(), null);
        Payment Legacy(Guid docId, decimal amt) => new()
        {
            CompanyId = shop.Id, DocumentId = docId, PaymentNumber = "PAY-R250M-" + Guid.NewGuid().ToString("N")[..6], PaymentDate = DateTime.UtcNow.Date,
            PaymentMethod = PaymentMethod.BankTransfer, Amount = amt, Notes = PortalSlipPending.LegacyMarker + " แนบสลิปจาก Portal ลูกค้า · ไฟล์: /u/x.jpg",
            CreatedBy = PortalSlipPending.LegacyCreatedBy,
        };
        var movable = Legacy(doc.Id, 400m);
        var review = Legacy(goneDoc.Id, 250m);
        var real = new Payment
        {
            CompanyId = shop.Id, DocumentId = doc.Id, PaymentNumber = "PAY-R250X-" + Guid.NewGuid().ToString("N")[..6], PaymentDate = DateTime.UtcNow.Date,
            PaymentMethod = PaymentMethod.BankTransfer, Amount = 100m, Notes = "รับชำระจริง", CreatedBy = "user",
        };
        db.Payments.AddRange(movable, review, real);
        await db.SaveChangesAsync();
        goneDoc.IsDeleted = true;   // เอกสารต้นทางถูกลบ ⇒ ระบบไม่ย้ายเอง (ต้องให้คนตัดสิน)
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("""CREATE TABLE IF NOT EXISTS "OneShotDataFixes" ("Key" varchar(100) PRIMARY KEY, "AppliedAt" timestamp NOT NULL);""");
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OneShotDataFixes" WHERE "Key" = {0}""", PortalSlipLegacyMigration.MarkerKey);
        var r = await PortalSlipLegacyMigration.MigrateAsync(db);
        Assert.True(r.Moved >= 1);
        Assert.True(r.NeedsReview >= 1);

        using var check = DbTestDatabase.TryCreateContext()!;
        var m = await check.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == movable.Id);
        Assert.True(m.IsDeleted);                                   // soft — แถวยังอยู่
        Assert.StartsWith(PortalSlipPending.LegacyMarker, m.Notes);
        var intent = await check.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == shop.Id && i.SourceId == doc.Id);
        Assert.Equal(PaymentIntentStatus.Pending, intent.Status);
        Assert.Equal(400m, intent.Amount);
        Assert.Equal(ManualSlipPaymentProvider.Code, intent.ProviderCode);
        Assert.Contains(intent.Id.ToString("N"), m.Notes);

        var rv = await check.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == review.Id);
        Assert.False(rv.IsDeleted);                                 // ไม่แตะ
        Assert.False(await check.PaymentIntents.AnyAsync(i => i.CompanyId == shop.Id && i.SourceId == goneDoc.Id));
        Assert.False((await check.Payments.AsNoTracking().SingleAsync(p => p.Id == real.Id)).IsDeleted);   // แถวรับชำระจริงไม่ถูกแตะ

        var events = await check.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == shop.Id && a.EntityType == PortalSlipLegacyMigration.AuditEntityType).Select(a => a.NewValues!).ToListAsync();
        Assert.Contains(events, e => e.Contains("PortalSlipLegacyMovedToIntent") && e.Contains(intent.Id.ToString()));
        Assert.Contains(events, e => e.Contains("PortalSlipLegacyNeedsReview"));

        // รันซ้ำ (marker มีแล้ว) ⇒ ไม่ทำอะไร
        var again = await PortalSlipLegacyMigration.MigrateAsync(check);
        Assert.Equal(new PortalSlipLegacyMigration.Result(0, 0), again);
        Assert.Equal(1, await check.PaymentIntents.CountAsync(i => i.CompanyId == shop.Id && i.SourceId == doc.Id));
    }
}

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
        var ctl = new CrossTenantWorkflowController(Svc(db), db, new PermissionService(db),
            new ChatRateLimiter(db, NullLogger<ChatRateLimiter>.Instance));
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

    /// <param name="discoverable">ฝ่ายค้าน H3: รับคำเชิญได้เฉพาะบริษัทที่เปิด PartnerDiscoverable — เทสต์เชิญตั้งค่าเปิดเป็นค่าตั้งต้น</param>
    private static async Task<Company> CompanyAsync(AccountingDbContext db, string taxId, string? branch, string name, bool discoverable = true)
    {
        var c = new Company { Name = name, TaxId = taxId, BranchCode = branch, PartnerDiscoverable = discoverable };
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
        // ข้อความไทยใน JSON ถูก escape (\uXXXX) — อ่านค่า message จาก JSON ก่อนเทียบ (บทเรียน CI: JSON-escaped Thai)
        Assert.Contains(PartnerCompanyMatch.InviteAckMessage, JsonDocument.Parse(Body(known)).RootElement.GetProperty("message").GetString());

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
        var again = await Portal(db).UploadDocumentSlipAsync(shop.Id, doc.ContactId, doc.Id, Slip(), 500m);   // ฝ่ายค้าน H2: สลิปใหม่ = รายการใหม่ (คนละก้อน)
        Assert.True(res.AwaitingReview);
        Assert.NotEqual(res.PaymentIntentId, again.PaymentIntentId);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.Payments.IgnoreQueryFilters().AnyAsync(p => p.DocumentId == doc.Id));   // ไม่มีเงินรับก่อนตรวจ
        var intent = await check.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == res.PaymentIntentId);
        Assert.Equal(2, await check.PaymentIntents.CountAsync(i => i.CompanyId == shop.Id && i.SourceId == doc.Id));
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
        var otherDoc = await InvoiceAsync(db, shop.Id, RandTax(), null);
        var real = new Payment
        {
            CompanyId = shop.Id, DocumentId = otherDoc.Id, PaymentNumber = "PAY-R250X-" + Guid.NewGuid().ToString("N")[..6], PaymentDate = DateTime.UtcNow.Date,
            PaymentMethod = PaymentMethod.BankTransfer, Amount = 100m, Notes = "รับชำระจริง", CreatedBy = "user",
        };
        db.Payments.AddRange(movable, review, real);
        await db.SaveChangesAsync();
        goneDoc.IsDeleted = true;   // เอกสารต้นทางถูกลบ ⇒ ระบบไม่ย้ายเอง (ต้องให้คนตัดสิน)
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(Accounting.Data.DatabaseMigrationHelper.OneShotDataFixesTableSql);
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

    // ───── ฝ่ายค้านรอบ 250 (H1 · H2 · H3 · M3 · M5) ─────

    [Fact]
    public async Task H1_migration_สลิปเก่าที่มีเงินจริงบันทึกทีหลัง_ต้องให้คนตัดสิน_ไม่สร้างรายการรอยืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var shop = await CompanyAsync(db, RandTax(), "00000", "ร้าน H1 250");
        var doc = await InvoiceAsync(db, shop.Id, RandTax(), null);
        var slipDay = DateTime.UtcNow.Date.AddDays(-3);
        var legacy = new Payment
        {
            CompanyId = shop.Id, DocumentId = doc.Id, PaymentNumber = "PAY-R250H1-" + Guid.NewGuid().ToString("N")[..6], PaymentDate = slipDay,
            PaymentMethod = PaymentMethod.BankTransfer, Amount = 1_070m, Notes = PortalSlipPending.LegacyMarker + " แนบสลิปจาก Portal ลูกค้า · ไฟล์: /u/h1.jpg",
            CreatedBy = PortalSlipPending.LegacyCreatedBy,
        };
        // ร้านบันทึกรับเงินจริงทีหลัง (เงินก้อนเดียวกันกับสลิป)
        var real = new Payment
        {
            CompanyId = shop.Id, DocumentId = doc.Id, PaymentNumber = "PAY-R250H1R-" + Guid.NewGuid().ToString("N")[..6], PaymentDate = slipDay.AddDays(1),
            PaymentMethod = PaymentMethod.BankTransfer, Amount = 1_070m, Notes = "รับชำระโอน", CreatedBy = "user",
        };
        db.Payments.AddRange(legacy, real);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(Accounting.Data.DatabaseMigrationHelper.OneShotDataFixesTableSql);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OneShotDataFixes" WHERE "Key" = {0}""", PortalSlipLegacyMigration.MarkerKey);
        await PortalSlipLegacyMigration.MigrateAsync(db);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False((await check.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == legacy.Id)).IsDeleted);   // ไม่แตะ
        Assert.False(await check.PaymentIntents.AnyAsync(i => i.CompanyId == shop.Id && i.SourceId == doc.Id));                   // ไม่มีอะไรให้กดยืนยันซ้ำ
        Assert.Contains(await check.AuditLogs.AsNoTracking()
                .Where(a => a.CompanyId == shop.Id && a.EntityId == legacy.Id.ToString()).Select(a => a.NewValues!).ToListAsync(),
            v => v.Contains("PortalSlipLegacyNeedsReview") && System.Text.RegularExpressions.Regex.Unescape(v).Contains("ตั้งแต่วันแนบสลิป"));   // NewValues เป็น JSON — ไทยถูก escape
    }

    [Fact]
    public async Task H1_รับชำระก้อนอื่นแล้ว_สลิปรอตรวจที่ยอดเกินยอดค้างถูกติดธง_สลิปที่ยังพอดีไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var shop = await CompanyAsync(db, RandTax(), "00000", "ร้านธงสลิป 250");
        var doc = await InvoiceAsync(db, shop.Id, RandTax(), null);
        var big = await Portal(db).UploadDocumentSlipAsync(shop.Id, doc.ContactId, doc.Id, Slip(), 1_000m);
        var small = await Portal(db).UploadDocumentSlipAsync(shop.Id, doc.ContactId, doc.Id, Slip(), 70m);
        // เงินก้อนอื่นบันทึกแล้ว ⇒ ยอดค้างเหลือ 70
        await db.Documents.Where(d => d.Id == doc.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.PaidAmount, 1_000m).SetProperty(d => d.BalanceDue, 70m));

        Assert.Equal(1, await DocumentSlipIntents.FlagSupersededAsync(db, shop.Id, doc.Id));

        using var check = DbTestDatabase.TryCreateContext()!;
        var b = await check.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == big.PaymentIntentId);
        var s2 = await check.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == small.PaymentIntentId);
        Assert.Equal(DocumentSlipIntents.SupersededCode, b.FailureCode);
        Assert.Equal(PaymentIntentStatus.Pending, b.Status);              // ไม่เปลี่ยนสถานะเอง — คนปฏิเสธ
        Assert.Null(s2.FailureCode);                                      // ทิศตรงข้าม: สลิปที่ยังพอดียอดค้างไม่ถูกแตะ
        Assert.Equal(0, await DocumentSlipIntents.FlagSupersededAsync(check, shop.Id, doc.Id));   // เรียกซ้ำไม่ติดธงซ้ำ
    }

    [Fact]
    public async Task H2_งวดแรกยืนยันแล้ว_สลิปงวดสองรับได้_เกินยอดค้างหรือชำระครบ_ปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var shop = await CompanyAsync(db, RandTax(), "00000", "ร้านงวด 250");
        var doc = await InvoiceAsync(db, shop.Id, RandTax(), null);
        var first = await Portal(db).UploadDocumentSlipAsync(shop.Id, doc.ContactId, doc.Id, Slip(), 500m);
        // งวด 1 ยืนยันแล้ว (สถานะสำเร็จ + ยอดเอกสารลด — แทนผลของ handler)
        await db.PaymentIntents.Where(i => i.Id == first.PaymentIntentId)
            .ExecuteUpdateAsync(x => x.SetProperty(i => i.Status, PaymentIntentStatus.Succeeded));
        await db.Documents.Where(d => d.Id == doc.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.PaidAmount, 500m).SetProperty(d => d.BalanceDue, 570m).SetProperty(d => d.Status, DocumentStatus.PartiallyPaid));

        using var c2 = DbTestDatabase.TryCreateContext()!;
        var second = await Portal(c2).UploadDocumentSlipAsync(shop.Id, doc.ContactId, doc.Id, Slip(), 570m);   // เดิม: "ชำระเรียบร้อยแล้ว"
        Assert.NotEqual(first.PaymentIntentId, second.PaymentIntentId);
        var over = await Assert.ThrowsAsync<BusinessRuleException>(() => Portal(c2).UploadDocumentSlipAsync(shop.Id, doc.ContactId, doc.Id, Slip(), 600m));
        Assert.Equal(DocumentSlipIntents.RuleCode, over.RuleCode);

        await c2.Documents.Where(d => d.Id == doc.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.PaidAmount, 1_070m).SetProperty(d => d.BalanceDue, 0m).SetProperty(d => d.Status, DocumentStatus.Paid));
        using var c3 = DbTestDatabase.TryCreateContext()!;
        await Assert.ThrowsAsync<BusinessRuleException>(() => Portal(c3).UploadDocumentSlipAsync(shop.Id, doc.ContactId, doc.Id, Slip(), null));
    }

    [Fact]
    public async Task H3_ปลายทางไม่เปิดให้ค้นเจอ_ไม่ได้คำเชิญ_บริษัทปลอมที่ไม่เปิดไม่ทำให้กำกวม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var hiddenTax = RandTax();
        var hidden = await CompanyAsync(db, hiddenTax, "00000", "ไม่เปิด 250", discoverable: false);
        var victimTax = RandTax();
        var victim = await CompanyAsync(db, victimTax, "00000", "เหยื่อ 250", discoverable: true);
        var fake = await CompanyAsync(db, victimTax, "00000", "บริษัทปลอมตั้งเลขเหยื่อ 250", discoverable: false);
        var inviter = await CompanyAsync(db, RandTax(), "00000", "ผู้เชิญ H3 250");
        var uid = await OwnerAsync(db, inviter.Id);

        var r1 = await Controller(db, uid).Invite(inviter.Id, new CrossTenantWorkflowController.InviteRequest(hiddenTax, null));
        var r2 = await Controller(db, uid).Invite(inviter.Id, new CrossTenantWorkflowController.InviteRequest(victimTax, null));
        Assert.Equal(Body(r1), Body(r2));

        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.TradingPartnerships.AsNoTracking().Where(p => p.CompanyAId == inviter.Id || p.CompanyBId == inviter.Id).ToListAsync();
        var row = Assert.Single(rows);                                    // ถึงเหยื่อ (เปิด) เท่านั้น · ไม่ถึงบริษัทที่ไม่เปิด
        Assert.Contains(victim.Id, new[] { row.CompanyAId, row.CompanyBId });
        Assert.DoesNotContain(hidden.Id, new[] { row.CompanyAId, row.CompanyBId });
        Assert.DoesNotContain(fake.Id, new[] { row.CompanyAId, row.CompanyBId });
    }

    [Fact]
    public async Task H3_เชิญเกิน20ครั้งต่อวัน_429_ไม่ขึ้นกับปลายทาง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var inviter = await CompanyAsync(db, RandTax(), "00000", "ผู้เชิญรัว 250");
        var uid = await OwnerAsync(db, inviter.Id);
        for (var i = 0; i < CrossTenantWorkflowService.InvitesPerCompanyPerDay; i++)
            Assert.Equal(202, Status(await Controller(db, uid).Invite(inviter.Id, new CrossTenantWorkflowController.InviteRequest(RandTax(), null))));
        Assert.Equal(429, Status(await Controller(db, uid).Invite(inviter.Id, new CrossTenantWorkflowController.InviteRequest(RandTax(), null))));
    }

    [Fact]
    public async Task M5_คู่เชื่อมแล้วเลขตรงแต่สาขาไม่ตรง_ไม่เงียบ_ติดป้ายพร้อมเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var tax = RandTax();
        var seller = await CompanyAsync(db, RandTax(), "00000", "ผู้ขาย M5 250");
        var buyer = await CompanyAsync(db, tax, "00001", "ผู้ซื้อสาขา 1 M5 250");
        await AcceptedAsync(db, seller.Id, buyer.Id);
        var doc = await InvoiceAsync(db, seller.Id, tax, "00000");   // ผู้ติดต่อระบุสำนักงานใหญ่ แต่คู่ค้ามีแต่สาขา 1

        var r = await Svc(db).RouteSentDocumentAsync(doc);
        Assert.False(r.Routed);
        Assert.Equal(PartnerCompanyMatch.BranchMismatchRouteMessage(tax, "00000"), r.FailureReason);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.True(PartnerRoutingFailedNote.Has((await check.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id)).InternalNotes));
    }

    private static DocumentService DocSvc(AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    [Fact]
    public async Task M3_ยกเลิกแถวสลิปเก่าที่ไม่เคยนับ_ไม่ถอยยอดเอกสาร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var shop = await CompanyAsync(db, RandTax(), "00000", "ร้าน M3 250");
        var doc = await InvoiceAsync(db, shop.Id, RandTax(), null);
        await db.Documents.Where(d => d.Id == doc.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.PaidAmount, 500m).SetProperty(d => d.BalanceDue, 570m).SetProperty(d => d.Status, DocumentStatus.PartiallyPaid));
        var legacy = new Payment
        {
            CompanyId = shop.Id, DocumentId = doc.Id, PaymentNumber = "PAY-R250M3-" + Guid.NewGuid().ToString("N")[..6], PaymentDate = DateTime.UtcNow.Date,
            PaymentMethod = PaymentMethod.BankTransfer, Amount = 300m, Notes = PortalSlipPending.LegacyMarker + " แนบสลิปจาก Portal ลูกค้า",
            CreatedBy = PortalSlipPending.LegacyCreatedBy,
        };
        db.Payments.Add(legacy);
        await db.SaveChangesAsync();

        using var svcDb = DbTestDatabase.TryCreateContext()!;
        await DocSvc(svcDb).VoidPaymentAsync(shop.Id, legacy.Id);

        using var check = DbTestDatabase.TryCreateContext()!;
        var d = await check.Documents.AsNoTracking().SingleAsync(x => x.Id == doc.Id);
        Assert.Equal(500m, d.PaidAmount);                                   // เดิม: 500 − 300 = 200 (หักยอดที่ไม่เคยบวก)
        Assert.Equal(570m, d.BalanceDue);
        Assert.True((await check.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == legacy.Id)).IsDeleted);
    }
}

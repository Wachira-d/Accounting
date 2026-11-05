using System.Net;
using Accounting.Controllers;
using Accounting.Helpers;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 239 — ยอมรับใบเสนอราคาออนไลน์พร้อมลายเซ็นผ่านเส้นจริง (<c>PublicQuotationController</c>) บน PostgreSQL:
/// บันทึกคอลัมน์หลักฐาน + ไฟล์ลายเซ็นบนที่เก็บกลาง + แถว audit chain · ยอมรับซ้ำ = สถานะเดิม (ไม่มี audit/ไฟล์ซ้ำ) · ค่าตั้งบังคับลายเซ็น ·
/// ลิงก์หมดอายุ = 404 ทุกเส้น · PDF สาธารณะเอื้อมได้เฉพาะใบของ token (token บริษัทอื่น/สุ่ม/ใบยกเลิก = 404) · ลายเซ็นของบริษัทอื่นอ่านไม่ได้แม้แถวถูกแก้ ·
/// HTML ของเอกสาร (ตัวเรนเดอร์จริง) มีช่องลูกค้ายอมรับ + ลายเซ็น
/// </summary>
[Trait("Category", "Db")]
public class QuotationAcceptanceRound239DbTests
{
    private readonly ITestOutputHelper _out;
    public QuotationAcceptanceRound239DbTests(ITestOutputHelper output) => _out = output;

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "qt239-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static IFileStorage MakeStorage(string root)
        => new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));

    private static async Task<(Guid CompanyId, Document Doc)> SeedAsync(Accounting.Data.AccountingDbContext db, string name,
        DocumentStatus status = DocumentStatus.Approved, DateTime? expires = null, bool requireSignature = false)
    {
        var company = new Company { Name = name, TaxId = "0105556000007" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        if (requireSignature)
        {
            db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id, RequireSignatureForQuotationAccept = true });
            await db.SaveChangesAsync();
        }
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า " + name, IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var doc = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Quotation,
            DocumentNumber = "QT-" + Random.Shared.Next(100000, 999999), Status = status, DocumentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, Currency = "THB",
            QuotationAcceptToken = PublicQuotationController.NewToken(),
            QuotationAcceptTokenExpiresAt = expires ?? DateTime.UtcNow.AddDays(30),
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "งานติดตั้ง", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return (company.Id, doc);
    }

    private static PublicQuotationController Controller(Accounting.Data.AccountingDbContext db, IFileStorage storage,
        IPdfGenerationService? pdf = null)
        => new(db, NullLogger<PublicQuotationController>.Instance, leads: null, pdf: pdf, storage: storage)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.9") },
                    Request = { Headers = { UserAgent = "Mozilla/5.0 (test)" } },
                },
            },
        };

    private static string SignatureUri()
        => QuotationAcceptanceRound239Tests.DataUri(QuotationAcceptanceRound239Tests.DrawnSignature());

    [Fact]
    public async Task ยอมรับพร้อมลายเซ็น_บันทึกหลักฐาน_ไฟล์_auditchain_และยอมรับซ้ำได้สถานะเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var root = TempRoot();
        var storage = MakeStorage(root);
        var (cid, doc) = await SeedAsync(db, "บริษัทยอมรับ239");
        var expectedHash = DocumentSignedContent.Hash(doc, doc.Lines);

        var res = await Controller(db, storage).Accept(doc.QuotationAcceptToken!,
            new PublicQuotationController.AcceptRequest("คุณเอ ใจดี", "ส่งของวันจันทร์", SignatureUri()));
        Assert.IsType<OkObjectResult>(res);

        var row = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id && d.CompanyId == cid);
        Assert.NotNull(row.QuotationAcceptedAt);
        Assert.Equal("คุณเอ ใจดี", row.QuotationAcceptedBy);
        Assert.Equal("ส่งของวันจันทร์", row.QuotationAcceptNote);
        Assert.Equal(expectedHash, row.QuotationAcceptContentHash);
        Assert.Equal("203.0.113.9", row.QuotationAcceptIp);
        Assert.Equal(QuotationAcceptance.Sha256Hex("Mozilla/5.0 (test)"), row.QuotationAcceptUserAgentHash);
        Assert.Equal(QuotationAcceptance.Sha256Hex(doc.QuotationAcceptToken), row.QuotationAcceptTokenHash);
        Assert.Null(row.Notes);   // ไม่ต่อข้อความลง Notes อีก (เนื้อหาเอกสารไม่เปลี่ยนหลังยอมรับ)
        Assert.True(QuotationAcceptance.IsOwnSignatureLocation(row.QuotationAcceptSignaturePath, storage.Locator.AttachmentsRoot, cid, doc.Id));
        var bytes = await storage.ReadAllBytesAsync(row.QuotationAcceptSignaturePath);
        Assert.True(SignatureImageValidator.Validate(Convert.ToBase64String(bytes!)).Ok);

        var audits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == cid && a.EntityType == nameof(Document) && a.EntityId == doc.Id.ToString()
                && a.Action == AuditAction.Approve).ToListAsync();   // แถว Create ของการ seed ไม่นับ (CI รอบ 239)
        var audit = Assert.Single(audits);
        Assert.Equal(AuditAction.Approve, audit.Action);
        Assert.NotNull(audit.RowHash);   // อยู่ใน hash chain ของบริษัท
        Assert.Contains("QuotationAcceptedOnline", audit.NewValues);
        Assert.Contains(expectedHash, audit.NewValues);
        Assert.Equal("203.0.113.9", audit.IpAddress);

        // ยอมรับซ้ำ (ชื่อ/ลายเซ็นใหม่) ⇒ สถานะเดิม · ไม่ทับ · ไม่มี audit/ไฟล์เพิ่ม
        var again = await Controller(db, storage).Accept(doc.QuotationAcceptToken!,
            new PublicQuotationController.AcceptRequest("คนอื่น", null, SignatureUri()));
        Assert.IsType<OkObjectResult>(again);
        var row2 = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id && d.CompanyId == cid);
        Assert.Equal(row.QuotationAcceptedAt, row2.QuotationAcceptedAt);
        Assert.Equal("คุณเอ ใจดี", row2.QuotationAcceptedBy);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.CompanyId == cid && a.EntityId == doc.Id.ToString() && a.Action == AuditAction.Approve));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "uploads", QuotationAcceptance.StorageFolder, cid.ToString("N"))));

        // ตัวเรนเดอร์จริง (HTML): ช่องลูกค้าอนุมัติ = ชื่อ + เวลายอมรับ + รูปลายเซ็น
        // การยอมรับเขียนด้วย ExecuteUpdate (ข้าม change tracker) ⇒ แถว doc ที่ context นี้ติดตามไว้ยังเป็นค่าเก่า — ล้างก่อนเรนเดอร์
        // (คำขอจริงใช้ context ใหม่ทุกครั้ง · CI รอบ 239)
        db.ChangeTracker.Clear();
        var html = await new PdfGenerationService(db, new DocumentTemplateService(db), storage: storage)   // ที่เก็บเดียวกับตอนยอมรับ — ตัวเรนเดอร์อ่านเฉพาะไฟล์ใต้รากของมันเอง
            .GenerateDocumentHtmlAsync(cid, new GeneratePdfRequest(doc.Id, null, null, null, null));
        Assert.Contains("คุณเอ ใจดี", html);
        Assert.Contains("ยอมรับออนไลน์", html);
        Assert.Contains("data:image/png;base64,", html);
    }

    [Fact]
    public async Task ค่าตั้งบังคับลายเซ็น_ไม่เซ็นถูกปฏิเสธ_เซ็นแล้วผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var storage = MakeStorage(TempRoot());
        var (cid, doc) = await SeedAsync(db, "บริษัทบังคับเซ็น239", requireSignature: true);

        var denied = await Controller(db, storage).Accept(doc.QuotationAcceptToken!, new PublicQuotationController.AcceptRequest("คุณบี"));
        Assert.IsType<BadRequestObjectResult>(denied);
        Assert.Null((await db.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id && d.CompanyId == cid)).QuotationAcceptedAt);

        var view = Assert.IsType<OkObjectResult>(await Controller(db, storage).View(doc.QuotationAcceptToken!));
        Assert.Contains("requireSignature = True", view.Value!.GetType().GetProperty("Data")!.GetValue(view.Value)!.ToString());

        var ok = await Controller(db, storage).Accept(doc.QuotationAcceptToken!,
            new PublicQuotationController.AcceptRequest("คุณบี", null, SignatureUri()));
        Assert.IsType<OkObjectResult>(ok);
    }

    [Fact]
    public async Task ลิงก์หมดอายุ_404ทุกเส้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var storage = MakeStorage(TempRoot());
        var called = false;
        var pdf = F4Proxy<IPdfGenerationService>.Create((_, _) => { called = true; return null; });
        var (_, doc) = await SeedAsync(db, "บริษัทหมดอายุ239", expires: DateTime.UtcNow.AddMinutes(-1));
        var c = Controller(db, storage, pdf);
        Assert.IsType<NotFoundObjectResult>(await c.View(doc.QuotationAcceptToken!));
        Assert.IsType<NotFoundObjectResult>(await c.Pdf(doc.QuotationAcceptToken!));
        Assert.IsType<NotFoundObjectResult>(await c.Accept(doc.QuotationAcceptToken!,
            new PublicQuotationController.AcceptRequest("คุณซี", null, SignatureUri())));
        Assert.False(called);
    }

    [Fact]
    public async Task PDFสาธารณะ_เอื้อมได้เฉพาะใบของtoken_บริษัทอื่นและใบยกเลิกได้404()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var storage = MakeStorage(TempRoot());
        var calls = new List<(Guid Company, Guid Doc)>();
        var pdf = F4Proxy<IPdfGenerationService>.Create((name, args) =>
        {
            if (name != nameof(IPdfGenerationService.GenerateDocumentPdfAsync)) return null;
            var req = (GeneratePdfRequest)args[1]!;
            calls.Add(((Guid)args[0]!, req.DocumentId));
            return Task.FromResult(new GeneratePdfResponse(req.DocumentId, "QT", "QT.pdf", "application/pdf", 4,
                new byte[] { 0x25, 0x50, 0x44, 0x46 }, DateTime.UtcNow));
        });
        var (cidA, docA) = await SeedAsync(db, "บริษัทเอ239");
        var (cidB, docB) = await SeedAsync(db, "บริษัทบี239");
        var (_, voided) = await SeedAsync(db, "บริษัทยกเลิก239", status: DocumentStatus.Voided);

        Assert.IsType<FileContentResult>(await Controller(db, storage, pdf).Pdf(docA.QuotationAcceptToken!));
        Assert.IsType<FileContentResult>(await Controller(db, storage, pdf).Pdf(docB.QuotationAcceptToken!));
        Assert.Equal(new[] { (cidA, docA.Id), (cidB, docB.Id) }, calls);

        Assert.IsType<NotFoundObjectResult>(await Controller(db, storage, pdf).Pdf(PublicQuotationController.NewToken()));
        Assert.IsType<NotFoundObjectResult>(await Controller(db, storage, pdf).Pdf(voided.QuotationAcceptToken!));
        Assert.IsType<NotFoundObjectResult>(await Controller(db, storage, pdf).Pdf("../" + docA.Id));
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task ลายเซ็นของบริษัทอื่น_อ่านผ่านtokenไม่ได้_แม้แถวถูกแก้ให้ชี้ไฟล์นั้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var storage = MakeStorage(TempRoot());
        var (cidA, docA) = await SeedAsync(db, "บริษัทเจ้าของลายเซ็น239");
        var (cidB, docB) = await SeedAsync(db, "บริษัทผู้แอบดู239");
        await Controller(db, storage).Accept(docA.QuotationAcceptToken!, new PublicQuotationController.AcceptRequest("คุณเอ", null, SignatureUri()));
        var pathA = (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == docA.Id && d.CompanyId == cidA)).QuotationAcceptSignaturePath;

        // เจ้าของ token อ่านลายเซ็นตัวเองได้
        Assert.IsType<FileContentResult>(await Controller(db, storage).Signature(docA.QuotationAcceptToken!));

        // แถวของบริษัท B ถูกแก้ให้ชี้ไฟล์ของ A ⇒ token ของ B ต้องอ่านไม่ได้
        await db.Documents.Where(d => d.Id == docB.Id && d.CompanyId == cidB).ExecuteUpdateAsync(u => u
            .SetProperty(d => d.QuotationAcceptedAt, DateTime.UtcNow)
            .SetProperty(d => d.QuotationAcceptSignaturePath, pathA));
        Assert.IsType<NotFoundObjectResult>(await Controller(db, storage).Signature(docB.QuotationAcceptToken!));
    }

    // ───────────── ฝ่ายค้านรอบ 239 ─────────────

    /// <summary>ที่เก็บที่เรียกงานแทรกหลังเขียนไฟล์ (จำลองอีกคำขอยอมรับไปก่อน ระหว่าง "อ่านใบ" กับ "จองการยอมรับ")</summary>
    private sealed class HookedStorage : IFileStorage
    {
        private readonly IFileStorage _inner;
        private readonly Func<Task> _afterWrite;
        public HookedStorage(IFileStorage inner, Func<Task> afterWrite) { _inner = inner; _afterWrite = afterWrite; }
        public string ProviderName => _inner.ProviderName;
        public bool IsLocal => _inner.IsLocal;
        public FileStorageLocator Locator => _inner.Locator;
        public async Task WriteAsync(string location, byte[] content, string? contentType = null, CancellationToken ct = default)
        { await _inner.WriteAsync(location, content, contentType, ct); await _afterWrite(); }
        public Task WriteAsync(string location, Stream content, string? contentType = null, CancellationToken ct = default)
            => _inner.WriteAsync(location, content, contentType, ct);
        public Task<byte[]?> ReadAllBytesAsync(string? location, CancellationToken ct = default) => _inner.ReadAllBytesAsync(location, ct);
        public byte[]? ReadAllBytes(string? location) => _inner.ReadAllBytes(location);
        public Task<Stream?> OpenReadAsync(string? location, CancellationToken ct = default) => _inner.OpenReadAsync(location, ct);
        public Task<bool> ExistsAsync(string? location, CancellationToken ct = default) => _inner.ExistsAsync(location, ct);
        public bool Exists(string? location) => _inner.Exists(location);
        public Task<bool> DeleteAsync(string? location, CancellationToken ct = default) => _inner.DeleteAsync(location, ct);
    }

    [Fact]
    public async Task Race_อีกคำขอยอมรับไปก่อนระหว่างทาง_ไม่มีauditของคำขอนี้_และไฟล์ลายเซ็นถูกลบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var root = TempRoot();
        var (cid, doc) = await SeedAsync(db, "บริษัทแข่งกันยอมรับ239");
        var hooked = new HookedStorage(MakeStorage(root), async () =>
        {
            using var other = DbTestDatabase.TryCreateContext()!;
            await other.Documents.Where(d => d.Id == doc.Id && d.CompanyId == cid).ExecuteUpdateAsync(u => u
                .SetProperty(d => d.QuotationAcceptedAt, DateTime.UtcNow)
                .SetProperty(d => d.QuotationAcceptedBy, "คำขอที่มาก่อน"));
        });

        var res = await Controller(db, hooked).Accept(doc.QuotationAcceptToken!,
            new PublicQuotationController.AcceptRequest("คำขอที่มาทีหลัง", null, SignatureUri()));
        Assert.IsType<OkObjectResult>(res);   // ตอบสถานะจริง (ยอมรับแล้ว) ไม่ใช่ error

        var row = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id && d.CompanyId == cid);
        Assert.Equal("คำขอที่มาก่อน", row.QuotationAcceptedBy);
        Assert.Null(row.QuotationAcceptSignaturePath);
        Assert.Equal(0, await db.AuditLogs.CountAsync(a => a.CompanyId == cid && a.EntityId == doc.Id.ToString() && a.Action == AuditAction.Approve));
        var dir = Path.Combine(root, "uploads", QuotationAcceptance.StorageFolder, cid.ToString("N"));
        Assert.True(!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0);
    }

    private static async Task<(Guid CompanyId, Document Doc)> SeedDeliveryAsync(Accounting.Data.AccountingDbContext db, string name)
    {
        var company = new Company { Name = name, TaxId = "0105556000007" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = company.Id, Name = "ผู้รับ " + name, IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var doc = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.DeliveryNote,
            DocumentNumber = "DN-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = DateTime.UtcNow.Date, Currency = "THB",
            DeliverySignToken = PublicQuotationController.NewToken(), DeliverySignTokenExpiresAt = DateTime.UtcNow.AddDays(14),
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "สินค้า", Quantity = 2, UnitPrice = 0m, Amount = 0m });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return (company.Id, doc);
    }

    [Fact]
    public async Task S2_SignDelivery_tokenมั่ว404ก่อนตรวจรูป_เซ็นสำเร็จ_เซ็นซ้ำได้สถานะเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var storage = MakeStorage(TempRoot());
        var (cid, doc) = await SeedDeliveryAsync(db, "บริษัทส่งของ239");

        // token มั่ว + รูปเสีย ⇒ 404 (ไม่ใช่ 400 ของตัวตรวจรูป = ไม่ได้ถอดรูป)
        Assert.IsType<NotFoundObjectResult>(await Controller(db, storage).SignDelivery(PublicQuotationController.NewToken(),
            new PublicQuotationController.DeliverySignRequest("ใคร", "ไม่ใช่รูป")));

        var ok = await Controller(db, storage).SignDelivery(doc.DeliverySignToken!,
            new PublicQuotationController.DeliverySignRequest("คุณรับของ", SignatureUri()));
        Assert.IsType<OkObjectResult>(ok);
        var row = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id && d.CompanyId == cid);
        Assert.Equal("คุณรับของ", row.DeliverySignedBy);
        Assert.StartsWith("data:image/png;base64,", row.DeliverySignatureBase64);

        // เซ็นซ้ำ (รูปเสียด้วย) ⇒ สถานะเดิม ไม่ทับ
        var again = await Controller(db, storage).SignDelivery(doc.DeliverySignToken!,
            new PublicQuotationController.DeliverySignRequest("คนอื่น", "ไม่ใช่รูป"));
        Assert.IsType<OkObjectResult>(again);
        var row2 = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id && d.CompanyId == cid);
        Assert.Equal("คุณรับของ", row2.DeliverySignedBy);
        Assert.Equal(row.DeliverySignedAt, row2.DeliverySignedAt);
    }
}

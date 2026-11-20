using Accounting.Helpers;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>รอบ 278 — ช่องลายเซ็นฝั่งบริษัทสองช่องที่เป็นคนเดียวกัน (ภาพจริง QT-20261119-0001 ที่ระบบภายนอกสร้างผ่าน API key):
/// ช่อง 0 "K.นัท" + ลายเซ็นจาก payload · ช่อง 1 ผู้ออกคีย์ "ณัฐพล ทรัพย์บวรโรจน์" ไม่มีลายเซ็นในโปรไฟล์ ·
/// ตัวรวมอยู่ใน <c>ResolveSignersAsync</c> ⇒ HTML / QuestPDF / พรีวิว / หน้าลูกค้า (PDF) ได้ signers ชุดเดียวกัน</summary>
[Trait("Category", "Db")]
public class SignatureSlotsR278DbTests
{
    private readonly ITestOutputHelper _out;
    public SignatureSlotsR278DbTests(ITestOutputHelper output) => _out = output;

    private static string Sig() => QuotationAcceptanceRound239Tests.DataUri(QuotationAcceptanceRound239Tests.DrawnSignature());

    private static async Task<(Guid Cid, User Nat, Document Doc)> SeedAsync(Accounting.Data.AccountingDbContext db, string? aliases,
        string? preparerName = "K.นัท", Guid? preparerUserId = null)
    {
        var company = new Company { Name = "บริษัท 278 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000007" };
        db.Companies.Add(company);
        var nat = new User { Email = $"r278-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ณัฐพล ทรัพย์บวรโรจน์", SignerAliases = aliases };
        db.Users.Add(nat);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = nat.Id, CompanyId = company.Id, Role = UserRole.Owner });
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า 278", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var doc = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Quotation,
            DocumentNumber = "QT-20261119-" + Random.Shared.Next(1000, 9999), Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, Currency = "THB",
            // API key route: CreatedBy/UpdatedBy = ผู้ออกคีย์ · ผู้จัดทำจาก payload
            CreatedBy = nat.Id.ToString(), UpdatedBy = nat.Id.ToString(),
            PreparerName = preparerName, PreparerSignatureBase64 = Sig(), PreparerUserId = preparerUserId,
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "งานติดตั้ง", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return (company.Id, nat, doc);
    }

    private static PdfGenerationService Pdf(Accounting.Data.AccountingDbContext db) => new(db, new DocumentTemplateService(db));

    [Fact]
    public async Task ผูกชื่อเล่นในโปรไฟล์แล้ว_สองช่องได้ลายเซ็นและชื่อเต็มเดียวกัน_ทั้งHTMLและQuestPDF()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, _, doc) = await SeedAsync(db, aliases: "K.นัท");

        var signers = await Pdf(db).ResolveSignersAsync(doc);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", signers[0].Name);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", signers[1].Name);
        Assert.NotNull(signers[1].SignatureImageBytes);
        Assert.Equal(signers[0].SignatureImageBytes, signers[1].SignatureImageBytes);   // รูปของคนเดียวกัน

        db.ChangeTracker.Clear();
        var html = await Pdf(db).GenerateDocumentHtmlAsync(cid, new GeneratePdfRequest(doc.Id, null, null, null, null));
        Assert.DoesNotContain("K.นัท", html);
        var b64 = Sig()["data:image/png;base64,".Length..];
        Assert.True(CountOf(html, b64) >= 2, "รูปลายเซ็นต้องอยู่ทั้งช่องผู้เสนอราคาและผู้มีอำนาจลงนาม");
        // QuestPDF รับ signers จาก ResolveSignersAsync ตัวเดียวกัน — ต้องเรนเดอร์ได้
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        db.ChangeTracker.Clear();
        var pdf = await Pdf(db).GenerateDocumentPdfAsync(cid, new GeneratePdfRequest(doc.Id, null, null, null, null));
        Assert.True(pdf.PdfData.Length > 1000);
    }

    [Fact]
    public async Task ชื่อคล้ายแต่ไม่มีกุญแจ_ไม่รวม_คงหน้าตาเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (_, _, doc) = await SeedAsync(db, aliases: null);
        var signers = await Pdf(db).ResolveSignersAsync(doc);
        Assert.Equal("K.นัท", signers[0].Name);
        Assert.NotNull(signers[0].SignatureImageBytes);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", signers[1].Name);
        Assert.Null(signers[1].SignatureImageBytes);   // ห้ามใส่ลายเซ็นของคนหนึ่งลงช่องของอีกคน
    }

    [Fact]
    public async Task ผูกด้วยPreparerUserIdของคนอื่น_ไม่รวมแม้ชื่อเล่นตรง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var someoneElse = new User { Email = $"r278-o-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "พนักงานขาย" };
        db.Users.Add(someoneElse);
        await db.SaveChangesAsync();
        var (_, _, doc) = await SeedAsync(db, aliases: "K.นัท", preparerUserId: someoneElse.Id);
        var signers = await Pdf(db).ResolveSignersAsync(doc);
        Assert.Equal("K.นัท", signers[0].Name);
        Assert.Null(signers[1].SignatureImageBytes);
    }

    [Fact]
    public async Task ผูกด้วยPreparerUserId_รวมได้โดยไม่ต้องตั้งชื่อเล่น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (_, nat, doc) = await SeedAsync(db, aliases: null);
        doc.PreparerUserId = nat.Id;
        await db.SaveChangesAsync();
        var signers = await Pdf(db).ResolveSignersAsync(doc);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", signers[0].Name);
        Assert.NotNull(signers[1].SignatureImageBytes);
    }

    [Fact]
    public async Task ใบร่าง_ไม่มีผู้มีอำนาจลงนาม_ไม่รวม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (_, _, doc) = await SeedAsync(db, aliases: "K.นัท");
        doc.Status = DocumentStatus.Draft;
        await db.SaveChangesAsync();
        var signers = await Pdf(db).ResolveSignersAsync(doc);
        Assert.Null(signers[1].SignatureImageBytes);   // ใบที่ยังไม่อนุมัติห้ามมีหลักฐานลงนามช่องผู้มีอำนาจ
        Assert.Equal("K.นัท", signers[0].Name);
    }

    [Fact]
    public async Task กุญแจทางAPI_อีเมลและIdของสมาชิกเท่านั้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, nat, _) = await SeedAsync(db, aliases: null);
        var docs = new DocumentService(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);
        Assert.Null(await docs.ResolvePreparerUserIdAsync(cid, null, " "));
        Assert.Equal(nat.Id, await docs.ResolvePreparerUserIdAsync(cid, null, nat.Email.ToUpperInvariant()));
        Assert.Equal(nat.Id, await docs.ResolvePreparerUserIdAsync(cid, nat.Id, nat.Email));
        var outsider = new User { Email = $"r278-x-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "คนนอก" };
        db.Users.Add(outsider);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => docs.ResolvePreparerUserIdAsync(cid, outsider.Id, null));
        await Assert.ThrowsAsync<BusinessRuleException>(() => docs.ResolvePreparerUserIdAsync(cid, null, outsider.Email));
        await Assert.ThrowsAsync<BusinessRuleException>(() => docs.ResolvePreparerUserIdAsync(cid, nat.Id, "nobody@test.local"));
    }

    private static int CountOf(string hay, string needle)
    {
        int n = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}

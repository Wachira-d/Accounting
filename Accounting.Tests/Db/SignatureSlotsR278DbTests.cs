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
/// ผลแช่แข็งตอนออกเอกสาร (StampSignerFreeze) · ตัวรวมอยู่ใน <c>ResolveSignersAsync</c> ⇒ HTML / QuestPDF / พรีวิว / หน้าลูกค้า ได้ signers ชุดเดียวกัน ·
/// ฝ่ายค้าน HIGH-1 (ไม่คัดลอกรูป payload เข้าช่องผู้มีอำนาจลงนาม) · HIGH-2 (ชื่อเล่นต่อบริษัท ต้องเจ้าของอนุมัติ) · HIGH-3 (แช่แข็ง + ปุ่มปรับ)</summary>
[Trait("Category", "Db")]
public class SignatureSlotsR278DbTests
{
    private readonly ITestOutputHelper _out;
    public SignatureSlotsR278DbTests(ITestOutputHelper output) => _out = output;

    private static string Sig() => QuotationAcceptanceRound239Tests.DataUri(QuotationAcceptanceRound239Tests.DrawnSignature());
    private static string Sig2() => QuotationAcceptanceRound239Tests.DataUri(QuotationAcceptanceRound239Tests.DrawnSignature(300, 120));

    private sealed record Seed(Guid Cid, User Nat, Contact Contact);

    private static async Task<Seed> SeedCompanyAsync(Accounting.Data.AccountingDbContext db, string? natProfileSig = null)
    {
        var company = new Company { Name = "บริษัท 278 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000007" };
        db.Companies.Add(company);
        var nat = new User { Email = $"r278-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ณัฐพล ทรัพย์บวรโรจน์", SignatureImageBase64 = natProfileSig };
        db.Users.Add(nat);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = nat.Id, CompanyId = company.Id, Role = UserRole.Owner });
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า 278", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return new Seed(company.Id, nat, contact);
    }

    private static async Task ApprovedAliasAsync(Accounting.Data.AccountingDbContext db, Seed s, string alias, bool approved = true)
    {
        db.SignerAliases.Add(new SignerAlias
        {
            CompanyId = s.Cid, UserId = s.Nat.Id, Alias = alias, NormalizedAlias = SignatureSlotIdentity.NormalizeName(alias)!,
            ApprovedAt = approved ? DateTime.UtcNow : null, ApprovedByUserId = approved ? s.Nat.Id : null,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>ใบเสนอราคาจากเส้น API key — ออกแล้วตอนบันทึก ⇒ ผู้ลงนามถูกแช่แข็งที่นี่</summary>
    private static async Task<Document> QtAsync(Accounting.Data.AccountingDbContext db, Seed s, string? payloadSig = "default",
        DocumentStatus status = DocumentStatus.Approved, Guid? preparerUserId = null)
    {
        var doc = new Document
        {
            CompanyId = s.Cid, ContactId = s.Contact.Id, DocumentType = DocumentType.Quotation,
            DocumentNumber = "QT-20261119-" + Random.Shared.Next(1000, 9999), Status = status, DocumentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, Currency = "THB",
            CreatedBy = s.Nat.Id.ToString(), UpdatedBy = s.Nat.Id.ToString(),
            PreparerName = "K.นัท", PreparerSignatureBase64 = payloadSig == "default" ? Sig() : payloadSig, PreparerUserId = preparerUserId,
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "งานติดตั้ง", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    private static PdfGenerationService Pdf(Accounting.Data.AccountingDbContext db) => new(db, new DocumentTemplateService(db));

    [Fact]
    public async Task ชื่อเล่นอนุมัติแล้ว_ชื่อเต็มทั้งสองช่อง_กรรมการไม่มีรูป_payloadมีรูป_ช่องกรรมการยังไม่มีรูป()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedCompanyAsync(db);
        await ApprovedAliasAsync(db, s, "K.นัท");
        var doc = await QtAsync(db, s);
        Assert.NotNull(doc.SignersFrozenAt);
        Assert.Contains(SignatureSlotIdentity.KindApprovedAlias, doc.SignerSnapshotJson);

        var signers = await Pdf(db).ResolveSignersAsync(doc);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", signers[0].Name);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", signers[1].Name);
        Assert.NotNull(signers[0].SignatureImageBytes);   // รูป payload อยู่ช่องผู้จัดทำได้
        Assert.Null(signers[1].SignatureImageBytes);      // HIGH-1: ไม่คัดลอกรูป payload เข้าช่องผู้มีอำนาจลงนาม
        Assert.Null(signers[1].SignatureImageDataUri);

        db.ChangeTracker.Clear();
        var html = await Pdf(db).GenerateDocumentHtmlAsync(s.Cid, new GeneratePdfRequest(doc.Id, null, null, null, null));
        Assert.DoesNotContain("K.นัท", html);
        Assert.Equal(1, CountOf(html, Sig()["data:image/png;base64,".Length..]));   // รูป payload ที่เดียว
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        db.ChangeTracker.Clear();
        var pdf = await Pdf(db).GenerateDocumentPdfAsync(s.Cid, new GeneratePdfRequest(doc.Id, null, null, null, null));
        Assert.True(pdf.PdfData.Length > 1000);
    }

    [Fact]
    public async Task ทิศกลับ_ช่องผู้จัดทำไม่มีรูป_ใช้รูปโปรไฟล์ของผู้ใช้ที่ผูก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, natProfileSig: Sig2());
        await ApprovedAliasAsync(db, s, "K.นัท");
        var doc = await QtAsync(db, s, payloadSig: null);
        var signers = await Pdf(db).ResolveSignersAsync(doc);
        Assert.NotNull(signers[0].SignatureImageBytes);
        Assert.Equal(signers[1].SignatureImageBytes, signers[0].SignatureImageBytes);   // รูปโปรไฟล์ของคนเดียวกัน
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", signers[0].Name);
    }

    [Fact]
    public async Task ชื่อเล่นยังไม่อนุมัติ_และไม่มีกุญแจ_ไม่รวม_คงหน้าตาเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db);
        await ApprovedAliasAsync(db, s, "K.นัท", approved: false);
        var doc = await QtAsync(db, s);
        var signers = await Pdf(db).ResolveSignersAsync(doc);
        Assert.Equal("K.นัท", signers[0].Name);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", signers[1].Name);
        Assert.Null(signers[1].SignatureImageBytes);
    }

    [Fact]
    public async Task ใบที่ออกก่อนอนุมัติชื่อเล่น_ไม่เปลี่ยนย้อนหลัง_ปุ่มปรับของเจ้าของต้องมีเหตุผล_ลูกค้าเซ็นแล้วปรับไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db);
        var doc = await QtAsync(db, s);   // ออกก่อนมีชื่อเล่น ⇒ snapshot ว่าง
        await ApprovedAliasAsync(db, s, "K.นัท");
        Assert.Equal("K.นัท", (await Pdf(db).ResolveSignersAsync(doc))[0].Name);   // HIGH-3: ไม่ใช้กติกาใหม่ย้อนหลัง

        var svc = new SignerIdentityService(db);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RefreshDocumentSignersAsync(s.Cid, s.Nat.Id, doc.Id, " "));
        var r = await svc.RefreshDocumentSignersAsync(s.Cid, s.Nat.Id, doc.Id, "ผูกชื่อเล่นผู้เสนอราคาแล้ว");
        Assert.Equal(1, r.Candidates);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.CompanyId == s.Cid && a.EntityId == doc.Id.ToString() && a.NewValues!.Contains(SignatureSlotIdentity.RuleCodeRefresh)));
        db.ChangeTracker.Clear();
        var fresh = await db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == doc.Id);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", (await Pdf(db).ResolveSignersAsync(fresh))[0].Name);

        // ลูกค้ายอมรับแล้ว (แบบ QT ของเจ้าของ) ⇒ ปรับไม่ได้
        var accepted = await QtAsync(db, s);
        accepted.QuotationAcceptedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RefreshDocumentSignersAsync(s.Cid, s.Nat.Id, accepted.Id, "ขอปรับผู้ลงนาม"));
    }

    [Fact]
    public async Task ใบที่ออกก่อนรอบนี้_snapshotว่าง_ไม่รวม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db);
        await ApprovedAliasAsync(db, s, "K.นัท");
        var doc = await QtAsync(db, s);
        doc.SignerSnapshotJson = SignatureSlotIdentity.EmptySnapshotJson;   // สิ่งที่ Round278Migration เขียนให้ใบเก่า
        await db.SaveChangesAsync();
        Assert.Equal("K.นัท", (await Pdf(db).ResolveSignersAsync(doc))[0].Name);
    }

    [Fact]
    public async Task ผูกด้วยPreparerUserIdของคนอื่น_ไม่รวมแม้ชื่อเล่นตรง_และใบร่างไม่แช่แข็ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db);
        await ApprovedAliasAsync(db, s, "K.นัท");
        var other = new User { Email = $"r278-o-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "พนักงานขาย สมหญิง" };
        db.Users.Add(other);
        await db.SaveChangesAsync();
        var doc = await QtAsync(db, s, preparerUserId: other.Id);
        Assert.Equal("K.นัท", (await Pdf(db).ResolveSignersAsync(doc))[0].Name);

        var draft = await QtAsync(db, s, status: DocumentStatus.Draft);
        Assert.Null(draft.SignerSnapshotJson);
        var signers = await Pdf(db).ResolveSignersAsync(draft);
        Assert.Equal("K.นัท", signers[0].Name);
        Assert.Null(signers[1].SignatureImageBytes);
    }

    [Fact]
    public async Task ชื่อเล่น_ชื่อของคนอื่นหรือคำกลาง_ถูกปฏิเสธ_ของสมาชิกรออนุมัติ_เจ้าของอนุมัติได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db);   // A = ณัฐพล (เจ้าของ)
        var b = new User { Email = $"r278-b-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "บี พนักงาน" };
        db.Users.Add(b);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = b.Id, CompanyId = s.Cid, Role = UserRole.Staff });
        await db.SaveChangesAsync();
        var svc = new SignerIdentityService(db);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.SaveMineAsync(s.Cid, b.Id, new SignerIdentityService.SaveMyRequest("ณัฐพล ทรัพย์บวรโรจน์", null)));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.SaveMineAsync(s.Cid, b.Id, new SignerIdentityService.SaveMyRequest("ฝ่ายบัญชี", null)));

        var mine = await svc.SaveMineAsync(s.Cid, b.Id, new SignerIdentityService.SaveMyRequest("บีบี", true));
        Assert.Equal(SignerIdentityService.StatusPending, Assert.Single(mine.Aliases).Status);
        Assert.True(mine.AllowExternalPreparerBinding);
        // A ตั้งชื่อเล่นซ้ำกับของ B (รออนุมัติ) ⇒ ปฏิเสธ
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.SaveMineAsync(s.Cid, s.Nat.Id, new SignerIdentityService.SaveMyRequest("บีบี", null)));

        var pending = await svc.PendingAsync(s.Cid);
        var p = Assert.Single(pending);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.DecideAsync(s.Cid, b.Id, p.Id, true));   // ไม่ใช่เจ้าของ
        Assert.Empty(await svc.DecideAsync(s.Cid, s.Nat.Id, p.Id, true));
        Assert.Equal(SignerIdentityService.StatusApproved, Assert.Single((await svc.GetMineAsync(s.Cid, b.Id)).Aliases).Status);
        // audit ของแถวชื่อเล่น (เพิ่ม + อนุมัติ)
        Assert.True(await db.AuditLogs.CountAsync(a => a.CompanyId == s.Cid && a.EntityType == nameof(SignerAlias)) >= 2);
        // ลบชื่อเล่น ⇒ ยกเลิกทันที
        Assert.Empty((await svc.SaveMineAsync(s.Cid, b.Id, new SignerIdentityService.SaveMyRequest("", null))).Aliases);
    }

    [Fact]
    public async Task กุญแจทางAPI_ผู้เรียกเองหรือผู้ที่เปิดสิทธิ์เท่านั้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db);
        var d = new User { Email = $"r278-d-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "กรรมการ ดี" };
        db.Users.Add(d);
        await db.SaveChangesAsync();
        var du = new CompanyUser { UserId = d.Id, CompanyId = s.Cid, Role = UserRole.Accountant };
        db.CompanyUsers.Add(du);
        await db.SaveChangesAsync();
        var docs = new DocumentService(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);
        var keyOwner = s.Nat.Id.ToString();

        Assert.Null(await docs.ResolvePreparerUserIdAsync(s.Cid, null, " ", keyOwner));
        Assert.Equal(s.Nat.Id, await docs.ResolvePreparerUserIdAsync(s.Cid, null, s.Nat.Email.ToUpperInvariant(), keyOwner));   // ผู้ออกคีย์เอง
        await Assert.ThrowsAsync<BusinessRuleException>(() => docs.ResolvePreparerUserIdAsync(s.Cid, d.Id, null, keyOwner));   // D ไม่ได้เปิดสิทธิ์
        du.AllowExternalPreparerBinding = true;
        await db.SaveChangesAsync();
        Assert.Equal(d.Id, await docs.ResolvePreparerUserIdAsync(s.Cid, d.Id, d.Email, keyOwner));

        var outsider = new User { Email = $"r278-x-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "คนนอก" };
        db.Users.Add(outsider);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => docs.ResolvePreparerUserIdAsync(s.Cid, outsider.Id, null, outsider.Id.ToString()));
        await Assert.ThrowsAsync<BusinessRuleException>(() => docs.ResolvePreparerUserIdAsync(s.Cid, s.Nat.Id, "nobody@test.local", keyOwner));
    }

    private static int CountOf(string hay, string needle)
    {
        int n = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}

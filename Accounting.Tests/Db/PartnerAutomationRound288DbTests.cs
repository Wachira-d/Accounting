using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Scope = Accounting.Helpers.PartnerConsent.Scope;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 288 (คำสั่งเจ้าของ "ทำให้ใช้งานได้ทั้งหมด") บน PostgreSQL จริง — ท่อฝั่งผู้ซื้อ <see cref="PartnerPayablePipeline"/>:
/// (1) <c>AutoApprovePurchase</c>: ยินยอม + ในเพดาน + ผังมั่นใจ ⇒ อนุมัติผ่านเส้นอนุมัติตัวเดียว (ด้วย GUID ผู้ให้ · Unattended · ไม่เรียก AI) ·
/// เกินเพดาน/ผังไม่มั่นใจ/ใบกำกับไม่ครบ/มีคำเตือน/ไม่ยินยอม/ผู้ให้หมดสิทธิ์ ⇒ ร่าง + เหตุ (หมายเหตุ/audit ครั้งเดียวต่อเหตุ) · ประวัติผู้ขายทำให้ผังมั่นใจ ·
/// (2) <c>ReceiveSlipCopy</c>: ยินยอม ⇒ สำเนารูปสลิปของรายการนั้นแนบใบซื้อของผู้ซื้อ · ไม่ยินยอม ⇒ ไม่ส่ง · ไฟล์บริษัทอื่นไม่ถูกหยิบ · tenant isolation
/// <para>เส้นอนุมัติใช้ตัวปลอมของ <c>IDocumentService</c> ที่บันทึกการเรียก (เส้นจริง = <c>DocumentService.ApproveDocumentAsync</c> ล็อกจุดเรียกด้วย required_call_site_check)</para>
/// </summary>
[Trait("Category", "Db")]
public class PartnerAutomationRound288DbTests
{
    private readonly ITestOutputHelper _out;
    public PartnerAutomationRound288DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static readonly Random Rng = new();

    /// <summary>เลขผู้เสียภาษี 13 หลักที่ checksum ถูก (ผู้ขายต้องผ่าน ม.86/4)</summary>
    private static string ValidTax()
    {
        int[] d;
        lock (Rng) d = new[] { 0, 1 }.Concat(Enumerable.Range(0, 10).Select(_ => Rng.Next(10))).ToArray();
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += d[i] * (13 - i);
        return string.Concat(d) + ((11 - sum % 11) % 10);
    }

    private sealed record Seed(Company Seller, Company Buyer, Guid PartnershipId, Contact BuyerAtSeller, Guid OwnerB, Guid ExpenseB);

    private static CrossTenantWorkflowService Xt(AccountingDbContext db) => new(db, NullLogger<CrossTenantWorkflowService>.Instance);
    private static PartnerConsentService Consent(AccountingDbContext db) => new(db, new PermissionService(db), NullLogger<PartnerConsentService>.Instance);

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        await DbTestDatabase.ApplyDdlAsync(db, PartnerConsentMigration.Statements().Concat(Round288Migration.Statements()));
        var seller = new Company { Name = "ผู้ขาย 288 " + Guid.NewGuid().ToString("N")[..6], TaxId = ValidTax(), BranchCode = "00000", PartnerDiscoverable = true,
            Address = "1 ถนนทดสอบ กรุงเทพฯ 10110" };
        var buyer = new Company { Name = "ผู้ซื้อ 288 " + Guid.NewGuid().ToString("N")[..6], TaxId = ValidTax(), BranchCode = "00000" };
        db.Companies.AddRange(seller, buyer);
        await db.SaveChangesAsync();
        var (a, b) = seller.Id.CompareTo(buyer.Id) < 0 ? (seller.Id, buyer.Id) : (buyer.Id, seller.Id);
        var p = new TradingPartnership { CompanyAId = a, CompanyBId = b, InvitedByCompanyId = seller.Id, Status = TradingPartnershipStatus.Accepted, AcceptedAt = DateTime.UtcNow.AddDays(-1) };
        db.TradingPartnerships.Add(p);
        db.WorkflowAutomationConfigs.Add(new WorkflowAutomationConfig { CompanyId = buyer.Id, CreatePurchaseDraftsFromPartners = true });
        var contact = new Contact { CompanyId = seller.Id, Name = buyer.Name, TaxId = buyer.TaxId, BranchCode = "00000", IsCustomer = true };
        db.Contacts.Add(contact);
        var expense = new ChartOfAccount { CompanyId = buyer.Id, AccountCode = "53100", AccountName = "ค่าบริการ", AccountType = AccountType.Expense, Level = 4, IsActive = true };
        db.ChartOfAccounts.Add(expense);
        var u = new User { Email = $"r288-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของผู้ซื้อ 288" };
        db.Users.Add(u);
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = buyer.Id, Role = UserRole.Owner });
        await db.SaveChangesAsync();
        return new Seed(seller, buyer, p.Id, contact, u.Id, expense.Id);
    }

    /// <summary>ใบแจ้งหนี้ของผู้ขาย → ใบซื้อร่างของผู้ซื้อ (เส้นเดิม) · บรรทัดได้ผัง 53100 + แถวคำแนะนำของโมเดลในบ้าน (ความมั่นใจ <paramref name="localConf"/> · null = ไม่มีแถว)</summary>
    private static async Task<(Document Invoice, Guid PurchaseId, Guid LinkId)> RoutedAsync(AccountingDbContext db, Seed s, decimal total = 1_070m, decimal? localConf = 0.92m)
    {
        var sub = Math.Round(total / 1.07m, 2, MidpointRounding.AwayFromZero);
        var inv = new Document
        {
            CompanyId = s.Seller.Id, ContactId = s.BuyerAtSeller.Id, DocumentType = DocumentType.Invoice, DocumentNumber = "S288-" + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, SubTotal = sub, VatAmount = total - sub, TotalAmount = total, BalanceDue = total, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการรายเดือน", Quantity = 1, UnitPrice = sub, Amount = sub, VatRate = 7m, VatAmount = total - sub });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        var link = await Xt(db).OnDocumentSentAsync(inv);
        using var c = DbTestDatabase.TryCreateContext()!;
        var pid = await c.Documents.AsNoTracking().Where(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId == link!.Id).Select(d => d.Id).SingleAsync();
        Guid? fb = null;
        if (localConf is decimal conf)
        {
            var row = new AiSuggestionFeedback
            {
                CompanyId = s.Buyer.Id, FeatureKey = AiFeatureKey.GlAccountSuggestion.ToString(), PromptHash = Guid.NewGuid().ToString("N"), PromptJson = "{}",
                LocalModelAnswer = "53100", LocalModelConfidence = conf,
            };
            c.AiSuggestionFeedbacks.Add(row);
            await c.SaveChangesAsync();
            fb = row.Id;
        }
        await c.DocumentLines.Where(l => l.DocumentId == pid).ExecuteUpdateAsync(x => x.SetProperty(l => l.AccountId, (Guid?)s.ExpenseB).SetProperty(l => l.GlAccountAiFeedbackId, fb));
        return (inv, pid, link!.Id);
    }

    private static async Task GrantAsync(Seed s, Scope scope, decimal? max)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var pv = await Consent(c).PreviewAsync(s.Buyer.Id, s.OwnerB, new PartnerConsentService.PreviewRequest(s.PartnershipId, scope.ToString(), max, null));
        await Consent(c).GrantAsync(s.Buyer.Id, s.OwnerB, new PartnerConsentService.GrantRequest(s.PartnershipId, scope.ToString(), pv.MaxAmount, null,
            pv.TextVersion, pv.EvidenceHash, PartnerConsent.ConfirmPhrase));
    }

    private sealed record ApproveCall(Guid DocumentId, string ApprovedBy, ApprovalAckSource Source, bool WithAiHints);

    /// <summary>IDocumentService ปลอม — คำเตือนก่อนอนุมัติตาม <paramref name="warnings"/> · อนุมัติ = ออกเลขแทนร่าง + สถานะอนุมัติ (แถวจริงของผู้ซื้อ)</summary>
    private static IDocumentService Docs(List<ApproveCall> calls, Func<Guid, string[]>? warnings = null) => F4Proxy<IDocumentService>.Create((m, args) =>
    {
        if (m == nameof(IDocumentService.PreviewApprovalWarningsAsync))
            return (IReadOnlyList<string>)(warnings?.Invoke((Guid)args[1]!) ?? Array.Empty<string>());
        if (m != nameof(IDocumentService.ApproveDocumentAsync)) return null;
        var id = (Guid)args[1]!;
        calls.Add(new ApproveCall(id, (string)args[2]!, (ApprovalAckSource)args[3]!, (bool)args[4]!));
        async Task<Accounting.Models.DTOs.Document.DocumentResponse> Run()
        {
            using var other = DbTestDatabase.TryCreateContext()!;
            var number = "PI288-" + Random.Shared.Next(100000, 999999);
            await other.Documents.Where(d => d.Id == id).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Approved)
                .SetProperty(d => d.DocumentNumber, number));
            return null!;
        }
        return Run();
    });

    private sealed class PipelineScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new PipelineScope();
        private sealed class PipelineScope : IServiceScope, IServiceProvider
        {
            private readonly AccountingDbContext _db = DbTestDatabase.TryCreateContext()!;
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type t) => t == typeof(AccountingDbContext) ? _db : t == typeof(PartnerConsentService) ? Consent(_db) : null;
            public void Dispose() => _db.Dispose();
        }
    }

    private static PartnerPayablePipeline Pipe(AccountingDbContext db, IDocumentService docs, IFileStorage? storage = null)
        => new(db, docs, new PipelineScopes(), NullLogger<PartnerPayablePipeline>.Instance, null, null, null, storage);

    private static Task ResetWatermarkAsync(AccountingDbContext db, Guid linkId)
        => db.CrossTenantDocumentLinks.Where(l => l.Id == linkId).ExecuteUpdateAsync(x => x.SetProperty(l => l.AutoApproveEvaluatedAt, (DateTime?)null));

    private static async Task<List<string>> DocEventsAsync(AccountingDbContext db, Guid companyId, Guid docId, string evt)
        => (await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId && a.EntityType == "Document" && a.EntityId == docId.ToString()).Select(a => a.NewValues!).ToListAsync())
            .Where(v => v.Contains("\"event\":\"" + evt + "\"")).ToList();

    [Fact]
    public async Task อนุมัติเองในเพดาน_ด้วยGUIDผู้ให้_ไม่เรียกAI_เกินเพดานคงเป็นร่างพร้อมเหตุ_ไม่สแปม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (_, inCap, inCapLink) = await RoutedAsync(db, s, 1_070m);
        var (_, overCap, overLink) = await RoutedAsync(db, s, 5_350m);
        await GrantAsync(s, Scope.AutoApprovePurchase, 2_000m);
        var calls = new List<ApproveCall>();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c1, Docs(calls)).AutoApproveDraftsAsync(s.Buyer.Id, DateTime.UtcNow));
        var call = Assert.Single(calls);
        Assert.Equal(inCap, call.DocumentId);
        Assert.Equal(s.OwnerB.ToString(), call.ApprovedBy);           // ผู้อนุมัติ = GUID ผู้ให้ความยินยอม (ไม่ใช่สตริงระบบ)
        Assert.Equal(ApprovalAckSource.Unattended, call.Source);      // ไม่ประทับว่าคนรับทราบคำเตือน
        Assert.False(call.WithAiHints);
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var ok = await c2.Documents.AsNoTracking().SingleAsync(d => d.Id == inCap);
            Assert.Equal(DocumentStatus.Approved, ok.Status);
            Assert.Contains(PartnerAutoApprovePolicy.ApprovedMarker, ok.InternalNotes);
            Assert.Equal(PartnerAutoApprovePolicy.Codes.Approved, (await c2.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == inCapLink)).AutoApproveCode);
            Assert.Single(await DocEventsAsync(c2, s.Buyer.Id, inCap, "PartnerPurchaseAutoApproved"));
            var held = await c2.Documents.AsNoTracking().SingleAsync(d => d.Id == overCap);
            Assert.Equal(DocumentStatus.Draft, held.Status);
            Assert.StartsWith("DRAFT-", held.DocumentNumber);
            Assert.Contains(PartnerAutoApprovePolicy.HoldMarker, held.InternalNotes);
            var link = await c2.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == overLink);
            Assert.Equal(PartnerAutoApprovePolicy.Codes.Consent, link.AutoApproveCode);
            Assert.Contains("เกินเพดาน", link.AutoApproveReason);
            await ResetWatermarkAsync(c2, overLink);
        }
        // รอบถัดไป (เหตุเดิม) ⇒ ไม่อนุมัติ · ไม่ลงหมายเหตุ/audit ซ้ำ
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await Pipe(c3, Docs(calls)).AutoApproveDraftsAsync(s.Buyer.Id, DateTime.UtcNow));
        Assert.Single(calls);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Single(await DocEventsAsync(check, s.Buyer.Id, overCap, "PartnerPurchaseAutoApproveHeld"));
        var notes = (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == overCap)).InternalNotes ?? "";
        Assert.Equal(1, notes.Split(PartnerAutoApprovePolicy.HoldMarker).Length - 1);
        Assert.False(AuditHashChain.Analyze(await check.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.Buyer.Id && a.RowHash != null).OrderBy(a => a.Id).ToListAsync()).HasIntegrityFindings);
    }

    [Fact]
    public async Task ผังไม่มั่นใจ_ใบกำกับไม่ครบ_มีคำเตือน_คงเป็นร่าง_ประวัติผู้ขายที่คนอนุมัติทำให้อนุมัติได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (_, unsure, unsureLink) = await RoutedAsync(db, s, 1_070m, localConf: 0.60m);
        var (_, taxInv, taxLink) = await RoutedAsync(db, s, 1_070m);
        var (_, warned, warnLink) = await RoutedAsync(db, s, 1_070m);
        // ใบกำกับที่ขาดวันที่ใบกำกับของผู้ขาย (ม.86/4)
        await db.Documents.Where(d => d.Id == taxInv).ExecuteUpdateAsync(x => x.SetProperty(d => d.HasTaxInvoiceReference, true).SetProperty(d => d.SupplierTaxInvoiceDate, (DateTime?)null));
        await GrantAsync(s, Scope.AutoApprovePurchase, 5_000m);
        var calls = new List<ApproveCall>();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await Pipe(c1, Docs(calls, id => id == warned ? new[] { "[§65 ตรี] ค่ารับรองเกินเพดาน — ตรวจก่อนอนุมัติ" } : Array.Empty<string>()))
                .AutoApproveDraftsAsync(s.Buyer.Id, DateTime.UtcNow));
        Assert.Empty(calls);
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            async Task<CrossTenantDocumentLink> Link(Guid id) => await c2.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == id);
            Assert.Equal(PartnerAutoApprovePolicy.Codes.GlUnsure, (await Link(unsureLink)).AutoApproveCode);
            var t = await Link(taxLink);
            Assert.Equal(PartnerAutoApprovePolicy.Codes.TaxInvoice, t.AutoApproveCode);
            Assert.Contains("วันที่ใบกำกับ", t.AutoApproveReason);
            Assert.Equal(PartnerAutoApprovePolicy.Codes.Warnings, (await Link(warnLink)).AutoApproveCode);
            foreach (var id in new[] { unsure, taxInv, warned })
                Assert.Equal(DocumentStatus.Draft, (await c2.Documents.AsNoTracking().SingleAsync(d => d.Id == id)).Status);
        }
        // ทิศตรงข้าม: ผู้ใช้เคยอนุมัติใบซื้อของผู้ขายรายนี้ด้วยผัง 53100 ⇒ ผังของใบที่ไม่มั่นใจกลายเป็น "ผู้ใช้เคยยืนยัน" ⇒ อนุมัติได้
        var supplier = await db.Documents.AsNoTracking().Where(d => d.Id == unsure).Select(d => d.ContactId).SingleAsync();
        var prior = new Document
        {
            CompanyId = s.Buyer.Id, ContactId = supplier, DocumentType = DocumentType.PurchaseInvoice, DocumentNumber = "PI-OLD-" + Guid.NewGuid().ToString("N")[..6],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date.AddMonths(-1), SubTotal = 500m, TotalAmount = 500m, Currency = "THB",
        };
        prior.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการเดือนก่อน", Quantity = 1, UnitPrice = 500m, Amount = 500m, AccountId = s.ExpenseB });
        db.Documents.Add(prior);
        await db.SaveChangesAsync();
        // ใบที่ระบบอนุมัติเองไม่นับเป็นการยืนยันของคน (ไม่เรียนจากตัวเอง)
        Assert.Contains(s.ExpenseB, await PartnerPayablePipeline.VendorConfirmedAccountsAsync(db, s.Buyer.Id, supplier, unsure, DateTime.UtcNow));
        await db.Documents.Where(d => d.Id == prior.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.InternalNotes, PartnerAutoApprovePolicy.ApprovedMarker + " x"));
        Assert.DoesNotContain(s.ExpenseB, await PartnerPayablePipeline.VendorConfirmedAccountsAsync(db, s.Buyer.Id, supplier, unsure, DateTime.UtcNow));
        await db.Documents.Where(d => d.Id == prior.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.InternalNotes, (string?)null));
        await ResetWatermarkAsync(db, unsureLink);
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c3, Docs(calls)).AutoApproveDraftsAsync(s.Buyer.Id, DateTime.UtcNow));
        Assert.Equal(unsure, Assert.Single(calls).DocumentId);
    }

    [Fact]
    public async Task ไม่ยินยอม_ไม่แตะใบ_ผู้ให้หมดสิทธิ์_ไม่อนุมัติ_บริษัทอื่นไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var other = await SeedAsync(db);
        var (_, mine, myLink) = await RoutedAsync(db, s);
        var (_, theirs, theirLink) = await RoutedAsync(db, other);
        var calls = new List<ApproveCall>();
        // ไม่ยินยอม ⇒ ไม่มีงาน (ลิงก์ไม่ถูกประเมิน · ใบไม่ถูกแตะ)
        using (var c0 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await Pipe(c0, Docs(calls)).AutoApproveDraftsAsync(s.Buyer.Id, DateTime.UtcNow));
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Null((await c1.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == myLink)).AutoApproveEvaluatedAt);
        await GrantAsync(s, Scope.AutoApprovePurchase, 5_000m);
        // ผู้ให้ถูกลดเป็นพนักงานที่ทำได้เฉพาะร่าง ⇒ ความยินยอมหยุด (ไม่อนุมัติแทน)
        await db.CompanyUsers.Where(cu => cu.CompanyId == s.Buyer.Id && cu.UserId == s.OwnerB)
            .ExecuteUpdateAsync(x => x.SetProperty(cu => cu.Role, UserRole.Staff).SetProperty(cu => cu.DraftDocumentsOnly, true));
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await Pipe(c2, Docs(calls)).AutoApproveDraftsAsync(s.Buyer.Id, DateTime.UtcNow));
        Assert.Empty(calls);
        using (var c3 = DbTestDatabase.TryCreateContext()!)
        {
            var l = await c3.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(x => x.Id == myLink);
            Assert.Equal(PartnerAutoApprovePolicy.Codes.Consent, l.AutoApproveCode);
            Assert.Contains("ไม่มีสิทธิ์", l.AutoApproveReason);
            Assert.Equal(DocumentStatus.Draft, (await c3.Documents.AsNoTracking().SingleAsync(d => d.Id == mine)).Status);
            // tenant: ความยินยอมของบริษัทนี้ไม่ไปแตะใบของบริษัทอื่น
            var t = await c3.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(x => x.Id == theirLink);
            Assert.Null(t.AutoApproveEvaluatedAt);
            Assert.Equal(DocumentStatus.Draft, (await c3.Documents.AsNoTracking().SingleAsync(d => d.Id == theirs)).Status);
            // บริษัทอื่นเรียกท่อด้วย companyId ของตัวเอง (ไม่มีความยินยอม) ⇒ ไม่มีงาน
            Assert.Equal(0, await Pipe(c3, Docs(calls)).AutoApproveDraftsAsync(other.Buyer.Id, DateTime.UtcNow));
        }
        // ทิศตรงข้าม: คืนสิทธิ์ ⇒ อนุมัติได้
        await db.CompanyUsers.Where(cu => cu.CompanyId == s.Buyer.Id && cu.UserId == s.OwnerB)
            .ExecuteUpdateAsync(x => x.SetProperty(cu => cu.Role, UserRole.Owner).SetProperty(cu => cu.DraftDocumentsOnly, false));
        await ResetWatermarkAsync(db, myLink);
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c4, Docs(calls)).AutoApproveDraftsAsync(s.Buyer.Id, DateTime.UtcNow));
        Assert.Equal(mine, Assert.Single(calls).DocumentId);
    }

    // ═══ ReceiveSlipCopy ═══

    private static readonly byte[] Png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Enumerable.Repeat((byte)7, 64)).ToArray();

    private static IFileStorage TempStorage(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "r288-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
    }

    /// <summary>สลิปที่ผู้ซื้อส่งให้ผู้ขาย (ไฟล์ + รายการชำระของผู้ขายที่ยืนยันแล้ว) + การแจ้ง "ผู้ขายรับเงินแล้ว" ในบริษัทผู้ซื้อ</summary>
    private static async Task<(Guid NoticeId, Guid IntentId)> SlipNoticeAsync(AccountingDbContext db, Seed s, Document inv, Guid purchaseId, Guid linkId, string root,
        Guid? attachmentCompany = null)
    {
        var path = Path.Combine(root, "seller-slip-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(path, Png);
        var file = new FileAttachment
        {
            CompanyId = attachmentCompany ?? s.Seller.Id, FileName = Path.GetFileName(path), OriginalFileName = "slip.png", ContentType = "image/png", StoragePath = path,
            EntityType = "Document", EntityId = inv.Id, CreatedBy = "portal-customer",
        };
        db.Set<FileAttachment>().Add(file);
        var intent = new PaymentIntent
        {
            CompanyId = s.Seller.Id, SourceKind = PaymentSourceKind.Document, SourceId = inv.Id, Amount = inv.TotalAmount, MethodKind = PaymentMethodKind.ManualSlip,
            Status = PaymentIntentStatus.Succeeded, ProviderCode = "manual-slip", IdempotencyKey = "r288-" + Guid.NewGuid().ToString("N"), SlipAttachmentId = file.Id,
        };
        db.PaymentIntents.Add(intent);
        var n = new PartnerPaymentNotice
        {
            CompanyId = s.Buyer.Id, TradingPartnershipId = s.PartnershipId, LinkId = linkId, SourceCompanyId = s.Seller.Id, SourceDocumentId = inv.Id,
            SourceDocumentNumber = inv.DocumentNumber, TargetDocumentId = purchaseId, PaymentIntentId = intent.Id, SellerPaymentId = Guid.NewGuid(),
            CashAmount = inv.TotalAmount, PaidAt = DateTime.UtcNow.Date, Status = PartnerPaymentNoticeRule.StatusOpen, CreatedBy = "partner-payment-notice",
        };
        db.PartnerPaymentNotices.Add(n);
        await db.SaveChangesAsync();
        return (n.Id, intent.Id);
    }

    private static Task<List<FileAttachment>> BuyerCopiesAsync(AccountingDbContext db, Seed s, Guid purchaseId)
        => db.Set<FileAttachment>().AsNoTracking().Where(a => a.CompanyId == s.Buyer.Id && a.EntityType == "Document" && a.EntityId == purchaseId
            && a.CreatedBy == PartnerSlipCopyRule.AttachmentCreatedBy).ToListAsync();

    [Fact]
    public async Task สำเนาสลิป_ไม่ยินยอมไม่ส่ง_ยินยอมแล้วแนบใบซื้อครั้งเดียว_ไฟล์บริษัทอื่นไม่ถูกหยิบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var storage = TempStorage(out var root);
        try
        {
            var s = await SeedAsync(db);
            var (inv, purchase, link) = await RoutedAsync(db, s);
            var docs = Docs(new List<ApproveCall>());
            // ยังไม่ยินยอม (การแจ้งเกิดก่อน) ⇒ ไม่มีงาน · ไม่มีรูปข้ามบริษัท
            var (early, _) = await SlipNoticeAsync(db, s, inv, purchase, link, root);
            // เวลาให้ความยินยอมเก็บระดับวินาที ⇒ การแจ้งในวินาทีเดียวกับการให้นับเป็น "หลังให้" — การแจ้งก่อนให้ในเทสต์ย้อนไป 1 นาที (ไม่ขึ้นกับจังหวะนาฬิกา)
            await db.PartnerPaymentNotices.Where(n => n.Id == early).ExecuteUpdateAsync(x => x.SetProperty(n => n.CreatedAt, DateTime.UtcNow.AddMinutes(-1)));
            using (var c0 = DbTestDatabase.TryCreateContext()!)
                Assert.Equal(0, await Pipe(c0, docs, storage).CopySlipsAsync(s.Buyer.Id, DateTime.UtcNow));
            using (var c1 = DbTestDatabase.TryCreateContext()!)
            {
                Assert.Empty(await BuyerCopiesAsync(c1, s, purchase));
                Assert.Null((await c1.PartnerPaymentNotices.AsNoTracking().SingleAsync(n => n.Id == early)).SlipCopyCheckedAt);
            }
            await GrantAsync(s, Scope.ReceiveSlipCopy, null);
            var (noticeId, _) = await SlipNoticeAsync(db, s, inv, purchase, link, root);
            // ไฟล์ที่อ้างเป็นของบริษัทอื่น (ไม่ใช่ผู้ขายของการแจ้ง) ⇒ ไม่หยิบ
            var (foreign, _) = await SlipNoticeAsync(db, s, inv, purchase, link, root, attachmentCompany: s.Buyer.Id);
            using (var c2 = DbTestDatabase.TryCreateContext()!)
                Assert.Equal(1, await Pipe(c2, docs, storage).CopySlipsAsync(s.Buyer.Id, DateTime.UtcNow));
            using (var c3 = DbTestDatabase.TryCreateContext()!)
            {
                var copy = Assert.Single(await BuyerCopiesAsync(c3, s, purchase));
                Assert.Equal(Png, await File.ReadAllBytesAsync(copy.StoragePath));
                Assert.StartsWith(root, copy.StoragePath);
                var n = await c3.PartnerPaymentNotices.AsNoTracking().SingleAsync(x => x.Id == noticeId);
                Assert.Equal(copy.Id, n.SlipCopyAttachmentId);
                Assert.NotNull(n.SlipCopyCheckedAt);
                // การแจ้งที่เกิดก่อนให้ความยินยอม ⇒ ไม่ส่งย้อน (จบ ไม่ส่ง) · ไฟล์บริษัทอื่น ⇒ จบ ไม่ส่ง
                var e = await c3.PartnerPaymentNotices.AsNoTracking().SingleAsync(x => x.Id == early);
                Assert.Null(e.SlipCopyAttachmentId);
                Assert.NotNull(e.SlipCopyCheckedAt);
                Assert.Null((await c3.PartnerPaymentNotices.AsNoTracking().SingleAsync(x => x.Id == foreign)).SlipCopyAttachmentId);
                var evts = (await c3.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.Buyer.Id && a.EntityType == nameof(PartnerPaymentNotice)).Select(a => a.NewValues!).ToListAsync())
                    .Count(v => v.Contains("PartnerSlipCopyReceived"));
                Assert.Equal(1, evts);
            }
            // รันซ้ำ ⇒ ไม่มีสำเนาที่สอง
            using (var c4 = DbTestDatabase.TryCreateContext()!)
                Assert.Equal(0, await Pipe(c4, docs, storage).CopySlipsAsync(s.Buyer.Id, DateTime.UtcNow));
            using var check = DbTestDatabase.TryCreateContext()!;
            Assert.Single(await BuyerCopiesAsync(check, s, purchase));
            Assert.Empty(await check.Set<FileAttachment>().AsNoTracking().Where(a => a.CompanyId == s.Seller.Id && a.CreatedBy == PartnerSlipCopyRule.AttachmentCreatedBy).ToListAsync());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}

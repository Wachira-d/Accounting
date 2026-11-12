using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 257 ชุด 3 (erp-review/2026-11-10/team-D.md) — เส้นคู่ค้าบน PostgreSQL จริง ทั้งสองทิศ:
/// D-H4 หลักฐานจากใบแจ้งหนี้ไม่ติดไปกับใบกำกับที่แปลงมา · D-H5 ระงับการเชื่อมหยุดเส้นอัตโนมัติ + เชิญใหม่เปิดได้ · D-M1 คำเชิญจริงแจ้งเจ้าของปลายทาง ·
/// D-M2 ส่วนลดท้ายบิล/หน่วย/ภาษีหักตามต้นฉบับ · LOW ใบสั่งซื้อปลายทางเป็นร่าง · ใบเสร็จที่เป็นใบกำกับแนบเข้าใบซื้อเดิม
/// <para>seed: บริษัทใหม่ทุกเทสต์ · เลขภาษีสุ่ม (ฐานร่วม)</para>
/// </summary>
[Trait("Category", "Db")]
public class PartnerExchangeRound257DbTests
{
    private readonly ITestOutputHelper _out;
    public PartnerExchangeRound257DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static readonly Random Rng = new();
    private static string RandTax()
    {
        lock (Rng) return "08" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Rng.Next(10))));
    }

    private static CrossTenantWorkflowService Svc(AccountingDbContext db) => new(db, NullLogger<CrossTenantWorkflowService>.Instance);

    private sealed record Pair(Company Seller, Company Buyer, Guid PartnershipId, Contact BuyerAtSeller);

    private static async Task<Pair> SeedAsync(AccountingDbContext db, bool buyerDrafts = true, bool buyerDiscoverable = false)
    {
        var seller = new Company { Name = "ผู้ขาย 257 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000", PartnerDiscoverable = true };
        var buyer = new Company { Name = "ผู้ซื้อ 257 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000", PartnerDiscoverable = buyerDiscoverable };
        db.Companies.AddRange(seller, buyer);
        await db.SaveChangesAsync();
        var (a, b) = seller.Id.CompareTo(buyer.Id) < 0 ? (seller.Id, buyer.Id) : (buyer.Id, seller.Id);
        var p = new TradingPartnership { CompanyAId = a, CompanyBId = b, InvitedByCompanyId = seller.Id, Status = TradingPartnershipStatus.Accepted };
        db.TradingPartnerships.Add(p);
        db.WorkflowAutomationConfigs.Add(new WorkflowAutomationConfig { CompanyId = buyer.Id, CreatePurchaseDraftsFromPartners = buyerDrafts });
        var contact = new Contact { CompanyId = seller.Id, Name = buyer.Name, TaxId = buyer.TaxId, BranchCode = "00000", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return new Pair(seller, buyer, p.Id, contact);
    }

    private static async Task<Document> SellerDocAsync(AccountingDbContext db, Pair s, DocumentType type, Guid? relatedTo = null, bool? byLaw = null,
        Action<Document>? shape = null)
    {
        var d = new Document
        {
            CompanyId = s.Seller.Id, ContactId = s.BuyerAtSeller.Id, DocumentType = type, DocumentNumber = "S257-" + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m,
            Currency = "THB", RelatedDocumentId = relatedTo, IsTaxInvoiceByLaw = byLaw,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการวางระบบ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        shape?.Invoke(d);
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    private static Task<Document> DraftOfAsync(AccountingDbContext db, Guid buyerId, Guid linkId)
        => db.Documents.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.CompanyId == buyerId && d.PartnerSourceLinkId == linkId);

    // ═══════════ D-H4 ═══════════

    [Fact]
    public async Task R257_H4_ใบแจ้งหนี้_ยืนยันไม่ได้_คำยืนยันเก่าไม่ติดไปกับใบกำกับที่แปลงมา_ยืนยันใหม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var invoice = await SellerDocAsync(db, s, DocumentType.Invoice);
        var l1 = await Svc(db).OnDocumentSentAsync(invoice);

        using var c1 = DbTestDatabase.TryCreateContext()!;
        var draft = await DraftOfAsync(c1, s.Buyer.Id, l1!.Id);
        Assert.Equal(PartnerInputVatEvidence.AwaitingTaxInvoice, draft.PartnerInputVatEvidence);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(c1).ConfirmPartnerOriginalAsync(s.Buyer.Id, draft.Id, Guid.NewGuid()));
        Assert.Equal(PartnerInputVatEvidence.AwaitingTaxInvoiceMessage, ex.Message);
        var row = Assert.Single(await Svc(c1).IncomingPurchaseDraftsAsync(s.Buyer.Id));
        Assert.False((bool)row.GetType().GetProperty("NeedsOriginal")!.GetValue(row)!);   // ปุ่มยืนยันไม่แสดง
        Assert.Equal(PartnerInputVatEvidence.AwaitingTaxInvoiceMessage, (string?)row.GetType().GetProperty("ConfirmBlockedReason")!.GetValue(row));

        // ข้อมูลก่อนแก้: ผู้รับเคยกด "ได้ต้นฉบับ" บนใบแจ้งหนี้ไว้แล้ว (persisted PaperConfirmed)
        await c1.Documents.Where(d => d.Id == draft.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.PartnerInputVatEvidence, PartnerInputVatEvidence.PaperConfirmed));
        var taxInvoice = await SellerDocAsync(c1, s, DocumentType.TaxInvoice, relatedTo: invoice.Id);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        await Svc(c2).OnDocumentSentAsync(await c2.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == taxInvoice.Id));

        using var c3 = DbTestDatabase.TryCreateContext()!;
        var after = await c3.Documents.AsNoTracking().SingleAsync(d => d.Id == draft.Id);
        Assert.Equal(PartnerInputVatEvidence.PaperRequired, after.PartnerInputVatEvidence);   // ตั้งใหม่ด้วย Of(...) — ไม่ใช่คำยืนยันของใบแจ้งหนี้
        Assert.True(PartnerInputVatEvidence.BlocksClaim(after.PartnerInputVatEvidence));
        Assert.Equal(taxInvoice.DocumentNumber, after.SupplierInvoiceNumber);
        Assert.Equal(1, await c3.Documents.CountAsync(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId != null));
        Assert.Equal(PartnerInputVatEvidence.PaperConfirmed, (await Svc(c3).ConfirmPartnerOriginalAsync(s.Buyer.Id, draft.Id, Guid.NewGuid())).Evidence);
    }

    [Fact]
    public async Task R257_H4_ทิศตรงข้าม_ยืนยันใบกำกับไว้แล้ว_ใบกำกับที่ออกแทนมาถึง_คงค่าที่ยืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var tiv = await SellerDocAsync(db, s, DocumentType.TaxInvoice);
        var l1 = await Svc(db).OnDocumentSentAsync(tiv);
        using var c1 = DbTestDatabase.TryCreateContext()!;
        var draft = await DraftOfAsync(c1, s.Buyer.Id, l1!.Id);
        Assert.Equal(PartnerInputVatEvidence.PaperConfirmed, (await Svc(c1).ConfirmPartnerOriginalAsync(s.Buyer.Id, draft.Id, Guid.NewGuid())).Evidence);

        var replacement = await SellerDocAsync(c1, s, DocumentType.TaxInvoice, relatedTo: tiv.Id);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        await Svc(c2).OnDocumentSentAsync(await c2.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == replacement.Id));
        using var c3 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(PartnerInputVatEvidence.PaperConfirmed, await c3.Documents.Where(d => d.Id == draft.Id).Select(d => d.PartnerInputVatEvidence).SingleAsync());
    }

    [Fact]
    public async Task R257_LOW_ใบเสร็จที่เป็นใบกำกับ_แนบเข้าใบซื้อเดิม_ไม่สร้างใบใหม่_ทิศตรงข้ามขายเงินสดได้เหตุบนแถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var invoice = await SellerDocAsync(db, s, DocumentType.Invoice);
        var l1 = await Svc(db).OnDocumentSentAsync(invoice);
        var receipt = await SellerDocAsync(db, s, DocumentType.Receipt, relatedTo: invoice.Id, byLaw: true);
        var lr = await Svc(db).OnDocumentSentAsync(receipt);
        var cashReceipt = await SellerDocAsync(db, s, DocumentType.Receipt, byLaw: true);
        var lc = await Svc(db).OnDocumentSentAsync(cashReceipt);

        using var check = DbTestDatabase.TryCreateContext()!;
        var draft = await DraftOfAsync(check, s.Buyer.Id, l1!.Id);
        Assert.Equal(1, await check.Documents.CountAsync(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId != null));
        Assert.Equal(PartnerInputVatEvidence.PaperRequired, draft.PartnerInputVatEvidence);   // ใบกำกับมาถึงแล้ว ⇒ ยืนยันต้นฉบับได้
        Assert.Equal(receipt.DocumentNumber, draft.SupplierInvoiceNumber);
        Assert.Equal((Guid?)draft.Id, (await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == lr!.Id)).TargetDocumentId);
        var cashLink = await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == lc!.Id);
        Assert.Null(cashLink.TargetDocumentId);
        Assert.Equal(PartnerExchangeRules.ReceiptNoChainMessage, cashLink.Comment);
    }

    // ═══════════ D-M2 ═══════════

    [Fact]
    public async Task R257_M2_ส่วนลดท้ายบิล_หน่วย_ภาษีหัก_ตามต้นฉบับ_ทิศตรงข้ามยอดไม่รวมเท่า_ไม่สร้างพร้อมเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        // 2 บรรทัด 500 ส่วนลดบรรทัด 10% (ลดบรรทัดละ 50) ⇒ 900 · ส่วนลดท้ายบิล 100 เฉลี่ยลงบรรทัด ⇒ 800 · VAT 56 · หัก 3% = 24 ⇒ 832
        var inv = await SellerDocAsync(db, s, DocumentType.TaxInvoice, shape: d =>
        {
            d.Lines.Clear();
            foreach (var i in new[] { 1, 2 })
                d.Lines.Add(new DocumentLine
                {
                    LineOrder = i, Description = "งาน " + i, Quantity = 2, Unit = "ชั่วโมง", UnitPrice = 250m, DiscountPercent = 10m, DiscountAmount = 50m,
                    Amount = 400m, VatRate = 7m, VatAmount = 28m, WithholdingTaxRate = 3m, WithholdingTaxAmount = 12m, IncomeTypeCode = "40(2)",
                });
            d.DiscountAmount = 100m; d.BillDiscountPercent = 0m; d.BillDiscountAmount = 100m;
            d.SubTotal = 800m; d.VatAmount = 56m; d.WithholdingTaxAmount = 24m; d.TotalAmount = 832m; d.BalanceDue = 832m;
        });
        var link = await Svc(db).OnDocumentSentAsync(inv);
        var broken = await SellerDocAsync(db, s, DocumentType.TaxInvoice, shape: d => { d.SubTotal = 900m; d.VatAmount = 63m; d.TotalAmount = 963m; });
        var brokenLink = await Svc(db).OnDocumentSentAsync(broken);

        using var check = DbTestDatabase.TryCreateContext()!;
        var draft = await DraftOfAsync(check, s.Buyer.Id, link!.Id);
        Assert.Equal(100m, draft.BillDiscountAmount);
        Assert.Equal(100m, draft.DiscountAmount);
        Assert.Equal(24m, draft.WithholdingTaxAmount);
        Assert.Equal(800m, draft.SubTotal);
        Assert.Equal(832m, draft.TotalAmount);
        Assert.Equal(draft.SubTotal, draft.Lines.Sum(l => l.Amount) + draft.RoundingAdjustment);   // บรรทัดรวมเท่าหัวใบ
        Assert.All(draft.Lines, l =>
        {
            Assert.Equal("ชั่วโมง", l.Unit);
            Assert.Equal(10m, l.DiscountPercent);
            Assert.Equal(12m, l.WithholdingTaxAmount);
            Assert.Equal("40(2)", l.IncomeTypeCode);
        });
        Assert.False(await check.Documents.AnyAsync(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId == brokenLink!.Id));
        Assert.Contains("ไม่เท่ายอดก่อนภาษี", (await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == brokenLink!.Id)).Comment);
    }

    // ═══════════ D-H5 ═══════════

    [Fact]
    public async Task R257_H5_ระงับ_หยุดส่งเอกสาร_audit_เชิญใหม่เปิดคำเชิญ_ทิศตรงข้ามคนนอกระงับไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, buyerDiscoverable: true);
        var actor = Guid.NewGuid();
        var outsider = await SeedAsync(db);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Svc(db).SuspendPartnershipAsync(outsider.Buyer.Id, s.PartnershipId, actor, null));

        var p = await Svc(db).SuspendPartnershipAsync(s.Buyer.Id, s.PartnershipId, actor, "เลิกค้าขาย");
        Assert.Equal(TradingPartnershipStatus.Suspended, p.Status);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).SuspendPartnershipAsync(s.Seller.Id, s.PartnershipId, actor, null));   // ซ้ำ = 409

        using var c1 = DbTestDatabase.TryCreateContext()!;
        Assert.True(await c1.AuditLogs.AnyAsync(a => a.CompanyId == s.Buyer.Id && a.EntityType == "TradingPartnership" && a.EntityId == s.PartnershipId.ToString()
            && a.NewValues != null && a.NewValues.Contains("TradingPartnershipSuspended")));
        var inv = await SellerDocAsync(c1, s, DocumentType.TaxInvoice);
        Assert.Null(await Svc(c1).OnDocumentSentAsync(inv));   // เส้นอัตโนมัติหยุด — ไม่มีแถวกล่องเข้า/ใบซื้อร่าง
        Assert.False(await c1.CrossTenantDocumentLinks.AnyAsync(l => l.SourceDocumentId == inv.Id));
        Assert.Equal("Suspended", (await Svc(c1).PartnerStatusForContactAsync(s.Seller.Id, s.BuyerAtSeller.Id)).State);

        // ฝ่ายค้าน LOW: เชิญซ้ำทันทีหลังระงับ ⇒ ยังระงับ (ช่วงพัก 7 วัน) · พ้นช่วงพักแล้วเชิญ ⇒ คำเชิญสด (Pending) ⇒ อีกฝ่ายยอมรับได้
        await Svc(c1).InvitePartnerAsync(s.Seller.Id, s.Buyer.TaxId!, "00000", actor, null);
        using (var cw = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(TradingPartnershipStatus.Suspended, (await cw.TradingPartnerships.AsNoTracking().SingleAsync(x => x.Id == s.PartnershipId)).Status);
            await cw.TradingPartnerships.Where(x => x.Id == s.PartnershipId)
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.SuspendedAt, DateTime.UtcNow.AddDays(-8)));
        }
        using (var ci = DbTestDatabase.TryCreateContext()!)
            await Svc(ci).InvitePartnerAsync(s.Seller.Id, s.Buyer.TaxId!, "00000", actor, null);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(TradingPartnershipStatus.Pending, (await c2.TradingPartnerships.AsNoTracking().SingleAsync(x => x.Id == s.PartnershipId)).Status);
        Assert.Equal(TradingPartnershipStatus.Accepted, (await Svc(c2).AcceptPartnershipAsync(s.Buyer.Id, s.PartnershipId, actor)).Status);
    }

    // ═══════════ D-M1 ═══════════

    [Fact]
    public async Task R257_M1_รอบ264_คำเชิญถึงปลายทางทั้งที่เปิดและไม่เปิดค้นเจอ_ไม่เขียนกระดิ่งในคำขอ_เข้าคิวแจ้ง()
    {
        // รอบ 264 (คำตัดสิน 264-1-1 แทน 257-3-3): ปลายทางที่ไม่เปิด "ให้คู่ค้าค้นเจอ" ได้คำขอด้วย (เดิมไม่เก็บ = ผู้ใช้รายงานว่าหาที่ยอมรับไม่เจอ) ·
        // การแจ้งไม่อยู่ในคำขอของผู้เชิญแล้ว (outbox ⇒ งานเบื้องหลังส่งผ่านเครื่องแจ้งเตือน — PartnerLinkRequestRound264DbTests)
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        async Task<(Company Co, Guid Owner)> CompanyWithOwnerAsync(bool discoverable)
        {
            var co = new Company { Name = "ปลายทาง 257 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000", PartnerDiscoverable = discoverable };
            db.Companies.Add(co);
            var u = new User { Email = $"r257-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของ 257" };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = co.Id, Role = UserRole.Owner });
            await db.SaveChangesAsync();
            return (co, u.Id);
        }
        var inviter = new Company { Name = "ผู้เชิญ 257 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000" };
        db.Companies.Add(inviter);
        await db.SaveChangesAsync();
        var open = await CompanyWithOwnerAsync(discoverable: true);
        var closed = await CompanyWithOwnerAsync(discoverable: false);

        await Svc(db).InvitePartnerAsync(inviter.Id, open.Co.TaxId!, null, Guid.NewGuid(), "สวัสดี");
        await Svc(db).InvitePartnerAsync(inviter.Id, closed.Co.TaxId!, null, Guid.NewGuid(), "สวัสดี");

        using var check = DbTestDatabase.TryCreateContext()!;
        foreach (var target in new[] { open, closed })
        {
            var row = await check.TradingPartnerships.AsNoTracking().SingleAsync(p => p.CompanyAId == target.Co.Id || p.CompanyBId == target.Co.Id);
            Assert.Equal(TradingPartnershipStatus.Pending, row.Status);
            Assert.NotNull(row.InviteNoticeDueAt);                                        // เข้าคิวแจ้ง
            Assert.False(await check.Notifications.AnyAsync(n => n.UserId == target.Owner));   // ไม่มีงานแจ้งในคำขอ (เวลาตอบคงที่)
        }
        // ผู้เชิญไม่ได้รับสัญญาณใดในบริษัทตัวเอง (P-1)
        Assert.False(await check.Notifications.AnyAsync(n => n.CompanyId == inviter.Id));
    }

    // ═══════════ LOW: ใบสั่งซื้อปลายทางเป็นร่าง ═══════════

    [Fact]
    public async Task R257_LOW_อนุมัติใบเสนอราคา_ใบสั่งซื้อปลายทางเป็นร่าง_ไม่ส่งย้อนเข้าผู้ขาย_ทิศตรงข้ามคู่ระงับไม่สร้าง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        async Task<(Pair S, Guid LinkId, Guid Actor)> QuoteAsync()
        {
            var s = await SeedAsync(db, buyerDrafts: false);
            var actorUser = new User { Email = $"r257q-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้อนุมัติ 257" };
            db.Users.Add(actorUser);
            await db.SaveChangesAsync();
            db.CompanyUsers.Add(new CompanyUser { UserId = actorUser.Id, CompanyId = s.Buyer.Id, Role = UserRole.Staff });
            var q = await SellerDocAsync(db, s, DocumentType.Quotation, shape: d => d.Status = DocumentStatus.Sent);
            var link = new CrossTenantDocumentLink
            {
                TradingPartnershipId = s.PartnershipId, SourceCompanyId = s.Seller.Id, SourceDocumentId = q.Id, TargetCompanyId = s.Buyer.Id,
                LinkType = CrossTenantLinkType.QuotationFlow, Status = CrossTenantLinkStatus.PendingApproval,
            };
            db.CrossTenantDocumentLinks.Add(link);
            await db.SaveChangesAsync();
            return (s, link.Id, actorUser.Id);
        }
        var live = await QuoteAsync();
        await Svc(db).ApproveIncomingAsync(live.S.Buyer.Id, live.LinkId, live.Actor, overrideAutoCreatePo: true);
        var paused = await QuoteAsync();
        await db.TradingPartnerships.Where(p => p.Id == paused.S.PartnershipId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, TradingPartnershipStatus.Suspended));
        using (var c0 = DbTestDatabase.TryCreateContext()!)
            await Svc(c0).ApproveIncomingAsync(paused.S.Buyer.Id, paused.LinkId, paused.Actor, overrideAutoCreatePo: true);

        using var check = DbTestDatabase.TryCreateContext()!;
        var po = await check.Documents.AsNoTracking().SingleAsync(d => d.CompanyId == live.S.Buyer.Id && d.DocumentType == DocumentType.PurchaseOrder);
        Assert.Equal(DocumentStatus.Draft, po.Status);
        Assert.StartsWith("DRAFT-", po.DocumentNumber);
        Assert.False(await check.CrossTenantDocumentLinks.AnyAsync(l => l.SourceDocumentId == po.Id));   // ร่างไม่เข้าระบบผู้ขาย
        Assert.False(await check.Documents.AnyAsync(d => d.CompanyId == live.S.Seller.Id && d.Reference == "PO " + po.DocumentNumber));
        // ทิศตรงข้าม: คู่ระงับ ⇒ อนุมัติได้ แต่ไม่สร้างเอกสารปลายทาง + เหตุบนแถว
        Assert.False(await check.Documents.AnyAsync(d => d.CompanyId == paused.S.Buyer.Id && d.DocumentType == DocumentType.PurchaseOrder));
        Assert.Contains("ระงับ", (await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == paused.LinkId)).Comment);
        // ฝ่ายค้าน M5: แถวบอกว่า PO เป็นร่าง · ตัวคัดลอกเดียว
        Assert.Contains(CrossTenantWorkflowService.DownstreamPoDraftComment, (await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == live.LinkId)).Comment);
        Assert.Equal(1_000m, po.SubTotal);
        Assert.Single(await check.DocumentLines.AsNoTracking().Where(l => l.DocumentId == po.Id).ToListAsync());
    }

    // ═══════════ ฝ่ายค้านของ 2b94b71a ═══════════

    [Fact]
    public async Task R257F_M1_ได้ใบกำกับนอกระบบ_ร่างได้เลขวันที่_PaperConfirmed_ทิศตรงข้ามใบกำกับผ่านระบบใช้ทางนี้ไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var invoice = await SellerDocAsync(db, s, DocumentType.Invoice);
        var l1 = await Svc(db).OnDocumentSentAsync(invoice);
        var tiv = await SellerDocAsync(db, s, DocumentType.TaxInvoice);
        var l2 = await Svc(db).OnDocumentSentAsync(tiv);
        using var c1 = DbTestDatabase.TryCreateContext()!;
        var draft = await DraftOfAsync(c1, s.Buyer.Id, l1!.Id);
        var outcome = await Svc(c1).ConfirmExternalTaxInvoiceAsync(s.Buyer.Id, draft.Id, Guid.NewGuid(), false, " IV-EXT-1 ", DateTime.UtcNow.Date);
        Assert.False(outcome.Approved);
        Assert.Equal(PartnerInputVatEvidence.PaperConfirmed, outcome.Evidence);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var after = await c2.Documents.AsNoTracking().SingleAsync(d => d.Id == draft.Id);
        Assert.Equal("IV-EXT-1", after.SupplierInvoiceNumber);
        Assert.True(after.HasTaxInvoiceReference);
        Assert.True(await c2.AuditLogs.AnyAsync(a => a.CompanyId == s.Buyer.Id && a.EntityId == draft.Id.ToString()
            && a.NewValues != null && a.NewValues.Contains("PartnerExternalTaxInvoiceConfirmed")));
        // ทิศตรงข้าม: ใบที่ได้ใบกำกับผ่านระบบ (PaperRequired) ใช้ทางนี้ไม่ได้ · บริษัทอื่นใช้ไม่ได้
        var other = await DraftOfAsync(c2, s.Buyer.Id, l2!.Id);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(c2).ConfirmExternalTaxInvoiceAsync(s.Buyer.Id, other.Id, Guid.NewGuid(), false, "X", DateTime.UtcNow.Date));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Svc(c2).ConfirmExternalTaxInvoiceAsync(s.Seller.Id, draft.Id, Guid.NewGuid(), false, "X", DateTime.UtcNow.Date));
        // ฝ่ายค้าน LOW: ยืนยันแบบ e-Tax ประทับเวลาบนใบที่ไม่มีไฟล์ลงลายมือชื่อ = 409
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(c2).ConfirmPartnerOriginalAsync(s.Buyer.Id, other.Id, Guid.NewGuid(), stampedEtax: true));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task R257F_M3_ใบกำกับVATไม่ตรงใบซื้อ_คงรอใบกำกับ_เหตุบนแถว_ทิศตรงข้ามVATตรง_ได้หลักฐานปกติ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var inv = await SellerDocAsync(db, s, DocumentType.Invoice);
        var l1 = await Svc(db).OnDocumentSentAsync(inv);
        var tivBad = await SellerDocAsync(db, s, DocumentType.TaxInvoice, relatedTo: inv.Id, shape: d =>
        {
            d.Lines.Clear();
            d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 5m, VatAmount = 50m });
            d.VatAmount = 50m; d.TotalAmount = 1_050m;
        });
        var lb = await Svc(db).OnDocumentSentAsync(tivBad);
        using var c1 = DbTestDatabase.TryCreateContext()!;
        var draft = await DraftOfAsync(c1, s.Buyer.Id, l1!.Id);
        Assert.Equal(PartnerInputVatEvidence.AwaitingTaxInvoice, draft.PartnerInputVatEvidence);
        Assert.Contains("VAT", (await c1.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == lb!.Id)).Comment);

        var s2 = await SeedAsync(c1);
        var inv2 = await SellerDocAsync(c1, s2, DocumentType.Invoice);
        var m1 = await Svc(c1).OnDocumentSentAsync(inv2);
        var tivOk = await SellerDocAsync(c1, s2, DocumentType.TaxInvoice, relatedTo: inv2.Id);
        await Svc(c1).OnDocumentSentAsync(tivOk);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(PartnerInputVatEvidence.PaperRequired, (await DraftOfAsync(c2, s2.Buyer.Id, m1!.Id)).PartnerInputVatEvidence);
    }

    [Fact]
    public async Task R257F_LOW_ใบแทนหลังต้นทางยกเลิก_ล้างธง_ตรวจสดไม่ติด_ทิศตรงข้ามไม่มีใบแทนยังติด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var tiv = await SellerDocAsync(db, s, DocumentType.TaxInvoice);
        var l1 = await Svc(db).OnDocumentSentAsync(tiv);
        await db.Documents.Where(d => d.Id == tiv.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Voided));
        using (var cv = DbTestDatabase.TryCreateContext()!) Assert.Equal(1, await Svc(cv).OnSourceVoidedAsync(s.Seller.Id, tiv.Id));
        using var c1 = DbTestDatabase.TryCreateContext()!;
        var draftId = (await DraftOfAsync(c1, s.Buyer.Id, l1!.Id)).Id;
        Assert.True((await Svc(c1).PartnerSourceCheckAsync(s.Buyer.Id, draftId)).Voided);   // ทิศตรงข้าม: ยังไม่มีใบแทน ⇒ ติด
        var replacement = new Document
        {
            CompanyId = s.Seller.Id, ContactId = s.BuyerAtSeller.Id, DocumentType = DocumentType.TaxInvoice, DocumentNumber = "S257R-" + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m,
            Currency = "THB", ReplacesDocumentId = tiv.Id,
        };
        replacement.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการวางระบบ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        c1.Documents.Add(replacement);
        await c1.SaveChangesAsync();
        await Svc(c1).OnDocumentSentAsync(replacement);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(PartnerInputVatEvidence.PaperRequired, await c2.Documents.Where(d => d.Id == draftId).Select(d => d.PartnerInputVatEvidence).SingleAsync());
        Assert.False((await Svc(c2).PartnerSourceCheckAsync(s.Buyer.Id, draftId)).Voided);
        Assert.Equal(PartnerInputVatEvidence.PaperConfirmed, (await Svc(c2).ConfirmPartnerOriginalAsync(s.Buyer.Id, draftId, Guid.NewGuid())).Evidence);
    }

    [Fact]
    public async Task R257F_M2_ยืนยันก่อนใบกำกับมาถึง_ตัวหาเจอ_ทิศตรงข้ามยืนยันหลังใบกำกับ_ไม่เจอ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        // ใบที่ 1: ยืนยัน (audit) ก่อนแถวกล่องเข้าของใบกำกับ — ข้อมูลรุ่นก่อนแก้
        var s = await SeedAsync(db);
        var inv = await SellerDocAsync(db, s, DocumentType.Invoice);
        var l1 = await Svc(db).OnDocumentSentAsync(inv);
        using var c1 = DbTestDatabase.TryCreateContext()!;
        var draft = await DraftOfAsync(c1, s.Buyer.Id, l1!.Id);
        var tiv = await SellerDocAsync(c1, s, DocumentType.TaxInvoice, relatedTo: inv.Id);
        await Svc(c1).OnDocumentSentAsync(tiv);
        var tivLinkAt = await c1.CrossTenantDocumentLinks.Where(l => l.SourceDocumentId == tiv.Id).Select(l => l.CreatedAt).SingleAsync();
        await c1.Documents.Where(d => d.Id == draft.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.PartnerInputVatEvidence, PartnerInputVatEvidence.PaperConfirmed));
        c1.AddChainedAuditLog(new AuditLog
        {
            CompanyId = s.Buyer.Id, Action = AuditAction.Update, EntityType = "Document", EntityId = draft.Id.ToString(),
            OldValues = "{\"inputVatEvidence\":\"PaperRequired\"}",
            NewValues = "{\"inputVatEvidence\":\"PaperConfirmed\",\"ruleCode\":\"" + PartnerExchangeRules.RuleCode + "\"}",
            Timestamp = tivLinkAt.AddDays(-1),
        });
        await c1.SaveChangesAsync();
        // ใบที่ 2 (ทิศตรงข้าม): ยืนยันจริงหลังใบกำกับมาถึง
        var s2 = await SeedAsync(c1);
        var tiv2 = await SellerDocAsync(c1, s2, DocumentType.TaxInvoice);
        var m1 = await Svc(c1).OnDocumentSentAsync(tiv2);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var d2 = await DraftOfAsync(c2, s2.Buyer.Id, m1!.Id);
        await Svc(c2).ConfirmPartnerOriginalAsync(s2.Buyer.Id, d2.Id, Guid.NewGuid());

        using var check = DbTestDatabase.TryCreateContext()!;
        var hit = Assert.Single(await PartnerEvidenceRepair.FindAsync(check, s.Buyer.Id));
        Assert.Equal(draft.Id, hit.DocumentId);
        Assert.Empty(await PartnerEvidenceRepair.FindAsync(check, s2.Buyer.Id));
    }

    // ═══════════ รอบ 257 ปิดท้าย (คำตัดสิน 257-5-3): MED-3 · MED-4 · MED-5 ═══════════

    [Fact]
    public async Task Z_MED3_ทางนอกระบบ_สายมีใบกำกับผ่านระบบVATไม่ตรง_409_แก้VATให้ตรงแล้วผ่าน_eTaxประทับเวลา409()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var inv = await SellerDocAsync(db, s, DocumentType.Invoice);
        var l1 = await Svc(db).OnDocumentSentAsync(inv);
        var tivBad = await SellerDocAsync(db, s, DocumentType.TaxInvoice, relatedTo: inv.Id, shape: d =>
        {
            d.Lines.Clear();
            d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 5m, VatAmount = 50m });
            d.VatAmount = 50m; d.TotalAmount = 1_050m;
        });
        await Svc(db).OnDocumentSentAsync(tivBad);
        using var c1 = DbTestDatabase.TryCreateContext()!;
        var draft = await DraftOfAsync(c1, s.Buyer.Id, l1!.Id);
        Assert.Equal(PartnerInputVatEvidence.AwaitingTaxInvoice, draft.PartnerInputVatEvidence);
        // เดิม: กดทางนอกระบบได้เลยทั้งที่ VAT บนใบซื้อ (70) ไม่ตรงใบกำกับของผู้ขาย (50) ⇒ ปลดภาษีซื้อบนยอดผิด
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Svc(c1).ConfirmExternalTaxInvoiceAsync(s.Buyer.Id, draft.Id, Guid.NewGuid(), false, tivBad.DocumentNumber, DateTime.UtcNow.Date));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("VAT", ex.Message);
        Assert.Contains("แก้ยอด/VAT", ex.Message);          // ใบร่าง ⇒ ทางไปต่อ = แก้ให้ตรง
        using (var cx = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(PartnerInputVatEvidence.AwaitingTaxInvoice, await cx.Documents.Where(d => d.Id == draft.Id).Select(d => d.PartnerInputVatEvidence).SingleAsync());
        // "ได้ e-Tax ที่ประทับเวลา" บนทางนอกระบบ = ไม่มีไฟล์ลงลายมือชื่อถึงผู้รับ ⇒ 409 (ก่อนถึงด่าน VAT)
        using (var cx = DbTestDatabase.TryCreateContext()!)
        {
            var st = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Svc(cx).ConfirmExternalTaxInvoiceAsync(s.Buyer.Id, draft.Id, Guid.NewGuid(), true, tivBad.DocumentNumber, DateTime.UtcNow.Date));
            Assert.Equal(409, st.StatusCode);
            Assert.Contains("e-Tax", st.Message);
        }
        // ทิศตรงข้าม: แก้ VAT ของใบซื้อให้ตรงใบกำกับ ⇒ ทางนอกระบบผ่าน
        using var c2 = DbTestDatabase.TryCreateContext()!;
        await c2.Documents.Where(d => d.Id == draft.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.VatAmount, 50m).SetProperty(d => d.TotalAmount, 1_050m));
        var ok = await Svc(c2).ConfirmExternalTaxInvoiceAsync(s.Buyer.Id, draft.Id, Guid.NewGuid(), false, tivBad.DocumentNumber, DateTime.UtcNow.Date);
        Assert.Equal(PartnerInputVatEvidence.PaperConfirmed, ok.Evidence);
    }

    [Fact]
    public async Task Z_MED4_ยืนยันนอกระบบแล้วใบกำกับเดียวกันมาถึง_คงคำยืนยัน_ทิศตรงข้ามเลขไม่ตรง_ตั้งกลับต้องได้ต้นฉบับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        async Task<(Pair S, Guid DraftId, string? Evidence)> RunAsync(bool sameNumber)
        {
            using var c0 = DbTestDatabase.TryCreateContext()!;
            var s = await SeedAsync(c0);
            var inv = await SellerDocAsync(c0, s, DocumentType.Invoice);
            var l1 = await Svc(c0).OnDocumentSentAsync(inv);
            var tiv = await SellerDocAsync(c0, s, DocumentType.TaxInvoice, relatedTo: inv.Id);   // ผู้ขายออกแล้วแต่ยังไม่ส่งผ่านระบบ
            Guid draftId;
            using (var c1 = DbTestDatabase.TryCreateContext()!)
            {
                draftId = (await DraftOfAsync(c1, s.Buyer.Id, l1!.Id)).Id;
                var outcome = await Svc(c1).ConfirmExternalTaxInvoiceAsync(s.Buyer.Id, draftId, Guid.NewGuid(), false,
                    sameNumber ? " " + tiv.DocumentNumber.ToLowerInvariant() + " " : "IV-OTHER-999", DateTime.UtcNow.Date);
                Assert.Equal(PartnerInputVatEvidence.PaperConfirmed, outcome.Evidence);
            }
            using (var c2 = DbTestDatabase.TryCreateContext()!)
                Assert.NotNull(await Svc(c2).OnDocumentSentAsync(await c2.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == tiv.Id)));
            using var c3 = DbTestDatabase.TryCreateContext()!;
            var evidence = await c3.Documents.Where(d => d.Id == draftId).Select(d => d.PartnerInputVatEvidence).SingleAsync();
            Assert.Equal(1, await c3.Documents.CountAsync(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId != null));   // ไม่สร้างใบที่สอง
            return (s, draftId, evidence);
        }

        var kept = await RunAsync(sameNumber: true);
        Assert.Equal(PartnerInputVatEvidence.PaperConfirmed, kept.Evidence);   // เดิม: ถูกตั้งกลับ PaperRequired ทั้งที่ยืนยันใบกำกับใบนี้ไว้แล้ว
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.True(await c.AuditLogs.AnyAsync(a => a.CompanyId == kept.S.Buyer.Id && a.EntityId == kept.DraftId.ToString()
                && a.NewValues != null && a.NewValues.Contains("\"externalConfirmationKept\":true")));
        // ทิศตรงข้าม: เลขที่ใบกำกับที่คนบันทึกไม่ตรงกับใบที่มาถึง ⇒ ตั้งกลับให้ยืนยันต้นฉบับใบกำกับใบนี้ (พฤติกรรมเดิมของ D-H4)
        var reset = await RunAsync(sameNumber: false);
        Assert.Equal(PartnerInputVatEvidence.PaperRequired, reset.Evidence);
    }

    [Fact]
    public async Task Z_MED5_ยืนยันแล้วแต่สายไม่มีใบกำกับ_ตัวหาเจอ_ทิศตรงข้ามยืนยันผ่านทางนอกระบบ_และสายที่มีใบกำกับ_ไม่เจอ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        // ใบที่ 1 (ข้อมูลรุ่นก่อน D-H4): ต้นทางเป็นใบแจ้งหนี้ · ผู้รับกด "ได้ต้นฉบับ" ไว้ · ใบกำกับไม่เคยมา
        var s = await SeedAsync(db);
        var inv = await SellerDocAsync(db, s, DocumentType.Invoice);
        var l1 = await Svc(db).OnDocumentSentAsync(inv);
        using var c1 = DbTestDatabase.TryCreateContext()!;
        var legacy = await DraftOfAsync(c1, s.Buyer.Id, l1!.Id);
        await c1.Documents.Where(d => d.Id == legacy.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.PartnerInputVatEvidence, PartnerInputVatEvidence.PaperConfirmed));
        // ใบที่ 2 (ทิศตรงข้าม): ยืนยันผ่านทาง "ได้ใบกำกับนอกระบบ" ⇒ คำยืนยันของใบกำกับจริง
        var s2 = await SeedAsync(c1);
        var inv2 = await SellerDocAsync(c1, s2, DocumentType.Invoice);
        var m1 = await Svc(c1).OnDocumentSentAsync(inv2);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var external = await DraftOfAsync(c2, s2.Buyer.Id, m1!.Id);
        await Svc(c2).ConfirmExternalTaxInvoiceAsync(s2.Buyer.Id, external.Id, Guid.NewGuid(), false, "IV-EXT-Z5", DateTime.UtcNow.Date);
        // ใบที่ 3 (ทิศตรงข้าม): สายมีใบกำกับ + ยืนยันตามปกติ
        var s3 = await SeedAsync(c2);
        var tiv3 = await SellerDocAsync(c2, s3, DocumentType.TaxInvoice);
        var n1 = await Svc(c2).OnDocumentSentAsync(tiv3);
        using var c3 = DbTestDatabase.TryCreateContext()!;
        var normal = await DraftOfAsync(c3, s3.Buyer.Id, n1!.Id);
        await Svc(c3).ConfirmPartnerOriginalAsync(s3.Buyer.Id, normal.Id, Guid.NewGuid());

        using var check = DbTestDatabase.TryCreateContext()!;
        var hit = Assert.Single(await PartnerEvidenceRepair.FindNoCarrierAsync(check, s.Buyer.Id));
        Assert.Equal((legacy.Id, DocumentStatus.Draft), (hit.DocumentId, hit.Status));
        Assert.True(PartnerEvidenceRepair.NotApproved(hit.Status));
        Assert.Empty(await PartnerEvidenceRepair.FindNoCarrierAsync(check, s2.Buyer.Id));
        Assert.Empty(await PartnerEvidenceRepair.FindNoCarrierAsync(check, s3.Buyer.Id));
        Assert.Empty(await PartnerEvidenceRepair.FindAsync(check, s.Buyer.Id));   // กลุ่มแรก (ต้องมีใบกำกับในสาย) ไม่เจอใบนี้ — เหตุที่ต้องมีกลุ่มที่สอง
    }
}

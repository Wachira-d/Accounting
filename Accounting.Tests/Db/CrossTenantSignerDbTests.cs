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
/// รอบ 2026-10-14 ทีม F4 (PAYMENT_CHANNEL_AUDIT ส่วนที่ 2 · ID#10) — ผู้ลงนามของ workflow ข้ามบริษัทบน PostgreSQL จริง
/// <para>(1) ผู้อนุมัติเริ่มต้นที่ออกจากบริษัทแล้ว ⇒ ไม่อนุมัติ/ลงนามอัตโนมัติ (เอกสารเข้ายังรออนุมัติ + หมายเหตุบอกเหตุและทางไปต่อ) ·
/// ทิศตรงข้าม: ยังเป็นสมาชิก ⇒ อนุมัติอัตโนมัติในชื่อคนนั้นตามเดิม</para>
/// <para>(2) อนุมัติด้วยมือโดยระบุผู้ลงนามที่เป็นผู้ใช้ของบริษัทอื่น ⇒ ปฏิเสธ ไม่มีลายเซ็นถูกประทับ</para>
/// <para>(3) ตั้งผู้อนุมัติเริ่มต้นเป็นคนนอกบริษัท ⇒ ปฏิเสธ · ลายเซ็นเริ่มต้นต้องเป็นของผู้อนุมัติคนนั้น</para>
/// </summary>
[Trait("Category", "Db")]
public class CrossTenantSignerDbTests
{
    private readonly ITestOutputHelper _out;
    public CrossTenantSignerDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid SellerId, Guid BuyerId, Guid LinkId, Guid DefaultApprover, Guid Outsider);

    private static async Task<Guid> UserAsync(Accounting.Data.AccountingDbContext db, Guid? memberOf, string name)
    {
        var u = new User { Email = $"f4x-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = name };
        db.Users.Add(u);
        if (memberOf is { } cid)
            db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = cid, Role = UserRole.Staff });
        await db.SaveChangesAsync();
        return u.Id;
    }

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, bool defaultApproverIsMember)
    {
        var seller = new Company { Name = "ผู้ขายข้ามบริษัท", TaxId = "0105556000011" };
        var buyer = new Company { Name = "ผู้ซื้อข้ามบริษัท", TaxId = "0105556000012" };
        db.Companies.AddRange(seller, buyer);
        await db.SaveChangesAsync();
        var defaultApprover = await UserAsync(db, defaultApproverIsMember ? buyer.Id : null, "ผู้อนุมัติเริ่มต้น");
        var outsider = await UserAsync(db, seller.Id, "ผู้ใช้ของบริษัทอื่น");
        var contact = new Contact { CompanyId = seller.Id, Name = buyer.Name, TaxId = buyer.TaxId, IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var quote = new Document
        {
            CompanyId = seller.Id, ContactId = contact.Id, DocumentType = DocumentType.Quotation, DocumentNumber = "QT-" + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Sent, DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, TotalAmount = 1_070m, VatAmount = 70m,
            BalanceDue = 1_070m, Currency = "THB",
        };
        quote.Lines.Add(new DocumentLine { LineOrder = 1, Description = "บริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatAmount = 70m });
        db.Documents.Add(quote);
        var partnership = new TradingPartnership
        {
            CompanyAId = seller.Id, CompanyBId = buyer.Id, Status = TradingPartnershipStatus.Accepted, InvitedByCompanyId = seller.Id,
        };
        db.TradingPartnerships.Add(partnership);
        db.WorkflowAutomationConfigs.Add(new WorkflowAutomationConfig
        {
            CompanyId = buyer.Id, AutoApproveIncomingQuotations = true, DefaultApproverUserId = defaultApprover,
        });
        await db.SaveChangesAsync();
        var link = new CrossTenantDocumentLink
        {
            TradingPartnershipId = partnership.Id, SourceCompanyId = seller.Id, SourceDocumentId = quote.Id, TargetCompanyId = buyer.Id,
            LinkType = CrossTenantLinkType.QuotationFlow, Status = CrossTenantLinkStatus.PendingApproval,
        };
        db.CrossTenantDocumentLinks.Add(link);
        await db.SaveChangesAsync();
        return new Seed(seller.Id, buyer.Id, link.Id, defaultApprover, outsider);
    }

    private static CrossTenantWorkflowService Svc(Accounting.Data.AccountingDbContext db) =>
        new(db, NullLogger<CrossTenantWorkflowService>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ผู้อนุมัติเริ่มต้นออกแล้ว_ไม่อนุมัติอัตโนมัติพร้อมหมายเหตุ_ยังเป็นสมาชิก_อนุมัติอัตโนมัติ(bool isMember)
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db, isMember);
        var link = await db.CrossTenantDocumentLinks.SingleAsync(l => l.Id == s.LinkId);

        await Svc(db).TryAutoApproveAsync(link);

        using var check = DbTestDatabase.TryCreateContext()!;
        var saved = await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == s.LinkId);
        var approvals = await check.DocumentApprovals.AsNoTracking()
            .Where(a => a.CompanyId == s.SellerId && a.ApprovalType == "External").ToListAsync();
        if (!isMember)
        {
            Assert.Equal(CrossTenantLinkStatus.PendingApproval, saved.Status);
            Assert.Contains("ผู้อนุมัติเริ่มต้น", saved.Comment ?? "");            // ไม่ค้างเงียบ — กล่องเอกสารเข้าเห็นเหตุ
            Assert.Empty(approvals);                                              // ไม่มีลายเซ็นในนามคนที่ออกไปแล้ว
            Assert.Equal(CrossTenantSignerResolver.DepartedDefaultApproverNotice,
                await Svc(check).GetDefaultApproverNoticeAsync(await Svc(check).GetOrCreateConfigAsync(s.BuyerId)));
        }
        else
        {
            Assert.Equal(CrossTenantLinkStatus.Approved, saved.Status);
            Assert.Equal(s.DefaultApprover, Assert.Single(approvals).ApproverUserId);
        }
    }

    [Fact]
    public async Task อนุมัติด้วยมือ_ระบุผู้ลงนามของบริษัทอื่น_ถูกปฏิเสธ_ไม่มีลายเซ็นถูกประทับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, defaultApproverIsMember: true);
        var actor = await UserAsync(db, s.BuyerId, "ผู้กดอนุมัติ");

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Svc(db).ApproveIncomingAsync(s.BuyerId, s.LinkId, actor, signatureUserId: s.Outsider));
        Assert.Equal(CrossTenantSignerResolver.RuleCode, ex.RuleCode);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(CrossTenantLinkStatus.PendingApproval,
            (await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == s.LinkId)).Status);
        Assert.False(await check.DocumentApprovals.AnyAsync(a => a.CompanyId == s.SellerId && a.ApprovalType == "External"));
    }

    [Fact]
    public async Task ตั้งผู้อนุมัติเริ่มต้นเป็นคนนอก_ถูกปฏิเสธ_ลายเซ็นต้องเป็นของผู้อนุมัติ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, defaultApproverIsMember: true);
        var svc = Svc(db);
        var cfg = await svc.GetOrCreateConfigAsync(s.BuyerId);

        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ApplyDefaultSignerAsync(cfg, s.Outsider, null));
        Assert.Equal(s.DefaultApprover, cfg.DefaultApproverUserId);              // ปฏิเสธแล้วค่าเดิมไม่ถูกแตะ

        var outsiderSig = new UserSignature { UserId = s.Outsider, SignatureData = "AAAA", IsActive = true };
        var ownSig = new UserSignature { UserId = s.DefaultApprover, SignatureData = "BBBB", IsActive = true };
        db.UserSignatures.AddRange(outsiderSig, ownSig);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ApplyDefaultSignerAsync(cfg, null, outsiderSig.Id));
        await svc.ApplyDefaultSignerAsync(cfg, null, ownSig.Id);                 // ทิศตรงข้าม: ลายเซ็นของผู้อนุมัติเอง ตั้งได้
        Assert.Equal(ownSig.Id, cfg.DefaultSignatureId);
    }
}

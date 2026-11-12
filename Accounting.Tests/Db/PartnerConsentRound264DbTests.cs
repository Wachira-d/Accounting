using System.Security.Claims;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Scope = Accounting.Helpers.PartnerConsent.Scope;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 264 P1 — ความยินยอม "งานอัตโนมัติกับคู่ค้า" บน PostgreSQL จริง: ให้ (สองขั้น) → ถอน → ให้ใหม่ (unique บางส่วน) · แทนที่ = ถอน+แถวใหม่ ·
/// ระงับคู่ = หยุด และเชื่อมกลับไม่ฟื้นเอง · เพดาน · ความยินยอมไม่กว้างกว่าสิทธิ์ผู้ให้ (ตอนให้ + ตอนใช้) · แยกบริษัท · trigger append-only ·
/// hash เขียน→อ่านจากฐาน→ตรวจ · audit chain ของผู้ให้สะอาด (<c>AuditHashChain.Analyze</c>) · ค่าตั้งเดิมระดับบริษัทเข้า audit (C-06)
/// </summary>
[Trait("Category", "Db")]
public class PartnerConsentRound264DbTests
{
    private readonly ITestOutputHelper _out;
    public PartnerConsentRound264DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static readonly Random Rng = new();
    private static string RandTax()
    {
        lock (Rng) return "08" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Rng.Next(10))));
    }

    private sealed record Seed(Guid A, Guid B, Guid PartnershipId, Guid OwnerB, Guid BankB);

    private static async Task<Guid> UserAsync(AccountingDbContext db, Guid companyId, UserRole role, Guid? companyRoleId = null)
    {
        var u = new User { Email = $"r264-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ทดสอบ 264" };
        db.Users.Add(u);
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = companyId, Role = role, CompanyRoleId = companyRoleId });
        await db.SaveChangesAsync();
        return u.Id;
    }

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        foreach (var sql in PartnerConsentMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);   // idempotent + trigger append-only
        var a = new Company { Name = "ผู้ขาย 264 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000" };
        var b = new Company { Name = "ผู้ซื้อ 264 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        var (lo, hi) = a.Id.CompareTo(b.Id) < 0 ? (a.Id, b.Id) : (b.Id, a.Id);
        var p = new TradingPartnership { CompanyAId = lo, CompanyBId = hi, InvitedByCompanyId = a.Id, Status = TradingPartnershipStatus.Accepted };
        db.TradingPartnerships.Add(p);
        var bank = new BankAccount { CompanyId = b.Id, AccountName = "กระแสรายวัน", BankName = "กสิกร", AccountNumber = "1112223334" };
        db.BankAccounts.Add(bank);
        await db.SaveChangesAsync();
        var owner = await UserAsync(db, b.Id, UserRole.Owner);
        return new Seed(a.Id, b.Id, p.Id, owner, bank.Id);
    }

    private static PartnerConsentService Svc(AccountingDbContext db, INotificationEngine? notify = null)
        => new(db, new PermissionService(db), NullLogger<PartnerConsentService>.Instance, notify);

    /// <summary>ขั้นดูข้อความ → ขั้นยินยอม ด้วย hash ที่ขั้นแรกคืน (เส้นเดียวกับหน้า)</summary>
    private static async Task<PartnerAutomationConsent> GrantAsync(AccountingDbContext db, Guid companyId, Guid userId, Guid pid, Scope s, decimal? max, Guid? bank = null)
    {
        var pv = await Svc(db).PreviewAsync(companyId, userId, new PartnerConsentService.PreviewRequest(pid, s.ToString(), max, bank));
        return await Svc(db).GrantAsync(companyId, userId, new PartnerConsentService.GrantRequest(pid, s.ToString(), pv.MaxAmount, bank,
            pv.TextVersion, pv.EvidenceHash, pv.NeedsTypedConfirm ? PartnerConsent.ConfirmPhrase : null));
    }

    private static Task<List<AuditLog>> ChainAsync(AccountingDbContext db, Guid companyId)
        => db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId && a.RowHash != null).OrderBy(a => a.Id).ToListAsync();

    [Fact]
    public async Task ให้_ถอน_ให้ใหม่_unique_บางส่วน_hash_จากฐานตรวจผ่าน_audit_chain_สะอาด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);

        PartnerAutomationConsent first;
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            first = await GrantAsync(c1, s.B, s.OwnerB, s.PartnershipId, Scope.AutoRecordPayment, 5_000m, s.BankB);
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var ok = await Svc(c2).CheckAsync(s.B, s.PartnershipId, Scope.AutoRecordPayment, 4_000m);
            Assert.True(ok.Ok, ok.Reason);
            Assert.Equal(first.Id, ok.ConsentId);
            Assert.Equal(s.OwnerB, ok.GrantorUserId);
            Assert.Equal(s.BankB, ok.DefaultPayBankAccountId);
            // hash round-trip: แถวที่อ่านกลับจากฐาน (numeric(18,2) · timestamp) ยังตรวจผ่านด้วยฟังก์ชันเดียวกับตอนเขียน
            var stored = await c2.PartnerAutomationConsents.AsNoTracking().SingleAsync(x => x.Id == first.Id);
            Assert.True(PartnerConsent.HashMatches(stored));
        }
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            await Svc(c3).WithdrawAsync(s.B, s.OwnerB, first.Id, "ทดสอบถอน");
        using (var c4 = DbTestDatabase.TryCreateContext()!)
        {
            var off = await Svc(c4).CheckAsync(s.B, s.PartnershipId, Scope.AutoRecordPayment, 4_000m);
            Assert.False(off.Ok);
            Assert.Equal(PartnerConsent.Codes.NoConsent, off.Code);
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(c4).WithdrawAsync(s.B, s.OwnerB, first.Id, null));   // ถอนซ้ำ = 409
        }
        PartnerAutomationConsent second;
        using (var c5 = DbTestDatabase.TryCreateContext()!)
            second = await GrantAsync(c5, s.B, s.OwnerB, s.PartnershipId, Scope.AutoRecordPayment, 6_000m);
        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.PartnerAutomationConsents.AsNoTracking().Where(x => x.CompanyId == s.B).OrderBy(x => x.GrantedAt).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].WithdrawnAt);
        Assert.Equal("ทดสอบถอน", rows[0].WithdrawReason);
        Assert.Null(rows[1].WithdrawnAt);
        Assert.Equal(second.Id, rows[1].Id);
        var events = await check.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.B && a.EntityType == nameof(PartnerAutomationConsent)).Select(a => a.NewValues!).ToListAsync();
        Assert.Equal(2, events.Count(e => e.Contains("PartnerConsentGranted")));
        Assert.Equal(1, events.Count(e => e.Contains("PartnerConsentWithdrawn")));
        Assert.All(events, e => Assert.Contains(PartnerConsent.RuleCode, e));
        Assert.False(AuditHashChain.Analyze(await ChainAsync(check, s.B)).HasIntegrityFindings);
    }

    [Fact]
    public async Task เปลี่ยนเพดาน_ถอนแถวเดิม_แถวใหม่หนึ่งแถว_ไม่แก้ทับ_trigger_กันแก้ตรง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        PartnerAutomationConsent r1, r2;
        using (var c1 = DbTestDatabase.TryCreateContext()!) r1 = await GrantAsync(c1, s.B, s.OwnerB, s.PartnershipId, Scope.AutoApprovePurchase, 1_000m);
        using (var c2 = DbTestDatabase.TryCreateContext()!) r2 = await GrantAsync(c2, s.B, s.OwnerB, s.PartnershipId, Scope.AutoApprovePurchase, 2_000m);
        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.PartnerAutomationConsents.AsNoTracking().Where(x => x.CompanyId == s.B).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, x => x.WithdrawnAt == null);
        Assert.Equal(1_000m, rows.Single(x => x.Id == r1.Id).MaxAmount);   // แถวเดิมคงค่าเดิม
        Assert.NotNull(rows.Single(x => x.Id == r1.Id).WithdrawnAt);
        Assert.Equal(2_000m, rows.Single(x => x.Id == r2.Id).MaxAmount);

        // ระดับฐาน: แก้เพดานแถวที่ใช้อยู่ตรง ๆ ไม่ได้ · แถวที่ถอนแล้วแตะไม่ได้เลย
        await Assert.ThrowsAnyAsync<Exception>(() => check.Database.ExecuteSqlRawAsync(
            "UPDATE \"PartnerAutomationConsents\" SET \"MaxAmount\" = 999999 WHERE \"Id\" = {0}", r2.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => check.Database.ExecuteSqlRawAsync(
            "UPDATE \"PartnerAutomationConsents\" SET \"WithdrawReason\" = 'x' WHERE \"Id\" = {0}", r1.Id));
        // ทิศตรงข้าม: ช่องถอนของแถวที่ใช้อยู่ประทับได้ (เส้นถอนปกติ)
        Assert.Equal(1, await check.Database.ExecuteSqlRawAsync(
            "UPDATE \"PartnerAutomationConsents\" SET \"WithdrawnAt\" = now(), \"WithdrawReason\" = 'sql' WHERE \"Id\" = {0}", r2.Id));
        Assert.False(AuditHashChain.Analyze(await ChainAsync(check, s.B)).HasIntegrityFindings);
    }

    [Fact]
    public async Task ระงับคู่_หยุดทุกขอบเขต_เชื่อมกลับ_ความยินยอมเดิมไม่ฟื้นเอง_ทิศตรงข้าม_ให้ใหม่ผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            await GrantAsync(c1, s.B, s.OwnerB, s.PartnershipId, Scope.ReceiveSlipCopy, null);
        }
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            await new CrossTenantWorkflowService(c2, NullLogger<CrossTenantWorkflowService>.Instance).SuspendPartnershipAsync(s.B, s.PartnershipId, s.OwnerB, "ทดสอบ");
        using (var c3 = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(PartnerConsent.Codes.PartnerNotActive, (await Svc(c3).CheckAsync(s.B, s.PartnershipId, Scope.ReceiveSlipCopy, null)).Code);
            // ระหว่างระงับให้ใหม่ไม่ได้
            await Assert.ThrowsAsync<BusinessRuleException>(() => GrantAsync(c3, s.B, s.OwnerB, s.PartnershipId, Scope.ReceiveSlipCopy, null));
        }
        await db.TradingPartnerships.Where(p => p.Id == s.PartnershipId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, TradingPartnershipStatus.Accepted));
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(PartnerConsent.Codes.StaleAfterSuspend, (await Svc(c4).CheckAsync(s.B, s.PartnershipId, Scope.ReceiveSlipCopy, null)).Code);
        using (var c5 = DbTestDatabase.TryCreateContext()!)
            await GrantAsync(c5, s.B, s.OwnerB, s.PartnershipId, Scope.ReceiveSlipCopy, null);
        using var c6 = DbTestDatabase.TryCreateContext()!;
        Assert.True((await Svc(c6).CheckAsync(s.B, s.PartnershipId, Scope.ReceiveSlipCopy, null)).Ok);
    }

    [Fact]
    public async Task เพดาน_เกิน_ไม่ผ่าน_เท่า_ผ่าน_ไม่รู้ยอด_ไม่ผ่าน_ไม่มีเพดานให้ไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        using (var c0 = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => GrantAsync(c0, s.B, s.OwnerB, s.PartnershipId, Scope.AutoRecordPayment, null));
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await GrantAsync(c1, s.B, s.OwnerB, s.PartnershipId, Scope.AutoRecordPayment, 10_700m);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(PartnerConsent.Codes.OverCap, (await Svc(c2).CheckAsync(s.B, s.PartnershipId, Scope.AutoRecordPayment, 10_700.01m)).Code);
        Assert.True((await Svc(c2).CheckAsync(s.B, s.PartnershipId, Scope.AutoRecordPayment, 10_700m)).Ok);
        Assert.Equal(PartnerConsent.Codes.AmountUnknown, (await Svc(c2).CheckAsync(s.B, s.PartnershipId, Scope.AutoRecordPayment, null)).Code);
        // ขอบเขตอื่นของคู่เดียวกันไม่ได้รับไปด้วย
        Assert.Equal(PartnerConsent.Codes.NoConsent, (await Svc(c2).CheckAsync(s.B, s.PartnershipId, Scope.AutoApprovePurchase, 1m)).Code);
    }

    [Fact]
    public async Task สิทธิ์ผู้ให้_ไม่พอให้ไม่ได้_พอให้ได้_ถูกลดสิทธิ์หรือปิดผู้ใช้แล้วหยุดเอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var role = new CompanyRole { CompanyId = s.B, Name = "ผู้ตั้งค่า 264" };
        db.CompanyRoles.Add(role);
        db.CompanyRolePermissions.Add(new CompanyRolePermission { CompanyRoleId = role.Id, MenuItemId = PermissionKeys.CompanySettingsEdit, CanAccess = true });
        await db.SaveChangesAsync();
        var staff = await UserAsync(db, s.B, UserRole.Staff, role.Id);

        // ตั้งค่าบริษัทได้แต่อนุมัติใบซื้อไม่ได้ ⇒ เปิด "บันทึกจ่ายเอง" ไม่ได้ (403) · ขอบเขตแคบที่ไม่ต้องอนุมัติก็ยังต้องเห็นฝั่งซื้อ
        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => GrantAsync(c1, s.B, staff, s.PartnershipId, Scope.AutoRecordPayment, 1_000m));
            Assert.Equal(403, ex.StatusCode);
            Assert.Contains("Document.Purchase.Approve", ex.Message);
            Assert.Equal(0, await c1.PartnerAutomationConsents.CountAsync(x => x.CompanyId == s.B));
        }
        // ทิศตรงข้าม: ให้สิทธิ์อนุมัติใบซื้อ ⇒ ให้ได้ และใช้ได้
        var approve = new CompanyRolePermission { CompanyRoleId = role.Id, MenuItemId = PermissionKeys.DocumentPurchaseApprove, CanAccess = true };
        db.CompanyRolePermissions.Add(approve);
        await db.SaveChangesAsync();
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            await GrantAsync(c2, s.B, staff, s.PartnershipId, Scope.AutoRecordPayment, 1_000m);
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            Assert.True((await Svc(c3).CheckAsync(s.B, s.PartnershipId, Scope.AutoRecordPayment, 500m)).Ok);

        // ถูกลดสิทธิ์ ⇒ ตอนใช้หยุดเอง (ไม่ต้องถอนแถว)
        await db.CompanyRolePermissions.Where(x => x.Id == approve.Id).ExecuteDeleteAsync();
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(PartnerConsent.Codes.GrantorLostPermission, (await Svc(c4).CheckAsync(s.B, s.PartnershipId, Scope.AutoRecordPayment, 500m)).Code);
        // คืนสิทธิ์แต่ปิดผู้ใช้ ⇒ ยังหยุด
        db.CompanyRolePermissions.Add(new CompanyRolePermission { CompanyRoleId = role.Id, MenuItemId = PermissionKeys.DocumentPurchaseApprove, CanAccess = true });
        await db.SaveChangesAsync();
        using (var c5 = DbTestDatabase.TryCreateContext()!)
            Assert.True((await Svc(c5).CheckAsync(s.B, s.PartnershipId, Scope.AutoRecordPayment, 500m)).Ok);
        await db.Users.Where(u => u.Id == staff).ExecuteUpdateAsync(x => x.SetProperty(u => u.Status, UserStatus.Inactive));
        using var c6 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(PartnerConsent.Codes.GrantorLostPermission, (await Svc(c6).CheckAsync(s.B, s.PartnershipId, Scope.AutoRecordPayment, 500m)).Code);
    }

    [Fact]
    public async Task แยกบริษัท_คู่ค้าไม่เห็นและไม่ได้รับความยินยอมของอีกฝั่ง_บริษัทนอกคู่ไม่พบ_คำขอจากคู่ค้าไม่สร้างแถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var ownerA = await UserAsync(db, s.A, UserRole.Owner);
        var outsider = new Company { Name = "นอกคู่ 264 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000" };
        db.Companies.Add(outsider);
        await db.SaveChangesAsync();
        var ownerX = await UserAsync(db, outsider.Id, UserRole.Owner);

        PartnerAutomationConsent row;
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            row = await GrantAsync(c1, s.B, s.OwnerB, s.PartnershipId, Scope.AutoApprovePurchase, 3_000m);

        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            // A (อีกฝั่งของคู่เดียวกัน) ไม่ได้รับความยินยอมของ B
            Assert.Equal(PartnerConsent.Codes.NoConsent, (await Svc(c2).CheckAsync(s.A, s.PartnershipId, Scope.AutoApprovePurchase, 1m)).Code);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => Svc(c2).WithdrawAsync(s.A, ownerA, row.Id, null));
            var pageA = await Svc(c2).ListAsync(s.A, ownerA);
            Assert.All(pageA.Partners.SelectMany(p => p.Scopes), sc => Assert.Null(sc.Active));
            Assert.All(pageA.Partners, p => Assert.Empty(p.History));
            // บริษัทนอกคู่ = ไม่พบคู่ (ไม่บอกว่ามีแถว)
            Assert.Equal(PartnerConsent.Codes.NoPartner, (await Svc(c2).CheckAsync(outsider.Id, s.PartnershipId, Scope.AutoApprovePurchase, 1m)).Code);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => GrantAsync(c2, outsider.Id, ownerX, s.PartnershipId, Scope.ReceiveSlipCopy, null));
            Assert.Empty((await Svc(c2).ListAsync(outsider.Id, ownerX)).Partners);
        }

        // A ขอ ⇒ แจ้งกระดิ่งของ B พร้อมลิงก์หน้าความยินยอม · ไม่มีแถวความยินยอมเกิด (F9)
        var sent = new List<(Guid Company, NotificationContext Ctx)>();
        var notify = F4Proxy<INotificationEngine>.Create((m, args) =>
        {
            if (m == nameof(INotificationEngine.DispatchAsync)) sent.Add(((Guid)args[0]!, (NotificationContext)args[2]!));
            return null;
        });
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            await Svc(c3, notify).RequestFromPartnerAsync(s.A, ownerA, s.PartnershipId, new[] { nameof(Scope.AutoRecordPayment) });
        Assert.Single(sent);
        Assert.Equal(s.B, sent[0].Company);
        Assert.Equal(PartnerConsent.PageUrl(s.PartnershipId), sent[0].Ctx.ActionUrl);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(0, await check.PartnerAutomationConsents.CountAsync(x => x.CompanyId == s.A));
        Assert.Equal(1, await check.PartnerAutomationConsents.CountAsync(x => x.CompanyId == s.B));
        // ขอบเขตทั้งบริษัทขอเป็นรายคู่ไม่ได้ (ทิศตรงข้าม)
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(check, notify).RequestFromPartnerAsync(s.A, ownerA, s.PartnershipId, new[] { nameof(Scope.PurchaseDrafts) }));
        foreach (var cid in new[] { s.A, s.B })
            Assert.False(AuditHashChain.Analyze(await ChainAsync(check, cid)).HasIntegrityFindings);
    }

    [Fact]
    public async Task ค่าตั้งเดิมระดับบริษัท_เปลี่ยนแล้วเข้า_audit_chain_ไม่เปลี่ยนไม่เขียน_และขอบเขตทั้งบริษัทอ่านค่าตั้งนั้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        CrossTenantWorkflowController Ctl(AccountingDbContext c)
        {
            var ctl = new CrossTenantWorkflowController(new CrossTenantWorkflowService(c, NullLogger<CrossTenantWorkflowService>.Instance), c,
                new PermissionService(c), new ChatRateLimiter(c, NullLogger<ChatRateLimiter>.Instance));
            ctl.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, s.OwnerB.ToString()) }, "test")),
                },
            };
            return ctl;
        }
        using (var c0 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(PartnerConsent.Codes.CompanyWideOff, (await Svc(c0).CheckAsync(s.B, s.PartnershipId, Scope.PurchaseDrafts, null)).Code);
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await Ctl(c1).UpdateConfig(s.B, new CrossTenantWorkflowController.WorkflowConfigUpdate(null, null, null, null, null, null, null, null, null, null, null,
                CreatePurchaseDraftsFromPartners: true));
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            Assert.True((await Svc(c2).CheckAsync(s.B, s.PartnershipId, Scope.PurchaseDrafts, null)).Ok);
            // ส่งค่าเดิมซ้ำ ⇒ ไม่มีแถว audit ใหม่
            await Ctl(c2).UpdateConfig(s.B, new CrossTenantWorkflowController.WorkflowConfigUpdate(null, null, null, null, null, null, null, null, null, null, null,
                CreatePurchaseDraftsFromPartners: true));
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == s.B && a.EntityType == nameof(WorkflowAutomationConfig)).ToListAsync();
        var one = Assert.Single(rows);
        Assert.Equal(s.OwnerB, one.UserId);
        Assert.Contains("WorkflowAutomationConfigChanged", one.NewValues);
        Assert.Contains("createPurchaseDraftsFromPartners", one.NewValues);
        Assert.Contains("\"false\"", one.OldValues);
        Assert.False(AuditHashChain.Analyze(await ChainAsync(check, s.B)).HasIntegrityFindings);
    }
}

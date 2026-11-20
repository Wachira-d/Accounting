using System.Security.Claims;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Auth;
using Accounting.Models.DTOs.Company;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 277 (งาน B) — คำเชิญเข้าบริษัทบน PostgreSQL จริง: กดเชิญ ⇒ แถว "รอตอบรับ" ทันที (ทั้งอีเมลใหม่และผู้มีบัญชี — ไม่ยัดเข้าบริษัทเอง) ·
/// token เก็บเป็น hash · เชิญซ้ำ = แถวเดิม (ลิงก์เดิมตาย) · ผู้ใช้ใหม่สมัครผ่านลิงก์ (AuthService.RegisterAsync) ไม่เกิดบริษัทใหม่ ·
/// ผู้ใช้เดิมตอบรับ · อีเมลไม่ตรง/หมดอายุ/ใช้ซ้ำ/ยกเลิก ถูกปฏิเสธ · ไม่กำหนดบทบาท ⇒ ทุก API ของบริษัท 403 (TenantAccessMiddleware) จนเจ้าของตั้งบทบาท ·
/// ที่นั่งเต็มตอนรับ · migration token ดิบ → hash สูตรเดียวกับ C# · แจ้งเจ้าของเมื่อมีคนตอบรับ
/// </summary>
[Trait("Category", "Db")]
public class TeamInvitationDbTests
{
    private readonly ITestOutputHelper _out;
    public TeamInvitationDbTests(ITestOutputHelper output) => _out = output;

    private sealed class Captured { public readonly List<(string Key, NotificationContext Ctx)> Items = new(); }

    private static INotificationEngine Capture(Captured c) => F4Proxy<INotificationEngine>.Create((name, args) =>
    {
        if (name == nameof(INotificationEngine.DispatchAsync)) c.Items.Add(((string)args[1]!, (NotificationContext)args[2]!));
        return null;
    });

    private sealed class Sp : IServiceProvider
    {
        private readonly ISubscriptionService _subs;
        public Sp(ISubscriptionService subs) => _subs = subs;
        public object? GetService(Type t) => t == typeof(ISubscriptionService) ? _subs : null;
    }

    private static QuotaGateService Gate(AccountingDbContext db)
        => new(db, new Sp(new SubscriptionService(db, F4Proxy<INotificationService>.Create((_, _) => null))));

    private static CompanyService Company(AccountingDbContext db) => new(db, null!, quotaGate: Gate(db));

    private static CompanyInvitationService Inv(AccountingDbContext db, Captured? c = null)
        => new(db, Gate(db), c == null ? null : Capture(c), NullLogger<CompanyInvitationService>.Instance);

    private static string TokenOf(Accounting.Services.Interfaces.AddUserResult r)
        => Uri.UnescapeDataString(r.InviteLink!.Split("token=")[1]);

    private sealed record Seed(Guid CompanyId, Guid Owner, string OwnerEmail);

    private static async Task<Seed> SeedAsync(AccountingDbContext db, int? seatLimit = null)
    {
        var c = new Company { Name = "บริษัททดสอบคำเชิญ " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000041" };
        db.Companies.Add(c);
        var owner = new User { Email = $"r277-owner-{Guid.NewGuid():N}@test.local", FullName = "เจ้าของ", PasswordHash = "x", Status = UserStatus.Active };
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { CompanyId = c.Id, UserId = owner.Id, Role = UserRole.Owner });
        if (seatLimit is int lim)
        {
            db.Subscriptions.Add(new Subscription
            {
                CompanyId = c.Id, Plan = SubscriptionPlan.Pro, Status = SubscriptionStatus.Active,
                StartDate = DateTime.UtcNow.AddDays(-10), EndDate = DateTime.UtcNow.AddDays(200), UsageResetDate = DateTime.UtcNow.AddDays(20),
            });
            db.CompanyQuotaOverrides.Add(new CompanyQuotaOverride { CompanyId = c.Id, ResourceKey = QuotaResources.Users, Limit = lim, Reason = "r277 test" });
        }
        await db.SaveChangesAsync();
        return new Seed(c.Id, owner.Id, owner.Email);
    }

    private static async Task<User> UserAsync(AccountingDbContext db, string? email = null)
    {
        var u = new User { Email = email ?? $"r277-u-{Guid.NewGuid():N}@test.local", FullName = "ผู้ถูกเชิญ", PasswordHash = "x", Status = UserStatus.Active };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    [Fact]
    public async Task เชิญอีเมลใหม่_แถวรอตอบรับทันที_tokenเก็บเป็นhash_ไม่มีสมาชิกเพิ่ม_เชิญซ้ำใช้แถวเดิมลิงก์เดิมตาย()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var email = $"r277-new-{Guid.NewGuid():N}@test.local";

        var r1 = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest(email.ToUpperInvariant()));
        Assert.True(r1.WasInvited);
        var list = await Company(db).ListInvitationsAsync(s.CompanyId, s.Owner);
        var row = Assert.Single(list);
        Assert.Equal(email, row.Email);
        Assert.False(row.RoleChosen);
        Assert.Equal("Pending", row.Status);
        Assert.Contains("ยังไม่กำหนดบทบาท", row.RoleLabel);
        Assert.True(row.ExpiresAt > DateTime.UtcNow.AddDays(6));
        Assert.Equal(1, await db.CompanyUsers.CountAsync(cu => cu.CompanyId == s.CompanyId));

        var token1 = TokenOf(r1);
        var stored = await db.CompanyInvitations.AsNoTracking().SingleAsync(i => i.Id == row.Id);
        Assert.Null(stored.Token);
        Assert.Equal(CompanyInvitationPolicy.HashToken(token1), stored.TokenHash);
        Assert.NotEqual(token1, stored.TokenHash);

        // เชิญซ้ำ (พร้อมบทบาท) ⇒ แถวเดิม · ลิงก์เดิมใช้ไม่ได้ · ลิงก์ใหม่ใช้ได้
        var r2 = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest(email, UserRole.Accountant));
        Assert.Equal(r1.InvitationId, r2.InvitationId);
        Assert.Single(await Company(db).ListInvitationsAsync(s.CompanyId, s.Owner));
        Assert.False(await Inv(db).IsUsableForEmailAsync(token1, email));
        Assert.True(await Inv(db).IsUsableForEmailAsync(TokenOf(r2), email));
        // audit ผ่าน hash chain
        Assert.True(await db.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityType == "CompanyInvitation" && a.EntityId == row.Id.ToString()));
    }

    [Fact]
    public async Task ผู้มีบัญชีแล้ว_ไม่ถูกยัดเข้าบริษัท_ต้องตอบรับเอง_ได้บทบาทที่เลือก_แจ้งเจ้าของ_ใช้ซ้ำไม่ได้()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var u = await UserAsync(db);

        var r = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest(u.Email, UserRole.Accountant, OwnDocumentsOnly: true));
        Assert.False(await db.CompanyUsers.AnyAsync(cu => cu.CompanyId == s.CompanyId && cu.UserId == u.Id));   // เดิม: เข้าทันที
        var preview = await Inv(db).PreviewAsync(TokenOf(r));
        Assert.DoesNotContain(typeof(InvitationPreviewResponse).GetProperties(), p => p.Name.Contains("Exist"));   // ฝ่ายค้าน LOW: ไม่บอกการมีบัญชี
        Assert.Equal(InvitationStatus.Pending, preview.Status);

        var cap = new Captured();
        var res = await Inv(db, cap).AcceptAsync(u.Id, TokenOf(r));
        Assert.Equal(s.CompanyId, res.CompanyId);
        Assert.False(res.AccessPending);
        var cu = await db.CompanyUsers.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.UserId == u.Id);
        Assert.Equal(UserRole.Accountant, cu.Role);
        Assert.False(cu.AccessPending);
        Assert.True(cu.OwnDocumentsOnly);
        Assert.True(cu.DraftDocumentsOnly);   // เห็นเฉพาะของตัวเอง ⇒ เฉพาะร่างด้วย (276-2)
        Assert.Contains(cap.Items, i => i.Key == NotificationEvents.TeamInvitationAccepted);
        Assert.Empty(await Company(db).ListInvitationsAsync(s.CompanyId, s.Owner));

        // ใช้ซ้ำ ⇒ ปฏิเสธ
        var again = await Assert.ThrowsAsync<BusinessRuleException>(() => Inv(db).AcceptAsync(u.Id, TokenOf(r)));
        Assert.Equal(CompanyInvitationPolicy.RuleCode, again.RuleCode);
        Assert.Equal(1, await db.CompanyUsers.CountAsync(x => x.CompanyId == s.CompanyId && x.UserId == u.Id));
    }

    [Fact]
    public async Task อีเมลไม่ตรง_หมดอายุ_ยกเลิก_tokenมั่ว_ถูกปฏิเสธ_ไม่เกิดสมาชิก()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var invited = $"r277-inv-{Guid.NewGuid():N}@test.local";
        var r = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest(invited, UserRole.Staff));
        var stranger = await UserAsync(db);

        var mismatch = await Assert.ThrowsAsync<BusinessRuleException>(() => Inv(db).AcceptAsync(stranger.Id, TokenOf(r)));
        Assert.Equal(403, mismatch.StatusCode);
        Assert.Contains(invited, mismatch.Message);
        var bogus = await Assert.ThrowsAsync<BusinessRuleException>(() => Inv(db).AcceptAsync(stranger.Id, "not-a-real-token"));
        Assert.Equal(404, bogus.StatusCode);

        // หมดอายุ ⇒ ปฏิเสธ + พลิกสถานะ
        var target = await UserAsync(db, invited);
        await db.CompanyInvitations.Where(i => i.Id == r.InvitationId).ExecuteUpdateAsync(x => x.SetProperty(i => i.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        var expired = await Assert.ThrowsAsync<BusinessRuleException>(() => Inv(db).AcceptAsync(target.Id, TokenOf(r)));
        Assert.Contains("หมดอายุ", expired.Message);
        Assert.Equal(InvitationStatus.Expired, (await db.CompanyInvitations.AsNoTracking().SingleAsync(i => i.Id == r.InvitationId)).Status);

        // ยกเลิก ⇒ ลิงก์ใช้ไม่ได้
        var r2 = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest($"r277-c-{Guid.NewGuid():N}@test.local"));
        await Company(db).CancelInvitationAsync(s.CompanyId, s.Owner, r2.InvitationId!.Value);
        Assert.False(await Inv(db).IsUsableForEmailAsync(TokenOf(r2), r2.Email));
        Assert.Equal(1, await db.CompanyUsers.CountAsync(x => x.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task ไม่กำหนดบทบาท_สมาชิกยังไม่มีสิทธิ์_ทุกAPIของบริษัท403ยกเว้นสิทธิ์ของฉัน_เจ้าของตั้งบทบาทแล้วผ่าน()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var u = await UserAsync(db);
        var r = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest(u.Email));
        var cap = new Captured();
        var res = await Inv(db, cap).AcceptAsync(u.Id, TokenOf(r));
        Assert.True(res.AccessPending);
        var cu = await db.CompanyUsers.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.UserId == u.Id);
        Assert.True(cu.AccessPending);
        Assert.Equal(UserRole.Viewer, cu.Role);
        Assert.Null(cu.CompanyRoleId);
        var note = Assert.Single(cap.Items, i => i.Key == NotificationEvents.TeamInvitationAccepted);
        Assert.Contains("ตั้ง", note.Ctx.Title);

        // ด่านทุกคำขอ: ทุก endpoint ของบริษัท 403 · ยกเว้น GET my-permissions
        Assert.Equal(403, await CallAsync(db, u.Id, s.CompanyId, "GET", $"/api/companies/{s.CompanyId}/documents"));
        Assert.Equal(403, await CallAsync(db, u.Id, s.CompanyId, "GET", $"/api/companies/{s.CompanyId}/dashboard"));
        Assert.Equal(403, await CallAsync(db, u.Id, s.CompanyId, "POST", $"/api/company/{s.CompanyId}/roles/my-permissions"));
        Assert.Equal(200, await CallAsync(db, u.Id, s.CompanyId, "GET", $"/api/company/{s.CompanyId}/roles/my-permissions"));
        // ทิศตรงข้าม: เจ้าของ (สมาชิกปกติ) ผ่าน
        Assert.Equal(200, await CallAsync(db, s.Owner, s.CompanyId, "GET", $"/api/companies/{s.CompanyId}/documents"));

        var perms = await new RolePermissionService(db, null, new PermissionService(db)).GetMyPermissionsAsync(s.CompanyId, u.Id);
        Assert.True(perms.AccessPending);
        Assert.Empty(perms.AllowedMenuIds);
        Assert.False(string.IsNullOrWhiteSpace(perms.AccessPendingNotice));

        // เจ้าของตั้งบทบาท ⇒ ปลดสถานะ (audit) ⇒ ผ่านด่าน
        await Company(db).UpdateUserRoleAsync(s.CompanyId, s.Owner, u.Id, UserRole.Staff);
        Assert.False((await db.CompanyUsers.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.UserId == u.Id)).AccessPending);
        Assert.Equal(200, await CallAsync(db, u.Id, s.CompanyId, "GET", $"/api/companies/{s.CompanyId}/documents"));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityType == "CompanyUser" && a.EntityId == u.Id.ToString()));
    }

    [Fact]
    public async Task ผูกบทบาทกำหนดเองให้สมาชิกรอสิทธิ์_ปลดสถานะ_ถอดบทบาทไม่ปลดใครเพิ่ม()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var u = await UserAsync(db);
        var r = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest(u.Email));
        await Inv(db).AcceptAsync(u.Id, TokenOf(r));
        var role = new CompanyRole { CompanyId = s.CompanyId, Name = "พนักงานขาย r277" };
        db.CompanyRoles.Add(role);
        await db.SaveChangesAsync();
        var rps = new RolePermissionService(db, null, new PermissionService(db));
        await rps.AssignRoleAsync(s.CompanyId, u.Id, s.Owner, new AssignCustomRoleRequest(role.Id));
        var cu = await db.CompanyUsers.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.UserId == u.Id);
        Assert.False(cu.AccessPending);
        Assert.Equal(role.Id, cu.CompanyRoleId);
    }

    [Fact]
    public async Task สมัครใหม่ผ่านลิงก์คำเชิญ_ไม่เกิดบริษัทใหม่แม้ส่งชื่อบริษัทมา_เข้าบริษัทที่เชิญ()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var email = $"r277-reg-{Guid.NewGuid():N}@test.local";
        var r = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest(email, UserRole.Staff));
        var stubName = "บริษัทที่ไม่ควรเกิด " + Guid.NewGuid().ToString("N")[..8];

        var auth = Auth(db);
        await auth.RegisterAsync(new RegisterRequest(email, "Passw0rd!x", "ผู้สมัครใหม่", null, null, null,
            CompanyName: stubName, InvitationToken: TokenOf(r), AcceptedTerms: true));

        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Email == email);
        Assert.False(await db.Companies.AnyAsync(c => c.Name == stubName));   // เทสต์อื่นรันขนาน — ไม่นับจำนวนบริษัททั้งฐาน
        var memberships = await db.CompanyUsers.AsNoTracking().Where(x => x.UserId == user.Id).ToListAsync();
        var m = Assert.Single(memberships);
        Assert.Equal(s.CompanyId, m.CompanyId);
        Assert.Equal(UserRole.Staff, m.Role);

        // ทิศตรงข้าม: สมัครปกติ (ไม่มีคำเชิญ) ยังเปิดบริษัทของตัวเองได้ตามเดิม
        var own = $"r277-own-{Guid.NewGuid():N}@test.local";
        await auth.RegisterAsync(new RegisterRequest(own, "Passw0rd!x", "เจ้าของใหม่", null, null, null, CompanyName: "บริษัทของฉัน r277", AcceptedTerms: true));
        var ownUser = await db.Users.AsNoTracking().SingleAsync(x => x.Email == own);
        Assert.True(await db.CompanyUsers.AnyAsync(x => x.UserId == ownUser.Id && x.Role == UserRole.Owner));
    }

    [Fact]
    public async Task ที่นั่ง_คำเชิญค้างกินที่นั่ง_หมดอายุคืนที่นั่ง_ยกเลิกคืนที่นั่ง_เต็มตอนรับถูกปฏิเสธ()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, seatLimit: 2);
        var a = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest($"r277-a-{Guid.NewGuid():N}@test.local", UserRole.Staff));
        var full = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest($"r277-b-{Guid.NewGuid():N}@test.local")));
        Assert.Equal("QUOTA-FULL:users", full.RuleCode);
        // เชิญซ้ำอีเมลเดิมไม่กินที่นั่งเพิ่ม (ทิศตรงข้าม)
        await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest(a.Email, UserRole.Staff));

        await Company(db).CancelInvitationAsync(s.CompanyId, s.Owner, a.InvitationId!.Value);
        var b = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest($"r277-b-{Guid.NewGuid():N}@test.local"));
        await db.CompanyInvitations.Where(i => i.Id == b.InvitationId).ExecuteUpdateAsync(x => x.SetProperty(i => i.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        db.ChangeTracker.Clear();   // ExecuteUpdate ไม่แตะ entity ที่ติดตามอยู่ (จำลอง "คำขอใหม่")
        // หมดอายุแล้วไม่ถือที่นั่ง ⇒ เชิญคนใหม่ได้ · ส่งซ้ำคำเชิญที่หมดอายุต้องมีที่ว่าง (ตอนนี้เต็ม) ⇒ ปฏิเสธ
        var c = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest($"r277-c-{Guid.NewGuid():N}@test.local", UserRole.Staff));
        await Assert.ThrowsAsync<BusinessRuleException>(() => Company(db).ResendInvitationAsync(s.CompanyId, s.Owner, b.InvitationId!.Value));

        // แพ็กเกจถูกลดหลังเชิญ ⇒ ตอบรับไม่ได้ (บอกเหตุ)
        await db.CompanyQuotaOverrides.Where(o => o.CompanyId == s.CompanyId).ExecuteUpdateAsync(x => x.SetProperty(o => o.Limit, 1));
        db.ChangeTracker.Clear();
        var cu = await UserAsync(db, c.Email);
        var seat = await Assert.ThrowsAsync<BusinessRuleException>(() => Inv(db).AcceptAsync(cu.Id, TokenOf(c)));
        Assert.Contains("ที่นั่ง", seat.Message);
        Assert.False(await db.CompanyUsers.AnyAsync(x => x.CompanyId == s.CompanyId && x.UserId == cu.Id));
    }

    [Fact]
    public async Task Migration_tokenดิบเดิมกลายเป็นhashสูตรเดียวกับCSharp_ลิงก์เดิมยังใช้ได้_รันซ้ำได้()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var raw = "legacy-" + Guid.NewGuid().ToString("N");
        var email = $"r277-legacy-{Guid.NewGuid():N}@test.local";
        var inv = new CompanyInvitation
        {
            CompanyId = s.CompanyId, Email = email, Role = UserRole.Staff, InvitedByUserId = s.Owner,
            Token = raw, Status = InvitationStatus.Pending, ExpiresAt = DateTime.UtcNow.AddDays(3),
        };
        db.CompanyInvitations.Add(inv);
        await db.SaveChangesAsync();

        foreach (var sql in Round277Migration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        foreach (var sql in Round277Migration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);   // idempotent

        var row = await db.CompanyInvitations.AsNoTracking().SingleAsync(i => i.Id == inv.Id);
        Assert.Null(row.Token);
        Assert.Equal(CompanyInvitationPolicy.HashToken(raw), row.TokenHash);
        Assert.True(row.RoleChosen);
        var u = await UserAsync(db, email);
        var res = await Inv(db).AcceptAsync(u.Id, raw);
        Assert.False(res.AccessPending);
    }

    [Fact]
    public async Task ฝ่ายค้านHIGH_สมาชิกรอสิทธิ์_ไม่อยู่ในตัวกรองสมาชิก_หน้าสำนักงานบัญชีไม่เห็น_รายชื่อบริษัทไม่มีเลขภาษี_ตั้งบทบาทแล้วเห็น()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var u = await UserAsync(db);
        var r = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest(u.Email));
        await Inv(db).AcceptAsync(u.Id, TokenOf(r));

        Assert.False(await CompanyMembership.ActiveOf(db, u.Id).AnyAsync());                       // บอท LINE เลือกบริษัทจากตัวนี้
        Assert.False(await CompanyMembership.ActiveRow(db, s.CompanyId, u.Id).AnyAsync());           // me/permissions · SignalR
        Assert.False(await CompanyMembership.IsActiveMemberAsync(db, s.CompanyId, u.Id));            // ผู้อนุมัติ/ผู้ลงนาม
        Assert.True(await CompanyMembership.IsActiveMemberAsync(db, s.CompanyId, s.Owner));          // ทิศตรงข้าม

        var ws = await Workspace(db, u.Id);
        Assert.DoesNotContain(s.CompanyId.ToString(), ws);

        var list = await Company(db).GetUserCompaniesAsync(u.Id);
        var row = Assert.Single(list, c => c.Id == s.CompanyId);
        Assert.True(row.AccessPending);
        Assert.True(string.IsNullOrEmpty(row.TaxId));
        Assert.Null(row.Subscription);

        await Company(db).UpdateUserRoleAsync(s.CompanyId, s.Owner, u.Id, UserRole.Staff);
        Assert.True(await CompanyMembership.ActiveOf(db, u.Id).AnyAsync());
        Assert.Contains(s.CompanyId.ToString(), await Workspace(db, u.Id));
        var after = Assert.Single(await Company(db).GetUserCompaniesAsync(u.Id), c => c.Id == s.CompanyId);
        Assert.False(after.AccessPending);
        Assert.Equal("0105556000041", after.TaxId);
    }

    [Fact]
    public async Task ฝ่ายค้านMED3_ตอบรับด้วยtoken_ประทับยืนยันอีเมล_อีเมลไม่ตรงไม่ประทับ()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var u = await UserAsync(db);
        Assert.False(u.EmailVerified);
        var stranger = await UserAsync(db);
        var r = await Company(db).AddUserAsync(s.CompanyId, s.Owner, new AddCompanyUserRequest(u.Email, UserRole.Staff));
        await Assert.ThrowsAsync<BusinessRuleException>(() => Inv(db).AcceptAsync(stranger.Id, TokenOf(r)));
        Assert.False(await db.Users.AsNoTracking().Where(x => x.Id == stranger.Id).Select(x => x.EmailVerified).SingleAsync());
        await Inv(db).AcceptAsync(u.Id, TokenOf(r));
        Assert.True(await db.Users.AsNoTracking().Where(x => x.Id == u.Id).Select(x => x.EmailVerified).SingleAsync());
    }

    private static async Task<string> Workspace(AccountingDbContext db, Guid userId)
    {
        var ctl = new Accounting.Controllers.AccountantWorkspaceController(db);
        ctl.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test")),
            },
        };
        var res = await ctl.GetWorkspace();
        var ok = (Microsoft.AspNetCore.Mvc.OkObjectResult)res.Result!;
        return System.Text.Json.JsonSerializer.Serialize(ok.Value);
    }

    // ── ตัวช่วย ──

    private static async Task<int> CallAsync(AccountingDbContext db, Guid userId, Guid companyId, string method, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.RouteValues["companyId"] = companyId.ToString();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"));
        ctx.Response.Body = new MemoryStream();
        var nextCalled = false;
        var mw = new TenantAccessMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        await mw.InvokeAsync(ctx, db);
        return nextCalled ? 200 : ctx.Response.StatusCode;
    }

    private static AuthService Auth(AccountingDbContext db)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "r277-test-secret-0123456789-abcdefghijklmnopqrstuvwxyz-ABCDEFGHIJ",
            ["Jwt:Issuer"] = "accounting-test",
            ["Jwt:Audience"] = "accounting-test",
            ["Jwt:ExpireMinutes"] = "60",
        }).Build();
        return new AuthService(db, config,
            F4Proxy<IEmailService>.Create((_, _) => null),
            F4Proxy<ISubscriptionService>.Create((_, _) => null),
            F4Proxy<IAccountingService>.Create((_, _) => null),
            F4Proxy<ICompanyService>.Create((_, _) => null),
            NullLogger<AuthService>.Instance,
            invitations: Inv(db));
    }
}

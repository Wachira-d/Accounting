using System.Security.Claims;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// W9 R-63 — สิทธิ์เจ้าของบริษัทของทีม I (รอบ 205) บน PostgreSQL จริง:
/// <list type="bullet">
/// <item><c>POST admin/companies/{id}/assign-owner</c> พร้อมกัน 2 คำขอ (คนละผู้รับ) ใต้ <c>pg_advisory_xact_lock</c> ⇒ Owner คนเดียว ·
/// อีกคำขอได้ "มีเจ้าของแล้ว" (ไม่ใช่สองเจ้าของเงียบ ๆ)</item>
/// <item>ถอด/ลดสิทธิ์ Owner คนสุดท้าย ⇒ <see cref="LastOwnerGuard"/> ปฏิเสธ · ทิศตรงข้าม: มี Owner ลูกค้าอีกคน ⇒ ทำได้ ·
/// แอดมินแพลตฟอร์มที่เป็น Owner ไม่นับเป็น "เจ้าของอีกคน"</item>
/// <item>ตัวเลือก (<c>GET admin/companies/without-owner</c>): แอดมินแพลตฟอร์มที่ไม่ใช่ผู้สร้างไม่อยู่ในตัวเลือก + ตั้งไม่ได้ ·
/// ทิศตรงข้าม: ผู้สร้างที่ active ถูกเสนออัตโนมัติ (AutoPromote) และตั้งเองได้</item>
/// </list>
/// สร้าง controller/service ตรง (ไม่ผ่าน HTTP) — ด่านทั้งหมดอยู่ในเมธอดเหล่านี้ ไม่ใช่ใน filter
/// </summary>
[Trait("Category", "Db")]
public class CompanyOwnershipDbTests
{
    private readonly ITestOutputHelper _out;
    public CompanyOwnershipDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<Guid> UserAsync(AccountingDbContext db, Guid? companyId, string name, UserRole role = UserRole.Staff,
        bool platformAdmin = false, UserStatus status = UserStatus.Active)
    {
        var u = new User { Email = $"w9-own-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = name, Status = status, IsSystemAdmin = platformAdmin };
        db.Users.Add(u);
        if (companyId is { } cid)
            db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = cid, Role = role });
        await db.SaveChangesAsync();
        return u.Id;
    }

    private static async Task<Guid> CompanyAsync(AccountingDbContext db, string name, Guid? createdBy)
    {
        var c = new Company { Name = name, TaxId = "0105556000011", CreatedBy = createdBy?.ToString() };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    private static AdminController Admin(AccountingDbContext db, Guid adminId) => new(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, adminId.ToString()),
                    new Claim(ClaimTypes.Email, "platform-admin@test.local"),
                }, "Test")),
            },
        },
    };

    private static bool Ok(ActionResult<ApiResponse<object>> r) => r.Result is OkObjectResult;
    private static string? Message(ActionResult<ApiResponse<object>> r)
        => ((r.Result as ObjectResult)?.Value as ApiResponse<object>)?.Message;

    [Fact]
    public async Task assign_owner_พร้อมกันสองคำขอ_ได้เจ้าของคนเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var admin = await UserAsync(db, null, "แอดมินแพลตฟอร์ม", platformAdmin: true);
        var cid = await CompanyAsync(db, "W9 เจ้าของพร้อมกัน", createdBy: null);
        var a = await UserAsync(db, cid, "สมาชิก A");
        var b = await UserAsync(db, cid, "สมาชิก B");

        using var c1 = DbTestDatabase.TryCreateContext()!;
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var t1 = Admin(c1, admin).AssignOwnerToOwnerlessCompany(cid, new AdminController.AssignOwnerRequest(a));
        var t2 = Admin(c2, admin).AssignOwnerToOwnerlessCompany(cid, new AdminController.AssignOwnerRequest(b));
        var results = await Task.WhenAll(t1, t2);

        Assert.Equal(1, results.Count(Ok));
        var denied = results.Single(r => !Ok(r));
        Assert.Contains("มีเจ้าของแล้ว", Message(denied));

        using var check = DbTestDatabase.TryCreateContext()!;
        var owners = await check.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == cid && cu.Role == UserRole.Owner).ToListAsync();
        Assert.Single(owners);
        Assert.Contains(owners[0].UserId, new[] { a, b });
        // audit chain: หนึ่งแถวต่อการตั้งเจ้าของที่สำเร็จ
        Assert.Equal(1, await check.AuditLogs.AsNoTracking().CountAsync(x => x.CompanyId == cid && x.EntityType == "CompanyUser"
            && x.NewValues!.Contains(OwnerlessCompanyRepair.RuleCode)));
    }

    [Fact]
    public async Task ถอดหรือลดสิทธิ์เจ้าของคนสุดท้าย_ถูกปฏิเสธ_มีเจ้าของอีกคนจึงทำได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await CompanyAsync(db, "W9 เจ้าของคนสุดท้าย", createdBy: null);
        var actor = await UserAsync(db, cid, "เจ้าของผู้สั่ง", UserRole.Owner);
        var target = await UserAsync(db, cid, "เจ้าของเป้าหมาย", UserRole.Owner);
        // แอดมินแพลตฟอร์มที่เป็น Owner ด้วย — ไม่นับเป็น "เจ้าของลูกค้าอีกคน" (ฝ่ายค้านรอบ 205)
        await UserAsync(db, cid, "แอดมินที่เป็นสมาชิก", UserRole.Owner, platformAdmin: true);

        // actor ถอด target ⇒ เหลือ Owner ลูกค้า = actor เอง (นับคนอื่นที่ไม่ใช่ target) ⇒ ทำได้ … แต่ถ้า actor เป็น Viewer ก่อน?
        // จัดฉาก: ลด actor เป็น Staff ไม่ได้ (ตัวเอง) — จึงใช้ target เป็นผู้สั่งลด actor แทน
        using var c1 = DbTestDatabase.TryCreateContext()!;
        var svc = new CompanyService(c1, new AccountingService(c1));

        // ทิศตรงข้าม: ยังมี Owner ลูกค้าอีกคน (target) ⇒ ลด actor ได้
        await svc.UpdateUserRoleAsync(cid, target, actor, UserRole.Staff);
        using (var chk = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(UserRole.Staff, (await chk.CompanyUsers.AsNoTracking().SingleAsync(x => x.CompanyId == cid && x.UserId == actor)).Role);

        // ตอนนี้ target เป็น Owner ลูกค้าคนเดียว (แอดมินไม่นับ) ⇒ ลด/ถอด target โดยแอดมิน (ผ่าน EnsureOwnerAccess เพราะ IsSystemAdmin) ต้องถูกปฏิเสธ
        var adminMember = await c1.CompanyUsers.AsNoTracking().Where(x => x.CompanyId == cid && x.User.IsSystemAdmin).Select(x => x.UserId).SingleAsync();
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var svc2 = new CompanyService(c2, new AccountingService(c2));
        var demote = await Assert.ThrowsAsync<BusinessRuleException>(() => svc2.UpdateUserRoleAsync(cid, adminMember, target, UserRole.Staff));
        Assert.Equal(LastOwnerGuard.RuleCode, demote.RuleCode);
        var remove = await Assert.ThrowsAsync<BusinessRuleException>(() => svc2.RemoveUserAsync(cid, adminMember, target));
        Assert.Equal(LastOwnerGuard.RuleCode, remove.RuleCode);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(UserRole.Owner, (await check.CompanyUsers.AsNoTracking().SingleAsync(x => x.CompanyId == cid && x.UserId == target)).Role);
    }

    [Fact]
    public async Task แอดมินที่ไม่ใช่ผู้สร้าง_ไม่อยู่ในตัวเลือกและตั้งไม่ได้_ผู้สร้างactive_ถูกเสนอและตั้งได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var admin = await UserAsync(db, null, "แอดมินแพลตฟอร์ม", platformAdmin: true);
        var creator = await UserAsync(db, null, "ผู้สร้างบริษัท");
        var cid = await CompanyAsync(db, "W9 ตัวเลือกเจ้าของ", createdBy: creator);
        db.CompanyUsers.Add(new CompanyUser { UserId = creator, CompanyId = cid, Role = UserRole.Staff });   // ผู้สร้างที่หลุด Owner (ของเสียก่อนรอบ 205)
        var adminMember = await UserAsync(db, cid, "แอดมินอีกคนที่เป็นสมาชิก", UserRole.Staff, platformAdmin: true);
        await db.SaveChangesAsync();

        // ตัวเลือก: ผู้สร้างอยู่ · แอดมินที่ไม่ใช่ผู้สร้างไม่อยู่ · ข้อเสนอ = AutoPromote ผู้สร้าง
        var members = await db.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == cid)
            .Select(cu => new OwnerlessCompanyRepair.Member(cu.UserId, cu.Role, cu.JoinedAt, cu.User.IsSystemAdmin, cu.User.Status == UserStatus.Active))
            .ToListAsync();
        var proposal = OwnerlessCompanyRepair.Decide(creator.ToString(), members);
        Assert.Equal(OwnerlessCompanyRepair.Kind.AutoPromote, proposal.Kind);
        Assert.Equal(creator, proposal.UserId);
        Assert.Contains(creator, proposal.Candidates);
        Assert.DoesNotContain(adminMember, proposal.Candidates);

        // endpoint: ตั้งแอดมินที่ไม่ใช่ผู้สร้าง ⇒ ปฏิเสธ · ตั้งผู้สร้าง ⇒ ได้
        using var c1 = DbTestDatabase.TryCreateContext()!;
        var denied = await Admin(c1, admin).AssignOwnerToOwnerlessCompany(cid, new AdminController.AssignOwnerRequest(adminMember));
        Assert.False(Ok(denied));
        Assert.Contains("ผู้ดูแลแพลตฟอร์ม", Message(denied));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var ok = await Admin(c2, admin).AssignOwnerToOwnerlessCompany(cid, new AdminController.AssignOwnerRequest(creator));
        Assert.True(Ok(ok), Message(ok));

        using var check = DbTestDatabase.TryCreateContext()!;
        var roles = await check.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == cid).ToDictionaryAsync(cu => cu.UserId, cu => cu.Role);
        Assert.Equal(UserRole.Owner, roles[creator]);
        Assert.Equal(UserRole.Staff, roles[adminMember]);
    }
}

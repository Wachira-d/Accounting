using Accounting.Helpers;
using Accounting.Models.DTOs.Approval;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 2026-10-14 ทีม F4 (PAYMENT_CHANNEL_AUDIT ส่วนที่ 2 · ID#9 + ID#11) — เส้นอนุมัติจริงบน PostgreSQL
/// <para>(ID#9) มือถือเดินเจ้าของกติกาเดียวกับเว็บ: คนที่ไม่ใช่ผู้อนุมัติของขั้นถูกปฏิเสธ (403 · ทั้งอนุมัติและปฏิเสธ) โดยไม่มีอะไรถูกบันทึก ·
/// ผู้อนุมัติตัวจริงกดแล้วแถวที่รออยู่ถูก<b>แก้</b> (ไม่เพิ่มแถวใหม่) · ข้อผิดพลาดเป็นไทยไม่มี GUID</para>
/// <para>(ID#11) กฎที่ชี้ไปหาผู้อนุมัติที่ออกจากบริษัทแล้ว ⇒ ส่งคำขอไม่ได้ (409 + ทางไปต่อ) · ทิศตรงข้าม: สมาชิกครบ ⇒ ส่งได้ ·
/// คำขอที่ค้างอยู่แล้วเห็นป้าย + ผู้ดูแลโอนให้สมาชิกคนใหม่ได้ (โอนให้ผู้ขอไม่ได้ · โอนขั้นที่คนเดิมยังอยู่ไม่ได้)</para>
/// </summary>
[Trait("Category", "Db")]
public class ApprovalWorkflowMembershipDbTests
{
    private readonly ITestOutputHelper _out;
    public ApprovalWorkflowMembershipDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid Requester, Guid Approver, Guid Other, Guid RuleId, Guid EntityId);

    private static async Task<Guid> UserAsync(Accounting.Data.AccountingDbContext db, Guid companyId, string name, bool member = true,
        UserStatus status = UserStatus.Active)
    {
        var u = new User
        {
            Email = $"f4-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = name, Status = status,
        };
        db.Users.Add(u);
        if (member)
            db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = companyId, Role = UserRole.Staff });
        await db.SaveChangesAsync();
        return u.Id;
    }

    /// <summary>บริษัท + ผู้ขอ + ผู้อนุมัติขั้นที่ 1 + สมาชิกอีกคน + กฎหนึ่งขั้น (ทุกประเภท) · รายการที่ขออนุมัติเป็น JournalEntry
    /// (ขั้นสุดท้ายไม่ต้องอนุมัติเอกสารจริง — เทสต์นี้ล็อกด่านผู้อนุมัติ ไม่ใช่เส้นอนุมัติเอกสาร)</summary>
    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, bool approverIsMember = true)
    {
        var company = new Company { Name = "บริษัททดสอบผู้อนุมัติ", TaxId = "0105556000001" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var requester = await UserAsync(db, company.Id, "ผู้ขอ");
        var approver = await UserAsync(db, company.Id, "ผู้อนุมัติขั้นหนึ่ง", member: approverIsMember);
        var other = await UserAsync(db, company.Id, "สมาชิกอื่น");
        var rule = new ApprovalRule { CompanyId = company.Id, Name = "กฎทดสอบ", IsActive = true };
        rule.Steps.Add(new ApprovalStep { StepOrder = 1, ApproverUserId = approver, IsRequired = true });
        db.ApprovalRules.Add(rule);
        await db.SaveChangesAsync();
        return new Seed(company.Id, requester, approver, other, rule.Id, Guid.NewGuid());
    }

    /// <summary>คำขอที่ค้างอยู่แล้ว (ส่งไว้ก่อนผู้อนุมัติออก) — สร้างตรงเหมือนแถวในฐานจริง</summary>
    private static async Task<Guid> PendingRequestAsync(Accounting.Data.AccountingDbContext db, Seed s)
    {
        var req = new ApprovalRequest
        {
            CompanyId = s.CompanyId, ApprovalRuleId = s.RuleId, EntityType = "JournalEntry", EntityId = s.EntityId,
            RequestedByUserId = s.Requester, OverallStatus = ApprovalStatus.Pending, CurrentStep = 1,
        };
        req.Actions.Add(new ApprovalAction { StepOrder = 1, ApproverUserId = s.Approver, Status = ApprovalStatus.Pending });
        db.ApprovalRequests.Add(req);
        await db.SaveChangesAsync();
        return req.Id;
    }

    private static IPermissionService AllowAll() =>
        F4Proxy<IPermissionService>.Create((name, _) =>
            name == nameof(IPermissionService.HasPermissionAsync) ? (object)true : new List<string>());

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private static (ApprovalService Approval, MobileApiService Mobile) Services(Accounting.Data.AccountingDbContext db)
    {
        var map = new F4ServiceMap().Add(AllowAll());
        var approval = new ApprovalService(db, Notifications(), map);
        map.Add<IApprovalService>(approval);
        // VendorIntelligenceService ใช้เฉพาะตอนอนุมัติเอกสารครบ — เทสต์นี้ใช้ JournalEntry จึงไม่ถูกเรียก
        var mobile = new MobileApiService(db, null!, map);
        return (approval, mobile);
    }

    [Fact]
    public async Task มือถือ_คนที่ไม่ใช่ผู้อนุมัติของขั้น_อนุมัติหรือปฏิเสธไม่ได้_และไม่มีอะไรถูกบันทึก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var reqId = await PendingRequestAsync(db, s);
        var (_, mobile) = Services(db);

        foreach (var action in new[] { "approve", "reject" })
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                mobile.QuickApproveAsync(s.CompanyId, s.EntityId, "JournalEntry", action, s.Other));
            Assert.Equal(403, ex.StatusCode);
            Assert.Equal(ApprovalApproverCheck.NotAssignedRuleCode, ex.RuleCode);
            Assert.DoesNotContain(s.EntityId.ToString(), ex.Message);
        }

        using var check = DbTestDatabase.TryCreateContext()!;
        var req = await check.ApprovalRequests.AsNoTracking().Include(r => r.Actions).SingleAsync(r => r.Id == reqId);
        Assert.Equal(ApprovalStatus.Pending, req.OverallStatus);
        Assert.Equal(1, req.CurrentStep);
        var only = Assert.Single(req.Actions);                          // เดิมมือถือเพิ่มแถวใหม่ทุกครั้งที่กด
        Assert.Equal(ApprovalStatus.Pending, only.Status);
    }

    [Fact]
    public async Task มือถือ_ผู้อนุมัติตัวจริงอนุมัติได้_แก้แถวที่รออยู่ไม่เพิ่มแถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var reqId = await PendingRequestAsync(db, s);
        var (_, mobile) = Services(db);

        var res = await mobile.QuickApproveAsync(s.CompanyId, s.EntityId, "JournalEntry", "approve", s.Approver);
        Assert.True(res.Success);
        Assert.DoesNotContain(s.EntityId.ToString(), res.Message ?? "");

        using var check = DbTestDatabase.TryCreateContext()!;
        var req = await check.ApprovalRequests.AsNoTracking().Include(r => r.Actions).SingleAsync(r => r.Id == reqId);
        Assert.Equal(ApprovalStatus.Approved, req.OverallStatus);
        var only = Assert.Single(req.Actions);
        Assert.Equal(ApprovalStatus.Approved, only.Status);
        Assert.Equal(s.Approver, only.ApproverUserId);
        Assert.NotNull(only.ActionAt);
    }

    [Fact]
    public async Task มือถือ_ไม่มีคำขอที่รออยู่_404ภาษาไทย_และคำสั่งไม่รู้จัก_400()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var (_, mobile) = Services(db);
        var nf = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            mobile.QuickApproveAsync(s.CompanyId, s.EntityId, "JournalEntry", "approve", s.Approver));
        Assert.DoesNotContain(s.EntityId.ToString(), nf.Message);
        Assert.Contains("ไม่พบคำขออนุมัติ", nf.Message);
        var bad = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            mobile.QuickApproveAsync(s.CompanyId, s.EntityId, "JournalEntry", "maybe", s.Approver));
        Assert.Equal(400, bad.StatusCode);
    }

    [Fact]
    public async Task ส่งขออนุมัติ_กฎชี้ผู้อนุมัติที่ออกแล้ว_409พร้อมทางไปต่อ_สมาชิกครบส่งได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }

        var departed = await SeedAsync(db, approverIsMember: false);
        var (approvalA, _) = Services(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            approvalA.SubmitForApprovalAsync(departed.CompanyId, "JournalEntry", departed.EntityId, departed.Requester));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(ApprovalApproverCheck.DepartedRuleCode, ex.RuleCode);
        Assert.Contains("ผู้อนุมัติขั้นหนึ่ง", ex.Message);
        Assert.Contains("กฎการอนุมัติ", ex.Message);
        Assert.False(await db.ApprovalRequests.AnyAsync(r => r.CompanyId == departed.CompanyId));

        var ok = await SeedAsync(db, approverIsMember: true);
        var (approvalB, _) = Services(db);
        var created = await approvalB.SubmitForApprovalAsync(ok.CompanyId, "JournalEntry", ok.EntityId, ok.Requester);
        Assert.Equal(ApprovalStatus.Pending, created.OverallStatus);
        Assert.Null(created.StaleApproverNotice);
    }

    [Fact]
    public async Task คำขอค้างเพราะผู้อนุมัติออก_เห็นป้าย_ผู้ดูแลโอนให้สมาชิกใหม่_แล้วคนใหม่อนุมัติได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var reqId = await PendingRequestAsync(db, s);
        // ผู้อนุมัติถูกถอดออกจากบริษัทหลังคำขอถูกส่ง (CompanyService.RemoveUserAsync ลบแถว CompanyUsers)
        db.CompanyUsers.Remove(await db.CompanyUsers.SingleAsync(cu => cu.CompanyId == s.CompanyId && cu.UserId == s.Approver));
        await db.SaveChangesAsync();
        var (approval, _) = Services(db);

        var stalled = await approval.GetStalledRequestsAsync(s.CompanyId);
        var row = Assert.Single(stalled);
        Assert.NotNull(row.StaleApproverNotice);
        Assert.True(Assert.Single(row.Actions).ApproverDeparted);

        // โอนให้ผู้ขอไม่ได้ (SoD)
        var sod = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            approval.ReassignApproverAsync(s.CompanyId, reqId, s.Other, new ReassignApproverRequest(s.Requester)));
        Assert.Equal(ApprovalApproverCheck.ReassignRuleCode, sod.RuleCode);

        var after = await approval.ReassignApproverAsync(s.CompanyId, reqId, s.Other, new ReassignApproverRequest(s.Other, 1, "ผู้อนุมัติเดิมลาออก"));
        Assert.Null(after.StaleApproverNotice);
        Assert.Empty(await approval.GetStalledRequestsAsync(s.CompanyId));

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var req = await check.ApprovalRequests.AsNoTracking().Include(r => r.Actions).SingleAsync(r => r.Id == reqId);
            var action = Assert.Single(req.Actions);                    // แก้แถวเดิม ไม่เพิ่ม
            Assert.Equal(s.Other, action.ApproverUserId);
            Assert.Contains("โอนผู้อนุมัติ", action.Comments ?? "");
        }

        // คนใหม่อนุมัติได้จริง (ทางไปต่อครบวง) · คนเดิมที่ออกแล้วไม่ใช่ผู้อนุมัติของขั้นอีก
        var (approval2, _) = Services(db);
        var done = await approval2.SubmitActionAsync(s.CompanyId, reqId, s.Other, new SubmitApprovalActionRequest(ApprovalStatus.Approved, null));
        Assert.Equal(ApprovalStatus.Approved, done.OverallStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ขั้นสุดท้ายของเอกสาร_ต้องมีสิทธิ์อนุมัติเอกสาร_ไม่มีสิทธิ์403ไม่บันทึก_มีสิทธิ์ผ่าน(bool hasPermission)
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var contact = new Contact { CompanyId = s.CompanyId, Name = "ลูกค้าทดสอบ", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var doc = new Document
        {
            CompanyId = s.CompanyId, ContactId = contact.Id, DocumentType = DocumentType.Invoice, DocumentNumber = "DRAFT-" + Guid.NewGuid(),
            Status = DocumentStatus.WaitingApproval, DocumentDate = DateTime.UtcNow.Date, SubTotal = 100m, TotalAmount = 100m, BalanceDue = 100m,
            Currency = "THB",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        var req = new ApprovalRequest
        {
            CompanyId = s.CompanyId, ApprovalRuleId = s.RuleId, EntityType = "Document", EntityId = doc.Id,
            RequestedByUserId = s.Requester, OverallStatus = ApprovalStatus.Pending, CurrentStep = 1,
        };
        req.Actions.Add(new ApprovalAction { StepOrder = 1, ApproverUserId = s.Approver, Status = ApprovalStatus.Pending });
        db.ApprovalRequests.Add(req);
        await db.SaveChangesAsync();

        // ไม่มี IDocumentService ในแผนที่ ⇒ ขั้นพรีวิวคำเตือน/อนุมัติเอกสารจริงถูกข้าม — เทสต์นี้ล็อกเฉพาะด่านสิทธิ์ที่มาก่อน
        var perms = F4Proxy<IPermissionService>.Create((name, _) =>
            name == nameof(IPermissionService.HasPermissionAsync) ? (object)hasPermission : new List<string>());
        var approval = new ApprovalService(db, Notifications(), new F4ServiceMap().Add(perms));

        if (!hasPermission)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                approval.SubmitActionAsync(s.CompanyId, req.Id, s.Approver, new SubmitApprovalActionRequest(ApprovalStatus.Approved, null)));
            Assert.Equal(403, ex.StatusCode);
            Assert.Equal("PERM-DOC-APPROVE", ex.RuleCode);
            using var check = DbTestDatabase.TryCreateContext()!;
            Assert.Equal(ApprovalStatus.Pending, (await check.ApprovalRequests.AsNoTracking().SingleAsync(r => r.Id == req.Id)).OverallStatus);
            // ทิศตรงข้ามของด่านเดียวกัน: การปฏิเสธไม่ต้องใช้สิทธิ์อนุมัติเอกสาร (ผู้อนุมัติของขั้นตีกลับได้เสมอ)
            var rejected = await approval.SubmitActionAsync(s.CompanyId, req.Id, s.Approver,
                new SubmitApprovalActionRequest(ApprovalStatus.Rejected, "ยอดไม่ถูก"));
            Assert.Equal(ApprovalStatus.Rejected, rejected.OverallStatus);
        }
        else
        {
            var ok = await approval.SubmitActionAsync(s.CompanyId, req.Id, s.Approver, new SubmitApprovalActionRequest(ApprovalStatus.Approved, null));
            Assert.Equal(ApprovalStatus.Approved, ok.OverallStatus);
        }
    }

    [Fact]
    public async Task โอนผู้อนุมัติ_ขั้นที่คนเดิมยังเป็นสมาชิก_โอนไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var reqId = await PendingRequestAsync(db, s);
        var (approval, _) = Services(db);
        Assert.Empty(await approval.GetStalledRequestsAsync(s.CompanyId));
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            approval.ReassignApproverAsync(s.CompanyId, reqId, s.Other, new ReassignApproverRequest(s.Other)));
        Assert.Equal(409, ex.StatusCode);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(s.Approver, (await check.ApprovalActions.AsNoTracking().SingleAsync(a => a.ApprovalRequestId == reqId)).ApproverUserId);
    }
}

using System.Security.Claims;
using Accounting.Data;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 276 — ผู้ใช้ "เห็นเฉพาะเอกสารที่ตัวเองสร้าง / สร้าง-แก้ได้เฉพาะร่าง" บน PostgreSQL จริง:
/// ตัวกรองแถว EF · ด่านทุกคำขอ (<see cref="DocumentRestrictionFilter"/>: list/get/export/แปลง/ไฟล์แนบ ⇒ 404/403) · ด่านชั้น service
/// (<c>ApproveDocumentAsync</c>/<c>VoidDocumentAsync</c>) · ส่งขออนุมัติ → ตีกลับ/ถอน/อนุมัติ + แจ้งเตือนถูกช่อง · ทิศตรงข้าม: ผู้ไม่ถูกจำกัดพฤติกรรมเดิม
/// </summary>
[Trait("Category", "Db")]
public class DocumentRestrictionDbTests
{
    private readonly ITestOutputHelper _out;
    public DocumentRestrictionDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid Owner, Guid Clerk, string ClerkEmail, Guid Other, Guid Accountant, Guid ContactId,
        Guid ClerkDoc, Guid ClerkEmailDoc, Guid OtherDoc, Guid SystemDoc, Guid ClerkApprovedDoc);

    private static async Task<Guid> UserAsync(AccountingDbContext db, Guid companyId, string name, UserRole role, Guid? roleId = null,
        bool own = false, bool draft = false)
    {
        var u = new User { Email = $"r276-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = name, Status = UserStatus.Active };
        db.Users.Add(u);
        db.CompanyUsers.Add(new CompanyUser
        {
            UserId = u.Id, CompanyId = companyId, Role = role, CompanyRoleId = roleId, OwnDocumentsOnly = own, DraftDocumentsOnly = draft,
        });
        await db.SaveChangesAsync();
        return u.Id;
    }

    private static Document Doc(Guid companyId, Guid contactId, string createdBy, DocumentStatus status = DocumentStatus.Draft) => new()
    {
        CompanyId = companyId, ContactId = contactId, DocumentType = DocumentType.Quotation, Status = status,
        DocumentNumber = (status == DocumentStatus.Draft ? "DRAFT-" : "QT-") + Guid.NewGuid().ToString("N")[..10],
        DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, TotalAmount = 1_070m, VatAmount = 70m, BalanceDue = 1_070m,
        Currency = "THB", CreatedBy = createdBy,
    };

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบจำกัดเอกสาร", TaxId = "0105556000041" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        // บทบาท "พนักงานขาย": สร้างเอกสารได้ (สิทธิ์เดิม) — การจำกัดอยู่ที่ค่าต่อผู้ใช้
        var sales = new CompanyRole { CompanyId = company.Id, Name = "พนักงานขาย" };
        sales.Permissions.Add(new CompanyRolePermission { MenuItemId = PermissionKeys.DocumentCreate, CanAccess = true });
        db.CompanyRoles.Add(sales);
        await db.SaveChangesAsync();
        var owner = await UserAsync(db, company.Id, "เจ้าของ", UserRole.Owner);
        var clerk = await UserAsync(db, company.Id, "พนักงานจำกัด", UserRole.Staff, sales.Id, own: true, draft: true);
        var other = await UserAsync(db, company.Id, "พนักงานอื่น", UserRole.Staff, sales.Id);
        var accountant = await UserAsync(db, company.Id, "นักบัญชี", UserRole.Accountant);
        var clerkEmail = await db.Users.Where(u => u.Id == clerk).Select(u => u.Email).SingleAsync();
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้าทดสอบ", TaxId = "0105556000050", ContactType = ContactType.JuristicPerson, IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var d1 = Doc(company.Id, contact.Id, clerk.ToString());
        var d2 = Doc(company.Id, contact.Id, clerkEmail);                 // ทาง LINE ประทับอีเมล
        var d3 = Doc(company.Id, contact.Id, other.ToString());
        var d4 = Doc(company.Id, contact.Id, "system:recurring");
        var d5 = Doc(company.Id, contact.Id, clerk.ToString(), DocumentStatus.Approved);
        db.Documents.AddRange(d1, d2, d3, d4, d5);
        await db.SaveChangesAsync();
        return new Seed(company.Id, owner, clerk, clerkEmail, other, accountant, contact.Id, d1.Id, d2.Id, d3.Id, d4.Id, d5.Id);
    }

    private sealed class Captured { public readonly List<(string Key, NotificationContext Ctx)> Items = new(); }

    private static INotificationEngine Capture(Captured c) => F4Proxy<INotificationEngine>.Create((name, args) =>
    {
        if (name == nameof(INotificationEngine.DispatchAsync)) c.Items.Add(((string)args[1]!, (NotificationContext)args[2]!));
        return null;
    });

    private static DocumentApprovalRequestService Svc(AccountingDbContext db, Captured c)
    {
        var perms = new PermissionService(db);
        var map = new F4ServiceMap().Add<IPermissionService>(perms);
        var approval = new ApprovalService(db, F4Proxy<INotificationService>.Create((_, _) => null), map);
        return new DocumentApprovalRequestService(db, perms, approval, Capture(c));
    }

    /// <summary>บริบทคำขอของผู้ใช้ (DbContext ใหม่ = หนึ่งคำขอ) — ตั้งขอบเขตแบบเดียวกับ DocumentRestrictionFilter</summary>
    private static async Task<AccountingDbContext> RequestAsAsync(Guid companyId, Guid userId)
    {
        var db = DbTestDatabase.TryCreateContext()!;
        DocumentRestrictionGuard.ApplyScope(db, userId, await DocumentRestrictionGuard.ForUserAsync(db, companyId, userId));
        return db;
    }

    [Fact]
    public async Task ตัวกรองแถว_ผู้เห็นเฉพาะของตัวเองเห็นแต่ใบตัวเอง_ทั้งไอดีและอีเมล_ใบคนอื่นไม่พบ_ผู้อื่นเห็นครบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);

        using (var clerk = await RequestAsAsync(s.CompanyId, s.Clerk))
        {
            var ids = await clerk.Documents.AsNoTracking().Where(d => d.CompanyId == s.CompanyId).Select(d => d.Id).ToListAsync();
            Assert.Equal(new[] { s.ClerkDoc, s.ClerkEmailDoc, s.ClerkApprovedDoc }.OrderBy(x => x), ids.OrderBy(x => x));
            Assert.Null(await clerk.Documents.FirstOrDefaultAsync(d => d.Id == s.OtherDoc && d.CompanyId == s.CompanyId));
            Assert.False(await clerk.Documents.AnyAsync(d => d.Id == s.SystemDoc));
            Assert.True(clerk.DocumentDraftOnlyRequest);
        }
        // ทิศตรงข้าม: ผู้ไม่ถูกจำกัด (คนอื่น/เจ้าของ) — ไม่มีขอบเขต ⇒ เห็นครบ 5 ใบ (พฤติกรรมเดิม)
        foreach (var u in new[] { s.Other, s.Owner })
        {
            using var r = await RequestAsAsync(s.CompanyId, u);
            Assert.Null(r.DocumentOwnerScopeUserId);
            Assert.False(r.DocumentDraftOnlyRequest);
            Assert.Equal(5, await r.Documents.CountAsync(d => d.CompanyId == s.CompanyId));
        }
    }

    private static ActionExecutingContext Ctx(AccountingDbContext db, Guid companyId, Guid userId, string method, string template,
        Dictionary<string, string>? route = null, string? query = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        if (query != null) http.Request.QueryString = new QueryString(query);
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));
        var rd = new RouteData();
        rd.Values["companyId"] = companyId.ToString();
        foreach (var kv in route ?? new()) rd.Values[kv.Key] = kv.Value;
        var ad = new ControllerActionDescriptor { AttributeRouteInfo = new AttributeRouteInfo { Template = template } };
        return new ActionExecutingContext(new ActionContext(http, rd, ad), new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
    }

    private static async Task<(bool Next, int? Status)> RunFilterAsync(AccountingDbContext db, ActionExecutingContext ctx)
    {
        var called = false;
        await new DocumentRestrictionFilter(db).OnActionExecutionAsync(ctx, () =>
        {
            called = true;
            return Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), new object()));
        });
        return (called, (ctx.Result as ObjectResult)?.StatusCode);
    }

    private const string C = "api/companies/{companyId:guid}/";

    [Fact]
    public async Task ด่านทุกคำขอ_ใบคนอื่น404_รายงาน403_ใบอนุมัติแล้วแก้ไม่ได้_ไฟล์แนบของสิ่งอื่น403_ผู้ไม่ถูกจำกัดผ่าน()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(seedDb);
        var doc = (Guid id) => new Dictionary<string, string> { ["documentId"] = id.ToString() };

        async Task<(bool, int?)> Clerk(string m, string t, Dictionary<string, string>? r = null, string? q = null)
        {
            using var db = DbTestDatabase.TryCreateContext()!;
            return await RunFilterAsync(db, Ctx(db, s.CompanyId, s.Clerk, m, t, r, q));
        }

        // ใบของตัวเอง: อ่าน/แก้ร่าง/แปลง/ขออนุมัติ ผ่าน
        Assert.Equal((true, (int?)null), await Clerk("GET", C + "Document/{documentId:guid}", doc(s.ClerkDoc)));
        Assert.Equal((true, (int?)null), await Clerk("PUT", C + "Document/{documentId:guid}", doc(s.ClerkDoc)));
        Assert.Equal((true, (int?)null), await Clerk("GET", C + "Document"));
        Assert.Equal((true, (int?)null), await Clerk("GET", C + "Document/export"));
        // ใบของคนอื่น (เดา id): อ่าน · แปลง · คัดลอก · ขออนุมัติ ⇒ 404 (ไม่ยืนยันว่ามีใบนั้น)
        Assert.Equal((false, (int?)404), await Clerk("GET", C + "Document/{documentId:guid}", doc(s.OtherDoc)));
        Assert.Equal((false, (int?)404), await Clerk("POST", C + "Document/{documentId:guid}/convert/{targetType}", doc(s.OtherDoc)));
        Assert.Equal((false, (int?)404), await Clerk("POST", C + "documents/{sourceId:guid}/clone",
            new Dictionary<string, string> { ["sourceId"] = s.OtherDoc.ToString() }));
        Assert.Equal((false, (int?)404), await Clerk("POST", C + "document/{documentId:guid}/approval-request", doc(s.OtherDoc)));
        Assert.Equal((false, (int?)404), await Clerk("GET", C + "attachments/{entityType}/{entityId:guid}",
            new Dictionary<string, string> { ["entityType"] = "Document", ["entityId"] = s.OtherDoc.ToString() }));
        Assert.Equal((false, (int?)404), await Clerk("GET", C + "approval/for-entity", null, $"?entityType=Document&entityId={s.OtherDoc}"));
        // ไฟล์แนบของสิ่งที่ไม่ใช่เอกสาร (เช่นพนักงาน) ⇒ 403
        Assert.Equal((false, (int?)403), await Clerk("GET", C + "attachments/{entityType}/{entityId:guid}",
            new Dictionary<string, string> { ["entityType"] = "Employee", ["entityId"] = Guid.NewGuid().ToString() }));
        // รายงาน/ข้อมูลรวมของบริษัท · อนุมัติ ⇒ 403
        Assert.Equal((false, (int?)403), await Clerk("GET", C + "Report/profit-loss"));
        Assert.Equal((false, (int?)403), await Clerk("POST", C + "Document/{documentId:guid}/approve", doc(s.ClerkDoc)));
        // ใบของตัวเองที่อนุมัติแล้ว: แก้ไม่ได้ (ต้องให้ผู้ดูแลจัดการ)
        Assert.Equal((false, (int?)403), await Clerk("PUT", C + "Document/{documentId:guid}", doc(s.ClerkApprovedDoc)));

        // ทิศตรงข้าม: ผู้ไม่ถูกจำกัด — รายงาน/ใบของคนอื่น/อนุมัติ ผ่านด่านนี้ (ด่านเดิมของ action ตัดสินต่อ)
        using (var db = DbTestDatabase.TryCreateContext()!)
            Assert.Equal((true, (int?)null), await RunFilterAsync(db, Ctx(db, s.CompanyId, s.Other, "GET", C + "Report/profit-loss")));
        using (var db = DbTestDatabase.TryCreateContext()!)
            Assert.Equal((true, (int?)null), await RunFilterAsync(db, Ctx(db, s.CompanyId, s.Owner, "POST", C + "Document/{documentId:guid}/approve", doc(s.ClerkDoc))));
    }

    [Fact]
    public async Task ด่านชั้นservice_ผู้เฉพาะร่างอนุมัติและยกเลิกไม่ได้ทุกทางเข้า_ผู้อื่นผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);

        // ผู้กระทำ = id (เว็บ/มือถือ/workflow) และ = อีเมล (LINE)
        foreach (var actor in new[] { s.Clerk.ToString(), s.ClerkEmail })
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => DocumentRestrictionGuard.EnsureMayFinalizeAsync(db, s.CompanyId, actor, "อนุมัติ"));
            Assert.Equal(403, ex.StatusCode);
            Assert.Equal(DocumentRestrictionPolicy.DraftOnlyRuleCode, ex.RuleCode);
        }
        // ทิศตรงข้าม: เจ้าของ · ป้ายระบบ (งานเบื้องหลัง) ผ่าน
        await DocumentRestrictionGuard.EnsureMayFinalizeAsync(db, s.CompanyId, s.Owner.ToString(), "อนุมัติ");
        await DocumentRestrictionGuard.EnsureMayFinalizeAsync(db, s.CompanyId, "system:recurring", "อนุมัติ");

        // ต่อสายจริงใน ApproveDocumentAsync (ไม่ใช่แค่ helper): ใบยังเป็นร่าง
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var docs = new DocumentService(req, new AccountingService(req), RecurringTransactionDbTests.AllowAll<ISubscriptionService>.Create(), null!, null!,
                NullLogger<DocumentService>.Instance, null!, null!, null!);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => docs.ApproveDocumentAsync(s.CompanyId, s.ClerkDoc, s.Clerk.ToString()));
            Assert.Equal(DocumentRestrictionPolicy.DraftOnlyRuleCode, ex.RuleCode);
        }
        // คำขอของผู้เฉพาะร่างที่ประทับผู้กระทำเป็นป้ายระบบ (อนุมัติอัตโนมัติตอนสร้าง) / ยกเลิก ⇒ ถูกกันด้วยธงของคำขอ
        using (var req = await RequestAsAsync(s.CompanyId, s.Clerk))
        {
            var docs = new DocumentService(req, new AccountingService(req), RecurringTransactionDbTests.AllowAll<ISubscriptionService>.Create(), null!, null!,
                NullLogger<DocumentService>.Instance, null!, null!, null!);
            Assert.Equal(DocumentRestrictionPolicy.DraftOnlyRuleCode,
                (await Assert.ThrowsAsync<BusinessRuleException>(() => docs.ApproveDocumentAsync(s.CompanyId, s.ClerkDoc, "system"))).RuleCode);
            Assert.Equal(DocumentRestrictionPolicy.DraftOnlyRuleCode,
                (await Assert.ThrowsAsync<BusinessRuleException>(() => docs.VoidDocumentAsync(s.CompanyId, s.ClerkApprovedDoc))).RuleCode);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(DocumentStatus.Draft, (await check.Documents.SingleAsync(d => d.Id == s.ClerkDoc)).Status);
        Assert.Equal(DocumentStatus.Approved, (await check.Documents.SingleAsync(d => d.Id == s.ClerkApprovedDoc)).Status);
    }

    [Fact]
    public async Task ส่งขออนุมัติ_แจ้งผู้อนุมัติ_ตีกลับพร้อมเหตุ_ถอน_อนุมัติแล้วแจ้งผู้ขอ_อนุมัติเองไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var cap = new Captured();

        // ผู้อนุมัติเอกสาร = เจ้าของ + นักบัญชี (ไม่รวมผู้เฉพาะร่าง/พนักงานที่ไม่มีสิทธิ์อนุมัติ)
        var approvers = await DocumentApproverDirectory.ResolveAsync(db, new PermissionService(db), s.CompanyId, DocumentType.Quotation);
        Assert.Equal(new[] { s.Owner, s.Accountant }.OrderBy(x => x), approvers.OrderBy(x => x));

        DocumentApprovalSubmitResultShape submitted;
        using (var req = await RequestAsAsync(s.CompanyId, s.Clerk))
        {
            var r = await Svc(req, cap).RequestAsync(s.CompanyId, s.ClerkDoc, s.Clerk, "ฝากตรวจราคาบรรทัด 2");
            submitted = new(r.Route, r.ApproverCount);
            // ใบของคนอื่น — ไม่พบ (ตัวกรองแถว)
            await Assert.ThrowsAsync<KeyNotFoundException>(() => Svc(req, cap).RequestAsync(s.CompanyId, s.OtherDoc, s.Clerk, null));
        }
        Assert.Equal(new DocumentApprovalSubmitResultShape("Basic", 2), submitted);
        var (key, ctx) = Assert.Single(cap.Items);
        Assert.Equal(NotificationEvents.DocumentApprovalRequested, key);
        Assert.Equal(s.Clerk, ctx.ActorUserId);                          // ผู้ขอไม่ได้รับแจ้งเรื่องของตัวเอง
        Assert.Null(ctx.RecipientUserId);                               // ตามตารางบทบาท (ช่องที่บริษัทตั้ง)
        Assert.Contains($"id={s.ClerkDoc}", ctx.ActionUrl);
        Assert.DoesNotContain("ลูกค้าทดสอบ", ctx.Message);               // คำตัดสิน 276-6: ไม่มีชื่อลูกค้าในข้อความ (ไปถึงกลุ่มได้)
        Assert.DoesNotContain("0105556000050", ctx.Message);

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(DocumentStatus.WaitingApproval, (await check.Documents.SingleAsync(d => d.Id == s.ClerkDoc)).Status);
            Assert.Equal(DocumentApprovalRequestStatus.Pending, (await check.DocumentApprovalRequests.SingleAsync(r => r.DocumentId == s.ClerkDoc)).Status);
            // คิวของเจ้าของเห็น · คิวของผู้ขอว่าง
            Assert.Contains(await Svc(check, cap).QueueAsync(s.CompanyId, s.Owner), q => q.DocumentId == s.ClerkDoc);
            Assert.DoesNotContain(await Svc(check, cap).QueueAsync(s.CompanyId, s.Clerk), q => q.DocumentId == s.ClerkDoc);
            var st = await Svc(check, cap).GetStateAsync(s.CompanyId, s.ClerkDoc, s.Owner);
            Assert.True(st.CanApprove && st.CanReturn && !st.CanWithdraw && st.MayFinalize);
            var mine = await Svc(check, cap).GetStateAsync(s.CompanyId, s.ClerkDoc, s.Clerk);
            Assert.True(mine.CanWithdraw && !mine.CanApprove && !mine.MayFinalize);
            Assert.NotNull(mine.RestrictionNotice);
        }

        // ผู้ขอตีกลับเอง/อนุมัติเองไม่ได้ (SoD)
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(DocumentApprovalRequestService.SelfApprovalCode,
                (await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req, cap).ReturnAsync(s.CompanyId, s.ClerkDoc, s.Clerk, "ไม่ผ่าน"))).RuleCode);
            Assert.Equal(DocumentApprovalRequestService.SelfApprovalCode,
                (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                    DocumentApprovalRequestService.EnsureNotSelfApprovalAsync(req, s.CompanyId, s.ClerkDoc, s.Clerk.ToString()))).RuleCode);
            // ผู้อนุมัติไม่ใช่ผู้ขอ ⇒ ผ่านด่านนี้ (ทิศตรงข้าม)
            await DocumentApprovalRequestService.EnsureNotSelfApprovalAsync(req, s.CompanyId, s.ClerkDoc, s.Owner.ToString());
            // เหตุผลว่าง = ปฏิเสธ
            Assert.Equal(400, (await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req, cap).ReturnAsync(s.CompanyId, s.ClerkDoc, s.Owner, " "))).StatusCode);
        }

        cap.Items.Clear();
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req, cap).ReturnAsync(s.CompanyId, s.ClerkDoc, s.Owner, "ราคาบรรทัด 2 ผิด");
        var returned = Assert.Single(cap.Items);
        Assert.Equal(NotificationEvents.DocumentApprovalReturned, returned.Key);
        Assert.Equal(s.Clerk, returned.Ctx.RecipientUserId);
        Assert.Contains("ราคาบรรทัด 2 ผิด", returned.Ctx.Message);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(DocumentStatus.Rejected, (await check.Documents.SingleAsync(d => d.Id == s.ClerkDoc)).Status);
            var st = await Svc(check, cap).GetStateAsync(s.CompanyId, s.ClerkDoc, s.Clerk);
            Assert.Contains("ราคาบรรทัด 2 ผิด", st.StatusLabel);
            Assert.True(st.CanRequest);
        }

        // ขอใหม่ → ถอน (ผู้อื่นถอนไม่ได้) → ขอใหม่ → อนุมัติ ⇒ แจ้งผู้ขอ
        using (var req = await RequestAsAsync(s.CompanyId, s.Clerk))
            await Svc(req, cap).RequestAsync(s.CompanyId, s.ClerkDoc, s.Clerk, null);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(403, (await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req, cap).WithdrawAsync(s.CompanyId, s.ClerkDoc, s.Owner))).StatusCode);
        using (var req = await RequestAsAsync(s.CompanyId, s.Clerk))
            Assert.Equal("Draft", (await Svc(req, cap).WithdrawAsync(s.CompanyId, s.ClerkDoc, s.Clerk)).DocumentStatus);
        using (var req = await RequestAsAsync(s.CompanyId, s.Clerk))
            await Svc(req, cap).RequestAsync(s.CompanyId, s.ClerkDoc, s.Clerk, null);
        cap.Items.Clear();
        using (var req = DbTestDatabase.TryCreateContext()!)
            await DocumentApprovalRequestService.OnDocumentApprovedAsync(req, Capture(cap), s.CompanyId, s.ClerkDoc, s.Owner.ToString(), "QT-0001", DocumentType.Quotation);
        var granted = Assert.Single(cap.Items);
        Assert.Equal(NotificationEvents.DocumentApprovalGranted, granted.Key);
        Assert.Equal(s.Clerk, granted.Ctx.RecipientUserId);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var rows = await check.DocumentApprovalRequests.Where(r => r.DocumentId == s.ClerkDoc).OrderBy(r => r.RequestedAt).ToListAsync();
            Assert.Equal(new[] { DocumentApprovalRequestStatus.Returned, DocumentApprovalRequestStatus.Withdrawn, DocumentApprovalRequestStatus.Approved },
                rows.Select(r => r.Status));
            Assert.Equal(s.Owner, rows[^1].DecidedByUserId);
            // audit chain ทุกขั้น (ขอ ×3 · ตีกลับ · ถอน)
            Assert.True(await check.AuditLogs.CountAsync(a => a.CompanyId == s.CompanyId && a.EntityId == s.ClerkDoc.ToString()) >= 5);
        }
    }

    [Fact]
    public async Task ผู้ไม่ถูกจำกัดที่อนุมัติเองได้และไม่มีกฎ_ส่งขออนุมัติได้คำตอบเดิม_กดอนุมัติได้เลย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var cap = new Captured();
        var ownerDoc = Doc(s.CompanyId, s.ContactId, s.Owner.ToString());
        db.Documents.Add(ownerDoc);
        await db.SaveChangesAsync();
        using var req = await RequestAsAsync(s.CompanyId, s.Owner);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req, cap).RequestAsync(s.CompanyId, ownerDoc.Id, s.Owner, null));
        Assert.Equal(DocumentApprovalRequestService.NoRuleSelfApproveCode, ex.RuleCode);
        Assert.Empty(cap.Items);
        Assert.Equal(DocumentStatus.Draft, (await req.Documents.AsNoTracking().SingleAsync(d => d.Id == ownerDoc.Id)).Status);
    }

    [Fact]
    public async Task เจ้าของตั้งค่าได้_ตั้งบนเจ้าของไม่ได้_เห็นเฉพาะของตัวเองบังคับเฉพาะร่าง_migration_ซ้ำได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        foreach (var sql in Round276Migration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        foreach (var sql in Round276Migration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);   // idempotent
        var s = await SeedAsync(db);
        var company = new CompanyService(db, null!, logger: NullLogger<CompanyService>.Instance);
        await company.UpdateDocumentRestrictionAsync(s.CompanyId, s.Owner, s.Other, ownDocumentsOnly: true, draftDocumentsOnly: false);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var cu = await check.CompanyUsers.SingleAsync(x => x.CompanyId == s.CompanyId && x.UserId == s.Other);
            Assert.True(cu.OwnDocumentsOnly && cu.DraftDocumentsOnly);
            var members = await new CompanyService(check, null!, logger: NullLogger<CompanyService>.Instance).GetMembersAsync(s.CompanyId, s.Owner);
            Assert.NotNull(members.Single(m => m.UserId == s.Other).EffectiveDocumentRestrictionNotice);
            Assert.Null(members.Single(m => m.UserId == s.Accountant).EffectiveDocumentRestrictionNotice);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var second = await UserAsync(req, s.CompanyId, "เจ้าของคนที่สอง", UserRole.Owner);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new CompanyService(req, null!, logger: NullLogger<CompanyService>.Instance).UpdateDocumentRestrictionAsync(s.CompanyId, s.Owner, second, false, true));
            Assert.Equal(409, ex.StatusCode);
            // ผู้ที่ไม่ใช่เจ้าของตั้งไม่ได้
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new CompanyService(req, null!, logger: NullLogger<CompanyService>.Instance).UpdateDocumentRestrictionAsync(s.CompanyId, s.Accountant, s.Other, false, false));
        }
    }


    // ══════════════════ ชุดแก้ฝ่ายค้าน 276 ══════════════════

    private static DocumentService Docs(AccountingDbContext db) =>
        new(db, new AccountingService(db), RecurringTransactionDbTests.AllowAll<ISubscriptionService>.Create(), null!, null!,
            NullLogger<DocumentService>.Instance, null!, null!, null!);

    [Fact]
    public async Task ฝ่ายค้าน_HIGH1_LINE_ผู้ถูกจำกัดดูยอดและถามไม่ได้_ตอบเหตุ_ผู้อื่นใช้ได้ตามเดิม()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(seed);
        var clerkLine = "U" + Guid.NewGuid().ToString("N");
        var ownerLine = "U" + Guid.NewGuid().ToString("N");
        (await seed.Users.SingleAsync(u => u.Id == s.Clerk)).LineUserId = clerkLine;
        (await seed.Users.SingleAsync(u => u.Id == s.Owner)).LineUserId = ownerLine;
        await seed.SaveChangesAsync();
        var asked = 0;
        LineBotService Bot(AccountingDbContext db) => new(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), null!,
            NullLogger<LineBotService>.Instance, F4Proxy<IDocumentService>.Create((_, _) => null), null!, null!, null!,
            F4Proxy<IChatbotService>.Create((_, _) => { asked++; throw new InvalidOperationException("ไม่ควรถูกเรียก"); }),
            new PermissionService(db), null!, null!, null!);

        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var summary = await Bot(db).HandleMessageAsync(clerkLine, "ดูยอด");
            Assert.StartsWith("⛔", summary);
            Assert.DoesNotContain("เงินเข้า", summary);
        }
        using (var db = DbTestDatabase.TryCreateContext()!)
            Assert.StartsWith("⛔", await Bot(db).HandleMessageAsync(clerkLine, "ถาม ค่าน้ำมันลงหมวดไหน"));
        Assert.Equal(0, asked);                                           // ไม่เข้า RAG ของบริษัทเลย
        // ทิศตรงข้าม: เจ้าของดูยอดได้ตามเดิม
        using (var db = DbTestDatabase.TryCreateContext()!)
            Assert.Contains("เงินเข้า", await Bot(db).HandleMessageAsync(ownerLine, "ดูยอด"));
    }

    [Fact]
    public async Task ฝ่ายค้าน_HIGH2_ทางเข้าไม่มีบริษัทในroute_ภาพรวมข้ามบริษัท403_แจ้งเตือนของตัวเองผ่าน_ผู้ไม่ถูกจำกัดผ่าน()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(seedDb);
        async Task<(bool, int?)> Run(Guid user, string m, string t)
        {
            using var db = DbTestDatabase.TryCreateContext()!;
            var ctx = Ctx(db, s.CompanyId, user, m, t);
            ctx.RouteData.Values.Remove("companyId");
            return await RunFilterAsync(db, ctx);
        }
        Assert.Equal((false, (int?)403), await Run(s.Clerk, "GET", "api/accountant-workspace"));
        Assert.Equal((false, (int?)403), await Run(s.Clerk, "POST", "api/account-subscription/attach"));
        Assert.Equal((true, (int?)null), await Run(s.Clerk, "GET", "api/notification"));
        Assert.Equal((true, (int?)null), await Run(s.Clerk, "POST", "api/auth/change-password"));
        Assert.Equal((true, (int?)null), await Run(s.Other, "GET", "api/accountant-workspace"));
    }

    [Fact]
    public async Task ฝ่ายค้าน_MED3_แจ้งเตือน_ผู้ถูกจำกัดได้เฉพาะเรื่องใบตัวเอง_รายการเก่าถูกกรอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        Assert.True(await DocumentRestrictionGuard.MayReceiveAsync(db, s.CompanyId, s.Clerk, "Document", s.ClerkDoc));
        Assert.False(await DocumentRestrictionGuard.MayReceiveAsync(db, s.CompanyId, s.Clerk, "Document", s.OtherDoc));
        Assert.False(await DocumentRestrictionGuard.MayReceiveAsync(db, s.CompanyId, s.Clerk, "Payment", Guid.NewGuid()));
        Assert.True(await DocumentRestrictionGuard.MayReceiveAsync(db, s.CompanyId, s.Other, "Document", s.ClerkDoc));   // ทิศตรงข้าม
        db.Notifications.AddRange(
            new Notification { UserId = s.Clerk, CompanyId = s.CompanyId, Title = "ของตัวเอง", Message = "x", EntityType = "Document", EntityId = s.ClerkDoc },
            new Notification { UserId = s.Clerk, CompanyId = s.CompanyId, Title = "ใบคนอื่น", Message = "x", EntityType = "Document", EntityId = s.OtherDoc },
            new Notification { UserId = s.Clerk, CompanyId = s.CompanyId, Title = "รับชำระ", Message = "x", EntityType = "Payment", EntityId = Guid.NewGuid() },
            new Notification { UserId = s.Clerk, CompanyId = null, Title = "ระบบ", Message = "x" });
        await db.SaveChangesAsync();
        using var req = DbTestDatabase.TryCreateContext()!;
        var list = await new NotificationService(req, NullLogger<NotificationService>.Instance).GetUserNotificationsAsync(s.Clerk);
        Assert.Equal(new[] { "ของตัวเอง", "ระบบ" }.OrderBy(x => x), list.Select(n => n.Title).OrderBy(x => x));
        Assert.Equal(2, (await new NotificationService(req, NullLogger<NotificationService>.Instance).GetCountAsync(s.Clerk)).Total);
    }

    [Fact]
    public async Task ฝ่ายค้าน_MED5_MED6_idใบคนอื่นในbody404_แก้ผู้ติดต่อเดิมไม่ได้_สร้างใหม่ได้()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(seedDb);
        var otherLine = await seedDb.DocumentLines.Where(l => l.DocumentId == s.OtherDoc).Select(l => (Guid?)l.Id).FirstOrDefaultAsync();
        if (otherLine == null)
        {
            var l = new DocumentLine { DocumentId = s.OtherDoc, LineOrder = 1, Description = "x", Quantity = 1, UnitPrice = 1, Amount = 1 };
            seedDb.DocumentLines.Add(l); await seedDb.SaveChangesAsync(); otherLine = l.Id;
        }
        async Task<(bool, int?)> Post(object body, string t = C + "Document")
        {
            using var db = DbTestDatabase.TryCreateContext()!;
            var ctx = Ctx(db, s.CompanyId, s.Clerk, "POST", t);
            ctx.ActionArguments["request"] = body;
            return await RunFilterAsync(db, ctx);
        }
        var line = new Accounting.Models.DTOs.Document.DocumentLineRequest("x", 1, null, 100, 0, 7, 0, null);
        Accounting.Models.DTOs.Document.CreateDocumentRequest Req(Guid? related, Guid? srcLine = null) => new(
            DocumentType.Invoice, DateTime.UtcNow.Date, null, s.ContactId, null, null,
            new List<Accounting.Models.DTOs.Document.DocumentLineRequest> { srcLine == null ? line : line with { SourceLineId = srcLine } },
            RelatedDocumentId: related);
        Assert.Equal((false, (int?)404), await Post(Req(s.OtherDoc)));
        Assert.Equal((false, (int?)404), await Post(Req(null, otherLine)));
        Assert.Equal((false, (int?)404), await Post(new Accounting.Models.DTOs.DocumentTemplate.GeneratePdfRequest(s.OtherDoc, null, null, null, null),
            C + "document-templates/generate-pdf"));
        Assert.Equal((true, (int?)null), await Post(Req(s.ClerkDoc)));      // ทิศตรงข้าม: ใบของตัวเอง
        Assert.Equal((true, (int?)null), await Post(Req(null)));
        // MED-6: แก้ผู้ติดต่อเดิม 403 · สร้างใหม่ได้
        using (var db = DbTestDatabase.TryCreateContext()!)
            Assert.Equal((false, (int?)403), await RunFilterAsync(db, Ctx(db, s.CompanyId, s.Clerk, "PUT", C + "Document/contacts/{contactId:guid}",
                new Dictionary<string, string> { ["contactId"] = s.ContactId.ToString() })));
        using (var db = DbTestDatabase.TryCreateContext()!)
            Assert.Equal((true, (int?)null), await RunFilterAsync(db, Ctx(db, s.CompanyId, s.Clerk, "POST", C + "Document/contacts")));
    }

    [Fact]
    public async Task ฝ่ายค้าน_MED4_แปลงซ้ำใบต้นทางที่คนอื่นแปลงไปแล้ว_ถูกกัน_และอนุมัติใบเกินของผู้ถูกจำกัดถูกบล็อก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        // ใบเสนอราคาของผู้ถูกจำกัด (อนุมัติแล้ว) · ผู้อื่นแปลงเป็นใบแจ้งหนี้ครบแล้ว (อนุมัติแล้ว)
        var qt = Doc(s.CompanyId, s.ContactId, s.Clerk.ToString(), DocumentStatus.Approved);
        var qtLine = new DocumentLine { LineOrder = 1, Description = "งานบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7 };
        qt.Lines.Add(qtLine);
        db.Documents.Add(qt);
        await db.SaveChangesAsync();
        var inv = Doc(s.CompanyId, s.ContactId, s.Other.ToString(), DocumentStatus.Approved);
        inv.DocumentType = DocumentType.Invoice; inv.RelatedDocumentId = qt.Id;
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "งานบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7, SourceLineId = qtLine.Id });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();

        // ผ่านด่านคำขอจริง (ทางเขียน ⇒ ยกตัวกรองแถว) แล้ว service แปลง · ข้อความเหตุผ่านตัวช่วยกลาง (ไม่มีเลข/ยอดของใบคนอื่น)
        Exception? thrown = null;
        string? reply = null;
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ctx = Ctx(req, s.CompanyId, s.Clerk, "POST", C + "Document/{documentId:guid}/convert/{targetType}",
                new Dictionary<string, string> { ["documentId"] = qt.Id.ToString() });
            var called = false;
            ActionExecutedContext? done = null;
            await new DocumentRestrictionFilter(req).OnActionExecutionAsync(ctx, async () =>
            {
                called = true;
                Assert.Null(req.DocumentOwnerScopeUserId);                // ด่านระดับบริษัทเห็นทั้งบริษัท
                Assert.True(req.DocumentDraftOnlyRequest);
                done = new ActionExecutedContext(ctx, new List<IFilterMetadata>(), new object());
                try { await Docs(req).ConvertDocumentAsync(s.CompanyId, qt.Id, DocumentType.Invoice, s.Clerk.ToString()); }
                catch (Exception ex) { thrown = ex; done.Exception = ex; }
                return done;
            });
            Assert.True(called);
            reply = done!.ExceptionHandled
                ? ((done.Result as ObjectResult)?.Value as Accounting.Models.DTOs.ApiResponse<object>)?.Message
                : done.Exception?.Message;
        }
        Assert.NotNull(thrown);
        _out.WriteLine("ข้อความถึงผู้ใช้: " + reply);
        Assert.NotNull(reply);
        Assert.DoesNotContain(inv.DocumentNumber, reply);
        Assert.DoesNotContain("1,000", reply);
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await check.Documents.CountAsync(d => d.CompanyId == s.CompanyId && d.RelatedDocumentId == qt.Id));

        // ใบเกินที่ค้างอยู่ (ผู้ถูกจำกัดสร้างก่อนแก้) ⇒ อนุมัติแบบรับทราบคำเตือนผ่านไม่ได้
        var dup = Doc(s.CompanyId, s.ContactId, s.Clerk.ToString());
        dup.DocumentType = DocumentType.Invoice; dup.RelatedDocumentId = qt.Id;
        dup.Lines.Add(new DocumentLine { LineOrder = 1, Description = "งานบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7 });
        db.Documents.Add(dup);
        await db.SaveChangesAsync();
        Assert.Equal(s.Clerk, dup.CreatedByUserId);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var d = await req.Documents.Include(x => x.Lines).SingleAsync(x => x.Id == dup.Id);
            Assert.NotNull(await Docs(req).RestrictedCreatorOverConvertAsync(s.CompanyId, d));
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Docs(req).ApproveDocumentAsync(s.CompanyId, dup.Id, s.Owner.ToString(), true));
            Assert.Equal(DocumentService.RestrictedOverConvertRuleCode, ex.RuleCode);
        }
        // ทิศตรงข้าม: ใบเกินแบบเดียวกันที่ผู้ไม่ถูกจำกัดสร้าง ⇒ ไม่ใช่ด่านนี้ (ยังเป็นคำเตือนก่อนอนุมัติตามข้อ 138)
        var dup2 = Doc(s.CompanyId, s.ContactId, s.Other.ToString());
        dup2.DocumentType = DocumentType.Invoice; dup2.RelatedDocumentId = qt.Id;
        dup2.Lines.Add(new DocumentLine { LineOrder = 1, Description = "งานบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7 });
        db.Documents.Add(dup2);
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Null(await Docs(req).RestrictedCreatorOverConvertAsync(s.CompanyId, await req.Documents.Include(x => x.Lines).SingleAsync(x => x.Id == dup2.Id)));
    }

    [Fact]
    public async Task ฝ่ายค้าน_LOW10_ผู้สร้างเป็นid_ทั้งid_อีเมล_ป้ายระบบในคำขอที่ถูกจำกัด_migration_เติมแถวเก่า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var map = await check.Documents.IgnoreQueryFilters().Where(d => d.CompanyId == s.CompanyId).ToDictionaryAsync(d => d.Id, d => d.CreatedByUserId);
            Assert.Equal(s.Clerk, map[s.ClerkDoc]);
            Assert.Equal(s.Clerk, map[s.ClerkEmailDoc]);                   // อีเมลสมาชิก (ทาง LINE)
            Assert.Equal(s.Other, map[s.OtherDoc]);
            Assert.Null(map[s.SystemDoc]);                                  // ป้ายระบบนอกคำขอของผู้ถูกจำกัด
        }
        // ป้ายระบบ (OCR) ในคำขอของผู้ถูกจำกัด ⇒ เป็นของเขา
        using (var req = await RequestAsAsync(s.CompanyId, s.Clerk))
        {
            var ocrDoc = Doc(s.CompanyId, s.ContactId, "ocr-auto");
            req.Documents.Add(ocrDoc);
            await req.SaveChangesAsync();
            Assert.Equal(s.Clerk, ocrDoc.CreatedByUserId);
            Assert.True(await req.Documents.AnyAsync(d => d.Id == ocrDoc.Id));   // เขาเห็นใบนี้
        }
        // แถวเก่า (ไม่มี CreatedByUserId) ⇒ migration เติมจาก id/อีเมลที่จับคู่ได้แน่นอน (ทำซ้ำได้)
        await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""Documents"" SET ""CreatedByUserId"" = NULL WHERE ""CompanyId"" = {s.CompanyId}");
        foreach (var sql in Round276Migration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        foreach (var sql in Round276Migration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var map = await check.Documents.IgnoreQueryFilters().Where(d => d.CompanyId == s.CompanyId).ToDictionaryAsync(d => d.Id, d => d.CreatedByUserId);
            Assert.Equal(s.Clerk, map[s.ClerkDoc]);
            Assert.Equal(s.Clerk, map[s.ClerkEmailDoc]);
            Assert.Equal(s.Other, map[s.OtherDoc]);
            Assert.Null(map[s.SystemDoc]);
        }
    }

    [Fact]
    public async Task ฝ่ายค้าน_LOW7_ผู้ขออนุมัติผ่านLINE_อีเมล_อนุมัติใบที่ตัวเองขอไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var otherEmail = await db.Users.Where(u => u.Id == s.Other).Select(u => u.Email).SingleAsync();
        db.DocumentApprovalRequests.Add(new DocumentApprovalRequest { CompanyId = s.CompanyId, DocumentId = s.OtherDoc, RequestedByUserId = s.Other });
        await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            DocumentApprovalRequestService.EnsureNotSelfApprovalAsync(db, s.CompanyId, s.OtherDoc, otherEmail));
        Assert.Equal(DocumentApprovalRequestService.SelfApprovalCode, ex.RuleCode);
        await DocumentApprovalRequestService.EnsureNotSelfApprovalAsync(db, s.CompanyId, s.OtherDoc, "system:recurring");   // ป้ายระบบ = ไม่ใช่ด่านนี้
    }

    [Fact]
    public async Task ฝ่ายค้าน_LOW12_OCR_ผู้ถูกจำกัดใช้ได้เฉพาะสแกนของตัวเอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var mine = new FileAttachment { CompanyId = s.CompanyId, EntityType = "OcrScan", EntityId = Guid.NewGuid(), FileName = "a.jpg", OriginalFileName = "a.jpg",
            StoragePath = "x", ContentType = "image/jpeg", UploadedByUserId = s.Clerk };
        var theirs = new FileAttachment { CompanyId = s.CompanyId, EntityType = "OcrScan", EntityId = Guid.NewGuid(), FileName = "b.jpg", OriginalFileName = "b.jpg",
            StoragePath = "y", ContentType = "image/jpeg", UploadedByUserId = s.Other };
        db.FileAttachments.AddRange(mine, theirs);
        await db.SaveChangesAsync();
        var myScan = new OcrScanResult { CompanyId = s.CompanyId, FileAttachmentId = mine.Id };
        var theirScan = new OcrScanResult { CompanyId = s.CompanyId, FileAttachmentId = theirs.Id };
        db.OcrScanResults.AddRange(myScan, theirScan);
        await db.SaveChangesAsync();
        async Task<(bool, int?)> Clerk(string m, string t, string key, Guid id)
        {
            using var r = DbTestDatabase.TryCreateContext()!;
            return await RunFilterAsync(r, Ctx(r, s.CompanyId, s.Clerk, m, t, new Dictionary<string, string> { [key] = id.ToString() }));
        }
        Assert.Equal((true, (int?)null), await Clerk("POST", C + "ocr/scan/{fileAttachmentId:guid}", "fileAttachmentId", mine.Id));
        Assert.Equal((true, (int?)null), await Clerk("POST", C + "ocr/{scanId:guid}/create-document", "scanId", myScan.Id));
        Assert.Equal((false, (int?)404), await Clerk("POST", C + "ocr/{scanId:guid}/create-document", "scanId", theirScan.Id));
        Assert.Equal((false, (int?)404), await Clerk("GET", C + "ocr/{scanId:guid}", "scanId", theirScan.Id));
        Assert.Equal((false, (int?)403), await Clerk("POST", C + "ocr/{scanId:guid}/import-stock", "scanId", myScan.Id));
    }

    [Fact]
    public async Task ฝ่ายค้าน_ข้อความตอบกลับ_ใบซ้ำของคนอื่นคืนแค่ผลตัดสิน_ทิศตรงข้ามใบของตัวเองเห็นครบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var theirs = Doc(s.CompanyId, s.ContactId, s.Other.ToString(), DocumentStatus.Approved);
        theirs.DocumentType = DocumentType.PurchaseInvoice; theirs.SupplierInvoiceNumber = "IV-777";
        var mine = Doc(s.CompanyId, s.ContactId, s.Clerk.ToString(), DocumentStatus.Approved);
        mine.DocumentType = DocumentType.PurchaseInvoice; mine.SupplierInvoiceNumber = "IV-888";
        db.Documents.AddRange(theirs, mine);
        await db.SaveChangesAsync();
        using var req = await RequestAsAsync(s.CompanyId, s.Clerk);
        var dupTheirs = await Docs(req).CheckDuplicateAsync(s.CompanyId, s.ContactId, "IV-777", DocumentType.PurchaseInvoice, 500m, DateTime.UtcNow.Date);
        Assert.True(dupTheirs.HasStrongMatch);                         // ตรวจทั้งบริษัท (ไม่ตาบอดเพราะตัวกรองแถว)
        var c = Assert.Single(dupTheirs.Candidates);
        Assert.Equal(Guid.Empty, c.Id);
        Assert.DoesNotContain(theirs.DocumentNumber, c.DocumentNumber);
        Assert.Null(c.ContactName);
        Assert.Equal(500m, c.TotalAmount);                               // ยอดที่ผู้ใช้กรอกเอง ไม่ใช่ยอดของใบนั้น
        var dupMine = await Docs(req).CheckDuplicateAsync(s.CompanyId, s.ContactId, "IV-888", DocumentType.PurchaseInvoice, 500m, DateTime.UtcNow.Date);
        Assert.Equal(mine.DocumentNumber, Assert.Single(dupMine.Candidates).DocumentNumber);
        // ผลสำเร็จที่อ้างเลขของคนอื่น (เช่นหมายเหตุ OCR) ⇒ ถูกแทนด้วยตัวช่วยกลาง
        var resp = new Accounting.Models.DTOs.ApiResponse<object>(true, null, "ซ้ำกับ " + theirs.DocumentNumber + " · ของฉัน " + mine.DocumentNumber);
        await RestrictedResponseRedactor.RedactValueAsync(req, s.CompanyId, s.Clerk, resp);
        Assert.DoesNotContain(theirs.DocumentNumber, resp.Message);
        Assert.Contains(mine.DocumentNumber, resp.Message);
    }

    private sealed record DocumentApprovalSubmitResultShape(string Route, int ApproverCount);
}

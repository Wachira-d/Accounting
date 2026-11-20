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

    private sealed record DocumentApprovalSubmitResultShape(string Route, int ApproverCount);
}

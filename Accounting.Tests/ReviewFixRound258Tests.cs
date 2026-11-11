using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Accounting.Controllers;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Accounting.Tests.Db;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 258 — แก้ผลฝ่ายค้านของแพ็ก "สิทธิ์ + คิวส่ง" (<c>erp-review/2026-11-11/review-258-perms-queue.md</c>) · pure (ไม่แตะฐาน) · สองทิศ:
/// R-H1 งานของตัวตนผู้ใช้ห้ามใช้ API key · R-M1 คีย์รายการประจำมีผู้รับตั้งต้น · R-L2 ธงปุ่มสร้าง+อนุมัติ · R-L3 ค่ากลุ่ม LINE ต่อเหตุการณ์ ·
/// R-L4/R-L5 สิ่งที่ผู้ไม่มีสิทธิ์เห็นจากตารางส่ง · R-L9 หัวเรื่องไม่แสดงเลขชั่วคราว
/// </summary>
public class ReviewFixRound258Tests
{
    private static readonly Guid Company = Guid.Parse("25800000-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("25800000-0000-0000-0000-0000000000bb");

    // ───── R-H1 ─────

    public static IEnumerable<object[]> IdentityActions() => new[]
    {
        new object[] { typeof(NotificationConfigController), nameof(NotificationConfigController.ClearMyLineBinding) },
        new object[] { typeof(AuthController), nameof(AuthController.LinkExternalLogin) },
        new object[] { typeof(AuthController), nameof(AuthController.RemoveExternalLogin) },
        new object[] { typeof(AuthController), nameof(AuthController.ChangePassword) },
        new object[] { typeof(AuthController), nameof(AuthController.UpdateProfile) },
        new object[] { typeof(MobileController), nameof(MobileController.RegisterDevice) },
    };

    [Theory]
    [MemberData(nameof(IdentityActions))]
    public void งานของตัวตนผู้ใช้_บนrouteที่ไม่มีบริษัท_ติดด่านห้ามAPIkey(Type controller, string action)
    {
        var method = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(m => m.Name == action);
        Assert.NotNull(method.GetCustomAttribute<RejectApiKeyAttribute>());
    }

    private static AuthorizationFilterContext FilterContext(bool apiKey)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) }, "test")),
        };
        if (apiKey) http.Items["IsApiKeyAuth"] = true;
        return new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>());
    }

    [Fact]
    public void ด่านห้ามAPIkey_คีย์ได้403พร้อมทางไปต่อ_คนที่ล็อกอินผ่าน()
    {
        var gate = typeof(AuthController).GetMethod(nameof(AuthController.LinkExternalLogin))!.GetCustomAttribute<RejectApiKeyAttribute>()!;
        var key = FilterContext(apiKey: true);
        gate.OnAuthorization(key);
        var denied = Assert.IsType<ObjectResult>(key.Result);
        Assert.Equal(403, denied.StatusCode);
        Assert.Contains("ผูกบัญชีภายนอก", Json(denied.Value), StringComparison.Ordinal);
        // ทิศตรงข้าม: คนที่ล็อกอินบนเว็บไม่ถูกกัน
        var person = FilterContext(apiKey: false);
        gate.OnAuthorization(person);
        Assert.Null(person.Result);
    }

    private static (LineBotBindController Ctl, List<string> BotCalls) Bind(bool apiKey)
    {
        var calls = new List<string>();
        var bot = F4Proxy<ILineBotService>.Create((name, _) => { calls.Add(name); return name == nameof(ILineBotService.IssueBindCodeAsync) ? "123456" : null; });
        var central = F4Proxy<INextAccLineConfig>.Create((name, _) => name == nameof(INextAccLineConfig.GetAsync)
            ? Task.FromResult(new NextAccLineSettings("T", "S", "@nextacc", null, null, NextAccLineConfigRule.SourceAdmin))
            : Task.FromResult(NextAccLineConfigRule.PlatformGroupIds(null, null)));
        var ctx = Ctx();
        if (apiKey) ctx.HttpContext.Items["IsApiKeyAuth"] = true;
        return (new LineBotBindController(bot, central) { ControllerContext = ctx }, calls);
    }

    [Fact]
    public async Task ออกรหัสผูกLINE_คีย์API_403_ไม่ออกรหัส_แม้ตัวตนของคีย์คือเจ้าของ()
    {
        var (ctl, calls) = Bind(apiKey: true);
        var result = await ctl.IssueBindCode();
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task ออกรหัสผูกLINE_ทิศตรงข้าม_คนที่ล็อกอินได้รหัสตามเดิม()
    {
        var (ctl, calls) = Bind(apiKey: false);
        var result = await ctl.IssueBindCode();
        var ok = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(200, ok.StatusCode ?? 200);
        Assert.Contains("123456", Json(ok.Value), StringComparison.Ordinal);
        Assert.Equal(new[] { nameof(ILineBotService.IssueBindCodeAsync) }, calls);
    }

    // ───── R-M1 ─────

    [Theory]
    [InlineData(NotificationEvents.RecurringRunFailed)]
    [InlineData(NotificationEvents.RecurringApprovedWithNotes)]
    public void คีย์รายการประจำ_บริษัทยังไม่ตั้ง_ค่าตั้งต้นถึงเจ้าของทางกระดิ่งและอีเมล(string key)
    {
        var row = Assert.Single(NotificationAudiencePolicy.DefaultMatrix(key, companyLineGroupConfigured: false));
        Assert.Equal("Owner", row.RecipientRole);
        Assert.True(row.EnableSystem);
        Assert.True(row.EnableEmail);
    }

    [Fact]
    public void ทิศตรงข้าม_คีย์แจ้งล่วงหน้ายังเป็นกระดิ่งอย่างเดียว()
    {
        var row = Assert.Single(NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.RecurringUpcomingRun, false));
        Assert.True(row.EnableSystem);
        Assert.False(row.EnableEmail);
    }

    // ───── R-L9 ─────

    [Fact]
    public void หัวเรื่องค้างร่าง_ไม่แสดงเลขชั่วคราวของใบร่าง_เลขจริงยังแสดง()
    {
        var draft = RecurringStaffNotice.StuckDraftTitle("ค่าเช่า", "DRAFT-0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.DoesNotContain("DRAFT-", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("(", draft, StringComparison.Ordinal);
        Assert.Contains("ค่าเช่า", draft, StringComparison.Ordinal);
        Assert.Contains("(INV-2026-0001)", RecurringStaffNotice.StuckDraftTitle("ค่าเช่า", "INV-2026-0001"), StringComparison.Ordinal);
        Assert.Null(RecurringStaffNotice.ReadableNumber(null));
        Assert.Null(RecurringStaffNotice.ReadableNumber("draft-abc"));
        Assert.Equal("INV-1", RecurringStaffNotice.ReadableNumber(" INV-1 "));
    }

    // ───── R-L3 ─────

    [Fact]
    public void ค่ากลุ่มLINEของเหตุการณ์_แถวใดส่งtrueคือเปิด_ส่งมาแต่falseคือปิด_ไม่มีแถวใดส่งคือคงเดิม()
    {
        Assert.True(NotificationSettingMerge.EventValue(new bool?[] { false, null, true }));
        Assert.False(NotificationSettingMerge.EventValue(new bool?[] { null, false, null }));
        Assert.Null(NotificationSettingMerge.EventValue(new bool?[] { null, null }));
        Assert.Null(NotificationSettingMerge.EventValue(Array.Empty<bool?>()));
    }

    // ───── R-L2 ─────

    [Fact]
    public void ปุ่มสร้างพร้อมอนุมัติในหน้าตรวจ_ตามด่านเดียวกับendpoint()
    {
        var noIssues = Array.Empty<string>();
        Assert.True(OcrPostingReadiness.EvaluateReviewed("Completed", false, null, true, noIssues, "A")!.Value.CanAutoApprove);
        var blocked = OcrPostingReadiness.EvaluateReviewed("Completed", false, null, true, new[] { "error" }, "A")!.Value;
        Assert.False(blocked.CanAutoApprove);
        Assert.False(string.IsNullOrWhiteSpace(blocked.Reason));
        Assert.False(OcrPostingReadiness.EvaluateReviewed("Completed", false, null, true, noIssues, "D")!.Value.CanAutoApprove);
        // คนเปิดดูแล้ว: ไม่มีเงื่อนไขลายมือ/สินทรัพย์/ใบซ้ำ (ต่างจากช่องติ๊กอนุมัติรวด) — ใบสะอาดผ่าน
        // ไม่ประเมิน: สร้างแล้ว / ยังสแกนไม่เสร็จ
        Assert.Null(OcrPostingReadiness.EvaluateReviewed("Completed", true, null, true, noIssues, "A"));
        Assert.Null(OcrPostingReadiness.EvaluateReviewed("Failed", false, null, true, noIssues, "A"));
    }

    // ───── R-L4 / R-L5 ─────

    [Fact]
    public void ผู้ไม่มีสิทธิ์_ไม่เห็นBccและเทมเพลตของกฎ_ผู้มีสิทธิ์เห็นครบ()
    {
        EmailScheduleRule Rule() => new() { Trigger = "PayrollPaid", BccEmails = "boss@co.th", SubjectTemplate = "สลิป {Period}", BodyTemplate = "<p>x</p>", SendAtHour = 9 };
        var hidden = ScheduleReadView.Rule(Rule(), canManage: false);
        Assert.Null(hidden.BccEmails);
        Assert.Null(hidden.SubjectTemplate);
        Assert.Null(hidden.BodyTemplate);
        Assert.Equal("PayrollPaid", hidden.Trigger);
        Assert.Equal(9, hidden.SendAtHour);
        var full = ScheduleReadView.Rule(Rule(), canManage: true);
        Assert.Equal("boss@co.th", full.BccEmails);
        Assert.Equal("<p>x</p>", full.BodyTemplate);
    }

    [Fact]
    public void แถวคิวส่วนบุคคล_ซ่อนผู้รับและหัวเรื่องจากผู้ไม่มีสิทธิ์_แถวเอกสารคงเดิม()
    {
        Assert.Equal("•••@staff.co.th", ScheduleReadView.Recipient("somchai@staff.co.th", "PayrollDetail", canManage: false));
        Assert.Equal("•••@a.co, •••@b.co", ScheduleReadView.Recipient("x@a.co; y@b.co", "WithholdingTaxCert", false));
        Assert.Equal(ScheduleReadView.HiddenSubject, ScheduleReadView.Subject("สลิปเงินเดือน สมชาย", "PayrollDetail", false));
        // ทิศตรงข้าม: ผู้มีสิทธิ์เห็นครบ · แถวเอกสาร/ใบแจ้งยอดไม่ถูกซ่อน
        Assert.Equal("somchai@staff.co.th", ScheduleReadView.Recipient("somchai@staff.co.th", "PayrollDetail", true));
        Assert.Equal("สลิปเงินเดือน สมชาย", ScheduleReadView.Subject("สลิปเงินเดือน สมชาย", "PayrollDetail", true));
        Assert.Equal("ap@buyer.co.th", ScheduleReadView.Recipient("ap@buyer.co.th", "Document", false));
        Assert.Equal("ใบแจ้งหนี้ INV-1", ScheduleReadView.Subject("ใบแจ้งหนี้ INV-1", "MonthlyStatement", false));
    }

    private static ControllerContext Ctx()
        => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) }, "test")),
            },
        };

    private static EmailScheduleController Schedule(bool allowed)
    {
        var svc = F4Proxy<IEmailScheduleService>.Create((name, _) => name switch
        {
            nameof(IEmailScheduleService.GetRulesAsync) => (object?)new List<EmailScheduleRule>
            {
                new() { CompanyId = Company, Trigger = "PayrollPaid", BccEmails = "leak@example.com", BodyTemplate = "เนื้อหา" },
            },
            nameof(IEmailScheduleService.GetRecentQueueAsync) => new List<EmailQueue>
            {
                new() { CompanyId = Company, EntityType = "PayrollDetail", ToEmail = "somchai@staff.co.th", Subject = "สลิปเงินเดือน สมชาย", Status = "Failed" },
                new() { CompanyId = Company, EntityType = "Document", ToEmail = "ap@buyer.co.th", Subject = "ใบแจ้งหนี้ INV-1", Status = "Pending" },
            },
            _ => null,
        });
        var perms = F4Proxy<IPermissionService>.Create((name, _) => name == nameof(IPermissionService.HasPermissionAsync) ? (object)allowed : new List<string>());
        return new EmailScheduleController(svc, null, perms) { ControllerContext = Ctx() };
    }

    private static string Json(object? value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [Fact]
    public async Task ตารางส่ง_ผู้ไม่มีสิทธิ์อ่านได้แต่ไม่เห็นของลับ_และไม่มีปุ่ม()
    {
        var ctl = Schedule(allowed: false);
        var rules = Json(((ObjectResult)(await ctl.GetRules(Company)).Result!).Value);
        Assert.Contains("PayrollPaid", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("leak@example.com", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("เนื้อหา", rules, StringComparison.Ordinal);

        var queue = Json(((ObjectResult)(await ctl.GetQueue(Company)).Result!).Value);
        Assert.DoesNotContain("somchai@staff.co.th", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("สลิปเงินเดือน สมชาย", queue, StringComparison.Ordinal);
        Assert.Contains("ap@buyer.co.th", queue, StringComparison.Ordinal);       // แถวเอกสารไม่ถูกซ่อน
        Assert.Contains("\"canManage\":false", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CanRetry\":true", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CanCancel\":true", queue, StringComparison.Ordinal);

        var access = Json(((ObjectResult)(await ctl.Access(Company)).Result!).Value);
        Assert.Contains("\"canManage\":false", access, StringComparison.Ordinal);
        Assert.Contains("CompanySettings.Edit", access, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ตารางส่ง_ทิศตรงข้าม_ผู้มีสิทธิ์เห็นครบและมีปุ่ม()
    {
        var ctl = Schedule(allowed: true);
        var rules = Json(((ObjectResult)(await ctl.GetRules(Company)).Result!).Value);
        Assert.Contains("leak@example.com", rules, StringComparison.Ordinal);
        Assert.Contains("เนื้อหา", rules, StringComparison.Ordinal);

        var queue = Json(((ObjectResult)(await ctl.GetQueue(Company)).Result!).Value);
        Assert.Contains("somchai@staff.co.th", queue, StringComparison.Ordinal);
        Assert.Contains("สลิปเงินเดือน สมชาย", queue, StringComparison.Ordinal);
        Assert.Contains("\"canManage\":true", queue, StringComparison.Ordinal);
        Assert.Contains("\"CanRetry\":true", queue, StringComparison.Ordinal);    // แถว Failed
        Assert.Contains("\"CanCancel\":true", queue, StringComparison.Ordinal);   // แถว Pending

        var access = Json(((ObjectResult)(await ctl.Access(Company)).Result!).Value);
        Assert.Contains("\"canManage\":true", access, StringComparison.Ordinal);
    }
}

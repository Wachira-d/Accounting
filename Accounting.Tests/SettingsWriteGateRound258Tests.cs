using System.Security.Claims;
using Accounting.Controllers;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Accounting.Tests.Db;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 258 (A-H1 · B-H1 · A-H3 · A-H2 · A-M9) — ด่านเขียนของหน้าตารางส่ง/ตารางแจ้งเตือน + หน้าผูก LINE พนักงาน (pure · ไม่แตะฐาน):
/// <para>(1) <see cref="SettingsWriteGate"/> สองทิศ: ไม่มีสิทธิ์ = ปฏิเสธพร้อมทางไปต่อ · มีสิทธิ์ = ผ่าน · คีย์กับงานตั้งกฎ = ปฏิเสธแม้ตัวตนมีสิทธิ์</para>
/// <para>(2) <see cref="EmailScheduleController"/> / <see cref="NotificationConfigController"/>: ผู้ไม่มีสิทธิ์ได้ 403 และ<b>บริการไม่ถูกเรียก</b> ·
/// ทิศตรงข้าม: ผู้มีสิทธิ์ยังบันทึกได้ · ค่าส่วนตัวของผู้ใช้ไม่ต้องมีสิทธิ์ตั้งค่าบริษัท</para>
/// <para>(3) <see cref="NotificationSettingMerge"/>: ช่องที่ไม่ได้ส่ง = คงค่าที่ใช้อยู่ · ค่าที่ส่งมาชนะ</para>
/// <para>(4) <see cref="StaffLineBindView"/>: ชื่อบอท/ลิงก์จากค่าตั้ง OA กลาง · ยังไม่พร้อม = บอกเหตุ</para>
/// </summary>
public class SettingsWriteGateRound258Tests
{
    private static readonly Guid Company = Guid.Parse("25800000-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("25800000-0000-0000-0000-0000000000aa");

    // ───── (1) ตัวตัดสิน ─────

    [Fact]
    public void Gate_ไม่มีสิทธิ์_ปฏิเสธพร้อมชื่องานและทางไปต่อ()
    {
        var d = SettingsWriteGate.Decide("แก้กฎตารางส่ง", isApiKeyRequest: false, rejectApiKey: true, hasSettingsEdit: false);
        Assert.False(d.Allowed);
        Assert.Equal(SettingsWriteGate.RuleCode, d.RuleCode);
        Assert.Contains("แก้กฎตารางส่ง", d.Message!, StringComparison.Ordinal);
        Assert.Contains("CompanySettings.Edit", d.Message!, StringComparison.Ordinal);
        Assert.Contains("บทบาทและสิทธิ์", d.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_ทิศตรงข้าม_มีสิทธิ์_ผ่าน_ไม่มีข้อความ()
    {
        var d = SettingsWriteGate.Decide("แก้กฎตารางส่ง", false, true, true);
        Assert.True(d.Allowed);
        Assert.Null(d.Message);
        Assert.Null(d.RuleCode);
    }

    [Fact]
    public void Gate_คีย์กับงานตั้งกฎ_ปฏิเสธแม้ตัวตนของคีย์มีสิทธิ์()
    {
        var d = SettingsWriteGate.Decide("ลบกฎตารางส่ง", isApiKeyRequest: true, rejectApiKey: true, hasSettingsEdit: true);
        Assert.False(d.Allowed);
        Assert.Equal(OwnerActionGuard.RuleCode, d.RuleCode);
        Assert.Contains("API key", d.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_คีย์กับงานคิว_ที่ไม่ห้ามคีย์_ตัดสินด้วยสิทธิ์()
    {
        Assert.True(SettingsWriteGate.Decide("ส่งอีกครั้ง", true, rejectApiKey: false, hasSettingsEdit: true).Allowed);
        Assert.False(SettingsWriteGate.Decide("ส่งอีกครั้ง", true, rejectApiKey: false, hasSettingsEdit: false).Allowed);
    }

    // ───── (2) controller ─────

    private sealed class Calls { public List<string> Names { get; } = new(); }

    private static ControllerContext Ctx(bool apiKey = false)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) }, "test")),
        };
        if (apiKey) http.Items["IsApiKeyAuth"] = true;
        return new ControllerContext { HttpContext = http };
    }

    private static IPermissionService Perms(bool allowed, Calls? asked = null)
        => F4Proxy<IPermissionService>.Create((name, args) =>
        {
            asked?.Names.Add(name + ":" + (args.Length > 2 ? args[2] : ""));
            return name == nameof(IPermissionService.HasPermissionAsync) ? (object)allowed : new List<string>();
        });

    private static (EmailScheduleController Ctl, Calls Svc) Schedule(bool allowed, bool apiKey = false)
    {
        var calls = new Calls();
        var svc = F4Proxy<IEmailScheduleService>.Create((name, _) =>
        {
            calls.Names.Add(name);
            return name == nameof(IEmailScheduleService.UpsertRuleAsync) ? new EmailScheduleRule { Trigger = "DocumentApproved" } : null;
        });
        var ctl = new EmailScheduleController(svc, null, Perms(allowed)) { ControllerContext = Ctx(apiKey) };
        return (ctl, calls);
    }

    private static EmailScheduleController.RuleRequest Rule()
        => new(Guid.Empty, "DocumentApproved", true, null, 0, 9, 0, "เรื่อง", "เนื้อหา", "leak@example.com");

    private static int? Status<T>(ActionResult<T> r) => r.Result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    [Fact]
    public async Task ตารางส่ง_ผู้ไม่มีสิทธิ์_403ทุกคำสั่งเขียน_บริการไม่ถูกเรียก()
    {
        var (ctl, calls) = Schedule(allowed: false);
        Assert.Equal(403, Status(await ctl.UpsertRule(Company, Rule())));
        Assert.Equal(403, Status(await ctl.DeleteRule(Company, Guid.NewGuid())));
        Assert.Equal(403, Status(await ctl.Retry(Company, Guid.NewGuid())));
        Assert.Equal(403, Status(await ctl.Cancel(Company, Guid.NewGuid())));
        Assert.Empty(calls.Names);
    }

    [Fact]
    public async Task ตารางส่ง_ทิศตรงข้าม_ผู้มีสิทธิ์_ยังบันทึก_ลบ_ส่งอีกครั้ง_ยกเลิกได้()
    {
        var (ctl, calls) = Schedule(allowed: true);
        Assert.Equal(200, Status(await ctl.UpsertRule(Company, Rule())));
        Assert.Equal(200, Status(await ctl.DeleteRule(Company, Guid.NewGuid())));
        Assert.Equal(200, Status(await ctl.Retry(Company, Guid.NewGuid())));
        Assert.Equal(200, Status(await ctl.Cancel(Company, Guid.NewGuid())));
        Assert.Equal(new[]
        {
            nameof(IEmailScheduleService.UpsertRuleAsync), nameof(IEmailScheduleService.DeleteRuleAsync),
            nameof(IEmailScheduleService.RetryFailedAsync), nameof(IEmailScheduleService.CancelPendingAsync),
        }, calls.Names);
    }

    [Fact]
    public async Task ตารางส่ง_คีย์API_ตั้งและลบกฎไม่ได้แม้ตัวตนมีสิทธิ์_แต่ส่งอีกครั้งในคิวได้()
    {
        var (ctl, calls) = Schedule(allowed: true, apiKey: true);
        Assert.Equal(403, Status(await ctl.UpsertRule(Company, Rule())));
        Assert.Equal(403, Status(await ctl.DeleteRule(Company, Guid.NewGuid())));
        Assert.Empty(calls.Names);
        Assert.Equal(200, Status(await ctl.Retry(Company, Guid.NewGuid())));
        Assert.Equal(new[] { nameof(IEmailScheduleService.RetryFailedAsync) }, calls.Names);
    }

    [Fact]
    public async Task ตารางส่ง_ไม่มีบริการสิทธิ์_ปิดทางปลอดภัย_403()
    {
        var calls = new Calls();
        var svc = F4Proxy<IEmailScheduleService>.Create((name, _) => { calls.Names.Add(name); return null; });
        var ctl = new EmailScheduleController(svc) { ControllerContext = Ctx() };
        Assert.Equal(403, Status(await ctl.DeleteRule(Company, Guid.NewGuid())));
        Assert.Empty(calls.Names);
    }

    private static (NotificationConfigController Ctl, Calls Svc) Notify(bool allowed, bool apiKey = false)
    {
        var calls = new Calls();
        var svc = F4Proxy<INotificationConfigService>.Create((name, _) => { calls.Names.Add(name); return null; });
        var ctl = new NotificationConfigController(svc, Perms(allowed)) { ControllerContext = Ctx(apiKey) };
        return (ctl, calls);
    }

    private static BulkUpdateNotificationSettingsRequest Matrix()
        => new(new List<NotificationSettingUpdateDto> { new("document.approved", "Owner", true, false, false) });

    [Fact]
    public async Task ตารางแจ้งเตือน_ผู้ไม่มีสิทธิ์และคีย์_403_บริการไม่ถูกเรียก()
    {
        var (viewer, viewerCalls) = Notify(allowed: false);
        Assert.Equal(403, Status(await viewer.BulkUpsertSettings(Company, Matrix())));
        Assert.Empty(viewerCalls.Names);

        var (key, keyCalls) = Notify(allowed: true, apiKey: true);
        Assert.Equal(403, Status(await key.BulkUpsertSettings(Company, Matrix())));
        Assert.Empty(keyCalls.Names);
    }

    [Fact]
    public async Task ตารางแจ้งเตือน_ทิศตรงข้าม_ผู้มีสิทธิ์บันทึกได้_และค่าส่วนตัวไม่ต้องมีสิทธิ์ตั้งค่าบริษัท()
    {
        var (admin, adminCalls) = Notify(allowed: true);
        Assert.Equal(200, Status(await admin.BulkUpsertSettings(Company, Matrix())));
        Assert.Equal(new[] { nameof(INotificationConfigService.BulkUpsertCompanySettingsAsync) }, adminCalls.Names);

        var (viewer, viewerCalls) = Notify(allowed: false);
        var prefs = new BulkUpdateNotificationPreferencesRequest(new List<NotificationPreferenceDto> { new("document.approved", false, true, false) });
        Assert.Equal(200, Status(await viewer.UpsertMyPreferences(Company, prefs)));
        Assert.Equal(new[] { nameof(INotificationConfigService.BulkUpsertUserPreferencesAsync) }, viewerCalls.Names);
    }

    [Fact]
    public void ทางผูกLINEด้วยuserIdดิบ_ถูกถอด_ไม่มีactionและไม่มีเมธอดบริการ()
    {
        Assert.Null(typeof(NotificationConfigController).GetMethod("SetMyLineBinding"));
        Assert.Null(typeof(INotificationConfigService).GetMethod("SetLineBindingAsync"));
        // สถานะการผูกไม่ส่ง LINE userId เต็ม (กุญแจยืนยันตัวของบอท)
        Assert.Null(typeof(LineBindingResponse).GetProperty("LineUserId"));
        Assert.NotNull(typeof(LineBindingResponse).GetProperty(nameof(LineBindingResponse.LineUserIdTail)));
    }

    // ───── (3) รวมค่า "กลุ่ม LINE ของบริษัท" ─────

    [Theory]
    // ไม่ได้ส่งช่องนี้ (null) = คงค่าที่ใช้อยู่
    [InlineData(null, true, true, true, false, true)]      // แถวเดิมเปิด ⇒ เปิดต่อ (บั๊กเดิม: ถูกปิด)
    [InlineData(null, false, true, true, true, false)]     // แถวเดิมปิด ⇒ ปิดต่อ (ไม่เปิดเองจากแถวอื่น/ค่าตั้งต้น)
    [InlineData(null, null, true, true, false, true)]      // แถวใหม่ของเหตุการณ์ที่ตั้งแล้ว ⇒ ตามแถวอื่นของเหตุการณ์
    [InlineData(null, null, true, false, true, false)]     // เหตุการณ์ที่บริษัทปิดกลุ่มไว้แล้ว ⇒ ค่าตั้งต้นของระบบไม่เปิดกลับ
    [InlineData(null, null, false, false, true, true)]     // ยังไม่เคยตั้ง ⇒ ค่าตั้งต้นของระบบ (เปิด) ถูกเก็บ ไม่หาย
    [InlineData(null, null, false, false, false, false)]   // ยังไม่เคยตั้ง + ค่าตั้งต้นปิด ⇒ ปิด
    // ทิศตรงข้าม: ผู้เรียกส่งค่ามา = ชนะเสมอ
    [InlineData(false, true, true, true, true, false)]
    [InlineData(true, false, true, false, false, true)]
    [InlineData(false, null, false, false, true, false)]
    public void รวมค่ากลุ่มLINE(bool? requested, bool? stored, bool eventHasRows, bool anyStoredOn, bool systemDefault, bool expected)
        => Assert.Equal(expected, NotificationSettingMerge.LineGroup(requested, stored, eventHasRows, anyStoredOn, systemDefault));

    [Fact]
    public void ป้ายบทบาทผู้รับ_ครบทุกบทบาทในแคตตาล็อก()
    {
        foreach (var role in Accounting.Models.Constants.NotificationRecipientRoles.All)
            Assert.False(string.IsNullOrWhiteSpace(Accounting.Models.Constants.NotificationRecipientRoles.Labels[role]));
        Assert.Equal("ฝ่ายบัญชี", Accounting.Models.Constants.NotificationRecipientRoles.Labels["Accounting"]);
    }

    // ───── (4) หน้าผูก LINE พนักงาน ─────

    [Fact]
    public void หน้าผูก_บอทพร้อม_ส่งชื่อบอทและลิงก์เพิ่มเพื่อนจากค่าตั้งOAกลาง()
    {
        var v = StaffLineBindView.From(botReady: true, centralBasicId: "nextacc");
        Assert.True(v.BotReady);
        Assert.Equal("@nextacc", v.BasicId);
        Assert.Equal("https://line.me/R/ti/p/%40nextacc", v.AddFriendUrl);
        Assert.Null(v.Notice);
    }

    [Fact]
    public void หน้าผูก_บอทพร้อมแต่ไม่มีBasicId_ยังออกรหัสได้_บอกว่าไม่มีชื่อให้ค้น()
    {
        var v = StaffLineBindView.From(true, null);
        Assert.True(v.BotReady);
        Assert.Null(v.BasicId);
        Assert.Null(v.AddFriendUrl);
        Assert.Equal(StaffLineBindView.NoBasicIdNotice, v.Notice);
    }

    [Fact]
    public void หน้าผูก_บอทไม่พร้อม_ไม่ออกรหัส_บอกเหตุ()
    {
        var v = StaffLineBindView.From(false, "@nextacc");
        Assert.False(v.BotReady);
        Assert.Equal(StaffLineBindView.NotReadyNotice, v.Notice);
    }
}

using System.Net;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Tests.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 232 (คำตัดสิน 232-Notify) — นโยบายผู้รับแจ้งเตือน LINE: เรื่องของผู้เช่า vs เรื่องของแพลตฟอร์ม
/// <para>ที่มา: OA กลางของ NextAcc ส่ง "✅ เอกสารอนุมัติแล้ว PI-… บริษัท บุญทรัพย์ ถาวร จำกัด" เข้าห้องของเจ้าของแพลตฟอร์ม เพราะ
/// <c>LineNotifyService.ResolveConfigAsync</c> เดิมถอยไป token + GroupId ของ OA กลางเมื่อบริษัทไม่มี OA</para>
/// ล็อกทั้งสองทิศ: (ก) TenantBusiness ไม่ถึงกลุ่มกลาง/แอดมินแม้บริษัทไม่มี OA (ไม่ fallback) · (ข) PlatformAccount ถึงเจ้าของผ่าน OA กลางได้ ·
/// (ค) PlatformOps ถึงกลุ่มกลางได้ · (ง) 1:1 ถึงสมาชิกผู้เช่าผ่านบอทกลางยังทำงาน (เส้นพนักงาน/ลูกค้าของรอบ 211/212 ไม่ถดถอย)
/// </summary>
public class NotificationAudiencePolicyRound232Tests
{
    // ───────── Classify ─────────

    [Theory]
    [InlineData("document.approved", NotificationAudience.TenantBusiness)]
    [InlineData("document.payment_received", NotificationAudience.TenantBusiness)]
    [InlineData("lodging.booking_created", NotificationAudience.TenantBusiness)]
    [InlineData("payroll.slip_ready", NotificationAudience.TenantBusiness)]
    [InlineData("approval.required", NotificationAudience.TenantBusiness)]
    [InlineData("subscription.trial_ending", NotificationAudience.PlatformAccount)]
    [InlineData("subscription.payment_succeeded", NotificationAudience.PlatformAccount)]
    [InlineData("quota.documents_level", NotificationAudience.PlatformAccount)]
    [InlineData("notify.quota.80", NotificationAudience.PlatformAccount)]
    [InlineData("overage.started", NotificationAudience.PlatformAccount)]
    [InlineData("platform.ops.webhook_failed", NotificationAudience.PlatformOps)]
    [InlineData("", NotificationAudience.TenantBusiness)]
    [InlineData(null, NotificationAudience.TenantBusiness)]
    [InlineData("something.unknown", NotificationAudience.TenantBusiness)]
    public void Classify_maps_event_keys_and_defaults_unknown_to_tenant(string? key, NotificationAudience expected)
        => Assert.Equal(expected, NotificationAudiencePolicy.Classify(key));

    [Fact]
    public void Every_catalog_event_is_classified_and_platform_keys_never_land_in_tenant_group_defaults()
    {
        foreach (var (_, key, _) in NotificationEvents.All)
        {
            var a = NotificationAudiencePolicy.Classify(key);
            // คีย์ subscription.* / quota.* / notify.quota.* / overage.* = เรื่องบัญชีกับแพลตฟอร์ม · ที่เหลือ = ธุรกิจของผู้เช่า
            var platform = key.StartsWith("subscription.") || key.StartsWith("quota.") || key.StartsWith("notify.quota.") || key.StartsWith("overage.");
            Assert.Equal(platform ? NotificationAudience.PlatformAccount : NotificationAudience.TenantBusiness, a);
            if (platform) Assert.DoesNotContain(key, NotificationAudiencePolicy.MigratedBroadcastKeys);
        }
    }

    // ───────── LineRoute: TenantBusiness ─────────

    [Theory]
    [InlineData(LineOaKind.NextAccOa)]
    [InlineData(LineOaKind.CompanyOa)]
    public void Tenant_business_never_reaches_platform_group_even_without_company_oa(LineOaKind oa)
    {
        // ผู้รับเป็นทั้งเจ้าของผู้เช่าและแอดมิน: ได้เฉพาะตามบทบาทที่เหตุการณ์กำหนด — ในฐานะห้องของแอดมิน = ไม่ได้ (ไม่ว่า OA ไหน)
        foreach (var configured in new[] { true, false })
        {
            var d = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, oa, LineTargetKind.PlatformGroup, configured);
            Assert.False(d.Allowed);
            Assert.Equal(NotificationAudiencePolicy.RuleTenantNotPlatform, d.RuleCode);
            Assert.Contains("ไม่ส่งเข้ากลุ่ม LINE กลาง", d.Reason);
        }
    }

    [Fact]
    public void Tenant_business_without_company_oa_is_denied_not_redirected()
    {
        var d = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.CompanyOa, LineTargetKind.CompanyGroup, companyOaConfigured: false);
        Assert.False(d.Allowed);
        Assert.Equal(NotificationAudiencePolicy.RuleTenantNoCompanyOa, d.RuleCode);
        Assert.Equal(NotificationAudiencePolicy.NoCompanyOaMessage, d.Reason);
        Assert.Contains("ไม่ส่งผ่าน LINE OA กลาง", d.Reason);
        // กลุ่มของบริษัทผ่าน OA กลางไม่มีความหมาย ⇒ กั้นเช่นกัน
        Assert.False(NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.NextAccOa, LineTargetKind.CompanyGroup, false).Allowed);
    }

    [Fact]
    public void Tenant_business_with_company_oa_is_allowed_for_group_and_member()
    {
        Assert.True(NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.CompanyOa, LineTargetKind.CompanyGroup, true).Allowed);
        var m = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.CompanyOa, LineTargetKind.TenantMember, true);
        Assert.True(m.Allowed);
        Assert.Equal(NotificationAudiencePolicy.RuleTenantCompanyOa, m.RuleCode);
        Assert.Null(m.Reason);
    }

    [Fact]
    public void Tenant_member_bound_to_nextacc_bot_still_receives_one_to_one()
    {
        // ทิศตรงข้าม: พนักงาน/ลูกค้าที่ผูก "ผูก 123456" กับบอทกลางเอง = ช่องส่วนตัวของเขา (รอบ 212 C ไม่ถดถอย)
        var d = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.NextAccOa, LineTargetKind.TenantMember, companyOaConfigured: false);
        Assert.True(d.Allowed);
        Assert.Equal(NotificationAudiencePolicy.RuleTenantMemberViaNextAcc, d.RuleCode);
    }

    // ───────── LineRoute: PlatformAccount ─────────

    [Fact]
    public void Platform_account_reaches_owner_via_nextacc_but_not_platform_group_or_company_oa()
    {
        var ok = NotificationAudiencePolicy.LineRoute(NotificationAudience.PlatformAccount, LineOaKind.NextAccOa, LineTargetKind.TenantMember, false);
        Assert.True(ok.Allowed);
        Assert.Equal(NotificationAudiencePolicy.RuleAccountOwnerViaNextAcc, ok.RuleCode);

        var grp = NotificationAudiencePolicy.LineRoute(NotificationAudience.PlatformAccount, LineOaKind.NextAccOa, LineTargetKind.PlatformGroup, false);
        Assert.False(grp.Allowed);
        Assert.Equal(NotificationAudiencePolicy.RuleAccountNotPlatformGroup, grp.RuleCode);

        var co = NotificationAudiencePolicy.LineRoute(NotificationAudience.PlatformAccount, LineOaKind.CompanyOa, LineTargetKind.TenantMember, true);
        Assert.False(co.Allowed);
        Assert.Equal(NotificationAudiencePolicy.RuleAccountNotCompanyOa, co.RuleCode);
    }

    // ───────── LineRoute: PlatformOps ─────────

    [Fact]
    public void Platform_ops_reaches_platform_group_only()
    {
        // ฝ่ายค้าน 232 N3: ปลายทาง LINE ของ PlatformOps มีแค่กลุ่มกลาง (ไม่มี "แอดมิน 1:1" — ทางที่ไม่มีผู้เรียกถูกลบ · enum มี 3 ค่า)
        Assert.Equal(3, Enum.GetValues<LineTargetKind>().Length);
        Assert.True(NotificationAudiencePolicy.LineRoute(NotificationAudience.PlatformOps, LineOaKind.NextAccOa, LineTargetKind.PlatformGroup, false).Allowed);
        foreach (var t in new[] { LineTargetKind.TenantMember, LineTargetKind.CompanyGroup })
        {
            var d = NotificationAudiencePolicy.LineRoute(NotificationAudience.PlatformOps, LineOaKind.NextAccOa, t, true);
            Assert.False(d.Allowed);
            Assert.Equal(NotificationAudiencePolicy.RuleOpsNotTenant, d.RuleCode);
        }
        Assert.False(NotificationAudiencePolicy.LineRoute(NotificationAudience.PlatformOps, LineOaKind.CompanyOa, LineTargetKind.PlatformGroup, true).Allowed);
    }

    // ───────── DefaultMatrix / CompanyLineGroupConfigured / CompanyGroupNotice ─────────

    [Fact]
    public void Default_matrix_for_migrated_keys_is_owner_bell_plus_company_group_only_when_bound()
    {
        foreach (var key in NotificationAudiencePolicy.MigratedBroadcastKeys)
        {
            var noOa = Assert.Single(NotificationAudiencePolicy.DefaultMatrix(key, companyLineGroupConfigured: false));
            Assert.Equal("Owner", noOa.RecipientRole);
            Assert.True(noOa.EnableSystem);
            Assert.False(noOa.EnableEmail);
            Assert.False(noOa.EnableLine);
            Assert.False(noOa.EnableLineGroup);
            // บริษัทที่ตั้ง OA + กลุ่มไว้แล้วเคยได้ประกาศกลุ่ม ⇒ ค่าตั้งต้นคงไว้ (กันถดถอย)
            Assert.True(Assert.Single(NotificationAudiencePolicy.DefaultMatrix(key, companyLineGroupConfigured: true)).EnableLineGroup);
        }
        // คีย์อื่น (เช่น ลา/อนุมัติ) = ว่าง — พฤติกรรมเดิม "ไม่ตั้ง = ไม่ส่ง" ไม่เปลี่ยน
        Assert.Empty(NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.LeaveSubmitted, true));
        Assert.Empty(NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.SubscriptionExpired, true));
    }

    [Fact]
    public void Migrated_keys_exist_in_catalog()
    {
        var catalog = NotificationEvents.All.Select(e => e.Key).ToHashSet();
        foreach (var key in NotificationAudiencePolicy.MigratedBroadcastKeys) Assert.Contains(key, catalog);
        Assert.Contains(NotificationEvents.PaymentReceived, NotificationAudiencePolicy.MigratedBroadcastKeys);
        Assert.Contains(NotificationEvents.InvoiceOverdue, NotificationAudiencePolicy.MigratedBroadcastKeys);
        Assert.Contains(NotificationEvents.LodgingSlipUploaded, NotificationAudiencePolicy.MigratedBroadcastKeys);
    }

    [Theory]
    [InlineData(true, "tok", "Cabc", true)]
    [InlineData(true, "tok", null, false)]
    [InlineData(true, null, "Cabc", false)]
    [InlineData(false, "tok", "Cabc", false)]
    [InlineData(true, " ", "Cabc", false)]
    public void Company_line_group_configured_requires_enabled_token_and_group(bool enabled, string? token, string? group, bool expected)
        => Assert.Equal(expected, NotificationAudiencePolicy.CompanyLineGroupConfigured(enabled, token, group));

    [Fact]
    public void Company_group_notice_tells_user_no_fallback_to_central_oa()
    {
        Assert.Equal(NotificationAudiencePolicy.NoCompanyOaMessage, NotificationAudiencePolicy.CompanyGroupNotice(false, true));
        Assert.Contains("ยังไม่ได้ใส่", NotificationAudiencePolicy.CompanyGroupNotice(true, false));
        Assert.Contains("ส่งเข้ากลุ่ม LINE ของทีมได้", NotificationAudiencePolicy.CompanyGroupNotice(true, true));
    }

    // ───────── LineNotifyService: ไม่มี fallback จริง (HTTP capture) ─────────

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<(string? Auth, string Body)> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Headers.Authorization?.Parameter, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    /// <summary>DbContext = null โดยตั้งใจ = บริษัทไม่มี OA ของตัวเอง (ResolveCompanyOaAsync คืน None) · OA กลางมาจาก resolver ที่ฉีด</summary>
    private static LineNotifyService Line(CaptureHandler h, NextAccLineSettings central)
    {
        var factory = F4Proxy<IHttpClientFactory>.Create((m, a) => m == nameof(IHttpClientFactory.CreateClient) ? new HttpClient(h, disposeHandler: false) : null);
        var secrets = F4Proxy<ISecretProtector>.Create((m, a) => a.Length > 0 ? a[0] : null);
        var resolver = F4Proxy<INextAccLineConfig>.Create((m, a) => Task.FromResult(central));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new LineNotifyService(factory, cfg, NullLogger<LineNotifyService>.Instance, null!, secrets, resolver);
    }

    private static NextAccLineSettings Central(bool opsToGroup = true)
        => new("CENTRAL-TOKEN", "S", "@nextacc", null, "C" + new string('a', 32), NextAccLineConfigRule.SourceAdmin, null, opsToGroup);

    [Fact]
    public async Task Company_group_without_company_oa_is_denied_and_central_group_never_receives_it()
    {
        // บั๊กที่รายงาน: "✅ เอกสารอนุมัติแล้ว" ของผู้เช่าเด้งเข้ากลุ่มกลาง — ตอนนี้ต้องถูกกั้นพร้อมเหตุ และไม่มี HTTP ออกไปเลย
        var h = new CaptureHandler();
        var r = await Line(h, Central()).TryPushCompanyGroupAsync(Guid.NewGuid(), "✅ เอกสารอนุมัติแล้ว\n📄 PI-20261001-0005");
        Assert.False(r.Ok);
        Assert.Equal(NotificationAudiencePolicy.RuleTenantNoCompanyOa, r.RuleCode);
        Assert.Equal(NotificationAudiencePolicy.NoCompanyOaMessage, r.Error);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Company_flex_and_test_without_company_oa_are_denied_without_central_token()
    {
        var h = new CaptureHandler();
        var svc = Line(h, Central());
        var flex = await svc.TryPushFlexToUserAsync(Guid.NewGuid(), "Ucustomer", "สลิป", new { type = "bubble" });
        Assert.False(flex.Ok);
        Assert.Equal(NotificationAudiencePolicy.RuleTenantNoCompanyOa, flex.RuleCode);
        var (ok, msg) = await svc.TestConfigAsync(Guid.NewGuid(), "Uanyone");
        Assert.False(ok);
        Assert.Contains("OA ของร้าน", msg);
        Assert.Empty(h.Requests);   // เดิมปุ่มทดสอบยิงด้วย token กลาง ⇒ "สำเร็จ" ทั้งที่ร้านไม่มี OA
    }

    [Fact]
    public async Task Platform_ops_goes_to_central_group_with_central_token()
    {
        var h = new CaptureHandler();
        var r = await Line(h, Central()).TryPushPlatformOpsAsync("📩 มีข้อความติดต่อใหม่!");
        Assert.True(r.Ok);
        var req = Assert.Single(h.Requests);
        Assert.Equal("CENTRAL-TOKEN", req.Auth);
        Assert.Contains("\"to\":\"C" + new string('a', 32) + "\"", req.Body);
    }

    [Fact]
    public async Task Platform_ops_respects_admin_switch_and_missing_group()
    {
        var h = new CaptureHandler();
        var off = await Line(h, Central(opsToGroup: false)).TryPushPlatformOpsAsync("x");
        Assert.False(off.Ok);
        Assert.Contains("ปิด", off.Error);
        var noGroup = await Line(h, Central() with { GroupId = null }).TryPushPlatformOpsAsync("x");
        Assert.False(noGroup.Ok);
        Assert.Contains("Group ID", noGroup.Error);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Tenant_member_via_nextacc_still_pushes_with_central_token()
    {
        // ทิศตรงข้าม: เส้นพนักงาน/ลูกค้าที่ผูกกับบอทกลาง (รอบ 212 C/D) ไม่ถดถอย
        var h = new CaptureHandler();
        var r = await Line(h, Central()).TryPushViaNextAccAsync("Ustaff123456", new[] { "สลิปพร้อมแล้ว" });
        Assert.True(r.Ok);
        Assert.Equal("CENTRAL-TOKEN", Assert.Single(h.Requests).Auth);
        Assert.Contains("\"to\":\"Ustaff123456\"", h.Requests[0].Body);
    }

    [Fact]
    public void Push_result_denied_carries_rule_code()
    {
        var d = LinePushResult.Denied("R", "เหตุ");
        Assert.False(d.Ok);
        Assert.Equal("R", d.RuleCode);
        Assert.Null(LinePushResult.Fail("x").RuleCode);
        Assert.True(LinePushResult.Success.Ok);
    }
}

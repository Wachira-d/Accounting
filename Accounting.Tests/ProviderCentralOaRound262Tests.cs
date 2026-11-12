using System.Net;
using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Tests.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 262 (คำตัดสิน 262-1-1…262-1-8 · <c>erp-review/2026-11-12/team-262-provider-line-billing.md</c>) — บริษัทผู้ให้บริการ: LINE OA กลาง = OA ของบริษัทสำหรับผู้ติดต่อ
/// (pure · สองทิศ): ตัวหาผู้รับเปิด LINE ผู้ใช้ 1:1 ผ่าน OA กลางเฉพาะบริษัทผู้ให้บริการ · บริษัทอื่นคง N8 · กลุ่มกลางของแอดมินยังห้าม · บอทกลางเปลี่ยน ⇒ ผูกใหม่ ·
/// ด่านบันทึกกฎ · คำเชิญไถ่ข้าม OA ไม่ได้ · แจ้งค่าบริการ → trigger ของการ์ด · เจ้าของที่ได้การ์ดแล้วไม่ได้ข้อความตรงซ้ำ · ปุ่มชำระ = ศูนย์บิล · แถวความพร้อม/ประโยคจากเซิร์ฟเวอร์
/// </summary>
public class ProviderCentralOaRound262Tests
{
    private static readonly Guid Platform = Guid.Parse("26200000-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("26200000-0000-0000-0000-000000000002");
    private static readonly IReadOnlyCollection<string> AdminGroups = new[] { "Cadmin262000000000000000000000001" };
    private const string CentralBot = "Ucentral262000000000000000000001";

    private static ContactRecipientRule.EndpointFacts Ep(string kind, string? group = null, string? oaDestination = CentralBot)
        => new(Guid.NewGuid(), Platform, Guid.NewGuid(), kind, null, null, kind == NotifyEndpointKind.LineGroup ? group ?? "C262group000000000000000000000001" : null,
            NotifyEndpointKind.ScopeNextAcc, null, 5, NotifyTopics.Serialize(NotifyTopics.All), true, false, true, "line-invite",
            Guid.NewGuid(), null, null, null, null, false, new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc)) { OaDestination = oaDestination };

    private static ContactRecipientRule.LineOpenness Line(bool provider, string? centralBot = CentralBot)
        => new(true, CompanyOaReady: false, null, "ยังไม่ได้เปิดใช้ LINE OA ของบริษัท", NextAccOpen: false,
            NextAccGroupOpen: provider, NextAccUserOpen: provider, CentralOaDestination: centralBot);

    [Fact]
    public void บริษัทผู้ให้บริการ_LINE_ผู้ใช้_1ต่อ1_ผ่าน_OA_กลาง_ใช้ได้แม้ไม่มี_OA_บริษัท()
        => Assert.True(ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineUser), AdminGroups, Line(true)).Usable);

    [Fact]
    public void ทิศตรงข้าม_บริษัทอื่น_LINE_ผู้ใช้ผ่าน_OA_กลาง_ยังปิด_N8()
    {
        var st = ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineUser), AdminGroups, Line(false, null));
        Assert.False(st.Usable);
        Assert.Equal("line-not-open", st.Code);
        Assert.Equal(ContactRecipientRule.NextAccNotOpenReason, st.Reason);
        Assert.Equal(ContactRecipientRule.RuleCodeNextAccNotOpen, st.RuleCode);
    }

    [Fact]
    public void ทิศตรงข้าม_บอทของ_OA_กลางเปลี่ยน_ปลายทางเดิมต้องผูกใหม่_ไม่รู้บอท_ไม่ตัดสิน()
    {
        var changed = ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineUser), AdminGroups, Line(true, "Uother262000000000000000000000001"));
        Assert.False(changed.Usable);
        Assert.Equal("oa-changed", changed.Code);
        Assert.Equal(ContactRecipientRule.RuleCodeOaChanged, changed.RuleCode);
        Assert.True(ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineUser), AdminGroups, Line(true, null)).Usable);
        Assert.True(ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineUser, oaDestination: null), AdminGroups, Line(true)).Usable);
    }

    [Fact]
    public void ทิศตรงข้าม_กลุ่มกลางของแอดมินยังห้าม_แม้บริษัทผู้ให้บริการเปิดทุกช่อง()
    {
        var st = ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineGroup, AdminGroups.First()), AdminGroups, Line(true));
        Assert.False(st.Usable);
        Assert.Equal(ContactRecipientRule.RuleCodePlatformGroup, st.RuleCode);
    }

    [Fact]
    public void ทิศตรงข้าม_ปลายทาง_OA_ของบริษัท_ไม่ได้รับผลจากธง_OA_กลาง()
    {
        var company = Ep(NotifyEndpointKind.LineUser) with { OaScope = NotifyEndpointKind.ScopeCompany };
        var st = ContactRecipientRule.Status(company, AdminGroups, Line(true));
        Assert.False(st.Usable);   // บริษัทผู้ให้บริการไม่มี OA ของตัวเอง ⇒ ปลายทาง scope company ไม่ส่ง (ไม่ไหลไป OA กลาง)
        Assert.Equal(ContactRecipientRule.RuleCodeCompanyOaNotReady, st.RuleCode);
    }

    [Fact]
    public void บันทึกกฎ_บริษัทผู้ให้บริการติ๊ก_LINE_ผู้ใช้ได้_ทิศตรงข้าม_บริษัทอื่น_หรือกลุ่มไม่เปิด_ปฏิเสธ()
    {
        Assert.Null(ContactLineChannel.RuleProblem("DocumentDueSoon", false, lineUser: true, lineGroup: false, companyOaReady: false, null, centralUserOpen: true));
        Assert.Null(ContactLineChannel.RuleProblem("DocumentDueSoon", false, lineUser: true, lineGroup: true, companyOaReady: false, null,
            centralGroupOpen: true, centralUserOpen: true));
        Assert.NotNull(ContactLineChannel.RuleProblem("DocumentDueSoon", false, lineUser: true, lineGroup: false, companyOaReady: false, null));
        Assert.NotNull(ContactLineChannel.RuleProblem("DocumentDueSoon", false, lineUser: true, lineGroup: true, companyOaReady: false, null, centralUserOpen: true));
        // สลิปเงินเดือนยังห้ามเข้าช่อง LINE ของผู้ติดต่อเสมอ
        Assert.NotNull(ContactLineChannel.RuleProblem("PayrollPaid", false, lineUser: true, lineGroup: false, companyOaReady: false, null, centralUserOpen: true));
    }

    [Theory]
    [InlineData(null, NotifyEndpointKind.ScopeCompany, true)]
    [InlineData("company", NotifyEndpointKind.ScopeCompany, true)]
    [InlineData("nextacc", NotifyEndpointKind.ScopeNextAcc, true)]
    [InlineData("company", NotifyEndpointKind.ScopeNextAcc, false)]   // คำเชิญ OA บริษัท ไถ่ผ่าน webhook กลางไม่ได้
    [InlineData("nextacc", NotifyEndpointKind.ScopeCompany, false)]   // คำเชิญ OA กลาง ไถ่ผ่าน webhook บริษัทไม่ได้
    [InlineData(null, NotifyEndpointKind.ScopeNextAcc, false)]
    public void คำเชิญไถ่ได้เฉพาะ_OA_เดียวกัน(string? inviteScope, string webhookScope, bool expected)
        => Assert.Equal(expected, ProviderCentralOa.InviteMatchesChannel(inviteScope, webhookScope));

    [Fact]
    public void ขอบเขตคำเชิญตามบริษัท_และด่านเดียวกับกลุ่ม()
    {
        Assert.Equal(NotifyEndpointKind.ScopeNextAcc, ProviderCentralOa.InviteScope(true));
        Assert.Equal(NotifyEndpointKind.ScopeCompany, ProviderCentralOa.InviteScope(false));
        Assert.True(ProviderCentralOa.Allowed(Platform, Platform));
        Assert.False(ProviderCentralOa.Allowed(Other, Platform));
        Assert.False(ProviderCentralOa.Allowed(Platform, null));
    }

    [Fact]
    public void แจ้งค่าบริการ_เป็น_trigger_ของการ์ด_ผลตัดบัตร_ไม่มีการ์ด()
    {
        var inv = Guid.NewGuid();
        Assert.Equal("DocumentApproved", ProviderCentralOa.TriggerForKind(PlatformDunningPolicy.KindOfKey(PlatformDunningPolicy.Key(inv, PlatformDunningPolicy.KindIssued, 0))));
        Assert.Equal("DocumentDueSoon", ProviderCentralOa.TriggerForKind(PlatformDunningPolicy.KindOfKey(PlatformDunningPolicy.Key(inv, PlatformDunningPolicy.KindBefore, 3))));
        Assert.Equal("DocumentDueSoon", ProviderCentralOa.TriggerForKind(PlatformDunningPolicy.KindOfKey(PlatformDunningPolicy.Key(inv, PlatformDunningPolicy.KindDue, 0))));
        Assert.Equal("DocumentOverdue", ProviderCentralOa.TriggerForKind(PlatformDunningPolicy.KindOfKey(PlatformDunningPolicy.Key(inv, PlatformDunningPolicy.KindAfter, 7))));
        Assert.Null(PlatformDunningPolicy.KindOfKey(PlatformAutoChargePolicy.NoticeKey(inv, 1, "failed")));
        Assert.Null(PlatformDunningPolicy.KindOfKey("co:" + inv.ToString("N") + ":suspend:20261112"));
        Assert.Null(PlatformDunningPolicy.KindOfKey(null));
        Assert.Null(ProviderCentralOa.TriggerForKind(null));
    }

    [Fact]
    public void กุญแจคิว_ต่อใบต่อขั้น_และหน้าต่างกันซ้ำ()
    {
        var inv = Guid.NewGuid();
        var a = ProviderCentralOa.QueueBaseKey(inv, "DocumentDueSoon", PlatformDunningPolicy.Key(inv, PlatformDunningPolicy.KindBefore, 7));
        var b = ProviderCentralOa.QueueBaseKey(inv, "DocumentDueSoon", PlatformDunningPolicy.Key(inv, PlatformDunningPolicy.KindBefore, 3));
        Assert.NotEqual(a, b);
        Assert.True(ContactLineChannel.QueueKey(a, Guid.NewGuid()).Length <= 300);   // IdempotencyKey varchar(300)
        Assert.True(a.Length <= 250);   // BaseKey varchar(250)
        Assert.Null(ProviderCentralOa.DuplicateWindow("DocumentApproved"));
        Assert.Null(ProviderCentralOa.DuplicateWindow(ContactLineChannel.ReceiptTrigger));
        Assert.Equal(TimeSpan.FromHours(20), ProviderCentralOa.DuplicateWindow("DocumentOverdue"));
    }

    private sealed record Owner(string Name, string? LineUserId);

    [Fact]
    public void เจ้าของที่ได้การ์ดทางคิวแล้ว_ไม่ได้ข้อความตรงซ้ำ_คนอื่นยังได้()
    {
        var owners = new[] { new Owner("A", "U262a"), new Owner("B", " U262b "), new Owner("C", null) };
        var left = ProviderCentralOa.OwnersNotCovered(owners, o => o.LineUserId, new[] { "U262b" });
        Assert.Equal(new[] { "A", "C" }, left.Select(o => o.Name));
        Assert.Equal(3, ProviderCentralOa.OwnersNotCovered(owners, o => o.LineUserId, Array.Empty<string>()).Count);
    }

    [Fact]
    public void ปุ่มชำระของใบค่าบริการ_ไปศูนย์บิล_ไม่ติ๊ก_ไม่มีปุ่ม()
    {
        const string url = "https://app.nextacc.test/pages/billing.html";
        Assert.Equal(url, ProviderCentralOa.BillingPayUrl(null, url));
        Assert.Equal(url, ProviderCentralOa.BillingPayUrl(true, url));
        Assert.Null(ProviderCentralOa.BillingPayUrl(false, url));
        Assert.Null(ProviderCentralOa.BillingPayUrl(null, null));
    }

    [Fact]
    public void ประโยคแท็บ_LINE_บริษัทผู้ให้บริการ_ไม่อ้างส่วนที่ซ่อน_ทิศตรงข้าม_บริษัทอื่นคงเดิม()
    {
        var p = ProviderCentralOa.ContactSendNotice(true, "@810pamnc");
        Assert.DoesNotContain("ตั้งค่าขั้นสูง", p);
        Assert.Contains("LINE OA กลาง", p);
        Assert.Contains("@810pamnc", p);
        Assert.Contains("ศูนย์บิล", p);
        Assert.Contains("ตั้งค่าขั้นสูง", ProviderCentralOa.ContactSendNotice(false, "@810pamnc"));
        Assert.DoesNotContain("@810pamnc", ProviderCentralOa.ContactSendNotice(false, "@810pamnc"));
    }

    [Fact]
    public void แถวความพร้อม_เฉพาะบริษัทผู้ให้บริการ_บอกเหตุตามข้อเท็จจริง()
    {
        var ok = new ProviderCentralOa.ReadinessFacts(true, true, null, "@810pamnc", 3, 10, true, true);
        Assert.Empty(ProviderCentralOa.ReadinessRows(false, ok));
        var rows = ProviderCentralOa.ReadinessRows(true, ok);
        Assert.Equal(new[] { "central-oa", "central-basic-id", "central-webhook", "bound", "billing-line" }, rows.Select(r => r.Code));
        Assert.All(rows, r => Assert.Equal(LineOaHealth.LevelOk, r.Level));
        var bad = ProviderCentralOa.ReadinessRows(true, ok with { CentralHasSecret = false, CentralBasicId = null, BoundContacts = 0, BillingLineEnabled = false });
        Assert.Equal(LineOaHealth.LevelError, bad.Single(r => r.Code == "central-oa").Level);
        Assert.Equal(ProviderCentralOa.CentralNoSecret, bad.Single(r => r.Code == "central-oa").Detail);
        Assert.Equal(LineOaHealth.LevelWarn, bad.Single(r => r.Code == "central-basic-id").Level);
        Assert.Equal(LineOaHealth.LevelWarn, bad.Single(r => r.Code == "bound").Level);
        Assert.Equal(LineOaHealth.LevelWarn, bad.Single(r => r.Code == "billing-line").Level);
        Assert.Equal(LineGroupBindRule.Replies.CentralOaNotConfigured, ProviderCentralOa.CentralProblem(false, false, null));
        Assert.Equal("ถอดรหัสไม่ได้", ProviderCentralOa.CentralProblem(false, true, "ถอดรหัสไม่ได้"));
        Assert.Null(ProviderCentralOa.CentralProblem(true, true, null));
    }

    [Fact]
    public void กล่องศูนย์บิล_บอกเหตุทุกทางตัน()
    {
        Assert.False(ProviderCentralOa.BillingView(false, false, null, 0, true).Available);
        Assert.Contains("ใบแจ้งหนี้ใบแรก", ProviderCentralOa.BillingView(true, false, null, 0, true).Reason);
        Assert.Equal(ProviderCentralOa.CentralNoSecret, ProviderCentralOa.BillingView(true, true, ProviderCentralOa.CentralNoSecret, 0, true).Reason);
        Assert.False(ProviderCentralOa.BillingView(true, true, null, 0, canManage: false).Available);
        var ok = ProviderCentralOa.BillingView(true, true, null, 2, true);
        Assert.True(ok.Available);
        Assert.Null(ok.Reason);
        Assert.Equal(2, ok.BoundCount);
    }

    [Fact]
    public void ประโยค_OA_กลาง_สี่กรณี_และป้ายกฎ()
    {
        Assert.Contains("LINE ผู้ใช้ 1:1 และกลุ่ม", ContactLineChannel.CentralOaSentence(true, true));
        Assert.Contains("ยังไม่เปิด", ContactLineChannel.CentralOaSentence(true, false));
        Assert.Contains("ยังไม่เปิด", ContactLineChannel.CentralOaSentence(false, false));
        Assert.Contains("เปิดแล้วสำหรับบริษัทนี้", ContactRecipientResolver.DeliveryNoteFor(true));
        Assert.Contains("LINE OA กลางของ NextAcc", ContactLineChannel.ChannelLabels(true).LineUser);
        Assert.DoesNotContain("LINE OA กลาง", ContactLineChannel.ChannelLabels(false).LineUser);
    }

    private sealed class Capture : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task ตอนส่ง_ไม่รู้บริษัทผู้ให้บริการ_ปฏิเสธ_LINE_ผู้ใช้ผ่าน_OA_กลาง_ไม่ยิง_HTTP()
    {
        var h = new Capture();
        var factory = F4Proxy<IHttpClientFactory>.Create((m, a) => m == nameof(IHttpClientFactory.CreateClient) ? new HttpClient(h, disposeHandler: false) : null);
        var secrets = F4Proxy<ISecretProtector>.Create((m, a) => a.Length > 0 ? a[0] : null);
        var central = F4Proxy<INextAccLineConfig>.Create((m, a) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
            ? (object)NextAccLineConfigRule.PlatformGroupIds(AdminGroups.First(), null)
            : new NextAccLineSettings("CENTRAL-TOKEN", "S", "@810pamnc", CentralBot, AdminGroups.First(), NextAccLineConfigRule.SourceAdmin));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var svc = new LineNotifyService(factory, cfg, NullLogger<LineNotifyService>.Instance, null!, secrets, central);

        var r = await svc.TryPushContactAsync(Other, NotifyEndpointKind.ScopeNextAcc, LineTargetKind.TenantMember, "U262user0000000000000000000000001",
            "alt", new { type = "bubble" }, Guid.NewGuid());

        Assert.False(r.Ok);
        Assert.Equal(ContactRecipientRule.RuleCodeNextAccNotOpen, r.RuleCode);
        Assert.Equal(ContactRecipientRule.NextAccNotOpenReason, r.Error);
        Assert.Equal(0, h.Calls);
    }
}

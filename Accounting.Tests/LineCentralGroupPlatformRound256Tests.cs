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
/// รอบ 256 (คำตัดสินเจ้าของ 256-2 · 2026-11-09) — กลุ่ม LINE ของผู้ติดต่อผ่าน LINE OA กลาง <b>เฉพาะบริษัทผู้ให้บริการ</b> (pure · สองทิศ):
/// ด่านเดียว <see cref="LineGroupBindRule.CentralScopeAllowed"/> · ตัวหาผู้รับ (<c>ContactRecipientRule.LineGate</c>) เปิดเฉพาะกลุ่ม · ด่านบันทึกกฎ ·
/// ตอนส่ง: ไม่รู้บริษัทผู้ให้บริการ ⇒ ปฏิเสธพร้อมเหตุโดยไม่ยิง HTTP · บริษัทอื่นคง N8
/// </summary>
public class LineCentralGroupPlatformRound256Tests
{
    private static readonly Guid Platform = Guid.Parse("25600000-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("25600000-0000-0000-0000-000000000002");
    private static readonly IReadOnlyCollection<string> AdminGroups = new[] { "Cadmin256000000000000000000000001" };

    [Fact]
    public void ด่านเดียว_เฉพาะบริษัทผู้ให้บริการ_ทิศตรงข้าม_บริษัทอื่น_ไม่ได้ตั้ง_GuidEmpty()
    {
        Assert.True(LineGroupBindRule.CentralScopeAllowed(Platform, Platform));
        Assert.False(LineGroupBindRule.CentralScopeAllowed(Other, Platform));
        Assert.False(LineGroupBindRule.CentralScopeAllowed(Platform, null));
        Assert.False(LineGroupBindRule.CentralScopeAllowed(Guid.Empty, Guid.Empty));
        Assert.False(LineGroupBindRule.CentralScopeAllowed(Platform, Guid.Empty));
    }

    [Theory]
    [InlineData("message", "#HeyNextAcc", true)]
    [InlineData("message", "#ByeNextAcc", true)]
    [InlineData("leave", null, true)]
    [InlineData("memberJoined", null, true)]
    [InlineData("memberLeft", null, true)]
    [InlineData("message", "ส่งใบเสนอราคาให้หน่อย", false)]   // คุยกันในกลุ่ม ⇒ ไม่แตะฐาน (ฝ่ายค้าน M2)
    [InlineData("message", null, false)]
    [InlineData("join", null, false)]
    [InlineData("unsend", null, false)]
    public void event_กลุ่มของ_OA_กลางที่ต้องทำอะไร_สองทิศ(string type, string? text, bool expected)
        => Assert.Equal(expected, LineGroupBindRule.CentralGroupEventActionable(type, text));

    [Fact]
    public void ยอดใช้_OA_กลาง_แสดง_group_push_และยังไม่อ่าน_push()
    {
        var note = NextAccOaQuotaRule.UsageSummary("2026-11", null, null, 12, 3);
        Assert.Contains("ยังไม่ได้อ่านจาก LINE", note);
        Assert.Contains("บริษัทผู้ให้บริการ 3", note);
        Assert.Contains("/ 1,000", NextAccOaQuotaRule.UsageSummary("2026-11", 10, 1000, 0, 0));
    }

    private static ContactRecipientRule.EndpointFacts Ep(string kind, string groupOrNull = "C256group000000000000000000000001")
        => new(Guid.NewGuid(), Platform, Guid.NewGuid(), kind, null, null, kind == NotifyEndpointKind.LineGroup ? groupOrNull : null,
            NotifyEndpointKind.ScopeNextAcc, null, 5, NotifyTopics.Serialize(NotifyTopics.All), true, false, true, LineGroupBindRule.SourceGroupCode,
            Guid.NewGuid(), null, null, null, null, false, new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc)) { OaDestination = "Ucentral256" };

    private static ContactRecipientRule.LineOpenness Line(bool centralGroups)
        => new(true, CompanyOaReady: false, null, "ยังไม่ได้เปิดใช้ LINE OA ของบริษัท", NextAccOpen: false, NextAccGroupOpen: centralGroups);

    [Fact]
    public void บริษัทผู้ให้บริการ_กลุ่มผ่าน_OA_กลาง_ใช้ได้แม้ไม่มี_OA_บริษัท()
        => Assert.True(ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineGroup), AdminGroups, Line(true)).Usable);

    [Fact]
    public void ทิศตรงข้าม_ไม่ใช่บริษัทผู้ให้บริการแล้ว_กลุ่มเดิมไม่ส่งพร้อมเหตุ()
    {
        var st = ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineGroup), AdminGroups, Line(false));
        Assert.False(st.Usable);
        Assert.Equal("line-not-open", st.Code);
        Assert.Equal(ContactRecipientRule.RuleCodeNextAccNotOpen, st.RuleCode);
        Assert.Equal(ContactRecipientRule.CentralGroupNotPlatformReason, st.Reason);
    }

    [Fact]
    public void ทิศตรงข้าม_LINE_ผู้ใช้_1ต่อ1_ผ่าน_OA_กลาง_ยังปิดแม้เป็นบริษัทผู้ให้บริการ_N8()
    {
        var st = ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineUser), AdminGroups, Line(true));
        Assert.False(st.Usable);
        Assert.Equal(ContactRecipientRule.NextAccNotOpenReason, st.Reason);
    }

    [Fact]
    public void ทิศตรงข้าม_กลุ่มกลางของแอดมิน_ไม่ใช้ได้แม้เปิดกลุ่มผ่าน_OA_กลาง()
    {
        var st = ContactRecipientRule.Status(Ep(NotifyEndpointKind.LineGroup, AdminGroups.First()), AdminGroups, Line(true));
        Assert.False(st.Usable);
        Assert.Equal(ContactRecipientRule.RuleCodePlatformGroup, st.RuleCode);
    }

    [Fact]
    public void บันทึกกฎ_บริษัทผู้ให้บริการติ๊กกลุ่มอย่างเดียวได้_ทิศตรงข้าม_ติ๊ก_LINE_ผู้ใช้_หรือไม่ใช่บริษัทผู้ให้บริการ_ปฏิเสธ()
    {
        Assert.Null(ContactLineChannel.RuleProblem("DocumentDueSoon", false, lineUser: false, lineGroup: true, companyOaReady: false, null, centralGroupOpen: true));
        Assert.NotNull(ContactLineChannel.RuleProblem("DocumentDueSoon", false, lineUser: true, lineGroup: true, companyOaReady: false, null, centralGroupOpen: true));
        Assert.NotNull(ContactLineChannel.RuleProblem("DocumentDueSoon", false, lineUser: false, lineGroup: true, companyOaReady: false, null, centralGroupOpen: false));
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
    public async Task ตอนส่ง_ไม่รู้บริษัทผู้ให้บริการ_ปฏิเสธกลุ่มผ่าน_OA_กลาง_ไม่ยิง_HTTP()
    {
        var h = new Capture();
        var factory = F4Proxy<IHttpClientFactory>.Create((m, a) => m == nameof(IHttpClientFactory.CreateClient) ? new HttpClient(h, disposeHandler: false) : null);
        var secrets = F4Proxy<ISecretProtector>.Create((m, a) => a.Length > 0 ? a[0] : null);
        var resolver = F4Proxy<INextAccLineConfig>.Create((m, a) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
            ? (object)NextAccLineConfigRule.PlatformGroupIds(AdminGroups.First(), null)
            : new NextAccLineSettings("CENTRAL-TOKEN", "S", "@810pamnc", null, AdminGroups.First(), NextAccLineConfigRule.SourceAdmin));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var svc = new LineNotifyService(factory, cfg, NullLogger<LineNotifyService>.Instance, null!, secrets, resolver);

        var r = await svc.TryPushContactAsync(Other, NotifyEndpointKind.ScopeNextAcc, LineTargetKind.ContactGroup, "C256group000000000000000000000001",
            "alt", new { type = "bubble" }, Guid.NewGuid());

        Assert.False(r.Ok);
        Assert.Equal(ContactRecipientRule.RuleCodeNextAccNotOpen, r.RuleCode);
        Assert.Equal(ContactRecipientRule.CentralGroupNotPlatformReason, r.Error);
        Assert.Equal(0, h.Calls);
    }
}

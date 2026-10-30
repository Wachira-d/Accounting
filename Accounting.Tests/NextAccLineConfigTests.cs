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
/// รอบ 212 แพ็ก D — ค่าตั้ง LINE OA กลางของ NextAcc บนหน้าแอดมิน: ลำดับ "หน้าแอดมินก่อน · config Line:* fallback" (<see cref="NextAccLineConfigRule.Merge"/>) ·
/// ความหมาย ว่าง/ล้าง ของช่องลับและไม่ลับ · ตัวตรวจรูปแบบ Basic ID / Bot User ID / Group ID · resolver ตัวเดียว (<see cref="NextAccLineConfig"/>) ·
/// ผู้รับสลิปเงินเดือน (<see cref="NextAccLineConfigRule.PayslipRecipient"/>) + Flex ผ่าน OA กลาง (<see cref="LineNotifyService.TryPushFlexViaNextAccAsync"/>) —
/// ล็อกทั้งทิศใหม่และทิศตรงข้าม (deployment เดิมที่ตั้ง env ยังทำงาน · LINE ID ที่ HR กรอกเองยังใช้เส้นเดิม)
/// </summary>
public class NextAccLineConfigTests
{
    private const string Bot = "U0123456789abcdef0123456789abcdef";
    private const string Group = "C0123456789abcdef0123456789abcdef";

    private static NextAccLineValues V(string? token, string? secret = null, string? basic = null, string? bot = null, string? group = null)
        => new(token, secret, basic, bot, group);

    private static IConfiguration Cfg(string? token, string? secret = null, string? basic = null)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Line:ChannelAccessToken"] = token, ["Line:ChannelSecret"] = secret, ["Line:BasicId"] = basic,
        }).Build();

    // ───────── Merge: ลำดับ ─────────

    [Fact]
    public void Admin_token_wins_as_a_whole_set_without_mixing_config_secret()
    {
        var r = NextAccLineConfigRule.Merge(V("ADMIN-TOKEN", null, "@adminoa"), V("CFG-TOKEN", "CFG-SECRET", "@cfgoa", Bot, Group));
        Assert.Equal(NextAccLineConfigRule.SourceAdmin, r.Source);
        Assert.Equal("ADMIN-TOKEN", r.AccessToken);
        Assert.Null(r.ChannelSecret);   // ไม่ยืม secret ของอีก channel (ลายเซ็นไม่มีวันผ่าน)
        Assert.Null(r.BotUserId);
        Assert.Equal("@adminoa", r.BasicId);
        Assert.False(r.HasSecret);
    }

    [Fact]
    public void No_admin_token_falls_back_to_config_entirely_so_env_deployments_keep_working()
    {
        var r = NextAccLineConfigRule.Merge(V(null, "ADMIN-SECRET", "@adminoa"), V("CFG-TOKEN", "CFG-SECRET", "cfgoa", Bot, Group));
        Assert.Equal(NextAccLineConfigRule.SourceConfig, r.Source);
        Assert.Equal("CFG-TOKEN", r.AccessToken);
        Assert.Equal("CFG-SECRET", r.ChannelSecret);
        Assert.Equal("@cfgoa", r.BasicId);   // normalize เติม @
        Assert.Equal(Bot, r.BotUserId);
        Assert.Equal(Group, r.GroupId);
    }

    [Fact]
    public void No_admin_row_at_all_uses_config()
    {
        var r = NextAccLineConfigRule.Merge(null, V("CFG-TOKEN", "S"));
        Assert.Equal(NextAccLineConfigRule.SourceConfig, r.Source);
        Assert.True(r.HasToken);
    }

    [Fact]
    public void Neither_has_token_reports_none_and_keeps_admin_values_visible()
    {
        var r = NextAccLineConfigRule.Merge(V("  ", null, "@adminoa"), V(null, "CFG-SECRET"));
        Assert.Equal(NextAccLineConfigRule.SourceNone, r.Source);
        Assert.False(r.HasToken);
        Assert.Equal("@adminoa", r.BasicId);
        var none = NextAccLineConfigRule.Merge(null, null);
        Assert.False(none.HasToken);
        Assert.Equal(NextAccLineConfigRule.SourceNone, none.Source);
    }

    [Fact]
    public void Undecryptable_admin_token_does_not_fall_back_to_config_set()
    {
        // ฝ่ายค้าน (B+D) ข้อ 3: token ในหน้าแอดมินถอดไม่ได้ ⇒ ไม่สลับไปใช้ config (อาจเป็นคนละ OA) ⇒ ไม่มี token + เหตุชัด
        var r = NextAccLineConfigRule.Merge(V(null, "S", "@adminoa", Bot), V("CFG-TOKEN", "CFG-SECRET", "@cfgoa"), adminTokenUndecryptable: true);
        Assert.False(r.HasToken);
        Assert.Equal(NextAccLineConfigRule.SourceNone, r.Source);
        Assert.Equal(NextAccLineConfigRule.UndecryptableTokenMessage, r.Problem);
        Assert.Null(r.ChannelSecret);
        Assert.Equal("@adminoa", r.BasicId);
        // ทิศตรงข้าม: ไม่มี token ในหน้าแอดมินเลย ⇒ ใช้ config ตามเดิม (deployment ที่ตั้งผ่าน env)
        var cfg = NextAccLineConfigRule.Merge(V(null, null, "@adminoa"), V("CFG-TOKEN", "CFG-SECRET"));
        Assert.Equal(NextAccLineConfigRule.SourceConfig, cfg.Source);
        Assert.Null(cfg.Problem);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void Admin_token_and_secret_must_be_saved_as_a_pair(bool token, bool secret, bool problem)
        => Assert.Equal(problem, NextAccLineConfigRule.PairProblem(token, secret) != null);

    [Fact]
    public void Invalid_basic_id_in_storage_is_not_used()
        => Assert.Null(NextAccLineConfigRule.Merge(V("T", "S", "@bad id!"), null).BasicId);

    // ───────── ช่องลับ / ไม่ลับ: ว่าง = คง · ล้างต้องสั่งชัด ─────────

    [Fact]
    public void Secret_blank_keeps_existing_value()
    {
        Assert.Equal("ENC-OLD", NextAccLineConfigRule.ApplySecret("ENC-OLD", null, clear: false, v => "ENC:" + v));
        Assert.Equal("ENC-OLD", NextAccLineConfigRule.ApplySecret("ENC-OLD", "   ", clear: false, v => "ENC:" + v));
    }

    [Fact]
    public void Secret_clear_requires_explicit_flag()
    {
        Assert.Null(NextAccLineConfigRule.ApplySecret("ENC-OLD", "", clear: true, v => "ENC:" + v));
        // พิมพ์ค่าใหม่ + ติ๊กล้าง ⇒ ค่าใหม่ชนะ (ไม่ทิ้งสิ่งที่ผู้ใช้เพิ่งพิมพ์)
        Assert.Equal("ENC:new", NextAccLineConfigRule.ApplySecret("ENC-OLD", " new ", clear: true, v => "ENC:" + v));
    }

    [Fact]
    public void Secret_round_trips_through_secret_protector_into_resolved_settings()
    {
        var protector = new SecretProtector("unit-test-key-212d");
        var stored = NextAccLineConfigRule.ApplySecret(null, "LONG-LIVED-TOKEN", false, v => protector.Protect(v));
        Assert.NotEqual("LONG-LIVED-TOKEN", stored);   // เก็บแบบเข้ารหัส
        var r = NextAccLineConfigRule.Merge(V(protector.Unprotect(stored)), V("CFG"));
        Assert.Equal("LONG-LIVED-TOKEN", r.AccessToken);
        Assert.Equal(NextAccLineConfigRule.SourceAdmin, r.Source);
        // คีย์เปลี่ยน ⇒ ถอดไม่ออก ⇒ ไม่เอาค่าว่างมาใช้ — ถอยไป config (หน้าแอดมินแสดงคำเตือนให้กรอกใหม่)
        var other = new SecretProtector("another-key");
        Assert.False(other.IsUsable(stored));
        Assert.Equal(NextAccLineConfigRule.SourceConfig, NextAccLineConfigRule.Merge(V(other.Unprotect(stored)), V("CFG")).Source);
    }

    [Fact]
    public void Text_null_keeps_empty_clears()
    {
        Assert.Equal("@old", NextAccLineConfigRule.ApplyText("@old", null, provided: false));
        Assert.Null(NextAccLineConfigRule.ApplyText("@old", null, provided: true));
        Assert.Equal("@new", NextAccLineConfigRule.ApplyText("@old", "@new", provided: true));
    }

    // ───────── ตัวตรวจรูปแบบ ─────────

    [Theory]
    [InlineData("@123abcde", "@123abcde")]
    [InlineData("123abcde", "@123abcde")]
    [InlineData(" @nextacc.th ", "@nextacc.th")]
    public void Basic_id_valid_is_normalized(string input, string expected)
    {
        Assert.Null(NextAccLineConfigRule.ValidateBasicId(input, out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("@bad id")]
    [InlineData("@ก")]
    [InlineData("https://line.me/R/ti/p/@abc")]
    public void Basic_id_invalid_is_rejected(string input)
        => Assert.NotNull(NextAccLineConfigRule.ValidateBasicId(input, out _));

    [Fact]
    public void Blank_inputs_mean_clear_and_are_valid()
    {
        Assert.Null(NextAccLineConfigRule.ValidateBasicId("", out var b)); Assert.Null(b);
        Assert.Null(NextAccLineConfigRule.ValidateBotUserId(" ", out var u)); Assert.Null(u);
        Assert.Null(NextAccLineConfigRule.ValidateGroupId(null, out var g)); Assert.Null(g);
    }

    [Fact]
    public void Bot_user_id_and_group_id_formats()
    {
        Assert.Null(NextAccLineConfigRule.ValidateBotUserId(Bot, out _));
        Assert.NotNull(NextAccLineConfigRule.ValidateBotUserId("U0123456789ABCDEF0123456789ABCDEF", out _));
        Assert.NotNull(NextAccLineConfigRule.ValidateBotUserId("@nextacc", out _));
        Assert.Null(NextAccLineConfigRule.ValidateGroupId(Group, out _));
        Assert.Null(NextAccLineConfigRule.ValidateGroupId("R0123456789abcdef0123456789abcdef", out _));
        Assert.NotNull(NextAccLineConfigRule.ValidateGroupId(Bot, out _));
    }

    [Fact]
    public void Webhook_url_from_base_url()
    {
        Assert.Equal("https://app.nextacc.com/api/line-webhook", NextAccLineConfigRule.WebhookUrl("https://app.nextacc.com/"));
        Assert.EndsWith("/api/line-webhook", NextAccLineConfigRule.WebhookUrl(null));
    }

    // ───────── resolver ─────────

    [Fact]
    public async Task Resolver_without_db_reads_config_only()
    {
        var r = await new NextAccLineConfig(null, Cfg("CFG-TOKEN", "CFG-SECRET", "@cfg"), new SecretProtector("k")).GetAsync();
        Assert.Equal(NextAccLineConfigRule.SourceConfig, r.Source);
        Assert.Equal("CFG-SECRET", r.ChannelSecret);
        Assert.Equal("@cfg", r.BasicId);
    }

    [Fact]
    public async Task Resolver_with_nothing_configured_says_not_configured()
    {
        var r = await new NextAccLineConfig(null, Cfg(null), new SecretProtector("k")).GetAsync();
        Assert.False(r.HasToken);
        Assert.Contains("LINE OA กลาง", NextAccLineConfigRule.NotConfiguredMessage);
    }

    // ───────── ผู้รับสลิปเงินเดือน: OA ที่ผูกจริง ─────────

    [Fact]
    public void Payslip_recipient_bound_via_nextacc_goes_through_nextacc()
    {
        var r = NextAccLineConfigRule.PayslipRecipient("Uemp", employeeLineIdBoundViaNextAcc: true, userLineId: "Uuser");
        Assert.NotNull(r);
        Assert.Equal("Uemp", r!.LineUserId);   // Employee.LineId มาก่อน User.LineUserId
        Assert.True(r.ViaNextAcc);
    }

    [Fact]
    public void Payslip_recipient_typed_by_hr_keeps_legacy_company_route()
    {
        var r = NextAccLineConfigRule.PayslipRecipient("Uhr", employeeLineIdBoundViaNextAcc: false, userLineId: null);
        Assert.Equal("Uhr", r!.LineUserId);
        Assert.False(r.ViaNextAcc);
    }

    [Fact]
    public void Payslip_recipient_proven_user_binding_beats_hr_typed_id()
    {
        // ฝ่ายค้าน (B+D) ข้อ 6: ค่าที่ HR พิมพ์เอง (ไม่รู้ OA) แพ้การผูกที่พิสูจน์ได้กับ OA กลาง
        var r = NextAccLineConfigRule.PayslipRecipient("Uhr", employeeLineIdBoundViaNextAcc: false, userLineId: "Uuser");
        Assert.Equal("Uuser", r!.LineUserId);
        Assert.True(r.ViaNextAcc);
    }

    [Fact]
    public void Payslip_recipient_from_user_binding_is_nextacc_and_none_is_null()
    {
        var r = NextAccLineConfigRule.PayslipRecipient(" ", false, "Uuser");
        Assert.Equal("Uuser", r!.LineUserId);
        Assert.True(r.ViaNextAcc);
        Assert.Null(NextAccLineConfigRule.PayslipRecipient(null, false, null));
    }

    // ───────── LineNotifyService ผ่าน resolver ─────────

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<(string? Auth, string Body)> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Headers.Authorization?.Parameter, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    /// <summary>DbContext = null โดยตั้งใจ — เส้น OA กลางต้องไม่แตะค่าตั้งของบริษัท</summary>
    private static LineNotifyService Line(CaptureHandler h, string? configToken, INextAccLineConfig? resolver = null)
    {
        var factory = F4Proxy<IHttpClientFactory>.Create((m, a) => m == nameof(IHttpClientFactory.CreateClient) ? new HttpClient(h, disposeHandler: false) : null);
        var secrets = F4Proxy<ISecretProtector>.Create((m, a) => a.Length > 0 ? a[0] : null);
        return new LineNotifyService(factory, Cfg(configToken), NullLogger<LineNotifyService>.Instance, null!, secrets, resolver);
    }

    [Fact]
    public async Task Flex_via_nextacc_uses_admin_token_over_config()
    {
        var h = new CaptureHandler();
        var admin = F4Proxy<INextAccLineConfig>.Create((m, a) => Task.FromResult(
            new NextAccLineSettings("ADMIN-TOKEN", "S", "@nextacc", null, null, NextAccLineConfigRule.SourceAdmin)));
        var r = await Line(h, "CFG-TOKEN", admin).TryPushFlexViaNextAccAsync("Ustaff123456", "สลิปเงินเดือน", new { type = "bubble" });
        Assert.True(r.Ok);
        var req = Assert.Single(h.Requests);
        Assert.Equal("ADMIN-TOKEN", req.Auth);
        Assert.Contains("\"type\":\"flex\"", req.Body);
        Assert.Contains("\"to\":\"Ustaff123456\"", req.Body);
    }

    [Fact]
    public async Task Flex_via_nextacc_falls_back_to_config_token_when_not_injected()
    {
        var h = new CaptureHandler();
        var r = await Line(h, "CFG-TOKEN").TryPushFlexViaNextAccAsync("Ustaff123456", "", new { type = "bubble" });
        Assert.True(r.Ok);
        Assert.Equal("CFG-TOKEN", Assert.Single(h.Requests).Auth);
    }

    [Fact]
    public async Task Flex_via_nextacc_without_any_token_fails_with_reason_and_sends_nothing()
    {
        var h = new CaptureHandler();
        var r = await Line(h, null).TryPushFlexViaNextAccAsync("Ustaff123456", "x", new { type = "bubble" });
        Assert.False(r.Ok);
        Assert.Equal(NextAccLineConfigRule.NotConfiguredMessage, r.Error);
        Assert.Empty(h.Requests);
        var unbound = await Line(h, "CFG-TOKEN").TryPushFlexViaNextAccAsync(" ", "x", new { type = "bubble" });
        Assert.False(unbound.Ok);
        Assert.Empty(h.Requests);
    }
}

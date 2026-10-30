using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Cms;
using Accounting.Services.Interfaces;
using Accounting.Tests.Db;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 212 แพ็ก B (คำตัดสินข้อ 171 ข้อ 4 · เจ้าของ: "ส่ง") — ร้านเปลี่ยน LINE OA ⇒ เชิญลูกค้าที่ผูกไว้เดิมให้ผูกใหม่ ·
/// ตัวตัดสิน <see cref="CustomerRelinkInvitePlan"/> (ตรวจการสลับ · เลือกผู้รับ · เลือกช่อง) · คีย์กันซ้ำ <see cref="CustomerNotifyKeys.Relink"/> ·
/// ส่งผ่าน <see cref="BookingNotifier"/> ตัวเดียว (<c>LineViaNextAcc</c> = push ผ่าน OA เดิมของ NextAcc · ร้านปิดประเภท <c>line.relink</c> ได้)
/// </summary>
public class CmsRound212RelinkTests
{
    private const string Nx = CustomerNotifyChannel.ScopeNextAcc;
    private const string Co = CustomerNotifyChannel.ScopeCompany;
    private static readonly Guid Site = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000b1");
    private static readonly Guid SwitchId = Guid.Parse("5a5a5a5a-0000-0000-0000-000000000001");

    // ═════════ 1. ตรวจการสลับ ═════════

    [Theory]
    [InlineData(Nx, "Ubot1", Co, "Ushop", true)]     // ร้านเปิด OA ของตัวเอง
    [InlineData(Co, "Ushop", Nx, "Ubot1", true)]     // ร้านปิด OA ของตัวเอง กลับมาใช้ OA กลาง
    [InlineData(Co, "Ushop", Co, "Ushop2", true)]    // token ใหม่เป็น bot อื่น
    [InlineData(Nx, "Ubot1", Nx, "Ubot2", true)]     // bot ของ OA กลางเปลี่ยน (webhook)
    [InlineData(Co, "Ushop", Co, null, false)]       // ออก token ใหม่ ยังไม่รู้ bot — ไม่ใช่การสลับ (รอ webhook)
    [InlineData(Co, null, Co, "Ushop", false)]       // เพิ่งรู้ bot ครั้งแรก
    [InlineData(Co, "Ushop", Co, "Ushop", false)]    // แก้ Basic ID/กลุ่มอย่างเดียว
    [InlineData(Nx, null, Nx, null, false)]
    [InlineData(null, null, Co, "Ushop", false)]     // ไม่รู้ค่าเดิม
    public void Switch_detection_matrix(string? fromScope, string? fromDest, string toScope, string? toDest, bool expected)
        => Assert.Equal(expected, CustomerRelinkInvitePlan.IsSwitch(fromScope, fromDest, toScope, toDest));

    [Fact]
    public void Dedupe_key_is_stable_and_direction_sensitive()
    {
        Assert.Equal(CustomerRelinkInvitePlan.DedupeKey(Nx, "Ub", Co, "Us"), CustomerRelinkInvitePlan.DedupeKey(Nx, "Ub", Co, "Us"));
        Assert.NotEqual(CustomerRelinkInvitePlan.DedupeKey(Nx, "Ub", Co, "Us"), CustomerRelinkInvitePlan.DedupeKey(Co, "Us", Nx, "Ub"));
        Assert.Equal("company:->nextacc:-", CustomerRelinkInvitePlan.DedupeKey(Co, null, Nx, " "));
        Assert.True(CustomerRelinkInvitePlan.DedupeKey(Co, new string('x', 64), Co, new string('y', 64)).Length <= 200);
    }

    // ═════════ 2. เลือกผู้รับ ═════════

    private static RelinkLinkFact Link(string user, string kind = CustomerLineLinkRule.KindMember, string scope = Nx, string? dest = "Ubot1",
        bool blocked = false, bool withdrawn = false, bool relevant = true, string? email = "c@x.co", DateTime? linkedAt = null, string? token = null)
        => new(Guid.NewGuid(), Site, kind, Guid.NewGuid(), user, scope, dest, linkedAt ?? new DateTime(2026, 10, 1), blocked ? DateTime.UtcNow : null,
            withdrawn, relevant, email, token);

    private static RelinkPlanResult PlanNxToCompany(IEnumerable<RelinkLinkFact> links, bool nextAccCanPush = true)
        => CustomerRelinkInvitePlan.Plan(new RelinkSwitch(SwitchId, Nx, "Ubot1"), Co, "Ushop", links, nextAccCanPush);

    [Fact]
    public void Previously_linked_customers_are_invited_once_per_person()
    {
        var plan = PlanNxToCompany(new[]
        {
            Link("Ua"),                                                         // สมาชิก
            Link("Ua", CustomerLineLinkRule.KindBooking, linkedAt: new DateTime(2026, 10, 20)),  // คนเดียวกัน ผูกการจองด้วย
            Link("Ub", CustomerLineLinkRule.KindOrder),
        });
        Assert.Equal(2, plan.Invite.Count);
        var a = plan.Invite.Single(c => c.Link.LineUserId == "Ua");
        Assert.Equal(CustomerLineLinkRule.KindMember, a.Link.TargetKind);   // สมาชิกเป็นตัวแทน (หน้า "บัญชีของฉัน" ผูกครั้งเดียวใช้ได้ทุกรายการ)
        Assert.Equal(0, plan.Skipped);
    }

    [Fact]
    public void Withdrawn_blocked_and_inactive_are_skipped()
    {
        var plan = PlanNxToCompany(new[]
        {
            Link("Uw", withdrawn: true),
            Link("Ub", blocked: true),
            Link("Ub2", CustomerLineLinkRule.KindBooking), Link("Ub2", CustomerLineLinkRule.KindOrder, withdrawn: true),   // แถวใดแถวหนึ่งถอน ⇒ ไม่เชิญคนนั้น
            Link("Ui", CustomerLineLinkRule.KindBooking, relevant: false),     // การจองผ่านไปแล้ว
            Link("Uok"),
        });
        Assert.Equal(new[] { "Uok" }, plan.Invite.Select(c => c.Link.LineUserId).ToArray());
        Assert.Equal(3, plan.SkippedConsent);
        Assert.Equal(1, plan.SkippedInactive);
        Assert.Equal(4, plan.Skipped);
    }

    [Fact]
    public void Links_on_other_oa_or_still_usable_are_not_touched()
    {
        var plan = PlanNxToCompany(new[]
        {
            Link("Uold", scope: Co, dest: "Uoldshop"),    // ผูกกับ OA ร้านตัวเก่า — ไม่ใช่ OA เดิมของการสลับนี้
            Link("Unow", scope: Co, dest: "Ushop"),       // ผูกกับ OA ปัจจุบันแล้ว
            Link("Uother", dest: "Ubot9"),               // OA กลางคนละ bot
            Link("Uyes", dest: null),                    // ไม่รู้ bot ⇒ ตัดสินด้วย scope (เป็นของ OA เดิม)
        });
        Assert.Equal(new[] { "Uyes" }, plan.Invite.Select(c => c.Link.LineUserId).ToArray());
    }

    [Fact]
    public void Switch_back_makes_old_links_usable_and_nobody_is_invited()
    {
        // บันทึก Nx→Co ไว้ แต่ก่อนงานเดิน ร้านปิด OA ของตัวเองกลับไป Nx ⇒ ลิงก์ Nx ใช้ได้อีก ⇒ ไม่เชิญ
        var plan = CustomerRelinkInvitePlan.Plan(new RelinkSwitch(SwitchId, Nx, "Ubot1"), Nx, "Ubot1", new[] { Link("Ua") }, true);
        Assert.Empty(plan.Invite);
    }

    // ═════════ 3. เลือกช่อง: OA เดิม vs อีเมล ═════════

    [Fact]
    public void Old_nextacc_oa_pushes_when_shop_moved_to_own_oa()
    {
        var c = PlanNxToCompany(new[] { Link("Ua") }).Invite.Single();
        Assert.Equal(CustomerNotifyChannel.ChannelLine, c.Channel);
        Assert.Equal("c@x.co", c.Email);   // ส่งไปด้วย — push ล้ม ⇒ ถอยไปอีเมล
    }

    [Theory]
    [InlineData(Co, Nx, true, true, "email")]     // OA เดิม = ร้าน (token ถูกปิด/แทน) ⇒ อีเมล
    [InlineData(Co, Co, true, true, "email")]
    [InlineData(Nx, Nx, true, true, "email")]     // bot ของ OA กลางเปลี่ยน ⇒ OA เดิมไม่มีแล้ว
    [InlineData(Nx, Co, false, true, "email")]    // ไม่มี token กลาง
    [InlineData(Nx, Co, true, false, "line")]
    [InlineData(Co, Nx, true, false, "none")]     // ไม่มีช่องติดต่อ ⇒ ข้าม
    public void Channel_choice(string fromScope, string current, bool nextAccCanPush, bool hasEmail, string expected)
    {
        // ผ่าน Plan (ตัวที่ระบบเดินจริง) — ลิงก์ผูกกับ bot D1 ของ OA เดิม · OA ปัจจุบันเป็น bot D2
        var plan = CustomerRelinkInvitePlan.Plan(new RelinkSwitch(SwitchId, fromScope, "D1"), current, "D2",
            new[] { Link("Ua", scope: fromScope, dest: "D1", email: hasEmail ? "c@x.co" : null) }, nextAccCanPush);
        if (expected == CustomerRelinkInvitePlan.ChannelNone)
        {
            Assert.Empty(plan.Invite);
            Assert.Equal(1, plan.NoChannel);
        }
        else Assert.Equal(expected, plan.Invite.Single().Channel);
    }

    [Fact]
    public void No_channel_is_counted_as_skipped()
    {
        var plan = CustomerRelinkInvitePlan.Plan(new RelinkSwitch(SwitchId, Co, "Ushop"), Nx, "Ubot1",
            new[] { Link("Ua", scope: Co, dest: "Ushop", email: null), Link("Ub", scope: Co, dest: "Ushop") }, true);
        Assert.Single(plan.Invite);
        Assert.Equal(CustomerNotifyChannel.ChannelEmail, plan.Invite[0].Channel);
        Assert.Equal(1, plan.NoChannel);
    }

    [Fact]
    public void Relevance_rules()
    {
        var today = new DateOnly(2026, 10, 30);
        Assert.True(CustomerRelinkInvitePlan.IsRelevant(CustomerLineLinkRule.KindMember, true, null, null, null, today));
        Assert.False(CustomerRelinkInvitePlan.IsRelevant(CustomerLineLinkRule.KindMember, false, null, null, null, today));
        Assert.True(CustomerRelinkInvitePlan.IsRelevant(CustomerLineLinkRule.KindBooking, null, BookingStatus.Confirmed, new DateTime(2026, 10, 30), null, today));
        Assert.False(CustomerRelinkInvitePlan.IsRelevant(CustomerLineLinkRule.KindBooking, null, BookingStatus.Confirmed, new DateTime(2026, 10, 29), null, today));
        Assert.False(CustomerRelinkInvitePlan.IsRelevant(CustomerLineLinkRule.KindBooking, null, BookingStatus.Cancelled, new DateTime(2026, 11, 5), null, today));
        Assert.True(CustomerRelinkInvitePlan.IsRelevant(CustomerLineLinkRule.KindOrder, null, null, null, SiteOrderStatus.ReadyForPickup, today));
        Assert.False(CustomerRelinkInvitePlan.IsRelevant(CustomerLineLinkRule.KindOrder, null, null, null, SiteOrderStatus.Delivered, today));
    }

    // ═════════ 4. คีย์กันซ้ำ + ความคืบหน้า ═════════

    [Fact]
    public void Message_key_is_idempotent_per_switch_and_person_without_raw_user_id()
    {
        var k1 = CustomerNotifyKeys.Relink(SwitchId, "U1234567890abcdef");
        Assert.Equal(k1, CustomerNotifyKeys.Relink(SwitchId, " U1234567890abcdef "));
        Assert.NotEqual(k1, CustomerNotifyKeys.Relink(Guid.NewGuid(), "U1234567890abcdef"));
        Assert.NotEqual(k1, CustomerNotifyKeys.Relink(SwitchId, "U999"));
        Assert.StartsWith("relink:", k1);
        Assert.DoesNotContain("U1234567890abcdef", k1);   // ไม่เก็บ userId ในบันทึกการส่ง (PDPA)
        Assert.True(k1.Length <= 200);
        // แผนเดียวกันสองรอบได้คีย์เดียวกัน (ทยอยส่งข้ามรอบ/ข้ามเครื่องไม่ซ้ำ)
        var links = new[] { Link("Ua"), Link("Ub") };
        Assert.Equal(PlanNxToCompany(links).Invite.Select(c => c.MessageKey), PlanNxToCompany(links).Invite.Select(c => c.MessageKey));
    }

    [Fact]
    public void Terminal_log_states()
    {
        Assert.True(CustomerRelinkInvitePlan.IsTerminal(NotifyLogStatus.Sent, 1));
        Assert.True(CustomerRelinkInvitePlan.IsTerminal(NotifyLogStatus.Suppressed, 1));   // ร้านปิดประเภทนี้/โควตาเต็ม
        Assert.False(CustomerRelinkInvitePlan.IsTerminal(NotifyLogStatus.Failed, 1));      // ลองรอบหน้า
        Assert.True(CustomerRelinkInvitePlan.IsTerminal(NotifyLogStatus.Failed, CustomerRelinkInvitePlan.MaxAttempts));
        Assert.False(CustomerRelinkInvitePlan.IsTerminal(NotifyLogStatus.Claimed, 1));
        Assert.True(CustomerRelinkInvitePlan.IsTerminal(NotifyLogStatus.Claimed, CustomerRelinkInvitePlan.MaxAttempts + 1));
        Assert.False(CustomerRelinkInvitePlan.IsTerminal(null, 0));
    }

    [Fact]
    public void Progress_label_and_relink_link()
    {
        Assert.StartsWith("เชิญผูกใหม่: รอเริ่มส่ง", CustomerRelinkInvitePlan.ProgressLabel(false, 0, 0, 0, 0, false));
        var l = CustomerRelinkInvitePlan.ProgressLabel(true, 12, 5, 1, 2, false);
        Assert.Contains("ส่งแล้ว 5/12", l);
        Assert.Contains("ส่งไม่สำเร็จ 1", l);
        Assert.Contains("ข้าม 2", l);
        Assert.Contains("เสร็จแล้ว", CustomerRelinkInvitePlan.ProgressLabel(true, 3, 3, 0, 0, true));

        Assert.Equal("https://shop.example/account", CmsStorefrontLink.RelinkLine("https://shop.example", CustomerLineLinkRule.KindMember, null));
        Assert.Equal("https://shop.example/my-booking/tok_1", CmsStorefrontLink.RelinkLine("https://shop.example", CustomerLineLinkRule.KindBooking, "tok_1"));
        Assert.Equal("https://shop.example/track-order/tok_2", CmsStorefrontLink.RelinkLine("https://shop.example", CustomerLineLinkRule.KindOrder, "tok_2"));
        Assert.Contains("บัญชีของฉัน", CmsStorefrontLink.RelinkLine(null, CustomerLineLinkRule.KindMember, null));   // ไม่มีลิงก์ ⇒ บอกทาง ไม่เงียบ
        Assert.Contains("https://shop.example/check", CmsStorefrontLink.RelinkLine("https://shop.example", CustomerLineLinkRule.KindBooking, null));

        var text = CustomerRelinkInvitePlan.Text("ร้านเอ", "https://shop.example/account");
        Assert.Contains("https://shop.example/account", text);
        Assert.Contains("ไม่ต้องทำอะไร", text);   // PDPA: ไม่ต้องการ — ไม่ต้องทำอะไร
        Assert.DoesNotContain("@", text);
    }

    // ═════════ 5. ส่งผ่านตัวส่งเดียว — OA เดิม · ปิดตามค่าตั้ง ═════════

    private sealed class TestNotifier : BookingNotifier
    {
        private readonly IReadOnlyList<CustomerNotifyRuleRow> _rules;
        public TestNotifier(IReadOnlyList<CustomerNotifyRuleRow> rules, IEmailService email, ILineNotifyService line, INotifyQuotaService quota)
            : base(NullLogger<BookingNotifier>.Instance, email, line, null, null, null, quota)
        { _rules = rules; }
        protected override Task<CompanySettings?> LoadEmailSettingsAsync(Guid companyId, CancellationToken ct) => Task.FromResult<CompanySettings?>(new CompanySettings());
        protected override Task<IReadOnlyList<CustomerNotifyRuleRow>> LoadRulesAsync(Guid companyId, Guid? siteId, CancellationToken ct) => Task.FromResult(_rules);
        // บริษัทใช้ OA ของตัวเองแล้ว — เส้นปกติจะ push ผ่าน OA ร้าน (ผิดสำหรับ userId ของ OA กลาง)
        protected override Task<string?> ResolveLineScopeAsync(Guid companyId, CancellationToken ct) => Task.FromResult<string?>(Co);
    }

    private static TestNotifier Build(List<string> log, bool pushOk = true, IReadOnlyList<CustomerNotifyRuleRow>? rules = null)
    {
        var email = F4Proxy<IEmailService>.Create((m, a) =>
        {
            if (m != nameof(IEmailService.TrySendAsync)) return null;
            log.Add("system-email");
            return true;
        });
        var line = F4Proxy<ILineNotifyService>.Create((m, a) =>
        {
            if (m == nameof(ILineNotifyService.TryPushViaNextAccAsync)) { log.Add("push-nextacc"); return pushOk ? LinePushResult.Success : LinePushResult.Fail("LINE ตอบ 400"); }
            if (m == nameof(ILineNotifyService.TryPushToUserAsync)) { log.Add("push-company"); return true; }
            return null;
        });
        var quota = F4Proxy<INotifyQuotaService>.Create((m, a) => m switch
        {
            nameof(INotifyQuotaService.ClaimAsync) => (object?)new NotifyClaim(true, false, Guid.NewGuid(), null),
            nameof(INotifyQuotaService.TryReserveAsync) => (object?)Reserve(log),
            _ => null,
        });
        return new TestNotifier(rules ?? Array.Empty<CustomerNotifyRuleRow>(), email, line, quota);
    }

    private static NotifyReservation Reserve(List<string> log)
    {
        log.Add("reserve");
        return new NotifyReservation(true, "plan", "2026-10", 300, 0, 1);
    }

    private static CustomerNotifyRequest Invite(bool viaLine)
        => new(Guid.NewGuid(), Site, CustomerNotifyEvents.LineRelink, CustomerNotifyKeys.Relink(SwitchId, "Ua"),
            new BookingContact("c@x.co", viaLine ? "Ua" : null), "s", "t")
        { TargetKind = CustomerLineLinkRule.KindMember, TargetId = Guid.NewGuid(), LineViaNextAcc = viaLine };

    [Fact]
    public async Task Invite_via_old_nextacc_oa_uses_explicit_central_push_and_is_metered()
    {
        var log = new List<string>();
        var r = await Build(log).SendAsync(Invite(viaLine: true));
        Assert.True(r.Sent);
        Assert.Equal(CustomerNotifyChannel.ChannelLine, r.Channel);
        Assert.Equal(new[] { "reserve", "push-nextacc" }, log);   // LINE ก่อน (ช่องที่ลูกค้าผูกเอง) · ไม่ผ่าน OA ร้าน
        Assert.DoesNotContain("push-company", log);
    }

    [Fact]
    public async Task Failed_old_oa_push_falls_back_to_email()
    {
        var log = new List<string>();
        var r = await Build(log, pushOk: false).SendAsync(Invite(viaLine: true));
        Assert.True(r.Sent);
        Assert.Equal(CustomerNotifyChannel.ChannelEmail, r.Channel);
        Assert.Contains("push-nextacc", log);
        Assert.Contains("system-email", log);
    }

    [Fact]
    public async Task Email_invite_never_pushes()
    {
        var log = new List<string>();
        var r = await Build(log).SendAsync(Invite(viaLine: false));
        Assert.True(r.Sent);
        Assert.Equal(CustomerNotifyChannel.ChannelEmail, r.Channel);
        Assert.DoesNotContain("push-nextacc", log);
        Assert.DoesNotContain("push-company", log);
    }

    [Fact]
    public async Task Owner_can_disable_relink_invites_in_rules_matrix()
    {
        Assert.True(CustomerNotifyEvents.IsConfigurable(CustomerNotifyEvents.LineRelink));
        Assert.Contains(CustomerNotifyEvents.Catalog, e => e.Key == CustomerNotifyEvents.LineRelink && e.LineCapable);
        var off = new[] { new CustomerNotifyRuleRow(null, CustomerNotifyEvents.LineRelink, false, false) };
        var log = new List<string>();
        var r = await Build(log, rules: off).SendAsync(Invite(viaLine: true));
        Assert.False(r.Sent);
        Assert.True(r.IsSuppressed);
        Assert.Empty(log);   // ไม่จองโควตา ไม่ส่ง
        // ทิศตรงข้าม: ปิดประเภทอื่น ไม่กระทบคำเชิญ
        var other = new[] { new CustomerNotifyRuleRow(null, CustomerNotifyEvents.BookingReminder, false, false) };
        var log2 = new List<string>();
        Assert.True((await Build(log2, rules: other).SendAsync(Invite(viaLine: true))).Sent);
    }
}

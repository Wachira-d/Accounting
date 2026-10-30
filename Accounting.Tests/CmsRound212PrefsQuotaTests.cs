using System.Globalization;
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
/// รอบ 212 แพ็ก A (คำตัดสินข้อ 171 · <c>erp-review/2026-10-30/NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN.md</c>) —
/// ค่าตั้งแจ้งลูกค้าต่อประเภท × ช่อง (<see cref="CustomerNotifyPolicy"/>) · ถอยไปอีเมลของระบบ 3 ค่า (<see cref="CustomerNotifyChannel.EmailPlan"/>) ·
/// โควตาข้อความช่องกลาง (<see cref="NotifyQuotaPolicy"/>) · ตัวส่งเดียว <see cref="BookingNotifier"/> (กันซ้ำ · ระงับ · ไปช่องของร้านเมื่อเต็ม · คืนหน่วยเมื่อล้ม) ·
/// ทุกกลุ่มล็อกทั้ง "ค่าเริ่มต้น = พฤติกรรมเดิม" และทิศตรงข้าม
/// </summary>
public class CmsRound212PrefsQuotaTests
{
    private static readonly Guid SiteA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SiteB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // ═════════ 1. ตัวตัดสินค่าตั้ง ═════════

    [Fact]
    public void No_rows_means_both_channels_on_like_before()
    {
        var p = CustomerNotifyPolicy.Resolve(Array.Empty<CustomerNotifyRuleRow>(), SiteA, CustomerNotifyEvents.BookingReminder);
        Assert.True(p.LineOn);
        Assert.True(p.EmailOn);
        Assert.Equal(CustomerNotifyPolicy.LayerDefault, p.LineSource);
        Assert.Equal(CustomerNotifyPolicy.LayerDefault, p.EmailSource);
        // ไม่มีเว็บ (บัตรคอร์ส) ก็ค่าเริ่มต้นเดียวกัน
        Assert.True(CustomerNotifyPolicy.Resolve(Array.Empty<CustomerNotifyRuleRow>(), null, CustomerNotifyEvents.CreditExpiring).AnyOn);
    }

    [Fact]
    public void Site_beats_company_and_key_beats_all_per_channel()
    {
        var rules = new[]
        {
            new CustomerNotifyRuleRow(null, CustomerNotifyEvents.All, false, true),            // บริษัท: ทั้งหมด ปิด LINE
            new CustomerNotifyRuleRow(null, CustomerNotifyEvents.OrderShipped, true, null),    // บริษัท: จัดส่งแล้ว เปิด LINE
            new CustomerNotifyRuleRow(SiteA, CustomerNotifyEvents.All, null, false),           // เว็บ A: ทั้งหมด ปิดอีเมล
            new CustomerNotifyRuleRow(SiteA, CustomerNotifyEvents.OrderShipped, false, null),  // เว็บ A: จัดส่งแล้ว ปิด LINE
        };
        var a = CustomerNotifyPolicy.Resolve(rules, SiteA, CustomerNotifyEvents.OrderShipped);
        Assert.False(a.LineOn);
        Assert.Equal(CustomerNotifyPolicy.LayerSite, a.LineSource);
        Assert.False(a.EmailOn);
        Assert.Equal(CustomerNotifyPolicy.LayerSiteAll, a.EmailSource);

        var company = CustomerNotifyPolicy.Resolve(rules, null, CustomerNotifyEvents.OrderShipped);
        Assert.True(company.LineOn);                                     // คีย์ชนะ "*"
        Assert.Equal(CustomerNotifyPolicy.LayerCompany, company.LineSource);
        Assert.True(company.EmailOn);

        var other = CustomerNotifyPolicy.Resolve(rules, null, CustomerNotifyEvents.BookingConfirmed);
        Assert.False(other.LineOn);
        Assert.Equal(CustomerNotifyPolicy.LayerCompanyAll, other.LineSource);
    }

    [Fact]
    public void Opposite_site_A_off_does_not_touch_site_B()
    {
        var rules = new[] { new CustomerNotifyRuleRow(SiteA, CustomerNotifyEvents.All, false, false) };
        Assert.False(CustomerNotifyPolicy.Resolve(rules, SiteA, CustomerNotifyEvents.BookingConfirmed).AnyOn);
        var b = CustomerNotifyPolicy.Resolve(rules, SiteB, CustomerNotifyEvents.BookingConfirmed);
        Assert.True(b.LineOn && b.EmailOn);
        Assert.Equal(CustomerNotifyPolicy.LayerDefault, b.LineSource);
    }

    [Fact]
    public void Shop_messages_ignore_customer_rules()
    {
        var rules = new[] { new CustomerNotifyRuleRow(null, CustomerNotifyEvents.All, false, false) };
        Assert.True(CustomerNotifyPolicy.Resolve(rules, null, CustomerNotifyEvents.ShopNotify).AnyOn);
        Assert.True(CustomerNotifyPolicy.Resolve(rules, null, CustomerNotifyEvents.ShopPaymentProblem).EmailOn);
        Assert.True(CustomerNotifyEvents.IsEssential(CustomerNotifyEvents.ShopPaymentProblem));
        Assert.False(CustomerNotifyEvents.IsEssential(CustomerNotifyEvents.ShopNotify));
        Assert.False(CustomerNotifyEvents.IsConfigurable(CustomerNotifyEvents.ShopNotify));
    }

    [Fact]
    public void Plan_keeps_old_order_and_marks_metered_sources()
    {
        var on = CustomerNotifyPolicy.Resolve(Array.Empty<CustomerNotifyRuleRow>(), null, CustomerNotifyEvents.BookingConfirmed);
        // ไม่ผูก LINE ⇒ อีเมลก่อน (เดิม) · ร้านตั้งอีเมล ⇒ ร้านก่อนแล้วระบบ
        var steps = CustomerNotifyPolicy.Plan(on, hasEmail: true, hasLine: true, lineOptedIn: false, CustomerNotifyChannel.ScopeNextAcc,
            CustomerNotifyChannel.EmailPlan(true));
        Assert.Equal(new[] { CustomerNotifyPolicy.SrcTenantSmtp, CustomerNotifyPolicy.SrcSystemSmtp, CustomerNotifyPolicy.SrcNextAccOa },
            steps.Select(s => s.Source).ToArray());
        Assert.Equal(new[] { false, true, true }, steps.Select(s => s.Metered).ToArray());

        // ผูก LINE เอง ⇒ LINE ก่อน · OA ของร้าน = ไม่นับ
        var opted = CustomerNotifyPolicy.Plan(on, true, true, true, CustomerNotifyChannel.ScopeCompany, CustomerNotifyChannel.EmailPlan(false));
        Assert.Equal(CustomerNotifyChannel.ChannelLine, opted[0].Channel);
        Assert.Equal(CustomerNotifyPolicy.SrcCompanyOa, opted[0].Source);
        Assert.False(opted[0].Metered);
        Assert.True(opted[1].Metered);   // อีเมลระบบ
    }

    [Fact]
    public void Line_off_but_customer_linked_means_email_only()
    {
        var rules = new[] { new CustomerNotifyRuleRow(null, CustomerNotifyEvents.All, false, null) };
        var p = CustomerNotifyPolicy.Resolve(rules, null, CustomerNotifyEvents.BookingReminder);
        var steps = CustomerNotifyPolicy.Plan(p, true, true, true, CustomerNotifyChannel.ScopeNextAcc, CustomerNotifyChannel.EmailPlan(false));
        Assert.Single(steps);
        Assert.Equal(CustomerNotifyChannel.ChannelEmail, steps[0].Channel);
    }

    [Fact]
    public void Suppress_reasons_distinguish_policy_tenant_only_and_no_contact()
    {
        var off = CustomerNotifyPolicy.Resolve(new[] { new CustomerNotifyRuleRow(null, CustomerNotifyEvents.All, false, false) }, null, CustomerNotifyEvents.OrderReady);
        Assert.Equal(CustomerNotifyPolicy.SuppressedByPolicyReason, CustomerNotifyPolicy.SuppressReason(off, true, true, CustomerNotifyChannel.EmailPlan(false)));
        var on = CustomerNotifyPolicy.Resolve(Array.Empty<CustomerNotifyRuleRow>(), null, CustomerNotifyEvents.OrderReady);
        Assert.Equal(CustomerNotifyPolicy.SuppressedTenantOnlyReason,
            CustomerNotifyPolicy.SuppressReason(on, true, false, CustomerNotifyChannel.EmailPlan(false, CustomerNotifyChannel.FallbackTenantOnly)));
        Assert.Null(CustomerNotifyPolicy.SuppressReason(on, false, false, CustomerNotifyChannel.EmailPlan(false)));   // ไม่มีช่องติดต่อ = Fail เดิม ไม่ใช่ระงับ
        Assert.Null(CustomerNotifyPolicy.SuppressReason(on, true, false, CustomerNotifyChannel.EmailPlan(false)));
    }

    [Fact]
    public void Validate_rejects_unknown_and_duplicate_keys()
    {
        Assert.Empty(CustomerNotifyPolicy.Validate(new[]
        {
            new CustomerNotifyRuleRow(null, CustomerNotifyEvents.All, true, null),
            new CustomerNotifyRuleRow(null, CustomerNotifyEvents.CartAbandoned, null, false),
        }));
        Assert.NotEmpty(CustomerNotifyPolicy.Validate(new[] { new CustomerNotifyRuleRow(null, "booking.typo", true, true) }));
        Assert.NotEmpty(CustomerNotifyPolicy.Validate(new[] { new CustomerNotifyRuleRow(null, CustomerNotifyEvents.ShopNotify, true, true) }));
        Assert.NotEmpty(CustomerNotifyPolicy.Validate(new[]
        {
            new CustomerNotifyRuleRow(null, CustomerNotifyEvents.OrderShipped, true, null),
            new CustomerNotifyRuleRow(null, CustomerNotifyEvents.OrderShipped, false, null),
        }));
    }

    [Fact]
    public void Catalog_keys_unique_and_mappings_cover_message_sources()
    {
        var keys = CustomerNotifyEvents.Catalog.Select(e => e.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(19, keys.Count);   // รอบ 212 แพ็ก B: + line.relink
        Assert.Equal(CustomerNotifyEvents.OrderShipped, CustomerNotifyEvents.OrderStatusKey(SiteOrderStatus.Shipped));
        Assert.Equal(CustomerNotifyEvents.OrderReady, CustomerNotifyEvents.OrderStatusKey(SiteOrderStatus.ReadyForPickup));
        Assert.Equal(CustomerNotifyEvents.OrderDelivered, CustomerNotifyEvents.OrderStatusKey(SiteOrderStatus.Delivered));
        Assert.Null(CustomerNotifyEvents.OrderStatusKey(SiteOrderStatus.Processing));
        // ตรงกับข้อความ: สถานะที่ CmsNotificationText.CustomerOrderStatus มีข้อความ = สถานะที่มีคีย์
        foreach (SiteOrderStatus st in Enum.GetValues(typeof(SiteOrderStatus)))
            Assert.Equal(CmsNotificationText.CustomerOrderStatus(st, "s", "N1", null, null, null, 0m) != null,
                CustomerNotifyEvents.OrderStatusKey(st) != null);
        Assert.Equal(CustomerNotifyEvents.LodgingCreated, CustomerNotifyEvents.LodgingGuestKey(LodgingGuestConfirmPolicy.EventCreated));
        Assert.Equal(CustomerNotifyEvents.LodgingSlipRequired, CustomerNotifyEvents.LodgingGuestKey(LodgingGuestConfirmPolicy.EventAwaitingSlip));
        Assert.Null(CustomerNotifyEvents.LodgingGuestKey("payment-problem"));
    }

    // ═════════ 2. ถอยไปอีเมลของระบบ (3 ค่า × ตั้ง/ไม่ตั้ง) ═════════

    [Theory]
    [InlineData(null, true, "tenant,system")]
    [InlineData(null, false, "system")]
    [InlineData("", true, "tenant,system")]
    [InlineData("system-on-fail-or-unset", true, "tenant,system")]
    [InlineData("system-on-fail-or-unset", false, "system")]
    [InlineData("system-when-unset-only", true, "tenant")]
    [InlineData("system-when-unset-only", false, "system")]
    [InlineData("tenant-only", true, "tenant")]
    [InlineData("tenant-only", false, "")]
    [InlineData("garbage", true, "tenant,system")]
    public void Email_plan_follows_fallback_setting(string? fallback, bool configured, string expected)
        => Assert.Equal(expected, string.Join(",", CustomerNotifyChannel.EmailPlan(configured, fallback)));

    [Fact]
    public void Fallback_values_validate_and_default()
    {
        Assert.True(CustomerNotifyChannel.IsValidFallback(null));
        Assert.True(CustomerNotifyChannel.IsValidFallback(CustomerNotifyChannel.FallbackTenantOnly));
        Assert.False(CustomerNotifyChannel.IsValidFallback("always"));
        Assert.Equal(CustomerNotifyChannel.FallbackSystemOnFailOrUnset, CustomerNotifyChannel.EffectiveFallback(null));
        Assert.Equal(3, CustomerNotifyChannel.FallbackOptions.Count);
        // รอบ 211 เดิม (เรียกแบบอาร์กิวเมนต์เดียว) ไม่เปลี่ยน
        Assert.Equal(new[] { CustomerNotifyChannel.EmailTenant, CustomerNotifyChannel.EmailSystem }, CustomerNotifyChannel.EmailPlan(true));
    }

    // ═════════ 3. โควตา (pure) ═════════

    [Fact]
    public void Only_shared_channels_are_metered()
    {
        Assert.True(NotifyQuotaPolicy.IsMetered(CustomerNotifyPolicy.SrcNextAccOa));
        Assert.True(NotifyQuotaPolicy.IsMetered(CustomerNotifyPolicy.SrcSystemSmtp));
        Assert.False(NotifyQuotaPolicy.IsMetered(CustomerNotifyPolicy.SrcCompanyOa));
        Assert.False(NotifyQuotaPolicy.IsMetered(CustomerNotifyPolicy.SrcTenantSmtp));
        Assert.False(NotifyQuotaPolicy.IsMetered(null));
    }

    [Fact]
    public void Plan_limit_override_then_template_then_unlimited()
    {
        Assert.Equal(50, NotifyQuotaPolicy.PlanLimit(50, 300, 100, isTrial: false));
        Assert.Equal(NotifyQuotaPolicy.Unlimited, NotifyQuotaPolicy.PlanLimit(-1, 300, 100, false));
        Assert.Equal(300, NotifyQuotaPolicy.PlanLimit(null, 300, 100, false));
        Assert.Equal(100, NotifyQuotaPolicy.PlanLimit(null, 300, 100, true));
        Assert.Equal(300, NotifyQuotaPolicy.PlanLimit(null, 300, null, true));
        Assert.Equal(NotifyQuotaPolicy.Unlimited, NotifyQuotaPolicy.PlanLimit(null, null, null, false));   // ไม่มีแม่แบบ = เดิม (ไม่จำกัด)
        Assert.Equal(0, NotifyQuotaPolicy.PlanLimit(null, 0, null, false));
    }

    [Fact]
    public void Warn_levels_80_and_100_and_crossing_once()
    {
        Assert.Equal(0, NotifyQuotaPolicy.WarnLevel(79, 100));
        Assert.Equal(1, NotifyQuotaPolicy.WarnLevel(80, 100));
        Assert.Equal(2, NotifyQuotaPolicy.WarnLevel(100, 100));
        Assert.Equal(0, NotifyQuotaPolicy.WarnLevel(5000, NotifyQuotaPolicy.Unlimited));
        Assert.Equal(1, NotifyQuotaPolicy.CrossedLevel(79, 80, 100));
        Assert.Equal(0, NotifyQuotaPolicy.CrossedLevel(80, 81, 100));
        Assert.Equal(2, NotifyQuotaPolicy.CrossedLevel(99, 100, 100));
    }

    [Fact]
    public void Decide_uses_plan_then_live_bonus_then_essential()
    {
        var now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(NotifyQuotaDecision.Plan, NotifyQuotaPolicy.Decide(99, 100, false, 0, null, now));
        Assert.Equal(NotifyQuotaDecision.Bonus, NotifyQuotaPolicy.Decide(100, 100, false, 5, now.AddDays(1), now));
        Assert.Equal(NotifyQuotaDecision.Full, NotifyQuotaPolicy.Decide(100, 100, false, 5, now.AddDays(-1), now));   // โบนัสหมดอายุไม่นับ
        Assert.Equal(NotifyQuotaDecision.Essential, NotifyQuotaPolicy.Decide(100, 100, true, 0, null, now));
        Assert.Equal(NotifyQuotaDecision.Plan, NotifyQuotaPolicy.Decide(10_000, NotifyQuotaPolicy.Unlimited, false, 0, null, now));
    }

    [Fact]
    public void Effective_limit_counts_only_live_bonus()
    {
        var now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(300 + 20 + 480, NotifyQuotaPolicy.EffectiveLimit(300, 20, 480, now.AddDays(10), now));
        Assert.Equal(300 + 20, NotifyQuotaPolicy.EffectiveLimit(300, 20, 480, now.AddDays(-1), now));
        Assert.Equal(NotifyQuotaPolicy.Unlimited, NotifyQuotaPolicy.EffectiveLimit(-1, 0, 500, null, now));
    }

    [Fact]
    public void Period_is_bangkok_month()
    {
        Assert.Equal("2026-10", NotifyQuotaPolicy.PeriodOf(new DateTime(2026, 10, 31, 16, 30, 0, DateTimeKind.Utc)));   // 23:30 ไทย 31 ต.ค.
        Assert.Equal("2026-11", NotifyQuotaPolicy.PeriodOf(new DateTime(2026, 10, 31, 17, 30, 0, DateTimeKind.Utc)));   // 00:30 ไทย 1 พ.ย.
    }

    [Fact]
    public void Message_keys_are_culture_invariant_and_distinct()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
            Assert.Equal("credit:11111111222233334444555555555555:expiring:20261101", CustomerNotifyKeys.Credit(id, new DateOnly(2026, 11, 1)));
            Assert.Contains(":202611010930", CustomerNotifyKeys.BookingRescheduled(id, new DateTime(2026, 11, 1), new TimeOnly(9, 30)));
        }
        finally { CultureInfo.CurrentCulture = prev; }
        var s = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.NotEqual(CustomerNotifyKeys.Class(s, b, CustomerNotifyEvents.ClassSubstitute, "ครูเอ"),
            CustomerNotifyKeys.Class(s, b, CustomerNotifyEvents.ClassSubstitute, "ครูบี"));
        Assert.Equal(CustomerNotifyKeys.Class(s, b, CustomerNotifyEvents.ClassCancelled, "x"), CustomerNotifyKeys.Class(s, b, CustomerNotifyEvents.ClassCancelled, "y"));
        Assert.NotEqual(CustomerNotifyKeys.Reminder(b, 24), CustomerNotifyKeys.Reminder(b, 2));
        Assert.True(CustomerNotifyKeys.Lodging(b, CustomerNotifyEvents.LodgingSlipRejected, DateTime.UtcNow.ToString("O")).Length <= 200);
    }

    // ═════════ 4. ตัวส่งเดียว — ค่าตั้ง · กันซ้ำ · โควตา ═════════

    private sealed class Calls { public List<string> Log { get; } = new(); }

    /// <summary>บริการโควตาปลอม — ตั้งผล claim/จองได้ · จำการเรียก</summary>
    private sealed class FakeQuota : INotifyQuotaService
    {
        public NotifyClaim Claim { get; set; } = new(true, false, Guid.NewGuid(), null);
        public bool Full { get; set; }
        public List<string> Calls { get; } = new();
        public List<bool> EssentialFlags { get; } = new();
        public string? CompletedStatus { get; private set; }
        public string? CompletedSource { get; private set; }

        public Task<NotifyClaim> ClaimAsync(Guid companyId, string? messageKey, string eventKey, string? targetKind, Guid? targetId, CancellationToken ct = default)
        { Calls.Add("claim"); return Task.FromResult(Claim); }
        public Task<NotifyReservation> TryReserveAsync(Guid companyId, bool essential, CancellationToken ct = default)
        {
            Calls.Add("reserve");
            EssentialFlags.Add(essential);
            return Task.FromResult(Full && !essential
                ? new NotifyReservation(false, "full", "2026-10", 300, 2, 300)
                : new NotifyReservation(true, "plan", "2026-10", 300, 0, 10));
        }
        public Task ReleaseAsync(Guid companyId, NotifyReservation reservation, CancellationToken ct = default) { Calls.Add("release"); return Task.CompletedTask; }
        public Task AlertIfCrossedAsync(Guid companyId, NotifyReservation reservation, CancellationToken ct = default) { Calls.Add("alert"); return Task.CompletedTask; }
        public Task CompleteAsync(Guid companyId, Guid? logId, string status, string? channel, string? source, bool metered, string? note, CancellationToken ct = default)
        { Calls.Add("complete:" + status); CompletedStatus = status; CompletedSource = source; return Task.CompletedTask; }
        public Task<NotifyQuotaStatus> GetStatusAsync(Guid companyId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NotifyQuotaStatus> PurchaseTopUpAsync(Guid companyId, int packs, string actor, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TestNotifier : BookingNotifier
    {
        private readonly CompanySettings? _s;
        private readonly IReadOnlyList<CustomerNotifyRuleRow> _rules;
        private readonly string _scope;
        public TestNotifier(CompanySettings? s, IReadOnlyList<CustomerNotifyRuleRow> rules, string scope, IEmailService email,
            ILineNotifyService line, ICustomerLineLinkService links, IEmailSenderFactory senders, INotifyQuotaService quota)
            : base(NullLogger<BookingNotifier>.Instance, email, line, links, senders, null, quota)
        { _s = s; _rules = rules; _scope = scope; }
        protected override Task<CompanySettings?> LoadEmailSettingsAsync(Guid companyId, CancellationToken ct) => Task.FromResult(_s);
        protected override Task<IReadOnlyList<CustomerNotifyRuleRow>> LoadRulesAsync(Guid companyId, Guid? siteId, CancellationToken ct) => Task.FromResult(_rules);
        protected override Task<string?> ResolveLineScopeAsync(Guid companyId, CancellationToken ct) => Task.FromResult<string?>(_scope);
    }

    private static TestNotifier Build(Calls calls, FakeQuota quota, string? optedLine = null, bool tenantConfigured = false, bool tenantOk = true,
        bool systemOk = true, bool lineOk = true, string? fallback = null, IReadOnlyList<CustomerNotifyRuleRow>? rules = null,
        string scope = CustomerNotifyChannel.ScopeNextAcc)
    {
        var settings = new CompanySettings
        {
            EmailConfigured = tenantConfigured, EmailSmtpHost = "smtp.shop.test", EmailFromAddress = "shop@shop.test", CustomerEmailFallback = fallback,
        };
        var sender = F4Proxy<IEmailSender>.Create((m, a) =>
        {
            if (m != nameof(IEmailSender.SendAsync)) return null;
            calls.Log.Add("tenant");
            return tenantOk ? EmailSendResult.Ok("id") : EmailSendResult.Fail("535 auth");
        });
        var factory = F4Proxy<IEmailSenderFactory>.Create((m, a) => m == nameof(IEmailSenderFactory.GetSenderForSettings) ? sender : null);
        var system = F4Proxy<IEmailService>.Create((m, a) =>
        {
            if (m != nameof(IEmailService.TrySendAsync)) return null;
            calls.Log.Add("system");
            return systemOk;
        });
        var line = F4Proxy<ILineNotifyService>.Create((m, a) =>
        {
            if (m != nameof(ILineNotifyService.TryPushToUserAsync)) return null;
            calls.Log.Add("line");
            return lineOk;
        });
        var links = F4Proxy<ICustomerLineLinkService>.Create((m, a) => m == nameof(ICustomerLineLinkService.ResolveLineUserIdAsync) ? optedLine : null);
        return new TestNotifier(settings, rules ?? Array.Empty<CustomerNotifyRuleRow>(), scope, system, line, links, factory, quota);
    }

    private static CustomerNotifyRequest Req(string eventKey = CustomerNotifyEvents.BookingConfirmed, string? key = "booking:x:confirmed")
        => new(Guid.NewGuid(), SiteA, eventKey, key,
            new BookingContact("a@b.co", null) { LineTarget = CustomerLineTarget.ForBooking(Guid.NewGuid(), null) }, "s", "t");

    [Fact]
    public async Task Defaults_send_like_before_and_count_system_email()
    {
        var calls = new Calls(); var q = new FakeQuota();
        var r = await Build(calls, q).SendAsync(Req());
        Assert.True(r.Sent);
        Assert.Equal("email", r.Channel);
        Assert.Null(r.Warning);
        Assert.Equal(new[] { "system" }, calls.Log);
        Assert.Equal(new[] { "claim", "reserve", "alert", "complete:Sent" }, q.Calls);
        Assert.Equal(CustomerNotifyPolicy.SrcSystemSmtp, q.CompletedSource);
    }

    [Fact]
    public async Task Tenant_channels_are_not_counted()
    {
        var calls = new Calls(); var q = new FakeQuota();
        var r = await Build(calls, q, optedLine: "Ulinked", scope: CustomerNotifyChannel.ScopeCompany).SendAsync(Req());
        Assert.Equal("line", r.Channel);
        Assert.DoesNotContain("reserve", q.Calls);
        var q2 = new FakeQuota();
        var r2 = await Build(new Calls(), q2, tenantConfigured: true).SendAsync(Req());
        Assert.True(r2.Sent);
        Assert.DoesNotContain("reserve", q2.Calls);
    }

    [Fact]
    public async Task Quota_full_routes_to_tenant_channel_with_warning()
    {
        var calls = new Calls(); var q = new FakeQuota { Full = true };
        // ลูกค้าผูก LINE กับ OA กลาง (นับ) + ร้านตั้งอีเมลเอง ⇒ LINE ถูกข้าม (เต็ม) ⇒ อีเมลของร้าน
        var r = await Build(calls, q, optedLine: "Ulinked", tenantConfigured: true).SendAsync(Req());
        Assert.True(r.Sent);
        Assert.Equal("email", r.Channel);
        Assert.Equal(new[] { "tenant" }, calls.Log);
        Assert.NotNull(r.Warning);
        Assert.StartsWith(CmsNotificationText.FallbackTag, r.Warning);
    }

    [Fact]
    public async Task Quota_full_without_tenant_channel_suppresses_and_sends_nothing()
    {
        var calls = new Calls(); var q = new FakeQuota { Full = true };
        var r = await Build(calls, q).SendAsync(Req());
        Assert.False(r.Sent);
        Assert.True(r.IsSuppressed);
        Assert.Contains("โควตา", r.Error);
        Assert.Empty(calls.Log);
        Assert.Equal(NotifyLogStatus.Suppressed, q.CompletedStatus);
        Assert.Contains("alert", q.Calls);   // แจ้งเจ้าของระดับเต็ม (ประทับกันซ้ำที่บริการ)
    }

    [Fact]
    public async Task Essential_shop_message_is_never_suppressed_by_quota()
    {
        var calls = new Calls(); var q = new FakeQuota { Full = true };
        var r = await Build(calls, q).SendAsync(Req(CustomerNotifyEvents.ShopPaymentProblem, null));
        Assert.True(r.Sent);
        Assert.Equal(new[] { true }, q.EssentialFlags);
    }

    [Fact]
    public async Task Failed_shared_send_releases_the_unit()
    {
        var calls = new Calls(); var q = new FakeQuota();
        var r = await Build(calls, q, systemOk: false).SendAsync(Req());
        Assert.False(r.Sent);
        Assert.False(r.IsSuppressed);
        Assert.Contains("release", q.Calls);
        Assert.Equal(NotifyLogStatus.Failed, q.CompletedStatus);
    }

    [Fact]
    public async Task Already_sent_key_is_not_sent_again()
    {
        var calls = new Calls(); var q = new FakeQuota { Claim = new NotifyClaim(false, true, null, "line") };
        var r = await Build(calls, q).SendAsync(Req());
        Assert.True(r.Sent);
        Assert.True(r.Duplicate);
        Assert.Equal("line", r.Channel);
        Assert.Empty(calls.Log);
        Assert.Equal(new[] { "claim" }, q.Calls);
    }

    [Fact]
    public async Task Key_in_flight_elsewhere_is_suppressed_not_sent()
    {
        var calls = new Calls(); var q = new FakeQuota { Claim = new NotifyClaim(false, false, null, null) };
        var r = await Build(calls, q).SendAsync(Req());
        Assert.False(r.Sent);
        Assert.True(r.IsSuppressed);
        Assert.Empty(calls.Log);
    }

    [Fact]
    public async Task Policy_off_suppresses_and_opposite_event_still_sends()
    {
        var rules = new[] { new CustomerNotifyRuleRow(SiteA, CustomerNotifyEvents.BookingConfirmed, false, false) };
        var calls = new Calls(); var q = new FakeQuota();
        var r = await Build(calls, q, rules: rules).SendAsync(Req());
        Assert.True(r.IsSuppressed);
        Assert.Equal(CustomerNotifyPolicy.SuppressedByPolicyReason, r.Error);
        Assert.Empty(calls.Log);
        var other = await Build(new Calls(), new FakeQuota(), rules: rules).SendAsync(Req(CustomerNotifyEvents.BookingCancelled, "booking:x:cancelled"));
        Assert.True(other.Sent);
    }

    [Fact]
    public async Task Fallback_settings_change_email_sources()
    {
        // ร้านตั้งแล้วล้ม + "ไม่ถอย" ⇒ ไม่เรียกอีเมลระบบ
        var calls = new Calls();
        var r = await Build(calls, new FakeQuota(), tenantConfigured: true, tenantOk: false, fallback: CustomerNotifyChannel.FallbackSystemWhenUnsetOnly).SendAsync(Req());
        Assert.False(r.Sent);
        Assert.Equal(new[] { "tenant" }, calls.Log);
        // ค่าเริ่มต้น ⇒ ถอยไประบบ + คำเตือน (รอบ 211 เดิม)
        var calls2 = new Calls();
        var r2 = await Build(calls2, new FakeQuota(), tenantConfigured: true, tenantOk: false).SendAsync(Req());
        Assert.True(r2.Sent);
        Assert.Equal(new[] { "tenant", "system" }, calls2.Log);
        Assert.Contains("535 auth", r2.Warning);
        // ร้านเท่านั้น + ไม่ตั้ง ⇒ ระงับ (ไม่ส่งอีเมลในนามระบบ)
        var calls3 = new Calls();
        var r3 = await Build(calls3, new FakeQuota(), fallback: CustomerNotifyChannel.FallbackTenantOnly).SendAsync(Req());
        Assert.True(r3.IsSuppressed);
        Assert.Empty(calls3.Log);
    }

    [Fact]
    public async Task Without_quota_service_behaves_like_round_211()
    {
        var calls = new Calls();
        var n = new BookingNotifier(NullLogger<BookingNotifier>.Instance,
            F4Proxy<IEmailService>.Create((m, a) => { if (m == nameof(IEmailService.TrySendAsync)) { calls.Log.Add("system"); return true; } return null; }));
        var r = await n.SendAsync(Req());
        Assert.True(r.Sent);
        Assert.Equal(new[] { "system" }, calls.Log);
    }
}

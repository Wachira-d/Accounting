using System.Net;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Tests.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 212 C (คำตัดสินข้อ 5 · <c>erp-review/2026-10-30/NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN.md</c>) — สรุปประจำวันทาง LINE ถึงพนักงาน:
/// ตัวประกอบข้อความ (<see cref="StaffDailySummaryBuilder"/>) · เพดาน LINE (<see cref="LineMessageLimits"/>) · การกรองต่อผู้รับ / เวลาไทย / สถานะแถวส่ง
/// (<see cref="StaffDailySummaryPlan"/>) · push ถึงพนักงานผ่าน OA กลางแบบระบุชัด (<see cref="LineNotifyService.TryPushViaNextAccAsync"/>) —
/// ทุกกลุ่มล็อกทั้งทิศ "ใหม่" และ "ทิศตรงข้าม/เดิมไม่เปลี่ยน"
/// </summary>
public class CmsRound212DailySummaryTests
{
    private static readonly DateOnly Today = new(2026, 10, 30);
    private static readonly Guid SiteA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SiteB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static StaffDailySummaryPlan.SummaryData SampleData() => new(
        Bookings: new[]
        {
            new StaffDailySummaryPlan.BookingRow(SiteA, Today, new TimeOnly(9, 0), "นวดไทย", "หมอนวด ก", "คุณเอ"),
            new StaffDailySummaryPlan.BookingRow(SiteA, Today, new TimeOnly(11, 0), "นวดน้ำมัน", "หมอนวด ก", "คุณบี"),
            new StaffDailySummaryPlan.BookingRow(SiteB, Today, new TimeOnly(10, 0), "ตัดผม", "ช่าง ข", "คุณซี"),
            new StaffDailySummaryPlan.BookingRow(SiteA, Today.AddDays(1), new TimeOnly(9, 0), "นวดไทย", "หมอนวด ก", "คุณดี"),
        },
        Classes: new[] { new StaffDailySummaryPlan.ClassRow(SiteA, Today, new TimeOnly(18, 0), "โยคะ", "ครูอ้อ", 8, 10, 2) },
        MadeToOrder: new[]
        {
            new StaffDailySummaryPlan.OrderRow(SiteA, "SO-0002", "คุณอี", Today.AddDays(2), 1200m, "ยืนยันแล้ว"),
            new StaffDailySummaryPlan.OrderRow(SiteB, "SO-0001", "คุณเอฟ", Today.AddDays(-1), 0m, "กำลังเตรียม"),
        },
        ToShip: new[] { new StaffDailySummaryPlan.OrderRow(SiteB, "SO-0003", "คุณจี", null, 0m, "ยืนยันแล้ว") },
        Lodging: new[] { new StaffDailySummaryPlan.LodgingRow("บ้านริมน้ำ", "R-001", "คุณเอช", true, false) },
        Credits: new[] { new StaffDailySummaryPlan.CreditRow("CP-001", "คุณไอ", "10 ครั้ง", Today.AddDays(3), 2.5m) });

    private static readonly string[] AllSections = StaffDailySummaryPlan.Sections.Select(s => s.Key).ToArray();

    private static StaffDailySummaryPlan.RecipientScope FullScope() => new(
        StaffDailySummaryPlan.PermissionKeysToCheck(), new[] { SiteA, SiteB }, new[] { SiteA, SiteB });

    // ═════════ 1. ตัวประกอบข้อความ ═════════

    [Fact]
    public void Builder_renders_each_section_with_numbers_and_provider_grouping()
    {
        var msgs = StaffDailySummaryBuilder.Build(Today, "ร้านทดสอบ", null, SampleData(), "https://app.example");
        var text = Assert.Single(msgs);
        Assert.StartsWith("☀️ สรุปประจำวัน 30/10/2026 · ร้านทดสอบ", text);
        Assert.Contains("📅 การจองวันนี้ — 3 คิว", text);
        Assert.Contains("👤 หมอนวด ก (2)", text);
        Assert.Contains("• 09:00 นวดไทย · คุณเอ", text);
        Assert.Contains("📅 พรุ่งนี้ 31/10/2026 — 1 คิว", text);
        Assert.Contains("• หมอนวด ก: 1 คิว (นวดไทย 1)", text);
        Assert.Contains("• วันนี้ 18:00 โยคะ (ครูอ้อ) 8/10 ที่ (80%) · สำรอง 2", text);
        Assert.Contains("🧁 คิวผลิตตามสั่ง — 2 รายการ · ค้างชำระรวม 1,200.00 ฿", text);
        Assert.Contains("• ⚠️ เลยกำหนด 29/10/2026 · SO-0001 · คุณเอฟ", text);
        Assert.Contains("• พร้อมรับ 01/11/2026 · SO-0002 · คุณอี · ค้างชำระ 1,200.00 ฿", text);
        Assert.Contains("📦 ออเดอร์รอจัดส่ง — 1 รายการ", text);
        Assert.Contains("🏨 ที่พักวันนี้ — เช็คอิน 1 · เช็คเอาต์ 0", text);
        Assert.Contains("• CP-001 · คุณไอ · 10 ครั้ง · หมดอายุ 02/11/2026 · คงเหลือ 2.5", text);
        Assert.DoesNotContain("ดูเพิ่มเติม", text);   // ไม่ถูกตัด = ไม่มีท้ายตัด
    }

    [Fact]
    public void Builder_omits_null_sections_and_says_empty_for_empty_ones()
    {
        var d = new StaffDailySummaryPlan.SummaryData(Array.Empty<StaffDailySummaryPlan.BookingRow>(), null, null, null, null, null);
        var text = Assert.Single(StaffDailySummaryBuilder.Build(Today, "ร้าน", "เว็บหลัก", d, null));
        Assert.Contains("(เว็บหลัก)", text);
        Assert.Contains("📅 การจองวันนี้ — 0 คิว", text);
        Assert.Contains("• ไม่มีรายการ", text);
        Assert.DoesNotContain("คลาส", text);       // null = ไม่มีหัวข้อ (ไม่ใช่หัวข้อว่าง)
        Assert.DoesNotContain("ออเดอร์", text);
        Assert.DoesNotContain("ที่พัก", text);
    }

    [Fact]
    public void Builder_splits_within_line_limits_and_reports_omitted_items_with_link()
    {
        var many = Enumerable.Range(0, 3000).Select(i =>
            new StaffDailySummaryPlan.OrderRow(SiteA, $"SO-{i:D5}", "ลูกค้าชื่อยาวพอสมควรสำหรับทดสอบ " + i, null, 0m, "ยืนยันแล้ว")).ToList();
        var d = new StaffDailySummaryPlan.SummaryData(null, null, null, many, null, null);
        var msgs = StaffDailySummaryBuilder.Build(Today, "ร้าน", null, d, "https://app.example/");

        Assert.Equal(LineMessageLimits.MaxMessagesPerPush, msgs.Count);
        Assert.All(msgs, m => Assert.True(m.Length <= LineMessageLimits.MaxTextChars, $"ก้อนยาว {m.Length}"));
        var last = msgs[^1];
        Assert.Contains("ดูเพิ่มเติม: https://app.example/pages/cms-orders.html", last);

        // ที่แสดง + ที่บอกว่าเหลือ = ทั้งหมด (ไม่หายเงียบ)
        var shown = msgs.Sum(m => m.Split('\n').Count(l => l.StartsWith("• SO-")));
        var omitted = int.Parse(System.Text.RegularExpressions.Regex.Match(last, @"และอีก (\d+) รายการ").Groups[1].Value);
        Assert.Equal(StaffDailySummaryBuilder.ItemCount(Today, d), shown + omitted);
        Assert.Equal(3000, shown + omitted);
    }

    [Fact]
    public void Builder_without_base_url_names_the_page_instead_of_a_link()
    {
        var many = Enumerable.Range(0, 3000).Select(i =>
            new StaffDailySummaryPlan.CreditRow($"CP-{i:D5}", "ลูกค้าทดสอบ " + i, "แพ็กเกจยาวพอสมควร", Today.AddDays(1), 1m)).ToList();
        var msgs = StaffDailySummaryBuilder.Build(Today, "ร้าน", null, new StaffDailySummaryPlan.SummaryData(null, null, null, null, null, many), null);
        Assert.Contains("หน้า \"บัตรคอร์สใกล้หมดอายุ (7 วัน)\" ในระบบ", msgs[^1]);
    }

    [Fact]
    public void Line_limits_normalize_drops_blank_truncates_and_caps()
    {
        var input = new[] { "a", "", null, new string('x', 6000), "b", "c", "d", "e" };
        var n = LineMessageLimits.Normalize(input);
        Assert.Equal(5, n.Count);
        Assert.Equal("a", n[0]);
        Assert.Equal(LineMessageLimits.MaxTextChars, n[1].Length);
        Assert.EndsWith(LineMessageLimits.Ellipsis, n[1]);
        Assert.Equal("d", n[4]);   // ครบ 5 ก้อนแล้ว "e" ไม่ถูกส่ง
        Assert.Empty(LineMessageLimits.Normalize(null));
    }

    // ═════════ 2. กรองต่อผู้รับ (ไม่มีสิทธิ์ = ไม่มีหัวข้อ · เว็บที่ไม่เห็น = ไม่มีแถว) ═════════

    [Fact]
    public void Recipient_without_order_permission_gets_no_order_sections()
    {
        var scope = new StaffDailySummaryPlan.RecipientScope(Array.Empty<string>(), new[] { SiteA, SiteB }, new[] { SiteA, SiteB });
        var f = StaffDailySummaryPlan.FilterForRecipient(SampleData(), AllSections, scope, lodgingActive: true);
        Assert.Null(f.MadeToOrder);
        Assert.Null(f.ToShip);
        Assert.Null(f.Lodging);      // ไม่มี LodgingManage
        Assert.NotNull(f.Bookings);  // การจองอาศัยสิทธิ์รายเว็บ
        var text = string.Join("\n", StaffDailySummaryBuilder.Build(Today, "ร้าน", null, f, null));
        Assert.DoesNotContain("SO-000", text);
        Assert.DoesNotContain("คุณเอช", text);
    }

    [Fact]
    public void Recipient_sees_only_rows_of_sites_they_can_access()
    {
        var scope = new StaffDailySummaryPlan.RecipientScope(new[] { PermissionKeys.CmsOrderManage }, new[] { SiteA }, new[] { SiteA });
        var f = StaffDailySummaryPlan.FilterForRecipient(SampleData(), AllSections, scope, lodgingActive: false);
        Assert.All(f.Bookings!, b => Assert.Equal(SiteA, b.SiteId));
        Assert.Equal(3, f.Bookings!.Count);
        Assert.Single(f.MadeToOrder!);
        Assert.Empty(f.ToShip!);     // ออเดอร์รอส่งอยู่เว็บ B ⇒ หัวข้อมี แต่ไม่มีแถว
        var text = string.Join("\n", StaffDailySummaryBuilder.Build(Today, "ร้าน", null, f, null));
        Assert.DoesNotContain("คุณซี", text);   // การจองของเว็บ B
        Assert.DoesNotContain("SO-0001", text);
    }

    [Fact]
    public void Opposite_direction_full_permission_sees_everything_selected()
    {
        var f = StaffDailySummaryPlan.FilterForRecipient(SampleData(), AllSections, FullScope(), lodgingActive: true);
        Assert.Equal(4, f.Bookings!.Count);
        Assert.Equal(2, f.MadeToOrder!.Count);
        Assert.Single(f.ToShip!);
        Assert.Single(f.Lodging!);
        Assert.Single(f.Credits!);
    }

    [Fact]
    public void Unselected_or_inactive_modules_are_not_in_the_message()
    {
        var f = StaffDailySummaryPlan.FilterForRecipient(SampleData(), new[] { StaffDailySummaryPlan.Lodging, StaffDailySummaryPlan.Credits }, FullScope(), lodgingActive: false);
        Assert.Null(f.Bookings);
        Assert.Null(f.Lodging);      // บริษัทไม่มีที่พัก
        Assert.NotNull(f.Credits);
        Assert.True(f.HasAnySection);

        var none = StaffDailySummaryPlan.FilterForRecipient(SampleData(), new[] { StaffDailySummaryPlan.Bookings },
            new StaffDailySummaryPlan.RecipientScope(Array.Empty<string>(), Array.Empty<Guid>(), Array.Empty<Guid>()), true);
        Assert.False(none.HasAnySection);   // ไม่มีเว็บที่เห็น ⇒ ไม่มีอะไรส่ง (ไม่ใช่ข้อความเปล่า)
    }

    [Fact]
    public void Credits_section_accepts_any_of_its_keys()
    {
        foreach (var key in new[] { PermissionKeys.PosManager, PermissionKeys.PosCashier, PermissionKeys.CmsOrderManage })
        {
            var scope = new StaffDailySummaryPlan.RecipientScope(new[] { key }, Array.Empty<Guid>(), Array.Empty<Guid>());
            Assert.NotNull(StaffDailySummaryPlan.FilterForRecipient(SampleData(), AllSections, scope, false).Credits);
        }
        var noKey = new StaffDailySummaryPlan.RecipientScope(new[] { PermissionKeys.LodgingManage }, Array.Empty<Guid>(), Array.Empty<Guid>());
        Assert.Null(StaffDailySummaryPlan.FilterForRecipient(SampleData(), AllSections, noKey, false).Credits);
    }

    [Fact]
    public void Recipients_are_union_of_users_and_roles_among_members_only()
    {
        var owner = Guid.NewGuid(); var staff = Guid.NewGuid(); var acc = Guid.NewGuid(); var outsider = Guid.NewGuid();
        var members = new[] { (owner, UserRole.Owner), (staff, UserRole.Staff), (acc, UserRole.Accountant) };
        var r = StaffDailySummaryPlan.ResolveRecipients(members, new[] { staff, outsider }, new[] { UserRole.Owner });
        Assert.Equal(new[] { owner, staff }.OrderBy(x => x), r.OrderBy(x => x));
        Assert.DoesNotContain(outsider, r);   // ไม่ใช่สมาชิกแล้ว ⇒ ไม่ได้รับ
    }

    [Fact]
    public void Section_and_role_csv_round_trip_and_reject_unknown()
    {
        Assert.Equal("bookings,credits", StaffDailySummaryPlan.SectionsCsv(new[] { "credits", "BOOKINGS", "bookings", "nope" }));
        Assert.Equal(new[] { "nope" }, StaffDailySummaryPlan.UnknownSections(new[] { "credits", "nope" }));
        Assert.Equal(new[] { UserRole.Owner, UserRole.Staff }, StaffDailySummaryPlan.ParseRoles("Owner, staff,PlatformSupport,xx"));
        Assert.Equal(StaffDailySummaryPlan.Sections.Count, StaffDailySummaryPlan.ParseSections(StaffDailySummaryPlan.DefaultSectionsCsv).Count);
    }

    // ═════════ 3. เวลาไทย + watermark + สถานะแถวส่ง ═════════

    private static DateTime Bkk(int y, int mo, int d, int h, int mi) => BookingSlotEngine.BangkokNow(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc));

    [Fact]
    public void Due_uses_bangkok_time_window_and_watermark()
    {
        var sendAt = new TimeOnly(7, 30);
        Assert.False(StaffDailySummaryPlan.Due(Bkk(2026, 10, 30, 0, 29), sendAt, null));          // 07:29 ไทย
        Assert.True(StaffDailySummaryPlan.Due(Bkk(2026, 10, 30, 0, 30), sendAt, null));           // 07:30 ไทย
        Assert.False(StaffDailySummaryPlan.Due(Bkk(2026, 10, 30, 0, 35), sendAt, Today));          // ส่งแล้ววันนี้
        Assert.True(StaffDailySummaryPlan.Due(Bkk(2026, 10, 30, 15, 59), sendAt, Today.AddDays(-1))); // 22:59 ไทย ส่งตามหลังได้
        Assert.False(StaffDailySummaryPlan.Due(Bkk(2026, 10, 30, 16, 0), sendAt, Today.AddDays(-1)));  // 23:00 ไทย ไม่ส่งแล้ว
    }

    [Fact]
    public void Due_rolls_over_at_bangkok_midnight_not_utc()
    {
        // 17:10 UTC 30 ต.ค. = 00:10 ไทย 31 ต.ค. — วันใหม่แล้วตามเวลาไทย
        Assert.True(StaffDailySummaryPlan.Due(Bkk(2026, 10, 30, 17, 10), new TimeOnly(0, 5), Today));
        // 16:50 UTC 30 ต.ค. = 23:50 ไทย 30 ต.ค. — ยังเป็นวันเดิม ส่งแล้ว ⇒ ไม่ซ้ำ
        Assert.False(StaffDailySummaryPlan.Due(Bkk(2026, 10, 30, 16, 50), new TimeOnly(0, 5), Today));
    }

    [Fact]
    public void Send_time_rounds_down_to_five_minutes()
    {
        Assert.Equal(new TimeOnly(7, 30), StaffDailySummaryPlan.RoundSendAt(new TimeOnly(7, 34)));
        Assert.Equal(new TimeOnly(7, 35), StaffDailySummaryPlan.RoundSendAt(new TimeOnly(7, 35)));
        Assert.True(StaffDailySummaryPlan.Due(Bkk(2026, 10, 30, 0, 30), new TimeOnly(7, 34), null));   // 07:34 ปัดเป็น 07:30
    }

    [Fact]
    public void Terminal_status_decides_when_watermark_may_move()
    {
        Assert.True(StaffDailySummaryPlan.IsTerminal(StaffDailySummaryPlan.StatusSent, 1));
        Assert.True(StaffDailySummaryPlan.IsTerminal(StaffDailySummaryPlan.StatusNoLine, 1));
        Assert.True(StaffDailySummaryPlan.IsTerminal(StaffDailySummaryPlan.StatusNothingVisible, 1));
        // ฝ่ายค้านรอบ 212 S4: โควตาเต็มไม่ใช่จุดจบของวัน (เติมแพ็กแล้วรอบถัดไปส่งต่อจนถึง 23:00) — ไม่ขึ้นกับจำนวนครั้งที่ลอง
        Assert.False(StaffDailySummaryPlan.IsTerminal(StaffDailySummaryPlan.StatusQuotaExceeded, 1));
        Assert.False(StaffDailySummaryPlan.IsTerminal(StaffDailySummaryPlan.StatusQuotaExceeded, StaffDailySummaryPlan.MaxAttempts + 5));
        Assert.False(StaffDailySummaryPlan.IsTerminal(StaffDailySummaryPlan.StatusFailed, StaffDailySummaryPlan.MaxAttempts - 1));   // ยังลองใหม่ได้
        Assert.True(StaffDailySummaryPlan.IsTerminal(StaffDailySummaryPlan.StatusFailed, StaffDailySummaryPlan.MaxAttempts));
        Assert.False(StaffDailySummaryPlan.IsTerminal(StaffDailySummaryPlan.StatusSending, 1));   // กำลังส่ง ⇒ ยังไม่จบ
        Assert.Equal("ยังไม่ผูก LINE", StaffDailySummaryPlan.StatusLabel(StaffDailySummaryPlan.StatusNoLine));
    }

    [Fact]
    public void Message_key_is_one_per_config_day_recipient()
    {
        var c = Guid.NewGuid(); var u = Guid.NewGuid();
        Assert.Equal(StaffDailySummaryPlan.MessageKey(c, Today, u), StaffDailySummaryPlan.MessageKey(c, Today, u));
        Assert.NotEqual(StaffDailySummaryPlan.MessageKey(c, Today, u), StaffDailySummaryPlan.MessageKey(c, Today.AddDays(1), u));
        Assert.StartsWith("staffsum:", StaffDailySummaryPlan.MessageKey(c, Today, u));
    }

    // ═════════ 4. push ถึงพนักงานผ่าน OA กลาง (บั๊กแฝง §0) ═════════

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<(string? Auth, string Body)> Requests { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Headers.Authorization?.Parameter, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status) { Content = new StringContent("{\"message\":\"x\"}") };
        }
    }

    /// <summary>DbContext = null โดยตั้งใจ — เส้น OA กลางต้องไม่แตะค่าตั้งของบริษัทเลย (แตะ = NullReference = เทสต์ล้ม)</summary>
    private static LineNotifyService Line(CaptureHandler h, string? nextAccToken)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Line:ChannelAccessToken"] = nextAccToken }).Build();
        var factory = F4Proxy<IHttpClientFactory>.Create((m, a) => m == nameof(IHttpClientFactory.CreateClient) ? new HttpClient(h, disposeHandler: false) : null);
        var secrets = F4Proxy<ISecretProtector>.Create((m, a) => a.Length > 0 ? a[0] : null);
        return new LineNotifyService(factory, cfg, NullLogger<LineNotifyService>.Instance, null!, secrets);
    }

    [Fact]
    public async Task Staff_push_uses_nextacc_token_without_reading_company_settings()
    {
        var h = new CaptureHandler();
        var r = await Line(h, "NEXTACC-TOKEN").TryPushViaNextAccAsync("Ustaff123456", new[] { "สวัสดี", "", "ก้อนสอง" });
        Assert.True(r.Ok);
        var req = Assert.Single(h.Requests);
        Assert.Equal("NEXTACC-TOKEN", req.Auth);
        Assert.Contains("\"to\":\"Ustaff123456\"", req.Body);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(req.Body, "\"type\":\"text\"").Count);   // ก้อนว่างถูกทิ้ง
    }

    [Fact]
    public async Task Staff_push_without_nextacc_token_fails_with_reason_and_never_falls_back()
    {
        var h = new CaptureHandler();
        var r = await Line(h, null).TryPushViaNextAccAsync("Ustaff123456", new[] { "x" });
        Assert.False(r.Ok);
        Assert.Contains("NextAcc", r.Error);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Staff_push_reports_line_error_and_caps_five_messages()
    {
        var h = new CaptureHandler { Status = HttpStatusCode.BadRequest };
        var r = await Line(h, "T").TryPushViaNextAccAsync("Ustaff123456", Enumerable.Range(0, 8).Select(i => "m" + i).ToList());
        Assert.False(r.Ok);
        Assert.Contains("400", r.Error);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(h.Requests[0].Body, "\"type\":\"text\"").Count);
        var unbound = await Line(new CaptureHandler(), "T").TryPushViaNextAccAsync("", new[] { "x" });
        Assert.False(unbound.Ok);
    }

    [Fact]
    public async Task Customer_push_without_company_keeps_global_resolution_unchanged()
    {
        // ทิศตรงข้าม: เส้นลูกค้า/เดิม (companyId null) ยังเลือก token ผ่าน ResolveConfigAsync ตามเดิม — ไม่ถูกเปลี่ยนความหมาย
        var h = new CaptureHandler();
        Assert.True(await Line(h, "GLOBAL").TryPushToUserAsync(null, "Ucustomer", "ข้อความ"));
        Assert.Equal("GLOBAL", Assert.Single(h.Requests).Auth);
    }
}

using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 210 (คำตัดสินข้อ 169) — ต้องเป็นสมาชิกก่อนจอง (<see cref="BookingAccessRule"/>) · แขกค้นการจอง/คำสั่งซื้อด้วยเลขที่ + เบอร์/อีเมล
/// (<see cref="GuestRecordLookup"/>) · ลิงก์จัดการ/ติดตามในข้อความถึงลูกค้า (<see cref="CmsStorefrontLink"/>) ·
/// ทุกกลุ่มล็อกทั้งทิศ "ถูกกัน/ไม่ตรง" และทิศ "พฤติกรรมเดิมยังผ่าน"
/// </summary>
public class CmsRound210Tests
{
    // ═════════ 1. ต้องเป็นสมาชิกก่อนจอง — ค่าเริ่มต้น = เดิม (แขกจองได้) ═════════

    [Fact]
    public void Defaults_keep_guest_booking_open()
    {
        Assert.False(new SiteBookingSettings().RequireMemberToBook);
        Assert.Null(new SiteBookingService().MemberOnly);
        Assert.False(BookingAccessRule.RequiresMember(false, null));
        Assert.Null(BookingAccessRule.Problem(BookingAccessRule.RequiresMember(false, null), hasVerifiedCustomer: false, isStaff: false));
    }

    [Theory]
    // site, override ⇒ requires
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void Service_override_wins_over_site(bool site, bool? svc, bool expected)
        => Assert.Equal(expected, BookingAccessRule.RequiresMember(site, svc));

    [Theory]
    // requires, member, staff ⇒ blocked
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    public void Problem_matrix(bool requires, bool member, bool staff, bool blocked)
    {
        var p = BookingAccessRule.Problem(requires, member, staff);
        Assert.Equal(blocked, p != null);
        if (blocked) Assert.Contains("เข้าสู่ระบบ", p);   // บอกทางไปต่อ
    }

    [Fact]
    public void Member_rejection_is_401()
        => Assert.Equal(401, BookingAccessRule.MemberRequiredStatus);

    [Theory]
    [InlineData(BookingMemberAccess.Inherit, null)]
    [InlineData(BookingMemberAccess.Required, true)]
    [InlineData(BookingMemberAccess.NotRequired, false)]
    public void Member_access_round_trips(BookingMemberAccess access, bool? stored)
    {
        Assert.Equal(stored, BookingAccessRule.ToOverride(access));
        Assert.Equal(access, BookingAccessRule.FromOverride(stored));
    }

    [Fact]
    public void Options_come_from_server_with_site_state()
    {
        var on = BookingAccessRule.Options(true);
        Assert.Equal(new[] { "Inherit", "Required", "NotRequired" }, on.Select(o => o.Value));
        Assert.Contains("ต้องเป็นสมาชิก", on[0].Label);
        Assert.Contains("แขกจองได้", BookingAccessRule.Options(false)[0].Label);
    }

    [Fact]
    public void ApplySettings_null_member_access_keeps_existing_value()
    {
        var svc = new SiteBookingService { MemberOnly = true };
        CmsBookingService.ApplySettings(svc, new CreateBookingServiceRequest { Name = "นวด", MemberAccess = null });
        Assert.True(svc.MemberOnly);   // ผู้เรียก API เดิมไม่รีเซ็ตเงียบ
        CmsBookingService.ApplySettings(svc, new CreateBookingServiceRequest { Name = "นวด", MemberAccess = BookingMemberAccess.Inherit });
        Assert.Null(svc.MemberOnly);
        CmsBookingService.ApplySettings(svc, new CreateBookingServiceRequest { Name = "นวด", MemberAccess = BookingMemberAccess.NotRequired });
        Assert.False(svc.MemberOnly);
    }

    [Fact]
    public void ApplySettings_rejects_unknown_member_access()
        => Assert.Throws<BusinessRuleException>(() => CmsBookingService.ApplySettings(new SiteBookingService(),
            new CreateBookingServiceRequest { Name = "นวด", MemberAccess = (BookingMemberAccess)99 }));

    // ═════════ 2. ค้นด้วยเลขที่ + เบอร์/อีเมล ═════════

    private static readonly Guid Co = Guid.NewGuid(), Site = Guid.NewGuid();

    private static GuestLookupCandidate Row(string number = "BK-2026-0001", string? token = "tok_abcdefghijklmnopqrstu",
        string? email = "Somchai@Example.com", string? phone = "081-234-5678", Guid? company = null, Guid? site = null,
        string? customerEmail = null, string? customerPhone = null)
        => new(company ?? Co, site ?? Site, number, token, new[] { email, customerEmail }, new[] { phone, customerPhone });

    [Theory]
    [InlineData("BK-2026-0001", "0812345678")]
    [InlineData(" bk-2026-0001 ", "081 234 5678")]
    [InlineData("BK-2026-0001", "+66 81 234 5678")]
    [InlineData("BK-2026-0001", "  somchai@example.COM ")]
    public void Matches_after_normalization(string number, string contact)
        => Assert.Equal("tok_abcdefghijklmnopqrstu", GuestRecordLookup.Match(new[] { Row() }, Co, Site, number, contact));

    [Theory]
    [InlineData("BK-2026-0001", "0812345679")]            // เบอร์ผิดหนึ่งหลัก — ไม่ fuzzy
    [InlineData("BK-2026-0001", "somchai@example.co")]    // อีเมลผิด
    [InlineData("BK-2026-0002", "0812345678")]            // เลขที่ผิด
    [InlineData("BK-2026-0001", "")]                       // ไม่กรอกข้อมูลติดต่อ
    [InlineData("BK-2026-0001", "5678")]                   // เบอร์สั้น (ชนง่าย) ไม่ใช้จับคู่
    [InlineData("", "0812345678")]
    public void Wrong_or_missing_parts_do_not_match(string number, string contact)
        => Assert.Null(GuestRecordLookup.Match(new[] { Row() }, Co, Site, number, contact));

    [Fact]
    public void Other_company_or_site_never_matches()
    {
        Assert.Null(GuestRecordLookup.Match(new[] { Row(company: Guid.NewGuid()) }, Co, Site, "BK-2026-0001", "0812345678"));
        Assert.Null(GuestRecordLookup.Match(new[] { Row(site: Guid.NewGuid()) }, Co, Site, "BK-2026-0001", "0812345678"));
    }

    [Fact]
    public void Email_input_does_not_match_phone_field_and_vice_versa()
    {
        var r = Row(email: null, phone: "0812345678");
        Assert.Null(GuestRecordLookup.Match(new[] { r }, Co, Site, "BK-2026-0001", "0812345678@x.com"));
        Assert.Equal("tok_abcdefghijklmnopqrstu", GuestRecordLookup.Match(new[] { r }, Co, Site, "BK-2026-0001", "0812345678"));
    }

    [Fact]
    public void Linked_member_contact_also_matches()
        => Assert.NotNull(GuestRecordLookup.Match(new[] { Row(email: null, phone: null, customerEmail: "m@shop.th") }, Co, Site, "BK-2026-0001", "M@shop.th"));

    [Fact]
    public void Row_without_token_is_not_returned()
        => Assert.Null(GuestRecordLookup.Match(new[] { Row(token: null) }, Co, Site, "BK-2026-0001", "0812345678"));

    [Fact]
    public void Request_validation_and_number_bucket()
    {
        Assert.True(GuestRecordLookup.IsValidRequest("SO-1", "a@b.co"));
        Assert.False(GuestRecordLookup.IsValidRequest("SO-1", "abc"));
        Assert.False(GuestRecordLookup.IsValidRequest(new string('9', 65), "0812345678"));
        var bucket = GuestRecordLookup.NumberBucket(GuestRecordLookup.NormalizeNumber(" so-1 "));
        Assert.Equal(16, bucket!.Length);
        Assert.Equal(bucket, GuestRecordLookup.NumberBucket("SO-1"));
        // คีย์โควตาต่อเลขที่ต้องพอดี BucketKey varchar(120) แม้เลขยาวสุด (เดิมยาวเกิน ⇒ limiter fail-open เงียบ)
        var longest = $"m:cms-lookup-num:{Guid.NewGuid():N}:booking:{GuestRecordLookup.NumberBucket(new string('X', 64))}";
        Assert.True(longest.Length <= 120);
    }

    // ═════════ 3. ลิงก์ในข้อความถึงลูกค้า ═════════

    [Fact]
    public void Verified_custom_domain_wins()
        => Assert.Equal("https://shop.example.co.th", CmsStorefrontLink.BaseUrl(new[] { "Shop.Example.co.th" }, "https://app.nextacc.com", "myshop"));

    [Fact]
    public void Falls_back_to_path_based_app_url()
        => Assert.Equal("https://app.nextacc.com/site/myshop", CmsStorefrontLink.BaseUrl(Array.Empty<string>(), "https://app.nextacc.com/", "MyShop"));

    [Theory]
    [InlineData("javascript:alert(1)", "myshop")]
    [InlineData("", "myshop")]
    [InlineData("https://app.nextacc.com", "bad key/../x")]
    [InlineData(null, "myshop")]
    public void No_usable_base_gives_null(string? app, string key)
        => Assert.Null(CmsStorefrontLink.BaseUrl(new[] { "not a host", "" }, app, key));

    [Fact]
    public void Booking_line_has_escaped_manage_link()
    {
        var line = CmsStorefrontLink.BookingManageLine("https://shop.example.com", "ab_c-1", "BK-1");
        Assert.Contains("https://shop.example.com/my-booking/ab_c-1", line);
        Assert.Contains("/my-booking/a%20b", CmsStorefrontLink.BookingManageLine("https://x.th", "a b", "BK-1"));
    }

    [Fact]
    public void Order_line_has_track_link()
        => Assert.Contains("https://app.nextacc.com/site/s1/track-order/tok123", CmsStorefrontLink.OrderTrackLine("https://app.nextacc.com/site/s1", "tok123", "SO-9"));

    [Fact]
    public void No_base_url_falls_back_to_number_and_lookup_page_not_silent()
    {
        var b = CmsStorefrontLink.BookingManageLine(null, "tok", "BK-77");
        Assert.Contains("BK-77", b);
        Assert.Contains(CmsStorefrontLink.LookupPageName, b);
        var o = CmsStorefrontLink.OrderTrackLine("https://x.th", null, "SO-5");   // ใบเก่าไม่มีโทเคน ⇒ ชี้หน้าค้นของเว็บ
        Assert.Contains("https://x.th/check", o);
        Assert.Contains("SO-5", o);
    }

    [Fact]
    public void WithLine_appends_and_blank_keeps_message()
    {
        var m = new CmsNotification("s", "body");
        Assert.Equal("body\nline", CmsNotificationText.WithLine(m, "line").Text);
        Assert.Same(m, CmsNotificationText.WithLine(m, "  "));
    }

    [Fact]
    public void Rescheduled_text_has_old_and_new_time()
    {
        var n = CmsNotificationText.CustomerBookingRescheduled("ร้าน", "BK-1", "นวด", "สมชาย",
            new DateOnly(2026, 11, 1), new TimeOnly(10, 0), new DateOnly(2026, 11, 2), new TimeOnly(14, 30));
        Assert.Contains("10:00", n.Text);
        Assert.Contains("14:30", n.Text);
        Assert.Contains("BK-1", n.Subject);
    }

    // ═════════ ฝ่ายค้านรอบ 210: ถังต่อผู้ติดต่อ + IPv6 /64 ═════════

    [Fact]
    public void Contact_bucket_is_same_for_any_format_of_the_same_contact()
    {
        Assert.Equal(GuestRecordLookup.ContactBucket("081-234-5678"), GuestRecordLookup.ContactBucket("0812345678"));
        Assert.Equal(GuestRecordLookup.ContactBucket(" A@X.com "), GuestRecordLookup.ContactBucket("a@x.com"));
        Assert.NotEqual(GuestRecordLookup.ContactBucket("0812345678"), GuestRecordLookup.ContactBucket("0812345679"));
        Assert.Null(GuestRecordLookup.ContactBucket("hello"));
        Assert.Equal(16, GuestRecordLookup.ContactBucket("a@x.com")!.Length);   // พอดี BucketKey varchar(120)
    }

    [Fact]
    public void Ip_bucket_groups_ipv6_by_64_and_keeps_ipv4()
    {
        var a = GuestRecordLookup.IpBucket(System.Net.IPAddress.Parse("2001:db8:1:2::1"));
        var b = GuestRecordLookup.IpBucket(System.Net.IPAddress.Parse("2001:db8:1:2:ffff::9"));
        var other = GuestRecordLookup.IpBucket(System.Net.IPAddress.Parse("2001:db8:1:3::1"));
        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
        Assert.Equal("203.0.113.5", GuestRecordLookup.IpBucket(System.Net.IPAddress.Parse("203.0.113.5")));
        Assert.Equal("203.0.113.5", GuestRecordLookup.IpBucket(System.Net.IPAddress.Parse("::ffff:203.0.113.5")));
        Assert.Equal("unknown", GuestRecordLookup.IpBucket(null));
    }
}

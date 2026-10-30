using System.Text;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 217 (คำตัดสินข้อ 175) — "กดจองเบิ้ลสองที บันทึกการจองซ้ำได้" + "กดยืนยันแล้วนิ่ง":
/// <list type="bullet">
///   <item><see cref="IdempotencyKeyPolicy"/> — รูปคีย์ · namespace ผู้เรียก · แฮชคำขอ · ตัดสิน replay/in-flight/mismatch/หมดอายุ</item>
///   <item><see cref="DuplicateSubmitGuard"/> — ผู้จองคนเดียวกัน (สมาชิก/เบอร์/อีเมล) · ทิศตรงข้าม (คนละคน/สมาชิกต่างคน) · เปิดใบเดิมให้ใคร</item>
///   <item><see cref="DeferredNotifyPolicy"/> — ชนิดคิว · ถอย/ยอมแพ้</item>
/// </list>
/// ระดับฐานจริง (คำขอพร้อมกัน) อยู่ที่ <c>Db/SubmitOnceRound217DbTests</c>
/// </summary>
public class SubmitOnceRound217Tests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    // ═════════ IdempotencyKeyPolicy ═════════

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("   ", null, null)]
    [InlineData("short", null, IdempotencyKeyPolicy.BadKeyMessage)]
    [InlineData("  0f8fad5b-d9cb-469f-a165-70867728950e  ", "0f8fad5b-d9cb-469f-a165-70867728950e", null)]
    public void Normalize_accepts_uuid_trims_and_rejects_bad_length(string? raw, string? key, string? problem)
    {
        var (k, p) = IdempotencyKeyPolicy.Normalize(raw);
        Assert.Equal(key, k);
        Assert.Equal(problem, p);
    }

    [Fact]
    public void Normalize_rejects_overlong_key()
        => Assert.Equal(IdempotencyKeyPolicy.BadKeyMessage, IdempotencyKeyPolicy.Normalize(new string('a', 129)).Problem);

    [Fact]
    public void Subject_prefers_claim_then_user_then_credential_hash_then_ip()
    {
        Assert.Equal("c1", IdempotencyKeyPolicy.Subject("c1", "u", "Bearer x", "1.1.1.1"));
        Assert.Equal("u", IdempotencyKeyPolicy.Subject(null, "u", "Bearer x", "1.1.1.1"));
        var cred = IdempotencyKeyPolicy.Subject(null, null, "Bearer x", "1.1.1.1");
        Assert.StartsWith("cred:", cred);
        Assert.DoesNotContain("Bearer", cred);   // ไม่เก็บ credential ดิบในตาราง
        Assert.Equal("anon:1.1.1.1", IdempotencyKeyPolicy.Subject(null, null, null, "1.1.1.1"));
        // สมาชิกคนละโทเคน ⇒ คนละ namespace (คีย์ชนกันก็ไม่เห็นผลของกัน)
        Assert.NotEqual(cred, IdempotencyKeyPolicy.Subject(null, null, "Bearer y", "1.1.1.1"));
    }

    [Fact]
    public void CacheKey_is_bounded_to_column_length()
        => Assert.Equal(IdempotencyKeyPolicy.MaxCacheKeyLength,
            IdempotencyKeyPolicy.CacheKey("s", "POST", "/api/" + new string('x', 600), "k12345678").Length);

    private static readonly byte[] K = IdempotencyKeyPolicy.DeriveHashKey("test-secret-0123456789-0123456789-abcdef");

    [Fact]
    public void RequestHash_same_body_same_hash_and_streaming_equals_one_shot()
    {
        var body = Encoding.UTF8.GetBytes("{\"bookingServiceId\":\"a\",\"startTime\":\"19:00\"}");
        var a = IdempotencyKeyPolicy.RequestHash(K, "POST", "/api/x", body);
        Assert.Equal(a, IdempotencyKeyPolicy.RequestHash(K, "post", "/api/x", body));
        using var h = IdempotencyKeyPolicy.BeginRequestHash(K, "POST", "/api/x");
        h.AppendData(body, 0, 10);
        h.AppendData(body, 10, body.Length - 10);
        Assert.Equal(a, Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant());
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void RequestHash_differs_on_body_path_or_query()
    {
        var b1 = Encoding.UTF8.GetBytes("{\"startTime\":\"19:00\"}");
        var b2 = Encoding.UTF8.GetBytes("{\"startTime\":\"20:00\"}");
        var h = IdempotencyKeyPolicy.RequestHash(K, "POST", "/api/x", b1);
        Assert.NotEqual(h, IdempotencyKeyPolicy.RequestHash(K, "POST", "/api/x", b2));
        Assert.NotEqual(h, IdempotencyKeyPolicy.RequestHash(K, "POST", "/api/y", b1));
        Assert.NotEqual(h, IdempotencyKeyPolicy.RequestHash(K, "POST", "/api/x?a=1", b1));
    }

    [Fact]
    public void S6_hash_is_keyed_hmac_not_plain_sha256_and_depends_on_server_secret()
    {
        var body = Encoding.UTF8.GetBytes("{\"password\":\"hunter2\"}");
        var keyed = IdempotencyKeyPolicy.RequestHash(K, "POST", "/api/x", body);
        var plain = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("POST\n/api/x\n").Concat(body).ToArray())).ToLowerInvariant();
        Assert.NotEqual(plain, keyed);   // เดาย้อนจากแฮชเปล่าไม่ได้
        var other = IdempotencyKeyPolicy.DeriveHashKey("another-secret-0123456789-0123456789-xyz");
        Assert.NotEqual(keyed, IdempotencyKeyPolicy.RequestHash(other, "POST", "/api/x", body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void S6_missing_server_secret_fails_fast(string? secret)
        => Assert.Equal(IdempotencyKeyPolicy.MissingSecretMessage,
            Assert.Throws<InvalidOperationException>(() => IdempotencyKeyPolicy.DeriveHashKey(secret)).Message);

    [Theory]
    [InlineData("/api/companies/x/cms/sites/y/portal/register", true)]
    [InlineData("/api/companies/x/cms/sites/y/portal/login", true)]
    [InlineData("/api/auth/refresh", true)]
    [InlineData("/api/auth/reset-password", true)]
    [InlineData("/api/companies/x/cms/sites/y/booking/bookings", false)]
    [InlineData("/api/companies/x/cms/sites/y/booking/bookings/by-token/abc/cancel", false)]
    [InlineData("/api/companies/x/cms/sites/y/lodging/reservations/abc/slip", false)]
    public void S6_credential_paths_are_excluded(string path, bool excluded)
        => Assert.Equal(excluded, IdempotencyKeyPolicy.IsCredentialPath(path));

    private static readonly byte[] FileA = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("slip-image-a"));
    private static readonly byte[] FileB = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("slip-image-b"));

    [Fact]
    public void S2_multipart_hash_ignores_field_order_and_boundary_but_not_file_content()
    {
        var f1 = new[] { new IdempotencyKeyPolicy.MultipartField("amount", "500"), new IdempotencyKeyPolicy.MultipartField("note", "โอนแล้ว") };
        var f2 = f1.Reverse().ToArray();
        var file = new[] { new IdempotencyKeyPolicy.MultipartFile("slip", "a.jpg", "image/jpeg", FileA) };
        var h1 = IdempotencyKeyPolicy.MultipartHash(K, "POST", "/api/s", f1, file);
        Assert.Equal(h1, IdempotencyKeyPolicy.MultipartHash(K, "POST", "/api/s", f2, file));   // ลำดับช่อง/boundary ไม่มีผล
        Assert.NotEqual(h1, IdempotencyKeyPolicy.MultipartHash(K, "POST", "/api/s", f1,
            new[] { new IdempotencyKeyPolicy.MultipartFile("slip", "a.jpg", "image/jpeg", FileB) }));        // สลิปใบอื่น = คำขออื่น
        Assert.NotEqual(h1, IdempotencyKeyPolicy.MultipartHash(K, "POST", "/api/s",
            new[] { new IdempotencyKeyPolicy.MultipartField("amount", "600"), f1[1] }, file));
    }

    [Theory]
    [InlineData(200, "application/json", "{\"success\":true,\"data\":1}", true)]
    [InlineData(201, "application/json; charset=utf-8", "{\"success\":true}", true)]
    [InlineData(200, "application/json", "{\"success\":false,\"message\":\"x\"}", false)]
    [InlineData(200, "application/json", "{\"Success\":false}", false)]
    [InlineData(200, "text/plain", "ok", true)]
    [InlineData(200, "application/json", "not json", true)]
    [InlineData(409, "application/json", "{\"success\":false}", false)]
    [InlineData(500, "application/json", "{}", false)]
    public void Note1_store_only_success_results(int status, string contentType, string body, bool store)
        => Assert.Equal(store, IdempotencyKeyPolicy.ShouldStore(status, contentType, Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void Decide_done_same_hash_replays()
        => Assert.Equal(IdempotencyKeyPolicy.Decision.Replay, IdempotencyKeyPolicy.Decide("Done", "h", true, 60, "h"));

    [Fact]
    public void Decide_inflight_same_hash_waits()
        => Assert.Equal(IdempotencyKeyPolicy.Decision.InFlight, IdempotencyKeyPolicy.Decide("InFlight", "h", false, 1, "h"));

    [Fact]
    public void S2_inflight_with_different_body_waits_instead_of_mismatch()
        => Assert.Equal(IdempotencyKeyPolicy.Decision.InFlight, IdempotencyKeyPolicy.Decide("InFlight", "h1", false, 5, "h2"));

    [Fact]
    public void Decide_done_with_different_body_is_mismatch()
        => Assert.Equal(IdempotencyKeyPolicy.Decision.Mismatch, IdempotencyKeyPolicy.Decide("Done", "h1", true, 60, "h2"));

    [Fact]
    public void Decide_legacy_row_without_hash_still_replays()
        => Assert.Equal(IdempotencyKeyPolicy.Decision.Replay, IdempotencyKeyPolicy.Decide("Done", null, true, 60, "h"));

    [Fact]
    public void Decide_expired_row_is_reclaimed_even_with_other_body()
        => Assert.Equal(IdempotencyKeyPolicy.Decision.Reclaim,
            IdempotencyKeyPolicy.Decide("Done", "h1", true, IdempotencyKeyPolicy.Retention.TotalSeconds + 1, "h2"));

    [Fact]
    public void S5_stale_inflight_is_reclaimed_fresh_inflight_is_not()
    {
        Assert.Equal(IdempotencyKeyPolicy.Decision.Reclaim,
            IdempotencyKeyPolicy.Decide("InFlight", "h", false, IdempotencyKeyPolicy.StaleInFlightAfter.TotalSeconds + 1, "h"));
        Assert.Equal(IdempotencyKeyPolicy.Decision.InFlight,
            IdempotencyKeyPolicy.Decide("InFlight", "h", false, IdempotencyKeyPolicy.StaleInFlightAfter.TotalSeconds - 1, "h"));
        // ทิศตรงข้าม: Done เก่ากว่า 2 นาทียังเป็นผลที่ replay ได้ (ยึดคืนเฉพาะ InFlight)
        Assert.Equal(IdempotencyKeyPolicy.Decision.Replay,
            IdempotencyKeyPolicy.Decide("Done", "h", true, IdempotencyKeyPolicy.StaleInFlightAfter.TotalSeconds + 60, "h"));
    }

    // ═════════ DuplicateSubmitGuard ═════════

    [Fact]
    public void IdentityOf_normalizes_phone_and_email_and_null_when_nothing_to_compare()
    {
        var a = DuplicateSubmitGuard.IdentityOf(null, "+66 81-234-5678", " Somchai@Example.COM ");
        Assert.Equal("0812345678", a!.Phone);
        Assert.Equal("somchai@example.com", a.Email);
        Assert.Null(DuplicateSubmitGuard.IdentityOf(null, "1234", "not-an-email"));   // เบอร์สั้น/อีเมลผิดรูป = ไม่มีตัวตนให้เทียบ (ห้ามเดา)
        Assert.Null(DuplicateSubmitGuard.IdentityOf(null, null, null));
    }

    /// <summary>ผู้จองคนเดียวกันไหม — ผ่านทางเข้าจริงตัวเดียว (FindDuplicate บนแถวเดียว)</summary>
    private static bool Same(Guid? c1, string? p1, string? e1, Guid? c2, string? p2, string? e2)
        => DuplicateSubmitGuard.FindDuplicate(new[] { new Row(1, c1, p1, e1) },
            r => DuplicateSubmitGuard.IdentityOf(r.CustomerId, r.Phone, r.Email), DuplicateSubmitGuard.IdentityOf(c2, p2, e2)) != null;

    [Fact]
    public void Same_guest_by_phone_in_any_format_is_same_requester()
        => Assert.True(Same(null, "081-234-5678", null, null, "66812345678", "x@y.z"));

    [Fact]
    public void Same_guest_by_email_case_insensitive_is_same_requester()
        => Assert.True(Same(null, null, "A@X.th", null, "0899999999", "a@x.th"));

    [Fact]
    public void Same_member_is_same_requester_even_without_phone()
    {
        var m = Guid.NewGuid();
        Assert.True(Same(m, null, null, m, "0812345678", null));
    }

    [Fact]
    public void Two_different_members_sharing_a_phone_are_not_duplicates()
        => Assert.False(Same(Guid.NewGuid(), "0812345678", null, Guid.NewGuid(), "0812345678", null));

    [Fact]
    public void Different_guests_are_not_duplicates()
        => Assert.False(Same(null, "0812345678", "a@x.th", null, "0899999999", "b@x.th"));

    [Fact]
    public void FindDuplicate_returns_first_matching_in_caller_order_or_null()
    {
        var rows = new[]
        {
            new Row(1, null, "0899999999", null),
            new Row(2, null, "0812345678", null),
            new Row(3, null, "0812345678", null),
        };
        var me = DuplicateSubmitGuard.IdentityOf(null, "081 234 5678", null);
        Assert.Equal(2, DuplicateSubmitGuard.FindDuplicate(rows, r => DuplicateSubmitGuard.IdentityOf(r.CustomerId, r.Phone, r.Email), me)!.N);
        Assert.Null(DuplicateSubmitGuard.FindDuplicate(rows, r => DuplicateSubmitGuard.IdentityOf(r.CustomerId, r.Phone, r.Email), null));
    }

    private sealed record Row(int N, Guid? CustomerId, string? Phone, string? Email);

    [Fact]
    public void S1_outcome_member_owner_gets_existing_staff_and_guest_get_409()
    {
        var owner = Guid.NewGuid();
        Assert.Equal(DuplicateSubmitGuard.Outcome.ReturnExisting, DuplicateSubmitGuard.Decide(false, owner, owner));
        Assert.Equal(DuplicateSubmitGuard.Outcome.ConflictForStaff, DuplicateSubmitGuard.Decide(staff: true, owner, owner));   // ไม่คืนเงียบ ๆ ให้พนักงาน/API
        Assert.Equal(DuplicateSubmitGuard.Outcome.ConflictForGuest, DuplicateSubmitGuard.Decide(false, null, owner));          // แขกพิมพ์เบอร์ของคนอื่น
        Assert.Equal(DuplicateSubmitGuard.Outcome.ConflictForGuest, DuplicateSubmitGuard.Decide(false, Guid.NewGuid(), owner));
        Assert.Equal(DuplicateSubmitGuard.Outcome.ConflictForGuest, DuplicateSubmitGuard.Decide(false, owner, null));          // ใบเดิมของแขก
        Assert.Contains("BK-2610-0003", DuplicateSubmitGuard.BookingStaffMessage("BK-2610-0003"));
    }

    [Fact]
    public void S1_same_shape_requires_same_units_guests_and_requested_resource()
    {
        var r = Guid.NewGuid();
        Assert.True(DuplicateSubmitGuard.SameBookingShape(1, 1, null, 1, 1, r));     // ไม่ระบุผู้ให้บริการ (ระบบจัดให้) = รูปเดียวกัน
        Assert.True(DuplicateSubmitGuard.SameBookingShape(1, 1, r, 1, 1, r));
        Assert.False(DuplicateSubmitGuard.SameBookingShape(1, 1, Guid.NewGuid(), 1, 1, r));   // ที่นั่งที่สองกับผู้ให้บริการอีกคน
        Assert.False(DuplicateSubmitGuard.SameBookingShape(1, 2, null, 1, 1, null));          // จำนวนคนต่าง
        Assert.False(DuplicateSubmitGuard.SameBookingShape(2, 1, null, 1, 1, null));          // จำนวนช่วงต่าง
    }

    [Fact]
    public void Window_is_ten_minutes()
        => Assert.Equal(Now.AddMinutes(-10), DuplicateSubmitGuard.WindowStart(Now));

    // ═════════ DeferredNotifyPolicy ═════════

    [Fact]
    public void New_row_is_due_now_and_rejects_unknown_kind()
    {
        var cid = Guid.NewGuid(); var sid = Guid.NewGuid(); var id = Guid.NewGuid();
        var row = DeferredNotifyPolicy.New(cid, sid, DeferredNotifyPolicy.BookingCreated, id, Now);
        Assert.Equal((cid, sid, id, Now, 0), (row.CompanyId, row.SiteId, row.EntityId, row.DueAt, row.Attempts));
        Assert.Null(row.DoneAt);
        Assert.Throws<ArgumentException>(() => DeferredNotifyPolicy.New(cid, sid, "booking.whatever", id, Now));
    }

    [Fact]
    public void AfterFailure_backs_off_then_gives_up_at_max_attempts()
    {
        Assert.Equal((false, Now.AddSeconds(30)), DeferredNotifyPolicy.AfterFailure(1, Now));
        Assert.Equal((false, Now.AddMinutes(1)), DeferredNotifyPolicy.AfterFailure(2, Now));
        Assert.Equal((true, Now), DeferredNotifyPolicy.AfterFailure(DeferredNotifyPolicy.MaxAttempts, Now));
    }

    [Fact]
    public void S4_skip_cancelled_noshow_deleted_bookings_send_others()
    {
        Assert.NotNull(DeferredNotifyPolicy.BookingSkipReason(Accounting.Models.Enums.BookingStatus.Cancelled));
        Assert.NotNull(DeferredNotifyPolicy.BookingSkipReason(Accounting.Models.Enums.BookingStatus.NoShow));
        Assert.NotNull(DeferredNotifyPolicy.BookingSkipReason(null));
        Assert.Null(DeferredNotifyPolicy.BookingSkipReason(Accounting.Models.Enums.BookingStatus.Pending));
        Assert.Null(DeferredNotifyPolicy.BookingSkipReason(Accounting.Models.Enums.BookingStatus.Confirmed));
        Assert.Null(DeferredNotifyPolicy.BookingSkipReason(Accounting.Models.Enums.BookingStatus.Waitlisted));
    }

    [Fact]
    public void S4_skip_cancelled_refunded_deleted_orders_send_others()
    {
        Assert.NotNull(DeferredNotifyPolicy.OrderSkipReason(Accounting.Models.Enums.SiteOrderStatus.Cancelled));
        Assert.NotNull(DeferredNotifyPolicy.OrderSkipReason(Accounting.Models.Enums.SiteOrderStatus.Refunded));
        Assert.NotNull(DeferredNotifyPolicy.OrderSkipReason(null));
        Assert.Null(DeferredNotifyPolicy.OrderSkipReason(Accounting.Models.Enums.SiteOrderStatus.Pending));
        Assert.Null(DeferredNotifyPolicy.OrderSkipReason(Accounting.Models.Enums.SiteOrderStatus.Confirmed));
        Assert.Null(DeferredNotifyPolicy.OrderSkipReason(Accounting.Models.Enums.SiteOrderStatus.PartiallyRefunded));
    }

    [Fact]
    public void Truncate_keeps_error_visible_and_bounded()
    {
        Assert.Equal("ไม่ทราบสาเหตุ", DeferredNotifyPolicy.Truncate(null));
        Assert.Equal(DeferredNotifyPolicy.MaxErrorLength, DeferredNotifyPolicy.Truncate(new string('x', 900)).Length);
    }
}

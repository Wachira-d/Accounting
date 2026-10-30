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

    [Fact]
    public void RequestHash_same_body_same_hash_and_streaming_equals_one_shot()
    {
        var body = Encoding.UTF8.GetBytes("{\"bookingServiceId\":\"a\",\"startTime\":\"19:00\"}");
        var a = IdempotencyKeyPolicy.RequestHash("POST", "/api/x", body);
        Assert.Equal(a, IdempotencyKeyPolicy.RequestHash("post", "/api/x", body));
        using var h = IdempotencyKeyPolicy.BeginRequestHash("POST", "/api/x");
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
        var h = IdempotencyKeyPolicy.RequestHash("POST", "/api/x", b1);
        Assert.NotEqual(h, IdempotencyKeyPolicy.RequestHash("POST", "/api/x", b2));
        Assert.NotEqual(h, IdempotencyKeyPolicy.RequestHash("POST", "/api/y", b1));
        Assert.NotEqual(h, IdempotencyKeyPolicy.RequestHash("POST", "/api/x?a=1", b1));
    }

    [Fact]
    public void Decide_done_same_hash_replays()
        => Assert.Equal(IdempotencyKeyPolicy.Decision.Replay, IdempotencyKeyPolicy.Decide("Done", "h", true, Now.AddMinutes(-1), "h", Now));

    [Fact]
    public void Decide_inflight_same_hash_waits()
        => Assert.Equal(IdempotencyKeyPolicy.Decision.InFlight, IdempotencyKeyPolicy.Decide("InFlight", "h", false, Now.AddSeconds(-1), "h", Now));

    [Fact]
    public void Decide_different_body_is_mismatch_even_when_done_or_inflight()
    {
        Assert.Equal(IdempotencyKeyPolicy.Decision.Mismatch, IdempotencyKeyPolicy.Decide("Done", "h1", true, Now.AddMinutes(-1), "h2", Now));
        Assert.Equal(IdempotencyKeyPolicy.Decision.Mismatch, IdempotencyKeyPolicy.Decide("InFlight", "h1", false, Now.AddMinutes(-1), "h2", Now));
    }

    [Fact]
    public void Decide_legacy_row_without_hash_still_replays()
        => Assert.Equal(IdempotencyKeyPolicy.Decision.Replay, IdempotencyKeyPolicy.Decide("Done", null, true, Now.AddMinutes(-1), "h", Now));

    [Fact]
    public void Decide_expired_row_is_reclaimed_even_with_other_body()
        => Assert.Equal(IdempotencyKeyPolicy.Decision.Reclaim,
            IdempotencyKeyPolicy.Decide("Done", "h1", true, Now - IdempotencyKeyPolicy.Retention - TimeSpan.FromSeconds(1), "h2", Now));

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
    public void Reveal_only_to_staff_or_owning_member_never_to_anonymous_phone_match()
    {
        var owner = Guid.NewGuid();
        Assert.True(DuplicateSubmitGuard.Reveal(staff: true, null, owner));
        Assert.True(DuplicateSubmitGuard.Reveal(false, owner, owner));
        Assert.False(DuplicateSubmitGuard.Reveal(false, null, owner));          // แขกพิมพ์เบอร์ของคนอื่น — ไม่เปิดใบ/โทเคน
        Assert.False(DuplicateSubmitGuard.Reveal(false, Guid.NewGuid(), owner)); // สมาชิกอื่นที่ใช้เบอร์เดียวกัน
        Assert.False(DuplicateSubmitGuard.Reveal(false, owner, null));          // ใบเดิมเป็นของแขก (ไม่มีเจ้าของสมาชิก)
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
    public void Truncate_keeps_error_visible_and_bounded()
    {
        Assert.Equal("ไม่ทราบสาเหตุ", DeferredNotifyPolicy.Truncate(null));
        Assert.Equal(DeferredNotifyPolicy.MaxErrorLength, DeferredNotifyPolicy.Truncate(new string('x', 900)).Length);
    }
}

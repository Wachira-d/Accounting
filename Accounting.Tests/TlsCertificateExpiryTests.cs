using Accounting.Helpers;
using Xunit;
using L = Accounting.Helpers.TlsCertificateExpiry.Level;

namespace Accounting.Tests;

/// <summary>ระดับเตือนใบรับรอง SSL — เจ้าของรายงาน 2026-10-19 เว็บเข้าไม่ได้ทั้งระบบเพราะ cert หมดอายุ (เตือนตอนหมดแล้วไม่มีใครเห็น ⇒ เตือนล่วงหน้า)</summary>
public class TlsCertificateExpiryTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 3, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(90, L.Ok)]
    [InlineData(31, L.Ok)]
    [InlineData(30, L.Notice)]
    [InlineData(15, L.Notice)]
    [InlineData(14, L.Warning)]
    [InlineData(8, L.Warning)]
    [InlineData(7, L.Urgent)]
    [InlineData(2, L.Urgent)]
    [InlineData(1, L.Critical)]
    public void Classify_by_days_left(int days, L expected)
        => Assert.Equal(expected, TlsCertificateExpiry.LevelFor(Now.AddDays(days).AddMinutes(1), Now));

    [Fact]
    public void Expired_and_unknown()
    {
        Assert.Equal(L.Expired, TlsCertificateExpiry.LevelFor(Now.AddMinutes(-1), Now));
        Assert.Equal(L.Expired, TlsCertificateExpiry.LevelFor(Now, Now));
        Assert.Equal(L.Unknown, TlsCertificateExpiry.LevelFor(null, Now));   // ไม่รู้ ≠ ปกติ
    }

    [Fact]
    public void Notifies_once_per_step_down_and_resets_after_renewal()
    {
        Assert.False(TlsCertificateExpiry.ShouldNotify(L.Ok, null));
        Assert.False(TlsCertificateExpiry.ShouldNotify(L.Unknown, null));
        Assert.True(TlsCertificateExpiry.ShouldNotify(L.Notice, null));
        Assert.False(TlsCertificateExpiry.ShouldNotify(L.Notice, L.Notice));        // วันถัดไปยังอยู่ขั้นเดิม ⇒ ไม่แจ้งซ้ำทุกวัน
        Assert.True(TlsCertificateExpiry.ShouldNotify(L.Warning, L.Notice));
        Assert.True(TlsCertificateExpiry.ShouldNotify(L.Expired, L.Critical));
        Assert.False(TlsCertificateExpiry.ShouldNotify(L.Notice, L.Urgent));        // (ไม่ควรเกิด) ระดับดีขึ้นแต่ยังไม่ Ok ⇒ ไม่แจ้ง
    }

    [Fact]
    public void Messages_say_what_to_do_and_stay_silent_when_ok()
    {
        Assert.Null(TlsCertificateExpiry.Message(L.Ok, Now.AddDays(60), Now, "nextacc.net", null));
        Assert.Null(TlsCertificateExpiry.Message(L.Unknown, null, Now, "nextacc.net", null));
        var soon = TlsCertificateExpiry.Message(L.Warning, Now.AddDays(10).AddHours(2), Now, "nextacc.net", null)!;
        Assert.Contains("nextacc.net", soon);
        Assert.Contains("อีก 10 วัน", soon);
        Assert.Contains("IIS", soon);
        Assert.Contains("หมดอายุแล้ว", TlsCertificateExpiry.Message(L.Expired, Now.AddDays(-2), Now, "nextacc.net", null));
        Assert.Contains("ตรวจวันหมดอายุ", TlsCertificateExpiry.Message(L.Unknown, null, Now, "nextacc.net", "timeout"));
    }

    [Theory]
    [InlineData(L.Expired, false, "danger")]
    [InlineData(L.Critical, false, "danger")]
    [InlineData(L.Urgent, false, "danger")]
    [InlineData(L.Warning, false, "warning")]
    [InlineData(L.Notice, false, "warning")]
    [InlineData(L.Ok, false, "none")]
    [InlineData(L.Unknown, true, "info")]      // ตรวจไม่ได้ ≠ ปกติ — แถบสีฟ้าบอกเหตุผล
    [InlineData(L.Unknown, false, "none")]
    public void Severity_is_decided_by_server(L level, bool hasError, string expected)
        => Assert.Equal(expected, TlsCertificateExpiry.Severity(level, hasError));

    [Fact]
    public void Message_names_the_issuer_so_the_admin_knows_where_to_renew()
    {
        var m = TlsCertificateExpiry.Message(L.Urgent, Now.AddDays(3), Now, "nextacc.net", null, "CN=R11, O=Let's Encrypt")!;
        Assert.Contains("ผู้ออก CN=R11, O=Let's Encrypt", m);
        Assert.DoesNotContain("ผู้ออก", TlsCertificateExpiry.Message(L.Urgent, Now.AddDays(3), Now, "nextacc.net", null, null)!);
    }

    [Fact]
    public void Stale_check_means_the_daily_job_stopped()
    {
        Assert.False(TlsCertificateExpiry.IsStale(null, Now));                 // ยังไม่เคยตรวจ (job เพิ่งเริ่ม)
        Assert.False(TlsCertificateExpiry.IsStale(Now.AddDays(-1), Now));
        Assert.False(TlsCertificateExpiry.IsStale(Now.AddDays(-3), Now));      // ขอบ = ยังไม่ค้าง
        Assert.True(TlsCertificateExpiry.IsStale(Now.AddDays(-3).AddMinutes(-1), Now));
        Assert.Contains("ไม่ได้รัน", TlsCertificateExpiry.StaleNote(Now.AddDays(-5)));
    }

    [Fact]
    public void Renewed_or_moved_certificate_restarts_the_warning_steps()
    {
        var exp = Now.AddDays(5);
        Assert.False(TlsCertificateExpiry.IsNewCertificate("nextacc.net", exp, "nextacc.net", exp));          // ใบเดิม ⇒ คงระดับที่แจ้งแล้ว
        Assert.False(TlsCertificateExpiry.IsNewCertificate("nextacc.net", exp, "NEXTACC.net", null));         // ตรวจไม่ได้รอบนี้ ≠ ใบใหม่
        Assert.True(TlsCertificateExpiry.IsNewCertificate("nextacc.net", exp, "nextacc.net", exp.AddDays(6))); // ใบ 6 วันต่ออายุ ⇒ เตือนใหม่ได้
        Assert.True(TlsCertificateExpiry.IsNewCertificate("old.example", exp, "nextacc.net", exp));           // ย้ายโดเมน
    }

    [Fact]
    public void Thai_date_uses_buddhist_year_and_bangkok_time()
        // 2026-10-19 03:00 UTC = 19/10/2569 10:00 น. เวลาไทย
        => Assert.Equal("19/10/2569 10:00 น.", TlsCertificateExpiry.ThaiDate(Now));

    [Theory]
    [InlineData("https://nextacc.net", "nextacc.net", 443)]
    [InlineData("https://nextacc.net/", "nextacc.net", 443)]
    [InlineData("https://app.example.co.th:8443/x", "app.example.co.th", 8443)]
    public void Target_from_https_base_url(string url, string host, int port)
        => Assert.Equal((host, port), TlsCertificateExpiry.Target(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://nextacc.net")]   // ไม่ใช่ https ⇒ ไม่มีใบรับรองให้ตรวจ
    [InlineData("nextacc.net")]
    public void No_target_without_https(string? url)
        => Assert.Null(TlsCertificateExpiry.Target(url));
}

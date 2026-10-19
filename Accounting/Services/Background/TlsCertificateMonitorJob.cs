using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// ตรวจวันหมดอายุใบรับรอง SSL ของโดเมนแพลตฟอร์ม (<c>SiteSettings.AppBaseUrl</c>) วันละครั้ง — เก็บผลลง SiteSettings ให้
/// แถบเตือนบนหน้าเว็บอ่าน · ข้ามขั้น 30/14/7/1 วัน/หมดแล้ว ⇒ แจ้งเตือนในระบบ + <b>อีเมล</b> ถึงผู้ดูแลระบบทุกคน
/// (อีเมลยังถึงแม้เว็บเข้าไม่ได้เพราะ cert หมด — แถบเตือนตอนนั้นไม่มีใครเห็น)
///
/// <para>ที่มา: เจ้าของรายงาน 2026-10-19 "server ออนไม่ได้" → สาเหตุ cert หมดอายุ · ตัวตัดสินระดับ <see cref="TlsCertificateExpiry"/></para>
/// <para>อ่านใบรับรองเท่านั้น (ยอมรับทุกใบใน callback เพื่ออ่านใบที่หมดแล้วได้ · ไม่ส่งข้อมูลใด ๆ ผ่านการเชื่อมต่อนั้น)</para>
/// </summary>
public class TlsCertificateMonitorJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TlsCertificateMonitorJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public TlsCertificateMonitorJob(IServiceProvider services, ILogger<TlsCertificateMonitorJob> logger)
    {
        _services = services; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // หน่วงให้ migration (คอลัมน์ TlsCert*) เสร็จก่อน
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lockScope = _services.CreateScope();
                var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(TlsCertificateMonitorJob),
                    () => RunAsync(stoppingToken), _logger, ct: stoppingToken);
            }
            catch (Exception ex) { _logger.LogError(ex, "TlsCertificateMonitorJob failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var settings = await db.SiteSettings.FirstOrDefaultAsync(ct);
        if (settings == null) return;

        var baseUrl = string.IsNullOrWhiteSpace(settings.AppBaseUrl)
            ? scope.ServiceProvider.GetRequiredService<IConfiguration>()["App:BaseUrl"]
            : settings.AppBaseUrl;
        var target = TlsCertificateExpiry.Target(baseUrl);
        var now = DateTime.UtcNow;
        settings.TlsCertCheckedAt = now;
        if (target is not { } t)
        {
            settings.TlsCertHost = null;
            settings.TlsCertNotAfter = null;
            settings.TlsCertIssuer = null;
            settings.TlsCertCheckError = "ยังไม่ได้ตั้ง URL ของระบบแบบ https (ตั้งค่าระบบ → URL ของระบบ) — จึงไม่มีใบรับรองให้ตรวจ";
            await db.SaveChangesAsync(ct);
            return;
        }

        var (host, port) = t;
        var previousHost = settings.TlsCertHost;
        var previousNotAfter = settings.TlsCertNotAfter;
        settings.TlsCertHost = host;
        try
        {
            var (notAfterUtc, issuer) = await ProbeAsync(host, port, ct);
            settings.TlsCertNotAfter = notAfterUtc;
            settings.TlsCertIssuer = issuer.Length > 500 ? issuer[..500] : issuer;
            settings.TlsCertCheckError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // ต่อไม่ได้ ≠ ปกติ: เก็บเหตุผลให้แถบเตือนบอก (ค่า NotAfter เดิมคงไว้ — ยังเป็นข้อมูลล่าสุดที่รู้)
            var msg = $"เชื่อมต่อ {host}:{port} ไม่สำเร็จ ({ex.GetType().Name}: {ex.Message})";
            settings.TlsCertCheckError = msg.Length > 500 ? msg[..500] : msg;
            _logger.LogWarning("TLS check: {Message}", settings.TlsCertCheckError);
        }

        var level = TlsCertificateExpiry.LevelFor(settings.TlsCertNotAfter, now);
        // ต่ออายุแล้ว / ใบใหม่ — รอบหน้าเริ่มนับใหม่
        if (level == TlsCertificateExpiry.Level.Ok
            || TlsCertificateExpiry.IsNewCertificate(previousHost, previousNotAfter, host, settings.TlsCertNotAfter))
            settings.TlsCertNotifiedLevel = null;
        var last = settings.TlsCertNotifiedLevel is int n ? (TlsCertificateExpiry.Level?)n : null;
        await db.SaveChangesAsync(ct);
        if (!TlsCertificateExpiry.ShouldNotify(level, last)) return;

        var message = TlsCertificateExpiry.Message(level, settings.TlsCertNotAfter, now, host, settings.TlsCertCheckError, settings.TlsCertIssuer)!;
        _logger.LogWarning("TLS certificate: {Message}", message);
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        // EmailService ข้ามเงียบเมื่อไม่ได้ตั้ง SMTP ⇒ ถามก่อน แล้วบอกในแจ้งเตือนว่าอีเมลไม่ออก (ฝ่ายค้าน P2-4 — ตอน cert หมดจริง ช่องเดียวที่ถึงคนคืออีเมล)
        var emailReady = await email.IsSystemEmailConfiguredAsync();
        var inAppMessage = emailReady ? message : message + " · " + TlsCertificateExpiry.EmailNotConfiguredNote;
        var admins = await db.Users.AsNoTracking()
            .Where(u => u.IsSystemAdmin && u.Status == UserStatus.Active)
            .Select(u => new { u.Id, u.Email, u.FullName })
            .ToListAsync(ct);
        var delivered = 0;
        foreach (var a in admins)
        {
            // แต่ละคน/แต่ละช่องแยก try — คนแรกล้มต้องไม่ทำให้คนถัดไปไม่ได้รับ
            try
            {
                await notifications.SendAsync(a.Id, null, NotificationType.SecurityAlert,
                    "ใบรับรอง SSL ใกล้หมดอายุ", inAppMessage, "/admin/");
                delivered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "แจ้งเตือนใบรับรอง SSL ในระบบถึง {User} ไม่สำเร็จ", a.Id);
            }
            if (!emailReady || string.IsNullOrWhiteSpace(a.Email)) continue;
            try
            {
                await email.SendNotificationEmailAsync(a.Email, a.FullName, "ใบรับรอง SSL ของระบบใกล้หมดอายุ",
                    System.Net.WebUtility.HtmlEncode(message));
                delivered++;   // ถึงทางอีเมล = ถึงคนแล้ว (กันอีเมลซ้ำทุกวันเมื่อช่องในระบบล้ม)
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ส่งอีเมลเตือนใบรับรอง SSL ถึง {User} ไม่สำเร็จ", a.Id);
            }
        }
        // ประทับ "แจ้งขั้นนี้แล้ว" เฉพาะเมื่อถึงคนอย่างน้อยหนึ่งช่อง — ไม่งั้นพรุ่งนี้ลองใหม่ (เดิมประทับก่อนส่ง ⇒ ล้มครั้งเดียวเงียบทั้งขั้น)
        if (delivered == 0)
        {
            _logger.LogError("เตือนใบรับรอง SSL ไม่ถึงผู้ดูแลระบบเลย ({Admins} คน) — จะลองใหม่รอบถัดไป", admins.Count);
            return;
        }
        settings.TlsCertNotifiedLevel = (int)level;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>เปิด TLS handshake แล้วอ่านใบรับรองของเซิร์ฟเวอร์ (ยอมรับทุกใบเพื่ออ่านใบที่หมดแล้วได้ · ไม่ส่งข้อมูล)</summary>
    private static async Task<(DateTime NotAfterUtc, string Issuer)> ProbeAsync(string host, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, timeout.Token);
        X509Certificate2? captured = null;
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
            (_, cert, _, _) =>
            {
                if (cert != null) captured = new X509Certificate2(cert);
                return true;
            });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
        var c = captured ?? (ssl.RemoteCertificate is { } rc ? new X509Certificate2(rc) : null)
            ?? throw new InvalidOperationException("เซิร์ฟเวอร์ไม่ส่งใบรับรองกลับมา");
        using (c)
            return (c.NotAfter.ToUniversalTime(), c.Issuer);
    }
}

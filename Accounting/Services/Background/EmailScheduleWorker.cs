using Accounting.Services.Interfaces;

namespace Accounting.Services.Background;

/// <summary>
/// HostedService สำหรับการส่งอีเมลอัตโนมัติ:
///   • ProcessPendingQueueAsync ทุก 1 นาที — ดึงคิวถึงเวลามาส่ง
///   • ScanDueSoonAndOverdueAsync ทุก 1 ชม. — สแกนเอกสารใกล้/เกินกำหนด
/// scoped DI ใช้ผ่าน _services.CreateScope() ตามปกติของ BackgroundService.
/// </summary>
public class EmailScheduleWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EmailScheduleWorker> _logger;
    private static readonly TimeSpan QueueInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromHours(1);

    public EmailScheduleWorker(IServiceProvider services, ILogger<EmailScheduleWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // หน่วงเริ่มทำงาน 30s ให้ app boot เสร็จก่อน
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (TaskCanceledException) { return; }

        DateTime lastScan = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IEmailScheduleService>();

                var sent = await svc.ProcessPendingQueueAsync(stoppingToken);
                if (sent > 0)
                    _logger.LogInformation("EmailScheduleWorker: ส่ง {Count} ฉบับสำเร็จ", sent);

                if (DateTime.UtcNow - lastScan > ScanInterval)
                {
                    var enq = await svc.ScanDueSoonAndOverdueAsync(stoppingToken);
                    if (enq > 0)
                        _logger.LogInformation("EmailScheduleWorker: enqueue {Count} แจ้งเตือนใกล้/เกินกำหนด", enq);

                    var stmt = await svc.ScanMonthlyStatementsAsync(stoppingToken);
                    if (stmt > 0)
                        _logger.LogInformation("EmailScheduleWorker: enqueue {Count} statement รายเดือน", stmt);

                    lastScan = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not TaskCanceledException)
            {
                _logger.LogError(ex, "EmailScheduleWorker tick failed");
            }

            try { await Task.Delay(QueueInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}

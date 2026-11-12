using Accounting.Data;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 260 ฝ่ายค้าน 260-1 M4 (PDPA retention by purpose · คำตัดสิน 260-1-9) — ลบสำเนารูปที่ลูกค้าส่งในแชต LINE เมื่อไม่มีเหตุให้เก็บ:
/// ผูก/ปิดแล้วแต่ลบไม่สำเร็จตอนนั้น · ไม่ได้ผูกเกิน <c>LineChatSlipRule.UnmatchedRetentionDays</c> วัน (ปิดแถวพร้อมเหตุ) — ตัวทำงานเดียว
/// <c>LineChatSlipService.PurgeExpiredAsync</c> · รันวันละครั้ง · กันสองเครื่องด้วย <c>Helpers/JobLock</c> (try ไม่รอ)
/// </summary>
public class LineChatSlipRetentionJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LineChatSlipRetentionJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public LineChatSlipRetentionJob(IServiceProvider services, ILogger<LineChatSlipRetentionJob> logger)
    {
        _services = services; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(17), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                var storage = scope.ServiceProvider.GetRequiredService<Accounting.Services.Storage.IFileStorage>();
                await Accounting.Helpers.JobLock.RunExclusiveAsync(db, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(LineChatSlipRetentionJob), async () =>
                {
                    var n = await Accounting.Services.Implementations.LineChatSlipService.PurgeExpiredAsync(db, storage, _logger, DateTime.UtcNow, stoppingToken);
                    if (n > 0) _logger.LogInformation("ลบรูปสลิปจากแชต LINE ที่พ้นระยะเก็บ {Count} รูป", n);
                }, _logger, ct: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "LineChatSlipRetentionJob ล้ม — รอบหน้าลองใหม่");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}

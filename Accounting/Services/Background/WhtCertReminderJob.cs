using Accounting.Data;
using Accounting.Services.Implementations;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §4.3 เฟส 245) — งานทวงหนังสือรับรอง 50 ทวิ อัตโนมัติ · ทุก 6 ชั่วโมง (แต่ละบริษัทรันวันละครั้ง — watermark
/// <c>CompanySettings.WhtCertReminderLastRunAt</c>) · <b>ทีละเครื่อง</b>ผ่าน <see cref="Accounting.Helpers.JobLock"/> (F4 ข้อ 6 · กฎ #4 D) — สองเครื่องไม่ส่งอีเมลทวงซ้ำ
/// (ชั้นที่สอง: unique ขั้น/ลูกค้า/วันถูกหัก + กุญแจกันซ้ำของคิวอีเมล) · ตัวงานอยู่ที่ <see cref="WhtCertReminderService"/>
/// </summary>
public class WhtCertReminderJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<WhtCertReminderJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public WhtCertReminderJob(IServiceProvider services, ILogger<WhtCertReminderJob> logger)
    { _services = services; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(7), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "WhtCertReminderJob รอบนี้ล้ม — รอบหน้าลองใหม่"); }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>หนึ่งรอบภายใต้ล็อกงาน — ล็อกกับงานใช้ DbContext ตัวเดียวกัน (ล็อกผูก connection) · อีกเครื่องถือล็อก ⇒ ข้ามรอบนี้</summary>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<WhtCertReminderService>();
        return await Accounting.Helpers.JobLock.RunExclusiveAsync(db, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(WhtCertReminderJob),
            async () =>
            {
                var n = await svc.RunDueAsync(DateTime.UtcNow, ct);
                if (n > 0) _logger.LogInformation("ทวง 50 ทวิ อัตโนมัติ: เข้าคิวอีเมล {Count} ฉบับ", n);
            }, _logger, ct: ct);
    }
}

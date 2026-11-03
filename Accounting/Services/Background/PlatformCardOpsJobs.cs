using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §5.2) — ตัดบัตรอัตโนมัติของใบแจ้งหนี้ค่าบริการ NextAcc ที่ถึงกำหนด ทุกชั่วโมง
/// <para>กฎ #4 D: ทีละเครื่อง (<see cref="JobLock"/> · scope เดียวกับงาน — ล็อกผูก connection ที่เขียนข้อมูล) · ความพยายามต่อใบ unique (ใบ+ครั้งที่) ⇒
/// ซ้ำข้ามเครื่องไม่ได้แม้ล็อกหลุด · สวิตช์แพลตฟอร์มปิด (ค่าตั้งต้น) ⇒ ไม่ทำอะไร · ผลแต่ละรอบลง JobRunLog</para>
/// </summary>
public class PlatformAutoChargeJob : BackgroundService
{
    public const string JobName = "PlatformAutoCharge";
    private static readonly TimeSpan Cycle = TimeSpan.FromHours(1);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PlatformAutoChargeJob> _logger;

    public PlatformAutoChargeJob(IServiceScopeFactory scopes, ILogger<PlatformAutoChargeJob> logger) { _scopes = scopes; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(7), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunLockedAsync(_scopes, _logger, stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _logger.LogError(ex, "PlatformAutoChargeJob รอบนี้ล้ม"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>หนึ่งรอบภายใต้ JobLock — งานรายชั่วโมงและปุ่มแอดมิน "ตัดตอนนี้" ใช้ตัวนี้ตัวเดียว · null = อีกเครื่องกำลังทำอยู่</summary>
    public static async Task<AutoChargeRunResult?> RunLockedAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<IPlatformAutoChargeService>();
        AutoChargeRunResult? result = null;
        var started = DateTime.UtcNow;
        var ran = await JobLock.RunExclusiveAsync(db, AdvisoryLockKey.BackgroundJob, nameof(PlatformAutoChargeJob),
            async () => { result = await svc.RunOnceAsync(ct); }, logger, ct: ct);
        if (ran && result != null)
        {
            var rec = scope.ServiceProvider.GetService<IJobRunRecorder>();
            if (rec != null) await rec.RecordAsync(JobName, result.Failed == 0, result.Message, result.Attempted, started);
        }
        return ran ? result : null;
    }
}

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §5.3) — ดึงรอบโอนของช่องทางค่าบริการ NextAcc จากผู้ให้บริการแล้ว<b>ร่าง</b>รอบโอน ทุก 6 ชั่วโมง (ไม่ลงบัญชี)
/// <para>ทีละเครื่อง (<see cref="JobLock"/>) · watermark = รอบโอนล่าสุดที่เก็บ (<c>PlatformProviderTransfers.ProviderCreatedAt</c>) · unique เลขรอบโอน ⇒ ดึงซ้ำไม่เกิดแถวซ้ำ ·
/// ผู้ให้บริการล้ม ⇒ ล้มดังใน log + JobRunLog</para>
/// </summary>
public class PlatformTransferPullJob : BackgroundService
{
    public const string JobName = "PlatformTransferPull";
    private static readonly TimeSpan Cycle = TimeSpan.FromHours(6);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PlatformTransferPullJob> _logger;

    public PlatformTransferPullJob(IServiceScopeFactory scopes, ILogger<PlatformTransferPullJob> logger) { _scopes = scopes; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(11), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            using var scope = _scopes.CreateScope();
            var rec = scope.ServiceProvider.GetService<IJobRunRecorder>();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                var svc = scope.ServiceProvider.GetRequiredService<IPlatformTransferReconciliationService>();
                TransferPullResult? result = null;
                var ran = await JobLock.RunExclusiveAsync(db, AdvisoryLockKey.BackgroundJob, nameof(PlatformTransferPullJob),
                    async () => { result = await svc.PullAsync(Guid.Empty, stoppingToken); }, _logger, ct: stoppingToken);
                if (ran && result != null && rec != null) await rec.RecordAsync(JobName, true, result.Message, result.Drafted, started);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "PlatformTransferPullJob รอบนี้ล้ม");
                if (rec != null) await rec.RecordAsync(JobName, false, ex.Message, 0, started);
            }
            try { await Task.Delay(Cycle, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}

using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Background;

/// <summary>สรุปประจำวันทาง LINE ถึงพนักงาน (รอบ 212 C · <c>NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN.md</c> §5) — รอบละ 5 นาที · เดินทีละเครื่องผ่าน
/// <see cref="JobLock"/> (F4 #6) · ชุดที่ถึงกำหนดตัดสินด้วย <see cref="StaffDailySummaryPlan.Due"/> + watermark <c>LastSentLocalDate</c> ต่อบริษัท/ชุด ·
/// กันส่งซ้ำต่อวันต่อผู้รับด้วย unique (ชุด, วัน, คน) ใน <c>StaffDailySummaryService</c> (สองชั้น: ล็อกงาน + แถวส่ง)</summary>
public class StaffDailySummaryJob : BackgroundService
{
    public const string JobName = "StaffDailySummary";
    private static readonly TimeSpan Cycle = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StaffDailySummaryJob> _logger;

    public StaffDailySummaryJob(IServiceScopeFactory scopeFactory, ILogger<StaffDailySummaryJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "StaffDailySummaryJob รอบนี้ล้ม"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    /// <summary>กันสองเครื่องส่งสรุปซ้ำ (try ไม่ wait — อีกเครื่องทำอยู่ = ข้ามรอบ)</summary>
    private async Task RunGuardedAsync(CancellationToken ct)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(StaffDailySummaryJob), () => RunOnceAsync(ct), _logger, ct: ct);
    }

    /// <summary>หนึ่งรอบ — ชุดที่ถึงกำหนดทุกบริษัท</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IStaffDailySummaryService>();
        var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
        var started = DateTime.UtcNow;
        var sent = await svc.RunDueAsync(DateTime.UtcNow, ct);
        if (recorder != null && sent > 0)
            await recorder.RecordAsync(JobName, true, $"ส่งสรุปประจำวันถึง {sent} คน", sent, started);
        return sent;
    }
}

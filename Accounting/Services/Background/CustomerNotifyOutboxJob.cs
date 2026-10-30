using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Background;

/// <summary>คิวข้อความถึงลูกค้าที่ส่งตามหลัง (รอบ 212 แพ็ก B · <c>NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN.md</c> §5) — ตอนนี้: <b>คำเชิญผูก LINE ใหม่</b>หลังร้านเปลี่ยน OA ·
/// รอบละ 5 นาที · เดินทีละเครื่องผ่าน <see cref="JobLock"/> (F4 #6) · ทยอยส่งต่อบริษัท ≤ <see cref="CustomerRelinkInvitePlan.BatchPerCompanyPerRun"/> คน/รอบ ·
/// watermark = <c>LineOaSwitchEvents.ProcessedAt</c> ต่อการสลับ (ต่อบริษัท) · กันส่งซ้ำข้ามเครื่อง/ข้ามรอบด้วยคีย์ข้อความใน <c>CustomerNotifyLogs</c> (สองชั้น: ล็อกงาน + คีย์)</summary>
public class CustomerNotifyOutboxJob : BackgroundService
{
    public const string JobName = "CustomerNotifyOutbox";
    private static readonly TimeSpan Cycle = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CustomerNotifyOutboxJob> _logger;

    public CustomerNotifyOutboxJob(IServiceScopeFactory scopeFactory, ILogger<CustomerNotifyOutboxJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "CustomerNotifyOutboxJob รอบนี้ล้ม"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    /// <summary>กันสองเครื่องส่งคำเชิญซ้ำ (try ไม่ wait — อีกเครื่องทำอยู่ = ข้ามรอบ)</summary>
    private async Task RunGuardedAsync(CancellationToken ct)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(CustomerNotifyOutboxJob), () => RunOnceAsync(ct), _logger, ct: ct);
    }

    /// <summary>หนึ่งรอบ — การสลับ OA ที่ค้างทุกบริษัท</summary>
    public async Task<RelinkRunResult> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICustomerRelinkInviteService>();
        var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
        var started = DateTime.UtcNow;
        var r = await svc.ProcessPendingAsync(DateTime.UtcNow, ct);
        if (recorder != null && r.Events > 0)
            await recorder.RecordAsync(JobName, r.Failed == 0,
                $"เชิญผูก LINE ใหม่: การเปลี่ยน OA {r.Events} รายการ · ส่งถึง {r.Sent} · ส่งไม่สำเร็จ {r.Failed} · ครบ {r.Completed}", r.Sent, started);
        return r;
    }
}

using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Background;

/// <summary>รอบ 240 (คำตัดสิน 240-3): ส่งเหตุการณ์ "ลูกค้าเซ็น/ยอมรับ" จาก outbox — รอบละ 1 นาที · เดินทีละเครื่องผ่าน <see cref="JobLock"/> (F4 #6) ·
/// ไม่ทำงานในคำขอของลูกค้า ⇒ ปลายทางล่ม/ช้าไม่ทำให้การยอมรับล้ม · ผลรอบบันทึกผ่าน <see cref="IJobRunRecorder"/> เมื่อมีงาน</summary>
public class SignEventOutboxJob : BackgroundService
{
    public const string JobName = "SignEventOutbox";
    private static readonly TimeSpan Cycle = TimeSpan.FromMinutes(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SignEventOutboxJob> _logger;

    public SignEventOutboxJob(IServiceScopeFactory scopeFactory, ILogger<SignEventOutboxJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lockScope = _scopeFactory.CreateScope();
                var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(SignEventOutboxJob),
                    () => RunOnceAsync(stoppingToken), _logger, ct: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "SignEventOutboxJob รอบนี้ล้ม"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<SignEventDispatcher>();
        var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
        var started = DateTime.UtcNow;
        var r = await dispatcher.ProcessDueAsync(DateTime.UtcNow, ct);
        if (recorder != null && r.Processed > 0)
            await recorder.RecordAsync(JobName, r.Failed == 0 && r.Dead == 0,
                $"เหตุการณ์ลูกค้าเซ็น {r.Processed} รายการ · callback สำเร็จ {r.Delivered} · รอลองใหม่ {r.Failed} · เลิกลอง {r.Dead} · ล้าง payload เก่า {r.Purged}",
                r.Processed, started);
    }
}

/// <summary>รอบ 240 (คำตัดสิน 240-7): ส่งอีเมลขอยอมรับอัตโนมัติเมื่อใบเสนอราคาอนุมัติ + อีเมลเตือนครั้งเดียวต่อลิงก์ — รอบละ 10 นาที · <see cref="JobLock"/> ·
/// watermark: <c>CompanySettings.SignLinkAutoEmailSince</c> (ใบที่สร้างก่อนเปิดค่าตั้งไม่ถูกส่งย้อนหลัง) + บันทึกการส่งของเอกสาร (ลองอัตโนมัติครั้งเดียวต่อใบ/ต่อลิงก์) ·
/// ครอบทุกทางเข้าอนุมัติ (เว็บ · กฎ · มือถือ · LINE · API) เพราะอ่านสถานะเอกสาร ไม่ได้เกาะเส้นอนุมัติเส้นใดเส้นหนึ่ง</summary>
public class SignRequestAutomationJob : BackgroundService
{
    public const string JobName = "SignRequestAutomation";
    private static readonly TimeSpan Cycle = TimeSpan.FromMinutes(10);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SignRequestAutomationJob> _logger;

    public SignRequestAutomationJob(IServiceScopeFactory scopeFactory, ILogger<SignRequestAutomationJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(4), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lockScope = _scopeFactory.CreateScope();
                var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(SignRequestAutomationJob),
                    () => RunOnceAsync(stoppingToken), _logger, ct: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "SignRequestAutomationJob รอบนี้ล้ม"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ISignRequestService>();
        var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
        var started = DateTime.UtcNow;
        var r = await svc.RunAutomationAsync(DateTime.UtcNow, ct);
        if (r.Error != null)
        {
            // ฝ่ายค้าน S4: ทำงานไม่ได้ทั้งรอบ = ล้มดังบนหน้าสถานะงาน (ไม่ใช่ log อย่างเดียว)
            if (recorder != null) await recorder.RecordAsync(JobName, false, r.Error, 0, started);
            return;
        }
        var total = r.AutoSent + r.AutoFailed + r.Reminded + r.RemindFailed;
        if (recorder != null && total > 0)
            await recorder.RecordAsync(JobName, r.AutoFailed == 0 && r.RemindFailed == 0,
                $"ส่งอีเมลขอยอมรับอัตโนมัติ {r.AutoSent} (ล้ม {r.AutoFailed}) · อีเมลเตือน {r.Reminded} (ล้ม {r.RemindFailed})", total, started);
    }
}

using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 252 WP-12A — งานเบื้องหลังของ Autopilot ส่วน A · รอบละ 5 นาที · <b>แต่ละขั้นเดินทีละเครื่องผ่าน <see cref="JobLock"/></b> (F4 #6 · คีย์แยกต่อขั้น ⇒ ขั้นหนึ่งช้าไม่บล็อกอีกขั้น):
/// (1) ใบเสนอราคาที่ลูกค้ายอมรับ → แปลงอัตโนมัติ (<see cref="QuotationAcceptAutopilotService"/>) · (2) เงินโอนเข้า → ปิดหนี้/เสนอ (<see cref="IBankReceiptSettleService"/>) ·
/// (3) สรุปประจำวันถึงทีม (<see cref="AutopilotDigestService"/>) · ขั้นที่ล้มบันทึกผลรอบเป็น "ล้ม" ผ่าน <see cref="IJobRunRecorder"/> (ล้มดังที่หน้าสถานะงาน — ไม่ใช่แค่ log)
/// </summary>
public class AutopilotJob : BackgroundService
{
    public const string JobName = "Autopilot";
    private static readonly TimeSpan Cycle = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutopilotJob> _logger;

    public AutopilotJob(IServiceScopeFactory scopeFactory, ILogger<AutopilotJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await StepAsync("QuotationAccept", async sp => await sp.GetRequiredService<QuotationAcceptAutopilotService>().RunOnceAsync(DateTime.UtcNow, stoppingToken),
                n => $"ตัดสินใบเสนอราคาที่ลูกค้ายอมรับ {n} ใบ", stoppingToken);
            await StepAsync("BankAutoSettle", async sp => await sp.GetRequiredService<IBankReceiptSettleService>().EvaluatePendingAsync(DateTime.UtcNow, stoppingToken),
                n => $"ประเมินเงินโอนเข้า {n} รายการ", stoppingToken);
            await StepAsync("Digest", async sp => await sp.GetRequiredService<AutopilotDigestService>().RunOnceAsync(DateTime.UtcNow, stoppingToken),
                n => $"ส่งสรุป Autopilot ประจำวัน {n} บริษัท", stoppingToken);
            // รอบ 255 WP-12B ข้อ 8: 50 ทวิ ที่ผู้จ่ายออก ⇒ กล่องรับของผู้ขายที่เป็นคู่ค้า NextAcc (ค่าตั้งต้นปิด · JobLock ของขั้นนี้)
            await StepAsync("PartnerWhtCerts", async sp => await sp.GetRequiredService<PartnerWhtCertExchangeService>().RunOnceAsync(DateTime.UtcNow, stoppingToken),
                n => $"ส่ง/แจ้ง 50 ทวิ ผ่านคู่ค้า NextAcc {n} ใบ", stoppingToken);
            // รอบ 264 (คำตัดสิน 264-1-4): คำขอเชื่อมคู่ค้า ⇒ แจ้งเจ้าของ/ผู้ดูแลของบริษัทปลายทาง (กระดิ่ง · อีเมล · LINE) — นอกคำขอของผู้ขอ (เวลาตอบคงที่)
            await StepAsync("PartnerLinkRequests", async sp => await sp.GetRequiredService<CrossTenantWorkflowService>().DispatchPendingLinkRequestNoticesAsync(DateTime.UtcNow, stoppingToken),
                n => $"แจ้งคำขอเชื่อมคู่ค้า NextAcc {n} รายการ", stoppingToken);
            // รอบ 264 P3 (คำตัดสิน 264-3-8): การแจ้ง "ผู้ขายคู่ค้ารับเงินแล้ว" ⇒ ระบบของผู้ซื้อบันทึกจ่ายเองภายใต้ความยินยอม (ไม่ทำในคำขอของผู้ขาย) + ผังบัญชีใบซื้อร่าง
            await StepAsync("PartnerPayable", async sp => await sp.GetRequiredService<PartnerPayablePipeline>().RunOnceAsync(DateTime.UtcNow, stoppingToken),
                n => $"ท่อฝั่งผู้ซื้อคู่ค้า NextAcc ทำ {n} รายการ (บันทึกจ่าย/เติมผังใบร่าง)", stoppingToken);
            // ฝ่ายค้าน 264-P4 M2 (คำตัดสิน 264-5-15): outbox สำเนาสลิปของ "แจ้งโอนให้ผู้ขาย" ที่ยังค้าง (ความยินยอมของผู้ขายตรวจใหม่ทุกครั้ง)
            await StepAsync("PartnerSlipCopies", async sp => await sp.GetRequiredService<PartnerOutboundPaymentService>().DeliverPendingSlipCopiesAsync(DateTime.UtcNow, stoppingToken),
                n => $"ส่งสำเนาสลิปแจ้งโอนให้ผู้ขายคู่ค้า NextAcc {n} รายการ", stoppingToken);
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    /// <summary>หนึ่งขั้น: scope ใหม่ · ล็อกของขั้นนั้น · บันทึกผลรอบเมื่อมีงานหรือล้ม</summary>
    private async Task StepAsync(string step, Func<IServiceProvider, Task<int>> work, Func<int, string> summary, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            using var lockScope = _scopeFactory.CreateScope();
            var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(AutopilotJob) + ":" + step, async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var n = await work(scope.ServiceProvider);
                var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
                if (recorder != null && n > 0)
                    await recorder.RecordAsync(JobName + ":" + step, true, summary(n), n, started);
            }, _logger, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "AutopilotJob ขั้น {Step} ล้ม", step);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
                if (recorder != null)
                    await recorder.RecordAsync(JobName + ":" + step, false, "ล้ม: " + ex.Message, 0, started);
            }
            catch (Exception rex) when (rex is not OperationCanceledException) { _logger.LogError(rex, "บันทึกผลรอบ AutopilotJob ล้ม"); }
        }
    }
}

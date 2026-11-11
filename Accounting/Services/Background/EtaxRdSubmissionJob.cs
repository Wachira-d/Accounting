using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 259 (B-M6 · คำตัดสิน 259-4-6 · CLAUDE.md §F "Submission cron ภายในวันที่ 15 ของเดือนถัดไป · retry queue") — งานนำส่ง e-Tax ต่อกรมสรรพากร
/// ของบริษัทที่เปิด<b>โหมดส่งตรง</b> (<c>EtaxEnabled</c> + <see cref="EtaxMode.Direct"/>) · ทุก 3 ชั่วโมงในช่วงวันที่ 1–15 (เวลาไทย) · หลังจากนั้นทุก 12 ชม. สำหรับใบที่เลยกำหนด ·
/// ทีละเครื่องผ่าน <see cref="JobLock"/> (F4 ข้อ 6) · ตัวงาน = <see cref="IEtaxInvoiceService.SubmitPendingPreviousMonthsAsync"/> (คิวเว้นช่วงต่อฉบับ ·
/// ประทับ "ส่งแล้ว" เฉพาะเมื่อกรมตอบรับจริง) · ผลรวมแจ้งทีมต่อบริษัท + เรื่องค้างหน้า Autopilot · โหมด e-Tax by Email ไม่เกี่ยว (ส่งทางอีเมลคนละเส้น)
/// </summary>
public class EtaxRdSubmissionJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EtaxRdSubmissionJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(3);
    private static readonly TimeSpan OutsideWindowInterval = TimeSpan.FromHours(12);

    public EtaxRdSubmissionJob(IServiceProvider services, ILogger<EtaxRdSubmissionJob> logger)
    { _services = services; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(11), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "EtaxRdSubmissionJob รอบนี้ล้ม — รอบหน้าลองใหม่"); }
            // ฝ่ายค้าน 259-D M4: วันที่ 1–15 ทุก 3 ชม. · หลังวันที่ 15 ยังทำงาน (ใบเลยกำหนดลองวันละครั้งตามคิว RdNextAttemptAt) ทุก 12 ชม.
            var wait = EtaxRdSubmission.InSubmissionWindow(DateTime.UtcNow) ? Interval : OutsideWindowInterval;
            try { await Task.Delay(wait, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>หนึ่งรอบภายใต้ล็อกงาน · ทุกวัน (ฝ่ายค้าน 259-D M4 — เดิมหยุดหลังวันที่ 15 ⇒ ใบที่ล้ม/ลงนามช้าค้างไปเดือนหน้า) · คืน false เมื่ออีกเครื่องถือล็อก</summary>
    internal async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var etax = scope.ServiceProvider.GetRequiredService<IEtaxInvoiceService>();
        return await JobLock.RunExclusiveAsync(db, AdvisoryLockKey.BackgroundJob, nameof(EtaxRdSubmissionJob), async () =>
        {
            var companies = await db.CompanySettings.AsNoTracking()
                .Where(s => s.EtaxEnabled && s.EtaxMode == EtaxMode.Direct)
                .Select(s => s.CompanyId).Distinct().ToListAsync(ct);
            foreach (var cid in companies)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var r = await etax.SubmitPendingPreviousMonthsAsync(cid, nowUtc, ct);
                    if (r.Attempted > 0)
                        _logger.LogInformation("นำส่ง e-Tax ประจำเดือน {Company}: สำเร็จ {Ok}/{All} ไม่สำเร็จ {Failed}", cid, r.Succeeded, r.Attempted, r.Failed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // บริษัทหนึ่งล้ม ไม่หยุดบริษัทอื่น (เหตุต่อฉบับอยู่บนแถว e-Tax)
                    _logger.LogError(ex, "นำส่ง e-Tax ประจำเดือนของบริษัท {Company} ล้ม", cid);
                }
            }
        }, _logger, ct: ct);
    }
}

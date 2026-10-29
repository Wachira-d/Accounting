using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>ปิดบัตรคอร์ส/เครดิตที่หมดอายุ + รับรู้ส่วนคงเหลือเป็นรายได้ (breakage · TFRS for NPAEs บทที่ 6) — ทีม L
///
/// <para>รันทุก 6 ชม. · กันรันซ้ำข้าม instance ด้วย <c>Helpers/JobLock</c> (F4 ข้อ 6) · ทำทีละบริษัทที่มีบัตรเลยวันหมดอายุ ·
/// บัตรที่มีการจองค้าง <b>ข้าม</b> (ป้ายเตือนบนหน้าจอบอกให้ปิดการจองก่อน — ไม่ประทับว่าหมดอายุเอง) · ตัวลงบัญชีตัวเดียวกับปุ่มบนจอ
/// (<see cref="ICourseCreditService.ExpireDueAsync"/>)</para></summary>
public class CourseCreditExpiryJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CourseCreditExpiryJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public CourseCreditExpiryJob(IServiceProvider services, ILogger<CourseCreditExpiryJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(11), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lockScope = _services.CreateScope();
                var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                await Accounting.Helpers.JobLock.RunExclusiveAsync(
                    lockDb, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(CourseCreditExpiryJob),
                    () => RunAsync(stoppingToken), _logger, ct: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "CourseCreditExpiryJob failed");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var today = Accounting.Helpers.ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        List<Guid> companies;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            // รอบ 209 P5: + บริษัทที่มีสมาชิกไม่จำกัดค้างรับรู้ตามวัน + บริษัทที่เปิดแจ้งเตือนใกล้หมดอายุ (แพ็กเกจ ExpiryReminderDays > 0)
            var yesterday = today.AddDays(-1);
            companies = await db.CreditPasses.AsNoTracking()
                .Where(p => p.Status == CreditPassStatus.Active
                    && ((p.ExpiresOn != null && p.ExpiresOn < today)
                        || (p.Kind == CreditPackageKind.UnlimitedPeriod && p.PeriodStartsOn != null && p.PeriodStartsOn <= yesterday
                            && (p.RecognizedThroughDate == null || p.RecognizedThroughDate < yesterday))))
                .Select(p => p.CompanyId)
                .Union(db.CreditPackages.AsNoTracking().Where(k => k.ExpiryReminderDays > 0).Select(k => k.CompanyId))
                .Distinct().Take(1000).ToListAsync(ct);
        }
        foreach (var companyId in companies)
        {
            // ลำดับ: รับรู้ตามวันก่อน → แจ้งเตือนใกล้หมดอายุ → ปิดบัตรหมดอายุ (ปิดบัตรสมาชิกรับรู้ครบถึงวันหมดอายุเองอีกชั้น) · ขั้นหนึ่งล้มไม่หยุดขั้นถัดไป
            try
            {
                using var scope = _services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ICourseCreditService>();
                var pr = await svc.RecognizePeriodDueAsync(companyId, "system:course-credit-period", ct);
                if (pr.Failed > 0)
                    _logger.LogError("บริษัท {Cid}: รับรู้รายได้สมาชิกไม่จำกัดล้ม {F} ใบ (สำเร็จ {N} · {Amt:N2}) — {Msgs}",
                        companyId, pr.Failed, pr.Passes, pr.Recognized, string.Join(" | ", pr.Messages));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "รับรู้รายได้สมาชิกไม่จำกัดของบริษัท {Cid} ล้ม", companyId);
            }
            try
            {
                using var scope = _services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ICourseCreditService>();
                var rr = await svc.SendExpiryRemindersAsync(companyId, "system:course-credit-reminder", ct);
                if (rr.Failed > 0)
                    _logger.LogWarning("บริษัท {Cid}: แจ้งเตือนบัตรใกล้หมดอายุส่งไม่ถึง {F} ใบ (ถึง {S}) — ประทับเหตุบนบัตรแล้ว · {Msgs}",
                        companyId, rr.Failed, rr.Sent, string.Join(" | ", rr.Messages));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "แจ้งเตือนบัตรใกล้หมดอายุของบริษัท {Cid} ล้ม", companyId);
            }
            try
            {
                using var scope = _services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ICourseCreditService>();
                var r = await svc.ExpireDueAsync(companyId, "system:course-credit-expiry", ct);
                if (r.Failed > 0)
                    _logger.LogError("บริษัท {Cid}: ปิดบัตรหมดอายุล้ม {F} ใบ (สำเร็จ {N} · ข้ามเพราะจองค้าง {S}) — {Msgs}",
                        companyId, r.Failed, r.Expired, r.SkippedWithReservations, string.Join(" | ", r.Messages));
                else if (r.Expired > 0 || r.SkippedWithReservations > 0)
                    _logger.LogInformation("บริษัท {Cid}: ปิดบัตรหมดอายุ {N} ใบ (breakage {Amt:N2}) · ข้าม {S} ใบ",
                        companyId, r.Expired, r.BreakageRecognized, r.SkippedWithReservations);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ปิดบัตรคอร์สหมดอายุของบริษัท {Cid} ล้ม", companyId);
            }
        }
    }
}

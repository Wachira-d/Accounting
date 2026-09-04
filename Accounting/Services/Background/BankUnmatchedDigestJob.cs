using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>Bank reconciliation digest รายวัน (workflow แบบ Xero: statement
/// เข้ามา → ระบบชวนผู้ใช้เข้าไปยืนยัน match ทุกเช้า):
///
/// ทุก 24 ชม. ต่อบริษัท — นับ BankTransaction ที่ยัง Unmatched (ย้อนหลัง
/// 45 วัน) ต่อบัญชีธนาคาร → ส่ง notification พร้อม deep-link เข้าหน้า
/// reconcile ซึ่งปุ่ม "AI match ทั้งชุด" จะเสนอคู่ให้อัตโนมัติ (student-first
/// local model ตามกฎ distillation — job นี้จงใจ "ไม่" เรียก AI เอง เพื่อไม่
/// เผา budget โดยไม่มีคนดูผล; การเสนอ + การเก็บ feedback เกิดในหน้า UI
/// ที่ผู้ใช้กดยืนยัน ซึ่งเป็นจุดที่ระบบเรียนรู้ได้จริง).
///
/// เงียบเมื่อไม่มีรายการค้าง — ไม่ spam.</summary>
public class BankUnmatchedDigestJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BankUnmatchedDigestJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public BankUnmatchedDigestJob(IServiceProvider services, ILogger<BankUnmatchedDigestJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(11), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "BankUnmatchedDigestJob failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }

    /// <summary>กันสอง instance ทำงานรอบเดียวกันพร้อมกัน (ผลตรวจ F-09)
    ///
    /// <para>ใช้ <b>try</b> ไม่ใช่ wait — งานตามตารางที่อีกเครื่องกำลังทำอยู่
    /// การรอคือทำงานเดิมซ้ำเปล่า ๆ ข้ามไปรอบหน้าถูกกว่า</para></summary>
    private async Task RunGuardedAsync(CancellationToken ct)
    {
        using var lockScope = _services.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await Accounting.Helpers.JobLock.RunExclusiveAsync(
            lockDb, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(BankUnmatchedDigestJob),
            () => RunAsync(ct), _logger, ct: ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var notify = scope.ServiceProvider.GetService<INotificationEngine>();
        if (notify == null) return;

        var since = DateTime.UtcNow.AddDays(-45);
        var digest = await db.Set<Models.Entities.BankTransaction>().AsNoTracking()
            .Where(t => t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && t.TransactionDate >= since
                && !t.IsDeleted)
            .GroupBy(t => new { t.CompanyId, t.BankAccountId })
            .Select(g => new
            {
                g.Key.CompanyId,
                g.Key.BankAccountId,
                Count = g.Count(),
                Total = g.Sum(x => x.Amount),
            })
            .ToListAsync(ct);
        if (digest.Count == 0) return;

        var accountNames = await db.Set<Models.Entities.BankAccount>().AsNoTracking()
            .Where(b => digest.Select(d => d.BankAccountId).Contains(b.Id))
            .Select(b => new { b.Id, b.AccountName, b.BankName })
            .ToListAsync(ct);
        var nameById = accountNames.ToDictionary(a => a.Id, a => $"{a.AccountName} ({a.BankName})");

        foreach (var companyGroup in digest.GroupBy(d => d.CompanyId))
        {
            var totalCount = companyGroup.Sum(d => d.Count);
            var detail = string.Join(" · ", companyGroup.Select(d =>
                $"{nameById.GetValueOrDefault(d.BankAccountId, "บัญชีธนาคาร")}: {d.Count} รายการ"));
            try
            {
                await notify.DispatchAsync(companyGroup.Key, "bank.unmatched_digest",
                    new NotificationContext
                    {
                        Title = $"🏦 มีรายการธนาคารรอ match {totalCount} รายการ",
                        Message = $"{detail} — เปิดหน้ากระทบยอดแล้วกด \"AI match ทั้งชุด\" ระบบจะเสนอคู่ให้ยืนยัน",
                        ActionUrl = "/pages/bank.html",
                        EntityType = "BankReconciliation",
                    });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bank digest notify failed for company {Cid}", companyGroup.Key);
            }
        }
    }
}

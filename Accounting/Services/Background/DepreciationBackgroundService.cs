using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// Monthly auto-depreciation cron. Once a day after midnight, for every
/// company that owns Active fixed assets, checks whether the depreciation
/// JE for the PREVIOUS month is already posted — and if not, runs
/// FixedAssetService.CalculateDepreciationAsync which creates the
/// AssetDepreciation rows + a single posted JE
/// (Dr ค่าเสื่อมราคา / Cr ค่าเสื่อมราคาสะสม) per the asset's mapped
/// expense+accumulated accounts.
///
/// Idempotent: CalculateDepreciationAsync skips rows already IsPosted=true,
/// so running it twice in the same month is a no-op. A DepreciationPosted
/// notification fires per company on success.
///
/// Why previous-month (not current): you can't depreciate days that haven't
/// happened yet. The job is meant to land on the 1st-2nd of every month,
/// posting the just-closed month's depreciation.
/// </summary>
public class DepreciationBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DepreciationBackgroundService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public DepreciationBackgroundService(
        IServiceProvider services,
        ILogger<DepreciationBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Defer first run so EF + migrations are settled on a fresh container.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "DepreciationBackgroundService cycle failed"); }

            try { await Task.Delay(CheckInterval, stoppingToken); } catch { return; }
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
            lockDb, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(DepreciationBackgroundService),
            () => RunCycleAsync(ct), _logger, ct: ct);
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        // Compute the period the job is responsible for: previous calendar month.
        var today = DateTime.UtcNow.Date;
        var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
        var target = firstOfThisMonth.AddMonths(-1);
        int year = target.Year, month = target.Month;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var assetService = scope.ServiceProvider.GetRequiredService<IFixedAssetService>();
        var notify = scope.ServiceProvider.GetService<INotificationEngine>();
        // ผลรอบลงสถานะงาน (JobRunLog) — 2026-10-14 (ID#13): เดิมบริษัทที่ลงค่าเสื่อมไม่สำเร็จมีแค่ LogError
        // ⇒ ผู้ดูแลไม่เห็นในหน้าสถานะงาน และเจ้าของบริษัทไม่รู้จนปิดงบ (ล้มดัง 3 ที่ — CLAUDE.md F2 ข้อ 7)
        var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
        var startedAt = DateTime.UtcNow;
        var postedCompanies = 0;
        var failures = new List<string>();

        // Companies with at least one Active asset linked to expense + accumulated GL accounts.
        var companyIds = await db.FixedAssets.AsNoTracking()
            .Where(a => !a.IsDeleted && a.Status == AssetStatus.Active
                && a.DepreciationExpenseAccountId != null
                && a.AccumulatedDepreciationAccountId != null)
            .Select(a => a.CompanyId)
            .Distinct()
            .ToListAsync(ct);

        if (companyIds.Count == 0)
        {
            if (recorder != null)
                await recorder.RecordAsync(nameof(DepreciationBackgroundService), true,
                    $"งวด {month:D2}/{year}: ไม่มีสินทรัพย์ที่ต้องลงค่าเสื่อม", 0, startedAt);
            return;
        }

        var request = new CalculateDepreciationRequest(year, month);
        // ชื่อบริษัทสำหรับข้อความสถานะงาน — โหลดก่อนวน (ถ้าบริษัทหนึ่งล้มกลางคัน context อาจอยู่ในสภาพที่ query ต่อไม่ได้)
        var companyNames = await db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        foreach (var companyId in companyIds)
        {
            if (ct.IsCancellationRequested) return;

            // Idempotency: skip if every Active GL-mapped asset in the company
            // already has a posted depreciation row for this period.
            var unpostedCount = await db.FixedAssets.AsNoTracking()
                .Where(a => a.CompanyId == companyId && !a.IsDeleted
                    && a.Status == AssetStatus.Active
                    && a.DepreciationExpenseAccountId != null
                    && a.AccumulatedDepreciationAccountId != null
                    && a.PurchaseDate <= new DateTime(year, month, 1)
                    && !db.AssetDepreciations.Any(d => d.FixedAssetId == a.Id
                        && d.Year == year && d.Month == month && d.IsPosted))
                .CountAsync(ct);

            if (unpostedCount == 0) continue;

            try
            {
                var rows = await assetService.CalculateDepreciationAsync(companyId, request, "system:cron");
                var postedCount = rows.Count(r => r.IsPosted);
                if (postedCount == 0) continue;
                postedCompanies++;

                _logger.LogInformation(
                    "Auto-depreciation posted for company {CompanyId} {Year}/{Month}: {Count} assets",
                    companyId, year, month, postedCount);

                if (notify != null)
                {
                    try
                    {
                        var totalAmount = rows.Where(r => r.IsPosted).Sum(r => r.Amount);
                        await notify.DispatchAsync(companyId, NotificationEvents.DepreciationPosted, new NotificationContext
                        {
                            Title = $"ลงค่าเสื่อมราคา {month:D2}/{year} อัตโนมัติ",
                            Message = $"โพสต์ JE ค่าเสื่อมราคา {postedCount} สินทรัพย์ · ยอดรวม {totalAmount:N2} บาท",
                            ActionUrl = "/pages/fixed-assets.html",
                            EntityType = "FixedAsset", EntityId = companyId,
                        });
                    }
                    catch (Exception nex) { _logger.LogWarning(nex, "DepreciationPosted notification failed for {CompanyId}", companyId); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Auto-depreciation failed for company {CompanyId} {Year}/{Month}",
                    companyId, year, month);
                var companyName = companyNames.TryGetValue(companyId, out var nm) ? nm : companyId.ToString();
                failures.Add($"{companyName}: {ex.Message}");
                // แจ้งบริษัทนั้นเอง (ผู้ที่ตั้งรับแจ้งเตือนเหตุการณ์นี้) — ข้อความบอกสาเหตุ + ทางไปต่อ
                if (notify != null)
                {
                    try
                    {
                        await notify.DispatchAsync(companyId, NotificationEvents.DepreciationFailed, new NotificationContext
                        {
                            Title = $"ลงค่าเสื่อมราคา {month:D2}/{year} อัตโนมัติไม่สำเร็จ",
                            Message = $"{ex.Message} — แก้สาเหตุ (เช่น เปิดใช้ผังค่าเสื่อม/เลือกผังใหม่ที่หน้าสินทรัพย์ถาวร) "
                                + "แล้วกด \"คำนวณค่าเสื่อมราคา\" ของงวดนี้ที่หน้าสินทรัพย์ถาวร",
                            ActionUrl = "/pages/fixed-assets.html",
                            EntityType = "FixedAsset", EntityId = companyId,
                        });
                    }
                    catch (Exception nex) { _logger.LogWarning(nex, "DepreciationFailed notification failed for {CompanyId}", companyId); }
                }
            }
        }

        if (recorder != null)
            await recorder.RecordAsync(nameof(DepreciationBackgroundService), failures.Count == 0,
                $"งวด {month:D2}/{year}: ลงสำเร็จ {postedCompanies} บริษัท"
                + (failures.Count > 0 ? $" · ล้ม {failures.Count} บริษัท — {string.Join(" | ", failures.Take(10))}" : ""),
                postedCompanies, startedAt);
    }
}

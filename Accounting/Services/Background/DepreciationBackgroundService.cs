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
            try { await RunCycleAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "DepreciationBackgroundService cycle failed"); }

            try { await Task.Delay(CheckInterval, stoppingToken); } catch { return; }
        }
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

        // Companies with at least one Active asset linked to expense + accumulated GL accounts.
        var companyIds = await db.FixedAssets.AsNoTracking()
            .Where(a => !a.IsDeleted && a.Status == AssetStatus.Active
                && a.DepreciationExpenseAccountId != null
                && a.AccumulatedDepreciationAccountId != null)
            .Select(a => a.CompanyId)
            .Distinct()
            .ToListAsync(ct);

        if (companyIds.Count == 0) return;

        var request = new CalculateDepreciationRequest(year, month);

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
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Auto-depreciation failed for company {CompanyId} {Year}/{Month}",
                    companyId, year, month);
            }
        }
    }
}

using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// Daily cron job that refreshes Document.AgingDays for every Pending
/// / Approved-but-Unpaid document so list views and aging reports can
/// render badges without recomputing per request. Cheap full-table
/// update (Documents are bounded by company scale) — runs at startup
/// and every 6 hours after.
///
/// AgingDays = (today - DocumentDate) for documents that:
///   * are Approved (not Voided/Cancelled/Draft)
///   * have outstanding balance > 0 (Total - Paid)
///   * are not a credit-note / receipt (in/out neutral)
///
/// Flags badges in list UI:
///   30+   yellow
///   60+   orange
///   90+   red ("Pending Aging" auto-filter trigger)
/// </summary>
public class DocumentAgingBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DocumentAgingBackgroundService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public DocumentAgingBackgroundService(IServiceProvider services, ILogger<DocumentAgingBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait 60s after startup so EF model + migrations are settled before
        // we run heavy queries on a fresh container.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAgingAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DocumentAgingBackgroundService cycle failed");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }

    private async Task RefreshAgingAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        // PostgreSQL-native date arithmetic via raw SQL — portable across
        // EF Core versions and avoids the SqlServer-only DateDiffDay helper.
        // DocumentStatus (AllEnums.cs): Approved=2, Sent=3, PartiallyPaid=4,
        // Overdue=7 — the posted-but-unpaid states that should age. (The old
        // code used Status=1, which is WaitingApproval — a bug: approved
        // unpaid invoices never aged.)
        var sqlUpdate = """
            UPDATE "Documents"
               SET "AgingDays" = EXTRACT(DAY FROM (CURRENT_DATE - "DocumentDate"))::int,
                   "AgingLastEvaluatedAt" = NOW()
             WHERE "IsDeleted" = false
               AND "Status" IN (2, 3, 4, 7)
               AND "TotalAmount" > 0
               AND "PaidAmount" < "TotalAmount"
        """;
        var affected = await db.Database.ExecuteSqlRawAsync(sqlUpdate, ct);

        // Clear stale aging on docs that have since been paid/voided.
        var sqlClear = """
            UPDATE "Documents"
               SET "AgingDays" = NULL,
                   "AgingLastEvaluatedAt" = NOW()
             WHERE "AgingDays" IS NOT NULL
               AND ("IsDeleted" = true OR "Status" NOT IN (2, 3, 4, 7) OR "PaidAmount" >= "TotalAmount")
        """;
        var cleared = await db.Database.ExecuteSqlRawAsync(sqlClear, ct);

        _logger.LogInformation("DocumentAgingBackgroundService: refreshed {Affected}, cleared {Cleared}",
            affected, cleared);
    }
}

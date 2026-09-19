using Accounting.Data;
using Accounting.Services.Implementations.Forecast;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Inventory;

/// <summary>
/// Per-SKU demand forecast + reorder recommendation. Mines the
/// StockMovement table for outbound history, buckets daily, feeds to
/// CrostonForecaster, and emits a "will run out in N days, suggested
/// order Q units" report row per product.
///
/// Cheap enough to run on demand (a 90-day daily series per SKU is
/// 90 decimals; Croston is O(n)). For a 5,000-SKU catalogue the full
/// pass finishes in ≲1 second. Not cached — admin/UI hits trigger a
/// fresh forecast so reorder advice always reflects the last sale.
///
/// Output feeds two surfaces:
///   • Local view: /stock/reorder-forecast — table for AP / purchasing
///     team. Sortable by "days to stockout" so the most urgent items
///     surface first.
///   • Narrative (Helpers/ReorderNarrative): **เลขคณิตล้วน ไม่เรียก AI** —
///     เรียบเรียงแถวข้างบนเป็นประโยคไทย ผ่าน endpoint
///     POST ai/inventory/reorder-narrative (D-5 รอบ 184: เดิมส่งตารางนี้ไปให้
///     DeepSeek เล่าเรื่อง = ถามสิ่งที่เราเพิ่งคำนวณเอง + ไม่มีนักเรียนรองรับ
///     ⇒ kill-switch ไม่ผ่าน)
/// </summary>
public interface IInventoryReorderForecastService
{
    Task<IReadOnlyList<ReorderForecastRow>> ForecastAsync(Guid companyId,
        int historyDays = 90, int leadTimeDays = 7, decimal serviceLevelZ = 1.645m,
        CancellationToken ct = default);
}

public sealed record ReorderForecastRow(
    Guid ProductId,
    string? Sku,
    string Name,
    decimal CurrentStock,
    decimal MinimumStock,
    decimal AvgDailyDemand,
    decimal DaysOfStockRemaining,
    decimal ReorderPoint,
    decimal SuggestedOrderQuantity,
    string Urgency,                   // "Critical" | "Warning" | "OK" | "Idle"
    string ForecastMethod);            // "Croston" | "Insufficient"

public class InventoryReorderForecastService : IInventoryReorderForecastService
{
    private readonly AccountingDbContext _db;

    public InventoryReorderForecastService(AccountingDbContext db) { _db = db; }

    public async Task<IReadOnlyList<ReorderForecastRow>> ForecastAsync(Guid companyId,
        int historyDays = 90, int leadTimeDays = 7, decimal serviceLevelZ = 1.645m,
        CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.Date.AddDays(-Math.Clamp(historyDays, 14, 365));
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.TrackStock)
            .Select(p => new { p.Id, p.SKU, p.Name, p.CurrentStock, p.MinimumStock })
            .ToListAsync(ct);
        if (products.Count == 0) return Array.Empty<ReorderForecastRow>();

        // Bulk-pull OUT movements for these SKUs in one query — avoids
        // an N+1 per product.
        var pids = products.Select(p => p.Id).ToHashSet();
        var movements = await _db.StockMovements.AsNoTracking()
            .Where(m => m.CompanyId == companyId && !m.IsDeleted
                        && pids.Contains(m.ProductId)
                        && m.MovementType == "OUT"
                        && m.MovementDate >= since)
            .Select(m => new { m.ProductId, m.MovementDate, m.Quantity })
            .ToListAsync(ct);
        var byProduct = movements.GroupBy(m => m.ProductId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var bucketCount = (DateTime.UtcNow.Date - since).Days + 1;
        var rows = new List<ReorderForecastRow>(products.Count);

        foreach (var p in products)
        {
            var series = new decimal[bucketCount];
            if (byProduct.TryGetValue(p.Id, out var ms))
            {
                foreach (var m in ms)
                {
                    var idx = (int)(m.MovementDate.Date - since).TotalDays;
                    if (idx >= 0 && idx < bucketCount) series[idx] += m.Quantity;
                }
            }

            // Skip-if-no-movement: still emit a row so admin can see
            // "this SKU hasn't sold in the window" — useful for slow-
            // moving inventory pruning.
            var fc = CrostonForecaster.Forecast(series);
            var avgDaily = fc.MeanDemandPerPeriod;
            var rop = CrostonForecaster.ReorderPoint(fc, leadTimeDays, serviceLevelZ);
            var daysLeft = avgDaily > 0 ? p.CurrentStock / avgDaily : decimal.MaxValue;
            // Suggested order: cover demand from now through next review
            // (lead time + 14 days), minus any stock above the reorder
            // point. Capped at 365× daily demand to avoid silly orders.
            var coverDays = leadTimeDays + 14m;
            var suggested = avgDaily > 0
                ? Math.Max(0m, Math.Ceiling(avgDaily * coverDays - Math.Max(0m, p.CurrentStock - rop)))
                : 0m;
            suggested = Math.Min(suggested, avgDaily * 365m);

            var urgency = avgDaily <= 0 ? "Idle"
                : p.CurrentStock <= 0 ? "Critical"
                : daysLeft < leadTimeDays ? "Critical"
                : p.CurrentStock < rop ? "Warning"
                : "OK";

            rows.Add(new ReorderForecastRow(
                p.Id, p.SKU, p.Name, p.CurrentStock, p.MinimumStock,
                AvgDailyDemand: Math.Round(avgDaily, 3),
                DaysOfStockRemaining: daysLeft == decimal.MaxValue ? -1m : Math.Round(daysLeft, 1),
                ReorderPoint: rop,
                SuggestedOrderQuantity: suggested,
                Urgency: urgency,
                ForecastMethod: fc.MeanDemandPerPeriod > 0 ? "Croston" : "Insufficient"));
        }
        return rows.OrderBy(r => r.Urgency switch
        {
            "Critical" => 0, "Warning" => 1, "OK" => 2, _ => 3,
        }).ThenBy(r => r.DaysOfStockRemaining).ToList();
    }
}

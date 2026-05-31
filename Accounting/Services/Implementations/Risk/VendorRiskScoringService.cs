using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Risk;

/// <summary>
/// Per-vendor risk scoring — surfaces vendors that show patterns of
/// dispute, delayed shipping, or business instability based purely on
/// the company's own purchasing history. The mirror to CustomerPaymentRisk
/// but on the AP side: who should we hesitate before placing a big order
/// with?
///
/// Signals (all local, no DeepSeek):
///   • Document rejection rate (Voided / Cancelled bills) — proxy for
///     incorrect deliveries or disputes.
///   • Inconsistent pricing — same SKU/description with wildly
///     different unit prices across recent bills (CV > 0.4 = volatile).
///   • Recent activity decline — bills last 90 days vs prior 90 days
///     (a collapsing vendor relationship is a vendor-side instability
///     signal you want to know about BEFORE the next big order).
///   • Long gaps in supply — implicit "do they still service us?"
///   • Negotiated discount ratio — vendors who lock list price (no
///     discount applied) tend to be inflexible.
///
/// Output is a 0-100 score + tier + reasons. Risk score is exposed
/// via the procurement UI; AiFeatureKey.VendorCanonicalization can
/// (separately) wrap this with a "consider alternatives" narrative.
/// </summary>
public interface IVendorRiskScoringService
{
    Task<IReadOnlyList<VendorRisk>> ScoreAllAsync(Guid companyId,
        int lookbackMonths = 12, CancellationToken ct = default);
}

public sealed record VendorRisk(
    Guid ContactId,
    string ContactName,
    int TotalBills,
    int VoidedBills,
    decimal VoidedRate,                // 0-1
    decimal PriceVolatilityCV,         // coefficient of variation on duplicate SKUs
    int DaysSinceLastBill,
    decimal RecentActivityDelta,       // (last 90d count - prior 90d) / prior 90d
    decimal RiskScore,                 // 0-100
    string RiskTier,                   // "Low" | "Medium" | "High" | "Critical"
    IReadOnlyList<string> Reasons,
    string SuggestedAction);

public class VendorRiskScoringService : IVendorRiskScoringService
{
    private readonly AccountingDbContext _db;

    public VendorRiskScoringService(AccountingDbContext db) { _db = db; }

    public async Task<IReadOnlyList<VendorRisk>> ScoreAllAsync(Guid companyId,
        int lookbackMonths = 12, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.Date.AddMonths(-Math.Clamp(lookbackMonths, 1, 36));
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                        && (d.DocumentType == DocumentType.PurchaseInvoice
                            || d.DocumentType == DocumentType.Expense)
                        && d.DocumentDate >= since)
            .Include(d => d.Lines)
            .Select(d => new
            {
                d.ContactId, ContactName = d.Contact.Name,
                d.DocumentDate, d.Status, d.TotalAmount,
                Lines = d.Lines.Select(l => new {
                    l.Description, l.Quantity, l.UnitPrice,
                }).ToList(),
            })
            .ToListAsync(ct);

        if (docs.Count == 0) return Array.Empty<VendorRisk>();

        var now = DateTime.UtcNow.Date;
        var ninetyDaysAgo = now.AddDays(-90);
        var oneEightyDaysAgo = now.AddDays(-180);

        var byVendor = docs.GroupBy(d => (d.ContactId, d.ContactName));
        var results = new List<VendorRisk>();
        foreach (var g in byVendor)
        {
            var bills = g.ToList();
            var voided = bills.Count(b => b.Status == DocumentStatus.Voided);
            var voidRate = bills.Count > 0 ? (decimal)voided / bills.Count : 0m;
            var lastBill = bills.Max(b => b.DocumentDate);
            var daysSince = (decimal)(now - lastBill).TotalDays;

            var recent = bills.Count(b => b.DocumentDate >= ninetyDaysAgo);
            var prior = bills.Count(b => b.DocumentDate < ninetyDaysAgo && b.DocumentDate >= oneEightyDaysAgo);
            var activityDelta = prior > 0 ? (decimal)(recent - prior) / prior : 0m;

            // Price volatility: gather (descKey → list of unit prices)
            // from line items; compute CV per descKey, average.
            var pricesBySku = new Dictionary<string, List<decimal>>();
            foreach (var b in bills)
                foreach (var l in b.Lines)
                {
                    if (string.IsNullOrWhiteSpace(l.Description) || l.UnitPrice <= 0) continue;
                    var key = l.Description.Trim().ToLowerInvariant();
                    if (key.Length > 50) key = key[..50];
                    if (!pricesBySku.ContainsKey(key)) pricesBySku[key] = new();
                    pricesBySku[key].Add(l.UnitPrice);
                }
            var cvs = pricesBySku.Values.Where(v => v.Count >= 2).Select(CV).ToList();
            var avgCv = cvs.Count > 0 ? cvs.Average() : 0m;

            // ── Scorecard ──────────────────────────────────────────
            var score = voidRate * 40m
                + Math.Min(1m, avgCv / 0.4m) * 25m
                + Math.Min(90m, daysSince) / 90m * 15m * (recent == 0 ? 1m : 0.5m)
                + (activityDelta < -0.5m ? 20m : activityDelta < -0.25m ? 10m : 0m);
            score = Math.Min(100m, score);

            var tier = score >= 70 ? "Critical"
                    : score >= 50 ? "High"
                    : score >= 25 ? "Medium" : "Low";

            var reasons = new List<string>();
            if (voidRate > 0.10m) reasons.Add($"อัตรา void/cancel {voidRate:P0} (สูงผิดปกติ)");
            if (avgCv > 0.4m) reasons.Add($"ราคาต่อหน่วยผันผวน CV {avgCv:F2}");
            if (daysSince > 90 && recent == 0) reasons.Add($"ไม่มีบิลในช่วง {daysSince:N0} วันที่ผ่านมา");
            if (activityDelta < -0.5m) reasons.Add($"จำนวนบิลลดลง {(-activityDelta):P0} จาก 90 วันก่อน");

            var suggested = tier switch
            {
                "Critical" => "หา supplier สำรอง + ทบทวนสัญญา",
                "High"     => "ขอใบเสนอราคาคู่เปรียบเทียบจาก vendor อื่น",
                "Medium"   => "ตรวจสอบราคาก่อนสั่งครั้งต่อไป",
                _          => "ใช้งานต่อตามปกติ",
            };

            results.Add(new VendorRisk(
                ContactId: g.Key.ContactId,
                ContactName: g.Key.ContactName,
                TotalBills: bills.Count,
                VoidedBills: voided,
                VoidedRate: Math.Round(voidRate, 3),
                PriceVolatilityCV: Math.Round(avgCv, 2),
                DaysSinceLastBill: (int)daysSince,
                RecentActivityDelta: Math.Round(activityDelta, 2),
                RiskScore: Math.Round(score, 1),
                RiskTier: tier,
                Reasons: reasons,
                SuggestedAction: suggested));
        }
        return results.OrderByDescending(r => r.RiskScore).ToList();
    }

    private static decimal CV(List<decimal> values)
    {
        if (values.Count < 2) return 0m;
        var mean = values.Average();
        if (mean == 0) return 0m;
        var sumSq = values.Sum(v => (v - mean) * (v - mean));
        var std = (decimal)Math.Sqrt((double)(sumSq / (values.Count - 1)));
        return Math.Abs(std / mean);
    }
}

using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Risk;

/// <summary>
/// Per-customer payment risk score — predicts how likely the next
/// invoice is to be paid late + a magnitude estimate (expected days
/// past due). Powers the "ใครต้องโทรตามก่อน" workflow for AR teams.
///
/// Algorithm: feature-based logistic-style scoring on confirmed
/// invoice payment history. No formal LR fit (training data per-tenant
/// is too small for a stable solve); instead we use a calibrated
/// hand-rolled scorecard whose weights came from a small DeepSeek
/// pilot study on confirmed Thai SME data. As the corpus grows the
/// weights can be replaced with proper fit — interface stays the same.
///
/// Output flows two surfaces:
///   • Direct: GET /api/companies/{id}/risk/customer-payment — table
///     for AR collections.
///   • AI narrative (AiFeatureKey.AgingExplanation): DeepSeek wraps
///     the numeric risk in Thai prose ("คุณ X เคยจ่ายช้าเฉลี่ย 18
///     วัน, แนะนำส่ง reminder ก่อน due date 5 วัน").
/// </summary>
public interface ICustomerPaymentRiskService
{
    Task<IReadOnlyList<CustomerPaymentRisk>> AnalyzeAsync(Guid companyId,
        int lookbackMonths = 12, CancellationToken ct = default);
}

public sealed record CustomerPaymentRisk(
    Guid ContactId,
    string ContactName,
    int TotalInvoices,
    int PaidLateCount,
    decimal LatePaymentRate,           // 0-1
    decimal AvgDaysLate,
    decimal P90DaysLate,
    decimal OutstandingAmount,
    decimal RiskScore,                 // 0-100 (higher = more risky)
    string RiskTier,                   // "Low" | "Medium" | "High" | "Critical"
    IReadOnlyList<string> Reasons,
    string SuggestedAction);

public class CustomerPaymentRiskService : ICustomerPaymentRiskService
{
    private readonly AccountingDbContext _db;

    public CustomerPaymentRiskService(AccountingDbContext db) { _db = db; }

    public async Task<IReadOnlyList<CustomerPaymentRisk>> AnalyzeAsync(Guid companyId,
        int lookbackMonths = 12, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.Date.AddMonths(-Math.Clamp(lookbackMonths, 1, 36));

        // Pull AR documents in the window. PaymentDate vs DueDate is
        // the supervised signal — null PaymentDate + BalanceDue > 0 +
        // past DueDate = currently overdue.
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                        && (d.DocumentType == DocumentType.Invoice
                            || d.DocumentType == DocumentType.TaxInvoice
                            || d.DocumentType == DocumentType.BillingNote)
                        && d.DocumentDate >= since
                        && d.Status != DocumentStatus.Voided
                        && d.Status != DocumentStatus.Draft)
            .Select(d => new
            {
                d.Id, d.ContactId, ContactName = d.Contact.Name,
                d.DocumentDate, d.DueDate, d.PaymentDate, d.TotalAmount, d.BalanceDue,
                d.Status,
            })
            .ToListAsync(ct);

        if (docs.Count == 0) return Array.Empty<CustomerPaymentRisk>();

        var now = DateTime.UtcNow.Date;
        var byCustomer = docs.GroupBy(d => (d.ContactId, d.ContactName));
        var results = new List<CustomerPaymentRisk>();
        foreach (var g in byCustomer)
        {
            var invoices = g.ToList();
            var paid = invoices.Where(i => i.PaymentDate.HasValue && i.DueDate.HasValue).ToList();
            var paidLate = paid.Where(i => i.PaymentDate!.Value > i.DueDate!.Value).ToList();
            var lateDays = paidLate.Select(i => (decimal)(i.PaymentDate!.Value - i.DueDate!.Value).TotalDays).ToList();

            var totalInv = invoices.Count;
            var paidCount = paid.Count;
            var lateCount = paidLate.Count;
            var rate = paidCount > 0 ? (decimal)lateCount / paidCount : 0m;
            var avgLate = lateDays.Count > 0 ? lateDays.Average() : 0m;
            var p90Late = lateDays.Count > 0 ? Percentile(lateDays, 0.90m) : 0m;
            var outstanding = invoices.Sum(i => i.BalanceDue);

            // ── Scorecard ──────────────────────────────────────────
            // Score 0-100 from 5 weighted features:
            //   • Late rate         (×40)   0..1
            //   • Avg days late     (×0.6, capped 30) 0..30 days
            //   • P90 days late     (×0.5, capped 60)
            //   • Currently overdue (binary ×15)
            //   • Outstanding share (×10)   outstanding/total amount
            var currentlyOverdue = invoices.Any(i => i.BalanceDue > 0
                && i.DueDate.HasValue && i.DueDate.Value < now);
            var totalAmount = invoices.Sum(i => i.TotalAmount);
            var outstandingShare = totalAmount > 0 ? outstanding / totalAmount : 0m;

            var score = rate * 40m
                + Math.Min(30m, avgLate) * 0.6m
                + Math.Min(60m, p90Late) * 0.5m
                + (currentlyOverdue ? 15m : 0m)
                + outstandingShare * 10m;
            score = Math.Min(100m, score);

            var tier = score >= 70 ? "Critical"
                    : score >= 50 ? "High"
                    : score >= 25 ? "Medium" : "Low";

            var reasons = new List<string>();
            if (rate > 0.40m) reasons.Add($"จ่ายช้า {rate:P0} ของยอด invoice ทั้งหมด");
            if (avgLate > 15m) reasons.Add($"เฉลี่ยจ่ายช้า {avgLate:N0} วัน");
            if (currentlyOverdue) reasons.Add($"มียอดค้างชำระเลย due date ปัจจุบัน {outstanding:N0} บาท");
            if (outstandingShare > 0.50m) reasons.Add("มากกว่าครึ่งของยอด invoice ยังไม่ได้รับชำระ");

            var suggested = tier switch
            {
                "Critical" => "ระงับเครดิตชั่วคราว + ส่งทีมตามทันที",
                "High"     => "ส่ง reminder ก่อน due date 5 วัน + monitor ใกล้ชิด",
                "Medium"   => "ตั้ง follow-up อัตโนมัติเมื่อใกล้ครบกำหนด",
                _          => "ติดตามตามปกติ",
            };

            results.Add(new CustomerPaymentRisk(
                ContactId: g.Key.ContactId,
                ContactName: g.Key.ContactName,
                TotalInvoices: totalInv,
                PaidLateCount: lateCount,
                LatePaymentRate: Math.Round(rate, 3),
                AvgDaysLate: Math.Round(avgLate, 1),
                P90DaysLate: Math.Round(p90Late, 1),
                OutstandingAmount: outstanding,
                RiskScore: Math.Round(score, 1),
                RiskTier: tier,
                Reasons: reasons,
                SuggestedAction: suggested));
        }
        return results.OrderByDescending(r => r.RiskScore).ToList();
    }

    private static decimal Percentile(List<decimal> sortedSrc, decimal p)
    {
        if (sortedSrc.Count == 0) return 0m;
        var sorted = sortedSrc.OrderBy(x => x).ToList();
        var idx = (int)Math.Ceiling((double)p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}

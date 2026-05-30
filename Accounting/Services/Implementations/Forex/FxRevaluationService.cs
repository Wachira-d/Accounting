using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Forex;

/// <summary>
/// Period-end FX revaluation — every open foreign-currency AR/AP
/// balance is restated at the period-end exchange rate. The difference
/// vs the booked rate (carrying value) is the unrealised FX gain/loss
/// and posts to two GL accounts: 4901 ("กำไรจากอัตราแลกเปลี่ยน") or
/// 5901 ("ขาดทุนจากอัตราแลกเปลี่ยน"), against the AR/AP control account.
///
/// Per ประมวลรัษฎากร §65(2): closing FX rate of the BoT reference rate
/// on the last business day of the period is the canonical source.
/// We don't fetch BoT rates here — admin provides the closing rates;
/// service computes the variance + posts the JE.
///
/// Output: a single batch JE with N debit/credit pairs (one per
/// affected currency × AR/AP combination) so the entire revaluation
/// for a period lives in ONE auditable transaction.
/// </summary>
public interface IFxRevaluationService
{
    Task<FxRevaluationResult> ProposeAsync(Guid companyId, DateTime asOf,
        IReadOnlyDictionary<string, decimal> closingRates,
        CancellationToken ct = default);
}

public sealed record FxRevaluationResult(
    DateTime AsOf,
    IReadOnlyList<FxRevaluationLine> Lines,
    decimal TotalGain,
    decimal TotalLoss,
    decimal NetEffect,
    IReadOnlyList<string> Warnings);

public sealed record FxRevaluationLine(
    string Currency,
    string Direction,                 // "AR" | "AP"
    decimal OriginalFcyAmount,
    decimal BookedThbValue,           // sum of (FCY × original rate)
    decimal CurrentThbValue,          // sum of (FCY × closing rate)
    decimal Variance);                // Current - Booked; positive = gain on AR / loss on AP

public class FxRevaluationService : IFxRevaluationService
{
    private readonly AccountingDbContext _db;

    public FxRevaluationService(AccountingDbContext db) { _db = db; }

    public async Task<FxRevaluationResult> ProposeAsync(Guid companyId, DateTime asOf,
        IReadOnlyDictionary<string, decimal> closingRates,
        CancellationToken ct = default)
    {
        var lines = new List<FxRevaluationLine>();
        var warnings = new List<string>();

        // AR side — open Invoice / TaxInvoice / BillingNote in non-base
        // currency. BalanceDue is in document currency; ExchangeRate
        // captures the booked rate.
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                        && d.BalanceDue > 0
                        && d.Currency != null && d.Currency != "THB"
                        && d.DocumentDate <= asOf
                        && d.Status != DocumentStatus.Voided
                        && d.Status != DocumentStatus.Draft)
            .Select(d => new
            {
                d.DocumentType, d.Currency, d.ExchangeRate, d.BalanceDue,
            })
            .ToListAsync(ct);

        var groups = docs
            .GroupBy(d => new
            {
                d.Currency,
                Direction = (d.DocumentType == DocumentType.PurchaseInvoice
                             || d.DocumentType == DocumentType.Expense) ? "AP" : "AR",
            });
        foreach (var g in groups)
        {
            var cur = g.Key.Currency!;
            if (!closingRates.TryGetValue(cur, out var closingRate) || closingRate <= 0)
            {
                warnings.Add($"ไม่ได้รับ closing rate สำหรับ {cur} — ข้ามการ revalue ฝั่ง {g.Key.Direction}");
                continue;
            }
            var fcy = g.Sum(x => x.BalanceDue);
            var booked = g.Sum(x => x.BalanceDue * (x.ExchangeRate > 0 ? x.ExchangeRate : 1m));
            var current = fcy * closingRate;
            var variance = Math.Round(current - booked, 2);
            if (variance == 0m) continue;
            lines.Add(new FxRevaluationLine(cur, g.Key.Direction,
                Math.Round(fcy, 2), Math.Round(booked, 2),
                Math.Round(current, 2), variance));
        }

        // Sign convention: variance > 0 on AR = gain (we owe MORE THB
        // from customers, meaning baht is worth less → revenue grew);
        // variance > 0 on AP = loss (we owe more in baht to vendors).
        decimal totalGain = 0, totalLoss = 0;
        foreach (var l in lines)
        {
            var effect = l.Direction == "AR" ? l.Variance : -l.Variance;
            if (effect > 0) totalGain += effect;
            else if (effect < 0) totalLoss += -effect;
        }
        var net = Math.Round(totalGain - totalLoss, 2);
        return new FxRevaluationResult(asOf, lines, totalGain, totalLoss, net, warnings);
    }
}

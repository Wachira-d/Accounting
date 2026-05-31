using Accounting.Data;
using Accounting.Models.Entities;
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

    /// <summary>Post the proposed revaluation as a single auditable
    /// JournalEntry. Per ประมวลรัษฎากร §65(2) the closing rate of the
    /// period is the basis; this method takes the same closingRates
    /// dictionary so the post is reproducible from the proposal.
    /// Reference is stamped "FX-REVAL-{asOf:yyyyMM}" so a re-run on
    /// the same period is idempotent (subsequent calls bail when the
    /// reference already exists in the period).</summary>
    Task<FxRevaluationPostResult> PostAsync(Guid companyId, DateTime asOf,
        IReadOnlyDictionary<string, decimal> closingRates, string createdBy,
        CancellationToken ct = default);
}

public sealed record FxRevaluationPostResult(
    FxRevaluationResult Proposal,
    Guid? JournalEntryId,
    string? JournalNumber,
    string Outcome,                   // "Posted" | "AlreadyPosted" | "Skipped"
    string? Message);

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

    public async Task<FxRevaluationPostResult> PostAsync(Guid companyId, DateTime asOf,
        IReadOnlyDictionary<string, decimal> closingRates, string createdBy,
        CancellationToken ct = default)
    {
        var proposal = await ProposeAsync(companyId, asOf, closingRates, ct);
        if (proposal.Lines.Count == 0)
            return new FxRevaluationPostResult(proposal, null, null, "Skipped",
                "ไม่มี FCY balance ที่ต้อง revalue");

        var reference = $"FX-REVAL-{asOf:yyyyMM}";

        // Idempotency — if a JE with this Reference already exists in
        // the period, return early so a re-run doesn't double-post.
        var existing = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && j.Reference == reference && !j.IsDeleted
                && j.Status != JournalEntryStatus.Voided)
            .Select(j => new { j.Id, j.EntryNumber })
            .FirstOrDefaultAsync(ct);
        if (existing != null)
            return new FxRevaluationPostResult(proposal, existing.Id, existing.EntryNumber, "AlreadyPosted",
                $"งวด {asOf:yyyy-MM} ถูก revalue ไปแล้วโดย JE {existing.EntryNumber}");

        // Look up GL accounts. AR control = 1130, AP control = 2110,
        // FX gain = 4901 (revenue side), FX loss = 5901 (expense side).
        // The proposer above already validates closing rate exists;
        // here we additionally require the four GL accounts.
        var arCtrl = await FindAccountAsync(companyId, "1130", ct);
        var apCtrl = await FindAccountAsync(companyId, "2110", ct);
        var fxGain = await FindAccountAsync(companyId, "4901", ct);
        var fxLoss = await FindAccountAsync(companyId, "5901", ct);
        var missing = new List<string>();
        if (proposal.Lines.Any(l => l.Direction == "AR") && arCtrl == null) missing.Add("1130 ลูกหนี้การค้า");
        if (proposal.Lines.Any(l => l.Direction == "AP") && apCtrl == null) missing.Add("2110 เจ้าหนี้การค้า");
        if (proposal.NetEffect != 0 && (fxGain == null || fxLoss == null))
            missing.Add("4901 / 5901 (กำไร/ขาดทุนจากอัตราแลกเปลี่ยน)");
        if (missing.Count > 0)
            return new FxRevaluationPostResult(proposal, null, null, "Skipped",
                $"ไม่พบบัญชี: {string.Join(", ", missing)} — โปรดสร้างใน Chart of Accounts ก่อน");

        // Build the JE — one line per (currency × direction) on the
        // AR/AP control side; one net line on the FX gain/loss side
        // per direction. Sign convention:
        //   AR variance > 0  → Dr 1130 / Cr 4901  (gain)
        //   AR variance < 0  → Dr 5901 / Cr 1130  (loss)
        //   AP variance > 0  → Dr 5901 / Cr 2110  (loss — owe more THB)
        //   AP variance < 0  → Dr 2110 / Cr 4901  (gain — owe less THB)
        var entryYearMonth = asOf.ToString("yyyyMM");
        var jePrefix = $"JV-FX-{entryYearMonth}-";
        var maxJe = await _db.JournalEntries.IgnoreQueryFilters()
            .Where(j => j.CompanyId == companyId && j.EntryNumber.StartsWith(jePrefix))
            .Select(j => j.EntryNumber).MaxAsync(ct) as string;
        var jeSeq = 1;
        if (maxJe != null && int.TryParse(maxJe.Substring(jePrefix.Length), out var n)) jeSeq = n + 1;

        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = $"{jePrefix}{jeSeq:D4}",
            EntryDate = asOf.Date,
            JournalType = JournalType.General,
            Description = $"FX revaluation งวด {asOf:yyyy-MM}",
            Reference = reference,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            CreatedBy = createdBy,
        };

        decimal totalDr = 0, totalCr = 0;
        foreach (var line in proposal.Lines)
        {
            if (line.Variance == 0) continue;
            var control = line.Direction == "AR" ? arCtrl! : apCtrl!;
            var isGain = (line.Direction == "AR" && line.Variance > 0)
                      || (line.Direction == "AP" && line.Variance < 0);
            var pnlAcct = isGain ? fxGain! : fxLoss!;
            var absVar = Math.Abs(line.Variance);

            if (line.Direction == "AR" && line.Variance > 0)
            {
                // Dr AR / Cr FX Gain
                je.Lines.Add(NewLine(control.Id, absVar, 0, $"Reval AR {line.Currency} @ {asOf:yyyy-MM-dd}"));
                je.Lines.Add(NewLine(pnlAcct.Id, 0, absVar, $"FX gain {line.Currency}"));
            }
            else if (line.Direction == "AR" && line.Variance < 0)
            {
                // Dr FX Loss / Cr AR
                je.Lines.Add(NewLine(pnlAcct.Id, absVar, 0, $"FX loss {line.Currency}"));
                je.Lines.Add(NewLine(control.Id, 0, absVar, $"Reval AR {line.Currency} @ {asOf:yyyy-MM-dd}"));
            }
            else if (line.Direction == "AP" && line.Variance > 0)
            {
                // Dr FX Loss / Cr AP  (liability grew in THB)
                je.Lines.Add(NewLine(pnlAcct.Id, absVar, 0, $"FX loss {line.Currency}"));
                je.Lines.Add(NewLine(control.Id, 0, absVar, $"Reval AP {line.Currency} @ {asOf:yyyy-MM-dd}"));
            }
            else
            {
                // AP variance < 0: Dr AP / Cr FX Gain
                je.Lines.Add(NewLine(control.Id, absVar, 0, $"Reval AP {line.Currency} @ {asOf:yyyy-MM-dd}"));
                je.Lines.Add(NewLine(pnlAcct.Id, 0, absVar, $"FX gain {line.Currency}"));
            }
            totalDr += absVar;
            totalCr += absVar;
        }
        je.TotalDebit = totalDr;
        je.TotalCredit = totalCr;

        if (Math.Abs(je.TotalDebit - je.TotalCredit) > 0.01m)
            return new FxRevaluationPostResult(proposal, null, null, "Skipped",
                $"JE ไม่สมดุล: Dr={je.TotalDebit} Cr={je.TotalCredit}");

        // Period-lock check (same guard the manual JE create enforces).
        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId && f.StartDate <= asOf && f.EndDate >= asOf, ct);
        if (period != null && period.Status != FiscalPeriodStatus.Open)
            return new FxRevaluationPostResult(proposal, null, null, "Skipped",
                $"งวด {period.Name} ถูกปิด — เปิดงวดก่อนจึงจะ revalue ได้");
        if (period != null) je.FiscalPeriodId = period.Id;

        _db.JournalEntries.Add(je);
        await _db.SaveChangesAsync(ct);
        return new FxRevaluationPostResult(proposal, je.Id, je.EntryNumber, "Posted",
            $"โพสต์ JE {je.EntryNumber} · กำไร {proposal.TotalGain:N2} · ขาดทุน {proposal.TotalLoss:N2} · สุทธิ {proposal.NetEffect:N2}");
    }

    private static JournalEntryLine NewLine(Guid accountId, decimal dr, decimal cr, string desc) =>
        new() { AccountId = accountId, DebitAmount = dr, CreditAmount = cr, Description = desc };

    private async Task<ChartOfAccount?> FindAccountAsync(Guid companyId, string code, CancellationToken ct) =>
        await _db.ChartOfAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == code
                && a.IsActive && !a.IsDeleted, ct);
}

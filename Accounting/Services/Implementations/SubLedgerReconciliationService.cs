using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// Sub-Ledger ↔ GL Reconciliation report (Phase I of accountant-tools
/// upgrade). For each "control account" category (AR / AP / Inventory /
/// Fixed Assets / Cash / Bank), compares:
///   * GL ending balance of the control account at the as-of date
///   * Sum of the underlying sub-ledger details (Contact balances,
///     stock × cost, FA NBV, …)
/// and surfaces the variance so the accountant can drill in.
///
/// This is the single most important monthly health-check report —
/// nothing reconciles to anything if sub-ledger ≠ GL.
/// </summary>
public class SubLedgerReconciliationService
{
    private readonly AccountingDbContext _db;

    public SubLedgerReconciliationService(AccountingDbContext db) { _db = db; }

    public record ReconLine(
        string Category,           // "ลูกหนี้การค้า (AR)"
        string AccountCode,
        string AccountName,
        Guid AccountId,
        decimal GlBalance,         // GL ending balance at asOf
        decimal SubLedgerBalance,  // Sum of sub-ledger details
        decimal Variance,          // GL - SubLedger
        bool IsReconciled,         // |variance| ≤ tolerance
        int SubLedgerItemCount,
        string? Notes);

    public record ReconResult(
        DateTime AsOfDate,
        decimal Tolerance,
        List<ReconLine> Lines,
        int ReconciledCount,
        int VarianceCount);

    public async Task<ReconResult> ReconcileAsync(Guid companyId, DateTime asOf, decimal tolerance = 0.01m)
    {
        var asOfExclusive = asOf.Date.AddDays(1);
        var lines = new List<ReconLine>();

        // ===== AR — ลูกหนี้การค้า =====
        // Control account: typically AccountCode starting with "112" or "113"
        // for ลูกหนี้. We pick the highest-balance Level-4+ account that matches.
        var arAccounts = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted
                && (a.AccountCode.StartsWith("112") || a.AccountCode.StartsWith("113"))
                && a.AccountName.Contains("ลูกหนี้")
                && a.AccountType == AccountType.Asset)
            .ToListAsync();
        foreach (var acc in arAccounts)
        {
            var glBal = await ComputeGlBalanceAsync(companyId, acc.Id, asOfExclusive);
            // Sub-ledger: sum of revenue-side documents with outstanding
            // balance at the as-of date — covers Sent / PartiallyPaid /
            // Overdue / Approved alike (excludes Draft, Voided, Rejected).
            var arSub = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.DocumentDate < asOfExclusive
                    && d.Status != DocumentStatus.Draft
                    && d.Status != DocumentStatus.Voided
                    && d.Status != DocumentStatus.Rejected
                    && d.BalanceDue > 0
                    && (d.DocumentType == DocumentType.Invoice
                        || d.DocumentType == DocumentType.TaxInvoice
                        || d.DocumentType == DocumentType.BillingNote
                        || d.DocumentType == DocumentType.DebitNote))
                .GroupBy(d => 1)
                .Select(g => new { Total = g.Sum(d => d.BalanceDue), Count = g.Count() })
                .FirstOrDefaultAsync();
            var subBal = arSub?.Total ?? 0m;
            var subCount = arSub?.Count ?? 0;
            var variance = Math.Round(glBal - subBal, 2);
            lines.Add(new ReconLine("ลูกหนี้การค้า (AR)", acc.AccountCode, acc.AccountName, acc.Id,
                glBal, subBal, variance, Math.Abs(variance) <= tolerance, subCount,
                subCount == 0 ? "ไม่มีเอกสารฝั่ง AR ที่ค้างชำระ" : null));
        }

        // ===== AP — เจ้าหนี้การค้า =====
        var apAccounts = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted
                && a.AccountCode.StartsWith("211")
                && a.AccountName.Contains("เจ้าหนี้")
                && a.AccountType == AccountType.Liability)
            .ToListAsync();
        foreach (var acc in apAccounts)
        {
            var glBal = await ComputeGlBalanceAsync(companyId, acc.Id, asOfExclusive);
            var apSub = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.DocumentDate < asOfExclusive
                    && d.Status != DocumentStatus.Draft
                    && d.Status != DocumentStatus.Voided
                    && d.Status != DocumentStatus.Rejected
                    && d.BalanceDue > 0
                    && (d.DocumentType == DocumentType.PurchaseInvoice
                        || d.DocumentType == DocumentType.Expense
                        || d.DocumentType == DocumentType.PaymentVoucher))
                .GroupBy(d => 1)
                .Select(g => new { Total = g.Sum(d => d.BalanceDue), Count = g.Count() })
                .FirstOrDefaultAsync();
            var subBal = apSub?.Total ?? 0m;
            var subCount = apSub?.Count ?? 0;
            // AP is liability — credit balance is the natural "positive" here.
            // Our GL helper returns Dr-Cr so liabilities come back negative.
            // Compare absolute balances for clarity.
            var glBalNorm = -glBal;  // flip so positive = "we owe"
            var variance = Math.Round(glBalNorm - subBal, 2);
            lines.Add(new ReconLine("เจ้าหนี้การค้า (AP)", acc.AccountCode, acc.AccountName, acc.Id,
                glBalNorm, subBal, variance, Math.Abs(variance) <= tolerance, subCount,
                subCount == 0 ? "ไม่มีเอกสารฝั่ง AP ที่ค้างชำระ" : null));
        }

        // ===== Inventory — สินค้าคงเหลือ =====
        var invAccounts = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted
                && a.AccountCode.StartsWith("113")
                && (a.AccountName.Contains("สินค้าคงเหลือ") || a.AccountName.Contains("สินค้า") )
                && a.AccountType == AccountType.Asset)
            .ToListAsync();
        foreach (var acc in invAccounts)
        {
            var glBal = await ComputeGlBalanceAsync(companyId, acc.Id, asOfExclusive);
            // Stock valuation = sum(CurrentStock × CostPrice) across tracked products
            var products = await _db.Products.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.TrackStock)
                .Select(p => new { p.CurrentStock, p.CostPrice })
                .ToListAsync();
            var subBal = products.Sum(p => p.CurrentStock * p.CostPrice);
            var variance = Math.Round(glBal - subBal, 2);
            lines.Add(new ReconLine("สินค้าคงเหลือ (Inventory)", acc.AccountCode, acc.AccountName, acc.Id,
                glBal, subBal, variance, Math.Abs(variance) <= tolerance, products.Count, null));
        }

        // ===== Fixed Assets =====
        var faAccounts = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted
                && (a.AccountCode.StartsWith("152") || a.AccountCode.StartsWith("153") || a.AccountCode.StartsWith("154"))
                && a.AccountType == AccountType.Asset)
            .ToListAsync();
        if (faAccounts.Count > 0)
        {
            var glBal = 0m;
            foreach (var acc in faAccounts)
                glBal += await ComputeGlBalanceAsync(companyId, acc.Id, asOfExclusive);
            var assets = await _db.FixedAssets.AsNoTracking()
                .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.Status != AssetStatus.Disposed)
                .ToListAsync();
            var subBal = assets.Sum(a => a.NetBookValue);
            var variance = Math.Round(glBal - subBal, 2);
            lines.Add(new ReconLine("สินทรัพย์ถาวร (Fixed Assets)",
                string.Join(",", faAccounts.Select(a => a.AccountCode)),
                "ทุกบัญชี 152x/153x/154x",
                faAccounts.First().Id,
                glBal, subBal, variance, Math.Abs(variance) <= tolerance, assets.Count, null));
        }

        // ===== Cash =====
        var cashAccounts = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted
                && a.AccountCode.StartsWith("1111")
                && a.AccountType == AccountType.Asset)
            .ToListAsync();
        foreach (var acc in cashAccounts)
        {
            var glBal = await ComputeGlBalanceAsync(companyId, acc.Id, asOfExclusive);
            // Cash has no separate sub-ledger; GL is authoritative. Report
            // it for completeness so the accountant has the as-of figure.
            lines.Add(new ReconLine("เงินสด (Cash)", acc.AccountCode, acc.AccountName, acc.Id,
                glBal, glBal, 0m, true, 0, "เงินสดไม่มี sub-ledger — GL คือยอดอ้างอิง"));
        }

        // ===== Bank =====
        var bankAccounts = await _db.Set<BankAccount>().AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive)
            .ToListAsync();
        foreach (var bk in bankAccounts)
        {
            if (!bk.LinkedAccountId.HasValue) continue;
            var acc = await _db.ChartOfAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == bk.LinkedAccountId.Value);
            if (acc == null) continue;
            var glBal = await ComputeGlBalanceAsync(companyId, acc.Id, asOfExclusive);
            // Sub-ledger = BankAccount.CurrentBalance (system maintains this
            // from imported statements + manual entries).
            var subBal = bk.CurrentBalance;
            var variance = Math.Round(glBal - subBal, 2);
            lines.Add(new ReconLine("ธนาคาร (Bank)", acc.AccountCode,
                $"{bk.BankName} {bk.AccountNumber}", acc.Id,
                glBal, subBal, variance, Math.Abs(variance) <= tolerance, 1, null));
        }

        return new ReconResult(
            asOf,
            tolerance,
            lines,
            lines.Count(l => l.IsReconciled),
            lines.Count(l => !l.IsReconciled));
    }

    /// <summary>
    /// GL ending balance of one account at an as-of date — sum of all
    /// Posted+Reversed JE lines (Dr - Cr) where entry date is BEFORE the
    /// as-of-exclusive date. Includes OpeningBalance rows that match
    /// the as-of's fiscal period's start.
    /// </summary>
    private async Task<decimal> ComputeGlBalanceAsync(Guid companyId, Guid accountId, DateTime asOfExclusive)
    {
        var movements = await (
            from line in _db.JournalEntryLines.AsNoTracking()
            join entry in _db.JournalEntries on line.JournalEntryId equals entry.Id
            where entry.CompanyId == companyId
                && line.AccountId == accountId
                && entry.EntryDate < asOfExclusive
                && (entry.Status == JournalEntryStatus.Posted || entry.Status == JournalEntryStatus.Reversed)
            select new { line.DebitAmount, line.CreditAmount }
        ).ToListAsync();
        var movementNet = movements.Sum(m => m.DebitAmount - m.CreditAmount);

        // Add opening balances whose fiscal period overlaps/precedes the as-of.
        var openings = await (
            from ob in _db.OpeningBalances.AsNoTracking()
            join fp in _db.FiscalPeriods on ob.FiscalPeriodId equals fp.Id
            where ob.CompanyId == companyId
                && ob.AccountId == accountId
                && fp.StartDate < asOfExclusive
            select new { ob.OpeningDebit, ob.OpeningCredit }
        ).ToListAsync();
        var openingNet = openings.Sum(o => o.OpeningDebit - o.OpeningCredit);

        return movementNet + openingNet;
    }
}

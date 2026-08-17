using Accounting.Models.DTOs.Accounting;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class TaxService
{
    // ============================================================
    // Task 4 — RD compliance extensions
    //   * Defer Input VAT (≤6 months per Thai law)
    //   * Filing lock + unlock
    //   * Reject & Reverse workflow (autonomous GL reversal)
    //   * PND.54 (foreign WHT) + PP.36 (foreign service VAT) processing
    // ============================================================

    /// <summary>
    /// Defer one Input-VAT-bearing document from its current period to a
    /// later one (up to 6 months by Thai Revenue Code Section 82/3).
    /// Creates a VatDeferral row; the source TaxReport regenerate flow
    /// must respect deferrals and exclude them, the target month must
    /// include them when generated.
    /// </summary>
    public async Task<VatDeferral> DeferInputVatAsync(
        Guid companyId, Guid documentId, int deferredToPeriod, string? reason, string userId)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        if (doc.VatAmount <= 0)
            throw new InvalidOperationException("เอกสารนี้ไม่มี VAT ที่จะเลื่อน");

        var docPeriod = doc.DocumentDate.Year * 100 + doc.DocumentDate.Month;
        // Validate within 6 months (e.g. doc=202601 → max defer to 202607).
        var docDate = doc.DocumentDate;
        var maxDefer = docDate.AddMonths(6);
        var maxPeriod = maxDefer.Year * 100 + maxDefer.Month;
        if (deferredToPeriod <= docPeriod)
            throw new InvalidOperationException("วันที่เลื่อนต้องอยู่หลังเดือนของเอกสาร");
        if (deferredToPeriod > maxPeriod)
            throw new InvalidOperationException(
                $"กฎหมายอนุญาตให้เลื่อน Input VAT ได้สูงสุด 6 เดือน — เลื่อนได้ไม่เกินงวด {maxPeriod}");

        // Find or pseudo-link the original TaxReport for audit. If none yet
        // (operator deferring proactively before generating the source month),
        // we still record the deferral; the regenerate flow can read it later.
        var originalReport = await _db.TaxReports
            .FirstOrDefaultAsync(r => r.CompanyId == companyId
                && r.TaxType == TaxType.VAT
                && r.Year == docDate.Year && r.Month == docDate.Month);

        var deferral = new VatDeferral
        {
            CompanyId = companyId,
            DocumentId = documentId,
            OriginalTaxReportId = originalReport?.Id ?? Guid.Empty,
            DeferredAmount = doc.VatAmount,
            DeferralReason = reason,
            DeferredFromPeriod = docPeriod,
            DeferredToPeriod = deferredToPeriod,
            CreatedBy = userId,
        };
        _db.VatDeferrals.Add(deferral);
        await _db.SaveChangesAsync();
        return deferral;
    }

    public async Task UnlockTaxFilingAsync(Guid companyId, Guid reportId, string userId, string reason)
    {
        var report = await _db.TaxReports
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");
        if (report.FilingLockedAt == null)
            throw new InvalidOperationException("รายงานนี้ไม่ได้ถูก lock");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("ต้องระบุเหตุผลในการ unlock");

        report.FilingLockedAt = null;
        report.FilingLockedBy = null;
        report.Status = TaxReportStatus.Draft;
        report.Notes = (report.Notes ?? "") + $"\n[UNLOCK by {userId} @ {DateTime.UtcNow:u}] {reason}";
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Cancel & Reverse — when the RD rejects a filing or the company
    /// discovers an error post-filing, this records the rejection and
    /// generates a counter JE that moves the previously-claimed Input
    /// VAT into a non-claimable VAT expense account so the period
    /// balances net to zero on that VAT.
    /// </summary>
    public async Task<TaxReportResponse> RejectAndReverseTaxReportAsync(
        Guid companyId, Guid reportId, string reason, Guid? nonClaimableVatAccountId, string userId)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("ต้องระบุเหตุผลในการ Reject");

        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        if (report.Status != TaxReportStatus.Filed)
            throw new InvalidOperationException("Reject ได้เฉพาะรายงานที่ Filed แล้ว");
        if (report.ReversalJournalEntryId.HasValue)
            throw new InvalidOperationException("รายงานนี้ถูก Reject + Reverse ไปแล้ว");

        // Find a default non-claimable VAT expense account if none provided.
        var nonClaim = nonClaimableVatAccountId.HasValue
            ? await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.Id == nonClaimableVatAccountId.Value && a.CompanyId == companyId)
            : await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.CompanyId == companyId
                    && a.AccountType == AccountType.Expense
                    && a.AccountName.Contains("VAT") && a.AccountName.Contains("ไม่"));
        if (nonClaim == null)
            throw new InvalidOperationException(
                "ไม่พบบัญชี 'VAT ขอคืนไม่ได้' (Non-claimable VAT Expense) — กรุณาสร้างหรือระบุ Id");

        var inputVatAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId
                && a.AccountCode.StartsWith("11317"))   // Standard Thai chart: 11317 = ภาษีซื้อ
            ?? await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.CompanyId == companyId
                    && a.AccountType == AccountType.Asset
                    && a.AccountName.Contains("ภาษีซื้อ"));
        if (inputVatAccount == null)
            throw new InvalidOperationException("ไม่พบบัญชีภาษีซื้อในผังบัญชี");

        if (report.InputVat <= 0)
            throw new InvalidOperationException("รายงานไม่มี Input VAT ที่ต้อง reverse");

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Build the reversal JE — Dr Non-claimable VAT Expense, Cr Input VAT.
            var lines = new List<JournalLineRequest>
            {
                new(nonClaim.Id, report.InputVat, 0,
                    $"Reject VAT คืน {report.Year}/{report.Month:D2} — {reason}"),
                new(inputVatAccount.Id, 0, report.InputVat,
                    $"Reverse Input VAT ที่ถูก Reject — รายงาน {report.Id.ToString("N")[..8]}"),
            };

            // Use the central CreateJournalEntryAsync via the IAccountingService
            // when injected; otherwise create directly here. To avoid a wider
            // DI surgery this partial just inserts the JE directly using a
            // running number scheme that matches the convention.
            var entryNumber = $"VR-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString("N")[..6].ToUpper()}";
            var entry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = DateTime.UtcNow.Date,
                JournalType = JournalType.General,
                Description = $"Reverse VAT Reject — รายงาน {report.Year}/{report.Month:D2} — {reason}",
                Reference = report.Id.ToString("N")[..8],
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                TotalDebit = report.InputVat,
                TotalCredit = report.InputVat,
                CreatedBy = userId,
            };
            _db.JournalEntries.Add(entry);

            int order = 1;
            foreach (var l in lines)
            {
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    JournalEntryId = entry.Id,
                    AccountId = l.AccountId,
                    DebitAmount = l.DebitAmount,
                    CreditAmount = l.CreditAmount,
                    Description = l.Description,
                    LineOrder = order++,
                });
            }

            report.RejectionReason = reason;
            report.RejectedAt = DateTime.UtcNow;
            report.RejectedBy = userId;
            report.ReversalJournalEntryId = entry.Id;
            // Lift the filing lock so corrections can be made.
            report.FilingLockedAt = null;
            report.Status = TaxReportStatus.Draft;

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return MapToResponse(report);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Whether a document is currently locked because a TaxReport that
    /// includes it has FilingLockedAt set. Used by the document edit guard.
    /// </summary>
    public async Task<bool> IsDocumentFilingLockedAsync(Guid companyId, Guid documentId)
    {
        // A filing-lock applies when ANY TaxReportLine references this
        // document and its parent TaxReport.FilingLockedAt is set.
        return await _db.TaxReports.AnyAsync(r =>
            r.CompanyId == companyId
            && r.FilingLockedAt != null
            && r.Lines.Any(l => l.DocumentId == documentId));
    }

    /// <summary>
    /// Adjust a freshly-generated VAT report's Input VAT to:
    ///   1. SUBTRACT documents that were deferred OUT of this month
    ///      (they show up on the deferred target month instead).
    ///   2. ADD documents that were deferred INTO this month from
    ///      earlier months.
    /// Recomputes report.InputVat + NetVat after both passes.
    /// </summary>
    private async Task ApplyVatDeferralsAsync(Guid companyId, int year, int month, TaxReport report,
        bool markClaims = true)
    {
        var period = year * 100 + month;
        var deferrals = await _db.VatDeferrals.AsNoTracking()
            .Where(d => d.CompanyId == companyId
                && (d.DeferredFromPeriod == period || d.DeferredToPeriod == period))
            .ToListAsync();
        if (deferrals.Count == 0) return;

        var subtract = deferrals
            .Where(d => d.DeferredFromPeriod == period && d.ClaimedAt == null)
            .Sum(d => d.DeferredAmount);
        var add = deferrals
            .Where(d => d.DeferredToPeriod == period && d.ClaimedAt == null)
            .Sum(d => d.DeferredAmount);

        // ⚠️ ต้องเป็น "บรรทัด" ไม่ใช่บวก/ลบ scalar — RecalcVatTotals คำนวณยอด
        // จาก lines ล้วน: การแก้ scalar ตรง ๆ จะถูกเขียนทับหายทันทีที่ผู้ใช้
        // ติ๊กบรรทัดใดก็ตาม (auto-save → RecalcVatTotals). ใช้ IncomeTypeCode
        // "INPUT" ยอด +/- ให้ Recalc นับได้เองทุกครั้ง
        var deferLineOrder = report.Lines.Count == 0 ? 1 : report.Lines.Max(l => l.LineOrder) + 1;
        if (subtract > 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = deferLineOrder++,
                // ให้ตัวเรียง §87 วางบรรทัดนี้ท้ายงวด (ไม่ใช่ 01/01/0001)
                TransactionDate = new DateTime(year, month, 1).AddMonths(1).AddDays(-1),
                Description = $"[เลื่อนเคลมออกจากงวดนี้] ภาษีซื้อเลื่อนไปงวดหน้า -{subtract:N2}",
                IncomeAmount = 0,
                TaxRate = 0,
                TaxAmount = -subtract,
                IncomeTypeCode = "INPUT"
            });
            report.Notes = (report.Notes ?? "") + $"\n[VAT Deferred OUT] -{subtract:N2} (เลื่อนการเคลม)";
        }
        if (add > 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = deferLineOrder++,
                // ให้ตัวเรียง §87 วางบรรทัดนี้ท้ายงวด (ไม่ใช่ 01/01/0001)
                TransactionDate = new DateTime(year, month, 1).AddMonths(1).AddDays(-1),
                Description = $"[เคลมที่เลื่อนมาจากเดือนก่อน] ภาษีซื้อยกเข้า +{add:N2}",
                IncomeAmount = 0,
                TaxRate = 0,
                TaxAmount = add,
                IncomeTypeCode = "INPUT"
            });
            report.Notes = (report.Notes ?? "") + $"\n[VAT Deferred IN] +{add:N2} (เคลมจากเดือนก่อน)";
            // Mark the incoming deferrals as claimed so they don't pile up.
            // (preview/export: markClaims=false — report ชั่วคราวห้าม stamp)
            if (markClaims)
            {
                var incoming = deferrals.Where(d => d.DeferredToPeriod == period && d.ClaimedAt == null).ToList();
                foreach (var d in incoming)
                {
                    var tracked = await _db.VatDeferrals.FirstOrDefaultAsync(x => x.Id == d.Id);
                    if (tracked != null)
                    {
                        tracked.ClaimedAt = DateTime.UtcNow;
                        tracked.ClaimedTaxReportId = report.Id;
                    }
                }
            }
        }
        // ใช้สูตรกลางจริง (RecalcVatTotals) — เดิมคำนวณ scalar เอง
        // `NetVat = Output − Input` ซึ่ง**ทิ้งเครดิตภาษีซื้อยกมา (VAT_CREDIT_CF)**
        // ที่ generate เพิ่งหักไว้ ⇒ งวดที่มีทั้งเครดิตยกมา + deferral ได้ NetVat
        // สูงเกินเท่าเครดิตยกมา (จ่ายเกิน) และพอผู้ใช้ติ๊กบรรทัดใดก็ตาม
        // RecalcVatTotals ของ auto-save จะคำนวณแบบมีเครดิต → ยอด "เปลี่ยนเอง"
        // ทั้งที่ไม่ได้แก้อะไร (สูตรเดียวกันต้องมีที่เดียว)
        RecalcVatTotals(report);
    }
}

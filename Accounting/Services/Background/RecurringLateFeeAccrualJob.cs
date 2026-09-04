using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Journal;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>คิดค่าปรับ AR ที่เกินกำหนดสำหรับเอกสารที่ออกจาก
/// RecurringTransaction ที่เปิด LateFeeEnabled. ทุก 24 ชั่วโมง:
///
/// 1. ดึง Document ที่ source = recurring (Reference startsWith "AUTO-")
///    และ recurring.LateFeeEnabled = true
/// 2. เฉพาะ status Approved/Sent/PartiallyPaid + BalanceDue > 0
/// 3. คำนวณ daysOverdue = today - DueDate - GraceDays (negative = skip)
/// 4. lateFee = balanceDue × ratePerDay/100 × daysOverdue
/// 5. clamp ที่ LateFeeMaxPercent ของ TotalAmount
/// 6. Post JE: Dr AR (1130) / Cr Other Income (49xx) — ไม่กระทบ Document.TotalAmount
/// 7. mark accrual record (DocumentNote) กัน double-accrue
///
/// runs ครั้งเดียวต่อวัน ตอนที่ 03:00 BKK (defer 2 ชั่วโมงหลัง startup).</summary>
public class RecurringLateFeeAccrualJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RecurringLateFeeAccrualJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public RecurringLateFeeAccrualJob(IServiceProvider services, ILogger<RecurringLateFeeAccrualJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "RecurringLateFeeAccrualJob failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
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
            lockDb, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(RecurringLateFeeAccrualJob),
            () => RunAsync(ct), _logger, ct: ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var today = DateTime.UtcNow.Date;

        // Recurring templates ที่ใช้กับ AR (Invoice/TaxInvoice เท่านั้น) +
        // LateFeeEnabled
        var recurrings = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.LateFeeEnabled && r.LateFeeRatePerDay > 0
                && r.DocumentType != null
                && (r.DocumentType == Models.Enums.DocumentType.Invoice
                    || r.DocumentType == Models.Enums.DocumentType.TaxInvoice))
            .Select(r => new {
                r.Id, r.CompanyId, r.Name, r.LateFeeRatePerDay,
                r.LateFeeGraceDays, r.LateFeeMaxPercent,
            })
            .ToListAsync(ct);

        if (recurrings.Count == 0) return;

        var arStatuses = new[] { DocumentStatus.Approved, DocumentStatus.Sent,
            DocumentStatus.PartiallyPaid, DocumentStatus.Overdue };
        int processed = 0, accrued = 0;
        decimal totalAccrued = 0m;

        foreach (var rec in recurrings)
        {
            if (ct.IsCancellationRequested) break;
            var refPrefix = $"AUTO-{rec.Name}";

            var docs = await db.Documents
                .Where(d => d.CompanyId == rec.CompanyId && !d.IsDeleted
                    && d.Reference == refPrefix
                    && arStatuses.Contains(d.Status)
                    && d.BalanceDue > 0.005m
                    && d.DueDate != null)
                .ToListAsync(ct);

            foreach (var doc in docs)
            {
                processed++;
                var due = doc.DueDate!.Value.Date;
                var daysOverdue = (int)(today - due).TotalDays - rec.LateFeeGraceDays;
                if (daysOverdue <= 0) continue;

                // กัน double accrue: ดู Notes ถ้า contains LATE_FEE_ACCRUED_<today>
                var marker = $"[LATE-FEE-{today:yyyy-MM-dd}]";
                if ((doc.InternalNotes ?? "").Contains(marker)) continue;

                var grossFee = Math.Round(doc.BalanceDue * rec.LateFeeRatePerDay / 100m * 1m, 2);
                if (grossFee < 0.01m) continue;

                // Cap check — sum prior late fees from InternalNotes (lightweight)
                if (rec.LateFeeMaxPercent.HasValue)
                {
                    var cap = Math.Round(doc.TotalAmount * rec.LateFeeMaxPercent.Value / 100m, 2);
                    var priorAccrued = ExtractPriorAccruedFromNotes(doc.InternalNotes);
                    if (priorAccrued + grossFee > cap)
                    {
                        var remain = cap - priorAccrued;
                        if (remain <= 0) continue;
                        grossFee = remain;
                    }
                }

                // Look up GL accounts
                var ar = await db.ChartOfAccounts.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.CompanyId == rec.CompanyId
                        && a.AccountCode == "1130" && a.IsActive, ct);
                var income = await db.ChartOfAccounts.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.CompanyId == rec.CompanyId
                        && (a.AccountCode == "4901" || a.AccountCode.StartsWith("49"))
                        && a.IsActive, ct);
                if (ar == null || income == null) continue;

                try
                {
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    await JournalEntryBuilder
                        .For(db, rec.CompanyId, today)
                        .Description($"ค่าปรับช้า {daysOverdue} วัน — {doc.DocumentNumber}")
                        .Reference(doc.DocumentNumber)
                        .SourceDocument(doc.Id)
                        .Debit(ar.Id, grossFee, $"AR ค่าปรับช้า {daysOverdue} วัน")
                        .Credit(income.Id, grossFee, $"รายได้ค่าปรับ {doc.DocumentNumber}")
                        .PostAsync("system:late-fee-cron", ct);

                    // Increase BalanceDue + Notes marker
                    doc.BalanceDue += grossFee;
                    doc.InternalNotes = (doc.InternalNotes ?? "") +
                        $" {marker} +{grossFee:N2}";
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    accrued++;
                    totalAccrued += grossFee;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Late fee accrual failed for doc {DocId}", doc.Id);
                }
            }
        }

        if (accrued > 0)
            _logger.LogInformation(
                "Late fee cycle: processed {Processed} docs, accrued {Accrued} fees, total {Total:N2}",
                processed, accrued, totalAccrued);
    }

    private static decimal ExtractPriorAccruedFromNotes(string? notes)
    {
        if (string.IsNullOrEmpty(notes)) return 0m;
        // Parse pattern: [LATE-FEE-yyyy-MM-dd] +123.45
        decimal sum = 0m;
        var idx = 0;
        while ((idx = notes.IndexOf("[LATE-FEE-", idx, StringComparison.Ordinal)) >= 0)
        {
            var plus = notes.IndexOf('+', idx);
            if (plus < 0) break;
            var space = notes.IndexOfAny(new[] { ' ', '[', '\n' }, plus + 1);
            var len = space > 0 ? space - plus - 1 : notes.Length - plus - 1;
            if (decimal.TryParse(notes.AsSpan(plus + 1, Math.Min(len, 12)), out var v)) sum += v;
            idx = plus + 1;
        }
        return sum;
    }
}

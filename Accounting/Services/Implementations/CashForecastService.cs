using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Treasury;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public interface ICashForecastService
{
    Task<CashForecastResponse> ForecastAsync(Guid companyId, CashForecastRequest req, CancellationToken ct = default);
}

/// <summary>
/// Day-by-day cash forecast that combines:
///   • current bank+cash balance (opening)
///   • open A/R Documents — expected in on DueDate
///   • open A/P Documents — expected out on DueDate
///   • Approved-not-yet-Paid PayrollRuns — expected out on PayDate
/// Output is a running balance per day with first-negative-date
/// detection and concentration / minimum-cash risk alerts.
///
/// Project-scoped mode filters AR / AP / payroll-allocated rows to
/// the project but keeps the company-wide opening cash — projects
/// don't have isolated bank accounts in this system.
/// </summary>
public class CashForecastService : ICashForecastService
{
    private readonly AccountingDbContext _db;

    public CashForecastService(AccountingDbContext db) { _db = db; }

    public async Task<CashForecastResponse> ForecastAsync(Guid companyId, CashForecastRequest req, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var horizon = today.AddDays(Math.Clamp(req.HorizonDays, 1, 365));

        // ===== Opening cash (sum of all active bank accounts + cash on hand) =====
        var openingCash = await _db.BankAccounts
            .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive)
            .SumAsync(b => b.CurrentBalance, ct);

        var arTypes = new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.BillingNote };
        var apTypes = new[] { DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.CertificateInLieu };

        // ===== A/R expected in =====
        var arQuery = _db.Documents
            .Include(d => d.Project)
            .Where(d => d.CompanyId == companyId
                && arTypes.Contains(d.DocumentType)
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Paid
                && d.BalanceDue > 0
                && !d.IsDeleted);
        if (req.ProjectId.HasValue) arQuery = arQuery.Where(d => d.ProjectId == req.ProjectId.Value);
        var arDocs = await arQuery.ToListAsync(ct);
        await _db.HydrateContactsAsync(companyId, arDocs);  // กัน INNER JOIN ตัดใบที่ contact ถูกลบ

        // ===== A/P expected out =====
        var apQuery = _db.Documents
            .Include(d => d.Project)
            .Where(d => d.CompanyId == companyId
                && apTypes.Contains(d.DocumentType)
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Paid
                && d.BalanceDue > 0
                && !d.IsDeleted);
        if (req.ProjectId.HasValue) apQuery = apQuery.Where(d => d.ProjectId == req.ProjectId.Value);
        var apDocs = await apQuery.ToListAsync(ct);
        await _db.HydrateContactsAsync(companyId, apDocs);  // กัน INNER JOIN ตัดใบที่ contact ถูกลบ

        // ===== Payroll expected out =====
        var payrollItems = new List<CashForecastItem>();
        if (req.IncludePayroll && !req.ProjectId.HasValue)
        {
            // Only company-wide forecast pulls payroll — when scoped to
            // a project, labour is already in ProjectCostEntry; double-
            // counting would inflate outflow.
            var runs = await _db.Set<Models.Entities.PayrollRun>()
                .Where(r => r.CompanyId == companyId
                    && r.Status == "Approved"
                    && r.PayDate <= horizon
                    && !r.IsDeleted)
                .Select(r => new {
                    r.Id, r.PayrollNumber, r.PayDate, r.TotalNetPay,
                    r.TotalWithholdingTax, r.TotalSocialSecurityEmployee, r.TotalSocialSecurityEmployer,
                })
                .ToListAsync(ct);
            payrollItems.AddRange(runs.Select(r => new CashForecastItem(
                "Payroll", r.PayDate,
                r.TotalNetPay + r.TotalSocialSecurityEmployer,  // SSO employer is also outflow
                r.Id, r.PayrollNumber, null, null, null, null,
                "เงินเดือน + ประกันสังคมนายจ้าง",
                r.PayDate >= today ? "OnTime" : "Overdue",
                (int)(r.PayDate.Date - today).TotalDays)));
        }

        // ===== Project name (when scoped) =====
        string? projectName = null;
        if (req.ProjectId.HasValue)
        {
            projectName = await _db.Projects
                .Where(p => p.Id == req.ProjectId.Value && p.CompanyId == companyId)
                .Select(p => p.Name).FirstOrDefaultAsync(ct);
        }

        // ===== Materialize inflow / outflow items =====
        var inflowItems = arDocs.Select(d => new CashForecastItem(
            "AR",
            (d.DueDate ?? d.DocumentDate).Date,
            d.BalanceDue,
            d.Id, d.DocumentNumber,
            d.ContactId, d.Contact?.Name,
            d.ProjectId, d.Project?.Name,
            null,
            (d.DueDate ?? d.DocumentDate).Date >= today ? "OnTime" : "Overdue",
            (int)((d.DueDate ?? d.DocumentDate).Date - today).TotalDays))
            .OrderBy(i => i.ExpectedDate).ToList();

        var outflowItems = apDocs.Select(d => new CashForecastItem(
            "AP",
            (d.DueDate ?? d.DocumentDate).Date,
            d.BalanceDue,
            d.Id, d.DocumentNumber,
            d.ContactId, d.Contact?.Name,
            d.ProjectId, d.Project?.Name,
            null,
            (d.DueDate ?? d.DocumentDate).Date >= today ? "OnTime" : "Overdue",
            (int)((d.DueDate ?? d.DocumentDate).Date - today).TotalDays))
            .Concat(payrollItems)
            .OrderBy(i => i.ExpectedDate).ToList();

        // ===== Day-by-day projection =====
        // Bucket overdue items into today (since they should be paid /
        // received already — pulling them into the past would hide the
        // gap from the running balance).
        var byDay = new List<CashForecastDay>();
        decimal running = openingCash;
        decimal minBalance = openingCash;
        DateTime? firstNegative = openingCash < 0 ? today : null;

        for (var d = today; d <= horizon; d = d.AddDays(1))
        {
            var dInflow = inflowItems
                .Where(i => i.ExpectedDate == d || (d == today && i.ExpectedDate < today))
                .ToList();
            var dOutflow = outflowItems
                .Where(i => i.ExpectedDate == d || (d == today && i.ExpectedDate < today))
                .ToList();
            var inSum = dInflow.Sum(i => i.Amount);
            var outSum = dOutflow.Sum(i => i.Amount);
            running += inSum - outSum;
            if (running < minBalance) minBalance = running;
            if (firstNegative == null && running < 0) firstNegative = d;
            byDay.Add(new CashForecastDay(d, inSum, outSum, inSum - outSum, running,
                dInflow.Count, dOutflow.Count));
        }

        // ===== Per-contact AR / AP summaries (aging buckets) =====
        var topDebtors = BuildArSummaries(arDocs, today)
            .OrderByDescending(s => s.TotalOutstanding).Take(20).ToList();
        var topCreditors = BuildApSummaries(apDocs, today)
            .OrderByDescending(s => s.TotalOutstanding).Take(20).ToList();

        // ===== Risk alerts =====
        var risks = BuildRiskAlerts(openingCash, running, minBalance, firstNegative,
            req.MinimumCashFloor, topDebtors, topCreditors, byDay);

        return new CashForecastResponse(
            today, horizon, req.ProjectId, projectName,
            openingCash, running, minBalance, firstNegative,
            inflowItems.Sum(i => i.Amount),
            outflowItems.Sum(i => i.Amount),
            byDay, inflowItems, outflowItems,
            topDebtors, topCreditors, risks);
    }

    private static List<ArContactSummary> BuildArSummaries(List<Models.Entities.Document> docs, DateTime today)
    {
        return docs
            .Where(d => d.Contact != null)
            .GroupBy(d => d.Contact!)
            .Select(g => {
                decimal current = 0, d130 = 0, d3160 = 0, d6190 = 0, over90 = 0;
                foreach (var doc in g)
                {
                    var dueDate = doc.DueDate ?? doc.DocumentDate;
                    var aging = Math.Max(0, (int)(today - dueDate.Date).TotalDays);
                    if (aging == 0) current += doc.BalanceDue;
                    else if (aging <= 30) d130 += doc.BalanceDue;
                    else if (aging <= 60) d3160 += doc.BalanceDue;
                    else if (aging <= 90) d6190 += doc.BalanceDue;
                    else over90 += doc.BalanceDue;
                }
                return new ArContactSummary(
                    g.Key.Id, g.Key.Name, g.Key.TaxId,
                    current, d130, d3160, d6190, over90,
                    current + d130 + d3160 + d6190 + over90,
                    g.Min(d => d.DocumentDate),
                    g.Count());
            })
            .ToList();
    }

    private static List<ApContactSummary> BuildApSummaries(List<Models.Entities.Document> docs, DateTime today)
    {
        return docs
            .Where(d => d.Contact != null)
            .GroupBy(d => d.Contact!)
            .Select(g => {
                decimal current = 0, d130 = 0, d3160 = 0, d6190 = 0, over90 = 0;
                foreach (var doc in g)
                {
                    var dueDate = doc.DueDate ?? doc.DocumentDate;
                    var aging = Math.Max(0, (int)(today - dueDate.Date).TotalDays);
                    if (aging == 0) current += doc.BalanceDue;
                    else if (aging <= 30) d130 += doc.BalanceDue;
                    else if (aging <= 60) d3160 += doc.BalanceDue;
                    else if (aging <= 90) d6190 += doc.BalanceDue;
                    else over90 += doc.BalanceDue;
                }
                return new ApContactSummary(
                    g.Key.Id, g.Key.Name, g.Key.TaxId,
                    current, d130, d3160, d6190, over90,
                    current + d130 + d3160 + d6190 + over90,
                    g.Min(d => d.DocumentDate),
                    g.Count());
            })
            .ToList();
    }

    private static List<CashRiskAlert> BuildRiskAlerts(
        decimal opening, decimal closing, decimal minBalance, DateTime? firstNegative,
        decimal? floor, List<ArContactSummary> debtors, List<ApContactSummary> creditors,
        List<CashForecastDay> byDay)
    {
        var risks = new List<CashRiskAlert>();

        if (firstNegative.HasValue)
            risks.Add(new CashRiskAlert("Critical",
                "เงินสดจะติดลบ",
                $"คาดว่าเงินสดจะติดลบในวันที่ {firstNegative.Value:yyyy-MM-dd} (ยอดต่ำสุด {minBalance:N2})",
                firstNegative, minBalance, null));
        else if (floor.HasValue && minBalance < floor.Value)
            risks.Add(new CashRiskAlert("Warning",
                $"เงินสดต่ำกว่ายอดสำรอง {floor.Value:N0}",
                $"ยอดเงินสดต่ำสุดในช่วงคาดการณ์ = {minBalance:N2} ต่ำกว่ายอดสำรองที่ตั้งไว้",
                byDay.FirstOrDefault(b => b.RunningBalance < floor.Value)?.Date,
                minBalance, null));

        var arTotal = debtors.Sum(d => d.TotalOutstanding);
        if (arTotal > 0)
        {
            var topAr = debtors.FirstOrDefault();
            if (topAr != null && topAr.TotalOutstanding / arTotal > 0.5m)
                risks.Add(new CashRiskAlert("Info",
                    "ลูกหนี้กระจุกตัว",
                    $"ลูกค้า {topAr.ContactName} คิดเป็น {(topAr.TotalOutstanding / arTotal):P0} ของลูกหนี้คงค้างทั้งหมด — ความเสี่ยงสูงถ้าจ่ายช้า",
                    null, topAr.TotalOutstanding, topAr.ContactId));

            var over90Pct = debtors.Sum(d => d.Over90Days) / arTotal;
            if (over90Pct > 0.20m)
                risks.Add(new CashRiskAlert("Warning",
                    "ลูกหนี้ค้างเกิน 90 วันสูง",
                    $"{over90Pct:P0} ของลูกหนี้คงค้างเกิน 90 วัน — พิจารณาตัดสำรองหนี้สงสัยจะสูญ",
                    null, debtors.Sum(d => d.Over90Days), null));
        }

        var apOverdue = creditors.Sum(c => c.Days1To30 + c.Days31To60 + c.Days61To90 + c.Over90Days);
        if (apOverdue > 0)
            risks.Add(new CashRiskAlert("Warning",
                "เจ้าหนี้ค้างเกินกำหนด",
                $"มีเจ้าหนี้ที่เกินกำหนดชำระแล้ว {apOverdue:N2} — ตรวจสอบเครดิตเทอม",
                null, apOverdue, null));

        return risks;
    }
}

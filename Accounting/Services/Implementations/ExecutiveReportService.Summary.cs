using Accounting.Data;
using Accounting.Models.DTOs.Executive;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class ExecutiveReportService
{
    public async Task<ExecutiveSummaryResponse> GetExecutiveSummaryAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var asOf = toDate;
        var span = (toDate - fromDate).TotalDays + 1;
        var prevTo = fromDate.AddDays(-1);
        var prevFrom = prevTo.AddDays(-(int)span + 1);

        var revenue = await SumRevenueAsync(companyId, fromDate, toDate);
        var revPrev = await SumRevenueAsync(companyId, prevFrom, prevTo);
        var expense = await SumExpenseAsync(companyId, fromDate, toDate);
        var expPrev = await SumExpenseAsync(companyId, prevFrom, prevTo);
        var cogs = await SumCogsAsync(companyId, fromDate, toDate);
        var depreciation = await GetDepreciationAsync(companyId, fromDate, toDate);
        var grossProfit = revenue - cogs;
        var operatingExpense = expense - cogs;
        var operatingProfit = grossProfit - operatingExpense;
        var netIncome = revenue - expense;
        var netPrev = revPrev - expPrev;
        var ebitda = operatingProfit + depreciation;

        var cashBalance = await GetCashAndBankAsync(companyId, asOf);
        var receivables = await GetBalanceAsync(companyId, AccountType.Asset, asOf, "113");
        var payables = await GetBalanceAsync(companyId, AccountType.Liability, asOf, "212");
        var totalCurrentAssets = await GetBalanceAsync(companyId, AccountType.Asset, asOf, "11");
        var totalCurrentLiabilities = await GetBalanceAsync(companyId, AccountType.Liability, asOf, "21");
        var workingCapital = totalCurrentAssets - totalCurrentLiabilities;

        // Invoices
        var invoices = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
            .Where(d => d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .Select(d => new { d.Status, d.DueDate, d.BalanceDue, d.TotalAmount })
            .ToListAsync();
        var invoiceCount = invoices.Count;
        var overdueInvoices = invoices.Where(i => i.BalanceDue > 0 && i.DueDate.HasValue && i.DueDate.Value < DateTime.UtcNow).ToList();
        var overdueCount = overdueInvoices.Count;
        var overdueAmount = overdueInvoices.Sum(i => i.BalanceDue);

        var ratiosResp = await GetFinancialRatiosAsync(companyId, asOf, fromDate, toDate);
        var cashPos = await BuildCashPositionAsync(companyId, asOf);
        var trends = await BuildTrendPointsAsync(companyId, fromDate, toDate, "monthly", 6);
        var topCustomers = await BuildTopCustomersAsync(companyId, fromDate, toDate, 5);
        var topSuppliers = await BuildTopSuppliersAsync(companyId, fromDate, toDate, 5);
        var alerts = BuildAlerts(revenue, expense, netIncome, cashPos, ratiosResp.Ratios, overdueAmount, overdueCount);

        var kpi = new ExecutiveKpi(
            R2(revenue), R2(revPrev), Pct(revenue - revPrev, Math.Abs(revPrev)),
            R2(expense), R2(expPrev), Pct(expense - expPrev, Math.Abs(expPrev)),
            R2(grossProfit), Pct(grossProfit, revenue),
            R2(operatingProfit), Pct(operatingProfit, revenue),
            R2(netIncome), R2(netPrev), Pct(netIncome - netPrev, Math.Abs(netPrev)),
            Pct(netIncome, revenue),
            R2(ebitda), Pct(ebitda, revenue),
            R2(cashBalance), R2(receivables), R2(payables),
            R2(workingCapital),
            invoiceCount, overdueCount, R2(overdueAmount));

        return new ExecutiveSummaryResponse(
            fromDate, toDate, asOf,
            kpi, ratiosResp.Ratios, cashPos, alerts,
            trends.revenue, trends.expense, trends.netIncome,
            topCustomers, topSuppliers, null);
    }

    private async Task<decimal> GetDepreciationAsync(Guid companyId, DateTime from, DateTime to)
    {
        // Account code 56 = Depreciation expense
        var lines = PostedLinesQuery(companyId, from, to)
            .Where(l => l.Account.AccountType == AccountType.Expense)
            .Where(l => l.Account.AccountCode.StartsWith("56"));
        var totals = await lines.GroupBy(_ => 1)
            .Select(g => new { D = g.Sum(x => x.DebitAmount), C = g.Sum(x => x.CreditAmount) })
            .FirstOrDefaultAsync();
        return totals == null ? 0 : (totals.D - totals.C);
    }

    private async Task<CashPositionSummary> BuildCashPositionAsync(Guid companyId, DateTime asOf)
    {
        var allCash = await GetBalanceAsync(companyId, AccountType.Asset, asOf, "111");
        var bankBal = await GetBalanceAsync(companyId, AccountType.Asset, asOf, "1112");
        var cashOnHand = allCash - bankBal;
        var total = cashOnHand + bankBal;

        var overduePay = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .Where(d => d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense || d.DocumentType == DocumentType.CertificateInLieu)
            .Where(d => d.BalanceDue > 0 && d.DueDate.HasValue && d.DueDate.Value < DateTime.UtcNow)
            .SumAsync(d => (decimal?)d.BalanceDue) ?? 0;

        var avail = total - overduePay;

        var thirtyAgo = asOf.AddDays(-30);
        var avgExpense = await SumExpenseAsync(companyId, thirtyAgo, asOf);
        var avgDaily = avgExpense / 30m;
        var runway = avgDaily > 0 ? (int)Math.Floor(total / avgDaily) : 999;

        return new CashPositionSummary(R2(cashOnHand), R2(bankBal), R2(total), R2(avail), runway, R2(avgDaily));
    }

    private List<ExecutiveAlert> BuildAlerts(
        decimal revenue, decimal expense, decimal netIncome,
        CashPositionSummary cash, FinancialRatios ratios,
        decimal overdueAR, int overdueCount)
    {
        var alerts = new List<ExecutiveAlert>();
        if (cash.CashRunwayDays < 30)
            alerts.Add(new ExecutiveAlert("critical", "cash",
                "เงินสดเหลือใช้น้อยกว่า 30 วัน",
                $"คาดว่าจะใช้เงินสดได้อีก {cash.CashRunwayDays} วัน หากไม่มีรายได้เพิ่ม", cash.Total));
        else if (cash.CashRunwayDays < 60)
            alerts.Add(new ExecutiveAlert("warning", "cash",
                "เงินสดเหลือใช้น้อยกว่า 60 วัน",
                $"คาดว่าจะใช้เงินสดได้อีก {cash.CashRunwayDays} วัน", cash.Total));

        if (ratios.CurrentRatio < 1m)
            alerts.Add(new ExecutiveAlert("critical", "cash",
                "อัตราส่วนทุนหมุนเวียนต่ำกว่า 1",
                $"Current Ratio = {ratios.CurrentRatio:F2} (ควร > 1.5)", ratios.CurrentRatio));

        if (overdueCount > 0)
            alerts.Add(new ExecutiveAlert(overdueAR > 100000m ? "warning" : "info", "ar",
                $"มีลูกหนี้เกินกำหนดชำระ {overdueCount} รายการ",
                $"ยอดรวม {overdueAR:N2} บาท ที่ต้องติดตาม", overdueAR));

        if (netIncome < 0)
            alerts.Add(new ExecutiveAlert("critical", "margin",
                "ผลประกอบการขาดทุนในงวดนี้",
                $"ขาดทุน {Math.Abs(netIncome):N2} บาท", netIncome));
        else if (revenue > 0 && (netIncome / revenue) < 0.05m)
            alerts.Add(new ExecutiveAlert("warning", "margin",
                "อัตรากำไรสุทธิต่ำกว่า 5%",
                $"Net Profit Margin = {Pct(netIncome, revenue):F2}%", netIncome));

        if (ratios.DebtToEquity > 2m)
            alerts.Add(new ExecutiveAlert("warning", "margin",
                "อัตราส่วนหนี้ต่อทุนสูง",
                $"D/E Ratio = {ratios.DebtToEquity:F2} (เสี่ยงต่อสภาพคล่อง)", ratios.DebtToEquity));

        if (alerts.Count == 0)
            alerts.Add(new ExecutiveAlert("info", "margin",
                "สถานะธุรกิจอยู่ในเกณฑ์ดี",
                "ไม่พบประเด็นสำคัญในงวดนี้"));
        return alerts;
    }
}

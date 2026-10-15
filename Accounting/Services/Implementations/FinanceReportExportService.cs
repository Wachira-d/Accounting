using System.Globalization;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Dimension;
using Accounting.Models.DTOs.Loan;
using Accounting.Models.DTOs.Project;
using Accounting.Models.DTOs.Treasury;
using Accounting.Services.Implementations.Cheque;
using Accounting.Services.Implementations.Forex;
using Accounting.Services.Implementations.PettyCash;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **ไฟล์ส่งออก (CSV / Excel) ของหน้าการเงินที่เหลือ** (2026-10-15 ทีม X5 · erp-review/2026-10-14/EXPORT_AUDIT.md §X5) —
/// สินเชื่อ · งบประมาณ · โครงการ · มิติ/สาขา · งวดบัญชี · ตรวจข้อมูล · วิเคราะห์ AR/AP · เงินสดย่อย · ตีราคาอัตราแลกเปลี่ยน ·
/// บริหารการเงิน 9 แท็บ · คาดการณ์เงินสด · เช็คคงค้าง
///
/// <para>หลักการเดียวกับ <see cref="AccountingReportExportService"/> (ทีม X3): <b>ตัวเลขทุกตัวมาจาก method ของ service ตัวเดียวกับที่หน้าเรียก</b>
/// ด้วยตัวกรองชุดเดียวกัน — ที่นี่แค่ (1) ดึงครบทุกแถว (วนทุกหน้า ไม่ตัดที่ pageSize — <c>PagedRequest</c> หนีบไว้ที่ 200)
/// (2) เติมชื่อที่ต้องใช้ตามรอยด้วยคิวรีที่กรอง CompanyId (3) ส่งให้ <see cref="FinanceReportSheets"/> + <see cref="TabularExport"/></para>
/// </summary>
public class FinanceReportExportService
{
    /// <summary>เพดานแถวของรายการแบบแบ่งหน้าต่อไฟล์ — เกินแล้วล้มดังบอกให้แคบตัวกรอง (ไม่ตัดเงียบ)</summary>
    public const int MaxRows = 50_000;
    private const int PageSize = 200;   // = เพดานของ PagedRequest

    private readonly AccountingDbContext _db;
    private readonly ILoanService? _loans;
    private readonly IBudgetService? _budgets;
    private readonly IProjectAccountingService? _projects;
    private readonly IAccountingService? _accounting;
    private readonly IDimensionalAccountingService? _dimensions;
    private readonly DataIntegrityReportService? _integrity;
    private readonly IArApAnalysisService? _arap;
    private readonly IPettyCashService? _pettyCash;
    private readonly IFxRevaluationService? _fx;
    private readonly IFinancialManagementService? _financial;
    private readonly ICashForecastService? _forecast;
    private readonly IChequeService? _cheques;

    public FinanceReportExportService(AccountingDbContext db,
        ILoanService? loans = null, IBudgetService? budgets = null, IProjectAccountingService? projects = null,
        IAccountingService? accounting = null, IDimensionalAccountingService? dimensions = null,
        DataIntegrityReportService? integrity = null, IArApAnalysisService? arap = null, IPettyCashService? pettyCash = null,
        IFxRevaluationService? fx = null, IFinancialManagementService? financial = null, ICashForecastService? forecast = null,
        IChequeService? cheques = null)
    {
        _db = db;
        _loans = loans; _budgets = budgets; _projects = projects; _accounting = accounting; _dimensions = dimensions;
        _integrity = integrity; _arap = arap; _pettyCash = pettyCash; _fx = fx; _financial = financial; _forecast = forecast;
        _cheques = cheques;
    }

    private static T Need<T>(T? svc) where T : class
        => svc ?? throw new InvalidOperationException($"{typeof(T).Name} ไม่ได้ลงทะเบียน — ส่งออกรายงานนี้ไม่ได้");

    private Task<List<string>> TitleAsync(Guid companyId, string report, string period, Guid? projectId = null)
        => AccountingReportExportService.ReportTitleAsync(_db, companyId, report, period, projectId, null, null);

    private static string AsOfNow() => "ณ วันที่ " + ThaiDate.ToThaiDisplayString(DateTime.UtcNow);

    private static string Period(DateTime? from, DateTime? to)
        => from.HasValue || to.HasValue
            ? $"ช่วง {(from.HasValue ? ThaiDate.ToThaiDisplayString(from.Value) : "แรกสุด")} – {(to.HasValue ? ThaiDate.ToThaiDisplayString(to.Value) : "ล่าสุด")}"
            : "ทุกช่วงวันที่";

    /// <summary>CSV ส่งออกได้ทีละแผ่น — <paramref name="part"/> เลือกแผ่น (ว่าง = แผ่นแรก) · ชื่อที่ไม่รู้จัก = 400 พร้อมรายชื่อที่ใช้ได้</summary>
    private static TabularFile Pick(TabularFormat format, string baseName, DateTime? from, DateTime? to, string? part,
        params (string Key, TabularSheet Sheet)[] sheets)
    {
        if (format == TabularFormat.Xlsx)
            return TabularExport.Render(format, baseName, from, to, sheets.Select(s => s.Sheet).ToArray());
        var key = (part ?? "").Trim().ToLowerInvariant();
        var hit = key.Length == 0 ? sheets[0] : sheets.FirstOrDefault(s => s.Key == key);
        if (hit.Sheet == null)
            throw new BusinessRuleException($"ส่วนที่ขอ \"{part}\" ไม่มีในรายงานนี้ — ใช้ part = {string.Join(" / ", sheets.Select(s => s.Key))}");
        return TabularExport.Render(format, key.Length == 0 || hit.Key == sheets[0].Key ? baseName : baseName + "_" + hit.Sheet.Name, from, to, hit.Sheet);
    }

    // ───────────────────────── สินเชื่อ ─────────────────────────

    /// <summary>สินเชื่อทุกสัญญาตามตัวกรองของหน้า (สถานะ) + ตารางผ่อน + ประวัติชำระ · <paramref name="loanId"/> = สัญญาเดียว (ปุ่มในหน้ารายละเอียด) ·
    /// CSV <c>part=list|schedule|payments</c></summary>
    public async Task<TabularFile> LoansAsync(Guid companyId, string? status, Guid? loanId, string? part, TabularFormat format)
    {
        var svc = Need(_loans);
        List<LoanResponse> loans;
        if (loanId.HasValue)
            loans = new List<LoanResponse> { await svc.GetByIdAsync(companyId, loanId.Value) };
        else
            loans = await AllPagesAsync(p => svc.GetAllAsync(companyId, status, new PagedRequest(p, PageSize)), "สินเชื่อ");
        var accIds = loans.SelectMany(l => new[] { l.LoanAccountId, l.InterestExpenseAccountId, l.MoneyAccountId })
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var accounts = accIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && accIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.AccountCode + " " + a.AccountName);
        var schedules = new List<LoanScheduleExportRow>();
        var payments = new List<LoanPaymentExportRow>();
        var key = (part ?? "").Trim().ToLowerInvariant();
        if (format == TabularFormat.Xlsx || key is "schedule" or "payments")
        {
            foreach (var l in loans)
            {
                if (format == TabularFormat.Xlsx || key == "schedule")
                    schedules.AddRange((await svc.GetScheduleAsync(companyId, l.Id)).Select(s => new LoanScheduleExportRow(l.LoanNumber, l.Name, s)));
                if (format == TabularFormat.Xlsx || key == "payments")
                    payments.AddRange((await svc.GetPaymentsAsync(companyId, l.Id)).OrderBy(p => p.PaymentDate)
                        .Select(p => new LoanPaymentExportRow(l.LoanNumber, l.Name, p)));
            }
        }
        var summary = await svc.GetSummaryAsync(companyId);
        var title = await TitleAsync(companyId, loanId.HasValue ? $"สินเชื่อ {loans[0].LoanNumber} — {loans[0].Name}" : "ทะเบียนสินเชื่อ", AsOfNow());
        if (!loanId.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(status)) title.Add("สถานะ: " + status);
            title.Add($"{loans.Count:#,##0} สัญญา · ทั้งบริษัท: เงินต้นคงเหลือ {summary.TotalOutstandingPrincipal:#,##0.00} · ค่างวดต่อเดือน {summary.TotalMonthlyPayment:#,##0.00}");
        }
        return Pick(format, loanId.HasValue ? "สินเชื่อ_" + loans[0].LoanNumber : "สินเชื่อ", null, null, part,
            ("list", FinanceReportSheets.Loans(loans, accounts, title)),
            ("schedule", FinanceReportSheets.LoanSchedules(schedules, title)),
            ("payments", FinanceReportSheets.LoanPayments(payments, title)));
    }

    // ───────────────────────── งบประมาณ ─────────────────────────

    public async Task<TabularFile> BudgetsAsync(Guid companyId, int? fiscalYear, TabularFormat format)
    {
        var svc = Need(_budgets);
        var items = await AllPagesAsync(p => svc.GetAllPagedAsync(companyId, fiscalYear, new PagedRequest(p, PageSize)), "งบประมาณ");
        var projects = await ProjectLabelsAsync(companyId, items.Where(b => b.ProjectId.HasValue).Select(b => b.ProjectId!.Value));
        var title = await TitleAsync(companyId, "รายการงบประมาณ", fiscalYear.HasValue ? $"ปีงบประมาณ {fiscalYear} (ค.ศ.)" : "ทุกปี");
        return TabularExport.Render(format, "งบประมาณ", null, null, FinanceReportSheets.Budgets(items, projects, title));
    }

    /// <summary>งบประมาณเทียบยอดจริง (ตัวเดียวกับปุ่ม "เปรียบเทียบ") · Excel + งบรายเดือน + แจ้งเตือนเกินงบ · CSV <c>part=vs-actual|monthly|alerts</c></summary>
    public async Task<TabularFile> BudgetVsActualAsync(Guid companyId, Guid budgetId, string? part, TabularFormat format)
    {
        var svc = Need(_budgets);
        var r = await svc.GetBudgetVsActualAsync(companyId, budgetId);
        var b = await svc.GetByIdAsync(companyId, budgetId);
        var title = await TitleAsync(companyId, $"งบประมาณเทียบยอดจริง — {r.BudgetName}",
            $"ปีงบประมาณ {r.FiscalYear} (ค.ศ.) · 1 ม.ค. – 31 ธ.ค. {r.FiscalYear + 543}", b.ProjectId);
        title.Add("ยอดจริง = ใบสำคัญที่ผ่านรายการแล้ว/กลับรายการแล้ว · บัญชีหมวด 2/3/4 กลับเครื่องหมาย (ด้านเครดิต)");
        return Pick(format, $"งบเทียบจริง_{r.FiscalYear}", null, null, part,
            ("vs-actual", FinanceReportSheets.BudgetVsActual(r, title)),
            ("monthly", FinanceReportSheets.BudgetMonthly(b, title)),
            ("alerts", FinanceReportSheets.BudgetAlerts(r.Alerts, title)));
    }

    // ───────────────────────── โครงการ ─────────────────────────

    public async Task<TabularFile> ProjectsAsync(Guid companyId, string? status, string? search, TabularFormat format)
    {
        var svc = Need(_projects);
        var items = await AllPagesAsync(p => svc.GetAllAsync(companyId, status, new PagedRequest(p, PageSize, search)), "โครงการ");
        var title = await TitleAsync(companyId, "ทะเบียนโครงการ", AsOfNow());
        if (!string.IsNullOrWhiteSpace(status)) title.Add("สถานะ: " + status);
        if (!string.IsNullOrWhiteSpace(search)) title.Add("ค้นหา: " + search);
        title.Add($"{items.Count:#,##0} โครงการ");
        return TabularExport.Render(format, "โครงการ", null, null, FinanceReportSheets.Projects(items, title));
    }

    /// <summary>
    /// กำไรขาดทุนโครงการ — ตัวเลขสรุป/หมวดจาก <c>GetProfitabilityAsync</c> (แท็บ "กำไร/ขาดทุน") · รายการต้นทุนทุกบรรทัดจาก
    /// <c>GetCostEntriesAsync</c> (แท็บ "ต้นทุน" — วนทุกหน้า) · กระแสเงินสดตั้งแต่ต้นปีจากงบกระแสเงินสดกรองโครงการ ·
    /// CSV <c>part=summary|breakdown|entries|cash-flow</c>
    /// </summary>
    public async Task<TabularFile> ProjectPnlAsync(Guid companyId, Guid projectId, string? part, TabularFormat format)
    {
        var svc = Need(_projects);
        var project = await svc.GetByIdAsync(companyId, projectId);
        var profit = await svc.GetProfitabilityAsync(companyId, projectId);
        var entries = await AllPagesAsync(p => svc.GetCostEntriesAsync(companyId, projectId, new PagedRequest(p, PageSize)), "รายการต้นทุน");
        var ids = entries.Select(e => e.Id).ToList();
        var links = new Dictionary<Guid, (Guid? Doc, Guid? Je)>();
        foreach (var chunk in ids.Chunk(1000))
            foreach (var x in await _db.ProjectCostEntries.AsNoTracking()
                         .Where(e => e.CompanyId == companyId && e.ProjectId == projectId && chunk.Contains(e.Id))
                         .Select(e => new { e.Id, e.DocumentId, e.JournalEntryId }).ToListAsync())
                links[x.Id] = (x.DocumentId, x.JournalEntryId);
        var docIds = links.Values.Where(v => v.Doc.HasValue).Select(v => v.Doc!.Value).Distinct().ToList();
        var jeIds = links.Values.Where(v => v.Je.HasValue).Select(v => v.Je!.Value).Distinct().ToList();
        var docNumbers = docIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && docIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.DocumentNumber);
        var jeNumbers = jeIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.JournalEntries.AsNoTracking().Where(j => j.CompanyId == companyId && jeIds.Contains(j.Id))
                .ToDictionaryAsync(j => j.Id, j => j.EntryNumber);
        var rows = entries.OrderBy(e => e.EntryDate).Select(e =>
        {
            links.TryGetValue(e.Id, out var l);
            return new ProjectCostExportRow(e,
                l.Doc.HasValue && docNumbers.TryGetValue(l.Doc.Value, out var dn) ? dn : null,
                l.Je.HasValue && jeNumbers.TryGetValue(l.Je.Value, out var jn) ? jn : null);
        }).ToList();

        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var from = new DateTime(today.Year, 1, 1);
        var title = await TitleAsync(companyId, $"กำไรขาดทุนโครงการ {project.Code} {project.Name}", AsOfNow());
        if (!string.IsNullOrWhiteSpace(project.CustomerName)) title.Add("ลูกค้า: " + project.CustomerName);
        var key = (part ?? "").Trim().ToLowerInvariant();
        TabularSheet cashFlowSheet;
        if (format == TabularFormat.Xlsx || key == "cash-flow")
        {
            var cf = await Need(_accounting).GetCashFlowStatementAsync(companyId, from, today, projectId);
            var cfTitle = new List<string>(title) { $"กระแสเงินสดตั้งแต่ต้นปี: {ThaiDate.ToThaiDisplayString(from)} – {ThaiDate.ToThaiDisplayString(today)}" };
            cashFlowSheet = AccountingReportSheets.CashFlow(cf, cfTitle);
        }
        else cashFlowSheet = FinanceReportSheets.ProjectCostBreakdown(profit, title);   // CSV ไม่ได้ขอแผ่นนี้ — ไม่ต้องคำนวณงบกระแสเงินสด
        return Pick(format, "กำไรขาดทุนโครงการ_" + project.Code, null, null, part,
            ("summary", FinanceReportSheets.ProjectPnlSummary(project, profit, title)),
            ("breakdown", FinanceReportSheets.ProjectCostBreakdown(profit, title)),
            ("entries", FinanceReportSheets.ProjectCostEntries(rows, profit.TotalCost, title)),
            ("cash-flow", cashFlowSheet));
    }

    // ───────────────────────── มิติ / สาขา / งวดบัญชี ─────────────────────────

    public async Task<TabularFile> DimensionPnlAsync(Guid companyId, Guid dimensionId, DateTime fromDate, DateTime toDate, TabularFormat format)
    {
        if (fromDate.Date > toDate.Date) throw new BusinessRuleException("วันที่เริ่มต้องไม่หลังวันที่สิ้นสุด");
        var r = await Need(_dimensions).GetDimensionPnLAsync(companyId, dimensionId, fromDate, toDate);
        var title = await TitleAsync(companyId, "กำไรขาดทุนตามมิติ — " + r.DimensionName, Period(fromDate, toDate));
        title.Add("นับเฉพาะรายการที่ปันส่วนเข้ามิติ (JournalLineDimension) ของใบสำคัญที่ผ่านรายการแล้ว");
        return TabularExport.Render(format, "กำไรขาดทุนตามมิติ", fromDate, toDate, FinanceReportSheets.DimensionPnl(r, title));
    }

    /// <summary>ทะเบียนศูนย์ต้นทุน/แผนก + สาขา · CSV <c>part=dimensions|branches</c></summary>
    public async Task<TabularFile> DimensionsAsync(Guid companyId, string? part, TabularFormat format)
    {
        var svc = Need(_dimensions);
        var tree = await svc.GetDimensionsAsync(companyId);
        var flat = new List<DimensionResponse>();
        var seen = new HashSet<Guid>();
        void Walk(IEnumerable<DimensionResponse>? nodes)
        {
            foreach (var n in nodes ?? Enumerable.Empty<DimensionResponse>())
            {
                if (!seen.Add(n.Id)) continue;
                flat.Add(n);
                Walk(n.Children);
            }
        }
        Walk(tree);
        var parents = flat.ToDictionary(d => d.Id, d => d.Code + " " + d.Name);
        var branches = await svc.GetBranchesAsync(companyId, includeInactive: true);
        var title = await TitleAsync(companyId, "ศูนย์ต้นทุน / แผนก / สาขา", AsOfNow());
        return Pick(format, "มิติและสาขา", null, null, part,
            ("dimensions", FinanceReportSheets.Dimensions(flat, parents, title)),
            ("branches", FinanceReportSheets.Branches(branches, title)));
    }

    public async Task<TabularFile> FiscalPeriodsAsync(Guid companyId, TabularFormat format)
    {
        var periods = await Need(_accounting).GetFiscalPeriodsAsync(companyId);
        var jeIds = periods.Where(p => p.YearEndJournalEntryId.HasValue).Select(p => p.YearEndJournalEntryId!.Value).Distinct().ToList();
        var jeNumbers = jeIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.JournalEntries.AsNoTracking().Where(j => j.CompanyId == companyId && jeIds.Contains(j.Id))
                .ToDictionaryAsync(j => j.Id, j => j.EntryNumber);
        var title = await TitleAsync(companyId, "งวดบัญชี", AsOfNow());
        return TabularExport.Render(format, "งวดบัญชี", null, null, FinanceReportSheets.FiscalPeriods(periods, jeNumbers, title));
    }

    // ───────────────────────── ตรวจข้อมูล ─────────────────────────

    public async Task<TabularFile> DataIntegrityAsync(Guid companyId, TabularFormat format)
    {
        var r = await Need(_integrity).GetAsync(companyId);
        var title = await TitleAsync(companyId, "ตรวจข้อมูลจากบั๊กที่แก้แล้ว", AsOfNow());
        title.Add(r.Total == 0 ? "ไม่พบรายการที่ต้องตรวจ" : $"พบ {r.Total} รายการ — " +
            string.Join(" · ", DataIntegritySections.Counts(r).Where(c => c.Count > 0).Select(c => $"{c.Section.Title} {c.Count}")));
        return TabularExport.Render(format, "ตรวจข้อมูล", null, null, FinanceReportSheets.DataIntegrity(r, title));
    }

    // ───────────────────────── วิเคราะห์ AR/AP ─────────────────────────

    /// <summary>ภาพรวม + อายุหนี้ + แนวโน้ม + ลูกหนี้/เจ้าหนี้สูงสุด + ค่าเผื่อหนี้สงสัยจะสูญ + ลูกค้าเสี่ยง ·
    /// CSV <c>part=overview|aging|trend|top-ar|top-ap|bad-debt|risk</c></summary>
    public async Task<TabularFile> ArApAnalysisAsync(Guid companyId, string? part, TabularFormat format)
    {
        var svc = Need(_arap);
        var o = await svc.GetOverviewAsync(companyId);
        var bd = await svc.GetBadDebtAnalysisAsync(companyId);
        var title = await TitleAsync(companyId, "วิเคราะห์ลูกหนี้/เจ้าหนี้", AsOfNow());
        var bdTitle = new List<string>(title) { "อัตราตั้งสำรองตามช่วงอายุหนี้ที่ระบบกำหนด (ยังไม่ใช่อัตราต่อบริษัท) — ผู้ทำบัญชีตรวจก่อนลงบัญชี" };
        return Pick(format, "วิเคราะห์ลูกหนี้เจ้าหนี้", null, null, part,
            ("overview", FinanceReportSheets.ArApOverview(o, title)),
            ("aging", FinanceReportSheets.ArApAging(o, title)),
            ("trend", FinanceReportSheets.ArApTrend(o.MonthlyTrend, title)),
            ("top-ar", FinanceReportSheets.ArApTopContacts("ลูกหนี้สูงสุด", o.TopReceivables, title)),
            ("top-ap", FinanceReportSheets.ArApTopContacts("เจ้าหนี้สูงสุด", o.TopPayables, title)),
            ("bad-debt", FinanceReportSheets.BadDebtBuckets(bd, bdTitle)),
            ("risk", FinanceReportSheets.BadDebtRiskContacts(bd.RiskContacts, bdTitle)));
    }

    // ───────────────────────── เงินสดย่อย ─────────────────────────

    public async Task<TabularFile> PettyCashFundsAsync(Guid companyId, TabularFormat format)
    {
        var funds = await Need(_pettyCash).ListAsync(companyId);
        var title = await TitleAsync(companyId, "กองทุนเงินสดย่อย", AsOfNow());
        return TabularExport.Render(format, "กองทุนเงินสดย่อย", null, null, FinanceReportSheets.PettyCashFunds(funds, title));
    }

    public async Task<TabularFile> PettyCashRegisterAsync(Guid companyId, Guid fundId, DateTime? fromDate, DateTime? toDate, TabularFormat format)
    {
        var r = await Need(_pettyCash).GetRegisterAsync(companyId, fundId, fromDate, toDate);
        var title = await TitleAsync(companyId, "ทะเบียนเงินสดย่อย — " + r.FundName, Period(fromDate, toDate));
        title.Add($"ผังบัญชีกองทุน: {r.FundAccountLabel ?? "ยังไม่ได้ตั้ง"} · วงเงิน {r.ImprestAmount:#,##0.00} · คงเหลือปัจจุบัน {r.CurrentBalance:#,##0.00}");
        return TabularExport.Render(format, "เงินสดย่อย_" + r.FundName, fromDate, toDate, FinanceReportSheets.PettyCashRegisterSheet(r, title));
    }

    // ───────────────────────── ตีราคาอัตราแลกเปลี่ยน ─────────────────────────

    /// <summary>อ่าน <c>USD:36.5,EUR:39.1</c> → อัตราปิด · ค่าที่อ่านไม่ออก/ไม่เป็นบวก = 400 (ไม่ข้ามเงียบ)</summary>
    public static Dictionary<string, decimal> ParseRates(string? raw)
    {
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || kv[0].Length != 3 || !decimal.TryParse(kv[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v <= 0)
                throw new BusinessRuleException($"อัตราแลกเปลี่ยน \"{part}\" อ่านไม่ออก — ใช้รูปแบบ USD:36.50 คั่นแต่ละสกุลด้วยจุลภาค");
            rates[kv[0].ToUpperInvariant()] = v;
        }
        if (rates.Count == 0) throw new BusinessRuleException("ใส่อัตราแลกเปลี่ยนอย่างน้อย 1 สกุล (เช่น USD:36.50)");
        return rates;
    }

    public async Task<TabularFile> FxRevaluationAsync(Guid companyId, DateTime asOf, string? rates, TabularFormat format)
    {
        var parsed = ParseRates(rates);
        var r = await Need(_fx).ProposeAsync(companyId, asOf, parsed);
        var title = await TitleAsync(companyId, "ตีราคาลูกหนี้/เจ้าหนี้สกุลเงินต่างประเทศ (ยังไม่ลงบัญชี)", "ณ วันที่ " + ThaiDate.ToThaiDisplayString(asOf));
        title.Add("อัตราปิด: " + string.Join(" · ", parsed.Select(kv => $"{kv.Key} {kv.Value.ToString("0.######", CultureInfo.InvariantCulture)}")));
        foreach (var w in r.Warnings) title.Add("⚠ " + w);
        return TabularExport.Render(format, "ตีราคาอัตราแลกเปลี่ยน", null, asOf, FinanceReportSheets.FxRevaluation(r, parsed, title));
    }

    // ───────────────────────── บริหารการเงิน ─────────────────────────

    /// <summary>หนึ่งแท็บของหน้า financial-mgmt (<paramref name="kind"/>) · Excel มีแผ่นรายละเอียดของแท็บนั้นด้วย (ตารางตัดจ่าย / การคืนมัดจำ / รายบรรทัด) ·
    /// CSV <c>part=detail</c> = แผ่นรายละเอียด</summary>
    public async Task<TabularFile> FinancialAsync(Guid companyId, string? kind, string? direction, string? part, TabularFormat format)
    {
        var svc = Need(_financial);
        var k = (kind ?? "").Trim().ToLowerInvariant();
        switch (k)
        {
            case "prepaid":
            {
                var items = await svc.GetPrepaidExpensesAsync(companyId);
                var t = await TitleAsync(companyId, "ค่าใช้จ่ายจ่ายล่วงหน้า", AsOfNow());
                return Pick(format, "จ่ายล่วงหน้า", null, null, part, ("list", FinanceReportSheets.Prepaids(items, t)), ("detail", FinanceReportSheets.PrepaidSchedules(items, t)));
            }
            case "deposits":
            {
                var items = await svc.GetDepositsAsync(companyId, direction);
                var t = await TitleAsync(companyId, "เงินมัดจำ", AsOfNow());
                if (!string.IsNullOrWhiteSpace(direction)) t.Add("ทิศทาง: " + (direction == "Paid" ? "จ่าย" : "รับ"));
                return Pick(format, "เงินมัดจำ", null, null, part, ("list", FinanceReportSheets.Deposits(items, t)), ("detail", FinanceReportSheets.DepositRefunds(items, t)));
            }
            case "accrued":
            {
                var items = await svc.GetAccruedExpensesAsync(companyId);
                var t = await TitleAsync(companyId, "ค่าใช้จ่ายค้างจ่าย", AsOfNow());
                return TabularExport.Render(format, "ค้างจ่าย", null, null, FinanceReportSheets.Accrueds(items, t));
            }
            case "bad-debt":
            {
                var items = await svc.GetBadDebtAllowancesAsync(companyId);
                var t = await TitleAsync(companyId, "ค่าเผื่อหนี้สงสัยจะสูญ", AsOfNow());
                return Pick(format, "ค่าเผื่อหนี้สงสัยจะสูญ", null, null, part, ("list", FinanceReportSheets.BadDebtAllowances(items, t)), ("detail", FinanceReportSheets.BadDebtAllowanceLines(items, t)));
            }
            case "obsolescence":
            {
                var items = await svc.GetInventoryObsolescencesAsync(companyId);
                var t = await TitleAsync(companyId, "ค่าเผื่อสินค้าล้าสมัย", AsOfNow());
                return Pick(format, "สินค้าล้าสมัย", null, null, part, ("list", FinanceReportSheets.Obsolescences(items, t)), ("detail", FinanceReportSheets.ObsolescenceLines(items, t)));
            }
            case "cit":
            {
                var items = await svc.GetCITListAsync(companyId);
                var t = await TitleAsync(companyId, "ภาษีเงินได้นิติบุคคล (ประมาณการ)", AsOfNow());
                return TabularExport.Render(format, "ภาษีนิติบุคคล", null, null, FinanceReportSheets.Cits(items, t));
            }
            case "appropriations":
            {
                var items = await svc.GetProfitAppropriationsAsync(companyId);
                var t = await TitleAsync(companyId, "การจัดสรรกำไร", AsOfNow());
                return TabularExport.Render(format, "จัดสรรกำไร", null, null, FinanceReportSheets.Appropriations(items, t));
            }
            case "capital":
            {
                var items = await svc.GetCapitalTransactionsAsync(companyId);
                var t = await TitleAsync(companyId, "รายการทุน", AsOfNow());
                return TabularExport.Render(format, "ทุน", null, null, FinanceReportSheets.Capitals(items, t));
            }
            case "investments":
            {
                var items = await svc.GetInvestmentsAsync(companyId);
                var t = await TitleAsync(companyId, "เงินลงทุนระยะสั้น", AsOfNow());
                return TabularExport.Render(format, "เงินลงทุน", null, null, FinanceReportSheets.Investments(items, t));
            }
            default:
                throw new BusinessRuleException("kind ต้องเป็น prepaid / deposits / accrued / bad-debt / obsolescence / cit / appropriations / capital / investments");
        }
    }

    // ───────────────────────── คาดการณ์เงินสด ─────────────────────────

    /// <summary>ตัวกรองเดียวกับ <c>GET cash-forecast/detailed</c> · Excel ทุกแผ่น (รายการรับ/จ่าย <b>ทั้งหมด</b> — หน้าแสดง 50 แรก) ·
    /// CSV <c>part=days|receipts|payments|debtors|creditors|risks|summary</c></summary>
    public async Task<TabularFile> CashForecastAsync(Guid companyId, int? days, Guid? projectId, decimal? minimumCashFloor, string? part, TabularFormat format)
    {
        var d = await Need(_forecast).ForecastAsync(companyId,
            new CashForecastRequest(HorizonDays: days ?? 30, ProjectId: projectId, IncludePayroll: true, MinimumCashFloor: minimumCashFloor));
        var title = await TitleAsync(companyId, "คาดการณ์กระแสเงินสด",
            $"{ThaiDate.ToThaiDisplayString(d.AsOfDate)} – {ThaiDate.ToThaiDisplayString(d.HorizonEndDate)} ({days ?? 30} วัน)", projectId);
        if (minimumCashFloor.HasValue && minimumCashFloor.Value > 0) title.Add($"เงินสดขั้นต่ำที่ตั้งไว้ {minimumCashFloor:#,##0.00}");
        return Pick(format, "คาดการณ์เงินสด", d.AsOfDate, d.HorizonEndDate, part,
            ("days", FinanceReportSheets.ForecastDays(d.ByDay, title)),
            ("receipts", FinanceReportSheets.ForecastItems("รับเข้า", d.UpcomingReceipts, title)),
            ("payments", FinanceReportSheets.ForecastItems("จ่ายออก", d.UpcomingPayments, title)),
            ("debtors", FinanceReportSheets.ForecastContacts("ลูกหนี้ค้าง", FinanceReportSheets.FromAr(d.TopDebtors), title)),
            ("creditors", FinanceReportSheets.ForecastContacts("เจ้าหนี้ค้าง", FinanceReportSheets.FromAp(d.TopCreditors), title)),
            ("risks", FinanceReportSheets.ForecastRisks(d.Risks, title)),
            ("summary", FinanceReportSheets.ForecastSummary(d, title)));
    }

    // ───────────────────────── เช็คคงค้าง ─────────────────────────

    /// <summary>เช็คคงค้าง — ชื่อผู้ติดต่อแปลงที่เซิร์ฟเวอร์ (เดิมไฟล์จาก JS ใช้รายชื่อ 200 รายแรกที่หน้าโหลด ⇒ รายที่เกินเป็นรหัสย่อ)</summary>
    public async Task<TabularFile> OutstandingChequesAsync(Guid companyId, Guid? bankAccountId, TabularFormat format)
    {
        var rows = await Need(_cheques).ListOutstandingRowsAsync(companyId, bankAccountId);
        var title = await TitleAsync(companyId, "เช็คคงค้าง", AsOfNow());
        title.Add($"{rows.Count:#,##0} ฉบับ");
        return TabularExport.Render(format, "เช็คคงค้าง", null, null, FinanceReportSheets.OutstandingCheques(rows, title));
    }

    // ───────────────────────── ภายใน ─────────────────────────

    /// <summary>วนทุกหน้าของ endpoint แบบแบ่งหน้า (หน้าละ 200 = เพดาน PagedRequest) · เกิน <see cref="MaxRows"/> = ล้มดัง</summary>
    private static async Task<List<T>> AllPagesAsync<T>(Func<int, Task<PagedResponse<T>>> fetch, string what)
    {
        var all = new List<T>();
        for (var page = 1; ; page++)
        {
            var res = await fetch(page);
            if (res.TotalCount > MaxRows)
                throw new BusinessRuleException($"{what}ตามตัวกรองมี {res.TotalCount:#,##0} แถว เกินเพดาน {MaxRows:#,##0} แถวต่อไฟล์ — แคบตัวกรองแล้วส่งออกทีละส่วน");
            all.AddRange(res.Items);
            if (res.Items.Count == 0 || page >= res.TotalPages) break;
        }
        return all;
    }

    private async Task<Dictionary<Guid, string>> ProjectLabelsAsync(Guid companyId, IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, string>();
        return await _db.Projects.AsNoTracking().Where(p => p.CompanyId == companyId && list.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Code + " " + p.Name);
    }
}

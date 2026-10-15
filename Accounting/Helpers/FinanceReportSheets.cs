using Accounting.Models.DTOs.Accounting;
using Accounting.Models.DTOs.ArApAnalysis;
using Accounting.Models.DTOs.Budget;
using Accounting.Models.DTOs.Dimension;
using Accounting.Models.DTOs.FinancialManagement;
using Accounting.Models.DTOs.Loan;
using Accounting.Models.DTOs.Project;
using Accounting.Models.DTOs.Treasury;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Cheque;
using Accounting.Services.Implementations.Forex;
using Accounting.Services.Implementations.PettyCash;

namespace Accounting.Helpers;

/// <summary>แถว "ตัวชี้วัด / ค่า / หน่วย" ของแผ่นสรุป (ค่าเป็นตัวเลขจริงใน Excel)</summary>
public sealed record ExportKeyValue(string Label, decimal? Value, string? Unit = null, string? Note = null);

/// <summary>หนึ่งงวดของตารางผ่อน พร้อมเลขที่สัญญา (ไฟล์รวมหลายสัญญา)</summary>
public sealed record LoanScheduleExportRow(string LoanNumber, string LoanName, LoanScheduleResponse Row);

/// <summary>หนึ่งการชำระ พร้อมเลขที่สัญญา</summary>
public sealed record LoanPaymentExportRow(string LoanNumber, string LoanName, LoanPaymentResponse Row);

/// <summary>รายการต้นทุนโครงการ + เลขเอกสาร/ใบสำคัญต้นทาง (service แปลงด้วยคิวรีที่กรองบริษัท)</summary>
public sealed record ProjectCostExportRow(ProjectCostEntryResponse Entry, string? DocumentNumber, string? JournalEntryNumber);

/// <summary>บรรทัดงบกำไรขาดทุนตามมิติ (รหัส · ชื่อ · จำนวนเงิน) — ใช้ทั้งแถวข้อมูลและแถวรวม</summary>
public sealed record DimensionPnlExportRow(string? AccountCode, string AccountName, decimal Amount);

/// <summary>ลูกหนี้/เจ้าหนี้ค้างของหน้าคาดการณ์เงินสด (สองชนิด DTO รูปเดียวกัน → แถวเดียว)</summary>
public sealed record ForecastContactExportRow(string ContactName, string? TaxId, decimal CurrentBalance, decimal Days1To30,
    decimal Days31To60, decimal Days61To90, decimal Over90Days, decimal TotalOutstanding, DateTime? OldestDocumentDate, int OpenDocumentCount);

/// <summary>
/// **คอลัมน์ของไฟล์ส่งออกหน้าการเงินที่เหลือ (ทีม X5 2026-10-15)** — สินเชื่อ · งบประมาณ · โครงการ · มิติ/สาขา · งวดบัญชี ·
/// ตรวจข้อมูล · วิเคราะห์ AR/AP · เงินสดย่อย · ตีราคาอัตราแลกเปลี่ยน · บริหารการเงิน (9 แท็บ) · คาดการณ์เงินสด · เช็คคงค้าง
///
/// <para>pure — รับ DTO ที่ service ของหน้าคืน (ตัวเดียวกับที่หน้าแสดง) แล้วจัดเป็น <see cref="TabularSheet"/> ผ่าน
/// <see cref="TabularExport"/> (ตัวเขียนเดียวของระบบ) · <b>ห้ามคำนวณยอดใหม่ที่นี่</b> — แถวรวมใช้ยอดที่ service คืน
/// (ข้อยกเว้นเดียว: ผลต่างรวมของงบประมาณ = งบรวม − ยอดจริงรวม ของ service ซึ่งหน้าก็แสดงแบบเดียวกัน)</para>
/// </summary>
public static class FinanceReportSheets
{
    // ───────────────────────── ป้าย (ตัวเดียวฝั่งเซิร์ฟเวอร์ของไฟล์ส่งออก) ─────────────────────────

    private static string LoanTypeLabel(string? t) => t switch
    {
        "BankLoan" => "สินเชื่อธนาคาร",
        "Mortgage" => "จำนอง",
        "LineOfCredit" => "วงเงิน OD",
        "PersonalLoan" => "สินเชื่อส่วนบุคคล",
        _ => t ?? "",
    };

    private static string InterestTypeLabel(string? t) => t switch
    {
        "Fixed" => "คงที่",
        "Floating" => "ลอยตัว",
        "FlatRate" => "flat rate",
        _ => t ?? "",
    };

    private static string FiscalPeriodStatusLabel(FiscalPeriodStatus s) => s switch
    {
        FiscalPeriodStatus.Open => "เปิด",
        FiscalPeriodStatus.Closed => "ปิดงวดชั่วคราว (เปิดใหม่ได้)",
        FiscalPeriodStatus.Locked => "ปิดถาวร (ปิดปีบัญชี)",
        _ => s.ToString(),
    };

    private static string DimensionTypeLabel(DimensionType t) => t switch
    {
        DimensionType.CostCenter => "ศูนย์ต้นทุน",
        DimensionType.ProfitCenter => "ศูนย์กำไร",
        DimensionType.Department => "แผนก",
        DimensionType.Branch => "สาขา",
        _ => t.ToString(),
    };

    /// <summary>ระดับความเสี่ยงลูกหนี้ (ค่า enum-string จาก <c>ArApAnalysisService</c>) → ไทย</summary>
    private static string RiskLevelLabel(string? level) => level switch
    {
        "Critical" => "วิกฤต",
        "High" => "สูง",
        "Medium" => "กลาง",
        "Low" => "ต่ำ",
        _ => level ?? "",
    };

    private static string ForecastSourceLabel(string? source) => source switch
    {
        "AR" => "ลูกหนี้",
        "AP" => "เจ้าหนี้",
        "Payroll" => "เงินเดือน",
        "Cheque" => "เช็ค",
        _ => source ?? "",
    };

    private static string ForecastTimingLabel(string? timing) => timing switch
    {
        "OnTime" => "ตามกำหนด",
        "Overdue" => "เกินกำหนด",
        _ => timing ?? "",
    };

    // ───────────────────────── สินเชื่อ ─────────────────────────

    public static TabularSheet Loans(IEnumerable<LoanResponse> loans, IReadOnlyDictionary<Guid, string> accounts, IEnumerable<string> title)
    {
        string? Acc(Guid? id) => id.HasValue && accounts.TryGetValue(id.Value, out var n) ? n : null;
        var cols = new[]
        {
            new TabularColumn<LoanResponse>("เลขที่สัญญา", l => l.LoanNumber),
            new TabularColumn<LoanResponse>("ชื่อสินเชื่อ", l => l.Name),
            new TabularColumn<LoanResponse>("ประเภท", l => LoanTypeLabel(l.LoanType)),
            new TabularColumn<LoanResponse>("ผู้ให้กู้", l => l.Lender),
            new TabularColumn<LoanResponse>("เงินต้น", l => l.PrincipalAmount, TabularCellKind.Money),
            new TabularColumn<LoanResponse>("อัตราดอกเบี้ย (% ต่อปี)", l => l.InterestRate, TabularCellKind.Number),
            new TabularColumn<LoanResponse>("วิธีคิดดอกเบี้ย", l => InterestTypeLabel(l.InterestType)),
            new TabularColumn<LoanResponse>("ระยะเวลา (เดือน)", l => l.TermMonths, TabularCellKind.Integer),
            new TabularColumn<LoanResponse>("วันรับเงินกู้", l => l.DisbursementDate, TabularCellKind.Date),
            new TabularColumn<LoanResponse>("วันชำระงวดแรก", l => l.FirstPaymentDate, TabularCellKind.Date),
            new TabularColumn<LoanResponse>("วันครบกำหนด", l => l.MaturityDate, TabularCellKind.Date),
            new TabularColumn<LoanResponse>("ความถี่การชำระ", l => l.RepaymentFrequency),
            new TabularColumn<LoanResponse>("ค่างวด", l => l.MonthlyPayment, TabularCellKind.Money),
            new TabularColumn<LoanResponse>("เงินต้นคงเหลือ", l => l.OutstandingPrincipal, TabularCellKind.Money),
            new TabularColumn<LoanResponse>("ดอกเบี้ยจ่ายสะสม", l => l.TotalInterestPaid, TabularCellKind.Money),
            new TabularColumn<LoanResponse>("สถานะ (รหัส)", l => l.Status),
            new TabularColumn<LoanResponse>("ผังบัญชีเงินกู้", l => Acc(l.LoanAccountId)),
            new TabularColumn<LoanResponse>("ผังบัญชีดอกเบี้ยจ่าย", l => Acc(l.InterestExpenseAccountId)),
            new TabularColumn<LoanResponse>("บัญชีรับ/จ่ายเงินกู้", l => Acc(l.MoneyAccountId)),
        };
        return TabularExport.Sheet("สินเชื่อ", cols, loans, title: title);
    }

    public static TabularSheet LoanSchedules(IEnumerable<LoanScheduleExportRow> rows, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<LoanScheduleExportRow>("เลขที่สัญญา", r => r.LoanNumber),
            new TabularColumn<LoanScheduleExportRow>("ชื่อสินเชื่อ", r => r.LoanName),
            new TabularColumn<LoanScheduleExportRow>("งวดที่", r => r.Row.InstallmentNumber, TabularCellKind.Integer),
            new TabularColumn<LoanScheduleExportRow>("วันครบกำหนด", r => r.Row.DueDate, TabularCellKind.Date),
            new TabularColumn<LoanScheduleExportRow>("เงินต้น", r => r.Row.PrincipalPortion, TabularCellKind.Money),
            new TabularColumn<LoanScheduleExportRow>("ดอกเบี้ย", r => r.Row.InterestPortion, TabularCellKind.Money),
            new TabularColumn<LoanScheduleExportRow>("ค่างวดรวม", r => r.Row.PaymentAmount, TabularCellKind.Money),
            new TabularColumn<LoanScheduleExportRow>("เงินต้นคงเหลือหลังงวด", r => r.Row.RemainingBalance, TabularCellKind.Money),
            new TabularColumn<LoanScheduleExportRow>("สถานะ", r => r.Row.IsPaid ? "ชำระแล้ว" : "รอชำระ"),
            new TabularColumn<LoanScheduleExportRow>("วันที่ชำระ", r => r.Row.PaidDate, TabularCellKind.Date),
        };
        return TabularExport.Sheet("ตารางผ่อน", cols, rows, title: title);
    }

    public static TabularSheet LoanPayments(IEnumerable<LoanPaymentExportRow> rows, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<LoanPaymentExportRow>("เลขที่สัญญา", r => r.LoanNumber),
            new TabularColumn<LoanPaymentExportRow>("ชื่อสินเชื่อ", r => r.LoanName),
            new TabularColumn<LoanPaymentExportRow>("วันที่ชำระ", r => r.Row.PaymentDate, TabularCellKind.Date),
            new TabularColumn<LoanPaymentExportRow>("งวดที่", r => r.Row.InstallmentNumber, TabularCellKind.Integer),
            new TabularColumn<LoanPaymentExportRow>("ยอดชำระรวม", r => r.Row.TotalPaid, TabularCellKind.Money),
            new TabularColumn<LoanPaymentExportRow>("เงินต้น", r => r.Row.PrincipalPaid, TabularCellKind.Money),
            new TabularColumn<LoanPaymentExportRow>("ดอกเบี้ย", r => r.Row.InterestPaid, TabularCellKind.Money),
            new TabularColumn<LoanPaymentExportRow>("ค่าปรับล่าช้า", r => r.Row.LateFee, TabularCellKind.Money),
            new TabularColumn<LoanPaymentExportRow>("วิธีชำระ", r => r.Row.PaymentMethod),
            new TabularColumn<LoanPaymentExportRow>("อ้างอิง/หมายเหตุ", r => r.Row.Reference),
        };
        return TabularExport.Sheet("ประวัติการชำระ", cols, rows, title: title);
    }

    // ───────────────────────── งบประมาณ ─────────────────────────

    public static TabularSheet Budgets(IEnumerable<BudgetResponse> budgets, IReadOnlyDictionary<Guid, string> projects, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<BudgetResponse>("ชื่องบประมาณ", b => b.Name),
            new TabularColumn<BudgetResponse>("ปีงบประมาณ (ค.ศ.)", b => b.FiscalYear, TabularCellKind.Integer),
            new TabularColumn<BudgetResponse>("โครงการ", b => b.ProjectId.HasValue && projects.TryGetValue(b.ProjectId.Value, out var p) ? p : "ทั้งบริษัท"),
            new TabularColumn<BudgetResponse>("งบประมาณรวม", b => b.TotalBudget, TabularCellKind.Money),
            new TabularColumn<BudgetResponse>("จำนวนบัญชี", b => b.Lines?.Count ?? 0, TabularCellKind.Integer),
            new TabularColumn<BudgetResponse>("สถานะ", b => b.IsActive ? "ใช้งาน" : "ปิด"),
            new TabularColumn<BudgetResponse>("สร้างเมื่อ", b => b.CreatedAt, TabularCellKind.Timestamp),
        };
        return TabularExport.Sheet("งบประมาณ", cols, budgets, title: title);
    }

    /// <summary>งบประมาณเทียบยอดจริง — คอลัมน์ชุดเดียวกับตารางบนหน้า (+ % ใช้ไป) · แถวรวม = TotalBudget/TotalActual ของ service</summary>
    public static TabularSheet BudgetVsActual(BudgetVsActualResponse r, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<BudgetVsActualLine>("รหัสบัญชี", l => l.AccountCode),
            new TabularColumn<BudgetVsActualLine>("ชื่อบัญชี", l => l.AccountName),
            new TabularColumn<BudgetVsActualLine>("งบประมาณทั้งปี", l => l.BudgetAmount, TabularCellKind.Money),
            new TabularColumn<BudgetVsActualLine>("ยอดจริง", l => l.ActualAmount, TabularCellKind.Money),
            new TabularColumn<BudgetVsActualLine>("ผลต่าง (งบ − จริง)", l => l.Variance, TabularCellKind.Money),
            new TabularColumn<BudgetVsActualLine>("ผลต่าง % ของงบ", l => Math.Round(l.VariancePercent, 2, MidpointRounding.AwayFromZero), TabularCellKind.Number),
            new TabularColumn<BudgetVsActualLine>("ใช้ไป % ของงบ", l => l.UsedPercent, TabularCellKind.Number),
            new TabularColumn<BudgetVsActualLine>("สถานะ", l => l.UsageStatus),
        };
        var footer = new[]
        {
            new BudgetVsActualLine(Guid.Empty, "", "รวม", r.TotalBudget, r.TotalActual, r.TotalBudget - r.TotalActual, 0m,
                BudgetUsageStatus.Label(r.TotalBudget, r.TotalActual), BudgetUsageStatus.UsedPercent(r.TotalBudget, r.TotalActual)),
        };
        return TabularExport.Sheet("งบเทียบยอดจริง", cols, r.Lines, footer, title);
    }

    /// <summary>งบรายเดือนต่อบัญชี (ม.ค.–ธ.ค.) — ข้อมูลที่ผู้ใช้กรอก (หน้าแก้ไขงบ)</summary>
    public static TabularSheet BudgetMonthly(BudgetResponse b, IEnumerable<string> title)
    {
        var cols = new List<TabularColumn<BudgetLineResponse>>
        {
            new("รหัสบัญชี", l => l.AccountCode),
            new("ชื่อบัญชี", l => l.AccountName),
        };
        var thMonths = new[] { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." };
        Func<BudgetLineResponse, decimal>[] month =
        {
            l => l.Month1, l => l.Month2, l => l.Month3, l => l.Month4, l => l.Month5, l => l.Month6,
            l => l.Month7, l => l.Month8, l => l.Month9, l => l.Month10, l => l.Month11, l => l.Month12,
        };
        for (var i = 0; i < 12; i++)
        {
            var f = month[i];
            cols.Add(new(thMonths[i], l => f(l), TabularCellKind.Money));
        }
        cols.Add(new("รวมทั้งปี", l => l.TotalBudget, TabularCellKind.Money));
        return TabularExport.Sheet("งบรายเดือน", cols, b.Lines ?? new List<BudgetLineResponse>(), title: title);
    }

    public static TabularSheet BudgetAlerts(IEnumerable<BudgetAlertResponse> alerts, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<BudgetAlertResponse>("รหัสบัญชี", a => a.AccountCode),
            new TabularColumn<BudgetAlertResponse>("ชื่อบัญชี", a => a.AccountName),
            new TabularColumn<BudgetAlertResponse>("ระดับ (รหัส)", a => a.AlertLevel),
            new TabularColumn<BudgetAlertResponse>("งบประมาณ", a => a.BudgetAmount, TabularCellKind.Money),
            new TabularColumn<BudgetAlertResponse>("ยอดจริง", a => a.ActualAmount, TabularCellKind.Money),
            new TabularColumn<BudgetAlertResponse>("เกินงบ %", a => Math.Round(a.OverBudgetPercent, 2, MidpointRounding.AwayFromZero), TabularCellKind.Number),
            new TabularColumn<BudgetAlertResponse>("ข้อความ", a => a.Message),
        };
        return TabularExport.Sheet("แจ้งเตือนเกินงบ", cols, alerts, title: title);
    }

    // ───────────────────────── โครงการ ─────────────────────────

    public static TabularSheet Projects(IEnumerable<ProjectResponse> projects, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<ProjectResponse>("รหัสโครงการ", p => p.Code),
            new TabularColumn<ProjectResponse>("ชื่อโครงการ", p => p.Name),
            new TabularColumn<ProjectResponse>("ลูกค้า", p => p.CustomerName),
            new TabularColumn<ProjectResponse>("วันเริ่ม", p => p.StartDate, TabularCellKind.Date),
            new TabularColumn<ProjectResponse>("วันสิ้นสุด", p => p.EndDate, TabularCellKind.Date),
            new TabularColumn<ProjectResponse>("สถานะ (รหัส)", p => p.Status),
            new TabularColumn<ProjectResponse>("วิธีเรียกเก็บเงิน (รหัส)", p => p.BillingMethod),
            new TabularColumn<ProjectResponse>("วิธีรับรู้รายได้ (รหัส)", p => p.RevenueRecognitionMethod),
            new TabularColumn<ProjectResponse>("มูลค่าสัญญา", p => p.ContractAmount, TabularCellKind.Money),
            new TabularColumn<ProjectResponse>("งบประมาณ", p => p.BudgetAmount, TabularCellKind.Money),
            new TabularColumn<ProjectResponse>("ต้นทุนจริง", p => p.ActualCost, TabularCellKind.Money),
            new TabularColumn<ProjectResponse>("รายได้รับรู้แล้ว", p => p.ActualRevenue, TabularCellKind.Money),
            new TabularColumn<ProjectResponse>("ความคืบหน้า %", p => p.CompletionPercent, TabularCellKind.Number),
            new TabularColumn<ProjectResponse>("รหัสระบบภายนอก", p => p.ExternalSystem == null ? null : p.ExternalSystem + ":" + p.ExternalId),
        };
        return TabularExport.Sheet("โครงการ", cols, projects, title: title);
    }

    /// <summary>สรุปกำไรขาดทุนโครงการ — ตัวเลขทุกตัวจาก <c>GetProfitabilityAsync</c> (ตัวเดียวกับแท็บ "กำไร/ขาดทุน")</summary>
    public static TabularSheet ProjectPnlSummary(ProjectResponse project, ProjectProfitabilityResponse p, IEnumerable<string> title)
    {
        var rows = new List<ExportKeyValue>
        {
            new("มูลค่าสัญญา", p.ContractAmount, "บาท"),
            new("รายได้รับรู้แล้ว", p.TotalRevenue, "บาท"),
            new("ต้นทุนรวม", p.TotalCost, "บาท"),
            new("กำไรขั้นต้น", p.GrossProfit, "บาท"),
            new("อัตรากำไรขั้นต้น", Math.Round(p.GrossProfitPercent, 2, MidpointRounding.AwayFromZero), "%", "ของรายได้รับรู้"),
            new("งบประมาณ", project.BudgetAmount, "บาท"),
            new("ผลต่างงบประมาณ (งบ − ต้นทุน)", p.BudgetVariance, "บาท", p.BudgetVariance >= 0 ? "ต่ำกว่างบ" : "เกินงบ"),
            new("ความคืบหน้า", p.CompletionPercent, "%"),
        };
        return KeyValues("สรุปกำไรขาดทุน", rows, title);
    }

    public static TabularSheet ProjectCostBreakdown(ProjectProfitabilityResponse p, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<ProjectCostBreakdownItem>("หมวดต้นทุน", b => b.Label),
            new TabularColumn<ProjectCostBreakdownItem>("รหัสหมวด", b => b.CostType),
            new TabularColumn<ProjectCostBreakdownItem>("ยอด", b => b.Amount, TabularCellKind.Money),
            new TabularColumn<ProjectCostBreakdownItem>("สัดส่วน %", b => b.Percent, TabularCellKind.Number),
            new TabularColumn<ProjectCostBreakdownItem>("จำนวนรายการ", b => b.EntryCount, TabularCellKind.Integer),
        };
        var footer = new[] { new ProjectCostBreakdownItem("", "รวมต้นทุน", p.TotalCost, p.TotalCost > 0 ? 100m : 0m,
            p.CostBreakdown.Sum(b => b.EntryCount), new List<ProjectCostEntryDetail>()) };
        return TabularExport.Sheet("ต้นทุนตามหมวด", cols, p.CostBreakdown, footer, title);
    }

    /// <summary>รายการต้นทุนทุกบรรทัด (ไม่ตัดที่ 200/หมวด แบบแท็บกำไร หรือ 20 แถวแบบแท็บต้นทุน) · แถวรวม = TotalCost ของ service</summary>
    public static TabularSheet ProjectCostEntries(IEnumerable<ProjectCostExportRow> rows, decimal totalCost, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<ProjectCostExportRow>("วันที่", r => r.Entry.EntryDate, TabularCellKind.Date),
            new TabularColumn<ProjectCostExportRow>("หมวด (รหัส)", r => r.Entry.CostType),
            new TabularColumn<ProjectCostExportRow>("รายละเอียด", r => r.Entry.Description),
            new TabularColumn<ProjectCostExportRow>("จำนวน", r => r.Entry.Quantity, TabularCellKind.Number),
            new TabularColumn<ProjectCostExportRow>("ราคาต่อหน่วย", r => r.Entry.UnitCost, TabularCellKind.Number),
            new TabularColumn<ProjectCostExportRow>("ยอด", r => r.Entry.Amount, TabularCellKind.Money),
            new TabularColumn<ProjectCostExportRow>("พฤติกรรมต้นทุน", r => r.Entry.CostBehavior == "Fixed" ? "คงที่" : "ผันแปร"),
            new TabularColumn<ProjectCostExportRow>("เรียกเก็บลูกค้าได้", r => r.Entry.IsBillable ? "ได้" : "ไม่ได้"),
            new TabularColumn<ProjectCostExportRow>("เรียกเก็บแล้ว", r => r.Entry.IsBilled ? "แล้ว" : "ยัง"),
            new TabularColumn<ProjectCostExportRow>("เอกสารต้นทาง", r => r.DocumentNumber),
            new TabularColumn<ProjectCostExportRow>("เลขที่ใบสำคัญ", r => r.JournalEntryNumber),
        };
        var footer = new[]
        {
            new ProjectCostExportRow(new ProjectCostEntryResponse(Guid.Empty, Guid.Empty, default, "", "รวมต้นทุน", 0m, 0m, totalCost, false, false), null, null),
        };
        // แถวรวม: ช่องวันที่/จำนวน/ราคาต่อหน่วย/ธง ไม่มีความหมาย ⇒ ล้างเป็นว่าง
        var sheet = TabularExport.Sheet("รายการต้นทุน", cols, rows, footer, title);
        foreach (var f in sheet.Footer)
            for (var i = 0; i < f.Length; i++)
                if (i != 2 && i != 5) f[i] = null;
        return sheet;
    }

    // ───────────────────────── มิติ / สาขา ─────────────────────────

    public static TabularSheet DimensionPnl(DimensionPnLResponse r, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<DimensionPnlExportRow>("รหัสบัญชี", x => x.AccountCode),
            new TabularColumn<DimensionPnlExportRow>("ชื่อบัญชี", x => x.AccountName),
            new TabularColumn<DimensionPnlExportRow>("จำนวนเงิน (เดบิต − เครดิต / ยอดปันส่วน)", x => x.Amount, TabularCellKind.Money),
        };
        var rows = r.Lines.Select(l => new DimensionPnlExportRow(l.AccountCode, l.AccountName, l.Amount));
        var footer = new[]
        {
            new DimensionPnlExportRow(null, "รายได้รวม", r.TotalRevenue),
            new DimensionPnlExportRow(null, "ค่าใช้จ่ายรวม", r.TotalExpenses),
            new DimensionPnlExportRow(null, "กำไร (ขาดทุน) สุทธิ", r.NetIncome),
        };
        return TabularExport.Sheet("กำไรขาดทุนตามมิติ", cols, rows, footer, title);
    }

    /// <summary>ทะเบียนมิติ (ศูนย์ต้นทุน/แผนก) — ต้นไม้ถูกแผ่เป็นแถว (ลูกตามหลังแม่) · <c>parents</c> = ชื่อแม่</summary>
    public static TabularSheet Dimensions(IEnumerable<DimensionResponse> flat, IReadOnlyDictionary<Guid, string> parents, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<DimensionResponse>("รหัส", d => d.Code),
            new TabularColumn<DimensionResponse>("ชื่อ", d => d.Name),
            new TabularColumn<DimensionResponse>("ชื่อ (อังกฤษ)", d => d.NameEn),
            new TabularColumn<DimensionResponse>("ชนิด", d => DimensionTypeLabel(d.DimensionType)),
            new TabularColumn<DimensionResponse>("ระดับ", d => d.Level, TabularCellKind.Integer),
            new TabularColumn<DimensionResponse>("ภายใต้", d => d.ParentId.HasValue && parents.TryGetValue(d.ParentId.Value, out var p) ? p : null),
            new TabularColumn<DimensionResponse>("ผู้รับผิดชอบ", d => d.ManagerName),
            new TabularColumn<DimensionResponse>("งบประมาณต่อปี", d => d.AnnualBudget, TabularCellKind.Money),
            new TabularColumn<DimensionResponse>("สถานะ", d => d.IsActive ? "ใช้งาน" : "ปิด"),
            new TabularColumn<DimensionResponse>("คำอธิบาย", d => d.Description),
        };
        return TabularExport.Sheet("ศูนย์ต้นทุน-แผนก", cols, flat, title: title);
    }

    public static TabularSheet Branches(IEnumerable<BranchResponse> branches, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<BranchResponse>("รหัสสาขา (ภายใน)", b => b.Code),
            new TabularColumn<BranchResponse>("ชื่อสาขา", b => b.Name),
            new TabularColumn<BranchResponse>("ชื่อ (อังกฤษ)", b => b.NameEn),
            new TabularColumn<BranchResponse>("รหัสสาขาสรรพากร", b => b.TaxBranchCode),
            new TabularColumn<BranchResponse>("สำนักงานใหญ่", b => b.IsHeadOffice ? "ใช่" : ""),
            new TabularColumn<BranchResponse>("ที่อยู่", b => b.Address),
            new TabularColumn<BranchResponse>("ตำบล/แขวง", b => b.SubDistrict),
            new TabularColumn<BranchResponse>("อำเภอ/เขต", b => b.District),
            new TabularColumn<BranchResponse>("จังหวัด", b => b.Province),
            new TabularColumn<BranchResponse>("รหัสไปรษณีย์", b => b.PostalCode),
            new TabularColumn<BranchResponse>("โทรศัพท์", b => b.Phone),
            new TabularColumn<BranchResponse>("อีเมล", b => b.Email),
            new TabularColumn<BranchResponse>("ผู้จัดการ", b => b.ManagerName),
            new TabularColumn<BranchResponse>("สถานะ", b => b.IsActive ? "ใช้งาน" : "ปิด"),
        };
        return TabularExport.Sheet("สาขา", cols, branches, title: title);
    }

    // ───────────────────────── งวดบัญชี ─────────────────────────

    public static TabularSheet FiscalPeriods(IEnumerable<FiscalPeriodResponse> periods, IReadOnlyDictionary<Guid, string> journalNumbers, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<FiscalPeriodResponse>("ชื่องวด", p => p.Name),
            new TabularColumn<FiscalPeriodResponse>("ปี (ค.ศ.)", p => p.Year, TabularCellKind.Integer),
            new TabularColumn<FiscalPeriodResponse>("เดือน", p => p.Month, TabularCellKind.Integer),
            new TabularColumn<FiscalPeriodResponse>("วันเริ่มต้น", p => p.StartDate, TabularCellKind.Date),
            new TabularColumn<FiscalPeriodResponse>("วันสิ้นสุด", p => p.EndDate, TabularCellKind.Date),
            new TabularColumn<FiscalPeriodResponse>("สถานะ", p => FiscalPeriodStatusLabel(p.Status)),
            new TabularColumn<FiscalPeriodResponse>("ปิดงวดเมื่อ", p => p.ClosedAt, TabularCellKind.Timestamp),
            new TabularColumn<FiscalPeriodResponse>("ปิดงวดโดย", p => p.ClosedBy),
            new TabularColumn<FiscalPeriodResponse>("ล็อกถาวรเมื่อ", p => p.LockedAt, TabularCellKind.Timestamp),
            new TabularColumn<FiscalPeriodResponse>("ล็อกโดย", p => p.LockedBy),
            new TabularColumn<FiscalPeriodResponse>("ใบสำคัญปิดปี", p => p.YearEndJournalEntryId.HasValue && journalNumbers.TryGetValue(p.YearEndJournalEntryId.Value, out var n) ? n : null),
        };
        return TabularExport.Sheet("งวดบัญชี", cols, periods, title: title);
    }

    // ───────────────────────── ตรวจข้อมูล ─────────────────────────

    public static TabularSheet DataIntegrity(DataIntegrityReport report, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<(DataIntegritySectionInfo Section, DataIntegritySuspect Row)>("หมวด", x => x.Section.Title),
            new TabularColumn<(DataIntegritySectionInfo Section, DataIntegritySuspect Row)>("เลขที่", x => x.Row.Number),
            new TabularColumn<(DataIntegritySectionInfo Section, DataIntegritySuspect Row)>("วันที่", x => x.Row.Date, TabularCellKind.Date),
            new TabularColumn<(DataIntegritySectionInfo Section, DataIntegritySuspect Row)>("รายละเอียด", x => x.Row.Detail),
            new TabularColumn<(DataIntegritySectionInfo Section, DataIntegritySuspect Row)>("ค่าที่เก็บไว้", x => x.Row.Stored, TabularCellKind.Money),
            new TabularColumn<(DataIntegritySectionInfo Section, DataIntegritySuspect Row)>("ค่าที่ควรเป็น / ถ้าบันทึกซ้ำจะได้", x => x.Row.Expected, TabularCellKind.Money),
            new TabularColumn<(DataIntegritySectionInfo Section, DataIntegritySuspect Row)>("ชนิด (รหัส)", x => x.Row.Kind),
            new TabularColumn<(DataIntegritySectionInfo Section, DataIntegritySuspect Row)>("แก้ได้จากหน้ารายงาน", x => x.Row.FixPath != null ? "ได้" : ""),
            new TabularColumn<(DataIntegritySectionInfo Section, DataIntegritySuspect Row)>("ลิงก์ในระบบ", x => x.Row.LinkUrl),
        };
        return TabularExport.Sheet("ตรวจข้อมูล", cols, DataIntegritySections.Rows(report).ToList(), title: title);
    }

    // ───────────────────────── วิเคราะห์ AR/AP ─────────────────────────

    public static TabularSheet ArApOverview(ArApOverviewResponse d, IEnumerable<string> title)
    {
        var rows = new List<ExportKeyValue>
        {
            new("ลูกหนี้รวม (AR)", d.TotalReceivables, "บาท"),
            new("เจ้าหนี้รวม (AP)", d.TotalPayables, "บาท"),
            new("สถานะสุทธิ (AR − AP)", d.NetPosition, "บาท"),
            new("อัตราเก็บหนี้ได้", d.CollectionRate, "%"),
            new("DSO (วันเก็บหนี้เฉลี่ย)", d.Dso, "วัน"),
            new("DPO (วันชำระเฉลี่ย)", d.Dpo, "วัน"),
            new("AR เกินกำหนด", d.OverdueArAmount, "บาท", $"{d.OverdueArCount} รายการ"),
            new("AP เกินกำหนด", d.OverdueApAmount, "บาท", $"{d.OverdueApCount} รายการ"),
        };
        return KeyValues("ภาพรวม", rows, title);
    }

    public static TabularSheet ArApAging(ArApOverviewResponse d, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<(string Side, ArApAgingSummary A)>("ฝั่ง", x => x.Side),
            new TabularColumn<(string Side, ArApAgingSummary A)>("ยังไม่ครบกำหนด", x => x.A.Current, TabularCellKind.Money),
            new TabularColumn<(string Side, ArApAgingSummary A)>("1-30 วัน", x => x.A.Days1To30, TabularCellKind.Money),
            new TabularColumn<(string Side, ArApAgingSummary A)>("31-60 วัน", x => x.A.Days31To60, TabularCellKind.Money),
            new TabularColumn<(string Side, ArApAgingSummary A)>("61-90 วัน", x => x.A.Days61To90, TabularCellKind.Money),
            new TabularColumn<(string Side, ArApAgingSummary A)>("เกิน 90 วัน", x => x.A.Over90, TabularCellKind.Money),
            new TabularColumn<(string Side, ArApAgingSummary A)>("รวม", x => x.A.Total, TabularCellKind.Money),
        };
        var rows = new (string Side, ArApAgingSummary A)[] { ("ลูกหนี้", d.ArAging), ("เจ้าหนี้", d.ApAging) };
        return TabularExport.Sheet("อายุหนี้", cols, rows, title: title);
    }

    public static TabularSheet ArApTrend(IEnumerable<ArApTrendItem> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<ArApTrendItem>("งวด (ค.ศ.)", i => i.Period),
            new TabularColumn<ArApTrendItem>("ยอดลูกหนี้ปลายเดือน", i => i.ArBalance, TabularCellKind.Money),
            new TabularColumn<ArApTrendItem>("ยอดเจ้าหนี้ปลายเดือน", i => i.ApBalance, TabularCellKind.Money),
            new TabularColumn<ArApTrendItem>("ลูกหนี้ใหม่", i => i.ArNew, TabularCellKind.Money),
            new TabularColumn<ArApTrendItem>("รับชำระ", i => i.ArPaid, TabularCellKind.Money),
            new TabularColumn<ArApTrendItem>("เจ้าหนี้ใหม่", i => i.ApNew, TabularCellKind.Money),
            new TabularColumn<ArApTrendItem>("จ่ายชำระ", i => i.ApPaid, TabularCellKind.Money),
        };
        return TabularExport.Sheet("แนวโน้ม 12 เดือน", cols, items, title: title);
    }

    public static TabularSheet ArApTopContacts(string name, IEnumerable<ArApTopContact> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<ArApTopContact>("ผู้ติดต่อ", c => c.ContactName),
            new TabularColumn<ArApTopContact>("เลขประจำตัวผู้เสียภาษี", c => c.TaxId),
            new TabularColumn<ArApTopContact>("ยอดค้าง", c => c.TotalBalance, TabularCellKind.Money),
            new TabularColumn<ArApTopContact>("เกินกำหนด", c => c.OverdueAmount, TabularCellKind.Money),
            new TabularColumn<ArApTopContact>("จำนวนเอกสาร", c => c.DocumentCount, TabularCellKind.Integer),
            new TabularColumn<ArApTopContact>("เอกสารเกินกำหนด", c => c.OverdueCount, TabularCellKind.Integer),
            new TabularColumn<ArApTopContact>("ค้างนานสุด (วัน)", c => c.MaxAgingDays, TabularCellKind.Integer),
        };
        return TabularExport.Sheet(name, cols, items, title: title);
    }

    /// <summary>ค่าเผื่อหนี้สงสัยจะสูญตามช่วงอายุ · แถวรวม = TotalReceivables / EstimatedBadDebt ของ service</summary>
    public static TabularSheet BadDebtBuckets(BadDebtAnalysisResponse d, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<BadDebtBucket>("ช่วงอายุหนี้", b => b.Label),
            new TabularColumn<BadDebtBucket>("ยอดค้างชำระ", b => b.Amount, TabularCellKind.Money),
            new TabularColumn<BadDebtBucket>("อัตราตั้งสำรอง %", b => b.ProvisionRate, TabularCellKind.Number),
            new TabularColumn<BadDebtBucket>("ค่าเผื่อหนี้สงสัยจะสูญ", b => b.EstimatedLoss, TabularCellKind.Money),
            new TabularColumn<BadDebtBucket>("จำนวนเอกสาร", b => b.DocumentCount, TabularCellKind.Integer),
        };
        var footer = new[] { new BadDebtBucket("รวม", d.TotalReceivables, 0m, d.EstimatedBadDebt, d.Buckets.Sum(b => b.DocumentCount)) };
        var sheet = TabularExport.Sheet("ค่าเผื่อหนี้สงสัยจะสูญ", cols, d.Buckets, footer, title);
        foreach (var f in sheet.Footer) f[2] = null;   // อัตรารวมไม่มีความหมาย
        return sheet;
    }

    public static TabularSheet BadDebtRiskContacts(IEnumerable<BadDebtContactItem> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<BadDebtContactItem>("ลูกค้า", c => c.ContactName),
            new TabularColumn<BadDebtContactItem>("ยอดเกินกำหนด", c => c.TotalOverdue, TabularCellKind.Money),
            new TabularColumn<BadDebtContactItem>("ค้างนานสุด (วัน)", c => c.MaxAgingDays, TabularCellKind.Integer),
            new TabularColumn<BadDebtContactItem>("ระดับเสี่ยง", c => RiskLevelLabel(c.RiskLevel)),
            new TabularColumn<BadDebtContactItem>("ประมาณการสูญเสีย", c => c.EstimatedLoss, TabularCellKind.Money),
        };
        return TabularExport.Sheet("ลูกค้าเสี่ยงสูง", cols, items, title: title);
    }

    // ───────────────────────── เงินสดย่อย ─────────────────────────

    public static TabularSheet PettyCashFunds(IEnumerable<PettyCashFundResponse> funds, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<PettyCashFundResponse>("ชื่อกองทุน", f => f.Name),
            new TabularColumn<PettyCashFundResponse>("ผังบัญชีของกองทุน", f => f.LinkedAccountCode == null ? null : f.LinkedAccountCode + " " + f.LinkedAccountName),
            new TabularColumn<PettyCashFundResponse>("วงเงิน", f => f.ImprestAmount, TabularCellKind.Money),
            new TabularColumn<PettyCashFundResponse>("คงเหลือ", f => f.CurrentBalance, TabularCellKind.Money),
            new TabularColumn<PettyCashFundResponse>("สถานะ", f => f.IsActive ? "ใช้งาน" : "ปิด"),
        };
        return TabularExport.Sheet("กองทุนเงินสดย่อย", cols, funds, title: title);
    }

    /// <summary>ทะเบียนเงินสดย่อย · แถวรวม = TotalIn/TotalOut ของ <see cref="PettyCashRegister"/></summary>
    public static TabularSheet PettyCashRegisterSheet(PettyCashRegister r, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<PettyCashRegisterRow>("วันที่", x => x.TransactionDate, TabularCellKind.Date),
            new TabularColumn<PettyCashRegisterRow>("ชนิด", x => x.TypeLabel),
            new TabularColumn<PettyCashRegisterRow>("รายละเอียด", x => x.Description),
            new TabularColumn<PettyCashRegisterRow>("เลขที่ใบเสร็จ/หลักฐาน", x => x.ReceiptReference),
            new TabularColumn<PettyCashRegisterRow>("รับเข้า (เติมเงิน)", x => x.AmountIn, TabularCellKind.Money),
            new TabularColumn<PettyCashRegisterRow>("จ่ายออก (เบิกจ่าย)", x => x.AmountOut, TabularCellKind.Money),
            new TabularColumn<PettyCashRegisterRow>("รหัสบัญชีค่าใช้จ่าย", x => x.ExpenseAccountCode),
            new TabularColumn<PettyCashRegisterRow>("ชื่อบัญชีค่าใช้จ่าย", x => x.ExpenseAccountName),
            new TabularColumn<PettyCashRegisterRow>("โครงการ", x => x.ProjectLabel),
            new TabularColumn<PettyCashRegisterRow>("เลขที่ใบสำคัญ", x => x.JournalEntryNumber),
            new TabularColumn<PettyCashRegisterRow>("รายจ่ายต้องห้าม §65 ตรี", x => x.IsNonDeductible ? "ใช่ " + x.NonDeductibleRuleCode : ""),
        };
        var footer = new[] { new PettyCashRegisterRow(Guid.Empty, default, "", "", "รวม", null, 0m, r.TotalIn, r.TotalOut,
            null, null, null, null, null, false, null, default) };
        var sheet = TabularExport.Sheet("ทะเบียนเงินสดย่อย", cols, r.Rows, footer, title);
        foreach (var f in sheet.Footer) f[0] = null;
        return sheet;
    }

    // ───────────────────────── ตีราคาอัตราแลกเปลี่ยน ─────────────────────────

    /// <summary>ผลคำนวณตีราคา (ยังไม่ลงบัญชี) · อัตราปิดตามที่ผู้ใช้กรอก · แถวรวม = TotalGain/TotalLoss/NetEffect ของ service</summary>
    public static TabularSheet FxRevaluation(FxRevaluationResult r, IReadOnlyDictionary<string, decimal> rates, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<FxRevaluationLine>("สกุลเงิน", l => l.Currency),
            new TabularColumn<FxRevaluationLine>("ฝั่ง", l => l.Direction == "AR" ? "ลูกหนี้ (AR)" : l.Direction == "AP" ? "เจ้าหนี้ (AP)" : l.Direction),
            new TabularColumn<FxRevaluationLine>("อัตราปิด (บาท/หน่วย)", l => l.Currency != null && rates.TryGetValue(l.Currency, out var x) ? (decimal?)x : null, TabularCellKind.Number),
            new TabularColumn<FxRevaluationLine>("ยอดเงินตราต่างประเทศ", l => l.OriginalFcyAmount, TabularCellKind.Money),
            new TabularColumn<FxRevaluationLine>("มูลค่าตามบัญชี (บาท)", l => l.BookedThbValue, TabularCellKind.Money),
            new TabularColumn<FxRevaluationLine>("มูลค่า ณ อัตราปิด (บาท)", l => l.CurrentThbValue, TabularCellKind.Money),
            new TabularColumn<FxRevaluationLine>("ผลต่าง (ปัจจุบัน − ตามบัญชี)", l => l.Variance, TabularCellKind.Money),
        };
        var footer = new[]
        {
            new FxRevaluationLine("", "รวมกำไร", 0m, 0m, 0m, r.TotalGain),
            new FxRevaluationLine("", "รวมขาดทุน", 0m, 0m, 0m, r.TotalLoss),
            new FxRevaluationLine("", "สุทธิ", 0m, 0m, 0m, r.NetEffect),
        };
        var sheet = TabularExport.Sheet("ตีราคาอัตราแลกเปลี่ยน", cols, r.Lines, footer, title);
        foreach (var f in sheet.Footer) { f[2] = null; f[3] = null; f[4] = null; f[5] = null; }
        return sheet;
    }

    // ───────────────────────── บริหารการเงิน (9 แท็บ) ─────────────────────────

    public static TabularSheet Prepaids(IEnumerable<PrepaidExpenseResponse> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<PrepaidExpenseResponse>("เลขที่", p => p.ReferenceNo),
            new TabularColumn<PrepaidExpenseResponse>("รายละเอียด", p => p.Description),
            new TabularColumn<PrepaidExpenseResponse>("วันเริ่ม", p => p.StartDate, TabularCellKind.Date),
            new TabularColumn<PrepaidExpenseResponse>("วันสิ้นสุด", p => p.EndDate, TabularCellKind.Date),
            new TabularColumn<PrepaidExpenseResponse>("จำนวนงวด", p => p.TotalPeriods, TabularCellKind.Integer),
            new TabularColumn<PrepaidExpenseResponse>("ยอดรวม", p => p.TotalAmount, TabularCellKind.Money),
            new TabularColumn<PrepaidExpenseResponse>("ตัดแล้ว", p => p.AmortizedAmount, TabularCellKind.Money),
            new TabularColumn<PrepaidExpenseResponse>("คงเหลือ", p => p.RemainingAmount, TabularCellKind.Money),
            new TabularColumn<PrepaidExpenseResponse>("สถานะ (รหัส)", p => p.Status),
            new TabularColumn<PrepaidExpenseResponse>("บัญชีจ่ายล่วงหน้า", p => p.PrepaidAccountName),
            new TabularColumn<PrepaidExpenseResponse>("บัญชีค่าใช้จ่าย", p => p.ExpenseAccountName),
        };
        return TabularExport.Sheet("จ่ายล่วงหน้า", cols, items, title: title);
    }

    public static TabularSheet PrepaidSchedules(IEnumerable<PrepaidExpenseResponse> items, IEnumerable<string> title)
    {
        var rows = items.SelectMany(p => (p.Schedules ?? new List<PrepaidScheduleResponse>()).Select(s => (p.ReferenceNo, S: s)));
        var cols = new[]
        {
            new TabularColumn<(string ReferenceNo, PrepaidScheduleResponse S)>("เลขที่", x => x.ReferenceNo),
            new TabularColumn<(string ReferenceNo, PrepaidScheduleResponse S)>("งวดที่", x => x.S.PeriodNumber, TabularCellKind.Integer),
            new TabularColumn<(string ReferenceNo, PrepaidScheduleResponse S)>("วันที่ตัดจ่าย", x => x.S.ScheduledDate, TabularCellKind.Date),
            new TabularColumn<(string ReferenceNo, PrepaidScheduleResponse S)>("จำนวน", x => x.S.Amount, TabularCellKind.Money),
            new TabularColumn<(string ReferenceNo, PrepaidScheduleResponse S)>("ตัดแล้ว", x => x.S.IsProcessed ? "แล้ว" : "ยัง"),
        };
        return TabularExport.Sheet("ตารางตัดจ่าย", cols, rows, title: title);
    }

    public static TabularSheet Deposits(IEnumerable<DepositResponse> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<DepositResponse>("เลขที่", d => d.ReferenceNo),
            new TabularColumn<DepositResponse>("รายละเอียด", d => d.Description),
            new TabularColumn<DepositResponse>("ประเภท", d => d.DepositType),
            new TabularColumn<DepositResponse>("ทิศทาง", d => d.Direction == "Paid" ? "จ่าย" : "รับ"),
            new TabularColumn<DepositResponse>("ผู้ติดต่อ", d => d.ContactName),
            new TabularColumn<DepositResponse>("วันที่", d => d.TransactionDate, TabularCellKind.Date),
            new TabularColumn<DepositResponse>("กำหนดคืน", d => d.ExpectedReturnDate, TabularCellKind.Date),
            new TabularColumn<DepositResponse>("จำนวน", d => d.Amount, TabularCellKind.Money),
            new TabularColumn<DepositResponse>("คืนแล้ว", d => d.RefundedAmount, TabularCellKind.Money),
            new TabularColumn<DepositResponse>("คงเหลือ", d => d.RemainingAmount, TabularCellKind.Money),
            new TabularColumn<DepositResponse>("สถานะ (รหัส)", d => d.Status),
            new TabularColumn<DepositResponse>("บัญชีเงินมัดจำ", d => d.DepositAccountName),
        };
        return TabularExport.Sheet("เงินมัดจำ", cols, items, title: title);
    }

    public static TabularSheet DepositRefunds(IEnumerable<DepositResponse> items, IEnumerable<string> title)
    {
        var rows = items.SelectMany(d => (d.Refunds ?? new List<DepositRefundResponse>()).Select(r => (d.ReferenceNo, R: r)));
        var cols = new[]
        {
            new TabularColumn<(string ReferenceNo, DepositRefundResponse R)>("เลขที่มัดจำ", x => x.ReferenceNo),
            new TabularColumn<(string ReferenceNo, DepositRefundResponse R)>("วันที่คืน", x => x.R.RefundDate, TabularCellKind.Date),
            new TabularColumn<(string ReferenceNo, DepositRefundResponse R)>("จำนวน", x => x.R.Amount, TabularCellKind.Money),
            new TabularColumn<(string ReferenceNo, DepositRefundResponse R)>("หมายเหตุ", x => x.R.Notes),
        };
        return TabularExport.Sheet("การคืนมัดจำ", cols, rows, title: title);
    }

    public static TabularSheet Accrueds(IEnumerable<AccruedExpenseResponse> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<AccruedExpenseResponse>("เลขที่", a => a.ReferenceNo),
            new TabularColumn<AccruedExpenseResponse>("รายละเอียด", a => a.Description),
            new TabularColumn<AccruedExpenseResponse>("ประเภท", a => a.ExpenseType),
            new TabularColumn<AccruedExpenseResponse>("วันที่ตั้งค้างจ่าย", a => a.AccrualDate, TabularCellKind.Date),
            new TabularColumn<AccruedExpenseResponse>("ยอด", a => a.Amount, TabularCellKind.Money),
            new TabularColumn<AccruedExpenseResponse>("จ่ายแล้ว", a => a.PaidAmount, TabularCellKind.Money),
            new TabularColumn<AccruedExpenseResponse>("คงเหลือ", a => a.RemainingAmount, TabularCellKind.Money),
            new TabularColumn<AccruedExpenseResponse>("สถานะ (รหัส)", a => a.Status),
            new TabularColumn<AccruedExpenseResponse>("ตั้งซ้ำ", a => a.IsRecurring ? a.RecurringFrequency ?? "ใช่" : ""),
            new TabularColumn<AccruedExpenseResponse>("บัญชีค่าใช้จ่าย", a => a.ExpenseAccountName),
            new TabularColumn<AccruedExpenseResponse>("บัญชีค่าใช้จ่ายค้างจ่าย", a => a.AccruedAccountName),
        };
        return TabularExport.Sheet("ค้างจ่าย", cols, items, title: title);
    }

    public static TabularSheet BadDebtAllowances(IEnumerable<BadDebtAllowanceResponse> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<BadDebtAllowanceResponse>("เลขที่", b => b.ReferenceNo),
            new TabularColumn<BadDebtAllowanceResponse>("วันที่", b => b.AllowanceDate, TabularCellKind.Date),
            new TabularColumn<BadDebtAllowanceResponse>("วิธี", b => b.Method),
            new TabularColumn<BadDebtAllowanceResponse>("ลูกหนี้รวม", b => b.TotalReceivable, TabularCellKind.Money),
            new TabularColumn<BadDebtAllowanceResponse>("ค่าเผื่อที่ต้องมี", b => b.AllowanceAmount, TabularCellKind.Money),
            new TabularColumn<BadDebtAllowanceResponse>("ค่าเผื่อเดิม", b => b.PreviousAllowance, TabularCellKind.Money),
            new TabularColumn<BadDebtAllowanceResponse>("ปรับปรุง", b => b.AdjustmentAmount, TabularCellKind.Money),
            new TabularColumn<BadDebtAllowanceResponse>("สถานะ (รหัส)", b => b.Status),
            new TabularColumn<BadDebtAllowanceResponse>("หมายเหตุ", b => b.Notes),
        };
        return TabularExport.Sheet("ค่าเผื่อหนี้สงสัยจะสูญ", cols, items, title: title);
    }

    public static TabularSheet BadDebtAllowanceLines(IEnumerable<BadDebtAllowanceResponse> items, IEnumerable<string> title)
    {
        var rows = items.SelectMany(b => (b.Lines ?? new List<BadDebtAllowanceLineResponse>()).Select(l => (b.ReferenceNo, L: l)));
        var cols = new[]
        {
            new TabularColumn<(string ReferenceNo, BadDebtAllowanceLineResponse L)>("เลขที่", x => x.ReferenceNo),
            new TabularColumn<(string ReferenceNo, BadDebtAllowanceLineResponse L)>("ลูกค้า", x => x.L.ContactName),
            new TabularColumn<(string ReferenceNo, BadDebtAllowanceLineResponse L)>("ช่วงอายุหนี้", x => x.L.AgingBucket),
            new TabularColumn<(string ReferenceNo, BadDebtAllowanceLineResponse L)>("ยอดค้าง", x => x.L.OutstandingAmount, TabularCellKind.Money),
            new TabularColumn<(string ReferenceNo, BadDebtAllowanceLineResponse L)>("อัตรา %", x => x.L.AllowancePercentage, TabularCellKind.Number),
            new TabularColumn<(string ReferenceNo, BadDebtAllowanceLineResponse L)>("ค่าเผื่อ", x => x.L.AllowanceAmount, TabularCellKind.Money),
        };
        return TabularExport.Sheet("รายละเอียดค่าเผื่อหนี้", cols, rows, title: title);
    }

    public static TabularSheet Obsolescences(IEnumerable<InventoryObsolescenceResponse> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<InventoryObsolescenceResponse>("เลขที่", b => b.ReferenceNo),
            new TabularColumn<InventoryObsolescenceResponse>("วันที่", b => b.AllowanceDate, TabularCellKind.Date),
            new TabularColumn<InventoryObsolescenceResponse>("วิธี", b => b.Method),
            new TabularColumn<InventoryObsolescenceResponse>("มูลค่าสินค้า", b => b.TotalInventoryValue, TabularCellKind.Money),
            new TabularColumn<InventoryObsolescenceResponse>("ค่าเผื่อที่ต้องมี", b => b.AllowanceAmount, TabularCellKind.Money),
            new TabularColumn<InventoryObsolescenceResponse>("ค่าเผื่อเดิม", b => b.PreviousAllowance, TabularCellKind.Money),
            new TabularColumn<InventoryObsolescenceResponse>("ปรับปรุง", b => b.AdjustmentAmount, TabularCellKind.Money),
            new TabularColumn<InventoryObsolescenceResponse>("สถานะ (รหัส)", b => b.Status),
            new TabularColumn<InventoryObsolescenceResponse>("หมายเหตุ", b => b.Notes),
        };
        return TabularExport.Sheet("สินค้าล้าสมัย", cols, items, title: title);
    }

    public static TabularSheet ObsolescenceLines(IEnumerable<InventoryObsolescenceResponse> items, IEnumerable<string> title)
    {
        var rows = items.SelectMany(b => (b.Lines ?? new List<InventoryObsolescenceLineResponse>()).Select(l => (b.ReferenceNo, L: l)));
        var cols = new[]
        {
            new TabularColumn<(string ReferenceNo, InventoryObsolescenceLineResponse L)>("เลขที่", x => x.ReferenceNo),
            new TabularColumn<(string ReferenceNo, InventoryObsolescenceLineResponse L)>("รหัสสินค้า", x => x.L.ProductCode),
            new TabularColumn<(string ReferenceNo, InventoryObsolescenceLineResponse L)>("ชื่อสินค้า", x => x.L.ProductName),
            new TabularColumn<(string ReferenceNo, InventoryObsolescenceLineResponse L)>("ช่วงอายุ", x => x.L.AgingBucket),
            new TabularColumn<(string ReferenceNo, InventoryObsolescenceLineResponse L)>("คงเหลือ", x => x.L.CurrentStock, TabularCellKind.Number),
            new TabularColumn<(string ReferenceNo, InventoryObsolescenceLineResponse L)>("มูลค่า", x => x.L.StockValue, TabularCellKind.Money),
            new TabularColumn<(string ReferenceNo, InventoryObsolescenceLineResponse L)>("อัตรา %", x => x.L.AllowancePercentage, TabularCellKind.Number),
            new TabularColumn<(string ReferenceNo, InventoryObsolescenceLineResponse L)>("ค่าเผื่อ", x => x.L.AllowanceAmount, TabularCellKind.Money),
        };
        return TabularExport.Sheet("รายละเอียดสินค้าล้าสมัย", cols, rows, title: title);
    }

    public static TabularSheet Cits(IEnumerable<CITResponse> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<CITResponse>("ปีภาษี", c => c.TaxYear),
            new TabularColumn<CITResponse>("งวด", c => c.TaxPeriod),
            new TabularColumn<CITResponse>("รายได้", c => c.TotalRevenue, TabularCellKind.Money),
            new TabularColumn<CITResponse>("ค่าใช้จ่าย", c => c.TotalExpenses, TabularCellKind.Money),
            new TabularColumn<CITResponse>("กำไรทางบัญชี", c => c.AccountingProfit, TabularCellKind.Money),
            new TabularColumn<CITResponse>("รายการบวกกลับ", c => c.AddBackItems, TabularCellKind.Money),
            new TabularColumn<CITResponse>("รายการหักออก", c => c.DeductionItems, TabularCellKind.Money),
            new TabularColumn<CITResponse>("กำไรสุทธิทางภาษี", c => c.TaxableProfit, TabularCellKind.Money),
            new TabularColumn<CITResponse>("อัตราภาษี", c => c.TaxRate, TabularCellKind.Number),
            new TabularColumn<CITResponse>("ภาษี", c => c.TaxAmount, TabularCellKind.Money),
            new TabularColumn<CITResponse>("เครดิตภาษีหัก ณ ที่จ่าย", c => c.WithholdingTaxCredit, TabularCellKind.Money),
            new TabularColumn<CITResponse>("เครดิตภาษีจ่ายล่วงหน้า (ภ.ง.ด.51)", c => c.PrepaidTaxCredit, TabularCellKind.Money),
            new TabularColumn<CITResponse>("ภาษีต้องชำระสุทธิ", c => c.NetTaxPayable, TabularCellKind.Money),
            new TabularColumn<CITResponse>("สถานะ (รหัส)", c => c.Status),
            new TabularColumn<CITResponse>("หมายเหตุ", c => c.Notes),
        };
        return TabularExport.Sheet("ภาษีนิติบุคคล", cols, items, title: title);
    }

    public static TabularSheet Appropriations(IEnumerable<ProfitAppropriationResponse> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<ProfitAppropriationResponse>("เลขที่", a => a.ReferenceNo),
            new TabularColumn<ProfitAppropriationResponse>("ปีบัญชี", a => a.FiscalYear),
            new TabularColumn<ProfitAppropriationResponse>("วันที่อนุมัติ", a => a.ApprovalDate, TabularCellKind.Date),
            new TabularColumn<ProfitAppropriationResponse>("กำไรสุทธิ", a => a.NetProfit, TabularCellKind.Money),
            new TabularColumn<ProfitAppropriationResponse>("สำรองตามกฎหมาย", a => a.LegalReserve, TabularCellKind.Money),
            new TabularColumn<ProfitAppropriationResponse>("เงินปันผล", a => a.DividendAmount, TabularCellKind.Money),
            new TabularColumn<ProfitAppropriationResponse>("ปันผลต่อหุ้น", a => a.DividendPerShare, TabularCellKind.Number),
            new TabularColumn<ProfitAppropriationResponse>("กำไรสะสมคงเหลือ", a => a.RetainedAmount, TabularCellKind.Money),
            new TabularColumn<ProfitAppropriationResponse>("สถานะ (รหัส)", a => a.Status),
            new TabularColumn<ProfitAppropriationResponse>("หมายเหตุ", a => a.Notes),
        };
        return TabularExport.Sheet("จัดสรรกำไร", cols, items, title: title);
    }

    public static TabularSheet Capitals(IEnumerable<CapitalTransactionResponse> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<CapitalTransactionResponse>("เลขที่", c => c.ReferenceNo),
            new TabularColumn<CapitalTransactionResponse>("วันที่", c => c.TransactionDate, TabularCellKind.Date),
            new TabularColumn<CapitalTransactionResponse>("ประเภท", c => c.TransactionType),
            new TabularColumn<CapitalTransactionResponse>("จำนวนหุ้น", c => c.ShareQuantity, TabularCellKind.Number),
            new TabularColumn<CapitalTransactionResponse>("มูลค่าหุ้นละ", c => c.ParValue, TabularCellKind.Number),
            new TabularColumn<CapitalTransactionResponse>("เงินรับ/จ่าย", c => c.PaidAmount, TabularCellKind.Money),
            new TabularColumn<CapitalTransactionResponse>("ส่วนเกินมูลค่าหุ้น", c => c.SharePremium, TabularCellKind.Money),
            new TabularColumn<CapitalTransactionResponse>("สถานะ (รหัส)", c => c.Status),
            new TabularColumn<CapitalTransactionResponse>("มติกรรมการ", c => c.BoardResolutionRef),
            new TabularColumn<CapitalTransactionResponse>("เลขจดทะเบียน DBD", c => c.DbrRegistrationRef),
            new TabularColumn<CapitalTransactionResponse>("หมายเหตุ", c => c.Notes),
        };
        return TabularExport.Sheet("ทุน", cols, items, title: title);
    }

    public static TabularSheet Investments(IEnumerable<InvestmentResponse> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<InvestmentResponse>("เลขที่", i => i.ReferenceNo),
            new TabularColumn<InvestmentResponse>("ประเภท", i => i.InvestmentType),
            new TabularColumn<InvestmentResponse>("รายละเอียด", i => i.Description),
            new TabularColumn<InvestmentResponse>("สถาบัน", i => i.InstitutionName),
            new TabularColumn<InvestmentResponse>("เลขที่บัญชี", i => i.AccountNumber),
            new TabularColumn<InvestmentResponse>("วันที่ซื้อ", i => i.PurchaseDate, TabularCellKind.Date),
            new TabularColumn<InvestmentResponse>("ครบกำหนด", i => i.MaturityDate, TabularCellKind.Date),
            new TabularColumn<InvestmentResponse>("วันที่ขาย", i => i.SaleDate, TabularCellKind.Date),
            new TabularColumn<InvestmentResponse>("ต้นทุน", i => i.PurchaseCost, TabularCellKind.Money),
            new TabularColumn<InvestmentResponse>("มูลค่าปัจจุบัน", i => i.CurrentValue, TabularCellKind.Money),
            new TabularColumn<InvestmentResponse>("เงินจากการขาย", i => i.SaleProceeds, TabularCellKind.Money),
            new TabularColumn<InvestmentResponse>("กำไร (ขาดทุน) จากการขาย", i => i.GainLoss, TabularCellKind.Money),
            new TabularColumn<InvestmentResponse>("ดอกเบี้ย %", i => i.InterestRate, TabularCellKind.Number),
            new TabularColumn<InvestmentResponse>("สถานะ (รหัส)", i => i.Status),
            new TabularColumn<InvestmentResponse>("บัญชีเงินลงทุน", i => i.InvestmentAccountName),
        };
        return TabularExport.Sheet("เงินลงทุน", cols, items, title: title);
    }

    // ───────────────────────── คาดการณ์เงินสด ─────────────────────────

    public static TabularSheet ForecastSummary(CashForecastResponse d, IEnumerable<string> title)
    {
        var rows = new List<ExportKeyValue>
        {
            new("เงินสดวันนี้", d.OpeningCash, "บาท", "ทั้งบริษัท (เงินสดไม่แยกตามโครงการ)"),
            new("รวมเข้าคาดการณ์", d.TotalExpectedIn, "บาท"),
            new("รวมออกคาดการณ์", d.TotalExpectedOut, "บาท"),
            new("คาดการณ์สิ้นช่วง", d.ProjectedClosingCash, "บาท", "ณ " + ThaiDate.ToThaiDisplayString(d.HorizonEndDate)),
            new("ต่ำสุดในช่วง", d.MinimumCashInPeriod, "บาท",
                d.FirstNegativeDate.HasValue ? "ติดลบครั้งแรก " + ThaiDate.ToThaiDisplayString(d.FirstNegativeDate.Value) : null),
        };
        return KeyValues("สรุป", rows, title);
    }

    public static TabularSheet ForecastDays(IEnumerable<CashForecastDay> days, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<CashForecastDay>("วันที่", b => b.Date, TabularCellKind.Date),
            new TabularColumn<CashForecastDay>("เงินเข้า", b => b.ExpectedIn, TabularCellKind.Money),
            new TabularColumn<CashForecastDay>("เงินออก", b => b.ExpectedOut, TabularCellKind.Money),
            new TabularColumn<CashForecastDay>("สุทธิ", b => b.NetChange, TabularCellKind.Money),
            new TabularColumn<CashForecastDay>("ยอดคงเหลือสะสม", b => b.RunningBalance, TabularCellKind.Money),
            new TabularColumn<CashForecastDay>("รายการเข้า", b => b.InflowItemCount, TabularCellKind.Integer),
            new TabularColumn<CashForecastDay>("รายการออก", b => b.OutflowItemCount, TabularCellKind.Integer),
        };
        return TabularExport.Sheet("รายวัน", cols, days, title: title);
    }

    /// <summary>รายการรับ/จ่ายที่คาดการณ์ — <b>ทุกรายการ</b> (หน้าแสดง 50 แรก)</summary>
    public static TabularSheet ForecastItems(string name, IEnumerable<CashForecastItem> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<CashForecastItem>("วันที่คาดการณ์", i => i.ExpectedDate, TabularCellKind.Date),
            new TabularColumn<CashForecastItem>("ที่มา", i => ForecastSourceLabel(i.Source)),
            new TabularColumn<CashForecastItem>("เลขที่เอกสาร", i => i.DocumentNumber),
            new TabularColumn<CashForecastItem>("ผู้ติดต่อ", i => i.ContactName),
            new TabularColumn<CashForecastItem>("โครงการ", i => i.ProjectName),
            new TabularColumn<CashForecastItem>("จำนวนเงิน", i => i.Amount, TabularCellKind.Money),
            new TabularColumn<CashForecastItem>("สถานะกำหนด", i => ForecastTimingLabel(i.TimingStatus)),
            new TabularColumn<CashForecastItem>("ห่างจากวันนี้ (วัน)", i => i.DaysFromToday, TabularCellKind.Integer),
            new TabularColumn<CashForecastItem>("ที่มาของวันที่ (รหัส)", i => i.TimingBasis),
            new TabularColumn<CashForecastItem>("เลื่อนตามประวัติ (วัน)", i => i.LagDaysApplied, TabularCellKind.Integer),
            new TabularColumn<CashForecastItem>("หมายเหตุ", i => i.Note),
        };
        return TabularExport.Sheet(name, cols, items, title: title);
    }

    public static TabularSheet ForecastContacts(string name, IEnumerable<ForecastContactExportRow> items, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<ForecastContactExportRow>("ผู้ติดต่อ", c => c.ContactName),
            new TabularColumn<ForecastContactExportRow>("เลขประจำตัวผู้เสียภาษี", c => c.TaxId),
            new TabularColumn<ForecastContactExportRow>("ยังไม่ครบกำหนด", c => c.CurrentBalance, TabularCellKind.Money),
            new TabularColumn<ForecastContactExportRow>("1-30 วัน", c => c.Days1To30, TabularCellKind.Money),
            new TabularColumn<ForecastContactExportRow>("31-60 วัน", c => c.Days31To60, TabularCellKind.Money),
            new TabularColumn<ForecastContactExportRow>("61-90 วัน", c => c.Days61To90, TabularCellKind.Money),
            new TabularColumn<ForecastContactExportRow>("เกิน 90 วัน", c => c.Over90Days, TabularCellKind.Money),
            new TabularColumn<ForecastContactExportRow>("รวมค้าง", c => c.TotalOutstanding, TabularCellKind.Money),
            new TabularColumn<ForecastContactExportRow>("เอกสารเก่าสุด", c => c.OldestDocumentDate, TabularCellKind.Date),
            new TabularColumn<ForecastContactExportRow>("จำนวนเอกสาร", c => c.OpenDocumentCount, TabularCellKind.Integer),
        };
        return TabularExport.Sheet(name, cols, items, title: title);
    }

    public static IEnumerable<ForecastContactExportRow> FromAr(IEnumerable<ArContactSummary> items) => items.Select(c =>
        new ForecastContactExportRow(c.ContactName, c.TaxId, c.CurrentBalance, c.Days1To30, c.Days31To60, c.Days61To90, c.Over90Days,
            c.TotalOutstanding, c.OldestDocumentDate, c.OpenDocumentCount));

    public static IEnumerable<ForecastContactExportRow> FromAp(IEnumerable<ApContactSummary> items) => items.Select(c =>
        new ForecastContactExportRow(c.ContactName, c.TaxId, c.CurrentBalance, c.Days1To30, c.Days31To60, c.Days61To90, c.Over90Days,
            c.TotalOutstanding, c.OldestDocumentDate, c.OpenDocumentCount));

    public static TabularSheet ForecastRisks(IEnumerable<CashRiskAlert> risks, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<CashRiskAlert>("ระดับ (รหัส)", r => r.Level),
            new TabularColumn<CashRiskAlert>("หัวข้อ", r => r.Title),
            new TabularColumn<CashRiskAlert>("รายละเอียด", r => r.Message),
            new TabularColumn<CashRiskAlert>("วันที่เกี่ยวข้อง", r => r.RelevantDate, TabularCellKind.Date),
            new TabularColumn<CashRiskAlert>("จำนวนเงิน", r => r.Amount, TabularCellKind.Money),
        };
        return TabularExport.Sheet("ความเสี่ยง", cols, risks, title: title);
    }

    // ───────────────────────── เช็คคงค้าง ─────────────────────────

    public static TabularSheet OutstandingCheques(IEnumerable<OutstandingChequeRow> rows, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<OutstandingChequeRow>("เลขที่เช็ค", c => c.ChequeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new TabularColumn<OutstandingChequeRow>("วันที่เช็ค", c => c.ChequeDate, TabularCellKind.Date),
            new TabularColumn<OutstandingChequeRow>("ทิศทาง", c => c.IsInbound ? "รับ" : "จ่าย"),
            new TabularColumn<OutstandingChequeRow>("ผู้ติดต่อ", c => c.ContactName),
            new TabularColumn<OutstandingChequeRow>("เลขประจำตัวผู้เสียภาษี", c => c.ContactTaxId),
            new TabularColumn<OutstandingChequeRow>("จำนวนเงิน", c => c.Amount, TabularCellKind.Money),
            new TabularColumn<OutstandingChequeRow>("สกุลเงิน", c => c.Currency),
            new TabularColumn<OutstandingChequeRow>("บัญชีธนาคาร", c => c.BankLabel),
            new TabularColumn<OutstandingChequeRow>("เล่มเช็ค", c => c.ChequeBookNumber),
            new TabularColumn<OutstandingChequeRow>("ธนาคารผู้ออก (เช็ครับ)", c => c.IssuingBankName),
            new TabularColumn<OutstandingChequeRow>("โครงการ", c => c.ProjectName),
            new TabularColumn<OutstandingChequeRow>("สถานะ", c => c.StatusLabel),
            new TabularColumn<OutstandingChequeRow>("หมายเหตุ", c => c.Notes),
        };
        return TabularExport.Sheet("เช็คคงค้าง", cols, rows, title: title);
    }

    // ───────────────────────── ภายใน ─────────────────────────

    private static TabularSheet KeyValues(string name, IEnumerable<ExportKeyValue> rows, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<ExportKeyValue>("รายการ", r => r.Label),
            new TabularColumn<ExportKeyValue>("ค่า", r => r.Value.HasValue ? (decimal?)Math.Round(r.Value.Value, 2, MidpointRounding.AwayFromZero) : null, TabularCellKind.Number),
            new TabularColumn<ExportKeyValue>("หน่วย", r => r.Unit),
            new TabularColumn<ExportKeyValue>("หมายเหตุ", r => r.Note),
        };
        return TabularExport.Sheet(name, cols, rows, title: title);
    }
}

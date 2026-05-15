using System.Text;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.DTOs.Payroll;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>Payroll management including payslip PDF generation</summary>
public class PayrollService : IPayrollService
{
    private readonly AccountingDbContext _db;
    private readonly IPdfGenerationService? _pdfService;
    private readonly IAccountingService? _accountingService;
    private readonly ISalaryAdvanceService? _salaryAdvanceService;

    // Thai personal income tax brackets (progressive)
    private static readonly (decimal UpperBound, decimal Rate)[] ThaiTaxBrackets =
    {
        (150_000m, 0.00m),
        (300_000m, 0.05m),
        (500_000m, 0.10m),
        (750_000m, 0.15m),
        (1_000_000m, 0.20m),
        (2_000_000m, 0.25m),
        (5_000_000m, 0.30m),
        (decimal.MaxValue, 0.35m)
    };

    // Social security constants
    private const decimal SsoRate = 0.05m;          // 5% employee contribution
    private const decimal SsoMaxBase = 15_000m;     // max salary base per month
    private const decimal SsoMaxContribution = 750m; // max monthly contribution

    public PayrollService(AccountingDbContext db, IPdfGenerationService? pdfService = null,
        IAccountingService? accountingService = null, ISalaryAdvanceService? salaryAdvanceService = null)
    {
        _db = db;
        _pdfService = pdfService;
        _accountingService = accountingService;
        _salaryAdvanceService = salaryAdvanceService;
    }

    // ===== Employees =====

    public async Task<EmployeeResponse> CreateEmployeeAsync(Guid companyId, CreateEmployeeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EmployeeCode))
            throw new InvalidOperationException("รหัสพนักงานห้ามว่าง");

        if (request.BaseSalary < 0)
            throw new InvalidOperationException("เงินเดือนฐานต้องไม่ติดลบ");

        if (request.StartDate > DateTime.UtcNow.AddYears(1))
            throw new InvalidOperationException("วันเริ่มงานต้องไม่เกิน 1 ปีข้างหน้า");

        if (!string.IsNullOrEmpty(request.CitizenId) && !Regex.IsMatch(request.CitizenId, @"^\d{13}$"))
            throw new InvalidOperationException("เลขบัตรประชาชนต้องเป็นตัวเลข 13 หลัก");

        var existing = await _db.Set<Employee>()
            .AnyAsync(e => e.CompanyId == companyId && e.EmployeeCode == request.EmployeeCode);
        if (existing)
            throw new InvalidOperationException($"รหัสพนักงาน {request.EmployeeCode} ซ้ำ");

        var employee = new Employee
        {
            CompanyId = companyId,
            EmployeeCode = request.EmployeeCode,
            TitleTh = request.TitleTh,
            FirstNameTh = request.FirstNameTh,
            LastNameTh = request.LastNameTh,
            FirstNameEn = request.FirstNameEn,
            LastNameEn = request.LastNameEn,
            CitizenId = request.CitizenId,
            DateOfBirth = request.DateOfBirth,
            Gender = request.Gender,
            Address = request.Address,
            Phone = request.Phone,
            Email = request.Email,
            Department = request.Department,
            Position = request.Position,
            EmploymentType = request.EmploymentType,
            StartDate = request.StartDate,
            BaseSalary = request.BaseSalary,
            SalaryType = request.SalaryType ?? "Monthly",
            BankName = request.BankName,
            BankAccountNumber = request.BankAccountNumber,
            BankAccountName = request.BankAccountName,
            SocialSecurityNumber = request.SocialSecurityNumber,
            SocialSecurityHospital = request.SocialSecurityHospital,
            IsSubjectToSocialSecurity = request.IsSubjectToSocialSecurity,
            HasProvidentFund = request.HasProvidentFund,
            ProvidentFundEmployeePercent = request.ProvidentFundEmployeePercent,
            ProvidentFundEmployerPercent = request.ProvidentFundEmployerPercent,
            BranchId = request.BranchId,
            DimensionId = request.DimensionId
        };

        _db.Set<Employee>().Add(employee);
        await _db.SaveChangesAsync();

        return MapToEmployeeResponse(employee);
    }

    public async Task<EmployeeResponse> GetEmployeeAsync(Guid companyId, Guid employeeId)
    {
        var employee = await _db.Set<Employee>()
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        return MapToEmployeeResponse(employee);
    }

    public async Task<PagedResponse<EmployeeResponse>> GetEmployeesAsync(Guid companyId, PagedRequest request)
    {
        var query = _db.Set<Employee>()
            .Where(e => e.CompanyId == companyId && !e.IsDeleted);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = $"%{request.Search}%";
            query = query.Where(e => EF.Functions.ILike(e.EmployeeCode, search)
                || EF.Functions.ILike(e.FirstNameTh, search)
                || EF.Functions.ILike(e.LastNameTh, search)
                || (e.FirstNameEn != null && EF.Functions.ILike(e.FirstNameEn, search)));
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(e => e.EmployeeCode)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<EmployeeResponse>(
            items.Select(MapToEmployeeResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<EmployeeResponse> UpdateEmployeeAsync(Guid companyId, Guid employeeId, UpdateEmployeeRequest request)
    {
        var employee = await _db.Set<Employee>()
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        if (request.Position != null) employee.Position = request.Position;
        if (request.Department != null) employee.Department = request.Department;
        if (request.Phone != null) employee.Phone = request.Phone;
        if (request.Email != null) employee.Email = request.Email;
        if (request.BaseSalary.HasValue) employee.BaseSalary = request.BaseSalary.Value;
        if (request.BankName != null) employee.BankName = request.BankName;
        if (request.BankAccountNumber != null) employee.BankAccountNumber = request.BankAccountNumber;
        if (request.SocialSecurityHospital != null) employee.SocialSecurityHospital = request.SocialSecurityHospital;
        if (request.HasProvidentFund.HasValue) employee.HasProvidentFund = request.HasProvidentFund.Value;
        if (request.ProvidentFundEmployeePercent.HasValue) employee.ProvidentFundEmployeePercent = request.ProvidentFundEmployeePercent.Value;
        if (request.ProvidentFundEmployerPercent.HasValue) employee.ProvidentFundEmployerPercent = request.ProvidentFundEmployerPercent.Value;
        if (request.BranchId.HasValue) employee.BranchId = request.BranchId.Value;
        if (request.DimensionId.HasValue) employee.DimensionId = request.DimensionId.Value;

        employee.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapToEmployeeResponse(employee);
    }

    public async Task TerminateEmployeeAsync(Guid companyId, Guid employeeId, DateTime endDate)
    {
        var employee = await _db.Set<Employee>()
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        employee.EndDate = endDate;
        employee.IsActive = false;
        employee.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ===== Payroll Items =====

    public async Task<PayrollItemResponse> CreatePayrollItemAsync(Guid companyId, CreatePayrollItemRequest request)
    {
        var item = new PayrollItem
        {
            CompanyId = companyId,
            Code = request.Code,
            Name = request.Name,
            NameEn = request.NameEn,
            ItemType = request.ItemType,
            CalculationType = request.CalculationType,
            FixedAmount = request.FixedAmount,
            Percentage = request.Percentage,
            IsTaxable = request.IsTaxable,
            AccountId = request.AccountId
        };

        _db.Set<PayrollItem>().Add(item);
        await _db.SaveChangesAsync();

        return MapToPayrollItemResponse(item);
    }

    public async Task<List<PayrollItemResponse>> GetPayrollItemsAsync(Guid companyId)
    {
        var items = await _db.Set<PayrollItem>()
            .Where(i => i.CompanyId == companyId && i.IsActive && !i.IsDeleted)
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Code)
            .ToListAsync();

        return items.Select(MapToPayrollItemResponse).ToList();
    }

    // ===== Payroll Runs =====

    public async Task<PayrollRunResponse> CreatePayrollRunAsync(Guid companyId, CreatePayrollRunRequest request, string createdBy)
    {
        if (request.Month < 1 || request.Month > 12)
            throw new InvalidOperationException("เดือนต้องอยู่ระหว่าง 1 ถึง 12");

        if (request.Year < 2020 || request.Year > DateTime.UtcNow.Year + 1)
            throw new InvalidOperationException("ปีต้องอยู่ระหว่าง 2020 ถึงปีปัจจุบัน+1");

        if (request.PeriodStart >= request.PeriodEnd)
            throw new InvalidOperationException("วันเริ่มต้นงวดต้องน้อยกว่าวันสิ้นสุดงวด");

        var duplicateRun = await _db.Set<PayrollRun>()
            .AnyAsync(r => r.CompanyId == companyId && r.Year == request.Year
                && r.Month == request.Month && r.Status != "Voided" && !r.IsDeleted);
        if (duplicateRun)
            throw new InvalidOperationException($"รอบจ่ายเงินเดือน {request.Year}/{request.Month:D2} มีอยู่แล้ว");

        var count = await _db.Set<PayrollRun>()
            .CountAsync(r => r.CompanyId == companyId && r.Year == request.Year);
        var payrollNumber = $"PR-{request.Year}{request.Month:D2}-{(count + 1):D3}";

        var run = new PayrollRun
        {
            CompanyId = companyId,
            PayrollNumber = payrollNumber,
            Name = request.Name,
            Year = request.Year,
            Month = request.Month,
            PayDate = request.PayDate,
            PeriodStart = request.PeriodStart,
            PeriodEnd = request.PeriodEnd,
            Status = "Draft",
            CreatedBy = createdBy
        };

        _db.Set<PayrollRun>().Add(run);
        await _db.SaveChangesAsync();

        return MapToPayrollRunResponse(run);
    }

    public async Task<PayrollRunResponse> GetPayrollRunAsync(Guid companyId, Guid payrollRunId)
    {
        var run = await _db.Set<PayrollRun>()
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId && !r.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        return MapToPayrollRunResponse(run);
    }

    public async Task<PagedResponse<PayrollRunResponse>> GetPayrollRunsAsync(Guid companyId, PagedRequest request)
    {
        var query = _db.Set<PayrollRun>()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<PayrollRunResponse>(
            items.Select(MapToPayrollRunResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<PayrollRunResponse> CalculatePayrollAsync(Guid companyId, Guid payrollRunId)
    {
        var run = await _db.Set<PayrollRun>()
            .Include(r => r.Details)
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        if (run.Status != "Draft")
            throw new InvalidOperationException("สามารถคำนวณได้เฉพาะรอบที่เป็น Draft เท่านั้น");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Remove existing details
            _db.Set<PayrollDetail>().RemoveRange(run.Details);

            // Get active employees
            var employees = await _db.Set<Employee>()
                .Where(e => e.CompanyId == companyId && e.IsActive && !e.IsDeleted
                    && e.StartDate <= run.PeriodEnd
                    && (e.EndDate == null || e.EndDate >= run.PeriodStart))
                .ToListAsync();

            // Get payroll items for earnings/deductions calculation
            var payrollItems = await _db.Set<PayrollItem>()
                .Where(i => i.CompanyId == companyId && i.IsActive && !i.IsDeleted)
                .ToListAsync();

            var earningItems = payrollItems.Where(i => i.ItemType == "Earning").ToList();
            var deductionItems = payrollItems.Where(i => i.ItemType == "Deduction").ToList();

            decimal totalGross = 0, totalDeductions = 0, totalNet = 0;
            decimal totalWht = 0, totalSsoEmp = 0, totalSsoEr = 0;
            decimal totalPvdEmp = 0, totalPvdEr = 0;

            foreach (var emp in employees)
            {
                // Get cumulative income for this year (prior months)
                var priorDetails = await _db.Set<PayrollDetail>()
                    .Include(d => d.PayrollRun)
                    .Where(d => d.EmployeeId == emp.Id
                        && d.PayrollRun.CompanyId == companyId
                        && d.PayrollRun.Year == run.Year
                        && d.PayrollRun.Month < run.Month
                        && d.PayrollRun.Status != "Voided")
                    .ToListAsync();

                var cumulativeIncome = priorDetails.Sum(d => d.GrossIncome);
                var cumulativeTax = priorDetails.Sum(d => d.WithholdingTax);

                // Calculate earnings from PayrollItems
                var overtimePay = 0m;
                var allowances = 0m;
                var commission = 0m;
                var bonus = 0m;
                var otherIncome = 0m;

                foreach (var item in earningItems)
                {
                    var amount = item.CalculationType == "Fixed"
                        ? (item.FixedAmount ?? 0)
                        : (item.Percentage ?? 0) / 100m * emp.BaseSalary;

                    if (item.Code.StartsWith("OT", StringComparison.OrdinalIgnoreCase))
                        overtimePay += amount;
                    else if (item.Code.Equals("COM", StringComparison.OrdinalIgnoreCase))
                        commission += amount;
                    else if (item.Code.Equals("BONUS", StringComparison.OrdinalIgnoreCase))
                        bonus += amount;
                    else
                        allowances += amount;
                }

                // Calculate leave deductions
                var approvedLeaves = await _db.Set<EmployeeLeave>()
                    .Where(l => l.EmployeeId == emp.Id && l.CompanyId == companyId
                        && l.Status == "Approved"
                        && l.StartDate <= run.PeriodEnd && l.EndDate >= run.PeriodStart)
                    .ToListAsync();

                var leaveDays = approvedLeaves.Sum(l => l.TotalDays);
                var workDaysInMonth = DateTime.DaysInMonth(run.Year, run.Month);

                // Calculate other deductions from PayrollItems
                var otherDeductions = 0m;
                foreach (var item in deductionItems)
                {
                    var amount = item.CalculationType == "Fixed"
                        ? (item.FixedAmount ?? 0)
                        : (item.Percentage ?? 0) / 100m * emp.BaseSalary;
                    otherDeductions += amount;
                }

                // Deduct unpaid leave from base salary
                var unpaidLeaveDays = approvedLeaves
                    .Where(l => l.LeaveType == "UnpaidLeave" || l.LeaveType == "ลาไม่รับค่าจ้าง")
                    .Sum(l => l.TotalDays);
                var leaveDeduction = workDaysInMonth > 0
                    ? Math.Round(emp.BaseSalary * unpaidLeaveDays / workDaysInMonth, 2, MidpointRounding.AwayFromZero)
                    : 0m;

                var grossIncome = emp.BaseSalary - leaveDeduction + overtimePay + allowances + commission + bonus + otherIncome;

                // Social security: base on BaseSalary (not gross), capped at 15,000 THB/month per Thai SSO law
                var ssoEmployee = 0m;
                var ssoEmployer = 0m;
                if (emp.IsSubjectToSocialSecurity)
                {
                    var ssoBase = Math.Min(emp.BaseSalary, SsoMaxBase);
                    ssoEmployee = Math.Min(ssoBase * SsoRate, SsoMaxContribution);
                    ssoEmployer = ssoEmployee;
                }

                // Provident fund calculation
                var pvdEmployee = 0m;
                var pvdEmployer = 0m;
                if (emp.HasProvidentFund)
                {
                    pvdEmployee = emp.BaseSalary * emp.ProvidentFundEmployeePercent / 100m;
                    pvdEmployer = emp.BaseSalary * emp.ProvidentFundEmployerPercent / 100m;
                }

                // Thai withholding tax: TRD-standard annualization = (YTD including this month) * 12 / elapsed months
                var ytdIncome = cumulativeIncome + grossIncome;
                var estimatedAnnualIncome = run.Month > 0 ? ytdIncome * 12 / run.Month : ytdIncome * 12;
                var estimatedAnnualTax = CalculateThaiIncomeTax(estimatedAnnualIncome);
                var remainingMonths = 13 - run.Month;
                var monthlyTax = remainingMonths > 0
                    ? (estimatedAnnualTax - cumulativeTax) / remainingMonths
                    : 0m;
                monthlyTax = Math.Max(0, monthlyTax);

                var totalDeductionsForEmp = ssoEmployee + monthlyTax + pvdEmployee + otherDeductions;
                var netPay = grossIncome - totalDeductionsForEmp;

                var detail = new PayrollDetail
                {
                    CompanyId = companyId,
                    PayrollRunId = payrollRunId,
                    EmployeeId = emp.Id,
                    BaseSalary = emp.BaseSalary,
                    OvertimePay = overtimePay,
                    Allowances = allowances,
                    Commission = commission,
                    Bonus = bonus,
                    OtherIncome = otherIncome,
                    GrossIncome = grossIncome,
                    SocialSecurityEmployee = ssoEmployee,
                    SocialSecurityEmployer = ssoEmployer,
                    WithholdingTax = monthlyTax,
                    ProvidentFundEmployee = pvdEmployee,
                    ProvidentFundEmployer = pvdEmployer,
                    LoanDeduction = 0,
                    OtherDeductions = otherDeductions,
                    TotalDeductions = totalDeductionsForEmp,
                    NetPay = netPay,
                    CumulativeIncomeYTD = cumulativeIncome + grossIncome,
                    CumulativeTaxYTD = cumulativeTax + monthlyTax,
                    EstimatedAnnualIncome = estimatedAnnualIncome,
                    EstimatedAnnualTax = estimatedAnnualTax,
                    WorkDays = workDaysInMonth,
                    LeaveDays = (int)leaveDays
                };

                _db.Set<PayrollDetail>().Add(detail);

                totalGross += grossIncome;
                totalDeductions += totalDeductionsForEmp;
                totalNet += netPay;
                totalWht += monthlyTax;
                totalSsoEmp += ssoEmployee;
                totalSsoEr += ssoEmployer;
                totalPvdEmp += pvdEmployee;
                totalPvdEr += pvdEmployer;
            }

            run.TotalGrossSalary = totalGross;
            run.TotalDeductions = totalDeductions;
            run.TotalNetPay = totalNet;
            run.TotalWithholdingTax = totalWht;
            run.TotalSocialSecurityEmployee = totalSsoEmp;
            run.TotalSocialSecurityEmployer = totalSsoEr;
            run.TotalProvidentFundEmployee = totalPvdEmp;
            run.TotalProvidentFundEmployer = totalPvdEr;
            run.EmployeeCount = employees.Count;
            run.Status = "Calculated";

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            return MapToPayrollRunResponse(run);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<PayrollRunResponse> ApprovePayrollAsync(Guid companyId, Guid payrollRunId, string approvedBy)
    {
        var run = await _db.Set<PayrollRun>()
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        if (run.Status != "Calculated")
            throw new InvalidOperationException("สามารถอนุมัติได้เฉพาะรอบที่คำนวณแล้วเท่านั้น");

        run.Status = "Approved";
        run.ApprovedBy = approvedBy;
        run.ApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapToPayrollRunResponse(run);
    }

    public async Task<PayrollRunResponse> ProcessPaymentAsync(Guid companyId, Guid payrollRunId, string processedBy)
    {
        var run = await _db.Set<PayrollRun>()
            .Include(r => r.Details)
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        if (run.Status != "Approved")
            throw new InvalidOperationException("สามารถจ่ายได้เฉพาะรอบที่อนุมัติแล้วเท่านั้น");

        // Prevent duplicate payments for same year/month
        var alreadyPaid = await _db.Set<PayrollRun>()
            .AnyAsync(r => r.CompanyId == companyId && r.Year == run.Year
                && r.Month == run.Month && r.Status == "Paid" && r.Id != payrollRunId && !r.IsDeleted);
        if (alreadyPaid)
            throw new InvalidOperationException($"รอบจ่ายเงินเดือน {run.Year}/{run.Month:D2} ถูกจ่ายไปแล้ว");

        await using var payTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            run.Status = "Paid";
            run.UpdatedBy = processedBy;
            run.UpdatedAt = DateTime.UtcNow;

            if (_accountingService != null && run.TotalGrossSalary > 0)
            {
                try
                {
                var lines = new List<Models.DTOs.Accounting.JournalLineRequest>();

                // Dr: เงินเดือนและค่าจ้าง (541 - ค่าใช้จ่ายบุคลากร)
                var salaryAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode == "54111" && a.Level >= 4)
                    ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode.StartsWith("541") && a.Level >= 4);
                if (salaryAccount != null)
                    lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                        salaryAccount.Id, run.TotalGrossSalary, 0, $"เงินเดือน {run.Month}/{run.Year}"));

                // Dr: ประกันสังคมส่วนนายจ้าง (54120)
                if (run.TotalSocialSecurityEmployer > 0)
                {
                    var ssoExpAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode == "54120" && a.Level >= 4)
                        ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode.StartsWith("541") && a.Level >= 4
                        && a.AccountName.Contains("ประกันสังคม"));
                    if (ssoExpAccount != null)
                        lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                            ssoExpAccount.Id, run.TotalSocialSecurityEmployer, 0, "ประกันสังคมส่วนนายจ้าง"));
                }

                // Cr: ภาษีเงินได้หัก ณ ที่จ่ายค้างจ่าย (21914 - ภ.ง.ด. 1)
                if (run.TotalWithholdingTax > 0)
                {
                    var whtAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode == "21914" && a.Level >= 4)
                        ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode.StartsWith("219") && a.Level >= 4
                        && a.AccountName.Contains("หัก ณ ที่จ่าย"));
                    if (whtAccount != null)
                        lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                            whtAccount.Id, 0, run.TotalWithholdingTax, "ภาษีหัก ณ ที่จ่าย (เงินเดือน)"));
                }

                // Cr: ประกันสังคมค้างจ่าย (21815) — ทั้งส่วนลูกจ้างและนายจ้าง
                var totalSso = run.TotalSocialSecurityEmployee + run.TotalSocialSecurityEmployer;
                if (totalSso > 0)
                {
                    var ssoPayableAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode == "21815" && a.Level >= 4)
                        ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode.StartsWith("218") && a.Level >= 4
                        && a.AccountName.Contains("ประกันสังคม"));
                    if (ssoPayableAccount != null)
                        lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                            ssoPayableAccount.Id, 0, totalSso, "ประกันสังคมค้างจ่าย"));
                }

                // Cr: กองทุนสำรองเลี้ยงชีพค้างจ่าย (21818) — ส่วนลูกจ้าง+นายจ้าง
                var totalPvd = run.TotalProvidentFundEmployee + run.TotalProvidentFundEmployer;
                if (totalPvd > 0)
                {
                    var pvdPayableAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode == "21818" && a.Level >= 4)
                        ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                            a.CompanyId == companyId && a.AccountCode.StartsWith("218") && a.Level >= 4
                            && a.AccountName.Contains("สำรองเลี้ยงชีพ"));
                    if (pvdPayableAccount != null)
                        lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                            pvdPayableAccount.Id, 0, totalPvd, "กองทุนสำรองเลี้ยงชีพค้างจ่าย"));
                }

                // Dr: กองทุนสำรองเลี้ยงชีพส่วนนายจ้าง (54124)
                if (run.TotalProvidentFundEmployer > 0)
                {
                    var pvdExpAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode == "54124" && a.Level >= 4)
                        ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                            a.CompanyId == companyId && a.AccountCode.StartsWith("541") && a.Level >= 4
                            && a.AccountName.Contains("สำรองเลี้ยงชีพ"));
                    if (pvdExpAccount != null)
                        lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                            pvdExpAccount.Id, run.TotalProvidentFundEmployer, 0, "กองทุนสำรองเลี้ยงชีพส่วนนายจ้าง"));
                }

                // ── Advance recovery: clear outstanding salary advances ──
                // For each employee, recover MonthlyDeduction (or the full
                // outstanding balance when MonthlyDeduction is 0), capped at
                // that employee's net pay. Credits Advance Receivable and
                // reduces the cash actually disbursed.
                decimal totalAdvanceRecovered = 0m;
                var advanceRepayments = new List<(SalaryAdvance Advance, decimal Amount)>();
                if (_salaryAdvanceService != null)
                {
                    foreach (var detail in run.Details)
                    {
                        var employeeRecoverable = detail.NetPay;
                        var outstanding = await _salaryAdvanceService
                            .GetOutstandingForEmployeeAsync(companyId, detail.EmployeeId);
                        foreach (var adv in outstanding)
                        {
                            if (employeeRecoverable <= 0) break;
                            var recover = adv.MonthlyDeduction > 0
                                ? Math.Min(adv.MonthlyDeduction, adv.OutstandingAmount)
                                : adv.OutstandingAmount;
                            recover = Math.Min(recover, employeeRecoverable);
                            if (recover <= 0) continue;
                            advanceRepayments.Add((adv, recover));
                            totalAdvanceRecovered += recover;
                            employeeRecoverable -= recover;
                        }
                    }
                }
                if (totalAdvanceRecovered > 0)
                {
                    var advanceAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                        && (a.AccountName.Contains("ทดรอง") || a.AccountName.Contains("เงินยืมพนักงาน")))
                        ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                        && a.AccountType == AccountType.Asset && a.AccountCode.StartsWith("115"));
                    if (advanceAccount == null)
                        throw new InvalidOperationException(
                            "มีเงินทดรองค้างชำระแต่ไม่พบบัญชี 'เงินทดรองจ่าย' ในผังบัญชี — กรุณาสร้างบัญชีก่อน");
                    lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                        advanceAccount.Id, 0, totalAdvanceRecovered, "หักคืนเงินทดรองจ่ายพนักงาน"));
                }

                // Cr: เงินสด/ธนาคาร (111) — เงินเดือนสุทธิ หักเงินทดรองที่เรียกคืน
                var cashPaid = run.TotalNetPay - totalAdvanceRecovered;
                var cashAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode == "11122" && a.Level >= 4)
                    ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.Level >= 4);
                if (cashAccount != null)
                    lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                        cashAccount.Id, 0, cashPaid, $"จ่ายเงินเดือน {run.Month}/{run.Year}"));

                if (lines.Count >= 2)
                {
                    var totalDebit = lines.Sum(l => l.DebitAmount);
                    var totalCredit = lines.Sum(l => l.CreditAmount);
                    if (totalDebit != totalCredit)
                        throw new InvalidOperationException(
                            $"Payroll journal unbalanced: Dr={totalDebit:N2} Cr={totalCredit:N2}");

                    var entry = await _accountingService.CreateJournalEntryAsync(companyId,
                        new Models.DTOs.Accounting.CreateJournalEntryRequest(
                            run.PayDate,
                            $"เงินเดือนประจำเดือน {run.Month}/{run.Year} ({run.EmployeeCount} คน)",
                            $"HR-PR-{run.Year}-{run.Month:D2}",
                            lines, JournalType.General), processedBy);
                    await _accountingService.PostJournalEntryAsync(companyId, entry.Id);
                    var je = await _db.JournalEntries.FindAsync(entry.Id);
                    if (je != null) { je.IsAutoGenerated = true; }
                    // Link the run to its posted journal entry, then recover
                    // outstanding advances (entities are tracked on the same
                    // context — persisted by the SaveChanges below, inside
                    // the surrounding payTransaction).
                    run.JournalEntryId = entry.Id;
                    foreach (var (adv, amount) in advanceRepayments)
                        _salaryAdvanceService!.ApplyRepayment(adv, amount);
                    await _db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Payroll journal creation failed: {ex.Message}", ex);
            }
            }

            await _db.SaveChangesAsync();
            await payTransaction.CommitAsync();
        }
        catch
        {
            await payTransaction.RollbackAsync();
            throw;
        }

        return MapToPayrollRunResponse(run);
    }

    public async Task VoidPayrollAsync(Guid companyId, Guid payrollRunId)
    {
        var run = await _db.Set<PayrollRun>()
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        if (run.Status == "Paid")
            throw new InvalidOperationException("ไม่สามารถยกเลิกรอบที่จ่ายแล้วได้");

        run.Status = "Voided";
        run.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<PayrollDetailResponse> GetPayrollDetailAsync(Guid companyId, Guid payrollRunId, Guid employeeId)
    {
        var detail = await _db.Set<PayrollDetail>()
            .Include(d => d.Employee)
            .FirstOrDefaultAsync(d => d.PayrollRunId == payrollRunId
                && d.EmployeeId == employeeId
                && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายละเอียดเงินเดือน");

        return new PayrollDetailResponse(
            detail.EmployeeId,
            detail.Employee.EmployeeCode,
            $"{detail.Employee.FirstNameTh} {detail.Employee.LastNameTh}",
            detail.BaseSalary, detail.OvertimePay, detail.Allowances,
            detail.Commission, detail.Bonus, detail.GrossIncome,
            detail.SocialSecurityEmployee, detail.WithholdingTax,
            detail.ProvidentFundEmployee, detail.OtherDeductions,
            detail.TotalDeductions, detail.NetPay);
    }

    public async Task<PayslipResponse> GeneratePayslipAsync(Guid companyId, Guid payrollRunId, Guid employeeId)
    {
        var detail = await _db.Set<PayrollDetail>()
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            .FirstOrDefaultAsync(d => d.PayrollRunId == payrollRunId
                && d.EmployeeId == employeeId
                && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายละเอียดเงินเดือน");

        var emp = detail.Employee;
        var run = detail.PayrollRun;
        var employeeName = $"{emp.TitleTh}{emp.FirstNameTh} {emp.LastNameTh}";
        var fileName = $"Payslip_{emp.EmployeeCode}_{run.Year}{run.Month:D2}.pdf";

        // Generate HTML payslip content
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine("<style>body{font-family:'THSarabunNew',sans-serif;font-size:14px;margin:20px;} table{width:100%;border-collapse:collapse;margin:10px 0;} td,th{border:1px solid #ccc;padding:6px 8px;} th{background:#4472C4;color:#fff;} .right{text-align:right;} .title{text-align:center;font-size:20px;font-weight:bold;margin-bottom:10px;} .section{font-weight:bold;background:#f0f0f0;} .total{font-weight:bold;background:#e8f0fe;}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<div class='title'>ใบสลิปเงินเดือน / Payslip</div>");
        sb.AppendLine($"<div style='text-align:center;margin-bottom:15px;'>งวดเดือน {run.Month:D2}/{run.Year} | วันจ่าย {run.PayDate:dd/MM/yyyy}</div>");

        // Employee info
        sb.AppendLine("<table><tr><td><strong>รหัส:</strong> " + emp.EmployeeCode + "</td>");
        sb.AppendLine($"<td><strong>ชื่อ:</strong> {employeeName}</td></tr>");
        sb.AppendLine($"<tr><td><strong>แผนก:</strong> {emp.Department ?? "-"}</td>");
        sb.AppendLine($"<td><strong>ตำแหน่ง:</strong> {emp.Position ?? "-"}</td></tr></table>");

        // Earnings & Deductions side by side
        sb.AppendLine("<table><thead><tr><th colspan='2'>รายได้ (Earnings)</th><th colspan='2'>รายการหัก (Deductions)</th></tr></thead><tbody>");
        sb.AppendLine($"<tr><td>เงินเดือน</td><td class='right'>{detail.BaseSalary:N2}</td><td>ประกันสังคม</td><td class='right'>{detail.SocialSecurityEmployee:N2}</td></tr>");
        sb.AppendLine($"<tr><td>ค่าล่วงเวลา</td><td class='right'>{detail.OvertimePay:N2}</td><td>ภาษีหัก ณ ที่จ่าย</td><td class='right'>{detail.WithholdingTax:N2}</td></tr>");
        sb.AppendLine($"<tr><td>เบี้ยเลี้ยง</td><td class='right'>{detail.Allowances:N2}</td><td>กองทุนสำรองเลี้ยงชีพ</td><td class='right'>{detail.ProvidentFundEmployee:N2}</td></tr>");
        sb.AppendLine($"<tr><td>คอมมิชชั่น</td><td class='right'>{detail.Commission:N2}</td><td>หักเงินกู้</td><td class='right'>{detail.LoanDeduction:N2}</td></tr>");
        sb.AppendLine($"<tr><td>โบนัส</td><td class='right'>{detail.Bonus:N2}</td><td>หักอื่นๆ</td><td class='right'>{detail.OtherDeductions:N2}</td></tr>");
        sb.AppendLine($"<tr class='total'><td>รวมรายได้</td><td class='right'>{detail.GrossIncome:N2}</td><td>รวมรายการหัก</td><td class='right'>{detail.TotalDeductions:N2}</td></tr>");
        sb.AppendLine("</tbody></table>");

        // Net pay
        sb.AppendLine($"<table><tr class='total'><td style='text-align:center;font-size:18px;'>เงินได้สุทธิ (Net Pay): {detail.NetPay:N2} บาท</td></tr></table>");

        // YTD info
        sb.AppendLine($"<table><tr><td>รายได้สะสม (YTD)</td><td class='right'>{detail.CumulativeIncomeYTD:N2}</td>");
        sb.AppendLine($"<td>ภาษีสะสม (YTD)</td><td class='right'>{detail.CumulativeTaxYTD:N2}</td></tr></table>");

        sb.AppendLine("</body></html>");

        var htmlContent = sb.ToString();
        var pdfContent = _pdfService != null
            ? _pdfService.ConvertHtmlToPdfBytes(htmlContent)
            : PdfGenerationService.ConvertHtmlToPdf(htmlContent, null);

        return new PayslipResponse(
            employeeId, $"{emp.FirstNameTh} {emp.LastNameTh}",
            run.Year, run.Month, pdfContent, fileName);
    }

    // ===== Leave =====

    public async Task<LeaveResponse> CreateLeaveAsync(Guid companyId, CreateLeaveRequest request)
    {
        if (request.StartDate > request.EndDate)
            throw new InvalidOperationException("วันเริ่มต้นลาต้องไม่เกินวันสิ้นสุด");

        if (request.TotalDays <= 0)
            throw new InvalidOperationException("จำนวนวันลาต้องมากกว่า 0");

        var employee = await _db.Set<Employee>()
            .FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        // Check for overlapping approved leaves
        var overlapping = await _db.Set<EmployeeLeave>()
            .AnyAsync(l => l.EmployeeId == request.EmployeeId && l.CompanyId == companyId
                && l.Status == "Approved" && !l.IsDeleted
                && l.StartDate <= request.EndDate && l.EndDate >= request.StartDate);
        if (overlapping)
            throw new InvalidOperationException("มีรายการลาที่ทับซ้อนกันในช่วงเวลาเดียวกัน");

        // Quota check — count Approved + Pending leaves of the same type
        // for the start year. Resolved quotas merge per-company overrides
        // (CompanySettings.LeaveQuotasJson) with Thai labor-law defaults.
        var quotas = await ResolveLeaveQuotasAsync(companyId);
        if (quotas.TryGetValue(request.LeaveType, out var allocated) && allocated > 0)
        {
            var leaveYear = request.StartDate.Year;
            var usedThisYear = await _db.Set<EmployeeLeave>()
                .Where(l => l.CompanyId == companyId
                    && l.EmployeeId == request.EmployeeId
                    && l.LeaveType == request.LeaveType
                    && l.StartDate.Year == leaveYear
                    && (l.Status == "Approved" || l.Status == "Pending")
                    && !l.IsDeleted)
                .SumAsync(l => (decimal?)l.TotalDays) ?? 0m;
            if (usedThisYear + request.TotalDays > allocated)
                throw new InvalidOperationException(
                    $"จำนวนวันลาเกินโควต้า — {request.LeaveType} ปี {leaveYear} สิทธิ์ {allocated:0.#} วัน ใช้ไปแล้ว {usedThisYear:0.#} วัน ขอเพิ่ม {request.TotalDays:0.#} วัน");
        }

        var leave = new EmployeeLeave
        {
            CompanyId = companyId,
            EmployeeId = request.EmployeeId,
            LeaveType = request.LeaveType,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            TotalDays = request.TotalDays,
            Reason = request.Reason,
            Status = "Pending"
        };

        _db.Set<EmployeeLeave>().Add(leave);
        await _db.SaveChangesAsync();

        return MapToLeaveResponse(leave, employee);
    }

    public async Task<LeaveResponse> GetLeaveAsync(Guid companyId, Guid leaveId)
    {
        var leave = await _db.Set<EmployeeLeave>()
            .Include(l => l.Employee)
            .FirstOrDefaultAsync(l => l.Id == leaveId && l.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการลา");
        return MapToLeaveResponse(leave, leave.Employee);
    }

    public async Task<LeaveResponse> ApproveLeaveAsync(Guid companyId, Guid leaveId, string approvedBy)
    {
        var leave = await _db.Set<EmployeeLeave>()
            .Include(l => l.Employee)
            .FirstOrDefaultAsync(l => l.Id == leaveId && l.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการลา");

        if (leave.Status != "Pending")
            throw new InvalidOperationException("สามารถอนุมัติได้เฉพาะรายการที่รอดำเนินการ");

        leave.Status = "Approved";
        leave.ApprovedBy = approvedBy;
        await _db.SaveChangesAsync();

        return MapToLeaveResponse(leave, leave.Employee);
    }

    public async Task<LeaveResponse> RejectLeaveAsync(Guid companyId, Guid leaveId, string rejectedBy, RejectLeaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new InvalidOperationException("กรุณาระบุเหตุผลในการปฏิเสธ");

        var leave = await _db.Set<EmployeeLeave>()
            .Include(l => l.Employee)
            .FirstOrDefaultAsync(l => l.Id == leaveId && l.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการลา");

        if (leave.Status != "Pending")
            throw new InvalidOperationException("สามารถปฏิเสธได้เฉพาะรายการที่รอดำเนินการ");

        leave.Status = "Rejected";
        leave.ApprovedBy = rejectedBy;
        leave.RejectionReason = request.Reason;
        await _db.SaveChangesAsync();

        return MapToLeaveResponse(leave, leave.Employee);
    }

    public async Task<LeaveResponse> CancelLeaveAsync(Guid companyId, Guid leaveId)
    {
        var leave = await _db.Set<EmployeeLeave>()
            .Include(l => l.Employee)
            .FirstOrDefaultAsync(l => l.Id == leaveId && l.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการลา");

        // Only Pending or Approved leaves can be cancelled. Rejected/Cancelled
        // leaves are terminal — and a cancelled approved leave that was already
        // counted by a posted payroll run should not be silently undone here.
        if (leave.Status != "Pending" && leave.Status != "Approved")
            throw new InvalidOperationException("ยกเลิกได้เฉพาะรายการที่ยังรอดำเนินการหรืออนุมัติแล้ว");

        leave.Status = "Cancelled";
        await _db.SaveChangesAsync();

        return MapToLeaveResponse(leave, leave.Employee);
    }

    // Thai labor-law defaults — used when CompanySettings.LeaveQuotasJson
    // is not set. Annual: 6d (พ.ร.บ.คุ้มครองแรงงาน), Sick: 30d/yr (paid
    // ceiling), Personal: 3d, Maternity: 98d.
    private static readonly Dictionary<string, decimal> DefaultLeaveQuotas = new()
    {
        ["Annual"] = 6m,
        ["Sick"] = 30m,
        ["Personal"] = 3m,
        ["Maternity"] = 98m,
        ["Other"] = 0m,
    };

    private async Task<Dictionary<string, decimal>> ResolveLeaveQuotasAsync(Guid companyId)
    {
        var settings = await _db.Set<CompanySettings>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (string.IsNullOrWhiteSpace(settings?.LeaveQuotasJson))
            return new Dictionary<string, decimal>(DefaultLeaveQuotas);
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(settings.LeaveQuotasJson);
            if (parsed == null) return new Dictionary<string, decimal>(DefaultLeaveQuotas);
            // Merge: tenant overrides win, defaults fill the gaps.
            var merged = new Dictionary<string, decimal>(DefaultLeaveQuotas);
            foreach (var kvp in parsed) merged[kvp.Key] = kvp.Value;
            return merged;
        }
        catch { return new Dictionary<string, decimal>(DefaultLeaveQuotas); }
    }

    public async Task<LeaveBalanceResponse> GetLeaveBalanceAsync(Guid companyId, Guid employeeId, int year)
    {
        var employee = await _db.Set<Employee>()
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        var quotas = await ResolveLeaveQuotasAsync(companyId);

        // "Used" counts Approved + Pending leaves (Pending reserves the
        // quota so two overlapping requests can't both eat the same days).
        // Rejected / Cancelled leaves don't count.
        var leaves = await _db.Set<EmployeeLeave>()
            .Where(l => l.CompanyId == companyId
                && l.EmployeeId == employeeId
                && l.StartDate.Year == year
                && (l.Status == "Approved" || l.Status == "Pending")
                && !l.IsDeleted)
            .ToListAsync();

        var balances = quotas.Select(q =>
        {
            var used = leaves.Where(l => l.LeaveType == q.Key).Sum(l => l.TotalDays);
            return new LeaveBalanceItem(q.Key, q.Value, used, Math.Max(0m, q.Value - used));
        }).OrderBy(b => b.LeaveType).ToList();

        return new LeaveBalanceResponse(
            employeeId,
            $"{employee.FirstNameTh} {employee.LastNameTh}".Trim(),
            year,
            balances);
    }

    public async Task<List<LeaveResponse>> GetLeavesAsync(Guid companyId, Guid? employeeId, int? year)
    {
        var query = _db.Set<EmployeeLeave>()
            .Include(l => l.Employee)
            .Where(l => l.CompanyId == companyId && !l.IsDeleted);

        if (employeeId.HasValue)
            query = query.Where(l => l.EmployeeId == employeeId.Value);

        if (year.HasValue)
            query = query.Where(l => l.StartDate.Year == year.Value);

        var leaves = await query
            .OrderByDescending(l => l.StartDate)
            .ToListAsync();

        return leaves.Select(l => MapToLeaveResponse(l, l.Employee)).ToList();
    }

    // ===== Tax Reports =====

    public async Task<object> GeneratePnd1Async(Guid companyId, int year, int month)
    {
        var details = await _db.Set<PayrollDetail>()
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            .Where(d => d.CompanyId == companyId
                && d.PayrollRun.Year == year
                && d.PayrollRun.Month == month
                && d.PayrollRun.Status != "Voided")
            .ToListAsync();

        var lines = details.Select(d => new
        {
            EmployeeCode = d.Employee.EmployeeCode,
            CitizenId = d.Employee.CitizenId,
            FullName = $"{d.Employee.TitleTh}{d.Employee.FirstNameTh} {d.Employee.LastNameTh}",
            IncomeType = "เงินเดือน ค่าจ้าง (ม.40(1))",
            TaxableIncome = d.GrossIncome,
            TaxWithheld = d.WithholdingTax
        }).ToList();

        return new
        {
            FormCode = "ภ.ง.ด.1",
            Year = year,
            Month = month,
            TotalEmployees = lines.Count,
            TotalTaxableIncome = lines.Sum(l => l.TaxableIncome),
            TotalTaxWithheld = lines.Sum(l => l.TaxWithheld),
            Lines = lines
        };
    }

    public async Task<object> GenerateSsoReportAsync(Guid companyId, int year, int month)
    {
        var details = await _db.Set<PayrollDetail>()
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            .Where(d => d.CompanyId == companyId
                && d.PayrollRun.Year == year
                && d.PayrollRun.Month == month
                && d.PayrollRun.Status != "Voided"
                && d.Employee.IsSubjectToSocialSecurity)
            .ToListAsync();

        var lines = details.Select(d => new
        {
            EmployeeCode = d.Employee.EmployeeCode,
            SocialSecurityNumber = d.Employee.SocialSecurityNumber,
            FullName = $"{d.Employee.TitleTh}{d.Employee.FirstNameTh} {d.Employee.LastNameTh}",
            SalaryBase = Math.Min(d.BaseSalary, SsoMaxBase),
            EmployeeContribution = d.SocialSecurityEmployee,
            EmployerContribution = d.SocialSecurityEmployer
        }).ToList();

        return new
        {
            FormCode = "สปส.1-10",
            Year = year,
            Month = month,
            TotalEmployees = lines.Count,
            TotalEmployeeContribution = lines.Sum(l => l.EmployeeContribution),
            TotalEmployerContribution = lines.Sum(l => l.EmployerContribution),
            TotalContribution = lines.Sum(l => l.EmployeeContribution + l.EmployerContribution),
            Lines = lines
        };
    }

    // ===== PND3 Report (ภ.ง.ด.3) =====

    public async Task<object> GeneratePnd3Async(Guid companyId, int year, int month)
    {
        var startDate = new DateTime(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);

        var docs = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.WithholdingTaxAmount > 0)
            .ToListAsync();

        var lines = docs.SelectMany(d => d.Lines
            .Where(l => l.WithholdingTaxAmount > 0)
            .Select(l => new
            {
                TaxPayerId = d.Contact?.TaxId,
                TaxPayerName = d.Contact?.Name ?? "",
                IncomeTypeCode = l.IncomeTypeCode ?? "40(8)",
                IncomeAmount = l.Amount,
                TaxRate = l.WithholdingTaxRate,
                TaxWithheld = l.WithholdingTaxAmount,
                DocumentNumber = d.DocumentNumber,
                PaymentDate = d.DocumentDate
            }))
            .ToList();

        // Group by vendor for summary
        var vendorSummary = lines
            .GroupBy(l => new { l.TaxPayerId, l.TaxPayerName })
            .Select(g => new
            {
                g.Key.TaxPayerId,
                g.Key.TaxPayerName,
                TotalIncome = g.Sum(l => l.IncomeAmount),
                TotalTaxWithheld = g.Sum(l => l.TaxWithheld),
                TransactionCount = g.Count()
            })
            .ToList();

        return new
        {
            FormCode = "ภ.ง.ด.3",
            Year = year,
            Month = month,
            TotalVendors = vendorSummary.Count,
            TotalIncome = lines.Sum(l => l.IncomeAmount),
            TotalTaxWithheld = lines.Sum(l => l.TaxWithheld),
            VendorSummary = vendorSummary,
            Lines = lines
        };
    }

    // ===== Thai Income Tax Calculation =====

    /// <summary>
    /// Calculates Thai personal income tax using progressive brackets.
    /// Brackets: 0-150K=0%, 150K-300K=5%, 300K-500K=10%, 500K-750K=15%,
    /// 750K-1M=20%, 1M-2M=25%, 2M-5M=30%, 5M+=35%
    /// </summary>
    private static decimal CalculateThaiIncomeTax(decimal annualTaxableIncome)
    {
        if (annualTaxableIncome <= 0) return 0;

        decimal totalTax = 0;
        decimal previousBound = 0;

        foreach (var (upperBound, rate) in ThaiTaxBrackets)
        {
            if (annualTaxableIncome <= previousBound)
                break;

            var taxableInBracket = Math.Min(annualTaxableIncome, upperBound) - previousBound;
            if (taxableInBracket > 0)
            {
                totalTax += taxableInBracket * rate;
            }

            previousBound = upperBound;
        }

        return Math.Round(totalTax, 2, MidpointRounding.AwayFromZero);
    }

    // ===== Mapping Helpers =====

    private static EmployeeResponse MapToEmployeeResponse(Employee e) =>
        new(e.Id, e.EmployeeCode, e.TitleTh, e.FirstNameTh, e.LastNameTh,
            e.FirstNameEn, e.LastNameEn, e.CitizenId, e.Department, e.Position,
            e.EmploymentType, e.StartDate, e.EndDate, e.BaseSalary,
            e.SalaryType, e.IsActive, e.CreatedAt);

    private static PayrollItemResponse MapToPayrollItemResponse(PayrollItem i) =>
        new(i.Id, i.Code, i.Name, i.ItemType, i.CalculationType,
            i.FixedAmount, i.Percentage, i.IsTaxable, i.IsActive);

    private static PayrollRunResponse MapToPayrollRunResponse(PayrollRun r) =>
        new(r.Id, r.PayrollNumber, r.Name, r.Year, r.Month, r.PayDate,
            r.Status, r.TotalGrossSalary, r.TotalDeductions, r.TotalNetPay,
            r.TotalWithholdingTax, r.TotalSocialSecurityEmployee,
            r.TotalSocialSecurityEmployer, r.EmployeeCount, r.CreatedAt);

    private static LeaveResponse MapToLeaveResponse(EmployeeLeave l, Employee e) =>
        new(l.Id, l.EmployeeId, $"{e.FirstNameTh} {e.LastNameTh}",
            l.LeaveType, l.StartDate, l.EndDate, l.TotalDays, l.Status, l.Reason,
            l.ApprovedBy, l.RejectionReason);
}

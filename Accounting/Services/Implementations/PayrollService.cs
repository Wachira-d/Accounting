using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class PayrollService : IPayrollService
{
    private readonly AccountingDbContext _db;

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

    public PayrollService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Employees =====

    public async Task<EmployeeResponse> CreateEmployeeAsync(Guid companyId, CreateEmployeeRequest request)
    {
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
            query = query.Where(e => e.EmployeeCode.Contains(request.Search)
                || e.FirstNameTh.Contains(request.Search)
                || e.LastNameTh.Contains(request.Search)
                || (e.FirstNameEn != null && e.FirstNameEn.Contains(request.Search)));

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

        // Remove existing details
        _db.Set<PayrollDetail>().RemoveRange(run.Details);

        // Get active employees
        var employees = await _db.Set<Employee>()
            .Where(e => e.CompanyId == companyId && e.IsActive && !e.IsDeleted
                && e.StartDate <= run.PeriodEnd
                && (e.EndDate == null || e.EndDate >= run.PeriodStart))
            .ToListAsync();

        decimal totalGross = 0, totalDeductions = 0, totalNet = 0;
        decimal totalWht = 0, totalSsoEmp = 0, totalSsoEr = 0;

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

            // Calculate gross income
            var grossIncome = emp.BaseSalary;

            // Social security calculation
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

            // Thai withholding tax calculation (annualized method)
            var estimatedAnnualIncome = cumulativeIncome + grossIncome * (13 - run.Month);
            var estimatedAnnualTax = CalculateThaiIncomeTax(estimatedAnnualIncome);
            var remainingMonths = 13 - run.Month;
            var monthlyTax = remainingMonths > 0
                ? (estimatedAnnualTax - cumulativeTax) / remainingMonths
                : 0m;
            monthlyTax = Math.Max(0, monthlyTax);

            var totalDeductionsForEmp = ssoEmployee + monthlyTax + pvdEmployee;
            var netPay = grossIncome - totalDeductionsForEmp;

            var detail = new PayrollDetail
            {
                CompanyId = companyId,
                PayrollRunId = payrollRunId,
                EmployeeId = emp.Id,
                BaseSalary = emp.BaseSalary,
                OvertimePay = 0,
                Allowances = 0,
                Commission = 0,
                Bonus = 0,
                OtherIncome = 0,
                GrossIncome = grossIncome,
                SocialSecurityEmployee = ssoEmployee,
                SocialSecurityEmployer = ssoEmployer,
                WithholdingTax = monthlyTax,
                ProvidentFundEmployee = pvdEmployee,
                ProvidentFundEmployer = pvdEmployer,
                LoanDeduction = 0,
                OtherDeductions = 0,
                TotalDeductions = totalDeductionsForEmp,
                NetPay = netPay,
                CumulativeIncomeYTD = cumulativeIncome + grossIncome,
                CumulativeTaxYTD = cumulativeTax + monthlyTax,
                EstimatedAnnualIncome = estimatedAnnualIncome,
                EstimatedAnnualTax = estimatedAnnualTax,
                WorkDays = DateTime.DaysInMonth(run.Year, run.Month)
            };

            _db.Set<PayrollDetail>().Add(detail);

            totalGross += grossIncome;
            totalDeductions += totalDeductionsForEmp;
            totalNet += netPay;
            totalWht += monthlyTax;
            totalSsoEmp += ssoEmployee;
            totalSsoEr += ssoEmployer;
        }

        run.TotalGrossSalary = totalGross;
        run.TotalDeductions = totalDeductions;
        run.TotalNetPay = totalNet;
        run.TotalWithholdingTax = totalWht;
        run.TotalSocialSecurityEmployee = totalSsoEmp;
        run.TotalSocialSecurityEmployer = totalSsoEr;
        run.EmployeeCount = employees.Count;
        run.Status = "Calculated";

        await _db.SaveChangesAsync();
        return MapToPayrollRunResponse(run);
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
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        if (run.Status != "Approved")
            throw new InvalidOperationException("สามารถจ่ายได้เฉพาะรอบที่อนุมัติแล้วเท่านั้น");

        run.Status = "Paid";
        run.UpdatedBy = processedBy;
        run.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

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

        var employeeName = $"{detail.Employee.FirstNameTh} {detail.Employee.LastNameTh}";
        var fileName = $"Payslip_{detail.Employee.EmployeeCode}_{detail.PayrollRun.Year}{detail.PayrollRun.Month:D2}.pdf";

        // Generate a placeholder PDF (actual PDF generation would use a PDF library)
        var pdfContent = System.Text.Encoding.UTF8.GetBytes(
            $"PAYSLIP - {employeeName} - {detail.PayrollRun.Year}/{detail.PayrollRun.Month:D2}");

        return new PayslipResponse(
            employeeId, employeeName,
            detail.PayrollRun.Year, detail.PayrollRun.Month,
            pdfContent, fileName);
    }

    // ===== Leave =====

    public async Task<LeaveResponse> CreateLeaveAsync(Guid companyId, CreateLeaveRequest request)
    {
        var employee = await _db.Set<Employee>()
            .FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

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

        return Math.Round(totalTax, 2);
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
            l.LeaveType, l.StartDate, l.EndDate, l.TotalDays, l.Status, l.Reason);
}

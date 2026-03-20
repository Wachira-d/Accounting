using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IPayrollService
{
    // Employees
    Task<EmployeeResponse> CreateEmployeeAsync(Guid companyId, CreateEmployeeRequest request);
    Task<EmployeeResponse> GetEmployeeAsync(Guid companyId, Guid employeeId);
    Task<PagedResponse<EmployeeResponse>> GetEmployeesAsync(Guid companyId, PagedRequest request);
    Task<EmployeeResponse> UpdateEmployeeAsync(Guid companyId, Guid employeeId, UpdateEmployeeRequest request);
    Task TerminateEmployeeAsync(Guid companyId, Guid employeeId, DateTime endDate);

    // Payroll Items (earnings/deductions types)
    Task<PayrollItemResponse> CreatePayrollItemAsync(Guid companyId, CreatePayrollItemRequest request);
    Task<List<PayrollItemResponse>> GetPayrollItemsAsync(Guid companyId);

    // Payroll Runs
    Task<PayrollRunResponse> CreatePayrollRunAsync(Guid companyId, CreatePayrollRunRequest request, string createdBy);
    Task<PayrollRunResponse> GetPayrollRunAsync(Guid companyId, Guid payrollRunId);
    Task<PagedResponse<PayrollRunResponse>> GetPayrollRunsAsync(Guid companyId, PagedRequest request);
    Task<PayrollRunResponse> CalculatePayrollAsync(Guid companyId, Guid payrollRunId);
    Task<PayrollRunResponse> ApprovePayrollAsync(Guid companyId, Guid payrollRunId, string approvedBy);
    Task<PayrollRunResponse> ProcessPaymentAsync(Guid companyId, Guid payrollRunId, string processedBy);
    Task VoidPayrollAsync(Guid companyId, Guid payrollRunId);
    Task<PayrollDetailResponse> GetPayrollDetailAsync(Guid companyId, Guid payrollRunId, Guid employeeId);
    Task<PayslipResponse> GeneratePayslipAsync(Guid companyId, Guid payrollRunId, Guid employeeId);

    // Leave
    Task<LeaveResponse> CreateLeaveAsync(Guid companyId, CreateLeaveRequest request);
    Task<LeaveResponse> ApproveLeaveAsync(Guid companyId, Guid leaveId, string approvedBy);
    Task<List<LeaveResponse>> GetLeavesAsync(Guid companyId, Guid? employeeId, int? year);

    // Tax: ภ.ง.ด.1 generation
    Task<object> GeneratePnd1Async(Guid companyId, int year, int month);
    Task<object> GenerateSsoReportAsync(Guid companyId, int year, int month);
}

// DTOs
public record CreateEmployeeRequest(string EmployeeCode, string TitleTh, string FirstNameTh, string LastNameTh, string? FirstNameEn, string? LastNameEn, string? CitizenId, DateTime? DateOfBirth, string? Gender, string? Address, string? Phone, string? Email, string? Department, string? Position, string? EmploymentType, DateTime StartDate, decimal BaseSalary, string? SalaryType, string? BankName, string? BankAccountNumber, string? BankAccountName, string? SocialSecurityNumber, string? SocialSecurityHospital, bool IsSubjectToSocialSecurity, bool HasProvidentFund, decimal ProvidentFundEmployeePercent, decimal ProvidentFundEmployerPercent, Guid? BranchId, Guid? DimensionId);
public record UpdateEmployeeRequest(string? Position, string? Department, string? Phone, string? Email, decimal? BaseSalary, string? BankName, string? BankAccountNumber, string? SocialSecurityHospital, bool? HasProvidentFund, decimal? ProvidentFundEmployeePercent, decimal? ProvidentFundEmployerPercent, Guid? BranchId, Guid? DimensionId);
public record EmployeeResponse(Guid Id, string EmployeeCode, string TitleTh, string FirstNameTh, string LastNameTh, string? FirstNameEn, string? LastNameEn, string? CitizenId, string? Department, string? Position, string? EmploymentType, DateTime StartDate, DateTime? EndDate, decimal BaseSalary, string SalaryType, bool IsActive, DateTime CreatedAt);

public record CreatePayrollItemRequest(string Code, string Name, string? NameEn, string ItemType, string CalculationType, decimal? FixedAmount, decimal? Percentage, bool IsTaxable, Guid? AccountId);
public record PayrollItemResponse(Guid Id, string Code, string Name, string ItemType, string CalculationType, decimal? FixedAmount, decimal? Percentage, bool IsTaxable, bool IsActive);

public record CreatePayrollRunRequest(string Name, int Year, int Month, DateTime PayDate, DateTime PeriodStart, DateTime PeriodEnd);
public record PayrollRunResponse(Guid Id, string PayrollNumber, string Name, int Year, int Month, DateTime PayDate, string Status, decimal TotalGrossSalary, decimal TotalDeductions, decimal TotalNetPay, decimal TotalWithholdingTax, decimal TotalSocialSecurityEmployee, decimal TotalSocialSecurityEmployer, int EmployeeCount, DateTime CreatedAt);
public record PayrollDetailResponse(Guid EmployeeId, string EmployeeCode, string EmployeeName, decimal BaseSalary, decimal OvertimePay, decimal Allowances, decimal Commission, decimal Bonus, decimal GrossIncome, decimal SocialSecurityEmployee, decimal WithholdingTax, decimal ProvidentFundEmployee, decimal OtherDeductions, decimal TotalDeductions, decimal NetPay);
public record PayslipResponse(Guid EmployeeId, string EmployeeName, int Year, int Month, byte[] PdfData, string FileName);

public record CreateLeaveRequest(Guid EmployeeId, string LeaveType, DateTime StartDate, DateTime EndDate, decimal TotalDays, string? Reason);
public record LeaveResponse(Guid Id, Guid EmployeeId, string EmployeeName, string LeaveType, DateTime StartDate, DateTime EndDate, decimal TotalDays, string Status, string? Reason);

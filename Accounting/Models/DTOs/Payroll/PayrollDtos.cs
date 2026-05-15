namespace Accounting.Models.DTOs.Payroll;

public record CreateEmployeeRequest(
    string EmployeeCode, string TitleTh, string FirstNameTh,
    string LastNameTh, string? FirstNameEn, string? LastNameEn,
    string? CitizenId, DateTime? DateOfBirth, string? Gender,
    string? Address, string? Phone, string? Email,
    string? Department, string? Position, string? EmploymentType,
    DateTime StartDate, decimal BaseSalary, string? SalaryType,
    string? BankName, string? BankAccountNumber,
    string? BankAccountName, string? SocialSecurityNumber,
    string? SocialSecurityHospital, bool IsSubjectToSocialSecurity,
    bool HasProvidentFund, decimal ProvidentFundEmployeePercent,
    decimal ProvidentFundEmployerPercent,
    Guid? BranchId, Guid? DimensionId);

public record UpdateEmployeeRequest(
    string? Position, string? Department, string? Phone,
    string? Email, decimal? BaseSalary, string? BankName,
    string? BankAccountNumber, string? SocialSecurityHospital,
    bool? HasProvidentFund,
    decimal? ProvidentFundEmployeePercent,
    decimal? ProvidentFundEmployerPercent,
    Guid? BranchId, Guid? DimensionId);

public record EmployeeResponse(
    Guid Id, string EmployeeCode, string TitleTh,
    string FirstNameTh, string LastNameTh,
    string? FirstNameEn, string? LastNameEn,
    string? CitizenId, string? Department, string? Position,
    string? EmploymentType, DateTime StartDate, DateTime? EndDate,
    decimal BaseSalary, string SalaryType, bool IsActive,
    DateTime CreatedAt);

public record CreatePayrollItemRequest(
    string Code, string Name, string? NameEn,
    string ItemType, string CalculationType,
    decimal? FixedAmount, decimal? Percentage,
    bool IsTaxable, Guid? AccountId);

public record PayrollItemResponse(
    Guid Id, string Code, string Name, string ItemType,
    string CalculationType, decimal? FixedAmount,
    decimal? Percentage, bool IsTaxable, bool IsActive);

public record CreatePayrollRunRequest(
    string Name, int Year, int Month,
    DateTime PayDate, DateTime PeriodStart, DateTime PeriodEnd);

public record PayrollRunResponse(
    Guid Id, string PayrollNumber, string Name,
    int Year, int Month, DateTime PayDate, string Status,
    decimal TotalGrossSalary, decimal TotalDeductions,
    decimal TotalNetPay, decimal TotalWithholdingTax,
    decimal TotalSocialSecurityEmployee,
    decimal TotalSocialSecurityEmployer,
    int EmployeeCount, DateTime CreatedAt);

public record PayrollDetailResponse(
    Guid EmployeeId, string EmployeeCode, string EmployeeName,
    decimal BaseSalary, decimal OvertimePay, decimal Allowances,
    decimal Commission, decimal Bonus, decimal GrossIncome,
    decimal SocialSecurityEmployee, decimal WithholdingTax,
    decimal ProvidentFundEmployee, decimal OtherDeductions,
    decimal TotalDeductions, decimal NetPay);

public record PayslipResponse(
    Guid EmployeeId, string EmployeeName,
    int Year, int Month, byte[] PdfData, string FileName);

public record CreateLeaveRequest(
    Guid EmployeeId, string LeaveType,
    DateTime StartDate, DateTime EndDate,
    decimal TotalDays, string? Reason);

public record LeaveResponse(
    Guid Id, Guid EmployeeId, string EmployeeName,
    string LeaveType, DateTime StartDate, DateTime EndDate,
    decimal TotalDays, string Status, string? Reason,
    string? ApprovedBy = null,
    string? RejectionReason = null);

public record RejectLeaveRequest(string Reason);

using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Payroll;

namespace Accounting.Services.Interfaces;

public interface IPayrollService
{
    // Employees
    Task<EmployeeResponse> CreateEmployeeAsync(Guid companyId, CreateEmployeeRequest request);
    /// <summary>includePii=true เปิด field PII (CitizenId/Phone/Email) แบบ raw
    /// — ตั้งเฉพาะเมื่อ caller มี permission "pii:view". Default mask ทุก field
    /// ตาม PDPA ม.26 (deny by default).</summary>
    Task<EmployeeResponse> GetEmployeeAsync(Guid companyId, Guid employeeId, bool includePii = false);
    Task<PagedResponse<EmployeeResponse>> GetEmployeesAsync(Guid companyId, PagedRequest request, bool includePii = false);
    Task<EmployeeResponse> UpdateEmployeeAsync(Guid companyId, Guid employeeId, UpdateEmployeeRequest request);
    Task TerminateEmployeeAsync(Guid companyId, Guid employeeId, DateTime endDate);

    /// <summary>Bulk upsert from external HRIS — matches existing rows on
    /// (CompanyId, ExternalSystem, ExternalId) and updates them, otherwise
    /// inserts. Returns counts + per-row errors.</summary>
    Task<SyncEmployeesResponse> SyncEmployeesAsync(Guid companyId, SyncEmployeesRequest request);
    Task<EmployeeResponse?> GetEmployeeByExternalAsync(Guid companyId, string externalSystem, string externalId);
    Task DeleteEmployeeAsync(Guid companyId, Guid employeeId);
    Task<EmployeeResponse> RestoreEmployeeAsync(Guid companyId, Guid employeeId);

    /// <summary>คำนวณค่าชดเชยตาม Labor Code §118 (preview เท่านั้น — ไม่บันทึก GL).
    /// ใช้แสดงตัวเลขก่อนกดเลิกจ้าง; การจ่ายจริงทำผ่านเงินเดือนสุดท้ายหรือ Expense voucher.</summary>
    Task<SeverancePreviewResponse> PreviewSeverancePayAsync(Guid companyId, Guid employeeId, SeverancePreviewRequest request);

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
    /// <summary>ออกใบ 50 ทวิรายปีให้พนักงาน (ภงด.1 §40(1)) — รวบรวม WHT
    /// ทั้งปีต่อพนักงาน 1 ใบ คืน PDF เดียวเมื่อระบุ employee, คืน Zip
    /// เมื่อต้องการทุกคน.</summary>
    Task<(string FileName, byte[] Bytes)> GenerateAnnualEmployeeWhtCertsAsync(
        Guid companyId, int year, Guid? singleEmployeeId, string requestedBy);
    /// <summary>โพสต์ JE เงินชดเชยเลิกจ้าง §118 (Dr Severance / Cr Cash)
    /// — เรียกหลังยืนยันยอดจาก PreviewSeverancePayAsync.</summary>
    Task<Guid?> PostSeveranceAsync(Guid companyId, Guid employeeId,
        decimal amount, DateTime payDate, string postedBy);
    /// <summary>ปิดปี: คำนวณวันลาคงเหลือของพนักงาน × LeaveType ที่ carry-forward
    /// → upsert EmployeeLeaveBalance ของปีถัดไป (cap ด้วย CarryForwardCap).
    /// คืนจำนวนแถวที่ upsert.</summary>
    Task<int> RunYearEndLeaveCarryForwardAsync(Guid companyId, int year, string performedBy);
    Task<PayrollDetailResponse> GetPayrollDetailAsync(Guid companyId, Guid payrollRunId, Guid employeeId);
    Task<PayslipResponse> GeneratePayslipAsync(Guid companyId, Guid payrollRunId, Guid employeeId);

    // Leave
    Task<LeaveResponse> CreateLeaveAsync(Guid companyId, CreateLeaveRequest request);
    Task<LeaveResponse> GetLeaveAsync(Guid companyId, Guid leaveId);
    Task<LeaveResponse> ApproveLeaveAsync(Guid companyId, Guid leaveId, Guid approverUserId, string approverName);
    Task<LeaveResponse> RejectLeaveAsync(Guid companyId, Guid leaveId, Guid rejectorUserId, string rejectorName, RejectLeaveRequest request);
    Task<LeaveResponse> CancelLeaveAsync(Guid companyId, Guid leaveId);
    Task<List<LeaveResponse>> GetLeavesAsync(Guid companyId, Guid? employeeId, int? year);
    Task<LeaveBalanceResponse> GetLeaveBalanceAsync(Guid companyId, Guid employeeId, int year);

    // Tax: ภ.ง.ด.1 generation
    Task<object> GeneratePnd1Async(Guid companyId, int year, int month);
    Task<object> GenerateSsoReportAsync(Guid companyId, int year, int month);
    Task<object> GeneratePnd3Async(Guid companyId, int year, int month);
}

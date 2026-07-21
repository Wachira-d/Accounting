using Accounting.Helpers;
using System.Text;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.DTOs.Hr;
using Accounting.Models.DTOs.Payroll;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting.Services.Implementations;

/// <summary>Payroll management including payslip PDF generation</summary>
public class PayrollService : IPayrollService
{
    private readonly AccountingDbContext _db;
    private readonly IPdfGenerationService? _pdfService;
    private readonly IAccountingService? _accountingService;
    private readonly ISalaryAdvanceService? _salaryAdvanceService;
    private readonly IOrganizationService? _organizationService;
    private readonly IPermissionService? _permissionService;
    private readonly INotificationEngine? _notify;

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

    // Social security parameters are YEAR-DEPENDENT (เพดานปรับขึ้นเป็นขั้น
    // ตามพระราชกฤษฎีกา: 15,000 → 17,500 ปี 2026 → 20,000 ปี 2029 → 23,000
    // ปี 2032) — resolved per payroll-run year via GetSsoParamsAsync below:
    // company override row (SsoYearConfigs) first, then the statutory default
    // schedule in Helpers.SsoRateSchedule. No more hard-coded 15,000/750.

    // Thai personal income tax allowances (Revenue Code §47).
    // Simplified model — covers the deductions most SMEs configure:
    //   • Personal allowance: ฿60,000 / year (everyone)
    //   • Each Employee.TaxAllowances unit: ฿30,000 / year (spouse with no income
    //     and each qualifying child use 60K and 30K respectively — we treat
    //     TaxAllowances as a count of 30K-equivalent dependants which is the
    //     pragmatic UI choice)
    //   • SSO contributions are deductible up to the annual contribution cap
    //     (12 × monthly max ของปีนั้น — 10,500 ตั้งแต่ปี 2026, เดิม 9,000)
    //   • Provident-fund employee contribution is deductible up to 15 % of
    //     salary capped at ฿500,000 / yr; we use the actual annual contribution
    //     subject to that cap.
    private const decimal PitPersonalAllowance = 60_000m;
    private const decimal PitPerDependantAllowance = 30_000m;
    private const decimal PitPvdMaxDeductible = 500_000m;

    private readonly IWebhookService? _webhooks;
    private readonly ITaxFilingExportService? _taxFilingExport;
    private readonly IFileAttachmentService? _attachments;
    private readonly ILogger<PayrollService>? _logger;

    private readonly IEmailScheduleService? _emailSchedule;
    // ใช้สร้าง DI scope ใหม่สำหรับงาน background หลังจ่ายเงินเดือน (สร้าง PDF
    // สลิป/ภงด.1/สปส. นอก request เพื่อกัน proxy timeout จากงานหนัก)
    private readonly IServiceScopeFactory? _scopeFactory;

    public PayrollService(AccountingDbContext db, IPdfGenerationService? pdfService = null,
        IAccountingService? accountingService = null, ISalaryAdvanceService? salaryAdvanceService = null,
        IOrganizationService? organizationService = null, IPermissionService? permissionService = null,
        INotificationEngine? notify = null, IWebhookService? webhooks = null,
        ITaxFilingExportService? taxFilingExport = null, IFileAttachmentService? attachments = null,
        ILogger<PayrollService>? logger = null, IEmailScheduleService? emailSchedule = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _pdfService = pdfService;
        _accountingService = accountingService;
        _salaryAdvanceService = salaryAdvanceService;
        _organizationService = organizationService;
        _permissionService = permissionService;
        _notify = notify;
        _webhooks = webhooks;
        _emailSchedule = emailSchedule;
        _taxFilingExport = taxFilingExport;
        _attachments = attachments;
        _logger = logger;
    }

    private async Task FireWebhookAsync(Guid companyId, string eventType, object payload)
    {
        if (_webhooks == null) return;
        try { await _webhooks.TriggerAsync(companyId, eventType, payload); }
        catch { /* fire-and-forget */ }
    }

    /// <summary>Resolve the SSO parameters effective for a year: the
    /// company's SsoYearConfigs override row wins; otherwise the statutory
    /// schedule (SsoRateSchedule). Returns employee/employer monthly caps
    /// pre-computed (= ceiling × rate). Buddhist-era years normalised.</summary>
    internal async Task<(decimal MaxBase, decimal Rate, decimal EmployerRate, decimal MaxContribution, decimal EmployerMaxContribution)>
        GetSsoParamsAsync(Guid companyId, int year)
    {
        var y = year > 2400 ? year - 543 : year;
        var cfg = await _db.Set<SsoYearConfig>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Year == y && !c.IsDeleted);
        decimal ceiling, rate, erRate;
        if (cfg != null)
        {
            ceiling = cfg.WageCeiling;
            rate = cfg.RatePercent / 100m;
            erRate = cfg.EmployerRatePercent / 100m;
        }
        else
        {
            (ceiling, rate) = Accounting.Helpers.SsoRateSchedule.GetDefault(y);
            erRate = rate;
        }
        return (ceiling, rate, erRate,
            Math.Round(ceiling * rate, 2), Math.Round(ceiling * erRate, 2));
    }

    /// <summary>Fire-and-forget — swallowed inside the engine itself.</summary>
    private Task NotifyHrAsync(Guid companyId, string eventKey, Guid employeeId, Guid? actorUserId,
        string title, string message, Guid entityId, string entityType, string actionUrl) =>
        _notify == null ? Task.CompletedTask : _notify.DispatchAsync(companyId, eventKey, new NotificationContext
        {
            Title = title, Message = message, ActionUrl = actionUrl,
            EntityType = entityType, EntityId = entityId,
            RequesterEmployeeId = employeeId, ActorUserId = actorUserId,
        });

    /// <summary>Payroll-run-level events have no individual requester —
    /// recipients resolve to HR / Accounting / Owner only.</summary>
    private Task NotifyRunAsync(Guid companyId, string eventKey, Guid? actorUserId,
        string title, string message, Guid entityId) =>
        _notify == null ? Task.CompletedTask : _notify.DispatchAsync(companyId, eventKey, new NotificationContext
        {
            Title = title, Message = message,
            ActionUrl = "/pages/payroll.html#tab=runs",
            EntityType = "PayrollRun", EntityId = entityId,
            ActorUserId = actorUserId,
        });

    /// <summary>True when HR enforcement is configured on for the company.
    /// Cached fetch — small CompanySettings row, used in hot HR paths.</summary>
    private async Task<bool> IsManagerApprovalEnforcedAsync(Guid companyId) =>
        await _db.Set<CompanySettings>()
            .AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.EnforceManagerApproval)
            .FirstOrDefaultAsync();

    /// <summary>Allow approval when the user is an Owner / SystemAdmin
    /// of the company (the documented HR override) — applied alongside
    /// the direct-manager check when EnforceManagerApproval is on.</summary>
    private async Task<bool> IsPrivilegedHrApproverAsync(Guid companyId, Guid userId)
    {
        var role = await _db.Set<CompanyUser>()
            .AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
            .Select(cu => (UserRole?)cu.Role)
            .FirstOrDefaultAsync();
        return role == UserRole.Owner || role == UserRole.SystemAdmin;
    }

    /// <summary>Check the approver against the leave/advance's requesting
    /// employee's direct manager (with department-head fallback) — plus
    /// the Owner / SystemAdmin and granular-permission overrides. Throws
    /// a clear error when EnforceManagerApproval is on and no path
    /// authorises the user. No-op when enforcement is off.</summary>
    private async Task EnsureCanApproveAsync(Guid companyId, Guid requestingEmployeeId, Guid approverUserId,
        string actionLabel, string? overridePermissionKey = null)
    {
        if (!await IsManagerApprovalEnforcedAsync(companyId)) return;
        if (_organizationService == null) return;

        var info = await _organizationService.GetDirectManagerInfoAsync(companyId, requestingEmployeeId);
        var isManager = info.ManagerUserId == approverUserId;
        var isDeptHeadFallback = info.ManagerUserId == null && info.DepartmentHeadEmployeeId.HasValue
            && await _db.Employees.AnyAsync(e => e.Id == info.DepartmentHeadEmployeeId.Value
                && e.UserId == approverUserId);
        if (isManager || isDeptHeadFallback) return;
        if (await IsPrivilegedHrApproverAsync(companyId, approverUserId)) return;
        // Granular-permission override — admins can grant the specific
        // permission key to any company role.
        if (!string.IsNullOrEmpty(overridePermissionKey) && _permissionService != null
            && await _permissionService.HasPermissionAsync(companyId, approverUserId, overridePermissionKey))
            return;

        var approver = info.ManagerName ?? info.DepartmentHeadName ?? "หัวหน้าโดยตรง";
        throw new InvalidOperationException(
            $"ไม่มีสิทธิ์{actionLabel} — ระบบกำหนดให้เฉพาะ {approver} (หรือเจ้าของบริษัท / ผู้ที่ได้รับสิทธิ์) เท่านั้นที่ทำได้");
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
            TitleTh = Accounting.Helpers.ThaiTitleHelper.Normalize(request.TitleTh), // กัน "Mrs." หลุดไปไฟล์ยื่น สปส./สรรพากร
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
            DimensionId = request.DimensionId,
            DepartmentId = request.DepartmentId,
            PositionId = request.PositionId,
            DirectManagerId = request.DirectManagerId,
            CostBehavior = request.CostBehavior
                ?? ((request.SalaryType ?? "Monthly") == "Monthly" ? "Fixed" : "Variable"),
            ExternalId = request.ExternalId,
            ExternalSystem = request.ExternalSystem,
            LastSyncedAt = request.ExternalId != null ? DateTime.UtcNow : null,
            LineId = request.LineId,
        };

        _db.Set<Employee>().Add(employee);
        await _db.SaveChangesAsync();

        // สปส.1-03 — ขึ้นทะเบียนผู้ประกันตนภายใน 30 วันนับจากวันเริ่มงาน (§34).
        // สร้าง ComplianceFiling row เป็น deadline tracker ให้ surface ในปฏิทิน
        // compliance ที่มีอยู่ (ไม่ต้องสร้าง UI ใหม่). เฉพาะพนักงานที่อยู่ในระบบ สปส.
        if (employee.IsSubjectToSocialSecurity)
        {
            await TrackSsoEmployeeFilingAsync(companyId, "SSO_NewEmployee", "สปส.1-03",
                employee.StartDate, employee.StartDate.AddDays(30),
                $"ขึ้นทะเบียน {employee.FirstNameTh} {employee.LastNameTh} ({employee.EmployeeCode}) — ภายใน 30 วัน");
        }

        await FireWebhookAsync(companyId, "employee.created", new
        {
            id = employee.Id, employeeCode = employee.EmployeeCode,
            firstNameTh = employee.FirstNameTh, lastNameTh = employee.LastNameTh,
            email = employee.Email, employmentType = employee.EmploymentType,
            salaryType = employee.SalaryType, baseSalary = employee.BaseSalary,
            externalId = employee.ExternalId, externalSystem = employee.ExternalSystem,
        });

        return await GetEmployeeAsync(companyId, employee.Id);
    }

    public async Task<EmployeeResponse> GetEmployeeAsync(Guid companyId, Guid employeeId, bool includePii = false, bool includeSalary = true)
    {
        var employee = await _db.Set<Employee>()
            .Include(e => e.DepartmentRef)
            .Include(e => e.PositionRef)
            .Include(e => e.DirectManager)
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        return MapToEmployeeResponse(employee, includePii, includeSalary);
    }

    public async Task<PagedResponse<EmployeeResponse>> GetEmployeesAsync(Guid companyId, PagedRequest request, bool includePii = false, bool includeSalary = true)
    {
        var query = _db.Set<Employee>()
            .Include(e => e.DepartmentRef)
            .Include(e => e.PositionRef)
            .Include(e => e.DirectManager)
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
            items.Select(e => MapToEmployeeResponse(e, includePii)).ToList(),
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
        // Org structure (preferred over the legacy string Department/Position)
        if (request.DepartmentId.HasValue) employee.DepartmentId = request.DepartmentId.Value;
        if (request.PositionId.HasValue) employee.PositionId = request.PositionId.Value;
        if (request.DirectManagerId.HasValue)
        {
            if (request.DirectManagerId.Value == employeeId)
                throw new InvalidOperationException("พนักงานไม่สามารถเป็นหัวหน้าของตัวเองได้");
            employee.DirectManagerId = request.DirectManagerId.Value;
        }
        // Onboarding / offboarding (preserves all historical HR + GL records).
        // When an Employee is offboarded we ALSO mirror it onto the linked
        // User.Status so app access (login + LIFF) is disabled — the
        // employee record itself stays for audit. Re-onboarding restores
        // Active. Only mirrored when the User isn't already in a stricter
        // state (Suspended, PendingVerification) which is admin-managed.
        if (request.CostBehavior != null) employee.CostBehavior = request.CostBehavior;
        if (request.SalaryType != null) employee.SalaryType = request.SalaryType;
        // LastSyncedAt only stamps when ExternalId is actually present — a
        // plain UI edit that re-sends an unchanged costBehavior shouldn't
        // look like an HRIS sync.
        if (request.ExternalId != null)
        {
            employee.ExternalId = request.ExternalId;
            employee.LastSyncedAt = DateTime.UtcNow;
        }
        if (request.ExternalSystem != null) employee.ExternalSystem = request.ExternalSystem;
        if (request.LineId != null) employee.LineId = request.LineId;
        if (request.IsActive.HasValue)
        {
            employee.IsActive = request.IsActive.Value;
            if (employee.UserId.HasValue)
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == employee.UserId.Value);
                if (user != null)
                {
                    if (!request.IsActive.Value && user.Status == UserStatus.Active)
                        user.Status = UserStatus.Inactive;
                    else if (request.IsActive.Value && user.Status == UserStatus.Inactive)
                        user.Status = UserStatus.Active;
                }
            }
        }

        employee.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await FireWebhookAsync(companyId, "employee.updated", new
        {
            id = employee.Id, employeeCode = employee.EmployeeCode,
            isActive = employee.IsActive,
            externalId = employee.ExternalId, externalSystem = employee.ExternalSystem,
        });

        return await GetEmployeeAsync(companyId, employee.Id);
    }

    public async Task<SyncEmployeesResponse> SyncEmployeesAsync(Guid companyId, SyncEmployeesRequest request)
    {
        var inserted = 0; var updated = 0; var skipped = 0;
        var errors = new List<string>();

        foreach (var r in request.Rows)
        {
            if (string.IsNullOrWhiteSpace(r.ExternalId))
            {
                errors.Add($"{r.EmployeeCode}: ต้องระบุ ExternalId เพื่อ sync");
                skipped++;
                continue;
            }
            var existing = await _db.Set<Employee>().FirstOrDefaultAsync(e =>
                e.CompanyId == companyId
                && e.ExternalSystem == request.ExternalSystem
                && e.ExternalId == r.ExternalId
                && !e.IsDeleted);

            if (existing != null)
            {
                existing.FirstNameTh = r.FirstNameTh;
                existing.LastNameTh = r.LastNameTh;
                if (r.FirstNameEn != null) existing.FirstNameEn = r.FirstNameEn;
                if (r.LastNameEn != null) existing.LastNameEn = r.LastNameEn;
                if (r.Email != null) existing.Email = r.Email;
                if (r.Phone != null) existing.Phone = r.Phone;
                if (r.Department != null) existing.Department = r.Department;
                if (r.Position != null) existing.Position = r.Position;
                if (r.BaseSalary > 0) existing.BaseSalary = r.BaseSalary;
                if (r.SalaryType != null) existing.SalaryType = r.SalaryType;
                if (r.CostBehavior != null) existing.CostBehavior = r.CostBehavior;
                existing.LastSyncedAt = DateTime.UtcNow;
                existing.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
            else
            {
                try
                {
                    var dup = await _db.Set<Employee>()
                        .AnyAsync(e => e.CompanyId == companyId && e.EmployeeCode == r.EmployeeCode && !e.IsDeleted);
                    if (dup)
                    {
                        errors.Add($"{r.EmployeeCode}: รหัสซ้ำ");
                        skipped++;
                        continue;
                    }
                    var newEmp = new Employee
                    {
                        CompanyId = companyId,
                        EmployeeCode = r.EmployeeCode,
                        TitleTh = Accounting.Helpers.ThaiTitleHelper.Normalize(r.TitleTh),
                        FirstNameTh = r.FirstNameTh,
                        LastNameTh = r.LastNameTh,
                        FirstNameEn = r.FirstNameEn,
                        LastNameEn = r.LastNameEn,
                        CitizenId = r.CitizenId,
                        Email = r.Email,
                        Phone = r.Phone,
                        Department = r.Department,
                        Position = r.Position,
                        EmploymentType = r.EmploymentType,
                        StartDate = r.StartDate,
                        BaseSalary = r.BaseSalary,
                        SalaryType = r.SalaryType ?? "Monthly",
                        BankName = r.BankName,
                        BankAccountNumber = r.BankAccountNumber,
                        BankAccountName = r.BankAccountName,
                        SocialSecurityNumber = r.SocialSecurityNumber,
                        IsSubjectToSocialSecurity = r.IsSubjectToSocialSecurity,
                        CostBehavior = r.CostBehavior
                            ?? ((r.SalaryType ?? "Monthly") == "Monthly" ? "Fixed" : "Variable"),
                        ExternalId = r.ExternalId,
                        ExternalSystem = request.ExternalSystem,
                        LastSyncedAt = DateTime.UtcNow,
                    };
                    _db.Set<Employee>().Add(newEmp);
                    inserted++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{r.EmployeeCode}: {ex.Message}");
                    skipped++;
                }
            }
        }

        await _db.SaveChangesAsync();
        return new SyncEmployeesResponse(inserted, updated, skipped, errors);
    }

    public async Task<EmployeeResponse?> GetEmployeeByExternalAsync(Guid companyId, string externalSystem, string externalId)
    {
        var emp = await _db.Set<Employee>()
            .Include(e => e.DepartmentRef).Include(e => e.PositionRef).Include(e => e.DirectManager)
            .FirstOrDefaultAsync(e => e.CompanyId == companyId
                && e.ExternalSystem == externalSystem
                && e.ExternalId == externalId
                && !e.IsDeleted);
        return emp == null ? null : MapToEmployeeResponse(emp);
    }

    public async Task DeleteEmployeeAsync(Guid companyId, Guid employeeId)
    {
        var emp = await _db.Set<Employee>()
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        // Block deletion when the employee is still on an open payroll
        // run or has unallocated time entries — soft-deletion would
        // orphan those rows. Partners must close out the run / clear
        // time first.
        var openRun = await _db.Set<PayrollDetail>()
            .Include(d => d.PayrollRun)
            .AnyAsync(d => d.EmployeeId == employeeId
                && d.CompanyId == companyId
                && d.PayrollRun.Status != "Paid"
                && d.PayrollRun.Status != "Voided"
                && !d.IsDeleted);
        if (openRun)
            throw new InvalidOperationException("พนักงานยังอยู่ในรอบจ่ายเงินเดือนที่ยังไม่ปิด — ปิดรอบก่อน");

        emp.IsDeleted = true;
        emp.IsActive = false;
        emp.EndDate ??= DateTime.UtcNow.Date;
        emp.UpdatedAt = DateTime.UtcNow;

        // Mirror to the linked User so login + LIFF are revoked. Stay
        // conservative — only flip Active → Inactive, never override
        // stricter admin-managed states (Suspended / PendingVerification).
        if (emp.UserId.HasValue)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == emp.UserId.Value);
            if (user != null && user.Status == UserStatus.Active)
                user.Status = UserStatus.Inactive;
        }

        await _db.SaveChangesAsync();
        await FireWebhookAsync(companyId, "employee.deleted", new
        {
            id = emp.Id, employeeCode = emp.EmployeeCode,
            externalId = emp.ExternalId, externalSystem = emp.ExternalSystem,
        });
    }

    public async Task<EmployeeResponse> RestoreEmployeeAsync(Guid companyId, Guid employeeId)
    {
        // Employee has a global query filter (!IsDeleted) — bypass it
        // so we can locate the soft-deleted row to restore.
        var emp = await _db.Set<Employee>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId && e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงานที่ถูกลบไว้");

        // The unique index on (CompanyId, EmployeeCode) is filtered to
        // active rows, so a partner may have created a new active
        // employee with the same code between delete and restore.
        // Block restore in that case rather than crashing at SaveChanges.
        var activeDup = await _db.Set<Employee>()
            .AnyAsync(e => e.CompanyId == companyId
                && e.EmployeeCode == emp.EmployeeCode
                && e.Id != employeeId);
        if (activeDup)
            throw new InvalidOperationException(
                $"รหัสพนักงาน {emp.EmployeeCode} ถูกใช้กับพนักงานคนอื่นแล้ว — เปลี่ยนรหัสก่อนกู้คืน");

        emp.IsDeleted = false;
        emp.IsActive = true;
        emp.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await FireWebhookAsync(companyId, "employee.restored", new
        {
            id = emp.Id, employeeCode = emp.EmployeeCode,
            externalId = emp.ExternalId, externalSystem = emp.ExternalSystem,
        });
        return await GetEmployeeAsync(companyId, emp.Id);
    }

    public async Task<SeverancePreviewResponse> PreviewSeverancePayAsync(
        Guid companyId, Guid employeeId, SeverancePreviewRequest request)
    {
        var employee = await _db.Set<Employee>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        var (days, eligible, explanation) = ComputeSeveranceDays(
            employee.StartDate, request.EndDate, request.TerminationReason);
        var years = (decimal)((request.EndDate - employee.StartDate).TotalDays / 365.25);
        // §118 ใช้ค่าจ้างวันสุดท้าย (last daily rate). System stores monthly BaseSalary →
        // divide by 30 per RD's labour-court convention.
        var dailyRate = Math.Round(employee.BaseSalary / 30m, 2, MidpointRounding.AwayFromZero);
        var amount = eligible ? dailyRate * days : 0m;

        return new SeverancePreviewResponse(
            employee.Id,
            $"{employee.TitleTh}{employee.FirstNameTh} {employee.LastNameTh}".Trim(),
            employee.StartDate,
            request.EndDate,
            Math.Round(years, 2, MidpointRounding.AwayFromZero),
            days,
            dailyRate,
            Math.Round(amount, 2, MidpointRounding.AwayFromZero),
            eligible,
            explanation);
    }

    /// <summary>Number of calendar days the leave covers inside [periodStart, periodEnd].
    /// Replaces the old <c>l.TotalDays</c> Sum which double-counted leaves spanning
    /// multiple months (a 60-day maternity leave would deduct 60 days from every
    /// month it touched).</summary>
    private static decimal DaysInPeriod(EmployeeLeave leave, DateTime periodStart, DateTime periodEnd)
    {
        var from = leave.StartDate.Date > periodStart.Date ? leave.StartDate.Date : periodStart.Date;
        var to = leave.EndDate.Date < periodEnd.Date ? leave.EndDate.Date : periodEnd.Date;
        if (to < from) return 0m;
        return (decimal)((to - from).TotalDays + 1);
    }

    /// <summary>Labor Code §118 bracket lookup. Returns (days, eligible, reason).</summary>
    private static (int Days, bool Eligible, string Reason) ComputeSeveranceDays(
        DateTime startDate, DateTime endDate, string? terminationReason)
    {
        // §583 / §119: employer-with-cause terminations (gross misconduct,
        // dishonesty, intentional damage, repeated negligence after warning,
        // criminal conviction, abandonment ≥3 working days) AND voluntary
        // resignation get no severance.
        var reason = (terminationReason ?? "").Trim().ToLowerInvariant();
        var noSeveranceFlags = new[] {
            "resign", "voluntary", "ลาออก",
            "misconduct", "gross misconduct", "dishonesty", "ทุจริต",
            "criminal", "abandon", "ทอดทิ้ง",
            "probation", "ทดลองงาน"  // probation period termination also exempt
        };
        if (noSeveranceFlags.Any(f => reason.Contains(f)))
            return (0, false, $"ไม่มีสิทธิ์ค่าชดเชยตามมาตรา 119 / 583 (เหตุผล: {terminationReason})");

        var totalDays = (endDate - startDate).TotalDays;
        // §118 brackets (post-2019 amendment added the 400-day tier for >20y)
        if (totalDays < 120) return (0, false, "อายุงานน้อยกว่า 120 วัน — ไม่อยู่ในเกณฑ์ §118");
        if (totalDays < 365) return (30, true, "อายุงาน 120 วัน – 1 ปี → 30 วัน");
        if (totalDays < 365 * 3) return (90, true, "อายุงาน 1 – 3 ปี → 90 วัน");
        if (totalDays < 365 * 6) return (180, true, "อายุงาน 3 – 6 ปี → 180 วัน");
        if (totalDays < 365 * 10) return (240, true, "อายุงาน 6 – 10 ปี → 240 วัน");
        if (totalDays < 365 * 20) return (300, true, "อายุงาน 10 – 20 ปี → 300 วัน");
        return (400, true, "อายุงานเกิน 20 ปี → 400 วัน (Labor Code §118 หลังแก้ไข พ.ศ. 2562)");
    }

    /// <summary>โพสต์ JE เงินชดเชยเลิกจ้าง (มาตรา 118 พรบ.คุ้มครองแรงงาน)
    /// — เรียกตามหลังที่ใช้ PreviewSeverancePayAsync เพื่อยืนยันยอด.
    /// Dr ค่าใช้จ่ายเงินชดเชย (5xxx หรือ default Expense) / Cr เงินสด-ธนาคาร.
    /// บันทึก reference ใน description ผูกกับ employee + termination date.</summary>
    public async Task<Guid?> PostSeveranceAsync(
        Guid companyId, Guid employeeId, decimal amount, DateTime payDate, string postedBy)
    {
        if (amount <= 0)
            throw new InvalidOperationException("จำนวนเงินชดเชยต้องมากกว่า 0");
        if (_accountingService == null)
            throw new InvalidOperationException("Accounting service ไม่พร้อมใช้งาน");

        var emp = await _db.Set<Employee>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        // Fiscal-period guard (same as ProcessPaymentAsync).
        var fp = await _db.FiscalPeriods.AsNoTracking()
            .Where(p => p.CompanyId == companyId
                && p.StartDate <= payDate && p.EndDate >= payDate)
            .Select(p => new { p.Status, p.Name })
            .FirstOrDefaultAsync();
        if (fp != null && fp.Status == FiscalPeriodStatus.Closed)
            throw new InvalidOperationException(
                $"งวดบัญชี \"{fp.Name}\" ปิดแล้ว — ไม่สามารถโพสต์รายการเงินชดเชยเข้างวดนี้ได้");

        var severanceAccount = await _db.ChartOfAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "54125" && a.IsActive)
            ?? await _db.ChartOfAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.CompanyId == companyId
                    && a.AccountType == AccountType.Expense && a.IsActive)
            ?? throw new InvalidOperationException("ไม่พบบัญชีค่าใช้จ่ายในผังบัญชี");

        var cashAccount = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId
                && a.AccountCode.StartsWith("111") && a.AccountCode.Length >= 5 && a.IsActive)
            .OrderBy(a => a.AccountCode)
            .FirstOrDefaultAsync()
            ?? await _db.ChartOfAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.CompanyId == companyId
                    && a.AccountCode.StartsWith("111") && a.IsActive)
            ?? throw new InvalidOperationException("ไม่พบบัญชีเงินสดในผังบัญชี");

        var jeReq = new Models.DTOs.Accounting.CreateJournalEntryRequest(
            EntryDate: payDate,
            Description: $"เงินชดเชยเลิกจ้าง (§118) — {emp.EmployeeCode} {emp.FirstNameTh} {emp.LastNameTh}",
            Reference: $"SEV-{emp.EmployeeCode}-{payDate:yyyyMMdd}",
            Lines: new List<Models.DTOs.Accounting.JournalLineRequest>
            {
                new(severanceAccount.Id, amount, 0, "เงินชดเชยเลิกจ้าง"),
                new(cashAccount.Id, 0, amount, "จ่ายเงินชดเชย"),
            },
            JournalType: JournalType.CashPayments);

        var je = await _accountingService.CreateJournalEntryAsync(companyId, jeReq, postedBy);
        await _accountingService.PostJournalEntryAsync(companyId, je.Id);

        await NotifyRunAsync(companyId, NotificationEvents.PayrollPaid, actorUserId: null,
            title: $"โพสต์เงินชดเชยเลิกจ้าง {emp.FirstNameTh} {emp.LastNameTh}",
            message: $"จำนวน {amount:N2} บาท · JE {je.EntryNumber}",
            entityId: emp.Id);

        return je.Id;
    }

    /// <summary>Year-end carry-forward: สำหรับพนักงานที่ Active ทุกคน × ทุก
    /// LeaveType ที่ AllowCarryForward=true, สร้าง EmployeeLeaveBalance ใน
    /// targetYear (= year + 1) โดย CarriedForwardDays = unused days ใน year
    /// (clamped to LeaveType.CarryForwardCap). Idempotent: upsert ตามคีย์
    /// (Company, Employee, Year, LeaveType). คืนจำนวนแถวที่ upsert.</summary>
    public async Task<int> RunYearEndLeaveCarryForwardAsync(
        Guid companyId, int year, string performedBy)
    {
        var targetYear = year + 1;
        var leaveTypes = await _db.Set<LeaveType>().AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted
                && t.CarryForward && t.IsActive)
            .ToListAsync();
        if (leaveTypes.Count == 0) return 0;

        var employees = await _db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId && !e.IsDeleted && e.IsActive)
            .Select(e => new { e.Id, e.StartDate })
            .ToListAsync();
        if (employees.Count == 0) return 0;

        // Days used + base balance ของปีต้นทาง — รวม base quota (จาก
        // LeaveType) + carry-forward เดิม (ถ้ามี) + adjustment + ลบ
        // จำนวนวันลาที่ Approved
        var balances = await _db.Set<EmployeeLeaveBalance>()
            .Where(b => b.CompanyId == companyId && b.Year == year && !b.IsDeleted)
            .ToListAsync();
        var leavesByEmpType = await _db.Set<EmployeeLeave>()
            .Where(l => l.CompanyId == companyId && !l.IsDeleted
                && l.Status == "Approved"
                && l.StartDate.Year <= year && l.EndDate.Year >= year)
            .GroupBy(l => new { l.EmployeeId, l.LeaveType })
            .Select(g => new { g.Key.EmployeeId, g.Key.LeaveType, Total = g.Sum(x => x.TotalDays) })
            .ToListAsync();

        var existingTarget = await _db.Set<EmployeeLeaveBalance>()
            .Where(b => b.CompanyId == companyId && b.Year == targetYear && !b.IsDeleted)
            .ToListAsync();
        var existingByKey = existingTarget.ToDictionary(
            b => (b.EmployeeId, b.LeaveTypeCode), b => b);

        int upserts = 0;
        foreach (var emp in employees)
        {
            foreach (var lt in leaveTypes)
            {
                var srcBal = balances.FirstOrDefault(b => b.EmployeeId == emp.Id && b.LeaveTypeCode == lt.Code);
                var carriedFrom = srcBal?.CarriedForwardDays ?? 0m;
                var adj = srcBal?.AdjustmentDays ?? 0m;
                var used = leavesByEmpType.FirstOrDefault(x => x.EmployeeId == emp.Id && x.LeaveType == lt.Code)?.Total ?? 0m;
                var unused = Math.Max(0, lt.AnnualQuota + carriedFrom + adj - used);
                // Cap by CarryForwardCap (0 = no cap, รักษาความเข้ากันได้)
                var carried = lt.CarryForwardCap.HasValue && lt.CarryForwardCap.Value > 0
                    ? Math.Min(unused, lt.CarryForwardCap.Value) : unused;
                if (carried <= 0) continue;

                if (existingByKey.TryGetValue((emp.Id, lt.Code), out var ex))
                {
                    ex.CarriedForwardDays = carried;
                    ex.Phase = "YearEnd";
                    ex.UpdatedBy = performedBy;
                    ex.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    _db.Set<EmployeeLeaveBalance>().Add(new EmployeeLeaveBalance
                    {
                        CompanyId = companyId,
                        EmployeeId = emp.Id,
                        Year = targetYear,
                        LeaveTypeCode = lt.Code,
                        CarriedForwardDays = carried,
                        AdjustmentDays = 0,
                        Phase = "YearEnd",
                        CreatedBy = performedBy,
                    });
                }
                upserts++;
            }
        }
        await _db.SaveChangesAsync();
        return upserts;
    }

    public async Task TerminateEmployeeAsync(Guid companyId, Guid employeeId, DateTime endDate)
    {
        var employee = await _db.Set<Employee>()
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        employee.EndDate = endDate;
        employee.IsActive = false;
        employee.UpdatedAt = DateTime.UtcNow;

        // Mirror offboarding to the linked User so app access (login +
        // LIFF) is disabled. Same conservative rule as the IsActive
        // toggle above — only flip Active → Inactive, never override
        // stricter admin-managed states (Suspended / PendingVerification).
        if (employee.UserId.HasValue)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == employee.UserId.Value);
            if (user != null && user.Status == UserStatus.Active)
                user.Status = UserStatus.Inactive;
        }

        // สปส.6-09 — แจ้งสิ้นสุดความเป็นผู้ประกันตน ภายในวันที่ 15 ของเดือนถัดไป.
        // deadline tracker ผ่าน ComplianceFiling (surface ในปฏิทิน compliance).
        if (employee.IsSubjectToSocialSecurity)
        {
            var sps609Due = new DateTime(endDate.Year, endDate.Month, 15).AddMonths(1);
            await TrackSsoEmployeeFilingAsync(companyId, "SSO_Termination", "สปส.6-09",
                endDate, sps609Due,
                $"แจ้งออก {employee.FirstNameTh} {employee.LastNameTh} ({employee.EmployeeCode}) — ภายในวันที่ 15 ของเดือนถัดไป");
        }

        await _db.SaveChangesAsync();
        await FireWebhookAsync(companyId, "employee.terminated", new
        {
            id = employee.Id, employeeCode = employee.EmployeeCode,
            endDate = employee.EndDate,
            externalId = employee.ExternalId, externalSystem = employee.ExternalSystem,
        });
    }

    /// <summary>สร้าง ComplianceFiling deadline tracker สำหรับ สปส.1-03/6-09
    /// (event-driven ตอนพนักงานเข้า/ออก). Idempotent: ถ้ามี row เดียวกัน
    /// (type + เดือน + ปี) อยู่แล้วไม่สร้างซ้ำ. Fire-and-forget — fail
    /// ไม่ทำให้ create/terminate พัง (deadline tracker เป็น nice-to-have).</summary>
    private async Task TrackSsoEmployeeFilingAsync(
        Guid companyId, string filingType, string formCode,
        DateTime eventDate, DateTime dueDate, string note)
    {
        try
        {
            var year = eventDate.Year;
            var month = eventDate.Month;
            var exists = await _db.Set<ComplianceFiling>().AnyAsync(f =>
                f.CompanyId == companyId && f.FilingType == filingType
                && f.Year == year && f.Month == month && f.Status == "NotStarted");
            if (exists)
            {
                // มี row เดือนนี้แล้ว → append note (มีหลายคนเข้า/ออกเดือนเดียวกัน)
                var existing = await _db.Set<ComplianceFiling>().FirstAsync(f =>
                    f.CompanyId == companyId && f.FilingType == filingType
                    && f.Year == year && f.Month == month && f.Status == "NotStarted");
                existing.Notes = string.IsNullOrWhiteSpace(existing.Notes)
                    ? note : existing.Notes + "\n" + note;
            }
            else
            {
                _db.Set<ComplianceFiling>().Add(new ComplianceFiling
                {
                    CompanyId = companyId,
                    FilingType = filingType,
                    FormCode = formCode,
                    Year = year,
                    Month = month,
                    DueDate = dueDate,
                    Status = "NotStarted",
                    Notes = note,
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TrackSsoEmployeeFiling failed (non-fatal) for {Type}", filingType);
        }
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

    /// <summary>
    /// Import payroll run จากระบบนอก (TakeTime) — รับยอดสำเร็จรูปต่อพนักงาน
    /// แล้วสร้าง run สถานะ Calculated ทันที (ไม่คำนวณใหม่). approve/pay/exports
    /// เดิมทำงานต่อจากยอดที่ส่งมา → ออก GL + ภงด.1 + สปส.1-10 + 50ทวิ + payslip
    /// จากตัวเลขที่ผันแปรของ TakeTime จริง ๆ.
    ///
    /// Validation: gross − totalDeductions(ฝั่งลูกจ้าง) == netPay ต่อบรรทัด;
    /// employee map เจอ (ExternalId → CitizenId); account code resolve ได้.
    /// Idempotency: ExternalRunRef ซ้ำ → คืน run เดิม ไม่สร้างซ้ำ.
    /// </summary>
    public async Task<ImportPayrollRunResult> ImportPayrollRunAsync(
        Guid companyId, ImportPayrollRunRequest request, string createdBy)
    {
        if (request.Recalculate)
            throw new InvalidOperationException(
                "endpoint นี้สำหรับ import ยอดสำเร็จรูป (recalculate=false). ถ้าต้องการให้ NextAcc " +
                "คำนวณเอง ใช้ POST /runs → /calculate แทน.");
        if (request.Month < 1 || request.Month > 12)
            throw new InvalidOperationException("เดือนต้องอยู่ระหว่าง 1 ถึง 12");
        if (request.Lines == null || request.Lines.Count == 0)
            throw new InvalidOperationException("ต้องมีรายการพนักงานอย่างน้อย 1 คน");

        // ── Idempotency ──
        if (!string.IsNullOrWhiteSpace(request.ExternalRunRef))
        {
            var existing = await _db.Set<PayrollRun>()
                .FirstOrDefaultAsync(r => r.CompanyId == companyId
                    && r.ExternalRunRef == request.ExternalRunRef && !r.IsDeleted);
            if (existing != null)
                return ToImportResult(existing, wasExisting: true,
                    new List<string> { $"ExternalRunRef '{request.ExternalRunRef}' มีอยู่แล้ว — คืน run เดิม (ไม่สร้างซ้ำ)" });
        }

        var warnings = new List<string>();

        // ── Resolve employees: load ทั้งบริษัท (CitizenId decrypt in-memory
        //    ผ่าน ValueConverter — query SQL by encrypted ไม่ได้). ──
        var employees = await _db.Set<Employee>()
            .Where(e => e.CompanyId == companyId && !e.IsDeleted)
            .ToListAsync();
        var byExtId = employees.Where(e => !string.IsNullOrEmpty(e.ExternalId))
            .GroupBy(e => e.ExternalId!).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        string DigitsOnly(string? s) => new string((s ?? "").Where(char.IsDigit).ToArray());
        var byCitizen = employees.Where(e => !string.IsNullOrEmpty(e.CitizenId))
            .GroupBy(e => DigitsOnly(e.CitizenId)).ToDictionary(g => g.Key, g => g.First());

        // ── Resolve + validate ทุก line ก่อน (atomic: ผิดคนเดียว reject ทั้ง run) ──
        var resolved = new List<(ImportPayrollLine Line, Employee Emp)>();
        foreach (var (line, idx) in request.Lines.Select((l, i) => (l, i + 1)))
        {
            Employee? emp = null;
            if (!string.IsNullOrWhiteSpace(line.EmployeeExternalId)
                && byExtId.TryGetValue(line.EmployeeExternalId, out var e1)) emp = e1;
            if (emp == null && !string.IsNullOrWhiteSpace(line.CitizenId)
                && byCitizen.TryGetValue(DigitsOnly(line.CitizenId), out var e2)) emp = e2;
            if (emp == null)
                throw new InvalidOperationException(
                    $"บรรทัด {idx} ({line.EmployeeName ?? line.EmployeeExternalId ?? line.CitizenId}): " +
                    "หาพนักงานในระบบไม่เจอ — sync employee (ExternalId/CitizenId) ก่อน import");

            // gross − หักฝั่งลูกจ้าง == net. **สำคัญ:** SSO/PVD ฝั่งนายจ้างเป็น
            // ค่าใช้จ่ายของบริษัท ไม่หักจาก net ของลูกจ้าง — ถ้านับรวมจะทำให้
            // GL ไม่ balance ตอน pay (Dr salary+SSO-er ≠ Cr payable+WHT+cash).
            // validate ที่นี่เพื่อ reject 422 ทันที (แทนที่จะ fail cryptic ตอน pay).
            var empDeductions = line.SocialSecurityEmployee + line.WithholdingTax
                + line.ProvidentFundEmployee + line.SalaryAdvance + line.OtherDeductions;
            var expectedNet = Math.Round(line.GrossIncome - empDeductions, 2);
            if (Math.Abs(expectedNet - line.NetPay) > 0.01m)
                throw new InvalidOperationException(
                    $"บรรทัด {idx} ({emp.FirstNameTh} {emp.LastNameTh}): netPay ไม่ตรง — " +
                    $"net ต้อง = gross − (หักฝั่งลูกจ้าง: ปกส.ลูกจ้าง + WHT + PVD ลูกจ้าง + เบิกล่วงหน้า + อื่นๆ). " +
                    $"คำนวณได้ {line.GrossIncome:N2} − {empDeductions:N2} = {expectedNet:N2} แต่ส่ง netPay {line.NetPay:N2}. " +
                    $"หมายเหตุ: ปกส./PVD ฝั่งนายจ้าง ห้ามนำมาหักจาก net (เป็นค่าใช้จ่ายบริษัท ลง GL แยก)");

            // ตรวจ account code (ถ้าส่งมา) resolve ได้
            foreach (var code in new[] { line.SalaryExpenseAccountCode, line.PaymentAccountCode })
            {
                if (!string.IsNullOrWhiteSpace(code)
                    && !await _db.ChartOfAccounts.AnyAsync(a => a.CompanyId == companyId
                        && a.AccountCode == code && a.IsActive && !a.IsDeleted))
                    throw new InvalidOperationException($"บรรทัด {idx}: ผังบัญชี '{code}' ไม่มีในระบบหรือถูกปิดใช้");
            }
            resolved.Add((line, emp));
        }

        // ── สร้าง run + details ──
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
            Status = "Calculated",                 // ข้าม calculate — ใช้ยอดที่ส่งมา
            CreatedBy = createdBy,
            IsExternalImport = true,
            ExternalSystem = request.ExternalSystem,
            ExternalRunRef = request.ExternalRunRef,
            // account override ระดับ run — ใช้ของ line แรกที่ส่งมา (ปกติทุก line
            // ใช้บัญชีเดียวกัน). post GL จะ prefer ค่านี้ถ้า set.
            SalaryExpenseAccountCode = resolved.Select(r => r.Line.SalaryExpenseAccountCode)
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
            NetPaymentAccountCode = resolved.Select(r => r.Line.PaymentAccountCode)
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
        };

        foreach (var (line, emp) in resolved)
        {
            run.Details.Add(new PayrollDetail
            {
                CompanyId = companyId,
                EmployeeId = emp.Id,
                BaseSalary = line.BaseSalary,
                OvertimePay = line.OvertimePay,
                Allowances = line.Allowances,
                Commission = line.Commission,
                Bonus = line.Bonus,
                OtherIncome = line.OtherEarnings,
                GrossIncome = line.GrossIncome,
                TaxableGross = line.TaxableGross ?? line.GrossIncome,
                SocialSecurityEmployee = line.SocialSecurityEmployee,
                SocialSecurityEmployer = line.SocialSecurityEmployer,
                WithholdingTax = line.WithholdingTax,
                ProvidentFundEmployee = line.ProvidentFundEmployee,
                ProvidentFundEmployer = line.ProvidentFundEmployer,
                OtherDeductions = line.OtherDeductions + line.SalaryAdvance,
                TotalDeductions = line.TotalDeductions,
                NetPay = line.NetPay,
                // แหล่งจ่ายรายคน — เก็บ per-line เพื่อ split Cr เงินสด/ธนาคารตอน Pay
                NetPaymentAccountCode = string.IsNullOrWhiteSpace(line.PaymentAccountCode)
                    ? null : line.PaymentAccountCode,
            });
        }

        // totals = ผลรวมยอดที่ส่งมา (ไม่คำนวณใหม่)
        run.TotalGrossSalary = resolved.Sum(r => r.Line.GrossIncome);
        run.TotalWithholdingTax = resolved.Sum(r => r.Line.WithholdingTax);
        run.TotalSocialSecurityEmployee = resolved.Sum(r => r.Line.SocialSecurityEmployee);
        run.TotalSocialSecurityEmployer = resolved.Sum(r => r.Line.SocialSecurityEmployer);
        run.TotalProvidentFundEmployee = resolved.Sum(r => r.Line.ProvidentFundEmployee);
        run.TotalProvidentFundEmployer = resolved.Sum(r => r.Line.ProvidentFundEmployer);
        run.TotalNetPay = resolved.Sum(r => r.Line.NetPay);
        run.TotalDeductions = resolved.Sum(r => r.Line.TotalDeductions);
        run.EmployeeCount = resolved.Count;

        _db.Set<PayrollRun>().Add(run);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(request.ExternalRunRef))
        {
            // race: 2 import พร้อมกัน ExternalRunRef เดียวกัน → unique index ชน.
            // คืน run ที่อีก request สร้างไว้.
            var existing = await _db.Set<PayrollRun>().AsNoTracking()
                .FirstOrDefaultAsync(r => r.CompanyId == companyId
                    && r.ExternalRunRef == request.ExternalRunRef && !r.IsDeleted);
            if (existing != null)
                return ToImportResult(existing, wasExisting: true,
                    new List<string> { "ExternalRunRef ซ้ำ (race) — คืน run ที่สร้างไว้แล้ว" });
            throw;
        }

        _logger?.LogInformation(
            "Imported payroll run {Num} from {Sys} ({Ref}): {Count} emp, gross {Gross}, net {Net}",
            run.PayrollNumber, request.ExternalSystem, request.ExternalRunRef,
            run.EmployeeCount, run.TotalGrossSalary, run.TotalNetPay);

        return ToImportResult(run, wasExisting: false, warnings);
    }

    private static ImportPayrollRunResult ToImportResult(PayrollRun run, bool wasExisting, List<string> warnings)
        => new(run.Id, run.PayrollNumber, run.Status,
            run.TotalGrossSalary, run.TotalWithholdingTax,
            run.TotalSocialSecurityEmployee, run.TotalSocialSecurityEmployer,
            run.TotalNetPay, run.EmployeeCount, run.JournalEntryId, wasExisting, warnings);

    public async Task<PayrollRunResponse> GetPayrollRunAsync(Guid companyId, Guid payrollRunId)
    {
        var run = await _db.Set<PayrollRun>()
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId && !r.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        // เติมรายการรายคน (ทั้ง run ที่สร้างในระบบและ import จากระบบนอก) เพื่อให้
        // หน้าจอ run detail แสดงตารางรายคน + ปุ่มสลิป/50ทวิ ได้
        var details = await _db.Set<PayrollDetail>().AsNoTracking()
            .Where(d => d.PayrollRunId == run.Id)
            .ToListAsync();
        List<PayrollRunLineDto>? lines = null;
        if (details.Count > 0)
        {
            var empIds = details.Select(d => d.EmployeeId).Distinct().ToList();
            var emps = await _db.Set<Employee>().AsNoTracking()
                .Where(e => e.CompanyId == companyId && empIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id);
            lines = details.Select(d =>
            {
                emps.TryGetValue(d.EmployeeId, out var e);
                var name = e != null ? $"{e.FirstNameTh} {e.LastNameTh}".Trim() : "(ไม่พบพนักงาน)";
                return new PayrollRunLineDto(
                    d.EmployeeId, string.IsNullOrWhiteSpace(name) ? "(ไม่ระบุชื่อ)" : name,
                    e?.EmployeeCode,
                    d.BaseSalary, d.OvertimePay, d.Allowances, d.Commission, d.Bonus, d.OtherIncome,
                    d.GrossIncome,
                    d.SocialSecurityEmployee, d.SocialSecurityEmployer, d.WithholdingTax,
                    d.ProvidentFundEmployee, d.LoanDeduction, d.OtherDeductions,
                    d.TotalDeductions, d.NetPay,
                    d.NetPaymentAccountCode);
            }).ToList();
        }

        return MapToPayrollRunResponse(run) with { Details = lines };
    }

    public async Task<PayrollRunResponse> SetEmployeePaymentAccountAsync(
        Guid companyId, Guid payrollRunId, Guid employeeId, string? accountCode)
    {
        var run = await _db.Set<PayrollRun>()
            .Include(r => r.Details)
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId && !r.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        // แก้แหล่งจ่ายได้เฉพาะก่อนจ่าย — Paid แล้ว JE ออกไปแล้ว ห้ามแก้ย้อนหลัง
        if (run.Status != "Calculated" && run.Status != "Approved")
            throw new InvalidOperationException(
                "แก้แหล่งจ่ายได้เฉพาะรอบที่ยังไม่จ่าย (Calculated/Approved) เท่านั้น");

        var detail = run.Details.FirstOrDefault(d => d.EmployeeId == employeeId)
            ?? throw new KeyNotFoundException("ไม่พบพนักงานในรอบนี้");

        var code = string.IsNullOrWhiteSpace(accountCode) ? null : accountCode.Trim();
        if (code != null)
        {
            // validate: ต้องเป็นผังเงินสด/ธนาคาร/ช่องจ่าย (111x/1133/2123) ของบริษัทนี้
            var ok = await _db.ChartOfAccounts.AnyAsync(a => a.CompanyId == companyId
                && a.AccountCode == code && a.IsActive && !a.IsDeleted && a.Level >= 4
                && (a.AccountCode.StartsWith("111") || a.AccountCode.StartsWith("1133")
                    || a.AccountCode.StartsWith("2123")));
            if (!ok)
                throw new InvalidOperationException($"ผังบัญชีแหล่งจ่าย '{code}' ไม่ถูกต้อง (ต้องเป็นเงินสด/ธนาคาร/ช่องจ่าย)");
        }

        detail.NetPaymentAccountCode = code;
        detail.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await GetPayrollRunAsync(companyId, payrollRunId);
    }

    public async Task<PayrollRunResponse> UpdatePayrollDetailAsync(Guid companyId,
        Guid payrollRunId, Guid employeeId, UpdatePayrollDetailRequest req, string updatedBy)
    {
        var run = await _db.Set<PayrollRun>()
            .Include(r => r.Details)
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId && !r.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        // แก้ยอดได้เฉพาะก่อนจ่าย — Paid แล้ว JE ออกไปแล้ว ต้อง void ก่อนถึงแก้
        if (run.Status != "Calculated" && run.Status != "Approved")
            throw new InvalidOperationException(
                "แก้ยอดได้เฉพาะรอบที่ยังไม่จ่าย (Calculated/Approved) — ถ้าจ่ายแล้วต้อง void ก่อน");

        var d = run.Details.FirstOrDefault(x => x.EmployeeId == employeeId)
            ?? throw new KeyNotFoundException("ไม่พบพนักงานในรอบนี้");

        static decimal Pos(decimal v) => v < 0 ? 0 : v;
        if (req.BaseSalary.HasValue) d.BaseSalary = Pos(req.BaseSalary.Value);
        if (req.OvertimePay.HasValue) d.OvertimePay = Pos(req.OvertimePay.Value);
        if (req.Allowances.HasValue) d.Allowances = Pos(req.Allowances.Value);
        if (req.Commission.HasValue) d.Commission = Pos(req.Commission.Value);
        if (req.Bonus.HasValue) d.Bonus = Pos(req.Bonus.Value);
        if (req.OtherIncome.HasValue) d.OtherIncome = Pos(req.OtherIncome.Value);
        if (req.SocialSecurityEmployee.HasValue) d.SocialSecurityEmployee = Pos(req.SocialSecurityEmployee.Value);
        if (req.SocialSecurityEmployer.HasValue) d.SocialSecurityEmployer = Pos(req.SocialSecurityEmployer.Value);
        if (req.WithholdingTax.HasValue) d.WithholdingTax = Pos(req.WithholdingTax.Value);
        if (req.ProvidentFundEmployee.HasValue) d.ProvidentFundEmployee = Pos(req.ProvidentFundEmployee.Value);
        if (req.LoanDeduction.HasValue) d.LoanDeduction = Pos(req.LoanDeduction.Value);
        if (req.OtherDeductions.HasValue) d.OtherDeductions = Pos(req.OtherDeductions.Value);

        // รวมยอดใหม่ — หักฝั่งลูกจ้างเท่านั้นที่กระทบ net (ปกส./PVD นายจ้าง = cost บริษัท)
        d.GrossIncome = d.BaseSalary + d.OvertimePay + d.Allowances + d.Commission + d.Bonus + d.OtherIncome;
        d.TaxableGross = d.GrossIncome;
        d.TotalDeductions = d.SocialSecurityEmployee + d.WithholdingTax + d.ProvidentFundEmployee
            + d.LoanDeduction + d.OtherDeductions;
        d.NetPay = d.GrossIncome - d.TotalDeductions;
        if (d.NetPay < 0)
            throw new InvalidOperationException(
                $"ยอดสุทธิติดลบ ({d.NetPay:N2}) — รายการหักรวมมากกว่ารายได้ ตรวจสอบยอดอีกครั้ง");
        d.UpdatedAt = DateTime.UtcNow;

        // รวม run totals ใหม่จาก details ทั้งหมด
        run.TotalGrossSalary = run.Details.Sum(x => x.GrossIncome);
        run.TotalWithholdingTax = run.Details.Sum(x => x.WithholdingTax);
        run.TotalSocialSecurityEmployee = run.Details.Sum(x => x.SocialSecurityEmployee);
        run.TotalSocialSecurityEmployer = run.Details.Sum(x => x.SocialSecurityEmployer);
        run.TotalProvidentFundEmployee = run.Details.Sum(x => x.ProvidentFundEmployee);
        run.TotalProvidentFundEmployer = run.Details.Sum(x => x.ProvidentFundEmployer);
        run.TotalNetPay = run.Details.Sum(x => x.NetPay);
        run.TotalDeductions = run.Details.Sum(x => x.TotalDeductions);
        run.UpdatedBy = updatedBy;
        run.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger?.LogInformation("แก้ยอด payroll detail run {Run} emp {Emp} โดย {By} → net {Net}",
            payrollRunId, employeeId, updatedBy, d.NetPay);
        return await GetPayrollRunAsync(companyId, payrollRunId);
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
        // First read just enough to confirm the run exists + belongs to the
        // tenant (cheap check, no FOR UPDATE). The serialised re-read happens
        // INSIDE the transaction below — necessary to avoid the race where two
        // HR clicks pass the Draft check then both recompute, the second
        // overwriting totals or hitting a duplicate-detail insert.
        _ = await _db.Set<PayrollRun>()
            .AsNoTracking()
            .Where(r => r.Id == payrollRunId && r.CompanyId == companyId)
            .Select(r => r.Id)
            .FirstOrDefaultAsync()
            != Guid.Empty ? true : throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Row-level lock on the payroll run for the rest of this
            // transaction. Mirrors DocumentService.ApproveDocumentAsync's
            // FOR UPDATE pattern. A concurrent Calculate will wait here and
            // either see the run already Calculated (and reject below) or
            // proceed serially.
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"PayrollRuns\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE",
                payrollRunId, companyId);

            var run = await _db.Set<PayrollRun>()
                .Include(r => r.Details)
                .FirstAsync(r => r.Id == payrollRunId && r.CompanyId == companyId);

            if (run.Status != "Draft")
                throw new InvalidOperationException(
                    "สามารถคำนวณได้เฉพาะรอบที่เป็น Draft เท่านั้น — รอบนี้ถูกคำนวณ/อนุมัติไปแล้วโดยผู้ใช้งานคนอื่น");

            // Remove existing details
            _db.Set<PayrollDetail>().RemoveRange(run.Details);

            // Get active employees
            var employees = await _db.Set<Employee>()
                .Where(e => e.CompanyId == companyId && e.IsActive && !e.IsDeleted
                    && e.StartDate <= run.PeriodEnd
                    && (e.EndDate == null || e.EndDate >= run.PeriodStart))
                .ToListAsync();

            // CompanySettings — ใช้ในการคำนวณกองทุนเงินทดแทน (กท.20ก)
            // โหลด 1 ครั้งก่อน loop เพื่อกัน N+1
            var companySettings = await _db.Set<CompanySettings>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.CompanyId == companyId && !c.IsDeleted);

            // Get payroll items for earnings/deductions calculation
            var payrollItems = await _db.Set<PayrollItem>()
                .Where(i => i.CompanyId == companyId && i.IsActive && !i.IsDeleted)
                .ToListAsync();

            var earningItems = payrollItems.Where(i => i.ItemType == "Earning").ToList();
            var deductionItems = payrollItems.Where(i => i.ItemType == "Deduction").ToList();

            // Batch-load per-employee lookups that previously ran one query per
            // employee inside the loop — for a 100-person payroll that turned a
            // single Calculate click into 200+ round-trips. We pre-fetch both
            // the YTD PayrollDetails (prior months of the same fiscal year) and
            // every approved leave overlapping this period, then materialise
            // per-employee views in memory.
            var employeeIds = employees.Select(e => e.Id).ToList();
            var priorDetailsAll = await _db.Set<PayrollDetail>()
                .Include(d => d.PayrollRun)
                .Where(d => employeeIds.Contains(d.EmployeeId)
                    && d.PayrollRun.CompanyId == companyId
                    && d.PayrollRun.Year == run.Year
                    && d.PayrollRun.Month < run.Month
                    && d.PayrollRun.Status != "Voided")
                .AsNoTracking()
                .ToListAsync();
            var priorDetailsByEmployee = priorDetailsAll.GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

            var approvedLeavesAll = await _db.Set<EmployeeLeave>()
                .Where(l => employeeIds.Contains(l.EmployeeId)
                    && l.CompanyId == companyId
                    && l.Status == "Approved"
                    && l.StartDate <= run.PeriodEnd && l.EndDate >= run.PeriodStart)
                .AsNoTracking()
                .ToListAsync();
            var leavesByEmployee = approvedLeavesAll.GroupBy(l => l.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

            // ===== Batch-load attendance + compensation profile =====
            // EmployeeProjectTime rows in the run period drive OT/per-diem/
            // accommodation/OT-meal extras. CompanyCompensationDefaults +
            // per-employee profiles override the rates. Both pre-fetched
            // once so the inner loop stays O(employees). employeeIds is
            // already in scope from the prior-details / leaves prefetch.
            var timeRowsAll = await _db.EmployeeProjectTimes
                .Where(t => t.CompanyId == companyId
                    && employeeIds.Contains(t.EmployeeId)
                    && t.WorkDate >= run.PeriodStart.Date && t.WorkDate <= run.PeriodEnd.Date
                    && !t.IsDeleted)
                .AsNoTracking()
                .ToListAsync();
            var timeByEmployee = timeRowsAll.GroupBy(t => t.EmployeeId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var compDefaults = await _db.CompanyCompensationDefaults
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyId == companyId && !x.IsDeleted)
                ?? new CompanyCompensationDefaults { CompanyId = companyId };
            var compProfiles = await _db.EmployeeCompensationProfiles
                .Where(p => p.CompanyId == companyId && employeeIds.Contains(p.EmployeeId) && !p.IsDeleted)
                .AsNoTracking()
                .ToDictionaryAsync(p => p.EmployeeId);

            decimal totalGross = 0, totalDeductions = 0, totalNet = 0;
            decimal totalWht = 0, totalSsoEmp = 0, totalSsoEr = 0;
            decimal totalWc = 0;   // กองทุนเงินทดแทน (กท.20ก)
            decimal totalPvdEmp = 0, totalPvdEr = 0;

            // SSO parameters effective for THIS run's year — the wage ceiling
            // steps up by royal decree (15,000 → 17,500 in 2026 → 20,000 in
            // 2029 → 23,000 in 2032) and a company can override per year.
            var sso = await GetSsoParamsAsync(companyId, run.Year);

            foreach (var emp in employees)
            {
                // Get cumulative income for this year (prior months) — sourced
                // from the batched lookup above; falls back to empty list when
                // there are no prior runs.
                var priorDetails = priorDetailsByEmployee.TryGetValue(emp.Id, out var pd) ? pd : new List<PayrollDetail>();

                var cumulativeIncome = priorDetails.Sum(d => d.GrossIncome);
                var cumulativeTax = priorDetails.Sum(d => d.WithholdingTax);

                // Calculate earnings from PayrollItems
                var overtimePay = 0m;
                var allowances = 0m;
                var commission = 0m;
                var bonus = 0m;
                var otherIncome = 0m;
                // Tax-exempt income (สวัสดิการรักษาพยาบาล ฯลฯ ที่ติ๊ก
                // "ไม่หัก WHT") — รวมใน net pay จ่ายให้พนักงาน แต่ไม่รวม
                // ฐานคำนวณภาษี (estimatedAnnualIncome below subtracts it).
                var nonTaxableExtra = 0m;

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

                // ===== Attendance-driven extras (OT + per-diem + accom + OT-meal) =====
                // Read EmployeeProjectTime rows for this employee in the run
                // period and apply the merged per-employee → company-defaults
                // compensation rates. When no attendance rows exist, the
                // calculation yields zero and behaviour matches the pre-
                // attendance world — companies without clock-in integration
                // are unaffected.
                if (timeByEmployee.TryGetValue(emp.Id, out var empTimeRows) && empTimeRows.Count > 0)
                {
                    var prof = compProfiles.GetValueOrDefault(emp.Id);
                    var otMultWeekday = prof?.OvertimeRateMultiplierWeekday ?? compDefaults.OvertimeRateMultiplierWeekday;
                    var otMultHoliday = prof?.OvertimeRateMultiplierHoliday ?? compDefaults.OvertimeRateMultiplierHoliday;
                    var perDiemRate = prof?.PerDiemRate ?? compDefaults.PerDiemRate;
                    var accomRate = prof?.AccommodationAllowance ?? compDefaults.AccommodationAllowance;
                    var otMealRate = prof?.OvertimeMealAllowance ?? compDefaults.OvertimeMealAllowance;

                    // Hourly base — Monthly: salary / (workDays × workHours);
                    // Daily: salary / workHours; Hourly: salary is the rate.
                    decimal hourly = emp.SalaryType switch
                    {
                        "Hourly" => emp.BaseSalary,
                        "Daily" => compDefaults.StandardWorkHoursPerDay > 0
                            ? emp.BaseSalary / compDefaults.StandardWorkHoursPerDay : 0,
                        _ => (compDefaults.StandardWorkDaysPerMonth * compDefaults.StandardWorkHoursPerDay) > 0
                            ? emp.BaseSalary / (compDefaults.StandardWorkDaysPerMonth * compDefaults.StandardWorkHoursPerDay) : 0,
                    };

                    decimal otPayWeekday = 0, otPayHoliday = 0;
                    decimal perDiemSum = 0, accomSum = 0, otMealSum = 0, dailyMealSum = 0;
                    int workDays = 0;   // for daily-meal + Daily-type custom allowances
                    foreach (var grp in empTimeRows.GroupBy(t => t.WorkDate.Date))
                    {
                        var dayOt = grp.Sum(t => t.OvertimeHours ?? 0);
                        var dayRegular = grp.Sum(t => t.Hours) - dayOt;
                        var dayIsHoliday = grp.Any(t => t.IsHoliday);
                        if (dayIsHoliday) otPayHoliday += dayOt * hourly * otMultHoliday;
                        else otPayWeekday += dayOt * hourly * otMultWeekday;
                        if (grp.Any(t => t.HasPerDiem)) perDiemSum += perDiemRate;
                        if (grp.Any(t => t.HasAccommodation)) accomSum += accomRate;
                        if (grp.Any(t => t.HasOvertimeMeal)) otMealSum += otMealRate;
                        // Daily meal: นับวันที่มีชั่วโมงทำงานปกติ (ไม่ใช่
                        // เฉพาะ OT) — แยกจาก OvertimeMealAllowance ที่แจ้ง
                        // เฉพาะวัน OT.
                        if (!dayIsHoliday && dayRegular > 0)
                        {
                            workDays++;
                            dailyMealSum += compDefaults.DailyMealAllowance;
                        }
                    }
                    overtimePay += Math.Round(otPayWeekday + otPayHoliday, 2, MidpointRounding.AwayFromZero);
                    allowances += Math.Round(perDiemSum + accomSum + otMealSum + dailyMealSum, 2, MidpointRounding.AwayFromZero);

                    // Custom allowances ที่บริษัทตั้งเองในตาราง — Monthly =
                    // จ่ายเต็มจำนวนต่อรอบ, Daily = คูณวันทำงานจริง. isTaxable
                    // = false เก็บไว้ใน otherIncome แยกแล้ว NOT รวมในฐาน WHT
                    // (skipped from estimatedAnnualIncome below).
                    if (!string.IsNullOrWhiteSpace(compDefaults.CustomAllowancesJson))
                    {
                        try
                        {
                            var customs = System.Text.Json.JsonSerializer
                                .Deserialize<List<CustomAllowanceItem>>(compDefaults.CustomAllowancesJson) ?? new();
                            foreach (var c in customs)
                            {
                                if (c.Amount <= 0) continue;
                                var amt = string.Equals(c.Type, "Daily", StringComparison.OrdinalIgnoreCase)
                                    ? c.Amount * workDays
                                    : c.Amount;
                                if (c.IsTaxable) allowances += Math.Round(amt, 2, MidpointRounding.AwayFromZero);
                                else nonTaxableExtra += Math.Round(amt, 2, MidpointRounding.AwayFromZero);
                            }
                        }
                        catch { /* malformed JSON — skip silently */ }
                    }
                }

                // Calculate leave deductions — sourced from the batched lookup.
                var approvedLeaves = leavesByEmployee.TryGetValue(emp.Id, out var lv) ? lv : new List<EmployeeLeave>();

                // Bound the displayed LeaveDays to this payroll period — old code
                // showed total leave days regardless of overlap, so a one-week leave
                // crossing month-end inflated next month's report too.
                var leaveDays = approvedLeaves.Sum(l => DaysInPeriod(l, run.PeriodStart, run.PeriodEnd));
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

                // Deduct unpaid leave from base salary.
                //
                // Thai Labor Code §41 + SSO Act §67: maternity is 98 days/yr;
                // employer pays full salary for the FIRST 45 days; the next 45
                // are reimbursed by SSO (50% of insured wage) — not the employer;
                // anything beyond 90 is unpaid by employer. The previous code
                // paid the full 98 days as if it were a paid leave type, which
                // over-paid the employer's share by up to 53 calendar days.
                //
                // We compute, for each Maternity leave overlapping this month:
                //   employerPaid = clamp(45 - daysAlreadyConsumed, 0, daysThisMonth)
                //   sso/unpaid   = daysThisMonth − employerPaid
                // The sso/unpaid portion is added to the unpaid-leave bucket so
                // the salary is pro-rated down accordingly.
                var unpaidLeaveDays = approvedLeaves
                    .Where(l => l.LeaveType == "UnpaidLeave" || l.LeaveType == "ลาไม่รับค่าจ้าง")
                    .Sum(l => DaysInPeriod(l, run.PeriodStart, run.PeriodEnd));

                var maternityLeaves = approvedLeaves
                    .Where(l => l.LeaveType == "Maternity" || l.LeaveType == "ลาคลอด");
                foreach (var ml in maternityLeaves)
                {
                    var daysBeforeMonth = Math.Max(0,
                        (decimal)Math.Min(45, (run.PeriodStart.AddDays(-1) - ml.StartDate).TotalDays + 1));
                    var daysThisMonth = DaysInPeriod(ml, run.PeriodStart, run.PeriodEnd);
                    var employerPaidThisMonth = Math.Max(0m, Math.Min(45m - daysBeforeMonth, daysThisMonth));
                    var unpaidEmployerThisMonth = daysThisMonth - employerPaidThisMonth;
                    if (unpaidEmployerThisMonth > 0) unpaidLeaveDays += unpaidEmployerThisMonth;
                }

                var leaveDeduction = workDaysInMonth > 0
                    ? Math.Round(emp.BaseSalary * unpaidLeaveDays / workDaysInMonth, 2, MidpointRounding.AwayFromZero)
                    : 0m;

                // taxableGross = ส่วนที่นำไปคำนวณ WHT (ตามประมวลรัษฎากร §40(1)).
                // grossIncome (จ่ายให้พนักงาน) = taxableGross + สวัสดิการยกเว้นภาษี.
                var taxableGross = emp.BaseSalary - leaveDeduction + overtimePay + allowances + commission + bonus + otherIncome;
                var grossIncome = taxableGross + nonTaxableExtra;

                // Social security: base on BaseSalary (not gross), capped at the
                // year's statutory wage ceiling (resolved above — 17,500 from
                // 2026, stepping up per the royal decree; override-able per year).
                // กฎหมาย ม.33: ฐานคำนวณ "ขั้นต่ำ 1,650 บาท ขั้นสูง = เพดานของปีนั้น"
                // — เงินเดือนต่ำกว่า 1,650 → ใช้ฐาน 1,650 (ไม่ใช่ skip),
                //   เพราะ ม.33 บังคับสมทบทุกคนที่อยู่ในระบบ (ลูกจ้าง <1,650
                //   หายาก ปกติเด็กฝึกงาน — แต่ฐานคงต้องตามกฎ).
                var ssoEmployee = 0m;
                var ssoEmployer = 0m;
                if (emp.IsSubjectToSocialSecurity)
                {
                    const decimal SsoMinBase = 1_650m;
                    var ssoBase = Math.Max(SsoMinBase, Math.Min(emp.BaseSalary, sso.MaxBase));
                    ssoEmployee = Math.Min(Math.Round(ssoBase * sso.Rate, 2), sso.MaxContribution);
                    ssoEmployer = Math.Min(Math.Round(ssoBase * sso.EmployerRate, 2), sso.EmployerMaxContribution);
                }

                // กองทุนเงินทดแทน (กท.20ก) — นายจ้างฝ่ายเดียว, อัตรา 0.2–1.0%
                // ตามประเภทกิจการ. ฐานต่อเดือน cap 20,000 (= 240,000/ปี ตาม
                // พ.ร.บ.เงินทดแทน §44 — ส่วนเกิน 240k/ปี ไม่นับสมทบ). default
                // ปิด → ระบบไม่คิดจนกว่าจะตั้งค่าใน Settings เพื่อกัน double-post
                // ในข้อมูลเดิม.
                var workersComp = 0m;
                if (companySettings?.WorkersCompensationEnabled == true
                    && emp.IsSubjectToSocialSecurity
                    && companySettings.WorkersCompensationRatePercent > 0)
                {
                    const decimal WcMonthlyBaseCap = 20_000m;   // 240,000/12
                    var wcBase = Math.Min(emp.BaseSalary, WcMonthlyBaseCap);
                    workersComp = Math.Round(wcBase * companySettings.WorkersCompensationRatePercent / 100m, 2);
                }

                // Provident fund calculation
                var pvdEmployee = 0m;
                var pvdEmployer = 0m;
                if (emp.HasProvidentFund)
                {
                    pvdEmployee = emp.BaseSalary * emp.ProvidentFundEmployeePercent / 100m;
                    pvdEmployer = emp.BaseSalary * emp.ProvidentFundEmployerPercent / 100m;
                }

                // Thai withholding tax: TRD-standard annualization = (YTD
                // including this month) * 12 / elapsed months. Annualise the
                // TAXABLE portion only — สวัสดิการยกเว้นภาษีถูกแยกไว้แล้วใน
                // nonTaxableExtra → ไม่กระทบฐาน WHT.
                var ytdIncome = cumulativeIncome + taxableGross;
                var estimatedAnnualIncome = run.Month > 0 ? ytdIncome * 12 / run.Month : ytdIncome * 12;

                // Apply Revenue Code §47 allowances before bracket lookup. Skipping
                // these used to over-withhold by 5–15 % depending on income tier —
                // employees ended up subsidising the company's cash flow until the
                // year-end true-up that this system doesn't yet automate.
                // Annual SSO deduction cap follows the year's ceiling too
                // (12 × monthly max — e.g. 10,500 from 2026, was 9,000).
                var annualSso = Math.Min(ssoEmployee * 12m, sso.MaxContribution * 12m);
                var annualPvd = Math.Min(pvdEmployee * 12m, PitPvdMaxDeductible);

                // §47/47ทวิ — รวมค่าลดหย่อนรายตัว. โหลด TaxRuleConfig
                // ของบริษัท × ปี (fallback เป็นค่า default ถ้าไม่มี config) —
                // ทำให้ admin ปรับเกณฑ์ได้เมื่อสรรพากรเปลี่ยน ไม่ต้อง deploy.
                var taxCfg = await GetTaxRuleAsync(companyId, run.Year);
                var personalAllow = taxCfg?.PersonalAllowance ?? PitPersonalAllowance;
                var spouseAllow = taxCfg?.SpouseAllowance ?? 60_000m;
                var childAllow = taxCfg?.ChildAllowance ?? 30_000m;
                var childPost2561Bonus = (taxCfg?.ChildAllowancePost2561 ?? 60_000m) - childAllow;
                var parentAllow = taxCfg?.ParentAllowance ?? 30_000m;
                var lifeInsCap = taxCfg?.LifeInsuranceCap ?? 100_000m;
                var pvdCap = taxCfg?.PvdCap ?? PitPvdMaxDeductible;
                var donationCapPct = (taxCfg?.DonationCapPercent ?? 10m) / 100m;
                var perDependantLegacy = PitPerDependantAllowance;

                var detailedAllowance =
                    (emp.HasSpouseAllowance ? spouseAllow : 0m)
                    + (emp.ChildAllowanceCount * childAllow)
                    + (emp.SecondAndLaterChildren * childPost2561Bonus)
                    + (Math.Min(4, emp.ParentAllowanceCount) * parentAllow)
                    + Math.Min(lifeInsCap, emp.LifeInsurancePremium)
                    + Math.Min(pvdCap, emp.RmfSsfContribution);
                var hasDetailed = emp.HasSpouseAllowance
                    || emp.ChildAllowanceCount > 0 || emp.SecondAndLaterChildren > 0
                    || emp.ParentAllowanceCount > 0 || emp.LifeInsurancePremium > 0
                    || emp.RmfSsfContribution > 0;
                var dependantsAllowance = hasDetailed
                    ? detailedAllowance
                    : perDependantLegacy * Math.Max(0, emp.TaxAllowances);
                var baseDeductions = personalAllow + dependantsAllowance + annualSso + annualPvd;
                // บริจาคหักได้ตาม donationCapPct ของเงินได้สุทธิหลังลดหย่อน
                var afterBase = Math.Max(0, estimatedAnnualIncome - baseDeductions);
                var donation = Math.Min(emp.DonationAmount, afterBase * donationCapPct);
                var personalDeductions = baseDeductions + donation;
                var estimatedTaxableIncome = Math.Max(0m, estimatedAnnualIncome - personalDeductions);

                var brackets = ParseBrackets(taxCfg);
                var estimatedAnnualTax = CalculateThaiIncomeTax(estimatedTaxableIncome, brackets);
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
                    // TaxableGross = ฐานรายได้ที่ใช้คำนวณ WHT (Gross −
                    // สวัสดิการยกเว้นภาษีเช่น ค่ารักษาพยาบาล). ใช้ใน
                    // ภ.ง.ด.1 e-Filing export + 50 ทวิ. ปัจจุบัน engine
                    // เดิมแยก nonTaxableExtra ไว้แล้ว — ใช้ค่านี้.
                    TaxableGross = taxableGross,
                    SocialSecurityEmployee = ssoEmployee,
                    SocialSecurityEmployer = ssoEmployer,
                    WorkersCompensation = workersComp,
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
                totalWc += workersComp;
                totalPvdEmp += pvdEmployee;
                totalPvdEr += pvdEmployer;
            }

            run.TotalGrossSalary = totalGross;
            run.TotalDeductions = totalDeductions;
            run.TotalNetPay = totalNet;
            run.TotalWithholdingTax = totalWht;
            run.TotalSocialSecurityEmployee = totalSsoEmp;
            run.TotalSocialSecurityEmployer = totalSsoEr;
            run.TotalWorkersCompensation = totalWc;
            run.TotalProvidentFundEmployee = totalPvdEmp;
            run.TotalProvidentFundEmployer = totalPvdEr;
            run.EmployeeCount = employees.Count;
            run.Status = "Calculated";

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            await NotifyRunAsync(companyId, NotificationEvents.PayrollGenerated, actorUserId: null,
                title: $"คำนวณรอบเงินเดือน {run.Month:D2}/{run.Year} เสร็จสิ้น",
                message: $"พนักงาน {run.EmployeeCount} คน · ยอดรวมจ่ายสุทธิ {run.TotalNetPay:N2} บาท",
                entityId: run.Id);

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
        // Row lock on the run for the duration of the approval — pair-protect
        // against a second concurrent click sliding through the status guard.
        await using var tx = await _db.Database.BeginTransactionAsync();
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"PayrollRuns\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE",
            payrollRunId, companyId);

        var run = await _db.Set<PayrollRun>()
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        if (run.Status != "Calculated")
            throw new InvalidOperationException(
                "สามารถอนุมัติได้เฉพาะรอบที่คำนวณแล้วเท่านั้น — รอบนี้อาจถูกอนุมัติไปแล้วโดยผู้ใช้งานคนอื่น");

        run.Status = "Approved";
        run.ApprovedBy = approvedBy;
        run.ApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await NotifyRunAsync(companyId, NotificationEvents.PayrollApproved, actorUserId: null,
            title: $"อนุมัติรอบเงินเดือน {run.Month:D2}/{run.Year}",
            message: $"อนุมัติโดย {approvedBy} · ยอดรวมจ่ายสุทธิ {run.TotalNetPay:N2} บาท",
            entityId: run.Id);

        return MapToPayrollRunResponse(run);
    }

    public async Task<PayrollRunResponse> ProcessPaymentAsync(Guid companyId, Guid payrollRunId, string processedBy)
    {
        // Quick existence check before the long-running pay transaction. The
        // FOR UPDATE lock is taken inside payTransaction below so a concurrent
        // Pay click waits and re-reads under the lock.
        if (!await _db.Set<PayrollRun>().AnyAsync(r => r.Id == payrollRunId && r.CompanyId == companyId))
            throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        var run = await _db.Set<PayrollRun>()
            .Include(r => r.Details).ThenInclude(d => d.Employee).ThenInclude(e => e.DepartmentRef)
            .FirstAsync(r => r.Id == payrollRunId && r.CompanyId == companyId);

        if (run.Status != "Approved")
            throw new InvalidOperationException("สามารถจ่ายได้เฉพาะรอบที่อนุมัติแล้วเท่านั้น");

        // Fiscal-period guard: refuse to post into a period that's already
        // closed by Accounting (DocumentService.cs:1041-1043 does the same
        // for documents). Otherwise HR clicks Pay → run goes to Paid state,
        // then AccountingService.CreateJournalEntryAsync throws because the
        // period is closed → user is left with a stale "Paid" record + no JE.
        var payDate = run.PayDate;
        var fp = await _db.FiscalPeriods.AsNoTracking()
            .Where(p => p.CompanyId == companyId
                && p.StartDate <= payDate && p.EndDate >= payDate)
            .Select(p => new { p.Status, p.Name })
            .FirstOrDefaultAsync();
        if (fp != null && fp.Status == FiscalPeriodStatus.Closed)
            throw new InvalidOperationException(
                $"งวดบัญชี \"{fp.Name}\" ปิดแล้ว — ไม่สามารถจ่ายเงินเดือนเข้างวดนี้ได้ กรุณาเปิดงวดก่อน หรือเปลี่ยน PayDate ให้อยู่ในงวดที่เปิด");

        // Prevent duplicate payments for same year/month
        var alreadyPaid = await _db.Set<PayrollRun>()
            .AnyAsync(r => r.CompanyId == companyId && r.Year == run.Year
                && r.Month == run.Month && r.Status == "Paid" && r.Id != payrollRunId && !r.IsDeleted);
        if (alreadyPaid)
            throw new InvalidOperationException($"รอบจ่ายเงินเดือน {run.Year}/{run.Month:D2} ถูกจ่ายไปแล้ว");

        var clearedAdvances = new List<SalaryAdvance>();
        await using var payTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Row lock + re-read under the lock — a concurrent /pay click
            // would otherwise pass the "Approved" check and double-post.
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"PayrollRuns\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE",
                payrollRunId, companyId);
            var lockedStatus = await _db.Set<PayrollRun>()
                .Where(r => r.Id == payrollRunId && r.CompanyId == companyId)
                .Select(r => r.Status)
                .FirstAsync();
            if (lockedStatus != "Approved")
                throw new InvalidOperationException(
                    "รอบนี้ถูกประมวลผลไปแล้วโดยผู้ใช้งานคนอื่น — กรุณารีเฟรชหน้านี้");

            run.Status = "Paid";
            run.UpdatedBy = processedBy;
            run.UpdatedAt = DateTime.UtcNow;

            if (_accountingService != null && run.TotalGrossSalary > 0)
            {
                try
                {
                var lines = new List<Models.DTOs.Accounting.JournalLineRequest>();

                // Dr: เงินเดือนและค่าจ้าง (541 - ค่าใช้จ่ายบุคลากร).
                // Split the salary debit by department cost-centre so each
                // department's expense lands on its own dimension on the
                // GL — prefer Employee.DepartmentRef.DimensionId (the new
                // org-structure linkage) and fall back to the employee's
                // own DimensionId, then to null. Companies that haven't
                // configured departments still produce a single line.
                // External import override → prefer run.SalaryExpenseAccountCode
                var salaryAccount = (!string.IsNullOrWhiteSpace(run.SalaryExpenseAccountCode)
                        ? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                            a.CompanyId == companyId && a.AccountCode == run.SalaryExpenseAccountCode
                            && a.IsActive && !a.IsDeleted)
                        : null)
                    ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode == "54111" && a.Level >= 4)
                    ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode.StartsWith("541") && a.Level >= 4);
                if (salaryAccount != null)
                {
                    var byDim = run.Details
                        .GroupBy(d => d.Employee.DepartmentRef?.DimensionId ?? d.Employee.DimensionId)
                        .Select(g => new { DimensionId = g.Key, Total = g.Sum(d => d.GrossIncome) })
                        .Where(x => x.Total > 0)
                        .ToList();
                    if (byDim.Count <= 1)
                    {
                        var single = byDim.FirstOrDefault();
                        lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                            salaryAccount.Id, run.TotalGrossSalary, 0,
                            $"เงินเดือน {run.Month}/{run.Year}",
                            DimensionId: single?.DimensionId));
                    }
                    else
                    {
                        foreach (var grp in byDim)
                            lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                                salaryAccount.Id, grp.Total, 0,
                                $"เงินเดือน {run.Month}/{run.Year}" + (grp.DimensionId.HasValue ? "" : " (ไม่ระบุ cost center)"),
                                DimensionId: grp.DimensionId));
                    }
                }

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

                // ── กองทุนเงินทดแทน (กท.20ก) — Dr ค่าใช้จ่าย + Cr ค้างจ่าย ──
                // นายจ้างฝ่ายเดียว 0.2–1.0% เปิดเมื่อ CompanySettings.
                // WorkersCompensationEnabled = true. ใช้คนละผัง SSO เพราะ
                // ยื่นแยกแบบ + รอบยื่นต่างกัน (สปส.1-10 รายเดือน, กท.20ก รายปี)
                if (run.TotalWorkersCompensation > 0)
                {
                    var wcExpAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode == "54121" && a.Level >= 4)
                        ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode.StartsWith("541") && a.Level >= 4
                        && a.AccountName.Contains("เงินทดแทน"));
                    if (wcExpAccount != null)
                        lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                            wcExpAccount.Id, run.TotalWorkersCompensation, 0, "กองทุนเงินทดแทน (นายจ้าง)"));
                    var wcPayableAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode == "21816" && a.Level >= 4)
                        ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                            a.CompanyId == companyId && a.AccountCode.StartsWith("218") && a.Level >= 4
                            && a.AccountName.Contains("เงินทดแทน"));
                    if (wcPayableAccount != null)
                        lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                            wcPayableAccount.Id, 0, run.TotalWorkersCompensation, "กองทุนเงินทดแทนค้างจ่าย"));
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
                        decimal detailRecovered = 0m;
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
                            detailRecovered += recover;
                            employeeRecoverable -= recover;
                        }
                        // Stash the per-employee recovery so VoidPayrollAsync
                        // can restore SalaryAdvance.OutstandingAmount exactly.
                        detail.AdvanceRecovered = detailRecovered;
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

                // Cr: เงินสด/ธนาคาร (111) — เงินเดือนสุทธิ หักเงินทดรองที่เรียกคืน.
                // แหล่งจ่ายรายคน: group ยอดสุทธิตาม PayrollDetail.NetPaymentAccountCode
                // (fallback → run.NetPaymentAccountCode → default 11122/111x) →
                // จ่ายแต่ละคนจากบัญชีของตัวเองได้ (ลง Cr หลายบรรทัดตามบัญชี).
                var cashPaid = run.TotalNetPay - totalAdvanceRecovered;
                var defaultCashAccount = (!string.IsNullOrWhiteSpace(run.NetPaymentAccountCode)
                        ? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                            a.CompanyId == companyId && a.AccountCode == run.NetPaymentAccountCode
                            && a.IsActive && !a.IsDeleted)
                        : null)
                    ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode == "11122" && a.Level >= 4)
                    ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.Level >= 4);
                if (defaultCashAccount != null)
                {
                    // resolve โค้ดแหล่งจ่ายรายคนทั้งหมด (cache ต่อโค้ด) — เฉพาะผังที่ใช้ได้
                    var codeToAccount = new Dictionary<string, ChartOfAccount>(StringComparer.OrdinalIgnoreCase);
                    foreach (var code in run.Details
                        .Select(d => d.NetPaymentAccountCode)
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var acc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                            a.CompanyId == companyId && a.AccountCode == code
                            && a.IsActive && !a.IsDeleted && a.Level >= 4);
                        if (acc != null) codeToAccount[code!] = acc;
                    }
                    // จับยอดสุทธิรายคน (หลังหักเงินทดรอง) เข้าบัญชีจ่ายของแต่ละคน
                    var byPayAccount = new Dictionary<Guid, (ChartOfAccount Acc, decimal Amt)>();
                    foreach (var d in run.Details)
                    {
                        var net = d.NetPay - d.AdvanceRecovered;
                        if (net == 0) continue;
                        var acc = (!string.IsNullOrWhiteSpace(d.NetPaymentAccountCode)
                                    && codeToAccount.TryGetValue(d.NetPaymentAccountCode!, out var a))
                            ? a : defaultCashAccount;
                        byPayAccount[acc.Id] = byPayAccount.TryGetValue(acc.Id, out var cur)
                            ? (acc, cur.Amt + net) : (acc, net);
                    }
                    if (byPayAccount.Count == 0)
                        // data เก่าไม่มี detail → ลงรวมบรรทัดเดียวเหมือนเดิม
                        lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                            defaultCashAccount.Id, 0, cashPaid, $"จ่ายเงินเดือน {run.Month}/{run.Year}"));
                    else
                        foreach (var kv in byPayAccount.Values)
                            lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                                kv.Acc.Id, 0, kv.Amt,
                                $"จ่ายเงินเดือน {run.Month}/{run.Year}"
                                    + (byPayAccount.Count > 1 ? $" ({kv.Acc.AccountCode})" : "")));
                }

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
                    if (je != null)
                    {
                        je.IsAutoGenerated = true;
                        // Payroll JEs are sensitive — flag so GL / general-ledger
                        // queries can redact them for users without PayrollView.
                        je.Sensitivity = SensitivityKind.Payroll;
                    }
                    // Link the run to its posted journal entry, then recover
                    // outstanding advances (entities are tracked on the same
                    // context — persisted by the SaveChanges below, inside
                    // the surrounding payTransaction).
                    run.JournalEntryId = entry.Id;
                    foreach (var (adv, amount) in advanceRepayments)
                    {
                        if (_salaryAdvanceService!.ApplyRepayment(adv, amount))
                            clearedAdvances.Add(adv);
                    }
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

        await NotifyRunAsync(companyId, NotificationEvents.PayrollPaid, actorUserId: null,
            title: $"จ่ายเงินเดือน {run.Month:D2}/{run.Year} เรียบร้อย",
            message: $"ดำเนินการโดย {processedBy} · ยอดรวมจ่ายสุทธิ {run.TotalNetPay:N2} บาท",
            entityId: run.Id);

        // แจ้งพนักงานแต่ละคนตรง ๆ ผ่าน LINE/Email — ใช้ Employee.LineId/Email
        // ที่ NotificationEngine resolve อัตโนมัติเมื่อ role=Requester. ผู้ใช้
        // เปิด/ปิด event นี้ได้จากหน้าตั้งค่าการแจ้งเตือน (กฎอยู่ที่ admin
        // matrix). PDF สลิปแนบทาง Email ผ่าน EmailScheduleService ที่
        // ถัดมาอยู่แล้ว — LINE จะแจ้งเป็นข้อความสั้น ๆ พร้อมลิงก์ดูสลิป.
        var paidDetails = await _db.Set<PayrollDetail>().AsNoTracking()
            .Where(d => d.PayrollRunId == run.Id && d.CompanyId == companyId)
            .Select(d => new { d.EmployeeId, d.NetPay, d.Id })
            .ToListAsync();
        foreach (var det in paidDetails)
        {
            await NotifyHrAsync(companyId, NotificationEvents.PayrollSlipReady,
                det.EmployeeId, actorUserId: null,
                title: $"เงินเดือน {run.Month:D2}/{run.Year} โอนเรียบร้อยแล้ว",
                message: $"ยอดสุทธิ {det.NetPay:N2} บาท เข้าบัญชีของท่านแล้ว\nดูสลิป: เข้าระบบ NextAcc แท็บเงินเดือน",
                entityId: det.Id, entityType: "PayslipDetail",
                actionUrl: $"/pages/payroll.html?run={run.Id}&emp={det.EmployeeId}");
        }

        // งานหนักหลังจ่าย (สร้าง PDF ภงด.1 cert + ภงด.1/สปส. filings + สลิปทุกคน
        // + อีเมล) — ย้ายออกนอก request ผ่าน DI scope ใหม่ เพื่อกัน proxy/connection
        // timeout จากการ render PDF หลายไฟล์. การจ่ายเงิน commit แล้ว → response
        // ต้องกลับทันที. เอกสารพวกนี้ best-effort + สร้าง on-demand ได้อยู่แล้ว.
        await DispatchPostPaymentArtifactsAsync(companyId, run.Id, processedBy);

        // Notify each employee whose advance was fully repaid by this run.
        foreach (var adv in clearedAdvances)
        {
            await NotifyHrAsync(companyId, NotificationEvents.AdvanceCleared,
                adv.EmployeeId, actorUserId: null,
                title: "เงินทดรองของคุณเคลียร์ครบแล้ว",
                message: $"ยอดรวม {adv.Amount:N2} บาท ถูกหักคืนครบทั้งจำนวนจากเงินเดือน {run.Month:D2}/{run.Year}",
                entityId: adv.Id, entityType: "SalaryAdvance",
                actionUrl: "/pages/salary-advance.html");
        }

        return MapToPayrollRunResponse(run);
    }

    /// <summary>
    /// ออกใบ 50 ทวิรายปีให้พนักงาน — รวบรวม PayrollDetail ของทุกรอบใน
    /// ปี ค.ศ. ที่ระบุ จัดเป็นใบรับรองหัก ณ ที่จ่ายต่อพนักงาน 1 ใบ
    /// (TaxType.WithholdingTax1 = ภงด.1 §40(1) เงินเดือน) แล้ว generate
    /// PDF ตามเทมเพลตที่ใช้กับ supplier เดิม. ใบรับรองสร้างใน-memory
    /// ไม่บันทึก WithholdingTaxCert entity (พนักงานเก็บตามรอบ payroll
    /// อยู่แล้ว — ไม่ต้องซ้ำ).
    /// คืน Zip ที่รวมทุกใบเป็นไฟล์เดียว (Filename / Bytes).
    /// </summary>
    public async Task<(string FileName, byte[] Bytes)> GenerateAnnualEmployeeWhtCertsAsync(
        Guid companyId, int year, Guid? singleEmployeeId, string requestedBy)
    {
        if (_pdfService is not Implementations.PdfGenerationService pdf)
            throw new InvalidOperationException("PDF service is not available");

        var details = await _db.Set<PayrollDetail>()
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            // §50ทวิ requires a cert for every employee that received income
            // in the year, even when WithholdingTax is ฿0 (low earner). Also
            // include runs that finalised after year-end (Approved → late
            // adjustments), not just Paid.
            .Where(d => d.CompanyId == companyId
                && d.PayrollRun.Year == year
                && (d.PayrollRun.Status == "Paid" || d.PayrollRun.Status == "Approved")
                && d.GrossIncome > 0
                && (singleEmployeeId == null || d.EmployeeId == singleEmployeeId.Value))
            .OrderBy(d => d.EmployeeId).ThenBy(d => d.PayrollRun.Month)
            .ToListAsync();

        if (details.Count == 0)
            throw new InvalidOperationException(
                singleEmployeeId.HasValue
                    ? "ไม่พบเงินได้ของพนักงานนี้ในปีที่เลือก"
                    : "ไม่พบเงินได้ของพนักงานในปีที่เลือก");

        var grouped = details.GroupBy(d => d.EmployeeId).ToList();

        // Single employee → return the PDF directly. Multiple → zip them.
        using var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var g in grouped)
            {
                var emp = g.First().Employee;
                var totalIncome = g.Sum(d => d.GrossIncome);
                var totalTax = g.Sum(d => d.WithholdingTax);
                // Effective rate (display): tax / income × 100. แสดงเป็น
                // อัตราเฉลี่ยรายปี (RD ยอมรับ).
                var effRate = totalIncome > 0 ? Math.Round(totalTax * 100m / totalIncome, 2) : 0m;

                var cert = new WithholdingTaxCert
                {
                    CompanyId = companyId,
                    CertificateNumber = $"PAYROLL-{year}-{emp.EmployeeCode}",
                    PayeeContact = BuildEmployeeAsContact(emp),
                    PayeeContactId = Guid.Empty,
                    TaxFormType = TaxType.WithholdingTax1,   // ภงด.1 — §40(1) เงินเดือน
                    TaxYear = year,
                    TaxMonth = 12,                            // annual summary
                    CertificateType = WithholdingTaxCertType.Withhold,
                    Status = WithholdingTaxCertStatus.Issued,
                    IssuedDate = DateTime.UtcNow,
                    TotalIncomeAmount = totalIncome,
                    TotalTaxAmount = totalTax,
                    CreatedBy = requestedBy,
                };
                // หนึ่งบรรทัดต่อเดือนที่จ่ายจริง — โปร่งใสกว่ารวมยอดเดียว
                var order = 1;
                foreach (var d in g.OrderBy(x => x.PayrollRun.Month))
                {
                    cert.Lines.Add(new WithholdingTaxCertLine
                    {
                        LineOrder = order++,
                        IncomeTypeCode = "1",   // §40(1) เงินเดือน
                        IncomeDescription = $"เงินเดือน เดือน {d.PayrollRun.Month:D2}/{year}",
                        PaymentDate = d.PayrollRun.PayDate,
                        IncomeAmount = d.GrossIncome,
                        TaxRate = d.GrossIncome > 0 ? Math.Round(d.WithholdingTax * 100m / d.GrossIncome, 2) : 0m,
                        TaxAmount = d.WithholdingTax,
                    });
                }

                var pdfBytes = await pdf.BuildEmployeeAnnualCertPdfAsync(companyId, cert);
                // Audit trail: record every 50ทวิ generation so a dispute
                // ("ผมไม่เคยได้ใบรับรอง") has an answer (when + who + which
                // employee + amounts).
                _db.AuditLogs.Add(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = Guid.TryParse(requestedBy, out var actorId) ? actorId : (Guid?)null,
                    Action = AuditAction.Print,
                    EntityType = "WhtCertAnnual",
                    EntityId = emp.Id.ToString(),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        emp.EmployeeCode, emp.CitizenId,
                        Year = year,
                        TotalIncome = totalIncome, TotalTax = totalTax,
                    }),
                    Timestamp = DateTime.UtcNow,
                });

                if (grouped.Count == 1)
                {
                    await _db.SaveChangesAsync();
                    return ($"WHT50tawi_{emp.EmployeeCode}_{year}.pdf", pdfBytes);
                }

                var fileName = $"WHT50tawi_{emp.EmployeeCode}_{year}.pdf";
                var entry = archive.CreateEntry(fileName, System.IO.Compression.CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(pdfBytes);
            }
        }
        await _db.SaveChangesAsync();   // persist the per-employee audit rows
        return ($"WHT50tawi_{year}_employees.zip", zip.ToArray());
    }

    /// <summary>Build a Contact-shaped object holding the employee's identity
    /// + address — the cert renderer expects PayeeContact. Not saved to DB —
    /// purely a transport for the PDF builder.</summary>
    private static Contact BuildEmployeeAsContact(Employee e) => new()
    {
        Name = $"{e.TitleTh}{e.FirstNameTh} {e.LastNameTh}".Trim(),
        TaxId = e.CitizenId,
        // ContactType heuristic mirrors what BuildContactInfo does elsewhere —
        // CitizenId เริ่มต้นด้วย 0 = นิติบุคคล (rare for an employee), else บุคคล.
        ContactType = !string.IsNullOrEmpty(e.CitizenId) && e.CitizenId.StartsWith("0")
            ? ContactType.JuristicPerson : ContactType.Individual,
        Address = e.Address,
    };

    public async Task VoidPayrollAsync(Guid companyId, Guid payrollRunId)
    {
        var run = await _db.Set<PayrollRun>()
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรอบจ่ายเงินเดือน");

        if (run.Status == "Voided")
            throw new InvalidOperationException("รอบจ่ายเงินเดือนนี้ถูกยกเลิกแล้ว");

        // Paid runs CAN be voided — but the posted journal entry MUST be
        // reversed in the same transaction so AP/cash/WHT/SSO payables don't
        // sit on the books for an event that no longer counts. (Previously
        // VoidPayrollAsync only flipped the status string, leaving the GL
        // posted — every voided run silently corrupted the trial balance.)
        // Salary advances paid down by this run ARE NOW restored from each
        // PayrollDetail.AdvanceRecovered (recorded at Pay time) — without this,
        // voiding silently zeroed each employee's outstanding advance.
        var reversedNote = "รอบยังไม่ถูกผูกบัญชี — ยกเลิกได้ทันที";
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Lock the run row INSIDE the transaction เพื่อกัน void ซ้อน — สอง void
            // พร้อมกันต่างอ่าน Status="Paid" แล้ว restore advance/reverse JE ทั้งคู่
            // → OutstandingAmount เด้งกลับซ้ำ + JE ถูกกลับสองรอบ. Re-read หลัง lock:
            // ถ้าถูก void ไปแล้ว = no-op idempotent.
            var lockedRun = await _db.Set<PayrollRun>()
                .FromSqlRaw(
                    """SELECT * FROM "PayrollRuns" WHERE "Id" = {0} AND "CompanyId" = {1} FOR UPDATE""",
                    payrollRunId, companyId)
                .FirstOrDefaultAsync();
            if (lockedRun == null || lockedRun.Status == "Voided")
            {
                await tx.RollbackAsync();
                return;
            }
            run = lockedRun;

            if (run.Status == "Paid" && run.JournalEntryId.HasValue && _accountingService != null)
            {
                await _accountingService.ReverseJournalEntryAsync(companyId, run.JournalEntryId.Value,
                    reversalDate: DateTime.UtcNow.Date,
                    description: $"ยกเลิกรอบจ่ายเงินเดือน {run.PayrollNumber} ({run.Month:D2}/{run.Year})",
                    systemTriggered: true);
                reversedNote = "ระบบกลับรายการบัญชี + คืนยอดเงินทดรองที่หักในรอบนี้ให้พนักงานเรียบร้อย";
            }

            // Restore salary advances per the recorded per-detail recovery.
            // Apply oldest-first (FIFO) so the same advances we paid DOWN
            // become outstanding again in the same order they were cleared.
            if (run.Status == "Paid")
            {
                var details = await _db.Set<PayrollDetail>()
                    .Where(d => d.PayrollRunId == payrollRunId && d.CompanyId == companyId
                        && d.AdvanceRecovered > 0)
                    .Select(d => new { d.EmployeeId, d.AdvanceRecovered })
                    .ToListAsync();
                foreach (var d in details)
                {
                    var remaining = d.AdvanceRecovered;
                    // Pull advances that this run could have touched — any
                    // that still has ClearedAmount > 0 (we'll undo from those).
                    var advances = await _db.Set<SalaryAdvance>()
                        .Where(a => a.CompanyId == companyId && a.EmployeeId == d.EmployeeId
                            && a.ClearedAmount > 0 && !a.IsDeleted)
                        .OrderBy(a => a.RequestDate).ThenBy(a => a.Id)
                        .ToListAsync();
                    foreach (var adv in advances)
                    {
                        if (remaining <= 0) break;
                        var refund = Math.Min(remaining, adv.ClearedAmount);
                        adv.ClearedAmount -= refund;
                        adv.OutstandingAmount += refund;
                        // Re-open if it was fully cleared by this run.
                        if (adv.Status == "Cleared" && adv.OutstandingAmount > 0)
                            adv.Status = "Disbursed";
                        remaining -= refund;
                    }
                }
            }

            run.Status = "Voided";
            run.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        await NotifyRunAsync(companyId, NotificationEvents.PayrollPaid, actorUserId: null,
            title: $"ยกเลิกรอบจ่ายเงินเดือน {run.Month:D2}/{run.Year}",
            message: reversedNote,
            entityId: run.Id);
    }

    /// <summary>
    /// Auto-generate every government filing + payslip for a paid run and
    /// attach them to the PayrollRun so HR has ONE place to download from.
    /// Files produced (each saved as FileAttachment with EntityType="PayrollRun"
    /// + EntityId=run.Id so the existing attachment list endpoint surfaces
    /// them):
    ///   • ภ.ง.ด.1 (รายงานหัก ณ ที่จ่ายเงินเดือน) — submit to RD by the 7th
    ///   • สปส.1-10 (เงินสมทบประกันสังคม) — submit to SSO by the 15th
    ///   • Slip เงินเดือน — one PDF per employee
    /// Every step is wrapped so a single broken payslip doesn't lose the others.
    /// Idempotent on file name — re-running (e.g. after a retry) skips files
    /// already attached.
    /// </summary>
    /// <summary>ออก WithholdingTaxCert (ภ.ง.ด.1) ต่อพนักงาน per month
    /// เมื่อ payroll status=Paid. Idempotent: ถ้า cert เดิมมีอยู่แล้ว
    /// (same Year+Month+Employee+TaxForm) → void เก่า + ออกใหม่ (re-post
    /// payroll = re-issue). Audit trail เก็บผ่าน SourcePayrollRunId.
    /// Best-effort — generation failure ไม่ rollback การ pay.</summary>
    private async Task IssueMonthlyPnd1CertsAsync(Guid companyId, PayrollRun run)
    {
        try
        {
            var details = run.Details.Where(d => d.WithholdingTax > 0).ToList();
            if (details.Count == 0) return;

            // Void existing certs ที่ออกจาก run นี้ (re-post scenario)
            var existingFromThisRun = await _db.Set<WithholdingTaxCert>()
                .Where(c => c.CompanyId == companyId && c.SourcePayrollRunId == run.Id
                    && c.Status != WithholdingTaxCertStatus.Voided)
                .ToListAsync();
            foreach (var ex in existingFromThisRun) ex.Status = WithholdingTaxCertStatus.Voided;

            // Map employee → contact (auto-create contact stub ถ้าไม่มี).
            // WhtCert link ผ่าน PayeeContactId — พนักงานต้องมี Contact record
            // ปกติระบบ payroll ออก Contact ให้แล้ว แต่ guard ไว้.
            var empIds = details.Select(d => d.EmployeeId).Distinct().ToList();
            var employees = await _db.Set<Employee>().AsNoTracking()
                .Where(e => empIds.Contains(e.Id))
                .ToListAsync();
            var empById = employees.ToDictionary(e => e.Id);

            foreach (var d in details)
            {
                if (!empById.TryGetValue(d.EmployeeId, out var emp)) continue;
                if (string.IsNullOrWhiteSpace(emp.TaxId)) continue; // ไม่มี tax id ออก cert ไม่ได้

                // Skip ถ้ามี cert เดือนนี้อยู่แล้วและไม่ใช่จาก run นี้
                // (กรณี HR ออกเองด้วยมือก่อน) — ไม่ override manual cert
                var manualExists = await _db.Set<WithholdingTaxCert>().AsNoTracking()
                    .AnyAsync(c => c.CompanyId == companyId
                        && c.TaxYear == run.Year && c.TaxMonth == run.Month
                        && c.TaxFormType == TaxType.WithholdingTax1
                        && c.SourcePayrollRunId == null
                        && c.Status != WithholdingTaxCertStatus.Voided
                        && c.PayeeContact.TaxId == emp.TaxId);
                if (manualExists) continue;

                // ค้น/สร้าง contact ของพนักงาน (employee-as-contact)
                var contact = await _db.Set<Contact>()
                    .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.TaxId == emp.TaxId);
                if (contact == null)
                {
                    contact = new Contact
                    {
                        CompanyId = companyId,
                        Name = $"{emp.TitleTh} {emp.FirstNameTh} {emp.LastNameTh}".Trim(),
                        TaxId = emp.TaxId,
                        ContactType = ContactType.Individual,
                        IsSupplier = true
                    };
                    _db.Set<Contact>().Add(contact);
                    await _db.SaveChangesAsync();
                }

                var certNumber = $"PND1-{run.Year}{run.Month:D2}-{emp.EmployeeCode}";
                var taxableIncome = d.TaxableGross > 0 ? d.TaxableGross : d.GrossIncome;

                var cert = new WithholdingTaxCert
                {
                    CompanyId = companyId,
                    CertificateNumber = certNumber,
                    PayeeContactId = contact.Id,
                    TaxFormType = TaxType.WithholdingTax1,
                    TaxYear = run.Year,
                    TaxMonth = run.Month,
                    CertificateType = Models.DTOs.Tax.WithholdingTaxCertType.Withhold,
                    Status = WithholdingTaxCertStatus.Issued,
                    TotalIncomeAmount = taxableIncome,
                    TotalTaxAmount = d.WithholdingTax,
                    IssuedDate = DateTime.UtcNow,
                    SourcePayrollRunId = run.Id
                };
                _db.Set<WithholdingTaxCert>().Add(cert);
                await _db.SaveChangesAsync();

                _db.Set<WithholdingTaxCertLine>().Add(new WithholdingTaxCertLine
                {
                    WithholdingTaxCertId = cert.Id,
                    LineOrder = 1,
                    IncomeTypeCode = "1",   // §40(1) เงินเดือน
                    IncomeDescription = $"เงินเดือนประจำเดือน {run.Month:D2}/{run.Year}",
                    PaymentDate = run.PayDate,
                    IncomeAmount = taxableIncome,
                    TaxRate = taxableIncome > 0 ? Math.Round(d.WithholdingTax / taxableIncome * 100, 4) : 0,
                    TaxAmount = d.WithholdingTax
                });
            }
            await _db.SaveChangesAsync();
            _logger?.LogInformation("ออก ภ.ง.ด.1 cert {Count} ฉบับสำหรับ run {Run}", details.Count, run.Id);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "IssueMonthlyPnd1CertsAsync failed run={Run}", run.Id);
        }
    }

    /// <summary>ยิงงานสร้างเอกสารหลังจ่าย — ถ้ามี IServiceScopeFactory ทำใน
    /// background scope (response กลับทันที กัน timeout); ไม่มี (เช่น test) →
    /// ทำ inline ใน request scope เดิม.</summary>
    private async Task DispatchPostPaymentArtifactsAsync(Guid companyId, Guid runId, string actor)
    {
        if (_scopeFactory == null)
        {
            await GeneratePostPaymentArtifactsAsync(companyId, runId, actor);
            return;
        }
        var sf = _scopeFactory;
        // fire-and-forget — scope ใหม่มี DbContext ของตัวเอง (request scope เดิม
        // ถูก dispose หลัง response). best-effort: error = log + ปล่อย (สร้าง
        // เอกสาร on-demand ได้)
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = sf.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IPayrollService>();
                await svc.GeneratePostPaymentArtifactsAsync(companyId, runId, actor);
            }
            catch { /* swallowed — เอกสาร best-effort */ }
        });
    }

    public async Task GeneratePostPaymentArtifactsAsync(Guid companyId, Guid runId, string actor)
    {
        var run = await _db.Set<PayrollRun>()
            .FirstOrDefaultAsync(r => r.Id == runId && r.CompanyId == companyId && !r.IsDeleted);
        if (run == null) return;

        // ออก ภ.ง.ด.1 cert ต่อพนักงาน (idempotent)
        await IssueMonthlyPnd1CertsAsync(companyId, run);

        // Auto-generate government filings + payslip ทุกคน แนบเข้า run (จุด
        // ดาวน์โหลดเดียวให้ HR). Best-effort.
        await AutoGenerateFilingsAsync(companyId, run, actor);

        // Auto-email schedule hook — enqueue payslip ส่งพนักงานแต่ละคน
        if (_emailSchedule != null)
        {
            try { await _emailSchedule.OnPayrollPaidAsync(companyId, run.Id); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Payslip email enqueue failed Run={Run}", run.Id); }
        }
    }

    private async Task AutoGenerateFilingsAsync(Guid companyId, PayrollRun run, string actor)
    {
        if (_attachments == null) return;

        // Resolve a real user for the FileAttachment FK (FileAttachmentService
        // requires UploadedByUserId). Owner is the safe fallback — same pattern
        // used by OcrController.ResolveUploaderUserIdAsync / WHT cert attach.
        Guid uploaderId = Guid.Empty;
        if (Guid.TryParse(actor, out var parsed)
            && await _db.Users.AsNoTracking().AnyAsync(u => u.Id == parsed))
            uploaderId = parsed;
        if (uploaderId == Guid.Empty)
            uploaderId = await _db.Set<CompanyUser>().AsNoTracking()
                .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId).FirstOrDefaultAsync();
        if (uploaderId == Guid.Empty)
            uploaderId = await _db.Set<CompanyUser>().AsNoTracking()
                .Where(cu => cu.CompanyId == companyId)
                .Select(cu => cu.UserId).FirstOrDefaultAsync();
        if (uploaderId == Guid.Empty) return;  // no user → can't attribute

        var existing = await _db.FileAttachments.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted
                && f.EntityType == "PayrollRun" && f.EntityId == run.Id)
            .Select(f => f.OriginalFileName)
            .ToListAsync();
        var alreadyAttached = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        async Task AttachAsync(string fileName, string contentType, byte[] bytes)
        {
            if (alreadyAttached.Contains(fileName) || bytes is null || bytes.Length == 0) return;
            try { await _attachments.UploadBytesAsync(companyId, "PayrollRun", run.Id, fileName, contentType, bytes, uploaderId); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Attach filing {File} failed for run {Run}", fileName, run.Id); }
        }

        // ── 1. ภ.ง.ด.1 — monthly salary WHT remittance ──────────────────
        if (_taxFilingExport != null && run.TotalWithholdingTax > 0)
        {
            try
            {
                var pnd1 = await _taxFilingExport.ExportPnd1Async(companyId, run.Year, run.Month);
                await AttachAsync(pnd1.FileName, pnd1.ContentType, pnd1.FileData);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ภ.ง.ด.1 generation failed for run {Run}", run.Id);
            }
        }

        // ── 2. สปส.1-10 — monthly SSO contribution report ────────────────
        if (_taxFilingExport != null
            && (run.TotalSocialSecurityEmployee > 0 || run.TotalSocialSecurityEmployer > 0))
        {
            try
            {
                var sso = await _taxFilingExport.ExportSso110Async(companyId, run.Year, run.Month);
                await AttachAsync(sso.FileName, sso.ContentType, sso.FileData);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "สปส.1-10 generation failed for run {Run}", run.Id);
            }
        }

        // ── 3. Payslips — one PDF per employee ──────────────────────────
        var employeeIds = await _db.Set<PayrollDetail>().AsNoTracking()
            .Where(d => d.PayrollRunId == run.Id && d.CompanyId == companyId)
            .Select(d => d.EmployeeId).ToListAsync();
        foreach (var empId in employeeIds)
        {
            try
            {
                var slip = await GeneratePayslipAsync(companyId, run.Id, empId);
                await AttachAsync(slip.FileName, "application/pdf", slip.PdfData);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Payslip generation failed (run {Run}, employee {Emp})", run.Id, empId);
            }
        }
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
            .Include(d => d.Employee).ThenInclude(e => e.DepartmentRef)
            .Include(d => d.Employee).ThenInclude(e => e.PositionRef)
            .Include(d => d.PayrollRun)
            .FirstOrDefaultAsync(d => d.PayrollRunId == payrollRunId
                && d.EmployeeId == employeeId
                && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายละเอียดเงินเดือน");

        var emp = detail.Employee;
        var run = detail.PayrollRun;
        var employeeName = $"{emp.TitleTh}{emp.FirstNameTh} {emp.LastNameTh}".Trim();
        var deptDisplay = emp.DepartmentRef?.Name ?? emp.Department ?? "-";
        var posDisplay = emp.PositionRef?.Title ?? emp.Position ?? "-";

        // สลิป PDF — compose ด้วย QuestPDF โดยตรง (โลโก้ + สีธีมจากใบกำกับ)
        // ให้สวยคงที่ทุก server โดยไม่ต้องเปิด Puppeteer
        var payslipData = new PayslipPdfData(
            employeeName, emp.EmployeeCode, deptDisplay, posDisplay,
            run.Year, run.Month, run.PayDate,
            detail.BaseSalary, detail.OvertimePay, detail.Allowances, detail.Commission,
            detail.Bonus, detail.OtherIncome, detail.GrossIncome,
            detail.SocialSecurityEmployee, detail.WithholdingTax, detail.ProvidentFundEmployee,
            detail.LoanDeduction, detail.OtherDeductions, detail.TotalDeductions,
            detail.NetPay, detail.CumulativeIncomeYTD, detail.CumulativeTaxYTD);
        byte[] pdfContent = _pdfService != null
            ? await _pdfService.GeneratePayslipPdfAsync(companyId, payslipData)
            : Array.Empty<byte>();

        // ชื่อไฟล์มีชื่อพนักงาน (ตามที่เจ้าของขอ) — sanitize อักขระต้องห้าม
        var safeName = SanitizeFileToken(employeeName);
        var fileName = $"สลิปเงินเดือน_{safeName}_{run.Month:D2}-{run.Year}.pdf";

        return new PayslipResponse(
            employeeId, employeeName, run.Year, run.Month, pdfContent, fileName);
    }

    private static readonly string[] _thMonthsFull = { "", "มกราคม", "กุมภาพันธ์", "มีนาคม",
        "เมษายน", "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม" };

    /// <summary>ตัดอักขระที่ใช้เป็นชื่อไฟล์ไม่ได้ออก (กัน path traversal/HTTP header).</summary>
    private static string SanitizeFileToken(string s)
    {
        var cleaned = new string((s ?? "").Select(ch =>
            char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-' || ch == '_'
            || (ch >= '฀' && ch <= '๿') ? ch : '_').ToArray()).Trim();
        cleaned = cleaned.Replace(' ', '-');
        return string.IsNullOrWhiteSpace(cleaned) ? "employee" : cleaned;
    }

    /// <summary>สลิปเงินเดือนดีไซน์ใหม่ — หัวแถบสีธีม + โลโก้บริษัท, การ์ดข้อมูล
    /// พนักงาน, ตารางรายได้/รายการหักชัดเจน, กล่องเงินสุทธิเด่น. ออกแบบสำหรับ
    /// headless Chromium (CSS เต็ม) และยัง degrade ได้บน block parser.</summary>
    private static string BuildPayslipHtml(PayrollDetail d, Employee emp, PayrollRun run,
        Company? company, string employeeName, string dept, string position,
        string primary, string? logoDataUri)
    {
        string M(decimal v) => v.ToString("N2");
        string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var periodTh = $"{_thMonthsFull[Math.Clamp(run.Month, 1, 12)]} {run.Year + 543}";
        var coName = Esc(company?.Name ?? "บริษัท");
        var coTax = string.IsNullOrWhiteSpace(company?.TaxId) ? "" : $"เลขประจำตัวผู้เสียภาษี {Esc(company!.TaxId)}";
        var coAddr = Esc(company?.Address ?? "");

        // แถวรายได้ (ซ่อนรายการที่เป็น 0 ยกเว้นเงินเดือนฐาน)
        var earn = new List<(string, decimal)>
        {
            ("เงินเดือน", d.BaseSalary), ("ค่าล่วงเวลา", d.OvertimePay),
            ("เบี้ยเลี้ยง / ค่าครองชีพ", d.Allowances), ("คอมมิชชัน", d.Commission),
            ("โบนัส", d.Bonus), ("รายได้อื่น", d.OtherIncome),
        };
        var ded = new List<(string, decimal)>
        {
            ("ประกันสังคม", d.SocialSecurityEmployee), ("ภาษีหัก ณ ที่จ่าย", d.WithholdingTax),
            ("กองทุนสำรองเลี้ยงชีพ", d.ProvidentFundEmployee), ("หักเงินกู้", d.LoanDeduction),
            ("หักอื่น ๆ", d.OtherDeductions),
        };
        string Rows(List<(string Label, decimal Val)> items) => string.Concat(
            items.Where((x, i) => i == 0 || x.Val != 0).Select(x =>
                $"<tr><td class='lbl'>{Esc(x.Label)}</td><td class='amt'>{M(x.Val)}</td></tr>"));

        return $@"<!DOCTYPE html><html lang='th'><head><meta charset='utf-8'/>
<style>
  *{{box-sizing:border-box;}}
  body{{font-family:'Sarabun','TH Sarabun New','Noto Sans Thai',sans-serif;font-size:13px;color:#1e293b;margin:0;padding:24px;}}
  .doc{{max-width:720px;margin:0 auto;}}
  .hdr{{display:flex;justify-content:space-between;align-items:center;background:{primary};color:#fff;border-radius:12px 12px 0 0;padding:18px 24px;}}
  .hdr .co{{display:flex;align-items:center;gap:14px;}}
  .hdr img{{height:46px;width:auto;background:#fff;border-radius:8px;padding:4px;}}
  .hdr .co-name{{font-size:18px;font-weight:700;line-height:1.25;}}
  .hdr .co-sub{{font-size:11px;opacity:.9;font-weight:400;}}
  .hdr .slip-t{{text-align:right;}}
  .hdr .slip-t .t1{{font-size:17px;font-weight:700;}}
  .hdr .slip-t .t2{{font-size:12px;opacity:.92;}}
  .meta{{display:grid;grid-template-columns:repeat(4,1fr);gap:1px;background:#e2e8f0;border:1px solid #e2e8f0;}}
  .meta .cell{{background:#f8fafc;padding:10px 14px;}}
  .meta .k{{font-size:10px;color:#64748b;text-transform:uppercase;letter-spacing:.4px;}}
  .meta .v{{font-size:13px;font-weight:600;color:#0f172a;margin-top:2px;}}
  .cols{{display:flex;gap:0;border:1px solid #e2e8f0;border-top:none;}}
  .col{{flex:1;}}
  .col + .col{{border-left:1px solid #e2e8f0;}}
  .col h3{{margin:0;font-size:13px;font-weight:700;padding:10px 16px;background:#f1f5f9;color:#0f172a;border-bottom:1px solid #e2e8f0;}}
  .col h3.earn{{color:{primary};}}
  table.lines{{width:100%;border-collapse:collapse;}}
  table.lines td{{padding:7px 16px;border-bottom:1px solid #f1f5f9;font-size:13px;}}
  table.lines td.amt{{text-align:right;font-variant-numeric:tabular-nums;}}
  .sub{{display:flex;justify-content:space-between;padding:10px 16px;font-weight:700;background:#f8fafc;border-top:1px solid #e2e8f0;font-size:13px;}}
  .sub .amt{{font-variant-numeric:tabular-nums;}}
  .net{{display:flex;justify-content:space-between;align-items:center;background:{primary};color:#fff;
        padding:16px 24px;border-radius:0 0 12px 12px;margin-top:0;}}
  .net .lbl{{font-size:14px;font-weight:600;}}
  .net .val{{font-size:24px;font-weight:800;font-variant-numeric:tabular-nums;}}
  .ytd{{display:flex;gap:24px;justify-content:flex-end;margin-top:12px;font-size:11px;color:#64748b;}}
  .ytd b{{color:#0f172a;font-variant-numeric:tabular-nums;}}
  .foot{{margin-top:18px;text-align:center;font-size:10px;color:#94a3b8;border-top:1px solid #e2e8f0;padding-top:10px;}}
</style></head>
<body><div class='doc'>
  <div class='hdr'>
    <div class='co'>
      {(string.IsNullOrEmpty(logoDataUri) ? "" : $"<img src='{logoDataUri}' alt='logo'/>")}
      <div><div class='co-name'>{coName}</div><div class='co-sub'>{coTax}{(string.IsNullOrEmpty(coAddr) ? "" : (string.IsNullOrEmpty(coTax) ? "" : " · ") + coAddr)}</div></div>
    </div>
    <div class='slip-t'><div class='t1'>สลิปเงินเดือน</div><div class='t2'>Payslip · {Esc(periodTh)}</div></div>
  </div>
  <div class='meta'>
    <div class='cell'><div class='k'>พนักงาน</div><div class='v'>{Esc(employeeName)}</div></div>
    <div class='cell'><div class='k'>รหัส</div><div class='v'>{Esc(emp.EmployeeCode)}</div></div>
    <div class='cell'><div class='k'>แผนก / ตำแหน่ง</div><div class='v'>{Esc(dept)} · {Esc(position)}</div></div>
    <div class='cell'><div class='k'>วันที่จ่าย</div><div class='v'>{run.PayDate:dd/MM/}{run.PayDate.Year + 543}</div></div>
  </div>
  <div class='cols'>
    <div class='col'>
      <h3 class='earn'>รายได้ (Earnings)</h3>
      <table class='lines'>{Rows(earn)}</table>
      <div class='sub'><span>รวมรายได้</span><span class='amt'>{M(d.GrossIncome)}</span></div>
    </div>
    <div class='col'>
      <h3>รายการหัก (Deductions)</h3>
      <table class='lines'>{Rows(ded)}</table>
      <div class='sub'><span>รวมรายการหัก</span><span class='amt'>{M(d.TotalDeductions)}</span></div>
    </div>
  </div>
  <div class='net'><span class='lbl'>เงินได้สุทธิ (Net Pay)</span><span class='val'>{M(d.NetPay)} ฿</span></div>
  <div class='ytd'><span>รายได้สะสมทั้งปี (YTD): <b>{M(d.CumulativeIncomeYTD)}</b></span><span>ภาษีสะสมทั้งปี (YTD): <b>{M(d.CumulativeTaxYTD)}</b></span></div>
  <div class='foot'>เอกสารนี้ออกโดยระบบ NextAcc · งวด {Esc(periodTh)} · พิมพ์เพื่อเก็บเป็นหลักฐานการรับเงินเดือน</div>
</div></body></html>";
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

        // ───── Per-type policy gates (LeaveType catalog) ─────
        // Half-day on a type that doesn't allow it → reject.
        // RequiresAttachment + Sick > 3 days (พ.ร.บ.คุ้มครองแรงงาน §32):
        // for now we enforce the "doctor cert needed" rule by requiring
        // request.Reason to be non-empty when RequiresAttachment is set
        // and TotalDays > 3 — actual attachment upload + check requires
        // the LeaveAttachment endpoint added later; treating non-empty
        // reason as the minimum bar for now so the gate isn't bypassed
        // silently. The frontend already warns the user about the
        // attachment in the picker hint.
        var typeRow = await _db.Set<LeaveType>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.CompanyId == companyId && t.Code == request.LeaveType && !t.IsDeleted);
        if (typeRow != null)
        {
            if (request.HalfDayMarker > 0 && !typeRow.AllowHalfDay)
                throw new InvalidOperationException(
                    $"ประเภท '{typeRow.NameTh}' ไม่อนุญาตให้ลาครึ่งวัน");
            if (typeRow.RequiresAttachment && request.TotalDays > 3
                && string.IsNullOrWhiteSpace(request.Reason))
                throw new InvalidOperationException(
                    $"ลา '{typeRow.NameTh}' มากกว่า 3 วันต้องระบุเหตุผล + แนบหลักฐาน " +
                    "(เช่นใบรับรองแพทย์) ตาม พ.ร.บ.คุ้มครองแรงงาน §32");
            if (typeRow.AdvanceNoticeDays > 0)
            {
                var noticeDays = (request.StartDate.Date - DateTime.UtcNow.Date).TotalDays;
                if (noticeDays < typeRow.AdvanceNoticeDays)
                {
                    // Soft warning only — Thai practice allows late
                    // requests for emergencies; we don't hard-block, but
                    // the rejection reason is recorded in case manager
                    // wants to ding the worker.
                    // Caller (UI) shows hint at picker time.
                }
            }
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
            HalfDayMarker = request.HalfDayMarker,
            Status = "Pending"
        };

        _db.Set<EmployeeLeave>().Add(leave);
        await _db.SaveChangesAsync();

        await NotifyHrAsync(companyId, NotificationEvents.LeaveSubmitted,
            employee.Id, actorUserId: null,
            title: $"คำขอลาใหม่จาก {employee.FirstNameTh} {employee.LastNameTh}",
            message: $"{leave.LeaveType} · {leave.StartDate:dd/MM/yyyy} – {leave.EndDate:dd/MM/yyyy} ({leave.TotalDays} วัน)" +
                     (string.IsNullOrWhiteSpace(leave.Reason) ? "" : $"\nเหตุผล: {leave.Reason}"),
            entityId: leave.Id, entityType: "EmployeeLeave",
            actionUrl: "/pages/payroll.html#tab=leaves");

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

    public async Task<LeaveResponse> ApproveLeaveAsync(Guid companyId, Guid leaveId, Guid approverUserId, string approverName)
    {
        var leave = await _db.Set<EmployeeLeave>()
            .Include(l => l.Employee)
            .FirstOrDefaultAsync(l => l.Id == leaveId && l.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการลา");

        if (leave.Status != "Pending")
            throw new InvalidOperationException("สามารถอนุมัติได้เฉพาะรายการที่รอดำเนินการ");

        await EnsureCanApproveAsync(companyId, leave.EmployeeId, approverUserId, "อนุมัติคำขอลา", PermissionKeys.LeaveApprove);

        leave.Status = "Approved";
        leave.ApprovedBy = approverName;
        await _db.SaveChangesAsync();

        await NotifyHrAsync(companyId, NotificationEvents.LeaveApproved,
            leave.EmployeeId, actorUserId: approverUserId,
            title: $"คำขอลา {leave.StartDate:dd/MM} – {leave.EndDate:dd/MM} ได้รับอนุมัติ",
            message: $"{leave.LeaveType} · อนุมัติโดย {approverName}",
            entityId: leave.Id, entityType: "EmployeeLeave",
            actionUrl: "/pages/payroll.html#tab=leaves");

        return MapToLeaveResponse(leave, leave.Employee);
    }

    public async Task<LeaveResponse> RejectLeaveAsync(Guid companyId, Guid leaveId, Guid rejectorUserId, string rejectorName, RejectLeaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new InvalidOperationException("กรุณาระบุเหตุผลในการปฏิเสธ");

        var leave = await _db.Set<EmployeeLeave>()
            .Include(l => l.Employee)
            .FirstOrDefaultAsync(l => l.Id == leaveId && l.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการลา");

        if (leave.Status != "Pending")
            throw new InvalidOperationException("สามารถปฏิเสธได้เฉพาะรายการที่รอดำเนินการ");

        await EnsureCanApproveAsync(companyId, leave.EmployeeId, rejectorUserId, "ปฏิเสธคำขอลา", PermissionKeys.LeaveReject);

        leave.Status = "Rejected";
        leave.ApprovedBy = rejectorName;
        leave.RejectionReason = request.Reason;
        await _db.SaveChangesAsync();

        await NotifyHrAsync(companyId, NotificationEvents.LeaveRejected,
            leave.EmployeeId, actorUserId: rejectorUserId,
            title: $"คำขอลา {leave.StartDate:dd/MM} – {leave.EndDate:dd/MM} ถูกปฏิเสธ",
            message: $"เหตุผล: {request.Reason}",
            entityId: leave.Id, entityType: "EmployeeLeave",
            actionUrl: "/pages/payroll.html#tab=leaves");

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

        await NotifyHrAsync(companyId, NotificationEvents.LeaveCancelled,
            leave.EmployeeId, actorUserId: null,
            title: $"คำขอลา {leave.StartDate:dd/MM} – {leave.EndDate:dd/MM} ถูกยกเลิก",
            message: $"{leave.LeaveType}",
            entityId: leave.Id, entityType: "EmployeeLeave",
            actionUrl: "/pages/payroll.html#tab=leaves");

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
        // Priority order (highest wins):
        //   1) LeaveType table (HR configured via /leaves/admin/types)
        //   2) CompanySettings.LeaveQuotasJson (legacy)
        //   3) DefaultLeaveQuotas (Thai labor-law baseline)
        // 1 + 2 fill the gaps from 3 so a tenant doesn't accidentally
        // lose a quota by partial config.
        var typeRows = await _db.Set<LeaveType>().AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted)
            .ToListAsync();
        if (typeRows.Count > 0)
        {
            var merged = new Dictionary<string, decimal>(DefaultLeaveQuotas);
            foreach (var t in typeRows) merged[t.Code] = t.AnnualQuota;
            return merged;
        }
        // Fallback to the legacy JSON in CompanySettings.
        var settings = await _db.Set<CompanySettings>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (string.IsNullOrWhiteSpace(settings?.LeaveQuotasJson))
            return new Dictionary<string, decimal>(DefaultLeaveQuotas);
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(settings.LeaveQuotasJson);
            if (parsed == null) return new Dictionary<string, decimal>(DefaultLeaveQuotas);
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

        // Wage base cap follows the YEAR being reported, not a fixed 15,000.
        var ssoParams = await GetSsoParamsAsync(companyId, year);
        var lines = details.Select(d => new
        {
            EmployeeCode = d.Employee.EmployeeCode,
            SocialSecurityNumber = d.Employee.SocialSecurityNumber,
            FullName = $"{d.Employee.TitleTh}{d.Employee.FirstNameTh} {d.Employee.LastNameTh}",
            SalaryBase = Math.Min(d.BaseSalary, ssoParams.MaxBase),
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
            .Include(d => d.Lines)   // ไม่ Include Contact — hydrate แยก (กัน INNER JOIN ตัดแถว ภ.ง.ด.3)
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.WithholdingTaxAmount > 0)
            .ToListAsync();
        await _db.HydrateContactsAsync(companyId, docs);

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
    /// <summary>คิดภาษีโดย walk progressive brackets — รับ override
    /// brackets จาก TaxRuleConfig (ถ้ามี) ไม่งั้น fallback ใช้
    /// hardcoded ThaiTaxBrackets ปัจจุบัน.</summary>
    private static decimal CalculateThaiIncomeTax(decimal annualTaxableIncome,
        (decimal UpperBound, decimal Rate)[]? brackets = null)
    {
        if (annualTaxableIncome <= 0) return 0;
        var b = brackets ?? ThaiTaxBrackets;

        decimal totalTax = 0;
        decimal previousBound = 0;

        foreach (var (upperBound, rate) in b)
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

    /// <summary>โหลด TaxRuleConfig ของ company × fiscal year. คืน null
    /// ถ้าไม่มี → caller ใช้ค่า default (Pit* constants + ThaiTaxBrackets).
    /// Cache ใน-memory ของ instance นี้ — Year ของ payroll ไม่เปลี่ยนระหว่าง run.</summary>
    private readonly Dictionary<(Guid CompanyId, int Year), TaxRuleConfig?> _taxRuleCache = new();
    private async Task<TaxRuleConfig?> GetTaxRuleAsync(Guid companyId, int fiscalYear)
    {
        var key = (companyId, fiscalYear);
        if (_taxRuleCache.TryGetValue(key, out var cached)) return cached;
        var cfg = await _db.TaxRuleConfigs.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.FiscalYear == fiscalYear && c.IsActive && !c.IsDeleted)
            .FirstOrDefaultAsync();
        _taxRuleCache[key] = cfg;
        return cfg;
    }

    /// <summary>Parse BracketsJson → array สำหรับ CalculateThaiIncomeTax.
    /// คืน null ถ้า config ไม่มี / json ว่าง / parse fail → caller fallback.</summary>
    private static (decimal UpperBound, decimal Rate)[]? ParseBrackets(TaxRuleConfig? cfg)
    {
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.BracketsJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(cfg.BracketsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var list = new List<(decimal UpperBound, decimal Rate)>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("upperBound", out var ub) || !ub.TryGetDecimal(out var u)) continue;
                if (!el.TryGetProperty("rate", out var rt) || !rt.TryGetDecimal(out var r)) continue;
                list.Add((u <= 0 ? decimal.MaxValue : u, r));
            }
            return list.Count > 0 ? list.OrderBy(b => b.UpperBound).ToArray() : null;
        }
        catch { return null; }
    }

    // ===== Mapping Helpers =====

    /// <summary>PDPA ม.26 mask flag: เมื่อ caller ไม่มี permission "pii:view"
    /// → CitizenId, Phone, Email mask ด้วย PiiMask helper. Default คือ
    /// masked (deny by default). controller ต้อง opt-in.</summary>
    private static EmployeeResponse MapToEmployeeResponse(Employee e, bool includePii = false, bool includeSalary = true)
    {
        var citizenId = includePii ? e.CitizenId : Accounting.Helpers.PiiMask.CitizenId(e.CitizenId);
        var phone = includePii ? e.Phone : Accounting.Helpers.PiiMask.Phone(e.Phone);
        var email = includePii ? e.Email : Accounting.Helpers.PiiMask.Email(e.Email);
        // เงินเดือน = ข้อมูลอ่อนไหว (payroll sensitivity) — ผู้ไม่มีสิทธิ์ดูเงินเดือน
        // ได้ค่า 0 (ยังใช้เลือกพนักงานใน dropdown/org chart ได้ แต่ไม่เห็นฐานเงินเดือน)
        var baseSalary = includeSalary ? e.BaseSalary : 0m;
        return new(e.Id, e.EmployeeCode, e.TitleTh, e.FirstNameTh, e.LastNameTh,
            e.FirstNameEn, e.LastNameEn, citizenId, e.Department, e.Position,
            e.EmploymentType, e.StartDate, e.EndDate, baseSalary,
            e.SalaryType, e.IsActive, e.CreatedAt,
            e.DepartmentId, e.DepartmentRef?.Name,
            e.PositionId, e.PositionRef?.Title,
            e.DirectManagerId,
            e.DirectManager != null
                ? $"{e.DirectManager.TitleTh}{e.DirectManager.FirstNameTh} {e.DirectManager.LastNameTh}".Trim()
                : null,
            e.ContactId,
            e.CostBehavior,
            e.ExternalId, e.ExternalSystem, e.LastSyncedAt,
            phone, email, e.LineId);
    }

    private static PayrollItemResponse MapToPayrollItemResponse(PayrollItem i) =>
        new(i.Id, i.Code, i.Name, i.ItemType, i.CalculationType,
            i.FixedAmount, i.Percentage, i.IsTaxable, i.IsActive);

    private static PayrollRunResponse MapToPayrollRunResponse(PayrollRun r) =>
        new(r.Id, r.PayrollNumber, r.Name, r.Year, r.Month, r.PayDate,
            r.Status, r.TotalGrossSalary, r.TotalDeductions, r.TotalNetPay,
            r.TotalWithholdingTax, r.TotalSocialSecurityEmployee,
            r.TotalSocialSecurityEmployer, r.EmployeeCount, r.CreatedAt,
            r.SsoSettledAt, r.SsoSettlementJournalEntryId, r.SsoFilingNumber,
            r.SsoLateFeeAmount, r.TotalWorkersCompensation,
            Details: null, ExternalSystem: r.ExternalSystem, ExternalRunRef: r.ExternalRunRef,
            JournalEntryId: r.JournalEntryId);

    private static LeaveResponse MapToLeaveResponse(EmployeeLeave l, Employee e) =>
        new(l.Id, l.EmployeeId, $"{e.FirstNameTh} {e.LastNameTh}",
            l.LeaveType, l.StartDate, l.EndDate, l.TotalDays, l.Status, l.Reason,
            l.ApprovedBy, l.RejectionReason,
            HalfDayMarker: l.HalfDayMarker,
            CreatedAt: l.CreatedAt);

    // ── Settle SSO to สำนักงานประกันสังคม (สปส.1-10) ────────────────────────
    //
    // ตอนจ่ายเงินเดือน (ProcessPaymentAsync) ระบบ Cr 21815 (ประกันสังคมค้างจ่าย)
    // ค้างไว้. กฎหมาย — พ.ร.บ.ประกันสังคม §47: นายจ้างต้องนำส่งภายในวันที่ 15
    // ของเดือนถัดไป (กระดาษ) / 22 ของเดือนถัดไป (e-Filing).
    // method นี้ post JE คู่ที่สอง: **Dr 21815 / Cr Bank** ตามตัวอย่างที่
    // ผู้ใช้แสดงมา → หนี้สิน 21815 หักล้างเหลือ 0 พอดี.
    //
    // late fee (§49): เงินเพิ่ม **2% ต่อเดือน** ของยอดที่นำส่งช้า เริ่มนับ
    // จากวันที่เลยกำหนด. ระบบคำนวณ + post Dr 5xxx ค่าใช้จ่าย / Cr Bank ในรอบ
    // เดียวกัน. payDate ≤ deadline → late fee = 0.
    public async Task<PayrollRunResponse> SettleSocialSecurityAsync(
        Guid companyId, Guid payrollRunId,
        DateTime payDate, Guid? bankAccountId, Guid? bankGlAccountId, string? filingNumber, string performedBy)
    {
        if (_accountingService == null)
            throw new InvalidOperationException("ระบบบัญชียังไม่พร้อม — ไม่สามารถลง JE นำส่งประกันสังคม");

        var run = await _db.Set<PayrollRun>()
            .FirstOrDefaultAsync(r => r.Id == payrollRunId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรอบเงินเดือน");

        if (run.Status != "Paid")
            throw new InvalidOperationException(
                $"นำส่งประกันสังคมได้เมื่อรอบเงินเดือนอยู่สถานะ Paid เท่านั้น (ปัจจุบัน: {run.Status})");

        if (run.SsoSettledAt.HasValue)
            throw new InvalidOperationException(
                $"รอบนี้นำส่ง สปส. ไปแล้วเมื่อ {run.SsoSettledAt:dd/MM/yyyy} (JE {run.SsoSettlementJournalEntryId})");

        var totalSso = run.TotalSocialSecurityEmployee + run.TotalSocialSecurityEmployer;
        if (totalSso <= 0)
            throw new InvalidOperationException("รอบนี้ไม่มียอดประกันสังคมต้องนำส่ง");

        // ── หาผังบัญชี ──
        // 21815 ประกันสังคมค้างจ่าย (Cr ตอนจ่ายเงินเดือน) → ตอนนี้ Dr ล้างหนี้
        var ssoPayableAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode == "21815" && a.Level >= 4)
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode.StartsWith("218") && a.Level >= 4
                && a.AccountName.Contains("ประกันสังคม"))
            ?? throw new InvalidOperationException(
                "ไม่พบบัญชี 'ประกันสังคมค้างจ่าย' (21815) — กรุณาสร้างก่อน");

        // ── หา Bank/Cash ──
        // (1) ถ้าผู้ใช้ระบุ BankAccountId → ใช้ LinkedAccountId ของบัญชีนั้น
        // (2) ถ้าไม่ระบุ → ใช้ CompanySettings.DefaultPaymentAccountId
        // (3) สุดท้าย fallback บัญชี 111x ตัวแรก (auto-pick lowest)
        Guid? bankGlId = null;
        // (0) เลือก GL เงินสด/เงินทดรองกรรมการ/ช่องจ่ายอื่นโดยตรง (payment channel)
        //     — validate ว่าเป็นผังของบริษัทนี้ + active + posting level
        if (bankGlAccountId.HasValue && bankGlAccountId.Value != Guid.Empty)
        {
            bankGlId = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.Id == bankGlAccountId.Value && a.CompanyId == companyId
                    && a.IsActive && !a.IsDeleted && a.Level >= 4)
                .Select(a => (Guid?)a.Id).FirstOrDefaultAsync();
            if (!bankGlId.HasValue)
                throw new InvalidOperationException("บัญชีแหล่งจ่ายที่เลือกไม่ถูกต้อง — เลือกใหม่อีกครั้ง");
        }
        if (!bankGlId.HasValue && bankAccountId.HasValue)
        {
            bankGlId = await _db.BankAccounts.AsNoTracking()
                .Where(b => b.Id == bankAccountId.Value && b.CompanyId == companyId)
                .Select(b => b.LinkedAccountId)
                .FirstOrDefaultAsync();
            if (!bankGlId.HasValue)
                throw new InvalidOperationException(
                    "บัญชีธนาคารที่เลือกยังไม่ผูกผังบัญชี (LinkedAccountId) — ตั้งค่าก่อน");
        }
        if (!bankGlId.HasValue)
        {
            var cs = await _db.Set<CompanySettings>().AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted)
                .Select(c => c.DefaultPaymentAccountId)
                .FirstOrDefaultAsync();
            if (cs.HasValue) bankGlId = cs;
        }
        if (!bankGlId.HasValue)
        {
            bankGlId = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                    && a.AccountCode.StartsWith("111") && a.Level >= 4)
                .OrderBy(a => a.AccountCode)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync();
        }
        if (!bankGlId.HasValue)
            throw new InvalidOperationException("ไม่พบบัญชีเงินสด/ธนาคาร (111x) — กรุณาสร้างก่อน");

        // ── late fee §49 (2%/เดือนของยอดที่นำส่งช้า, เริ่มนับวันที่ 16 ของเดือนถัดไป) ──
        // เพดาน late fee = 100% ของยอดที่นำส่ง (ตามคำพิพากษาสรรพากร — ใช้
        // เกณฑ์เดียวกัน เพราะ พ.ร.บ.ประกันสังคมไม่ระบุ cap → ใช้ practice).
        var lateFee = ComputeSsoLateFee(run.Year, run.Month, payDate, totalSso);

        // ── post JE: Dr 21815 / Cr Bank (+ late fee ถ้ามี) ──
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var desc = $"นำส่งประกันสังคม {run.Month:D2}/{run.Year}" +
                       (string.IsNullOrWhiteSpace(filingNumber) ? "" : $" — เลขรับ {filingNumber}");
            var jeLines = new List<Models.DTOs.Accounting.JournalLineRequest>
            {
                new(ssoPayableAccount.Id, totalSso, 0,
                    "ล้างประกันสังคมค้างจ่าย (Dr 21815)"),
            };
            if (lateFee > 0)
            {
                var lateFeeAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                    && a.AccountCode.StartsWith("54") && a.Level >= 4
                    && (a.AccountName.Contains("เงินเพิ่ม") || a.AccountName.Contains("ค่าปรับ")))
                    ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                        && a.AccountCode.StartsWith("58") && a.Level >= 4);   // ค่าใช้จ่ายอื่น
                if (lateFeeAccount != null)
                    jeLines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                        lateFeeAccount.Id, lateFee, 0,
                        $"เงินเพิ่มประกันสังคม 2%/เดือน (§49) — นำส่งช้า"));
            }
            jeLines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                bankGlId.Value, 0, totalSso + lateFee, $"จ่ายเงินสมทบประกันสังคม {run.Month:D2}/{run.Year}"));

            var jeReq = new Models.DTOs.Accounting.CreateJournalEntryRequest(
                EntryDate: payDate,
                Description: desc,
                Reference: run.PayrollNumber,
                Lines: jeLines,
                JournalType: Models.Enums.JournalType.General);
            var je = await _accountingService.CreateJournalEntryAsync(companyId, jeReq, performedBy);
            await _accountingService.PostJournalEntryAsync(companyId, je.Id);

            run.SsoSettledAt = payDate;
            run.SsoSettlementJournalEntryId = je.Id;
            run.SsoFilingNumber = filingNumber;
            run.SsoLateFeeAmount = lateFee;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger?.LogInformation(
                "Settled SSO for PayrollRun {RunId} ({Month}/{Year}): {Total} + late fee {Late} → JE {JeId}",
                run.Id, run.Month, run.Year, totalSso, lateFee, je.Id);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        return await GetPayrollRunAsync(companyId, payrollRunId);
    }

    /// <summary>คำนวณเงินเพิ่มประกันสังคม (§49 พ.ร.บ.ประกันสังคม):
    /// 2% ต่อเดือนของยอดที่นำส่ง × จำนวนเดือนช้า (ปัดเศษเดือนขึ้น).
    /// deadline = วันที่ 15 ของเดือนถัดจาก period; เพดาน 100% ของยอดส่ง.
    /// payDate ≤ deadline → 0. คืน 0 ทันทีถ้าไม่มียอดส่ง.</summary>
    public static decimal ComputeSsoLateFee(int periodYear, int periodMonth, DateTime payDate, decimal totalSso)
    {
        if (totalSso <= 0) return 0m;
        var deadline = new DateTime(periodYear, periodMonth, 15).AddMonths(1);   // 15 of next month
        if (payDate.Date <= deadline.Date) return 0m;
        var daysLate = (payDate.Date - deadline.Date).Days;
        var monthsLate = (decimal)Math.Ceiling(daysLate / 30.0);
        var fee = Math.Round(totalSso * 0.02m * monthsLate, 2);
        return Math.Min(fee, totalSso);   // cap 100%
    }
}

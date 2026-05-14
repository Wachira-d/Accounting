using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Payroll;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// Salary-advance workflow that integrates straight into the core
/// accounting engine: on disbursement it generates a standard
/// PaymentVoucher <see cref="Document"/> (Dr Advance Receivable /
/// Cr Cash) via the central <see cref="IDocumentService"/> — no isolated
/// HR ledger. Outstanding balances are recovered by the payroll run.
/// </summary>
public class SalaryAdvanceService : ISalaryAdvanceService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _documentService;

    public SalaryAdvanceService(AccountingDbContext db, IDocumentService documentService)
    {
        _db = db;
        _documentService = documentService;
    }

    public async Task<SalaryAdvanceResponse> CreateAsync(Guid companyId, CreateSalaryAdvanceRequest request, string createdBy)
    {
        if (request.Amount <= 0)
            throw new InvalidOperationException("จำนวนเงินทดรองต้องมากกว่า 0");
        if (request.MonthlyDeduction < 0)
            throw new InvalidOperationException("ยอดหักต่อเดือนต้องไม่ติดลบ");

        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        var advance = new SalaryAdvance
        {
            CompanyId = companyId,
            AdvanceNumber = await NextAdvanceNumberAsync(companyId),
            EmployeeId = employee.Id,
            RequestDate = request.RequestDate,
            Amount = request.Amount,
            Reason = request.Reason,
            MonthlyDeduction = request.MonthlyDeduction,
            OutstandingAmount = request.Amount,
            Status = "Draft",
            CreatedBy = createdBy
        };

        _db.SalaryAdvances.Add(advance);
        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, advance.Id);
    }

    public async Task<SalaryAdvanceResponse> GetByIdAsync(Guid companyId, Guid advanceId)
    {
        var advance = await _db.SalaryAdvances
            .Include(a => a.Employee)
            .Include(a => a.ApprovedByUser)
            .FirstOrDefaultAsync(a => a.Id == advanceId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการเงินทดรองจ่าย");
        return MapToResponse(advance);
    }

    public async Task<PagedResponse<SalaryAdvanceResponse>> GetAllAsync(
        Guid companyId, string? status, Guid? employeeId, PagedRequest request)
    {
        var query = _db.SalaryAdvances
            .Include(a => a.Employee)
            .Include(a => a.ApprovedByUser)
            .Where(a => a.CompanyId == companyId);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(a => a.Status == status);
        if (employeeId.HasValue)
            query = query.Where(a => a.EmployeeId == employeeId.Value);
        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(a => a.AdvanceNumber.Contains(request.Search));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<SalaryAdvanceResponse>(
            items.Select(MapToResponse).ToList(), total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<SalaryAdvanceResponse> UpdateAsync(Guid companyId, Guid advanceId, UpdateSalaryAdvanceRequest request)
    {
        var advance = await _db.SalaryAdvances
            .FirstOrDefaultAsync(a => a.Id == advanceId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการเงินทดรองจ่าย");

        if (advance.Status != "Draft")
            throw new InvalidOperationException("แก้ไขได้เฉพาะรายการที่เป็น Draft เท่านั้น");

        if (request.RequestDate.HasValue) advance.RequestDate = request.RequestDate.Value;
        if (request.Amount.HasValue)
        {
            if (request.Amount.Value <= 0)
                throw new InvalidOperationException("จำนวนเงินทดรองต้องมากกว่า 0");
            advance.Amount = request.Amount.Value;
            advance.OutstandingAmount = request.Amount.Value;
        }
        if (request.Reason != null) advance.Reason = request.Reason;
        if (request.MonthlyDeduction.HasValue)
        {
            if (request.MonthlyDeduction.Value < 0)
                throw new InvalidOperationException("ยอดหักต่อเดือนต้องไม่ติดลบ");
            advance.MonthlyDeduction = request.MonthlyDeduction.Value;
        }

        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, advance.Id);
    }

    public async Task<SalaryAdvanceResponse> SubmitAsync(Guid companyId, Guid advanceId)
    {
        var advance = await _db.SalaryAdvances
            .FirstOrDefaultAsync(a => a.Id == advanceId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการเงินทดรองจ่าย");

        if (advance.Status != "Draft")
            throw new InvalidOperationException("ส่งอนุมัติได้เฉพาะรายการที่เป็น Draft");

        advance.Status = "Submitted";
        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, advance.Id);
    }

    public async Task<SalaryAdvanceResponse> ApproveAsync(Guid companyId, Guid advanceId, Guid approverUserId, ApproveSalaryAdvanceRequest request)
    {
        var advance = await _db.SalaryAdvances
            .FirstOrDefaultAsync(a => a.Id == advanceId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการเงินทดรองจ่าย");

        if (advance.Status != "Submitted")
            throw new InvalidOperationException("อนุมัติได้เฉพาะรายการที่ Submitted");

        advance.Status = "Approved";
        advance.ApprovedByUserId = approverUserId;
        advance.ApprovedAt = DateTime.UtcNow;
        advance.ApprovalNotes = request.Notes;
        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, advance.Id);
    }

    public async Task<SalaryAdvanceResponse> RejectAsync(Guid companyId, Guid advanceId, Guid approverUserId, RejectSalaryAdvanceRequest request)
    {
        var advance = await _db.SalaryAdvances
            .FirstOrDefaultAsync(a => a.Id == advanceId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการเงินทดรองจ่าย");

        if (advance.Status != "Submitted")
            throw new InvalidOperationException("ปฏิเสธได้เฉพาะรายการที่ Submitted");

        advance.Status = "Rejected";
        advance.ApprovedByUserId = approverUserId;
        advance.ApprovedAt = DateTime.UtcNow;
        advance.RejectionReason = request.Reason;
        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, advance.Id);
    }

    public async Task<SalaryAdvanceResponse> DisburseAsync(
        Guid companyId, Guid advanceId, DisburseSalaryAdvanceRequest request, string disbursedBy)
    {
        var advance = await _db.SalaryAdvances
            .Include(a => a.Employee)
            .FirstOrDefaultAsync(a => a.Id == advanceId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการเงินทดรองจ่าย");

        if (advance.Status != "Approved")
            throw new InvalidOperationException("จ่ายเงินทดรองได้เฉพาะรายการที่อนุมัติแล้ว");

        var employee = advance.Employee;
        var contactId = await EnsureEmployeeContactAsync(companyId, employee);

        var advanceAccount = await FindAdvanceReceivableAccountAsync(companyId)
            ?? throw new InvalidOperationException(
                "ไม่พบบัญชี 'เงินทดรองจ่าย' / 'ลูกหนี้เงินทดรอง' (สินทรัพย์หมุนเวียน) ในผังบัญชี — กรุณาสร้างบัญชีก่อนจ่ายเงินทดรอง");

        var employeeName = $"{employee.TitleTh}{employee.FirstNameTh} {employee.LastNameTh}".Trim();
        var disbursementDate = request.DisbursementDate ?? DateTime.UtcNow.Date;

        // The PaymentVoucher's single line points at the Advance Receivable
        // account; DocumentService.AutoPostToJournalAsync debits each line's
        // account and credits cash → Dr Advance Receivable / Cr Cash, exactly
        // the entry Task 2 requires. No outer transaction here: ApproveDocument
        // runs its own execution-strategy transaction and nesting would throw.
        var createReq = new CreateDocumentRequest(
            DocumentType.PaymentVoucher,
            disbursementDate,
            null,
            contactId,
            advance.AdvanceNumber,
            $"เงินทดรองจ่ายพนักงาน {employeeName}" + (string.IsNullOrWhiteSpace(advance.Reason) ? "" : $" — {advance.Reason}"),
            new List<DocumentLineRequest>
            {
                new DocumentLineRequest(
                    $"เงินทดรองจ่าย - {employeeName}",
                    1m, null, advance.Amount, 0m, 0m, 0m,
                    advanceAccount.Id)
            },
            BankAccountId: request.BankAccountId);

        var doc = await _documentService.CreateDocumentAsync(companyId, createReq, disbursedBy);
        await _documentService.ApproveDocumentAsync(companyId, doc.Id, disbursedBy);

        advance.Status = "Disbursed";
        advance.DisbursedAt = disbursementDate;
        advance.DisbursementDocumentId = doc.Id;
        advance.ClearedAmount = 0m;
        advance.OutstandingAmount = advance.Amount;
        advance.UpdatedBy = disbursedBy;
        advance.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await GetByIdAsync(companyId, advance.Id);
    }

    public async Task VoidAsync(Guid companyId, Guid advanceId)
    {
        var advance = await _db.SalaryAdvances
            .FirstOrDefaultAsync(a => a.Id == advanceId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการเงินทดรองจ่าย");

        if (advance.Status == "Disbursed" || advance.Status == "Cleared")
            throw new InvalidOperationException(
                "ไม่สามารถยกเลิกรายการที่จ่ายเงินแล้วได้ — กรุณายกเลิกใบสำคัญจ่ายที่เกี่ยวข้องแทน");

        advance.Status = "Voided";
        advance.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<List<SalaryAdvance>> GetOutstandingForEmployeeAsync(Guid companyId, Guid employeeId)
    {
        return await _db.SalaryAdvances
            .Where(a => a.CompanyId == companyId
                && a.EmployeeId == employeeId
                && a.Status == "Disbursed"
                && a.OutstandingAmount > 0)
            .OrderBy(a => a.DisbursedAt)
            .ToListAsync();
    }

    public void ApplyRepayment(SalaryAdvance advance, decimal amount)
    {
        if (amount <= 0) return;
        var applied = Math.Min(amount, advance.OutstandingAmount);
        advance.ClearedAmount += applied;
        advance.OutstandingAmount -= applied;
        if (advance.OutstandingAmount <= 0.009m)
        {
            advance.OutstandingAmount = 0m;
            advance.Status = "Cleared";
        }
        advance.UpdatedAt = DateTime.UtcNow;
    }

    // ===== Helpers =====

    /// <summary>Mirror an employee as a Contact (Individual / supplier) so HR
    /// payments treat them as a valid payee in the core accounting system.
    /// Persists the link immediately so it can be used as a document ContactId.</summary>
    private async Task<Guid> EnsureEmployeeContactAsync(Guid companyId, Employee employee)
    {
        if (employee.ContactId.HasValue)
        {
            var exists = await _db.Set<Contact>()
                .AnyAsync(c => c.Id == employee.ContactId.Value && c.CompanyId == companyId);
            if (exists) return employee.ContactId.Value;
        }

        var fullName = $"{employee.TitleTh}{employee.FirstNameTh} {employee.LastNameTh}".Trim();
        var contact = new Contact
        {
            CompanyId = companyId,
            Name = string.IsNullOrWhiteSpace(fullName) ? employee.EmployeeCode : fullName,
            TaxId = employee.TaxId ?? employee.CitizenId,
            ContactType = ContactType.Individual,
            IsSupplier = true,
            IsCustomer = false,
            Address = employee.Address,
            Phone = employee.Phone,
            Email = employee.Email,
            IsActive = true,
            CreatedBy = "system:hr-sync"
        };
        _db.Set<Contact>().Add(contact);
        employee.ContactId = contact.Id;
        await _db.SaveChangesAsync();
        return contact.Id;
    }

    /// <summary>Locate the "Advance Receivable" current-asset account. Tries
    /// Thai name keywords first (เงินทดรอง / เงินยืมพนักงาน) then a 115x code
    /// prefix. Returns null when nothing suitable exists.</summary>
    private async Task<ChartOfAccount?> FindAdvanceReceivableAccountAsync(Guid companyId)
    {
        var byName = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                && (a.AccountName.Contains("ทดรอง") || a.AccountName.Contains("เงินยืมพนักงาน")))
            .OrderBy(a => a.AccountCode)
            .FirstOrDefaultAsync();
        if (byName != null) return byName;

        return await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                && a.AccountType == AccountType.Asset
                && a.AccountCode.StartsWith("115"))
            .OrderBy(a => a.AccountCode)
            .FirstOrDefaultAsync();
    }

    private async Task<string> NextAdvanceNumberAsync(Guid companyId)
    {
        var prefix = $"HR-ADV-{DateTime.UtcNow:yyyyMM}-";
        var max = await _db.SalaryAdvances
            .IgnoreQueryFilters()
            .Where(a => a.CompanyId == companyId && a.AdvanceNumber.StartsWith(prefix))
            .Select(a => a.AdvanceNumber)
            .MaxAsync() as string;
        var seq = 1;
        if (max != null)
        {
            var lastPart = max.Substring(prefix.Length);
            if (int.TryParse(lastPart, out var parsed)) seq = parsed + 1;
        }
        return $"{prefix}{seq:D4}";
    }

    private static SalaryAdvanceResponse MapToResponse(SalaryAdvance a) => new(
        a.Id,
        a.AdvanceNumber,
        a.EmployeeId,
        a.Employee?.EmployeeCode ?? "",
        a.Employee != null
            ? $"{a.Employee.TitleTh}{a.Employee.FirstNameTh} {a.Employee.LastNameTh}".Trim()
            : "",
        a.RequestDate,
        a.Amount,
        a.Reason,
        a.Status,
        a.MonthlyDeduction,
        a.ClearedAmount,
        a.OutstandingAmount,
        a.ApprovedAt,
        a.ApprovedByUser?.FullName,
        a.ApprovalNotes,
        a.RejectionReason,
        a.DisbursedAt,
        a.DisbursementDocumentId,
        a.CreatedAt);
}

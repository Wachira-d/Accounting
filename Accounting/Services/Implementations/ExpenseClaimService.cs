using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.DTOs.Expense;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ExpenseClaimService : IExpenseClaimService
{
    private readonly AccountingDbContext _db;
    private readonly IAccountingService? _accountingService;

    public ExpenseClaimService(AccountingDbContext db, IAccountingService? accountingService = null)
    {
        _db = db;
        _accountingService = accountingService;
    }

    public async Task<ExpenseClaimResponse> CreateAsync(Guid companyId, CreateExpenseClaimRequest request, Guid submittedByUserId)
    {
        var expYearMonth = DateTime.UtcNow.ToString("yyyyMM");
        var expPrefix = $"EXP-{expYearMonth}-";
        var maxExp = await _db.ExpenseClaims
            .IgnoreQueryFilters()
            .Where(e => e.CompanyId == companyId && e.ClaimNumber.StartsWith(expPrefix))
            .Select(e => e.ClaimNumber)
            .MaxAsync() as string;
        var expSeq = 1;
        if (maxExp != null)
        {
            var lastPart = maxExp.Substring(expPrefix.Length);
            if (int.TryParse(lastPart, out var parsed)) expSeq = parsed + 1;
        }
        var claimNumber = $"{expPrefix}{expSeq:D4}";

        var claim = new ExpenseClaim
        {
            CompanyId = companyId,
            ClaimNumber = claimNumber,
            Title = request.Title,
            Description = request.Description,
            ExpenseDate = request.ExpenseDate,
            SubmittedByUserId = submittedByUserId
        };

        var order = 1;
        foreach (var line in request.Lines)
        {
            claim.Lines.Add(new ExpenseClaimLine
            {
                LineOrder = order++,
                Description = line.Description,
                Amount = line.Amount,
                VatRate = line.VatRate,
                VatAmount = line.VatAmount,
                WithholdingTaxRate = line.WithholdingTaxRate,
                WithholdingTaxAmount = line.WithholdingTaxAmount,
                NetAmount = line.NetAmount,
                AccountId = line.AccountId,
                Category = line.Category,
                Reference = line.Reference
            });
        }

        claim.SubTotal = claim.Lines.Sum(l => l.Amount);
        claim.VatAmount = claim.Lines.Sum(l => l.VatAmount);
        claim.WithholdingTaxAmount = claim.Lines.Sum(l => l.WithholdingTaxAmount);
        claim.TotalAmount = claim.SubTotal + claim.VatAmount - claim.WithholdingTaxAmount;

        // Policy compliance checks
        ValidateExpensePolicy(claim);

        _db.ExpenseClaims.Add(claim);
        await _db.SaveChangesAsync();

        return await GetByIdAsync(companyId, claim.Id);
    }

    public async Task<ExpenseClaimResponse> GetByIdAsync(Guid companyId, Guid claimId)
    {
        var claim = await _db.ExpenseClaims
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .Include(e => e.SubmittedByUser)
            .Include(e => e.ApprovedByUser)
            .FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        return MapToResponse(claim);
    }

    public async Task<PagedResponse<ExpenseClaimResponse>> GetAllAsync(Guid companyId, ExpenseClaimStatus? status, PagedRequest request)
    {
        var query = _db.ExpenseClaims
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .Include(e => e.SubmittedByUser)
            .Include(e => e.ApprovedByUser)
            .Where(e => e.CompanyId == companyId);

        if (status.HasValue)
            query = query.Where(e => e.Status == status.Value);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(e => e.ClaimNumber.Contains(request.Search) || e.Title.Contains(request.Search));

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(e => e.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<ExpenseClaimResponse>(
            items.Select(MapToResponse).ToList(), total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<ExpenseClaimResponse> UpdateAsync(Guid companyId, Guid claimId, UpdateExpenseClaimRequest request)
    {
        var claim = await _db.ExpenseClaims
            .Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status != ExpenseClaimStatus.Draft)
            throw new InvalidOperationException("สามารถแก้ไขได้เฉพาะใบเบิกที่เป็น Draft เท่านั้น");

        if (request.Title != null) claim.Title = request.Title;
        if (request.Description != null) claim.Description = request.Description;
        if (request.ExpenseDate.HasValue) claim.ExpenseDate = request.ExpenseDate.Value;

        if (request.Lines != null)
        {
            _db.Set<ExpenseClaimLine>().RemoveRange(claim.Lines);
            claim.Lines.Clear();

            var order = 1;
            foreach (var line in request.Lines)
            {
                claim.Lines.Add(new ExpenseClaimLine
                {
                    ExpenseClaimId = claim.Id,
                    LineOrder = order++,
                    Description = line.Description,
                    Amount = line.Amount,
                    VatRate = line.VatRate,
                    VatAmount = line.VatAmount,
                    WithholdingTaxRate = line.WithholdingTaxRate,
                    WithholdingTaxAmount = line.WithholdingTaxAmount,
                    NetAmount = line.NetAmount,
                    AccountId = line.AccountId,
                    Category = line.Category,
                    Reference = line.Reference
                });
            }

            claim.SubTotal = claim.Lines.Sum(l => l.Amount);
            claim.VatAmount = claim.Lines.Sum(l => l.VatAmount);
            claim.WithholdingTaxAmount = claim.Lines.Sum(l => l.WithholdingTaxAmount);
            claim.TotalAmount = claim.SubTotal + claim.VatAmount - claim.WithholdingTaxAmount;
        }

        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, claim.Id);
    }

    public async Task<ExpenseClaimResponse> SubmitAsync(Guid companyId, Guid claimId)
    {
        var claim = await _db.ExpenseClaims.FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status != ExpenseClaimStatus.Draft)
            throw new InvalidOperationException("สามารถส่งอนุมัติได้เฉพาะใบเบิกที่เป็น Draft");

        claim.Status = ExpenseClaimStatus.Submitted;
        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, claim.Id);
    }

    public async Task<ExpenseClaimResponse> ApproveAsync(Guid companyId, Guid claimId, Guid approverUserId, ApproveExpenseClaimRequest request)
    {
        var claim = await _db.ExpenseClaims.FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status != ExpenseClaimStatus.Submitted)
            throw new InvalidOperationException("สามารถอนุมัติได้เฉพาะใบเบิกที่ Submitted");

        claim.Status = ExpenseClaimStatus.Approved;
        claim.ApprovedByUserId = approverUserId;
        claim.ApprovedAt = DateTime.UtcNow;
        claim.ApprovalNotes = request.Notes;
        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, claim.Id);
    }

    public async Task<ExpenseClaimResponse> RejectAsync(Guid companyId, Guid claimId, Guid approverUserId, RejectExpenseClaimRequest request)
    {
        var claim = await _db.ExpenseClaims.FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status != ExpenseClaimStatus.Submitted)
            throw new InvalidOperationException("สามารถปฏิเสธได้เฉพาะใบเบิกที่ Submitted");

        claim.Status = ExpenseClaimStatus.Rejected;
        claim.ApprovedByUserId = approverUserId;
        claim.ApprovedAt = DateTime.UtcNow;
        claim.RejectionReason = request.Reason;
        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, claim.Id);
    }

    public async Task<ExpenseClaimResponse> MarkAsPaidAsync(Guid companyId, Guid claimId, PayExpenseClaimRequest request)
    {
        var claim = await _db.ExpenseClaims
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status != ExpenseClaimStatus.Approved)
            throw new InvalidOperationException("สามารถจ่ายเงินได้เฉพาะใบเบิกที่ Approved");

        claim.Status = ExpenseClaimStatus.Paid;
        claim.PaidAt = DateTime.UtcNow;
        claim.PaidMethod = request.PaymentMethod;
        claim.PaidReference = request.Reference;
        await _db.SaveChangesAsync();

        // Create PV journal entry: Dr Expense accounts, Cr Cash
        if (_accountingService != null)
        {
            await CreateExpenseClaimJournalAsync(companyId, claim);
        }

        return await GetByIdAsync(companyId, claim.Id);
    }

    public async Task VoidAsync(Guid companyId, Guid claimId)
    {
        var claim = await _db.ExpenseClaims.FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status == ExpenseClaimStatus.Paid)
            throw new InvalidOperationException("ไม่สามารถยกเลิกใบเบิกที่จ่ายเงินแล้วได้");

        claim.Status = ExpenseClaimStatus.Voided;
        await _db.SaveChangesAsync();
    }

    public async Task<List<ExpenseClaimResponse>> GetMyClaimsAsync(Guid companyId, Guid userId)
    {
        var claims = await _db.ExpenseClaims
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .Include(e => e.SubmittedByUser)
            .Include(e => e.ApprovedByUser)
            .Where(e => e.CompanyId == companyId && e.SubmittedByUserId == userId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        return claims.Select(MapToResponse).ToList();
    }

    // Expense policy compliance
    private static readonly Dictionary<string, decimal> CategoryLimits = new()
    {
        { "transportation", 5000m },
        { "meal", 2000m },
        { "accommodation", 10000m },
        { "entertainment", 5000m },
        { "supplies", 3000m }
    };
    private const decimal MaxSingleClaimAmount = 100000m;
    private const decimal MaxSingleLineAmount = 50000m;

    private static void ValidateExpensePolicy(ExpenseClaim claim)
    {
        var violations = new List<string>();

        if (claim.TotalAmount > MaxSingleClaimAmount)
            violations.Add($"ยอดรวมเกินวงเงินสูงสุด ({MaxSingleClaimAmount:N0} บาท)");

        foreach (var line in claim.Lines)
        {
            if (line.Amount > MaxSingleLineAmount)
                violations.Add($"รายการ '{line.Description}' เกินวงเงินต่อรายการ ({MaxSingleLineAmount:N0} บาท)");

            if (!string.IsNullOrEmpty(line.Category) &&
                CategoryLimits.TryGetValue(line.Category.ToLowerInvariant(), out var limit) &&
                line.Amount > limit)
            {
                violations.Add($"รายการ '{line.Description}' เกินวงเงินหมวด {line.Category} ({limit:N0} บาท)");
            }
        }

        if (claim.ExpenseDate > DateTime.UtcNow.AddDays(1))
            violations.Add("วันที่ค่าใช้จ่ายไม่สามารถเป็นวันในอนาคตได้");

        if (claim.ExpenseDate < DateTime.UtcNow.AddDays(-90))
            violations.Add("ค่าใช้จ่ายเก่าเกิน 90 วัน ไม่สามารถเบิกได้");

        if (violations.Count > 0)
            throw new InvalidOperationException($"ไม่ผ่านนโยบายค่าใช้จ่าย: {string.Join("; ", violations)}");
    }

    /// <summary>
    /// สร้างรายการบันทึกบัญชี PV สำหรับการจ่ายเงินค่าใช้จ่าย
    /// Dr: บัญชีค่าใช้จ่าย (ตาม line items) + VAT Input (ถ้ามี)
    /// Cr: เงินสด/ธนาคาร (111101) + WHT ค้างจ่าย (ถ้ามี)
    /// </summary>
    private async Task CreateExpenseClaimJournalAsync(Guid companyId, ExpenseClaim claim)
    {
        var lines = new List<JournalLineRequest>();

        // Dr: แต่ละรายการค่าใช้จ่าย
        foreach (var line in claim.Lines)
        {
            if (line.AccountId.HasValue)
            {
                lines.Add(new JournalLineRequest(
                    line.AccountId.Value, line.Amount, 0,
                    $"ค่าใช้จ่าย - {line.Description}"));
            }
            else
            {
                // ถ้าไม่ได้ระบุบัญชี ใช้บัญชีค่าใช้จ่ายทั่วไป (529xxx)
                var defaultExpAccount = await FindAccountAsync(companyId, "549");
                if (defaultExpAccount != null)
                    lines.Add(new JournalLineRequest(
                        defaultExpAccount.Id, line.Amount, 0,
                        $"ค่าใช้จ่าย - {line.Description}"));
            }

            // Dr: VAT Input (ภาษีซื้อ)
            if (line.VatAmount > 0)
            {
                var vatInputAccount = await FindAccountAsync(companyId, "116");
                if (vatInputAccount != null)
                    lines.Add(new JournalLineRequest(
                        vatInputAccount.Id, line.VatAmount, 0, "ภาษีซื้อ"));
            }
        }

        // Cr: WHT ค้างจ่าย (ถ้ามี)
        if (claim.WithholdingTaxAmount > 0)
        {
            var whtAccount = await FindAccountAsync(companyId, "21916")
                ?? await FindAccountAsync(companyId, "21917");
            if (whtAccount != null)
                lines.Add(new JournalLineRequest(
                    whtAccount.Id, 0, claim.WithholdingTaxAmount, "ภาษีหัก ณ ที่จ่ายค้างจ่าย"));
        }

        // Cr: เงินสด/ธนาคาร — ยอดที่จ่ายจริง (TotalAmount ซึ่งหัก WHT ไว้แล้ว)
        var cashAccount = await FindAccountAsync(companyId, "111");
        if (cashAccount != null)
        {
            lines.Add(new JournalLineRequest(
                cashAccount.Id, 0, claim.TotalAmount,
                $"จ่ายเงินเบิกค่าใช้จ่าย - {claim.ClaimNumber}"));
        }

        if (lines.Count < 2) return;

        var totalDebit = lines.Sum(l => l.DebitAmount);
        var totalCredit = lines.Sum(l => l.CreditAmount);
        if (totalDebit != totalCredit)
            throw new InvalidOperationException(
                $"Journal entry unbalanced: Dr={totalDebit:N2} Cr={totalCredit:N2}");

        var journalRequest = new CreateJournalEntryRequest(
            DateTime.UtcNow,
            $"เบิกค่าใช้จ่าย {claim.ClaimNumber} - {claim.Title}",
            claim.ClaimNumber,
            lines,
            JournalType.CashPayments);

        await _accountingService!.CreateJournalEntryAsync(companyId, journalRequest, "system");
    }

    /// <summary>
    /// ค้นหาบัญชีจากรหัส — รองรับทั้งรหัส 4 หลัก (prefix) และ 6 หลัก (exact)
    /// </summary>
    private async Task<ChartOfAccount?> FindAccountAsync(Guid companyId, string codePrefix)
    {
        return await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == codePrefix)
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith(codePrefix) && a.Level >= 4)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();
    }

    private static ExpenseClaimResponse MapToResponse(ExpenseClaim e) => new(
        e.Id, e.ClaimNumber, e.Title, e.Description, e.ExpenseDate, e.Status,
        e.SubTotal, e.VatAmount, e.WithholdingTaxAmount, e.TotalAmount,
        e.SubmittedByUser?.FullName, e.SubmittedByUserId,
        e.ApprovedAt, e.ApprovedByUser?.FullName,
        e.PaidAt, e.PaidReference,
        e.Lines.OrderBy(l => l.LineOrder).Select(l => new ExpenseClaimLineResponse(
            l.Id, l.Description, l.Amount, l.VatRate, l.VatAmount,
            l.WithholdingTaxRate, l.WithholdingTaxAmount, l.NetAmount,
            l.AccountId, l.Account?.AccountName, l.Category, l.Reference)).ToList(),
        e.CreatedAt);
}

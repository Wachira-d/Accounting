using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Expense;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ExpenseClaimService : IExpenseClaimService
{
    private readonly AccountingDbContext _db;

    public ExpenseClaimService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<ExpenseClaimResponse> CreateAsync(Guid companyId, CreateExpenseClaimRequest request, Guid submittedByUserId)
    {
        var count = await _db.ExpenseClaims.CountAsync(e => e.CompanyId == companyId);
        var claimNumber = $"EXP-{DateTime.UtcNow:yyyyMM}-{(count + 1):D4}";

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
        var claim = await _db.ExpenseClaims.FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status != ExpenseClaimStatus.Approved)
            throw new InvalidOperationException("สามารถจ่ายเงินได้เฉพาะใบเบิกที่ Approved");

        claim.Status = ExpenseClaimStatus.Paid;
        claim.PaidAt = DateTime.UtcNow;
        claim.PaidMethod = request.PaymentMethod;
        claim.PaidReference = request.Reference;
        await _db.SaveChangesAsync();
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

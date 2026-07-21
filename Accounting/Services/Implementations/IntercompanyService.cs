using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Intercompany;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class IntercompanyService : IIntercompanyService
{
    private readonly AccountingDbContext _db;

    public IntercompanyService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<IntercompanyTxnResponse> CreateAsync(Guid companyId, CreateIntercompanyTxnRequest request, string createdBy)
    {
        // Generate transaction number
        var count = await _db.Set<IntercompanyTransaction>()
            .CountAsync(t => t.CompanyId == companyId);
        var txnNumber = $"IC-{DateTime.UtcNow:yyyyMM}-{(count + 1):D4}";

        var txn = new IntercompanyTransaction
        {
            CompanyId = companyId,
            SourceCompanyId = companyId,
            TargetCompanyId = request.TargetCompanyId,
            TransactionNumber = txnNumber,
            TransactionDate = request.TransactionDate,
            Description = request.Description,
            Amount = request.Amount,
            Currency = request.Currency ?? "THB",
            Status = IntercompanyStatus.Pending,
            CreatedBy = createdBy
        };

        _db.Set<IntercompanyTransaction>().Add(txn);

        var lineOrder = 1;
        foreach (var line in request.Lines)
        {
            var vatAmount = line.Amount * line.VatRate / 100m;
            _db.Set<IntercompanyTransactionLine>().Add(new IntercompanyTransactionLine
            {
                CompanyId = companyId,
                IntercompanyTransactionId = txn.Id,
                LineOrder = lineOrder++,
                Description = line.Description,
                SourceAccountId = line.SourceAccountId,
                TargetAccountId = line.TargetAccountId,
                Amount = line.Amount,
                VatRate = line.VatRate,
                VatAmount = vatAmount
            });
        }

        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, txn.Id);
    }

    public async Task<IntercompanyTxnResponse> GetByIdAsync(Guid companyId, Guid transactionId)
    {
        var txn = await _db.Set<IntercompanyTransaction>()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการระหว่างบริษัท");

        return MapToResponse(txn);
    }

    public async Task<PagedResponse<IntercompanyTxnResponse>> GetAllAsync(Guid companyId, IntercompanyStatus? status, PagedRequest request)
    {
        var query = _db.Set<IntercompanyTransaction>()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted);

        if (status.HasValue)
            query = query.Where(t => t.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(t => t.TransactionNumber.Contains(request.Search)
                || t.Description.Contains(request.Search));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(t => t.TransactionDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<IntercompanyTxnResponse>(
            items.Select(MapToResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<IntercompanyTxnResponse> ConfirmAsync(Guid companyId, Guid transactionId, string confirmedBy)
    {
        var txn = await _db.Set<IntercompanyTransaction>()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการระหว่างบริษัท");

        if (txn.Status == IntercompanyStatus.Pending)
            txn.Status = IntercompanyStatus.ConfirmedBySource;
        else if (txn.Status == IntercompanyStatus.ConfirmedBySource)
            txn.Status = IntercompanyStatus.Completed;
        else
            throw new InvalidOperationException($"ไม่สามารถยืนยันรายการในสถานะ {txn.Status}");

        txn.UpdatedBy = confirmedBy;
        txn.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapToResponse(txn);
    }

    public async Task VoidAsync(Guid companyId, Guid transactionId)
    {
        var txn = await _db.Set<IntercompanyTransaction>()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการระหว่างบริษัท");

        if (txn.Status == IntercompanyStatus.Completed)
            throw new InvalidOperationException("ไม่สามารถยกเลิกรายการที่เสร็จสมบูรณ์แล้ว");

        txn.Status = IntercompanyStatus.Voided;
        txn.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<List<IntercompanyBalanceResponse>> GetIntercompanyBalancesAsync(Guid companyId)
    {
        var transactions = await _db.Set<IntercompanyTransaction>()
            .Where(t => (t.SourceCompanyId == companyId || t.TargetCompanyId == companyId)
                && t.Status == IntercompanyStatus.Completed
                && !t.IsDeleted)
            .ToListAsync();

        var companies = await _db.Companies
            .Where(c => !c.IsDeleted)
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        var counterparties = transactions
            .Select(t => t.SourceCompanyId == companyId ? t.TargetCompanyId : t.SourceCompanyId)
            .Distinct();

        var result = new List<IntercompanyBalanceResponse>();
        foreach (var cpId in counterparties)
        {
            var receivable = transactions
                .Where(t => t.SourceCompanyId == companyId && t.TargetCompanyId == cpId)
                .Sum(t => t.Amount);

            var payable = transactions
                .Where(t => t.TargetCompanyId == companyId && t.SourceCompanyId == cpId)
                .Sum(t => t.Amount);

            var companyName = companies.GetValueOrDefault(cpId, "Unknown");

            result.Add(new IntercompanyBalanceResponse(
                cpId, companyName, receivable, payable, receivable - payable));
        }

        return result;
    }

    private static IntercompanyTxnResponse MapToResponse(IntercompanyTransaction t) =>
        new(t.Id, t.SourceCompanyId, t.TargetCompanyId, t.TransactionNumber,
            t.TransactionDate, t.Description, t.Amount, t.Status,
            t.IsEliminated, t.CreatedAt);
}

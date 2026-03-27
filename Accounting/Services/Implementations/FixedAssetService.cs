using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class FixedAssetService : IFixedAssetService
{
    private readonly AccountingDbContext _db;

    public FixedAssetService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<FixedAssetResponse> CreateAsync(Guid companyId, CreateFixedAssetRequest request, string createdBy)
    {
        var existing = await _db.FixedAssets.AnyAsync(a => a.CompanyId == companyId && a.AssetCode == request.AssetCode);
        if (existing)
            throw new InvalidOperationException($"รหัสสินทรัพย์ {request.AssetCode} ซ้ำ");

        var asset = new FixedAsset
        {
            CompanyId = companyId,
            AssetCode = request.AssetCode,
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Location = request.Location,
            SerialNumber = request.SerialNumber,
            PurchaseDate = request.PurchaseDate,
            PurchaseCost = request.PurchaseCost,
            SalvageValue = request.SalvageValue,
            UsefulLifeMonths = request.UsefulLifeMonths,
            DepreciationMethod = request.DepreciationMethod,
            NetBookValue = request.PurchaseCost,
            AssetAccountId = request.AssetAccountId,
            DepreciationExpenseAccountId = request.DepreciationExpenseAccountId,
            AccumulatedDepreciationAccountId = request.AccumulatedDepreciationAccountId,
            CreatedBy = createdBy
        };

        _db.FixedAssets.Add(asset);
        await _db.SaveChangesAsync();
        return MapToResponse(asset);
    }

    public async Task<FixedAssetResponse> GetByIdAsync(Guid companyId, Guid assetId)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");
        return MapToResponse(asset);
    }

    public async Task<PagedResponse<FixedAssetResponse>> GetAllAsync(Guid companyId, PagedRequest request)
    {
        var query = _db.FixedAssets.Where(a => a.CompanyId == companyId);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(a => a.Name.Contains(request.Search) || a.AssetCode.Contains(request.Search));

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(a => a.AssetCode)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<FixedAssetResponse>(
            items.Select(MapToResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<FixedAssetResponse> UpdateAsync(Guid companyId, Guid assetId, UpdateFixedAssetRequest request)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (request.Name != null) asset.Name = request.Name;
        if (request.Description != null) asset.Description = request.Description;
        if (request.Category != null) asset.Category = request.Category;
        if (request.Location != null) asset.Location = request.Location;
        if (request.SerialNumber != null) asset.SerialNumber = request.SerialNumber;
        if (request.AssetAccountId.HasValue) asset.AssetAccountId = request.AssetAccountId;
        if (request.DepreciationExpenseAccountId.HasValue) asset.DepreciationExpenseAccountId = request.DepreciationExpenseAccountId;
        if (request.AccumulatedDepreciationAccountId.HasValue) asset.AccumulatedDepreciationAccountId = request.AccumulatedDepreciationAccountId;

        await _db.SaveChangesAsync();
        return MapToResponse(asset);
    }

    public async Task<FixedAssetResponse> DisposeAsync(Guid companyId, Guid assetId, DisposeAssetRequest request, string performedBy)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (asset.Status != AssetStatus.Active && asset.Status != AssetStatus.FullyDepreciated)
            throw new InvalidOperationException("สินทรัพย์นี้ไม่สามารถจำหน่ายได้");

        asset.Status = AssetStatus.Disposed;
        asset.DisposalDate = request.DisposalDate;
        asset.DisposalAmount = request.DisposalAmount;

        // Calculate gain/loss on disposal
        var gainLoss = request.DisposalAmount - asset.NetBookValue;

        // Auto-create journal entry for disposal if accounts are configured
        if (asset.AssetAccountId.HasValue && asset.AccumulatedDepreciationAccountId.HasValue)
        {
            var entryNumber = $"DEP-DISP-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}";

            var journalEntry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = request.DisposalDate,
                JournalType = JournalType.General,
                Description = $"จำหน่ายสินทรัพย์: {asset.Name} ({asset.AssetCode})",
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                CreatedBy = performedBy
            };

            // Debit: Accumulated Depreciation (remove accumulated)
            journalEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = asset.AccumulatedDepreciationAccountId.Value,
                DebitAmount = asset.AccumulatedDepreciation,
                CreditAmount = 0,
                Description = $"ค่าเสื่อมราคาสะสม - {asset.Name}"
            });

            // Credit: Asset account (remove asset at cost)
            journalEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = asset.AssetAccountId.Value,
                DebitAmount = 0,
                CreditAmount = asset.PurchaseCost,
                Description = $"ตัดสินทรัพย์ - {asset.Name}"
            });

            // If disposal amount > 0, debit Bank/Cash
            if (request.DisposalAmount > 0)
            {
                // Use the first active bank account's linked account or a default
                var bankAccount = await _db.Set<BankAccount>()
                    .Where(b => b.CompanyId == companyId && b.IsActive && b.LinkedAccountId.HasValue)
                    .FirstOrDefaultAsync();

                if (bankAccount?.LinkedAccountId != null)
                {
                    journalEntry.Lines.Add(new JournalEntryLine
                    {
                        AccountId = bankAccount.LinkedAccountId.Value,
                        DebitAmount = request.DisposalAmount,
                        CreditAmount = 0,
                        Description = $"รับเงินจากจำหน่ายสินทรัพย์ - {asset.Name}"
                    });
                }
            }

            // Gain or loss balancing entry
            if (gainLoss != 0)
            {
                // For now, use the depreciation expense account as a proxy for gain/loss
                var balancingAccountId = asset.DepreciationExpenseAccountId ?? asset.AssetAccountId!.Value;
                if (gainLoss > 0)
                {
                    journalEntry.Lines.Add(new JournalEntryLine
                    {
                        AccountId = balancingAccountId,
                        DebitAmount = 0,
                        CreditAmount = gainLoss,
                        Description = $"กำไรจากการจำหน่ายสินทรัพย์ - {asset.Name}"
                    });
                }
                else
                {
                    journalEntry.Lines.Add(new JournalEntryLine
                    {
                        AccountId = balancingAccountId,
                        DebitAmount = Math.Abs(gainLoss),
                        CreditAmount = 0,
                        Description = $"ขาดทุนจากการจำหน่ายสินทรัพย์ - {asset.Name}"
                    });
                }
            }

            journalEntry.TotalDebit = journalEntry.Lines.Sum(l => l.DebitAmount);
            journalEntry.TotalCredit = journalEntry.Lines.Sum(l => l.CreditAmount);

            _db.JournalEntries.Add(journalEntry);
        }

        await _db.SaveChangesAsync();
        return MapToResponse(asset);
    }

    public async Task<List<DepreciationResponse>> GetDepreciationsAsync(Guid companyId, Guid assetId)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        var depreciations = await _db.AssetDepreciations
            .Where(d => d.FixedAssetId == assetId)
            .OrderBy(d => d.Year).ThenBy(d => d.Month)
            .ToListAsync();

        return depreciations.Select(d => new DepreciationResponse(
            d.Id, d.FixedAssetId, d.Year, d.Month,
            d.Amount, d.AccumulatedAmount, d.NetBookValue, d.IsPosted)).ToList();
    }

    public async Task<List<DepreciationResponse>> CalculateDepreciationAsync(
        Guid companyId, CalculateDepreciationRequest request, string performedBy)
    {
        var assets = await _db.FixedAssets
            .Where(a => a.CompanyId == companyId && a.Status == AssetStatus.Active)
            .ToListAsync();

        var results = new List<AssetDepreciation>();
        var journalLines = new List<JournalEntryLine>();

        foreach (var asset in assets)
        {
            var exists = await _db.AssetDepreciations
                .AnyAsync(d => d.FixedAssetId == asset.Id && d.Year == request.Year && d.Month == request.Month);
            if (exists) continue;

            // Check if asset purchase date is before this period
            var periodStart = new DateTime(request.Year, request.Month, 1);
            if (asset.PurchaseDate > periodStart) continue;

            var monthlyDepreciation = asset.DepreciationMethod switch
            {
                DepreciationMethod.StraightLine =>
                    (asset.PurchaseCost - asset.SalvageValue) / asset.UsefulLifeMonths,
                DepreciationMethod.DecliningBalance =>
                    asset.NetBookValue * (2.0m / asset.UsefulLifeMonths) / 2,
                DepreciationMethod.DoubleDecliningBalance =>
                    asset.NetBookValue * (2.0m / asset.UsefulLifeMonths),
                _ => 0
            };

            // Don't depreciate below salvage value
            if (asset.NetBookValue - monthlyDepreciation < asset.SalvageValue)
                monthlyDepreciation = asset.NetBookValue - asset.SalvageValue;

            if (monthlyDepreciation <= 0) continue;

            asset.AccumulatedDepreciation += monthlyDepreciation;
            asset.NetBookValue -= monthlyDepreciation;

            if (asset.NetBookValue <= asset.SalvageValue)
                asset.Status = AssetStatus.FullyDepreciated;

            var depreciation = new AssetDepreciation
            {
                CompanyId = companyId,
                FixedAssetId = asset.Id,
                Year = request.Year,
                Month = request.Month,
                Amount = monthlyDepreciation,
                AccumulatedAmount = asset.AccumulatedDepreciation,
                NetBookValue = asset.NetBookValue,
                CreatedBy = performedBy
            };

            _db.AssetDepreciations.Add(depreciation);
            results.Add(depreciation);

            // Collect journal entry lines for batch posting
            if (asset.DepreciationExpenseAccountId.HasValue && asset.AccumulatedDepreciationAccountId.HasValue)
            {
                journalLines.Add(new JournalEntryLine
                {
                    AccountId = asset.DepreciationExpenseAccountId.Value,
                    DebitAmount = monthlyDepreciation,
                    CreditAmount = 0,
                    Description = $"ค่าเสื่อมราคา {request.Month}/{request.Year} - {asset.Name}"
                });
                journalLines.Add(new JournalEntryLine
                {
                    AccountId = asset.AccumulatedDepreciationAccountId.Value,
                    DebitAmount = 0,
                    CreditAmount = monthlyDepreciation,
                    Description = $"ค่าเสื่อมราคาสะสม {request.Month}/{request.Year} - {asset.Name}"
                });
            }
        }

        // Create batch journal entry for all depreciation
        if (journalLines.Count > 0)
        {
            var entryNumber = $"JV-{request.Year}{request.Month:D2}-DEP{Guid.NewGuid().ToString()[..4].ToUpper()}";
            var journalEntry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = new DateTime(request.Year, request.Month, DateTime.DaysInMonth(request.Year, request.Month)),
                JournalType = JournalType.General,
                Description = $"ค่าเสื่อมราคาประจำเดือน {request.Month}/{request.Year}",
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                TotalDebit = journalLines.Sum(l => l.DebitAmount),
                TotalCredit = journalLines.Sum(l => l.CreditAmount),
                CreatedBy = performedBy
            };

            foreach (var line in journalLines)
            {
                line.JournalEntryId = journalEntry.Id;
                journalEntry.Lines.Add(line);
            }

            _db.JournalEntries.Add(journalEntry);
        }

        await _db.SaveChangesAsync();

        return results.Select(d => new DepreciationResponse(
            d.Id, d.FixedAssetId, d.Year, d.Month,
            d.Amount, d.AccumulatedAmount, d.NetBookValue, d.IsPosted)).ToList();
    }

    public async Task<RevaluationResponse> RevalueAsync(Guid companyId, Guid assetId, RevalueAssetRequest request, string performedBy)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (asset.Status != AssetStatus.Active)
            throw new InvalidOperationException("สามารถตีราคาใหม่ได้เฉพาะสินทรัพย์ที่ Active เท่านั้น");

        if (request.NewFairValue <= 0)
            throw new ArgumentException("มูลค่ายุติธรรมต้องมากกว่า 0");

        var oldNbv = asset.NetBookValue;
        var surplus = request.NewFairValue - oldNbv;

        // Update asset values
        asset.NetBookValue = request.NewFairValue;
        asset.PurchaseCost = asset.PurchaseCost + surplus;

        // Create revaluation journal entry
        if (asset.AssetAccountId.HasValue && surplus != 0)
        {
            var entryNumber = $"REVAL-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}";
            var journalEntry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = request.RevaluationDate,
                JournalType = JournalType.General,
                Description = $"ตีราคาสินทรัพย์ใหม่: {asset.Name} ({asset.AssetCode}) - {request.Notes}",
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                CreatedBy = performedBy
            };

            if (surplus > 0)
            {
                // Debit: Asset account, Credit: Revaluation surplus (use asset account as proxy)
                journalEntry.Lines.Add(new JournalEntryLine
                {
                    AccountId = asset.AssetAccountId.Value,
                    DebitAmount = surplus,
                    CreditAmount = 0,
                    Description = $"ตีราคาเพิ่ม - {asset.Name}"
                });
            }
            else
            {
                // Debit: Revaluation loss, Credit: Asset account
                journalEntry.Lines.Add(new JournalEntryLine
                {
                    AccountId = asset.AssetAccountId.Value,
                    DebitAmount = 0,
                    CreditAmount = Math.Abs(surplus),
                    Description = $"ตีราคาลด - {asset.Name}"
                });
            }

            journalEntry.TotalDebit = journalEntry.Lines.Sum(l => l.DebitAmount);
            journalEntry.TotalCredit = journalEntry.Lines.Sum(l => l.CreditAmount);
            _db.JournalEntries.Add(journalEntry);
        }

        await _db.SaveChangesAsync();

        return new RevaluationResponse(
            asset.Id, asset.AssetCode, asset.Name,
            oldNbv, request.NewFairValue, surplus, request.RevaluationDate);
    }

    private static FixedAssetResponse MapToResponse(FixedAsset a) =>
        new(a.Id, a.AssetCode, a.Name, a.Description, a.Category,
            a.Location, a.SerialNumber, a.PurchaseDate, a.PurchaseCost,
            a.SalvageValue, a.UsefulLifeMonths, a.DepreciationMethod,
            a.AccumulatedDepreciation, a.NetBookValue, a.Status,
            a.DisposalDate, a.DisposalAmount, a.CreatedAt);
}

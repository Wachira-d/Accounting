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

        var gainLoss = request.DisposalAmount - asset.NetBookValue;

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

            // Dr: ค่าเสื่อมราคาสะสม (ล้างยอดสะสม)
            journalEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = asset.AccumulatedDepreciationAccountId.Value,
                DebitAmount = asset.AccumulatedDepreciation,
                CreditAmount = 0,
                Description = $"ค่าเสื่อมราคาสะสม - {asset.Name}"
            });

            // Cr: สินทรัพย์ (ตัดออกราคาทุน)
            journalEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = asset.AssetAccountId.Value,
                DebitAmount = 0,
                CreditAmount = asset.PurchaseCost,
                Description = $"ตัดสินทรัพย์ - {asset.Name}"
            });

            // Dr: เงินสด/ธนาคาร (ถ้าขายได้เงิน)
            if (request.DisposalAmount > 0)
            {
                var cashAccount = await FindAccountAsync(companyId, "111");
                if (cashAccount != null)
                {
                    journalEntry.Lines.Add(new JournalEntryLine
                    {
                        AccountId = cashAccount.Id,
                        DebitAmount = request.DisposalAmount,
                        CreditAmount = 0,
                        Description = $"รับเงินจากจำหน่ายสินทรัพย์ - {asset.Name}"
                    });
                }
            }

            // กำไร/ขาดทุนจากการจำหน่าย
            if (gainLoss != 0)
            {
                if (gainLoss > 0)
                {
                    // Cr: กำไรจากการจำหน่ายสินทรัพย์ (รายได้อื่น 42xx)
                    var gainAccount = await FindAccountAsync(companyId, "43030")
                        ?? await FindAccountAsync(companyId, "430")
                        ?? await FindAccountAsync(companyId, "43");
                    if (gainAccount != null)
                    {
                        journalEntry.Lines.Add(new JournalEntryLine
                        {
                            AccountId = gainAccount.Id,
                            DebitAmount = 0,
                            CreditAmount = gainLoss,
                            Description = $"กำไรจากการจำหน่ายสินทรัพย์ - {asset.Name}"
                        });
                    }
                }
                else
                {
                    // Dr: ขาดทุนจากการจำหน่ายสินทรัพย์ (ค่าใช้จ่ายอื่น 54xx)
                    var lossAccount = await FindAccountAsync(companyId, "57110")
                        ?? await FindAccountAsync(companyId, "571")
                        ?? await FindAccountAsync(companyId, "57");
                    if (lossAccount != null)
                    {
                        journalEntry.Lines.Add(new JournalEntryLine
                        {
                            AccountId = lossAccount.Id,
                            DebitAmount = Math.Abs(gainLoss),
                            CreditAmount = 0,
                            Description = $"ขาดทุนจากการจำหน่ายสินทรัพย์ - {asset.Name}"
                        });
                    }
                }
            }

            journalEntry.TotalDebit = journalEntry.Lines.Sum(l => l.DebitAmount);
            journalEntry.TotalCredit = journalEntry.Lines.Sum(l => l.CreditAmount);

            _db.JournalEntries.Add(journalEntry);
        }

        await _db.SaveChangesAsync();
        return MapToResponse(asset);
    }

    public async Task<FixedAssetResponse> WriteOffAsync(Guid companyId, Guid assetId, WriteOffAssetRequest request, string performedBy)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (asset.Status != AssetStatus.Active && asset.Status != AssetStatus.FullyDepreciated)
            throw new InvalidOperationException("สินทรัพย์นี้ไม่สามารถตัดจำหน่ายได้");

        var remainingNBV = asset.NetBookValue;
        asset.Status = AssetStatus.WrittenOff;
        asset.DisposalDate = request.WriteOffDate;
        asset.DisposalAmount = 0;

        // Journal entry: ตัดจำหน่ายสินทรัพย์ (NBV เหลือ 0, ไม่ได้รับเงิน)
        if (asset.AssetAccountId.HasValue && asset.AccumulatedDepreciationAccountId.HasValue)
        {
            var entryNumber = $"WO-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}";

            var journalEntry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = request.WriteOffDate,
                JournalType = JournalType.General,
                Description = $"ตัดจำหน่ายสินทรัพย์: {asset.Name} ({asset.AssetCode}){(request.Reason != null ? $" - {request.Reason}" : "")}",
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                CreatedBy = performedBy
            };

            // Dr: ค่าเสื่อมราคาสะสม (ล้างยอดสะสม)
            journalEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = asset.AccumulatedDepreciationAccountId.Value,
                DebitAmount = asset.AccumulatedDepreciation,
                CreditAmount = 0,
                Description = $"ค่าเสื่อมราคาสะสม - {asset.Name}"
            });

            // Dr: ขาดทุนจากการตัดจำหน่าย (ส่วนที่ยังเหลือ NBV)
            if (remainingNBV > 0)
            {
                var lossAccount = await FindAccountAsync(companyId, "57110")
                    ?? await FindAccountAsync(companyId, "571")
                    ?? await FindAccountAsync(companyId, "57");
                if (lossAccount != null)
                {
                    journalEntry.Lines.Add(new JournalEntryLine
                    {
                        AccountId = lossAccount.Id,
                        DebitAmount = remainingNBV,
                        CreditAmount = 0,
                        Description = $"ขาดทุนจากการตัดจำหน่ายสินทรัพย์ - {asset.Name}"
                    });
                }
            }

            // Cr: สินทรัพย์ (ตัดออกราคาทุน)
            journalEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = asset.AssetAccountId.Value,
                DebitAmount = 0,
                CreditAmount = asset.PurchaseCost,
                Description = $"ตัดสินทรัพย์ - {asset.Name}"
            });

            journalEntry.TotalDebit = journalEntry.Lines.Sum(l => l.DebitAmount);
            journalEntry.TotalCredit = journalEntry.Lines.Sum(l => l.CreditAmount);
            _db.JournalEntries.Add(journalEntry);
        }

        // Update asset NBV
        asset.AccumulatedDepreciation = asset.PurchaseCost;
        asset.NetBookValue = 0;

        await _db.SaveChangesAsync();
        return MapToResponse(asset);
    }

    public async Task<FixedAssetResponse> AdjustUsefulLifeAsync(Guid companyId, Guid assetId, AdjustUsefulLifeRequest request)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (asset.Status != AssetStatus.Active)
            throw new InvalidOperationException("สามารถปรับอายุการใช้งานได้เฉพาะสินทรัพย์ที่ Active เท่านั้น");

        if (request.NewUsefulLifeMonths <= 0)
            throw new ArgumentException("อายุการใช้งานต้องมากกว่า 0 เดือน");

        asset.UsefulLifeMonths = request.NewUsefulLifeMonths;
        if (request.NewSalvageValue.HasValue)
        {
            if (request.NewSalvageValue.Value < 0)
                throw new ArgumentException("มูลค่าซากต้องไม่ติดลบ");
            asset.SalvageValue = request.NewSalvageValue.Value;
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

        asset.NetBookValue = request.NewFairValue;
        asset.PurchaseCost = asset.PurchaseCost + surplus;

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

    public async Task<List<AssetCategoryResponse>> GetCategoriesAsync(Guid companyId)
    {
        var assets = await _db.FixedAssets
            .Where(a => a.CompanyId == companyId)
            .ToListAsync();

        return assets
            .GroupBy(a => a.Category ?? "ไม่ระบุหมวดหมู่")
            .Select(g => new AssetCategoryResponse(
                g.Key,
                g.Count(),
                g.Sum(a => a.PurchaseCost),
                g.Sum(a => a.NetBookValue)))
            .OrderBy(c => c.Category)
            .ToList();
    }

    public async Task<AssetRegisterReport> GetAssetRegisterReportAsync(Guid companyId)
    {
        var assets = await _db.FixedAssets
            .Where(a => a.CompanyId == companyId)
            .OrderBy(a => a.AssetCode)
            .ToListAsync();

        var items = assets.Select(a => new AssetRegisterReportItem(
            a.AssetCode, a.Name, a.Category, a.Location,
            a.PurchaseDate, a.PurchaseCost, a.SalvageValue,
            a.UsefulLifeMonths, a.DepreciationMethod.ToString(),
            a.AccumulatedDepreciation, a.NetBookValue,
            a.Status.ToString(), a.DisposalDate, a.DisposalAmount)).ToList();

        return new AssetRegisterReport(
            DateTime.UtcNow,
            items,
            assets.Sum(a => a.PurchaseCost),
            assets.Sum(a => a.AccumulatedDepreciation),
            assets.Sum(a => a.NetBookValue),
            assets.Count(a => a.Status == AssetStatus.Active),
            assets.Count(a => a.Status == AssetStatus.Disposed || a.Status == AssetStatus.WrittenOff),
            assets.Count(a => a.Status == AssetStatus.FullyDepreciated));
    }

    public async Task<DepreciationScheduleReport> GetDepreciationScheduleAsync(Guid companyId, Guid assetId)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        var schedule = new List<DepreciationScheduleItem>();
        var nbv = asset.PurchaseCost;
        var accumulated = 0m;
        var startDate = asset.PurchaseDate;

        for (int i = 0; i < asset.UsefulLifeMonths; i++)
        {
            var periodDate = startDate.AddMonths(i + 1);
            var openingNBV = nbv;

            var depAmount = asset.DepreciationMethod switch
            {
                DepreciationMethod.StraightLine =>
                    (asset.PurchaseCost - asset.SalvageValue) / asset.UsefulLifeMonths,
                DepreciationMethod.DecliningBalance =>
                    nbv * (2.0m / asset.UsefulLifeMonths) / 2,
                DepreciationMethod.DoubleDecliningBalance =>
                    nbv * (2.0m / asset.UsefulLifeMonths),
                _ => 0
            };

            if (nbv - depAmount < asset.SalvageValue)
                depAmount = nbv - asset.SalvageValue;

            if (depAmount <= 0) break;

            accumulated += depAmount;
            nbv -= depAmount;

            schedule.Add(new DepreciationScheduleItem(
                periodDate.Year, periodDate.Month,
                openingNBV, depAmount, accumulated, nbv));

            if (nbv <= asset.SalvageValue) break;
        }

        return new DepreciationScheduleReport(
            asset.Id, asset.AssetCode, asset.Name,
            asset.PurchaseCost, asset.SalvageValue,
            asset.UsefulLifeMonths, asset.DepreciationMethod.ToString(),
            schedule);
    }

    public async Task<ImportFixedAssetsResult> ImportAsync(Guid companyId, List<ImportFixedAssetRow> rows, string createdBy)
    {
        var errors = new List<string>();
        var successCount = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNum = i + 1;

            if (string.IsNullOrWhiteSpace(row.AssetCode))
            {
                errors.Add($"แถวที่ {rowNum}: รหัสสินทรัพย์ว่างเปล่า");
                continue;
            }
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                errors.Add($"แถวที่ {rowNum}: ชื่อสินทรัพย์ว่างเปล่า");
                continue;
            }
            if (row.PurchaseCost <= 0)
            {
                errors.Add($"แถวที่ {rowNum}: ราคาทุนต้องมากกว่า 0");
                continue;
            }

            var exists = await _db.FixedAssets.AnyAsync(a => a.CompanyId == companyId && a.AssetCode == row.AssetCode);
            if (exists)
            {
                errors.Add($"แถวที่ {rowNum}: รหัสสินทรัพย์ {row.AssetCode} ซ้ำ");
                continue;
            }

            var method = row.DepreciationMethod?.ToLower() switch
            {
                "decliningbalance" or "declining" or "ยอดลดลง" => DepreciationMethod.DecliningBalance,
                "doubledecliningbalance" or "doubledeclining" or "ยอดลดลงทวีคูณ" => DepreciationMethod.DoubleDecliningBalance,
                _ => DepreciationMethod.StraightLine
            };

            _db.FixedAssets.Add(new FixedAsset
            {
                CompanyId = companyId,
                AssetCode = row.AssetCode,
                Name = row.Name,
                Category = row.Category,
                Location = row.Location,
                SerialNumber = row.SerialNumber,
                PurchaseDate = row.PurchaseDate,
                PurchaseCost = row.PurchaseCost,
                SalvageValue = row.SalvageValue,
                UsefulLifeMonths = row.UsefulLifeMonths > 0 ? row.UsefulLifeMonths : 60,
                DepreciationMethod = method,
                NetBookValue = row.PurchaseCost,
                CreatedBy = createdBy
            });
            successCount++;
        }

        if (successCount > 0)
            await _db.SaveChangesAsync();

        return new ImportFixedAssetsResult(rows.Count, successCount, errors.Count, errors);
    }

    private async Task<ChartOfAccount?> FindAccountAsync(Guid companyId, string codePrefix)
    {
        return await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == codePrefix)
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith(codePrefix) && a.Level >= 4)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();
    }

    private static FixedAssetResponse MapToResponse(FixedAsset a) =>
        new(a.Id, a.AssetCode, a.Name, a.Description, a.Category,
            a.Location, a.SerialNumber, a.PurchaseDate, a.PurchaseCost,
            a.SalvageValue, a.UsefulLifeMonths, a.DepreciationMethod,
            a.AccumulatedDepreciation, a.NetBookValue, a.Status,
            a.DisposalDate, a.DisposalAmount, a.CreatedAt);
}

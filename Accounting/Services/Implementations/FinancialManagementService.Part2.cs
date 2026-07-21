using Accounting.Data;
using Accounting.Models.DTOs.FinancialManagement;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

// Partial class for features 6-9
public partial class FinancialManagementService
{
    // ==================== 6. CORPORATE INCOME TAX ====================

    public async Task<CITResponse> CalculateCITAsync(Guid companyId, CalculateCITRequest request, string userId)
    {
        // Calculate revenue & expenses from journal entries for the tax year
        var taxYear = int.Parse(request.TaxYear);
        var ceYear = taxYear > 2400 ? taxYear - 543 : taxYear;
        var yearStart = new DateTime(ceYear, 1, 1);
        var yearEnd = yearStart.AddYears(1);

        if (request.TaxPeriod == "HalfYear")
        {
            yearEnd = yearStart.AddMonths(6);
        }

        var revenue = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= yearStart && j.EntryDate < yearEnd)
            .SelectMany(j => j.Lines)
            .Where(l => l.Account.AccountType == AccountType.Revenue)
            .SumAsync(l => l.CreditAmount - l.DebitAmount);

        var expenses = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= yearStart && j.EntryDate < yearEnd)
            .SelectMany(j => j.Lines)
            .Where(l => l.Account.AccountType == AccountType.Expense)
            .SumAsync(l => l.DebitAmount - l.CreditAmount);

        var accountingProfit = revenue - expenses;
        var addBack = request.AddBackItems ?? 0;
        var deductions = request.DeductionItems ?? 0;
        var taxableProfit = accountingProfit + addBack - deductions;

        // Thai SME CIT progressive rates: first 300K exempt, 300K-3M at 15%, above 3M at 20%
        decimal taxRate = 20;
        decimal taxAmount;
        if (taxableProfit <= 0)
        {
            taxAmount = 0;
        }
        else
        {
            // SME simplified: first 300K exempt, 300K-3M at 15%, above 3M at 20%
            taxAmount = taxableProfit <= 300000 ? 0
                : taxableProfit <= 3000000 ? (taxableProfit - 300000) * 0.15m
                : 300000 * 0 + 2700000 * 0.15m + (taxableProfit - 3000000) * 0.20m;
        }

        var whtCredit = request.WithholdingTaxCredit ?? 0;
        var prepaidCredit = request.PrepaidTaxCredit ?? 0;
        var netPayable = Math.Max(0, taxAmount - whtCredit - prepaidCredit);

        var entity = new CorporateIncomeTax
        {
            CompanyId = companyId, TaxYear = request.TaxYear, TaxPeriod = request.TaxPeriod,
            TotalRevenue = revenue, TotalExpenses = expenses, AccountingProfit = accountingProfit,
            AddBackItems = addBack, DeductionItems = deductions, TaxableProfit = taxableProfit,
            TaxRate = taxRate, TaxAmount = Math.Round(taxAmount, 2, MidpointRounding.AwayFromZero),
            WithholdingTaxCredit = whtCredit, PrepaidTaxCredit = prepaidCredit,
            NetTaxPayable = Math.Round(netPayable, 2, MidpointRounding.AwayFromZero),
            Notes = request.Notes, CreatedBy = userId
        };

        _db.Set<CorporateIncomeTax>().Add(entity);
        await _db.SaveChangesAsync();
        return MapCIT(entity);
    }

    public async Task<List<CITResponse>> GetCITListAsync(Guid companyId)
    {
        var items = await _db.Set<CorporateIncomeTax>()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .OrderByDescending(c => c.TaxYear).ThenByDescending(c => c.TaxPeriod)
            .ToListAsync();
        return items.Select(MapCIT).ToList();
    }

    public async Task<CITResponse> PostCITAsync(Guid companyId, Guid id, string userId)
    {
        var entity = await _db.Set<CorporateIncomeTax>()
            .FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล");

        if (entity.Status == "Filed") throw new InvalidOperationException("ยื่นแล้ว");
        if (entity.TaxAmount <= 0) { entity.Status = "Filed"; await _db.SaveChangesAsync(); return MapCIT(entity); }

        // Dr 58000 (CIT Expense) / Cr 21920 (CIT Payable)
        var citExpenseAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("580") && a.IsActive);
        var citPayableAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("2192") && a.IsActive);

        if (citExpenseAcc != null && citPayableAcc != null)
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);

            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                JournalType = JournalType.General,
                Description = $"ภาษีเงินได้นิติบุคคล {entity.TaxYear} ({entity.TaxPeriod})",
                Reference = $"CIT-{entity.TaxYear}-{entity.TaxPeriod}",
                Status = JournalEntryStatus.Posted, IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = entity.TaxAmount, TotalCredit = entity.TaxAmount,
                Lines = new List<JournalEntryLine>
                {
                    new() { AccountId = citExpenseAcc.Id, DebitAmount = entity.TaxAmount, Description = "ภาษีเงินได้นิติบุคคล", LineOrder = 1 },
                    new() { AccountId = citPayableAcc.Id, CreditAmount = entity.TaxAmount, Description = "ภาษีเงินได้ค้างจ่าย", LineOrder = 2 }
                }
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.JournalEntryId = je.Id;
        }

        entity.Status = "Filed";
        await _db.SaveChangesAsync();
        return MapCIT(entity);
    }

    private static CITResponse MapCIT(CorporateIncomeTax c) => new(
        c.Id, c.TaxYear, c.TaxPeriod, c.TotalRevenue, c.TotalExpenses, c.AccountingProfit,
        c.AddBackItems, c.DeductionItems, c.TaxableProfit, c.TaxRate, c.TaxAmount,
        c.WithholdingTaxCredit, c.PrepaidTaxCredit, c.NetTaxPayable,
        c.Status, c.Notes, c.JournalEntryId);

    // ==================== 7. PROFIT APPROPRIATION ====================

    public async Task<ProfitAppropriationResponse> CreateProfitAppropriationAsync(Guid companyId, CreateProfitAppropriationRequest request, string userId)
    {
        var refNo = $"DIV-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..4].ToUpper()}";

        // Get net profit from retained earnings
        var retainedEarningsAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("3202") && a.IsActive);

        decimal netProfit = 0;
        if (retainedEarningsAcc != null)
        {
            netProfit = await _db.JournalEntries
                .Where(j => j.CompanyId == companyId && j.Status == JournalEntryStatus.Posted)
                .SelectMany(j => j.Lines)
                .Where(l => l.AccountId == retainedEarningsAcc.Id)
                .SumAsync(l => l.CreditAmount - l.DebitAmount);
        }

        var retained = netProfit - request.LegalReserve - request.DividendAmount;

        var entity = new ProfitAppropriation
        {
            CompanyId = companyId, ReferenceNo = refNo, ApprovalDate = DateTime.UtcNow,
            FiscalYear = request.FiscalYear, NetProfit = netProfit,
            LegalReserve = request.LegalReserve, DividendAmount = request.DividendAmount,
            RetainedAmount = retained, DividendPerShare = request.DividendPerShare,
            Notes = request.Notes, CreatedBy = userId
        };

        _db.Set<ProfitAppropriation>().Add(entity);
        await _db.SaveChangesAsync();
        return MapAppropriation(entity);
    }

    public async Task<List<ProfitAppropriationResponse>> GetProfitAppropriationsAsync(Guid companyId)
    {
        var items = await _db.Set<ProfitAppropriation>()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted)
            .OrderByDescending(p => p.ApprovalDate).ToListAsync();
        return items.Select(MapAppropriation).ToList();
    }

    public async Task<ProfitAppropriationResponse> ApproveProfitAppropriationAsync(Guid companyId, Guid id, string userId)
    {
        var entity = await _db.Set<ProfitAppropriation>()
            .FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล");

        if (entity.Status != "Draft") throw new InvalidOperationException("สถานะไม่ถูกต้อง");

        var retainedAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("3202") && a.IsActive);
        var reserveAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("3201") && a.IsActive);
        var cashAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.IsActive);

        var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);

        // 1. Legal reserve: Dr กำไรสะสม 32020 / Cr สำรองฯ 32010
        if (entity.LegalReserve > 0 && retainedAcc != null && reserveAcc != null)
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                JournalType = JournalType.General,
                Description = $"จัดสรรสำรองตามกฎหมาย ปี {entity.FiscalYear}",
                Reference = entity.ReferenceNo, Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = entity.LegalReserve, TotalCredit = entity.LegalReserve,
                Lines = new List<JournalEntryLine>
                {
                    new() { AccountId = retainedAcc.Id, DebitAmount = entity.LegalReserve, Description = "จัดสรรกำไรสะสม", LineOrder = 1 },
                    new() { AccountId = reserveAcc.Id, CreditAmount = entity.LegalReserve, Description = "สำรองตามกฎหมาย", LineOrder = 2 }
                }
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.ReserveJournalId = je.Id;
        }

        // 2. Dividend declaration: Dr กำไรสะสม 32020 / Cr เงินปันผลค้างจ่าย 22xxx (not direct to cash)
        if (entity.DividendAmount > 0 && retainedAcc != null)
        {
            var divPayableAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == "22010" && a.Level >= 4)
                ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode.StartsWith("220") && a.Level >= 4);
            var creditAcc = divPayableAcc ?? cashAcc;
            if (creditAcc != null)
            {
                var jeNum = await NextJvNumberAsync(companyId);
                var je = new JournalEntry
                {
                    CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                    JournalType = JournalType.General,
                    Description = divPayableAcc != null
                        ? $"ประกาศจ่ายเงินปันผล ปี {entity.FiscalYear}"
                        : $"จ่ายเงินปันผล ปี {entity.FiscalYear}",
                    Reference = entity.ReferenceNo, Status = JournalEntryStatus.Posted,
                    IsAutoGenerated = true, FiscalPeriodId = fpId,
                    TotalDebit = entity.DividendAmount, TotalCredit = entity.DividendAmount,
                    Lines = new List<JournalEntryLine>
                    {
                        new() { AccountId = retainedAcc.Id, DebitAmount = entity.DividendAmount, Description = "จ่ายเงินปันผล", LineOrder = 1 },
                        new() { AccountId = creditAcc.Id, CreditAmount = entity.DividendAmount, Description = divPayableAcc != null ? "เงินปันผลค้างจ่าย" : "จ่ายเงินปันผล", LineOrder = 2 }
                    }
                };
                _db.JournalEntries.Add(je);
                await _db.SaveChangesAsync();
                entity.DividendJournalId = je.Id;
            }
        }

        entity.Status = "Distributed";
        await _db.SaveChangesAsync();
        return MapAppropriation(entity);
    }

    private static ProfitAppropriationResponse MapAppropriation(ProfitAppropriation p) => new(
        p.Id, p.ReferenceNo, p.ApprovalDate, p.FiscalYear,
        p.NetProfit, p.LegalReserve, p.DividendAmount, p.RetainedAmount, p.DividendPerShare,
        p.Status, p.Notes, p.ReserveJournalId, p.DividendJournalId);

    // ==================== 8. CAPITAL TRANSACTIONS ====================

    public async Task<CapitalTransactionResponse> CreateCapitalTransactionAsync(Guid companyId, CreateCapitalTransactionRequest request, string userId)
    {
        var refNo = $"CAP-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..4].ToUpper()}";
        var premium = request.PaidAmount - (request.ShareQuantity * request.ParValue);

        var entity = new CapitalTransaction
        {
            CompanyId = companyId, ReferenceNo = refNo, TransactionDate = request.TransactionDate ?? DateTime.UtcNow,
            TransactionType = request.TransactionType,
            ShareQuantity = request.ShareQuantity, ParValue = request.ParValue,
            PaidAmount = request.PaidAmount, SharePremium = premium,
            BoardResolutionRef = request.BoardResolutionRef,
            DbrRegistrationRef = request.DbrRegistrationRef,
            Notes = request.Notes, CreatedBy = userId
        };

        _db.Set<CapitalTransaction>().Add(entity);
        await _db.SaveChangesAsync();
        return MapCapital(entity);
    }

    public async Task<List<CapitalTransactionResponse>> GetCapitalTransactionsAsync(Guid companyId)
    {
        var items = await _db.Set<CapitalTransaction>()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .OrderByDescending(c => c.TransactionDate).ToListAsync();
        return items.Select(MapCapital).ToList();
    }

    public async Task<CapitalTransactionResponse> CompleteCapitalTransactionAsync(Guid companyId, Guid id, string userId)
    {
        var entity = await _db.Set<CapitalTransaction>()
            .FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล");

        if (entity.Status == "Completed") throw new InvalidOperationException("ดำเนินการแล้ว");

        var cashAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.IsActive);
        var capitalAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("3102") && a.IsActive); // ทุนชำระแล้ว
        var premiumAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("3103") && a.IsActive); // ส่วนเกินมูลค่าหุ้น

        if (cashAcc != null && capitalAcc != null)
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);
            var lines = new List<JournalEntryLine>();
            var capitalAmount = entity.ShareQuantity * entity.ParValue;

            if (entity.TransactionType == "Increase")
            {
                // Dr Cash / Cr Capital + Premium
                lines.Add(new() { AccountId = cashAcc.Id, DebitAmount = entity.PaidAmount, Description = "รับเงินเพิ่มทุน", LineOrder = 1 });
                lines.Add(new() { AccountId = capitalAcc.Id, CreditAmount = capitalAmount, Description = "เพิ่มทุนชำระแล้ว", LineOrder = 2 });
                if (entity.SharePremium > 0 && premiumAcc != null)
                    lines.Add(new() { AccountId = premiumAcc.Id, CreditAmount = entity.SharePremium, Description = "ส่วนเกินมูลค่าหุ้น", LineOrder = 3 });
            }
            else // Decrease
            {
                lines.Add(new() { AccountId = capitalAcc.Id, DebitAmount = capitalAmount, Description = "ลดทุนชำระแล้ว", LineOrder = 1 });
                lines.Add(new() { AccountId = cashAcc.Id, CreditAmount = entity.PaidAmount, Description = "จ่ายคืนทุน", LineOrder = 2 });
            }

            var totalDr = lines.Sum(l => l.DebitAmount);
            var totalCr = lines.Sum(l => l.CreditAmount);

            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                JournalType = JournalType.General,
                Description = $"{(entity.TransactionType == "Increase" ? "เพิ่มทุน" : "ลดทุน")} {entity.ShareQuantity:N0} หุ้น",
                Reference = entity.ReferenceNo, Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = totalDr, TotalCredit = totalCr, Lines = lines
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.JournalEntryId = je.Id;
        }

        entity.Status = "Completed";
        await _db.SaveChangesAsync();
        return MapCapital(entity);
    }

    private static CapitalTransactionResponse MapCapital(CapitalTransaction c) => new(
        c.Id, c.ReferenceNo, c.TransactionDate, c.TransactionType,
        c.ShareQuantity, c.ParValue, c.PaidAmount, c.SharePremium,
        c.Status, c.BoardResolutionRef, c.DbrRegistrationRef, c.Notes, c.JournalEntryId);

    // ==================== 9. SHORT-TERM INVESTMENT ====================

    public async Task<InvestmentResponse> CreateInvestmentAsync(Guid companyId, CreateInvestmentRequest request, string userId)
    {
        var refNo = $"INV-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..4].ToUpper()}";

        var entity = new ShortTermInvestment
        {
            CompanyId = companyId, ReferenceNo = refNo, InvestmentType = request.InvestmentType,
            Description = request.Description, PurchaseDate = request.PurchaseDate,
            MaturityDate = request.MaturityDate, PurchaseCost = request.PurchaseCost,
            CurrentValue = request.PurchaseCost, InterestRate = request.InterestRate,
            InvestmentAccountId = request.InvestmentAccountId,
            InstitutionName = request.InstitutionName, AccountNumber = request.AccountNumber,
            CreatedBy = userId
        };

        // Auto journal: Dr Investment 112xx / Cr Cash 111xx
        var cashAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.IsActive);

        if (cashAcc != null)
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var fpId = await GetFiscalPeriodIdAsync(companyId, request.PurchaseDate);

            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = request.PurchaseDate,
                JournalType = JournalType.General,
                Description = $"ซื้อเงินลงทุนชั่วคราว: {request.Description}",
                Reference = refNo, Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = request.PurchaseCost, TotalCredit = request.PurchaseCost,
                Lines = new List<JournalEntryLine>
                {
                    new() { AccountId = request.InvestmentAccountId, DebitAmount = request.PurchaseCost, Description = $"เงินลงทุน - {request.Description}", LineOrder = 1 },
                    new() { AccountId = cashAcc.Id, CreditAmount = request.PurchaseCost, Description = "จ่ายเงินซื้อลงทุน", LineOrder = 2 }
                }
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.PurchaseJournalId = je.Id;
        }

        _db.Set<ShortTermInvestment>().Add(entity);
        await _db.SaveChangesAsync();
        return MapInvestment(entity);
    }

    public async Task<List<InvestmentResponse>> GetInvestmentsAsync(Guid companyId)
    {
        var items = await _db.Set<ShortTermInvestment>()
            .Include(i => i.InvestmentAccount)
            .Where(i => i.CompanyId == companyId && !i.IsDeleted)
            .OrderByDescending(i => i.PurchaseDate).ToListAsync();
        return items.Select(MapInvestment).ToList();
    }

    public async Task<InvestmentResponse> SellInvestmentAsync(Guid companyId, Guid id, SellInvestmentRequest request, string userId)
    {
        var entity = await _db.Set<ShortTermInvestment>()
            .Include(i => i.InvestmentAccount)
            .FirstOrDefaultAsync(i => i.Id == id && i.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล");

        if (entity.Status != "Active") throw new InvalidOperationException("สถานะไม่ถูกต้อง");

        entity.SaleDate = request.SaleDate ?? DateTime.UtcNow;
        entity.SaleProceeds = request.SaleProceeds;
        entity.GainLoss = request.SaleProceeds - entity.PurchaseCost;
        entity.Status = "Sold";

        // Journal: Dr Cash / Cr Investment + Gain or Loss
        var cashAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.IsActive);

        if (cashAcc != null)
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);
            var lines = new List<JournalEntryLine>
            {
                new() { AccountId = cashAcc.Id, DebitAmount = request.SaleProceeds, Description = "รับเงินจากขายเงินลงทุน", LineOrder = 1 },
                new() { AccountId = entity.InvestmentAccountId, CreditAmount = entity.PurchaseCost, Description = $"ขายเงินลงทุน - {entity.Description}", LineOrder = 2 }
            };

            if (entity.GainLoss > 0) // Gain
            {
                var gainAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode.StartsWith("4303") && a.IsActive); // กำไรจากขายสินทรัพย์
                if (gainAcc != null)
                    lines.Add(new() { AccountId = gainAcc.Id, CreditAmount = entity.GainLoss.Value, Description = "กำไรจากขายเงินลงทุน", LineOrder = 3 });
            }
            else if (entity.GainLoss < 0) // Loss
            {
                var lossAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode.StartsWith("5711") && a.IsActive); // ขาดทุน
                if (lossAcc != null)
                    lines.Add(new() { AccountId = lossAcc.Id, DebitAmount = Math.Abs(entity.GainLoss.Value), Description = "ขาดทุนจากขายเงินลงทุน", LineOrder = 3 });
            }

            var totalDr = lines.Sum(l => l.DebitAmount);
            var totalCr = lines.Sum(l => l.CreditAmount);

            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                JournalType = JournalType.General,
                Description = $"ขายเงินลงทุน: {entity.Description}",
                Reference = entity.ReferenceNo, Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = totalDr, TotalCredit = totalCr, Lines = lines
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.SaleJournalId = je.Id;
        }

        await _db.SaveChangesAsync();
        return MapInvestment(entity);
    }

    private static InvestmentResponse MapInvestment(ShortTermInvestment i) => new(
        i.Id, i.ReferenceNo, i.InvestmentType, i.Description,
        i.PurchaseDate, i.MaturityDate, i.SaleDate,
        i.PurchaseCost, i.CurrentValue, i.SaleProceeds, i.GainLoss, i.InterestRate,
        i.Status, i.InstitutionName, i.AccountNumber,
        i.InvestmentAccountId, i.InvestmentAccount?.AccountName ?? "",
        i.PurchaseJournalId, i.SaleJournalId);
}

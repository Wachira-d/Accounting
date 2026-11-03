using Accounting.Data;
using Accounting.Helpers;
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
            .Where(j => j.CompanyId == companyId
                && j.EntryDate >= yearStart && j.EntryDate < yearEnd)
            .WherePeriodActivity()   // ★ ทีม O 2026-10-22 — Posted + Reversed · ไม่นับใบปิดบัญชี (เดิม Posted อย่างเดียว ⇒ รายการที่ยกเลิกถูกหักซ้ำ)
            .SelectMany(j => j.Lines)
            .Where(l => l.Account.AccountType == AccountType.Revenue)
            .SumAsync(l => l.CreditAmount - l.DebitAmount);

        var expenses = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && j.EntryDate >= yearStart && j.EntryDate < yearEnd)
            .WherePeriodActivity()   // ★ ทีม O 2026-10-22 — Posted + Reversed · ไม่นับใบปิดบัญชี (เดิม Posted อย่างเดียว ⇒ รายการที่ยกเลิกถูกหักซ้ำ)
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

        // ══ รอบ 228 (TAX_PENALTY_AUDIT §3 ข้อ 1 · ฝ่ายค้าน S1/S2) — ตั้งหนี้ให้เส้นชำระ (หน้านำส่ง ภ.ง.ด.51/50 · Helpers/CitPayment) ล้างได้พอดี ══
        // รูป JE จากตัวตั้งเดียว CitPayment.AccrualPlan: ภ.ง.ด.51 Dr 11920 = ยอดที่ต้องชำระ / Cr 21920 (ไม่แตะ 11910 — ใช้ครั้งเดียวตอน ภ.ง.ด.50) ·
        // ภ.ง.ด.50 Dr 580xx ภาษีทั้งปี / Cr เครดิต ภ.ง.ด.51 (ผังที่ JE ครึ่งปีลง Dr — ใหม่ 11920 · เดิม 580xx) / Cr 11910 / Cr 21920 สุทธิ
        // (เดิม Cr 21920 เต็มภาษี ⇒ หนี้ค้างเท่าเครดิตตลอดไป · ภ.ง.ด.51 ลงค่าใช้จ่ายซ้ำ) · ผังไม่ครบ ⇒ ปฏิเสธ (เดิมข้ามการลงบัญชีเงียบ ๆ แล้วประทับ "Filed")
        var type = CitPayment.TypeOf(entity.TaxPeriod)
            ?? throw new BusinessRuleException($"รอบภาษี \"{entity.TaxPeriod}\" ไม่รู้จัก (HalfYear/Annual)", "CIT-POST-PERIOD");
        var ceYear = CitPayment.CeYear(entity.TaxYear)
            ?? throw new BusinessRuleException($"ปีภาษี \"{entity.TaxYear}\" อ่านไม่ได้", "CIT-POST-PERIOD");

        // S2: รอบเดียวกัน (ปี ค.ศ. + ครึ่งปี/ทั้งปี) ตั้งหนี้ซ้ำไม่ได้ ถ้ารายการอื่นยังมี JE ที่มีผล — คำนวณใหม่ต้องกลับรายการ JE เดิมก่อน (ไม่งั้น 21920 ตั้งสองเท่า)
        var otherJeIds = (await _db.Set<CorporateIncomeTax>().AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.Id != entity.Id && c.TaxPeriod == entity.TaxPeriod && c.JournalEntryId != null)
                .Select(c => new { c.TaxYear, c.JournalEntryId }).ToListAsync())
            .Where(c => CitPayment.CeYear(c.TaxYear) == ceYear).Select(c => c.JournalEntryId!.Value).ToList();
        if (otherJeIds.Count > 0)
        {
            var live = await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && otherJeIds.Contains(j.Id) && !j.IsDeleted
                    && j.Status == JournalEntryStatus.Posted && j.ReversedByEntryId == null)
                .Select(j => j.EntryNumber).ToListAsync();
            if (live.Count > 0)
                throw new BusinessRuleException(
                    $"{CitPayment.PeriodLabel(type, ceYear)} ตั้งหนี้ภาษีไว้แล้ว (JE {string.Join(", ", live)}) — "
                    + "ถ้าคำนวณใหม่ ให้กลับรายการ JE เดิมที่หน้าสมุดรายวันก่อน แล้วค่อยบันทึกรายการนี้ (กันหนี้ 21920 ตั้งซ้ำ)", "CIT-POST-DUP", 409);
        }

        async Task<ChartOfAccount> Req(string code, Func<ChartOfAccount, bool>? fallback, string name)
        {
            var exact = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == code && a.IsActive);
            if (exact != null) return exact;
            if (fallback != null)
            {
                var alt = (await _db.ChartOfAccounts.Where(a => a.CompanyId == companyId && a.IsActive).ToListAsync()).Where(fallback)
                    .OrderBy(a => a.AccountCode).FirstOrDefault();
                if (alt != null) return alt;
            }
            throw new BusinessRuleException($"ไม่พบผังบัญชี {code} ({name}) — สร้างผังที่หน้า “ผังบัญชี” ก่อนบันทึกภาษีเงินได้นิติบุคคล", "CIT-POST-ACCOUNT");
        }

        var plan = CitPayment.AccrualPlan(type, entity.TaxAmount, entity.PrepaidTaxCredit, entity.WithholdingTaxCredit);
        var lines = new List<JournalEntryLine>();
        int order = 1;
        foreach (var pl in plan)
        {
            Guid accId;
            string desc;
            switch (pl.Role)
            {
                case CitPayment.AccrualRole.Expense:
                    accId = (await Req("58000", a => a.AccountCode.StartsWith("580") && a.AccountType == AccountType.Expense, "ภาษีเงินได้นิติบุคคล (ค่าใช้จ่าย)")).Id;
                    desc = "ภาษีเงินได้นิติบุคคล (ภ.ง.ด.50)"; break;
                case CitPayment.AccrualRole.Prepaid:
                    accId = (await Req(CitPayment.PrepaidCode, null, "ภาษีเงินได้นิติบุคคลจ่ายล่วงหน้า")).Id;
                    desc = "ภาษีเงินได้นิติบุคคลจ่ายล่วงหน้า (ภ.ง.ด.51 · ยอดที่ชำระ)"; break;
                case CitPayment.AccrualRole.PrepaidOffset:
                {
                    // ผังเครดิต ภ.ง.ด.51 = ผังที่ JE ตั้งหนี้ครึ่งปีของปีเดียวกันลง Dr (ตั้งแบบเดิมลงค่าใช้จ่าย ⇒ Cr ค่าใช้จ่ายเท่าที่ลงซ้ำ) · ไม่พบ ⇒ 11920
                    var halfJeIds = (await _db.Set<CorporateIncomeTax>().AsNoTracking()
                            .Where(c => c.CompanyId == companyId && c.TaxPeriod == "HalfYear" && c.JournalEntryId != null && c.Id != entity.Id)
                            .Select(c => new { c.TaxYear, c.JournalEntryId, c.CreatedAt }).ToListAsync())
                        .Where(c => CitPayment.CeYear(c.TaxYear) == ceYear).OrderByDescending(c => c.CreatedAt).Select(c => c.JournalEntryId!.Value).ToList();
                    var halfDebitAcc = halfJeIds.Count == 0 ? null : await _db.JournalEntryLines.AsNoTracking()
                        .Where(l => l.JournalEntryId == halfJeIds[0] && l.JournalEntry.CompanyId == companyId && l.DebitAmount > 0)
                        .Select(l => (Guid?)l.AccountId).FirstOrDefaultAsync();
                    accId = halfDebitAcc ?? (await Req(CitPayment.PrepaidCode, null, "ภาษีเงินได้นิติบุคคลจ่ายล่วงหน้า")).Id;
                    desc = "หักภาษีที่ชำระตาม ภ.ง.ด.51"; break;
                }
                case CitPayment.AccrualRole.WhtCredit:
                    accId = (await Req(CitPayment.WhtCreditCode, null, "ภาษีถูกหัก ณ ที่จ่าย")).Id;
                    desc = "หักภาษีถูกหัก ณ ที่จ่าย (เครดิตภาษีทั้งปี)"; break;
                default:
                    accId = (await Req(CitPayment.PayableCode, null, "ภาษีเงินได้นิติบุคคลค้างจ่าย")).Id;
                    desc = "ภาษีเงินได้นิติบุคคลค้างจ่าย (ยอดที่ต้องชำระ)"; break;
            }
            lines.Add(new() { AccountId = accId, DebitAmount = pl.Debit, CreditAmount = pl.Credit, Description = desc, LineOrder = order++ });
        }
        var jeTotal = plan.Sum(x => x.Debit);

        if (lines.Count > 0)
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
                TotalDebit = jeTotal, TotalCredit = jeTotal,
                Lines = lines
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
                .Where(j => j.CompanyId == companyId)
                .WhereInLedger()   // ★ ทีม O 2026-10-22 — ยอดคงเหลือกำไรสะสม: Posted + Reversed (คู่กลับรายการหักล้างกัน)
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
            var divPayableAcc = await _db.ChartOfAccounts.DefaultPostingCandidates(_db).FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == "22010")
                ?? await _db.ChartOfAccounts.DefaultFallbackCandidates(_db).FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode.StartsWith("220"));
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
        // ผังบัญชีจากคำขอ: ของบริษัทนี้ · เปิดใช้ · ไม่ใช่บัญชีคุม — ตัวตัดสินเดียว PostingAccountGuard (2026-10-14 · ID#13) ·
        // เดิมเขียนลง JournalEntryLine.AccountId ดิบ ๆ ⇒ ผังบริษัทอื่น/ปิดใช้/บัญชีคุมลงสมุดได้
        await _db.EnsurePostingAccountAsync(companyId, request.InvestmentAccountId, "บัญชีเงินลงทุน");
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

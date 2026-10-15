using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Loan;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class LoanService : ILoanService
{
    private readonly AccountingDbContext _db;

    public LoanService(AccountingDbContext db)
    {
        _db = db;
    }

    private const string LoanAccountField = "ผังบัญชีเงินกู้";
    private const string InterestAccountField = "ผังบัญชีดอกเบี้ยจ่าย";
    private const string MoneyAccountField = "บัญชีรับ/จ่ายเงินกู้";

    public async Task<LoanResponse> CreateAsync(Guid companyId, CreateLoanRequest request)
    {
        // ── ตรวจคำขอ (ตัวตัดสินเดียว Helpers/LoanPostingPlan) — เดิมไม่ตรวจอะไรเลย: วันที่ 0001-01-01 · เงินต้น 0 ผ่าน ──
        var invalid = LoanPostingPlan.ValidateCreate(request.Name, request.PrincipalAmount, request.InterestRate,
            request.TermMonths, request.InterestType, request.RepaymentFrequency,
            request.DisbursementDate, request.FirstPaymentDate);
        if (invalid != null) throw new BusinessRuleException(invalid, LoanPostingPlan.RuleInvalidRequest);
        if (string.IsNullOrWhiteSpace(request.LoanType))
            throw new BusinessRuleException("เลือก “ประเภทสินเชื่อ”", LoanPostingPlan.RuleInvalidRequest);
        if (request.ContactId is Guid contactId && contactId != Guid.Empty
            && !await _db.Contacts.AnyAsync(c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted))
            throw new BusinessRuleException("ไม่พบ “ผู้ให้กู้ (ผู้ติดต่อ)” ที่เลือกในบริษัทนี้ — เลือกใหม่หรือเว้นว่าง", LoanPostingPlan.RuleInvalidRequest);

        // ── ผังบัญชีจากคำขอ: ของบริษัทนี้ · เปิดใช้ · ระดับรายการ · ชนิดตรงช่อง (เดิมเขียนตรงลง JE — ข้ามบริษัท/ผังคุมได้) ──
        // ช่องทางเงิน: BankAccounts.Id → ผังที่ผูก หรือผังเงินสดตรง (เดิม Loan.BankAccountId รับผังบัญชีในช่องชื่อ "บัญชีธนาคาร")
        var loanAccountId = await RequireOptionalAccountAsync(companyId, request.LoanAccountId, LoanAccountField, AccountType.Liability);
        var interestAccountId = await RequireOptionalAccountAsync(companyId, request.InterestExpenseAccountId, InterestAccountField, AccountType.Expense);
        // ฝ่ายค้านรอบ 2 P2(c): รูปคำขอเดิม (ผังบัญชีในช่อง bankAccountId) รับต่ออีกหนึ่งรอบ + ข้อความเลิกใช้
        var (reqBank, reqMoney, legacyNotice) = await PickedAccountGuard.NormalizeLegacyLoanBankFieldAsync(_db, companyId,
            request.BankAccountId, request.MoneyAccountId);
        var moneyAccountId = await PickedAccountGuard.ResolveMoneyChannelAsync(_db, companyId,
            reqBank, reqMoney, MoneyAccountField);
        var disbursement = LoanPostingPlan.ForDisbursement(request.PostDisbursementEntry,
            loanAccountId.HasValue, moneyAccountId.HasValue);
        if (disbursement.Error != null) throw new BusinessRuleException(disbursement.Error, disbursement.RuleCode);

        var loanPrefix = "LOAN-";
        var maxLoanNum = await _db.Loans
            .IgnoreQueryFilters()
            .Where(l => l.CompanyId == companyId && l.LoanNumber.StartsWith(loanPrefix))
            .Select(l => l.LoanNumber)
            .MaxAsync() as string;
        var loanSeq = 1;
        if (maxLoanNum != null)
        {
            var lastPart = maxLoanNum.Substring(loanPrefix.Length);
            if (int.TryParse(lastPart, out var parsed)) loanSeq = parsed + 1;
        }
        var loanNumber = $"{loanPrefix}{loanSeq:D6}";

        // Calculate monthly payment using standard amortization formula
        // PMT = P * [r(1+r)^n] / [(1+r)^n - 1]
        var monthlyPayment = CalculateMonthlyPayment(
            request.PrincipalAmount,
            request.InterestRate,
            request.TermMonths,
            request.InterestType);

        var firstPaymentDate = request.FirstPaymentDate ?? request.DisbursementDate.AddMonths(1);
        var maturityDate = firstPaymentDate.AddMonths(request.TermMonths - 1);

        var loan = new Loan
        {
            CompanyId = companyId,
            LoanNumber = loanNumber,
            LoanType = request.LoanType,
            Name = request.Name.Trim(),
            Lender = string.IsNullOrWhiteSpace(request.Lender) ? null : request.Lender.Trim(),
            ContactId = request.ContactId == Guid.Empty ? null : request.ContactId,
            PrincipalAmount = request.PrincipalAmount,
            InterestRate = request.InterestRate,
            InterestType = request.InterestType,
            TermMonths = request.TermMonths,
            DisbursementDate = request.DisbursementDate,
            FirstPaymentDate = firstPaymentDate,
            MaturityDate = maturityDate,
            RepaymentFrequency = LoanPostingPlan.MonthlyFrequency,
            MonthlyPayment = monthlyPayment,
            OutstandingPrincipal = request.PrincipalAmount,
            LoanAccountId = loanAccountId,
            InterestExpenseAccountId = interestAccountId,
            MoneyAccountId = moneyAccountId,
            Status = "Active"
        };

        _db.Loans.Add(loan);

        // Create journal entry for loan disbursement: Dr Cash/Bank, Cr Loan Payable
        // ลงหรือไม่ลงตัดสินที่ LoanPostingPlan.ForDisbursement แล้ว (มีครึ่งเดียว = ปฏิเสธข้างบน ไม่ข้ามเงียบแบบเดิม)
        if (disbursement.Post)
        {
            // เลข JE ผ่านตัวกลาง (advisory lock deterministic) — เดิมคำนวณเอง
            // ด้วย string MAX โดยไม่มีล็อก (ผู้ออกเลข JE เถื่อนอีกตัวในตระกูล
            // "4 ผู้ออก 3 lock key" ที่เคยไล่แก้)
            var disbNumber = await Journal.JournalEntryBuilder.NextJournalNumberAsync(
                _db, companyId, "LD", request.DisbursementDate);

            var disbEntry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = disbNumber,
                EntryDate = request.DisbursementDate,
                Description = $"รับเงินกู้ {loanNumber} - {request.Name}",
                TotalDebit = request.PrincipalAmount,
                TotalCredit = request.PrincipalAmount,
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true
            };

            disbEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = loan.MoneyAccountId!.Value,
                DebitAmount = request.PrincipalAmount,
                CreditAmount = 0,
                Description = "รับเงินกู้เข้าบัญชี",
                LineOrder = 1
            });
            disbEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = loan.LoanAccountId!.Value,
                DebitAmount = 0,
                CreditAmount = request.PrincipalAmount,
                Description = "เจ้าหนี้เงินกู้",
                LineOrder = 2
            });

            _db.JournalEntries.Add(disbEntry);
        }

        await _db.SaveChangesAsync();

        return MapToResponse(loan) with { Notice = legacyNotice };
    }

    /// <summary>ผังบัญชีที่ส่งมา (ถ้ามี) ต้องลงรายการได้และชนิดตรงช่อง — คืนรหัสที่ผ่านแล้ว · ไม่ส่ง/Guid.Empty = null</summary>
    private async Task<Guid?> RequireOptionalAccountAsync(Guid companyId, Guid? accountId, string field, AccountType type)
    {
        if (!PickedAccountGuard.IsSet(accountId)) return null;
        await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, accountId!.Value, field, new[] { type });
        return accountId;
    }

    /// <summary>ผังที่เสนอเป็นค่าตั้งต้นของฟอร์มสินเชื่อ (เซิร์ฟเวอร์ตัดสินจากผังมาตรฐาน — หน้าไม่มีรหัสผังของตัวเอง) · ผังที่บริษัทไม่มี = null</summary>
    public async Task<LoanAccountDefaultsResponse> GetAccountDefaultsAsync(Guid companyId, string? loanType, int termMonths)
    {
        var loanCode = LoanPostingPlan.SuggestLoanAccountCode(loanType, termMonths);
        var codes = new[] { loanCode, LoanPostingPlan.DefaultInterestExpenseCode };
        var rows = await _db.ChartOfAccounts.WhereNotControlAccount(_db).AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive && codes.Contains(a.AccountCode))
            .Select(a => new { a.Id, a.AccountCode, a.AccountType })
            .ToListAsync();
        var loanAcc = rows.FirstOrDefault(r => r.AccountCode == loanCode && r.AccountType == AccountType.Liability);
        var intAcc = rows.FirstOrDefault(r => r.AccountCode == LoanPostingPlan.DefaultInterestExpenseCode && r.AccountType == AccountType.Expense);
        return new LoanAccountDefaultsResponse(loanAcc?.Id, intAcc?.Id);
    }

    public async Task<LoanResponse> GetByIdAsync(Guid companyId, Guid loanId)
    {
        var loan = await _db.Loans
            .FirstOrDefaultAsync(l => l.Id == loanId && l.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลสินเชื่อ");
        return MapToResponse(loan);
    }

    public async Task<PagedResponse<LoanResponse>> GetAllAsync(Guid companyId, string? status, PagedRequest request)
    {
        var query = _db.Loans.Where(l => l.CompanyId == companyId);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(l => l.Status == status);

        if (!string.IsNullOrEmpty(request.Search))
        {
            var search = $"%{request.Search}%";
            query = query.Where(l => EF.Functions.ILike(l.LoanNumber, search) || EF.Functions.ILike(l.Name, search));
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);

        return new PagedResponse<LoanResponse>(
            items.Select(MapToResponse).ToList(),
            totalCount,
            request.Page,
            request.PageSize,
            totalPages);
    }

    public async Task<LoanResponse> UpdateAsync(Guid companyId, Guid loanId, UpdateLoanRequest request)
    {
        var loan = await _db.Loans
            .FirstOrDefaultAsync(l => l.Id == loanId && l.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลสินเชื่อ");

        if (request.Name != null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                throw new BusinessRuleException("“ชื่อสินเชื่อ” ว่างไม่ได้", LoanPostingPlan.RuleInvalidRequest);
            loan.Name = request.Name.Trim();
        }
        if (request.Status != null) loan.Status = request.Status;

        // ── ผังบัญชี (ทีม F1 · 2026-10-14) — เดิม UpdateLoanRequest ไม่มีช่องผังเลย ขณะที่ข้อความตอนบันทึกชำระบอกให้ไปตั้งค่าที่นี่ = ทางตัน ──
        string? notice = null;
        if (PickedAccountGuard.IsSet(request.LoanAccountId) && request.LoanAccountId != loan.LoanAccountId)
        {
            await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, request.LoanAccountId!.Value,
                LoanAccountField, new[] { AccountType.Liability });
            // ไม่บล็อก (JE แต่ละใบยังสมดุล) แต่บอกให้รู้ว่ายอดที่ลงไปแล้วยังอยู่ผังเก่า — คำตัดสินข้อ 150: นอกเหนือ GL integrity = คำเตือน
            if (loan.LoanAccountId.HasValue
                && await _db.LoanPayments.AnyAsync(p => p.CompanyId == companyId && p.LoanId == loanId && !p.IsDeleted))
                notice = "เปลี่ยนผังบัญชีเงินกู้แล้ว — ยอดหนี้และการชำระที่ลงบัญชีไปก่อนหน้ายังอยู่ในผังเดิม "
                         + "ถ้าต้องย้ายยอด ให้บันทึกโอนที่หน้า “สมุดรายวันทั่วไป”";
            loan.LoanAccountId = request.LoanAccountId;
        }
        if (PickedAccountGuard.IsSet(request.InterestExpenseAccountId))
        {
            await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, request.InterestExpenseAccountId!.Value,
                InterestAccountField, new[] { AccountType.Expense });
            loan.InterestExpenseAccountId = request.InterestExpenseAccountId;
        }
        var (updBank, updMoney, updLegacy) = await PickedAccountGuard.NormalizeLegacyLoanBankFieldAsync(_db, companyId,
            request.BankAccountId, request.MoneyAccountId);
        var newMoney = await PickedAccountGuard.ResolveMoneyChannelAsync(_db, companyId,
            updBank, updMoney, MoneyAccountField);
        if (newMoney.HasValue) loan.MoneyAccountId = newMoney;
        if (updLegacy != null) notice = notice == null ? updLegacy : notice + " · " + updLegacy;

        if (request.InterestRate.HasValue)
        {
            loan.InterestRate = request.InterestRate.Value;
            // Recalculate monthly payment based on outstanding principal and remaining months
            var paidInstallments = await _db.LoanPayments
                .CountAsync(p => p.LoanId == loanId);
            var remainingMonths = Math.Max(1, loan.TermMonths - paidInstallments);
            loan.MonthlyPayment = CalculateMonthlyPayment(
                loan.OutstandingPrincipal,
                request.InterestRate.Value,
                remainingMonths,
                loan.InterestType);

            // ตารางผ่อนต้องตามอัตราใหม่ด้วย — เดิม recompute แค่ MonthlyPayment
            // ⇒ การ์ดสรุปโชว์ค่างวดใหม่ แต่ตาราง (และดอกเบี้ยค้างจ่ายที่ตั้งจาก
            // ตาราง) ยังเป็นอัตราเก่า = สองตัวเลขในโมดัลเดียวกันไม่ตรงกัน
            // GenerateScheduleAsync ลบเฉพาะงวดที่ยังไม่จ่ายแล้วสร้างใหม่ จึง
            // ปลอดภัยกับงวดที่จ่ายไปแล้ว. เรียกเฉพาะเมื่อมีตารางอยู่ก่อน —
            // สินเชื่อที่ผู้ใช้ยังไม่กด "สร้างตาราง" ไม่ควรได้ตารางโผล่มาเอง
            var hasSchedule = await _db.LoanSchedules
                .AnyAsync(x => x.CompanyId == companyId && x.LoanId == loanId);
            if (hasSchedule)
            {
                await _db.SaveChangesAsync();   // ให้ generator เห็นอัตรา/ค่างวดใหม่
                await GenerateScheduleAsync(companyId, loanId);
            }
        }

        await _db.SaveChangesAsync();
        return MapToResponse(loan) with { Notice = notice };
    }

    public async Task<List<LoanScheduleResponse>> GenerateScheduleAsync(Guid companyId, Guid loanId)
    {
        var loan = await _db.Loans
            .FirstOrDefaultAsync(l => l.Id == loanId && l.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลสินเชื่อ");

        // Remove existing unpaid schedules
        var existingSchedules = await _db.LoanSchedules
            .Where(s => s.LoanId == loanId && !s.IsPaid)
            .ToListAsync();
        _db.LoanSchedules.RemoveRange(existingSchedules);

        var schedules = new List<LoanSchedule>();
        var balance = loan.PrincipalAmount;
        var monthlyRate = loan.InterestRate / 100m / 12m;
        var monthlyPayment = loan.MonthlyPayment;

        for (int i = 1; i <= loan.TermMonths; i++)
        {
            decimal interestPortion;
            decimal principalPortion;

            if (loan.InterestType == "FlatRate")
            {
                // Flat rate: interest is constant each period
                interestPortion = loan.PrincipalAmount * monthlyRate;
                principalPortion = loan.PrincipalAmount / loan.TermMonths;
                monthlyPayment = principalPortion + interestPortion;
            }
            else
            {
                // Standard amortization (Fixed/Floating)
                interestPortion = balance * monthlyRate;
                principalPortion = monthlyPayment - interestPortion;
            }

            // Last installment adjustment to handle rounding
            if (i == loan.TermMonths)
            {
                principalPortion = balance;
                monthlyPayment = principalPortion + interestPortion;
            }

            balance -= principalPortion;
            if (balance < 0) balance = 0;

            var schedule = new LoanSchedule
            {
                CompanyId = companyId,
                LoanId = loanId,
                InstallmentNumber = i,
                DueDate = loan.FirstPaymentDate.AddMonths(i - 1),
                PaymentAmount = Math.Round(monthlyPayment, 2, MidpointRounding.AwayFromZero),
                PrincipalPortion = Math.Round(principalPortion, 2, MidpointRounding.AwayFromZero),
                InterestPortion = Math.Round(interestPortion, 2, MidpointRounding.AwayFromZero),
                RemainingBalance = Math.Round(balance, 2, MidpointRounding.AwayFromZero)
            };

            schedules.Add(schedule);
        }

        _db.LoanSchedules.AddRange(schedules);
        await _db.SaveChangesAsync();

        return schedules.Select(MapScheduleToResponse).ToList();
    }

    public async Task<List<LoanScheduleResponse>> GetScheduleAsync(Guid companyId, Guid loanId)
    {
        var schedules = await _db.LoanSchedules
            .Where(s => s.LoanId == loanId && s.CompanyId == companyId)
            .OrderBy(s => s.InstallmentNumber)
            .ToListAsync();

        return schedules.Select(MapScheduleToResponse).ToList();
    }

    public async Task<LoanPaymentResponse> MakePaymentAsync(Guid companyId, Guid loanId, MakeLoanPaymentRequest request, string performedBy)
    {
        var loan = await _db.Loans
            .FirstOrDefaultAsync(l => l.Id == loanId && l.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลสินเชื่อ");

        if (loan.Status != "Active")
            throw new InvalidOperationException("สินเชื่อไม่อยู่ในสถานะใช้งาน");

        // ── งวดที่จ่าย: ระบุมา หรือ = งวดค้างจ่ายงวดแรก ──
        var schedule = request.InstallmentNumber.HasValue
            ? await _db.LoanSchedules.FirstOrDefaultAsync(x =>
                x.CompanyId == companyId && x.LoanId == loanId
                && x.InstallmentNumber == request.InstallmentNumber.Value)
            : await _db.LoanSchedules
                .Where(x => x.CompanyId == companyId && x.LoanId == loanId && !x.IsPaid)
                .OrderBy(x => x.InstallmentNumber)
                .FirstOrDefaultAsync();

        // ── แตกยอดเป็น ต้น/ดอก ──
        // ผู้ใช้ระบุเองมา (0 มีความหมาย เช่น "จ่ายดอกอย่างเดียว") → เชื่อค่านั้น
        // ไม่ระบุ → แตกจากตารางผ่อนของงวด: ดอกตามตาราง (ไม่เกินยอดที่จ่าย)
        // ที่เหลือเป็นเงินต้น — ตรงกับวิธีตัดชำระเงินกู้มาตรฐาน
        var lateFee = request.LateFee ?? 0m;
        decimal principal, interest;
        if (request.PrincipalPortion.HasValue || request.InterestPortion.HasValue)
        {
            principal = request.PrincipalPortion ?? 0m;
            interest = request.InterestPortion ?? 0m;
            if (request.Amount.HasValue
                && Math.Abs(request.Amount.Value - (principal + interest + lateFee)) > 0.01m)
                throw new InvalidOperationException(
                    $"ยอดที่จ่าย {request.Amount:N2} ไม่เท่ากับ เงินต้น {principal:N2} + ดอกเบี้ย {interest:N2}"
                    + (lateFee > 0 ? $" + ค่าปรับ {lateFee:N2}" : "")
                    + " — กรุณาตรวจการแตกยอดอีกครั้ง");
        }
        else
        {
            var amount = request.Amount
                ?? throw new InvalidOperationException("กรุณาระบุยอดที่จ่าย หรือแตกเงินต้น/ดอกเบี้ยมาให้");
            var baseAmount = amount - lateFee;
            if (baseAmount < 0)
                throw new InvalidOperationException("ค่าปรับมากกว่ายอดที่จ่าย — ตรวจตัวเลขอีกครั้ง");
            interest = Math.Min(schedule?.InterestPortion ?? 0m, baseAmount);
            principal = baseAmount - interest;
        }

        var totalPaid = principal + interest + lateFee;
        if (totalPaid <= 0)
            throw new InvalidOperationException("ยอดชำระต้องมากกว่า 0");

        if (schedule != null)
        {
            schedule.IsPaid = true;
            schedule.PaidDate = request.PaymentDate;
        }

        var installmentNumber = request.InstallmentNumber ?? schedule?.InstallmentNumber ?? 0;
        var payment = new LoanPayment
        {
            CompanyId = companyId,
            LoanId = loanId,
            InstallmentNumber = installmentNumber,
            PaymentDate = request.PaymentDate,
            PrincipalPaid = principal,
            InterestPaid = interest,
            TotalPaid = totalPaid,
            LateFee = request.LateFee,
            PaymentMethod = string.IsNullOrWhiteSpace(request.PaymentMethod)
                ? "BankTransfer" : request.PaymentMethod,
            Reference = request.Reference ?? request.Notes,
            CreatedBy = performedBy
        };

        _db.LoanPayments.Add(payment);

        // Update loan balances
        loan.OutstandingPrincipal -= principal;
        loan.TotalPrincipalPaid += principal;
        loan.TotalInterestPaid += interest;

        if (loan.OutstandingPrincipal <= 0)
        {
            loan.OutstandingPrincipal = 0;
            loan.Status = "PaidOff";
        }

        // ── JE: Dr เงินกู้ (ตัดหนี้) + Dr ดอกเบี้ยจ่าย / Cr เงินสด-ธนาคาร ──
        // เส้นเงิน: JE ต้องสมดุลหรือไม่โพสต์เลยพร้อมบอกเหตุ (ห้ามโพสต์ JE
        // ขาข้างเดียวแบบเดิมที่ข้ามบรรทัดเงียบ ๆ เมื่อผังบัญชีไม่ครบ)
        if (principal > 0 && !loan.LoanAccountId.HasValue)
            throw new BusinessRuleException(LoanPostingPlan.NoLoanAccountMessage, LoanPostingPlan.RuleNoLoanAccount);
        if (interest + lateFee > 0 && !loan.InterestExpenseAccountId.HasValue)
            throw new BusinessRuleException(LoanPostingPlan.NoInterestAccountMessage, LoanPostingPlan.RuleNoInterestAccount);
        // ผังที่ผูกไว้อาจถูกปิดใช้งาน/ลบหลังผูก — ตรวจซ้ำก่อนเขียนลง JE
        if (principal > 0)
            await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, loan.LoanAccountId!.Value,
                LoanAccountField, new[] { AccountType.Liability });
        if (interest + lateFee > 0)
            await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, loan.InterestExpenseAccountId!.Value,
                InterestAccountField, new[] { AccountType.Expense });

        // จ่ายจากบัญชีไหน: ที่ส่งมากับการชำระ > ผังเงินของสินเชื่อ > ปฏิเสธพร้อมบอกให้เลือก ·
        // เดิมไม่สนผังของสินเชื่อเลย แล้วจ่ายจาก 11122 / 111x ตัวแรกเสมอ (PAYMENT_CHANNEL_AUDIT ส่วนที่ 2 ข้อ 2)
        const string payFromField = "จ่ายจากบัญชี";
        var (payBank, payMoney, _) = await PickedAccountGuard.NormalizeLegacyLoanBankFieldAsync(_db, companyId,
            request.BankAccountId, request.MoneyAccountId);
        var cashAccountId = await PickedAccountGuard.ResolveMoneyChannelAsync(_db, companyId,
            payBank, payMoney, payFromField);
        // ผังเงินของสินเชื่อ (อาจมาจากบัญชีธนาคาร → ผังที่ผูก) — ตัวตัดสินเดียวกับตอนเลือก (ฝ่ายค้านรอบ 2 P1-1: ไม่ใช่กติกาที่เข้มกว่าตอนเลือก)
        if (!cashAccountId.HasValue && loan.MoneyAccountId.HasValue)
            cashAccountId = await PickedAccountGuard.ResolveMoneyChannelAsync(_db, companyId, null, loan.MoneyAccountId, MoneyAccountField);
        if (!cashAccountId.HasValue)
            throw new BusinessRuleException(LoanPostingPlan.NoMoneyAccountMessage, LoanPostingPlan.RuleNoMoneyAccount);

        // เลข JE ผ่านตัวกลาง (advisory lock deterministic) — เดิมที่นี่คำนวณเอง
        // ด้วย string MAX โดยไม่มีล็อกเลย = ผู้ออกเลข JE เถื่อนอีกตัว
        var entryNumber = await Journal.JournalEntryBuilder.NextJournalNumberAsync(
            _db, companyId, "LP", request.PaymentDate);
        var journalEntry = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = request.PaymentDate,
            Description = $"ชำระสินเชื่อ {loan.LoanNumber} งวดที่ {installmentNumber}",
            TotalDebit = totalPaid,
            TotalCredit = totalPaid,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true
        };

        var lines = new List<JournalEntryLine>();
        var lineOrder = 1;
        if (principal > 0)
            lines.Add(new JournalEntryLine
            {
                JournalEntryId = journalEntry.Id,
                AccountId = loan.LoanAccountId!.Value,
                DebitAmount = principal,
                CreditAmount = 0,
                Description = "ชำระเงินต้น",
                LineOrder = lineOrder++
            });
        if (interest + lateFee > 0)
            lines.Add(new JournalEntryLine
            {
                JournalEntryId = journalEntry.Id,
                AccountId = loan.InterestExpenseAccountId!.Value,
                DebitAmount = interest + lateFee,
                CreditAmount = 0,
                // ค่าปรับล่าช้ารวมในขาดอกเบี้ยจ่าย — เดิมไม่มีขา Dr ของค่าปรับ
                // เลย ⇒ ใส่ LateFee เมื่อไร JE ไม่สมดุลทันที
                Description = lateFee > 0 ? $"ดอกเบี้ยจ่าย + ค่าปรับล่าช้า {lateFee:N2}" : "ดอกเบี้ยจ่าย",
                LineOrder = lineOrder++
            });
        lines.Add(new JournalEntryLine
        {
            JournalEntryId = journalEntry.Id,
            AccountId = cashAccountId.Value,
            DebitAmount = 0,
            CreditAmount = totalPaid,
            Description = $"จ่ายชำระสินเชื่อ {loan.LoanNumber}",
            LineOrder = lineOrder
        });

        journalEntry.Lines = lines;
        _db.JournalEntries.Add(journalEntry);
        payment.JournalEntryId = journalEntry.Id;

        await _db.SaveChangesAsync();

        return new LoanPaymentResponse(
            payment.Id, payment.InstallmentNumber, payment.PaymentDate,
            payment.PrincipalPaid, payment.InterestPaid, payment.TotalPaid,
            payment.LateFee, payment.PaymentMethod, payment.Reference);
    }

    public async Task<List<LoanPaymentResponse>> GetPaymentsAsync(Guid companyId, Guid loanId)
    {
        var payments = await _db.LoanPayments
            .Where(p => p.LoanId == loanId && p.CompanyId == companyId)
            .OrderBy(p => p.InstallmentNumber)
            .ToListAsync();

        return payments.Select(p => new LoanPaymentResponse(
            p.Id, p.InstallmentNumber, p.PaymentDate,
            p.PrincipalPaid, p.InterestPaid, p.TotalPaid,
            p.LateFee, p.PaymentMethod, p.Reference)).ToList();
    }

    public async Task<LoanSummaryResponse> GetSummaryAsync(Guid companyId)
    {
        var loans = await _db.Loans
            .Where(l => l.CompanyId == companyId)
            .ToListAsync();

        var activeLoans = loans.Where(l => l.Status == "Active").ToList();
        var startOfYear = new DateTime(DateTime.UtcNow.Year, 1, 1);

        var interestPaidYTD = await _db.LoanPayments
            .Where(p => p.CompanyId == companyId && p.PaymentDate >= startOfYear)
            .SumAsync(p => p.InterestPaid);

        return new LoanSummaryResponse(
            TotalLoans: loans.Count,
            ActiveLoans: activeLoans.Count,
            TotalOutstandingPrincipal: activeLoans.Sum(l => l.OutstandingPrincipal),
            TotalMonthlyPayment: activeLoans.Sum(l => l.MonthlyPayment),
            TotalInterestPaidYTD: interestPaidYTD,
            Loans: loans.Select(MapToResponse).ToList());
    }

    // ===== Amortization Calculation =====

    /// <summary>
    /// Calculate monthly payment using standard amortization formula:
    /// PMT = P * [r(1+r)^n] / [(1+r)^n - 1]
    /// where P = principal, r = monthly rate, n = number of months
    /// </summary>
    private static decimal CalculateMonthlyPayment(decimal principal, decimal annualRate, int termMonths, string interestType)
    {
        if (principal <= 0 || termMonths <= 0)
            return 0;

        if (annualRate <= 0)
            return principal / termMonths;

        var monthlyRate = annualRate / 100m / 12m;

        if (interestType == "FlatRate")
        {
            // Flat rate: total interest = P * r * n, spread evenly
            var totalInterest = principal * monthlyRate * termMonths;
            return (principal + totalInterest) / termMonths;
        }

        // Standard amortization formula: PMT = P * [r(1+r)^n] / [(1+r)^n - 1]
        var rateDouble = (double)monthlyRate;
        var power = Math.Pow(1 + rateDouble, termMonths);
        var pmt = (double)principal * (rateDouble * power) / (power - 1);

        return Math.Round((decimal)pmt, 2, MidpointRounding.AwayFromZero);
    }

    // ===== Mappers =====

    private static LoanResponse MapToResponse(Loan l) => new(
        l.Id, l.LoanNumber, l.LoanType, l.Name, l.Lender,
        l.PrincipalAmount, l.InterestRate, l.InterestType, l.TermMonths,
        l.DisbursementDate, l.MaturityDate, l.MonthlyPayment,
        l.OutstandingPrincipal, l.TotalInterestPaid, l.Status, l.CreatedAt,
        FirstPaymentDate: l.FirstPaymentDate,
        RepaymentFrequency: l.RepaymentFrequency,
        ContactId: l.ContactId,
        LoanAccountId: l.LoanAccountId,
        InterestExpenseAccountId: l.InterestExpenseAccountId,
        MoneyAccountId: l.MoneyAccountId);

    private static LoanScheduleResponse MapScheduleToResponse(LoanSchedule s) => new(
        s.InstallmentNumber, s.DueDate, s.PaymentAmount,
        s.PrincipalPortion, s.InterestPortion, s.RemainingBalance,
        s.IsPaid, s.PaidDate);
}

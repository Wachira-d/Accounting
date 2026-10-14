using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.PettyCash;

/// <summary>
/// Petty cash management — open a tin with an imprest amount, record
/// disbursements as they happen, replenish back to imprest at month-
/// end. Posts the cash-side JE automatically; user picks the expense
/// account per disbursement.
///
/// Custodian model: each fund has ONE responsible user. Audit shows
/// who took out what. Cycle-count (compare CurrentBalance to physical
/// cash on hand) belongs in the StockCount-like flow but for cash.
/// </summary>
public interface IPettyCashService
{
    /// <param name="linkedAccountId">ผังบัญชีของกองทุน (null = ใช้ผังมาตรฐาน <c>PettyCashJePlan.DefaultFundAccountCode</c>
    /// เมื่อมีและใช้งานได้ · ไม่มี = ล้มพร้อมบอกให้เลือก)</param>
    Task<PettyCashFund> CreateFundAsync(Guid companyId, string name,
        Guid? custodianUserId, decimal imprestAmount, Guid? linkedAccountId,
        CancellationToken ct = default);

    /// <summary>แก้กองทุน — ชื่อ · วงเงิน · <b>ผังบัญชีของกองทุน</b> (ทางออกของกองทุนเก่าที่เปิดโดยไม่มีผัง ⇒ ทุกการเบิก/เติมล้ม ·
    /// PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 ข้อ 1 "ทางตัน") · null = ไม่เปลี่ยนช่องนั้น</summary>
    Task<PettyCashFund> UpdateFundAsync(Guid companyId, Guid fundId, string? name,
        decimal? imprestAmount, Guid? linkedAccountId, CancellationToken ct = default);

    /// <summary>ผังที่เสนอเป็นค่าตั้งต้นของกองทุน (11112 เงินสดย่อย ถ้ามีและใช้งานได้) — null = ผังบริษัทไม่มี</summary>
    Task<PettyCashFundAccountOption?> GetDefaultFundAccountAsync(Guid companyId, CancellationToken ct = default);

    Task<PettyCashTransaction> DisburseAsync(Guid companyId, Guid fundId,
        decimal amount, string description, Guid? expenseAccountId,
        string? receiptRef, DateTime txnDate, Guid? projectId = null,
        CancellationToken ct = default);

    /// <param name="sourceAccountId">ผังบัญชีต้นทางของเงินที่เติมโดยตรง (เงินสด/กระเป๋าเงิน — ChartOfAccounts.Id)</param>
    /// <param name="sourceBankAccountId">บัญชีธนาคารต้นทาง (BankAccounts.Id → <c>LinkedAccountId</c>) · ส่งได้อย่างใดอย่างหนึ่ง ·
    /// ไม่ส่งทั้งคู่ = ระบบใช้บัญชีธนาคารที่ผูกผังอยู่ <b>ใบเดียว</b> ของบริษัท มิฉะนั้นล้มพร้อมบอกให้เลือก — ห้ามเดาแทนผู้ใช้</param>
    Task<PettyCashTransaction> ReplenishAsync(Guid companyId, Guid fundId,
        decimal amount, string? notes, DateTime txnDate,
        Guid? sourceAccountId = null, Guid? sourceBankAccountId = null, CancellationToken ct = default);

    Task<IReadOnlyList<PettyCashFundResponse>> ListAsync(Guid companyId, CancellationToken ct = default);
    Task<IReadOnlyList<PettyCashTransaction>> ListTransactionsAsync(Guid companyId, Guid fundId,
        DateTime? fromDate, DateTime? toDate, CancellationToken ct = default);
}

/// <summary>กองทุนเงินสดย่อยสำหรับหน้าจอ — ผังของกองทุนออกเป็นรหัส+ชื่อ (หน้าไม่ต้องเดา) · <c>HasPostedTransactions</c> บอกว่าเปลี่ยนผังได้ไหม</summary>
public sealed record PettyCashFundResponse(
    Guid Id, string Name, Guid? CustodianUserId, decimal ImprestAmount, decimal CurrentBalance,
    Guid? LinkedAccountId, string? LinkedAccountCode, string? LinkedAccountName, bool IsActive,
    bool HasPostedTransactions);

/// <summary>ผังบัญชีที่เสนอให้กองทุน</summary>
public sealed record PettyCashFundAccountOption(Guid AccountId, string AccountCode, string AccountName);

public class PettyCashService : IPettyCashService
{
    private readonly AccountingDbContext _db;

    public PettyCashService(AccountingDbContext db) { _db = db; }

    private const string FundAccountField = "ผังบัญชีของกองทุน";

    public async Task<PettyCashFund> CreateFundAsync(Guid companyId, string name,
        Guid? custodianUserId, decimal imprestAmount, Guid? linkedAccountId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleException("กรอก “ชื่อกองทุน” ก่อนบันทึก", "PETTY-FUND-NAME");
        if (imprestAmount <= 0)
            throw new BusinessRuleException("“วงเงินกองทุน (imprest)” ต้องมากกว่า 0", "PETTY-FUND-IMPREST");
        if (custodianUserId is Guid cu && cu != Guid.Empty
            && !await _db.CompanyUsers.AnyAsync(x => x.CompanyId == companyId && x.UserId == cu, ct))
            throw new BusinessRuleException("“ผู้ดูแลกองทุน” ไม่ใช่สมาชิกของบริษัทนี้ — เลือกผู้ดูแลใหม่", "PETTY-FUND-CUSTODIAN");

        // ผังของกองทุน: ผู้ใช้เลือก > ผังมาตรฐาน 11112 (ค่าตั้งต้นที่แน่นอน ไม่ใช่การเดา) > ล้มพร้อมบอกให้เลือก ·
        // เดิมหน้าเว็บส่ง null เสมอและไม่มีที่ผูกภายหลัง ⇒ ทุกการเบิก/เติมล้ม (PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 ข้อ 1)
        Guid fundAccountId;
        if (PickedAccountGuard.IsSet(linkedAccountId))
        {
            await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, linkedAccountId!.Value,
                FundAccountField, new[] { AccountType.Asset }, ct);
            fundAccountId = linkedAccountId!.Value;
        }
        else
        {
            var def = await GetDefaultFundAccountAsync(companyId, ct)
                ?? throw new BusinessRuleException(
                    $"เลือก “{FundAccountField}” ก่อนบันทึก — ผังบัญชีของบริษัทไม่มี {PettyCashJePlan.DefaultFundAccountCode} เงินสดย่อย "
                    + "(เพิ่มที่หน้า “ผังบัญชี” หรือเลือกผังสินทรัพย์อื่นที่ใช้เป็นเงินสดย่อย)", PettyCashJePlan.RuleNoFundAccount);
            fundAccountId = def.AccountId;
        }

        var fund = new PettyCashFund
        {
            CompanyId = companyId,
            Name = name.Trim(),
            CustodianUserId = PickedAccountGuard.IsSet(custodianUserId) ? custodianUserId : null,
            ImprestAmount = imprestAmount,
            CurrentBalance = 0m,            // bank → cash JE on first replenishment funds the tin
            LinkedAccountId = fundAccountId,
        };
        _db.PettyCashFunds.Add(fund);
        await _db.SaveChangesAsync(ct);
        return fund;
    }

    public async Task<PettyCashFund> UpdateFundAsync(Guid companyId, Guid fundId, string? name,
        decimal? imprestAmount, Guid? linkedAccountId, CancellationToken ct = default)
    {
        var fund = await FindFundAsync(companyId, fundId, ct);
        if (name != null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new BusinessRuleException("“ชื่อกองทุน” ว่างไม่ได้", "PETTY-FUND-NAME");
            fund.Name = name.Trim();
        }
        if (imprestAmount.HasValue)
        {
            if (imprestAmount.Value <= 0)
                throw new BusinessRuleException("“วงเงินกองทุน (imprest)” ต้องมากกว่า 0", "PETTY-FUND-IMPREST");
            fund.ImprestAmount = imprestAmount.Value;
        }
        if (PickedAccountGuard.IsSet(linkedAccountId) && linkedAccountId != fund.LinkedAccountId)
        {
            await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, linkedAccountId!.Value,
                FundAccountField, new[] { AccountType.Asset }, ct);
            // ยอดที่ลงบัญชีไปแล้วอยู่ในผังเดิม — เปลี่ยนผังตอนนี้ = ยอดเงินสดย่อยในงบแตกเป็นสองผังโดยไม่มีใครรู้ (ความถูกต้องของ GL ⇒ บล็อก)
            if (fund.LinkedAccountId.HasValue
                && await _db.PettyCashTransactions.AnyAsync(t => t.CompanyId == companyId && t.FundId == fund.Id
                    && !t.IsDeleted && t.JournalEntryId != null, ct))
                throw new BusinessRuleException(
                    "กองทุนนี้มีรายการที่ลงบัญชีไปแล้ว — เปลี่ยน “ผังบัญชีของกองทุน” ไม่ได้ เพราะยอดเดิมอยู่ในผังเก่า "
                    + "· ถ้าต้องย้ายผังจริง: บันทึกโอนยอดที่หน้า “สมุดรายวันทั่วไป” (เดบิตผังใหม่ / เครดิตผังเดิม) แล้วเปิดกองทุนใหม่ที่ผูกผังใหม่",
                    "PETTY-FUND-ACCOUNT-LOCKED");
            fund.LinkedAccountId = linkedAccountId;
        }
        fund.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return fund;
    }

    public async Task<PettyCashFundAccountOption?> GetDefaultFundAccountAsync(Guid companyId, CancellationToken ct = default)
        => await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive && a.Level >= 4
                && a.AccountType == AccountType.Asset && a.AccountCode == PettyCashJePlan.DefaultFundAccountCode)
            .Select(a => new PettyCashFundAccountOption(a.Id, a.AccountCode, a.AccountName))
            .FirstOrDefaultAsync(ct);

    private async Task<PettyCashFund> FindFundAsync(Guid companyId, Guid fundId, CancellationToken ct)
        => await _db.PettyCashFunds.FirstOrDefaultAsync(
               f => f.Id == fundId && f.CompanyId == companyId && !f.IsDeleted, ct)
           ?? throw new KeyNotFoundException("ไม่พบกองทุนเงินสดย่อยนี้ (อาจถูกลบไปแล้ว) — รีเฟรชหน้าแล้วเลือกกองทุนใหม่");

    /// <summary>ผังของกองทุนต้องยังลงรายการได้ตอนลง JE (ผังอาจถูกปิดใช้งานหลังผูก)</summary>
    private async Task RequireFundAccountUsableAsync(Guid companyId, PettyCashFund fund, CancellationToken ct)
        => await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, fund.LinkedAccountId!.Value,
            FundAccountField + $" ของกองทุน {fund.Name}", new[] { AccountType.Asset }, ct);

    public async Task<PettyCashTransaction> DisburseAsync(Guid companyId, Guid fundId,
        decimal amount, string description, Guid? expenseAccountId,
        string? receiptRef, DateTime txnDate, Guid? projectId = null,
        CancellationToken ct = default)
    {
        if (amount <= 0) throw new BusinessRuleException("“จำนวนเงิน” ต้องมากกว่า 0", PettyCashJePlan.RuleBadAmount);
        if (string.IsNullOrWhiteSpace(description))
            throw new BusinessRuleException("กรอก “คำอธิบาย” ว่าจ่ายค่าอะไร ก่อนบันทึก", "PETTY-NO-DESCRIPTION");
        var fund = await FindFundAsync(companyId, fundId, ct);

        // ── ลงบัญชี: Dr ค่าใช้จ่าย / Cr ผังของกองทุน ────────────────────
        // ⚠ เดิมเป็น `if (มีทั้งสองผัง) { ลง JE }` **ไม่มี else** ⇒ ขาดผังไหน
        // ก็บันทึกรายการโดยไม่มี JE เงียบ ๆ = เงินออกจากลิ้นชักโดยบัญชีไม่รู้
        // (`DECISION_AUDIT_2026-09-18.md` §3 D4-7)
        var plan = Accounting.Helpers.PettyCashJePlan.ForDisbursement(
            expenseAccountId, fund.LinkedAccountId, receiptRef, amount);
        if (!plan.Ok) throw new BusinessRuleException(plan.Error!, plan.RuleCode);
        // รหัสจาก request ต้องเป็นผังของบริษัทนี้ที่ลงรายการได้ (เดิมเขียนตรงลง JournalEntryLine.AccountId — ข้ามบริษัท/ผังคุมได้)
        await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, plan.DebitAccountId, "บัญชีค่าใช้จ่าย", null, ct);
        await RequireFundAccountUsableAsync(companyId, fund, ct);
        if (projectId is Guid pid && pid != Guid.Empty
            && !await _db.Projects.AnyAsync(p => p.Id == pid && p.CompanyId == companyId && !p.IsDeleted, ct))
            throw new BusinessRuleException("ไม่พบ “โครงการ” ที่เลือกในบริษัทนี้ — รีเฟรชหน้าแล้วเลือกโครงการใหม่ หรือเว้นว่าง", "PETTY-PROJECT");
        if (projectId == Guid.Empty) projectId = null;

        if (fund.CurrentBalance < amount)
            throw new BusinessRuleException(
                $"เงินสดย่อยกองทุน {fund.Name} คงเหลือ {fund.CurrentBalance:N2} บาท ไม่พอจ่าย {amount:N2} บาท — "
                + "กด “เติมเงิน” ที่กองทุนนี้ก่อน หรือลดจำนวนเงิน", "PETTY-INSUFFICIENT");
        fund.CurrentBalance -= amount;
        var txn = new PettyCashTransaction
        {
            CompanyId = companyId,
            FundId = fundId,
            TransactionDate = txnDate,
            Amount = amount,
            Type = "Disbursement",
            Description = description,
            ExpenseAccountId = expenseAccountId,
            ReceiptReference = receiptRef,
            ProjectId = projectId,
        };
        _db.PettyCashTransactions.Add(txn);

        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = $"JV-PC-{txnDate:yyyyMMdd}-{Guid.NewGuid().ToString()[..4].ToUpper()}",
            EntryDate = txnDate,
            JournalType = JournalType.General,
            Description = $"จ่ายเงินสดย่อย: {description}",
            Reference = receiptRef,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            ProjectId = projectId,
            TotalDebit = amount,
            TotalCredit = amount,
        };
        je.Lines.Add(new JournalEntryLine
        {
            AccountId = plan.DebitAccountId,
            DebitAmount = amount,
            CreditAmount = 0,
            Description = description,
            ProjectId = projectId,
        });
        je.Lines.Add(new JournalEntryLine
        {
            AccountId = plan.CreditAccountId,
            DebitAmount = 0,
            CreditAmount = amount,
            Description = $"จ่ายจากเงินสดย่อย {fund.Name}",
            ProjectId = projectId,
        });
        _db.JournalEntries.Add(je);
        txn.JournalEntryId = je.Id;

        // §65 ตรี(9) — จ่ายโดยไม่มีหลักฐานผู้รับ: **บันทึกได้ แต่ติดธงบวกกลับ**
        // (เงินออกจากลิ้นชักไปแล้วจริง การห้ามบันทึกจะทำให้ยอดในระบบไม่ตรงกับ
        // เงินในลิ้นชักถาวร ซึ่งเสียหายกว่า — ดู doc ของ `PettyCashJePlan`)
        txn.IsNonDeductible = plan.NonDeductible;
        txn.NonDeductibleRuleCode = plan.NonDeductible ? plan.RuleCode : null;

        await _db.SaveChangesAsync(ct);
        return txn;
    }

    /// <summary>
    /// เติมเงินสดย่อย — **ต้องมี JE เสมอ**: Dr ผังของกองทุน / Cr ผังบัญชีต้นทาง.
    /// เดิมเมธอดนี้ **ไม่มี JE เลย** ⇒ `PettyCashFund.CurrentBalance` ขึ้น แต่ GL
    /// ไม่ขยับทั้งสองขา = ยอดธนาคารในงบไม่ลด ยอดเงินสดย่อยในงบไม่ขึ้น
    /// (`DECISION_AUDIT_2026-09-18.md` §3 D4-7 — "เงินออกธนาคารเข้าถังโดย GL ไม่รู้")
    /// </summary>
    public async Task<PettyCashTransaction> ReplenishAsync(Guid companyId, Guid fundId,
        decimal amount, string? notes, DateTime txnDate,
        Guid? sourceAccountId = null, Guid? sourceBankAccountId = null, CancellationToken ct = default)
    {
        if (amount <= 0) throw new BusinessRuleException("“จำนวนเติม” ต้องมากกว่า 0", PettyCashJePlan.RuleBadAmount);
        var fund = await FindFundAsync(companyId, fundId, ct);
        if (!fund.LinkedAccountId.HasValue)
            throw new BusinessRuleException(PettyCashJePlan.NoFundAccountMessage, PettyCashJePlan.RuleNoFundAccount);
        await RequireFundAccountUsableAsync(companyId, fund, ct);

        // ต้นทางของเงิน: บัญชีธนาคาร (→ LinkedAccountId) หรือผังเงินตรง ที่ผู้ใช้เลือก > บัญชีธนาคารที่ผูกผังอยู่ **ใบเดียว**
        // ของบริษัท (ไม่ใช่การเดา — เป็นคำตอบเดียวที่เป็นไปได้) > ล้มพร้อมบอก ·
        // เดิมหน้าเว็บส่ง BankAccount.Id มาในช่องผังบัญชี แล้วเขียนตรงลง JE ⇒ FK ล้ม (บั๊กคลาสเดียวกับรายการเกิดซ้ำ)
        const string sourceField = "เติมจากบัญชี";
        var resolvedSource = await PickedAccountGuard.ResolveMoneyChannelAsync(_db, companyId,
            sourceBankAccountId, sourceAccountId, sourceField, ct);
        if (!resolvedSource.HasValue)
        {
            var linkedBanks = await _db.BankAccounts.AsNoTracking()
                .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive
                    && b.LinkedAccountId != null)
                .Select(b => b.Id)
                .Take(2)
                .ToListAsync(ct);
            if (linkedBanks.Count == 1)
                resolvedSource = await PickedAccountGuard.ResolveBankGlAsync(_db, companyId, linkedBanks[0], sourceField, ct);
        }

        var plan = Accounting.Helpers.PettyCashJePlan.ForReplenishment(
            fund.LinkedAccountId, resolvedSource, amount);
        if (!plan.Ok) throw new BusinessRuleException(plan.Error!, plan.RuleCode);

        fund.CurrentBalance += amount;
        var txn = new PettyCashTransaction
        {
            CompanyId = companyId,
            FundId = fundId,
            TransactionDate = txnDate,
            Amount = amount,
            Type = "Replenishment",
            Description = string.IsNullOrWhiteSpace(notes) ? "เติมเงินสดย่อย" : notes.Trim(),
        };
        _db.PettyCashTransactions.Add(txn);

        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = $"JV-PC-{txnDate:yyyyMMdd}-{Guid.NewGuid().ToString()[..4].ToUpper()}",
            EntryDate = txnDate,
            JournalType = JournalType.General,
            Description = $"เติมเงินสดย่อย {fund.Name}",
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            TotalDebit = amount,
            TotalCredit = amount,
        };
        je.Lines.Add(new JournalEntryLine
        {
            AccountId = plan.DebitAccountId,
            DebitAmount = amount,
            CreditAmount = 0,
            Description = $"เติมเงินสดย่อย {fund.Name}",
        });
        je.Lines.Add(new JournalEntryLine
        {
            AccountId = plan.CreditAccountId,
            DebitAmount = 0,
            CreditAmount = amount,
            Description = $"โอนออกเพื่อเติมเงินสดย่อย {fund.Name}",
        });
        _db.JournalEntries.Add(je);
        txn.JournalEntryId = je.Id;

        await _db.SaveChangesAsync(ct);
        return txn;
    }

    public async Task<IReadOnlyList<PettyCashFundResponse>> ListAsync(Guid companyId, CancellationToken ct = default) =>
        await _db.PettyCashFunds.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted)
            .OrderBy(f => f.Name)
            .Select(f => new PettyCashFundResponse(
                f.Id, f.Name, f.CustodianUserId, f.ImprestAmount, f.CurrentBalance,
                f.LinkedAccountId,
                f.LinkedAccount != null ? f.LinkedAccount.AccountCode : null,
                f.LinkedAccount != null ? f.LinkedAccount.AccountName : null,
                f.IsActive,
                _db.PettyCashTransactions.Any(t => t.CompanyId == companyId && t.FundId == f.Id
                    && !t.IsDeleted && t.JournalEntryId != null)))
            .ToListAsync(ct);

    public Task<IReadOnlyList<PettyCashTransaction>> ListTransactionsAsync(Guid companyId, Guid fundId,
        DateTime? fromDate, DateTime? toDate, CancellationToken ct = default)
    {
        var q = _db.PettyCashTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.FundId == fundId && !t.IsDeleted);
        if (fromDate.HasValue) q = q.Where(t => t.TransactionDate >= fromDate.Value);
        if (toDate.HasValue) q = q.Where(t => t.TransactionDate <= toDate.Value);
        return q.OrderByDescending(t => t.TransactionDate).Take(500).ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<PettyCashTransaction>)t.Result, ct);
    }
}

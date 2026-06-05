using System.Globalization;
using System.Text;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
// Explicit alias so 'Bank.BankFlowClassifier' resolves unambiguously to the
// shared classifier in the child namespace (no broad import → no name clash).
using Bank = Accounting.Services.Implementations.Bank;

namespace Accounting.Services.Implementations;

public partial class BankService : IBankService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<BankService>? _logger;

    public BankService(AccountingDbContext db, ILogger<BankService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<BankAccountResponse> CreateBankAccountAsync(Guid companyId, CreateBankAccountRequest request)
    {
        // Input validation
        if (string.IsNullOrWhiteSpace(request.AccountName))
            throw new ArgumentException("กรุณาระบุชื่อบัญชี");

        if (string.IsNullOrWhiteSpace(request.BankName))
            throw new ArgumentException("กรุณาระบุชื่อธนาคาร");

        if (string.IsNullOrWhiteSpace(request.AccountNumber) || request.AccountNumber.Length < 5 || request.AccountNumber.Length > 20)
            throw new ArgumentException("เลขที่บัญชีต้องมีความยาว 5-20 ตัวอักษร");

        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3 || request.Currency != request.Currency.ToUpperInvariant())
            throw new ArgumentException("สกุลเงินต้องเป็นรหัส 3 ตัวอักษรพิมพ์ใหญ่ (เช่น THB, USD)");

        if (request.OpeningBalance < 0)
            throw new ArgumentException("ยอดเปิดบัญชีต้องไม่ติดลบ");

        var linkedAccountId = request.LinkedAccountId;

        if (!linkedAccountId.HasValue)
        {
            var subAccount = await AutoCreateLinkedAccountAsync(companyId, request);
            if (subAccount != null)
                linkedAccountId = subAccount.Id;
        }

        var account = new BankAccount
        {
            CompanyId = companyId,
            AccountName = request.AccountName,
            BankName = request.BankName,
            AccountNumber = request.AccountNumber,
            BranchName = request.BranchName,
            AccountType = request.AccountType,
            Currency = request.Currency,
            CurrentBalance = request.OpeningBalance,
            LinkedAccountId = linkedAccountId
        };

        _db.Set<BankAccount>().Add(account);
        await _db.SaveChangesAsync();

        if (account.LinkedAccountId.HasValue)
        {
            await _db.Entry(account).Reference(a => a.LinkedAccount).LoadAsync();
        }

        return MapToResponse(account);
    }

    private async Task<ChartOfAccount?> AutoCreateLinkedAccountAsync(Guid companyId, CreateBankAccountRequest request)
    {
        var parentCode = request.AccountType switch
        {
            "Current" => "11121",
            "Fixed" => "11123",
            _ => "11122" // Savings as default
        };

        var parent = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == parentCode && a.IsActive);

        if (parent == null)
            throw new InvalidOperationException(
                $"ไม่พบบัญชีผังบัญชีหลัก {parentCode} สำหรับประเภท {request.AccountType} — " +
                "กรุณาสร้างบัญชีกลุ่มเงินฝากธนาคารในผังบัญชีก่อน หรือระบุ LinkedAccountId โดยตรง");

        var existingChildren = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && a.ParentAccountId == parent.Id)
            .OrderByDescending(a => a.AccountCode)
            .ToListAsync();

        var nextSeq = 1;
        foreach (var child in existingChildren)
        {
            var suffix = child.AccountCode.Replace(parentCode + "-", "");
            if (int.TryParse(suffix, out var num) && num >= nextSeq)
                nextSeq = num + 1;
        }

        var maskedNumber = request.AccountNumber.Length >= 4
            ? "xxx-" + request.AccountNumber[^4..]
            : request.AccountNumber;

        var subAccount = new ChartOfAccount
        {
            CompanyId = companyId,
            AccountCode = $"{parentCode}-{nextSeq:D3}",
            AccountName = $"{parent.AccountName} - {request.BankName} {maskedNumber}",
            AccountNameEn = $"{parent.AccountNameEn ?? parent.AccountName} - {request.BankName} {maskedNumber}",
            AccountType = AccountType.Asset,
            ParentAccountId = parent.Id,
            Level = parent.Level + 1,
            IsActive = true,
            IsSystemAccount = true,
            Description = $"สร้างอัตโนมัติจากบัญชีธนาคาร: {request.BankName} {request.AccountNumber}"
        };

        _db.ChartOfAccounts.Add(subAccount);
        await _db.SaveChangesAsync();

        return subAccount;
    }

    public async Task<List<BankAccountResponse>> GetBankAccountsAsync(Guid companyId)
    {
        var accounts = await _db.Set<BankAccount>()
            .Include(a => a.LinkedAccount)
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .OrderBy(a => a.AccountName)
            .ToListAsync();

        // Compute live balance from posted journal entries for each linked COA account.
        // The stored CurrentBalance only tracks manual transactions/imports — API-posted
        // journal entries go to the linked COA and must be reflected here.
        var linkedIds = accounts.Where(a => a.LinkedAccountId.HasValue)
            .Select(a => a.LinkedAccountId!.Value).Distinct().ToList();
        var glBalances = new Dictionary<Guid, decimal>();
        if (linkedIds.Any())
        {
            var postedEntryIds = _db.JournalEntries
                .Where(j => j.CompanyId == companyId && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed))
                .Select(j => j.Id);
            var sums = await _db.JournalEntryLines
                .Where(l => linkedIds.Contains(l.AccountId) && postedEntryIds.Contains(l.JournalEntryId))
                .GroupBy(l => l.AccountId)
                .Select(g => new { AccountId = g.Key, Debit = g.Sum(l => l.DebitAmount), Credit = g.Sum(l => l.CreditAmount) })
                .ToListAsync();
            // Bank accounts are Asset type → balance = Debit - Credit
            foreach (var s in sums)
                glBalances[s.AccountId] = s.Debit - s.Credit;
        }

        return accounts.Select(a =>
        {
            var gl = a.LinkedAccountId.HasValue && glBalances.TryGetValue(a.LinkedAccountId.Value, out var b) ? b : 0m;
            // Use GL-computed balance when a linked COA is set (authoritative source).
            // Fall back to stored CurrentBalance when no COA is linked (pure manual tracking).
            var displayBalance = a.LinkedAccountId.HasValue ? gl : a.CurrentBalance;
            return new BankAccountResponse(
                a.Id, a.AccountName, a.BankName, a.AccountNumber,
                a.BranchName, a.AccountType, a.Currency, displayBalance,
                a.LinkedAccountId, a.LinkedAccount?.AccountCode, a.LinkedAccount?.AccountName,
                a.IsActive);
        }).ToList();
    }

    public async Task<BankAccountResponse> UpdateBankAccountAsync(Guid companyId, Guid accountId, UpdateBankAccountRequest request)
    {
        var account = await _db.Set<BankAccount>()
            .FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        if (request.AccountName != null) account.AccountName = request.AccountName;
        if (request.BranchName != null) account.BranchName = request.BranchName;
        if (request.IsActive.HasValue) account.IsActive = request.IsActive.Value;
        if (request.LinkedAccountId.HasValue) account.LinkedAccountId = request.LinkedAccountId.Value;

        await _db.SaveChangesAsync();
        return MapToResponse(account);
    }

    public async Task<BankTransactionResponse> CreateTransactionAsync(Guid companyId, CreateBankTransactionRequest request)
    {
        // Input validation
        if (request.Amount <= 0)
            throw new ArgumentException("จำนวนเงินต้องมากกว่า 0");

        if (request.TransactionDate > DateTime.UtcNow.Date.AddDays(1))
            throw new ArgumentException("วันที่ทำรายการต้องไม่เป็นวันในอนาคต");

        await using var dbTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var account = await _db.Set<BankAccount>()
                .FirstOrDefaultAsync(a => a.Id == request.BankAccountId && a.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

            var amount = request.TransactionType == BankTransactionType.Withdrawal || request.TransactionType == BankTransactionType.Fee
                ? -Math.Abs(request.Amount) : Math.Abs(request.Amount);

            // Check if balance would go negative for withdrawals/fees
            var description = request.Description;
            if ((request.TransactionType == BankTransactionType.Withdrawal || request.TransactionType == BankTransactionType.Fee)
                && account.CurrentBalance + amount < 0)
            {
                description = $"[คำเตือน: ยอดคงเหลือติดลบ] {description}";
            }

            account.CurrentBalance += amount;

            var transaction = new BankTransaction
            {
                CompanyId = companyId,
                BankAccountId = request.BankAccountId,
                TransactionDate = request.TransactionDate,
                TransactionType = request.TransactionType,
                Amount = request.Amount,
                BalanceAfter = account.CurrentBalance,
                Description = description,
                Reference = request.Reference,
                Payee = request.Payee
            };

            _db.Set<BankTransaction>().Add(transaction);
            await _db.SaveChangesAsync();

            await dbTransaction.CommitAsync();

            return MapTransactionToResponse(transaction);
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }
    }

    public async Task<PagedResponse<BankTransactionResponse>> GetTransactionsAsync(Guid companyId, Guid bankAccountId, PagedRequest request)
    {
        var query = _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId && t.BankAccountId == bankAccountId);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(t => (t.Description != null && t.Description.Contains(request.Search))
                || (t.Reference != null && t.Reference.Contains(request.Search))
                || (t.Payee != null && t.Payee.Contains(request.Search)));

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(t => t.TransactionDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<BankTransactionResponse>(
            items.Select(MapTransactionToResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    /// <summary>
    /// Amount-integrity guard. Counterparts on the SAME side as the bank txn
    /// ADD; counterparts on the OPPOSITE side SUBTRACT (legit net-settlement —
    /// e.g. customer Receipt 2,500 minus PaymentVoucher refund 500 = 2,000 net
    /// into bank). What's BLOCKED is same-side subtraction (two Receipt
    /// Vouchers can't offset each other — the original −500 + 2,500 nonsense).
    /// Document direction comes from its type; JE direction from the SIGNED net
    /// on the bank's own GL account.
    /// </summary>
    private async Task ValidateMatchAmountAsync(Guid companyId, BankTransaction txn, IEnumerable<Guid> matchedIds)
    {
        var ids = matchedIds.Where(i => i != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return;

        var bankCoaId = await _db.Set<BankAccount>().AsNoTracking()
            .Where(a => a.Id == txn.BankAccountId && a.CompanyId == companyId)
            .Select(a => a.LinkedAccountId)
            .FirstOrDefaultAsync();

        bool txnIsIn = txn.TransactionType is BankTransactionType.Deposit
            or BankTransactionType.Interest;

        // Same-side items add; opposite-side items subtract (net-settlement).
        // Net = sameDirSum − oppDirSum, compared to bank amount.
        decimal sameDirSum = 0m, oppDirSum = 0m;

        var pays = await _db.Payments.AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.CompanyId == companyId)
            .Select(p => new { p.Id, p.Amount, DocType = p.Document.DocumentType, DocNo = p.Document.DocumentNumber })
            .ToListAsync();
        foreach (var p in pays)
        {
            bool payIsIn = p.DocType is DocumentType.Receipt or DocumentType.ReceiptVoucher
                or DocumentType.Invoice or DocumentType.TaxInvoice
                or DocumentType.BillingNote or DocumentType.DebitNote;
            bool payIsOut = p.DocType is DocumentType.PaymentVoucher or DocumentType.Expense
                or DocumentType.PurchaseInvoice or DocumentType.CertificateInLieu;
            // Direction unknown (other doc types) → treat as additive (legacy
            // safe default).
            if (!payIsIn && !payIsOut) { sameDirSum += p.Amount; continue; }
            if (payIsIn == txnIsIn) sameDirSum += p.Amount;
            else oppDirSum += p.Amount;
        }
        var payIds = pays.Select(p => p.Id).ToHashSet();

        // The rest are treated as JournalEntries.
        var jeIds = ids.Where(i => !payIds.Contains(i)).ToList();
        if (jeIds.Count > 0)
        {
            if (bankCoaId.HasValue)
            {
                var lines = await _db.JournalEntryLines.AsNoTracking()
                    .Where(l => jeIds.Contains(l.JournalEntryId) && l.AccountId == bankCoaId.Value)
                    .Select(l => new { l.JournalEntryId, Net = l.DebitAmount - l.CreditAmount })
                    .ToListAsync();
                var withBankLine = lines.Select(l => l.JournalEntryId).ToHashSet();

                // Tally each JE: same-side adds, opposite-side subtracts.
                var jeNetSigned = lines.GroupBy(l => l.JournalEntryId)
                    .ToDictionary(g => g.Key, g => g.Sum(x => x.Net));
                foreach (var kv in jeNetSigned)
                {
                    if (Math.Abs(kv.Value) < 0.01m) continue;
                    bool jeIsIn = kv.Value > 0;
                    var mag = Math.Abs(kv.Value);
                    if (jeIsIn == txnIsIn) sameDirSum += mag;
                    else oppDirSum += mag;
                }
                // JEs that don't touch the bank account → unknown side; additive.
                var noBankLine = jeIds.Where(id => !withBankLine.Contains(id)).ToList();
                if (noBankLine.Count > 0)
                    sameDirSum += await _db.JournalEntries.AsNoTracking()
                        .Where(j => noBankLine.Contains(j.Id)).SumAsync(j => j.TotalDebit);
            }
            else
            {
                sameDirSum += await _db.JournalEntries.AsNoTracking()
                    .Where(j => jeIds.Contains(j.Id)).SumAsync(j => j.TotalDebit);
            }
        }

        var target = Math.Abs(txn.Amount);
        var net = sameDirSum - oppDirSum;
        var diff = Math.Abs(net - target);
        if (diff > 0.01m)
        {
            // Identify the offending bank line so the operator can find it.
            var sign = txn.TransactionType == BankTransactionType.Deposit ? "+" : "-";
            var desc = (txn.Description ?? txn.Payee ?? "").Trim();
            if (desc.Length > 40) desc = desc[..40] + "…";
            var who = $"รายการธนาคาร {txn.TransactionDate:dd/MM/yyyy} {sign}{target:N2}" +
                      (string.IsNullOrWhiteSpace(desc) ? "" : $" ({desc})");
            throw new InvalidOperationException(
                $"{who}: ยอดที่จับคู่ ({net:N2}) ไม่ตรงกับยอดธนาคาร ({target:N2}) — ต่างกัน {diff:N2} บาท. " +
                "ฝั่งเดียวกันบวกกัน, ข้ามฝั่งหักกัน (เช่น Receipt 2,500 − PaymentVoucher 500 = 2,000 net เข้าบัญชี). " +
                "ใบรับ 2 ใบไม่สามารถนำมาลบกันได้ — เลือกเอกสาร/JE ให้ถูก หรือใช้กลุ่มกระทบยอด M:N.");
        }
    }

    public async Task<BankTransactionResponse> ReconcileAsync(Guid companyId, ReconcileRequest request)
    {
        var transaction = await _db.Set<BankTransaction>()
            .FirstOrDefaultAsync(t => t.Id == request.BankTransactionId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการธนาคาร");

        // Validate mutual exclusivity: match to Payment XOR JournalEntry, not both
        if (request.MatchedPaymentId.HasValue && request.MatchedJournalEntryId.HasValue)
            throw new InvalidOperationException("ไม่สามารถจับคู่กับทั้งการชำระเงินและสมุดรายวันพร้อมกันได้ กรุณาเลือกอย่างใดอย่างหนึ่ง");

        if (!request.MatchedPaymentId.HasValue && !request.MatchedJournalEntryId.HasValue)
            throw new InvalidOperationException("กรุณาระบุการชำระเงินหรือสมุดรายวันที่ต้องการจับคู่");

        // Validate referenced entities exist and not already matched
        if (request.MatchedPaymentId.HasValue)
        {
            var paymentExists = await _db.Payments.AnyAsync(p => p.Id == request.MatchedPaymentId.Value && p.CompanyId == companyId);
            if (!paymentExists) throw new KeyNotFoundException("ไม่พบรายการชำระเงินที่ระบุ");
            var alreadyMatched = await _db.Set<BankTransaction>().AnyAsync(t =>
                t.MatchedPaymentId == request.MatchedPaymentId.Value
                && t.Id != request.BankTransactionId
                && t.ReconciliationStatus == ReconciliationStatus.Matched);
            if (alreadyMatched)
                throw new InvalidOperationException("รายการชำระเงินนี้ถูกจับคู่กับรายการธนาคารอื่นแล้ว");
        }
        if (request.MatchedJournalEntryId.HasValue)
        {
            var jeExists = await _db.JournalEntries.AnyAsync(j => j.Id == request.MatchedJournalEntryId.Value && j.CompanyId == companyId);
            if (!jeExists) throw new KeyNotFoundException("ไม่พบสมุดรายวันที่ระบุ");
            var alreadyMatched = await _db.Set<BankTransaction>().AnyAsync(t =>
                t.MatchedJournalEntryId == request.MatchedJournalEntryId.Value
                && t.Id != request.BankTransactionId
                && t.ReconciliationStatus == ReconciliationStatus.Matched);
            if (alreadyMatched)
                throw new InvalidOperationException("สมุดรายวันนี้ถูกจับคู่กับรายการธนาคารอื่นแล้ว");
        }

        // Amounts must agree — block partial/mismatched 1:1 matches.
        var matchId = request.MatchedPaymentId ?? request.MatchedJournalEntryId;
        await ValidateMatchAmountAsync(companyId, transaction, new[] { matchId!.Value });

        transaction.ReconciliationStatus = ReconciliationStatus.Matched;
        transaction.MatchedPaymentId = request.MatchedPaymentId;
        transaction.MatchedJournalEntryId = request.MatchedJournalEntryId;
        transaction.ReconciledAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return MapTransactionToResponse(transaction);
    }

    public async Task<List<BankTransactionResponse>> GetUnreconciledAsync(Guid companyId, Guid bankAccountId)
    {
        var transactions = await _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId
                && t.BankAccountId == bankAccountId
                && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync();

        return transactions.Select(MapTransactionToResponse).ToList();
    }

    public async Task<List<BankTransactionResponse>> AutoMatchAsync(Guid companyId, Guid bankAccountId)
    {
        await using var dbTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Bank's GL account — lets JE matching use the net that actually hit
            // the bank (compound entries), consistent with the shared resolver.
            var autoBankCoaId = await _db.Set<BankAccount>().AsNoTracking()
                .Where(a => a.Id == bankAccountId && a.CompanyId == companyId)
                .Select(a => a.LinkedAccountId).FirstOrDefaultAsync();

            var unmatched = await _db.Set<BankTransaction>()
                .Where(t => t.CompanyId == companyId
                    && t.BankAccountId == bankAccountId
                    && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
                .ToListAsync();

            var payments = await _db.Payments
                .Where(p => p.CompanyId == companyId && p.PaymentMethod == PaymentMethod.BankTransfer)
                .ToListAsync();

            // Get already matched payment IDs to prevent double-matching
            var alreadyMatchedPaymentIds = await _db.Set<BankTransaction>()
                .Where(t => t.CompanyId == companyId && t.MatchedPaymentId.HasValue)
                .Select(t => t.MatchedPaymentId!.Value)
                .ToListAsync();

            // Duplicate detection: get already processed transaction keys (amount+date+reference)
            var processedTransactionKeys = await _db.Set<BankTransaction>()
                .Where(t => t.CompanyId == companyId
                    && t.BankAccountId == bankAccountId
                    && t.ReconciliationStatus == ReconciliationStatus.Matched)
                .Select(t => new { t.Amount, t.TransactionDate, t.Reference })
                .ToListAsync();

            var processedKeySet = new HashSet<string>(
                processedTransactionKeys.Select(t => $"{t.Amount}|{t.TransactionDate:yyyyMMdd}|{t.Reference ?? ""}"));

            var matched = new List<BankTransactionResponse>();

            var alreadyMatchedJeIds = new HashSet<Guid>(
                await _db.Set<BankTransaction>()
                    .Where(t => t.CompanyId == companyId && t.MatchedJournalEntryId.HasValue)
                    .Select(t => t.MatchedJournalEntryId!.Value)
                    .ToListAsync());

            foreach (var txn in unmatched)
            {
                // Duplicate detection: skip if same amount+date+reference already processed
                var txnKey = $"{txn.Amount}|{txn.TransactionDate:yyyyMMdd}|{txn.Reference ?? ""}";
                if (processedKeySet.Contains(txnKey))
                    continue;

                // Smart matching: amount + date proximity + reference similarity
                var candidates = payments
                    .Where(p => !alreadyMatchedPaymentIds.Contains(p.Id)
                        && !matched.Any(m => m.MatchedPaymentId == p.Id))
                    .ToList();

                Payment? bestMatch = null;
                decimal bestScore = 0;

                foreach (var payment in candidates)
                {
                    decimal score = 0;

                    // Amount must match EXACTLY (within 1 satang) — auto-match
                    // never leaves an unaccounted remainder. Near-but-not-equal
                    // amounts are left for the operator to match manually / group.
                    if (Math.Abs(payment.Amount - txn.Amount) <= 0.01m)
                        score += 50;
                    else
                        continue;

                    // Flow-aware directional window (shared classifier) — a
                    // KSHOP/Thai-QR deposit only reaches back 1 day, a cheque
                    // 7, etc.; a receipt dated after the deposit can't fund it.
                    var autoWin = Bank.BankFlowClassifier.Window(txn.Description, txn.Payee, txn.Reference);
                    if (!Bank.BankFlowClassifier.InWindow(payment.PaymentDate, txn.TransactionDate, autoWin)) continue;
                    var daysDiff = Math.Abs((payment.PaymentDate.Date - txn.TransactionDate.Date).TotalDays);
                    if (daysDiff <= 0) score += 30;
                    else if (daysDiff <= 3) score += 20;
                    else score += 10;

                    // Reference match = 20 points
                    if (!string.IsNullOrEmpty(txn.Reference) && !string.IsNullOrEmpty(payment.Reference)
                        && txn.Reference.Contains(payment.Reference, StringComparison.OrdinalIgnoreCase))
                        score += 20;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMatch = payment;
                    }
                }

                // Only match if confidence is high enough (at least amount match + date proximity)
                if (bestMatch != null && bestScore >= 60)
                {
                    txn.ReconciliationStatus = ReconciliationStatus.Matched;
                    txn.MatchedPaymentId = bestMatch.Id;
                    txn.ReconciledAt = DateTime.UtcNow;
                    txn.ReconciledBy = "AutoMatch";
                    matched.Add(MapTransactionToResponse(txn));
                    alreadyMatchedPaymentIds.Add(bestMatch.Id);
                    processedKeySet.Add(txnKey);
                    continue;
                }

                // Try matching against JournalEntries if no Payment match found.
                // Exclude reversed (superseded) entries; backward 7 days /
                // forward 1 (a JE dated after the deposit can't have funded it).
                var journalEntries = await _db.JournalEntries
                    .Include(j => j.Lines)
                    .Where(j => j.CompanyId == companyId && j.Status == JournalEntryStatus.Posted
                        && j.ReversedByEntryId == null)
                    .Where(j => j.EntryDate >= txn.TransactionDate.Date.AddDays(-7)
                        && j.EntryDate <= txn.TransactionDate.Date.AddDays(1))
                    .ToListAsync();

                JournalEntry? bestJeMatch = null;
                decimal bestJeScore = 0;

                foreach (var je in journalEntries.Where(j => !alreadyMatchedJeIds.Contains(j.Id)))
                {
                    decimal jeScore = 0;

                    // Match on the NET that posts to the bank account (compound
                    // entries valued by what actually hit the bank); fall back to
                    // the JE total when it doesn't post to the bank account.
                    decimal jeBankAmt = autoBankCoaId.HasValue
                        ? Math.Abs(je.Lines.Where(l => l.AccountId == autoBankCoaId.Value)
                            .Sum(l => l.DebitAmount - l.CreditAmount))
                        : 0m;
                    if (jeBankAmt <= 0.01m) jeBankAmt = Math.Max(je.TotalDebit, je.TotalCredit);
                    if (Math.Abs(jeBankAmt - Math.Abs(txn.Amount)) <= 0.01m)
                        jeScore += 50;
                    else
                        continue;

                    // Flow-aware directional window (shared classifier).
                    var jeWin = Bank.BankFlowClassifier.Window(txn.Description, txn.Payee, txn.Reference);
                    if (!Bank.BankFlowClassifier.InWindow(je.EntryDate, txn.TransactionDate, jeWin)) continue;
                    var jeDaysDiff = Math.Abs((je.EntryDate.Date - txn.TransactionDate.Date).TotalDays);
                    if (jeDaysDiff <= 0) jeScore += 30;
                    else if (jeDaysDiff <= 3) jeScore += 20;
                    else jeScore += 10;

                    // Reference match
                    if (!string.IsNullOrEmpty(txn.Reference) && !string.IsNullOrEmpty(je.Reference)
                        && txn.Reference.Contains(je.Reference, StringComparison.OrdinalIgnoreCase))
                        jeScore += 20;

                    if (jeScore > bestJeScore) { bestJeScore = jeScore; bestJeMatch = je; }
                }

                if (bestJeMatch != null && bestJeScore >= 60)
                {
                    txn.ReconciliationStatus = ReconciliationStatus.Matched;
                    txn.MatchedJournalEntryId = bestJeMatch.Id;
                    txn.ReconciledAt = DateTime.UtcNow;
                    txn.ReconciledBy = "AutoMatch";
                    matched.Add(MapTransactionToResponse(txn));
                    alreadyMatchedJeIds.Add(bestJeMatch.Id);
                    processedKeySet.Add(txnKey);
                }
            }

            await _db.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return matched;
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }
    }

    public async Task<ImportBankStatementResponse> ImportBankStatementAsync(Guid companyId, ImportBankStatementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Base64Content))
            throw new ArgumentException("กรุณาระบุเนื้อหาไฟล์");

        var fmt = (request.FileFormat ?? "").Trim().ToUpperInvariant();
        if (fmt != "CSV" && fmt != "EXCEL" && fmt != "XLSX")
            throw new ArgumentException("รองรับเฉพาะรูปแบบ CSV และ Excel (.xlsx) เท่านั้น");

        var account = await _db.Set<BankAccount>()
            .FirstOrDefaultAsync(a => a.Id == request.BankAccountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        var fileBytes = Convert.FromBase64String(request.Base64Content);
        var (parsedRows, skippedRows) = (fmt == "EXCEL" || fmt == "XLSX")
            ? BankExcelParser.ParseStatement(fileBytes)
            : BankCsvParser.ParseStatement(fileBytes);

        if (parsedRows.Count == 0)
        {
            var hint = skippedRows.Count > 0
                ? $" (ตัวอย่างปัญหา: {string.Join("; ", skippedRows.Take(3))})"
                : "";
            throw new ArgumentException($"ไม่พบรายการที่นำเข้าได้จากไฟล์{hint}");
        }

        // Load existing transactions for duplicate detection
        var existingTxns = await _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId && t.BankAccountId == request.BankAccountId)
            .Select(t => new { t.Id, t.TransactionDate, t.Amount, t.TransactionType, t.Description, t.Reference })
            .ToListAsync();

        var conflicts = new List<ImportConflict>();
        int imported = 0, skipped = 0;

        await using var dbTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            decimal? lastBalance = null;
            var rowNum = 0;
            foreach (var r in parsedRows)
            {
                rowNum++;
                var isDeposit = r.Deposit > 0;
                var amount = isDeposit ? r.Deposit : r.Withdrawal;
                if (amount <= 0) continue;

                var desc = r.Description;
                if (!string.IsNullOrWhiteSpace(r.Channel)) desc = $"{desc} | {r.Channel}";
                if (desc.Length > 500) desc = desc[..500];

                var txnType = isDeposit ? BankTransactionType.Deposit : BankTransactionType.Withdrawal;

                // Check for duplicates: same date + amount + type
                var duplicate = existingTxns.FirstOrDefault(t =>
                    t.TransactionDate.Date == r.Date.Date
                    && t.Amount == amount
                    && t.TransactionType == txnType);

                if (duplicate != null)
                {
                    var isSameContent = string.Equals(
                        (duplicate.Description ?? "").Trim(),
                        desc.Trim(),
                        StringComparison.OrdinalIgnoreCase);

                    if (isSameContent)
                    {
                        skipped++;
                        lastBalance = r.Balance;
                        continue;
                    }

                    if (!request.ForceOverwrite)
                    {
                        conflicts.Add(new ImportConflict(
                            rowNum, r.Date, amount,
                            desc, duplicate.Description, duplicate.Id));
                        lastBalance = r.Balance;
                        continue;
                    }

                    // ForceOverwrite: update existing record
                    var existingEntity = await _db.Set<BankTransaction>()
                        .FirstAsync(t => t.Id == duplicate.Id);
                    existingEntity.Description = desc;
                    existingEntity.Reference = r.Reference;
                    existingEntity.BalanceAfter = r.Balance;
                    imported++;
                    lastBalance = r.Balance;
                    continue;
                }

                _db.Set<BankTransaction>().Add(new BankTransaction
                {
                    CompanyId = companyId,
                    BankAccountId = request.BankAccountId,
                    TransactionDate = r.Date,
                    TransactionType = txnType,
                    Amount = amount,
                    BalanceAfter = r.Balance,
                    Description = desc,
                    Reference = r.Reference
                });
                imported++;
                lastBalance = r.Balance;
            }

            if (conflicts.Count > 0 && !request.ForceOverwrite)
            {
                await dbTransaction.RollbackAsync();
                return new ImportBankStatementResponse(imported, skipped, conflicts.Count, conflicts);
            }

            if (imported > 0 && lastBalance.HasValue)
                account.CurrentBalance = lastBalance.Value;

            await _db.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            _logger?.LogInformation("Bank CSV import: {Imported} imported, {Skipped} duplicates skipped, {Conflicts} conflicts",
                imported, skipped, conflicts.Count);

            // Auto-match imported transactions with existing payments
            if (imported > 0)
            {
                try { await AutoMatchAsync(companyId, request.BankAccountId); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Auto-match after import failed (non-critical)"); }
            }

            return new ImportBankStatementResponse(imported, skipped, 0);
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }
    }

    private static BankAccountResponse MapToResponse(BankAccount a) => new(
        a.Id, a.AccountName, a.BankName, a.AccountNumber,
        a.BranchName, a.AccountType, a.Currency, a.CurrentBalance,
        a.LinkedAccountId, a.LinkedAccount?.AccountCode, a.LinkedAccount?.AccountName,
        a.IsActive);

    private static BankTransactionResponse MapTransactionToResponse(BankTransaction t) => new(
        t.Id, t.BankAccountId, t.TransactionDate, t.TransactionType,
        t.Amount, t.BalanceAfter, t.Description, t.Reference, t.Payee,
        t.ReconciliationStatus, t.MatchedPaymentId);
}

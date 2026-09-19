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
    private readonly IHttpContextAccessor? _httpContext;

    public BankService(AccountingDbContext db,
        ILogger<BankService>? logger = null,
        IHttpContextAccessor? httpContext = null)
    {
        _db = db;
        _logger = logger;
        _httpContext = httpContext;
    }

    /// <summary>Resolve the current request's authenticated user id from the
    /// JWT claims. Returns Guid.Empty when no HTTP context (background job)
    /// or no valid claim — callers should skip audit-log writes in that case.</summary>
    private Task<Guid> ResolveCurrentUserIdAsync(Guid companyId)
    {
        var ctx = _httpContext?.HttpContext;
        if (ctx?.User is null) return Task.FromResult(Guid.Empty);
        try
        {
            return Task.FromResult(Accounting.Helpers.JwtHelper.GetUserIdFromClaims(ctx.User));
        }
        catch
        {
            return Task.FromResult(Guid.Empty);
        }
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

        if (!string.IsNullOrWhiteSpace(request.AccountType) && !ValidBankAccountTypes.Contains(request.AccountType))
            throw new ArgumentException("ประเภทบัญชีต้องเป็น Savings (ออมทรัพย์), Current (กระแสรายวัน) หรือ Fixed (ฝากประจำ)");

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

    private static readonly string[] ValidBankAccountTypes = { "Savings", "Current", "Fixed" };

    /// <summary>รหัสกลุ่มเงินฝากในผังบัญชีตามประเภทบัญชีธนาคาร</summary>
    private static string ParentCodeForAccountType(string? accountType) => accountType switch
    {
        "Current" => "11121",
        "Fixed" => "11123",
        _ => "11122" // Savings as default
    };

    private async Task<ChartOfAccount?> AutoCreateLinkedAccountAsync(Guid companyId, CreateBankAccountRequest request)
    {
        var parentCode = ParentCodeForAccountType(request.AccountType);

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
            // ยอดตามธนาคาร (statement ล่าสุด) เทียบยอดตามบัญชี — ผลต่าง = งานกระทบยอดที่ค้าง
            var bookBalance = a.LinkedAccountId.HasValue ? gl : a.CurrentBalance;
            return new BankAccountResponse(
                a.Id, a.AccountName, a.BankName, a.AccountNumber,
                a.BranchName, a.AccountType, a.Currency, displayBalance,
                a.LinkedAccountId, a.LinkedAccount?.AccountCode, a.LinkedAccount?.AccountName,
                a.IsActive,
                a.StatementBalance, a.StatementBalanceDate, a.StatementImportedAt,
                bookBalance,
                a.StatementBalance.HasValue ? a.StatementBalance.Value - bookBalance : null);
        }).ToList();
    }

    public async Task<BankAccountResponse> UpdateBankAccountAsync(Guid companyId, Guid accountId, UpdateBankAccountRequest request)
    {
        var account = await _db.Set<BankAccount>()
            .Include(a => a.LinkedAccount)
            .FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        if (request.AccountName != null) account.AccountName = request.AccountName;
        if (request.BranchName != null) account.BranchName = request.BranchName;
        if (request.IsActive.HasValue) account.IsActive = request.IsActive.Value;
        if (request.LinkedAccountId.HasValue)
        {
            // ⚠ เดิมรับ id จาก client **ดิบ ๆ** ⇒ ผูกบัญชีธนาคารกับผังของ
            // บริษัทอื่นได้ (ละเมิด tenant isolation — กฎเหล็ก #2 M) และผูกกับ
            // ผังที่ไม่ใช่เงินสด/เงินฝากได้ ⇒ ตัวกระทบยอดอ่าน "ขาที่วิ่งผ่าน
            // บัญชีนี้" ผิดใบทุกใบ
            var coa = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.Id == request.LinkedAccountId.Value && a.CompanyId == companyId && !a.IsDeleted)
                .Select(a => new { a.AccountCode, a.IsActive })
                .FirstOrDefaultAsync()
                ?? throw new KeyNotFoundException("ไม่พบผังบัญชีที่เลือกในบริษัทนี้");
            if (!coa.IsActive)
                throw new InvalidOperationException("ผังบัญชีที่เลือกถูกปิดใช้งานอยู่ — เลือกบัญชีอื่น");
            if (!coa.AccountCode.StartsWith("111"))
                throw new InvalidOperationException(
                    $"ผังบัญชี {coa.AccountCode} ไม่ใช่กลุ่มเงินสด/เงินฝากธนาคาร (111x) — "
                    + "การกระทบยอดอ่านยอดจาก \"ขาที่วิ่งผ่านบัญชีนี้\" จึงต้องผูกกับผังเงินฝากเท่านั้น");
            account.LinkedAccountId = request.LinkedAccountId.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.BankName))
            account.BankName = request.BankName;

        if (!string.IsNullOrWhiteSpace(request.AccountNumber))
        {
            if (request.AccountNumber.Length < 5 || request.AccountNumber.Length > 20)
                throw new ArgumentException("เลขที่บัญชีต้องมีความยาว 5-20 ตัวอักษร");
            account.AccountNumber = request.AccountNumber;
        }

        if (!string.IsNullOrWhiteSpace(request.Currency))
        {
            if (request.Currency.Length != 3 || request.Currency != request.Currency.ToUpperInvariant())
                throw new ArgumentException("สกุลเงินต้องเป็นรหัส 3 ตัวอักษรพิมพ์ใหญ่ (เช่น THB, USD)");
            account.Currency = request.Currency;
        }

        var typeChanged = false;
        if (!string.IsNullOrWhiteSpace(request.AccountType) && request.AccountType != account.AccountType)
        {
            if (!ValidBankAccountTypes.Contains(request.AccountType))
                throw new ArgumentException("ประเภทบัญชีต้องเป็น Savings (ออมทรัพย์), Current (กระแสรายวัน) หรือ Fixed (ฝากประจำ)");
            account.AccountType = request.AccountType;
            typeChanged = true;
        }

        // บัญชีย่อยในผังที่ระบบสร้างให้อัตโนมัติ → ปรับตามข้อมูลใหม่
        // (ย้ายกลุ่ม 11121/11122/11123 เมื่อเปลี่ยนประเภท + ชื่อธนาคาร/เลขบัญชีใหม่)
        // JE อ้าง AccountId ไม่ใช่รหัสบัญชี — เปลี่ยนรหัส/parent ไม่กระทบรายการที่ลงแล้ว
        await SyncLinkedAccountAsync(companyId, account, typeChanged);

        await _db.SaveChangesAsync();
        return MapToResponse(account);
    }

    /// <summary>
    /// ทำให้บัญชีย่อยในผังบัญชี (ที่ AutoCreateLinkedAccountAsync สร้าง) สอดคล้อง
    /// กับข้อมูลบัญชีธนาคารหลังแก้ไข — เฉพาะบัญชีที่ IsSystemAccount และอยู่ใต้กลุ่ม
    /// เงินฝากธนาคาร (11121/11122/11123) เท่านั้น; บัญชีที่ผู้ใช้เลือกผูกเองไม่ถูกแตะ
    /// </summary>
    private async Task SyncLinkedAccountAsync(Guid companyId, BankAccount account, bool typeChanged)
    {
        if (!account.LinkedAccountId.HasValue) return;

        var linked = account.LinkedAccount
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.Id == account.LinkedAccountId.Value && a.CompanyId == companyId);
        if (linked == null || !linked.IsSystemAccount || linked.ParentAccountId == null) return;

        var currentParent = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.Id == linked.ParentAccountId.Value && a.CompanyId == companyId);
        if (currentParent == null || currentParent.AccountCode is not ("11121" or "11122" or "11123"))
            return; // ไม่ใช่บัญชีที่ระบบสร้างใต้กลุ่มเงินฝาก — ไม่ยุ่ง

        var targetParent = currentParent;
        if (typeChanged)
        {
            var targetCode = ParentCodeForAccountType(account.AccountType);
            if (targetCode != currentParent.AccountCode)
            {
                targetParent = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == targetCode && a.IsActive)
                    ?? throw new InvalidOperationException(
                        $"ไม่พบบัญชีผังบัญชีหลัก {targetCode} สำหรับประเภท {account.AccountType} — " +
                        "กรุณาสร้างบัญชีกลุ่มเงินฝากธนาคารในผังบัญชีก่อน");

                var siblings = await _db.ChartOfAccounts
                    .Where(a => a.CompanyId == companyId && a.ParentAccountId == targetParent.Id)
                    .Select(a => a.AccountCode)
                    .ToListAsync();
                var nextSeq = 1;
                foreach (var code in siblings)
                {
                    var suffix = code.Replace(targetCode + "-", "");
                    if (int.TryParse(suffix, out var num) && num >= nextSeq)
                        nextSeq = num + 1;
                }

                linked.ParentAccountId = targetParent.Id;
                linked.Level = targetParent.Level + 1;
                linked.AccountCode = $"{targetCode}-{nextSeq:D3}";
            }
        }

        var maskedNumber = account.AccountNumber.Length >= 4
            ? "xxx-" + account.AccountNumber[^4..]
            : account.AccountNumber;
        linked.AccountName = $"{targetParent.AccountName} - {account.BankName} {maskedNumber}";
        linked.AccountNameEn = $"{targetParent.AccountNameEn ?? targetParent.AccountName} - {account.BankName} {maskedNumber}";
        linked.Description = $"สร้างอัตโนมัติจากบัญชีธนาคาร: {account.BankName} {account.AccountNumber}";
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

        // ชื่อผู้ใช้สำหรับป้าย "👤 <ชื่อ>" — ค้นครั้งเดียวทั้งหน้า
        var names = await ResolveReconcilerNamesAsync(companyId, items);
        // ⚠ ห้ามส่ง method group ที่มีพารามิเตอร์ optional เข้า `Select`
        // (CS0411 — บทเรียน กฎเหล็ก #4 H) ต้องเป็น lambda เสมอ
        return new PagedResponse<BankTransactionResponse>(
            items.Select(t => MapTransactionToResponse(t, LookupReconcilerName(names, t))).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    /// <summary>
    /// ด่านความถูกต้องของยอด — รายการฝั่งเดียวกับบรรทัดธนาคาร **บวก**,
    /// ฝั่งตรงข้าม **หัก** (net-settlement เช่น ใบเสร็จ 2,500 − ใบสำคัญจ่าย 500
    /// = 2,000 สุทธิเข้าบัญชี). ที่ถูกบล็อกคือการหักกันภายในฝั่งเดียวกัน
    ///
    /// เลขคณิตย้ายไป <see cref="Accounting.Helpers.BankMatchAmountReconciler"/>
    /// (pure + มีเทสต์) — เมธอดนี้เหลือหน้าที่ "ดึงข้อมูลจาก DB แล้วแปลงเป็น
    /// อินพุตของตัวกระทบยอด". สองช่องที่ `DECISION_AUDIT_2026-09-18.md` §3 D4-5
    /// ระบุถูกปิดในตัวกระทบยอดนั้น:
    ///   (1) ชนิดที่ไม่รู้ทิศ เดิม "บวกเงียบ ๆ (legacy safe default)" →
    ///       ตอนนี้ **ปฏิเสธพร้อมบอกว่าใบไหน**
    ///   (2) เดิม "ผ่านถ้า net หรือ gross ตรง" = สองโอกาสผ่านต่อใบ →
    ///       ตอนนี้แต่ละใบมียอดเงินสดที่คาดว่าผ่านธนาคาร **ค่าเดียว**
    /// </summary>
    private async Task ValidateMatchAmountAsync(Guid companyId, BankTransaction txn, IEnumerable<Guid> matchedIds)
    {
        var ids = matchedIds.Where(i => i != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return;

        var bankMeta = await _db.Set<BankAccount>().AsNoTracking()
            .Where(a => a.Id == txn.BankAccountId && a.CompanyId == companyId)
            .Select(a => new { a.LinkedAccountId, a.Currency })
            .FirstOrDefaultAsync();
        var bankCoaId = bankMeta?.LinkedAccountId;
        var bankCurrency = bankMeta?.Currency;
        // สกุลฐานของบริษัท — อัตราแลกเปลี่ยนบนเอกสารเทียบกับสกุลนี้เสมอ
        var homeCurrency = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.BaseCurrency).FirstOrDefaultAsync();

        var bankDirection = txn.TransactionType is BankTransactionType.Deposit
            or BankTransactionType.Interest
            ? Accounting.Helpers.BankFlowDirection.Inflow
            : Accounting.Helpers.BankFlowDirection.Outflow;

        var items = new List<Accounting.Helpers.BankMatchAmountReconciler.Item>();

        var pays = await _db.Payments.AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.CompanyId == companyId)
            .Select(p => new
            {
                p.Id, p.Amount, p.WithholdingTaxAmount, p.FeeAmount,
                p.ExchangeRate,
                DocType = p.Document.DocumentType,
                DocNo = p.Document.DocumentNumber,
                DocCurrency = p.Document.Currency,
                DocRate = p.Document.ExchangeRate
            })
            .ToListAsync();
        foreach (var p in pays)
        {
            // ── FX: `Payment.Amount` อยู่ใน **สกุลของเอกสาร** ส่วนยอดบนบรรทัด
            // ธนาคารอยู่ใน **สกุลของบัญชี** — เดิมเทียบกันตรง ๆ ⇒ ใบ 1,000 USD
            // ที่เงินเข้า 35,000 บาท **กระทบยอดไม่ได้เลย** (throw ทุกครั้ง)
            // (`DECISION_AUDIT_2026-09-18.md` §3 D4-8 "FX")
            var fx = Accounting.Helpers.BankMatchCurrency.Convert(
                bankCurrency, p.DocCurrency, p.Amount, p.ExchangeRate, p.DocRate, homeCurrency);
            if (!fx.Comparable)
                throw new InvalidOperationException(
                    $"{p.DocType} {p.DocNo}: {fx.Reason}");
            bool payIsIn = p.DocType is DocumentType.Receipt or DocumentType.ReceiptVoucher
                or DocumentType.Invoice or DocumentType.TaxInvoice
                or DocumentType.BillingNote or DocumentType.DebitNote;
            bool payIsOut = p.DocType is DocumentType.PaymentVoucher or DocumentType.Expense
                or DocumentType.PurchaseInvoice or DocumentType.CertificateInLieu;
            var dir = payIsIn ? Accounting.Helpers.BankFlowDirection.Inflow
                : payIsOut ? Accounting.Helpers.BankFlowDirection.Outflow
                : Accounting.Helpers.BankFlowDirection.Unknown;   // ⬅ เดิมบวกเงียบ ๆ
            items.Add(new Accounting.Helpers.BankMatchAmountReconciler.Item(
                p.Id,
                $"{p.DocType} {p.DocNo}".Trim(),
                dir,
                RecordedAmount: p.Amount,
                BankLineAmount: null,
                WithheldAmount: p.WithholdingTaxAmount,
                FeeAmount: p.FeeAmount,
                // หักยอดในสกุลเอกสารให้เสร็จก่อนค่อยแปลง — แปลงทีละช่อง
                // จะปัดเศษ 3 ครั้ง (คลาดได้ถึง 1.5 สตางค์ > tolerance 1 สตางค์)
                ConversionRate: fx.Rate));
        }

        var payIds = pays.Select(p => p.Id).ToHashSet();
        var jeIds = ids.Where(i => !payIds.Contains(i)).ToList();
        if (jeIds.Count > 0)
        {
            var jeHeads = await _db.JournalEntries.AsNoTracking()
                .Where(j => jeIds.Contains(j.Id) && j.CompanyId == companyId)
                .Select(j => new { j.Id, j.TotalDebit, j.EntryNumber })
                .ToListAsync();
            var jeById = jeHeads.ToDictionary(j => j.Id);

            var jeNetSigned = new Dictionary<Guid, decimal>();
            if (bankCoaId.HasValue)
            {
                var lines = await _db.JournalEntryLines.AsNoTracking()
                    .Where(l => jeIds.Contains(l.JournalEntryId) && l.AccountId == bankCoaId.Value)
                    .Select(l => new { l.JournalEntryId, Net = l.DebitAmount - l.CreditAmount })
                    .ToListAsync();
                jeNetSigned = lines.GroupBy(l => l.JournalEntryId)
                    .ToDictionary(g => g.Key, g => g.Sum(x => x.Net));
            }

            foreach (var id in jeIds)
            {
                var label = jeById.TryGetValue(id, out var h) && !string.IsNullOrWhiteSpace(h.EntryNumber)
                    ? $"สมุดรายวัน {h.EntryNumber}" : $"สมุดรายวัน {id.ToString("N")[..8]}";
                var gross = jeById.TryGetValue(id, out var hh) ? hh.TotalDebit : 0m;

                if (jeNetSigned.TryGetValue(id, out var signed) && Math.Abs(signed) >= 0.01m)
                {
                    items.Add(new Accounting.Helpers.BankMatchAmountReconciler.Item(
                        id, label,
                        signed > 0 ? Accounting.Helpers.BankFlowDirection.Inflow
                                   : Accounting.Helpers.BankFlowDirection.Outflow,
                        RecordedAmount: gross,
                        BankLineAmount: signed));
                    continue;
                }

                // JE ที่ไม่แตะบัญชีธนาคารเลย → **ไม่รู้ทิศ** เดิมบวกเข้าไปเงียบ ๆ
                // (ถ้ามันไม่แตะบัญชีธนาคาร มันก็ไม่ใช่ต้นทางของเงินก้อนนี้)
                // ⚠ กรณีบัญชีธนาคารยังไม่ผูกผังบัญชี เราไม่มีทางรู้ทิศของ JE ใด ๆ
                // เลย — บอกผู้ใช้ตรง ๆ ว่าต้องไปผูกก่อน (ห้ามเดาทิศให้)
                var why = bankCoaId.HasValue
                    ? " (ไม่มีบรรทัดลงบัญชีธนาคารนี้)"
                    : " (บัญชีธนาคารนี้ยังไม่ได้ผูกกับผังบัญชี — ตั้งค่าบัญชีธนาคาร → ผังบัญชีที่เชื่อมโยง)";
                items.Add(new Accounting.Helpers.BankMatchAmountReconciler.Item(
                    id, label + why, Accounting.Helpers.BankFlowDirection.Unknown,
                    RecordedAmount: gross));
            }
        }

        var result = Accounting.Helpers.BankMatchAmountReconciler.Reconcile(
            txn.Amount, bankDirection, items);
        if (result.Ok) return;

        // ระบุบรรทัดธนาคารที่มีปัญหาให้ผู้ใช้หาเจอ
        var sign = txn.TransactionType == BankTransactionType.Deposit ? "+" : "-";
        var desc = (txn.Description ?? txn.Payee ?? "").Trim();
        if (desc.Length > 40) desc = desc[..40] + "…";
        var who = $"รายการธนาคาร {txn.TransactionDate:dd/MM/yyyy} {sign}{Math.Abs(txn.Amount):N2}" +
                  (string.IsNullOrWhiteSpace(desc) ? "" : $" ({desc})");
        throw new InvalidOperationException($"{who}: {result.Message}");
    }

    public async Task<BankTransactionResponse> ReconcileAsync(Guid companyId, ReconcileRequest request)
    {
        var transaction = await _db.Set<BankTransaction>()
            .FirstOrDefaultAsync(t => t.Id == request.BankTransactionId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการธนาคาร");

        // FISCAL PERIOD GUARD — once a period is Closed/Locked the books are
        // frozen for audit. Allowing a reconcile in a closed period would
        // shift cash without an audit trail. Block at the door.
        await EnsureFiscalPeriodOpenAsync(companyId, transaction.TransactionDate, "reconcile");

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
        // ข้อเสนอเดิมถูกแทนที่ด้วยคำตอบของคน — ล้างทิ้ง ไม่งั้นแถวถือคู่สองชุด
        transaction.SuggestedDocumentId = null;
        transaction.ReconciledAt = DateTime.UtcNow;
        // ⚠ เดิม **ไม่เคยตั้ง `ReconciledBy`** ที่นี่เลย ⇒ แถวที่คนกดจับคู่เอง
        // ยังค้างค่าเดิม ("AutoMatch (เสนอ รอยืนยัน)") ⇒ หน้าจอจะบอกว่า
        // "⚙️ ระบบ" ทั้งที่คนเป็นคนตัดสิน (ป้ายโกหก — กฎเหล็ก #1)
        var reconcilerId = await ResolveCurrentUserIdAsync(companyId);
        transaction.ReconciledBy = Accounting.Helpers.BankMatchAttribution.Person(reconcilerId);
        transaction.MatchRuleCode = "BANK-MATCH-MANUAL";
        transaction.MatchReason = "ผู้ใช้เลือกคู่เองจากหน้าจับคู่";

        // CAPTURE (กฎเหล็ก #1) — การจับคู่ที่ **คนยืนยัน** คือคำตอบที่เชื่อได้
        // ที่สุดของโดเมนนี้ เดิมเส้น 1:1 ไม่สอนอะไรกลับเข้าคลังเลย
        var captured = new List<(ReconciliationItemType Type, Guid Id, decimal Amount)>();
        if (request.MatchedPaymentId.HasValue)
            captured.Add((ReconciliationItemType.Payment, request.MatchedPaymentId.Value, Math.Abs(transaction.Amount)));
        else
            captured.Add((ReconciliationItemType.JournalEntry, request.MatchedJournalEntryId!.Value, Math.Abs(transaction.Amount)));
        await CaptureConfirmedMatchAsync(companyId, transaction, captured);

        await _db.SaveChangesAsync();
        var reconcilerName = (await CompanyMemberNamesAsync(companyId, new[] { reconcilerId }))
            .Values.FirstOrDefault();
        return MapTransactionToResponse(transaction, reconcilerName);
    }

    public async Task<List<BankTransactionResponse>> GetUnreconciledAsync(Guid companyId, Guid bankAccountId)
    {
        // `Suggested` = "ระบบเสนอคู่ไว้ แต่ยังไม่มีใครยืนยัน" ⇒ **ยังไม่ได้
        // กระทบยอด** ต้องอยู่ในลิสต์นี้เหมือน `Unmatched`
        // (`BankService.Reconciliation.cs` ก็นับสองสถานะนี้เป็น pending อยู่แล้ว)
        // ถ้าไม่รวม รายการที่ arbiter ลดชั้นจาก `Matched` → `Suggested` จะ
        // **หายไปจากหน้าจับคู่ด้วยมือและตัวสร้างกลุ่ม M:N** = ผู้ใช้ไม่มีทางไปต่อ
        var transactions = await _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId
                && t.BankAccountId == bankAccountId
                && (t.ReconciliationStatus == ReconciliationStatus.Unmatched
                    || t.ReconciliationStatus == ReconciliationStatus.Suggested))
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync();

        var unreconciledNames = await ResolveReconcilerNamesAsync(companyId, transactions);
        return transactions
            .Select(t => MapTransactionToResponse(t, LookupReconcilerName(unreconciledNames, t)))
            .ToList();
    }

    public async Task<List<BankTransactionResponse>> AutoMatchAsync(Guid companyId, Guid bankAccountId)
    {
        await using var dbTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Bank's GL account — lets JE matching use the net that actually hit
            // the bank (compound entries), consistent with the shared resolver.
            var autoBankMeta = await _db.Set<BankAccount>().AsNoTracking()
                .Where(a => a.Id == bankAccountId && a.CompanyId == companyId)
                .Select(a => new { a.LinkedAccountId, a.Currency }).FirstOrDefaultAsync();
            var autoBankCoaId = autoBankMeta?.LinkedAccountId;
            var autoBankCurrency = autoBankMeta?.Currency;
            var autoHomeCurrency = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId).Select(c => c.BaseCurrency).FirstOrDefaultAsync();

            var unmatched = await _db.Set<BankTransaction>()
                .Where(t => t.CompanyId == companyId
                    && t.BankAccountId == bankAccountId
                    && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
                .ToListAsync();

            // ⚠ เดิมกรอง `p.PaymentMethod == PaymentMethod.BankTransfer` ตรงนี้
            // ⇒ **PromptPay / QR / เช็คที่ขึ้นเงินแล้ว / บัตร หลุดจากการจับคู่
            // อัตโนมัติทั้งหมด** ทั้งที่เงินเข้าบัญชีธนาคารจริง
            // (`DECISION_AUDIT_2026-09-18.md` §3 D4-1 · `BankService.cs:595`)
            // ช่องทางการชำระไม่ใช่เครื่องตัดสินว่าเงินผ่านธนาคารไหม — ให้
            // ตัวให้คะแนนกลางตัดสินจากยอด/วัน/ชื่อผู้โอน/เลขอ้างอิงแทน
            var payments = await _db.Payments
                .Include(p => p.Document)
                .Where(p => p.CompanyId == companyId && !p.IsDeleted
                    && p.Document.Status != DocumentStatus.Voided)
                .ToListAsync();
            // เรียกแบบ static (ไม่ import `Accounting.Helpers` ทั้ง namespace เพราะ
            // ไฟล์นี้ import `Accounting.Services.Helpers` อยู่แล้ว — ชนกันได้)
            await Accounting.Helpers.ContactHydration.HydratePaymentContactsAsync(_db, companyId, payments);

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

                // ═══ ให้คะแนนด้วยสูตรกลางตัวเดียว + ให้ arbiter ตัดสิน ═══
                // เดิมที่นี่มีสูตรของตัวเอง (ยอด 50 / วัน 30 / อ้างอิง 20) ที่
                // **ไม่ดูชื่อผู้โอนเลย** แล้วใช้ `score > bestScore` = ใครมาก่อน
                // ชนะ ⇒ ใบสองใบที่ยอด+วันเท่ากันเป๊ะได้คำตอบตามลำดับแถวใน DB
                // และอันดับที่เครื่องประทับไม่ตรงกับอันดับที่คนเห็นบนจอ
                // (GAP-1 · `DECISION_DOCTRINE.md` §4.2)
                var autoWin = Bank.BankFlowClassifier.Window(txn.Description, txn.Payee, txn.Reference);
                var options = new List<Accounting.Helpers.BankMatchArbiter.Option>();
                foreach (var payment in candidates)
                {
                    // หน้าต่างเวลาตามชนิดกระแสเงิน (ยังเป็นด่านแข็งเหมือนเดิม —
                    // ใบที่ลงวันหลังเงินเข้าไม่มีทางเป็นต้นทางของเงินก้อนนั้น)
                    if (!Bank.BankFlowClassifier.InWindow(payment.PaymentDate, txn.TransactionDate, autoWin))
                        continue;

                    // ── FX: เทียบยอดข้ามสกุลไม่ได้ ⇒ **ข้ามผู้สมัครรายนั้น** ──
                    // เส้นนี้ประทับสถานะเอง ทิศปลอดภัยคือไม่ตอบ (G5) — ใบที่ถูก
                    // ข้ามยังโผล่ในหน้าจับคู่ด้วยมือพร้อมเหตุผล ผู้ใช้จึงมีทางไปต่อ
                    var payFx = Accounting.Helpers.BankMatchCurrency.Convert(
                        autoBankCurrency, payment.Document?.Currency, payment.Amount,
                        payment.ExchangeRate, payment.Document?.ExchangeRate, autoHomeCurrency);
                    if (!payFx.Comparable) continue;

                    var sc = Accounting.Helpers.BankMatchScorer.Score(new Accounting.Helpers.BankMatchScorer.Input(
                        CandidateAmount: payFx.BankCurrencyAmount,
                        BankAmount: Math.Abs(txn.Amount),
                        CandidateDate: payment.PaymentDate,
                        BankDate: txn.TransactionDate,
                        CandidateRef: payment.Reference,
                        CandidateNotes: payment.Notes,
                        CandidateDocNumber: payment.Document?.DocumentNumber,
                        CandidateName: payment.Document?.Contact?.Name,
                        BankDescription: txn.Description,
                        BankReference: txn.Reference,
                        BankPayee: txn.Payee));
                    options.Add(new Accounting.Helpers.BankMatchArbiter.Option(
                        payment.Id, "Payment", sc.Score, sc.HasIdentitySignal, sc.Reason));
                }

                var payDecision = Accounting.Helpers.BankMatchArbiter.Decide(options);

                if (payDecision.Verdict == Accounting.Helpers.BankMatchVerdict.Apply)
                {
                    txn.ReconciliationStatus = ReconciliationStatus.Matched;
                    txn.MatchedPaymentId = payDecision.Chosen!.Id;
                    txn.SuggestedDocumentId = null;
                    txn.ReconciledAt = DateTime.UtcNow;
                    txn.ReconciledBy = Accounting.Helpers.BankMatchAttribution.AutoMatch;
                    txn.MatchRuleCode = payDecision.RuleCode;
                    txn.MatchReason = payDecision.Reason;
                    matched.Add(MapTransactionToResponse(txn));
                    alreadyMatchedPaymentIds.Add(payDecision.Chosen.Id);
                    processedKeySet.Add(txnKey);
                    continue;
                }

                if (payDecision.Verdict == Accounting.Helpers.BankMatchVerdict.Suggest)
                {
                    // แยกไม่ออกด้วยหลักฐานที่มี → **เสนอ** ไม่ประทับ
                    // (แต่ยังเก็บ id ของคู่ที่เสนอไว้เสมอ — ห้ามมีสถานะที่ไม่มีคู่)
                    txn.ReconciliationStatus = ReconciliationStatus.Suggested;
                    txn.MatchedPaymentId = payDecision.Chosen!.Id;
                    txn.ReconciledBy = Accounting.Helpers.BankMatchAttribution.AutoMatchSuggested;
                    txn.MatchRuleCode = payDecision.RuleCode;
                    txn.MatchReason = payDecision.Reason;
                    alreadyMatchedPaymentIds.Add(payDecision.Chosen.Id);
                    _logger?.LogInformation(
                        "Bank txn {Txn}: เสนอรายการชำระ {PaymentId} ({Rule}) — {Reason}",
                        txn.Id, payDecision.Chosen.Id, payDecision.RuleCode, payDecision.Reason);
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

                // ฝั่ง JE ใช้ **สูตรกลางตัวเดียวกัน** กับฝั่ง Payment และกับ
                // รายการที่มนุษย์เห็น (เดิมเป็นสำเนาที่ 6 ของสูตร: 50/30/20)
                var jeOptions = new List<Accounting.Helpers.BankMatchArbiter.Option>();
                var jeWin = Bank.BankFlowClassifier.Window(txn.Description, txn.Payee, txn.Reference);
                foreach (var je in journalEntries.Where(j => !alreadyMatchedJeIds.Contains(j.Id)))
                {
                    if (!Bank.BankFlowClassifier.InWindow(je.EntryDate, txn.TransactionDate, jeWin)) continue;

                    // ยอดที่ลงบัญชีธนาคารจริง (compound entry วัดด้วยเงินที่ขยับ
                    // ในบัญชีนั้น) — ไม่มีบรรทัดธนาคารจึงค่อยถอยไปใช้ยอดรวม
                    decimal jeBankAmt = autoBankCoaId.HasValue
                        ? Math.Abs(je.Lines.Where(l => l.AccountId == autoBankCoaId.Value)
                            .Sum(l => l.DebitAmount - l.CreditAmount))
                        : 0m;
                    if (jeBankAmt <= 0.01m) jeBankAmt = Math.Max(je.TotalDebit, je.TotalCredit);

                    var jeSc = Accounting.Helpers.BankMatchScorer.Score(new Accounting.Helpers.BankMatchScorer.Input(
                        CandidateAmount: jeBankAmt,
                        BankAmount: Math.Abs(txn.Amount),
                        CandidateDate: je.EntryDate,
                        BankDate: txn.TransactionDate,
                        CandidateRef: je.Reference,
                        CandidateNotes: je.Description,
                        CandidateDocNumber: je.EntryNumber,
                        CandidateName: null,
                        BankDescription: txn.Description,
                        BankReference: txn.Reference,
                        BankPayee: txn.Payee));
                    jeOptions.Add(new Accounting.Helpers.BankMatchArbiter.Option(
                        je.Id, "JournalEntry", jeSc.Score, jeSc.HasIdentitySignal, jeSc.Reason));
                }

                var jeDecision = Accounting.Helpers.BankMatchArbiter.Decide(jeOptions);
                if (jeDecision.Verdict == Accounting.Helpers.BankMatchVerdict.Apply)
                {
                    txn.ReconciliationStatus = ReconciliationStatus.Matched;
                    txn.MatchedJournalEntryId = jeDecision.Chosen!.Id;
                    txn.SuggestedDocumentId = null;
                    txn.ReconciledAt = DateTime.UtcNow;
                    txn.ReconciledBy = Accounting.Helpers.BankMatchAttribution.AutoMatch;
                    txn.MatchRuleCode = jeDecision.RuleCode;
                    txn.MatchReason = jeDecision.Reason;
                    matched.Add(MapTransactionToResponse(txn));
                    alreadyMatchedJeIds.Add(jeDecision.Chosen.Id);
                    processedKeySet.Add(txnKey);
                }
                else if (jeDecision.Verdict == Accounting.Helpers.BankMatchVerdict.Suggest)
                {
                    txn.ReconciliationStatus = ReconciliationStatus.Suggested;
                    txn.MatchedJournalEntryId = jeDecision.Chosen!.Id;
                    txn.ReconciledBy = Accounting.Helpers.BankMatchAttribution.AutoMatchSuggested;
                    txn.MatchRuleCode = jeDecision.RuleCode;
                    txn.MatchReason = jeDecision.Reason;
                    alreadyMatchedJeIds.Add(jeDecision.Chosen.Id);
                    _logger?.LogInformation(
                        "Bank txn {Txn}: เสนอสมุดรายวัน {JeId} ({Rule}) — {Reason}",
                        txn.Id, jeDecision.Chosen.Id, jeDecision.RuleCode, jeDecision.Reason);
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

        // ยอดคงเหลือ "ล่าสุดจริง" ของ statement — ห้ามใช้แถวสุดท้ายของไฟล์ตรง ๆ
        // เพราะธนาคารไทยหลายแห่ง export แบบใหม่→เก่า (ยอดแถวสุดท้าย = ยอดเก่าสุด)
        // ใช้แถวที่วันที่มากสุด "ที่มียอดคงเหลือจริง" (Balance=null คือช่องว่าง);
        // หลายแถววันเดียวกันเคารพลำดับในไฟล์ (เก่า→ใหม่เอาแถวท้าย, ใหม่→เก่าเอาแถวแรก)
        var newestFirst = parsedRows.Count > 1 && parsedRows[0].Date > parsedRows[^1].Date;
        BankCsvParser.Row? latestRow = null;
        var balanceRows = parsedRows.Where(r => r.Balance.HasValue).ToList();
        if (balanceRows.Count > 0)
        {
            var maxDate = balanceRows.Max(r => r.Date);
            latestRow = newestFirst
                ? balanceRows.First(r => r.Date == maxDate)
                : balanceRows.Last(r => r.Date == maxDate);
        }

        // ตรวจความต่อเนื่องของยอด: balance แถวถัดไปต้อง = แถวก่อน + ฝาก − ถอน
        // จุดที่ไม่ต่อเนื่อง = ไฟล์ขาดรายการ (ตัดหน้า/กรองบางประเภทออกตอน export)
        // → เตือน เพราะนำเข้าต่อไปเฉย ๆ จะได้ยอดบัญชีที่ไม่มีวันตรงกับธนาคาร
        var chronological = newestFirst ? Enumerable.Reverse(parsedRows).ToList() : parsedRows;
        var continuityBreaks = 0;
        for (var ci = 1; ci < chronological.Count; ci++)
        {
            var prevBal = chronological[ci - 1].Balance;
            var curBal = chronological[ci].Balance;
            if (!prevBal.HasValue || !curBal.HasValue) continue;
            var expected = prevBal.Value + chronological[ci].Deposit - chronological[ci].Withdrawal;
            if (Math.Abs(expected - curBal.Value) > 0.01m) continuityBreaks++;
        }
        var warnings = new List<string>();
        if (continuityBreaks > 0)
            warnings.Add($"⚠ ยอดคงเหลือในไฟล์ไม่ต่อเนื่อง {continuityBreaks} จุด — ไฟล์อาจขาดรายการ " +
                "(ถูกตัดหน้า/กรองบางประเภทออกตอน export) ยอดบัญชีอาจไม่ตรงธนาคารจนกว่าจะนำเข้าครบ");

        await using var dbTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var rowNum = 0;
            var usedExisting = new HashSet<Guid>();
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
                // จับคู่แบบใช้ครั้งเดียว — statement มี 2 รายการยอดเท่ากันวันเดียวกัน
                // ได้จริง (เช่น PromptPay 55 บาท 2 ครั้ง) ห้ามให้ทั้งคู่ชนแถวเดิม
                // แถวเดียวแล้วหายไป 1 รายการ
                var duplicate = existingTxns.FirstOrDefault(t =>
                    !usedExisting.Contains(t.Id)
                    && t.TransactionDate.Date == r.Date.Date
                    && t.Amount == amount
                    && t.TransactionType == txnType);

                if (duplicate != null)
                {
                    usedExisting.Add(duplicate.Id);
                    var isSameContent = string.Equals(
                        (duplicate.Description ?? "").Trim(),
                        desc.Trim(),
                        StringComparison.OrdinalIgnoreCase);

                    if (isSameContent)
                    {
                        skipped++;
                        continue;
                    }

                    if (!request.ForceOverwrite)
                    {
                        conflicts.Add(new ImportConflict(
                            rowNum, r.Date, amount,
                            desc, duplicate.Description, duplicate.Id));
                        continue;
                    }

                    // ForceOverwrite: update existing record
                    var existingEntity = await _db.Set<BankTransaction>()
                        .FirstAsync(t => t.Id == duplicate.Id);
                    existingEntity.Description = desc;
                    existingEntity.Reference = r.Reference;
                    existingEntity.BalanceAfter = r.Balance ?? existingEntity.BalanceAfter;
                    imported++;
                    continue;
                }

                _db.Set<BankTransaction>().Add(new BankTransaction
                {
                    CompanyId = companyId,
                    BankAccountId = request.BankAccountId,
                    TransactionDate = r.Date,
                    TransactionType = txnType,
                    Amount = amount,
                    BalanceAfter = r.Balance ?? 0,
                    Description = desc,
                    Reference = r.Reference
                });
                imported++;
            }

            if (conflicts.Count > 0 && !request.ForceOverwrite)
            {
                await dbTransaction.RollbackAsync();
                return new ImportBankStatementResponse(imported, skipped, conflicts.Count, conflicts, warnings);
            }

            // อัปเดต snapshot ยอดจริงทุกครั้งที่ไฟล์ผ่าน (แม้รายการซ้ำถูกข้ามหมด —
            // การอัพ statement ช่วงทับซ้อนก็ยังยืนยันยอด ณ วันล่าสุดได้)
            // ไม่มีแถวไหนมียอดเลย → คง snapshot เดิมไว้ ดีกว่าเขียนทับด้วยศูนย์ปลอม
            if (latestRow != null)
            {
                account.StatementBalance = latestRow.Balance;
                account.StatementBalanceDate = latestRow.Date;
                account.StatementImportedAt = DateTime.UtcNow;
                account.CurrentBalance = latestRow.Balance!.Value;
            }
            else
                warnings.Add("⚠ ไฟล์ไม่มีคอลัมน์ยอดคงเหลือที่อ่านได้ — ยอดตามธนาคารบนการ์ดบัญชีไม่ถูกอัปเดต");

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

            return new ImportBankStatementResponse(imported, skipped, 0, null, warnings);
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
        a.IsActive,
        a.StatementBalance, a.StatementBalanceDate, a.StatementImportedAt);

    /// <summary>
    /// แปลงแถวเป็น DTO. <paramref name="personName"/> = ชื่อผู้ใช้ที่ผู้เรียก
    /// **ค้นมาให้แล้ว** (null = ค้นไม่เจอ/ไม่ได้ค้น → ป้ายเป็น "👤 ผู้ใช้" เฉย ๆ
    /// ห้ามแต่งชื่อ). ป้ายคำนวณที่เซิร์ฟเวอร์ตัวเดียว — JS แสดงอย่างเดียว
    /// </summary>
    private static BankTransactionResponse MapTransactionToResponse(
        BankTransaction t, string? personName = null)
    {
        var who = Accounting.Helpers.BankMatchAttribution.Describe(t.ReconciledBy, personName);
        return new BankTransactionResponse(
            t.Id, t.BankAccountId, t.TransactionDate, t.TransactionType,
            t.Amount, t.BalanceAfter, t.Description, t.Reference, t.Payee,
            t.ReconciliationStatus, t.MatchedPaymentId,
            MatchedJournalEntryId: t.MatchedJournalEntryId,
            SuggestedDocumentId: t.SuggestedDocumentId,
            ReconciledBy: t.ReconciledBy,
            ReconciledAt: t.ReconciledAt,
            ReconciledByKind: who.Kind.ToString(),
            ReconciledByLabel: who.Kind == Accounting.Helpers.BankMatchActorKind.Unknown
                ? null : who.Label,
            MatchRuleCode: t.MatchRuleCode,
            MatchReason: t.MatchReason);
    }

    /// <summary>
    /// ค้นชื่อผู้ใช้ของทุกแถวที่ `ReconciledBy` เป็น GUID ในคิวรีเดียว
    /// (กัน N+1 และกัน "หน้าเว็บอ่านฟิลด์ที่เซิร์ฟเวอร์ไม่เคยส่ง")
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveReconcilerNamesAsync(
        Guid companyId, IEnumerable<BankTransaction> rows)
    {
        var ids = new HashSet<Guid>();
        foreach (var t in rows)
        {
            if (string.IsNullOrWhiteSpace(t.ReconciledBy)) continue;
            var core = t.ReconciledBy!.Trim();
            if (core.EndsWith(Accounting.Helpers.BankMatchAttribution.AiAssistedSuffix,
                    StringComparison.OrdinalIgnoreCase))
                core = core[..^Accounting.Helpers.BankMatchAttribution.AiAssistedSuffix.Length];
            if (Guid.TryParse(core, out var uid) && uid != Guid.Empty) ids.Add(uid);
        }
        return await CompanyMemberNamesAsync(companyId, ids);
    }

    /// <summary>ชื่อผู้ใช้ของ id ที่ส่งมา — **เฉพาะคนที่เป็นสมาชิกของบริษัทนี้**
    ///
    /// <para><c>User</c> ไม่มีช่อง <c>CompanyId</c> (คนหนึ่งอยู่ได้หลายบริษัท) ⇒
    /// ความเป็นสมาชิกอยู่ที่ <c>CompanyUsers</c> ซึ่งเป็นตารางเดียวกับที่
    /// <c>TenantGuard</c> ใช้ตัดสิน ⇒ การกรองด้วยตารางนี้คือ tenant isolation
    /// ตัวจริง (กฎ M) · id ที่ไม่ได้เป็นสมาชิกจะ**หายไปจากผลลัพธ์** ⇒ ฝั่งเรียก
    /// แสดง "ไม่ทราบผู้ทำรายการ" แทนการเผยชื่อคนของบริษัทอื่น</para>
    ///
    /// <para>เป็นจุดเดียวในไฟล์นี้ที่ตอบคำถาม "ผู้ใช้คนนี้อยู่บริษัทนี้ไหม" —
    /// ผู้เรียกทั้งสองเส้น (จับคู่ 1:1 · ตารางรายการ) เดินตัวเดียวกัน</para></summary>
    private async Task<Dictionary<string, string>> CompanyMemberNamesAsync(
        Guid companyId, IEnumerable<Guid> userIds)
    {
        var list = userIds.Distinct().Where(id => id != Guid.Empty).ToList();
        if (list.Count == 0) return new Dictionary<string, string>();
        var users = await _db.Users.AsNoTracking()
            .Where(u => list.Contains(u.Id)
                && _db.CompanyUsers.Any(cu => cu.UserId == u.Id && cu.CompanyId == companyId))
            .Select(u => new { u.Id, u.FullName })
            .ToListAsync();
        return users.ToDictionary(u => u.Id.ToString("D"), u => u.FullName ?? "");
    }

    /// <summary>หาชื่อที่ตรงกับค่า `ReconciledBy` ของแถวนี้จากตารางที่ค้นมาแล้ว</summary>
    private static string? LookupReconcilerName(
        Dictionary<string, string> names, BankTransaction t)
    {
        if (names.Count == 0 || string.IsNullOrWhiteSpace(t.ReconciledBy)) return null;
        var core = t.ReconciledBy!.Trim();
        if (core.EndsWith(Accounting.Helpers.BankMatchAttribution.AiAssistedSuffix,
                StringComparison.OrdinalIgnoreCase))
            core = core[..^Accounting.Helpers.BankMatchAttribution.AiAssistedSuffix.Length];
        return names.TryGetValue(core, out var n) && !string.IsNullOrWhiteSpace(n) ? n : null;
    }

    /// <summary>Block reconciliation in a Closed or Locked fiscal period.
    /// Throws InvalidOperationException with a clear Thai message naming the
    /// period and action so the UI can show it directly. No-op when the date
    /// falls outside any defined period (some companies haven't seeded their
    /// fiscal year yet — we don't want to block those).</summary>
    private async Task EnsureFiscalPeriodOpenAsync(Guid companyId, DateTime txnDate, string action)
    {
        var fp = await _db.FiscalPeriods.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted
                && f.StartDate <= txnDate && f.EndDate >= txnDate)
            .Select(f => new { f.Name, f.Status })
            .FirstOrDefaultAsync();
        if (fp == null) return;          // no period defined → allow
        if (fp.Status == FiscalPeriodStatus.Open) return;
        throw new InvalidOperationException(
            $"งวดบัญชี '{fp.Name}' ถูก{(fp.Status == FiscalPeriodStatus.Locked ? "ล็อก" : "ปิด")}แล้ว — ห้ามทำ {action} ในงวดนี้");
    }
}

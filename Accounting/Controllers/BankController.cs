using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Data;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class BankController : ControllerBase
{
    private readonly IBankService _bankService;
    private readonly IAccountingService _accounting;
    private readonly AccountingDbContext _db;

    public BankController(IBankService bankService, IAccountingService accounting, AccountingDbContext db)
    {
        _bankService = bankService;
        _accounting = accounting;
        _db = db;
    }

    [HttpGet("accounts")]
    public async Task<ActionResult<ApiResponse<List<BankAccountResponse>>>> GetAccounts(Guid companyId)
    {
        var result = await _bankService.GetBankAccountsAsync(companyId);
        return Ok(new ApiResponse<List<BankAccountResponse>>(true, result));
    }

    [HttpPost("accounts")]
    public async Task<ActionResult<ApiResponse<BankAccountResponse>>> CreateAccount(Guid companyId, [FromBody] CreateBankAccountRequest request)
    {
        var result = await _bankService.CreateBankAccountAsync(companyId, request);
        return StatusCode(201, new ApiResponse<BankAccountResponse>(true, result, "สร้างบัญชีธนาคารสำเร็จ"));
    }

    [HttpPut("accounts/{accountId:guid}")]
    public async Task<ActionResult<ApiResponse<BankAccountResponse>>> UpdateAccount(Guid companyId, Guid accountId, [FromBody] UpdateBankAccountRequest request)
    {
        var result = await _bankService.UpdateBankAccountAsync(companyId, accountId, request);
        return Ok(new ApiResponse<BankAccountResponse>(true, result));
    }

    [HttpGet("accounts/{accountId:guid}/transactions")]
    public async Task<ActionResult<ApiResponse<PagedResponse<BankTransactionResponse>>>> GetTransactions(
        Guid companyId, Guid accountId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _bankService.GetTransactionsAsync(companyId, accountId, new PagedRequest(page, pageSize));
        return Ok(new ApiResponse<PagedResponse<BankTransactionResponse>>(true, result));
    }

    [HttpPost("transactions")]
    public async Task<ActionResult<ApiResponse<BankTransactionResponse>>> CreateTransaction(Guid companyId, [FromBody] CreateBankTransactionRequest request)
    {
        var result = await _bankService.CreateTransactionAsync(companyId, request);
        return StatusCode(201, new ApiResponse<BankTransactionResponse>(true, result));
    }

    [HttpPost("reconcile")]
    public async Task<ActionResult<ApiResponse<BankTransactionResponse>>> Reconcile(Guid companyId, [FromBody] ReconcileRequest request)
    {
        var result = await _bankService.ReconcileAsync(companyId, request);
        return Ok(new ApiResponse<BankTransactionResponse>(true, result, "กระทบยอดสำเร็จ"));
    }

    [HttpGet("accounts/{accountId:guid}/unreconciled")]
    public async Task<ActionResult<ApiResponse<List<BankTransactionResponse>>>> GetUnreconciled(Guid companyId, Guid accountId)
    {
        var result = await _bankService.GetUnreconciledAsync(companyId, accountId);
        return Ok(new ApiResponse<List<BankTransactionResponse>>(true, result));
    }

    [HttpPost("accounts/{accountId:guid}/auto-match")]
    public async Task<ActionResult<ApiResponse<List<BankTransactionResponse>>>> AutoMatch(Guid companyId, Guid accountId)
    {
        var result = await _bankService.AutoMatchAsync(companyId, accountId);
        return Ok(new ApiResponse<List<BankTransactionResponse>>(true, result, $"จับคู่อัตโนมัติได้ {result.Count} รายการ"));
    }

    [HttpPost("import-statement")]
    // Default Kestrel body limit is 30MB. A multi-month .xlsx that's base64-
    // encoded inside the JSON body inflates ~33% so the practical CSV/Excel
    // file ceiling is ~22MB without this override. Bumping to 100MB covers
    // even year-long exports comfortably.
    [RequestSizeLimit(100_000_000)]
    public async Task<ActionResult<ApiResponse<ImportBankStatementResponse>>> ImportStatement(
        Guid companyId, [FromBody] ImportBankStatementRequest request)
    {
        var result = await _bankService.ImportBankStatementAsync(companyId, request);
        if (result.Conflicts > 0)
            return Ok(new ApiResponse<ImportBankStatementResponse>(true, result,
                $"พบรายการซ้ำ {result.Conflicts} รายการ กรุณาเลือกใช้ข้อมูลเก่าหรือใหม่"));
        var msg = $"นำเข้า {result.Imported} รายการสำเร็จ";
        if (result.Skipped > 0) msg += $" (ข้าม {result.Skipped} รายการซ้ำ)";
        return Ok(new ApiResponse<ImportBankStatementResponse>(true, result, msg));
    }

    [HttpPost("accounts/{accountId:guid}/ai-match")]
    public async Task<ActionResult<ApiResponse<AiReconciliationResult>>> AiSmartMatch(
        Guid companyId, Guid accountId, [FromBody] AiReconciliationRequest? request)
    {
        var result = await _bankService.AiSmartMatchAsync(companyId, accountId, request ?? new());
        return Ok(new ApiResponse<AiReconciliationResult>(true, result,
            $"AI วิเคราะห์เสร็จ: พบ {result.SuggestionsFound} คู่ที่แนะนำ"));
    }

    /// <summary>One-shot bulk reconciliation — bundles a whole month
    /// of unmatched bank txns + every open AR/AP/JE/Payment + company
    /// context into a single AI call. Returns a complete match plan
    /// (1:1, M:1, 1:M), unmatched list with reasons, and "missing
    /// data" hints for memos that cite non-existent doc numbers.
    ///
    /// One button → one decision per line — user accepts, rejects, or
    /// edits each proposed match in the UI; bulk-confirmed matches are
    /// then applied via /batch-reconcile so the existing atomic write
    /// path stays the single source of truth.</summary>
    [HttpPost("accounts/{accountId:guid}/bulk-ai-match")]
    public async Task<ActionResult<ApiResponse<Services.Implementations.Bank.BulkAiMatchPlan>>> BulkAiMatch(
        Guid companyId, Guid accountId,
        [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate,
        [FromServices] Services.Implementations.Bank.IBulkBankAiMatchService bulk,
        CancellationToken ct)
    {
        if (toDate < fromDate)
            return BadRequest(new ApiResponse<object>(false, null, "toDate ต้องอยู่หลัง fromDate"));
        if ((toDate - fromDate).TotalDays > 92)
            return BadRequest(new ApiResponse<object>(false, null, "ช่วงเวลาเกิน 92 วัน — กรุณาแบ่งเป็นช่วงสั้นกว่า"));

        var plan = await bulk.ProposeAsync(companyId, accountId, fromDate, toDate, ct);
        var msg = plan.Status switch
        {
            "Success" => $"AI เสนอ match {plan.Matches.Count} รายการ, ไม่ตรง {plan.Unmatched.Count}, ขาดข้อมูล {plan.MissingData.Count}",
            "Truncated" => $"ข้อมูลเยอะ — ส่งให้ AI แค่บางส่วน. AI เสนอ match {plan.Matches.Count} รายการ",
            "AiUnavailable" => "AI ใช้งานไม่ได้ — กรุณา match ด้วยมือ",
            _ => plan.Warnings.FirstOrDefault() ?? "ไม่มีข้อมูลให้ match",
        };
        return Ok(new ApiResponse<Services.Implementations.Bank.BulkAiMatchPlan>(true, plan, msg));
    }

    public sealed record BulkBankMatchOutcomesRequest(
        List<Services.Implementations.Bank.BulkMatchOutcome> Outcomes);

    /// <summary>Hook for the UI to record per-match feedback after the
    /// user clicks "ยืนยัน match ที่เลือก" in the bulk modal. Each
    /// outcome is the child feedback row id + the chosen candidate +
    /// whether the user took AI's pick as-is. Feeds the
    /// BankMatchDistillationModel training corpus so accuracy keeps
    /// improving with every bulk session.</summary>
    [HttpPost("accounts/{accountId:guid}/bulk-ai-match/outcomes")]
    public async Task<ActionResult<ApiResponse<object>>> RecordBulkMatchOutcomes(
        Guid companyId, Guid accountId,
        [FromBody] BulkBankMatchOutcomesRequest req,
        [FromServices] Services.Implementations.Bank.IBulkBankAiMatchService bulk,
        CancellationToken ct)
    {
        await bulk.RecordMatchOutcomesAsync(companyId, req.Outcomes ?? new(), ct);
        return Ok(new ApiResponse<object>(true, new { recorded = req.Outcomes?.Count ?? 0 }));
    }

    [HttpGet("accounts/{accountId:guid}/reconciliation-summary")]
    public async Task<ActionResult<ApiResponse<ReconciliationSummaryDto>>> GetReconciliationSummary(
        Guid companyId, Guid accountId)
    {
        var result = await _bankService.GetReconciliationSummaryAsync(companyId, accountId);
        return Ok(new ApiResponse<ReconciliationSummaryDto>(true, result));
    }

    [HttpPost("batch-reconcile")]
    public async Task<ActionResult<ApiResponse<List<BankTransactionResponse>>>> BatchReconcile(
        Guid companyId, [FromBody] BatchReconcileRequest request)
    {
        var result = await _bankService.BatchReconcileAsync(companyId, request);
        return Ok(new ApiResponse<List<BankTransactionResponse>>(true, result,
            $"จับคู่สำเร็จ {result.Count} รายการ"));
    }

    [HttpPost("unmatch")]
    public async Task<ActionResult<ApiResponse<BankTransactionResponse>>> UnmatchTransaction(
        Guid companyId, [FromBody] UnmatchRequest request)
    {
        var result = await _bankService.UnmatchTransactionAsync(companyId, request);
        return Ok(new ApiResponse<BankTransactionResponse>(true, result, "ยกเลิกการจับคู่สำเร็จ"));
    }

    /// <summary>
    /// List ranked match candidates (Payments + JournalEntries) for a bank transaction.
    /// Used by the manual reconciliation picker so the user can choose from a list
    /// instead of typing UUIDs.
    /// </summary>
    [HttpGet("transactions/{transactionId:guid}/match-candidates")]
    public async Task<ActionResult<ApiResponse<MatchCandidatesResponse>>> GetMatchCandidates(
        Guid companyId, Guid transactionId)
    {
        var result = await _bankService.GetMatchCandidatesAsync(companyId, transactionId);
        return Ok(new ApiResponse<MatchCandidatesResponse>(true, result));
    }

    /// <summary>
    /// AI auto-suggest a single or many-to-one match. Picks the subset of payments or
    /// journal entries whose amounts sum exactly to the bank transaction's amount.
    /// </summary>
    [HttpGet("transactions/{transactionId:guid}/ai-suggest-match")]
    public async Task<ActionResult<ApiResponse<AiMatchSuggestionResponse>>> AiSuggestMatch(
        Guid companyId, Guid transactionId)
    {
        var result = await _bankService.SuggestMatchAsync(companyId, transactionId);
        return Ok(new ApiResponse<AiMatchSuggestionResponse>(true, result, result.Message));
    }

    public sealed record CreateJeFromTxnRequest(Guid CounterpartAccountId, string? Description);

    /// <summary>
    /// One-click for an unmatched bank line that has NO document/JE behind it
    /// yet (e.g. a customer deposit the shop never raised a receipt for). Posts
    /// a balanced journal entry against the bank's GL account + the chosen
    /// counterpart account, then reconciles the transaction to it — so the
    /// operator clears 'orphan' bank lines without leaving the page.
    ///   Deposit (เงินเข้า)   → Dr Bank / Cr counterpart (usually a revenue 4xxx)
    ///   Withdrawal (เงินออก) → Dr counterpart (usually an expense 5xxx) / Cr Bank
    /// </summary>
    [HttpPost("transactions/{transactionId:guid}/create-je")]
    public async Task<ActionResult<ApiResponse<object>>> CreateJeFromTransaction(
        Guid companyId, Guid transactionId, [FromBody] CreateJeFromTxnRequest req)
    {
        var txn = await _db.Set<BankTransaction>()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId && !t.IsDeleted);
        if (txn == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการธนาคาร"));
        if (txn.ReconciliationStatus == ReconciliationStatus.Matched)
            return BadRequest(new ApiResponse<object>(false, null, "รายการนี้กระทบยอดแล้ว"));

        var bank = await _db.Set<BankAccount>()
            .FirstOrDefaultAsync(b => b.Id == txn.BankAccountId && b.CompanyId == companyId);
        if (bank?.LinkedAccountId == null)
            return BadRequest(new ApiResponse<object>(false, null,
                "บัญชีธนาคารยังไม่ได้ผูกกับผังบัญชี (GL) — ตั้งค่าบัญชีธนาคารก่อน"));

        var counter = await _db.ChartOfAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == req.CounterpartAccountId && a.CompanyId == companyId && a.IsActive);
        if (counter == null)
            return BadRequest(new ApiResponse<object>(false, null, "ไม่พบบัญชีคู่ที่เลือก หรือถูกปิดใช้งาน"));

        var amount = Math.Abs(txn.Amount);
        if (amount <= 0)
            return BadRequest(new ApiResponse<object>(false, null, "ยอดเงินต้องมากกว่า 0"));

        var isIn = txn.TransactionType is BankTransactionType.Deposit or BankTransactionType.Interest;
        var bankLineDesc = $"{bank.AccountName} - {txn.Description ?? txn.Payee ?? "bank txn"}";
        var counterDesc = req.Description ?? txn.Description ?? txn.Payee ?? counter.AccountName;

        var lines = isIn
            ? new List<JournalLineRequest>
              {
                  new(bank.LinkedAccountId.Value, amount, 0, bankLineDesc),   // Dr Bank
                  new(counter.Id, 0, amount, counterDesc),                    // Cr Revenue/other
              }
            : new List<JournalLineRequest>
              {
                  new(counter.Id, amount, 0, counterDesc),                    // Dr Expense/other
                  new(bank.LinkedAccountId.Value, 0, amount, bankLineDesc),   // Cr Bank
              };

        var actor = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "bank-recon";
        var je = await _accounting.CreateJournalEntryAsync(companyId, new CreateJournalEntryRequest(
            EntryDate: txn.TransactionDate.Date,
            Description: $"กระทบยอดธนาคาร: {bankLineDesc}",
            Reference: txn.Reference,
            Lines: lines,
            JournalType: isIn ? JournalType.CashReceipts : JournalType.CashPayments), actor);

        // CreateJournalEntryAsync already persists the entry as Posted, so it
        // is immediately a real GL entry — just reconcile the txn to it.
        await _bankService.ReconcileAsync(companyId, new ReconcileRequest(
            BankTransactionId: transactionId,
            MatchedPaymentId: null,
            MatchedJournalEntryId: je.Id));

        return Ok(new ApiResponse<object>(true, new { journalEntryId = je.Id, journalEntryNumber = je.EntryNumber },
            $"สร้างรายการบัญชี {je.EntryNumber} และกระทบยอดสำเร็จ"));
    }

    public sealed record MatchedLine(Guid TxnId, DateTime Date, decimal Amount, string Type,
        string? Description, string? Reference, string CounterpartKind, string CounterpartLabel,
        decimal CounterpartAmount);
    public sealed record GlPosting(string JeNumber, DateTime Date, string? Description,
        decimal Net, bool LinkedToBankTxn);
    public sealed record ReconDetailResponse(decimal BankBalance, decimal BookBalance, decimal Difference,
        List<MatchedLine> Matched, List<GlPosting> UnlinkedGlPostings, decimal UnlinkedGlTotal, string Explanation);

    /// <summary>
    /// Answers "everything is matched, so why is there still a difference?".
    /// Returns (a) every matched bank line with WHAT it was reconciled to, and
    /// (b) the GL postings to this bank's linked account that are NOT backed by
    /// a matched bank transaction — those unlinked postings (opening balance,
    /// manual JEs, direct document postings) are exactly what makes the GL
    /// balance differ from the bank statement.
    /// </summary>
    [HttpGet("accounts/{accountId:guid}/reconciliation-detail")]
    public async Task<ActionResult<ApiResponse<ReconDetailResponse>>> ReconciliationDetail(
        Guid companyId, Guid accountId)
    {
        var account = await _db.Set<BankAccount>().AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId);
        if (account == null) return NotFound(new ApiResponse<ReconDetailResponse>(false, null, "ไม่พบบัญชีธนาคาร"));

        var txns = await _db.Set<BankTransaction>().AsNoTracking()
            .Where(t => t.BankAccountId == accountId && t.CompanyId == companyId && !t.IsDeleted
                && t.ReconciliationStatus == ReconciliationStatus.Matched)
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync();

        // Resolve counterpart labels in bulk.
        var payIds = txns.Where(t => t.MatchedPaymentId.HasValue).Select(t => t.MatchedPaymentId!.Value).Distinct().ToList();
        var jeIds = txns.Where(t => t.MatchedJournalEntryId.HasValue).Select(t => t.MatchedJournalEntryId!.Value).Distinct().ToList();
        var payments = await _db.Set<Payment>().AsNoTracking().Include(p => p.Document)
            .Where(p => payIds.Contains(p.Id))
            .Select(p => new { p.Id, p.PaymentNumber, p.Amount, p.PaymentDate, DocNo = p.Document.DocumentNumber })
            .ToListAsync();
        var jes = await _db.JournalEntries.AsNoTracking()
            .Where(j => jeIds.Contains(j.Id))
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.TotalDebit, j.Description })
            .ToListAsync();

        var typeMap = new Dictionary<BankTransactionType, string>
        {
            [BankTransactionType.Deposit] = "ฝาก", [BankTransactionType.Withdrawal] = "ถอน",
            [BankTransactionType.Transfer] = "โอน", [BankTransactionType.Fee] = "ค่าธรรมเนียม",
            [BankTransactionType.Interest] = "ดอกเบี้ย",
        };

        var matched = new List<MatchedLine>();
        foreach (var t in txns)
        {
            string kind = "—", label = "(จับคู่แล้ว แต่ไม่พบรายละเอียด)";
            decimal cpAmount = 0;
            if (t.MatchedPaymentId.HasValue)
            {
                var p = payments.FirstOrDefault(x => x.Id == t.MatchedPaymentId.Value);
                if (p != null) { kind = "Payment"; label = $"{p.DocNo ?? p.PaymentNumber}"; cpAmount = p.Amount; }
            }
            else if (t.MatchedJournalEntryId.HasValue)
            {
                var j = jes.FirstOrDefault(x => x.Id == t.MatchedJournalEntryId.Value);
                if (j != null) { kind = "JournalEntry"; label = $"{j.EntryNumber}" + (j.Description != null ? $" — {j.Description}" : ""); cpAmount = j.TotalDebit; }
            }
            else if (t.ReconciliationGroupId.HasValue)
            {
                kind = "Group"; label = "กลุ่มกระทบยอด (หลายรายการ)";
            }
            matched.Add(new MatchedLine(t.Id, t.TransactionDate, t.Amount,
                typeMap.GetValueOrDefault(t.TransactionType, t.TransactionType.ToString()),
                t.Description ?? t.Payee, t.Reference, kind, label, cpAmount));
        }

        // GL postings to the linked account, flagged by whether a matched bank
        // txn points at that JE. Unlinked ones explain the bank-vs-GL gap.
        decimal bookBalance = 0;
        var unlinked = new List<GlPosting>();
        decimal unlinkedTotal = 0;
        if (account.LinkedAccountId.HasValue)
        {
            var matchedJeIds = txns.Where(t => t.MatchedJournalEntryId.HasValue)
                .Select(t => t.MatchedJournalEntryId!.Value).ToHashSet();

            // Net per JE on the linked account (two simple queries, then map).
            var glLines = await _db.JournalEntryLines.AsNoTracking()
                .Where(l => l.AccountId == account.LinkedAccountId.Value
                    && _db.JournalEntries.Any(j => j.Id == l.JournalEntryId && j.CompanyId == companyId
                        && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)))
                .Select(l => new { l.JournalEntryId, l.DebitAmount, l.CreditAmount })
                .ToListAsync();

            var netByJe = glLines.GroupBy(x => x.JournalEntryId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.DebitAmount) - g.Sum(x => x.CreditAmount));
            bookBalance = netByJe.Values.Sum();

            var unlinkedJeIds = netByJe.Keys.Where(id => !matchedJeIds.Contains(id)).ToList();
            var jeHeaders = await _db.JournalEntries.AsNoTracking()
                .Where(j => unlinkedJeIds.Contains(j.Id))
                .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.Description })
                .ToListAsync();

            foreach (var h in jeHeaders)
            {
                var net = netByJe[h.Id];
                unlinked.Add(new GlPosting(h.EntryNumber, h.EntryDate, h.Description, net, false));
                unlinkedTotal += net;
            }
            unlinked = unlinked.OrderByDescending(u => Math.Abs(u.Net)).ToList();
        }

        var diff = account.CurrentBalance - bookBalance;
        var explanation = Math.Abs(diff) < 0.01m
            ? "ยอดธนาคารตรงกับ GL — ไม่มีผลต่าง"
            : $"ผลต่าง {diff:N2} เกิดจากรายการใน GL ของบัญชีธนาคารนี้ที่ไม่ได้มาจากรายการเดินบัญชี (bank txn) — มักเป็นยอดยกมา/JE ตั้งต้น หรือเอกสารที่ลงบัญชีธนาคารตรงๆ โดยไม่มี statement line. ดูรายการด้านล่าง (รวม {unlinkedTotal:N2}).";

        return Ok(new ApiResponse<ReconDetailResponse>(true, new ReconDetailResponse(
            account.CurrentBalance, bookBalance, diff, matched, unlinked, unlinkedTotal, explanation)));
    }

    public sealed record MatchInfoItem(string Kind, string Label, DateTime? Date, decimal Amount);
    public sealed record MatchInfoResponse(Guid TxnId, decimal TxnAmount, string Status,
        List<MatchInfoItem> Counterparts, decimal CounterpartTotal, bool AmountsAgree, string? GroupNumber);

    /// <summary>Per-row "what did this bank line match to?" — resolves the
    /// single Payment/JE link, the legacy M:1 id list, or the full M:N
    /// ReconciliationGroup items, so the movement list can show the matched
    /// counterpart(s) inline.</summary>
    [HttpGet("transactions/{transactionId:guid}/match-info")]
    public async Task<ActionResult<ApiResponse<MatchInfoResponse>>> MatchInfo(
        Guid companyId, Guid transactionId)
    {
        var t = await _db.Set<BankTransaction>().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == transactionId && x.CompanyId == companyId && !x.IsDeleted);
        if (t == null) return NotFound(new ApiResponse<MatchInfoResponse>(false, null, "ไม่พบรายการ"));

        var items = new List<MatchInfoItem>();
        string? groupNumber = null;

        // Collect the ids this txn points at (single + legacy multi).
        var payIds = new List<Guid>();
        var jeIds = new List<Guid>();
        var docIds = new List<Guid>();
        if (t.MatchedPaymentId.HasValue) payIds.Add(t.MatchedPaymentId.Value);
        if (t.MatchedJournalEntryId.HasValue) jeIds.Add(t.MatchedJournalEntryId.Value);

        // M:N group expansion (authoritative when present).
        if (t.ReconciliationGroupId.HasValue)
        {
            var group = await _db.Set<ReconciliationGroup>().AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == t.ReconciliationGroupId.Value && g.CompanyId == companyId);
            groupNumber = group?.GroupNumber;
            var gItems = await _db.ReconciliationGroupItems.AsNoTracking()
                .Where(i => i.GroupId == t.ReconciliationGroupId.Value
                    && i.ItemType != ReconciliationItemType.BankTransaction)
                .Select(i => new { i.ItemType, i.ItemId })
                .ToListAsync();
            foreach (var gi in gItems)
            {
                if (gi.ItemType == ReconciliationItemType.Payment) payIds.Add(gi.ItemId);
                else if (gi.ItemType == ReconciliationItemType.JournalEntry) jeIds.Add(gi.ItemId);
                else if (gi.ItemType == ReconciliationItemType.Document) docIds.Add(gi.ItemId);
            }
        }

        // Legacy M:1 list (AI bulk match) — the full set lives in JSON, while
        // MatchedJournalEntryId only holds the FIRST id. Must include all of
        // them or the popup under-reports (showing 2,200 of a 5,800 match).
        if (!string.IsNullOrWhiteSpace(t.MatchedEntryIdsJson))
        {
            List<Guid> jsonIds = new();
            try { jsonIds = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(t.MatchedEntryIdsJson!) ?? new(); } catch { }
            if (jsonIds.Count > 0)
            {
                var jsonPayIds = await _db.Payments.AsNoTracking()
                    .Where(p => jsonIds.Contains(p.Id) && p.CompanyId == companyId)
                    .Select(p => p.Id).ToListAsync();
                payIds.AddRange(jsonPayIds);
                jeIds.AddRange(jsonIds.Except(jsonPayIds));   // non-payments treated as JEs
            }
        }

        payIds = payIds.Distinct().ToList();
        jeIds = jeIds.Distinct().ToList();
        docIds = docIds.Distinct().ToList();

        // Bank's own GL account — so a JE counterpart is measured by what it
        // actually posted to the bank (matches the match-issues / guard logic).
        var bankCoaId = await _db.Set<BankAccount>().AsNoTracking()
            .Where(a => a.Id == t.BankAccountId && a.CompanyId == companyId)
            .Select(a => a.LinkedAccountId).FirstOrDefaultAsync();

        if (payIds.Count > 0)
        {
            var pays = await _db.Set<Payment>().AsNoTracking().Include(p => p.Document)
                .Where(p => payIds.Contains(p.Id))
                .Select(p => new { p.Id, p.PaymentNumber, p.Amount, p.PaymentDate, DocNo = p.Document.DocumentNumber })
                .ToListAsync();
            items.AddRange(pays.Select(p => new MatchInfoItem("💳 Payment",
                p.DocNo ?? p.PaymentNumber, p.PaymentDate, p.Amount)));
        }
        if (jeIds.Count > 0)
        {
            var jes = await _db.JournalEntries.AsNoTracking()
                .Where(j => jeIds.Contains(j.Id))
                .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.TotalDebit, j.Description })
                .ToListAsync();
            // Net movement on the bank account per JE (fallback to total).
            var jeBankNet = new Dictionary<Guid, decimal>();
            if (bankCoaId.HasValue)
            {
                var lines = await _db.JournalEntryLines.AsNoTracking()
                    .Where(l => jeIds.Contains(l.JournalEntryId) && l.AccountId == bankCoaId.Value)
                    .Select(l => new { l.JournalEntryId, Net = l.DebitAmount - l.CreditAmount }).ToListAsync();
                jeBankNet = lines.GroupBy(l => l.JournalEntryId).ToDictionary(g => g.Key, g => Math.Abs(g.Sum(x => x.Net)));
            }
            items.AddRange(jes.Select(j => new MatchInfoItem("📒 JE",
                j.EntryNumber + (j.Description != null ? $" — {j.Description}" : ""), j.EntryDate,
                jeBankNet.TryGetValue(j.Id, out var net) ? net : j.TotalDebit)));
        }
        if (docIds.Count > 0)
        {
            var docs = await _db.Documents.AsNoTracking()
                .Where(d => docIds.Contains(d.Id))
                .Select(d => new { d.DocumentNumber, d.DocumentDate, d.TotalAmount })
                .ToListAsync();
            items.AddRange(docs.Select(d => new MatchInfoItem("📄 เอกสาร",
                d.DocumentNumber, d.DocumentDate, d.TotalAmount)));
        }

        var cpTotal = items.Sum(i => i.Amount);
        var agree = items.Count == 0 || Math.Abs(cpTotal - Math.Abs(t.Amount)) < 0.01m;
        return Ok(new ApiResponse<MatchInfoResponse>(true, new MatchInfoResponse(
            t.Id, Math.Abs(t.Amount), t.ReconciliationStatus.ToString(),
            items, cpTotal, agree, groupNumber)));
    }

    public sealed record MatchIssue(Guid TxnId, DateTime Date, string Type, string? Description,
        decimal BankAmount, decimal MatchedAmount, decimal Difference, string CounterpartLabel);
    public sealed record MatchIssuesResponse(int Count, decimal TotalDifference, List<MatchIssue> Issues);

    /// <summary>
    /// Lists already-matched bank lines whose counterpart amount(s) do NOT equal
    /// the bank amount — the legacy bad matches made before the amount guard
    /// existed. Lets the UI flag them and the operator unmatch + redo.
    /// </summary>
    [HttpGet("accounts/{accountId:guid}/match-issues")]
    public async Task<ActionResult<ApiResponse<MatchIssuesResponse>>> MatchIssues(
        Guid companyId, Guid accountId)
    {
        var bankCoaId = await _db.Set<BankAccount>().AsNoTracking()
            .Where(a => a.Id == accountId && a.CompanyId == companyId)
            .Select(a => a.LinkedAccountId).FirstOrDefaultAsync();

        var txns = await _db.Set<BankTransaction>().AsNoTracking()
            .Where(t => t.BankAccountId == accountId && t.CompanyId == companyId && !t.IsDeleted
                && t.ReconciliationStatus == ReconciliationStatus.Matched)
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync();

        // Expand every txn's counterpart id set (single + legacy JSON + group).
        var groupIds = txns.Where(t => t.ReconciliationGroupId.HasValue)
            .Select(t => t.ReconciliationGroupId!.Value).Distinct().ToList();
        // Empty groupIds → Contains yields an empty set, so this is safe.
        var groupItems = await _db.ReconciliationGroupItems.AsNoTracking()
            .Where(i => groupIds.Contains(i.GroupId) && i.ItemType != ReconciliationItemType.BankTransaction)
            .Select(i => new { i.GroupId, i.ItemId }).ToListAsync();

        var idsByTxn = new Dictionary<Guid, List<Guid>>();
        foreach (var t in txns)
        {
            var ids = new List<Guid>();
            if (t.MatchedPaymentId.HasValue) ids.Add(t.MatchedPaymentId.Value);
            if (t.MatchedJournalEntryId.HasValue) ids.Add(t.MatchedJournalEntryId.Value);
            if (!string.IsNullOrWhiteSpace(t.MatchedEntryIdsJson))
                try { ids.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(t.MatchedEntryIdsJson!) ?? new()); } catch { }
            if (t.ReconciliationGroupId.HasValue)
                ids.AddRange(groupItems.Where(g => g.GroupId == t.ReconciliationGroupId.Value).Select(g => g.ItemId));
            idsByTxn[t.Id] = ids.Where(i => i != Guid.Empty).Distinct().ToList();
        }

        var allIds = idsByTxn.Values.SelectMany(x => x).Distinct().ToList();
        var payAmts = await _db.Payments.AsNoTracking()
            .Where(p => allIds.Contains(p.Id) && p.CompanyId == companyId)
            .Select(p => new { p.Id, p.Amount }).ToListAsync();
        var payMap = payAmts.ToDictionary(p => p.Id, p => p.Amount);
        var payIdSet = payMap.Keys.ToHashSet();

        // JE net on the bank account (per JE), with total fallback for JEs that
        // don't post to it.
        var jeIds = allIds.Where(i => !payIdSet.Contains(i)).ToList();
        var jeNet = new Dictionary<Guid, decimal>();
        if (jeIds.Count > 0 && bankCoaId.HasValue)
        {
            var lines = await _db.JournalEntryLines.AsNoTracking()
                .Where(l => jeIds.Contains(l.JournalEntryId) && l.AccountId == bankCoaId.Value)
                .Select(l => new { l.JournalEntryId, Net = l.DebitAmount - l.CreditAmount }).ToListAsync();
            jeNet = lines.GroupBy(l => l.JournalEntryId).ToDictionary(g => g.Key, g => Math.Abs(g.Sum(x => x.Net)));
        }
        var jeNoBankLine = jeIds.Where(id => !jeNet.ContainsKey(id)).ToList();
        if (jeNoBankLine.Count > 0)
        {
            var totals = await _db.JournalEntries.AsNoTracking()
                .Where(j => jeNoBankLine.Contains(j.Id))
                .Select(j => new { j.Id, j.TotalDebit }).ToListAsync();
            foreach (var x in totals) jeNet[x.Id] = x.TotalDebit;
        }

        var typeMap = new Dictionary<BankTransactionType, string>
        {
            [BankTransactionType.Deposit] = "ฝาก", [BankTransactionType.Withdrawal] = "ถอน",
            [BankTransactionType.Transfer] = "โอน", [BankTransactionType.Fee] = "ค่าธรรมเนียม",
            [BankTransactionType.Interest] = "ดอกเบี้ย",
        };

        var issues = new List<MatchIssue>();
        foreach (var t in txns)
        {
            var ids = idsByTxn[t.Id];
            if (ids.Count == 0) continue;   // matched but no resolvable counterpart — skip (separate concern)
            decimal matchedAmt = ids.Sum(id => payMap.TryGetValue(id, out var pa) ? pa : jeNet.GetValueOrDefault(id, 0));
            var bankAmt = Math.Abs(t.Amount);
            var diff = Math.Abs(matchedAmt - bankAmt);
            if (diff <= 0.01m) continue;
            issues.Add(new MatchIssue(t.Id, t.TransactionDate,
                typeMap.GetValueOrDefault(t.TransactionType, t.TransactionType.ToString()),
                t.Description ?? t.Payee, bankAmt, matchedAmt, matchedAmt - bankAmt,
                ids.Count > 1 ? $"{ids.Count} รายการ" : ""));
        }

        return Ok(new ApiResponse<MatchIssuesResponse>(true, new MatchIssuesResponse(
            issues.Count, issues.Sum(i => Math.Abs(i.Difference)), issues)));
    }

    [HttpDelete("transactions/{transactionId:guid}")]
    public async Task<ActionResult<ApiResponse<int>>> DeleteTransaction(Guid companyId, Guid transactionId)
    {
        var count = await _bankService.DeleteTransactionAsync(companyId, transactionId);
        return Ok(new ApiResponse<int>(true, count, "ลบรายการสำเร็จ"));
    }

    /// <summary>
    /// Bulk delete transactions — by ID list, or by bank account + date range.
    /// Reconciled transactions are skipped unless DeleteReconciled=true.
    /// </summary>
    [HttpPost("transactions/bulk-delete")]
    public async Task<ActionResult<ApiResponse<int>>> BulkDeleteTransactions(
        Guid companyId, [FromBody] DeleteTransactionsRequest request)
    {
        var count = await _bankService.DeleteTransactionsAsync(companyId, request);
        return Ok(new ApiResponse<int>(true, count, $"ลบ {count} รายการสำเร็จ"));
    }

    // ===== M:N reconciliation + net-off (Receipt − PaymentVoucher = bank line) =====

    /// <summary>
    /// Create a reconciliation group that binds N bank transactions to M match items
    /// (Payments / Journal Entries / Documents). Each side carries a signed
    /// AllocatedAmount so net-off cases (เช่น Receipt + Payment Voucher = ยอดธนาคาร
    /// รายการเดียว) work without splitting into two reconciliations.
    /// </summary>
    [HttpPost("reconciliation-groups")]
    public async Task<ActionResult<ApiResponse<ReconciliationGroupResponse>>> CreateReconciliationGroup(
        Guid companyId, [FromBody] CreateReconciliationGroupRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bankService.CreateReconciliationGroupAsync(companyId, request, userId);
        return Ok(new ApiResponse<ReconciliationGroupResponse>(true, result,
            result.IsBalanced ? "กระทบยอดสำเร็จ" : "บันทึกกลุ่มแล้ว — ยังไม่สมดุล กรุณาตรวจสอบ"));
    }

    [HttpGet("reconciliation-groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<ReconciliationGroupResponse>>> GetReconciliationGroup(
        Guid companyId, Guid groupId)
    {
        var result = await _bankService.GetReconciliationGroupAsync(companyId, groupId);
        return Ok(new ApiResponse<ReconciliationGroupResponse>(true, result));
    }

    [HttpGet("accounts/{accountId:guid}/reconciliation-groups")]
    public async Task<ActionResult<ApiResponse<PagedResponse<ReconciliationGroupListItem>>>> ListReconciliationGroups(
        Guid companyId, Guid accountId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _bankService.GetReconciliationGroupsAsync(companyId, accountId, new PagedRequest(page, pageSize));
        return Ok(new ApiResponse<PagedResponse<ReconciliationGroupListItem>>(true, result));
    }

    [HttpDelete("reconciliation-groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> UnreconcileGroup(Guid companyId, Guid groupId)
    {
        await _bankService.UnreconcileGroupAsync(companyId, groupId);
        return Ok(new ApiResponse<string>(true, null, "ยกเลิกกลุ่มกระทบยอดสำเร็จ"));
    }

    /// <summary>
    /// Full pool of unmatched items (Payment / JournalEntry / Document) for an
    /// account — feeds the M:N workbench so the operator can see everything,
    /// not just candidates near a single bank transaction. Optional date range
    /// (defaults to trailing 6 months) and free-text filter.
    /// </summary>
    [HttpGet("accounts/{accountId:guid}/unmatched-items")]
    public async Task<ActionResult<ApiResponse<UnmatchedItemsResponse>>> GetUnmatchedItems(
        Guid companyId, Guid accountId,
        [FromQuery] string? search = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var result = await _bankService.GetUnmatchedItemsAsync(companyId, accountId, search, fromDate, toDate);
        return Ok(new ApiResponse<UnmatchedItemsResponse>(true, result));
    }

    /// <summary>
    /// AI suggestions backed by learned patterns — for an unmatched bank
    /// transaction, returns items ranked by the company's historical
    /// reconciliation history. Confidence 0..1 with a human reason string.
    /// Complements the heuristic AI suggest endpoint (amount/date/payee
    /// scoring) by leaning on what the operator has actually confirmed in
    /// the past.
    /// </summary>
    [HttpGet("transactions/{transactionId:guid}/learned-suggestions")]
    public async Task<ActionResult<ApiResponse<LearnedSuggestionsResponse>>> GetLearnedSuggestions(
        Guid companyId, Guid transactionId)
    {
        var result = await _bankService.GetLearnedSuggestionsAsync(companyId, transactionId);
        return Ok(new ApiResponse<LearnedSuggestionsResponse>(true, result));
    }

    /// <summary>
    /// Excel export of reconciliation state — Transactions / Groups / Group Items / Summary.
    /// Date range defaults to last 3 months when not specified.
    /// </summary>
    [HttpGet("accounts/{accountId:guid}/reconciliation-report.xlsx")]
    public async Task<IActionResult> ExportReconciliationReport(
        Guid companyId, Guid accountId,
        [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null)
    {
        var bytes = await _bankService.ExportReconciliationReportAsync(companyId, accountId, fromDate, toDate);
        var fileName = $"reconciliation-{accountId:N}-{DateTime.UtcNow:yyyyMMdd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
}

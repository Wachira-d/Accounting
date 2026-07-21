using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai.Prompts;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// AI augmentation for bank-feed → document matching. Used when the
/// local matcher's best score is below the auto-accept threshold so a
/// human would otherwise have to pick from a list manually. AI re-ranks
/// using semantic memo parsing + thai vendor name fuzzy match.
/// </summary>
public interface IBankAiAugmenter
{
    Task<DocumentAiSuggestion> SuggestStatementMatchAsync(
        Guid companyId, Guid bankTransactionId,
        string? memo, DateTime txnDate, decimal txnAmount, string currency,
        BankTransactionType txnType,
        string? localBestDocumentId, decimal? localConfidence,
        CancellationToken ct = default);
}

public class BankAiAugmenter : IBankAiAugmenter
{
    private const int CandidateLimit = 15;
    private readonly AccountingDbContext _db;
    private readonly IAiOrchestrator _orchestrator;
    private readonly ILogger<BankAiAugmenter> _logger;

    public BankAiAugmenter(AccountingDbContext db, IAiOrchestrator orchestrator,
        ILogger<BankAiAugmenter> logger)
    { _db = db; _orchestrator = orchestrator; _logger = logger; }

    public async Task<DocumentAiSuggestion> SuggestStatementMatchAsync(
        Guid companyId, Guid bankTransactionId,
        string? memo, DateTime txnDate, decimal txnAmount, string currency,
        BankTransactionType txnType,
        string? localBestDocumentId, decimal? localConfidence,
        CancellationToken ct = default)
    {
        try
        {
            // Open documents in the right direction (Deposit → outbound
            // AR; Withdrawal → outbound AP). Filter by amount window
            // (±15% to catch FX / bank fees) and date window (±14d).
            var amountLow = txnAmount * 0.85m;
            var amountHigh = txnAmount * 1.15m;
            var dateLow = txnDate.AddDays(-14);
            var dateHigh = txnDate.AddDays(14);

            var isDeposit = txnType == BankTransactionType.Deposit;
            var direction = isDeposit
                ? new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt }
                : new[] { DocumentType.PurchaseInvoice, DocumentType.PaymentVoucher };

            // Fetch matching window THEN sort by date-proximity in
            // memory — Math.Abs on TimeSpan.TotalDays isn't always
            // EF-translatable across providers, and the window is
            // already small enough (≤candidates*2) that client-side
            // sort is fine.
            var prefetchLimit = CandidateLimit * 3;
            var raw = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                            && direction.Contains(d.DocumentType)
                            && d.DocumentDate >= dateLow && d.DocumentDate <= dateHigh
                            && d.BalanceDue > 0
                            && d.BalanceDue >= amountLow && d.BalanceDue <= amountHigh
                            && (d.Status == DocumentStatus.Approved
                                || d.Status == DocumentStatus.PartiallyPaid))
                .Take(prefetchLimit)
                .Select(d => new
                {
                    d.Id,
                    d.DocumentNumber,
                    d.DocumentType,
                    d.DocumentDate,
                    d.BalanceDue,
                    ContactName = d.Contact != null ? d.Contact.Name : null,
                })
                .ToListAsync(ct);
            var openDocs = raw
                .OrderBy(d => Math.Abs((d.DocumentDate - txnDate).TotalDays))
                .Take(CandidateLimit)
                .ToList();

            if (openDocs.Count == 0)
            {
                return new DocumentAiSuggestion(
                    Answer: localBestDocumentId ?? "__NEW__",
                    Confidence: 0.30m,
                    Alternatives: Array.Empty<string>(),
                    Risks: new[] { "ไม่พบ document ที่เปิดอยู่ในช่วง ±14 วัน + ±15% จำนวนเงิน" },
                    ComplianceFlags: Array.Empty<string>(),
                    Reasoning: "ไม่มี candidate",
                    SuggestedActions: new[] { "ตรวจสอบว่าผู้ส่ง/ผู้รับ ออกเอกสารแล้วหรือยัง" },
                    UsedAi: false,
                    FeedbackId: null);
            }

            var candidates = openDocs.Select(d => new BankMatchPrompt.OpenDocCandidate(
                d.Id.ToString(), d.DocumentNumber, d.DocumentType.ToString(),
                d.DocumentDate, d.BalanceDue, d.ContactName)).ToList();

            var req = BankMatchPrompt.Build(
                companyId, bankTransactionId, memo, txnDate, txnAmount, currency,
                candidates, localBestDocumentId, localConfidence);

            var resp = await _orchestrator.AskAsync(req, ct);
            return new DocumentAiSuggestion(
                resp.PrimaryAnswer, resp.Confidence,
                resp.Alternatives, resp.Risks, resp.ComplianceFlags,
                resp.Reasoning, resp.SuggestedActions,
                resp.UsedAi, resp.FeedbackId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bank match augmenter failed");
            return new DocumentAiSuggestion(
                Answer: localBestDocumentId, Confidence: localConfidence,
                Alternatives: Array.Empty<string>(), Risks: Array.Empty<string>(),
                ComplianceFlags: Array.Empty<string>(),
                Reasoning: null, SuggestedActions: Array.Empty<string>(),
                UsedAi: false, FeedbackId: null);
        }
    }
}

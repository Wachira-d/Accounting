using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai.Prompts;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// AI augmentation for document-workflow decisions:
///   • SuggestApprovalWarningFixAsync — turn a soft-warn into a
///     specific action recommendation.
///   • ClassifyCreditNoteReasonAsync — pick Return/Discount/Adjustment/
///     Writeoff per ประมวลรัษฎากร §82/10.
///   • InferWhtCategoryAsync — pick the right WHT revenue code + rate.
///   • SuggestPaymentVoucherAccountingAsync — the user's explicit
///     example: เลือกผังบัญชีตอนสร้างใบสำคัญจ่ายจากใบกำกับภาษี.
///
/// Same safety contract as OcrAiAugmenter — every method returns a
/// usable result even when AI is down. Outer try/catch in each method;
/// orchestrator's safety net is a second line of defence.
/// </summary>
public interface IDocumentAiAugmenter
{
    Task<DocumentAiSuggestion> SuggestApprovalWarningFixAsync(
        Guid companyId, Guid documentId, string warningText,
        object documentSnapshot, object? vendorHistory,
        CancellationToken ct = default);

    Task<DocumentAiSuggestion> ClassifyCreditNoteReasonAsync(
        Guid companyId, Guid creditNoteId,
        object creditNoteSnapshot, object? originalInvoice,
        string? localGuess, decimal? localConfidence,
        CancellationToken ct = default);

    Task<DocumentAiSuggestion> InferWhtCategoryAsync(
        Guid companyId, Guid? documentId,
        string? vendorName, string? vendorTaxId, string? vendorType,
        string lineDescription, decimal amount,
        string? localGuess, decimal? localConfidence,
        CancellationToken ct = default);

    /// <summary>
    /// Pick GL accounts for a Payment Voucher being created from a Tax
    /// Invoice. AI sees the invoice + vendor history + the tenant's
    /// chart of accounts + Thai WHT/VAT booking rules, and proposes
    /// the debit-side accounts for each line. This is the call site
    /// the user specifically asked for.
    /// </summary>
    Task<DocumentAiSuggestion> SuggestPaymentVoucherAccountingAsync(
        Guid companyId, Guid sourceInvoiceId,
        string? vendorName, string? vendorTaxId, string? vendorIndustry,
        string lineDescription, decimal amount, string currency,
        string? localBestAccountCode, decimal? localConfidence,
        CancellationToken ct = default);
}

public sealed record DocumentAiSuggestion(
    string? Answer,
    decimal? Confidence,
    IReadOnlyList<string> Alternatives,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> ComplianceFlags,
    string? Reasoning,
    IReadOnlyList<string> SuggestedActions,
    bool UsedAi,
    Guid? FeedbackId);

public class DocumentAiAugmenter : IDocumentAiAugmenter
{
    private readonly AccountingDbContext _db;
    private readonly IAiOrchestrator _orchestrator;
    private readonly ILogger<DocumentAiAugmenter> _logger;

    public DocumentAiAugmenter(AccountingDbContext db, IAiOrchestrator orchestrator,
        ILogger<DocumentAiAugmenter> logger)
    { _db = db; _orchestrator = orchestrator; _logger = logger; }

    public async Task<DocumentAiSuggestion> SuggestApprovalWarningFixAsync(
        Guid companyId, Guid documentId, string warningText,
        object documentSnapshot, object? vendorHistory,
        CancellationToken ct = default)
    {
        try
        {
            var req = ApprovalWarningFixPrompt.Build(
                companyId, documentId, warningText,
                documentSnapshot, vendorHistory,
                localFix: "Acknowledge", localConfidence: 0.50m);
            var resp = await _orchestrator.AskAsync(req, ct);
            return Convert(resp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Approval warning fix augmenter failed");
            return Fallback(localAnswer: "Acknowledge", confidence: 0.50m);
        }
    }

    public async Task<DocumentAiSuggestion> ClassifyCreditNoteReasonAsync(
        Guid companyId, Guid creditNoteId,
        object creditNoteSnapshot, object? originalInvoice,
        string? localGuess, decimal? localConfidence,
        CancellationToken ct = default)
    {
        try
        {
            var req = CreditNoteReasonPrompt.Build(
                companyId, creditNoteId, creditNoteSnapshot, originalInvoice,
                localGuess ?? "Adjustment", localConfidence ?? 0.40m);
            var resp = await _orchestrator.AskAsync(req, ct);
            return Convert(resp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CN reason augmenter failed");
            return Fallback(localGuess ?? "Adjustment", localConfidence ?? 0.40m);
        }
    }

    public async Task<DocumentAiSuggestion> InferWhtCategoryAsync(
        Guid companyId, Guid? documentId,
        string? vendorName, string? vendorTaxId, string? vendorType,
        string lineDescription, decimal amount,
        string? localGuess, decimal? localConfidence,
        CancellationToken ct = default)
    {
        try
        {
            var req = WhtCategoryPrompt.Build(
                companyId, documentId, vendorName, vendorTaxId, vendorType,
                lineDescription, amount, localGuess, localConfidence);
            var resp = await _orchestrator.AskAsync(req, ct);
            return Convert(resp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WHT category augmenter failed");
            return Fallback(localGuess, localConfidence);
        }
    }

    public async Task<DocumentAiSuggestion> SuggestPaymentVoucherAccountingAsync(
        Guid companyId, Guid sourceInvoiceId,
        string? vendorName, string? vendorTaxId, string? vendorIndustry,
        string lineDescription, decimal amount, string currency,
        string? localBestAccountCode, decimal? localConfidence,
        CancellationToken ct = default)
    {
        try
        {
            // Candidate accounts: active expense + asset + COGS + payable.
            var candidates = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .Take(60)
                .Select(a => new GlAccountPrompt.AccountCandidate(
                    a.AccountCode, a.AccountName, a.AccountType.ToString(), a.IsActive))
                .ToListAsync(ct);

            var vendorKey = !string.IsNullOrEmpty(vendorTaxId)
                ? vendorTaxId
                : (vendorName ?? "").Trim().ToLowerInvariant();
            var since = DateTime.UtcNow.AddMonths(-24);
            var history = !string.IsNullOrEmpty(vendorKey)
                ? await _db.OcrCategoryMappings.AsNoTracking()
                    .Where(m => m.CompanyId == companyId && !m.IsDeleted
                                && m.VendorKey == vendorKey
                                && m.LastUsedAt > since)
                    .OrderByDescending(m => m.TimesUsed)
                    .Take(8)
                    .Select(m => new GlAccountPrompt.VendorHistoricalAccount(
                        m.AccountCode, m.AccountName ?? "", m.TimesUsed, 0m))
                    .ToListAsync(ct)
                : new List<GlAccountPrompt.VendorHistoricalAccount>();

            var req = GlAccountPrompt.Build(
                companyId, vendorName, vendorTaxId, vendorIndustry,
                lineDescription, amount, currency,
                candidates, history,
                localBestAccountCode, localConfidence,
                featureKey: AiFeatureKey.PaymentVoucherAccountingSuggestion,
                localModelVersion: "ExpenseCategoryLearner-v1",
                sourceEntityType: "Document", sourceEntityId: sourceInvoiceId,
                whtRecognitionBasis: "Cash");

            var resp = await _orchestrator.AskAsync(req, ct);
            return Convert(resp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PV accounting augmenter failed");
            return Fallback(localBestAccountCode, localConfidence);
        }
    }

    private static DocumentAiSuggestion Convert(AiResponse resp) => new(
        Answer: resp.PrimaryAnswer,
        Confidence: resp.Confidence,
        Alternatives: resp.Alternatives,
        Risks: resp.Risks,
        ComplianceFlags: resp.ComplianceFlags,
        Reasoning: resp.Reasoning,
        SuggestedActions: resp.SuggestedActions,
        UsedAi: resp.UsedAi,
        FeedbackId: resp.FeedbackId);

    private static DocumentAiSuggestion Fallback(string? localAnswer, decimal? confidence) => new(
        Answer: localAnswer,
        Confidence: confidence,
        Alternatives: Array.Empty<string>(),
        Risks: Array.Empty<string>(),
        ComplianceFlags: Array.Empty<string>(),
        Reasoning: null,
        SuggestedActions: Array.Empty<string>(),
        UsedAi: false,
        FeedbackId: null);
}

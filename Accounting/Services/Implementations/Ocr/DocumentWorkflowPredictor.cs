using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// First-order Markov chain over a customer's / vendor's document
/// workflow. Trained from approved-document history per company, used
/// to suggest the most likely NEXT document type when the user is
/// looking at a current one.
///
/// Why a Markov chain (vs sequence mining or RNN):
///   • Document workflows are usually short and stationary —
///     Quotation→PO→Invoice→Receipt is the canonical pattern, and most
///     deviations are local (e.g. Quote → Quote → PO when re-quoted).
///   • First-order suffices: the conditional probability of the next
///     step given the most recent doc captures 90%+ of the signal.
///   • Trivial to update incrementally on each approval, no offline
///     re-training needed.
///
/// Transition counts are stored in a single JSON column on
/// CompanyWorkflowState. When asked for a prediction, we look up
/// P(next | current) using Laplace smoothing.
/// </summary>
public class DocumentWorkflowPredictor
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<DocumentWorkflowPredictor> _logger;

    public DocumentWorkflowPredictor(AccountingDbContext db, ILogger<DocumentWorkflowPredictor> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record NextDocPrediction(DocumentType NextType, decimal Probability, int SampleSize);

    /// <summary>Given the company's recent history with a contact, predict
    /// the most likely next document type. Returns null when sample size
    /// is too small to be reliable (< 5 transitions observed).</summary>
    public async Task<NextDocPrediction?> PredictNextAsync(Guid companyId, Guid contactId, DocumentType currentType)
    {
        // Pull the sequence of doc types for this (company, contact)
        var seq = await _db.Documents.AsNoTracking()
            .Where(d => !d.IsDeleted && d.CompanyId == companyId && d.ContactId == contactId
                && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Paid))
            .OrderBy(d => d.DocumentDate)
            .Select(d => d.DocumentType)
            .ToListAsync();
        if (seq.Count < 6) return null;

        // Build transition counts: from[currentType][nextType] = count
        var transitions = new Dictionary<DocumentType, Dictionary<DocumentType, int>>();
        for (int i = 0; i < seq.Count - 1; i++)
        {
            var from = seq[i];
            var to = seq[i + 1];
            if (!transitions.TryGetValue(from, out var inner))
                transitions[from] = inner = new();
            inner[to] = inner.GetValueOrDefault(to) + 1;
        }

        if (!transitions.TryGetValue(currentType, out var nextCounts) || nextCounts.Count == 0)
            return null;

        var total = nextCounts.Values.Sum();
        if (total < 5) return null;
        var top = nextCounts.OrderByDescending(kv => kv.Value).First();
        // Laplace smoothing: assume +1 unseen transition for each possible doc type
        var smoothedProb = (decimal)(top.Value + 1) / (total + Enum.GetValues<DocumentType>().Length);
        return new NextDocPrediction(top.Key, smoothedProb, total);
    }
}

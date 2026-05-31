using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Ai.Embedding;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// Local distillation model for FuzzyDuplicateDetection — answers
/// "is this incoming document a duplicate of one we already booked?"
/// without spending DeepSeek tokens for the routine "no, looks new" case.
///
/// Unlike vendor canon / GL account which mine AiSuggestionFeedback,
/// this model is a pure INDEX over the Documents table — every approved
/// document is a known-good record; a candidate that closely resembles
/// one of them is a duplicate. No supervision corpus is needed.
///
/// Candidate generation (cheap path):
///   • Same contact + amount within ±2% + date within ±30 days.
///   • Reduces a 50k-document company to typically <10 candidates.
///
/// Final scoring (semantic):
///   • Cosine similarity between candidate subject/line embedding and
///     the incoming document's subject embedding. Combined with amount
///     proximity and date proximity into a 0-1 confidence.
///
/// Prompt schema (FuzzyDuplicateDetection):
///   { "document": { "contactId": "...", "amount": 1234.56,
///                   "documentDate": "2025-01-15",
///                   "documentType": "Invoice", "subject": "..." } }
///
/// Returns: the candidate Document.Id with highest score, or null when
/// no candidate clears the 0.70 confidence floor (orchestrator then
/// falls through to DeepSeek or returns "no duplicate found").
/// </summary>
public class DuplicateDocumentDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.FuzzyDuplicateDetection;
    public string Version { get; private set; } = "v0";
    public bool IsReady => true;        // Always ready — queries DB on demand.

    private readonly IServiceProvider _services;
    private readonly IEmbeddingService _embedding;
    private readonly ILogger<DuplicateDocumentDistillationModel> _logger;

    /// <summary>Minimum combined score for a positive "is duplicate"
    /// answer. Below this we return null and let the caller decide
    /// (orchestrator may still want DeepSeek to look).</summary>
    private const decimal DuplicateConfidenceFloor = 0.70m;

    public DuplicateDocumentDistillationModel(IServiceProvider services,
        IEmbeddingService embedding,
        ILogger<DuplicateDocumentDistillationModel> logger)
    { _services = services; _embedding = embedding; _logger = logger; }

    public Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        // No-op — this model is a live index over Documents, not a
        // periodically-retrained store. Version bumps anyway so admin
        // sees this model as "fresh".
        Version = "v" + DateTime.UtcNow.ToString("yyyyMMddHH");
        return Task.CompletedTask;
    }

    public async Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        var input = ExtractInput(inputJson);
        if (input == null) return null;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        // Candidate generation: same contact + amount ±2% + date ±30d.
        // The ±2% band catches OCR rounding ("12,345.00" vs "12345.0")
        // without exploding the candidate set.
        var minAmt = input.Amount * 0.98m;
        var maxAmt = input.Amount * 1.02m;
        var minDate = input.DocumentDate.AddDays(-30);
        var maxDate = input.DocumentDate.AddDays(30);

        var candidates = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                        && d.ContactId == input.ContactId
                        && d.TotalAmount >= minAmt && d.TotalAmount <= maxAmt
                        && d.DocumentDate >= minDate && d.DocumentDate <= maxDate
                        && d.Status != DocumentStatus.Voided)
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.TotalAmount, d.DocumentDate,
                // No header "Subject" on the entity — derive a stable
                // semantic fingerprint from Notes (typed by the user)
                // falling back to DocumentNumber so empty-Notes docs
                // still get compared.
                Subject = (d.Notes ?? "") + " " + d.DocumentNumber,
            })
            .Take(50)        // hard cap so a pathological query doesn't dominate
            .ToListAsync(ct);

        if (candidates.Count == 0) return null;

        var queryVec = _embedding.Embed(input.Subject);
        var scored = new List<(Guid Id, string Number, decimal Score)>();
        foreach (var c in candidates)
        {
            var candVec = _embedding.Embed(c.Subject);
            var cosine = IEmbeddingService.Cosine(queryVec, candVec);
            // Combined score: 0.5 × subject-cosine + 0.3 × amount-prox +
            // 0.2 × date-prox. Amount proximity = 1 - (|Δ|/avg); date
            // proximity = max(0, 1 - daysDiff/30).
            var amountProx = (decimal)Math.Max(0,
                1 - (double)Math.Abs(c.TotalAmount - input.Amount) / Math.Max(1, (double)input.Amount));
            var daysDiff = Math.Abs((c.DocumentDate - input.DocumentDate).TotalDays);
            var dateProx = (decimal)Math.Max(0, 1 - daysDiff / 30.0);
            var score = 0.5m * (decimal)cosine + 0.3m * amountProx + 0.2m * dateProx;
            scored.Add((c.Id, c.DocumentNumber, score));
        }

        var top = scored.OrderByDescending(x => x.Score).First();
        if (top.Score < DuplicateConfidenceFloor) return null;

        // The "answer" is a JSON blob identifying the duplicate +
        // why so the caller can show the user what we matched against.
        var alts = scored.OrderByDescending(x => x.Score).Skip(1).Take(3)
            .Select(a => JsonSerializer.Serialize(new { id = a.Id, documentNumber = a.Number, score = Math.Round(a.Score, 3) }))
            .ToList();
        var primary = JsonSerializer.Serialize(new
        {
            id = top.Id,
            documentNumber = top.Number,
            score = Math.Round(top.Score, 3),
        });
        return new LocalPrediction(
            PrimaryAnswer: primary,
            Confidence: top.Score,
            Alternatives: alts,
            SupportingSamples: candidates.Count,
            ModelVersion: Version);
    }

    private static InputDoc? ExtractInput(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("document", out var d)) return null;
            if (!d.TryGetProperty("contactId", out var c)) return null;
            var contactStr = c.GetString();
            if (!Guid.TryParse(contactStr, out var contactId)) return null;
            var amount = d.TryGetProperty("amount", out var a) ? a.GetDecimal() : 0m;
            if (amount <= 0) return null;
            var dateStr = d.TryGetProperty("documentDate", out var dt) ? dt.GetString() : null;
            if (!DateTime.TryParse(dateStr, out var date)) return null;
            var subject = d.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "";
            return new InputDoc(contactId, amount, date, subject);
        }
        catch { return null; }
    }

    private sealed record InputDoc(Guid ContactId, decimal Amount, DateTime DocumentDate, string Subject);
}

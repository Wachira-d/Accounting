using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// Local model that suggests the settlement basis for a Payment Voucher —
/// Cash (จ่ายทันที) vs Credit (เครดิต) — per supplier.
///
/// Teacher–student loop: every time the operator confirms a payment type
/// the choice is recorded as AiSuggestionFeedback; LoadFromFeedbackAsync
/// mines those to learn "this supplier is usually paid {Cash|Credit}".
///
/// Cold start: until enough confirmations exist for a supplier, PredictAsync
/// falls back to a deterministic heuristic computed from the supplier's own
/// history + open payables, so the suggestion is useful from day one:
///   • supplier has unsettled PurchaseInvoices  → lean Credit (you owe them)
///   • supplier's past PVs were mostly Cash/Credit → follow the majority
///   • otherwise                                 → default Cash (most SME
///     ad-hoc expenses are pay-now)
///
/// Input JSON shape (what PredictAsync receives): { "contactId": "&lt;guid&gt;" }.
/// PrimaryAnswer is the enum name ("Cash" | "Credit").
/// </summary>
public class PaymentTypeDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.PaymentTypeSuggestion;
    public string Version { get; private set; } = "v0";
    public bool IsReady => true;   // the heuristic path makes it always usable

    private readonly IServiceProvider _services;
    private readonly ILogger<PaymentTypeDistillationModel> _logger;

    // (CompanyId, ContactId) → (cashConfirmed, creditConfirmed)
    private readonly Dictionary<(Guid, Guid), (int Cash, int Credit)> _learned = new();
    private readonly object _lock = new();

    // Minimum confirmations before the learned signal overrides the heuristic.
    private const int MinSamples = 3;

    public PaymentTypeDistillationModel(IServiceProvider services,
        ILogger<PaymentTypeDistillationModel> logger)
    { _services = services; _logger = logger; }

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        var rows = await db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CompanyId == companyId
                        && f.FeatureKey == nameof(AiFeatureKey.PaymentTypeSuggestion)
                        && f.UserChosenAt != null
                        && !f.IsDeleted)
            .Select(f => new { f.PromptJson, f.UserChosenAnswer })
            .ToListAsync(ct);

        var counts = new Dictionary<(Guid, Guid), (int Cash, int Credit)>();
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.UserChosenAnswer)) continue;
            var contactId = ExtractContactId(r.PromptJson);
            if (contactId == null) continue;
            var key = (companyId, contactId.Value);
            counts.TryGetValue(key, out var c);
            if (r.UserChosenAnswer.Equals("Credit", StringComparison.OrdinalIgnoreCase))
                c.Credit++;
            else if (r.UserChosenAnswer.Equals("Cash", StringComparison.OrdinalIgnoreCase))
                c.Cash++;
            counts[key] = c;
        }

        lock (_lock)
        {
            foreach (var kv in counts) _learned[kv.Key] = kv.Value;
            Version = "v" + DateTime.UtcNow.ToString("yyyyMMddHHmm");
        }
        _logger.LogInformation("PaymentTypeDistillationModel loaded {Count} supplier patterns for company {Co}",
            counts.Count, companyId);
    }

    public async Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        var contactId = ExtractContactId(inputJson);
        if (contactId == null) return null;

        // 1. Learned signal (confirmed history) wins once it has enough support.
        lock (_lock)
        {
            if (_learned.TryGetValue((companyId, contactId.Value), out var c))
            {
                var total = c.Cash + c.Credit;
                if (total >= MinSamples)
                {
                    var credit = c.Credit >= c.Cash;
                    var majority = credit ? c.Credit : c.Cash;
                    var conf = Math.Round((decimal)majority / total, 2);
                    return new LocalPrediction(
                        credit ? "Credit" : "Cash",
                        Math.Min(0.95m, conf),
                        new[] { credit ? "Cash" : "Credit" },
                        total, Version);
                }
            }
        }

        // 2. Cold-start heuristic from the supplier's own document history.
        return await HeuristicAsync(companyId, contactId.Value, ct);
    }

    private async Task<LocalPrediction?> HeuristicAsync(Guid companyId, Guid contactId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        // Open payables to this supplier → you carry a credit liability with
        // them, so a new voucher is more likely settling/booking on credit.
        var openPayables = await db.Documents.AsNoTracking()
            .CountAsync(d => d.CompanyId == companyId && d.ContactId == contactId && !d.IsDeleted
                && (d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense)
                && d.BalanceDue > 0
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft, ct);

        // Past Payment Vouchers for this supplier, split by type.
        var pvTypes = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId && !d.IsDeleted
                && d.DocumentType == DocumentType.PaymentVoucher
                && d.PaymentType != null)
            .GroupBy(d => d.PaymentType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var cashCount = pvTypes.FirstOrDefault(x => x.Type == Models.Enums.PaymentType.Cash)?.Count ?? 0;
        var creditCount = pvTypes.FirstOrDefault(x => x.Type == Models.Enums.PaymentType.Credit)?.Count ?? 0;

        if (openPayables > 0)
            return new LocalPrediction("Credit", 0.72m, new[] { "Cash" }, openPayables,
                $"heuristic:open-payables={openPayables}");

        if (cashCount + creditCount > 0)
        {
            var credit = creditCount > cashCount;
            var majority = credit ? creditCount : cashCount;
            var conf = Math.Round(0.55m + 0.30m * majority / (cashCount + creditCount), 2);
            return new LocalPrediction(credit ? "Credit" : "Cash", conf,
                new[] { credit ? "Cash" : "Credit" }, cashCount + creditCount,
                $"heuristic:history(cash={cashCount},credit={creditCount})");
        }

        // No signal — most ad-hoc SME expenses are paid immediately.
        return new LocalPrediction("Cash", 0.50m, new[] { "Credit" }, 0, "heuristic:default");
    }

    private static Guid? ExtractContactId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("contactId", out var el)
                && Guid.TryParse(el.GetString(), out var g))
                return g;
        }
        catch { }
        return null;
    }
}

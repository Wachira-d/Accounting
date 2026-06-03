using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai.Prompts;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// Aggregates the advanced AI flows that touch multiple data sources:
///   • OCR Tier-4 review (re-asks AI about the whole scan with full context)
///   • Stock decision (per-line CreateNew/UpdateExisting/Match)
///   • AR/AP analysis (high-risk customers/vendors, cash gap)
///   • Document conversion suggestion (what target doc to create)
///   • Comprehensive bank match (1toN, Nto1, offset, with deductions)
///
/// Same safety contract as the other augmenters — every method
/// try/catch wrapped; AI down = return local pick / empty result;
/// nothing throws to caller.
/// </summary>
public interface IAdvancedAiAugmenter
{
    Task<AdvancedAiResult> ReviewOcrAsync(
        Guid companyId, Guid scanResultId, CancellationToken ct = default);

    Task<AdvancedAiResult> SuggestStockDecisionsAsync(
        Guid companyId, Guid scanResultId, CancellationToken ct = default);

    Task<AdvancedAiResult> AnalyzeArApAsync(
        Guid companyId, CancellationToken ct = default);

    Task<AdvancedAiResult> SuggestDocumentConversionAsync(
        Guid companyId, Guid scanResultId, CancellationToken ct = default);

    Task<AdvancedAiResult> ComprehensiveBankMatchAsync(
        Guid companyId, Guid bankTransactionId, CancellationToken ct = default);
}

/// <summary>
/// Result wrapper. The Structured field carries the full JSON the AI
/// returned so per-feature parse logic stays in the caller (each
/// feature has a different "lines": [...] / "corrections": {...}
/// shape). Primary / Confidence / FeedbackId mirror the orchestrator.
/// </summary>
public sealed record AdvancedAiResult(
    string? Primary,
    decimal? Confidence,
    string? StructuredJson,    // raw response payload for per-feature decode
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> ComplianceFlags,
    string? Reasoning,
    IReadOnlyList<string> SuggestedActions,
    bool UsedAi,
    Guid? FeedbackId,
    // Schema validation outcome. Populated when the caller supplied
    // expected top-level keys to ToResult(). Empty list = JSON shape
    // matches expectations; non-empty = AI returned but skipped or
    // renamed a key — frontend should surface the warning instead of
    // silently rendering blank.
    IReadOnlyList<string> SchemaWarnings)
{
    public bool HasSchemaIssues => SchemaWarnings.Count > 0;
}

/// <summary>Compact projection used by the AR/AP aging helper.
/// Concrete record (not anonymous) so the Aging() local function
/// can take it without dynamic.</summary>
internal record ArApRow(string ContactName, DateTime DocumentDate, decimal BalanceDue);

public class AdvancedAiAugmenter : IAdvancedAiAugmenter
{
    private readonly AccountingDbContext _db;
    private readonly IAiOrchestrator _orchestrator;
    private readonly ILogger<AdvancedAiAugmenter> _logger;

    public AdvancedAiAugmenter(AccountingDbContext db, IAiOrchestrator orchestrator,
        ILogger<AdvancedAiAugmenter> logger)
    { _db = db; _orchestrator = orchestrator; _logger = logger; }

    public async Task<AdvancedAiResult> ReviewOcrAsync(Guid companyId, Guid scanResultId, CancellationToken ct = default)
    {
        try
        {
            var scan = await _db.OcrScanResults.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == scanResultId && s.CompanyId == companyId, ct);
            if (scan == null) return Fallback(null);

            var company = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId)
                .Select(c => new { c.Id, c.Name, c.TaxId, c.Address, c.Phone, c.Email })
                .FirstOrDefaultAsync(ct);
            // Company is registered + valid for the request, but stay
            // defensive — a brand-new tenant might race the AI lookup.
            if (company == null) return Fallback(null);

            // Vendor history grounding — last 5 docs from same vendor.
            object? vendorHistory = null;
            if (!string.IsNullOrEmpty(scan.ExtractedVendorTaxId))
            {
                var contact = await _db.Contacts.AsNoTracking()
                    .Where(c => c.CompanyId == companyId && c.TaxId == scan.ExtractedVendorTaxId)
                    .Select(c => c.Id).FirstOrDefaultAsync(ct);
                if (contact != Guid.Empty)
                {
                    vendorHistory = await _db.Documents.AsNoTracking()
                        .Where(d => d.CompanyId == companyId && d.ContactId == contact && !d.IsDeleted)
                        .OrderByDescending(d => d.DocumentDate)
                        .Take(5)
                        .Select(d => new
                        {
                            d.DocumentNumber, d.DocumentType, d.DocumentDate,
                            d.SubTotal, d.VatAmount, d.WithholdingTaxAmount, d.TotalAmount,
                        })
                        .ToListAsync(ct);
                }
            }

            var extracted = new
            {
                document_type = scan.DocumentType,
                document_number = scan.ExtractedDocumentNumber,
                document_date = scan.ExtractedDate?.ToString("yyyy-MM-dd"),
                vendor_name = scan.ExtractedVendorName,
                vendor_tax_id = scan.ExtractedVendorTaxId,
                buyer_name = scan.BuyerName,
                buyer_tax_id = scan.BuyerTaxId,
                sub_total = scan.ExtractedSubTotal,
                vat_amount = scan.ExtractedVatAmount,
                total_amount = scan.ExtractedTotalAmount,
                confidence = scan.Confidence,
            };

            var req = OcrReviewPrompt.Build(
                companyId, scanResultId,
                scan.RawTextContent ?? "",
                extracted,
                companyContext: company,
                vendorHistory: vendorHistory,
                localGuessTargetType: scan.TargetDocumentType ?? scan.DocumentType,
                localConfidence: scan.Confidence);
            var resp = await _orchestrator.AskAsync(req, ct);
            // OcrReviewPrompt expects corrections / target_document / vendor_canonical /
            // line_items in the response — flagging any missing key surfaces
            // a "AI ตอบแต่ขาด corrections" warning to the operator instead of
            // silently leaving the form unchanged.
            return ToResult(resp, "corrections", "target_document", "vendor_canonical", "line_items");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OCR review augmenter failed");
            return Fallback(null);
        }
    }

    public async Task<AdvancedAiResult> SuggestStockDecisionsAsync(Guid companyId, Guid scanResultId, CancellationToken ct = default)
    {
        try
        {
            var scan = await _db.OcrScanResults.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == scanResultId && s.CompanyId == companyId, ct);
            if (scan == null || string.IsNullOrEmpty(scan.ExtractedItemsJson))
                return Fallback(null);

            // Pull product catalog (cap at 200 to control token count;
            // larger catalogs would need vector-search pre-filter — not
            // built yet, future enhancement).
            var products = await _db.Products.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.IsActive)
                .OrderBy(p => p.Code)
                .Take(200)
                .Select(p => new
                {
                    id = p.Id.ToString(),
                    code = p.Code,
                    name = p.Name,
                    unit = p.Unit,
                    product_type = p.ProductType.ToString(),
                    current_stock = p.CurrentStock,
                })
                .ToListAsync(ct);

            // Parse the stored items JSON written by OcrService.
            object[] lineItems;
            try
            {
                using var doc = JsonDocument.Parse(scan.ExtractedItemsJson);
                lineItems = doc.RootElement.EnumerateArray()
                    .Select((el, idx) => (object)new
                    {
                        line_index = idx,
                        description = el.TryGetProperty("Description", out var d) ? d.GetString() : "",
                        quantity = el.TryGetProperty("Quantity", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetDecimal() : 1m,
                        unit_price = el.TryGetProperty("UnitPrice", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDecimal() : 0m,
                        amount = el.TryGetProperty("Amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : 0m,
                    })
                    .ToArray();
            }
            catch { lineItems = Array.Empty<object>(); }

            if (lineItems.Length == 0) return Fallback(null);

            var docHeader = new
            {
                document_type = scan.DocumentType,
                document_number = scan.ExtractedDocumentNumber,
                vendor_name = scan.ExtractedVendorName,
                vendor_tax_id = scan.ExtractedVendorTaxId,
                total_amount = scan.ExtractedTotalAmount,
            };

            var req = StockDecisionPrompt.Build(
                companyId, scanResultId, docHeader,
                lineItems, products.Cast<object>().ToArray());
            var resp = await _orchestrator.AskAsync(req, ct);
            // StockDecisionPrompt expects { lines: [...] } — per-line CreateNew/
            // Update/Match decisions. Without that key the UI's "apply decisions"
            // button has nothing to apply.
            return ToResult(resp, "lines");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stock decision augmenter failed");
            return Fallback(null);
        }
    }

    public async Task<AdvancedAiResult> AnalyzeArApAsync(Guid companyId, CancellationToken ct = default)
    {
        try
        {
            var today = DateTime.UtcNow.Date;
            // AR aging — outstanding receivable docs grouped by age bucket.
            var arDocs = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                            && (d.DocumentType == DocumentType.Invoice
                                || d.DocumentType == DocumentType.TaxInvoice)
                            && d.BalanceDue > 0
                            && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.PartiallyPaid))
                .Select(d => new ArApRow(
                    d.Contact != null ? d.Contact.Name : "(unknown)",
                    d.DocumentDate, d.BalanceDue))
                .ToListAsync(ct);
            var apDocs = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                            && d.DocumentType == DocumentType.PurchaseInvoice
                            && d.BalanceDue > 0
                            && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.PartiallyPaid))
                .Select(d => new ArApRow(
                    d.Contact != null ? d.Contact.Name : "(unknown)",
                    d.DocumentDate, d.BalanceDue))
                .ToListAsync(ct);

            object Aging(IEnumerable<ArApRow> rows)
            {
                var arr = rows.Select(r => new
                {
                    contact = r.ContactName,
                    days = (int)(today - r.DocumentDate.Date).TotalDays,
                    amount = r.BalanceDue,
                }).ToList();
                return new
                {
                    total_overdue = arr.Where(r => r.days > 30).Sum(r => r.amount),
                    count = arr.Count,
                    by_bucket = new
                    {
                        current_0_30 = arr.Where(r => r.days <= 30).Sum(r => r.amount),
                        late_31_60 = arr.Where(r => r.days > 30 && r.days <= 60).Sum(r => r.amount),
                        late_61_90 = arr.Where(r => r.days > 60 && r.days <= 90).Sum(r => r.amount),
                        late_90_plus = arr.Where(r => r.days > 90).Sum(r => r.amount),
                    },
                    top_5_by_amount = arr.OrderByDescending(r => r.amount).Take(5),
                };
            }

            var req = ArApAnalysisPrompt.Build(
                companyId,
                arAging: Aging(arDocs),
                apAging: Aging(apDocs),
                customerPaymentHistory: new { },
                vendorPaymentHistory: new { });
            var resp = await _orchestrator.AskAsync(req, ct);
            // ArApAnalysisPrompt expects risk_buckets + cash_gap_forecast +
            // recommendations — the UI's three-section render needs all three.
            return ToResult(resp, "risk_buckets", "cash_gap_forecast", "recommendations");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AR/AP augmenter failed");
            return Fallback(null);
        }
    }

    public async Task<AdvancedAiResult> SuggestDocumentConversionAsync(Guid companyId, Guid scanResultId, CancellationToken ct = default)
    {
        try
        {
            var scan = await _db.OcrScanResults.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == scanResultId && s.CompanyId == companyId, ct);
            if (scan == null) return Fallback(null);

            var snapshot = new
            {
                document_type = scan.DocumentType,
                document_number = scan.ExtractedDocumentNumber,
                document_date = scan.ExtractedDate?.ToString("yyyy-MM-dd"),
                vendor_name = scan.ExtractedVendorName,
                vendor_tax_id = scan.ExtractedVendorTaxId,
                total_amount = scan.ExtractedTotalAmount,
                vat_amount = scan.ExtractedVatAmount,
            };
            var req = DocumentConversionPrompt.Build(
                companyId, scanResultId,
                scannedDocType: scan.DocumentType ?? "Unknown",
                scannedSnapshot: snapshot,
                ourRole: scan.OurRole ?? "Unknown");
            var resp = await _orchestrator.AskAsync(req, ct);
            // DocumentConversionPrompt expects { targets: [...] } — viable
            // conversion targets with prefill strategies.
            return ToResult(resp, "targets");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Document conversion augmenter failed");
            return Fallback(null);
        }
    }

    public async Task<AdvancedAiResult> ComprehensiveBankMatchAsync(Guid companyId, Guid bankTransactionId, CancellationToken ct = default)
    {
        try
        {
            var txn = await _db.Set<BankTransaction>().AsNoTracking()
                .Where(t => t.Id == bankTransactionId && t.CompanyId == companyId)
                .Select(t => new
                {
                    t.Id, t.TransactionDate, t.Amount, t.TransactionType,
                    t.Description, t.Reference,
                })
                .FirstOrDefaultAsync(ct);
            if (txn == null) return Fallback(null);

            // Cast widely: ±20% amount + ±21d for the comprehensive
            // pattern matcher (lets AI handle aggregation cases).
            var amountLow = txn.Amount * 0.80m;
            var amountHigh = txn.Amount * 1.20m;
            var dateLow = txn.TransactionDate.AddDays(-21);
            var dateHigh = txn.TransactionDate.AddDays(21);

            var openDocs = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                            && d.BalanceDue > 0
                            && d.DocumentDate >= dateLow && d.DocumentDate <= dateHigh
                            && d.BalanceDue >= amountLow / 5 && d.BalanceDue <= amountHigh
                            && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.PartiallyPaid))
                .OrderBy(d => d.DocumentDate)
                .Take(40)
                .Select(d => new
                {
                    id = d.Id.ToString(),
                    number = d.DocumentNumber,
                    type = d.DocumentType.ToString(),
                    date = d.DocumentDate.ToString("yyyy-MM-dd"),
                    outstanding = d.BalanceDue,
                    contact = d.Contact != null ? d.Contact.Name : null,
                })
                .ToListAsync(ct);

            var statementLine = new
            {
                memo = string.IsNullOrEmpty(txn.Reference) ? txn.Description : $"{txn.Reference} {txn.Description}",
                date = txn.TransactionDate.ToString("yyyy-MM-dd"),
                amount = txn.Amount,
                direction = txn.TransactionType.ToString(),
            };

            var req = BankComprehensiveMatchPrompt.Build(
                companyId, bankTransactionId,
                statementLine, openDocs.Cast<object>().ToArray(),
                recentPayments: Array.Empty<object>(),
                counterpartyOffsetSummary: null);
            var resp = await _orchestrator.AskAsync(req, ct);
            // BankComprehensiveMatchPrompt expects { matches, missing_pieces,
            // patterns_detected } — the UI's three-tab view requires all three.
            return ToResult(resp, "matches", "missing_pieces", "patterns_detected");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Comprehensive bank match augmenter failed");
            return Fallback(null);
        }
    }

    private static AdvancedAiResult ToResult(AiResponse resp, params string[] expectedTopLevelKeys) => new(
        Primary: resp.PrimaryAnswer,
        Confidence: resp.Confidence,
        StructuredJson: resp.RawResponseJson,
        Risks: resp.Risks,
        ComplianceFlags: resp.ComplianceFlags,
        Reasoning: resp.Reasoning,
        SuggestedActions: resp.SuggestedActions,
        UsedAi: resp.UsedAi,
        FeedbackId: resp.FeedbackId,
        SchemaWarnings: ValidateSchema(resp.RawResponseJson, resp.UsedAi, expectedTopLevelKeys));

    /// <summary>Verify the AI's JSON response contains every expected
    /// top-level key. Catches the silent-render-blank class of bug
    /// where AI succeeded but skipped the field the UI parses. Returns
    /// an empty list when AI didn't run (no schema to validate) or
    /// when all keys are present.</summary>
    private static IReadOnlyList<string> ValidateSchema(
        string? rawJson, bool usedAi, IReadOnlyList<string> expectedKeys)
    {
        if (!usedAi || expectedKeys.Count == 0) return Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(rawJson))
            return new[] { "AI ตอบแต่ JSON ว่าง — ไม่สามารถ render ผลได้" };
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new[] { $"AI ตอบแต่ JSON root ไม่ใช่ object (เป็น {doc.RootElement.ValueKind})" };
            var missing = expectedKeys.Where(k => !doc.RootElement.TryGetProperty(k, out _)).ToList();
            return missing.Count == 0
                ? Array.Empty<string>()
                : new[] { $"AI ตอบแต่ขาด key: {string.Join(", ", missing)}" };
        }
        catch (JsonException ex)
        {
            return new[] { $"AI ตอบแต่ JSON parse ไม่ได้: {ex.Message}" };
        }
    }

    private static AdvancedAiResult Fallback(string? primary) => new(
        primary, null, null,
        Array.Empty<string>(), Array.Empty<string>(),
        null, Array.Empty<string>(),
        false, null,
        Array.Empty<string>());
}

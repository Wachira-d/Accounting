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
                // ชนิดกระดาษ vs เอกสารที่จะสร้าง เป็นคนละคำถาม — prompt ขอทั้งคู่
                scanned_document_type = scan.ScannedDocumentType,
                target_document_type = scan.TargetDocumentType,
                our_role = scan.OurRole,
                document_number = scan.ExtractedDocumentNumber,
                document_date = scan.ExtractedDate?.ToString("yyyy-MM-dd"),
                vendor_name = scan.ExtractedVendorName,
                vendor_tax_id = scan.ExtractedVendorTaxId,
                // §86/4 บังคับสาขาทั้งสองฝั่ง และ prompt สั่งให้ตรวจ §86 —
                // เดิมไม่ส่งเลย โมเดลจึงตรวจข้อนี้ไม่ได้
                vendor_branch_code = scan.VendorBranchCode,
                vendor_address = scan.VendorAddress,
                buyer_name = scan.BuyerName,
                buyer_tax_id = scan.BuyerTaxId,
                buyer_branch_code = scan.BuyerBranchCode,
                buyer_address = scan.BuyerAddress,
                sub_total = scan.ExtractedSubTotal,
                vat_amount = scan.ExtractedVatAmount,
                discount_amount = scan.ExtractedDiscountAmount,
                total_amount = scan.ExtractedTotalAmount,
                has_wht = scan.HasWht,
                wht_rate = scan.WhtRate,
                confidence = scan.Confidence,
            };

            // รายการที่สกัดได้แล้ว — output schema สั่งให้คืน line_items กลับมา
            // พร้อม unit/qty/unit_price แต่เดิม**ไม่ส่ง input ให้เทียบเลย**
            object? lineItems = null;
            if (!string.IsNullOrWhiteSpace(scan.ExtractedItemsJson))
            {
                try
                {
                    lineItems = System.Text.Json.JsonSerializer
                        .Deserialize<System.Text.Json.JsonElement>(scan.ExtractedItemsJson);
                }
                catch { /* JSON เสีย — ส่ง null ดีกว่าทำทั้ง call ล้ม */ }
            }

            var req = OcrReviewPrompt.Build(
                companyId, scanResultId,
                scan.RawTextContent ?? "",
                extracted,
                companyContext: company,
                vendorHistory: vendorHistory,
                localGuessTargetType: scan.TargetDocumentType ?? scan.DocumentType,
                localConfidence: scan.Confidence,
                lineItems: lineItems);
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

            // ประวัติการชำระต่อคู่ค้า (12 เดือน) — prompt สั่งให้แยก "ค้างแต่จ่าย
            // ตรงเวลาเสมอ" ออกจาก "เสี่ยงจริง" ซึ่งทำไม่ได้ถ้าไม่มีข้อมูลนี้
            // (เดิมส่ง new{} ว่างเปล่า → AI มโนล้วน). ดึงจาก Payments ย้อน 12 เดือน
            // join เอกสาร คำนวณ avg วันจ่าย + นับตรง/ช้า ต่อคู่ค้า top 15
            var yearAgo = today.AddYears(-1);
            var arTypes = new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt };
            var apTypes = new[] { DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.PaymentVoucher };

            async Task<object> PayHistoryAsync(DocumentType[] types)
            {
                // ดึงวันดิบมาคำนวณ client-side — DateDiffDay เป็นของ SQL Server
                // ไม่ใช่ PostgreSQL (Npgsql แปลไม่ได้)
                var raw = await (from p in _db.Payments.AsNoTracking()
                                 join d in _db.Documents.AsNoTracking() on p.DocumentId equals d.Id
                                 where d.CompanyId == companyId && !p.IsDeleted && !d.IsDeleted
                                       && types.Contains(d.DocumentType)
                                       && p.PaymentDate >= yearAgo
                                       // Document.ContactId เป็น Guid (ไม่ใช่ Guid?) ⇒ `!= null`
                                       // เป็นจริงเสมอ (CS8073) ตัวกรองนี้จึงไม่เคยทำงาน:
                                       // เอกสารที่ไม่มีคู่ค้าหลุดเข้ามาเป็น "(unknown)" แล้วถูกเฉลี่ย
                                       // รวมเป็นคู่ค้ารายเดียว ⇒ ประวัติการชำระที่ส่งให้ AI เพี้ยน
                                       && d.ContactId != Guid.Empty
                                 select new
                                 {
                                     ContactName = d.Contact != null ? d.Contact.Name : "(unknown)",
                                     d.DocumentDate,
                                     p.PaymentDate,
                                     CreditDays = d.CreditDays ?? 30,
                                 }).ToListAsync(ct);
                return raw
                    .Select(r => new
                    {
                        r.ContactName,
                        DaysToPay = (int)(r.PaymentDate.Date - r.DocumentDate.Date).TotalDays,
                        r.CreditDays,
                    })
                    .GroupBy(r => r.ContactName)
                    .Select(g => new
                    {
                        contact = g.Key,
                        payments = g.Count(),
                        avg_days_to_pay = (int)Math.Round(g.Average(x => (double)x.DaysToPay)),
                        on_time = g.Count(x => x.DaysToPay <= x.CreditDays),
                        late = g.Count(x => x.DaysToPay > x.CreditDays),
                        worst_days = g.Max(x => x.DaysToPay),
                    })
                    .OrderByDescending(x => x.payments)
                    .Take(15)
                    .ToList();
            }

            var req = ArApAnalysisPrompt.Build(
                companyId,
                arAging: Aging(arDocs),
                apAging: Aging(apDocs),
                customerPaymentHistory: await PayHistoryAsync(arTypes),
                vendorPaymentHistory: await PayHistoryAsync(apTypes));
            var resp = await _orchestrator.AskAsync(req, ct);
            // Local-First: when AI didn't run (disabled / over budget / down),
            // don't hand back an empty panel — build the same risk_buckets +
            // cash_gap_forecast shape from the aging data we already queried so
            // the operator still sees who's overdue. The UI already renders a
            // "🤖 AI ไม่พร้อม — แสดงคำตอบ local model" banner for usedAi=false.
            if (!resp.UsedAi)
                return BuildLocalArApResult(arDocs, apDocs, today);

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

    /// <summary>Deterministic local AR/AP analysis — the kill-switch fallback
    /// for AgingExplanation. Produces the exact risk_buckets / cash_gap_forecast
    /// JSON shape ai-tools.html parses, computed purely from the aging rows.
    /// No learning needed: aging is arithmetic, so this is 100% as correct as
    /// the data (only the narrative polish is missing vs the AI version).</summary>
    private static AdvancedAiResult BuildLocalArApResult(
        List<ArApRow> arDocs, List<ArApRow> apDocs, DateTime today)
    {
        int Days(ArApRow r) => (int)(today - r.DocumentDate.Date).TotalDays;

        List<string> ByContact(IEnumerable<ArApRow> rows, int minDays, int maxDays, string noun)
            => rows.GroupBy(r => r.ContactName)
                   .Select(g => new
                   {
                       Contact = g.Key,
                       Amount = g.Sum(x => x.BalanceDue),
                       MaxDays = g.Max(Days),
                   })
                   .Where(g => g.MaxDays >= minDays && g.MaxDays <= maxDays)
                   .OrderByDescending(g => g.Amount)
                   .Take(8)
                   .Select(g => $"{g.Contact} (ค้าง {g.MaxDays} วัน, ฿{g.Amount:N0})")
                   .ToList();

        var arOver30 = arDocs.Where(r => Days(r) > 30).Sum(r => r.BalanceDue);
        var arOver90 = arDocs.Where(r => Days(r) > 90).Sum(r => r.BalanceDue);
        var apTotal = apDocs.Sum(r => r.BalanceDue);

        var structured = JsonSerializer.Serialize(new
        {
            risk_buckets = new
            {
                high_risk_customers = ByContact(arDocs, 91, int.MaxValue, "ลูกค้า"),
                late_but_reliable = ByContact(arDocs, 31, 90, "ลูกค้า"),
                high_risk_vendors = ByContact(apDocs, 61, int.MaxValue, "เจ้าหนี้"),
            },
            cash_gap_forecast =
                $"ลูกหนี้ค้างเกิน 30 วัน ฿{arOver30:N0} (เกิน 90 วัน ฿{arOver90:N0}) • " +
                $"เจ้าหนี้ค้างชำระรวม ฿{apTotal:N0} • สุทธิ ฿{(arOver30 - apTotal):N0}",
        });

        var actions = new List<string>();
        if (arOver90 > 0)
            actions.Add($"เร่งทวงถามลูกค้าค้างเกิน 90 วัน (฿{arOver90:N0}) — เสี่ยงเป็นหนี้สูญ");
        if (arOver30 > 0)
            actions.Add($"ติดตามลูกหนี้ค้างเกิน 30 วัน รวม ฿{arOver30:N0}");
        if (apTotal > arOver30 && apTotal > 0)
            actions.Add($"เจ้าหนี้ค้างชำระ (฿{apTotal:N0}) มากกว่าลูกหนี้ที่จะเก็บได้ — วางแผนกระแสเงินสด");
        if (actions.Count == 0)
            actions.Add("ไม่มีรายการค้างที่ต้องดำเนินการเร่งด่วน");

        return new AdvancedAiResult(
            Primary: null,
            Confidence: null,
            StructuredJson: structured,
            Risks: Array.Empty<string>(),
            ComplianceFlags: Array.Empty<string>(),
            Reasoning: "วิเคราะห์โดย local model (ระบบคำนวณอายุหนี้เอง — AI ไม่พร้อม)",
            SuggestedActions: actions,
            UsedAi: false,
            FeedbackId: null,
            SchemaWarnings: Array.Empty<string>());
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

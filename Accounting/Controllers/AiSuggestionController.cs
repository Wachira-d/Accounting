using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Prompts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Per-tenant AI suggestion endpoints — called by UI when the user is
/// about to make a decision and wants AI's recommendation BEFORE
/// committing. Every response carries a FeedbackId so the user's
/// subsequent click can be paired back to the original suggestion via
/// /ai-feedback/record.
///
/// All endpoints follow the same shape:
///   request: minimal context (the decision being made)
///   response: { primary, confidence, alternatives, risks,
///               complianceFlags, reasoning, suggestedActions,
///               feedbackId, usedAi }
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/ai")]
[Authorize]
public class AiSuggestionController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentAiAugmenter _docAi;
    private readonly IBankAiAugmenter _bankAi;
    private readonly IAdvancedAiAugmenter _advAi;
    private readonly IAiOrchestrator _orchestrator;
    private readonly IEnumerable<Accounting.Services.Ai.Distillation.ILocalDistillationModel> _localModels;
    private readonly IAiFeedbackRecorder _feedback;

    public AiSuggestionController(AccountingDbContext db,
        IDocumentAiAugmenter docAi, IBankAiAugmenter bankAi,
        IAdvancedAiAugmenter advAi, IAiOrchestrator orchestrator,
        IEnumerable<Accounting.Services.Ai.Distillation.ILocalDistillationModel> localModels,
        IAiFeedbackRecorder feedback)
    { _db = db; _docAi = docAi; _bankAi = bankAi; _advAi = advAi; _orchestrator = orchestrator;
      _localModels = localModels; _feedback = feedback; }

    // ────────────────────────────────────────────────────────────────
    //  Payment Voucher: suggest the settlement basis (Cash จ่ายทันที vs
    //  Credit เครดิต) for a supplier. Served by the local
    //  PaymentTypeDistillationModel — confirmed history when it has
    //  enough samples, else a deterministic heuristic (open payables +
    //  past PV mix). A feedback row is recorded so the operator's actual
    //  choice trains the model over time (/ai-feedback/record).
    // ────────────────────────────────────────────────────────────────
    [HttpGet("payment-voucher/suggest-type")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestPaymentType(
        Guid companyId, [FromQuery] Guid contactId, CancellationToken ct)
    {
        if (contactId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "contactId ห้ามว่าง"));

        var model = _localModels.FirstOrDefault(m => m.FeatureKey == AiFeatureKey.PaymentTypeSuggestion);
        if (model == null)
            return Ok(new ApiResponse<object>(true, new { paymentType = "Cash", confidence = 0.5, reasoning = "default", feedbackId = (Guid?)null }));

        var inputJson = JsonSerializer.Serialize(new { contactId = contactId.ToString() });
        var pred = await model.PredictAsync(companyId, inputJson, ct);
        var answer = pred?.PrimaryAnswer ?? "Cash";
        var confidence = pred?.Confidence ?? 0.5m;

        // Record so the user's eventual confirmation (via /ai-feedback/record)
        // becomes a training row for this supplier.
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.PaymentTypeSuggestion,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: answer, AiConfidence: confidence,
                LocalModelAnswer: answer, LocalModelConfidence: confidence,
                LocalModelVersion: pred?.ModelVersion ?? model.Version,
                SourceEntityType: "Contact", SourceEntityId: contactId,
                Status: AiCallStatus.Success, ProviderUsed: AiProviderType.DeepSeek,
                ModelVersion: pred?.ModelVersion, LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* feedback is best-effort */ }

        var reasoning = (pred?.ModelVersion?.StartsWith("heuristic") ?? false)
            ? (answer == "Credit"
                ? "ผู้ขายรายนี้มีหนี้ค้าง/ประวัติซื้อเชื่อ — แนะนำตั้งเป็นเครดิต"
                : "ไม่มีหนี้ค้างกับผู้ขายรายนี้ — แนะนำจ่ายทันที")
            : $"เรียนรู้จากประวัติที่ยืนยันแล้ว {pred?.SupportingSamples ?? 0} ครั้ง";

        return Ok(new ApiResponse<object>(true, new
        {
            paymentType = answer,
            confidence,
            supportingSamples = pred?.SupportingSamples ?? 0,
            reasoning,
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  GL account suggestion when composing a Payment Voucher line
    //  from a source Tax Invoice (the user's explicit example).
    // ────────────────────────────────────────────────────────────────

    public sealed record SuggestPaymentVoucherAccountRequest(
        Guid SourceInvoiceId,
        string LineDescription,
        decimal Amount,
        string? Currency,
        string? CurrentAccountCode,
        // Direct counterparty hint — for documents still in the create form
        // where no source invoice exists yet but the operator has already
        // picked the contact. Used when SourceInvoiceId is Empty.
        Guid? ContactId = null);

    [HttpPost("payment-voucher/suggest-account")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestPaymentVoucherAccount(
        Guid companyId, [FromBody] SuggestPaymentVoucherAccountRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.LineDescription))
            return BadRequest(new ApiResponse<object>(false, null, "LineDescription ห้ามว่าง"));

        // Resolve vendor context. Source invoice takes priority (richest
        // signal — gives doc type / amount baseline). Otherwise fall back to
        // the contact id passed directly from the in-progress create form so
        // AI still sees the supplier name + tax id when classifying a
        // brand-new document the operator hasn't saved yet.
        string? vendorName = null, vendorTaxId = null;
        if (req.SourceInvoiceId != Guid.Empty)
        {
            var src = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == req.SourceInvoiceId && d.CompanyId == companyId && !d.IsDeleted)
                .Select(d => new
                {
                    ContactName = d.Contact != null ? d.Contact.Name : null,
                    ContactTaxId = d.Contact != null ? d.Contact.TaxId : null,
                })
                .FirstOrDefaultAsync(ct);
            vendorName = src?.ContactName;
            vendorTaxId = src?.ContactTaxId;
        }
        if (vendorName == null && req.ContactId.HasValue && req.ContactId.Value != Guid.Empty)
        {
            var c = await _db.Contacts.AsNoTracking()
                .Where(x => x.Id == req.ContactId.Value && x.CompanyId == companyId)
                .Select(x => new { x.Name, x.TaxId })
                .FirstOrDefaultAsync(ct);
            vendorName = c?.Name;
            vendorTaxId = c?.TaxId;
        }

        var result = await _docAi.SuggestPaymentVoucherAccountingAsync(
            companyId, req.SourceInvoiceId,
            vendorName: vendorName,
            vendorTaxId: vendorTaxId,
            vendorIndustry: null,
            lineDescription: req.LineDescription,
            amount: req.Amount,
            currency: req.Currency ?? "THB",
            localBestAccountCode: req.CurrentAccountCode,
            localConfidence: req.CurrentAccountCode != null ? 0.50m : (decimal?)null,
            ct);

        return Ok(new ApiResponse<object>(true, ToDto(result)));
    }

    // ────────────────────────────────────────────────────────────────
    //  WHT category inference — when user is about to set the WHT
    //  rate on a payment line. AI proposes code + rate based on
    //  vendor type + line description.
    // ────────────────────────────────────────────────────────────────

    public sealed record InferWhtRequest(
        Guid? DocumentId,
        string? VendorName,
        string? VendorTaxId,
        string? VendorType,
        string LineDescription,
        decimal Amount,
        string? CurrentCode);

    [HttpPost("wht/infer-category")]
    public async Task<ActionResult<ApiResponse<object>>> InferWhtCategory(
        Guid companyId, [FromBody] InferWhtRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.LineDescription))
            return BadRequest(new ApiResponse<object>(false, null, "LineDescription ห้ามว่าง"));
        var result = await _docAi.InferWhtCategoryAsync(
            companyId, req.DocumentId,
            req.VendorName, req.VendorTaxId, req.VendorType,
            req.LineDescription, req.Amount,
            req.CurrentCode, req.CurrentCode != null ? 0.50m : (decimal?)null,
            ct);
        return Ok(new ApiResponse<object>(true, ToDto(result)));
    }

    // ────────────────────────────────────────────────────────────────
    //  VAT type inference — per document line (7% / 0% / Exempt).
    //  Heuristic-first picker; ground-truth from user override trains
    //  OcrCategoryMapping (vendor+keyword → rate) so reused vendor lines
    //  auto-fill the right rate.
    // ────────────────────────────────────────────────────────────────

    public sealed record InferVatRequest(
        Guid? ContactId,
        string? VendorName,
        string? VendorTaxId,
        string? VendorCountryCode,    // "TH" or 2-letter ISO; null = unknown
        string LineDescription,
        decimal Amount,
        string? CurrentRate);          // "7" | "0" | "Exempt"

    [HttpPost("vat/infer-type")]
    public async Task<ActionResult<ApiResponse<object>>> InferVatType(
        Guid companyId, [FromBody] InferVatRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.LineDescription))
            return BadRequest(new ApiResponse<object>(false, null, "LineDescription ห้ามว่าง"));

        // 1) Vendor-side check: ผู้ขายต่างประเทศ → export rule (0% ฝั่งขาย,
        //    หรือ ภ.พ.36 ฝั่งซื้อ). ใช้ทั้ง explicit country และเทียบ TaxId
        //    ที่ไม่ใช่รูปแบบไทย (13 หลัก เริ่ม 0–9).
        var isForeign = !string.IsNullOrWhiteSpace(req.VendorCountryCode)
            && !string.Equals(req.VendorCountryCode, "TH", StringComparison.OrdinalIgnoreCase);
        if (!isForeign && !string.IsNullOrWhiteSpace(req.VendorTaxId))
        {
            var tid = req.VendorTaxId.Where(char.IsDigit).ToArray();
            if (tid.Length > 0 && tid.Length != 13) isForeign = true;
        }

        // 2) Description-side check: keyword สำหรับ Exempt (ตาม §81/1-15)
        //    ครอบคลุมหมวดที่ SMB ไทยเจอบ่อย — ครู / ผัก / นม / หนังสือ /
        //    หนังสือพิมพ์ / ยา (เฉพาะตามรายการกระทรวงสาธารณสุข) / ปุ๋ย
        var desc = req.LineDescription.ToLowerInvariant();
        var exemptKeywords = new[]
        {
            "ค่าเล่าเรียน", "ค่าเรียน", "ค่าสอน", "tuition",
            "ผัก", "ผลไม้", "ข้าวสาร",
            "นม", "milk", "fresh milk",
            "หนังสือ", "นิตยสาร", "หนังสือพิมพ์", "newspaper",
            "ปุ๋ย", "อาหารสัตว์", "เมล็ดพันธุ์",
            "ค่าขนส่งสาธารณะ", "รถเมล์", "รถไฟ", "btx", "mrt",
            "ค่าเช่าอสังหาริมทรัพย์", "rental of immovable property",
            "ค่ารักษาพยาบาล", "โรงพยาบาล",
        };
        var isExempt = exemptKeywords.Any(k => desc.Contains(k));

        // 3) Pick + reasoning
        string suggestion;
        string reasoning;
        if (isExempt)
        {
            suggestion = "Exempt";
            reasoning = "รายละเอียดตรงกับหมวดยกเว้น VAT ตาม §81 (อาหาร / ขนส่ง / การศึกษา / หนังสือ ฯลฯ)";
        }
        else if (isForeign)
        {
            suggestion = "0";
            reasoning = "ผู้ขายต่างประเทศ — ฝั่งขายใช้ 0% (export), ฝั่งซื้อให้บันทึก ภ.พ.36 แยก";
        }
        else
        {
            suggestion = "7";
            reasoning = "ผู้ขายในประเทศ + รายการทั่วไป → VAT 7% (อัตราปกติ)";
        }

        // Log to feedback so user override trains the model
        var inputJson = JsonSerializer.Serialize(new
        {
            req.ContactId, req.VendorTaxId, req.VendorCountryCode,
            req.LineDescription, req.Amount, isForeign, isExempt,
        });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.VatTypeInference,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: suggestion, AiConfidence: isExempt || isForeign ? 0.85m : 0.95m,
                LocalModelAnswer: suggestion, LocalModelConfidence: 0.85m,
                LocalModelVersion: "heuristic-v1",
                SourceEntityType: "DocumentLine",
                SourceEntityId: req.ContactId,
                Status: AiCallStatus.Success, ProviderUsed: AiProviderType.DeepSeek,
                ModelVersion: "heuristic", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            vatType = suggestion,
            confidence = isExempt || isForeign ? 0.85m : 0.95m,
            reasoning,
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Payment terms (credit days) — pure-lookup picker when user
    //  selects a contact on a new sales/purchase document. Resolves:
    //   Contact.CreditDays
    //    → OcrVendorIntelligence.TypicalPaymentTermsDays
    //    → CompanySettings.DefaultPaymentDueDays (final fallback)
    //  No ML — but logged through orchestrator so override pattern
    //  feeds future per-vendor improvement.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("payment-terms/suggest")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestPaymentTerms(
        Guid companyId, [FromQuery] Guid contactId, CancellationToken ct)
    {
        if (contactId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "contactId ห้ามว่าง"));

        var contact = await _db.Contacts.AsNoTracking()
            .Where(c => c.Id == contactId && c.CompanyId == companyId)
            .Select(c => new { c.Name, c.TaxId })
            .FirstOrDefaultAsync(ct);

        // Tier 1 — Mode (ค่าที่ใช้บ่อยที่สุด) จากเอกสารล่าสุด 12 ใบที่มี
        //          CreditDays ระบุ — ทนกับเอกสารกระตุ้นเดี่ยวที่ตั้งผิด.
        // Tier 2 — OcrVendorIntelligence.TypicalPaymentTermsDays (per-tax-id)
        // Tier 3 — CompanySettings.DefaultPaymentDueDays
        int days; string source; decimal confidence;
        var recentTerms = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId
                && d.CreditDays != null && d.CreditDays > 0 && !d.IsDeleted)
            .OrderByDescending(d => d.DocumentDate)
            .Take(12)
            .Select(d => d.CreditDays!.Value)
            .ToListAsync(ct);
        if (recentTerms.Count >= 2)
        {
            // Mode of recent CreditDays values — บ่อยสุดชนะ tie ใช้ค่าล่าสุด
            days = recentTerms.GroupBy(x => x).OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key).First().Key;
            source = "DocumentHistory";
            confidence = recentTerms.Count >= 5 ? 0.92m : 0.75m;
        }
        else
        {
            int? learned = null;
            if (!string.IsNullOrWhiteSpace(contact?.TaxId))
            {
                learned = await _db.Set<OcrVendorIntelligence>().AsNoTracking()
                    .Where(v => v.CompanyId == companyId && v.VendorTaxId == contact.TaxId)
                    .Select(v => v.TypicalPaymentTermsDays)
                    .FirstOrDefaultAsync(ct);
            }
            if (learned.HasValue && learned.Value > 0)
            {
                days = learned.Value; source = "OcrVendorIntelligence"; confidence = 0.80m;
            }
            else
            {
                days = await _db.CompanySettings.AsNoTracking()
                    .Where(s => s.CompanyId == companyId)
                    .Select(s => s.DefaultPaymentDueDays)
                    .FirstOrDefaultAsync(ct);
                if (days <= 0) days = 30;
                source = "CompanySettings.DefaultPaymentDueDays"; confidence = 0.50m;
            }
        }

        var inputJson = JsonSerializer.Serialize(new { contactId, vendorName = contact?.Name, vendorTaxId = contact?.TaxId });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.PaymentTermsSuggestion,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: days.ToString(), AiConfidence: confidence,
                LocalModelAnswer: days.ToString(), LocalModelConfidence: confidence,
                LocalModelVersion: "lookup-v1",
                SourceEntityType: "Contact", SourceEntityId: contactId,
                Status: AiCallStatus.Success, ProviderUsed: AiProviderType.DeepSeek,
                ModelVersion: "lookup", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            creditDays = days,
            source,
            confidence,
            reasoning = source switch
            {
                "DocumentHistory" => $"จากเอกสาร {recentTerms.Count} ใบล่าสุดกับผู้ติดต่อรายนี้: บ่อยสุด Net {days} วัน",
                "OcrVendorIntelligence" => $"เรียนรู้จากประวัติเอกสารกับผู้ขายรายนี้: ปกติ {days} วัน",
                _ => $"ใช้ค่าเริ่มต้นบริษัท: Net {days} วัน (ยังไม่มีข้อมูลผู้ขายรายนี้)",
            },
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Credit-note reason classification — when user creates a CN
    //  from an invoice, AI proposes Return / Discount / Adjustment /
    //  Writeoff before they pick from the dropdown.
    // ────────────────────────────────────────────────────────────────

    public sealed record ClassifyCnReasonRequest(
        Guid CreditNoteId,
        Guid? OriginalInvoiceId,
        string? CurrentReason);

    [HttpPost("credit-note/classify-reason")]
    public async Task<ActionResult<ApiResponse<object>>> ClassifyCreditNoteReason(
        Guid companyId, [FromBody] ClassifyCnReasonRequest req, CancellationToken ct)
    {
        var cn = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == req.CreditNoteId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate,
                d.TotalAmount, d.SubTotal, d.Currency, d.Notes,
                LineDescriptions = d.Lines.Select(l => l.Description).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (cn == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบใบลดหนี้"));

        object? origSnapshot = null;
        if (req.OriginalInvoiceId.HasValue)
        {
            origSnapshot = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == req.OriginalInvoiceId.Value && d.CompanyId == companyId && !d.IsDeleted)
                .Select(d => new
                {
                    d.Id, d.DocumentNumber, d.DocumentDate, d.TotalAmount, d.SubTotal,
                    LineDescriptions = d.Lines.Select(l => l.Description).ToList(),
                })
                .FirstOrDefaultAsync(ct);
        }

        var result = await _docAi.ClassifyCreditNoteReasonAsync(
            companyId, req.CreditNoteId, cn, origSnapshot,
            req.CurrentReason ?? "Adjustment", 0.40m, ct);
        return Ok(new ApiResponse<object>(true, ToDto(result)));
    }

    // ────────────────────────────────────────────────────────────────
    //  Bank statement match — when user is reconciling a statement
    //  line that didn't auto-match.
    // ────────────────────────────────────────────────────────────────

    public sealed record BankMatchRequest(
        Guid BankTransactionId,
        string? CurrentMatchedDocId);

    [HttpPost("bank/suggest-match")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestBankMatch(
        Guid companyId, [FromBody] BankMatchRequest req, CancellationToken ct)
    {
        var txn = await _db.Set<BankTransaction>().AsNoTracking()
            .Where(t => t.Id == req.BankTransactionId && t.CompanyId == companyId)
            .Select(t => new
            {
                t.Id, t.TransactionDate, t.Amount, t.TransactionType,
                t.Description, t.Reference,
            })
            .FirstOrDefaultAsync(ct);
        if (txn == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบ bank transaction"));

        var memo = string.IsNullOrEmpty(txn.Reference) ? txn.Description : $"{txn.Reference} {txn.Description}";
        var result = await _bankAi.SuggestStatementMatchAsync(
            companyId, txn.Id, memo, txn.TransactionDate, txn.Amount, "THB",
            txn.TransactionType, req.CurrentMatchedDocId,
            req.CurrentMatchedDocId != null ? 0.50m : (decimal?)null,
            ct);
        return Ok(new ApiResponse<object>(true, ToDto(result)));
    }

    // ────────────────────────────────────────────────────────────────
    //  Anomaly explanation — lazy "explain this" button on the
    //  anomaly card. Cached on the AnomalyDetection row so second
    //  view doesn't re-bill.
    // ────────────────────────────────────────────────────────────────

    [HttpPost("anomalies/{anomalyId:guid}/explain")]
    public async Task<ActionResult<ApiResponse<object>>> ExplainAnomaly(
        Guid companyId, Guid anomalyId, [FromQuery] bool force = false, CancellationToken ct = default)
    {
        var anomaly = await _db.AnomalyDetections
            .FirstOrDefaultAsync(a => a.Id == anomalyId && a.CompanyId == companyId && !a.IsDeleted, ct);
        if (anomaly == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบ anomaly"));

        // Cache short-circuit unless force=true. Cached explanation is
        // valid until the underlying data changes — the simplest proxy
        // is age-based (re-explain after 30 days), but for now we leave
        // it indefinite; admin retry button passes force=true.
        if (!force && !string.IsNullOrEmpty(anomaly.AiReasoning))
        {
            return Ok(new ApiResponse<object>(true, new
            {
                primary = anomaly.AiVerdict,
                confidence = anomaly.AiConfidence,
                reasoning = anomaly.AiReasoning,
                suggestedActions = DeserializeList(anomaly.AiSuggestedActionsJson),
                risks = DeserializeList(anomaly.AiRisksJson),
                feedbackId = anomaly.AiFeedbackId,
                usedAi = anomaly.AiFeedbackId.HasValue,
                cached = true,
            }));
        }

        // Build vendor context if anomaly is on a Document — gives AI
        // grounding for "is this amount really unusual for this vendor?".
        object vendorHistory = "";
        object peerAverage = "";
        if (anomaly.EntityType == "Document" || anomaly.EntityType == "JournalEntry")
        {
            try
            {
                var doc = await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == anomaly.EntityId && d.CompanyId == companyId)
                    .Select(d => new { d.ContactId })
                    .FirstOrDefaultAsync(ct);
                if (doc?.ContactId != null)
                {
                    var since = DateTime.UtcNow.AddMonths(-12);
                    var hist = await _db.Documents.AsNoTracking()
                        .Where(d => d.CompanyId == companyId && d.ContactId == doc.ContactId
                                    && d.DocumentDate >= since && !d.IsDeleted)
                        .Select(d => d.TotalAmount).ToListAsync(ct);
                    if (hist.Count > 0)
                    {
                        vendorHistory = new
                        {
                            count = hist.Count,
                            avg = hist.Average(),
                            min = hist.Min(),
                            max = hist.Max(),
                            median = hist.OrderBy(x => x).Skip(hist.Count / 2).FirstOrDefault(),
                        };
                    }
                }
            }
            catch { /* grounding is best-effort */ }
        }

        var req = AnomalyExplainPrompt.Build(
            companyId, anomalyId,
            anomaly.AnomalyType, anomaly.Description,
            vendorHistory, peerAverage,
            anomaly.ActualValue ?? 0m, anomaly.DetailJson ?? "",
            localGuess: anomaly.Severity == "Critical" ? "LikelyError" : "NeedReview");
        var resp = await _orchestrator.AskAsync(req, ct);

        // Persist explanation on the anomaly row so subsequent reads
        // don't re-call. UserChoice (Acknowledge/Resolve/FalsePositive)
        // is recorded via the existing /acknowledge / /resolve /
        // /false-positive endpoints and paired with AiFeedbackId via
        // a separate /ai-feedback/record call from the UI.
        if (resp.UsedAi && !string.IsNullOrEmpty(resp.PrimaryAnswer))
        {
            anomaly.AiVerdict = resp.PrimaryAnswer;
            anomaly.AiConfidence = resp.Confidence;
            anomaly.AiReasoning = resp.Reasoning;
            anomaly.AiSuggestedActionsJson = JsonSerializer.Serialize(resp.SuggestedActions);
            anomaly.AiRisksJson = JsonSerializer.Serialize(resp.Risks);
            anomaly.AiFeedbackId = resp.FeedbackId;
            anomaly.AiExplainedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new ApiResponse<object>(true, new
        {
            primary = resp.PrimaryAnswer,
            confidence = resp.Confidence,
            reasoning = resp.Reasoning,
            suggestedActions = resp.SuggestedActions,
            risks = resp.Risks,
            feedbackId = resp.FeedbackId,
            usedAi = resp.UsedAi,
            cached = false,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Payment-voucher batch suggestion — one call returns AI's pick
    //  for EVERY line in one go. Used by the "สร้างใบสำคัญจ่ายจาก
    //  ใบกำกับภาษี" wizard so the user sees a fully pre-filled draft
    //  and only needs to override the rare bad guess.
    // ────────────────────────────────────────────────────────────────

    public sealed record BatchSuggestPvRequest(Guid SourceInvoiceId);

    [HttpPost("payment-voucher/suggest-all-accounts")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestAllPvAccounts(
        Guid companyId, [FromBody] BatchSuggestPvRequest req, CancellationToken ct)
    {
        var src = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == req.SourceInvoiceId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new
            {
                d.Id, d.Currency,
                ContactName = d.Contact != null ? d.Contact.Name : null,
                ContactTaxId = d.Contact != null ? d.Contact.TaxId : null,
                Lines = d.Lines.Select(l => new { l.Id, l.Description, l.Amount, l.AccountId })
                               .ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (src == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบ source invoice"));
        if (src.Lines.Count == 0)
            return Ok(new ApiResponse<object>(true, new { lines = Array.Empty<object>() }));

        // Single BULK AI call covering every line — AI sees cross-line
        // patterns (cluster detection, odd-line-out) the previous
        // per-line fan-out couldn't. Cost drops from N× to 1×; accuracy
        // improves on multi-category invoices.
        using var aiCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bulkLines = src.Lines.Select(l => (
            LineId: l.Id,
            Description: l.Description ?? "",
            Amount: l.Amount,
            CurrentAccountCode: (string?)null)).ToList();
        var bulk = await _docAi.SuggestAllPaymentVoucherAccountingAsync(
            companyId, req.SourceInvoiceId,
            src.ContactName, src.ContactTaxId, vendorIndustry: null,
            bulkLines, src.Currency ?? "THB", aiCts.Token);

        var results = src.Lines.Select(ln => new
        {
            lineId = ln.Id,
            description = ln.Description,
            amount = ln.Amount,
            ai = ToDto(bulk.ByLineId.TryGetValue(ln.Id, out var s) ? s : null),
        }).ToList();
        return Ok(new ApiResponse<object>(true, new
        {
            lines = results,
            crossLineObservations = bulk.CrossLineObservations,
            warnings = bulk.Warnings,
            usedAi = bulk.UsedAi,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Advanced AI: Tier-4 OCR review, stock decisions, AR/AP
    //  analysis, doc conversion, comprehensive bank match.
    //  All driven by IAdvancedAiAugmenter which sees the entire
    //  scan + company + relevant history in one shot.
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tier-4 review: AI re-reads everything the OCR pipeline extracted
    /// + raw text + company context + vendor history, returns
    /// corrections / corrections-confirmed + Thai compliance flags
    /// (e.g. "vendor has no Tax ID — VAT line is invalid per §86").
    /// Used by the OCR review modal "🤖 ตรวจสอบกับ AI" button.
    /// </summary>
    [HttpPost("ocr/{scanResultId:guid}/ai-review")]
    public async Task<ActionResult<ApiResponse<object>>> ReviewScanWithAi(
        Guid companyId, Guid scanResultId, CancellationToken ct)
    {
        var r = await _advAi.ReviewOcrAsync(companyId, scanResultId, ct);
        return Ok(new ApiResponse<object>(true, ToAdvancedDto(r)));
    }

    /// <summary>
    /// For each line of an OCR'd document, decide CreateNew/Update/Match
    /// + Inventory/Supply/FixedAsset/Service + semantic match for
    /// equivalent-but-differently-named items.
    /// </summary>
    [HttpPost("ocr/{scanResultId:guid}/stock-decisions")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestStockDecisions(
        Guid companyId, Guid scanResultId, CancellationToken ct)
    {
        var r = await _advAi.SuggestStockDecisionsAsync(companyId, scanResultId, ct);
        return Ok(new ApiResponse<object>(true, ToAdvancedDto(r)));
    }

    /// <summary>Whole-company AR/AP analysis with risk buckets +
    /// cash gap forecast. Used by reports / dashboard.</summary>
    [HttpPost("ar-ap/analyze")]
    public async Task<ActionResult<ApiResponse<object>>> AnalyzeArAp(
        Guid companyId, CancellationToken ct)
    {
        var r = await _advAi.AnalyzeArApAsync(companyId, ct);
        return Ok(new ApiResponse<object>(true, ToAdvancedDto(r)));
    }

    /// <summary>
    /// "What can I create from this scanned doc?" AI proposes the
    /// target doc type list (Invoice→Receipt+TaxInvoice+DeliveryNote
    /// etc.) + prefill strategy per target.
    /// </summary>
    [HttpPost("ocr/{scanResultId:guid}/conversion-suggestions")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestConversion(
        Guid companyId, Guid scanResultId, CancellationToken ct)
    {
        var r = await _advAi.SuggestDocumentConversionAsync(companyId, scanResultId, ct);
        return Ok(new ApiResponse<object>(true, ToAdvancedDto(r)));
    }

    /// <summary>
    /// Comprehensive bank-statement matching — 1to1, 1toMany, Manyto1,
    /// Offset, WithDeductions, Incomplete patterns. Identifies missing
    /// pieces when no complete match exists.
    /// </summary>
    [HttpPost("bank/{bankTransactionId:guid}/comprehensive-match")]
    public async Task<ActionResult<ApiResponse<object>>> ComprehensiveBankMatch(
        Guid companyId, Guid bankTransactionId, CancellationToken ct)
    {
        var r = await _advAi.ComprehensiveBankMatchAsync(companyId, bankTransactionId, ct);
        return Ok(new ApiResponse<object>(true, ToAdvancedDto(r)));
    }

    private static object ToAdvancedDto(AdvancedAiResult r) => new
    {
        primary = r.Primary,
        confidence = r.Confidence,
        // Raw JSON the AI returned. UI parses per-feature fields
        // (corrections, lines, targets, etc.) from this directly.
        structured = r.StructuredJson,
        risks = r.Risks,
        complianceFlags = r.ComplianceFlags,
        reasoning = r.Reasoning,
        suggestedActions = r.SuggestedActions,
        feedbackId = r.FeedbackId,
        usedAi = r.UsedAi,
        // Schema validation outcome from the augmenter — populated when
        // AI ran but its JSON didn't carry the keys the UI parses (e.g.
        // 'corrections' missing from OCR-review, 'risk_buckets' missing
        // from AR/AP analysis). UI shows these as orange warnings instead
        // of silently rendering blank sections.
        schemaWarnings = r.SchemaWarnings,
    };

    private static IReadOnlyList<string> DeserializeList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return Array.Empty<string>(); }
    }

    // ────────────────────────────────────────────────────────────────
    //  Reorder forecast narrative — wraps the Croston output with a
    //  DeepSeek prose summary so admin gets a one-line per-SKU
    //  recommendation ("SKU 1234 จะหมดใน 7 วัน — แนะนำสั่ง 50 ชิ้น").
    // ────────────────────────────────────────────────────────────────
    public sealed record ReorderNarrativeRequest(
        List<ReorderNarrativeRow> Rows);

    public sealed record ReorderNarrativeRow(
        string Sku, string Name, decimal CurrentStock,
        decimal AvgDailyDemand, decimal DaysOfStockRemaining,
        decimal SuggestedOrderQuantity, string Urgency);

    [HttpPost("inventory/reorder-narrative")]
    public async Task<ActionResult<ApiResponse<object>>> ReorderNarrative(
        Guid companyId, [FromBody] ReorderNarrativeRequest req,
        [FromServices] IAiOrchestrator orchestrator,
        CancellationToken ct)
    {
        if (req.Rows == null || req.Rows.Count == 0)
            return Ok(new ApiResponse<object>(true, new { narrative = "", usedAi = false }));
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            task = "reorder_forecast_narrative",
            rows = req.Rows.Take(40),       // cap context
        });
        var aiReq = new AiRequest
        {
            FeatureKey = AiFeatureKey.ReorderForecast,
            CompanyId = companyId,
            SystemPrompt = "You are a Thai inventory analyst. Given a Croston-forecasted reorder report, produce 3-5 sentences in Thai prioritising which SKUs to act on first + why. Be specific (use SKU codes + amounts).",
            UserPromptJson = payload,
            CacheTtlOverrideDays = 1,
            MaxTokensOverride = 600,
        };
        var resp = await orchestrator.AskAsync(aiReq, ct);
        // Three-state outcome (the previous "?? Reasoning ?? ''" fallback
        // was useful but ambiguous — the operator couldn't tell whether
        // AI was off, returned bad output, or genuinely had nothing to
        // say). Now status makes it explicit.
        string narrative;
        string status;
        if (!resp.UsedAi)
        {
            narrative = "AI ปิดอยู่หรือไม่พร้อมใช้งาน — ลองอีกครั้งหรือดูข้อมูล raw จากตารางด้านบน";
            status = "ai_unavailable";
        }
        else if (!string.IsNullOrWhiteSpace(resp.PrimaryAnswer))
        {
            narrative = resp.PrimaryAnswer!;
            status = "ok";
        }
        else if (!string.IsNullOrWhiteSpace(resp.Reasoning))
        {
            narrative = resp.Reasoning!;
            status = "partial";    // AI ran but PrimaryAnswer was empty
        }
        else
        {
            narrative = "AI ตอบแต่ผลว่าง — ลองดูจาก raw data แทน";
            status = "empty_response";
        }
        return Ok(new ApiResponse<object>(true, new
        {
            narrative,
            status,
            usedAi = resp.UsedAi,
            feedbackId = resp.FeedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Anomaly explanation — local model returns deterministic Thai
    //  reason + suggested actions from MAD z-score; DeepSeek wraps
    //  the borderline 5% with prose. Hybrid mode by default.
    //
    //  Note on the two endpoints:
    //   POST /anomalies/{anomalyId}/explain  — persisted variant.
    //     Reads an existing AnomalyDetection row, runs AI, then writes
    //     AiVerdict / AiConfidence / AiReasoning / AiFeedbackId BACK
    //     onto the row so subsequent reads (and dashboards) can skip
    //     the AI call. Used for "explain this flagged anomaly" UX.
    //   POST /anomaly/explain                — ad-hoc variant (below).
    //     No persistence — caller passes raw amount + history inline
    //     (e.g. "I'm typing a new invoice and the amount looks high,
    //     what does AI think?"). Used during document entry / before
    //     the row exists in AnomalyDetection. Both paths share the
    //     same prompt builder.
    // ────────────────────────────────────────────────────────────────
    public sealed record ExplainAnomalyRequest(
        Guid? AnomalyId, decimal Amount,
        List<decimal> History,
        string? VendorName, string? VendorTaxId);

    [HttpPost("anomaly/explain")]
    public async Task<ActionResult<ApiResponse<object>>> ExplainAnomaly(
        Guid companyId, [FromBody] ExplainAnomalyRequest req,
        [FromServices] IAiOrchestrator orchestrator,
        CancellationToken ct)
    {
        if (req.Amount <= 0 || req.History == null || req.History.Count < 3)
            return BadRequest(new ApiResponse<object>(false, null,
                "ต้องการ amount + ประวัติย่างน้อย 3 ค่า"));
        var aiReq = Accounting.Services.Ai.Prompts.AnomalyExplainPrompt.Build(
            companyId, req.AnomalyId ?? Guid.NewGuid(),
            anomalyType: "AmountOutlier",
            anomalyDescription: $"Amount {req.Amount} flagged",
            vendorHistory12mo: new { vendor = req.VendorName, history = req.History },
            peerAverage: new { },
            anomalyAmount: req.Amount,
            anomalyContextJson: "{}",
            localGuess: null);
        var resp = await orchestrator.AskAsync(aiReq, ct);
        return Ok(new ApiResponse<object>(true, new
        {
            answer = resp.PrimaryAnswer,
            confidence = resp.Confidence,
            reasoning = resp.Reasoning,
            usedAi = resp.UsedAi,
            feedbackId = resp.FeedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Duplicate document detection — UI calls this BEFORE save to
    //  surface "a very similar document already exists" warning.
    // ────────────────────────────────────────────────────────────────
    public sealed record CheckDuplicateRequest(
        Guid ContactId, decimal Amount, DateTime DocumentDate, string? Subject);

    [HttpPost("documents/check-duplicate")]
    public async Task<ActionResult<ApiResponse<object>>> CheckDuplicate(
        Guid companyId, [FromBody] CheckDuplicateRequest req, CancellationToken ct)
    {
        var r = await _docAi.CheckDuplicateAsync(
            companyId, req.ContactId, req.Amount, req.DocumentDate, req.Subject, ct);
        return Ok(new ApiResponse<object>(true, new
        {
            isDuplicate = r != null,
            ai = ToDto(r),
        }));
    }

    private static object ToDto(DocumentAiSuggestion? r)
    {
        r ??= new DocumentAiSuggestion(
            Answer: null, Confidence: null,
            Alternatives: Array.Empty<string>(),
            Risks: Array.Empty<string>(),
            ComplianceFlags: Array.Empty<string>(),
            Reasoning: null,
            SuggestedActions: Array.Empty<string>(),
            UsedAi: false, FeedbackId: null);
        return new
        {
            primary = r.Answer,
            confidence = r.Confidence,
            alternatives = r.Alternatives,
            risks = r.Risks,
            complianceFlags = r.ComplianceFlags,
            reasoning = r.Reasoning,
            suggestedActions = r.SuggestedActions,
            feedbackId = r.FeedbackId,
            usedAi = r.UsedAi,
        };
    }
}

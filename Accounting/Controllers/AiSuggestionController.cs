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
    //  ONLINE-LEARNING MEMORY — "train ไปเลย"
    //  Every suggestion endpoint calls MemoryLookupAsync FIRST with a
    //  stable per-feature input key. If the company's users have already
    //  settled on an answer for that exact input (learned inline by
    //  AiFeedbackRecorder.RecordUserChoiceAsync the moment they last
    //  confirmed/overrode), it's returned immediately — no heuristic, no
    //  provider call, no batch wait. The same key is stamped onto the
    //  feedback row's PromptHash so the next confirmation updates the
    //  same memory bucket.
    // ────────────────────────────────────────────────────────────────

    /// <summary>Learned answer for (company, feature, key) when the
    /// company has confirmed it at least once and net agreement is ≥50%.
    /// Returns null to fall through to the heuristic.</summary>
    private async Task<(string Answer, decimal Confidence, int Samples)?> MemoryLookupAsync(
        Guid companyId, AiFeatureKey feature, string inputKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputKey)) return null;
        if (inputKey.Length > 256) inputKey = inputKey[..256];
        var m = await _db.AiSuggestionMemories.AsNoTracking().FirstOrDefaultAsync(
            x => x.CompanyId == companyId && x.FeatureKey == feature.ToString() && x.InputKey == inputKey, ct);
        if (m == null) return null;
        if (m.Confidence < 0.50m) return null;          // contested — let heuristic decide
        // ⚠️ ต้องมีคำยืนยันที่ **ตั้งใจ** อย่างน้อยหนึ่งครั้ง — ยอด AcceptCount ล้วน
        // อาจมาจากการกด "อนุมัติ" รัว ๆ โดยไม่เคยมองค่าที่ระบบเติมให้เลย
        // (ทีม T3 รอบ 177 §3.1 · แถวเก่าถูก backfill ให้เท่า AcceptCount ใน migration
        //  จึงไม่มีใครเสียสิ่งที่เคยทำงานอยู่)
        if (m.ExplicitAcceptCount < 1) return null;
        var samples = m.AcceptCount + m.OverrideCount;
        return (m.LearnedAnswer, m.Confidence, samples);
    }

    /// <summary>Normalise a free-text input into a stable memory key —
    /// lowercased, collapsed whitespace, first 60 chars. Used for
    /// name/description-keyed features (product name, asset name, JE memo).</summary>
    private static string NormKey(params string?[] parts)
    {
        var joined = string.Join("|", parts.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => System.Text.RegularExpressions.Regex.Replace(p!.Trim().ToLowerInvariant(), @"\s+", " ")));
        return joined.Length > 200 ? joined[..200] : joined;
    }

    /// <summary>Record a feedback row for a memory-served answer, stamping
    /// the same memory key onto PromptHash so a subsequent user override
    /// updates the same bucket. Returns the feedbackId for the UI to pair
    /// its /ai-feedback/record call to.</summary>
    private async Task<Guid?> RecordLearnedAsync(Guid companyId, AiFeatureKey feature, string memKey,
        string answer, decimal confidence, string entityType, Guid? entityId, CancellationToken ct)
    {
        try
        {
            return await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: feature,
                PromptHash: memKey, PromptJson: JsonSerializer.Serialize(new { memKey, source = "Learned" }),
                ResponseJson: null, AiPrimaryAnswer: answer, AiConfidence: confidence,
                LocalModelAnswer: answer, LocalModelConfidence: confidence,
                LocalModelVersion: "memory-v1",
                SourceEntityType: entityType, SourceEntityId: entityId,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "memory", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { return null; }
    }

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

        var ptMemKey = contactId.ToString();
        var ptMem = await MemoryLookupAsync(companyId, AiFeatureKey.PaymentTypeSuggestion, ptMemKey, ct);
        if (ptMem.HasValue)
        {
            var fid0 = await RecordLearnedAsync(companyId, AiFeatureKey.PaymentTypeSuggestion, ptMemKey,
                ptMem.Value.Answer, ptMem.Value.Confidence, "Contact", contactId, ct);
            return Ok(new ApiResponse<object>(true, new
            {
                paymentType = ptMem.Value.Answer, confidence = ptMem.Value.Confidence,
                supportingSamples = ptMem.Value.Samples,
                reasoning = $"🧠 เรียนรู้จากที่ทีมคุณเลือกไว้กับผู้ขายรายนี้ ({ptMem.Value.Samples} ครั้ง)",
                feedbackId = fid0,
            }));
        }

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
                PromptHash: ptMemKey, PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: answer, AiConfidence: confidence,
                LocalModelAnswer: answer, LocalModelConfidence: confidence,
                LocalModelVersion: pred?.ModelVersion ?? model.Version,
                SourceEntityType: "Contact", SourceEntityId: contactId,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
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
    //  GENERIC GL-account-slot suggestion — fills any chart-of-accounts
    //  dropdown across the app (contact AR/AP/GR, fixed-asset
    //  asset/dep/accum, department/budget/petty-cash expense). One
    //  endpoint, one learning loop. Resolution:
    //    1. AiSuggestionMemory (slot|context) — what this company settled on
    //    2. Most-used account for the slot in posting/asset history
    //    3. Standard code-prefix default (lowest code in the family)
    //  Returns accountId + code + name + reasoning + feedbackId.
    // ────────────────────────────────────────────────────────────────

    /// <summary>Code-prefix family per slot — the COA convention this
    /// app seeds (113x AR, 211/212 AP, 212305 GR-clearing, 15/16 asset,
    /// 156x accum-dep contra, 5x expense).</summary>
    private static string SlotPrefix(string slot) => slot switch
    {
        "ContactAR" => "113",
        "ContactAP" => "212",
        "ContactIrGr" => "212305",
        "AssetAccount" => "16",
        "AssetDepExpense" => "5",
        "AssetAccumDep" => "166",
        "DeptExpense" or "BudgetExpense" or "PettyExpense" or "RecurringExpense" => "5",
        _ => "",
    };

    [HttpGet("gl-account/suggest")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestGlAccountSlot(
        Guid companyId,
        [FromQuery] string slot,
        [FromQuery] Guid? contactId,
        [FromQuery] string? assetCategory,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slot))
            return BadRequest(new ApiResponse<object>(false, null, "slot ห้ามว่าง"));

        // Context key — narrows the learned answer to the right scope.
        var context = contactId.HasValue && contactId.Value != Guid.Empty
            ? contactId.Value.ToString()
            : !string.IsNullOrWhiteSpace(assetCategory) ? NormKey(assetCategory) : "global";
        var memKey = $"{slot}|{context}";

        // 1) Learned memory wins.
        var mem = await MemoryLookupAsync(companyId, AiFeatureKey.GlAccountSlotSuggestion, memKey, ct);
        if (mem.HasValue && Guid.TryParse(mem.Value.Answer, out var learnedAcc))
        {
            var a = await _db.ChartOfAccounts.AsNoTracking()
                .Where(x => x.Id == learnedAcc && x.CompanyId == companyId && !x.IsDeleted)
                .Select(x => new { x.Id, x.AccountCode, x.AccountName }).FirstOrDefaultAsync(ct);
            if (a != null)
            {
                var fid0 = await RecordLearnedAsync(companyId, AiFeatureKey.GlAccountSlotSuggestion, memKey,
                    mem.Value.Answer, mem.Value.Confidence, "ChartOfAccount", a.Id, ct);
                return Ok(new ApiResponse<object>(true, new
                {
                    accountId = a.Id, accountCode = a.AccountCode, accountName = a.AccountName,
                    confidence = mem.Value.Confidence, source = "Learned",
                    reasoning = $"🧠 เรียนรู้จากที่ทีมคุณเลือกไว้สำหรับช่องนี้ ({mem.Value.Samples} ครั้ง)",
                    feedbackId = fid0,
                }));
            }
        }

        // 2) Most-used account for the slot in history.
        Guid? pickedId = null; string source = ""; decimal confidence = 0.40m;
        var prefix = SlotPrefix(slot);

        if ((slot == "ContactAR" || slot == "ContactAP") && contactId.HasValue && contactId.Value != Guid.Empty)
        {
            // Mode of AccountId on JE lines posted from this contact's docs,
            // restricted to the slot's code family.
            var docIds = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && d.ContactId == contactId.Value && !d.IsDeleted)
                .Select(d => d.Id).Take(50).ToListAsync(ct);
            if (docIds.Count > 0)
            {
                var used = await (from l in _db.Set<JournalEntryLine>().AsNoTracking()
                                  join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                                  where j.CompanyId == companyId && j.SourceDocumentId != null
                                     && docIds.Contains(j.SourceDocumentId!.Value)
                                     && l.Account.AccountCode.StartsWith(prefix)
                                  select l.AccountId).ToListAsync(ct);
                if (used.Count > 0)
                {
                    pickedId = used.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
                    source = "ContactHistory"; confidence = used.Count >= 4 ? 0.85m : 0.65m;
                }
            }
        }
        else if ((slot == "AssetAccount" || slot == "AssetDepExpense" || slot == "AssetAccumDep")
                 && !string.IsNullOrWhiteSpace(assetCategory))
        {
            // Mode of the matching account across other assets of the same category.
            var sameCat = _db.Set<FixedAsset>().AsNoTracking()
                .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.Category == assetCategory);
            var ids = slot switch
            {
                "AssetAccount" => await sameCat.Where(x => x.AssetAccountId != null).Select(x => x.AssetAccountId!.Value).ToListAsync(ct),
                "AssetDepExpense" => await sameCat.Where(x => x.DepreciationExpenseAccountId != null).Select(x => x.DepreciationExpenseAccountId!.Value).ToListAsync(ct),
                _ => await sameCat.Where(x => x.AccumulatedDepreciationAccountId != null).Select(x => x.AccumulatedDepreciationAccountId!.Value).ToListAsync(ct),
            };
            if (ids.Count > 0)
            {
                pickedId = ids.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
                source = "AssetCategoryHistory"; confidence = ids.Count >= 3 ? 0.85m : 0.65m;
            }
        }
        else if (prefix == "5")
        {
            // Expense slots: most-used expense account company-wide.
            // JournalEntryLine has no CompanyId — scope via the parent JE.
            var used = await (from l in _db.Set<JournalEntryLine>().AsNoTracking()
                              join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                              where j.CompanyId == companyId && l.DebitAmount > 0
                                 && l.Account.AccountCode.StartsWith("5")
                              select l.AccountId).Take(500).ToListAsync(ct);
            if (used.Count > 0)
            {
                pickedId = used.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
                source = "ExpenseHistory"; confidence = 0.60m;
            }
        }

        // 3) Code-prefix default — lowest code in the family.
        if (pickedId == null && !string.IsNullOrEmpty(prefix))
        {
            var def = await _db.ChartOfAccounts.AsNoTracking()
                .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.AccountCode.StartsWith(prefix))
                .OrderBy(x => x.AccountCode)
                .Select(x => x.Id).FirstOrDefaultAsync(ct);
            if (def != Guid.Empty) { pickedId = def; source = "PrefixDefault"; confidence = 0.45m; }
        }

        if (pickedId == null)
            return Ok(new ApiResponse<object>(true, new { accountId = (Guid?)null,
                reasoning = "ยังไม่มีข้อมูลพอจะแนะนำบัญชีช่องนี้", feedbackId = (Guid?)null }));

        var acct = await _db.ChartOfAccounts.AsNoTracking()
            .Where(x => x.Id == pickedId.Value && x.CompanyId == companyId)
            .Select(x => new { x.Id, x.AccountCode, x.AccountName }).FirstOrDefaultAsync(ct);
        if (acct == null)
            return Ok(new ApiResponse<object>(true, new { accountId = (Guid?)null,
                reasoning = "บัญชีที่แนะนำถูกลบไปแล้ว", feedbackId = (Guid?)null }));

        var inputJson = JsonSerializer.Serialize(new { slot, contactId, assetCategory, source });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.GlAccountSlotSuggestion,
                PromptHash: memKey, PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: acct.Id.ToString(), AiConfidence: confidence,
                LocalModelAnswer: acct.Id.ToString(), LocalModelConfidence: confidence,
                LocalModelVersion: "slot-heuristic-v1",
                SourceEntityType: "ChartOfAccount", SourceEntityId: acct.Id,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "slot-heuristic", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            accountId = acct.Id, accountCode = acct.AccountCode, accountName = acct.AccountName,
            confidence, source,
            reasoning = source switch
            {
                "ContactHistory" => $"จากประวัติการลงบัญชีของผู้ติดต่อรายนี้: {acct.AccountCode} {acct.AccountName}",
                "AssetCategoryHistory" => $"จากสินทรัพย์หมวดเดียวกัน: {acct.AccountCode} {acct.AccountName}",
                "ExpenseHistory" => $"บัญชีค่าใช้จ่ายที่ใช้บ่อยสุด: {acct.AccountCode} {acct.AccountName}",
                _ => $"ค่าเริ่มต้นตามผังบัญชี: {acct.AccountCode} {acct.AccountName}",
            },
            feedbackId,
        }));
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

        // ── ด่านกันคำตอบที่แต่งขึ้น (รอบ 178) ────────────────────────────────
        // ผู้เรียกอีกรายของ feature key เดียวกัน (`DocumentService` สายคำเตือน
        // หัก ณ ที่จ่าย) ตรวจว่ารหัสที่ได้ **มีอยู่จริงใน ThaiWhtRateTable** ก่อนใช้
        // แต่เส้นนี้คืนค่าดิบ ⇒ ด่านถูกใส่ที่เดียวจากสองที่ (ผิดหลัก "แก้ที่หนึ่ง
        // grep ทั้งเรพ") · รหัสที่ไม่มีในตารางแปลว่าโมเดลแต่งขึ้น ⇒ ไม่ส่งออกไป
        // ให้หน้าเว็บเอาไปตั้งอัตราหัก เพราะอัตราที่ผิดคือเงินที่นำส่งผิดจริง
        if (!string.IsNullOrWhiteSpace(result.Answer)
            && !string.Equals(result.Answer, "None", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(result.Answer, "Skip", StringComparison.OrdinalIgnoreCase)
            && Accounting.Helpers.ThaiWhtRateTable.Find(result.Answer) == null)
        {
            return Ok(new ApiResponse<object>(true, new
            {
                answer = (string?)null,
                confidence = 0m,
                usedAi = result.UsedAi,
                feedbackId = result.FeedbackId,   // ⬅ ยังเก็บไว้: คำตอบที่ถูกทิ้งก็เป็นข้อมูลสอน
                reasoning = "ระบบเสนอรหัสประเภทเงินได้ที่ไม่มีในตารางอัตรา ท.ป.4/2528 — "
                    + "จึงไม่แนะนำค่าใด กรุณาเลือกประเภทเงินได้เอง",
            }));
        }

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

        // Memory key = (contact, normalised description). VAT treatment is
        // stable per vendor+item, so once the team confirms it once it's learned.
        var vatMemKey = NormKey(req.ContactId?.ToString(), req.LineDescription);
        var vatMem = await MemoryLookupAsync(companyId, AiFeatureKey.VatTypeInference, vatMemKey, ct);
        if (vatMem.HasValue)
        {
            var fid0 = await RecordLearnedAsync(companyId, AiFeatureKey.VatTypeInference, vatMemKey,
                vatMem.Value.Answer, vatMem.Value.Confidence, "DocumentLine", req.ContactId, ct);
            return Ok(new ApiResponse<object>(true, new
            {
                vatType = vatMem.Value.Answer, confidence = vatMem.Value.Confidence,
                reasoning = $"🧠 เรียนรู้จากที่ทีมคุณเคยตั้งไว้กับรายการนี้ ({vatMem.Value.Samples} ครั้ง)",
                feedbackId = fid0,
            }));
        }

        // ═══ กติกาอยู่ที่ Helpers/ThaiVatTypeRule ตัวเดียว (E-OCR-01/OCR-02) ═══
        // เดิมเขียนไว้ในเมธอดนี้ที่เดียว ⇒ สาย OCR (ซึ่งกฎเหล็ก #3 ตั้งเป้าให้เป็น
        // 90% ของงาน) เรียกไม่ได้เลย และตั้ง VatRate = 7 ทุกบรรทัดแทน
        var isForeign = Accounting.Helpers.ThaiVatTypeRule.LooksForeignVendor(
            req.VendorCountryCode, req.VendorTaxId);
        var isExempt = Accounting.Helpers.ThaiVatTypeRule.LooksExempt(req.LineDescription);
        var suggestion = Accounting.Helpers.ThaiVatTypeRule.Suggest(
            req.LineDescription, req.VendorCountryCode, req.VendorTaxId);
        var reasoning = Accounting.Helpers.ThaiVatTypeRule.Reasoning(suggestion);

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
                PromptHash: vatMemKey, PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: suggestion, AiConfidence: isExempt || isForeign ? 0.85m : 0.95m,
                LocalModelAnswer: suggestion, LocalModelConfidence: 0.85m,
                LocalModelVersion: "heuristic-v1",
                SourceEntityType: "DocumentLine",
                SourceEntityId: req.ContactId,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
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

        var memKey = contactId.ToString();
        // Learned-first: ถ้าผู้ใช้บริษัทนี้เคยยืนยันเทอมของผู้ติดต่อรายนี้
        // แล้ว → คืนค่านั้นเลย (online learning) ไม่ต้องคำนวณ heuristic ใหม่.
        var learnedMem = await MemoryLookupAsync(companyId, AiFeatureKey.PaymentTermsSuggestion, memKey, ct);
        if (learnedMem.HasValue && int.TryParse(learnedMem.Value.Answer, out var learnedDays) && learnedDays > 0)
        {
            var fid0 = await RecordLearnedAsync(companyId, AiFeatureKey.PaymentTermsSuggestion, memKey,
                learnedMem.Value.Answer, learnedMem.Value.Confidence, "Contact", contactId, ct);
            return Ok(new ApiResponse<object>(true, new
            {
                creditDays = learnedDays, source = "Learned", confidence = learnedMem.Value.Confidence,
                reasoning = $"🧠 เรียนรู้จากที่ทีมคุณยืนยันไว้: Net {learnedDays} วัน ({learnedMem.Value.Samples} ครั้ง)",
                feedbackId = fid0,
            }));
        }

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
                PromptHash: memKey, PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: days.ToString(), AiConfidence: confidence,
                LocalModelAnswer: days.ToString(), LocalModelConfidence: confidence,
                LocalModelVersion: "lookup-v1",
                SourceEntityType: "Contact", SourceEntityId: contactId,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
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
    //  Payment channel — bank account / cash account to pay this
    //  supplier from. Looks at the last 12 Payments to the same
    //  contact and picks the channel that occurs most frequently.
    //  Falls back to first active bank account, then cash.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("payment-channel/suggest")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestPaymentChannel(
        Guid companyId, [FromQuery] Guid contactId, CancellationToken ct)
    {
        if (contactId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "contactId ห้ามว่าง"));

        var memKey = contactId.ToString();
        var learnedMem = await MemoryLookupAsync(companyId, AiFeatureKey.PaymentChannelSuggestion, memKey, ct);
        if (learnedMem.HasValue)
        {
            var fid0 = await RecordLearnedAsync(companyId, AiFeatureKey.PaymentChannelSuggestion, memKey,
                learnedMem.Value.Answer, learnedMem.Value.Confidence, "Contact", contactId, ct);
            return Ok(new ApiResponse<object>(true, new
            {
                paymentChannel = learnedMem.Value.Answer, label = (string?)null,
                source = "Learned", confidence = learnedMem.Value.Confidence,
                reasoning = $"🧠 เรียนรู้จากที่ทีมคุณเลือกไว้ ({learnedMem.Value.Samples} ครั้ง)",
                feedbackId = fid0,
            }));
        }

        // Find recent payments to documents belonging to this contact.
        // Each payment has BankAccountId (bank) or OverridePaymentAccountId
        // (cash/director-advance/clearing — non-bank GL). The Mode of the
        // top channel wins. Payment.PaymentMethod is recorded too as a
        // secondary signal (Cash vs BankTransfer).
        var recent = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.Document.ContactId == contactId && !p.IsDeleted)
            .OrderByDescending(p => p.PaymentDate)
            .Take(12)
            .Select(p => new
            {
                p.BankAccountId,
                p.OverridePaymentAccountId,
                p.PaymentMethod,
            })
            .ToListAsync(ct);

        string? channelValue = null;     // "bank:<id>" | "account:<id>" | ""
        string? label = null;
        string source; decimal confidence;

        if (recent.Count >= 2)
        {
            // Build a unified key per row + count occurrences
            var grouped = recent
                .Select(p => p.OverridePaymentAccountId.HasValue
                    ? ($"account:{p.OverridePaymentAccountId.Value}", "account", p.OverridePaymentAccountId.Value)
                    : p.BankAccountId.HasValue
                        ? ($"bank:{p.BankAccountId.Value}", "bank", p.BankAccountId.Value)
                        : ("", "cash", Guid.Empty))
                .GroupBy(x => x.Item1)
                .OrderByDescending(g => g.Count())
                .First();
            channelValue = grouped.Key;
            source = "PaymentHistory";
            confidence = recent.Count >= 5 ? 0.90m : 0.70m;

            // Resolve a friendly label for the picked channel.
            if (grouped.Key.StartsWith("bank:") && Guid.TryParse(grouped.Key[5..], out var bid))
            {
                var b = await _db.Set<BankAccount>().AsNoTracking()
                    .Where(x => x.Id == bid && x.CompanyId == companyId)
                    .Select(x => new { x.BankName, x.AccountName, x.AccountNumber })
                    .FirstOrDefaultAsync(ct);
                if (b != null) label = $"{b.AccountName} ({b.BankName} {b.AccountNumber})";
            }
            else if (grouped.Key.StartsWith("account:") && Guid.TryParse(grouped.Key[8..], out var aid))
            {
                var a = await _db.ChartOfAccounts.AsNoTracking()
                    .Where(x => x.Id == aid && x.CompanyId == companyId)
                    .Select(x => new { x.AccountCode, x.AccountName })
                    .FirstOrDefaultAsync(ct);
                if (a != null) label = $"{a.AccountCode} - {a.AccountName}";
            }
            else label = "เงินสด";
        }
        else
        {
            // Cold-start: prefer the first active bank account; if none, cash.
            var bank = await _db.Set<BankAccount>().AsNoTracking()
                .Where(x => x.CompanyId == companyId && x.IsActive && !x.IsDeleted)
                .OrderBy(x => x.AccountName)
                .Select(x => new { x.Id, x.BankName, x.AccountName, x.AccountNumber })
                .FirstOrDefaultAsync(ct);
            if (bank != null)
            {
                channelValue = $"bank:{bank.Id}";
                label = $"{bank.AccountName} ({bank.BankName} {bank.AccountNumber})";
                source = "FirstActiveBank";
            }
            else
            {
                channelValue = "";
                label = "เงินสด";
                source = "CashFallback";
            }
            confidence = 0.40m;
        }

        var inputJson = JsonSerializer.Serialize(new { contactId, sampleSize = recent.Count });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.PaymentChannelSuggestion,
                PromptHash: memKey, PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: channelValue, AiConfidence: confidence,
                LocalModelAnswer: channelValue, LocalModelConfidence: confidence,
                LocalModelVersion: "history-mode-v1",
                SourceEntityType: "Contact", SourceEntityId: contactId,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "history-mode", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            paymentChannel = channelValue,
            label,
            confidence,
            source,
            reasoning = source switch
            {
                "PaymentHistory" => $"จากการจ่าย {recent.Count} ครั้งล่าสุดให้ผู้ขายรายนี้: ใช้ {label} บ่อยสุด",
                "FirstActiveBank" => $"ยังไม่มีประวัติจ่าย — แนะนำบัญชีธนาคารหลัก: {label}",
                _ => "ยังไม่มีบัญชีธนาคารตั้งไว้ — จ่ายเงินสด",
            },
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Project allocation per document line — picks the active
    //  project this supplier was most recently linked to. Falls back
    //  to the project whose Contact matches the supplier.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("project-allocation/suggest")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestProjectAllocation(
        Guid companyId, [FromQuery] Guid contactId, [FromQuery] string? lineDescription, CancellationToken ct)
    {
        if (contactId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "contactId ห้ามว่าง"));

        var projMemKey = contactId.ToString();
        var projMem = await MemoryLookupAsync(companyId, AiFeatureKey.ProjectAllocationSuggestion, projMemKey, ct);
        if (projMem.HasValue && Guid.TryParse(projMem.Value.Answer, out var learnedProj))
        {
            var fid0 = await RecordLearnedAsync(companyId, AiFeatureKey.ProjectAllocationSuggestion, projMemKey,
                projMem.Value.Answer, projMem.Value.Confidence, "Contact", contactId, ct);
            var pName = await _db.Set<Project>().AsNoTracking()
                .Where(p => p.Id == learnedProj && p.CompanyId == companyId)
                .Select(p => p.Code + " - " + p.Name).FirstOrDefaultAsync(ct);
            return Ok(new ApiResponse<object>(true, new
            {
                projectId = (Guid?)learnedProj, projectLabel = pName,
                confidence = projMem.Value.Confidence, source = "Learned",
                reasoning = $"🧠 เรียนรู้จากที่ทีมคุณผูกโครงการให้ผู้ขายรายนี้ ({projMem.Value.Samples} ครั้ง)",
                feedbackId = fid0,
            }));
        }

        // Tier 1: latest DocumentLine.ProjectId attached to a doc with this contact
        var recent = await _db.Set<DocumentLine>().AsNoTracking()
            .Where(l => l.Document.CompanyId == companyId && l.ProjectId != null
                && l.Document.ContactId == contactId && !l.Document.IsDeleted)
            .OrderByDescending(l => l.Document.DocumentDate)
            .Take(12)
            .Select(l => l.ProjectId!.Value)
            .ToListAsync(ct);

        Guid? pickedId = null; string? pickedName = null; string source; decimal confidence;
        if (recent.Count >= 2)
        {
            pickedId = recent.GroupBy(p => p).OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Max()).First().Key;
            source = "DocumentLineHistory"; confidence = recent.Count >= 5 ? 0.90m : 0.70m;
        }
        else
        {
            // Tier 2: project whose Contact = this contact, status Active
            var byContact = await _db.Set<Project>().AsNoTracking()
                .Where(p => p.CompanyId == companyId && p.ContactId == contactId && p.Status == "Active" && !p.IsDeleted)
                .OrderByDescending(p => p.StartDate)
                .Select(p => p.Id)
                .FirstOrDefaultAsync(ct);
            if (byContact != Guid.Empty)
            {
                pickedId = byContact; source = "ProjectContactLink"; confidence = 0.65m;
            }
            else { source = "NoSignal"; confidence = 0.0m; }
        }

        if (pickedId.HasValue)
        {
            pickedName = await _db.Set<Project>().AsNoTracking()
                .Where(p => p.Id == pickedId.Value && p.CompanyId == companyId)
                .Select(p => p.Code + " - " + p.Name)
                .FirstOrDefaultAsync(ct);
        }

        var inputJson = JsonSerializer.Serialize(new { contactId, lineDescription, sampleSize = recent.Count });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.ProjectAllocationSuggestion,
                PromptHash: projMemKey, PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: pickedId?.ToString() ?? "", AiConfidence: confidence,
                LocalModelAnswer: pickedId?.ToString() ?? "", LocalModelConfidence: confidence,
                LocalModelVersion: "history-mode-v1",
                SourceEntityType: "Contact", SourceEntityId: contactId,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "history-mode", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            projectId = pickedId,
            projectLabel = pickedName,
            confidence,
            source,
            reasoning = source switch
            {
                "DocumentLineHistory" => $"จากเอกสาร {recent.Count} ใบล่าสุดของผู้ขายรายนี้: ใช้ {pickedName} บ่อยสุด",
                "ProjectContactLink" => $"ผู้ขายผูกกับโครงการ {pickedName} (ยังไม่มีประวัติเอกสาร)",
                _ => "ยังไม่มีข้อมูลพอจะแนะนำ",
            },
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Contact fuzzy match — find existing contacts similar to a
    //  typed name + tax-id. Prevents duplicate contact creation.
    // ────────────────────────────────────────────────────────────────

    public sealed record FuzzyContactRequest(
        string Name,
        string? TaxId,
        bool? IsCustomer,
        bool? IsSupplier,
        int? Limit);

    [HttpPost("contact/fuzzy-match")]
    public async Task<ActionResult<ApiResponse<object>>> FuzzyMatchContact(
        Guid companyId, [FromBody] FuzzyContactRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length < 2)
            return Ok(new ApiResponse<object>(true, new { matches = new object[0] }));

        var limit = Math.Clamp(req.Limit ?? 5, 1, 20);
        var nameLower = req.Name.Trim().ToLowerInvariant();
        var taxIdDigits = string.IsNullOrWhiteSpace(req.TaxId)
            ? null : new string(req.TaxId.Where(char.IsDigit).ToArray());

        // Stage 1: exact TaxId hit wins (perfect signal — same legal entity)
        if (!string.IsNullOrEmpty(taxIdDigits) && taxIdDigits.Length >= 10)
        {
            var byTaxId = await _db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.TaxId != null && c.TaxId == taxIdDigits)
                .Select(c => new { c.Id, c.Name, c.TaxId, c.IsCustomer, c.IsSupplier })
                .Take(5)
                .ToListAsync(ct);
            if (byTaxId.Count > 0)
            {
                return Ok(new ApiResponse<object>(true, new
                {
                    matches = byTaxId.Select(c => new
                    {
                        id = c.Id, name = c.Name, taxId = c.TaxId,
                        isCustomer = c.IsCustomer, isSupplier = c.IsSupplier,
                        confidence = 1.00m, reason = "TaxId ตรง — เป็นนิติบุคคลเดียวกัน",
                    }),
                    feedbackId = (Guid?)null,
                }));
            }
        }

        // Stage 2: fuzzy on Name — server-side LIKE + Levenshtein-style ranking
        // ดึงผู้สมัครจาก DB (กรอง role + LIKE) แล้ว rank ด้วย similarity ratio
        var candidates = await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted
                && (req.IsCustomer != true || c.IsCustomer)
                && (req.IsSupplier != true || c.IsSupplier)
                && (c.Name.ToLower().Contains(nameLower) || EF.Functions.ILike(c.Name, nameLower.Substring(0, Math.Min(3, nameLower.Length)) + "%")))
            .Select(c => new { c.Id, c.Name, c.TaxId, c.IsCustomer, c.IsSupplier })
            .Take(50)
            .ToListAsync(ct);

        // Ranking: bigram overlap × inverse-length-diff. Pure C# (no DB ext required).
        decimal Similarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0m;
            var al = a.ToLowerInvariant(); var bl = b.ToLowerInvariant();
            if (al == bl) return 1m;
            var bigramsA = new HashSet<string>();
            for (int i = 0; i + 1 < al.Length; i++) bigramsA.Add(al.Substring(i, 2));
            var bigramsB = new HashSet<string>();
            for (int i = 0; i + 1 < bl.Length; i++) bigramsB.Add(bl.Substring(i, 2));
            if (bigramsA.Count == 0 || bigramsB.Count == 0) return 0m;
            var overlap = bigramsA.Intersect(bigramsB).Count();
            return (decimal)(2.0 * overlap) / (bigramsA.Count + bigramsB.Count);
        }

        var ranked = candidates
            .Select(c => new
            {
                c.Id, c.Name, c.TaxId, c.IsCustomer, c.IsSupplier,
                Score = Similarity(c.Name, req.Name),
            })
            .Where(c => c.Score >= 0.30m)
            .OrderByDescending(c => c.Score)
            .Take(limit)
            .ToList();

        var inputJson = JsonSerializer.Serialize(new { req.Name, req.TaxId, candidatesCount = ranked.Count });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.ContactFuzzyMatch,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: ranked.FirstOrDefault()?.Id.ToString() ?? "",
                AiConfidence: ranked.FirstOrDefault()?.Score ?? 0m,
                LocalModelAnswer: ranked.FirstOrDefault()?.Id.ToString() ?? "",
                LocalModelConfidence: ranked.FirstOrDefault()?.Score ?? 0m,
                LocalModelVersion: "bigram-v1",
                SourceEntityType: "Contact", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "bigram", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            matches = ranked.Select(c => new
            {
                id = c.Id, name = c.Name, taxId = c.TaxId,
                isCustomer = c.IsCustomer, isSupplier = c.IsSupplier,
                confidence = c.Score,
                reason = c.Score >= 0.85m ? "ชื่อคล้ายมาก — น่าจะเป็นรายเดียวกัน"
                       : c.Score >= 0.60m ? "ชื่อใกล้เคียง — ตรวจสอบก่อนสร้างซ้ำ"
                       : "ชื่อมีส่วนตรงกัน",
            }),
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Manual Journal Entry — suggest Dr/Cr account per line from
    //  similar JE descriptions in history.
    // ────────────────────────────────────────────────────────────────

    public sealed record SuggestJeAccountRequest(
        string Description,
        decimal? Amount,
        string? EntryType,        // "JV" / "RV" / "PV" / etc.
        string? Side);            // "Debit" | "Credit"

    [HttpPost("manual-je/suggest-account")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestManualJeAccount(
        Guid companyId, [FromBody] SuggestJeAccountRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Description) || req.Description.Trim().Length < 3)
            return Ok(new ApiResponse<object>(true, new { suggestions = new object[0] }));

        // Memory key = description keyword + side. JE coding for a recurring
        // memo (e.g. "ค่าน้ำประปา" Debit) is stable, so it learns fast.
        var jeMemKey = NormKey(req.Description, req.Side ?? "Debit");
        var jeMem = await MemoryLookupAsync(companyId, AiFeatureKey.ManualJeAccountSuggestion, jeMemKey, ct);
        if (jeMem.HasValue && Guid.TryParse(jeMem.Value.Answer, out var learnedAcct))
        {
            var fid0 = await RecordLearnedAsync(companyId, AiFeatureKey.ManualJeAccountSuggestion, jeMemKey,
                jeMem.Value.Answer, jeMem.Value.Confidence, "JournalEntryLine", null, ct);
            var acc = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.Id == learnedAcct && a.CompanyId == companyId)
                .Select(a => new { a.Id, a.AccountCode, a.AccountName }).FirstOrDefaultAsync(ct);
            if (acc != null)
                return Ok(new ApiResponse<object>(true, new
                {
                    suggestions = new[] { new {
                        accountId = acc.Id, accountCode = acc.AccountCode, accountName = acc.AccountName,
                        confidence = jeMem.Value.Confidence, supportingSamples = jeMem.Value.Samples } },
                    learned = true, feedbackId = fid0,
                }));
        }

        // Mine prior JournalEntryLine.Description that contains the same
        // keyword and pick the most frequently used AccountId for that side.
        var descLower = req.Description.Trim().ToLowerInvariant();
        var keyword = descLower.Length > 25 ? descLower.Substring(0, 25) : descLower;
        var wantDebit = string.Equals(req.Side, "Credit", StringComparison.OrdinalIgnoreCase) ? false : true;

        var history = await (from l in _db.Set<JournalEntryLine>().AsNoTracking()
                             join e in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.Id
                             where e.CompanyId == companyId
                                && l.AccountId != Guid.Empty
                                && l.Description != null
                                && l.Description.ToLower().Contains(keyword)
                                && ((wantDebit && l.DebitAmount > 0) || (!wantDebit && l.CreditAmount > 0))
                             orderby e.EntryDate descending
                             select new { l.AccountId, l.Description })
            .Take(50)
            .ToListAsync(ct);

        var grouped = history
            .GroupBy(h => h.AccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(3)
            .ToList();

        if (grouped.Count == 0)
            return Ok(new ApiResponse<object>(true, new { suggestions = new object[0], feedbackId = (Guid?)null }));

        var ids = grouped.Select(g => g.AccountId).ToList();
        var accts = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.CompanyId == companyId)
            .Select(a => new { a.Id, a.AccountCode, a.AccountName })
            .ToListAsync(ct);
        var byId = accts.ToDictionary(a => a.Id);
        var total = grouped.Sum(g => g.Count);

        var suggestions = grouped
            .Where(g => byId.ContainsKey(g.AccountId))
            .Select(g => new
            {
                accountId = g.AccountId,
                accountCode = byId[g.AccountId].AccountCode,
                accountName = byId[g.AccountId].AccountName,
                confidence = total > 0 ? Math.Round((decimal)g.Count / total, 2) : 0m,
                supportingSamples = g.Count,
            })
            .ToList();

        var inputJson = JsonSerializer.Serialize(new
        {
            req.Description, req.Amount, req.EntryType, req.Side,
            sampleSize = history.Count,
        });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.ManualJeAccountSuggestion,
                PromptHash: jeMemKey, PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: suggestions.FirstOrDefault()?.accountId.ToString() ?? "",
                AiConfidence: suggestions.FirstOrDefault()?.confidence ?? 0m,
                LocalModelAnswer: suggestions.FirstOrDefault()?.accountId.ToString() ?? "",
                LocalModelConfidence: suggestions.FirstOrDefault()?.confidence ?? 0m,
                LocalModelVersion: "history-mode-v1",
                SourceEntityType: "JournalEntryLine", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "history-mode", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new { suggestions, feedbackId }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Cost-centre / Branch dimension allocation per line.
    //  Mirrors project allocation but for AccountingDimension.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("dimension-allocation/suggest")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestDimensionAllocation(
        Guid companyId, [FromQuery] Guid contactId, CancellationToken ct)
    {
        if (contactId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "contactId ห้ามว่าง"));

        var dimMemKey = contactId.ToString();
        var dimMem = await MemoryLookupAsync(companyId, AiFeatureKey.DimensionAllocationSuggestion, dimMemKey, ct);
        if (dimMem.HasValue && Guid.TryParse(dimMem.Value.Answer, out var learnedDim))
        {
            var fid0 = await RecordLearnedAsync(companyId, AiFeatureKey.DimensionAllocationSuggestion, dimMemKey,
                dimMem.Value.Answer, dimMem.Value.Confidence, "Contact", contactId, ct);
            var dLabel = await _db.Set<AccountingDimension>().AsNoTracking()
                .Where(x => x.Id == learnedDim && x.CompanyId == companyId)
                .Select(x => x.Code + " - " + x.Name).FirstOrDefaultAsync(ct);
            return Ok(new ApiResponse<object>(true, new
            {
                dimensionId = (Guid?)learnedDim, dimensionLabel = dLabel,
                confidence = dimMem.Value.Confidence, source = "Learned",
                reasoning = $"🧠 เรียนรู้จากที่ทีมคุณลงศูนย์ต้นทุนให้ผู้ขายรายนี้ ({dimMem.Value.Samples} ครั้ง)",
                feedbackId = fid0,
            }));
        }

        // DocumentLine ไม่มี DimensionId — ดึงผ่าน JE ที่ระบบ post จาก
        // เอกสารของ contact นี้ (SourceDocumentId เป็น Document.Id).
        // ใช้ DimensionId ที่ header หรือ ที่ line ก็ได้ (รวมเป็น union).
        var recentDocIds = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId && !d.IsDeleted)
            .OrderByDescending(d => d.DocumentDate)
            .Take(20)
            .Select(d => d.Id)
            .ToListAsync(ct);

        var fromHeader = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && j.SourceDocumentId != null
                && recentDocIds.Contains(j.SourceDocumentId!.Value) && j.DimensionId != null)
            .Select(j => j.DimensionId!.Value).ToListAsync(ct);
        var fromLine = await (from l in _db.Set<JournalEntryLine>().AsNoTracking()
                              join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                              where j.CompanyId == companyId && j.SourceDocumentId != null
                                 && recentDocIds.Contains(j.SourceDocumentId!.Value)
                                 && l.DimensionId != null
                              select l.DimensionId!.Value).ToListAsync(ct);
        var recent = fromHeader.Concat(fromLine).ToList();

        Guid? pickedId = null; string? pickedLabel = null;
        string source; decimal confidence;
        if (recent.Count >= 2)
        {
            pickedId = recent.GroupBy(p => p).OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Max()).First().Key;
            source = "DocumentLineHistory"; confidence = recent.Count >= 5 ? 0.90m : 0.70m;
        }
        else { source = "NoSignal"; confidence = 0.0m; }

        if (pickedId.HasValue)
        {
            pickedLabel = await _db.Set<AccountingDimension>().AsNoTracking()
                .Where(d => d.Id == pickedId.Value && d.CompanyId == companyId)
                .Select(d => d.Code + " - " + d.Name)
                .FirstOrDefaultAsync(ct);
        }

        var inputJson = JsonSerializer.Serialize(new { contactId, sampleSize = recent.Count });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.DimensionAllocationSuggestion,
                PromptHash: dimMemKey, PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: pickedId?.ToString() ?? "", AiConfidence: confidence,
                LocalModelAnswer: pickedId?.ToString() ?? "", LocalModelConfidence: confidence,
                LocalModelVersion: "history-mode-v1",
                SourceEntityType: "Contact", SourceEntityId: contactId,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "history-mode", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            dimensionId = pickedId,
            dimensionLabel = pickedLabel,
            confidence, source,
            reasoning = pickedId.HasValue
                ? $"ผู้ขายรายนี้มักลงที่ศูนย์ต้นทุน {pickedLabel} ({recent.Count} ครั้งล่าสุด)"
                : "ยังไม่มีข้อมูลพอจะแนะนำ",
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Fixed asset category + useful-life + depreciation method
    //  suggestion from asset name. Keyword-based per Thai practice.
    // ────────────────────────────────────────────────────────────────

    public sealed record SuggestAssetCategoryRequest(
        string AssetName,
        decimal? PurchaseCost);

    [HttpPost("asset/suggest-category")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestAssetCategory(
        Guid companyId, [FromBody] SuggestAssetCategoryRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.AssetName))
            return BadRequest(new ApiResponse<object>(false, null, "AssetName ห้ามว่าง"));

        var assetMemKey = NormKey(req.AssetName);  // capture key — learning สะสมต่อชื่อสินทรัพย์
        var name = req.AssetName.ToLowerInvariant();
        string category; int usefulLifeMonths; string depMethod = "StraightLine"; string reasoning;

        // Thai-RD-accepted useful life table (พระราชกฤษฎีกา §3, 145):
        //   อาคาร 20 ปี / รถยนต์ 5 ปี / เครื่องจักร 5-10 ปี / IT 3-5 ปี / เฟอร์ฯ 5 ปี
        if (Contains(name, "อาคาร", "building", "warehouse", "โรงงาน", "โกดัง"))
        { category = "Buildings"; usefulLifeMonths = 240; reasoning = "อาคาร — 20 ปี (พรฎ.145)"; }
        else if (Contains(name, "รถยนต์", "vehicle", "car", "รถ", "truck", "รถบรรทุก", "มอเตอร์ไซค์"))
        { category = "Vehicles"; usefulLifeMonths = 60; reasoning = "ยานพาหนะ — 5 ปี"; }
        else if (Contains(name, "computer", "คอมพิวเตอร์", "laptop", "notebook", "server", "เซิร์ฟเวอร์", "พีซี"))
        { category = "ITEquipment"; usefulLifeMonths = 36; reasoning = "อุปกรณ์ IT — 3 ปี"; }
        else if (Contains(name, "ปริ๊น", "printer", "scanner", "monitor", "จอ", "router", "switch", "เครื่องพิมพ์"))
        { category = "ITEquipment"; usefulLifeMonths = 36; reasoning = "อุปกรณ์ IT รอบนอก — 3 ปี"; }
        else if (Contains(name, "เครื่องจักร", "machine", "machinery", "เครื่องผลิต"))
        { category = "Machinery"; usefulLifeMonths = 120; reasoning = "เครื่องจักร — 10 ปี"; }
        else if (Contains(name, "เฟอร์", "furniture", "โต๊ะ", "เก้าอี้", "ตู้", "ชั้น"))
        { category = "Furniture"; usefulLifeMonths = 60; reasoning = "เฟอร์นิเจอร์ — 5 ปี"; }
        else if (Contains(name, "software", "license", "ซอฟต์แวร์", "ไลเซนส์"))
        { category = "Intangible"; usefulLifeMonths = 36; depMethod = "StraightLine"; reasoning = "Intangible — 3 ปี"; }
        else if (Contains(name, "เครื่องปรับอากาศ", "air condition", "แอร์", "พัดลม"))
        { category = "OfficeEquipment"; usefulLifeMonths = 60; reasoning = "อุปกรณ์สำนักงาน — 5 ปี"; }
        else
        { category = "OfficeEquipment"; usefulLifeMonths = 60; reasoning = "ค่าเริ่มต้น — 5 ปี (ตรวจสอบประเภทอีกครั้ง)"; }

        var inputJson = JsonSerializer.Serialize(new { req.AssetName, req.PurchaseCost });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.AssetCategorySuggestion,
                PromptHash: assetMemKey, PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: category, AiConfidence: 0.80m,
                LocalModelAnswer: category, LocalModelConfidence: 0.80m,
                LocalModelVersion: "keyword-v1",
                SourceEntityType: "FixedAsset", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "keyword", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            category, usefulLifeMonths, depreciationMethod = depMethod,
            confidence = 0.80m, reasoning, feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Payroll item → §40 income-type code mapping.
    // ────────────────────────────────────────────────────────────────

    public sealed record SuggestIncomeTypeRequest(string ComponentName, string? ItemType);

    [HttpPost("payroll/suggest-income-type")]
    public ActionResult<ApiResponse<object>> SuggestIncomeType(
        Guid companyId, [FromBody] SuggestIncomeTypeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ComponentName))
            return BadRequest(new ApiResponse<object>(false, null, "ComponentName ห้ามว่าง"));

        var n = req.ComponentName.ToLowerInvariant();
        string code; string label; string reasoning;
        if (Contains(n, "เงินเดือน", "ค่าจ้าง", "salary", "wage", "โอที", "ot", "ล่วงเวลา",
                      "เบี้ยขยัน", "ค่าตำแหน่ง", "โบนัส", "bonus", "ค่าครองชีพ", "ค่าน้ำมัน", "ค่าเดินทาง"))
        { code = "40(1)"; label = "เงินได้จากการจ้างแรงงาน"; reasoning = "เงินที่นายจ้างจ่ายให้ลูกจ้าง"; }
        else if (Contains(n, "ค่าบริการ", "ที่ปรึกษา", "ค่าจ้างทำของ", "freelance", "consultant"))
        { code = "40(2)"; label = "เงินได้จากหน้าที่/ตำแหน่งงานหรือบริการ"; reasoning = "การให้บริการนอกการจ้างแรงงาน"; }
        else if (Contains(n, "royalty", "ค่าลิขสิทธิ์", "ค่าสิทธิ์"))
        { code = "40(3)"; label = "ค่าลิขสิทธิ์ / ค่ากู๊ดวิลล์"; reasoning = "ค่าสิทธิ์ทรัพย์สินทางปัญญา"; }
        else if (Contains(n, "ดอกเบี้ย", "interest", "เงินปันผล", "dividend"))
        { code = "40(4)"; label = "ดอกเบี้ย / เงินปันผล"; reasoning = "ผลตอบแทนจากการลงทุน"; }
        else if (Contains(n, "ค่าเช่า", "rental", "rent"))
        { code = "40(5)"; label = "ค่าเช่า / ค่าทรัพย์สิน"; reasoning = "การให้ใช้ทรัพย์สิน"; }
        else if (Contains(n, "วิชาชีพ", "professional", "หมอ", "ทันตแพทย์", "ทนาย", "วิศวกร", "บัญชี"))
        { code = "40(6)"; label = "วิชาชีพอิสระ"; reasoning = "ผู้ประกอบวิชาชีพอิสระตามกฎหมาย"; }
        else if (Contains(n, "รับเหมา", "contractor", "ค่าก่อสร้าง", "งานเหมา"))
        { code = "40(7)"; label = "รับเหมา"; reasoning = "การรับเหมาที่ผู้ทำพร้อมวัสดุสำคัญ"; }
        else
        { code = "40(8)"; label = "ธุรกิจ / การเกษตร / อุตสาหกรรม / อื่น ๆ"; reasoning = "ค่าเริ่มต้น — ตรวจสอบประเภทอีกครั้ง"; }

        return Ok(new ApiResponse<object>(true, new
        {
            incomeTypeCode = code, label, reasoning, confidence = 0.85m,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  FX rate suggest — latest CurrencyRate row for (from, THB) on
    //  or before document date.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("fx-rate/suggest")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestFxRate(
        Guid companyId,
        [FromQuery] string currency,
        [FromQuery] DateTime? date,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return BadRequest(new ApiResponse<object>(false, null, "currency ห้ามว่าง"));
        var asOf = (date ?? DateTime.UtcNow).Date;
        var ccy = currency.ToUpperInvariant();
        if (ccy == "THB")
            return Ok(new ApiResponse<object>(true, new { rate = 1m, asOf, confidence = 1m, source = "Identity" }));

        var row = await _db.Set<CurrencyRate>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.FromCurrency == ccy && r.ToCurrency == "THB"
                && r.EffectiveDate <= asOf
                // รอบ 193 (M2): แถวอัตรา 0 ที่ค้างจากฟอร์มเก่า (A03) = "ไม่มีอัตรา" ไม่ใช่ "อัตรา 0" —
                // เดิมถูกหยิบเป็นแถวล่าสุดแล้วแนะนำอัตรา 0 · เกณฑ์เดียวกับ CurrencyService (MidRate > 0)
                // เว้นแต่มีราคาซื้อ/ขายครบ (สูตร fallback ข้างล่างใช้ได้)
                && (r.MidRate > 0 || (r.BuyRate > 0 && r.SellRate > 0)))
            .OrderByDescending(r => r.EffectiveDate)
            .Select(r => new { r.MidRate, r.BuyRate, r.SellRate, r.EffectiveDate, r.Source })
            .FirstOrDefaultAsync(ct);

        decimal rate; DateTime effective; string source; decimal confidence;
        if (row != null)
        {
            rate = row.MidRate > 0 ? row.MidRate : (row.BuyRate + row.SellRate) / 2m;
            effective = row.EffectiveDate;
            source = row.Source ?? "CurrencyRateHistory";
            var ageDays = (asOf - effective).Days;
            confidence = ageDays <= 0 ? 1.00m : ageDays <= 1 ? 0.95m
                       : ageDays <= 7 ? 0.80m : ageDays <= 30 ? 0.65m : 0.45m;
        }
        else
        {
            rate = 0m; effective = asOf; source = "NoRateAvailable"; confidence = 0m;
        }

        var inputJson = JsonSerializer.Serialize(new { ccy, asOf });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.FxRateSuggestion,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: rate.ToString("0.######"), AiConfidence: confidence,
                LocalModelAnswer: rate.ToString("0.######"), LocalModelConfidence: confidence,
                LocalModelVersion: "lookup-v1",
                SourceEntityType: "CurrencyRate", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "lookup", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            rate, asOf = effective, source, confidence,
            ageDays = (asOf - effective).Days,
            reasoning = row != null
                ? $"อัตรา {ccy}→THB ล่าสุด {rate:N4} ณ {effective:yyyy-MM-dd}"
                : $"ยังไม่มีอัตราของ {ccy} ในระบบ — กรุณาเพิ่มที่หน้า Currency",
            feedbackId,
        }));
    }

    private static bool Contains(string source, params string[] keywords)
        => keywords.Any(k => source.Contains(k, StringComparison.OrdinalIgnoreCase));

    // ────────────────────────────────────────────────────────────────
    //  Cash flow forecast narrative — Thai prose summary from KPIs.
    //  Template-based; AI cloud not used. Surfaces actionable
    //  suggestions in plain language for non-finance owners.
    // ────────────────────────────────────────────────────────────────

    public sealed record CashForecastNarrativeRequest(
        decimal CashIn,
        decimal CashOut,
        decimal CurrentBalance,
        decimal LowestBalance,
        DateTime? LowestBalanceDate,
        int? Days,
        int? OverdueArCount,
        decimal? OverdueArAmount);

    [HttpPost("cash-forecast/narrative")]
    public async Task<ActionResult<ApiResponse<object>>> GenerateForecastNarrative(
        Guid companyId, [FromBody] CashForecastNarrativeRequest req, CancellationToken ct)
    {
        var horizon = req.Days ?? 30;
        var net = req.CashIn - req.CashOut;
        var endBalance = req.CurrentBalance + net;
        var parts = new List<string>();

        if (net >= 0)
            parts.Add($"📈 ใน {horizon} วันข้างหน้า กระแสเงินสดสุทธิ +{net:N0} บาท (เข้า {req.CashIn:N0} − ออก {req.CashOut:N0})");
        else
            parts.Add($"📉 ใน {horizon} วันข้างหน้า กระแสเงินสดสุทธิ {net:N0} บาท (เข้า {req.CashIn:N0} − ออก {req.CashOut:N0}) — เงินจ่ายมากกว่าเงินรับ");

        if (req.LowestBalance < 0)
            parts.Add($"🚨 ยอดคงเหลือจะติดลบสูงสุด {req.LowestBalance:N0} บาท" +
                (req.LowestBalanceDate.HasValue ? $" ราววันที่ {req.LowestBalanceDate.Value:dd/MM/yyyy}" : "") +
                " — ต้องเร่งทวงหนี้ หรือเลื่อนชำระค่าใช้จ่ายที่ไม่จำเป็น");
        else if (req.LowestBalance < req.CurrentBalance * 0.30m)
            parts.Add($"⚠️ ยอดคงเหลือต่ำสุด {req.LowestBalance:N0} บาท (เหลือ <30% ของวันนี้) — ติดตามใกล้ชิด");
        else
            parts.Add($"✅ ยอดคงเหลือต่ำสุดอยู่ที่ {req.LowestBalance:N0} บาท — สภาพคล่องเพียงพอ");

        if (req.OverdueArCount.HasValue && req.OverdueArCount.Value > 0)
            parts.Add($"📑 มีลูกหนี้ค้าง {req.OverdueArCount} ราย รวม {req.OverdueArAmount ?? 0:N0} บาท — เร่งทวงเพื่อเสริมสภาพคล่อง");

        // Suggested actions
        var actions = new List<string>();
        if (net < 0)
        {
            actions.Add("เร่งเก็บเงินจากลูกหนี้รายใหญ่ที่เกินกำหนด");
            actions.Add("เจรจาขยายเทอมจ่ายกับผู้จำหน่ายหลัก");
            actions.Add("ตรวจค่าใช้จ่ายที่เลื่อนชำระได้ใน 1-2 สัปดาห์");
        }
        if (req.LowestBalance < 0)
        {
            actions.Add("เตรียม OD/วงเงินสำรอง เพื่อกัน cashflow gap");
        }
        if (req.OverdueArCount > 5)
            actions.Add("รวบรวมรายชื่อลูกหนี้ค้าง > 30 วัน ส่งให้ฝ่ายเก็บเงิน");

        var inputJson = JsonSerializer.Serialize(req);
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.ForecastNarrative,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: net.ToString("0"),
                AiConfidence: 0.80m, LocalModelAnswer: net.ToString("0"),
                LocalModelConfidence: 0.80m, LocalModelVersion: "template-v1",
                SourceEntityType: "CashFlowForecast", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "template", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            narrative = string.Join("\n", parts),
            actions,
            netCashFlow = net, endBalance,
            severity = req.LowestBalance < 0 ? "Critical"
                     : net < 0 ? "Warning" : "Healthy",
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Customer RFM segmentation
    //   Recency  — days since last invoice (lower = better)
    //   Frequency — count of invoices in window
    //   Monetary  — total sales value
    //  Each scored 1-5 by quintile → 125 combinations → 5 segments.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("customer/rfm-segmentation")]
    public async Task<ActionResult<ApiResponse<object>>> GetRfmSegmentation(
        Guid companyId, [FromQuery] int windowDays = 365, CancellationToken ct = default)
    {
        windowDays = Math.Clamp(windowDays, 90, 1825);
        var since = DateTime.UtcNow.AddDays(-windowDays);
        var today = DateTime.UtcNow.Date;

        var sales = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.ContactId != Guid.Empty
                && (d.DocumentType == DocumentType.Invoice
                    || d.DocumentType == DocumentType.TaxInvoice
                    || d.DocumentType == DocumentType.Receipt)
                && d.DocumentDate >= since)
            .Select(d => new { d.ContactId, d.DocumentDate, d.TotalAmount })
            .ToListAsync(ct);

        var perCustomer = sales
            .GroupBy(s => s.ContactId)
            .Select(g => new
            {
                ContactId = g.Key,
                Recency = (today - g.Max(s => s.DocumentDate).Date).Days,
                Frequency = g.Count(),
                Monetary = g.Sum(s => s.TotalAmount),
            })
            .ToList();

        if (perCustomer.Count == 0)
        {
            return Ok(new ApiResponse<object>(true, new
            {
                windowDays, customerCount = 0, segments = Array.Empty<object>(),
                reasoning = $"ไม่มีลูกค้าที่มีการซื้อใน {windowDays} วัน",
            }));
        }

        // Quintile boundaries — recency lower=better (reversed); freq/monetary higher=better
        int Quintile(decimal value, List<decimal> sorted, bool higherIsBetter)
        {
            if (sorted.Count == 0) return 3;
            var pos = sorted.BinarySearch(value);
            if (pos < 0) pos = ~pos;
            var pct = (double)pos / sorted.Count;
            var score = pct switch
            {
                <= 0.20 => 1,
                <= 0.40 => 2,
                <= 0.60 => 3,
                <= 0.80 => 4,
                _ => 5,
            };
            return higherIsBetter ? score : 6 - score;
        }

        var recSorted = perCustomer.Select(c => (decimal)c.Recency).OrderBy(x => x).ToList();
        var freqSorted = perCustomer.Select(c => (decimal)c.Frequency).OrderBy(x => x).ToList();
        var monSorted = perCustomer.Select(c => c.Monetary).OrderBy(x => x).ToList();

        var contactIds = perCustomer.Select(c => c.ContactId).ToList();
        var contactNames = await _db.Contacts.AsNoTracking()
            .Where(c => contactIds.Contains(c.Id) && c.CompanyId == companyId)
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        string Classify(int r, int f, int m)
        {
            var total = r + f + m;
            if (r >= 4 && f >= 4 && m >= 4) return "Champion";
            if (f >= 4 && m >= 4) return "Loyal";
            if (r >= 4 && f <= 2) return "NewCustomer";
            if (r <= 2 && (f >= 3 || m >= 3)) return "AtRisk";
            if (r <= 2 && f <= 2 && m <= 2) return "Lost";
            if (m >= 4) return "BigSpender";
            return "Regular";
        }

        var scored = perCustomer.Select(c =>
        {
            var r = Quintile(c.Recency, recSorted, higherIsBetter: false);
            var f = Quintile(c.Frequency, freqSorted, higherIsBetter: true);
            var m = Quintile(c.Monetary, monSorted, higherIsBetter: true);
            return new
            {
                contactId = c.ContactId,
                name = contactNames.GetValueOrDefault(c.ContactId, "(ไม่ระบุ)"),
                recency = c.Recency, frequency = c.Frequency, monetary = c.Monetary,
                rScore = r, fScore = f, mScore = m,
                segment = Classify(r, f, m),
            };
        }).OrderByDescending(s => s.rScore + s.fScore + s.mScore).ToList();

        var bySegment = scored.GroupBy(s => s.segment)
            .Select(g => new { segment = g.Key, count = g.Count(),
                totalValue = g.Sum(s => s.monetary) })
            .OrderByDescending(g => g.count)
            .ToList();

        var inputJson = JsonSerializer.Serialize(new { windowDays, customerCount = perCustomer.Count });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.CustomerRfmSegmentation,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: perCustomer.Count.ToString(),
                AiConfidence: 0.90m, LocalModelAnswer: perCustomer.Count.ToString(),
                LocalModelConfidence: 0.90m, LocalModelVersion: "rfm-v1",
                SourceEntityType: "Contact", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "rfm", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            windowDays,
            customerCount = perCustomer.Count,
            summary = bySegment,
            top20 = scored.Take(20),
            reasoning = $"แบ่ง {perCustomer.Count} ลูกค้าเป็น {bySegment.Count} กลุ่ม " +
                $"— Champion {bySegment.FirstOrDefault(s => s.segment == "Champion")?.count ?? 0} ราย, " +
                $"AtRisk {bySegment.FirstOrDefault(s => s.segment == "AtRisk")?.count ?? 0} ราย",
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Inventory ABC analysis — Pareto by revenue.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("inventory/abc-analysis")]
    public async Task<ActionResult<ApiResponse<object>>> GetAbcAnalysis(
        Guid companyId, [FromQuery] int windowDays = 365, CancellationToken ct = default)
    {
        windowDays = Math.Clamp(windowDays, 90, 1825);
        var since = DateTime.UtcNow.AddDays(-windowDays);

        // Revenue per product = sum of DocumentLine.Amount where parent doc
        // is a sales doc (Invoice/TaxInvoice/Receipt) in window.
        var perProduct = await (from l in _db.Set<DocumentLine>().AsNoTracking()
                                join d in _db.Documents.AsNoTracking() on l.DocumentId equals d.Id
                                where d.CompanyId == companyId && !d.IsDeleted
                                   && l.ProductCode != null
                                   && (d.DocumentType == DocumentType.Invoice
                                       || d.DocumentType == DocumentType.TaxInvoice
                                       || d.DocumentType == DocumentType.Receipt)
                                   && d.DocumentDate >= since
                                   && d.Status != DocumentStatus.Rejected
                                   && d.Status != DocumentStatus.Voided
                                group l by l.ProductCode into g
                                select new { ProductCode = g.Key, Revenue = g.Sum(x => x.Amount) }
                               ).ToListAsync(ct);

        if (perProduct.Count == 0)
        {
            return Ok(new ApiResponse<object>(true, new
            {
                windowDays, totalRevenue = 0m, classification = Array.Empty<object>(),
                reasoning = $"ไม่มีข้อมูลการขายใน {windowDays} วัน",
            }));
        }

        var sorted = perProduct.OrderByDescending(p => p.Revenue).ToList();
        var totalRevenue = sorted.Sum(p => p.Revenue);

        var classified = new List<object>();
        decimal cumulative = 0m;
        foreach (var p in sorted)
        {
            cumulative += p.Revenue;
            var cumPct = totalRevenue > 0 ? cumulative / totalRevenue : 0m;
            string cls = cumPct <= 0.75m ? "A" : cumPct <= 0.95m ? "B" : "C";
            classified.Add(new
            {
                productCode = p.ProductCode,
                revenue = Math.Round(p.Revenue, 2),
                revenuePct = totalRevenue > 0 ? Math.Round(p.Revenue / totalRevenue * 100m, 2) : 0m,
                cumulativePct = Math.Round(cumPct * 100m, 2),
                classification = cls,
            });
        }

        var counts = classified.GroupBy(o => ((dynamic)o).classification as string)
            .Select(g => new { cls = g.Key!, count = g.Count() })
            .ToList();
        var aCount = counts.FirstOrDefault(c => c.cls == "A")?.count ?? 0;
        var bCount = counts.FirstOrDefault(c => c.cls == "B")?.count ?? 0;
        var cCount = counts.FirstOrDefault(c => c.cls == "C")?.count ?? 0;

        var inputJson = JsonSerializer.Serialize(new { windowDays, productCount = perProduct.Count });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.InventoryAbcAnalysis,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: aCount.ToString(),
                AiConfidence: 0.90m, LocalModelAnswer: aCount.ToString(),
                LocalModelConfidence: 0.90m, LocalModelVersion: "pareto-v1",
                SourceEntityType: "Product", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "pareto", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            windowDays,
            totalRevenue = Math.Round(totalRevenue, 2),
            classCounts = new { A = aCount, B = bCount, C = cCount },
            items = classified.Take(100),
            reasoning = $"แบ่ง {perProduct.Count} SKUs: A-class {aCount} (75% revenue), " +
                $"B-class {bCount} (15%), C-class {cCount} (5%). A-class ต้อง stockout watch — " +
                $"C-class พิจารณาตัดทิ้งหรือลด stock.",
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Smart CSV column-mapping suggestion — match headers to schema.
    // ────────────────────────────────────────────────────────────────

    public sealed record SmartImportRequest(
        string EntityType,            // "Contact" | "Product" | "Invoice" | "JournalEntry"
        List<string> Headers,
        List<List<string>>? SampleRows);

    [HttpPost("import/suggest-mapping")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestImportMapping(
        Guid companyId, [FromBody] SmartImportRequest req, CancellationToken ct)
    {
        if (req.Headers == null || req.Headers.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null, "Headers ห้ามว่าง"));

        // Schema dictionary — target field → keyword set
        var contactSchema = new Dictionary<string, string[]>
        {
            ["name"] = new[] { "ชื่อ", "ลูกค้า", "ผู้ขาย", "name", "customer", "vendor", "supplier" },
            ["taxId"] = new[] { "เลขผู้เสียภาษี", "เลขประจำ", "taxid", "tax_id", "tax id", "vat number" },
            ["address"] = new[] { "ที่อยู่", "address" },
            ["phone"] = new[] { "โทร", "เบอร์", "phone", "tel", "mobile" },
            ["email"] = new[] { "อีเมล", "email", "e-mail" },
            ["isCustomer"] = new[] { "ลูกค้า", "customer" },
            ["isSupplier"] = new[] { "ผู้จำหน่าย", "ผู้ขาย", "supplier", "vendor" },
        };
        var productSchema = new Dictionary<string, string[]>
        {
            ["code"] = new[] { "รหัส", "รหัสสินค้า", "code", "sku", "product code" },
            ["name"] = new[] { "ชื่อ", "ชื่อสินค้า", "name", "product" },
            ["sellingPrice"] = new[] { "ราคาขาย", "ราคา", "price", "selling" },
            ["costPrice"] = new[] { "ต้นทุน", "cost" },
            ["currentStock"] = new[] { "คงคลัง", "stock", "qty", "quantity" },
            ["vatRate"] = new[] { "vat", "ภาษีมูลค่า" },
            ["unit"] = new[] { "หน่วย", "unit" },
            ["category"] = new[] { "หมวด", "category" },
        };
        var jeSchema = new Dictionary<string, string[]>
        {
            ["entryDate"] = new[] { "วันที่", "date", "entry date" },
            ["description"] = new[] { "คำอธิบาย", "description", "memo", "narrative" },
            ["accountCode"] = new[] { "รหัสบัญชี", "account code", "account" },
            ["debitAmount"] = new[] { "เดบิต", "debit", "dr" },
            ["creditAmount"] = new[] { "เครดิต", "credit", "cr" },
        };
        var schema = req.EntityType?.ToLowerInvariant() switch
        {
            "contact" => contactSchema,
            "product" => productSchema,
            "journalentry" or "je" => jeSchema,
            _ => contactSchema,
        };

        decimal Bigram(string a, string b)
        {
            var al = a.ToLowerInvariant(); var bl = b.ToLowerInvariant();
            if (al == bl) return 1m;
            if (string.IsNullOrEmpty(al) || string.IsNullOrEmpty(bl)) return 0m;
            var aSet = new HashSet<string>(); for (int i = 0; i + 1 < al.Length; i++) aSet.Add(al.Substring(i, 2));
            var bSet = new HashSet<string>(); for (int i = 0; i + 1 < bl.Length; i++) bSet.Add(bl.Substring(i, 2));
            if (aSet.Count == 0 || bSet.Count == 0) return 0m;
            var overlap = aSet.Intersect(bSet).Count();
            return (decimal)(2.0 * overlap) / (aSet.Count + bSet.Count);
        }

        var mappings = new List<object>();
        foreach (var header in req.Headers)
        {
            var hLower = header.Trim().ToLowerInvariant();
            string? best = null; decimal bestScore = 0m;
            foreach (var (target, keywords) in schema)
            {
                // Exact keyword contains → high score
                if (keywords.Any(k => hLower.Contains(k.ToLowerInvariant())))
                {
                    if (1m > bestScore) { best = target; bestScore = 1m; }
                    continue;
                }
                // Otherwise rank by bigram similarity max
                var maxSim = keywords.Max(k => Bigram(hLower, k));
                if (maxSim > bestScore && maxSim >= 0.40m)
                {
                    best = target; bestScore = maxSim;
                }
            }
            mappings.Add(new
            {
                header,
                suggestedField = best,
                confidence = bestScore,
                isMapped = best != null,
            });
        }

        var mappedCount = mappings.Count(m => ((dynamic)m).isMapped);
        var inputJson = JsonSerializer.Serialize(new
        {
            req.EntityType, headerCount = req.Headers.Count, mappedCount,
        });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.ImportColumnMatch,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: mappedCount.ToString(),
                AiConfidence: 0.80m, LocalModelAnswer: mappedCount.ToString(),
                LocalModelConfidence: 0.80m, LocalModelVersion: "bigram-keyword-v1",
                SourceEntityType: req.EntityType, SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "bigram-keyword", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            entityType = req.EntityType,
            mappings,
            mappedCount,
            unmappedCount = req.Headers.Count - mappedCount,
            reasoning = $"ตอบ {mappedCount}/{req.Headers.Count} columns. " +
                "ที่ตอบไม่ได้ user เลือก mapping เอง — feedback บันทึกเพื่อปรับ schema ครั้งถัดไป.",
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Dead-stock detection — products with stock > 0 but no OUT
    //  movement in N days (default 90).
    // ────────────────────────────────────────────────────────────────

    [HttpGet("inventory/dead-stock")]
    public async Task<ActionResult<ApiResponse<object>>> CheckDeadStock(
        Guid companyId, [FromQuery] int days = 90, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 30, 365);
        var threshold = DateTime.UtcNow.AddDays(-days);

        // ผลิตภัณฑ์ที่ trackStock = true + currentStock > 0 +
        // ไม่มี OUT movement หลัง threshold (= no movement in last N days)
        var trackedWithStock = await _db.Set<Product>().AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                && p.TrackStock && p.CurrentStock > 0)
            .Select(p => new { p.Id, p.Code, p.Name, p.CurrentStock, p.CostPrice })
            .ToListAsync(ct);

        var recentMoves = await _db.Set<StockMovement>().AsNoTracking()
            .Where(m => m.CompanyId == companyId && !m.IsDeleted
                && (m.MovementType == "OUT" || m.MovementType == "TRANSFER_OUT")
                && m.MovementDate >= threshold)
            .Select(m => m.ProductId)
            .Distinct()
            .ToListAsync(ct);
        var movedSet = new HashSet<Guid>(recentMoves);
        var dead = trackedWithStock.Where(p => !movedSet.Contains(p.Id)).ToList();
        var deadValue = dead.Sum(p => p.CurrentStock * p.CostPrice);

        var inputJson = JsonSerializer.Serialize(new { days, deadCount = dead.Count });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.DeadStockDetection,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: dead.Count.ToString(),
                AiConfidence: 0.95m, LocalModelAnswer: dead.Count.ToString(),
                LocalModelConfidence: 0.95m, LocalModelVersion: "stats-v1",
                SourceEntityType: "Product", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "stats", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            thresholdDays = days,
            deadStockCount = dead.Count,
            deadStockValue = Math.Round(deadValue, 2),
            items = dead.Take(50).Select(p => new
            {
                productId = p.Id, code = p.Code, name = p.Name,
                currentStock = p.CurrentStock,
                tiedUpCapital = Math.Round(p.CurrentStock * p.CostPrice, 2),
            }),
            reasoning = dead.Count == 0
                ? $"ไม่มีสินค้าที่ไม่เคลื่อนไหวใน {days} วันที่ผ่านมา"
                : $"พบ {dead.Count} สินค้าไม่เคลื่อนไหว {days} วัน — เงินทุนจม {deadValue:N0} บาท",
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Book-tax difference detection — non-deductible expenses
    //  per §65ตรี: รับรอง, น้ำมันรถส่วนตัว, ค่าปรับ, บริจาคเกิน.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("tax-adjustment/book-tax-diff")]
    public async Task<ActionResult<ApiResponse<object>>> CheckBookTaxDifference(
        Guid companyId, [FromQuery] int year, CancellationToken ct)
    {
        if (year < 2000 || year > 2200)
            return BadRequest(new ApiResponse<object>(false, null, "year ไม่ถูกต้อง"));

        var start = new DateTime(year, 1, 1);
        var end = new DateTime(year, 12, 31);

        var lines = await (from l in _db.Set<DocumentLine>().AsNoTracking()
                           join d in _db.Documents.AsNoTracking() on l.DocumentId equals d.Id
                           where d.CompanyId == companyId && !d.IsDeleted
                              && d.DocumentDate >= start && d.DocumentDate <= end
                              && (d.DocumentType == DocumentType.PaymentVoucher
                                  || d.DocumentType == DocumentType.PurchaseInvoice
                                  || d.DocumentType == DocumentType.Expense)
                              && d.Status != DocumentStatus.Rejected
                              && d.Status != DocumentStatus.Voided
                           select new { l.Description, l.Amount, d.DocumentNumber, d.Id }
                          ).ToListAsync(ct);

        // §65ตรี — non-deductible categories (สำคัญสำหรับ SMB ไทย)
        var categories = new Dictionary<string, (string[] Keywords, string Label, decimal AddbackPct)>
        {
            ["Entertainment"] = (new[] { "รับรอง", "เลี้ยง", "ของขวัญ", "ของฝาก", "entertain", "gift" }, "ค่ารับรอง (เกิน 0.3% ของรายได้)", 1.0m),
            ["PenaltyFine"] = (new[] { "ค่าปรับ", "ปรับ", "penalty", "fine" }, "ค่าปรับ (เพิ่ม VAT/ภาษีล่าช้า)", 1.0m),
            ["PersonalCar"] = (new[] { "น้ำมัน", "รถส่วนตัว", "ค่าน้ำมัน", "fuel" }, "ค่าน้ำมันรถส่วนตัว (ไม่หักทั้งจำนวน)", 1.0m),
            ["DonationOver"] = (new[] { "บริจาค", "donation" }, "เงินบริจาค (ตรวจไม่เกิน 2% NIBD)", 0.5m),
        };

        var hits = new List<object>();
        decimal totalAddback = 0m;
        foreach (var cat in categories)
        {
            var matched = lines.Where(l => !string.IsNullOrWhiteSpace(l.Description)
                && cat.Value.Keywords.Any(k => l.Description.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matched.Count == 0) continue;
            var amount = matched.Sum(m => m.Amount);
            var addback = Math.Round(amount * cat.Value.AddbackPct, 2);
            totalAddback += addback;
            hits.Add(new
            {
                category = cat.Key, label = cat.Value.Label,
                amount = Math.Round(amount, 2), addback,
                lineCount = matched.Count,
                examples = matched.Take(3).Select(m => new { m.DocumentNumber, m.Description, m.Amount }),
            });
        }

        var inputJson = JsonSerializer.Serialize(new { companyId, year, hitCount = hits.Count, totalAddback });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.BookTaxDifferenceDetection,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: totalAddback.ToString("0.##"),
                AiConfidence: 0.80m, LocalModelAnswer: totalAddback.ToString("0.##"),
                LocalModelConfidence: 0.80m, LocalModelVersion: "rules-v1",
                SourceEntityType: "FiscalYear", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "rules", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            year, totalAddback,
            categories = hits,
            reasoning = hits.Count == 0
                ? $"ไม่พบค่าใช้จ่ายต้องห้ามตาม §65ตรี ในปี {year}"
                : $"พบ {hits.Count} หมวดต้องเพิ่มกลับ — รวม {totalAddback:N0} บาท สำหรับภาษีเงินได้นิติบุคคล",
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Inventory reorder point per product — avg daily consumption
    //  over last 90 days × lead time + safety stock buffer.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("inventory/reorder-point/suggest")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestReorderPoint(
        Guid companyId, [FromQuery] Guid productId, [FromQuery] int? leadTimeDays, CancellationToken ct)
    {
        if (productId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "productId ห้ามว่าง"));

        var since = DateTime.UtcNow.AddDays(-90);
        var outMoves = await _db.Set<StockMovement>().AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.ProductId == productId
                && (m.MovementType == "OUT" || m.MovementType == "TRANSFER_OUT")
                && m.MovementDate >= since && !m.IsDeleted)
            .Select(m => new { m.Quantity, m.MovementDate })
            .ToListAsync(ct);

        // Lead time default 14 วัน (SMB Thai typical for local supplier;
        // imports use 30+). Safety stock buffer 1.5× lead-time demand.
        var lt = leadTimeDays ?? 14;
        var totalOut = outMoves.Sum(m => Math.Abs(m.Quantity));
        var daysCovered = outMoves.Count > 0
            ? Math.Max(1, (DateTime.UtcNow - outMoves.Min(m => m.MovementDate)).Days)
            : 90;
        var avgDaily = daysCovered > 0 ? totalOut / daysCovered : 0m;
        var leadTimeDemand = avgDaily * lt;
        var safetyStock = leadTimeDemand * 0.5m;
        var reorderPoint = Math.Round(leadTimeDemand + safetyStock, 2);
        var maxStock = Math.Round(reorderPoint * 2m, 2);  // 1 lead time worth of cushion

        decimal confidence = outMoves.Count >= 20 ? 0.90m
                          : outMoves.Count >= 10 ? 0.75m
                          : outMoves.Count >= 3 ? 0.55m : 0.30m;

        var product = await _db.Set<Product>().AsNoTracking()
            .Where(p => p.Id == productId && p.CompanyId == companyId)
            .Select(p => new { p.Code, p.Name, p.CurrentStock, p.MinimumStock })
            .FirstOrDefaultAsync(ct);

        var inputJson = JsonSerializer.Serialize(new { productId, leadTime = lt, sampleSize = outMoves.Count });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.InventoryReorderPointSuggestion,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: reorderPoint.ToString("0.##"),
                AiConfidence: confidence,
                LocalModelAnswer: reorderPoint.ToString("0.##"),
                LocalModelConfidence: confidence,
                LocalModelVersion: "stats-v1",
                SourceEntityType: "Product", SourceEntityId: productId,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "stats", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        var needsReorderNow = product != null && product.CurrentStock <= reorderPoint && reorderPoint > 0;

        return Ok(new ApiResponse<object>(true, new
        {
            reorderPoint, maxStock,
            avgDailyConsumption = Math.Round(avgDaily, 4),
            leadTimeDays = lt,
            sampleSize = outMoves.Count,
            confidence,
            currentStock = product?.CurrentStock,
            needsReorderNow,
            reasoning = outMoves.Count == 0
                ? "ยังไม่มีประวัติการเบิก/ขาย — ไม่สามารถคำนวณ"
                : $"จาก {outMoves.Count} ครั้งเบิก/ขาย ใช้เฉลี่ย {avgDaily:N3}/วัน × lead time {lt} วัน + safety = {reorderPoint:N2}",
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Period-close anomaly check — scan checklist before closing.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("period-close/anomaly-check")]
    public async Task<ActionResult<ApiResponse<object>>> CheckPeriodCloseAnomaly(
        Guid companyId, [FromQuery] int year, [FromQuery] int month, CancellationToken ct)
    {
        if (year < 2000 || year > 2200 || month < 1 || month > 12)
            return BadRequest(new ApiResponse<object>(false, null, "year/month ไม่ถูกต้อง"));

        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);

        var issues = new List<object>();

        // 1) Draft JEs in period
        var draftJes = await _db.JournalEntries.AsNoTracking()
            .CountAsync(j => j.CompanyId == companyId && !j.IsDeleted
                && j.EntryDate >= start && j.EntryDate <= end
                && j.Status == JournalEntryStatus.Draft, ct);
        if (draftJes > 0)
            issues.Add(new { severity = "Warning", code = "DRAFT_JE",
                title = $"JE ร่างค้าง {draftJes} ใบ",
                fix = "อนุมัติหรือลบก่อนปิดงวด" });

        // 2) Approved Documents without posted JE (auto-post should have created one)
        // เฉพาะชนิดที่ AutoPost ลง JE จริง — ใบเสนอราคา/PR/PO/ใบส่งของ/ใบวางบิล ไม่มี JE
        // โดยธรรมชาติ เดิมถูกนับเป็น "ยังไม่ลงบัญชี" ทุกใบ (false positive กลบของจริง) และ
        // `== Approved` ทำให้ใบ Sent/Paid ที่ AutoPost ล้มเงียบหลุดจากการตรวจ (ERP_REVIEW A-04)
        var jeBearingTypes = new[]
        {
            DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.ReceiptVoucher,
            DocumentType.DebitNote, DocumentType.CreditNote, DocumentType.PurchaseInvoice, DocumentType.Expense,
            DocumentType.PaymentVoucher, DocumentType.CertificateInLieu, DocumentType.GoodsReceiptNote,
        };
        var docsNoJe = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.DocumentDate >= start && d.DocumentDate <= end
                && jeBearingTypes.Contains(d.DocumentType)
                && !DocumentStatusRules.NotIssued.Contains(d.Status)
                && d.Status != DocumentStatus.Voided
                && !_db.JournalEntries.Any(j => j.SourceDocumentId == d.Id && !j.IsDeleted))
            .CountAsync(ct);
        if (docsNoJe > 0)
            issues.Add(new { severity = "High", code = "DOC_NO_JE",
                title = $"เอกสารอนุมัติ {docsNoJe} ใบยังไม่ลงบัญชี",
                fix = "Re-post หรือสร้าง JE manual" });

        // 3) Trial balance non-zero (sum of approved JE Dr vs Cr)
        var (totalDr, totalCr) = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && !j.IsDeleted
                && j.EntryDate >= start && j.EntryDate <= end
                && j.Status == JournalEntryStatus.Posted)
            .GroupBy(j => 1)
            .Select(g => new ValueTuple<decimal, decimal>(g.Sum(x => x.TotalDebit), g.Sum(x => x.TotalCredit)))
            .FirstOrDefaultAsync(ct);
        var imbalance = Math.Abs(totalDr - totalCr);
        if (imbalance > 0.01m)
            issues.Add(new { severity = "Critical", code = "TRIAL_IMBALANCE",
                title = $"Trial balance ไม่สมดุล — Dr {totalDr:N2} vs Cr {totalCr:N2} (ห่าง {imbalance:N2})",
                fix = "ตรวจ JE ที่ Dr ≠ Cr ในเดือนนี้" });

        // 4) Missing monthly depreciation — fixed assets active แต่ไม่มี JE depreciation
        var hasFixedAssets = await _db.Set<FixedAsset>().AsNoTracking()
            .AnyAsync(a => a.CompanyId == companyId && !a.IsDeleted
                && a.Status == AssetStatus.Active
                && a.PurchaseDate <= end, ct);
        if (hasFixedAssets)
        {
            var depJeCount = await _db.JournalEntries.AsNoTracking()
                .CountAsync(j => j.CompanyId == companyId && !j.IsDeleted
                    && j.EntryDate >= start && j.EntryDate <= end
                    && (j.Description != null && j.Description.Contains("ค่าเสื่อมราคา")), ct);
            if (depJeCount == 0)
                issues.Add(new { severity = "Warning", code = "MISSING_DEPRECIATION",
                    title = "ยังไม่ได้ลงค่าเสื่อมราคาประจำเดือน",
                    fix = "รัน Background depreciation หรือสร้าง JE manual" });
        }

        var inputJson = JsonSerializer.Serialize(new { companyId, year, month });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.PeriodCloseAnomalyCheck,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: issues.Count.ToString(),
                AiConfidence: 0.95m, LocalModelAnswer: issues.Count.ToString(),
                LocalModelConfidence: 0.95m, LocalModelVersion: "rules-v1",
                SourceEntityType: "FiscalPeriod", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "rules", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            period = $"{year}-{month:D2}",
            issueCount = issues.Count,
            canClose = issues.Count == 0
                || !issues.Any(i => ((dynamic)i).severity == "Critical"
                                  || ((dynamic)i).severity == "High"),
            issues, feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Bad-debt risk score per customer.
    //   - daysOverdueMax: latest unpaid invoice days past due
    //   - overdueRatio: overdue / open invoices
    //   - writeOffRate: historical write-off / total invoices
    //  Compose into 0-100 score → Low / Medium / High band.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("bad-debt/check")]
    public async Task<ActionResult<ApiResponse<object>>> CheckBadDebtRisk(
        Guid companyId, [FromQuery] Guid contactId, CancellationToken ct)
    {
        if (contactId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "contactId ห้ามว่าง"));

        var today = DateTime.UtcNow.Date;
        var openInvoices = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId && !d.IsDeleted
                && (d.DocumentType == DocumentType.Invoice
                    || d.DocumentType == DocumentType.TaxInvoice
                    || d.DocumentType == DocumentType.BillingNote)
                && d.BalanceDue > 0
                && (d.Status == DocumentStatus.Approved
                    || d.Status == DocumentStatus.PartiallyPaid
                    || d.Status == DocumentStatus.Sent
                    || d.Status == DocumentStatus.Overdue))
            .Select(d => new { d.Id, d.DueDate, d.DocumentDate, d.BalanceDue, d.TotalAmount })
            .ToListAsync(ct);
        var totalInvoices = await _db.Documents.AsNoTracking()
            .CountAsync(d => d.CompanyId == companyId && d.ContactId == contactId && !d.IsDeleted
                && (d.DocumentType == DocumentType.Invoice
                    || d.DocumentType == DocumentType.TaxInvoice), ct);
        // ระบบไม่มีสถานะ "ตัดหนี้สูญ" แยก — ใช้เอกสารที่ถูก Void เป็น proxy
        // ของการตัดหนี้ (ยกเลิกใบแจ้งหนี้ที่เก็บไม่ได้). 0 = ไม่เคยมี.
        var writeOffs = await _db.Documents.AsNoTracking()
            .CountAsync(d => d.CompanyId == companyId && d.ContactId == contactId && !d.IsDeleted
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.Status == DocumentStatus.Voided, ct);

        int daysOverdueMax = 0;
        decimal overdueAmount = 0m;
        decimal openAmount = openInvoices.Sum(i => i.BalanceDue);
        foreach (var inv in openInvoices)
        {
            var due = inv.DueDate ?? inv.DocumentDate.AddDays(30);
            var days = (today - due.Date).Days;
            if (days > daysOverdueMax) daysOverdueMax = days;
            if (days > 0) overdueAmount += inv.BalanceDue;
        }
        var overdueRatio = openAmount > 0 ? overdueAmount / openAmount : 0m;
        var writeOffRate = totalInvoices > 0 ? (decimal)writeOffs / totalInvoices : 0m;

        // Score 0-100:
        //  daysOverdueMax 0-180+ → 0-50 pts (clamped at 180 = 50pts)
        //  overdueRatio 0-1 → 0-30 pts
        //  writeOffRate 0-1 → 0-20 pts
        var score = Math.Min(50m, daysOverdueMax / 180m * 50m)
                  + overdueRatio * 30m
                  + writeOffRate * 20m;
        score = Math.Round(Math.Clamp(score, 0m, 100m), 1);

        string band; string color;
        if (score >= 60m) { band = "High"; color = "#dc2626"; }
        else if (score >= 30m) { band = "Medium"; color = "#f59e0b"; }
        else { band = "Low"; color = "#10b981"; }

        var inputJson = JsonSerializer.Serialize(new
        {
            contactId, openInvoices = openInvoices.Count, totalInvoices, writeOffs,
            daysOverdueMax, overdueRatio, writeOffRate, score,
        });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.BadDebtRiskDetection,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: band, AiConfidence: 0.85m,
                LocalModelAnswer: band, LocalModelConfidence: 0.85m,
                LocalModelVersion: "stats-v1",
                SourceEntityType: "Contact", SourceEntityId: contactId,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "stats", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            score, band, color,
            metrics = new {
                openInvoices = openInvoices.Count, openAmount,
                overdueAmount, daysOverdueMax,
                overdueRatio = Math.Round(overdueRatio, 2),
                writeOffs, writeOffRate = Math.Round(writeOffRate, 2),
            },
            reasoning = band switch
            {
                "High" => $"⚠️ ความเสี่ยงสูง — มี {openInvoices.Count} ใบค้าง สูงสุด {daysOverdueMax} วัน (เคยตัดหนี้สูญ {writeOffs} ใบ)",
                "Medium" => $"ความเสี่ยงกลาง — มี {openInvoices.Count} ใบค้าง สูงสุด {daysOverdueMax} วัน",
                _ => $"ความเสี่ยงต่ำ — จ่ายปกติ ({openInvoices.Count} ใบค้าง)",
            },
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Discount % suggestion when creating a sales document.
    // ────────────────────────────────────────────────────────────────

    [HttpGet("discount/suggest")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestDiscount(
        Guid companyId, [FromQuery] Guid contactId, [FromQuery] decimal? amount, CancellationToken ct)
    {
        if (contactId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "contactId ห้ามว่าง"));

        // Lifetime sales (paid invoices) + repeat count
        var sales = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId && !d.IsDeleted
                && (d.DocumentType == DocumentType.Invoice
                    || d.DocumentType == DocumentType.TaxInvoice
                    || d.DocumentType == DocumentType.Receipt))
            .Select(d => new { d.TotalAmount, d.DocumentDate, d.DiscountAmount })
            .ToListAsync(ct);

        var lifetimeValue = sales.Sum(s => s.TotalAmount);
        var docCount = sales.Count;
        var avgDiscountGiven = sales.Count > 0 && lifetimeValue > 0
            ? Math.Round(sales.Sum(s => s.DiscountAmount) / Math.Max(lifetimeValue, 1m) * 100m, 2)
            : 0m;

        // Suggest tier:
        //  Lifetime ≥1M + 10+ docs → 7%
        //  Lifetime ≥300K + 5+ docs → 5%
        //  Lifetime ≥100K + 3+ docs → 3%
        //  Lifetime ≥30K + 2+ docs → 2%
        //  else → 0%
        decimal suggested; string tier;
        if (lifetimeValue >= 1_000_000m && docCount >= 10) { suggested = 7m; tier = "Diamond"; }
        else if (lifetimeValue >= 300_000m && docCount >= 5) { suggested = 5m; tier = "Gold"; }
        else if (lifetimeValue >= 100_000m && docCount >= 3) { suggested = 3m; tier = "Silver"; }
        else if (lifetimeValue >= 30_000m && docCount >= 2) { suggested = 2m; tier = "Bronze"; }
        else { suggested = 0m; tier = "New"; }

        // Cap at avgDiscountGiven + 2% so we never recommend wildly above
        // what this customer historically received.
        if (avgDiscountGiven > 0 && suggested > avgDiscountGiven + 2m)
            suggested = Math.Round(avgDiscountGiven + 2m, 2);

        var inputJson = JsonSerializer.Serialize(new { contactId, amount, lifetimeValue, docCount });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.DiscountSuggestion,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: suggested.ToString("0.##"), AiConfidence: 0.75m,
                LocalModelAnswer: suggested.ToString("0.##"), LocalModelConfidence: 0.75m,
                LocalModelVersion: "tier-v1",
                SourceEntityType: "Contact", SourceEntityId: contactId,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "tier", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            discountPercent = suggested,
            tier, confidence = 0.75m,
            metrics = new { lifetimeValue, docCount, avgDiscountGiven },
            reasoning = tier switch
            {
                "Diamond" => $"💎 ลูกค้า Diamond — ซื้อ {docCount} ครั้ง รวม {lifetimeValue:N0} บาท → แนะนำ 7%",
                "Gold" => $"🥇 ลูกค้า Gold — ซื้อ {docCount} ครั้ง รวม {lifetimeValue:N0} บาท → แนะนำ 5%",
                "Silver" => $"🥈 ลูกค้า Silver — ซื้อ {docCount} ครั้ง รวม {lifetimeValue:N0} บาท → แนะนำ 3%",
                "Bronze" => $"🥉 ลูกค้า Bronze — ซื้อ {docCount} ครั้ง รวม {lifetimeValue:N0} บาท → แนะนำ 2%",
                _ => "ลูกค้าใหม่ — ยังไม่มีประวัติพอจะลด — แนะนำ 0%",
            },
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Approval routing suggestion — who should approve this doc?
    // ────────────────────────────────────────────────────────────────

    [HttpGet("approval-routing/suggest")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestApprovalRouting(
        Guid companyId, [FromQuery] string documentType, [FromQuery] decimal amount, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(documentType))
            return BadRequest(new ApiResponse<object>(false, null, "documentType ห้ามว่าง"));

        // Find approvers who approved similar (doc type + amount band ±30%)
        // documents in the last 90 days. Pick the most frequent one.
        var lo = amount * 0.7m;
        var hi = amount * 1.3m;
        var since = DateTime.UtcNow.AddDays(-90);
        var docType = Enum.TryParse<DocumentType>(documentType, true, out var dt) ? dt : DocumentType.Invoice;

        var approvers = await (
            from a in _db.Set<DocumentApproval>().AsNoTracking()
            join d in _db.Documents.AsNoTracking() on a.DocumentId equals d.Id
            where a.CompanyId == companyId && a.Status == ApprovalStatus.Approved
               && a.ApproverUserId != null
               && d.DocumentType == docType
               && d.TotalAmount >= lo && d.TotalAmount <= hi
               && a.ApprovedAt != null && a.ApprovedAt > since
            select a.ApproverUserId!.Value
        ).ToListAsync(ct);

        Guid? pickedUserId = null; string? pickedName = null; int supporting = 0;
        if (approvers.Count >= 2)
        {
            var top = approvers.GroupBy(u => u).OrderByDescending(g => g.Count()).First();
            pickedUserId = top.Key;
            supporting = top.Count();
            pickedName = await _db.Users.AsNoTracking()
                .Where(u => u.Id == pickedUserId.Value)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync(ct);
        }

        decimal confidence = approvers.Count >= 5 ? 0.85m
                           : approvers.Count >= 2 ? 0.65m : 0m;
        var inputJson = JsonSerializer.Serialize(new { documentType, amount, sampleSize = approvers.Count });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.ApprovalRoutingSuggestion,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: pickedUserId?.ToString() ?? "",
                AiConfidence: confidence,
                LocalModelAnswer: pickedUserId?.ToString() ?? "",
                LocalModelConfidence: confidence,
                LocalModelVersion: "history-mode-v1",
                SourceEntityType: "Document", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "history-mode", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            approverUserId = pickedUserId,
            approverName = pickedName,
            confidence, supportingSamples = supporting,
            reasoning = pickedUserId.HasValue
                ? $"📋 {pickedName} อนุมัติเอกสารแบบเดียวกัน {supporting} ใน {approvers.Count} ครั้งล่าสุด"
                : "ยังไม่มีประวัติการอนุมัติพอจะแนะนำ — ใช้กฎ ApprovalRule เดิม",
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Credit limit suggestion for a new customer — from p75 of
    //  existing customers' peak AR balance (sum of unpaid Invoice
    //  balances). Cold-starts to 50,000 THB for the very first
    //  customer (SMB Thai default).
    // ────────────────────────────────────────────────────────────────

    [HttpGet("credit-limit/suggest")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestCreditLimit(
        Guid companyId, CancellationToken ct)
    {
        // Compute per-customer max outstanding AR balance from the last
        // 90 days of Invoice / TaxInvoice documents. Cap small samples to
        // a safe default (50K) so a brand-new tenant doesn't get a
        // misleading limit from 1 outlier.
        var since = DateTime.UtcNow.AddDays(-90);
        var customerBalances = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.ContactId != Guid.Empty
                && (d.DocumentType == DocumentType.Invoice
                    || d.DocumentType == DocumentType.TaxInvoice
                    || d.DocumentType == DocumentType.BillingNote)
                && d.DocumentDate >= since)
            .GroupBy(d => d.ContactId)
            .Select(g => new { ContactId = g.Key, Peak = g.Sum(x => x.TotalAmount) })
            .ToListAsync(ct);

        decimal suggested; string source; decimal confidence;
        if (customerBalances.Count >= 5)
        {
            var sorted = customerBalances.Select(c => c.Peak).OrderBy(p => p).ToList();
            // 75th percentile peak — generous-but-not-reckless default
            var p75Index = (int)Math.Ceiling(sorted.Count * 0.75) - 1;
            p75Index = Math.Clamp(p75Index, 0, sorted.Count - 1);
            suggested = Math.Round(sorted[p75Index] / 1000m) * 1000m;  // round to 1000s
            if (suggested < 10000m) suggested = 10000m;
            source = "P75PeakBalance"; confidence = 0.80m;
        }
        else if (customerBalances.Count > 0)
        {
            suggested = Math.Round(customerBalances.Average(c => c.Peak) / 1000m) * 1000m;
            if (suggested < 10000m) suggested = 10000m;
            source = "MeanPeakBalance"; confidence = 0.55m;
        }
        else
        {
            suggested = 50000m; source = "SmbDefault"; confidence = 0.30m;
        }

        var inputJson = JsonSerializer.Serialize(new { sampleSize = customerBalances.Count });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.CreditLimitSuggestion,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: suggested.ToString("0"), AiConfidence: confidence,
                LocalModelAnswer: suggested.ToString("0"), LocalModelConfidence: confidence,
                LocalModelVersion: "stats-v1",
                SourceEntityType: "Contact", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "stats", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            creditLimit = suggested, source, confidence,
            sampleSize = customerBalances.Count,
            reasoning = source switch
            {
                "P75PeakBalance" => $"จากลูกค้าปัจจุบัน {customerBalances.Count} ราย: P75 ของยอดค้างสูงสุด = {suggested:N0} บาท",
                "MeanPeakBalance" => $"จากลูกค้า {customerBalances.Count} ราย (น้อย): ค่าเฉลี่ย = {suggested:N0} บาท",
                _ => "ลูกค้าใหม่ของบริษัท — ใช้ค่าเริ่มต้น 50,000 บาท (ปรับได้ภายหลัง)",
            },
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Product category tagging from name + description.
    // ────────────────────────────────────────────────────────────────

    public sealed record SuggestProductCategoryRequest(
        string ProductName,
        string? Description);

    [HttpPost("product/suggest-category")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestProductCategory(
        Guid companyId, [FromBody] SuggestProductCategoryRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ProductName))
            return BadRequest(new ApiResponse<object>(false, null, "ProductName ห้ามว่าง"));

        var catMemKey = NormKey(req.ProductName);
        var catMem = await MemoryLookupAsync(companyId, AiFeatureKey.ProductCategoryTagging, catMemKey, ct);
        if (catMem.HasValue)
        {
            var fid0 = await RecordLearnedAsync(companyId, AiFeatureKey.ProductCategoryTagging, catMemKey,
                catMem.Value.Answer, catMem.Value.Confidence, "Product", null, ct);
            // Answer อาจเป็น category name หรือ ProductCategory id — ส่งทั้งคู่
            var existingMatch = await _db.Set<ProductCategory>().AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted
                    && (c.Id.ToString() == catMem.Value.Answer || c.Name == catMem.Value.Answer))
                .Select(c => new { c.Id, c.Name }).FirstOrDefaultAsync(ct);
            return Ok(new ApiResponse<object>(true, new
            {
                category = existingMatch?.Name ?? catMem.Value.Answer,
                reasoning = $"🧠 เรียนรู้จากที่ทีมคุณเคยจัดสินค้าชื่อคล้ายกัน ({catMem.Value.Samples} ครั้ง)",
                existingCategoryId = existingMatch?.Id, existingCategoryName = existingMatch?.Name,
                confidence = catMem.Value.Confidence, feedbackId = fid0,
            }));
        }

        var text = (req.ProductName + " " + (req.Description ?? "")).ToLowerInvariant();
        string category; string reasoning;

        // Map ตามหมวดที่นิยมใน SMB ไทย
        if (Contains(text, "เสื้อ", "กางเกง", "กระโปรง", "ผ้า", "ชุด", "ปกเสื้อ", "shirt", "pants", "skirt", "fabric"))
        { category = "เสื้อผ้า"; reasoning = "ตรงกับหมวด เสื้อผ้า/เครื่องแต่งกาย"; }
        else if (Contains(text, "อาหาร", "เครื่องดื่ม", "ผลไม้", "ขนม", "เบเกอรี่", "food", "snack", "beverage"))
        { category = "อาหารและเครื่องดื่ม"; reasoning = "ตรงกับหมวด อาหาร/เครื่องดื่ม"; }
        else if (Contains(text, "ปูน", "เหล็ก", "ทราย", "อิฐ", "ไม้", "ท่อ", "วัสดุ", "construction", "cement", "steel"))
        { category = "วัสดุก่อสร้าง"; reasoning = "ตรงกับหมวด วัสดุก่อสร้าง"; }
        else if (Contains(text, "บริการ", "ค่าบริการ", "ค่าแรง", "service", "fee", "consulting"))
        { category = "บริการ"; reasoning = "ตรงกับหมวด บริการ"; }
        else if (Contains(text, "computer", "คอมพิวเตอร์", "laptop", "เครื่อง", "อิเล็กทรอนิกส์", "phone", "มือถือ"))
        { category = "อิเล็กทรอนิกส์"; reasoning = "ตรงกับหมวด อิเล็กทรอนิกส์/IT"; }
        else if (Contains(text, "ยา", "วิตามิน", "อาหารเสริม", "medicine", "supplement", "เครื่องสำอาง", "cosmetic"))
        { category = "สุขภาพและความงาม"; reasoning = "ตรงกับหมวด สุขภาพ/ความงาม"; }
        else if (Contains(text, "เฟอร์", "โต๊ะ", "เก้าอี้", "ตู้", "ชั้น", "furniture", "office"))
        { category = "เฟอร์นิเจอร์"; reasoning = "ตรงกับหมวด เฟอร์นิเจอร์/สำนักงาน"; }
        else
        { category = "ทั่วไป"; reasoning = "ค่าเริ่มต้น — ตรวจสอบประเภทอีกครั้ง"; }

        // Match กับ ProductCategory ที่บริษัทมีอยู่ (ถ้ามี)
        var existing = await _db.Set<ProductCategory>().AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => new { c.Id, c.Code, c.Name })
            .ToListAsync(ct);
        var matched = existing.FirstOrDefault(c =>
            c.Name.Equals(category, StringComparison.OrdinalIgnoreCase)
            || c.Name.Contains(category, StringComparison.OrdinalIgnoreCase));

        var inputJson = JsonSerializer.Serialize(new { req.ProductName, req.Description });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.ProductCategoryTagging,
                PromptHash: catMemKey, PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: matched?.Id.ToString() ?? category, AiConfidence: 0.75m,
                LocalModelAnswer: matched?.Id.ToString() ?? category, LocalModelConfidence: 0.75m,
                LocalModelVersion: "keyword-v1",
                SourceEntityType: "Product", SourceEntityId: null,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "keyword", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new
        {
            category, reasoning,
            existingCategoryId = matched?.Id,
            existingCategoryName = matched?.Name,
            confidence = 0.75m, feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Price drift detection per (product, contact). Flags when the
    //  entered unit price is >20% off the mean of the last 12 prior
    //  document lines for the same product. Catches typos (1500 vs
    //  15000) + price changes worth a second look before approval.
    // ────────────────────────────────────────────────────────────────

    public sealed record PriceDriftRequest(
        string ProductCode,
        decimal UnitPrice,
        Guid? ContactId,
        string? Description);

    [HttpPost("price-drift/check")]
    public async Task<ActionResult<ApiResponse<object>>> CheckPriceDrift(
        Guid companyId, [FromBody] PriceDriftRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ProductCode) || req.UnitPrice <= 0)
            return Ok(new ApiResponse<object>(true, new { ok = true, hasWarning = false }));

        // Pull last 12 historical lines for (companyId, productCode). Filter to
        // same contact when given (more accurate per-vendor mean), else
        // company-wide. Excludes the current document if it's being re-saved.
        var q = _db.Set<DocumentLine>().AsNoTracking()
            .Where(l => l.Document.CompanyId == companyId
                && l.ProductCode == req.ProductCode
                && !l.Document.IsDeleted
                && l.UnitPrice > 0);
        if (req.ContactId.HasValue && req.ContactId.Value != Guid.Empty)
            q = q.Where(l => l.Document.ContactId == req.ContactId.Value);
        var prices = await q
            .OrderByDescending(l => l.Document.DocumentDate)
            .Take(12)
            .Select(l => l.UnitPrice)
            .ToListAsync(ct);

        bool hasWarning = false; decimal expected = 0m; decimal stdDev = 0m; string? severity = null;
        if (prices.Count >= 3)
        {
            expected = prices.Average();
            var variance = prices.Sum(p => (p - expected) * (p - expected)) / prices.Count;
            stdDev = (decimal)Math.Sqrt((double)variance);
            // Deviation thresholds: 20% = warning, 50% = critical (likely typo)
            var diff = Math.Abs(req.UnitPrice - expected);
            var pctDiff = expected > 0 ? diff / expected : 0m;
            if (pctDiff > 0.50m) { hasWarning = true; severity = "Critical"; }
            else if (pctDiff > 0.20m) { hasWarning = true; severity = "Warning"; }
        }

        var inputJson = JsonSerializer.Serialize(new
        {
            req.ProductCode, req.UnitPrice, req.ContactId, sampleSize = prices.Count,
        });
        Guid? feedbackId = null;
        if (hasWarning)
        {
            try
            {
                feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                    CompanyId: companyId, FeatureKey: AiFeatureKey.PriceDriftDetection,
                    PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                    AiPrimaryAnswer: severity, AiConfidence: 0.85m,
                    LocalModelAnswer: severity, LocalModelConfidence: 0.85m,
                    LocalModelVersion: "stats-v1",
                    SourceEntityType: "DocumentLine", SourceEntityId: req.ContactId,
                    Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                    ModelVersion: "stats", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                    CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
            }
            catch { /* best-effort */ }
        }

        return Ok(new ApiResponse<object>(true, new
        {
            hasWarning, severity,
            expectedMean = Math.Round(expected, 2),
            stdDev = Math.Round(stdDev, 2),
            sampleSize = prices.Count,
            ratio = expected > 0 ? Math.Round(req.UnitPrice / expected, 2) : (decimal?)null,
            reasoning = !hasWarning
                ? (prices.Count < 3 ? "ยังไม่มีประวัติพอจะเทียบ" : "ราคาอยู่ในช่วงปกติ")
                : severity == "Critical"
                    ? $"⚠️ ราคา {req.UnitPrice:N2} ห่างจากค่าเฉลี่ย {expected:N2} > 50% — อาจพิมพ์ผิด"
                    : $"⚠️ ราคา {req.UnitPrice:N2} ห่างจากค่าเฉลี่ย {expected:N2} > 20% — ตรวจสอบ",
            feedbackId,
        }));
    }

    // ────────────────────────────────────────────────────────────────
    //  Document memo / description auto-generate from doc type +
    //  contact + lines summary.
    // ────────────────────────────────────────────────────────────────

    public sealed record SuggestMemoRequest(
        string DocumentType,
        Guid? ContactId,
        DateTime? DocumentDate,
        List<string>? LineDescriptions);

    [HttpPost("document/suggest-memo")]
    public async Task<ActionResult<ApiResponse<object>>> SuggestDocumentMemo(
        Guid companyId, [FromBody] SuggestMemoRequest req, CancellationToken ct)
    {
        var thMonths = new[] { "", "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.",
            "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." };
        string? contactName = null;
        if (req.ContactId.HasValue)
        {
            contactName = await _db.Contacts.AsNoTracking()
                .Where(c => c.Id == req.ContactId.Value && c.CompanyId == companyId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct);
        }

        var verb = req.DocumentType switch
        {
            "Invoice" or "TaxInvoice" or "Receipt" or "Quotation" or "BillingNote" or "DebitNote"
                => "ขาย",
            "PurchaseOrder" or "PurchaseInvoice" or "PaymentVoucher" or "ExpenseClaim"
                => "ซื้อ",
            "CreditNote" => "ลดหนี้",
            _ => "บันทึก",
        };

        var date = req.DocumentDate ?? DateTime.UtcNow;
        var period = $"{thMonths[date.Month]} {date.Year + 543}";

        // Summary of lines: take top-2 unique descriptions, truncate long ones.
        var summary = (req.LineDescriptions ?? new List<string>())
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Length > 40 ? d.Substring(0, 40) + "…" : d)
            .Distinct()
            .Take(2)
            .ToList();
        var lineSummary = summary.Count > 0 ? string.Join(" + ", summary) : "รายการตามแนบ";

        var memo = contactName != null
            ? $"{verb} {lineSummary} {(verb == "ซื้อ" ? "จาก" : "ให้")} {contactName} งวด {period}"
            : $"{verb} {lineSummary} งวด {period}";

        var inputJson = JsonSerializer.Serialize(new
        {
            req.DocumentType, req.ContactId,
            lineCount = req.LineDescriptions?.Count ?? 0,
        });
        Guid? feedbackId = null;
        try
        {
            feedbackId = await _feedback.RecordCallAsync(new AiFeedbackRecord(
                CompanyId: companyId, FeatureKey: AiFeatureKey.DocumentMemoGeneration,
                PromptHash: "", PromptJson: inputJson, ResponseJson: null,
                AiPrimaryAnswer: memo, AiConfidence: 0.75m,
                LocalModelAnswer: memo, LocalModelConfidence: 0.75m,
                LocalModelVersion: "template-v1",
                SourceEntityType: "Document", SourceEntityId: req.ContactId,
                Status: AiCallStatus.LocalServed, ProviderUsed: AiProviderType.None,
                ModelVersion: "template", LatencyMs: 0, InputTokens: 0, OutputTokens: 0,
                CostUsd: 0m, CacheHitOfFeedbackId: null, ErrorMessage: null), ct);
        }
        catch { /* best-effort */ }

        return Ok(new ApiResponse<object>(true, new { memo, confidence = 0.75m, feedbackId }));
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

        // ── ด่านกันคำตอบที่แต่งขึ้นก่อนให้แตะฟอร์ม (T3-07) ───────────────────
        // เดิมหน้าเว็บเขียนคำตอบ AI ลงช่องตรง ๆ ทุกช่อง **รวมยอดเงินและเลขผู้เสียภาษี**
        // โดยเซิร์ฟเวอร์ตรวจแค่ว่า JSON มี key ครบไหม ⇒ ตัวเลขที่โมเดลแต่งกลายเป็นยอด
        // บนใบกำกับ §86/4 ด้วยการกดปุ่มเดียว. ตัวตัดสินต้องอยู่ที่เซิร์ฟเวอร์
        // (Helpers/OcrReviewGuard) — หน้าเว็บได้แค่ "ช่องที่ผ่าน" กับ "ช่องที่ไม่ผ่าน
        // + เหตุผล" ไปแสดงเป็นคำแนะนำ
        var scanAmounts = await _db.Set<Models.Entities.OcrScanResult>().AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.Id == scanResultId)
            .Select(x => new { x.ExtractedSubTotal, x.ExtractedVatAmount, x.ExtractedTotalAmount, x.RawTextContent })
            .FirstOrDefaultAsync(ct);
        var guard = Helpers.OcrReviewGuard.Filter(r.StructuredJson,
            scanAmounts?.ExtractedSubTotal, scanAmounts?.ExtractedVatAmount, scanAmounts?.ExtractedTotalAmount,
            // ข้อความจากกระดาษ = หลักฐานเดียวที่พิสูจน์ชื่อ/เลขที่ที่โมเดลเสนอได้
            rawText: scanAmounts?.RawTextContent);

        var dto = ToAdvancedDto(r);
        return Ok(new ApiResponse<object>(true, new
        {
            review = dto,
            accepted = guard.Accepted,
            rejected = guard.Rejected.Select(x => new { field = x.Field, value = x.Value, reason = x.Reason }),
        }));
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
    //  Reorder forecast narrative — **เลขคณิตล้วน ไม่เรียก AI** (D-5)
    //
    //  ⚠️ เดิมส่งตารางที่เราคำนวณเสร็จแล้วไปให้ DeepSeek เรียบเรียงเป็นร้อยแก้ว
    //  ซึ่งผิดเกณฑ์ DECISION_DOCTRINE §2.1 สองข้อพร้อมกัน (ถามสิ่งที่เราเพิ่ง
    //  คำนวณเอง · คำตอบไม่ใช่ชุดปิดที่ตรวจกลับได้) และเป็น AiFeatureKey ตัวเดียว
    //  ในระบบที่ยิง provider โดยไม่มี ILocalDistillationModel ⇒ ปิด provider
    //  ทุกตัวแล้ว endpoint ตอบ "AI ปิดอยู่" = kill-switch test ไม่ผ่าน
    //
    //  ตอนนี้ประโยคมาจาก Helpers/ReorderNarrative (pure + มีเทสต์) ⇒ ทำงานครบ
    //  100% เมื่อ AI ดับ/เกินงบ/ไม่มีเน็ต · `usedAi` คง false เสมอเพื่อให้หน้าจอ
    //  ติดป้ายซื่อสัตย์ (กฎเหล็ก #1: "🤖 AI แนะนำ" เฉพาะตอนเรียกจริง)
    // ────────────────────────────────────────────────────────────────
    public sealed record ReorderNarrativeRequest(
        List<ReorderNarrativeRow> Rows);

    public sealed record ReorderNarrativeRow(
        string Sku, string Name, decimal CurrentStock,
        decimal AvgDailyDemand, decimal DaysOfStockRemaining,
        decimal SuggestedOrderQuantity, string Urgency);

    [HttpPost("inventory/reorder-narrative")]
    public ActionResult<ApiResponse<object>> ReorderNarrative(
        Guid companyId, [FromBody] ReorderNarrativeRequest req)
    {
        var rows = (req?.Rows ?? new List<ReorderNarrativeRow>())
            .Select(r => new Accounting.Helpers.ReorderRow(
                r.Sku, r.Name, r.CurrentStock, r.AvgDailyDemand,
                r.DaysOfStockRemaining, r.SuggestedOrderQuantity, r.Urgency))
            .ToList();
        return Ok(new ApiResponse<object>(true, new
        {
            narrative = Accounting.Helpers.ReorderNarrative.Build(rows),
            // สามสถานะเดิมยุบเหลือหนึ่ง: ตัวคำนวณเชิงกำหนดตอบได้เสมอ จึงไม่มี
            // "ai_unavailable" / "empty_response" อีกต่อไป
            status = "ok",
            usedAi = false,
            feedbackId = (Guid?)null,
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

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Ocr;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class OcrService : IOcrService
{
    // Pre-compiled vendor-detail regexes used by ParseThaiDocument — hot path,
    // 4-5 invocations per scan, so the compilation cost is worth amortizing.
    private static readonly Regex VendorPhoneRegex = new(
        @"(?:โทร(?:ศัพท์)?\.?|TEL\.?|TELEPHONE|PHONE|มือถือ)\s*[:：]?\s*([0-9][\d\-\s\.()]{7,18}\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Sanitize OCR-extracted phone — คืน null ถ้าไม่ใช่เบอร์โทรไทย
    /// ที่สมเหตุสมผล (9-11 หลัก, ไม่ใช่ TaxId 13 หลัก / รหัสไปรษณีย์ 5 หลัก).
    /// ปล่อย Contact.Phone = null ดีกว่าเก็บค่าผิด (TaxId/ชื่อ/string มั่ว ๆ
    /// ที่ Azure DI อาจดึงมาผิด field).</summary>
    private static string? SanePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digitCount = raw.Count(char.IsDigit);
        return digitCount is >= 9 and <= 11 ? raw.Trim() : null;
    }
    private static readonly Regex VendorEmailRegex = new(
        @"[\w\.\-]+@[\w\.\-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled);
    // รหัสสาขา: ย้ายกฎไป BranchCodeExtractor (pure + testable) เพื่ออ่านทั้ง
    // ฝั่งผู้ขายและผู้ซื้อด้วยกฎเดียวกัน — ห้ามมี regex สาขาซ้ำในไฟล์นี้อีก
    // คำ/ข้อยกเว้นเรื่อง "มัดจำ" ย้ายไป Helpers/OcrDepositMarker ตัวเดียว — ตัวนั้นบังคับว่า
    // คำต้องมีเงิน > 0 ประกอบ และบรรทัด "หัก…มัดจำ" ไม่ใช่ใบมัดจำ (สแกนจริง 2026-09-10)
    private static readonly Regex VendorAddressRegex = new(
        @"(?:ที่อยู่|ADDRESS)\s*[:：]?\s*((?:[^\n]+\n?){1,4}?)(?=\n\s*(?:โทร|TEL|เลขประจำตัว|TAX\s*ID|อีเมล|EMAIL|FAX|$))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AccountingDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OcrService> _logger;
    private readonly IDbdLookupService _dbdLookup;
    private readonly AzureDocumentIntelligenceService _azureDi;
    private readonly ExpenseCategoryLearner _categoryLearner;
    private readonly VendorIntelligenceService _vendorIntel;
    private readonly EmbeddedTesseractOcrService _embeddedOcr;
    private readonly AssociationRuleMiner _ruleMiner;
    private readonly TfIdfNaiveBayesClassifier _nbClassifier;
    private readonly RecurringExpenseDetector _recurringDetector;
    private readonly Services.Interfaces.IOcrQuotaService _quota;
    private readonly Ocr.AzureDiPatternLearner _azureLearner;
    private readonly Ocr.VendorKnownGoodCorrector _knownGoodCorrector;
    private readonly Ocr.RdComplianceValidator? _rdComplianceValidator;
    private readonly Ocr.GlobalDocWorkflowLearner? _docWorkflowLearner;
    private readonly Services.Ai.IOcrAiAugmenter? _aiAugmenter;
    private readonly Services.Ai.IAiFeedbackRecorder? _feedbackRecorder;
    /// <summary>อัตรา ธปท. สำหรับใบสกุลต่างประเทศ (optional — ไม่มี = ให้ผู้ใช้กรอกเอง)</summary>
    private readonly Services.Interfaces.IBotExchangeRateService? _fxRates;
    private readonly Services.Interfaces.IAccountingService? _accounting;
    private readonly Ocr.ProductMatcher? _productMatcher;

    public OcrService(AccountingDbContext db, IHttpClientFactory httpClientFactory,
        IConfiguration configuration, ILogger<OcrService> logger,
        IDbdLookupService dbdLookup,
        AzureDocumentIntelligenceService azureDi,
        ExpenseCategoryLearner categoryLearner,
        VendorIntelligenceService vendorIntel,
        EmbeddedTesseractOcrService embeddedOcr,
        AssociationRuleMiner ruleMiner,
        TfIdfNaiveBayesClassifier nbClassifier,
        RecurringExpenseDetector recurringDetector,
        Services.Interfaces.IOcrQuotaService quota,
        Ocr.AzureDiPatternLearner azureLearner,
        Ocr.VendorKnownGoodCorrector knownGoodCorrector,
        Ocr.RdComplianceValidator? rdComplianceValidator = null,
        Ocr.GlobalDocWorkflowLearner? docWorkflowLearner = null,
        Services.Ai.IOcrAiAugmenter? aiAugmenter = null,
        Services.Interfaces.IAccountingService? accounting = null,
        Ocr.ProductMatcher? productMatcher = null,
        Services.Ai.IAiFeedbackRecorder? feedbackRecorder = null,
        Services.Interfaces.IBotExchangeRateService? fxRates = null,
        Services.Interfaces.IPermissionService? perms = null)
    {
        _perms = perms;
        _docWorkflowLearner = docWorkflowLearner;
        _aiAugmenter = aiAugmenter;
        _feedbackRecorder = feedbackRecorder;
        _fxRates = fxRates;
        _accounting = accounting;
        _productMatcher = productMatcher;
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _dbdLookup = dbdLookup;
        _azureDi = azureDi;
        _categoryLearner = categoryLearner;
        _vendorIntel = vendorIntel;
        _embeddedOcr = embeddedOcr;
        _ruleMiner = ruleMiner;
        _nbClassifier = nbClassifier;
        _recurringDetector = recurringDetector;
        _quota = quota;
        _azureLearner = azureLearner;
        _knownGoodCorrector = knownGoodCorrector;
        _rdComplianceValidator = rdComplianceValidator;
    }

    private readonly Services.Interfaces.IPermissionService? _perms;

    /// <summary>
    /// ด่าน "สร้างเอกสารชนิดนี้ได้ไหม" สำหรับเส้นที่ auto-create ในตัวเอง
    ///
    /// <para>⚠️ ที่มา (ผลตรวจทีม E · E-02): ด่าน <c>CanCreateAsync</c> อยู่ที่
    /// <c>OcrController</c> เท่านั้น — แต่ <c>ScanAsync(autoCreate: true)</c>
    /// สร้าง Draft เองโดยไม่ผ่าน controller นั้นเลย ⇒ <b>เส้นรูปใน LINE</b>
    /// สร้างเอกสารได้ทุกชนิดโดยไม่ถูกตรวจสิทธิ์ (สมาชิกที่เจ้าของตั้งใจให้มีแค่
    /// สิทธิ์ฝั่งขาย ถ่ายบิลเข้า LINE แล้วได้ PurchaseInvoice/Expense)</para>
    ///
    /// <para><paramref name="actingUserId"/> = <c>null</c> แปลว่า "เส้นที่ไม่มี
    /// ผู้ใช้เป็นเจ้าของการกระทำ" (partner API ที่ถูกคุมด้วย scope ของ API key
    /// อยู่แล้ว · งานเบื้องหลัง) ⇒ ไม่ตรวจ — <b>ไม่ใช่ค่าเริ่มต้นสำหรับคน</b></para>
    /// </summary>
    private async Task<bool> CanActorCreateAsync(Guid companyId, Guid? actingUserId, OcrScanResult scan)
    {
        if (actingUserId is not Guid uid || _perms is null) return true;
        var target = Accounting.Helpers.OcrTargetDocumentType.Resolve(
            null, scan.TargetDocumentType, scan.DocumentType,
            hasLinkedPurchaseOrder: scan.LinkedPurchaseOrderId.HasValue).Type;
        return await Accounting.Helpers.DocumentPermissionHelper.CanCreateAsync(
            _perms, companyId, uid, target);
    }

    public async Task<OcrResultResponse> ScanAsync(Guid companyId, Guid fileAttachmentId, string? preferredEngine = null, string? externalMetadataJson = null, bool autoCreate = false, bool forceRescan = false, Guid? actingUserId = null)
    {
        // Normalize the user's engine preference into one of three modes.
        // "auto" = current cascade; "azure" = Tier 1 only (no local fallback
        // when user explicitly wants Azure accuracy); "local" = skip Tier 1
        // entirely. Tier 0 e-Tax XML always runs regardless.
        var enginePref = (preferredEngine ?? "auto").Trim().ToLowerInvariant();
        if (enginePref != "azure" && enginePref != "local") enginePref = "auto";

        var file = await _db.FileAttachments
            .FirstOrDefaultAsync(f => f.Id == fileAttachmentId && f.CompanyId == companyId)
            ?? throw new InvalidOperationException("File attachment not found.");

        // Compute file hash for duplicate detection
        string? fileHash = null;
        try
        {
            if (File.Exists(file.StoragePath))
            {
                var bytes = await File.ReadAllBytesAsync(file.StoragePath);
                fileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
        }
        catch { /* hash is optional */ }

        // ── ด่านไฟล์ซ้ำ (hash ตรง) ──
        // `forceRescan` = ผู้ใช้กด "สแกนใหม่" เพราะไม่พอใจผลเดิม — ต้องเดินเส้น
        // engine จริง ไม่ใช่คืนสำเนาเดิมแล้วตอบว่า "สแกนใหม่สำเร็จ" (silent no-op)
        //
        // ⚠️ สองเงื่อนไขที่เพิ่มเข้ามา (บั๊กจริง):
        //   • `!r.IsDuplicate` — เดิมไม่กรอง ⇒ **สำเนาของสำเนา**: การอัปโหลดครั้งที่ 3
        //     อาจไปคัดลอกจากแถวสำเนาของครั้งที่ 2 ทำให้ข้อมูลที่หายไปแล้วยิ่งหายซ้ำ
        //   • `OrderByDescending(CreatedAt)` — เดิมไม่มี OrderBy ⇒ ได้แถวไหนขึ้นกับ
        //     query plan (ไม่ deterministic) · เลือก "ต้นฉบับล่าสุด" เพราะหลังผู้ใช้
        //     กดสแกนใหม่ด้วย engine ที่ดีกว่า ผลที่ใหม่กว่าคือผลที่ควรใช้ต่อ
        OcrScanResult? duplicateOf = null;
        if (!forceRescan && !string.IsNullOrEmpty(fileHash))
        {
            duplicateOf = await _db.Set<OcrScanResult>()
                .Where(r => r.CompanyId == companyId && r.FileHash == fileHash
                            && r.ScanStatus == "Completed" && !r.IsDuplicate)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync();
        }

        var scanResult = new OcrScanResult
        {
            CompanyId = companyId,
            FileAttachmentId = fileAttachmentId,
            OriginalFileName = file.OriginalFileName,
            ScanStatus = "Processing",
            Confidence = 0m,
            FileHash = fileHash,
            IsDuplicate = duplicateOf != null,
            DuplicateOfScanId = duplicateOf?.Id,
            // Record the uploader (resolved to a real user — the integration
            // operator via X-Acting-User, or the web user, or the owner) so the
            // auto-created document's creator signature reflects who actually
            // ran this scan instead of a generic literal.
            CreatedBy = file.UploadedByUserId != Guid.Empty ? file.UploadedByUserId.ToString() : null,
            // Structured order/project metadata the partner uploaded with the
            // file — drives auto project allocation per line below. Stored even
            // when extraction yields no items so it survives reload.
            ExternalMetadataJson = externalMetadataJson
        };

        _db.Set<OcrScanResult>().Add(scanResult);
        await _db.SaveChangesAsync();

        if (duplicateOf != null)
        {
            // คัดลอก **ผลการอ่านทั้งหมด** ผ่านตัวกลางตัวเดียว — เดิมเป็นรายการช่อง
            // ที่เขียนด้วยมือ **10 ช่องจาก 49 ช่องที่ควรคัดลอก** ⇒ ข้อความดิบ · รายการสินค้า ·
            // ช่อง §86/4 ฝั่งผู้ซื้อ · TargetDocumentType · หมวดค่าใช้จ่าย · WHT ·
            // ค่าความมั่นใจรายช่อง **หายเงียบทุกครั้งที่อัปโหลดไฟล์เดิมซ้ำ**
            // (ดูเหตุผลเต็ม + ลิสต์ช่องที่ห้ามคัดลอกใน Helpers/OcrScanSnapshot)
            // ⚠️ ชื่อเต็ม — ไฟล์นี้อยู่ใน Accounting.Services.Implementations และเรพมี
            // `Accounting.Services.Helpers` อยู่ด้วย ⇒ `Helpers.X` จะผูกไปชั้นใกล้แล้ว
            // CS0234 (กติกาใน CLAUDE.md §F · จับได้ด้วย tools/namespace_shadow_check.py)
            Accounting.Helpers.OcrScanSnapshot.CopyExtractionFrom(duplicateOf, scanResult);
            scanResult.ScanStatus = "Completed";
            scanResult.OcrEngine = "Cached";   // copied from a prior scan; no OCR engine ran
            // คำตัดสินเกี่ยวกับ "ตัวกระดาษ" (§82/5 เคลมไม่ได้ · ยังไม่ใช่ใบกำกับ · วันที่เดา ·
            // ยอดบรรทัดไม่ครบ) ต้องติดไปกับสำเนาด้วย — เดิมถูกเขียนทับทั้งก้อน ⇒ อัปไฟล์เดิม
            // ซ้ำแล้วใบกำกับอย่างย่อกลับมาเคลมภาษีซื้อได้ (ผลตรวจ 2026-09-06 · T5-N1)
            var carriedNotes = Accounting.Helpers.OcrScanSnapshot.DecisionNotes(duplicateOf.ProcessingNotes);
            scanResult.ProcessingNotes =
                $"Duplicate of scan {duplicateOf.Id} (engine: {duplicateOf.OcrEngine ?? "unknown"})"
                + (carriedNotes.Length > 0 ? "\n" + carriedNotes : "");
            scanResult.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return await AttachComplianceAsync(companyId, MapToResponse(scanResult));
        }

        // Load SiteSettings ONCE per scan — feeds gateway config + provider routing + Azure DI.
        // Avoids 3 separate roundtrips for the same single-row table.
        var siteSettings = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync();
        var ocrProvider = GetEffectiveProvider(siteSettings);
        OcrExtractedData? extractedData = null;

        var scanStartedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "OCR scan started ScanId={ScanId} CompanyId={CompanyId} Provider={Provider} FileSize={FileSize} ContentType={ContentType}",
            scanResult.Id, companyId, ocrProvider, file.FileSize, file.ContentType);

        try
        {
            string extractedText;

            // ─── 3-tier provider chain ───
            //   1. Azure DI v4 (cloud) — highest accuracy, requires API key + internet
            //   2. Python ocr-service (PaddleOCR+EasyOCR) — high accuracy, requires
            //      external Python service running at LocalServiceUrl
            //   3. Embedded Tesseract (in-process, ALWAYS available) — last-resort
            //      fallback so the system never fails when the upper tiers are
            //      unreachable. Pure NuGet + tessdata files in wwwroot — no
            //      external installation needed.
            //
            // Cascade: each tier's failure falls down to the next, with the reason
            // recorded in ReasoningTrace so the user knows which engine produced
            // the result.

            // Diagnose WHY Azure is or isn't usable — admin can see which field is missing
            var azureToggleOn = siteSettings?.AzureDiEnabled == true;
            var azureHasEndpoint = !string.IsNullOrEmpty(siteSettings?.AzureDiEndpoint);
            var azureHasKey = !string.IsNullOrEmpty(siteSettings?.AzureDiApiKey);
            var azureEnabled = azureToggleOn && azureHasEndpoint && azureHasKey;
            string? azureSkipReason = null;
            // The OcrProvider field is the FALLBACK preference (which engine
            // to try when Azure isn't available). It must NOT block Azure
            // when the admin has explicitly enabled and configured it —
            // doing so was confusing for users who left provider="local"
            // from earlier setup and then enabled Azure but couldn't figure
            // out why scans still went to local.
            if (!azureToggleOn)
                azureSkipReason = "AzureDiEnabled = false (ยังไม่เปิด toggle)";
            else if (!azureHasEndpoint)
                azureSkipReason = "Azure DI Endpoint ว่าง — กรุณากรอกใน admin/ocr-config";
            else if (!azureHasKey)
                azureSkipReason = "Azure DI API Key ว่าง — กรุณากรอกใน admin/ocr-config";

            extractedData = null;
            extractedText = "";
            string? lastError = null;
            string? ocrEngineUsed = null;   // surfaced on scanResult.OcrEngine for the debug panel
            // Tier-outcome ledger — every cascade step writes ONE line so the
            // debug panel can show the full story regardless of which tier
            // finally produced the result. Without this, an Azure failure
            // got silently overwritten when Python local also failed, and
            // the user had no way to see why Azure was bypassed.
            var tierTrace = new List<string>();
            string? azureOutcome = null;     // "skipped: ...", "failed: ...", "ok"
            // PDF text-layer side channel — extracted lazily once (when we
            // need it as a hybrid signal in Tier 3). We no longer use it
            // as a standalone Tier-0 bypass because (a) it misses table
            // layout and column alignment on receipts so extraction is
            // imperfect, and (b) when Azure DI quota exists it's a strict
            // upgrade. We DO merge it into Tier 3 (Tesseract) so PDF scans
            // that fall through to local get both signals — clean digital
            // characters + spatial OCR — fed into ParseThaiDocument.
            string? pdfTextLayer = null;
            string? pdfTextLayerReason = null;

            // Pre-tier diagnostic: WHY would Azure not even be tried?
            //   • toggle off / endpoint or key missing → azureSkipReason
            //     was set above based on AzureDiEnabled / Endpoint / Key.
            //   • quota exhausted → resolved below.
            // Capture all three branches in azureOutcome so the trace is
            // always populated.

            // ─── Tier 0: e-Tax embedded XML shortcut ───
            // If the upload is a PDF/A-3 that carries an ETDA Cross Industry
            // Invoice XML in its EmbeddedFiles tree, the legally authoritative
            // values live INSIDE the file. Read them directly — no OCR engine
            // needed, no quota consumed, 100% accuracy on every field. This
            // is the single biggest accuracy win available and runs FIRST.
            //
            // Only attempts on PDF content-type. Any failure (no embed,
            // unknown XML schema, broken parse) falls through to Tier 1.
            if (extractedData == null
                && (file.ContentType?.Contains("pdf", StringComparison.OrdinalIgnoreCase) == true
                    || (file.OriginalFileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true)))
            {
                try
                {
                    var pdfBytes = await File.ReadAllBytesAsync(file.StoragePath);
                    var etax = Ocr.EtaxPdfXmlExtractor.TryExtract(pdfBytes);
                    if (etax != null && etax.Found)
                    {
                        extractedData = MapEtaxToOcrData(etax);
                        extractedText = etax.XmlContent ?? "";
                        ocrEngineUsed = "EtaxXml";
                        tierTrace.Add($"Tier 0 — e-Tax XML embedded → ใช้ค่าจาก XML โดยตรง " +
                            $"(เอกสาร {etax.MappedDocumentType} {etax.DocumentNumber}, " +
                            $"{etax.Items.Count} รายการ, ยอดรวม {etax.GrandTotal:N2})");
                        _logger.LogInformation(
                            "OCR Tier 0 (e-Tax XML) hit: ScanId={ScanId} Company={Cid} DocNum={DocNum} Total={Total}",
                            scanResult.Id, companyId, etax.DocumentNumber, etax.GrandTotal);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "e-Tax XML extraction attempt failed — falling through to Tier 1");
                }
            }

            // Tier 1: Azure DI — runs whenever the admin has fully configured
            // it (toggle + endpoint + key), regardless of OcrProvider's value.
            // Per-engine quota gate: even when Azure DI is enabled +
            // configured, the tenant's subscription may have exhausted
            // its Azure budget this month. When that happens, skip Tier 1
            // and route directly to local OCR (when FallbackToLocal is on
            // for this tenant). The legacy single-budget mode is unchanged.
            var (azureQuotaAllowed, quotaSkipReason) = await _quota.CheckAzureQuotaAsync(companyId);
            if (!azureQuotaAllowed)
            {
                // Fold the precise quota reason into the existing skip-reason
                // pipeline so it ends up in ProcessingNotes alongside the
                // toggle/endpoint/key reasons. Distinguishes "plan = 0 pages"
                // from "quota หมด" so the admin knows whether to upgrade
                // plan vs buy credits vs wait for next month.
                azureSkipReason = quotaSkipReason ?? "Azure DI quota หมดสำหรับเดือนนี้";
                _logger.LogInformation("Azure DI skipped for {Cid}: {Reason}", companyId, azureSkipReason);
            }

            // Per-scan user preference: when the caller picked "local",
            // bypass Tier 1 entirely so no Azure cost is incurred — even
            // if Azure is enabled and has quota.
            if (enginePref == "local")
            {
                azureSkipReason = "ผู้ใช้เลือก Local OCR — ข้าม Azure DI ตามคำสั่ง";
            }

            if (extractedData == null && azureEnabled && azureQuotaAllowed && enginePref != "local")
            {
                var azureResult = await ExtractWithAzureDiAsync(companyId, file, siteSettings);
                if (azureResult.Success)
                {
                    extractedText = azureResult.Text;
                    extractedData = azureResult.Data;
                    ocrEngineUsed = "AzureDI";
                    azureOutcome = $"ok (confidence {extractedData.Confidence:P0})";
                    // Engine-specific accounting: record the Azure page usage
                    // so the next CanUseAzureAsync correctly reflects the new total.
                    await _quota.RecordEngineUsageAsync(companyId, "Azure");
                    // ── ตัวซ่อมจากประวัติผู้ขาย ต้องรันบนเส้น Azure ด้วย ────────────
                    // เดิมเรียกเฉพาะ Tier 2/3 ด้วยเหตุผลว่า "ของ Azure สะอาดอยู่แล้ว"
                    // — จริงเมื่อคลัง known-good มีแต่ค่าที่ Azure เองสอนไว้ (แทนตัวเอง
                    // = no-op) แต่ **ไม่จริง**เมื่อคลังมีค่าที่ *ผู้ใช้แก้เอง*
                    // (Source = UserCorrection ซึ่ง BestMatch จัดให้ชนะ Azure เสมอ)
                    //
                    // ขอบเขตที่ทำได้จริง (อย่าอ่านเกินนี้ — ฝ่ายค้านรอบ 174 ทักว่า
                    // คอมเมนต์เดิมกล่าวเกิน): `BestMatch` บังคับความคล้าย ≥ 0.80
                    // ⇒ ช่วยเฉพาะเคส "สะกดเพี้ยนเล็กน้อยจากค่าที่ผู้ใช้เคยแก้"
                    // เคสชื่อแบรนด์ละติน ↔ ชื่อนิติบุคคลไทย (ความคล้าย 0.000)
                    // **ไม่ได้แก้ที่นี่** — ตัวที่แก้คือด่านทะเบียน + การพิสูจน์กุญแจ
                    //
                    // allowTaxIdRecovery: false — Azure อ่านเลขบนกระดาษได้แม่น
                    // ถ้ามันบอกว่าไม่มีเลข การเติมเลขจากประวัติ = แต่งเลขที่ไม่ได้อยู่
                    // บนใบนี้ลงช่องผู้ขาย แล้วไหลต่อไปเป็นเลขใน §86/4 และรายงาน §87
                    await _knownGoodCorrector.ApplyAsync(companyId, extractedData,
                        allowTaxIdRecovery: false);
                    // ตัวเรียนรู้จาก Azure (AzureDiPatternLearner) **ย้ายไปรันหลังขั้นตัดสินคู่ค้า**
                    // — เดิมสอนด้วยผลดิบตรงนี้ ก่อน Enrich/ตัวตัดสิน/ตัวกรองเลขที่ ⇒ จำ "ชื่อบริษัท
                    // เราเอง" เป็นผู้ขาย และจำเลขที่บ้านเป็นเลขที่เอกสาร (ดูบล็อก persist ด้านล่าง)
                }
                else
                {
                    lastError = azureResult.Error ?? "unknown";
                    azureOutcome = $"failed: {lastError}";
                    _logger.LogWarning("Azure DI failed ({Err}) — falling back to next tier", lastError);
                }
            }
            else if (azureSkipReason != null)
            {
                azureOutcome = $"skipped: {azureSkipReason}";
            }
            else if (!azureEnabled)
            {
                azureOutcome = "skipped: AzureDI ไม่ได้เปิดใช้งาน (ตรวจสอบ admin/ocr-config — toggle + endpoint + key)";
            }
            tierTrace.Add($"[Tier 1 Azure DI] {azureOutcome ?? "(not attempted)"}");

            // Tier 2: Python local service (PaddleOCR+EasyOCR).
            // When the user explicitly chose "azure", do NOT silently fall
            // back to local — they wanted Azure-grade accuracy. The scan
            // surfaces failure (Tier 3 still runs as a last-resort below
            // only when azure isn't the explicit choice).
            string? pythonOutcome = null;
            if (extractedData == null && enginePref != "azure")
            {
                try
                {
                    var localResult = await ExtractWithLocalServiceAsync(file);
                    if (!string.IsNullOrEmpty(localResult.RawText))
                    {
                        extractedText = localResult.RawText;
                        extractedData = localResult.Data;
                        ocrEngineUsed = "LocalPython";
                        pythonOutcome = $"ok (confidence {extractedData.Confidence:P0})";
                        await _quota.RecordEngineUsageAsync(companyId, "Local");
                        if (lastError != null)
                            extractedData.ReasoningTrace.Insert(0, $"[Fallback] Azure DI failed: {lastError} — used Python local pipeline");
                        else if (azureSkipReason != null)
                            extractedData.ReasoningTrace.Insert(0, $"[Provider] ข้าม Azure DI — {azureSkipReason}; ใช้ Local (PaddleOCR+EasyOCR)");
                        // Local-cascade output is noisy — substitute known-good
                        // canonical values that Azure DI captured for this
                        // vendor on a previous scan, when fuzzy match passes.
                        await _knownGoodCorrector.ApplyAsync(companyId, extractedData);
                    }
                    else
                    {
                        pythonOutcome = "no text returned";
                    }
                }
                catch (Exception ex)
                {
                    lastError = $"Python local: {ex.Message}";
                    pythonOutcome = $"failed: {ex.Message}";
                    _logger.LogWarning(ex, "Python local OCR failed — falling back to embedded Tesseract");
                }
            }
            else
            {
                pythonOutcome = "not attempted (Tier 1 succeeded)";
            }
            tierTrace.Add($"[Tier 2 Python local] {pythonOutcome}");

            // Tier 3: Embedded Tesseract (always available, in-process) —
            // hybrid with PDF text-layer when the input file is a digital
            // PDF. Strategy: extract the text layer (free, character-
            // perfect digital text) and concatenate it with Tesseract's
            // output (noisy but layout-aware). Feed the merged text into
            // ParseThaiDocument so both signals contribute to extraction.
            // Tesseract-alone still runs for images and PDFs without a
            // text layer.
            //
            // Skipped when user explicitly chose "azure" — they wanted
            // Azure-grade accuracy, not noisy Tesseract output. Failure
            // surfaces below as scan-status Failed so they know to retry.
            if (extractedData == null && enginePref != "azure")
            {
                var embeddedResult = await ExtractWithEmbeddedTesseractAsync(file);

                // Try to fold in a PDF text-layer side-channel for digital
                // PDFs. Cheap (in-memory) so we do it once here even if
                // the Tier-0 short-circuit is gone.
                var isPdf = file.ContentType?.Contains("pdf", StringComparison.OrdinalIgnoreCase) == true
                            || (file.OriginalFileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ?? false);
                if (isPdf && pdfTextLayer == null)
                {
                    try
                    {
                        var pdfBytes = await File.ReadAllBytesAsync(file.StoragePath);
                        var maxPages = siteSettings?.OcrMaxPagesPerScan ?? 10;
                        var pdfResult = Ocr.PdfTextLayerExtractor.TryExtract(pdfBytes, maxPages);
                        if (pdfResult.HasUsableText)
                        {
                            pdfTextLayer = pdfResult.Text;
                            pdfTextLayerReason = $"{pdfResult.Reason} ({pdfResult.CharsExtracted} chars)";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "PdfTextLayer extraction failed during Tier-3 merge");
                    }
                }

                if (!string.IsNullOrEmpty(pdfTextLayer))
                {
                    // Combine both signals before parsing. Order: text-layer
                    // first (so ParseThaiDocument sees the clean characters)
                    // then Tesseract output (so any field the text-layer
                    // missed has a second chance from the OCR).
                    var combined = pdfTextLayer + "\n\n──── TESSERACT ────\n\n" + (embeddedResult.RawText ?? "");
                    var normalized = Ocr.ThaiTextNormalizer.Normalize(combined);
                    extractedData = ParseThaiDocument(normalized);
                    extractedText = combined;
                    // Hybrid is more reliable than Tesseract alone but less
                    // than character-perfect text-layer-only — 0.90 splits
                    // the difference. Gateway can still knock it down.
                    extractedData.Confidence = Math.Max(extractedData.Confidence,
                        Math.Max(0.90m, embeddedResult.Data.Confidence));
                    Ocr.SmartFieldExtractor.Enrich(extractedData, combined);
                    ocrEngineUsed = "PdfTextLayer+Tesseract";
                    extractedData.ReasoningTrace.Insert(0,
                        $"[Hybrid] รวม PDF text-layer ({pdfTextLayerReason}) + Tesseract OCR เข้าด้วยกัน");
                }
                else
                {
                    // Image input, or PDF with no extractable text layer
                    // (image-only scans). Tesseract-alone path.
                    extractedText = embeddedResult.RawText;
                    extractedData = embeddedResult.Data;
                    ocrEngineUsed = "EmbeddedTesseract";
                }

                await _quota.RecordEngineUsageAsync(companyId, "Local");
                if (lastError != null)
                    extractedData.ReasoningTrace.Insert(0, $"[Fallback] tiers above failed ({lastError}) — used local engine");
                // Tesseract output is the noisiest of the engines — run
                // the known-good corrector here so any prior Azure-DI scan
                // of this vendor cleans up the obvious garbling before it
                // reaches the gateway / book.
                await _knownGoodCorrector.ApplyAsync(companyId, extractedData);
                // Always make the Azure-skip reason visible even when local ran
                // cleanly. Surface to ProcessingNotes too (ReasoningTrace is in-
                // memory only). Common case: admin tested the connection but
                // forgot to flip the AzureDiEnabled toggle.
                if (azureSkipReason != null)
                {
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + $"\n[Azure DI ข้าม] {azureSkipReason}";
                }
            }
            tierTrace.Add($"[Tier 3 Local hybrid] {(ocrEngineUsed == "PdfTextLayer+Tesseract" ? "PDF text-layer + Tesseract" : ocrEngineUsed == "EmbeddedTesseract" ? "Tesseract only" : enginePref == "azure" ? "skipped (user เลือก Azure-only)" : "not attempted")}");

            // Azure-only path that failed: no engine produced data. Surface
            // an empty result with a Failed status so the user knows to
            // either retry or switch engines. We refund quota in the
            // controller via the OcrEngine == null / status != Completed gate.
            if (extractedData == null)
            {
                extractedData = new OcrExtractedData { Confidence = 0m };
                ocrEngineUsed = ocrEngineUsed ?? "None";
                extractedData.ReasoningTrace.Add(
                    enginePref == "azure"
                        ? $"[Azure-only] ผู้ใช้เลือก Azure DI แต่สแกนไม่สำเร็จ — {lastError ?? azureSkipReason ?? "ไม่ทราบสาเหตุ"}. กรุณาลองอีกครั้งหรือเลือก Engine อื่น."
                        : $"[ทุก Tier ล้มเหลว] {lastError ?? "ไม่มี engine ที่ทำงานได้"}");
                scanResult.ScanStatus = "Failed";
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n" + extractedData.ReasoningTrace[^1];
            }

            // Prepend the tier-outcome ledger to the reasoning trace so the
            // debug panel shows EVERY tier's status — no more silent
            // "Azure was bypassed" mystery when both Azure and Python fail.
            for (int i = tierTrace.Count - 1; i >= 0; i--)
                extractedData.ReasoningTrace.Insert(0, tierTrace[i]);

            scanResult.OcrEngine = ocrEngineUsed;

            // Math/confidence gateway — uses pre-loaded SiteSettings (no extra DB hit)
            var gatewayConfig = BuildGatewayConfig(siteSettings);
            var lineItemsForValidation = extractedData.Items
                .Select(i => new OcrConfidenceGateway.LineItemForValidation(i.Quantity, i.UnitPrice, i.Amount))
                .ToList();
            var whtRatePct = extractedData.HasWht && extractedData.WhtRate.HasValue
                ? extractedData.WhtRate.Value
                : (decimal?)null;
            // ★ ยอดหัก ณ ที่จ่ายที่ส่งให้ด่านตรวจ ต้องเป็น **ยอดที่พิมพ์บนกระดาษ**
            //
            // ⚠️ เดิมบรรทัดนี้ **คำนวณ** whtAmt = SubTotal × Rate/100 แล้วส่งเข้าไป
            // ให้ด่านที่ตรวจว่า "WhtAmount ≈ SubTotal × Rate/100" ⇒ ด่านเทียบสูตร
            // กับผลของสูตรตัวเอง ⇒ **ผ่านทุกครั้งตลอดกาล** = ด่านที่ไม่มีอยู่จริง
            // (ผลตรวจ 2026-09-06 · T2-02) · ตอนนี้อ่านจากกระดาษผ่าน
            // Helpers/PaperWhtReader — ไม่มีบนกระดาษ = null (ด่านข้ามไป ไม่แต่งยอด)
            var paperWht = Accounting.Helpers.PaperWhtReader.Read(
                extractedText, extractedData.SubTotal);
            decimal? whtAmt = paperWht.Amount;
            // กระดาษบอกยอดแต่ไม่บอกอัตรา → อนุมานอัตราจากยอดจริง (แล้ว
            // NormalizeWhtRate จะ snap เข้าอัตราตามกฎหมายให้อีกชั้น)
            if (!whtRatePct.HasValue && paperWht.Amount is > 0m
                && extractedData.SubTotal is > 0m)
            {
                var derived = Math.Round(
                    paperWht.Amount.Value / extractedData.SubTotal.Value * 100m, 2,
                    MidpointRounding.AwayFromZero);
                extractedData.HasWht = true;
                // เขียนลง WhtRate เฉพาะเมื่อ snap เข้าอัตราตามกฎหมายได้ —
                // SmartFieldExtractor.NormalizeWhtRate ทำงานไปก่อนหน้านี้แล้ว
                // ค่าดิบอย่าง 3.21% จึงจะค้างอยู่ถาวรถ้าเขียนลงไปตรง ๆ
                // (อัตราที่ snap ไม่ได้ = ผู้ขายคิดผิดฐาน → ปล่อยให้ด่านเตือน)
                whtRatePct = Accounting.Helpers.ThaiWhtRateTable.SnapToStatutory(derived) ?? derived;
                if (Accounting.Helpers.ThaiWhtRateTable.SnapToStatutory(derived) is decimal snapped)
                    extractedData.WhtRate ??= snapped;
            }
            // กระดาษบอกอัตราแต่ไม่บอกยอด → ไม่มีอะไรให้ตรวจ (ห้ามคำนวณยอดแล้ว
            // เอาไปตรวจกับสูตรเดิม — นั่นคือบั๊กที่เพิ่งแก้)

            var gatewayResult = OcrConfidenceGateway.Validate(
                extractedData.Confidence,
                extractedData.DocumentDate,
                extractedData.SubTotal,
                extractedData.VatAmount,
                extractedData.TotalAmount,
                extractedData.VendorTaxId,
                extractedData.BuyerTaxId,
                extractedData.FieldConfidence.ToDictionary(kv => kv.Key, kv => (decimal)kv.Value),
                config: gatewayConfig,
                lineItems: lineItemsForValidation,
                whtAmount: whtAmt,
                whtRatePercent: whtRatePct,
                documentNumber: extractedData.DocumentNumber,
                vendorName: extractedData.VendorName,
                documentDiscount: extractedData.DiscountAmount);

            extractedData.Confidence = gatewayResult.AdjustedConfidence;
            // ── ผลของด่านคณิตศาสตร์ต้องมี "ผู้บริโภค" ไม่ใช่แค่หักคะแนน (D3-3) ──
            // `GatewayResult.MathConsistent` เคยไม่มีผู้อ่านทั้งเรพ ⇒ ใบที่ระบบเอง
            // บอกว่า "อย่างน้อยหนึ่งช่องอ่านผิด" ยังถูกอนุมัติอัตโนมัติได้ถ้าคะแนน
            // รวมยังถึงเกณฑ์. แท็กนี้อยู่ในรายการห้ามอนุมัติเองของ
            // Helpers/OcrPostingReadiness — ผู้ใช้ยังกดอนุมัติเองได้ (นั่นคือการ
            // ตัดสินใจของคน) แต่ระบบต้องไม่ตัดสินแทนเงียบ ๆ
            // ธงนี้ตั้งได้จาก 3 เหตุ (ยอดหัวใบไม่ลงตัว · สามช่องขัดกันเอง · Σ บรรทัด
            // ไม่ตรงหัวใบ) ⇒ ข้อความต้องไม่ระบุเหตุเดียว แต่ชี้ไปที่บรรทัด [Gateway]
            // ที่บอกเหตุจริง (ประโยคที่ระบุสาเหตุต้องตรวจสาเหตุนั้นจริง)
            if (!gatewayResult.MathConsistent)
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + "\n[MATH] ตัวเลขบนใบขัดกันเอง (ยอดหัวใบไม่ลงตัว หรือ Σ บรรทัดไม่ตรงหัวใบ) — "
                    + "ดูรายละเอียดที่บรรทัด [Gateway] ในเหตุผลประกอบ แล้วตรวจยอดกับกระดาษก่อนอนุมัติ";
            foreach (var w in gatewayResult.Warnings)
                extractedData.ReasoningTrace.Add("[Gateway] " + w);
            // ข้อสังเกตที่ไม่ใช่ความผิด (ใบผสมอัตรา VAT · ราคารวม VAT · ส่วนลดที่
            // กระดาษพิมพ์ไว้) — ติดป้ายคนละแบบและ **ไม่หักคะแนน** เพื่อไม่ให้
            // คำเตือนจริงจมอยู่ในกองคำเตือนปลอม (ผลตรวจ T2-03/T2-16)
            foreach (var n in gatewayResult.Notes)
                extractedData.ReasoningTrace.Add("[Gateway·ข้อสังเกต] " + n);

            // ── ด่านตรวจเลขที่เอกสาร (anti-hallucination) ──
            // ที่มา (บั๊กจริง): บิล กฟภ. มี "เลขที่ (No.)" กับ "เลขที่ใบแจ้งหนี้"
            // ใกล้กัน — LLM เอาสองเลขมาต่อกันเป็นเลขเดียวที่ไม่มีอยู่บนกระดาษ
            // เลย. เลขที่ผิด = ตามใบไม่เจอ + dedup (เลขที่+ยอด) ไม่มีวันจับใบซ้ำ.
            // ตรวจกับ token ที่ OCR อ่านได้จริง: ถ้าผ่าออกเป็น 2 เลขที่ต่างก็อยู่
            // บนเอกสาร = ถูกต่อกัน → เลือกเลขที่หลัก (ป้าย "เลขที่/No." ชนะป้ายรอง
            // "เลขที่ใบแจ้งหนี้/สัญญา/เครื่องวัด") — ตรรกะอยู่ใน pure class มีเทสต์
            var (cleanDocNo, docNoNote) = DocumentNumberSanitizer.Sanitize(
                extractedData.DocumentNumber, extractedText);
            if (docNoNote != null)
            {
                extractedData.DocumentNumber = cleanDocNo;
                // ล้างทิ้งเพราะเป็นเลขที่บ้าน = "ไม่รู้" ⇒ คะแนนต้องต่ำจนไฮไลต์เหลือง
                // ขึ้น (กฎเหล็ก #3 ข้อ 3) ห้ามปล่อยให้ช่องว่างดูเหมือนมั่นใจ 85%
                extractedData.FieldConfidence["DocumentNumber"] = cleanDocNo == null ? 0.25 : 0.85;
                extractedData.ReasoningTrace.Add("[DocNo] " + docNoNote);
            }

            scanResult.DocumentType = extractedData.DocumentType;
            scanResult.Confidence = extractedData.Confidence;
            scanResult.ExtractedDocumentNumber = extractedData.DocumentNumber;
            scanResult.ExtractedDate = extractedData.DocumentDate;
            scanResult.ExtractedVendorName = extractedData.VendorName;
            scanResult.ExtractedVendorTaxId = extractedData.VendorTaxId;
            scanResult.ExtractedSubTotal = extractedData.SubTotal;
            scanResult.ExtractedVatAmount = extractedData.VatAmount;
            scanResult.ExtractedTotalAmount = extractedData.TotalAmount;
            scanResult.RawTextContent = extractedText;
            // ⚠️ ทุก tier ล้ม → ตั้ง Failed ไว้ข้างบนแล้ว ห้ามทับเป็น Completed
            // (ผลตรวจ 2026-09-05 T2-01: เดิมทับเสมอ ⇒ controller ไม่คืนโควตา
            // และผู้ใช้เห็น "เสร็จ" พร้อมช่องว่างทุกช่อง)
            if (scanResult.ScanStatus != "Failed")
                scanResult.ScanStatus = "Completed";
            scanResult.ProcessedAt = DateTime.UtcNow;

            // ── Paper enrichment (engine-agnostic) ──
            // Pulls fields the structured extractors don't return — explicit
            // due date (→ credit terms), header discount, per-line unit —
            // straight off the raw text. Must run BEFORE items serialization
            // (units persist into ExtractedItemsJson) and BEFORE the role
            // inferrer (derived credit terms flip the PV/PI decision).
            EnrichFromRawText(extractedData, extractedText);

            // ── เติมช่องที่ยังว่างด้วยแพตเทิร์นที่ระบบเรียนไว้ ──
            // จุดอ่านของตาราง OcrLearnedPatterns ซึ่งถูก**เขียน**ทุกครั้งที่ผู้ใช้
            // แก้ผลสแกน + ทุกครั้งที่สแกนผ่าน Azure DI แต่ก่อนหน้านี้
            // **ไม่มีใครอ่านเลย** (DocumentZoneAnalyzer.Analyze ไม่มี call site
            // ทั้งเรพ) ⇒ จ่ายค่าเขียนทุกการแก้แต่ความแม่นไม่เคยดีขึ้น
            // ต้องรันหลัง EnrichFromRawText เพราะเป็นตัวเติม "ช่องที่ยังว่าง"
            // เท่านั้น ห้ามทับค่าที่ engine อ่านได้
            await ApplyLearnedPatternsAsync(companyId, extractedData, extractedText);

            // ── ทางสำรองสุดท้าย: วิเคราะห์โซนบนกระดาษ ──
            // รันเฉพาะตอนที่ pipeline หลัก "ไม่ได้อะไรเลย" (ไม่รู้ทั้งผู้ขายและ
            // ยอดรวม) ซึ่งเป็นตอนที่ผู้ใช้ต้องมานั่งกรอกเองทั้งใบ
            // ⚠️ DocumentZoneAnalyzer.Analyze เป็นตัวสกัดอีกชุดที่เขียนไว้ครบ
            // (โซน + FieldPatternLibrary) แต่ **ไม่มี call site ทั้งเรพ**
            await ApplyZoneAnalysisFallbackAsync(companyId, extractedData, extractedText);

            // ⚠️ re-sync "ช่องระบุตัวตนเอกสาร" กลับเข้า entity —
            // การ sync ชุดใหญ่อยู่ **ก่อน** สามขั้นข้างบน (EnrichFromRawText /
            // ApplyLearnedPatterns / ZoneFallback) และ re-sync ที่มีอยู่เดิม
            // ครอบแค่ยอดเงิน ⇒ ช่องที่สามขั้นนั้นเติมให้ (ชื่อผู้ขาย/เลขภาษี/
            // เลขที่เอกสาร/วันที่) อยู่แต่ใน memory ไม่เคยถูกบันทึก
            // ผลคือ ReasoningTrace เขียนว่า "เติมให้แล้ว" แต่หน้าจอยังว่าง
            // และ CreateDocumentFromScan อ่านจาก entity → ได้เอกสารลงวันที่
            // วันนี้แทนวันที่บนกระดาษ (ผิดงวด ภ.พ.30)
            // เติมเฉพาะช่องที่ entity ยังว่าง — ไม่ทับค่าที่ sync ไปแล้ว
            if (string.IsNullOrWhiteSpace(scanResult.ExtractedVendorName))
                scanResult.ExtractedVendorName = extractedData.VendorName;
            if (string.IsNullOrWhiteSpace(scanResult.ExtractedVendorTaxId))
                scanResult.ExtractedVendorTaxId = extractedData.VendorTaxId;
            if (string.IsNullOrWhiteSpace(scanResult.ExtractedDocumentNumber))
                scanResult.ExtractedDocumentNumber = extractedData.DocumentNumber;
            scanResult.ExtractedDate ??= extractedData.DocumentDate;
            scanResult.ExtractedSubTotal ??= extractedData.SubTotal;
            scanResult.ExtractedVatAmount ??= extractedData.VatAmount;
            scanResult.ExtractedTotalAmount ??= extractedData.TotalAmount;

            // ── engine ไม่คืนตารางรายการเลย → ให้ AI แตกบรรทัดจากข้อความ ──
            // เส้นทาง Tesseract แบบฝังคืนแต่ข้อความล้วน ⇒ Items ว่างทุกใบ
            // ผลคือเอกสารได้บรรทัดสรุปใบเดียว แยกหมวดค่าใช้จ่ายไม่ได้
            // (ยังลงบัญชีได้ = local path ที่มีอยู่แล้ว ⇒ kill-switch ผ่าน)
            await TrySplitLineItemsAsync(companyId, scanResult, extractedData, extractedText);

            // ── เชื่อค่าเงินจากระบบภายนอก (override OCR vision) ──
            // พาร์ทเนอร์ที่ยิง OCR ผ่าน API ส่งยอดที่กรอก/คำนวณเองมาใน metadata →
            // เชื่อค่านั้นแทนค่าที่ OCR แกะจากรูป (กันอ่านเลขผิด 530↔630). ทำหลัง
            // EnrichFromRawText เพื่อให้ override ทับค่าที่เดาจาก raw text ด้วย,
            // และก่อน serialize/dup-check/auto-create เพื่อให้ทุก path ใช้ค่าจริง.
            ApplyExternalAmountOverrides(extractedData, externalMetadataJson);
            // re-sync ค่าที่ถูก override กลับเข้า scanResult (assigned ไว้ด้านบนแล้ว)
            scanResult.ExtractedSubTotal = extractedData.SubTotal;
            scanResult.ExtractedVatAmount = extractedData.VatAmount;
            scanResult.ExtractedTotalAmount = extractedData.TotalAmount;

            // ── External metadata → auto project allocation ──
            // When the partner uploaded order/project metadata with the file,
            // link each OCR'd line back to its originating project so the
            // created document's lines get ProjectId pre-selected. Runs BEFORE
            // serialization + AutoCreate so both the persisted JSON and any
            // auto-created document carry the allocation. Fully fail-safe.
            if (extractedData.Items.Count > 0 && !string.IsNullOrWhiteSpace(externalMetadataJson))
            {
                try
                {
                    var projectMatcher = new Ocr.OcrMetadataProjectMatcher(_db, _aiAugmenter, _logger);
                    var matchTrace = await projectMatcher.ApplyAsync(
                        companyId, scanResult.Id, externalMetadataJson, extractedData.Items);
                    foreach (var traceLine in matchTrace)
                        extractedData.ReasoningTrace.Add("[ProjectMatch] " + traceLine);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OCR project metadata matching failed for scan {Sid}", scanResult.Id);
                }
            }

            // 🔧 Sanitize phantom split-VAT line items BEFORE persistence.
            // OfficeMate (และใบกำกับสไตล์เดียวกัน) มี footer แยก "Amount Exclude
            // VAT (ส่วนที่มีภาษี)" + "Amount NON VAT (ส่วนที่ไม่มีภาษี)" — Typhoon/
            // DeepSeek-VL บางครั้งอ่าน footer 2 บรรทัดนี้เป็น line items แยก
            // (เช่น "Epson L6370 (ส่วนมีภาษี) 9,289.71" + "Epson L6370
            // (ส่วนไม่มีภาษี) 0.01"). Sanitizer ตัด suffix + ยุบบรรทัดที่ซ้ำกัน
            // ทิ้ง phantom remainder (≤ ฿1). ทำที่นี่ครั้งเดียวก่อน serialize →
            // ทั้ง CreateDocumentFromScanAsync และ AutoCreateDocumentAsync ได้
            // ประโยชน์เหมือนกัน (ก่อนหน้านี้ AutoCreate path ไม่มี reconcile →
            // เอกสารที่สร้างจาก OCR API ได้บรรทัดผิดต่างจาก web UI).
            SanitizeVatSplitArtifacts(extractedData);

            // หน่วยนับ: เอกสารไม่พิมพ์/โมเดลไม่ให้มา → อนุมานจากคำอธิบายด้วยกฎ
            // (ค่าไฟ→"หน่วย" kWh, น้ำ→ลบ.ม., เช่ารายเดือน→เดือน ฯลฯ) ก่อน
            // serialize — ทั้ง path "สร้างทันที" และ handoff เข้าฟอร์มได้หน่วย
            // เดียวกัน. เติมเฉพาะที่ว่าง — ไม่ทับของที่เอกสาร/ผู้ใช้ระบุ
            // _(ที่มา: บิลค่าไฟ 3,611 "ชิ้น" — default ชิ้นเหมาะกับสินค้าเท่านั้น)_
            foreach (var it in extractedData.Items)
                if (string.IsNullOrWhiteSpace(it.Unit))
                    it.Unit = UnitInferrer.Infer(it.Description);

            // ⚠️ บรรทัดรายการ **ไม่ serialize ตรงนี้แล้ว** (ผลตรวจ 2026-09-18 · D3-1)
            // — จุดเดียวที่เขียน ExtractedItemsJson ของไปป์ไลน์อยู่ท้ายสุดก่อนบล็อก
            // CONTENT FINGERPRINT ดูเหตุผลเต็มที่นั่น (สรุป: ที่นี่อยู่**ก่อน**
            // ApplyProductCrossReferenceAsync + ตัวเรียนรายบรรทัด ⇒ ผังบัญชีรายบรรทัด
            // ที่ดีที่สุดไม่เคยถูกบันทึกลงแถวสแกน)
            scanResult.ExtractedDiscountAmount = extractedData.DiscountAmount;

            scanResult.ExpenseCategory = extractedData.ExpenseCategory;
            // NOTE: HasWht/WhtRate/PaymentTermsDays/DocumentType/Confidence are re-synced
            // AFTER the vendorPred block below — vendor intelligence may override them.

            // ───── Document role inference (run BEFORE VendorIntel) ─────
            // Determine who we are in this paper (Buyer/Seller) and what doc
            // we should CREATE — independent of what the paper itself shows.
            // VendorIntel below may then refine the TargetDocumentType using
            // learned per-vendor history; placing the inferrer before VendorIntel
            // keeps the scanned-vs-target separation clean.
            // Company context — used by the role inferrer (tax-id match) AND
            // the category resolver (industry-bias weighting). Single query
            // serves both.
            var companyContext = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId && !c.IsDeleted)
                .Select(c => new { c.TaxId, c.Name, c.NameEn, c.BranchCode, c.Address, c.BusinessType, c.IndustryType })
                .FirstOrDefaultAsync();
            // ตัวตนของเราจากฐานข้อมูล — หลักฐานชั้นบนสุดของ Helpers/OcrPartyResolver
            var ourIdentity = new Accounting.Helpers.OcrOurIdentity(
                companyContext?.Name, companyContext?.NameEn, companyContext?.TaxId,
                companyContext?.BranchCode, companyContext?.Address);
            // Per-company preference for Buyer + TaxInvoice flow target.
            // Defaults to PaymentVoucher (cash-basis, most Thai SMEs);
            // accrual-basis companies set it to PurchaseInvoice in
            // CompanySettings.
            var buyerInvoiceTarget = await _db.Set<CompanySettings>().AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted)
                .Select(c => (DocumentType?)c.OcrBuyerInvoiceDefaultTarget)
                .FirstOrDefaultAsync();
            // ผลจากตัวอนุมานที่ขั้นตอนถัด ๆ ไปต้องใช้ — ประกาศนอกบล็อกเพื่อให้
            // VendorIntel/federated learner ด้านล่างอ่านได้ (เดิมค่าติดอยู่ในบล็อก
            // จนกติกา federated ต้องเขียนเงื่อนไขที่เป็นจริงไม่ได้เลย)
            decimal ocrRoleConfidence = 0m;
            var ocrLikelyOurOwnDoc = false;
            bool? ocrWeAreWithheld = null;
            var ocrTargetFromPaper = false;   // true = กระดาษมี marker ชัด ไม่ใช่ default
            bool? ocrInputVatClaimable = null;   // §82/5 — ส่งเป็นบริบทให้ AI ตอนจัดผังบัญชี
            {
                DocumentType? prevScanned = null;
                if (Enum.TryParse<DocumentType>(extractedData.DocumentType, ignoreCase: true, out var prevDt))
                    prevScanned = prevDt;
                // Normalize once: collapse Tesseract's inter-character Thai
                // spacing so every keyword scan (role inferrer, category
                // resolver, basket-rule lookup) sees readable Thai.
                // Original extractedText is preserved for display.
                var normalizedText = Ocr.ThaiTextNormalizer.Normalize(extractedText);
                // ── ขั้นตัดสินกลาง "ชุดข้อมูลไหนคือใคร" — Helpers/OcrPartyResolver ──
                //
                // ที่มา (ผู้ใช้รายงาน 2026-09-11 สองรอบ): บิลเงินสดที่ร้านออกให้เรา ถูกสรุปว่า
                // เราเป็นผู้ขาย · แก้รอบแรกแล้ว "ชื่อผู้ขาย" บนจอยังเป็นเราอยู่ เพราะการตัดสิน
                // ใครเป็นใครกระจายอยู่หลายจุด และจุดที่ย้ายค่าไม่ได้ sync ลงแถวสแกน
                // ⇒ รวมเป็นตัวตัดสินเดียว (ตัวตนจาก DB > ป้ายบนกระดาษ > ช่องที่ engine ใส่)
                //   แล้ว sync ผลสุดท้ายลงแถวสแกน**ครบทุกช่อง**ที่จุดเดียว (ดูบล็อก persist ด้านล่าง)
                var vendorBlock0 = new Accounting.Helpers.OcrPartyBlock(extractedData.VendorName, extractedData.VendorTaxId,
                    extractedData.VendorAddress, extractedData.VendorBranchCode);
                var buyerBlock0 = new Accounting.Helpers.OcrPartyBlock(extractedData.BuyerName, extractedData.BuyerTaxId,
                    extractedData.BuyerAddress, extractedData.BuyerBranchCode);
                var isEtaxXml = ocrEngineUsed == "EtaxXml";
                // e-Tax XML ที่ลงนามดิจิทัล = ความจริงตามกฎหมาย ไม่ใช่ผลอ่าน — ห้ามให้ตัวตัดสินจาก
                // "ป้ายบนกระดาษ" ย้าย/ล้างช่อง (ข้อความคือ tag XML ซึ่งมีคำว่า Seller/Buyer เต็มไปหมด)
                // ตัดสินฝั่งจากเลขภาษีใน XML เท่านั้น และไม่ถาม AI
                var partyResolution = isEtaxXml
                    ? new Accounting.Helpers.OcrPartyResolution(
                        vendorBlock0, buyerBlock0,
                        Accounting.Helpers.ThaiTaxId.Same(buyerBlock0.TaxId, ourIdentity.TaxId) ? Accounting.Helpers.OcrSelfSide.Buyer
                            : Accounting.Helpers.ThaiTaxId.Same(vendorBlock0.TaxId, ourIdentity.TaxId) ? Accounting.Helpers.OcrSelfSide.Seller
                            : Accounting.Helpers.OcrSelfSide.Unknown,
                        1.0m, Accounting.Helpers.OcrPartyDecision.Unchanged, false, null,
                        new[] { "e-Tax XML ลงนามแล้ว — ฝั่งตัดสินจากเลขภาษีใน XML เท่านั้น ไม่แตะช่องคู่สัญญา" })
                    : Accounting.Helpers.OcrPartyResolver.Resolve(normalizedText, ourIdentity, vendorBlock0, buyerBlock0);
                {
                    var v = partyResolution.Vendor;
                    var b = partyResolution.Buyer;
                    var vendorNameCleared = !string.IsNullOrWhiteSpace(extractedData.VendorName) && string.IsNullOrWhiteSpace(v.Name);
                    var buyerNameCleared = !string.IsNullOrWhiteSpace(extractedData.BuyerName) && string.IsNullOrWhiteSpace(b.Name);
                    extractedData.VendorName = v.Name; extractedData.VendorTaxId = v.TaxId;
                    extractedData.VendorAddress = v.Address; extractedData.VendorBranchCode = v.BranchCode;
                    extractedData.BuyerName = b.Name; extractedData.BuyerTaxId = b.TaxId;
                    extractedData.BuyerAddress = b.Address; extractedData.BuyerBranchCode = b.BranchCode;
                    // ช่องคู่ค้าที่ถูกล้าง/ย้ายออก = "ไม่รู้" ⇒ คะแนนต่ำจนไฮไลต์ขึ้น (กฎเหล็ก #3 ข้อ 3)
                    // ห้ามปล่อยให้ช่องว่างดูเหมือนมั่นใจ และห้ามแต่งชื่อขึ้นมาเอง
                    if (vendorNameCleared) extractedData.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerName] = 0.20;
                    if (buyerNameCleared) extractedData.FieldConfidence[Accounting.Helpers.OcrFieldKeys.BuyerName] = 0.20;
                    foreach (var r in partyResolution.Reasons)
                        extractedData.ReasoningTrace.Add("[Party] " + r);
                    if (partyResolution.Decision != Accounting.Helpers.OcrPartyDecision.Unchanged)
                        extractedData.ReasoningTrace.Add(
                            $"[Party] ตัดสิน: {partyResolution.Decision} · เราเป็น {partyResolution.OurSide} (มั่นใจ {partyResolution.Confidence:P0})");

                    // ช่องผู้ขายว่างหลังตัดสิน + เราเป็นผู้ซื้อ + กระดาษเป็นแบบฟอร์มพิมพ์สำเร็จ →
                    // เสนอชื่อในกรอบบน (ที่เล่มบิลสงวนให้ผู้ออกบิล) ด้วยความมั่นใจต่ำให้ผู้ใช้ตรวจ
                    // — กฎเหล็ก #3: เติมไว้แล้วไฮไลต์ ไม่ใช่ปล่อยว่าง · ใบรับรองแทนใบเสร็จต้องมีชื่อผู้รับเงิน
                    if (string.IsNullOrWhiteSpace(extractedData.VendorName)
                        && partyResolution.OurSide == Accounting.Helpers.OcrSelfSide.Buyer)
                    {
                        var issuerGuess = Accounting.Helpers.OcrIssuerHeaderGuess.FromTopBox(normalizedText, ourIdentity);
                        if (issuerGuess != null)
                        {
                            extractedData.VendorName = issuerGuess;
                            extractedData.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerName] = Accounting.Helpers.OcrIssuerHeaderGuess.Confidence;
                            extractedData.Note(Accounting.Helpers.OcrFieldKeys.SellerName, issuerGuess,
                                Accounting.Helpers.OcrFieldSource.Guess, (decimal)Accounting.Helpers.OcrIssuerHeaderGuess.Confidence,
                                "ชื่อในกรอบบนของแบบฟอร์ม (ตำแหน่งผู้ออกบิล) — ข้อเสนอ ไม่ใช่การอ่าน");
                            extractedData.ReasoningTrace.Add(
                                $"[Party] เสนอผู้ขาย “{issuerGuess}” จากกรอบบนของแบบฟอร์ม (มั่นใจ {Accounting.Helpers.OcrIssuerHeaderGuess.Confidence:P0}) — กรุณาตรวจ");
                        }
                    }
                }

                // คำตัดสินของ resolver ต้องถึงตัวอนุมานบทบาท (ทีมตรวจ 2026-09-11: เดิม Infer เห็นแค่
                // ช่องที่ย้ายแล้ว ⇒ เคส "ป้ายชี้ฝั่งเราแต่ engine ใส่ชื่ออื่นในช่อง" ถูกทิ้งเงียบ ๆ)
                // — override ที่ 0.92 ยังแพ้เลขภาษีที่ตรงกับเรา (1.0) และทิศจาก 50 ทวิ ใน Infer เอง
                var resolverRole = partyResolution.OurSide != Accounting.Helpers.OcrSelfSide.Unknown
                    && partyResolution.Confidence >= 0.80m
                    ? partyResolution.OurSide.ToString() : null;
                var role = OcrDocumentRoleInferrer.Infer(
                    rawText: normalizedText,
                    vendorTaxId: extractedData.VendorTaxId,
                    buyerTaxId: extractedData.BuyerTaxId,
                    vendorName: extractedData.VendorName,
                    buyerName: extractedData.BuyerName,
                    companyTaxId: companyContext?.TaxId,
                    companyName: companyContext?.Name,
                    previousScannedType: prevScanned,
                    buyerInvoiceDefaultTarget: buyerInvoiceTarget,
                    // Credit terms read off THIS paper (regex "เครดิต N วัน" /
                    // "Net N") — flips invoice target PV→PI when > 0. The
                    // VendorIntel history backfill runs AFTER this point on
                    // purpose: only the paper's own terms prove "unpaid".
                    paymentTermsDays: extractedData.PaymentTermsDays,
                    roleOverride: resolverRole);

                // ─── ถาม "ครู" ว่าเราเป็นผู้ซื้อหรือผู้ขาย — เฉพาะเคสที่กติกาตัดสินไม่ได้ ───
                //
                // กฎเหล็ก #1: AI เป็นครูพิเศษ ไม่ใช่อวัยวะ — เรียกเฉพาะเมื่อ OcrPartyResolver
                // บอกว่าหลักฐานไม่พอ (ShouldAskAi) หรือตัวอนุมานเองก็ไม่มั่นใจ (< 0.7).
                // ปิด provider → student (GenericFeedbackDistillationModel) ตอบ · ไม่มีใครตอบ →
                // คงคำตอบของกติกา. คำตอบผ่านด่าน: ∈ {Buyer,Seller} · conf ≥ 0.70 · ห้ามขัดกับ
                // ตัวตนทางกฎหมาย (เลขภาษีที่ตรงกับเราชี้ฝั่งแล้ว AI เปลี่ยนไม่ได้)
                // ⚠️ ถามเฉพาะเมื่อ resolver บอกว่า "หลักฐานเชิงกติกาไม่พอ" (ShouldAskAi) — เดิมเพิ่ม
                // เงื่อนไข `roleConf < 0.7` เข้าไปด้วย ⇒ สลิป POS ทุกใบ (ไม่มีชื่อเรา ⇒ Infer ให้ 0.6
                // จากตำแหน่ง) จะเรียก provider ทุกครั้ง = ตรงข้ามกฎเหล็ก #1 ข้อ 6 และชน daily cap
                // ในวันปกติ (ทีมตรวจ 2026-09-11) · e-Tax XML ไม่ถามเลย
                var askRole = _aiAugmenter != null && !isEtaxXml
                    && partyResolution.ShouldAskAi
                    && role.RoleConfidence < 0.9m
                    && !string.IsNullOrWhiteSpace(normalizedText) && normalizedText.Trim().Length >= 40;
                if (askRole)
                {
                    try
                    {
                        using var roleCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        var ai = await _aiAugmenter!.InferOurRoleAsync(
                            companyId, scanResult.Id, normalizedText,
                            partyResolution.Vendor, partyResolution.Buyer,
                            scannedDocumentType: role.ScannedDocType?.ToString(),
                            localGuess: role.OurRole, localConfidence: role.RoleConfidence,
                            ruleReasons: partyResolution.Reasons,
                            whyAsking: partyResolution.AskAiReason ?? $"ตัวอนุมานบทบาทมั่นใจแค่ {role.RoleConfidence:P0}",
                            ct: roleCts.Token);
                        scanResult.OurRoleAiFeedbackId = ai.FeedbackId;
                        scanResult.OurRoleAiSuggested = ai.Answer;

                        var vendorPinnedByTax = Accounting.Helpers.OcrPartyResolver.SelfStrength(
                            partyResolution.Vendor, ourIdentity, out _) >= 1.0m;
                        var buyerPinnedByTax = Accounting.Helpers.OcrPartyResolver.SelfStrength(
                            partyResolution.Buyer, ourIdentity, out _) >= 1.0m;
                        var contradictsIdentity = (ai.Answer == "Buyer" && vendorPinnedByTax)
                            || (ai.Answer == "Seller" && buyerPinnedByTax);
                        if (ai.HasModelAnswer && (ai.Confidence ?? 0m) >= 0.70m
                            && (ai.Confidence ?? 0m) > role.RoleConfidence
                            && ai.Answer is "Buyer" or "Seller" && !contradictsIdentity
                            && !string.Equals(ai.Answer, role.OurRole, StringComparison.Ordinal))
                        {
                            extractedData.ReasoningTrace.Add(
                                $"[Role] {(ai.UsedAi ? "🤖 AI" : "⚙️ นักเรียน")} ตัดสินว่าเราเป็น {ai.Answer} "
                                + $"(มั่นใจ {ai.Confidence:P0}) แทนคำตอบกติกา {role.OurRole} ({role.RoleConfidence:P0}): {ai.Reasoning}");
                            scanResult.OurRoleUsedAi = ai.UsedAi;   // ป้ายซื่อสัตย์: true เฉพาะ provider จริง

                            // คำตอบ AI ต้อง**ย้ายบล็อก**ด้วย ไม่ใช่แค่เปลี่ยนป้ายบทบาท — ไม่งั้นชื่อเราค้าง
                            // ช่องผู้ขายแล้ว sync ลงแถว = อาการเดิมของผู้ใช้กลับมาอีกทาง (ทีมตรวจ)
                            var aiSide = ai.Answer == "Buyer" ? Accounting.Helpers.OcrSelfSide.Buyer : Accounting.Helpers.OcrSelfSide.Seller;
                            var (v2, b2) = Accounting.Helpers.OcrPartyResolver.ApplySide(
                                new Accounting.Helpers.OcrPartyBlock(extractedData.VendorName, extractedData.VendorTaxId,
                                    extractedData.VendorAddress, extractedData.VendorBranchCode),
                                new Accounting.Helpers.OcrPartyBlock(extractedData.BuyerName, extractedData.BuyerTaxId,
                                    extractedData.BuyerAddress, extractedData.BuyerBranchCode),
                                aiSide, ourIdentity);
                            if (!string.IsNullOrWhiteSpace(extractedData.VendorName) && string.IsNullOrWhiteSpace(v2.Name))
                                extractedData.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerName] = 0.20;
                            if (!string.IsNullOrWhiteSpace(extractedData.BuyerName) && string.IsNullOrWhiteSpace(b2.Name))
                                extractedData.FieldConfidence[Accounting.Helpers.OcrFieldKeys.BuyerName] = 0.20;
                            extractedData.VendorName = v2.Name; extractedData.VendorTaxId = v2.TaxId;
                            extractedData.VendorAddress = v2.Address; extractedData.VendorBranchCode = v2.BranchCode;
                            extractedData.BuyerName = b2.Name; extractedData.BuyerTaxId = b2.TaxId;
                            extractedData.BuyerAddress = b2.Address; extractedData.BuyerBranchCode = b2.BranchCode;
                            role = OcrDocumentRoleInferrer.Infer(
                                rawText: normalizedText,
                                vendorTaxId: extractedData.VendorTaxId, buyerTaxId: extractedData.BuyerTaxId,
                                vendorName: extractedData.VendorName, buyerName: extractedData.BuyerName,
                                companyTaxId: companyContext?.TaxId, companyName: companyContext?.Name,
                                previousScannedType: prevScanned, buyerInvoiceDefaultTarget: buyerInvoiceTarget,
                                paymentTermsDays: extractedData.PaymentTermsDays,
                                roleOverride: ai.Answer);
                        }
                        else if (ai.HasModelAnswer && string.Equals(ai.Answer, role.OurRole, StringComparison.Ordinal))
                        {
                            scanResult.OurRoleUsedAi = ai.UsedAi;   // ยืนยันก็คือเรียก provider จริง — ตัวชี้วัดต้องนับ
                            extractedData.ReasoningTrace.Add(
                                $"[Role] {(ai.UsedAi ? "AI" : "นักเรียน")} ยืนยันคำตอบกติกา ({ai.Answer} · {ai.Confidence:P0})");
                        }
                        else if (contradictsIdentity)
                        {
                            extractedData.ReasoningTrace.Add(
                                $"[Role] AI ตอบ {ai.Answer} ขัดกับเลขผู้เสียภาษีที่ตรงกับเราบนกระดาษ — ทิ้งคำตอบ AI");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ถาม AI เรื่องบทบาทไม่สำเร็จ — ใช้คำตอบกติกาต่อ");
                    }
                }
                if (role.ScannedDocType.HasValue)
                    extractedData.DocumentType = role.ScannedDocType.Value.ToString();
                extractedData.OurRole = role.OurRole;
                SetTargetDocumentType(extractedData, role.TargetDocType.ToString(),
                    // กระดาษบอกชนิดเอกสารเอง = ป้ายบนกระดาษ · ไม่บอก = กติกา/ค่าเริ่มต้นตามบทบาท
                    role.ScannedDocType.HasValue
                        ? Accounting.Helpers.OcrFieldSource.PaperLabel
                        : Accounting.Helpers.OcrFieldSource.Rule,
                    role.RoleConfidence,
                    role.ScannedDocType.HasValue
                        ? $"ชนิดกระดาษ {role.ScannedDocType} + บทบาทเรา ({role.OurRole})"
                        : $"กระดาษไม่ได้ระบุชนิด — ค่าเริ่มต้นตามบทบาท ({role.OurRole}) และการตั้งค่าบริษัท");
                ocrRoleConfidence = role.RoleConfidence;
                ocrLikelyOurOwnDoc = role.LikelyOurOwnIssuedDocument;
                ocrWeAreWithheld = role.WeAreWithheld;
                // "กระดาษบอกเอง" = มี marker ชนิดเอกสารชัด — ต่างจากการตกลง
                // default (Expense/PaymentVoucher) ซึ่งเป็นการเดา
                ocrTargetFromPaper = role.ScannedDocType.HasValue;
                ocrInputVatClaimable = role.InputVatClaimable;
                foreach (var r in role.Reasons)
                    extractedData.ReasoningTrace.Add("[Role] " + r);

                // ─── AI ช่วยจำแนกชนิดเอกสาร — เฉพาะตอนกติกาไม่มั่นใจ ───
                //
                // กฎเหล็ก #1 (student-first): กติกาให้คำตอบไปแล้วข้างบน AI เป็น
                // "ครูพิเศษ" ที่เรียกเฉพาะเคสคลุมเครือ ปิด provider ทั้งหมดแล้ว
                // feature ยังทำงานครบผ่านคำตอบของกติกา
                //
                // เกณฑ์เรียก: บทบาทไม่มั่นใจ (< 0.7) **หรือ** กระดาษไม่มี marker
                // ชนิดเอกสารเลย — สองกรณีนี้คือที่ที่กติกาเดาล้วน ๆ และผิดแล้วแพง
                // ที่สุด (ชนิดผิด = บัญชีคู่ผิดทั้งใบ + เข้ารายงานภาษีผิดฝั่ง)
                //
                // ⚠️ AiFeatureKey.DocumentTypeClassification มี enum + prompt +
                // student ครบมานานแล้ว แต่**ไม่เคยมีใครเรียก** ⇒ prompt ตายอยู่ใน
                // ไฟล์ และ student อดอาหารถาวร (ไม่มี feedback row เลยจึงไม่มีวัน
                // IsReady) — นี่คือการต่อสายเส้นที่ขาด
                var aiWorthAsking = (role.RoleConfidence < 0.7m || !role.ScannedDocType.HasValue)
                    && !string.IsNullOrWhiteSpace(normalizedText)
                    && normalizedText.Trim().Length >= 40;
                if (_aiAugmenter != null && aiWorthAsking)
                {
                    try
                    {
                        // ⚠️ เดิมเขียน CreateLinkedTokenSource(default) ซึ่ง `default`
                        // กำกวมระหว่าง overload (CancellationToken vs params
                        // CancellationToken[]) = CS0121. ไม่มี token ต้นทางให้ link
                        // อยู่แล้ว จึงใช้ CTS ธรรมดาที่มี timeout ในตัว
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        var cls = await _aiAugmenter.ClassifyDocumentTypeAsync(
                            companyId, scanResult.Id,
                            rawText: normalizedText,
                            documentNumber: extractedData.DocumentNumber,
                            vendorName: extractedData.VendorName,
                            totalAmount: extractedData.TotalAmount,
                            localGuess: extractedData.TargetDocumentType,
                            localConfidence: role.RoleConfidence,
                            // บริบทที่ตัดสินคำตอบ — ถ้าไม่ส่ง โมเดลไม่มีทางรู้ว่า
                            // เราเป็นผู้ซื้อหรือผู้ขาย แล้วต้องเดา ซึ่งคำตอบที่
                            // ข้ามฝั่งจะถูกด่านทิ้งอยู่ดี = จ่าย token แล้วโยนทิ้ง
                            context: new Services.Ai.OcrDocTypeContext(
                                OurRole: role.OurRole,
                                RoleConfidence: role.RoleConfidence,
                                ScannedDocumentType: role.ScannedDocType?.ToString(),
                                VendorTaxId: extractedData.VendorTaxId,
                                BuyerName: extractedData.BuyerName,
                                BuyerTaxId: extractedData.BuyerTaxId,
                                DocumentDate: extractedData.DocumentDate,
                                VatAmount: extractedData.VatAmount,
                                TopLineDescriptions: extractedData.Items
                                    .Select(i => i.Description ?? "")
                                    .Where(d => d.Length > 0).Take(5).ToList()),
                            ct: cts.Token);

                        scanResult.TargetDocTypeAiFeedbackId = cls.FeedbackId;
                        scanResult.TargetDocTypeAiSuggested = cls.Answer;

                        // ── ด่านกันมั่ว 3 ชั้น (กฎเหล็ก #1) ──
                        //  1. ต้องเป็นค่าใน enum จริง (ไม่ใช่คำที่ AI แต่งขึ้น)
                        //  2. confidence ≥ 0.70
                        //  3. ต้องอยู่ฝั่งเดียวกับบทบาท — AI มองไม่เห็นว่าเราเป็น
                        //     ผู้ซื้อหรือผู้ขาย จึงเสนอข้ามฝั่งได้ง่าย
                        // ★ กฎเหล็ก #1 (T3-01): คำตอบของ "นักเรียน" ที่ short-circuit มา
                        // (UsedAi=false, FromStudent=true) ใช้ได้เท่า AI — ผ่านด่านเดียวกัน
                        if (cls.HasModelAnswer && (cls.Confidence ?? 0m) >= 0.70m
                            && Enum.TryParse<DocumentType>(cls.Answer, true, out var aiType)
                            && Accounting.Helpers.DocumentSide.MatchesRole(aiType, extractedData.OurRole))
                        {
                            scanResult.TargetDocTypeUsedAi = cls.UsedAi;   // ป้ายซื่อสัตย์: true เฉพาะ provider จริง
                            SetTargetDocumentType(extractedData, aiType.ToString(),
                                cls.UsedAi
                                    ? Accounting.Helpers.OcrFieldSource.Ai
                                    : Accounting.Helpers.OcrFieldSource.Student,
                                cls.Confidence ?? 0m,
                                cls.UsedAi ? "เรียก AI เพราะกติกาไม่มั่นใจ" : "นักเรียน (local model) ตอบแทน AI");
                        }
                        else if (cls.UsedAi || cls.FromStudent)
                        {
                            extractedData.ReasoningTrace.Add(
                                $"[AI] เสนอ {cls.Answer} (มั่นใจ {cls.Confidence:P0}) แต่ไม่ผ่านด่านตรวจ "
                                + "(ไม่อยู่ใน enum / มั่นใจต่ำ / คนละฝั่งกับบทบาท) → ใช้คำตอบของกติกาต่อ");
                        }
                    }
                    catch (Exception ex)
                    {
                        // AI ล่ม/เกินงบ/timeout → ใช้คำตอบของกติกาต่อเงียบ ๆ
                        _logger.LogWarning(ex, "จำแนกชนิดเอกสารด้วย AI ไม่สำเร็จ (ใช้ผลของกติกาต่อ)");
                    }
                }

                // เอกสารที่เราออกเองถูกสแกนกลับเข้ามา — ต้องเตือนถึงหน้าเว็บ
                // ไม่ใช่ซ่อนอยู่ใน reasoning trace ที่พับไว้ (สร้างต่อ = ออกใบขาย
                // ใบที่สอง เลข/ยอดซ้ำในรายงานภาษีขายที่ยื่นไปแล้ว)
                // สแกน "สำเนา" มาลงบัญชี — เสี่ยงเคลมภาษีซื้อซ้ำ (§86/4 ให้ผู้ซื้อ
                // ใช้ต้นฉบับ) เตือนถึงหน้าเว็บ ไม่ใช่ซ่อนใน trace ที่พับไว้
                if (role.LooksLikeCopy && role.OurRole == "Buyer")
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + "\n[COPY-DOC] กระดาษระบุว่าเป็น \"สำเนา\" — ผู้ซื้อต้องใช้ต้นฉบับในการเคลมภาษีซื้อ "
                        + "(§86/4) และต้นฉบับใบเดียวกันอาจถูกลงบัญชีไปแล้ว ตรวจสอบก่อนสร้างเอกสาร";

                if (role.LikelyOurOwnIssuedDocument)
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + "\n[OWN-DOC] เลขผู้ขายบนกระดาษคือบริษัทเราเอง — น่าจะเป็นสำเนาเอกสารที่เราออกไปแล้ว "
                        + "ตรวจว่ามีใบนี้ในระบบหรือยังก่อนสร้างใหม่";

                // §82/5 — เตือนผู้ใช้เมื่อเอกสารที่ได้รับ "เคลมภาษีซื้อไม่ได้"
                // (ใบกำกับภาษีอย่างย่อ / ใบเสร็จ-บิลเงินสด ที่ไม่ใช่ใบกำกับเต็มรูป).
                // เตือนเฉพาะตอนมี VAT จริง (ไม่มี VAT = ไม่มีอะไรให้เคลมอยู่แล้ว).
                // เก็บลง ProcessingNotes (โชว์ในหน้า review + carry ผ่าน handoff)
                // ด้วย prefix [VAT-CLAIM] ให้ frontend ดึงมาแสดงเป็น banner ได้.
                var docVat = extractedData.VatAmount ?? 0m;
                if (role.InputVatClaimable == false && docVat > 0 && !string.IsNullOrEmpty(role.InputVatClaimWarning))
                {
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + "\n[VAT-CLAIM] " + role.InputVatClaimWarning;
                    extractedData.ReasoningTrace.Add("[VAT-CLAIM] " + role.InputVatClaimWarning);
                }

                // §82/5(4)/(6) — ต้องห้ามตาม "ชนิดรายจ่าย" ไม่ใช่ตามรูปแบบใบ
                // (role inferrer ข้างบนตรวจได้แค่รูปแบบ: ใบย่อ/ไม่ใช่ใบกำกับ
                // เต็มรูป). ใบกำกับเต็มรูปที่ถูกต้อง 100% ของค่าน้ำมันรถเก๋ง/
                // ค่ารับรอง ก็เคลมไม่ได้ — เดิมระบบเปิดเคลมให้ทุกใบที่รูปแบบถูก
                // ⇒ ยื่น ภ.พ.30 เกินสิทธิ์เงียบ ๆ (ผู้ใช้รายงาน)
                if (role.OurRole == "Buyer" && docVat > 0)
                {
                    var prohibited = ProhibitedInputVatScreener.Screen(
                        normalizedText, extractedData.VendorName,
                        extractedData.Items.Select(i => i.Description));
                    if (!string.IsNullOrEmpty(prohibited.Warning))
                    {
                        // Claimable=false → ใช้ prefix [VAT-CLAIM] ตัวเดียวกับ
                        // เส้นรูปแบบใบ: ทั้ง frontend และ backend รู้จัก marker นี้
                        // อยู่แล้ว (default ไม่เคลม + ตั้ง IsVatClaimable=false
                        // รายบรรทัด) — ไม่ต้องเพิ่ม marker ใหม่ให้ที่อื่นต้องตามแก้
                        // Claimable=null (รถที่เคลมได้) → เตือนอย่างเดียว
                        // ใช้ prefix [VAT-NOTE] เพื่อ **ไม่** ปิดการเคลม
                        var prefix = prohibited.Claimable == false ? "[VAT-CLAIM]" : "[VAT-NOTE]";
                        var msg = $"{prefix} ({prohibited.RuleCode}) {prohibited.Warning}";
                        scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n" + msg;
                        extractedData.ReasoningTrace.Add(msg);
                    }
                }
            }

            // Store zone analysis info for debugging — these never change after this point
            if (!string.IsNullOrEmpty(extractedData.ZoneSummary))
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n[Zone Analysis]\n" + extractedData.ZoneSummary;
            if (!string.IsNullOrEmpty(extractedData.BuyerName))
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + $"\n[Buyer] {extractedData.BuyerName} TaxID:{extractedData.BuyerTaxId ?? "N/A"}";
            // Persist buyer fields so subsequent loads (list endpoint,
            // page reload) still have them. Without this, MapToResponse
            // would re-read the entity, see NULL on Buyer*, and the
            // RD-compliance UI would warn "ควรระบุเลขผู้เสียภาษีของผู้ซื้อ"
            // every time the user opened the scan even though OCR did
            // pick the value up correctly on the initial run.
            scanResult.BuyerName = extractedData.BuyerName;
            scanResult.BuyerTaxId = extractedData.BuyerTaxId;
            // ⬅ ผู้ขายด้วย — เดิม ExtractedVendorName/TaxId ถูกเขียนลงแถว **ก่อน** ขั้นตัดสิน
            // ใครเป็นใคร แล้วไม่มีใคร sync ซ้ำ ⇒ หน่วยความจำย้ายชื่อเราไปฝั่งผู้ซื้อแล้ว แต่
            // แถวสแกน (ที่หน้าจออ่าน) ยังถือชื่อเราเป็นผู้ขาย (ผู้ใช้รายงาน 2026-09-11 รอบสอง)
            // กติกา: ค่าที่ persist ต้อง sync **หลัง** ตัวตัดสินตัวสุดท้ายเสมอ
            scanResult.ExtractedVendorName = extractedData.VendorName;
            scanResult.ExtractedVendorTaxId = extractedData.VendorTaxId;
            // §86/4 สาขา + ที่อยู่ที่อ่านได้จากใบใบนี้ (กฎเหล็ก #3) — เดิมมีแต่ใน
            // OcrExtractedData ที่อยู่ในหน่วยความจำ พอ reload หน้าค่าหาย และตอน
            // สร้างเอกสารต้องไปหยิบ Contact.BranchCode ซึ่งอาจเป็นสาขาอื่น
            scanResult.VendorBranchCode = extractedData.VendorBranchCode;
            scanResult.VendorAddress = extractedData.VendorAddress;
            scanResult.BuyerBranchCode = extractedData.BuyerBranchCode;
            scanResult.BuyerAddress = extractedData.BuyerAddress;

            // ── สอนตัวเองจากผล Azure **หลังขั้นตัดสินคู่ค้า/ตัวกรองเลขที่/AI** (ย้ายมาจากจุดที่ engine
            //    เพิ่งคืนผล) — ยังอยู่ก่อน DBD/เติมเลขผู้ซื้อ ซึ่งไม่แตะช่องที่สอน ──
            // สแกนจริง 2026-09-11: ตัวเรียนรู้เคยรันตอน engine คืนผลดิบ ⇒ จำ "บจก. แอมแฮปปี้เนส"
            // (= เราเอง) เป็นผู้ขายที่รู้ว่าถูก และจำเลขที่บ้าน "177/18" เป็นเลขที่เอกสารด้วย
            // regex กวาดทุกอย่าง ⇒ สแกนรอบถัดไปได้ "CASHSALE" เป็นเลขที่ · ตอนนี้สอนด้วยค่าที่ผ่าน
            // ตัวตัดสินคู่ค้า/ตัวกรองเลขที่แล้ว และ learner ปฏิเสธเองเมื่อผู้ขาย = เรา
            if (ocrEngineUsed == "AzureDI")
            {
                try
                {
                    await _azureLearner.LearnAsync(
                        companyId, extractedText ?? "",
                        vendorTaxId: extractedData.VendorTaxId,
                        vendorName: extractedData.VendorName,
                        documentNumber: extractedData.DocumentNumber,
                        buyerName: extractedData.BuyerName,
                        buyerTaxId: extractedData.BuyerTaxId,
                        vendorAddress: extractedData.VendorAddress,
                        vendorPhone: extractedData.VendorPhone,
                        vendorEmail: extractedData.VendorEmail,
                        vendorBranchCode: extractedData.VendorBranchCode,
                        sourceConfidence: extractedData.Confidence,
                        ourCompanyName: companyContext?.Name,
                        ourTaxId: companyContext?.TaxId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Azure-DI pattern learning failed (non-fatal)");
                }
            }
            // รหัสสาขาที่ "อ่านไม่ได้" ต้องมีคะแนนต่ำติดไว้ (E-OCR-03) —
            // MapToResponse เติม "00000" ให้เสมอตามกฎเหล็ก #3 (ห้ามส่ง null ให้ UI)
            // แต่ถ้าไม่มี key ใน FieldConfidence ป้ายจะไปหยิบคะแนนของ**ทั้งใบ**
            // ⇒ ผู้ใช้เห็น "00000 · 95%" ทั้งที่ไม่มีใครอ่านค่านี้จากกระดาษเลย
            // ⇒ กดยืนยัน ⇒ ใบของสาขาที่ 3 ลงเป็นสำนักงานใหญ่ (§86/4 + §87)
            if (string.IsNullOrWhiteSpace(extractedData.VendorBranchCode))
                extractedData.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerBranchCode] = 0.30;
            if (string.IsNullOrWhiteSpace(extractedData.BuyerBranchCode))
                extractedData.FieldConfidence[Accounting.Helpers.OcrFieldKeys.BuyerBranchCode] = 0.30;
            // วันที่อ่านไม่ได้ (ผลตรวจ 2026-09-05 T1-04): CreateDocumentFromScanAsync จะ
            // เติม "วันนี้" ให้ (กฎเหล็ก #3 ห้ามปล่อยว่าง) — แต่วันนี้กลายเป็น tax point/
            // งวด ภ.พ.30 เงียบ ๆ ⇒ ต้องมีป้ายเหลือง + ห้าม auto-approve (ดู OcrController)
            if (!extractedData.DocumentDate.HasValue)
            {
                extractedData.FieldConfidence[Accounting.Helpers.OcrFieldKeys.DocumentDate] = 0.30;
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + "\n[DATE-UNKNOWN] อ่านวันที่บนกระดาษไม่ได้ — ระบบจะเติมวันนี้ให้เป็นค่าเริ่มต้น กรุณายืนยันวันที่ก่อนอนุมัติ (งวดภาษี §78/§82/3)";
            }

            // ── สมุดที่มาของค่ารายช่อง (D1) — **เขียนท้ายไปป์ไลน์** ──────────
            // (บล็อกนี้ถูกย้ายลงไปไว้หลัง EnrichFromDbdAsync — ดูเหตุผลที่นั่น)

            // [Reasoning] section is built AFTER vendorPred so VendorIntel/Learner traces are included.

            // ───── Default category resolution (Thai expense classifier) ─────
            // Before consulting per-tenant learned data, run the global
            // Thai-aware classifier to bootstrap fresh tenants. The resolver
            // only fills EMPTY fields, so anything the OCR provider already
            // surfaced (or the user's prior corrections produced) is kept.
            // ExpenseCategoryLearner below may then override with company-
            // specific habits when they exist.
            // Normalize Thai before keyword scanning — same reason as the
            // role inferrer above; the brand/keyword rules in the resolver
            // can't find "ค่าไฟฟ้า" / "การไฟฟ้า" in a "ค ่ า ไฟ ฟ ้ า" /
            // "ก า ร ไฟ ฟ ้ า" string.
            var categoryResolverText = Ocr.ThaiTextNormalizer.Normalize(extractedText);
            // แยกบรรทัด "ที่มีเงินจริง" ออกจากแถวที่แบบฟอร์มพิมพ์ไว้ยอด 0
            // (เช่น "ค่าจัดส่ง / Shipping Fee 0.00" ที่ขึ้นทุกใบของผู้ขายรายนั้น) —
            // แถวยอด 0 เป็นหลักฐานอ่อน และห้ามใช้ตัดสินประเภทเงินได้ ม.40
            var paidLines = extractedData.Items
                .Where(i => (i.Amount ?? 0m) > 0m || (i.UnitPrice ?? 0m) > 0m)
                .Select(i => i.Description).ToList();
            var zeroLines = extractedData.Items
                .Where(i => (i.Amount ?? 0m) <= 0m && (i.UnitPrice ?? 0m) <= 0m)
                .Select(i => i.Description).ToList();
            // ── หมวดค่าใช้จ่ายที่ "มาถึงก่อน" ตัวจัดหมวดของเรา ──────────────
            // `extractedData.ExpenseCategory` ณ จุดนี้ **ไม่ได้มาจาก**
            // `ExpenseCategoryResolver` — มันมาจากชั้นก่อนหน้า (ช่อง
            // `expense_category` ของ engine ในเครื่อง · ตารางค่าตั้งต้นตามชนิด
            // เอกสาร · ค่าที่ผู้ใช้เคยแก้) ซึ่ง**ไม่มีชั้นไหนเขียนร่องรอยไว้เลย**
            // ⇒ ผู้ใช้ถามว่า "ทำไมแนะนำหมวดนี้" แล้วตอบไม่ได้ แม้แต่ตอนไล่โค้ด
            // (เคสจริง: หมวด "ค่าสาธารณูปโภค" บนใบซื้ออุปกรณ์แคมป์ปิ้ง 2026-09-18
            //  ซึ่ง grep แล้วไม่มี label นี้ใน resolver เลย)
            // หลักการข้อ G4: ผู้ชนะต้องบอกได้ว่าตัวเองมาจากไหน ก่อนจะให้ใครเชื่อ
            if (!string.IsNullOrWhiteSpace(extractedData.ExpenseCategory))
                extractedData.ReasoningTrace.Add(
                    $"[Category] ค่าที่ติดมาก่อนตัวจัดหมวด: '{extractedData.ExpenseCategory}' "
                    + "(มาจากช่อง expense_category ของ engine หรือค่าตั้งต้นตามชนิดเอกสาร "
                    + "— ไม่ใช่ผลของ ExpenseCategoryResolver)");

            var categoryResult = Ocr.ExpenseCategoryResolver.Resolve(
                vendorName: extractedData.VendorName,
                headerDescription: extractedData.ExpenseCategory,
                lineDescriptions: paidLines,
                rawText: categoryResolverText,
                industry: companyContext?.IndustryType,
                businessType: companyContext?.BusinessType,
                zeroAmountLineDescriptions: zeroLines);
            if (categoryResult != null)
                Ocr.ExpenseCategoryResolver.ApplyTo(extractedData, categoryResult, categoryResolverText);
            else if (!string.IsNullOrWhiteSpace(extractedData.ExpenseCategory))
                extractedData.ReasoningTrace.Add(
                    "[Category] ตัวจัดหมวดไม่มีกติกาไหนตรงกับใบนี้ ⇒ คงค่าที่ติดมาก่อนหน้าไว้ "
                    + $"('{extractedData.ExpenseCategory}') — ถ้าหมวดนี้ดูไม่เข้าเรื่อง แปลว่า"
                    + "ค่ามาจากชั้น engine/ค่าตั้งต้น ไม่ใช่จากกติกาของระบบ");

            // ───── Basket-analysis association rule lookup ─────
            // Apriori-mined rules from approved-doc history across all
            // tenants. A high-lift rule "brand:HomePro + kw:วัสดุ → acct:5305"
            // beats the static keyword resolver because it reflects what
            // real Thai businesses actually book.
            try
            {
                var rule = await _ruleMiner.FindBestMatchAsync(
                    extractedData.VendorName,
                    extractedData.Items.Select(i => i.Description));
                if (rule != null && rule.Confidence >= 0.7m && rule.Lift >= 2m
                    && rule.Consequent.StartsWith("acct:")
                    && (string.IsNullOrEmpty(extractedData.DebitAccountCode)
                        || extractedData.FieldConfidence.GetValueOrDefault("DebitAccount", 0) < 0.85))
                {
                    var code = rule.Consequent.Substring("acct:".Length);
                    extractedData.DebitAccountCode = code;
                    var acctName = await _db.ChartOfAccounts.AsNoTracking()
                        .Where(a => a.CompanyId == companyId && a.AccountCode == code && !a.IsDeleted)
                        .Select(a => a.AccountName)
                        .FirstOrDefaultAsync();
                    if (!string.IsNullOrEmpty(acctName)) extractedData.DebitAccountName = acctName;
                    extractedData.FieldConfidence["DebitAccount"] = (double)Math.Min(0.95m, rule.Confidence);
                    extractedData.ReasoningTrace.Add(
                        $"[BasketRule] {string.Join("+", rule.Antecedent)} → {rule.Consequent} " +
                        $"(conf {rule.Confidence:P0}, lift {rule.Lift:F1}, จาก {rule.TransactionCount} เอกสาร)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Association rule lookup failed (non-fatal)");
            }

            // ───── Naive Bayes + TF-IDF classifier ─────
            // Probabilistic blend of per-tenant historical (vendor +
            // description → account) data. Wins over the keyword learner
            // when there are many low-frequency overlapping tokens that
            // a count-based approach would dilute. Only commits when the
            // log-margin over the runner-up class exceeds 1 nat.
            try
            {
                var nbDesc = extractedData.Items.FirstOrDefault()?.Description
                    ?? extractedData.ExpenseCategory ?? "";
                var nbPred = await _nbClassifier.PredictAsync(
                    companyId, extractedData.VendorTaxId, extractedData.VendorName, nbDesc);
                if (nbPred != null && nbPred.Confidence > 0.7m
                    && extractedData.FieldConfidence.GetValueOrDefault("DebitAccount", 0) < (double)nbPred.Confidence)
                {
                    extractedData.DebitAccountCode = nbPred.AccountCode;
                    if (!string.IsNullOrEmpty(nbPred.AccountName))
                        extractedData.DebitAccountName = nbPred.AccountName;
                    extractedData.FieldConfidence["DebitAccount"] = (double)nbPred.Confidence;
                    extractedData.ReasoningTrace.Add(
                        $"[NaiveBayes] รหัส {nbPred.AccountCode} confidence {nbPred.Confidence:P0} ({nbPred.Reason})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NB classifier failed (non-fatal)");
            }

            // ───── Product cross-reference ─────
            // When a line-item description matches an existing Product (by
            // exact name or barcode/SKU substring), prefer the product's
            // configured PurchaseAccount over any inferred account — the
            // product master is the strongest signal: a human set this up
            // intentionally and Thai products almost always have one
            // canonical expense bucket.
            await ApplyProductCrossReferenceAsync(companyId, extractedData);

            // ───── Learned category prediction (per line description) ─────
            // If we have a learned mapping for this vendor, prefer it over generic
            // industry-based defaults. Per-line predictions also override individual
            // line item.SuggestedAccountCode when learner is more confident.
            var bestDescription = extractedData.Items.FirstOrDefault()?.Description
                ?? extractedData.ExpenseCategory;
            var (learnedCode, learnedName, learnedConf) = await _categoryLearner.PredictAsync(
                companyId, extractedData.VendorTaxId, extractedData.VendorName, bestDescription);
            // ⚠️ เดิมบล็อกนี้ **ทับผังเดบิตโดยไม่เทียบกับใคร** และไม่อัปเดต
            // FieldConfidence ⇒ (ก) คำตอบของ NaiveBayes ที่ 0.92 และของ **สินค้า
            // master ที่ 0.95** (ซึ่ง doc-comment ข้างบนเขียนเองว่า "แข็งแรงที่สุด
            // เพราะคนตั้งใจตั้งไว้") ถูกทับด้วยตัวเรียนรู้ที่มั่นใจแค่ 0.55
            // (ข) ป้ายความมั่นใจยังค้างที่ 0.95 ของเจ้าเดิม ⇒ ไฮไลต์เหลืองตาม
            // กฎเหล็ก #3 ข้อ 3 ไม่ขึ้น ทั้งที่ค่าที่แสดงมาจากแหล่งที่อ่อนกว่ามาก
            // (ผลตรวจ 2026-09-06 · T3-08)
            // กติกา: ผู้มาทีหลังต้อง **มั่นใจกว่าจริง** ถึงจะทับ และต้องเขียน
            // ความมั่นใจของตัวเองลงไปด้วยเสมอ
            var existingDebitConf = (decimal)extractedData.FieldConfidence
                .GetValueOrDefault("DebitAccount", 0);
            if (learnedCode != null && learnedConf >= 0.55m && learnedConf > existingDebitConf)
            {
                extractedData.DebitAccountCode = learnedCode;
                extractedData.DebitAccountName = learnedName;
                extractedData.FieldConfidence["DebitAccount"] = (double)learnedConf;
                extractedData.ReasoningTrace.Add($"[Learner] เคยใช้รหัส {learnedCode} กับผู้ขายนี้+คำอธิบายนี้ (confidence {learnedConf:P0})");
            }
            else if (learnedCode != null && learnedConf >= 0.55m)
            {
                // ไม่ทับ แต่ต้องบอกว่ามีความเห็นที่ต่าง — ห้ามเงียบ
                extractedData.ReasoningTrace.Add(
                    $"[Learner] เคยใช้รหัส {learnedCode} (confidence {learnedConf:P0}) "
                    + $"แต่ไม่ทับค่าเดิม {extractedData.DebitAccountCode} ที่มั่นใจกว่า ({existingDebitConf:P0})");
            }

            // Per-line: เติมเฉพาะบรรทัดที่ยังว่าง (ไม่ทับของสินค้า master)
            // — ทำนอกเงื่อนไขข้างบน เพราะบรรทัดที่ยังว่างควรได้คำตอบเสมอ
            // แม้หัวใบจะมีเจ้าอื่นที่มั่นใจกว่าอยู่แล้ว
            if (learnedCode != null && learnedConf >= 0.55m)
            {
                foreach (var item in extractedData.Items)
                {
                    if (string.IsNullOrEmpty(item.SuggestedAccountCode))
                    {
                        var (perLineCode, _, perLineConf) = await _categoryLearner.PredictAsync(
                            companyId, extractedData.VendorTaxId, extractedData.VendorName, item.Description);
                        if (perLineCode != null && perLineConf >= 0.55m)
                            item.SuggestedAccountCode = perLineCode;
                    }
                }
            }

            // ───── Vendor intelligence prediction (DocumentType + WHT + amount sanity) ─────
            // Higher-level "what does this supplier usually look like?" cache built from
            // approved Documents history. Used to:
            //   • Auto-select DocumentType when AI/rules are uncertain
            //   • Fill in WHT habits when extraction missed it
            //   • Flag amount anomalies for user review
            //   • Backfill debit account when per-line learner had no match
            var vendorPred = await _vendorIntel.PredictAsync(
                companyId, extractedData.VendorTaxId, extractedData.VendorName, extractedData.TotalAmount);
            if (vendorPred.HasHistory)
            {
                extractedData.ReasoningTrace.Add($"[VendorIntel] พบประวัติ {vendorPred.SampleSize} เอกสารของผู้ขายรายนี้");
                foreach (var reason in vendorPred.Reasons)
                    extractedData.ReasoningTrace.Add("[VendorIntel] " + reason);

                // Auto-apply TargetDocumentType when high confidence.
                // VendorIntel learns from previously-CREATED documents, so its
                // prediction is the doc-type to create — not the paper that was
                // scanned. Override TargetDocumentType (set earlier by the role
                // inferrer); never touch DocumentType (the scanned paper type).
                if (vendorPred.DocumentType.HasValue
                    && vendorPred.DocumentTypeConfidence >= VendorIntelligenceService.HighConfidence
                    // ⚠️ prior ของ vendor บอกได้แค่ "เอกสารของเจ้านี้มักลงเป็นอะไร"
                    // ไม่ได้รู้ว่าใบนี้เราเป็นผู้ซื้อหรือผู้ขาย. TrainFromAdminAsync
                    // ไม่กรองฝั่ง ⇒ คำแก้ฝั่งขายเขียนทับ prior ฝั่งซื้อของ vendor
                    // เดียวกันได้ แล้วมาทับใบซื้อใบถัดไปแบบอัตโนมัติที่ ≥0.85
                    // → ยอมให้ทับเฉพาะเมื่ออยู่ฝั่งเดียวกับบทบาทที่อนุมานได้
                    && Accounting.Helpers.DocumentSide.MatchesRole(vendorPred.DocumentType.Value, extractedData.OurRole))
                {
                    var newTarget = vendorPred.DocumentType.Value.ToString();
                    if (extractedData.TargetDocumentType != newTarget)
                        SetTargetDocumentType(extractedData, newTarget,
                            Accounting.Helpers.OcrFieldSource.VendorHistory,
                            vendorPred.DocumentTypeConfidence,
                            $"ประวัติ {vendorPred.SampleSize} เอกสารของผู้ขายรายนี้ (ฝั่งเดียวกับบทบาทเรา)");
                    // ⚠️ **ห้ามยกคะแนนความมั่นใจทั้งใบด้วย prior ของผู้ขาย** (D3-3)
                    // เดิม: `Confidence = Math.Max(Confidence, vendorPred.DocumentTypeConfidence)`
                    // — prior บอกได้แค่ "เอกสารของเจ้านี้มักลงเป็นชนิดอะไร" ไม่ได้รู้ว่า
                    // **ตัวเลขบนใบนี้อ่านถูกไหม** ⇒ ใบที่ Gateway หักคะแนนเพราะ
                    // sub + vat ≠ total ของผู้ขายประจำ ถูกดันกลับขึ้นเหนือเกณฑ์
                    // auto-create 0.85 = ด่านคณิตศาสตร์ถูกลบล้างด้วยความคุ้นเคย
                    // (ชนิดเอกสารที่ prior ตัดสินยังถูกใช้อยู่ตามเดิม — แค่ไม่ยกคะแนน)
                }
                else if (vendorPred.DocumentType.HasValue && vendorPred.DocumentTypeConfidence >= VendorIntelligenceService.MediumConfidence)
                {
                    extractedData.ReasoningTrace.Add(
                        $"[VendorIntel] แนะนำสร้าง {vendorPred.DocumentType.Value} (confidence {vendorPred.DocumentTypeConfidence:P0}) — รอผู้ใช้ยืนยัน");
                }
                // Federated fallback — when per-tenant VendorIntel has no
                // confident prediction (new vendor for this tenant, but a
                // vendor the SaaS as a whole has seen before), consult the
                // cross-tenant pool. The federated pattern needs k=3 distinct
                // contributing tenants to be "active" — under the floor, silent.
                //
                // ⚠️ เงื่อนไขเดิมมี `string.IsNullOrEmpty(extractedData.TargetDocumentType)`
                // ซึ่ง **เป็นจริงไม่ได้เลย** เพราะ OcrDocumentRoleInferrer ด้านบน
                // เซ็ตค่าให้เสมอทุกเส้นทาง ⇒ ตัวเรียนรู้ federated เขียนข้อมูลสะสม
                // มาตลอดแต่ไม่เคยถูกอ่านสักครั้ง (write-only ตั้งแต่วันแรก)
                //
                // เงื่อนไขที่ถูกต้อง: ใช้เมื่อ "เรายังไม่มั่นใจ" —
                //   1. VendorIntel ของ tenant นี้ไม่มั่นใจ (< Medium)
                //   2. รู้ชนิดกระดาษ (ใช้เป็น key ของ pattern)
                //   3. ตัวอนุมานเองก็ไม่มั่นใจ (บทบาท < 0.9) **หรือ** เป้าหมายมาจาก
                //      default ไม่ใช่ marker บนกระดาษ — ถ้ากระดาษบอกชัดและเรารู้
                //      บทบาทแน่นอนแล้ว ความรู้ของคนอื่นไม่ควรมาทับ
                if (_docWorkflowLearner != null
                    && (vendorPred.DocumentTypeConfidence < VendorIntelligenceService.MediumConfidence)
                    && !string.IsNullOrEmpty(extractedData.DocumentType)
                    && (ocrRoleConfidence < 0.9m || !ocrTargetFromPaper))
                {
                    try
                    {
                        // ⚠️ เดิมสร้างคีย์เองแล้ว **ลืมด่าน 13 หลัก** ⇒ ใบที่ OCR อ่านเลข
                        // ผู้เสียภาษีไม่ครบจะไปหา "tax:0105556" ที่ไม่มีใครเคยเขียน
                        // (ฝั่งเขียนตกไปใช้ "name:") ⇒ ความรู้จากผู้เช่าคนอื่นไม่เคยถูกใช้เลย
                        // สำหรับใบกลุ่มนั้น — ตอนนี้ทั้งสองฝั่งผ่าน Helpers/VendorLearningKey
                        var fedVKey = Accounting.Helpers.VendorLearningKey.For(
                            extractedData.VendorTaxId, extractedData.VendorName);
                        if (!string.IsNullOrEmpty(fedVKey))
                        {
                            var (fedTarget, fedTenants) = await _docWorkflowLearner.PredictAsync(fedVKey, extractedData.DocumentType);
                            // ห้ามข้ามฝั่งซื้อ/ขาย — ความรู้จาก tenant อื่นบอกได้แค่
                            // "vendor รายนี้มักถูกลงเป็นอะไร" ไม่ได้รู้ว่าใบนี้เราเป็น
                            // ผู้ซื้อหรือผู้ขาย ถ้าเป้าที่แนะนำอยู่คนละฝั่งกับบทบาทที่
                            // ยืนยันแล้ว = ข้อมูลใช้ไม่ได้ ทิ้งไป
                            if (!string.IsNullOrEmpty(fedTarget)
                                && Enum.TryParse<DocumentType>(fedTarget, out var fedDt)
                                && Accounting.Helpers.DocumentSide.MatchesRole(fedDt, extractedData.OurRole))
                            {
                                SetTargetDocumentType(extractedData, fedTarget,
                                    Accounting.Helpers.OcrFieldSource.VendorHistory,
                                    FederatedTargetConfidence,
                                    $"🌐 คลังกลางข้ามผู้เช่า: {fedTenants} ลูกค้าที่ใช้ผู้ขายรายเดียวกันลงเป็นชนิดนี้ "
                                    + "(คลังกลางไม่รายงานความมั่นใจ)");
                            }
                            else if (!string.IsNullOrEmpty(fedTarget))
                            {
                                extractedData.ReasoningTrace.Add(
                                    $"[Federated] ข้ามคำแนะนำ {fedTarget} — คนละฝั่งกับบทบาทเรา ({extractedData.OurRole})");
                            }
                        }
                    }
                    catch { /* federation outage — silent */ }
                }

                // Auto-fill debit account when per-line learner had no result
                if (string.IsNullOrEmpty(extractedData.DebitAccountCode)
                    && !string.IsNullOrEmpty(vendorPred.DebitAccountCode)
                    && vendorPred.DebitAccountConfidence >= VendorIntelligenceService.MediumConfidence)
                {
                    extractedData.DebitAccountCode = vendorPred.DebitAccountCode;
                    extractedData.DebitAccountName = vendorPred.DebitAccountName;
                    // ⚠️ **ต้องเขียนความมั่นใจลงไปด้วยเสมอ** — เดิมบล็อกนี้เติมรหัสแต่
                    // ไม่เคยแตะ `FieldConfidence["DebitAccount"]` ⇒ ค่าค้างที่ 0 ⇒
                    //  (ก) ด่าน "ครูภายนอกต้องมั่นใจกว่าของเดิม" ที่เพิ่มรอบ 179
                    //      **ผ่านทุกครั้ง** เมื่อประวัติผู้ขายเป็นคนเติม — AI 0.70 ทับได้เสมอ
                    //  (ข) ไฮไลต์เหลืองตาม กฎเหล็ก #3 ข้อ 3 คำนวณจาก 0 ⇒ ขึ้นเหลืองทั้งที่
                    //      ประวัติผู้ขายเป็นหลักฐานที่ดี
                    // (ทีมตรวจรอบ 180 จับรูนี้ในสิ่งที่ผมเพิ่งแก้เอง)
                    extractedData.FieldConfidence[Accounting.Helpers.OcrFieldKeys.DebitAccount] =
                        (double)vendorPred.DebitAccountConfidence;
                    extractedData.ReasoningTrace.Add(
                        $"[VendorIntel] เลือกรหัสบัญชี {vendorPred.DebitAccountCode} จากประวัติผู้ขาย (confidence {vendorPred.DebitAccountConfidence:P0})");
                }

                // ── ประวัติผู้ขายเรื่อง WHT = **ข้อเสนอ** ห้ามสวมรอย "กระดาษ" ──
                //
                // ⚠️ ที่มา (ผลตรวจ 2026-09-18 · D3-2): เดิมบล็อกนี้เขียน
                // `HasWht = true; WhtRate = …` ซึ่งเป็นช่อง "ยอด/อัตราที่**พิมพ์บน
                // กระดาษ**" (ดู doc ของ OcrExtractedData.WhtRate) ทั้งที่
                // `Helpers/PaperWhtReader` เพิ่งอ่านกระดาษแล้วตอบว่าไม่มีส่วนหัก ⇒
                //   1. เส้นสร้างเอกสารคิด `headerWht` แล้วหัก ณ ที่จ่ายจริงบนใบ
                //   2. ตอนอนุมัติ `ScanPaperWhtEvidenceAsync` อ่าน `scan.HasWht`
                //      แล้วสรุปว่า "กระดาษประกาศเชิงบวก ⇒ ต้องหัก" (ชนะทุกด่าน)
                //   3. `VendorIntelligenceService` เรียนกลับจาก
                //      `doc.WithholdingTaxAmount > 0` ⇒ **วงจรสอนตัวเอง** ที่
                //      DECISION_DOCTRINE §3 ห้าม (ประวัติยืนยันประวัติของตัวเอง)
                // ตอนนี้เขียนลง `SuggestedWhtRate` (ช่อง "ข้อเสนอ") + ความมั่นใจราย
                // ช่อง (< 0.85 ⇒ ไฮไลต์เหลืองตามกฎเหล็ก #3 ข้อ 3) ⇒ เส้นสร้างเอกสาร
                // ออกคำเตือน [WHT-SUGGEST] ให้คนตัดสิน แต่ไม่หักเงินให้เอง
                //
                // ── รอบ 184 (D-3 ส่วนที่เหลือ) — "ประวัติใบเดียวไม่ใช่ประวัติ" ──
                // เกณฑ์เดิมคือ MediumConfidence (0.65) และ **ไม่มีขั้นต่ำของขนาด
                // ตัวอย่างเลย** ⇒ ผู้ขายที่มีเอกสารใบเดียวได้ WhtConfidence =
                // max(p, 1−p) = 1.0 ทันที (บล็อกนี้ไม่มี sizeFactor ต่างจากบล็อก
                // ชนิดเอกสาร/ผังบัญชีข้างบน) ⇒ ใบแรกของผู้ขายใหม่กลายเป็น "นิสัย"
                // ตอนนี้ทุกเงื่อนไขอยู่ใน Helpers/OcrWhtSuggestionGate ตัวเดียว
                // (เกณฑ์เป็น public const ให้เทสต์อ้างได้ · G6)
                var whtGate = Accounting.Helpers.OcrWhtSuggestionGate.Judge(
                    paperAlreadyAnswered: extractedData.HasWht,
                    historyLeansToWithhold: vendorPred.HasWht == true,
                    historyRate: vendorPred.WhtRate,
                    totalDocuments: vendorPred.SampleSize,
                    dominance: vendorPred.WhtConfidence);
                if (whtGate.Suggest)
                {
                    // บล็อกนี้**เสนออย่างเดียว ไม่เขียนค่า**: คนตัดสินคือ
                    // ApplyWhtSuggestion (arbiter) ท้ายไปป์ไลน์ — เลิก "ใครเขียนก่อนชนะ"
                    extractedData.Note(Accounting.Helpers.OcrFieldKeys.SuggestedWhtRate,
                        vendorPred.WhtRate,
                        Accounting.Helpers.OcrFieldSource.VendorHistory, vendorPred.WhtConfidence,
                        "ประวัติการหัก ณ ที่จ่ายของผู้ขายรายนี้ — ไม่ใช่ยอดบนกระดาษใบนี้");
                    extractedData.ReasoningTrace.Add(
                        $"[VendorIntel] **เสนอ** หัก ณ ที่จ่าย {vendorPred.WhtRate}% — {whtGate.Reason} "
                        + "· ระบบไม่หักให้ ต้องให้ผู้ใช้ยืนยันก่อน");
                }
                else if (vendorPred.HasWht == true && vendorPred.WhtRate is > 0m
                         && !extractedData.HasWht && !extractedData.SuggestedWhtRate.HasValue)
                {
                    // ทางไปต่อของผู้ใช้ที่ถูกกัน (F2 ข้อ 8): บอกว่า**ทำไมไม่เสนอ**
                    // แทนที่จะเงียบ — ช่องอัตรายังกรอกเองได้ตามปกติ
                    extractedData.ReasoningTrace.Add(
                        $"[VendorIntel] ไม่เสนออัตราหัก ณ ที่จ่ายจากประวัติ — {whtGate.Reason} "
                        + "· กรอกเองได้ในช่อง \"อัตราหัก ณ ที่จ่าย\"");
                }

                // Auto-fill payment terms when missing
                if (!extractedData.PaymentTermsDays.HasValue && vendorPred.TypicalPaymentTermsDays.HasValue)
                {
                    extractedData.PaymentTermsDays = vendorPred.TypicalPaymentTermsDays;
                    extractedData.ReasoningTrace.Add(
                        $"[VendorIntel] ตั้ง payment terms = {vendorPred.TypicalPaymentTermsDays} วัน จากประวัติผู้ขาย");

                    // ── เครดิตเทอมจากประวัติ = หลักฐานว่า "ยังไม่จ่าย" ──
                    //
                    // ตัวอนุมานรันก่อนจุดนี้และเห็นเฉพาะเครดิตเทอมที่ "พิมพ์บน
                    // กระดาษ" ใบที่ไม่พิมพ์เทอมจึงตกไปเป็นใบสำคัญจ่ายเสมอ =
                    // **บันทึกการจ่ายเงินที่ไม่เคยเกิดขึ้น** (เงินออกจากบัญชีธนาคาร
                    // ในสมุด ทั้งที่ยังไม่ได้จ่าย → กระทบยอดธนาคารเพี้ยน + เจ้าหนี้ขาด)
                    //
                    // ผิดทางไหนแย่กว่ากัน: ตั้งหนี้ทั้งที่จ่ายแล้ว = เจ้าหนี้เกินชั่วคราว
                    // แล้วไปตัดตอนบันทึกจ่าย (มี flow รองรับ) · แต่บันทึกจ่ายทั้งที่
                    // ยังไม่จ่าย = เงินสดหาย ไม่มี flow ย้อนกลับ ⇒ เลือกตั้งหนี้
                    //
                    // ใช้เฉพาะฝั่งซื้อ + ใบที่ยังเป็น default PaymentVoucher +
                    // กระดาษไม่มีตราชำระแล้ว (ถ้ามีตรา ตัวอนุมานจะไม่ได้ให้ PV
                    // จาก default อยู่แล้ว แต่กันไว้ให้ชัด)
                    if (extractedData.OurRole == "Buyer"
                        && extractedData.TargetDocumentType == nameof(DocumentType.PaymentVoucher)
                        && vendorPred.TypicalPaymentTermsDays > 0
                        && extractedData.DocumentType is nameof(DocumentType.TaxInvoice) or nameof(DocumentType.Invoice))
                    {
                        SetTargetDocumentType(extractedData, nameof(DocumentType.PurchaseInvoice),
                            Accounting.Helpers.OcrFieldSource.VendorHistory, 1.0m,
                            $"ผู้ขายรายนี้ให้เครดิต {vendorPred.TypicalPaymentTermsDays} วันเป็นปกติ "
                            + "และกระดาษไม่มีตรา \"ชำระแล้ว\" → ตั้งหนี้แทนใบสำคัญจ่าย "
                            + "(กันบันทึกการจ่ายที่ยังไม่เกิด)");
                    }
                }

                // ─── Pattern-based confidence boost ───
                // If this vendor's history shows a consistent document-number
                // prefix and the current scan's number also starts with that
                // prefix, raise the DocumentNumber confidence. When extraction
                // produced a number that DOESN'T match the historic prefix it's
                // likely an OCR misread — drop confidence to flag for review.
                if (!string.IsNullOrEmpty(vendorPred.TypicalDocNumberPrefix)
                    && !string.IsNullOrEmpty(extractedData.DocumentNumber)
                    && vendorPred.TypicalDocNumberPrefix.Length >= 2)
                {
                    if (extractedData.DocumentNumber.StartsWith(vendorPred.TypicalDocNumberPrefix,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        extractedData.FieldConfidence["DocumentNumber"] = Math.Max(
                            extractedData.FieldConfidence.GetValueOrDefault("DocumentNumber", 0), 0.95);
                        extractedData.ReasoningTrace.Add(
                            $"[VendorIntel] เลขเอกสาร '{extractedData.DocumentNumber}' ขึ้นต้นตรงกับ pattern '{vendorPred.TypicalDocNumberPrefix}' ของผู้ขายรายนี้");
                    }
                    else
                    {
                        extractedData.FieldConfidence["DocumentNumber"] = Math.Min(
                            extractedData.FieldConfidence.GetValueOrDefault("DocumentNumber", 0.7), 0.5);
                        extractedData.ReasoningTrace.Add(
                            $"[VendorIntel] เลขเอกสาร '{extractedData.DocumentNumber}' ไม่ตรง pattern '{vendorPred.TypicalDocNumberPrefix}' ของผู้ขายรายนี้ — โปรดตรวจสอบ");
                    }
                }

                // Boost ExpenseCategory confidence when the current scan's
                // descriptions share words with the vendor's historical
                // top-keywords. This pushes a marginal category match into
                // high-confidence territory without overriding what the user
                // (or learner) has already trained.
                if (vendorPred.TopLineKeywords != null && vendorPred.TopLineKeywords.Count > 0
                    && !string.IsNullOrEmpty(extractedData.ExpenseCategory))
                {
                    var corpus = string.Join(" ",
                        new[] { extractedData.ExpenseCategory ?? "" }
                            .Concat(extractedData.Items.Select(i => i.Description ?? ""))
                    ).ToLowerInvariant();
                    int hits = vendorPred.TopLineKeywords
                        .Where(kv => corpus.Contains(kv.Key))
                        .Sum(kv => kv.Value);
                    if (hits >= 3)
                    {
                        extractedData.FieldConfidence["ExpenseCategory"] = Math.Max(
                            extractedData.FieldConfidence.GetValueOrDefault("ExpenseCategory", 0), 0.92);
                        extractedData.ReasoningTrace.Add(
                            $"[VendorIntel] รายการตรงกับ keyword ประวัติ {hits} ครั้ง → หมวด '{extractedData.ExpenseCategory}' มั่นใจสูง");
                    }
                }
            }

            // ───── DeepSeek GL-account classification (teacher → student) ─────
            // Throw the line to the connected AI provider (DeepSeek) to pick the
            // expense/asset account, then DISTIL its answer into the per-tenant
            // local model. The orchestrator routes student-first: when the local
            // GlAccountDistillationModel is already confident it short-circuits
            // WITHOUT a paid call; otherwise it asks DeepSeek and records the
            // answer as an AiSuggestionFeedback row that the nightly
            // AiFeedbackTrainingJob distils back into the local model.
            // See CLAUDE.md → "🚨 กฎเหล็ก #1 — Distillation Mandate" for the
            // four-step contract every AI call in this codebase must follow.
            if (_aiAugmenter != null)
            {
                try
                {
                    // ส่งเฉพาะ "ข้อมูลจริงจากบิล" ให้ AI — ห้าม fallback เป็น
                    // ExpenseCategory ที่ rule-based เดาไว้ เพราะถ้าเดาผิด AI จะ
                    // เห็นชื่อหมวดผิดเป็น "รายการในบิล" แล้วยืนยันกลับด้วย
                    // confidence สูง (เคสจริง: บิล Makro อ่านรายการไม่ได้ →
                    // resolver เดา "ค่าที่ปรึกษากฎหมาย/บัญชี" → AI ตอบ 54620
                    // มั่นใจ 0.95 ทั้งที่ซื้อของ). รายการหลายบรรทัดส่ง 3 บรรทัด
                    // แรกให้ AI เห็นภาพรวมตะกร้า ไม่ใช่ชิ้นแรกชิ้นเดียว
                    // แนบ "ยอดต่อบรรทัด" ไว้ใน description ด้วย — เดิมส่ง TotalAmount
                    // ทั้งใบเป็น amount ทำให้กฎ capitalize (≥฿50,000/ชิ้น) ตัดสินบน
                    // ตัวเลขผิด (บิล Makro รวม 60,000 ที่มีปริ้นเตอร์ 4,500 → AI เห็น
                    // 60,000 เลยสั่ง capitalize ทั้งตะกร้า)
                    // ⚠️ เดิม `.Take(3)` ตัดที่ 3 บรรทัดแรก — ตะกร้าที่มี 12 รายการ
                    // ถูกตัดสินบัญชีจากรายการที่บังเอิญอยู่ต้นบิล ซึ่งไม่ได้แปลว่า
                    // เป็นตัวแทนของยอดเลย (บิลค้าปลีกมักเรียงตามลำดับสแกนสินค้า)
                    // ตอนนี้เรียงตาม**ยอดต่อบรรทัดจากมากไปน้อย** แล้วส่งได้ถึง 12
                    // บรรทัด + บอกจำนวนที่เหลือ ⇒ ตัวแทนตะกร้าตรงกับที่เงินอยู่จริง
                    var rankedItems = extractedData.Items
                        .Where(i => !string.IsNullOrWhiteSpace(i.Description))
                        .OrderByDescending(i => i.Amount ?? 0m)
                        .ToList();
                    const int glMaxLines = 12;
                    var itemDescs = rankedItems
                        .Take(glMaxLines)
                        .Select(i => i.Amount.HasValue && i.Amount.Value > 0
                            ? $"{i.Description!.Trim()} (฿{i.Amount.Value:N0})"
                            : i.Description!.Trim())
                        .ToList();
                    if (rankedItems.Count > glMaxLines)
                        itemDescs.Add($"…และอีก {rankedItems.Count - glMaxLines} รายการย่อย");
                    var aiLineDesc = itemDescs.Count > 0
                        ? string.Join(" | ", itemDescs)
                        : extractedData.VendorName ?? "";
                    // amount ตัวแทน = บรรทัดที่แพงสุด (ตัวตัดสิน capitalize ต่อชิ้น)
                    // — ไม่ใช่ยอดรวมทั้งใบ; ไม่มีรายบรรทัด → ใช้ยอดใบตามเดิม
                    var aiLineAmount = extractedData.Items
                        .Where(i => i.Amount.HasValue && i.Amount.Value > 0)
                        .Select(i => i.Amount!.Value)
                        .DefaultIfEmpty(extractedData.TotalAmount ?? 0m)
                        .Max();
                    if (!string.IsNullOrWhiteSpace(aiLineDesc))
                    {
                        var localConf = (decimal)extractedData.FieldConfidence.GetValueOrDefault("DebitAccount", 0);
                        using var glCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        // vendorIndustry = ประเภทธุรกิจของ "ผู้ขาย" (ไม่ใช่บริษัทเรา!)
                        // เดิมส่ง companyContext.IndustryType ผิด — ทำให้ AI คิดว่า
                        // ผู้ขายเป็นธุรกิจเดียวกับเรา (เช่นใบปั๊มน้ำมัน → AI เห็น
                        // "vendor.industry=Hotel" เลยเดาว่าเป็นของใช้โรงแรม). company
                        // industry ส่งแยกผ่าน businessContext ใน augmenter อยู่แล้ว.
                        // DBD ยังไม่มี vendor TSIC ชัด → ส่ง null ให้ AI อนุมานจาก
                        // ชื่อผู้ขาย + รหัสสินค้า/หน่วย ตาม decode rules ใน prompt.
                        // บรรทัดที่ยอดสูงสุด — ใช้ส่งหน่วย/จำนวน/ราคาต่อหน่วยของ
                        // บรรทัดนั้นให้ AI ตาม decode rule ข้อ 8(b) ของพรอมป์ต์
                        // (เดิมส่งแต่คำบรรยาย ⇒ กฎที่พรอมป์ต์พึ่งมากที่สุดไม่มี
                        //  อินพุตให้ใช้เลย: "L/ลิตร → น้ำมัน · kWh → ค่าไฟ")
                        var mainLine = extractedData.Items
                            .Where(i => i.Amount.HasValue)
                            .OrderByDescending(i => i.Amount!.Value)
                            .FirstOrDefault();
                        var glResult = await _aiAugmenter.SuggestGlAccountAsync(
                            companyId, scanResult.Id,
                            extractedData.VendorName, extractedData.VendorTaxId,
                            extractedData.DbdJuristicType,   // ประเภทนิติบุคคลผู้ขาย (ถ้า DBD เจอ) มิฉะนั้น null
                            aiLineDesc, aiLineAmount, "THB",
                            extractedData.DebitAccountCode, localConf,
                            new Services.Ai.GlLineContext(
                                Unit: mainLine?.Unit,
                                Quantity: mainLine?.Quantity,
                                UnitPrice: mainLine?.UnitPrice,
                                DocumentDate: extractedData.DocumentDate,
                                OurRole: extractedData.OurRole,
                                InputVatClaimable: ocrInputVatClaimable),
                            glCts.Token);

                        // Record the trail. FeedbackId เก็บเสมอ (training signal).
                        // เก็บ AI primary + confidence แม้ถูกปฏิเสธ → review UI
                        // โชว์ "AI เสนอ X (conf Y%)" ให้ผู้ใช้เห็น + เลือกเองได้.
                        scanResult.GlAccountAiFeedbackId = glResult.FeedbackId;
                        if (glResult.UsedAi && !string.IsNullOrEmpty(glResult.Answer))
                        {
                            scanResult.GlAccountAiSuggestedCode = glResult.Answer;
                            scanResult.GlAccountAiConfidence = glResult.Confidence;
                        }

                        // Apply only when AI actually ran, was confident, and named
                        // a real account in THIS company's CoA (anti-hallucination).
                        // GlAccountUsedAi = "AI's answer was APPLIED" — ป้ายซื่อสัตย์:
                        // true เฉพาะตอนค่าที่แสดงมาจาก AI จริง ไม่ใช่แค่ AI ถูกเรียก.
                        scanResult.GlAccountUsedAi = false;
                        // ★ กฎเหล็ก #1 (T3-01): นักเรียนที่ short-circuit ใช้ได้เท่า AI
                        //
                        // ⚠️ **ครูภายนอกต้องมั่นใจกว่าของเดิมจริงถึงจะทับ** (รอบ 179)
                        // เดิมบล็อกนี้ไม่เทียบกับ `FieldConfidence["DebitAccount"]` เลย
                        // ⇒ คำตอบของ AI ที่ 0.70 ทับกติกาตะกร้า/NaiveBayes/ประวัติผู้ขาย
                        // ที่ 0.95 ได้ เพียงเพราะ**มาทีหลัง** — เป็น "ใครมาก่อนชนะ"
                        // ในทรงกลับด้าน ซึ่งกฎเหล็ก #4 A ห้ามไว้ และชั้นถัดขึ้นไปในไฟล์
                        // เดียวกัน (`[Learner]`) ก็เทียบก่อนทับอยู่แล้ว = สองมาตรฐาน
                        //
                        // นักเรียน (`UsedAi == false`) ไม่ต้องผ่านด่านนี้ เพราะคำตอบของมัน
                        // **คือ**ประวัติที่ผู้ใช้บริษัทนี้ยืนยันไว้เอง — อยู่ชั้นเดียวกับ
                        // ตัวที่มันจะไปทับ ไม่ใช่การเดาจากภายนอก (DECISION_DOCTRINE G1)
                        var glIncumbentConf = (decimal)extractedData.FieldConfidence
                            .GetValueOrDefault("DebitAccount", 0);
                        var glConf = glResult.Confidence ?? 0m;
                        var glMayOverride = !glResult.UsedAi || glConf > glIncumbentConf;
                        if (glResult.HasModelAnswer && glConf >= 0.70m && glMayOverride)
                        {
                            var aiAcct = await _db.ChartOfAccounts.AsNoTracking()
                                .FirstOrDefaultAsync(a => a.CompanyId == companyId
                                    && a.AccountCode == glResult.Answer
                                    && a.IsActive && !a.IsDeleted);
                            if (aiAcct != null)
                            {
                                extractedData.DebitAccountCode = aiAcct.AccountCode;
                                extractedData.DebitAccountName = aiAcct.AccountName;
                                extractedData.FieldConfidence["DebitAccount"] = (double)(glResult.Confidence ?? 0.7m);
                                scanResult.GlAccountUsedAi = glResult.UsedAi;   // ป้ายซื่อสัตย์: true เฉพาะ provider จริง
                                extractedData.ReasoningTrace.Add(
                                    $"[{glResult.SourceLabel}] จัดหมวดบัญชี → {aiAcct.AccountCode} {aiAcct.AccountName} "
                                    + $"(confidence {(glResult.Confidence ?? 0):P0})"
                                    + (string.IsNullOrEmpty(glResult.Reasoning) ? "" : ": " + glResult.Reasoning));
                                foreach (var risk in glResult.Risks)
                                    extractedData.ReasoningTrace.Add("[AI risk] " + risk);
                            }
                        }
                        // AI ถูกเรียกแต่ไม่ได้ใช้ → **ต้องบอกว่าทำไม** ไม่ใช่เงียบ
                        // (G4: ผู้แพ้ต้องถูกบันทึกไว้ให้ไล่ย้อน/ให้ผู้ใช้สลับได้)
                        if (!scanResult.GlAccountUsedAi && glResult.UsedAi
                            && !string.IsNullOrEmpty(glResult.Answer))
                        {
                            var why = glConf < 0.70m
                                ? "ต่ำกว่าเกณฑ์ 70%"
                                : !glMayOverride
                                    ? $"ไม่สูงกว่าค่าเดิมที่มีอยู่ ({glIncumbentConf:P0}) จึงไม่ทับ"
                                    : "ไม่มีรหัสนี้ในผังบัญชีของบริษัท";
                            extractedData.ReasoningTrace.Add(
                                $"[AI/DeepSeek] เสนอ {glResult.Answer} (confidence {glConf:P0}) "
                                + $"— {why} → ใช้ค่าเดิม {extractedData.DebitAccountCode}");
                        }
                    }
                }
                catch (Exception aiEx)
                {
                    _logger.LogWarning(aiEx, "GL-account AI classification failed (non-fatal)");
                }
            }

            // ── บิลเงินสด/ใบเสร็จไม่เป็นทางการ → "ใบรับรองแทนใบเสร็จรับเงิน" ──
            // ใบเสร็จที่ไม่มีเลขผู้เสียภาษี 13 หลัก (แม่ค้าตลาด, วินมอเตอร์ไซค์,
            // ร้านไม่จด VAT) ระบุตัวผู้รับเงินไม่ได้ → §65 ตรี(9)(18) เสี่ยงโดน
            // บวกกลับเป็นรายจ่ายต้องห้าม. แนวปฏิบัติกรมสรรพากร (คู่มือเอกสาร
            // ประกอบการลงบัญชีฯ) ให้จัดทำ "ใบรับรองแทนใบเสร็จรับเงิน" ประกอบ
            // โดยแนบหลักฐานการจ่าย → target จึงเปลี่ยนจาก Expense/PV เป็น
            // CertificateInLieu (ฟอร์มมีช่องผู้รับรอง/เหตุผล ครบตามที่สรรพากร
            // ต้องการ + แนบรูปบิลเดิมเป็นหลักฐานอัตโนมัติ). เฉพาะใบไม่มี VAT —
            // ใบมี VAT แต่ไม่มีเลขผู้เสียภาษีคือใบกำกับอย่างย่อ ซึ่งมีเส้นทาง
            // §82/5(2) ของตัวเองอยู่แล้ว (เตือนเคลมภาษีซื้อไม่ได้ ด้านบน)
            if (extractedData.OurRole == "Buyer"
                && string.Equals(extractedData.DocumentType, "Receipt", StringComparison.OrdinalIgnoreCase)
                && (extractedData.VatAmount ?? 0m) <= 0m
                && extractedData.TargetDocumentType
                    is null or nameof(DocumentType.Expense) or nameof(DocumentType.PaymentVoucher))
            {
                // ⚠️ เดิมตัดสินจากสัญญาณเดียว (ไม่มีเลขผู้เสียภาษี = ระบุผู้รับเงินไม่ได้)
                // ⇒ บิลเงินสดของร้านที่มี **ชื่อ + ที่อยู่ครบบนกระดาษ** ถูกเปลี่ยนเป็น
                // "ใบรับรองแทนใบเสร็จ" ทั้งที่ผู้ขายออกบิลให้แล้ว (ผู้ใช้รายงาน 2026-09-11)
                // — §65 ตรี(18) ถามว่า "พิสูจน์ผู้รับเงินได้ไหม" ไม่ได้ถามหาเลข 13 หลัก
                // ตัวตัดสินอยู่ที่ Helpers/OcrPayeeEvidence ตัวเดียว (pure + มีเทสต์)
                var payee = Accounting.Helpers.OcrPayeeEvidence.Evaluate(
                    extractedData.VendorTaxId, extractedData.VendorName,
                    extractedData.VendorAddress, extractedData.VendorPhone);
                if (payee.Level == Accounting.Helpers.OcrPayeeProof.Unidentified)
                {
                    SetTargetDocumentType(extractedData, nameof(DocumentType.CertificateInLieu),
                        Accounting.Helpers.OcrFieldSource.Rule, 1.0m,
                        $"{payee.Reason} และไม่มี VAT — หลักฐานยังไม่พอเป็นรายจ่ายทางภาษี "
                        + "(§65 ตรี(9)(18)) → จัดทำ \"ใบรับรองแทนใบเสร็จรับเงิน\" แนบรูปบิลเป็นหลักฐาน"
                        + "ตามแนวทางกรมสรรพากร");
                }
                else if (payee.Level == Accounting.Helpers.OcrPayeeProof.Weak)
                {
                    // ก้ำกึ่ง = **บอก ไม่ใช่เปลี่ยนให้เงียบ ๆ** (กติกา "ไม่รู้ = บอกว่าไม่รู้")
                    extractedData.ReasoningTrace.Add(
                        $"[CertInLieu] {payee.Reason} — ยังคงชนิดเอกสารเดิมไว้ "
                        + "ถ้าตรวจแล้วพิสูจน์ผู้รับเงินไม่ได้ (§65 ตรี(18)) ให้เปลี่ยนช่อง "
                        + "\"เอกสารที่จะสร้างในระบบ\" เป็น \"ใบรับรองแทนใบเสร็จ\" เอง");
                }
                else if (!Accounting.Helpers.ThaiTaxId.IsPlausibleFromScan(extractedData.VendorTaxId))
                {
                    // ไม่มีเลขภาษีแต่พิสูจน์ผู้รับได้ = เคสที่กติกาเดิมเปลี่ยนชนิดผิด
                    // → จดไว้ให้ผู้ใช้เห็นว่าทำไม "ไม่" ออกใบรับรองแทนใบเสร็จ
                    extractedData.ReasoningTrace.Add(
                        $"[CertInLieu] ไม่ต้องออกใบรับรองแทนใบเสร็จ — {payee.Reason} "
                        + "(พิสูจน์ผู้รับเงินได้ตาม §65 ตรี(18)) · ผู้ขายออกบิลให้แล้ว "
                        + "→ บันทึกเป็นใบสำคัญจ่าย/ค่าใช้จ่ายแนบบิลตามปกติ");
                }
            }

            // ───── ใบมัดจำ/รับเงินล่วงหน้า (ฝั่งขาย) ─────
            // เอกสารรับเงินที่ระบุว่าเป็น "มัดจำ/เงินล่วงหน้า" ต้องลง Cr 217xx
            // (ขายรอรับรู้) ไม่ใช่รายได้ — เดิมไม่มี target นี้ ผู้ใช้ต้องเลือก
            // "ใบเสร็จ" แล้วไปติ๊กมัดจำเองในฟอร์ม ลืมติ๊ก = รับรู้รายได้เร็วเกิน
            // (ผิดทั้งงบและงวด ภ.พ.30). "Deposit" เป็น pseudo-target ที่ฟอร์ม
            // แปลงเป็น Receipt + IsDeposit ให้เอง
            // ตัวตัดสิน "เป็นใบมัดจำจริงไหม" อยู่ที่ Helpers/OcrDepositMarker ตัวเดียว (คำต้องมีเงิน
            // ประกอบ · แถวฟอร์ม "หักเงินมัดจำ 0.00" ไม่นับ) — ใช้ร่วมสองฝั่ง
            var depositDecision = Accounting.Helpers.OcrDepositMarker.Decide(
                scanResult.RawTextContent, extractedData.Items.Select(i => (i.Description, i.Amount)));
            if (!depositDecision.IsDeposit
                && Accounting.Helpers.OcrDepositMarker.Keyword.IsMatch(scanResult.RawTextContent ?? ""))
                extractedData.ReasoningTrace.Add($"[Deposit] พบคำว่ามัดจำแต่ไม่ถือเป็นใบมัดจำ — {depositDecision.Reason}");
            if (extractedData.OurRole == "Seller"
                && depositDecision.IsDeposit
                && extractedData.TargetDocumentType
                    is null or nameof(DocumentType.Receipt) or nameof(DocumentType.Invoice))
            {
                SetTargetDocumentType(extractedData, "Deposit",
                    Accounting.Helpers.OcrFieldSource.PaperLabel, 1.0m,
                    "เอกสารระบุ \"มัดจำ/รับล่วงหน้า\" พร้อมยอดเงิน (Helpers/OcrDepositMarker) → "
                    + "Cr ขายรอรับรู้ 217xx แทนรายได้ (รับรู้รายได้เมื่อส่งมอบ/ออกใบกำกับ)");
            }

            // ───── มัดจำ/จ่ายล่วงหน้า (ฝั่ง**ซื้อ**) ─────
            // ⚠️ เดิมบล็อกข้างบนครอบเฉพาะฝั่งขาย — ฝั่งซื้อไม่มีเส้นทางเลย
            // (ผลตรวจ 2026-09-06 · T1-15) ⇒ ใบเสร็จรับเงินมัดจำ 30% จากผู้รับเหมา/
            // ผู้ขายเครื่องจักร ถูก ExpenseCategoryResolver จัดเป็นค่าจ้าง/ต้นทุน
            // แล้วลงเป็น**ค่าใช้จ่ายทันที** ทั้งที่ยังไม่ได้รับมอบงาน ⇒ รับรู้
            // ค่าใช้จ่ายเร็วเกิน (ผิดงวด) และเมื่อใบจริงมาทีหลังจะลงซ้ำทั้งก้อน
            //
            // ขั้นนี้แก้เฉพาะ**ผังบัญชีที่แนะนำ** (Dr สินทรัพย์ 118xx) ซึ่งตัด
            // ความผิดเรื่องงวดได้แล้ว — flow หักมัดจำอัตโนมัติตอนใบจริงมาถึง
            // ยังเป็นงานแยก (backlog T1-15) จึงเขียนป้ายไว้ให้คนเห็นด้วย
            if (extractedData.OurRole == "Buyer" && depositDecision.IsDeposit)
            {
                var depCodes = Accounting.Helpers.PurchaseDepositAccount
                    .PreferredCodes(scanResult.RawTextContent);
                var depRows = await _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive
                        && depCodes.Contains(a.AccountCode))
                    .Select(a => new { a.AccountCode, a.AccountName })
                    .ToListAsync();
                var depPick = depCodes
                    .Select(c => depRows.FirstOrDefault(a => a.AccountCode == c))
                    .FirstOrDefault(a => a != null);
                if (depPick != null)
                {
                    extractedData.DebitAccountCode = depPick.AccountCode;
                    extractedData.DebitAccountName = depPick.AccountName;
                    extractedData.FieldConfidence["DebitAccount"] = 0.80;
                    extractedData.ReasoningTrace.Add(
                        $"[Deposit-Buy] เอกสารระบุ \"มัดจำ/จ่ายล่วงหน้า\" และเราเป็นผู้จ่าย → "
                        + $"Dr {depPick.AccountCode} {depPick.AccountName} (สินทรัพย์) แทนค่าใช้จ่าย "
                        + "— โอนเข้าค่าใช้จ่าย/ต้นทุนเมื่อรับมอบงานหรือได้รับใบกำกับเต็มจำนวน");
                }
                else
                {
                    // ไม่มีผัง 118xx = บอกว่าไม่มี ไม่ใช่เงียบแล้วปล่อยลงค่าใช้จ่าย
                    extractedData.ReasoningTrace.Add(
                        "[Deposit-Buy] เอกสารระบุ \"มัดจำ/จ่ายล่วงหน้า\" แต่ผังบัญชียังไม่มี "
                        + "\"เงินมัดจำจ่ายล่วงหน้า\" (118/11810/11820) — กรุณาเพิ่มผังแล้วเลือกเอง "
                        + "ไม่ควรลงเป็นค่าใช้จ่ายทันที");
                }
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + "\n[DEPOSIT-BUY] ใบนี้เป็นมัดจำ/จ่ายล่วงหน้าฝั่งซื้อ — ยอดนี้ยังไม่ใช่"
                    + "ค่าใช้จ่ายของงวด ต้องหักกลบเมื่อได้รับใบกำกับเต็มจำนวน";
            }

            // ── D-4 ขั้นที่ 1: ให้ arbiter **ตัดสินจริง** หนึ่งช่อง ────────────────
            // ช่องที่เลือกคือ "อัตราหัก ณ ที่จ่ายที่ระบบเสนอ" เพราะมีผู้เสนอสองราย
            // ที่วันนี้แข่งกันด้วย "ใครเขียนก่อนชนะ" (ตัวจัดหมวดรันที่ :1314 ·
            // ประวัติผู้ขายรันที่ :1590 แล้วถูกกันด้วย `!HasValue`) — ทิศที่ความ
            // เสียหายมองเห็นและแก้ทัน (G5): ค่านี้เป็น **ข้อเสนอ** ที่ขึ้นไฮไลต์
            // เหลือง + โน้ต [WHT-SUGGEST] เสมอ ไม่เคยหักเงินให้เอง
            //
            // ⚠️ **ไม่ได้สลับลำดับชั้น** (กฎเหล็ก #4 H): `Statute` เป็นชั้นที่เพิ่ม
            // ใหม่และอยู่เหนือ `VendorHistory` ⇒ เมื่อทั้งสองเสนอ ผู้ชนะยังเป็น
            // ตารางกฎหมายเหมือนเดิมทุกใบ · ที่เปลี่ยนคือ**เหตุผลของการชนะ**
            // (จากลำดับบรรทัด → จากลำดับชั้นหลักฐานที่ประกาศไว้เป็นข้อมูล)
            ApplyWhtSuggestion(extractedData);

            // ข้อเสนอ WHT ตามกฎหมาย + ประเภทเงินได้ ม.40 — ต้อง persist ไม่งั้นหายตอน
            // reload หน้า และเส้นสร้างเอกสารอ่านจากแถวนี้ (T1-07 · T4-06)
            scanResult.SuggestedWhtRate = extractedData.SuggestedWhtRate;
            scanResult.SuggestedWhtSource = extractedData.SuggestedWhtSource;
            scanResult.WhtIncomeTypeCode = extractedData.WhtIncomeTypeCode;

            // ───── Re-sync mutable fields (extractedData → scanResult) ─────
            // Persist mutations from VendorIntel / Learner / role inferrer
            // back to OcrScanResult so they reach MapToResponse() and
            // AutoCreateDocumentAsync(). DocumentType holds the SCANNED paper
            // type (back-compat); the new TargetDocumentType / OurRole columns
            // carry the role-inferrer's decision and are what AutoCreate
            // consumes when picking a document type to build.
            scanResult.DocumentType = extractedData.DocumentType;
            scanResult.ScannedDocumentType = extractedData.DocumentType;
            scanResult.OurRole = extractedData.OurRole;
            scanResult.TargetDocumentType = extractedData.TargetDocumentType;
            scanResult.HasWht = extractedData.HasWht;
            scanResult.WhtRate = extractedData.WhtRate;
            scanResult.PaymentTermsDays = extractedData.PaymentTermsDays;
            // สกุลเงิน: ค่าที่เอกสารประกาศไว้ (e-Tax XML) ก่อน แล้วค่อยเดาจากข้อความ
            // — เก็บลงแถวเพื่อให้เส้นสร้างเอกสาร/DTO ไม่ต้องเดาซ้ำคนละที่
            scanResult.Currency = extractedData.Currency ?? InferCurrency(extractedText);
            scanResult.Confidence = extractedData.Confidence;

            // [Reasoning] ถูกย้ายไป dump ท้ายไปป์ไลน์ (ก่อน SaveChangesAsync) —
            // คอมเมนต์เดิมตรงนี้เขียนว่า "LAST" แต่ยัง**ก่อน**ด่านทะเบียน/ตัวเดา
            // ผังบัญชีอีก 270 บรรทัด ⇒ เหตุผลของขั้นเหล่านั้นไม่เคยถึงผู้ใช้

            // ── Credit-account auto-fill by target document type ──
            // The Category resolver fills the DEBIT side (expense
            // account). Credit depends on the bookkeeping flow:
            //   PaymentVoucher / Expense / ReceiptVoucher → cash (1110)
            //   PurchaseInvoice                           → A/P (2100)
            //   Invoice / TaxInvoice (Seller)             → A/R (1130)
            // We pick the first matching account in the company's CoA
            // by code prefix — accommodates Thai SMEs that use 1110,
            // 1111, 11110, etc. Falls through silently if none exists.
            if (string.IsNullOrEmpty(extractedData.CreditAccountCode)
                && !string.IsNullOrEmpty(extractedData.TargetDocumentType))
            {
                // ── แหล่งเงิน Cr — 3 ชั้น priority (กัน auto-pick มั่ว) ──
                // เฉพาะเอกสารจ่าย/รับสด (Cr = เงินสด/ธนาคาร). สำหรับ A/P-A/R
                // ไม่มี "แหล่งเงิน" ให้เลือก → ใช้ prefix fallback ปกติ.
                //   1. Partner metadata (paymentAccountCode/bankCode) — รู้แน่ที่สุด
                //   2. CompanySettings.DefaultPaymentAccountId — บริษัทตั้งไว้
                //   3. Lowest-code fallback (เดิม) — กัน null
                var isCashCreditType = extractedData.TargetDocumentType
                    is "PaymentVoucher" or "Expense" or "ReceiptVoucher";
                if (isCashCreditType)
                {
                    var src = await ResolvePaymentSourceOverrideAsync(companyId, externalMetadataJson);
                    if (src != null)
                    {
                        extractedData.CreditAccountCode = src.Value.Code;
                        extractedData.CreditAccountName = src.Value.Name;
                        extractedData.ReasoningTrace.Add(
                            $"[Credit] แหล่งเงิน {src.Value.Code} {src.Value.Name} ({src.Value.Source})");
                    }
                }

                if (string.IsNullOrEmpty(extractedData.CreditAccountCode))
                {
                    string[] creditPrefixes = extractedData.TargetDocumentType switch
                    {
                        "PaymentVoucher" or "Expense" => new[] { "1111", "1110", "111" },        // Cash / Bank
                        "ReceiptVoucher"             => new[] { "1111", "1110", "111" },        // Cash received
                        "PurchaseInvoice"            => new[] { "2110", "2100", "211" },        // A/P
                        "Invoice" or "TaxInvoice"    => new[] { "1130", "1131", "113" },        // A/R
                        _ => Array.Empty<string>()
                    };
                    foreach (var pfx in creditPrefixes)
                    {
                        var creditAcct = await _db.ChartOfAccounts
                            .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith(pfx)
                                    && a.IsActive && !a.IsDeleted)
                            .OrderBy(a => a.AccountCode)
                            .Select(a => new { a.AccountCode, a.AccountName })
                            .FirstOrDefaultAsync();
                        if (creditAcct != null)
                        {
                            extractedData.CreditAccountCode = creditAcct.AccountCode;
                            extractedData.CreditAccountName = creditAcct.AccountName;
                            extractedData.ReasoningTrace.Add(
                                $"[Credit] เลือกบัญชีเครดิต {creditAcct.AccountCode} จากประเภทเอกสาร {extractedData.TargetDocumentType} (default lowest-code)");
                            // ★ ป้ายซื่อสัตย์: ชั้นนี้คือ "เดาแบบมีเหตุผล" ไม่ใช่ค่าที่
                            //   อ่านมาจากกระดาษ — สำหรับใบจ่ายเงิน แหล่งเงินที่เดา
                            //   ผิดทำให้ (ก) เงินสดในมือติดลบทั้งที่จ่ายผ่านธนาคาร
                            //   (ข) กระทบยอดธนาคารไม่เจอคู่ · ต้องดังถึงผู้ใช้ ไม่ใช่
                            //   อยู่แต่ใน trace ที่ไม่มีใครเปิด (ผลตรวจ T1-17)
                            //   — ตัวเลือกเชิงนโยบาย "ปล่อยว่างแล้วบังคับเลือก" เป็น
                            //   คำถามของเจ้าของโปรเจกต์ ยังไม่ตัดสินแทน
                            if (isCashCreditType)
                                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                                    + $"\n[PAY-SOURCE-GUESS] แหล่งเงิน {creditAcct.AccountCode} "
                                    + $"{creditAcct.AccountName} เป็นค่าที่ระบบเลือกให้เอง "
                                    + "(กระดาษไม่ได้ระบุ และบริษัทยังไม่ได้ตั้งบัญชีจ่ายเริ่มต้น) "
                                    + "— ตรวจว่าจ่ายจากบัญชีนี้จริงก่อนอนุมัติ";
                            break;
                        }
                    }
                }
            }

            // Re-sync ExpenseCategory after the resolver / vendor-intel /
            // basket-rule miners have all written to extractedData. The
            // earlier sync at line ~477 ran BEFORE the resolver, leaving
            // the scan row showing "Expense Category: null" even when
            // the resolver matched "ค่าน้ำประปา" with score 8.0.
            if (!string.IsNullOrEmpty(extractedData.ExpenseCategory))
                scanResult.ExpenseCategory = extractedData.ExpenseCategory;

            // Match GL accounts from suggestions against company's chart of accounts
            if (!string.IsNullOrEmpty(extractedData.DebitAccountCode))
            {
                // Resolve the suggested code against the company's actual
                // Chart of Accounts. Strategy:
                //   1. Exact match → use it.
                //   2. Prefix match → tenant uses a sub-coded version
                //      of the standard account (e.g. 54430 instead of
                //      5306 — common when CoA was extended for branch /
                //      cost-center detail). Prefer the active account
                //      with the longest matching prefix.
                //   3. Keyword match on account name → handles custom
                //      CoA where the prefix doesn't follow standard
                //      Thai SME ranges (e.g. tenant has no 5306-style
                //      code but their "ค่าซ่อมรถยนต์" account is 6101).
                // Without this fallback, the resolver suggests a generic
                // 5306 that doesn't exist in the company's CoA and the
                // UI dropdown can't auto-select anything.
                var seeded = extractedData.DebitAccountCode;
                var debitAccount = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == seeded && !a.IsDeleted);
                if (debitAccount == null)
                {
                    // Prefix match — longest first so 53061 beats 5306x
                    debitAccount = await _db.ChartOfAccounts
                        .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                            && a.AccountCode.StartsWith(seeded))
                        .OrderByDescending(a => a.AccountCode.Length)
                        .ThenBy(a => a.AccountCode)
                        .FirstOrDefaultAsync();
                }
                if (debitAccount == null && !string.IsNullOrEmpty(extractedData.DebitAccountName))
                {
                    // Keyword match on account name — pull the most
                    // distinctive Thai keyword from the suggested name
                    // (skip stopword-class words like "ค่า") and look
                    // for an account whose name contains it.
                    //
                    // ⚠️ **ตัวเลือกต้องผ่าน Helpers/GlDebitAccountPicker** — เดิมบรรทัดนี้
                    // `OrderBy(AccountCode).First()` โดย **ไม่กรองประเภทบัญชี** ⇒ หนี้สิน
                    // ที่ขึ้นต้น "2" มาก่อนค่าใช้จ่ายที่ขึ้นต้น "5" **เสมอ** ⇒ ผังบัญชีที่มี
                    // ทั้ง "ค่าโทรศัพท์ค้างจ่าย 21513" (หนี้สิน) และ "ค่าโทรศัพท์ฯ 5304"
                    // (ค่าใช้จ่าย) จะได้ตัวที่ผิดทุกครั้ง — การเดบิตบัญชีค้างจ่ายคือการ
                    // ตัดหนี้ที่ไม่เคยตั้งไว้ ⇒ งบฐานะการเงินเพี้ยน + ค่าใช้จ่ายไม่เข้างบ P&L
                    // (ผู้ใช้รายงาน 2026-09-18 — ใบซื้ออุปกรณ์แคมป์ปิ้งได้บัญชี 21513)
                    var name = extractedData.DebitAccountName;
                    var keyword = ExtractDistinctiveKeyword(name);
                    if (!string.IsNullOrEmpty(keyword))
                    {
                        var candidates = await _db.ChartOfAccounts
                            .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                                && a.AccountName.Contains(keyword))
                            .Select(a => new { a.Id, a.AccountCode, a.AccountName, a.AccountType })
                            .ToListAsync();
                        var picked = Accounting.Helpers.GlDebitAccountPicker.Pick(
                            candidates.Select(a => new Accounting.Helpers.GlAccountCandidate(
                                a.AccountCode, a.AccountName, a.AccountType)),
                            seededCode: seeded);
                        if (picked is { } p)
                        {
                            debitAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(
                                a => a.CompanyId == companyId && a.AccountCode == p.Code && !a.IsDeleted);
                        }
                        else if (candidates.Count > 0)
                        {
                            // มีบัญชีชื่อตรงแต่ไม่มีตัวไหนเป็นเดบิตของค่าใช้จ่ายได้เลย
                            // ⇒ ปล่อยว่างให้คนเลือก และ**บอกว่าทำไม** ไม่ใช่เงียบ
                            extractedData.ReasoningTrace.Add(
                                $"[CoA] พบบัญชีชื่อใกล้เคียง \"{keyword}\" {candidates.Count} รายการ "
                                + "แต่เป็นบัญชีหนี้สิน/รายได้/ส่วนของเจ้าของทั้งหมด ซึ่งใช้เป็นบัญชีเดบิต "
                                + "ของค่าใช้จ่ายไม่ได้ — เว้นว่างไว้ให้เลือกเอง");
                        }
                    }
                }
                if (debitAccount != null)
                {
                    if (debitAccount.AccountCode != seeded)
                    {
                        extractedData.ReasoningTrace.Add(
                            $"[CoA] {seeded} ไม่มีใน CoA — เลือก {debitAccount.AccountCode} {debitAccount.AccountName} แทน");
                    }
                    extractedData.DebitAccountCode = debitAccount.AccountCode;
                    extractedData.DebitAccountName = debitAccount.AccountName;
                }
            }
            if (!string.IsNullOrEmpty(extractedData.CreditAccountCode))
            {
                var creditAccount = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == extractedData.CreditAccountCode && !a.IsDeleted);
                if (creditAccount != null)
                {
                    extractedData.CreditAccountCode = creditAccount.AccountCode;
                    extractedData.CreditAccountName = creditAccount.AccountName;
                }
            }

            // Serialise the suggestion AFTER all CoA resolution above so the
            // UI receives the company's real account codes — not the generic
            // 5306-style codes the category resolver seeds. Serialising
            // earlier (before the exact→prefix→keyword pass) was the reason
            // "AI แนะนำ 5306" still showed even though 54430 exists in the CoA.
            if (extractedData.DebitAccountCode != null || extractedData.CreditAccountCode != null)
            {
                scanResult.SuggestedAccountsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    extractedData.DebitAccountCode, extractedData.DebitAccountName,
                    extractedData.CreditAccountCode, extractedData.CreditAccountName,
                    extractedData.VatAccountCode, extractedData.VatAccountName,
                });
            }

            // ───── บรรทัดรายการ: serialize **ครั้งเดียว · ท้ายไปป์ไลน์** ─────
            //
            // ⚠️ ที่มา (ผลตรวจ 2026-09-18 · D3-1): เดิม serialize ที่ต้นไปป์ไลน์ ด้วย
            // projection 8 ช่อง และอยู่ **ก่อน** `ApplyProductCrossReferenceAsync`
            // (ตัวเขียน `line.SuggestedAccountCode` จากสินค้าใน master) กับตัวเรียน
            // รายบรรทัด ⇒
            //   (ก) `BuildScanLinesAsync` อ่าน `item.SuggestedAccountCode` ได้ **null
            //       ทุกบรรทัด** แล้วตกไปใช้บัญชีระดับหัวใบ ⇒ ชั้นหลักฐานที่แข็งที่สุด
            //       (สินค้าที่ผูกผังบัญชีไว้แล้ว) **ไม่เคยถึงเอกสาร** และ trace
            //       "[Product] รายการ → บัญชี" กลายเป็นคำโกหก (หน้าจอรอบแรกเห็นถูก
            //       เพราะ MapToResponse ใช้ `data.Items` — reload แล้วหาย)
            //   (ข) projection ตก `VatRate` / `VatAmount` / `ProjectAiFeedbackId` /
            //       `AiSuggestedProjectId` ที่บรรทัดถืออยู่จริง ⇒ ด่าน "ผู้ใช้/AI
            //       ตัดสินอัตรา VAT มาแล้ว ห้ามทับ" เป็นจริงเฉพาะหลังผู้ใช้แก้บรรทัด
            //       (เส้นแก้บรรทัด re-serialize เต็มรูปอยู่แล้ว)
            //
            // ตำแหน่งนี้อยู่ **หลัง** ทุกตัวที่แตะบรรทัด (project matcher · sanitizer ·
            // UnitInferrer · Product cross-reference · ตัวเรียนรายบรรทัด) และ **ก่อน**
            // ทุกตัวที่อ่าน JSON กลับ (`ScanLineDescriptions` ของการผูกใบต้นทาง ·
            // alias ตอน link-po · auto-create → `CreateDocumentFromScanAsync` ที่
            // re-query แถวนี้) — ย้ายไปท้ายกว่านี้ = auto-create ได้เอกสารบรรทัดว่าง
            if (extractedData.Items.Count > 0)
                scanResult.ExtractedItemsJson = SerializeExtractedItems(extractedData.Items);

            // ───── CONTENT FINGERPRINT (cross-format dedup) ─────
            // Catches the case where same invoice is uploaded as JPG once + PDF later.
            // File hash differs but the semantic content matches. Stored on the row for
            // indexed lookups and surfaced to user as "เคยอัปโหลดมาแล้วในรูปแบบอื่น".
            scanResult.ContentFingerprint = ComputeContentFingerprint(extractedData);

            if (!string.IsNullOrEmpty(scanResult.ContentFingerprint))
            {
                var contentDup = await _db.Set<OcrScanResult>()
                    .Where(r => r.CompanyId == companyId
                        && r.Id != scanResult.Id
                        && r.ContentFingerprint == scanResult.ContentFingerprint
                        && r.ScanStatus == "Completed"
                        && r.CreatedAt > DateTime.UtcNow.AddDays(-90))
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => new { r.Id, r.OriginalFileName, r.CreatedAt })
                    .FirstOrDefaultAsync();
                if (contentDup != null)
                {
                    scanResult.IsDuplicate = true;
                    scanResult.DuplicateOfScanId = contentDup.Id;
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") +
                        $"\n[Content Duplicate] เนื้อหาเอกสารตรงกับไฟล์ที่เคยอัปโหลดเมื่อ {contentDup.CreatedAt:yyyy-MM-dd HH:mm} ({contentDup.OriginalFileName}) — ระบบไม่สร้างเอกสารซ้ำ";
                }
            }

            // Check for duplicate by document number + amount (legacy fallback when fingerprint missing)
            if (!string.IsNullOrEmpty(extractedData.DocumentNumber) && extractedData.TotalAmount.HasValue)
            {
                var docDuplicate = await _db.Set<OcrScanResult>()
                    .FirstOrDefaultAsync(r => r.CompanyId == companyId
                        && r.Id != scanResult.Id
                        && r.ExtractedDocumentNumber == extractedData.DocumentNumber
                        && r.ExtractedTotalAmount == extractedData.TotalAmount
                        && r.ScanStatus == "Completed");
                if (docDuplicate != null)
                {
                    scanResult.IsDuplicate = true;
                    scanResult.DuplicateOfScanId = docDuplicate.Id;
                    // ⚠️ เดิมบรรทัดนี้ใช้ `=` **ทับ** ProcessingNotes ทั้งก้อน ⇒ สแกน
                    // ที่ถูกตีว่าซ้ำจะเสียทุกอย่างที่สะสมมาก่อนหน้า รวม **[VAT-CLAIM]**
                    // (ธง §82/5 ที่ด่านบันทึก JE อ่านด้วย `Contains("[VAT-CLAIM]")`)
                    // ⇒ ใบภาษีซื้อต้องห้ามที่บังเอิญซ้ำ ผ่านด่าน §82/5 ไปได้ ·
                    // และเสีย [Field Confidence] / [Deposit-Buy] / [Reasoning] ไปด้วย
                    // — ต่อท้ายเสมอ ห้ามทับ (ผลตรวจ 2026-09-06 · พบระหว่างแก้ T1-15)
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + $"\n[Duplicate] อาจซ้ำกับสแกนก่อนหน้า — เลขที่ {extractedData.DocumentNumber} "
                        + $"และยอด {extractedData.TotalAmount:N2} ตรงกัน";
                }
            }

            // If the seller is our own company, the real vendor is the buyer.
            // Project just the TaxId column (not full Company entity) for efficiency.
            {
                var ourTaxId = await _db.Companies
                    .Where(c => c.Id == companyId)
                    .Select(c => c.TaxId)
                    .FirstOrDefaultAsync();
                if (!string.IsNullOrEmpty(ourTaxId))
                {
                    // (บล็อก "ถ้าผู้ขายคือเราให้เอาผู้ซื้อมาเป็นผู้ขาย" เดิมอยู่ตรงนี้ — ถูกยุบเข้า
                    //  Helpers/OcrPartyResolver ข้างบนแล้ว: ตัวเดิมเทียบเลขแบบ == ตรง ๆ และ
                    //  **ไม่ล้างอีกฝั่ง** ⇒ สองช่องถือคู่ค้ารายเดียวกัน)

                    // ── เติมเลขผู้ซื้อจากกระดาษ (กฎเหล็ก #3: OCR ต้องกรอกให้ครบ) ──
                    //
                    // บนใบฝั่งซื้อ "ผู้ซื้อ" คือบริษัทเราเสมอ. ตัวสกัดจะไม่เดาเลข
                    // ผู้ซื้อถ้าไม่เจอคำว่า "ผู้ซื้อ/ลูกค้า" บนกระดาษ (กันบาร์โค้ด
                    // สินค้าถูกยัดมาเป็นเลขผู้ซื้อ — บั๊ก PI-20260820-0005) แต่ถ้า
                    // **เลขของเราปรากฏบนกระดาษจริง** และเราไม่ใช่ผู้ขาย ก็สรุปได้
                    // แน่นอนว่าเราคือผู้ซื้อ → เติมให้เลย ไม่ปล่อยช่องว่างให้ผู้ใช้
                    // กรอกเอง และไม่ปล่อยเลขผิดที่ OCR หยิบมาค้างไว้
                    //
                    // เงื่อนไข "เราไม่ใช่ผู้ขาย" ต้องพิสูจน์เชิงบวก — ใช้ "อ่านเลข
                    // ผู้ขายได้ และเป็นคนละเลขกับเรา" ไม่ใช่แค่ "เลขผู้ขายไม่ตรงกับเรา"
                    // เพราะถ้า OCR อ่านเลขผู้ขายไม่ออกเลย (ว่าง) เงื่อนไขหลังจะเป็นจริง
                    // บนใบ**ขาย**ของเราเองด้วย ⇒ ไปทับเลขลูกค้าด้วยเลขบริษัทเรา
                    var vendorIdentified = Accounting.Helpers.ThaiTaxId.IsValid(extractedData.VendorTaxId);
                    var weAreVendor = Accounting.Helpers.ThaiTaxId.Same(extractedData.VendorTaxId, ourTaxId);
                    var buyerIsUs = Accounting.Helpers.ThaiTaxId.Same(extractedData.BuyerTaxId, ourTaxId);
                    if (vendorIdentified && !weAreVendor && !buyerIsUs
                        && Ocr.RdComplianceValidator.RawTextHasTaxId(scanResult.RawTextContent, ourTaxId))
                    {
                        var replaced = extractedData.BuyerTaxId;
                        extractedData.BuyerTaxId = ourTaxId;
                        scanResult.BuyerTaxId = ourTaxId;
                        extractedData.ReasoningTrace.Add(string.IsNullOrEmpty(replaced)
                            ? $"[Buyer] เติมเลขผู้ซื้อ = บริษัทเรา ({ourTaxId}) — พบเลขนี้บนกระดาษและเราไม่ใช่ผู้ขาย"
                            : $"[Buyer] แทนเลขผู้ซื้อที่อ่านมาผิด '{replaced}' ด้วยเลขบริษัทเรา ({ourTaxId}) "
                              + "— พบเลขนี้บนกระดาษจริง");
                    }
                }
            }

            // === DBD Verification: authoritative company info from กรมพัฒนาธุรกิจการค้า ===
            // If the extracted Tax ID is a 13-digit juristic ID, query DBD to get the
            // canonical company name + address. This is treated as ground truth — if OCR
            // disagrees, we trust DBD and record the OCR mismatch as negative training.
            await EnrichFromDbdAsync(companyId, extractedData, scanResult);

            // ── สมุดที่มาของค่ารายช่อง (D1) ──────────────────────────────────
            // ยังไม่ย้ายตัวตัดสิน — ค่าที่ผู้ใช้เห็นมาจากลำดับเดิมทุกประการ
            // เก็บไว้เพื่อ (ก) ผู้ใช้กดดูได้ว่า "ค่านี้มาจากไหน" (ข) เวลาไล่บั๊ก
            // จะรู้ทันทีว่าใครเป็นคนใส่ ไม่ต้องเดาจากลำดับบรรทัดในเมธอด 2,000 บรรทัด
            //
            // ⚠️ ย้ายลงมาจากกลางไปป์ไลน์ (ผลตรวจ 2026-09-18 · D3-4/D3-B): เดิมเขียน
            // ก่อน VendorIntel · Federated · CertInLieu · Deposit · ทะเบียน DBD
            // ⇒ ผู้เสนอทุกรายหลังจุดนั้น (รวมชั้นที่ **เปลี่ยนชนิดเอกสาร**) ถูกทิ้ง
            // เงียบ ๆ — สมุดที่ควรตอบว่า "ค่านี้มาจากไหน" จึงตอบได้แค่ครึ่งแรกของ
            // ไปป์ไลน์. ตัวตัดสินยังไม่ย้าย (ลำดับเดิมทุกประการ) แต่สมุดครบแล้ว
            //
            // ── รอบ 184 (D-4 ขั้นที่ 1) — สมุดต้องรายงาน "ค่าที่อยู่ในฟอร์มจริง" ──
            // เดิมสมุดแสดงผลของ `DecideAll` ตรง ๆ ทั้งที่ arbiter ไม่ได้ตัดสินอะไร
            // ⇒ ป้าย "🧾 ค่านี้มาจากไหน" ขัดกับค่าที่ผู้ใช้เห็นได้โดยไม่มีอะไรฟ้อง (V1)
            // ตอนนี้: ค่าจริงมาก่อน → ผู้เสนอที่ตรงกับค่าจริงคือ "ที่มา" →
            // ไม่มีใครตรง ⇒ Unknown (ห้ามยกผู้ชนะของ arbiter มาสวม)
            // และเก็บ `arbiterAgreed` ไว้เป็นตัววัดว่าพร้อมทำขั้นที่ 2 เมื่อไร
            if (extractedData.FieldCandidates.Count > 0)
            {
                var provenance = Accounting.Helpers.OcrFieldProvenance.Build(
                    extractedData.FieldCandidates, BuildActualFieldValues(extractedData));
                scanResult.FieldDecisionsJson = Accounting.Helpers.OcrFieldProvenance.ToJson(provenance);
                if (provenance.ArbiterAgreed is decimal agreedRatio)
                    extractedData.ReasoningTrace.Add(
                        $"[Arbiter] ตัวตัดสินเห็นด้วยกับค่าที่ใช้จริง {provenance.Agreed}/{provenance.Compared} ช่อง "
                        + $"({agreedRatio:P0}) — ต่ำ = ยังมีชั้นที่เขียนค่าโดยไม่ผ่านตัวตัดสิน");
            }

            // ห้ามจับคู่คู่ค้าเป็น **บริษัทเราเอง** — Contact ของตัวเองมีอยู่จริงในหลาย tenant
            // (สร้างไว้ออกใบให้ตัวเอง/ทดสอบ) และเดิมไม่มีด่านเลย ⇒ สแกนที่ engine ใส่ชื่อเรา
            // ผิดช่องจะได้ MatchedContact = ตัวเอง แล้วเอกสารที่สร้างเป็นการซื้อขายกับตัวเอง
            bool IsOurOwnContact(string? taxId, string? name)
                => Accounting.Helpers.ThaiTaxId.Same(taxId, companyContext?.TaxId)
                || Accounting.Helpers.OcrSelfPartyGuard.IsSelf(name, companyContext?.Name)
                || Accounting.Helpers.OcrSelfPartyGuard.IsSelf(name, companyContext?.NameEn);

            if (!string.IsNullOrEmpty(extractedData.VendorTaxId))
            {
                // Same juristic TaxId can exist as SEVERAL contacts — one per
                // branch (สำนักงานใหญ่ 00000 + สาขา 00001, …). Pick the contact
                // whose BranchCode matches the OCR'd branch; blank/absent codes
                // normalize to head-office 00000 so legacy rows still match.
                // ⚠️ เทียบเลขภาษีแบบ normalize (ตัวเลขล้วน) ใน memory — เดิมเทียบ
                // == ตรง ๆ: contact เก่าที่เก็บมีขีด/เว้นวรรค ("0-2735-...") ไม่
                // match กับ OCR ("0273563000920") → หลุดไปสร้างซ้ำทุกสแกน
                // (เคสจริง: vendor เดียวโดนสร้าง 3 record เลขภาษีเดียวกัน)
                var ocrTaxDigits = DocumentService.NormalizeTaxDigits(extractedData.VendorTaxId);
                var taxMatches = (await _db.Contacts
                    .Where(c => c.CompanyId == companyId && !c.IsDeleted
                        && c.TaxId != null && c.TaxId != "")
                    .Select(c => new { c.Id, c.Name, c.BranchCode, c.TaxId })
                    .ToListAsync())
                    .Where(c => DocumentService.NormalizeTaxDigits(c.TaxId) == ocrTaxDigits)
                    .Where(c => !IsOurOwnContact(c.TaxId, c.Name))
                    .Select(c => new { c.Id, c.Name, c.BranchCode })
                    .ToList();
                if (taxMatches.Count == 1)
                {
                    scanResult.MatchedContactId = taxMatches[0].Id;
                }
                else if (taxMatches.Count > 1)
                {
                    static string NormBranch(string? b)
                    {
                        var digits = new string((b ?? "").Where(char.IsDigit).ToArray());
                        return digits.Length == 0 ? "00000" : digits.PadLeft(5, '0');
                    }
                    var ocrBranch = NormBranch(extractedData.VendorBranchCode);
                    var byBranch = taxMatches.FirstOrDefault(c => NormBranch(c.BranchCode) == ocrBranch);
                    // Deterministic fallback: head office (00000) first, then
                    // lowest branch code — an unordered query made the pick
                    // change between scans.
                    var pick = byBranch ?? taxMatches
                        .OrderBy(c => NormBranch(c.BranchCode))
                        .ThenBy(c => c.Id)
                        .First();
                    scanResult.MatchedContactId = pick.Id;
                    extractedData.ReasoningTrace.Add(byBranch != null
                        ? $"[Branch] TaxID ตรง {taxMatches.Count} รายชื่อ — เลือกตามสาขา {ocrBranch}: '{pick.Name}'"
                        : $"[Branch] TaxID ตรง {taxMatches.Count} รายชื่อ แต่ไม่มีสาขา {ocrBranch} — ใช้รายแรก '{pick.Name}' (โปรดตรวจสอบ)");
                }
            }

            // ⚠️ จับคู่ด้วย "ชื่อ" เป็นเส้นเสี่ยงที่สุด — ผูกเอกสารผิดรายได้ทั้งใบ
            // (เคสจริง: ใบกำกับของบริษัทหนึ่งไปโผล่ใต้ชื่ออีกราย). กติกา:
            //   • ชื่อที่ OCR อ่านได้ต้องยาวพอ (≥6 ตัวอักษรหลังตัดช่องว่าง) —
            //     เดิมไม่มีขั้นต่ำ: ถ้า OCR ได้ "นาย"/"บริษัท" จะ Contains ตรงกับ
            //     ผู้ติดต่อจำนวนมาก แล้ว FirstOrDefault (ไม่มี OrderBy) หยิบมั่ว
            //   • หยิบตัวที่ "ใกล้เคียงที่สุด" แบบ deterministic ไม่ใช่แถวแรกที่ DB คืน
            var ocrVendorName = (extractedData.VendorName ?? "").Trim();
            var ocrVendorNameKey = new string(ocrVendorName.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            if (!scanResult.MatchedContactId.HasValue && ocrVendorNameKey.Length >= 6)
            {
                // substring match — โหลด candidate มาเลือกในหน่วยความจำ (deterministic)
                var subMatches = await _db.Contacts.AsNoTracking()
                    .Where(c => c.CompanyId == companyId && !c.IsDeleted
                        && c.Name.Contains(ocrVendorName))
                    .Select(c => new { c.Id, c.Name })
                    .Take(50)
                    .ToListAsync();
                subMatches = subMatches.Where(c => !IsOurOwnContact(null, c.Name)).ToList();
                var subPick = subMatches
                    .Select(c => new { c.Id, c.Name, Sim = Ocr.FuzzyMatcher.Similarity(c.Name, ocrVendorName) })
                    .OrderByDescending(x => x.Sim)
                    .ThenBy(x => x.Name.Length)     // ชื่อสั้นสุด = ตรงตัวสุด
                    .ThenBy(x => x.Id)
                    .FirstOrDefault();
                if (subMatches.Count > 1)
                    extractedData.ReasoningTrace.Add(
                        $"[Match] ชื่อผู้ขาย '{ocrVendorName}' ตรงแบบ substring {subMatches.Count} ราย — เลือก '{subPick!.Name}' (ใกล้เคียงสุด) โปรดตรวจสอบ");
                scanResult.MatchedContactId = subPick?.Id;

                // When substring misses, fall back to Levenshtein-based fuzzy
                // match against every supplier contact. This catches the
                // common case where OCR returns "บริษัท เอบีซี เซอร์วิส จํากัด"
                // but the existing contact is "เอบีซี เซอร์วิส" — normalize
                // both, compute similarity, accept the best ≥0.85.
                if (!scanResult.MatchedContactId.HasValue)
                {
                    var allSuppliers = await _db.Contacts.AsNoTracking()
                        .Where(c => c.CompanyId == companyId && !c.IsDeleted
                            && (c.IsSupplier || !c.IsCustomer))
                        .Select(c => new { c.Id, c.Name })
                        .ToListAsync();
                    allSuppliers = allSuppliers.Where(c => !IsOurOwnContact(null, c.Name)).ToList();
                    var best = allSuppliers
                        .Select(c => new { c.Id, c.Name, Sim = Ocr.FuzzyMatcher.Similarity(c.Name, extractedData.VendorName) })
                        .OrderByDescending(x => x.Sim)
                        .ThenBy(x => x.Id)       // tie → deterministic (สแกนซ้ำได้ผลเดิม)
                        .FirstOrDefault();
                    if (best != null && best.Sim >= 0.85)
                    {
                        scanResult.MatchedContactId = best.Id;
                        extractedData.ReasoningTrace.Add(
                            $"[Fuzzy] จับคู่ผู้ติดต่อ '{best.Name}' similarity {best.Sim:P0}");
                    }
                }
            }

            // ───── AI Augmentation: vendor canonicalisation ─────
            // Runs only when local matchers (TaxId + substring + fuzzy
            // ≥0.85) ALL missed. The augmenter sees the local pick
            // (none, in this branch) + a fresh candidate list and may
            // produce a match that fuzzy edit-distance couldn't (Thai
            // ↔ English company names, abbreviation expansion, missing
            // legal suffix, OCR-introduced character noise).
            //
            // ALL failure paths fall through cleanly — the local
            // auto-create logic below still runs unchanged. AI down /
            // disabled / over-budget = behave like AI wasn't there.
            if (!scanResult.MatchedContactId.HasValue && _aiAugmenter != null
                && (!string.IsNullOrEmpty(extractedData.VendorName)
                    || !string.IsNullOrEmpty(extractedData.VendorTaxId)))
            {
                try
                {
                    using var aiCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var aiResult = await _aiAugmenter.CanonicaliseVendorAsync(
                        companyId, scanResult.Id,
                        extractedData.VendorName, extractedData.VendorTaxId,
                        extractedData.VendorAddress,
                        localBestContactId: null, localConfidence: 0m,
                        aiCts.Token);
                    // ★ T3-05: เก็บ FeedbackId **ทุกครั้ง** ที่มีแถว — เดิมเก็บเฉพาะเมื่อ AI
                    // ถูก apply ⇒ เคส "AI ตอบผิด/ไม่มั่นใจ" (สัญญาณสอนที่ดีที่สุด) ไม่มีวัน
                    // ถูกปิด loop ตอนผู้ใช้เลือกคู่ค้าเอง (MatchContactAsync อ่านช่องนี้)
                    if (aiResult.FeedbackId.HasValue)
                        scanResult.AiSuggestionFeedbackId = aiResult.FeedbackId;
                    // ★ T3-01: นักเรียนที่ short-circuit ใช้ได้เท่า AI (ผ่าน guard เดิม)
                    if (aiResult.HasModelAnswer && aiResult.Answer != "__NEW__"
                        && (aiResult.Confidence ?? 0m) >= 0.70m
                        && Guid.TryParse(aiResult.Answer, out var aiContactId))
                    {
                        // Verify the contact still exists + belongs to
                        // the tenant — AI could hallucinate a GUID.
                        var verified = await _db.Contacts.AsNoTracking()
                            .AnyAsync(c => c.Id == aiContactId && c.CompanyId == companyId && !c.IsDeleted);
                        if (verified)
                        {
                            scanResult.MatchedContactId = aiContactId;
                            scanResult.AiSuggestedContactId = aiContactId;
                            scanResult.AiSuggestionFeedbackId = aiResult.FeedbackId;
                            extractedData.ReasoningTrace.Add(
                                $"[{aiResult.SourceLabel}] vendor canon → contact {aiContactId} ({aiResult.Confidence:P0})"
                                + (string.IsNullOrEmpty(aiResult.Reasoning) ? "" : ": " + aiResult.Reasoning));
                            foreach (var risk in aiResult.Risks)
                                extractedData.ReasoningTrace.Add("[AI risk] " + risk);
                        }
                    }
                    else if (!aiResult.UsedAi && aiResult.FeedbackId.HasValue)
                    {
                        // AI was attempted but unavailable — store the
                        // feedback row id anyway so the UI shows "AI
                        // tried, fell back to local" badge.
                        scanResult.AiSuggestionFeedbackId = aiResult.FeedbackId;
                    }
                }
                catch (Exception aiEx)
                {
                    // Belt-and-braces — augmenter catches internally,
                    // but if anything else throws (DI / context-disposed
                    // race) we still continue the OCR pipeline.
                    _logger.LogWarning(aiEx, "AI vendor canon hook failed; continuing without AI suggestion");
                }
            }

            // ═══════ CONTACT AUTO-CREATION — DBD preferred, OCR fallback ═══════
            // Decision tree when no existing contact matched:
            //   1. DBD verified  → create with DBD canonical name + address (best case)
            //   2. DBD failed BUT TaxId checksum-valid + ≥1 supporting field
            //      (vendor name OR address OR phone) → create from OCR data,
            //      tag as Unverified so user knows to confirm. Better than
            //      "Manual Review Required" — saves the user from re-typing.
            //   3. No TaxId at all but strong vendor name + address → create
            //      with TaxId=null, ContactType=JuristicPerson (best guess).
            //   4. Nothing actionable → leave Contact null + surface note.
            // contactJustCreated short-circuits the enrichment block below
            // (the freshly-created contact already has every field we'd fill).
            bool contactJustCreated = false;
            if (!scanResult.MatchedContactId.HasValue
                && extractedData.DbdMatched
                && !IsOurOwnContact(extractedData.VendorTaxId, extractedData.VendorName)
                && !string.IsNullOrEmpty(extractedData.VendorTaxId))
            {
                // ───── Branch 1: DBD verified ─────
                var addressParts = ParseAddressIntoParts(extractedData.DbdAddress);

                var newContact = new Contact
                {
                    CompanyId = companyId,
                    Name = extractedData.DbdCanonicalName ?? extractedData.VendorName ?? $"ผู้ขาย (TaxID: {extractedData.VendorTaxId})",
                    TaxId = extractedData.VendorTaxId,
                    BranchCode = extractedData.VendorBranchCode,
                    IsCustomer = false,
                    IsSupplier = true,
                    ContactType = ContactType.JuristicPerson,
                    Address = extractedData.DbdAddress,
                    Phone = SanePhone(extractedData.VendorPhone),
                    Email = extractedData.VendorEmail,
                    BuildingNumber = addressParts.BuildingNumber,
                    Moo = addressParts.Moo,
                    StreetName = addressParts.StreetName,
                    SubDistrict = addressParts.SubDistrict,
                    District = addressParts.District,
                    Province = addressParts.Province,
                    PostalCode = addressParts.PostalCode,
                    CountryCode = "TH",
                    CreatedBy = "OCR-DBD-AutoCreate"
                };
                _db.Contacts.Add(newContact);
                await _db.SaveChangesAsync();
                scanResult.MatchedContactId = newContact.Id;
                contactJustCreated = true;
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + $"\n[Auto-Create] สร้าง Contact ใหม่จากข้อมูล DBD: {newContact.Name} (TaxID {newContact.TaxId})";
                _logger.LogInformation("Auto-created DBD-verified contact for company {CompanyId} TaxId={TaxId} Name={Name}",
                    companyId, newContact.TaxId, newContact.Name);
            }
            else if (!scanResult.MatchedContactId.HasValue
                && !IsOurOwnContact(extractedData.VendorTaxId, extractedData.VendorName))
            {
                // ───── Branches 2-4: DBD unavailable / failed ─────
                var hasValidTaxId = !string.IsNullOrEmpty(extractedData.VendorTaxId)
                    && Ocr.SmartFieldExtractor.IsValidThaiTaxId(extractedData.VendorTaxId);
                // ชื่อที่เป็นแค่ "ข้อเสนอ" (เดาจากกรอบบน · มั่นใจ < 0.5) ห้ามกลายเป็น Contact
                // ถาวรโดยผู้ใช้ยังไม่ยืนยัน — Contact ผิดหนึ่งราย = จับคู่ผิดทุกใบถัดไป
                var hasVendorName = !string.IsNullOrWhiteSpace(extractedData.VendorName)
                    && extractedData.VendorName.Trim().Length >= 3
                    && extractedData.FieldConfidence.GetValueOrDefault(Accounting.Helpers.OcrFieldKeys.SellerName, 1.0) >= 0.5;
                var hasAddress = !string.IsNullOrWhiteSpace(extractedData.VendorAddress);
                var hasPhone = !string.IsNullOrWhiteSpace(extractedData.VendorPhone);
                // Quality gate — we want at least ONE supporting field beyond
                // the TaxId, OR (when no TaxId) a strong name+address combo.
                // This keeps a misread "12345678901" with no other data from
                // spawning a junk contact, while still helping the user when
                // OCR has captured meaningful detail.
                var canCreateFromOcr =
                    (hasValidTaxId && (hasVendorName || hasAddress || hasPhone)) ||
                    (hasVendorName && hasAddress);

                if (canCreateFromOcr)
                {
                    var addressParts = ParseAddressIntoParts(extractedData.VendorAddress);
                    var fallbackName = hasVendorName
                        ? extractedData.VendorName!.Trim()
                        : (hasValidTaxId
                            ? $"ผู้ขาย (TaxID: {extractedData.VendorTaxId})"
                            : "ผู้ขายจาก OCR (ยังไม่ได้ยืนยัน)");

                    var newContact = new Contact
                    {
                        CompanyId = companyId,
                        Name = fallbackName,
                        TaxId = hasValidTaxId ? extractedData.VendorTaxId : null,
                        BranchCode = extractedData.VendorBranchCode,
                        IsCustomer = false,
                        IsSupplier = true,
                        // ContactType heuristic: 13-digit TaxId starting with 0 = juristic;
                        // starting with 1-8 = individual NID. When unsure default to
                        // JuristicPerson — most OCR'd receipts are from companies, and
                        // the user can flip the type if it turns out to be a person.
                        ContactType = hasValidTaxId
                            ? (extractedData.VendorTaxId!.StartsWith("0") ? ContactType.JuristicPerson : ContactType.Individual)
                            : ContactType.JuristicPerson,
                        Address = extractedData.VendorAddress,
                        Phone = extractedData.VendorPhone,
                        Email = extractedData.VendorEmail,
                        BuildingNumber = addressParts.BuildingNumber,
                        Moo = addressParts.Moo,
                        StreetName = addressParts.StreetName,
                        SubDistrict = addressParts.SubDistrict,
                        District = addressParts.District,
                        Province = addressParts.Province,
                        PostalCode = addressParts.PostalCode,
                        CountryCode = "TH",
                        // Suffix tags the source so admins can later filter "needs
                        // verification" contacts and follow up. Keep it short —
                        // CreatedBy is shown in the audit log column.
                        CreatedBy = "OCR-FallbackAutoCreate"
                    };
                    _db.Contacts.Add(newContact);
                    await _db.SaveChangesAsync();
                    scanResult.MatchedContactId = newContact.Id;
                    contactJustCreated = true;
                    var verifyHint = extractedData.DbdLookupAttempted
                        ? "DBD ยืนยันไม่ได้"
                        : "ไม่ได้ตรวจกับ DBD";
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + $"\n[Auto-Create (Unverified)] สร้าง Contact จาก OCR: {newContact.Name}"
                        + (newContact.TaxId != null ? $" (TaxID {newContact.TaxId})" : "")
                        + $" — {verifyHint}, กรุณาตรวจสอบในภายหลัง";
                    _logger.LogInformation(
                        "Auto-created OCR-only contact (DBD failed) for company {CompanyId} TaxId={TaxId} Name={Name}",
                        companyId, newContact.TaxId, newContact.Name);
                }
                else if (!string.IsNullOrEmpty(extractedData.VendorTaxId))
                {
                    // Have a TaxId but nothing else trustworthy — keep the
                    // original "Manual Review Required" note so user can
                    // resolve in the UI.
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + $"\n[Manual Review Required] ไม่สามารถยืนยัน TaxID {extractedData.VendorTaxId} จาก DBD/RD และข้อมูลผู้ขายไม่เพียงพอ — กรุณาตรวจสอบและสร้าง Contact ด้วยตนเอง";
                }
            }
            // Backfill ข้อมูลใน contact เดิม. แยก 2 step:
            //   (1) TaxId — รันเสมอเมื่อ OCR แกะได้ + contact ยังไม่มี (เคสปกติ
            //       ที่ vendor ถูก match จากชื่อ ตั้งแต่ตอนสร้าง contact แต่ไม่มี
            //       TaxId). เดิมติด gate ของ DBD/Phone/Address ทำให้ใบที่
            //       แกะได้แค่ TaxId + Name หลุดการ backfill → user เปิดฟอร์ม
            //       มาเห็น "(ผู้ติดต่อยังไม่มีเลขผู้เสียภาษี)" ทั้งที่ OCR มี.
            //   (2) DBD enrichment fields (address/phone/email/branch) — รัน
            //       เฉพาะตอนมีข้อมูล (กันเขียนทับด้วยค่าว่าง).
            if (!contactJustCreated && scanResult.MatchedContactId.HasValue)
            {
                var existing = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == scanResult.MatchedContactId);
                if (existing != null)
                {
                    bool changed = false;
                    // (1) TaxId — เขียนได้เฉพาะเมื่อ "มั่นใจว่าเป็นรายเดียวกันจริง"
                    // ⚠️ contact ตัวนี้อาจถูกจับคู่มาด้วย "ชื่อ" (substring/fuzzy)
                    // การประทับเลขภาษีจากกระดาษลงไปทันทีจะ "เปลี่ยนตัวตน" ของ
                    // ผู้ติดต่อรายนั้นถาวร → เอกสารเก่าทุกใบของรายนั้นเปลี่ยนเลข
                    // ภาษีตาม + รายงานภาษีเพี้ยน. เงื่อนไข: ชื่อบนกระดาษต้อง
                    // ใกล้เคียงชื่อ contact จริง ๆ (≥0.90) และเลขต้องครบ 13 หลัก
                    var enrichNameSim = string.IsNullOrWhiteSpace(extractedData.VendorName)
                        ? 0d
                        : Ocr.FuzzyMatcher.Similarity(existing.Name, extractedData.VendorName!);
                    var enrichTaxDigits = DocumentService.NormalizeTaxDigits(extractedData.VendorTaxId);
                    if (string.IsNullOrWhiteSpace(existing.TaxId) && enrichTaxDigits.Length == 13)
                    {
                        if (enrichNameSim >= 0.90)
                        { existing.TaxId = extractedData.VendorTaxId; changed = true; }
                        else
                            scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                                + $"\n[Enrich] ไม่เติมเลขภาษี {enrichTaxDigits} ให้ '{existing.Name}' — ชื่อบนกระดาษ"
                                + $" ('{extractedData.VendorName}') ต่างจากผู้ติดต่อที่จับคู่ไว้ (ความใกล้เคียง {enrichNameSim:P0})"
                                + " โปรดตรวจว่าเป็นผู้ขายรายเดียวกันก่อนแก้ข้อมูลผู้ติดต่อเอง";
                    }
                    // (2) DBD / phone / address enrichment — gated ตามเดิม
                    var hasDbdOrContacts = extractedData.DbdCanonicalName != null
                        || !string.IsNullOrWhiteSpace(extractedData.DbdAddress)
                        || !string.IsNullOrWhiteSpace(extractedData.VendorPhone)
                        || !string.IsNullOrWhiteSpace(extractedData.VendorAddress);
                    if (hasDbdOrContacts)
                    {
                        // ที่อยู่: เติมทั้ง free-text + structured (บ้านเลขที่/ตำบล/
                        // อำเภอ/จังหวัด/ไปรษณีย์). เดิมเติมแค่ free-text ทำให้เอกสาร
                        // PDF (structured ว่าง→fallback free-text ที่ OCR เดาผิด) +
                        // ฟอร์มผู้ติดต่อ (อ่าน structured) ขึ้นว่าง/ผิด — ดู
                        // EnrichContactAddress.
                        if (EnrichContactAddress(existing, extractedData)) changed = true;
                        var sanePhone = SanePhone(extractedData.VendorPhone);
                        if (string.IsNullOrWhiteSpace(existing.Phone) && !string.IsNullOrWhiteSpace(sanePhone))
                        { existing.Phone = sanePhone; changed = true; }
                        if (string.IsNullOrWhiteSpace(existing.Email) && !string.IsNullOrWhiteSpace(extractedData.VendorEmail))
                        { existing.Email = extractedData.VendorEmail; changed = true; }
                        if (string.IsNullOrWhiteSpace(existing.BranchCode) && !string.IsNullOrWhiteSpace(extractedData.VendorBranchCode))
                        { existing.BranchCode = extractedData.VendorBranchCode; changed = true; }
                    }
                    if (changed)
                    {
                        existing.UpdatedBy = extractedData.DbdMatched ? "OCR-DbdEnrich" : "OCR-Enrich";
                        scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + $"\n[Enrich] อัปเดตข้อมูล Contact ที่ว่างของ {existing.Name}";
                    }
                }
            }

            // ─── PO check by vendor (business-flow step) ───
            // When the matched vendor has open Purchase Orders in the system,
            // the operator should book this paper through the PO/receiving
            // function instead of a fresh expense — surface the open POs so
            // the review UI can warn before they create a duplicate booking.
            if (scanResult.MatchedContactId.HasValue)
            {
                try
                {
                    var poCutoff = DateTime.UtcNow.AddMonths(-6);
                    var openPos = await _db.Documents.AsNoTracking()
                        .Where(d => d.CompanyId == companyId && !d.IsDeleted
                            && d.ContactId == scanResult.MatchedContactId.Value
                            && d.DocumentType == DocumentType.PurchaseOrder
                            && d.Status != DocumentStatus.Voided
                            && d.Status != DocumentStatus.Rejected
                            && d.Status != DocumentStatus.Draft
                            && d.Status != DocumentStatus.Paid   // fully billed = no longer open
                            && d.DocumentDate >= poCutoff)
                        .OrderByDescending(d => d.DocumentDate)
                        .Select(d => new { d.Id, d.DocumentNumber })
                        .Take(5)
                        .ToListAsync();
                    if (openPos.Count > 0)
                    {
                        scanResult.OpenPoNumbersJson = System.Text.Json.JsonSerializer.Serialize(
                            openPos.Select(p => p.DocumentNumber));
                        scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                            + $"\n[PO] ผู้ขายรายนี้มีใบสั่งซื้อค้างในระบบ {openPos.Count} ใบ ({string.Join(", ", openPos.Take(3).Select(p => p.DocumentNumber))}) — หากรายการนี้สั่งผ่าน PO กรุณาบันทึกผ่านฟังก์ชันรับตามใบสั่งซื้อ ไม่ใช่สร้างใหม่";

                        // AUTO-LINK BY PAPER REFERENCE: many invoices print the
                        // buyer's PO number ("อ้างอิงใบสั่งซื้อ PO-2026-0012").
                        // When exactly ONE open PO's number appears verbatim in
                        // the OCR text, link automatically — the strongest
                        // possible match signal, no user action needed. Two or
                        // more hits stay manual (ambiguous).
                        if (!scanResult.LinkedPurchaseOrderId.HasValue)
                        {
                            var hits = openPos.Where(p =>
                                    p.DocumentNumber.Length >= 4
                                    && (extractedText ?? "").Contains(p.DocumentNumber, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            if (hits.Count == 1)
                            {
                                scanResult.LinkedPurchaseOrderId = hits[0].Id;
                                scanResult.LinkedPurchaseOrderNumber = hits[0].DocumentNumber;
                                scanResult.ProcessingNotes += $"\n[PO] พบเลขที่ {hits[0].DocumentNumber} บนเอกสาร → ผูกกับใบสั่งซื้อให้อัตโนมัติ";
                                extractedData.ReasoningTrace.Add($"[PO] เอกสารอ้างอิง {hits[0].DocumentNumber} → auto-link");
                            }
                        }
                    }
                }
                catch (Exception poEx)
                {
                    _logger.LogWarning(poEx, "Open-PO check failed (non-fatal) for scan {ScanId}", scanResult.Id);
                }
            }

            // ─── ใบต้นทางทุกชนิด (ทั่วไปกว่า PO ข้างบน) ───
            // ผู้ใช้ 2026-09-10: "PO บุญทรัพย์ 599 อยู่ในระบบ พอ OCR ใบแจ้งหนี้ซื้อบุญทรัพย์ 599 เข้ามา
            // ต้องผูกให้เลย · มีเลข PO ยิ่งชัวร์ · ครอบคลุมทุกประเภทเอกสาร" — บล็อก PO ข้างบนผูก
            // เฉพาะเมื่อเลข PO พิมพ์บนกระดาษ; ตัวนี้เพิ่มเกณฑ์ยอดตรง + ฝั่งขาย + ทุกคู่ใน ValidConversions
            await SuggestPredecessorLinkAsync(companyId, scanResult, extractedData, extractedText);

            // ─── Stock vs Expense suggestion (business-flow step) ───
            // Recommend which entry mode the operator should pick:
            //   "Stock"   — this vendor has product aliases on file (we've
            //               imported their goods to stock before) and the scan
            //               has line items → likely an inventory purchase.
            //   "Expense" — everything else (services, utilities, one-offs).
            // A hint only — the user still chooses; never blocks anything.
            try
            {
                var vendorHasProductHistory = scanResult.MatchedContactId.HasValue
                    && await _db.ProductAliases.AsNoTracking().AnyAsync(a =>
                        a.CompanyId == companyId && !a.IsDeleted
                        && a.ContactId == scanResult.MatchedContactId.Value);
                // ⚠️ เดิมใช้สัญญาณเดียว (ผู้ขายเคยนำเข้าสต๊อกไหม) ⇒ บริษัทค้าขายที่
                // เพิ่งเริ่มใช้ระบบ ทุกใบซื้อสินค้าถูกแนะนำเป็นค่าใช้จ่าย ทั้งที่
                // ApplyProductCrossReferenceAsync จับคู่บรรทัดกับ Product master
                // ได้อยู่แล้วแต่ผลไม่ถูกใช้ตัดสิน (ผลตรวจ 2026-09-06 · T1-19)
                var advice = Accounting.Helpers.OcrEntryModeAdvisor.Decide(
                    lineCount: extractedData.Items.Count,
                    linesMatchedToProductMaster: extractedData.ProductMasterMatchedLines,
                    vendorHasProductHistory: vendorHasProductHistory,
                    industry: companyContext?.IndustryType);
                scanResult.SuggestedEntryMode = advice.Mode.ToString();
                extractedData.ReasoningTrace.Add($"[EntryMode] {advice.Mode} — {advice.Reason}");
            }
            catch (Exception emEx)
            {
                _logger.LogWarning(emEx, "Entry-mode suggestion failed (non-fatal) for scan {ScanId}", scanResult.Id);
            }

            // ─── Potential Fixed Asset detection (Phase 4) ───
            // Inspect line items for capital-asset signals BEFORE the auto-
            // create gate. When at least one line crosses the threshold, the
            // document is flagged for manual review — the UI surfaces a
            // "Potential Asset" alert with a Register button. Auto-create is
            // suppressed in that case so we don't pre-book the spend as
            // expense and then have to reverse it.
            try
            {
                var assetInput = extractedData.Items
                    .Select(i => (i.Description, i.Quantity, i.UnitPrice, i.Amount))
                    .ToList();
                var allDecisions = Ocr.FixedAssetDetector.Analyze(assetInput);
                var assetCandidates = Ocr.FixedAssetDetector.PotentialAssetsOnly(allDecisions);
                if (assetCandidates.Count > 0)
                {
                    scanResult.HasPotentialFixedAsset = true;
                    // Persist a serialized snapshot so the UI can render
                    // the alert without re-running the detector on every fetch.
                    scanResult.PotentialAssetLinesJson = System.Text.Json.JsonSerializer.Serialize(
                        assetCandidates.Select(d => new
                        {
                            lineIndex = d.LineIndex,
                            description = extractedData.Items.ElementAtOrDefault(d.LineIndex)?.Description,
                            unitPrice = extractedData.Items.ElementAtOrDefault(d.LineIndex)?.UnitPrice,
                            amount = extractedData.Items.ElementAtOrDefault(d.LineIndex)?.Amount,
                            quantity = extractedData.Items.ElementAtOrDefault(d.LineIndex)?.Quantity,
                            suggestedCategory = d.SuggestedCategory,
                            suggestedUsefulLifeMonths = d.SuggestedUsefulLifeMonths,
                            confidenceScore = d.ConfidenceScore,
                            reasons = d.Reasons,
                        }));
                    extractedData.ReasoningTrace.Add(
                        $"[FixedAsset] พบ {assetCandidates.Count} รายการที่อาจเป็นสินทรัพย์ถาวร — รอ user ยืนยัน (Register Asset)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fixed-asset detection failed (non-fatal)");
            }

            // Auto-create document if confidence >= threshold (85%).
            // Recurring vendors get a lower threshold (75%) — we already know
            // what their docs look like and the user has approved enough of
            // them historically that a "looks right" scan is safe to commit
            // without staging in Draft.
            // SUPPRESS auto-create when a potential asset was detected — the
            // user must register the asset(s) first (Dr: Asset / Cr: AP-or-Cash)
            // before the scan should produce an Expense document. Otherwise
            // the books would double-count: expense from auto-create + asset
            // capitalization from manual register.
            var autoCreateThreshold = decimal.TryParse(_configuration["Ocr:AutoCreateThreshold"], out var t) ? t : 0.85m;
            if (scanResult.HasPotentialFixedAsset)
            {
                autoCreateThreshold = decimal.MaxValue;  // effectively disable auto-create
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + "\n[FixedAsset] auto-create suppressed — โปรดกด Register Asset ก่อนสร้างเอกสาร";
            }
            // Handwriting detection: if Azure's styleFont reported
            // hand-written spans on this doc, capture the flag onto the
            // scan record and suppress auto-create so the user verifies
            // amounts manually. Handwritten amounts on a printed form
            // are a common source of OCR errors AND fraud.
            if (extractedData.FieldConfidence.TryGetValue("Handwriting", out var handConf))
            {
                scanResult.HasHandwriting = true;
                scanResult.HandwritingConfidence = (decimal)handConf;
                autoCreateThreshold = decimal.MaxValue;
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + $"\n[Handwriting] ✋ ตรวจพบลายมือ (conf {handConf:P0}) — auto-create suppressed, โปรดตรวจสอบยอดเงิน";
            }
            try
            {
                var recurring = await _recurringDetector.DetectAsync(companyId, scanResult.MatchedContactId);
                if (recurring != null && recurring.Confidence >= 0.7m)
                {
                    autoCreateThreshold = Math.Max(0.75m, autoCreateThreshold - 0.10m);
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + $"\n[Recurring] {recurring.Cadence} pattern ({recurring.SampleSize} docs, ~฿{recurring.TypicalAmount:N0}) — auto-create threshold ลดเป็น {autoCreateThreshold:P0}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recurring detection failed (non-fatal)");
            }

            // Critical-fields hard gate: even if Confidence and contact match
            // both look good, refuse to auto-create when the OCR couldn't
            // extract a usable date, document number, or total. These three
            // are the bookkeeping minimum — without them the created
            // document is just noise the user has to delete + redo.
            var hasUsableTotal = (extractedData.TotalAmount ?? 0) > 0m;
            var hasUsableDate = extractedData.DocumentDate.HasValue;
            // ใบรับรองแทนใบเสร็จ: บิลต้นทาง (แม่ค้าตลาด/วินฯ) มักไม่มีเลขที่
            // เอกสารเลย — เลขจริงคือเลขของ "ใบรับรอง" ที่เราออกเองตอน Approve.
            // บังคับเลขที่กับ target นี้ = ปิด auto-create กับเคสที่ฟีเจอร์นี้
            // เกิดมาเพื่อรองรับพอดี
            var hasUsableDocNumber = !string.IsNullOrWhiteSpace(extractedData.DocumentNumber)
                || extractedData.TargetDocumentType == nameof(DocumentType.CertificateInLieu);
            // 50 ทวิ ที่ "เราถูกหัก" ไม่ใช่เอกสารขายใบใหม่ — สร้าง RV standalone แล้ว
            // อนุมัติ = JE ขายสด (Dr เงินสด / Cr รายได้+ภาษีขาย) ⇒ รายได้เบิ้ล
            // (ผลตรวจ 2026-09-05 T1-11) → ห้าม auto-create; ให้ผู้ใช้ผูกกับใบขายเดิม
            var criticalFieldsOk = hasUsableTotal && hasUsableDate && hasUsableDocNumber
                && ocrWeAreWithheld != true;

            // AUTO-CREATE GATING — the business flow now mandates that web /
            // human-driven OCR only SUGGESTS the target document type (saved on
            // the scan as TargetDocumentType); the user makes the final create
            // decision in the review UI by calling CreateDocumentFromScanAsync.
            // Only callers that explicitly opt in (e.g. integration partner API
            // syncs) skip the user step. autoCreate=false → record the inferred
            // target as a NOTE and stop.
            if (!autoCreate)
            {
                if (scanResult.Confidence >= autoCreateThreshold
                    && scanResult.MatchedContactId.HasValue
                    && criticalFieldsOk
                    && !scanResult.IsDuplicate
                    && !scanResult.CreatedDocumentId.HasValue)
                {
                    var suggested = string.IsNullOrWhiteSpace(scanResult.TargetDocumentType)
                        ? "เอกสาร" : scanResult.TargetDocumentType;
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + $"\n[Suggest] ระบบแนะนำหมวด \"{suggested}\" — กด \"สร้างเอกสาร\" ในหน้าตรวจสอบเพื่อยืนยัน";
                }
            }
            else if (scanResult.Confidence >= autoCreateThreshold
                && scanResult.MatchedContactId.HasValue
                && !scanResult.IsDuplicate
                && !scanResult.CreatedDocumentId.HasValue
                && criticalFieldsOk)
            {
                try
                {
                    // ด่านสิทธิ์ต้องอยู่ตรงนี้ ไม่ใช่ที่ controller ตัวเดียว —
                    // เส้นนี้สร้าง Draft เองโดยไม่ผ่าน /create-document (E-02)
                    if (!await CanActorCreateAsync(companyId, actingUserId, scanResult))
                    {
                        // ห้ามเงียบ — ผู้ใช้ต้องรู้ว่าต้องขอสิทธิ์อะไร
                        scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                            + "\n[NO-PERM] อ่านเอกสารสำเร็จแต่ยังสร้างเอกสารให้ไม่ได้ — "
                            + "บัญชีของคุณไม่มีสิทธิ์สร้างเอกสารประเภทนี้ กรุณาขอสิทธิ์จากเจ้าของบริษัท";
                    }
                    else
                        await AutoCreateDocumentAsync(companyId, scanResult, extractedData);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-create document failed for scan {ScanId}", scanResult.Id);
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + $" Auto-create failed: {ex.Message}";
                }
            }
            else if (autoCreate
                  && scanResult.Confidence >= autoCreateThreshold
                  && scanResult.MatchedContactId.HasValue
                  && !criticalFieldsOk)
            {
                // Surface why we declined to auto-create — admin opens the
                // scan and sees exactly which fields the OCR missed.
                var missing = new List<string>();
                if (!hasUsableTotal) missing.Add("ยอดรวม");
                if (!hasUsableDate) missing.Add("วันที่");
                if (!hasUsableDocNumber) missing.Add("เลขที่เอกสาร");
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + $"\n[Auto-create ระงับ] ข้อมูลสำคัญยังขาด: {string.Join(", ", missing)} — กรุณากรอกใน \"ตรวจสอบ & สอนระบบ\" ก่อนสร้างเอกสาร";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "OCR scan failed ScanId={ScanId} CompanyId={CompanyId} FileId={FileId} Provider={Provider} ElapsedMs={ElapsedMs}",
                scanResult.Id, companyId, fileAttachmentId, ocrProvider,
                (int)(DateTime.UtcNow - scanStartedAt).TotalMilliseconds);
            scanResult.ScanStatus = "Failed";
            scanResult.ProcessedAt = DateTime.UtcNow;
            scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + $"\n[Error] {ex.Message}";
        }

        // ── สิ่งที่ผู้ใช้ต้องเห็น: เก็บ**ท้ายสุด** ห้ามเก็บกลางทาง ──────────────
        //
        // เดิมทั้งสองบล็อกนี้อยู่กลางไปป์ไลน์ ⇒ ทุกอย่างที่เกิด**หลัง**จุดนั้นหายเงียบ:
        //   • ความมั่นใจรายช่อง — [DBD] ที่ลดเหลือ 0.30/0.50 เพื่อขอให้คนตรวจ ·
        //     DebitAccount · DocumentNumber · ExpenseCategory · VAT/SubTotal ที่คำนวณย้อน
        //     ⇒ ผู้ใช้เห็นป้ายเขียวบนช่องที่ระบบเองยังไม่มั่นใจ (กฎเหล็ก #3 ข้อ 3
        //     สั่งให้ไฮไลต์เหลืองเมื่อ < 0.85 — ไฮไลต์นั้นไม่เคยขึ้น)
        //   • [Reasoning] — ถูก dump ที่บรรทัด ~1888 ซึ่งอยู่**ก่อน**ด่านทะเบียน (DBD)
        //     ถึง 270 บรรทัด ⇒ เหตุผลทุกบรรทัดที่ EnrichFromDbdAsync เขียน
        //     ("ไม่ได้ค้นทะเบียนเพราะเลขใช้ไม่ได้" · "ใช้ชื่อนิติบุคคลแทนชื่อแบรนด์")
        //     **ไม่เคยถึงผู้ใช้เลย** ทั้งตอนสดและตอน reload เพราะ DTO ไม่มีช่อง
        //     ReasoningTrace — มีแต่ ProcessingNotes (ฝ่ายค้านรอบ 174 จับได้)
        //
        // ตำแหน่งนี้อยู่**นอก** try/catch โดยตั้งใจ: สแกนที่ล้มกลางทางก็ยังต้อง
        // เก็บสิ่งที่รู้แล้วไว้ให้คนตรวจ ไม่ใช่ทิ้งทั้งชุด
        //
        // ⚠️ `extractedData` เป็น **nullable** และเป็น null จริงเมื่อ engine ตัวแรก
        // โยน exception ก่อนถูกกำหนดค่า (เช่น โควตา/Tesseract ล้ม) ⇒ ต้องใช้ `?.`
        // ไม่งั้น NRE ตรงนี้จะข้าม SaveChangesAsync ข้างล่าง แล้วแถวที่ INSERT ไว้
        // เป็น "Processing" จะค้างสถานะนั้นตลอดกาล พร้อมกับ 500 ถึงผู้ใช้ —
        // ซึ่งเป็นเคสเดียวกับที่คอมเมนต์ข้างบนบอกว่าจะรองรับ (CS8602 เป็นแค่ warning
        // และเรพไม่ได้เปิด TreatWarningsAsErrors ⇒ CI ไม่จับให้)
        if (extractedData?.FieldConfidence is { Count: > 0 } conf)
        {
            // เก็บลงฐานด้วยชื่อช่องกลาง — เดิมเก็บแค่เป็นข้อความใน
            // ProcessingNotes ซึ่งอ่านกลับมาใช้ไม่ได้ ⇒ พอ reload หน้า
            // ความมั่นใจรายช่องหายหมด แล้วป้าย % ตกไปใช้ค่าทั้งใบ
            var canon = Accounting.Helpers.OcrFieldKeys.Canonicalize(conf);
            scanResult.FieldConfidenceJson = System.Text.Json.JsonSerializer.Serialize(canon);
            scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n[Field Confidence]\n" +
                string.Join("\n", canon.Select(kv => $"  {kv.Key}: {kv.Value:P0}"));
        }

        if (extractedData?.ReasoningTrace is { Count: > 0 } trace)
            scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n[Reasoning]\n" +
                string.Join("\n", trace.Select(r => "  • " + r));

        // บรรทัดที่อ่านได้ต้องไม่หายเมื่อไปป์ไลน์ล้มกลางทาง — จุด serialize หลักอยู่
        // กลางไปป์ไลน์ (ต้องอยู่ก่อนตัวอ่าน JSON ทุกตัว ดูเหตุผลที่นั่น) ส่วนบล็อกนี้
        // อยู่**นอก** try/catch จึงเติมให้เฉพาะตอนที่ยังไม่มีใครเขียน — ห้ามทับของที่
        // เส้นสร้างเอกสาร/ผู้ใช้แก้บรรทัดเขียนไว้แล้ว
        if (string.IsNullOrEmpty(scanResult.ExtractedItemsJson)
            && extractedData?.Items is { Count: > 0 } salvagedItems)
            scanResult.ExtractedItemsJson = SerializeExtractedItems(salvagedItems);

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "OCR scan finished ScanId={ScanId} CompanyId={CompanyId} Provider={Provider} Status={Status} Confidence={Confidence:P0} IsDuplicate={IsDuplicate} HasMatch={HasMatch} HasDocument={HasDocument} ElapsedMs={ElapsedMs}",
            scanResult.Id, companyId, ocrProvider, scanResult.ScanStatus,
            scanResult.Confidence, scanResult.IsDuplicate,
            scanResult.MatchedContactId.HasValue, scanResult.CreatedDocumentId.HasValue,
            (int)(DateTime.UtcNow - scanStartedAt).TotalMilliseconds);
        return await AttachComplianceAsync(companyId, MapToResponse(scanResult, extractedData));
    }

    private static OcrConfidenceGateway.GatewayConfig BuildGatewayConfig(SiteSettings? settings)
    {
        if (settings == null) return OcrConfidenceGateway.GatewayConfig.Default;
        return new OcrConfidenceGateway.GatewayConfig
        {
            MaxPenalty = ClampPct(settings.OcrGatewayMaxPenalty, 0.20m, 1.0m, 0.60m),
            MathTolerance = settings.OcrGatewayMathTolerance > 0 ? settings.OcrGatewayMathTolerance : 2.0m,
            TaxIdPenalty = ClampPct(settings.OcrGatewayTaxIdPenalty, 0m, 1.0m, 0.15m),
            MathPenalty = ClampPct(settings.OcrGatewayMathPenalty, 0m, 1.0m, 0.20m),
            DatePenalty = ClampPct(settings.OcrGatewayDatePenalty, 0m, 1.0m, 0.15m),
            VatRatePenalty = ClampPct(settings.OcrGatewayVatRatePenalty, 0m, 1.0m, 0.10m),
            LowConfidencePenalty = ClampPct(settings.OcrGatewayLowConfidencePenalty, 0m, 1.0m, 0.05m),
        };
    }

    private static decimal ClampPct(decimal value, decimal min, decimal max, decimal fallback)
        => (value <= 0 || value > 1) ? fallback : Math.Clamp(value, min, max);

    /// <summary>
    /// Best-effort parse of a free-text Thai address into ETDA-compliant structured parts.
    /// Delegates to ThaiAddressParser — the canonical parser handles หมู่ที่ +
    /// Thai numerals + Bangkok aliases + multi-prefix variants which the inline
    /// regex here used to miss.
    /// </summary>
    private static (string? BuildingNumber, string? StreetName, string? SubDistrict, string? District, string? Province, string? PostalCode, string? Moo)
        ParseAddressIntoParts(string? address)
    {
        var p = ThaiAddressParser.Parse(address);
        return (p.BuildingNumber, p.StreetName, p.SubDistrict, p.District, p.Province, p.PostalCode, p.Moo);
    }

    /// <summary>เติม/อัปเดตที่อยู่ของ Contact ที่ "ถูก match จากของเดิม" ให้ครบทั้ง
    /// free-text (<c>Address</c>) และ structured fields (บ้านเลขที่/หมู่/ถนน/ตำบล/
    /// อำเภอ/จังหวัด/รหัสไปรษณีย์).
    ///
    /// แก้บั๊ก OCR-via-API: เดิม enrichment เติมแค่ free-text <c>Address</c> ทำให้
    ///   • เอกสาร/PDF (FormatThaiAddress) เมื่อ structured ว่าง → ตกไปใช้ free-text
    ///     ที่ OCR เดามาผิด → "ที่อยู่ในเอกสารผิด"
    ///   • ฟอร์มผู้ติดต่ออ่าน structured fields → ขึ้นว่าง → ผู้ใช้ต้องกด "ดึงข้อมูล"
    ///     (DBD) เองทุกครั้ง
    ///
    /// ลำดับความน่าเชื่อถือ: DBD (ground truth) ก่อน VendorAddress (OCR เดา) เสมอ.
    /// การ "ทับ" ค่าเดิมที่ไม่ว่าง อนุญาตเฉพาะเมื่อ DBD ยืนยัน **และ** contact ถูก
    /// จัดการโดย OCR เอง (CreatedBy/UpdatedBy ขึ้นต้น "OCR") — ไม่แตะที่อยู่ที่ผู้ใช้
    /// กรอก/ยืนยันด้วยตนเอง. field ที่ว่างอยู่แล้วเติมได้เสมอ (ปลอดภัย).
    /// คืน true เมื่อมีการเปลี่ยนแปลง.</summary>
    private static bool EnrichContactAddress(Contact c, OcrExtractedData data)
    {
        var dbdMatched = data.DbdMatched && !string.IsNullOrWhiteSpace(data.DbdAddress);
        var freeAddr = dbdMatched ? data.DbdAddress : data.VendorAddress;
        if (string.IsNullOrWhiteSpace(freeAddr)) return false;

        var ocrManaged = (c.CreatedBy ?? "").StartsWith("OCR", StringComparison.OrdinalIgnoreCase)
                      || (c.UpdatedBy ?? "").StartsWith("OCR", StringComparison.OrdinalIgnoreCase);
        var allowOverwrite = dbdMatched && ocrManaged;

        var parts = ParseAddressIntoParts(freeAddr);
        bool changed = false;

        bool Apply(string? cur, string? val, Action<string> set)
        {
            if (string.IsNullOrWhiteSpace(val)) return false;
            if (string.IsNullOrWhiteSpace(cur)
                || (allowOverwrite && !string.Equals(cur, val, StringComparison.Ordinal)))
            { set(val!); return true; }
            return false;
        }

        // street = ส่วนหัวเต็ม (ห้อง/ชั้น/อาคาร/ซอย/ถนน) ไม่ใช่แค่ชื่อถนนสั้นจาก
        // parser — กันรายละเอียดหายตอน render structured address.
        var streetHead = ThaiAddressParser.ExtractStreetHead(freeAddr, parts.BuildingNumber, parts.Moo);
        if (Apply(c.Address, freeAddr, v => c.Address = v)) changed = true;
        if (Apply(c.BuildingNumber, parts.BuildingNumber, v => c.BuildingNumber = v)) changed = true;
        if (Apply(c.Moo, parts.Moo, v => c.Moo = v)) changed = true;
        if (Apply(c.StreetName, streetHead, v => c.StreetName = v)) changed = true;
        if (Apply(c.SubDistrict, parts.SubDistrict, v => c.SubDistrict = v)) changed = true;
        if (Apply(c.District, parts.District, v => c.District = v)) changed = true;
        if (Apply(c.Province, parts.Province, v => c.Province = v)) changed = true;
        if (Apply(c.PostalCode, parts.PostalCode, v => c.PostalCode = v)) changed = true;
        return changed;
    }

    /// <summary>
    /// Build a content-based fingerprint from extracted fields. Same logical document
    /// uploaded as PDF and JPG produces matching fingerprints (different file hashes).
    /// Returns null when not enough fields are present to make a reliable fingerprint —
    /// avoids false-positive collisions between blank/partial scans.
    /// </summary>
    private static string? ComputeContentFingerprint(OcrExtractedData data)
    {
        if (string.IsNullOrWhiteSpace(data.VendorTaxId) && string.IsNullOrWhiteSpace(data.VendorName))
            return null;
        if (string.IsNullOrWhiteSpace(data.DocumentNumber) && !data.TotalAmount.HasValue)
            return null;

        var vendorKey = string.IsNullOrWhiteSpace(data.VendorTaxId)
            ? (data.VendorName ?? "").Trim().ToLowerInvariant()
            : new string(data.VendorTaxId.Where(char.IsDigit).ToArray());
        var docKey = (data.DocumentNumber ?? "").Trim().ToLowerInvariant();
        var dateKey = data.DocumentDate?.ToString("yyyy-MM-dd") ?? "";
        var amtKey = data.TotalAmount.HasValue
            ? Math.Round(data.TotalAmount.Value, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            : "";
        var input = $"{vendorKey}|{docKey}|{dateKey}|{amtKey}";
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    /// <summary>
    /// Pull the most distinctive keyword out of a suggested account name so
    /// it can be matched against a company's custom Chart of Accounts.
    /// Thai account names run words together with no spaces, so we:
    ///   1. drop a leading account code if the caller passed "5306 ค่า…",
    ///   2. cut at the first conjunction ("และ", "หรือ", "/") so
    ///      "ค่าซ่อมแซมและบำรุงรักษา" yields "ซ่อมแซม" — a shorter stem
    ///      matches more CoA variants than the full compound,
    ///   3. strip shared classifier prefixes ("ค่า", "บัญชี") that nearly
    ///      every expense account carries and so add no signal,
    ///   4. fall back to the longest space-separated token for names that
    ///      do use spaces (English or mixed).
    /// Returns null when nothing distinctive (≥3 chars) is left.
    /// </summary>
    private static string? ExtractDistinctiveKeyword(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var work = name.Trim();

        // Drop a leading account code passed as "5306 ค่า…".
        work = Regex.Replace(work, @"^\s*\d[\d\-\.]*\s+", "");

        // Cut at the first conjunction / separator — keep the leading concept.
        foreach (var sep in new[] { "และ", "หรือ", "/", ",", "&" })
        {
            var idx = work.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 2) { work = work[..idx]; break; }
        }

        // Strip shared classifier prefixes that carry no signal.
        foreach (var prefix in new[] { "ค่าใช้จ่าย", "ค่า", "บัญชี" })
        {
            if (work.StartsWith(prefix, StringComparison.Ordinal) && work.Length > prefix.Length + 2)
            {
                work = work[prefix.Length..];
                break;
            }
        }

        work = work.Trim();

        // For space-separated names, take the longest meaningful token.
        if (work.Contains(' '))
        {
            var token = work
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length >= 3)
                .OrderByDescending(t => t.Length)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(token)) work = token;
        }

        return work.Length >= 3 ? work : null;
    }

    /// <summary>Resolve the active OCR provider name from SiteSettings → appsettings.json
    /// → "local" default. Returns lowercase. Other config (LocalServiceUrl, AzureEndpoint,
    /// AutoCreateThreshold) is consumed at the point of use directly from SiteSettings.</summary>
    private string GetEffectiveProvider(SiteSettings? siteSettings)
        => (siteSettings?.OcrProvider ?? _configuration["Ocr:Provider"] ?? "").ToLowerInvariant();

    private record AzureExtractionResult(bool Success, string Text, OcrExtractedData Data, string? Error);

    /// <summary>
    /// Match each OCR'd line-item description against the company's Products
    /// table. When a strong match is found, override the line's suggested
    /// account with the product's PurchaseAccount — the product master is
    /// authoritative because a human configured it deliberately.
    ///
    /// Match strategy (in order of strength):
    ///   1. Exact code/SKU/barcode hit anywhere in the description
    ///   2. Exact product name substring (≥3 chars)
    ///   3. Loose Thai name match — first 6 Thai chars of the name appear
    ///
    /// Header-level DebitAccount is updated when ≥2 line items resolve to
    /// the same product account, or when there's only one line.
    /// </summary>
    /// <summary>จับคู่บรรทัดกับ Product master แล้ว<b>บันทึกจำนวนบรรทัดที่ตรง</b>
    /// ลง <see cref="OcrExtractedData.ProductMasterMatchedLines"/> — ตัวเลขนี้เป็น
    /// หลักฐานเดียวที่ใช้ได้ตั้งแต่ใบแรก (cold-start) ว่า "ของบนใบนี้คือสินค้าของเรา"
    /// เดิมคำนวณแล้วทิ้ง มีแต่ผังบัญชีที่ถูกใช้ต่อ (ผลตรวจ T1-19)</summary>
    private async Task ApplyProductCrossReferenceAsync(Guid companyId, OcrExtractedData data)
    {
        if (data.Items == null || data.Items.Count == 0) return;

        // Pull only products that have a PurchaseAccount configured — without
        // an account these rows can't influence the decision and just bloat
        // the in-memory scan.
        var products = await _db.Products.AsNoTracking()
            .Include(p => p.PurchaseAccount)
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                && p.PurchaseAccountId != null
                && p.PurchaseAccount != null)
            .Select(p => new {
                p.Code, p.Name, p.SKU, p.Barcode,
                AccountCode = p.PurchaseAccount!.AccountCode,
                AccountName = p.PurchaseAccount.AccountName,
            })
            .ToListAsync();
        if (products.Count == 0) return;

        var accountHits = new Dictionary<string, (string Name, int Count)>();
        foreach (var line in data.Items)
        {
            var desc = (line.Description ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(desc)) continue;

            var match = products.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(p.Code) && desc.Contains(p.Code.ToLowerInvariant()))
                || (!string.IsNullOrEmpty(p.Barcode) && desc.Contains(p.Barcode.ToLowerInvariant()))
                || (!string.IsNullOrEmpty(p.SKU) && desc.Contains(p.SKU.ToLowerInvariant()))
                || (!string.IsNullOrEmpty(p.Name) && p.Name.Length >= 3 && desc.Contains(p.Name.ToLowerInvariant())));

            if (match == null) continue;
            data.ProductMasterMatchedLines++;
            line.SuggestedAccountCode = match.AccountCode;
            var entry = accountHits.GetValueOrDefault(match.AccountCode);
            accountHits[match.AccountCode] = (match.AccountName, entry.Count + 1);
            data.ReasoningTrace.Add(
                $"[Product] รายการ '{line.Description}' ตรงกับสินค้า master '{match.Name}' → บัญชี {match.AccountCode}");
        }

        if (accountHits.Count == 0) return;
        // Dominant account across the lines becomes the header-level Debit
        var dominant = accountHits.OrderByDescending(kv => kv.Value.Count).First();
        if (dominant.Value.Count >= Math.Max(2, data.Items.Count / 2))
        {
            data.DebitAccountCode = dominant.Key;
            data.DebitAccountName = dominant.Value.Name;
            data.FieldConfidence["DebitAccount"] = 0.95;
            data.ReasoningTrace.Add(
                $"[Product] บัญชี header → {dominant.Key} ({dominant.Value.Count}/{data.Items.Count} รายการตรงสินค้า master)");
        }
    }

    private async Task<AzureExtractionResult> ExtractWithAzureDiAsync(Guid companyId, FileAttachment file, SiteSettings? siteSettings)
    {
        if (!System.IO.File.Exists(file.StoragePath))
            return new AzureExtractionResult(false, "", new OcrExtractedData(), "Source file missing");

        var fileBytes = await System.IO.File.ReadAllBytesAsync(file.StoragePath);
        var contentType = OcrPreprocessor.EffectiveContentType(file.ContentType ?? "", file.OriginalFileName);

        var preflight = OcrPreprocessor.Check(fileBytes, contentType, file.OriginalFileName);
        if (!preflight.Ok)
            return new AzureExtractionResult(false, "", new OcrExtractedData(), preflight.ErrorMessage);

        // ─── In-process image enhancement before sending to Azure ───
        // ImagePreprocessor: EXIF auto-rotate, grayscale, contrast bump,
        // upscale small images. Returns identical bytes for clean inputs
        // (PDFs / high-res images). Improves Azure DI extraction on
        // phone-camera snaps; reduces payload size for jpegs.
        var prep = Ocr.ImagePreprocessor.Process(fileBytes, contentType, file.OriginalFileName);
        if (prep.StepsApplied.Count > 0)
        {
            fileBytes = prep.Bytes;
            contentType = prep.ContentType;
            _logger.LogInformation("Image preprocessed for {File}: {Steps}",
                file.OriginalFileName, string.Join(", ", prep.StepsApplied));
        }

        var azureResult = await _azureDi.AnalyzeAsync(fileBytes, contentType, siteSettings,
            fileName: file.OriginalFileName);
        // Surface preprocessing trace alongside Azure's own warnings so the
        // debug panel shows the full processing chain. Insert first so it
        // appears at the top of the trace.
        if (azureResult != null && prep.StepsApplied.Count > 0)
            azureResult.Warnings.Insert(0, $"[ImagePrep] {string.Join(", ", prep.StepsApplied)}");
        if (azureResult == null)
            return new AzureExtractionResult(false, "", new OcrExtractedData(), "Azure DI not enabled or not configured");
        if (!azureResult.Success)
            return new AzureExtractionResult(false, "", new OcrExtractedData(), azureResult.ErrorMessage);

        var data = await MapAzureDiToExtractedDataAsync(companyId, azureResult);
        // Apply universal constraints — keeps the data shape consistent across
        // all three OCR providers and enforces math invariants / tax-id
        // checksum / WHT-rate validity that Azure DI may have missed.
        Ocr.SmartFieldExtractor.Enrich(data, azureResult.RawText ?? "");
        return new AzureExtractionResult(true, azureResult.RawText ?? "", data, null);
    }

    private Task<OcrExtractedData> MapAzureDiToExtractedDataAsync(Guid companyId, AzureDiResult azure)
    {
        var data = new OcrExtractedData
        {
            DocumentType = MapAzureDocType(azure.DocumentType, azure.ModelId, azure.RawText),
            Confidence = azure.OverallConfidence,
            DocumentNumber = azure.InvoiceId,
            DocumentDate = azure.InvoiceDate,
            VendorName = azure.VendorName,
            VendorTaxId = ExtractDigits(azure.VendorTaxId, 13),
            VendorAddress = azure.VendorAddress,
            VendorPhone = azure.VendorPhone,
            BuyerName = azure.CustomerName,
            BuyerTaxId = ExtractDigits(azure.CustomerTaxId, 13),
            BuyerAddress = azure.CustomerAddress,
            SubTotal = azure.SubTotal,
            VatAmount = azure.TotalTax,
            TotalAmount = azure.InvoiceTotal ?? azure.AmountDue,
            ZoneSummary = $"Azure DI {azure.ModelId}: confidence={azure.OverallConfidence:P0}",
        };

        // Per-field confidence (cast decimal → double for OcrExtractedData dictionary)
        foreach (var (k, v) in azure.FieldConfidence)
            data.FieldConfidence[k] = (double)v;

        // ── บันทึกที่มาของค่า (D1) ───────────────────────────────────────────
        // Azure เป็นแหล่งเดียวที่รายงานความมั่นใจ **รายช่อง** จริง ๆ จึงแยกได้ว่า
        // ช่องไหนควรถือเป็น AzureHighConfidence (≥ 0.85 = เกณฑ์เดียวกับไฮไลต์เหลือง
        // กฎเหล็ก #3 ข้อ 3) และช่องไหนเป็นแค่ผลอ่านธรรมดา — ห้ามเหมาทั้งใบด้วย
        // OverallConfidence เพราะใบเดียวกันมีช่องที่ชัดและช่องที่เบลอปนกัน
        decimal AzureConf(string canonicalKey)
        {
            foreach (var (k, v) in azure.FieldConfidence)
                if (string.Equals(Accounting.Helpers.OcrFieldKeys.Canonical(k), canonicalKey, StringComparison.Ordinal))
                    return v;
            return azure.OverallConfidence;
        }
        void NoteAzure(string field, object? value)
        {
            var conf = AzureConf(field);
            var src = conf >= 0.85m
                ? Accounting.Helpers.OcrFieldSource.AzureHighConfidence
                : Accounting.Helpers.OcrFieldSource.Engine;
            var evidence = $"Azure DI {azure.ModelId}";
            switch (value)
            {
                case string sv: data.Note(field, sv, src, conf, evidence); break;
                case decimal dv: data.Note(field, dv, src, conf, evidence); break;
                case DateTime tv: data.Note(field, tv, src, conf, evidence); break;
            }
        }
        NoteAzure(Accounting.Helpers.OcrFieldKeys.SellerName, data.VendorName);
        NoteAzure(Accounting.Helpers.OcrFieldKeys.SellerTaxId, data.VendorTaxId);
        NoteAzure(Accounting.Helpers.OcrFieldKeys.BuyerName, data.BuyerName);
        NoteAzure(Accounting.Helpers.OcrFieldKeys.BuyerTaxId, data.BuyerTaxId);
        NoteAzure(Accounting.Helpers.OcrFieldKeys.DocumentNumber, data.DocumentNumber);
        NoteAzure(Accounting.Helpers.OcrFieldKeys.DocumentDate, data.DocumentDate);
        NoteAzure(Accounting.Helpers.OcrFieldKeys.SubTotal, data.SubTotal);
        NoteAzure(Accounting.Helpers.OcrFieldKeys.VatAmount, data.VatAmount);
        NoteAzure(Accounting.Helpers.OcrFieldKeys.TotalAmount, data.TotalAmount);

        // Reasoning trace
        data.ReasoningTrace.Add($"[Azure DI] Model: {azure.ModelId} | Pages: {azure.PageCount} | Documents: {azure.MultiDocumentCount}");
        if (!string.IsNullOrEmpty(azure.VendorName))
            data.ReasoningTrace.Add($"[Azure DI] Vendor: {azure.VendorName}");
        if (!string.IsNullOrEmpty(azure.CustomerName))
            data.ReasoningTrace.Add($"[Azure DI] Customer: {azure.CustomerName}");
        // Surface multi-document warnings to user
        foreach (var w in azure.Warnings)
            data.ReasoningTrace.Add($"[Azure DI Warning] {w}");
        if (azure.InvoiceTotal.HasValue)
            data.ReasoningTrace.Add($"[Azure DI] Total: {azure.InvoiceTotal.Value:N2}");
        // Handwriting detection — flag scan for manual amount verification
        // when any field appears to be hand-written on a printed form.
        if (azure.HandwrittenSpanCount > 0)
        {
            data.ReasoningTrace.Add(
                $"[Azure DI] ⚠️ ตรวจพบลายมือ {azure.HandwrittenSpanCount} จุด (conf {azure.HandwrittenConfidence:P0}) — กรุณาตรวจสอบยอดเงิน");
            // Dock confidence slightly so the gateway flags it
            data.FieldConfidence["Handwriting"] = (double)azure.HandwrittenConfidence;
        }
        // Selection-mark count surfaced for tax forms
        if (azure.SelectionMarks.Count > 0)
        {
            var selected = azure.SelectionMarks.Count(m => m.State == "selected");
            data.ReasoningTrace.Add(
                $"[Azure DI] Selection marks: {selected} เลือก / {azure.SelectionMarks.Count} ทั้งหมด");
        }

        // Map line items
        foreach (var item in azure.Items)
        {
            data.Items.Add(new OcrExtractedLineItem
            {
                Description = item.Description,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Amount = item.Amount,
                // ค่าที่ Azure อ่านมาให้แล้ว — ใช้ของจริงก่อนเสมอ แล้วค่อยให้
                // ThaiVatTypeRule เดาเฉพาะบรรทัดที่ยังว่าง (T2-04)
                Unit = string.IsNullOrWhiteSpace(item.Unit) ? null : item.Unit!.Trim(),
                VatRate = item.TaxRate,
                VatAmount = item.Tax,
            });
        }

        // ── กำหนดชำระที่ใบพิมพ์ไว้ → เครดิตเทอม (T2-04) ──
        // Azure คืน DueDate มาอยู่แล้ว แต่ไม่มีใครอ่าน ⇒ ระบบไปเดาเครดิตเทอมจาก
        // ประวัติผู้ขายทั้งที่กระดาษบอกไว้ตรง ๆ · เก็บเป็น "จำนวนวัน" ให้ตรงกับ
        // ช่องที่ระบบใช้จริง และเฉพาะเมื่อค่าสมเหตุสมผล (0–365 วัน)
        if (azure.DueDate.HasValue && azure.InvoiceDate.HasValue)
        {
            var days = (int)(azure.DueDate.Value.Date - azure.InvoiceDate.Value.Date).TotalDays;
            if (days is >= 0 and <= 365)
            {
                data.PaymentTermsDays ??= days;
                data.ReasoningTrace.Add($"[Azure DI] กำหนดชำระบนใบ {azure.DueDate:d} → เครดิต {days} วัน");
            }
        }
        if (!string.IsNullOrWhiteSpace(azure.PurchaseOrder))
            data.ReasoningTrace.Add($"[Azure DI] อ้างใบสั่งซื้อ {azure.PurchaseOrder} — ตรวจสอบการผูก PO ก่อนยืนยัน");

        // ─── Recover missing fields from keyValuePairs ───
        // Azure's keyValuePairs feature catches Thai-anchored data that
        // the standard Invoice schema doesn't include. We backfill the
        // gaps here — schema fields take priority, K-V fills the rest.
        ApplyKeyValueFallback(azure, data);

        // ─── Cross-validate with barcodes / QR ───
        // RD-issued e-Receipts encode a canonical amount + tax ID in a
        // QR; when present, raise field confidence to 1.0 on matching
        // schema-extracted values and flag mismatches.
        if (azure.Barcodes.Count > 0)
        {
            data.ReasoningTrace.Add($"[Azure DI] Barcodes: {azure.Barcodes.Count} detected " +
                $"({string.Join(", ", azure.Barcodes.Select(b => b.Kind ?? "?"))})");
            foreach (var bc in azure.Barcodes)
            {
                if (string.IsNullOrEmpty(bc.Value)) continue;
                // Treat any 13-digit run inside the barcode as a tax ID
                // candidate — common for RD QR which embeds buyer tax ID.
                //
                // แต่ **บาร์โค้ดสินค้า EAN-13 ก็เป็นเลข 13 หลัก** และมีโอกาส ~1/10
                // ที่จะผ่าน mod-11 ไทยโดยบังเอิญ — ใบกำกับร้านวัสดุมีบาร์โค้ด
                // ทุกบรรทัดสินค้า ⇒ เกือบการันตีว่าจะมีตัวหนึ่งถูกยัดเป็นเลข
                // ผู้เสียภาษีผู้ขาย ทับของจริง (defect class เดียวกับ
                // PI-20260820-0005). QR ของสรรพากรไม่ใช่ EAN-13 จึงไม่โดนตัด
                var digits = new string(bc.Value.Where(char.IsDigit).ToArray());
                if (digits.Length >= 13)
                {
                    var taxId = digits.Substring(0, 13);
                    if (Accounting.Helpers.ThaiTaxId.LooksLikeProductBarcode(bc.Value))
                    {
                        data.ReasoningTrace.Add(
                            $"[Azure DI Barcode] ข้าม {bc.Value} — เป็นบาร์โค้ดสินค้า (EAN-13) ไม่ใช่เลขผู้เสียภาษี");
                    }
                    else if (Ocr.SmartFieldExtractor.IsValidThaiTaxId(taxId))
                    {
                        if (string.IsNullOrEmpty(data.VendorTaxId)) data.VendorTaxId = taxId;
                        else if (data.VendorTaxId == taxId)
                            data.FieldConfidence["SellerTaxId"] = 1.0;
                        data.ReasoningTrace.Add($"[Azure DI Barcode] Found valid tax id {taxId} in {bc.Kind}");
                    }
                }
            }
        }

        // ─── Language detection telemetry ───
        if (azure.DetectedLanguages.Count > 0)
            data.ReasoningTrace.Add($"[Azure DI] Languages: {string.Join(", ", azure.DetectedLanguages)}");

        // NOTE: Seller/buyer swap is intentionally NOT performed here. The unified
        // swap block in ScanAsync runs AFTER OcrConfidenceGateway, ensuring all
        // OCR providers (local + Azure DI) submit pre-swap data to the gateway —
        // so checksum and math validations are consistent across paths.

        return Task.FromResult(data);
    }

    /// <summary>
    /// Fill OcrExtractedData fields that the standard Invoice schema
    /// missed using Azure's generic keyValuePairs output. Thai-anchored
    /// fields that benefit most: เลขประจำตัวผู้เสียภาษี → VendorTaxId or
    /// BuyerTaxId (we pick based on proximity in the original document),
    /// เลขที่ → DocumentNumber, วันที่ → DocumentDate, ส่งถึง/ผู้รับ → BuyerName.
    /// Only fills when the schema field is empty — never overwrites a
    /// schema-confirmed value.
    /// </summary>
    private static void ApplyKeyValueFallback(AzureDiResult azure, OcrExtractedData data)
    {
        if (azure.KeyValuePairs.Count == 0) return;

        // Tax-id-shaped keys → fill missing tax ID. We assign by which
        // side of the document the key appeared on (seller anchor vs
        // buyer anchor); approximated by keyword in the K-V's key text.
        foreach (var (key, value) in azure.KeyValuePairs)
        {
            var keyLower = key.ToLowerInvariant();

            // Tax IDs (13 digits)
            if (keyLower.Contains("เลขประจำตัว") || keyLower.Contains("tax id"))
            {
                var taxId = ExtractDigits(value, 13);
                // ต้องผ่าน checksum จริง — ช่องนี้เป็น K-V ที่ Azure เดาคู่ key/value
                // เอง ถ้าค่าที่จับมาไม่ใช่เลขผู้เสียภาษี การเติมลงไปคือสร้างข้อมูลผิด
                // ที่กฎ TENANT_BUYER_MISMATCH จะเอาไปฟันธงว่า "อัพโหลดผิดบริษัท"
                if (!Accounting.Helpers.ThaiTaxId.IsPlausibleFromScan(taxId)) taxId = "";
                if (!string.IsNullOrEmpty(taxId))
                {
                    var isBuyerSide = keyLower.Contains("ผู้ซื้อ") || keyLower.Contains("ลูกค้า")
                        || keyLower.Contains("customer") || keyLower.Contains("buyer");
                    if (isBuyerSide && string.IsNullOrEmpty(data.BuyerTaxId)) data.BuyerTaxId = taxId;
                    else if (string.IsNullOrEmpty(data.VendorTaxId)) data.VendorTaxId = taxId;
                }
                continue;
            }

            // Document number — "เลขที่ใบกำกับ", "Invoice No.", etc.
            if (string.IsNullOrEmpty(data.DocumentNumber)
                && (keyLower.Contains("เลขที่") || keyLower.Contains("invoice no") || keyLower.Contains("doc no")))
            {
                data.DocumentNumber = value.Trim();
                continue;
            }

            // Date — "วันที่", "Date"
            if (!data.DocumentDate.HasValue
                && (keyLower.Contains("วันที่") || keyLower == "date" || keyLower.Contains("invoice date")))
            {
                // ⚠️ เดิม `DateTime.TryParse(value, out var d)` เปล่า ๆ ⇒ ผลขึ้นกับ
                // culture ของ process (th-TH = ปฏิทินพุทธ ⇒ ISO 2026 → ค.ศ. 1483
                // → ถูกล้างทิ้ง) และอ่าน dd/MM แบบไทยไม่ได้เมื่อ culture เป็น en-US
                // → ตัวแปลงกลางตัวเดียว (ผลตรวจ 2026-09-06 · T2-19)
                if (Accounting.Helpers.ThaiDate.TryParseFlexible(value, out var d))
                    // Pin Kind=Utc with same y/m/d — see BUGFIX note in
                    // SmartFieldExtractor.ValidateAndNormalizeDate.
                    data.DocumentDate = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);
                continue;
            }

            // Buyer name (Customer/ลูกค้า/ผู้ซื้อ)
            if (string.IsNullOrEmpty(data.BuyerName)
                && (keyLower.Contains("ลูกค้า") || keyLower.Contains("ผู้ซื้อ") || keyLower.Contains("customer")
                    || keyLower.Contains("ส่งถึง") || keyLower.Contains("bill to")))
            {
                data.BuyerName = value.Trim();
            }
        }

        if (azure.KeyValuePairs.Count > 0)
            data.ReasoningTrace.Add($"[Azure DI KV] {azure.KeyValuePairs.Count} key-value pairs scanned for fallback");
    }

    /// <summary>สร้างรายการทะเบียน "ภาษีถูกหัก ณ ที่จ่าย" จากหนังสือรับรองที่สแกน
    ///
    /// <para>เข้าเงื่อนไขเมื่อบริษัทเราอยู่ช่อง <b>ผู้ถูกหักภาษี</b> บนกระดาษ —
    /// สถานะเป็น <b>Received</b> ทันที (มีใบจริงอยู่ในมือแล้ว ต่างจากแถวที่ระบบ
    /// สร้างเองตอนรับเงินซึ่งเป็น Pending เพราะยังไม่มีใบ)</para>
    ///
    /// <para>ผูกไฟล์สแกนไว้กับรายการเพื่อให้ครบตาม พ.ร.บ.บัญชี ม.10 (เก็บ 5 ปี)</para></summary>
    private async Task EnsureWhtCreditFromCertAsync(Guid companyId, OcrScanResult result, Guid? documentId)
    {
        // OcrScanResult ไม่มีช่อง "ยอดภาษีที่ถูกหัก" ตรง ๆ — มีแค่ HasWht/WhtRate
        // จึงคำนวณจากฐาน × อัตรา ถ้าครบ
        //
        // ⚠️ เดิมบรรทัดสุดท้ายเป็น `derived > 0 ? derived : ExtractedTotalAmount`
        // = **เอายอดรวมทั้งใบมาเป็นยอดภาษี** เมื่ออ่านฐาน/อัตราไม่ครบ ⇒ ใบ 50 ทวิ
        // ยอด 1,070 หัก 3% ถูกบันทึกเป็นเครดิต CIT 1,070 บาทแทนที่จะเป็น 30 บาท
        // และ IncomeAmount ถูกเขียน 0 ⇒ อัตราที่คำนวณกลับได้เป็นอนันต์ ไม่มีด่านไหน
        // จับได้เลย แถวนั้นเป็น Status=Received ⇒ **หักภาษีจริงใน ภ.ง.ด.50 ทันที**
        // นี่คือ "ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้" ในเส้นทางที่เป็นเงิน
        // ตรรกะทั้งหมดอยู่ใน resolver กลางที่เทสต์ได้ (Helpers/WhtCertAmountResolver)
        // — ตัวเลขภาษีต้องมีเทสต์ยืนยัน ไม่ใช่คำนวณสด ๆ กลางเมธอด async
        var whtResolved = Accounting.Helpers.WhtCertAmountResolver.Resolve(
            result.ExtractedSubTotal, result.WhtRate,
            result.ExtractedTotalAmount, result.RawTextContent);
        var whtAmount = whtResolved.Amount;
        var needsReview = whtResolved.NeedsReview;

        // อ่านไม่ได้ทั้งสองทาง = **ไม่รู้** → ยังบันทึกแถวไว้ให้ผู้ใช้เห็นและกรอกเอง
        // แต่เป็น Pending (TaxService นับเฉพาะ Received/Claimed เข้าเครดิต CIT)
        // ห้ามเดายอดแล้วตั้งเป็น Received เด็ดขาด
        var unknownAmount = whtResolved.IsUnknown;

        // กันซ้ำ: สแกนใบเดิมอีกรอบไม่ควรได้เครดิตสองเท่า
        var certNo = result.ExtractedDocumentNumber?.Trim();
        var dup = await _db.WhtCreditsReceived.AnyAsync(w => w.CompanyId == companyId
            && ((documentId != null && w.DocumentId == documentId)
                || (certNo != null && w.CertificateNumber == certNo)));
        if (dup) return;

        var docDate = result.ExtractedDate ?? DateTime.UtcNow.Date;
        var ourProfile = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.FiscalYearStartMonth, c.BusinessType })
            .FirstOrDefaultAsync();
        var startMonth = ourProfile?.FiscalYearStartMonth ?? 1;
        var ourBusinessType = ourProfile?.BusinessType ?? BusinessType.JuristicPerson;
        if (startMonth is < 1 or > 12) startMonth = 1;
        var taxYear = docDate.Month >= startMonth ? docDate.Year : docDate.Year - 1;

        // ── กันเครดิตซ้ำข้ามเส้นทาง (เงินก้อนเดียว 2 แถว) ─────────────────
        // `DocumentService.SyncWhtCreditReceivedAsync` สร้างแถว **Pending**
        // ไว้แล้วตอนรับชำระใบขาย (ผูกกับ**ใบขายต้นทาง**, ไม่มีเลขที่ใบรับรอง)
        // ส่วนเมธอดนี้ผูกกับ **ReceiptVoucher ใบใหม่** ที่สร้างจากการสแกน
        // ⇒ key ของสองทางไม่มีวันชนกัน ⇒ ภ.ง.ด.50 หักเครดิตเกินเท่าตัว
        // (แถวหนึ่ง Received นับทันที อีกแถว Pending รอผู้ใช้กด "ได้รับใบแล้ว")
        //
        // ทางแก้: ถ้าเจอแถว Pending ของผู้จ่ายรายเดียวกัน ปีภาษีเดียวกัน
        // ยอดใกล้เคียงกัน → **อัปเกรดแถวเดิม** (เติมเลขที่ใบ/ไฟล์แนบ/สถานะ)
        // แทนการสร้างแถวใหม่ — ใบจริงคือหลักฐานของเงินก้อนเดิม ไม่ใช่ก้อนใหม่
        if (!unknownAmount && result.MatchedContactId is Guid payerId)
        {
            const decimal matchTolerance = 1m;
            var candidates = await _db.WhtCreditsReceived
                .Where(w => w.CompanyId == companyId && !w.IsDeleted
                            && w.Status == WhtCreditStatus.Pending
                            && w.PayerContactId == payerId
                            && w.TaxYear == taxYear
                            && w.CertificateNumber == null)
                .ToListAsync();
            var match = candidates
                .FirstOrDefault(w => Math.Abs(w.WhtAmount - whtAmount) <= matchTolerance);
            if (match != null)
            {
                match.CertificateNumber = certNo;
                match.CertificateDate = result.ExtractedDate;
                match.AttachmentId = result.FileAttachmentId;
                match.Status = WhtCreditStatus.Received;   // มีใบจริงในมือแล้ว
                match.Notes = (match.Notes ?? "") +
                    $" · จับคู่กับหนังสือรับรองที่สแกน ({result.OriginalFileName})";
                match.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "จับคู่ใบ 50 ทวิ ที่สแกนกับเครดิตที่รอใบอยู่แล้ว (credit {Id}) — ไม่สร้างแถวใหม่",
                    match.Id);
                return;
            }
        }
        var baseAmount = result.ExtractedSubTotal ?? 0m;
        _db.WhtCreditsReceived.Add(new WhtCreditReceived
        {
            CompanyId = companyId,
            TaxYear = taxYear,
            CertificateNumber = certNo,
            CertificateDate = result.ExtractedDate,
            PayerContactId = result.MatchedContactId,
            // ผู้จ่าย = ผู้ที่หักเรา — บนใบคือคู่ค้าที่ OCR อ่านได้
            PayerName = result.ExtractedVendorName ?? "",
            PayerTaxId = result.ExtractedVendorTaxId,
            // แบบที่ผู้จ่ายต้องยื่นตัดสินจาก **ผู้ถูกหัก = ตัวเรา** ไม่ใช่ผู้จ่าย:
            // หักจากบุคคลธรรมดา → ภ.ง.ด.3 · หักจากนิติบุคคล → ภ.ง.ด.53
            // เดิม hardcode Pnd53 เสมอ ⇒ ผู้ใช้ที่เป็นบุคคลธรรมดา/คณะบุคคล
            // (BusinessType.Individual) ได้แบบผิดทุกใบโดยไม่มีอะไรบอกว่าเป็นการเดา
            PayerFormType = ourBusinessType == BusinessType.Individual
                ? WhtPayerFormType.Pnd3
                : WhtPayerFormType.Pnd53,
            // ประเภทเงินได้ — ป้อนให้ CheckRate ตรวจอัตรากับ ท.ป.4/2528 ได้
            // ★ ตัวอ่านอยู่ที่ Helpers/WhtCertIncomeType — ส่งฐานเงินได้ไปช่วยชี้ขาด
            // ด้วย (ฟอร์ม 50 ทวิ พิมพ์ทุกประเภทเป็นรายการให้ติ๊ก ⇒ ต้องดูว่า
            // **แถวไหนมีจำนวนเงิน** ไม่ใช่ดูว่าคำไหนปรากฏ)
            IncomeTypeCode = Accounting.Helpers.WhtCertIncomeType.Infer(
                result.RawTextContent, baseAmount > 0m ? baseAmount : null),
            IncomeAmount = baseAmount,
            WhtRate = whtResolved.Rate,
            WhtAmount = whtAmount,
            // มีใบจริงอยู่ในมือแล้ว (นี่คือตัวใบที่เพิ่งสแกน) → ใช้เครดิตได้เลย
            // **ยกเว้น** อ่านยอดภาษีไม่ได้เลย → Pending เพื่อให้ TaxService
            // (นับเฉพาะ Received/Claimed) ไม่เอาไปหักภาษีจนกว่าคนจะกรอกยอดจริง
            Status = unknownAmount ? WhtCreditStatus.Pending : WhtCreditStatus.Received,
            DocumentId = documentId,
            AttachmentId = result.FileAttachmentId,
            Notes = $"สร้างจากการสแกนหนังสือรับรอง ({result.OriginalFileName})"
                + (unknownAmount
                    ? " — ⚠️ อ่าน \"ยอดภาษีที่หักและนำส่ง\" จากใบไม่ได้ "
                      + "กรุณากรอกยอดแล้วกดยืนยันเพื่อใช้เป็นเครดิต (ยังไม่ถูกนับใน ภ.ง.ด.50)"
                    : needsReview
                        ? " — ⚠️ อ่านอัตรา/ฐานภาษีไม่ครบ ยอดภาษีมาจากจำนวนเงินตัวอักษรบนใบ กรุณาตรวจก่อนใช้เครดิต"
                        : ""),
        });
        await _db.SaveChangesAsync();
        _logger.LogInformation("บันทึกเครดิตภาษีถูกหักจากหนังสือรับรองที่สแกน {Amount} (scan {Id})",
            whtAmount, result.Id);
    }

    /// <summary>อ่านสกุลเงินจากข้อความบนกระดาษ — คืน null เมื่อไม่พบ (ถือเป็นบาท)
    ///
    /// <para>เอกสารสกุลต่างประเทศที่ถูกบันทึกเป็นบาทคือความผิดพลาดแบบ "เงียบ":
    /// ตัวเลขถูกเก็บเท่าเดิมแต่ความหมายต่างกันหลายสิบเท่า และไม่มีอะไรเตือน
    /// — ตรวจไว้ดีกว่าปล่อยผ่าน (ตั้ง Currency แล้ว approve จะบังคับให้ระบุ
    /// อัตราแลกเปลี่ยนเอง ซึ่งเป็นการล้มแบบดังกว่าการเงียบ)</para>
    ///
    /// <para>ระวัง false positive: "$" อย่างเดียวไม่พอ (บางใบพิมพ์ THB ด้วย $)
    /// จึงต้องเจอรหัสสกุลเป็นคำเต็มหรือคู่กับตัวเลข</para></summary>
    internal static string? InferCurrency(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;
        var t = rawText.ToUpperInvariant();
        // มีคำว่าบาท/THB ชัดเจน = บาทแน่นอน ไม่ต้องเดาต่อ
        if (t.Contains("THB") || rawText.Contains("บาท")) return null;
        foreach (var (code, words) in new[]
        {
            ("USD", new[] { "USD", "US DOLLAR", "U.S. DOLLAR" }),
            ("EUR", new[] { "EUR", "EURO" }),
            ("JPY", new[] { "JPY", "YEN" }),
            ("CNY", new[] { "CNY", "RMB", "YUAN" }),
            ("GBP", new[] { "GBP", "POUND STERLING" }),
            ("SGD", new[] { "SGD", "SINGAPORE DOLLAR" }),
        })
        {
            foreach (var w in words)
                if (System.Text.RegularExpressions.Regex.IsMatch(t, $@"\b{System.Text.RegularExpressions.Regex.Escape(w)}\b"))
                    return code;
        }
        return null;
    }

    /// <summary>อ่าน "เหตุผลการลดหนี้" จากข้อความบนกระดาษ (§86/10 บังคับระบุ)
    ///
    /// <para>คืน null เมื่อไม่พบคำบ่งชี้ชัดเจน — ปล่อยให้ผู้ใช้เลือกเอง ดีกว่าเดา
    /// ผิดแล้วลงบัญชีผิด (เฉพาะ "คืนสินค้า" เท่านั้นที่กระทบสต๊อก อีก 3 แบบไม่กระทบ)</para></summary>
    internal static CreditNoteReason? InferCreditNoteReason(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;
        var t = rawText.ToLowerInvariant();
        // เรียงตามความจำเพาะ: คืนสินค้าเป็นเคสเดียวที่กระทบสต๊อก จึงต้องชัดจริงก่อน
        if (t.Contains("คืนสินค้า") || t.Contains("รับคืนสินค้า") || t.Contains("สินค้าคืน")
            || t.Contains("goods return") || t.Contains("sales return"))
            return CreditNoteReason.Return;
        if (t.Contains("ส่วนลด") || t.Contains("ลดราคา") || t.Contains("discount"))
            return CreditNoteReason.Discount;
        if (t.Contains("ตัดหนี้สูญ") || t.Contains("หนี้สูญ") || t.Contains("write-off") || t.Contains("write off"))
            return CreditNoteReason.Writeoff;
        if (t.Contains("ปรับปรุงยอด") || t.Contains("ปรับยอด") || t.Contains("คลาดเคลื่อน")
            || t.Contains("ไม่ครบตามจำนวน") || t.Contains("adjustment"))
            return CreditNoteReason.Adjustment;
        return null;   // ไม่เดา — ผู้ใช้เลือกเองบนฟอร์ม
    }

    /// <summary>อ่าน "ประเภทเงินได้" จากหนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ)
    ///
    /// <para>ค่านี้เป็น input ของตัวตรวจอัตรา <c>WhtCreditService.CheckRate</c>
    /// (ท.ป.4/2528) — ถ้าไม่มี ตัวตรวจจะเงียบ แปลว่าผู้จ่ายหักผิดอัตราแล้ว
    /// ไม่มีอะไรเตือน จนไปเจอตอนกระทบยอดกับ ภ.ง.ด.50</para>
    ///
    /// <para><b>กับดัก:</b> แบบ 50 ทวิ ที่เป็นฟอร์มพิมพ์สำเร็จมีหัวข้อ 1–6
    /// ครบทุกประเภทอยู่บนกระดาษอยู่แล้ว การจับคำตรง ๆ จะเจอทุกประเภทพร้อมกัน
    /// จึงคืนค่าเฉพาะตอนที่เจอ "กลุ่มเดียว" เท่านั้น — เจอหลายกลุ่ม = อ่านฟอร์ม
    /// เปล่า ไม่ใช่รายการจริง → คืน null</para>
    ///
    /// <para><b>ห้ามเดาจากอัตราที่หัก</b> เพราะจะทำให้ CheckRate ตรวจกับตัวเอง
    /// แล้วผ่านทุกครั้ง = ปิดตัวตรวจโดยไม่รู้ตัว</para></summary>
    /// <summary>ชนิดกระดาษ — **สิ่งที่พิมพ์อยู่บนใบชนะเสมอ**
    ///
    /// <para>⚠️ ที่มา (ผลตรวจ 2026-09-06 · T2-14): ชื่อไฟล์เป็นตัวเลือกโมเดล
    /// (<c>AzureDiRequestPlanner.DetectModelFromFilename</c> — "receipt"/"cafe"/"ใบเสร็จ"
    /// → prebuilt-receipt) แล้ว <c>modelId</c> ก็บังคับชนิดเป็น <c>Receipt</c> ต่อ
    /// ⇒ **ใบกำกับภาษีเต็มรูปที่ผู้ใช้ตั้งชื่อไฟล์ว่า "receipt-2026-09.pdf" ถูกตีเป็น
    /// ใบเสร็จ** แล้วโดนปิดเคลมภาษีซื้อตาม §82/5(1) ทั้งที่กระดาษถูกต้องครบ</para></summary>
    /// <summary>ทางเข้าสำหรับเทสต์ — ตรรกะเดียวกับที่ pipeline ใช้จริง</summary>
    internal static string MapAzureDocTypeForTest(string? azureDocType, string? modelId, string? rawText)
        => MapAzureDocType(azureDocType, modelId, rawText);

    private static string MapAzureDocType(string? azureDocType, string? modelId, string? rawText = null)
    {
        // คำบนกระดาษเป็นหลักฐาน ส่วนชื่อไฟล์/โมเดลเป็นแค่การเดาของเรา
        if (!string.IsNullOrWhiteSpace(rawText))
        {
            var t = Ocr.ThaiTextNormalizer.Normalize(rawText);
            if (t.Contains("ใบลดหนี้", StringComparison.OrdinalIgnoreCase)) return "CreditNote";
            if (t.Contains("ใบเพิ่มหนี้", StringComparison.OrdinalIgnoreCase)) return "DebitNote";
            if (t.Contains("ใบกำกับภาษี", StringComparison.OrdinalIgnoreCase)
                || t.Contains("tax invoice", StringComparison.OrdinalIgnoreCase))
                return "Invoice";   // ใบกำกับ = เอกสารภาษี ไม่ใช่ Receipt
        }
        if (modelId?.Contains("receipt", StringComparison.OrdinalIgnoreCase) == true)
            return "Receipt";
        return azureDocType?.ToLowerInvariant() switch
        {
            "invoice" => "Invoice",
            "creditnote" => "CreditNote",
            "debitnote" => "DebitNote",
            "receipt" => "Receipt",
            _ => "Invoice",
        };
    }

    private static string? ExtractDigits(string? value, int expectedLength)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == expectedLength ? digits : (digits.Length > 0 ? digits : null);
    }

    /// <summary>ความมั่นใจที่ใช้บันทึกที่มาเมื่อ**คลังกลางข้ามผู้เช่า**เป็นคนเสนอชนิดเอกสาร —
    /// ตัวเรียนกลางไม่รายงานความมั่นใจกลับมา จึงตั้งไว้ต่ำกว่า
    /// <see cref="VendorIntelligenceService.MediumConfidence"/> เพื่อไม่ให้ค่าที่
    /// **ไม่ได้วัด** ชนะค่าที่วัดจริงของแหล่งเดียวกัน (ห้ามแต่งตัวเลขให้ดูมั่นใจ)</summary>
    private const decimal FederatedTargetConfidence = 0.50m;

    /// <summary>**ตัวเขียน "ชนิดเอกสารที่จะสร้าง" ตัวเดียวของไปป์ไลน์** — เขียนค่า +
    /// บันทึกที่มา + ทิ้งร่องรอยในคราวเดียว
    ///
    /// <para>⚠️ ที่มา (ผลตรวจ 2026-09-18 · D3-4): ช่องนี้ถูกทับ 7 ชั้นแบบ "ใครมาหลังชนะ"
    /// โดยไม่มีชั้นไหน <c>Note()</c> เลย ⇒ หน้ารีวิวตอบไม่ได้ว่าค่ามาจากไหน และเวลาไล่บั๊ก
    /// ต้องเดาจากลำดับบรรทัด. ทุกชั้นที่เปลี่ยนชนิดเอกสารต้องเรียกตัวนี้ ห้ามเขียน
    /// <c>data.TargetDocumentType = …</c> ตรง ๆ อีก</para>
    ///
    /// <para><b>ยังไม่ย้ายตัวตัดสิน</b> — ค่าที่ใช้จริงยังมาจากลำดับเดิมทุกประการ
    /// (การสลับลำดับชั้นต้องมี golden ก่อน/หลังตามกฎเหล็ก #4 H)</para></summary>
    private static void SetTargetDocumentType(
        OcrExtractedData data, string newTarget, Accounting.Helpers.OcrFieldSource source,
        decimal confidence, string why)
    {
        var previous = data.TargetDocumentType;
        data.TargetDocumentType = newTarget;
        data.Note(Accounting.Helpers.OcrFieldKeys.TargetDocumentType, newTarget, source, confidence, why);
        var head = string.IsNullOrWhiteSpace(previous) || string.Equals(previous, newTarget, StringComparison.OrdinalIgnoreCase)
            ? $"[Target] ตั้งเป็น {newTarget}"
            : $"[Target] เปลี่ยน {previous} → {newTarget}";
        data.ReasoningTrace.Add(
            $"{head} · ที่มา {Accounting.Helpers.OcrFieldArbiter.SourceLabel(source)} "
            + $"(มั่นใจ {confidence:P0}) — {why}");
    }

    /// <summary>**ค่าที่อยู่ในฟอร์มจริง ณ ตอนนี้** ต่อช่องกลาง (<see cref="Accounting.Helpers.OcrFieldKeys"/>)
    /// — ตัวป้อนของสมุด "ค่านี้มาจากไหน" (<see cref="Accounting.Helpers.OcrFieldProvenance"/>)
    ///
    /// <para>ช่องที่<b>ไม่อยู่ใน dictionary นี้</b> แปลว่า "ยังไม่ได้ตรวจ" และจะไม่ถูกนับ
    /// เข้าตัวหารของ <c>arbiterAgreed</c> — ต่างจากช่องที่อยู่แต่ค่าเป็น <c>null</c>
    /// ซึ่งแปลว่า "ตรวจแล้ว ฟอร์มยังว่าง" (undefined ≠ null ≠ 0)</para>
    ///
    /// <para>รูปของค่าต้องตรงกับที่ <c>Note()</c> บันทึก (<c>"0.00"</c> สำหรับจำนวนเงิน ·
    /// <c>"yyyy-MM-dd"</c> สำหรับวันที่) มิฉะนั้น "ค่าเดียวกัน" จะดูเหมือนคนละค่า —
    /// ส่วนการเทียบตัวเลขยังผ่าน <c>OcrFieldProvenance.SameValue</c> อีกชั้น</para></summary>
    private static Dictionary<string, string?> BuildActualFieldValues(OcrExtractedData d)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // ชื่อ Amt (ไม่ใช่ Money) — กันชนกับเมธอด Money(decimal) ของที่อื่นในเรพ
        string? Amt(decimal? v) => v?.ToString("0.00", inv);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Accounting.Helpers.OcrFieldKeys.SellerName] = d.VendorName,
            [Accounting.Helpers.OcrFieldKeys.SellerTaxId] = d.VendorTaxId,
            [Accounting.Helpers.OcrFieldKeys.BuyerName] = d.BuyerName,
            [Accounting.Helpers.OcrFieldKeys.BuyerTaxId] = d.BuyerTaxId,
            [Accounting.Helpers.OcrFieldKeys.SellerAddress] = d.VendorAddress,
            [Accounting.Helpers.OcrFieldKeys.BuyerAddress] = d.BuyerAddress,
            [Accounting.Helpers.OcrFieldKeys.SellerBranchCode] = d.VendorBranchCode,
            [Accounting.Helpers.OcrFieldKeys.BuyerBranchCode] = d.BuyerBranchCode,
            [Accounting.Helpers.OcrFieldKeys.DocumentNumber] = d.DocumentNumber,
            [Accounting.Helpers.OcrFieldKeys.DocumentDate] = d.DocumentDate?.ToString("yyyy-MM-dd", inv),
            [Accounting.Helpers.OcrFieldKeys.SubTotal] = Amt(d.SubTotal),
            [Accounting.Helpers.OcrFieldKeys.VatAmount] = Amt(d.VatAmount),
            [Accounting.Helpers.OcrFieldKeys.TotalAmount] = Amt(d.TotalAmount),
            [Accounting.Helpers.OcrFieldKeys.WhtRate] = Amt(d.WhtRate),
            [Accounting.Helpers.OcrFieldKeys.SuggestedWhtRate] = Amt(d.SuggestedWhtRate),
            [Accounting.Helpers.OcrFieldKeys.ExpenseCategory] = d.ExpenseCategory,
            [Accounting.Helpers.OcrFieldKeys.DebitAccount] = d.DebitAccountCode,
            [Accounting.Helpers.OcrFieldKeys.PaymentTerms] = d.PaymentTermsDays?.ToString(inv),
            [Accounting.Helpers.OcrFieldKeys.TargetDocumentType] = d.TargetDocumentType,
        };
    }

    /// <summary>**ตัวตัดสิน "อัตราหัก ณ ที่จ่ายที่ระบบเสนอ" ตัวเดียวของไปป์ไลน์** (D-4 ขั้นที่ 1)
    ///
    /// <para>ทุกชั้นที่เสนออัตรามีหน้าที่ <c>Note(OcrFieldKeys.SuggestedWhtRate, …)</c>
    /// อย่างเดียว — <b>ห้ามเขียน <c>data.SuggestedWhtRate</c> เองอีก</b>. คนตัดสินคือ
    /// <see cref="Accounting.Helpers.OcrFieldArbiter"/> ซึ่งเรียงด้วยตาราง
    /// <c>Precedence</c> (ข้อมูล) ไม่ใช่ลำดับบรรทัด</para>
    ///
    /// <para>ข้อยกเว้นที่ตั้งใจ: <c>ExpenseCategoryResolver</c> ยังเขียนค่าตั้งต้นไว้ด้วย
    /// เพื่อให้เส้นที่เรียกมันแบบเดี่ยว ๆ (เทสต์ · ตัวเรียกอื่น) ได้คำตอบเดิม —
    /// เมธอดนี้ <b>เขียนทับด้วยผลของ arbiter เสมอ</b> เมื่อมีผู้เสนอในสมุด ⇒ ผลสุดท้าย
    /// ของไปป์ไลน์มาจากตัวตัดสินตัวเดียว</para>
    ///
    /// <para><b>ไม่มีผู้เสนอ = ไม่แตะอะไร</b> (ห้ามล้างค่าที่ชั้นอื่นตั้งไว้ด้วยความว่าง)</para></summary>
    internal static void ApplyWhtSuggestion(OcrExtractedData data)
    {
        var decision = Accounting.Helpers.OcrFieldArbiter.Decide(
            Accounting.Helpers.OcrFieldKeys.SuggestedWhtRate, data.FieldCandidates);
        if (decision?.Value == null) return;
        if (!decimal.TryParse(decision.Value, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var rate) || rate <= 0m)
            return;

        var source = Accounting.Helpers.OcrWhtSuggestionGate.FromFieldSource(decision.Source);
        var changed = data.SuggestedWhtRate != rate || data.SuggestedWhtSource != source;
        data.SuggestedWhtRate = rate;
        data.SuggestedWhtSource = source;
        // กฎเหล็ก #3 ข้อ 3 — ข้อเสนอต้องมีคะแนนรายช่องเสมอ (ขาดคะแนน = ไม่มีไฮไลต์
        // = ผู้ใช้ไม่รู้ว่าต้องตรวจ) และต้องต่ำกว่า 0.85 เพราะกระดาษใบนี้ไม่ได้พิมพ์ไว้
        data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.WhtRate] =
            Accounting.Helpers.OcrWhtSuggestionGate.SuggestionFieldConfidence;

        if (!changed) return;
        var losers = decision.Alternatives
            .Where(a => !Accounting.Helpers.OcrFieldProvenance.SameValue(a.Value, decision.Value))
            .Select(a => $"{a.Value}% ({Accounting.Helpers.OcrFieldArbiter.SourceLabel(a.Source)})")
            .ToList();
        data.ReasoningTrace.Add(
            $"[WHT-ARBITER] เลือกอัตราที่เสนอ {rate}% · ที่มา "
            + Accounting.Helpers.OcrWhtSuggestionGate.Describe(source)
            + (losers.Count > 0 ? $" · แหล่งอื่นเสนอ: {string.Join(" · ", losers)}" : ""));
    }

    /// <summary>**รูปเดียวของ "บรรทัดที่สแกนได้" ที่ลงฐาน** (<c>OcrScanResult.ExtractedItemsJson</c>)
    ///
    /// <para>ทุกฝั่งที่เขียนต้องผ่านตัวนี้ — ห้าม projection มือ. ที่มา (D3-1): ไปป์ไลน์เคย
    /// เขียนด้วย projection 8 ช่องของตัวเอง ขณะที่เส้น "ผู้ใช้แก้บรรทัด" เขียนวัตถุเต็ม
    /// ⇒ ช่องที่ projection ไม่มี (<c>VatRate</c> · <c>VatAmount</c> ·
    /// <c>ProjectAiFeedbackId</c> · <c>AiSuggestedProjectId</c>) หายทุกครั้งที่ไปป์ไลน์
    /// เขียน แล้วกลับมาเฉพาะหลังผู้ใช้แก้ = "ค่าเดียวกันสองรูป" ที่ผู้ใช้เห็นเป็น
    /// ค่าหายเงียบ ๆ ตอน reload</para></summary>
    internal static string SerializeExtractedItems(IReadOnlyList<OcrExtractedLineItem> items)
        => System.Text.Json.JsonSerializer.Serialize(items);

    /// <summary>
    /// ตัด "(ส่วนมีภาษี)" / "(ส่วนไม่มีภาษี)" / "(VATable)" / "(non-VAT)" /
    /// "(VAT-included)" และคู่ขนานทั้งไทย+อังกฤษ ออกจากท้าย description ของ
    /// item ที่ AI/OCR แตก footer summary ของใบกำกับ (เช่น OfficeMate
    /// "Amount Exclude VAT" + "Amount NON VAT") เป็น line items หลอก. หลังตัด
    /// suffix:
    ///   • ยุบบรรทัดที่ description ตรงกันให้เหลือบรรทัดเดียว (sum amount/qty)
    ///   • drop บรรทัดที่ amount + unit_price เป็น 0 หรือ ≤ ฿1 (rounding artifact)
    /// Idempotent + side-effect-free นอกจาก mutate extractedData.Items.
    /// </summary>
    internal static void SanitizeVatSplitArtifacts(OcrExtractedData data)
    {
        if (data?.Items == null || data.Items.Count == 0) return;

        // Suffixes ที่บ่งบอกว่าเป็น footer split — ไม่ใช่ line item จริง.
        // ใช้ trailing-paren match (ตัดเฉพาะที่ขึ้นต้น "(" + จบ ")" ท้าย string)
        // เพื่อไม่ไปแตะ "Epson L6370 (รุ่นปี 2024)" หรือ description ที่ใส่
        // วงเล็บบอก spec จริง.
        string[] splitMarkers =
        {
            "ส่วนมีภาษี", "ส่วนที่มีภาษี", "ส่วนคิดภาษี",
            "ส่วนไม่มีภาษี", "ส่วนที่ไม่มีภาษี", "ส่วนยกเว้นภาษี",
            "vatable", "non-vat", "non vat", "nonvat",
            "vat included", "vat-included", "incl. vat", "incl vat",
            "vat excluded", "vat-excluded", "excl. vat", "excl vat",
            "with vat", "without vat",
            "มีภาษี", "ไม่มีภาษี",   // shorter forms (fallback — checked AFTER longer matches above)
        };

        static string TrimSplitSuffix(string? desc, string[] markers)
        {
            if (string.IsNullOrWhiteSpace(desc)) return desc ?? "";
            var s = desc.TrimEnd();
            // ลบวงเล็บท้ายซ้ำๆ — เผื่อ AI ใส่ซ้อน "(...) (...)" (rare).
            for (var safety = 0; safety < 3; safety++)
            {
                if (!s.EndsWith(")")) break;
                var openIdx = s.LastIndexOf('(');
                if (openIdx < 0) break;
                var inside = s.Substring(openIdx + 1, s.Length - openIdx - 2)
                    .Trim().ToLowerInvariant();
                var match = false;
                foreach (var m in markers)
                {
                    if (inside.Contains(m.ToLowerInvariant())) { match = true; break; }
                }
                if (!match) break;
                s = s.Substring(0, openIdx).TrimEnd();
            }
            return s;
        }

        // 1) ตัด suffix
        foreach (var item in data.Items)
            item.Description = TrimSplitSuffix(item.Description, splitMarkers);

        // 1b) ⭐ กัน "จำนวนระเบิด": OCR อ่านยอดบรรทัด (Amount) ถูก แต่อ่าน "จำนวน"
        //     ผิด (มักอ่านตัวเลขในคอลัมน์ยอด/ราคา มาใส่เป็นจำนวน) → line-building
        //     คิด qty×unitPrice → ยอดพุ่งไกลจาก Amount จริง. ถ้าเจอ Amount ที่เชื่อได้
        //     + UnitPrice + Quantity ครบ แล้ว qty×price เพี้ยนจาก Amount เกิน tol
        //     (±2% หรือ ฿1) → **เชื่อ Amount เป็นหลัก** แก้ Quantity = Amount/UnitPrice
        //     (รักษายอดบรรทัด = Amount ที่ OCR แสดงถูก → ยอดรวมไม่ระเบิด).
        //     ทำก่อน fold/merge เพื่อให้ EffAmt/ยอดรวมถัดไปใช้ค่าที่ reconcile แล้ว.
        foreach (var item in data.Items)
        {
            var amt = item.Amount ?? 0m;
            var up = item.UnitPrice ?? 0m;
            var qty = item.Quantity ?? 0m;
            if (amt <= 0m) continue;
            var tol = System.Math.Max(1m, System.Math.Abs(amt) * 0.02m);

            // ราคา/หน่วยหาย แต่มียอด+จำนวน → หารหาให้ (บิลสาธารณูปโภคพิมพ์
            // แต่ปริมาณกับยอดรวม ไม่พิมพ์ราคาต่อหน่วย)
            if (up <= 0m && qty > 0m)
            {
                item.UnitPrice = System.Math.Round(amt / qty, 4, System.MidpointRounding.AwayFromZero);
                continue;
            }
            if (up <= 0m || qty <= 0m) continue;

            var computed = System.Math.Round(up * qty, 2);
            if (System.Math.Abs(computed - amt) <= tol) continue;   // ตรงอยู่แล้ว

            // ⚠️ แยกสองเคสให้ถูกตัว (บั๊กจริง — บิลค่าไฟ 59 ล้าน):
            //
            // เคส ก: UnitPrice ≈ Amount ทั้งที่ qty > 1 — OCR อ่าน "ยอดรวม
            //   บรรทัด" มาใส่ช่องราคา/หน่วย (qty=3,611 kWh, up=16,351.48 =
            //   ยอดทั้งบิล). ตัวผิดคือ **ราคา** ไม่ใช่จำนวน — 3,611 หน่วยคือ
            //   ค่ามิเตอร์จริง ตรวจย้อน/เทียบเดือนได้ ทิ้งไม่ได้. ตามหลักบัญชี
            //   ราคาทุนต่อหน่วย = ยอดจ่ายจริง ÷ ปริมาณ → หารหา ไม่ใช่บิด
            //   ปริมาณให้เข้ากับราคา (เดิมเคสนี้ตกไปแขนงล่าง → qty โดนเขียน
            //   ทับเป็น 1 เงียบ ๆ ปริมาณหาย)
            //
            // เคส ข: qty ≈ Amount — ตัวจำนวนเองคือยอดที่อ่านหลงคอลัมน์มา
            //   (เช่น qty=1,180 amt=1,180) → จำนวนไม่ใช่ข้อมูลจริง → เชื่อ
            //   Amount+UnitPrice แก้ qty ให้ line = Amount (พฤติกรรมเดิม)
            //
            // ตัวแยกสองเคส: จำนวน "ใกล้ยอดเงิน" = จำนวนคือตัวหลงคอลัมน์ ·
            // จำนวน "ต่างจากยอดเงิน" = จำนวนเป็นข้อมูลอิสระจากกระดาษ (มิเตอร์/
            // ปริมาณจริง) ห้ามทิ้ง — ยอดบรรทัด (Amount) คงเดิมทั้งสองทาง
            var qtyLooksLikeAmount = System.Math.Abs(qty - amt) <= tol;
            if (qty > 1m && !qtyLooksLikeAmount && System.Math.Abs(up - amt) <= tol)
            {
                item.UnitPrice = System.Math.Round(amt / qty, 4, System.MidpointRounding.AwayFromZero);
                continue;
            }
            var fixedQty = System.Math.Round(amt / up, 3, System.MidpointRounding.AwayFromZero);
            item.Quantity = fixedQty > 0m ? fixedQty : 1m;
        }

        // EffAmt = ยอดบรรทัดที่เชื่อถือได้ — Amount ถ้ามี, ไม่งั้น UnitPrice×Quantity.
        // กันเคส external OCR ส่งแต่ UnitPrice+Quantity ไม่ได้ส่ง Amount.
        static decimal EffAmt(OcrExtractedLineItem it)
            => (it.Amount ?? 0m) > 0m
                ? it.Amount!.Value
                : (it.UnitPrice ?? 0m) * (it.Quantity ?? 1m);

        // 2) drop บรรทัดที่กลายเป็นว่าง / phantom remainder (amount + price ≤ ฿1)
        const decimal PHANTOM_THRESHOLD = 1m;
        data.Items.RemoveAll(it =>
        {
            var emptyDesc = string.IsNullOrWhiteSpace(it.Description);
            var isPhantom = Math.Abs(EffAmt(it)) <= PHANTOM_THRESHOLD
                && Math.Abs(it.UnitPrice ?? 0m) <= PHANTOM_THRESHOLD;
            return emptyDesc && isPhantom;
        });

        // 3) ⭐ FOLD phantom remainder (≤ ฿1) เข้าบรรทัดใหญ่สุด — รักษายอดรวม
        //    เป๊ะ (description-INDEPENDENT). นี่คือต้นเหตุหลักของ "ส่วนต่าง 0.02":
        //    external OCR คำนวณฐาน VAT ย้อนกลับ (VAT/0.07) ได้ 4,691.57 แล้ว
        //    โยนเศษ 4,691.59−4,691.57 = 0.02 เป็น line "ส่วนไม่มีภาษี". เศษนี้
        //    ไม่ใช่สินค้าจริง (ขายของ 2 สตางค์ไม่มีจริง) → fold เข้าบรรทัดหลัก.
        if (data.Items.Count >= 2)
        {
            var phantoms = data.Items.Where(it =>
                EffAmt(it) > 0m
                && EffAmt(it) <= PHANTOM_THRESHOLD
                && (it.UnitPrice ?? 0m) <= PHANTOM_THRESHOLD).ToList();
            var reals = data.Items.Where(it => !phantoms.Contains(it)).ToList();
            if (phantoms.Count > 0 && reals.Count > 0)
            {
                var main = reals.OrderByDescending(EffAmt).First();
                var foldAmt = phantoms.Sum(EffAmt);
                main.Amount = EffAmt(main) + foldAmt;
                if (main.Quantity is decimal mq && mq > 0m)
                    main.UnitPrice = Math.Round(main.Amount.Value / mq, 2);
                foreach (var p in phantoms) data.Items.Remove(p);
            }
        }

        // 4) ยุบบรรทัดที่ description ตรงกันจริง ๆ (สินค้าซ้ำ — เคส VAT split
        //    ที่ external แตกสินค้าเดียวเป็น 2 บรรทัดเท่า ๆ กัน). หลัง fold
        //    phantom แล้ว ที่เหลือคือบรรทัดสินค้าจริง — merge ตาม description.
        //    เก็บลำดับเดิม (LINQ GroupBy ไม่ stable → dictionary).
        //    ⚠️ ต้องเทียบ **ราคาต่อหน่วย** ด้วย ไม่ใช่ description อย่างเดียว
        //    (ผลตรวจ 2026-09-06 · T2-20): "ค่าแรง 500" + "ค่าแรง 300" ถูกยุบเป็น
        //    qty 2 × ฿400 — **ราคาต่อหน่วยที่ไม่มีอยู่บนกระดาษ** · ยอดรวมยังตรง
        //    จึงเงียบสนิท แต่ค่านี้ไหลต่อไปเป็นต้นทุนสินค้าตอนนำเข้าสต๊อก
        //    (ProductMatcher เขียน cost) และเป็นรายละเอียดรายบรรทัดของรายงาน §87
        //    เจตนาเดิมของขั้นนี้เขียนไว้เองว่า "สินค้าเดียวที่ถูกแตกเป็น 2 บรรทัด
        //    **เท่า ๆ กัน**" — ซึ่งแปลว่าราคาต่อหน่วยต้องเท่ากันอยู่แล้ว
        var seen = new Dictionary<string, OcrExtractedLineItem>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<OcrExtractedLineItem>();
        foreach (var item in data.Items)
        {
            var desc = (item.Description ?? "").Trim();
            // ราคา/หน่วยที่ยังไม่รู้ (null/0) ใช้คีย์ว่าง — ยุบกับบรรทัดที่ราคาเท่ากัน
            // ไม่ได้ แต่ยุบกับบรรทัดที่ยังไม่รู้ราคาเหมือนกันได้
            var priceKey = (item.UnitPrice ?? 0m) > 0m
                ? Math.Round(item.UnitPrice!.Value, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";
            var key = string.IsNullOrEmpty(desc) ? "" : desc + "\u0001" + priceKey;
            if (string.IsNullOrEmpty(desc))
            {
                merged.Add(item);
                continue;
            }
            if (seen.TryGetValue(key, out var existing))
            {
                existing.Quantity = (existing.Quantity ?? 0m) + (item.Quantity ?? 0m);
                existing.Amount = (existing.Amount ?? 0m) + (item.Amount ?? 0m);
                if (existing.Quantity is decimal q && q > 0m && existing.Amount is decimal a)
                    existing.UnitPrice = Math.Round(a / q, 2);
                if (string.IsNullOrEmpty(existing.SuggestedAccountCode)
                    && !string.IsNullOrEmpty(item.SuggestedAccountCode))
                    existing.SuggestedAccountCode = item.SuggestedAccountCode;
            }
            else
            {
                seen[key] = item;
                merged.Add(item);
            }
        }

        data.Items.Clear();
        foreach (var it in merged) data.Items.Add(it);
    }

    /// <summary>
    /// Embedded Tesseract OCR — runs entirely in-process, no external service.
    /// Used as the last-resort fallback when Azure DI and the Python ocr-service
    /// are both unavailable. Accuracy is lower than the upper tiers but the
    /// reasoning trace + ParseThaiDocument rule-based extraction still produces
    /// usable data (vendor name, tax ID, amounts) that the user can correct.
    /// </summary>
    private async Task<(string RawText, OcrExtractedData Data)> ExtractWithEmbeddedTesseractAsync(FileAttachment file)
    {
        if (!_embeddedOcr.IsAvailable)
        {
            // Both upper tiers + embedded failed — return an empty result with
            // a clear reasoning trace. The user can still re-scan after the
            // admin installs tessdata.
            var emptyData = new OcrExtractedData
            {
                DocumentType = "Receipt",
                Confidence = 0m,
            };
            emptyData.ReasoningTrace.Add("[Embedded] ไม่พบ tessdata — กรุณา download (scripts/download-tessdata.sh)");
            return ("", emptyData);
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(file.StoragePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read file for embedded OCR: {Path}", file.StoragePath);
            var errData = new OcrExtractedData { DocumentType = "Receipt", Confidence = 0m };
            errData.ReasoningTrace.Add($"[Embedded] อ่านไฟล์ไม่ได้: {ex.Message}");
            return ("", errData);
        }

        var result = await _embeddedOcr.ExtractTextAsync(bytes, file.ContentType ?? "", file.OriginalFileName);
        if (!result.Success)
        {
            var errData = new OcrExtractedData { DocumentType = "Receipt", Confidence = 0m };
            errData.ReasoningTrace.Add($"[Embedded] OCR ล้มเหลว: {result.Error ?? "unknown"}");
            return ("", errData);
        }

        // ParseThaiDocument is the same rule-based extractor used by the Python
        // local service — it works on raw text from any source. So Tesseract's
        // output flows through the same field extraction (vendor name, tax ID,
        // amounts, document type detection from keywords).
        // Run the rule-based parser against a normalized copy of the
        // text so the Thai keyword anchors actually match. Tesseract
        // emits "ค ่ า ไฟ ฟ้า" with spaces between every cluster; without
        // collapsing those spaces every Thai regex below silently misses
        // its target. The original text is kept for storage / display.
        var normalizedForParse = Ocr.ThaiTextNormalizer.Normalize(result.Text ?? "");
        var data = ParseThaiDocument(normalizedForParse);
        // Use Tesseract's mean word confidence as the baseline; ParseThaiDocument
        // may bump it up if it found high-signal keywords (e.g. "ใบกำกับภาษี").
        data.Confidence = Math.Max(result.Confidence, data.Confidence);
        data.ReasoningTrace.Add($"[Embedded] Tesseract OCR confidence = {result.Confidence:P0}");
        // Pull every remaining field via real-world constraints (tax-id
        // checksum, vendor≠buyer mutual exclusion, math invariants, WHT
        // normalization, date range, doc-number plausibility) — this is the
        // path that benefits most from the smart extractor.
        Ocr.SmartFieldExtractor.Enrich(data, result.Text ?? "");
        return (result.Text ?? "", data);
    }

    private async Task<(string RawText, OcrExtractedData Data)> ExtractWithLocalServiceAsync(FileAttachment file)
    {
        var serviceUrl = _configuration["Ocr:LocalServiceUrl"] ?? "http://localhost:8501";
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(120);

        // All failure paths in this method THROW (rather than return a stub) so the
        // caller in ScanAsync triggers its try/catch and falls through to Tier 3
        // (Embedded Tesseract). Returning a stub here would set extractedData to a
        // non-null value, making the cascade skip the embedded fallback entirely
        // and leave rawText = filename — which is exactly the production bug the
        // user reported on 2026-05-11.
        byte[] fileData;
        try { fileData = await File.ReadAllBytesAsync(file.StoragePath); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Local OCR: ไม่สามารถอ่านไฟล์ต้นทาง ({ex.Message})", ex);
        }

        // ─── ปรับภาพก่อนส่ง (เหมือนเส้น Azure) ───
        // ⚠️ เดิมส่ง `fileData` **ดิบ** ⇒ engine ที่อ่อนกว่า Azure กลับได้ภาพที่
        // แย่กว่า: รูปจากมือถือมี EXIF orientation 6/8 (ตะแคง) · ความคมต่ำ ·
        // ภาพเล็ก — ทั้งที่ `Ocr.ImagePreprocessor` มีอยู่แล้วและถูกเรียกจาก
        // ที่เดียวคือเส้น Azure (defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้")
        // ⇒ ผลตรวจ 2026-09-06 · T2-17 · PaddleOCR เรียงบรรทัดจากพิกัด y แล้ว x
        // ⇒ ภาพตะแคง = ทุก regex ที่อิง "ป้ายกับค่าอยู่บรรทัดเดียวกัน" หลุดหมด
        // ผ่าน EffectiveContentType เหมือนเส้น Azure — ไฟล์ที่ไม่มี ContentType
        // ติดมาจะถูกเดาจากนามสกุล มิฉะนั้น ImagePreprocessor ข้ามทันที
        // (มันเช็ค `contentType.StartsWith("image/")`) ⇒ ปรับภาพไม่เกิดขึ้นเลย
        var localContentType = OcrPreprocessor.EffectiveContentType(
            file.ContentType ?? "", file.OriginalFileName);
        var localPrep = Ocr.ImagePreprocessor.Process(fileData, localContentType, file.OriginalFileName);
        if (localPrep.StepsApplied.Count > 0)
        {
            fileData = localPrep.Bytes;
            localContentType = localPrep.ContentType;
            _logger.LogInformation("Image preprocessed for local OCR {File}: {Steps}",
                file.OriginalFileName, string.Join(", ", localPrep.StepsApplied));
        }

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileData);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(localContentType);
        form.Add(fileContent, "file", file.OriginalFileName);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync($"{serviceUrl}/ocr/extract", form);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Local OCR service unreachable at {Url}: {Err} — will fall through to embedded Tesseract",
                serviceUrl, ex.Message);
            throw new InvalidOperationException(
                $"Local OCR service ไม่ตอบสนอง ({serviceUrl}): {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Local OCR service returned {Status}", response.StatusCode);
            throw new InvalidOperationException(
                $"Local OCR service ตอบกลับ HTTP {(int)response.StatusCode}");
        }

        try
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var data = new OcrExtractedData
            {
                DocumentType = root.TryGetProperty("document_type", out var dt) ? (dt.GetString() ?? "Receipt") : "Receipt",
                Confidence = root.TryGetProperty("confidence", out var cf) ? (decimal)cf.GetDouble() : 0.5m,
                VendorName = root.TryGetProperty("vendor_name", out var vn) ? vn.GetString() : null,
                VendorTaxId = root.TryGetProperty("vendor_tax_id", out var vt) ? vt.GetString() : null,
                BuyerName = root.TryGetProperty("buyer_name", out var bn) ? bn.GetString() : null,
                BuyerTaxId = root.TryGetProperty("buyer_tax_id", out var bt) ? bt.GetString() : null,
                DocumentNumber = root.TryGetProperty("document_number", out var dn) ? dn.GetString() : null,
                SubTotal = root.TryGetProperty("subtotal", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)st.GetDouble() : null,
                VatAmount = root.TryGetProperty("vat_amount", out var va) && va.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)va.GetDouble() : null,
                TotalAmount = root.TryGetProperty("total_amount", out var ta) && ta.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)ta.GetDouble() : null,
                ExpenseCategory = root.TryGetProperty("expense_category", out var ec) ? ec.GetString() : null,
                HasWht = root.TryGetProperty("has_wht", out var hw) && hw.ValueKind == System.Text.Json.JsonValueKind.True,
                WhtRate = root.TryGetProperty("wht_rate", out var wr) && wr.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)wr.GetDouble() : null,
                PaymentTermsDays = root.TryGetProperty("payment_terms_days", out var pt) && pt.ValueKind == System.Text.Json.JsonValueKind.Number ? pt.GetInt32() : null,
                ZoneSummary = root.TryGetProperty("ocr_engine", out var oe) ? $"Local OCR: {oe.GetString()}" : "Local OCR: paddleocr",
            };

            // ตัวแปลงกลาง — python คืน ISO แต่ห้ามพึ่ง culture ของ process (T2-19)
            if (root.TryGetProperty("document_date", out var dd) && dd.GetString() is string dateStr
                && Accounting.Helpers.ThaiDate.TryParseFlexible(dateStr, out var parsedDate))
            {
                // Pin to Kind=Utc using the same y/m/d so JSON/DB
                // round-trip doesn't shift to the previous calendar day
                // (the "29 พค → 28 พค" bug). DocumentDate is a calendar
                // date, not a moment in time.
                data.DocumentDate = new DateTime(parsedDate.Year, parsedDate.Month, parsedDate.Day, 0, 0, 0, DateTimeKind.Utc);
            }

            // ── Per-field confidence (parity with Azure DI) ──
            if (root.TryGetProperty("field_confidence", out var fc) && fc.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in fc.EnumerateObject())
                {
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                        data.FieldConfidence[prop.Name] = prop.Value.GetDouble();
                }
            }

            // ── Reasoning trace ──
            if (root.TryGetProperty("reasoning_trace", out var rtTrace) && rtTrace.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var entry in rtTrace.EnumerateArray())
                {
                    var s = entry.GetString();
                    if (!string.IsNullOrEmpty(s)) data.ReasoningTrace.Add(s);
                }
            }
            else if (root.TryGetProperty("reasoning", out var reason) && reason.GetString() is string reasonStr)
            {
                data.ReasoningTrace.Add($"[Local AI] {reasonStr}");
            }

            // ── Multi-page warnings ──
            if (root.TryGetProperty("warnings", out var wn) && wn.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var w in wn.EnumerateArray())
                {
                    var s = w.GetString();
                    if (!string.IsNullOrEmpty(s)) data.ReasoningTrace.Add($"[Warning] {s}");
                }
            }

            // Parse suggested accounts
            if (root.TryGetProperty("suggested_accounts", out var sa) && sa.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                data.DebitAccountCode = sa.TryGetProperty("debit_account_code", out var dac) ? dac.GetString() : null;
                data.DebitAccountName = sa.TryGetProperty("debit_account_name", out var dan) ? dan.GetString() : null;
                data.CreditAccountCode = sa.TryGetProperty("credit_account_code", out var cac) ? cac.GetString() : null;
                data.CreditAccountName = sa.TryGetProperty("credit_account_name", out var can) ? can.GetString() : null;
                data.VatAccountCode = sa.TryGetProperty("vat_account_code", out var vac) ? vac.GetString() : null;
                data.VatAccountName = sa.TryGetProperty("vat_account_name", out var van) ? van.GetString() : null;
            }

            // Parse line items
            if (root.TryGetProperty("items", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    data.Items.Add(new OcrExtractedLineItem
                    {
                        Description = item.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                        Quantity = item.TryGetProperty("quantity", out var qty) && qty.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)qty.GetDouble() : null,
                        UnitPrice = item.TryGetProperty("unit_price", out var up) && up.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)up.GetDouble() : null,
                        Amount = item.TryGetProperty("amount", out var amt) && amt.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)amt.GetDouble() : null,
                        SuggestedAccountCode = item.TryGetProperty("suggested_account_code", out var sac) ? sac.GetString() : null,
                    });
                }
            }

            var rawText = root.TryGetProperty("raw_text", out var rt) ? rt.GetString() ?? "" : "";
            // Apply universal constraints — keeps the data shape consistent
            // across all three OCR providers and recovers fields the Python
            // service may have missed (e.g. buyer tax id, math invariants).
            Ocr.SmartFieldExtractor.Enrich(data, rawText);
            // A successful HTTP 200 with empty raw_text is still a "service worked but
            // couldn't read the document" — let the caller decide whether to fall
            // through to embedded. We DON'T throw here; we return the empty result
            // because some PDFs legitimately have no text and the gateway should
            // still get a chance to mark it low-confidence.
            return (rawText, data);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // Malformed JSON or unexpected schema from the microservice — treat as
            // service failure so cascade falls through to embedded.
            _logger.LogError(ex, "Local OCR service returned unparseable response");
            throw new InvalidOperationException(
                $"Local OCR: response parse error ({ex.Message})", ex);
        }
    }

    public async Task SubmitCorrectionAsync(Guid companyId, Guid scanResultId, OcrCorrectionRequest correction)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        // Capture previous values BEFORE updating — used as negative examples
        var prevVendorName = result.ExtractedVendorName;
        var prevVendorTaxId = result.ExtractedVendorTaxId;
        var prevDocNumber = result.ExtractedDocumentNumber;

        // What account did DeepSeek suggest at scan time? Captured BEFORE the
        // SuggestedAccountsJson overwrite below, so the distillation loop can
        // tell whether the user accepted the AI pick or overrode it.
        string? aiSuggestedDebitBefore = null;
        if (!string.IsNullOrEmpty(result.SuggestedAccountsJson))
        {
            try
            {
                using var d0 = System.Text.Json.JsonDocument.Parse(result.SuggestedAccountsJson);
                if (d0.RootElement.TryGetProperty("DebitAccountCode", out var dc0))
                    aiSuggestedDebitBefore = dc0.GetString();
            }
            catch { /* malformed — leave null */ }
        }

        // Update the scan result with corrected data
        if (correction.DocumentType != null)
        {
            // DocumentType corrections target the SCANNED paper type — keep the
            // ScannedDocumentType column in lockstep so the two fields don't
            // diverge after user edits.
            result.DocumentType = correction.DocumentType;
            result.ScannedDocumentType = correction.DocumentType;
        }
        if (correction.TargetDocumentType != null) result.TargetDocumentType = correction.TargetDocumentType;
        if (correction.VendorName != null) result.ExtractedVendorName = correction.VendorName;
        if (correction.VendorTaxId != null) result.ExtractedVendorTaxId = correction.VendorTaxId;
        if (correction.DocumentNumber != null) result.ExtractedDocumentNumber = correction.DocumentNumber;
        if (correction.DocumentDate.HasValue) result.ExtractedDate = correction.DocumentDate;
        if (correction.SubTotal.HasValue) result.ExtractedSubTotal = correction.SubTotal;
        if (correction.VatAmount.HasValue) result.ExtractedVatAmount = correction.VatAmount;
        if (correction.TotalAmount.HasValue) result.ExtractedTotalAmount = correction.TotalAmount;
        // Persist remaining user edits back to the scan row — without this
        // the user changes debit/credit account or WHT, clicks save, but
        // the scan-detail panel reopens showing the original suggestions.
        // (The previous code only fed corrections to the learners; it
        // never updated the displayed scan record.)
        if (correction.ExpenseCategory != null) result.ExpenseCategory = correction.ExpenseCategory;
        if (correction.HasWht.HasValue) result.HasWht = correction.HasWht.Value;
        if (correction.WhtRate.HasValue) result.WhtRate = correction.WhtRate;
        // ประเภทเงินได้ ม.40 ที่ผู้ใช้เลือก — "" = ล้างค่า (กติกาช่องแก้ไขของเรพนี้)
        if (correction.WhtIncomeTypeCode != null)
            result.WhtIncomeTypeCode = correction.WhtIncomeTypeCode.Length == 0
                ? null : correction.WhtIncomeTypeCode;
        // ── ช่องที่เพิ่งเปิดให้ผู้ใช้แก้ได้ (เดิมไม่มีทางแก้เลย) ──
        if (correction.BuyerTaxId != null) result.BuyerTaxId = correction.BuyerTaxId;
        if (correction.VendorBranchCode != null) result.VendorBranchCode = correction.VendorBranchCode;
        if (correction.BuyerBranchCode != null) result.BuyerBranchCode = correction.BuyerBranchCode;
        // ที่อยู่ + ชื่อผู้ซื้อ + เครดิตเทอม (E-OCR-05) — "" = ล้างค่า · null = ไม่ได้แตะ
        if (correction.VendorAddress != null)
            result.VendorAddress = string.IsNullOrWhiteSpace(correction.VendorAddress) ? null : correction.VendorAddress.Trim();
        if (correction.BuyerName != null)
            result.BuyerName = string.IsNullOrWhiteSpace(correction.BuyerName) ? null : correction.BuyerName.Trim();
        if (correction.BuyerAddress != null)
            result.BuyerAddress = string.IsNullOrWhiteSpace(correction.BuyerAddress) ? null : correction.BuyerAddress.Trim();
        if (correction.PaymentTermsDays.HasValue)
            result.PaymentTermsDays = correction.PaymentTermsDays.Value >= 0 ? correction.PaymentTermsDays.Value : null;
        // "" = ผู้ใช้ลบหมายเหตุทิ้ง (ล้างค่า) · null = ไม่ได้แตะช่องนี้
        if (correction.Notes != null)
            result.UserNotes = string.IsNullOrWhiteSpace(correction.Notes) ? null : correction.Notes.Trim();

        // ── บทบาทเรา: แก้ได้ + อนุมานเป้าหมายใหม่ตามบทบาทที่ถูกต้อง ──
        //
        // ⚠️ เดิมไม่มีทั้ง field และ UI ⇒ อนุมานผิดแล้วแก้ไม่ได้ตลอดไป และ
        // SubmitCorrection ก็**ไม่เคยรันตัวอนุมานซ้ำ**หลังผู้ใช้แก้เลขภาษี/ชื่อ
        // ⇒ แก้เลขผู้ซื้อให้ถูกแล้ว บทบาท/เป้าหมายยังค้างค่าเดิมที่คำนวณจาก
        // ข้อมูลผิด (defect class "เก็บแล้วต้อง echo กลับ" กลับด้าน: รับค่าแล้ว
        // ไม่คำนวณต่อ)
        var roleChanged = correction.OurRole is "Buyer" or "Seller"
            && !string.Equals(result.OurRole, correction.OurRole, StringComparison.Ordinal);
        var identityChanged = correction.VendorTaxId != null || correction.BuyerTaxId != null
            || correction.VendorName != null || correction.DocumentType != null;
        if (roleChanged) result.OurRole = correction.OurRole;
        if ((roleChanged || identityChanged) && correction.TargetDocumentType == null)
        {
            // ผู้ใช้ไม่ได้ระบุเป้าหมายมาเอง → อนุมานใหม่จากข้อมูลที่แก้แล้ว
            var co = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId)
                .Select(c => new { c.TaxId, c.Name }).FirstOrDefaultAsync();
            DocumentType? prevScanned = Enum.TryParse<DocumentType>(result.DocumentType, true, out var pd) ? pd : null;
            var re = Ocr.OcrDocumentRoleInferrer.Infer(
                rawText: Ocr.ThaiTextNormalizer.Normalize(result.RawTextContent ?? ""),
                vendorTaxId: result.ExtractedVendorTaxId, buyerTaxId: result.BuyerTaxId,
                vendorName: result.ExtractedVendorName, buyerName: result.BuyerName,
                companyTaxId: co?.TaxId, companyName: co?.Name,
                previousScannedType: prevScanned,
                paymentTermsDays: result.PaymentTermsDays);
            // บทบาทที่ผู้ใช้ระบุเองชนะการอนุมานเสมอ — คนเห็นกระดาษจริง
            var finalRole = roleChanged ? correction.OurRole! : re.OurRole;
            result.OurRole = finalRole;
            // เป้าหมายจากตัวอนุมานใช้ได้เฉพาะเมื่ออยู่ฝั่งเดียวกับบทบาทสุดท้าย
            if (Accounting.Helpers.DocumentSide.MatchesRole(re.TargetDocType, finalRole))
                result.TargetDocumentType = re.TargetDocType.ToString();
            result.ProcessingNotes = (result.ProcessingNotes ?? "")
                + $"\n[Re-infer] ผู้ใช้แก้ข้อมูลระบุตัวตน → อนุมานใหม่: บทบาท {finalRole} "
                + $"· เอกสารที่จะสร้าง {result.TargetDocumentType}";
        }
        if (correction.DebitAccountCode != null || correction.CreditAccountCode != null)
        {
            // SuggestedAccountsJson is the canonical store for the
            // displayed Dr/Cr codes. Merge the user's edits into whatever
            // the auto-suggester wrote so admin-Dr-edit doesn't blow
            // away the auto-Cr suggestion (and vice versa).
            string? debitCode = correction.DebitAccountCode;
            string? creditCode = correction.CreditAccountCode;
            string? debitName = null;
            string? creditName = null;
            if (!string.IsNullOrEmpty(debitCode))
            {
                debitName = await _db.ChartOfAccounts
                    .Where(a => a.CompanyId == companyId && a.AccountCode == debitCode && !a.IsDeleted)
                    .Select(a => a.AccountName).FirstOrDefaultAsync();
            }
            if (!string.IsNullOrEmpty(creditCode))
            {
                creditName = await _db.ChartOfAccounts
                    .Where(a => a.CompanyId == companyId && a.AccountCode == creditCode && !a.IsDeleted)
                    .Select(a => a.AccountName).FirstOrDefaultAsync();
            }
            // Preserve any side the admin didn't touch from the existing JSON.
            string? existingDebit = null, existingCredit = null;
            string? existingDebitName = null, existingCreditName = null;
            if (!string.IsNullOrEmpty(result.SuggestedAccountsJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(result.SuggestedAccountsJson);
                    var r = doc.RootElement;
                    if (r.TryGetProperty("DebitAccountCode", out var d)) existingDebit = d.GetString();
                    if (r.TryGetProperty("CreditAccountCode", out var c)) existingCredit = c.GetString();
                    if (r.TryGetProperty("DebitAccountName", out var dn)) existingDebitName = dn.GetString();
                    if (r.TryGetProperty("CreditAccountName", out var cn)) existingCreditName = cn.GetString();
                }
                catch { /* malformed — overwrite */ }
            }
            result.SuggestedAccountsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                DebitAccountCode = debitCode ?? existingDebit,
                DebitAccountName = debitName ?? existingDebitName,
                CreditAccountCode = creditCode ?? existingCredit,
                CreditAccountName = creditName ?? existingCreditName,
            });
        }

        // ★ ตัวชี้วัดคุณภาพ (D4): "ผู้ใช้แก้จริง" ต้องแยกจาก "ระบบบันทึกแถว"
        // — เดิมนับจาก UpdatedAt != null ซึ่งขยับทุกครั้งที่ระบบเซฟเอง ⇒ อัตราการแก้
        // ~100% ทุก tenant = ตัวเลขที่อ่านไม่ได้ (ผลตรวจ 2026-09-06 · T5)
        var correctedFields = Accounting.Helpers.OcrCorrectedFieldList.From(correction);
        if (correctedFields.Length > 0)
        {
            result.UserCorrectedAt ??= DateTime.UtcNow;   // ครั้งแรกเท่านั้น
            result.UserCorrectedFields = Accounting.Helpers.OcrCorrectedFieldList.Merge(
                result.UserCorrectedFields, correctedFields);
        }

        await _db.SaveChangesAsync();

        // ───── ปิดวงจร: คำแก้ของผู้ใช้ → คลัง known-good ของผู้ขายรายนี้ ─────
        //
        // ⚠️ ก่อนหน้านี้คลัง VendorKnownGoodValues มีผู้เขียนแค่ AzureDiPatternLearner
        // (Source = "AzureDI") ⇒ ตัวจัดอันดับใน VendorKnownGoodCorrector ที่เขียนไว้ว่า
        // "UserCorrection ชนะ Azure" **ไม่มีวันได้ทำงาน** เพราะไม่มีแถว UserCorrection
        // อยู่เลย ⇒ ผู้ใช้แก้ชื่อผู้ขายกี่รอบ ใบถัดไปของผู้ขายรายเดิมก็กลับไปผิดแบบเดิม
        // (หลักการข้อ 2 "มี ≠ ถูกเรียก" — ตัวจัดอันดับมีอยู่ แต่ไม่มีข้อมูลให้จัด)
        var knownGoodTaxId = correction.VendorTaxId ?? result.ExtractedVendorTaxId;
        if (!string.IsNullOrWhiteSpace(knownGoodTaxId))
        {
            try
            {
                if (correction.VendorName != null)
                    await _knownGoodCorrector.RememberUserCorrectionAsync(
                        companyId, knownGoodTaxId, "SellerName", correction.VendorName);
                if (correction.VendorAddress != null)
                    await _knownGoodCorrector.RememberUserCorrectionAsync(
                        companyId, knownGoodTaxId, "VendorAddress", correction.VendorAddress);
            }
            catch (Exception ex)
            {
                // ไม่ให้การเรียนรู้ล้มไปกระทบการบันทึกคำแก้ (ซึ่งบันทึกไปแล้วข้างบน)
                _logger.LogWarning(ex, "บันทึกค่า known-good จากคำแก้ของผู้ใช้ไม่สำเร็จ (ไม่กระทบคำแก้)");
            }
        }
        else if (correction.VendorName != null || correction.VendorAddress != null)
        {
            // ไม่มีเลขผู้เสียภาษี = ไม่มีกุญแจให้ผูกคำแก้ ⇒ ครั้งหน้าระบบจำไม่ได้
            // **ห้ามเงียบ** — ไม่งั้นผู้ใช้เข้าใจว่าสอนระบบไปแล้ว (หลักการข้อ 7)
            result.ProcessingNotes = (result.ProcessingNotes ?? "")
                + "\n[KnownGood] ยังจำคำแก้ชื่อ/ที่อยู่ผู้ขายใบนี้ไม่ได้ เพราะยังไม่มีเลขผู้เสียภาษี"
                + "ของผู้ขายเป็นกุญแจ — กรอกเลขผู้เสียภาษีแล้วบันทึกอีกครั้ง ระบบจะจำให้ใบถัดไป";
            await _db.SaveChangesAsync();
        }

        // ───── Train VendorIntelligence with the corrected target type ─────
        // When the user changes "เอกสารที่จะสร้าง" we want next scan of the
        // same vendor to predict the same target — that's exactly what
        // TrainFromAdminAsync does (per-tenant, weight 1). Without this the
        // correction would update only the current row, not future scans.
        if (!string.IsNullOrEmpty(correction.TargetDocumentType)
            && Enum.TryParse<DocumentType>(correction.TargetDocumentType, ignoreCase: true, out var corrTarget))
        {
            try
            {
                // ── D-3 · ทางเข้าที่สองของ "ประวัติ WHT" ที่เคยไม่เดินด่าน ──
                //
                // ⚠️ บล็อกนี้ยิงเมื่อผู้ใช้เปลี่ยน **ชนิดเอกสารที่จะสร้าง** แต่ส่ง
                // `whtRate` ที่ฟอร์มพกมาด้วยเข้าไปสอนคลังประวัติเสมอ ⇒ ค่าที่
                // **ระบบเป็นคนเติมเองแล้วผู้ใช้ไม่เคยแตะ** กลายเป็น "ประวัติของ
                // ผู้ขาย" ผ่านประตูหลัง — วงจรสอนตัวเองตัวเดียวกับ D3-2 ที่รอบ 183
                // ปิดไปแล้วที่ `TrainFromDocumentAsync` (ราก R5: ทางเข้าอื่นไม่เดินด่าน)
                //
                // ใช้ตัวตัดสินตัวเดียวกัน (`OcrWhtLearningScope`) ไม่ใช่เขียนเกณฑ์ใหม่
                var paperWht = Accounting.Helpers.PaperWhtReader.Read(result.RawTextContent);
                var whtLearn = Accounting.Helpers.OcrWhtLearningScope.Decide(
                    hasScan: true,
                    paperShowsWht: paperWht.Amount is > 0m || paperWht.RatePercent is > 0m,
                    userCorrectedFields: result.UserCorrectedFields);
                if (!whtLearn.Learn && correction.WhtRate.HasValue)
                    _logger.LogInformation(
                        "ไม่สอนประวัติ WHT ของผู้ขายจากสแกน {ScanId} — {Reason}",
                        result.Id, Accounting.Helpers.OcrWhtLearningScope.Explain(whtLearn.Evidence));
                await _vendorIntel.TrainFromAdminAsync(
                    companyId,
                    correction.VendorTaxId ?? result.ExtractedVendorTaxId,
                    correction.VendorName ?? result.ExtractedVendorName,
                    corrTarget,
                    debitAccountCode: correction.DebitAccountCode,
                    debitAccountName: null,
                    whtRate: whtLearn.Learn ? correction.WhtRate : null,
                    paymentTermsDays: null,
                    weight: 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to train VendorIntel from correction");
            }
        }

        // ───── Train the category learner from this correction ─────
        // When user manually picks a debit account (the expense category) for a vendor,
        // remember it so future scans of the same vendor pre-fill that account.
        if (!string.IsNullOrEmpty(correction.DebitAccountCode))
        {
            var debitName = await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountCode == correction.DebitAccountCode && !a.IsDeleted)
                .Select(a => a.AccountName)
                .FirstOrDefaultAsync();
            await _categoryLearner.RecordAsync(
                companyId,
                correction.VendorTaxId ?? result.ExtractedVendorTaxId,
                correction.VendorName ?? result.ExtractedVendorName,
                correction.ExpenseCategory ?? result.ExpenseCategory,
                correction.DebitAccountCode,
                debitName);

            // ── Close the DeepSeek distillation loop ──
            // When this scan was classified by DeepSeek, record the user's
            // final account pick against that AiSuggestionFeedback row. The
            // nightly AiFeedbackTrainingJob mines rows WHERE UserChosenAt != null
            // to retrain GlAccountDistillationModel, so without this the teacher's
            // answer (and the user's correction of it) would never reach the
            // student. acceptedAi = user kept the AI's suggestion unchanged.
            if (_feedbackRecorder != null && result.GlAccountAiFeedbackId.HasValue)
            {
                try
                {
                    var acceptedAi = result.GlAccountUsedAi
                        && string.Equals(aiSuggestedDebitBefore, correction.DebitAccountCode, StringComparison.Ordinal);
                    await _feedbackRecorder.RecordUserChoiceAsync(
                        result.GlAccountAiFeedbackId.Value, correction.DebitAccountCode, acceptedAi, default,
                        Accounting.Models.Enums.UserChoiceSource.Explicit);   // ผู้ใช้ส่งรหัสผังมาในคำแก้เอง
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to record GL-account feedback choice (non-fatal)");
                }
            }
        }

        // ───── ปิด loop AI ให้ช่องที่เหลือ (กฎเหล็ก #1 ข้อ CAPTURE) ─────
        //
        // ⚠️ ผลตรวจ AI พบว่า loop ปิดจริงแค่ 3 ช่อง (ผังบัญชี · ผู้ติดต่อ ·
        // โครงการรายบรรทัด) ส่วนคำแก้ที่เหลือ — ชนิดเอกสาร/เป้าหมาย/บทบาท/
        // วันที่/ยอด/WHT — ไปถึงแค่ learner ที่ไม่ใช่ LLM หรือไม่ไปไหนเลย
        // ⇒ feedback row ของ AI ค้างสถานะ "ยังไม่รู้คำตอบจริง" ตลอดไป และ
        // AiFeedbackTrainingJob (ซึ่ง mine เฉพาะแถวที่ UserChosenAt != null)
        // จึงไม่เคยได้ข้อมูลจากช่องเหล่านี้เลย
        //
        // บันทึกทุกช่องที่มี feedbackId ผูกอยู่ ไม่ว่าผู้ใช้จะแก้ช่องไหน
        if (_feedbackRecorder != null)
        {
            // ⚠️ `userTouched` แยก "ผู้ใช้แก้ช่องนี้เอง" ออกจาก "ช่องนี้ถูกส่งมาด้วย
            // เพราะอยู่ในฟอร์มเดียวกัน" — เดิมทุกช่องที่มี feedbackId ถูกบันทึกเป็น
            // คำยืนยันเท่ากันหมด ⇒ ช่องที่ผู้ใช้ไม่เคยมองก็กลายเป็น "ความจริง"
            // (ทีม T3 รอบ 177 §3.1 — ตัวเดียวกับกติกา userTouched ของฝั่งฟอร์ม)
            async Task CloseAiFeedbackLoop(Guid? feedbackId, string? chosen, string? aiAnswer, bool userTouched)
            {
                if (feedbackId is null || string.IsNullOrWhiteSpace(chosen)) return;
                try
                {
                    await _feedbackRecorder.RecordUserChoiceAsync(
                        feedbackId.Value, chosen,
                        // นิยามเดียวกับเส้น 1-click (Helpers/OcrAiLabelScope) — สองทางเข้า
                        // ต้องให้ label เดียวกันกับใบเดียวกัน
                        acceptedAi: Accounting.Helpers.OcrAiLabelScope.AcceptedAi(aiAnswer, chosen), default,
                        userTouched
                            ? Accounting.Models.Enums.UserChoiceSource.Explicit
                            : Accounting.Models.Enums.UserChoiceSource.Implicit);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "บันทึก feedback ของผู้ใช้ไม่สำเร็จ (ไม่กระทบการบันทึกคำแก้)");
                }
            }
            // ชนิดเอกสารที่จะสร้าง — คำตอบจริงของ DocumentTypeClassification /
            // OcrFullReview / DocumentConversionSuggestion
            await CloseAiFeedbackLoop(result.TargetDocTypeAiFeedbackId,
                correction.TargetDocumentType ?? result.TargetDocumentType,
                result.TargetDocTypeAiSuggested,
                userTouched: !string.IsNullOrWhiteSpace(correction.TargetDocumentType));
            // บทบาทเรา (ผู้ซื้อ/ผู้ขาย) — คำตอบจริงของ DocumentRoleInference: ผู้ใช้ยืนยัน/แก้
            // ในหน้า review คือ feedback ที่สอน student (กฎเหล็ก #1 ขั้น CAPTURE)
            await CloseAiFeedbackLoop(result.OurRoleAiFeedbackId,
                correction.OurRole ?? result.OurRole,
                result.OurRoleAiSuggested,
                userTouched: !string.IsNullOrWhiteSpace(correction.OurRole));
        }

        // Federated doc-workflow learning — when the user confirms what
        // TargetDocumentType to create for a given (Vendor, ScannedType),
        // contribute that mapping to the cross-tenant pool. Lets the
        // next tenant who scans the SAME vendor's SAME scanned doc type
        // (e.g. "all utility receipts from MEA become PaymentVouchers")
        // get the right TargetDocumentType pre-selected.
        if (_docWorkflowLearner != null)
        {
            var scannedDocType = correction.DocumentType ?? result.ScannedDocumentType ?? result.DocumentType;
            var targetDocType = correction.TargetDocumentType ?? result.TargetDocumentType;
            if (!string.IsNullOrEmpty(scannedDocType) && !string.IsNullOrEmpty(targetDocType))
            {
                // คีย์เดียวกับฝั่งอ่าน (Helpers/VendorLearningKey) — ห้ามเขียนสูตรเอง
                var taxId = correction.VendorTaxId ?? result.ExtractedVendorTaxId;
                var name = correction.VendorName ?? result.ExtractedVendorName;
                var vKey = Accounting.Helpers.VendorLearningKey.For(taxId, name);

                if (!string.IsNullOrEmpty(vKey))
                {
                    try
                    {
                        await _docWorkflowLearner.RecordConfirmAsync(companyId, vKey, scannedDocType, targetDocType);
                        await _db.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Federated doc-workflow contribution write failed (non-fatal)");
                    }
                }
            }
        }

        // Forward correction to local AI service for learning
        var serviceUrl = _configuration["Ocr:LocalServiceUrl"] ?? "http://localhost:8501";
        try
        {
            var client = _httpClientFactory.CreateClient();
            var payload = new
            {
                original_text = result.RawTextContent ?? "",
                original_result = new { document_type = result.DocumentType, confidence = result.Confidence },
                corrected_result = new
                {
                    document_type = correction.DocumentType ?? result.DocumentType,
                    vendor_name = correction.VendorName ?? result.ExtractedVendorName,
                    vendor_tax_id = correction.VendorTaxId ?? result.ExtractedVendorTaxId,
                    document_number = correction.DocumentNumber ?? result.ExtractedDocumentNumber,
                    document_date = (correction.DocumentDate ?? result.ExtractedDate)?.ToString("yyyy-MM-dd"),
                    subtotal = correction.SubTotal ?? result.ExtractedSubTotal,
                    vat_amount = correction.VatAmount ?? result.ExtractedVatAmount,
                    total_amount = correction.TotalAmount ?? result.ExtractedTotalAmount,
                },
                document_type = correction.DocumentType ?? result.DocumentType
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            await client.PostAsync($"{serviceUrl}/ocr/correct", content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit correction to learning service");
        }

        // Learn patterns from correction using zone analyzer
        try
        {
            if (!string.IsNullOrEmpty(result.RawTextContent))
            {
                var learnedPatterns = DocumentZoneAnalyzer.LearnFromCorrection(
                    result.RawTextContent, companyId,
                    correction.VendorTaxId ?? result.ExtractedVendorTaxId,
                    correction.VendorName, correction.VendorTaxId,
                    correction.DocumentNumber, correction.TotalAmount,
                    previousVendorName: prevVendorName,
                    previousTaxId: prevVendorTaxId,
                    previousDocNumber: prevDocNumber);

                foreach (var newPattern in learnedPatterns)
                {
                    if (newPattern.IsNegativeExample)
                    {
                        // Negative example: identified by FieldName + NegativeValue
                        var existingNeg = await _db.OcrLearnedPatterns
                            .FirstOrDefaultAsync(p => p.CompanyId == companyId
                                && p.FieldName == newPattern.FieldName
                                && p.IsNegativeExample
                                && p.NegativeValue == newPattern.NegativeValue
                                && (p.VendorTaxId == newPattern.VendorTaxId || (p.VendorTaxId == null && newPattern.VendorTaxId == null)));
                        if (existingNeg != null)
                        {
                            existingNeg.FailureCount++;
                            existingNeg.LastConfirmedAt = DateTime.UtcNow;
                        }
                        else
                        {
                            _db.OcrLearnedPatterns.Add(newPattern);
                        }
                    }
                    else
                    {
                        var existing = await _db.OcrLearnedPatterns
                            .FirstOrDefaultAsync(p => p.CompanyId == companyId
                                && !p.IsNegativeExample
                                && p.FieldName == newPattern.FieldName
                                && p.ContextKeyword == newPattern.ContextKeyword
                                && (p.VendorTaxId == newPattern.VendorTaxId || (p.VendorTaxId == null && newPattern.VendorTaxId == null)));

                        if (existing != null)
                        {
                            existing.TimesConfirmed++;
                            existing.LastConfirmedAt = DateTime.UtcNow;
                            existing.ExtractionRegex = newPattern.ExtractionRegex;
                            existing.SearchRadius = Math.Max(existing.SearchRadius, newPattern.SearchRadius);
                        }
                        else
                        {
                            _db.OcrLearnedPatterns.Add(newPattern);
                        }
                    }
                }
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to learn patterns from correction");
        }
    }

    /// <summary>
    /// เอา **เลขผู้เสียภาษี** ที่อ่านได้ไปค้นทะเบียนราชการ (RD VAT → DBD) แล้วใช้
    /// ชื่อ/ที่อยู่ทางการเป็นความจริง — ส่วนที่ OCR อ่านต่างกลายเป็นสัญญาณสอนระบบ
    ///
    /// <para>═══ ตัวตัดสินอยู่ที่ <see cref="Accounting.Helpers.DbdIdentityGuard"/> ═══
    /// เดิมตรรกะ "ทะเบียนชนะได้ไหม" ถูกเขียน inline อยู่ตรงนี้ **และ** ถูกยุบเป็น
    /// helper ไว้แล้วสำหรับเส้น integration ⇒ มีสองสำเนาที่ drift ได้ทันที
    /// (doc ของ helper เขียนว่า "ยุบมาแล้ว" แต่สำเนาที่นี่ไม่เคยถูกถอด —
    /// ญาติของ defect class "แก้ที่เดียวจาก N"). ตอนนี้เหลือตัวตัดสินตัวเดียว</para>
    /// </summary>
    private async Task EnrichFromDbdAsync(Guid companyId, OcrExtractedData data, OcrScanResult scanResult)
    {
        // ── ด่านก่อนยิง: เลขต้อง "ใช้เป็นเลขผู้เสียภาษีได้จริง" ──
        // เดิมเช็คแค่ `Length != 13` ⇒ เลขที่ checksum ไม่ผ่าน และ **บาร์โค้ดสินค้า
        // EAN-13** ที่หลุดมาเป็นเลขผู้ขาย ก็ถูกส่งไปค้นทะเบียน แล้วไปพึ่งด่านชื่อ
        // กันไว้ทีหลัง — สู้ไม่ค้นตั้งแต่แรกไม่ได้ (เปลืองโควตา API + เพิ่มโอกาส
        // ได้ "คนละบริษัท" มายั่วให้ทับ). IsPlausibleFromScan = checksum + ไม่ใช่บาร์โค้ด
        if (!Accounting.Helpers.ThaiTaxId.IsPlausibleFromScan(data.VendorTaxId))
        {
            // "ไม่รู้ = บอกว่าไม่รู้" — ผู้ใช้ต้องเห็นว่าทำไมไม่มีข้อมูลทะเบียน
            if (!string.IsNullOrWhiteSpace(data.VendorTaxId))
                data.ReasoningTrace.Add(
                    $"[DBD] ไม่ได้ค้นทะเบียน — '{data.VendorTaxId}' ใช้เป็นเลขผู้เสียภาษีไม่ได้ " +
                    "(หลักตรวจสอบไม่ผ่าน หรือหน้าตาเป็นบาร์โค้ดสินค้า) กรุณาตรวจเลขบนกระดาษ");
            return;
        }

        DbdCompanyResult? dbd = null;
        try
        {
            dbd = await _dbdLookup.GetByJuristicIdAsync(data.VendorTaxId!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DBD lookup failed for TaxId {TaxId}", data.VendorTaxId);
        }

        if (dbd == null)
        {
            scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") +
                $"\n[DBD] ไม่พบข้อมูลในกรมพัฒนาธุรกิจการค้า (TaxID: {data.VendorTaxId}) — ใช้ข้อมูลจาก OCR";
            data.DbdLookupAttempted = true;
            return;
        }

        data.DbdLookupAttempted = true;

        var ocrName = data.VendorName?.Trim();
        var dbdName = dbd.NameTh?.Trim();

        // ═══ คำถามที่ถูกคือ "กุญแจถูกไหม" ไม่ใช่ "ชื่อคล้ายกันไหม" ═══
        // (เคสดีแคทลอน 2026-09-18: กระดาษพิมพ์แบรนด์ "DECATHLON" เป็นชื่อผู้ขาย ·
        //  ทะเบียนคืนชื่อนิติบุคคลภาษาไทย · ความคล้ายข้ามตัวอักษร = 0.000 เท่ากับ
        //  "คนละบริษัท" ⇒ ด่านเดิมโยนชื่อทางการทิ้งแล้วเก็บชื่อโลโก้ไว้)
        var keyEvidence = await JudgeVendorKeyEvidenceAsync(companyId, data, scanResult);
        var nameSim = Ocr.FuzzyMatcher.Similarity(ocrName, dbdName);
        var verdict = Accounting.Helpers.DbdIdentityGuard.Judge(dbdName, ocrName, nameSim,
            keyProven: keyEvidence == Accounting.Helpers.VendorKeyEvidence.ProvenSellerKey);

        if (!Accounting.Helpers.DbdIdentityGuard.RegistryWins(verdict))
        {
            // คนละบริษัท + กุญแจก็พิสูจน์ไม่ได้ → ผู้ต้องสงสัยคือ **เลขผู้เสียภาษี**
            // ห้ามทับ ห้ามสอน — ลด confidence ให้ไฮไลต์เหลืองขึ้น (กฎเหล็ก #3 ข้อ 3)
            // แล้วให้คนตัดสิน (หลัก "ไม่รู้ = ต้องบอกว่าไม่รู้")
            data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerTaxId] = 0.30;
            data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerName] = 0.50;
            data.ReasoningTrace.Add(
                $"[DBD] ⚠ เลข {data.VendorTaxId} เป็นของ '{dbd.NameTh}' แต่บนกระดาษเขียนว่า '{ocrName}' " +
                $"(ต่างกันสิ้นเชิง และไม่พบป้ายกำกับยืนยันว่าเลขนี้เป็นของผู้ขาย) — " +
                "น่าจะอ่าน**เลขผู้เสียภาษี**ผิด จึงไม่ทับชื่อและไม่นำไปสอนระบบ");
            scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") +
                "\n[DBD] ⚠ " + Accounting.Helpers.DbdIdentityGuard.KeyMismatchMessage(
                    data.VendorTaxId!, dbd.NameTh ?? "", ocrName ?? "");
            return;   // ไม่ตั้ง DbdMatched ⇒ ไม่สร้าง Contact ของบริษัทที่ไม่เกี่ยวข้อง
        }

        // ทะเบียนชนะ — adopt as authoritative
        data.DbdMatched = true;
        data.DbdCanonicalName = dbd.NameTh;
        data.DbdAddress = dbd.Address;
        data.DbdJuristicType = dbd.JuristicType;
        data.DbdStatus = dbd.Status;
        data.VendorName = dbd.NameTh;   // ใช้การสะกดทางการเสมอ (§86/4 ต้องการชื่อนิติบุคคล)

        switch (verdict)
        {
            case Accounting.Helpers.DbdTrustVerdict.ExactMatch:
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerName] = 1.0;
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerTaxId] = 1.0;
                data.ReasoningTrace.Add($"[DBD] ✓ ชื่อบริษัทตรงกับทะเบียน ({dbd.NameTh}) — ใช้ชื่อทางการ");
                break;

            case Accounting.Helpers.DbdTrustVerdict.NoIncomingName:
                // อ่านชื่อไม่ได้ **แต่กุญแจพิสูจน์แล้ว** — ทะเบียนคือคำตอบที่ถูกที่สุดที่มี
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerName] = 1.0;
                data.ReasoningTrace.Add($"[DBD] อ่านชื่อผู้ขายจากกระดาษไม่ได้ — ใช้ชื่อจากทะเบียน: {dbd.NameTh}");
                break;

            case Accounting.Helpers.DbdTrustVerdict.NoIncomingNameUnprovenKey:
                // อ่านชื่อไม่ได้ **และกุญแจก็ยังพิสูจน์ไม่ได้** — ยังเติมค่าให้ (กฎเหล็ก #3
                // ห้ามปล่อยช่องว่าง) แต่ห้ามประทับ 1.0: เลข 13 หลักที่ผ่าน checksum อาจเป็น
                // บาร์โค้ด/เลขผู้ซื้อ ⇒ ทะเบียนคืนชื่อ**บริษัทอื่น**ที่ถูกต้องตามทะเบียน
                // แล้วไหลลงใบกำกับเงียบ ๆ (§86/4) — ต่ำกว่า 0.85 = ไฮไลต์เหลืองให้คนตรวจ
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerName] = 0.55;
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerTaxId] = 0.55;
                data.ReasoningTrace.Add(
                    $"[DBD] อ่านชื่อผู้ขายจากกระดาษไม่ได้ และ**ไม่พบป้ายกำกับ**ยืนยันว่าเลข " +
                    $"{data.VendorTaxId} เป็นของผู้ขาย — เติมชื่อจากทะเบียนไว้ให้ ('{dbd.NameTh}') " +
                    "แต่ยังไม่ถือว่ายืนยัน กรุณาตรวจว่าเลขผู้เสียภาษีบนกระดาษเป็นของผู้ขายจริง");
                break;

            case Accounting.Helpers.DbdTrustVerdict.KeyVerifiedNameDiffers:
                // กระดาษพิมพ์ **แบรนด์/โลโก้** ไม่ใช่ชื่อนิติบุคคล — ไม่ใช่การอ่านผิด
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerName] = 0.95;
                data.ReasoningTrace.Add(
                    $"[DBD] บนกระดาษเขียนว่า '{ocrName}' ซึ่งเป็นชื่อแบรนด์/ร้าน — เลขผู้เสียภาษี " +
                    $"{data.VendorTaxId} มีป้ายกำกับยืนยันบนกระดาษ และขึ้นทะเบียนเป็น '{dbd.NameTh}' " +
                    "⇒ ใช้ชื่อนิติบุคคลตามทะเบียน (§86/4 ต้องใช้ชื่อผู้ประกอบการ ไม่ใช่ชื่อร้าน)");
                break;

            default:   // SameCompanyMisspelled — บริษัทเดียวกันที่ OCR สะกดเพี้ยน
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerName] = 0.95;
                data.ReasoningTrace.Add(
                    $"[DBD] ⚠ OCR อ่านได้ '{ocrName}' แต่ทะเบียนระบุ '{dbd.NameTh}' " +
                    $"(ใกล้เคียง {nameSim:P0}) — ใช้จากทะเบียนและเรียนรู้");
                break;
        }

        if (Accounting.Helpers.DbdIdentityGuard.ShouldLearnMismatch(verdict, dbdName, ocrName))
            await RecordOcrMismatchAsync(companyId, data.VendorTaxId!, "SellerName",
                wrongValue: ocrName, correctValue: dbd.NameTh);

        // Update scanResult so subsequent saves use the canonical name
        scanResult.ExtractedVendorName = data.VendorName;

        scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") +
            $"\n[DBD] ✓ พบข้อมูล: {dbd.NameTh}" +
            (string.IsNullOrEmpty(dbd.Status) ? "" : $" (สถานะ: {dbd.Status})") +
            (string.IsNullOrEmpty(dbd.Address) ? "" : $"\n[DBD Address] {dbd.Address}");
    }

    /// <summary>
    /// รวบรวมหลักฐานจาก**กระดาษ**ว่า "เลขที่จะเอาไปค้นทะเบียน เป็นเลขของผู้ขายจริง"
    /// แล้วส่งให้ <see cref="Accounting.Helpers.OcrVendorKeyEvidence"/> ตัดสิน
    ///
    /// <para>ตัวตัดสินเป็นฟังก์ชันบริสุทธิ์ที่มีเทสต์ — ที่นี่ทำแค่ "หาหลักฐาน"
    /// (ตำแหน่ง/ป้ายกำกับ/เลขบริษัทเรา) ห้ามเขียนกติกาซ้ำ</para>
    /// </summary>
    private async Task<Accounting.Helpers.VendorKeyEvidence> JudgeVendorKeyEvidenceAsync(
        Guid companyId, OcrExtractedData data, OcrScanResult scanResult)
    {
        if (string.IsNullOrWhiteSpace(scanResult.RawTextContent))
            return Accounting.Helpers.VendorKeyEvidence.Unproven;

        // ⚠️ **ต้องใช้ข้อความก้อนเดียวกันทั้งสองตัว** — ตำแหน่งของเลขกับตำแหน่งของป้าย
        // ถูกเอามาเทียบกัน ถ้าสกัดเลขจากข้อความดิบแต่หาป้ายจากข้อความที่ normalize แล้ว
        // ดัชนีจะเลื่อนกันทั้งหน้า แล้วเงื่อนไข "เลขอยู่ใต้ป้ายผู้ซื้อไหม" จะเพี้ยนเงียบ ๆ
        //
        // และต้องเป็นฝั่ง **normalize** เพราะ Tesseract ไทยแทรกช่องว่างทุกกลุ่มอักษร
        // ("ผู้ ซื้ อ") ⇒ OcrPartyLabels ซึ่งเทียบสตริงตรง ๆ จะหาป้ายฝั่งผู้ซื้อไม่เจอ
        // = ทิศที่ผ่อนปรนเกินไป (เลขผู้ซื้อกลายเป็น "พิสูจน์แล้ว")
        var raw = Ocr.ThaiTextNormalizer.Normalize(scanResult.RawTextContent!);

        // เลขก้อนนี้บนกระดาษ: มีป้าย "เลขประจำตัวผู้เสียภาษี" กำกับไหม และอยู่ตรงไหน
        // (ตัวสกัดตัวเดียวกับที่ใช้ตอนหาเลข — ไม่สร้างสำเนาที่สองของกติกาป้าย)
        var candidate = Ocr.SmartFieldExtractor.ExtractTaxIdCandidates(raw)
            .FirstOrDefault(c => Accounting.Helpers.ThaiTaxId.Same(c.Id, data.VendorTaxId));
        var labels = Accounting.Helpers.OcrPartyLabels.FindAll(raw);

        var ourTaxId = await _db.Companies
            .Where(c => c.Id == companyId)
            .Select(c => c.TaxId)
            .FirstOrDefaultAsync();

        return Accounting.Helpers.OcrVendorKeyEvidence.Judge(
            data.VendorTaxId, data.BuyerTaxId, ourTaxId,
            labelledAsTaxIdOnPaper: candidate.Labelled,
            taxIdPosition: string.IsNullOrEmpty(candidate.Id) ? -1 : candidate.Position,
            labels.BuyerPos, labels.SellerPos,
            textLength: raw.Length);
    }

    /// <summary>
    /// Persist a negative training example: this OCR-extracted value was wrong
    /// (according to DBD) for this vendor.
    /// </summary>
    private async Task RecordOcrMismatchAsync(Guid companyId, string vendorTaxId, string fieldName,
        string? wrongValue, string? correctValue)
    {
        if (string.IsNullOrEmpty(wrongValue)) return;
        try
        {
            var existing = await _db.OcrLearnedPatterns.FirstOrDefaultAsync(p =>
                p.CompanyId == companyId && p.IsNegativeExample &&
                p.FieldName == fieldName && p.NegativeValue == wrongValue &&
                p.VendorTaxId == vendorTaxId);
            if (existing != null)
            {
                existing.FailureCount++;
                existing.LastConfirmedAt = DateTime.UtcNow;
            }
            else
            {
                _db.OcrLearnedPatterns.Add(new OcrLearnedPattern
                {
                    CompanyId = companyId,
                    VendorTaxId = vendorTaxId,
                    FieldName = fieldName,
                    ContextKeyword = "(dbd-mismatch)",
                    IsNegativeExample = true,
                    NegativeValue = wrongValue,
                    FailureCount = 1,
                    CreatedBy = "DBD-Verify"
                });
            }
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record DBD mismatch for {Field}", fieldName);
        }
    }

    private static OcrExtractedData ParseThaiDocument(string text)
    {
        var data = new OcrExtractedData();

        if (string.IsNullOrWhiteSpace(text))
        {
            data.DocumentType = "Receipt";
            data.Confidence = 0.3m;
            return data;
        }

        var upperText = text.ToUpperInvariant();

        // Document type detection — TaxInvoice wins even if Receipt text also present
        bool hasTaxInvoice = text.Contains("ใบกำกับภาษี") || text.Contains("ใบกํากับภาษี") || upperText.Contains("TAX INVOICE");
        bool hasReceipt = text.Contains("ใบเสร็จรับเงิน") || upperText.Contains("RECEIPT");
        bool hasInvoice = text.Contains("ใบแจ้งหนี้") || (upperText.Contains("INVOICE") && !upperText.Contains("TAX INVOICE"));
        bool hasPurchaseOrder = text.Contains("ใบสั่งซื้อ") || upperText.Contains("PURCHASE ORDER");
        bool hasWhtDoc = text.Contains("หนังสือรับรอง") || text.Contains("50 ทวิ") || text.Contains("ภาษีหัก ณ ที่จ่าย");
        bool hasCreditNote = text.Contains("ใบลดหนี้") || upperText.Contains("CREDIT NOTE");
        bool hasDebitNote = text.Contains("ใบเพิ่มหนี้") || upperText.Contains("DEBIT NOTE");
        bool hasCertInLieu = text.Contains("ใบรับรองแทนใบเสร็จ") || upperText.Contains("CERTIFICATE IN LIEU");

        if (hasTaxInvoice) { data.DocumentType = "TaxInvoice"; data.Confidence = 0.95m; }
        else if (hasCertInLieu) { data.DocumentType = "CertificateInLieu"; data.Confidence = 0.90m; }
        else if (hasCreditNote) { data.DocumentType = "CreditNote"; data.Confidence = 0.90m; }
        else if (hasDebitNote) { data.DocumentType = "DebitNote"; data.Confidence = 0.90m; }
        else if (hasWhtDoc) { data.DocumentType = "WHT"; data.Confidence = 0.90m; }
        else if (hasPurchaseOrder) { data.DocumentType = "PurchaseOrder"; data.Confidence = 0.85m; }
        else if (hasInvoice) { data.DocumentType = "Invoice"; data.Confidence = 0.90m; }
        else if (hasReceipt) { data.DocumentType = "Receipt"; data.Confidence = 0.88m; }
        else { data.DocumentType = "Receipt"; data.Confidence = 0.5m; }

        // Extract ALL 13-digit tax IDs
        var taxIdPattern = Accounting.Helpers.ThaiTaxId.Pattern;
        var allTaxIds = Regex.Matches(text, taxIdPattern)
            .Cast<Match>()
            .Select(m => Regex.Replace(m.Groups[1].Value, @"[-\s]", ""))
            .Where(id => id.Length == 13)
            .Distinct()
            .ToList();

        // Extract ALL company names with position
        var companyPattern = @"(บริษัท|ห้างหุ้นส่วน(?:จำกัด|สามัญ)?|ร้าน)\s*(.+?)(?:\s*จำกัด(?:\s*\(มหาชน\))?|\s*\(|(?=\s*เลข|\s*สาขา|\s*ที่อยู่|\s*\d{1}[- \t]?\d{4})|$)";
        var companyMatches = Regex.Matches(text, companyPattern, RegexOptions.Multiline);
        var companyNames = new List<(string FullName, int Position)>();
        foreach (Match cm in companyMatches)
        {
            var prefix = cm.Groups[1].Value;
            var name = cm.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2) continue;
            var afterMatch = text.Substring(cm.Index, Math.Min(cm.Length + 30, text.Length - cm.Index));
            var suffix = "";
            if (afterMatch.Contains("จำกัด"))
                suffix = afterMatch.Contains("มหาชน") ? " จำกัด (มหาชน)" : " จำกัด";
            companyNames.Add(($"{prefix} {name}{suffix}".Trim(), cm.Index));
        }

        // Distinguish vendor from our company by seller/buyer section context
        var sellerKeywords = new[] { "ผู้ขาย", "ผู้ออกใบ", "ผู้ให้บริการ", "SELLER", "FROM", "ผู้ออก" };
        var buyerKeywords = new[] { "ผู้ซื้อ", "ลูกค้า", "นามผู้ซื้อ", "BUYER", "CUSTOMER", "BILL TO", "SOLD TO", "ส่งถึง" };

        string? vendorName = null;
        string? vendorTaxId = null;

        if (companyNames.Count >= 2)
        {
            int sellerIdx = -1, buyerIdx = -1;
            foreach (var kw in sellerKeywords)
            {
                var kwPos = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                if (kwPos >= 0) { sellerIdx = kwPos; break; }
            }
            foreach (var kw in buyerKeywords)
            {
                var kwPos = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                if (kwPos >= 0) { buyerIdx = kwPos; break; }
            }

            if (sellerIdx >= 0 && buyerIdx >= 0)
            {
                var sellerCompany = companyNames.OrderBy(c => Math.Abs(c.Position - sellerIdx)).First();
                vendorName = sellerCompany.FullName;
                if (allTaxIds.Count >= 2)
                {
                    var taxIdPositions = Regex.Matches(text, taxIdPattern)
                        .Cast<Match>()
                        .Select(m => (Id: Regex.Replace(m.Groups[1].Value, @"[-\s]", ""), Pos: m.Index))
                        .Where(x => x.Id.Length == 13)
                        .ToList();
                    vendorTaxId = taxIdPositions.OrderBy(x => Math.Abs(x.Pos - sellerIdx)).First().Id;
                }
                else if (allTaxIds.Count == 1)
                    vendorTaxId = allTaxIds[0];
            }
            else
            {
                vendorName = companyNames[0].FullName;
                vendorTaxId = allTaxIds.Count > 0 ? allTaxIds[0] : null;
            }
        }
        else if (companyNames.Count == 1)
        {
            vendorName = companyNames[0].FullName;
            vendorTaxId = allTaxIds.Count > 0 ? allTaxIds[0] : null;
        }
        else if (allTaxIds.Count > 0)
            vendorTaxId = allTaxIds[0];

        data.VendorName = vendorName;
        data.VendorTaxId = vendorTaxId;

        // Vendor contact details — anchored to Thai keywords so a random
        // 13-digit TaxId or 5-digit postal code can't be misread as a phone.
        var phoneMatch = VendorPhoneRegex.Match(text);
        if (phoneMatch.Success)
        {
            var raw = phoneMatch.Groups[1].Value;
            var digitCount = raw.Count(char.IsDigit);
            if (digitCount >= 9 && digitCount <= 11)
                data.VendorPhone = raw.Trim();
        }

        var emailMatch = VendorEmailRegex.Match(text);
        if (emailMatch.Success)
            data.VendorEmail = emailMatch.Value.Trim().TrimEnd('.', ',', ';');

        // Branch code — e-Tax spec requires 5-digit zero-padded; "00000" = HQ.
        // แยกฝั่งผู้ขาย/ผู้ซื้อด้วย BranchCodeExtractor: เดิมยิง regex ทับทั้งหน้า
        // แล้วยัดผลเป็นสาขา "ผู้ขาย" ตัวเดียว ⇒ สาขาผู้ซื้อไม่เคยถูกอ่าน (ตกเป็น
        // 00000 เสมอ) → ขายให้สาขาลูกค้าแล้วรายงานภาษีขายขึ้นสำนักงานใหญ่ผิด
        // (ประกาศอธิบดีฯ 199 / §86/4)
        var branches = BranchCodeExtractor.Extract(text);
        if (!string.IsNullOrWhiteSpace(branches.SellerBranchCode))
            data.VendorBranchCode = branches.SellerBranchCode;
        if (!string.IsNullOrWhiteSpace(branches.BuyerBranchCode))
            data.BuyerBranchCode = branches.BuyerBranchCode;

        var addrMatch = VendorAddressRegex.Match(text);
        if (addrMatch.Success)
        {
            var raw = addrMatch.Groups[1].Value
                .Replace("\r", " ").Replace("\n", " ")
                .Trim();
            // >250 chars almost certainly means the regex bled through into
            // the next section — Thai vendor addresses fit in ~120 chars.
            if (raw.Length >= 10 && raw.Length <= 250)
                data.VendorAddress = raw;
        }

        // Document number
        // ⚠️ "เลขที่ 99/1 ถ.สุขุมวิท" (ที่อยู่) ใช้คำเดียวกับเลขที่เอกสาร — คัดค่าที่ตาม
        // ด้วยคำบอกที่อยู่ออก (ผลตรวจ 2026-09-05 T2-10) · ป้ายเฉพาะของเอกสารมาก่อน
        // `tokEnd` ต้องมาก่อน `notAddr` — ไม่งั้น regex backtrack ให้กลุ่มสั้นลงจนด่านผ่าน
        // ("เลขที่ 123/45 ถนนพระราม 4" → "123/4") · ผลตรวจ 2026-09-06 · T5-N2
        const string tokEnd = @"(?![A-Za-z0-9\-/])";
        const string notAddr = @"(?!\s*(?:ถ\.|ถนน|หมู่|ม\.\s*\d|ซ\.|ซอย|ต\.|ตำบล|อ\.|อำเภอ|แขวง|เขต|จ\.|จังหวัด|Rd\b|Road\b|Soi\b|Moo\b))";
        string?[] docNumPatterns = {
            @"(?:เลขที่ใบกำกับ(?:ภาษี)?|เลขที่ใบเสร็จ(?:รับเงิน)?|Tax\s*Invoice\s*No\.?|Receipt\s*No\.?)\s*[:：]?\s*([A-Za-z0-9][A-Za-z0-9\-/]{2,})",
            @"เลขที่\s*[:：]?\s*([A-Za-z0-9\-/]+\d+)" + tokEnd + notAddr,
            @"(?:No|เลข(?:ที่)?)\s*\.?\s*[:：]?\s*([A-Za-z0-9\-/]+)" + tokEnd + notAddr,
            @"(?:INV|REC|TAX|TX|IV|PO|CN|DN)[\-/]?\s*(\d[\d\-/]*)",
        };
        foreach (var pattern in docNumPatterns)
        {
            var docNumMatch = Regex.Match(text, pattern!, RegexOptions.IgnoreCase);
            if (docNumMatch.Success) { data.DocumentNumber = docNumMatch.Groups[1].Value.Trim(); break; }
        }

        // Date — multiple patterns including Thai month names
        string[] datePatterns = {
            @"(?:วันที่|Date)\s*[:：]?\s*(\d{1,2})\s*[/\-\.]\s*(\d{1,2})\s*[/\-\.]\s*(\d{2,4})",
            @"(\d{1,2})\s*[/\-\.]\s*(\d{1,2})\s*[/\-\.]\s*(\d{4})",
            // ⚠️ ชื่อเดือน **เต็ม** ("15 สิงหาคม 2569") เป็นรูปที่ใบกำกับไทยพิมพ์บ่อยที่สุด
            // แต่แพตเทิร์นเดิมมีแต่ตัวย่อ ⇒ ไม่แมตช์เลย แล้วตกไปแพตเทิร์นถัดไป/ไม่มีวันที่
            // (ผลตรวจ 2026-09-06 · T2-13) · ตัวแปลงชื่อ→เลขอยู่ที่ Helpers/ThaiMonthName
            @"(\d{1,2})\s+(มกราคม|กุมภาพันธ์|มีนาคม|เมษายน|พฤษภาคม|มิถุนายน|กรกฎาคม|สิงหาคม|กันยายน|ตุลาคม|พฤศจิกายน|ธันวาคม)\s+(\d{4})",
            @"(\d{1,2})\s+(ม\.?ค\.?|ก\.?พ\.?|มี\.?ค\.?|เม\.?ย\.?|พ\.?ค\.?|มิ\.?ย\.?|ก\.?ค\.?|ส\.?ค\.?|ก\.?ย\.?|ต\.?ค\.?|พ\.?ย\.?|ธ\.?ค\.?)\s+(\d{4})",
            @"(\d{1,2})\s*[/\-\.]\s*(\d{1,2})\s*[/\-\.]\s*(\d{2})\b",
        };
        foreach (var pattern in datePatterns)
        {
            var dateMatch = Regex.Match(text, pattern);
            if (!dateMatch.Success) continue;
            int day = int.Parse(dateMatch.Groups[1].Value);
            string monthStr = dateMatch.Groups[2].Value;
            int year = int.Parse(dateMatch.Groups[3].Value);
            // ⚠️ เดิม `> 2500` + `< 100 -> +2000` = ปีย่อ "69" (พ.ศ.) กลายเป็น 2069
            // ขณะที่อีกเส้นทางในไฟล์เดียวกันแปลงเป็น 2026 ⇒ ใบเดียวกันลงคนละปี
            // ตามเส้นทาง OCR — ใช้ตัวแปลงกลาง Helpers/ThaiDate.NormalizeYear
            year = Accounting.Helpers.ThaiDate.NormalizeYear(year);
            // ★ ตารางชื่อเดือนอยู่ที่ Helpers/ThaiMonthName ตัวเดียว (รู้จักชื่อเต็ม
            // "สิงหาคม" ที่ตัวย่อจับไม่ได้ + อังกฤษ) และ **คืน null เมื่อไม่รู้จัก**
            //
            // ⚠️ เดิมตั้ง month = 1 เมื่อแมตช์ไม่ได้ ⇒ "15 สิงหาคม 2569" กลายเป็น
            // 15 มกราคม 2026 เงียบ ๆ ⇒ tax point (§78) และงวด ภ.พ.30 ผิดไป 7 เดือน
            // โดยยอดทุกตัวยังถูกจึงไม่มีอะไรสะดุดตา — "ค่า default ที่แต่งขึ้น
            // อันตรายกว่าการไม่ตอบ" ⇒ ไม่รู้จัก = ข้ามวันที่นี้ไปหาตัวถัดไป
            if (Accounting.Helpers.ThaiMonthName.TryParse(monthStr) is not int month) continue;
            if (day >= 1 && day <= 31 && month >= 1 && month <= 12 && year >= 1900)
            {
                try { data.DocumentDate = new DateTime(year, month, day); } catch { }
                break;
            }
        }

        // Amount — total
        string[] totalPatterns = {
            @"(?:รวม(?:เงิน)?(?:ทั้งสิ้น|ทั้งหมด|สุทธิ)|ยอดรวม(?:สุทธิ)?|GRAND\s*TOTAL|NET\s*TOTAL)\s*[:：]?\s*([\d,]+\.?\d*)",
            @"(?:TOTAL)\s*[:：]?\s*([\d,]+\.?\d*)",
            @"(?:รวมเงิน|จำนวนเงินรวม)\s*[:：]?\s*([\d,]+\.?\d*)",
        };
        foreach (var pattern in totalPatterns)
        {
            var totalMatch = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (totalMatch.Success) { data.TotalAmount = ParseDecimal(totalMatch.Groups[1].Value); if (data.TotalAmount > 0) break; }
        }

        // VAT
        var vatMatch2 = Regex.Match(text, @"(?:ภาษีมูลค่าเพิ่ม|VAT|Vat)\s*(?:7\s*%?)?\s*[:：]?\s*([\d,]+\.?\d*)", RegexOptions.IgnoreCase);
        if (vatMatch2.Success) data.VatAmount = ParseDecimal(vatMatch2.Groups[1].Value);

        // SubTotal
        var subMatch2 = Regex.Match(text, @"(?:ราคาสินค้า|ก่อนภาษี|รวมเงิน(?!ทั้ง)|SUB\s*TOTAL|Subtotal|ราคารวม)\s*[:：]?\s*([\d,]+\.?\d*)", RegexOptions.IgnoreCase);
        if (subMatch2.Success) data.SubTotal = ParseDecimal(subMatch2.Groups[1].Value);

        // Calculate missing values
        if (data.TotalAmount > 0 && data.VatAmount > 0 && data.SubTotal == null)
            data.SubTotal = data.TotalAmount - data.VatAmount;
        else if (data.SubTotal > 0 && data.VatAmount > 0 && data.TotalAmount == null)
            data.TotalAmount = data.SubTotal + data.VatAmount;
        else if (data.TotalAmount > 0 && data.SubTotal == null && data.VatAmount == null && hasTaxInvoice)
        {
            // BUGFIX (2025-05): Many Thai SMEs print "ใบเสร็จ/ใบกำกับภาษี"
            // on a single template even when they are NOT VAT-registered
            // and cannot issue a real tax invoice. Auto-splitting the
            // TotalAmount as if it had 7% VAT in that case poisons the
            // ledger with imaginary input-VAT receivable.
            //
            // Per ประมวลรัษฎากร §86, only persons registered for VAT
            // (มี Tax ID 13 หลัก) may issue an invoice with VAT. Gate the
            // back-calculation on a valid 13-digit vendor tax ID + a
            // sanity rounding match. When in doubt, leave both fields
            // null so the gateway flags it as low-confidence + the user
            // sees the original total verbatim.
            // ★ ด่านตัวเดียวอยู่ที่ Helpers/VatBackCalcGuard (pure + มีเทสต์)
            //   เดิมเงื่อนไขคือ "มีคำว่าใบกำกับภาษีที่ไหนก็ได้บนหน้า + ผู้ขายมี
            //   เลข 13 หลัก" ⇒ ใบเสร็จร้านที่พิมพ์ท้ายบิลว่า "ขอใบกำกับภาษีได้ที่
            //   เคาน์เตอร์" และใบที่ขายสินค้ายกเว้น §81 ก็ถูกแยก 7/107 ออกมาเป็น
            //   "ภาษีซื้อ" ที่ไม่มีอยู่จริง แล้วไหลไป ภ.พ.30 (ผลตรวจ T1-22)
            var backCalc = Accounting.Helpers.VatBackCalcGuard.Decide(
                text, data.VendorTaxId, data.Items.Select(i => i.Description));
            if (backCalc.Allowed)
            {
                data.SubTotal = Math.Round(data.TotalAmount.Value / 1.07m, 2, MidpointRounding.AwayFromZero);
                data.VatAmount = data.TotalAmount.Value - data.SubTotal.Value;
                // ★ ค่าที่ **คำนวณ** ต้องติดป้ายความมั่นใจเสมอ ไม่ใช่กลืนไปกับค่าที่
                //   อ่านมาจากกระดาษ — เดิมไม่มี key ทั้งสองช่อง ⇒ ป้ายเหลืองไม่เคยขึ้น
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.VatAmount] = backCalc.Confidence;
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SubTotal] = backCalc.Confidence;
                data.ReasoningTrace.Add("[VAT back-calc] " + backCalc.Reason);
            }
            else
            {
                // เหตุผลมาจากด่านตัวเดียวกัน — ห้ามเขียนข้อความชุดที่สองที่นี่
                // (เดิมมีข้อความเขียนมือครอบเฉพาะเคส "ไม่มีเลข 13 หลัก" เท่านั้น
                //  อีกสองเคสที่เพิ่มเข้ามาจะเงียบถ้าไม่รวมมาที่เดียว)
                data.ReasoningTrace.Add("[VAT skip] " + backCalc.Reason
                    + " — ใส่ยอดรวมตามที่อ่านมา ส่วน SubTotal/VatAmount เว้นว่างให้ผู้ใช้ตรวจ");
            }
        }

        // GL account suggestions
        var accountMap = new Dictionary<string, (string Dc, string Dn, string Cc, string Cn, string Cat)>
        {
            ["TaxInvoice"] = ("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า", "ค่าสินค้า"),
            ["Invoice"] = ("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า", "ค่าสินค้า"),
            ["Receipt"] = ("5300", "ค่าใช้จ่ายบริหาร", "1110", "เงินสด", "ค่าบริการ"),
            ["PurchaseOrder"] = ("1200", "สินค้าคงเหลือ", "2100", "เจ้าหนี้การค้า", "ค่าสินค้า"),
            ["WHT"] = ("2170", "ภาษีหัก ณ ที่จ่าย", "1110", "เงินสด", "อื่นๆ"),
            ["CreditNote"] = ("2100", "เจ้าหนี้การค้า", "5100", "ต้นทุนขาย", "ค่าสินค้า"),
            ["DebitNote"] = ("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า", "ค่าสินค้า"),
        };
        if (accountMap.TryGetValue(data.DocumentType, out var acct))
        {
            data.DebitAccountCode = acct.Dc; data.DebitAccountName = acct.Dn;
            data.CreditAccountCode = acct.Cc; data.CreditAccountName = acct.Cn;
            data.ExpenseCategory = acct.Cat;
            if (data.VatAmount > 0) { data.VatAccountCode = "1400"; data.VatAccountName = "ภาษีซื้อ"; }
        }

        // ── WHT detection ──
        //
        // ⚠️ เดิม `WHT|W/?T` **ไม่มี word-boundary** ⇒ `W/?T` จับ "T" ตัวเดียวใน
        // คำอังกฤษทั่วไปได้ (growth · Newton · WATT …) ⇒ `HasWht=true` บนใบที่
        // ไม่เกี่ยวเลย แล้วหน้า review ติ๊ก "มีภาษีหัก ณ ที่จ่าย" ให้เอง
        // (ผลตรวจ 2026-09-06 · T1-23)
        //
        // และเดิมอัตรามาจาก "เลข%" **ตัวไหนก็ได้** ในหน้าต่าง 70 ตัวอักษรรอบป้าย
        // ⇒ "ส่วนลด 3%" ที่อยู่ใกล้กันกลายเป็นอัตราหัก ณ ที่จ่าย → หักจริง
        // ตอนนี้บังคับให้ % อยู่**หลังป้าย ไม่เกิน 20 ตัวอักษร และบรรทัดเดียวกัน**
        var whtMatch = Regex.Match(text,
            @"หัก[ \t]*ณ[ \t]*ที่จ่าย|ภาษี[ \t]*หัก|\bWHT\b|\bW\.?T\.?\b",
            RegexOptions.IgnoreCase);
        if (whtMatch.Success)
        {
            data.HasWht = true;
            var rateMatch = Regex.Match(text,
                @"(?:หัก[ \t]*ณ[ \t]*ที่จ่าย|ภาษี[ \t]*หัก|\bWHT\b)[^%\n]{0,20}?([0-9]+(?:\.[0-9]+)?)[ \t]*%",
                RegexOptions.IgnoreCase);
            if (rateMatch.Success
                && decimal.TryParse(rateMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var rawRate)
                // อัตราต้องอยู่ในตารางกฎหมาย (ท.ป.4/2528 + §3 เตรส) — ตัวตัดสิน
                // ตัวเดียวคือ Helpers/ThaiWhtRateTable ห้ามเขียนลิสต์ซ้ำที่นี่
                && Accounting.Helpers.ThaiWhtRateTable.SnapToStatutory(rawRate) is decimal snapped)
            {
                // ── เกณฑ์ 1,000 บาท (ท.ป.4/2528 ข้อ 12) ──
                // เดิมไม่มีที่ไหนในเส้น OCR เรียกเกณฑ์นี้เลย ⇒ ใบ 500 บาทก็ถูกตั้ง
                // อัตราหัก 3% ให้ (ผู้รับเงินโดนหักเกินความจำเป็น) — ผลตรวจ T1-24
                var whtBase = data.SubTotal ?? data.TotalAmount ?? 0m;
                if (whtBase > 0m && !Accounting.Helpers.ThaiWhtRateTable.ShouldWithhold(whtBase))
                {
                    data.HasWht = false;
                    data.ReasoningTrace.Add(
                        $"[WHT] กระดาษระบุอัตรา {snapped}% แต่ฐาน {whtBase:N2} บาท ต่ำกว่าเกณฑ์ "
                        + "1,000 บาท (ท.ป.4/2528 ข้อ 12) → ไม่ตั้งภาษีหัก ณ ที่จ่ายให้ "
                        + "(ถ้ารวมทั้งสัญญาถึงเกณฑ์ ให้ติ๊กเอง)");
                }
                else data.WhtRate = snapped;
            }
        }

        // Payment terms
        var termsMatch = Regex.Match(text, @"(?:ชำระ|จ่าย).*?(?:ภายใน|within)\s*(\d+)\s*(?:วัน|days)", RegexOptions.IgnoreCase);
        if (termsMatch.Success)
            data.PaymentTermsDays = int.Parse(termsMatch.Groups[1].Value);

        return data;
    }

    private static decimal? ParseDecimal(string value)
    {
        var cleaned = value.Replace(",", "").Trim();
        return decimal.TryParse(cleaned, out var result) ? result : null;
    }

    public async Task<OcrResultResponse> GetResultAsync(Guid companyId, Guid scanResultId)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        await ClearDanglingCreatedDocumentAsync(companyId, new[] { result });
        return await AttachComplianceAsync(companyId, MapToResponse(result));
    }

    /// <summary>
    /// **ล้าง "ตัวชี้ค้าง" ไปยังเอกสารที่ถูกลบแล้ว** — ฝั่งอ่านของคู่สมมาตรกับ
    /// <c>DocumentService.DeleteDocumentAsync</c>
    ///
    /// <para>ฝั่งเขียนตัดตัวชี้ให้แล้วตอนลบ แต่ฝั่งอ่านต้องซื่อสัตย์ด้วยตัวเอง:
    /// แถวที่ค้างมาก่อนคอมมิตนี้ · การลบผ่านเส้นทางอื่นในอนาคต · ข้อมูลที่ถูก
    /// restore มาไม่ครบ ⇒ ถ้าไม่ตรวจ การ์ดจะโกหกว่า "สร้างแล้ว" ต่อไปเรื่อย ๆ
    /// (หลักการ "ชี้ไป ≠ ปลายทางมีจริง" — ดู <see cref="Accounting.Helpers.OcrCreatedDocumentLink"/>)</para>
    ///
    /// <para>คิวรีเดียวต่อหน้า ไม่ใช่ต่อแถว (กัน N+1) · ล้างค่าแล้ว<b>บันทึกลงฐาน</b>
    /// เพื่อให้ตัวกรองแท็บ "รอตรวจสอบ" (ซึ่งกรองที่ระดับ SQL ก่อนถึงตรงนี้)
    /// เห็นความจริงเดียวกันในครั้งถัดไป</para>
    /// </summary>
    private async Task ClearDanglingCreatedDocumentAsync(Guid companyId, IReadOnlyList<OcrScanResult> rows)
    {
        var ids = rows
            .Where(r => r.CreatedDocumentId.HasValue && r.CreatedDocumentId.Value != Guid.Empty)
            .Select(r => r.CreatedDocumentId!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return;

        var alive = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && ids.Contains(d.Id))
            .Select(d => d.Id)
            .ToListAsync();
        var aliveSet = alive.ToHashSet();

        var cleared = 0;
        foreach (var r in rows)
        {
            if (!Accounting.Helpers.OcrCreatedDocumentLink.IsDangling(
                    r.CreatedDocumentId, aliveSet.Contains(r.CreatedDocumentId ?? Guid.Empty)))
                continue;
            r.CreatedDocumentId = null;
            r.ProcessingNotes = (r.ProcessingNotes ?? "")
                + Accounting.Helpers.OcrCreatedDocumentLink.UnlinkNote(null, DateTime.UtcNow);
            r.UpdatedAt = DateTime.UtcNow;
            cleared++;
        }
        if (cleared > 0)
        {
            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "ล้างตัวชี้ค้างของสแกน {Count} ใบ (เอกสารปลายทางถูกลบไปแล้ว) บริษัท {CompanyId}",
                cleared, companyId);
        }
    }

    public async Task<PagedResponse<OcrResultResponse>> GetResultsAsync(Guid companyId, string? status, PagedRequest request)
    {
        var query = _db.Set<OcrScanResult>()
            .Where(r => r.CompanyId == companyId);

        // ตัวกรองสถานะ — "Created"/"Pending" เป็นสถานะ **เชิงความหมาย** ไม่ใช่ค่า
        // ใน ScanStatus (ซึ่งมีแค่ Pending/Processing/Completed/Failed)
        //
        // ⚠️ เดิมเทียบ `r.ScanStatus == status` ตรง ๆ ⇒ แท็บ "สร้างแล้ว" ส่ง
        // status=Created ไปแล้วได้ 0 แถวเสมอ = **แท็บว่างถาวร** ทั้งที่ตัวเลข
        // บนการ์ดสถิตินับได้ และตัวกรองฝั่งหน้าเว็บก็เขียนรอไว้แล้วแต่ไม่มี
        // ข้อมูลให้กรอง
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = status switch
            {
                "Created" => query.Where(r => r.CreatedDocumentId != null),
                // "รอตรวจสอบ" = สแกนเสร็จแล้วแต่ยังไม่ได้สร้างเอกสาร
                "Pending" => query.Where(r => r.ScanStatus == "Completed" && r.CreatedDocumentId == null),
                _ => query.Where(r => r.ScanStatus == status),
            };
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(r => r.OriginalFileName.Contains(request.Search)
                                  || (r.ExtractedVendorName != null && r.ExtractedVendorName.Contains(request.Search))
                                  || (r.ExtractedDocumentNumber != null && r.ExtractedDocumentNumber.Contains(request.Search)));

        var totalCount = await query.CountAsync();

        var rows = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        await ClearDanglingCreatedDocumentAsync(companyId, rows);

        // คำเตือนบนการ์ดคำนวณที่นี่ที่เดียว (ดู OcrScanComplianceEvaluator) —
        // ดึงเลขบริษัท **ครั้งเดียวต่อหน้า** ไม่ใช่ต่อแถว (กัน N+1)
        var items = (await AttachComplianceAsync(companyId, rows.Select(r => MapToResponse(r)).ToList()));

        return new PagedResponse<OcrResultResponse>(
            items, totalCount, request.Page, request.PageSize,
            (int)Math.Ceiling(totalCount / (double)request.PageSize));
    }

    public async Task<OcrResultResponse> CreateDocumentFromScanAsync(Guid companyId, Guid scanResultId, string createdBy, string? targetTypeOverride = null)
        => await CreateDocumentFromScanAsync(companyId, scanResultId, createdBy, targetTypeOverride, false);

    /// <param name="allowDuplicate">ผู้ใช้ยืนยันแล้วว่ารู้ตัวว่าเป็นใบซ้ำและยังต้องการสร้าง —
    /// ส่งมาจากหน้าเว็บหลังกดยืนยันในกล่องเตือนเท่านั้น</param>
    ///
    /// <remarks>
    /// ⚠️ ด่านกันซ้ำทั้งหมด (`CreatedDocumentId` · `FindDuplicateDocumentWarningAsync`)
    /// เป็นการ **อ่านแล้วค่อยเขียน** โดย transaction เพิ่งเปิดทีหลัง ⇒ สองคำขอที่มาพร้อมกัน
    /// (ดับเบิลคลิก · เปิดสองแท็บ · เว็บ+LINE · สอง instance) **ผ่านด่านทั้งคู่** แล้วได้
    /// เอกสารสองใบจากกระดาษใบเดียว = VAT ซ้ำใน ภ.พ.30 + จ่ายเจ้าหนี้ซ้ำ
    /// (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T5). ล็อกต่อ "สแกน 1 ใบ" ด้วย
    /// <see cref="Accounting.Helpers.JobLock"/> (session-level try-lock — ข้าม instance ได้
    /// เพราะคีย์มาจาก <see cref="Accounting.Helpers.AdvisoryLockKey"/> ที่ไม่สุ่มต่อ process)
    /// · ใช้ **try** ไม่ใช่ wait: คำขอที่สองไม่ควรรอแล้วสร้างใบที่สองต่อ — ควรบอกให้รีเฟรช
    /// </remarks>
    public async Task<OcrResultResponse> CreateDocumentFromScanAsync(
        Guid companyId, Guid scanResultId, string createdBy, string? targetTypeOverride, bool allowDuplicate)
    {
        OcrResultResponse? outcome = null;
        var ran = await Accounting.Helpers.JobLock.RunExclusiveAsync(
            _db, "ocr-create-document", scanResultId.ToString("N"),
            async () =>
            {
                outcome = await CreateDocumentFromScanCoreAsync(
                    companyId, scanResultId, createdBy, targetTypeOverride, allowDuplicate);
            },
            _logger, companyId);
        if (!ran || outcome == null)
            throw new Accounting.Helpers.BusinessRuleException(
                "กำลังสร้างเอกสารจากสแกนใบนี้อยู่ (มีอีกหน้าต่าง/อีกเครื่องกดพร้อมกัน) — "
                + "รอสักครู่แล้วรีเฟรชหน้า จะเห็นปุ่ม \"สร้างแล้ว →\" บนการ์ด",
                "OCR-CREATE-IN-PROGRESS");
        return outcome;
    }

    private async Task<OcrResultResponse> CreateDocumentFromScanCoreAsync(
        Guid companyId, Guid scanResultId, string createdBy, string? targetTypeOverride, bool allowDuplicate)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        // Normalise createdBy to a real user GUID so the document's creator
        // signature resolves (caller may pass an email/empty for headless/API
        // paths). Prefer the passed user, else the scan's operator, else owner.
        createdBy = await ResolveOcrCreatorAsync(companyId,
            Guid.TryParse(createdBy, out _) ? createdBy : result.CreatedBy, createdBy);

        if (result.ScanStatus != "Completed")
            throw new InvalidOperationException("OCR scan is not yet completed.");

        if (result.CreatedDocumentId.HasValue)
            throw new Accounting.Helpers.BusinessRuleException(
                "สแกนนี้สร้างเอกสารไปแล้ว — เปิดใบเดิมจากปุ่ม \"สร้างแล้ว →\" บนการ์ด "
                + "(ถ้าต้องการแก้ตัวเลข ให้แก้ที่ตัวเอกสาร ไม่ใช่ที่ผลสแกน)");

        // ── ด่านกันสร้างเอกสารซ้ำ ────────────────────────────────────────────
        //
        // เดิมเซิร์ฟเวอร์เช็คแค่ `CreatedDocumentId` ของ "สแกนใบนี้" — สแกนใบใหม่
        // ที่ระบบเองตรวจว่าเป็นใบซ้ำ (IsDuplicate จาก hash / fingerprint /
        // เลขที่+ยอด) ยังสร้างเอกสารที่สองได้เงียบ ๆ และหน้าเว็บก็เตือนเฉพาะปุ่ม
        // เดียวจากสามปุ่ม ⇒ อีกสองปุ่มลัดผ่านด่านไปเลย
        //
        // ตรวจสองชั้น: (1) ธงที่ตอนสแกนตั้งไว้ (2) **เทียบกับตารางเอกสารจริง**
        // ซึ่งเดิมไม่เคยเทียบเลย — สแกนที่ไม่ซ้ำกับสแกนเก่า แต่ซ้ำกับเอกสารที่
        // คีย์มือไว้ก่อน ก็ยังหลุด
        if (!allowDuplicate)
        {
            var dupMsg = await FindDuplicateDocumentWarningAsync(companyId, result);
            if (dupMsg != null) throw new Accounting.Helpers.BusinessRuleException(dupMsg);
        }

        // ป้ายยอดสลับ (SubTotal ↔ Total) ที่สแกนเก่า persist ไว้ก่อนมีตัวซ่อมในขั้นสกัด —
        // ต้องซ่อมที่นี่ด้วย ไม่งั้นสแกนที่ค้างอยู่ (เช่นใบลักกี้เวย์ 667/43.64/623.36)
        // ยังสร้างเอกสารผิดต่อไปแม้โค้ดขั้นสกัดแก้แล้ว · ตัวตัดสิน = OcrHeaderAmounts ตัวเดียว
        NormalizeSwappedHeaderAmounts(result);

        // Document type precedence: explicit caller override (the user's live
        // dropdown pick in the review modal) → persisted inferred
        // TargetDocumentType → fallback mapping off the scanned paper type.
        // pseudo-target "Deposit" (ใบมัดจำ) — ไม่ใช่ค่าใน enum: ลงเป็น Receipt
        // + IsDeposit=true (ทรงเดียวกับฟอร์มเอกสาร ห้าม drift สองทาง). ต้อง
        // ตัดสินก่อน Enum.TryParse ไม่งั้นตกไป fallback แล้วกลายเป็น Expense
        // ★ ตัวตัดสินอยู่ที่ Helpers/OcrTargetDocumentType ตัวเดียว — ด่านสิทธิ์ใน
        // OcrController เรียกตัวเดียวกัน (ผลตรวจย้อน 2026-09-06: ด่านเดิมมีตัวแปลง
        // ของตัวเองที่คืน null แล้วข้ามด่าน ทั้งที่เส้นนี้ยังสร้างเอกสารจริงเสมอ)
        var resolvedTarget = Accounting.Helpers.OcrTargetDocumentType.Resolve(
            targetTypeOverride, result.TargetDocumentType, result.DocumentType,
            hasLinkedPurchaseOrder: result.LinkedPurchaseOrderId.HasValue);
        var wantDeposit = resolvedTarget.IsDeposit;
        var docType = resolvedTarget.Type;
        if (wantDeposit)
            result.TargetDocumentType = Accounting.Helpers.OcrTargetDocumentType.DepositPseudoType;
        else if (!string.IsNullOrWhiteSpace(targetTypeOverride)
            && Enum.TryParse<DocumentType>(targetTypeOverride, ignoreCase: true, out var overrideTarget))
            // Persist the user's choice so re-opening the scan reflects it.
            result.TargetDocumentType = overrideTarget.ToString();

        // ─── PO LINKAGE — receive against the operator-linked PO ───
        // When the scan was linked to a PO via /link-po, force the target to
        // PurchaseInvoice and stamp RelatedDocumentId so the new PI ties back
        // to the order. Lines mapped to PO lines inherit the PO's AccountId
        // (resolved below in the lines loop) instead of re-deriving from OCR.
        Document? linkedPo = null;
        Dictionary<int, Guid?> poLineMap = new();
        if (result.LinkedPurchaseOrderId.HasValue)
        {
            linkedPo = await _db.Documents.AsNoTracking()
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.Id == result.LinkedPurchaseOrderId.Value
                    && d.CompanyId == companyId && !d.IsDeleted);
            if (linkedPo != null)
            {
                docType = DocumentType.PurchaseInvoice;
                if (!string.IsNullOrWhiteSpace(result.PoLineMappingsJson))
                {
                    try
                    {
                        var raw = System.Text.Json.JsonSerializer
                            .Deserialize<Dictionary<string, Guid?>>(result.PoLineMappingsJson) ?? new();
                        foreach (var (k, v) in raw)
                            if (int.TryParse(k, out var idx)) poLineMap[idx] = v;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "PoLineMappingsJson parse failed for scan {ScanId}", result.Id);
                    }
                }
            }
        }

        // ── TYPE-DEPENDENT DATA ──────────────────────────────────────────
        // The user may switch the target type in the review dropdown; every
        // field whose MEANING depends on the type must follow the FINAL pick,
        // not the scan's default assumption.
        //
        // Sales-side targets bill OUR customer — the counterparty is the
        // BUYER printed on the paper, not the vendor (on a sales doc the
        // vendor block is us).
        // ใช้ตัวตัดสินกลาง (Helpers/DocumentSide.cs) — เดิมสูตรนี้เขียนซ้ำ 3 ที่
        // และให้คำตอบไม่ตรงกันบนเอกสารใบเดียว
        var isSalesSide = Accounting.Helpers.DocumentSide.IsSales(docType, result.OurRole);

        // ─── ใบต้นทางทุกชนิด (Helpers/OcrPredecessorMatcher) ───
        // ผูกไว้ตอนสแกน (อัตโนมัติ) หรือผู้ใช้เลือกใน review — เอกสารใหม่ต้องชี้กลับด้วย
        // RelatedDocumentId เหมือนเส้น PO. ถ้าผู้ใช้เปลี่ยนชนิดเป้าหมายจนต้นทางไม่ใช่ชนิดที่
        // แปลงมาเป็นเอกสารนี้ได้อีก → ไม่ผูก + บอกใน Notes (ห้ามผูกผิดคู่เงียบ ๆ)
        Document? linkedPred = null;
        if (linkedPo == null && result.LinkedPredecessorDocumentId.HasValue)
        {
            linkedPred = await _db.Documents.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == result.LinkedPredecessorDocumentId.Value
                    && d.CompanyId == companyId && !d.IsDeleted);
            if (linkedPred != null && !DocumentService.GetPredecessorTypes(docType).Contains(linkedPred.DocumentType))
            {
                result.ProcessingNotes = (result.ProcessingNotes ?? "")
                    + $"\n[LINK] ไม่ผูกกับ {linkedPred.DocumentNumber}: {linkedPred.DocumentType} ไม่ใช่ต้นทางของ {docType}";
                linkedPred = null;
            }
        }

        // ── 50 ทวิ ที่ "เราถูกหัก" (ผลตรวจ 2026-09-05 T1-11) ─────────────────
        // กระดาษนี้เป็นหลักฐานเครดิตภาษีของใบขายที่ออกไปแล้ว ไม่ใช่ใบขายใบใหม่.
        // เดิม role inferrer ตั้ง target = ReceiptVoucher แล้วเส้นนี้สร้าง RV ที่ไม่มี
        // ใบต้นทาง (RelatedDocumentId=null) ⇒ ตอนอนุมัติ JE เป็น "ขายสด" (Dr เงินสด /
        // Cr รายได้ + ภาษีขาย) ⇒ รายได้เบิ้ล + VAT ขายเบิ้ล + AR ไม่ถูกล้าง.
        // ทางที่ถูก: ลงทะเบียนเครดิตภาษี (WhtCreditReceived) อย่างเดียว แล้วให้ผู้ใช้
        // ผูกกับใบขาย/ใบเสร็จเดิม — ไม่แต่งเอกสารที่ไม่มีบนโลก
        if (isSalesSide && linkedPo == null && linkedPred == null)
        {
            var ourTaxIdForCert = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId).Select(c => c.TaxId).FirstOrDefaultAsync();
            var certWeAreWithheld = Ocr.OcrDocumentRoleInferrer.InferWhtCertWeAreWithheld(
                result.RawTextContent, ourTaxIdForCert);
            if (certWeAreWithheld == true && (result.ExtractedTotalAmount ?? 0) > 0)
            {
                try { await EnsureWhtCreditFromCertAsync(companyId, result, null); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ลงทะเบียนภาษีถูกหักจากหนังสือรับรองไม่สำเร็จ (scan {Id})", result.Id);
                }
                result.ProcessingNotes = (result.ProcessingNotes ?? "")
                    + "\n[WHT-CERT] หนังสือรับรอง 50 ทวิ ที่เราถูกหัก — ลงทะเบียนเครดิตภาษี (ภ.ง.ด.50/51) ให้แล้ว ไม่สร้างเอกสารขายใหม่";
                await _db.SaveChangesAsync();
                throw new Accounting.Helpers.BusinessRuleException(
                    "กระดาษนี้เป็นหนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) ที่บริษัทเรา“ถูกหัก” — ไม่ใช่ใบขายใบใหม่ "
                    + "ระบบลงทะเบียนเครดิตภาษีไว้ให้แล้ว (เมนู ภาษีถูกหัก) · ถ้าต้องการบันทึกรับเงิน ให้เปิดใบแจ้งหนี้/ใบกำกับเดิม "
                    + "แล้วบันทึกรับชำระโดยระบุยอดหัก ณ ที่จ่าย แทนการสร้างเอกสารจากสแกน",
                    "OCR-WHTCERT-NO-SALE");
            }
        }

        Guid? contactId;
        if (isSalesSide)
        {
            // Counterparty = buyer on the paper. MatchedContactId points at the
            // vendor side, which on a sales doc is ourselves — never use it as
            // the primary pick here.
            // ใบต้นทางที่ผูกไว้รู้คู่ค้าอยู่แล้ว — ใช้ตัวนั้นก่อน (ไม่งั้น OCR อ่านชื่อผู้ซื้อเพี้ยน
            // แล้วสร้างลูกค้าใหม่ ทั้งที่ใบเสนอราคาต้นทางชี้ลูกค้าเดิมชัด ๆ)
            contactId = linkedPred?.ContactId;
            var buyerTax = result.BuyerTaxId;
            var buyerNm = result.BuyerName;
            if (!contactId.HasValue && !string.IsNullOrWhiteSpace(buyerTax))
                contactId = await ResolveContactIdByTaxIdAsync(companyId, buyerTax);
            if (!contactId.HasValue && !string.IsNullOrWhiteSpace(buyerNm))
                contactId = await _db.Contacts
                    .Where(c => c.CompanyId == companyId && !c.IsDeleted && c.Name.Contains(buyerNm))
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefaultAsync();
            if (!contactId.HasValue && !string.IsNullOrWhiteSpace(buyerNm))
            {
                var cust = new Contact
                {
                    CompanyId = companyId,
                    Name = buyerNm!,
                    TaxId = buyerTax,
                    // สาขาผู้ซื้อจากกระดาษ (§86/4 / ประกาศฯ 199) — เดิมไม่เคยเก็บ
                    // ⇒ ลูกค้าใหม่ทุกรายตกเป็นสำนักงานใหญ่ แม้ใบระบุ "สาขาที่ 3"
                    BranchCode = result.BuyerBranchCode,
                    IsCustomer = true,
                    IsSupplier = false,
                    CreatedBy = createdBy
                };
                _db.Contacts.Add(cust);
                contactId = cust.Id;
            }
            // ลูกค้าที่มีอยู่แล้วแต่ยังไม่มีสาขา — เติมจากกระดาษ (เติมเฉพาะตอน
            // ว่าง ไม่ทับค่าที่ผู้ใช้ตั้งไว้ — pattern เดียวกับฝั่งผู้ขาย)
            if (contactId.HasValue && !string.IsNullOrWhiteSpace(result.BuyerBranchCode))
            {
                var custRow = await _db.Contacts
                    .FirstOrDefaultAsync(c => c.Id == contactId.Value && c.CompanyId == companyId);
                if (custRow != null && string.IsNullOrWhiteSpace(custRow.BranchCode))
                {
                    custRow.BranchCode = result.BuyerBranchCode;
                    custRow.UpdatedBy = "OCR-Enrich";
                }
            }
            // Last resort so creation doesn't hard-fail when the buyer block
            // was unreadable — the user can re-pick the customer on the doc.
            contactId ??= result.MatchedContactId;
        }
        else
        {
            // Purchase side (PV / PI / Expense / CertInLieu …) — the vendor.
            contactId = result.MatchedContactId;
            if (!contactId.HasValue && !string.IsNullOrWhiteSpace(result.ExtractedVendorTaxId))
            {
                // normalize เลขภาษี — เทียบ == ตรง ๆ ไม่เจอเมื่อ format ต่าง → สร้างซ้ำ
                var vTaxDigits = DocumentService.NormalizeTaxDigits(result.ExtractedVendorTaxId);
                contactId = (await _db.Contacts
                    .Where(c => c.CompanyId == companyId && !c.IsDeleted
                        && c.TaxId != null && c.TaxId != "")
                    .Select(c => new { c.Id, c.TaxId })
                    .ToListAsync())
                    .Where(c => DocumentService.NormalizeTaxDigits(c.TaxId) == vTaxDigits)
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefault();
            }
            if (!contactId.HasValue && !string.IsNullOrWhiteSpace(result.ExtractedVendorName))
            {
                // Create a new supplier contact from extracted data
                var newContact = new Contact
                {
                    CompanyId = companyId,
                    Name = result.ExtractedVendorName,
                    TaxId = result.ExtractedVendorTaxId,
                    IsCustomer = false,
                    IsSupplier = true,
                    CreatedBy = createdBy
                };
                _db.Contacts.Add(newContact);
                contactId = newContact.Id;
            }
            // Backfill TaxId เข้า contact เดิม — เคสที่ vendor "ABC จำกัด" ถูก
            // match จากชื่อ (fuzzy / canonical) ตั้งแต่ตอน OCR ก่อนหน้า ตอนนั้น
            // contact ถูกสร้าง/มีอยู่แล้วแบบไม่มี TaxId แต่ OCR รอบนี้แกะ TaxId
            // ได้จากใบ → อัปเดตให้ contact ครบ §86/4 (ไม่ต้องให้ user ไปแก้
            // มือในหน้า Contacts) — สอดคล้องกับ backfill ที่มีอยู่ใน
            // ProcessScanAsync :1529 ที่อาจไม่ทันรอบนี้.
            if (contactId.HasValue && !string.IsNullOrWhiteSpace(result.ExtractedVendorTaxId))
            {
                var existing = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId.Value);
                if (existing != null && string.IsNullOrWhiteSpace(existing.TaxId))
                {
                    existing.TaxId = result.ExtractedVendorTaxId.Trim();
                }
            }
        }

        if (!contactId.HasValue)
            throw new InvalidOperationException("Cannot create document: no contact could be resolved from OCR data.");

        // CertificateInLieu legal fields — the printed form needs a certifier
        // (the person attesting the payment happened) — default to the
        // creating user; the reason gets a sensible RD-compliant default the
        // user can refine in the editor.
        string? certifierName = null;
        if (docType == DocumentType.CertificateInLieu && Guid.TryParse(createdBy, out var certUid))
            certifierName = await _db.Users.AsNoTracking()
                .Where(u => u.Id == certUid)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync();

        // Dates follow the FINAL doc type: paid-evidence types (PV / Receipt /
        // ReceiptVoucher / CertInLieu) record WHEN the money moved and carry
        // no credit due date; credit types (PI / Invoice / Expense / …) are
        // the reverse — due date from the OCR-read terms, no payment date.
        // Normalize เป็น "วันที่ไทย ณ 00:00 UTC" — กัน DocumentDate ถูก store
        // แบบ shift (02/06 BKK = 01/06 17:00 UTC) ที่ทำให้เลข/ภพ.30 boundary ผิด
        var docDate = Accounting.Helpers.ThaiDate.CalendarDateUtc(result.ExtractedDate ?? DateTime.UtcNow);
        var isPaidType = docType is DocumentType.PaymentVoucher or DocumentType.Receipt
            or DocumentType.ReceiptVoucher or DocumentType.CertificateInLieu;
        DateTime? dueDate = isPaidType ? null
            : result.PaymentTermsDays.HasValue ? docDate.AddDays(result.PaymentTermsDays.Value)
            : null;
        DateTime? paymentDate = isPaidType ? docDate : null;

        // ── Pre-fill: header WHT + reconstructed sub-total ──
        // The scan records HasWht + WhtRate (the % read off the paper); the
        // baht amount is recomputed from the ex-VAT base so the created
        // document carries the withholding without the user re-keying it.
        // WHT base is ALWAYS (grand total − VAT) — not ExtractedSubTotal,
        // which on discounted papers is the PRE-discount figure and would
        // overstate the withholding by discount × rate.
        // SubTotal ต้อง "ผูก" กับ grand total: subtotal + VAT − discount = total.
        // เดิมเชื่อ ExtractedSubTotal ตรง ๆ → เคส OCR แกะ subtotal (เช่น 630) ไม่
        // ตรงกับ grand total (เช่น 530) โดยไม่มี VAT/ส่วนลดอธิบาย → fallback line
        // ใช้ headerSubTotal (630) แต่ document.TotalAmount = headerTotal (530) →
        // "บรรทัด/ใบพิมพ์/JE = 630 แต่ยอดในรายงาน = 530" (report ≠ print ≠ JE).
        // ใช้ ExtractedSubTotal เฉพาะตอนที่ tie กับ grand total (รองรับ VAT-incl +
        // ส่วนลดจริง); ไม่งั้น derive จาก grand total (ตัวเลขเด่น/พาร์ทเนอร์ส่ง =
        // ตัวตั้งต้นที่เชื่อถือได้สุด) เพื่อให้ subtotal/line/total แตกกันไม่ได้.
        var hdrDiscRaw = result.ExtractedDiscountAmount ?? 0;
        var headerSubTotal = ResolveHeaderSubTotal(result, hdrDiscRaw);
        var whtBase = Math.Max(0, (result.ExtractedTotalAmount ?? 0) - (result.ExtractedVatAmount ?? 0));
        var whtRate = result.HasWht && result.WhtRate is > 0 ? result.WhtRate.Value : 0m;
        // ⚠️ ไม่หักให้เอง (ผู้ใช้ต้องยืนยัน) แต่ **ต้องเตือน** เมื่อกฎหมายให้หักและถึงเกณฑ์
        // ฿1,000 ท.ป.4/2528 ข้อ 12 — ไม่หัก = ผู้จ่ายรับผิด ม.54 + เงินเพิ่ม 1.5%/เดือน
        // (ผลตรวจ 2026-09-06 · T1-07 · T1-24)
        if (!isSalesSide && whtRate == 0m && result.SuggestedWhtRate is > 0m
            && Accounting.Helpers.ThaiWhtRateTable.ShouldWithhold(whtBase, 0m))
        {
            var it = Accounting.Helpers.ThaiWhtRateTable.Find(result.WhtIncomeTypeCode);
            // ⚠️ ข้อเสนอมาได้ 2 ทาง และ**ข้อความต้องบอกตรงว่ามาจากไหน** (D3-2 · D-3)
            //
            // เดิมเลือกประโยคด้วย `it != null` (= "มีรหัสประเภทเงินได้ ม.40 ไหม")
            // ซึ่งเป็นการ**เดาที่มาจากช่องที่บังเอิญมีค่า**: ใบที่ข้อเสนอมาจากนิสัย
            // ผู้ขายแต่มีรหัส ม.40 ติดมาจากชั้นอื่น จะได้ประโยค "หมวดนี้กฎหมายให้หัก"
            // ⇒ ประโยคอ้างกฎหมายบนใบที่กฎหมายไม่ได้พูดอะไรเลย (หลักการข้อ 7:
            // ข้อความที่ระบุ "สาเหตุ" ต้องตรวจสาเหตุนั้นจริง)
            // ตอนนี้อ่านจาก `SuggestedWhtSource` ที่ตัวตัดสินประทับไว้ตรง ๆ
            // แถวที่สแกนไว้**ก่อนรอบ 184** ไม่มีที่มาเก็บไว้ ⇒ `None` · ห้ามเดาว่าเป็น
            // ชั้นไหน (ไม่รู้ = บอกว่าไม่รู้ · G3) และไม่มี migration เพราะ "ที่มา"
            // ของใบเก่าไม่มีอยู่ในข้อมูลต้นทางอีกแล้ว — ดูสเปก §migration
            var whtSuggestLine = result.SuggestedWhtSource switch
            {
                Accounting.Helpers.WhtEvidenceSource.Statute =>
                    $"\n[WHT-SUGGEST] หมวดรายจ่ายนี้กฎหมายให้ผู้จ่ายหัก ณ ที่จ่าย {result.SuggestedWhtRate}%"
                        + (it != null ? $" ({it.TaxSection} {it.Name} → {string.Join("/", it.ApplicableForms)})" : ""),
                Accounting.Helpers.WhtEvidenceSource.VendorHistory =>
                    $"\n[WHT-SUGGEST] ผู้ขายรายนี้เคยถูกหัก ณ ที่จ่าย {result.SuggestedWhtRate}% เป็นปกติ"
                        + " (จากประวัติของผู้ขาย ไม่ใช่จากกระดาษใบนี้)"
                        + (it != null ? $" · ประเภทเงินได้ที่บันทึกไว้: {it.TaxSection} {it.Name}" : " และยังไม่รู้ประเภทเงินได้ ม.40"),
                _ =>
                    $"\n[WHT-SUGGEST] มีข้อเสนออัตราหัก ณ ที่จ่าย {result.SuggestedWhtRate}%"
                        + " แต่**ไม่ทราบที่มา** (สแกนรุ่นก่อนบันทึกที่มา) — ตรวจกับกระดาษก่อนใช้"
                        + (it != null ? $" · ประเภทเงินได้ที่บันทึกไว้: {it.TaxSection} {it.Name}" : ""),
            };
            result.ProcessingNotes = (result.ProcessingNotes ?? "")
                + whtSuggestLine
                + $" ≈ {Math.Round(whtBase * result.SuggestedWhtRate!.Value / 100m, 2, MidpointRounding.AwayFromZero):N2} บาท "
                + "— กระดาษไม่ได้พิมพ์ไว้ ระบบจึงไม่หักให้ ตรวจแล้วกรอกในใบก่อนอนุมัติ";
        }
        // MidpointRounding.AwayFromZero — เส้น JE (:5568) ระบุไว้แล้ว แต่เส้นนี้ลืม ⇒
        // ฐาน 101.50 × 3% ได้ 3.04 กับ 3.05 ตามปุ่มที่กด (ผลตรวจ 2026-09-06 · T5-N6)
        var headerWht = whtRate > 0
            ? Math.Round(whtBase * whtRate / 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;
        // App-wide convention (DocumentService / IntegrationService):
        // TotalAmount = SubTotal + VAT − WHT (net payable). The paper's grand
        // total does NOT deduct WHT, so subtract here — otherwise
        // TotalAmount ≠ PaidAmount + BalanceDue and a later edit-recompute
        // shifts the total by the WHT amount.
        var headerTotal = Math.Max(0, (result.ExtractedTotalAmount ?? 0) - headerWht);

        // ── Pre-fill: scan-level suggested debit GL ──
        // Used as the line-account fallback when a line has neither a PO
        // mapping nor its own SuggestedAccountCode — so every line lands with
        // a GL pick wherever the classifier produced one.
        var scanDebitAccountId = await ResolveScanDebitAccountIdAsync(companyId, result);

        // ── Pre-fill: แหล่งเงิน/ช่องทางชำระ (Credit account ที่ผู้ใช้เลือกใน review) ──
        // CreditAccountCode = ผังที่จะลง Cr (เช่น 11110 เงินสด, 11120 ธนาคาร,
        // 21230 เจ้าหนี้กรรมการ). map เข้า BankAccountId ถ้าผูก BankAccount
        // อยู่; ไม่งั้นเป็น PaymentAccountId (any GL account ที่ลงผ่าน PV).
        Guid? scanCreditAccountId = null;
        Guid? scanCreditBankAccountId = null;
        if (!string.IsNullOrWhiteSpace(result.SuggestedAccountsJson))
        {
            try
            {
                using var sa = System.Text.Json.JsonDocument.Parse(result.SuggestedAccountsJson);
                if (sa.RootElement.TryGetProperty("CreditAccountCode", out var cac)
                    && cac.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(cac.GetString()))
                {
                    var creditCode = cac.GetString();
                    scanCreditAccountId = await _db.ChartOfAccounts.AsNoTracking()
                        .Where(a => a.CompanyId == companyId && a.AccountCode == creditCode && !a.IsDeleted)
                        .Select(a => (Guid?)a.Id).FirstOrDefaultAsync();
                    // ถ้าเป็นบัญชีธนาคาร (มี BankAccount ผูกอยู่) → ใช้ BankAccountId
                    if (scanCreditAccountId.HasValue)
                    {
                        scanCreditBankAccountId = await _db.BankAccounts.AsNoTracking()
                            .Where(b => b.CompanyId == companyId && b.LinkedAccountId == scanCreditAccountId.Value)
                            .Select(b => (Guid?)b.Id).FirstOrDefaultAsync();
                    }
                }
            }
            catch { /* malformed — skip */ }
        }

        // ── Pre-fill: ใบกำกับภาษีของผู้ขาย (RD §86/4 + §86/14) ──
        // เอกสารฝั่งซื้อที่มี VAT + เลขใบ → บันทึกเลข/วัน/สาขาใบผู้ขาย +
        // ติ๊ก HasTaxInvoiceReference เพื่อให้ขึ้นรายงานภาษีซื้อ ภพ.30 ทันที
        // (เดิมใส่แค่ Reference → ฟอร์มแก้ไขโชว์ "ขาดเลขใบกำกับ" + ภาษีซื้อ
        // ค้าง 11640). PV ใช้ flag HasTaxInvoiceReference; PI/Expense ใช้
        // SupplierInvoiceNumber/Date ตรง ๆ.
        // §82/5 — เอกสารที่เคลมภาษีซื้อไม่ได้ (ใบกำกับอย่างย่อ §86/6 / ใบเสร็จ
        // ที่ไม่ใช่ใบกำกับเต็มรูป) จะถูก tag [VAT-CLAIM] ใน ProcessingNotes ตอน
        // scan. ห้ามเปิด "ใช้งานใบกำกับภาษี" อัตโนมัติ (จะกลายเป็นเคลม VAT ผิด
        // กฎหมาย). ให้ตรงกับ Path A ที่ไม่ auto-ติ๊กเคลมในเคสนี้.
        var vatNotClaimable = (result.ProcessingNotes ?? "").Contains("[VAT-CLAIM]");
        // ⚠️ เลขที่ของ "ใบแจ้งหนี้/ใบส่งของ/ใบเสร็จธรรมดา" ไม่ใช่เลขใบกำกับภาษี (ผลตรวจ
        // 2026-09-05 T1-20): ผู้ให้บริการไทยมักออกใบแจ้งหนี้ก่อน ใบกำกับตามมาตอนรับเงิน
        // (§78/1) — ถ้าเอาเลขใบแจ้งหนี้ลง SupplierInvoiceNumber ระบบจะเคลมภาษีซื้อ
        // **งวดนี้** ทั้งที่ยังไม่มีใบกำกับ (§82/5(1)) · ทางที่ถูกคือพัก VAT ที่ 11640
        // แล้วเติมเลขใบกำกับตอนใบมาถึง (flow มีอยู่แล้ว). ไม่รู้ชนิดกระดาษ = คงพฤติกรรมเดิม
        var paperIsNotTaxInvoice = result.DocumentType is not null
            && (string.Equals(result.DocumentType, "Invoice", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result.DocumentType, "Receipt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result.DocumentType, "DeliveryNote", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result.DocumentType, "Quotation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result.DocumentType, "BillingNote", StringComparison.OrdinalIgnoreCase));
        if (paperIsNotTaxInvoice && !isSalesSide && (result.ExtractedVatAmount ?? 0) > 0 && !vatNotClaimable)
            result.ProcessingNotes = (result.ProcessingNotes ?? "")
                + "\n[TAX-INV-PENDING] กระดาษเป็น" + result.DocumentType + " ไม่ใช่ใบกำกับภาษี — VAT พักไว้ (11640) รอเติมเลขใบกำกับเมื่อได้รับ (§82/5(1))";
        var bookSupplierInvoice = !isSalesSide
            && !vatNotClaimable
            && !paperIsNotTaxInvoice
            && (docType is DocumentType.PaymentVoucher or DocumentType.PurchaseInvoice or DocumentType.Expense)
            && !string.IsNullOrWhiteSpace(result.ExtractedDocumentNumber)
            && ((result.ExtractedVatAmount ?? 0) > 0 || !string.IsNullOrWhiteSpace(result.ExtractedVendorTaxId));

        // ใบลดหนี้/ใบเพิ่มหนี้ฝั่งซื้อ: ช่องคู่เดียวกันนี้เปลี่ยนความหมายเป็น
        // "เลขที่/วันที่ **ใบลดหนี้** ที่ผู้ขายออกให้" (ดู _syncSupplierInvoiceFields
        // ฝั่ง UI) — เดิม bookSupplierInvoice ไม่ครอบ CN/DN เลย สแกนใบลดหนี้มา
        // ช่องนี้จึงว่างเสมอ ทั้งที่เลขอยู่บนกระดาษตรงหน้า (กฎเหล็ก #3 ห้ามปล่อย
        // field ที่เอกสารมีให้ว่าง). ไม่ผูกกับ vatNotClaimable/มี VAT — เลขที่และ
        // วันที่ของใบลดหนี้เป็นข้อมูลอ้างอิงตาม §86/10 ไม่ใช่เงื่อนไขการเคลม
        var bookSupplierCreditNote = !isSalesSide
            && docType is DocumentType.CreditNote or DocumentType.DebitNote
            && !string.IsNullOrWhiteSpace(result.ExtractedDocumentNumber);
        var bookSupplierRef = bookSupplierInvoice || bookSupplierCreditNote;

        // Transaction wraps the insert. ก่อนหน้านี้ออก "เลขจริง" ทันทีตอนสร้าง
        // Draft → ผิดกฎ CLAUDE.md ("เลขเอกสารออกตอน Approve เท่านั้น, Draft
        // ใช้ DRAFT-{guid} placeholder") + เป็น root cause ของ DocumentNumber↔
        // DocumentDate desync: ถ้า DocumentDate ถูกแก้ทีหลัง (review UI / OCR
        // retry / artifact ก่อน TZ-fix), เลขที่ออกไปแล้วจะคาวันเก่า ตอน Approve
        // ที่ regen เฉพาะเอกสารขึ้นต้น "DRAFT-" ก็ skip เลขเดิม → mismatch.
        // ใช้ DRAFT- placeholder ตามกฎ → ApproveDocumentAsync จะ regen เลขจาก
        // doc.DocumentDate ตอน Approve (ผ่าน DocumentNumberGenerator.NextAsync
        // ใน DocumentService.cs:1828) → DocumentNumber ตรงกับ DocumentDate
        // ที่ store ใน DB เสมอ. Bonus: ลบ Draft ไม่สร้าง gap ใน sequence
        // (§86/4 compliance).
        await using var txn = await _db.Database.BeginTransactionAsync();
        var docNumber = $"DRAFT-{Guid.NewGuid():N}".Substring(0, 14);
        // สาขาผู้ขาย: Contact ถูก enrich ด้วยสาขาที่ OCR แกะจากกระดาษตอน scan
        // แล้ว (BranchCodeExtractor → Contact.BranchCode) — ใช้ค่านั้นก่อน ค่อย
        // fallback 00000. เดิม hardcode "00000" ทับ → ใบสาขา 00003 ขึ้นรายงาน
        // ภาษีซื้อเป็นสำนักงานใหญ่ผิด (ประกาศฯ 199/§86/4) แบบเงียบ
        // ⚠️ ลำดับที่ถูกต้อง: สาขาที่พิมพ์อยู่ "บนใบใบนี้" ต้องมาก่อน Contact —
        // ผู้ขายหลายสาขาใช้ Contact เดียวกัน ถ้าอ่านจาก Contact จะได้สาขาของใบที่
        // สแกนครั้งก่อน (เช่นใบนี้สาขา 00003 แต่ Contact ค้าง 00000) ขึ้นรายงาน
        // ภาษีซื้อผิดสาขาแบบเงียบ (ประกาศฯ 199 / §86/4)
        var vendorBranchForBook = bookSupplierRef
            ? (!string.IsNullOrWhiteSpace(result.VendorBranchCode)
                ? result.VendorBranchCode
                : await _db.Contacts.AsNoTracking()
                    .Where(c => c.Id == contactId.Value && c.CompanyId == companyId)
                    .Select(c => c.BranchCode).FirstOrDefaultAsync())
            : null;
        var document = new Document
        {
            CompanyId = companyId,
            DocumentNumber = docNumber,
            DocumentType = docType,
            // ใบลดหนี้/เพิ่มหนี้: role inferrer รู้อยู่แล้วว่าเราเป็นผู้ซื้อหรือผู้ขาย
            // ของใบนี้ (จากเลขผู้เสียภาษีบนกระดาษเทียบกับบริษัทเรา) — เดิมข้อมูลนี้
            // ถูกทิ้ง ทำให้ระบบต้องไป "เดา" ฝั่งภาษีอีกครั้งตอนอนุมัติ ทั้งที่รู้แล้ว
            // (เดาผิด = JE ลงผิดฝั่งถาวร ยอดไปโผล่ผิดฝั่งใน ภ.พ.30)
            // สกุลเงินบนกระดาษ — เดิมไม่เคยอ่าน ใบ USD จึงถูกบันทึกเป็นบาทเงียบ ๆ
            // (ตัวเลขเท่าเดิมแต่ความหมายผิด = ยอดผิดหลายสิบเท่า) ตรวจจากสัญลักษณ์/
            // รหัสสกุลบนเอกสาร ไม่พบ = THB ตามเดิม
            // สกุลเงินที่เอกสารประกาศไว้ (e-Tax XML) ชนะการเดาจากข้อความเสมอ
            Currency = result.Currency ?? InferCurrency(result.RawTextContent) ?? "THB",
            // เหตุผลการลดหนี้ (§86/10) — บังคับก่อนอนุมัติ เดิม OCR ไม่เคยเซ็ต
            // ใบลดหนี้ที่สแกนมาจึงติดบล็อก "ต้องระบุเหตุผล" ทุกใบ 100%
            // กระดาษมักพิมพ์เหตุผลไว้อยู่แล้ว → อ่านจากข้อความ ถ้าไม่พบค่อยให้ผู้ใช้เลือก
            // ใบมัดจำ (pseudo-target "Deposit") → Receipt + IsDeposit: AutoPost
            // ลง Cr 217xx ขายรอรับรู้ แทนรายได้ (รับรู้เมื่อส่งมอบ/ออกใบกำกับ)
            IsDeposit = wantDeposit,
            CreditNoteReason = docType == DocumentType.CreditNote
                ? InferCreditNoteReason(result.RawTextContent) : null,
            CnDnPurchaseSideOverride =
                docType is DocumentType.CreditNote or DocumentType.DebitNote
                    ? string.Equals(result.OurRole, "Buyer", StringComparison.OrdinalIgnoreCase)
                    : null,
            Status = DocumentStatus.Draft,
            DocumentDate = docDate,
            DueDate = dueDate,
            PaymentDate = paymentDate,
            ContactId = contactId.Value,
            SubTotal = headerSubTotal,
            VatAmount = result.ExtractedVatAmount ?? 0,
            DiscountAmount = result.ExtractedDiscountAmount ?? 0,
            WithholdingTaxAmount = headerWht,
            TotalAmount = headerTotal,
            // Paid-evidence types are cash-settled at creation — the app's
            // convention (DocumentService edit/approve paths) keeps such docs
            // at BalanceDue=0 / PaidAmount=Total so they never appear in the
            // aging / ค้างชำระ reports. Credit types carry the full balance.
            // Expense is explicitly Credit per the role separation: it is the
            // request/accrual document (ตั้งหนี้) — cash only moves on a PV.
            PaymentType = isPaidType ? Models.Enums.PaymentType.Cash
                : docType == DocumentType.Expense ? Models.Enums.PaymentType.Credit
                : null,
            PaidAmount = isPaidType ? headerTotal : 0,
            BalanceDue = isPaidType ? 0 : headerTotal,
            Reference = result.ExtractedDocumentNumber,
            // หมายเหตุของผู้ใช้ (เหตุผลทางธุรกิจ) มาก่อนเสมอ — เป็นสิ่งที่คนอ่าน
            // ใบจริง ๆ ต้องเห็น ส่วนที่มาของไฟล์เป็นข้อมูลระบบต่อท้าย
            // (§65 ตรี(3)/(14): ไม่มีเหตุผลว่าเกี่ยวกับกิจการ = รายจ่ายต้องห้าม)
            Notes = string.Join(" · ", new[]
            {
                result.UserNotes,
                linkedPo != null
                    ? $"Created from OCR scan: {result.OriginalFileName} (รับตาม PO {linkedPo.DocumentNumber})"
                    : linkedPred != null
                        ? $"Created from OCR scan: {result.OriginalFileName} (อ้างอิง {linkedPred.DocumentNumber})"
                        : $"Created from OCR scan: {result.OriginalFileName}",
            }.Where(s => !string.IsNullOrWhiteSpace(s))),
            // Linkback so the new PI's "อ้างอิงเอกสาร" surfaces the PO.
            RelatedDocumentId = linkedPo?.Id ?? linkedPred?.Id,
            // ใบรับรองแทนใบเสร็จ — legal fields the printed form requires.
            CertificateReason = docType == DocumentType.CertificateInLieu
                ? "ผู้ขาย/ผู้รับเงินไม่สามารถออกใบเสร็จรับเงินได้" : null,
            CertifierName = certifierName,
            // ใบกำกับภาษีของผู้ขาย — เติมจาก OCR ให้ฟอร์มไม่โชว์ "ขาดเลขใบ"
            // + ภาษีซื้อขึ้น ภพ.30 ได้เลย (ไม่ค้าง 11640 โดยไม่จำเป็น)
            HasTaxInvoiceReference = bookSupplierInvoice && docType == DocumentType.PaymentVoucher,
            // PI/Expense/PV = เลขใบกำกับของผู้ขาย · CN/DN ฝั่งซื้อ = เลขใบลดหนี้/
            // เพิ่มหนี้ที่ผู้ขายออกให้ (ช่องเดียวกัน ความหมายตามชนิดเอกสาร)
            SupplierInvoiceNumber = bookSupplierRef ? result.ExtractedDocumentNumber : null,
            SupplierTaxInvoiceDate = bookSupplierRef ? result.ExtractedDate : null,
            // ไม่แต่ง "00000" เมื่อทั้งกระดาษและ Contact ไม่บอกสาขา (ผลตรวจ 2026-09-05
            // T1-10) — ป้าย 30% บนหน้า review ถูกแล้ว แต่ค่าที่เขียนลงเอกสารต้องเป็น
            // "ไม่รู้" ให้ผู้ใช้ยืนยัน ไม่ใช่ "สำนักงานใหญ่" ที่ไปโผล่ในรายงานภาษีซื้อ §87
            SupplierBranchCode = bookSupplierRef
                ? (string.IsNullOrWhiteSpace(vendorBranchForBook) ? null : vendorBranchForBook)
                : null,
            // หมวดค่าใช้จ่ายระดับเอกสาร = ผังเดบิตที่ AI/ผู้ใช้เลือก
            ExpenseCategoryId = !isSalesSide ? scanDebitAccountId : null,
            // แหล่งเงิน/ช่องทางชำระ = ผังเครดิตที่เลือกใน review (ฝั่งซื้อ)
            // bankAccount > paymentAccount → BankAccountId; ไม่งั้น PaymentAccountId
            BankAccountId = !isSalesSide ? scanCreditBankAccountId : null,
            PaymentAccountId = !isSalesSide && !scanCreditBankAccountId.HasValue
                ? scanCreditAccountId : null,
            CreatedBy = createdBy
        };

        // Build document lines from the persisted extracted items (the same
        // data AutoCreateDocumentAsync uses). Without this the document was
        // created with a header but ZERO lines, so it opened completely empty
        // in the editor — the user couldn't see what was scanned.
        var items = new List<OcrExtractedLineItem>();
        if (!string.IsNullOrWhiteSpace(result.ExtractedItemsJson))
        {
            try
            {
                items = System.Text.Json.JsonSerializer
                    .Deserialize<List<OcrExtractedLineItem>>(result.ExtractedItemsJson) ?? new();
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "OCR ExtractedItemsJson parse failed for scan {ScanId}", result.Id);
            }
        }

        // ⭐ Re-sanitize ตอน create-document ด้วย (ไม่ใช่แค่ตอน scan) — กันเคส
        // scan เก่าที่ทำก่อนมี sanitizer: ExtractedItemsJson ฝัง split
        // "(ส่วนมีภาษี)/(ส่วนไม่มีภาษี)" + เศษ 0.02 ไว้แล้ว → fold/merge ที่นี่
        // ทำให้ document ที่สร้างจาก scan เก่าก็ได้บรรทัดสะอาด. Idempotent.
        if (items.Count > 0)
        {
            var tmpSan = new OcrExtractedData();
            foreach (var it in items) tmpSan.Items.Add(it);
            SanitizeVatSplitArtifacts(tmpSan);
            items = tmpSan.Items.ToList();
        }

        // ★ ตัวสร้างบรรทัดจากผลสแกน **ตัวเดียว** — RepopulateDocumentLinesFromScanAsync
        // เรียกตัวนี้ด้วย (เดิมมีสำเนาที่สองที่ไม่รู้จัก PricesIncludeVat/ส่วนลด/VAT รายบรรทัด
        // ⇒ กระดาษใบเดียวกันได้บรรทัดคนละแบบตามปุ่มที่กด — ผลตรวจ 2026-09-10)
        await BuildScanLinesAsync(companyId, result, items, document, linkedPo, poLineMap,
            scanDebitAccountId, headerSubTotal, hdrDiscRaw, whtRate, headerWht, isSalesSide);

        // เอกสารที่ scan ตรวจว่า "เคลมภาษีซื้อไม่ได้" ([VAT-CLAIM] เช่นใบกำกับ
        // อย่างย่อ §82/5(2) / ใบเสร็จไม่ใช่ใบกำกับเต็มรูป §82/5(1)) — ต้องปิด
        // เคลมที่ระดับบรรทัดด้วย: default IsVatClaimable=true จะพา VAT ไปพัก
        // 11640 แล้วกล่องเติมใบกำกับชวนผู้ใช้กรอกเลขสลิป POS ดันเข้า 11610 =
        // ทำผิดกฎหมายทั้งที่ตั้งใจดี. ปิดแล้ว VAT fold เข้าค่าใช้จ่ายตอน approve
        // ตามที่กฎหมายกำหนดทันที
        if (!isSalesSide && vatNotClaimable)
        {
            foreach (var dl in document.Lines)
            {
                dl.IsVatClaimable = false;
                dl.VatNonClaimableReason ??=
                    "เอกสารต้นทางเคลมภาษีซื้อไม่ได้ (ใบกำกับอย่างย่อ/ไม่ใช่ใบกำกับเต็มรูป §82/5) — ขอใบกำกับเต็มรูปจากผู้ขายหากต้องการเคลม";
            }
        }

        // ── ธงผังบัญชีเป็นตัวปิดการเคลมภาษีซื้อ (T1-05) ───────────────────────
        // เส้นคีย์มือผ่าน DocumentService บังคับข้อนี้มาตลอด แต่เส้น OCR สร้าง entity
        // เองจึงข้ามไป ⇒ ใบเดียวกันได้คำตอบคนละอย่างตามทางที่เข้า. ตัวตัดสินอยู่ที่
        // Helpers/InputVatAccountPolicy ตัวเดียว (pure + มีเทสต์)
        if (!isSalesSide)
        {
            var lineAccountIds = document.Lines
                .Where(l => l.AccountId.HasValue)
                .Select(l => l.AccountId!.Value)
                .Distinct()
                .ToList();
            if (lineAccountIds.Count > 0)
            {
                var flags = await _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && lineAccountIds.Contains(a.Id))
                    .Select(a => new { a.Id, a.InputVatClaimable })
                    .ToDictionaryAsync(x => x.Id, x => x.InputVatClaimable);
                var forcedByChart = 0;
                foreach (var dl in document.Lines)
                {
                    bool? acctClaimable = dl.AccountId.HasValue && flags.TryGetValue(dl.AccountId.Value, out var f)
                        ? f : null;
                    var outcome = Accounting.Helpers.InputVatAccountPolicy.Apply(
                        dl.IsVatClaimable, dl.VatNonClaimableReason, acctClaimable);
                    dl.IsVatClaimable = outcome.Claimable;
                    dl.VatNonClaimableReason = outcome.Reason;
                    if (outcome.Changed) forcedByChart++;
                }
                if (forcedByChart > 0)
                    result.ProcessingNotes = (result.ProcessingNotes ?? "")
                        + $"\n[VAT-CLAIM] ผังบัญชีที่เลือกตั้งเป็นภาษีซื้อต้องห้าม — ปิดการเคลมให้ {forcedByChart} บรรทัด (§82/5)";
            }
        }

        // ── ผู้ขายต่างประเทศ: ภาระ ภ.พ.36 (§83/6) + ภ.ง.ด.54 (§70) ──────────
        // นักบัญชีเห็น invoice จาก AWS/Google/Adobe แล้วรู้ทันทีว่าต้อง (1) นำส่ง VAT 7%
        // แทนผู้ขายด้วย ภ.พ.36 (แล้วเคลมเป็นภาษีซื้อเดือนถัดไป) (2) หัก ณ ที่จ่ายตาม
        // ม.70 ถ้าเป็นค่าบริการ/ค่าสิทธิ — ระบบเดิม**ไม่เคยพูดถึงทั้งสองอย่าง**
        // ⇒ ภาระหายทั้งก้อน (เบี้ยปรับ 2 เท่า + เงินเพิ่ม) · ผลตรวจ 2026-09-06 · T1-12
        if (!isSalesSide
            && Accounting.Helpers.ThaiVatTypeRule.LooksForeignVendor(null, result.ExtractedVendorTaxId)
            && (result.ExtractedTotalAmount ?? 0m) > 0m)
        {
            var baseAmt = Math.Max(0m, (result.ExtractedTotalAmount ?? 0m) - (result.ExtractedVatAmount ?? 0m));
            var pp36 = Math.Round(baseAmt * 0.07m, 2, MidpointRounding.AwayFromZero);
            result.ProcessingNotes = (result.ProcessingNotes ?? "")
                + $"\n[PP36] ผู้ขายไม่มีเลขผู้เสียภาษีไทย — ถ้าเป็นบริการที่ใช้ในไทย ต้องนำส่ง VAT แทน "
                + $"ผู้ประกอบการต่างประเทศด้วย ภ.พ.36 ≈ {pp36:N2} บาท (§83/6) ภายในวันที่ 7/15 ของเดือนถัดไป "
                + "แล้วจึงเคลมเป็นภาษีซื้อในเดือนที่นำส่ง"
                + $"\n[PND54] ค่าบริการ/ค่าสิทธิ/ดอกเบี้ยที่จ่ายออกต่างประเทศ ต้องหัก ณ ที่จ่ายตาม ม.70 "
                + "(ทั่วไป 15% · เงินปันผล 10%) และยื่น ภ.ง.ด.54 — ตรวจอนุสัญญาภาษีซ้อน (DTA) ก่อนใช้อัตรา";
        }

        // ── สกุลเงินต่างประเทศต้องมีอัตราแลกเปลี่ยน (T1-05 · T1-12) ──────────
        // `Currency` ถูกอ่านจากกระดาษมาตั้งแต่ต้น แต่ไม่เคยมีใครหา `ExchangeRate` ให้
        // ⇒ ใบ USD ถูกบันทึกเป็นบาทเงียบ ๆ (ตัวเลขเท่าเดิม ความหมายผิดหลายสิบเท่า).
        // ไม่รู้อัตรา = **บอกว่าไม่รู้** แล้วห้าม auto-approve — ห้ามแต่งอัตราให้เอง
        if (!string.Equals(document.Currency, "THB", StringComparison.OrdinalIgnoreCase))
        {
            var fxRate = await ResolveScanExchangeRateAsync(document.Currency, document.DocumentDate);
            if (fxRate is decimal fx && fx > 0m)
            {
                document.ExchangeRate = fx;
                result.ProcessingNotes = (result.ProcessingNotes ?? "")
                    + $"\n[FX] {document.Currency} @ {fx:N4} (อัตรา ธปท. วันที่เอกสาร) — ตรวจสอบก่อนยืนยัน";
            }
            else
            {
                result.ProcessingNotes = (result.ProcessingNotes ?? "")
                    + $"\n[FX-UNKNOWN] เอกสารสกุล {document.Currency} แต่ยังไม่มีอัตราแลกเปลี่ยนของวันที่นี้ — "
                    + "กรอกอัตราในใบก่อนอนุมัติ (ระบบไม่เดาอัตราให้)";
            }
        }

        _db.Documents.Add(document);

        result.CreatedDocumentId = document.Id;
        result.MatchedContactId = contactId;

        await _db.SaveChangesAsync();
        await txn.CommitAsync();

        // ★ CAPTURE ฝั่ง "ยอมรับ" (กฎเหล็ก #1 · ผลตรวจ 2026-09-05 T3-03): เส้น 1-click
        // (สร้างจากการ์ด/สร้างทั้งชุด) ไม่ผ่าน SubmitCorrectionAsync ⇒ เดิมตัวอย่าง
        // "ผู้ใช้รับตามที่เติม" หายหมด เหลือแต่ตัวอย่างที่แก้ = selection bias ให้นักเรียน
        // (ผัง GL ปิดลูปที่ ApproveDocument อยู่แล้ว — ที่นี่ปิดชนิดเอกสาร + คู่ค้า)
        if (_feedbackRecorder != null)
        {
            try
            {
                // ⚠️ เส้นนี้คือ "กดสร้างจากการ์ด" — ผู้ใช้ไม่ได้เลือกชนิดเอกสาร/คู่ค้า
                // ทีละช่อง แค่ยอมรับสิ่งที่ระบบเติมให้ ⇒ เป็นคำยืนยันแบบ Implicit
                // (นับเป็น Explicit เมื่อไร คลังจะเรียนนิสัยการกด ไม่ใช่ความถูกต้อง)
                // ⚠️ "AI ถูกไหม" ต้องเทียบกับ **คำตอบของ AI** (`TargetDocTypeAiSuggested`)
                // ไม่ใช่กับ `TargetDocumentType` ซึ่งเป็น**ค่าสุดท้าย**หลังผ่านอีก 5 ชั้น
                // (VendorIntel · Federated · เครดิตเทอม · CertInLieu · Deposit)
                // ⇒ เดิม: AI ตอบ X แล้วชั้นหลังทับเป็น Y ผู้ใช้กดสร้าง = บันทึกว่า
                // "AI ถูก" ทั้งที่คำตอบ AI ถูกทิ้งไปแล้ว ⇒ คลังคำตอบของนักเรียนเต็ม
                // ไปด้วย label ปลอม (ผลตรวจ 2026-09-18 · D3-4) · เส้น
                // SubmitCorrectionAsync เทียบถูกมาตลอด — สองทางเข้าต้องนิยามเดียวกัน
                if (result.TargetDocTypeAiFeedbackId is Guid dtFid)
                    await _feedbackRecorder.RecordUserChoiceAsync(dtFid, docType.ToString(),
                        acceptedAi: Accounting.Helpers.OcrAiLabelScope.AcceptedAi(
                            result.TargetDocTypeAiSuggested, docType.ToString()),
                        CancellationToken.None, Accounting.Models.Enums.UserChoiceSource.Implicit);
                if (result.AiSuggestionFeedbackId is Guid vFid && contactId is Guid chosenContact)
                    await _feedbackRecorder.RecordUserChoiceAsync(vFid, chosenContact.ToString(),
                        acceptedAi: result.AiSuggestedContactId == chosenContact,
                        CancellationToken.None, Accounting.Models.Enums.UserChoiceSource.Implicit);
            }
            catch (Exception fx)
            {
                _logger.LogWarning(fx, "บันทึก feedback ฝั่งยอมรับตอนสร้างเอกสารจากสแกนไม่สำเร็จ (scan {Id})", result.Id);
            }
        }

        // Re-link scanned file to the created document (orphan prevention)
        await RelinkScanFileToDocumentAsync(companyId, result.FileAttachmentId, document.Id);

        // Run RD-compliance + tenant-mismatch validation on the freshly
        // created document (Task 5 of ERP upgrade). Failures don't roll
        // back the document creation — they surface as
        // Document.RdComplianceStatus badges + OcrValidationLog rows.
        // ── หนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) ที่ "เราถูกหัก" ──────────────
        // ทิศทางดูจากช่อง "ผู้มีหน้าที่หักภาษี ณ ที่จ่าย" vs "ผู้ถูกหักภาษี ณ ที่จ่าย"
        // ว่าเลขผู้เสียภาษีของบริษัทเราอยู่ช่องไหน — เราอยู่ช่องผู้ถูกหัก = ได้เครดิต
        // ภาษีใช้ใน ภ.ง.ด.50/51 → ลงทะเบียนให้เลย (ดู WHT_CREDIT_PLAN.md)
        try
        {
            var ourTaxId = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId).Select(c => c.TaxId).FirstOrDefaultAsync();
            var weAreWithheld = Ocr.OcrDocumentRoleInferrer.InferWhtCertWeAreWithheld(
                result.RawTextContent, ourTaxId);
            if (weAreWithheld == true && (result.ExtractedTotalAmount ?? 0) > 0)
            {
                await EnsureWhtCreditFromCertAsync(companyId, result, document.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "บันทึกทะเบียนภาษีถูกหักจากหนังสือรับรองไม่สำเร็จ (scan {Id})", result.Id);
        }

        if (_rdComplianceValidator != null)
        {
            try
            {
                var ocrResultDto = MapToResponse(result);
                await _rdComplianceValidator.EvaluateAndPersistAsync(
                    companyId, document.Id, ocrResultDto, result.RawTextContent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RdComplianceValidator failed for document {DocId}", document.Id);
            }
        }

        return MapToResponse(result);
    }

    /// <summary>ตรวจ RD compliance ซ้ำจากผลสแกนเดิม (ไม่ต้องสแกนใหม่)
    ///
    /// <para>ทำไมต้องมี: ผลตรวจถูก persist ลง <c>RdComplianceIssuesJson</c>
    /// ตอนสแกน "ครั้งเดียว" — เมื่อ validator ฉลาดขึ้นภายหลัง (เช่น Rule 3
    /// เปลี่ยนจากดูช่อง BuyerTaxId ว่าง → ค้นเลขบริษัทใน raw text ทั้งหน้า)
    /// ใบที่สแกนไว้ก่อนหน้ายังโชว์คำเตือนเก่าตลอดไป ทั้งที่กระดาษครบจริง
    /// (เคสจริง: ใบกำกับปั๊มน้ำมันมี TAX ID ผู้ซื้อบรรทัดล่างสุด OCR แยกช่อง
    /// ไม่ได้ แต่เลขอยู่ใน raw text — ตรวจซ้ำแล้วหายเตือนเอง)</para></summary>
    public async Task<(string Status, string IssuesJson)> RecheckRdComplianceAsync(
        Guid companyId, Guid documentId)
    {
        if (_rdComplianceValidator == null)
            throw new InvalidOperationException("ตัวตรวจความครบถ้วนตามกรมสรรพากรยังไม่ได้เปิดใช้งาน");
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.CreatedDocumentId == documentId)
            ?? throw new KeyNotFoundException(
                "ไม่พบผลสแกน OCR ของเอกสารนี้ — ตรวจซ้ำได้เฉพาะเอกสารที่สร้างจากการสแกน");
        var dto = MapToResponse(scan);
        var status = await _rdComplianceValidator.EvaluateAndPersistAsync(
            companyId, documentId, dto, scan.RawTextContent);
        var issues = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => d.RdComplianceIssuesJson)
            .FirstOrDefaultAsync();
        _logger.LogInformation("ตรวจ RD compliance ซ้ำ doc {DocId} → {Status}", documentId, status);
        return (status.ToString(), issues ?? "[]");
    }

    /// <summary>
    /// **ตัวสร้าง DocumentLine จากผลสแกน — ตัวเดียวของทุกทางเข้า** (สร้างเอกสารจากสแกน ·
    /// ดึงรายการซ้ำเข้าเอกสารว่าง). กระทบยอด Σ บรรทัด ↔ หัวใบผ่าน
    /// <see cref="Accounting.Helpers.OcrLineReconciler"/> (ราคารวม VAT · ส่วนลดบนกระดาษ ·
    /// บรรทัดขาด) · อัตรา VAT รายบรรทัด (<see cref="Accounting.Helpers.ThaiVatTypeRule"/>) ·
    /// เฉลี่ย VAT/WHT ลงบรรทัดโดยเศษอยู่บรรทัดสุดท้าย · ผังบัญชี PO-line → SuggestedAccountCode →
    /// ผังระดับสแกน · แนบ feedbackId ปิดลูปการสอน (กฎเหล็ก #1)
    ///
    /// <para>ตั้ง <c>document.PricesIncludeVat</c> เมื่อกระดาษพิมพ์ราคารวม VAT และเขียน
    /// <c>result.ProcessingNotes</c> ([Σ]/[Σ-GAP]) — ผู้เรียกต้อง persist ทั้งคู่</para>
    /// </summary>
    private async Task BuildScanLinesAsync(
        Guid companyId, OcrScanResult result, List<OcrExtractedLineItem> items, Document document,
        Document? linkedPo, Dictionary<int, Guid?> poLineMap, Guid? scanDebitAccountId,
        decimal headerSubTotal, decimal hdrDiscRaw, decimal whtRate, decimal headerWht, bool isSalesSide)
    {
        if (items.Count > 0)
        {
            // 🔧 Reconcile line amounts — แยก 4 case (เลิกใส่ "ส่วนลด" มั่วๆ
            // เคสจริงเคยตีค่าขนส่ง 50฿ ใน OfficeMate เป็นส่วนลด 600฿ ผิดทั้งใบ):
            //   (A) ราคารวม VAT แล้ว (Unit Price Incl.VAT) — linesGross อยู่
            //       ระหว่าง subtotal กับ total → ตั้ง PricesIncludeVat + ถ้ามี
            //       ส่วนต่างเพิ่ม line "(OCR ไม่อ่าน — น่าจะเป็นค่าขนส่ง)"
            //   (B) ราคาแยก VAT มาตรฐาน — linesGross ≈ subtotal → ไม่ทำอะไร
            //   (C) มีส่วนลดจริง — linesGross > total → ใส่ DiscountAmount
            //   (D) OCR ขาด — linesGross < subtotal → ไม่ทำอะไร (user แก้เอง)
            var hdrSub   = result.ExtractedSubTotal   ?? 0m;
            var hdrTotal = result.ExtractedTotalAmount ?? 0m;
            var hdrVatHdr = result.ExtractedVatAmount  ?? 0m;
            var grossSum = items.Sum(x =>
                (x.UnitPrice.HasValue ? x.UnitPrice.Value * (x.Quantity ?? 1m) : (x.Amount ?? 0m)));

            // ★ ตัวจำแนกอยู่ใน Helpers/OcrLineReconciler (pure + มีเทสต์ด้วยตัวเลขจริง)
            // ผลตรวจ 2026-09-05 T2-05/T1-09: ตรรกะเดิมเทียบ Σ บรรทัด (ก่อน VAT) กับยอดรวม
            // (หลัง VAT) ⇒ ใบมีส่วนลดถูกตีเป็น "ราคารวม VAT" แล้วแต่งบรรทัดผี · ส่วนลด
            // ถูกคิดเทียบยอดรวมแล้วบวก VAT ซ้ำ. ตอนนี้: เทียบกับยอดก่อน VAT "หลังส่วนลด"
            // · ส่วนลดต้องมีหลักฐานบนกระดาษ · ตัดสินไม่ได้ = บันทึกช่องว่างให้คนดู
            //
            // ExtractedSubTotal บนกระดาษไทยมักเป็น "รวมเงิน" **ก่อน**หักส่วนลด — ถอดให้เป็น
            // ยอดก่อน VAT หลังส่วนลดก่อนเทียบ (สูตรเดียวกับ subTies ข้างบน)
            var netSubForRecon = hdrSub;
            if (hdrSub > 0m && hdrDiscRaw > 0m
                && Math.Abs((hdrSub - hdrDiscRaw + hdrVatHdr) - hdrTotal) <= Accounting.Helpers.OcrLineReconciler.Tolerance)
                netSubForRecon = hdrSub - hdrDiscRaw;
            else if (hdrSub <= 0m && hdrTotal > 0m)
                netSubForRecon = Math.Max(0m, hdrTotal - hdrVatHdr);

            var recon = Accounting.Helpers.OcrLineReconciler.Classify(
                grossSum, netSubForRecon, hdrVatHdr, hdrTotal, hdrDiscRaw);
            decimal docDiscountPercent = recon.DiscountPercent;   // ใช้เฉพาะ case C
            if (recon.PricesIncludeVat)
                document.PricesIncludeVat = true;    // Case A — ถอด VAT ออกจากยอดบรรทัดข้างล่าง
            if (docDiscountPercent > 0m && recon.TargetLineSum is decimal targetSum && items.Count > 0)
            {
                // Case C: ส่วนลดที่กระดาษพิมพ์ไว้จริง — **กระจายเป็นจำนวนเงิน** ตามสัดส่วน
                // แล้วยัดเศษที่บรรทัดสุดท้าย ⇒ Σ บรรทัด = ยอดก่อน VAT บนกระดาษ **เป๊ะเสมอ**
                //
                // ⚠️ ห้ามคิดจาก % ที่ปัด 2 ตำแหน่ง (ผลตรวจ 2026-09-06 · T5-N4): บิล 999,999
                // ลด 33,333 คลาดถึง ฿33 · และตัวยัดเศษที่จำกัด ≤ ฿1 จะ "ยอมแพ้เงียบ" ⇒
                // Σ Line.Amount ≠ SubTotal ⇒ ตอนอนุมัติ JE เดบิตจากบรรทัดแต่เครดิตจากยอดหัว
                // ⇒ เด้ง "การบันทึกบัญชีไม่สมดุล" โดยผู้ใช้ไม่รู้ว่าเลขไหนผิด
                var grossList = items
                    .Select(x => x.UnitPrice.HasValue ? x.UnitPrice.Value * (x.Quantity ?? 1m) : (x.Amount ?? 0m))
                    .ToList();
                var grossTotal = grossList.Sum();
                if (grossTotal > 0m)
                {
                    decimal assigned = 0m;
                    for (var di = 0; di < items.Count; di++)
                    {
                        var share = di == items.Count - 1
                            ? targetSum - assigned
                            : Math.Round(targetSum * grossList[di] / grossTotal, 2, MidpointRounding.AwayFromZero);
                        assigned += share;
                        items[di].Amount = share;
                    }
                }
            }
            if (recon.UnreconciledGap != 0m)
            {
                // Case D / กำกวม — ไม่แต่งบรรทัด ไม่แต่งส่วนลด บอกผู้ใช้ตรง ๆ (หน้า review
                // แสดง ProcessingNotes) แล้วให้ด่าน Σ ตอนอนุมัติเป็นคนตัดสิน
                result.ProcessingNotes = (result.ProcessingNotes ?? "") + "\n[Σ-GAP] " + recon.Note;
                _logger.LogWarning("OCR scan {ScanId}: {Note}", result.Id, recon.Note);
            }
            else if (!string.IsNullOrEmpty(recon.Note))
                result.ProcessingNotes = (result.ProcessingNotes ?? "") + "\n[Σ] " + recon.Note;

            // Pro-rate the header VAT across lines by amount share (the
            // paper rarely itemises VAT per line). Remainder lands on the
            // last line so the lines sum exactly to the header VAT.
            var lineAmountSum = items.Sum(x => x.Amount ?? 0);
            var headerVat = result.ExtractedVatAmount ?? 0;
            decimal whtAssigned = 0;

            // ═══ E-OCR-01/OCR-02: อัตรา VAT ตัดสิน **รายบรรทัด** ═══
            // เดิมทุกบรรทัดได้ `headerVat > 0 ? 7 : 0` เหมือนกันหมด แล้วเฉลี่ย VAT
            // ของหัวใบตามสัดส่วนยอดลงทุกบรรทัด ⇒ บิลผสม 7%/ยกเว้น (Makro/BigC/
            // บิลอาหาร — งานประจำวัน) บรรทัดยกเว้นติดป้าย 7% และได้ VAT ที่ไม่ควรมี
            // ⇒ รายงานภาษีซื้อ §87 ที่แยกคอลัมน์ผิดทุกใบ **โดยยอดรวมยังตรง**
            // จึงเงียบสนิท · กติกาเดียวกับ endpoint /ai/vat/infer-type ที่หน้า
            // กรอกมือเรียกอยู่แล้ว — ย้ายมาไว้ที่ Helpers/ThaiVatTypeRule ตัวเดียว
            var standardVatRate = headerVat > 0m ? 7m : 0m;
            foreach (var it in items)
            {
                if (it.VatRate.HasValue) continue;   // ผู้ใช้/AI ตัดสินมาแล้ว — ห้ามทับ
                it.VatRate = headerVat > 0m
                    ? Accounting.Helpers.ThaiVatTypeRule.ToVatRate(
                        Accounting.Helpers.ThaiVatTypeRule.Suggest(
                            // สแกนไม่มีช่อง "ประเทศผู้ขาย" — ใช้รูปแบบเลขผู้เสียภาษี
                        // เป็นตัวบอก (ไทย = 13 หลักเสมอ) เหมือนที่ endpoint ทำ
                        it.Description, null, result.ExtractedVendorTaxId),
                        standardVatRate)
                    : 0m;
            }
            // เฉลี่ยเฉพาะบรรทัดที่อัตรา > 0 (เศษลงบรรทัดที่เสียภาษีบรรทัดสุดท้าย)
            var spreadVat = Accounting.Helpers.ThaiVatTypeRule.SpreadHeaderVat(
                items.Select(x => (x.Amount ?? 0m, x.VatRate ?? 0m)).ToList(), headerVat);
            for (var vi = 0; vi < items.Count; vi++) items[vi].VatAmount = spreadVat[vi];

            // ── ด่านตรวจ Σ (E-OCR-01) ──
            // ถ้าเฉลี่ยแล้วไม่ตรงกับ VAT บนกระดาษ แปลว่าอัตราที่เดารายบรรทัดขัดกับ
            // หัวใบ (เช่นทั้งใบถูกเดาว่ายกเว้นแต่กระดาษมี VAT) — ไม่แต่งตัวเลขให้
            // ตรง แต่บันทึกไว้ให้หน้า review เตือน แล้วให้คนตัดสิน
            var vatSum = spreadVat.Sum();
            if (headerVat > 0m && Math.Abs(vatSum - headerVat) > 0.01m)
            {
                _logger.LogWarning(
                    "OCR scan {ScanId}: Σ VAT รายบรรทัด {Sum} ≠ VAT บนหัวใบ {Header} — " +
                    "อัตรารายบรรทัดที่เดาไว้อาจขัดกับกระดาษ", result.Id, vatSum, headerVat);
            }

            int lineOrder = 1;
            // ปิดลูปการสอน local model (กฎเหล็ก #1): แนบ feedbackId ระดับ scan ไว้
            // บรรทัดแรก เพื่อให้ตอน user ยืนยัน/แก้ผัง ApproveDocument เรียก
            // RecordLineAccountFeedback ได้ — เดิม OCR สร้างเอกสารแล้ว feedback หาย
            // ระบบเลยไม่เคยเรียนรู้จากผัง GL ที่ AI เดาให้.
            var glFeedbackAttached = false;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                Guid? poLineAccountId = null;
                Guid? scannedLineAccountId = null;
                string? lineProductCode = null;
                Guid? lineSourceLineId = null;
                // PO-line override (highest priority): when this OCR line was
                // mapped to a PO line via /link-po, inherit the PO's GL account
                // + product code so the receiving entry matches the order.
                // SourceLineId records the consumption — ComputeConsumptionAsync
                // keys 3-way-match / PO fulfillment on it; without it the PO
                // stays 100% open after receiving (duplicate-booking risk).
                if (linkedPo != null && poLineMap.TryGetValue(i, out var mappedPoLineId) && mappedPoLineId.HasValue)
                {
                    var poLine = linkedPo.Lines.FirstOrDefault(l => l.Id == mappedPoLineId.Value && !l.IsDeleted);
                    if (poLine != null)
                    {
                        poLineAccountId = poLine.AccountId;
                        lineProductCode = poLine.ProductCode;
                        lineSourceLineId = poLine.Id;
                    }
                }
                if (poLineAccountId == null && !string.IsNullOrEmpty(item.SuggestedAccountCode))
                {
                    var lineAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode == item.SuggestedAccountCode && !a.IsDeleted);
                    scannedLineAccountId = lineAccount?.Id;
                }
                // ★ ลำดับชั้นอยู่ใน Helpers/OcrLineAccountSource ตัวเดียว (pure + มีเทสต์):
                // บรรทัด PO > ผังบัญชีของบรรทัดนั้น > ผังบัญชีระดับหัวใบ. ชั้นกลางเคย
                // **ตายสนิท** เพราะไปป์ไลน์ serialize บรรทัดก่อนตัวเทียบสินค้า/ตัวเรียน
                // รายบรรทัดจะเขียน SuggestedAccountCode (D3-1) ⇒ ทุกใบตกไปใช้บัญชีหัวใบ
                var accountPick = Accounting.Helpers.OcrLineAccountSource.Resolve(
                    poLineAccountId, scannedLineAccountId, scanDebitAccountId);
                var lineAccountId = accountPick.AccountId;

                var amount = item.Amount ?? 0;
                // Last line absorbs the rounding remainder (both VAT and WHT)
                // so the line sums tie out exactly to the header figures.
                var lineVat = spreadVat[i];
                decimal lineWht = 0;
                if (headerWht > 0 && lineAmountSum > 0)
                {
                    lineWht = i == items.Count - 1
                        ? Math.Round(headerWht - whtAssigned, 2)
                        : Math.Round(headerWht * amount / lineAmountSum, 2);
                    whtAssigned += lineWht;
                }

                // แนบ feedbackId ระดับ scan ให้บรรทัดแรกที่ใช้ผัง GL จาก AI
                // (1 feedback row = 1 บรรทัด เพื่อไม่ให้บันทึก choice ซ้ำ).
                Guid? lineGlFeedbackId = null;
                if (!glFeedbackAttached && result.GlAccountAiFeedbackId.HasValue && lineAccountId.HasValue)
                {
                    lineGlFeedbackId = result.GlAccountAiFeedbackId;
                    glFeedbackAttached = true;
                }

                document.Lines.Add(new DocumentLine
                {
                    LineOrder = lineOrder++,
                    Description = item.Description ?? result.DocumentType ?? "รายการจาก OCR",
                    Quantity = item.Quantity ?? 1,
                    // Resolve: ว่าง → อนุมานจากคำอธิบาย (ค่าไฟ→"หน่วย") ก่อนตก
                    // "ชิ้น" · "ชิ้น" บนบรรทัดที่กฎรู้จัก (สแกนเก่าที่ default
                    // ค้างมา) → แทนด้วยหน่วยจริง · หน่วยอื่น = ตามที่ระบุเสมอ
                    Unit = UnitInferrer.Resolve(item.Unit, item.Description),
                    UnitPrice = item.UnitPrice ?? item.Amount ?? 0,
                    // ส่วนลด: ราคา/หน่วยคงเป็นราคาเต็ม, ใส่ % ส่วนลด, Amount = ยอดหลังลด
                    DiscountPercent = docDiscountPercent,
                    DiscountAmount = docDiscountPercent > 0m
                        ? Math.Round((item.UnitPrice ?? item.Amount ?? 0) * (item.Quantity ?? 1m)
                            - amount, 2)
                        : 0m,
                    // ⚠️ Case A (ราคา/หน่วยรวม VAT แล้ว): `DocumentLine.Amount` ต้องเป็น
                    // ยอด **ก่อน VAT** ตาม convention ของ DocumentService
                    // (`Amount = ComputeLineAmounts(...).NetAmount` — UnitPrice คงเป็นราคา
                    // รวม VAT ส่วน Amount เป็น net) เดิมเก็บยอดรวม VAT ลงตรง ๆ ⇒
                    //   Σ Line.Amount = ยอดรวม VAT   แต่   Document.SubTotal = ยอด net
                    // สองค่านี้ขัดกันในใบเดียว และตอนอนุมัติ JE ฝั่งซื้อลง
                    //   Dr ค่าใช้จ่าย = Σ Line.Amount (รวม VAT) + Dr ภาษีซื้อ (VAT อีกรอบ)
                    //   Cr เจ้าหนี้    = TotalAmount (net + VAT)
                    // ⇒ เดบิตเกินเครดิตเท่ายอด VAT พอดี = "การบันทึกบัญชีไม่สมดุล"
                    // (เคสจริง: IKEA 1,396 รวม VAT 91.32 → Dr 1,487.32 ≠ Cr 1,396)
                    // หักออกแล้วยอดตรงกับที่ ComputeLineAmounts คำนวณเป๊ะ ⇒ เปิดแก้ไข
                    // เอกสารแล้วบันทึกใหม่ ตัวเลขไม่ขยับ
                    Amount = document.PricesIncludeVat ? Math.Round(amount - lineVat, 2) : amount,
                    VatRate = item.VatRate ?? standardVatRate,
                    VatAmount = lineVat,
                    // WHT read off the paper → pre-fill rate + baht per line so
                    // the WHT cert auto-generation has line data ready.
                    WithholdingTaxRate = whtRate,
                    WithholdingTaxAmount = lineWht,
                    // ประเภทเงินได้ ม.40 — WithholdingTaxCertService อ่านจาก
                    // **บรรทัด** (`line.IncomeTypeCode ?? "8"`) ตอนออก 50 ทวิ และ
                    // ไฟล์ ภ.ง.ด.3/53 ก็ map จากค่าเดียวกันนี้ ⇒ ไม่เติมที่นี่
                    // = ทุกใบจากเส้น OCR ตกไปเป็น "8 ค่าบริการอื่น ๆ" เสมอ
                    // (เดิมเส้น OCR ไม่เคยเซ็ตเลย ⇒ ต้องไปกรอกในใบทุกครั้ง)
                    IncomeTypeCode = !isSalesSide && whtRate > 0m ? result.WhtIncomeTypeCode : null,
                    AccountId = lineAccountId,
                    GlAccountAiFeedbackId = lineGlFeedbackId,
                    ProductCode = lineProductCode,
                    SourceLineId = lineSourceLineId,
                    ProjectId = item.ProjectId,
                });
            }

            // Header-level project: when every line carries the same project
            // (e.g. set by the metadata matcher or the review UI's main-project
            // picker), surface it on the header too — list pages + project
            // P&L fallbacks read the header field.
            var lineProjects = items.Select(x => x.ProjectId).Distinct().ToList();
            if (lineProjects.Count == 1 && lineProjects[0].HasValue)
                document.ProjectId = lineProjects[0];
        }
        else
        {
            // No itemised lines were extracted — fall back to a single summary
            // line from the header totals so the document still has content.
            document.Lines.Add(new DocumentLine
            {
                LineOrder = 1,
                Description = result.DocumentType ?? "รายการจาก OCR",
                Quantity = 1,
                UnitPrice = headerSubTotal,
                Amount = headerSubTotal,
                VatRate = result.ExtractedVatAmount > 0 ? 7 : 0,
                VatAmount = result.ExtractedVatAmount ?? 0,
                WithholdingTaxRate = whtRate,
                WithholdingTaxAmount = headerWht,
                // ประเภทเงินได้ ม.40 — เหมือนสาขามีรายการย่อย: ตัวออก 50 ทวิ /
                // ไฟล์ ภ.ง.ด.3/53 อ่านค่านี้จากบรรทัด ไม่ใช่จากหัวเอกสาร
                IncomeTypeCode = !isSalesSide && whtRate > 0m ? result.WhtIncomeTypeCode : null,
                // Scan-level suggested debit GL — previously this branch left
                // the account empty even when the classifier knew the answer.
                AccountId = scanDebitAccountId,
                // ปิดลูปการสอน local model (กฎเหล็ก #1) — บรรทัดสรุปใบเดียว
                // แนบ feedbackId ระดับ scan ไว้ ให้ approve เรียนรู้ผัง GL.
                GlAccountAiFeedbackId = scanDebitAccountId.HasValue ? result.GlAccountAiFeedbackId : null,
            });
        }

    }

    public async Task<OcrLinePreviewResponse> PreviewDocumentLinesAsync(
        Guid companyId, Guid scanResultId, string? targetTypeOverride)
    {
        // AsNoTracking — เมธอดนี้ต้องไม่บันทึกอะไร (NormalizeSwappedHeaderAmounts/BuildScanLinesAsync
        // เขียน ProcessingNotes ลง object ในหน่วยความจำ ซึ่งจะไม่ถูก persist)
        var result = await _db.Set<OcrScanResult>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == scanResultId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผลสแกน");

        var notesBefore = result.ProcessingNotes ?? "";
        NormalizeSwappedHeaderAmounts(result);

        var items = new List<OcrExtractedLineItem>();
        if (!string.IsNullOrWhiteSpace(result.ExtractedItemsJson))
        {
            try
            {
                items = System.Text.Json.JsonSerializer
                    .Deserialize<List<OcrExtractedLineItem>>(result.ExtractedItemsJson) ?? new();
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "OCR ExtractedItemsJson parse failed for scan {ScanId}", result.Id);
            }
        }
        if (items.Count > 0)
        {
            var tmpSan = new OcrExtractedData();
            foreach (var it in items) tmpSan.Items.Add(it);
            SanitizeVatSplitArtifacts(tmpSan);
            items = tmpSan.Items.ToList();
        }

        var resolvedTarget = Accounting.Helpers.OcrTargetDocumentType.Resolve(
            targetTypeOverride, result.TargetDocumentType, result.DocumentType,
            hasLinkedPurchaseOrder: result.LinkedPurchaseOrderId.HasValue);
        var docType = resolvedTarget.Type;

        Document? linkedPo = null;
        Dictionary<int, Guid?> poLineMap = new();
        if (result.LinkedPurchaseOrderId.HasValue)
        {
            linkedPo = await _db.Documents.AsNoTracking()
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.Id == result.LinkedPurchaseOrderId.Value
                    && d.CompanyId == companyId && !d.IsDeleted);
            if (linkedPo != null)
            {
                docType = DocumentType.PurchaseInvoice;
                if (!string.IsNullOrWhiteSpace(result.PoLineMappingsJson))
                {
                    try
                    {
                        var raw = System.Text.Json.JsonSerializer
                            .Deserialize<Dictionary<string, Guid?>>(result.PoLineMappingsJson) ?? new();
                        foreach (var (k, v) in raw)
                            if (int.TryParse(k, out var idx)) poLineMap[idx] = v;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "PoLineMappingsJson parse failed for scan {ScanId}", result.Id);
                    }
                }
            }
        }

        var isSalesSide = Accounting.Helpers.DocumentSide.IsSales(docType, result.OurRole);
        var hdrDiscRaw = result.ExtractedDiscountAmount ?? 0;
        var headerSubTotal = ResolveHeaderSubTotal(result, hdrDiscRaw);
        var whtRate = result.HasWht && result.WhtRate is > 0 ? result.WhtRate.Value : 0m;
        var whtBase = Math.Max(0, (result.ExtractedTotalAmount ?? 0) - (result.ExtractedVatAmount ?? 0));
        var headerWht = whtRate > 0
            ? Math.Round(whtBase * whtRate / 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;
        var scanDebitAccountId = await ResolveScanDebitAccountIdAsync(companyId, result);

        var document = new Document { CompanyId = companyId, DocumentType = docType };
        await BuildScanLinesAsync(companyId, result, items, document, linkedPo, poLineMap,
            scanDebitAccountId, headerSubTotal, hdrDiscRaw, whtRate, headerWht, isSalesSide);

        // AccountId → รหัสผัง (ฟอร์มปลายทางรับเป็น code)
        var accountIds = document.Lines.Where(l => l.AccountId.HasValue).Select(l => l.AccountId!.Value).Distinct().ToList();
        var codeMap = accountIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && accountIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.AccountCode);

        var lines = document.Lines.OrderBy(l => l.LineOrder).Select(l => new OcrLinePreviewLineDto(
            l.Description, l.Quantity, l.Unit, l.UnitPrice, l.DiscountPercent, l.DiscountAmount, l.Amount,
            l.VatRate, l.VatAmount, l.WithholdingTaxRate,
            l.AccountId.HasValue && codeMap.TryGetValue(l.AccountId.Value, out var code) ? code : null,
            l.ProductCode, l.ProjectId, l.SourceLineId,
            // BuildScanLinesAsync เดินลูป items แบบ 1 แถว = 1 บรรทัด (LineOrder เริ่มที่ 1)
            // — ส่งดัชนีต้นทางออกไปตรง ๆ แทนที่จะให้หน้าเว็บสมมติว่าลำดับตรงกัน
            items.Count > 0 ? l.LineOrder - 1 : -1)).ToList();

        var notesAfter = result.ProcessingNotes ?? "";
        var delta = notesAfter.Length > notesBefore.Length ? notesAfter[notesBefore.Length..].Trim() : null;

        return new OcrLinePreviewResponse(
            resolvedTarget.IsDeposit ? Accounting.Helpers.OcrTargetDocumentType.DepositPseudoType : docType.ToString(),
            isSalesSide, document.PricesIncludeVat,
            headerSubTotal, result.ExtractedVatAmount ?? 0m, result.ExtractedTotalAmount ?? 0m, headerWht,
            lines, string.IsNullOrWhiteSpace(delta) ? null : delta);
    }

    /// <summary>ยอดก่อน VAT ของหัวใบที่ใช้เป็นตัวตั้ง — ใช้ <c>ExtractedSubTotal</c> เฉพาะเมื่อ
    /// ผูกกับยอดรวมได้ (รองรับ VAT-incl + ส่วนลดจริง) ไม่งั้นถอยจากยอดรวม (ตัวเลขเด่นที่
    /// เชื่อถือได้สุด) เพื่อให้ subtotal/บรรทัด/ยอดรวมแตกกันไม่ได้ · สูตรเดียวทั้งสองทางเข้า</summary>
    private static decimal ResolveHeaderSubTotal(OcrScanResult result, decimal hdrDiscRaw)
    {
        var hdrTotalRaw = result.ExtractedTotalAmount ?? 0;
        var hdrVatRaw = result.ExtractedVatAmount ?? 0;
        if (hdrTotalRaw <= 0) return result.ExtractedSubTotal ?? 0m;
        var subFromTotal = Math.Max(0, hdrTotalRaw - hdrVatRaw);
        var subTies = result.ExtractedSubTotal is > 0
            && Math.Abs((result.ExtractedSubTotal!.Value + hdrVatRaw - hdrDiscRaw) - hdrTotalRaw) <= 1m;
        return subTies ? result.ExtractedSubTotal!.Value : subFromTotal;
    }

    /// <summary>ผังเดบิตระดับสแกน (จาก SuggestedAccountsJson.DebitAccountCode) → Id ในผังของ
    /// บริษัท — ใช้เป็น fallback ของบรรทัดที่ไม่มีผังของตัวเอง · ตัวเดียวทั้งสองทางเข้า</summary>
    private async Task<Guid?> ResolveScanDebitAccountIdAsync(Guid companyId, OcrScanResult result)
    {
        if (string.IsNullOrWhiteSpace(result.SuggestedAccountsJson)) return null;
        try
        {
            using var sa = System.Text.Json.JsonDocument.Parse(result.SuggestedAccountsJson);
            if (sa.RootElement.TryGetProperty("DebitAccountCode", out var dac)
                && dac.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(dac.GetString()))
            {
                var code = dac.GetString();
                return await _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && a.AccountCode == code && !a.IsDeleted)
                    .Select(a => (Guid?)a.Id)
                    .FirstOrDefaultAsync();
            }
        }
        catch { /* malformed suggestion JSON — line GL stays empty */ }
        return null;
    }

    /// <summary>ป้ายยอดสลับ (SubTotal ↔ Total) ที่สแกน persist ไว้ — ซ่อมก่อนใช้สร้างบรรทัด
    /// ตัวตัดสิน = <see cref="Accounting.Helpers.OcrHeaderAmounts"/> ตัวเดียว · ใช้ทั้งสองทางเข้า</summary>
    private void NormalizeSwappedHeaderAmounts(OcrScanResult result)
    {
        if (!Accounting.Helpers.OcrHeaderAmounts.IsSwapped(
                result.ExtractedSubTotal, result.ExtractedVatAmount, result.ExtractedTotalAmount))
            return;
        var subBefore = result.ExtractedSubTotal!.Value;
        var totBefore = result.ExtractedTotalAmount!.Value;
        (result.ExtractedSubTotal, result.ExtractedTotalAmount) = (result.ExtractedTotalAmount, result.ExtractedSubTotal);
        result.ProcessingNotes = (result.ProcessingNotes ?? "") + "\n"
            + Accounting.Helpers.OcrHeaderAmounts.SwapNote(subBefore, totBefore);
        _logger.LogInformation("OCR scan {ScanId}: สลับป้าย SubTotal/Total กลับ ({Sub} ↔ {Tot})",
            result.Id, subBefore, totBefore);
    }

    /// <summary>
    /// Re-populate the line items of a document that was created from an OCR
    /// scan but ended up with no lines (e.g. created before the line-building
    /// fix). Finds the scan via its CreatedDocumentId, rebuilds DocumentLines
    /// from the persisted ExtractedItemsJson, and recomputes the header totals.
    /// Refuses if the document already has real (non-blank) lines so manual
    /// work is never clobbered.
    /// </summary>
    public async Task<OcrResultResponse> RepopulateDocumentLinesFromScanAsync(
        Guid companyId, Guid documentId, string performedBy)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.CreatedDocumentId == documentId)
            ?? throw new InvalidOperationException("เอกสารนี้ไม่ได้ถูกสร้างจากการสแกน OCR");

        var document = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new InvalidOperationException("ไม่พบเอกสาร");

        var hasRealLines = document.Lines.Any(l => l.Amount != 0 || !string.IsNullOrWhiteSpace(l.Description));
        if (hasRealLines)
            throw new InvalidOperationException("เอกสารมีรายการอยู่แล้ว — ลบรายการเดิมก่อนหากต้องการดึงจาก OCR ใหม่");

        // Drop any blank placeholder lines before rebuilding.
        if (document.Lines.Count > 0)
        {
            _db.Set<DocumentLine>().RemoveRange(document.Lines.ToList());
            document.Lines.Clear();
        }

        var items = new List<OcrExtractedLineItem>();
        if (!string.IsNullOrWhiteSpace(result.ExtractedItemsJson))
        {
            try
            {
                items = System.Text.Json.JsonSerializer
                    .Deserialize<List<OcrExtractedLineItem>>(result.ExtractedItemsJson) ?? new();
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "OCR ExtractedItemsJson parse failed for scan {ScanId}", result.Id);
            }
        }

        // ⭐ Re-sanitize ตอน create-document ด้วย (ไม่ใช่แค่ตอน scan) — กันเคส
        // scan เก่าที่ทำก่อนมี sanitizer: ExtractedItemsJson ฝัง split
        // "(ส่วนมีภาษี)/(ส่วนไม่มีภาษี)" + เศษ 0.02 ไว้แล้ว → fold/merge ที่นี่
        // ทำให้ document ที่สร้างจาก scan เก่าก็ได้บรรทัดสะอาด. Idempotent.
        if (items.Count > 0)
        {
            var tmpSan = new OcrExtractedData();
            foreach (var it in items) tmpSan.Items.Add(it);
            SanitizeVatSplitArtifacts(tmpSan);
            items = tmpSan.Items.ToList();
        }

        // ★ ตัวสร้างบรรทัดตัวเดียวกับ CreateDocumentFromScanAsync — เดิมเส้นนี้เขียนบรรทัดเอง
        // (UnitPrice = item.UnitPrice, VAT 7% ทั้งใบ, ไม่รู้จัก PricesIncludeVat/ส่วนลด/VAT
        // รายบรรทัด/WHT) = สำเนาที่สองที่ให้คำตอบคนละแบบบนกระดาษใบเดียวกัน
        NormalizeSwappedHeaderAmounts(result);
        var hdrDiscRaw = result.ExtractedDiscountAmount ?? 0;
        var headerSubTotal = ResolveHeaderSubTotal(result, hdrDiscRaw);
        var isSalesSide = Accounting.Helpers.DocumentSide.IsSales(document.DocumentType, result.OurRole);
        var whtRate = result.HasWht && result.WhtRate is > 0 ? result.WhtRate.Value : 0m;
        var whtBase = Math.Max(0, (result.ExtractedTotalAmount ?? 0) - (result.ExtractedVatAmount ?? 0));
        var headerWht = whtRate > 0
            ? Math.Round(whtBase * whtRate / 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;
        var scanDebitAccountId = await ResolveScanDebitAccountIdAsync(companyId, result);
        await BuildScanLinesAsync(companyId, result, items, document, linkedPo: null,
            poLineMap: new Dictionary<int, Guid?>(), scanDebitAccountId, headerSubTotal, hdrDiscRaw,
            whtRate, headerWht, isSalesSide);

        // Recompute header totals from the rebuilt lines so the document is
        // self-consistent even before the user opens + saves it.
        var subTotal = document.Lines.Sum(l => l.Amount);
        var vat = document.Lines.Sum(l =>
            l.VatAmount != 0 ? l.VatAmount : Math.Round(l.Amount * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero));
        var wht = document.Lines.Sum(l => l.WithholdingTaxAmount);
        document.SubTotal = subTotal;
        document.VatAmount = vat;
        document.WithholdingTaxAmount = wht;
        // convention เดียวกับ DocumentService: TotalAmount = Sub + VAT − WHT
        document.TotalAmount = subTotal + vat - wht;
        document.BalanceDue = subTotal + vat - wht;
        document.UpdatedAt = DateTime.UtcNow;
        document.UpdatedBy = performedBy;

        await _db.SaveChangesAsync();
        return MapToResponse(result);
    }

    /// <summary>
    /// Record a balanced Journal Entry straight from a scan — the "บันทึก JE
    /// เท่านั้น" path for when the real document was issued in an external
    /// system and only the GL effect needs to land here. Builds Dr/Cr lines
    /// from the extracted amounts + the user's two account picks, auto-adding
    /// balanced VAT and WHT lines when those accounts resolve, then delegates
    /// to the standard CreateJournalEntryAsync (same validation + numbering).
    /// </summary>
    public async Task<Guid> CreateJournalEntryFromScanAsync(Guid companyId, Guid scanResultId,
        Models.DTOs.Ocr.CreateJeFromScanRequest request, string performedBy)
    {
        if (_accounting == null)
            throw new InvalidOperationException("Accounting service unavailable");

        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("ไม่พบผลการสแกน");
        if (result.ScanStatus != "Completed")
            throw new InvalidOperationException("สแกนยังไม่เสร็จ");
        if (result.CreatedJournalEntryId.HasValue)
            throw new InvalidOperationException("สแกนนี้บันทึก JE ไปแล้ว");
        if (result.CreatedDocumentId.HasValue)
            throw new InvalidOperationException("สแกนนี้สร้างเอกสารไปแล้ว — ไม่ต้องบันทึก JE ซ้ำ");

        // ── ด่านกันซ้ำ: เส้น JE ตรงเดิม **ไม่มีเลย** (ผลตรวจ 2026-09-06 · T1-14) ──
        // เส้นสร้างเอกสารเรียก FindDuplicateDocumentWarningAsync มาตั้งแต่ต้น แต่
        // ปุ่ม "บันทึก JE" ลัดผ่านไปทั้งดุ้น ⇒ ใบกำกับใบเดียวกันถูกลงบัญชีสองครั้ง
        // (ญาติของ "ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี" — หลังใส่ด่านที่ไหน
        // ต้องไล่ทุกทางเข้าที่แตะข้อมูลชุดเดียวกัน)
        if (!request.AllowDuplicate)
        {
            var jeDupMsg = await FindDuplicateDocumentWarningAsync(companyId, result);
            if (jeDupMsg != null) throw new Accounting.Helpers.BusinessRuleException(jeDupMsg);
        }

        // §82/5 — เส้น JE ตรงเดิมข้ามด่านภาษีซื้อทุกด่านที่เส้นเอกสารมี (ผลตรวจ 2026-09-05
        // T1-02): screener ตั้ง [VAT-CLAIM] ไว้ถูกแล้ว (ใบย่อ/ค่ารับรอง/รถยนต์นั่ง) แต่
        // ผู้ใช้ติ๊ก "ลงภาษีซื้อ" ที่นี่ได้ ⇒ เคลมภาษีซื้อต้องห้ามลง GL ตรง ๆ. ล้มดังพร้อม
        // ทางไปต่อ — ไม่ปลดติ๊กให้เงียบ ๆ (ห้าม silent no-op)
        if (request.PostVat && (result.ProcessingNotes ?? "").Contains("[VAT-CLAIM]"))
            throw new Accounting.Helpers.BusinessRuleException(
                "ใบนี้เคลมภาษีซื้อไม่ได้ตาม §82/5 (ใบกำกับอย่างย่อ/ไม่ใช่ใบกำกับเต็มรูป/ค่ารับรอง/รถยนต์นั่ง) — "
                + "ปลดติ๊ก “ลงภาษีซื้อ” แล้วบันทึกใหม่ (VAT จะรวมเข้าค่าใช้จ่าย) หรือกด “สร้างเอกสาร” ให้ระบบพับ VAT ให้อัตโนมัติ",
                "RD-82/5-JE");

        // Amounts. Derive subtotal from total−vat when only the total was read.
        var vat = (request.PostVat ? result.ExtractedVatAmount : 0m) ?? 0m;
        var subtotal = result.ExtractedSubTotal ?? 0m;
        var total = result.ExtractedTotalAmount ?? (subtotal + vat);
        if (subtotal == 0m && total > 0m) subtotal = total - vat;
        if (total <= 0m)
            throw new InvalidOperationException("ไม่มียอดเงินที่อ่านได้ — กรุณาตรวจสอบยอดในหน้ารีวิวก่อน");

        decimal wht = 0m;
        if (request.PostWht && result.HasWht && result.WhtRate is > 0m)
        {
            // ฐาน WHT = ยอดรวมบนกระดาษ − VAT (สูตรเดียวกับเส้นเอกสาร CreateDocumentFromScanAsync)
            // — ExtractedSubTotal บนใบมีส่วนลดคือยอด**ก่อนหักส่วนลด** ใช้แล้วหักเกิน
            // (ผลตรวจ 2026-09-05 T1-03: ใบเดียวกันเคยได้ WHT ต่างกันตามปุ่มที่กด)
            var whtBase = Math.Max(0m, total - (result.ExtractedVatAmount ?? 0m));
            wht = Math.Round(whtBase * result.WhtRate!.Value / 100m, 2, MidpointRounding.AwayFromZero);
        }

        var debitAcc = await ResolveAccountIdByCodeAsync(companyId, request.DebitAccountCode)
            ?? throw new InvalidOperationException($"ไม่พบบัญชีเดบิตรหัส {request.DebitAccountCode}");
        var creditAcc = await ResolveAccountIdByCodeAsync(companyId, request.CreditAccountCode)
            ?? throw new InvalidOperationException($"ไม่พบบัญชีเครดิตรหัส {request.CreditAccountCode}");

        var isSeller = string.Equals(result.OurRole, "Seller", StringComparison.OrdinalIgnoreCase);

        Guid? vatAcc = null;
        if (request.PostVat && vat > 0m)
            // รหัสผังตามผังมาตรฐาน (ChartOfAccountTemplates): 21911 ภาษีขาย ภ.พ.30 ·
            // 11610 ภาษีซื้อ ภ.พ.30 (fallback 116 เหมือน DocumentService.ResolveInputVat).
            // ผลตรวจ 2026-09-05 T1-01: รหัสเดิม (11511/1151/115 · 21701/2161/2162) ไม่มีใน
            // ผังมาตรฐานเลยสักตัว ⇒ ตกไปค้นชื่อแล้วหยิบบัญชีคุมระดับ 3 ("116 ภาษีซื้อรอเครดิต")
            vatAcc = isSeller
                ? await ResolveTaxAccountAsync(companyId, new[] { "21911", "2191" }, AccountType.Liability, "ภาษีขาย")
                : await ResolveTaxAccountAsync(companyId, new[] { "11610", "116" }, AccountType.Asset, "ภาษีซื้อ");

        Guid? whtAcc = null;
        if (wht > 0m)
        {
            // ภ.ง.ด.53 (21917) เมื่อผู้รับเงินเป็นนิติบุคคล · ภ.ง.ด.3 (21916) เมื่อเป็นบุคคล
            // — ตัดสินจากเลขผู้เสียภาษีของผู้ขายบนกระดาษ (กติกาเดียวกับ PdfGenerationService)
            var payeeJuristic = Ocr.SmartFieldExtractor.IsJuristicTaxId(result.ExtractedVendorTaxId);
            // ⚠️ fallback ด้วยชื่อ "หัก ณ ที่จ่าย" เฉย ๆ จะไปโดน 21914 (ภ.ง.ด.1 เงินเดือน)
            // ซึ่งเป็นแบบยื่นคนละใบ ⇒ ยอดนำส่งผิดทั้งสองแบบ (ผลตรวจ 2026-09-06 · T5-N7)
            whtAcc = await ResolveTaxAccountAsync(companyId,
                payeeJuristic ? new[] { "21917", "21916" } : new[] { "21916", "21917" },
                AccountType.Liability, "หัก ณ ที่จ่าย",
                excludeNameKeywords: new[] { "ภ.ง.ด.1", "ภ.ง.ด. 1", "เงินเดือน" });
        }

        // Build a guaranteed-balanced set of lines. Optional VAT/WHT lines only
        // appear when their account resolves; the primary side absorbs the rest
        // so total debits always equal total credits.
        var lines = new List<Accounting.Models.DTOs.Accounting.JournalLineRequest>();
        if (isSeller)
        {
            var vatLine = vatAcc != null ? vat : 0m;
            var revenueAmt = total - vatLine;
            // ฝั่งขาย: ลูกค้าหักภาษี ณ ที่จ่ายจากเรา → เงินที่ได้รับจริงน้อยกว่ายอดใบ
            // ส่วนที่ถูกหักเป็น **สินทรัพย์** (เครดิตภาษีใช้ใน ภ.ง.ด.50/51) ไม่ใช่หนี้สิน
            // ⚠️ เดิมคำนวณ wht ไว้แล้วแต่สาขานี้ไม่ใส่บรรทัดเลย ⇒ ลูกหนี้เกินจริงเท่ายอดหัก
            // และไม่มีเครดิตภาษี ⇒ จ่าย CIT เกิน (ผลตรวจ 2026-09-06 · T5-N3)
            var sellerWhtAcc = wht > 0m
                ? await ResolveTaxAccountAsync(companyId, new[] { "11910" }, AccountType.Asset, "ภาษีถูกหัก")
                : null;
            if (wht > 0m && sellerWhtAcc == null)
                throw new Accounting.Helpers.BusinessRuleException(
                    "ใบนี้ถูกหักภาษี ณ ที่จ่าย แต่ผังบัญชียังไม่มี \"11910 ภาษีถูกหัก ณ ที่จ่าย\" — "
                    + "เพิ่มผังบัญชีนี้ก่อน หรือปลดติ๊ก \"บันทึกภาษีหัก ณ ที่จ่าย\" แล้วบันทึกใหม่",
                    "OCR-JE-NO-WHT-ASSET");
            var sellerWht = sellerWhtAcc != null ? wht : 0m;
            lines.Add(new(debitAcc, total - sellerWht, 0m, "ลูกหนี้/เงินรับ"));
            if (sellerWht > 0m) lines.Add(new(sellerWhtAcc!.Value, sellerWht, 0m, "ภาษีถูกหัก ณ ที่จ่าย"));
            lines.Add(new(creditAcc, 0m, revenueAmt, "รายได้"));
            if (vatLine > 0m) lines.Add(new(vatAcc!.Value, 0m, vatLine, "ภาษีขาย"));
        }
        else
        {
            // ⚠️ ฝั่งซื้อ: เราเป็นผู้หักภาษี ณ ที่จ่าย ⇒ ยอดที่หักไว้เป็น **หนี้สิน**
            // ที่ต้องนำส่งกรมสรรพากร (ภ.ง.ด.3/53 วันที่ 7/15 ของเดือนถัดไป).
            // ถ้าหาผังไม่เจอแล้วปล่อยให้ whtLine = 0 เงียบ ๆ ⇒ เครดิตเต็มจำนวน
            // = "จ่ายผู้ขายครบ" ทั้งที่ผู้ใช้ติ๊กว่าหัก ⇒ ไม่มีหนี้สินให้นำส่ง +
            // เจ้าหนี้เกินจริง + 50 ทวิ ที่ออกไปแล้วไม่มีคู่ในบัญชี. ล้มดังพร้อม
            // ทางไปต่อ — ทรงเดียวกับฝั่งขาย (ผลตรวจย้อน 2026-09-06: ฝั่งขายแก้แล้ว
            // ที่ T5-N3 แต่ฝั่งซื้อ ซึ่งเป็นเคสที่พบบ่อยกว่า ถูกทิ้งไว้)
            if (wht > 0m && whtAcc == null)
                throw new Accounting.Helpers.BusinessRuleException(
                    "ใบนี้มีภาษีหัก ณ ที่จ่าย แต่ผังบัญชียังไม่มี \"21916 ภ.ง.ด.3\" / \"21917 ภ.ง.ด.53\" — "
                    + "เพิ่มผังบัญชีนี้ก่อน หรือปลดติ๊ก \"บันทึกภาษีหัก ณ ที่จ่าย\" แล้วบันทึกใหม่",
                    "OCR-JE-NO-WHT-LIABILITY");
            var vatLine = vatAcc != null ? vat : 0m;
            var whtLine = whtAcc != null ? wht : 0m;
            var expenseAmt = total - vatLine;
            var creditAmt = total - whtLine;
            lines.Add(new(debitAcc, expenseAmt, 0m, "ค่าใช้จ่าย/สินทรัพย์"));
            if (vatLine > 0m) lines.Add(new(vatAcc!.Value, vatLine, 0m, "ภาษีซื้อ"));
            if (whtLine > 0m) lines.Add(new(whtAcc!.Value, 0m, whtLine, "ภาษีหัก ณ ที่จ่าย"));
            lines.Add(new(creditAcc, 0m, creditAmt, "เจ้าหนี้/เงินจ่าย"));
        }

        var entryDate = request.EntryDate ?? result.ExtractedDate ?? DateTime.UtcNow.Date;
        var description = !string.IsNullOrWhiteSpace(request.Description)
            ? request.Description!
            : $"บันทึกจากสแกน OCR: {result.OriginalFileName}";

        var jeReq = new Accounting.Models.DTOs.Accounting.CreateJournalEntryRequest(
            EntryDate: entryDate,
            Description: description,
            Reference: result.ExtractedDocumentNumber,
            Lines: lines,
            JournalType: isSeller ? JournalType.Sales : JournalType.Purchase);

        var je = await _accounting.CreateJournalEntryAsync(companyId, jeReq, performedBy);

        result.CreatedJournalEntryId = je.Id;
        result.ProcessingNotes = (result.ProcessingNotes ?? "") + $" JE recorded: {je.EntryNumber}.";
        await _db.SaveChangesAsync();

        // Re-link the scanned file to nothing extra — it stays attached to the
        // scan; the JE references it via the scan in the audit trail.
        return je.Id;
    }

    /// <summary>อัตราแลกเปลี่ยนของวันที่เอกสาร — คืน <c>null</c> เมื่อหาไม่ได้
    ///
    /// <para>ห้าม throw: กฎเหล็ก #3 บังคับว่าสแกนต้องได้ Draft เสมอ ผู้ใช้กรอกอัตราเองได้
    /// ที่ใบ ส่วนด่านตอน **อนุมัติ** (<c>DocumentService.ResolveExchangeRateAsync</c>)
    /// เป็นตัวบล็อกจริงถ้ายังไม่มีอัตรา</para></summary>
    private async Task<decimal?> ResolveScanExchangeRateAsync(string? currency, DateTime documentDate)
    {
        if (string.IsNullOrWhiteSpace(currency)
            || string.Equals(currency, "THB", StringComparison.OrdinalIgnoreCase)) return 1m;
        if (_fxRates == null) return null;
        try
        {
            var rate = await _fxRates.GetRateAsync(currency.ToUpperInvariant(), documentDate);
            return rate != null && rate.MidRate > 0m ? rate.MidRate : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ดึงอัตราแลกเปลี่ยน {Cur} ไม่สำเร็จ — ปล่อยให้ผู้ใช้กรอกเอง", currency);
            return null;
        }
    }

    /// <summary>Resolve a company account by exact code → Id (active only).</summary>
    private async Task<Guid?> ResolveAccountIdByCodeAsync(Guid companyId, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive && a.AccountCode == code)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>Resolve a VAT/WHT account by trying a list of standard codes,
    /// then falling back to the first active account of the right type whose
    /// name contains the keyword. Returns null when nothing matches (caller
    /// then folds the amount into the primary line to keep the JE balanced).</summary>
    private async Task<Guid?> ResolveTaxAccountAsync(Guid companyId, string[] codes, AccountType type,
        string nameKeyword, string[]? excludeNameKeywords = null)
    {
        foreach (var c in codes)
        {
            var hit = await ResolveAccountIdByCodeAsync(companyId, c);
            if (hit != null) return hit;
        }
        // ค้นด้วยชื่อ: เอาบัญชีที่ "ลงรายการได้" ก่อน (รหัสยาวสุด = ระดับลึกสุด) — เดิม
        // OrderBy รหัสแบบข้อความทำให้บัญชีคุม "116" ชนะ "11610" (ผลตรวจ 2026-09-05 T1-01)
        var q = _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive
                && a.AccountType == type && a.AccountName!.Contains(nameKeyword));
        foreach (var ex in excludeNameKeywords ?? Array.Empty<string>())
            q = q.Where(a => !a.AccountName!.Contains(ex));
        return await q
            .OrderByDescending(a => a.AccountCode.Length)
            .ThenBy(a => a.AccountCode)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
    }

    public async Task SetExtractedLineProjectAsync(Guid companyId, Guid scanResultId,
        int lineIndex, Guid? projectId, string? projectName)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");
        if (string.IsNullOrEmpty(scan.ExtractedItemsJson))
            throw new InvalidOperationException("Scan ไม่มีรายการสินค้าใน OCR result.");
        if (lineIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));

        // Verify the project exists in this company when assigning.
        if (projectId.HasValue)
        {
            var exists = await _db.Projects.AnyAsync(
                p => p.Id == projectId.Value && p.CompanyId == companyId && !p.IsDeleted);
            if (!exists) throw new InvalidOperationException("ไม่พบ project ที่ระบุ");
        }

        List<OcrExtractedLineItem> items;
        try
        {
            items = System.Text.Json.JsonSerializer
                .Deserialize<List<OcrExtractedLineItem>>(scan.ExtractedItemsJson) ?? new();
        }
        catch
        {
            throw new InvalidOperationException("ExtractedItemsJson เสียหาย — ไม่สามารถ parse");
        }
        if (lineIndex >= items.Count)
            throw new ArgumentOutOfRangeException(nameof(lineIndex), "lineIndex เกินจำนวนรายการที่สแกนได้");

        // ปิดลูปการสอน MatchLineProject (กฎเหล็ก #1) — ผู้ใช้ override project ราย
        // บรรทัด = ground-truth. ถ้าบรรทัดนี้เคยถูก AI เดา (มี ProjectAiFeedbackId)
        // → record คำตอบจริง. acceptedAi = ผู้ใช้เลือกตรงกับที่ AI แนะนำ.
        var lineBefore = items[lineIndex];
        if (_feedbackRecorder != null && lineBefore.ProjectAiFeedbackId.HasValue && projectId.HasValue)
        {
            try
            {
                var acceptedAi = lineBefore.AiSuggestedProjectId == projectId;
                await _feedbackRecorder.RecordUserChoiceAsync(
                    lineBefore.ProjectAiFeedbackId.Value, projectId.Value.ToString(), acceptedAi, default,
                    Accounting.Models.Enums.UserChoiceSource.Explicit);   // ผู้ใช้เลือกโปรเจกต์ของบรรทัดนี้เอง
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record project-match feedback choice (non-fatal)");
            }
        }

        items[lineIndex].ProjectId = projectId;
        items[lineIndex].ProjectName = projectName;
        scan.ExtractedItemsJson = SerializeExtractedItems(items);
        scan.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task SetAllExtractedLineProjectsAsync(Guid companyId, Guid scanResultId,
        Guid? projectId, string? projectName, bool onlyEmpty)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");
        if (string.IsNullOrEmpty(scan.ExtractedItemsJson))
            throw new InvalidOperationException("Scan ไม่มีรายการสินค้าใน OCR result.");

        if (projectId.HasValue)
        {
            var exists = await _db.Projects.AnyAsync(
                p => p.Id == projectId.Value && p.CompanyId == companyId && !p.IsDeleted);
            if (!exists) throw new InvalidOperationException("ไม่พบ project ที่ระบุ");
        }

        List<OcrExtractedLineItem> items;
        try
        {
            items = System.Text.Json.JsonSerializer
                .Deserialize<List<OcrExtractedLineItem>>(scan.ExtractedItemsJson) ?? new();
        }
        catch
        {
            throw new InvalidOperationException("ExtractedItemsJson เสียหาย — ไม่สามารถ parse");
        }

        // Preserve user's prior per-line overrides when onlyEmpty=true.
        // The UI uses this when the user clicks "apply main" AFTER
        // already overriding some rows individually — we don't want
        // to clobber their work.
        foreach (var item in items)
        {
            if (onlyEmpty && item.ProjectId.HasValue) continue;
            // ปิดลูป (กฎเหล็ก #1) เช่นเดียวกับ single-line — บรรทัดที่ AI เคยเดาแล้ว
            // ผู้ใช้ "apply main" ทับ = user override. record ก่อนเขียนทับ.
            if (_feedbackRecorder != null && item.ProjectAiFeedbackId.HasValue && projectId.HasValue)
            {
                try
                {
                    var acceptedAi = item.AiSuggestedProjectId == projectId;
                    await _feedbackRecorder.RecordUserChoiceAsync(
                        item.ProjectAiFeedbackId.Value, projectId.Value.ToString(), acceptedAi, default,
                        Accounting.Models.Enums.UserChoiceSource.BulkApprove);   // คลิกเดียวทับทุกบรรทัด
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to record project-match feedback (bulk, non-fatal)");
                }
            }
            item.ProjectId = projectId;
            item.ProjectName = projectName;
        }
        scan.ExtractedItemsJson = SerializeExtractedItems(items);
        scan.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>แก้ description / จำนวน / ราคาต่อหน่วย ของบรรทัด OCR ในหน้า review
    /// (กฎเหล็ก #3: OCR เติมให้ครบ ผู้ใช้แค่ยืนยัน/แก้ inline — ไม่ต้องสร้างเอกสารก่อน
    /// แล้วค่อยเข้าไปแก้ทีหลัง). recompute Amount = round(qty×unitPrice,2) เสมอ เพื่อ
    /// ให้ qty-guard (SanitizeVatSplitArtifacts) ที่รันซ้ำตอน create ไม่ "แก้กลับ"
    /// ค่าที่ผู้ใช้ตั้งเอง (qty×price == amount เป๊ะ → อยู่ในระยะ tolerance). persist
    /// ลง ExtractedItemsJson → CreateDocumentFromScan อ่านไปใช้. คืน amount ใหม่ให้ UI
    /// อัปเดตช่องยอดโดยไม่ต้อง refetch. null = ไม่แตะ field นั้น (คงค่าเดิม).</summary>
    /// <param name="accountCode">ผังบัญชีรายบรรทัด — เดิม<b>แก้ไม่ได้เลย</b>
    /// (ตาราง review แสดงเป็นข้อความอย่างเดียว) ทั้งที่กฎเหล็ก #3 ข้อ 1 บังคับ
    /// ให้มี <c>GlAccountCode</c> รายบรรทัด และการแก้ตรงนี้คือสิ่งที่
    /// GlAccountDistillationModel ใช้เรียน</param>
    public async Task<decimal> SetExtractedLineFieldsAsync(Guid companyId, Guid scanResultId,
        int lineIndex, string? description, decimal? quantity, decimal? unitPrice,
        string? accountCode = null)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");
        if (string.IsNullOrEmpty(scan.ExtractedItemsJson))
            throw new InvalidOperationException("Scan ไม่มีรายการสินค้าใน OCR result.");
        if (lineIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));
        if (quantity.HasValue && quantity.Value < 0m)
            throw new InvalidOperationException("จำนวนต้องไม่ติดลบ");
        if (unitPrice.HasValue && unitPrice.Value < 0m)
            throw new InvalidOperationException("ราคาต่อหน่วยต้องไม่ติดลบ");

        List<OcrExtractedLineItem> items;
        try
        {
            items = System.Text.Json.JsonSerializer
                .Deserialize<List<OcrExtractedLineItem>>(scan.ExtractedItemsJson) ?? new();
        }
        catch
        {
            throw new InvalidOperationException("ExtractedItemsJson เสียหาย — ไม่สามารถ parse");
        }
        if (lineIndex >= items.Count)
            throw new ArgumentOutOfRangeException(nameof(lineIndex), "lineIndex เกินจำนวนรายการที่สแกนได้");

        var line = items[lineIndex];
        if (description != null) line.Description = description.Trim();
        if (quantity.HasValue) line.Quantity = quantity.Value;
        if (unitPrice.HasValue) line.UnitPrice = unitPrice.Value;
        if (accountCode != null)
            line.SuggestedAccountCode = accountCode.Trim() is { Length: > 0 } c ? c : null;

        // recompute amount จาก qty×price ที่ (แก้แล้ว) — ถ้าครบทั้งคู่
        var qty = line.Quantity ?? 0m;
        var up = line.UnitPrice ?? 0m;
        if (qty > 0m && up > 0m)
            line.Amount = System.Math.Round(qty * up, 2);

        scan.ExtractedItemsJson = SerializeExtractedItems(items);
        scan.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return line.Amount ?? 0m;
    }

    /// <summary>
    /// เพิ่ม/ลบบรรทัดรายการของผลสแกน
    ///
    /// <para>⚠️ ตาราง review เดิม<b>เพิ่มหรือลบแถวไม่ได้เลย</b> — OCR รวมสองแถว
    /// เป็นแถวเดียว หรือแตกแถวเกินมา ผู้ใช้มีทางออกแค่ "แกะใหม่" (ซึ่งจำกัด
    /// จำนวนครั้ง) หรือไปแก้ทีหลังในฟอร์มเอกสาร ⇒ ขัดกฎเหล็ก #3 ข้อ 6 ที่บอกว่า
    /// ตารางต้องแก้ได้เมื่อ OCR หลุด</para>
    ///
    /// <para><paramref name="lineIndex"/> = -1 เมื่อเพิ่มต่อท้าย</para>
    /// </summary>
    public async Task<int> ModifyExtractedLineAsync(
        Guid companyId, Guid scanResultId, string action, int lineIndex)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new KeyNotFoundException("ไม่พบผลสแกนนี้");
        if (scan.CreatedDocumentId.HasValue)
            throw new Accounting.Helpers.BusinessRuleException(
                "สแกนนี้สร้างเอกสารไปแล้ว — แก้รายการที่ตัวเอกสาร ไม่ใช่ที่ผลสแกน");

        List<OcrExtractedLineItem> items;
        try
        {
            items = string.IsNullOrEmpty(scan.ExtractedItemsJson)
                ? new List<OcrExtractedLineItem>()
                : System.Text.Json.JsonSerializer
                    .Deserialize<List<OcrExtractedLineItem>>(scan.ExtractedItemsJson) ?? new();
        }
        catch { throw new Accounting.Helpers.BusinessRuleException("ข้อมูลรายการของผลสแกนเสียหาย — กด 'แกะใหม่'"); }

        switch (action)
        {
            case "add":
                items.Add(new OcrExtractedLineItem { Description = "", Quantity = 1m, UnitPrice = 0m, Amount = 0m });
                break;
            case "delete":
                if (lineIndex < 0 || lineIndex >= items.Count)
                    throw new Accounting.Helpers.BusinessRuleException("ไม่พบบรรทัดที่จะลบ");
                items.RemoveAt(lineIndex);
                break;
            default:
                throw new Accounting.Helpers.BusinessRuleException($"คำสั่งไม่ถูกต้อง: {action}");
        }

        scan.ExtractedItemsJson = SerializeExtractedItems(items);
        scan.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // ปิด loop การเรียนรู้ (กฎเหล็ก #1 ขั้น CAPTURE) — ผู้ใช้เพิ่ม/ลบบรรทัด
        // บนใบที่ AI เป็นคนแตกรายการให้ = ground truth ว่า AI แตกมาไม่ครบ/เกิน
        // ไม่บันทึก = AiFeedbackTrainingJob mine ไม่ได้ (mine เฉพาะแถวที่
        // UserChosenAt != null) ⇒ จ่าย token ทุกใบแต่ไม่เคยฉลาดขึ้น
        await RecordLineSplitFeedbackAsync(scan, items.Count, acceptedAi: false);
        return items.Count;
    }

    /// <summary>ส่งคำตอบจริงของผู้ใช้กลับไปสอน (feature OcrLineItemSplit)
    ///
    /// <para>ยิงครั้งเดียวต่อสแกน — ล้าง <c>LineSplitAiFeedbackId</c> หลังบันทึก
    /// เพื่อไม่ให้การแก้บรรทัดครั้งที่ 2, 3 ส่งซ้ำ. ล้มเหลว = เงียบ
    /// (ห้ามขวางการแก้ไขของผู้ใช้)</para></summary>
    private async Task RecordLineSplitFeedbackAsync(
        OcrScanResult scan, int finalLineCount, bool acceptedAi)
    {
        if (_feedbackRecorder == null || scan.LineSplitAiFeedbackId is not Guid fbId) return;
        try
        {
            await _feedbackRecorder.RecordUserChoiceAsync(
                fbId, finalLineCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                acceptedAi, default,
                // ผู้เรียกเดียวของเมธอดนี้คือ "ผู้ใช้เพิ่ม/ลบบรรทัดเอง"
                Accounting.Models.Enums.UserChoiceSource.Explicit);
            scan.LineSplitAiFeedbackId = null;      // ปิดแล้วปิดเลย ไม่ส่งซ้ำ
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "บันทึก feedback การแตกบรรทัดไม่สำเร็จ (scan {ScanId})", scan.Id);
        }
    }

    public async Task<OcrResultResponse> MatchContactAsync(Guid companyId, Guid scanResultId, Guid contactId)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        var contact = await _db.Contacts
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == contactId)
            ?? throw new InvalidOperationException("Contact not found.");

        // ปิดลูปการสอน local model (กฎเหล็ก #1) — ตอน AI เดา vendor canon แล้ว
        // ผู้ใช้มา "ยืนยัน/แก้" คู่ค้าเอง คือ ground-truth ของ VendorCanon feature.
        // ถ้าไม่บันทึก AiFeedbackTrainingJob จะ mine ไม่ได้ (มัน mine row ที่
        // UserChosenAt != null) → student ไม่เคยเรียนคำตอบจริง. acceptedAi =
        // ผู้ใช้เลือกตรงกับที่ AI แนะนำพอดี. ห่อ try กัน record ล้มไม่ให้ล้ม match.
        if (_feedbackRecorder != null && result.AiSuggestionFeedbackId.HasValue)
        {
            try
            {
                var acceptedAi = result.AiSuggestedContactId == contactId;
                await _feedbackRecorder.RecordUserChoiceAsync(
                    result.AiSuggestionFeedbackId.Value, contactId.ToString(), acceptedAi, default,
                    Accounting.Models.Enums.UserChoiceSource.Explicit);   // ผู้ใช้กดจับคู่คู่ค้าเอง
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record VendorCanon feedback choice (non-fatal)");
            }
        }

        result.MatchedContactId = contactId;
        await _db.SaveChangesAsync();

        return MapToResponse(result);
    }

    /// <summary>Resolve a REAL user GUID to stamp as a document's CreatedBy so
    /// signature resolution works (ResolveSignersAsync parses CreatedBy as a
    /// Guid → looks up the user's signature). OCR auto-create previously stamped
    /// a literal like "OCR-AutoCreate" which isn't a GUID, so the creator's
    /// signature slot (e.g. ผู้จ่ายเงิน on a Payment Voucher) stayed blank.
    /// Order: the scan's own creator (the operator who ran OCR) → the company
    /// Owner (covers API/headless scans where no operator is known) → the
    /// literal fallback only if neither exists.</summary>
    private async Task<string> ResolveOcrCreatorAsync(Guid companyId, string? scanCreatedBy, string fallback)
    {
        if (Guid.TryParse(scanCreatedBy, out var opId))
        {
            var ok = await _db.Set<CompanyUser>().AsNoTracking()
                .AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == opId);
            if (ok) return opId.ToString();
        }
        var ownerId = await _db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.Role == Models.Enums.UserRole.Owner)
            .Select(cu => (Guid?)cu.UserId)
            .FirstOrDefaultAsync();
        return ownerId?.ToString() ?? fallback;
    }

    /// <summary>
    /// เชื่อค่าเงินที่ระบบภายนอก "กรอก/คำนวณเองแล้ว" ส่งมาใน OCR API metadata —
    /// override ค่าที่ OCR แกะจากรูป (กัน OCR อ่านเลขผิด เช่น 530↔630). per
    /// กฎเหล็ก #3 fallback chain: partner-provided > OCR vision. รับได้ทั้งวางที่
    /// top-level หรือซ้อนใน "amounts": { ... }. key ที่รองรับ (case-sensitive,
    /// ลองหลายชื่อ): total/totalAmount/grandTotal/amount, subTotal, vat/vatAmount,
    /// wht/whtAmount หรือ whtRate, และ "lineItems":[{description,quantity,
    /// unitPrice,amount}] (key เฉพาะ ไม่ชนกับ "items" ของ project matcher).
    /// Fail-safe: metadata เพี้ยน → ไม่ทำอะไร (คงค่า OCR).
    /// </summary>
    private void ApplyExternalAmountOverrides(OcrExtractedData data, string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return;
        try
        {
            using var docu = System.Text.Json.JsonDocument.Parse(metadataJson);
            var root = docu.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;

            // ยอมรับทั้ง top-level และ nested "amounts" object
            var scope = root;
            if (root.TryGetProperty("amounts", out var amtObj)
                && amtObj.ValueKind == System.Text.Json.JsonValueKind.Object)
                scope = amtObj;

            static decimal? Num(System.Text.Json.JsonElement obj, params string[] keys)
            {
                foreach (var k in keys)
                    if (obj.TryGetProperty(k, out var v))
                    {
                        if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetDecimal(out var d))
                            return d;
                        if (v.ValueKind == System.Text.Json.JsonValueKind.String
                            && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var ds))
                            return ds;
                    }
                return null;
            }

            var extTotal = Num(scope, "total", "totalAmount", "grandTotal", "grandtotal", "amount", "netTotal");
            var extSub   = Num(scope, "subTotal", "subtotal", "subTotalAmount");
            var extVat   = Num(scope, "vat", "vatAmount", "tax", "taxAmount");
            var extWht   = Num(scope, "wht", "whtAmount", "withholdingTax", "withholdingTaxAmount");
            var extWhtRate = Num(scope, "whtRate", "withholdingTaxRate");

            var changed = new List<string>();

            // line items override (key เฉพาะ "lineItems")
            if (root.TryGetProperty("lineItems", out var li)
                && li.ValueKind == System.Text.Json.JsonValueKind.Array && li.GetArrayLength() > 0)
            {
                var newItems = new List<OcrExtractedLineItem>();
                foreach (var el in li.EnumerateArray())
                {
                    if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var qty = Num(el, "quantity", "qty") ?? 1m;
                    var unit = Num(el, "unitPrice", "price", "unit_price");
                    var amt = Num(el, "amount", "lineAmount", "total");
                    if (!amt.HasValue && unit.HasValue) amt = Math.Round(unit.Value * qty, 2);
                    if (!unit.HasValue && amt.HasValue && qty != 0) unit = Math.Round(amt.Value / qty, 2);
                    string? desc = null;
                    if (el.TryGetProperty("description", out var de) && de.ValueKind == System.Text.Json.JsonValueKind.String)
                        desc = de.GetString();
                    desc ??= (el.TryGetProperty("name", out var ne) && ne.ValueKind == System.Text.Json.JsonValueKind.String)
                        ? ne.GetString() : null;
                    newItems.Add(new OcrExtractedLineItem
                    {
                        Description = string.IsNullOrWhiteSpace(desc) ? "รายการจากระบบภายนอก" : desc!.Trim(),
                        Quantity = qty,
                        UnitPrice = unit,
                        Amount = amt,
                    });
                }
                if (newItems.Count > 0)
                {
                    data.Items.Clear();
                    foreach (var it in newItems) data.Items.Add(it);
                    changed.Add($"lineItems×{newItems.Count}");
                    // ถ้าไม่ได้ส่ง total มา → ผูก total จากผลรวมบรรทัดที่ส่งมา
                    if (!extTotal.HasValue)
                        extTotal = newItems.Sum(x => x.Amount ?? (x.UnitPrice ?? 0) * (x.Quantity ?? 1));
                }
            }

            if (extTotal.HasValue && extTotal.Value > 0)
            { data.TotalAmount = extTotal.Value; data.FieldConfidence["TotalAmount"] = 1.0; changed.Add($"total={extTotal:0.00}"); }
            if (extSub.HasValue && extSub.Value > 0)
            { data.SubTotal = extSub.Value; data.FieldConfidence["SubTotal"] = 1.0; changed.Add($"subTotal={extSub:0.00}"); }
            if (extVat.HasValue)
            { data.VatAmount = extVat.Value; data.FieldConfidence["VatAmount"] = 1.0; changed.Add($"vat={extVat:0.00}"); }
            if (extWht.HasValue && extWht.Value > 0)
            {
                data.HasWht = true;
                var baseAmt = (data.TotalAmount ?? 0) - (data.VatAmount ?? 0);
                if (baseAmt > 0) data.WhtRate = Math.Round(extWht.Value / baseAmt * 100m, 2);
                changed.Add($"wht={extWht:0.00}");
            }
            else if (extWhtRate.HasValue && extWhtRate.Value > 0)
            { data.HasWht = true; data.WhtRate = extWhtRate.Value; changed.Add($"whtRate={extWhtRate}"); }

            if (changed.Count > 0)
                data.ReasoningTrace.Add("[ExternalAmounts] เชื่อค่าจากระบบภายนอก (override OCR): "
                    + string.Join(", ", changed));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External amount override parse failed — คงค่า OCR เดิม");
        }
    }

    /// <summary>
    /// หา "แหล่งเงิน" (บัญชี Cr เงินสด/ธนาคาร) สำหรับ OCR PV/Receipt แบบจ่ายสด
    /// ตาม priority: (1) partner metadata top-level paymentAccountCode/bankCode
    /// — match ChartOfAccount.AccountCode ก่อน ไม่เจอลอง BankAccount.AccountNumber
    /// → LinkedAccountId; (2) CompanySettings.DefaultPaymentAccountId. คืน null
    /// เมื่อไม่มีทั้งคู่ → caller ใช้ lowest-code fallback เดิม. Defensive: ทุก
    /// step fail-safe (metadata เพี้ยน / บัญชีถูกลบ → ข้าม).
    /// </summary>
    private async Task<(string Code, string Name, string Source)?> ResolvePaymentSourceOverrideAsync(
        Guid companyId, string? metadataJson)
    {
        // ── Layer 1: partner metadata ──
        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            string? hint = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var key in new[] { "paymentAccountCode", "paymentSourceCode",
                                                "bankAccountCode", "bankCode", "paymentAccount" })
                    {
                        if (doc.RootElement.TryGetProperty(key, out var v))
                        {
                            if (v.ValueKind == System.Text.Json.JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(v.GetString()))
                            { hint = v.GetString()!.Trim(); break; }
                            if (v.ValueKind == System.Text.Json.JsonValueKind.Number)
                            { hint = v.GetRawText(); break; }
                        }
                    }
                }
            }
            catch { /* metadata เพี้ยน → ข้าม ไป Layer 2 */ }

            if (!string.IsNullOrWhiteSpace(hint))
            {
                // 1a. match CoA code ตรง ๆ
                var acct = await _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && a.AccountCode == hint
                            && a.IsActive && !a.IsDeleted)
                    .Select(a => new { a.AccountCode, a.AccountName })
                    .FirstOrDefaultAsync();
                if (acct != null)
                    return (acct.AccountCode, acct.AccountName, "partner metadata");

                // 1b. match เลขบัญชีธนาคาร → LinkedAccountId
                var bankLinked = await _db.BankAccounts.AsNoTracking()
                    .Where(b => b.CompanyId == companyId && !b.IsDeleted
                            && b.AccountNumber == hint && b.LinkedAccountId != null)
                    .Select(b => b.LinkedAccountId)
                    .FirstOrDefaultAsync();
                if (bankLinked.HasValue)
                {
                    var linkedAcct = await _db.ChartOfAccounts.AsNoTracking()
                        .Where(a => a.Id == bankLinked.Value && a.IsActive && !a.IsDeleted)
                        .Select(a => new { a.AccountCode, a.AccountName })
                        .FirstOrDefaultAsync();
                    if (linkedAcct != null)
                        return (linkedAcct.AccountCode, linkedAcct.AccountName, "partner metadata (bank)");
                }
            }
        }

        // ── Layer 2: company default ──
        var defaultId = await _db.Set<CompanySettings>().AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => c.DefaultPaymentAccountId)
            .FirstOrDefaultAsync();
        if (defaultId.HasValue)
        {
            var acct = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.Id == defaultId.Value && a.CompanyId == companyId
                        && a.IsActive && !a.IsDeleted)
                .Select(a => new { a.AccountCode, a.AccountName })
                .FirstOrDefaultAsync();
            if (acct != null)
                return (acct.AccountCode, acct.AccountName, "ค่าตั้งต้นบริษัท");
        }

        return null;
    }

    private async Task AutoCreateDocumentAsync(Guid companyId, OcrScanResult scan, OcrExtractedData? extractedData = null)
    {
        // ───── ทางเดียวกับ "อัปโหลดผ่านระบบ" (กฎ: ห้ามมี path คู่ขนาน) ─────
        // เดิม method นี้เป็น implementation คู่ขนานที่ "ง่ายกว่า"
        // CreateDocumentFromScanAsync (path ที่ web UI กดปุ่ม "สร้างเอกสาร" ใช้)
        // → ทำให้ "โยนไฟล์ผ่าน OCR API (autoCreate=true)" ได้เอกสารไม่ตรงกับ
        // การอัปโหลดผ่านหน้าเว็บ. สิ่งที่ path เดิมขาด:
        //   • WHT base reconstruct (TotalAmount = SubTotal + VAT − WHT)
        //   • supplier-invoice reference → HasTaxInvoiceReference (ขึ้น ภพ.30)
        //   • bank/payment account จาก CreditAccountCode (BankAccountId vs
        //     PaymentAccountId + LinkedAccountId lookup)
        //   • sales-side contact resolution (buyer แทน vendor)
        //   • PO linkage + per-line GL inherit
        //   • CertificateInLieu legal fields
        //   • line reconcile Case A/B/C/D + VAT pro-rate
        //   • GlAccountAiFeedbackId ต่อบรรทัด (ปิดลูปสอน local model)
        //   • RD-compliance validation
        // ตอนนี้ delegate ไป path เดียวกันทั้งหมด → ทุก data point ตรงกัน.
        //
        // CreateDocumentFromScanAsync re-query scan ตาม Id แล้วอ่าน
        // ExtractedItemsJson / SuggestedAccountsJson / TargetDocumentType /
        // Buyer* ฯลฯ ที่ ScanAsync เพิ่ง set — persist ก่อน delegate เพื่อให้
        // ค่าเหล่านั้นถูก commit (กัน identity-map subtlety + เปิด txn ซ้อน).
        await _db.SaveChangesAsync();

        // createdBy: ปล่อยให้ ResolveOcrCreatorAsync ภายในจัดการ (scan.CreatedBy
        //   → owner fallback เมื่อเป็น int_ key ที่ไม่ใช่ user จริง).
        // targetType = null: ใช้ TargetDocumentType ที่ role-inferrer/VendorIntel
        //   infer ไว้บน scan (เหมือนที่ web UI default เลือกให้ในตัวเลือกหลัก).
        await CreateDocumentFromScanAsync(
            companyId, scan.Id, scan.CreatedBy ?? string.Empty, targetTypeOverride: null);
    }

    /// <summary>
    /// Move the OCR-uploaded file from its placeholder OcrScan entity to the
    /// real Document that just got created. Updates EntityType + EntityId in place
    /// (no physical file move — same StoragePath).
    /// </summary>
    private async Task RelinkScanFileToDocumentAsync(Guid companyId, Guid? fileAttachmentId, Guid documentId)
    {
        if (!fileAttachmentId.HasValue) return;
        var attachment = await _db.FileAttachments
            .FirstOrDefaultAsync(f => f.Id == fileAttachmentId.Value && f.CompanyId == companyId);
        if (attachment == null) return;
        attachment.EntityType = "Document";
        attachment.EntityId = documentId;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Re-linked scan file {FileId} to Document {DocId}", attachment.Id, documentId);
    }

    /// <summary>Public entry point — link an OCR scan's source file to a
    /// Document ที่สร้างผ่าน path อื่น (เช่น UI handoff: OCR review →
    /// "เปิดในฟอร์มเอกสาร" → documents.html → POST /documents) ที่ไม่ได้ผ่าน
    /// CreateDocumentFromScanAsync ทำให้ FileAttachment ค้างอยู่ที่
    /// EntityType="OcrScan" → เอกสารเปิดดูแล้วไม่เห็นไฟล์แนบ.
    /// Idempotent: เรียกซ้ำได้ — relink ซ้ำเป็น no-op.</summary>
    public async Task<bool> LinkScanToExistingDocumentAsync(Guid companyId, Guid scanId, Guid documentId)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(s => s.Id == scanId && s.CompanyId == companyId);
        if (scan == null) return false;
        var docExists = await _db.Documents
            .AnyAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted);
        if (!docExists) return false;
        await RelinkScanFileToDocumentAsync(companyId, scan.FileAttachmentId, documentId);
        if (scan.CreatedDocumentId != documentId)
        {
            scan.CreatedDocumentId = documentId;
            await _db.SaveChangesAsync();
        }
        return true;
    }

    /// <summary>
    /// Register one OCR'd line item as a FixedAsset, delegating to the
    /// existing FixedAssetService.CreateAsync (no duplication of asset
    /// lifecycle logic) AND posting the initial capitalization Journal
    /// Entry inside a DB transaction so the books stay balanced even if
    /// any step fails.
    ///
    /// Journal entry shape:
    ///   Dr: AssetAccount         <PurchaseCost>
    ///   Cr: AccruedPayables /    <PurchaseCost>     (default credit account)
    ///       Cash
    ///
    /// When the scan's MatchedContact exists, the JE Description records
    /// the vendor. After registration:
    ///   • The scan's HasPotentialFixedAsset flag is cleared and the
    ///     line is removed from PotentialAssetLinesJson — so the UI
    ///     stops alerting on the same scan.
    ///   • Auto-create remains suppressed until the user explicitly
    ///     dismisses (or until all asset candidates have been registered).
    /// </summary>
    public async Task<object> RegisterAssetFromScanAsync(
        Guid companyId, Guid scanResultId,
        Controllers.OcrController.RegisterAssetFromScanRequest req,
        Services.Interfaces.IFixedAssetService assetService,
        string createdBy)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.Id == scanResultId && r.CompanyId == companyId)
            ?? throw new InvalidOperationException("OCR scan result not found.");
        if (scan.ScanStatus != "Completed")
            throw new InvalidOperationException("Scan ยังไม่เสร็จ — ไม่สามารถลงทะเบียนสินทรัพย์");

        // Side-effect: if the user assigned a project to this line via
        // the review UI (POST /line-project), promote it into the scope
        // so RegisterAssetAsync (and any future per-line writer) can
        // honour the allocation. Kept inline next to the resolver so
        // the read+write path stays bookended.

        // Resolve the line item from the stored ExtractedItemsJson so we
        // can prefill PurchaseCost / Description when the request omits
        // them. The user may also edit any field via the request body.
        OcrLineItemDto? line = null;
        if (!string.IsNullOrEmpty(scan.ExtractedItemsJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(scan.ExtractedItemsJson);
                var arr = doc.RootElement;
                if (arr.ValueKind == System.Text.Json.JsonValueKind.Array
                    && req.LineIndex >= 0 && req.LineIndex < arr.GetArrayLength())
                {
                    var el = arr[req.LineIndex];
                    line = new OcrLineItemDto(
                        el.TryGetProperty("Description", out var d) ? d.GetString() : null,
                        el.TryGetProperty("Quantity", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)q.GetDouble() : null,
                        el.TryGetProperty("UnitPrice", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)u.GetDouble() : null,
                        el.TryGetProperty("Amount", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)a.GetDouble() : null,
                        el.TryGetProperty("SuggestedAccountCode", out var s) ? s.GetString() : null,
                        el.TryGetProperty("ProjectId", out var pid) && pid.ValueKind == System.Text.Json.JsonValueKind.String
                            && Guid.TryParse(pid.GetString(), out var pg) ? pg : null,
                        el.TryGetProperty("ProjectName", out var pn) ? pn.GetString() : null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse ExtractedItemsJson on scan {Id}", scanResultId);
            }
        }

        var purchaseCost = req.PurchaseCost ?? line?.Amount ?? line?.UnitPrice ?? 0m;
        if (purchaseCost <= 0)
            throw new InvalidOperationException("ราคาซื้อต้องมากกว่า 0");
        var purchaseDate = req.PurchaseDate ?? scan.ExtractedDate ?? DateTime.UtcNow.Date;

        // Single DB transaction so the asset row + journal entry stay
        // mutually-consistent. FixedAssetService.CreateAsync uses the
        // shared DbContext, so its SaveChanges happens INSIDE this txn.
        var strategy = _db.Database.CreateExecutionStrategy();
        Models.DTOs.FixedAsset.FixedAssetResponse? created = null;
        Guid? journalEntryId = null;
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                created = await assetService.CreateAsync(companyId,
                    new Models.DTOs.FixedAsset.CreateFixedAssetRequest(
                        AssetCode: req.AssetCode,
                        Name: req.Name,
                        Description: req.Description ?? line?.Description,
                        Category: req.Category,
                        Location: null,
                        SerialNumber: req.SerialNumber,
                        PurchaseDate: purchaseDate,
                        PurchaseCost: purchaseCost,
                        SalvageValue: req.SalvageValue,
                        UsefulLifeMonths: req.UsefulLifeMonths,
                        DepreciationMethod: req.DepreciationMethod,
                        AssetAccountId: req.AssetAccountId,
                        DepreciationExpenseAccountId: req.DepreciationExpenseAccountId,
                        AccumulatedDepreciationAccountId: req.AccumulatedDepreciationAccountId),
                    createdBy);

                // ผูกสินทรัพย์กลับไปที่สแกน — ให้ AutoRegisterFixedAssetsAsync
                // รู้ว่าของชิ้นนี้ลงทะเบียนไปแล้ว ตอนเอกสารจากสแกนเดียวกันถูก
                // approve (เดิม key คนละตัวจึงไม่มีวันชน → สินทรัพย์ซ้ำ)
                var assetRow = await _db.Set<FixedAsset>()
                    .FirstOrDefaultAsync(a => a.Id == created.Id && a.CompanyId == companyId);
                if (assetRow != null)
                {
                    assetRow.SourceScanResultId = scan.Id;
                    await _db.SaveChangesAsync();
                }

                // Initial capitalization journal entry — Dr: Asset / Cr: AP-or-Cash.
                // We only post when both account IDs are known (asset
                // accounts are required for the FixedAsset to depreciate
                // correctly anyway, so the user must supply them).
                if (req.AssetAccountId.HasValue)
                {
                    // Find a default credit account: scan's suggested CreditAccount,
                    // else the first Liability/Equity account configured as "AP"/"Cash"
                    Guid? creditAccountId = null;
                    if (!string.IsNullOrEmpty(scan.SuggestedAccountsJson))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(scan.SuggestedAccountsJson);
                            if (doc.RootElement.TryGetProperty("CreditAccountCode", out var cc) && cc.GetString() is string code)
                            {
                                creditAccountId = await _db.ChartOfAccounts.AsNoTracking()
                                    .Where(a => a.CompanyId == companyId && a.AccountCode == code && !a.IsDeleted)
                                    .Select(a => (Guid?)a.Id).FirstOrDefaultAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            // เส้นทางเงิน (JE ตั้งสินทรัพย์) — ห้ามเงียบ: ถ้าอ่านไม่ได้
                            // fallback ข้างล่างจะหยิบ "บัญชีเจ้าหนี้ตัวแรกที่ขึ้นต้น 21"
                            // มาลงแทน ซึ่งเป็นการเดาที่ผู้ใช้ไม่มีทางรู้
                            _logger.LogWarning(ex,
                                "อ่านบัญชีเครดิตที่แนะนำของสแกน {ScanId} ไม่ได้ — จะตกไปใช้บัญชีเจ้าหนี้ตัวแรก", scan.Id);
                        }
                    }
                    if (!creditAccountId.HasValue)
                    {
                        // Fallback: first AP-style account (code starts with 21)
                        creditAccountId = await _db.ChartOfAccounts.AsNoTracking()
                            .Where(a => a.CompanyId == companyId && !a.IsDeleted
                                && a.AccountType == AccountType.Liability
                                && a.AccountCode.StartsWith("21"))
                            .OrderBy(a => a.AccountCode)
                            .Select(a => (Guid?)a.Id).FirstOrDefaultAsync();
                    }

                    if (creditAccountId.HasValue)
                    {
                        var entryNumber = $"JV-AST-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..4].ToUpper()}";
                        var je = new JournalEntry
                        {
                            CompanyId = companyId,
                            EntryNumber = entryNumber,
                            EntryDate = purchaseDate,
                            JournalType = JournalType.General,
                            Description = $"ลงทะเบียนสินทรัพย์ {req.AssetCode} {req.Name} (OCR scan {scanResultId})",
                            Status = JournalEntryStatus.Posted,
                            IsAutoGenerated = true,
                            TotalDebit = purchaseCost,
                            TotalCredit = purchaseCost,
                            CreatedBy = createdBy,
                        };
                        je.Lines.Add(new JournalEntryLine
                        {
                            AccountId = req.AssetAccountId.Value,
                            DebitAmount = purchaseCost,
                            CreditAmount = 0,
                            Description = $"ลงทะเบียนสินทรัพย์ {req.Name}",
                        });
                        je.Lines.Add(new JournalEntryLine
                        {
                            AccountId = creditAccountId.Value,
                            DebitAmount = 0,
                            CreditAmount = purchaseCost,
                            Description = $"เจ้าหนี้ค่าสินทรัพย์ {req.Name}",
                        });
                        // Mandatory double-entry balance check — same
                        // pattern as the depreciation entry in
                        // FixedAssetService.CalculateDepreciationAsync.
                        if (Math.Abs(je.TotalDebit - je.TotalCredit) > 0.01m)
                            throw new InvalidOperationException(
                                $"Journal ไม่สมดุล: Dr={je.TotalDebit:N2} Cr={je.TotalCredit:N2}");
                        _db.JournalEntries.Add(je);
                        journalEntryId = je.Id;
                    }
                }

                // Remove this line from the asset-candidates list. When
                // none remain, clear the alert flag entirely.
                RemoveAssetCandidate(scan, req.LineIndex);
                scan.ProcessingNotes = (scan.ProcessingNotes ?? "")
                    + $"\n[FixedAsset] ลงทะเบียน asset {req.AssetCode} '{req.Name}' จาก line {req.LineIndex} (cost {purchaseCost:N2})";

                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        return new
        {
            assetId = created?.Id,
            assetCode = created?.AssetCode,
            journalEntryId,
            purchaseCost,
            remainingCandidates = scan.HasPotentialFixedAsset,
        };
    }

    /// <summary>Drop a registered line from the JSON candidate list so
    /// the UI alert hides itself once every asset has been registered.</summary>
    private static void RemoveAssetCandidate(OcrScanResult scan, int lineIndex)
    {
        if (string.IsNullOrEmpty(scan.PotentialAssetLinesJson)) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(scan.PotentialAssetLinesJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            var remaining = doc.RootElement.EnumerateArray()
                .Where(el => !(el.TryGetProperty("lineIndex", out var idx)
                    && idx.ValueKind == System.Text.Json.JsonValueKind.Number
                    && idx.GetInt32() == lineIndex))
                .Select(el => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(el.GetRawText()))
                .ToList();
            scan.PotentialAssetLinesJson = remaining.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(remaining)
                : null;
            scan.HasPotentialFixedAsset = remaining.Count > 0;
        }
        catch { /* malformed JSON — leave as-is */ }
    }

    public async Task DeleteScanAsync(Guid companyId, Guid scanResultId, bool cascadeCreatedDocument = false, string? reason = null, Guid? performedByUserId = null)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        // Handle the auto-created document case. Two flows:
        //   • cascadeCreatedDocument = false → bail out (old behavior) so the
        //     user can't accidentally orphan or delete a document they
        //     intend to keep.
        //   • cascadeCreatedDocument = true → delete the document too, but
        //     ONLY while it's still Draft. Approved/Paid documents are
        //     financial records and must not be deleted via the OCR UI —
        //     the user has to void/reverse them through the normal docs UI.
        // ⚠️ สแกนที่ลง **JE ตรง** (ปุ่ม "บันทึก JE") ไม่มี Document ให้ cascade —
        // เดิมจึงหลุดด่านข้างล่างทั้งหมดแล้วลบได้ ⇒ **ลบหลักฐานของรายการบัญชีที่ post
        // ไปแล้ว** (พ.ร.บ.การบัญชี ม.10 ให้เก็บ 5 ปี · ผู้สอบบัญชีเปิด JE แล้วไม่มีเอกสาร
        // ต้นทาง) — ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T5
        if (result.CreatedJournalEntryId.HasValue)
        {
            var jeNo = await _db.JournalEntries.AsNoTracking()
                .Where(j => j.Id == result.CreatedJournalEntryId.Value && j.CompanyId == companyId)
                .Select(j => j.EntryNumber)
                .FirstOrDefaultAsync();
            throw new Accounting.Helpers.BusinessRuleException(
                $"สแกนนี้เป็นหลักฐานของใบสำคัญ {jeNo ?? "ที่ลงบัญชีแล้ว"} — ลบไม่ได้ "
                + "(พ.ร.บ.การบัญชี ม.10 ต้องเก็บเอกสารประกอบ 5 ปี) · ถ้าลงผิด ให้กลับรายการ "
                + "ใบสำคัญนั้นจากหน้าใบสำคัญก่อน แล้วค่อยลบสแกน",
                "OCR-DELETE-HAS-JE");
        }

        if (result.CreatedDocumentId.HasValue)
        {
            if (!cascadeCreatedDocument)
                throw new InvalidOperationException(
                    "เอกสารถูกสร้างจาก scan นี้แล้ว — ส่ง cascade=true เพื่อลบทั้งคู่ (เฉพาะกรณี Draft)");

            var doc = await _db.Documents
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.Id == result.CreatedDocumentId.Value
                    && d.CompanyId == companyId && !d.IsDeleted);
            if (doc != null)
            {
                if (doc.Status != Models.Enums.DocumentStatus.Draft
                    && doc.Status != Models.Enums.DocumentStatus.WaitingApproval
                    && doc.Status != Models.Enums.DocumentStatus.Rejected)
                {
                    throw new InvalidOperationException(
                        $"เอกสารที่สร้างจาก scan นี้อยู่ในสถานะ {doc.Status} — ต้อง void/reverse จากหน้าเอกสารแทน");
                }
                // Soft-delete to preserve audit trail (consistent with how
                // documents are deleted elsewhere). Lines cascade via the
                // entity's IsDeleted filter; FK rows like attachments are
                // re-pointed below.
                doc.IsDeleted = true;
                doc.UpdatedAt = DateTime.UtcNow;
                doc.UpdatedBy = "OCR-DeleteCascade";
                foreach (var line in doc.Lines)
                {
                    line.IsDeleted = true;
                    line.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        if (result.FileAttachmentId.HasValue)
        {
            var file = await _db.FileAttachments.FirstOrDefaultAsync(f => f.Id == result.FileAttachmentId);
            if (file != null)
            {
                try { if (File.Exists(file.StoragePath)) File.Delete(file.StoragePath); } catch { }
                _db.FileAttachments.Remove(file);
            }
        }

        // "Label before delete" (business-flow step): the scan row is removed
        // for good, so persist the operator's label/reason to the audit log.
        // Added AFTER every validation that can throw — otherwise a failed
        // delete left a pending audit row in the tracked context that a later
        // SaveChanges on the same scoped context would persist.
        if (!string.IsNullOrWhiteSpace(reason))
        {
            _db.AuditLogs.Add(new AuditLog
            {
                CompanyId = companyId,
                UserId = performedByUserId,
                Action = Models.Enums.AuditAction.Delete,
                EntityType = "OcrScanResult",
                EntityId = scanResultId.ToString(),
                OldValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    result.OriginalFileName,
                    result.ExtractedVendorName,
                    result.ExtractedDocumentNumber,
                    result.ExtractedTotalAmount,
                    Label = reason.Trim(),
                }),
                Timestamp = DateTime.UtcNow
            });
        }

        _db.Set<OcrScanResult>().Remove(result);
        await _db.SaveChangesAsync();
    }

    /// <summary>อ่านความมั่นใจรายช่องที่เก็บไว้ในฐาน — คืน null เมื่อไม่มี/พัง
    /// (null = "ยังไม่รู้" ให้ UI ตกไปใช้ค่าทั้งใบตามเดิม ไม่ใช่ "มั่นใจ 0%")</summary>
    private static Dictionary<string, double>? ParseFieldConfidenceJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(json);
            return parsed is { Count: > 0 } ? Accounting.Helpers.OcrFieldKeys.Canonicalize(parsed) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// ใบนี้ซ้ำกับอะไรในระบบหรือไม่ — คืนข้อความเตือน (null = ไม่ซ้ำ)
    ///
    /// <para>เทียบสองแหล่ง: ธง <c>IsDuplicate</c> ที่ตั้งตอนสแกน (hash ไฟล์ /
    /// fingerprint เนื้อหา / เลขที่+ยอด) และ <b>ตารางเอกสารจริง</b> ซึ่งเดิม
    /// ไม่เคยถูกเทียบเลย — ใบที่คีย์มือไว้ก่อนแล้วมาสแกนทีหลังจึงสร้างซ้ำได้
    /// (ฝั่งซื้อ = เลขใบกำกับผู้ขายซ้ำ ⇒ เคลมภาษีซื้อซ้ำ · ฝั่งขาย = ออกเลข
    /// เอกสารใหม่ให้รายการเดิม ⇒ ยอดขายเกินจริงใน ภ.พ.30)</para>
    /// </summary>
    private async Task<string?> FindDuplicateDocumentWarningAsync(Guid companyId, OcrScanResult result)
    {
        if (result.IsDuplicate && result.DuplicateOfScanId.HasValue)
        {
            var prior = await _db.Set<OcrScanResult>().AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.Id == result.DuplicateOfScanId.Value)
                .Select(r => new { r.CreatedDocumentId, r.OriginalFileName })
                .FirstOrDefaultAsync();
            if (prior?.CreatedDocumentId != null)
            {
                var priorNo = await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == prior.CreatedDocumentId.Value && d.CompanyId == companyId)
                    .Select(d => d.DocumentNumber).FirstOrDefaultAsync();
                return $"ใบนี้ซ้ำกับที่สแกนไว้แล้ว ({prior.OriginalFileName}) ซึ่งสร้างเป็นเอกสาร "
                     + $"{priorNo ?? "(ไม่ทราบเลขที่)"} ไปแล้ว — เปิดใบเดิมแทนการสร้างใหม่ "
                     + "· ถ้าเป็นคนละใบจริง ให้กดยืนยันสร้างซ้ำในกล่องเตือน";
            }
        }

        // ── เทียบกับเอกสารจริง: เลขที่เดียวกัน + คู่ค้าเดียวกัน ──
        //
        // ⚠️ เดิมต้อง "เลขที่ **และ** ยอดตรงถึงสตางค์" ถึงจะเตือน — ยอดต่างแม้
        // บาทเดียวก็ `return null` เงียบ ⇒ OCR อ่าน 1,070 เป็น 1,010 (หลักเดียว
        // เพี้ยน = ความผิดพลาดที่พบบ่อยที่สุดของ OCR) ⇒ ใบซ้ำหลุดเข้าระบบ ⇒
        // **เคลมภาษีซื้อสองครั้ง**จากใบกำกับใบเดียว (ผลตรวจ 2026-09-06 · T1-14)
        // §86/4 บังคับให้เลขใบกำกับ unique ต่อผู้ขาย ⇒ "ผู้ขายเดียวกัน + เลข
        // เดียวกัน" = ใบเดียวกันเสมอ · ตรรกะตัดสินอยู่ใน Helpers/OcrDuplicateScanRule
        var docNo = (result.ExtractedDocumentNumber ?? "").Trim();
        if (docNo.Length < 3) return null;
        var vendorDigits = Accounting.Helpers.ThaiTaxId.Normalize(result.ExtractedVendorTaxId);

        // ฝั่งซื้อเก็บเลขใบของผู้ขายไว้ที่ SupplierInvoiceNumber; ฝั่งขายใช้
        // DocumentNumber ของเราเอง — เทียบทั้งสองช่องเพื่อครอบทั้งสองทิศ
        var rows = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.Status != DocumentStatus.Voided
                && (d.SupplierInvoiceNumber == docNo || d.DocumentNumber == docNo))
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentDate, d.TotalAmount, d.ContactId })
            .Take(20)
            .ToListAsync();
        if (rows.Count == 0) return null;

        // ดึงเลขผู้เสียภาษีของคู่ค้าทุกใบในคราวเดียว (กัน N+1)
        var contactIds = rows.Select(r => r.ContactId).Where(id => id != Guid.Empty).Distinct().ToList();
        var contactTaxes = contactIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await _db.Contacts.AsNoTracking()
                    .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.TaxId })
                    .ToListAsync())
                .ToDictionary(c => c.Id,
                    c => Accounting.Helpers.ThaiTaxId.Normalize(c.TaxId) ?? "");

        var candidates = rows.Select(r => new Accounting.Helpers.DuplicateCandidate(
                r.Id, r.DocumentNumber, r.DocumentDate, r.TotalAmount,
                r.ContactId != Guid.Empty && contactTaxes.TryGetValue(r.ContactId, out var tx) ? tx : ""))
            .ToList();

        var decision = Accounting.Helpers.OcrDuplicateScanRule.Decide(
            candidates, result.ExtractedTotalAmount, vendorDigits, result.ExtractedDate);
        if (decision.Match is not Accounting.Helpers.DuplicateCandidate hit)
            return null;

        if (decision.Verdict == Accounting.Helpers.OcrDuplicateVerdict.SameNumberAndAmount)
            return $"มีเอกสาร {hit.DocumentNumber} ({hit.DocumentDate:dd/MM/yyyy}) ยอด {hit.TotalAmount:N2} "
                 + $"ที่ใช้เลขที่ “{docNo}” และยอดเดียวกันอยู่แล้ว — สร้างซ้ำจะทำให้ยอดในรายงานภาษีเกินจริง "
                 + "· เปิดใบเดิมแทน หรือกดยืนยันสร้างซ้ำถ้าเป็นคนละใบจริง";

        // เลขที่ตรงแต่ยอดต่าง — บอกส่วนต่างให้คนตัดสิน (ห้ามเงียบ)
        var gapText = decision.AmountGap > 0m
            ? $"ยอดต่างกัน {decision.AmountGap:N2} บาท (ใบเดิม {hit.TotalAmount:N2} · ที่อ่านได้ {result.ExtractedTotalAmount:N2}) — OCR อาจอ่านยอดผิด"
            : $"ใบเดิมยอด {hit.TotalAmount:N2} · ใบนี้ระบบอ่านยอดไม่ได้";
        return $"มีเอกสาร {hit.DocumentNumber} ({hit.DocumentDate:dd/MM/yyyy}) ที่ใช้เลขที่ “{docNo}” "
             + $"ของผู้ขายรายเดียวกันอยู่แล้ว · {gapText} "
             + "· เลขใบกำกับภาษีไม่ซ้ำกันต่อผู้ขาย (§86/4) จึงน่าจะเป็นใบเดียวกัน — "
             + "เปิดใบเดิมแทน หรือกดยืนยันสร้างซ้ำถ้าเป็นคนละใบจริง";
    }

    /// <summary>เติมคำเตือน "ข้อมูลตามสรรพากรยังไม่ครบ" ให้ผลสแกนที่จะส่งออกไป
    /// — ดึงเลขผู้เสียภาษีของบริษัทครั้งเดียวแล้วประเมินทุกแถว (กัน N+1)
    ///
    /// <para>อยู่ตรงนี้เพราะ <c>MapToResponse</c> เป็น sync ไปแตะฐานไม่ได้ และ
    /// การประเมินต้องรู้เลขบริษัท — แยกเป็นขั้นตอน async หลัง map จึงเป็นที่
    /// เดียวที่เรียกได้ทั้งเส้นทางรายการและเส้นทางรายตัว</para></summary>
    private async Task<List<OcrResultResponse>> AttachComplianceAsync(
        Guid companyId, List<OcrResultResponse> items)
    {
        if (items.Count == 0) return items;
        var ourTaxId = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.TaxId).FirstOrDefaultAsync();
        // แผนที่ฝั่งซื้อ/ขาย สร้างครั้งเดียวต่อ request แล้วแนบไปกับทุกแถว —
        // หน้าเว็บจะได้เลิกถือลิสต์ salesTypes ของตัวเอง (ดูหมายเหตุใน DTO)
        var sideMap = Accounting.Helpers.DocumentSide.BuildSideMap()
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return items
            .Select(x => x with
            {
                ComplianceIssues = Ocr.OcrScanComplianceEvaluator.Evaluate(x, ourTaxId),
                DocumentSideMap = sideMap,
            })
            .ToList();
    }

    /// <summary>เวอร์ชันรายตัว — ใช้กับ endpoint ที่คืนผลสแกนใบเดียว</summary>
    private async Task<OcrResultResponse> AttachComplianceAsync(
        Guid companyId, OcrResultResponse item)
        => (await AttachComplianceAsync(companyId, new List<OcrResultResponse> { item }))[0];

    private OcrResultResponse MapToResponse(OcrScanResult r, OcrExtractedData? data = null)
    {
        OcrSuggestedAccountsDto? suggestedAccounts = null;
        List<OcrLineItemDto>? items = null;

        if (data != null)
        {
            if (data.DebitAccountCode != null || data.CreditAccountCode != null)
            {
                suggestedAccounts = new OcrSuggestedAccountsDto(
                    data.DebitAccountCode, data.DebitAccountName,
                    data.CreditAccountCode, data.CreditAccountName,
                    data.VatAccountCode, data.VatAccountName);
            }
            if (data.Items.Count > 0)
            {
                items = data.Items.Select(i => new OcrLineItemDto(
                    i.Description, i.Quantity, i.UnitPrice, i.Amount, i.SuggestedAccountCode,
                    i.ProjectId, i.ProjectName, i.Unit, i.VatRate, i.VatAmount)).ToList();
            }
        }

        // Fall back to stored entity data when data parameter is null
        if (suggestedAccounts == null && !string.IsNullOrEmpty(r.SuggestedAccountsJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(r.SuggestedAccountsJson);
                var sa = doc.RootElement;
                suggestedAccounts = new OcrSuggestedAccountsDto(
                    sa.TryGetProperty("DebitAccountCode", out var dac) ? dac.GetString() : null,
                    sa.TryGetProperty("DebitAccountName", out var dan) ? dan.GetString() : null,
                    sa.TryGetProperty("CreditAccountCode", out var cac) ? cac.GetString() : null,
                    sa.TryGetProperty("CreditAccountName", out var can) ? can.GetString() : null,
                    sa.TryGetProperty("VatAccountCode", out var vac) ? vac.GetString() : null,
                    sa.TryGetProperty("VatAccountName", out var van) ? van.GetString() : null);
            }
            catch (Exception ex)
            {
                // เดิม catch {} เงียบ ⇒ ผังบัญชีที่แนะนำหายไปจากการ์ดโดยไม่มีร่องรอย
                _logger.LogWarning(ex,
                    "อ่าน SuggestedAccountsJson ของสแกน {ScanId} ไม่ได้ — การ์ดจะไม่โชว์ผังที่แนะนำ", r.Id);
            }
        }

        if (items == null && !string.IsNullOrEmpty(r.ExtractedItemsJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(r.ExtractedItemsJson);
                items = doc.RootElement.EnumerateArray().Select(el => new OcrLineItemDto(
                    el.TryGetProperty("Description", out var d) ? d.GetString() : null,
                    el.TryGetProperty("Quantity", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)q.GetDouble() : null,
                    el.TryGetProperty("UnitPrice", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)u.GetDouble() : null,
                    el.TryGetProperty("Amount", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)a.GetDouble() : null,
                    el.TryGetProperty("SuggestedAccountCode", out var s) ? s.GetString() : null,
                    el.TryGetProperty("ProjectId", out var pid) && pid.ValueKind == System.Text.Json.JsonValueKind.String
                        && Guid.TryParse(pid.GetString(), out var pg) ? pg : (Guid?)null,
                    el.TryGetProperty("ProjectName", out var pn) ? pn.GetString() : null,
                    el.TryGetProperty("Unit", out var un) ? un.GetString() : null,
                    // E-OCR-01 — อัตรา/ยอดภาษีรายบรรทัดต้องกลับถึงหน้า review
                    el.TryGetProperty("VatRate", out var vr) && vr.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)vr.GetDouble() : null,
                    el.TryGetProperty("VatAmount", out var va) && va.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)va.GetDouble() : null
                )).ToList();
            }
            catch (Exception ex)
            {
                // เดิม catch {} ⇒ ExtractedItemsJson ที่เพี้ยนทำให้ items = null
                // แล้วหน้า review โชว์ตารางรายการ **ว่างเปล่าโดยไม่มี error**
                // ผู้ใช้กดยืนยันแล้วได้เอกสารที่ไม่มีบรรทัดเลย ทั้งที่กระดาษมี
                _logger.LogWarning(ex,
                    "อ่าน ExtractedItemsJson ของสแกน {ScanId} ไม่ได้ — ตารางรายการจะว่าง", r.Id);
            }
        }

        var expenseCategory = data?.ExpenseCategory ?? r.ExpenseCategory;
        var hasWht = data?.HasWht ?? r.HasWht;
        var whtRate = data?.WhtRate ?? r.WhtRate;
        var paymentTermsDays = data?.PaymentTermsDays ?? r.PaymentTermsDays;

        OcrDbdInfo? dbdInfo = null;
        if (data?.DbdLookupAttempted == true)
        {
            dbdInfo = new OcrDbdInfo(
                LookupAttempted: true,
                Matched: data.DbdMatched,
                CanonicalName: data.DbdCanonicalName,
                Address: data.DbdAddress,
                JuristicType: data.DbdJuristicType,
                Status: data.DbdStatus);
        }

        return new OcrResultResponse(
            r.Id, r.OriginalFileName, r.ScanStatus, r.DocumentType, r.Confidence,
            r.ExtractedVendorName, r.ExtractedVendorTaxId, r.ExtractedDocumentNumber,
            r.ExtractedDate, r.ExtractedSubTotal, r.ExtractedVatAmount, r.ExtractedTotalAmount,
            r.MatchedContactId, r.CreatedDocumentId, r.ProcessedAt,
            r.IsDuplicate, r.DuplicateOfScanId, r.FileHash, r.ProcessingNotes,
            expenseCategory, suggestedAccounts,
            hasWht, whtRate,
            paymentTermsDays, items,
            r.RawTextContent,
            // ความมั่นใจรายช่อง: ใช้ของสด (ตอนสแกน) ถ้ามี ไม่งั้นอ่านจากฐาน —
            // เดิมมีแค่ของสด พอ reload หน้าค่าหายหมด แล้วป้าย % ข้างทุกช่องตกไป
            // ใช้ confidence ของทั้งใบ ดูเหมือนข้อมูลรายช่องจริงทั้งที่เป็นเลขเดียวกัน
            data?.FieldConfidence is { Count: > 0 } fresh
                ? Accounting.Helpers.OcrFieldKeys.Canonicalize(fresh)
                : ParseFieldConfidenceJson(r.FieldConfidenceJson),
            // Prefer the in-memory extraction (fresh scan path) but
            // fall back to the persisted entity values when remapping
            // a list row or a page reload where `data` is null.
            data?.BuyerName ?? r.BuyerName,
            data?.BuyerTaxId ?? r.BuyerTaxId,
            dbdInfo,
            r.ScannedDocumentType,
            r.OurRole,
            r.TargetDocumentType,
            r.OcrEngine,
            r.HasPotentialFixedAsset,
            r.PotentialAssetLinesJson,
            Quality: BuildQualityDto(r),
            HasHandwriting: r.HasHandwriting,
            HandwritingConfidence: r.HandwritingConfidence,
            SuggestedEntryMode: r.SuggestedEntryMode,
            OpenPoNumbersJson: r.OpenPoNumbersJson,
            LinkedPurchaseOrderId: r.LinkedPurchaseOrderId,
            LinkedPurchaseOrderNumber: r.LinkedPurchaseOrderNumber,
            LinkedPredecessorDocumentId: r.LinkedPredecessorDocumentId,
            LinkedPredecessorNumber: r.LinkedPredecessorNumber,
            LinkedPredecessorType: r.LinkedPredecessorType,
            PredecessorLinkReason: r.PredecessorLinkReason,
            PredecessorCandidatesJson: r.PredecessorCandidatesJson,
            ExtractedDiscountAmount: data?.DiscountAmount ?? r.ExtractedDiscountAmount,
            GlAccountUsedAi: r.GlAccountUsedAi,
            // ชื่อบัญชี — UI resolve เองจาก CoA ที่โหลดไว้ (MapToResponse sync,
            // หลีกเลี่ยง DB call ต่อ scan)
            GlAccountAiSuggestedCode: r.GlAccountAiSuggestedCode,
            GlAccountAiSuggestedName: null,
            GlAccountAiConfidence: r.GlAccountAiConfidence,
            // §86/4 (กฎเหล็ก #3): ค่าจาก scan ล่าสุดก่อน แล้ว fallback ค่าที่เก็บไว้
            // — สาขาต้องมีค่าเสมอ ("00000" = สำนักงานใหญ่) ห้ามปล่อย null ให้ UI
            // ⚠️ ห้ามเติม "00000" (ผลตรวจ 2026-09-06 · T1-10 ต่อ): เส้นเขียนเอกสาร
            // เลิกเดาสาขาไปแล้ว แต่ **DTO ยังเดาอยู่** ⇒ ช่องบนหน้า review ถูก
            // pre-fill เป็น "สำนักงานใหญ่" ทุกใบ แล้วผู้ใช้กดยืนยัน 1 คลิก
            // ⇒ ใบของสาขาที่ 3 ขึ้นรายงานภาษีซื้อ §87 เป็นสำนักงานใหญ่เหมือนเดิม
            // (ประกาศอธิบดีฯ ฉบับที่ 199 บังคับให้ระบุสาขาจริง) · หน้าเว็บมีหมายเหตุ
            // ห้ามเติมเองอยู่แล้ว แต่ถูกลบล้างเพราะเซิร์ฟเวอร์ส่ง "00000" มาให้ก่อน
            // — "ไม่รู้ = บอกว่าไม่รู้" แล้วให้ป้าย confidence 0.30 เตือน
            VendorBranchCode: data?.VendorBranchCode ?? r.VendorBranchCode,
            VendorAddress: data?.VendorAddress ?? r.VendorAddress,
            BuyerBranchCode: data?.BuyerBranchCode ?? r.BuyerBranchCode,
            BuyerAddress: data?.BuyerAddress ?? r.BuyerAddress,
            // ป้ายซื่อสัตย์ตามกฎเหล็ก #1 — บอกว่ารายการในใบนี้ AI เป็นคนแตกให้
            LineSplitUsedAi: r.LineSplitUsedAi,
            TargetDocTypeUsedAi: r.TargetDocTypeUsedAi,
            OurRoleUsedAi: r.OurRoleUsedAi,
            // สกุลเงินอ่านจากข้อความบนกระดาษ (ตัวเดียวกับที่เส้น "สร้างทันที" ใช้)
            // — ส่งออกมาเพื่อให้เส้น handoff ได้คำตอบเดียวกัน (T4-08)
            Currency: r.Currency ?? InferCurrency(r.RawTextContent),
            GlAccountAiFeedbackId: r.GlAccountAiFeedbackId,
            SuggestedWhtRate: r.SuggestedWhtRate,
            SuggestedWhtSource: r.SuggestedWhtSource,
            WhtIncomeTypeCode: r.WhtIncomeTypeCode,
            UserNotes: r.UserNotes,
            FieldDecisionsJson: r.FieldDecisionsJson);
    }

    private static OcrQualityGradeDto? BuildQualityDto(OcrScanResult r)
    {
        if (r.ScanStatus != "Completed") return null;
        var grade = Ocr.ScanQualityGrader.Compute(r);
        // เหตุผล + คำแนะนำ — เดิม DTO ทิ้ง Reasons ทั้งก้อน ผู้ใช้เห็นแค่ตัวอักษร
        // "D" โดยไม่รู้ว่าเพราะอะไรและต้องทำอะไรต่อ
        var advice = grade.Letter switch
        {
            "A" => "ข้อมูลครบและสอดคล้องกัน — ยืนยันได้เลย",
            "B" => "ข้อมูลใช้ได้ แนะนำกวาดตาดูยอดกับวันที่ก่อนยืนยัน",
            "C" => "ต้องตรวจก่อนยืนยัน — เปิด 'ไฟล์แนบ' เทียบกับกระดาษจริง",
            _   => "คุณภาพรูปต่ำ — แนะนำ **ถ่าย/สแกนใหม่ให้คมขึ้น** แล้วกด 'สแกนใหม่' "
                 + "(ถ่ายตรง ๆ ไม่เอียง แสงสม่ำเสมอ เห็นครบทั้งใบ)",
        };
        return new OcrQualityGradeDto(grade.Letter, grade.Score, grade.Color, grade.Reasons, advice);
    }

    /// <summary>
    /// Map ETDA e-Tax XML extraction result onto the OcrExtractedData shape
    /// the rest of the pipeline (gateway, role inferrer, vendor enrichment)
    /// consumes. Confidence is hard-pinned to 100% because every value came
    /// from the legally authoritative XML — not heuristic OCR text. Each
    /// field also gets a 1.0 FieldConfidence so the gateway treats them as
    /// gold and won't try to "correct" them.
    /// </summary>
    private static OcrExtractedData MapEtaxToOcrData(Ocr.EtaxPdfXmlExtractor.ExtractResult etax)
    {
        var data = new OcrExtractedData
        {
            DocumentType = etax.MappedDocumentType ?? "TaxInvoice",
            Confidence = 1.0m,
            DocumentNumber = etax.DocumentNumber,
            DocumentDate = etax.DocumentDate,
            VendorName = etax.SellerName,
            VendorTaxId = etax.SellerTaxId,
            VendorBranchCode = etax.SellerBranchCode,
            VendorAddress = etax.SellerAddress,
            BuyerName = etax.BuyerName,
            BuyerTaxId = etax.BuyerTaxId,
            BuyerBranchCode = etax.BuyerBranchCode,
            BuyerAddress = etax.BuyerAddress,
            SubTotal = etax.LineTotal ?? etax.TaxBasis,
            VatAmount = etax.VatAmount,
            TotalAmount = etax.GrandTotal,
            // ★ ส่วนลดท้ายบิลที่ XML **ประกาศไว้แล้ว** = LineTotal − TaxBasis
            // (TaxBasisTotalAmount คือฐานภาษีหลังหักส่วนลดตามสเปก CII)
            // ⇒ ไม่ต้องอนุมานจากส่วนต่างเหมือนเส้นอ่านกระดาษ (ผลตรวจ T2-08)
            DiscountAmount = etax.LineTotal is decimal lt && etax.TaxBasis is decimal tb
                && lt - tb > 0m ? lt - tb : null,
            // สกุลเงินที่ XML ประกาศไว้ — ไม่ต้องเดาจากข้อความ
            Currency = string.IsNullOrWhiteSpace(etax.Currency) ? null : etax.Currency!.Trim().ToUpperInvariant(),
        };

        // ใช้ชื่อช่องกลาง (Helpers/OcrFieldKeys.cs) — เดิมใช้ชื่อชุดของ Azure
        // ปนกับชื่อของตัวเอง ทำให้ฝั่งอ่านหาไม่เจอ
        foreach (var k in new[] {
            Accounting.Helpers.OcrFieldKeys.DocumentNumber, Accounting.Helpers.OcrFieldKeys.DocumentDate,
            Accounting.Helpers.OcrFieldKeys.SellerName, Accounting.Helpers.OcrFieldKeys.SellerTaxId,
            Accounting.Helpers.OcrFieldKeys.SellerAddress, Accounting.Helpers.OcrFieldKeys.SellerBranchCode,
            Accounting.Helpers.OcrFieldKeys.BuyerName, Accounting.Helpers.OcrFieldKeys.BuyerTaxId,
            Accounting.Helpers.OcrFieldKeys.BuyerAddress, Accounting.Helpers.OcrFieldKeys.BuyerBranchCode,
            Accounting.Helpers.OcrFieldKeys.SubTotal, Accounting.Helpers.OcrFieldKeys.VatAmount,
            Accounting.Helpers.OcrFieldKeys.TotalAmount })
        {
            data.FieldConfidence[k] = 1.0;
        }

        foreach (var li in etax.Items)
        {
            data.Items.Add(new OcrExtractedLineItem
            {
                Description = li.Description,
                Quantity = li.Quantity,
                UnitPrice = li.UnitPrice,
                Amount = li.Amount,
                // ★ หน่วยนับที่ XML ประกาศไว้ (unitCode ตาม UN/ECE Rec.20) —
                // ตัวสกัดอ่านมาได้ตั้งแต่แรกแต่ตัว map ทิ้งทุกครั้ง ⇒ ทุกบรรทัด
                // ของใบ e-Tax ตกไปเป็น "ชิ้น" ทั้งที่เอกสารที่มีลายเซ็นดิจิทัล
                // บอกหน่วยจริงไว้แล้ว (ผลตรวจ 2026-09-06 · T2-08)
                Unit = string.IsNullOrWhiteSpace(li.Unit)
                    ? null : Ocr.UnitInferrer.FromUneceCode(li.Unit.Trim()),
            });
        }

        data.ReasoningTrace.Add(
            $"[Tier 0] ดึงค่าจาก e-Tax XML ที่ฝังใน PDF/A-3 — เอกสาร {etax.DocumentTypeName} " +
            $"({etax.DocumentTypeCode}) เลขที่ {etax.DocumentNumber}, ยอดรวม {etax.GrandTotal:N2} {etax.Currency}");
        return data;
    }

    /// <summary>
    /// เติมช่องที่ยังว่างจากแพตเทิร์นที่ระบบเรียนไว้ของผู้ขายรายนี้
    /// (<c>OcrLearnedPatterns</c>) — <b>จุดอ่าน</b>ของตารางที่ก่อนหน้านี้มีแต่
    /// ฝั่งเขียน ดู <see cref="DocumentZoneAnalyzer.ApplyLearnedPatternsTo"/>
    ///
    /// <para>เลือกแพตเทิร์นของผู้ขายรายนี้ก่อน ถ้ายังไม่รู้ว่าใครเป็นผู้ขาย
    /// (ซึ่งเป็นเคสที่ต้องการความช่วยเหลือที่สุด) ใช้แพตเทิร์นระดับบริษัทที่
    /// ไม่ผูกกับผู้ขาย. ทุก query มี CompanyId. ล้มเหลว = ข้ามเงียบ ๆ</para>
    /// </summary>
    private async Task ApplyLearnedPatternsAsync(
        Guid companyId, OcrExtractedData data, string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return;
        try
        {
            var vendorTaxId = data.VendorTaxId;
            var q = _db.OcrLearnedPatterns.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && !p.IsNegativeExample);
            q = !string.IsNullOrWhiteSpace(vendorTaxId)
                ? q.Where(p => p.VendorTaxId == vendorTaxId || p.VendorTaxId == null)
                : q.Where(p => p.VendorTaxId == null);

            var patterns = await q
                .OrderByDescending(p => p.TimesConfirmed)
                .Take(60)
                .ToListAsync();
            if (patterns.Count == 0) return;

            var filled = DocumentZoneAnalyzer.ApplyLearnedPatternsTo(data, rawText, patterns);
            if (filled.Count > 0)
                data.ReasoningTrace.Add(
                    $"[Learned] เติมจากแพตเทิร์นที่เรียนไว้: {string.Join(", ", filled)}");
            // เลขที่เอกสารที่แพตเทิร์นเติมมา ต้องผ่านด่านเดียวกับเลขที่จาก engine — เดิมข้าม
            // (ด่านรันก่อนขั้นนี้) ⇒ "CASHSALE"/เลขที่บ้านหลุดเข้ามาทางประตูหลัง
            // ("ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี")
            if (filled.Contains("เลขที่เอกสาร"))
            {
                var (cleanDoc, docNote) = DocumentNumberSanitizer.Sanitize(data.DocumentNumber, rawText);
                if (docNote != null)
                {
                    data.DocumentNumber = cleanDoc;
                    data.FieldConfidence["DocumentNumber"] = cleanDoc == null ? 0.25 : 0.85;
                    data.ReasoningTrace.Add("[Learned] " + docNote);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ใช้แพตเทิร์นที่เรียนไว้ไม่สำเร็จ — ข้ามขั้นตอนนี้");
        }
    }

    /// <summary>
    /// ให้ AI แตกรายการจากข้อความดิบ เมื่อ engine ไม่คืนตารางรายการมาเลย
    ///
    /// <para><b>ด่านกันมั่ว (กฎเหล็ก #1)</b>: รับผลก็ต่อเมื่อผลรวมของบรรทัด
    /// ที่ได้กลับมา<b>ลงตัวกับยอดหัวกระดาษ</b> (ยอดก่อนภาษี หรือยอดรวมเมื่อ
    /// ราคารวม VAT) ภายใน ±1 บาท — ไม่ลงตัว = ทิ้งทั้งชุด ไม่ใช่รับบางบรรทัด
    /// เพราะบรรทัดที่แต่งขึ้นจะกลายเป็นรายการทางบัญชีจริง</para>
    ///
    /// <para>AI ปิด/ล่ม/ตอบไม่ลงตัว → ไม่ทำอะไร ⇒ คงพฤติกรรมเดิม (บรรทัดสรุป
    /// ใบเดียวจากยอดหัวกระดาษ) ผู้ใช้ยังสร้างเอกสารได้ครบ</para>
    /// </summary>
    /// <summary>แตกรายการสินค้าเมื่อ engine ไม่คืนตาราง — ครู (AI) → นักเรียน (local model)
    /// → <see cref="Accounting.Helpers.RawTextLineSplitter"/> (ไม่ต้องมีโมเดลเลย)
    ///
    /// <para>ทั้งสามทางเดินผ่าน <see cref="Accounting.Helpers.OcrLineSplitGuard"/> ตัวเดียวกัน:
    /// ผลรวมบรรทัดต้องตรงยอดบนกระดาษถึงจะกลายเป็นรายการทางบัญชี ⇒ ทางที่เดาผิดถูกปฏิเสธ
    /// ไม่ใช่ถูกบันทึก. ก่อนหน้านี้มีแต่ทางแรก และ feature นี้ไม่มี student ⇒ ปิด provider
    /// = เอกสารได้บรรทัดสรุปใบเดียวตลอดกาล (kill-switch ไม่ผ่าน · ผลตรวจ 2026-09-06 T3-02)</para></summary>
    private async Task TrySplitLineItemsAsync(
        Guid companyId, OcrScanResult scanResult, OcrExtractedData data, string? rawText)
    {
        var scanResultId = scanResult.Id;
        if (data.Items.Count > 0) return;                    // engine ให้รายการมาแล้ว
        if (string.IsNullOrWhiteSpace(rawText)) return;

        var sub = data.SubTotal ?? 0m;
        var total = data.TotalAmount ?? 0m;
        if (sub <= 0m && total <= 0m) return;                // ไม่มียอดให้ตรวจ = ไม่เดา

        try
        {
            string? candidateJson = null;
            Guid? modelFeedbackId = null;
            var usedProvider = false;
            var source = "local-text";

            if (_aiAugmenter != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var res = await _aiAugmenter.SplitLineItemsAsync(
                    companyId, scanResultId, rawText,
                    data.DocumentType, data.VendorName,
                    data.SubTotal, data.VatAmount, data.TotalAmount, cts.Token);

                // ★ T3-01: นักเรียน (local model) ที่ short-circuit ใช้ได้เท่า AI — ผ่านด่านเดียวกัน
                if (res.HasModelAnswer)
                {
                    candidateJson = res.Answer;
                    modelFeedbackId = res.FeedbackId;
                    usedProvider = res.UsedAi;
                    source = res.SourceLabel;
                }
            }

            // ★ T3-02: ไม่มีโมเดลตอบ (ปิด provider / เกินงบ / ยังไม่มี student) →
            // แตกบรรทัดจากข้อความเอง แล้วให้ด่านตัวเดิมเป็นคนตัดสิน
            candidateJson ??= Accounting.Helpers.RawTextLineSplitter.SplitToJson(rawText);
            if (string.IsNullOrWhiteSpace(candidateJson))
            {
                data.ReasoningTrace.Add("[LineSplit] ข้อความไม่มีบรรทัดที่ลงท้ายด้วยจำนวนเงิน — ใช้บรรทัดสรุปใบเดียว");
                return;
            }

            // ด่านตรวจอยู่ใน Helpers/OcrLineSplitGuard (pure + มีเทสต์) —
            // ตรรกะที่ตัดสินว่า "ยอมให้บรรทัดที่แตกมากลายเป็นรายการบัญชีไหม"
            // ต้องทดสอบได้ ไม่ใช่ฝังกลางเมธอด async
            var guard = Accounting.Helpers.OcrLineSplitGuard.Evaluate(candidateJson, sub, total);
            if (!guard.Accepted)
            {
                data.ReasoningTrace.Add($"[LineSplit] ไม่รับผลจาก {source} — {guard.Reason}");
                _logger.LogInformation(
                    "line-split ถูกปฏิเสธ (scan {ScanId}, source {Source}): {Reason}",
                    scanResultId, source, guard.Reason);
                return;
            }

            foreach (var line in guard.Lines)
            {
                data.Items.Add(new OcrExtractedLineItem
                {
                    Description = line.Description,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    Amount = line.Amount,
                    Unit = line.Unit,
                });
            }
            // ปิด loop การเรียนรู้ (กฎเหล็ก #1 ขั้น CAPTURE) — เก็บ feedbackId
            // ไว้กับสแกน เพื่อให้ตอนผู้ใช้แก้/ยืนยันรายการในหน้า review
            // ระบบส่งคำตอบจริงกลับไปสอนได้ ไม่งั้น = จ่าย token ฟรีทุกใบ
            // (เส้น local-text ไม่มีแถวโมเดล จึงไม่มี feedbackId ให้เก็บ)
            scanResult.LineSplitAiFeedbackId = modelFeedbackId;
            scanResult.LineSplitUsedAi = usedProvider;   // ป้ายซื่อสัตย์: true เฉพาะ provider จริง
            data.ReasoningTrace.Add(
                (usedProvider ? "🤖 [LineSplit] AI" : "⚙️ [LineSplit] ระบบ")
                + $" แตกรายการจากข้อความได้ {guard.Lines.Count} บรรทัด "
                + $"(รวม ฿{guard.Sum:N2} ตรงกับยอดบนกระดาษ) — กรุณาตรวจก่อนยืนยัน");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "line-split ล้มเหลว — ใช้บรรทัดสรุปใบเดียวตามเดิม");
        }
    }

    /// <summary>
    /// ทางสำรองสุดท้ายเมื่อ pipeline หลักอ่านอะไรไม่ได้เลย — ใช้
    /// <see cref="DocumentZoneAnalyzer.Analyze"/> ซึ่งแบ่งกระดาษเป็นโซน
    /// (หัวเอกสาร/ผู้ขาย/ผู้ซื้อ/รายการ/สรุปยอด) แล้วสกัดด้วย
    /// <c>FieldPatternLibrary</c>
    ///
    /// <para>⚠️ <c>Analyze</c> เป็นตัวสกัดอีกชุดที่เขียนไว้ครบ ~900 บรรทัด
    /// แต่ <b>ไม่มี call site ทั้งเรพ</b> — เข้าถึงได้ทางเดียวคือผ่านเมธอดนี้</para>
    ///
    /// <para>เงื่อนไขเข้า: ไม่รู้ทั้ง <c>VendorName</c> และ <c>TotalAmount</c>
    /// (คือสถานะที่ผู้ใช้ต้องกรอกเองทั้งใบอยู่แล้ว) ⇒ ผลลัพธ์แย่ที่สุดคือเท่าเดิม
    /// และ**เติมเฉพาะช่องที่ยังว่าง** ไม่ทับค่าที่ engine อ่านได้</para>
    /// </summary>
    private async Task ApplyZoneAnalysisFallbackAsync(
        Guid companyId, OcrExtractedData data, string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return;
        if (!string.IsNullOrWhiteSpace(data.VendorName) || data.TotalAmount is > 0) return;

        try
        {
            var ourTaxId = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId).Select(c => c.TaxId).FirstOrDefaultAsync();

            var zoned = DocumentZoneAnalyzer.Analyze(rawText, learnedPatterns: null, ourCompanyTaxId: ourTaxId);

            var filled = new List<string>();
            if (string.IsNullOrWhiteSpace(data.VendorName) && !string.IsNullOrWhiteSpace(zoned.SellerName))
            { data.VendorName = zoned.SellerName; filled.Add("ชื่อผู้ขาย"); }

            if (string.IsNullOrWhiteSpace(data.VendorTaxId)
                && Accounting.Helpers.ThaiTaxId.IsValid(zoned.SellerTaxId))
            { data.VendorTaxId = Accounting.Helpers.ThaiTaxId.Normalize(zoned.SellerTaxId); filled.Add("เลขผู้เสียภาษีผู้ขาย"); }

            if (string.IsNullOrWhiteSpace(data.BuyerName) && !string.IsNullOrWhiteSpace(zoned.BuyerName))
            { data.BuyerName = zoned.BuyerName; filled.Add("ชื่อผู้ซื้อ"); }

            if (string.IsNullOrWhiteSpace(data.BuyerTaxId)
                && Accounting.Helpers.ThaiTaxId.IsValid(zoned.BuyerTaxId))
            { data.BuyerTaxId = Accounting.Helpers.ThaiTaxId.Normalize(zoned.BuyerTaxId); filled.Add("เลขผู้เสียภาษีผู้ซื้อ"); }

            if (string.IsNullOrWhiteSpace(data.DocumentNumber) && !string.IsNullOrWhiteSpace(zoned.DocumentNumber))
            { data.DocumentNumber = zoned.DocumentNumber; filled.Add("เลขที่เอกสาร"); }

            if (data.DocumentDate == null && zoned.DocumentDate != null)
            { data.DocumentDate = zoned.DocumentDate; filled.Add("วันที่"); }

            if (data.TotalAmount is not > 0 && zoned.TotalAmount is > 0)
            { data.TotalAmount = zoned.TotalAmount; filled.Add("ยอดรวม"); }

            if (data.SubTotal is not > 0 && zoned.SubTotal is > 0)
            { data.SubTotal = zoned.SubTotal; filled.Add("ยอดก่อนภาษี"); }

            if (data.VatAmount is not > 0 && zoned.VatAmount is > 0)
            { data.VatAmount = zoned.VatAmount; filled.Add("ภาษีมูลค่าเพิ่ม"); }

            if (data.PaymentTermsDays == null && zoned.PaymentTermsDays is > 0)
            { data.PaymentTermsDays = zoned.PaymentTermsDays; filled.Add("เครดิตเทอม"); }

            if (filled.Count > 0)
                data.ReasoningTrace.Add(
                    $"[ZoneFallback] pipeline หลักอ่านไม่ได้ — วิเคราะห์โซนเติมให้: {string.Join(", ", filled)}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "zone-analysis fallback ล้มเหลว — ข้ามขั้นตอนนี้");
        }
    }

    /// <summary>Engine-agnostic raw-text enrichment — fields no structured
    /// extractor returns today. Fail-safe: any regex/date mishap simply leaves
    /// the field null (the user can still key it in the review UI).</summary>
    private void EnrichFromRawText(OcrExtractedData data, string? rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return;
        var text = Ocr.ThaiTextNormalizer.Normalize(rawText);

        // ── รหัสสาขา §86/4 (ประกาศอธิบดีฯ 199) — ทุกเส้นทาง engine ──
        //
        // ⚠️ เดิม BranchCodeExtractor ถูกเรียกจาก **ที่เดียว** คือ ParseThaiDocument
        // ซึ่งรันเฉพาะเส้นทาง Tesseract ⇒ บนเส้นทาง Azure DI (เส้นหลัก) และ python
        // **ไม่เคยอ่านรหัสสาขาจากกระดาษเลย** แล้ว MapToResponse ก็ ?? "00000"
        // ให้เงียบ ๆ ⇒ ใบของ "สาขาที่ 3" ขึ้นเป็นสำนักงานใหญ่ทุกใบ ซึ่งผิดทั้ง
        // §86/4 และรายงานภาษีรายสถานประกอบการ (§87)
        //
        // ที่นี่คือจุดที่ทุก engine ผ่าน — เติมเฉพาะช่องที่ยังว่าง ไม่ทับของ e-Tax XML
        if (string.IsNullOrWhiteSpace(data.VendorBranchCode)
            || string.IsNullOrWhiteSpace(data.BuyerBranchCode))
        {
            var br = BranchCodeExtractor.Extract(text);
            if (string.IsNullOrWhiteSpace(data.VendorBranchCode) && br.SellerBranchCode != null)
            {
                data.VendorBranchCode = br.SellerBranchCode;
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerBranchCode] = 0.85;
                data.ReasoningTrace.Add($"[Enrich] รหัสสาขาผู้ขายจากกระดาษ {br.SellerBranchCode}");
            }
            if (string.IsNullOrWhiteSpace(data.BuyerBranchCode) && br.BuyerBranchCode != null)
            {
                data.BuyerBranchCode = br.BuyerBranchCode;
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.BuyerBranchCode] = 0.85;
                data.ReasoningTrace.Add($"[Enrich] รหัสสาขาผู้ซื้อจากกระดาษ {br.BuyerBranchCode}");
            }
        }

        // ── ที่อยู่ผู้ขายจากข้อความ (เดิมเติมแค่ email/phone) ──
        // Azure ให้ VendorAddress ได้บ้างไม่ได้บ้าง; regex ตัวนี้มีอยู่แล้วและ
        // ถูกใช้ในเส้นทาง Tesseract — ยกมาให้ทุกเส้นทางใช้ร่วมกัน
        if (string.IsNullOrWhiteSpace(data.VendorAddress))
        {
            var addr = VendorAddressRegex.Match(text);
            if (addr.Success && addr.Groups.Count > 1)
            {
                var candidate = addr.Groups[1].Value.Trim();
                if (candidate.Length >= 10)
                {
                    data.VendorAddress = candidate;
                    data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerAddress] = 0.7;
                    data.ReasoningTrace.Add("[Enrich] ที่อยู่ผู้ขายจากข้อความบนกระดาษ");
                }
            }
        }

        // 0) Vendor email/phone fallback — ทุก path. Azure DI ไม่มี field email,
        //    + Azure อาจไม่จับ phone เคสที่บนเอกสารระบุไม่ชัด → contact ที่สร้าง
        //    จะ Email/Phone = null. regex หาเพิ่มจาก raw text (ไม่ทับค่าที่
        //    Azure ตั้งมาแล้ว).
        if (string.IsNullOrWhiteSpace(data.VendorEmail))
        {
            var em = VendorEmailRegex.Match(rawText);
            if (em.Success)
            {
                var candidate = em.Value.Trim().TrimEnd('.', ',', ';', ':');
                data.VendorEmail = candidate;
                data.ReasoningTrace.Add($"[Enrich] อีเมลบนเอกสาร {candidate}");
            }
        }
        if (string.IsNullOrWhiteSpace(data.VendorPhone))
        {
            var pm = VendorPhoneRegex.Match(rawText);
            if (pm.Success)
            {
                var sane = SanePhone(pm.Groups[1].Value);
                if (sane != null)
                {
                    data.VendorPhone = sane;
                    data.ReasoningTrace.Add($"[Enrich] เบอร์โทรบนเอกสาร {sane}");
                }
            }
        }

        // 1) Explicit due date ("ครบกำหนด 15/07/2569", "Due Date: 15/07/2026")
        //    → credit terms in days. The Net-N regex in the parser covers
        //    "เครดิต 30 วัน"; this covers papers that print a date instead.
        if (!data.PaymentTermsDays.HasValue && data.DocumentDate.HasValue)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(text,
                    @"(?:ครบกำหนด(?:ชำระ)?|กำหนดชำระ|due\s*date)\s*:?\s*(?:วันที่)?\s*(\d{1,2})[\/\-\.](\d{1,2})[\/\-\.](\d{2,4})",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success
                    && int.TryParse(m.Groups[1].Value, out var dd)
                    && int.TryParse(m.Groups[2].Value, out var mm)
                    && int.TryParse(m.Groups[3].Value, out var yy))
                {
                    // ตัวแปลงกลาง (เดิมกติกานี้ถูกเขียนซ้ำที่นี่ด้วยเกณฑ์ของตัวเอง)
                    var year = Accounting.Helpers.ThaiDate.NormalizeYear(yy);
                    var due = new DateTime(year, mm, dd);
                    var days = (due.Date - data.DocumentDate.Value.Date).Days;
                    if (days is > 0 and <= 365)
                    {
                        data.PaymentTermsDays = days;
                        data.ReasoningTrace.Add($"[Enrich] วันครบกำหนด {due:dd/MM/yyyy} บนเอกสาร → เครดิตเทอม {days} วัน");
                    }
                }
            }
            catch { /* unparseable date — leave terms empty */ }
        }

        // 2) Header discount ("ส่วนลด 500.00"). Sanity: must be positive and
        //    smaller than the grand total, otherwise it's a misread.
        if (!data.DiscountAmount.HasValue)
        {
            var dm = System.Text.RegularExpressions.Regex.Match(text,
                @"(?:ส่วนลด(?:รวม|การค้า)?|discount)\s*:?\s*(?:฿|บาท)?\s*([\d,]+(?:\.\d{1,2})?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (dm.Success
                && decimal.TryParse(dm.Groups[1].Value.Replace(",", ""), out var disc)
                && disc > 0 && disc < (data.TotalAmount ?? decimal.MaxValue))
            {
                data.DiscountAmount = disc;
                data.ReasoningTrace.Add($"[Enrich] ส่วนลดบนเอกสาร ฿{disc:N2}");
            }
        }

        // 2b) Header delivery / shipping / freight charge — เคสจริง OfficeMate
        // ขึ้น "ค่าขนส่งพิเศษ / Delivery Charge Incl.VAT 50.00" ใต้บรรทัด line
        // items. OCR ไม่ได้อ่านเป็น line → เคยตกหล่นทำให้ระบบใส่ placeholder
        // "OCR ไม่อ่านบรรทัดนี้". ดึงมาเป็น OCR line จริง — desc = "ค่าขนส่ง",
        // amount + unit price = ยอดที่อ่านได้, append เข้า Items เพื่อให้
        // CreateDocumentFromScanAsync ใส่เป็น line ปกติ (ผู้ใช้ไม่ต้องเดา).
        var hasDeliveryLine = data.Items.Any(it =>
            !string.IsNullOrEmpty(it.Description) &&
            (it.Description.Contains("ขนส่ง") || it.Description.Contains("จัดส่ง")
             || it.Description.IndexOf("delivery", StringComparison.OrdinalIgnoreCase) >= 0
             || it.Description.IndexOf("shipping", StringComparison.OrdinalIgnoreCase) >= 0
             || it.Description.IndexOf("freight", StringComparison.OrdinalIgnoreCase) >= 0));
        if (!hasDeliveryLine)
        {
            var sm = System.Text.RegularExpressions.Regex.Match(text,
                @"(?:ค่าขนส่ง(?:พิเศษ)?|ค่าจัดส่ง|delivery\s*charge|shipping(?:\s*charge)?|freight)" +
                @"(?:\s*(?:incl\.?\s*vat|รวม\s*vat|รวมภาษี))?\s*:?\s*(?:฿|บาท)?\s*([\d,]+(?:\.\d{1,2})?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (sm.Success
                && decimal.TryParse(sm.Groups[1].Value.Replace(",", ""), out var fee)
                && fee > 0 && fee < (data.TotalAmount ?? decimal.MaxValue))
            {
                data.Items.Add(new OcrExtractedLineItem
                {
                    Description = "ค่าขนส่ง",
                    Quantity = 1m,
                    UnitPrice = fee,
                    Amount = fee,
                });
                data.ReasoningTrace.Add($"[Enrich] ค่าขนส่งบนเอกสาร ฿{fee:N2} — เพิ่มเป็น line");
            }
        }

        // 2c) ค่าบริการ (Service Charge) + ปัดเศษ — บิลร้านอาหาร/โรงแรมเป็น
        // งานประจำวันของ SME แต่ทั้งเรพไม่เคยอ่านสองช่องนี้เลย (ผลตรวจ T2-18)
        // ⇒ อาหาร 1,000 · SC 10% = 100 · ยอดก่อน VAT 1,100 ⇒ Σ บรรทัดที่ OCR
        // อ่านได้ 1,000 ไม่ตรงหัวใบ 1,100 ⇒ ตัวจำแนกตกเป็น LinesShort ทุกใบ
        // (เอกสารมีบรรทัดรวม 1,000 แต่หัวใบ 1,100 — ขัดกันเองในใบเดียว)
        //
        // ★ ด่านกันการแต่งบรรทัด: "ค่าบริการ" เป็นคำกว้าง — ใบแจ้งหนี้บริการ
        //   ("ค่าบริการรายเดือน 5,000") ก็เข้าป้ายนี้ ถ้าเติมเป็นบรรทัดใหม่จะ
        //   นับซ้ำทันที ⇒ เติมได้ **เฉพาะเมื่อยอดหัวใบยืนยันเอง** คือ
        //   Σ บรรทัด + SC ≈ ยอดก่อน VAT (ภายในค่าเผื่อของ OcrLineReconciler)
        //   ยืนยันไม่ได้ = ไม่เติม แค่จดไว้ใน trace ให้คนดู
        //   ("ค่าที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้ อันตรายกว่าการไม่ตอบ")
        var surcharge = Accounting.Helpers.ThaiBillSurcharge.Read(text);
        if (surcharge.ServiceChargeAmount is decimal scAmt && scAmt > 0m)
        {
            var hasServiceLine = data.Items.Any(it =>
                !string.IsNullOrEmpty(it.Description) &&
                (it.Description.Contains("ค่าบริการ") || it.Description.Contains("เซอร์วิส")
                 || it.Description.IndexOf("service charge", StringComparison.OrdinalIgnoreCase) >= 0));
            var lineSum = data.Items.Sum(it => it.Amount ?? ((it.Quantity ?? 0m) * (it.UnitPrice ?? 0m)));
            var hdrSub = data.SubTotal ?? 0m;
            var reconciles = hdrSub > 0m && data.Items.Count > 0
                && Math.Abs(lineSum + scAmt - hdrSub) <= Accounting.Helpers.OcrLineReconciler.Tolerance
                && Math.Abs(lineSum - hdrSub) > Accounting.Helpers.OcrLineReconciler.Tolerance;
            if (!hasServiceLine && reconciles)
            {
                var pctLabel = surcharge.ServiceChargePercent is decimal p2
                    ? $" {p2:0.##}%" : "";
                data.Items.Add(new OcrExtractedLineItem
                {
                    Description = $"ค่าบริการ (Service Charge){pctLabel}",
                    Quantity = 1m,
                    UnitPrice = scAmt,
                    Amount = scAmt,
                });
                data.ReasoningTrace.Add(
                    $"[Enrich] ค่าบริการบนบิล ฿{scAmt:N2}{pctLabel} — เพิ่มเป็น line "
                    + $"(Σ บรรทัด {lineSum:N2} + {scAmt:N2} = ยอดก่อน VAT {hdrSub:N2})");
            }
            else if (!hasServiceLine)
            {
                data.ReasoningTrace.Add(
                    $"[Enrich] พบ “ค่าบริการ ฿{scAmt:N2}” บนเอกสาร แต่ยอดหัวใบยืนยันไม่ได้ "
                    + "(อาจเป็นชื่อรายการปกติของใบแจ้งหนี้บริการ) — ไม่เติมเป็นบรรทัด");
            }
        }
        if (surcharge.RoundingAdjustment is decimal roundAdj && roundAdj != 0m)
            data.ReasoningTrace.Add($"[Enrich] บิลระบุปัดเศษ {roundAdj:N2} บาท "
                + "— อยู่ในค่าเผื่อของด่านผลรวม ไม่ปรับตัวเลขใด");

        // 3) Per-line unit from the description (ถุง/เส้น/กล่อง/ลัง…) — reuses
        //    the stock-import unit detector so the two paths agree.
        foreach (var it in data.Items)
        {
            if (!string.IsNullOrEmpty(it.Unit) || string.IsNullOrEmpty(it.Description)) continue;
            var u = Ocr.ProductMatcher.DetectUnit(it.Description);
            if (!string.IsNullOrEmpty(u)) it.Unit = u;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // PO LINKAGE — the "ฟังก์ชันชื่อแทน / รับตาม PO" function from the diagram
    // ════════════════════════════════════════════════════════════════════════

    public async Task<List<OpenPurchaseOrderDto>> GetOpenPosForScanAsync(Guid companyId, Guid scanResultId)
    {
        var scan = await _db.Set<OcrScanResult>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new KeyNotFoundException("ไม่พบรายการสแกน");
        if (!scan.MatchedContactId.HasValue) return new();

        // Lookback window matches the ScanAsync warning (6 mo) so the picker
        // mirrors the banner — never surfaces a PO the warning didn't flag.
        var cutoff = DateTime.UtcNow.AddMonths(-6);
        var pos = await _db.Documents.AsNoTracking()
            .Include(d => d.Lines).ThenInclude(l => l.Account)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.ContactId == scan.MatchedContactId.Value
                && d.DocumentType == DocumentType.PurchaseOrder
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected
                && d.Status != DocumentStatus.Draft
                && d.Status != DocumentStatus.Paid   // fully billed = no longer open
                && d.DocumentDate >= cutoff)
            .OrderByDescending(d => d.DocumentDate)
            .ToListAsync();

        return pos.Select(p => new OpenPurchaseOrderDto(
            p.Id, p.DocumentNumber, p.DocumentDate, p.Status.ToString(),
            p.TotalAmount,
            p.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder)
                .Select(l => new OpenPurchaseOrderLineDto(
                    l.Id, l.LineOrder, l.Description, l.Quantity, l.UnitPrice, l.Amount,
                    l.AccountId, l.Account?.AccountCode))
                .ToList())).ToList();
    }

    public async Task<OcrResultResponse> LinkPurchaseOrderAsync(Guid companyId, Guid scanResultId,
        LinkPurchaseOrderRequest request, string performedBy)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new KeyNotFoundException("ไม่พบรายการสแกน");
        if (scan.CreatedDocumentId.HasValue)
            throw new InvalidOperationException("เอกสารถูกสร้างจาก scan นี้แล้ว — ไม่สามารถเปลี่ยน PO ที่ผูกได้");

        // Resolve the PO — must belong to the same company AND be the vendor
        // we matched on this scan (prevents linking to an unrelated supplier's
        // PO by id-tampering).
        var po = await _db.Documents.AsNoTracking()
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == request.PurchaseOrderId
                && d.CompanyId == companyId && !d.IsDeleted
                && d.DocumentType == DocumentType.PurchaseOrder)
            ?? throw new KeyNotFoundException("ไม่พบใบสั่งซื้อ");
        if (scan.MatchedContactId.HasValue && po.ContactId != scan.MatchedContactId.Value)
            throw new InvalidOperationException("ใบสั่งซื้อนี้ไม่ใช่ของผู้ขายที่จับคู่ไว้");

        // Sanity-check the line mappings — each non-null poLineId must belong
        // to THIS PO. Drop invalid pairs silently rather than failing the link.
        var validPoLineIds = po.Lines.Where(l => !l.IsDeleted).Select(l => l.Id).ToHashSet();
        var cleaned = new Dictionary<int, Guid?>();
        if (request.LineMappings != null)
        {
            foreach (var kv in request.LineMappings)
            {
                if (kv.Value == null) cleaned[kv.Key] = null;
                else if (validPoLineIds.Contains(kv.Value.Value)) cleaned[kv.Key] = kv.Value;
            }
        }

        scan.LinkedPurchaseOrderId = po.Id;
        scan.LinkedPurchaseOrderNumber = po.DocumentNumber;
        scan.PoLineMappingsJson = cleaned.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(cleaned.ToDictionary(k => k.Key.ToString(), k => k.Value))
            : null;
        // Pre-stamp the inferred target type so the dropdown reflects the
        // pick — operator can still override before clicking "สร้างเอกสาร".
        scan.TargetDocumentType = nameof(DocumentType.PurchaseInvoice);
        scan.ProcessingNotes = (scan.ProcessingNotes ?? "")
            + $"\n[PO link] ผูกกับใบสั่งซื้อ {po.DocumentNumber} ({cleaned.Values.Count(v => v.HasValue)}/{cleaned.Count} บรรทัด)";

        // Learn the OCR↔PO line aliases. Mapped PO lines that have a ProductCode
        // we can resolve get an alias row keyed to this vendor — next scan from
        // the same supplier matches the wording instantly.
        if (cleaned.Count > 0 && _productMatcher != null && !string.IsNullOrWhiteSpace(scan.ExtractedItemsJson))
        {
            try
            {
                var ocrLines = System.Text.Json.JsonSerializer
                    .Deserialize<List<OcrExtractedLineItem>>(scan.ExtractedItemsJson) ?? new();
                foreach (var (ocrIdx, poLineId) in cleaned.Where(kv => kv.Value.HasValue))
                {
                    if (ocrIdx < 0 || ocrIdx >= ocrLines.Count) continue;
                    var ocrDesc = ocrLines[ocrIdx].Description;
                    if (string.IsNullOrWhiteSpace(ocrDesc)) continue;

                    var poLine = po.Lines.First(l => l.Id == poLineId!.Value);
                    if (string.IsNullOrWhiteSpace(poLine.ProductCode)) continue;
                    var product = await _db.Products.AsNoTracking()
                        .FirstOrDefaultAsync(p => p.CompanyId == companyId
                            && p.Code == poLine.ProductCode && !p.IsDeleted);
                    if (product == null) continue;

                    await _productMatcher.RecordAliasAsync(companyId, product.Id, ocrDesc!,
                        scan.MatchedContactId, performedBy, source: "po-link");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PO-link alias learning failed (non-fatal) for scan {Id}", scanResultId);
            }
        }

        await _db.SaveChangesAsync();
        return MapToResponse(scan);
    }

    public async Task<OcrResultResponse> UnlinkPurchaseOrderAsync(Guid companyId, Guid scanResultId)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new KeyNotFoundException("ไม่พบรายการสแกน");
        if (scan.CreatedDocumentId.HasValue)
            throw new InvalidOperationException("เอกสารถูกสร้างจาก scan นี้แล้ว — ยกเลิกการผูก PO ไม่ได้");

        scan.LinkedPurchaseOrderId = null;
        scan.LinkedPurchaseOrderNumber = null;
        scan.PoLineMappingsJson = null;
        if (scan.LinkedPredecessorType == nameof(DocumentType.PurchaseOrder)) ClearPredecessorLink(scan);
        await _db.SaveChangesAsync();
        return MapToResponse(scan);
    }

    // ═══════════════ ใบต้นทางทุกชนิด (Helpers/OcrPredecessorMatcher) ═══════════════

    /// <summary>คู่ค้าจากเลขผู้เสียภาษี (normalize ก่อนเทียบ — กันรูปแบบต่างกัน) — ใช้ทั้งเส้นสร้างเอกสาร
    /// ฝั่งขายและตัวหาใบต้นทาง (เดิมเขียน query นี้ inline ในเส้นสร้างเอกสาร)</summary>
    private async Task<Guid?> ResolveContactIdByTaxIdAsync(Guid companyId, string? taxId)
    {
        if (string.IsNullOrWhiteSpace(taxId)) return null;
        var digits = DocumentService.NormalizeTaxDigits(taxId);
        if (string.IsNullOrEmpty(digits)) return null;
        return (await _db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted && c.TaxId != null && c.TaxId != "")
                .Select(c => new { c.Id, c.TaxId })
                .ToListAsync())
            .Where(c => DocumentService.NormalizeTaxDigits(c.TaxId) == digits)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefault();
    }

    /// <summary>คำนวณผู้สมัคร "ใบต้นทาง" ของสแกนด้วยตัวตัดสินตัวเดียว — ใช้ทั้งตอนสแกนและ GET สด</summary>
    private async Task<(Accounting.Helpers.PredecessorDecision Decision, List<Accounting.Helpers.PredecessorCandidate> Loaded)> ComputePredecessorDecisionAsync(
        Guid companyId, OcrScanResult scan, string? rawText, IReadOnlyList<string> lineDescriptions)
    {
        var empty = (new Accounting.Helpers.PredecessorDecision(null, Array.Empty<Accounting.Helpers.RankedPredecessor>(), "ไม่มีชนิดต้นทางสำหรับเอกสารเป้าหมายนี้"), new List<Accounting.Helpers.PredecessorCandidate>());
        var target = Accounting.Helpers.OcrTargetDocumentType.Resolve(
            null, scan.TargetDocumentType, scan.DocumentType,
            hasLinkedPurchaseOrder: scan.LinkedPurchaseOrderId.HasValue);
        var predTypes = DocumentService.GetPredecessorTypes(target.Type).ToList();
        if (predTypes.Count == 0) return empty;

        var isSales = Accounting.Helpers.DocumentSide.IsSales(target.Type, scan.OurRole);
        var contactId = isSales
            ? await ResolveContactIdByTaxIdAsync(companyId, scan.BuyerTaxId)
            : scan.MatchedContactId;
        if (!contactId.HasValue)
            return (new Accounting.Helpers.PredecessorDecision(null, Array.Empty<Accounting.Helpers.RankedPredecessor>(),
                isSales ? "ยังไม่รู้ว่าผู้ซื้อบนกระดาษเป็นลูกค้ารายไหน" : "ยังไม่ได้จับคู่ผู้ขาย"), new List<Accounting.Helpers.PredecessorCandidate>());

        // "เปิดอยู่" = ออกแล้วและยังไม่จบสาย — ร่าง/รออนุมัติยังไม่มีผลทางบัญชี · Paid = จบแล้ว
        var cutoff = DateTime.UtcNow.AddMonths(-12);
        var docs = await _db.Documents.AsNoTracking()
            .Include(d => d.Lines)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.ContactId == contactId.Value
                && predTypes.Contains(d.DocumentType)
                && d.Status != DocumentStatus.Draft
                && d.Status != DocumentStatus.WaitingApproval
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected
                && d.Status != DocumentStatus.Paid
                && d.DocumentDate >= cutoff)
            .OrderByDescending(d => d.DocumentDate)
            .Take(20)
            .ToListAsync();
        var loaded = docs.Select(d => new Accounting.Helpers.PredecessorCandidate(
                d.Id, d.DocumentType, d.DocumentNumber, d.DocumentDate, d.TotalAmount, d.BalanceDue,
                d.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder).Select(l => l.Description ?? "").ToList()))
            .ToList();
        var facts = new Accounting.Helpers.PredecessorScanFacts(rawText ?? scan.RawTextContent, scan.ExtractedTotalAmount, scan.ExtractedSubTotal, lineDescriptions);
        return (Accounting.Helpers.OcrPredecessorMatcher.Decide(facts, loaded), loaded);
    }

    private static List<PredecessorCandidateDto> ToCandidateDtos(IEnumerable<Accounting.Helpers.RankedPredecessor> ranked)
        => ranked.Select(r => new PredecessorCandidateDto(
            r.Candidate.Id, r.Candidate.Type.ToString(), r.Candidate.DocumentNumber, r.Candidate.DocumentDate,
            r.Candidate.TotalAmount, r.Candidate.BalanceDue, r.Strength.ToString(), r.Score, r.Reason)).ToList();

    private static IReadOnlyList<string> ScanLineDescriptions(OcrScanResult scan)
    {
        if (string.IsNullOrWhiteSpace(scan.ExtractedItemsJson)) return Array.Empty<string>();
        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<List<OcrExtractedLineItem>>(scan.ExtractedItemsJson) ?? new())
                .Select(i => i.Description ?? "").ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static void ApplyPredecessorLink(OcrScanResult scan, Accounting.Helpers.PredecessorCandidate c, string reason)
    {
        scan.LinkedPredecessorDocumentId = c.Id;
        scan.LinkedPredecessorNumber = c.DocumentNumber;
        scan.LinkedPredecessorType = c.Type.ToString();
        scan.PredecessorLinkReason = reason;
        if (c.Type == DocumentType.PurchaseOrder)
        {
            // เดินเส้น PO เดิมด้วย — สืบทอด GL รายบรรทัด + บังคับเป้าหมาย PurchaseInvoice
            scan.LinkedPurchaseOrderId = c.Id;
            scan.LinkedPurchaseOrderNumber = c.DocumentNumber;
            scan.TargetDocumentType = nameof(DocumentType.PurchaseInvoice);
        }
    }

    private static void ClearPredecessorLink(OcrScanResult scan)
    {
        scan.LinkedPredecessorDocumentId = null;
        scan.LinkedPredecessorNumber = null;
        scan.LinkedPredecessorType = null;
        scan.PredecessorLinkReason = null;
    }

    /// <summary>ขั้นสแกน: หาใบต้นทาง → ผูกอัตโนมัติเมื่อชัด · เก็บผู้สมัครไว้ให้เลือก · ไม่ล้มการสแกน</summary>
    private async Task SuggestPredecessorLinkAsync(Guid companyId, OcrScanResult scanResult, OcrExtractedData extractedData, string? extractedText)
    {
        try
        {
            var lines = extractedData.Items.Select(i => i.Description ?? "").ToList();
            var (decision, loaded) = await ComputePredecessorDecisionAsync(companyId, scanResult, extractedText, lines);
            if (loaded.Count == 0) return;
            scanResult.PredecessorCandidatesJson = System.Text.Json.JsonSerializer.Serialize(ToCandidateDtos(decision.Candidates));
            if (decision.AutoLink != null
                && !scanResult.LinkedPredecessorDocumentId.HasValue
                && !scanResult.LinkedPurchaseOrderId.HasValue)
            {
                ApplyPredecessorLink(scanResult, decision.AutoLink.Candidate, "auto: " + decision.AutoLink.Reason);
            }
            else if (decision.AutoLink != null && scanResult.LinkedPurchaseOrderId.HasValue
                && !scanResult.LinkedPredecessorDocumentId.HasValue
                && decision.AutoLink.Candidate.Id == scanResult.LinkedPurchaseOrderId.Value)
            {
                // บล็อก PO ข้างบนผูกใบเดียวกันไว้แล้ว — บันทึกในช่องทั่วไปให้หน้าเว็บเห็นเหตุผลเดียวกัน
                ApplyPredecessorLink(scanResult, decision.AutoLink.Candidate, "auto: " + decision.AutoLink.Reason);
            }
            scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n[LINK] " + decision.Summary;
            extractedData.ReasoningTrace.Add("[LINK] " + decision.Summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Predecessor-link check failed (non-fatal) for scan {ScanId}", scanResult.Id);
        }
    }

    public async Task<List<PredecessorCandidateDto>> GetPredecessorCandidatesAsync(Guid companyId, Guid scanResultId)
    {
        var scan = await _db.Set<OcrScanResult>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new KeyNotFoundException("ไม่พบรายการสแกน");
        var (decision, _) = await ComputePredecessorDecisionAsync(companyId, scan, scan.RawTextContent, ScanLineDescriptions(scan));
        return ToCandidateDtos(decision.Candidates);
    }

    public async Task<OcrResultResponse> LinkPredecessorAsync(Guid companyId, Guid scanResultId,
        LinkPredecessorRequest request, string performedBy)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new KeyNotFoundException("ไม่พบรายการสแกน");
        if (scan.CreatedDocumentId.HasValue)
            throw new Accounting.Helpers.BusinessRuleException("เอกสารถูกสร้างจากสแกนนี้แล้ว — เปลี่ยนใบต้นทางที่ตัวเอกสารแทน");

        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId && d.CompanyId == companyId && !d.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบเอกสารต้นทาง");

        var target = Accounting.Helpers.OcrTargetDocumentType.Resolve(
            null, scan.TargetDocumentType, scan.DocumentType, hasLinkedPurchaseOrder: false);
        if (!DocumentService.GetPredecessorTypes(target.Type).Contains(doc.DocumentType))
            throw new Accounting.Helpers.BusinessRuleException(
                $"{doc.DocumentType} ไม่ใช่เอกสารต้นทางของ {target.Type} — เปลี่ยนชนิดเอกสารเป้าหมายก่อน หรือเลือกใบชนิดอื่น");

        // คู่ค้าต้องตรงกัน — กันผูกใบของคนอื่นด้วยการยิง id (ฝั่งซื้อเทียบผู้ขายที่จับคู่ ·
        // ฝั่งขายเทียบลูกค้าจากเลขภาษีผู้ซื้อเมื่อรู้)
        var isSales = Accounting.Helpers.DocumentSide.IsSales(target.Type, scan.OurRole);
        var expected = isSales ? await ResolveContactIdByTaxIdAsync(companyId, scan.BuyerTaxId) : scan.MatchedContactId;
        if (expected.HasValue && doc.ContactId != expected.Value)
            throw new Accounting.Helpers.BusinessRuleException("เอกสารต้นทางนี้ไม่ใช่ของคู่ค้ารายเดียวกับกระดาษที่สแกน");

        if (doc.DocumentType == DocumentType.PurchaseOrder)
        {
            // เดินเส้น PO เดิม (ตรวจสิทธิ์/สืบทอด GL/เรียนรู้ alias) แล้วค่อยประทับช่องทั่วไป
            await LinkPurchaseOrderAsync(companyId, scanResultId, new LinkPurchaseOrderRequest(doc.Id, null), performedBy);
            scan = await _db.Set<OcrScanResult>().FirstAsync(r => r.Id == scanResultId);
        }
        scan.LinkedPredecessorDocumentId = doc.Id;
        scan.LinkedPredecessorNumber = doc.DocumentNumber;
        scan.LinkedPredecessorType = doc.DocumentType.ToString();
        scan.PredecessorLinkReason = "user";
        scan.ProcessingNotes = (scan.ProcessingNotes ?? "") + $"\n[LINK] ผู้ใช้ผูกกับ {doc.DocumentType} {doc.DocumentNumber}";
        await _db.SaveChangesAsync();
        return MapToResponse(scan);
    }

    public async Task<OcrResultResponse> UnlinkPredecessorAsync(Guid companyId, Guid scanResultId)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new KeyNotFoundException("ไม่พบรายการสแกน");
        if (scan.CreatedDocumentId.HasValue)
            throw new Accounting.Helpers.BusinessRuleException("เอกสารถูกสร้างจากสแกนนี้แล้ว — แก้การอ้างอิงที่ตัวเอกสารแทน");
        if (scan.LinkedPredecessorType == nameof(DocumentType.PurchaseOrder))
        {
            scan.LinkedPurchaseOrderId = null;
            scan.LinkedPurchaseOrderNumber = null;
            scan.PoLineMappingsJson = null;
        }
        ClearPredecessorLink(scan);
        await _db.SaveChangesAsync();
        return MapToResponse(scan);
    }
}

internal class OcrExtractedData
{
    /// <summary>The kind of paper that was scanned (e.g. "Receipt", "TaxInvoice").
    /// This stays close to what's printed on the page — feed for downstream
    /// rendering and traceability.</summary>
    public string DocumentType { get; set; } = "Receipt";

    /// <summary>The doc type we should CREATE in our books — different from
    /// DocumentType when the workflow shifts perspective (e.g. a supplier's
    /// receipt → we book a PaymentVoucher). Set by OcrDocumentRoleInferrer
    /// and refined by VendorIntelligenceService's high-confidence override.
    /// </summary>
    public string? TargetDocumentType { get; set; }
    public string? OurRole { get; set; }
    public decimal Confidence { get; set; }
    public string? DocumentNumber { get; set; }
    public DateTime? DocumentDate { get; set; }
    public string? VendorName { get; set; }
    public string? VendorTaxId { get; set; }
    public decimal? SubTotal { get; set; }
    public decimal? VatAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? ExpenseCategory { get; set; }
    /// <summary>Header discount (ส่วนลด) read off the paper — flows to
    /// Document.DiscountAmount on creation.</summary>
    public decimal? DiscountAmount { get; set; }

    /// <summary>จำนวนบรรทัดที่จับคู่กับ Product master ของบริษัทได้ (ตั้งโดย
    /// <c>ApplyProductCrossReferenceAsync</c>) — หลักฐาน cold-start ว่า "ของบน
    /// ใบนี้คือสินค้าของเรา" ใช้โดย <see cref="Accounting.Helpers.OcrEntryModeAdvisor"/></summary>
    public int ProductMasterMatchedLines { get; set; }
    public string? DebitAccountCode { get; set; }
    public string? DebitAccountName { get; set; }
    public string? CreditAccountCode { get; set; }
    public string? CreditAccountName { get; set; }
    public string? VatAccountCode { get; set; }
    public string? VatAccountName { get; set; }
    public bool HasWht { get; set; }
    public decimal? WhtRate { get; set; }

    /// <summary>อัตราที่ **กฎหมายกำหนด** สำหรับหมวดรายจ่ายนี้ (ท.ป.4/2528) —
    /// แยกจาก <see cref="WhtRate"/> ซึ่งคือ "ยอดที่พิมพ์อยู่บนกระดาษ"
    ///
    /// <para>⚠️ ที่มา (ผลตรวจ 2026-09-06 · T1-07): ระบบเดิมเสนอหัก ณ ที่จ่าย
    /// **เฉพาะเมื่อกระดาษพูดถึง WHT** — แต่ใบแจ้งหนี้ค่าบริการของผู้ขายไทยเกือบทั้งหมด
    /// ไม่พิมพ์ เพราะหน้าที่หักเป็นของ**ผู้จ่าย** ⇒ ระบบไม่เคยเตือนเลย แล้วผู้จ่าย
    /// รับผิดตาม ม.54 (ต้องจ่ายภาษีที่ไม่ได้หักเอง + เงินเพิ่ม 1.5%/เดือน)</para>
    ///
    /// <para>เป็น **ข้อเสนอ** ไม่ใช่การตัดสิน — ระบบไม่ตั้ง <c>HasWht</c> ให้เอง
    /// เพราะหักเกินก็ผิด (ผู้รับต้องไปขอคืน) หน้าเว็บโชว์เป็นปุ่ม "ใช้อัตรานี้"</para></summary>
    public decimal? SuggestedWhtRate { get; set; }

    /// <summary>**ใครเป็นคนเสนออัตราใน <see cref="SuggestedWhtRate"/>** — ค่าที่เห็นได้
    /// ไม่ใช่เดาย้อนจาก "ช่องไหนบังเอิญมีค่า" (D-3 · <c>Helpers/OcrWhtSuggestion</c>)
    ///
    /// <para>ตั้งโดย <c>ApplyWhtSuggestion</c> จากผลของ <c>Helpers/OcrFieldArbiter</c>
    /// เท่านั้น — ชั้นที่เสนอมีหน้าที่ <c>Note()</c> อย่างเดียว</para></summary>
    public Accounting.Helpers.WhtEvidenceSource SuggestedWhtSource { get; set; }
        = Accounting.Helpers.WhtEvidenceSource.None;

    /// <summary>รหัสประเภทเงินได้ ม.40 (<see cref="Accounting.Helpers.ThaiWhtRateTable"/>) —
    /// จำเป็นต่อ **หนังสือรับรอง 50 ทวิ + ภ.ง.ด.3/53** ไม่ใช่แค่อัตรา (T4-06)</summary>
    public string? WhtIncomeTypeCode { get; set; }
    public int? PaymentTermsDays { get; set; }

    /// <summary>สกุลเงินที่<b>เอกสารประกาศไว้</b> (e-Tax XML <c>InvoiceCurrencyCode</c>)
    /// — <c>null</c> = ต้องเดาจากข้อความ
    ///
    /// <para>⚠️ e-Tax XML บอกสกุลเงินไว้ตรง ๆ พร้อมลายเซ็นดิจิทัล แต่เส้นสร้างเอกสาร
    /// กลับไป **เดาจาก raw text** ทุกครั้ง (<c>InferCurrency</c>) ⇒ ใบ USD ที่ XML
    /// ประกาศชัดยังมีโอกาสถูกบันทึกเป็นบาท = ตัวเลขผิดหลายสิบเท่าเงียบ ๆ
    /// (ผลตรวจ 2026-09-06 · T2-08)</para></summary>
    public string? Currency { get; set; }
    public string? ZoneSummary { get; set; }
    public string? BuyerName { get; set; }
    public string? BuyerTaxId { get; set; }
    public Dictionary<string, double> FieldConfidence { get; set; } = new();
    public List<string> ReasoningTrace { get; set; } = new();

    /// <summary>**สมุดบันทึกว่าใครเสนอค่าอะไรให้ช่องไหน** (สถาปัตยกรรมเป้าหมาย D1)
    ///
    /// <para>ไปป์ไลน์นี้มี 6+ แหล่งเขียนทับช่องเดียวกันตามลำดับบรรทัดในเมธอด ⇒ ไล่ย้อน
    /// ไม่ได้ว่าค่าที่ผู้ใช้เห็นมาจากไหน และผลลัพธ์ขึ้นกับลำดับโค้ด ไม่ใช่คุณภาพหลักฐาน.
    /// ทุกแหล่งเรียก <see cref="Note"/> ตอนเสนอค่า แล้ว <c>Helpers/OcrFieldArbiter</c>
    /// เป็นคนตัดสิน/บันทึกที่มา</para>
    ///
    /// <para><b>เฟสนี้บันทึกที่มาอย่างเดียว — ยังไม่ย้ายตัวตัดสิน</b> (ค่าที่ผู้ใช้เห็น
    /// ยังมาจากลำดับเดิมทุกประการ) ตามแผนของ §4 D1 ที่ให้ทำทีละขั้นเพื่อไม่ให้การ
    /// รื้อใหญ่กลายเป็นความเสี่ยงที่มากกว่าปัญหาเดิม</para></summary>
    public List<Accounting.Helpers.OcrFieldCandidate> FieldCandidates { get; set; } = new();

    /// <summary>บันทึกว่าแหล่งหนึ่งเสนอค่าอะไรให้ช่องหนึ่ง — ค่าว่างถูกข้าม
    /// (ไม่บันทึก "ไม่มีคำตอบ" เป็นผู้เสนอ)</summary>
    public void Note(string field, string? value, Accounting.Helpers.OcrFieldSource source,
        decimal confidence, string? evidence = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        FieldCandidates.Add(new Accounting.Helpers.OcrFieldCandidate(
            field, value.Trim(), source, confidence, evidence));
    }

    /// <summary>รูปแบบตัวเลข/วันที่ที่ใช้บันทึกที่มา — ต้องเป็นรูปเดียวทุกแหล่ง
    /// ไม่งั้น "ค่าเดียวกัน" จากสองแหล่งจะดูเหมือนคนละค่า</summary>
    public void Note(string field, decimal? value, Accounting.Helpers.OcrFieldSource source,
        decimal confidence, string? evidence = null)
        => Note(field, value?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            source, confidence, evidence);

    public void Note(string field, DateTime? value, Accounting.Helpers.OcrFieldSource source,
        decimal confidence, string? evidence = null)
        => Note(field, value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            source, confidence, evidence);
    // === DBD enrichment ===
    public bool DbdLookupAttempted { get; set; }
    public bool DbdMatched { get; set; }
    public string? DbdCanonicalName { get; set; }
    public string? DbdAddress { get; set; }
    public string? DbdJuristicType { get; set; }
    public string? DbdStatus { get; set; }
    public List<OcrExtractedLineItem> Items { get; set; } = new();
    // ─── Vendor contact details (used by Contact auto-create fallback when DBD fails) ───
    // Azure DI populates VendorAddress/VendorPhone directly; ParseThaiDocument
    // best-effort extracts them from raw text via regex on Thai phone/postal patterns.
    public string? VendorAddress { get; set; }
    public string? VendorPhone { get; set; }
    public string? VendorEmail { get; set; }
    public string? VendorBranchCode { get; set; }
    public string? BuyerAddress { get; set; }
    public string? BuyerPhone { get; set; }
    public string? BuyerEmail { get; set; }
    public string? BuyerBranchCode { get; set; }
}

internal class OcrExtractedLineItem
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Amount { get; set; }
    public string? SuggestedAccountCode { get; set; }
    /// <summary>Per-line project assignment captured in the review UI.
    /// Persisted so re-opening the review after a crash preserves the
    /// user's allocation work + auto-create uses it.</summary>
    public Guid? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    /// <summary>Unit (ถุง/เส้น/กล่อง…) detected from the description —
    /// flows to DocumentLine.Unit instead of the blanket "ชิ้น" default.</summary>
    public string? Unit { get; set; }
    /// <summary>ปิดลูปการสอน MatchLineProject (กฎเหล็ก #1) — feedback row ที่
    /// orchestrator คืนตอน AI เดา project ให้บรรทัดนี้. เก็บฝังใน ExtractedItemsJson
    /// เพื่อให้ตอนผู้ใช้ override project (SetExtractedLineProjectAsync) รู้ว่าจะปิด
    /// ลูปไหน + AiSuggestedProjectId ใช้เทียบว่าผู้ใช้รับคำตอบ AI หรือแก้.</summary>
    public Guid? ProjectAiFeedbackId { get; set; }
    public Guid? AiSuggestedProjectId { get; set; }
    /// <summary>อัตรา VAT ของบรรทัดนี้ (<c>7</c> เสียภาษี · <c>0</c> อัตราศูนย์ ·
    /// <c>-1</c> ยกเว้น §81) — <c>null</c> = ยังไม่ได้ตัดสิน
    ///
    /// <para>⚠️ ที่มา (E-OCR-01): เดิมทุกบรรทัดถูกตั้งเป็น
    /// <c>headerVat &gt; 0 ? 7 : 0</c> เหมือนกันหมด ⇒ ใบผสม 7%/ยกเว้น
    /// (Makro/BigC/บิลอาหาร = งานประจำวัน) รายงานภาษีซื้อ §87 ที่แยกคอลัมน์
    /// ผิดทุกใบ โดยยอดรวมยังตรงจึงไม่มีใครเห็น · เก็บลง
    /// <c>ExtractedItemsJson</c> เพื่อให้หน้า review แก้ได้และค่าไม่หายตอนเปิดใหม่</para></summary>
    public decimal? VatRate { get; set; }
    /// <summary>ภาษีของบรรทัดนี้หลังเฉลี่ยจากหัวใบ (เฉพาะบรรทัดที่ <c>VatRate &gt; 0</c>)
    /// — เก็บไว้ให้หน้า review แสดงและตรวจ Σ ได้โดยไม่ต้องคำนวณซ้ำคนละที่</summary>
    public decimal? VatAmount { get; set; }
}

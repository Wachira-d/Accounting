using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Entities;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/ocr")]
[Authorize]
public class OcrController : ControllerBase
{
    private readonly IOcrService _service;
    private readonly IOcrQuotaService _quota;
    private readonly AccountingDbContext _db;
    public OcrController(IOcrService service, IOcrQuotaService quota, AccountingDbContext db)
    { _service = service; _quota = quota; _db = db; }

    [HttpPost("upload")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> UploadAndScan(Guid companyId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "กรุณาเลือกไฟล์"));

        // === Read bytes once, then run quality preflight BEFORE consuming quota ===
        // Rejecting low-resolution / corrupt files here means the user doesn't get
        // charged a quota page for an obviously-unscannable file.
        byte[] fileBytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            fileBytes = ms.ToArray();
        }

        var preflight = Accounting.Services.Implementations.Ocr.OcrPreprocessor.Check(
            fileBytes,
            file.ContentType ?? "",
            file.FileName);
        if (!preflight.Ok)
            return BadRequest(new ApiResponse<object>(false, null, preflight.ErrorMessage ?? "ไฟล์ไม่ผ่านการตรวจสอบ"));

        var fileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();

        var recentCutoff = DateTime.UtcNow.AddSeconds(-60);
        var recentDuplicate = await _db.Set<OcrScanResult>()
            .Where(r => r.CompanyId == companyId && r.FileHash == fileHash && r.CreatedAt >= recentCutoff)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync();
        if (recentDuplicate != null)
        {
            // Same file uploaded within the last minute — return the existing scan
            // instead of creating a parallel duplicate.
            var existing = await _service.GetResultAsync(companyId, recentDuplicate.Id);
            return Ok(new ApiResponse<OcrResultResponse>(true, existing,
                "ไฟล์นี้เพิ่งถูกอัปโหลดไปแล้ว — แสดงผลเดิม"));
        }

        // Atomic check-and-decrement — prevents two parallel uploads from both
        // passing the availability check and over-consuming quota.
        if (!await _quota.TryConsumeAsync(companyId))
        {
            var status = await _quota.GetQuotaStatusAsync(companyId);
            return StatusCode(429, new ApiResponse<object>(false, null,
                $"โควต้า OCR หมด — ใช้ไป {status.UsedThisMonth}/{status.MaxPagesPerMonth} หน้าในเดือนนี้ กรุณาซื้อเครดิตเพิ่ม"));
        }

        var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "ocr");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
        var filePath = Path.Combine(uploadsDir, fileName);

        await System.IO.File.WriteAllBytesAsync(filePath, fileBytes);

        var attachment = new FileAttachment
        {
            CompanyId = companyId,
            FileName = fileName,
            OriginalFileName = file.FileName,
            ContentType = file.ContentType ?? "application/octet-stream",
            FileSize = file.Length,
            StoragePath = filePath,
            EntityType = "OcrScan",
            EntityId = Guid.NewGuid(),
            UploadedByUserId = JwtHelper.GetUserIdFromClaims(User)
        };
        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync();

        OcrResultResponse result;
        try
        {
            result = await _service.ScanAsync(companyId, attachment.Id);
        }
        catch
        {
            // Refund quota when scan crashes — caller didn't get a result
            await _quota.RefundAsync(companyId);
            throw;
        }

        // Refund if scan returned a duplicate (no new OCR work was actually done)
        // OR if scan failed silently (status != Completed)
        if (result.IsDuplicate || result.ScanStatus != "Completed")
            await _quota.RefundAsync(companyId);

        return Ok(new ApiResponse<OcrResultResponse>(true, result));
    }

    [HttpPost("scan/{fileAttachmentId:guid}")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> Scan(Guid companyId, Guid fileAttachmentId)
        => Ok(new ApiResponse<OcrResultResponse>(true, await _service.ScanAsync(companyId, fileAttachmentId)));

    /// <summary>
    /// Re-process an existing scan without consuming additional quota.
    /// Bounded by SiteSettings.OcrMaxRetriesPerScan to prevent abuse.
    /// </summary>
    [HttpPost("{scanId:guid}/retry")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> Retry(Guid companyId, Guid scanId)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.Id == scanId && r.CompanyId == companyId);
        if (scan == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการสแกน"));

        var settings = await _db.SiteSettings.FirstOrDefaultAsync();
        var maxRetries = settings?.OcrMaxRetriesPerScan ?? 1;
        if (scan.RetryCount >= maxRetries)
            return StatusCode(429, new ApiResponse<object>(false, null,
                $"ใช้ retry ครบ {maxRetries} ครั้งแล้ว — กรุณาอัปโหลดใหม่ (ใช้โควต้า)"));

        scan.RetryCount++;
        scan.ScanStatus = "Processing";
        await _db.SaveChangesAsync();

        if (!scan.FileAttachmentId.HasValue)
            return BadRequest(new ApiResponse<object>(false, null, "ไม่พบไฟล์ต้นฉบับ"));

        var result = await _service.ScanAsync(companyId, scan.FileAttachmentId.Value);
        return Ok(new ApiResponse<OcrResultResponse>(true, result, "Retry สำเร็จ (ไม่ใช้โควต้า)"));
    }

    [HttpGet("{scanId:guid}")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> GetResult(Guid companyId, Guid scanId)
        => Ok(new ApiResponse<OcrResultResponse>(true, await _service.GetResultAsync(companyId, scanId)));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<OcrResultResponse>>>> GetResults(Guid companyId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<OcrResultResponse>>(true, await _service.GetResultsAsync(companyId, status, new PagedRequest(page, pageSize))));

    [HttpPost("{scanId:guid}/create-document")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> CreateDocument(Guid companyId, Guid scanId)
        => Ok(new ApiResponse<OcrResultResponse>(true, await _service.CreateDocumentFromScanAsync(companyId, scanId, User.Identity?.Name ?? "")));

    [HttpPost("{scanId:guid}/match-contact/{contactId:guid}")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> MatchContact(Guid companyId, Guid scanId, Guid contactId)
        => Ok(new ApiResponse<OcrResultResponse>(true, await _service.MatchContactAsync(companyId, scanId, contactId)));

    [HttpPost("{scanId:guid}/correct")]
    public async Task<ActionResult<ApiResponse<object>>> SubmitCorrection(Guid companyId, Guid scanId, [FromBody] OcrCorrectionRequest correction)
    {
        await _service.SubmitCorrectionAsync(companyId, scanId, correction);
        return Ok(new ApiResponse<object>(true, null, "Correction saved and sent to learning service"));
    }

    /// <summary>
    /// Register a line item from this OCR scan as a Fixed Asset. The body
    /// carries the line index + any user-edited fields. Server:
    ///   1. Pulls the OCR'd line item (Description, UnitPrice, Quantity).
    ///   2. Calls the EXISTING FixedAssetService.CreateAsync — reuses all
    ///      lifecycle / depreciation-calculation logic (StraightLine /
    ///      DecliningBalance / DoubleDecliningBalance) verbatim.
    ///   3. Initial Journal Entry (Dr: AssetAccount / Cr: AccruedAP) is
    ///      generated atomically inside a DB transaction.
    /// Returns the newly-created FixedAsset id so the UI can deep-link to
    /// the asset register.
    /// </summary>
    public record RegisterAssetFromScanRequest(
        int LineIndex,
        string AssetCode,
        string Name,
        string? Description,
        string? Category,
        string? SerialNumber,
        DateTime? PurchaseDate,
        decimal? PurchaseCost,
        decimal SalvageValue,
        int UsefulLifeMonths,
        Models.Enums.DepreciationMethod DepreciationMethod = Models.Enums.DepreciationMethod.StraightLine,
        Guid? AssetAccountId = null,
        Guid? DepreciationExpenseAccountId = null,
        Guid? AccumulatedDepreciationAccountId = null);

    [HttpPost("{scanId:guid}/register-asset")]
    public async Task<ActionResult<ApiResponse<object>>> RegisterAsset(
        Guid companyId, Guid scanId, [FromBody] RegisterAssetFromScanRequest req,
        [FromServices] Services.Interfaces.IFixedAssetService assetService)
    {
        var result = await _service.RegisterAssetFromScanAsync(companyId, scanId, req,
            assetService, User.Identity?.Name ?? "ocr-asset-register");
        return Ok(new ApiResponse<object>(true, result, "ลงทะเบียนสินทรัพย์ถาวรเรียบร้อย"));
    }

    /// <summary>
    /// Ranked review queue — surfaces the scans whose user-correction
    /// would yield the highest learning signal. Score combines per-field
    /// uncertainty, vendor novelty, recency, and the asset-alert flag.
    /// Use from a "Needs Review" UI tab so the user spends correction
    /// effort where it does most good.
    /// </summary>
    [HttpGet("review-queue")]
    public async Task<ActionResult<ApiResponse<object>>> ReviewQueue(
        Guid companyId,
        [FromServices] Services.Implementations.Ocr.ActiveLearningRanker ranker,
        [FromQuery] int limit = 20)
    {
        var ranked = await ranker.RankAsync(companyId, Math.Clamp(limit, 1, 100));
        // Annotate with letter-grade quality for at-a-glance UI rendering
        var scans = await _db.Set<Accounting.Models.Entities.OcrScanResult>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && ranked.Select(x => x.ScanId).Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r);
        var result = ranked.Select(r =>
        {
            scans.TryGetValue(r.ScanId, out var scan);
            var grade = scan != null ? Services.Implementations.Ocr.ScanQualityGrader.Compute(scan) : null;
            return new
            {
                r.ScanId,
                r.VendorName,
                r.OriginalFileName,
                r.ProcessedAt,
                r.Confidence,
                r.UncertaintyScore,
                r.NoveltyScore,
                r.RecencyFactor,
                r.HasPotentialFixedAsset,
                r.PriorityScore,
                r.ReasonHint,
                Quality = grade != null ? new { grade.Letter, grade.Score, grade.Color } : null,
            };
        }).ToList();
        return Ok(new ApiResponse<object>(true, result));
    }

    /// <summary>
    /// Delete an OCR scan result. When the scan auto-created a draft
    /// document, pass cascade=true to delete the document too — the
    /// service will refuse if that document has already been approved
    /// or paid (those need to be voided via the normal Documents flow).
    /// </summary>
    [HttpDelete("{scanId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid companyId, Guid scanId, [FromQuery] bool cascade = false)
    {
        await _service.DeleteScanAsync(companyId, scanId, cascade);
        return Ok(new ApiResponse<object>(true, null,
            cascade ? "ลบ scan และเอกสารที่สร้างอัตโนมัติเรียบร้อย" : "ลบสำเร็จ"));
    }

    [HttpGet("quota")]
    public async Task<ActionResult<ApiResponse<OcrQuotaStatus>>> GetQuota(Guid companyId)
        => Ok(new ApiResponse<OcrQuotaStatus>(true, await _quota.GetQuotaStatusAsync(companyId)));

    [HttpPost("credits/purchase")]
    public async Task<ActionResult<ApiResponse<OcrCreditPurchaseResponse>>> PurchaseCredits(
        Guid companyId, [FromBody] OcrCreditPurchaseRequest request)
    {
        var result = await _quota.PurchaseCreditsAsync(companyId, request.Pages, User.Identity?.Name ?? "");
        return Ok(new ApiResponse<OcrCreditPurchaseResponse>(true, result, "สร้างรายการซื้อเครดิตสำเร็จ — รอ Admin อนุมัติ"));
    }

    [HttpGet("credits/history")]
    public async Task<ActionResult<ApiResponse<List<OcrCreditPurchaseResponse>>>> GetCreditHistory(Guid companyId)
        => Ok(new ApiResponse<List<OcrCreditPurchaseResponse>>(true, await _quota.GetPurchaseHistoryAsync(companyId)));

    [HttpGet("{scanId:guid}/image")]
    public async Task<IActionResult> GetImage(Guid companyId, Guid scanId)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanId);
        if (scan?.FileAttachmentId == null)
            return NotFound();

        var file = await _db.FileAttachments
            .FirstOrDefaultAsync(f => f.Id == scan.FileAttachmentId && f.CompanyId == companyId);
        if (file == null || !System.IO.File.Exists(file.StoragePath))
            return NotFound();

        return PhysicalFile(file.StoragePath, file.ContentType ?? "application/octet-stream", file.OriginalFileName);
    }

    /// <summary>
    /// Read current OCR provider configuration + status. Admin uses this on the
    /// admin-ocr page to show "Azure DI: Enabled / Disabled", "Embedded Tesseract:
    /// Ready (eng+tha)" etc.
    /// </summary>
    [HttpGet("admin/config")]
    [Authorize(Roles = "SystemAdmin,Admin")]
    public async Task<ActionResult<ApiResponse<object>>> GetOcrConfig(
        Guid companyId,
        [FromServices] Accounting.Services.Implementations.Ocr.EmbeddedTesseractOcrService embeddedOcr)
    {
        var settings = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            azure = new
            {
                enabled = settings?.AzureDiEnabled ?? false,
                endpoint = settings?.AzureDiEndpoint,
                hasApiKey = !string.IsNullOrEmpty(settings?.AzureDiApiKey),
                modelId = settings?.AzureDiModelId,
                apiVersion = settings?.AzureDiApiVersion,
                lastTestStatus = settings?.AzureDiLastTestStatus,
            },
            embedded = new
            {
                available = embeddedOcr.IsAvailable,
                languages = embeddedOcr.Languages,
                tessdataPath = embeddedOcr.TessdataPath,
            },
            pythonService = new
            {
                // Python service URL comes from appsettings.json (not the per-tenant SiteSettings)
                // because it's a deployment-level configuration, not a user-tunable setting.
                url = HttpContext.RequestServices.GetService<IConfiguration>()?["Ocr:LocalServiceUrl"] ?? "(not configured)",
            },
            provider = settings?.OcrProvider ?? "auto",
        }, "OCR configuration"));
    }

    /// <summary>
    /// Update OCR provider settings. Only the fields supplied in the body are
    /// changed — null/missing fields are left as-is.
    /// </summary>
    [HttpPut("admin/config")]
    [Authorize(Roles = "SystemAdmin,Admin")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateOcrConfig(
        Guid companyId,
        [FromBody] UpdateOcrConfigRequest request)
    {
        var settings = await _db.SiteSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new SiteSettings();
            _db.SiteSettings.Add(settings);
        }

        if (request.AzureDiEnabled.HasValue) settings.AzureDiEnabled = request.AzureDiEnabled.Value;
        if (request.AzureDiEndpoint != null) settings.AzureDiEndpoint = request.AzureDiEndpoint;
        // Defense-in-depth: only overwrite when a non-empty value is supplied.
        // Some serializers map an empty input field to "" rather than null,
        // which would silently wipe the stored secret on every save.
        if (!string.IsNullOrEmpty(request.AzureDiApiKey)) settings.AzureDiApiKey = request.AzureDiApiKey;
        if (request.AzureDiModelId != null) settings.AzureDiModelId = request.AzureDiModelId;
        if (request.AzureDiApiVersion != null) settings.AzureDiApiVersion = request.AzureDiApiVersion;
        if (request.OcrProvider != null) settings.OcrProvider = request.OcrProvider;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, "บันทึกการตั้งค่า OCR สำเร็จ"));
    }

    public record UpdateOcrConfigRequest(
        bool? AzureDiEnabled,
        string? AzureDiEndpoint,
        string? AzureDiApiKey,
        string? AzureDiModelId,
        string? AzureDiApiVersion,
        string? OcrProvider);

    /// <summary>
    /// Submit a training correction from the admin-ocr test page. Unlike the
    /// regular SubmitCorrectionAsync (which trains from a persisted scan row),
    /// this endpoint trains from raw fields the admin typed in — used when
    /// they're testing a sample file that wasn't saved as a real scan.
    /// </summary>
    [HttpPost("admin/train-from-sample")]
    [Authorize(Roles = "SystemAdmin,Admin")]
    public async Task<ActionResult<ApiResponse<object>>> TrainFromSample(
        Guid companyId,
        [FromBody] AdminTrainRequest request,
        [FromServices] Accounting.Services.Implementations.Ocr.ExpenseCategoryLearner learner,
        [FromServices] Accounting.Services.Implementations.Ocr.VendorIntelligenceService vendorIntel)
    {
        if (string.IsNullOrWhiteSpace(request.VendorName) && string.IsNullOrWhiteSpace(request.VendorTaxId))
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุชื่อหรือเลขประจำตัวผู้ขาย"));
        if (string.IsNullOrWhiteSpace(request.AccountCode))
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุรหัสบัญชีที่ต้องการสอน"));

        var accountName = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && a.AccountCode == request.AccountCode && !a.IsDeleted)
            .Select(a => a.AccountName)
            .FirstOrDefaultAsync();

        // 1. Train ExpenseCategoryLearner (per vendor + description → account)
        await learner.RecordAsync(
            companyId,
            request.VendorTaxId,
            request.VendorName,
            request.Description ?? "",
            request.AccountCode,
            accountName);

        // 2. Train VendorIntelligence (per-vendor habits — DocumentType, WHT, etc.)
        // Parse DocumentType if supplied; fall back to no-op if invalid.
        Models.Enums.DocumentType? docType = null;
        if (!string.IsNullOrEmpty(request.DocumentType)
            && Enum.TryParse<Models.Enums.DocumentType>(request.DocumentType, ignoreCase: true, out var dt))
            docType = dt;

        await vendorIntel.TrainFromAdminAsync(
            companyId,
            request.VendorTaxId,
            request.VendorName,
            docType,
            request.AccountCode,
            accountName,
            request.WhtRate,
            request.PaymentTermsDays,
            weight: Math.Max(1, request.Weight ?? 1));

        return Ok(new ApiResponse<object>(true, new
        {
            accountName,
            trainedDocumentType = docType?.ToString(),
            whtRate = request.WhtRate,
            paymentTermsDays = request.PaymentTermsDays
        }, $"สอนระบบเรียบร้อย: ผู้ขาย '{request.VendorName ?? request.VendorTaxId}' → {request.AccountCode}{(docType.HasValue ? $" + {docType.Value}" : "")}{(request.WhtRate.HasValue ? $" + WHT {request.WhtRate}%" : "")}"));
    }

    public record AdminTrainRequest(
        string? VendorName,
        string? VendorTaxId,
        string? Description,
        string AccountCode,
        // Extended fields — let admin teach VendorIntelligence habits, not just
        // the per-line account mapping. All optional.
        string? DocumentType,        // "PurchaseInvoice" | "Expense" | "CertificateInLieu" | ...
        decimal? WhtRate,            // 1, 2, 3, 5, 10, 15
        int? PaymentTermsDays,       // typical credit period
        int? Weight);                // confidence — admin can say "I've seen this 5 times"

    /// <summary>
    /// Admin test-scan endpoint — runs the full OCR pipeline on an uploaded file
    /// WITHOUT consuming quota or persisting a scan row. Used by the admin page
    /// to inspect what each provider reads from a sample document. Returns the
    /// raw OCR text + parsed fields + reasoning trace + which provider was used.
    /// </summary>
    [HttpPost("admin/test-scan")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    [Authorize(Roles = "SystemAdmin,Admin")]
    public async Task<ActionResult<ApiResponse<object>>> AdminTestScan(
        Guid companyId,
        IFormFile file,
        [FromQuery] string? forceProvider,  // "azure" | "python" | "embedded" — overrides chain
        [FromServices] Accounting.Services.Implementations.Ocr.EmbeddedTesseractOcrService embeddedOcr,
        [FromServices] Accounting.Services.Implementations.Ocr.AzureDocumentIntelligenceService azureDi)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "กรุณาเลือกไฟล์"));

        byte[] fileBytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            fileBytes = ms.ToArray();
        }

        // Run the requested provider. Admin test bypasses quota and doesn't
        // persist anything — output is purely for inspection.
        string rawText = "";
        decimal confidence = 0;
        string providerUsed = "";
        string? error = null;

        try
        {
            switch ((forceProvider ?? "embedded").ToLowerInvariant())
            {
                case "embedded":
                    providerUsed = "Embedded Tesseract";
                    var emb = await embeddedOcr.ExtractTextAsync(fileBytes, file.ContentType ?? "", file.FileName);
                    rawText = emb.Text;
                    confidence = emb.Confidence;
                    error = emb.Success ? null : emb.Error;
                    break;

                case "azure":
                    providerUsed = "Azure DI";
                    var siteSettings = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync();
                    if (siteSettings?.AzureDiEnabled != true || string.IsNullOrEmpty(siteSettings.AzureDiEndpoint))
                    {
                        error = "Azure DI ไม่ได้ตั้งค่า — กรุณาเปิดใช้ใน admin settings ก่อน";
                        break;
                    }
                    try
                    {
                        var azResult = await azureDi.AnalyzeAsync(fileBytes, file.ContentType ?? "application/octet-stream", siteSettings);
                        rawText = azResult?.RawText ?? "";
                        confidence = azResult?.OverallConfidence ?? 0m;
                    }
                    catch (Exception ex)
                    {
                        error = $"Azure error: {ex.Message}";
                    }
                    break;

                case "python":
                {
                    providerUsed = "Python local service";
                    var pyUrl = HttpContext.RequestServices.GetService<IConfiguration>()?["Ocr:LocalServiceUrl"];
                    if (string.IsNullOrEmpty(pyUrl))
                    {
                        error = "Ocr:LocalServiceUrl ไม่ได้ตั้งค่าใน appsettings.json — Python service ใช้งานไม่ได้";
                        break;
                    }
                    try
                    {
                        var client = HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
                        client.Timeout = TimeSpan.FromSeconds(60);
                        using var content = new MultipartFormDataContent();
                        var byteContent = new ByteArrayContent(fileBytes);
                        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");
                        content.Add(byteContent, "file", file.FileName ?? "upload");
                        var resp = await client.PostAsync($"{pyUrl.TrimEnd('/')}/ocr", content);
                        var body = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode)
                        {
                            error = $"Python service HTTP {(int)resp.StatusCode}: {body.Substring(0, Math.Min(body.Length, 200))}";
                            break;
                        }
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("text", out var t)) rawText = t.GetString() ?? "";
                        else if (root.TryGetProperty("raw_text", out var rt)) rawText = rt.GetString() ?? "";
                        if (root.TryGetProperty("confidence", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number)
                            confidence = (decimal)c.GetDouble();
                    }
                    catch (HttpRequestException ex)
                    {
                        error = $"เชื่อมต่อ Python service ไม่ได้ ({pyUrl}): {ex.Message}";
                    }
                    catch (TaskCanceledException)
                    {
                        error = $"Python service timeout (URL: {pyUrl})";
                    }
                    catch (Exception ex)
                    {
                        error = $"Python service error: {ex.Message}";
                    }
                    break;
                }

                default:
                    error = $"Unknown provider: {forceProvider}";
                    break;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // Always run the rule-based parser on whatever text we got — gives the
        // admin a preview of which fields the pipeline would extract.
        var parsed = string.IsNullOrEmpty(rawText) ? null : ParseAdminPreview(rawText);

        return Ok(new ApiResponse<object>(error == null, new
        {
            provider = providerUsed,
            embeddedAvailable = embeddedOcr.IsAvailable,
            embeddedLanguages = embeddedOcr.Languages,
            embeddedTessdataPath = embeddedOcr.TessdataPath,
            rawText,
            confidence,
            parsedFields = parsed,
            error
        }, error ?? "OCR สำเร็จ"));
    }

    /// <summary>
    /// Run the same rule-based Thai-document parser used in production on raw
    /// OCR text — purely for admin inspection. Mirrors a subset of
    /// OcrService.ParseThaiDocument but doesn't depend on its internals.
    /// </summary>
    private static object ParseAdminPreview(string text)
    {
        var upperText = text.ToUpperInvariant();
        string? docType = null;
        if (text.Contains("ใบกำกับภาษี") || upperText.Contains("TAX INVOICE")) docType = "TaxInvoice";
        else if (text.Contains("ใบรับรองแทนใบเสร็จ") || upperText.Contains("CERTIFICATE IN LIEU")) docType = "CertificateInLieu";
        else if (text.Contains("ใบลดหนี้") || upperText.Contains("CREDIT NOTE")) docType = "CreditNote";
        else if (text.Contains("ใบเพิ่มหนี้") || upperText.Contains("DEBIT NOTE")) docType = "DebitNote";
        else if (text.Contains("ใบสั่งซื้อ") || upperText.Contains("PURCHASE ORDER")) docType = "PurchaseOrder";
        else if (text.Contains("ใบแจ้งหนี้") || (upperText.Contains("INVOICE") && !upperText.Contains("TAX INVOICE"))) docType = "Invoice";
        else if (text.Contains("ใบเสร็จรับเงิน") || upperText.Contains("RECEIPT")) docType = "Receipt";

        // Extract all 13-digit Thai tax IDs found in the text
        var taxIdPattern = @"(\d{1}\s*-?\s*\d{4}\s*-?\s*\d{5}\s*-?\s*\d{2}\s*-?\s*\d{1})";
        var taxIds = System.Text.RegularExpressions.Regex.Matches(text, taxIdPattern)
            .Select(m => new string(m.Value.Where(char.IsDigit).ToArray()))
            .Where(s => s.Length == 13)
            .Distinct()
            .Take(5)
            .ToArray();
        if (taxIds.Length == 0)
        {
            taxIds = System.Text.RegularExpressions.Regex.Matches(text, @"\d{13}")
                .Select(m => m.Value).Distinct().Take(5).ToArray();
        }

        // Money amounts — patterns like "1,234.56" or "1234"
        var amountPattern = @"(\d{1,3}(?:,\d{3})*\.\d{2}|\d+\.\d{2})";
        var amounts = System.Text.RegularExpressions.Regex.Matches(text, amountPattern)
            .Select(m => m.Value).Take(20).ToArray();

        // Dates — basic Thai/Western formats
        var datePattern = @"\d{1,2}[/\-\.]\d{1,2}[/\-\.]\d{2,4}";
        var dates = System.Text.RegularExpressions.Regex.Matches(text, datePattern)
            .Select(m => m.Value).Take(10).ToArray();

        // Document numbers — common prefixes
        var docNumberPattern = @"(?:เลขที่|No\.?|INV|TAX|REF)\s*[:\#]?\s*([A-Z0-9\-/]{4,20})";
        var docNumbers = System.Text.RegularExpressions.Regex.Matches(text, docNumberPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value).Take(5).ToArray();

        return new
        {
            documentType = docType,
            taxIds,
            amounts,
            dates,
            documentNumbers = docNumbers,
            textLength = text.Length
        };
    }

    /// <summary>
    /// Rebuild the vendor intelligence cache from existing approved Documents.
    /// Run once after deploy / data import so OCR auto-suggestions work for
    /// vendors that already have history. Idempotent — safe to re-run.
    /// </summary>
    [HttpPost("intelligence/backfill")]
    [Authorize(Roles = "SystemAdmin,Admin")]
    public async Task<ActionResult<ApiResponse<object>>> BackfillVendorIntelligence(
        Guid companyId,
        [FromServices] Accounting.Services.Implementations.Ocr.VendorIntelligenceService vendorIntel,
        [FromQuery] int sinceMonths = 24)
    {
        var trained = await vendorIntel.BackfillFromHistoryAsync(companyId, sinceMonths);
        return Ok(new ApiResponse<object>(true, new { vendorsTrained = trained, sinceMonths },
            $"เรียนรู้ข้อมูลผู้ขาย {trained} ราย จากเอกสารย้อนหลัง {sinceMonths} เดือน"));
    }

    /// <summary>
    /// Inspect what the system has learned about a specific vendor — useful for
    /// debugging "why did OCR pre-fill account X for this vendor?"
    /// </summary>
    [HttpGet("intelligence/vendor")]
    [Authorize(Roles = "SystemAdmin,Admin")]
    public async Task<ActionResult<ApiResponse<object>>> GetVendorIntelligence(
        Guid companyId, [FromQuery] string? taxId, [FromQuery] string? name)
    {
        if (string.IsNullOrEmpty(taxId) && string.IsNullOrEmpty(name))
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุ taxId หรือ name อย่างน้อย 1 อย่าง"));

        string key = "";
        if (!string.IsNullOrEmpty(taxId))
        {
            var digits = new string(taxId.Where(char.IsDigit).ToArray());
            if (digits.Length == 13) key = $"tax:{digits}";
        }
        if (string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(name))
            key = $"name:{name.Trim().ToLowerInvariant()}";

        var intel = await _db.OcrVendorIntelligence
            .FirstOrDefaultAsync(v => v.CompanyId == companyId && v.VendorKey == key && !v.IsDeleted);
        if (intel == null)
            return Ok(new ApiResponse<object>(true, null, "ไม่พบประวัติของผู้ขายรายนี้"));

        return Ok(new ApiResponse<object>(true, new
        {
            intel.VendorName, intel.VendorTaxId,
            intel.MostCommonDocumentType, intel.MostCommonDocumentTypeCount,
            intel.TotalDocuments, intel.DocumentTypeBreakdownJson,
            intel.MostCommonDebitAccountCode, intel.MostCommonDebitAccountName,
            intel.MostCommonDebitAccountCount, intel.DebitAccountBreakdownJson,
            intel.TypicallyHasWht, intel.TypicalWhtRate, intel.WhtUsageCount,
            intel.AvgTotalAmount, intel.MinTotalAmount, intel.MaxTotalAmount, intel.MedianTotalAmount,
            intel.TypicalPaymentTermsDays,
            intel.LastTrainedAt, intel.LastDocumentDate
        }, "ข้อมูลที่ระบบเรียนรู้เกี่ยวกับผู้ขายรายนี้"));
    }
}

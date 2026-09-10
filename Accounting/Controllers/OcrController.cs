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
    // สำหรับ "สร้าง + อนุมัติ" — อนุมัติที่ controller ไม่ฉีดเข้า OcrService
    // (กันวงกลม DI + OcrService ไม่ควรรู้จัก approve pipeline ทั้งชุด)
    private readonly IDocumentService _documentService;
    // ด่านสิทธิ์ (ผลตรวจ 2026-09-05 T1-06): เส้น "สร้าง+อนุมัติ" และ "บันทึก JE" จากหน้าสแกน
    // เป็นทางอนุมัติทางที่ 4 ที่ไม่มีด่าน — ใช้ตัวตัดสินกลางเดียวกับเว็บ/ลายเซ็น/LINE/มือถือ
    private readonly IPermissionService _perms;
    public OcrController(IOcrService service, IOcrQuotaService quota, AccountingDbContext db,
        IDocumentService documentService, IPermissionService perms)
    { _service = service; _quota = quota; _db = db; _documentService = documentService; _perms = perms; }

    /// <summary>ชนิดเอกสารที่สแกนนี้จะกลายเป็น — ผ่าน <c>Helpers.OcrTargetDocumentType</c>
    /// ตัวเดียวกับที่ <c>OcrService.CreateDocumentFromScanAsync</c> ใช้จริง
    ///
    /// <para>⚠️ เดิมเมธอดนี้แปลงเองแล้ว <b>คืน null</b> เมื่อแปลงไม่ได้ (สแกนที่ยังไม่มี
    /// <c>TargetDocumentType</c> · ค่า pseudo "Deposit") ⇒ ผู้เรียกข้ามด่านสิทธิ์ไปเลย
    /// ทั้งที่ service ยังสร้างเอกสารจริงด้วย fallback ตามชนิดกระดาษ — และ "Deposit"
    /// กลายเป็น <b>Receipt ฝั่งขาย</b> (ผลตรวจย้อน 2026-09-06 · คอมมิต 4fd8dd6)</para>
    ///
    /// <para>คืน null เฉพาะเมื่อ<b>ไม่พบแถวสแกน</b> — เส้นนั้นจะไป 404/ข้อความของ service เอง</para></summary>
    private async Task<Models.Enums.DocumentType?> ResolveTargetTypeAsync(Guid companyId, Guid scanId, string? targetType)
    {
        var scan = await _db.Set<OcrScanResult>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.Id == scanId)
            .Select(r => new { r.TargetDocumentType, r.DocumentType, r.LinkedPurchaseOrderId })
            .FirstOrDefaultAsync();
        if (scan == null) return null;
        return Accounting.Helpers.OcrTargetDocumentType.Resolve(
            targetType, scan.TargetDocumentType, scan.DocumentType,
            hasLinkedPurchaseOrder: scan.LinkedPurchaseOrderId.HasValue).Type;
    }

    /// <summary>Resolve a REAL user id to stamp on uploads. JWT/user-key auth
    /// gives a genuine user. Integration (int_) key auth sets NameIdentifier to
    /// the IntegrationId (not a User), so we attribute the upload to the
    /// company's Owner instead — otherwise the FileAttachment→User FK throws
    /// and the whole OCR upload 500s. Falls back to any company member.</summary>
    private async Task<Guid> ResolveUploaderUserIdAsync(Guid companyId)
    {
        Guid claimId;
        try { claimId = JwtHelper.GetUserIdFromClaims(User); }
        catch { claimId = Guid.Empty; }
        if (claimId != Guid.Empty
            && await _db.Users.AsNoTracking().AnyAsync(u => u.Id == claimId))
            return claimId;

        var ownerId = await _db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.Role == Models.Enums.UserRole.Owner)
            .Select(cu => cu.UserId)
            .FirstOrDefaultAsync();
        if (ownerId != Guid.Empty) return ownerId;

        return await _db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == companyId)
            .Select(cu => cu.UserId)
            .FirstOrDefaultAsync();
    }

    [HttpPost("upload")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> UploadAndScan(
        Guid companyId, IFormFile file,
        // Optional per-scan engine override: "auto" | "azure" | "local".
        // Null/empty/anything-else = "auto" (full cascade, current behavior).
        // UI feeds this from the selector populated by GET /ocr/engines so
        // exhausted engines can't be chosen client-side; the server still
        // re-validates azure quota and returns 429 if the user beat the
        // quota refresh.
        [FromQuery] string? preferredEngine = null,
        // Optional structured metadata an external system sends ALONGSIDE the
        // file (a multipart form field) — order/project line info used to
        // auto-allocate each OCR'd line to its originating project. Free-text
        // JSON; ignored when absent or unparseable.
        [FromForm] string? metadata = null,
        // Whether to AUTO-CREATE the inferred target document right after the
        // scan. Web/human uploads must leave this null → the gating below
        // resolves to false so the user picks the doc type explicitly in the
        // review UI (matches the business flow). Integration partner syncs
        // (authenticated by an int_ key) default to true to preserve their
        // historical zero-touch behavior; partners or web callers can override
        // with ?autoCreate=true / false at any time.
        [FromQuery] bool? autoCreate = null)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "กรุณาเลือกไฟล์"));

        // Server-side guard: "azure" pick must still have Azure budget at
        // scan time. Prevents the case where the user opened the page when
        // azure had 1 page left, another scan consumed it, then they
        // clicked upload — without this check the scan would silently fall
        // through to local even though they explicitly picked Azure.
        var normalizedEngine = (preferredEngine ?? "").Trim().ToLowerInvariant();
        if (normalizedEngine == "azure")
        {
            var (azureAllowed, azureReason) = await _quota.CheckAzureQuotaAsync(companyId);
            if (!azureAllowed)
                return StatusCode(429, new ApiResponse<object>(false, null,
                    azureReason ?? "Azure DI ใช้ไม่ได้ — กรุณาเลือก Engine อื่น"));
        }

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
            // Resolve a REAL user for the FK. An integration (int_) key sets
            // NameIdentifier to the IntegrationId — NOT a User — so using it
            // raw violated FileAttachment→User FK and 500'd the whole upload.
            UploadedByUserId = await ResolveUploaderUserIdAsync(companyId)
        };
        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync();

        // EFFECTIVE auto-create flag:
        //   explicit ?autoCreate=… wins; otherwise default by auth context —
        //   integration partner key (carries IntegrationId) → true (preserves
        //   "zero-touch sync"); web/JWT/acc_ key → false (user picks).
        var isIntegrationPartner = HttpContext.Items.ContainsKey("IntegrationId")
            || User.FindFirst("IntegrationId") != null;
        var effectiveAutoCreate = autoCreate ?? isIntegrationPartner;

        OcrResultResponse result;
        try
        {
            result = await _service.ScanAsync(companyId, attachment.Id, preferredEngine, metadata, effectiveAutoCreate);
        }
        catch
        {
            // Refund quota when scan crashes — caller didn't get a result
            await _quota.RefundAsync(companyId);
            throw;
        }

        // Refund if scan returned a duplicate (no new OCR work was actually done),
        // failed silently (status != Completed), or extracted everything from an
        // embedded e-Tax XML (no OCR engine ever ran — the page count was
        // pre-charged but never consumed).
        if (result.IsDuplicate || result.ScanStatus != "Completed" || result.OcrEngine == "EtaxXml")
            await _quota.RefundAsync(companyId);

        return Ok(new ApiResponse<OcrResultResponse>(true, result));
    }

    [HttpPost("scan/{fileAttachmentId:guid}")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> Scan(
        Guid companyId, Guid fileAttachmentId, [FromQuery] bool? autoCreate = null,
        [FromBody] OcrScanMetadataRequest? body = null)
    {
        // Same auto-create defaulting as /upload — integration partners using
        // the two-step upload-then-scan flow keep their zero-touch behavior;
        // web/JWT callers default to suggest-only.
        var isIntegrationPartner = HttpContext.Items.ContainsKey("IntegrationId")
            || User.FindFirst("IntegrationId") != null;
        var effective = autoCreate ?? isIntegrationPartner;
        // forward partner metadata (amount override / payment source / project)
        // — เหมือน path /upload เพื่อให้ flow 2 ขั้น (upload→scan) เชื่อค่าที่
        // ระบบภายนอกส่งมาได้เหมือนกัน
        return Ok(new ApiResponse<OcrResultResponse>(true,
            await _service.ScanAsync(companyId, fileAttachmentId,
                preferredEngine: body?.Engine, externalMetadataJson: body?.Metadata,
                autoCreate: effective)));
    }

    /// <summary>Body ของ /scan สำหรับ flow 2 ขั้น — แนบ metadata (ยอดที่ระบบ
    /// ภายนอกกรอก/แหล่งเงิน/project) + เลือก engine ได้. ทุก field optional.</summary>
    public record OcrScanMetadataRequest(string? Metadata = null, string? Engine = null);

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

        // ⚠️ ตรวจไฟล์ **ก่อน** เขียนอะไรลงแถว — เดิมเพิ่ม RetryCount แล้วค่อยเจอว่า
        // ไม่มีไฟล์ ⇒ ผู้ใช้เสียสิทธิ์ retry ไปฟรี ๆ กับคำขอที่ทำไม่ได้ตั้งแต่ต้น
        if (!scan.FileAttachmentId.HasValue)
            return BadRequest(new ApiResponse<object>(false, null, "ไม่พบไฟล์ต้นฉบับ"));

        // ⚠️ เดิมตั้ง `scan.ScanStatus = "Processing"` ตรงนี้ — แต่ ScanAsync สร้าง
        // **แถวใหม่** เสมอ ไม่มีใครกลับมาตั้งสถานะแถวนี้คืน ⇒ แถวเดิมค้าง
        // "Processing" ตลอดกาล · แถวเก่าไม่ได้กำลังถูกประมวลผล จึงไม่ควรแตะสถานะเลย
        scan.RetryCount++;
        await _db.SaveChangesAsync();

        // forceRescan: ถ้าไม่ส่ง ด่าน hash จะคืน **สำเนาของผลเดิม** แล้วเราตอบว่า
        // "Retry สำเร็จ" ทั้งที่ไม่มี engine ตัวไหนทำงานเลย = silent no-op
        var result = await _service.ScanAsync(companyId, scan.FileAttachmentId.Value, forceRescan: true);

        // ผลไปโผล่ที่ **เอกสารคนละแถว** — ต้องบอกเลขปลายทางเสมอ ไม่งั้นผู้ใช้เปิด
        // แถวเดิมแล้วเห็นค่าเท่าเดิม จะรายงานว่า "กดปุ่มแล้วไม่มีอะไรเกิดขึ้น"
        scan.ProcessingNotes = string.IsNullOrWhiteSpace(scan.ProcessingNotes)
            ? $"สแกนใหม่แล้ว → ผลอยู่ที่สแกน {result.Id}"
            : $"{scan.ProcessingNotes}\nสแกนใหม่แล้ว → ผลอยู่ที่สแกน {result.Id}";
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<OcrResultResponse>(true, result,
            $"สแกนใหม่แล้ว (ไม่ใช้โควต้า) — ผลอยู่ที่รายการสแกนใหม่ {result.Id} · รายการเดิมยังเก็บผลอ่านครั้งก่อนไว้เพื่อเปรียบเทียบ"));
    }

    [HttpGet("{scanId:guid}")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> GetResult(Guid companyId, Guid scanId)
        => Ok(new ApiResponse<OcrResultResponse>(true, await _service.GetResultAsync(companyId, scanId)));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<OcrResultResponse>>>> GetResults(Guid companyId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<OcrResultResponse>>(true, await _service.GetResultsAsync(companyId, status, new PagedRequest(page, pageSize))));

    [HttpPost("{scanId:guid}/create-document")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> CreateDocument(
        Guid companyId, Guid scanId, [FromQuery] string? targetType = null,
        [FromQuery] bool approve = false,
        // ผู้ใช้กดยืนยันในกล่องเตือน "ใบนี้ซ้ำ" แล้ว — ด่านกันซ้ำอยู่ฝั่งเซิร์ฟเวอร์
        // ทุกปุ่มจึงถูกกันเหมือนกันหมด ไม่ใช่แค่ปุ่มที่หน้าเว็บนึกจะเช็ค
        [FromQuery] bool allowDuplicate = false)
    {
        // Pass the user GUID (not Identity.Name, which is the email) so the
        // created document's CreatedBy resolves to a real user → its
        // signature prints. The service still owner-falls-back if empty.
        var userGuid = JwtHelper.GetUserIdFromClaims(User);
        var userId = userGuid.ToString();

        // ด่านสิทธิ์ "สร้าง" (T1-06) — ข้อความบอกคีย์ที่ต้องขอ ไม่ใช่ 403 เปล่า
        var target = await ResolveTargetTypeAsync(companyId, scanId, targetType);
        if (target is Models.Enums.DocumentType tType
            && !await DocumentPermissionHelper.CanCreateAsync(_perms, companyId, userGuid, tType))
        {
            return StatusCode(403, new ApiResponse<OcrResultResponse>(false, null,
                $"ไม่มีสิทธิ์สร้างเอกสารประเภท {tType} — ขอสิทธิ์สร้างเอกสาร"
                + (DocumentPermissionHelper.IsRevenue(tType) ? "ฝั่งขาย" : "ฝั่งซื้อ") + "จากเจ้าของบริษัท"));
        }

        var result = await _service.CreateDocumentFromScanAsync(
            companyId, scanId, userId, targetType, allowDuplicate);

        // "สร้าง + อนุมัติ" — อนุมัติผ่าน pipeline ปกติเต็มขั้น (ด่าน §86/4,
        // JE, stock, เลขเอกสารจริง). อนุมัติไม่ผ่าน ≠ ล้มเหลวทั้งก้อน:
        // ใบ Draft ถูกสร้างสำเร็จแล้ว จึงตอบ 200 พร้อมเหตุผลใน ProcessingNotes
        // ให้ผู้ใช้เปิดใบไปแก้แล้วกดอนุมัติเอง (ห้าม throw ทิ้ง — เดี๋ยวผู้ใช้
        // เข้าใจว่าไม่มีใบเกิดขึ้นแล้วสแกนซ้ำ = ใบซ้ำ)
        if (approve && result.CreatedDocumentId.HasValue)
        {
            // ตัวตัดสิน "พร้อมลงบัญชีเองไหม" อยู่ที่ Helpers/OcrPostingReadiness ตัวเดียว
            // (เว็บ · LINE · มือถือ ต้องใช้ตัวเดียวกัน — ไม่งั้นสามช่องทางสามนโยบาย)
            // ครอบทั้งวันที่ที่อ่านไม่ได้ · Σ บรรทัดไม่ตรงหัวใบ · ไม่รู้อัตราแลกเปลี่ยน ·
            // กระดาษยังไม่ใช่ใบกำกับ · 50 ทวิ ที่เราถูกหัก
            var readiness = Helpers.OcrPostingReadiness.Evaluate(
                result.ProcessingNotes, result.ExtractedDate != null);
            var skipReason = readiness.CanAutoApprove ? null : readiness.Reason;
            // T1-06: ด่านสิทธิ์อนุมัติ — ตัวเดียวกับ DocumentController/ลายเซ็น/LINE/มือถือ
            var createdType = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == result.CreatedDocumentId.Value && d.CompanyId == companyId)
                .Select(d => (Models.Enums.DocumentType?)d.DocumentType)
                .FirstOrDefaultAsync();
            if (skipReason == null && createdType is Models.Enums.DocumentType cType
                && !await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userGuid, cType))
                skipReason = $"ไม่มีสิทธิ์อนุมัติเอกสารประเภท {cType} — สร้างเป็น Draft ไว้แล้ว รอผู้มีสิทธิ์อนุมัติ";

            string? approveNote = null;
            if (skipReason != null)
            {
                approveNote = "\n[APPROVE-SKIP] " + skipReason;
            }
            else
            {
                try
                {
                    await _documentService.ApproveDocumentAsync(
                        companyId, result.CreatedDocumentId.Value, userId);
                }
                catch (Exception ex)
                {
                    approveNote = "\n[APPROVE-FAIL] " + ex.Message;
                }
            }
            if (approveNote != null)
            {
                result = result with { ProcessingNotes = (result.ProcessingNotes ?? "") + approveNote };
                // ⚠️ เขียนลงแถวจริงด้วย — เดิมต่อสตริงใส่ DTO อย่างเดียว ⇒ เหตุผลอยู่ใน
                // HTTP response ครั้งเดียวแล้ว**หายถาวร**: เปิดหน้าใหม่/รีเฟรชแล้วไม่มีอะไร
                // บอกว่าทำไมใบยังเป็น Draft (ผลตรวจ 2026-09-06 · T5-N8 — "ล้มดังในที่ที่คนดู")
                var scanRow = await _db.Set<OcrScanResult>()
                    .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanId);
                if (scanRow != null)
                {
                    scanRow.ProcessingNotes = (scanRow.ProcessingNotes ?? "") + approveNote;
                    await _db.SaveChangesAsync();
                }
            }
        }
        return Ok(new ApiResponse<OcrResultResponse>(true, result));
    }

    /// <summary>Rebuild an OCR-created document's lines from the original scan
    /// when it was created empty. Looked up by documentId (the document edit
    /// page calls this). Refuses if the document already has real lines.</summary>
    [HttpPost("documents/{documentId:guid}/repopulate-lines")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> RepopulateLines(Guid companyId, Guid documentId)
        => Ok(new ApiResponse<OcrResultResponse>(true,
            await _service.RepopulateDocumentLinesFromScanAsync(companyId, documentId, User.Identity?.Name ?? ""),
            "ดึงรายการจาก OCR สำเร็จ"));

    /// <summary>ตรวจความครบถ้วนตามกรมสรรพากรซ้ำ จากผลสแกนที่เก็บไว้ —
    /// ใช้ล้างคำเตือนค้างของใบที่สแกนก่อน validator จะถูกปรับปรุง
    /// (ไม่ต้องอัปโหลด/สแกนใหม่)</summary>
    [HttpPost("documents/{documentId:guid}/recheck-compliance")]
    public async Task<ActionResult<ApiResponse<object>>> RecheckCompliance(Guid companyId, Guid documentId)
    {
        var (status, issuesJson) = await _service.RecheckRdComplianceAsync(companyId, documentId);
        return Ok(new ApiResponse<object>(true, new { status, issuesJson }, "ตรวจซ้ำแล้ว"));
    }

    /// <summary>ผูกไฟล์ scan เข้ากับเอกสารที่สร้างผ่าน UI handoff (documents.html
    /// save()) — เรียกหลัง POST /documents สำเร็จ. แก้ปัญหา file ค้างที่
    /// EntityType="OcrScan" ทำให้เปิดเอกสารแล้วไม่เห็นไฟล์แนบ</summary>
    [HttpPost("{scanId:guid}/link-document/{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> LinkScanToDocument(
        Guid companyId, Guid scanId, Guid documentId)
    {
        var ok = await _service.LinkScanToExistingDocumentAsync(companyId, scanId, documentId);
        if (!ok) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบ scan หรือเอกสาร"));
        return Ok(new ApiResponse<bool>(true, true, "ผูกไฟล์ต้นฉบับเข้าเอกสารแล้ว"));
    }

    /// <summary>Record a Journal Entry directly from a scan (no business
    /// document) — for when the real document was issued externally and only
    /// the GL effect is needed here.</summary>
    [HttpPost("{scanId:guid}/create-journal-entry")]
    public async Task<ActionResult<ApiResponse<object>>> CreateJournalEntry(
        Guid companyId, Guid scanId, [FromBody] Models.DTOs.Ocr.CreateJeFromScanRequest request)
    {
        // T1-06: ลง JE จริงต้องมีสิทธิ์จัดการใบสำคัญ (คีย์เดียวกับหน้า JE manual)
        var jeUser = JwtHelper.GetUserIdFromClaims(User);
        if (!await _perms.HasPermissionAsync(companyId, jeUser, Models.Constants.PermissionKeys.JournalManage))
            return StatusCode(403, new ApiResponse<object>(false, null,
                "ไม่มีสิทธิ์บันทึกใบสำคัญ (JE) — ขอสิทธิ์ “จัดการใบสำคัญ” จากเจ้าของบริษัท หรือกด “สร้างเอกสาร” แทน"));
        var jeId = await _service.CreateJournalEntryFromScanAsync(companyId, scanId, request, User.Identity?.Name ?? "");
        return Ok(new ApiResponse<object>(true, new { journalEntryId = jeId }, "บันทึก JE จากสแกนสำเร็จ"));
    }

    public sealed record SetAllLinesProjectRequest(Guid? ProjectId, string? ProjectName, bool OnlyEmpty);

    /// <summary>Apply ONE project to EVERY OCR-extracted line in a
    /// single call. Used by the UI's "main project" picker —
    /// dramatically reduces clicks for the common case where most
    /// lines belong to the same job. OnlyEmpty=true preserves any
    /// per-line overrides the user already made.</summary>
    [HttpPost("{scanId:guid}/lines-project")]
    public async Task<ActionResult<ApiResponse<object>>> SetAllLinesProject(
        Guid companyId, Guid scanId, [FromBody] SetAllLinesProjectRequest req)
    {
        await _service.SetAllExtractedLineProjectsAsync(companyId, scanId,
            req.ProjectId, req.ProjectName, req.OnlyEmpty);
        var verb = req.OnlyEmpty ? "เติม project ให้บรรทัดว่าง" : "ตั้ง project ทุกบรรทัด";
        return Ok(new ApiResponse<object>(true, new
        {
            projectId = req.ProjectId,
            projectName = req.ProjectName,
            onlyEmpty = req.OnlyEmpty,
        }, req.ProjectId.HasValue ? verb + "แล้ว" : "ล้าง project ทุกบรรทัดแล้ว"));
    }

    /// <summary>พรีวิวบรรทัด (อ่านอย่างเดียว ไม่บันทึก) — ปุ่ม “แก้ในฟอร์มก่อน” ต้องใช้ผลนี้
    /// เติมฟอร์ม ไม่คำนวณกระทบยอดเองบนหน้าเว็บ</summary>
    [HttpGet("{scanId:guid}/line-preview")]
    public async Task<ActionResult<ApiResponse<OcrLinePreviewResponse>>> LinePreview(
        Guid companyId, Guid scanId, [FromQuery] string? targetType = null)
        => Ok(new ApiResponse<OcrLinePreviewResponse>(true,
            await _service.PreviewDocumentLinesAsync(companyId, scanId, targetType)));

    /// <summary>List the matched vendor's open Purchase Orders for the
    /// review modal's "เลือก PO" picker — each PO comes back with its
    /// line items inline so the operator can map OCR↔PO lines without
    /// a second call.</summary>
    [HttpGet("{scanId:guid}/open-pos")]
    public async Task<ActionResult<ApiResponse<List<OpenPurchaseOrderDto>>>> GetOpenPos(Guid companyId, Guid scanId)
        => Ok(new ApiResponse<List<OpenPurchaseOrderDto>>(true,
            await _service.GetOpenPosForScanAsync(companyId, scanId)));

    /// <summary>Link this scan to one of the vendor's open POs and record the
    /// per-line OCR↔PO mappings (the "ฟังก์ชันชื่อแทน" function). When
    /// CreateDocument fires later, the resulting Purchase Invoice inherits the
    /// PO's GL accounts on mapped lines and links back via RelatedDocumentId.</summary>
    [HttpPost("{scanId:guid}/link-po")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> LinkPo(
        Guid companyId, Guid scanId, [FromBody] LinkPurchaseOrderRequest request)
        => Ok(new ApiResponse<OcrResultResponse>(true,
            await _service.LinkPurchaseOrderAsync(companyId, scanId, request, User.Identity?.Name ?? "ocr-po-link"),
            "ผูกกับใบสั่งซื้อสำเร็จ"));

    /// <summary>Remove the PO linkage from a scan (no document yet created).
    /// Lets the operator re-pick or fall back to plain expense entry.</summary>
    [HttpDelete("{scanId:guid}/link-po")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> UnlinkPo(Guid companyId, Guid scanId)
        => Ok(new ApiResponse<OcrResultResponse>(true,
            await _service.UnlinkPurchaseOrderAsync(companyId, scanId),
            "ยกเลิกการผูก PO แล้ว"));

    /// <summary>ใบต้นทางทุกชนิดที่อาจตรงกับสแกน (PO/GRN · ใบเสนอราคา/ใบวางบิล/ใบส่งของ · ใบแจ้งหนี้/ใบกำกับ)
    /// — เซิร์ฟเวอร์ให้เหตุผลและระดับความแน่นมาแล้ว หน้าเว็บแสดงอย่างเดียว</summary>
    [HttpGet("{scanId:guid}/predecessor-candidates")]
    public async Task<ActionResult<ApiResponse<List<PredecessorCandidateDto>>>> PredecessorCandidates(Guid companyId, Guid scanId)
        => Ok(new ApiResponse<List<PredecessorCandidateDto>>(true,
            await _service.GetPredecessorCandidatesAsync(companyId, scanId)));

    [HttpPost("{scanId:guid}/link-predecessor")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> LinkPredecessor(
        Guid companyId, Guid scanId, [FromBody] LinkPredecessorRequest request)
        => Ok(new ApiResponse<OcrResultResponse>(true,
            await _service.LinkPredecessorAsync(companyId, scanId, request, User.Identity?.Name ?? "ocr-link"),
            "ผูกกับเอกสารต้นทางแล้ว"));

    [HttpDelete("{scanId:guid}/link-predecessor")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> UnlinkPredecessor(Guid companyId, Guid scanId)
        => Ok(new ApiResponse<OcrResultResponse>(true,
            await _service.UnlinkPredecessorAsync(companyId, scanId),
            "ยกเลิกการผูกเอกสารต้นทางแล้ว"));

    public sealed record SetLineProjectRequest(int LineIndex, Guid? ProjectId, string? ProjectName);

    /// <summary>Assign / clear a project on one OCR-extracted line.
    /// Persists into ExtractedItemsJson so when CreateDocument fires,
    /// the resulting DocumentLine.ProjectId carries this allocation.
    /// Lets user split a multi-line invoice across multiple projects
    /// at review time, before committing the doc.</summary>
    [HttpPost("{scanId:guid}/line-project")]
    public async Task<ActionResult<ApiResponse<object>>> SetLineProject(
        Guid companyId, Guid scanId, [FromBody] SetLineProjectRequest req)
    {
        await _service.SetExtractedLineProjectAsync(companyId, scanId,
            req.LineIndex, req.ProjectId, req.ProjectName);
        return Ok(new ApiResponse<object>(true, new
        {
            lineIndex = req.LineIndex,
            projectId = req.ProjectId,
            projectName = req.ProjectName,
        }, req.ProjectId.HasValue ? "บันทึก project ของบรรทัดแล้ว" : "ยกเลิก project ของบรรทัดแล้ว"));
    }

    public sealed record SetLineFieldsRequest(
        int LineIndex, string? Description, decimal? Quantity, decimal? UnitPrice,
        string? AccountCode = null);

    /// <summary>action = "add" | "delete" · LineIndex ใช้เฉพาะตอน delete</summary>
    public sealed record ModifyLineRequest(string Action, int LineIndex = -1);

    /// <summary>แก้ description/จำนวน/ราคาต่อหน่วยของบรรทัด OCR inline ในหน้า review
    /// (กฎเหล็ก #3). recompute amount = qty×price, persist ลง ExtractedItemsJson แล้ว
    /// CreateDocument อ่านไปใช้. คืน amount ใหม่ให้ UI อัปเดตช่องยอดโดยไม่ refetch.</summary>
    [HttpPost("{scanId:guid}/line-fields")]
    public async Task<ActionResult<ApiResponse<object>>> SetLineFields(
        Guid companyId, Guid scanId, [FromBody] SetLineFieldsRequest req)
    {
        var amount = await _service.SetExtractedLineFieldsAsync(companyId, scanId,
            req.LineIndex, req.Description, req.Quantity, req.UnitPrice, req.AccountCode);
        return Ok(new ApiResponse<object>(true, new
        {
            lineIndex = req.LineIndex,
            amount,
        }, "บันทึกบรรทัดแล้ว"));
    }

    /// <summary>เพิ่ม/ลบบรรทัดรายการของผลสแกน — เดิมตาราง review ทำไม่ได้เลย</summary>
    [HttpPost("{scanId:guid}/modify-line")]
    public async Task<ActionResult<ApiResponse<object>>> ModifyLine(
        Guid companyId, Guid scanId, [FromBody] ModifyLineRequest req)
    {
        var count = await _service.ModifyExtractedLineAsync(companyId, scanId, req.Action, req.LineIndex);
        return Ok(new ApiResponse<object>(true, new { lineCount = count },
            req.Action == "add" ? "เพิ่มบรรทัดแล้ว" : "ลบบรรทัดแล้ว"));
    }

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
    /// Build a per-line preview of what would happen if the user pushed the
    /// "นำเข้าสต็อก" button on this OCR scan. For each extracted line item,
    /// runs the ProductMatcher cascade (alias → code-hit → trigram → fuzzy)
    /// and returns the top-1 candidate plus a few alternatives, with the
    /// confidence score that pre-selects the row in the UI. Lines whose
    /// best confidence stays below the threshold are flagged
    /// <c>WillCreateNew = true</c> so the modal opens them in "create
    /// product" mode by default. No DB writes happen here — purely a
    /// read-only suggestion endpoint the UI polls on modal open.
    /// </summary>
    [HttpGet("{scanId:guid}/stock-preview")]
    public async Task<ActionResult<ApiResponse<OcrStockPreviewResponse>>> StockPreview(
        Guid companyId, Guid scanId,
        [FromServices] Services.Implementations.Ocr.ProductMatcher matcher,
        [FromServices] Services.Implementations.Ocr.GlobalProductLearner globalLearner,
        [FromServices] Services.Implementations.Ocr.GlobalAssetCategoryLearner globalAssetLearner)
    {
        var scan = await _db.OcrScanResults
            .Where(s => s.CompanyId == companyId && s.Id == scanId && !s.IsDeleted)
            .FirstOrDefaultAsync();
        if (scan == null) return NotFound(new ApiResponse<OcrStockPreviewResponse>(false, null, "ไม่พบผลการสแกน"));

        // Lazy backfill — first time the company opens the import modal,
        // seed aliases from any existing purchase DocumentLines so the
        // matcher works without a cold start. Cheap: skip when there's
        // already at least one alias on file.
        var hasAliases = await _db.ProductAliases
            .AsNoTracking()
            .AnyAsync(a => a.CompanyId == companyId && !a.IsDeleted);
        if (!hasAliases)
        {
            await matcher.BackfillAliasesFromHistoryAsync(companyId, User.Identity?.Name ?? "ocr-backfill");
        }

        var lines = new List<OcrLineItemDto>();
        if (!string.IsNullOrWhiteSpace(scan.ExtractedItemsJson))
        {
            try { lines = System.Text.Json.JsonSerializer.Deserialize<List<OcrLineItemDto>>(scan.ExtractedItemsJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
            catch { /* malformed JSON — treat as empty so the UI still opens */ }
        }

        // Run the fixed-asset detector across all lines once. Returns
        // one LineDecision per OCR line — IsPotentialAsset flag + a
        // suggested category + useful-life + confidence. Pre-filtered
        // by the ฿5k threshold + capital-asset keyword list. Cheap
        // (pure in-memory).
        var assetDecisions = Services.Implementations.Ocr.FixedAssetDetector.Analyze(
            lines.Select(l => (l.Description, l.Quantity, l.UnitPrice, l.Amount)).ToList());

        // ดึง global pattern ของทุกบรรทัดครั้งเดียวก่อนเข้าลูป — เดิม MatchAsync
        // ยิง GetActivePatternAsync (ตัวเดี่ยว) ทุกบรรทัด = N+1 query ต่อการสแกน
        // 1 ใบ ทั้งที่ตัวรวม GetActivePatternsAsync เขียนไว้ให้ใช้เพื่อการนี้
        // อยู่ติดกันในไฟล์เดียวกันแต่ไม่มีใครเรียก
        await matcher.PrewarmGlobalPatternsAsync(lines.Select(l => l.Description));

        var resultLines = new List<OcrStockPreviewLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var desc = line.Description ?? "";
            var matches = await matcher.MatchAsync(companyId, desc, scan.MatchedContactId);
            var best = matches.FirstOrDefault();
            var alternatives = matches.Skip(1).ToList();

            var detectedUnit = Services.Implementations.Ocr.ProductMatcher.DetectUnit(desc);
            var detectedQty = line.Quantity ?? Services.Implementations.Ocr.ProductMatcher.DetectQuantity(desc);

            // ── Price anomaly (matched product but OCR'd price is way off) ──
            bool priceAnomaly = false;
            decimal? expectedCost = null;
            string? priceHint = null;
            if (best != null && line.UnitPrice.HasValue && best.CostPrice > 0)
            {
                expectedCost = best.CostPrice;
                var diff = Math.Abs(line.UnitPrice.Value - best.CostPrice);
                var pct = diff / best.CostPrice;
                if (pct >= 0.30m && diff >= 5m)  // 30%+ AND ≥ 5฿ absolute
                {
                    priceAnomaly = true;
                    var dir = line.UnitPrice.Value > best.CostPrice ? "สูงกว่า" : "ต่ำกว่า";
                    priceHint = $"ราคา OCR ฿{line.UnitPrice:N2} {dir}ทุนเดิม ฿{best.CostPrice:N2} ({Math.Round(pct * 100)}%) — ตรวจสอบว่าจับคู่ถูกชนิด/ขนาดไหม";
                }
            }

            // ── Unit-conversion auto-fill ──
            // When OCR'd unit is "ลัง" or "โหล" but the matched product
            // is stocked in "ชิ้น" with a conversion on file, propose
            // converting the qty up-front. The user can untick.
            decimal? convQty = null;
            string? convUnit = null;
            decimal? convRate = null;
            string? convHint = null;
            if (best != null && !string.IsNullOrEmpty(detectedUnit) && !detectedUnit.Equals(best.Unit, StringComparison.OrdinalIgnoreCase))
            {
                var conversion = await _db.UnitConversions
                    .AsNoTracking()
                    .Where(u => u.CompanyId == companyId && u.ProductId == best.ProductId && !u.IsDeleted
                            && u.FromUnit == detectedUnit && u.ToUnit == best.Unit)
                    .FirstOrDefaultAsync();
                if (conversion != null && conversion.ConversionRate > 0 && detectedQty.HasValue)
                {
                    convQty = detectedQty.Value * conversion.ConversionRate;
                    convUnit = conversion.ToUnit;
                    convRate = conversion.ConversionRate;
                    convHint = $"1 {conversion.FromUnit} = {conversion.ConversionRate:0.##} {conversion.ToUnit} — แปลงเป็น {convQty:0.##} {conversion.ToUnit} อัตโนมัติ";
                }
            }

            // Global federated suggestion (only consulted when there's
            // no strong local match — saves work on rows the matcher
            // already nailed). Threshold is adaptive — vendors with a
            // hand-curated alias dictionary (≥10 confirmed mappings) get
            // a lower bar for auto-accept.
            GlobalProductSuggestion? globalSugg = null;
            var threshold = await matcher.GetVendorAdaptiveThresholdAsync(companyId, scan.MatchedContactId);
            var willCreate = best == null || best.Confidence < threshold;
            if (willCreate)
            {
                try
                {
                    var norm = Services.Implementations.Ocr.ProductMatcher.Normalize(desc);
                    var pattern = await globalLearner.GetActivePatternAsync(norm);
                    if (pattern != null)
                    {
                        globalSugg = new GlobalProductSuggestion(
                            CanonicalLabel: pattern.CanonicalLabel,
                            Brand: pattern.Brand,
                            Unit: pattern.Unit,
                            CategoryHint: pattern.CategoryHint,
                            TenantCount: pattern.TenantCount,
                            TotalConfirms: pattern.TotalConfirms);
                    }
                }
                catch { /* federation outage is silent */ }
            }

            // ── Asset detection + federated category hint ──
            var dec = assetDecisions.FirstOrDefault(d => d.LineIndex == i);
            bool isLikelyAsset = dec?.IsPotentialAsset ?? false;
            GlobalAssetSuggestion? globalAssetSugg = null;
            if (isLikelyAsset)
            {
                try
                {
                    var assetNorm = Services.Implementations.Ocr.ProductMatcher.Normalize(desc);
                    var ap = await globalAssetLearner.GetActivePatternAsync(assetNorm);
                    if (ap != null)
                        globalAssetSugg = new GlobalAssetSuggestion(ap.Category, ap.UsefulLifeMonths, ap.DepreciationMethod, ap.TenantCount, ap.TotalConfirms);
                }
                catch { /* federation outage */ }
            }
            var defaultDest = isLikelyAsset
                ? OcrImportDestination.FixedAsset
                : OcrImportDestination.Stock;

            resultLines.Add(new OcrStockPreviewLine(
                LineIndex: i,
                Description: desc,
                Quantity: line.Quantity,
                UnitPrice: line.UnitPrice,
                Amount: line.Amount,
                DetectedUnit: detectedUnit,
                DetectedQuantity: detectedQty,
                BestMatch: best,
                Alternatives: alternatives,
                WillCreateNew: willCreate,
                PriceAnomaly: priceAnomaly,
                ExpectedUnitCost: expectedCost,
                PriceAnomalyHint: priceHint,
                ConvertedQuantity: convQty,
                ConvertedUnit: convUnit,
                ConversionRate: convRate,
                ConversionHint: convHint,
                GlobalSuggestion: globalSugg,
                IsLikelyAsset: isLikelyAsset,
                SuggestedAssetCategory: globalAssetSugg?.Category ?? dec?.SuggestedCategory,
                SuggestedUsefulLifeMonths: globalAssetSugg?.UsefulLifeMonths ?? dec?.SuggestedUsefulLifeMonths,
                AssetConfidence: dec != null ? (double)dec.ConfidenceScore : null,
                AssetReasons: dec?.Reasons,
                DefaultDestination: defaultDest,
                GlobalAssetSuggestion: globalAssetSugg));
        }

        return Ok(new ApiResponse<OcrStockPreviewResponse>(true, new OcrStockPreviewResponse(
            ScanId: scanId,
            VendorContactId: scan.MatchedContactId,
            VendorName: scan.ExtractedVendorName,
            Lines: resultLines)));
    }

    /// <summary>
    /// Commit the user-confirmed import. For each line:
    ///   • If ProductId is set, increment that product's stock and learn the
    ///     OCR'd description as a vendor-bound ProductAlias.
    ///   • If ProductId is null, create a new Product (TrackStock = true,
    ///     ProductType = Product) using NewProductName / NewProductCode (auto
    ///     when null) / VatRate, then book the IN movement, then learn the
    ///     description as the very first alias of the new product.
    /// All movements run inside one DB transaction so a partial failure
    /// doesn't leave half-imported stock.
    /// </summary>
    [HttpPost("{scanId:guid}/import-stock")]
    public async Task<ActionResult<ApiResponse<OcrStockImportResult>>> ImportStock(
        Guid companyId, Guid scanId,
        [FromBody] OcrStockImportRequest req,
        [FromServices] Services.Interfaces.IProductService productService,
        [FromServices] Services.Implementations.Ocr.ProductMatcher matcher,
        [FromServices] Services.Interfaces.IFixedAssetService assetService,
        [FromServices] Services.Implementations.Ocr.GlobalAssetCategoryLearner globalAssetLearner)
    {
        if (req?.Lines == null || req.Lines.Count == 0)
            return BadRequest(new ApiResponse<OcrStockImportResult>(false, null, "ไม่มีรายการที่จะนำเข้า"));

        var scan = await _db.OcrScanResults
            .Where(s => s.CompanyId == companyId && s.Id == scanId && !s.IsDeleted)
            .FirstOrDefaultAsync();
        if (scan == null) return NotFound(new ApiResponse<OcrStockImportResult>(false, null, "ไม่พบผลการสแกน"));

        // ⚠️ กันกดซ้ำ — เดิม endpoint นี้ไม่มี guard ใด ๆ เลย ⇒ double-click /
        // retry / refresh = สต็อกเข้าซ้ำทุกรอบ (ดู OcrScanResult.StockImportedAt)
        if (scan.StockImportedAt.HasValue)
            return BadRequest(new ApiResponse<OcrStockImportResult>(false, null,
                $"สแกนนี้นำเข้าสต็อกไปแล้วเมื่อ {scan.StockImportedAt:dd/MM/yyyy HH:mm} — " +
                "นำเข้าซ้ำจะทำให้ยอดคงเหลือเกินจริง หากต้องแก้ให้ปรับสต็อกที่หน้าสินค้าโดยตรง"));

        var userId = User.Identity?.Name ?? "ocr-stock-import";
        var vendorId = scan.MatchedContactId;
        // ใบที่สแกนมามี VAT จริงไหม — ใช้ตั้ง VatRate ของสินค้าที่สร้างใหม่
        // แทนการ hardcode 7% (สินค้ายกเว้น §81 / อัตรา 0 จะได้ไม่ถูกตั้งผิด)
        var scanHasVat = (scan.ExtractedVatAmount ?? 0m) > 0m;
        var lineResults = new List<OcrStockImportLineResult>();
        var created = 0; var matched = 0; var movements = 0; var aliases = 0; var assetsCreated = 0;

        // Reuse OCR'd line text for alias learning even when the user edits Qty/Cost.
        var ocrLines = new List<OcrLineItemDto>();
        if (!string.IsNullOrWhiteSpace(scan.ExtractedItemsJson))
        {
            try { ocrLines = System.Text.Json.JsonSerializer.Deserialize<List<OcrLineItemDto>>(scan.ExtractedItemsJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
            catch { /* alias learning skipped when JSON is malformed */ }
        }

        // โหลดสินค้าที่ผู้ใช้เลือกไว้ **ครั้งเดียว** ก่อนเข้าลูป
        //
        // ⚠️ ที่มา (ผลตรวจทีม G · SYSTEM_AUDIT_2026-09-07.md G-05): ลูปนี้วน
        // 1 รอบต่อบรรทัดสินค้า และข้างในมี round trip 4-7 ครั้งต่อบรรทัด ⇒ ใบส่งของ
        // ค้าส่ง 50 บรรทัด = **~250-350 query ในหนึ่ง request** ทั้งหมดอยู่ใน
        // transaction เดียว ⇒ ถือ connection + ล็อกแถวสินค้ายาว บิลใหญ่ (100+
        // บรรทัด) เสี่ยง timeout
        var pickedIds = req.Lines.Where(l => l.ProductId.HasValue)
            .Select(l => l.ProductId!.Value).Distinct().ToList();
        var pickedProducts = pickedIds.Count == 0
            ? new Dictionary<Guid, Models.Entities.Product>()
            : await _db.Products
                .Where(x => x.CompanyId == companyId && pickedIds.Contains(x.Id) && !x.IsDeleted)
                .ToDictionaryAsync(x => x.Id);

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            foreach (var item in req.Lines)
            {
                var ocrDesc = (item.LineIndex >= 0 && item.LineIndex < ocrLines.Count) ? ocrLines[item.LineIndex].Description : null;

                // Resolve effective destination: Destination wins; if it's
                // the default Stock and the legacy AsSupplies bool is set,
                // honor that for back-compat.
                var dest = item.Destination;
                if (dest == OcrImportDestination.Stock && item.AsSupplies)
                    dest = OcrImportDestination.Supplies;

                // ──────────────────────────────────────────────────────
                // BRANCH: Fixed Asset destination
                // Capitalize the line as a depreciable asset. Calls the
                // existing FixedAssetService.CreateAsync with auto JE
                // (Dr Asset / Cr AP). No stock movement. Federated
                // pattern updated so the next tenant sees this category.
                // ──────────────────────────────────────────────────────
                if (dest == OcrImportDestination.FixedAsset)
                {
                    var assetName = !string.IsNullOrWhiteSpace(item.NewProductName) ? item.NewProductName!
                                  : !string.IsNullOrWhiteSpace(ocrDesc) ? ocrDesc!
                                  : $"Asset {DateTime.UtcNow:HHmmss}";
                    var assetCode = !string.IsNullOrWhiteSpace(item.AssetCode) ? item.AssetCode!
                                  : await GenerateAssetCodeAsync(companyId);
                    var category = item.AssetCategory ?? item.NewProductCategory ?? "ทั่วไป";
                    var ulm = item.UsefulLifeMonths ?? 60;
                    var depMethod = item.DepreciationMethod switch
                    {
                        "DecliningBalance" => Models.Enums.DepreciationMethod.DecliningBalance,
                        "DoubleDecliningBalance" => Models.Enums.DepreciationMethod.DoubleDecliningBalance,
                        _ => Models.Enums.DepreciationMethod.StraightLine
                    };
                    var purchaseCost = item.UnitCost * (item.Quantity > 0 ? item.Quantity : 1m);

                    try
                    {
                        var assetReq = new Models.DTOs.FixedAsset.CreateFixedAssetRequest(
                            AssetCode: assetCode,
                            Name: assetName,
                            Description: ocrDesc,
                            Category: category,
                            Location: null,
                            SerialNumber: item.SerialNumber,
                            PurchaseDate: scan.ExtractedDate ?? DateTime.UtcNow.Date,
                            PurchaseCost: purchaseCost,
                            SalvageValue: item.SalvageValue ?? 0m,
                            UsefulLifeMonths: ulm,
                            DepreciationMethod: depMethod,
                            AssetAccountId: item.AssetAccountId,
                            DepreciationExpenseAccountId: item.DepreciationExpenseAccountId,
                            AccumulatedDepreciationAccountId: item.AccumulatedDepreciationAccountId,
                            PostAcquisitionJournalEntry: false,
                            CreditAccountId: null);
                        var asset = await assetService.CreateAsync(companyId, assetReq, userId);
                        assetsCreated++;

                        // Federated category pool — anonymized.
                        if (!string.IsNullOrWhiteSpace(ocrDesc))
                        {
                            try
                            {
                                var norm = Services.Implementations.Ocr.ProductMatcher.Normalize(ocrDesc);
                                await globalAssetLearner.RecordConfirmAsync(companyId, norm, category, ulm, depMethod.ToString());
                            }
                            catch { /* federation outage */ }
                        }

                        lineResults.Add(new OcrStockImportLineResult(
                            LineIndex: item.LineIndex,
                            ProductId: null,
                            ProductCode: assetCode,
                            ProductName: assetName,
                            QuantityIn: 0,
                            NewStockBalance: 0,
                            WasCreated: true,
                            AliasLearned: false,
                            ErrorMessage: null,
                            Destination: OcrImportDestination.FixedAsset,
                            FixedAssetId: asset.Id));
                    }
                    catch (Exception ex)
                    {
                        lineResults.Add(new OcrStockImportLineResult(
                            item.LineIndex, null, assetCode, assetName, 0, 0, false, false,
                            $"สร้างสินทรัพย์ไม่สำเร็จ: {ex.Message}",
                            OcrImportDestination.FixedAsset, null));
                    }
                    continue;
                }

                // ──────────────────────────────────────────────────────
                // BRANCH: Stock / Supplies destinations (existing flow)
                // ──────────────────────────────────────────────────────
                Guid productId;
                string code;
                string name;
                var wasCreated = false;

                if (item.ProductId.HasValue)
                {
                    pickedProducts.TryGetValue(item.ProductId.Value, out var p);
                    if (p == null)
                    {
                        lineResults.Add(new OcrStockImportLineResult(item.LineIndex, Guid.Empty, "", "", 0, 0, false, false, "ไม่พบสินค้าในระบบ"));
                        continue;
                    }
                    productId = p.Id; code = p.Code; name = p.Name;
                    matched++;
                }
                else
                {
                    var newName = !string.IsNullOrWhiteSpace(item.NewProductName) ? item.NewProductName!
                                : !string.IsNullOrWhiteSpace(ocrDesc) ? ocrDesc!
                                : $"Auto product {DateTime.UtcNow:HHmmss}";
                    var newCode = !string.IsNullOrWhiteSpace(item.NewProductCode) ? item.NewProductCode!
                                : await GenerateProductCodeAsync(companyId);
                    var createReq = new Models.DTOs.Product.CreateProductRequest(
                        Code: newCode,
                        Name: newName,
                        NameEn: null,
                        Description: ocrDesc,
                        ProductType: dest == OcrImportDestination.Supplies ? Models.Enums.ProductType.Supplies : Models.Enums.ProductType.Product,
                        SKU: null,
                        Barcode: null,
                        Category: item.NewProductCategory,
                        // ⚠️ เดิม `?? "ชิ้น"` ตรง ๆ — เส้นทางเอกสารถูกแก้ให้ผ่าน
                        // UnitInferrer ไปแล้ว แต่เส้นทางนี้ตกค้าง และร้ายกว่าเพราะ
                        // เขียนลง **ทะเบียนสินค้า** ⇒ ค่าไฟ/ค่าบริการได้หน่วย
                        // "ชิ้น" ติดตัวไปตลอด (defect class "แก้ตัวเดียว เหลือที่เหลือ")
                        Unit: !string.IsNullOrWhiteSpace(item.Unit) ? item.Unit
                            : (Services.Implementations.Ocr.UnitInferrer.Infer(ocrDesc) ?? "ชิ้น"),
                        SellingPrice: 0m,
                        CostPrice: item.UnitCost,
                        // ⚠️ เดิม `?? 7m` = สมมติว่าทุกอย่างเสีย VAT 7% ⇒ สินค้าที่
                        // §81 ยกเว้น/อัตรา 0 ถูกตั้ง 7% ในทะเบียน แล้ว **ใบขาย
                        // ทุกใบในอนาคต** ของสินค้านั้นคิด VAT ผิดตามไปด้วย
                        // ใช้หลักฐานบนกระดาษแทนการเดา: ใบที่ไม่มี VAT เลย = 0
                        VatRate: item.VatRate ?? (scanHasVat ? 7m : 0m),
                        IsVatIncluded: false,
                        TrackStock: true,
                        MinimumStock: 0);
                    var prod = await productService.CreateAsync(companyId, createReq);
                    productId = prod.Id; code = prod.Code; name = prod.Name;
                    created++; wasCreated = true;
                }

                decimal newBalance = 0;
                if (req.MoveStock && item.Quantity > 0)
                {
                    var move = await productService.AdjustStockAsync(companyId, new Models.DTOs.Product.StockAdjustmentRequest(
                        ProductId: productId,
                        Quantity: item.Quantity,
                        MovementType: "IN",
                        UnitCost: item.UnitCost,
                        Reference: $"OCR-IMPORT/{scan.ExtractedDocumentNumber ?? scanId.ToString("N").Substring(0, 8)}",
                        Notes: $"นำเข้าจาก OCR (สแกน {scanId}) — {ocrDesc ?? "-"}"), userId);
                    movements++;
                    newBalance = move.BalanceAfter;
                }
                else
                {
                    // ⚠️ ต้องอ่านค่าล่าสุด (ไม่ใช่จาก dict ที่โหลดก่อนลูป) เพราะบรรทัด
                    // ก่อนหน้าในใบเดียวกันอาจขยับสต๊อกของสินค้าตัวเดียวกันไปแล้ว
                    var p = await _db.Products
                        .FirstOrDefaultAsync(x => x.Id == productId && x.CompanyId == companyId);
                    newBalance = p?.CurrentStock ?? 0m;
                }

                var aliasLearned = false;
                if (req.LearnAliases && !string.IsNullOrWhiteSpace(ocrDesc))
                {
                    await matcher.RecordAliasAsync(companyId, productId, ocrDesc!, vendorId, userId,
                        source: wasCreated ? "auto" : "user");
                    aliasLearned = true; aliases++;
                }

                lineResults.Add(new OcrStockImportLineResult(
                    LineIndex: item.LineIndex,
                    ProductId: productId,
                    ProductCode: code,
                    ProductName: name,
                    QuantityIn: item.Quantity,
                    NewStockBalance: newBalance,
                    WasCreated: wasCreated,
                    AliasLearned: aliasLearned,
                    ErrorMessage: null,
                    Destination: dest,
                    FixedAssetId: null));
            }

            // ประทับ marker ใน transaction เดียวกับ movement — commit พร้อมกัน
            // หรือ rollback พร้อมกัน (ไม่มีสถานะ "ของเข้าแล้วแต่ไม่มี marker")
            if (movements > 0 || created > 0 || assetsCreated > 0)
                scan.StockImportedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return StatusCode(500, new ApiResponse<OcrStockImportResult>(false, null, $"นำเข้าสต็อกไม่สำเร็จ: {ex.Message}"));
        }

        return Ok(new ApiResponse<OcrStockImportResult>(true, new OcrStockImportResult(
            LinesProcessed: lineResults.Count,
            ProductsCreated: created,
            ProductsMatched: matched,
            StockMovementsCreated: movements,
            AliasesLearned: aliases,
            FixedAssetsCreated: assetsCreated,
            LineResults: lineResults), "นำเข้าเรียบร้อย"));
    }

    private async Task<string> GenerateAssetCodeAsync(Guid companyId)
    {
        // FA-NNNNN pattern. Falls back to timestamp if pattern is taken.
        var existing = await _db.Set<Models.Entities.FixedAsset>()
            .Where(a => a.CompanyId == companyId && a.AssetCode.StartsWith("FA-"))
            .Select(a => a.AssetCode)
            .ToListAsync();
        var max = 0;
        foreach (var c in existing)
            if (int.TryParse(c.AsSpan(3), out var n) && n > max) max = n;
        return $"FA-{(max + 1):D5}";
    }

    /// <summary>Record a "this OCR'd wording is NOT that product" rejection.
    /// Adds a ProductNegativeAlias scoped to the scan's vendor so future
    /// matches with the same wording from the same supplier will not
    /// re-surface the rejected product. UI calls this when the user
    /// dismisses a suggested candidate in the import modal.</summary>
    [HttpPost("{scanId:guid}/reject-match")]
    public async Task<ActionResult<ApiResponse<object>>> RejectMatch(
        Guid companyId, Guid scanId,
        [FromBody] OcrRejectMatchRequest req,
        [FromServices] Services.Implementations.Ocr.ProductMatcher matcher)
    {
        var scan = await _db.OcrScanResults
            .Where(s => s.CompanyId == companyId && s.Id == scanId && !s.IsDeleted)
            .FirstOrDefaultAsync();
        if (scan == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบผลการสแกน"));
        await matcher.RecordRejectionAsync(
            companyId, req.RejectedProductId, req.OcrDescription, scan.MatchedContactId,
            User.Identity?.Name ?? "ocr-reject", req.Reason);
        return Ok(new ApiResponse<object>(true, null, "บันทึกการปฏิเสธแล้ว ระบบจะไม่เสนอสินค้านี้สำหรับชื่อนี้อีก"));
    }

    private async Task<string> GenerateProductCodeAsync(Guid companyId)
    {
        // Naming scheme: "P-NNNNN" — pads to 5 digits so list sort stays
        // intuitive up to 99,999 products. Falls back to timestamp if the
        // catalog already uses an incompatible scheme.
        var existing = await _db.Products
            .Where(p => p.CompanyId == companyId && p.Code.StartsWith("P-"))
            .Select(p => p.Code)
            .ToListAsync();
        var max = 0;
        foreach (var c in existing)
        {
            if (int.TryParse(c.AsSpan(2), out var n) && n > max) max = n;
        }
        return $"P-{(max + 1):D5}";
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
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid companyId, Guid scanId,
        [FromQuery] bool cascade = false,
        // Optional operator label explaining WHY the scan is being discarded
        // ("ไม่ใช่เอกสารบริษัท", "ค่าใช้จ่ายต้องห้าม", …). Persisted to the
        // audit log before the row is removed.
        [FromQuery] string? reason = null)
    {
        // Capture the operator id so the audit row records WHO labelled+deleted,
        // not just when. Falls back to Guid.Empty when the caller is an int_
        // key with no real user (passed as null so the audit row stays clean).
        Guid? actor;
        try { actor = JwtHelper.GetUserIdFromClaims(User); if (actor == Guid.Empty) actor = null; }
        catch { actor = null; }

        await _service.DeleteScanAsync(companyId, scanId, cascade, reason, actor);
        return Ok(new ApiResponse<object>(true, null,
            cascade ? "ลบ scan และเอกสารที่สร้างอัตโนมัติเรียบร้อย" : "ลบสำเร็จ"));
    }

    [HttpGet("quota")]
    public async Task<ActionResult<ApiResponse<OcrQuotaStatus>>> GetQuota(Guid companyId)
        => Ok(new ApiResponse<OcrQuotaStatus>(true, await _quota.GetQuotaStatusAsync(companyId)));

    /// <summary>
    /// Feeds the per-scan engine selector on the upload page. Returns the
    /// three user-selectable choices (Auto / Azure / Local) with an
    /// `available` flag and (when unavailable) a Thai-language reason —
    /// the UI greys out unavailable options and pre-selects the best
    /// available default.
    ///
    /// "Best available" priority:
    ///   1. Azure DI when configured + has quota — highest accuracy
    ///   2. Local otherwise — Tesseract always works as last resort
    ///   3. Auto is always shown + always recommended when both are up;
    ///      it picks at scan time from the same cascade as before.
    /// </summary>
    [HttpGet("engines")]
    public async Task<ActionResult<ApiResponse<object>>> GetAvailableEngines(Guid companyId)
    {
        var siteSettings = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync();
        var azureConfigured = siteSettings?.AzureDiEnabled == true
            && !string.IsNullOrEmpty(siteSettings.AzureDiEndpoint)
            && !string.IsNullOrEmpty(siteSettings.AzureDiApiKey);
        var (azureQuotaAllowed, azureQuotaReason) = await _quota.CheckAzureQuotaAsync(companyId);
        var quota = await _quota.GetQuotaStatusAsync(companyId);

        // Local availability: Tesseract is in-process and always works,
        // but if the plan caps Local pages and the cap is hit, the next
        // scan would over-shoot. We mirror Azure's gate so the UI is
        // consistent — if Local has a cap and it's full, disable.
        var localExhausted = quota.LocalMaxPagesPerMonth.HasValue
            && (quota.LocalUsedThisMonth ?? 0) >= quota.LocalMaxPagesPerMonth.Value;
        var totalExhausted = quota.TotalAvailable <= 0;

        string? azureDisabledReason =
            !azureConfigured ? "ระบบยังไม่ได้ตั้งค่า Azure DI (admin ต้องเปิดใช้)"
            : !azureQuotaAllowed ? (azureQuotaReason ?? "Azure DI โควต้าหมด")
            : null;
        string? localDisabledReason =
            totalExhausted ? "โควต้า OCR เดือนนี้หมดทั้งหมด — กรุณาซื้อเครดิตเพิ่ม"
            : localExhausted ? "โควต้า Local OCR เดือนนี้เต็มแล้ว"
            : null;
        string? autoDisabledReason = totalExhausted
            ? "โควต้า OCR เดือนนี้หมดทั้งหมด — กรุณาซื้อเครดิตเพิ่ม"
            : null;

        var azureAvailable = azureDisabledReason == null;
        var localAvailable = localDisabledReason == null;
        var autoAvailable = autoDisabledReason == null;

        // Pre-select: prefer Auto when anything works (it picks best at
        // scan time); fall back to whichever single engine is up; finally
        // fall back to "auto" even when disabled so the dropdown has
        // a default value (the button will still be disabled).
        var recommendedDefault = autoAvailable ? "auto"
            : azureAvailable ? "azure"
            : localAvailable ? "local"
            : "auto";

        return Ok(new ApiResponse<object>(true, new
        {
            recommendedDefault,
            engines = new[]
            {
                new
                {
                    key = "auto",
                    label = "อัตโนมัติ (แนะนำ)",
                    description = "ระบบเลือก Engine ที่ดีที่สุดให้อัตโนมัติ — ลองตามลำดับ Azure DI ➜ Local",
                    available = autoAvailable,
                    disabledReason = autoDisabledReason,
                    isRecommended = autoAvailable,
                    tierBadge = "★★★",
                },
                new
                {
                    key = "azure",
                    label = "Azure DI (แม่นยำสูงสุด)",
                    description = "Cloud OCR คุณภาพสูง เหมาะกับใบกำกับภาษีที่มี layout ซับซ้อน — มีค่าใช้จ่ายต่อหน้า",
                    available = azureAvailable,
                    disabledReason = azureDisabledReason,
                    isRecommended = false,
                    tierBadge = "★★★",
                },
                new
                {
                    key = "local",
                    label = "Local (ฟรี)",
                    description = "ใช้ Engine ในเครื่อง (PaddleOCR / Tesseract) ไม่เสีย credit Azure — ความแม่นยำต่ำกว่า",
                    available = localAvailable,
                    disabledReason = localDisabledReason,
                    isRecommended = false,
                    tierBadge = "★★",
                },
            },
            quota = new
            {
                azureUsed = quota.AzureUsedThisMonth,
                azureMax = quota.AzureMaxPagesPerMonth,
                localUsed = quota.LocalUsedThisMonth,
                localMax = quota.LocalMaxPagesPerMonth,
            },
        }));
    }

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

        // Force inline display — passing the filename makes ASP.NET emit
        // 'Content-Disposition: attachment', which triggered a download on the
        // OCR review page instead of showing the document inside the viewer.
        // Set the header manually with 'inline' + escape the filename for a
        // safe RFC 6266 fallback (Thai filenames pass through fine on RFC 5987
        // user-agents and the percent-encoded form is the strict fallback).
        var safeName = Uri.EscapeDataString(file.OriginalFileName ?? "document");
        Response.Headers.ContentDisposition = $"inline; filename=\"{safeName}\"; filename*=UTF-8''{safeName}";
        return PhysicalFile(file.StoragePath, file.ContentType ?? "application/octet-stream");
    }

    /// <summary>
    /// Read current OCR provider configuration + status. Admin uses this on the
    /// admin-ocr page to show "Azure DI: Enabled / Disabled", "Embedded Tesseract:
    /// Ready (eng+tha)" etc.
    /// </summary>
    [HttpGet("admin/config")]
    [Authorize(Roles = "SystemAdmin")]
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
    [Authorize(Roles = "SystemAdmin")]
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
    [Authorize(Roles = "SystemAdmin")]
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
    [Authorize(Roles = "SystemAdmin")]
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
    [Authorize(Roles = "SystemAdmin")]
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
    [Authorize(Roles = "SystemAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> GetVendorIntelligence(
        Guid companyId, [FromQuery] string? taxId, [FromQuery] string? name)
    {
        if (string.IsNullOrEmpty(taxId) && string.IsNullOrEmpty(name))
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุ taxId หรือ name อย่างน้อย 1 อย่าง"));

        var key = Accounting.Helpers.VendorLearningKey.For(taxId, name);

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

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

        // === Pre-upload dedup: read file bytes once, hash, and check for an
        // already-uploaded copy within the last 60 seconds. This catches double
        // form-submits, drag+change race conditions, and rapid retries before
        // we ever create a duplicate FileAttachment + OcrScanResult. ===
        byte[] fileBytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            fileBytes = ms.ToArray();
        }
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

        if (!await _quota.CanScanAsync(companyId))
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
            ContentType = file.ContentType,
            FileSize = file.Length,
            StoragePath = filePath,
            EntityType = "OcrScan",
            EntityId = Guid.NewGuid(),
            UploadedByUserId = JwtHelper.GetUserIdFromClaims(User)
        };
        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync();

        var result = await _service.ScanAsync(companyId, attachment.Id);
        await _quota.IncrementUsageAsync(companyId);
        return Ok(new ApiResponse<OcrResultResponse>(true, result));
    }

    [HttpPost("scan/{fileAttachmentId:guid}")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> Scan(Guid companyId, Guid fileAttachmentId)
        => Ok(new ApiResponse<OcrResultResponse>(true, await _service.ScanAsync(companyId, fileAttachmentId)));

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

    [HttpDelete("{scanId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid companyId, Guid scanId)
    {
        await _service.DeleteScanAsync(companyId, scanId);
        return Ok(new ApiResponse<object>(true, null, "ลบสำเร็จ"));
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
}

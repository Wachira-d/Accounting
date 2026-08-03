using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.FileAttachment;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/attachments")]
[Authorize]
public class FileAttachmentController : ControllerBase
{
    private readonly IFileAttachmentService _attachmentService;
    private readonly IImageProcessingService _images;

    public FileAttachmentController(IFileAttachmentService attachmentService, IImageProcessingService images)
    {
        _attachmentService = attachmentService;
        _images = images;
    }

    [HttpPost("{entityType}/{entityId:guid}")]
    [RequestSizeLimit(25 * 1024 * 1024)] // 25MB max
    public async Task<ActionResult<ApiResponse<FileAttachmentResponse>>> Upload(
        Guid companyId, string entityType, Guid entityId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<FileAttachmentResponse>(false, null!, "กรุณาอัพโหลดไฟล์"));

        // "ExpenseClaim" added 2026 for the §65 ทวิ "ไม่มีใบเสร็จ" flow
        // — employee must attach evidence (photo of goods, taxi meter,
        // CC slip, etc.) before Submit can fire when NoReceipt = true.
        // "PayrollRun" — สลิปโอนเงิน/ใบเสร็จ สปส. + ใบเสร็จ ภ.ง.ด.1 ของงวดเงินเดือน
        // เป็นหลักฐานการจ่ายที่ต้องเก็บ 5 ปี (พ.ร.บ.การบัญชี ม.10)
        var allowedEntityTypes = new[] { "Document", "Contact", "Payment", "JournalEntry", "FixedAsset", "Expense", "ExpenseClaim", "Product", "Project", "PayrollRun" };
        if (!allowedEntityTypes.Contains(entityType))
            return BadRequest(new ApiResponse<FileAttachmentResponse>(false, null!, "ประเภทไม่ถูกต้อง"));

        var userId = JwtHelper.GetUserIdFromClaims(User);
        var storageDir = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "attachments", companyId.ToString());
        Directory.CreateDirectory(storageDir);

        string fileName; string storagePath; long finalSize;
        if (_images.IsProcessableImage(file.ContentType))
        {
            // Choose profile by entity type — slip-like things stay readable, the rest get the generic cap.
            var profile = entityType is "Payment" or "Document" or "PayrollRun" ? ImageProfile.Slip : ImageProfile.Generic;
            await using var s = file.OpenReadStream();
            var processed = await _images.ProcessAndSaveAsync(s, file.ContentType, file.FileName, storageDir, $"/uploads/attachments/{companyId}", profile);
            fileName = Path.GetFileName(processed.AbsolutePath);
            storagePath = Path.Combine("uploads", "attachments", companyId.ToString(), fileName);
            finalSize = processed.FinalBytes;
        }
        else
        {
            var shortGuid = Guid.NewGuid().ToString("N")[..8];
            fileName = $"{entityType}_{entityId}_{DateTime.UtcNow:yyyyMMddHHmmss}_{shortGuid}{Path.GetExtension(file.FileName)}";
            storagePath = Path.Combine("uploads", "attachments", companyId.ToString(), fileName);
            var fullPath = Path.Combine(Directory.GetCurrentDirectory(), storagePath);
            using (var stream = new FileStream(fullPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }
            finalSize = file.Length;
        }

        var result = await _attachmentService.UploadAsync(companyId, entityType, entityId,
            fileName, file.FileName, file.ContentType, finalSize, storagePath, userId);

        return StatusCode(201, new ApiResponse<FileAttachmentResponse>(true, result, "อัพโหลดไฟล์สำเร็จ"));
    }

    [HttpGet("{entityType}/{entityId:guid}")]
    public async Task<ActionResult<ApiResponse<List<FileAttachmentResponse>>>> GetByEntity(
        Guid companyId, string entityType, Guid entityId)
    {
        var result = await _attachmentService.GetByEntityAsync(companyId, entityType, entityId);
        return Ok(new ApiResponse<List<FileAttachmentResponse>>(true, result));
    }

    [HttpDelete("{attachmentId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid attachmentId)
    {
        await _attachmentService.DeleteAsync(companyId, attachmentId);
        return NoContent();
    }

    /// <summary>
    /// Authenticated download — streams the physical file ONLY when the JWT belongs to
    /// a user with access to the owning company. Static-file serving via /uploads/ is
    /// kept for backward compat but UI should prefer this endpoint for sensitive
    /// financial documents to prevent URL leaks (e.g. logged in browser history).
    /// </summary>
    [HttpGet("{attachmentId:guid}/download")]
    public async Task<IActionResult> Download(Guid companyId, Guid attachmentId)
    {
        var attachment = await _attachmentService.GetByIdAsync(companyId, attachmentId);
        if (attachment == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบไฟล์"));

        var fullPath = Path.IsPathRooted(attachment.StoragePath)
            ? attachment.StoragePath
            : Path.Combine(Directory.GetCurrentDirectory(), attachment.StoragePath);
        if (!System.IO.File.Exists(fullPath))
            return NotFound(new ApiResponse<object>(false, null, "ไฟล์ถูกลบหรือย้ายแล้ว"));

        var contentType = string.IsNullOrEmpty(attachment.ContentType)
            ? "application/octet-stream" : attachment.ContentType;
        return PhysicalFile(fullPath, contentType, attachment.OriginalFileName);
    }
}

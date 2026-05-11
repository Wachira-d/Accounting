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

    public FileAttachmentController(IFileAttachmentService attachmentService)
    {
        _attachmentService = attachmentService;
    }

    [HttpPost("{entityType}/{entityId:guid}")]
    [RequestSizeLimit(25 * 1024 * 1024)] // 25MB max
    public async Task<ActionResult<ApiResponse<FileAttachmentResponse>>> Upload(
        Guid companyId, string entityType, Guid entityId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<FileAttachmentResponse>(false, null!, "กรุณาอัพโหลดไฟล์"));

        var allowedEntityTypes = new[] { "Document", "Contact", "Payment", "JournalEntry", "FixedAsset", "Expense", "Product", "Project" };
        if (!allowedEntityTypes.Contains(entityType))
            return BadRequest(new ApiResponse<FileAttachmentResponse>(false, null!, "ประเภทไม่ถูกต้อง"));

        var userId = JwtHelper.GetUserIdFromClaims(User);
        var shortGuid = Guid.NewGuid().ToString("N")[..8];
        var fileName = $"{entityType}_{entityId}_{DateTime.UtcNow:yyyyMMddHHmmss}_{shortGuid}{Path.GetExtension(file.FileName)}";
        var storagePath = Path.Combine("uploads", "attachments", companyId.ToString(), fileName);
        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), storagePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var result = await _attachmentService.UploadAsync(companyId, entityType, entityId,
            fileName, file.FileName, file.ContentType, file.Length, storagePath, userId);

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

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
}

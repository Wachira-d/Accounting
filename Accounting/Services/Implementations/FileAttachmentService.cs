using Accounting.Data;
using Accounting.Models.DTOs.FileAttachment;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class FileAttachmentService : IFileAttachmentService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<FileAttachmentService> _logger;
    private readonly string _storagePath;

    // Allowed file extensions (whitelist)
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
        ".doc", ".docx", ".xls", ".xlsx", ".csv", ".txt",
        ".zip", ".rar", ".7z"
    };

    // Max file sizes per category (in bytes)
    private const long MaxFileSizeDefault = 25 * 1024 * 1024;  // 25 MB
    private const long MaxFileSizeImage = 10 * 1024 * 1024;    // 10 MB

    public FileAttachmentService(AccountingDbContext db, ILogger<FileAttachmentService> logger, IConfiguration config)
    {
        _db = db;
        _logger = logger;
        _storagePath = config["FileStorage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
    }

    public async Task<FileAttachmentResponse> UploadAsync(Guid companyId, string entityType, Guid entityId,
        string fileName, string originalFileName, string contentType, long fileSize, string storagePath, Guid uploadedByUserId)
    {
        // Validate file extension
        var extension = Path.GetExtension(originalFileName);
        if (!AllowedExtensions.Contains(extension))
            throw new InvalidOperationException($"ประเภทไฟล์ {extension} ไม่ได้รับอนุญาต");

        // Validate file size
        var maxSize = contentType.StartsWith("image/") ? MaxFileSizeImage : MaxFileSizeDefault;
        if (fileSize > maxSize)
            throw new InvalidOperationException($"ขนาดไฟล์เกินกำหนด (สูงสุด {maxSize / (1024 * 1024)} MB)");

        // Ensure storage directory exists
        var companyDir = Path.Combine(_storagePath, companyId.ToString(), entityType);
        Directory.CreateDirectory(companyDir);

        // Generate unique filename
        var uniqueFileName = $"{Guid.NewGuid()}{extension}";
        var actualStoragePath = Path.Combine(companyDir, uniqueFileName);

        // If storagePath is provided (temp file), move it; otherwise use the path as-is
        if (File.Exists(storagePath))
        {
            File.Move(storagePath, actualStoragePath);
        }
        else
        {
            actualStoragePath = storagePath; // Use provided path directly
        }

        var attachment = new FileAttachment
        {
            CompanyId = companyId,
            EntityType = entityType,
            EntityId = entityId,
            FileName = uniqueFileName,
            OriginalFileName = originalFileName,
            ContentType = contentType,
            FileSize = fileSize,
            StoragePath = actualStoragePath,
            UploadedByUserId = uploadedByUserId
        };

        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync();

        _logger.LogInformation("File uploaded: {FileName} ({FileSize} bytes) for {EntityType}/{EntityId}",
            originalFileName, fileSize, entityType, entityId);

        var user = await _db.Users.FindAsync(uploadedByUserId);
        return MapToResponse(attachment, user?.FullName ?? "");
    }

    public async Task<List<FileAttachmentResponse>> GetByEntityAsync(Guid companyId, string entityType, Guid entityId)
    {
        var attachments = await _db.FileAttachments
            .Include(f => f.UploadedByUser)
            .Where(f => f.CompanyId == companyId && f.EntityType == entityType && f.EntityId == entityId)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync();

        return attachments.Select(f => MapToResponse(f, f.UploadedByUser?.FullName ?? "")).ToList();
    }

    public async Task DeleteAsync(Guid companyId, Guid attachmentId)
    {
        var attachment = await _db.FileAttachments
            .FirstOrDefaultAsync(f => f.Id == attachmentId && f.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบไฟล์แนบ");

        // Soft delete in DB
        attachment.IsDeleted = true;
        await _db.SaveChangesAsync();

        // Delete physical file
        if (!string.IsNullOrEmpty(attachment.StoragePath) && File.Exists(attachment.StoragePath))
        {
            try
            {
                File.Delete(attachment.StoragePath);
                _logger.LogInformation("File deleted: {StoragePath}", attachment.StoragePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete physical file: {StoragePath}", attachment.StoragePath);
            }
        }
    }

    private static FileAttachmentResponse MapToResponse(FileAttachment f, string uploadedByName) =>
        new(f.Id, f.FileName, f.OriginalFileName, f.ContentType, f.FileSize,
            f.StoragePath, f.ThumbnailPath, f.EntityType, f.EntityId,
            uploadedByName, f.CreatedAt);
}

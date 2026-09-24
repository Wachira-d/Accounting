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
    private readonly IErrorLogService _errorLogService;
    private readonly string _storagePath;

    // Allowed file extensions (whitelist)
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
        ".doc", ".docx", ".xls", ".xlsx", ".csv", ".txt",
        ".zip", ".rar", ".7z"
    };

    // Max file sizes per category (in bytes)
    private const long MaxFileSizeDefault = 25 * 1024 * 1024;  // 25 MB
    private const long MaxFileSizeImage = 10 * 1024 * 1024;    // 10 MB

    public FileAttachmentService(AccountingDbContext db, ILogger<FileAttachmentService> logger, IConfiguration config, IErrorLogService errorLogService)
    {
        _db = db;
        _logger = logger;
        _errorLogService = errorLogService;
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

    public async Task<FileAttachmentResponse> UploadBytesAsync(Guid companyId, string entityType, Guid entityId,
        string originalFileName, string contentType, byte[] content, Guid uploadedByUserId)
    {
        var extension = Path.GetExtension(originalFileName);
        if (!AllowedExtensions.Contains(extension))
            throw new InvalidOperationException($"ประเภทไฟล์ {extension} ไม่ได้รับอนุญาต");

        var maxSize = contentType.StartsWith("image/") ? MaxFileSizeImage : MaxFileSizeDefault;
        if (content.LongLength > maxSize)
            throw new InvalidOperationException($"ขนาดไฟล์เกินกำหนด (สูงสุด {maxSize / (1024 * 1024)} MB)");

        var companyDir = Path.Combine(_storagePath, companyId.ToString(), entityType);
        Directory.CreateDirectory(companyDir);

        var uniqueFileName = $"{Guid.NewGuid()}{extension}";
        var actualStoragePath = Path.Combine(companyDir, uniqueFileName);
        await File.WriteAllBytesAsync(actualStoragePath, content);

        var attachment = new FileAttachment
        {
            CompanyId = companyId,
            EntityType = entityType,
            EntityId = entityId,
            FileName = uniqueFileName,
            OriginalFileName = originalFileName,
            ContentType = contentType,
            FileSize = content.LongLength,
            StoragePath = actualStoragePath,
            UploadedByUserId = uploadedByUserId
        };

        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync();

        _logger.LogInformation("File saved from bytes: {FileName} ({FileSize} bytes) for {EntityType}/{EntityId}",
            originalFileName, content.LongLength, entityType, entityId);

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

        // ⭐ Bulletproof fallback: เอกสารที่สร้างจาก OCR — ถ้าไฟล์ต้นฉบับยัง
        // ค้างที่ EntityType="OcrScan" (relink พลาด: race / old-build / refactor
        // regression) → query หา OcrScanResult ที่ CreatedDocumentId = เอกสารนี้
        // แล้วดึงไฟล์มา + relink on-read (idempotent). กันเคส "บนระบบมีไฟล์ แต่
        // api ดึงไปไม่เจอ" เพราะ EntityType ไม่ตรง.
        if (string.Equals(entityType, "Document", StringComparison.OrdinalIgnoreCase))
        {
            var scanFileIds = await _db.Set<Models.Entities.OcrScanResult>().AsNoTracking()
                .Where(s => s.CompanyId == companyId && s.CreatedDocumentId == entityId
                    && s.FileAttachmentId != null)
                .Select(s => s.FileAttachmentId!.Value)
                .ToListAsync();

            if (scanFileIds.Count > 0)
            {
                var alreadyIds = attachments.Select(a => a.Id).ToHashSet();
                // ย้ายเฉพาะไฟล์ที่ยังเป็นของสแกน (EntityType "OcrScan") — ไฟล์ของรายการอื่นที่ถูกสแกนผ่าน POST ocr/scan/{fileId}
                // หรือไฟล์ของเอกสารใบอื่น ห้ามดึงมาเป็นของใบนี้ (ฝ่ายค้านรอบ 193 · S2-C2 — เดิมย้ายไฟล์ใดก็ได้ที่สแกนชี้)
                var orphanFiles = await _db.FileAttachments
                    .Include(f => f.UploadedByUser)
                    .Where(f => f.CompanyId == companyId && scanFileIds.Contains(f.Id)
                        && !f.IsDeleted && !alreadyIds.Contains(f.Id) && f.EntityType == "OcrScan")
                    .ToListAsync();

                if (orphanFiles.Count > 0)
                {
                    // relink on-read → ครั้งถัดไป query ปกติเจอเลย (lazy repair)
                    foreach (var f in orphanFiles)
                    {
                        f.EntityType = "Document";
                        f.EntityId = entityId;
                    }
                    try { await _db.SaveChangesAsync(); }
                    catch (Exception ex)
                    {
                        // read path — ซ่อมไม่สำเร็จไม่ทำให้การอ่านล้ม (ไฟล์ยังถูกคืนในรอบนี้) แต่ต้องทิ้งร่องรอย
                        _logger.LogWarning(ex, "relink-on-read ของเอกสาร {DocId} ไม่สำเร็จ", entityId);
                    }
                    attachments.AddRange(orphanFiles);
                    attachments = attachments.OrderByDescending(f => f.CreatedAt).ToList();
                }
            }
        }

        return attachments.Select(f => MapToResponse(f, f.UploadedByUser?.FullName ?? "")).ToList();
    }

    public async Task<FileAttachmentResponse?> GetByIdAsync(Guid companyId, Guid attachmentId)
    {
        var attachment = await _db.FileAttachments
            .Include(f => f.UploadedByUser)
            .FirstOrDefaultAsync(f => f.Id == attachmentId && f.CompanyId == companyId);
        return attachment == null ? null : MapToResponse(attachment, attachment.UploadedByUser?.FullName ?? "");
    }

    public async Task DeleteAsync(Guid companyId, Guid attachmentId, bool keepPhysicalFile = false)
    {
        var attachment = await _db.FileAttachments
            .FirstOrDefaultAsync(f => f.Id == attachmentId && f.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบไฟล์แนบ");

        // Soft delete in DB
        attachment.IsDeleted = true;
        await _db.SaveChangesAsync();

        // Delete physical file — ยกเว้นหลักฐานที่ต้องเก็บตามกฎหมาย (ผู้เรียกตัดสินผ่าน keepPhysicalFile)
        if (keepPhysicalFile)
        {
            _logger.LogInformation("Attachment {Id} soft-deleted; physical file retained (legal retention)", attachmentId);
            return;
        }
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
                await _errorLogService.LogErrorAsync(ex, $"FileAttachment.DeleteFile/{attachment.StoragePath}");
            }
        }
    }

    private static FileAttachmentResponse MapToResponse(FileAttachment f, string uploadedByName) =>
        new(f.Id, f.FileName, f.OriginalFileName, f.ContentType, f.FileSize,
            f.StoragePath, f.ThumbnailPath, f.EntityType, f.EntityId,
            uploadedByName, f.CreatedAt);
}

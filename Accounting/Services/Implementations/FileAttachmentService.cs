using Accounting.Data;
using Accounting.Models.DTOs.FileAttachment;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class FileAttachmentService : IFileAttachmentService
{
    private readonly AccountingDbContext _db;

    public FileAttachmentService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<FileAttachmentResponse> UploadAsync(Guid companyId, string entityType, Guid entityId,
        string fileName, string originalFileName, string contentType, long fileSize, string storagePath, Guid uploadedByUserId)
    {
        var attachment = new FileAttachment
        {
            CompanyId = companyId,
            EntityType = entityType,
            EntityId = entityId,
            FileName = fileName,
            OriginalFileName = originalFileName,
            ContentType = contentType,
            FileSize = fileSize,
            StoragePath = storagePath,
            UploadedByUserId = uploadedByUserId
        };

        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync();

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

        attachment.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    private static FileAttachmentResponse MapToResponse(FileAttachment f, string uploadedByName) =>
        new(f.Id, f.FileName, f.OriginalFileName, f.ContentType, f.FileSize,
            f.StoragePath, f.ThumbnailPath, f.EntityType, f.EntityId,
            uploadedByName, f.CreatedAt);
}

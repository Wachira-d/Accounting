using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class NotificationConfigService : INotificationConfigService
{
    private readonly AccountingDbContext _db;

    public NotificationConfigService(AccountingDbContext db) => _db = db;

    public NotificationCatalogResponse GetCatalog() => new(
        NotificationEvents.All.Select(e => new NotificationEventCatalogItem(e.Domain, e.Key, e.Label)).ToList(),
        NotificationRecipientRoles.All.ToList());

    // ===== Company settings (admin matrix) =====

    public async Task<List<NotificationSettingDto>> GetCompanySettingsAsync(Guid companyId)
    {
        var rows = await _db.NotificationSettings
            .AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .ToListAsync();
        var list = rows.Select(r => new NotificationSettingDto(
            r.EventKey, r.RecipientRole, r.EnableSystem, r.EnableEmail, r.EnableLine, r.EnableLineGroup)).ToList();
        // รอบ 232 (232-Notify): คีย์ที่ย้ายจากประกาศกลุ่ม LINE เดิม — บริษัทที่ยังไม่มีแถวของคีย์นั้นเห็น "ค่าตั้งต้นที่ระบบใช้อยู่" (IsDefault)
        // ตัวเดียวกับที่ NotificationEngine ใช้ (NotificationAudiencePolicy.DefaultMatrix) — หน้าเว็บไม่มีสำเนากติกา
        var line = await _db.Set<CompanySettings>().AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => new { s.LineEnabled, s.LineChannelAccessToken, s.LineDefaultGroupId }).FirstOrDefaultAsync();
        var groupConfigured = line != null && NotificationAudiencePolicy.CompanyLineGroupConfigured(line.LineEnabled, line.LineChannelAccessToken, line.LineDefaultGroupId);
        var configuredKeys = rows.Select(r => r.EventKey).ToHashSet(StringComparer.Ordinal);
        foreach (var key in NotificationAudiencePolicy.MigratedBroadcastKeys)
        {
            if (configuredKeys.Contains(key)) continue;
            foreach (var d in NotificationAudiencePolicy.DefaultMatrix(key, groupConfigured))
                list.Add(new NotificationSettingDto(key, d.RecipientRole, d.EnableSystem, d.EnableEmail, d.EnableLine, d.EnableLineGroup, IsDefault: true));
        }
        return list;
    }

    public async Task BulkUpsertCompanySettingsAsync(Guid companyId, BulkUpdateNotificationSettingsRequest request, string updatedBy)
    {
        if (request?.Settings == null) return;
        var existing = await _db.NotificationSettings
            .Where(s => s.CompanyId == companyId)
            .ToListAsync();
        var existingMap = existing.ToDictionary(s => (s.EventKey, s.RecipientRole));

        foreach (var dto in request.Settings)
        {
            if (string.IsNullOrWhiteSpace(dto.EventKey) || string.IsNullOrWhiteSpace(dto.RecipientRole)) continue;
            if (existingMap.TryGetValue((dto.EventKey, dto.RecipientRole), out var row))
            {
                row.EnableSystem = dto.EnableSystem;
                row.EnableEmail = dto.EnableEmail;
                row.EnableLine = dto.EnableLine;
                row.EnableLineGroup = dto.EnableLineGroup;
                row.UpdatedAt = DateTime.UtcNow;
                row.UpdatedBy = updatedBy;
            }
            else
            {
                _db.NotificationSettings.Add(new NotificationSetting
                {
                    CompanyId = companyId,
                    EventKey = dto.EventKey,
                    RecipientRole = dto.RecipientRole,
                    EnableSystem = dto.EnableSystem,
                    EnableEmail = dto.EnableEmail,
                    EnableLine = dto.EnableLine,
                    EnableLineGroup = dto.EnableLineGroup,
                    CreatedBy = updatedBy,
                });
            }
        }
        await _db.SaveChangesAsync();
    }

    // ===== User preferences =====

    public async Task<List<NotificationPreferenceDto>> GetUserPreferencesAsync(Guid companyId, Guid userId)
    {
        var rows = await _db.NotificationPreferences
            .AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.UserId == userId)
            .ToListAsync();
        return rows.Select(r => new NotificationPreferenceDto(
            r.EventKey, r.SuppressSystem, r.SuppressEmail, r.SuppressLine)).ToList();
    }

    public async Task BulkUpsertUserPreferencesAsync(Guid companyId, Guid userId, BulkUpdateNotificationPreferencesRequest request)
    {
        if (request?.Preferences == null) return;
        var existing = await _db.NotificationPreferences
            .Where(p => p.CompanyId == companyId && p.UserId == userId)
            .ToListAsync();
        var existingMap = existing.ToDictionary(p => p.EventKey);

        foreach (var dto in request.Preferences)
        {
            if (string.IsNullOrWhiteSpace(dto.EventKey)) continue;
            // If all suppressions are off, treat as "no override" and delete the row.
            var noOverride = !dto.SuppressSystem && !dto.SuppressEmail && !dto.SuppressLine;
            if (existingMap.TryGetValue(dto.EventKey, out var row))
            {
                if (noOverride)
                {
                    _db.NotificationPreferences.Remove(row);
                }
                else
                {
                    row.SuppressSystem = dto.SuppressSystem;
                    row.SuppressEmail = dto.SuppressEmail;
                    row.SuppressLine = dto.SuppressLine;
                    row.UpdatedAt = DateTime.UtcNow;
                }
            }
            else if (!noOverride)
            {
                _db.NotificationPreferences.Add(new NotificationPreference
                {
                    CompanyId = companyId,
                    UserId = userId,
                    EventKey = dto.EventKey,
                    SuppressSystem = dto.SuppressSystem,
                    SuppressEmail = dto.SuppressEmail,
                    SuppressLine = dto.SuppressLine,
                });
            }
        }
        await _db.SaveChangesAsync();
    }

    // ===== LINE binding =====

    public async Task<LineBindingResponse> GetLineBindingAsync(Guid userId)
    {
        var lid = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.LineUserId)
            .FirstOrDefaultAsync();
        return new LineBindingResponse(lid, !string.IsNullOrEmpty(lid));
    }

    public async Task<LineBindingResponse> SetLineBindingAsync(Guid userId, LineBindingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.LineUserId))
            throw new InvalidOperationException("กรุณาระบุ LINE User ID");
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีผู้ใช้");
        user.LineUserId = request.LineUserId.Trim();
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new LineBindingResponse(user.LineUserId, true);
    }

    public async Task<LineBindingResponse> ClearLineBindingAsync(Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีผู้ใช้");
        user.LineUserId = null;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new LineBindingResponse(null, false);
    }
}

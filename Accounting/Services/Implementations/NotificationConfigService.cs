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
        NotificationRecipientRoles.All.ToList(),
        // รอบ 258 (E-H1): ป้ายบทบาทจากเซิร์ฟเวอร์ (ตัวตั้งตัวเดียว NotificationRecipientRoles.Labels)
        NotificationRecipientRoles.All.ToDictionary(r => r, r => NotificationRecipientRoles.Labels.TryGetValue(r, out var l) ? l : r));

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
        // รอบ 258 (A-H3 · E-H1): ช่อง "กลุ่ม LINE ของบริษัท" ที่ผู้เรียกไม่ได้ส่ง (null) = คงค่าที่ระบบใช้อยู่ — ตัวตัดสินเดียว NotificationSettingMerge ·
        // ค่าที่ใช้อยู่ของเหตุการณ์ = แถวที่บันทึกไว้ (ก่อนคำขอนี้) หรือค่าตั้งต้นของระบบเมื่อยังไม่เคยตั้ง (ตัวเดียวกับที่ NotificationEngine ใช้)
        var storedGroupByEvent = existing.GroupBy(s => s.EventKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Any(s => s.EnableLineGroup), StringComparer.Ordinal);
        var line = await _db.Set<CompanySettings>().AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => new { s.LineEnabled, s.LineChannelAccessToken, s.LineDefaultGroupId }).FirstOrDefaultAsync();
        var groupConfigured = line != null && NotificationAudiencePolicy.CompanyLineGroupConfigured(line.LineEnabled, line.LineChannelAccessToken, line.LineDefaultGroupId);

        // รอบ 258 ฝ่ายค้าน R-L3: ค่ากลุ่มที่คำขอระบุชัด ตัดสินต่อเหตุการณ์ (เครื่องแจ้งเตือนอ่านต่อเหตุการณ์) แล้วเขียนลงทุกแถวที่เก็บไว้ของเหตุการณ์นั้น
        var explicitByEvent = request.Settings
            .Where(d => d != null && !string.IsNullOrWhiteSpace(d.EventKey))
            .GroupBy(d => d.EventKey, StringComparer.Ordinal)
            .Select(g => new { g.Key, Value = NotificationSettingMerge.EventValue(g.Select(d => d.EnableLineGroup)) })
            .Where(x => x.Value != null)
            .ToDictionary(x => x.Key, x => x.Value!.Value, StringComparer.Ordinal);
        foreach (var stored in existing)
            if (explicitByEvent.TryGetValue(stored.EventKey, out var eventValue) && stored.EnableLineGroup != eventValue)
            {
                stored.EnableLineGroup = eventValue;
                stored.UpdatedAt = DateTime.UtcNow;
                stored.UpdatedBy = updatedBy;
            }

        foreach (var dto in request.Settings)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.EventKey) || string.IsNullOrWhiteSpace(dto.RecipientRole)) continue;
            var eventHasRows = storedGroupByEvent.TryGetValue(dto.EventKey, out var anyStoredOn);
            // ค่าที่ระบุชัดของเหตุการณ์ (ถ้ามี) ชนะค่าของแถว — ทุกแถวของเหตุการณ์ได้ค่าเดียวกัน
            var requestedGroup = explicitByEvent.TryGetValue(dto.EventKey, out var explicitValue) ? explicitValue : dto.EnableLineGroup;
            var systemDefaultOn = !eventHasRows
                && NotificationAudiencePolicy.DefaultMatrix(dto.EventKey, groupConfigured).Any(d => d.EnableLineGroup);
            if (existingMap.TryGetValue((dto.EventKey, dto.RecipientRole), out var row))
            {
                row.EnableSystem = dto.EnableSystem;
                row.EnableEmail = dto.EnableEmail;
                row.EnableLine = dto.EnableLine;
                row.EnableLineGroup = NotificationSettingMerge.LineGroup(requestedGroup, row.EnableLineGroup, eventHasRows, anyStoredOn, systemDefaultOn);
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
                    EnableLineGroup = NotificationSettingMerge.LineGroup(requestedGroup, null, eventHasRows, anyStoredOn, systemDefaultOn),
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

    /// <summary>ท้าย 6 ตัวของ LINE userId (ห้ามส่ง/จด id เต็ม — เป็นตัวระบุตัวบุคคลฝั่ง LINE และกุญแจยืนยันตัวของบอท)</summary>
    private static string? Tail(string? lineUserId)
        => string.IsNullOrEmpty(lineUserId) ? null : (lineUserId.Length > 6 ? lineUserId[^6..] : lineUserId);

    public async Task<LineBindingResponse> GetLineBindingAsync(Guid userId)
    {
        var lid = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.LineUserId)
            .FirstOrDefaultAsync();
        return new LineBindingResponse(!string.IsNullOrEmpty(lid), Tail(lid));
    }

    // รอบ 258 (A-H2): SetLineBindingAsync (ผูกด้วยการพิมพ์ LINE userId ดิบ) ถูกถอด — User.LineUserId คือกุญแจยืนยันตัวของบอทกลาง
    // (สร้างเอกสาร · อนุมัติ · ดูยอด) ทางนั้นข้ามทุกด่านของทางผูกด้วยรหัส (จำกัดความถี่ · audit · อีเมลแจ้งเจ้าของบัญชี) ·
    // ทางผูกเดียวที่เหลือ = รหัส 6 หลัก (LineBotService.HandleMessageAsync "ผูก 123456")

    public async Task<LineBindingResponse> ClearLineBindingAsync(Guid userId, Guid? companyId = null, string? ipAddress = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีผู้ใช้");
        var tail = Tail(user.LineUserId);
        user.LineUserId = null;
        user.UpdatedAt = DateTime.UtcNow;
        // การเปลี่ยนแปลงที่กระทบ "ใครสั่งบอทในนามบัญชีนี้ได้" ห้ามเงียบ (คู่กับ audit ตอนผูกใน LineBotService) — ไม่มีอะไรให้ถอด = ไม่จด
        if (tail != null)
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                UserEmail = user.Email,
                Action = Models.Enums.AuditAction.Update,
                EntityType = "LineBindCode",
                EntityId = userId.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new { Bound = false, LineUserIdTail = tail }),
                IpAddress = ipAddress,
            });
        await _db.SaveChangesAsync();
        return new LineBindingResponse(false, null);
    }
}

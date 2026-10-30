using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ค่าตั้งการเรียกเก็บที่หน้าแอดมินแสดง/แก้ (echo ทุกช่องที่รับ + ช่วงที่รับได้จากตัวตั้งเดียว)</summary>
public sealed record PlatformBillingSettingsDto(
    int RenewalInvoiceLeadDays,
    string ReminderDaysBeforeDue,
    string ReminderDaysAfterDue,
    int SuspendGraceDays,
    bool AutoSuspendEnabled,
    int PaymentLinkExpiryDays,
    bool ReminderEmailEnabled,
    bool ReminderLineEnabled,
    DateTime? UpdatedAt,
    string? UpdatedBy,
    bool IsDefault,
    string EnforcementMode,
    string SuspensionSummary,
    object Limits);

public sealed record UpdatePlatformBillingSettingsRequest(
    int RenewalInvoiceLeadDays,
    string? ReminderDaysBeforeDue,
    string? ReminderDaysAfterDue,
    int SuspendGraceDays,
    bool AutoSuspendEnabled,
    int PaymentLinkExpiryDays,
    bool ReminderEmailEnabled,
    bool ReminderLineEnabled);

/// <summary>ผู้แก้ค่าตั้ง (ลง audit chain)</summary>
public sealed record PlatformBillingActor(Guid? UserId, string? Email, string? Ip);

/// <summary>
/// รอบ 219 เฟส 2 (คำตัดสินข้อ 180) — อ่าน/แก้ค่าตั้งการเรียกเก็บค่าบริการ NextAcc (แถวเดียว) · อ่านสดทุกครั้ง ไม่แคช (กฎ #4 D) ·
/// แก้ = SystemAdmin เท่านั้น (controller) + audit chain <c>PLATFORM-BILLING-SETTINGS</c>
/// </summary>
public interface IPlatformBillingSettingsService
{
    Task<PlatformBillingPolicy> GetPolicyAsync(CancellationToken ct = default);
    Task<PlatformBillingSettingsDto> GetAsync(SubscriptionEnforcementMode effectiveMode, CancellationToken ct = default);
    Task<PlatformBillingSettingsDto> UpdateAsync(UpdatePlatformBillingSettingsRequest req, PlatformBillingActor actor,
        SubscriptionEnforcementMode effectiveMode, CancellationToken ct = default);
}

public class PlatformBillingSettingsService : IPlatformBillingSettingsService
{
    public const string AuditRuleCode = "PLATFORM-BILLING-SETTINGS";
    private readonly AccountingDbContext _db;
    public PlatformBillingSettingsService(AccountingDbContext db) => _db = db;

    /// <summary>ตัวโหลดเดียวของทุกผู้อ่าน (service · job · SubscriptionService ที่ไม่ได้ inject service นี้) — ไม่มีแถว = ค่าตั้งต้น</summary>
    public static async Task<PlatformBillingPolicy> LoadPolicyAsync(AccountingDbContext db, CancellationToken ct = default)
    {
        var row = await db.PlatformBillingSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == PlatformBillingSettings.SingletonId, ct);
        return PlatformBillingSettingsRules.FromRow(row);
    }

    public Task<PlatformBillingPolicy> GetPolicyAsync(CancellationToken ct = default) => LoadPolicyAsync(_db, ct);

    public async Task<PlatformBillingSettingsDto> GetAsync(SubscriptionEnforcementMode effectiveMode, CancellationToken ct = default)
    {
        var row = await _db.PlatformBillingSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == PlatformBillingSettings.SingletonId, ct);
        return ToDto(row, effectiveMode);
    }

    public async Task<PlatformBillingSettingsDto> UpdateAsync(UpdatePlatformBillingSettingsRequest req, PlatformBillingActor actor,
        SubscriptionEnforcementMode effectiveMode, CancellationToken ct = default)
    {
        var err = PlatformBillingSettingsRules.Validate(req.RenewalInvoiceLeadDays, req.ReminderDaysBeforeDue, req.ReminderDaysAfterDue,
            req.SuspendGraceDays, req.PaymentLinkExpiryDays, out var before, out var after);
        if (err != null) throw new BusinessRuleException(err, "PLATFORM-BILLING-SETTINGS-INVALID");

        var row = await _db.PlatformBillingSettings.FirstOrDefaultAsync(x => x.Id == PlatformBillingSettings.SingletonId, ct);
        var old = row == null ? null : Snapshot(row);
        if (row == null)
        {
            row = new PlatformBillingSettings { Id = PlatformBillingSettings.SingletonId, CreatedBy = actor.UserId?.ToString() };
            _db.PlatformBillingSettings.Add(row);
        }
        row.RenewalInvoiceLeadDays = req.RenewalInvoiceLeadDays;
        row.ReminderDaysBeforeDue = before;
        row.ReminderDaysAfterDue = after;
        row.SuspendGraceDays = req.SuspendGraceDays;
        row.AutoSuspendEnabled = req.AutoSuspendEnabled;
        row.PaymentLinkExpiryDays = req.PaymentLinkExpiryDays;
        row.ReminderEmailEnabled = req.ReminderEmailEnabled;
        row.ReminderLineEnabled = req.ReminderLineEnabled;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = actor.UserId?.ToString();

        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = null,
            UserId = actor.UserId,
            UserEmail = actor.Email ?? "",
            Action = AuditAction.Update,
            EntityType = nameof(PlatformBillingSettings),
            EntityId = PlatformBillingSettings.SingletonId.ToString(),
            OldValues = old == null ? null : JsonSerializer.Serialize(old),
            NewValues = JsonSerializer.Serialize(new { settings = Snapshot(row), ruleCode = AuditRuleCode }),
            IpAddress = actor.Ip,
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return ToDto(row, effectiveMode);
    }

    private static object Snapshot(PlatformBillingSettings r) => new
    {
        r.RenewalInvoiceLeadDays, r.ReminderDaysBeforeDue, r.ReminderDaysAfterDue, r.SuspendGraceDays,
        r.AutoSuspendEnabled, r.PaymentLinkExpiryDays, r.ReminderEmailEnabled, r.ReminderLineEnabled,
    };

    private static PlatformBillingSettingsDto ToDto(PlatformBillingSettings? row, SubscriptionEnforcementMode mode)
    {
        var p = PlatformBillingSettingsRules.FromRow(row);
        return new PlatformBillingSettingsDto(
            p.RenewalInvoiceLeadDays,
            string.Join(",", p.ReminderDaysBeforeDue),
            string.Join(",", p.ReminderDaysAfterDue),
            p.SuspendGraceDays, p.AutoSuspendEnabled, p.PaymentLinkExpiryDays, p.ReminderEmailEnabled, p.ReminderLineEnabled,
            row?.UpdatedAt, row?.UpdatedBy, row == null,
            mode.ToString(),
            PlatformBillingSettingsRules.DescribeSuspension(p, mode == SubscriptionEnforcementMode.Enforce),
            new
            {
                renewalLead = new[] { PlatformBillingSettingsRules.MinRenewalInvoiceLeadDays, PlatformBillingSettingsRules.MaxRenewalInvoiceLeadDays },
                grace = new[] { PlatformBillingSettingsRules.MinSuspendGraceDays, PlatformBillingSettingsRules.MaxSuspendGraceDays },
                linkExpiry = new[] { PlatformBillingSettingsRules.MinPaymentLinkExpiryDays, PlatformBillingSettingsRules.MaxPaymentLinkExpiryDays },
                maxReminderDay = PlatformBillingSettingsRules.MaxReminderDay,
                maxReminderCount = PlatformBillingSettingsRules.MaxReminderCount,
            });
    }
}

using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>รอบ 228 (PLATFORM_OMISE_AUDIT §5.4): บันทึกข้อพิพาท/chargeback จาก webhook ที่ยืนยันแล้ว</summary>
public interface IPaymentDisputeService
{
    /// <summary>บันทึกข้อพิพาทของรายการ (ในบริษัทของ config ที่ยืนยัน webhook) — idempotent (ค่าเดิม = ไม่เขียนซ้ำ/ไม่เตือนซ้ำ) ·
    /// <b>ไม่เปลี่ยนสถานะเงิน/สิทธิ์</b> · คืน true เมื่อมีการเปลี่ยน</summary>
    Task<bool> RecordAsync(Guid companyId, Guid intentId, ProviderDispute dispute, string source, CancellationToken ct = default);

    /// <summary>ฝ่ายค้านรอบ 228 S5: รับทราบ/จัดการข้อพิพาทแล้ว (ล้างธง "ต้องจัดการ" จนกว่าข้อพิพาทจะเปลี่ยน) · audit chain · ผู้เรียกตรวจสิทธิ์
    /// (ค่าบริการ NextAcc = SystemAdmin · ของร้าน = ผู้จัดการช่องทางรับชำระของร้าน)</summary>
    Task<bool> AcknowledgeAsync(Guid companyId, Guid intentId, string actor, string? note, CancellationToken ct = default);
}

/// <summary>
/// ข้อพิพาท/chargeback: ประวัติรายการ (<c>PaymentIntentEvent</c>) + ช่องบนรายการ (หน้าแอดมิน/รายการรับชำระแสดง) + audit chain +
/// กระดิ่งแอดมินแพลตฟอร์มเมื่อเป็นเงินค่าบริการ NextAcc · การตัดสินพักสิทธิ์/ใบลดหนี้/ค่าเสียหายเป็นของคน (<see cref="PaymentDisputeRules"/>)
/// </summary>
public class PaymentDisputeService : IPaymentDisputeService
{
    public const string AdminIntentsUrl = "/admin/platform-gateway.html#intents";
    public const string ShopIntentsUrl = "/pages/payment-intents.html";

    private readonly AccountingDbContext _db;
    private readonly ILogger<PaymentDisputeService> _logger;
    private readonly Accounting.Services.Interfaces.INotificationService? _notifications;

    public PaymentDisputeService(AccountingDbContext db, ILogger<PaymentDisputeService> logger,
        Accounting.Services.Interfaces.INotificationService? notifications = null)
    { _db = db; _logger = logger; _notifications = notifications; }

    public async Task<bool> RecordAsync(Guid companyId, Guid intentId, ProviderDispute dispute, string source, CancellationToken ct = default)
    {
        // ฝ่ายค้าน S4: ล็อกแถวรายการ — webhook สองตัวของข้อพิพาทเดียวกันมาพร้อมกันต้องตัดสินทีละตัว (ไม่ถอย/ไม่ซ้ำ)
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"PaymentIntents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", new object[] { intentId, companyId }, ct);
        var intent = await _db.PaymentIntents.FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);
        if (intent == null) return false;
        if (!PaymentDisputeRules.ShouldApply(intent.DisputeProviderRef, intent.DisputeState, intent.DisputeAmount,
                dispute.ProviderRef, dispute.State, dispute.Amount))
            return false;

        var before = intent.DisputeState;
        intent.DisputeState = dispute.State;
        intent.DisputeProviderRef = string.IsNullOrWhiteSpace(dispute.ProviderRef) ? intent.DisputeProviderRef : dispute.ProviderRef;
        intent.DisputeAmount = dispute.Amount;
        intent.DisputeReason = dispute.Reason;
        intent.DisputeUpdatedAt = DateTime.UtcNow;
        intent.DisputeAcknowledgedAt = null;   // เปลี่ยนแล้ว ⇒ ต้องดูใหม่
        intent.DisputeAcknowledgedBy = null;
        intent.UpdatedAt = DateTime.UtcNow;
        _db.PaymentIntentEvents.Add(new PaymentIntentEvent
        {
            CompanyId = companyId, IntentId = intent.Id, At = DateTime.UtcNow, Source = PaymentEventSource.Webhook,
            FromStatus = intent.Status, ToStatus = intent.Status,
            Note = $"⚠️ {PaymentDisputeRules.Label(dispute.State)} · ยอด {dispute.Amount:N2} · อ้างอิง {dispute.ProviderRef}"
                 + (string.IsNullOrWhiteSpace(dispute.Reason) ? "" : $" · เหตุ: {dispute.Reason}")
                 + $" · จาก {source} · {PaymentDisputeRules.NextStep(dispute.State)}",
        });
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, EntityType = nameof(PaymentIntent), EntityId = intent.Id.ToString(), Action = AuditAction.Update,
            OldValues = before == null ? null : System.Text.Json.JsonSerializer.Serialize(new { disputeState = before.ToString() }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "payment-dispute", ruleCode = PaymentDisputeRules.RuleCode, disputeState = dispute.State.ToString(),
                raw = dispute.RawStatus, dispute.ProviderRef, dispute.Amount, source,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _logger.LogWarning("ข้อพิพาท {State} รายการ {Intent} บริษัท {Company} ยอด {Amount} ({Ref})", dispute.State, intent.Id, companyId, dispute.Amount, dispute.ProviderRef);

        // กระดิ่ง (ล้มไม่ทำให้การบันทึกหาย — ช่องบนรายการ + ประวัติอยู่แล้ว): ค่าบริการ NextAcc ⇒ แอดมินแพลตฟอร์ม · ของร้าน ⇒ เจ้าของบริษัท (ฝ่ายค้าน S5)
        if (PaymentDisputeRules.NeedsAttention(dispute.State))
        {
            var title = $"ข้อพิพาทบัตร: {PaymentDisputeRules.Label(dispute.State)}";
            var body = $"รายการชำระ {intent.Amount:N2} บาท (อ้างอิง {intent.ProviderRef}) — {PaymentDisputeRules.NextStep(dispute.State)}";
            try
            {
                if (PlatformRevenueSource.IsPlatformRevenue(intent.SourceKind))
                    await PlatformPaymentAttention.NotifyAdminsAboutIntentAsync(_db, _notifications, title, body, intent.Id, AdminIntentsUrl, ct);
                else if (_notifications != null)
                    foreach (var owner in await _db.CompanyUsers.AsNoTracking()
                                 .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner).Select(cu => cu.UserId).Distinct().ToListAsync(ct))
                        await _notifications.SendAsync(owner, companyId, NotificationType.System, title, body, ShopIntentsUrl, nameof(PaymentIntent), intent.Id);
            }
            catch (Exception ex) { _logger.LogError(ex, "ส่งกระดิ่ง (ข้อพิพาท {Intent}) ไม่สำเร็จ — เรื่องอยู่บนรายการแล้ว", intent.Id); }
        }
        return true;
    }

    public async Task<bool> AcknowledgeAsync(Guid companyId, Guid intentId, string actor, string? note, CancellationToken ct = default)
    {
        var intent = await _db.PaymentIntents.FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);
        if (intent?.DisputeState == null) return false;
        intent.DisputeAcknowledgedAt = DateTime.UtcNow;
        intent.DisputeAcknowledgedBy = actor.Length > 200 ? actor[..200] : actor;
        intent.UpdatedAt = DateTime.UtcNow;
        _db.PaymentIntentEvents.Add(new PaymentIntentEvent
        {
            CompanyId = companyId, IntentId = intent.Id, At = DateTime.UtcNow, Source = PaymentEventSource.Manual,
            FromStatus = intent.Status, ToStatus = intent.Status,
            Note = $"รับทราบ/จัดการข้อพิพาท ({PaymentDisputeRules.Label(intent.DisputeState)}) โดย {actor}" + (string.IsNullOrWhiteSpace(note) ? "" : $" · {note.Trim()}"),
        });
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, EntityType = nameof(PaymentIntent), EntityId = intent.Id.ToString(), Action = AuditAction.Update,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "payment-dispute-acknowledge", ruleCode = PaymentDisputeRules.RuleCode, disputeState = intent.DisputeState.ToString(),
                intent.DisputeProviderRef, actor, note = note?.Trim(),
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

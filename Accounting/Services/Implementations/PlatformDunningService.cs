using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ผลประเมิน dunning ของบริษัทหนึ่ง (หน้าแอดมิน · เทสต์)</summary>
public sealed record DunningEvaluation(Guid CompanyId, DunningAction Action, string Reason, int RemindersSent, decimal OverdueAmount);

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 2 (G-7/G-8 · คำตัดสินข้อ 180) — <b>ทวงหนี้ค่าบริการ NextAcc + ระงับ/คืนสถานะ ต่อบริษัท</b> ตัวเดียวของทุกทางเข้า
/// (งาน <c>PlatformDunningJob</c> · หลังอนุมัติการชำระ · ปุ่มแอดมิน) — ตัดสินด้วย <see cref="PlatformDunningPolicy.Decide"/> (pure) · ส่งผ่าน
/// <see cref="IPlatformBillingNotifier"/> (คีย์กันซ้ำ) · ระงับ = <c>Subscription.Status = Suspended</c> (ด่านเดิม <c>SubscriptionGatePolicy</c>) เฉพาะโหมด Enforce ·
/// ทุกการระงับ/คืน/"จะระงับ"/พักการระงับ ลง <c>SubscriptionHistory</c> + audit chain
/// </summary>
public interface IPlatformDunningService
{
    Task<DunningEvaluation?> EvaluateCompanyAsync(Guid companyId, bool sendReminders, CancellationToken ct = default);
    /// <summary>แอดมินพักการระงับอัตโนมัติ N วัน (และคืนสถานะถ้าระบบระงับไว้) — เหตุผลบังคับ · audit</summary>
    Task<DunningEvaluation?> ExemptAsync(Guid companyId, int days, string reason, PlatformBillingActor actor, CancellationToken ct = default);
}

public class PlatformDunningService : IPlatformDunningService
{
    public const string RuleSuspend = "PLATFORM-DUNNING-SUSPEND", RuleRestore = "PLATFORM-DUNNING-RESTORE",
        RuleWouldSuspend = "PLATFORM-DUNNING-WOULD-SUSPEND", RuleExempt = "PLATFORM-DUNNING-EXEMPT";
    public const int MaxExemptDays = 90;

    private readonly AccountingDbContext _db;
    private readonly IPlatformBillingNotifier _notifier;
    private readonly ISubscriptionGateShadowLog _gate;
    private readonly IConfiguration _config;
    private readonly ILogger<PlatformDunningService> _logger;

    public PlatformDunningService(AccountingDbContext db, IPlatformBillingNotifier notifier, ISubscriptionGateShadowLog gate,
        IConfiguration config, ILogger<PlatformDunningService> logger)
    { _db = db; _notifier = notifier; _gate = gate; _config = config; _logger = logger; }

    public async Task<DunningEvaluation?> EvaluateCompanyAsync(Guid companyId, bool sendReminders, CancellationToken ct = default)
    {
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);
        if (platformId == null) return null;
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        if (sub == null) return null;
        var now = DateTime.UtcNow;
        var policy = await PlatformBillingSettingsService.LoadPolicyAsync(_db, ct);
        var mode = SubscriptionEnforcementResolver.Resolve(await _gate.ReadAdminSwitchAsync(ct), _config).EffectiveMode;

        var open = PlatformInvoiceRules.OpenStatuses;
        var rows = await PlatformInvoiceQueries.ForBuyer(_db, platformId.Value, companyId)
            .Where(d => open.Contains(d.Status) && d.BalanceDue > PlatformInvoiceRules.Epsilon)
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var pendingSlip = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.SubscriptionId == sub.Id && p.PlatformInvoiceDocumentId != null && ids.Contains(p.PlatformInvoiceDocumentId.Value)
                && p.SlipFileName != null
                && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview))
            .Select(p => p.PlatformInvoiceDocumentId!.Value).ToListAsync(ct);
        var invoices = rows.Select(r => new DunningInvoice(r.Id, r.DocumentNumber, r.DueDate, r.DocumentDate, r.BalanceDue,
            Excluded: pendingSlip.Contains(r.Id) || (sub.CancelAtPeriodEnd && sub.RenewalInvoiceDocumentId == r.Id))).ToList();

        var state = await _db.PlatformDunningStates.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        if (state == null) { state = new PlatformDunningState { CompanyId = companyId, CreatedBy = "system:dunning" }; _db.PlatformDunningStates.Add(state); }
        // ระบบเคยระงับ แต่สถานะถูกเปลี่ยนเป็นอย่างอื่นแล้ว (อนุมัติการต่ออายุ/แอดมินเปลี่ยนเอง) ⇒ ไม่ถือว่าระงับโดยระบบอีกต่อไป
        if (state.SuspendedByDunningAt != null && sub.Status != SubscriptionStatus.Suspended)
        { state.SuspendedByDunningAt = null; state.StatusBeforeSuspend = null; }

        var decision = PlatformDunningPolicy.Decide(now, invoices, policy, mode, sub.Status, state.SuspendedByDunningAt != null, state.SuspendExemptUntil);

        var sent = 0;
        if (sendReminders)
            foreach (var r in decision.Reminders)
            {
                var row = rows.First(x => x.Id == r.DocumentId);
                var (subject, body) = PlatformBillingMessages.For(r.Kind, r.Days, row);
                sent += await _notifier.SendAsync(companyId, r.DocumentId, r.MessageKey, subject, body, ct) > 0 ? 1 : 0;
            }

        switch (decision.Action)
        {
            case DunningAction.Suspend:
                state.StatusBeforeSuspend = sub.Status;
                state.SuspendedByDunningAt = now;
                Transition(sub, SubscriptionStatus.Suspended, "PlatformDunningSuspended", decision.Reason, RuleSuspend);
                state.WouldSuspendSince = null;
                await _notifier.SendAsync(companyId, null, PlatformDunningPolicy.CompanyKey(companyId, "suspended", now),
                    "ระงับการใช้งานชั่วคราว — ค้างชำระค่าบริการ NextAcc",
                    $"{decision.Reason}\nชำระที่ศูนย์บิลค่าบริการแล้วระบบจะเปิดใช้งานคืนเมื่อผู้ดูแลระบบยืนยันการชำระ", ct);
                break;
            case DunningAction.Restore:
                var to = PlatformDunningPolicy.RestoreStatus(state.StatusBeforeSuspend, sub.EndDate, now, sub.IsPermanentFree);
                Transition(sub, to, "PlatformDunningRestored", decision.Reason, RuleRestore);
                state.SuspendedByDunningAt = null; state.StatusBeforeSuspend = null;
                await _notifier.SendAsync(companyId, null, PlatformDunningPolicy.CompanyKey(companyId, "restored", now),
                    "เปิดใช้งานคืนแล้ว", "ได้รับชำระค่าบริการแล้ว — เปิดใช้งานตามปกติ", ct);
                break;
            case DunningAction.WouldSuspend:
                if (state.WouldSuspendSince == null)
                {
                    state.WouldSuspendSince = now;
                    Audit(sub, null, decision.Reason, RuleWouldSuspend, sub.Status, sub.Status);
                }
                break;
        }
        if (decision.Action != DunningAction.WouldSuspend && decision.Action != DunningAction.Suspend && decision.OverdueAmount <= 0m)
            state.WouldSuspendSince = null;
        state.LastEvaluatedAt = now;
        state.OverdueAmount = decision.OverdueAmount;
        state.OldestOverdueDueDate = decision.OldestOverdueDueUtc;
        state.LastAction = decision.Reason.Length > 300 ? decision.Reason[..300] : decision.Reason;
        state.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return new DunningEvaluation(companyId, decision.Action, decision.Reason, sent, decision.OverdueAmount);
    }

    public async Task<DunningEvaluation?> ExemptAsync(Guid companyId, int days, string reason, PlatformBillingActor actor, CancellationToken ct = default)
    {
        if (days < 1 || days > MaxExemptDays) throw new BusinessRuleException($"พักการระงับได้ 1–{MaxExemptDays} วัน");
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("กรุณาระบุเหตุผล");
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");
        var state = await _db.PlatformDunningStates.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        if (state == null) { state = new PlatformDunningState { CompanyId = companyId, CreatedBy = "admin" }; _db.PlatformDunningStates.Add(state); }
        var now = DateTime.UtcNow;
        state.SuspendExemptUntil = now.AddDays(days);
        var r = reason.Trim();
        if (r.Length > 300) r = r[..300];
        if (state.SuspendedByDunningAt != null && sub.Status == SubscriptionStatus.Suspended)
        {
            var to = PlatformDunningPolicy.RestoreStatus(state.StatusBeforeSuspend, sub.EndDate, now, sub.IsPermanentFree);
            Transition(sub, to, "PlatformDunningRestored", $"ผู้ดูแลระบบพักการระงับ {days} วัน: {r}", RuleExempt, actor);
            state.SuspendedByDunningAt = null; state.StatusBeforeSuspend = null;
        }
        else Audit(sub, actor, $"ผู้ดูแลระบบพักการระงับ {days} วัน: {r}", RuleExempt, sub.Status, sub.Status);
        state.WouldSuspendSince = null;
        state.LastAction = $"พักการระงับถึง {PlatformInvoiceRules.ThaiDate(state.SuspendExemptUntil.Value):dd/MM/yyyy}";
        state.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return new DunningEvaluation(companyId, DunningAction.None, state.LastAction, 0, state.OverdueAmount);
    }

    private void Transition(Subscription sub, SubscriptionStatus to, string action, string reason, string rule, PlatformBillingActor? actor = null)
    {
        var from = sub.Status;
        sub.Status = to;
        sub.UpdatedAt = DateTime.UtcNow;
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id, Action = action, FromStatus = from, ToStatus = to,
            Notes = reason.Length > 900 ? reason[..900] : reason, PerformedBy = actor?.UserId?.ToString() ?? "system:dunning",
        });
        Audit(sub, actor, reason, rule, from, to);
    }

    private void Audit(Subscription sub, PlatformBillingActor? actor, string reason, string rule, SubscriptionStatus from, SubscriptionStatus to)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = sub.CompanyId,
            UserId = actor?.UserId,
            UserEmail = actor?.Email ?? "system:dunning",
            Action = AuditAction.Update,
            EntityType = "Subscription",
            EntityId = sub.Id.ToString(),
            OldValues = JsonSerializer.Serialize(new { status = from.ToString() }),
            NewValues = JsonSerializer.Serialize(new { status = to.ToString(), reason, ruleCode = rule }),
            IpAddress = actor?.Ip,
            Timestamp = DateTime.UtcNow,
        });
}

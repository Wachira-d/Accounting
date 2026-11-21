using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>
/// **เส้นยืนยันรับเงินของรายการชำระ (สลิปโอน/ยืนยันมือ) — ตัวเดียว** (รอบ 282 แยกจาก <c>PaymentGatewayController.ConfirmManually</c> ทั้งก้อนโดยไม่เปลี่ยนพฤติกรรม)
/// <para>ผู้เรียก: ปุ่ม "ยืนยันด้วยมือ" (คน · <c>manual:{user}</c>) · <c>SlipAutoRecordService</c> (ระบบ · เมื่อ <see cref="SlipAutoRecordRule"/> ผ่านทุกด่าน) —
/// ลำดับเดียวกัน: ยอดสลิปไม่เกินยอดค้าง → วันรับเงินตามหลักฐาน (§78/1) → บัญชีที่เงินเข้า (<see cref="SlipReceivingAccount"/>) → ล็อกต่อรายการ →
/// ประทับบัญชี + audit → <c>ApplyChargeAsync</c> (ส่งต่อ <c>DocumentPaymentHandler</c> → <c>CreatePaymentAsync</c> ⇒ ใบเสร็จ/ใบกำกับ ณ วันรับเงิน 275-C · แจ้งคู่ค้า 281) ·
/// ไม่เปลี่ยนสถานะ ⇒ คืนค่าที่ประทับเดิม</para>
/// </summary>
public sealed class SlipIntentConfirmService
{
    private readonly AccountingDbContext _db;
    private readonly IPaymentIntentService _intents;

    public SlipIntentConfirmService(AccountingDbContext db, IPaymentIntentService intents) { _db = db; _intents = intents; }

    public enum Status { Confirmed, NotFound, BadRequest, Conflict, Busy, AlreadyClosed, NotApplied }

    public sealed record Outcome(Status Status, PaymentIntent? Intent, string? Message)
    {
        public bool Ok => Status == Status.Confirmed;
    }

    /// <summary>คีย์ล็อกต่อรายการของ "ยืนยัน" · "ปฏิเสธสลิป" · "ส่งต่อใหม่พร้อมบัญชีใหม่" (ตัวเดียว — รอบ 260-2 ฝ่ายค้าน L1)</summary>
    public static string ConfirmLockPart(Guid intentId) => $"{intentId:N}:confirm";

    /// <param name="receivedDate">วันรับเงินจริง (วันไทย) · null = วันที่แนบสลิป</param>
    /// <param name="bankAccountId">บัญชีที่เงินเข้า · null = ค่าแนะนำของเซิร์ฟเวอร์</param>
    /// <param name="actor">ผู้ยืนยัน (<c>manual:{user}</c> / <c>slip-auto:…</c>) — ลงประวัติ</param>
    public async Task<Outcome> ConfirmAsync(Guid companyId, PaymentIntent intent, DateTime? receivedDate, Guid? bankAccountId, Guid actorUserId,
        string actor, string reason, string? attachmentsRoot, CancellationToken ct = default, PaymentEventSource eventSource = PaymentEventSource.Manual)
    {
        var intentId = intent.Id;
        DateTime? slipPaidAt = null;
        if (DocumentSlipIntents.IsDocumentSlip(intent.SourceKind, intent.MethodKind))
        {
            var balance = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == intent.SourceId && d.CompanyId == companyId && !d.IsDeleted)
                .Select(d => (decimal?)d.BalanceDue)
                .FirstOrDefaultAsync(ct);
            if (balance == null)
                return new(Status.NotFound, null, "ไม่พบเอกสารของสลิปนี้ — ปฏิเสธสลิปแล้วบันทึกรับชำระที่เอกสารที่ถูกต้อง");
            if (DocumentSlipIntents.ExceedsBalance(intent.Amount, balance.Value))
                return new(Status.Conflict, null, DocumentSlipIntents.ConfirmBlockedMessage(intent.Amount, balance.Value));
            var (paidAt, dateProblem) = DocumentSlipIntents.ReceivedDate(receivedDate, intent.CreatedAt, DateTime.UtcNow);
            if (dateProblem != null) return new(Status.BadRequest, null, dateProblem);
            slipPaidAt = paidAt;
        }

        var chosenId = bankAccountId is Guid cb && cb != Guid.Empty ? cb : (Guid?)null;
        var receiving = await SlipReceivingAccount.ResolveAsync(_db, attachmentsRoot, intent, chosenId, ct);
        if (!receiving.Decision.Ok) return new(Status.BadRequest, null, receiving.Decision.Problem);

        PaymentIntent? updated = null;
        var alreadyClosed = false;
        string? lockProblem = null;
        (Guid? Bank, string? Origin)? previousStamp = null;
        // ประทับบัญชี + ยืนยัน ภายใต้ล็อกต่อรายการ — สองคนกดพร้อมกันคนละบัญชี ⇒ ค่าที่ประทับ = บัญชีที่ JE ใช้จริงเสมอ
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.PaymentIntent, ConfirmLockPart(intentId), async () =>
        {
            var current = await _db.PaymentIntents.AsNoTracking().Where(i => i.Id == intentId && i.CompanyId == companyId)
                .Select(i => new { i.Status, i.ReceivingBankAccountId, i.ReceivingAccountOrigin }).FirstAsync(ct);
            if (!PaymentIntentPolicy.Evaluate(current.Status, PaymentIntentStatus.Succeeded).Apply) { alreadyClosed = true; return; }
            if (receiving.Applies)
            {
                var final = await SlipReceivingAccount.ResolveAsync(_db, attachmentsRoot, intent, chosenId, ct);
                if (!final.Decision.Ok) { lockProblem = final.Decision.Problem; return; }
                previousStamp = (current.ReceivingBankAccountId, current.ReceivingAccountOrigin);
                await StampReceivingAccountAsync(companyId, intentId, final.Decision, chosenId, actorUserId, actor, "SlipReceivingAccountStamped", ct);
            }
            updated = await _intents.ApplyChargeAsync(intentId,
                new ProviderCharge(
                    ProviderRef: intent.ProviderRef ?? $"manual:{intentId:N}",
                    Status: PaymentIntentStatus.Succeeded,
                    RawStatus: eventSource == PaymentEventSource.Manual ? "manual_confirm" : "slip_auto_confirm",
                    Amount: intent.Amount,
                    PaidAtUtc: slipPaidAt),
                eventSource, $"{actor} · {reason.Trim()}", ct);
            if (updated.Status != PaymentIntentStatus.Succeeded && previousStamp is { } prev)
                await RevertReceivingAccountAsync(companyId, intentId, prev.Bank, prev.Origin, actorUserId, actor, updated.Status, ct);
        }, null, companyId, ct);
        if (!ran) return new(Status.Busy, null, "กำลังมีผู้ยืนยัน/ปฏิเสธรายการนี้อยู่ — รอสักครู่แล้วโหลดหน้าใหม่");
        if (lockProblem != null) return new(Status.BadRequest, null, lockProblem);
        if (alreadyClosed || updated == null)
            return new(Status.AlreadyClosed, null, "รายการนี้รับเงินแล้วหรือคืนเงินแล้ว (อาจมีผู้ยืนยันไปก่อน) — ยืนยันซ้ำไม่ได้ · โหลดหน้าใหม่เพื่อดูบัญชีที่ใช้");
        if (updated.Status != PaymentIntentStatus.Succeeded)
            return new(Status.NotApplied, updated,
                "ระบบไม่ได้เปลี่ยนรายการนี้เป็นรับเงินแล้ว — ดูเหตุในปุ่ม “ประวัติ” ของรายการ (เช่น โหมดทดสอบ/ใช้งานจริงไม่ตรงกัน)");
        return new(Status.Confirmed, updated, "ยืนยันการรับเงินแล้ว — ระบบดำเนินการต่อให้ต้นทางเรียบร้อย");
    }

    /// <summary>รอบ 260-2 (ฝ่ายค้าน L3): ประทับบัญชีที่เงินเข้า + audit ในธุรกรรมเดียว (UserId · RuleCode · LegalReference · เลือก vs แนะนำ)</summary>
    public async Task StampReceivingAccountAsync(Guid companyId, Guid intentId, SlipReceivingAccount.Decision d, Guid? chosen,
        Guid actorUserId, string actor, string eventName, CancellationToken ct)
    {
        var originName = d.Origin.ToString();
        var usedBank = d.BankAccountId;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.PaymentIntents.Where(i => i.Id == intentId && i.CompanyId == companyId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ReceivingBankAccountId, usedBank)
                .SetProperty(i => i.ReceivingAccountOrigin, originName), ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = actorUserId == Guid.Empty ? null : actorUserId, UserEmail = actor, Action = AuditAction.Update,
            EntityType = "PaymentIntent", EntityId = intentId.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = eventName,
                chosenBankAccountId = chosen,
                usedBankAccountId = d.BankAccountId,
                origin = originName,
                acceptedSuggestion = d.AcceptedSuggestion,
                suggestedBankAccountId = d.Suggested.DisplayBankAccountId ?? d.Suggested.BankAccountId,
                suggestedOrigin = d.Suggested.Origin.ToString(),
                suggestedReason = d.Suggested.Reason,
                overridden = d.Overridden,
                ruleCode = SlipReceivingAccount.RuleCode,
                legalReference = SlipReceivingAccount.LegalReference,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>ยืนยันไม่เปลี่ยนสถานะ ⇒ คืนค่าที่ประทับก่อนหน้า + audit ในธุรกรรมเดียว (ค่าที่ echo ต้องไม่บอกบัญชีที่ไม่เคยถูกใช้)</summary>
    public async Task RevertReceivingAccountAsync(Guid companyId, Guid intentId, Guid? bank, string? origin, Guid actorUserId, string actor,
        PaymentIntentStatus statusAfter, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.PaymentIntents.Where(i => i.Id == intentId && i.CompanyId == companyId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.ReceivingBankAccountId, bank).SetProperty(i => i.ReceivingAccountOrigin, origin), ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = actorUserId == Guid.Empty ? null : actorUserId, UserEmail = actor, Action = AuditAction.Update,
            EntityType = "PaymentIntent", EntityId = intentId.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "SlipReceivingAccountConfirmNotApplied", statusAfter = statusAfter.ToString(), restoredBankAccountId = bank, restoredOrigin = origin,
                ruleCode = SlipReceivingAccount.RuleCode, legalReference = SlipReceivingAccount.LegalReference,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}

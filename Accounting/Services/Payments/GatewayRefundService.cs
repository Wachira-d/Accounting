using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Journal;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>ผลการคืนเงินผ่านผู้ให้บริการ 1 ครั้ง</summary>
public sealed record GatewayRefundOutcome(
    bool Ok,
    string Message,
    string? RefundRef,
    decimal Amount,
    bool IsFullRefund,
    decimal RefundedTotal,
    Guid? JournalEntryId,
    string? JournalEntryNumber,
    string CreditNoteState,
    string? NextStep);

/// <summary>สถานะ "คืนเงินแล้ว ออกใบลดหนี้แล้วหรือยัง" ของรายการชำระเงินหนึ่ง (หน้า payment-intents)</summary>
public sealed record GatewayRefundCreditNoteView(Guid IntentId, decimal RefundedAmount, string State,
    bool NeedsCreditNote, decimal CreditNotesTotal);

public interface IGatewayRefundService
{
    /// <summary>คืนเงินผ่านผู้ให้บริการ + ลง JE คืนเงิน (Dr ลูกหนี้การค้า / Cr บัญชีพัก) + บันทึกยอดคืนสะสม</summary>
    Task<GatewayRefundOutcome> RefundAsync(Guid companyId, Guid intentId, decimal? amount, string reason,
        string actor, CancellationToken ct = default);

    /// <summary>สถานะใบลดหนี้ของรายการที่คืนเงินแล้ว — ตัวเดียวที่หน้ารายการใช้ติดป้าย</summary>
    Task<IReadOnlyDictionary<Guid, GatewayRefundCreditNoteView>> CreditNoteStatesAsync(Guid companyId,
        IReadOnlyCollection<Guid> intentIds, CancellationToken ct = default);
}

/// <summary>
/// **คืนเงินผ่าน payment gateway — ต้องมี JE เสมอ** (รอบ 198 G-1)
///
/// <para>═══ ที่มา ═══ endpoint เดิมเรียกผู้ให้บริการคืนเงินจริงแล้วเปลี่ยนสถานะอย่างเดียว ⇒ ไม่มีรายการบัญชี ·
/// 11340 ยังถือยอดเต็ม (ยอดที่จะถูกโอนมาน้อยกว่าที่บัญชีบอก) · ใบลดหนี้ที่ผู้ใช้ออกตามคำแนะนำ Cr ลูกหนี้ที่ล้างไปแล้ว ⇒
/// ลูกหนี้ติดลบ · และคืนบางส่วนหลายครั้งรวมเกินยอดรับได้ เพราะเทียบแค่ยอดครั้งนี้</para>
///
/// <para>═══ ลำดับ (ตั้งใจ) ═══ ตรวจทุกอย่างที่ตรวจได้<b>ก่อน</b>เรียกผู้ให้บริการ (ยอด · ผังลูกหนี้ · บัญชีพัก · งวดปิด)
/// เพราะเงินที่คืนไปแล้วเรียกกลับไม่ได้ · ล็อกต่อรายการตลอดเส้น (กันสองคนกดคืนพร้อมกันแล้วรวมเกินยอด) ·
/// ถ้าผู้ให้บริการคืนสำเร็จแต่ลงบัญชีไม่สำเร็จ = <b>ล้มดัง</b> (ประวัติรายการ + สถานะจริง + ข้อความถึงผู้ใช้)
/// ห้ามกลืน — เงินออกไปแล้วจริง</para>
/// </summary>
public class GatewayRefundService : IGatewayRefundService
{
    private readonly AccountingDbContext _db;
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly IPaymentIntentService _intents;
    private readonly IGatewayAccountResolver _accounts;
    private readonly ILogger<GatewayRefundService> _logger;

    public GatewayRefundService(AccountingDbContext db, IEnumerable<IPaymentProvider> providers,
        IPaymentIntentService intents, IGatewayAccountResolver accounts, ILogger<GatewayRefundService> logger)
    { _db = db; _providers = providers; _intents = intents; _accounts = accounts; _logger = logger; }

    public const string NextStepText =
        "เปิดเอกสารต้นทางแล้วออก \"ใบลดหนี้\" (§86/10) เพื่อลดภาษีขาย — ระบบลงบัญชีคืนเงินแล้ว "
        + "(Dr ลูกหนี้การค้า / Cr บัญชีพักผู้ให้บริการ) และตั้งยอดรอใบลดหนี้ไว้ที่ลูกหนี้ · ใบลดหนี้จะล้างยอดนี้ · "
        + "ระบบไม่ออกให้อัตโนมัติเพราะใบลดหนี้ต้องระบุเหตุผลตามที่กฎหมายกำหนดและยอดสะสมห้ามเกินใบเดิม ซึ่งต้องให้คนตัดสิน";

    public async Task<GatewayRefundOutcome> RefundAsync(Guid companyId, Guid intentId, decimal? amount,
        string reason, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Fail("กรุณาระบุเหตุผลการคืนเงิน");

        var (outcome, statusToApply, refundRef) = await RefundCoreAsync(companyId, intentId, amount, reason, actor, ct);

        // สถานะเดินผ่านเครื่องสถานะตัวเดียวของระบบ (ที่เดียวที่เขียน PaymentIntent.Status) —
        // สะท้อนความจริงว่าเงินออกแล้ว แม้ลงบัญชีไม่สำเร็จ (ยอดคืนสะสม = 0 ⇒ ถูกแยกเป็น "ต้องตรวจมือ")
        if (statusToApply is PaymentIntentStatus st)
        {
            var intentNow = await _intents.FindAsync(companyId, intentId, ct);
            if (intentNow != null)
                await _intents.ApplyChargeAsync(intentId,
                    new ProviderCharge(intentNow.ProviderRef ?? string.Empty, st, "refunded", intentNow.Amount),
                    PaymentEventSource.Manual, $"manual:{actor} · คืนเงิน {outcome.Amount:N2} ({refundRef})", ct);
        }

        if (outcome.Ok)
        {
            var states = await CreditNoteStatesAsync(companyId, new[] { intentId }, ct);
            if (states.TryGetValue(intentId, out var cn))
                outcome = outcome with { CreditNoteState = cn.State };
        }
        return outcome;
    }

    /// <summary>ผลของเส้นธุรกรรม + สถานะที่ต้องเดินต่อ (null = ไม่มีเงินออก ไม่ต้องเปลี่ยนสถานะ)</summary>
    private sealed record RefundStep(GatewayRefundOutcome Outcome, PaymentIntentStatus? StatusToApply, string? RefundRef);

    /// <summary>เส้นธุรกรรมเดียว: ล็อก → ตรวจทุกอย่างก่อนเงินออก → เรียกผู้ให้บริการ → JE + ยอดคืน (atomic)
    /// · <b>ไม่ใช้ execution strategy แบบ retry</b> — การรันซ้ำหลังผู้ให้บริการคืนเงินแล้ว = คืนซ้ำ</summary>
    private async Task<RefundStep> RefundCoreAsync(Guid companyId, Guid intentId, decimal? amount, string reason,
        string actor, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // ล็อก "การคืนเงิน" ของรายการนี้ตลอดเส้น — คนละคีย์กับล็อกสถานะของ ApplyChargeAsync
        // (ที่เรียกหลัง commit) จึงไม่ติดตัวเอง
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.PaymentIntent,
                $"refund:{intentId:N}") }, ct);

        var intent = await _db.PaymentIntents
            .FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);
        if (intent == null) return new RefundStep(Fail("ไม่พบรายการชำระเงิน"), null, null);

        var refundedBefore = intent.RefundedAmount;
        var check = GatewayRefundMath.Check(intent.Status, intent.Amount, refundedBefore, amount);
        if (!check.Ok) return new RefundStep(Fail(check.Message ?? "คืนเงินไม่ได้"), null, null);

        var provider = _providers.FirstOrDefault(p => p.ProviderCode == intent.ProviderCode);
        if (provider == null)
            return new RefundStep(Fail($"ไม่รู้จักช่องทางชำระเงิน \"{intent.ProviderCode}\""), null, null);
        if (!provider.Capabilities.SupportsRefund)
            return new RefundStep(Fail("ช่องทางนี้คืนเงินผ่านระบบไม่ได้ — ต้องคืนที่ธนาคาร/แดชบอร์ดของผู้ให้บริการเอง "
                + "แล้วออกใบลดหนี้ในระบบ (§86/10)"), null, null);

        // ── ตรวจก่อนเงินออก: บัญชีพัก · ผังลูกหนี้ · งวด ──
        var clearingId = await _accounts.ResolveMoneyInAccountAsync(intent, ct);
        if (clearingId is not Guid clearing)
            return new RefundStep(Fail("หาบัญชีพักของผู้ให้บริการไม่เจอ (แนะนำ 11340 ลูกหนี้ผู้ให้บริการรับชำระเงิน) — "
                + "ตั้งที่หน้า \"ตั้งค่าการรับชำระเงินออนไลน์\" ก่อนคืนเงิน (ยังไม่มีเงินออก)"), null, null);

        var contactPin = await ContactArPinAsync(companyId, intent, ct);
        var ar = await TradeReceivableAccount.ResolveAsync(_db, companyId, contactPin, ct);
        if (ar == null)
            return new RefundStep(Fail($"ไม่พบผังบัญชี {TradeReceivableAccount.StandardCode} ลูกหนี้การค้า "
                + "(ที่ตั้งยอดรอใบลดหนี้) — เพิ่มในผังบัญชีก่อนคืนเงิน (ยังไม่มีเงินออก)"), null, null);

        var entryDate = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var closed = await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, entryDate, ct);
        if (closed != null) return new RefundStep(Fail(closed + " (ยังไม่มีเงินออก)"), null, null);

        var config = intent.ProviderConfigId is Guid cid
            ? await _db.PaymentProviderConfigs.FirstOrDefaultAsync(c => c.Id == cid && c.CompanyId == companyId, ct)
            : null;

        // ── เงินออกจริง ──
        var result = await provider.RefundAsync(intent, check.Amount, reason.Trim(),
            config ?? new PaymentProviderConfig { CompanyId = companyId, ProviderCode = intent.ProviderCode }, ct);
        if (!result.Succeeded)
            return new RefundStep(Fail(result.FailureMessage ?? "ผู้ให้บริการปฏิเสธการคืนเงิน"), null, null);

        var newStatus = check.IsFullRefund ? PaymentIntentStatus.Refunded : PaymentIntentStatus.PartiallyRefunded;
        var fromStatus = intent.Status;
        try
        {
            var je = await JournalEntryBuilder.For(_db, companyId, entryDate)
                .Type(JournalType.CashPayments)
                .NumberPrefix("PV")
                .Description($"คืนเงินลูกค้าผ่านผู้ให้บริการรับชำระเงิน ({intent.ProviderCode}) {result.ProviderRefundRef}")
                .Reference(result.ProviderRefundRef)
                .CreatedBy(actor)
                .Debit(ar.Id, check.Amount, $"ตั้งลูกหนี้รอใบลดหนี้ — คืนเงิน {intent.SourceKind} ({reason.Trim()})")
                .Credit(clearing, check.Amount, $"คืนเงินจากยอดที่ผู้ให้บริการถือไว้ {result.ProviderRefundRef}")
                .PostAsync(actor, ct);

            intent.RefundedAmount = check.NewRefundedTotal;
            intent.LastRefundedAt = DateTime.UtcNow;
            intent.LastRefundJournalEntryId = je.Id;
            intent.UpdatedAt = DateTime.UtcNow;
            _db.PaymentIntentEvents.Add(new PaymentIntentEvent
            {
                CompanyId = companyId,
                IntentId = intent.Id,
                At = DateTime.UtcNow,
                Source = PaymentEventSource.Manual,
                FromStatus = fromStatus,
                ToStatus = newStatus,
                Note = $"คืนเงิน {check.Amount:N2} (สะสม {check.NewRefundedTotal:N2}) · อ้างอิง {result.ProviderRefundRef} · "
                     + $"JE {je.EntryNumber} โดย {actor} · {reason.Trim()}",
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new RefundStep(new GatewayRefundOutcome(true,
                    $"คืนเงิน {check.Amount:N2} บาทเรียบร้อย — ลงบัญชีใบสำคัญ {je.EntryNumber}",
                    result.ProviderRefundRef, check.Amount, check.IsFullRefund, check.NewRefundedTotal,
                    je.Id, je.EntryNumber, "", NextStepText),
                newStatus, result.ProviderRefundRef);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            // เงินออกไปแล้วจริง แต่ลงบัญชีไม่สำเร็จ — ห้ามกลืน: บันทึกประวัติ + สถานะจริง + ข้อความถึงผู้ใช้
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            _logger.LogError(ex, "คืนเงินที่ผู้ให้บริการสำเร็จแต่ลง JE ไม่สำเร็จ — intent {Intent} ยอด {Amount:N2} ref {Ref}",
                intentId, check.Amount, result.ProviderRefundRef);
            _db.PaymentIntentEvents.Add(new PaymentIntentEvent
            {
                CompanyId = companyId,
                IntentId = intentId,
                At = DateTime.UtcNow,
                Source = PaymentEventSource.System,
                FromStatus = fromStatus,
                ToStatus = newStatus,
                Note = $"⚠️ คืนเงินที่ผู้ให้บริการสำเร็จ {check.Amount:N2} (อ้างอิง {result.ProviderRefundRef}) "
                     + $"แต่ลงบัญชีไม่สำเร็จ: {ex.Message} — ต้องบันทึกรายการบัญชีคืนเงินด้วยมือ",
            });
            await _db.SaveChangesAsync(ct);
            return new RefundStep(new GatewayRefundOutcome(false,
                    $"คืนเงินที่ผู้ให้บริการสำเร็จแล้ว ({check.Amount:N2} บาท · อ้างอิง {result.ProviderRefundRef}) "
                    + $"แต่ลงบัญชีไม่สำเร็จ: {ex.Message} — ห้ามกดคืนซ้ำ · ต้องบันทึกรายการบัญชีคืนเงิน "
                    + "(Dr ลูกหนี้การค้า / Cr บัญชีพักผู้ให้บริการ) ด้วยมือ แล้วออกใบลดหนี้ที่เอกสารต้นทาง",
                    result.ProviderRefundRef, check.Amount, check.IsFullRefund, refundedBefore,
                    null, null, "", NextStepText),
                newStatus, result.ProviderRefundRef);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, GatewayRefundCreditNoteView>> CreditNoteStatesAsync(Guid companyId,
        IReadOnlyCollection<Guid> intentIds, CancellationToken ct = default)
    {
        var rows = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && intentIds.Contains(i.Id) && i.RefundedAmount > 0m)
            .Select(i => new { i.Id, i.SourceKind, i.SourceId, i.RefundedAmount })
            .ToListAsync(ct);

        // ต้นทางที่เป็นเอกสารในระบบเท่านั้นที่ใบลดหนี้อ้างถึงได้ — อย่างอื่น "ตรวจย้อนไม่ได้" (ไม่ใช่ "ครบ")
        var docIds = rows.Where(r => r.SourceKind == PaymentSourceKind.Document).Select(r => r.SourceId).Distinct().ToList();
        var cnTotals = docIds.Count == 0
            ? new Dictionary<Guid, decimal>()
            : await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.DocumentType == DocumentType.CreditNote
                    && d.RelatedDocumentId != null && docIds.Contains(d.RelatedDocumentId.Value)
                    && !DocumentStatusRules.NotIssued.Contains(d.Status)
                    && d.Status != DocumentStatus.Voided)
                .GroupBy(d => d.RelatedDocumentId!.Value)
                .Select(g => new { DocId = g.Key, Total = g.Sum(d => d.TotalAmount) })
                .ToDictionaryAsync(x => x.DocId, x => x.Total, ct);

        var result = new Dictionary<Guid, GatewayRefundCreditNoteView>();
        foreach (var r in rows)
        {
            var traceable = r.SourceKind == PaymentSourceKind.Document;
            var total = traceable && cnTotals.TryGetValue(r.SourceId, out var t) ? t : 0m;
            var state = GatewayRefundMath.CreditNoteState(r.RefundedAmount, traceable, total);
            result[r.Id] = new GatewayRefundCreditNoteView(r.Id, r.RefundedAmount, state.ToString(),
                GatewayRefundMath.NeedsCreditNote(state), total);
        }
        return result;
    }

    /// <summary>ผังลูกหนี้ที่ผู้ใช้ปักไว้บนลูกค้าของรายการนี้ (ถ้ามี) — เอกสารต้นทางชนะ intent</summary>
    private async Task<Guid?> ContactArPinAsync(Guid companyId, PaymentIntent intent, CancellationToken ct)
    {
        Guid? contactId = intent.ContactId;
        if (intent.SourceKind == PaymentSourceKind.Document)
        {
            var docContact = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == intent.SourceId && d.CompanyId == companyId)
                .Select(d => (Guid?)d.ContactId)
                .FirstOrDefaultAsync(ct);
            if (docContact is Guid dc && dc != Guid.Empty) contactId = dc;
        }
        if (contactId is not Guid id || id == Guid.Empty) return null;
        return await _db.Contacts.AsNoTracking()
            .Where(c => c.Id == id && c.CompanyId == companyId)
            .Select(c => c.DefaultArAccountId)
            .FirstOrDefaultAsync(ct);
    }

    private static GatewayRefundOutcome Fail(string message)
        => new(false, message, null, 0m, false, 0m, null, null, "", null);
}

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

    /// <summary>ตรวจผลการคืนเงินที่ "ผลไม่แน่ชัด" กับยอดคืนสะสมของผู้ให้บริการ (ฝ่ายค้าน E-2) — ไม่ได้เกิด ⇒ ปลดล็อก ·
    /// เกิดแล้ว ⇒ ลงบัญชีส่วนต่าง (เส้นเดียวกับคืนเงินปกติ) + ปลดล็อก · ไม่รู้/ขัดกัน ⇒ ล็อกต่อ</summary>
    Task<GatewayRefundOutcome> VerifyUnknownRefundAsync(Guid companyId, Guid intentId, string actor,
        CancellationToken ct = default);

    /// <summary>บันทึกผลด้วยมือของการคืนเงินที่ผลไม่แน่ชัด (review198-E2 E2-3 — ทางไปต่อเมื่อผู้ให้บริการเงียบ/ข้อมูลขัดกัน) ·
    /// เจ้าของกิจการตัดสินจากแดชบอร์ดผู้ให้บริการ + หลักฐาน · เงินออก ⇒ ลงบัญชีคืนเงิน (เส้นเดียวกับคืนเงินปกติ) · hash chain</summary>
    Task<GatewayRefundOutcome> ResolveUnknownRefundManuallyAsync(Guid companyId, Guid intentId,
        GatewayRefundManualDecision decision, decimal? amount, string? providerRefundRef, string? evidence,
        string actor, string? actorEmail, string? ipAddress, CancellationToken ct = default);

    /// <summary>บันทึกยอดคืนจริงย้อนหลังของรายการที่สถานะคืนแล้วแต่ระบบไม่มียอดคืน (รอบ 201 ทีม GW · A-GW7) — เจ้าของกิจการ + หลักฐาน ·
    /// เลือกลงใบสำคัญคืนเงิน (เส้นเดียวกับคืนเงินปกติ) หรือบันทึกเฉพาะยอด (ลงด้วยมือไว้แล้ว) · hash chain · ไม่ migrate</summary>
    Task<GatewayRefundOutcome> RecordLegacyRefundAsync(Guid companyId, Guid intentId, decimal? amount, DateTime? refundedAtUtc,
        string? providerRefundRef, string? evidence, GatewayLegacyRefundJournal journal,
        string actor, string? actorEmail, string? ipAddress, CancellationToken ct = default);
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
///
/// <para>═══ ผลไม่แน่ชัด (ฝ่ายค้าน E-2) ═══ ผู้ให้บริการ<b>โยน</b> (หมดเวลา/เครือข่ายหลุด/ผู้ใช้ยกเลิกคำขอ) = ไม่รู้ว่าเงินออกหรือยัง ·
/// เดิม tx rollback เงียบแล้วกดใหม่ได้ ⇒ คืนซ้ำ · ตอนนี้: บันทึกเหตุการณ์ "⚠️ ผลไม่แน่ชัด" + ประทับ
/// <c>PaymentIntent.RefundOutcomeUnknownSince</c> ⇒ <see cref="GatewayRefundMath.Check"/> ปฏิเสธการคืนเพิ่มจนกว่าจะ
/// <see cref="VerifyUnknownRefundAsync"/> (อ่านยอดคืนสะสมจากผู้ให้บริการ — <b>ไม่ประทับผลเอง</b>)</para>
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
        // เงินออกแล้ว ⇒ สถานะต้องถูกบันทึกแม้ผู้ใช้ยกเลิกคำขอ (ไม่งั้นสถานะยัง Succeeded แล้วกดคืนซ้ำได้) — ใช้ CancellationToken.None
        if (statusToApply is PaymentIntentStatus st)
        {
            var intentNow = await _intents.FindAsync(companyId, intentId, CancellationToken.None);
            if (intentNow != null)
                await _intents.ApplyChargeAsync(intentId,
                    new ProviderCharge(intentNow.ProviderRef ?? string.Empty, st, "refunded", intentNow.Amount),
                    PaymentEventSource.Manual, $"manual:{actor} · คืนเงิน {outcome.Amount:N2} ({refundRef})",
                    CancellationToken.None);
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
        var check = GatewayRefundMath.Check(intent.Status, intent.Amount, refundedBefore, amount,
            intent.RefundOutcomeUnknownSince != null);
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
        // E2-2: คำขอเงินออก**ไม่ผูกกับการยกเลิกของผู้ใช้** (CancellationToken.None) — ปิดหน้า/หมดเวลาฝั่งเบราว์เซอร์ต้องไม่ตัดคำขอกลางทาง
        // (ตัดกลางทาง = ผู้ให้บริการอาจยังทำต่อ แต่เราไม่รู้ผล) · หมดเวลาของ HttpClient เองยังทำงาน ⇒ ไปทาง "ผลไม่แน่ชัด"
        // · เครื่องหมายของครั้งนี้แนบไปกับคำขอ ให้การตรวจผลหา "ครั้งนี้" ในรายการคืนของผู้ให้บริการได้
        ProviderRefund result;
        var attemptAt = DateTime.UtcNow;
        var attemptMarker = Guid.NewGuid().ToString("N");
        string? unknownError = null;
        try
        {
            result = await provider.RefundAsync(intent, check.Amount, reason.Trim(), attemptMarker,
                config ?? new PaymentProviderConfig { CompanyId = companyId, ProviderCode = intent.ProviderCode },
                CancellationToken.None);
            // E2-1: ผู้ให้บริการตอบ 5xx/408 = ไม่รู้ผล (ไม่ใช่ "ปฏิเสธ") ⇒ ล็อกเหมือนหมดเวลา
            if (result.OutcomeUnknown) unknownError = result.FailureMessage ?? "ผู้ให้บริการตอบผิดพลาด";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "คืนเงินผ่านผู้ให้บริการผลไม่แน่ชัด — intent {Intent} ยอด {Amount:N2}", intentId, check.Amount);
            result = new ProviderRefund("", check.Amount, false, ex.Message, OutcomeUnknown: true);
            unknownError = ex.Message;
        }
        if (unknownError != null)
        {
            // E-2/E2-1: ไม่ตอบ/หมดเวลา/5xx = ไม่รู้ว่าเงินออกหรือยัง — ห้าม rollback เงียบ (กดใหม่ = คืนซ้ำได้) ·
            // ประทับ "ผลไม่แน่ชัด" + เหตุการณ์ใน**ธุรกรรมเดิมที่ยังถือล็อก** (ก่อนหน้านี้ยังไม่มีอะไรถูกเขียน) แล้ว commit ⇒
            // คำขอคืนครั้งถัดไปที่รอล็อกอยู่เห็นธงทันที · งานเขียนใช้ CancellationToken.None (ผู้ใช้ปิดหน้าไม่ทำให้ธงหาย)
            MarkOutcomeUnknown(companyId, intent, attemptAt, check.Amount, attemptMarker, actor, reason.Trim(), unknownError);
            await _db.SaveChangesAsync(CancellationToken.None);
            await tx.CommitAsync(CancellationToken.None);
            return new RefundStep(Fail(
                $"ผู้ให้บริการไม่ตอบผลการคืนเงิน {check.Amount:N2} บาท ({unknownError}) — ผลไม่แน่ชัด: เงินอาจออกไปแล้ว · "
                + "ห้ามกดคืนซ้ำ · ระบบล็อกการคืนเงินของรายการนี้ไว้ — กด \"ตรวจผลการคืนเงิน\" ที่หน้ารายการรับชำระออนไลน์ "
                + $"(หลัง {GatewayRefundMath.MinVerifyWait.TotalMinutes:0} นาทีถ้ายอดยังไม่ขยับ · ระบบจะถามยอดคืนสะสมจากผู้ให้บริการ "
                + "แล้วลงบัญชี/ปลดล็อกให้ตามผลจริง)"), null, null);
        }
        if (!result.Succeeded)
            return new RefundStep(Fail(result.FailureMessage ?? "ผู้ให้บริการปฏิเสธการคืนเงิน"), null, null);

        var newStatus = check.IsFullRefund ? PaymentIntentStatus.Refunded : PaymentIntentStatus.PartiallyRefunded;
        var fromStatus = intent.Status;
        try
        {
            var je = await BookRefundAsync(companyId, intent, ar.Id, clearing, entryDate, check.Amount, check.NewRefundedTotal,
                result.ProviderRefundRef, reason.Trim(), actor, fromStatus, newStatus, DateTime.UtcNow, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new RefundStep(new GatewayRefundOutcome(true,
                    $"คืนเงิน {check.Amount:N2} บาทเรียบร้อย — ลงบัญชีใบสำคัญ {je.EntryNumber}",
                    result.ProviderRefundRef, check.Amount, check.IsFullRefund, check.NewRefundedTotal,
                    je.Id, je.EntryNumber, "", NextStepText),
                newStatus, result.ProviderRefundRef);
        }
        catch (Exception ex)
        {
            // เงินออกไปแล้วจริง แต่ลงบัญชีไม่สำเร็จ — ห้ามกลืน: บันทึกประวัติ + สถานะจริง + ข้อความถึงผู้ใช้
            // จับ**ทุก** exception (ไม่ใช่แค่ InvalidOperation/DbUpdate): เครือข่าย DB หลุด · คำขอถูกยกเลิก ฯลฯ หลังเงินออก
            // ถ้าหลุดออกไป = ไม่มีประวัติ · สถานะยัง Succeeded · ยอดคืนสะสมไม่ขยับ ⇒ กดคืนซ้ำได้ = เงินออกสองรอบ ·
            // งานเขียนกู้คืนใช้ CancellationToken.None — ผู้ใช้ปิดหน้าไม่ได้ทำให้เงินที่ออกไปแล้วหายจากบันทึก
            await tx.RollbackAsync(CancellationToken.None);
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
            await _db.SaveChangesAsync(CancellationToken.None);
            return new RefundStep(new GatewayRefundOutcome(false,
                    $"คืนเงินที่ผู้ให้บริการสำเร็จแล้ว ({check.Amount:N2} บาท · อ้างอิง {result.ProviderRefundRef}) "
                    + $"แต่ลงบัญชีไม่สำเร็จ: {ex.Message} — ห้ามกดคืนซ้ำ · ต้องบันทึกรายการบัญชีคืนเงิน "
                    + "(Dr ลูกหนี้การค้า / Cr บัญชีพักผู้ให้บริการ) ด้วยมือ แล้วออกใบลดหนี้ที่เอกสารต้นทาง",
                    result.ProviderRefundRef, check.Amount, check.IsFullRefund, refundedBefore,
                    null, null, "", NextStepText),
                newStatus, result.ProviderRefundRef);
        }
    }

    /// <summary>ลงบัญชีคืนเงิน 1 ก้อน + ยอดคืนสะสม + เหตุการณ์ที่มียอดรายครั้ง — <b>ตัวเดียว</b>ของคืนเงินปกติและการตรวจผลที่ไม่แน่ชัด
    /// (ไม่ SaveChanges — ผู้เรียกบันทึกในธุรกรรมของตัวเอง)
    ///
    /// <para><paramref name="refundAtUtc"/> = เวลาที่เงินออกจริง (ตรวจผลทีหลัง = เวลาที่พยายามคืน ไม่ใช่เวลาที่กดตรวจ) —
    /// แผนรอบโอนใช้เวลานี้แยกยอดคืนก่อน/หลังวันเงินเข้า (R-E2)</para></summary>
    private async Task<JournalEntry> BookRefundAsync(Guid companyId, PaymentIntent intent, Guid arAccountId, Guid clearingAccountId,
        DateTime entryDate, decimal amount, decimal newRefundedTotal, string refundRef, string reason, string actor,
        PaymentIntentStatus fromStatus, PaymentIntentStatus newStatus, DateTime refundAtUtc, CancellationToken ct)
    {
        var je = await JournalEntryBuilder.For(_db, companyId, entryDate)
            .Type(JournalType.CashPayments)
            .NumberPrefix("PV")
            .Description($"คืนเงินลูกค้าผ่านผู้ให้บริการรับชำระเงิน ({intent.ProviderCode}) {refundRef}")
            .Reference(refundRef)
            .CreatedBy(actor)
            .Debit(arAccountId, amount, $"ตั้งลูกหนี้รอใบลดหนี้ — คืนเงิน {intent.SourceKind} ({reason})")
            .Credit(clearingAccountId, amount, $"คืนเงินจากยอดที่ผู้ให้บริการถือไว้ {refundRef}")
            .PostAsync(actor, ct);

        intent.RefundedAmount = newRefundedTotal;
        intent.LastRefundedAt = refundAtUtc;
        intent.LastRefundJournalEntryId = je.Id;
        intent.UpdatedAt = DateTime.UtcNow;
        _db.PaymentIntentEvents.Add(new PaymentIntentEvent
        {
            CompanyId = companyId,
            IntentId = intent.Id,
            At = refundAtUtc,
            Source = PaymentEventSource.Manual,
            FromStatus = fromStatus,
            ToStatus = newStatus,
            // ยอดรายครั้ง — ผลรวมต่อรายการต้องเท่ายอดคืนสะสม (GatewaySettlementMath.RefundedAsOf ใช้แยกก่อน/หลังวันเงินเข้า)
            RefundAmount = amount,
            Note = $"คืนเงิน {amount:N2} (สะสม {newRefundedTotal:N2}) · อ้างอิง {refundRef} · "
                 + $"JE {je.EntryNumber} โดย {actor} · {reason}",
        });
        return je;
    }

    /// <summary>E-2: ประทับ "ผลไม่แน่ชัด" + เหตุการณ์ ⚠️ บนแถวที่ถือล็อกอยู่ (ไม่ SaveChanges — ผู้เรียกบันทึก)</summary>
    private void MarkOutcomeUnknown(Guid companyId, PaymentIntent intent, DateTime attemptAtUtc, decimal amount,
        string attemptMarker, string actor, string reason, string error)
    {
        intent.RefundOutcomeUnknownSince ??= attemptAtUtc;
        // E2-2/E2-7: ยอด + เครื่องหมายของครั้งนี้ — ตรวจผลลงบัญชีได้เฉพาะเมื่อส่วนต่างเท่ายอดนี้ (ด่านคืนเงินกันไม่ให้มีครั้งที่สองซ้อนระหว่างล็อก)
        intent.RefundOutcomeUnknownAmount = amount;
        intent.RefundOutcomeUnknownAttempt = attemptMarker;
        intent.UpdatedAt = DateTime.UtcNow;
        _db.PaymentIntentEvents.Add(new PaymentIntentEvent
        {
            CompanyId = companyId,
            IntentId = intent.Id,
            At = DateTime.UtcNow,
            Source = PaymentEventSource.System,
            FromStatus = intent.Status,
            ToStatus = intent.Status,
            Note = $"⚠️ คืนเงิน {amount:N2} ผลไม่แน่ชัด — ผู้ให้บริการไม่ตอบ ({error}) · เงินอาจออกไปแล้ว · ล็อกการคืนเงินผ่านระบบ "
                 + $"จนกว่าจะตรวจผลกับผู้ให้บริการ · สั่งโดย {actor} · {reason}",
        });
    }

    public async Task<GatewayRefundOutcome> VerifyUnknownRefundAsync(Guid companyId, Guid intentId, string actor,
        CancellationToken ct = default)
    {
        var (outcome, statusToApply) = await VerifyCoreAsync(companyId, intentId, actor, ct);
        // สถานะเดินผ่านเครื่องสถานะตัวเดียว (หลัง commit) — เฉพาะเมื่อผู้ให้บริการยืนยันว่าเงินออกจริง
        if (statusToApply is PaymentIntentStatus st)
        {
            var intentNow = await _intents.FindAsync(companyId, intentId, CancellationToken.None);
            if (intentNow != null)
                await _intents.ApplyChargeAsync(intentId,
                    new ProviderCharge(intentNow.ProviderRef ?? string.Empty, st, "refunded", intentNow.Amount),
                    PaymentEventSource.Poll, $"verify:{actor} · ตรวจผลการคืนเงินที่ไม่แน่ชัด", CancellationToken.None);
        }
        return outcome;
    }

    /// <summary>เส้นธุรกรรมของการตรวจผล — ล็อกเดียวกับการคืนเงิน · ถามยอดคืนสะสมจากผู้ให้บริการ · ตัดสินด้วย
    /// <see cref="GatewayRefundMath.Verify"/> ตัวเดียว · ปลดล็อกเฉพาะเมื่อรู้แน่ (ไม่ประทับผลเอง)</summary>
    private async Task<(GatewayRefundOutcome Outcome, PaymentIntentStatus? StatusToApply)> VerifyCoreAsync(
        Guid companyId, Guid intentId, string actor, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.PaymentIntent,
                $"refund:{intentId:N}") }, ct);

        var intent = await _db.PaymentIntents
            .FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);
        if (intent == null) return (Fail("ไม่พบรายการชำระเงิน"), null);
        if (intent.RefundOutcomeUnknownSince is not DateTime attemptAt)
            return (Fail("รายการนี้ไม่มีการคืนเงินที่ผลไม่แน่ชัด — ไม่ต้องตรวจ"), null);

        var provider = _providers.FirstOrDefault(p => p.ProviderCode == intent.ProviderCode);
        if (provider == null) return (Fail($"ไม่รู้จักช่องทางชำระเงิน \"{intent.ProviderCode}\""), null);
        var config = intent.ProviderConfigId is Guid cid
            ? await _db.PaymentProviderConfigs.FirstOrDefaultAsync(c => c.Id == cid && c.CompanyId == companyId, ct)
            : null;

        ProviderCharge charge;
        try
        {
            charge = await provider.GetChargeAsync(intent,
                config ?? new PaymentProviderConfig { CompanyId = companyId, ProviderCode = intent.ProviderCode }, ct);
        }
        catch (Exception ex)
        {
            // ถามไม่สำเร็จ = ยังไม่รู้ ⇒ ไม่แตะอะไร (ล็อกคงอยู่) · บอกผู้ใช้ตรง ๆ
            _logger.LogWarning(ex, "ตรวจผลการคืนเงินไม่สำเร็จ — intent {Intent}", intentId);
            return (Fail($"ถามผู้ให้บริการไม่สำเร็จ ({ex.Message}) — ยังล็อกการคืนเงินไว้ ลองตรวจอีกครั้งภายหลัง"), null);
        }

        // E2-2: หา "ครั้งนี้" ในรายการคืนของผู้ให้บริการด้วยเครื่องหมายที่แนบไปตอนสั่งคืน (พบ = หลักฐานตรงว่าเงินออก + เลขอ้างอิงจริง ·
        // ไม่พบ = ไม่ใช่หลักฐานว่าไม่ออก) · E2-7: ส่งยอดที่พยายามคืนให้ตัวตัดสินเทียบกับส่วนต่าง · เวลา = ตัวตัดสินต้องรอพ้นช่วงที่คำขออาจยังค้าง
        var marked = !string.IsNullOrEmpty(intent.RefundOutcomeUnknownAttempt)
            ? charge.Refunds?.FirstOrDefault(r => r.AttemptMarker == intent.RefundOutcomeUnknownAttempt)
            : null;
        var v = GatewayRefundMath.Verify(intent.RefundedAmount, charge.RefundedTotal, intent.Amount,
            intent.RefundOutcomeUnknownAmount, marked?.Amount, attemptAt, DateTime.UtcNow);
        if (v.Outcome != GatewayRefundVerificationOutcome.MoneyWentOut)
        {
            if (v.Resolves) ClearOutcomeUnknown(intent);   // NoMoneyOut — ผู้ให้บริการยืนยันว่าไม่มีเงินออก (และพ้นช่วงรอแล้ว)
            intent.UpdatedAt = DateTime.UtcNow;
            _db.PaymentIntentEvents.Add(new PaymentIntentEvent
            {
                CompanyId = companyId, IntentId = intent.Id, At = DateTime.UtcNow, Source = PaymentEventSource.Poll,
                FromStatus = intent.Status, ToStatus = intent.Status,
                Note = $"ตรวจผลการคืนเงินที่ไม่แน่ชัด โดย {actor}: {v.Message}",
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (v.Resolves
                ? new GatewayRefundOutcome(true, v.Message, null, 0m, false, intent.RefundedAmount, null, null, "", null)
                : Fail(v.Message), null);
        }

        // เงินออกไปแล้วจริง — ลงบัญชีส่วนต่างด้วยเส้นเดียวกับคืนเงินปกติ (ผัง/งวดต้องพร้อม ไม่งั้นล็อกคงอยู่ + บอกทางไปต่อ)
        var clearingId = await _accounts.ResolveMoneyInAccountAsync(intent, ct);
        var ar = await TradeReceivableAccount.ResolveAsync(_db, companyId, await ContactArPinAsync(companyId, intent, ct), ct);
        // รอบ 200 ทีม G (E2-12): ลงวันที่เงินออกจริง (เวลาที่พยายามคืน) · งวดนั้นปิดแล้ว ⇒ วันนี้พร้อมหมายเหตุ — ตัวตัดสินเดียวกับบันทึกผลด้วยมือ
        var booking = await PastRefundBookingAsync(companyId, attemptAt, ct);
        var entryDate = booking.EntryDate;
        var closed = await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, entryDate, ct);
        if (clearingId is not Guid clearing || ar == null || closed != null)
            return (Fail($"ผู้ให้บริการคืนเงินไปแล้วจริง {v.AmountToBook:N2} บาท แต่ยังลงบัญชีไม่ได้: "
                + (closed ?? (clearingId == null
                    ? "ไม่พบบัญชีพักของผู้ให้บริการ (แนะนำ 11340)"
                    : $"ไม่พบผังลูกหนี้ {TradeReceivableAccount.StandardCode}"))
                + " — แก้แล้วกดตรวจอีกครั้ง (ยังล็อกการคืนเงินไว้)"), null);

        var newTotal = intent.RefundedAmount + v.AmountToBook;
        var isFull = newTotal >= intent.Amount - GatewayRefundMath.Tolerance;
        var newStatus = isFull ? PaymentIntentStatus.Refunded : PaymentIntentStatus.PartiallyRefunded;
        // เลขอ้างอิงจริงของผู้ให้บริการเมื่อพบรายการที่มีเครื่องหมาย (review198-E2 E2-12) · ไม่พบ = เลขภายใน
        var refundRef = !string.IsNullOrWhiteSpace(marked?.ProviderRefundRef)
            ? marked!.ProviderRefundRef
            : $"VERIFY-{intentId:N}"[..15];
        var je = await BookRefundAsync(companyId, intent, ar.Id, clearing, entryDate, v.AmountToBook, newTotal, refundRef,
            "ตรวจผลการคืนเงินที่ไม่แน่ชัด — ผู้ให้บริการยืนยันว่าคืนแล้ว" + (booking.Note == null ? "" : " · " + booking.Note),
            actor, intent.Status, newStatus, attemptAt, ct);
        ClearOutcomeUnknown(intent);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (new GatewayRefundOutcome(true, $"{v.Message} — ใบสำคัญ {je.EntryNumber}", refundRef, v.AmountToBook, isFull,
            newTotal, je.Id, je.EntryNumber, "", NextStepText), newStatus);
    }

    /// <summary>วันที่ใบสำคัญของเงินคืนที่ยืนยันทีหลัง — ถามงวดของวันที่เงินออกจริงแล้วให้ <see cref="GatewayRefundMath.PastRefundBooking"/> ตัดสิน</summary>
    private async Task<GatewayPastRefundBooking> PastRefundBookingAsync(Guid companyId, DateTime attemptAtUtc, CancellationToken ct)
    {
        var attemptClosed = await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId,
            ThaiDate.CalendarDateUtc(attemptAtUtc), ct) != null;
        return GatewayRefundMath.PastRefundBooking(attemptAtUtc, DateTime.UtcNow, attemptClosed);
    }

    /// <summary>ปลดล็อก "ผลไม่แน่ชัด" — ล้างทั้งธง ยอด และเครื่องหมายของครั้งนั้นพร้อมกัน (ห้ามล้างแค่ธงแล้วทิ้งยอดเก่าไว้ให้ครั้งหน้าอ่านผิด)</summary>
    private static void ClearOutcomeUnknown(PaymentIntent intent)
    {
        intent.RefundOutcomeUnknownSince = null;
        intent.RefundOutcomeUnknownAmount = null;
        intent.RefundOutcomeUnknownAttempt = null;
        intent.UpdatedAt = DateTime.UtcNow;
    }

    public async Task<GatewayRefundOutcome> ResolveUnknownRefundManuallyAsync(Guid companyId, Guid intentId,
        GatewayRefundManualDecision decision, decimal? amount, string? providerRefundRef, string? evidence,
        string actor, string? actorEmail, string? ipAddress, CancellationToken ct = default)
    {
        var (outcome, statusToApply) = await ResolveManuallyCoreAsync(companyId, intentId, decision, amount, providerRefundRef,
            evidence, actor, actorEmail, ipAddress, ct);
        // สถานะเดินผ่านเครื่องสถานะตัวเดียว (หลัง commit) — เฉพาะเมื่อคนยืนยันว่าเงินออกจริงและลงบัญชีแล้ว
        if (statusToApply is PaymentIntentStatus st)
        {
            var intentNow = await _intents.FindAsync(companyId, intentId, CancellationToken.None);
            if (intentNow != null)
                await _intents.ApplyChargeAsync(intentId,
                    new ProviderCharge(intentNow.ProviderRef ?? string.Empty, st, "refunded", intentNow.Amount),
                    PaymentEventSource.Manual, $"manual:{actor} · บันทึกผลการคืนเงินที่ไม่แน่ชัดด้วยมือ", CancellationToken.None);
        }
        return outcome;
    }

    /// <summary>เส้นธุรกรรมของการบันทึกผลด้วยมือ — ล็อกเดียวกับการคืนเงิน · ตัดสินด้วย <see cref="GatewayRefundMath.CheckManualResolution"/>
    /// ตัวเดียว · เงินออก ⇒ <see cref="BookRefundAsync"/> (ตัวลงบัญชีคืนเงินตัวเดียว) · ทุกผลลง hash chain (ใคร · ตัดสินอะไร · จากหลักฐานอะไร)</summary>
    private async Task<(GatewayRefundOutcome Outcome, PaymentIntentStatus? StatusToApply)> ResolveManuallyCoreAsync(
        Guid companyId, Guid intentId, GatewayRefundManualDecision decision, decimal? amount, string? providerRefundRef,
        string? evidence, string actor, string? actorEmail, string? ipAddress, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.PaymentIntent,
                $"refund:{intentId:N}") }, ct);

        var intent = await _db.PaymentIntents
            .FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);
        if (intent == null) return (Fail("ไม่พบรายการชำระเงิน"), null);

        var attemptAt = intent.RefundOutcomeUnknownSince ?? DateTime.UtcNow;
        var check = GatewayRefundMath.CheckManualResolution(intent.RefundOutcomeUnknownSince != null, decision, amount,
            providerRefundRef, evidence, intent.RefundedAmount, intent.Amount, attemptAt, DateTime.UtcNow);
        if (!check.Ok) return (Fail(check.Message ?? "บันทึกผลไม่ได้"), null);

        var evidenceText = evidence!.Trim();
        var before = new
        {
            refundOutcomeUnknownSince = intent.RefundOutcomeUnknownSince,
            refundOutcomeUnknownAmount = intent.RefundOutcomeUnknownAmount,
            refundedAmount = intent.RefundedAmount,
        };
        JournalEntry? je = null;
        PaymentIntentStatus? newStatus = null;
        var newTotal = intent.RefundedAmount;
        string? refundRef = null;

        if (decision == GatewayRefundManualDecision.MoneyWentOut)
        {
            // ลงบัญชีด้วยเส้นเดียวกับคืนเงินปกติ — ผัง/งวดต้องพร้อม ไม่งั้นล็อกคงอยู่ + บอกทางไปต่อ (ไม่มีอะไรถูกเขียน)
            var clearingId = await _accounts.ResolveMoneyInAccountAsync(intent, ct);
            var ar = await TradeReceivableAccount.ResolveAsync(_db, companyId, await ContactArPinAsync(companyId, intent, ct), ct);
            // รอบ 200 ทีม G (E2-12 + คำถามเจ้าของข้อ 4): วันที่เงินออกจริง ไม่ใช่วันที่กดบันทึก — ตัวตัดสินเดียวกับการตรวจผล
            var booking = await PastRefundBookingAsync(companyId, attemptAt, ct);
            var entryDate = booking.EntryDate;
            var closed = await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, entryDate, ct);
            if (clearingId is not Guid clearing || ar == null || closed != null)
                return (Fail("ยังลงบัญชีคืนเงินไม่ได้: "
                    + (closed ?? (clearingId == null
                        ? "ไม่พบบัญชีพักของผู้ให้บริการ (แนะนำ 11340)"
                        : $"ไม่พบผังลูกหนี้ {TradeReceivableAccount.StandardCode}"))
                    + " — แก้แล้วบันทึกผลอีกครั้ง (ยังล็อกการคืนเงินไว้)"), null);

            refundRef = providerRefundRef!.Trim();
            if (refundRef.Length > 100) refundRef = refundRef[..100];
            newTotal = intent.RefundedAmount + check.AmountToBook;
            newStatus = newTotal >= intent.Amount - GatewayRefundMath.Tolerance
                ? PaymentIntentStatus.Refunded : PaymentIntentStatus.PartiallyRefunded;
            je = await BookRefundAsync(companyId, intent, ar.Id, clearing, entryDate, check.AmountToBook, newTotal, refundRef,
                $"บันทึกผลการคืนเงินที่ไม่แน่ชัดด้วยมือ — {evidenceText}" + (booking.Note == null ? "" : " · " + booking.Note),
                actor, intent.Status, newStatus.Value, attemptAt, ct);
        }
        else
        {
            _db.PaymentIntentEvents.Add(new PaymentIntentEvent
            {
                CompanyId = companyId, IntentId = intent.Id, At = DateTime.UtcNow, Source = PaymentEventSource.Manual,
                FromStatus = intent.Status, ToStatus = intent.Status,
                Note = $"บันทึกผลการคืนเงินที่ไม่แน่ชัดด้วยมือ โดย {actor}: ไม่มีเงินออก (ตามแดชบอร์ดผู้ให้บริการ) · ปลดล็อก · หลักฐาน: {evidenceText}",
            });
        }
        ClearOutcomeUnknown(intent);

        // การตัดสินแทนผู้ให้บริการ = จุดที่ผู้สอบบัญชีถามเสมอ ⇒ hash chain (ใคร · ตัดสินอะไร · ยอด · หลักฐาน)
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            EntityType = nameof(PaymentIntent),
            EntityId = intent.Id.ToString(),
            Action = AuditAction.Update,
            UserEmail = actorEmail,
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "gateway-refund-manual-resolution",
                decision = decision.ToString(),
                amount = check.AmountToBook,
                providerRefundRef = refundRef,
                refundedAmount = newTotal,
                journalEntryNumber = je?.EntryNumber,
                evidence = evidenceText,
                actor,
            }),
            IpAddress = ipAddress,
            Timestamp = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (je == null)
            return (new GatewayRefundOutcome(true, "บันทึกผลแล้ว: ไม่มีเงินออก — ปลดล็อกการคืนเงินของรายการนี้แล้ว คืนใหม่ได้",
                null, 0m, false, intent.RefundedAmount, null, null, "", null), null);
        return (new GatewayRefundOutcome(true,
                $"บันทึกผลแล้ว: ผู้ให้บริการคืนไป {check.AmountToBook:N2} บาท — ลงบัญชีใบสำคัญ {je.EntryNumber} · ปลดล็อกแล้ว",
                refundRef, check.AmountToBook, newStatus == PaymentIntentStatus.Refunded, newTotal, je.Id, je.EntryNumber, "",
                NextStepText), newStatus);
    }

    public async Task<GatewayRefundOutcome> RecordLegacyRefundAsync(Guid companyId, Guid intentId, decimal? amount, DateTime? refundedAtUtc,
        string? providerRefundRef, string? evidence, GatewayLegacyRefundJournal journal,
        string actor, string? actorEmail, string? ipAddress, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // ล็อกเดียวกับการคืนเงิน/บันทึกผลด้วยมือของรายการนี้ — ยอดคืนสะสมที่ตรวจต้องเป็นยอดเดียวกับที่เขียน
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.PaymentIntent, $"refund:{intentId:N}") }, ct);

        var intent = await _db.PaymentIntents
            .FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);
        if (intent == null) return Fail("ไม่พบรายการชำระเงิน");

        var check = GatewayRefundMath.CheckLegacyRefundEntry(intent.Status, intent.Amount, intent.RefundedAmount,
            intent.RefundOutcomeUnknownSince != null, amount, refundedAtUtc, DateTime.UtcNow, providerRefundRef, evidence, journal);
        if (!check.Ok) return Fail(check.Message ?? "บันทึกไม่ได้");

        var refundAt = refundedAtUtc!.Value;
        var refundRef = providerRefundRef!.Trim();
        if (refundRef.Length > 100) refundRef = refundRef[..100];
        var evidenceText = evidence!.Trim();
        JournalEntry? je = null;
        if (journal == GatewayLegacyRefundJournal.BookNow)
        {
            // เส้นลงบัญชีคืนเงินตัวเดียว (BookRefundAsync) · วันที่ = วันที่เงินออกจริง (งวดปิด ⇒ วันนี้ + หมายเหตุ — ตัวตัดสินเดียวกับการตรวจผล)
            var clearingId = await _accounts.ResolveMoneyInAccountAsync(intent, ct);
            var ar = await TradeReceivableAccount.ResolveAsync(_db, companyId, await ContactArPinAsync(companyId, intent, ct), ct);
            var booking = await PastRefundBookingAsync(companyId, refundAt, ct);
            var closed = await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, booking.EntryDate, ct);
            if (clearingId is not Guid clearing || ar == null || closed != null)
                return Fail("ยังลงบัญชีคืนเงินไม่ได้: "
                    + (closed ?? (clearingId == null
                        ? "ไม่พบบัญชีพักของผู้ให้บริการ (แนะนำ 11340)"
                        : $"ไม่พบผังลูกหนี้ {TradeReceivableAccount.StandardCode}"))
                    + " — แก้แล้วบันทึกอีกครั้ง (ยังไม่มีอะไรถูกบันทึก)");
            je = await BookRefundAsync(companyId, intent, ar.Id, clearing, booking.EntryDate, check.AmountToBook, check.AmountToBook, refundRef,
                $"บันทึกยอดคืนย้อนหลัง (คืนก่อนระบบเก็บยอดคืน) — {evidenceText}" + (booking.Note == null ? "" : " · " + booking.Note),
                actor, intent.Status, intent.Status, refundAt, ct);
        }
        else
        {
            // ลงรายการบัญชีด้วยมือไว้แล้ว ⇒ บันทึกเฉพาะยอดคืน + เหตุการณ์ที่มียอดรายครั้ง (แผนรอบโอนใช้แยกก่อน/หลังวันเงินเข้า) — ห้ามลงซ้ำ
            intent.RefundedAmount = check.AmountToBook;
            intent.LastRefundedAt = refundAt;
            intent.UpdatedAt = DateTime.UtcNow;
            _db.PaymentIntentEvents.Add(new PaymentIntentEvent
            {
                CompanyId = companyId, IntentId = intent.Id, At = refundAt, Source = PaymentEventSource.Manual,
                FromStatus = intent.Status, ToStatus = intent.Status,
                RefundAmount = check.AmountToBook,
                Note = $"บันทึกยอดคืนย้อนหลัง {check.AmountToBook:N2} · อ้างอิง {refundRef} · ลงรายการบัญชีด้วยมือไว้แล้ว (ระบบไม่ลงซ้ำ) "
                     + $"โดย {actor} · หลักฐาน: {evidenceText}",
            });
        }

        // การเติมยอดที่ระบบไม่รู้ด้วยมือ = จุดที่ผู้สอบบัญชีถามเสมอ ⇒ hash chain (ใคร · ยอด · ลงบัญชีหรือไม่ · หลักฐาน)
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            EntityType = nameof(PaymentIntent),
            EntityId = intent.Id.ToString(),
            Action = AuditAction.Update,
            UserEmail = actorEmail,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { refundedAmount = 0m }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "gateway-legacy-refund-entry",
                amount = check.AmountToBook,
                refundedAt = refundAt,
                providerRefundRef = refundRef,
                journal = journal.ToString(),
                journalEntryNumber = je?.EntryNumber,
                evidence = evidenceText,
                actor,
            }),
            IpAddress = ipAddress,
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new GatewayRefundOutcome(true,
            je == null
                ? $"บันทึกยอดคืน {check.AmountToBook:N2} บาทแล้ว (ไม่ลงบัญชีซ้ำ — ลงด้วยมือไว้แล้ว) · รายการนี้เข้ารอบโอนตามปกติ"
                : $"บันทึกยอดคืน {check.AmountToBook:N2} บาทและลงบัญชีใบสำคัญ {je.EntryNumber} แล้ว · รายการนี้เข้ารอบโอนตามปกติ",
            refundRef, check.AmountToBook, intent.Status == PaymentIntentStatus.Refunded, check.AmountToBook,
            je?.Id, je?.EntryNumber, "", NextStepText);
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

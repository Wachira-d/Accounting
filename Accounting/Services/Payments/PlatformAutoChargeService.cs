using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>ผลหนึ่งรอบของงานตัดบัตรอัตโนมัติ</summary>
public sealed record AutoChargeRunResult(int Payers, int Attempted, int Succeeded, int Failed, int Skipped, string Message);

public sealed record AdminAutoChargeAttemptRow(Guid Id, Guid PayerCompanyId, string? CompanyName, Guid DocumentId, string? DocumentNumber,
    int AttemptNo, decimal Amount, string Outcome, string OutcomeLabel, string? Detail, DateTime AttemptedAt, Guid? IntentId);

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §5.2) — <b>ตัดบัตรอัตโนมัติ</b>ของใบแจ้งหนี้ค่าบริการ NextAcc ที่ถึงกำหนด (ใบต่ออายุ · ใบค่าใช้งาน · ใบอื่นของผู้เช่า)
/// <list type="number">
/// <item>สวิตช์แพลตฟอร์มเปิด · ช่องทาง NextAcc เปิดรับบัตรจริง · คำสั่งตัดของบริษัทเปิด + ไม่ถูกแอดมินระงับ + ยินยอมฉบับปัจจุบัน · บัตรตั้งต้นยืนยันแล้ว/ไม่หมดอายุ/โหมดเดียวกัน
///   (<see cref="PlatformSavedCardRules.AutoChargeBlockReason"/>) · สถานะแพ็กเกจตัดได้ (<see cref="PlatformAutoChargePolicy.SubscriptionChargeable"/>)</item>
/// <item>ต่อใบ: ตัวตัดสิน <see cref="PlatformAutoChargePolicy.Decide"/> (วันครบกำหนด + วันลองใหม่จากนโยบายทวงหนี้เดิม · ใบที่ทวงหนี้ไม่นับ = ไม่ตัด) ⇒
///   <b>จองแถวความพยายาม</b> (unique ใบ+ครั้งที่ — สองเครื่องไม่ตัดซ้ำ) ⇒ รายการชำระผูกใบ (<c>SubmitInvoicePaymentAsync</c> — ยอด = ยอดค้างของใบจากเซิร์ฟเวอร์) ⇒
///   <c>IPlatformPaymentService.StartSubscriptionPaymentAsync</c> ด้วยบัตรที่บันทึก (ทางเดียวกับผู้เช่ากดจ่ายเอง: intent ใต้บริษัทผู้ให้บริการ · ด่านยอด/สลิปซ้อน · handler ตัดชำระใบ)</item>
/// <item>ผล: สำเร็จ/ล้ม/ธนาคารขอยืนยันตัวตน ⇒ ประทับบนแถว + แจ้งผู้เช่า (คีย์กันซ้ำ) · ล้มครบจำนวนครั้ง ⇒ เหลือทวงหนี้/ระงับตามเดิม (<c>PlatformDunningService</c>)</item>
/// </list>
/// <para>ความยินยอมในคำสั่งตัด (อ้างฉบับนโยบายยกเลิก/คืนเงิน) คือหลักฐานการยอมรับเงื่อนไขของการตัดแต่ละครั้ง — นโยบายเปลี่ยน ⇒ หยุดตัดจนผู้เช่ายืนยันใหม่</para>
/// </summary>
public interface IPlatformAutoChargeService
{
    /// <summary>หนึ่งรอบ — ผู้เรียก (งาน/ปุ่มแอดมิน) ต้องถือ JobLock ของงานนี้ก่อน</summary>
    Task<AutoChargeRunResult> RunOnceAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AdminAutoChargeAttemptRow>> RecentAttemptsAsync(int take, CancellationToken ct = default);
}

public class PlatformAutoChargeService : IPlatformAutoChargeService
{
    public const int MaxPayersPerRun = 500;
    /// <summary>แถวที่จองแล้วแต่ไม่มีผล (เครื่องล้มกลางคัน) เกินกี่นาทีจึงถือว่าล้ม</summary>
    public const int StaleClaimMinutes = 60;
    /// <summary>ฝ่ายค้าน B1: ตรวจความพยายามที่ล้มย้อนกี่วัน (เงินเข้าทีหลัง ⇒ ประทับสำเร็จ)</summary>
    public const int RecheckFailedDays = 7;

    private readonly AccountingDbContext _db;
    private readonly IPlatformPaymentService _platform;
    private readonly IPlatformSavedCardService _cards;
    private readonly ISubscriptionService _subs;
    private readonly IPlatformBillingNotifier _notifier;
    private readonly ILogger<PlatformAutoChargeService> _logger;

    public PlatformAutoChargeService(AccountingDbContext db, IPlatformPaymentService platform, IPlatformSavedCardService cards,
        ISubscriptionService subs, IPlatformBillingNotifier notifier, ILogger<PlatformAutoChargeService> logger)
    { _db = db; _platform = platform; _cards = cards; _subs = subs; _notifier = notifier; _logger = logger; }

    private sealed class Tally { public int Payers, Attempted, Succeeded, Failed, Skipped; }

    public async Task<AutoChargeRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        if (!await _cards.GlobalEnabledAsync(ct)) return new(0, 0, 0, 0, 0, "ปิดการตัดบัตรอัตโนมัติทั้งระบบอยู่");
        if (await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct) is not Guid pid) return new(0, 0, 0, 0, 0, "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ");
        var status = await _platform.StatusAsync(ct);
        if (!status.Online || !status.Methods.Contains(nameof(PaymentMethodKind.Card)))
            return new(0, 0, 0, 0, 0, "ช่องทางรับชำระของ NextAcc ยังไม่เปิดรับบัตรจริง — ไม่ตัด (" + (status.OfflineReason ?? "ไม่มีวิธีบัตร") + ")");
        var cfg = await _platform.PlatformConfigAsync(ct);
        if (cfg == null) return new(0, 0, 0, 0, 0, "ยังไม่ได้ตั้งช่องทางรับชำระของ NextAcc");
        var policy = await PlatformBillingSettingsService.LoadPolicyAsync(_db, ct);
        var retryDays = PlatformAutoChargePolicy.RetryDays(policy.ReminderDaysAfterDue);
        var version = await _cards.CurrentConsentVersionAsync(ct);

        var mandates = await _db.PlatformAutoChargeMandates.AsNoTracking()
            .Where(m => m.PlatformCompanyId == pid && m.Enabled && m.AdminBlockedAt == null)
            // ฝ่ายค้าน S4: หมุนตาม watermark (ประมวลผลล่าสุดเก่าสุดก่อน · ยังไม่เคย = ก่อนสุด) — บริษัทที่ 501 ไม่ถูกข้ามตลอดไป
            .OrderBy(m => m.LastProcessedAt.HasValue).ThenBy(m => m.LastProcessedAt).ThenBy(m => m.EnabledAt)
            .Take(MaxPayersPerRun).ToListAsync(ct);
        var t = new Tally();
        foreach (var m in mandates)
        {
            if (ct.IsCancellationRequested) break;
            t.Payers++;
            try { await ProcessPayerAsync(pid, cfg, m, version, retryDays, t, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // บริษัทหนึ่งล้มไม่ลากบริษัทอื่น — ล้มดัง: log + นับในสรุปรอบ (JobRunLog) · แถวความพยายามที่จองไว้ถูกประทับผลใน ChargeInvoiceAsync แล้ว
                t.Failed++;
                _logger.LogError(ex, "ตัดบัตรอัตโนมัติของบริษัท {Payer} ล้ม", m.PayerCompanyId);
                _db.ChangeTracker.Clear();
            }
            // watermark ต่อบริษัท (ล้มก็ขยับ — ไม่ให้บริษัทที่ล้มซ้ำกินคิวทุกรอบ · ความพยายามที่จองไว้ยังอยู่ให้รอบหน้าตามผล)
            await _db.PlatformAutoChargeMandates.Where(x => x.Id == m.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastProcessedAt, DateTime.UtcNow), ct);
        }
        return new(t.Payers, t.Attempted, t.Succeeded, t.Failed, t.Skipped,
            $"บริษัท {t.Payers} · ตัด {t.Attempted} ครั้ง · สำเร็จ {t.Succeeded} · ไม่สำเร็จ {t.Failed} · ข้าม {t.Skipped}");
    }

    private async Task ProcessPayerAsync(Guid pid, PaymentProviderConfig cfg, PlatformAutoChargeMandate m, string version,
        IReadOnlyList<int> retryDays, Tally t, CancellationToken ct)
    {
        var payer = m.PayerCompanyId;
        await _cards.RefreshVerificationAsync(payer, ct);
        await SettleInFlightAsync(pid, payer, ct);

        var (card, cardProblem) = await _cards.UsableDefaultCardAsync(payer, cfg, ct);
        var juristic = await _cards.PayerIsJuristicAsync(payer, ct);
        if (PlatformSavedCardRules.AutoChargeBlockReason(true, m.Enabled, m.AdminBlockedAt != null, m.ConsentVersion, version, cardProblem,
                juristic, m.WhtAcknowledgedAt != null) != null
            || card == null)
        { t.Skipped++; return; }
        var alive = await _db.Companies.AsNoTracking().AnyAsync(c => c.Id == payer && !c.IsDeleted, ct);
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == payer, ct);
        if (!alive || sub == null) { t.Skipped++; return; }
        // ฝ่ายค้าน S7: ระงับโดยทวงหนี้ = ธงของงานทวงหนี้ + การเปลี่ยนเป็นระงับครั้งล่าสุดในประวัติเป็นของงานทวงหนี้ (แอดมินระงับทับ ⇒ ไม่ตัด)
        var dunningFlag = await _db.PlatformDunningStates.AsNoTracking()
            .AnyAsync(s => s.CompanyId == payer && s.SuspendedByDunningAt != null, ct);
        var lastSuspendAction = sub.Status != SubscriptionStatus.Suspended ? null : await _db.SubscriptionHistories.AsNoTracking()
            .Where(h => h.SubscriptionId == sub.Id && h.ToStatus == SubscriptionStatus.Suspended)
            .OrderByDescending(h => h.CreatedAt).Select(h => h.Action).FirstOrDefaultAsync(ct);
        var suspendedByDunning = PlatformAutoChargePolicy.SuspendedByDunning(dunningFlag, lastSuspendAction);
        if (!PlatformAutoChargePolicy.SubscriptionChargeable(sub.Status, suspendedByDunning)) { t.Skipped++; return; }

        // ใบค้างของบริษัทนี้ + ใบที่ทวงหนี้ไม่นับ (ตัวตัดสินเดียวกับ PlatformDunningService: PlatformInvoiceClaims.AwaitingPlatform + PlatformDunningPolicy.Excluded)
        var open = PlatformInvoiceRules.OpenStatuses;
        var rows = await PlatformInvoiceQueries.ForBuyer(_db, pid, payer)
            .Where(d => open.Contains(d.Status) && d.BalanceDue > PlatformInvoiceRules.Epsilon)
            .AsRows(payer).ToListAsync(ct);
        if (rows.Count == 0) return;
        var ids = rows.Select(r => r.Id).ToList();
        var awaiting = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.SubscriptionId == sub.Id && p.PlatformInvoiceDocumentId != null && ids.Contains(p.PlatformInvoiceDocumentId.Value))
            .Where(PlatformInvoiceClaims.AwaitingPlatform)
            .Select(p => p.PlatformInvoiceDocumentId!.Value).ToListAsync(ct);
        var unlinkedRenewalSlip = sub.RenewalInvoiceDocumentId != null && await _db.SubscriptionPayments.AsNoTracking()
            .AnyAsync(p => p.SubscriptionId == sub.Id && p.PlatformInvoiceDocumentId == null && p.RequestedPeriodMonths > 0 && p.SlipFileName != null
                && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview), ct);
        var attempts = await _db.PlatformAutoChargeAttempts.AsNoTracking()
            .Where(a => a.PlatformCompanyId == pid && ids.Contains(a.DocumentId))
            .Select(a => new { a.DocumentId, a.AttemptNo, a.AttemptedAt, a.Outcome }).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var r in rows.OrderBy(x => x.DueDate))
        {
            var excluded = PlatformDunningPolicy.Excluded(awaiting.Contains(r.Id), sub.CancelAtPeriodEnd, sub.RenewalInvoiceDocumentId == r.Id,
                PlatformInvoiceRules.SupersededRenewalReason(r.Reference, r.Id, sub.RenewalInvoiceDocumentId) != null, unlinkedRenewalSlip);
            var facts = attempts.Where(a => a.DocumentId == r.Id).Select(a => new AutoChargeAttemptFact(a.AttemptNo, a.AttemptedAt, a.Outcome)).ToList();
            var d = PlatformAutoChargePolicy.Decide(r.DueDate, now, r.BalanceDue, excluded, facts, retryDays);
            if (d.AttemptNo is not int no) continue;
            if (await OpenOnlinePaymentAsync(sub.Id, r.Id, ct)) { t.Skipped++; continue; }   // ผู้เช่าเริ่มจ่ายออนไลน์ไว้ (QR/3DS) — ไม่ตัดซ้อน
            // ฝ่ายค้าน (nice): ผู้เช่าแจ้งภาษีหัก ณ ที่จ่ายไว้กับรายการของใบนี้ ⇒ ไม่ตัด (ตัดเต็มยอดจะล้างยอดภาษีที่แจ้งไว้)
            if (await _db.SubscriptionPayments.AsNoTracking().AnyAsync(p => p.SubscriptionId == sub.Id && p.PlatformInvoiceDocumentId == r.Id
                    && p.WithholdingTaxAmount > 0m
                    && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview), ct))
            { t.Skipped++; continue; }
            await ChargeInvoiceAsync(pid, payer, r, no, retryDays, card, t, ct);
        }
    }

    /// <summary>มีรายการชำระออนไลน์ที่ยังเปิดอยู่ (QR/รอธนาคาร) ของรายการชำระที่ผูกใบนี้ไหม — มี ⇒ ห้ามตัดซ้อน</summary>
    private async Task<bool> OpenOnlinePaymentAsync(Guid subscriptionId, Guid documentId, CancellationToken ct)
    {
        var payIds = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.SubscriptionId == subscriptionId && p.PlatformInvoiceDocumentId == documentId
                && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview))
            .Select(p => p.Id).ToListAsync(ct);
        if (payIds.Count == 0) return false;
        return await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.SourceKind == PaymentSourceKind.SubscriptionPayment
            && payIds.Contains(i.SourceId) && (i.Status == PaymentIntentStatus.Created || i.Status == PaymentIntentStatus.Pending), ct);
    }

    private async Task ChargeInvoiceAsync(Guid pid, Guid payer, PlatformInvoiceRow inv, int attemptNo, IReadOnlyList<int> retryDays,
        PlatformCardRefs card, Tally t, CancellationToken ct)
    {
        // ── 1. จองครั้งนี้ (unique ใบ + ครั้งที่) — อีกเครื่อง/อีกรอบจองไปแล้ว ⇒ ไม่ทำซ้ำ ──
        var attempt = new PlatformAutoChargeAttempt
        {
            PlatformCompanyId = pid, PayerCompanyId = payer, DocumentId = inv.Id, AttemptNo = attemptNo, SavedCardId = card.SavedCardId,
            Amount = decimal.Round(inv.BalanceDue, 2, MidpointRounding.AwayFromZero), Outcome = PlatformAutoChargePolicy.OutcomeClaimed,
            AttemptedAt = DateTime.UtcNow, CreatedBy = "system:autocharge",
        };
        _db.PlatformAutoChargeAttempts.Add(attempt);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.Entry(attempt).State = EntityState.Detached;
            t.Skipped++;
            return;
        }
        t.Attempted++;

        // ── 2. รายการชำระผูกใบ (ยอด = ยอดค้างของใบ · เส้นเดียวกับศูนย์บิล) → 3. เริ่มชำระด้วยบัตรที่บันทึก ──
        PaymentIntent? intent = null;
        string? refusal = null;
        try
        {
            var payment = await _subs.SubmitInvoicePaymentAsync(payer, inv.Id,
                new SubmitInvoicePaymentRequest(null, null, null, $"ตัดบัตรอัตโนมัติ ครั้งที่ {attemptNo}", null), "system:autocharge");
            attempt.SubscriptionPaymentId = payment.Id;
            // ฝ่ายค้าน (nice): วิธีชำระของรายการ = บัตร (ตัวแจ้งชำระประทับโอนเงินเป็นค่าตั้งต้น)
            var sp = await _db.SubscriptionPayments.FirstOrDefaultAsync(p => p.Id == payment.Id, ct);
            if (sp != null) sp.PaymentMethod = PaymentMethod.CreditCard;
            await _db.SaveChangesAsync(ct);
            intent = await _platform.StartSubscriptionPaymentAsync(payer, payment.Id, PaymentMethodKind.Card, null, null, ct, card);
        }
        catch (BusinessRuleException ex) { refusal = ex.Message; }
        catch (KeyNotFoundException ex) { refusal = ex.Message; }
        catch (InvalidOperationException ex) { refusal = ex.Message; }   // ด่านก่อนสร้าง charge (ยังไม่มีเงินไปไหน) — ผู้ให้บริการล้ม = intent Failed ไม่ใช่ exception

        // ── 4. ประทับผล + แจ้ง ──
        var now = DateTime.UtcNow;
        attempt.IntentId = intent?.Id;
        attempt.Outcome = intent == null ? PlatformAutoChargePolicy.OutcomeFailed
            : PlatformAutoChargePolicy.OutcomeOf(intent.Status, !string.IsNullOrWhiteSpace(intent.AuthorizeUrl), intent.FailureCode);
        attempt.Detail = Trunc(refusal ?? intent?.FailureMessage, 500);
        if (!PlatformAutoChargePolicy.InFlight(attempt.Outcome)) attempt.FinishedAt = now;
        attempt.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        if (attempt.Outcome == PlatformAutoChargePolicy.OutcomeSucceeded) t.Succeeded++;
        else if (attempt.Outcome == PlatformAutoChargePolicy.OutcomeFailed) t.Failed++;
        await NotifyAsync(payer, inv.Id, inv.DocumentNumber, attempt, retryDays, ct);
    }

    /// <summary>ความพยายามที่ยังไม่จบ ⇒ อ่านผลล่าสุดของรายการชำระ (ถามผู้ให้บริการผ่าน RefreshForPayerAsync) · จองค้างไม่มีรายการ ⇒ หา intent ของรายการชำระ / หมดเวลา = ล้ม</summary>
    private async Task SettleInFlightAsync(Guid pid, Guid payer, CancellationToken ct)
    {
        // ฝ่ายค้าน B1: รวมความพยายามที่ "ล้ม" ภายใน 7 วันที่มีรายการชำระ — เงินอาจเข้าทีหลัง (ผลไม่แน่ชัดที่ปิดไปแล้ว/webhook มาช้า) ⇒ ประทับสำเร็จ + แจ้งใหม่
        var recentFailed = DateTime.UtcNow.AddDays(-RecheckFailedDays);
        var inflight = await _db.PlatformAutoChargeAttempts
            .Where(a => a.PlatformCompanyId == pid && a.PayerCompanyId == payer
                && (a.Outcome == PlatformAutoChargePolicy.OutcomeClaimed || a.Outcome == PlatformAutoChargePolicy.OutcomePending
                    || a.Outcome == PlatformAutoChargePolicy.OutcomeRequiresAction
                    || (a.Outcome == PlatformAutoChargePolicy.OutcomeFailed && a.IntentId != null && a.AttemptedAt >= recentFailed)))
            .ToListAsync(ct);
        if (inflight.Count == 0) return;
        var policy = await PlatformBillingSettingsService.LoadPolicyAsync(_db, ct);
        var retryDays = PlatformAutoChargePolicy.RetryDays(policy.ReminderDaysAfterDue);
        var now = DateTime.UtcNow;
        foreach (var a in inflight)
        {
            if (a.IntentId == null && a.SubscriptionPaymentId is Guid spId)
                a.IntentId = await _db.PaymentIntents.AsNoTracking()
                    .Where(i => i.CompanyId == pid && i.SourceKind == PaymentSourceKind.SubscriptionPayment && i.SourceId == spId && i.CreatedAt >= a.AttemptedAt)
                    .OrderByDescending(i => i.CreatedAt).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(ct);
            var before = a.Outcome;
            if (a.IntentId is Guid iid)
            {
                var intent = await _platform.RefreshForPayerAsync(payer, iid, ct);
                if (intent != null)
                {
                    a.Outcome = PlatformAutoChargePolicy.OutcomeOf(intent.Status, !string.IsNullOrWhiteSpace(intent.AuthorizeUrl), intent.FailureCode);
                    if (a.Outcome == PlatformAutoChargePolicy.OutcomeFailed) a.Detail = Trunc(intent.FailureMessage ?? a.Detail, 500);
                }
            }
            else if ((now - a.AttemptedAt).TotalMinutes > StaleClaimMinutes)
            {
                a.Outcome = PlatformAutoChargePolicy.OutcomeFailed;
                a.Detail = "ระบบหยุดกลางคันก่อนเริ่มตัด (ไม่มีรายการชำระ) — ลองใหม่ตามรอบถัดไป";
            }
            if (a.Outcome == before) continue;
            if (!PlatformAutoChargePolicy.InFlight(a.Outcome)) a.FinishedAt = now;
            a.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            var number = await _db.Documents.AsNoTracking().Where(d => d.Id == a.DocumentId && d.CompanyId == pid)
                .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct);
            await NotifyAsync(payer, a.DocumentId, number, a, retryDays, ct);
        }
    }

    /// <summary>แจ้งผู้เช่าตามผล — คีย์ต่อ (ใบ · ครั้งที่ · ผล) ⇒ ส่งครั้งเดียว · รอผลผู้ให้บริการ = ยังไม่แจ้ง</summary>
    private async Task NotifyAsync(Guid payer, Guid documentId, string? number, PlatformAutoChargeAttempt a, IReadOnlyList<int> retryDays,
        CancellationToken ct)
    {
        var no = number ?? "ค่าบริการ";
        (string Subject, string Body)? msg = a.Outcome switch
        {
            PlatformAutoChargePolicy.OutcomeSucceeded => ($"ตัดบัตรค่าบริการ NextAcc สำเร็จ — {no}",
                $"ระบบตัดบัตรที่บันทึกไว้ {a.Amount:N2} บาท สำหรับใบแจ้งหนี้ {no} เรียบร้อยแล้ว ใบเสร็จจะออกในศูนย์บิลเมื่อระบบยืนยันการชำระ"),
            PlatformAutoChargePolicy.OutcomeRequiresAction or PlatformAutoChargePolicy.OutcomeActionRequired => ($"ธนาคารขอยืนยันตัวตน — ชำระใบแจ้งหนี้ {no} ด้วยตนเอง",
                $"การตัดบัตรอัตโนมัติ {a.Amount:N2} บาท ต้องยืนยันตัวตนกับธนาคาร (3-D Secure) ซึ่งทำแทนท่านไม่ได้ — กรุณาชำระใบนี้ที่ศูนย์บิล"),
            PlatformAutoChargePolicy.OutcomeFailed => ($"ตัดบัตรค่าบริการ NextAcc ไม่สำเร็จ — {no}",
                $"ตัดบัตรครั้งที่ {a.AttemptNo} จำนวน {a.Amount:N2} บาท ไม่สำเร็จ: {a.Detail ?? "ผู้ให้บริการปฏิเสธรายการ"}\n"
                + (a.AttemptNo < PlatformAutoChargePolicy.MaxAttempts(retryDays)
                    ? "ระบบจะลองตัดอีกครั้งตามรอบ · หรือชำระด้วยตนเองที่ศูนย์บิล/เปลี่ยนบัตรตั้งต้น"
                    : "ระบบหยุดตัดอัตโนมัติสำหรับใบนี้แล้ว — กรุณาชำระที่ศูนย์บิลก่อนพ้นระยะผ่อนผัน")),
            _ => null,
        };
        if (msg is not { } m) return;
        await _notifier.SendAsync(payer, documentId, PlatformAutoChargePolicy.NoticeKey(documentId, a.AttemptNo, a.Outcome), m.Subject, m.Body, ct);
    }

    public async Task<IReadOnlyList<AdminAutoChargeAttemptRow>> RecentAttemptsAsync(int take, CancellationToken ct = default)
    {
        if (await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct) is not Guid pid) return Array.Empty<AdminAutoChargeAttemptRow>();
        var rows = await _db.PlatformAutoChargeAttempts.AsNoTracking().Where(a => a.PlatformCompanyId == pid)
            .OrderByDescending(a => a.AttemptedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        var payers = rows.Select(r => r.PayerCompanyId).Distinct().ToList();
        var docs = rows.Select(r => r.DocumentId).Distinct().ToList();
        var names = await _db.Companies.AsNoTracking().Where(c => payers.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var numbers = await _db.Documents.AsNoTracking().Where(d => d.CompanyId == pid && docs.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.DocumentNumber, ct);
        return rows.Select(r => new AdminAutoChargeAttemptRow(r.Id, r.PayerCompanyId, names.GetValueOrDefault(r.PayerCompanyId), r.DocumentId,
            numbers.GetValueOrDefault(r.DocumentId), r.AttemptNo, r.Amount, r.Outcome, PlatformAutoChargePolicy.OutcomeLabel(r.Outcome), r.Detail,
            r.AttemptedAt, r.IntentId)).ToList();
    }

    private static string? Trunc(string? s, int max) => s == null ? null : (s.Length > max ? s[..max] : s);
}

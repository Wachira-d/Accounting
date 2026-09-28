using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Journal;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>คำขอบันทึก "เงินที่ผู้ให้บริการโอนเข้าธนาคาร" 1 รอบ</summary>
public sealed record RecordSettlementRequest(
    string ProviderCode,
    DateTime FromDate,
    DateTime ToDate,
    // ยอดสุทธิที่เข้าบัญชีจริงตามสเตทเมนต์ — **ตัวตั้ง** ของการตรวจ
    decimal ActualNetReceived,
    DateTime SettledAt,
    string SettlementRef,
    Guid BankAccountId);

/// <summary>ผลของการดูตัวอย่าง/บันทึก — โครงเดียวกันทั้งสองเส้นเพื่อให้หน้าเว็บวาดที่เดียว</summary>
public sealed record SettlementOutcome(
    bool Ok,
    string? Message,
    SettlementPlan Plan,
    Guid? JournalEntryId,
    string? JournalEntryNumber);

/// <summary>รายการค้างโอน 1 แถว — ตัวเลขทุกช่องมาจาก <see cref="GatewaySettlementMath.Contribution"/> ตัวเดียวกับแผน JE</summary>
public sealed record PendingSettlementItem(
    Guid Id, string ProviderCode, DateTime? ConfirmedAt, decimal Amount, decimal RefundedAmount,
    decimal Clearing, decimal Fee, decimal FeeVat, bool FeeIsEstimated, decimal Net,
    bool IsRefundAfterSettlement, string? ProviderRef, string SourceKind, string Status);

/// <summary>ภาพรวมรายการค้างโอนของผู้ให้บริการหนึ่งราย</summary>
public sealed record PendingSettlementView(
    int Count, decimal Gross, decimal Fee, decimal ExpectedNet, bool AnyFeeEstimated,
    string FeeVatMode, int LegacyRefundedCount, IReadOnlyList<PendingSettlementItem> Items);

/// <summary>ผลการแก้ค่าธรรมเนียมจริงรายรายการ (รอบ 198 G-6)</summary>
public sealed record FeeCorrectionOutcome(bool Ok, string Message, decimal? OldFee, decimal? NewFee);

public interface IGatewaySettlementService
{
    /// <summary>รายการ "รับเงินแล้ว แต่ยังไม่เข้าธนาคาร" — เกณฑ์เลือกชุดเดียวกับแผน JE</summary>
    Task<PendingSettlementView> ListPendingAsync(Guid companyId, string? providerCode,
        CancellationToken ct = default);

    /// <summary>ดูตัวอย่างก่อนบันทึก — ไม่เขียนอะไรเลย</summary>
    Task<SettlementOutcome> PreviewAsync(Guid companyId, RecordSettlementRequest req,
        CancellationToken ct = default);

    /// <summary>บันทึกจริง: ลง JE + มาร์กรายการว่าโอนเข้าแล้ว (atomic)</summary>
    Task<SettlementOutcome> RecordAsync(Guid companyId, RecordSettlementRequest req, string actor,
        CancellationToken ct = default);

    /// <summary>แก้ค่าธรรมเนียมจริงของรายการที่ยังไม่บันทึกรอบโอน — บังคับเหตุผล + audit (hash chain)</summary>
    Task<FeeCorrectionOutcome> CorrectFeeAsync(Guid companyId, Guid intentId, decimal feeActual, string reason,
        string actor, string? actorEmail, string? ipAddress, CancellationToken ct = default);
}

/// <summary>
/// **ขั้น "เงินเข้าธนาคารจริง" ของ payment gateway** (PAYMENT_GATEWAY_DESIGN.md §4.5, §5)
///
/// ═══ ทำไมต้องแยกเป็นอีกขั้น ═══
/// ตอนลูกค้าจ่ายสำเร็จ เงิน<b>ยังไม่เข้าบัญชีเรา</b> ⇒ ระบบลง
/// <c>Dr 11340 ลูกหนี้ผู้ให้บริการรับชำระเงิน / Cr ลูกหนี้การค้า</c> ไว้ก่อน ·
/// ถ้าลง Dr ธนาคารตั้งแต่ตอนนั้น <b>ยอดธนาคารในระบบจะไม่ตรงกับสเตทเมนต์ตลอดเวลา</b>
/// และผู้ทำบัญชีจะกระทบยอดไม่ได้เลย · ขั้นนี้คือขั้นที่ล้าง 11340 ออกด้วยเงินจริง
///
/// ═══ ทำไม "บันทึกด้วยมือจากสเตทเมนต์" ถึงเป็นเส้นหลัก (ไม่ใช่ดึงอัตโนมัติ) ═══
/// ตัวตั้งของขั้นนี้คือ<b>ยอดที่เข้าบัญชีธนาคารจริง</b> ซึ่งผู้ใช้เห็นจากสเตทเมนต์/แดชบอร์ด
/// ของผู้ให้บริการ · ระบบเอายอดนั้นมา<b>ตรวจ</b>กับผลรวมที่คำนวณได้ แล้ว<b>บล็อกเมื่อไม่ตรง</b>
/// — ปลอดภัยกว่าการเชื่อ API รายงานยอดโอนซึ่งแต่ละเจ้าคืนโครงต่างกันและเราตรวจสอบไม่ได้
/// <para>การดึงอัตโนมัติเป็นงานของ <c>IPaymentProvider.ListSettlementsAsync</c> ในอนาคต
/// (ยังไม่มีเจ้าไหน implement — ดู PAYMENT_GATEWAY_DESIGN.md §7.1 backlog)</para>
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item>คณิตทั้งหมดอยู่ใน <see cref="GatewaySettlementMath"/> (บริสุทธิ์ + มีเทสต์) —
///   ที่นี่ทำแค่ "หาข้อมูลให้กติกา แล้วลงมือตามที่กติกาบอก"</item>
/// <item>ล็อกต่อ (บริษัท, provider) — การเลือกรายการที่ยังไม่โอนแล้วมาร์กทีหลังเป็น
///   read-modify-write · สองคนกดพร้อมกันจะลง JE ซ้ำ ⇒ ธนาคารเกินสองเท่า</item>
/// <item>ไม่มี "ปัดให้ลงตัว" — ไม่ตรงคือบล็อกพร้อมบอกว่าต่างเท่าไร</item>
/// <item>รอบ 198: JE ผ่าน <see cref="JournalEntryBuilder"/> (Dr=Cr · ด่านงวดปิด G-5 ตัวเดียวกับพรีวิว) ·
///   คืนบางส่วนนับยอดสุทธิหลังคืน (G-2) · VAT ค่าธรรมเนียม → 11630 (G-3) · WHT ฐานก่อน VAT + บล็อกจนกว่าจะออก 50 ทวิ ได้ (G-4)</item>
/// </list>
/// </summary>
public class GatewaySettlementService : IGatewaySettlementService
{
    /// <summary>11630 ภาษีซื้อรอเครดิต — VAT ค่าธรรมเนียมที่ยังไม่มีใบกำกับของผู้ให้บริการ</summary>
    public const string FeeInputVatDeferredCode = "11630";

    private readonly AccountingDbContext _db;
    private readonly IGatewayAccountResolver _accounts;
    private readonly ILogger<GatewaySettlementService> _logger;

    public GatewaySettlementService(AccountingDbContext db, IGatewayAccountResolver accounts,
        ILogger<GatewaySettlementService> logger)
    { _db = db; _accounts = accounts; _logger = logger; }

    public async Task<PendingSettlementView> ListPendingAsync(Guid companyId, string? providerCode,
        CancellationToken ct = default)
    {
        var configs = await _db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted
                && (providerCode == null || c.ProviderCode == providerCode))
            .ToListAsync(ct);
        GatewayFeeVatMode ModeOf(string code)
            => configs.FirstOrDefault(c => c.ProviderCode == code)?.FeeVatMode ?? GatewayFeeVatMode.None;

        var (unsettled, refundedAfter) = await SelectCandidatesAsync(companyId, providerCode, null, null, ct);
        var items = new List<PendingSettlementItem>();
        foreach (var (intent, input) in unsettled.Select(i => (i, ToInput(i, false)))
                     .Concat(refundedAfter.Select(i => (i, ToInput(i, true)))))
        {
            var c = GatewaySettlementMath.Contribution(input, ModeOf(intent.ProviderCode));
            items.Add(new PendingSettlementItem(intent.Id, intent.ProviderCode, intent.ConfirmedAt, intent.Amount,
                intent.RefundedAmount, c.Clearing, c.FeeDeducted, c.FeeVat,
                !input.AlreadySettled && intent.FeeActual == null,
                c.Net, input.AlreadySettled, intent.ProviderRef, intent.SourceKind.ToString(), intent.Status.ToString()));
        }

        // คืนเงินก่อนระบบเริ่มบันทึกยอดคืน (สถานะคืนแล้วแต่ยอดคืน = 0) — ไม่รู้ว่าคืนไปเท่าไร ⇒ ไม่นับในรอบโอน
        // และต้องบอกให้คนตรวจมือ (ห้ามเดาว่าคืนเต็ม/ห้ามนับยอดเต็ม)
        var legacy = await _db.PaymentIntents.AsNoTracking()
            .CountAsync(i => i.CompanyId == companyId
                && (providerCode == null || i.ProviderCode == providerCode)
                && i.SettlementJournalEntryId == null
                && (i.Status == PaymentIntentStatus.Refunded || i.Status == PaymentIntentStatus.PartiallyRefunded)
                && i.RefundedAmount == 0m, ct);

        var ordered = items.OrderBy(i => i.ConfirmedAt).ToList();
        return new PendingSettlementView(
            ordered.Count,
            R(ordered.Sum(i => i.Clearing)),
            R(ordered.Sum(i => i.Fee)),
            R(ordered.Sum(i => i.Net)),
            ordered.Any(i => i.FeeIsEstimated),
            providerCode == null ? "" : ModeOf(providerCode).ToString(),
            legacy,
            ordered);
    }

    public async Task<SettlementOutcome> PreviewAsync(Guid companyId, RecordSettlementRequest req,
        CancellationToken ct = default)
    {
        var (plan, _, _) = await BuildPlanAsync(companyId, req, ct);
        return new SettlementOutcome(plan.Ok, plan.Message, plan, null, null);
    }

    public async Task<SettlementOutcome> RecordAsync(Guid companyId, RecordSettlementRequest req,
        string actor, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // ล็อกต้องอยู่ **ก่อน** การเลือกรายการ — ไม่งั้นสองคนเลือกชุดเดียวกันได้
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.GatewaySettlement,
                    req.ProviderCode) }, ct);

            var (plan, unsettled, refundedAfter) = await BuildPlanAsync(companyId, req, ct);
            if (!plan.Ok)
            {
                await tx.RollbackAsync(ct);
                return new SettlementOutcome(false, plan.Message, plan, null, null);
            }

            var config = await _db.PaymentProviderConfigs
                .FirstOrDefaultAsync(c => c.CompanyId == companyId
                    && c.ProviderCode == req.ProviderCode && !c.IsDeleted, ct);

            var accounts = await ResolveAccountsAsync(companyId, config, req.ProviderCode, req.BankAccountId, plan, ct);
            if (accounts.Error != null)
            {
                await tx.RollbackAsync(ct);
                return new SettlementOutcome(false, accounts.Error, plan, null, null);
            }

            var entryDate = ThaiDate.CalendarDateUtc(req.SettledAt);
            var builder = JournalEntryBuilder.For(_db, companyId, entryDate)
                .Type(JournalType.CashReceipts)
                .NumberPrefix("RV")
                .Description($"รับโอนจากผู้ให้บริการรับชำระเงิน ({req.ProviderCode}) {req.SettlementRef}")
                .Reference(req.SettlementRef)
                .CreatedBy(actor);
            foreach (var line in plan.Lines)
            {
                if (line.Debit > 0m) builder.Debit(accounts.Map[line.Role], line.Debit, line.Description);
                if (line.Credit > 0m) builder.Credit(accounts.Map[line.Role], line.Credit, line.Description);
            }

            JournalEntry je;
            try
            {
                // ด่านงวดปิด (G-5) + Dr=Cr อยู่ใน builder — ตัวเดียวกับที่พรีวิวถาม ⇒ พรีวิวผ่านแต่ลงไม่ได้
                // เกิดได้แค่เมื่อมีคนปิดงวดระหว่างสองจังหวะ ซึ่งต้องคืนเป็นข้อความถึงผู้ใช้ ไม่ใช่ 500
                je = await builder.PostAsync(actor, ct);
            }
            catch (InvalidOperationException ex)
            {
                await tx.RollbackAsync(ct);
                return new SettlementOutcome(false, ex.Message,
                    GatewaySettlementMath.Block(plan, SettlementBlockReason.PeriodClosed, ex.Message), null, null);
            }

            var feeVatMode = config?.FeeVatMode ?? GatewayFeeVatMode.None;
            foreach (var intent in unsettled)
            {
                // ยอดที่ "โอนเข้าจริง" ของแต่ละรายการ = ยอดหลังคืน หักค่าธรรมเนียม (+VAT ถ้าหักแยก) ของตัวเอง
                // (เก็บรายรายการเพื่อให้รายงานกระทบยอดตรวจย้อนได้ว่ารายการไหนโอนแล้ว)
                var c = GatewaySettlementMath.Contribution(ToInput(intent, false), feeVatMode);
                var fee = intent.FeeActual ?? intent.FeeEstimated;
                intent.SettledAmount = c.Net;
                intent.SettledAt = req.SettledAt;
                intent.SettlementRef = req.SettlementRef;
                intent.SettlementJournalEntryId = je.Id;
                intent.RefundSettledAmount = intent.RefundedAmount;
                // ค่าธรรมเนียมที่ยังเป็นตัวประมาณ ถูกยืนยันด้วยยอดโอนจริงแล้ว
                intent.FeeActual ??= fee;
                intent.UpdatedAt = DateTime.UtcNow;
                AddEvent(companyId, intent,
                    $"บันทึกเงินโอนเข้าธนาคาร {req.SettlementRef} สุทธิ {c.Net:N2} "
                    + $"(ยอดหลังคืน {c.Clearing:N2} · ค่าธรรมเนียม {c.FeeDeducted:N2}) · JE {je.EntryNumber} โดย {actor}");
            }
            foreach (var intent in refundedAfter)
            {
                var deducted = intent.RefundedAmount - intent.RefundSettledAmount;
                intent.RefundSettledAmount = intent.RefundedAmount;
                intent.UpdatedAt = DateTime.UtcNow;
                AddEvent(companyId, intent,
                    $"ยอดคืนเงินหลังรอบโอนก่อน {deducted:N2} ถูกหักในรอบโอน {req.SettlementRef} · JE {je.EntryNumber} โดย {actor}");
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "บันทึกเงินโอนเข้าจาก gateway {Provider} บริษัท {Company}: {Count} รายการ "
                + "ยอด {Gross:N2} ค่าธรรมเนียม {Fee:N2} VAT {Vat:N2} สุทธิ {Net:N2} → JE {Je}",
                req.ProviderCode, companyId, plan.IntentIds.Count, plan.Gross, plan.FeeNetPaid, plan.FeeVat,
                plan.ExpectedNet, je.EntryNumber);

            return new SettlementOutcome(true,
                $"บันทึกเงินโอนเข้า {plan.IntentIds.Count} รายการแล้ว — ใบสำคัญ {je.EntryNumber}",
                plan, je.Id, je.EntryNumber);
        });
    }

    public async Task<FeeCorrectionOutcome> CorrectFeeAsync(Guid companyId, Guid intentId, decimal feeActual,
        string reason, string actor, string? actorEmail, string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new FeeCorrectionOutcome(false,
                "กรุณาระบุเหตุผล — เช่น \"ตามสเตทเมนต์ผู้ให้บริการรอบโอน …\" (ผู้สอบบัญชีต้องเห็นว่าแก้เพราะอะไร)",
                null, null);
        if (feeActual < 0m)
            return new FeeCorrectionOutcome(false, "ค่าธรรมเนียมต้องไม่ติดลบ", null, null);

        var intent = await _db.PaymentIntents
            .FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);
        if (intent == null)
            return new FeeCorrectionOutcome(false, "ไม่พบรายการชำระเงิน", null, null);
        if (intent.SettlementJournalEntryId != null)
            return new FeeCorrectionOutcome(false,
                "รายการนี้บันทึกรอบโอนไปแล้ว — ค่าธรรมเนียมอยู่ในใบสำคัญแล้ว แก้ที่นี่ไม่ได้ "
                + "(ต้องกลับรายการใบสำคัญรอบโอนก่อน)", intent.FeeActual, null);
        if (!PaymentIntentPolicy.IsSettledPositive(intent.Status))
            return new FeeCorrectionOutcome(false,
                "แก้ค่าธรรมเนียมได้เฉพาะรายการที่รับเงินสำเร็จแล้ว", intent.FeeActual, null);

        var fee = R(feeActual);
        if (fee > intent.Amount)
            return new FeeCorrectionOutcome(false,
                $"ค่าธรรมเนียม ({fee:N2}) มากกว่ายอดที่รับ ({intent.Amount:N2}) — ตรวจตัวเลขอีกครั้ง",
                intent.FeeActual, null);

        var old = intent.FeeActual;
        intent.FeeActual = fee;
        intent.UpdatedAt = DateTime.UtcNow;
        AddEvent(companyId, intent,
            $"แก้ค่าธรรมเนียมจริง {(old.HasValue ? old.Value.ToString("N2") : "(ยังไม่มี)")} → {fee:N2} โดย {actor} · {reason.Trim()}");

        // เงินที่ลงบัญชีขึ้นกับค่านี้ ⇒ ต้องอยู่ใน hash chain (ผู้สอบบัญชีถามเสมอว่าใครแก้ เพราะอะไร)
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            EntityType = nameof(PaymentIntent),
            EntityId = intent.Id.ToString(),
            Action = AuditAction.Update,
            UserEmail = actorEmail,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { feeActual = old }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "gateway-fee-correction",
                feeActual = fee,
                reason = reason.Trim(),
                actor,
            }),
            IpAddress = ipAddress,
            Timestamp = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        return new FeeCorrectionOutcome(true, $"แก้ค่าธรรมเนียมเป็น {fee:N2} แล้ว — กดดูตัวอย่างรอบโอนใหม่อีกครั้ง",
            old, fee);
    }

    /// <summary>เลือกรายการที่เข้าเงื่อนไข — ตัวเดียวของหน้ารายการค้างโอนและแผน JE
    /// (<paramref name="from"/>/<paramref name="to"/> = null ⇒ ทั้งหมด)</summary>
    private async Task<(List<PaymentIntent> Unsettled, List<PaymentIntent> RefundedAfter)> SelectCandidatesAsync(
        Guid companyId, string? providerCode, DateTime? from, DateTime? to, CancellationToken ct)
    {
        // ยังไม่เคยบันทึกรอบโอน: สำเร็จ · หรือคืนแล้ว (บางส่วน/เต็ม) ที่ระบบรู้ยอดคืน (RefundedAmount > 0)
        // — คืนเต็มนับ 0 แต่ค่าธรรมเนียมยังถูกหักในรอบโอน (G-2 · เดิมกรองแค่ Succeeded ⇒ คืนบางส่วน = บล็อกถาวร)
        var unsettledQ = _db.PaymentIntents
            .Where(i => i.CompanyId == companyId
                && (providerCode == null || i.ProviderCode == providerCode)
                && i.SettlementJournalEntryId == null
                && i.ConfirmedAt != null
                && (i.Status == PaymentIntentStatus.Succeeded
                    || ((i.Status == PaymentIntentStatus.PartiallyRefunded || i.Status == PaymentIntentStatus.Refunded)
                        && i.RefundedAmount > 0m)));
        if (from is DateTime f) unsettledQ = unsettledQ.Where(i => i.ConfirmedAt >= f);
        if (to is DateTime t) unsettledQ = unsettledQ.Where(i => i.ConfirmedAt < t);
        var unsettled = await unsettledQ.OrderBy(i => i.ConfirmedAt).ToListAsync(ct);

        // บันทึกรอบโอนไปแล้ว แต่คืนเงินภายหลัง — ผู้ให้บริการหักยอดคืนจากรอบโอนถัดไป
        var afterQ = _db.PaymentIntents
            .Where(i => i.CompanyId == companyId
                && (providerCode == null || i.ProviderCode == providerCode)
                && i.SettlementJournalEntryId != null
                && i.RefundedAmount > i.RefundSettledAmount);
        if (to is DateTime t2) afterQ = afterQ.Where(i => i.LastRefundedAt == null || i.LastRefundedAt < t2);
        var refundedAfter = await afterQ.OrderBy(i => i.LastRefundedAt).ToListAsync(ct);

        return (unsettled, refundedAfter);
    }

    private static SettlementIntentInput ToInput(PaymentIntent i, bool alreadySettled)
        => new(i.Id, i.Amount, i.FeeActual, i.FeeEstimated, i.RefundedAmount, alreadySettled, i.RefundSettledAmount);

    /// <summary>เลือกรายการที่เข้าเงื่อนไข แล้วให้ <see cref="GatewaySettlementMath"/> ตัดสิน + ด่านงวดปิด (G-5)</summary>
    private async Task<(SettlementPlan Plan, List<PaymentIntent> Unsettled, List<PaymentIntent> RefundedAfter)> BuildPlanAsync(
        Guid companyId, RecordSettlementRequest req, CancellationToken ct)
    {
        var from = ThaiDate.CalendarDateUtc(req.FromDate);
        var to = ThaiDate.CalendarDateUtc(req.ToDate).AddDays(1);   // ปลายช่วงแบบ exclusive

        var (unsettled, refundedAfter) = await SelectCandidatesAsync(companyId, req.ProviderCode, from, to, ct);

        var config = await _db.PaymentProviderConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId
                && c.ProviderCode == req.ProviderCode && !c.IsDeleted, ct);
        var vatRegistered = await CompanyVatStatus.IsRegisteredAsync(_db, companyId, ct);

        var inputs = unsettled.Select(i => ToInput(i, false))
            .Concat(refundedAfter.Select(i => ToInput(i, true)))
            .ToList();
        var plan = GatewaySettlementMath.Plan(
            inputs,
            req.ActualNetReceived,
            config?.WhtOnFee ?? GatewayFeeWhtMode.None,
            req.SettlementRef,
            config?.FeeVatMode ?? GatewayFeeVatMode.None,
            vatRegistered);

        if (plan.Ok)
        {
            // ด่านงวดปิด (G-5) — ตัวเดียวกับที่ JournalEntryBuilder.PostAsync ใช้ ⇒ พรีวิวบอกก่อนกดบันทึก
            var closed = await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId,
                ThaiDate.CalendarDateUtc(req.SettledAt), ct);
            if (closed != null)
                plan = GatewaySettlementMath.Block(plan, SettlementBlockReason.PeriodClosed, closed);
        }

        return (plan, unsettled, refundedAfter);
    }

    private sealed record AccountResolution(
        Dictionary<SettlementLineRole, Guid> Map, string? Error);

    /// <summary>หาผังบัญชีของแต่ละบทบาท — ขาดตัวไหนต้องบอก<b>ชื่อผังที่ขาด + ที่ตั้งค่า</b>
    /// ไม่ใช่ "เกิดข้อผิดพลาด" (ผู้ใช้ต้องรู้ว่าไปตั้งตรงไหน)</summary>
    private async Task<AccountResolution> ResolveAccountsAsync(Guid companyId,
        PaymentProviderConfig? config, string providerCode, Guid bankAccountId, SettlementPlan plan,
        CancellationToken ct)
    {
        var map = new Dictionary<SettlementLineRole, Guid>();

        var bankGl = await _db.Set<BankAccount>().AsNoTracking()
            .Where(b => b.Id == bankAccountId && b.CompanyId == companyId && !b.IsDeleted)
            .Select(b => b.LinkedAccountId)
            .FirstOrDefaultAsync(ct);
        if (bankGl is not Guid bankGlId)
            return new AccountResolution(map,
                "บัญชีธนาคารที่เลือกยังไม่ได้ผูกกับผังบัญชี — ไปที่ ตั้งค่า → บัญชีธนาคาร "
                + "แล้วเลือกผังบัญชีของธนาคารนี้ก่อน (ไม่งั้นเงินเข้าจะลงผังผิด)");
        map[SettlementLineRole.Bank] = bankGlId;

        // บัญชีพัก: ตัวตัดสินเดียวกับขา "เงินเข้า" และ JE คืนเงิน — ล้างผังเดียวกับที่ลงไว้เสมอ
        var clearingId = await _accounts.ResolveClearingAccountAsync(companyId, providerCode, config?.Id, ct);
        if (clearingId is not Guid cid)
            return new AccountResolution(map,
                "ยังไม่ได้ตั้ง \"บัญชีพักเงินผู้ให้บริการรับชำระเงิน\" (แนะนำ 11340) — "
                + "ตั้งได้ที่หน้า ตั้งค่าการรับชำระเงินออนไลน์");
        map[SettlementLineRole.Clearing] = cid;

        if (plan.Lines.Any(l => l.Role == SettlementLineRole.FeeExpense))
        {
            var feeId = config?.FeeExpenseAccountId
                ?? (await FindByCodeAsync(companyId, "54710", ct))?.Id;
            if (feeId is not Guid fid)
                return new AccountResolution(map,
                    "ยังไม่ได้ตั้งผังบัญชี \"ค่าธรรมเนียมรับชำระเงิน\" (แนะนำ 54710 ค่าธรรมเนียมธนาคาร) — "
                    + "ตั้งได้ที่หน้า ตั้งค่าการรับชำระเงินออนไลน์");
            map[SettlementLineRole.FeeExpense] = fid;
        }

        if (plan.Lines.Any(l => l.Role == SettlementLineRole.FeeInputVatDeferred))
        {
            var vatAcc = await FindByCodeAsync(companyId, FeeInputVatDeferredCode, ct);
            if (vatAcc == null)
                return new AccountResolution(map,
                    "ไม่พบผังบัญชี 11630 ภาษีซื้อรอเครดิต (ที่พัก VAT ของค่าธรรมเนียมจนกว่าจะได้ใบกำกับ) — "
                    + "เพิ่มในผังบัญชีก่อน หรือเปลี่ยน \"VAT ของค่าธรรมเนียม\" เป็น \"ไม่แยก\" ที่หน้าตั้งค่าการรับชำระเงินออนไลน์");
            map[SettlementLineRole.FeeInputVatDeferred] = vatAcc.Id;
        }

        if (plan.Lines.Any(l => l.Role == SettlementLineRole.WhtPayable))
        {
            // ผู้ให้บริการรับชำระเงินเป็นนิติบุคคล ⇒ ภ.ง.ด.53 (21917) · ปัจจุบันแผนที่มี WHT ถูกบล็อกก่อนถึงตรงนี้ (G-4)
            var whtAcc = await FindByCodeAsync(companyId, "21917", ct);
            if (whtAcc == null)
                return new AccountResolution(map,
                    "ไม่พบผังบัญชี 21917 ภาษีหัก ณ ที่จ่าย - ภ.ง.ด.53 — "
                    + "เพิ่มในผังบัญชีก่อน หรือปิดตัวเลือก \"หัก ณ ที่จ่ายค่าธรรมเนียม\"");
            map[SettlementLineRole.WhtPayable] = whtAcc.Id;
        }

        return new AccountResolution(map, null);
    }

    private void AddEvent(Guid companyId, PaymentIntent intent, string note)
        => _db.PaymentIntentEvents.Add(new PaymentIntentEvent
        {
            CompanyId = companyId,
            IntentId = intent.Id,
            At = DateTime.UtcNow,
            Source = PaymentEventSource.Manual,
            FromStatus = intent.Status,
            ToStatus = intent.Status,
            Note = note,
        });

    private Task<ChartOfAccount?> FindByCodeAsync(Guid companyId, string code, CancellationToken ct)
        => _db.ChartOfAccounts.FirstOrDefaultAsync(
            a => a.CompanyId == companyId && a.AccountCode == code && !a.IsDeleted, ct);

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// การรับชำระเงินผ่าน gateway — **ทางเข้าเดียวของทุกช่องทาง**
///
/// <para>ไม่มีชื่อผู้ให้บริการในไฟล์นี้เลย (บังคับด้วย
/// <c>tools/payment_provider_boundary_check.py</c>) — เปลี่ยน/เพิ่มเจ้าใหม่ = เพิ่ม
/// adapter ใน <c>Services/Payments/Providers/</c> อย่างเดียว</para>
///
/// <para>รอบ 198 (G-8): ทุก endpoint ที่เขียนมีด่านสิทธิ์จาก <see cref="PaymentGatewayPermissionScope"/> (คืนเงิน = สั่งโอนเงินออก ·
/// ยืนยันเงินเข้าด้วยมือ = กระทบยอดธนาคาร · รอบโอน/ค่าธรรมเนียม = ลง JE) + คืนเงิน/ยืนยันมือ/รอบโอนห้ามคีย์ API ·
/// ไฟล์นี้อยู่ใน WATCHED ของ <c>tools/write_permission_gate_check.py</c> แล้ว</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/pay")]
[Authorize]
public class PaymentGatewayController : ControllerBase
{
    private readonly IPaymentIntentService _intents;
    private readonly AccountingDbContext _db;
    private readonly Accounting.Services.Interfaces.IPermissionService _permissions;

    public PaymentGatewayController(IPaymentIntentService intents, AccountingDbContext db,
        Accounting.Services.Interfaces.IPermissionService permissions)
    { _intents = intents; _db = db; _permissions = permissions; }

    /// <summary>ด่านสิทธิ์ที่คีย์ขึ้นกับข้อมูลในคำขอ (attribute คงที่มองไม่เห็น) — null = ผ่าน</summary>
    private async Task<string?> DenyKeyAsync(Guid companyId, string key, string verb)
        => await _permissions.HasPermissionAsync(companyId, JwtHelper.GetUserIdFromClaims(User), key)
            ? null
            : $"ไม่มีสิทธิ์{verb} (ต้องการ {key.Replace("perm:", "")})";

    public sealed record CreateIntentRequest(
        PaymentSourceKind SourceKind, Guid SourceId, decimal Amount, PaymentMethodKind Method,
        string? Description, string? CustomerEmail, string? CustomerPhone,
        string? ReturnUrl, string? CardToken, Guid? SiteId, Guid? ContactId);

    /// <summary>ข้อมูลที่หน้าจ่ายเงินต้องใช้ — **ไม่มี secret key** และไม่มีชื่อเจ้า</summary>
    public sealed record IntentResponse(
        Guid Id, string Status, string Method, decimal Amount, string Currency,
        string? QrPayload, DateTime? QrExpiresAt, string? AuthorizeUrl,
        string? FailureMessage, bool IsTestMode);

    private static IntentResponse Map(PaymentIntent i, bool testMode) => new(
        i.Id, i.Status.ToString(), i.MethodKind.ToString(), i.Amount, i.Currency,
        i.QrPayload, i.QrExpiresAt, i.AuthorizeUrl, i.FailureMessage, testMode);

    private async Task<bool> IsTestModeAsync(PaymentIntent i, CancellationToken ct)
        => i.ProviderConfigId is Guid cid
           && await _db.PaymentProviderConfigs.AsNoTracking()
               .AnyAsync(c => c.Id == cid && c.Mode == PaymentProviderMode.Test, ct);

    [HttpPost("intents")]
    public async Task<ActionResult<ApiResponse<IntentResponse>>> Create(
        Guid companyId, [FromBody] CreateIntentRequest req, CancellationToken ct)
    {
        // สิทธิ์ของโมดูลต้นทาง (ใบแจ้งหนี้ · ออเดอร์เว็บ · การจอง · บิล POS · ค่าบริการ) — ลูกค้าปลายทางที่ไม่ล็อกอิน
        // เดินทาง PublicPaymentController ไม่ใช่ที่นี่
        var deny = await DenyKeyAsync(companyId, PaymentGatewayPermissionScope.StartKeyFor(req.SourceKind),
            "เริ่มรับชำระเงินของรายการนี้");
        if (deny != null)
            return StatusCode(403, new ApiResponse<IntentResponse>(false, null, deny));
        try
        {
            var intent = await _intents.StartAsync(companyId, new StartPaymentRequest(
                req.SourceKind, req.SourceId, req.Amount, req.Method, req.Description,
                req.CustomerEmail, req.CustomerPhone, req.ReturnUrl, req.CardToken,
                req.SiteId, req.ContactId), ct: ct);
            return Ok(new ApiResponse<IntentResponse>(true,
                Map(intent, await IsTestModeAsync(intent, ct))));
        }
        catch (InvalidOperationException ex)
        {
            // ข้อความจาก service เป็นภาษาไทยที่บอกทางแก้อยู่แล้ว — ส่งต่อตรง ๆ
            return BadRequest(new ApiResponse<IntentResponse>(false, null, ex.Message));
        }
    }

    /// <summary>สถานะปัจจุบัน — หน้าเว็บ poll ตัวนี้ระหว่างรอลูกค้าจ่าย
    ///
    /// <para>ถ้ายังเปิดอยู่จะ**ถามสถานะสด**จากผู้ให้บริการด้วย เพื่อให้ไม่ต้องรอ webhook
    /// (webhook หายเป็นเรื่องที่เกิดจริง — poll คือตาข่ายรับ)</para>
    ///
    /// <para>รอบ 200 ทีม G (G-8): เดิมมีแค่ <c>[Authorize]</c> ⇒ สมาชิกคนไหนก็ได้ (รวมบทบาทดูอย่างเดียว) อ่านยอด/สถานะรายการของทุกโมดูล และ
    /// <c>live=true</c> สั่งถามผู้ให้บริการ + <b>เปลี่ยนสถานะ</b> (ส่งต่อให้ต้นทางออกใบเสร็จ/ตัดหนี้) ได้ · ตอนนี้ต้องมีสิทธิ์เริ่มรับชำระของต้นทางนั้น
    /// (คนที่สร้าง QR ต้อง poll ได้) <b>หรือ</b>สิทธิ์ดูธนาคาร (นักบัญชีตรวจสถานะจากหน้ารายการ) — <see cref="PaymentGatewayPermissionScope.StatusKeysFor"/></para></summary>
    [HttpGet("intents/{intentId:guid}/status")]
    public async Task<ActionResult<ApiResponse<IntentResponse>>> Status(
        Guid companyId, Guid intentId, [FromQuery] bool live = true, CancellationToken ct = default)
    {
        var found = await _intents.FindAsync(companyId, intentId, ct);
        if (found == null) return NotFound(new ApiResponse<IntentResponse>(false, null, "ไม่พบรายการชำระเงิน"));
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var allowed = false;
        foreach (var key in PaymentGatewayPermissionScope.StatusKeysFor(found.SourceKind))
            if (await _permissions.HasPermissionAsync(companyId, userId, key)) { allowed = true; break; }
        if (!allowed)
            return StatusCode(403, new ApiResponse<IntentResponse>(false, null,
                PaymentGatewayPermissionScope.StatusDeniedMessage(found.SourceKind)));

        var intent = live ? await _intents.RefreshAsync(companyId, intentId, ct) : found;
        return Ok(new ApiResponse<IntentResponse>(true, Map(intent, await IsTestModeAsync(intent, ct))));
    }

    /// <summary>รายการชำระเงินของบริษัท — หน้าติดตาม/ตรวจสอบของผู้ดูแล · รอบ 200 ทีม G (G-8): สิทธิ์ดูธนาคาร (เดิมสมาชิกคนไหนก็เห็นยอด/สถานะคืนเงินทั้งบริษัท)</summary>
    [HttpGet("intents")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.ViewPayments)]
    public async Task<ActionResult<ApiResponse<object>>> List(
        Guid companyId, [FromServices] IGatewayRefundService refunds,
        [FromQuery] string? status, [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var q = _db.PaymentIntents.AsNoTracking().Where(i => i.CompanyId == companyId);
        if (Enum.TryParse<PaymentIntentStatus>(status, true, out var s))
            q = q.Where(i => i.Status == s);
        var rows = await q.OrderByDescending(i => i.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(i => new
            {
                i.Id, i.ProviderCode, SourceKindValue = i.SourceKind, sourceKind = i.SourceKind.ToString(), i.SourceId,
                i.Amount, StatusValue = i.Status, status = i.Status.ToString(), method = i.MethodKind.ToString(),
                i.ProviderRef, i.FailureMessage, i.ConfirmedAt, i.ConfirmedBy,
                i.FeeActual, i.SettledAt, i.CreatedAt,
                i.RefundedAmount, i.LastRefundedAt, i.LastRefundJournalEntryId,
                SettledByJournal = i.SettlementJournalEntryId != null,
                i.SettlementBatchId,
                i.RefundOutcomeUnknownSince,
                i.RefundOutcomeUnknownAmount,
            })
            .ToListAsync(ct);
        // A-GW4: intent ที่รอบโอน settlement เป็นเจ้าของ — "บันทึกรอบโอนแล้ว" เมื่อรอบโอนลงบัญชีแล้ว (ตัวตัดสินเดียวกับรายงานกระทบยอด)
        var batchFacts = await LoadBatchSettlementAsync(companyId,
            rows.Where(r => r.SettlementBatchId != null && !r.SettledByJournal).Select(r => (r.Id, r.SettlementBatchId!.Value)).ToList(), ct);
        var nowUtc = DateTime.UtcNow;

        // "คืนเงินแล้ว ยังไม่ออกใบลดหนี้" (§86/10 · รอบ 198 G-1) — ตัดสินที่เซิร์ฟเวอร์ตัวเดียว หน้าเว็บแค่ติดป้าย
        var cn = await refunds.CreditNoteStatesAsync(companyId,
            rows.Where(r => r.RefundedAmount > 0m).Select(r => r.Id).ToList(), ct);
        // R-E4: ป้ายช่อง "ค่าธรรมเนียม" ตามโหมด VAT ของผู้ให้บริการ — เซิร์ฟเวอร์ตัดสิน (AddedOnTop = ยอดก่อน VAT)
        var feeModes = await _db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => new { c.ProviderCode, c.FeeVatMode })
            .ToListAsync(ct);
        var shaped = rows.Select(r =>
        {
            cn.TryGetValue(r.Id, out var v);
            // คืนแล้วแต่ไม่มียอดคืน = คืนก่อนระบบบันทึกยอดคืน / ลงบัญชีคืนเงินไม่สำเร็จ ⇒ ต้องตรวจมือ (ไม่ใช่ "ครบ")
            var legacyRefund = r.RefundedAmount == 0m && (r.status == nameof(PaymentIntentStatus.Refunded)
                || r.status == nameof(PaymentIntentStatus.PartiallyRefunded));
            var isSettled = r.SettledByJournal || (batchFacts.TryGetValue(r.Id, out var bf) && bf.Posted);
            return new
            {
                r.Id, r.ProviderCode, r.sourceKind, r.SourceId, r.Amount, r.status, r.method,
                r.ProviderRef, r.FailureMessage, r.ConfirmedAt, r.ConfirmedBy, r.FeeActual, r.SettledAt,
                r.CreatedAt, r.RefundedAmount, r.LastRefundedAt, r.LastRefundJournalEntryId, isSettled,
                // รอบ 201 ทีม GW (A-GW2): ป้าย/ปุ่ม/ค้างนาน — เซิร์ฟเวอร์ตัดสิน (หน้าเว็บไม่มีสำเนาเกณฑ์อีก)
                statusLabel = PaymentIntentPolicy.StatusLabel(r.StatusValue),
                sourceKindLabel = PaymentIntentPolicy.SourceKindLabel(r.SourceKindValue),
                isStuck = PaymentIntentPolicy.IsStuck(r.StatusValue, r.CreatedAt, nowUtc),
                canCheckLive = PaymentIntentPolicy.IsOpen(r.StatusValue),
                // ปุ่มคืนเงิน = ด่านเดียวกับ service (GatewayRefundMath.Check ยอดเต็มที่เหลือ) — ผลไม่แน่ชัดมีปุ่มของตัวเอง
                canRefund = GatewayRefundMath.Check(r.StatusValue, r.Amount, r.RefundedAmount, null,
                    r.RefundOutcomeUnknownSince != null).Ok,
                canEditFee = PaymentIntentPolicy.CanEditFee(r.StatusValue, r.SettledByJournal, r.SettlementBatchId != null),
                inSettlementBatch = r.SettlementBatchId != null,
                refundableRemaining = GatewayRefundMath.Remaining(r.Amount, r.RefundedAmount),
                feeInputLabel = GatewaySettlementMath.FeeInputLabel(
                    feeModes.FirstOrDefault(m => m.ProviderCode == r.ProviderCode)?.FeeVatMode ?? GatewayFeeVatMode.None),
                creditNoteState = v?.State,
                needsCreditNote = v?.NeedsCreditNote ?? false,
                refundUntracked = legacyRefund,
                // E-2: คืนเงินผลไม่แน่ชัด — ล็อกคืนเพิ่ม · หน้าเว็บโชว์ปุ่ม "ตรวจผลการคืนเงิน" + ข้อความจากเซิร์ฟเวอร์
                refundOutcomeUnknown = r.RefundOutcomeUnknownSince != null,
                r.RefundOutcomeUnknownSince,
                // E2-3: ยอดของครั้งที่ผลไม่แน่ชัด — ค่าตั้งต้นของ "บันทึกผลด้วยมือ" (null = แถวเก่าที่ไม่รู้ยอด)
                r.RefundOutcomeUnknownAmount,
                refundOutcomeUnknownMessage = r.RefundOutcomeUnknownSince != null ? GatewayRefundMath.OutcomeUnknownMessage : null,
            };
        }).ToList();
        return Ok(new ApiResponse<object>(true, new
        {
            items = shaped,
            refundedAwaitingCreditNote = shaped.Count(x => x.needsCreditNote),
            refundUntracked = shaped.Count(x => x.refundUntracked),
            refundOutcomeUnknown = shaped.Count(x => x.refundOutcomeUnknown),
            stuck = shaped.Count(x => x.isStuck),
            stuckMinutes = (int)PaymentIntentPolicy.StuckThreshold.TotalMinutes,
            statusOptions = PaymentIntentPolicy.StatusOptions().Select(o => new { value = o.Value, label = o.Label }),
        }));
    }

    /// <summary>กระทบยอดเงินที่รับผ่าน gateway ของงวดหนึ่ง
    ///
    /// <para>สมการที่ต้องเป็นจริง: <c>Σ charge − Σ คืนเงิน − Σ ค่าธรรมเนียม =
    /// Σ ที่โอนเข้าจริง + ที่ยังไม่ถึงรอบโอน</c> · <b>ผลต่างที่อธิบายไม่ได้ต้องเป็น 0</b>
    /// — ไม่เป็นศูนย์แปลว่ามีเงินหายหรือค่าธรรมเนียมไม่ตรงที่คาด ต้องมีคนดู</para></summary>
    [HttpGet("reconciliation")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.ViewPayments)]
    public async Task<ActionResult<ApiResponse<object>>> Reconciliation(
        Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        // รอบ 200 ทีม G: ขอบช่วง = เที่ยงคืนเวลาไทย (ตัวเดียวกับแผนรอบโอน) — เดิม .Date ของเวลา UTC ⇒ เลื่อน 7 ชม.
        var fromLabel = ThaiDate.CalendarDateUtc(from ?? DateTime.UtcNow.AddDays(-30));
        var toLabel = ThaiDate.CalendarDateUtc(to ?? DateTime.UtcNow);
        var (fromDate, toDate) = GatewaySettlementMath.ConfirmedRangeUtc(fromLabel, toLabel);

        var raw = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId
                     && i.ConfirmedAt != null && i.ConfirmedAt >= fromDate && i.ConfirmedAt < toDate
                     && (i.Status == PaymentIntentStatus.Succeeded
                         || i.Status == PaymentIntentStatus.Refunded
                         || i.Status == PaymentIntentStatus.PartiallyRefunded))
            .Select(i => new
            {
                i.Id, i.ProviderCode, i.Amount, i.FeeActual, i.FeeEstimated, i.SettledAmount,
                IsRefundedFully = i.Status == PaymentIntentStatus.Refunded,
                SettledByJournal = i.SettlementJournalEntryId != null,
                i.SettlementBatchId,
                i.RefundedAmount, i.RefundSettledAmount, i.RefundDeductedAfterSettlement,
                i.SettledFeeDeducted,
            })
            .ToListAsync(ct);
        // รอบ 201 ทีม GW (A-GW4): intent ที่รอบโอน settlement (batch) เป็นเจ้าของ — โอนแล้วเมื่อรอบโอนลงบัญชีแล้ว · ยอดจากบรรทัดรอบโอน
        // (เดิม IsSettled = มีใบสำคัญรอบโอนเส้นเดิมเท่านั้น ⇒ batch ที่ลงบัญชีแล้วโชว์ "ยังไม่โอน" ตลอดไป)
        var batchFacts = await LoadBatchSettlementAsync(companyId,
            raw.Where(i => i.SettlementBatchId != null && !i.SettledByJournal).Select(i => (i.Id, i.SettlementBatchId!.Value)).ToList(), ct);
        // R-E5: โหมด VAT ค่าธรรมเนียมของผู้ให้บริการแต่ละราย — สูตรเดียวกับแผนรอบโอน (AddedOnTop = ถูกหักรวม VAT)
        var modes = await _db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => new { c.ProviderCode, c.FeeVatMode })
            .ToListAsync(ct);
        GatewayFeeVatMode ModeOf(string code)
            => modes.FirstOrDefault(m => m.ProviderCode == code)?.FeeVatMode ?? GatewayFeeVatMode.None;
        var intentRows = raw.Select(i => new GatewayReconciliationIntentRow(
                i.Amount, i.FeeActual, i.FeeEstimated, i.SettledAmount, i.IsRefundedFully, i.SettledByJournal,
                i.RefundedAmount, i.RefundSettledAmount, i.RefundDeductedAfterSettlement, ModeOf(i.ProviderCode),
                i.SettledFeeDeducted, OwnedByBatch: i.SettlementBatchId != null,
                BatchPosted: batchFacts.TryGetValue(i.Id, out var bp) && bp.Posted))
            .ToList();
        var rows = raw.Select((i, idx) => GatewayReconciliation.FromIntent(intentRows[idx],
            batchFacts.TryGetValue(i.Id, out var bf) ? bf.Amounts : null)).ToList();
        // A-GW9: แถวเก่าที่แยกยอดคืนหักรอบหลังไม่ได้ — นับให้เห็น (ไม่เติมย้อนหลังด้วยค่าที่แต่งขึ้น)
        var lateRefundUnknown = intentRows.Count(GatewayReconciliation.LateRefundSplitUnknown);

        var result = GatewayReconciliation.Compute(rows);
        return Ok(new ApiResponse<object>(true, new
        {
            fromDate = fromLabel, toDate = toLabel,
            result.SucceededCount, result.GrossCharged, result.RefundedAmount,
            result.FeeTotal, result.FeeIsEstimated, result.ExpectedNet,
            result.SettledTotal, result.UnsettledCount, result.UnsettledAmount,
            result.Difference, result.UnexplainedDifference, result.IsBalanced,
            lateRefundSplitUnknown = lateRefundUnknown,
            lateRefundSplitUnknownMessage = GatewayReconciliation.LateRefundSplitUnknownMessage(lateRefundUnknown),
        }));
    }

    /// <summary>สถานะรอบโอน settlement ของ intent ที่ batch เป็นเจ้าของ + ยอดจากบรรทัดของรอบที่ลงบัญชีแล้ว (รอบ 201 ทีม GW · A-GW4) —
    /// ตัวตัดสิน "โอนแล้ว" = <see cref="GatewayReconciliation.IsBatchPosted"/> · ยอด = <see cref="GatewayReconciliation.BatchSettled"/></summary>
    private async Task<Dictionary<Guid, (bool Posted, GatewayBatchSettledAmounts? Amounts)>> LoadBatchSettlementAsync(Guid companyId,
        IReadOnlyCollection<(Guid IntentId, Guid BatchId)> owned, CancellationToken ct)
    {
        var result = new Dictionary<Guid, (bool Posted, GatewayBatchSettledAmounts? Amounts)>();
        if (owned.Count == 0) return result;
        var batchIds = owned.Select(o => o.BatchId).Distinct().ToList();
        var statuses = await _db.SettlementBatches.AsNoTracking()
            .Where(b => b.CompanyId == companyId && batchIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Status, b.IsDeleted })
            .ToDictionaryAsync(b => b.Id, b => !b.IsDeleted && GatewayReconciliation.IsBatchPosted(b.Status), ct);
        var postedIntentIds = owned.Where(o => statuses.TryGetValue(o.BatchId, out var p) && p).Select(o => o.IntentId).ToList();
        var postedStatuses = new[] { SettlementBatchStatus.Posted, SettlementBatchStatus.BankMatched };
        var lines = postedIntentIds.Count == 0
            ? new List<(Guid IntentId, GatewayBatchLineFact Line)>()
            : (await _db.SettlementLines.AsNoTracking()
                    .Where(l => l.CompanyId == companyId && l.PaymentIntentId != null && postedIntentIds.Contains(l.PaymentIntentId.Value)
                        && !l.Batch.IsDeleted && postedStatuses.Contains(l.Batch.Status))
                    .Select(l => new { IntentId = l.PaymentIntentId!.Value, l.LineType, l.Amount })
                    .ToListAsync(ct))
                .Select(l => (IntentId: l.IntentId, Line: new GatewayBatchLineFact(l.LineType, l.Amount))).ToList();
        foreach (var (intentId, batchId) in owned)
        {
            var posted = statuses.TryGetValue(batchId, out var isPosted) && isPosted;
            result[intentId] = (posted, posted
                ? GatewayReconciliation.BatchSettled(lines.Where(l => l.IntentId == intentId).Select(l => l.Line))
                : null);
        }
        return result;
    }

    public sealed record ManualConfirmRequest(string Reason);
    public sealed record RefundRequest(decimal? Amount, string? Reason);

    /// <summary>**ยืนยันด้วยมือ** — เงินเข้าจริงแล้ว (เห็นในบัญชีธนาคาร/แดชบอร์ดผู้ให้บริการ)
    /// แต่ระบบยังไม่รู้ เพราะ webhook หายและถามสถานะสดก็ยังไม่ขึ้น
    ///
    /// <para>เดินผ่าน <c>ApplyChargeAsync</c> เส้นเดียวกับ webhook/poll ⇒ ได้ทั้ง
    /// การเปลี่ยนสถานะ · ประวัติ · และ<b>การส่งต่อให้ทางเข้าเดิมทำงาน</b> (ออกใบเสร็จ /
    /// ตัดหนี้ / ยืนยันการจอง) เหมือนกันทุกประการ — ห้ามเขียนเส้นที่สองที่แค่แก้สถานะ</para>
    ///
    /// <para><b>ต้องระบุเหตุผล</b> และถูกบันทึกว่า <c>manual:{user}</c> —
    /// การยืนยันด้วยมือคือจุดที่ผู้สอบบัญชีถามเสมอว่าใครกดและเพราะอะไร</para></summary>
    [HttpPost("intents/{intentId:guid}/confirm-manually")]
    [Accounting.Filters.RejectApiKey("ยืนยันการรับเงินด้วยมือ")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.ConfirmManually)]
    public async Task<ActionResult<ApiResponse<IntentResponse>>> ConfirmManually(
        Guid companyId, Guid intentId, [FromBody] ManualConfirmRequest req,
        [FromServices] IEnumerable<IPaymentProvider> providers, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new ApiResponse<IntentResponse>(false, null,
                "กรุณาระบุเหตุผล — เช่น \"ตรวจสอบยอดในบัญชีธนาคารแล้วพบเงินเข้าจริง\" "
                + "(ผู้สอบบัญชีต้องเห็นว่าใครยืนยันและเพราะอะไร)"));

        var intent = await _intents.FindAsync(companyId, intentId, ct);
        if (intent == null)
            return NotFound(new ApiResponse<IntentResponse>(false, null, "ไม่พบรายการชำระเงิน"));

        // รอบ 200 ทีม G: ช่องทางที่ผู้ให้บริการถือเงินไว้ก่อน แต่ไม่มี charge ที่ผู้ให้บริการเลย ⇒ ยืนยันที่นี่ = Dr บัญชีพักด้วยเงินที่ไม่มีวันถูกโอนมา
        var adapter = providers.FirstOrDefault(p => p.ProviderCode == intent.ProviderCode);
        var blocked = PaymentIntentPolicy.ManualConfirmBlockReason(
            adapter != null && !adapter.SettlesDirectlyToBank, intent.ProviderRef);
        if (blocked != null)
            return BadRequest(new ApiResponse<IntentResponse>(false, null, blocked));

        var actor = $"manual:{JwtHelper.GetUserIdFromClaims(User)}";
        var updated = await _intents.ApplyChargeAsync(intentId,
            new ProviderCharge(
                ProviderRef: intent.ProviderRef ?? $"manual:{intentId:N}",
                Status: PaymentIntentStatus.Succeeded,
                RawStatus: "manual_confirm",
                Amount: intent.Amount),
            PaymentEventSource.Manual, $"{actor} · {req.Reason.Trim()}", ct);

        return Ok(new ApiResponse<IntentResponse>(true,
            Map(updated, await IsTestModeAsync(updated, ct)),
            "ยืนยันการรับเงินแล้ว — ระบบดำเนินการต่อให้ต้นทางเรียบร้อย"));
    }

    /// <summary>คืนเงินผ่านผู้ให้บริการ — <b>ลงบัญชีคืนเงินเสมอ</b> (รอบ 198 G-1 · <see cref="IGatewayRefundService"/>)
    ///
    /// <para>JE: <c>Dr ลูกหนี้การค้า / Cr บัญชีพักผู้ให้บริการ</c> — ตั้งยอด "รอใบลดหนี้" ที่ลูกหนี้ · ยอดคืนสะสมห้ามเกินยอดรับ ·
    /// ตรวจผัง/งวดก่อนเงินออก · เงินออกแล้วแต่ลงบัญชีไม่ได้ = ล้มดัง (ไม่กลืน)</para>
    ///
    /// <para><b>ไม่สร้างใบลดหนี้ให้อัตโนมัติ</b>โดยตั้งใจ: §86/10 บังคับให้ใบลดหนี้มี "เหตุผล" ตาม closed list · ต้องอ้างใบกำกับเดิม ·
    /// และยอดสะสมของใบลดหนี้ห้ามเกินใบเดิม — ต้องให้คนตัดสิน · หน้ารายการติดป้าย "คืนเงินแล้ว ยังไม่ออกใบลดหนี้" แทน</para></summary>
    [HttpPost("intents/{intentId:guid}/refund")]
    [Accounting.Filters.RejectApiKey("คืนเงินลูกค้า")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.Refund)]
    public async Task<ActionResult<ApiResponse<object>>> Refund(
        Guid companyId, Guid intentId, [FromBody] RefundRequest req,
        [FromServices] IGatewayRefundService refunds, CancellationToken ct)
    {
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var r = await refunds.RefundAsync(companyId, intentId, req.Amount, req.Reason ?? string.Empty, actor, ct);
        var data = new
        {
            refundRef = r.RefundRef,
            amount = r.Amount,
            isFullRefund = r.IsFullRefund,
            refundedTotal = r.RefundedTotal,
            journalEntryId = r.JournalEntryId,
            journalEntryNumber = r.JournalEntryNumber,
            creditNoteState = r.CreditNoteState,
            // ขั้นถัดไปที่ระบบทำแทนไม่ได้ — ต้องบอกให้ชัด ไม่ใช่ปล่อยให้ผู้ใช้เดา
            nextStep = r.NextStep,
        };
        return r.Ok
            ? Ok(new ApiResponse<object>(true, data, r.Message))
            : BadRequest(new ApiResponse<object>(false, data, r.Message));
    }

    /// <summary>ตรวจผลการคืนเงินที่ "ผลไม่แน่ชัด" กับผู้ให้บริการ (ฝ่ายค้าน E-2) — ไม่ได้เกิด ⇒ ปลดล็อก · เกิดแล้ว ⇒ ลงบัญชีคืนเงิน
    /// ส่วนต่าง (เส้นเดียวกับคืนเงิน) + ปลดล็อก · ถามไม่ได้/ขัดกัน ⇒ ล็อกต่อ · สิทธิ์เดียวกับคืนเงิน (อาจลง JE เงินออก)</summary>
    [HttpPost("intents/{intentId:guid}/refund/verify")]
    [Accounting.Filters.RejectApiKey("ตรวจผลการคืนเงินลูกค้า")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.Refund)]
    public async Task<ActionResult<ApiResponse<object>>> VerifyRefund(
        Guid companyId, Guid intentId, [FromServices] IGatewayRefundService refunds, CancellationToken ct)
    {
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var r = await refunds.VerifyUnknownRefundAsync(companyId, intentId, actor, ct);
        var data = new
        {
            amount = r.Amount,
            refundedTotal = r.RefundedTotal,
            journalEntryId = r.JournalEntryId,
            journalEntryNumber = r.JournalEntryNumber,
            nextStep = r.NextStep,
        };
        return r.Ok
            ? Ok(new ApiResponse<object>(true, data, r.Message))
            : BadRequest(new ApiResponse<object>(false, data, r.Message));
    }

    /// <summary>คำขอบันทึกผลด้วยมือ — <c>Decision</c> = "NoMoneyOut" | "MoneyWentOut" (ชื่อ enum · อ่านไม่ออก = ปฏิเสธ)</summary>
    public sealed record RefundManualResolutionRequest(string? Decision, decimal? Amount, string? ProviderRefundRef, string? Evidence);

    /// <summary>บันทึกผลการคืนเงินที่ "ผลไม่แน่ชัด" ด้วยมือ (review198-E2 E2-3) — ทางไปต่อเมื่อผู้ให้บริการไม่ส่งยอดคืนสะสม/ข้อมูลขัดกัน ·
    /// <b>เจ้าของกิจการเท่านั้น</b> (ตัดสินแทนผู้ให้บริการว่าเงินออกหรือไม่ — ปลดล็อกการคืนเงินและรอบโอน) + ห้ามคีย์ API + สิทธิ์คืนเงิน ·
    /// ต้องมีหลักฐาน · เงินออก ⇒ ลงบัญชีคืนเงินเส้นเดียวกับคืนเงินปกติ · hash chain · ตัดสินที่ service (endpoint ห้ามปลดล็อกเอง)</summary>
    [HttpPost("intents/{intentId:guid}/refund/resolve-manually")]
    [Accounting.Filters.RejectApiKey("บันทึกผลการคืนเงินที่ไม่แน่ชัดด้วยมือ")]
    [Accounting.Filters.RequireOwner("บันทึกผลการคืนเงินที่ไม่แน่ชัดด้วยมือ",
        "เป็นการตัดสินแทนผู้ให้บริการว่าเงินออกหรือไม่ — ปลดล็อกการคืนเงินและรอบโอน")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.Refund)]
    public async Task<ActionResult<ApiResponse<object>>> ResolveRefundManually(
        Guid companyId, Guid intentId, [FromBody] RefundManualResolutionRequest req,
        [FromServices] IGatewayRefundService refunds, CancellationToken ct)
    {
        var decision = Enum.TryParse<GatewayRefundManualDecision>(req.Decision?.Trim(), ignoreCase: true, out var d)
            && Enum.IsDefined(d) ? d : GatewayRefundManualDecision.Unspecified;
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var r = await refunds.ResolveUnknownRefundManuallyAsync(companyId, intentId, decision, req.Amount, req.ProviderRefundRef,
            req.Evidence, actor, User?.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        var data = new
        {
            amount = r.Amount,
            refundedTotal = r.RefundedTotal,
            journalEntryId = r.JournalEntryId,
            journalEntryNumber = r.JournalEntryNumber,
            nextStep = r.NextStep,
        };
        return r.Ok
            ? Ok(new ApiResponse<object>(true, data, r.Message))
            : BadRequest(new ApiResponse<object>(false, data, r.Message));
    }

    /// <summary>คำขอบันทึกยอดคืนย้อนหลัง — <c>Journal</c> = "BookNow" | "AlreadyBookedManually" (ชื่อ enum · อ่านไม่ออก = ปฏิเสธ) ·
    /// <c>RoundTiming</c> (ฝ่ายค้าน GWO-3) = "DeductedInRecordedRound" | "DeductedInLaterRound" — บังคับเมื่อรายการอยู่ในรอบโอนที่บันทึกแล้วและคืนไม่หลังวันเงินเข้า</summary>
    public sealed record LegacyRefundRequest(decimal? Amount, DateTime? RefundedAt, string? ProviderRefundRef, string? Evidence, string? Journal,
        string? RoundTiming = null);

    /// <summary>บันทึกยอดคืนจริงของรายการที่ "คืนแล้วแต่ระบบไม่มียอดคืน" (รอบ 201 ทีม GW · A-GW7 · review198-E2 E2-12e) — แถวเก่า/ลงบัญชีคืนไม่สำเร็จ
    /// ค้าง −ค่าธรรมเนียมในกระทบยอดและไม่เข้ารอบโอนตลอดไป · <b>เจ้าของกิจการเท่านั้น</b> (เติมยอดที่ระบบไม่รู้แทนผู้ให้บริการ) + ห้ามคีย์ API + สิทธิ์คืนเงิน ·
    /// ต้องมีหลักฐาน · ตัดสินที่ service (<c>GatewayRefundMath.CheckLegacyRefundEntry</c>)</summary>
    [HttpPost("intents/{intentId:guid}/refund/record-legacy")]
    [Accounting.Filters.RejectApiKey("บันทึกยอดคืนเงินย้อนหลัง")]
    [Accounting.Filters.RequireOwner("บันทึกยอดคืนเงินย้อนหลัง",
        "เป็นการเติมยอดคืนที่ระบบไม่รู้แทนผู้ให้บริการ — กระทบรอบโอนและบัญชีพัก")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.Refund)]
    public async Task<ActionResult<ApiResponse<object>>> RecordLegacyRefund(
        Guid companyId, Guid intentId, [FromBody] LegacyRefundRequest req,
        [FromServices] IGatewayRefundService refunds, CancellationToken ct)
    {
        var journal = Enum.TryParse<GatewayLegacyRefundJournal>(req.Journal?.Trim(), ignoreCase: true, out var j)
            && Enum.IsDefined(j) ? j : GatewayLegacyRefundJournal.Unspecified;
        var roundTiming = Enum.TryParse<GatewayLegacyRefundRoundTiming>(req.RoundTiming?.Trim(), ignoreCase: true, out var rt)
            && Enum.IsDefined(rt) ? rt : GatewayLegacyRefundRoundTiming.Unspecified;
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var r = await refunds.RecordLegacyRefundAsync(companyId, intentId, req.Amount,
            req.RefundedAt is DateTime at ? DateTime.SpecifyKind(at, at.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : at.Kind) : null,
            req.ProviderRefundRef, req.Evidence, journal, roundTiming, actor, User?.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        var data = new
        {
            amount = r.Amount,
            refundedTotal = r.RefundedTotal,
            journalEntryId = r.JournalEntryId,
            journalEntryNumber = r.JournalEntryNumber,
            nextStep = r.NextStep,
        };
        return r.Ok
            ? Ok(new ApiResponse<object>(true, data, r.Message))
            : BadRequest(new ApiResponse<object>(false, data, r.Message));
    }

    public sealed record FeeCorrectionRequest(decimal FeeActual, string? Reason);

    /// <summary>แก้ค่าธรรมเนียมจริงรายรายการ (รอบ 198 G-6) — ข้อความบล็อกรอบโอน "ยอดไม่ตรง" ชี้มาที่ปุ่มนี้ ·
    /// เดิมไม่มีที่แก้เลย (<c>FeeEstimated</c> = 0 เสมอ) ⇒ ข้อความบอกทางแก้ที่ไม่มีอยู่จริง</summary>
    [HttpPut("intents/{intentId:guid}/fee")]
    [Accounting.Filters.RejectApiKey("แก้ค่าธรรมเนียมรับชำระเงิน")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.PostSettlement)]
    public async Task<ActionResult<ApiResponse<object>>> CorrectFee(
        Guid companyId, Guid intentId, [FromBody] FeeCorrectionRequest req,
        [FromServices] IGatewaySettlementService settlements, CancellationToken ct)
    {
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var r = await settlements.CorrectFeeAsync(companyId, intentId, req.FeeActual, req.Reason ?? string.Empty,
            actor, User?.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        var data = new { oldFee = r.OldFee, newFee = r.NewFee };
        return r.Ok
            ? Ok(new ApiResponse<object>(true, data, r.Message))
            : BadRequest(new ApiResponse<object>(false, data, r.Message));
    }

    // ══════════════════════════════════════════════════════════════════
    //  บันทึก "เงินที่ผู้ให้บริการโอนเข้าธนาคาร" (settlement)
    //
    //  ตอนลูกค้าจ่ายสำเร็จ เงินยังไม่เข้าบัญชีเรา — ลง Dr 11340 ไว้ก่อน ·
    //  ผู้ให้บริการรวบยอดแล้วโอนเข้า T+n หลังหักค่าธรรมเนียม ⇒ ขั้นนี้คือขั้นที่
    //  ล้าง 11340 ออกด้วยเงินจริง + รับรู้ค่าธรรมเนียมเป็นค่าใช้จ่าย
    //
    //  ⚠️ **ยอดที่โอนเข้าจริงเป็นตัวตั้ง** — ไม่ตรงกับที่คำนวณได้ = บล็อก
    //  ห้ามให้ระบบเดาส่วนต่าง (JE ที่ยอดธนาคารไม่ตรงสเตทเมนต์ = กระทบยอด
    //  ไม่ได้ตลอดไป) · สิทธิ์ระดับเดียวกับการลง JE
    // ══════════════════════════════════════════════════════════════════

    public sealed record SettlementRequestDto(
        string ProviderCode, DateTime FromDate, DateTime ToDate,
        decimal ActualNetReceived, DateTime SettledAt, string SettlementRef, Guid BankAccountId);

    private static object MapPlan(SettlementOutcome outcome) => new
    {
        outcome.Ok,
        outcome.Message,
        blockReason = outcome.Plan.Reason.ToString(),
        count = outcome.Plan.IntentIds.Count,
        outcome.Plan.Gross,
        feeNetPaid = outcome.Plan.FeeNetPaid,
        feeGrossedUp = outcome.Plan.FeeGrossedUp,
        whtOnFee = outcome.Plan.WhtOnFee,
        feeVat = outcome.Plan.FeeVat,
        refundDeducted = outcome.Plan.RefundDeducted,
        // R-E6: คำเตือนที่ไม่บล็อก (โหมด VAT ค่าธรรมเนียม) — ข้อความจาก GatewaySettlementMath ตัวเดียว
        warning = outcome.Plan.Warning,
        outcome.Plan.ExpectedNet,
        outcome.Plan.ActualNet,
        outcome.Plan.Difference,
        lines = outcome.Plan.Lines.Select(l => new
        { role = l.Role.ToString(), l.Debit, l.Credit, l.Description }),
        outcome.JournalEntryId,
        outcome.JournalEntryNumber,
    };

    /// <summary>รายการที่ "รับเงินแล้วแต่ยังไม่โอนเข้าธนาคาร" — ตั้งต้นของหน้าบันทึกการโอน
    ///
    /// <para>รอบ 198: เกณฑ์เลือก + ตัวเลขทุกช่องมาจาก service ตัวเดียวกับแผน JE (<see cref="IGatewaySettlementService.ListPendingAsync"/>)
    /// — เดิมเขียนเงื่อนไขซ้ำที่นี่ (เฉพาะ Succeeded) ⇒ รายการคืนบางส่วนหายจากทั้งหน้าและแผน</para></summary>
    [HttpGet("settlements/pending")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.PreviewSettlement)]
    public async Task<ActionResult<ApiResponse<object>>> PendingSettlement(
        Guid companyId, [FromQuery] string? providerCode,
        [FromServices] IGatewaySettlementService settlements, CancellationToken ct)
    {
        var v = await settlements.ListPendingAsync(companyId, providerCode, ct);
        return Ok(new ApiResponse<object>(true, new
        {
            count = v.Count,
            gross = v.Gross,
            fee = v.Fee,
            expectedNet = v.ExpectedNet,
            // ค่าธรรมเนียมที่ยังเป็นตัวประมาณต้องติดป้าย — ตัวเลขประมาณที่ไม่ติดป้าย
            // จะถูกอ่านเป็นตัวจริงแล้วนำไปตัดสินใจผิด
            anyFeeEstimated = v.AnyFeeEstimated,
            feeVatMode = v.FeeVatMode,
            feeVatWarning = v.FeeVatWarning,
            legacyRefundedCount = v.LegacyRefundedCount,
            items = v.Items.Select(i => new
            {
                i.Id, i.ProviderCode, i.ConfirmedAt, i.Amount, i.RefundedAmount, i.Clearing,
                fee = i.Fee, i.FeeVat, i.FeeIsEstimated, i.Net, i.IsRefundAfterSettlement,
                i.ProviderRef, i.SourceKind, i.Status,
                // R-E4: ค่าที่ปุ่ม "แก้ค่าธรรมเนียม" เติม/บันทึก (ความหมายตามโหมด) + ป้าย — เซิร์ฟเวอร์ตัดสิน หน้าเว็บแสดง
                i.FeeInput, i.FeeInputLabel,
            }),
        }));
    }

    /// <summary>ดูตัวอย่างก่อนบันทึก — ไม่เขียนอะไรเลย (ให้ผู้ใช้เห็น JE ก่อนกดจริง)</summary>
    [HttpPost("settlements/preview")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.PreviewSettlement)]
    public async Task<ActionResult<ApiResponse<object>>> PreviewSettlement(
        Guid companyId, [FromBody] SettlementRequestDto req,
        [FromServices] IGatewaySettlementService settlements, CancellationToken ct)
    {
        var outcome = await settlements.PreviewAsync(companyId, ToServiceRequest(req), ct);
        return Ok(new ApiResponse<object>(true, MapPlan(outcome), outcome.Message));
    }

    [HttpPost("settlements")]
    [Accounting.Filters.RejectApiKey("บันทึกรอบโอนเงินรับออนไลน์")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.PostSettlement)]
    public async Task<ActionResult<ApiResponse<object>>> RecordSettlement(
        Guid companyId, [FromBody] SettlementRequestDto req,
        [FromServices] IGatewaySettlementService settlements, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.SettlementRef))
            return BadRequest(new ApiResponse<object>(false, null!,
                "กรุณากรอกเลขอ้างอิงรอบโอน (ดูจากสเตทเมนต์/แดชบอร์ดผู้ให้บริการ) — "
                + "ใช้ตามรอยตอนกระทบยอดย้อนหลัง"));

        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var outcome = await settlements.RecordAsync(companyId, ToServiceRequest(req), actor, ct);
        return outcome.Ok
            ? Ok(new ApiResponse<object>(true, MapPlan(outcome), outcome.Message))
            // ไม่ตรง = ข้อมูลที่ผู้ใช้ต้องไปแก้ ไม่ใช่ error ของระบบ → คืนแผนไปด้วย
            // ให้หน้าจอโชว์ว่าต่างเท่าไรและต่างตรงไหน
            : BadRequest(new ApiResponse<object>(false, MapPlan(outcome), outcome.Message));
    }

    private static RecordSettlementRequest ToServiceRequest(SettlementRequestDto d)
        => new(d.ProviderCode, d.FromDate, d.ToDate, d.ActualNetReceived,
            d.SettledAt, d.SettlementRef.Trim(), d.BankAccountId);

    // ══════════════════════════════════════════════════════════════════
    //  VAT ค่าธรรมเนียมที่พักไว้ใน 11630 → 11610 เมื่อได้ใบกำกับของผู้ให้บริการ (ฝ่ายค้าน R-E3)
    // ══════════════════════════════════════════════════════════════════

    public sealed record FeeVatClaimRequestDto(
        string? ProviderCode, string? TaxInvoiceNo, DateTime TaxInvoiceDate, DateTime ClaimDate, decimal VatAmount,
        string? SupplierName, string? SupplierTaxId, string? SupplierBranchCode, string? LateReason);

    /// <summary>VAT ค่าธรรมเนียมที่รอใบกำกับ (ยอดค้างต่อเดือน + คำเตือนอายุ §82/3) — อ่านอย่างเดียว · สิทธิ์เดียวกับพรีวิวรอบโอน</summary>
    [HttpGet("settlements/fee-vat")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.PreviewSettlement)]
    public async Task<ActionResult<ApiResponse<object>>> FeeVatStatus(
        Guid companyId, [FromQuery] string? providerCode,
        [FromServices] IGatewaySettlementService settlements, CancellationToken ct)
    {
        var v = await settlements.FeeVatStatusAsync(companyId, providerCode ?? string.Empty, ct);
        return Ok(new ApiResponse<object>(true, new
        {
            v.ProviderCode, v.CompanyVatRegistered,
            deferredTotal = v.Aging.DeferredTotal, claimedTotal = v.Aging.ClaimedTotal, outstanding = v.Aging.Outstanding,
            worstLevel = v.Aging.WorstLevel.ToString(), warning = v.Aging.Warning,
            // รอบ 201 ทีม GW (A-GW5): VAT ค่าธรรมเนียมของรอบโอน batch ที่รอใบกำกับ (เคลมที่ใบสำคัญจ่าย ไม่ใช่ที่นี่) — แสดงอย่างเดียว
            batchUndueVat = v.BatchUndueVat, batchUndueDocuments = v.BatchUndueDocuments, batchPortionNote = v.BatchPortionNote,
            // A-GW8: ปรับปรุงเศษ 11630 — ผลจากตัวตรวจเดียวกับตอนบันทึก (ปุ่มขึ้นเฉพาะเมื่อ residueAllowed · ไม่ได้ = ข้อความบอกเหตุ)
            residueAllowed = v.Residue?.Ok ?? false, residueAmount = v.Residue?.Amount ?? 0m,
            residueThreshold = v.Residue?.Threshold ?? 0m, residueMessage = v.Residue?.Message,
            buckets = v.Aging.Buckets.Select(b => new
            {
                b.MonthStartUtc, b.Deferred, b.Outstanding, b.MonthsOld, level = b.Level.ToString(), b.Warning,
            }),
        }));
    }

    /// <summary>"รับใบกำกับค่าธรรมเนียม" — JV Dr 11610 / Cr 11630 เท่ายอด VAT บนใบกำกับ (<b>ไม่ลงค่าใช้จ่ายซ้ำ</b>) ·
    /// สิทธิ์ลง JE · ห้ามคีย์ API (ภาษีซื้อเข้า ภ.พ.30)</summary>
    [HttpPost("settlements/fee-vat/claim")]
    [Accounting.Filters.RejectApiKey("บันทึกรับใบกำกับค่าธรรมเนียมรับชำระเงิน")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.PostSettlement)]
    public async Task<ActionResult<ApiResponse<object>>> ClaimFeeVat(
        Guid companyId, [FromBody] FeeVatClaimRequestDto req,
        [FromServices] IGatewaySettlementService settlements, CancellationToken ct)
    {
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var r = await settlements.ClaimFeeVatAsync(companyId, new GatewayFeeVatClaimRequest(
                req.ProviderCode ?? string.Empty, req.TaxInvoiceNo, req.TaxInvoiceDate, req.ClaimDate, req.VatAmount,
                req.SupplierName, req.SupplierTaxId, req.SupplierBranchCode, req.LateReason),
            actor, User?.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        var data = new { r.JournalEntryId, r.JournalEntryNumber, r.OutstandingAfter };
        return r.Ok
            ? Ok(new ApiResponse<object>(true, data, r.Message))
            : BadRequest(new ApiResponse<object>(false, data, r.Message));
    }

    public sealed record FeeVatResidueRequestDto(string? ProviderCode, DateTime EntryDate, string? Reason);

    /// <summary>ปรับปรุงเศษ VAT ค่าธรรมเนียมที่ค้าง 11630 (รอบ 201 ทีม GW · A-GW8 · review198-E2 E2-12f) — เฉพาะยอดที่ไม่เกินเกณฑ์เศษปัดต่อใบกำกับที่เคลมแล้ว ·
    /// JE ค่าธรรมเนียม ↔ 11630 + hash chain · สิทธิ์ลง JE · ห้ามคีย์ API</summary>
    [HttpPost("settlements/fee-vat/residue")]
    [Accounting.Filters.RejectApiKey("ปรับปรุงเศษ VAT ค่าธรรมเนียมรับชำระเงิน")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.PostSettlement)]
    public async Task<ActionResult<ApiResponse<object>>> WriteOffFeeVatResidue(
        Guid companyId, [FromBody] FeeVatResidueRequestDto req,
        [FromServices] IGatewaySettlementService settlements, CancellationToken ct)
    {
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var r = await settlements.WriteOffFeeVatResidueAsync(companyId, req.ProviderCode ?? string.Empty, req.EntryDate,
            req.Reason ?? string.Empty, actor, User?.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        var data = new { r.JournalEntryId, r.JournalEntryNumber, r.Amount };
        return r.Ok
            ? Ok(new ApiResponse<object>(true, data, r.Message))
            : BadRequest(new ApiResponse<object>(false, data, r.Message));
    }

    /// <summary>ประวัติของรายการเดียว — "ลูกค้าบอกว่าจ่ายแล้วแต่ระบบไม่รู้" ตอบจากตรงนี้ · รอบ 200 ทีม G (G-8): สิทธิ์ดูธนาคาร
    /// (ข้อความประวัติมีเหตุผลคืนเงิน/หลักฐาน/ผู้ยืนยัน)</summary>
    [HttpGet("intents/{intentId:guid}/events")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.ViewPayments)]
    public async Task<ActionResult<ApiResponse<object>>> Events(
        Guid companyId, Guid intentId, CancellationToken ct)
    {
        var rows = await _db.PaymentIntentEvents.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.IntentId == intentId)
            .OrderBy(e => e.At)
            .Select(e => new
            {
                e.At, source = e.Source.ToString(),
                from = e.FromStatus == null ? null : e.FromStatus.ToString(),
                to = e.ToStatus.ToString(), e.Note,
            })
            .ToListAsync(ct);
        return Ok(new ApiResponse<object>(true, rows));
    }
}

/// <summary>
/// ปลายทาง webhook ของผู้ให้บริการ — **ไม่ต้องล็อกอิน** และ **ไม่มีชื่อเจ้าในโค้ด**
///
/// <para>ผู้ให้บริการเลือกได้จาก path segment แล้ว resolve เป็น <c>IPaymentProvider</c>
/// — การยืนยันเป็นหน้าที่ของ adapter (บางเจ้าใช้ HMAC บางเจ้าให้ fetch event กลับ)</para>
///
/// <para><b>ตอบ 200 เสมอ</b> แม้ปฏิเสธ — ถ้าตอบ error ผู้ให้บริการจะ retry ไม่รู้จบ
/// และการปฏิเสธของเราไม่ใช่ปัญหาของเขา · สิ่งที่เกิดขึ้นถูกบันทึกไว้ที่ฝั่งเราแล้ว</para>
/// </summary>
[ApiController]
[Route("api/pay/webhooks")]
[AllowAnonymous]
public class PaymentWebhookController : ControllerBase
{
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly IPaymentIntentService _intents;
    private readonly AccountingDbContext _db;
    private readonly ILogger<PaymentWebhookController> _logger;

    public PaymentWebhookController(IEnumerable<IPaymentProvider> providers,
        IPaymentIntentService intents, AccountingDbContext db,
        ILogger<PaymentWebhookController> logger)
    { _providers = providers; _intents = intents; _db = db; _logger = logger; }

    /// <summary>ปลายทาง webhook — <b>สองเส้นทาง action เดียว</b> (รอบ 201 ทีม GW · A-GW1)
    /// <list type="bullet">
    /// <item><c>{providerCode}/{token}</c> (URL ใหม่) — รหัสลับต่อ config ⇒ ลองยืนยันกับ config นั้นตัวเดียว · รหัสผิด/รูปผิด = <b>ไม่ยิงคำขอออกเลย</b>
    /// (เดิม POST นิรนาม 1 ครั้ง = คำขอออกด้วยคีย์ของทุกร้าน)</item>
    /// <item><c>{providerCode}</c> (URL เดิม · <c>token</c> = null) — <b>คงไว้</b>เพราะผู้ใช้ตั้งไว้ในแดชบอร์ดผู้ให้บริการ (ตัดทิ้ง = ลูกค้าจ่ายแล้วออเดอร์ไม่อัปเดต) ·
    /// ลองเฉพาะ config ที่มีหลักฐานว่าใช้ URL เดิม + โหมดปัจจุบันยังไม่ย้าย + ก่อนวันปิด (ฝ่ายค้าน GWO-1 · <see cref="GatewayWebhookRoute.AcceptsLegacy"/>) —
    /// ก่อนวันปิด POST นิรนามยังพาคีย์ของร้านกลุ่มนั้นออกไปได้ · คำขอที่อ้างรายการของร้านที่ถูกข้าม ⇒ ประทับ <c>LastLegacySkippedAt</c> (GWO-7)</item>
    /// </list>
    /// ตัวเลือก config = <see cref="GatewayWebhookRoute.ConfigsToTry"/> ตัวเดียวของทั้งสองเส้นทาง</summary>
    [HttpPost]
    [Route("{providerCode}")]
    [Route("{providerCode}/{token}")]
    public async Task<IActionResult> Receive(string providerCode, string? token, CancellationToken ct)
    {
        var provider = _providers.FirstOrDefault(p => p.ProviderCode == providerCode);
        if (provider == null) return Ok(new { received = true, handled = false });

        // รหัสรูปผิด = ไม่แตะฐานและไม่ยิงอะไรออก (ตัวตัดสินเดียวกับการเลือก config)
        if (token != null && !GatewayWebhookRoute.IsWellFormedToken(token))
        {
            _logger.LogWarning("webhook {Provider}: รหัสลับใน URL รูปผิด — ไม่ลองยืนยันกับการตั้งค่าใด", providerCode);
            return Ok(new { received = true, handled = false });
        }

        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync(ct);
        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        // ยังไม่รู้ว่าเป็นของบริษัทไหน — ผู้สมัคร = config ที่เปิดใช้ของ provider นี้ (เส้นรหัสลับกรองด้วยรหัสที่ฐานก่อน) แล้วให้
        // GatewayWebhookRoute.ConfigsToTry ตัดสินชุดที่จะลองจริง · adapter ที่ยืนยันไม่ผ่านจะคืน null (ไม่ throw)
        var candidatesQ = _db.PaymentProviderConfigs
            .Where(c => c.ProviderCode == providerCode && c.IsActive && !c.IsDeleted);
        if (token != null) candidatesQ = candidatesQ.Where(c => c.WebhookToken == token);
        var candidates = await candidatesQ.ToListAsync(ct);
        var now = DateTime.UtcNow;
        GatewayWebhookConfigFacts Facts(PaymentProviderConfig c) => new(c.Id, c.WebhookToken, c.LastTokenWebhookAt,
            c.LastTokenWebhookMode, c.Mode, c.LegacyWebhookEligible);
        // ฝ่ายค้าน GWO-1: URL เดิมลองเฉพาะร้านที่มีหลักฐานว่าใช้ URL เดิม + ยังไม่ย้าย + ก่อนวันปิด (AcceptsLegacy)
        var toTry = GatewayWebhookRoute.ConfigsToTry(candidates.Select(Facts), token, now);
        var configs = candidates.Where(c => toTry.Contains(c.Id)).ToList();

        // ฝ่ายค้าน GWO-7: คำขอทาง URL เดิมที่อ้างรายการของร้านที่ถูกข้าม ⇒ ประทับเวลาให้หน้าตั้งค่าของร้านนั้นเตือน "ตั้ง URL เดิมค้างไว้" ·
        // เลขรายการมาจากเนื้อคำขอที่ยังไม่ยืนยัน (ไม่ยิงคำขอออก · ไม่เปลี่ยนสถานะเงิน · ผลมีแค่คำเตือน)
        if (token == null)
        {
            var skipped = candidates.Where(c => !toTry.Contains(c.Id)).ToList();
            if (skipped.Count > 0 && provider.UnverifiedIntentHint(raw) is Guid hintedIntent)
            {
                // ขอบเขตบริษัท = บริษัทของ config ที่ถูกข้ามเท่านั้น (ไม่มี tenant จาก route — webhook นิรนาม)
                var skippedCompanies = skipped.Select(c => c.CompanyId).Distinct().ToList();
                var hinted = await _db.PaymentIntents.AsNoTracking()
                    .Where(i => i.Id == hintedIntent && skippedCompanies.Contains(i.CompanyId))
                    .Select(i => new { i.CompanyId, i.ProviderConfigId })
                    .FirstOrDefaultAsync(ct);
                var owner = hinted == null ? null
                    : skipped.FirstOrDefault(c => c.Id == hinted.ProviderConfigId && c.CompanyId == hinted.CompanyId);
                if (owner != null && GatewayWebhookRoute.ShouldStampLegacySkip(Facts(owner), owner.LastLegacySkippedAt, now))
                {
                    owner.LastLegacySkippedAt = now;
                    await _db.SaveChangesAsync(ct);
                }
            }
        }

        foreach (var cfg in configs)
        {
            VerifiedWebhookEvent? verified;
            try { verified = await provider.VerifyWebhookAsync(raw, headers, cfg, ct); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "webhook {Provider}: ยืนยันล้มเหลวสำหรับบริษัท {Company}",
                    providerCode, cfg.CompanyId);
                continue;
            }
            if (verified == null) continue;

            cfg.LastWebhookAt = DateTime.UtcNow;
            // A-GW1: เส้นทางที่ webhook มาจริง — หน้าตั้งค่าเตือน "ยังใช้ URL เดิม" · URL เดิมเลิกลอง config นี้เมื่อโหมดนี้ย้ายแล้ว
            if (token != null) { cfg.LastTokenWebhookAt = cfg.LastWebhookAt; cfg.LastTokenWebhookMode = cfg.Mode; }
            else cfg.LastLegacyWebhookAt = cfg.LastWebhookAt;
            await _db.SaveChangesAsync(ct);

            // รอบ 198 ฝ่ายค้าน R-E1 (P0): ลายเซ็นผ่านด้วยคีย์ของบริษัทหนึ่ง ไม่ได้แปลว่ารายการที่ metadata อ้างเป็นของบริษัทนั้น —
            // โหลดรายการ**ในบริษัทของ config** เท่านั้น แล้วให้ตัวตัดสินกลางตรวจความเป็นเจ้าของ + ยอด ก่อนเปลี่ยนสถานะใด ๆ
            var owned = await _db.PaymentIntents.AsNoTracking()
                .Where(i => i.Id == verified.IntentId && i.CompanyId == cfg.CompanyId)
                .Select(i => new Accounting.Helpers.PaymentWebhookOwnership.IntentFacts(
                    i.CompanyId, i.ProviderConfigId, i.ProviderCode, i.Amount))
                .FirstOrDefaultAsync(ct);
            var reject = Accounting.Helpers.PaymentWebhookOwnership.RejectReason(owned, cfg.CompanyId, cfg.Id,
                providerCode, verified.Charge.Status, verified.Charge.Amount);
            if (reject != null)
            {
                // ห้ามตอบ error (ผู้ให้บริการจะ retry ไม่รู้จบ) แต่ต้องรู้ว่ามีคนยิงรายการที่ไม่ใช่ของบริษัทนี้เข้ามา
                _logger.LogWarning("webhook {Provider}: ปฏิเสธ event {Event} ของบริษัท {Company} รายการ {Intent} — {Reason}",
                    providerCode, verified.EventId, cfg.CompanyId, verified.IntentId, reject);
                return Ok(new { received = true, handled = false });
            }

            await _intents.ApplyChargeAsync(verified.IntentId, verified.Charge,
                PaymentEventSource.Webhook, $"webhook:{providerCode}", ct);
            return Ok(new { received = true, handled = true });
        }

        // ยืนยันไม่ผ่านกับ config ไหนเลย — อาจเป็นของปลอม หรือคีย์ถูกเปลี่ยน · หรือรหัสลับไม่ตรงกับร้านใด (ไม่มีคำขอออก)
        // ต้องรู้ว่ามีคนยิงเข้ามา แต่ห้ามตอบ error (provider จะ retry ไม่รู้จบ)
        _logger.LogWarning("webhook {Provider}: ยืนยันไม่ผ่านกับการตั้งค่าใดเลย ({Route} · ลอง {Count} การตั้งค่า)",
            providerCode, token == null ? "URL เดิม" : "URL รหัสลับ", configs.Count);
        return Ok(new { received = true, handled = false });
    }
}

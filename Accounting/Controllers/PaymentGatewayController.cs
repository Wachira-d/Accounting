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
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/pay")]
[Authorize]
public class PaymentGatewayController : ControllerBase
{
    private readonly IPaymentIntentService _intents;
    private readonly AccountingDbContext _db;

    public PaymentGatewayController(IPaymentIntentService intents, AccountingDbContext db)
    { _intents = intents; _db = db; }

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
    /// (webhook หายเป็นเรื่องที่เกิดจริง — poll คือตาข่ายรับ)</para></summary>
    [HttpGet("intents/{intentId:guid}/status")]
    public async Task<ActionResult<ApiResponse<IntentResponse>>> Status(
        Guid companyId, Guid intentId, [FromQuery] bool live = true, CancellationToken ct = default)
    {
        var intent = live
            ? await _intents.RefreshAsync(companyId, intentId, ct)
            : await _intents.FindAsync(companyId, intentId, ct);
        if (intent == null) return NotFound(new ApiResponse<IntentResponse>(false, null, "ไม่พบรายการชำระเงิน"));
        return Ok(new ApiResponse<IntentResponse>(true, Map(intent, await IsTestModeAsync(intent, ct))));
    }

    /// <summary>รายการชำระเงินของบริษัท — หน้าติดตาม/ตรวจสอบของผู้ดูแล</summary>
    [HttpGet("intents")]
    public async Task<ActionResult<ApiResponse<object>>> List(
        Guid companyId, [FromQuery] string? status, [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var q = _db.PaymentIntents.AsNoTracking().Where(i => i.CompanyId == companyId);
        if (Enum.TryParse<PaymentIntentStatus>(status, true, out var s))
            q = q.Where(i => i.Status == s);
        var rows = await q.OrderByDescending(i => i.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(i => new
            {
                i.Id, i.ProviderCode, sourceKind = i.SourceKind.ToString(), i.SourceId,
                i.Amount, status = i.Status.ToString(), method = i.MethodKind.ToString(),
                i.ProviderRef, i.FailureMessage, i.ConfirmedAt, i.ConfirmedBy,
                i.FeeActual, i.SettledAt, i.CreatedAt,
            })
            .ToListAsync(ct);
        return Ok(new ApiResponse<object>(true, rows));
    }

    /// <summary>กระทบยอดเงินที่รับผ่าน gateway ของงวดหนึ่ง
    ///
    /// <para>สมการที่ต้องเป็นจริง: <c>Σ charge − Σ คืนเงิน − Σ ค่าธรรมเนียม =
    /// Σ ที่โอนเข้าจริง + ที่ยังไม่ถึงรอบโอน</c> · <b>ผลต่างที่อธิบายไม่ได้ต้องเป็น 0</b>
    /// — ไม่เป็นศูนย์แปลว่ามีเงินหายหรือค่าธรรมเนียมไม่ตรงที่คาด ต้องมีคนดู</para></summary>
    [HttpGet("reconciliation")]
    public async Task<ActionResult<ApiResponse<object>>> Reconciliation(
        Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        var toDate = (to ?? DateTime.UtcNow).Date.AddDays(1);
        var fromDate = (from ?? DateTime.UtcNow.AddDays(-30)).Date;

        var rows = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId
                     && i.ConfirmedAt != null && i.ConfirmedAt >= fromDate && i.ConfirmedAt < toDate
                     && (i.Status == PaymentIntentStatus.Succeeded
                         || i.Status == PaymentIntentStatus.Refunded
                         || i.Status == PaymentIntentStatus.PartiallyRefunded))
            .Select(i => new GatewayIntentAmounts(
                i.Amount, i.FeeActual, i.FeeEstimated, i.SettledAmount,
                i.Status == PaymentIntentStatus.Refunded,
                i.SettledAt != null))
            .ToListAsync(ct);

        var result = GatewayReconciliation.Compute(rows);
        return Ok(new ApiResponse<object>(true, new
        {
            fromDate, toDate = toDate.AddDays(-1),
            result.SucceededCount, result.GrossCharged, result.RefundedAmount,
            result.FeeTotal, result.FeeIsEstimated, result.ExpectedNet,
            result.SettledTotal, result.UnsettledCount, result.UnsettledAmount,
            result.Difference, result.UnexplainedDifference, result.IsBalanced,
        }));
    }

    public sealed record ManualConfirmRequest(string Reason);
    public sealed record RefundRequest(decimal? Amount, string Reason);

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
    public async Task<ActionResult<ApiResponse<IntentResponse>>> ConfirmManually(
        Guid companyId, Guid intentId, [FromBody] ManualConfirmRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new ApiResponse<IntentResponse>(false, null,
                "กรุณาระบุเหตุผล — เช่น \"ตรวจสอบยอดในบัญชีธนาคารแล้วพบเงินเข้าจริง\" "
                + "(ผู้สอบบัญชีต้องเห็นว่าใครยืนยันและเพราะอะไร)"));

        var intent = await _intents.FindAsync(companyId, intentId, ct);
        if (intent == null)
            return NotFound(new ApiResponse<IntentResponse>(false, null, "ไม่พบรายการชำระเงิน"));

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

    /// <summary>คืนเงินผ่านผู้ให้บริการ
    ///
    /// <para><b>ไม่สร้างใบลดหนี้ให้อัตโนมัติ</b>โดยตั้งใจ: §86/10 บังคับให้ใบลดหนี้มี
    /// "เหตุผล" ตาม closed list · ต้องอ้างใบกำกับเดิม · และยอดสะสมของใบลดหนี้ห้ามเกิน
    /// ใบเดิม — สิ่งเหล่านี้ต้องให้คนตัดสิน ระบบเดาแทนไม่ได้ · คำตอบจึงชี้ทางต่อว่า
    /// ให้ไปออกใบลดหนี้จากเอกสารต้นทาง (ซึ่งมีด่าน §86/10 ครบอยู่แล้ว)</para></summary>
    [HttpPost("intents/{intentId:guid}/refund")]
    public async Task<ActionResult<ApiResponse<object>>> Refund(
        Guid companyId, Guid intentId, [FromBody] RefundRequest req,
        [FromServices] IEnumerable<IPaymentProvider> providers, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new ApiResponse<object>(false, null!, "กรุณาระบุเหตุผลการคืนเงิน"));

        var intent = await _db.PaymentIntents
            .FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);
        if (intent == null) return NotFound(new ApiResponse<object>(false, null!, "ไม่พบรายการชำระเงิน"));
        if (intent.Status is not (PaymentIntentStatus.Succeeded or PaymentIntentStatus.PartiallyRefunded))
            return BadRequest(new ApiResponse<object>(false, null!,
                "คืนเงินได้เฉพาะรายการที่รับเงินสำเร็จแล้ว"));

        var amount = req.Amount ?? intent.Amount;
        if (amount <= 0 || amount > intent.Amount + 0.005m)
            return BadRequest(new ApiResponse<object>(false, null!,
                $"ยอดคืนต้องมากกว่า 0 และไม่เกินยอดที่รับไว้ ({intent.Amount:N2})"));

        var provider = providers.FirstOrDefault(p => p.ProviderCode == intent.ProviderCode);
        if (provider == null)
            return BadRequest(new ApiResponse<object>(false, null!,
                $"ไม่รู้จักช่องทางชำระเงิน \"{intent.ProviderCode}\""));
        if (!provider.Capabilities.SupportsRefund)
            return BadRequest(new ApiResponse<object>(false, null!,
                "ช่องทางนี้คืนเงินผ่านระบบไม่ได้ — ต้องคืนที่ธนาคาร/แดชบอร์ดของผู้ให้บริการเอง "
                + "แล้วออกใบลดหนี้ในระบบ (§86/10)"));

        var config = intent.ProviderConfigId is Guid cid
            ? await _db.PaymentProviderConfigs.FirstOrDefaultAsync(c => c.Id == cid, ct)
            : null;

        var result = await provider.RefundAsync(intent, amount, req.Reason.Trim(),
            config ?? new PaymentProviderConfig { CompanyId = companyId, ProviderCode = intent.ProviderCode }, ct);

        if (!result.Succeeded)
            return BadRequest(new ApiResponse<object>(false, null!,
                result.FailureMessage ?? "ผู้ให้บริการปฏิเสธการคืนเงิน"));

        var actor = $"manual:{JwtHelper.GetUserIdFromClaims(User)}";
        var full = amount >= intent.Amount - 0.005m;
        await _intents.ApplyChargeAsync(intentId,
            new ProviderCharge(intent.ProviderRef ?? string.Empty,
                full ? PaymentIntentStatus.Refunded : PaymentIntentStatus.PartiallyRefunded,
                "refunded", intent.Amount),
            PaymentEventSource.Manual, $"{actor} · คืนเงิน {amount:N2}: {req.Reason.Trim()}", ct);

        return Ok(new ApiResponse<object>(true, new
        {
            refundRef = result.ProviderRefundRef,
            amount,
            isFullRefund = full,
            // ขั้นถัดไปที่ระบบทำแทนไม่ได้ — ต้องบอกให้ชัด ไม่ใช่ปล่อยให้ผู้ใช้เดา
            nextStep = "เปิดเอกสารต้นทางแล้วออก \"ใบลดหนี้\" (§86/10) เพื่อลดภาษีขาย — "
                + "ระบบไม่ออกให้อัตโนมัติเพราะใบลดหนี้ต้องระบุเหตุผลตามที่กฎหมายกำหนด "
                + "และยอดสะสมห้ามเกินใบเดิม ซึ่งต้องให้คนตัดสิน",
        }, $"คืนเงิน {amount:N2} บาทเรียบร้อย"));
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
        outcome.Plan.ExpectedNet,
        outcome.Plan.ActualNet,
        outcome.Plan.Difference,
        lines = outcome.Plan.Lines.Select(l => new
        { role = l.Role.ToString(), l.Debit, l.Credit, l.Description }),
        outcome.JournalEntryId,
        outcome.JournalEntryNumber,
    };

    /// <summary>รายการที่ "รับเงินแล้วแต่ยังไม่โอนเข้าธนาคาร" — ตั้งต้นของหน้าบันทึกการโอน</summary>
    [HttpGet("settlements/pending")]
    public async Task<ActionResult<ApiResponse<object>>> PendingSettlement(
        Guid companyId, [FromQuery] string? providerCode, CancellationToken ct)
    {
        var rows = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId
                && i.Status == PaymentIntentStatus.Succeeded
                && i.SettlementJournalEntryId == null
                && i.ConfirmedAt != null
                && (providerCode == null || i.ProviderCode == providerCode))
            .OrderBy(i => i.ConfirmedAt)
            .Select(i => new
            {
                i.Id, i.ProviderCode, i.ConfirmedAt, i.Amount,
                fee = i.FeeActual ?? i.FeeEstimated,
                feeIsEstimated = i.FeeActual == null,
                i.ProviderRef, sourceKind = i.SourceKind.ToString(),
            })
            .ToListAsync(ct);

        return Ok(new ApiResponse<object>(true, new
        {
            count = rows.Count,
            gross = rows.Sum(r => r.Amount),
            fee = rows.Sum(r => r.fee),
            expectedNet = rows.Sum(r => r.Amount - r.fee),
            // ค่าธรรมเนียมที่ยังเป็นตัวประมาณต้องติดป้าย — ตัวเลขประมาณที่ไม่ติดป้าย
            // จะถูกอ่านเป็นตัวจริงแล้วนำไปตัดสินใจผิด
            anyFeeEstimated = rows.Any(r => r.feeIsEstimated),
            items = rows,
        }));
    }

    /// <summary>ดูตัวอย่างก่อนบันทึก — ไม่เขียนอะไรเลย (ให้ผู้ใช้เห็น JE ก่อนกดจริง)</summary>
    [HttpPost("settlements/preview")]
    public async Task<ActionResult<ApiResponse<object>>> PreviewSettlement(
        Guid companyId, [FromBody] SettlementRequestDto req,
        [FromServices] IGatewaySettlementService settlements, CancellationToken ct)
    {
        var outcome = await settlements.PreviewAsync(companyId, ToServiceRequest(req), ct);
        return Ok(new ApiResponse<object>(true, MapPlan(outcome), outcome.Message));
    }

    [HttpPost("settlements")]
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

    /// <summary>ประวัติของรายการเดียว — "ลูกค้าบอกว่าจ่ายแล้วแต่ระบบไม่รู้" ตอบจากตรงนี้</summary>
    [HttpGet("intents/{intentId:guid}/events")]
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

    [HttpPost("{providerCode}")]
    public async Task<IActionResult> Receive(string providerCode, CancellationToken ct)
    {
        var provider = _providers.FirstOrDefault(p => p.ProviderCode == providerCode);
        if (provider == null) return Ok(new { received = true, handled = false });

        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync(ct);
        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        // ยังไม่รู้ว่าเป็นของบริษัทไหน — ลองยืนยันกับทุก config ของ provider นี้
        // ที่เปิดใช้อยู่ · adapter ที่ยืนยันไม่ผ่านจะคืน null (ไม่ throw)
        var configs = await _db.PaymentProviderConfigs
            .Where(c => c.ProviderCode == providerCode && c.IsActive && !c.IsDeleted)
            .ToListAsync(ct);

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
            await _db.SaveChangesAsync(ct);

            await _intents.ApplyChargeAsync(verified.IntentId, verified.Charge,
                PaymentEventSource.Webhook, $"webhook:{providerCode}", ct);
            return Ok(new { received = true, handled = true });
        }

        // ยืนยันไม่ผ่านกับ config ไหนเลย — อาจเป็นของปลอม หรือคีย์ถูกเปลี่ยน
        // ต้องรู้ว่ามีคนยิงเข้ามา แต่ห้ามตอบ error (provider จะ retry ไม่รู้จบ)
        _logger.LogWarning("webhook {Provider}: ยืนยันไม่ผ่านกับการตั้งค่าใดเลย", providerCode);
        return Ok(new { received = true, handled = false });
    }
}

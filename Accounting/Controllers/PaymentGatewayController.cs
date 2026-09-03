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

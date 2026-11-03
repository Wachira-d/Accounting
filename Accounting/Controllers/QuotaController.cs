using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 229 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §B/§D) — ฝั่งผู้เช่า: <b>โควตาทั้งหมดในที่เดียว + ซื้อโควตาเสริม + เลือกต่ออายุ</b>
/// <list type="bullet">
/// <item><c>GET summary</c> — ใช้ไป/เพดาน/ระดับเตือน ทุกทรัพยากร (เซิร์ฟเวอร์คำนวณ — หน้าเว็บแสดงอย่างเดียว · โหลดไม่ได้ ≠ ไม่จำกัด)</item>
/// <item><c>GET offers</c> — ยอดของทุกวิธีซื้อ (เซิร์ฟเวอร์คิด) · <c>POST purchases</c> สร้างรายการ (ส่งยอดที่เห็นมาเทียบ — ไม่ตรง ⇒ 409)</item>
/// <item>ชำระ: <c>purchases/{id}/intents</c> (PayWidget โหมด publicBase — intent ใต้บริษัทผู้ให้บริการ) หรือแนบสลิป · ติ๊กยอมรับนโยบายบังคับ</item>
/// </list>
/// ทุกการเขียนต้องมีสิทธิ์ "จัดการค่าใช้จ่าย/ส่วนเสริม" (<c>BillingManage</c>) — ก่อค่าใช้จ่ายให้บริษัท
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/quota")]
[Authorize]
public class QuotaController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IQuotaGate _gate;
    private readonly IQuotaAddOnPurchaseService _purchases;

    public QuotaController(AccountingDbContext db, IQuotaGate gate, IQuotaAddOnPurchaseService purchases)
    { _db = db; _gate = gate; _purchases = purchases; }

    private string Actor => User.Identity?.Name ?? "unknown";

    private string? IdempotencyKeyHeader()
    {
        var v = Request?.Headers["Idempotency-Key"].ToString();
        return string.IsNullOrWhiteSpace(v) ? null : (v.Length > 200 ? v[..200] : v);
    }

    /// <summary>สรุปโควตาทุกทรัพยากร — ตัวเดียวของหน้า แพ็กเกจ/ศูนย์บิล/ทีม/เว็บไซต์ (D-4)</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<object>>> Summary(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<object>(true, new { items = await _gate.SummaryAsync(companyId, ct), generatedAt = DateTime.UtcNow }));

    /// <summary>ด่านของทรัพยากรเดียว (หน้าเว็บใช้ปิดปุ่ม "สร้าง" ล่วงหน้า + ข้อความเดียวกับด่านจริง)</summary>
    [HttpGet("check/{resource}")]
    public async Task<ActionResult<ApiResponse<object>>> Check(Guid companyId, string resource, CancellationToken ct)
    {
        if (!QuotaResources.IsKnown(resource) || QuotaResources.Find(resource)!.PerSite)
            return BadRequest(new ApiResponse<object>(false, null, "ไม่รู้จักทรัพยากรนี้"));
        return Ok(new ApiResponse<object>(true, await _gate.CheckAsync(companyId, resource, 1, ct)));
    }

    [HttpGet("offers")]
    public async Task<ActionResult<ApiResponse<object>>> Offers(Guid companyId, [FromQuery] int units = 1, CancellationToken ct = default)
        => Ok(new ApiResponse<object>(true, await _purchases.OffersAsync(companyId, Math.Clamp(units, 1, QuotaAddOnPricing.MaxUnitsPerPurchase), ct)));

    public sealed record CreatePurchaseBody(string Resource, string Mode, int Units, decimal? ExpectedPerUnit, decimal? ExpectedAmount);

    [HttpPost("purchases")]
    [Accounting.Filters.RejectApiKey("ซื้อโควตาเสริม")]
    [Accounting.Filters.RequirePermission(PermissionKeys.BillingManage)]
    public async Task<ActionResult<ApiResponse<object>>> Create(Guid companyId, [FromBody] CreatePurchaseBody body, CancellationToken ct)
    {
        if (!Enum.TryParse<QuotaAddOnMode>(body.Mode, true, out var mode))
            return BadRequest(new ApiResponse<object>(false, null, "วิธีซื้อไม่ถูกต้อง"));
        try
        {
            var row = await _purchases.CreateAsync(companyId,
                new QuotaPurchaseRequest(body.Resource ?? "", mode, body.Units, body.ExpectedPerUnit, body.ExpectedAmount), Actor, IdempotencyKeyHeader(), ct);
            return Ok(new ApiResponse<object>(true, Map(row), "สร้างรายการแล้ว — ชำระเงินเพื่อเริ่มใช้ได้ทันที"));
        }
        catch (QuotaOfferChangedException ex)
        {
            // ยอด/ราคาเปลี่ยนระหว่างที่ผู้ซื้อดูอยู่ ⇒ 409 + ข้อเสนอใหม่ (ยังไม่คิดเงิน) ให้หน้าเว็บวาดใหม่แล้วยืนยันอีกครั้ง
            return StatusCode(409, new ApiResponse<object>(false,
                new { ruleCode = ex.RuleCode, offers = await _purchases.OffersAsync(companyId, Math.Max(1, body.Units), ct) }, ex.Message));
        }
    }

    [HttpGet("purchases")]
    public async Task<ActionResult<ApiResponse<object>>> List(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<object>(true, (await _purchases.PurchasesAsync(companyId, ct)).Select(Map).ToList()));

    public sealed record StartBody(PaymentMethodKind Method = PaymentMethodKind.PromptPay, string? ReturnUrl = null, string? CardToken = null,
        bool PolicyAccepted = false, string? PolicyVersionShown = null);

    /// <summary>PayWidget โหมด publicBase: POST <c>{base}/intents</c> — ยอดจากเซิร์ฟเวอร์ (AmountDue ของรายการ) · ติ๊กยอมรับนโยบายก่อน</summary>
    [HttpPost("purchases/{purchaseId:guid}/intents")]
    [Accounting.Filters.RejectApiKey("ชำระค่าโควตาเสริม")]
    [Accounting.Filters.RequirePermission(PermissionKeys.BillingManage)]
    public async Task<ActionResult<ApiResponse<object>>> StartOnline(Guid companyId, Guid purchaseId, [FromBody] StartBody req,
        [FromServices] ILegalDocumentService legal, CancellationToken ct)
    {
        if (!await _db.CompanyQuotaAddOns.AsNoTracking().AnyAsync(p => p.Id == purchaseId && p.CompanyId == companyId, ct))
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการซื้อโควตาเสริมของบริษัทนี้"));
        try
        {
            await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(_db, legal, companyId, User,
                PurchasePolicyAcceptance.ChannelPlatformQuotaAddOn, purchaseId, req.PolicyAccepted, req.PolicyVersionShown,
                HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
            var intent = await _purchases.StartOnlineAsync(companyId, purchaseId, req.Method, req.ReturnUrl, req.CardToken, ct);
            await _db.SaveChangesAsync(ct);
            return Ok(new ApiResponse<object>(true, MapIntent(intent)));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("purchases/{purchaseId:guid}/intents/{intentId:guid}")]
    [Accounting.Filters.RequirePermission(PermissionKeys.BillingManage)]
    public async Task<ActionResult<ApiResponse<object>>> IntentStatus(Guid companyId, Guid purchaseId, Guid intentId,
        [FromServices] IPlatformPaymentService platform, CancellationToken ct)
    {
        var intent = await platform.RefreshForPayerAsync(companyId, intentId, ct);
        return intent == null || intent.SourceId != purchaseId
            ? NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการชำระเงิน"))
            : Ok(new ApiResponse<object>(true, MapIntent(intent)));
    }

    /// <summary>โอน + แนบสลิป (ช่องทางสำรองเมื่อยังไม่เปิดชำระออนไลน์) — ให้สิทธิ์เมื่อผู้ดูแลระบบอนุมัติแล้ว</summary>
    [HttpPost("purchases/{purchaseId:guid}/slip")]
    [RequestSizeLimit(SubscriptionSlipUpload.MaxBytes + 64 * 1024)]
    [Accounting.Filters.RejectApiKey("ส่งหลักฐานการชำระเงิน")]
    [Accounting.Filters.RequirePermission(PermissionKeys.BillingManage)]
    public async Task<ActionResult<ApiResponse<object>>> UploadSlip(Guid companyId, Guid purchaseId, IFormFile? file,
        [FromServices] IImageProcessingService images, [FromServices] IWebHostEnvironment env, [FromServices] ILegalDocumentService legal,
        CancellationToken ct, [FromForm] string? reference = null, [FromForm] bool policyAccepted = false, [FromForm] string? policyVersionShown = null,
        [FromForm] DateTime? transferDate = null)
    {
        if (!await _db.CompanyQuotaAddOns.AsNoTracking().AnyAsync(p => p.Id == purchaseId && p.CompanyId == companyId, ct))
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการซื้อโควตาเสริมของบริษัทนี้"));
        if (SubscriptionSlipUpload.Reject(file) is string bad) return BadRequest(new ApiResponse<object>(false, null, bad));
        await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(_db, legal, companyId, User,
            PurchasePolicyAcceptance.ChannelPlatformQuotaAddOn, purchaseId, policyAccepted, policyVersionShown,
            HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
        var stored = await SubscriptionSlipUpload.StoreAsync(file!, images, env);
        try
        {
            // S7: วันที่โอน (วันที่ไทยจากฟอร์ม) = วันที่ใบเสร็จ — แปลงเป็นเที่ยงวันไทยของวันนั้น (กันเลื่อนวันจากเขตเวลา)
            var transferUtc = transferDate is DateTime td ? DateTime.SpecifyKind(td.Date.AddHours(12 - 7), DateTimeKind.Utc) : (DateTime?)null;
            var row = await _purchases.UploadSlipAsync(companyId, purchaseId, stored.FileName, reference, Actor, ct, transferUtc);
            return Ok(new ApiResponse<object>(true, Map(row), "ส่งหลักฐานการชำระเงินแล้ว — ผู้ดูแลระบบจะตรวจสอบ เมื่ออนุมัติแล้วใช้ได้ทันทีและได้ใบเสร็จ/ใบกำกับภาษี"));
        }
        catch { SubscriptionSlipUpload.DeleteStored(stored); throw; }
    }

    [HttpPost("purchases/{purchaseId:guid}/cancel")]
    [Accounting.Filters.RejectApiKey("ยกเลิกโควตาเสริม")]
    [Accounting.Filters.RequirePermission(PermissionKeys.BillingManage)]
    public async Task<ActionResult<ApiResponse<object>>> Cancel(Guid companyId, Guid purchaseId, CancellationToken ct)
    {
        var row = await _purchases.CancelAsync(companyId, purchaseId, Actor, ct);
        var msg = row.Status == QuotaAddOnStatus.Cancelled ? "ยกเลิกรายการแล้ว"
            : row.Mode == QuotaAddOnMode.RecurringMonthly ? $"ยกเลิกแล้ว — ใช้ได้ถึง {QuotaAddOnPricing.ThaiDateText(row.PeriodEndUtc!.Value.AddHours(7).Date.AddDays(-1))} ไม่คิดค่าเดือนถัดไป (ไม่คืนเงินเดือนนี้)"
            : "ตั้งไม่ต่ออายุแล้ว — ใช้ได้ถึงวันหมดอายุแพ็กเกจ";
        return Ok(new ApiResponse<object>(true, Map(row), msg));
    }

    /// <summary>หน้าต่ออายุ: รายการโควตาเสริมแบบถึงวันหมดอายุที่จะต่อพร้อมแพ็กเกจ (ราคาเต็มรอบ) — ค่าตั้งต้น = ต่อ (คำตัดสินข้อ 229-3)</summary>
    [HttpGet("renewal")]
    public async Task<ActionResult<ApiResponse<object>>> Renewal(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<object>(true, await _purchases.RenewalPreviewAsync(companyId, ct)));

    public sealed record RenewalBody(bool Renew, int? Units);

    [HttpPost("renewal/{purchaseId:guid}")]
    [Accounting.Filters.RejectApiKey("เลือกรายการต่ออายุ")]
    [Accounting.Filters.RequirePermission(PermissionKeys.BillingManage)]
    public async Task<ActionResult<ApiResponse<object>>> SetRenewal(Guid companyId, Guid purchaseId, [FromBody] RenewalBody body, CancellationToken ct)
    {
        var row = await _purchases.SetRenewalAsync(companyId, purchaseId, body.Renew, body.Units, Actor, ct);
        return Ok(new ApiResponse<object>(true, Map(row), body.Renew ? "จะต่อพร้อมแพ็กเกจรอบหน้า" : "ไม่ต่อ — หมดพร้อมรอบปัจจุบัน (ข้อมูลไม่หาย)"));
    }

    internal static object Map(CompanyQuotaAddOn p)
    {
        var res = QuotaResources.Find(p.ResourceKey);
        return new
        {
            id = p.Id, resource = p.ResourceKey, label = res?.Label ?? p.ResourceKey, unit = res?.Unit, units = p.Units, unitSize = p.UnitSize,
            mode = p.Mode.ToString(), modeLabel = QuotaAddOnPurchaseService.ModeLabel(p.Mode),
            status = p.Status.ToString(), statusLabel = QuotaAddOnPurchaseService.StatusLabel(p.Status),
            live = QuotaLimitPolicy.IsLive(p.Status, p.PeriodStartUtc, p.PeriodEndUtc, DateTime.UtcNow),
            periodStart = p.PeriodStartUtc, periodEnd = p.PeriodEndUtc, perUnit = p.UnitPriceSnapshot, amountDue = p.AmountDue,
            monthlyUnitPrice = p.MonthlyUnitPriceSnapshot, note = p.ProrationNote, cancelAtPeriodEnd = p.CancelAtPeriodEnd,
            renewWithPlan = p.RenewWithPlan, renewUnits = p.RenewUnits, renewalLocked = p.RenewalInvoiceDocumentId != null,
            rejectedReason = p.RejectedReason, receiptNumber = p.ReceiptNumber, receiptDocumentId = p.ReceiptDocumentId,
            receivedAmount = p.ReceivedAmount, attentionRule = p.AttentionRule, attentionReason = p.AttentionReason, offlineReference = p.OfflineReference,
            slipUploadedAt = p.SlipUploadedAt, createdAt = p.CreatedAt,
            canPay = p.Status == QuotaAddOnStatus.AwaitingPayment || p.Status == QuotaAddOnStatus.Rejected,
            canCancel = p.Status is QuotaAddOnStatus.AwaitingPayment or QuotaAddOnStatus.Rejected
                || (p.Status == QuotaAddOnStatus.Active && p.Mode == QuotaAddOnMode.RecurringMonthly && !p.CancelAtPeriodEnd),
        };
    }

    private static object MapIntent(PaymentIntent i) => new
    {
        id = i.Id, status = i.Status.ToString(), amount = i.Amount, currency = i.Currency,
        qrPayload = i.QrPayload, qrExpiresAt = i.QrExpiresAt, authorizeUrl = i.AuthorizeUrl,
        failureMessage = i.FailureMessage, isTestMode = PaymentIntentTestMode.Decide(i.IsTestMode, null),
    };
}

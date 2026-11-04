using System.Security.Claims;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 232 (คำตัดสิน 232-Checkout-2) — <b>หน้าซื้อแพ็กเกจสาธารณะของ NextAcc</b>: <c>/pricing.html</c> (cart) → <c>/checkout.html</c> (สมัคร/เข้าสู่ระบบในหน้าเดียว
/// → ติ๊กยอมรับนโยบาย → จ่ายด้วย <c>pay-widget.js</c> ในหน้าเดียวกัน) → <c>/checkout-success.html</c> (poll สถานะ · ใบเสร็จ) — ไม่มีขั้นที่ต้องติดต่อเรา/LINE (ข้อกำหนด Omise)
/// <list type="bullet">
/// <item>สมัคร/เข้าสู่ระบบใช้ <c>api/auth/register</c>/<c>login</c> เดิม (เส้นถือรหัสผ่านไม่เข้าระบบคีย์กันซ้ำ — <see cref="IdempotencyKeyPolicy.IsCredentialPath"/>) ·
/// หลังได้โทเคน endpoint ที่แตะเงินเป็น <c>[Authorize]</c> + ด่านเจ้าของบริษัท (ไม่เปิดข้อมูลบริษัทอื่น)</item>
/// <item>แคตตาล็อก/ยอดตั้งต้นเปิดสาธารณะ (ไม่มีข้อมูลลูกค้า) · โควตาต่อ IP + ทั้งระบบบน PostgreSQL (<see cref="IChatRateLimiter"/> · fail-closed) ทุกเส้นที่เขียน</item>
/// <item>ยอดเงิน/โหมด/สถานะทั้งหมดมาจากเซิร์ฟเวอร์ (<see cref="IPublicCheckoutService"/> · <see cref="PublicCheckoutPolicy"/>) — หน้าเว็บแสดงอย่างเดียว</item>
/// <item>รูปเส้นทางเข้ากับ <c>PayWidget</c> โหมด <c>publicBase</c> (POST <c>{base}/intents</c> · GET <c>{base}/intents/{id}</c>) + <c>authHeader</c> ของหน้า</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/public/checkout")]
public class PublicCheckoutController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPublicCheckoutService _checkout;
    private readonly ILegalDocumentService _legal;
    private readonly IChatRateLimiter _rate;
    private readonly ISaasBillingDocumentService _billing;
    private readonly IConfiguration _config;
    private readonly IPlatformSavedCardService _savedCards;

    public PublicCheckoutController(AccountingDbContext db, IPublicCheckoutService checkout, ILegalDocumentService legal, IChatRateLimiter rate,
        ISaasBillingDocumentService billing, IConfiguration config, IPlatformSavedCardService savedCards)
    { _db = db; _checkout = checkout; _legal = legal; _rate = rate; _billing = billing; _config = config; _savedCards = savedCards; }

    private string Ip() => Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() is { Length: > 0 } f ? f
        : HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>โควตาต่อ IP + ทั้งระบบ (fail-closed — ตารางนับพัง = ปฏิเสธ ไม่เปิดเงียบ)</summary>
    private async Task<bool> RateOkAsync(string bucket, CancellationToken ct)
        => await _rate.TryConsumeAsync($"public-checkout:{bucket}:{Ip()}", PublicCheckoutPolicy.StartPerMinutePerIp, PublicCheckoutPolicy.StartPerDayPerIp, ct, failClosed: true)
           && await _rate.TryConsumeAsync($"public-checkout:{bucket}:global", PublicCheckoutPolicy.StartPerMinuteGlobal, PublicCheckoutPolicy.StartPerDayGlobal, ct, failClosed: true);

    /// <summary>ฝ่ายค้าน S3: โควตายอดตะกร้าต่อผู้ใช้ (ไม่ใช่ต่อ IP — ผู้ใช้คนเดียวเปลี่ยน IP ได้) + ทั้งระบบ</summary>
    private async Task<bool> UserQuoteRateOkAsync(Guid userId, CancellationToken ct)
        => await _rate.TryConsumeAsync($"public-checkout:quote:user:{userId}", PublicCheckoutPolicy.QuotePerMinutePerUser, PublicCheckoutPolicy.QuotePerDayPerUser, ct, failClosed: true)
           && await _rate.TryConsumeAsync("public-checkout:quote:global", PublicCheckoutPolicy.StartPerMinuteGlobal, PublicCheckoutPolicy.StartPerDayGlobal, ct, failClosed: true);

    /// <summary>ฝ่ายค้าน S3: รหัสคูปอง<b>ต่างกัน</b>ต่อผู้ใช้ต่อหน้าต่าง 10 นาที — ตัวตรวจ "รหัสใหม่ในหน้าต่างนี้" = bucket ต่อ (ผู้ใช้, หน้าต่าง, รหัส) โควตา 1
    /// (ครั้งแรก = true) · รหัสใหม่ ⇒ นับใน bucket ต่อ (ผู้ใช้, หน้าต่าง) โควตา <see cref="PublicCheckoutPolicy.CouponCodesPerWindowPerUser"/> ·
    /// ตรวจ<b>ก่อน</b>ประเมินคูปอง (ไม่งั้น 200 กับ 429 กลายเป็นตัวบอกว่ารหัสถูก) · ถามรหัสเดิมซ้ำไม่กินโควตา</summary>
    private async Task<bool> CouponAttemptOkAsync(Guid userId, string? coupon, CancellationToken ct)
    {
        var code = PlatformCouponRule.Normalize(coupon);
        if (code == null) return true;
        var window = PublicCheckoutPolicy.CouponWindowId(DateTime.UtcNow);
        var firstUse = await _rate.TryConsumeAsync($"public-checkout:coupon-code:{userId}:{window}:{code}", 1, 1, ct, failClosed: true);
        if (!firstUse) return true;   // รหัสเดิมในหน้าต่างนี้ (หรือตารางนับพัง ⇒ ไม่นับเพิ่ม — ด่านถัดไปยัง fail-closed)
        return await _rate.TryConsumeAsync($"public-checkout:coupon-codes:{userId}:{window}", PublicCheckoutPolicy.CouponCodesPerWindowPerUser,
            PublicCheckoutPolicy.CouponCodesPerWindowPerUser, ct, failClosed: true);
    }

    private static ActionResult<ApiResponse<T>> TooManyOf<T>(string message) => new ObjectResult(new ApiResponse<T>(false, default, message)) { StatusCode = 429 };

    private static ActionResult<ApiResponse<object>> TooMany() => new ObjectResult(new ApiResponse<object>(false, null, "คำขอถี่เกินไป — รอสักครู่แล้วลองใหม่")) { StatusCode = 429 };

    private Task<ObjectResult?> OwnerDenyAsync(Guid companyId, string verb)
        => Accounting.Filters.RequireOwnerAttribute.DenyAsync(HttpContext, _db, companyId, verb, "การซื้อแพ็กเกจ NextAcc");

    /// <summary>แคตตาล็อก (สาธารณะ): สวิตช์ · โหมดทดสอบ · แพ็กเกจ+ราคาทุกรอบ · วิธีชำระ · ลิงก์นโยบาย — ปิดอยู่ ⇒ 404 พร้อมเหตุผล (หน้าเว็บแสดง)</summary>
    [HttpGet("catalog")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<PublicCheckoutCatalog>>> Catalog(CancellationToken ct)
    {
        var c = await _checkout.CatalogAsync(ct);
        return c.Enabled ? Ok(new ApiResponse<PublicCheckoutCatalog>(true, c)) : NotFound(new ApiResponse<PublicCheckoutCatalog>(false, c, c.Reason));
    }

    /// <summary>ยอดตั้งต้นของตะกร้า (สาธารณะ · ราคาตั้ง — คูปอง/ราคาพิเศษคิดหลังรู้บริษัท)</summary>
    [HttpGet("quote")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<PublicCheckoutQuote>>> Quote([FromQuery] SubscriptionPlan plan, [FromQuery] BillingCycle cycle,
        [FromQuery] string? coupon, CancellationToken ct)
    {
        var avail = await _checkout.AvailabilityAsync(ct);
        if (!avail.Enabled) return NotFound(new ApiResponse<PublicCheckoutQuote>(false, null, avail.Reason));
        return Ok(new ApiResponse<PublicCheckoutQuote>(true, await _checkout.QuoteAsync(null, plan, cycle, coupon, ct)));
    }

    /// <summary>บริษัทที่ผู้ใช้เป็นเจ้าของ (หลังเข้าสู่ระบบ — เลือกซื้อให้บริษัทไหน)</summary>
    [HttpGet("me")]
    [Authorize]
    [Accounting.Filters.RejectApiKey("ซื้อแพ็กเกจผ่านหน้าสาธารณะ")]
    public async Task<ActionResult<ApiResponse<object>>> Me(CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var rows = await _checkout.OwnedCompaniesAsync(uid, ct);
        return Ok(new ApiResponse<object>(true, new
        {
            userId = uid, email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
            // รอบ 236: label = ป้ายแพ็กเกจที่มีผล (PlanIdentityResolver.Label) · purchaseBlocked = เหตุที่ซื้อเองไม่ได้ (License คุมอยู่) — หน้าเว็บแสดงอย่างเดียว
            companies = rows.Select(r => new { id = r.Id, name = r.Name, plan = r.Plan, status = r.Status, lowerPlans = r.LowerPlans, lowerNote = r.LowerNote,
                label = r.Label, purchaseBlocked = r.PurchaseBlocked, licenseRenewalPlan = r.LicenseRenewalPlan }).ToList(),
        }));
    }

    /// <summary>ยอดของตะกร้าสำหรับบริษัทนี้ (ราคาพิเศษ + คูปอง — ตัวเดียวกับตอนยืนยัน)</summary>
    [HttpGet("companies/{companyId:guid}/quote")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<PublicCheckoutQuote>>> CompanyQuote(Guid companyId, [FromQuery] SubscriptionPlan plan, [FromQuery] BillingCycle cycle,
        [FromQuery] string? coupon, CancellationToken ct)
    {
        if (await OwnerDenyAsync(companyId, "ดูยอดแพ็กเกจ") is { } deny) return deny;
        var avail = await _checkout.AvailabilityAsync(ct);
        if (!avail.Enabled) return NotFound(new ApiResponse<PublicCheckoutQuote>(false, null, avail.Reason));
        // ฝ่ายค้าน S3: คูปองไล่เดาได้ — โควตาต่อผู้ใช้ + รหัสต่างกันต่อ 10 นาที (ตรวจก่อนประเมินคูปอง)
        var uid = JwtHelper.GetUserIdFromClaims(User);
        if (!await UserQuoteRateOkAsync(uid, ct)) return TooManyOf<PublicCheckoutQuote>("คำขอถี่เกินไป — รอสักครู่แล้วลองใหม่");
        if (!await CouponAttemptOkAsync(uid, coupon, ct)) return TooManyOf<PublicCheckoutQuote>(PublicCheckoutPolicy.CouponTooManyMessage);
        return Ok(new ApiResponse<PublicCheckoutQuote>(true, await _checkout.QuoteAsync(companyId, plan, cycle, coupon, ct, uid)));   // รอบ 236 B1
    }

    /// <param name="WithholdTax">รอบ 234 (คำตัดสิน 234-1): ผู้ซื้อติ๊ก "หัก ณ ที่จ่าย" — ส่งแค่ true/false · ยอดหักคิดที่เซิร์ฟเวอร์ (null = ไม่หัก)</param>
    public sealed record OrderRequest(SubscriptionPlan Plan, BillingCycle Cycle, string? CouponCode, bool PolicyAccepted, string? PolicyVersionShown,
        bool? WithholdTax = null);

    /// <summary>สร้าง (หรือใช้ต่อ) รายการค่าแพ็กเกจ — ติ๊กยอมรับข้อกำหนด+นโยบายคืนเงินบังคับ (ตรวจก่อนสร้าง · หลักฐานผูกรายการ) · ยอดจากเซิร์ฟเวอร์ · อีเมลยืนยัน</summary>
    [HttpPost("companies/{companyId:guid}/orders")]
    [Authorize]
    [Accounting.Filters.RejectApiKey("ซื้อแพ็กเกจผ่านหน้าสาธารณะ")]
    public async Task<ActionResult<ApiResponse<object>>> CreateOrder(Guid companyId, [FromBody] OrderRequest req, CancellationToken ct)
    {
        if (await OwnerDenyAsync(companyId, "ซื้อแพ็กเกจ") is { } deny) return deny;
        if (!await RateOkAsync("order", ct)) return TooMany();
        // ฝ่ายค้าน S3: เส้นสร้างรายการก็ประเมินคูปอง — ใช้ตัวนับรหัสเดียวกับยอดตะกร้า (รหัสที่ถามยอดไว้แล้วไม่นับซ้ำ)
        if (!await CouponAttemptOkAsync(JwtHelper.GetUserIdFromClaims(User), req.CouponCode, ct))
            return TooManyOf<object>(PublicCheckoutPolicy.CouponTooManyMessage);
        // ด่านยอมรับนโยบายก่อนสร้างอะไร (ตัวตัดสินเดียว PurchasePolicyAcceptance.Problem) — ไม่ผ่าน = ไม่สร้างรายการ
        var current = await _legal.GetPurchasePolicyVersionAsync(ct);
        if (PurchasePolicyAcceptance.Problem(false, req.PolicyAccepted, req.PolicyVersionShown, current.Version) is string problem)
            return BadRequest(new ApiResponse<object>(false, null, problem));
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var order = await _checkout.CreateOrderAsync(uid, companyId, req.Plan, req.Cycle, req.CouponCode, ct, req.WithholdTax);
        // หลักฐานการยอมรับผูกกับรายการ (ช่องเดียวกับหน้าชำระในระบบ) · ฝ่ายค้าน S5: ผู้ใช้คนนี้ยอมรับฉบับนี้ของรายการนี้ไว้แล้ว (resume/กดซ้ำ) ⇒ ไม่เพิ่มแถวซ้ำ
        var alreadyAccepted = await PurchasePolicyAcceptance.UserAlreadyAcceptedAsync(_db, _legal, companyId, User,
            PurchasePolicyAcceptance.ChannelPlatformSubscription, order.PaymentId, ct);
        await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(_db, _legal, companyId, User, PurchasePolicyAcceptance.ChannelPlatformSubscription,
            order.PaymentId, accepted: !alreadyAccepted, req.PolicyVersionShown, Ip(), Request.Headers.UserAgent.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        if (!order.Resumed)
        {
            var origin = SystemBaseUrl.PublicOrigin(await SystemBaseUrl.ResolveAsync(_db, _config, ct), Request.Scheme, Request.Host.ToString());
            await _checkout.SendOrderEmailAsync(uid, order, origin, ct);
        }
        return Ok(new ApiResponse<object>(true, order, order.Message));
    }

    /// <summary>ช่องของ <c>PayWidget</c> โหมด publicBase — ยอดไม่รับจากหน้าเว็บ · ติ๊กยอมรับนโยบายต้องมีสำหรับรายการนี้</summary>
    /// รอบ 235 (ข้อ 10): + ช่องบันทึกบัตร (ชื่อเดียวกับ platform-pay — PayWidget ส่งเมื่อหน้าส่ง <c>savedCardOffer</c>) ⇒ <see cref="PlatformSavedCardStart"/> ตัวเดียว
    public sealed record StartRequest(PaymentMethodKind Method = PaymentMethodKind.PromptPay, string? ReturnUrl = null, string? CardToken = null,
        bool PolicyAccepted = false, string? PolicyVersionShown = null,
        bool SaveCard = false, bool SaveCardConsent = false, string? SaveCardConsentVersionShown = null, bool SaveCardWhtAcknowledged = false);

    /// <summary>รอบ 235 (ข้อ 10 · ฝ่ายค้าน N-6): ข้อเสนอ "บันทึกบัตรเพื่อตัดอัตโนมัติ" ของหน้าซื้อสาธารณะ — ด่านสิทธิ์<b>เดียวกับ StartIntent</b> (เจ้าของบริษัท)
    /// ข้อความ/ฉบับ/คำเตือนภาษีหักจากตัวเดียวกับ platform-pay (<c>IPlatformSavedCardService.OfferAsync</c>) · ผู้ซื้อที่เพิ่งสมัคร = เจ้าของบริษัทใหม่ ⇒ ได้ข้อเสนอเหมือนกัน</summary>
    [HttpGet("companies/{companyId:guid}/saved-card-offer")]
    [Authorize]
    [Accounting.Filters.RejectApiKey("ดูข้อเสนอบันทึกบัตร")]
    public async Task<ActionResult<ApiResponse<object>>> SavedCardOffer(Guid companyId, CancellationToken ct)
    {
        if (await OwnerDenyAsync(companyId, "ชำระค่าแพ็กเกจ") is { } deny) return deny;
        return Ok(new ApiResponse<object>(true, await _savedCards.OfferAsync(companyId, ct)));
    }

    [HttpPost("companies/{companyId:guid}/orders/{paymentId:guid}/intents")]
    [Authorize]
    [Accounting.Filters.RejectApiKey("ชำระค่าแพ็กเกจผ่านหน้าสาธารณะ")]
    public async Task<ActionResult<ApiResponse<object>>> StartIntent(Guid companyId, Guid paymentId, [FromBody] StartRequest req, CancellationToken ct)
    {
        if (await OwnerDenyAsync(companyId, "ชำระค่าแพ็กเกจ") is { } deny) return deny;
        if (!await RateOkAsync("intent", ct)) return TooMany();
        try
        {
            // ฝ่ายค้าน S5: pay-widget ส่ง PolicyAccepted=true ทุกคลิก — ผู้ใช้คนนี้ยอมรับฉบับนี้ของรายการนี้ไว้แล้ว ⇒ ไม่เพิ่มแถวซ้ำ (ยังไม่เคย = บังคับตามเดิม)
            var alreadyAccepted = await PurchasePolicyAcceptance.UserAlreadyAcceptedAsync(_db, _legal, companyId, User,
                PurchasePolicyAcceptance.ChannelPlatformSubscription, paymentId, ct);
            await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(_db, _legal, companyId, User, PurchasePolicyAcceptance.ChannelPlatformSubscription,
                paymentId, req.PolicyAccepted && !alreadyAccepted, req.PolicyVersionShown, Ip(), Request.Headers.UserAgent.ToString(), ct);
            // รอบ 235 (ข้อ 10): บันทึกบัตร (ถ้าติ๊ก) — เส้นเดียวกับ platform-pay (ยินยอม · ทิ้งบัตรเมื่อเริ่มไม่สำเร็จ · ผูกรายการแรก · SaveChanges หลักฐานการยอมรับ)
            var intent = await PlatformSavedCardStart.StartAsync(_db, _savedCards, companyId,
                new PlatformSavedCardStart.Request(req.Method, req.CardToken, req.SaveCard, req.SaveCardConsent, req.SaveCardConsentVersionShown, req.SaveCardWhtAcknowledged),
                new PlatformCardActor(JwtHelper.GetUserIdFromClaims(User), User.FindFirstValue(System.Security.Claims.ClaimTypes.Email), Ip(), Request.Headers.UserAgent.ToString()),
                saved => _checkout.StartIntentAsync(companyId, paymentId, req.Method, req.ReturnUrl, req.CardToken, ct, saved, JwtHelper.GetUserIdFromClaims(User)), ct);
            return Ok(new ApiResponse<object>(true, Map(intent)));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("companies/{companyId:guid}/orders/{paymentId:guid}/intents/{intentId:guid}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> IntentStatus(Guid companyId, Guid paymentId, Guid intentId, CancellationToken ct)
    {
        if (await OwnerDenyAsync(companyId, "ดูสถานะการชำระ") is { } deny) return deny;
        var intent = await _checkout.RefreshIntentAsync(companyId, intentId, ct);
        return intent == null || intent.SourceId != paymentId
            ? NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการชำระเงิน"))
            : Ok(new ApiResponse<object>(true, Map(intent)));
    }

    /// <summary>หน้าสำเร็จ: สถานะรายการ + ผลลัพธ์ (เปิดสิทธิ์แล้ว / รอตรวจ / ทดสอบ / ยังไม่จ่าย) + ใบเสร็จ — poll ได้</summary>
    [HttpGet("companies/{companyId:guid}/orders/{paymentId:guid}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<PublicCheckoutOrderStatus>>> OrderStatus(Guid companyId, Guid paymentId, [FromQuery] Guid? intentId, CancellationToken ct)
    {
        if (await OwnerDenyAsync(companyId, "ดูสถานะคำสั่งซื้อ") is { } deny) return deny;
        var s = await _checkout.OrderStatusAsync(companyId, paymentId, intentId, ct);
        return s == null ? NotFound(new ApiResponse<PublicCheckoutOrderStatus>(false, null, "ไม่พบคำสั่งซื้อของบริษัทนี้")) : Ok(new ApiResponse<PublicCheckoutOrderStatus>(true, s));
    }

    /// <summary>ใบเสร็จ/ใบกำกับค่าบริการ (PDF) ของรายการนี้ — ออกโดย <c>PlatformBillingDocumentIssuer</c> เมื่อเงินเข้า (เส้นเดิม)</summary>
    [HttpGet("companies/{companyId:guid}/orders/{paymentId:guid}/receipt")]
    [Authorize]
    public async Task<IActionResult> Receipt(Guid companyId, Guid paymentId, CancellationToken ct)
    {
        if (await OwnerDenyAsync(companyId, "ดาวน์โหลดใบเสร็จ") is { } deny) return deny;
        var s = await _checkout.OrderStatusAsync(companyId, paymentId, null, ct);
        if (s == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบคำสั่งซื้อของบริษัทนี้"));
        var pdf = await _billing.GetReceiptPdfAsync(paymentId);
        if (pdf == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่มีใบเสร็จสำหรับรายการนี้ (ออกอัตโนมัติเมื่อระบบยืนยันการรับเงิน)"));
        return File(pdf.Value.Bytes, "application/pdf", pdf.Value.FileName);
    }

    private static object Map(PaymentIntent i) => new
    {
        id = i.Id, status = i.Status.ToString(), amount = i.Amount, currency = i.Currency,
        qrPayload = i.QrPayload, qrExpiresAt = i.QrExpiresAt, authorizeUrl = i.AuthorizeUrl, failureMessage = i.FailureMessage,
        // โหมดจากค่าที่ประทับบน intent (ตัวเดียว PaymentIntentTestMode) — ป้าย/หมายเหตุจากเซิร์ฟเวอร์ (TestModePaymentPolicy)
        isTestMode = PaymentIntentTestMode.Decide(i.IsTestMode, null),
        statusLabel = TestModePaymentPolicy.StatusLabel(i.Status, PaymentIntentTestMode.Decide(i.IsTestMode, null)),
        testNote = TestModePaymentPolicy.PayerNote(i.Status, PaymentIntentTestMode.Decide(i.IsTestMode, null), i.SourceKind),
    };
}

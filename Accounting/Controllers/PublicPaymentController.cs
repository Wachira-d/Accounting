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
/// **จ่ายเงินออนไลน์สำหรับ "ลูกค้าปลายทาง" ที่ไม่ได้ล็อกอิน** — แขกที่พัก · ผู้ซื้อหน้าร้าน
///
/// ═══ ที่มา (ผลตรวจ LDG-P0-01) ═══
/// <para><c>PaymentGatewayController</c> เป็น <c>[Authorize]</c> ทั้งคลาส และไม่มี
/// ทางเข้าอื่นที่สร้าง <c>PaymentIntent</c> ได้ ⇒ ก่อนหน้านี้<b>ไม่มีลูกค้าปลายทาง
/// รายไหนจ่ายออนไลน์ได้เลยทั้งระบบ</b> · handler ปลายทางสองตัวที่เขียนไว้
/// (<c>LodgingReservationPaymentHandler</c> / <c>SiteOrderPaymentHandler</c>)
/// จึงไม่มีวันถูกเรียก</para>
///
/// <para><b>ทำไมปลอดภัยแม้ไม่ล็อกอิน</b> — ทุก endpoint ต้องมาพร้อม
/// "ของที่ผู้เรียกถืออยู่แล้ว" (<c>PublicToken</c> ของการจอง / <c>orderId</c> GUID
/// ที่คืนตอนสั่งซื้อ) และ <b>ทุกอย่างที่มีผลต่อเงิน resolve จากฐานข้อมูล</b>:
/// <list type="bullet">
///   <item><b>ไม่รับ <c>SourceId</c></b> — ป้องกัน IDOR (สร้าง intent ให้การจองคนอื่น)</item>
///   <item><b>ไม่รับ <c>Amount</c></b> — <c>IPublicPaymentResolver</c> คำนวณจากแถวจริง
///     ผ่าน <c>LodgingAmounts</c> ⇒ จ่าย 1 บาทแล้วได้ห้องไม่ได้</item>
///   <item><b>ไม่คืน intent ที่ไม่ใช่ของ token นั้น</b> — ตรวจ
///     <c>SourceKind + SourceId</c> ซ้ำทุกครั้งที่อ่านสถานะ</item>
/// </list></para>
///
/// <para>ไม่มีชื่อผู้ให้บริการในไฟล์นี้ (บังคับด้วย
/// <c>tools/payment_provider_boundary_check.py</c>) และ<b>ไม่คืน secret key</b>
/// — คืนเฉพาะ QR payload / ลิงก์ authorize ที่ตั้งใจให้เบราว์เซอร์เห็น</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/public-pay")]
[AllowAnonymous]
public class PublicPaymentController : ControllerBase
{
    private readonly IPaymentIntentService _intents;
    private readonly IPublicPaymentResolver _resolver;
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly AccountingDbContext _db;
    private readonly ILogger<PublicPaymentController> _logger;
    private readonly Accounting.Services.Interfaces.ILodgingService _lodging;

    /// <summary>รอบ 241: ลิงก์ชำระเงินของเอกสาร (null = สร้างนอก DI — เส้นเอกสารตอบ 404)</summary>
    private readonly Accounting.Services.Interfaces.IDocumentPayLinkService? _docLinks;

    public PublicPaymentController(IPaymentIntentService intents, IPublicPaymentResolver resolver,
        IEnumerable<IPaymentProvider> providers,
        AccountingDbContext db, ILogger<PublicPaymentController> logger,
        Accounting.Services.Interfaces.ILodgingService lodging,
        Accounting.Services.Interfaces.IDocumentPayLinkService? docLinks = null)
    { _intents = intents; _resolver = resolver; _providers = providers; _db = db; _logger = logger; _lodging = lodging; _docLinks = docLinks; }

    /// <summary>เพดานการสร้าง intent ต่อ source ต่อชั่วโมง — กันคนกดรัวจนตาราง
    /// intent บวมและกัน provider โดนยิงแทนเรา (StartAsync ใช้ intent เดิมซ้ำอยู่แล้ว
    /// เมื่อยอด+วิธีเท่าเดิม ⇒ การนับนี้จะชนเฉพาะกรณีที่ตั้งใจสร้างใบใหม่จริง ๆ)</summary>
    private const int MaxAttemptsPerHour = 20;

    /// <summary>เว้นระยะขั้นต่ำก่อนถามสถานะสดจาก provider อีกครั้ง
    ///
    /// <para>สั้นพอที่ผู้ใช้ไม่รู้สึกช้า (หน้าจ่ายเงิน poll ถี่สุด 2 วิ) แต่ยาวพอ
    /// ที่การเปิดหลายแท็บ/หลายเครื่องพร้อมกันจะไม่กลายเป็นการยิง provider ทวีคูณ ·
    /// webhook ยังเป็นเส้นหลักอยู่แล้ว — การ poll เป็นตาข่ายรับเมื่อ webhook หาย</para></summary>
    private static readonly TimeSpan MinLiveRefreshInterval = TimeSpan.FromSeconds(5);

    public sealed record StartRequest(PaymentMethodKind Method, string? ReturnUrl = null, string? CardToken = null);

    /// <summary>ข้อมูลที่หน้าจ่ายเงินต้องใช้ — ไม่มี secret · ไม่มีชื่อเจ้า</summary>
    public sealed record PublicIntentResponse(
        Guid Id, string Status, string Method, decimal Amount, string Currency,
        string? QrPayload, DateTime? QrExpiresAt, string? AuthorizeUrl,
        string? FailureMessage, bool IsTestMode,
        // รอบ 232 S3: ป้าย/หมายเหตุโหมดทดสอบจากเซิร์ฟเวอร์ (TestModePaymentPolicy ตัวเดียว) — หน้าจ่ายไม่มีข้อความเอง
        string StatusLabel, string? TestNote);

    private static PublicIntentResponse Map(PaymentIntent i, bool testMode) => new(
        i.Id, i.Status.ToString(), i.MethodKind.ToString(), i.Amount, i.Currency,
        i.QrPayload, i.QrExpiresAt, i.AuthorizeUrl, i.FailureMessage, testMode,
        TestModePaymentPolicy.StatusLabel(i.Status, testMode), TestModePaymentPolicy.PayerNote(i.Status, testMode, i.SourceKind));

    /// <summary>ฝ่ายค้านรอบ 219 รอบ 2: ตัวตัดสินเดียวกับด่านให้สิทธิ์ (โหมด ณ ตอนสร้างชนะ · แถวเดิมอ่าน config)</summary>
    private Task<bool> IsTestModeAsync(PaymentIntent i, CancellationToken ct)
        => PaymentIntentTestMode.ResolveAsync(_db, i, ct);

    /// <summary>ช่องทางที่ **เปิดใช้จริง** ของบริษัทนี้ — หน้าเว็บต้องถามที่นี่
    /// ห้ามเดาเอง (ที่พักที่ยังไม่เปิด gateway ต้องเห็นเฉพาะเส้นแนบสลิป)</summary>
    [HttpGet("methods")]
    public async Task<ActionResult<ApiResponse<object>>> Methods(Guid companyId, CancellationToken ct)
    {
        var cfg = await _db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (cfg == null)
            return Ok(new ApiResponse<object>(true, new
            {
                online = false,
                methods = Array.Empty<string>(),
                note = "ที่นี่ยังไม่เปิดรับชำระออนไลน์ — กรุณาโอนแล้วแนบสลิป",
            }));

        // ถามความสามารถจาก adapter เอง ไม่ใช่ hardcode รายการวิธีจ่ายไว้ที่นี่หรือในหน้าเว็บ
        // (defect class "สำเนามือฝั่ง JS" — คอมเมนต์บน PaymentCapabilities ห้ามไว้ตรง ๆ)
        var adapter = _providers.FirstOrDefault(p => p.ProviderCode == cfg.ProviderCode);
        // รอบ 228 (PLATFORM_OMISE_AUDIT §5.1): สิ่งที่หน้าชำระใช้รับบัตร (สคริปต์ + คีย์สาธารณะของโหมดนี้ — ไม่ใช่ความลับ) ·
        // รับบัตรไม่ได้ (ไม่มีคีย์สาธารณะ) ⇒ ไม่แสดงปุ่มบัตร (เดิมขึ้นแล้วเซิร์ฟเวอร์โยน "ไม่พบโทเคนบัตร" ทุกครั้ง)
        var card = PaymentClientSetup.For(adapter, cfg);
        var methods = PaymentClientSetup.WithoutUnusableCard((adapter?.Capabilities.Methods ?? new HashSet<PaymentMethodKind>())
            .Where(m => m != PaymentMethodKind.ManualSlip)
            .Select(m => m.ToString()), card);

        return Ok(new ApiResponse<object>(true, new
        {
            online = methods.Count > 0,
            methods,
            displayName = cfg.DisplayName,
            isTestMode = cfg.Mode == PaymentProviderMode.Test,
            card = card == null ? null : new { providerCode = card.ProviderCode, scriptUrl = card.ScriptUrl, publicKey = card.PublicKey, isTestMode = card.IsTestMode },
        }));
    }

    // ═══════════════ ที่พัก — พิสูจน์สิทธิ์ด้วย PublicToken ═══════════════

    [HttpPost("lodging/{siteId:guid}/{token}/intents")]
    public Task<ActionResult<ApiResponse<PublicIntentResponse>>> StartLodging(
        Guid companyId, Guid siteId, string token, [FromBody] StartRequest req, CancellationToken ct)
        => StartAsync(companyId, () => _resolver.ResolveLodgingAsync(companyId, siteId, token, ct), req, ct,
            // รอบ 202 (O-P1-4): ถือห้องระหว่างจ่าย — ก่อนสร้างรายการ (hold หมด + ห้องถูกจองไป ⇒ ปฏิเสธก่อนแขกจ่าย) และหลังสร้าง (ถึงวันหมดอายุของ QR/ลิงก์)
            holdForPayment: (target, expiresAt) => _lodging.HoldForOnlinePaymentAsync(companyId, target.SourceId, expiresAt));

    [HttpGet("lodging/{siteId:guid}/{token}/intents/{intentId:guid}")]
    public Task<ActionResult<ApiResponse<PublicIntentResponse>>> StatusLodging(
        Guid companyId, Guid siteId, string token, Guid intentId, CancellationToken ct)
        => StatusAsync(companyId, intentId, () => _resolver.ResolveLodgingAsync(companyId, siteId, token, ct), ct);

    // ═══════════════ คำสั่งซื้อหน้าเว็บ — พิสูจน์สิทธิ์ด้วย orderId ═══════════════

    [HttpPost("orders/{siteId:guid}/{orderId:guid}/intents")]
    public Task<ActionResult<ApiResponse<PublicIntentResponse>>> StartOrder(
        Guid companyId, Guid siteId, Guid orderId, [FromBody] StartRequest req, CancellationToken ct)
        => StartAsync(companyId, () => _resolver.ResolveOrderAsync(companyId, siteId, orderId, ct), req, ct);

    [HttpGet("orders/{siteId:guid}/{orderId:guid}/intents/{intentId:guid}")]
    public Task<ActionResult<ApiResponse<PublicIntentResponse>>> StatusOrder(
        Guid companyId, Guid siteId, Guid orderId, Guid intentId, CancellationToken ct)
        => StatusAsync(companyId, intentId, () => _resolver.ResolveOrderAsync(companyId, siteId, orderId, ct), ct);

    // ═══════════════ การจองนัดหมาย (CMS booking) — พิสูจน์สิทธิ์ด้วย PublicToken ของการจอง (รอบ 205 W2 R-08) ═══════════════

    [HttpPost("bookings/{siteId:guid}/{token}/intents")]
    public Task<ActionResult<ApiResponse<PublicIntentResponse>>> StartBooking(
        Guid companyId, Guid siteId, string token, [FromBody] StartRequest req, CancellationToken ct)
        => StartAsync(companyId, () => _resolver.ResolveBookingAsync(companyId, siteId, token, ct), req, ct);

    [HttpGet("bookings/{siteId:guid}/{token}/intents/{intentId:guid}")]
    public Task<ActionResult<ApiResponse<PublicIntentResponse>>> StatusBooking(
        Guid companyId, Guid siteId, string token, Guid intentId, CancellationToken ct)
        => StatusAsync(companyId, intentId, () => _resolver.ResolveBookingAsync(companyId, siteId, token, ct), ct);

    // ═══════════════ ลิงก์ชำระเงินของเอกสาร (ใบแจ้งหนี้/ใบกำกับ) — พิสูจน์สิทธิ์ด้วย PayLinkToken (รอบ 241) ═══════════════
    // ยอด = ยอดค้าง − ภาษีหัก ณ ที่จ่ายที่ลูกค้าจะหัก · ช่องทาง = ที่ใบนี้อนุญาต ∩ ที่บริษัทเปิดจริง · ใบยกเลิก/ชำระครบ/ลิงก์ถูกเพิกถอน ตัดสินสดทุกครั้ง
    // (ตัวตัดสินเดียว DocumentPayLinkPolicy ผ่าน IDocumentPayLinkService.PublicViewAsync)

    private async Task<PublicPayTarget?> ResolveDocumentAsync(Guid companyId, string token, CancellationToken ct)
        => _docLinks == null ? null : (await _docLinks.PublicViewAsync(companyId, token, ct))?.Target;

    [HttpGet("documents/{token}")]
    public async Task<ActionResult<ApiResponse<Accounting.Services.Interfaces.PublicDocumentPayView>>> ViewDocument(
        Guid companyId, string token, CancellationToken ct)
    {
        var view = _docLinks == null ? null : await _docLinks.PublicViewAsync(companyId, token, ct);
        if (view == null)
            return NotFound(new ApiResponse<Accounting.Services.Interfaces.PublicDocumentPayView>(false, null, "ลิงก์ไม่ถูกต้อง — กรุณาติดต่อผู้ขาย"));
        return Ok(new ApiResponse<Accounting.Services.Interfaces.PublicDocumentPayView>(true, view));
    }

    [HttpPost("documents/{token}/intents")]
    public Task<ActionResult<ApiResponse<PublicIntentResponse>>> StartDocument(
        Guid companyId, string token, [FromBody] StartRequest req, CancellationToken ct)
        => StartAsync(companyId, () => ResolveDocumentAsync(companyId, token, ct), req, ct);

    [HttpGet("documents/{token}/intents/{intentId:guid}")]
    public Task<ActionResult<ApiResponse<PublicIntentResponse>>> StatusDocument(
        Guid companyId, string token, Guid intentId, CancellationToken ct)
        => StatusAsync(companyId, intentId, () => ResolveDocumentAsync(companyId, token, ct), ct);

    // ═══════════════ แกนกลาง — ทางเข้าทุกเส้นเดินผ่านสองเมธอดนี้ ═══════════════

    /// <param name="holdForPayment">ต้นทางที่ต้อง "กันของไว้ระหว่างจ่าย" (การจองที่พัก) — เรียกก่อนสร้างรายการ (วันหมดอายุ null) และหลังสร้าง
    /// (วันหมดอายุของรายการ) · โยน <see cref="BusinessRuleException"/> = ปฏิเสธพร้อมข้อความ (ก่อนแขกจ่าย)</param>
    private async Task<ActionResult<ApiResponse<PublicIntentResponse>>> StartAsync(
        Guid companyId, Func<Task<PublicPayTarget?>> resolve, StartRequest req, CancellationToken ct,
        Func<PublicPayTarget, DateTime?, Task>? holdForPayment = null)
    {
        if (req.Method == PaymentMethodKind.ManualSlip)
            return BadRequest(new ApiResponse<PublicIntentResponse>(false, null,
                "การแนบสลิปไม่ต้องสร้างรายการชำระเงินออนไลน์"));

        var target = await resolve();
        if (target == null)
            return NotFound(new ApiResponse<PublicIntentResponse>(false, null, "ไม่พบรายการ"));
        if (!target.CanPayNow)
            return BadRequest(new ApiResponse<PublicIntentResponse>(false, null,
                target.BlockedNote ?? "รายการนี้ไม่มียอดค้างชำระแล้ว"));   // รอบ 205 W2: เหตุผลจริง (สลิปรอตรวจ · ไซต์ไม่เปิด) ไม่ใช่ "ไม่มียอด"
        // รอบ 241: เป้าหมายที่กำหนดช่องทางเอง (ลิงก์ชำระของเอกสาร) — วิธีที่ไม่อยู่ในชุด = ปฏิเสธ (ห้ามรับเงินทางที่ผู้ขายไม่ได้เปิดให้ใบนี้)
        if (target.AllowedMethods != null
            && !target.AllowedMethods.Contains(req.Method.ToString(), StringComparer.OrdinalIgnoreCase))
            return BadRequest(new ApiResponse<PublicIntentResponse>(false, null,
                "ช่องทางชำระเงินนี้ไม่ได้เปิดสำหรับเอกสารนี้ — เลือกช่องทางที่แสดงบนหน้าชำระเงิน"));

        var since = DateTime.UtcNow.AddHours(-1);
        var attempts = await _db.PaymentIntents.AsNoTracking().CountAsync(
            i => i.CompanyId == companyId && i.SourceKind == target.SourceKind
              && i.SourceId == target.SourceId && i.CreatedAt >= since, ct);
        if (attempts >= MaxAttemptsPerHour)
        {
            _logger.LogWarning("เกินเพดานสร้างรายการชำระเงิน — {Kind}/{Id} {N} ครั้งใน 1 ชม.",
                target.SourceKind, target.SourceId, attempts);
            return StatusCode(429, new ApiResponse<PublicIntentResponse>(false, null,
                "ลองสร้างรายการชำระเงินถี่เกินไป — กรุณารอสักครู่แล้วลองใหม่ "
                + "หรือโอนแล้วแนบสลิปแทน"));
        }

        try
        {
            if (holdForPayment != null) await holdForPayment(target, null);
            var intent = await _intents.StartAsync(companyId, new StartPaymentRequest(
                target.SourceKind, target.SourceId, target.AmountDue, req.Method,
                Description: target.Description,
                CustomerEmail: target.CustomerEmail, CustomerPhone: target.CustomerPhone,
                ReturnUrl: req.ReturnUrl, CardToken: req.CardToken,
                SiteId: target.SiteId == Guid.Empty ? null : target.SiteId, ContactId: target.ContactId), ct: ct);
            if (holdForPayment != null) await holdForPayment(target, intent.QrExpiresAt);

            return Ok(new ApiResponse<PublicIntentResponse>(true,
                Map(intent, await IsTestModeAsync(intent, ct))));
        }
        catch (BusinessRuleException ex)
        {
            // ห้องถูกจองไประหว่างที่ hold หมด (LODGING-OVERSOLD) — บอกก่อนแขกจ่าย พร้อมทางไปต่อในข้อความ
            return BadRequest(new ApiResponse<PublicIntentResponse>(false, null, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // ข้อความจาก service เป็นภาษาไทยที่บอกทางแก้อยู่แล้ว — ส่งต่อตรง ๆ
            return BadRequest(new ApiResponse<PublicIntentResponse>(false, null, ex.Message));
        }
    }

    private async Task<ActionResult<ApiResponse<PublicIntentResponse>>> StatusAsync(
        Guid companyId, Guid intentId, Func<Task<PublicPayTarget?>> resolve, CancellationToken ct)
    {
        // ⚠️ ต้องยืนยันก่อนเสมอว่า intent นี้เป็นของ token/orderId ที่ผู้เรียกถือ —
        // ไม่งั้นใครถือ intentId ของคนอื่นก็อ่านยอด/สถานะการจ่ายของคนอื่นได้
        // resolver คืนเป้าหมาย**เสมอ**เมื่อ token ถูก (แม้ยอดค้าง = 0) จึงเทียบตรง ๆ ได้
        var target = await resolve();
        var intent = await _intents.FindAsync(companyId, intentId, ct);

        // ข้อความปฏิเสธต้องเหมือนกันทุกทาง — ห้ามบอกว่า "มี intent นี้อยู่จริงไหม"
        // (กัน enumeration ข้ามผู้ถือ token · กติกาเดียวกับเส้น DSR)
        if (target == null || intent == null
            || intent.SourceKind != target.SourceKind || intent.SourceId != target.SourceId)
            return NotFound(new ApiResponse<PublicIntentResponse>(false, null, "ไม่พบรายการชำระเงิน"));

        // ถามสถานะสดจาก provider ด้วย — webhook หายเป็นเรื่องที่เกิดจริง
        //
        // ⚠️ **แต่ต้องหน่วง**: หน้าจ่ายเงิน poll ทุก 2-10 วิ นานได้ถึง 15 นาที ⇒
        // ถ้า refresh ทุกครั้งจะกลายเป็น "1 request ที่ไม่ต้องล็อกอิน = 1 outbound
        // call ไปหา provider" ⇒ ใครถือ token ที่ถูกต้องใบเดียวก็เปิดแท็บทิ้งไว้
        // หลายสิบแท็บแล้วยิง provider แทนเราได้ฟรี (ทั้งโควตาและค่าบริการเป็นของเรา)
        // หน่วงด้วย `LastPolledAt` ซึ่ง `RefreshAsync` เขียนทุกครั้งที่ถาม provider
        // **แม้สถานะไม่เปลี่ยน** — และเป็นฟิลด์เดียวกับที่ settlement job ใช้เว้นจังหวะ
        // อยู่แล้ว ⇒ มีความจริงชุดเดียว · ไม่ต้องเพิ่ม state ข้าม request
        // (กติกา multi-instance: ห้าม static dict/IMemoryCache โดยไม่มีแผน)
        // ⚠️ ห้ามใช้ UpdatedAt — มันขยับเฉพาะตอนข้อมูล charge เปลี่ยน ⇒ intent ที่
        // ลูกค้ายังไม่จ่าย (สถานะคงเดิม) จะ "เก่าตลอด" แล้วยิง provider ทุกครั้งเหมือนเดิม
        var lastTouched = intent.LastPolledAt ?? intent.CreatedAt;
        var staleEnough = DateTime.UtcNow - lastTouched >= MinLiveRefreshInterval;
        if (PaymentIntentPolicy.IsOpen(intent.Status) && staleEnough)
        {
            try { intent = await _intents.RefreshAsync(companyId, intentId, ct); }
            catch (Exception ex)
            {
                // provider ล่มต้องไม่ทำให้หน้าจ่ายเงินพัง — คืนสถานะล่าสุดที่เรามี
                _logger.LogWarning(ex, "ถามสถานะสดจากผู้ให้บริการไม่สำเร็จ intent {Intent}", intentId);
            }
        }

        return Ok(new ApiResponse<PublicIntentResponse>(true,
            Map(intent, await IsTestModeAsync(intent, ct))));
    }
}

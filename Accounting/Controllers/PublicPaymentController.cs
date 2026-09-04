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

    public PublicPaymentController(IPaymentIntentService intents, IPublicPaymentResolver resolver,
        IEnumerable<IPaymentProvider> providers,
        AccountingDbContext db, ILogger<PublicPaymentController> logger)
    { _intents = intents; _resolver = resolver; _providers = providers; _db = db; _logger = logger; }

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
        string? FailureMessage, bool IsTestMode);

    private static PublicIntentResponse Map(PaymentIntent i, bool testMode) => new(
        i.Id, i.Status.ToString(), i.MethodKind.ToString(), i.Amount, i.Currency,
        i.QrPayload, i.QrExpiresAt, i.AuthorizeUrl, i.FailureMessage, testMode);

    private async Task<bool> IsTestModeAsync(PaymentIntent i, CancellationToken ct)
        => i.ProviderConfigId is Guid cid
           && await _db.PaymentProviderConfigs.AsNoTracking()
               .AnyAsync(c => c.Id == cid && c.Mode == PaymentProviderMode.Test, ct);

    /// <summary>ช่องทางที่ **เปิดใช้จริง** ของบริษัทนี้ — หน้าเว็บต้องถามที่นี่
    /// ห้ามเดาเอง (ที่พักที่ยังไม่เปิด gateway ต้องเห็นเฉพาะเส้นแนบสลิป)</summary>
    [HttpGet("methods")]
    public async Task<ActionResult<ApiResponse<object>>> Methods(Guid companyId, CancellationToken ct)
    {
        var cfg = await _db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
            .Select(c => new { c.DisplayName, c.ProviderCode, c.Mode })
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
        var methods = (adapter?.Capabilities.Methods ?? new HashSet<PaymentMethodKind>())
            .Where(m => m != PaymentMethodKind.ManualSlip)
            .Select(m => m.ToString()).ToList();

        return Ok(new ApiResponse<object>(true, new
        {
            online = methods.Count > 0,
            methods,
            displayName = cfg.DisplayName,
            isTestMode = cfg.Mode == PaymentProviderMode.Test,
        }));
    }

    // ═══════════════ ที่พัก — พิสูจน์สิทธิ์ด้วย PublicToken ═══════════════

    [HttpPost("lodging/{siteId:guid}/{token}/intents")]
    public Task<ActionResult<ApiResponse<PublicIntentResponse>>> StartLodging(
        Guid companyId, Guid siteId, string token, [FromBody] StartRequest req, CancellationToken ct)
        => StartAsync(companyId, () => _resolver.ResolveLodgingAsync(companyId, siteId, token, ct), req, ct);

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

    // ═══════════════ แกนกลาง — ทางเข้าทุกเส้นเดินผ่านสองเมธอดนี้ ═══════════════

    private async Task<ActionResult<ApiResponse<PublicIntentResponse>>> StartAsync(
        Guid companyId, Func<Task<PublicPayTarget?>> resolve, StartRequest req, CancellationToken ct)
    {
        if (req.Method == PaymentMethodKind.ManualSlip)
            return BadRequest(new ApiResponse<PublicIntentResponse>(false, null,
                "การแนบสลิปไม่ต้องสร้างรายการชำระเงินออนไลน์"));

        var target = await resolve();
        if (target == null)
            return NotFound(new ApiResponse<PublicIntentResponse>(false, null, "ไม่พบรายการ"));
        if (!target.CanPayNow)
            return BadRequest(new ApiResponse<PublicIntentResponse>(false, null,
                "รายการนี้ไม่มียอดค้างชำระแล้ว"));

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
            var intent = await _intents.StartAsync(companyId, new StartPaymentRequest(
                target.SourceKind, target.SourceId, target.AmountDue, req.Method,
                Description: target.Description,
                CustomerEmail: target.CustomerEmail, CustomerPhone: target.CustomerPhone,
                ReturnUrl: req.ReturnUrl, CardToken: req.CardToken,
                SiteId: target.SiteId, ContactId: null), ct: ct);

            return Ok(new ApiResponse<PublicIntentResponse>(true,
                Map(intent, await IsTestModeAsync(intent, ct))));
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

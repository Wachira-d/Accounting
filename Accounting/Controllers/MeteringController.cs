using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// ฝั่งลูกค้า: เลือกฟีเจอร์ที่จะใช้ + ดูยอดการใช้งานของตัวเอง
/// (ACCOUNT_STRUCTURE.md §7.1, §8)
///
/// **PDPA**: endpoint ชุดนี้คืนเฉพาะ metadata การใช้งาน (ฟีเจอร์/จำนวน/ยอดเงิน/
/// เอกสารอ้างอิง) — ห้ามคืน prompt หรือเนื้อหาที่ส่งไป AI เด็ดขาด
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/metering")]
[Authorize]
public class MeteringController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IUsageMeteringService _metering;
    private readonly IQuotaService _quota;
    private readonly IPermissionService _permissions;
    private readonly INotifyQuotaService? _notifyQuota;

    public MeteringController(AccountingDbContext db, IUsageMeteringService metering,
        IQuotaService quota, IPermissionService permissions, INotifyQuotaService? notifyQuota = null)
    { _db = db; _metering = metering; _quota = quota; _permissions = permissions; _notifyQuota = notifyQuota; }

    /// <summary>
    /// **ด่านของทุก endpoint ที่ก่อค่าใช้จ่ายให้บริษัท**
    ///
    /// <para>เดิมคอนโทรลเลอร์นี้มีแค่ <c>[Authorize]</c> ระดับคลาส ⇒ สมาชิกคนไหน
    /// ก็เปิด add-on รายเดือน/ซื้อโควตา/ส่งหลักฐานชำระเงินแทนบริษัทได้ —
    /// <b>defect class เดียวกับที่เคยแก้ไปแล้ว</b>ใน <c>DocumentController</c>
    /// และ <c>PayrollController</c> ("[Authorize] ระดับคลาส = ล็อกอินอยู่ไหม
    /// ไม่ใช่ มีสิทธิ์ทำสิ่งนี้ไหม") · คอนโทรลเลอร์นี้ไม่ได้อยู่ใน
    /// <c>tools/write_permission_gate_check.py</c> จึงไม่มีใครจับได้ — เพิ่มเข้าไปแล้ว</para>
    ///
    /// <para>Owner/SystemAdmin ผ่านอัตโนมัติ ⇒ ผู้ที่เปิดบริษัทเองไม่กระทบ</para>
    /// </summary>
    private async Task<ActionResult?> RequireBillingAsync(Guid companyId, string verb)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.BillingManage))
            return null;
        return StatusCode(403, new ApiResponse<object>(false, new
        {
            requiredPermission = PermissionKeys.BillingManage.Replace("perm:", ""),
        }, $"ไม่มีสิทธิ์{verb} — ต้องได้รับสิทธิ์ \"จัดการค่าใช้จ่าย/ส่วนเสริม\" จากเจ้าของบริษัทก่อน"));
    }

    /// <summary>
    /// ฟีเจอร์ที่เปิดขายทั้งหมด + สถานะเปิด/ปิดของบริษัทนี้ + **ราคาที่มีผลตอนนี้**
    ///
    /// ลูกค้าต้องเห็นราคาก่อนกดเปิดเสมอ — การกดเปิดคือการยอมรับค่าใช้จ่าย
    /// </summary>
    [HttpGet("features")]
    public async Task<ActionResult<ApiResponse<object>>> GetFeatures(Guid companyId)
    {
        var now = DateTime.UtcNow;
        var accountId = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.BillingAccountId).FirstOrDefaultAsync();

        var features = await _db.ApiFeatures.AsNoTracking()
            .Where(f => f.IsPublished)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync();

        var enabled = await _db.CompanyFeatures.AsNoTracking()
            .Where(f => f.CompanyId == companyId)
            .ToDictionaryAsync(f => f.FeatureCode, f => f);

        var plans = await _db.ApiPricingPlans.AsNoTracking()
            .Where(p => p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now)
                     && (p.BillingAccountId == null || p.BillingAccountId == accountId))
            .ToListAsync();

        // ฟีเจอร์ที่บริษัทเปิดใช้อยู่แต่ถูก unpublish ไปแล้ว ต้องยังเห็นในรายการ
        // (ใช้ต่อได้ + ปิดเองได้) — ไม่งั้นลูกค้าเจอค่าใช้จ่ายจากของที่มองไม่เห็น
        var extraCodes = enabled.Values.Where(e => e.IsEnabled)
            .Select(e => e.FeatureCode)
            .Except(features.Select(f => f.FeatureCode)).ToList();
        if (extraCodes.Count > 0)
            features.AddRange(await _db.ApiFeatures.AsNoTracking()
                .Where(f => extraCodes.Contains(f.FeatureCode)).ToListAsync());

        return Ok(new ApiResponse<object>(true, features.Select(f =>
        {
            // ราคาเฉพาะกลุ่มชนะราคามาตรฐาน (ต้องตรงกับ UsageMeteringService.ResolvePlanAsync)
            var plan = plans.Where(p => p.FeatureCode == f.FeatureCode)
                .OrderByDescending(p => p.BillingAccountId.HasValue)
                .ThenByDescending(p => p.EffectiveFrom)
                .FirstOrDefault();
            enabled.TryGetValue(f.FeatureCode, out var state);
            return new
            {
                f.FeatureCode, f.Name, f.NameEn, f.Description, f.UnitLabel,
                f.Icon, f.ModuleCode, f.TrialDays,
                kind = f.Kind.ToString(),
                isPublished = f.IsPublished,
                isEnabled = state?.IsEnabled ?? false,
                enabledAt = state?.EnabledAt,
                enabledBy = state?.EnabledBy,
                trialUntil = state?.TrialUntil,
                grantSource = state?.GrantSource.ToString(),
                // ── สถานะการชำระเงิน (LDG-P0-03) — เซิร์ฟเวอร์คำนวณ หน้าเว็บแสดงอย่างเดียว ──
                paymentStatus = (state?.PaymentStatus ?? Models.Enums.AddOnPaymentStatus.NotRequired).ToString(),
                paymentStatusLabel = Accounting.Helpers.AddOnPaymentPolicy.StatusLabel(
                    state?.PaymentStatus ?? Models.Enums.AddOnPaymentStatus.NotRequired),
                paymentRejectedReason = state?.PaymentRejectedReason,
                slipUploadedAt = state?.SlipUploadedAt,
                // รอบ 219 เฟส 0 (G-11): วันสิ้นสุดระยะผ่อนผันของส่วนเสริมที่ยังไม่ชำระ + ยังใช้ได้ไหม — ตัวตัดสินเดียวกับด่านสิทธิ์
                paymentGraceUntil = state?.PaymentStatus == Models.Enums.AddOnPaymentStatus.AwaitingPayment
                    ? Accounting.Helpers.AddOnPaymentPolicy.GraceEndsAt(state.PaymentAwaitingSince) : null,
                usable = state == null || Accounting.Helpers.AddOnPaymentPolicy.UsableWhilePending(
                    state.PaymentStatus, state.PaymentAwaitingSince, DateTime.UtcNow),
                // สูตรเดียวกับ AddOnPurchaseService — เรียก policy ตัวเดียว ห้ามคัดลอกมา
                amountDue = state == null ? 0m
                    : Accounting.Helpers.AddOnPaymentPolicy.AmountDue(state.PaymentStatus, state.AcceptedUnitPrice),
                // รอบ 219 เฟส 2 ขั้น 3: ต้องเลือกวิธีชำระ + ติ๊กยอมรับตอนเปิดไหม (ตัวตัดสินเดียวกับเซิร์ฟเวอร์ — ทดลองใช้ครั้งแรก = ไม่ต้อง)
                requiresPaymentChoice = !(state?.IsEnabled ?? false) && Accounting.Helpers.AddOnPaymentPolicy.RequiresPayment(
                    Models.Enums.AddOnGrantSource.OwnerSelfServe, plan?.Method, plan?.UnitPrice,
                    state?.TrialUntil ?? (f.TrialDays > 0 && state == null ? (DateTime?)now.AddDays(f.TrialDays) : null), now),
                billingChoice = (state?.BillingChoice ?? Models.Enums.AddOnBillingChoice.Upfront).ToString(),
                billingChoiceLabel = state == null ? null : Accounting.Helpers.AddOnPaymentPolicy.ChoiceLabel(state.BillingChoice),
                pricing = plan == null ? null : new
                {
                    method = plan.Method.ToString(),
                    unitPrice = plan.UnitPrice,
                    freeQuotaPerMonth = plan.FreeQuotaPerMonth,
                    tierJson = plan.TierJson,
                },
                // ยังไม่ตั้งราคา = เปิดใช้ได้แต่ยังไม่คิดเงิน — บอกตรง ๆ ไม่ปล่อยให้เดา
                priceNote = plan == null ? "ยังไม่ได้กำหนดราคา — ใช้งานได้โดยยังไม่มีค่าใช้จ่าย" : null,
            };
        })));
    }

    /// <summary>รอบ 219 เฟส 2 ขั้น 3: <paramref name="BillingChoice"/> = "Upfront"/"MonthlyInvoice" — บังคับเมื่อส่วนเสริมต้องจ่าย (เซิร์ฟเวอร์ตัดสิน) ·
    /// ส่งวิธีชำระมา = การซื้อ ⇒ ต้องติ๊กยอมรับนโยบาย (part B <c>PurchasePolicyAcceptance</c> ช่อง platform-addon)</summary>
    public record ToggleFeatureRequest(bool Enabled, Models.Enums.AddOnBillingChoice? BillingChoice = null,
        bool PolicyAccepted = false, string? PolicyVersionShown = null);

    /// <summary>เปิด/ปิดฟีเจอร์ — เป็นสวิตช์ค่าใช้จ่ายจริง จึงบันทึกว่าใครกดตอนไหน</summary>
    [HttpPost("features/{featureCode}")]
    public async Task<ActionResult<ApiResponse<object>>> ToggleFeature(
        Guid companyId, string featureCode, [FromBody] ToggleFeatureRequest req,
        [FromServices] ILegalDocumentService legal, CancellationToken ct)
    {
        var block = await RequireBillingAsync(companyId,
            req.Enabled ? "เปิดใช้ส่วนเสริม" : "ปิดส่วนเสริม");
        if (block != null) return block;

        var feature = await _db.ApiFeatures.AsNoTracking()
            .FirstOrDefaultAsync(f => f.FeatureCode == featureCode);
        if (feature == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบฟีเจอร์นี้"));

        // เปิดใหม่ได้เฉพาะฟีเจอร์ที่ยังเปิดขาย — แต่ "ปิด" ทำได้เสมอ
        // (ลูกค้าต้องถอนตัวจากค่าใช้จ่ายได้ตลอดเวลา)
        if (req.Enabled && !feature.IsPublished)
            return BadRequest(new ApiResponse<string>(false, null,
                "ฟีเจอร์นี้ปิดรับผู้ใช้ใหม่แล้ว — กรุณาติดต่อผู้ดูแลระบบ"));

        var actor = User.Identity?.Name ?? "unknown";
        // รอบ 219 เฟส 2 ขั้น 3 (คำตัดสินเจ้าของข้อ 178 ⑤⑧): เลือกวิธีชำระ = ซื้อ ⇒ ติ๊กยอมรับนโยบายก่อนเปิด (แถวหลักฐานบันทึกพร้อมการเปิด)
        if (req.Enabled && req.BillingChoice != null)
        {
            var sourceId = await _db.CompanyFeatures.AsNoTracking()
                .Where(f => f.CompanyId == companyId && f.FeatureCode == featureCode && !f.IsDeleted)
                .Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);
            await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(_db, legal, companyId, User,
                PurchasePolicyAcceptance.ChannelPlatformAddOn, sourceId, req.PolicyAccepted, req.PolicyVersionShown,
                HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
        }
        await _metering.SetFeatureEnabledAsync(companyId, featureCode, req.Enabled, actor, req.BillingChoice, ct);

        return Ok(new ApiResponse<object>(true, new { featureCode, enabled = req.Enabled },
            req.Enabled ? $"เปิดใช้ {feature.Name} แล้ว" : $"ปิด {feature.Name} แล้ว — หยุดคิดค่าใช้จ่ายทันที"));
    }

    /// <summary>
    /// สิทธิ์ของผู้ใช้คนนี้บนหน้า "ส่วนเสริมของฉัน" — **เซิร์ฟเวอร์ตัดสิน หน้าเว็บแสดงอย่างเดียว**
    ///
    /// <para>หน้าเว็บต้องรู้ล่วงหน้าว่าจะกดได้ไหม เพื่อ<b>ไม่โชว์ปุ่มที่กดแล้ว 403</b>
    /// (กติกา "ปฏิเสธแล้วต้องมีทางไปต่อ" — บอกให้ชัดว่าต้องขอสิทธิ์อะไรจากใคร)
    /// · ห้ามให้ JS เดาจาก role เอง ไม่งั้นได้กติกาสิทธิ์สำเนาที่สองที่ drift</para>
    /// </summary>
    [HttpGet("my-access")]
    public async Task<ActionResult<ApiResponse<object>>> MyAccess(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var canBilling = await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.BillingManage);
        return Ok(new ApiResponse<object>(true, new
        {
            canManageBilling = canBilling,
            requiredPermission = PermissionKeys.BillingManage.Replace("perm:", ""),
            note = canBilling ? null
                : "การเปิดส่วนเสริมและซื้อโควตาเป็นการก่อค่าใช้จ่ายให้บริษัท "
                  + "— ขอสิทธิ์ “จัดการค่าใช้จ่าย/ส่วนเสริม” จากเจ้าของบริษัทก่อน",
        }));
    }

    // ═══════════════ ชำระค่าส่วนเสริม (LDG-P0-03) ═══════════════
    //  เดิม: กดเปิด = ใช้ได้ฟรีทันที ไม่มีทั้งช่องจ่ายและช่องแนบสลิป
    //  ตอนนี้: มี gateway → จ่ายแล้วเปิดทันที · ไม่มี → แนบสลิปแล้วรอแอดมินตรวจ

    /// <summary>สถานะการชำระเงินของส่วนเสริมตัวหนึ่ง — หน้าเว็บ**แสดงอย่างเดียว**
    /// (ข้อความสถานะมาจาก <c>AddOnPaymentPolicy.StatusLabel</c> ห้าม JS แต่งเอง)</summary>
    [HttpGet("features/{featureCode}/payment")]
    public async Task<ActionResult<ApiResponse<object>>> GetAddOnPayment(
        Guid companyId, string featureCode,
        [FromServices] Accounting.Services.Payments.IAddOnPurchaseService addons,
        CancellationToken ct)
    {
        var st = await addons.GetAsync(companyId, featureCode, ct);
        if (st == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้เปิดใช้ส่วนเสริมนี้"));
        return Ok(new ApiResponse<object>(true, st));
    }

    /// <summary>สร้างรายการชำระเงินออนไลน์ของส่วนเสริม — ยอดมาจากเซิร์ฟเวอร์เท่านั้น ·
    /// รอบ 219 ส่วน B: + ช่องติ๊กยอมรับข้อกำหนด/นโยบายคืนเงิน (<c>PurchasePolicyAcceptance</c>)</summary>
    public record StartAddOnPaymentRequest(Models.Enums.PaymentMethodKind Method, string? ReturnUrl = null,
        bool PolicyAccepted = false, string? PolicyVersionShown = null, string? CardToken = null);

    /// <summary>รอบ 219 ส่วน B (คำตัดสินข้อ 179 · ต่อจากเฟส 0 ข้อ 178): ชำระออนไลน์ผ่าน<b>ช่องทางของแพลตฟอร์ม</b> (<c>IPlatformPaymentService</c> — intent
    /// ใต้บริษัทผู้ให้บริการ + บริษัทผู้จ่าย) เฉพาะเมื่อแพลตฟอร์มเปิดรับเงินจริงแล้ว · ยังไม่เปิด ⇒ 409 + วิธีโอนของ NextAcc (ทางไปต่อ = แนบสลิป) ·
    /// ไม่ใช้ gateway ของร้านเด็ดขาด · เส้นหลักของหน้าเว็บคือ <c>platform-pay/addons/{code}/intents</c> (PayWidget) — ตัวนี้คงไว้ให้ผู้เรียกเดิม</summary>
    [HttpPost("features/{featureCode}/payment/intent")]
    public async Task<ActionResult<ApiResponse<object>>> StartAddOnPayment(
        Guid companyId, string featureCode, [FromBody] StartAddOnPaymentRequest req,
        [FromServices] Accounting.Services.Payments.IPlatformPaymentService platform,
        [FromServices] ILegalDocumentService legal, CancellationToken ct)
    {
        var block = await RequireBillingAsync(companyId, "ชำระค่าส่วนเสริม");
        if (block != null) return block;
        var status = await platform.StatusAsync(ct);
        if (!status.Online)
            return StatusCode(409, new ApiResponse<object>(false, await PlatformPayInfo.BuildAsync(_db, platform, legal, ct),
                status.OfflineReason ?? "ยังไม่เปิดชำระค่าบริการ NextAcc ออนไลน์ — กรุณาโอนเงินตามรายละเอียดแล้วแนบสลิป ผู้ดูแลระบบจะตรวจสอบและยืนยันให้"));
        var sourceId = await platform.AddOnSourceIdAsync(companyId, featureCode, ct);
        if (sourceId == null) return NotFound(new ApiResponse<object>(false, null, "ยังไม่ได้เปิดใช้ส่วนเสริมนี้"));
        try
        {
            await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(_db, legal, companyId, User,
                PurchasePolicyAcceptance.ChannelPlatformAddOn, sourceId, req.PolicyAccepted, req.PolicyVersionShown,
                HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
            var intent = await platform.StartAddOnAsync(companyId, featureCode, req.Method, req.ReturnUrl, req.CardToken, ct);
            await _db.SaveChangesAsync(ct);
            return Ok(new ApiResponse<object>(true, new
            {
                id = intent.Id, status = intent.Status.ToString(), amount = intent.Amount,
                currency = intent.Currency, qrPayload = intent.QrPayload,
                qrExpiresAt = intent.QrExpiresAt, authorizeUrl = intent.AuthorizeUrl,
                failureMessage = intent.FailureMessage, isTestMode = PaymentIntentTestMode.Decide(intent.IsTestMode, null),
            }));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    /// <summary>รอบ 219 เฟส 0 (G-1/G-13) + ส่วน B: วิธีชำระค่าบริการ NextAcc ที่หน้า "ส่วนเสริม"/"แพ็กเกจ" แสดง — <b>ไม่ถาม gateway ของร้าน</b> ·
    /// <c>online</c> = ช่องทางของแพลตฟอร์มเปิดรับเงินจริงแล้ว · <c>policy</c> = ข้อความ/ฉบับ/ลิงก์ของช่องติ๊กยอมรับนโยบาย (ตัวเดียว <c>PlatformPayInfo</c>)</summary>
    [HttpGet("platform-pay")]
    public async Task<ActionResult<ApiResponse<object>>> PlatformPay(Guid companyId,
        [FromServices] Accounting.Services.Payments.IPlatformPaymentService platform,
        [FromServices] ILegalDocumentService legal, CancellationToken ct)
        => Ok(new ApiResponse<object>(true, await PlatformPayInfo.BuildAsync(_db, platform, legal, ct)));

    /// <summary>แนบสลิปค่าส่วนเสริม — เส้นสำรองเมื่อยังไม่เปิด gateway</summary>
    [HttpPost("features/{featureCode}/payment/slip")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<object>>> UploadAddOnSlip(
        Guid companyId, string featureCode, IFormFile? file,
        [FromForm] string? reference, [FromForm] decimal? amount,
        [FromServices] Accounting.Services.Payments.IAddOnPurchaseService addons,
        [FromServices] IImageProcessingService images,
        [FromServices] IWebHostEnvironment env,
        [FromServices] Accounting.Services.Payments.IPlatformPaymentService platform,
        [FromServices] ILegalDocumentService legal,
        CancellationToken ct,
        [FromForm] bool policyAccepted = false, [FromForm] string? policyVersionShown = null)
    {
        var block = await RequireBillingAsync(companyId, "ส่งหลักฐานการชำระเงิน");
        if (block != null) return block;
        // รอบ 219 ส่วน B (คำตัดสินข้อ 179): ส่งสลิป = ชำระค่าบริการ ⇒ ติ๊กยอมรับข้อกำหนด + นโยบายคืนเงินก่อน (ยอมรับไว้แล้วสำหรับรายการนี้ = ผ่าน)
        await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(_db, legal, companyId, User,
            PurchasePolicyAcceptance.ChannelPlatformAddOn, await platform.AddOnSourceIdAsync(companyId, featureCode, ct),
            policyAccepted, policyVersionShown, HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);

        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "กรุณาเลือกไฟล์สลิป"));
        if (!file.ContentType.StartsWith("image/") && file.ContentType != "application/pdf")
            return BadRequest(new ApiResponse<object>(false, null, "รองรับเฉพาะรูปภาพหรือ PDF"));

        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var dir = Path.Combine(webRoot, "uploads", "slips");
        await using var s = file.OpenReadStream();
        var saved = await images.ProcessAndSaveAsync(
            s, file.ContentType, file.FileName, dir, "/uploads/slips", ImageProfile.Slip);

        var actor = User.Identity?.Name ?? "unknown";
        var st = await addons.UploadSlipAsync(companyId, featureCode, saved.RelativeUrl, reference, amount, actor, ct);
        return Ok(new ApiResponse<object>(true, st,
            "ส่งหลักฐานการชำระเงินแล้ว — ผู้ดูแลระบบจะตรวจสอบและยืนยันให้"));
    }

    // ═══════════════ โควตาเอกสาร: สถานะ · ซื้อเพิ่ม · แลกจากภารกิจ ═══════════════

    /// <summary>สถานะโควตาเอกสารเดือนนี้ — **เซิร์ฟเวอร์คำนวณ หน้าเว็บแสดงอย่างเดียว**
    ///
    /// ทุกหน้าที่อยากเตือน "ใกล้เต็ม" ต้องอ่านจากที่นี่ ห้ามเทียบเปอร์เซ็นต์เอง
    /// (defect class "สำเนามือฝั่ง JS" — เกิดมาแล้วกับ MENU_SECTIONS/docHeaderLabel)</summary>
    [HttpGet("quota")]
    public async Task<ActionResult<ApiResponse<DocumentQuotaStatus>>> GetQuota(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<DocumentQuotaStatus>(true, await _quota.GetDocumentQuotaAsync(companyId, ct)));

    /// <summary>ฝ่ายค้านรอบ 213 S4: <c>ExpectedPackSize/ExpectedPackPrice</c> = ข้อเสนอที่ผู้ซื้อเห็น (ใช้กับ notify.topup — ไม่ตรงของจริง ⇒ 409) · documents.topup ไม่อ่าน</summary>
    public record TopUpRequest(int Packs = 1, int? ExpectedPackSize = null, decimal? ExpectedPackPrice = null);

    /// <summary>header <c>Idempotency-Key</c> ของคำขอซื้อ (null = ไม่ได้ส่ง · ยาวเกิน 200 ตัดทิ้ง — ใช้แค่ hash)</summary>
    private string? IdempotencyKeyHeader()
    {
        var v = Request?.Headers["Idempotency-Key"].ToString();
        return string.IsNullOrWhiteSpace(v) ? null : (v.Length > 200 ? v[..200] : v);
    }

    /// <summary>ซื้อโควตาเอกสารเพิ่ม — ทางไปต่อทางที่ 1 เมื่อชนเพดาน (§11)</summary>
    [HttpPost("quota/topup")]
    public async Task<ActionResult<ApiResponse<DocumentQuotaStatus>>> TopUp(
        Guid companyId, [FromBody] TopUpRequest req, CancellationToken ct)
    {
        // ซื้อโควตา = จ่ายเงินจริง ⇒ ด่านเดียวกับการเปิด add-on
        var block = await RequireBillingAsync(companyId, "ซื้อโควตาเพิ่ม");
        if (block != null) return block;

        var actor = User.Identity?.Name ?? "unknown";
        // ฝ่ายค้านรอบ 212 S2: header Idempotency-Key — กดซ้ำ/ส่งซ้ำด้วยคีย์เดิม = คำขอเดียว (คิดเงินครั้งเดียว · ได้ผลเดิม)
        var status = await _quota.PurchaseTopUpAsync(companyId, req.Packs, actor, ct, IdempotencyKeyHeader());
        var docs = req.Packs * Models.Constants.AddOnCodes.DocumentsPerTopUpPack;
        return Ok(new ApiResponse<DocumentQuotaStatus>(true, status,
            $"เพิ่มโควตา {docs} ฉบับแล้ว — ใช้ได้ถึง {status.BonusExpiresAt?.AddHours(7):dd/MM/yyyy}"));
    }

    // ═══════════════ รอบ 212 แพ็ก A: โควตาข้อความผ่านช่องกลาง (LINE OA/อีเมลของ NextAcc) ═══════════════

    /// <summary>สถานะโควตาข้อความเดือนนี้ — เซิร์ฟเวอร์คำนวณ (NotifyQuotaPolicy) หน้าเว็บแสดงอย่างเดียว · ใช้ที่หน้า ตั้งค่า/การใช้งาน/แพ็กเกจ/ส่วนเสริม</summary>
    [HttpGet("notify-quota")]
    public async Task<ActionResult<ApiResponse<NotifyQuotaStatus>>> GetNotifyQuota(Guid companyId, CancellationToken ct)
    {
        if (_notifyQuota == null) return StatusCode(503, new ApiResponse<NotifyQuotaStatus>(false, null, "บริการโควตาข้อความไม่พร้อม"));
        return Ok(new ApiResponse<NotifyQuotaStatus>(true, await _notifyQuota.GetStatusAsync(companyId, ct)));
    }

    /// <summary>ซื้อแพ็กข้อความเพิ่ม (<c>notify.topup</c>) — ด่านเดียวกับซื้อโควตาเอกสาร · คิดเงินผ่าน UsageEvent เส้นเดิม</summary>
    [HttpPost("notify-quota/topup")]
    public async Task<ActionResult<ApiResponse<NotifyQuotaStatus>>> NotifyTopUp(
        Guid companyId, [FromBody] TopUpRequest req, CancellationToken ct)
    {
        var block = await RequireBillingAsync(companyId, "ซื้อแพ็กข้อความเพิ่ม");
        if (block != null) return block;
        if (_notifyQuota == null) return StatusCode(503, new ApiResponse<NotifyQuotaStatus>(false, null, "บริการโควตาข้อความไม่พร้อม"));

        var actor = User.Identity?.Name ?? "unknown";
        NotifyQuotaStatus status;
        try
        {
            status = await _notifyQuota.PurchaseTopUpAsync(companyId, req.Packs, actor, ct, IdempotencyKeyHeader(),
                req.ExpectedPackSize, req.ExpectedPackPrice);
        }
        catch (NotifyTopUpOfferChangedException ex)
        {
            // ฝ่ายค้านรอบ 213 S4: ขนาด/ราคาเปลี่ยนระหว่างที่ผู้ซื้อดูอยู่ ⇒ 409 + สถานะใหม่ (ยังไม่คิดเงิน) ให้หน้าเว็บแสดงข้อเสนอใหม่แล้วยืนยันอีกครั้ง
            return StatusCode(409, new ApiResponse<NotifyQuotaStatus>(false, await _notifyQuota.GetStatusAsync(companyId, ct), ex.Message));
        }
        // ฝ่ายค้านรอบ 213 S4: จำนวนที่ให้จริงจากการซื้อครั้งนี้ (ขนาดแพ็ก ณ ตอนซื้อ) — คำขอซ้ำ (คีย์เดิม) ไม่มีค่า ⇒ ไม่อ้างจำนวน
        var granted = status.Granted;
        return Ok(new ApiResponse<NotifyQuotaStatus>(true, status,
            granted is int g
                ? $"เพิ่มข้อความแจ้งเตือน {g:N0} ข้อความแล้ว — ใช้ได้ถึง {status.BonusExpiresAt?.AddHours(7):dd/MM/yyyy}"
                : "คำขอนี้ดำเนินการไปแล้ว — แสดงสถานะล่าสุด"));
    }

    /// <summary>ภารกิจแลกโควตาที่เปิดอยู่ (§12) — ว่างเปล่า = เจ้าของระบบปิดไว้
    /// หรือแพ็กเกจนี้ไม่รองรับ ซึ่งเป็นสถานะปกติ ไม่ใช่ error</summary>
    [HttpGet("quota/rewards")]
    public async Task<ActionResult<ApiResponse<List<QuotaRewardOptionDto>>>> ListRewards(
        Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<List<QuotaRewardOptionDto>>(true, await _quota.ListRewardOptionsAsync(companyId, ct)));

    public record ClaimRewardRequest(int WatchedSeconds, bool ClickedThrough = false);

    /// <summary>รับโควตาหลังทำภารกิจจบ — เวลาที่ส่งมาจากเบราว์เซอร์เชื่อไม่ได้ 100%
    /// จึงมีเพดานต่อวัน/เดือนเป็นด่านจริง (ดู <c>QuotaRewardPolicy</c>)</summary>
    [HttpPost("quota/rewards/{optionId:guid}/claim")]
    public async Task<ActionResult<ApiResponse<QuotaRewardClaimResult>>> ClaimReward(
        Guid companyId, Guid optionId, [FromBody] ClaimRewardRequest req, CancellationToken ct)
    {
        var userId = Helpers.JwtHelper.GetUserIdFromClaims(User);
        var res = await _quota.ClaimRewardAsync(companyId, optionId, req.WatchedSeconds,
            req.ClickedThrough, userId, ct);
        return res.Granted
            ? Ok(new ApiResponse<QuotaRewardClaimResult>(true, res, res.Message))
            : BadRequest(new ApiResponse<QuotaRewardClaimResult>(false, res, res.Message));
    }

    /// <summary>ยอดใช้งานรายเดือนของบริษัทนี้</summary>
    [HttpGet("usage")]
    public async Task<ActionResult<ApiResponse<object>>> GetUsage(
        Guid companyId, [FromQuery] int? year = null, [FromQuery] int? month = null)
    {
        var now = DateTime.UtcNow;
        var rows = await _metering.GetMonthlyUsageAsync(companyId, year ?? now.Year, month ?? now.Month);
        return Ok(new ApiResponse<object>(true, new
        {
            period = $"{year ?? now.Year:0000}-{month ?? now.Month:00}",
            totalCharged = rows.Sum(r => r.TotalCharged),
            items = rows,
        }));
    }

    /// <summary>
    /// ยอดรวมทั้งกลุ่ม — ต้องเป็นผู้ดูแลกลุ่ม (`BillingAccountAdmin`) เท่านั้น
    ///
    /// การเป็นสมาชิกบริษัทใดบริษัทหนึ่งในกลุ่ม **ไม่ให้สิทธิ์เห็นยอดของบริษัทอื่น**
    /// — แยก "บริหารกลุ่ม" ออกจาก "เป็นพนักงานบริษัทหนึ่ง" ตามหลัก §8
    /// </summary>
    [HttpGet("usage/account")]
    public async Task<ActionResult<ApiResponse<object>>> GetAccountUsage(
        Guid companyId, [FromQuery] int? year = null, [FromQuery] int? month = null)
    {
        var accountId = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.BillingAccountId).FirstOrDefaultAsync();
        if (accountId == null)
            return BadRequest(new ApiResponse<string>(false, null, "บริษัทนี้ไม่ได้สังกัดกลุ่มผู้จ่ายเงิน"));

        var userId = Helpers.JwtHelper.GetUserIdFromClaims(User);
        var isAdmin = await _db.BillingAccountAdmins.AsNoTracking()
            .AnyAsync(a => a.BillingAccountId == accountId.Value && a.UserId == userId);
        if (!isAdmin)
            return StatusCode(403, new ApiResponse<string>(false, null,
                "ต้องเป็นผู้ดูแลกลุ่มจึงจะดูยอดรวมทุกบริษัทได้"));

        var now = DateTime.UtcNow;
        var rows = await _metering.GetAccountMonthlyUsageAsync(
            accountId.Value, year ?? now.Year, month ?? now.Month);

        var account = await _db.BillingAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId.Value);

        return Ok(new ApiResponse<object>(true, new
        {
            period = $"{year ?? now.Year:0000}-{month ?? now.Month:00}",
            accountName = account?.Name,
            paymentModel = account?.PaymentModel.ToString(),
            creditBalance = account?.CreditBalance,
            totalCharged = rows.Sum(r => r.TotalCharged),
            byCompany = rows.GroupBy(r => new { r.CompanyId, r.CompanyName })
                .Select(g => new
                {
                    g.Key.CompanyId, g.Key.CompanyName,
                    charged = g.Sum(x => x.TotalCharged),
                    items = g.ToList(),
                })
                .OrderByDescending(x => x.charged),
        }));
    }
}

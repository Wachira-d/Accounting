using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 219 เฟส 0 (PLATFORM_BILLING_AUDIT · คำตัดสินข้อ 178) — <b>รายงานอ่านอย่างเดียว</b> ของรอยรั่วที่เกิดไปแล้วก่อนอุด ·
/// การย้อนสิทธิ์/เก็บเงินย้อนหลัง/ย้ายเงิน เป็นคำตัดสินของเจ้าของ ⇒ endpoint เหล่านี้<b>ไม่แก้ข้อมูลใด ๆ</b> (F2 ข้อ 9: ของที่ persist แล้วต้องมีทางจัดการ
/// แต่ห้ามย้อนอัตโนมัติ):
/// <list type="bullet">
///   <item><c>unpaid-activations</c> (G-0) — แพ็กเกจจ่ายเงินที่ผู้เช่าเปิดเอง (Convert/เปลี่ยนแพ็กเกจรุ่นก่อนเฟส 0) โดยไม่มีการชำระที่อนุมัติหลังจากนั้น</item>
///   <item><c>prepaid-accounts</c> (G-6) — กลุ่มบัญชี Prepaid ที่เครดิตติดลบ / ค่าใช้งานที่ถูกปิดรอบโดยไม่ออกใบ</item>
///   <item><c>tenant-routed-intents</c> (part B (b)) — รายการค่าบริการ NextAcc ที่เกิดใต้บริษัทของร้าน (เงินอาจเข้ากระเป๋าร้าน/เป็นโหมดทดสอบ)</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/admin/platform-billing")]
[Authorize(Roles = "SystemAdmin")]
public class AdminPlatformBillingController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public AdminPlatformBillingController(AccountingDbContext db) => _db = db;

    /// <summary>ชื่อ action ของประวัติที่แปลว่า "ผู้เช่าเปิด/เปลี่ยนแพ็กเกจเองทันที" (เส้นรุ่นก่อนเฟส 0)</summary>
    private static readonly string[] SelfActivationActions = { "TrialConverted", "PlanChanged" };

    [HttpGet("unpaid-activations")]
    public async Task<ActionResult<ApiResponse<object>>> UnpaidActivations(CancellationToken ct)
    {
        var subs = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.Status == SubscriptionStatus.Active && !s.IsPermanentFree && s.PricePerCycle > 0)
            .Select(s => new { s.Id, s.CompanyId, s.Plan, s.BillingCycle, s.PricePerCycle, s.EndDate })
            .ToListAsync(ct);
        var ids = subs.Select(s => s.Id).ToList();
        var selfAt = await _db.SubscriptionHistories.AsNoTracking()
            .Where(h => ids.Contains(h.SubscriptionId) && SelfActivationActions.Contains(h.Action))
            .GroupBy(h => h.SubscriptionId)
            .Select(g => new { Id = g.Key, At = g.Max(h => h.CreatedAt) })
            .ToDictionaryAsync(x => x.Id, x => (DateTime?)x.At, ct);
        var paidAt = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => ids.Contains(p.SubscriptionId) && p.Status == SubscriptionPaymentStatus.Approved)
            .GroupBy(p => p.SubscriptionId)
            .Select(g => new { Id = g.Key, At = g.Max(p => p.ReviewedAt ?? p.CreatedAt) })
            .ToDictionaryAsync(x => x.Id, x => (DateTime?)x.At, ct);
        var rows = subs
            .Where(s => PlatformPlanPricing.IsUnpaidSelfActivation(selfAt.GetValueOrDefault(s.Id), paidAt.GetValueOrDefault(s.Id)))
            .ToList();
        var names = await CompanyNamesAsync(rows.Select(r => r.CompanyId), ct);
        return Ok(new ApiResponse<object>(true, rows.Select(r => new
        {
            r.CompanyId,
            companyName = names.GetValueOrDefault(r.CompanyId, "(ไม่พบชื่อ)"),
            plan = r.Plan.ToString(),
            billingCycle = r.BillingCycle.ToString(),
            r.PricePerCycle,
            activatedAt = selfAt.GetValueOrDefault(r.Id),
            lastApprovedPaymentAt = paidAt.GetValueOrDefault(r.Id),
            r.EndDate,
        }).OrderBy(r => r.activatedAt).ToList()));
    }

    [HttpGet("prepaid-accounts")]
    public async Task<ActionResult<ApiResponse<object>>> PrepaidAccounts(CancellationToken ct)
    {
        var accounts = await _db.BillingAccounts.AsNoTracking()
            .Where(a => a.PaymentModel == PaymentModel.Prepaid)
            .Select(a => new { a.Id, a.Name, a.CreditBalance, a.CreatedBy })
            .ToListAsync(ct);
        var ids = accounts.Select(a => a.Id).ToList();
        // ปิดรอบแบบ prepaid = ตีตรา BilledPeriod แต่ไม่มีใบ (UsageInvoicingJob) — ยอดที่ไม่เคยถูกเรียกเก็บ
        var closed = await _db.UsageEvents.AsNoTracking()
            .Where(u => u.BillingAccountId != null && ids.Contains(u.BillingAccountId.Value)
                     && u.BilledPeriod != null && u.BilledDocumentId == null && u.ChargedAmount > 0 && !u.IsSandbox)
            .GroupBy(u => u.BillingAccountId!.Value)
            .Select(g => new { Id = g.Key, Amount = g.Sum(u => u.ChargedAmount), Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, ct);
        var companies = await _db.Companies.AsNoTracking()
            .Where(c => c.BillingAccountId != null && ids.Contains(c.BillingAccountId.Value))
            .GroupBy(c => c.BillingAccountId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        return Ok(new ApiResponse<object>(true, accounts
            .Select(a =>
            {
                closed.TryGetValue(a.Id, out var c);
                return new
                {
                    a.Id, a.Name, a.CreditBalance,
                    backfilled = a.CreatedBy == "migration:billing-account-backfill",
                    companyCount = companies.GetValueOrDefault(a.Id),
                    unbilledClosedAmount = c?.Amount ?? 0m,
                    unbilledClosedEvents = c?.Count ?? 0,
                };
            })
            .Where(a => a.CreditBalance < 0m || a.unbilledClosedAmount > 0m)
            .OrderByDescending(a => a.unbilledClosedAmount)
            .ToList()));
    }

    [HttpGet("tenant-routed-intents")]
    public async Task<ActionResult<ApiResponse<object>>> TenantRoutedIntents(CancellationToken ct)
    {
        var platformId = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt)
            .Select(s => s.PlatformCompanyId).FirstOrDefaultAsync(ct);
        var kinds = Enum.GetValues<PaymentSourceKind>().Where(PlatformRevenueSource.IsPlatformRevenue).ToList();
        var rows = await _db.PaymentIntents.AsNoTracking()
            .Where(i => kinds.Contains(i.SourceKind) && (platformId == null || i.CompanyId != platformId))
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new
            {
                i.Id, i.CompanyId, kind = i.SourceKind.ToString(), i.SourceId, status = i.Status.ToString(),
                i.Amount, i.ProviderCode, i.ProviderConfigId, i.CreatedAt, i.ConfirmedBy,
            })
            .Take(500)
            .ToListAsync(ct);
        var cfgIds = rows.Where(r => r.ProviderConfigId != null).Select(r => r.ProviderConfigId!.Value).Distinct().ToList();
        var modes = await _db.PaymentProviderConfigs.AsNoTracking().IgnoreQueryFilters()
            .Where(c => cfgIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Mode.ToString(), ct);
        var names = await CompanyNamesAsync(rows.Select(r => r.CompanyId), ct);
        return Ok(new ApiResponse<object>(true, rows.Select(r => new
        {
            r.Id, r.CompanyId,
            companyName = names.GetValueOrDefault(r.CompanyId, "(ไม่พบชื่อ)"),
            r.kind, r.SourceId, r.status, r.Amount, r.ProviderCode,
            mode = r.ProviderConfigId is Guid g ? modes.GetValueOrDefault(g) : null,
            r.CreatedAt, r.ConfirmedBy,
        }).ToList()));
    }

    // ═══════════ รอบ 219 เฟส 2 (คำตัดสินข้อ 180): ค่าตั้งการเรียกเก็บ — SystemAdmin แก้ได้ · ทุกการแก้ลง audit chain ═══════════

    private async Task<SubscriptionEnforcementMode> EffectiveModeAsync(ISubscriptionGateShadowLog gate, IConfiguration config, CancellationToken ct)
        => SubscriptionEnforcementResolver.Resolve(await gate.ReadAdminSwitchAsync(ct), config).EffectiveMode;

    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<PlatformBillingSettingsDto>>> GetSettings(
        [FromServices] IPlatformBillingSettingsService settings, [FromServices] ISubscriptionGateShadowLog gate,
        [FromServices] IConfiguration config, CancellationToken ct)
        => Ok(new ApiResponse<PlatformBillingSettingsDto>(true, await settings.GetAsync(await EffectiveModeAsync(gate, config, ct), ct)));

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<PlatformBillingSettingsDto>>> UpdateSettings(
        [FromBody] UpdatePlatformBillingSettingsRequest req,
        [FromServices] IPlatformBillingSettingsService settings, [FromServices] ISubscriptionGateShadowLog gate,
        [FromServices] IConfiguration config, CancellationToken ct)
    {
        var actor = new PlatformBillingActor(JwtHelper.GetUserIdFromClaims(User), User.FindFirstValue(ClaimTypes.Email),
            HttpContext.Connection.RemoteIpAddress?.ToString());
        var dto = await settings.UpdateAsync(req, actor, await EffectiveModeAsync(gate, config, ct), ct);
        return Ok(new ApiResponse<PlatformBillingSettingsDto>(true, dto, "บันทึกค่าตั้งการเรียกเก็บแล้ว"));
    }

    // ═══════════ รอบ 219 เฟส 2 ขั้น 2: ใบค้างทุกบริษัท · ทวงหนี้/ระงับ · ข้อความที่ส่ง ═══════════

    /// <summary>ใบแจ้งหนี้ค่าบริการที่ยังค้างของทุกบริษัทผู้เช่า + สถานะทวงหนี้ของบริษัท (ตัวหาเดียว PlatformInvoiceQueries)</summary>
    [HttpGet("open-invoices")]
    public async Task<ActionResult<ApiResponse<object>>> OpenInvoices(CancellationToken ct)
    {
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);
        if (platformId == null)
            return Ok(new ApiResponse<object>(true, new { configured = false, rows = Array.Empty<object>() },
                "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ (ตั้งค่าเว็บไซต์ → ข้อมูลผู้ขาย) — ยังไม่มีใบแจ้งหนี้จริงให้ติดตาม"));
        var rows = await PlatformInvoiceQueries.OpenForAllBuyersAsync(_db, platformId.Value, 1000, ct);
        var buyers = rows.Select(r => r.BuyerCompanyId).Distinct().ToList();
        var names = await CompanyNamesAsync(buyers, ct);
        var states = await _db.PlatformDunningStates.AsNoTracking().Where(s => buyers.Contains(s.CompanyId))
            .ToDictionaryAsync(s => s.CompanyId, ct);
        var subs = await _db.Subscriptions.AsNoTracking().Where(s => buyers.Contains(s.CompanyId))
            .Select(s => new { s.CompanyId, s.Status, s.RenewalInvoiceDocumentId })
            .ToListAsync(ct);
        var subBy = subs.GroupBy(s => s.CompanyId).ToDictionary(g => g.Key, g => g.First());
        var now = DateTime.UtcNow;
        return Ok(new ApiResponse<object>(true, new
        {
            configured = true,
            rows = rows.Select(r =>
            {
                states.TryGetValue(r.BuyerCompanyId, out var st);
                subBy.TryGetValue(r.BuyerCompanyId, out var sb);
                var late = PlatformInvoiceRules.DaysPastDue(r.DueDate, now);
                var state = PlatformInvoiceRules.StateOf(r.Status, r.BalanceDue, r.DueDate, now);
                var kind = PlatformInvoiceRules.KindOf(r.Reference);
                return new
                {
                    r.Id, r.BuyerCompanyId, companyName = names.GetValueOrDefault(r.BuyerCompanyId, "(ไม่พบชื่อ)"),
                    r.DocumentNumber, kind = kind.ToString(), kindLabel = PlatformInvoiceRules.KindLabel(kind),
                    r.DocumentDate, r.DueDate, r.TotalAmount, r.BalanceDue, daysPastDue = late,
                    state = state.ToString(), stateLabel = PlatformInvoiceRules.StateLabel(state, late),
                    subscriptionStatus = sb?.Status.ToString(),
                    suspendedByDunning = st?.SuspendedByDunningAt != null,
                    wouldSuspendSince = st?.WouldSuspendSince,
                    suspendExemptUntil = st?.SuspendExemptUntil,
                    lastDunningAction = st?.LastAction,
                    lastEvaluatedAt = st?.LastEvaluatedAt,
                };
            }).ToList(),
        }));
    }

    public record DunningExemptRequest(int Days, string? Reason);

    [HttpPost("dunning/{companyId:guid}/evaluate")]
    public async Task<ActionResult<ApiResponse<object>>> EvaluateDunning(Guid companyId,
        [FromServices] IPlatformDunningService dunning, CancellationToken ct)
    {
        var r = await dunning.EvaluateCompanyAsync(companyId, sendReminders: false, ct);
        return Ok(new ApiResponse<object>(true, r, r == null ? "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ/ไม่พบ subscription" : r.Reason));
    }

    [HttpPost("dunning/{companyId:guid}/exempt")]
    public async Task<ActionResult<ApiResponse<object>>> ExemptDunning(Guid companyId, [FromBody] DunningExemptRequest req,
        [FromServices] IPlatformDunningService dunning, CancellationToken ct)
    {
        var actor = new PlatformBillingActor(JwtHelper.GetUserIdFromClaims(User), User.FindFirstValue(ClaimTypes.Email),
            HttpContext.Connection.RemoteIpAddress?.ToString());
        var r = await dunning.ExemptAsync(companyId, req.Days, req.Reason ?? "", actor, ct);
        return Ok(new ApiResponse<object>(true, r, r?.Reason ?? "บันทึกแล้ว"));
    }

    // ═══════════ ขั้น 4: ลิงก์ชำระ — สร้างให้ใบที่มีอยู่แล้วเท่านั้น (ห้ามยอดอิสระ) · โทเคนแสดงครั้งเดียว · audit ทุกการกระทำ ═══════════

    public record CreatePaymentLinkRequest(int? ExpiryDays);
    public record RevokePaymentLinkRequest(string? Reason);

    private PlatformBillingActor Actor() => new(JwtHelper.GetUserIdFromClaims(User), User.FindFirstValue(ClaimTypes.Email),
        HttpContext.Connection.RemoteIpAddress?.ToString());

    [HttpPost("invoices/{documentId:guid}/payment-links")]
    public async Task<ActionResult<ApiResponse<PaymentLinkCreated>>> CreatePaymentLink(Guid documentId, [FromBody] CreatePaymentLinkRequest? req,
        [FromServices] IPlatformPaymentLinkService links, CancellationToken ct)
    {
        var created = await links.CreateAsync(documentId, req?.ExpiryDays, Actor(), ct);
        return Ok(new ApiResponse<PaymentLinkCreated>(true, created,
            $"สร้างลิงก์ชำระใบ {created.InvoiceNumber} แล้ว — คัดลอกตอนนี้ (ระบบเก็บเฉพาะรหัสแฮช แสดงลิงก์ซ้ำไม่ได้)"));
    }

    [HttpGet("payment-links")]
    public async Task<ActionResult<ApiResponse<List<PaymentLinkRow>>>> PaymentLinks([FromQuery] Guid? documentId,
        [FromServices] IPlatformPaymentLinkService links, CancellationToken ct)
        => Ok(new ApiResponse<List<PaymentLinkRow>>(true, await links.ListAsync(documentId, ct)));

    [HttpPost("payment-links/{linkId:guid}/revoke")]
    public async Task<ActionResult<ApiResponse<object>>> RevokePaymentLink(Guid linkId, [FromBody] RevokePaymentLinkRequest req,
        [FromServices] IPlatformPaymentLinkService links, CancellationToken ct)
    {
        await links.RevokeAsync(linkId, req?.Reason ?? "", Actor(), ct);
        return Ok(new ApiResponse<object>(true, null, "ยกเลิกลิงก์แล้ว"));
    }

    /// <summary>ใบแจ้งหนี้/แจ้งเตือนที่ส่งล่าสุด — ส่งไม่ถึง (Failed/Skipped) ขึ้นก่อน (ล้มดัง)</summary>
    [HttpGet("notices")]
    public async Task<ActionResult<ApiResponse<object>>> Notices(CancellationToken ct)
    {
        var rows = await _db.PlatformBillingNotices.AsNoTracking()
            .OrderByDescending(n => n.CreatedAt).Take(300).ToListAsync(ct);
        var names = await CompanyNamesAsync(rows.Select(r => r.BuyerCompanyId), ct);
        return Ok(new ApiResponse<object>(true, rows
            .OrderBy(r => r.Status == PlatformBillingNotifier.StatusSent ? 1 : 0).ThenByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id, r.BuyerCompanyId, companyName = names.GetValueOrDefault(r.BuyerCompanyId, "(ไม่พบชื่อ)"),
                r.MessageKey, r.Channel, r.Status, r.Detail, r.CreatedAt, r.SentAt,
            }).ToList()));
    }

    // ═══════════ ฝ่ายค้านรอบ 219 เฟส 2 (S2/B2/S3): รายการชำระที่ต้องจัดการด้วยมือ — ล้มดัง (ไม่ใช่หมายเหตุใน ReviewNotes ที่ไม่มีใครเห็น) ═══════════

    /// <summary>รายการชำระค่าบริการที่มีเรื่องค้าง (<c>AttentionReason</c>): ตัดชำระล้ม/ไม่มีใบเสร็จ · พักการอนุมัติอัตโนมัติ · รับเกินยอด — ใหม่สุดก่อน</summary>
    [HttpGet("payment-exceptions")]
    public async Task<ActionResult<ApiResponse<object>>> PaymentExceptions(CancellationToken ct)
    {
        var rows = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.AttentionReason != null)
            .OrderByDescending(p => p.AttentionAt)
            .Select(p => new
            {
                p.Id, p.PaymentNumber, p.Subscription.CompanyId, p.Amount, p.WithholdingTaxAmount, status = p.Status.ToString(),
                p.AttentionReason, p.AttentionRule, p.AttentionAt, p.PlatformInvoiceDocumentId, p.ReceiptNumber, p.PaymentDate,
                canRetrySettle = p.Status == SubscriptionPaymentStatus.Approved && p.PlatformInvoiceDocumentId != null && p.PlatformDocumentId == null,
            })
            .Take(500).ToListAsync(ct);
        var names = await CompanyNamesAsync(rows.Select(r => r.CompanyId), ct);
        return Ok(new ApiResponse<object>(true, rows.Select(r => new
        {
            r.Id, r.PaymentNumber, r.CompanyId, companyName = names.GetValueOrDefault(r.CompanyId, "(ไม่พบชื่อ)"), r.Amount, r.WithholdingTaxAmount,
            r.status, r.AttentionReason, r.AttentionRule, r.AttentionAt, r.PlatformInvoiceDocumentId, r.ReceiptNumber, r.PaymentDate, r.canRetrySettle,
        }).ToList()));
    }

    /// <summary>ตัดชำระใบแจ้งหนี้อีกครั้ง (idempotent ด้วยอ้างอิงต่อรายการ — บันทึกรับเงินไปแล้วไม่ซ้ำ · หาใบเสร็จที่ออกภายหลังได้)</summary>
    [HttpPost("payment-exceptions/{paymentId:guid}/retry-settle")]
    public async Task<ActionResult<ApiResponse<object>>> RetrySettle(Guid paymentId, [FromServices] ISaasBillingDocumentService billing, CancellationToken ct)
    {
        var p = await _db.SubscriptionPayments.AsNoTracking().Where(x => x.Id == paymentId)
            .Select(x => new { x.Status, x.PlatformInvoiceDocumentId, x.PlatformDocumentId }).FirstOrDefaultAsync(ct);
        if (p == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการชำระ"));
        if (p.Status != SubscriptionPaymentStatus.Approved || p.PlatformInvoiceDocumentId == null)
            return StatusCode(409, new ApiResponse<object>(false, null, "ตัดชำระได้เฉพาะรายการที่อนุมัติแล้วและผูกใบแจ้งหนี้ — รายการที่พักไว้ให้ใช้ปุ่มอนุมัติ/ปฏิเสธ"));
        if (p.PlatformDocumentId != null) return Ok(new ApiResponse<object>(true, null, "ตัดชำระและออกใบเสร็จแล้ว"));
        await billing.SettlePlatformInvoiceAsync(paymentId);
        var after = await _db.SubscriptionPayments.AsNoTracking().Where(x => x.Id == paymentId)
            .Select(x => new { x.PlatformDocumentId, x.ReceiptNumber, x.AttentionReason }).FirstAsync(ct);
        return Ok(new ApiResponse<object>(after.PlatformDocumentId != null, after,
            after.PlatformDocumentId != null ? $"ตัดชำระแล้ว · ใบเสร็จ {after.ReceiptNumber}" : (after.AttentionReason ?? "ยังตัดชำระไม่สำเร็จ")));
    }

    /// <summary>ฝ่ายค้าน S9: สลิปของรายการชำระค่าบริการ (SystemAdmin) — ไม่เสิร์ฟเป็นไฟล์สาธารณะแล้ว</summary>
    [HttpGet("subscription-payments/{paymentId:guid}/slip")]
    public async Task<IActionResult> PaymentSlip(Guid paymentId, [FromServices] IWebHostEnvironment env, CancellationToken ct)
    {
        var p = await _db.SubscriptionPayments.AsNoTracking().Where(x => x.Id == paymentId)
            .Select(x => new { x.SlipFileName, x.SlipOriginalFileName }).FirstOrDefaultAsync(ct);
        if (p == null || SubscriptionSlipUpload.OpenStored(env, p.SlipFileName) is not { } f)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบสลิป"));
        Response.Headers["Cache-Control"] = "private, no-store";
        return PhysicalFile(f.Path, f.ContentType, p.SlipOriginalFileName ?? p.SlipFileName);
    }

    public record ResolveAttentionRequest(string? Note);

    /// <summary>แอดมินจัดการเรื่องค้างนอกระบบแล้ว (คืนเงินส่วนเกิน · ออกใบลดหนี้ · บันทึกด้วยมือ) — หมายเหตุบังคับ · audit chain</summary>
    [HttpPost("payment-exceptions/{paymentId:guid}/resolve")]
    public async Task<ActionResult<ApiResponse<object>>> ResolveAttention(Guid paymentId, [FromBody] ResolveAttentionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Note)) return BadRequest(new ApiResponse<object>(false, null, "กรุณาระบุว่าจัดการอย่างไร"));
        var p = await _db.SubscriptionPayments.Include(x => x.Subscription).FirstOrDefaultAsync(x => x.Id == paymentId, ct);
        if (p == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการชำระ"));
        if (p.Status is SubscriptionPaymentStatus.Pending or SubscriptionPaymentStatus.UnderReview)
            return StatusCode(409, new ApiResponse<object>(false, null, "รายการยังรอตรวจ — อนุมัติหรือปฏิเสธก่อน"));
        var note = req!.Note!.Trim();
        PlatformPaymentAttention.Clear(_db, p, p.Subscription.CompanyId, User.FindFirstValue(ClaimTypes.Email) ?? JwtHelper.GetUserIdFromClaims(User).ToString(),
            note.Length > 300 ? note[..300] : note);
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, null, "บันทึกว่าจัดการแล้ว"));
    }

    private async Task<Dictionary<Guid, string>> CompanyNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await _db.Companies.IgnoreQueryFilters().AsNoTracking()
            .Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }
}

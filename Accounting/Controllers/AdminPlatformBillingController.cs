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

    /// <summary>คำตัดสิน 260-5-6 (ฝ่ายค้าน M4): ค่าสแกน API v1 ที่ไม่ควรถูกคิดแต่อยู่ในใบแจ้งหนี้ค่าบริการแล้ว (+ สแกนล้มที่กินโควตาฟรีของเดือนที่ปิดรอบ) —
    /// คิวอ่านอย่างเดียวให้ผู้ให้บริการออกใบลดหนี้ (ม.86/10) · ตัวประกอบเดียว <c>OcrFailedScanRefund.AwaitingCreditNotesAsync</c></summary>
    [HttpGet("ocr-v1-credit-notes")]
    public async Task<ActionResult<ApiResponse<object>>> OcrV1CreditNotes(CancellationToken ct)
    {
        var queue = await Accounting.Helpers.OcrFailedScanRefund.AwaitingCreditNotesAsync(_db, ct);
        return Ok(new ApiResponse<object>(true, queue));
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

    /// <summary>รอบ 228 (งานข้อ 6): รายงานรายได้ประจำ — แพ็กเกจที่ใช้งานจ่ายเงินแยกตามแพ็กเกจ + MRR · ลูกหนี้ค่าบริการค้างตามอายุ · ส่วนเสริม (MRR + รับเงินล่วงหน้า 30 วัน) ·
    /// อ่านอย่างเดียว · ตัวเลขจากตัวตัดสินเดียว <see cref="PlatformRevenueMetrics"/> · SystemAdmin (ทั้งคลาส · ข้ามบริษัทโดยตั้งใจ — รายงานของแพลตฟอร์ม)</summary>
    [HttpGet("revenue-report")]
    public async Task<ActionResult<ApiResponse<object>>> RevenueReport(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);

        // ── แพ็กเกจ (ไม่นับบริษัทผู้ให้บริการเอง) ──
        var subs = await _db.Subscriptions.AsNoTracking()
            .Where(s => platformId == null || s.CompanyId != platformId.Value)
            .Select(s => new { s.CompanyId, s.Plan, s.Status, s.IsPermanentFree, s.PricePerCycle, s.BillingCycle, s.CancelAtPeriodEnd })
            .ToListAsync(ct);
        var planNames = await _db.PlanTemplates.AsNoTracking().Where(t => t.IsActive)
            .Select(t => new { t.Plan, t.Name }).ToListAsync(ct);
        var nameOf = planNames.GroupBy(p => p.Plan).ToDictionary(g => g.Key, g => g.First().Name);
        var paying = subs.Where(s => PlatformRevenueMetrics.CountsTowardMrr(s.Status, s.IsPermanentFree, s.PricePerCycle)).ToList();
        var byPlan = paying.GroupBy(s => s.Plan).Select(g => new
        {
            plan = g.Key.ToString(), planName = nameOf.GetValueOrDefault(g.Key, g.Key.ToString()),
            activeCount = g.Count(),
            pastDueCount = g.Count(x => x.Status == SubscriptionStatus.PastDue),
            cancellingCount = g.Count(x => x.CancelAtPeriodEnd),
            mrr = g.Sum(x => PlatformRevenueMetrics.MonthlyEquivalent(x.PricePerCycle, x.BillingCycle)),
        }).OrderByDescending(x => x.mrr).ToList();
        var statusCounts = subs.GroupBy(s => s.Status).Select(g => new { status = g.Key.ToString(), count = g.Count() }).ToList();

        // ── ส่วนเสริม: ที่เปิดใช้และต้องจ่าย (ราคา/เดือน) — ฝ่ายค้าน S6: MRR นับเฉพาะที่จ่ายแล้ว · ไม่อยู่ในช่วงทดลอง · บริษัทมีแพ็กเกจที่ใช้งาน
        //    (รอชำระ/ทดลอง/บริษัทหมดอายุ แยกคอลัมน์ ไม่ปนรายได้ประจำ) — ตัวตัดสินเดียว PlatformRevenueMetrics.AddOnCountsTowardMrr ──
        var activeCompanies = subs.Where(x => x.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue or SubscriptionStatus.Trial)
            .Select(x => x.CompanyId).ToHashSet();
        var addOns = await _db.CompanyFeatures.AsNoTracking()
            .Where(f => !f.IsDeleted && f.IsEnabled && f.PaymentStatus != AddOnPaymentStatus.NotRequired && f.PaymentStatus != AddOnPaymentStatus.Rejected
                && (platformId == null || f.CompanyId != platformId.Value))
            .Select(f => new { f.CompanyId, f.FeatureCode, f.PaymentStatus, f.TrialUntil, Price = f.SnapshotUnitPrice ?? f.AcceptedUnitPrice })
            .ToListAsync(ct);
        var addOnByCode = addOns.GroupBy(a => a.FeatureCode).Select(g =>
        {
            var counted = g.Where(x => PlatformRevenueMetrics.AddOnCountsTowardMrr(x.PaymentStatus, x.TrialUntil, activeCompanies.Contains(x.CompanyId), now)).ToList();
            return new
            {
                featureCode = g.Key, count = g.Count(), paidCount = counted.Count,
                awaitingCount = g.Count(x => x.PaymentStatus is AddOnPaymentStatus.AwaitingPayment or AddOnPaymentStatus.PendingReview),
                trialCount = g.Count(x => x.TrialUntil > now),
                mrr = Math.Round(counted.Sum(x => x.Price ?? 0m), 2, MidpointRounding.AwayFromZero),
            };
        }).OrderByDescending(x => x.mrr).ToList();

        // ── ลูกหนี้ค่าบริการค้างตามอายุ + ใบเสร็จค่าส่วนเสริม 30 วัน (เอกสารจริงใน tenant ผู้ให้บริการ) ──
        object? aging = null;
        decimal addOnReceipts30 = 0m;
        if (platformId is Guid pid)
        {
            var open = await PlatformInvoiceQueries.OpenForAllBuyersAsync(_db, pid, 5000, ct);
            aging = PlatformRevenueMetrics.AgingBuckets.Select(b =>
            {
                var rows = open.Where(r => PlatformRevenueMetrics.AgingBucket(PlatformInvoiceRules.DaysPastDue(r.DueDate, now)) == b).ToList();
                return new { bucket = b, count = rows.Count, amount = rows.Sum(r => r.BalanceDue), companies = rows.Select(r => r.BuyerCompanyId).Distinct().Count() };
            }).ToList();
            var since = now.AddDays(-30);
            addOnReceipts30 = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == pid && !d.IsDeleted && d.Reference != null && d.Reference.StartsWith("ADDON-")
                    && (d.DocumentType == DocumentType.TaxInvoice || d.DocumentType == DocumentType.Receipt)
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.DocumentDate >= since)
                .SumAsync(d => (decimal?)d.TotalAmount, ct) ?? 0m;
        }

        return Ok(new ApiResponse<object>(true, new
        {
            generatedAt = now,
            platformConfigured = platformId != null,
            mrr = byPlan.Sum(x => x.mrr),
            addOnMrr = addOnByCode.Sum(x => x.mrr),
            payingSubscriptions = paying.Count,
            byPlan, statusCounts, addOns = addOnByCode,
            aging, addOnReceiptsLast30Days = addOnReceipts30,
            note = "MRR = ราคาต่อรอบ ÷ จำนวนเดือนของรอบ ของแพ็กเกจ Active/ค้างชำระที่ไม่ฟรี · ส่วนเสริม = ราคา/เดือนของที่เปิดใช้และต้องจ่าย · ลูกหนี้ = ใบแจ้งหนี้ค่าบริการที่ยังค้าง",
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
                hasWhtCertificate = p.WhtCertificateFileName != null,
            })
            .Take(500).ToListAsync(ct);
        var names = await CompanyNamesAsync(rows.Select(r => r.CompanyId), ct);
        return Ok(new ApiResponse<object>(true, rows.Select(r => new
        {
            r.Id, r.PaymentNumber, r.CompanyId, companyName = names.GetValueOrDefault(r.CompanyId, "(ไม่พบชื่อ)"), r.Amount, r.WithholdingTaxAmount,
            r.status, r.AttentionReason, r.AttentionRule, r.AttentionAt, r.PlatformInvoiceDocumentId, r.ReceiptNumber, r.PaymentDate, r.canRetrySettle,
            r.hasWhtCertificate,
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
    public async Task<IActionResult> PaymentSlip(Guid paymentId, [FromServices] IWebHostEnvironment env, [FromServices] Accounting.Services.Storage.IFileStorage storage, CancellationToken ct)
    {
        var p = await _db.SubscriptionPayments.AsNoTracking().Where(x => x.Id == paymentId)
            .Select(x => new { x.SlipFileName, x.SlipOriginalFileName }).FirstOrDefaultAsync(ct);
        if (p == null || SubscriptionSlipUpload.OpenStored(env, p.SlipFileName) is not { } f)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบสลิป"));
        Response.Headers["Cache-Control"] = "private, no-store";
        return await SubscriptionSlipUpload.ServeAsync(storage, f, p.SlipOriginalFileName ?? p.SlipFileName)
            ?? NotFound(new ApiResponse<object>(false, null, "ไม่พบสลิป"));
    }

    /// <summary>ฝ่ายค้านรอบสุดท้าย 4a: หนังสือรับรอง 50 ทวิ ที่ผู้เช่าส่ง (SystemAdmin)</summary>
    [HttpGet("subscription-payments/{paymentId:guid}/wht-certificate")]
    public async Task<IActionResult> WhtCertificate(Guid paymentId, [FromServices] IWebHostEnvironment env, [FromServices] Accounting.Services.Storage.IFileStorage storage, CancellationToken ct)
    {
        var name = await _db.SubscriptionPayments.AsNoTracking().Where(x => x.Id == paymentId).Select(x => x.WhtCertificateFileName).FirstOrDefaultAsync(ct);
        if (SubscriptionSlipUpload.OpenStored(env, name) is not { } f)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบหนังสือรับรอง"));
        Response.Headers["Cache-Control"] = "private, no-store";
        return await SubscriptionSlipUpload.ServeAsync(storage, f, "50tawi-" + System.IO.Path.GetFileName(f.Path))
            ?? NotFound(new ApiResponse<object>(false, null, "ไม่พบหนังสือรับรอง"));
    }

    // ═══════════ รอบ 234 (คำตัดสิน 234-W*): 50 ทวิ จากลูกค้า — ติดตาม · ยืนยันเข้าทะเบียนภาษีถูกหัก · ตีกลับ · ทวง · ผูกรายการเก่า ═══════════
    // ทะเบียนเดียว = WhtCreditReceived ของบริษัทผู้ให้บริการ (หน้า /pages/wht-credit.html) · ตัวตัดสินสถานะ PlatformWhtCertificateStatus

    [HttpGet("wht-certificates")]
    public async Task<ActionResult<ApiResponse<object>>> WhtCertificates([FromServices] Accounting.Services.Platform.PlatformWhtCertificateService svc,
        [FromQuery] int? overdueDays, [FromQuery] bool all = false, CancellationToken ct = default)
    {
        var days = overdueDays is int d && d > 0 ? d : PlatformWhtCertificateStatus.DefaultOverdueDays;
        var rows = await svc.ListAsync(days, all, ct);
        var quotaAddOns = await svc.ListQuotaAddOnsAsync(all, ct);   // รอบ 235 (ฝ่ายค้าน N-4): โควตาเสริมที่หัก ณ ที่จ่าย (อ่านจากทะเบียน)
        return Ok(new ApiResponse<object>(true, new
        {
            overdueDays = days, platformCompanyId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct), rows, quotaAddOns,
            awaitingUpload = rows.Count(r => r.State == nameof(PlatformWhtCertificateStatus.State.AwaitingUpload) || r.State == nameof(PlatformWhtCertificateStatus.State.Rejected)),
            awaitingReview = rows.Count(r => r.State == nameof(PlatformWhtCertificateStatus.State.AwaitingReview)),
            overdue = rows.Count(r => r.Overdue),
        }));
    }

    public record ConfirmWhtCertificateRequest(string? CertificateNumber, DateTime? CertificateDate, decimal? WhtAmount = null);
    public record RejectWhtCertificateRequest(string? Reason);

    [HttpPost("wht-certificates/{paymentId:guid}/confirm")]
    public async Task<ActionResult<ApiResponse<object>>> ConfirmWhtCertificate(Guid paymentId, [FromBody] ConfirmWhtCertificateRequest req,
        [FromServices] Accounting.Services.Platform.PlatformWhtCertificateService svc, CancellationToken ct)
    {
        var row = await svc.ConfirmAsync(paymentId, req?.CertificateNumber, req?.CertificateDate, AdminActor(), ct, req?.WhtAmount);
        return Ok(new ApiResponse<object>(true, row, "ยืนยันแล้ว — เข้าทะเบียนภาษีถูกหัก ณ ที่จ่ายของบริษัทผู้ให้บริการ (เครดิต ภ.ง.ด.50/51 ได้)"));
    }

    [HttpPost("wht-certificates/{paymentId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<object>>> RejectWhtCertificate(Guid paymentId, [FromBody] RejectWhtCertificateRequest req,
        [FromServices] Accounting.Services.Platform.PlatformWhtCertificateService svc, CancellationToken ct)
    {
        await svc.RejectAsync(paymentId, req?.Reason, AdminActor(), ct);
        return Ok(new ApiResponse<object>(true, null, "ตีกลับแล้ว — แจ้งผู้เช่าให้ส่งใหม่"));
    }

    [HttpPost("wht-certificates/{paymentId:guid}/remind")]
    public async Task<ActionResult<ApiResponse<object>>> RemindWhtCertificate(Guid paymentId,
        [FromServices] Accounting.Services.Platform.PlatformWhtCertificateService svc, CancellationToken ct)
    {
        await svc.RemindAsync(paymentId, AdminActor(), ct);
        return Ok(new ApiResponse<object>(true, null, "ส่งคำทวงถึงเจ้าของบริษัทแล้ว"));
    }

    /// <summary>รอบ 235 (ฝ่ายค้าน N-4): ทวง 50 ทวิ ของโควตาเสริม (วันละครั้ง · เส้นแจ้งเตือนเดิม)</summary>
    [HttpPost("wht-certificates/quota-addons/{purchaseId:guid}/remind")]
    public async Task<ActionResult<ApiResponse<object>>> RemindQuotaWhtCertificate(Guid purchaseId,
        [FromServices] Accounting.Services.Platform.PlatformWhtCertificateService svc, CancellationToken ct)
    {
        await svc.RemindQuotaAddOnAsync(purchaseId, AdminActor(), ct);
        return Ok(new ApiResponse<object>(true, null, "ส่งคำทวงถึงเจ้าของบริษัทแล้ว"));
    }

    /// <summary>234-W4: ผูกรายการเก่า (อนุมัติก่อนรอบ 234 · มีภาษีหัก) กับทะเบียน — รันซ้ำได้ · ไม่ประทับ "ได้รับใบแล้ว" เอง</summary>
    [HttpPost("wht-certificates/backfill")]
    public async Task<ActionResult<ApiResponse<object>>> BackfillWhtCertificates([FromServices] Accounting.Services.Platform.PlatformWhtCertificateService svc, CancellationToken ct)
    {
        var r = await svc.BackfillAsync(AdminActor(), ct);
        if (r is not { } res) return StatusCode(409, new ApiResponse<object>(false, null, "มีการผูกทะเบียนกำลังรันอยู่ที่เครื่องอื่น — ลองใหม่ภายหลัง"));
        return Ok(new ApiResponse<object>(true, new { res.Linked, res.Missing },
            $"ผูกทะเบียนแล้ว {res.Linked} รายการ" + (res.Missing > 0 ? $" · ยังไม่มีแถวในทะเบียน {res.Missing} รายการ (ใบเสร็จยังไม่ออก/ไม่มีขา 11910)" : "")));
    }

    private string AdminActor() => User.FindFirstValue(ClaimTypes.Email) ?? $"admin:{JwtHelper.GetUserIdFromClaims(User)}";

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
        // ฝ่ายค้านรอบสุดท้าย: อนุมัติแล้วแต่ยังไม่ตัดชำระ และใบยังค้าง ⇒ ห้ามปิดเรื่องเฉย ๆ (ใบถูกยกเว้นจากทวงหนี้ไปตลอดโดยไม่มีใครเห็น) —
        // ต้องตัดชำระให้สำเร็จ หรือปิดใบในบริษัทผู้ให้บริการก่อน (ยกเลิก/ใบลดหนี้)
        if (p.Status == SubscriptionPaymentStatus.Approved && p.PlatformInvoiceDocumentId is Guid invId && p.PlatformDocumentId == null
            && await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct) is Guid platformId
            && await PlatformInvoiceQueries.ByIdAsync(_db, platformId, invId, ct) is { } inv && PlatformInvoiceRules.IsOpen(inv.Status, inv.BalanceDue))
            return StatusCode(409, new ApiResponse<object>(false, null,
                $"ใบ {inv.DocumentNumber} ยังค้างและรายการนี้ยังไม่ได้ตัดชำระ — กด \"ตัดชำระอีกครั้ง\" (หรือบันทึกรับชำระด้วยมือในบริษัทผู้ให้บริการด้วยอ้างอิง "
                + $"{SaasBillingDocumentService.SettlementReference(p.PaymentNumber)} แล้วกดตัดชำระอีกครั้ง) หรือยกเลิก/ออกใบลดหนี้ใบนั้นก่อน"));
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

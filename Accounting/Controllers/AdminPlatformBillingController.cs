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

    private async Task<Dictionary<Guid, string>> CompanyNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await _db.Companies.IgnoreQueryFilters().AsNoTracking()
            .Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }
}

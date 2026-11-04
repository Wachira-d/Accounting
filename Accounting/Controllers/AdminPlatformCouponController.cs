using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §7.1 · คำตัดสินข้อ 230-Q2) — แอดมินแพลตฟอร์ม: <b>คูปอง/โค้ดส่วนลดของ NextAcc</b> (สร้าง · แก้ · ปิด · ดูการใช้) +
/// <b>แก้ยอดรายการชำระค่าแพ็กเกจที่ยังไม่จ่าย</b> (คำตัดสินเจ้าของข้อ 178 ① — แอดมินยืดหยุ่นได้เสมอ) · ทุกการเขียน audit chain ·
/// ตัวตัดสินค่าที่กรอก <see cref="PlatformCouponRule.AdminProblem"/> · หน้า <c>/admin/coupons.html</c>
/// <para>ไม่ลบคูปองที่เคยถูกใช้ (หลักฐานใบกำกับอ้างรหัส) — "ปิดใช้" แทน · ลบได้เฉพาะคูปองที่ไม่มีการใช้/จองเลย (soft delete)</para>
/// </summary>
[ApiController]
[Route("api/admin/platform-coupons")]
[Authorize(Roles = "SystemAdmin")]
public class AdminPlatformCouponController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ISubscriptionService _subs;

    public AdminPlatformCouponController(AccountingDbContext db, ISubscriptionService subs) { _db = db; _subs = subs; }

    private string Actor => JwtHelper.GetUserIdFromClaims(User).ToString();

    public sealed record CouponBody(string Code, string? Description, PlatformCouponKind Kind, decimal Value, DateTime? ValidFromUtc, DateTime? ValidToUtc,
        int? MaxUsesTotal, int? MaxUsesPerCompany, PlatformCouponScope AppliesTo, List<string>? Plans, List<string>? Cycles, List<string>? AddOnResources,
        bool FirstPaymentOnly, bool IsActive = true,
        // รอบ 235 (K-2 · ฝ่ายค้าน S-1): ใช้กับซื้อใหม่/ต่ออายุ/ทั้งสอง — ไม่ส่ง = New (ซื้อใหม่เท่านั้น — นโยบาย 233-5 จนแอดมินเปิดต่ออายุเอง)
        PlatformCouponPurchaseKinds PurchaseKinds = PlatformCouponPurchaseKinds.New);

    private static object Map(PlatformCoupon c, int reserved, int redeemed, DateTime now) => new
    {
        id = c.Id, code = c.Code, description = c.Description, kind = c.Kind.ToString(), value = c.Value,
        validFromUtc = c.ValidFromUtc, validToUtc = c.ValidToUtc, maxUsesTotal = c.MaxUsesTotal, maxUsesPerCompany = c.MaxUsesPerCompany,
        usedCount = c.UsedCount, reservedCount = reserved, redeemedCount = redeemed,
        appliesTo = c.AppliesTo.ToString(), plans = Split(c.PlansCsv), cycles = Split(c.CyclesCsv), addOnResources = Split(c.AddOnResourcesCsv),
        firstPaymentOnly = c.FirstPaymentOnly, isActive = c.IsActive, label = PlatformCouponRule.LabelOf(c),
        purchaseKinds = c.PurchaseKinds.ToString(), purchaseKindsLabel = PlatformCouponRule.PurchaseKindsLabel(c.PurchaseKinds),   // รอบ 235 (K-2) · echo กลับ
        // ป้ายสถานะจากเซิร์ฟเวอร์ (หน้าเว็บไม่ตัดสินเอง)
        statusLabel = !c.IsActive ? "ปิดใช้" : c.ValidToUtc is DateTime to && now >= to ? "หมดอายุ"
            : c.ValidFromUtc is DateTime f && now < f ? "ยังไม่เริ่ม" : c.MaxUsesTotal is int m && redeemed + reserved >= m ? "ใช้ครบแล้ว" : "ใช้ได้",
        createdAt = c.CreatedAt,
    };

    private static List<string> Split(string? csv) => string.IsNullOrWhiteSpace(csv) ? new List<string>()
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>ตัวเลือกที่หน้าฟอร์มใช้ (แพ็กเกจ/รอบบิล/ทรัพยากรที่ขายได้) — มาจากโค้ด/ฐาน ไม่พิมพ์ในหน้าเว็บ</summary>
    [HttpGet("options")]
    public async Task<ActionResult<ApiResponse<object>>> Options()
    {
        var plans = await _db.PlanTemplates.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.MonthlyPrice)
            .Select(t => new { value = t.Plan.ToString(), label = t.Name }).ToListAsync();
        var cycles = new[] { BillingCycle.Monthly, BillingCycle.Quarterly, BillingCycle.SemiAnnual, BillingCycle.Annual }
            .Select(c => new { value = c.ToString(), label = $"{PlatformPlanPricing.MonthsOf(c)} เดือน" });
        var resources = QuotaResources.All.Where(r => r.Sellable).Select(r => new { value = r.Key, label = r.Label });
        return Ok(new ApiResponse<object>(true, new { plans, cycles, resources }));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> List()
    {
        var now = DateTime.UtcNow;
        var coupons = await _db.PlatformCoupons.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(500).ToListAsync();
        var ids = coupons.Select(c => c.Id).ToList();
        var counts = await _db.PlatformCouponRedemptions.AsNoTracking().Where(r => ids.Contains(r.CouponId))
            .GroupBy(r => r.CouponId)
            .Select(g => new
            {
                Id = g.Key,
                Reserved = g.Count(r => r.Status == PlatformCouponRedemptionStatus.Reserved && r.ReservedUntilUtc > now),
                Redeemed = g.Count(r => r.Status == PlatformCouponRedemptionStatus.Redeemed),
            }).ToListAsync();
        var byId = counts.ToDictionary(x => x.Id);
        return Ok(new ApiResponse<object>(true, coupons.Select(c =>
            Map(c, byId.TryGetValue(c.Id, out var x) ? x.Reserved : 0, byId.TryGetValue(c.Id, out var y) ? y.Redeemed : 0, now)).ToList()));
    }

    /// <summary>การใช้คูปอง (บริษัท · รายการต้นทาง · ส่วนลด · สถานะ) ล่าสุดก่อน</summary>
    [HttpGet("{id:guid}/redemptions")]
    public async Task<ActionResult<ApiResponse<object>>> Redemptions(Guid id)
    {
        var rows = await _db.PlatformCouponRedemptions.AsNoTracking().Where(r => r.CouponId == id)
            .OrderByDescending(r => r.CreatedAt).Take(500)
            .Join(_db.Companies.AsNoTracking(), r => r.CompanyId, c => c.Id, (r, c) => new
            {
                r.Id, r.CompanyId, companyName = c.Name, r.SourceKind, r.SourceId, r.ListAmount, r.DiscountAmount, r.AmountAfter,
                status = r.Status.ToString(),
                statusLabel = r.Status == PlatformCouponRedemptionStatus.Redeemed ? "ใช้แล้ว (เงินเข้า)"
                    : r.Status == PlatformCouponRedemptionStatus.Released ? "ปลดแล้ว" : "จองไว้ (รอชำระ)",
                r.CreatedAt, r.RedeemedAt, r.ReleasedAt, r.ReleaseReason,
            }).ToListAsync();
        return Ok(new ApiResponse<object>(true, rows));
    }

    private string? Validate(CouponBody b)
        => PlatformCouponRule.AdminProblem(b.Code, b.Kind, b.Value, b.ValidFromUtc, b.ValidToUtc, b.MaxUsesTotal, b.MaxUsesPerCompany, b.AppliesTo, b.PurchaseKinds)
           ?? ListProblem(b);

    private static string? ListProblem(CouponBody b)
    {
        if (b.Plans?.Any(p => !Enum.TryParse<SubscriptionPlan>(p, true, out _)) == true) return "แพ็กเกจที่เลือกไม่ถูกต้อง";
        if (b.Cycles?.Any(c => !Enum.TryParse<BillingCycle>(c, true, out _)) == true) return "รอบบิลที่เลือกไม่ถูกต้อง";
        if (b.AddOnResources?.Any(r => QuotaResources.Find(r) is not { Sellable: true }) == true) return "รายการโควตาเสริมที่เลือกไม่ถูกต้อง";
        return null;
    }

    private static string? Csv(List<string>? xs) => xs == null || xs.Count == 0 ? null : string.Join(",", xs.Select(x => x.Trim()).Distinct());

    private void Apply(PlatformCoupon c, CouponBody b)
    {
        c.Code = PlatformCouponRule.Normalize(b.Code)!;
        c.Description = string.IsNullOrWhiteSpace(b.Description) ? null : b.Description.Trim()[..Math.Min(300, b.Description.Trim().Length)];
        c.Kind = b.Kind; c.Value = Math.Round(b.Value, 2, MidpointRounding.AwayFromZero);
        c.ValidFromUtc = b.ValidFromUtc; c.ValidToUtc = b.ValidToUtc;
        c.MaxUsesTotal = b.MaxUsesTotal; c.MaxUsesPerCompany = b.MaxUsesPerCompany;
        c.AppliesTo = b.AppliesTo; c.PlansCsv = Csv(b.Plans); c.CyclesCsv = Csv(b.Cycles); c.AddOnResourcesCsv = Csv(b.AddOnResources);
        c.FirstPaymentOnly = b.FirstPaymentOnly; c.IsActive = b.IsActive;
        c.PurchaseKinds = b.PurchaseKinds;   // รอบ 235 (K-2)
    }

    private void Audit(PlatformCoupon c, string action, object? before)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = null, UserEmail = Actor, Action = AuditAction.Update, EntityType = nameof(PlatformCoupon), EntityId = c.Id.ToString(),
            OldValues = before == null ? null : System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action, ruleCode = PlatformCouponRule.RuleCode, c.Code, kind = c.Kind.ToString(), c.Value, c.ValidFromUtc, c.ValidToUtc,
                c.MaxUsesTotal, c.MaxUsesPerCompany, appliesTo = c.AppliesTo.ToString(), c.PlansCsv, c.CyclesCsv, c.AddOnResourcesCsv,
                c.FirstPaymentOnly, c.IsActive, purchaseKinds = c.PurchaseKinds.ToString(), by = Actor,
            }),
            Timestamp = DateTime.UtcNow,
        });

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Create([FromBody] CouponBody body)
    {
        if (Validate(body) is string problem) return BadRequest(new ApiResponse<object>(false, null, problem));
        var code = PlatformCouponRule.Normalize(body.Code)!;
        if (await _db.PlatformCoupons.AnyAsync(c => c.Code == code))
            return Conflict(new ApiResponse<object>(false, null, $"มีคูปองรหัส {code} อยู่แล้ว"));
        var c = new PlatformCoupon { CreatedBy = Actor };
        Apply(c, body);
        _db.PlatformCoupons.Add(c);
        Audit(c, "PlatformCouponCreated", null);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, Map(c, 0, 0, DateTime.UtcNow), $"สร้างคูปอง {c.Code} แล้ว"));
    }

    /// <summary>แก้คูปอง — รายการที่จอง/ใช้ไปแล้วคงยอดเดิม (ส่วนลดคิดตอนสร้างรายการ ไม่คิดย้อนหลัง)</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Update(Guid id, [FromBody] CouponBody body)
    {
        if (Validate(body) is string problem) return BadRequest(new ApiResponse<object>(false, null, problem));
        var c = await _db.PlatformCoupons.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบคูปอง"));
        var code = PlatformCouponRule.Normalize(body.Code)!;
        var used = await _db.PlatformCouponRedemptions.AnyAsync(r => r.CouponId == id);
        if (used && code != c.Code)
            return Conflict(new ApiResponse<object>(false, null, "คูปองนี้ถูกใช้แล้ว — เปลี่ยนรหัสไม่ได้ (ใบกำกับอ้างรหัสเดิม) · สร้างคูปองใหม่แทน"));
        if (code != c.Code && await _db.PlatformCoupons.AnyAsync(x => x.Code == code && x.Id != id))
            return Conflict(new ApiResponse<object>(false, null, $"มีคูปองรหัส {code} อยู่แล้ว"));
        var before = new { c.Code, kind = c.Kind.ToString(), c.Value, c.ValidFromUtc, c.ValidToUtc, c.MaxUsesTotal, c.MaxUsesPerCompany, c.IsActive };
        Apply(c, body);
        c.UpdatedAt = DateTime.UtcNow; c.UpdatedBy = Actor;
        Audit(c, "PlatformCouponUpdated", before);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, Map(c, 0, 0, DateTime.UtcNow), $"บันทึกคูปอง {c.Code} แล้ว (รายการที่สร้างไปแล้วคงยอดเดิม)"));
    }

    /// <summary>ลบ — เฉพาะคูปองที่ไม่เคยถูกจอง/ใช้ (soft delete) · เคยใช้แล้ว ⇒ 409 ให้ "ปิดใช้" แทน (ไม่ทำลายหลักฐาน)</summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id)
    {
        var c = await _db.PlatformCoupons.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบคูปอง"));
        if (await _db.PlatformCouponRedemptions.AnyAsync(r => r.CouponId == id))
            return Conflict(new ApiResponse<object>(false, null, "คูปองนี้ถูกใช้/จองแล้ว — ลบไม่ได้ (เป็นหลักฐานของใบกำกับ) · ใช้ \"ปิดใช้\" แทน"));
        c.IsDeleted = true; c.IsActive = false; c.UpdatedAt = DateTime.UtcNow; c.UpdatedBy = Actor;
        Audit(c, "PlatformCouponDeleted", null);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, $"ลบคูปอง {c.Code} แล้ว"));
    }

    /// <summary>แก้ยอดรายการชำระค่าแพ็กเกจที่ยังไม่จ่าย (ส่วนต่างอัปเกรด/ราคาพิเศษ) — ตัวทำงาน <c>ISubscriptionService.OverridePaymentAmountAsync</c></summary>
    [HttpPost("~/api/admin/subscription-payments/{paymentId:guid}/amount")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> OverrideAmount(Guid paymentId, [FromBody] OverridePaymentAmountRequest body)
    {
        var r = await _subs.OverridePaymentAmountAsync(paymentId, body.Amount, body.Reason ?? "", Actor);
        // ฝ่ายค้าน N8: แก้ยอดหลังลูกค้าแนบสลิปแล้ว = ทำได้ (แอดมินยืดหยุ่น) แต่เตือนให้เทียบยอดในสลิป
        return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, r, $"แก้ยอด {r.PaymentNumber} เป็น {r.Amount:N2} บาทแล้ว"
            + (r.SlipFileName != null ? " · ⚠️ รายการนี้แนบสลิปแล้ว — ตรวจยอดในสลิปเทียบยอดใหม่ก่อนอนุมัติ" : "")
            + (r.CouponCode != null && r.CouponDiscountAmount <= 0m ? " · ปลดคูปองแล้ว (ยอดใหม่ไม่ต่ำกว่าราคาก่อนคูปอง)" : "")));
    }
}

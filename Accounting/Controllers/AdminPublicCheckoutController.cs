using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 232 (คำตัดสิน 232-Checkout-2): สวิตช์ "เปิดหน้าซื้อแพ็กเกจสาธารณะ" (<c>SiteSettings.PublicCheckoutEnabled</c> · ค่าเริ่มต้นปิด) + สถานะความพร้อมที่เซิร์ฟเวอร์ตัดสิน
/// (<see cref="PublicCheckoutPolicy.Decide"/> — โหมดทดสอบ/จริง · เหตุที่ยังซื้อออนไลน์ไม่ได้) · แก้จากหน้า <c>/admin/site-settings.html</c> (การ์ด "หน้าซื้อแพ็กเกจสาธารณะ") ·
/// audit chain ทุกครั้งที่สลับ · แยกจาก <c>AdminController</c> โดยตั้งใจ (ทีมอื่นแก้ไฟล์นั้นพร้อมกัน)
/// </summary>
[ApiController]
[Route("api/admin/public-checkout")]
[Authorize(Roles = "SystemAdmin")]
public class AdminPublicCheckoutController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPublicCheckoutService _checkout;
    private readonly IConfiguration _config;

    public AdminPublicCheckoutController(AccountingDbContext db, IPublicCheckoutService checkout, IConfiguration config)
    { _db = db; _checkout = checkout; _config = config; }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(CancellationToken ct)
    {
        var enabled = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => (bool?)s.PublicCheckoutEnabled).FirstOrDefaultAsync(ct);
        var avail = await _checkout.AvailabilityAsync(ct);
        var origin = SystemBaseUrl.PublicOrigin(await SystemBaseUrl.ResolveAsync(_db, _config, ct), Request.Scheme, Request.Host.ToString());
        return Ok(new ApiResponse<object>(true, new
        {
            enabled = enabled == true,
            onlineAvailable = avail.OnlineAvailable,
            isTestMode = avail.IsTestMode,
            reason = avail.Reason,
            pricingUrl = origin + "/pricing.html",
            checkoutUrl = origin + "/checkout.html",
            // ฝ่ายค้าน 232 B2: โดเมนจาก "URL ของระบบ" ถ้าตั้ง — ไม่งั้นโฮสต์ของคำขอ (หลัง proxy อาจเป็นชื่อภายใน) ⇒ บอกให้ตั้งก่อนส่ง Omise
            baseUrlFromSettings = SystemBaseUrl.IsConfigured(null, await SystemBaseUrl.ResolveAsync(_db, _config, ct)),
        }));
    }

    public sealed record SetRequest(bool Enabled);

    [HttpPut]
    [Accounting.Filters.RejectApiKey("เปิด/ปิดหน้าซื้อแพ็กเกจสาธารณะ")]
    public async Task<ActionResult<ApiResponse<object>>> Set([FromBody] SetRequest req, CancellationToken ct)
    {
        var s = await _db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (s == null) return BadRequest(new ApiResponse<object>(false, null, "ยังไม่มีค่าตั้งเว็บไซต์"));
        var before = s.PublicCheckoutEnabled;
        s.PublicCheckoutEnabled = req.Enabled;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = PdpaPolicy.PlatformScopeCompanyId, EntityType = nameof(SiteSettings), EntityId = s.Id.ToString(), Action = AuditAction.Update,
            UserEmail = User?.Identity?.Name,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "public-checkout-switch", from = before, to = req.Enabled, ruleCode = PublicCheckoutPolicy.RuleCode }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        var avail = await _checkout.AvailabilityAsync(ct);
        return Ok(new ApiResponse<object>(true, new { enabled = req.Enabled, onlineAvailable = avail.OnlineAvailable, isTestMode = avail.IsTestMode, reason = avail.Reason },
            !req.Enabled ? "ปิดหน้าซื้อแพ็กเกจสาธารณะแล้ว"
            : avail.IsTestMode ? "เปิดหน้าซื้อแพ็กเกจสาธารณะแล้ว — ช่องทางอยู่โหมดทดสอบ (ป้าย \"โหมดทดสอบ\" ทั้งหน้า · ไม่มีเงินจริง · ไม่เปิดสิทธิ์)"
            : avail.OnlineAvailable ? "เปิดหน้าซื้อแพ็กเกจสาธารณะแล้ว — รับเงินจริง"
            : $"เปิดหน้าแล้ว แต่ยังชำระออนไลน์ไม่ได้: {avail.Reason}"));
    }
}

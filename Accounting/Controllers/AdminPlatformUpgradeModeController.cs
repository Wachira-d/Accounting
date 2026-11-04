using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 237 (K-1 · คำตัดสินเจ้าของ 2026-11-04 "ทำให้ระบบรองรับทั้ง 2 ทาง สามารถเลือกได้" · DECISIONS 237-1): ค่าตั้งแพลตฟอร์ม "แบบอัปเกรดกลางรอบที่เปิดให้ลูกค้า"
/// (<c>SiteSettings.PlatformUpgradeModeOffer</c>) — CustomerChooses (ค่าตั้งต้น) / KeepExpiryOnly / RestartCycleOnly · แก้จากหน้า <c>/admin/site-settings.html</c>
/// แท็บ "แพ็กเกจบริการ" · audit chain ทุกครั้งที่เปลี่ยน · ป้ายมาจากเซิร์ฟเวอร์ (<see cref="PlatformProration.OfferLabel"/>) · มีผลกับ<b>ใบเสนอ/รายการใหม่</b>เท่านั้น
/// (รายการที่สร้างแล้วใช้แบบที่ประทับตอนซื้อ — 237-5) · แยก endpoint จาก <c>AdminController</c> (ไฟล์ใหญ่ · ทีมอื่นแก้พร้อมกัน)
/// </summary>
[ApiController]
[Route("api/admin/platform-upgrade-mode")]
[Authorize(Roles = "SystemAdmin")]
public class AdminPlatformUpgradeModeController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public AdminPlatformUpgradeModeController(AccountingDbContext db) => _db = db;

    private static object Shape(PlatformUpgradeModeOffer offer) => new
    {
        offer = offer.ToString(),
        offerLabel = PlatformProration.OfferLabel(offer),
        offeredModes = PlatformProration.OfferedModes(offer).Select(m => new { mode = m.ToString(), label = PlatformProration.ModeLabel(m) }).ToList(),
        choices = Enum.GetValues<PlatformUpgradeModeOffer>().Select(o => new { value = o.ToString(), label = PlatformProration.OfferLabel(o) }).ToList(),
    };

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(CancellationToken ct)
    {
        var offer = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => (PlatformUpgradeModeOffer?)s.PlatformUpgradeModeOffer).FirstOrDefaultAsync(ct)
                    ?? PlatformUpgradeModeOffer.CustomerChooses;
        return Ok(new ApiResponse<object>(true, Shape(offer)));
    }

    public sealed record SetRequest(PlatformUpgradeModeOffer Offer);

    [HttpPut]
    [Accounting.Filters.RejectApiKey("ตั้งแบบอัปเกรดแพ็กเกจกลางรอบ")]
    public async Task<ActionResult<ApiResponse<object>>> Set([FromBody] SetRequest req, CancellationToken ct)
    {
        if (!Enum.IsDefined(req.Offer)) return BadRequest(new ApiResponse<object>(false, null, "ค่าตั้งไม่ถูกต้อง"));
        var s = await _db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (s == null) return BadRequest(new ApiResponse<object>(false, null, "ยังไม่มีค่าตั้งเว็บไซต์"));
        var before = s.PlatformUpgradeModeOffer;
        s.PlatformUpgradeModeOffer = req.Offer;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = PdpaPolicy.PlatformScopeCompanyId, EntityType = nameof(SiteSettings), EntityId = s.Id.ToString(), Action = AuditAction.Update,
            UserEmail = User?.Identity?.Name,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "platform-upgrade-mode", from = before.ToString(), to = req.Offer.ToString(),
                ruleCode = PlatformProration.UpgradeModeRuleCode }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, Shape(req.Offer),
            $"บันทึกแล้ว — {PlatformProration.OfferLabel(req.Offer)} · มีผลกับใบเสนอ/รายการใหม่ (รายการที่สร้างแล้วใช้แบบที่ลูกค้าเลือกตอนซื้อ)"));
    }
}

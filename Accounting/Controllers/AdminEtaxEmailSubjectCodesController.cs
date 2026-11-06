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
/// รอบ 250 WP-03 (คำตัดสิน AUDIT_249 §3.3 N1): ตาราง "ชนิดเอกสาร → รหัสหัวเรื่อง e-Tax by Email" เป็นค่าตั้งแพลตฟอร์มที่แอดมินแก้ได้
/// (<c>SiteSettings.EtaxByEmailSubjectCodesJson</c>) — ค่าตั้งต้น = ค่าที่ใช้อยู่ (INV/RCT/DBN/CDN · ยังไม่เทียบคู่มือฉบับจริง) · รหัสผิด ⇒ ETDA ไม่ประทับเวลาทั้งชุด
/// จึงต้องแก้ได้ภายใน 1 นาทีโดยไม่ deploy · ตัวตรวจ/ตัวอ่านเดียว <see cref="EtaxByEmailFormat"/> · audit chain ทุกครั้งที่เปลี่ยน ·
/// แยก endpoint จาก <c>AdminController</c> (แบบ <c>AdminPlatformUpgradeModeController</c>)
/// </summary>
[ApiController]
[Route("api/admin/etax-email-subject-codes")]
[Authorize(Roles = "SystemAdmin")]
public class AdminEtaxEmailSubjectCodesController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public AdminEtaxEmailSubjectCodesController(AccountingDbContext db) => _db = db;

    private static object Shape(string? json) => new
    {
        slots = EtaxByEmailFormat.EffectiveSubjectCodes(json)
            .Select(s => new { key = s.Key, label = s.Label, code = s.Code, defaultCode = s.Default, isOverridden = s.Code != s.Default }).ToList(),
        unverifiedNote = EtaxByEmailFormat.SubjectCodesUnverifiedNote,
    };

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(CancellationToken ct)
    {
        var json = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.EtaxByEmailSubjectCodesJson).FirstOrDefaultAsync(ct);
        return Ok(new ApiResponse<object>(true, Shape(json)));
    }

    /// <summary>ค่าต่อช่อง (<c>Invoice</c>/<c>Receipt</c>/<c>DebitNote</c>/<c>CreditNote</c>) · ว่าง = กลับไปค่าตั้งต้น</summary>
    public sealed record SetRequest(Dictionary<string, string?>? Codes);

    [HttpPut]
    [Accounting.Filters.RejectApiKey("ตั้งรหัสหัวเรื่อง e-Tax by Email")]
    public async Task<ActionResult<ApiResponse<object>>> Set([FromBody] SetRequest req, CancellationToken ct)
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var (key, raw) in req.Codes ?? new Dictionary<string, string?>())
        {
            if (!EtaxByEmailFormat.SubjectCodeSlots.Any(s => s.Key == key)) { errors.Add($"ไม่รู้จักช่อง “{key}”"); continue; }
            var code = raw?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code)) continue;   // ว่าง = ค่าตั้งต้น
            if (!EtaxByEmailFormat.IsValidSubjectCode(code)) { errors.Add($"รหัสของ “{key}” ต้องเป็นตัวอักษรอังกฤษ 3 ตัว (เช่น INV · CDN · CRN) — ได้ “{raw}”"); continue; }
            codes[key] = code;
        }
        // ห้ามบันทึกบางส่วนเงียบ ๆ — ช่องใดผิด ปฏิเสธทั้งคำขอพร้อมเหตุ
        if (errors.Count > 0) return BadRequest(new ApiResponse<object>(false, null, string.Join(" · ", errors)));

        var s = await _db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (s == null) return BadRequest(new ApiResponse<object>(false, null, "ยังไม่มีค่าตั้งเว็บไซต์"));
        var before = s.EtaxByEmailSubjectCodesJson;
        var after = EtaxByEmailFormat.SerializeSubjectCodes(codes);
        s.EtaxByEmailSubjectCodesJson = after;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = PdpaPolicy.PlatformScopeCompanyId, EntityType = nameof(SiteSettings), EntityId = s.Id.ToString(), Action = AuditAction.Update,
            UserEmail = User?.Identity?.Name,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "etax-email-subject-codes", from = before, to = after,
                ruleCode = EtaxByEmailFormat.RuleCode }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, Shape(after),
            after == null ? "บันทึกแล้ว — ใช้รหัสตั้งต้นทั้งชุด" : "บันทึกแล้ว — มีผลกับอีเมล e-Tax ฉบับถัดไปทันที"));
    }
}

using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 212 แพ็ก A (คำตัดสินข้อ 171) — ค่าตั้ง "การแจ้งเตือนลูกค้า": ตาราง ประเภทข้อความ × LINE/อีเมล (ระดับบริษัท หรือ override ต่อเว็บ) +
/// ถอยไปอีเมลของระบบ (<c>CompanySettings.CustomerEmailFallback</c>) · <b>เซิร์ฟเวอร์ส่งแคตตาล็อก + ป้าย + ค่าที่ resolve แล้ว</b> — หน้าเว็บแสดงอย่างเดียว (F2 #5) ·
/// ผู้อ่านค่าตั้งเชิงธุรกิจ: <c>BookingNotifier.SendAsync</c> → <see cref="CustomerNotifyPolicy.Resolve"/> / <see cref="CustomerNotifyChannel.EmailPlan"/>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/customer-notify")]
[Authorize]
public class CustomerNotifyController : ControllerBase
{
    private readonly AccountingDbContext _db;

    public CustomerNotifyController(AccountingDbContext db) => _db = db;

    public record RuleInput(string EventKey, bool? LineEnabled, bool? EmailEnabled);
    public record SaveRulesRequest(Guid? SiteId, List<RuleInput>? Rules);
    public record SaveFallbackRequest(string? Value);

    private static readonly IReadOnlyDictionary<string, string> LayerLabels = new Dictionary<string, string>
    {
        [CustomerNotifyPolicy.LayerDefault] = "ค่าเริ่มต้น (ส่ง)",
        [CustomerNotifyPolicy.LayerCompany] = "ตั้งเอง (บริษัท)",
        [CustomerNotifyPolicy.LayerCompanyAll] = "ตามแถว \"ทั้งหมด\" ของบริษัท",
        [CustomerNotifyPolicy.LayerSite] = "ตั้งเอง (เว็บนี้)",
        [CustomerNotifyPolicy.LayerSiteAll] = "ตามแถว \"ทั้งหมด\" ของเว็บนี้",
    };

    [HttpGet("rules")]
    public async Task<ActionResult<ApiResponse<object>>> GetRules(Guid companyId, [FromQuery] Guid? siteId, CancellationToken ct)
    {
        string? siteName = null;
        if (siteId is Guid sid)
        {
            siteName = await _db.Sites.AsNoTracking().Where(s => s.Id == sid && s.CompanyId == companyId).Select(s => s.Name).FirstOrDefaultAsync(ct);
            if (siteName == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเว็บไซต์นี้ในบริษัท"));
        }
        return Ok(new ApiResponse<object>(true, await BuildAsync(companyId, siteId, siteName, ct)));
    }

    // ค่าตั้งของบริษัท = ด่านเดียวกับ PUT line-config/email-config (CompanySettingsEdit)
    [HttpPut("rules")]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ตั้งค่าการแจ้งเตือนลูกค้า")]
    public async Task<ActionResult<ApiResponse<object>>> SaveRules(Guid companyId, [FromBody] SaveRulesRequest req, CancellationToken ct)
    {
        var inputs = req.Rules ?? new List<RuleInput>();
        var rows = inputs.Select(r => new CustomerNotifyRuleRow(req.SiteId, (r.EventKey ?? "").Trim(), r.LineEnabled, r.EmailEnabled)).ToList();
        var errs = CustomerNotifyPolicy.Validate(rows);
        if (errs.Count > 0) return BadRequest(new ApiResponse<object>(false, null, string.Join(" · ", errs)));
        string? siteName = null;
        if (req.SiteId is Guid sid)
        {
            siteName = await _db.Sites.AsNoTracking().Where(s => s.Id == sid && s.CompanyId == companyId).Select(s => s.Name).FirstOrDefaultAsync(ct);
            if (siteName == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเว็บไซต์นี้ในบริษัท"));
        }

        // แทนที่ทั้งชุดของชั้นนี้ (บริษัท หรือ เว็บนั้น) · แถวที่ทั้งสองช่อง null = "ตามชั้นถัดไป" ⇒ ไม่เก็บแถว
        var existing = await _db.CustomerNotifyRules
            .Where(r => r.CompanyId == companyId && r.SiteId == req.SiteId)
            .ToListAsync(ct);
        var actor = User.Identity?.Name ?? "unknown";
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            var cur = existing.FirstOrDefault(e => e.EventKey == row.EventKey);
            if (row.LineEnabled == null && row.EmailEnabled == null)
            {
                if (cur != null) _db.CustomerNotifyRules.Remove(cur);
                continue;
            }
            if (cur == null)
            {
                _db.CustomerNotifyRules.Add(new CustomerNotifyRule
                {
                    CompanyId = companyId, SiteId = req.SiteId, EventKey = row.EventKey,
                    LineEnabled = row.LineEnabled, EmailEnabled = row.EmailEnabled, CreatedBy = actor,
                });
            }
            else
            {
                cur.LineEnabled = row.LineEnabled;
                cur.EmailEnabled = row.EmailEnabled;
                cur.UpdatedAt = now;
                cur.UpdatedBy = actor;
            }
        }
        // คีย์ที่ไม่ได้ส่งมา = ไม่แตะ (หน้าเว็บส่งทั้งตาราง · API ส่งบางแถวได้)
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, await BuildAsync(companyId, req.SiteId, siteName, ct), "บันทึกการตั้งค่าการแจ้งเตือนลูกค้าแล้ว"));
    }

    /// <summary>ถอยไปอีเมลของระบบ — แยกจาก PUT email-config (ซึ่งล้าง "ทดสอบผ่าน" ทุกครั้งที่บันทึก) · echo กลับใน GET rules + GET email-config</summary>
    [HttpPut("fallback")]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ตั้งค่าการแจ้งเตือนลูกค้า")]
    public async Task<ActionResult<ApiResponse<object>>> SaveFallback(Guid companyId, [FromBody] SaveFallbackRequest req, CancellationToken ct)
    {
        if (!CustomerNotifyChannel.IsValidFallback(req.Value))
            return BadRequest(new ApiResponse<object>(false, null, "ค่าการถอยไปอีเมลของระบบไม่ถูกต้อง"));
        var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId, ct);
        if (s == null)
        {
            s = await CompanySettingsFactory.AddNewAsync(_db, companyId);
        }
        s.CustomerEmailFallback = string.IsNullOrWhiteSpace(req.Value) ? null : req.Value.Trim();
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, await BuildAsync(companyId, null, null, ct), "บันทึกการตั้งค่าอีเมลถึงลูกค้าแล้ว"));
    }

    private async Task<object> BuildAsync(Guid companyId, Guid? siteId, string? siteName, CancellationToken ct)
    {
        var rules = await _db.CustomerNotifyRules.AsNoTracking()
            .Where(r => r.CompanyId == companyId && (r.SiteId == null || r.SiteId == siteId))
            .Select(r => new CustomerNotifyRuleRow(r.SiteId, r.EventKey, r.LineEnabled, r.EmailEnabled))
            .ToListAsync(ct);
        var settings = await _db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new { x.CustomerEmailFallback, x.EmailConfigured }).FirstOrDefaultAsync(ct);

        object Row(string key, string label, string? group, bool lineCapable, string? ownerSwitch, string? ownerSwitchUrl)
        {
            var own = rules.FirstOrDefault(r => r.SiteId == siteId && r.EventKey == key);
            // แถว "ทั้งหมด" resolve ด้วยตัวเองไม่ได้ (ไม่มีชั้นคีย์) ⇒ แสดงค่าที่ชั้นนี้/ชั้นบนตั้งไว้
            var eff = CustomerNotifyPolicy.Resolve(rules, siteId, key == CustomerNotifyEvents.All ? "__all__" : key);
            return new
            {
                key, label, group, lineCapable, ownerSwitch, ownerSwitchUrl,
                lineEnabled = own?.LineEnabled, emailEnabled = own?.EmailEnabled,
                effectiveLine = eff.LineOn, effectiveEmail = eff.EmailOn,
                lineLayer = LayerLabels.GetValueOrDefault(eff.LineSource, eff.LineSource),
                emailLayer = LayerLabels.GetValueOrDefault(eff.EmailSource, eff.EmailSource),
            };
        }

        return new
        {
            scope = siteId == null ? "company" : "site",
            siteId, siteName,
            all = Row(CustomerNotifyEvents.All, "ทั้งหมด (ทุกประเภทที่ไม่ได้ตั้งเฉพาะ)", null, true, null, null),
            events = CustomerNotifyEvents.Catalog.Select(e => Row(e.Key, e.Label, e.Group, e.LineCapable, e.OwnerSwitch, e.OwnerSwitchUrl)).ToList(),
            fallback = new
            {
                value = settings?.CustomerEmailFallback,
                effective = CustomerNotifyChannel.EffectiveFallback(settings?.CustomerEmailFallback),
                tenantEmailConfigured = settings?.EmailConfigured ?? false,
                options = CustomerNotifyChannel.FallbackOptions,
            },
            note = "ไม่ได้ตั้ง = ส่งตามเดิม · สวิตช์ \"ส่งไหม/เมื่อไร\" ของแต่ละประเภท (เช่น เตือนก่อนนัด · บัตรใกล้หมดอายุ · ตอบกลับฟอร์ม) ยังอยู่ที่เดิม — ตารางนี้เลือกเฉพาะช่องทาง",
        };
    }
}

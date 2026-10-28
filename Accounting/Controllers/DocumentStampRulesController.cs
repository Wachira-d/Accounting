using System.Security.Claims;
using Accounting.Data;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>กฎตราประทับ/ลายเซ็นอิเล็กทรอนิกส์ต่อประเภทเอกสาร × สถานะ (Document Stamp Rules · ทีม J 2026-10-22)
///
/// <para>เก็บใน <c>CompanySettings.DocumentStampRulesJson</c> · ตรวจ/จัดรูปผ่าน <see cref="DocumentStampPolicy"/> ตัวเดียว ·
/// GET ส่ง "ค่าที่มีผลจริง" ต่อประเภท (เซิร์ฟเวอร์คำนวณ · หน้าแสดงอย่างเดียว) · ประเภทที่ไม่อยู่ใน PUT = ล้างกลับเป็นค่าตั้งต้น (พฤติกรรมเดิม)</para>
/// <para>แยก controller จาก <c>SettingsController</c> เพื่อไม่แตะ DTO ตั้งค่าก้อนใหญ่ (positional record ที่หลายทีมแก้)</para></summary>
[ApiController]
[Route("api/companies/{companyId:guid}/settings/document-stamp-rules")]
[Authorize]
public class DocumentStampRulesController : ControllerBase
{
    private readonly AccountingDbContext _db;

    public DocumentStampRulesController(AccountingDbContext db) => _db = db;

    public record StageOption(string Key, string Label);
    /// <summary>ตัวเลือกผู้ลงนาม = สมาชิกทุกคนของบริษัท (W9 R-61 คำตัดสิน: ไม่กรอง — ใครลงนามเป็นเรื่องตั้งค่าของบริษัท) ·
    /// <paramref name="IsActive"/> = บัญชียังใช้งานได้ (<c>UserStatus.Active</c>) — หน้าแสดงบทบาท + สถานะข้างชื่อ · ผู้ที่ถูกถอดจากบริษัทไม่อยู่ในรายการ
    /// (และ renderer ไม่วาดลายเซ็น — <c>Helpers/DepartedSignerRule</c>)</summary>
    public record SignerOption(Guid UserId, string Name, string Role, bool IsActive);
    /// <summary>ค่าที่มีผลจริงของประเภทหนึ่ง (กฎที่ตั้ง หรือค่าตั้งต้น) — หน้าแสดงตรงตัว</summary>
    public record EffectiveRule(string Key, string Label, bool IsCustom,
        List<string> StampStages, List<string> SignatureStages, string Align,
        decimal OffsetXMm, decimal OffsetYMm, decimal WidthMm, decimal HeightMm,
        IReadOnlyList<string> SignerRoles, IReadOnlyList<Guid> SignerUserIds);
    /// <summary><paramref name="LastChangedAt"/>/<paramref name="LastChangedBy"/> = แถว audit ล่าสุดของกฎนี้ (W9 R-60) · null = ยังไม่เคยแก้ผ่านหน้านี้</summary>
    public record StampRulesResponse(List<EffectiveRule> Kinds, List<StageOption> Stages,
        List<string> Roles, List<SignerOption> Signers, bool HasStampImage, string Note,
        DateTime? LastChangedAt = null, string? LastChangedBy = null);
    public record UpdateStampRulesRequest(Dictionary<string, DocumentStampPolicy.Rule>? Rules);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<StampRulesResponse>>> Get(Guid companyId)
    {
        var s = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId);
        return Ok(new ApiResponse<StampRulesResponse>(true, await BuildAsync(companyId, s)));
    }

    [HttpPut]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("บันทึกกฎตราประทับ/ลายเซ็น")]
    public async Task<ActionResult<ApiResponse<StampRulesResponse>>> Update(Guid companyId, [FromBody] UpdateStampRulesRequest request)
    {
        var errors = DocumentStampPolicy.Validate(request.Rules);
        if (errors.Count > 0)
            return BadRequest(new ApiResponse<StampRulesResponse>(false, null, "กฎตราประทับไม่ถูกต้อง", errors));
        var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId);
        if (s == null)
            return BadRequest(new ApiResponse<StampRulesResponse>(false, null,
                "ยังไม่มีการตั้งค่าบริษัท — เปิดหน้า ตั้งค่า แล้วบันทึกข้อมูลบริษัทครั้งแรกก่อน"));
        // ผู้ลงนามที่ระบุต้องเป็นสมาชิกของบริษัทนี้ (กันใส่ id ของคนนอก)
        var members = await MemberIdsAsync(companyId);
        var foreign = (request.Rules ?? new()).Values
            .SelectMany(r => r?.SignerUserIds ?? new()).Where(id => !members.Contains(id)).Distinct().Count();
        if (foreign > 0)
            return BadRequest(new ApiResponse<StampRulesResponse>(false, null,
                $"ผู้ลงนาม {foreign} คนไม่ใช่สมาชิกของบริษัทนี้"));
        var before = s.DocumentStampRulesJson;
        var after = DocumentStampPolicy.Serialize(request.Rules);
        s.DocumentStampRulesJson = after;
        // W9 R-60: ค่าตั้งที่เปลี่ยนหน้าตาเอกสารกฎหมาย (50 ทวิ/ใบกำกับ) ต้องมีแถว audit append-only (กฎ M) ในธุรกรรมเดียวกับ Save ·
        // before/after = JSON ทั้งก้อน (ไม่ diff เอง) · EntityType ค่าเดียวกับฝั่งอ่าน "แก้ล่าสุด" ใน BuildAsync
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            Guid? actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = actorId,
                UserEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name,
                Action = AuditAction.Update,
                EntityType = DocumentStampPolicy.AuditEntityType,
                EntityId = s.Id.ToString(),
                OldValues = before,
                NewValues = after,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Timestamp = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<StampRulesResponse>(true, await BuildAsync(companyId, s), "บันทึกกฎตราประทับ/ลายเซ็นแล้ว"));
    }

    private async Task<HashSet<Guid>> MemberIdsAsync(Guid companyId)
        => (await _db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId).Select(cu => cu.UserId).ToListAsync()).ToHashSet();

    private async Task<StampRulesResponse> BuildAsync(Guid companyId, CompanySettings? s)
    {
        var rules = DocumentStampPolicy.Parse(s?.DocumentStampRulesJson);
        var legacy = new DocumentStampPolicy.Legacy(s?.StampWidthMm ?? 32m, s?.StampHeightMm ?? 32m, s?.StampAlign);
        var stages = Enum.GetValues<DocumentStampPolicy.Stage>();
        var kinds = new List<EffectiveRule>();
        foreach (var (key, label) in DocumentStampPolicy.Kinds)
        {
            var per = stages.Select(st => DocumentStampPolicy.Resolve(key, st, rules, legacy)).ToList();
            var d = per[0];
            kinds.Add(new EffectiveRule(key, label, d.IsCustom,
                per.Where(x => x.ShowStamp).Select(x => x.Stage.ToString()).ToList(),
                per.Where(x => x.ShowSignature).Select(x => x.Stage.ToString()).ToList(),
                d.Align, d.OffsetXMm, d.OffsetYMm, d.WidthMm, d.HeightMm, d.SignerRoles, d.SignerUserIds));
        }
        var signers = await (from cu in _db.Set<CompanyUser>().AsNoTracking()
                             join u in _db.Users.AsNoTracking() on cu.UserId equals u.Id
                             where cu.CompanyId == companyId
                             orderby u.FullName
                             select new { u.Id, u.FullName, u.SignatureName, cu.Role, u.Status }).ToListAsync();
        // W9 R-60: แก้ล่าสุดโดย/เมื่อ — อ่านจาก audit chain (ตัวเดียวกับที่ Update เขียน) ไม่เก็บซ้ำบน CompanySettings
        var lastAudit = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.EntityType == DocumentStampPolicy.AuditEntityType)
            .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
            .Select(a => new { a.Timestamp, a.UserEmail })
            .FirstOrDefaultAsync();
        return new StampRulesResponse(kinds,
            stages.Select(st => new StageOption(st.ToString(), DocumentStampPolicy.StageLabel(st))).ToList(),
            Enum.GetNames<Models.Enums.UserRole>().Where(n => n != nameof(Models.Enums.UserRole.SystemAdmin)).ToList(),
            signers.Select(x => new SignerOption(x.Id,
                string.IsNullOrWhiteSpace(x.SignatureName) ? x.FullName : x.SignatureName!, x.Role.ToString(),
                IsActive: x.Status == UserStatus.Active)).ToList(),
            HasStampImage: !string.IsNullOrWhiteSpace(s?.StampPath) || !string.IsNullOrWhiteSpace(s?.StampUrl),
            LastChangedAt: lastAudit?.Timestamp, LastChangedBy: lastAudit?.UserEmail,
            Note: "ยังไม่ตั้ง = ค่าเดิมของระบบ: ประทับตราเฉพาะเอกสารที่อนุมัติแล้ว/ชำระแล้ว/ยกเลิก (ร่าง·รออนุมัติไม่ประทับ) · "
                + "ลายเซ็นทุกสถานะ · 50 ทวิ ตราวางทับวงกลม \"ประทับตรานิติบุคคล\" ของแบบ (ใช้ระยะเลื่อน ไม่ใช้ซ้าย/กลาง/ขวา)");
    }
}

using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Settings;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// ประเภทเงินมัดจำต่อบริษัท (รอบ 194 ทีม C · spec S1/S2/S8) — ตัวตัดสินทั้งหมดอยู่ที่ <see cref="DepositPolicyResolver"/>
/// (ด่านบันทึก <c>KindProblem</c> · คำเตือน <c>KindWarning</c> · โหมดที่ใช้จริง <c>ResolveKind</c>) ผ่าน <see cref="DepositKindCatalog"/> ·
/// service นี้ทำแค่ tenant · รหัสไม่ซ้ำ · บัญชีมีจริง · ประเภทเริ่มต้นตัวเดียว · ลบ/ปิดใช้
///
/// <para>⚠️ ใบที่ออกแล้วตรึงสำเนาประเภทลงใบ (<c>Document.DepositKindId/DepositNature/DepositKindName</c>) — แก้/ปิด/ลบประเภททีหลัง
/// <b>ไม่</b>เปลี่ยนใบเดิม (§86/4)</para>
/// </summary>
public class DepositKindService : IDepositKindService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<DepositKindService> _logger;

    public DepositKindService(AccountingDbContext db, ILogger<DepositKindService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<DepositKindListResponse> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        await EnsureSeededForLegacyCompanyAsync(companyId, ct);
        var ctx = await DepositKindCatalog.LoadContextAsync(_db, companyId, ct);
        var rows = await _db.DepositKinds.AsNoTracking()
            .Where(k => k.CompanyId == companyId)
            .OrderBy(k => k.SortOrder).ThenBy(k => k.Code)
            .ToListAsync(ct);
        var company = DepositPolicyResolver.Resolve(ctx.Supply, ctx.CompanySetting);
        return new DepositKindListResponse(
            rows.Select(k => Map(ctx, k)).ToList(),
            ctx.DefaultKind?.Id,
            DepositKindCatalog.NatureOptions,
            DepositPolicyResolver.Options,
            DepositKindCatalog.Matrix(ctx),
            new DepositCompanyTreatmentInfo(ctx.CompanySetting, company.Treatment, company.Label, company.Source, company.NeedsOwnerChoice),
            ctx.Supply);
    }

    public async Task<DepositKindSaveResponse> CreateAsync(Guid companyId, SaveDepositKindRequest request, string userId, CancellationToken ct = default)
    {
        await EnsureSeededForLegacyCompanyAsync(companyId, ct);
        var codeProblem = DepositKindCatalog.CodeProblem(request.Code);
        if (codeProblem != null) throw new BusinessRuleException(codeProblem);
        if (request.Nature is null)
            throw new BusinessRuleException("กรุณาเลือกลักษณะเงิน — ส่วนหนึ่งของราคา · เงินประกันที่ต้องคืน · หรือ นอกระบบ VAT");

        var maxSort = await _db.DepositKinds.Where(k => k.CompanyId == companyId)
            .Select(k => (int?)k.SortOrder).MaxAsync(ct) ?? 0;
        var kind = new DepositKind
        {
            CompanyId = companyId,
            Code = DepositKindCatalog.NormalizeCode(request.Code),
            IsActive = request.IsActive ?? true,
            SortOrder = request.SortOrder ?? maxSort + 10,
            CreatedBy = userId,
        };
        await ApplyAsync(companyId, kind, request, ct);
        _db.DepositKinds.Add(kind);
        await SaveUniqueAsync(kind.Code, ct);
        return await SavedAsync(companyId, kind.Id, ct);
    }

    public async Task<DepositKindSaveResponse> UpdateAsync(Guid companyId, Guid id, SaveDepositKindRequest request, string userId, CancellationToken ct = default)
    {
        var kind = await FindAsync(companyId, id, ct);
        // รหัสว่าง = คงเดิม (ช่องรหัสของแถวที่ระบบสร้างถูกล็อกบนหน้า) · ส่งมาแล้วต่าง = เปลี่ยน — ยกเว้นแถว seed (คู่ค้าอ้างรหัสนี้อยู่)
        if (!string.IsNullOrWhiteSpace(request.Code))
        {
            var code = DepositKindCatalog.NormalizeCode(request.Code);
            if (code != kind.Code)
            {
                if (kind.SeedKey != null)
                    throw new BusinessRuleException($"รหัส “{kind.Code}” เป็นประเภทที่ระบบสร้าง เปลี่ยนรหัสไม่ได้ (ระบบภายนอกอ้างรหัสนี้) — "
                        + "แก้ชื่อ/ลักษณะ/วิธีบันทึกได้ตามปกติ หรือสร้างประเภทใหม่ด้วยรหัสที่ต้องการ");
                if (DepositKindCatalog.CodeProblem(code) is string codeProblem) throw new BusinessRuleException(codeProblem);
                kind.Code = code;
            }
        }
        if (request.Nature is null)
            throw new BusinessRuleException("กรุณาเลือกลักษณะเงิน — ส่วนหนึ่งของราคา · เงินประกันที่ต้องคืน · หรือ นอกระบบ VAT");
        await ApplyAsync(companyId, kind, request, ct);
        if (request.IsActive is bool active)
        {
            kind.IsActive = active;
            if (!active) kind.IsDefault = false;   // ประเภทเริ่มต้นที่ปิดใช้ = ตัวตัดสินข้ามอยู่แล้ว · ล้างธงให้หน้าจอไม่หลอก
        }
        if (request.SortOrder is int sort) kind.SortOrder = sort;
        kind.UpdatedAt = DateTime.UtcNow;
        kind.UpdatedBy = userId;
        await SaveUniqueAsync(kind.Code, ct);
        return await SavedAsync(companyId, kind.Id, ct);
    }

    public async Task<DepositKindDeleteResponse> DeleteAsync(Guid companyId, Guid id, string userId, CancellationToken ct = default)
    {
        var kind = await FindAsync(companyId, id, ct);
        // ใบที่ลบแบบ soft ก็นับ (ต้องเก็บ 5 ปี · อ้างประเภทได้) — IgnoreQueryFilters + กรองบริษัทเอง
        var docCount = await _db.Documents.IgnoreQueryFilters()
            .CountAsync(d => d.CompanyId == companyId && d.DepositKindId == id, ct);
        var lodgingCount = await _db.LodgingProperties
            .CountAsync(p => p.CompanyId == companyId && (p.RoomDepositKindId == id || p.SecurityDepositKindId == id), ct);
        var wasDefault = kind.IsDefault;
        kind.IsDefault = false;
        kind.UpdatedAt = DateTime.UtcNow;
        kind.UpdatedBy = userId;
        var tail = wasDefault ? " · บริษัทไม่มีประเภทเริ่มต้นแล้ว — ใบใหม่ใช้วิธีบันทึกตามค่าตั้งต้นบริษัท (ตั้งค่า → ภาษี)" : "";
        if (docCount > 0 || lodgingCount > 0)
        {
            kind.IsActive = false;
            await _db.SaveChangesAsync(ct);
            var uses = string.Join(" · ", new[]
            {
                docCount > 0 ? $"ใบมัดจำ {docCount:N0} ใบ" : null,
                lodgingCount > 0 ? $"ที่พัก {lodgingCount:N0} แห่ง" : null,
            }.Where(s => s != null));
            return new DepositKindDeleteResponse(id, Deleted: false, Deactivated: true,
                $"ประเภท “{kind.Name}” ถูกใช้อยู่ ({uses}) จึงลบไม่ได้ — ระบบปิดใช้แทน (ใบเดิมยังอ้างได้ครบ · เปิดใช้อีกครั้งได้ทุกเมื่อ){tail}");
        }
        kind.IsActive = false;
        kind.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
        return new DepositKindDeleteResponse(id, Deleted: true, Deactivated: false, $"ลบประเภท “{kind.Name}” แล้ว{tail}");
    }

    public async Task<DepositKindSaveResponse> SetDefaultAsync(Guid companyId, Guid id, string userId, CancellationToken ct = default)
    {
        var kind = await FindAsync(companyId, id, ct);
        if (!kind.IsActive)
            throw new BusinessRuleException($"ประเภท “{kind.Name}” ปิดใช้อยู่ — เปิดใช้ก่อนแล้วจึงตั้งเป็นประเภทเริ่มต้น");
        if (!kind.IsDefault)
        {
            // unique index "ประเภทเริ่มต้นตัวเดียวต่อบริษัท" ตรวจทีละคำสั่ง ⇒ ล้างตัวเดิมก่อน แล้วค่อยตั้งตัวใหม่ ในธุรกรรมเดียว
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.DepositKinds
                .Where(k => k.CompanyId == companyId && k.IsDefault && k.Id != id)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.IsDefault, false)
                    .SetProperty(k => k.UpdatedAt, DateTime.UtcNow)
                    .SetProperty(k => k.UpdatedBy, userId), ct);
            kind.IsDefault = true;
            kind.UpdatedAt = DateTime.UtcNow;
            kind.UpdatedBy = userId;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await SavedAsync(companyId, kind.Id, ct);
    }

    // ───────────────────────────── ภายใน ─────────────────────────────

    private async Task<DepositKind> FindAsync(Guid companyId, Guid id, CancellationToken ct)
        => await _db.DepositKinds.FirstOrDefaultAsync(k => k.Id == id && k.CompanyId == companyId, ct)
           ?? throw new BusinessRuleException("ไม่พบประเภทเงินมัดจำนี้ในบริษัท", statusCode: 404);

    /// <summary>ตรวจ + เขียนช่องที่แก้ได้ (ชื่อ · ลักษณะ · โหมด · บัญชี · เหตุผล · คำอธิบาย) — ด่านตัวเดียว
    /// <see cref="DepositPolicyResolver.KindProblem"/> (ลักษณะ "ราคา" + เลื่อน VAT ต้องมีเหตุผล · ค่าขยะ enum ถูกปฏิเสธ)</summary>
    private async Task ApplyAsync(Guid companyId, DepositKind kind, SaveDepositKindRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new BusinessRuleException("กรุณาระบุชื่อประเภทเงินมัดจำ (ชื่อนี้พิมพ์ลงใบมัดจำ)");
        if (name.Length > DepositKindCatalog.NameMaxLength)
            throw new BusinessRuleException($"ชื่อประเภทเงินมัดจำยาวเกิน {DepositKindCatalog.NameMaxLength} ตัวอักษร");
        var nature = request.Nature!.Value;
        var reason = string.IsNullOrWhiteSpace(request.PolicyReason) ? null : request.PolicyReason.Trim();
        var problem = DepositPolicyResolver.KindProblem(nature, request.VatTreatment, reason);
        if (problem != null)
        {
            // รหัสกฎของคู่ลักษณะ×โหมดที่ถูกปฏิเสธ (log/audit อ้างมาตราได้) — ค่าขยะ enum ไม่มีรหัส
            var ruleCode = DepositPolicyResolver.IsDefined(nature) && DepositPolicyResolver.IsDefined(request.VatTreatment)
                ? DepositPolicyResolver.KindWarning(nature, request.VatTreatment!.Value).RuleCode : null;
            throw new BusinessRuleException(problem, ruleCode);
        }

        kind.Name = name;
        kind.Nature = nature;
        kind.VatTreatment = request.VatTreatment;
        kind.PolicyReason = reason;
        kind.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        kind.LiabilityAccountCode = await AccountCodeOrNullAsync(companyId, request.LiabilityAccountCode, AccountType.Liability,
            "บัญชีหนี้สินพักเงินมัดจำ", ct);
        kind.ForfeitAccountCode = await AccountCodeOrNullAsync(companyId, request.ForfeitAccountCode, AccountType.Revenue,
            "บัญชีรายได้ตอนริบเป็นค่าเสียหาย", ct);
    }

    /// <summary>รหัสบัญชีที่ผู้ใช้ระบุต้องมีจริงในผังของบริษัท เปิดใช้ และเป็นหมวดที่ถูก (หนี้สิน/รายได้) — ว่าง = null (ค่าตามระบบ)</summary>
    private async Task<string?> AccountCodeOrNullAsync(Guid companyId, string? code, AccountType expected, string what, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        var acc = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.AccountCode == c)
            .Select(a => new { a.AccountType, a.IsActive })
            .FirstOrDefaultAsync(ct);
        if (acc == null) throw new BusinessRuleException($"{what}: ไม่พบรหัสบัญชี {c} ในผังบัญชีของบริษัท — เว้นว่างเพื่อใช้ค่าตามระบบ");
        if (!acc.IsActive) throw new BusinessRuleException($"{what}: บัญชี {c} ปิดใช้อยู่");
        if (acc.AccountType != expected)
            throw new BusinessRuleException($"{what}: บัญชี {c} ไม่ใช่หมวด{(expected == AccountType.Liability ? "หนี้สิน" : "รายได้")}");
        return c;
    }

    /// <summary>บันทึก — ชนรหัสซ้ำ (unique index ต่อบริษัท · สองคนบันทึกพร้อมกัน) ตอบข้อความไทยแทน 23505</summary>
    private async Task SaveUniqueAsync(string code, CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new BusinessRuleException($"รหัสประเภทเงินมัดจำ “{code}” มีอยู่แล้วในบริษัทนี้ — ใช้รหัสอื่น", ex);
        }
    }

    private async Task<DepositKindSaveResponse> SavedAsync(Guid companyId, Guid id, CancellationToken ct)
    {
        var ctx = await DepositKindCatalog.LoadContextAsync(_db, companyId, ct);
        var row = await _db.DepositKinds.AsNoTracking().FirstAsync(k => k.Id == id && k.CompanyId == companyId, ct);
        var item = Map(ctx, row);
        return new DepositKindSaveResponse(item, item.Warning, item.RuleCode);
    }

    private static DepositKindResponse Map(DepositKindCompanyContext ctx, DepositKind k)
    {
        var d = DepositKindCatalog.Preview(ctx, k);
        return new DepositKindResponse(
            k.Id, k.Code, k.Name, k.Nature, DepositKindCatalog.NatureLabelOf(k.Nature), k.VatTreatment,
            d.Treatment, d.Source, DepositPolicyResolver.LabelOf(d.Treatment), d.Warning, d.RuleCode, d.RequiresReason,
            k.IsDefault, k.IsActive, k.LiabilityAccountCode, d.LiabilityAccountCode, k.ForfeitAccountCode, k.PolicyReason,
            k.Description, k.SortOrder, IsSystem: k.SeedKey != null);
    }

    /// <summary>บริษัทที่เกิดก่อน migration รอบ 194 (หรือ migration ยังไม่รัน) ยังไม่มีแถวเลย ⇒ seed ตามประเภทธุรกิจก่อน
    /// (ตารางเดียว <see cref="DepositKindSeed"/>) · สองคำขอ seed พร้อมกัน ⇒ ตัวหลังชน unique (SeedKey) = อีกตัว seed ให้แล้ว</summary>
    private async Task EnsureSeededForLegacyCompanyAsync(Guid companyId, CancellationToken ct)
    {
        if (await _db.DepositKinds.IgnoreQueryFilters().AnyAsync(k => k.CompanyId == companyId, ct)) return;
        var added = await DepositKindSeed.EnsureSeededAsync(_db, companyId, ct);
        if (added == 0) return;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            _logger.LogInformation("ประเภทเงินมัดจำของบริษัท {Company} ถูก seed โดยคำขออื่นพร้อมกัน — ใช้ของที่มีอยู่", companyId);
        }
    }
}

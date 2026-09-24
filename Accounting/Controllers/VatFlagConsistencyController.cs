using Accounting.Data;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>ยอดเอกสารต่อชนิดในช่วงที่ธงขัดกัน</summary>
public record VatFlagDocTypeCount(string DocumentType, int Count, decimal VatAmount);

/// <summary>เอกสารหนึ่งใบที่ออกในช่วงที่ธงขัดกัน (ให้นักบัญชีไล่ตรวจ)</summary>
public record VatFlagConflictDocument(Guid DocumentId, string DocumentNumber, string DocumentType, string Status,
    DateTime DocumentDate, DateTime CreatedAt, decimal VatAmount, string? CreatedBy);

/// <summary>สถานะธง VAT สองตัวของบริษัทหนึ่ง + ผลกระทบ</summary>
/// <param name="Agreement">ชื่อ enum <see cref="VatFlagAgreement"/> (ส่งเป็นชื่อเสมอ — F2 ข้อ 5)</param>
/// <param name="EffectiveVatRegistered">ค่าที่เส้นเอกสาร/Integration ใช้อยู่วันนี้ (<see cref="CompanyVatStatus.IsRegistered"/>)
/// — POS/CMS/ที่พัก/ปฏิทินยื่นภาษีอ่าน <c>CompanyIsVatRegistered</c></param>
/// <param name="CountedSince">นับเอกสารตั้งแต่วันที่สร้างแถวค่าตั้ง (ระบบไม่มีประวัติการเปลี่ยนธง ⇒ ช่วงที่ขัดกันจริง
/// อาจสั้นกว่านี้) · <c>null</c> = ธงไม่ขัดกัน ไม่ได้นับ</param>
public record VatFlagConsistencyRow(
    Guid CompanyId, string CompanyName, string? TaxId,
    bool CompanyIsVatRegistered, decimal CompanyVatRate,
    bool? SettingsVatRegistered, decimal? SettingsDefaultVatRate,
    bool EffectiveVatRegistered, decimal EffectiveDefaultVatRate,
    string Agreement, string Explanation,
    DateTime? CountedSince, int DocumentsInWindow, decimal VatAmountInWindow,
    List<VatFlagDocTypeCount> ByType);

/// <summary>รายงานของบริษัทเดียว + รายการเอกสารให้ไล่ตรวจ</summary>
public record VatFlagConsistencyCompanyReport(VatFlagConsistencyRow Company,
    List<VatFlagConflictDocument> Documents, bool DocumentsTruncated, string Guidance);

/// <summary>รายงานทั้งแพลตฟอร์ม — เฉพาะบริษัทที่ธงขัดกัน</summary>
public record VatFlagConsistencyPlatformReport(int CompaniesChecked, int CompaniesWithoutSettingsRow,
    int ConflictCount, List<VatFlagConsistencyRow> Conflicts, string Guidance);

/// <summary>
/// **รายงาน "ธง VAT สองตัวขัดกัน" — อ่านอย่างเดียว ไม่แก้ข้อมูล** (รอบ 193 · ผลตรวจ S-01 · คำตัดสินเจ้าของข้อ 14
/// "ห้ามแก้ข้อมูลเก่าหลังบ้าน")
///
/// <para>ระบบเก็บสถานะจด VAT สองที่ (<c>Company.IsVatRegistered</c> · <c>CompanySettings.VatRegistered</c>) ที่ค่าเริ่มต้น
/// ตรงข้ามกัน · เส้นเอกสาร/Integration อ่านตัวหนึ่ง POS/CMS/ที่พัก/ปฏิทินยื่นภาษีอ่านอีกตัว ⇒ บริษัทที่สองธงขัดกัน
/// อาจออกใบกำกับเก็บ VAT ทั้งที่ทะเบียนบอกไม่จด (§90/2) หรือไม่ถูกเตือนให้ยื่น ภ.พ.30. <b>ยังไม่ได้เลือกว่าธงไหนถูก</b>
/// (คำถามเจ้าของ Q1) — รายงานนี้แค่ชี้ให้เจ้าของบริษัท/นักบัญชีตัดสินรายบริษัท + ไล่ใบที่ออกไปแล้ว</para>
///
/// <para>ด่าน: เส้นบริษัท = <c>CompanySettings.Edit</c> (เจ้าของผ่านอัตโนมัติ) · ทุก query กรอง <c>CompanyId</c> ·
/// เส้นแพลตฟอร์มอยู่ที่ <see cref="AdminVatFlagConsistencyController"/> (<c>SystemAdmin</c> เท่านั้น)</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/vat-flag-consistency")]
[Authorize]
[RequirePermission(PermissionKeys.CompanySettingsEdit)]
public class VatFlagConsistencyController : ControllerBase
{
    private const int MaxDocuments = 200;
    private readonly AccountingDbContext _db;

    public VatFlagConsistencyController(AccountingDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<ApiResponse<VatFlagConsistencyCompanyReport>>> Get(Guid companyId, CancellationToken ct = default)
    {
        var co = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.Id, c.Name, c.TaxId, c.IsVatRegistered, c.VatRate })
            .FirstOrDefaultAsync(ct);
        if (co == null)
            return NotFound(new ApiResponse<VatFlagConsistencyCompanyReport>(false, null, "ไม่พบบริษัท"));
        var s = await _db.CompanySettings.AsNoTracking()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted)
            .Select(x => new { x.VatRegistered, x.DefaultVatRate, x.CreatedAt })
            .FirstOrDefaultAsync(ct);

        var agreement = CompanyVatStatus.Compare(co.IsVatRegistered, s?.VatRegistered);
        var inConflict = IsConflict(agreement);
        var since = inConflict ? s?.CreatedAt : null;

        var byType = new List<VatFlagDocTypeCount>();
        var docs = new List<VatFlagConflictDocument>();
        var truncated = false;
        if (since is DateTime from)
        {
            var q = VatDocsQuery(_db).Where(d => d.CompanyId == companyId && d.CreatedAt >= from);
            byType = (await q
                    .GroupBy(d => d.DocumentType)
                    .Select(g => new { Type = g.Key, Count = g.Count(), Vat = g.Sum(x => x.VatAmount) })
                    .ToListAsync(ct))
                .Select(g => new VatFlagDocTypeCount(g.Type.ToString(), g.Count, g.Vat))
                .OrderBy(g => g.DocumentType)
                .ToList();
            var rows = await q.OrderBy(d => d.CreatedAt)
                .Take(MaxDocuments + 1)
                .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.Status, d.DocumentDate, d.CreatedAt, d.VatAmount, d.CreatedBy })
                .ToListAsync(ct);
            truncated = rows.Count > MaxDocuments;
            docs = rows.Take(MaxDocuments)
                .Select(d => new VatFlagConflictDocument(d.Id, d.DocumentNumber, d.DocumentType.ToString(),
                    d.Status.ToString(), d.DocumentDate, d.CreatedAt, d.VatAmount, d.CreatedBy))
                .ToList();
        }

        var row = BuildRow(co.Id, co.Name, co.TaxId, co.IsVatRegistered, co.VatRate,
            s?.VatRegistered, s?.DefaultVatRate, since, byType);
        return Ok(new ApiResponse<VatFlagConsistencyCompanyReport>(true,
            new VatFlagConsistencyCompanyReport(row, docs, truncated, Guidance)));
    }

    internal const string Guidance =
        "รายงานนี้อ่านอย่างเดียว — ระบบยังไม่ได้แก้ธงใด ๆ ให้. ให้เจ้าของบริษัท/นักบัญชียืนยันสถานะจด VAT ที่ถูกต้อง "
        + "(ดูใบ ภ.พ.20) แล้วแก้ที่หน้า 'ข้อมูลบริษัท' หรือ 'ตั้งค่า' ให้ตรงกัน · เอกสารในรายการที่เก็บ VAT ทั้งที่ยังไม่จด "
        + "ต้องออกใบลดหนี้/ใบแทนตามที่นักบัญชีแนะนำ (ห้ามแก้ใบที่ออกแล้วย้อนหลัง §86/4)";

    internal static bool IsConflict(VatFlagAgreement a)
        => a is VatFlagAgreement.CompanyNoSettingsYes or VatFlagAgreement.CompanyYesSettingsNo;

    /// <summary>เอกสารที่ "เราออก" และมี VAT — ชุดชนิดใบกำกับจาก <see cref="EtaxAutoIssueScope.EtaxTypes"/> ·
    /// ออกจริงแล้ว (<see cref="DocumentStatusRules.NotIssued"/>) · ใบลด/เพิ่มหนี้อาจเป็นฝั่งซื้อ ต้องดูรายใบ</summary>
    internal static IQueryable<Models.Entities.Document> VatDocsQuery(AccountingDbContext db)
        => db.Documents.AsNoTracking()
            .Where(d => EtaxAutoIssueScope.EtaxTypes.Contains(d.DocumentType)
                && !DocumentStatusRules.NotIssued.Contains(d.Status)
                && d.VatAmount > 0m);

    internal static VatFlagConsistencyRow BuildRow(Guid companyId, string name, string? taxId,
        bool companyFlag, decimal companyRate, bool? settingsFlag, decimal? settingsRate,
        DateTime? countedSince, List<VatFlagDocTypeCount> byType)
    {
        var agreement = CompanyVatStatus.Compare(companyFlag, settingsFlag);
        return new VatFlagConsistencyRow(companyId, name, taxId,
            companyFlag, companyRate, settingsFlag, settingsRate,
            CompanyVatStatus.IsRegistered(companyFlag, settingsFlag),
            CompanyVatStatus.DefaultRate(companyRate, settingsRate),
            agreement.ToString(), Explain(agreement),
            countedSince, byType.Sum(b => b.Count), byType.Sum(b => b.VatAmount), byType);
    }

    private static string Explain(VatFlagAgreement a) => a switch
    {
        VatFlagAgreement.Agree => "ธงทั้งสองตรงกัน",
        VatFlagAgreement.NoSettingsRow => "ยังไม่มีแถวค่าตั้ง — ทุกเส้นใช้ธงบริษัท (ไม่ขัดกัน)",
        VatFlagAgreement.CompanyNoSettingsYes =>
            "ข้อมูลบริษัทบอก \"ไม่จด VAT\" แต่ค่าตั้งบอก \"จด\" — เส้นเอกสาร/API ยอมออกใบกำกับเก็บ VAT (เสี่ยง §90/2) "
            + "ขณะที่ POS/เว็บ/ที่พัก/ปฏิทินยื่นภาษีถือว่าไม่จด (ไม่เตือนให้ยื่น ภ.พ.30)",
        VatFlagAgreement.CompanyYesSettingsNo =>
            "ข้อมูลบริษัทบอก \"จด VAT\" แต่ค่าตั้งบอก \"ไม่จด\" — เส้นเอกสารบล็อกใบกำกับ/ไม่เคลมภาษีซื้อ "
            + "ขณะที่ POS/เว็บ/ที่พักคิด VAT",
        _ => "ไม่ทราบ",
    };
}

/// <summary>ฝั่งแอดมินแพลตฟอร์มของ <see cref="VatFlagConsistencyController"/> — ข้ามบริษัทโดยเจตนา (ด่าน SystemAdmin) ·
/// อ่านอย่างเดียว · คืนเฉพาะบริษัทที่ธงขัดกัน (ไม่มีรายการเอกสาร — ดูรายบริษัทที่เส้นของบริษัทนั้น)</summary>
[ApiController]
[Route("api/admin/vat-flag-consistency")]
[Authorize(Roles = "SystemAdmin")]
public class AdminVatFlagConsistencyController : ControllerBase
{
    private sealed record VatFlagCompanyTypeCount(Guid CompanyId, Models.Enums.DocumentType Type, int Count, decimal Vat);

    private readonly AccountingDbContext _db;

    public AdminVatFlagConsistencyController(AccountingDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<ApiResponse<VatFlagConsistencyPlatformReport>>> Get(CancellationToken ct = default)
    {
        var companies = await _db.Companies.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.TaxId, c.IsVatRegistered, c.VatRate })
            .ToListAsync(ct);
        var settings = (await _db.CompanySettings.AsNoTracking()
                .Where(s => !s.IsDeleted)
                .Select(s => new { s.CompanyId, s.VatRegistered, s.DefaultVatRate, s.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(s => s.CompanyId)
            .ToDictionary(g => g.Key, g => g.First());

        var conflicts = companies
            .Select(c => new { c, s = settings.GetValueOrDefault(c.Id) })
            .Where(x => VatFlagConsistencyController.IsConflict(
                CompanyVatStatus.Compare(x.c.IsVatRegistered, x.s?.VatRegistered)))
            .ToList();
        var conflictIds = conflicts.Select(x => x.c.Id).ToList();

        // นับเอกสารต่อบริษัท×ชนิด ตั้งแต่วันที่สร้างแถวค่าตั้งของบริษัทนั้น (join แถวค่าตั้ง — CompanyId unique)
        var counts = new List<VatFlagCompanyTypeCount>();
        if (conflictIds.Count > 0)
            counts = (await (from d in VatFlagConsistencyController.VatDocsQuery(_db)
                             join s in _db.CompanySettings.AsNoTracking() on d.CompanyId equals s.CompanyId
                             where conflictIds.Contains(d.CompanyId) && !s.IsDeleted && d.CreatedAt >= s.CreatedAt
                             group d by new { d.CompanyId, d.DocumentType } into g
                             select new { g.Key.CompanyId, g.Key.DocumentType, Count = g.Count(), Vat = g.Sum(x => x.VatAmount) })
                    .ToListAsync(ct))
                .Select(x => new VatFlagCompanyTypeCount(x.CompanyId, x.DocumentType, x.Count, x.Vat))
                .ToList();

        var rows = conflicts
            .Select(x => VatFlagConsistencyController.BuildRow(x.c.Id, x.c.Name, x.c.TaxId,
                x.c.IsVatRegistered, x.c.VatRate, x.s?.VatRegistered, x.s?.DefaultVatRate, x.s?.CreatedAt,
                counts.Where(k => k.CompanyId == x.c.Id)
                    .Select(k => new VatFlagDocTypeCount(k.Type.ToString(), k.Count, k.Vat))
                    .OrderBy(k => k.DocumentType)
                    .ToList()))
            .OrderByDescending(r => r.VatAmountInWindow)
            .ThenBy(r => r.CompanyName)
            .ToList();

        return Ok(new ApiResponse<VatFlagConsistencyPlatformReport>(true,
            new VatFlagConsistencyPlatformReport(companies.Count,
                companies.Count(c => !settings.ContainsKey(c.Id)), rows.Count, rows,
                VatFlagConsistencyController.Guidance)));
    }
}

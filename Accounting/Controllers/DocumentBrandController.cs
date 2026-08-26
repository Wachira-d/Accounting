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
/// ชื่อทางการค้า / แบรนด์ที่ใช้ออกเอกสาร — กิจการเดียวมีได้หลายแบรนด์
/// (ร้าน/ไลน์สินค้า/ช่องทางขาย) แต่ละแบรนด์มีโลโก้ สี ที่อยู่ และท้ายเอกสารของตัวเอง
///
/// ⚠️ ด่านกฎหมายว่าแบรนด์ขึ้นเป็น "ชื่อหลัก" ได้กับเอกสารชนิดไหน อยู่ที่
/// <see cref="DocumentIssuerIdentity"/> ตัวเดียว — endpoint นี้แค่ถ่ายทอดผลออกไป
/// ให้หน้าเว็บใช้ ห้ามหน้าเว็บตัดสินเอง (ไม่งั้นกฎสองที่ drift กัน)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/document-brands")]
[Authorize]
public class DocumentBrandController : ControllerBase
{
    private readonly AccountingDbContext _db;

    public DocumentBrandController(AccountingDbContext db) => _db = db;

    public sealed record BrandRequest(
        string Name, string? NameEn, string? TagLine, string? TagLineEn,
        string? LogoPath, string? LogoUrl,
        string? Address, string? AddressEn, string? Phone, string? Email, string? Website,
        string? PrimaryColor, string? SecondaryColor, Guid? DefaultTemplateId,
        string? FooterNotes, string? FooterNotesEn,
        string? LegalNamePlacement, bool IsActive = true, int SortOrder = 0);

    public sealed record BrandResponse(
        Guid Id, string Name, string? NameEn, string? TagLine, string? TagLineEn,
        string? LogoPath, string? LogoUrl,
        string? Address, string? AddressEn, string? Phone, string? Email, string? Website,
        string? PrimaryColor, string? SecondaryColor, Guid? DefaultTemplateId,
        string? FooterNotes, string? FooterNotesEn,
        string LegalNamePlacement, bool IsActive, int SortOrder);

    private static BrandResponse Map(DocumentBrand b) => new(
        b.Id, b.Name, b.NameEn, b.TagLine, b.TagLineEn, b.LogoPath, b.LogoUrl,
        b.Address, b.AddressEn, b.Phone, b.Email, b.Website,
        b.PrimaryColor, b.SecondaryColor, b.DefaultTemplateId,
        b.FooterNotes, b.FooterNotesEn, b.LegalNamePlacement, b.IsActive, b.SortOrder);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<BrandResponse>>>> List(
        Guid companyId, [FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var q = _db.DocumentBrands.AsNoTracking().Where(b => b.CompanyId == companyId && !b.IsDeleted);
        if (!includeInactive) q = q.Where(b => b.IsActive);
        var rows = await q.OrderBy(b => b.SortOrder).ThenBy(b => b.Name).ToListAsync(ct);
        return Ok(new ApiResponse<List<BrandResponse>>(true, rows.Select(Map).ToList()));
    }

    /// <summary>ชนิดเอกสารที่ยอมให้ชื่อทางการค้าขึ้นเป็นชื่อหลัก — หน้าเว็บใช้
    /// ตัดสินว่าจะโชว์ตัวเลือกแบรนด์แบบ "ขึ้นหัวเลย" หรือแบบ "โลโก้/สีเท่านั้น"</summary>
    [HttpGet("policy")]
    public ActionResult<ApiResponse<object>> Policy()
    {
        var rows = Enum.GetValues<DocumentType>()
            .Select(t => new
            {
                type = t.ToString(),
                value = (int)t,
                brandCanBePrimary = DocumentIssuerIdentity.CanBrandBePrimary(t),
            })
            .ToList();
        return Ok(new ApiResponse<object>(true, new
        {
            types = rows,
            // ข้อความอธิบายให้หน้าเว็บใช้ตรง ๆ — คำอธิบายกฎหมายอยู่ที่เดียว
            note = "ใบกำกับภาษี/ใบเพิ่มหนี้/ใบลดหนี้/ใบเสร็จรับเงิน ต้องขึ้นชื่อนิติบุคคล"
                 + "เป็นตัวหลักตาม ป.รัษฎากร §86/4 — แบรนด์ยังใช้โลโก้ สี และที่อยู่หน้าร้านได้",
        }));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<BrandResponse>>> Create(
        Guid companyId, [FromBody] BrandRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new ApiResponse<BrandResponse>(false, null, "กรุณากรอกชื่อทางการค้า"));

        var b = new DocumentBrand { CompanyId = companyId };
        Apply(b, req);
        _db.DocumentBrands.Add(b);
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<BrandResponse>(true, Map(b), "บันทึกชื่อทางการค้าแล้ว"));
    }

    [HttpPut("{brandId:guid}")]
    public async Task<ActionResult<ApiResponse<BrandResponse>>> Update(
        Guid companyId, Guid brandId, [FromBody] BrandRequest req, CancellationToken ct)
    {
        var b = await _db.DocumentBrands
            .FirstOrDefaultAsync(x => x.Id == brandId && x.CompanyId == companyId && !x.IsDeleted, ct);
        if (b == null) return NotFound(new ApiResponse<BrandResponse>(false, null, "ไม่พบชื่อทางการค้านี้"));
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new ApiResponse<BrandResponse>(false, null, "กรุณากรอกชื่อทางการค้า"));

        Apply(b, req);
        b.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<BrandResponse>(true, Map(b), "บันทึกแล้ว"));
    }

    [HttpDelete("{brandId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(
        Guid companyId, Guid brandId, CancellationToken ct)
    {
        var b = await _db.DocumentBrands
            .FirstOrDefaultAsync(x => x.Id == brandId && x.CompanyId == companyId && !x.IsDeleted, ct);
        if (b == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบชื่อทางการค้านี้"));

        // เอกสารที่ออกไปแล้วต้องพิมพ์ซ้ำได้เหมือนเดิม — ลบจริงไม่ได้ ปิดใช้งานแทน
        var used = await _db.Documents
            .AnyAsync(d => d.CompanyId == companyId && d.BrandId == brandId && !d.IsDeleted, ct);
        if (used)
        {
            b.IsActive = false;
            b.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Ok(new ApiResponse<string>(true, null,
                "มีเอกสารที่ออกในนามนี้แล้ว — ปิดใช้งานให้แทนการลบ (เอกสารเก่ายังพิมพ์ได้เหมือนเดิม)"));
        }

        b.IsDeleted = true;
        b.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<string>(true, null, "ลบแล้ว"));
    }

    /// <summary>พรีวิวหัวเอกสารแบบสด — หน้าตั้งค่าเรียกเพื่อโชว์ว่าใบชนิดนี้
    /// จะขึ้นชื่ออะไร โดยใช้ resolver ตัวเดียวกับ PDF จริง (ไม่ใช่จำลองในหน้าเว็บ)</summary>
    [HttpGet("{brandId:guid}/preview")]
    public async Task<ActionResult<ApiResponse<object>>> Preview(
        Guid companyId, Guid brandId, [FromQuery] DocumentType type = DocumentType.Quotation,
        [FromQuery] string lang = "th", CancellationToken ct = default)
    {
        var b = await _db.DocumentBrands.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == brandId && x.CompanyId == companyId && !x.IsDeleted, ct);
        if (b == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบชื่อทางการค้านี้"));

        var co = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (co == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบบริษัท"));
        var st = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);

        var isEn = lang == "en";
        var id = DocumentIssuerIdentity.Resolve(
            type, null, isEn,
            co.Name, co.NameEn, co.TaxId,
            string.IsNullOrWhiteSpace(co.TaxId) ? null : FormatBranchLabel(co.BranchCode, co.BranchName),
            co.Address, co.Phone, co.Email,
            st?.LogoPath, st?.LogoUrl, st?.PrimaryColor,
            new DocumentBrandView(b.Name, b.NameEn, b.TagLine, b.TagLineEn, b.LogoPath, b.LogoUrl,
                b.Address, b.AddressEn, b.Phone, b.Email, b.Website, b.PrimaryColor,
                b.LegalNamePlacement, b.IsActive));

        return Ok(new ApiResponse<object>(true, new
        {
            primaryName = id.PrimaryName,
            secondaryName = id.SecondaryName,
            tagLine = id.TagLine,
            logoUrl = id.LogoUrl,
            address = id.Address,
            phone = id.Phone,
            email = id.Email,
            website = id.Website,
            primaryColor = id.PrimaryColor,
            legalLine = id.LegalLine,
            legalLineInHeader = id.LegalLineInHeader,
            legalLineInFooter = id.LegalLineInFooter,
            brandIsPrimary = id.BrandIsPrimary,
        }));
    }

    private static string FormatBranchLabel(string? code, string? name)
        => string.IsNullOrWhiteSpace(code) || code == "00000"
            ? "สำนักงานใหญ่"
            : (string.IsNullOrWhiteSpace(name) ? $"สาขาที่ {code}" : $"สาขาที่ {code} {name}");

    private static void Apply(DocumentBrand b, BrandRequest r)
    {
        b.Name = r.Name.Trim();
        b.NameEn = Blank(r.NameEn);
        b.TagLine = Blank(r.TagLine);
        b.TagLineEn = Blank(r.TagLineEn);
        b.LogoPath = Blank(r.LogoPath);
        b.LogoUrl = Blank(r.LogoUrl);
        b.Address = Blank(r.Address);
        b.AddressEn = Blank(r.AddressEn);
        b.Phone = Blank(r.Phone);
        b.Email = Blank(r.Email);
        b.Website = Blank(r.Website);
        b.PrimaryColor = Blank(r.PrimaryColor);
        b.SecondaryColor = Blank(r.SecondaryColor);
        b.DefaultTemplateId = r.DefaultTemplateId;
        b.FooterNotes = Blank(r.FooterNotes);
        b.FooterNotesEn = Blank(r.FooterNotesEn);
        // ไม่มีตัวเลือก "ไม่แสดงชื่อนิติบุคคล" — ค่านอกลิสต์ตกไป Footer เสมอ
        var lp = (r.LegalNamePlacement ?? "").Trim();
        b.LegalNamePlacement = lp is "Header" or "Both" ? lp : "Footer";
        b.IsActive = r.IsActive;
        b.SortOrder = r.SortOrder;
    }

    // "" จากฟอร์ม = ล้างค่า → เก็บเป็น null ให้ fallback ไปข้อมูลบริษัททำงาน
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

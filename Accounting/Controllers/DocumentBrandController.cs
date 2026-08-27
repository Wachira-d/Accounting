using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
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
    private readonly IImageProcessingService _images;
    private readonly IWebHostEnvironment _env;

    public DocumentBrandController(
        AccountingDbContext db, IImageProcessingService images, IWebHostEnvironment env)
    {
        _db = db;
        _images = images;
        _env = env;
    }

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

    /// <summary>อัปโหลดโลโก้ของแบรนด์ — ย่อ/บีบอัดด้วย pipeline เดียวกับโลโก้บริษัท
    /// (<c>ImageProfile.Logo</c> คงความโปร่งใส PNG) เก็บแยกโฟลเดอร์ต่อบริษัท
    /// <para>เดิมหน้าตั้งค่าให้พิมพ์ URL เอง ซึ่งใช้ได้เฉพาะคนที่มีไฟล์อยู่บน
    /// เว็บอยู่แล้ว — ผู้ใช้ทั่วไปมีแต่ไฟล์ในเครื่อง</para></summary>
    [HttpPost("{brandId:guid}/logo")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<BrandResponse>>> UploadLogo(
        Guid companyId, Guid brandId, IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<BrandResponse>(false, null, "กรุณาเลือกไฟล์โลโก้"));

        // ⚠️ **ไม่รับ SVG** — ImageProcessingService ปล่อย SVG ผ่านแบบดิบ แล้วไฟล์
        // ถูก serve จาก origin เดียวกับแอป (JWT อยู่ localStorage) ⇒ SVG ที่ฝัง
        // <script> = stored XSS ขโมย token ได้ (กฎเหล็ก #4 C — ผลตรวจข้อ 10)
        // ContentType มาจาก client ด้วย จึงเช็คนามสกุลไฟล์ควบ
        var allowed = new[] { "image/png", "image/jpeg", "image/gif", "image/webp" };
        var ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
        var allowedExt = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
        if (!allowed.Contains((file.ContentType ?? "").ToLowerInvariant()) || !allowedExt.Contains(ext))
            return BadRequest(new ApiResponse<BrandResponse>(false, null,
                "รองรับเฉพาะไฟล์ PNG, JPEG, GIF, WebP เท่านั้น (ไม่รับ SVG ด้วยเหตุผลด้านความปลอดภัย)"));

        var b = await _db.DocumentBrands
            .FirstOrDefaultAsync(x => x.Id == brandId && x.CompanyId == companyId && !x.IsDeleted, ct);
        if (b == null) return NotFound(new ApiResponse<BrandResponse>(false, null, "ไม่พบชื่อทางการค้านี้"));

        // WebRootPath เป็น null ได้ใน deployment ที่ไม่มี wwwroot — ถอยไป ContentRoot
        // (เคสเดียวกับ SettingsService.UploadLogoAsync)
        var webRoot = _env.WebRootPath
            ?? Path.Combine(_env.ContentRootPath ?? Directory.GetCurrentDirectory(), "wwwroot");
        var dir = Path.Combine(webRoot, "uploads", "brand-logos", companyId.ToString());
        var web = $"/uploads/brand-logos/{companyId}";

        var oldPath = b.LogoPath;
        try
        {
            using var stream = file.OpenReadStream();
            // FileName จาก multipart เป็น null ได้ (client บางตัวไม่ส่ง filename)
            // — ตัวประมวลผลรูปรับ string ไม่ nullable จึงต้องมีชื่อสำรองเสมอ
            var fileName = string.IsNullOrWhiteSpace(file.FileName) ? $"logo{ext}" : file.FileName;
            var processed = await _images.ProcessAndSaveAsync(
                stream, file.ContentType!, fileName, dir, web, ImageProfile.Logo);
            b.LogoPath = processed.AbsolutePath;
            b.LogoUrl = processed.RelativeUrl;
        }
        catch (UnauthorizedAccessException ex)
        {
            return BadRequest(new ApiResponse<BrandResponse>(false, null,
                $"ระบบไม่มีสิทธิ์เขียนไฟล์ลงโฟลเดอร์ uploads ({dir}) — โปรดติดต่อผู้ดูแลระบบ: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<BrandResponse>(false, null, $"บันทึกไฟล์ไม่สำเร็จ: {ex.Message}"));
        }

        b.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // ลบไฟล์เก่าหลังบันทึกสำเร็จเท่านั้น — ลบก่อนแล้วอัปโหลดพังคือเสียของเดิมฟรี
        try { if (!string.IsNullOrEmpty(oldPath) && oldPath != b.LogoPath && System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath); }
        catch { /* ไฟล์เก่าหาย/ถูกล็อก — ไม่ใช่เหตุให้การอัปโหลดล้ม */ }

        return Ok(new ApiResponse<BrandResponse>(true, Map(b), "อัปโหลดโลโก้แล้ว"));
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
                b.LegalNamePlacement, b.IsActive, b.FooterNotes, b.FooterNotesEn));

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

    // เดิมคำนวณเอง → ได้ "สาขาที่ 00003" (เลขศูนย์นำหน้าติดมาด้วย) ไม่ตรงถ้อยคำ
    // ตามประกาศอธิบดีฯ ฉบับที่ 199 — ย้ายไปใช้ resolver กลางตัวเดียวทั้งระบบ
    private static string FormatBranchLabel(string? code, string? name)
        => TaxBranchCode.LabelWithName(code, name);

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

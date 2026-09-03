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
/// **ศูนย์ช่วยเหลือ — เอกสารและวิดีโอสอนใช้งาน**
///
/// <para>เนื้อหาเป็นของ**แพลตฟอร์ม** (ผู้ให้บริการเป็นคนสอน) ลูกค้าทุกรายเห็น
/// ชุดเดียวกัน จึงไม่มี tenant scope — แต่ยังต้องล็อกอิน (เนื้อหาสอนวิธีใช้ระบบ
/// ไม่ใช่หน้าการตลาดสาธารณะ) · การแก้ไขจำกัดที่ <c>SystemAdmin</c></para>
/// </summary>
[ApiController]
[Route("api/help")]
[Authorize]
public class HelpCenterController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<HelpCenterController> _logger;
    private readonly IWebHostEnvironment _env;

    public HelpCenterController(AccountingDbContext db, ILogger<HelpCenterController> logger, IWebHostEnvironment env)
    { _db = db; _logger = logger; _env = env; }

    private const string UploadFolder = "help-media";

    /// <summary>รูปที่ส่งให้หน้าเว็บ — **URL สำหรับฝังคำนวณที่นี่** ไม่เก็บใน DB
    /// เพื่อไม่ให้ค้างเป็นสูตรเก่าเมื่อ provider เปลี่ยนรูปแบบ embed</summary>
    private static object ToDto(HelpResource r) => new
    {
        r.Id,
        r.Title,
        r.Description,
        category = r.Category.ToString(),
        categoryLabel = CategoryLabel(r.Category),
        r.ModuleCode,
        kind = r.Kind.ToString(),
        provider = r.Provider.ToString(),
        r.SourceUrl,
        r.StoragePath,
        r.FileName,
        r.DurationSeconds,
        r.IsPublished,
        r.SortOrder,
        r.ViewCount,
        // null = ฝังไม่ได้ → UI ต้องวาดปุ่ม "เปิดในแท็บใหม่" ไม่ใช่กรอบว่าง
        embedUrl = HelpMediaEmbed.ToEmbedUrl(r.SourceUrl ?? r.StoragePath, r.Provider),
        thumbnailUrl = r.ThumbnailUrl ?? HelpMediaEmbed.ThumbnailUrl(r.SourceUrl, r.Provider),
    };

    private static string CategoryLabel(HelpCategory c) => c switch
    {
        HelpCategory.GettingStarted => "เริ่มต้นใช้งาน",
        HelpCategory.Accounting => "ระบบบัญชี",
        HelpCategory.Documents => "เอกสาร",
        HelpCategory.Tax => "ภาษี",
        HelpCategory.Payroll => "เงินเดือน/ประกันสังคม",
        HelpCategory.Inventory => "สินค้าคงคลัง",
        HelpCategory.BusinessFeature => "ฟีเจอร์เฉพาะธุรกิจ",
        HelpCategory.Reports => "รายงาน",
        _ => "อื่น ๆ",
    };

    private bool IsPlatformAdmin() => User.IsInRole("SystemAdmin");

    /// <summary>รายการเนื้อหา — ลูกค้าเห็นเฉพาะที่เผยแพร่แล้ว · แอดมินเห็นทั้งหมด</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> List(
        [FromQuery] string? category = null, [FromQuery] string? moduleCode = null, [FromQuery] string? q = null)
    {
        var admin = IsPlatformAdmin();
        var query = _db.HelpResources.AsNoTracking().Where(r => !r.IsDeleted);
        if (!admin) query = query.Where(r => r.IsPublished);

        if (!string.IsNullOrWhiteSpace(category)
            && Enum.TryParse<HelpCategory>(category, true, out var cat))
            query = query.Where(r => r.Category == cat);

        if (!string.IsNullOrWhiteSpace(moduleCode))
            query = query.Where(r => r.ModuleCode == moduleCode);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(r => r.Title.Contains(term)
                || (r.Description != null && r.Description.Contains(term)));
        }

        var rows = await query
            .OrderBy(r => r.Category).ThenBy(r => r.SortOrder).ThenBy(r => r.Title)
            .Take(500)
            .ToListAsync();

        return Ok(new ApiResponse<object>(true, new
        {
            items = rows.Select(ToDto),
            // หมวดที่ "มีเนื้อหาจริง" เท่านั้น — หน้าเว็บสร้างแท็บจากตรงนี้
            // ไม่ต้องพิมพ์รายชื่อหมวดซ้ำ (drift เป็นศูนย์โดยโครงสร้าง)
            categories = rows.GroupBy(r => r.Category)
                .Select(g => new { value = g.Key.ToString(), label = CategoryLabel(g.Key), count = g.Count() })
                .OrderBy(x => x.label),
            modules = rows.Where(r => !string.IsNullOrWhiteSpace(r.ModuleCode))
                .GroupBy(r => r.ModuleCode!)
                .Select(g => new { value = g.Key, count = g.Count() })
                .OrderBy(x => x.value),
        }));
    }

    /// <summary>นับยอดเปิดดู — best-effort ไม่ให้ล้มเส้นการดู</summary>
    [HttpPost("{id:guid}/view")]
    public async Task<ActionResult<ApiResponse<object>>> RecordView(Guid id)
    {
        try
        {
            var row = await _db.HelpResources.FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted);
            if (row != null) { row.ViewCount++; await _db.SaveChangesAsync(); }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "นับยอดเปิดดูเนื้อหาช่วยเหลือไม่สำเร็จ"); }
        return Ok(new ApiResponse<object>(true, new { id }));
    }

    // ───────────────────────── แอดมินแพลตฟอร์ม ─────────────────────────

    public record UpsertHelpRequest(
        Guid? Id, string Title, string? Description, string Category, string? ModuleCode,
        string Kind, string? SourceUrl, string? StoragePath, string? FileName,
        int DurationSeconds, string? ThumbnailUrl, bool IsPublished, int SortOrder);

    [HttpPost]
    [Authorize(Roles = "SystemAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Upsert([FromBody] UpsertHelpRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาระบุชื่อเรื่อง"));
        if (!Enum.TryParse<HelpCategory>(req.Category, true, out var cat))
            return BadRequest(new ApiResponse<string>(false, null, "หมวดไม่ถูกต้อง"));
        if (!Enum.TryParse<HelpResourceKind>(req.Kind, true, out var kind))
            return BadRequest(new ApiResponse<string>(false, null, "ชนิดต้องเป็น Video / Document / Article / Link"));

        var hasSource = !string.IsNullOrWhiteSpace(req.SourceUrl) || !string.IsNullOrWhiteSpace(req.StoragePath);
        // บทความใช้ Description เป็นเนื้อหาจึงไม่ต้องมีไฟล์/ลิงก์ · ที่เหลือถ้าไม่มี
        // แหล่งสื่อเลย = การ์ดที่กดแล้วไม่มีอะไรเกิดขึ้น (silent no-op) จึงบล็อก
        if (kind != HelpResourceKind.Article && !hasSource)
            return BadRequest(new ApiResponse<string>(false, null,
                "ต้องใส่ลิงก์วิดีโอ หรืออัปโหลดไฟล์อย่างน้อยหนึ่งอย่าง"));

        var row = req.Id.HasValue
            ? await _db.HelpResources.FirstOrDefaultAsync(r => r.Id == req.Id.Value && !r.IsDeleted)
            : null;
        if (req.Id.HasValue && row == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบเนื้อหานี้"));
        if (row == null)
        {
            row = new HelpResource { CreatedBy = User.Identity?.Name };
            _db.HelpResources.Add(row);
        }

        row.Title = req.Title.Trim();
        row.Description = req.Description?.Trim();
        row.Category = cat;
        row.ModuleCode = string.IsNullOrWhiteSpace(req.ModuleCode) ? null : req.ModuleCode.Trim();
        row.Kind = kind;
        row.SourceUrl = string.IsNullOrWhiteSpace(req.SourceUrl) ? null : req.SourceUrl.Trim();
        row.StoragePath = string.IsNullOrWhiteSpace(req.StoragePath) ? null : req.StoragePath.Trim();
        row.FileName = req.FileName?.Trim();
        row.DurationSeconds = Math.Max(0, req.DurationSeconds);
        row.ThumbnailUrl = string.IsNullOrWhiteSpace(req.ThumbnailUrl) ? null : req.ThumbnailUrl.Trim();
        row.IsPublished = req.IsPublished;
        row.SortOrder = req.SortOrder;
        // เดา provider จาก URL เสมอ — ให้ผู้ดูแลเลือกเองแล้วเลือกผิด = ฝังไม่ขึ้น
        // โดยไม่มีอะไรบอกว่าทำไม
        row.Provider = row.StoragePath != null && row.SourceUrl == null
            ? HelpMediaProvider.SelfHosted
            : HelpMediaEmbed.DetectProvider(row.SourceUrl);
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync();

        var embeddable = HelpMediaEmbed.ToEmbedUrl(row.SourceUrl ?? row.StoragePath, row.Provider) != null;
        return Ok(new ApiResponse<object>(true, ToDto(row),
            embeddable || row.Kind != HelpResourceKind.Video
                ? "บันทึกแล้ว"
                : "บันทึกแล้ว — แต่ลิงก์นี้ฝังในหน้าไม่ได้ ผู้ใช้จะเห็นเป็นปุ่มเปิดแท็บใหม่แทน"));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "SystemAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id)
    {
        var row = await _db.HelpResources.FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted);
        if (row == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบเนื้อหานี้"));
        row.IsDeleted = true;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { id }, "ลบแล้ว"));
    }

    /// <summary>อัปโหลดวิดีโอ/คู่มือเข้าระบบเอง (ทางเลือกแทนการฝังจากภายนอก)
    ///
    /// เขียนลง <c>/uploads/help-media</c> ซึ่ง **อยู่ใน allow-list ของ static
    /// handler แล้ว** — โฟลเดอร์ใหม่ที่ลืมเพิ่มจะเขียนไฟล์สำเร็จแต่โหลด 404
    /// (defect class ที่ `tools/upload_route_check.py` คุมอยู่)</summary>
    [HttpPost("upload")]
    [Authorize(Roles = "SystemAdmin")]
    [RequestSizeLimit(300 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<object>>> Upload(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาเลือกไฟล์"));

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowed = new[] { ".mp4", ".webm", ".mov", ".m4v", ".pdf" };
        if (!allowed.Contains(ext))
            return BadRequest(new ApiResponse<string>(false, null,
                "รองรับเฉพาะไฟล์วิดีโอ (.mp4 .webm .mov .m4v) และคู่มือ .pdf"));

        var dir = Path.Combine(_env.WebRootPath ?? "wwwroot", "uploads", UploadFolder);
        Directory.CreateDirectory(dir);
        // ชื่อไฟล์เป็น GUID — ชื่อเดิมของผู้ดูแลอาจมีอักขระที่ทำให้ path พัง
        // และไฟล์นี้เสิร์ฟสาธารณะ จึงไม่ควรเดาชื่อได้
        var stored = $"{Guid.NewGuid():N}{ext}";
        var full = Path.Combine(dir, stored);
        await using (var fs = System.IO.File.Create(full))
            await file.CopyToAsync(fs);

        return Ok(new ApiResponse<object>(true, new
        {
            storagePath = $"/uploads/{UploadFolder}/{stored}",
            fileName = file.FileName,
            sizeBytes = file.Length,
        }, "อัปโหลดสำเร็จ"));
    }
}

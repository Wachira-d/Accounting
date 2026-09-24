using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers.V1;

/// <summary>
/// `/api/v1/ocr` — สแกนเอกสารด้วย AI แล้วคืนข้อมูลครบตาม §86/4
/// ให้ระบบบัญชีปลายทาง (Dynamics ฯลฯ) เอาไปสร้างเอกสารต่อ
///
/// **กฎเหล็ก #1 / §7.3 ในบริบท API**: response แนบ `feedbackId` ทุกครั้ง และ
/// ลูกค้าต้องส่งค่าที่ใช้จริงกลับมาที่ `/confirm` — การยืนยันตามปกติของ
/// workflow **คือ** feedback ที่สอน local model โดยลูกค้าไม่ต้องทำอะไรพิเศษ
/// ถ้าไม่มีขั้นนี้ ทุก call ของลูกค้า Connected จะเป็น "ยิงทิ้ง" ตลอดไป
/// </summary>
[ApiController]
[Route("api/v1/ocr")]
[Authorize]
public class OcrV1Controller : PublicApiControllerBase
{
    private const string Feature = "ocr.scan";

    private readonly IOcrService _ocr;
    private readonly ILogger<OcrV1Controller> _logger;

    public OcrV1Controller(AccountingDbContext db, IUsageMeteringService metering,
        IOcrService ocr, ILogger<OcrV1Controller> logger)
        : base(db, metering)
    { _ocr = ocr; _logger = logger; }

    /// <summary>
    /// อัปโหลดรูป/PDF แล้วรับข้อมูลที่อ่านได้ทันที (synchronous)
    ///
    /// เอกสารหน้าเดียวเสร็จในไม่กี่วินาที จึงตอบตรง ๆ ได้; งานหลายหน้า/ชุดใหญ่
    /// ใช้ `/batch` ที่คืน jobId แล้วแจ้งผลทาง webhook แทน
    /// </summary>
    [HttpPost("scan")]
    // ตัวตั้งตัวเดียวกับเส้นเว็บ (เดิม 30MB ที่นี่ · 10MB ที่เส้นเว็บ · 50MB ที่ด่านตรวจ
    // = สามเพดานสำหรับไฟล์ชนิดเดียวกัน)
    [RequestSizeLimit(Accounting.Services.Implementations.Ocr.OcrPreprocessor.MaxFileSize)]
    public async Task<IActionResult> Scan(IFormFile file, CancellationToken ct)
    {
        var (ctx, error) = await ResolveCallerAsync("ocr:write", Feature, ct);
        if (error != null) return error;

        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาแนบไฟล์"));

        try
        {
            var attachmentId = await SaveUploadAsync(ctx!.CompanyId, file, ct);
            if (attachmentId == null)
                return StatusCode(500, new ApiResponse<string>(false, null,
                    "บันทึกไฟล์ไม่สำเร็จ — ไม่มีการคิดค่าบริการ"));

            var result = await _ocr.ScanAsync(ctx.CompanyId, attachmentId.Value);

            // คิดเงิน **หลังงานสำเร็จเท่านั้น** — สแกนล้มแล้วเก็บเงินคือสิ่งที่
            // ลูกค้าให้อภัยยากที่สุด
            var usage = await MeterAsync(ctx, Feature, 1, "OcrScanResult", result.Id, ct);

            return Ok(new ApiResponse<object>(true, new
            {
                scanId = result.Id,
                data = result,
                // §7.3 ข้อ 1 — ลูกค้าต้องได้ feedbackId ไปส่งคืนตอนยืนยัน
                billing = new
                {
                    charged = usage.ChargedAmount,
                    coveredByFreeQuota = usage.CoveredByFreeQuota,
                    duplicate = usage.Duplicate,
                },
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR v1 scan ล้มเหลว company={Company}", ctx!.CompanyId);
            return StatusCode(500, new ApiResponse<string>(false, null,
                "สแกนเอกสารไม่สำเร็จ กรุณาลองใหม่ — ไม่มีการคิดค่าบริการสำหรับรายการนี้"));
        }
    }

    /// <summary>
    /// เก็บไฟล์ + สร้าง FileAttachment ให้ OCR engine ใช้ต่อ
    ///
    /// `UploadedByUserId` ต้องเป็น **User จริง** — key แบบ integration ตั้ง
    /// NameIdentifier เป็น IntegrationId ซึ่งไม่ใช่ User ถ้าใช้ตรง ๆ FK จะพัง
    /// (บั๊กเดิมที่ OcrController เจอมาแล้ว) จึง resolve เจ้าของบริษัทแทน
    /// </summary>
    private async Task<Guid?> SaveUploadAsync(Guid companyId, IFormFile file, CancellationToken ct)
    {
        try
        {
            var uploaderId = await Db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == companyId)
                .OrderBy(cu => cu.JoinedAt)
                .Select(cu => (Guid?)cu.UserId)
                .FirstOrDefaultAsync(ct);
            if (uploaderId == null) return null;

            var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "ocr");
            Directory.CreateDirectory(dir);
            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            var path = Path.Combine(dir, fileName);

            await using (var fs = System.IO.File.Create(path))
                await file.CopyToAsync(fs, ct);

            var attachment = new Models.Entities.FileAttachment
            {
                CompanyId = companyId,
                FileName = fileName,
                OriginalFileName = file.FileName,
                ContentType = file.ContentType ?? "application/octet-stream",
                FileSize = file.Length,
                StoragePath = path,
                EntityType = "OcrScan",
                EntityId = Guid.NewGuid(),
                UploadedByUserId = uploaderId.Value,
            };
            Db.FileAttachments.Add(attachment);
            await Db.SaveChangesAsync(ct);
            return attachment.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "บันทึกไฟล์ OCR (v1) ไม่สำเร็จ company={Company}", companyId);
            return null;
        }
    }

    public record ConfirmFieldRequest(string FieldName, string? FinalValue, Guid? FeedbackId);
    public record ConfirmRequest(Guid ScanId, List<ConfirmFieldRequest> Fields);

    /// <summary>
    /// ยืนยันค่าที่ใช้จริงหลังผู้ใช้ตรวจในระบบปลายทาง — **ปิดลูปการเรียนรู้**
    ///
    /// ระบบเทียบค่าสุดท้ายกับที่ AI เสนอ แล้วบันทึกเป็น feedback ให้ local model
    /// (ตรงกับที่ UI ของเราทำตอนผู้ใช้กดยืนยัน) ยิ่งลูกค้าใช้มาก local ยิ่งแม่น
    /// → เรียก AI น้อยลง → ต้นทุนต่อ transaction ลดลงเรื่อย ๆ
    ///
    /// ไม่คิดเงินซ้ำ — ค่าบริการเก็บไปแล้วตอน scan
    /// </summary>
    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm([FromBody] ConfirmRequest req,
        [FromServices] Services.Ai.IAiFeedbackRecorder recorder, CancellationToken ct)
    {
        var (ctx, error) = await ResolveCallerAsync("ocr:write", Feature, ct);
        if (error != null) return error;

        if (req?.Fields == null || req.Fields.Count == 0)
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาส่งรายการฟิลด์ที่ยืนยัน"));

        // รอบ 193 (ฝ่ายค้านรอบสอง): recorder ค้น feedback ด้วย Id อย่างเดียว ⇒ พาร์ตเนอร์ที่รู้ GUID ของบริษัทอื่น
        // เขียนทับคำตอบสอนโมเดลของบริษัทนั้นได้ — กรองให้เหลือเฉพาะ feedback ของบริษัทผู้เรียกก่อน (กฎ M tenant isolation)
        var askedIds = req.Fields.Where(x => x.FeedbackId.HasValue).Select(x => x.FeedbackId!.Value).Distinct().ToList();
        var ownIds = askedIds.Count == 0 ? new HashSet<Guid>()
            : (await Db.AiSuggestionFeedbacks.AsNoTracking()
                .Where(x => x.CompanyId == ctx.CompanyId && askedIds.Contains(x.Id))
                .Select(x => x.Id).ToListAsync(ct)).ToHashSet();

        var recorded = 0;
        foreach (var f in req.Fields)
        {
            if (!f.FeedbackId.HasValue || string.IsNullOrWhiteSpace(f.FinalValue)) continue;
            if (!ownIds.Contains(f.FeedbackId.Value)) continue;   // ไม่ใช่ของบริษัทนี้ = ไม่มีอยู่ (ไม่บอกว่ามีของคนอื่น)
            try
            {
                // acceptedAi ตัดสินที่ recorder โดยเทียบกับคำตอบเดิมที่บันทึกไว้
                // พาร์ตเนอร์ส่งค่าที่ "ตรวจแล้วแก้แล้ว" กลับมา = การลงมือเลือกของฝั่งเขา
            await recorder.RecordUserChoiceAsync(f.FeedbackId.Value, f.FinalValue!, acceptedAi: false, ct,
                Accounting.Models.Enums.UserChoiceSource.Explicit);
                recorded++;
            }
            catch (Exception ex)
            {
                // feedback ล้มต้องไม่ทำให้ทั้ง request ล้ม — ลูกค้ายืนยันเอกสารไปแล้ว
                _logger.LogWarning(ex, "บันทึก feedback ไม่สำเร็จ feedbackId={Id}", f.FeedbackId);
            }
        }

        return Ok(new ApiResponse<object>(true, new { scanId = req.ScanId, recordedFeedback = recorded },
            $"บันทึกการยืนยัน {recorded} ฟิลด์ — ระบบจะเรียนรู้เพื่อให้ครั้งหน้าแม่นขึ้น"));
    }
}

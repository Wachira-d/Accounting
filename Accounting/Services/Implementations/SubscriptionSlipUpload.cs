using Accounting.Models.DTOs.Subscription;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 219 เฟส 2 — <b>ตัวรับสลิปค่าบริการ NextAcc ตัวเดียว</b> ของทุกทางเข้า (หน้าแพ็กเกจ <c>SubscriptionController.UploadSlip</c> · ศูนย์บิล ·
/// ลิงก์ชำระสาธารณะ) — ชนิดไฟล์ · ย่อรูป · ผูกกับรายการชำระ · อ่านสลิปช่วยแอดมิน (advisory) ห้ามมีสำเนาที่สอง (เดิมอยู่ใน controller เดียว)
/// <para>ที่เก็บไฟล์ = <c>wwwroot/uploads/slips</c> เดิม แต่<b>ไม่เสิร์ฟตรงแล้ว</b> (ฝ่ายค้านรอบ 219 เฟส 2 S9 — ถอดจาก publicUploadPrefixes ·
/// เปิดผ่าน endpoint ที่ตรวจสิทธิ์ด้วย <see cref="OpenStored"/> เท่านั้น · ไฟล์เดิมอยู่ที่เดิม จึงไม่ต้องย้ายไฟล์) — การย้ายไปที่เก็บไฟล์กลาง (กฎ #4 D)
/// ยังเป็นงานแยก (ต้องย้ายไฟล์เดิม + ตัวอัปโหลดของลิงก์ชำระไม่มีผู้ใช้ให้ผูก FK ผู้อัปโหลด)</para>
/// </summary>
public static class SubscriptionSlipUpload
{
    public static readonly string[] AllowedContentTypes = { "image/jpeg", "image/png", "image/webp", "application/pdf" };
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>ตรวจไฟล์ก่อนสร้างอะไร — null = ใช้ได้</summary>
    public static string? Reject(IFormFile? file)
    {
        if (file == null || file.Length == 0) return "กรุณาแนบไฟล์สลิป";
        if (file.Length > MaxBytes) return "ไฟล์สลิปใหญ่เกิน 10MB";
        if (!AllowedContentTypes.Contains((file.ContentType ?? "").ToLowerInvariant())) return "รองรับเฉพาะไฟล์ JPG, PNG, WebP, PDF";
        return null;
    }

    /// <summary>ไฟล์สลิปที่เก็บแล้ว (ยังไม่ผูกรายการ)</summary>
    public sealed record StoredSlip(string FileName, string OriginalFileName, string ContentType, long Size, string StoragePath, string AbsolutePath);

    /// <summary>โฟลเดอร์เก็บสลิป — อยู่ใต้ wwwroot แต่<b>ไม่เสิร์ฟตรง</b> (ไม่อยู่ใน publicUploadPrefixes ของ Program.cs · ฝ่ายค้าน S9)</summary>
    public static string SlipDirectory(IWebHostEnvironment env)
        => Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "uploads", "slips");

    /// <summary>ฝ่ายค้าน N3: ตรวจ/ย่อ/เก็บไฟล์ <b>ก่อน</b>สร้างรายการชำระ — ไฟล์เสีย (ตัวย่อรูปตรวจไบต์จริง) ⇒ ไม่เกิดรายการค้างที่ไม่มีสลิป</summary>
    public static async Task<StoredSlip> StoreAsync(IFormFile file, IImageProcessingService images, IWebHostEnvironment env)
    {
        var dir = SlipDirectory(env);
        await using var s = file.OpenReadStream();
        var processed = await images.ProcessAndSaveAsync(s, file.ContentType, file.FileName, dir, "/uploads/slips", ImageProfile.Slip);
        var fileName = Path.GetFileName(processed.AbsolutePath);
        return new StoredSlip(fileName, file.FileName, file.ContentType, processed.FinalBytes > 0 ? processed.FinalBytes : file.Length,
            Path.Combine("uploads", "slips", fileName), Path.Combine(dir, fileName));
    }

    /// <summary>ผูกไฟล์ที่เก็บแล้วกับรายการชำระ + อ่านสลิปช่วยแอดมิน (advisory)</summary>
    public static async Task<SubscriptionPaymentResponse> AttachAsync(StoredSlip slip, Guid paymentId, string actor, ISubscriptionService subs,
        ISlipOcrAssistService? slipOcr, ILogger? logger = null)
    {
        var result = await subs.UploadPaymentSlipAsync(paymentId, slip.FileName, slip.OriginalFileName, slip.ContentType, slip.Size, slip.StoragePath, actor);
        // WP-C3: อ่านสลิป (local OCR) เทียบยอด → ช่วยแอดมินตรวจ · advisory ไม่ block (ล้ม = log ไม่กลืนเงียบ)
        if (slipOcr != null)
        {
            try
            {
                if (File.Exists(slip.AbsolutePath))
                    await slipOcr.ParseAndStoreAsync(paymentId, await File.ReadAllBytesAsync(slip.AbsolutePath), slip.ContentType);
            }
            catch (Exception ex) { logger?.LogWarning(ex, "อ่านสลิปค่าบริการ (advisory) ไม่สำเร็จ — payment {PaymentId}", paymentId); }
        }
        return result;
    }

    /// <summary>เก็บ + ผูกในครั้งเดียว (รายการมีอยู่แล้ว — หน้าแพ็กเกจแนบสลิปภายหลัง)</summary>
    public static async Task<SubscriptionPaymentResponse> SaveAsync(IFormFile file, Guid paymentId, string actor,
        IImageProcessingService images, IWebHostEnvironment env, ISubscriptionService subs, ISlipOcrAssistService? slipOcr,
        ILogger? logger = null)
        => await AttachAsync(await StoreAsync(file, images, env), paymentId, actor, subs, slipOcr, logger);

    /// <summary>ฝ่ายค้าน S9: เปิดไฟล์สลิปที่เก็บไว้ (ชื่อไฟล์ หรือ URL เดิม "/uploads/slips/x") — เอาเฉพาะชื่อไฟล์ (กัน path traversal) ·
    /// null = ไม่มีไฟล์ · ผู้เรียกต้องตรวจสิทธิ์ก่อนเสมอ</summary>
    public static (string Path, string ContentType)? OpenStored(IWebHostEnvironment env, string? fileNameOrUrl)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrUrl)) return null;
        var name = Path.GetFileName(fileNameOrUrl.Replace('\\', '/').Split('?')[0]);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) return null;
        var full = Path.Combine(SlipDirectory(env), name);
        if (!File.Exists(full)) return null;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var type = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
        return (full, type);
    }
}

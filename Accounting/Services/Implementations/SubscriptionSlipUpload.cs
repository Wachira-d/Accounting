using Accounting.Models.DTOs.Subscription;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 219 เฟส 2 — <b>ตัวรับสลิปค่าบริการ NextAcc ตัวเดียว</b> ของทุกทางเข้า (หน้าแพ็กเกจ <c>SubscriptionController.UploadSlip</c> · ศูนย์บิล ·
/// ลิงก์ชำระสาธารณะ) — ชนิดไฟล์ · ย่อรูป · ผูกกับรายการชำระ · อ่านสลิปช่วยแอดมิน (advisory) ห้ามมีสำเนาที่สอง (เดิมอยู่ใน controller เดียว)
/// <para>ที่เก็บไฟล์ = <c>wwwroot/uploads/slips</c> แบบเดิมของรายการชำระค่าบริการ (หน้าตรวจสลิปของแอดมินอ่าน <c>SlipStoragePath</c>) —
/// การย้ายไปที่เก็บไฟล์กลาง (กฎ #4 D) เป็นงานแยกที่ต้องย้ายไฟล์เดิมด้วย</para>
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

    public static async Task<SubscriptionPaymentResponse> SaveAsync(IFormFile file, Guid paymentId, string actor,
        IImageProcessingService images, IWebHostEnvironment env, ISubscriptionService subs, ISlipOcrAssistService? slipOcr,
        ILogger? logger = null)
    {
        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var dir = Path.Combine(webRoot, "uploads", "slips");
        await using var s = file.OpenReadStream();
        var processed = await images.ProcessAndSaveAsync(s, file.ContentType, file.FileName, dir, "/uploads/slips", ImageProfile.Slip);
        var fileName = Path.GetFileName(processed.AbsolutePath);
        var storagePath = Path.Combine("uploads", "slips", fileName);
        var finalSize = processed.FinalBytes > 0 ? processed.FinalBytes : file.Length;

        var result = await subs.UploadPaymentSlipAsync(paymentId, fileName, file.FileName, file.ContentType, finalSize, storagePath, actor);

        // WP-C3: อ่านสลิป (local OCR) เทียบยอด → ช่วยแอดมินตรวจ · advisory ไม่ block (ล้ม = log ไม่กลืนเงียบ)
        if (slipOcr != null)
        {
            try
            {
                var savedPath = Path.Combine(dir, fileName);
                if (File.Exists(savedPath))
                    await slipOcr.ParseAndStoreAsync(paymentId, await File.ReadAllBytesAsync(savedPath), file.ContentType);
            }
            catch (Exception ex) { logger?.LogWarning(ex, "อ่านสลิปค่าบริการ (advisory) ไม่สำเร็จ — payment {PaymentId}", paymentId); }
        }
        return result;
    }
}

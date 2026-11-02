using Accounting.Models.DTOs.Subscription;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 219 เฟส 2 — <b>ตัวรับสลิปค่าบริการ NextAcc ตัวเดียว</b> ของทุกทางเข้า (หน้าแพ็กเกจ <c>SubscriptionController.UploadSlip</c> · ศูนย์บิล ·
/// ลิงก์ชำระสาธารณะ) — ชนิดไฟล์ · ย่อรูป · ผูกกับรายการชำระ · อ่านสลิปช่วยแอดมิน (advisory) ห้ามมีสำเนาที่สอง (เดิมอยู่ใน controller เดียว)
/// <para>ที่เก็บไฟล์ = <c>wwwroot/uploads/slips</c> เดิม แต่<b>ไม่เสิร์ฟตรงแล้ว</b> (ฝ่ายค้านรอบ 219 เฟส 2 S9 — ถอดจาก publicUploadPrefixes ·
/// เปิดผ่าน endpoint ที่ตรวจสิทธิ์ด้วย <see cref="OpenStored"/> เท่านั้น · ไฟล์เดิมอยู่ที่เดิม จึงไม่ต้องย้ายไฟล์) — การย้ายไปที่เก็บไฟล์กลาง (กฎ #4 D)
/// ยังเป็นงานแยก (ต้องย้ายไฟล์เดิม + ตัวอัปโหลดของลิงก์ชำระไม่มีผู้ใช้ให้ผูก FK ผู้อัปโหลด)</para>
/// <para>ทีม FS 2026-11-02: ไบต์ของสลิปอยู่ใน<b>ที่เก็บกลาง</b> (<see cref="Accounting.Services.Storage.IFileStorage"/> · Local/S3) ที่ตำแหน่งเดิม —
/// เขียนผ่าน <c>IImageProcessingService</c> · อ่าน/ลบ/เสิร์ฟผ่านที่เก็บตัวเดียวกัน (<see cref="StoredSlip.Storage"/> · <see cref="ServeAsync"/>) ไม่แตะดิสก์ตรง</para>
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
    public sealed record StoredSlip(string FileName, string OriginalFileName, string ContentType, long Size, string StoragePath, string AbsolutePath,
        Accounting.Services.Storage.IFileStorage? Storage = null);

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
            Path.Combine("uploads", "slips", fileName), Path.Combine(dir, fileName), images.Storage);
    }

    /// <summary>ฝ่ายค้านรอบสุดท้าย 5: ลบไฟล์ที่เก็บไว้เมื่อสร้างรายการไม่สำเร็จ (ไม่ทิ้งไฟล์กำพร้าที่มี PII) — ลบไม่ได้ = ปล่อย (ไม่กลบ error เดิมของผู้เรียก)</summary>
    public static void DeleteStored(StoredSlip slip)
    {
        // sync เพราะผู้เรียกทุกรายอยู่ใน catch { …; throw; } — รอการลบจบก่อนโยน error เดิมต่อ
        try { slip.Storage?.DeleteAsync(slip.AbsolutePath).GetAwaiter().GetResult(); }
        catch (IOException) { /* ไฟล์ถูกล็อก/ที่เก็บไม่ตอบ (FileStorageException) — งานเก็บกวาดภายหลัง · error เดิมของผู้เรียกสำคัญกว่า */ }
        catch (UnauthorizedAccessException) { /* เช่นเดียวกัน */ }
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
                if (slip.Storage != null && await slip.Storage.ReadAllBytesAsync(slip.AbsolutePath) is { } slipBytes)
                    await slipOcr.ParseAndStoreAsync(paymentId, slipBytes, slip.ContentType);
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

    /// <summary>ฝ่ายค้าน S9: ตำแหน่งไฟล์สลิปที่เก็บไว้ (ชื่อไฟล์ หรือ URL เดิม "/uploads/slips/x") — เอาเฉพาะชื่อไฟล์ (กัน path traversal) ·
    /// null = ชื่อใช้ไม่ได้ · <b>ไม่ตรวจว่าไฟล์มีจริง</b> (ทีม FS 2026-11-02 — ไฟล์อาจอยู่บน object storage) ⇒ เสิร์ฟด้วย <see cref="ServeAsync"/>
    /// ซึ่งคืน null เมื่อไม่มีไฟล์ · ผู้เรียกต้องตรวจสิทธิ์ก่อนเสมอ</summary>
    public static (string Path, string ContentType)? OpenStored(IWebHostEnvironment env, string? fileNameOrUrl)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrUrl)) return null;
        var name = Path.GetFileName(fileNameOrUrl.Replace('\\', '/').Split('?')[0]);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) return null;
        var full = Path.Combine(SlipDirectory(env), name);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var type = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
        return (full, type);
    }

    /// <summary>เสิร์ฟสลิป/หนังสือรับรองที่เก็บไว้จากที่เก็บกลาง (Local = PhysicalFile ตามเดิม · S3 = สตรีม) — null = ไม่มีไฟล์ (ผู้เรียกตอบ 404 ของตัวเอง)</summary>
    public static Task<Microsoft.AspNetCore.Mvc.IActionResult?> ServeAsync(Accounting.Services.Storage.IFileStorage storage,
        (string Path, string ContentType) file, string? downloadName)
        => Accounting.Services.Storage.FileStorageResults.FileResultAsync(storage, file.Path, file.ContentType, downloadName);
}

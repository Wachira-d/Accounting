namespace Accounting.Helpers;

/// <summary>
/// <b>หลักฐาน (สลิป/ใบเสร็จ) ของรายการเงินสดย่อย — กติกาตัวเดียว</b> (2026-11-02 · erp-review/2026-11-01/PETTY_CASH_AUDIT.md ส่วน C)
///
/// <para>═══ ที่มา ═══ เจ้าของขอ: เติมเงินสดย่อยแนบสลิปโอนได้ (รูป/PDF · ไม่บังคับเป็นค่าตั้งต้น · บริษัทตั้งให้บังคับได้) และการเบิกแนบใบเสร็จได้
/// ด้วยกลไกเดียวกัน · เดิมเงินสดย่อยมีแค่ช่องพิมพ์ "เลขที่ใบเสร็จ" — ไม่มีไฟล์หลักฐานเลย ทั้งเติมและเบิก</para>
///
/// <para>ไฟล์เก็บผ่าน <c>IFileAttachmentService</c> (ที่เก็บไฟล์แนบกลางตัวเดียวกับเอกสาร/ใบเบิก) ชนิดเจ้าของ <see cref="EntityType"/> ·
/// ดู/ดาวน์โหลดผ่าน <c>GET attachments/{id}/download</c> (ด่าน <c>AttachmentPermissionScope</c> · ตรวจบริษัท) · ลบจากรายการ = เก็บไฟล์จริงไว้
/// (หลักฐานบัญชี 5 ปี — <c>AttachmentRetention</c>) · ชนิดไฟล์ตัดสินจาก<b>ไบต์จริง</b> (<see cref="UploadFileType.SniffAttachment"/>) ไม่ใช่นามสกุล</para>
/// </summary>
public static class PettyCashEvidence
{
    /// <summary>ค่า <c>FileAttachments.EntityType</c> ของหลักฐานเงินสดย่อย (เจ้าของ = <c>PettyCashTransactions.Id</c>)</summary>
    public const string EntityType = "PettyCashTransaction";

    public const string RuleRequired = "PETTY-TOPUP-EVIDENCE-REQUIRED";
    public const string RuleBadFile = "PETTY-EVIDENCE-FILE";

    /// <summary>ขนาดสูงสุดของไฟล์หลักฐาน (เท่ารูปของที่เก็บไฟล์กลาง)</summary>
    public const long MaxBytes = 10L * 1024 * 1024;

    /// <summary>นามสกุล (จากไบต์จริง) ที่รับ — รูปภาพและ PDF ที่ที่เก็บไฟล์กลางรับด้วย (ชนิดที่ที่เก็บไม่รับ = ปฏิเสธที่นี่ ไม่ปล่อยไปล้มข้างใน)</summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

    public const string RequiredMessage =
        "บริษัทตั้งให้ “เติมเงินสดย่อย” ต้องแนบสลิป/หลักฐานการโอนทุกครั้ง — แนบไฟล์รูปหรือ PDF ในช่อง “สลิป/หลักฐาน” แล้วบันทึกอีกครั้ง "
        + "(ปิดการบังคับได้ที่หน้า ตั้งค่า ช่อง “เติมเงินสดย่อยต้องแนบสลิป” ใต้ “รหัสผังบัญชีเงินสด/ธนาคาร”)";

    /// <summary>ตรวจไฟล์หลักฐาน — คืนชนิดจริงจากไบต์ หรือข้อความไทยที่บอกทางไปต่อ · รับเฉพาะรูปภาพและ PDF</summary>
    public static (UploadFileKind? Kind, string? Error) Check(ReadOnlySpan<byte> content, string? clientFileName)
    {
        if (content.Length == 0) return (null, "ไฟล์หลักฐานว่าง — เลือกไฟล์รูปหรือ PDF ใหม่");
        if (content.Length > MaxBytes)
            return (null, $"ไฟล์หลักฐานใหญ่เกิน {MaxBytes / (1024 * 1024)} MB — ย่อรูป/บีบ PDF แล้วแนบใหม่");
        var head = content.Length > UploadFileType.HeaderBytes ? content[..UploadFileType.HeaderBytes] : content;
        var kind = UploadFileType.SniffAttachment(head, clientFileName);
        if (kind is not { } k || !AllowedExtensions.Contains(k.Extension))
            return (null, "หลักฐานเงินสดย่อยรับเฉพาะรูปภาพ (JPG/PNG/WEBP …) หรือ PDF — ไฟล์นี้ไม่ใช่ชนิดดังกล่าว");
        return (k, null);
    }

    /// <summary>ชื่อไฟล์ที่ผู้ใช้เห็น = ชื่อเดิม + นามสกุลตามไบต์จริง (ไม่เชื่อนามสกุลของ client)</summary>
    public static string DisplayName(string? clientFileName, string extension)
    {
        var baseName = Path.GetFileNameWithoutExtension(clientFileName ?? "");
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "หลักฐานเงินสดย่อย";
        if (baseName.Length > 120) baseName = baseName[..120];
        return baseName + extension;
    }
}

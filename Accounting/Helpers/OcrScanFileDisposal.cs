using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ลบสแกนแล้วไฟล์ต้นฉบับของมันต้องทำอย่างไร</summary>
public enum ScanFileDisposalAction
{
    /// <summary>ไม่แตะไฟล์เลย — ไฟล์เป็นของรายการอื่น หรือสแกนอื่นยังใช้ไฟล์นี้อยู่</summary>
    Leave = 0,
    /// <summary>ถอดแถวไฟล์ออกจากรายการ (soft-delete) แต่เก็บไฟล์จริงไว้ตามระยะเก็บรักษา</summary>
    SoftDeleteKeepBytes,
    /// <summary>ลบทั้งแถวและไฟล์จริง — เฉพาะไฟล์ที่ไม่ใช่หลักฐานประกอบรายการบัญชี</summary>
    Remove,
}

/// <summary>
/// **ลบสแกนแล้วไฟล์ต้นฉบับต้องเป็นอย่างไร** — ตัวตัดสินตัวเดียวของ <c>OcrService.DeleteScanAsync</c>
///
/// <para>═══ ที่มา (ฝ่ายค้านรอบ 193 · S2-C1) ═══ <c>DeleteScanAsync</c> ลบไฟล์จริงบนดิสก์และแถว <c>FileAttachments</c> ของ
/// <c>scan.FileAttachmentId</c> เสมอ โดยไม่ดูว่าไฟล์เป็นของใคร ⇒ สแกนไฟล์แนบของรายการอื่น (<c>POST ocr/scan/{fileId}</c> ·
/// สลิปเงินเดือน · ใบเสร็จนำส่ง · ไฟล์ของเอกสารที่อนุมัติแล้ว) แล้วลบสแกน = ลบหลักฐานของรายการนั้นถาวร ข้ามระยะเก็บรักษา
/// (พ.ร.บ.การบัญชี ม.10 · §87/3) · และ retry สร้างแถวใหม่ที่ชี้ไฟล์เดิม ⇒ ลบแถวหนึ่งทำให้อีกแถวเปิดรูปไม่ได้</para>
///
/// <para>กติกา: (1) สแกนอื่นยังชี้ไฟล์นี้ → ไม่แตะ · (2) ไฟล์เป็นของเอกสารที่ถูกลบพร้อมสแกน (cascade) → ตาม
/// <see cref="AttachmentRetention.MustKeepPhysicalFile"/> ของเอกสารนั้น · (3) ไฟล์เป็นของรายการอื่น (เอกสารใบอื่น ·
/// ชนิดอื่นทุกชนิด · ชนิดที่ไม่รู้จัก) → ไม่แตะ · (4) ไฟล์ของสแกนเอง (<c>OcrScan</c>) → ตามระยะเก็บรักษา</para>
/// </summary>
public static class OcrScanFileDisposal
{
    public static ScanFileDisposalAction Decide(string? fileEntityType, Guid fileEntityId,
        bool usedByOtherScan, Guid? cascadedDocumentId, DocumentStatus? cascadedDocumentStatus)
    {
        if (usedByOtherScan) return ScanFileDisposalAction.Leave;
        var ft = (fileEntityType ?? "").Trim();
        if (string.Equals(ft, "Document", StringComparison.OrdinalIgnoreCase))
        {
            if (cascadedDocumentId is not { } docId || docId != fileEntityId) return ScanFileDisposalAction.Leave;
            return AttachmentRetention.MustKeepPhysicalFile("Document", cascadedDocumentStatus)
                ? ScanFileDisposalAction.SoftDeleteKeepBytes
                : ScanFileDisposalAction.Remove;
        }
        if (string.Equals(ft, "OcrScan", StringComparison.OrdinalIgnoreCase))
            return AttachmentRetention.MustKeepPhysicalFile("OcrScan", null)
                ? ScanFileDisposalAction.SoftDeleteKeepBytes
                : ScanFileDisposalAction.Remove;
        return ScanFileDisposalAction.Leave;
    }
}

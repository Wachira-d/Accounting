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
/// <see cref="AttachmentRetention.MustKeepPhysicalFile"/> ของเอกสารนั้น · (3) ไฟล์เป็นของรายการอื่นที่ยังอยู่ (เอกสารใบอื่น ·
/// ชนิดอื่นทุกชนิด · ชนิดที่ไม่รู้จัก) → ไม่แตะ · (4) ไฟล์ของสแกนเอง (<c>OcrScan</c>) หรือไฟล์ที่ยังชี้เอกสารร่างที่ถูกลบไปแล้ว
/// (ฝ่ายค้านรอบสอง R2-C1) → ตาม <see cref="AttachmentRetention.ScanFilePurgeable"/> (R2-C4 — สแกนที่ถูกลบได้ไม่เคยเป็นรายการ
/// บัญชี: สแกนที่ลง JE ลบไม่ได้ `OCR-DELETE-HAS-JE` · สแกนที่ผูกเอกสารต้อง cascade ⇒ ลบจริง ไม่ใช่ค้างดิสก์ตลอดไป)</para>
/// </summary>
public static class OcrScanFileDisposal
{
    /// <param name="fileOwnerExists">เอกสารที่ไฟล์ชี้อยู่ยังมีไหม (ใช้เฉพาะไฟล์ชนิด Document —
    /// <see cref="AttachmentPermissionScope.ScanFileOwnerNeedsExistenceCheck"/>)</param>
    public static ScanFileDisposalAction Decide(string? fileEntityType, Guid fileEntityId,
        bool usedByOtherScan, Guid? cascadedDocumentId, DocumentStatus? cascadedDocumentStatus, bool fileOwnerExists = true)
    {
        if (usedByOtherScan) return ScanFileDisposalAction.Leave;
        var ft = (fileEntityType ?? "").Trim();
        if (string.Equals(ft, "Document", StringComparison.OrdinalIgnoreCase))
        {
            if (cascadedDocumentId is { } docId && docId == fileEntityId)
                return AttachmentRetention.MustKeepPhysicalFile("Document", cascadedDocumentStatus)
                    ? ScanFileDisposalAction.SoftDeleteKeepBytes
                    : ScanFileDisposalAction.Remove;
            // เอกสารเจ้าของหายไปแล้ว (ร่างถูกลบ — DeleteDocumentAsync ไม่แตะแถวไฟล์) ⇒ ไฟล์กลับเป็นของสแกน
            if (!fileOwnerExists) return ScanFileDisposalAction.Remove;
            return ScanFileDisposalAction.Leave;
        }
        if (string.Equals(ft, "OcrScan", StringComparison.OrdinalIgnoreCase))
        {
            // relink พลาด: ไฟล์ยังเป็นของสแกนแต่เอกสารที่ลบพร้อมกันเป็นใบที่ต้องเก็บ → เก็บตามเอกสารนั้น
            if (cascadedDocumentId != null && AttachmentRetention.MustKeepPhysicalFile("Document", cascadedDocumentStatus))
                return ScanFileDisposalAction.SoftDeleteKeepBytes;
            return AttachmentRetention.ScanFilePurgeable(ft, linkedToEntry: false)
                ? ScanFileDisposalAction.Remove
                : ScanFileDisposalAction.SoftDeleteKeepBytes;
        }
        return ScanFileDisposalAction.Leave;
    }
}

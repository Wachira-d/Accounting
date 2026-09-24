using Accounting.Helpers;

namespace Accounting.Services.Interfaces;

/// <summary>
/// **ด่านเดียวของไฟล์แนบทุกทางเข้า** — ตาราง "ชนิด → คีย์" อยู่ที่ <see cref="AttachmentPermissionScope"/> ·
/// ตัวนี้ทำส่วนที่ต้องแตะฐานข้อมูล (ค้นแถวเจ้าของ · ชั้นความลับ · ฝั่งเอกสาร · สแกนผูกกับเอกสารไหน)
///
/// <para>═══ ที่มา (ฝ่ายค้านรอบ 193 · C2/C3) ═══ รอบ 193 U2 ใส่ด่านไว้เป็นเมธอด private ใน <c>FileAttachmentController</c>
/// ⇒ ทางเข้าอื่นที่อ่าน/เขียนไฟล์ชุดเดียวกัน (<c>StatutoryRemittanceController.UploadReceipt</c> ·
/// <c>OcrController</c> รูปสแกน/รายการสแกน) เรียกไม่ได้ จึงไม่มีด่านเลย · ย้ายออกมาเป็น service ให้ทุกทางเข้าเรียกตัวเดียวกัน
/// (F2 ข้อ 4 "ตัวตั้งตัวเดียว") · <c>tools/attachment_gate_check.py</c> ตรวจว่าทุก action ที่แตะไฟล์เรียกตัวนี้จริง</para>
/// </summary>
public interface IAttachmentAccessGate
{
    /// <summary>ด่านของไฟล์แนบชนิด <paramref name="entityType"/> ของแถว <paramref name="entityId"/> — <c>null</c> = ผ่าน</summary>
    /// <param name="attachmentId">ไฟล์ตัวที่กำลังเปิด/ลบ (ถ้ารู้) — ใช้กับชนิด <c>OcrScan</c> (หาสแกนที่ชี้ไฟล์นี้) และ
    /// ไฟล์ในถังก่อนบันทึก (ตรวจผู้อัปโหลด)</param>
    Task<AttachmentDenial?> DenyAttachmentAsync(Guid companyId, Guid userId, string? entityType, Guid entityId,
        AttachmentAccess access, string verb, Guid? attachmentId = null);

    /// <summary>ด่านของสแกนหนึ่งใบ (รูปต้นฉบับ · ผลอ่าน · การแก้/ลบ) — ตามเจ้าของไฟล์ (<see cref="AttachmentPermissionScope.ScanOwner"/>) ·
    /// ไม่พบสแกน = <c>null</c> (ให้เส้นนั้นตอบ 404 เอง)</summary>
    /// <param name="unlinkedEditIsMemberLevel">แก้สแกนที่ยังไม่ผูกจากหน้ารีวิว = ระดับสมาชิก (พฤติกรรมเดิม)</param>
    Task<AttachmentDenial?> DenyScanAsync(Guid companyId, Guid userId, Guid scanId, AttachmentAccess access, string verb,
        bool unlinkedEditIsMemberLevel = false);

    /// <summary>ผู้ใช้สแกนไฟล์แนบตัวนี้ (<c>POST ocr/scan/{fileId}</c>) ได้ไหม — ไฟล์ของรายการอื่นต้องอ่านไฟล์นั้นได้ ·
    /// ไฟล์ของสแกนเดิมต้องผ่านด่านของสแกนเดิม · ไม่พบไฟล์ = 404 (ฝ่ายค้านรอบ 193 · S2-C1)</summary>
    Task<AttachmentDenial?> DenyScanSourceAsync(Guid companyId, Guid userId, Guid fileAttachmentId);

    /// <summary>สแกนในชุดนี้ใบไหนที่ผู้ใช้<b>อ่านไม่ได้</b> (สำหรับเส้นรายการ — คิวรีเดียวต่อชุด ไม่ใช่ต่อใบ)</summary>
    Task<HashSet<Guid>> HiddenScanIdsAsync(Guid companyId, Guid userId, IReadOnlyCollection<Guid> scanIds);

    /// <summary>เอกสารในชุดนี้ใบไหนที่ผู้ใช้<b>อ่านไม่ได้</b> (ฝั่ง + ชั้นความลับ) — ใช้กรองรายการเอกสารที่เส้น OCR คืน
    /// (PO เปิด · ใบต้นทางที่น่าจะเป็น · รายงานตรวจยอด) ให้ตรงกับที่ผู้ใช้เห็นในหน้าเอกสาร</summary>
    Task<HashSet<Guid>> HiddenDocumentIdsAsync(Guid companyId, Guid userId, IReadOnlyCollection<Guid> documentIds);
}

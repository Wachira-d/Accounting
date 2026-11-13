using Accounting.Models.DTOs.Document;

namespace Accounting.Services.Interfaces;

/// <summary>
/// รอบ 266 แพ็ก A — "เลือกผูกใบเสร็จรับเงิน/เงินที่รับไว้แล้วกับใบแจ้งหนี้เอง (เจ้าเดียวกัน)" ทั้งจากหน้าใบแจ้งหนี้และหน้าใบเสร็จ ·
/// ตัวตัดสินเดียว <c>Helpers/ReceiptLinkPolicy</c> · เส้นที่เปิด: ใบเสร็จร่าง (ตั้งใบต้นทาง) · เงินรอจัดสรร (<c>CustomerCreditService.AllocateAsync</c>) ·
/// มัดจำ (<c>ApplyDepositToInvoiceAsync</c>) · ย้ายการรับชำระ = แพ็ก B · ใบขายสด = ปฏิเสธพร้อมทางไปต่อไปตัวช่วยใบซ้ำ (แพ็ก C · <c>IDuplicateSaleService</c>)
/// </summary>
public interface IReceiptLinkService
{
    /// <summary>ผู้สมัครของใบที่เปิดอยู่ (ใบแจ้งหนี้ ⇒ ใบเสร็จร่าง/เงินรอจัดสรร/มัดจำ/ใบเสร็จอื่นของเจ้าเดียวกัน · ใบเสร็จ ⇒ ใบค้างของเจ้าเดียวกัน) ทุกแถวมีผลตัดสิน</summary>
    Task<ReceiptLinkCandidatesResponse> CandidatesAsync(Guid companyId, Guid fromDocumentId, Guid userId, CancellationToken ct = default);

    /// <summary>อ่านอย่างเดียว: ผลตัดสิน + ผลที่จะเกิด + กุญแจพรีวิว</summary>
    Task<ReceiptLinkPreviewResponse> PreviewAsync(Guid companyId, ReceiptLinkRequest request, Guid userId, CancellationToken ct = default);

    /// <summary>ผูกจริง — ตัดสินซ้ำ + เทียบกุญแจพรีวิว (เปลี่ยน = 409 RL-STALE) แล้วส่งต่อไปเส้นเดิม · audit <c>DOC-RCPT-LINK</c></summary>
    Task<ReceiptLinkResult> LinkAsync(Guid companyId, ReceiptLinkRequest request, Guid userId, CancellationToken ct = default);

    /// <summary>ถอดการผูก (ใบเสร็จร่าง · เงินรอจัดสรร) — มัดจำไม่มีทางถอดในรุ่นนี้ (ปฏิเสธพร้อมทางไปต่อ)</summary>
    Task<ReceiptLinkResult> UndoAsync(Guid companyId, ReceiptLinkUndoRequest request, Guid userId, CancellationToken ct = default);
}

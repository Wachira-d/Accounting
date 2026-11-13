using Accounting.Models.DTOs.Document;

namespace Accounting.Services.Interfaces;

/// <summary>
/// รอบ 266 แพ็ก C — ตัวช่วย "ใบซ้ำ" (ใบขายสดกับใบแจ้งหนี้ของการขายเดียวกัน · คำตัดสิน 266-1-5 · 266-1-36…38) · ตัวตัดสินเดียว <c>Helpers/DuplicateSalePolicy</c> ·
/// ทุกเส้นเป็นเส้นเดิม: ยกเลิกเอกสาร (<c>VoidDocumentAsync</c>) · ใบลดหนี้ฉบับร่าง (<c>ConvertDocumentAsync</c>) · บันทึกรับชำระ (<c>CreatePaymentAsync</c>)
/// </summary>
public interface IDuplicateSaleService
{
    /// <summary>คู่ใบที่อาจซ้ำของใบที่เปิดอยู่ (ใบขายสด ⇒ ใบแจ้งหนี้ · ใบแจ้งหนี้ ⇒ ใบขายสด) — ผู้ติดต่อเดียวกัน · ยอดเท่ากัน · ใบลับที่ผู้ใช้ไม่เห็นไม่ปรากฏ</summary>
    Task<DuplicateSaleCandidatesResponse> CandidatesAsync(Guid companyId, Guid documentId, Guid userId, CancellationToken ct = default);

    /// <summary>อ่านอย่างเดียว: ผลตัดสินของทั้งสองตัวเลือก (คงใบขายสด / คงใบแจ้งหนี้) + ผลที่จะเกิด + กุญแจพรีวิว</summary>
    Task<DuplicateSalePreviewResponse> PreviewAsync(Guid companyId, Guid cashSaleId, Guid invoiceId, Guid userId, CancellationToken ct = default);

    /// <summary>ทำตามตัวเลือก — ตัดสินซ้ำ + เทียบกุญแจพรีวิว (เปลี่ยน = 409 DS-STALE) · audit <c>DOC-DUP-SALE</c></summary>
    Task<DuplicateSaleResult> ExecuteAsync(Guid companyId, DuplicateSaleRequest request, Guid userId, CancellationToken ct = default);
}

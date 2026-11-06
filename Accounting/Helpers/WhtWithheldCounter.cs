using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ภาษีหัก ณ ที่จ่ายที่ "หักไปแล้ว" ของใบหนึ่ง — ตัวนับเดียว** (ย้ายจาก <c>DocumentService.WhtAlreadyWithheldAsync</c> · รอบ 241 ฝ่ายค้าน B1)
/// = ทางบันทึกชำระ (<c>Payments</c> ที่ยังมีผล) + ทางแปลงเป็นใบเสร็จ/ใบสำคัญรับ/ใบสำคัญจ่ายที่อ้างใบนี้ (ไม่นับร่าง/ยกเลิก/ปฏิเสธ)
/// <para>ผู้เรียก: <c>DocumentService</c> (ยอดแนะนำ/เพดานตอนบันทึกชำระ · คำเตือนก่อนอนุมัติ) · <c>DocumentPayLinkService</c> (ยอดสุทธิของลิงก์ชำระ) ·
/// <c>DocumentPaymentHandler</c> (WHT ที่ต้องบันทึกเมื่อเงินเข้าผ่านลิงก์) — ตัวเลขสามที่ต้องเป็นตัวเดียวกัน ไม่งั้นลิงก์เก็บยอดสุทธิที่ตัวบันทึกชำระปฏิเสธ (เกินเพดาน)</para>
/// </summary>
public static class WhtWithheldCounter
{
    private static readonly DocumentType[] SettlementTypes = { DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.PaymentVoucher };

    /// <param name="excludeDocumentId">ใบที่ไม่นับ (ใบสำคัญจ่ายที่กำลังตรวจก่อนอนุมัติ)</param>
    public static async Task<decimal> AlreadyAsync(AccountingDbContext db, Guid companyId, Guid documentId,
        Guid? excludeDocumentId = null, CancellationToken ct = default)
    {
        var withheldViaPayments = await db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.DocumentId == documentId && !p.IsDeleted)
            .SumAsync(p => (decimal?)p.WithholdingTaxAmount, ct) ?? 0m;
        var withheldViaReceipts = await db.Documents.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.RelatedDocumentId == documentId
                && (excludeDocumentId == null || r.Id != excludeDocumentId)
                && SettlementTypes.Contains(r.DocumentType)
                && r.Status != DocumentStatus.Voided
                && r.Status != DocumentStatus.Draft
                && r.Status != DocumentStatus.Rejected
                && !r.IsDeleted)
            .SumAsync(r => (decimal?)r.WithholdingTaxAmount, ct) ?? 0m;
        return withheldViaPayments + withheldViaReceipts;
    }
}

using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 273 — ตัวโหลด<b>ตัวเดียว</b>ของ <see cref="Document.SettlementTotals"/> (ใบเสร็จรับชำระที่ไม่ถือ VAT ของใบต้นทาง) ใช้ร่วมกันโดย
/// <c>PdfGenerationService.ResolveServedAsReceiptAsync</c> (HTML + QuestPDF + หัวเอกสารของช่องทางอีเมล/LINE) และ
/// <c>DocumentService.GetDocumentAsync</c> (หน้ารายละเอียด) — ห้ามมีสำเนาเงื่อนไขที่สอง (กฎเหล็ก #4 A · ตัวตัดสินอยู่ที่ <see cref="SettlementReceiptTotals"/>)
/// <para>ใบต้นทาง = <c>RelatedDocumentId</c> (ทุกทางเข้าที่ออกใบเสร็จรับชำระ — บันทึกรับชำระ · ออกใบย้อนหลัง/รายใบของการรับหลายใบ · ช่องทางชำระออนไลน์ ·
/// สลิป LINE · คู่ค้า · ย้ายการรับชำระ · ออกใบแทน — ล้วนผ่าน <c>CreateSettlementReceiptAsync</c> ที่ตั้งช่องนี้ · ใบเสร็จที่ผู้ใช้สร้างจากฟอร์ม "ผูกกับเอกสารต้นทาง" ก็ตั้งช่องนี้)</para>
/// </summary>
internal static class SettlementReceiptTotalsLoader
{
    /// <summary>เติม <see cref="Document.SettlementTotals"/> (null เมื่อไม่เข้า) · tenant: ใบต้นทางต้องเป็นบริษัทเดียวกัน</summary>
    public static async Task ResolveAsync(AccountingDbContext db, Guid companyId, Document doc)
    {
        doc.SettlementTotals = null;
        if (!SettlementReceiptTotals.IsReceiptType(doc.DocumentType) || doc.RelatedDocumentId is not Guid srcId) return;
        // เงื่อนไข "ใบถือ VAT เอง" อยู่ที่ตัวตัดสินตัวเดียว (ไม่คัดลอกมากรองล่วงหน้าที่นี่ — กัน drift)
        // ใบต้นทางอาจถูกยกเลิก/ซ่อนแล้ว — อ่านข้าม query filter ให้เลขที่ยังพิมพ์ได้ (เหมือน ResolveReplacementReferenceAsync)
        var src = await db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.Id == srcId && d.CompanyId == companyId)
            .Select(d => new SettlementReceiptTotals.Source(d.DocumentType, d.DocumentNumber, d.DocumentDate,
                d.SubTotal, d.VatAmount, d.WithholdingTaxAmount, d.TotalAmount))
            .FirstOrDefaultAsync();
        if (src == null) return;
        doc.SettlementTotals = SettlementReceiptTotals.Decide(new SettlementReceiptTotals.Facts(
            doc.DocumentType, doc.IsTaxInvoiceByLaw == true, doc.IsDeposit, doc.VatAmount,
            doc.WithholdingTaxAmount, doc.TotalAmount, new[] { src }));
    }
}

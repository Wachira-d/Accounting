using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ชื่อชนิดเอกสาร (ไทย/อังกฤษ) ตามหัวกระดาษมาตรฐาน — <b>ตารางเดียว</b>ฝั่งเซิร์ฟเวอร์
///
/// <para>═══ ที่มา (รอบ 196 · ทีม Q) ═══ ตารางนี้เคยเป็น <c>PdfGenerationService.GetDocumentTitle</c> (private) ⇒
/// ข้อความอื่นที่ต้องเอ่ยชื่อชนิดเอกสาร (ป้าย "✓ ออกใบแจ้งหนี้ INV-… แล้ว" บนหน้ารวม) ต้องพิมพ์ตารางที่สองเอง
/// (ERP_REVIEW A-10: ชื่อชนิดเอกสารกระจาย ≥ 8 จุดใน C#). ย้ายออกมาให้ทั้ง renderer และป้าย lifecycle เรียกตัวเดียว —
/// หัวที่ผู้ใช้ตั้งเอง/หัวรวม/อย่างย่อ ยังเป็นงานของ <c>PdfGenerationService.ComputeDocumentTitle</c> (ตัวนี้คือ "ชื่อชนิด" เปล่า)</para>
/// </summary>
public static class DocumentTypeNames
{
    /// <summary>ชื่อชนิดเอกสาร — <paramref name="lang"/> = "en" ⇒ อังกฤษ · อื่น ๆ (รวม null) ⇒ ไทย</summary>
    public static string Title(DocumentType type, string? lang) => lang == "en" ? type switch
    {
        DocumentType.Quotation => "Quotation",
        DocumentType.Invoice => "Invoice",
        DocumentType.Receipt => "Receipt",
        DocumentType.TaxInvoice => "Tax Invoice",
        DocumentType.DebitNote => "Debit Note",
        DocumentType.CreditNote => "Credit Note",
        DocumentType.DeliveryNote => "Delivery Note",
        DocumentType.BillingNote => "Billing Note",
        DocumentType.ReceiptVoucher => "Receipt Voucher",
        DocumentType.PurchaseRequisition => "Purchase Requisition",
        DocumentType.PurchaseOrder => "Purchase Order",
        DocumentType.GoodsReceiptNote => "Goods Receipt Note",
        DocumentType.PurchaseInvoice => "Purchase Invoice",
        DocumentType.Expense => "Expense Record",
        DocumentType.PaymentVoucher => "Payment Voucher",
        DocumentType.CertificateInLieu => "Certificate in Lieu of Receipt",
        _ => "Document"
    } : type switch
    {
        DocumentType.Quotation => "ใบเสนอราคา",
        DocumentType.Invoice => "ใบแจ้งหนี้",
        DocumentType.Receipt => "ใบเสร็จรับเงิน",
        DocumentType.TaxInvoice => "ใบกำกับภาษี",
        DocumentType.DebitNote => "ใบเพิ่มหนี้",
        DocumentType.CreditNote => "ใบลดหนี้",
        DocumentType.DeliveryNote => "ใบส่งของ",
        DocumentType.BillingNote => "ใบวางบิล",
        DocumentType.ReceiptVoucher => "ใบสำคัญรับ",
        DocumentType.PurchaseRequisition => "ใบขอซื้อ",
        DocumentType.PurchaseOrder => "ใบสั่งซื้อ",
        DocumentType.GoodsReceiptNote => "ใบรับสินค้า",
        DocumentType.PurchaseInvoice => "ใบแจ้งหนี้ซื้อ",
        DocumentType.Expense => "ใบบันทึกค่าใช้จ่าย",
        DocumentType.PaymentVoucher => "ใบสำคัญจ่าย",
        DocumentType.CertificateInLieu => "ใบรับรองแทนใบเสร็จรับเงิน",
        _ => "เอกสาร"
    };
}

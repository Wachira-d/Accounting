namespace Accounting.Services.Interfaces;

/// <summary>WP-B2: ออกเอกสารค่าบริการ SaaS (ใบเสร็จรับเงิน / ใบกำกับภาษี) ให้ลูกค้า
/// เมื่อการชำระเงินได้รับอนุมัติ. platform = ผู้ขาย, บริษัทลูกค้า = ผู้ซื้อ.
/// ออกใบกำกับภาษี §86/4 เฉพาะเมื่อ platform จด VAT (SiteSettings) ไม่งั้นออก
/// ใบเสร็จรับเงินธรรมดา — กัน compliance ผิดจากการอ้าง VAT ทั้งที่ไม่ได้จด.</summary>
public interface ISaasBillingDocumentService
{
    /// <summary>gen PDF + เก็บเป็น FileAttachment ใต้บริษัทลูกค้า + stamp payment +
    /// อีเมลเจ้าของ (best-effort). idempotent — ถ้าออกใบแล้ว (ReceiptNumber != null) ข้าม.
    /// ห้าม throw ขึ้นไปทำให้ approval พัง — จับ error ภายใน.</summary>
    Task GenerateReceiptForApprovedPaymentAsync(Guid paymentId);

    /// <summary>ดึง PDF ที่ออกแล้วสำหรับดาวน์โหลด (regenerate ถ้าไฟล์หาย).</summary>
    Task<(byte[] Bytes, string FileName)?> GetReceiptPdfAsync(Guid paymentId);

    /// <summary>WP-B1: ออกใบแจ้งหนี้ต่ออายุล่วงหน้าก่อนหมดอายุ (SINV-YYYYMM-####)
    /// ยอดตาม PlanTemplate + BillingCycle ปัจจุบัน + เก็บ PDF + อีเมลลิงก์จ่าย.
    /// idempotent ต่อ EndDate ของรอบ — ไม่ throw. คืนเลขใบที่ออก (null = ไม่ได้ออก).</summary>
    Task<string?> GenerateRenewalInvoiceAsync(Guid subscriptionId);

    /// <summary>ดึง PDF ใบแจ้งหนี้ต่ออายุที่ออกแล้ว (สำหรับดาวน์โหลด).</summary>
    Task<(byte[] Bytes, string FileName)?> GetRenewalInvoicePdfAsync(Guid subscriptionId);
}

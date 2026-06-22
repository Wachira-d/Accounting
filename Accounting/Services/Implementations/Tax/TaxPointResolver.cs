using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Implementations.Tax;

/// <summary>
/// คำนวณ "จุดความรับผิดในการเสีย VAT" (tax point) ตาม ป.รัษฎากร §78/§78/1/§78/2.
/// VAT period ของ ภ.พ.30 ต้องใช้เดือนของ tax point ไม่ใช่ DocumentDate.
///
/// Pure function — ไม่แตะ DB. เรียกตอน approve เพื่อ snapshot TaxPointDate.
///
/// กฎ:
///   • ขายสินค้า §78        = MIN(DeliveryDate, OwnershipTransferDate, PaymentDate, IssueDate)
///   • บริการ §78/1         = MIN(PaymentDate, IssueDate, ServiceUsedDate)
///   • นำเข้า §78/2         = customsDutyPaidDate (ไม่ครอบคลุมที่นี่ — handled แยก)
///
/// "IssueDate" = DocumentDate (วันที่ออกเอกสาร/ใบกำกับของเรา). สำหรับเอกสารฝั่ง
/// ซื้อ (PurchaseInvoice/Expense) ใช้ SupplierTaxInvoiceDate ของผู้ขายเป็น issue
/// แทน (วันบนใบกำกับจริง) — fallback DocumentDate.
/// </summary>
public static class TaxPointResolver
{
    /// <summary>คืน tax point ตามชนิดเอกสาร. ถ้าไม่มี signal ใด ๆ เลย →
    /// fallback = issueDate (DocumentDate/SupplierTaxInvoiceDate).</summary>
    public static DateTime Resolve(Document doc)
    {
        // issue date — ใบกำกับของผู้ขาย (ซื้อ) หรือวันที่เอกสารเรา (ขาย)
        var issueDate = doc.SupplierTaxInvoiceDate ?? doc.DocumentDate;

        // เลือกชนิด tax point ตามว่าเป็นบริการหรือสินค้า — heuristic:
        // มี ServiceUsedDate = บริการ; ไม่งั้นถือเป็นสินค้า. (เอกสารส่วนใหญ่
        // ไม่ได้แยก goods/service ชัด — ใช้ field ที่ผู้ใช้กรอกเป็นตัวบอก)
        var candidates = new List<DateTime?>();

        if (doc.ServiceUsedDate.HasValue)
        {
            // §78/1 บริการ: MIN(PaymentDate, IssueDate, ServiceUsedDate)
            candidates.Add(doc.PaymentDate);
            candidates.Add(issueDate);
            candidates.Add(doc.ServiceUsedDate);
        }
        else
        {
            // §78 ขายสินค้า: MIN(DeliveryDate, OwnershipTransferDate, PaymentDate, IssueDate)
            candidates.Add(doc.DeliveryDate);
            candidates.Add(doc.OwnershipTransferDate);
            candidates.Add(doc.PaymentDate);
            candidates.Add(issueDate);
        }

        var valid = candidates.Where(d => d.HasValue).Select(d => d!.Value).ToList();
        return valid.Count > 0 ? valid.Min() : issueDate;
    }

    /// <summary>True เมื่อ tax point ตกต่างเดือนกับ DocumentDate — ใช้เตือน
    /// ผู้ใช้ว่า VAT จะเข้า ภ.พ.30 คนละเดือนกับที่บันทึกเอกสาร.</summary>
    public static bool DiffersFromDocumentMonth(Document doc, DateTime taxPoint)
        => taxPoint.Year != doc.DocumentDate.Year || taxPoint.Month != doc.DocumentDate.Month;
}

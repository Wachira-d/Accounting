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
///   • นำเข้า §78/2         = CustomsDutyPaidDate (วันชำระอากรขาเข้า — ใช้วันนี้
///                            **ตรง ๆ ไม่ใช่ MIN** ต่างจากอีกสองกรณี)
///
/// "IssueDate" = DocumentDate (วันที่ออกเอกสาร/ใบกำกับของเรา). สำหรับเอกสารฝั่ง
/// ซื้อ (PurchaseInvoice/Expense) ใช้ SupplierTaxInvoiceDate ของผู้ขายเป็น issue
/// แทน (วันบนใบกำกับจริง) — fallback DocumentDate.
/// </summary>
public static class TaxPointResolver
{
    /// <summary>ประเภทธุรกรรมสำหรับเลือกกฎ tax point</summary>
    public enum SupplyKind
    {
        /// <summary>ให้ระบบเดาจาก field ที่ผู้ใช้กรอก (พฤติกรรมเดิม)</summary>
        Auto = 0,
        /// <summary>ขายสินค้า §78</summary>
        Goods = 1,
        /// <summary>บริการ §78/1</summary>
        Service = 2,
        /// <summary>นำเข้า §78/2</summary>
        Import = 3,
    }

    /// <summary>คืน tax point ตามชนิดธุรกรรม
    ///
    /// <para><paramref name="kind"/> = Auto (ค่าเริ่มต้น) ให้ระบบเดาจาก field:
    /// มี CustomsDutyPaidDate → นำเข้า · มี ServiceUsedDate → บริการ · ที่เหลือ
    /// = สินค้า. การเดานี้ปลอดภัยเพราะ field ที่ไม่เกี่ยวจะเป็น null และไม่เข้า
    /// MIN อยู่แล้ว **ยกเว้นเคสเดียว**: เอกสารขายสินค้าที่ผู้ใช้ดันกรอก
    /// ServiceUsedDate ไว้ จะข้าม Delivery/OwnershipTransfer — caller ที่รู้
    /// ชนิดแน่นอนควรส่ง kind มาแทนการปล่อยให้เดา</para>
    ///
    /// <para>ถ้าไม่มี signal ใด ๆ เลย → fallback = issueDate</para></summary>
    public static DateTime Resolve(Document doc, SupplyKind kind = SupplyKind.Auto)
    {
        // issue date — ใบกำกับของผู้ขาย (ซื้อ) หรือวันที่เอกสารเรา (ขาย)
        var issueDate = doc.SupplierTaxInvoiceDate ?? doc.DocumentDate;

        var resolved = kind != SupplyKind.Auto ? kind
            : doc.CustomsDutyPaidDate.HasValue ? SupplyKind.Import
            : doc.ServiceUsedDate.HasValue ? SupplyKind.Service
            : SupplyKind.Goods;

        // §78/2 นำเข้า — จุดรับผิดคือ "วันชำระอากรขาเข้า" ตัวเดียว ไม่ใช่ MIN
        // ของหลายเหตุการณ์ (ถ้าไม่ได้กรอกวันไว้ ตกกลับไปใช้ issueDate)
        if (resolved == SupplyKind.Import)
            return doc.CustomsDutyPaidDate ?? issueDate;

        var candidates = new List<DateTime?>();
        if (resolved == SupplyKind.Service)
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

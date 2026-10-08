using Accounting.Models.Enums;
namespace Accounting.Models.DTOs.Document;

/// <summary>ใบค้างชำระของลูกค้าที่นำมารวมเป็นใบวางบิลได้ 1 แถว = 1 ใบ.</summary>
public record BillingNoteSourceItem(
    Guid Id,
    string DocumentNumber,
    string TypeLabel,                 // "ใบแจ้งหนี้" / "ใบกำกับภาษี" / "ใบเพิ่มหนี้"
    DateTime DocumentDate,
    DateTime? DueDate,
    decimal TotalAmount,
    decimal BalanceDue,               // ยอดที่จะเรียกเก็บ (คงค้าง ณ ตอนนี้)
    // เลขใบวางบิล active ที่รวมใบนี้ไว้แล้ว — UI ปิดติ๊ก + โชว์เลข (กันวางบิลซ้ำ)
    string? InBillingNoteNumber);

/// <summary>คำขอสร้างใบวางบิลจากใบค้างชำระหลายใบ (ลูกค้ารายเดียวกันทั้งชุด).</summary>
public record CreateBillingNoteFromInvoicesRequest(
    List<Guid> InvoiceIds,
    DateTime? DueDate = null,         // null = ใช้กำหนดชำระล่าสุดในชุด
    string? Notes = null);

/// <summary>ใบแจ้งหนี้/ใบกำกับในใบวางบิลรวม พร้อมยอดคงค้าง ณ ตอนนี้ — หน้า "รับชำระตามใบวางบิล" เปิดหน้าต่างบันทึกชำระเดิมของแต่ละใบ
/// (ค่าที่หน้าต่างนั้นต้องใช้มาจากเซิร์ฟเวอร์ทั้งหมด)</summary>
public record BillingNoteInvoiceItem(
    Guid Id,
    string DocumentNumber,
    DocumentType DocumentType,
    DocumentStatus Status,
    decimal TotalAmount,
    decimal BalanceDue,
    decimal WithholdingTaxAmount,
    decimal VatAmount,
    string Currency,
    decimal ExchangeRate);

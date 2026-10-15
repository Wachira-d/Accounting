using Accounting.Helpers;

namespace Accounting.Models.DTOs.Tax;

public record TaxFilingExportResult(
    string FormCode,
    string FormName,
    string FileName,
    string ContentType,
    byte[] FileData,
    int RecordCount,
    decimal TotalIncome,
    decimal TotalTax,
    string? Summary,
    // ภ.ง.ด.1/1ก: ยอดต่อประเภทเงินได้ 5 ข้อของหน้าแบบ (รอบ 2026-10-14 ทีม X1) — ตัวคำนวณเดียว Pnd1FileFormat.Summarize ·
    // หน้าเว็บ "แสดง" ไม่คิดเอง · แบบอื่น = null
    IReadOnlyList<Pnd1FileFormat.CategoryTotal>? Categories = null);

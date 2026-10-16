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
    IReadOnlyList<Pnd1FileFormat.CategoryTotal>? Categories = null,
    // ภ.ง.ด.1/1ก: ข้อความ .txt สำหรับ "ดูตัวอย่าง" — ช่อง 5 ปิดบังเมื่อผู้ขอไม่มีสิทธิ์ Pii.View (ฝ่ายค้าน ภ.ง.ด.1 P1-3 · เดิมหน้าเว็บ
    // ดึงไฟล์ยื่นจริงที่มีเลขบัตรเต็มมาแสดง) · แบบอื่น = null (หน้าเว็บดึงไฟล์ตามเดิม)
    string? PreviewText = null);

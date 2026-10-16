namespace Accounting.Models.DTOs.Ocr;

/// <summary>บรรทัดของสแกนที่ผลแกะรุ่น 6–8 ลงเป็น 1 × ยอด โดยไม่มีหลักฐานมิเตอร์ (<c>Helpers/OcrLumpSumRepair</c>)</summary>
public record OcrLumpSumLine(int LineNo, string? Description, decimal Amount, string? Suffix);

/// <summary>สแกนที่ต้องแกะใหม่ — <see cref="AlreadyMarked"/> = ซ่อมแล้ว (ติดแท็กบล็อกไว้แล้ว)</summary>
public record OcrLumpSumScan(
    Guid ScanId, string FileName, int? ExtractionVersion, DateTime CreatedAt, string? ExtractedDocumentNumber,
    List<OcrLumpSumLine> Lines, Guid? DocumentId, bool StockImported, bool AlreadyMarked);

/// <summary>เอกสารที่สร้างจากสแกนที่เสีย — ระบบไม่แก้เอง · <see cref="WayForward"/> = ทางไปต่อจากเซิร์ฟเวอร์</summary>
public record OcrLumpSumDocument(Guid DocumentId, Guid ScanId, string? DocumentNumber, string DocumentType, string Status, string WayForward);

/// <summary>การเคลื่อนไหวสต็อกที่มาจากสแกน/เอกสารที่เสีย — ระบบไม่แก้เอง</summary>
public record OcrLumpSumStockMovement(
    Guid MovementId, Guid ProductId, string? ProductCode, string? ProductName, decimal Quantity, decimal UnitCost,
    DateTime MovementDate, string? Reference, Guid? ScanId, Guid? DocumentId, string WayForward);

/// <summary>คีย์ที่ติดท้ายของตัวกัน (alias · คีย์ตัวเรียน · ชื่อสินค้า) — <see cref="Proposed"/> = คีย์ที่ตัดท้ายแล้ว</summary>
public record OcrLumpSumKey(Guid Id, string Current, string Proposed, DateTime CreatedAt, string? Owner, string? WayForward = null);

/// <summary>รายงานอ่านอย่างเดียว (ไม่มีงานไหนซ่อมอัตโนมัติ)</summary>
public record OcrLumpSumRepairReport(
    List<OcrLumpSumScan> Scans,
    List<OcrLumpSumDocument> Documents,
    List<OcrLumpSumStockMovement> StockMovements,
    List<OcrLumpSumKey> Aliases,
    List<OcrLumpSumKey> LearnerKeys,
    List<OcrLumpSumKey> Products)
{
    public int Total => Scans.Count(s => !s.AlreadyMarked) + Aliases.Count + LearnerKeys.Count;
}

/// <summary>แถวที่ผู้ใช้เลือกซ่อม (เฉพาะส่วนที่ปลอดภัย: สแกน · alias · คีย์ตัวเรียน)</summary>
public record OcrLumpSumRepairRequest(List<Guid>? ScanIds, List<Guid>? AliasIds, List<Guid>? LearnerKeyIds);

/// <summary>ผลต่อแถว — <see cref="Reason"/> = เหตุที่ข้าม (null = ซ่อมแล้ว)</summary>
public record OcrLumpSumRepairOutcome(string Kind, Guid Id, bool Repaired, string? Before, string? After, string? Reason);

public record OcrLumpSumRepairResult(int RepairedCount, int SkippedCount, List<OcrLumpSumRepairOutcome> Items);

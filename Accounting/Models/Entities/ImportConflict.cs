using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// Import duplicate conflict — per-row tracking ของการ import ที่ตรวจพบ
/// ว่ามีของซ้ำในระบบอยู่แล้ว. รอ user ตัดสินใจว่าจะใช้ของเดิม / ของใหม่ /
/// merge / skip.
///
/// ใช้กับทุก import flow:
///   - Smart Import (CSV/Excel column mapping)
///   - Bank statement import
///   - Fixed asset import
///   - OCR-based bulk import
///   - Cross-tenant document import
///   - Excel template import
///
/// Lifecycle:
///   1. Import service detect duplicate → INSERT ImportConflict (Resolution=Pending)
///   2. Import service หยุดที่ "Awaiting Resolution" status
///   3. User เปิด conflict resolver UI → choose per row
///   4. UI call ResolveAsync(conflictId, decision)
///   5. เมื่อ all resolved → user คลิก "Apply All" → import service ทำตาม
///      Resolution ของแต่ละแถว
/// </summary>
public class ImportConflict : TenantEntity
{
    /// <summary>FK to SmartImportSession (หรือ session อื่นที่อ้างอิงผ่าน
    /// SessionRef string สำหรับ flow ที่ไม่ใช้ SmartImportSession).</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Generic session reference ถ้า import flow ไม่ใช้
    /// SmartImportSession (e.g. "bank-stmt:bank-id-2025-03",
    /// "fixed-asset:upload-456").</summary>
    public string? SessionRef { get; set; }

    public int RowNumber { get; set; }
    public string EntityType { get; set; } = "";    // Contact | Product | Document | Employee | BankTxn | FixedAsset | JournalEntry

    /// <summary>Data ที่กำลังจะ import (JSON) — เก็บเป็น original shape
    /// ที่ user ส่งมา. ตอน Apply ระบบ deserialize + create entity.</summary>
    public string StagedDataJson { get; set; } = "{}";

    /// <summary>FK to existing entity ที่ match. NULL ถ้า duplicate detection
    /// คืน "เหมือนกันมากแต่ไม่ได้ link" — UI ใช้ ExistingDataJson แทน.</summary>
    public Guid? ExistingEntityId { get; set; }

    /// <summary>Snapshot ของ existing entity (JSON) สำหรับ side-by-side
    /// comparison ใน UI. ลบ link เปลี่ยน existing → still มีข้อมูลให้ดู.</summary>
    public string ExistingDataJson { get; set; } = "{}";

    /// <summary>0.0–1.0 — 1.0 = exact match (TaxId/Code) / 0.7+ = strong
    /// (name+amount fuzzy) / 0.5– = weak (need user judgment).</summary>
    public double MatchScore { get; set; }

    /// <summary>Why was this flagged? "Same TaxId" / "Same EmployeeCode" /
    /// "Vendor + Amount + Date within 30d" / etc.</summary>
    public string MatchReason { get; set; } = "";

    public ImportConflictResolution Resolution { get; set; } = ImportConflictResolution.Pending;
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }

    /// <summary>เมื่อ Resolution = Merge, ใส่ JSON ของฟิลด์ที่เลือกแต่ละ
    /// ตัว (existing vs staged). ถ้า Pending/UseExisting/UseNew/Skip ค่านี้
    /// null. รูปแบบ: {"fieldName": "existing"|"staged", ...}</summary>
    public string? MergeChoicesJson { get; set; }

    public string? UserNote { get; set; }
}

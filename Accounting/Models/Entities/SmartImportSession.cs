using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

public class SmartImportSession : TenantEntity
{
    public string EntityType { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string FileFormat { get; set; } = "csv";
    public bool HasHeaderRow { get; set; } = true;
    public ImportSessionStatus Status { get; set; } = ImportSessionStatus.Uploading;
    public int TotalRows { get; set; }
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public int SkippedCount { get; set; }
    public string? RawDataJson { get; set; }
    public string? ErrorsJson { get; set; }
    public DateTime? CompletedAt { get; set; }

    // ══ ศูนย์นำเข้าข้อมูล (Import Hub · รอบ 205 T0 · IMPORT_ETL_REVIEW §2.2) — ขยาย session เดิมเป็น "batch" (owner เดียว ·
    //    ไม่สร้างตาราง batch คู่ขนาน) · คอลัมน์ใหม่ทั้งหมด ADD COLUMN IF NOT EXISTS ใน DatabaseMigrationHelper ══

    /// <summary>ไฟล์ต้นทาง (<c>FileAttachments</c> ชนิด <c>"ImportBatch"</c> · EntityId = Id ของ batch) — เก็บไว้ดาวน์โหลดซ้ำ/วิเคราะห์ใหม่ ·
    /// ด่านอ่าน = สิทธิ์นำเข้าชนิดของ batch (<c>AttachmentPermissionScope</c> · ไฟล์มี PII ได้)</summary>
    public Guid? SourceFileAttachmentId { get; set; }
    /// <summary>SHA-256 (hex ตัวเล็ก 64 ตัว) ของไบต์ไฟล์ — เตือน "ไฟล์นี้เคยนำเข้าแล้ว" (ไม่บล็อก · คำตัดสินข้อ 150)</summary>
    public string? FileSha256 { get; set; }
    /// <summary>ชีตที่ใช้ (xlsx) · null = CSV/ชีตแรก</summary>
    public string? SheetName { get; set; }
    /// <summary>รายชื่อชีตทั้งหมดในไฟล์ (JSON array ของ string) — หน้าเว็บให้เลือกชีต</summary>
    public string? SheetNamesJson { get; set; }
    /// <summary>แถวหัวตาราง (0-based ในแถวของไฟล์) — ข้ามแถวชื่อรายงานด้านบน</summary>
    public int HeaderRowIndex { get; set; }
    /// <summary>จำนวนแถวที่เป็นหัวตาราง (หัวสองชั้น = 2 · merge เป็นชื่อเดียว)</summary>
    public int HeaderRowSpan { get; set; } = 1;
    /// <summary>encoding ที่ถอดได้ ("utf-8" · "utf-16" · "windows-874") · null = xlsx</summary>
    public string? Encoding { get; set; }
    /// <summary>ตัวคั่น CSV ("," ";" "	") · null = xlsx</summary>
    public string? Delimiter { get; set; }
    /// <summary><c>ImportHeaderSignature.Compute(headers)</c> — SHA-256 ของชุดหัวตารางที่ normalize แล้ว (ไม่ขึ้นกับลำดับ) ใช้หาแม่แบบ</summary>
    public string? HeaderSignature { get; set; }
    /// <summary>แม่แบบที่ใช้จับคู่คอลัมน์ (<see cref="ImportMappingTemplate"/>)</summary>
    public Guid? MappingTemplateId { get; set; }
    /// <summary>โปรแกรมต้นทางที่เดาได้/ผู้ใช้เลือก ("express" · "peak" · "flowaccount" · "bank:kbank" …)</summary>
    public string? SourceSystem { get; set; }
    /// <summary>ขั้นของ pipeline — worker ทำต่อจากขั้นนี้</summary>
    public ImportBatchPhase Phase { get; set; } = ImportBatchPhase.None;
    /// <summary>โหมด commit ล่าสุดที่ผู้ใช้เลือก</summary>
    public ImportCommitMode CommitMode { get; set; } = ImportCommitMode.GoodRowsOnly;
    /// <summary>จำนวนแถวที่ขั้นปัจจุบันทำไปแล้ว (progress)</summary>
    public int ProcessedRows { get; set; }
    /// <summary>worker ที่ถือ batch อยู่ (เครื่อง/instance) — claim ด้วย <c>FOR UPDATE SKIP LOCKED</c> (ทีม B)</summary>
    public string? ClaimedBy { get; set; }
    public DateTime? ClaimedAt { get; set; }
    /// <summary>เต้นทุก ≤ 2 นาทีระหว่างทำ — เกินแล้วเครื่องอื่นหยิบต่อได้</summary>
    public DateTime? HeartbeatAt { get; set; }
    /// <summary>กฎเหล็ก #1: feedback ของการจับคู่คอลัมน์ (ImportColumnMatch) — commit/remap ⇒ <c>RecordUserChoiceAsync</c></summary>
    public Guid? ColumnMatchAiFeedbackId { get; set; }
    /// <summary>ป้ายซื่อสัตย์: true เฉพาะเมื่อเรียก AI จริงในการจับคู่คอลัมน์ของ batch นี้</summary>
    public bool ColumnMatchUsedAi { get; set; }
    /// <summary>ตัวเลือกของ batch (JSON — <c>ImportBatchOptions</c> ของ pipeline: dateOrder · ค่าเริ่มของ field ฯลฯ)</summary>
    public string? OptionsJson { get; set; }
    /// <summary>สรุปผลตรวจทั้งชุด (JSON — จำนวนต่อ issue code · คำเตือนที่ต้องรับทราบ) — คงไว้แม้ staged rows ถูก purge</summary>
    public string? ReviewSummaryJson { get; set; }
    public DateTime? UndoneAt { get; set; }
    public string? UndoneBy { get; set; }

    public ICollection<SmartImportColumnMapping> ColumnMappings { get; set; } = new List<SmartImportColumnMapping>();
    public ICollection<ImportStagedRow> StagedRows { get; set; } = new List<ImportStagedRow>();
}

public class SmartImportColumnMapping : BaseEntity
{
    public Guid SessionId { get; set; }
    public int SourceIndex { get; set; }
    public string SourceHeader { get; set; } = null!;
    public string? TargetField { get; set; }
    public ColumnMatchType MatchType { get; set; } = ColumnMatchType.Unmapped;
    public ColumnMatchConfidence Confidence { get; set; } = ColumnMatchConfidence.None;
    public double ConfidenceScore { get; set; }
    public string? SampleValuesJson { get; set; }
    public string? SuggestionsJson { get; set; }

    public SmartImportSession Session { get; set; } = null!;
}

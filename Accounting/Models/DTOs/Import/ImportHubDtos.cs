using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Import;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  ศูนย์นำเข้าข้อมูล (Import Hub / ETL) — สัญญา DTO ของ endpoint ทั้งหมดใน IMPORT_ETL_REVIEW §2.7 (รอบ 205 T0 · FROZEN)
//
//  • ทีม E (ImportHubController + import-hub.html) อ่าน/เขียนรูปนี้ตรง ๆ · ทีม A–D เติมค่าผ่าน IImportPipelineService/IImportTarget
//  • ป้าย/สถานะ/สีเซลล์ทั้งหมดมาจากเซิร์ฟเวอร์ (*Label · ImportCellState) — หน้าเว็บห้ามมีสำเนากติกา (F2 ข้อ 5) · enum ออกเป็น "ชื่อ"
//  • ป้ายซื่อสัตย์ (กฎเหล็ก #1): UsedAi = true เฉพาะเมื่อเรียก AI จริง ⇒ "🤖 AI แนะนำ" · ไม่งั้น "⚙️ ระบบแนะนำ"
//  • เปลี่ยนสัญญา = เพิ่ม field ท้าย record (ค่าเริ่มต้น) เท่านั้น · ห้ามลบ/เปลี่ยนชื่อ/สลับลำดับ (ทุกทีมคอมไพล์จากไฟล์นี้)
// ═══════════════════════════════════════════════════════════════════════════════════════════════

// ───────────────────────── ผู้กระทำ / ไฟล์ ─────────────────────────

/// <summary>ผู้สั่งงาน — controller สร้างจาก JWT (UserId · ชื่อที่แสดง · IP) · ใช้ใน audit (<c>AddChainedAuditLog</c>) และ CreatedBy/UpdatedBy</summary>
public sealed record ImportActor(Guid UserId, string Name, string? Ip = null);

/// <summary>ไฟล์ที่อัปโหลด (controller อ่าน IFormFile เป็นไบต์ · ≤ 25 MB = เพดานไฟล์แนบ)</summary>
public sealed record ImportUploadedFile(string FileName, string? ContentType, byte[] Content);

/// <summary>ไฟล์ต้นทางสำหรับดาวน์โหลด (<c>GET batches/{id}/source-file</c>)</summary>
public sealed record ImportSourceFileDto(string FileName, string ContentType, byte[] Content);

// ───────────────────────── issue / เซลล์ / แถว ─────────────────────────

/// <summary>
/// ปัญหา/คำแนะนำหนึ่งข้อ — ใช้ทั้งใน DTO และ<b>เก็บลง <c>ImportStagedRow.IssuesJson</c></b> (รูปเดียวกัน · serialize ผ่าน <c>ImportJson</c>)
/// </summary>
/// <param name="Field">field ปลายทาง (null = ทั้งแถว)</param>
/// <param name="Code">รหัสคงที่ (เช่น <c>date-order-assumed</c> · <c>taxid-invalid</c> · <c>duplicate-in-file</c>) — ตัวกรอง chip + รับทราบคำเตือน</param>
/// <param name="Severity">Error = บล็อก (เฉพาะ §2.5) · Warning = รับทราบตอน commit · Info</param>
/// <param name="Message">ข้อความไทยที่บอกทางไปต่อ</param>
/// <param name="Suggested">ค่าแนะนำรูปมาตรฐาน — ผู้ใช้กด "ใช้คำแนะนำ" (ระบบไม่ใส่เอง)</param>
/// <param name="RuleCode">รหัสกติกา (เช่น <c>RD-86/4</c>)</param>
/// <param name="LegalRef">มาตรา/ประกาศที่อ้าง (tooltip)</param>
/// <param name="Source">ผู้ยก issue — <see cref="ImportIssueSource.Ai"/> เท่านั้นที่ติดป้าย AI</param>
public sealed record ImportIssueDto(
    string? Field,
    string Code,
    ImportIssueSeverity Severity,
    string Message,
    string? Suggested = null,
    string? RuleCode = null,
    string? LegalRef = null,
    ImportIssueSource Source = ImportIssueSource.Rule);

/// <summary>เซลล์หนึ่งช่องในตาราง review</summary>
/// <param name="Value">ค่ารูปมาตรฐานที่จะ commit (วันที่ yyyy-MM-dd · ยอด invariant · flag true/false)</param>
/// <param name="Raw">ค่าดิบจากไฟล์ (tooltip "ค่าเดิม")</param>
/// <param name="State">สีเซลล์ — เซิร์ฟเวอร์ตัดสิน</param>
/// <param name="SourceLabel">ป้ายแหล่งของค่า ("ตามไฟล์" · "⚙️ ระบบเติม" · "🤖 AI แนะนำ" · "✋ ผู้ใช้แก้")</param>
public sealed record ImportCellDto(
    string Field,
    string? Value,
    string? Raw,
    ImportCellState State,
    decimal? Confidence,
    bool UsedAi,
    string? SourceLabel);

/// <summary>ผู้สมัครจับคู่ข้อมูลเดิม</summary>
public sealed record ImportMatchCandidateDto(Guid EntityId, string Label, decimal Score, string Kind);

/// <summary>ผลจับคู่ข้อมูลเดิมของแถว (สร้างใหม่ / อัปเดต "X" 0.93)</summary>
/// <param name="Kind">วิธีจับคู่ ("TaxBranch" · "Code" · "LegalName" · "Ngram" · "Ai" · "User")</param>
/// <param name="AutoApplied">ระบบเลือกให้เอง (มีเลขภาษี/รหัสตรง) · false = แนะนำเท่านั้น (ชื่อคล้ายอย่างเดียว — คำตัดสินข้อ 151 (5))</param>
public sealed record ImportRowMatchDto(
    Guid? EntityId,
    string? Label,
    string Kind,
    decimal Score,
    bool AutoApplied,
    bool UsedAi,
    string SourceLabel,
    IReadOnlyList<ImportMatchCandidateDto> Candidates);

/// <summary>แถวหนึ่งในตาราง review (server-paged)</summary>
public sealed record ImportRowDto(
    Guid Id,
    int RowNo,
    string? DisplayKey,
    ImportRowStatus Status,
    string StatusLabel,
    ImportRowAction Action,
    string ActionLabel,
    decimal Confidence,
    IReadOnlyList<ImportCellDto> Cells,
    IReadOnlyList<ImportIssueDto> Issues,
    ImportRowMatchDto? Match,
    bool UserEdited,
    bool UsedAi,
    string? GroupKey,
    string? CommittedEntityType,
    Guid? CommittedEntityId,
    string? CommitError);

/// <summary>ตัวกรอง/หน้าของแถว (<c>GET batches/{id}/rows</c>) — PageSize ≤ 500 (ค่าเริ่ม 100)</summary>
public sealed record ImportRowQuery(
    int Page = 1,
    int PageSize = 100,
    ImportRowStatus? Status = null,
    string? IssueCode = null,
    string? Search = null);

/// <summary>จำนวนแถวต่อ issue code (chip ตัวกรอง + "ใช้คำแนะนำทั้งหมด")</summary>
public sealed record ImportIssueCountDto(string Code, ImportIssueSeverity Severity, string Label, int Count, bool HasSuggestion);

public sealed record ImportRowPageDto(
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<ImportRowDto> Rows,
    IReadOnlyList<ImportIssueCountDto> IssueCounts);

// ───────────────────────── จับคู่คอลัมน์ ─────────────────────────

/// <summary>การแปลงต่อคอลัมน์ (เก็บในแม่แบบ) — กลับเครื่องหมาย · ลำดับวันที่ · แผนที่ค่า ("ลูกค้า" → "Customer")</summary>
public sealed record ImportColumnTransformDto(
    bool Negate = false,
    ImportDateOrder? DateOrder = null,
    IReadOnlyDictionary<string, string>? ValueMap = null);

/// <summary>ผู้สมัคร field ของคอลัมน์ (dropdown)</summary>
public sealed record ImportColumnCandidateDto(string TargetField, string TargetLabel, decimal Score, ImportMapSource Source, string Reason);

/// <summary>แผนจับคู่ของหนึ่งคอลัมน์ในไฟล์</summary>
/// <param name="Source">ชั้นที่จับคู่ได้ (📌 แม่แบบ / 📚 คำพ้อง / 🧠 เรียนรู้ / 🤖 AI / ✋ ผู้ใช้)</param>
/// <param name="SourceLabel">ป้ายไทยจากเซิร์ฟเวอร์ (ไม่ใช่หน้าเว็บแปลเอง)</param>
/// <param name="UsedAi">true เฉพาะคอลัมน์ที่ AI ตอบจริงในรอบนี้</param>
public sealed record ImportColumnPlanDto(
    int SourceIndex,
    string SourceHeader,
    string? TargetField,
    string? TargetLabel,
    ImportMapSource Source,
    string SourceLabel,
    decimal Confidence,
    bool UsedAi,
    IReadOnlyList<string> SampleValues,
    IReadOnlyList<ImportColumnCandidateDto> Alternatives,
    ImportColumnTransformDto? Transform = null);

// ───────────────────────── batch ─────────────────────────

/// <summary>สรุป batch (รายการ + หัวหน้า detail)</summary>
public sealed record ImportBatchSummaryDto(
    Guid Id,
    string EntityType,
    string EntityLabel,
    string FileName,
    string? SourceSystem,
    ImportSessionStatus Status,
    string StatusLabel,
    ImportBatchPhase Phase,
    int TotalRows,
    int ValidCount,
    int WarningCount,
    int ErrorCount,
    int ExcludedCount,
    int CommittedCount,
    int FailedCount,
    int SkippedCount,
    DateTime CreatedAt,
    string? CreatedBy,
    DateTime? CompletedAt,
    DateTime? UndoneAt,
    bool CanCommit,
    bool CanUndo,
    bool CanCancel);

/// <summary>ความคืบหน้า (poll ระหว่าง Queued/Committing)</summary>
public sealed record ImportBatchProgressDto(
    ImportBatchPhase Phase,
    string PhaseLabel,
    int ProcessedRows,
    int TotalRows,
    decimal Percent,
    bool IsQueued,
    DateTime? HeartbeatAt);

/// <summary>สิ่งที่ Extract ตัดสินได้ — chip แก้ได้บนหน้า (ชีต · แถวหัวตาราง · โปรแกรมต้นทาง)</summary>
public sealed record ImportSourceInfoDto(
    string FileName,
    ImportFileKind Kind,
    string? Encoding,
    string? Delimiter,
    string? SheetName,
    IReadOnlyList<string> SheetNames,
    int HeaderRowIndex,
    int HeaderRowSpan,
    string? HeaderSignature,
    string? SourceSystem,
    string? SourceSystemLabel,
    Guid? SourceFileAttachmentId,
    string? FileSha256);

/// <summary>คำเตือนที่ต้องรับทราบก่อน commit (ส่ง Code กลับใน <see cref="ImportCommitOptions.AcknowledgedWarningCodes"/>)</summary>
public sealed record ImportWarningAckDto(string Code, string Label, int RowCount, string? LegalRef);

/// <summary>ไฟล์เดียวกัน (SHA-256 ตรง) เคยนำเข้าแล้ว — เตือน ไม่บล็อก (คำตัดสินข้อ 150)</summary>
public sealed record ImportDuplicateFileDto(Guid BatchId, string FileName, DateTime CreatedAt, ImportSessionStatus Status, int CommittedCount);

/// <summary>แม่แบบที่ batch ใช้อยู่</summary>
public sealed record ImportTemplateRefDto(Guid Id, string Name, bool ConfirmedByUser, bool AutoCommitWhenClean, int UseCount);

/// <summary>รายละเอียด batch (<c>GET batches/{id}</c> · ผลของ create/analyze/remap)</summary>
public sealed record ImportBatchDetailDto(
    ImportBatchSummaryDto Summary,
    ImportBatchProgressDto Progress,
    ImportSourceInfoDto Source,
    IReadOnlyList<ImportColumnPlanDto> Columns,
    IReadOnlyList<ImportFieldDto> TargetFields,
    ImportTemplateRefDto? Template,
    bool ColumnMatchUsedAi,
    IReadOnlyList<ImportIssueCountDto> IssueCounts,
    IReadOnlyList<ImportWarningAckDto> WarningsToAcknowledge,
    IReadOnlyList<ImportDuplicateFileDto> DuplicateFiles,
    IReadOnlyList<string> Notes);

public sealed record ImportBatchListQuery(int Page = 1, int PageSize = 20, string? EntityType = null, ImportSessionStatus? Status = null);

public sealed record ImportBatchListDto(int Page, int PageSize, int Total, IReadOnlyList<ImportBatchSummaryDto> Items, int HiddenCount = 0);

// ───────────────────────── คำขอ ─────────────────────────

/// <summary><c>POST batches</c> (multipart: ไฟล์ + ฟิลด์เหล่านี้) — ทุกช่องเว้นได้: เซิร์ฟเวอร์เดาชนิดข้อมูล/โปรแกรมต้นทาง/ชีต/แถวหัวตารางเอง</summary>
public sealed record ImportCreateBatchRequest(
    string? EntityType = null,
    string? SheetName = null,
    string? SourceSystem = null,
    Guid? TemplateId = null,
    int? HeaderRowIndex = null,
    ImportDateOrder DateOrder = ImportDateOrder.Auto,
    IReadOnlyDictionary<string, string?>? Options = null);

/// <summary><c>POST batches/{id}/analyze</c> — วิเคราะห์ใหม่ด้วยค่าที่ผู้ใช้แก้ (null = คงค่าเดิม) · แถวที่ผู้ใช้แก้แล้วไม่ถูกทับ</summary>
public sealed record ImportAnalyzeRequest(
    string? EntityType = null,
    string? SheetName = null,
    int? HeaderRowIndex = null,
    int? HeaderRowSpan = null,
    Guid? TemplateId = null,
    string? SourceSystem = null,
    ImportDateOrder? DateOrder = null);

/// <summary>ผู้ใช้กำหนด field ของคอลัมน์ (TargetField null = ไม่ใช้คอลัมน์นี้)</summary>
public sealed record ImportColumnAssignment(int SourceIndex, string? TargetField, ImportColumnTransformDto? Transform = null);

/// <summary><c>PUT batches/{id}/mapping</c> — ⇒ MapSource = User · แปลงแถวใหม่ (ยกเว้นเซลล์ที่ผู้ใช้แก้)</summary>
public sealed record ImportRemapRequest(IReadOnlyList<ImportColumnAssignment> Columns);

/// <summary><c>PATCH batches/{id}/rows/{rowId}</c> — ทุกช่องเว้นได้ (null = ไม่แตะ) · Cells: field → ค่าใหม่ ("" = ล้างค่า)</summary>
public sealed record ImportEditRowRequest(
    IReadOnlyDictionary<string, string?>? Cells = null,
    ImportRowAction? Action = null,
    Guid? MatchedEntityId = null,
    bool? Excluded = null,
    IReadOnlyList<string>? AcceptSuggestionFields = null,
    IReadOnlyList<string>? RejectSuggestionFields = null);

/// <summary><c>POST batches/{id}/bulk</c> — เลือกแถวด้วย RowIds หรือเงื่อนไข (WhereStatus/WhereIssueCode)</summary>
public sealed record ImportBulkActionRequest(
    ImportBulkOp Op,
    IReadOnlyList<Guid>? RowIds = null,
    ImportRowStatus? WhereStatus = null,
    string? WhereIssueCode = null,
    ImportRowAction? Action = null,
    string? Field = null,
    string? Value = null);

public sealed record ImportBulkResultDto(int Affected, ImportBatchSummaryDto Summary);

/// <summary><c>POST batches/{id}/commit</c></summary>
/// <param name="Mode">GoodRowsOnly (ค่าเริ่ม) | AllOrNothing</param>
/// <param name="AcknowledgedWarningCodes">คำเตือนที่ผู้ใช้รับทราบ — แถวที่มีคำเตือนนอกชุดนี้ไม่ถูกลง</param>
/// <param name="SaveTemplate">"จำรูปแบบไฟล์นี้" (ติ๊กไว้เป็นค่าเริ่ม)</param>
/// <param name="ApproveAfterCreate">เอกสาร: อนุมัติหลังสร้าง (ค่าเริ่ม false = ร่าง + เลข DRAFT · คำตัดสินข้อ 151 (1))</param>
public sealed record ImportCommitOptions(
    ImportCommitMode Mode = ImportCommitMode.GoodRowsOnly,
    IReadOnlyList<string>? AcknowledgedWarningCodes = null,
    bool SaveTemplate = true,
    bool ApproveAfterCreate = false,
    string? TemplateName = null);

public sealed record ImportRowFailureDto(Guid RowId, int RowNo, string? DisplayKey, string Error);

/// <summary>ผล commit (Queued = ไฟล์ใหญ่ worker ทำต่อ — poll GET batches/{id})</summary>
public sealed record ImportCommitResultDto(
    Guid BatchId,
    ImportSessionStatus Status,
    string StatusLabel,
    int Committed,
    int Failed,
    int Skipped,
    bool Queued,
    IReadOnlyList<ImportRowFailureDto> Failures,
    Guid? TemplateId);

/// <summary><c>POST batches/{id}/undo</c> — เหตุผลบังคับ (audit IMPORT-UNDO) · <c>string?</c> โดยตั้งใจ: service ตรวจเองแล้วตอบภาษาไทย
/// (non-nullable = ASP.NET ตีกลับเป็นอังกฤษก่อน · tools/dto_nullable_contract_check)</summary>
public sealed record ImportUndoRequest(string? Reason);

/// <summary>หนึ่งรายการในตัวอย่างการย้อน</summary>
/// <param name="WayForward">ย้อนไม่ได้ ⇒ ทางไปต่อ (เช่น "ออกใบลดหนี้" · "ยกเลิกการจับคู่ธนาคารก่อน")</param>
public sealed record ImportUndoItemDto(
    Guid RowId,
    int RowNo,
    string? DisplayKey,
    string EntityType,
    Guid EntityId,
    ImportUndoKind Kind,
    string KindLabel,
    bool Blocked,
    string? BlockReason,
    string? WayForward);

/// <summary><c>GET batches/{id}/undo-preview</c> — ผู้ใช้เห็นก่อนว่าอะไรย้อนได้/ไม่ได้ (ผู้ใช้ตัดสิน)</summary>
public sealed record ImportUndoPreviewDto(
    Guid BatchId,
    bool CanUndo,
    int ReversibleCount,
    int BlockedCount,
    IReadOnlyList<ImportUndoItemDto> Items,
    IReadOnlyList<string> Notes);

public sealed record ImportUndoResultDto(Guid BatchId, ImportSessionStatus Status, int Undone, int Blocked, IReadOnlyList<ImportRowFailureDto> Failures);

// ───────────────────────── แคตตาล็อกชนิดข้อมูล ─────────────────────────

/// <summary>field ปลายทางของชนิดข้อมูล (schema ของ target)</summary>
/// <param name="Field">ชื่อ field (คีย์ใน MappedJson · ตรงกับชื่อที่ writer เดิมอ่าน)</param>
/// <param name="Label">ชื่อไทยที่แสดง</param>
public sealed record ImportFieldDto(
    string Field,
    string Label,
    ImportFieldType Type,
    bool Required,
    string? Description = null,
    IReadOnlyList<string>? AllowedValues = null,
    string? Example = null,
    int? MaxLength = null);

/// <summary>ชนิดข้อมูลที่นำเข้าได้ (<c>GET entities</c>)</summary>
/// <param name="EntityType">คีย์ชนิดข้อมูล — สะกดเดียวกับ <c>ImportExportPermissionScope</c> (normalize ด้วย <c>NormalizeEntityType</c>)</param>
/// <param name="UserCanImport">ผู้ใช้คนนี้มีสิทธิ์ <see cref="PermissionKey"/> ไหม (หน้าเว็บล็อกปุ่ม + บอกเหตุผล — ห้าม silent no-op)</param>
public sealed record ImportEntityInfoDto(
    string EntityType,
    string Label,
    string? Description,
    string PermissionKey,
    bool UserCanImport,
    IReadOnlyList<ImportFieldDto> Fields,
    ImportCommitStyle CommitStyle,
    bool SupportsUpdate,
    bool SupportsUndo,
    IReadOnlyList<string> SourceSystems);

/// <summary>ขั้นในลำดับย้ายระบบ (§2.10) — ผังบัญชี → ผู้ติดต่อ → สินค้า → งบทดลองยกมา → … → กระทบยอด</summary>
public sealed record ImportMigrationStepDto(int Order, string EntityType, string Label, bool Done, string? Hint);

public sealed record ImportEntityCatalogueDto(
    IReadOnlyList<ImportEntityInfoDto> Entities,
    IReadOnlyList<ImportMigrationStepDto> MigrationOrder);

// ───────────────────────── แม่แบบ ─────────────────────────

/// <summary>หนึ่งคอลัมน์ของแม่แบบ — เก็บใน <c>ImportMappingTemplate.ColumnMapJson</c> เป็น array ของ record นี้</summary>
/// <param name="Header">หัวคอลัมน์ที่ normalize แล้ว (ตัวเดียวกับที่ใช้คิด HeaderSignature)</param>
public sealed record ImportTemplateColumnDto(string Header, string? TargetField, ImportColumnTransformDto? Transform = null);

public sealed record ImportTemplateDto(
    Guid Id,
    string EntityType,
    string Name,
    string HeaderSignature,
    string? SourceSystem,
    IReadOnlyList<string> Headers,
    IReadOnlyList<ImportTemplateColumnDto> Columns,
    int UseCount,
    DateTime? LastUsedAt,
    bool ConfirmedByUser,
    bool AutoCommitWhenClean,
    DateTime CreatedAt);

/// <summary><c>PUT templates/{id}</c> — AutoCommitWhenClean = opt-in ของผู้ใช้ (คำตัดสินข้อ 151 (3))</summary>
public sealed record ImportTemplateUpsertRequest(
    string? Name,
    bool AutoCommitWhenClean,
    IReadOnlyList<ImportTemplateColumnDto>? Columns = null,
    string? SourceSystem = null);

// ───────────────────────── กระทบยอดหลังย้ายระบบ (ทีม D) ─────────────────────────

/// <summary>หนึ่งบรรทัดกระทบยอด (AR/AP · สต็อก vs บัญชีสินค้า · ราคาทุน/ค่าเสื่อมสะสม vs งบทดลอง)</summary>
public sealed record ImportReconciliationLineDto(
    string Area,
    string Label,
    decimal SubledgerTotal,
    decimal LedgerTotal,
    decimal Difference,
    bool Balanced,
    string? Hint);

public sealed record ImportReconciliationReportDto(DateTime AsOf, bool AllBalanced, IReadOnlyList<ImportReconciliationLineDto> Lines);

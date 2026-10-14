using Accounting.Models.DTOs.Import;

namespace Accounting.Services.Import;

/// <summary>
/// **pipeline เดียวของศูนย์นำเข้าข้อมูล** — Extract → Transform → Stage → Review → Commit → Learn → Undo (รอบ 205 T0 · IMPORT_ETL_REVIEW §2.4)
///
/// <para>เจ้าของ implementation: <b>ทีม B</b> (<see cref="ImportPipelineService"/>) · ผู้เรียก: <b>ทีม E</b> (<c>ImportHubController</c> ·
/// <c>api/companies/{cid}/import</c>) + <see cref="ImportBatchWorker"/> · ทุกเมธอด<b>กรอง CompanyId เสมอ</b> (batch ของบริษัทอื่น = ไม่พบ) ·
/// ด่านสิทธิ์ (<c>ImportExportPermissionScope.ForImport(batch.EntityType)</c>) อยู่ที่ controller ก่อนเรียก</para>
///
/// <para>กติกาที่ทุกเมธอดต้องรักษา: ค่าผู้ใช้ชนะเสมอ (แถว/เซลล์ที่ <c>UserEdited</c> ไม่ถูกวิเคราะห์ทับ) · บล็อกเฉพาะ §2.5 นอกนั้นคำเตือนที่รับทราบได้ ·
/// commit ผ่าน <see cref="IImportTarget"/> เท่านั้น · audit <c>AddChainedAuditLog</c> RuleCode <c>IMPORT-COMMIT</c>/<c>IMPORT-UNDO</c> ·
/// AI ผ่าน <c>IAiOrchestrator</c> + ปิดลูป <c>RecordUserChoiceAsync</c> (กฎเหล็ก #1) · business error = <c>BusinessRuleException</c> (ข้อความไทย)</para>
/// </summary>
public interface IImportPipelineService
{
    /// <summary>
    /// อัปโหลด + สร้าง batch: เก็บไฟล์ (<c>UploadBytesAsync("ImportBatch", batchId)</c>) · SHA-256 · เตือนไฟล์ซ้ำที่ commit แล้ว ·
    /// ≤ 2,000 แถวทำ Extract+Transform ทันที (Status = Staged) · มากกว่านั้น Status = Queued ให้ worker ทำ
    /// </summary>
    Task<ImportBatchDetailDto> CreateAsync(Guid companyId, ImportUploadedFile file, ImportCreateBatchRequest request, ImportActor actor,
        CancellationToken ct = default);

    /// <summary>วิเคราะห์ใหม่ (เปลี่ยนชนิดข้อมูล/ชีต/แถวหัวตาราง/แม่แบบ/ลำดับวันที่) — แถวที่ผู้ใช้แก้ไม่ถูกทับ</summary>
    Task<ImportBatchDetailDto> AnalyzeAsync(Guid companyId, Guid batchId, ImportAnalyzeRequest request, ImportActor actor,
        CancellationToken ct = default);

    Task<ImportBatchListDto> ListAsync(Guid companyId, ImportBatchListQuery query, CancellationToken ct = default);

    /// <summary>batch ของบริษัทนี้ · ไม่พบ ⇒ null (controller ตอบ 404)</summary>
    Task<ImportBatchDetailDto?> GetAsync(Guid companyId, Guid batchId, CancellationToken ct = default);

    /// <param name="revealPii">(ฝ่ายค้านรอบ ETL · เพิ่มท้าย) true เฉพาะผู้มี <c>Pii.View</c> (controller ตัดสิน + บันทึก PiiAccessLog) — false = ช่องอ่อนไหวของ
    /// ชนิดที่มี PII (ทะเบียนพนักงาน: เลขบัตร · เลขบัญชี · เงินเดือน) ถูกปิดบัง (<c>Helpers/ImportPiiFields</c>) · ค่าเริ่ม = ปิดบัง</param>
    Task<ImportRowPageDto> GetRowsAsync(Guid companyId, Guid batchId, ImportRowQuery query, CancellationToken ct = default, bool revealPii = false);

    /// <summary>ผู้ใช้จับคู่คอลัมน์เอง (MapSource = User) ⇒ แปลงแถวใหม่ · ปิดลูป ImportColumnMatch ถ้ามี feedback</summary>
    Task<ImportBatchDetailDto> RemapAsync(Guid companyId, Guid batchId, ImportRemapRequest request, ImportActor actor,
        CancellationToken ct = default);

    /// <summary>แก้แถวเดียว (เซลล์ · action · คู่ที่จับ · ตัดออก · รับ/ปฏิเสธคำแนะนำ) ⇒ ตรวจแถวนั้นใหม่ · UserEdited = true</summary>
    Task<ImportRowDto> EditRowAsync(Guid companyId, Guid batchId, Guid rowId, ImportEditRowRequest request, ImportActor actor,
        CancellationToken ct = default, bool revealPii = false);

    Task<ImportBulkResultDto> BulkActionAsync(Guid companyId, Guid batchId, ImportBulkActionRequest request, ImportActor actor,
        CancellationToken ct = default);

    /// <summary>ลงข้อมูลจริง — GoodRowsOnly | AllOrNothing · idempotent (แถว Committed ข้าม) · ใหญ่ ⇒ Queued · SaveTemplate ⇒ แม่แบบ (ทีม A)</summary>
    Task<ImportCommitResultDto> CommitAsync(Guid companyId, Guid batchId, ImportCommitOptions options, ImportActor actor,
        CancellationToken ct = default);

    /// <summary>ตัวอย่างการย้อน (ไม่เขียนข้อมูล) — ผู้ใช้เห็นก่อนว่าอะไรย้อนได้/ไม่ได้ พร้อมทางไปต่อ</summary>
    Task<ImportUndoPreviewDto> PreviewUndoAsync(Guid companyId, Guid batchId, CancellationToken ct = default);

    /// <summary>ย้อนการนำเข้า — <paramref name="reason"/> บังคับ (ว่าง ⇒ BusinessRuleException ภาษาไทย)</summary>
    Task<ImportUndoResultDto> UndoAsync(Guid companyId, Guid batchId, string? reason, ImportActor actor, CancellationToken ct = default);

    /// <summary>ยกเลิก batch ที่ยังไม่ลงข้อมูล (Status = Cancelled)</summary>
    Task<ImportBatchSummaryDto> CancelAsync(Guid companyId, Guid batchId, ImportActor actor, CancellationToken ct = default);

    /// <summary>CSV ของแถวที่ผิด/ล้ม (ค่าดิบ + คอลัมน์ "ปัญหา") ให้แก้ในโปรแกรมต้นทางแล้วนำเข้าใหม่ — UTF-8 BOM</summary>
    Task<byte[]> ExportErrorRowsCsvAsync(Guid companyId, Guid batchId, CancellationToken ct = default, bool revealPii = false);

    /// <summary>ไฟล์ต้นทางของ batch · ไม่พบ batch/ไม่มีไฟล์ ⇒ null (ด่านอ่านไฟล์ = <c>IAttachmentAccessGate</c> ชนิด "ImportBatch" ที่ controller)</summary>
    Task<ImportSourceFileDto?> GetSourceFileAsync(Guid companyId, Guid batchId, CancellationToken ct = default);

    /// <summary>ชนิดข้อมูลที่นำเข้าได้ + สิทธิ์ของผู้ใช้ต่อชนิด + ลำดับย้ายระบบ (§2.10)</summary>
    Task<ImportEntityCatalogueDto> GetEntityCatalogueAsync(Guid companyId, Guid userId, CancellationToken ct = default);

    /// <summary>ให้ <see cref="ImportBatchWorker"/> ทำ batch ที่ claim แล้ว (Extract/Transform ของไฟล์ใหญ่ · commit ที่ Queued) ทีละ chunk 500 แถว ·
    /// คืน true เมื่อ batch ถึงสถานะปลายทางของขั้นนั้น</summary>
    Task<bool> ProcessQueuedAsync(Guid companyId, Guid batchId, string workerId, CancellationToken ct = default);
}

using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;

namespace Accounting.Services.Import;

/// <summary>
/// **คลังแม่แบบจับคู่คอลัมน์ต่อบริษัท** (<c>ImportMappingTemplates</c>) — รอบ 205 T0 · IMPORT_ETL_REVIEW §2.2/§2.4
///
/// <para>เจ้าของ implementation: <b>ทีม A</b> (<see cref="ImportMappingTemplateStore"/>) · หาแม่แบบด้วย <c>(EntityType, HeaderSignature)</c> —
/// signature จาก <c>ImportHeaderSignature.Compute</c> (ทีม A) · unique ต่อบริษัท (แถวไม่ถูกลบ) ⇒ SaveFromBatch = upsert ·
/// <c>AutoCommitWhenClean</c> เปลี่ยนได้เฉพาะทาง <see cref="UpdateAsync"/> (ผู้ใช้ opt-in · คำตัดสินข้อ 151 (3)) · ทุกเมธอดกรอง CompanyId</para>
/// </summary>
public interface IImportMappingTemplateStore
{
    /// <summary>แม่แบบที่ตรงชุดหัวตารางนี้ · ไม่มี ⇒ null</summary>
    Task<ImportMappingTemplate?> FindAsync(Guid companyId, string entityType, string headerSignature, CancellationToken ct = default);

    Task<IReadOnlyList<ImportTemplateDto>> ListAsync(Guid companyId, string? entityType, CancellationToken ct = default);

    Task<ImportTemplateDto?> GetAsync(Guid companyId, Guid templateId, CancellationToken ct = default);

    /// <summary>จำการจับคู่ของ batch เป็นแม่แบบ (upsert ตาม signature) — เรียกตอน commit เมื่อ SaveTemplate</summary>
    Task<ImportTemplateDto> SaveFromBatchAsync(Guid companyId, Guid batchId, string? name, bool confirmedByUser, ImportActor actor,
        CancellationToken ct = default);

    /// <summary>แก้ชื่อ/คอลัมน์/AutoCommitWhenClean · ไม่พบ ⇒ null</summary>
    Task<ImportTemplateDto?> UpdateAsync(Guid companyId, Guid templateId, ImportTemplateUpsertRequest request, ImportActor actor,
        CancellationToken ct = default);

    /// <summary>ลบ (soft-delete) · ไม่พบ ⇒ false</summary>
    Task<bool> DeleteAsync(Guid companyId, Guid templateId, ImportActor actor, CancellationToken ct = default);

    /// <summary>UseCount + 1 · LastUsedAt = ตอนนี้</summary>
    Task MarkUsedAsync(Guid companyId, Guid templateId, CancellationToken ct = default);

    // ── เพิ่มโดยทีม A (รอบ 205 · additive) ──

    /// <summary>
    /// จำการจับคู่เป็นแม่แบบจากคอลัมน์ที่ผู้เรียกส่งมาตรง ๆ (มี transform ต่อคอลัมน์ได้) — upsert ตาม signature ของชุดหัว ·
    /// <see cref="SaveFromBatchAsync"/> มอบมาที่นี่ · ทีม B เรียกตัวนี้ได้เมื่อ transform ของคอลัมน์ไม่ได้อยู่ใน <c>SmartImportColumnMappings</c>
    /// </summary>
    /// <param name="headers">หัวคอลัมน์ตามไฟล์ (ทุกคอลัมน์ · ใช้คิด signature + แสดงผล)</param>
    /// <param name="columns">header (ตามไฟล์หรือ normalize แล้วก็ได้) → field + transform · field null = "ไม่ใช้คอลัมน์นี้"</param>
    Task<ImportTemplateDto> SaveColumnsAsync(Guid companyId, string entityType, IReadOnlyList<string> headers,
        IReadOnlyList<ImportTemplateColumnDto> columns, string? sourceSystem, string? name, bool confirmedByUser, ImportActor actor,
        CancellationToken ct = default);

    /// <summary>
    /// คู่ "หัวคอลัมน์ → field" ที่บริษัทนี้ยืนยันแล้ว — จากแม่แบบ (ยืนยัน = น้ำหนักมากกว่า) + การยืนยันการจับคู่คอลัมน์
    /// (<c>AiSuggestionFeedback</c> ของ ImportColumnMatch ที่มี UserChosenAnswer) · ชั้น 🧠 ของ <c>ImportColumnMapper</c> และสมองของ
    /// <c>ImportColumnMatchDistillationModel</c> (ที่มาเดียว) · คู่ที่ขัดกันน้ำหนักเท่ากัน = ไม่เรียน · <paramref name="entityType"/> null = ทุกชนิด
    /// </summary>
    Task<IReadOnlyList<Accounting.Helpers.ImportLearnedHeader>> GetLearnedHeadersAsync(Guid companyId, string? entityType, CancellationToken ct = default);

    /// <summary>
    /// <b>ปิดลูปกฎเหล็ก #1 ของการจับคู่คอลัมน์</b> — เรียกตอนผู้ใช้ยืนยัน (commit) หรือแก้ (remap) ด้วยแผนสุดท้าย:
    /// <c>RecordUserChoiceAsync(feedbackId, คำตอบรูปมาตรฐานของคอลัมน์ที่ถูกถาม, acceptedAi)</c> · acceptedAi = แผนสุดท้ายตรงกับที่ระบบ/AI เสนอ ·
    /// feedback ต้องเป็นของบริษัทนี้และเป็น ImportColumnMatch (ไม่ตรง ⇒ false ไม่บันทึก) · ผู้เรียก: pipeline ทีม B
    /// (<c>SmartImportSession.ColumnMatchAiFeedbackId</c>)
    /// </summary>
    Task<bool> RecordColumnMatchChoiceAsync(Guid companyId, Guid feedbackId, IReadOnlyList<ImportColumnPlanDto> finalColumns,
        CancellationToken ct = default);
}

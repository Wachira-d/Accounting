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
}

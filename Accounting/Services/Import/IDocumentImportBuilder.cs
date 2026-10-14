using Accounting.Models.DTOs.Import;

namespace Accounting.Services.Import;

/// <summary>
/// **ประกอบคำขอสร้างเอกสารจากแถวนำเข้าหนึ่งกลุ่ม** (หัว + บรรทัด) — รอบ 205 T0 · IMPORT_ETL_REVIEW §2.4/§2.10
///
/// <para>เจ้าของ implementation: <b>ทีม D</b> (<c>Targets/DocumentImportBuilder.cs</c>) · target เอกสารส่ง <see cref="ImportDocumentBuild.Request"/> เข้า
/// <c>IDocumentService.CreateDocumentAsync</c> (domain service เดิม — <b>ห้ามเขียนตาราง Documents ตรง</b>) · เอกสารนำเข้า = <b>ร่าง + เลข DRAFT</b>
/// เลขเดิมจากไฟล์ไป <c>Reference</c> (คำตัดสินข้อ 151 (1)) · ยอดคำนวณโดย DocumentService ไม่ใช่ที่นี่</para>
/// </summary>
public interface IDocumentImportBuilder
{
    Task<ImportDocumentBuild> BuildAsync(Guid companyId, string entityType, IReadOnlyList<ImportTargetRow> groupRows,
        ImportCommitOptions options, CancellationToken ct = default);
}

/// <summary>ผลประกอบ — <see cref="Request"/> null = ประกอบไม่ได้ (ดู <see cref="Issues"/> ระดับ Error)</summary>
/// <param name="OriginalNumber">เลขเอกสารเดิมในไฟล์ (ไปที่ Reference)</param>
public sealed record ImportDocumentBuild(
    Accounting.Models.DTOs.Document.CreateDocumentRequest? Request,
    IReadOnlyList<ImportIssueDto> Issues,
    string? OriginalNumber = null);

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
    /// <summary>ประกอบคำขอตอนลงข้อมูล — <b>ไม่ถาม AI</b> (ฝ่ายค้านรอบ ETL): ผู้ติดต่อ = คู่ที่แถวเลือกไว้ (<see cref="ImportTargetRow.MatchedEntityId"/>) ·
    /// ไม่มี ⇒ กุญแจเลขภาษี+สาขา/ชื่อตรงตัวที่ไม่ซ้ำแบบ local · ผังแนะนำของบรรทัด = ที่ขั้นตรวจฝากไว้ (<see cref="ImportTargetRow.Hints"/>)</summary>
    Task<ImportDocumentBuild> BuildAsync(Guid companyId, string entityType, IReadOnlyList<ImportTargetRow> groupRows,
        ImportCommitOptions options, CancellationToken ct = default);

    /// <summary>
    /// (ฝ่ายค้านรอบ ETL · เพิ่ม) <b>ขั้นตรวจ</b>ของกลุ่ม: ตัดสินผู้ติดต่อ (ผู้ใช้เลือก → ContactId → เลขภาษี+สาขา → ชื่อตรงตัวที่ไม่ซ้ำในชุด SoftScope (คำเตือน) →
    /// ไม่พบ = บล็อกพร้อมผู้สมัครให้เลือก · ชื่อคล้ายอย่างเดียวไม่เลือกให้ — ข้อ 151 (5)) + ผังที่แนะนำของบรรทัดฝั่งซื้อที่ไม่มีผัง (นักเรียนก่อน · AI เมื่อ
    /// <paramref name="allowAi"/>) · ผลไปอยู่ในแถวพัก ⇒ ตอนลงข้อมูลใช้ซ้ำ ไม่ถาม AI อีก
    /// </summary>
    Task<ImportDocumentPreparation> PrepareAsync(Guid companyId, IReadOnlyList<ImportTargetRow> groupRows, bool allowAi, CancellationToken ct = default);
}

/// <summary>ผู้ติดต่อของเอกสารที่ขั้นตรวจตัดสิน — <see cref="PickedContactId"/> null = ยังไม่มี (ดู <see cref="Issues"/> — บล็อกพร้อมผู้สมัคร)</summary>
/// <param name="MatchKind">"User" · "ContactId" · "TaxBranch" · "ExactName" · วิธีของตัวจับคู่ · null</param>
/// <param name="AutoApplied">เลือกด้วยกุญแจ (เลขภาษี+สาขา / ContactId) — ไม่ต้องให้ผู้ใช้ดู</param>
/// <param name="AiFeedbackId">feedback ของ ImportEntityMatch (ชื่อกำกวม) — pipeline ปิดลูปตอนลงข้อมูลด้วยผู้ติดต่อที่ใช้จริง</param>
public sealed record ImportDocumentContactPick(
    Guid? PickedContactId,
    string? MatchKind,
    decimal? MatchScore,
    bool AutoApplied,
    IReadOnlyList<ImportMatchCandidateDto> Candidates,
    bool UsedAi,
    Guid? AiFeedbackId,
    string? AiAnswer,
    IReadOnlyList<ImportIssueDto> Issues);

/// <summary>ผังที่ระบบแนะนำให้บรรทัด (ฝั่งซื้อที่ไม่มีผัง) — <see cref="GlFeedbackId"/> ไปที่ <c>GlAccountAiFeedbackId</c> ของบรรทัด (DocumentService ปิดลูปตอนอนุมัติ)</summary>
public sealed record ImportDocumentLineHint(Guid RowId, string? SuggestedCode, Guid? GlFeedbackId, bool UsedAi);

public sealed record ImportDocumentPreparation(ImportDocumentContactPick Contact, IReadOnlyList<ImportDocumentLineHint> LineHints);

/// <summary>ผลประกอบ — <see cref="Request"/> null = ประกอบไม่ได้ (ดู <see cref="Issues"/> ระดับ Error)</summary>
/// <param name="OriginalNumber">เลขเอกสารเดิมในไฟล์ (ไปที่ Reference)</param>
public sealed record ImportDocumentBuild(
    Accounting.Models.DTOs.Document.CreateDocumentRequest? Request,
    IReadOnlyList<ImportIssueDto> Issues,
    string? OriginalNumber = null);

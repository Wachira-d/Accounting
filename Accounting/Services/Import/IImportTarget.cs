using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;

namespace Accounting.Services.Import;

/// <summary>
/// **ปลายทางของการนำเข้าหนึ่งชนิดข้อมูล** (รอบ 205 T0 · IMPORT_ETL_REVIEW §2.4) — ตรวจกฎธุรกิจ · ลงข้อมูลผ่าน writer/domain service เดิม · ย้อน
///
/// <para><b>ลงทะเบียนอัตโนมัติ</b>: class ที่ไม่ใช่ abstract และ implement interface นี้ถูก scan แล้ว <c>AddScoped</c> โดย
/// <see cref="ImportHubServiceRegistration.AddImportHub"/> — ทีม B/C/D เพิ่ม target = เพิ่มไฟล์ใน <c>Services/Import/Targets/</c> (ไม่แตะ Program.cs)
/// · หาด้วย <see cref="IImportTargetRegistry.Find"/> (คีย์ normalize ด้วย <c>ImportExportPermissionScope.NormalizeEntityType</c>)</para>
///
/// <para>เจ้าของ: ทีม B (master data: contacts/products/chartofaccounts/projects/employees/budgets) · ทีม C (banktransactions) ·
/// ทีม D (openingbalance/งบทดลอง · documents · payments · fixedassets · openingar/openingap · stock*)</para>
///
/// <para><b>CommitStyle</b>: <see cref="ImportCommitStyle.SharedTransaction"/> = pipeline เปิดธุรกรรมเดียว + savepoint ต่อ chunk 200 แถว แล้วเรียก
/// <see cref="CommitGroupAsync"/> ต่อ chunk (target <b>ห้าม</b>เปิด/commit ธุรกรรมเอง — SaveChanges ได้) · ล้ม ⇒ pipeline rollback savepoint + clear tracker +
/// replay ทีละแถว · <see cref="ImportCommitStyle.OwnTransactionPerGroup"/> = domain service เปิดธุรกรรมเอง (DocumentService ฯลฯ) ⇒ pipeline ไม่ห่อ ·
/// เรียกทีละกลุ่ม (<see cref="ImportTargetRow.GroupKey"/>) · idempotent ด้วยแถวที่ Committed แล้วไม่ถูกส่งซ้ำ</para>
///
/// <para>ห้ามเขียนตารางเอกสาร/รับชำระ/สินทรัพย์ตรง (คำตัดสินข้อ 151) · บล็อก (Severity Error) เฉพาะกรณีใน §2.5 นอกนั้น Warning</para>
/// </summary>
public interface IImportTarget
{
    /// <summary>คีย์ชนิดข้อมูล — สะกดเดียวกับ <c>ImportExportPermissionScope.ForImport</c> (เช่น "contacts" · "chart-of-accounts" · "stock-adjustments")</summary>
    string EntityType { get; }

    /// <summary>ชื่อไทยที่แสดง</summary>
    string Label { get; }

    /// <summary>field ปลายทาง (ป้อน mapper + ตัวแปลงค่า) — ทีม A ย้าย <c>GetTemplateFields</c> เป็น <c>ImportSchemas</c> แล้ว target อ้างจากที่นั่น</summary>
    IReadOnlyList<ImportFieldDto> Schema { get; }

    ImportCommitStyle CommitStyle { get; }

    /// <summary>รองรับ Update/Merge ของเดิมไหม (false ⇒ แถวที่จับคู่ได้ = Skip เป็นค่าเริ่ม)</summary>
    bool SupportsUpdate { get; }

    /// <summary>ตรวจกฎธุรกิจของแถว (หลัง normalize + จับคู่) — คืนผลต่อแถว (issue · action ที่แนะนำ · ค่าที่เติมให้) · ห้ามเขียนข้อมูล</summary>
    Task<IReadOnlyList<ImportRowValidation>> ValidateAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> rows, CancellationToken ct);

    /// <summary>ลงข้อมูลหนึ่งกลุ่ม/หนึ่ง chunk — คืนผลทุกแถวที่ได้รับ (สำเร็จ/ล้ม/ข้าม) · แถวที่ล้มห้ามทำให้แถวอื่นในกลุ่มหายเงียบ</summary>
    Task<IReadOnlyList<ImportRowCommitResult>> CommitGroupAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> group, CancellationToken ct);

    /// <summary>ตัวอย่างการย้อน (ไม่เขียนข้อมูล) — ย้อนได้ไหม · วิธี · เหตุผล + ทางไปต่อเมื่อย้อนไม่ได้ (§2.9)</summary>
    Task<IReadOnlyList<ImportUndoCheck>> CanUndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, CancellationToken ct);

    /// <summary>ย้อนจริง (เฉพาะแถวที่ <see cref="CanUndoAsync"/> บอกว่าได้) · <paramref name="reason"/> ลง audit</summary>
    Task<IReadOnlyList<ImportRowUndoResult>> UndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, string reason, CancellationToken ct);
}

/// <summary>บริบทของงานหนึ่งรอบ (ตรวจ/ลง/ย้อน)</summary>
/// <param name="EntityType">คีย์ชนิดข้อมูลของ batch (ตามที่เก็บใน SmartImportSession)</param>
/// <param name="Options">ตัวเลือกตอน commit (ขั้นตรวจใช้ค่าเริ่ม <c>new ImportCommitOptions()</c>)</param>
/// <param name="BatchOptions">ค่าเพิ่มเติมของ batch (OptionsJson — เช่น วันที่ยกมา · คลัง · บัญชีธนาคาร)</param>
public sealed record ImportTargetContext(
    Guid CompanyId,
    Guid BatchId,
    string EntityType,
    ImportActor Actor,
    ImportCommitOptions Options,
    string? SourceSystem = null,
    IReadOnlyDictionary<string, string?>? BatchOptions = null);

/// <summary>แถวที่ส่งให้ target — ค่าใน <see cref="Values"/> เป็นรูปมาตรฐานแล้ว (วันที่ yyyy-MM-dd · ยอด invariant · flag true/false)</summary>
/// <param name="Values">field → ค่า (ถอดจาก MappedJson · คีย์ไม่สนตัวพิมพ์)</param>
/// <param name="MatchedEntityId">ข้อมูลเดิมที่จับคู่/ผู้ใช้เลือก — Update/Merge ใช้ตัวนี้</param>
/// <param name="GroupKey">คีย์กลุ่ม (เอกสารหลายบรรทัด/JE) — target แบบ OwnTransactionPerGroup ได้ทั้งกลุ่มในครั้งเดียว</param>
public sealed record ImportTargetRow(
    Guid RowId,
    int RowNo,
    IReadOnlyDictionary<string, string?> Values,
    ImportRowAction Action,
    Guid? MatchedEntityId = null,
    string? GroupKey = null,
    string? DisplayKey = null);

/// <summary>ผลตรวจของแถวหนึ่งจาก target — null = ไม่เปลี่ยนค่าที่ pipeline ตัดสินไว้</summary>
/// <param name="FilledValues">ค่าที่ target เติมให้ (เซลล์ฟ้า "ระบบเติม") — ห้ามทับค่าที่ผู้ใช้แก้ (pipeline กันอีกชั้น)</param>
/// <param name="MatchCandidates">(รอบ 205 ทีม B · เพิ่มท้าย) ผู้สมัครจับคู่ข้อมูลเดิม (dropdown "อัปเดต X") — จาก <c>IImportEntityResolver</c></param>
/// <param name="MatchAutoApplied">(เพิ่มท้าย) ระบบเลือกคู่ให้เอง (กุญแจตรง: เลขภาษี+สาขา / รหัส) · false = แนะนำเท่านั้น</param>
/// <param name="MatchUsedAi">(เพิ่มท้าย) คำแนะนำคู่มาจาก AI จริง (ป้ายซื่อสัตย์)</param>
/// <param name="MatchAiFeedbackId">(เพิ่มท้าย) feedback ของ <c>ImportEntityMatch</c> — pipeline ปิดลูปตอน commit (กฎเหล็ก #1)</param>
public sealed record ImportRowValidation(
    Guid RowId,
    IReadOnlyList<ImportIssueDto> Issues,
    ImportRowAction? Action = null,
    Guid? MatchedEntityId = null,
    string? MatchKind = null,
    decimal? MatchScore = null,
    string? DisplayKey = null,
    string? GroupKey = null,
    IReadOnlyDictionary<string, string?>? FilledValues = null,
    IReadOnlyList<ImportMatchCandidateDto>? MatchCandidates = null,
    bool MatchAutoApplied = false,
    bool MatchUsedAi = false,
    Guid? MatchAiFeedbackId = null);

/// <summary>ผลลงข้อมูลของแถวหนึ่ง — สำเร็จต้องมี <see cref="EntityType"/>+<see cref="EntityId"/> (idempotent + ย้อนได้)</summary>
/// <param name="BeforeJson">ค่าเดิมก่อนทับ (Update/Merge) — เก็บลง ImportStagedRow.BeforeJson (เข้ารหัส) ใช้ย้อน</param>
/// <param name="Skipped">ข้ามโดยตั้งใจ (Action = Skip / ซ้ำของเดิม) — ไม่ใช่ล้ม</param>
public sealed record ImportRowCommitResult(
    Guid RowId,
    bool Success,
    string? EntityType = null,
    Guid? EntityId = null,
    string? Error = null,
    string? BeforeJson = null,
    bool Skipped = false);

/// <summary>แถวที่ลงไปแล้ว (ป้อน undo)</summary>
public sealed record ImportCommittedRow(
    Guid RowId,
    int RowNo,
    string EntityType,
    Guid EntityId,
    ImportRowAction Action,
    string? BeforeJson = null,
    string? DisplayKey = null);

/// <summary>ผลตรวจว่าย้อนแถวหนึ่งได้ไหม</summary>
public sealed record ImportUndoCheck(
    Guid RowId,
    bool CanUndo,
    ImportUndoKind Kind,
    string? BlockReason = null,
    string? WayForward = null);

public sealed record ImportRowUndoResult(Guid RowId, bool Success, string? Error = null);

/// <summary>ทะเบียน target (scoped) — หาตามคีย์ชนิดข้อมูล (สะกดได้หลายแบบ: "chart-of-accounts" = "chartofaccounts")</summary>
public interface IImportTargetRegistry
{
    /// <summary>target ของชนิดนี้ · ไม่มี ⇒ null (ผู้เรียกตอบ "ยังไม่รองรับการนำเข้าชนิดนี้ที่ศูนย์นำเข้า" — ไม่ใช่ 500)</summary>
    IImportTarget? Find(string? entityType);

    IReadOnlyList<IImportTarget> All { get; }
}

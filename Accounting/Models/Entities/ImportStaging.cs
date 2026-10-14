using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// แถวพักของศูนย์นำเข้าข้อมูล (Import Hub · รอบ 205 T0 · IMPORT_ETL_REVIEW §2.2) — หนึ่งแถวของไฟล์ = หนึ่งแถวที่นี่
///
/// <para>Extract→Transform เขียนที่นี่ (ขั้น Stage) · ผู้ใช้ตรวจ/แก้ที่นี่ (Review) · Commit อ่านจากที่นี่แล้วเขียนผ่าน writer/domain
/// service เดิม · idempotent ด้วย <see cref="Status"/> = Committed + <see cref="CommittedEntityId"/> (commit ซ้ำข้ามแถวที่ลงแล้ว)</para>
///
/// <para><b>PII</b> (PDPA ม.26): <see cref="RawJson"/>/<see cref="MappedJson"/>/<see cref="OriginalMappedJson"/>/<see cref="BeforeJson"/>
/// เข้ารหัสด้วย <c>EncryptedColumnConverter</c> (ทะเบียนพนักงาน/ผู้ติดต่อมีเลขบัตร/เลขบัญชี) ⇒ <b>ห้าม query ด้วยค่าในคอลัมน์เหล่านี้</b>
/// · <see cref="DisplayKey"/> เป็นข้อความธรรมดา ⇒ ห้ามใส่เลขบัตรประชาชน/เลขบัญชีธนาคาร (ใช้ชื่อ/รหัส/เลขเอกสาร)</para>
///
/// <para>รูป JSON (serialize ผ่าน <c>Services.Import.ImportJson</c> ตัวเดียว — camelCase · enum เป็นชื่อ):
/// <list type="bullet">
/// <item><see cref="RawJson"/> = array ของ string ตามลำดับคอลัมน์ในไฟล์</item>
/// <item><see cref="MappedJson"/> = object <c>{ "field": "ค่ารูปมาตรฐาน" | null }</c> — วันที่ <c>yyyy-MM-dd</c> · ยอด invariant
///   (<c>ImportValueNormalizer.CanonicalDate/CanonicalAmount</c>) · flag <c>"true"/"false"</c></item>
/// <item><see cref="OriginalMappedJson"/> = MappedJson ตอนวิเคราะห์ (ก่อนผู้ใช้แก้) — ฐานของการเรียนรู้ + ป้าย "ค่าเดิม"</item>
/// <item><see cref="BeforeJson"/> = ค่าของ entity เดิมก่อน Update (target กำหนดรูป) — ใช้ย้อน (RestoreBefore)</item>
/// <item><see cref="IssuesJson"/> = array ของ <c>ImportIssueDto</c></item>
/// <item><see cref="AiFeedbackIdsJson"/> = object <c>{ "field|match|account": "guid" }</c> — feedback ของ AI ที่แถวนี้ใช้ (ปิดลูปตอนยืนยัน)</item>
/// </list></para>
/// </summary>
public class ImportStagedRow : TenantEntity
{
    public Guid SessionId { get; set; }
    /// <summary>ลำดับแถวข้อมูล (1-based หลังหัวตาราง) — unique ต่อ batch</summary>
    public int RowNo { get; set; }

    public string? RawJson { get; set; }
    public string? MappedJson { get; set; }
    public string? OriginalMappedJson { get; set; }
    public string? BeforeJson { get; set; }

    /// <summary>ป้ายแถวที่คนอ่านเข้าใจ (ชื่อ/รหัส/เลขเอกสาร) ≤ 300 — ห้ามใส่ PII อ่อนไหว</summary>
    public string? DisplayKey { get; set; }
    public string? IssuesJson { get; set; }
    /// <summary>คะแนนความมั่นใจของทั้งแถว 0–1 (<c>ImportRowConfidence</c> · ทีม B) — &lt; 0.85 = เหลือง</summary>
    public decimal Confidence { get; set; }
    public ImportRowStatus Status { get; set; } = ImportRowStatus.Pending;
    public ImportRowAction Action { get; set; } = ImportRowAction.Create;

    /// <summary>ข้อมูลเดิมที่จับคู่ได้ (ผู้ติดต่อ/สินค้า/ผังบัญชี ฯลฯ) — Update/Merge ใช้ตัวนี้</summary>
    public Guid? MatchedEntityId { get; set; }
    /// <summary>วิธีจับคู่ ("TaxBranch" · "Code" · "LegalName" · "Ngram" · "Ai" · "User") ≤ 32</summary>
    public string? MatchKind { get; set; }
    public decimal? MatchScore { get; set; }
    /// <summary>คีย์รวมกลุ่ม (เอกสารหลายบรรทัด = เลขเอกสาร · JE = เลขใบ) — target ที่ commit ทีละกลุ่มใช้</summary>
    public string? GroupKey { get; set; }

    public string? CommittedEntityType { get; set; }
    public Guid? CommittedEntityId { get; set; }
    public string? CommitError { get; set; }
    /// <summary>ผู้ใช้แก้แถวนี้แล้ว — ห้ามวิเคราะห์ใหม่ทับ (ค่าผู้ใช้ชนะเสมอ · กฎเหล็ก #4 A)</summary>
    public bool UserEdited { get; set; }
    public string? AiFeedbackIdsJson { get; set; }

    public SmartImportSession Session { get; set; } = null!;
}

/// <summary>
/// แม่แบบจับคู่คอลัมน์ต่อบริษัท (รอบ 205 T0 · IMPORT_ETL_REVIEW §2.2) — "ไฟล์หน้าตานี้ (หัวตารางชุดนี้) ของชนิดนี้ จับคู่แบบนี้"
///
/// <para>unique <c>(CompanyId, EntityType, HeaderSignature)</c> เฉพาะแถวที่ไม่ถูกลบ · seed cold-start อยู่ใน<b>โค้ด</b>
/// (<c>Helpers/ImportSourceSeed</c> · ทีม A/D) ไม่ใช่แถวข้ามบริษัท · <see cref="AutoCommitWhenClean"/> = opt-in ต่อแม่แบบ ค่าเริ่ม false
/// (คำตัดสินข้อ 151 (3) — ผู้ใช้ตัดสิน)</para>
///
/// <para><see cref="ColumnMapJson"/> = array ของ <c>ImportTemplateColumnDto</c> (header ที่ normalize แล้ว → field + transform:
/// negate · dateOrder · valueMap) · <see cref="HeadersJson"/> = array ของหัวตารางตามไฟล์ (แสดงผล)</para>
/// </summary>
public class ImportMappingTemplate : TenantEntity
{
    public string EntityType { get; set; } = null!;
    public string HeaderSignature { get; set; } = null!;
    public string? SourceSystem { get; set; }
    public string Name { get; set; } = null!;
    public string? HeadersJson { get; set; }
    public string ColumnMapJson { get; set; } = "[]";
    public string? OptionsJson { get; set; }
    public int UseCount { get; set; }
    public DateTime? LastUsedAt { get; set; }
    /// <summary>ผู้ใช้กดยืนยันแม่แบบนี้แล้ว (ไม่ใช่แค่ระบบจำจากการ commit)</summary>
    public bool ConfirmedByUser { get; set; }
    /// <summary>ไฟล์ตรงแม่แบบ + ทุกแถวผ่าน ⇒ ลงให้อัตโนมัติ — opt-in ค่าเริ่ม false (คำตัดสินข้อ 151 (3))</summary>
    public bool AutoCommitWhenClean { get; set; }
}

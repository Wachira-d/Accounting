namespace Accounting.Models.DTOs.Bank;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  ตัวเขียน statement ธนาคารตัวเดียว (รอบ 205 ทีม C · IMPORT_ETL_REVIEW §1 แถว 6–9 · §3 ทีม C)
//
//  ทุกทางเข้าที่สร้าง BankTransaction จาก statement — หน้า "ธนาคาร → นำเข้า statement" (CSV/Excel/OFX) · open-banking file ·
//  ศูนย์นำเข้าข้อมูล (BankStatementImportTarget) · นำเข้าทั่วไป "banktransactions" (ImportExportService · ทีม B) · API v1 —
//  เรียก IBankService.ImportParsedStatementRowsAsync ด้วยรูปข้างล่าง (เดิมมีตัวเขียน 4 ตัว ความหมายยอดคงเหลือ 2 แบบ dedupe 3 แบบ)
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>หนึ่งบรรทัดของ statement ที่แยกจากไฟล์/คำขอแล้ว (ค่ามาตรฐาน — ไม่ใช่ข้อความดิบ)</summary>
/// <param name="Date">วันที่รายการ (เวลาได้ถ้าไฟล์มี)</param>
/// <param name="Withdrawal">เงินออก (บวก)</param>
/// <param name="Deposit">เงินเข้า (บวก) — มีทั้งสองช่องในบรรทัดเดียว = ยอดสุทธิ (ตัวเขียนตัดสินทิศจากเครื่องหมาย)</param>
/// <param name="Balance">ยอดคงเหลือตามธนาคารหลังรายการนี้ · <c>null</c> = ไฟล์ไม่มี/ช่องว่าง (แยกจาก 0.00 จริง)</param>
/// <param name="ExternalId">รหัสรายการที่ไม่ซ้ำจากต้นทาง (OFX FITID · ExternalId ของ API v1) — กันนำเข้าซ้ำแบบตรงตัว ·
/// เก็บลง <c>Reference</c> (รูปเดิมของ API v1 — ตัวกันซ้ำของ v1 อ่านช่องนี้)</param>
/// <param name="RowNumber">เลขแถวในไฟล์ (1-based) สำหรับข้อความถึงผู้ใช้ · 0 = ไม่ทราบ</param>
public sealed record BankStatementLineInput(
    DateTime Date,
    string? Description,
    decimal Withdrawal,
    decimal Deposit,
    decimal? Balance = null,
    string? Reference = null,
    string? Channel = null,
    string? ExternalId = null,
    string? Payee = null,
    int RowNumber = 0);

/// <summary>วิธีกันรายการซ้ำกับของเดิมในบัญชี</summary>
public enum BankStatementDedupeMode
{
    /// <summary>statement (ค่าเริ่ม): รหัสต้นทาง (ExternalId) ตรง ⇒ ซ้ำ · เลขอ้างอิง+วัน+ยอด+ทิศตรง ⇒ ซ้ำ · วัน+ยอด+ทิศตรงแบบใช้ครั้งเดียว:
    /// คำอธิบายตรง ⇒ ซ้ำ (ข้าม) · คำอธิบายต่าง ⇒ ขัดแย้ง (ตาม <see cref="BankStatementConflictPolicy"/>)</summary>
    Statement = 0,
    /// <summary>API v1: กันซ้ำด้วย ExternalId อย่างเดียว (บรรทัดที่ไม่มี ExternalId ลงทุกครั้ง — ความหมายเดิมของ v1 ที่ลูกค้าพึ่งอยู่)</summary>
    ExternalIdOnly = 1,
}

/// <summary>เมื่อวัน+ยอด+ทิศตรงรายการเดิมแต่คำอธิบายต่าง</summary>
public enum BankStatementConflictPolicy
{
    /// <summary>ย้อนทั้งไฟล์แล้วคืนรายการขัดแย้งให้ผู้ใช้เลือก (หน้า "ธนาคาร" ถาม "ใช้ข้อมูลใหม่/เก็บข้อมูลเดิม")</summary>
    Report = 0,
    /// <summary>ทับคำอธิบาย/เลขอ้างอิง/ยอดคงเหลือของรายการเดิม (ผู้ใช้เลือก "ใช้ข้อมูลใหม่")</summary>
    Overwrite = 1,
    /// <summary>เก็บของเดิม ข้ามบรรทัดในไฟล์ (ผู้ใช้เลือก "เก็บข้อมูลเดิม")</summary>
    KeepExisting = 2,
    /// <summary>ลงเป็นรายการใหม่ + คำเตือนให้ตรวจ (open-banking เดิม · ศูนย์นำเข้าเมื่อผู้ใช้เลือก "สร้างใหม่" เอง)</summary>
    InsertAsNew = 3,
}

/// <summary>ไฟล์ต้นทางที่ต้องเก็บเป็นหลักฐาน (ไฟล์แนบชนิด "BankStatement" ของบัญชีธนาคาร · พ.ร.บ.การบัญชี ม.10)</summary>
public sealed record BankStatementSourceFile(string FileName, string ContentType, byte[] Content);

/// <summary>ตัวเลือกของตัวเขียน — ค่าเริ่ม = ความหมายของหน้า "ธนาคาร → นำเข้า statement"</summary>
/// <param name="UpdateStatementSnapshot">ตั้ง StatementBalance/วันที่/CurrentBalance จากยอดคงเหลือล่าสุดของไฟล์ (ยอดตามธนาคาร) ·
/// false = API v1 (ไม่มียอดคงเหลือ · ไม่แตะการ์ดบัญชี)</param>
/// <param name="ClosingBalance">ยอดปิดของ statement (OFX LEDGERBAL) — ใช้เมื่อบรรทัดไม่มียอดคงเหลือรายบรรทัด</param>
/// <param name="SourceAccountNumber">เลขบัญชีตามไฟล์ (OFX ACCTID) — ไม่ตรงบัญชีที่เลือก ⇒ คำเตือน (ไม่บล็อก · คำตัดสินข้อ 150)</param>
/// <param name="SourceCurrency">สกุลเงินตามไฟล์ (OFX CURDEF)</param>
/// <param name="CreatedBy">ค่า CreatedBy ของแถวใหม่ (เช่น "api:v1") · null = ไม่ตั้ง</param>
/// <param name="UploadedByUserId">ผู้อัปโหลดไฟล์ต้นทาง · null = ผู้ใช้ของคำขอปัจจุบัน (JWT)</param>
public sealed record BankStatementWriteOptions(
    BankStatementDedupeMode Dedupe = BankStatementDedupeMode.Statement,
    BankStatementConflictPolicy OnConflict = BankStatementConflictPolicy.Report,
    bool UpdateStatementSnapshot = true,
    bool AutoMatch = true,
    BankStatementSourceFile? SourceFile = null,
    decimal? ClosingBalance = null,
    DateTime? ClosingBalanceDate = null,
    string? SourceAccountNumber = null,
    string? SourceCurrency = null,
    string? CreatedBy = null,
    Guid? UploadedByUserId = null,
    IReadOnlyList<string>? ParserNotes = null);

public enum BankStatementLineOutcome
{
    Created = 0,
    Updated = 1,
    /// <summary>มีอยู่แล้ว (ซ้ำ) — ไม่ลงซ้ำ · <see cref="BankStatementLineResult.ExistingTransactionId"/> = รายการเดิม</summary>
    Duplicate = 2,
    /// <summary>วัน+ยอด+ทิศตรงของเดิมแต่คำอธิบายต่าง (Report = ย้อนทั้งไฟล์ · KeepExisting = ข้าม)</summary>
    Conflict = 3,
    /// <summary>ไม่มีจำนวนเงิน — ไม่ลง</summary>
    Invalid = 4,
}

/// <summary>ผลต่อบรรทัด (ลำดับเดียวกับอินพุต) — ศูนย์นำเข้าใช้ผูกแถว ↔ รายการธนาคาร (ย้อนได้) · API v1 ใช้จับคู่ต่อบรรทัด</summary>
public sealed record BankStatementLineResult(
    int Index,
    BankStatementLineOutcome Outcome,
    Guid? TransactionId,
    Guid? ExistingTransactionId,
    string? Message);

/// <summary>ผลของตัวเขียน</summary>
/// <param name="RolledBack">true = ไม่มีอะไรถูกบันทึก (พบรายการขัดแย้งในโหมด Report)</param>
/// <param name="Warnings">คำเตือนคุณภาพไฟล์ (ยอดไม่ต่อเนื่อง · ไม่มียอดคงเหลือ · เลขบัญชีไม่ตรง · จับคู่อัตโนมัติไม่สำเร็จ ฯลฯ) — แสดงผู้ใช้ทุกข้อ</param>
public sealed record BankStatementWriteResult(
    int Imported,
    int Skipped,
    int Conflicts,
    List<ImportConflict> ConflictDetails,
    List<string> Warnings,
    IReadOnlyList<BankStatementLineResult> Lines,
    bool RolledBack,
    int AutoMatched,
    Guid? SourceFileAttachmentId)
{
    /// <summary>รูปตอบของหน้า "ธนาคาร → นำเข้า statement" (สัญญาเดิม)</summary>
    public ImportBankStatementResponse ToResponse()
        => new(Imported, Skipped, Conflicts, ConflictDetails.Count > 0 ? ConflictDetails : null, Warnings);
}

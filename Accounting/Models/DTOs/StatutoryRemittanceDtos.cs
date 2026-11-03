namespace Accounting.Models.DTOs;

/// <summary>1 รายการ "รอนำส่ง" — ภาษี/ประกันสังคม 1 ประเภท 1 งวด ที่ยังค้าง.</summary>
public record PendingRemittanceItem(
    string RemittanceType,        // SsoSps110 / WhtPnd1 / WhtPnd3 / WhtPnd53 / VatPp30
    string TypeLabel,             // "ประกันสังคม", "ภาษีหัก ณ ที่จ่าย (เงินเดือน)" ...
    string FormCode,              // "สปส.1-10", "ภ.ง.ด.1", "ภ.ง.ด.3", "ภ.ง.ด.53", "ภ.พ.30"
    int PeriodYear,
    int PeriodMonth,
    decimal Amount,               // ยอดค้างนำส่ง (สุทธิ — หักที่นำส่งบางส่วนแล้ว)
    DateTime PaperDueDate,        // กำหนดยื่นกระดาษ
    DateTime EFilingDueDate,      // กำหนดยื่น e-Filing (ขยายแล้ว)
    bool IsOverdue,               // วันนี้ > EFilingDueDate
    decimal LateFeePreview,       // เงินเพิ่มที่ระบบเสนอถ้าจ่ายวันนี้ — ทุกแบบ (Helpers/LatePaymentPenaltyPolicy · รอบ 220): ปกส. ม.49 · ภ.ง.ด. ม.27 · ภ.พ.30/36 ม.89/1
    string PayableAccountCode,    // ผังหนี้ค้างจ่าย (21815/21914/21916/21917/21911)
    // breakdown (nullable — เฉพาะบางประเภท)
    decimal? EmployeeAmount,      // SSO: ส่วนลูกจ้าง
    decimal? EmployerAmount,      // SSO: ส่วนนายจ้าง
    int? PayeeCount,              // WHT: จำนวนผู้ถูกหัก
    decimal? OutputVat,           // VAT: ภาษีขาย
    decimal? InputVat,            // VAT: ภาษีซื้อ
    Guid? RelatedPayrollRunId,    // SSO: รอบเงินเดือนที่ผูก (ถ้ามี)
    // ── "ยื่นแบบแล้วหรือยัง" (คนละเหตุการณ์กับ "จ่ายเงินแล้วหรือยัง") ──
    // เดิม IsOverdue คิดจากวันที่อย่างเดียว แล้วหักกลบเฉพาะเงินที่จ่าย ⇒ งวดที่
    // ผู้ใช้ "ยื่นแบบ" ไปแล้วที่หน้ารายงานภาษี ยังขึ้น "เลยกำหนด N วัน" สีแดง
    // ทั้งที่ยื่นตรงเวลา — จอเล่าเรื่องผิดและผู้ใช้ไม่มีอะไรให้ไล่ต่อ.
    // null = ยังไม่ได้ยื่นแบบ (หรือแบบนี้ยังไม่มีรายงานในระบบ)
    DateTime? ReportFiledAt = null,
    // ── ช่องโหว่ 50 ทวิ (รอบ 170) ── WHT: เอกสารฝั่งซื้อที่หัก ณ ที่จ่ายในงวดนี้แต่ **ยังไม่มีหนังสือรับรอง
    // ที่ออกจริง** (Issued/Printed) ⇒ ไม่รวมใน Amount (ตัวตั้ง = certs ชุดเดียวกับรายงาน/ไฟล์ยื่น) และ
    // RemitAsync บล็อกจนกว่าจะออกครบ — ห้ามนับเงียบ ๆ (ยอดนำส่งจะน้อยกว่าที่หักจริง) · null = ไม่มี
    int? UnissuedWhtCount = null,
    decimal? UnissuedWhtAmount = null,
    // รอบ 201 ทีม TX (B-7): วันที่ใช้เตือน/ตัดสินเลยกำหนด (TaxFilingDeadline.WarnBy) — ภ.พ.36/ภ.ง.ด.54 = วันกระดาษ จนกว่าจะยืนยันว่ามาตรการ
    // e-Filing ครอบ · null = เท่ากับ EFilingDueDate · WarnNote = ข้อความกำกับ (null = ไม่มี)
    DateTime? WarnDueDate = null,
    string? WarnNote = null,
    // รอบ 220 (คำตัดสินข้อ 181): เบี้ยปรับที่ระบบเสนอ (ภ.พ.30 ม.89 ลดตามระเบียบ · แบบอื่น 0) + เหตุผลต่อก้อน (tooltip) — ค่าเสนอ แก้ได้ตอนนำส่ง
    decimal PenaltyPreview = 0m,
    string? LateFeeNote = null,
    string? PenaltyNote = null,
    string? FineNote = null,
    // รอบ 228: ป้ายงวดของแบบรายครึ่งปี/รายปี (ภ.ง.ด.51/50 · กท.20ก) — เซิร์ฟเวอร์คิด หน้าแสดง · null = แบบรายเดือน (หน้าแสดงเดือน/ปีเอง)
    string? PeriodLabel = null,
    // รอบ 228: คำอธิบายยอด (เช่น กท.20ก ยอดรายปีเทียบตั้งค้างจ่ายรายเดือน · ภ.ง.ด.50 หักเครดิต) — null = ไม่มี
    string? AmountNote = null,
    // รอบ 228: ค่าที่หน้าเติมลงช่องเบี้ยปรับ — null = เท่ากับ PenaltyPreview · 0 = กติกายังไม่ยืนยัน (ภ.พ.36) แสดงค่าเสนอเป็นข้อมูลแต่ไม่เติม
    decimal? PenaltyPrefill = null,
    // ฝ่ายค้าน S4: ค่าที่หน้าเติมลงช่องเงินเพิ่ม — null = เท่ากับ LateFeePreview · 0 = กติกายังไม่ยืนยัน (กองทุนเงินทดแทน ม.46)
    decimal? LateFeePrefill = null,
    // ฝ่ายค้าน S6: เงินเพิ่ม §67 ตรี (ภ.ง.ด.50) แยกก้อน — ข้อมูลเท่านั้น ไม่เติมช่อง · 0/null = ไม่มี
    decimal Section67TerPreview = 0m,
    string? Section67TerNote = null,
    // ฝ่ายค้าน S3: แบบนี้ผูกใบสำคัญที่ลงเอง ("ชำระนอกระบบแล้ว") ได้ — เซิร์ฟเวอร์ตัดสิน (ภ.ง.ด.51/50 · กท.20ก)
    bool CanSettleOutside = false);

/// <summary>สรุปหน้านำส่ง — ยอดรวมรอนำส่ง + รายการ.</summary>
public record RemittanceDashboardResponse(
    decimal TotalPending,         // รวมยอดค้างทุกประเภท
    decimal TotalOverdue,         // รวมยอดที่เลยกำหนด
    int OverdueCount,
    List<PendingRemittanceItem> Pending,
    List<RemittanceHistoryItem> RecentHistory,
    // ภ.พ.36 นำส่งแล้ว รอรับรู้ภาษีซื้อ (ขั้นที่ 2) — ว่าง = ไม่มีค้าง
    List<Pp36AwaitingRecognitionItem> Pp36AwaitingRecognition,
    // รอบ 203 ทีม F3 (E-3/E-6): ภ.พ.36 ที่ "ต้องตรวจ" — ใบอนุมัติแล้วไม่มี JE (ไม่นับในยอด) · นำส่งเกิน · null = เส้นทางที่ไม่คำนวณ
    List<Pp36IssueItem>? Pp36Issues = null,
    // รอบ 228 ฝ่ายค้าน S5b/S3: ข้อความที่ต้องเห็นแม้ไม่มีรายการรอนำส่ง (เช่น §67 ตรี ของ ภ.ง.ด.50 ที่ยอดสุทธิ 0) — null/ว่าง = ไม่มี
    List<string>? Advisories = null,
    // ฝ่ายค้าน S3: ภ.ง.ด.51/50 · กท.20ก ที่ครบกำหนดเกิน 24 เดือนแล้วถูกซ่อน (ขอดูด้วย includeOldAnnual=true) — 0 = ไม่มี
    int HiddenOldAnnualCount = 0);

/// <summary>ภ.พ.36 ที่ต้องตรวจ (ไม่นับเงียบ) — Kind: "NoJournal" (ใบอนุมัติแล้วแต่ GL ไม่มี Cr 21912 · ซ่อม JE ก่อน) ·
/// "OverRemitted" (ยอดนำส่งมากกว่าหนี้ 21912 ของใบที่ผูกอยู่)</summary>
public record Pp36IssueItem(
    string Kind,
    int PeriodYear,
    int PeriodMonth,
    Guid? DocumentId,
    string? DocumentNumber,
    decimal Amount,
    string Message);

/// <summary>รายการที่นำส่งไปแล้ว.</summary>
public record RemittanceHistoryItem(
    Guid Id,
    string RemittanceType,
    string FormCode,
    int PeriodYear,
    int PeriodMonth,
    decimal Amount,
    decimal LateFee,
    DateTime PayDate,
    string? FilingNumber,
    Guid? JournalEntryId,
    Guid? ReceiptAttachmentId,
    string? CreatedBy,
    DateTime CreatedAt,
    // ภ.พ.36 เท่านั้น: งวดนี้ "รับรู้ภาษีซื้อ" (11640→11610 เข้า ภ.พ.30) แล้ว
    // หรือยัง — null = ไม่ใช่ ภ.พ.36. UI ใช้สลับปุ่ม "รับรู้" ↔ ป้าย ✓
    // (เดิมปุ่มโชว์ตลอดไม่มีสถานะ ผู้ใช้ไม่รู้ว่ากดแล้วหรือยัง)
    bool? Pp36Recognized = null,
    // รอบ 220: เบี้ยปรับ + ค่าปรับอื่น ที่ลงบัญชี (แถวเดิม = 0)
    decimal Penalty = 0m,
    decimal OtherFine = 0m,
    // รอบ 228: ป้ายงวดของแบบรายครึ่งปี/รายปี/รายตราสาร — null = แบบรายเดือน
    string? PeriodLabel = null,
    // รอบ 228 ฝ่ายค้าน S3: ผูกใบสำคัญที่ลงเอง (ไม่ใช่ JE ของเส้นนำส่ง) — ยกเลิกไม่กลับรายการ
    bool SettledOutside = false);

/// <summary>ภ.พ.36 ที่นำส่งแล้วแต่ยัง "ไม่ได้กดรับรู้ภาษีซื้อ" — ภาษีซื้อจึงยัง
/// ไม่ขึ้นใน ภ.พ.30 (ผู้ใช้เจอจริง: นำส่งแล้วหาใบใน ภ.พ.30 ไม่เจอ เพราะขั้นที่
/// 2 ซ่อนอยู่ในแท็บประวัติ) — dashboard ต้องดันขึ้นมาให้เห็นจนกว่าจะกดรับรู้</summary>
/// <summary>ใบของงวด ภ.พ.36 ที่ "รับรู้ภาษีซื้อแล้ว" — ตอบคำถาม "เข้า ภ.พ.30
/// งวดไหน แล้วทำไมเปิดรายงานไม่เจอ": บอกเดือนเคลมต่อใบ + สถานะรายงานงวดนั้น
/// (ไม่มีรายงาน / มีแต่สร้างก่อนรับรู้จึงยังไม่มีบรรทัดใบนี้ / อยู่ในรายงานแล้ว /
/// ยื่นแล้ว) — ผู้ใช้แก้เดือนเคลมต่อใบได้จากหน้านำส่งเลย</summary>
public record Pp36RecognizedDocItem(
    Guid DocumentId,
    string DocumentNumber,
    string SupplierName,
    decimal VatAmount,
    int ClaimYear,
    int ClaimMonth,
    string? RdReceiptNumber,
    // "none" = ยังไม่มีรายงาน ภ.พ.30 งวดเคลม · "Draft"/"Filed"/"Submitted" = สถานะรายงาน
    string ReportStatus,
    // true = บรรทัดใบนี้อยู่ในรายงานงวดเคลมแล้ว (non-excluded) — false + มีรายงาน
    // = รายงานสร้างก่อนรับรู้ ต้องกด "สร้างใหม่" เพื่อดึงใบเข้า
    bool InReport);

public record Pp36AwaitingRecognitionItem(
    int PeriodYear,
    int PeriodMonth,
    decimal VatAmount,        // ภาษีซื้อที่รอรับรู้ (บาท) = Σ ยอดที่พัก 11640 จริงใน GL ของใบที่อยู่ในรายการนำส่งนี้ (รอบ 203 — เดิม Σ VatAmount)
    int DocumentCount,
    DateTime RemittedAt,
    // รอบ 203 (คำตัดสินข้อ 133): งวดหนึ่งนำส่งได้หลายครั้ง ⇒ รับรู้ต่อ "รายการนำส่ง" · เลขใบเสร็จที่กรอกตอนนำส่ง (null = ยังไม่กรอก ต้องกรอกก่อนรับรู้)
    Guid? RemittanceId = null,
    string? RdReceiptNumber = null);

/// <summary>คำขอนำส่ง 1 งวด — ระบบ post JE (Dr หนี้ค้างจ่าย / Cr ธนาคาร) +
/// บันทึก StatutoryRemittance + (option) แนบใบเสร็จ.</summary>
public record RemitRequest(
    string RemittanceType,
    int PeriodYear,
    int PeriodMonth,
    DateTime PayDate,
    Guid? BankAccountId = null,   // BankAccount.Id → ใช้ LinkedAccountId เป็นผัง Cr
    Guid? BankGlAccountId = null, // ChartOfAccount.Id (เงินสด/ธนาคาร/ช่องจ่ายอื่น) → ใช้เป็นผัง Cr ตรง ๆ
    string? FilingNumber = null,
    Guid? ReceiptAttachmentId = null,
    bool IncludeLateFee = true,   // false = ไม่ลงเงินเพิ่ม (ทุกแบบ)
    string? Note = null,
    // เงินเพิ่มที่ผู้ใช้แก้ (ทุกแบบ · รอบ 220) — null = ปกส./ภ.พ.36 ใช้ค่าที่ระบบเสนอ (พฤติกรรมเดิม) · แบบอื่น = 0 (ห้ามประทับค่าที่ผู้ใช้ไม่เห็น)
    decimal? LateSurcharge = null,
    // รอบ 220 (คำตัดสินข้อ 181): เบี้ยปรับ / ค่าปรับอาญา-อื่น ๆ ที่จ่ายจริง — null = 0 · ลงผังค่าปรับ (รายจ่ายต้องห้าม §65 ตรี(6))
    decimal? Penalty = null,
    decimal? OtherFine = null,
    // รอบ 220 ฝ่ายค้านข้อ 1: ยื่นแบบกระดาษ ⇒ ครบกำหนดวันกระดาษเสมอ (ไม่มีสิทธิ์ขยาย e-Filing)
    bool PaperFiling = false);

/// <summary>รอบ 228 — จ่ายอากรแสตมป์ (หน้าอากรแสตมป์ → <c>RemitStampDutyAsync</c>) · DutyAmount null = ยอดบนรายการ ·
/// Surcharge/OtherFine null = 0 (หน้าเติมค่าเสนอให้เห็นก่อนกด) · ExpenseAccountId null = 54810</summary>
public record StampDutyRemitRequest(
    DateTime PayDate,
    string? RdReceiptNumber,
    Guid? BankAccountId = null,
    Guid? BankGlAccountId = null,
    decimal? DutyAmount = null,
    decimal? Surcharge = null,
    decimal? OtherFine = null,
    Guid? ExpenseAccountId = null);

/// <summary>รอบ 228 ฝ่ายค้าน S3 — "ชำระนอกระบบแล้ว": ผูกใบสำคัญ (JE) ที่ล้างหนี้ ภ.ง.ด.51/50 · กท.20ก ไว้แล้วกับงวด (ไม่ลง JE ใหม่ · audit) ·
/// ใช้เมื่อชำระด้วยใบสำคัญจ่ายก่อนมีเส้นนำส่ง แล้วระบบยังขึ้นเป็นค้าง</summary>
public record SettledOutsideRequest(
    string RemittanceType,
    int PeriodYear,
    int PeriodMonth,
    string JournalEntryNumber,
    string? Note = null);

public record RemitResult(
    Guid Id,
    Guid? JournalEntryId,
    decimal Amount,
    decimal LateFee,
    string Message,
    decimal Penalty = 0m,
    decimal OtherFine = 0m);

// ══════════════════════════════════════════════════════════════════════════
//  ปฏิทินนำส่ง (Filing calendar) — "เดือนไหนยื่นแล้ว/ยัง" แบบตาราง แบบ × เดือน
//
//  ต่างจาก RemittanceDashboardResponse ตรงที่ dashboard ตอบว่า "ยังค้างเท่าไร"
//  จึงตัดงวดที่ยอด = 0 ทิ้ง แต่กฎหมายไทยบังคับให้ยื่น **แม้ไม่มียอด**:
//    • ภ.พ.30  — ผู้จด VAT ต้องยื่นทุกเดือนแม้ไม่มีรายรับ (§83) ไม่ยื่น = ปรับอาญา
//    • สปส.1-10 — นายจ้างที่ขึ้นทะเบียนต้องยื่นทุกเดือนแม้ไม่มีค่าจ้าง
//    • ภ.ง.ด.1 — ยื่นทุกเดือนที่มีการจ่ายเงินได้ 40(1)(2) แม้ภาษีหัก = 0
//  ปฏิทินนี้จึงต้องแสดง "ต้องยื่นแต่ยอด 0" (IsNil) และ "ระบบยังไม่มีข้อมูล"
//  (Unknown) เป็นสถานะของตัวเอง ไม่ใช่ปล่อยให้หายไปเงียบ ๆ
// ══════════════════════════════════════════════════════════════════════════

/// <summary>1 ช่องในปฏิทิน = แบบ 1 ชนิด × งวด 1 เดือน.</summary>
public record FilingCalendarCell(
    int Year,
    int Month,
    // Filed | Partial | Pending | Unknown | NotRequired
    string Status,
    decimal Amount,               // ยอดที่ต้องนำส่ง (ติดลบ = ขอคืน)
    decimal LateFee,              // เงินเพิ่มประมาณการถ้าจ่ายวันนี้ (ทุกแบบ · Helpers/LatePaymentPenaltyPolicy · รอบ 220)
    DateTime PaperDueDate,
    DateTime EFilingDueDate,
    bool Overdue,                 // เลยกำหนด (WarnDueDate — ปกติ = e-Filing) และยังไม่ครบ
    int DaysToDue,                // ติดลบ = เลยมาแล้วกี่วัน
    bool FormFiled,               // ยื่นแบบแล้ว (TaxReport.Status = Filed)
    DateTime? FiledAt,
    bool Remitted,                // จ่ายเงินแล้ว (StatutoryRemittance / SsoSettledAt)
    DateTime? RemittedAt,
    string? FilingNumber,
    bool HasReceipt,              // แนบใบเสร็จ/หลักฐานแล้ว
    bool IsNil,                   // ยอด 0 → ต้องยื่น "แบบเปล่า"
    string Hint,                  // สิ่งที่ต้องทำ / เหตุผลที่ยังไม่รู้ยอด
    string? ActionUrl,            // ลิงก์ไปหน้าที่ทำงานนั้นได้ทันที
    DateTime? WarnDueDate = null); // รอบ 201 B-7: วันที่ใช้เตือน (Overdue/DaysToDue คิดจากวันนี้) — ภ.พ.36/ภ.ง.ด.54 = วันกระดาษ

/// <summary>1 แถว = แบบยื่น 1 ชนิด ตลอดช่วงเดือนที่ขอ.</summary>
public record FilingCalendarRow(
    string RemittanceType,
    string FormCode,
    string TypeLabel,
    string LegalNote,             // อ้างมาตรา/กฎที่บังคับให้ยื่น
    bool AlwaysRequired,          // ต้องยื่นทุกเดือนแม้ยอด 0
    bool Applicable,              // บริษัทนี้อยู่ในข่ายต้องยื่นแบบนี้ไหม
    string? NotApplicableReason,
    List<FilingCalendarCell> Cells);

/// <summary>ปฏิทินนำส่งทั้งตาราง + สรุปหัวข้อสำหรับ dashboard.</summary>
public record FilingCalendarResponse(
    List<string> Periods,         // "2026-07" เรียงเก่า→ใหม่
    List<FilingCalendarRow> Rows,
    int OverdueCount,
    decimal OverdueAmount,
    decimal OverdueLateFee,
    int DueSoonCount,             // ครบกำหนดภายใน 7 วัน
    int UnknownCount,             // ต้องยื่นแต่ระบบยังไม่มีข้อมูล
    int FiledCount,
    int RequiredCount,            // ช่องที่ต้องยื่นทั้งหมดในช่วง
    DateTime? NextDueDate,
    string? NextDueLabel,
    string Headline);             // ข้อความสรุป 1 บรรทัดสำหรับ dashboard

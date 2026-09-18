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
    decimal LateFeePreview,       // เงินเพิ่มประมาณการถ้าจ่ายวันนี้ (ปกส.; อื่น ๆ = 0)
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
    decimal? UnissuedWhtAmount = null);

/// <summary>สรุปหน้านำส่ง — ยอดรวมรอนำส่ง + รายการ.</summary>
public record RemittanceDashboardResponse(
    decimal TotalPending,         // รวมยอดค้างทุกประเภท
    decimal TotalOverdue,         // รวมยอดที่เลยกำหนด
    int OverdueCount,
    List<PendingRemittanceItem> Pending,
    List<RemittanceHistoryItem> RecentHistory,
    // ภ.พ.36 นำส่งแล้ว รอรับรู้ภาษีซื้อ (ขั้นที่ 2) — ว่าง = ไม่มีค้าง
    List<Pp36AwaitingRecognitionItem> Pp36AwaitingRecognition);

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
    bool? Pp36Recognized = null);

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
    decimal VatAmount,        // ภาษีซื้อที่รอรับรู้ (Σ VatAmount ของใบที่ยังพัก 11640)
    int DocumentCount,
    DateTime RemittedAt);

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
    bool IncludeLateFee = true,   // ปกส. — รวมเงินเพิ่ม §49
    string? Note = null);

public record RemitResult(
    Guid Id,
    Guid? JournalEntryId,
    decimal Amount,
    decimal LateFee,
    string Message);

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
    decimal LateFee,              // เงินเพิ่มประมาณการถ้าจ่ายวันนี้ (ปกส.)
    DateTime PaperDueDate,
    DateTime EFilingDueDate,
    bool Overdue,                 // เลยกำหนด e-Filing และยังไม่ครบ
    int DaysToDue,                // ติดลบ = เลยมาแล้วกี่วัน
    bool FormFiled,               // ยื่นแบบแล้ว (TaxReport.Status = Filed)
    DateTime? FiledAt,
    bool Remitted,                // จ่ายเงินแล้ว (StatutoryRemittance / SsoSettledAt)
    DateTime? RemittedAt,
    string? FilingNumber,
    bool HasReceipt,              // แนบใบเสร็จ/หลักฐานแล้ว
    bool IsNil,                   // ยอด 0 → ต้องยื่น "แบบเปล่า"
    string Hint,                  // สิ่งที่ต้องทำ / เหตุผลที่ยังไม่รู้ยอด
    string? ActionUrl);           // ลิงก์ไปหน้าที่ทำงานนั้นได้ทันที

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

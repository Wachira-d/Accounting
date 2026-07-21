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
    Guid? RelatedPayrollRunId);   // SSO: รอบเงินเดือนที่ผูก (ถ้ามี)

/// <summary>สรุปหน้านำส่ง — ยอดรวมรอนำส่ง + รายการ.</summary>
public record RemittanceDashboardResponse(
    decimal TotalPending,         // รวมยอดค้างทุกประเภท
    decimal TotalOverdue,         // รวมยอดที่เลยกำหนด
    int OverdueCount,
    List<PendingRemittanceItem> Pending,
    List<RemittanceHistoryItem> RecentHistory);

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
    DateTime CreatedAt);

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

namespace Accounting.Models.DTOs.Compliance;

public record ComplianceFilingResponse(
    Guid Id, string FilingType, string FormCode, int Year, int? Month,
    DateTime DueDate, DateTime? FiledDate, string Status,
    string? SubmissionReference, string? ConfirmationNumber,
    decimal? TaxAmount, decimal? PenaltyAmount,
    string? ValidationErrors, int DaysUntilDue,
    // ── หลักฐานการยื่นที่ระบบ "มีจริง" (ไม่ใช่สถานะที่ระบบประทับเอง) ──
    // เดิม `SubmitFilingAsync` แต่งเลข `SUB-…`/`CONF-…` จาก GUID แล้วประทับ
    // `Filed` ⇒ แถวที่ไม่เคยถูกส่งไปไหน มี "เลขยืนยัน" โชว์อยู่ · การเลิกแต่งเลข
    // (รอบ 183) ทำให้แถวใหม่เป็น null แต่**สถานะยังเป็น Filed เหมือนกันเป๊ะ**
    // ⇒ ถ้าไม่มีช่องนี้ ผู้เรียก API แยกไม่ออกระหว่าง "ยื่นแล้วมีเลขยืนยัน"
    // กับ "ผู้ใช้แจ้งว่ายื่นแล้ว" — ระดับหลักฐานใช้ชื่อชุดเดียวกับฝั่งรายงานภาษี
    // (`Helpers/TaxFilingEvidence`) เพื่อไม่ให้มีคำศัพท์ชุดที่สอง
    /// <summary>NotFiled / DeclaredByUser / ConfirmedByFilingNumber</summary>
    string? FilingEvidence = null,
    /// <summary>ประโยคที่บอก "สิ่งที่ระบบรู้จริง" — ห้ามเกินความจริง</summary>
    string? FilingEvidenceDetail = null);

public record FileComplianceRequest(string? Notes, string? ConfirmationNumber);

public record ComplianceCalendarResponse(
    int Year, int Month, string FilingType, string FormCode,
    DateTime DueDate, string Status, bool IsOverdue);

public record ComplianceValidationResponse(
    string FilingType, bool IsValid, List<string> Errors, List<string> Warnings);

public record CreateComplianceFilingRequest(string FilingType, string FormCode, int Year, int? Month, DateTime DueDate, string? Notes);

public record UpdateComplianceFilingRequest(DateTime? DueDate, string? Notes, string? Status);

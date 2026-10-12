using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Tax;

// ===== Withholding Tax Certificate (หนังสือรับรองหัก ณ ที่จ่าย) =====
public record CreateWithholdingTaxCertRequest(
    Guid PayeeContactId,
    TaxType? TaxFormType,
    int TaxYear,
    int TaxMonth,
    WithholdingTaxCertType CertificateType,
    List<WithholdingTaxCertLineRequest> Lines,
    /// <summary>เอกสารต้นทางที่ใบนี้ครอบ (ถ้ามี) — จำเป็นเพื่อให้รายงาน ภ.ง.ด.
    /// รู้ว่าเอกสารนั้น "ออกหนังสือรับรองแล้ว" และไม่ขึ้นแถวเตือนซ้ำอีกแถว
    /// (B1: cert ที่คีย์มือเดิมไม่มี DocumentId ⇒ ยอดเดียวได้ 2 บรรทัด)</summary>
    Guid? DocumentId = null);

public record WithholdingTaxCertLineRequest(
    string IncomeTypeCode,
    string IncomeDescription,
    DateTime PaymentDate,
    decimal IncomeAmount,
    decimal TaxRate,
    decimal TaxAmount,
    string? Condition);

public record WithholdingTaxCertResponse(
    Guid Id,
    string CertificateNumber,
    Guid PayerCompanyId,
    string PayerName,
    string PayerTaxId,
    string? PayerBranchCode,
    string PayerAddress,
    Guid PayeeContactId,
    string PayeeName,
    string? PayeeTaxId,
    string? PayeeBranchCode,
    string? PayeeAddress,
    TaxType TaxFormType,
    string TaxFormName,
    int TaxYear,
    int TaxMonth,
    WithholdingTaxCertType CertificateType,
    WithholdingTaxCertStatus Status,
    decimal TotalIncomeAmount,
    decimal TotalTaxAmount,
    List<WithholdingTaxCertLineResponse> Lines,
    DateTime? IssuedDate,
    DateTime CreatedAt,
    Guid? DocumentId = null,
    string? DocumentNumber = null,
    Guid? SourcePayrollRunId = null,
    // true เมื่อแก้ไขได้ = Draft + สร้างเอง (ไม่ผูกเอกสาร/payroll)
    bool IsEditable = false,
    // รอบ 200 ทีม WF (ฝ่ายค้าน W-5 · คำตัดสินข้อ 40): คำเตือนตอนออก/แก้ใบ (ไม่บล็อก) — อัตรา ภ.ง.ด.54 ไม่ตรงตัวตัดสิน ม.70 ·
    // ภ.พ.36 ของเอกสารต้นทางไม่รวมภาษีที่ออกแทน · null = ไม่ได้ตรวจ (ตอบจาก GET) · [] = ตรวจแล้วไม่มีปัญหา
    IReadOnlyList<string>? Warnings = null,
    // รอบ 203: ข้อความ "ระบุ" ของแถว 5/6 บนแบบ 50 ทวิ (คีย์ "5"/"6" · ไม่มีคีย์ = ไม่มีอะไรระบุ) — คำนวณจากตารางกลาง
    // ThaiWhtRateTable.CertificateRowSpecification ตัวเดียว ให้หน้าพรีวิวแสดงอย่างเดียว (server computes · page displays)
    IReadOnlyDictionary<string, string>? CertificateRowSpecs = null);

public record WithholdingTaxCertLineResponse(
    Guid Id,
    string IncomeTypeCode,
    string IncomeTypeName,
    string IncomeDescription,
    DateTime PaymentDate,
    decimal IncomeAmount,
    decimal TaxRate,
    decimal TaxAmount,
    string? Condition,
    // "รายการ" ที่แบบ 50 ทวิ พิมพ์ในช่อง (ระบุ) ของแถว 6 — `WhtCertFormText.LineSpec` (หน้าเว็บแสดงอย่างเดียว ไม่คิดเอง)
    string? CertificateSpecText = null);

public enum WithholdingTaxCertType
{
    Withhold = 1,      // หักภาษี ณ ที่จ่าย
    PayAlways = 2      // ออกภาษีให้ตลอดไป
}

public enum WithholdingTaxCertStatus
{
    Draft = 0,
    Issued = 1,
    Voided = 2,
    Printed = 3
}

// ===== Auto-generate DTOs =====
public record AutoGenerateWhtRequest(
    Guid DocumentId,
    bool AutoIssue = false);

public record BulkGenerateWhtRequest(
    int Year,
    int Month,
    bool AutoIssue = false);

public record BulkGenerateWhtResponse(
    int TotalDocuments,
    int Generated,
    int Skipped,
    List<string> SkippedReasons,
    List<WithholdingTaxCertResponse> Certificates);

/// <summary>แถวในลิสต์ "รอออกใบหัก ณ ที่จ่าย" — <c>Contact*</c> = ผู้รับเงินของเอกสาร · <c>Payee*</c> = ผู้ถูกหักภาษีที่ 50 ทวิ จะออกในนาม
/// (ต่างกันได้ เช่น ประกันผ่านโบรกเกอร์) · <c>PendingLabel</c> = เหตุที่ยังไม่มีใบ คำนวณที่เซิร์ฟเวอร์ (<c>WhtCertIssuePlan.PendingLabel</c>)</summary>
public record PendingWhtDocumentResponse(
    Guid DocumentId,
    string DocumentNumber,
    string DocumentType,
    DateTime DocumentDate,
    Guid ContactId,
    string ContactName,
    string? ContactTaxId,
    decimal SubTotal,
    decimal WithholdingTaxAmount,
    List<PendingWhtLineInfo> Lines,
    Guid? PayeeContactId = null,
    string? PayeeName = null,
    string? PayeeTaxId = null,
    bool PayeeDiffersFromContact = false,
    bool AutoIssueEnabled = true,
    string? AutoIssueFailedReason = null,
    string? PendingLabel = null);

/// <summary>ผลของการออก 50 ทวิ อัตโนมัติจากเอกสาร (<c>IWithholdingTaxCertService.TryAutoIssueForDocumentAsync</c>) ·
/// <paramref name="FailureReason"/> ≠ null = พยายามออกแล้วล้ม (เอกสารติดป้าย + อยู่ในลิสต์รอออกใบ) — ผู้เรียกต้องส่งต่อถึงผู้ใช้</summary>
public sealed record WhtAutoIssueOutcome(
    WhtCertIssueAction Action,
    Guid? CertificateId,
    string? CertificateNumber,
    string? FailureReason,
    string Message)
{
    public bool Failed => FailureReason != null;

    /// <summary>ข้อความที่ต้องบอกผู้ใช้ (null = ไม่มีอะไรต้องบอก) — ล้ม · หรือไม่ออกเพราะตั้งให้ออกเอง</summary>
    public string? UserNotice => Failed
        ? Message
        : Action == WhtCertIssueAction.ManualPending
            ? "ยังไม่ได้ออก 50 ทวิ: " + Message
            : null;
}

public record PendingWhtLineInfo(
    string Description,
    string? IncomeTypeCode,
    decimal Amount,
    decimal WithholdingTaxRate,
    decimal WithholdingTaxAmount);

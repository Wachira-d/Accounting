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
    bool IsEditable = false);

public record WithholdingTaxCertLineResponse(
    Guid Id,
    string IncomeTypeCode,
    string IncomeTypeName,
    string IncomeDescription,
    DateTime PaymentDate,
    decimal IncomeAmount,
    decimal TaxRate,
    decimal TaxAmount,
    string? Condition);

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
    List<PendingWhtLineInfo> Lines);

public record PendingWhtLineInfo(
    string Description,
    string? IncomeTypeCode,
    decimal Amount,
    decimal WithholdingTaxRate,
    decimal WithholdingTaxAmount);

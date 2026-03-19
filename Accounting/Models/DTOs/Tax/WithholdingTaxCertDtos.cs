using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Tax;

// ===== Withholding Tax Certificate (หนังสือรับรองหัก ณ ที่จ่าย) =====
public record CreateWithholdingTaxCertRequest(
    Guid PayeeContactId,
    TaxType TaxFormType,
    int TaxYear,
    int TaxMonth,
    WithholdingTaxCertType CertificateType,
    List<WithholdingTaxCertLineRequest> Lines);

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
    DateTime CreatedAt);

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

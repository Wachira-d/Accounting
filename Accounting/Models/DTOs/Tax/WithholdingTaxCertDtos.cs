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
    // รอบ 204: ช่องที่หน้าพิมพ์ 50 ทวิ (wht.html) ต้องติ๊ก — ดัชนีใน WhtCertFormText.FormBoxes / PayerConditions จากตัวตัดสินเดียว
    // WhtCertFormText.FormBoxIndex / PayerConditionIndex (ตัวเดียวกับ HTML + QuestPDF · หน้าไม่จับคู่ชื่อ enum เอง) · null = ไม่ติ๊กช่องใด
    int? FormBoxIndex = null,
    int? PayerConditionIndex = null);

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
    string? CertificateSpecText = null,
    // แถวบนแบบ 50 ทวิ ("1" "2" "3" "4a" "4b" "5" "6") — `ThaiWhtRateTable.CertificateRow` ตัวเดียวกับ renderer ทั้งสอง (ฝ่ายค้าน M6 ·
    // เดิมหน้าเว็บจับคู่รหัสตรงตัวเอง ⇒ รหัสแบบมาตรา "40(8)" ตกแถว 6 บนหน้าเว็บ แต่ PDF ลงแถว 5)
    string? CertificateRow = null);

public enum WithholdingTaxCertType
{
    Withhold = 1,      // หักภาษี ณ ที่จ่าย
    PayAlways = 2,     // ออกภาษีให้ตลอดไป
    PayOnce = 3        // ออกภาษีให้ครั้งเดียว (คำตัดสินเจ้าของ 2026-10-12 · DECISIONS ข้อ 148)
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
    bool AutoIssue = false,
    // รอบ 204 (ข้อ 143): ใบหลายผู้ถูกหัก — ออกใบของผู้ถูกหักรายนี้ (ใบรายเดียวไม่ต้องส่ง)
    Guid? PayeeContactId = null);

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
    string? PendingLabel = null,
    /// <summary>รอบ 204 (ข้อ 143): ใบหลายผู้ถูกหัก — แถวต่อผู้ถูกหัก (ยอด · มีใบแล้วไหม) · ใบรายเดียว = null</summary>
    List<PendingWhtPayeeInfo>? Payees = null,
    /// <summary>รอบ 204 (ข้อ 142/145): 50 ทวิ ที่ออกแล้วต้องตรวจ/ออกใหม่ (<c>WhtCertReviewNote</c>) — ใบโผล่ในลิสต์แม้มีใบแล้ว</summary>
    string? ReviewReason = null);

/// <summary>ผู้ถูกหักหนึ่งรายของใบหลายผู้ถูกหัก (รอบ 204 ข้อ 143) — ยอด/แบบมาจากเซิร์ฟเวอร์ (<c>WhtPayeeSplit</c>)</summary>
public record PendingWhtPayeeInfo(
    Guid PayeeContactId,
    string? PayeeName,
    string? PayeeTaxId,
    decimal WithholdingTaxAmount,
    bool HasCertificate,
    string? CertificateNumber);

/// <summary>รอบ 204 (ข้อ 145): เปลี่ยนผู้ถูกหักภาษีหลังอนุมัติ — <c>WhtPayeeContactId</c> หัวเอกสาร (null/Guid.Empty = ผู้รับเงินของเอกสาร) ·
/// <c>Lines</c> ผู้ถูกหักรายบรรทัด (ไม่ส่ง = คงเดิม) · <c>Reason</c> บังคับ (audit)</summary>
public record ChangeWhtPayeeRequest(
    Guid? WhtPayeeContactId,
    List<ChangeWhtPayeeLine>? Lines,
    string Reason);

public record ChangeWhtPayeeLine(Guid LineId, Guid? WhtPayeeContactId);

/// <summary>รอบ 204: รับทราบป้าย "50 ทวิ ต้องตรวจ" — เหตุผล/สิ่งที่ทำไปแล้ว (บังคับ · audit)</summary>
public record WhtCertReviewAckRequest(string? Reason);

/// <summary>ผลการเปลี่ยนผู้ถูกหัก — ใบที่ยกเลิก · ใบที่ออกใหม่ · JE ปรับปรุงผัง (null = แบบไม่เปลี่ยน ไม่ต้องย้ายผัง) · ข้อความถึงผู้ใช้</summary>
public record ChangeWhtPayeeResponse(
    List<string> VoidedCertificates,
    List<string> IssuedCertificates,
    string? AdjustmentJournalNumber,
    List<string> Notices);

/// <summary>ผลของการออก 50 ทวิ อัตโนมัติจากเอกสาร (<c>IWithholdingTaxCertService.TryAutoIssueForDocumentAsync</c>) ·
/// <paramref name="FailureReason"/> ≠ null = พยายามออกแล้วล้ม (เอกสารติดป้าย + อยู่ในลิสต์รอออกใบ) — ผู้เรียกต้องส่งต่อถึงผู้ใช้</summary>
public sealed record WhtAutoIssueOutcome(
    WhtCertIssueAction Action,
    Guid? CertificateId,
    string? CertificateNumber,
    string? FailureReason,
    string Message,
    // รอบ 204 (ข้อ 143): ใบหลายผู้ถูกหัก — ทุกใบที่ออกในการเรียกครั้งนี้ (ใบรายเดียว = null · ใช้ CertificateId ตามเดิม)
    IReadOnlyList<Guid>? CertificateIds = null)
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

// ===== รอบ 204 (คำตัดสินข้อ 148 (4)) — พรีวิวผู้จ่ายออกภาษีแทน: หน้าเว็บส่งฐาน/อัตรา เซิร์ฟเวอร์คิดด้วย Helpers/WhtGrossUp ตัวเดียว (หน้าเว็บไม่มีสูตร) =====
public record WhtGrossUpPreviewLine(decimal Base, decimal RatePercent);

/// <param name="SubTotal">ฐานรวมของเอกสาร (หลังส่วนลด + ผลต่างปัดเศษ) — ส่งมาเมื่อต้องการ "ยอดจ่ายผู้รับ" · null = ไม่คิด</param>
/// <param name="Vat">VAT รวมของเอกสาร (คู่กับ SubTotal)</param>
public record WhtGrossUpPreviewRequest(
    WithholdingTaxCertType CertType,
    List<WhtGrossUpPreviewLine>? Lines,
    decimal? SubTotal = null,
    decimal? Vat = null);

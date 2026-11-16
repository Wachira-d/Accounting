using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Tax;

// ===== รอบ 271 — ผูกใบสำคัญจ่ายกับ 50 ทวิ ที่ออกไว้แล้ว (ตัวตัดสินเดียว Helpers/WhtCertLinkPlan · หน้าเว็บแสดงอย่างเดียว) =====

/// <summary>ข้อตรวจหนึ่งข้อ (รหัสคงที่ + ข้อความไทย)</summary>
public record WhtLinkIssueDto(string Code, string Message);

/// <summary>ผลตรวจของใบผู้สมัครหนึ่งใบ — <paramref name="Verdict"/> เป็นชื่อ (Ok · OkWithWarnings · Blocked)</summary>
public record WhtLinkCandidateDto(
    Guid CertId,
    string CertificateNumber,
    string Status,
    string StatusLabel,
    string FormLabel,
    int TaxYear,
    int TaxMonth,
    string PayeeName,
    decimal TotalIncomeAmount,
    decimal TotalTaxAmount,
    string Verdict,
    IReadOnlyList<WhtLinkIssueDto> Blocks,
    IReadOnlyList<WhtLinkIssueDto> Warnings,
    /// <summary>ข้อความสั้นของคอลัมน์ "ผลตรวจ" (เซิร์ฟเวอร์ประกอบ)</summary>
    string ResultLabel);

/// <summary>ฝั่งการจ่ายที่ใช้เทียบ (เพื่อแสดงตารางเทียบ — ค่าทั้งหมดจากตัวออกใบตัวเดียว)</summary>
public record WhtLinkPvSideDto(
    Guid? DocumentId,
    string? DocumentNumber,
    string PayeeName,
    string? PayeeTaxId,
    string PayeeBranch,
    string FormLabel,
    string CertTypeLabel,
    decimal IncomeAmount,
    decimal TaxAmount,
    int PaymentYear,
    int PaymentMonth,
    IReadOnlyList<string> IncomeCodes);

/// <summary>รายการผู้สมัคร · <paramref name="FiledBlock"/> ≠ null = เปลี่ยนการผูกไม่ได้เลย (271-0-2) — หน้าเว็บโชว์กล่องแดงแทนตาราง ·
/// <paramref name="HiddenReason"/> ≠ null = ตัวเลือก "ผูก" ใช้ไม่ได้กับใบนี้ (หลายผู้ถูกหัก · e-Withholding · ไม่มีผู้สมัคร) พร้อมเหตุผล (ไม่ซ่อนเงียบ)</summary>
public record WhtLinkCandidatesResponse(
    WhtLinkPvSideDto? Pv,
    IReadOnlyList<WhtLinkCandidateDto> Candidates,
    string? FiledBlock,
    string? HiddenReason,
    /// <summary>ใบปัจจุบันที่จะถูกยกเลิกเมื่อเปลี่ยนการผูก (null = ผูกครั้งแรก)</summary>
    Guid? CurrentCertId,
    string? CurrentCertNumber,
    string? UnlinkedNotice);

/// <summary>แถวตารางเทียบ (ป้าย · ค่าฝั่งการจ่าย · ค่าฝั่งใบ · ผล ✅/⚠️/⛔ เป็นชื่อ)</summary>
public record WhtLinkCompareRow(string Label, string PvValue, string CertValue, string Result);

/// <summary>ผลตรวจเต็มก่อนกดยืนยัน (ไม่เขียนอะไร)</summary>
public record WhtLinkCheckResponse(
    WhtLinkCandidateDto Candidate,
    WhtLinkPvSideDto Pv,
    IReadOnlyList<WhtLinkCompareRow> Rows,
    Guid? CurrentCertId,
    string? CurrentCertNumber,
    /// <summary>ข้อความเรื่องใบเดิมที่จะหลุดการผูก (ยกเลิกเสมอ · 271-0-1) · null = ผูกครั้งแรก</summary>
    string? UnlinkedNotice,
    bool CanLink);

/// <summary>คำขอผูก/เปลี่ยนการผูก — <b>ไม่มี</b> ช่องรับทราบเดือนที่ยื่นแล้ว (271-0-2) และไม่มีทางเลือก "ลบใบเดิม" (271-0-1)</summary>
/// <param name="ExpectedCurrentCertId">ฝ่ายค้าน M6: ใบปัจจุบันที่ผู้ใช้เห็นตอนเปิดโมดัล (null = ยังไม่มีใบ) — ไม่ตรงของจริง ⇒ 409</param>
public record WhtLinkRequest(Guid CertId, Guid? PaymentId, string? Reason, List<string>? AcknowledgeWarnings, Guid? ExpectedCurrentCertId = null);

public record WhtLinkResult(Guid LinkedCertId, string LinkedCertNumber, Guid? VoidedCertId, string? VoidedCertNumber, string Message);

/// <summary>คำถามผู้สมัครจากฟอร์มใบสำคัญจ่ายที่ยังไม่บันทึก (ค่าที่ฟอร์มแสดงอยู่)</summary>
public record WhtLinkFormQuery(
    Guid PayeeContactId,
    decimal Wht,
    decimal Income,
    DateTime Date,
    WithholdingTaxCertType? CertType,
    string? IncomeCodes,
    bool IsForeignService,
    Guid? DocumentId,
    Guid? SelectedCertId);

/// <summary>50 ทวิ ของใบสำคัญจ่ายหนึ่งใบ (กล่องในหน้ารายละเอียด)</summary>
public record WhtDocumentCertRow(
    Guid Id,
    string CertificateNumber,
    string Status,
    string StatusLabel,
    string FormLabel,
    int TaxYear,
    int TaxMonth,
    decimal TotalTaxAmount,
    Guid? SourcePaymentId,
    string OriginLabel,
    /// <summary>เปลี่ยนการผูกได้ไหม (เซิร์ฟเวอร์ตัดสิน) · false ⇒ ปุ่มปิด + <see cref="RelinkBlockedReason"/></summary>
    bool CanRelink,
    string? RelinkBlockedReason);

public record WhtDocumentLinkStatus(
    bool Applies,
    IReadOnlyList<WhtDocumentCertRow> Certs,
    /// <summary>ปุ่ม "ผูกกับ 50 ทวิ ที่ออกไว้แล้ว" (ใบยังไม่มีใบ) แสดงได้ไหม + เหตุที่ใช้ไม่ได้</summary>
    bool CanLinkFirst,
    string? LinkFirstBlockedReason,
    string? LinkIntentCertNumber,
    IReadOnlyList<WhtLinkPaymentOption> Payments);

public record WhtLinkPaymentOption(Guid PaymentId, string PaymentNumber, DateTime PaymentDate, decimal WithholdingTaxAmount, string? CurrentCertNumber);

/// <summary>ใบสำคัญจ่ายที่น่าจะเป็นของใบ 50 ทวิ นี้ (โมดัลกลับทิศจากหน้า 50 ทวิ)</summary>
public record WhtCertPvCandidateDto(
    Guid DocumentId,
    string DocumentNumber,
    string DocumentTypeLabel,
    DateTime DocumentDate,
    decimal WithholdingTaxAmount,
    string? CurrentCertNumber,
    string Verdict,
    string ResultLabel,
    IReadOnlyList<WhtLinkIssueDto> Blocks,
    IReadOnlyList<WhtLinkIssueDto> Warnings);

public record WhtCertNoSourceRequest(string? Reason, bool Clear = false);

/// <summary>การ์ดสรุปของหน้า 50 ทวิ — นับทั้งชุดตามตัวกรองฝั่งเซิร์ฟเวอร์ (ไม่ใช่เฉพาะหน้าที่โหลด · F2)</summary>
public record WhtCertListSummary(int Total, int Draft, int Issued, int Voided, int Unlinked, int DuplicateSuspect, decimal TotalTax);

/// <summary>ค่าตัวกรองของรายการ 50 ทวิ (ฝั่งเซิร์ฟเวอร์)</summary>
public record WhtCertListFilter(TaxType? TaxFormType, int? Year, int? Month, WithholdingTaxCertStatus? Status, string? Link, string? Q,
    Guid? DocumentId = null);

/// <summary>ชื่อสถานะ/ป้ายของการผูก — ตัวเดียวสำหรับรายการ (server computes)</summary>
public static class WhtCertLinkLabels
{
    public static string Status(WithholdingTaxCertStatus s) => s switch
    {
        WithholdingTaxCertStatus.Draft => "ฉบับร่าง",
        WithholdingTaxCertStatus.Issued => "ออกแล้ว",
        WithholdingTaxCertStatus.Printed => "พิมพ์แล้ว",
        WithholdingTaxCertStatus.Voided => "ยกเลิก",
        _ => s.ToString(),
    };

    public static string CertType(WithholdingTaxCertType t) => WhtGrossUp.Normalize(t) switch
    {
        WithholdingTaxCertType.PayAlways => "(2) ออกให้ตลอดไป",
        WithholdingTaxCertType.PayOnce => "(3) ออกให้ครั้งเดียว",
        _ => "(1) หัก ณ ที่จ่าย",
    };

    public static string Result(WhtCertLinkDecision d) => d.Verdict switch
    {
        WhtCertLinkVerdict.Ok => "✅ ตรงทุกข้อ",
        WhtCertLinkVerdict.OkWithWarnings => "⚠️ " + string.Join(" · ", d.Warnings.Select(w => w.Message)),
        _ => "⛔ " + string.Join(" · ", d.Blocks.Select(b => b.Message)),
    };
}

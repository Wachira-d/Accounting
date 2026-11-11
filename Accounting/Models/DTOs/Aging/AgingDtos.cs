using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Aging;

public record AgingReportResponse(
    AgingReportType ReportType,
    DateTime AsOfDate,
    List<AgingBucket> Buckets,
    List<AgingContactDetail> Details,
    AgingTotals Totals,
    /// <summary>รอบ 258: เงินรอจัดสรรของลูกค้ารวม (รับแล้วยังไม่ผูกกับใบ · ยอด ณ ปัจจุบัน) — <b>ไม่ได้หักออกจากยอดค้าง</b>ในรายงาน (ยอดค้างรายใบยังจริงจนกว่าจะจัดสรร) ·
    /// เฉพาะรายงานลูกหนี้ที่ไม่กรองโครงการ · 0 = ไม่มี</summary>
    decimal UnappliedCreditTotal = 0m,
    /// <summary>รอบ 258: ข้อความอธิบายบรรทัดเงินรอจัดสรร (เซิร์ฟเวอร์เขียน — หน้าเว็บ/ไฟล์ส่งออกแสดงตาม) · null = ไม่มีเงินรอจัดสรร</summary>
    string? UnappliedCreditNote = null);

public record AgingBucket(
    string Label,
    int FromDays,
    int? ToDays,
    decimal TotalAmount,
    int DocumentCount);

public record AgingContactDetail(
    Guid ContactId,
    string ContactName,
    string? TaxId,
    decimal Current,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90Days,
    decimal TotalBalance,
    List<AgingDocumentDetail> Documents,
    /// <summary>รอบ 258: เงินรอจัดสรรของผู้ติดต่อรายนี้ (แสดงแยกบรรทัด — ไม่หักจาก <c>TotalBalance</c>)</summary>
    decimal UnappliedCredit = 0m);

public record AgingDocumentDetail(
    Guid DocumentId,
    string DocumentNumber,
    DocumentType DocumentType,
    DateTime DocumentDate,
    DateTime? DueDate,
    decimal TotalAmount,
    decimal BalanceDue,
    int AgingDays,
    string AgingBucket);

public record AgingTotals(
    decimal Current,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90Days,
    decimal GrandTotal);

public record AgingReportRequest(
    DateTime? AsOfDate = null,
    Guid? ContactId = null,
    int[]? CustomBuckets = null,
    /// <summary>Optional project scope. When set, the aging report only
    /// includes documents tagged with this project.</summary>
    Guid? ProjectId = null);

public enum AgingReportType
{
    AccountsReceivable = 1,
    AccountsPayable = 2
}

using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ระยะเก็บรักษาเอกสารที่ออกแล้ว — §87/3 (รายงานภาษี 5 ปี) + พ.ร.บ.การบัญชี ม.10 (5 ปีนับจากสิ้นรอบบัญชี) · ตัวตั้งตัวเดียว
/// (รอบ 200 ทีม R · B-06)
///
/// ═══ ที่มา ═══
/// เดิมด่าน "ห้ามลบถาวร" ใน <c>PurgeDocumentAsync</c> อ่านแค่ <c>Document.RetentionUntil</c> ซึ่ง<b>เขียนที่เดียว</b>คือ
/// <c>ApproveDocumentAsync</c> ⇒ ใบกำกับที่ออกจาก POS / API / นำเข้าไฟล์ / ข้ามบริษัท (สร้างเป็น Approved ตรง ๆ) มีค่าเป็น null
/// ⇒ ด่านเป็น no-op เงียบ ๆ = ลบใบกำกับที่ต้องเก็บ 5 ปีได้ทันทีโดยไม่ถามเหตุผล · ใบเดียวกันจากหน้าเอกสารถูกบล็อก
/// ⇒ ด่านตัดสินจาก "ใบนี้ออกแล้วหรือยัง" + คำนวณวันจากวันที่เอกสารเมื่อไม่มีค่าที่บันทึกไว้ (ไม่รู้ ≠ ไม่ต้องเก็บ · DOCTRINE §1)</summary>
public static class DocumentRetention
{
    public const int Years = 5;

    /// <summary>วันสิ้นสุดการเก็บ = MAX(วันสิ้นรอบบัญชีที่เอกสารอยู่, วันที่เอกสาร) + 5 ปี (สูตรเดียวกับที่ Approve บันทึก)</summary>
    public static DateTime ComputeUntil(DateTime documentDate, int fiscalYearStartMonth)
    {
        var startMonth = fiscalYearStartMonth is >= 1 and <= 12 ? fiscalYearStartMonth : 1;
        var fy = FiscalYear.FiscalYearOf(documentDate.Date, startMonth);
        var fyEnd = FiscalYear.RangeFor(fy, startMonth).EndInclusive;
        var basis = fyEnd > documentDate.Date ? fyEnd : documentDate.Date;
        return basis.AddYears(Years);
    }

    /// <summary>true = เอกสารเคยออกเลข/มีผลแล้ว (ร่าง · รออนุมัติ · ถูกปฏิเสธ · เลข <c>DRAFT-</c> = ยังไม่เคยออก)</summary>
    private static bool WasIssued(DocumentStatus status, string? documentNumber)
        => status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected)
           && !(documentNumber ?? "").StartsWith("DRAFT-", StringComparison.OrdinalIgnoreCase);

    /// <summary>วันสิ้นสุดการเก็บที่มีผล: ค่าที่บันทึกไว้ชนะ · ไม่มีค่า + ออกแล้ว ⇒ คำนวณ · ยังไม่เคยออก ⇒ null (ลบได้)</summary>
    public static DateTime? EffectiveUntil(DocumentStatus status, string? documentNumber, DateTime? storedUntil,
        DateTime documentDate, int fiscalYearStartMonth)
    {
        if (!WasIssued(status, documentNumber)) return null;
        return storedUntil ?? ComputeUntil(documentDate, fiscalYearStartMonth);
    }

    /// <summary>true = ยังอยู่ในช่วงเก็บรักษา (ห้ามลบถาวรโดยไม่มีเหตุผล override)</summary>
    public static bool InRetention(DateTime? effectiveUntil, DateTime todayUtc)
        => effectiveUntil.HasValue && todayUtc.Date < effectiveUntil.Value.Date;
}

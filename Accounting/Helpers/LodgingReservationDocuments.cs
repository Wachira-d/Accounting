using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>เอกสาร 1 ใบของการจอง (อ่านจาก <c>Document</c> ที่ <c>BookingNumber</c> = เลขจอง) — ข้อมูลดิบให้ <see cref="LodgingReservationDocuments.RoleLabel"/></summary>
public sealed record LodgingReservationDocumentRow(
    Guid Id, string Number, DocumentType Type, DocumentStatus Status, DateTime Date, decimal Total, bool IsDeposit, DepositNature? Nature);

/// <summary>
/// ป้าย "บทบาท" ของเอกสารแต่ละใบในการ์ดการเงินของการจอง (ทะเบียนงานคงค้าง 2026-10-26 R-50 — เดิมโมดัลลิงก์เฉพาะใบมัดจำใบแรก
/// <c>DepositDocumentId</c> ⇒ ใบรับเงินใบที่ 2+ จาก “รับชำระเพิ่ม/ระหว่างพัก” ไม่มีทางเปิดจากการจอง) · ตัดสินจาก<b>เอกสารที่ผูกจริง</b>
/// (ใบล่วงหน้า/ใบเช็คเอาต์/เงินประกัน) ก่อน แล้วค่อยตามชนิด · ชื่อชนิดเอกสารจาก <see cref="DocumentTypeNames.Title"/> ตัวเดียว (ไม่มีตารางป้ายที่สอง)
/// </summary>
public static class LodgingReservationDocuments
{
    public static string RoleLabel(LodgingReservationDocumentRow d, Guid? advanceTaxDocumentId, Guid? finalDocumentId, Guid? securityDepositDocumentId)
    {
        if (advanceTaxDocumentId is Guid a && d.Id == a) return "ใบกำกับภาษีล่วงหน้า";
        if (finalDocumentId is Guid f && d.Id == f) return "เอกสารเช็คเอาต์";
        if ((securityDepositDocumentId is Guid s && d.Id == s) || d.Nature == DepositNature.RefundableSecurity) return "ใบรับเงินประกันความเสียหาย";
        if (d.IsDeposit) return $"ใบรับเงินมัดจำ ({DocumentTypeNames.Title(d.Type, null)})";
        if (d.Type == DocumentType.CreditNote) return "ใบลดหนี้";
        return DocumentTypeNames.Title(d.Type, null);
    }

    /// <summary>สถานะที่<b>ไม่แสดง</b>ในการ์ดเอกสารของการจอง = ยังไม่ออกจริง (<see cref="DocumentStatusRules.NotIssued"/> · ไม่มีเลขจริง) <b>ยกเว้น</b>ถูกตีกลับ
    /// (เป็นประวัติที่พนักงานต้องเห็นว่าใบถูกตีกลับ) — รอบ 208 ฝ่ายค้าน W5 ข้อ 10 (เดิมตัดเฉพาะร่าง ⇒ ใบรออนุมัติเลข <c>DRAFT-…</c> โผล่ป้าย "มีผล")</summary>
    public static readonly DocumentStatus[] HiddenStatuses =
        DocumentStatusRules.NotIssued.Where(s => s != DocumentStatus.Rejected).ToArray();

    /// <summary>ป้ายสถานะไทย — ใบที่ยกเลิก/ตีกลับต้องเห็นชัด (ไม่ซ่อนออกจากรายการ — เป็นประวัติของการจอง)</summary>
    public static string StatusLabel(DocumentStatus s) => s switch
    {
        DocumentStatus.Voided => "ยกเลิกแล้ว",
        DocumentStatus.Rejected => "ถูกตีกลับ",
        DocumentStatus.Draft => "ร่าง",
        DocumentStatus.Paid => "ชำระแล้ว",
        _ => "มีผล",
    };
}

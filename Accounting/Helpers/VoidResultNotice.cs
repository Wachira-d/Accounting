using Accounting.Models.DTOs.Document;

namespace Accounting.Helpers;

/// <summary>
/// ข้อความเตือนจากผลยกเลิกเอกสาร (<see cref="PaymentVoidResult"/> — ธงต้องยกเลิกทาง e-Tax · ภาษีขายที่ถอย) สำหรับทางเข้าที่ยกเลิกเอกสาร ERP
/// <b>แทนผู้ใช้</b> (ยกเลิกออเดอร์ร้านค้า · ยกเลิกการจอง CMS) — รอบ 201 ทีม PL (คำสั่ง main agent หลังทีม DV เปลี่ยน <c>VoidDocumentAsync</c> ให้คืนผล)
///
/// <para>เดิมสองทางเข้านี้เรียก <c>VoidDocumentAsync</c> แล้วทิ้งผล ⇒ ใบที่ e-Tax ถึงกรมสรรพากรแล้วถูกยกเลิกในระบบโดยไม่มีใครเห็นว่าต้องไปยกเลิกทาง e-Tax ต่อ
/// (F2 ข้อ 7 — ล้มดัง/เตือนดังที่ข้อมูลที่ผู้ใช้เปิดดู) · ตัวประกอบข้อความตัวเดียว: ว่าง = ไม่มีอะไรต้องเตือน</para>
/// </summary>
public static class VoidResultNotice
{
    /// <summary>บรรทัดเตือน (ขึ้นต้นด้วยเลขเอกสารเมื่อมี) · ผลว่าง/ไม่มีธง ⇒ รายการว่าง</summary>
    public static IReadOnlyList<string> Lines(PaymentVoidResult? result, string? documentNumber)
    {
        if (result == null) return Array.Empty<string>();
        var prefix = string.IsNullOrWhiteSpace(documentNumber) ? "" : $"เอกสาร {documentNumber.Trim()}: ";
        return new[] { result.EtaxCancellationFlag, result.OutputVatNotice }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => prefix + s!.Trim())
            .ToList();
    }
}

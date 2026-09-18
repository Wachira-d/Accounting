using Accounting.Models.DTOs.Tax;

namespace Accounting.Helpers;

/// <summary>
/// สถานะของหนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) ที่ "นับเข้าแบบยื่น/ยอดนำส่ง" — **ที่เดียวของทั้งระบบ**
///
/// <para><b>ที่มา (รอบ 170 — REGRESSION_ROOT_CAUSE_2026-09-18 §4 #2)</b>: กติกา "Issued หรือ Printed"
/// ถูกพิมพ์ซ้ำ 5 ที่ (TaxService · TaxFilingExportService ×3 · DashboardService เขียนกลับด้านเป็น
/// "ไม่ใช่ Voided/Draft") ขณะที่หน้านำส่งไม่ได้อ่าน certs เลย ⇒ JE นำส่ง ≠ ไฟล์ที่ยื่น.
/// เมื่อทุกจอ/ไฟล์/JE เรียกตัวนี้ตัวเดียว การเพิ่มสถานะใหม่ (เช่น "ส่งให้ผู้ถูกหักแล้ว") จะไม่ทำให้
/// สองหน้าเล่าคนละเรื่องอีก</para>
///
/// <para>เก็บเป็น array เพื่อให้ EF แปล <c>Filed.Contains(c.Status)</c> เป็น <c>IN (...)</c> ได้ตรง ๆ</para>
/// </summary>
public static class WhtCertFilingScope
{
    /// <summary>ออกให้ผู้ถูกหักแล้ว ⇒ เข้า ภ.ง.ด.3/53/54 · ไฟล์ยื่น · ยอดนำส่ง · การ์ดแดชบอร์ด.
    /// Draft = ยังไม่ส่งมอบ (ไม่นับ) · Voided = ยกเลิก (ไม่นับ)</summary>
    public static readonly WithholdingTaxCertStatus[] Filed =
    {
        WithholdingTaxCertStatus.Issued,
        WithholdingTaxCertStatus.Printed,
    };
}

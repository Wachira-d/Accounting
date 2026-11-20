using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก E (T4 H-3 · L-8 · คำตัดสิน 4.2 · ฝ่ายค้าน LOW (ข)(ฉ)) — ข้อความประกอบบน 50 ทวิ ภ.ง.ด.1 ของ<b>ผู้ไม่มีเลข 13 หลักที่รับทราบแล้ว</b> (เลขพาสปอร์ต) ·
/// อ่านจาก<b>สำเนาผู้ถูกหัก ณ วันออก</b> (<see cref="WhtPayeeSnapshot.WaiverOf"/>) เท่านั้น — ใบเก่าไม่เปลี่ยนตามทะเบียนปัจจุบัน ·
/// ตัวเดียวของ PDF (HTML + QuestPDF · เอกสารทางการ = เลขเต็ม) และหน้าพิมพ์ <c>wht.html</c> (ผ่าน DTO — ปิดบังเว้นแต่ผู้ขอมีสิทธิ์ <c>pii:view</c>) ·
/// ข้อความจาก <see cref="EmployeeTaxIdentity.WaiverCertNote"/> ตัวเดียว
/// </summary>
public static class WhtCertPayeeNote
{
    /// <param name="includePii">true = เลขพาสปอร์ตเต็ม (PDF ทางการ / ผู้มี pii:view) · false = ปิดบัง (<see cref="PiiMask.Passport"/>)</param>
    public static string? Of(WithholdingTaxCert cert, bool includePii)
    {
        if (cert.TaxFormType != TaxType.WithholdingTax1) return null;
        if (EmployeeTaxIdentity.InspectValue(WhtPayeeSnapshot.Of(cert)?.TaxId).Problem != TaxIdProblem.Missing) return null;
        var (ack, passport) = WhtPayeeSnapshot.WaiverOf(cert);
        if (!ack) return null;
        return EmployeeTaxIdentity.WaiverCertNote(true, includePii ? passport : PiiMask.Passport(passport));
    }
}

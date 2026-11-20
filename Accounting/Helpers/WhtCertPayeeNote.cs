using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก E (T4 H-3 · L-8 · คำตัดสิน 4.2) — ข้อความประกอบบน 50 ทวิ ภ.ง.ด.1 ของ<b>ผู้ไม่มีเลข 13 หลักที่ผู้ทำบัญชีรับทราบแล้ว</b> (เลขพาสปอร์ต) ·
/// ตัวโหลดเดียวของ PDF (HTML + QuestPDF) และหน้าพิมพ์ <c>wht.html</c> (ผ่าน DTO <c>PayeeReferenceNote</c>) · ข้อความจาก
/// <see cref="EmployeeTaxIdentity.WaiverCertNote"/> ตัวเดียว
///
/// <para>เงื่อนไข: ใบแบบ ภ.ง.ด.1 · เลขผู้ถูกหักบนใบ (สำเนา ณ วันออก) ว่าง · ผู้ติดต่อของใบคือผู้ติดต่อที่ผูกกับพนักงาน (<c>Employee.ContactId</c>) ที่รับทราบแล้ว ·
/// พาสปอร์ตอ่านผ่าน converter (เข้ารหัสในฐาน — ไม่คัดลอกลงใบเป็นข้อความเปิด) · ทุก query กรอง <c>CompanyId</c></para>
/// </summary>
public static class WhtCertPayeeNote
{
    public static async Task<string?> LoadAsync(AccountingDbContext db, Guid companyId, WithholdingTaxCert cert, CancellationToken ct = default)
    {
        if (cert.TaxFormType != TaxType.WithholdingTax1) return null;
        if (EmployeeTaxIdentity.InspectValue(WhtPayeeSnapshot.Of(cert)?.TaxId).Problem != TaxIdProblem.Missing) return null;
        var emp = await db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.ContactId == cert.PayeeContactId && e.TaxIdWaiverAcknowledgedAt != null)
            .Select(e => new { e.PassportNumber })
            .FirstOrDefaultAsync(ct);
        // ใบออกไปแล้วโดยไม่มีเลข (สำเนา ณ วันออก) — แสดงข้อความประกอบตามการรับทราบ แม้ภายหลังพนักงานได้เลขแล้ว (ใบเดิมไม่เปลี่ยนเลขเอง)
        return emp == null ? null : EmployeeTaxIdentity.WaiverCertNote(true, emp.PassportNumber);
    }
}

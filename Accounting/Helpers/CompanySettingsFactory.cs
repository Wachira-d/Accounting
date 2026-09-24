using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวสร้างแถว <see cref="CompanySettings"/> ตัวเดียวของระบบ** (รอบ 193 · ผลตรวจ S-01)
///
/// <para>═══ ที่มา ═══ แถวค่าตั้งถูกสร้างแบบ lazy จาก 6 ที่ (SettingsService · DocumentEmailService ·
/// EmailConfig/OwnerConfig/LineConfig/Etax controller) ด้วย <c>new CompanySettings { CompanyId = … }</c>
/// ⇒ ได้ <c>VatRegistered = true</c> / <c>DefaultVatRate = 7</c> จากค่า default ของ entity เสมอ ไม่ว่าบริษัท
/// จะจด VAT หรือไม่. สถานการณ์จริง: สร้างบริษัทโดยไม่ติ๊ก "จด VAT" → วิซาร์ดพาไปหน้าตั้งค่า → หน้าโหลดแถวใหม่
/// ที่ติ๊ก "จด VAT" ไว้ → กดบันทึก (แก้อย่างอื่นก็ตาม) → <c>SettingsService</c> sync กลับทับ
/// <c>Company.IsVatRegistered = true</c> เงียบ ๆ ⇒ บริษัทไม่จด VAT ออกใบกำกับเก็บ VAT ได้ (§90/2)</para>
///
/// <para>กติกา: ทุกจุดที่สร้างแถวค่าตั้งเรียกตัวนี้ — seed สถานะ/อัตรา VAT จาก <c>Company</c> ·
/// <b>ไม่</b>ได้เลือกว่าธงไหนเป็นต้นทาง (คำถามเจ้าของ Q1 ยังเปิด) แค่ทำให้แถวใหม่ "เริ่มตรงกัน"
/// · <c>tools/company_settings_factory_check.py</c> ฟ้อง <c>new CompanySettings</c> นอกไฟล์นี้</para>
/// </summary>
public static class CompanySettingsFactory
{
    /// <summary>แถวค่าตั้งใหม่ของบริษัทนี้ (ยังไม่ Add เข้า DbContext) — pure</summary>
    /// <param name="vatStatusConfirmed">ผู้ใช้<b>ตอบ</b>เรื่องจด VAT มาแล้วในเส้นที่สร้างบริษัท (วิซาร์ด = true) ·
    /// หน้าสมัคร/SSO ไม่ถาม = false ⇒ <c>VatStatusConfirmedAt = null</c> ⇒ หน้าเอกสารขึ้นแถบให้ไปตั้งค่า (ฝ่ายค้าน C-9) ·
    /// บังคับส่งทุกครั้ง — ห้ามมีค่าเริ่มต้นที่ทำให้ "ไม่รู้" กลายเป็น "ยืนยันแล้ว" เงียบ ๆ</param>
    public static CompanySettings NewFor(Company company, bool vatStatusConfirmed)
        => NewFor(company.Id, company.IsVatRegistered, company.VatRate,
            vatStatusConfirmed ? DateTime.UtcNow : null);

    /// <summary>แกนกลางของ <see cref="NewFor(Company, bool)"/> — ค่าอื่นทุกช่องใช้ค่า default ของ entity ตามเดิม</summary>
    internal static CompanySettings NewFor(Guid companyId, bool companyIsVatRegistered, decimal companyVatRate,
        DateTime? vatStatusConfirmedAt)
        => new()
        {
            CompanyId = companyId,
            VatRegistered = companyIsVatRegistered,
            DefaultVatRate = companyVatRate,
            VatStatusConfirmedAt = vatStatusConfirmedAt,
        };

    /// <summary>lazy-create: อ่านธง VAT ของบริษัทแล้ว <c>Add</c> แถวใหม่เข้า <paramref name="db"/>
    /// (<b>ไม่</b> SaveChanges — ผู้เรียกบันทึกตามจังหวะเดิมของตัวเอง) · ไม่พบบริษัท = โยน
    /// <see cref="KeyNotFoundException"/> (แถวค่าตั้งที่ไม่มีบริษัทเจ้าของ = FK ล้มอยู่ดี — ล้มดังตั้งแต่ตรงนี้)</summary>
    public static async Task<CompanySettings> AddNewAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var co = await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.IsVatRegistered, c.VatRate })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท — สร้างค่าตั้งบริษัทไม่ได้");
        // lazy (บริษัทเก่าที่ยังไม่มีแถว) — ไม่มีใครตอบเรื่อง VAT ในเส้นนี้ = ยังไม่ยืนยัน
        var s = NewFor(companyId, co.IsVatRegistered, co.VatRate, vatStatusConfirmedAt: null);
        db.CompanySettings.Add(s);
        return s;
    }
}

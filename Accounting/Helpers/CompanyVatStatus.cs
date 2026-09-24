using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ความสัมพันธ์ของธง "จด VAT" สองตัวของบริษัทหนึ่ง ณ ตอนนี้ (รายงานอ่านอย่างเดียว — S-01)</summary>
public enum VatFlagAgreement
{
    /// <summary>สองธงตรงกัน</summary>
    Agree = 0,
    /// <summary>ยังไม่มีแถว <c>CompanySettings</c> — ทุกผู้อ่านใช้ธงบริษัทอยู่แล้ว (ไม่ขัด)</summary>
    NoSettingsRow = 1,
    /// <summary>ทะเบียนบริษัทบอก "ไม่จด" แต่ค่าตั้งบอก "จด" — ทิศที่ผิดกฎหมาย: เส้นเอกสาร/Integration
    /// ยอมออกใบกำกับเก็บ VAT (§90/2) ขณะที่ POS/CMS/ปฏิทินยื่นภาษีเห็นว่าไม่จด (ไม่เตือนให้ยื่น ภ.พ.30)</summary>
    CompanyNoSettingsYes = 2,
    /// <summary>ทะเบียนบริษัทบอก "จด" แต่ค่าตั้งบอก "ไม่จด" — เส้นเอกสารบล็อกใบกำกับ/ไม่เคลมภาษีซื้อ
    /// ขณะที่ POS/CMS/ที่พักคิด VAT</summary>
    CompanyYesSettingsNo = 3,
}

/// <summary>
/// **สถานะจด VAT ของบริษัท — ตัวอ่านตัวเดียวของผู้อ่านฝั่งค่าตั้ง** (รอบ 193 · ผลตรวจ S-01)
///
/// <para>═══ ที่มา ═══ ระบบเก็บสถานะ VAT สองที่ที่ค่าเริ่มต้น<b>ตรงข้ามกัน</b>:
/// <c>Company.IsVatRegistered</c> (default false) กับ <c>CompanySettings.VatRegistered</c> (default true).
/// ผู้อ่านฝั่งค่าตั้งทุกตัวเคยเขียน <c>?? true</c> เอง (DocumentService 5 จุด · IntegrationService 1 จุด) ⇒
/// บริษัทที่สร้างโดย<b>ไม่ติ๊ก</b> "จด VAT" แต่ยังไม่มีแถวค่าตั้ง ถูกถือว่า "จด" ⇒ ออกใบกำกับเก็บ VAT ได้ (§90/2)
/// ส่วนแถวค่าตั้งที่สร้างทีหลังแบบ lazy ก็ได้ <c>true</c> จากค่า default ของ entity (แก้ที่
/// <see cref="CompanySettingsFactory"/>)</para>
///
/// <para>═══ กติกาตอนนี้ (stopgap) ═══ มีแถวค่าตั้ง → ใช้ธงค่าตั้ง (พฤติกรรมเดิมทุกประการ) ·
/// ไม่มีแถว → ใช้ธงบริษัท (เดิม = true เสมอ). <b>ยังไม่ได้เลือกว่าธงไหนเป็นต้นทาง</b> —
/// รอคำตัดสินเจ้าของ Q1 (<c>erp-review/2026-09-24/audit-settings.md</c> §6) · ห้ามสลับลำดับที่นี่โดยไม่มีคำตัดสิน
/// เพราะบริษัทที่สองธงขัดกันวันนี้จะเปลี่ยนพฤติกรรมภาษีทันที (ดูรายงาน <c>VatFlagConsistencyController</c>)</para>
/// </summary>
public static class CompanyVatStatus
{
    /// <summary>จด VAT ไหม — ค่าตั้งก่อน (ถ้ามีแถว) ไม่งั้นทะเบียนบริษัท
    /// <para>TODO(Q1): เมื่อเจ้าของตัดสินต้นทาง ให้แก้ที่นี่ที่เดียว</para></summary>
    /// <param name="companyIsVatRegistered"><c>Company.IsVatRegistered</c></param>
    /// <param name="settingsVatRegistered"><c>CompanySettings.VatRegistered</c> · <c>null</c> = ยังไม่มีแถว</param>
    public static bool IsRegistered(bool companyIsVatRegistered, bool? settingsVatRegistered)
        => settingsVatRegistered ?? companyIsVatRegistered;

    /// <summary>อัตรา VAT ตั้งต้นของบริษัท (ยังไม่ผ่านด่าน "จดไหม" — ผู้เรียกที่คิดภาษีขายต้องผ่าน
    /// <see cref="OutputVatRate.ForCompany"/> ต่อ) · ลำดับเดียวกับ <see cref="IsRegistered(bool, bool?)"/></summary>
    public static decimal DefaultRate(decimal companyVatRate, decimal? settingsDefaultVatRate)
        => settingsDefaultVatRate ?? companyVatRate;

    /// <summary>สองธงสัมพันธ์กันอย่างไร — ใช้ในรายงานอ่านอย่างเดียว (ห้ามใช้ตัดสินภาษี)</summary>
    public static VatFlagAgreement Compare(bool companyIsVatRegistered, bool? settingsVatRegistered)
    {
        if (settingsVatRegistered is not bool s) return VatFlagAgreement.NoSettingsRow;
        if (s == companyIsVatRegistered) return VatFlagAgreement.Agree;
        return s ? VatFlagAgreement.CompanyNoSettingsYes : VatFlagAgreement.CompanyYesSettingsNo;
    }

    /// <summary>ห่อ query ของ <see cref="IsRegistered(bool, bool?)"/> — ทุก query กรอง CompanyId ·
    /// ไม่พบบริษัท = ไม่จด (ค่า default ของ entity — ทิศที่บล็อกการเก็บ VAT ไม่ใช่ทิศที่เงียบ)</summary>
    public static async Task<bool> IsRegisteredAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var p = await ProfileAsync(db, companyId, ct);
        return p.Registered;
    }

    /// <summary>สถานะ + อัตราตั้งต้น (ก่อนผ่าน <see cref="OutputVatRate.ForCompany"/>) ในสอง query</summary>
    public static async Task<(bool Registered, decimal DefaultRate)> ProfileAsync(
        AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var s = await db.CompanySettings.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => new { c.VatRegistered, c.DefaultVatRate })
            .FirstOrDefaultAsync(ct);
        var co = await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.IsVatRegistered, c.VatRate })
            .FirstOrDefaultAsync(ct);
        return (IsRegistered(co?.IsVatRegistered ?? false, s?.VatRegistered),
                DefaultRate(co?.VatRate ?? PartnerVatRate.StatutoryRate, s?.DefaultVatRate));
    }
}

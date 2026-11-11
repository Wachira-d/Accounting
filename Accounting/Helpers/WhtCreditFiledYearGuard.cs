using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 257 ชุด 2 ฝ่ายค้าน H1 (คำตัดสิน 257-2-5) — <b>ตัวตัดสินเดียวของ "เครดิตของปีที่ยื่น ภ.ง.ด.50 แล้ว ห้ามขยับ"</b>
///
/// <para>เดิมด่านกันแก้เครดิตที่ยื่นแล้วอิงสถานะ Claimed ต่อแถว — แต่ Claimed ประทับเฉพาะแถวได้ใบครบ ⇒ แถวได้ใบบางส่วนของปีที่ยื่นแล้ว
/// ถอนใบ/ตัดสูญ/แก้ได้ทั้งที่ยอดเครดิตของแถวนั้นอยู่ในแบบที่ยื่นไปแล้ว · ตอนนี้ล็อกตาม<b>ปีภาษี</b>: รอบบัญชีของวันที่บนหนังสือรับรองของแถว
/// (<c>CertificateDate</c> — ตัวเดียวกับ <see cref="CitCalculation.CreditableInPeriod"/>) มีรายงาน ภ.ง.ด.50 ที่ยื่น/บันทึกว่ายื่นแล้ว
/// (<see cref="TaxFilingLockPolicy.DeclaredOrFiledStatuses"/>) ⇒ 409 · Claimed เหลือไว้แสดงผลเท่านั้น</para>
///
/// <para>ผู้เรียก (ทุกทางที่เปลี่ยนเครดิตของแถวที่มีใบแล้ว): ถอนการยืนยันใบ · กลับรายการตัดสูญตอนยืนยันใบ · ตัดสูญ (รายแถว + ปิดปี) ·
/// แก้เลขที่/วันที่ใบ + ลบแถว · "ได้รับใบแล้ว" — ทุกทางถือล็อก <see cref="CompanyLockKey"/> ก่อนตรวจ (ตัวเดียวกับการยื่น/ปลดล็อก ภ.ง.ด.50 ⇒ ไม่ชนกันกลางทาง)</para>
/// </summary>
public static class WhtCreditFiledYearGuard
{
    public const string RuleCode = "RD-50BIS-CREDIT-FILED-YEAR";

    /// <summary>ส่วนของคีย์ล็อกระดับบริษัทของเครดิตภาษีถูกหัก (คู่กับ <see cref="AdvisoryLockKey.WhtCreditWriteOff"/>)</summary>
    public const string CompanyLockPart = "company-credits";

    /// <summary>คีย์ล็อกระดับบริษัท — ยื่น/ปลดล็อก ภ.ง.ด.50 (ประทับ/ถอน Claimed) · ถอนใบ · ยืนยันใบ · ตัดสูญ · แก้/ลบ/ได้รับใบแล้ว ถือคีย์เดียวกัน</summary>
    public static long CompanyLockKey(Guid companyId) => AdvisoryLockKey.For(companyId, AdvisoryLockKey.WhtCreditWriteOff, CompanyLockPart);

    /// <summary>ถือล็อกระดับบริษัทในธุรกรรมปัจจุบัน (xact lock — ผู้เรียกอยู่ในธุรกรรม) · ต้องถือ<b>ก่อน</b>ล็อกใบ/ล็อกแถว (ลำดับเดียวทุกทาง — ไม่ deadlock)</summary>
    public static Task LockCompanyAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { CompanyLockKey(companyId) }, ct);

    /// <summary>pure — วันที่บนใบอยู่ในรอบบัญชีที่ยื่นแล้วไหม · ไม่มีวันที่ (ยังไม่มีใบ ⇒ ยังไม่มีเครดิตในแบบใด) = ไม่ล็อก</summary>
    public static int? LockedYear(DateTime? certificateDate, int fiscalStartMonth, IReadOnlyCollection<int> filedYears)
        => certificateDate is DateTime d && filedYears.Contains(WhtCertIntakeVerdict.FiscalYearOf(d, fiscalStartMonth))
            ? WhtCertIntakeVerdict.FiscalYearOf(d, fiscalStartMonth) : null;

    /// <summary>ข้อความ 409 — บอกทางไปต่อสองทาง (ปลดล็อกแบบที่ยื่นแล้ว · บันทึกในปีถัดไป)</summary>
    public static string Message(int fiscalYear, string action)
        => $"{action}ไม่ได้ — เครดิตของรายการนี้อยู่ในปีภาษี {fiscalYear + 543} ซึ่งยื่น ภ.ง.ด.50 ไปแล้ว (ยอดในแบบที่ยื่นจะไม่ตรงกับทะเบียน) · "
           + "ถ้าต้องแก้จริง ให้ \"ปลดล็อก/กลับเป็นร่าง\" ภ.ง.ด.50 ปีนั้นก่อน (แล้วยื่นเพิ่มเติมตามยอดใหม่) หรือบันทึกการเปลี่ยนแปลงเป็นรายการของปีถัดไป";

    /// <summary>ปีภาษีที่มีรายงาน ภ.ง.ด.50 ยื่น/บันทึกว่ายื่นแล้วของบริษัท (tenant)</summary>
    public static async Task<List<int>> FiledYearsAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        return await db.TaxReports.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.TaxType == TaxType.CorporateIncomeTax
                && TaxFilingLockPolicy.DeclaredOrFiledStatuses.Contains(t.Status))
            .Select(t => t.Year).Distinct().ToListAsync(ct);
    }

    /// <summary>ตรวจวันที่ใบหลายค่า (ค่าเดิม/ค่าใหม่ของแถว) — ตัวใดอยู่ในปีที่ยื่นแล้ว ⇒ 409 พร้อมข้อความ <see cref="Message"/></summary>
    public static async Task EnsureNotFiledAsync(AccountingDbContext db, Guid companyId, string action, CancellationToken ct, params DateTime?[] certificateDates)
    {
        if (certificateDates.All(d => d is null)) return;
        var start = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.FiscalYearStartMonth).FirstOrDefaultAsync(ct);
        var filed = await FiledYearsAsync(db, companyId, ct);
        if (filed.Count == 0) return;
        foreach (var d in certificateDates)
            if (LockedYear(d, start, filed) is int fy)
                throw new BusinessRuleException(Message(fy, action), RuleCode, 409);
    }
}

using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 257 ชุด 2 (B-M1 · คำตัดสิน 257-2-1) — <b>ตัวเดียวที่ประทับ/ถอนสถานะ Claimed</b> ของทะเบียนภาษีถูกหัก (<see cref="WhtCreditReceived"/>)
///
/// <para>เดิมไม่มีโค้ดใดตั้ง Claimed (DECISIONS 252-WP09-8 อ้างว่า "flow ยื่นแบบประทับ") ⇒ ด่านกันแก้/ตัดสูญ/ถอนใบของเครดิตที่ยื่นแล้วไม่เคยทำงาน ·
/// คอลัมน์ "ใช้เครดิตแล้ว" = 0 เสมอ · ตอนนี้: ผู้ใช้กด "ยื่นภาษี" ของรายงาน ภ.ง.ด.50 (<c>TaxService.FileTaxReportAsync</c> — Submitted หรือ Filed)
/// ⇒ แถว Received ที่เครดิตเข้ารอบนั้น (<see cref="CitCalculation.CreditableInPeriod"/> — ชุดเดียวกับที่รายงานรวม) ถูกประทับ Claimed +
/// <c>ClaimedInTaxReportId</c> · "ปลดล็อก/กลับเป็นร่าง" หรือ Reject &amp; Reverse ⇒ ถอนเฉพาะแถวที่รายงานนั้นประทับ (กลับเป็น Received)</para>
///
/// <para><b>ไม่ประทับ</b>แถว Pending/Expired ที่ได้ใบบางส่วน — ส่วนที่ขาดใบยังต้องตาม/ตัดสูญได้ (Claimed ทั้งแถวจะซ่อนยอดที่ขาด · <see cref="WhtCreditableAmount.Missing"/> = 0)
/// · ภ.ง.ด.51 ไม่มีรายงานในระบบ (คำนวณที่เครื่องคำนวณ/ส่งออก) ⇒ ไม่ประทับ · ผู้เรียก SaveChanges เอง (ธุรกรรมเดียวกับการยื่น)</para>
/// <para>ฝ่ายค้าน H1 (คำตัดสิน 257-2-5): Claimed <b>ใช้แสดงผลเท่านั้น</b> — ด่านกันเปลี่ยนเครดิตของปีที่ยื่นแล้วคือ <see cref="WhtCreditFiledYearGuard"/> (ตามปีภาษี ไม่ใช่ตามแถว)</para>
/// </summary>
public static class WhtCreditClaimStamp
{
    public const string RuleCode = "RD-50BIS-CREDIT-CLAIMED";

    /// <summary>ฝ่ายค้าน M2: ยอดเครดิตในรายงาน ภ.ง.ด.50 ที่สร้างไว้ ไม่ตรงทะเบียนตอนยื่น (เกิน ฿0.01) ⇒ ต้องสร้างรายงานใหม่ก่อนยื่น</summary>
    public static bool ReportOutOfDate(decimal creditInReport, decimal creditNow)
        => Math.Abs(Math.Round(creditInReport, 2, MidpointRounding.AwayFromZero) - Math.Round(creditNow, 2, MidpointRounding.AwayFromZero)) > 0.01m;

    /// <summary>สถานะหลังถอนการประทับ — ทุกแถวที่ประทับมาจาก Received (เฉพาะได้ใบครบ — ตัวกรองใน <see cref="StampAsync"/>)</summary>
    public const WhtCreditStatus ReleasedStatus = WhtCreditStatus.Received;

    /// <summary>ประทับ Claimed ให้แถว Received ที่เครดิตเข้ารอบ [<paramref name="start"/>, <paramref name="endInclusive"/>] ของรายงาน <paramref name="reportId"/> · คืนจำนวนแถว</summary>
    public static async Task<int> StampAsync(AccountingDbContext db, Guid companyId, Guid reportId, DateTime start, DateTime endInclusive,
        DateTime nowUtc, CancellationToken ct = default)
    {
        // ฝ่ายค้าน M1: ล็อกระดับบริษัท (ตัวเดียวกับถอนใบ/ตัดสูญ/ยืนยันใบ) แล้วอ่านแถวใหม่ใต้ล็อก
        await WhtCreditFiledYearGuard.LockCompanyAsync(db, companyId, ct);
        var rows = await ReloadAsync(db, await db.WhtCreditsReceived
            .CreditableInPeriod(companyId, start, endInclusive)
            .Where(w => w.CompanyId == companyId && w.Status == WhtCreditStatus.Received)
            .ToListAsync(ct), ct);
        // ฝ่ายค้าน L1 (ทางปลอดภัย): ประทับเฉพาะแถวที่ไม่เหลือส่วนขาดใบเลย — Received ที่ใบต่ำกว่า ≤ ฿1 คงไว้ (เศษยังตัดสูญได้ · Claimed จะซ่อนเศษ)
        rows = rows.Where(w => w.Status == WhtCreditStatus.Received
            && WhtCreditableAmount.Missing(w.Status, w.WhtAmount, w.ReceivedAmount) <= 0.005m).ToList();
        foreach (var w in rows)
        {
            w.Status = WhtCreditStatus.Claimed;
            w.ClaimedInTaxReportId = reportId;
            w.ClaimedAt = nowUtc;
            w.UpdatedAt = nowUtc;
        }
        return rows.Count;
    }

    /// <summary>ถอนการประทับของรายงาน <paramref name="reportId"/> (กลับเป็นร่าง / ถูกปฏิเสธ) — เฉพาะแถวที่รายงานนั้นประทับ · คืนจำนวนแถว</summary>
    public static async Task<int> ReleaseAsync(AccountingDbContext db, Guid companyId, Guid reportId, DateTime nowUtc, CancellationToken ct = default)
    {
        await WhtCreditFiledYearGuard.LockCompanyAsync(db, companyId, ct);   // ฝ่ายค้าน M1
        var rows = (await ReloadAsync(db, await db.WhtCreditsReceived
            .Where(w => w.CompanyId == companyId && w.Status == WhtCreditStatus.Claimed && w.ClaimedInTaxReportId == reportId)
            .ToListAsync(ct), ct))
            .Where(w => w.Status == WhtCreditStatus.Claimed && w.ClaimedInTaxReportId == reportId).ToList();
        foreach (var w in rows)
        {
            w.Status = ReleasedStatus;
            w.ClaimedInTaxReportId = null;
            w.ClaimedAt = null;
            w.UpdatedAt = nowUtc;
        }
        return rows.Count;
    }

    /// <summary>แถวที่ context ติดตามอยู่แล้วต้องอ่านค่าล่าสุดใต้ล็อก (อีกคำขอเพิ่งถอนใบ/ตัดสูญ)</summary>
    private static async Task<List<WhtCreditReceived>> ReloadAsync(AccountingDbContext db, List<WhtCreditReceived> rows, CancellationToken ct)
    {
        foreach (var w in rows)
            if (db.Entry(w).State == EntityState.Unchanged) await db.Entry(w).ReloadAsync(ct);
        return rows;
    }
}

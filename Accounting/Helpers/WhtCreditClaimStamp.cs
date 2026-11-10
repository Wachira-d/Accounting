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
/// </summary>
public static class WhtCreditClaimStamp
{
    public const string RuleCode = "RD-50BIS-CREDIT-CLAIMED";

    /// <summary>สถานะหลังถอนการประทับ — ทุกแถวที่ประทับมาจาก Received (เฉพาะได้ใบครบ — ตัวกรองใน <see cref="StampAsync"/>)</summary>
    public const WhtCreditStatus ReleasedStatus = WhtCreditStatus.Received;

    /// <summary>ประทับ Claimed ให้แถว Received ที่เครดิตเข้ารอบ [<paramref name="start"/>, <paramref name="endInclusive"/>] ของรายงาน <paramref name="reportId"/> · คืนจำนวนแถว</summary>
    public static async Task<int> StampAsync(AccountingDbContext db, Guid companyId, Guid reportId, DateTime start, DateTime endInclusive,
        DateTime nowUtc, CancellationToken ct = default)
    {
        var rows = await db.WhtCreditsReceived
            .CreditableInPeriod(companyId, start, endInclusive)
            .Where(w => w.CompanyId == companyId && w.Status == WhtCreditStatus.Received)
            .ToListAsync(ct);
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
        var rows = await db.WhtCreditsReceived
            .Where(w => w.CompanyId == companyId && w.Status == WhtCreditStatus.Claimed && w.ClaimedInTaxReportId == reportId)
            .ToListAsync(ct);
        foreach (var w in rows)
        {
            w.Status = ReleasedStatus;
            w.ClaimedInTaxReportId = null;
            w.ClaimedAt = null;
            w.UpdatedAt = nowUtc;
        }
        return rows.Count;
    }
}

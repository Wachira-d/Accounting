using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 228 ทีมภาษี (ฝ่ายค้าน S8) — ตัวหาผังกองทุนเงินทดแทน<b>ตัวเดียว</b> ที่ตั้งค้างจ่ายตอนจ่ายเงินเดือน (<c>PayrollService</c>) และตอนนำส่ง/ยอดค้าง
/// (<c>StatutoryRemittanceService</c>) ใช้ร่วมกัน — เดิมเงินเดือนหา "21816 หรือ 218xx ชื่อมีเงินทดแทน" แต่หน้านำส่งหาแค่ 21816 ตรงรหัส ⇒
/// บริษัทที่ใช้ผังชื่ออื่นตั้งค้างจ่ายได้แต่นำส่งไม่ได้/ยอดคงเหลืออ่านผิดผัง
/// </summary>
public static class WorkersCompensationAccounts
{
    /// <summary>ผังค้างจ่าย (21816 → 218xx ชื่อมี "เงินทดแทน") · null = ไม่พบ (ผู้เรียกตัดสินข้อความ)</summary>
    public static Task<ChartOfAccount?> PayableAsync(AccountingDbContext db, Guid companyId)
        => ResolveAsync(db, companyId, WorkersCompensationBase.PayableCode, "218");

    /// <summary>ผังค่าใช้จ่าย (54121 → 541xx ชื่อมี "เงินทดแทน") · null = ไม่พบ</summary>
    public static Task<ChartOfAccount?> ExpenseAsync(AccountingDbContext db, Guid companyId)
        => ResolveAsync(db, companyId, WorkersCompensationBase.ExpenseCode, "541");

    public const string NameContains = "เงินทดแทน";

    private static async Task<ChartOfAccount?> ResolveAsync(AccountingDbContext db, Guid companyId, string exact, string prefix)
        => await db.ChartOfAccounts.DefaultPostingCandidates(db).FirstOrDefaultAsync(a =>
               a.CompanyId == companyId && a.AccountCode == exact)
           ?? await db.ChartOfAccounts.DefaultFallbackCandidates(db).FirstOrDefaultAsync(a =>
               a.CompanyId == companyId && a.AccountCode.StartsWith(prefix) && a.AccountName.Contains(NameContains));
}

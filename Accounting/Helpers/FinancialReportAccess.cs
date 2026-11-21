using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 284 (คำตัดสิน 284-7 · commit f90fef74 · ผู้ประสานงานตัดสิน Q1 ตามที่เจ้าของมอบอำนาจ) — <b>ตัวตัดสินเดียว</b>ของด่านสิทธิ์รายงานการเงินรวมของบริษัท
/// (งบกำไรขาดทุน · งบดุล · งบทดลอง · กระแสเงินสด · บัญชีแยกประเภท · ยอดเดือนของหน้าหลักโหมดง่าย · อายุลูกหนี้/เจ้าหนี้ · ประมาณการเงินสด ·
/// งบประมาณเทียบจริง · ตัวสร้างรายงาน · ไฟล์ยื่นภาษี) ที่เดิมมีแค่ <c>[Authorize]</c> + สมาชิกบริษัท
///
/// <para>═══ กติกา ═══ ผ่านเมื่อ (1) ด่านจริง <c>IPermissionService.HasPermissionAsync</c> ให้คีย์ใดคีย์หนึ่ง (เจ้าของ/SystemAdmin ทุกคีย์ ·
/// นักบัญชีตาม <see cref="BuiltInRolePermissions.AccountantDefaultKeys"/> · บทบาทกำหนดเองที่ติ๊กคีย์) <b>หรือ</b> (2) สมาชิกที่<b>ไม่ผูกบทบาทกำหนดเอง</b>
/// (เมนู <c>"*"</c> — พฤติกรรมเดิมก่อนรอบ 284 · กันถดถอยของพนักงานที่เจ้าของไม่เคยตั้งบทบาท) · ไม่ใช่สมาชิก/รอสิทธิ์ ⇒ ไม่ผ่าน (fail closed) ·
/// ผู้ "เห็นเฉพาะของตัวเอง" (276) ถูกกันก่อนถึงด่านนี้ที่ <c>DocumentRestrictionFilter</c> (route ไม่อยู่ในรายการอนุญาต) เหมือนเดิม</para>
///
/// <para>ด่านคีย์เดิมที่เข้มอยู่แล้ว (แดชบอร์ด · FP&amp;A · ผู้บริหาร · งบรวม · ส่งออกบัญชี) <b>ไม่</b>ใช้ตัวนี้ — คงพฤติกรรมเดิม (ไม่ผ่อนลง)</para>
/// </summary>
public static class FinancialReportAccess
{
    /// <summary>คีย์ของยอดเดือน (หน้าหลักโหมดง่าย) — คีย์ของด่านอื่นเขียนเป็นค่าคงที่บน attribute <c>[RequireReportAccess(...)]</c> (C# ต้องเป็น const)
    /// และทะเบียนเมนู <see cref="MenuPermissionMap.LegacyKeys"/> · เทสต์ <c>PackFRound284Tests</c> เทียบทั้งสองฝั่ง</summary>
    public static readonly string[] MonthSnapshot = { PermissionKeys.ReportsFinancial, PermissionKeys.ReportsDashboard };

    /// <param name="keyGranted">ด่านจริงให้คีย์ใดคีย์หนึ่ง</param>
    /// <param name="member">สมาชิกที่ใช้งานได้ (ไม่ใช่ null = สมาชิก · <c>AccessPending</c> ถูกกันที่ middleware แล้ว)</param>
    /// <param name="hasCustomRole">ผูกบทบาทกำหนดเอง (<c>CompanyUser.CompanyRoleId</c> มีค่า)</param>
    public static bool Allows(bool keyGranted, bool member, bool hasCustomRole)
        => keyGranted || (member && !hasCustomRole);

    /// <summary>ด่านจริง (อ่านฐาน) — ผู้เรียก: <c>Filters/RequireReportAccessAttribute</c> · <c>AccountingController.GetMonthSnapshot</c> ·
    /// <c>RolePermissionService.PermissionDeniedMenuIdsAsync</c> (ผ่าน <see cref="MenuPermissionMap.LegacyKeys"/>)</summary>
    public static async Task<bool> AllowsAsync(AccountingDbContext db, IPermissionService perms, Guid companyId, Guid userId, IReadOnlyList<string> keys)
    {
        foreach (var k in keys)
            if (await perms.HasPermissionAsync(companyId, userId, k)) return true;
        var row = await db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
            .Select(cu => new { HasRole = cu.CompanyRoleId != null, cu.AccessPending })
            .FirstOrDefaultAsync();
        return Allows(false, row != null && !row.AccessPending, row?.HasRole ?? true);
    }

    /// <summary>ข้อความเมื่อไม่ผ่าน (ไทย · บอกทางไปต่อ) — คีย์แรกของกลุ่ม</summary>
    public static string DeniedMessage(IReadOnlyList<string> keys) => PermissionKeys.DeniedMessage(keys[0]);

    /// <summary>หน้าหลักโหมดง่าย: ป้ายแทนการ์ดยอดเดือน เมื่อบทบาทไม่มีสิทธิ์ดูตัวเลขการเงิน (หน้าหลักยังใช้ได้ — ปุ่มลัด/งานค้าง)</summary>
    public const string SnapshotHiddenNotice =
        "ซ่อนยอดรายรับ-รายจ่ายของเดือน — บทบาทของคุณไม่มีสิทธิ์ “งบการเงิน” หรือ “ดูแดชบอร์ดภาพรวม” · ปุ่มลัดด้านล่างใช้ได้ตามปกติ · ขอสิทธิ์ได้จากเจ้าของบริษัท (หน้า “บทบาทและสิทธิ์”)";
}

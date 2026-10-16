using System.Security.Claims;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Accounting.Helpers;

/// <summary>
/// <b>ตัวตัดสินเดียวของ "ผู้ขอคนนี้เห็นข้อมูลส่วนบุคคลแบบไม่ปกปิดได้ไหม"</b> (PDPA ม.26 · CLAUDE.md §J: ค่าเต็มเฉพาะสิทธิ์ <c>Pii.View</c>) +
/// บันทึกร่องรอยเมื่อได้เห็นจริง (ม.37(4) <c>PiiAccessLog</c>) — คนที่ถูกปิดบังไม่ได้เข้าถึง PII จึงไม่บันทึก
///
/// <para>ย้ายมาจาก <c>PayrollController.CanViewPiiAsync</c> (ฝ่ายค้าน ภ.ง.ด.1 P1-3) เพื่อให้ <c>TaxFilingExportController</c> ใช้ตัวเดียวกัน
/// โดยไม่คัดลอก — เดิมรายงานตรวจทาน/ดูตัวอย่าง ภ.ง.ด.1 ตรวจแค่ Payroll.View แล้วแสดงเลขบัตรเต็ม ขณะที่หน้ารายงาน ภ.ง.ด.1
/// ของหน้าเงินเดือน (ข้อมูลชุดเดียวกัน) ปิดบังเมื่อไม่มี Pii.View</para>
/// </summary>
public static class PiiViewGate
{
    /// <param name="operation">ชนิดการเข้าถึงใน PiiAccessLog — "Read" (ดูบนจอ) / "Export" (ไฟล์ส่งออก)</param>
    public static async Task<bool> CanViewAsync(IPermissionService permissions,
        Services.Implementations.Pdpa.IPdpaService? pdpa, ClaimsPrincipal user, HttpContext? http,
        Guid companyId, Guid? subjectEmployeeId = null, string operation = "Read",
        string purpose = "ดูข้อมูลพนักงานแบบไม่ปกปิด (HR/Payroll)")
    {
        var userId = JwtHelper.GetUserIdFromClaims(user);
        var can = await permissions.HasPermissionAsync(companyId, userId, Models.Constants.PermissionKeys.PiiView);
        // ม.37(4) — เปิดดูแบบ raw ต้องมีร่องรอยว่าใครดูของใครเมื่อไร
        //
        // ⚠️ doc-comment ของ PermissionKeys.PiiView ระบุข้อนี้ไว้ตั้งแต่ต้นและ
        // PdpaService.LogPiiAccessAsync ก็เขียนไว้ครบ — แต่**ไม่มี call site เลย
        // ทั้งเรพ** ⇒ ตารางว่างเปล่าตลอด = ไม่มี control จริง
        // (doc-comment ที่บอกว่า "ป้องกันแล้วที่ X" เป็นเจตนา ไม่ใช่หลักฐาน)
        //
        // log เฉพาะตอน "ได้ดูจริง" — คนที่ถูก mask ไม่ได้เข้าถึง PII จึงไม่ต้องบันทึก
        if (can && pdpa != null)
        {
            await pdpa.LogPiiAccessAsync(companyId, userId,
                user.FindFirst(ClaimTypes.Email)?.Value ?? "",
                subjectType: "Employee", subjectId: subjectEmployeeId ?? Guid.Empty,
                fieldName: subjectEmployeeId.HasValue ? "*" : "* (รายการพนักงาน)",
                operation: operation, purpose: purpose,
                ipAddress: http?.Connection?.RemoteIpAddress?.ToString(),
                userAgent: http?.Request?.Headers.UserAgent.ToString());
        }
        return can;
    }
}

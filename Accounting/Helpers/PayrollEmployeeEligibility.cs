using System.Globalization;
using System.Linq.Expressions;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตั้งตัวเดียวของ "พนักงานคนนี้อยู่ในงวดเงินเดือนนี้ไหม"
///
/// <para>═══ ที่มา (รอบ 200 ทีม PR1) ═══ เงื่อนไขนี้เคยเขียน inline ใน <c>PayrollService.CalculatePayrollAsync</c>
/// ที่เดียว · พอมีทางเข้าที่สอง (➕ เพิ่มพนักงานเข้ารอบ · รายชื่อที่เพิ่มได้) ถ้าคัดลอกเงื่อนไขไปอีกที่
/// = สองกติกาที่จะ drift (เคสจริงของไฟล์นั้นเอง: D-S2 ลาออกกลางเดือนหายจากรอบ เพราะ <c>IsActive</c>
/// ตัดคนที่เพิ่งลาออกก่อนเงื่อนไข <c>EndDate</c> จะได้ทำงาน) ⇒ ยุบมาไว้ที่นี่ แล้วให้ทุกทางเข้าเรียกตัวนี้</para>
///
/// <para><see cref="InPeriod"/> คืน expression (ใช้ใน EF ได้ · <c>.Compile()</c> ใช้ตรวจในหน่วยความจำ) ·
/// <see cref="Reason"/> คืนข้อความไทยพร้อมทางไปต่อเมื่อไม่มีสิทธิ์ — สองตัวต้องตัดสินตรงกันทุกกรณี
/// (เทสต์ <c>PayrollEmployeeEligibilityTests</c> ไล่ตารางเทียบกัน)</para>
///
/// <para>⚠️ tenant (<c>CompanyId</c>) และ <c>IsDeleted</c> <b>ไม่อยู่ใน expression นี้โดยตั้งใจ</b> —
/// ผู้เรียกต้องเขียน <c>e.CompanyId == companyId &amp;&amp; !e.IsDeleted</c> เองใน Where ของตัวเอง
/// ให้ด่าน tenant มองเห็นที่จุด query (กฎ M)</para>
/// </summary>
public static class PayrollEmployeeEligibility
{
    /// <summary>มีสิทธิ์อยู่ในงวด [<paramref name="periodStart"/>, <paramref name="periodEnd"/>] ไหม:
    /// เริ่มงานไม่หลังสิ้นงวด · ไม่พ้นสภาพก่อนเริ่มงวด · และยัง active <b>หรือ</b> พ้นสภาพภายใน/หลังงวด
    /// (D-S2: ลาออกกลางเดือน <c>IsActive=false</c> แต่ต้องได้เงินเดือนงวดสุดท้าย)</summary>
    public static Expression<Func<Employee, bool>> InPeriod(DateTime periodStart, DateTime periodEnd)
        => e => (e.IsActive || (e.EndDate != null && e.EndDate >= periodStart))
            && e.StartDate <= periodEnd
            && (e.EndDate == null || e.EndDate >= periodStart);

    /// <summary>เหตุผล (ไทย · พร้อมทางไปต่อ) ที่พนักงานคนนี้<b>ไม่มีสิทธิ์</b>อยู่ในงวด — <c>null</c> = มีสิทธิ์
    /// (ตัดสินตรงกับ <see cref="InPeriod"/> ทุกกรณี)</summary>
    public static string? Reason(Employee employee, DateTime periodStart, DateTime periodEnd)
    {
        ArgumentNullException.ThrowIfNull(employee);
        if (employee.StartDate > periodEnd)
            return $"เริ่มงานวันที่ {D(employee.StartDate)} หลังวันสิ้นงวด ({D(periodEnd)}) — "
                + "ถ้าวันเริ่มงานบันทึกผิด ให้แก้ที่หน้าพนักงานก่อน หรือเพิ่มคนนี้ในรอบของงวดที่เริ่มงานจริง";
        if (employee.EndDate != null && employee.EndDate < periodStart)
            return $"พ้นสภาพพนักงานวันที่ {D(employee.EndDate.Value)} ก่อนวันเริ่มงวด ({D(periodStart)}) — "
                + "ถ้ายังค้างจ่ายงวดสุดท้าย ให้เพิ่มในรอบของงวดที่พ้นสภาพ หรือแก้วันพ้นสภาพที่หน้าพนักงานถ้าบันทึกผิด";
        if (!employee.IsActive && employee.EndDate == null)
            return "พนักงานถูกปิดใช้งานโดยไม่มีวันพ้นสภาพ — ระบบจึงไม่รู้ว่ายังทำงานในงวดนี้หรือไม่ · "
                + "เปิดใช้งานอีกครั้ง หรือระบุวันพ้นสภาพที่หน้าพนักงานก่อน";
        return null;
    }

    // ค.ศ. เสมอ (เครื่องที่ตั้ง culture th-TH จะได้ พ.ศ. เงียบ ๆ ถ้าไม่ระบุ InvariantCulture)
    private static string D(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}

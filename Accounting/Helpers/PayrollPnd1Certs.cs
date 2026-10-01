namespace Accounting.Helpers;

/// <summary>
/// 50 ทวิ ภ.ง.ด.1 ที่ออกอัตโนมัติจากรอบเงินเดือน — ตัวตั้งเดียวของ "เลขที่ใบ" และ "แถวรายคนถูกโหลดมาจริงไหม"
/// (รอบ 201 ทีม PR2 · ผลฝ่ายค้านรอบสอง P1-b/P1-c)
///
/// <para><b>P1-c</b>: เลขเดิม <c>PND1-{ปี}{เดือน}-{รหัสพนักงาน}</c> ชน unique (CompanyId, CertificateNumber) ซึ่งนับใบ Voided ด้วย ⇒
/// ยกเลิกรอบแล้วสร้างรอบเดือนเดิมใหม่แล้วจ่าย (หรือออกซ้ำตอน re-post) ล้มทุกครั้ง · คำตัดสิน main agent: ใบแรกคงรูปเดิม ·
/// ชนแล้วต่อท้าย <c>-2</c>, <c>-3</c> … (ไม่เปลี่ยน index · ไม่มี migration) · ผู้เรียกส่งชุดเลขที่ใช้แล้วของงวดนั้น
/// (รวมใบ Voided/ลบแล้ว) และหาเลขใต้ล็อกแถวรอบ</para>
///
/// <para><b>P1-b</b>: รอบที่มีพนักงานแต่ไม่มีแถวรายคนที่โหลดมา = ผู้เรียกลืม Include (เส้น background scope) ⇒ ต้องล้มดัง
/// ไม่ใช่ "ไม่มีใครถูกหักภาษี" แล้วคืนเงียบ</para>
/// </summary>
public static class PayrollPnd1Certs
{
    /// <summary>คำนำหน้าเลขใบของงวด — ใช้ค้นเลขที่ใช้แล้ว</summary>
    public static string Prefix(int year, int month) => $"PND1-{year}{month:D2}-";

    /// <summary>เลขใบถัดไปที่ว่าง — ใบแรกของคนนี้ในงวดใช้รูปเดิม · ชนแล้ว -2, -3 … (เทียบตรงตัวอักษร)</summary>
    public static string NextNumber(int year, int month, string employeeCode, IReadOnlySet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var baseNumber = Prefix(year, month) + (employeeCode ?? "").Trim();
        if (!taken.Contains(baseNumber)) return baseNumber;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseNumber}-{n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>รอบมีพนักงาน (<paramref name="employeeCount"/> &gt; 0) แต่ไม่มีแถวรายคนที่โหลดมาเลย ⇒ ผู้เรียกไม่ได้โหลด (ไม่ใช่ "ไม่มีใครถูกหัก")</summary>
    public static bool DetailsNotLoaded(int loadedDetails, int employeeCount) => loadedDetails == 0 && employeeCount > 0;
}

namespace Accounting.Helpers;

/// <summary>
/// กติกา "ช่องนี้ของพนักงานควรถูกเขียนเป็นอะไร" ตอนสร้าง/แก้ไข — **ตัวตัดสินตัวเดียว**
/// ที่ <c>PayrollService.CreateEmployeeAsync</c>/<c>UpdateEmployeeAsync</c> เรียก
///
/// ═══ ที่มา (A05 · D-07 · P0 รอบ 189 · แก้รอบ 193) ═══
/// เปิด "แก้ไขพนักงาน" → ช่อง ชื่อ/นามสกุล/เลขบัตร/วันเริ่มงาน/รหัส/คำนำหน้า มีค่าเดิมให้แก้
/// → กดบันทึก → toast "แก้ไขสำเร็จ" → เปิดใหม่ **ค่าเดิมกลับมาทุกช่อง** เพราะ
/// <c>UpdateEmployeeRequest</c> ไม่มีช่องเหล่านี้เลย (silent no-op — กฎเหล็ก #4 A) ·
/// เลขบัตรที่พิมพ์ผิดตอนสร้างแก้ผ่าน UI ไม่ได้ตลอดกาล ทั้งที่ไหลลง ภ.ง.ด.1/สปส.1-10
///
/// ═══ กับดักที่ต้องปิดพร้อมกัน: "ค่าที่ถูกปิดบัง" ย้อนกลับมาเขียนทับของจริง ═══
/// ผู้ไม่มีสิทธิ์ <c>pii:view</c> ได้เลขบัตร <c>1-XXXX-XXXXX-XX-3</c> · เบอร์ <c>08XXXXXX99</c>
/// · อีเมล <c>n***@…</c> ไปเติมฟอร์ม — ถ้าเซิร์ฟเวอร์รับค่าที่ส่งกลับมาตรง ๆ แค่เปิด
/// ฟอร์มแล้วกดบันทึก ข้อมูลจริงจะถูกแทนด้วยดาว/X ถาวร (เบอร์/อีเมลโดนแบบนี้อยู่แล้ว
/// ก่อนรอบนี้) ⇒ ค่าที่ <b>เท่ากับค่าปิดบังของของเดิม</b> = "ไม่ได้แก้" = ไม่แตะ
/// </summary>
public static class EmployeeRecordEdit
{
    /// <summary>ผลการตัดสินหนึ่งช่อง: <see cref="Changes"/> = ต้องเขียน <see cref="Value"/>
    /// ลง entity · <see cref="Error"/> ไม่ null = ปฏิเสธทั้งคำขอด้วยข้อความนี้ (ภาษาไทย ชี้ช่อง)</summary>
    public sealed record FieldEdit(bool Changes, string? Value, string? Error)
    {
        internal static readonly FieldEdit Keep = new(false, null, null);
        internal static FieldEdit Set(string? value) => new(true, value, null);
        internal static FieldEdit Reject(string error) => new(false, null, error);
    }

    /// <summary>ค่าที่ฟอร์มส่งกลับมา = ค่าปิดบังของของเดิมเป๊ะ ⇒ ผู้ใช้ไม่ได้แก้ช่องนี้</summary>
    public static bool IsMaskedEcho(string? incoming, string? maskedCurrent)
        => incoming != null && maskedCurrent != null
           && string.Equals(incoming.Trim(), maskedCurrent, StringComparison.Ordinal);

    /// <summary>เลข 13 หลัก (บัตรประชาชน / ผู้เสียภาษีของบุคคล) — null = ไม่แตะ · "" = ล้าง ·
    /// ค่าปิดบังของเดิม = ไม่แตะ · อื่น ๆ ต้องผ่าน checksum กลาง (<see cref="ThaiTaxIdValidator"/>)
    /// แล้วเก็บเป็นตัวเลขล้วน</summary>
    /// <param name="current">ค่าที่เก็บอยู่ (ถอดรหัสแล้ว) · สร้างใหม่ส่ง null</param>
    /// <param name="fieldLabel">ป้ายภาษาไทยของช่องบนฟอร์ม — ใช้ในข้อความปฏิเสธ</param>
    public static FieldEdit ThaiIdNumber(string? incoming, string? current, string fieldLabel)
    {
        if (incoming is null) return FieldEdit.Keep;
        var t = incoming.Trim();
        if (t.Length == 0)
            return string.IsNullOrWhiteSpace(current) ? FieldEdit.Keep : FieldEdit.Set(null);
        if (!string.IsNullOrWhiteSpace(current) && IsMaskedEcho(t, PiiMask.CitizenId(current)))
            return FieldEdit.Keep;
        if (t.IndexOf('X') >= 0 || t.IndexOf('x') >= 0)
            return FieldEdit.Reject($"{fieldLabel}: ค่านี้เป็นเลขที่ถูกปิดบัง (PDPA) — "
                + "พิมพ์เลขจริง 13 หลัก หรือคงค่าเดิมไว้โดยไม่แก้ช่องนี้");
        // เลขเดิม (แค่ต่างขีด/ช่องว่าง) = ไม่ได้แก้ — ตรวจ**ก่อน** checksum: เลขเก่าที่เข้าระบบ
        // ก่อนมีด่าน checksum (สมัยตรวจแค่ regex 13 หลัก · HRIS sync · นำเข้า CSV) ต้องไม่ทำให้
        // HR แก้ช่องอื่นของพนักงานคนนั้นไม่ได้เลย (เข้มขึ้นต้องมีทางไปต่อ — F2 ข้อ 8)
        var digits = ThaiTaxId.Normalize(t);
        if (!string.IsNullOrWhiteSpace(current) && digits == ThaiTaxId.Normalize(current))
            return FieldEdit.Keep;
        var check = ThaiTaxIdValidator.Check(t);
        if (!check.IsValid)
            return FieldEdit.Reject($"{fieldLabel}: {check.Reason}");
        return FieldEdit.Set(digits);
    }

    /// <summary>ช่องข้อความที่บังคับ (ชื่อ/นามสกุลภาษาไทย) — null = ไม่แตะ · ว่าง = ปฏิเสธ</summary>
    public static FieldEdit RequiredText(string? incoming, string fieldLabel)
    {
        if (incoming is null) return FieldEdit.Keep;
        var t = incoming.Trim();
        return t.Length == 0 ? FieldEdit.Reject($"{fieldLabel}ห้ามว่าง") : FieldEdit.Set(t);
    }

    /// <summary>ช่องข้อความไม่บังคับ — null = ไม่แตะ · "" = ล้างเป็น null (กฎเหล็ก #4 B
    /// "แก้ไข: "" = ล้างค่า") · อื่น ๆ = ตัดช่องว่างหัวท้าย</summary>
    public static FieldEdit OptionalText(string? incoming)
    {
        if (incoming is null) return FieldEdit.Keep;
        var t = incoming.Trim();
        return FieldEdit.Set(t.Length == 0 ? null : t);
    }

    /// <summary>เหตุผลที่แก้รหัสพนักงานไม่ได้ (null = แก้ได้) — รหัสถูกประทับลงเลขที่
    /// 50 ทวิ (<c>PND1-yyyymm-รหัส</c>) · สลิป · ไฟล์ยื่นที่ออกไปแล้ว เมื่อมีประวัติ
    /// เงินเดือน การเปลี่ยนรหัสทำให้เอกสารเดิมกับทะเบียนพนักงานอ้างกันไม่ติด</summary>
    public static string? CodeLockReason(bool hasPayrollHistory)
        => hasPayrollHistory
            ? "แก้รหัสพนักงานไม่ได้ — พนักงานคนนี้มีรายการเงินเดือนแล้ว และรหัสถูกใช้อ้างอิงใน "
              + "สลิป/หนังสือรับรอง 50 ทวิ/ไฟล์ยื่นที่ออกไปแล้ว (ช่องอื่นแก้ได้ตามปกติ)"
            : null;

    /// <summary>รหัสพนักงาน — null หรือเท่าเดิม = ไม่แตะ · ว่าง = ปฏิเสธ · มีประวัติเงินเดือน =
    /// ปฏิเสธพร้อมเหตุผลจาก <see cref="CodeLockReason"/> (การตรวจรหัสซ้ำอยู่ที่ service เพราะต้อง query)</summary>
    public static FieldEdit EmployeeCode(string? incoming, string current, bool hasPayrollHistory)
    {
        if (incoming is null) return FieldEdit.Keep;
        var t = incoming.Trim();
        if (t.Length == 0) return FieldEdit.Reject("รหัสพนักงานห้ามว่าง");
        if (string.Equals(t, current, StringComparison.Ordinal)) return FieldEdit.Keep;
        var locked = CodeLockReason(hasPayrollHistory);
        return locked != null ? FieldEdit.Reject(locked) : FieldEdit.Set(t);
    }

    /// <summary>วันเริ่มงานที่รับได้ — ไม่เกิน 1 ปีข้างหน้า (กติกาเดียวของทั้งสร้างและแก้ไข)</summary>
    public static string? StartDateError(DateTime startDate, DateTime utcNow)
        => startDate > utcNow.AddYears(1) ? "วันเริ่มงานต้องไม่เกิน 1 ปีข้างหน้า" : null;
}

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

    /// <summary>รอบ 274-10 — <b>ด่านเดียวของทุกทางเข้าที่เขียนคอลัมน์ PII ของพนักงาน</b> (แก้ไข · สร้าง · HRIS sync · นำเข้าไฟล์):
    /// ค่าที่เป็น ciphertext (<c>enc1:…</c> หรือรูปรุ่นก่อนไม่มีคำนำหน้า) ไม่ใช่ข้อมูล — ห้ามเก็บเป็นค่า (เดิมถูกเข้ารหัสซ้ำ = เข้ารหัสซ้อน ·
    /// ที่มา: ช่วงถอดคีย์เดิมไม่ได้ API คืน ciphertext ดิบ แล้วไฟล์ส่งออก/ฟอร์มถูกส่งกลับเข้ามา) · คืนข้อความไทย หรือ null เมื่อผ่าน</summary>
    public static string? CipherInputProblem(string? value, string fieldLabel)
    {
        var t = value?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (SecretInputRule.CipherPrefixProblem(t, fieldLabel) is string p) return p;
        // ฝ่ายค้าน 274-10 LOW-1: เครื่องหมายภายในของ converter (อักขระควบคุม) ไม่ใช่ข้อมูล
        if (EncryptedColumnConverter.HasInternalMarker(t))
            return $"{fieldLabel}: ค่านี้มีอักขระควบคุมภายในของระบบ ไม่ใช่ค่าจริง — กรอกค่าจริง";
        return EncryptedColumnConverter.IsUnreadable(t)
            ? $"{fieldLabel}: ค่านี้เป็นข้อความที่ระบบเข้ารหัสไว้ (ciphertext) ไม่ใช่ค่าจริง — กรอกค่าจริง"
            : null;
    }

    /// <summary>รอบ 274-10: ฟอร์มส่ง ciphertext ที่<b>เก็บอยู่ในแถวนี้เองและถอดไม่ได้</b>กลับมาเป๊ะ (ผู้ใช้เปิดฟอร์มแล้วกดบันทึกโดยไม่แตะช่องนี้) ⇒ "ไม่ได้แก้" = คงค่าเดิม
    /// (เดิม: ปฏิเสธทั้งคำขอ ⇒ แก้ช่องอื่นของพนักงานคนนั้นไม่ได้เลย · หรือถูกเข้ารหัสซ้ำ) · ค่านั้นยังขึ้นในรายงาน <c>tax-identity-health</c> และป้าย ⛔ ของไฟล์ยื่น (ไม่เงียบ)</summary>
    public static bool IsEchoOfStoredUnreadable(string? incoming, string? current)
        => incoming != null && EncryptedColumnConverter.IsUnreadable(current)
           && string.Equals(incoming.Trim(), current!.Trim(), StringComparison.Ordinal);

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
        // รอบ 274-10: ciphertext ของแถวนี้เองที่ส่งกลับมาเป๊ะ = ไม่ได้แก้ · ciphertext อื่น = ปฏิเสธ — ตรวจ<b>ก่อน</b>ด่าน X
        // (base64 มักมีตัว X ⇒ เดิมได้ข้อความ "เลขที่ถูกปิดบัง" ซึ่งไม่ใช่สาเหตุจริง — F2 ข้อ 7)
        if (IsEchoOfStoredUnreadable(t, current)) return FieldEdit.Keep;
        // ฝ่ายค้านรอบ 231 S2: คอลัมน์นี้เข้ารหัสผ่าน EncryptedColumnConverter — ciphertext ห้ามเข้า (ปิด oracle · กันเข้ารหัสซ้อน)
        if (CipherInputProblem(t, fieldLabel) is string cipherProblem)
            return FieldEdit.Reject(cipherProblem);
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

    /// <summary>เลขประกันสังคม (ผู้ประกันตน) — รอบ 201 ทีม PR2 (BACKLOG A-PR1 · D-06 UI): แรงงานต่างด้าว/ผู้ที่ สปส. ออกเลขแยกจาก
    /// เลขบัตร ต้องกรอกได้ที่หน้าพนักงาน (เดิมไม่มีช่อง ⇒ ไฟล์ สปส.1-10 ใช้เลขบัตรแทน ซึ่งคนกลุ่มนี้ไม่มี/ไม่ตรง)
    ///
    /// <para>null = ไม่แตะ · "" = ล้าง (กลับไปใช้เลขบัตรใน <c>SsoInsuredNumber.Resolve</c>) · ค่าปิดบังของเดิม = ไม่แตะ ·
    /// มี X = ปฏิเสธ (ค่าปิดบังที่ไม่ใช่ของเดิม) · ตัดขีด/ช่องว่าง แล้วต้องเป็นตัวเลข 13 หลัก · <b>ไม่ตรวจ checksum</b>
    /// (เลขที่ สปส. ออกให้คนต่างด้าวไม่รับประกันว่าผ่าน mod-11 ของเลขบัตร — ปฏิเสธเลขจริง = ทางตันที่แย่กว่า) ·
    /// เก็บเป็นตัวเลขล้วน (ไฟล์ สปส.1-10 ใช้ค่านี้ตรง)</para></summary>
    public static FieldEdit SsoInsuredNumber(string? incoming, string? current)
    {
        if (incoming is null) return FieldEdit.Keep;
        var t = incoming.Trim();
        if (t.Length == 0)
            return string.IsNullOrWhiteSpace(current) ? FieldEdit.Keep : FieldEdit.Set(null);
        if (!string.IsNullOrWhiteSpace(current) && IsMaskedEcho(t, PiiMask.CitizenId(current)))
            return FieldEdit.Keep;
        if (IsEchoOfStoredUnreadable(t, current)) return FieldEdit.Keep;   // รอบ 274-10 (แบบ ThaiIdNumber)
        if (CipherInputProblem(t, "เลขประกันสังคม") is string cipherProblem) return FieldEdit.Reject(cipherProblem);
        if (t.IndexOf('X') >= 0 || t.IndexOf('x') >= 0)
            return FieldEdit.Reject("เลขประกันสังคม: ค่านี้เป็นเลขที่ถูกปิดบัง (PDPA) — พิมพ์เลขจริง 13 หลัก หรือคงค่าเดิมไว้โดยไม่แก้ช่องนี้");
        var stripped = t.Replace("-", "").Replace(" ", "");
        // ★ ฝ่ายค้าน PR2 (SSO-1): เลขเดิม (แค่ต่างขีด/ช่องว่าง) = ไม่ได้แก้ — ตรวจ**ก่อน**ด่าน 13 หลัก (แบบ ThaiIdNumber) ·
        //   เลขเก่าที่เข้ามาทาง HRIS sync/นำเข้า CSV ก่อนมีด่าน (เช่น 10 หลัก) ต้องไม่ทำให้ HR แก้ช่องอื่นของคนนั้นไม่ได้เลย
        //   (ฟอร์มส่งเลขเดิมกลับมาทุกครั้งที่บันทึก — เข้มขึ้นต้องมีทางไปต่อ F2 ข้อ 8)
        if (!string.IsNullOrWhiteSpace(current) && stripped == current.Replace("-", "").Replace(" ", "").Trim())
            return FieldEdit.Keep;
        if (stripped.Length != 13 || !stripped.All(char.IsDigit))
            return FieldEdit.Reject("เลขประกันสังคม: ต้องเป็นตัวเลข 13 หลัก (ขีด/ช่องว่างได้) — "
                + "เว้นว่างถ้าใช้เลขบัตรประชาชนเป็นเลขผู้ประกันตน");
        return FieldEdit.Set(stripped);
    }

    public const string SyncOk = "สำเร็จ";
    public const string SyncWarn = "เตือน";
    public const string SyncSkipped = "มีรายการที่ข้าม";

    /// <summary>ฝ่ายค้าน 274-10 LOW-4: สถานะงาน HRIS sync — ข้ามแถว ⇒ "มีรายการที่ข้าม" · สร้างแล้วแต่ไม่เก็บบางช่อง ⇒ "เตือน" (ห้ามตอบ "สำเร็จ" เงียบ ๆ) · อื่น ๆ "สำเร็จ"</summary>
    public static string SyncStatus(int skipped, int warnings)
        => skipped > 0 ? SyncSkipped : warnings > 0 ? SyncWarn : SyncOk;

    /// <summary>วันเริ่มงานที่รับได้ — ไม่เกิน 1 ปีข้างหน้า (กติกาเดียวของทั้งสร้างและแก้ไข)</summary>
    public static string? StartDateError(DateTime startDate, DateTime utcNow)
        => startDate > utcNow.AddYears(1) ? "วันเริ่มงานต้องไม่เกิน 1 ปีข้างหน้า" : null;
}

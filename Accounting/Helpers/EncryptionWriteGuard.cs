namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก A ฝ่ายค้าน MED-2 — <b>ประตูเขียนข้อมูลเข้ารหัสของเครื่องนี้</b> (ตัวเดียว · สถานะของ process นี้ ไม่ใช่ state ข้ามเครื่อง:
/// แต่ละเครื่องตัดสินจากคีย์ของตัวเองเทียบกับลายนิ้วมือในฐาน — กฎ #4 §D)
///
/// <para><b>ที่มา:</b> เครื่องที่คีย์ไม่ตรงกับที่ระบบใช้ (<see cref="KeyFingerprintVerdict.Mismatch"/>/<see cref="KeyFingerprintVerdict.RotatedBack"/>)
/// เดิมหยุดแค่งานย้ายคีย์ แต่ยังเข้ารหัสค่าที่ผู้ใช้บันทึกใหม่ด้วยคีย์ของตัวเอง ⇒ เครื่องอื่นอ่านไม่ได้ (เลขบัตร/รหัสผ่านที่เพิ่งกรอกหายในเครื่องอื่น) ·
/// ตอนนี้: ผลตรวจคีย์ (<c>EncryptionKeyFingerprintService.CheckKeyAsync</c>) ตั้งประตูนี้ · <see cref="SecretProtector.Protect"/> และ
/// <c>EncryptedColumnConverter</c> ฝั่งเขียนเรียก <see cref="ThrowIfBlocked"/> ⇒ ล้มด้วย <see cref="EncryptionKeyMismatchException"/> (ข้อความไทย) ·
/// <b>ฝั่งอ่านไม่ถูกแตะ</b> (ข้อมูลยังอ่านได้ตามห่วงโซ่คีย์)</para>
/// </summary>
public static class EncryptionWriteGuard
{
    private static volatile string? _blockedReason;

    /// <summary>เหตุที่ปิดการเขียน (null = เขียนได้)</summary>
    public static string? BlockedReason => _blockedReason;

    /// <summary>ตั้งตามผลตรวจคีย์ — ปัญหาคีย์ ⇒ ปิด · อื่น ๆ ⇒ เปิด</summary>
    public static void Apply(KeyFingerprintVerdict verdict)
        => _blockedReason = EncryptionKeyFingerprintRule.IsProblem(verdict)
            ? "เครื่องให้บริการนี้ใช้คีย์เข้ารหัสไม่ตรงกับที่ระบบใช้อยู่ — ระบบหยุดบันทึกข้อมูลที่ต้องเข้ารหัส (เลขประจำตัว/เลขบัญชี/รหัสผ่านการเชื่อมต่อ) ไว้ก่อน "
              + "เพื่อไม่ให้เครื่องอื่นอ่านไม่ได้ · ลองใหม่อีกครั้ง หรือแจ้งผู้ดูแลระบบ (ข้อมูลเดิมยังอ่านได้ตามปกติ)"
            : null;

    /// <summary>เปิดประตู (เทสต์ · ผู้ดูแลยืนยันคีย์แล้ว)</summary>
    public static void Clear() => _blockedReason = null;

    /// <param name="what">สิ่งที่กำลังเขียน (ใช้ใน log/ข้อความ)</param>
    public static void ThrowIfBlocked(string what)
    {
        if (_blockedReason is string r) throw new EncryptionKeyMismatchException(r, what);
    }
}

/// <summary>รอบ 275 แพ็ก A (MED-2): เขียนข้อมูลเข้ารหัสไม่ได้เพราะคีย์ของเครื่องนี้ไม่ตรงกับที่ระบบใช้ — 503 (ลองใหม่/เครื่องอื่นรับได้) · ข้อความไทยถึงผู้ใช้</summary>
public sealed class EncryptionKeyMismatchException : BusinessRuleException
{
    public string What { get; }

    public EncryptionKeyMismatchException(string message, string what)
        : base(message, EncryptionKeyFingerprintRule.RuleCode, 503) => What = what;
}

namespace Accounting.Helpers;

/// <summary>
/// รอบ 231 ฝ่ายค้าน S2/N1 — ด่านรับค่า<b>ก่อนเข้ารหัส</b> (ตัวตั้งตัวเดียว · เรียกใน <c>SecretProtector.Protect</c> ⇒ ครอบผู้เขียนความลับทุกช่อง
/// และใน <c>EmployeeRecordEdit</c>/<c>PayrollService</c> สำหรับคอลัมน์ PII ที่เข้ารหัสผ่าน <c>EncryptedColumnConverter</c>)
///
/// <para><b>S2 — ปิด decryption oracle:</b> ถ้า "มีคำนำหน้า <c>enc1:</c> แล้ว = ไม่เข้ารหัสซ้ำ" ผู้ใช้ที่มีสิทธิ์เขียนช่องหนึ่งส่ง <c>enc1:&lt;ciphertext ของช่องอื่น&gt;</c>
/// เข้ามาได้ ⇒ ระบบเก็บตามนั้น ⇒ ฟีเจอร์ที่อ่านช่องนั้นถอดให้ (ส่งอีเมล/เรียก LINE ด้วยความลับของคนอื่น) ⇒ ค่าที่ผู้ใช้ส่งมา<b>ห้าม</b>ขึ้นต้นด้วยคำนำหน้า —
/// ปฏิเสธด้วยข้อความไทย ไม่เข้ารหัสซ้อนเงียบ ๆ (ซ้อนแล้ว "บันทึกสำเร็จ" แต่ค่าที่ได้คือสตริง enc1:… ไม่ใช่ความลับ)</para>
///
/// <para><b>N1 — กัน PostgreSQL 22001 (value too long):</b> ciphertext ยาวกว่า plaintext ~1.4 เท่า + 5 ตัว · คอลัมน์ความลับที่แคบสุดที่ยอมรับ token ยาวคือ
/// <c>varchar(2000)</c> (refresh token · LINE token ของบริษัท) ⇒ plaintext ≤ <see cref="MaxPlaintextBytes"/> ไบต์ ⇒ ciphertext ≤ 1,909 ตัว ·
/// คอลัมน์ <c>varchar(500)</c> (รหัสผ่าน SMTP · client secret · certificate password) รับค่าจริง ≤ 100 ตัวอยู่แล้ว — ด่านนี้ไม่รู้คอลัมน์ปลายทาง
/// จึงกันได้เฉพาะเพดานรวม (ค่าที่ยาว 331–1,400 ตัวลงคอลัมน์ 500 ยังเป็น 22001 — จดใน TEST_PLAN SEC2-26)</para>
/// </summary>
public static class SecretInputRule
{
    public const string RuleCipherPrefix = "SEC-INPUT-CIPHER-PREFIX";
    public const string RuleTooLong = "SEC-INPUT-TOO-LONG";

    /// <summary>plaintext ยาวสุดที่ยอมรับ นับเป็นไบต์ UTF-8 (ไทย 3 ไบต์/ตัว) — ≤ 1,400 ไบต์ ⇒ ciphertext ≤ 5 + ceil((1,400+28)/3)×4 = 1,909 ตัว &lt; varchar(2000)</summary>
    public const int MaxPlaintextBytes = 1400;

    /// <summary>คืนข้อความปฏิเสธ (ไทย) หรือ null เมื่อค่าผ่าน — ใช้กับเส้นที่ประกอบข้อความเอง (<c>EmployeeRecordEdit.FieldEdit.Reject</c>)</summary>
    public static string? CipherPrefixProblem(string? value, string fieldLabel)
        => EncryptionHelper.IsEncrypted(value)
            ? $"{fieldLabel}: ค่าห้ามขึ้นต้นด้วย {EncryptionHelper.CipherPrefix} (เป็นรูปแบบภายในของค่าที่เข้ารหัสแล้ว) — กรอกค่าจริง"
            : null;

    /// <summary>ด่านเดียวของ <c>SecretProtector.Protect</c>: ค่าว่างผ่าน · ขึ้นต้น enc1: ⇒ <see cref="BusinessRuleException"/> (400 · <see cref="RuleCipherPrefix"/>) ·
    /// ยาวเกิน ⇒ <see cref="RuleTooLong"/></summary>
    public static void Check(string? plaintext, string fieldLabel = "ค่าลับ")
    {
        if (string.IsNullOrEmpty(plaintext)) return;
        if (CipherPrefixProblem(plaintext, fieldLabel) is string p)
            throw new BusinessRuleException(p, RuleCipherPrefix);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(plaintext);
        if (bytes > MaxPlaintextBytes)
            throw new BusinessRuleException(
                $"{fieldLabel}: ยาวเกินที่เก็บได้ ({bytes:N0} ไบต์ · สูงสุด {MaxPlaintextBytes:N0} ไบต์) — ตรวจว่าคัดลอกมาถูกช่อง", RuleTooLong);
    }
}

namespace Accounting.Helpers;

/// <summary>
/// ประกอบข้อความเตือน "ความลับที่เก็บไว้แต่ถอดรหัสไม่ออก" — ที่เดียวของระบบ
/// (หน้าตั้งค่าอีเมลของบริษัทและของแอดมินใช้ข้อความชุดเดียวกัน)
///
/// ═══ ทำไมต้องมี ═══
/// ค่าที่เข้ารหัสไว้ด้วย <c>Security:EncryptionKey</c> ตัวเก่าจะถอดไม่ออกเมื่อคีย์
/// ถูกเปลี่ยน (ย้ายเซิร์ฟเวอร์ · ตั้ง ENCRYPTION_KEY จริงทับ dev key · deploy ใหม่
/// โดยไม่ยกคีย์เดิมมา). ตัวถอดรหัสคืนค่าว่างเพื่อไม่ให้ ciphertext หลุดออกไป —
/// ผลคือระบบเอา **รหัสผ่านว่าง** ไป authenticate แล้วเซิร์ฟเวอร์ปลายทางตอบ
/// error ที่ไม่ได้ชี้ต้นเหตุเลย (Gmail ตอบ "5.7.0 Authentication Required")
///
/// ที่แย่กว่านั้น: คอลัมน์ไม่ว่าง ⇒ หน้าจอเดิมโชว์ "••••••• (เก็บไว้)" ⇒ ผู้ใช้
/// เว้นช่องว่างไว้ตามที่หน้าจอบอก (เว้นว่าง = ใช้ค่าเดิม) ⇒ ค่าเสียไม่เคยถูกเขียนทับ
/// = **แก้ผ่านหน้าเว็บไม่ได้เลยตลอดกาล** ต้องบอกตรง ๆ ว่าให้กรอกใหม่
/// </summary>
public static class SecretWarnings
{
    /// <summary>รอบ 231 (กฎ F2 ข้อ 7 — ข้อความต้องอ้างเหตุที่ตรวจจริง): เหตุที่ค่าใช้ไม่ได้ จาก <see cref="ISecretProtector.Inspect"/> —
    /// "คีย์ถูกเปลี่ยน" พูดได้เฉพาะ <see cref="SecretState.WrongKey"/> (มีคำนำหน้า enc1 · รูปแบบถูก · ถอดไม่ออกทุกคีย์) · ค่าเสียหายบอกว่าเสียหาย ·
    /// ค่าที่ใช้ได้ ⇒ null (ไม่มีอะไรจะฟ้อง) — ก่อนรอบ 231 ทุกทางเข้าเขียน "คีย์ถูกเปลี่ยน" ตายตัว ทั้งที่กรณีจริงคือ token ของ LINE ถูกเก็บดิบเพราะเดารูปผิด</summary>
    public static string? UnusableReason(ISecretProtector secrets, string? stored) => ReasonFor(secrets.Inspect(stored));

    /// <summary>เหตุ "ใช้ไม่ได้" จากสถานะ (ผู้เรียกที่ <c>Inspect</c> ไว้แล้วใช้ตัวนี้ — ไม่ Inspect ซ้ำ · N2)</summary>
    public static string? ReasonFor(SecretState state) => state switch
    {
        SecretState.WrongKey => "คีย์เข้ารหัสของระบบถูกเปลี่ยนหลังจากบันทึกค่านี้ — ค่าอยู่ในรูปแบบ enc1 ที่ถูกต้อง แต่ถอดไม่ออกทั้งคีย์ปัจจุบันและคีย์เดิมทุกตัว",
        SecretState.Malformed => "ค่าที่เก็บไว้เสียหาย — ขึ้นต้นด้วย enc1: แต่เนื้อหาไม่ใช่ base64 หรือสั้นกว่าที่ ciphertext ต้องมี (ไม่ใช่เรื่องคีย์)",
        SecretState.Empty => "ไม่มีค่าเก็บไว้",
        _ => null,
    };

    /// <summary>ฝ่ายค้านรอบ 231 S1 — คำใบ้ (ไม่ใช่ข้อผิดพลาด) สำหรับ <see cref="SecretState.AmbiguousBase64"/>: ค่ายังใช้ได้เป็นค่าดิบ แต่ถ้าเคยเปลี่ยนคีย์ ค่านี้อาจเป็น
    /// ciphertext ของคีย์ที่หายไป ⇒ บอกให้กรอกใหม่ถ้าใช้ไม่ติด · null = ไม่มีคำใบ้</summary>
    private static string? SoftHintFor(SecretState state) => state == SecretState.AmbiguousBase64
        ? "ค่านี้ดูเหมือน ciphertext รุ่นก่อนแต่ถอดไม่ออก — ระบบใช้เป็นค่าดิบตามที่บันทึกไว้ · ถ้าเคยเปลี่ยน ENCRYPTION_KEY หรือใช้งานไม่ติด ให้กรอกใหม่"
        : null;

    private static string? SoftHint(ISecretProtector secrets, string? stored) => SoftHintFor(secrets.Inspect(stored));

    /// <summary>คำใบ้ช่องเดียว "{ชื่อช่อง}: {คำใบ้}" · null = ไม่มี</summary>
    public static string? FieldHint(ISecretProtector secrets, string label, string? stored)
        => SoftHint(secrets, stored) is string h ? $"{label}: {h}" : null;

    /// <summary>คำใบ้รวมของหลายช่อง (หน้าตั้งค่าอีเมลบริษัท/ระบบ) — ข้อความล้วน · null = ไม่มี</summary>
    public static string? BuildHints(ISecretProtector secrets, params (string Label, string? Stored)[] fields)
    {
        var hinted = fields.Where(f => !string.IsNullOrEmpty(f.Stored) && secrets.Inspect(f.Stored) == SecretState.AmbiguousBase64).Select(f => f.Label).ToList();
        return hinted.Count == 0 ? null : $"ℹ️ {string.Join(" · ", hinted)}: {SoftHintFor(SecretState.AmbiguousBase64)}";
    }

    /// <summary>ประโยคเตือนช่องเดียว: "{ชื่อช่อง} ที่บันทึกไว้ถอดรหัสไม่ได้ ({เหตุ}) — กรอกใหม่" · null = ใช้ได้</summary>
    public static string? FieldWarning(ISecretProtector secrets, string label, string? stored)
        => string.IsNullOrEmpty(stored) ? null
            : UnusableReason(secrets, stored) is string why ? $"{label} ที่บันทึกไว้ถอดรหัสไม่ได้ ({why}) — กรอกใหม่" : null;

    /// <param name="fields">คู่ (ชื่อที่ผู้ใช้เข้าใจ, ค่าที่เก็บไว้ในฐาน)</param>
    /// <returns>ข้อความไทยพร้อมแสดง หรือ null เมื่อไม่มีปัญหา</returns>
    public static string? Build(ISecretProtector secrets, params (string Label, string? Stored)[] fields)
    {
        var broken = fields
            .Where(f => !string.IsNullOrEmpty(f.Stored) && !secrets.IsUsable(f.Stored))
            .ToList();
        if (broken.Count == 0) return null;
        var reasons = broken.Select(f => UnusableReason(secrets, f.Stored) ?? "ถอดรหัสแล้วได้ค่าว่าง").Distinct().ToList();

        // ข้อความนี้ถูก escape ก่อนแสดงบนหน้าเว็บ — เขียนเป็นข้อความล้วน ไม่ใส่ markup
        return $"⚠️ {string.Join(" · ", broken.Select(f => f.Label))} ที่บันทึกไว้ถอดรหัสไม่ได้ "
             + $"({string.Join(" · ", reasons)}) "
             + "ระบบจึงส่งอีเมลไม่ผ่าน และเซิร์ฟเวอร์ปลายทางจะตอบว่า Authentication Required "
             + "— โปรดกรอกค่าใหม่ในช่องด้านล่างแล้วกดบันทึกอีกครั้ง "
             + "(เว้นช่องว่างไว้จะยังใช้ค่าเดิมที่เสียอยู่)";
    }
}

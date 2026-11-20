namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก A (T4 M-5 · S1–S4) — <b>ด่านเดียวของ "เอาความลับที่เก็บไว้ไปใช้จริง"</b> (เซ็น webhook · ตรวจลายเซ็นขาเข้า · ยืนยันตัวตนกับ SMTP/OAuth/ธนาคาร/
/// ผู้ให้บริการชำระเงิน · รหัสใบรับรอง e-Tax)
///
/// <para><b>ที่มา:</b> <see cref="ISecretProtector.Unprotect"/> คืน <c>""</c> เมื่อถอดไม่ออก (กัน ciphertext หลุด) — ผู้อ่านที่ไม่ถาม <c>IsUsable</c> ก่อนจึงเอาค่าว่าง
/// ไปใช้เงียบ ๆ: webhook ขาออก<b>ส่งโดยไม่มีลายเซ็น</b> · webhook CMS ขาเข้า<b>ตรวจ HMAC ด้วยกุญแจว่าง</b> · e-Tax เซ็นด้วยรหัสว่าง · Facebook login
/// <b>ข้ามการตรวจ app id</b> · Omise บอก "ยังไม่ได้ตั้งค่า" ทั้งที่ตั้งแล้วแต่คีย์ถูกหมุน</para>
///
/// <para><b>กติกา:</b> ว่าง = "ยังไม่ได้ตั้งค่า" (ผู้เรียกตัดสินว่าบังคับไหม) · ถอดไม่ออก (WrongKey/Malformed) = <b>ใช้ไม่ได้พร้อมเหตุจริง</b>
/// (<see cref="SecretWarnings.ReasonFor"/>) ห้ามถอยเป็นค่าว่าง/ค่า config เงียบ ๆ · ใช้ได้ = ค่าที่ถอดแล้ว · ข้อความถึงผู้ใช้ไม่ชี้ชื่อ env/URL (M-1)
/// ล็อกจุดเรียกด้วย <c>tools/required_call_site_check.py</c> (<c>_R275A_</c>)</para>
/// </summary>
public static class SecretUse
{
    /// <summary>รหัสกฎใน audit/คำตอบ — ความลับที่เก็บไว้ใช้ไม่ได้</summary>
    public const string RuleCode = "SEC-SECRET-UNUSABLE";

    /// <summary>ผลการเปิดใช้ความลับหนึ่งค่า · <see cref="Value"/> มีค่าเฉพาะเมื่อใช้ได้ · <see cref="Problem"/> = ข้อความไทยพร้อมแสดง (null = ใช้ได้/ไม่ได้ตั้ง)</summary>
    public sealed record Opened(string? Value, SecretState State, string? Problem)
    {
        /// <summary>มีความลับที่ใช้ได้จริง</summary>
        public bool Ok => !string.IsNullOrEmpty(Value);
        /// <summary>ไม่ได้ตั้งค่า (ไม่ใช่ข้อผิดพลาด — ผู้เรียกที่ความลับเป็นทางเลือกใช้ทางไม่มีความลับตามปกติ)</summary>
        public bool NotConfigured => State == SecretState.Empty;
        /// <summary>ตั้งไว้แต่ใช้ไม่ได้ — ต้องล้มดัง ห้ามทำต่อเหมือนไม่ได้ตั้ง</summary>
        public bool Unusable => !Ok && !NotConfigured;
    }

    /// <summary>ข้อความเมื่อตั้งไว้แต่ใช้ไม่ได้ — ตัวประกอบเดียว (ผู้เรียกทุกตัวและหน้า "สุขภาพข้อมูลเข้ารหัส" ใช้ประโยคเดียวกัน)</summary>
    public static string UnusableText(string label, SecretState state)
    {
        var l = label.Trim();
        return $"{l} ที่บันทึกไว้ใช้ไม่ได้ ({SecretWarnings.ReasonFor(state) ?? "ถอดรหัสไม่ได้"}) — กรอก{l}ใหม่ในหน้าตั้งค่า "
               + "หรือแจ้งผู้ดูแลระบบให้ตรวจคีย์เข้ารหัส (ดู " + EncryptionHealthText.PageName + ")";
    }

    /// <summary>ข้อความเมื่อยังไม่ได้ตั้งค่า (ป้ายที่ขึ้นต้นด้วยช่องว่างคงไว้ — "ยังไม่ได้ตั้งค่า secret key …")</summary>
    public static string NotConfiguredText(string label) => $"ยังไม่ได้ตั้งค่า{label}";

    /// <summary>เปิดใช้ความลับ — ตัดสินด้วย <see cref="ISecretProtector.Inspect"/> ครั้งเดียว แล้วถอดเฉพาะสถานะที่ใช้ได้</summary>
    public static Opened Open(ISecretProtector secrets, string label, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return new(null, SecretState.Empty, null);
        var state = secrets.Inspect(stored);
        if (state is SecretState.Empty) return new(null, SecretState.Empty, null);
        if (state is SecretState.WrongKey or SecretState.Malformed) return new(null, state, UnusableText(label, state));
        var plain = secrets.Unprotect(stored);
        // ใช้ได้ตามสถานะแต่ถอดได้ค่าว่าง (ตัวแทนในเทสต์/ค่าเสีย) = ใช้ไม่ได้ ไม่ใช่ "ไม่ได้ตั้ง"
        return string.IsNullOrEmpty(plain)
            ? new(null, SecretState.Malformed, UnusableText(label, SecretState.Malformed))
            : new(plain, state, null);
    }

    /// <summary>ความลับที่<b>ต้องมี</b> — ไม่ได้ตั้ง/ใช้ไม่ได้ ⇒ <see cref="BusinessRuleException"/> (422) พร้อมเหตุจริง</summary>
    public static string Require(ISecretProtector secrets, string label, string? stored)
    {
        var o = Open(secrets, label, stored);
        if (o.Ok) return o.Value!;
        throw new BusinessRuleException(o.Problem ?? NotConfiguredText(label), RuleCode, 422);
    }

    /// <summary>ความลับที่<b>เป็นทางเลือก</b> — ไม่ได้ตั้ง ⇒ null (ทางไม่มีความลับตามปกติ) · ตั้งไว้แต่ใช้ไม่ได้ ⇒ throw (ห้ามทำเหมือนไม่ได้ตั้ง:
    /// webhook ที่ผู้ใช้ตั้ง secret ไว้ต้องไม่ถูกส่งแบบไม่เซ็น)</summary>
    public static string? Optional(ISecretProtector secrets, string label, string? stored)
    {
        var o = Open(secrets, label, stored);
        if (o.Ok) return o.Value;
        if (o.NotConfigured) return null;
        throw new BusinessRuleException(o.Problem!, RuleCode, 422);
    }
}

/// <summary>
/// รอบ 275 แพ็ก A (T4 M-1) — ข้อความ "ไปดูที่ไหน" ของเรื่องคีย์เข้ารหัส <b>ตัวเดียว</b>: ผู้ใช้ของบริษัทเรียก API ดิบไม่ได้และตั้ง env ของแพลตฟอร์มไม่ได้ ⇒
/// ข้อความถึงผู้ใช้ชี้ไปที่หน้าจอ ส่วนชื่อ env อยู่ใน <see cref="AdminKeyAdvice"/> (log / หน้าผู้ดูแลแพลตฟอร์ม / runbook) เท่านั้น
/// </summary>
public static class EncryptionHealthText
{
    /// <summary>ชื่อหน้าที่ผู้ใช้เห็นในเมนู</summary>
    public const string PageName = "ตั้งค่า › สุขภาพข้อมูลเข้ารหัส";
    /// <summary>ที่อยู่หน้า (ลิงก์ในหน้าเว็บ)</summary>
    public const string PageUrl = "/pages/encryption-health.html";

    /// <summary>ทางไปต่อของผู้ใช้บริษัทเมื่อค่าที่เข้ารหัสถอดไม่ได้ — ไม่มีชื่อ env/URL ดิบ</summary>
    public const string TenantAdvice = "กรอกค่าใหม่ หรือแจ้งผู้ดูแลระบบ (คีย์เข้ารหัสของระบบอาจถูกเปลี่ยน) — ดูรายการทั้งหมดที่ " + PageName;

    /// <summary>ทางไปต่อของผู้ดูแลแพลตฟอร์ม (log · JobRunLog · หน้าผู้ดูแล) — ชื่อ env ได้ · ขั้นตอนเต็มใน <c>docs/runbooks/encryption-key-rotation.md</c></summary>
    public static string AdminKeyAdvice
        => $"ตั้ง {EncryptionKeyResolver.PreviousKeyEnv} (หรือ {EncryptionKeyResolver.PreviousKeyEnv}_2…_{EncryptionKeyResolver.MaxNumberedPreviousKeys}) "
           + "เป็นคีย์ที่ใช้ตอนบันทึกแล้วรีสตาร์ต — ขั้นตอนเต็ม: docs/runbooks/encryption-key-rotation.md";
}

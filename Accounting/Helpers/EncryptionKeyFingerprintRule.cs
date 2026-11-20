namespace Accounting.Helpers;

/// <summary>ผลเทียบลายนิ้วมือคีย์ของเครื่องนี้กับที่บันทึกในฐาน (<c>SiteSettings.EncryptionKeyFingerprint</c>) — ส่งออกเป็น "ชื่อ" เสมอ (F2 ข้อ 5)</summary>
public enum KeyFingerprintVerdict
{
    /// <summary>ตรวจไม่ได้ (ไม่มีแถวค่าตั้งระบบ · ตัวแทนในเทสต์ไม่มีลายนิ้วมือ)</summary>
    NotChecked,
    /// <summary>ฐานยังไม่เคยบันทึก ⇒ บันทึกคีย์ของเครื่องนี้</summary>
    FirstSeen,
    /// <summary>ตรงกัน — สภาพปกติ</summary>
    Match,
    /// <summary>ฐานบันทึกคีย์ที่อยู่ในรายการคีย์เดิมของเครื่องนี้ ⇒ หมุนคีย์ตามขั้นตอน (บันทึกคีย์ใหม่ + audit)</summary>
    Rotated,
    /// <summary>คีย์ของเครื่องนี้คือคีย์ที่ฐาน<b>เพิ่งหมุนออกไป</b> ⇒ น่าจะมีเครื่องที่ยังใช้คีย์เก่า (ห้ามย้ายแถวกลับ — กันชักเย่อ)</summary>
    RotatedBack,
    /// <summary>ฐานบันทึกคีย์ที่เครื่องนี้ไม่รู้จัก ⇒ อีกเครื่องใช้คีย์อื่น / restore ฐานจากที่อื่น / ตั้งคีย์ผิด</summary>
    Mismatch,
}

/// <summary>
/// รอบ 275 แพ็ก A (T4 M-4 · K3/K6/K9) — <b>ตัวตัดสินเดียว</b>ว่าคีย์เข้ารหัสของเครื่องนี้ตรงกับที่ระบบใช้อยู่ไหม
///
/// <para><b>ที่มา:</b> สองเครื่องคนละคีย์ (หรือเครื่องหนึ่งตั้ง PREVIOUS เป็นคีย์ของอีกเครื่อง) ไม่มีอะไรรู้ตัว ⇒ งานย้ายคีย์ของแต่ละเครื่องย้ายแถวไปคีย์ตัวเอง
/// ⇒ อีกเครื่องอ่านไม่ได้ · สลับกันไปมา · ตอนนี้: ฐานเก็บลายนิ้วมือ (<see cref="EncryptionKeyResolver.Fingerprint"/>) ของคีย์ที่ใช้อยู่ ·
/// งานย้ายคีย์ (PII + ความลับ) รันได้เฉพาะ <see cref="AllowsRekey"/> · ไม่ตรง ⇒ LogError + ป้ายบนหน้า "สุขภาพข้อมูลเข้ารหัส" + ผู้ดูแลแพลตฟอร์มกด "ยืนยันคีย์ปัจจุบัน"</para>
/// </summary>
public static class EncryptionKeyFingerprintRule
{
    public const string RuleCode = "SEC-KEY-FINGERPRINT";

    /// <param name="stored">ลายนิ้วมือที่ฐานบันทึก (null = ยังไม่เคย)</param>
    /// <param name="storedPrevious">ลายนิ้วมือก่อนการหมุนครั้งล่าสุด (กันชักเย่อ)</param>
    /// <param name="current">ของเครื่องนี้ (null = ตรวจไม่ได้)</param>
    /// <param name="previous">คีย์เดิมในห่วงโซ่ถอดของเครื่องนี้</param>
    public static KeyFingerprintVerdict Evaluate(string? stored, string? storedPrevious, string? current, IReadOnlyCollection<string> previous)
    {
        if (string.IsNullOrEmpty(current)) return KeyFingerprintVerdict.NotChecked;
        if (string.IsNullOrEmpty(stored)) return KeyFingerprintVerdict.FirstSeen;
        if (stored == current) return KeyFingerprintVerdict.Match;
        if (!string.IsNullOrEmpty(storedPrevious) && storedPrevious == current) return KeyFingerprintVerdict.RotatedBack;
        return previous.Contains(stored) ? KeyFingerprintVerdict.Rotated : KeyFingerprintVerdict.Mismatch;
    }

    /// <summary>งานย้ายคีย์ (เขียนแถวด้วยคีย์ของเครื่องนี้) ทำได้ไหม — ไม่ตรง ⇒ ห้าม (จะย้ายแถวไปคีย์ที่อีกเครื่องอ่านไม่ได้)</summary>
    public static bool AllowsRekey(KeyFingerprintVerdict v)
        => v is KeyFingerprintVerdict.FirstSeen or KeyFingerprintVerdict.Match or KeyFingerprintVerdict.Rotated or KeyFingerprintVerdict.NotChecked;

    /// <summary>ต้องบันทึกลายนิ้วมือของเครื่องนี้ลงฐาน</summary>
    public static bool ShouldStore(KeyFingerprintVerdict v) => v is KeyFingerprintVerdict.FirstSeen or KeyFingerprintVerdict.Rotated;

    /// <summary>ปัญหาที่ต้องให้คนตัดสิน</summary>
    public static bool IsProblem(KeyFingerprintVerdict v) => v is KeyFingerprintVerdict.Mismatch or KeyFingerprintVerdict.RotatedBack;

    /// <summary>ข้อความถึงผู้ดูแลแพลตฟอร์ม (ชื่อ env ได้) · null = ไม่มีอะไรต้องทำ</summary>
    public static string? AdminMessage(KeyFingerprintVerdict v) => v switch
    {
        KeyFingerprintVerdict.Mismatch =>
            "⛔ คีย์เข้ารหัสของเครื่องนี้ไม่ตรงกับคีย์ที่ระบบบันทึกไว้ และไม่อยู่ในรายการคีย์เดิม — อาจมีหลายเครื่องใช้ ENCRYPTION_KEY ต่างกัน หรือ restore ฐานจากที่อื่น · "
            + "งานย้ายคีย์หยุดไว้ (กันย้ายข้อมูลไปคีย์ที่เครื่องอื่นอ่านไม่ได้) · ตรวจ ENCRYPTION_KEY ทุกเครื่องให้ตรงกัน แล้วกด \"ยืนยันคีย์ปัจจุบัน\" เมื่อแน่ใจ",
        KeyFingerprintVerdict.RotatedBack =>
            "⛔ เครื่องนี้ใช้คีย์ที่ระบบเพิ่งหมุนออกไป — น่าจะมีเครื่องที่ยังไม่ได้ตั้ง ENCRYPTION_KEY ใหม่ · งานย้ายคีย์หยุดไว้ (กันย้ายข้อมูลกลับไปคีย์เก่า) · "
            + "ตั้งคีย์ใหม่ให้ทุกเครื่อง หรือกด \"ยืนยันคีย์ปัจจุบัน\" ถ้าตั้งใจย้อนกลับ",
        _ => null,
    };

    /// <summary>ข้อความถึงผู้ใช้ของบริษัท (ไม่มีชื่อ env) · null = ไม่มีอะไรต้องบอก</summary>
    public static string? TenantMessage(KeyFingerprintVerdict v) => IsProblem(v)
        ? "⚠️ ระบบตรวจพบว่าเครื่องให้บริการใช้คีย์เข้ารหัสไม่ตรงกัน — ข้อมูลที่เข้ารหัสบางรายการอาจแสดงว่า \"ถอดรหัสไม่ได้\" ชั่วคราว · ผู้ดูแลระบบได้รับแจ้งแล้ว (ยังไม่ต้องกรอกใหม่)"
        : null;
}

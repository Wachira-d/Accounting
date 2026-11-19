namespace Accounting.Helpers;

/// <summary>
/// **แหล่งคีย์เข้ารหัสตัวเดียว** ของคอลัมน์ PII (<c>EncryptedColumnConverter</c> ผ่าน <c>Program.cs</c>) และความลับที่เก็บใน DB (<c>SecretProtector</c>)
/// (รอบ 219 เฟส 0 · PLATFORM_GATEWAY_REFUND_POLICY_DESIGN P0-4 · กฎเหล็ก #4 C)
///
/// <para><b>ที่มา:</b> <c>Program.cs</c> อ่าน <c>ENCRYPTION_KEY</c> (env) ก่อน <c>Security:EncryptionKey</c> และ fail-fast ใน production
/// แต่ <c>SecretProtector</c> อ่าน <c>Security:EncryptionKey</c> อย่างเดียว แล้ว<b>ตกไปใช้ dev key ใน source พร้อม LogWarning</b>
/// ⇒ production ที่ตั้งแค่ <c>ENCRYPTION_KEY</c> เข้ารหัสคีย์ลับ Omise/SMTP ด้วยกุญแจสาธารณะ</para>
///
/// <para><b>ฝ่ายค้านรอบ 219 B2/S3:</b> การเปลี่ยนแหล่งคีย์ทำให้ค่าที่เคยเข้ารหัสด้วยคีย์เดิม (config เดิม หรือ dev key) ถอดไม่ออก ⇒
/// <see cref="LegacyCandidates"/> = คีย์เดิมที่ต้องลองถอดต่อ (dev key เป็นสาธารณะอยู่แล้ว ⇒ ไม่เสียความปลอดภัย) แล้ว <c>SecretReprotectJob</c>
/// เข้ารหัสใหม่ด้วยคีย์ปัจจุบัน · <b>ห้าม Trim คีย์ที่ส่งเข้า PBKDF2</b> — ตัดช่องว่างใช้แค่ตรวจว่า "ว่าง" (คีย์เดิมอาจมีช่องว่างท้ายจาก env)</para>
/// </summary>
public static class EncryptionKeyResolver
{
    /// <summary>คีย์ dev ที่อยู่ใน source — ห้ามใช้ใน production (ค่าเดียวกับ <c>EncryptionHelper</c>/<c>SecretProtector</c> เดิม)</summary>
    public const string DevKey = "default-dev-key-change-in-production";

    /// <summary>env <c>ENCRYPTION_KEY</c> ชนะ config <c>Security:EncryptionKey</c> · ว่าง/ช่องว่างล้วน = ไม่มี (null) · <b>คืนค่าดิบไม่ Trim</b> (S3)</summary>
    public static string? Resolve(string? envKey, string? configKey)
        => !string.IsNullOrWhiteSpace(envKey) ? envKey : !string.IsNullOrWhiteSpace(configKey) ? configKey : null;

    /// <summary>ฝ่ายค้านรอบ 219 รอบ 2: คีย์ปัจจุบันจาก env <c>ENCRYPTION_KEY</c> แล้ว config — ไม่มี = dev key (เครื่องนักพัฒนา) ·
    /// production ไม่มีทางได้ dev key เพราะ <c>Program.cs</c> fail-fast ตอนบูตด้วย <see cref="RequireUsable"/> แล้ว ·
    /// ใช้กับผู้เขียนความลับที่ไม่ได้ถือ <c>ISecretProtector</c> (เช่น <c>SitePaymentGateway.ApiKeyEncrypted</c>) ⇒ คีย์เดียวกับ SecretProtector</summary>
    public static string CurrentOrDev(string? configKey)
        => Resolve(Environment.GetEnvironmentVariable("ENCRYPTION_KEY"), configKey) ?? DevKey;

    /// <summary>รอบ 273: env ของ<b>คีย์เดิม</b> (ใช้ถอดอย่างเดียว) หลังหมุน <c>ENCRYPTION_KEY</c> — ตั้งเป็นคีย์ก่อนหน้าแล้วรีสตาร์ต ⇒
    /// คอลัมน์ PII ที่เข้ารหัสด้วยคีย์นั้นอ่านได้ทันที และ <c>PiiReprotectJob</c> ย้ายไปคีย์ปัจจุบัน (เสร็จแล้วถอดออกได้)</summary>
    public const string PreviousKeyEnv = "ENCRYPTION_KEY_PREVIOUS";

    /// <summary>ค่า placeholder ที่รู้กันทั้งโลก — ตรงทั้งค่า (ไม่สนตัวพิมพ์/ช่องว่างหัวท้าย) · ไม่ใช้เกณฑ์ความยาว (คีย์จริงที่สั้นของระบบที่ใช้อยู่ห้ามทำให้บูตไม่ขึ้น
    /// — ข้อมูล PII เดิมผูกกับคีย์นั้น)</summary>
    private static readonly string[] Placeholders =
    {
        "changeme", "change_me", "change-me", "secret", "your-secret-key", "your-encryption-key", "encryption-key",
        "CHANGE_THIS_TO_A_STRONG_SECRET_KEY_AT_LEAST_32_CHARS!!", "<set-in-env>", "placeholder", "xxx",
    };

    /// <summary>คีย์นี้ใช้ไม่ได้ใน production (ว่าง หรือเป็น dev key)</summary>
    private static bool IsUnsafe(string? key) => string.IsNullOrWhiteSpace(key) || key.Trim() == DevKey;

    /// <summary>คีย์นี้เป็น placeholder (production ปฏิเสธ · เครื่องนักพัฒนาใช้ตามเดิม)</summary>
    public static bool IsPlaceholder(string? key)
        => !string.IsNullOrWhiteSpace(key) && Placeholders.Any(p => string.Equals(p, key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>รอบ 273: คีย์ที่<b>คอลัมน์ PII</b> (<c>EncryptedColumnConverter</c>) ใช้ลองถอดต่อจากคีย์ปัจจุบัน — <see cref="PreviousKeyEnv"/> (ดิบ + ตัดช่องว่าง) ก่อน
    /// แล้ว <see cref="LegacyCandidates"/> (config เดิมที่ถูก env ชนะ · dev key ก่อนตั้งคีย์) · ไม่มีคีย์ปัจจุบันซ้ำ · ลำดับคงที่ ·
    /// ใช้ถอดอย่างเดียว (เขียนด้วยคีย์ปัจจุบันเสมอ) ⇒ dev key ในรายการไม่ลดความปลอดภัย (ค่าที่เข้ารหัสด้วยมันเปิดได้อยู่แล้ว)</summary>
    public static IReadOnlyList<string> PiiPreviousKeys(string currentKey, string? previousEnv, string? envKey, string? configKey)
    {
        var list = new List<string>();
        void Add(string? k)
        {
            if (string.IsNullOrWhiteSpace(k) || k == currentKey || list.Contains(k)) return;
            list.Add(k);
        }
        Add(previousEnv);
        Add(previousEnv?.Trim());
        foreach (var k in LegacyCandidates(currentKey, envKey, configKey)) Add(k);
        return list;
    }

    /// <summary>production + คีย์ไม่ปลอดภัย ⇒ throw (boot ไม่ขึ้น) · อื่น ๆ คืนคีย์ที่จะใช้ (ไม่มี = dev key สำหรับเครื่องนักพัฒนา) · ค่าดิบไม่ Trim</summary>
    public static string RequireUsable(string? key, bool isProduction, string usage)
    {
        if (IsUnsafe(key))
        {
            if (isProduction)
                throw new InvalidOperationException(
                    $"ENCRYPTION_KEY (หรือ Security:EncryptionKey) ต้องตั้งค่าใน production และห้ามเป็น dev key — ใช้กับ{usage} "
                    + "(กฎเหล็ก #4 C: ห้าม fallback dev key เงียบ ๆ)");
            return DevKey;
        }
        if (isProduction && IsPlaceholder(key))
            throw new InvalidOperationException(
                $"ENCRYPTION_KEY (หรือ Security:EncryptionKey) เป็นค่า placeholder — ตั้งคีย์สุ่มจริงใน production (ใช้กับ{usage} · กฎเหล็ก #4 C)");
        return key!;
    }

    /// <summary>ฝ่ายค้านรอบ 219 B2/S3: คีย์ "เดิม" ที่ความลับในฐานอาจถูกเข้ารหัสไว้ — <c>SecretProtector</c> รุ่นก่อนรอบ 219 ใช้
    /// <c>Security:EncryptionKey ?? DevKey</c> (ไม่อ่าน env) ⇒ ลอง config (ดิบ + ตัดช่องว่าง) · env (ตัดช่องว่าง) · dev key ·
    /// ตัดคีย์ปัจจุบันและค่าซ้ำออก · ลำดับคงที่ (ทดสอบได้)</summary>
    public static IReadOnlyList<string> LegacyCandidates(string currentKey, string? envKey, string? configKey)
    {
        var list = new List<string>();
        void Add(string? k)
        {
            if (string.IsNullOrEmpty(k) || k == currentKey || list.Contains(k)) return;
            list.Add(k);
        }
        Add(configKey);
        Add(configKey?.Trim());
        Add(envKey);
        Add(envKey?.Trim());
        Add(DevKey);
        return list;
    }
}

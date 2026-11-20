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

    /// <summary>รอบ 274: env ของ<b>คีย์เดิม</b> (ใช้ถอดอย่างเดียว) หลังหมุน <c>ENCRYPTION_KEY</c> — ตั้งเป็นคีย์ก่อนหน้าแล้วรีสตาร์ต ⇒
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

    /// <summary>รอบ 274: คีย์ที่<b>คอลัมน์ PII</b> (<c>EncryptedColumnConverter</c>) ใช้ลองถอดต่อจากคีย์ปัจจุบัน — <see cref="PreviousKeyEnv"/> (ดิบ + ตัดช่องว่าง) ก่อน
    /// แล้ว <see cref="LegacyCandidates"/> (config เดิมที่ถูก env ชนะ · dev key ก่อนตั้งคีย์) · ไม่มีคีย์ปัจจุบันซ้ำ · ลำดับคงที่ ·
    /// ใช้ถอดอย่างเดียว (เขียนด้วยคีย์ปัจจุบันเสมอ) ⇒ dev key ในรายการไม่ลดความปลอดภัย (ค่าที่เข้ารหัสด้วยมันเปิดได้อยู่แล้ว)</summary>
    public static IReadOnlyList<string> PiiPreviousKeys(string currentKey, string? previousEnv, string? envKey, string? configKey)
        => PreviousKeysFrom(currentKey, new[] { previousEnv }, envKey, configKey);

    /// <summary>รอบ 275 แพ็ก A (T4 M-4 · K3/K9): คีย์เดิม<b>หลายตัว</b> — แต่ละค่าลองทั้งก้อน (ดิบ + ตัดช่องว่าง) แล้วแยกทีละบรรทัด
    /// (ตัวคั่น = ขึ้นบรรทัดใหม่ — ไม่ชน base64/ตัวอักษรของคีย์) · ลำดับคงที่ตามที่ส่งเข้า · ตัดคีย์ปัจจุบัน/ค่าซ้ำ/ค่าว่าง</summary>
    public static IReadOnlyList<string> PreviousKeysFrom(string currentKey, IEnumerable<string?> previousInputs, string? envKey, string? configKey)
    {
        var list = new List<string>();
        void Add(string? k)
        {
            if (string.IsNullOrWhiteSpace(k) || k == currentKey || list.Contains(k)) return;
            list.Add(k);
        }
        foreach (var raw in previousInputs)
        {
            Add(raw);
            Add(raw?.Trim());
            if (raw == null || raw.IndexOfAny(new[] { '\n', '\r' }) < 0) continue;
            foreach (var line in raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Add(line);
                Add(line.Trim());
            }
        }
        foreach (var k in LegacyCandidates(currentKey, envKey, configKey)) Add(k);
        return list;
    }

    /// <summary>รอบ 275 แพ็ก A: env คีย์เดิมแบบมีเลขกำกับ <c>ENCRYPTION_KEY_PREVIOUS_2</c> … <c>_9</c> (หมุนหลายครั้งโดยงานย้ายยังไม่จบ · restore ฐานเก่า)</summary>
    public const int MaxNumberedPreviousKeys = 9;

    /// <summary>รอบ 275 แพ็ก A (T4 H-1): <b>ห่วงโซ่คีย์ถอดตัวเดียว</b> ของทั้งคอลัมน์ PII (<c>EncryptedColumnConverter</c> ผ่าน <c>Program.cs</c>)
    /// และความลับใน DB (<c>SecretProtector</c>) — เดิม SecretProtector ใช้แค่ <see cref="LegacyCandidates"/> (ไม่อ่าน <see cref="PreviousKeyEnv"/>) ⇒
    /// หมุนคีย์ตามคู่มือรอบ 274 แล้ว PII กลับมาอ่านได้ แต่ SMTP · LINE · Omise · e-Tax · webhook ทั้งชุดเป็น WrongKey ·
    /// ลำดับ: <see cref="PreviousKeyEnv"/> (หรือ config <c>Security:EncryptionKeyPrevious</c> เมื่อไม่มี env) → <c>_2</c>…<c>_9</c> → config เดิม/env ตัดช่องว่าง/dev key ·
    /// ใช้ถอดอย่างเดียว (เขียนด้วยคีย์ปัจจุบันเสมอ)</summary>
    /// <param name="env">ตัวอ่าน env (production = <see cref="Environment.GetEnvironmentVariable(string)"/> · เทสต์ส่งพจนานุกรม)</param>
    public static IReadOnlyList<string> DecryptKeyChain(string currentKey, Func<string, string?> env, string? configKey, string? configPreviousKey)
    {
        var inputs = new List<string?> { env(PreviousKeyEnv) ?? configPreviousKey };
        for (var i = 2; i <= MaxNumberedPreviousKeys; i++) inputs.Add(env($"{PreviousKeyEnv}_{i}"));
        return PreviousKeysFrom(currentKey, inputs, env("ENCRYPTION_KEY"), configKey);
    }

    /// <summary>รอบ 275 แพ็ก A (T4 M-4): "ลายนิ้วมือ" ของคีย์ — HMAC-SHA256 ของค่าคงที่ด้วยคีย์ (ตัด 16 ตัวอักษรฐานสิบหก) · ย้อนหาคีย์ไม่ได้ ·
    /// ใช้ตรวจว่าทุกเครื่องใช้คีย์เดียวกัน (<c>EncryptionKeyFingerprintRule</c>) · ห้ามคำนวณที่อื่น (ฝั่งเขียน = ฝั่งตรวจ)</summary>
    public static string Fingerprint(string key)
    {
        using var h = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes("nextacc/encryption-key-fingerprint/v1")))[..16].ToLowerInvariant();
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

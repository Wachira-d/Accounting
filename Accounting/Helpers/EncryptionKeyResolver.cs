namespace Accounting.Helpers;

/// <summary>
/// **แหล่งคีย์เข้ารหัสตัวเดียว** ของทั้งคอลัมน์ PII (<c>EncryptedColumnConverter</c>) และความลับที่เก็บใน DB (<c>SecretProtector</c>)
/// (รอบ 219 เฟส 0 · PLATFORM_GATEWAY_REFUND_POLICY_DESIGN P0-4 · กฎเหล็ก #4 C)
///
/// <para><b>ที่มา:</b> <c>Program.cs</c> อ่าน <c>ENCRYPTION_KEY</c> (env) ก่อน <c>Security:EncryptionKey</c> และ fail-fast ใน production
/// แต่ <c>SecretProtector</c> อ่าน <c>Security:EncryptionKey</c> อย่างเดียว แล้ว<b>ตกไปใช้ dev key ใน source พร้อม LogWarning</b>
/// ⇒ production ที่ตั้งแค่ <c>ENCRYPTION_KEY</c> เข้ารหัสคีย์ลับ Omise/SMTP ด้วยกุญแจสาธารณะ · ตอนนี้ทั้งสองถามที่นี่</para>
/// </summary>
public static class EncryptionKeyResolver
{
    /// <summary>คีย์ dev ที่อยู่ใน source — ห้ามใช้ใน production (ค่าเดียวกับ <c>EncryptionHelper</c>/<c>SecretProtector</c> เดิม)</summary>
    public const string DevKey = "default-dev-key-change-in-production";

    /// <summary>env <c>ENCRYPTION_KEY</c> ชนะ config <c>Security:EncryptionKey</c> · ว่าง/ช่องว่าง = null</summary>
    public static string? Resolve(string? envKey, string? configKey)
    {
        var k = !string.IsNullOrWhiteSpace(envKey) ? envKey : configKey;
        return string.IsNullOrWhiteSpace(k) ? null : k.Trim();
    }

    /// <summary>คีย์นี้ใช้ไม่ได้ใน production (ว่าง หรือเป็น dev key)</summary>
    private static bool IsUnsafe(string? key) => string.IsNullOrWhiteSpace(key) || key.Trim() == DevKey;

    /// <summary>production + คีย์ไม่ปลอดภัย ⇒ throw (boot ไม่ขึ้น) · อื่น ๆ คืนคีย์ที่จะใช้ (ไม่มี = dev key สำหรับเครื่องนักพัฒนา)</summary>
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
        return key!.Trim();
    }
}

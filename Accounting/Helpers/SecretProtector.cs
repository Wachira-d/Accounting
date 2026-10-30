using Microsoft.Extensions.Hosting;

namespace Accounting.Helpers;

/// <summary>
/// Wraps <see cref="EncryptionHelper"/> with the configured key from
/// <c>Security:EncryptionKey</c> so call-sites don't have to know about
/// the key. Use for sensitive fields stored in DB (SMTP password, OAuth
/// client secrets, refresh tokens, RD API secret, etc).
///
/// Transparent over legacy plaintext: <see cref="Unprotect"/> returns the
/// input unchanged if it doesn't look like our ciphertext, so old rows
/// keep working until the next save re-writes them encrypted.
/// </summary>
public interface ISecretProtector
{
    string? Protect(string? plaintext);
    string? Unprotect(string? value);

    /// <summary>
    /// true = มีความลับที่ **ถอดกลับมาใช้ได้จริง** อยู่ในค่านี้
    ///
    /// ต่างจาก <c>!string.IsNullOrEmpty(value)</c> ตรงที่ค่าซึ่งเป็น ciphertext
    /// แต่ถอดไม่ออก (คีย์ <c>Security:EncryptionKey</c> เปลี่ยนหลังจากบันทึกไว้)
    /// จะคืน false — ไม่งั้นหน้าจอจะบอกว่า "มีรหัสผ่านเก็บไว้แล้ว" ผู้ใช้จึงเว้นช่อง
    /// ว่างไว้ (= ใช้ค่าเดิม) แล้วระบบส่งอีเมลด้วยรหัสผ่านว่าง ⇒ เซิร์ฟเวอร์ตอบ
    /// "5.7.0 Authentication Required" วนแบบนี้ตลอดกาลโดยแก้ผ่านหน้าเว็บไม่ได้เลย
    /// </summary>
    bool IsUsable(string? value);
}

public class SecretProtector : ISecretProtector
{
    private const string DevKey = EncryptionKeyResolver.DevKey;

    private readonly string _key;
    private readonly ILogger<SecretProtector>? _logger;

    /// <summary>รอบ 219 เฟส 0 (P0-4): คีย์มาจาก <see cref="EncryptionKeyResolver"/> ตัวเดียวกับคอลัมน์ PII ใน <c>Program.cs</c>
    /// (env <c>ENCRYPTION_KEY</c> ชนะ <c>Security:EncryptionKey</c>) · production + ไม่มีคีย์/เป็น dev key ⇒ throw ตอนสร้าง (boot ไม่ขึ้น) ·
    /// เดิมอ่าน config อย่างเดียวแล้วตกไป dev key พร้อมแค่ LogWarning</summary>
    public SecretProtector(IConfiguration config, IHostEnvironment env, ILogger<SecretProtector> logger)
        : this(EncryptionKeyResolver.RequireUsable(
            EncryptionKeyResolver.Resolve(Environment.GetEnvironmentVariable("ENCRYPTION_KEY"), config["Security:EncryptionKey"]),
            env.IsProduction(), "ความลับที่เก็บในฐานข้อมูล (คีย์ gateway/SMTP/OAuth)"), logger)
    {
    }

    /// <summary>รับคีย์ตรง ๆ — ใช้ในเทสต์ (จำลองการ "เปลี่ยนคีย์" ได้โดยไม่ต้องยก
    /// IConfiguration/ILogger ทั้งชุดมา) และเผื่อ call site ที่ถือคีย์อยู่แล้ว</summary>
    public SecretProtector(string key, ILogger<SecretProtector>? logger = null)
    {
        _key = string.IsNullOrEmpty(key) ? DevKey : key;
        _logger = logger;
        if (_key == DevKey)
            _logger?.LogWarning("Security:EncryptionKey not configured — using the development default. Set ENCRYPTION_KEY before going to production.");
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        // Idempotent: don't double-encrypt
        if (EncryptionHelper.IsEncrypted(plaintext)) return plaintext;
        return EncryptionHelper.Encrypt(plaintext, _key);
    }

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // Legacy plaintext rows — return as-is so the app keeps working
        // until the next save migrates the value to ciphertext.
        if (!EncryptionHelper.IsEncrypted(value)) return value;
        try { return EncryptionHelper.Decrypt(value, _key); }
        catch (Exception ex)
        {
            // ถอดไม่ออก = ค่านี้ถูกเข้ารหัสด้วยคีย์คนละตัว (Security:EncryptionKey
            // ถูกเปลี่ยนหลังจากบันทึก). คืนค่าว่างเพื่อไม่ให้ ciphertext หลุดออกไป
            // แต่ **ผู้เรียกต้องเช็ค IsUsable ก่อน** ไม่งั้นจะเอาค่าว่างไปใช้เป็น
            // รหัสผ่านจริงแล้วได้ error ปลายทางที่ชี้ต้นเหตุไม่ได้
            _logger?.LogError(ex, "ถอดรหัสความลับที่เก็บไว้ไม่สำเร็จ — น่าจะเกิดจาก "
                + "Security:EncryptionKey ถูกเปลี่ยนหลังจากบันทึกค่านี้ ผู้ใช้ต้องกรอกใหม่");
            return string.Empty;
        }
    }

    public bool IsUsable(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!EncryptionHelper.IsEncrypted(value)) return true;   // legacy plaintext
        try { return !string.IsNullOrEmpty(EncryptionHelper.Decrypt(value, _key)); }
        catch { return false; }
    }
}

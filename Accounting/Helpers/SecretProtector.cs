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

    /// <summary>ฝ่ายค้านรอบ 219 B2: ค่านี้ถอดได้ด้วย<b>คีย์เดิม</b>เท่านั้น (ต้องเข้ารหัสใหม่ด้วยคีย์ปัจจุบัน) · ตัวจำลองในเทสต์ไม่ต้อง implement</summary>
    bool NeedsReprotect(string? value) => false;

    /// <summary>ฝ่ายค้านรอบ 219 B2: ถอดด้วยคีย์เดิมแล้วเข้ารหัสใหม่ด้วยคีย์ปัจจุบัน · null = ไม่ต้องทำ (คีย์ปัจจุบันอยู่แล้ว/plaintext/ถอดไม่ออก)</summary>
    string? Reprotect(string? value) => null;
}

public class SecretProtector : ISecretProtector
{
    private const string DevKey = EncryptionKeyResolver.DevKey;

    private readonly string _key;
    private readonly IReadOnlyList<string> _legacyKeys;
    private readonly ILogger<SecretProtector>? _logger;

    /// <summary>รอบ 219 เฟส 0 (P0-4): คีย์มาจาก <see cref="EncryptionKeyResolver"/> ตัวเดียวกับคอลัมน์ PII ใน <c>Program.cs</c>
    /// (env <c>ENCRYPTION_KEY</c> ชนะ <c>Security:EncryptionKey</c>) · production + ไม่มีคีย์/เป็น dev key ⇒ throw ตอนสร้าง (boot ไม่ขึ้น) ·
    /// ฝ่ายค้านรอบ 219 B2: ค่าที่เคยเข้ารหัสด้วยคีย์เดิม (config เดิม/dev key) ยังถอดได้ผ่าน <see cref="EncryptionKeyResolver.LegacyCandidates"/>
    /// แล้ว <c>SecretReprotectJob</c> เข้ารหัสใหม่ด้วยคีย์ปัจจุบัน</summary>
    public SecretProtector(IConfiguration config, IHostEnvironment env, ILogger<SecretProtector> logger)
        : this(Primary(config, env), logger,
            EncryptionKeyResolver.LegacyCandidates(Primary(config, env),
                Environment.GetEnvironmentVariable("ENCRYPTION_KEY"), config["Security:EncryptionKey"]))
    {
    }

    private static string Primary(IConfiguration config, IHostEnvironment env)
        => EncryptionKeyResolver.RequireUsable(
            EncryptionKeyResolver.Resolve(Environment.GetEnvironmentVariable("ENCRYPTION_KEY"), config["Security:EncryptionKey"]),
            env.IsProduction(), "ความลับที่เก็บในฐานข้อมูล (คีย์ gateway/SMTP/OAuth)");

    /// <summary>รับคีย์ตรง ๆ — ใช้ในเทสต์ (จำลองการ "เปลี่ยนคีย์" ได้โดยไม่ต้องยก
    /// IConfiguration/ILogger ทั้งชุดมา) และเผื่อ call site ที่ถือคีย์อยู่แล้ว ·
    /// <paramref name="legacyKeys"/> = คีย์เดิมที่ลองถอดต่อเมื่อคีย์ปัจจุบันถอดไม่ออก (ไม่ใช้เข้ารหัส)</summary>
    public SecretProtector(string key, ILogger<SecretProtector>? logger = null, IEnumerable<string>? legacyKeys = null)
    {
        _key = string.IsNullOrEmpty(key) ? DevKey : key;
        _legacyKeys = (legacyKeys ?? Array.Empty<string>()).Where(k => !string.IsNullOrEmpty(k) && k != _key).Distinct().ToList();
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

    /// <summary>ถอดด้วยคีย์ปัจจุบันก่อน แล้วคีย์เดิมตามลำดับ · คืน (ข้อความ, ถอดด้วยคีย์เดิมไหม) · ถอดไม่ได้ทุกคีย์ = null</summary>
    private (string Plain, bool ByLegacy)? TryDecrypt(string value)
    {
        try { return (EncryptionHelper.Decrypt(value, _key), false); } catch { /* ลองคีย์เดิมต่อ */ }
        foreach (var k in _legacyKeys)
        {
            try { return (EncryptionHelper.Decrypt(value, k), true); } catch { /* คีย์ถัดไป */ }
        }
        return null;
    }

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // Legacy plaintext rows — return as-is so the app keeps working
        // until the next save migrates the value to ciphertext.
        if (!EncryptionHelper.IsEncrypted(value)) return value;
        if (TryDecrypt(value) is { } r) return r.Plain;
        // ถอดไม่ออกทุกคีย์ (ปัจจุบัน + เดิม) = ค่านี้ถูกเข้ารหัสด้วยคีย์ที่ระบบไม่รู้จัก. คืนค่าว่างเพื่อไม่ให้ ciphertext หลุดออกไป
        // แต่ **ผู้เรียกต้องเช็ค IsUsable ก่อน** ไม่งั้นจะเอาค่าว่างไปใช้เป็นรหัสผ่านจริงแล้วได้ error ปลายทางที่ชี้ต้นเหตุไม่ได้
        _logger?.LogError("ถอดรหัสความลับที่เก็บไว้ไม่สำเร็จด้วยคีย์ปัจจุบันและคีย์เดิมทุกตัว — ผู้ใช้ต้องกรอกใหม่");
        return string.Empty;
    }

    public bool IsUsable(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!EncryptionHelper.IsEncrypted(value)) return true;   // legacy plaintext
        return TryDecrypt(value) is { } r && !string.IsNullOrEmpty(r.Plain);
    }

    public bool NeedsReprotect(string? value)
        => !string.IsNullOrEmpty(value) && EncryptionHelper.IsEncrypted(value) && TryDecrypt(value) is { ByLegacy: true };

    public string? Reprotect(string? value)
        => !string.IsNullOrEmpty(value) && EncryptionHelper.IsEncrypted(value) && TryDecrypt(value) is { ByLegacy: true } r
            ? EncryptionHelper.Encrypt(r.Plain, _key)
            : null;
}

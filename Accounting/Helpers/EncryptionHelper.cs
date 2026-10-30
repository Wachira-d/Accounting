using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// EncryptionHelper — เข้ารหัสข้อมูลอ่อนไหวก่อนเก็บลง Database
/// ใช้ AES-256-GCM (authenticated encryption)
/// สำหรับ: API keys, certificate passwords, connection strings, tokens
/// </summary>
public static class EncryptionHelper
{
    private const int NonceSize = 12;  // AES-GCM nonce = 96 bits
    private const int TagSize = 16;    // AES-GCM tag = 128 bits
    private const int KeySize = 32;    // AES-256 = 256 bits

    /// <summary>
    /// Encrypt plaintext using AES-256-GCM. Returns Base64-encoded ciphertext.
    /// Format: [nonce:12][tag:16][ciphertext:N]
    /// </summary>
    public static string Encrypt(string plaintext, string encryptionKey)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;

        var key = DeriveKey(encryptionKey);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Pack: nonce + tag + ciphertext
        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypt Base64-encoded AES-256-GCM ciphertext.
    /// </summary>
    public static string Decrypt(string encryptedBase64, string encryptionKey)
    {
        if (string.IsNullOrEmpty(encryptedBase64)) return encryptedBase64;

        var key = DeriveKey(encryptionKey);
        var data = Convert.FromBase64String(encryptedBase64);

        if (data.Length < NonceSize + TagSize)
            throw new CryptographicException("Invalid encrypted data");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[data.Length - NonceSize - TagSize];

        Buffer.BlockCopy(data, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(data, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(data, NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// Check if a string looks like it was encrypted by this helper (Base64 with min length).
    /// </summary>
    public static bool IsEncrypted(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 40) return false;
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length >= NonceSize + TagSize;
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Encrypted-value format check failed: {ex.Message}"); return false; }
    }

    /// <summary>ฝ่ายค้านรอบ 219 (ข้อ 2): แคชคีย์ที่ derive แล้ว — <c>SecretProtector</c> ลองคีย์ปัจจุบัน + คีย์เดิมได้ถึง 5 ตัวต่อค่า
    /// ⇒ PBKDF2 100k รอบ × 5 ต่อคำขอ = ขยายภาระ CPU · salt เป็นค่าคงที่ที่ derive จาก passphrase เอง (ไม่ใช่ salt ต่อค่า) ⇒
    /// กุญแจของแคช = SHA-256(passphrase) พอ (ไม่เก็บ passphrase ดิบในหน่วยความจำของแคช) · ผลเป็น pure/deterministic ⇒
    /// ไม่ใช่ state ข้ามคำขอที่ต้องสอดคล้องข้ามเครื่อง (กฎเหล็ก #4 §D อนุญาต — ทุก instance คำนวณได้ค่าเดียวกัน) ·
    /// จำกัดขนาด <see cref="MaxCachedKeys"/> (เต็ม = ล้างทั้งแคช — คีย์จริงในระบบมี ≤ 6 ตัว ⇒ ไม่เกิดในการใช้งานปกติ)</summary>
    internal const int MaxCachedKeys = 64;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> DerivedKeys = new(StringComparer.Ordinal);

    /// <summary>จำนวนคีย์ในแคช (เทสต์)</summary>
    internal static int CachedKeyCount => DerivedKeys.Count;

    /// <summary>
    /// Derive a 256-bit key from a passphrase using PBKDF2 (cached — see <see cref="DerivedKeys"/>).
    /// </summary>
    private static byte[] DeriveKey(string passphrase)
    {
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passphrase)));
        if (DerivedKeys.TryGetValue(cacheKey, out var cached)) return cached;
        var derived = DeriveKeyUncached(passphrase);
        if (DerivedKeys.Count >= MaxCachedKeys) DerivedKeys.Clear();
        DerivedKeys[cacheKey] = derived;
        return derived;
    }

    private static byte[] DeriveKeyUncached(string passphrase)
    {
        // Use a fixed salt derived from the passphrase itself for deterministic key derivation
        // In production, consider storing a random salt alongside the ciphertext
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("NextAcc:" + passphrase));
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            KeySize);
    }

    /// <summary>
    /// Generate a cryptographically secure random key for encryption.
    /// </summary>
    public static string GenerateKey()
    {
        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);
        return Convert.ToBase64String(key);
    }

    /// <summary>
    /// Hash sensitive data for safe logging (one-way).
    /// </summary>
    public static string HashForLog(string value)
    {
        if (string.IsNullOrEmpty(value)) return "[empty]";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..12] + "...";
    }
}

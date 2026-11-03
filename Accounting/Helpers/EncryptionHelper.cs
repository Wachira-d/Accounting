using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// EncryptionHelper — เข้ารหัสข้อมูลอ่อนไหวก่อนเก็บลง Database
/// ใช้ AES-256-GCM (authenticated encryption)
/// สำหรับ: API keys, certificate passwords, connection strings, tokens
///
/// <para><b>รูปแบบ ciphertext (รอบ 231 · ตัวตั้งตัวเดียว):</b> <c>enc1:</c> + Base64([nonce:12][tag:16][ciphertext:N]) —
/// คำว่า "เข้ารหัสแล้วหรือยัง" ตัดสินจาก<b>คำนำหน้า</b> <see cref="CipherPrefix"/> เท่านั้น (<see cref="IsEncrypted"/>)</para>
///
/// <para><b>ที่มา (บั๊ก production):</b> รุ่นก่อนรอบ 231 ไม่มีคำนำหน้า — <c>IsEncrypted</c> เดาจาก "ยาว ≥ 40 + เป็น base64 + ≥ 28 ไบต์" ⇒
/// Channel Access Token ของ LINE (base64 ยาว ~170 ตัว) ถูกเข้าใจว่าเป็น ciphertext ⇒ <c>SecretProtector.Protect</c> "ไม่เข้ารหัสซ้ำ" เก็บค่าดิบ ⇒
/// ตอนอ่าน พยายามถอดค่าดิบ ⇒ ล้มทุกคีย์ ⇒ <c>IsUsable=false</c> ⇒ ระบบทิ้งค่าตั้ง LINE OA กลางทั้งชุด (webhook 401) และหน้าจอฟ้องว่า
/// "คีย์เข้ารหัสของระบบถูกเปลี่ยน" ทั้งที่คีย์ไม่เคยเปลี่ยน · ตัวตัดสินเดิม (เดาจากรูป) เหลือไว้เป็น <see cref="LooksLikeLegacyCiphertext"/>
/// สำหรับ<b>ฝั่งอ่าน</b>ค่าเก่าที่ยังไม่มีคำนำหน้าเท่านั้น — ห้ามใช้ตัดสินฝั่งเขียน และห้ามมีสำเนาที่สองนอกไฟล์นี้ (<c>tools/encrypted_heuristic_check.py</c>)</para>
/// </summary>
public static class EncryptionHelper
{
    private const int NonceSize = 12;  // AES-GCM nonce = 96 bits
    private const int TagSize = 16;    // AES-GCM tag = 128 bits
    private const int KeySize = 32;    // AES-256 = 256 bits

    /// <summary>คำนำหน้า ciphertext รุ่น 1 — ค่าที่ขึ้นต้นด้วยคำนี้คือผลของ <see cref="Encrypt"/> แน่นอน · เปลี่ยนรูปแบบในอนาคต = คำนำหน้าใหม่ (enc2:) ไม่แก้ตัวนี้</summary>
    public const string CipherPrefix = "enc1:";

    /// <summary>
    /// Encrypt plaintext using AES-256-GCM. Returns <see cref="CipherPrefix"/> + Base64-encoded ciphertext.
    /// Format: enc1:[nonce:12][tag:16][ciphertext:N]
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

        return CipherPrefix + Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypt AES-256-GCM ciphertext — รับทั้งแบบมีคำนำหน้า <c>enc1:</c> (ปัจจุบัน) และแบบไม่มี (ค่าเก่าก่อนรอบ 231) ·
    /// รูปแบบผิด (ไม่ใช่ base64 / สั้นกว่า nonce+tag) ⇒ <see cref="CryptographicException"/> · คีย์ผิด/ถูกแก้ ⇒ exception จาก AES-GCM
    /// </summary>
    public static string Decrypt(string encryptedBase64, string encryptionKey)
    {
        if (string.IsNullOrEmpty(encryptedBase64)) return encryptedBase64;

        var key = DeriveKey(encryptionKey);
        var data = DecodePayload(encryptedBase64) ?? throw new CryptographicException("Invalid encrypted data");

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

    /// <summary>ค่านี้คือผลของ <see cref="Encrypt"/> (ขึ้นต้นด้วย <see cref="CipherPrefix"/>) — <b>ตัดสินจากคำนำหน้าเท่านั้น ไม่เดาจากรูป</b> ·
    /// ค่าเก่าที่ไม่มีคำนำหน้า (ciphertext ก่อนรอบ 231 หรือ plaintext) ⇒ false ทั้งคู่ — ฝั่งอ่านแยกสองอย่างนั้นด้วยการ "ลองถอด" (<c>SecretProtector.Inspect</c>)</summary>
    public static bool IsEncrypted(string? value)
        => !string.IsNullOrEmpty(value) && value.StartsWith(CipherPrefix, StringComparison.Ordinal);

    /// <summary>ค่าที่มีคำนำหน้าแล้วถอด base64 ได้ครบ nonce+tag — แยก "เสียหาย/ถูกตัด" (false) ออกจาก "คีย์ผิด" (true แต่ถอดไม่ออก)
    /// เพื่อให้ข้อความถึงผู้ใช้อ้างเหตุที่ตรวจจริง (กฎ F2 ข้อ 7) · ค่าที่ไม่มีคำนำหน้า ⇒ false</summary>
    public static bool IsWellFormedCiphertext(string? value)
        => IsEncrypted(value) && DecodePayload(value!) != null;

    /// <summary>
    /// <b>เฉพาะฝั่งอ่านค่าเก่า:</b> ค่าที่ไม่มีคำนำหน้าแต่ "ดูเหมือน" ciphertext รุ่นก่อนรอบ 231 (base64 ยาว ≥ 40 · ≥ nonce+tag ไบต์) ⇒ ควร<b>ลองถอด</b>ก่อน
    /// ถอดได้ = ciphertext เก่า (ย้ายไปรูปแบบมีคำนำหน้า) · ถอดไม่ได้ทุกคีย์ = plaintext เก่าที่บังเอิญเป็น base64 (เช่น token ของ LINE) ใช้ตามเดิม ·
    /// <b>ห้าม</b>ใช้ตัดสินว่า "เข้ารหัสแล้ว ไม่ต้องเข้ารหัสซ้ำ" — นั่นคือบั๊กที่ทำให้ token ถูกเก็บดิบ (ดูหัวไฟล์) · นิยามเดียวในเรพ
    /// </summary>
    public static bool LooksLikeLegacyCiphertext(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 40 || IsEncrypted(value)) return false;
        return DecodePayload(value) != null;
    }

    /// <summary>ตัด <see cref="CipherPrefix"/> (ถ้ามี) แล้วถอด base64 · null = ไม่ใช่ base64 หรือสั้นกว่า nonce+tag</summary>
    private static byte[]? DecodePayload(string value)
    {
        var body = IsEncrypted(value) ? value[CipherPrefix.Length..] : value;
        try
        {
            var bytes = Convert.FromBase64String(body);
            return bytes.Length >= NonceSize + TagSize ? bytes : null;
        }
        catch (FormatException) { return null; }
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

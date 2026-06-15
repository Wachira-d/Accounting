using Microsoft.AspNetCore.DataProtection;

namespace Accounting.Services.Implementations.Security;

/// <summary>
/// F15 — Column-level PII encryption ผ่าน ASP.NET Core DataProtection API.
/// ไม่ต้องการ KMS ภายนอก (Azure Key Vault / Vault / AWS KMS) เพราะ
/// DataProtection ใช้ self-managed key ring (เก็บใน
/// /var/aspnet/keys ผ่าน FileSystem provider ตอน prod ให้ persist).
///
/// ใช้ pattern:
///   var protector = pp.GetPiiProtector(); // purpose "Accounting.PII.v1"
///   var encrypted = protector.Encrypt(plainCitizenId);
///   // ... store in DB ...
///   var plain = protector.Decrypt(encrypted);
///
/// Purpose string จะ wrap key derivation — แก้ purpose ทีหลังจะ rotate ได้
/// (decrypt เก่ายังใช้ได้ใน period rotation policy).
///
/// ไม่ใช่ for: passwords (ใช้ BCrypt), keys (เก็บใน env), session token
/// (มี ASP.NET dpapi ของตัว).
///
/// Use cases:
///   • Employee.CitizenId — Thai ID 13 digit ที่อยู่ Identity classification
///   • User.SignatureImageBase64 — biometric ตามนิยาม PII
///   • Contact.TaxId เมื่อ shared in audit logs — เก็บ encrypted hash
/// </summary>
public interface IPiiProtector
{
    /// <summary>Encrypt plain text → base64-armored ciphertext. Null/empty
    /// passes through unchanged (no overhead, no false-positive on empties).</summary>
    string? Encrypt(string? plain);

    /// <summary>Decrypt — ถ้า input ไม่ใช่ ciphertext ของ purpose นี้ throws
    /// CryptographicException. TryDecrypt overload สำหรับ pull-through.</summary>
    string? Decrypt(string? cipher);

    /// <summary>Safe decrypt — คืน null เมื่อ tampering/key mismatch แทน
    /// throw. ใช้สำหรับ display where can show "(decryption failed)".</summary>
    string? TryDecrypt(string? cipher);
}

public class PiiProtector : IPiiProtector
{
    private const string Purpose = "Accounting.PII.v1";
    private const string CipherPrefix = "enc:v1:";

    private readonly IDataProtector _protector;

    public PiiProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string? Encrypt(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        // Skip if already encrypted (idempotent — re-encrypt ของเก่าจะทำลาย data)
        if (plain.StartsWith(CipherPrefix, StringComparison.Ordinal)) return plain;
        return CipherPrefix + _protector.Protect(plain);
    }

    public string? Decrypt(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return cipher;
        if (!cipher.StartsWith(CipherPrefix, StringComparison.Ordinal)) return cipher; // plain (legacy)
        return _protector.Unprotect(cipher[CipherPrefix.Length..]);
    }

    public string? TryDecrypt(string? cipher)
    {
        try { return Decrypt(cipher); }
        catch { return null; }
    }
}

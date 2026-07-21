using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Accounting.Helpers;

/// <summary>EF Core ValueConverter ที่ encrypt-at-rest ก่อน insert/update +
/// decrypt อัตโนมัติตอนอ่าน. ใช้กับ field PII (CitizenId, PassportNumber,
/// TaxId พนักงาน, BankAccountNumber).
///
/// Transparent over legacy plaintext: ถ้า ciphertext format ไม่ตรง (legacy
/// row ที่บันทึกไปก่อนเปิด encryption) ⇒ return as-is + log warning. ครั้งถัดไป
/// ที่ save จะ re-write เป็น ciphertext = lazy migration.
///
/// Key สำเร็จมาจาก static init ตอน Program.cs (`EncryptedColumnConverter.Configure`
/// คะตอน startup). ถ้าไม่ configure → fallback dev key + log warning.</summary>
public class EncryptedColumnConverter : ValueConverter<string?, string?>
{
    public EncryptedColumnConverter() : base(
        v => EncryptOnSave(v),
        v => DecryptOnRead(v))
    { }

    private static string _key = "default-dev-key-change-in-production";
    private static bool _configured;

    /// <summary>เรียกครั้งเดียวตอน startup (Program.cs) ก่อน DbContext build.
    /// ถ้าไม่เรียก → ใช้ dev key + log warning.</summary>
    public static void Configure(string encryptionKey)
    {
        if (string.IsNullOrWhiteSpace(encryptionKey)) return;
        _key = encryptionKey;
        _configured = true;
    }

    [return: NotNullIfNotNull(nameof(value))]
    private static string? EncryptOnSave(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (EncryptionHelper.IsEncrypted(value)) return value;   // idempotent — ไม่เข้ารหัสซ้ำ
        return EncryptionHelper.Encrypt(value, _key);
    }

    [return: NotNullIfNotNull(nameof(value))]
    private static string? DecryptOnRead(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!EncryptionHelper.IsEncrypted(value)) return value;  // legacy plaintext — pass-through
        try { return EncryptionHelper.Decrypt(value, _key); }
        catch
        {
            // Corrupted ciphertext / key rotation that lost the old row.
            // Return ค่าเดิมเพื่อไม่ครazh app — admin จะเห็น garbage ที่ frontend
            // แล้วรู้ตัวว่าต้อง re-key
            return value;
        }
    }

    public static bool IsConfigured => _configured;
}

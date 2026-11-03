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

    /// <summary>ฝ่ายค้านรอบ 231 S2 (ปิด oracle): ค่า <c>enc1:…</c> ที่<b>อ่านมาจากฐานแล้วถอดไม่ออก</b> (<see cref="DecryptOnRead"/> คืนค่าเดิม) จะถูก EF ส่งกลับมาตอนบันทึกแถวเดิม —
    /// ต้องผ่านไปทั้งก้อน (ไม่ห่อซ้อน) แต่ค่า <c>enc1:…</c> ที่<b>ผู้ใช้ส่งมา</b> ห้ามผ่าน (ไม่งั้นส่ง ciphertext ของช่องอื่นมาให้ระบบถอดให้ได้) · สองอย่างนี้แยกจากเนื้อหาไม่ได้
    /// ⇒ จำค่าที่อ่านแล้วถอดไม่ออกไว้ (hash · จำกัด <see cref="MaxPassThrough"/>) · กฎ #4 §D: ไม่ใช่ state ที่ต้องตรงกันข้ามเครื่อง — EF อ่านและบันทึก entity เดียวกันในโพรเซสเดียว ·
    /// ถ้าถูกล้างก่อนบันทึก ค่านั้นถูกห่อซ้อน (ค่าที่ถอดไม่ออกอยู่แล้ว ⇒ ไม่เสียอะไรเพิ่ม · ผู้ดูแลยังเห็น enc1:… ว่าต้อง re-key)</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> PassThrough = new(StringComparer.Ordinal);
    internal const int MaxPassThrough = 1024;
    private static string Fingerprint(string v) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(v)));

    /// <summary>รอบ 231: เข้ารหัส<b>เสมอ</b> — ไม่มี "มีคำนำหน้าแล้วข้าม" (เดิมเดาจากรูป ⇒ PII ที่บังเอิญเป็น base64 ยาวถูกเก็บดิบตลอดกาล · S2: ข้ามด้วยคำนำหน้า = oracle) ·
    /// ยกเว้นค่าที่ <see cref="DecryptOnRead"/> ปล่อยผ่านมาเพราะถอดไม่ออก (ดู <see cref="PassThrough"/>) · ค่าที่ผู้ใช้ส่ง enc1:… ถูกห่อซ้อน ⇒ อ่านกลับได้สตริง enc1:… ไม่ใช่ความลับที่ถอดแล้ว
    /// (ด่านข้อความไทยอยู่ที่ <see cref="SecretInputRule.CipherPrefixProblem"/> ในเส้นแก้ไขพนักงาน)</summary>
    [return: NotNullIfNotNull(nameof(value))]
    private static string? EncryptOnSave(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (EncryptionHelper.IsEncrypted(value) && PassThrough.ContainsKey(Fingerprint(value))) return value;   // ค่าเดิมที่อ่านมาแล้วถอดไม่ออก — คงไว้
        return EncryptionHelper.Encrypt(value, _key);
    }

    /// <summary>มีคำนำหน้า ⇒ ถอด (ถอดไม่ออก = คืนค่าเดิม ให้ผู้ดูแลเห็นว่าต้อง re-key — ไม่ล้มทั้งแถว) ·
    /// ไม่มีคำนำหน้าแต่ดูเหมือน ciphertext รุ่นก่อนรอบ 231 ⇒ ลองถอดด้วยคีย์ปัจจุบัน (ได้ = ciphertext เก่า · บันทึกครั้งถัดไปจะได้คำนำหน้า) ·
    /// อื่น ๆ = plaintext เก่า pass-through (รูปแบบเดียวกับ <c>SecretProtector.Inspect</c> — คอลัมน์ PII ไม่มีคีย์เดิมให้ลอง)</summary>
    [return: NotNullIfNotNull(nameof(value))]
    private static string? DecryptOnRead(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!EncryptionHelper.IsEncrypted(value) && !EncryptionHelper.LooksLikeLegacyCiphertext(value)) return value;  // legacy plaintext — pass-through
        try { return EncryptionHelper.Decrypt(value, _key); }
        catch
        {
            // มีคำนำหน้า: ciphertext เสียหาย / คีย์ถูกหมุนโดยไม่ย้ายแถว ⇒ คืนค่าเดิม (ผู้ดูแลเห็น enc1:… บนจอแล้วรู้ว่าต้อง re-key) + จำไว้ให้ EncryptOnSave ปล่อยผ่าน
            // ไม่มีคำนำหน้า: ค่าเก่าที่บังเอิญเป็น base64 แต่ไม่ใช่ ciphertext ⇒ คือ plaintext ใช้ตามเดิม (AmbiguousBase64 ในภาษาของ SecretProtector)
            if (EncryptionHelper.IsEncrypted(value))
            {
                if (PassThrough.Count >= MaxPassThrough) PassThrough.Clear();
                PassThrough.TryAdd(Fingerprint(value), 0);
            }
            return value;
        }
    }

    public static bool IsConfigured => _configured;
}

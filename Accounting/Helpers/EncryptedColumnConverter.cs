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
    private static IReadOnlyList<string> _previousKeys = Array.Empty<string>();

    /// <summary>เรียกครั้งเดียวตอน startup (Program.cs) ก่อน DbContext build.
    /// ถ้าไม่เรียก → ใช้ dev key + log warning.</summary>
    public static void Configure(string encryptionKey)
    {
        if (string.IsNullOrWhiteSpace(encryptionKey)) return;
        _key = encryptionKey;
        _configured = true;
    }

    /// <summary>รอบ 274 (บั๊ก production: เลขผู้เสียภาษีพนักงานในไฟล์ ภ.ง.ด.1/1ก ยาว 5–14 หลัก · 50 ทวิ ภ.ง.ด.1ก ได้ 7 หลัก หลังผู้ใช้ตั้ง <c>ENCRYPTION_KEY</c>) —
    /// <b>คีย์เดิมที่ใช้ถอดอย่างเดียว</b> (ไม่ใช้เข้ารหัส) จาก <see cref="EncryptionKeyResolver.PiiPreviousKeys"/> ·
    /// เดิมถอดด้วยคีย์ปัจจุบันตัวเดียว ⇒ แถวที่เข้ารหัสด้วย dev key/คีย์ config เดิมก่อนตั้งคีย์ใหม่ถอดไม่ออก ⇒ <see cref="DecryptOnRead"/> คืน ciphertext ทั้งก้อน ⇒
    /// ตัวตัดเลข (<see cref="ThaiTaxId.Normalize"/>) เก็บเฉพาะตัวเลขในข้อความ base64 = เลขสุ่มความยาวสุ่มลงไฟล์ยื่นสรรพากร ·
    /// ถอดด้วยคีย์เดิมได้ ⇒ ค่าถูกต้องทันที และบันทึกครั้งถัดไป/งาน <c>PiiReprotectJob</c> เข้ารหัสใหม่ด้วยคีย์ปัจจุบัน</summary>
    public static void ConfigurePreviousKeys(IEnumerable<string>? previousKeys)
        => _previousKeys = (previousKeys ?? Array.Empty<string>())
            .Where(k => !string.IsNullOrEmpty(k) && k != _key).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>คีย์ปัจจุบัน + คีย์เดิมที่ converter ใช้อยู่ — งานย้ายคีย์ (<c>PiiReprotectPass</c>) ใช้ชุดเดียวกับฝั่งอ่าน (ไม่ประกอบเอง)</summary>
    public static (string Current, IReadOnlyList<string> Previous) Keys => (_key, _previousKeys);

    /// <summary>ผลตรวจค่าหนึ่งค่าในคอลัมน์ PII · <see cref="Plain"/> = ค่าที่ใช้ได้ (Plaintext/Current/Previous) หรือค่าดิบ (Undecryptable) ·
    /// <see cref="KeyIndex"/> = ลำดับคีย์เดิมที่ถอดได้ (เฉพาะ <see cref="PiiCipherState.Previous"/>)</summary>
    public readonly record struct Inspection(PiiCipherState State, string? Plain, int KeyIndex = -1);

    /// <summary><b>ตัวตัดสินเดียว</b>ของ "ค่านี้ในคอลัมน์ PII ถอดด้วยคีย์ไหน" — pure (คีย์ส่งเข้ามา) ⇒ ฝั่งอ่านของ EF (<see cref="DecryptOnRead"/>) ·
    /// งานย้ายคีย์ · รายงานแถวเสีย และเทสต์เรียกตัวเดียวกัน · ลำดับ: คีย์ปัจจุบันก่อน แล้วคีย์เดิมตามลำดับ</summary>
    public static Inspection Inspect(string? stored, string currentKey, IReadOnlyList<string> previousKeys)
    {
        if (string.IsNullOrEmpty(stored)) return new(PiiCipherState.Empty, stored);
        var prefixed = EncryptionHelper.IsEncrypted(stored);
        if (!prefixed && !EncryptionHelper.LooksLikeLegacyCiphertext(stored)) return new(PiiCipherState.Plaintext, stored);
        if (TryDecrypt(stored, currentKey, out var p)) return new(PiiCipherState.Current, p);
        for (var i = 0; i < previousKeys.Count; i++)
            if (TryDecrypt(stored, previousKeys[i], out p)) return new(PiiCipherState.Previous, p, i);
        // ไม่มีคำนำหน้าและถอดไม่ออกทุกคีย์ = plaintext เก่าที่บังเอิญเป็น base64 (AmbiguousBase64 ในภาษาของ SecretProtector) — ใช้ตามเดิม
        return prefixed ? new(PiiCipherState.Undecryptable, stored) : new(PiiCipherState.Plaintext, stored);
    }

    private static bool TryDecrypt(string stored, string key, out string plain)
    {
        try { plain = EncryptionHelper.Decrypt(stored, key); return true; }
        catch (System.Security.Cryptography.CryptographicException) { plain = ""; return false; }
        catch (ArgumentException) { plain = ""; return false; }
    }

    /// <summary>เข้ารหัสด้วยคีย์ปัจจุบันของ converter — ให้งานย้ายคีย์เขียนรูปเดียวกับที่ EF เขียน (ไม่ถือคีย์เอง)</summary>
    public static string ProtectWithCurrentKey(string plain, string currentKey) => EncryptionHelper.Encrypt(plain, currentKey);

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

    /// <summary>มีคำนำหน้า ⇒ ถอดด้วยคีย์ปัจจุบัน แล้วคีย์เดิม (รอบ 274) · ถอดไม่ออกทุกคีย์ = คืนค่าเดิม (ผู้ดูแลเห็น enc1:… — ไม่ล้มทั้งแถว ·
    /// <b>ผู้อ่านเลขประจำตัวต้องผ่าน <see cref="EmployeeTaxIdentity.Inspect"/> ซึ่งปฏิเสธค่า enc1:… ไม่ตัดเอาแต่ตัวเลข</b>) ·
    /// ไม่มีคำนำหน้าแต่ดูเหมือน ciphertext รุ่นก่อนรอบ 231 ⇒ ลองถอดชุดเดียวกัน (ได้ = ciphertext เก่า · บันทึกครั้งถัดไปจะได้คำนำหน้า) ·
    /// อื่น ๆ = plaintext เก่า pass-through (รูปแบบเดียวกับ <c>SecretProtector.Inspect</c>)</summary>
    [return: NotNullIfNotNull(nameof(value))]
    private static string? DecryptOnRead(string? value)
    {
        var r = Inspect(value, _key, _previousKeys);
        if (r.State == PiiCipherState.Undecryptable)
        {
            // ciphertext เสียหาย / คีย์ถูกหมุนโดยไม่ตั้ง ENCRYPTION_KEY_PREVIOUS ⇒ คืนค่าเดิม + จำไว้ให้ EncryptOnSave ปล่อยผ่าน (ไม่ห่อซ้อน)
            if (PassThrough.Count >= MaxPassThrough) PassThrough.Clear();
            PassThrough.TryAdd(Fingerprint(value!), 0);
            return value;
        }
        return r.Plain;
    }

    public static bool IsConfigured => _configured;
}

/// <summary>สถานะค่าหนึ่งค่าในคอลัมน์ PII (<see cref="EncryptedColumnConverter.Inspect"/>)</summary>
public enum PiiCipherState
{
    /// <summary>ว่าง/null</summary>
    Empty,
    /// <summary>plaintext เก่า (ก่อนเปิดการเข้ารหัส) — ใช้ตามเดิม · บันทึกครั้งถัดไปถูกเข้ารหัส</summary>
    Plaintext,
    /// <summary>ถอดด้วยคีย์ปัจจุบันได้</summary>
    Current,
    /// <summary>ถอดได้ด้วยคีย์เดิม (dev key / config เดิม / ENCRYPTION_KEY_PREVIOUS) — ต้องเข้ารหัสใหม่ด้วยคีย์ปัจจุบัน</summary>
    Previous,
    /// <summary>มีคำนำหน้า enc1: แต่ถอดไม่ออกทุกคีย์ — กู้ไม่ได้จนกว่าจะได้คีย์เดิมคืน หรือกรอกใหม่</summary>
    Undecryptable,
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace Accounting.Helpers;

/// <summary>สถานะของค่าที่เก็บในช่องความลับ — ผลของ <see cref="ISecretProtector.Inspect"/> (รอบ 231 · ตัวตัดสินเดียวของ "ใช้ได้ไหม · ต้องย้ายไหม · ฟ้องว่าอะไร")</summary>
public enum SecretState
{
    /// <summary>null/ว่าง</summary>
    Empty,
    /// <summary>ไม่มีคำนำหน้า <c>enc1:</c> และไม่ดูเหมือน ciphertext (สั้น/ไม่ใช่ base64) ⇒ plaintext เก่า — ใช้ตามเดิม · ย้ายเป็น ciphertext ได้</summary>
    Plaintext,
    /// <summary>ฝ่ายค้านรอบ 231 S1: ไม่มีคำนำหน้า · ดูเหมือน ciphertext รุ่นก่อน (base64 ยาว) · ถอดไม่ออกทุกคีย์ ⇒ <b>ใช้เป็นค่าดิบ</b> (คำตัดสินเจ้าของ: token ที่รุ่นก่อนเก็บดิบต้องใช้ได้)
    /// แต่แยกจาก <see cref="Plaintext"/> เพราะอาจเป็น ciphertext ของคีย์ที่หายไป ⇒ หน้าจอให้คำใบ้ (<c>SecretWarnings.SoftHint</c>) · งานย้ายนับแยก + LogWarning</summary>
    AmbiguousBase64,
    /// <summary>ไม่มีคำนำหน้า แต่ถอดได้ด้วยคีย์ปัจจุบัน ⇒ ciphertext รุ่นก่อนรอบ 231 — ใช้ได้ · ย้ายเป็นรูปแบบมีคำนำหน้าเมื่อเปิด <c>Security:MigrateCipherPrefix</c></summary>
    LegacyFormat,
    /// <summary>ถอดได้ด้วยคีย์เดิม (มี/ไม่มีคำนำหน้าก็ได้) ⇒ ใช้ได้ · ต้องเข้ารหัสใหม่ด้วยคีย์ปัจจุบัน (ย้ายเสมอ — รอบ 219 B2)</summary>
    LegacyKey,
    /// <summary>มีคำนำหน้า + ถอดได้ด้วยคีย์ปัจจุบัน — สภาพที่ต้องการ</summary>
    Current,
    /// <summary>มีคำนำหน้าแต่รูปแบบผิด (ไม่ใช่ base64 / สั้นกว่า nonce+tag) ⇒ ค่าเสียหาย/ถูกตัด — <b>ไม่ใช่เรื่องคีย์</b></summary>
    Malformed,
    /// <summary>มีคำนำหน้า รูปแบบถูก แต่ถอดไม่ออกทั้งคีย์ปัจจุบันและคีย์เดิมทุกตัว ⇒ นี่คือกรณีเดียวที่พูดได้ว่า "คีย์เข้ารหัสของระบบถูกเปลี่ยน"</summary>
    WrongKey,
}

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
    /// <summary>เข้ารหัส<b>เสมอ</b> · ค่าที่ขึ้นต้น <c>enc1:</c> ⇒ <see cref="BusinessRuleException"/> (ฝ่ายค้านรอบ 231 S2 — ปิด oracle · <see cref="SecretInputRule"/>)</summary>
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

    /// <summary>ฝ่ายค้านรอบ 219 B2: ถอดด้วยคีย์เดิมแล้วเข้ารหัสใหม่ด้วยคีย์ปัจจุบัน (เสมอ) · รอบ 231: ค่าที่ไม่มีคำนำหน้า (ciphertext เก่า/plaintext/กำกวม) ⇒ รูปแบบ <c>enc1:</c>
    /// <b>เฉพาะเมื่อเปิด</b> <c>Security:MigrateCipherPrefix</c> (ฝ่ายค้าน B1 forward-only: ค่าไม่มีคำนำหน้ายังอ่านได้ครบ ไม่รีบห่อ · ห่อแล้ว rollback ไม่ได้) ·
    /// null = ไม่ต้องทำ/ยังไม่เปิดประตู/ถอดไม่ออก</summary>
    string? Reprotect(string? value) => null;

    /// <summary>รอบ 231: สถานะของค่าที่เก็บไว้ — ผู้ประกอบข้อความถึงผู้ใช้ต้องถามตัวนี้ก่อนอ้างว่า "คีย์ถูกเปลี่ยน" (กฎ F2 ข้อ 7) ·
    /// default สำหรับตัวแทนในเทสต์: ใช้ได้ = <see cref="SecretState.Current"/> · ไม่ได้ = <see cref="SecretState.WrongKey"/></summary>
    SecretState Inspect(string? value)
        => string.IsNullOrEmpty(value) ? SecretState.Empty : IsUsable(value) ? SecretState.Current : SecretState.WrongKey;
}

public class SecretProtector : ISecretProtector
{
    private const string DevKey = EncryptionKeyResolver.DevKey;

    /// <summary>ชื่อ config ของประตูห่อค่าไม่มีคำนำหน้า (ฝ่ายค้านรอบ 231 B1) — default ปิด</summary>
    public const string MigrateCipherPrefixConfigKey = "Security:MigrateCipherPrefix";

    private readonly string _key;
    private readonly IReadOnlyList<string> _legacyKeys;
    private readonly bool _migrateCipherPrefix;
    private readonly ILogger<SecretProtector>? _logger;

    /// <summary>รอบ 219 เฟส 0 (P0-4): คีย์มาจาก <see cref="EncryptionKeyResolver"/> ตัวเดียวกับคอลัมน์ PII ใน <c>Program.cs</c>
    /// (env <c>ENCRYPTION_KEY</c> ชนะ <c>Security:EncryptionKey</c>) · production + ไม่มีคีย์/เป็น dev key ⇒ throw ตอนสร้าง (boot ไม่ขึ้น) ·
    /// ฝ่ายค้านรอบ 219 B2: ค่าที่เคยเข้ารหัสด้วยคีย์เดิม (config เดิม/dev key) ยังถอดได้ผ่าน <see cref="EncryptionKeyResolver.LegacyCandidates"/>
    /// แล้ว <c>SecretReprotectJob</c> เข้ารหัสใหม่ด้วยคีย์ปัจจุบัน · รอบ 231 B1: <see cref="MigrateCipherPrefixConfigKey"/> เปิดประตูห่อค่าไม่มีคำนำหน้า</summary>
    public SecretProtector(IConfiguration config, IHostEnvironment env, ILogger<SecretProtector> logger)
        : this(Primary(config, env), logger,
            EncryptionKeyResolver.LegacyCandidates(Primary(config, env),
                Environment.GetEnvironmentVariable("ENCRYPTION_KEY"), config["Security:EncryptionKey"]),
            string.Equals(config[MigrateCipherPrefixConfigKey], "true", StringComparison.OrdinalIgnoreCase))
    {
    }

    private static string Primary(IConfiguration config, IHostEnvironment env)
        => EncryptionKeyResolver.RequireUsable(
            EncryptionKeyResolver.Resolve(Environment.GetEnvironmentVariable("ENCRYPTION_KEY"), config["Security:EncryptionKey"]),
            env.IsProduction(), "ความลับที่เก็บในฐานข้อมูล (คีย์ gateway/SMTP/OAuth)");

    /// <summary>รับคีย์ตรง ๆ — ใช้ในเทสต์ (จำลองการ "เปลี่ยนคีย์" ได้โดยไม่ต้องยก
    /// IConfiguration/ILogger ทั้งชุดมา) และเผื่อ call site ที่ถือคีย์อยู่แล้ว ·
    /// <paramref name="legacyKeys"/> = คีย์เดิมที่ลองถอดต่อเมื่อคีย์ปัจจุบันถอดไม่ออก (ไม่ใช้เข้ารหัส) ·
    /// <paramref name="migrateCipherPrefix"/> = ประตู B1 (ห่อค่าไม่มีคำนำหน้าใน <see cref="Reprotect"/>)</summary>
    public SecretProtector(string key, ILogger<SecretProtector>? logger = null, IEnumerable<string>? legacyKeys = null, bool migrateCipherPrefix = false)
    {
        _key = string.IsNullOrEmpty(key) ? DevKey : key;
        _legacyKeys = (legacyKeys ?? Array.Empty<string>()).Where(k => !string.IsNullOrEmpty(k) && k != _key).Distinct().ToList();
        _migrateCipherPrefix = migrateCipherPrefix;
        _logger = logger;
        if (_key == DevKey)
            _logger?.LogWarning("Security:EncryptionKey not configured — using the development default. Set ENCRYPTION_KEY before going to production.");
    }

    /// <summary>รอบ 231: เข้ารหัส<b>เสมอ</b> — ไม่มี "มีคำนำหน้าแล้วข้าม" (ฝ่ายค้าน S2: ข้ามได้ = ผู้ใช้ส่ง enc1:&lt;ciphertext ช่องอื่น&gt; มาให้ระบบถอดให้) ·
    /// ค่าที่ขึ้นต้น enc1: หรือยาวเกิน ⇒ <see cref="BusinessRuleException"/> จาก <see cref="SecretInputRule.Check"/> · ค่าที่ไม่มีคำนำหน้าไม่มีวันถูกคืนกลับไปเก็บดิบ
    /// แม้จะเป็น base64 ยาว (token ของ LINE — บั๊กต้นเรื่อง)</summary>
    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        SecretInputRule.Check(plaintext);
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

    /// <summary>ตัวตัดสินเดียวของ "ค่านี้คืออะไร" — ทุกเมธอดสาธารณะอ่านผลจากตัวนี้ (ห้ามเรียก <see cref="EncryptionHelper.IsEncrypted"/> ตัดสินเอง)</summary>
    private (SecretState State, string? Plain) Classify(string value)
    {
        if (EncryptionHelper.IsEncrypted(value))
        {
            if (!EncryptionHelper.IsWellFormedCiphertext(value)) return (SecretState.Malformed, null);
            return TryDecrypt(value) is { } r ? (r.ByLegacy ? SecretState.LegacyKey : SecretState.Current, r.Plain) : (SecretState.WrongKey, null);
        }
        // ไม่มีคำนำหน้า: ciphertext รุ่นก่อนรอบ 231 หรือ plaintext — แยกได้ทางเดียวคือลองถอด
        if (EncryptionHelper.LooksLikeLegacyCiphertext(value))
        {
            if (TryDecrypt(value) is { } legacy) return (legacy.ByLegacy ? SecretState.LegacyKey : SecretState.LegacyFormat, legacy.Plain);
            // S1: ดูเหมือน ciphertext แต่ถอดไม่ออกทุกคีย์ ⇒ ใช้เป็นค่าดิบ (token ที่รุ่นก่อนเก็บดิบ) แต่ติดป้าย "กำกวม" ให้หน้าจอ/งานย้ายเห็น
            return (SecretState.AmbiguousBase64, value);
        }
        return (SecretState.Plaintext, value);
    }

    public SecretState Inspect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return SecretState.Empty;
        return Classify(value).State;
    }

    /// <summary>ฝ่ายค้านรอบ 231 N5: ค่าเดียวกันถูกอ่านทุกคำขอ ⇒ log ครั้งเดียวต่อค่า (hash · ไม่เก็บ ciphertext) · เต็มแล้วล้าง (pure cache — ไม่ใช่ state ข้ามเครื่อง)</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LoggedUndecryptable = new(StringComparer.Ordinal);
    private const int MaxLoggedUndecryptable = 256;

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var (state, plain) = Classify(value);
        if (plain != null) return plain;
        // ถอดไม่ออก (Malformed/WrongKey) = คืนค่าว่างเพื่อไม่ให้ ciphertext หลุดออกไป
        // แต่ **ผู้เรียกต้องเช็ค IsUsable ก่อน** ไม่งั้นจะเอาค่าว่างไปใช้เป็นรหัสผ่านจริงแล้วได้ error ปลายทางที่ชี้ต้นเหตุไม่ได้
        var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
        if (LoggedUndecryptable.Count >= MaxLoggedUndecryptable) LoggedUndecryptable.Clear();
        if (LoggedUndecryptable.TryAdd(h, 0))
            _logger?.LogWarning("ถอดรหัสความลับที่เก็บไว้ไม่สำเร็จ ({State}: มีคำนำหน้า enc1 · ลองคีย์ปัจจุบัน + คีย์เดิม {LegacyCount} ตัว · hash {Hash}) — ผู้ใช้ต้องกรอกใหม่ (log ครั้งเดียวต่อค่า)",
                state, _legacyKeys.Count, h);
        return string.Empty;
    }

    public bool IsUsable(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return Classify(value).Plain is { Length: > 0 };
    }

    /// <summary>ย้ายค่าไปสภาพ <see cref="SecretState.Current"/>: <see cref="SecretState.LegacyKey"/> ⇒ เสมอ (คีย์เดิมอาจถูกถอดออก) ·
    /// LegacyFormat/Plaintext/AmbiguousBase64 ⇒ เฉพาะเมื่อเปิด <see cref="MigrateCipherPrefixConfigKey"/> (B1 — ค่ายังอ่านได้ครบ ไม่รีบ · ห่อแล้วย้อนรุ่นไม่ได้) ·
    /// Current/Malformed/WrongKey ⇒ null (ไม่แตะ — ค่าที่ถอดไม่ออกคงไว้ให้ผู้ใช้เห็นว่าต้องกรอกใหม่)</summary>
    public string? Reprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var (state, plain) = Classify(value);
        var migratable = state == SecretState.LegacyKey
            || (_migrateCipherPrefix && (state is SecretState.LegacyFormat or SecretState.Plaintext or SecretState.AmbiguousBase64));
        return migratable && plain != null ? EncryptionHelper.Encrypt(plain, _key) : null;
    }
}

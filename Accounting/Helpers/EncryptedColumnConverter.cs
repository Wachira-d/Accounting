using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
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
    public EncryptedColumnConverter() : this(PiiValueKind.Text) { }

    /// <summary>รอบ 274-10: <paramref name="kind"/> = ชนิดค่าของคอลัมน์ — ใช้ตัดสินว่า "ค่าที่แกะจากชั้นซ้อนได้" ใช้ได้ไหม
    /// (<see cref="PiiValueKind.ThaiTaxId"/> ต้องเป็นเลข 13 หลักที่ผ่าน mod-11 · อื่น ๆ ต้องไม่ใช่ ciphertext อีก)</summary>
    public EncryptedColumnConverter(PiiValueKind kind) : base(v => EncryptOnSave(v), ReadExpression(kind))
    { Kind = kind; }

    /// <summary>ชนิดค่าของคอลัมน์นี้ — งานย้ายคีย์/รายงานแถวเสียอ่านจาก converter ของโมเดล (ไม่มีรายชื่อคอลัมน์สำเนาที่สอง)</summary>
    public PiiValueKind Kind { get; }

    private static Expression<Func<string?, string?>> ReadExpression(PiiValueKind kind)
        => kind == PiiValueKind.ThaiTaxId ? v => DecryptOnRead(v, PiiValueKind.ThaiTaxId) : v => DecryptOnRead(v, PiiValueKind.Text);

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
    /// <para>รอบ 274-10: <see cref="NestedLayers"/> = จำนวนชั้นซ้อนที่แกะออก (สถานะ <see cref="PiiCipherState.Nested"/>) ·
    /// <see cref="NestedUnresolved"/> = ถอดชั้นนอกได้แต่ข้างในยังเป็น ciphertext ที่ใช้ไม่ได้ (ถอดไม่ออก/เกินความลึก/ผิดรูป/ผู้ใช้ส่ง ciphertext มาเอง) —
    /// <see cref="Plain"/> เป็น ciphertext ชั้นใน (ผู้อ่านปลายทางเห็น <see cref="IsUnreadable"/> = true ⇒ ไม่ตัดตัวเลข)</para></summary>
    public readonly record struct Inspection(PiiCipherState State, string? Plain, int KeyIndex = -1, int NestedLayers = 0, bool NestedUnresolved = false);

    /// <summary>รอบ 274-10: ความลึกสูงสุดของชั้นซ้อนที่แกะ (กันวนไม่จบ/ค่าที่ถูกห่อซ้ำโดยตั้งใจ)</summary>
    public const int MaxNestedDepth = 3;

    /// <summary>รอบ 274-10: เครื่องหมายใน plaintext ของ "ciphertext ที่ผู้ใช้ส่งมาเป็นค่า" ที่ <see cref="EncryptOnSave"/> ห่อตั้งแต่รอบนี้ —
    /// ฝั่งอ่านคืน ciphertext นั้นตามเดิม <b>ไม่แกะ</b> (ปิด oracle รอบ 231 S2 ต่อไป) · แถวซ้อนที่ไม่มีเครื่องหมาย = ของเก่าจากช่วงรอบ 231–274 ⇒ แกะได้</summary>
    internal const string UserCipherMarker = "\u0001pii-user-cipher:";

    /// <summary><b>ตัวตัดสินเดียว</b>ของ "ค่านี้ในคอลัมน์ PII ถอดด้วยคีย์ไหน" — pure (คีย์ส่งเข้ามา) ⇒ ฝั่งอ่านของ EF (<see cref="DecryptOnRead"/>) ·
    /// งานย้ายคีย์ · รายงานแถวเสีย และเทสต์เรียกตัวเดียวกัน · ลำดับ: คีย์ปัจจุบันก่อน แล้วคีย์เดิมตามลำดับ</summary>
    public static Inspection Inspect(string? stored, string currentKey, IReadOnlyList<string> previousKeys, PiiValueKind kind = PiiValueKind.Text)
    {
        if (string.IsNullOrEmpty(stored)) return new(PiiCipherState.Empty, stored);
        var prefixed = EncryptionHelper.IsEncrypted(stored);
        if (!prefixed && !EncryptionHelper.LooksLikeLegacyCiphertext(stored)) return new(PiiCipherState.Plaintext, stored);
        Inspection outer;
        if (TryDecrypt(stored, currentKey, out var p)) outer = new(PiiCipherState.Current, p);
        else
        {
            outer = default;
            for (var i = 0; i < previousKeys.Count && outer.State == PiiCipherState.Empty; i++)
                if (TryDecrypt(stored, previousKeys[i], out p)) outer = new(PiiCipherState.Previous, p, i);
            // รอบ 274 (ฝ่ายค้าน MED-1): ไม่มีคำนำหน้าแต่รูปเป็น ciphertext รุ่นก่อน (base64 ≥ 40 ตัว ≥ nonce+tag) และถอดไม่ออกทุกคีย์ = <b>ถอดไม่ได้</b>
            // (เดิม = plaintext ⇒ ผู้เรียกตัดตัวเลขจาก base64 ลงแบบ) · คอลัมน์ PII เป็นเลขสั้น/JSON — plaintext จริงไม่มีรูปนี้
            if (outer.State == PiiCipherState.Empty) return new(PiiCipherState.Undecryptable, stored);
        }
        return Unwrap(outer, currentKey, previousKeys, kind);
    }

    /// <summary>รอบ 274-10 — <b>แกะชั้นซ้อน</b> (ที่มา: ช่วงที่ถอดคีย์เดิมไม่ได้ API คืน ciphertext ดิบ แล้วทางเข้าที่ไม่มีด่าน (สร้างพนักงาน · HRIS sync · นำเข้าไฟล์ที่ส่งออกไว้)
    /// เขียนสตริงนั้นกลับลงช่อง ⇒ converter เข้ารหัสซ้ำด้วยคีย์ปัจจุบัน) · ถอดชั้นในด้วยชุดคีย์เดียวกัน (ปัจจุบัน → เดิม) ≤ <see cref="MaxNestedDepth"/> ชั้น ·
    /// รับค่าที่แกะได้เฉพาะเมื่อ "ใช้ได้จริง" (<see cref="AcceptsUnwrapped"/>) — ไม่ผ่าน = คงสถานะเดิม + <c>NestedUnresolved</c> (ห้ามแต่งค่า)</summary>
    private static Inspection Unwrap(Inspection outer, string currentKey, IReadOnlyList<string> previousKeys, PiiValueKind kind)
    {
        var plain = outer.Plain!;
        if (plain.StartsWith(UserCipherMarker, StringComparison.Ordinal))
            return outer with { Plain = plain[UserCipherMarker.Length..], NestedUnresolved = true };   // ciphertext ที่ผู้ใช้ส่งมา — ไม่แกะ
        if (!IsUnreadable(plain)) return outer;
        var inner = plain;
        var layers = 0;
        while (IsUnreadable(inner) && layers < MaxNestedDepth)
        {
            if (!TryDecryptAny(inner.Trim(), currentKey, previousKeys, out var next)) break;
            inner = next;
            layers++;
        }
        if (layers == 0 || !AcceptsUnwrapped(inner, kind)) return outer with { NestedUnresolved = true };
        return new(PiiCipherState.Nested, inner, outer.KeyIndex, layers);
    }

    /// <summary>รอบ 274-10: ค่าที่แกะจากชั้นซ้อนใช้ได้ไหม — ยังเป็น ciphertext/มีเครื่องหมายผู้ใช้ = ไม่ได้ ·
    /// เลขประจำตัวผู้เสียภาษี = ตัวเลข (ขีด/ช่องว่างได้) 13 หลักที่ผ่าน mod-11 เท่านั้น</summary>
    private static bool AcceptsUnwrapped(string? value, PiiValueKind kind)
    {
        if (string.IsNullOrWhiteSpace(value) || IsUnreadable(value) || value.StartsWith(UserCipherMarker, StringComparison.Ordinal)) return false;
        if (kind != PiiValueKind.ThaiTaxId) return true;
        return value.All(c => char.IsAsciiDigit(c) || c == '-' || c == ' ') && ThaiTaxId.IsValid(value);
    }

    private static bool TryDecryptAny(string stored, string currentKey, IReadOnlyList<string> previousKeys, out string plain)
    {
        if (TryDecrypt(stored, currentKey, out plain)) return true;
        foreach (var k in previousKeys)
            if (TryDecrypt(stored, k, out plain)) return true;
        return false;
    }

    /// <summary>รอบ 274: ค่าที่ <b>ได้มาจาก</b> converter แล้วยังเป็น ciphertext (enc1 หรือรุ่นก่อนไม่มีคำนำหน้า) = ถอดรหัสไม่ได้ ·
    /// ตัวตัดสินเดียวของผู้อ่านปลายทาง (ไฟล์ยื่น · ไฟล์ สปส. · ไฟล์โอน · export · import matching) — ห้ามตัดตัวเลขจากค่านี้</summary>
    public static bool IsUnreadable(string? valueFromEntity)
    {
        var t = valueFromEntity?.Trim();
        return !string.IsNullOrEmpty(t) && (EncryptionHelper.IsEncrypted(t) || EncryptionHelper.LooksLikeLegacyCiphertext(t));
    }

    /// <summary>รอบ 274 (ฝ่ายค้าน LOW-3): ค่าที่ส่งออกไฟล์/ตาราง (export · แม่แบบนำเข้า) — ถอดรหัสไม่ได้ ⇒ ป้าย ไม่ส่ง ciphertext ดิบ</summary>
    public const string UnreadableExportText = "⛔ ถอดรหัสไม่ได้";
    public static string? ForExport(string? valueFromEntity) => IsUnreadable(valueFromEntity) ? UnreadableExportText : valueFromEntity;

    /// <summary>รอบ 274 (ฝ่ายค้าน LOW-1): ผู้รับคำเตือนเมื่อถอดด้วย dev key — Program.cs ต่อ logger (production) · null = ไม่เตือน</summary>
    public static Action<long>? DevKeyUseWarning { get; set; }
    private static long _devKeyReads;
    private static long _devKeyLastWarnTicks;
    /// <summary>จำนวนครั้งที่อ่านค่าด้วย dev key ตั้งแต่บูต</summary>
    public static long DevKeyReads => System.Threading.Interlocked.Read(ref _devKeyReads);
    internal static readonly TimeSpan DevKeyWarnInterval = TimeSpan.FromHours(1);

    private static void NoteDevKeyRead()
    {
        var n = System.Threading.Interlocked.Increment(ref _devKeyReads);
        var now = DateTime.UtcNow.Ticks;
        var last = System.Threading.Interlocked.Read(ref _devKeyLastWarnTicks);
        // จำกัดความถี่: ครั้งแรก + ไม่เกินชั่วโมงละครั้ง (ห้าม log spam)
        if (last != 0 && now - last < DevKeyWarnInterval.Ticks) return;
        if (System.Threading.Interlocked.CompareExchange(ref _devKeyLastWarnTicks, now, last) != last) return;
        DevKeyUseWarning?.Invoke(n);
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
        if (IsUnreadable(value) && PassThrough.ContainsKey(Fingerprint(value))) return value;   // ค่าเดิมที่อ่านมาแล้วถอดไม่ออก — คงไว้
        // รอบ 274-10: ciphertext ที่ผู้ใช้/ทางเข้าส่งมาเป็นค่า (ด่านข้อความไทยอยู่ที่ EmployeeRecordEdit.CipherInputProblem) — ห่อพร้อมเครื่องหมาย
        // ⇒ ฝั่งอ่านไม่แกะ (Unwrap) · ไม่มีเครื่องหมาย = แถวซ้อนรุ่นเก่าที่แกะได้
        if (IsUnreadable(value)) return EncryptionHelper.Encrypt(UserCipherMarker + value, _key);
        return EncryptionHelper.Encrypt(value, _key);
    }

    /// <summary>มีคำนำหน้า ⇒ ถอดด้วยคีย์ปัจจุบัน แล้วคีย์เดิม (รอบ 274) · ถอดไม่ออกทุกคีย์ = คืนค่าเดิม (ผู้ดูแลเห็น enc1:… — ไม่ล้มทั้งแถว ·
    /// <b>ผู้อ่านเลขประจำตัวต้องผ่าน <see cref="EmployeeTaxIdentity.Inspect"/> ซึ่งปฏิเสธค่า enc1:… ไม่ตัดเอาแต่ตัวเลข</b>) ·
    /// ไม่มีคำนำหน้าแต่ดูเหมือน ciphertext รุ่นก่อนรอบ 231 ⇒ ลองถอดชุดเดียวกัน (ได้ = ciphertext เก่า · บันทึกครั้งถัดไปจะได้คำนำหน้า) ·
    /// อื่น ๆ = plaintext เก่า pass-through (รูปแบบเดียวกับ <c>SecretProtector.Inspect</c>)</summary>
    [return: NotNullIfNotNull(nameof(value))]
    private static string? DecryptOnRead(string? value, PiiValueKind kind)
    {
        var r = Inspect(value, _key, _previousKeys, kind);
        if (r.State == PiiCipherState.Undecryptable)
        {
            // ciphertext เสียหาย / คีย์ถูกหมุนโดยไม่ตั้ง ENCRYPTION_KEY_PREVIOUS ⇒ คืนค่าเดิม + จำไว้ให้ EncryptOnSave ปล่อยผ่าน (ไม่ห่อซ้อน)
            if (PassThrough.Count >= MaxPassThrough) PassThrough.Clear();
            PassThrough.TryAdd(Fingerprint(value!), 0);
            return value;
        }
        if (r.KeyIndex >= 0 && _previousKeys[r.KeyIndex] == EncryptionKeyResolver.DevKey) NoteDevKeyRead();
        // รอบ 274-10: ชั้นในใช้ไม่ได้ ⇒ คืน ciphertext ชั้นใน (ผู้อ่านปลายทางเห็น IsUnreadable) · ไม่ใส่ PassThrough: ถ้า EF เขียนค่านี้กลับ
        // จะถูกห่อพร้อมเครื่องหมาย (ความหมายเดิม — ยังใช้ไม่ได้) ไม่ใช่ถูกปล่อยเป็น ciphertext ชั้นในดิบที่อาจถอดได้ (oracle)
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
    /// <summary>รอบ 274-10: เข้ารหัสซ้อน (ciphertext ถูกเข้ารหัสอีกชั้น) และแกะได้เป็นค่าที่ใช้ได้ — อ่านถูกแล้ว ·
    /// งานย้ายคีย์เขียนใหม่เป็นชั้นเดียวบนคีย์ปัจจุบัน</summary>
    Nested,
}

/// <summary>รอบ 274-10: ชนิดค่าของคอลัมน์ PII — ใช้ตัดสินค่าที่แกะจากชั้นซ้อน (<see cref="EncryptedColumnConverter.AcceptsUnwrapped"/>)</summary>
public enum PiiValueKind
{
    /// <summary>ข้อความทั่วไป (เลขบัญชี/ประกันสังคม/พาสปอร์ต/JSON) — ใช้ได้เมื่อไม่ใช่ ciphertext อีก</summary>
    Text,
    /// <summary>เลขประจำตัวผู้เสียภาษี/เลขบัตรประชาชน — ต้องเป็น 13 หลักที่ผ่าน mod-11</summary>
    ThaiTaxId,
}

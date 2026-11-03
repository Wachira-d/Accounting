using System.Security.Cryptography;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 231 — รูปแบบ ciphertext มีคำนำหน้า <c>enc1:</c> · "เข้ารหัสแล้ว" ตัดสินจากคำนำหน้าเท่านั้น
///
/// ═══ บั๊ก production ที่เป็นที่มา ═══
/// เจ้าของกรอก Channel Access Token ของ LINE OA กลาง (base64 ยาว ~170 ตัว) ⇒ <c>EncryptionHelper.IsEncrypted</c> รุ่นเดิม
/// (เดาจาก "ยาว ≥ 40 + เป็น base64") ตอบ true ⇒ <c>SecretProtector.Protect</c> "ไม่เข้ารหัสซ้ำ" เก็บค่า<b>ดิบ</b> ⇒ ตอนอ่าน
/// <c>Unprotect/IsUsable</c> พยายามถอดค่าดิบ ⇒ ล้มทุกคีย์ ⇒ <c>NextAccLineConfig</c> ทิ้งค่าตั้งแอดมินทั้งชุด (รวม secret) ⇒ webhook 401
/// และหน้าจอฟ้อง "คีย์เข้ารหัสของระบบถูกเปลี่ยน" ทั้งที่คีย์ไม่เคยเปลี่ยน (เครื่อง dev ไม่มี ENCRYPTION_KEY)
///
/// เทสต์ล็อกทั้งสองทิศ: ค่าที่เคยพังกลับมาใช้ได้ (token ดิบที่ค้างในฐาน · token ใหม่ที่กรอก) และค่าที่ถูกอยู่แล้ว (ciphertext เก่าไม่มีคำนำหน้า ·
/// ciphertext คีย์เดิม · plaintext เก่า) ไม่ถูกแตะให้ผิด · และข้อความ "คีย์ถูกเปลี่ยน" ออกเฉพาะกรณีที่ตรวจพบจริง (กฎ F2 ข้อ 7)
/// </summary>
public class EncryptionFormatRound231Tests
{
    private const string KeyA = "r231-key-A-0123456789-abcdefghijklmnop";
    private const string KeyB = "r231-key-B-9876543210-zyxwvutsrqponmlk";

    /// <summary>รูปร่าง token ของ LINE (long-lived channel access token): base64 ยาว ~170 ตัว ลงท้าย '=' — deterministic เพื่อให้เทสต์ซ้ำได้</summary>
    private static readonly string LineShapedToken =
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string('k', 128)));   // 172 ตัว ลงท้าย "=" (base64 ถูกต้อง)

    /// <summary>ciphertext รุ่นก่อนรอบ 231 = ผลของ Encrypt โดยไม่มีคำนำหน้า</summary>
    private static string LegacyCipher(string plain, string key)
        => EncryptionHelper.Encrypt(plain, key)[EncryptionHelper.CipherPrefix.Length..];

    [Fact]
    public void รูปร่าง_token_ของ_LINE_ต้องผ่านการเดาแบบเดิม_จึงเป็นที่มาของบั๊ก()
    {
        // ยืนยันต้นเหตุ: ค่านี้คือสิ่งที่ heuristic เดิมเข้าใจผิดว่าเป็น ciphertext
        Assert.True(LineShapedToken.Length >= 40);
        Assert.True(EncryptionHelper.LooksLikeLegacyCiphertext(LineShapedToken));
        // แต่ตัวตัดสินฝั่งเขียน (คำนำหน้า) ต้องตอบว่า "ยังไม่ได้เข้ารหัส"
        Assert.False(EncryptionHelper.IsEncrypted(LineShapedToken));
    }

    // ───────── (1) token ใหม่ที่กรอก: Protect ต้องเข้ารหัสจริง แล้ว round-trip ได้ ─────────

    [Fact]
    public void token_รูปร่าง_LINE_เข้ารหัสจริง_ถอดกลับได้_และใช้ได้()
    {
        var p = new SecretProtector(KeyA);
        var stored = p.Protect(LineShapedToken)!;
        Assert.NotEqual(LineShapedToken, stored);                       // เดิม: เท่ากัน (เก็บดิบ)
        Assert.StartsWith(EncryptionHelper.CipherPrefix, stored);
        Assert.Equal(LineShapedToken, p.Unprotect(stored));
        Assert.True(p.IsUsable(stored));
        Assert.Equal(SecretState.Current, p.Inspect(stored));
        Assert.Null(SecretWarnings.UnusableReason(p, stored));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5QUJDREVGR0g=")]   // base64 ยาว 60 ตัว (ผ่านการเดาแบบเดิม)
    [InlineData("1234567890123456789012345678901234567890123456")]                 // ตัวเลขล้วน 46 ตัว
    [InlineData("whsec_0123456789abcdef0123456789abcdef0123456789abcdef")]      // webhook secret
    public void Protect_ไม่มีวันคืนค่าที่ไม่มีคำนำหน้ากลับไปเก็บดิบ(string input)
    {
        var stored = new SecretProtector(KeyA).Protect(input)!;
        Assert.NotEqual(input, stored);
        Assert.True(EncryptionHelper.IsEncrypted(stored));
        Assert.Equal(input, new SecretProtector(KeyA).Unprotect(stored));
    }

    [Fact]
    public void Protect_ปฏิเสธค่าที่ขึ้นต้น_enc1_ปิด_oracle()
    {
        // S2: ผู้ใช้ที่เขียนช่อง A ได้ ส่ง enc1:<ciphertext ของช่อง B> มา ⇒ ระบบต้องไม่เก็บตามนั้นแล้วถอดให้ตอนอ่าน
        var p = new SecretProtector(KeyA);
        var other = p.Protect("secret-of-other-field")!;
        var ex = Assert.Throws<BusinessRuleException>(() => p.Protect(other));
        Assert.Equal(SecretInputRule.RuleCipherPrefix, ex.RuleCode);
        Assert.Equal(400, ex.StatusCode);
        Assert.Throws<BusinessRuleException>(() => p.Protect(EncryptionHelper.CipherPrefix + "anything"));
        // ทิศตรงข้าม: ค่าที่ขึ้นต้นคล้ายแต่ไม่ใช่คำนำหน้า ผ่านตามปกติ
        Assert.Equal("enc1x", p.Unprotect(p.Protect("enc1x")));
        Assert.Equal("enc:v1:abc", p.Unprotect(p.Protect("enc:v1:abc")));
    }

    [Fact]
    public void SecretInputRule_ยาวเกิน_ปฏิเสธ_และคอลัมน์_2000_รับ_ciphertext_ของค่าสูงสุดได้()
    {
        var max = new string('a', SecretInputRule.MaxPlaintextBytes);
        var cipher = new SecretProtector(KeyA).Protect(max)!;
        Assert.True(cipher.Length <= 2000, $"ciphertext {cipher.Length} ตัว เกิน varchar(2000)");
        var ex = Assert.Throws<BusinessRuleException>(() => new SecretProtector(KeyA).Protect(max + "a"));
        Assert.Equal(SecretInputRule.RuleTooLong, ex.RuleCode);
        // ไทย 3 ไบต์/ตัว — นับเป็นไบต์
        Assert.Throws<BusinessRuleException>(() => new SecretProtector(KeyA).Protect(new string('ก', 500)));
        Assert.NotNull(new SecretProtector(KeyA).Protect(new string('ก', 400)));
        // PII: ข้อความปฏิเสธชี้ช่อง · ค่าปกติ null
        Assert.Contains("เลขบัตรประชาชน", SecretInputRule.CipherPrefixProblem(EncryptionHelper.CipherPrefix + "x", "เลขบัตรประชาชน"));
        Assert.Null(SecretInputRule.CipherPrefixProblem("1234567890121", "เลขบัตรประชาชน"));
        Assert.Contains("enc1:", EmployeeRecordEdit.ThaiIdNumber(EncryptionHelper.CipherPrefix + "AAAA", null, "เลขบัตรประชาชน").Error);
    }

    // ───────── (2) token ดิบที่ค้างในฐาน (ผลของบั๊ก) ต้องใช้ได้ทันทีโดยไม่ต้องกรอกใหม่ ─────────

    [Fact]
    public void token_ดิบที่รุ่นก่อนเก็บไว้_ต้องใช้ได้ตามเดิม_และไม่ฟ้องว่าคีย์ถูกเปลี่ยน()
    {
        var p = new SecretProtector(KeyA, legacyKeys: new[] { EncryptionKeyResolver.DevKey });
        // ในฐานคือค่าดิบ (สิ่งที่ Protect รุ่นเดิมคืนกลับมา)
        Assert.True(p.IsUsable(LineShapedToken));                       // เดิม: false ⇒ ทิ้งค่าตั้งทั้งชุด
        Assert.Equal(LineShapedToken, p.Unprotect(LineShapedToken));     // เดิม: "" (ถอดค่าดิบไม่ออก)
        // S1: ดูเหมือน ciphertext เก่าแต่ถอดไม่ออก = "กำกวม" — ใช้ได้ แต่มีคำใบ้ (ไม่ใช่ข้อผิดพลาด · ไม่ใช่ "คีย์ถูกเปลี่ยน")
        Assert.Equal(SecretState.AmbiguousBase64, p.Inspect(LineShapedToken));
        Assert.Null(SecretWarnings.UnusableReason(p, LineShapedToken));
        Assert.Null(SecretWarnings.FieldWarning(p, "Channel Access Token", LineShapedToken));
        Assert.Null(SecretWarnings.Build(p, ("Channel Access Token", LineShapedToken)));
        var hint = SecretWarnings.FieldHint(p, "Channel Access Token", LineShapedToken)!;
        Assert.Contains("ENCRYPTION_KEY", hint);
        Assert.DoesNotContain("คีย์เข้ารหัสของระบบถูกเปลี่ยน", hint);
        Assert.Contains("Channel Access Token", SecretWarnings.BuildHints(p, ("Channel Access Token", LineShapedToken), ("Channel Secret", p.Protect("s"))));
        Assert.DoesNotContain("Channel Secret", SecretWarnings.BuildHints(p, ("Channel Access Token", LineShapedToken), ("Channel Secret", p.Protect("s"))));
    }

    [Fact]
    public void ciphertext_คีย์หาย_ไม่มีคำนำหน้า_ยังusableแต่มีคำใบ้()
    {
        // ทิศตรงข้ามของ S1: ค่านี้คือ ciphertext จริงของคีย์ที่ระบบไม่รู้จักแล้ว — แยกจาก token ดิบไม่ได้ ⇒ คำตัดสินเจ้าของ: ใช้เป็นค่าดิบ + คำใบ้ ไม่ปฏิเสธ
        var lost = LegacyCipher("secret-of-lost-key", "key-that-no-longer-exists");
        var p = new SecretProtector(KeyA, legacyKeys: new[] { KeyB });
        Assert.Equal(SecretState.AmbiguousBase64, p.Inspect(lost));
        Assert.True(p.IsUsable(lost));
        Assert.Equal(lost, p.Unprotect(lost));
        Assert.Null(SecretWarnings.UnusableReason(p, lost));
        Assert.NotNull(SecretWarnings.FieldHint(p, "x", lost));
        Assert.Null(SecretWarnings.FieldHint(p, "x", p.Protect("ok")));          // ค่าปกติไม่มีคำใบ้
        Assert.Null(SecretWarnings.FieldHint(p, "x", "short-plain"));             // plaintext ธรรมดาไม่มีคำใบ้
        Assert.Null(SecretWarnings.BuildHints(p, ("x", p.Protect("ok")), ("y", "short-plain")));
    }

    [Fact]
    public void ประตู_MigrateCipherPrefix_ปิด_ค่าไม่มีคำนำหน้าไม่ถูกห่อ_เปิดแล้วถูกห่อ()
    {
        // B1 forward-only: ค่าไม่มีคำนำหน้ายังอ่านได้ครบ ⇒ ไม่รีบห่อ (ห่อแล้ว rollback ไม่ได้) · default ปิด
        var closed = new SecretProtector(KeyA);
        var open = new SecretProtector(KeyA, migrateCipherPrefix: true);
        var legacyFormat = LegacyCipher("app-password", KeyA);
        foreach (var v in new[] { LineShapedToken, legacyFormat, "short-plain" })
        {
            Assert.Null(closed.Reprotect(v));                                // ปิด = ไม่แตะ (ทั้ง Ambiguous · LegacyFormat · Plaintext)
            var wrapped = open.Reprotect(v);
            Assert.NotNull(wrapped);
            Assert.True(EncryptionHelper.IsEncrypted(wrapped));
            Assert.Equal(closed.Unprotect(v), closed.Unprotect(wrapped));    // ค่าที่อ่านได้เท่าเดิม
            Assert.Equal(SecretState.Current, closed.Inspect(wrapped));
            Assert.Null(open.Reprotect(wrapped));                            // ห่อแล้วไม่ทำซ้ำ
        }
    }

    [Fact]
    public void คีย์เดิม_ถูกย้ายเสมอ_ไม่ขึ้นกับประตู()
    {
        var legacyKey = new SecretProtector(EncryptionKeyResolver.DevKey).Protect("sk")!;
        var closed = new SecretProtector(KeyA, legacyKeys: new[] { EncryptionKeyResolver.DevKey });
        Assert.NotNull(closed.Reprotect(legacyKey));
        Assert.NotNull(closed.Reprotect(LegacyCipher("sk", EncryptionKeyResolver.DevKey)));   // คีย์เดิม + ไม่มีคำนำหน้า ก็ย้าย
    }

    // ───────── (3) ciphertext เก่า (ไม่มีคำนำหน้า) ยังถอดได้ และถูกย้าย ─────────

    [Fact]
    public void ciphertext_รุ่นก่อน_ไม่มีคำนำหน้า_ยังถอดได้ด้วยคีย์ปัจจุบัน_และ_Reprotect_ติดคำนำหน้า()
    {
        var legacy = LegacyCipher("app-password-เดิม", KeyA);
        Assert.False(EncryptionHelper.IsEncrypted(legacy));
        Assert.True(EncryptionHelper.LooksLikeLegacyCiphertext(legacy));
        Assert.Equal("app-password-เดิม", EncryptionHelper.Decrypt(legacy, KeyA));   // Decrypt รับทั้งสองรูป

        var p = new SecretProtector(KeyA, migrateCipherPrefix: true);
        Assert.True(p.IsUsable(legacy));
        Assert.Equal("app-password-เดิม", p.Unprotect(legacy));
        Assert.Equal(SecretState.LegacyFormat, p.Inspect(legacy));
        Assert.Null(new SecretProtector(KeyA).Reprotect(legacy));             // ประตูปิด = ไม่แตะ
        var migrated = p.Reprotect(legacy);
        Assert.NotNull(migrated);
        Assert.StartsWith(EncryptionHelper.CipherPrefix, migrated);
        Assert.Equal("app-password-เดิม", new SecretProtector(KeyA).Unprotect(migrated));
        Assert.Null(p.Reprotect(migrated));
    }

    [Fact]
    public void ciphertext_รุ่นก่อน_คีย์เดิม_ยังถอดได้ผ่านคีย์เดิม_และย้ายไปคีย์ปัจจุบันพร้อมคำนำหน้า()
    {
        var legacy = LegacyCipher("sk_live_123", EncryptionKeyResolver.DevKey);
        var current = new SecretProtector(KeyA, legacyKeys: new[] { EncryptionKeyResolver.DevKey });
        Assert.Equal(SecretState.LegacyKey, current.Inspect(legacy));
        Assert.Equal("sk_live_123", current.Unprotect(legacy));
        var migrated = current.Reprotect(legacy)!;
        Assert.True(EncryptionHelper.IsEncrypted(migrated));
        Assert.Equal("sk_live_123", new SecretProtector(KeyA).Unprotect(migrated));   // ไม่ต้องพึ่งคีย์เดิมอีก
        // มีคำนำหน้า + คีย์เดิม ⇒ ยังถูกจัดเป็น LegacyKey และย้ายได้ (รอบ 219 B2 เดิม)
        var prefixedLegacy = new SecretProtector(EncryptionKeyResolver.DevKey).Protect("x")!;
        Assert.Equal(SecretState.LegacyKey, current.Inspect(prefixedLegacy));
        Assert.NotNull(current.Reprotect(prefixedLegacy));
    }

    // ───────── ทิศตรงข้าม: ค่าที่ถูกอยู่แล้วไม่ถูกแตะให้ผิด ─────────

    [Theory]
    [InlineData("legacy-plain")]
    [InlineData("สมชาย ใจดี")]
    [InlineData("x")]
    public void plaintext_เก่าที่ไม่ใช่_base64_ยังใช้ได้ตามเดิม(string plain)
    {
        var p = new SecretProtector(KeyA);
        Assert.Equal(plain, p.Unprotect(plain));
        Assert.True(p.IsUsable(plain));
        Assert.Equal(SecretState.Plaintext, p.Inspect(plain));
    }

    [Fact]
    public void ค่าที่อยู่บนคีย์ปัจจุบันและมีคำนำหน้าแล้ว_ไม่ถูกย้ายซ้ำ()
    {
        var p = new SecretProtector(KeyA, legacyKeys: new[] { KeyB });
        var cur = p.Protect("ok");
        Assert.Equal(SecretState.Current, p.Inspect(cur));
        Assert.Null(p.Reprotect(cur));
    }

    // ───────── (4) ข้อความ "คีย์ถูกเปลี่ยน" เฉพาะเมื่อตรวจพบจริง ─────────

    [Fact]
    public void มีคำนำหน้า_แต่คีย์ผิดทุกตัว_ใช้ไม่ได้_และข้อความบอกว่าคีย์ถูกเปลี่ยน()
    {
        var stored = new SecretProtector(KeyA).Protect("app-password")!;
        var rotated = new SecretProtector(KeyB, legacyKeys: new[] { EncryptionKeyResolver.DevKey });
        Assert.False(rotated.IsUsable(stored));
        Assert.Equal(string.Empty, rotated.Unprotect(stored));           // ไม่ให้ ciphertext หลุด
        Assert.Equal(SecretState.WrongKey, rotated.Inspect(stored));
        Assert.Null(rotated.Reprotect(stored));                           // ไม่แตะ — ผู้ใช้กรอกใหม่
        var why = SecretWarnings.UnusableReason(rotated, stored)!;
        Assert.Contains("คีย์เข้ารหัสของระบบถูกเปลี่ยน", why);
        var msg = SecretWarnings.Build(rotated, ("รหัสผ่าน SMTP", stored))!;
        Assert.Contains("รหัสผ่าน SMTP", msg);
        Assert.Contains("คีย์เข้ารหัสของระบบถูกเปลี่ยน", msg);
        Assert.Contains("กรอกค่าใหม่", msg);
        Assert.Contains("Authentication Required", msg);
        Assert.DoesNotContain("<", msg);
        Assert.Contains("คีย์เข้ารหัสของระบบถูกเปลี่ยน", SecretWarnings.FieldWarning(rotated, "Channel Access Token", stored));
    }

    [Fact]
    public void มีคำนำหน้าแต่เนื้อหาเสียหาย_ข้อความต้องบอกว่าเสียหาย_ไม่ใช่คีย์ถูกเปลี่ยน()
    {
        var p = new SecretProtector(KeyA);
        var broken = EncryptionHelper.CipherPrefix + "ไม่ใช่ base64 !!";
        var tooShort = EncryptionHelper.CipherPrefix + Convert.ToBase64String(new byte[10]);
        foreach (var v in new[] { broken, tooShort })
        {
            Assert.False(p.IsUsable(v));
            Assert.Equal(string.Empty, p.Unprotect(v));
            Assert.Equal(SecretState.Malformed, p.Inspect(v));
            Assert.Null(p.Reprotect(v));
            var why = SecretWarnings.UnusableReason(p, v)!;
            Assert.Contains("เสียหาย", why);
            Assert.DoesNotContain("คีย์เข้ารหัสของระบบถูกเปลี่ยน", why);
            Assert.False(EncryptionHelper.IsWellFormedCiphertext(v));
        }
        Assert.True(EncryptionHelper.IsWellFormedCiphertext(p.Protect("ok")));
    }

    [Fact]
    public void ข้อความ_LINE_OA_กลาง_ใช้เหตุที่ตรวจจริง()
    {
        var p = new SecretProtector(KeyA);
        // token ดิบ ⇒ ใช้ได้ ⇒ Merge ไม่ได้รับเหตุ ⇒ ใช้ชุดแอดมินตามปกติ (เดิม: ทิ้งทั้งชุด + "คีย์ถูกเปลี่ยน")
        Assert.Null(SecretWarnings.UnusableReason(p, LineShapedToken));
        var eff = NextAccLineConfigRule.Merge(new NextAccLineValues(p.Unprotect(LineShapedToken), "S", "@oa", null, null), null, null);
        Assert.Equal(NextAccLineConfigRule.SourceAdmin, eff.Source);
        Assert.Equal(LineShapedToken, eff.AccessToken);
        // คีย์ผิดจริง ⇒ ประโยคอ้างเหตุนั้น
        var stored = new SecretProtector(KeyB).Protect("tok")!;
        var reason = SecretWarnings.UnusableReason(p, stored)!;
        var problem = NextAccLineConfigRule.Merge(new NextAccLineValues(null, "S", "@oa", null, null), null, reason).Problem!;
        Assert.Contains("คีย์เข้ารหัสของระบบถูกเปลี่ยน", problem);
        Assert.Contains("LINE OA กลาง", problem);
    }

    [Fact]
    public void secret_ใช้ไม่ได้_ชุดแอดมินไม่ถูกใช้_และบอกเหตุ_ไม่ปล่อย_webhook_401_เงียบ()
    {
        // S3: token ดี secret เสีย ⇒ เดิมใช้ชุดแอดมิน (ส่งได้ แต่ webhook ถูกปฏิเสธทุกครั้งโดยไม่มีใครบอก)
        var r = NextAccLineConfigRule.Merge(new NextAccLineValues("ADMIN-TOKEN", null, "@oa", null, null),
            new NextAccLineValues("CFG-TOKEN", "CFG-SECRET", "@cfg", null, null), null, "เหตุของ secret");
        Assert.False(r.HasToken);
        Assert.Equal(NextAccLineConfigRule.SourceNone, r.Source);                 // ไม่ถอยไป config (อาจเป็นคนละ OA)
        Assert.Contains("Channel Secret", r.Problem);
        Assert.Contains("(เหตุของ secret)", r.Problem);
        Assert.Contains("webhook", r.Problem);
        // ทิศตรงข้าม: ไม่มีเหตุทั้งคู่ ⇒ ชุดแอดมินตามปกติ
        Assert.Equal(NextAccLineConfigRule.SourceAdmin, NextAccLineConfigRule.Merge(new NextAccLineValues("T", "S", "@oa", null, null), null).Source);
    }

    // ───────── (5) คอลัมน์ PII (EF converter) ใช้รูปแบบเดียวกัน ─────────

    private static string? ToDb(string? v) => (string?)new EncryptedColumnConverter().ConvertToProvider(v);
    private static string? FromDb(string? v) => (string?)new EncryptedColumnConverter().ConvertFromProvider(v);

    [Fact]
    public void converter_PII_ที่บังเอิญเป็น_base64_ยาว_ต้องถูกเข้ารหัสจริงและถอดกลับได้()
    {
        var stored = ToDb(LineShapedToken)!;
        Assert.NotEqual(LineShapedToken, stored);                        // เดิม: เก็บดิบตลอดกาล
        Assert.True(EncryptionHelper.IsEncrypted(stored));
        Assert.Equal(LineShapedToken, FromDb(stored));
        // S2: ค่า enc1:… ที่ไม่ได้มาจากการอ่านแถวนี้ (ผู้ใช้ส่งมา) ⇒ ห่อซ้อน ไม่ปล่อยผ่าน ⇒ อ่านกลับได้สตริง enc1:… ไม่ใช่ token ที่ถอดแล้ว
        var userSupplied = ToDb(stored)!;
        Assert.NotEqual(stored, userSupplied);
        Assert.Equal(stored, FromDb(userSupplied));
    }

    [Fact]
    public void converter_ค่าที่อ่านมาแล้วถอดไม่ออก_บันทึกแถวเดิมต้องคงค่าไว้_ไม่ห่อซ้อน()
    {
        // ทิศตรงข้ามของ S2: EF อ่านค่า enc1 ที่ถอดไม่ออก (คีย์ถูกหมุน) แล้วบันทึกแถวเดิม (แก้ช่องอื่น) ⇒ ค่าเดิมต้องถูกส่งกลับไปทั้งก้อน
        var wrongKey = EncryptionHelper.Encrypt("x", "some-unknown-key-" + Guid.NewGuid());
        Assert.Equal(wrongKey, FromDb(wrongKey));                        // อ่าน: คืนค่าเดิม (ผู้ดูแลเห็นว่าต้อง re-key)
        Assert.Equal(wrongKey, ToDb(wrongKey));                          // บันทึก: ผ่านทั้งก้อน (ไม่ห่อซ้อน)
        // ค่า enc1 ที่ไม่เคยถูกอ่านผ่าน converter ในโพรเซสนี้ = ผู้ใช้ส่งมา ⇒ ห่อ
        var fresh = EncryptionHelper.Encrypt("y", "another-unknown-key-" + Guid.NewGuid());
        Assert.NotEqual(fresh, ToDb(fresh));
    }

    [Fact]
    public void mask_PII_ไม่พังเมื่อค่าเป็น_enc1()
    {
        // N3: ถ้าคอลัมน์ถอดไม่ออก (คืน enc1:…) ตัวปิดบังต้องไม่โยน และต้องไม่เผย ciphertext ทั้งก้อน
        var v = EncryptionHelper.Encrypt("1234567890121", "unknown-key-mask");
        foreach (var masked in new[] { PiiMask.CitizenId(v), PiiMask.TaxId(v), PiiMask.Passport(v), PiiMask.BankAccountNo(v) })
        {
            Assert.NotNull(masked);
            Assert.NotEqual(v, masked);
        }
    }

    [Fact]
    public void converter_อ่านแถวรุ่นก่อน_ciphertext_ไม่มีคำนำหน้า_ได้_plaintext_และ_plaintext_base64_ผ่านตรงๆ()
    {
        // converter ใช้คีย์ static ของมันเอง (dev key ถ้าไม่ Configure) — สร้าง ciphertext เก่าด้วยคีย์เดียวกับที่ converter ถอด
        var probe = ToDb("probe")!;
        var key = new[] { EncryptionKeyResolver.DevKey, KeyA, KeyB }.First(k => { try { return EncryptionHelper.Decrypt(probe, k) == "probe"; } catch { return false; } });
        var legacyCipher = LegacyCipher("1234567890121", key);
        Assert.Equal("1234567890121", FromDb(legacyCipher));             // ciphertext เก่าถอดได้
        Assert.Equal(LineShapedToken, FromDb(LineShapedToken));          // base64 ยาวที่ไม่ใช่ ciphertext = plaintext ผ่านตรง ๆ
        Assert.Equal("1234567890121", FromDb("1234567890121"));          // plaintext สั้นผ่านตรง ๆ
        var wrongKeyPrefixed = EncryptionHelper.Encrypt("x", "some-unknown-key");
        Assert.Equal(wrongKeyPrefixed, FromDb(wrongKeyPrefixed));        // ถอดไม่ออก = คืนค่าเดิม (ผู้ดูแลเห็น enc1:… ว่าต้อง re-key)
    }

    // ───────── ทางเข้าที่เขียนด้วย EncryptionHelper ตรง (SitePaymentGateway) ใช้รูปแบบเดียวกับ SecretProtector ─────────

    [Fact]
    public void Encrypt_ตรง_ได้คำนำหน้า_และ_SecretProtector_คีย์เดียวกันถอดได้()
    {
        var cipher = EncryptionHelper.Encrypt("omise-secret", KeyA);
        Assert.True(EncryptionHelper.IsEncrypted(cipher));
        Assert.Equal("omise-secret", new SecretProtector(KeyA).Unprotect(cipher));
        Assert.Null(new SecretProtector(KeyA).Reprotect(cipher));
        Assert.ThrowsAny<CryptographicException>(() => EncryptionHelper.Decrypt(cipher, KeyB));
    }

    [Fact]
    public void Decrypt_ค่าที่ไม่ใช่_base64_ต้องโยน_CryptographicException_ไม่ใช่_FormatException()
    {
        Assert.Throws<CryptographicException>(() => EncryptionHelper.Decrypt("ไม่ใช่ base64", KeyA));
        Assert.Throws<CryptographicException>(() => EncryptionHelper.Decrypt(EncryptionHelper.CipherPrefix + "!!", KeyA));
    }
}

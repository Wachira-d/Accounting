using System.Security.Cryptography;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// PDPA ม.26 — encrypt-at-rest ของ PII (เลขบัตรประชาชน / passport / เลขบัญชี /
/// เลขประกันสังคม / เลขผู้เสียภาษีพนักงาน)
///
/// ═══ ทำไมต้องมีเทสต์ชุดนี้ ═══
/// CLAUDE.md กฎเหล็ก #4 G: <b>"Control เชิง compliance (hash chain, retention,
/// encryption) ต้องมี round-trip test — control ที่ไม่มีเทสต์ยืนยัน = ไม่มี
/// control"</b> — ที่มาคือ hash chain ที่พังเงียบ ๆ เพราะไม่มีเทสต์เดียวที่
/// write→verify. ชั้น encryption อยู่ในสถานะเดียวกันมาตลอด: โค้ดครบ แต่ไม่มีอะไร
/// พิสูจน์ว่ามันเข้ารหัสจริง ถอดกลับได้จริง และไม่พังเมื่อ EF บันทึกซ้ำ
///
/// สมบัติที่ล็อกไว้ที่นี่ ทุกข้อคือสิ่งที่ถ้าพังแล้ว <b>เงียบ</b>:
/// <list type="number">
/// <item>ถอดกลับได้ตรงตัว (ไม่งั้นข้อมูลพนักงานหายทั้งบริษัท)</item>
/// <item><b>idempotent</b> — EF บันทึกซ้ำต้องไม่เข้ารหัสซ้อน (ซ้อนแล้วถอดกลับได้
///   แค่ชั้นเดียว = ได้ ciphertext ชั้นในเป็นคำตอบ)</item>
/// <item>ciphertext ต่างจาก plaintext และ <b>ต่างกันทุกครั้ง</b> (nonce สุ่ม —
///   ถ้าเหมือนกันทุกครั้ง ผู้ที่เห็นฐานข้อมูลจะรู้ว่าสองแถวมีค่าเดียวกัน)</item>
/// <item>คีย์ผิด/ข้อมูลถูกแก้ = ถอดไม่ได้ (พิสูจน์ว่าเข้ารหัสด้วยกุญแจจริง
///   และ GCM tag ทำงาน — ไม่ใช่แค่ encode)</item>
/// <item>plaintext เก่า (แถวก่อนเปิด encryption) ต้อง <b>ไม่</b> ถูกเข้าใจผิดว่า
///   เป็น ciphertext ไม่งั้นจะถูกข้ามการเข้ารหัสไปตลอดกาล</item>
/// <item>ความยาวผลลัพธ์ต้องพอดี <c>HasMaxLength(200)</c> ที่ตั้งไว้ใน DbContext</item>
/// </list>
/// </summary>
public class PiiEncryptionRoundTripTests
{
    // ค่าที่ใช้ในเทสต์เท่านั้น — ไม่ใช่กุญแจของระบบ
    private const string KeyA = "test-key-A-0123456789";
    private const string KeyB = "test-key-B-9876543210";

    /// <summary>ตัวอย่าง PII จริงตามรูปแบบไทย (ตัวเลขสมมติ)</summary>
    public static TheoryData<string> ThaiPiiSamples => new()
    {
        "1234567890121",              // เลขบัตรประชาชน 13 หลัก
        "AA1234567",                  // passport
        "1234567890",                 // เลขบัญชีธนาคาร
        "0105556091234",              // เลขผู้เสียภาษี
        "สมชาย ใจดี",                  // ข้อความไทย (UTF-8 หลายไบต์ต่อตัวอักษร)
        "x",                          // สั้นสุด
    };

    [Theory]
    [MemberData(nameof(ThaiPiiSamples))]
    public void เข้ารหัสแล้วถอดกลับต้องได้ค่าเดิมเป๊ะ(string plain)
    {
        var cipher = EncryptionHelper.Encrypt(plain, KeyA);
        Assert.Equal(plain, EncryptionHelper.Decrypt(cipher, KeyA));
    }

    [Theory]
    [MemberData(nameof(ThaiPiiSamples))]
    public void ciphertext_ต้องไม่ใช่ค่าเดิม(string plain)
        => Assert.NotEqual(plain, EncryptionHelper.Encrypt(plain, KeyA));

    [Fact]
    public void ค่าเดียวกันเข้ารหัสสองครั้งต้องได้ไม่เหมือนกัน_nonce_สุ่ม()
    {
        // ถ้าเหมือนกัน = คนที่เห็นฐานข้อมูลรู้ทันทีว่าสองแถวมีเลขบัตรเดียวกัน
        var a = EncryptionHelper.Encrypt("1234567890121", KeyA);
        var b = EncryptionHelper.Encrypt("1234567890121", KeyA);
        Assert.NotEqual(a, b);
        // แต่ถอดกลับได้ค่าเดียวกันทั้งคู่
        Assert.Equal(EncryptionHelper.Decrypt(a, KeyA), EncryptionHelper.Decrypt(b, KeyA));
    }

    [Fact]
    public void คีย์ผิดต้องถอดไม่ได้()
    {
        var cipher = EncryptionHelper.Encrypt("1234567890121", KeyA);
        Assert.Throws<CryptographicException>(() => EncryptionHelper.Decrypt(cipher, KeyB));
    }

    [Fact]
    public void ข้อมูลถูกแก้ต้องถอดไม่ได้_GCM_tag_ทำงาน()
    {
        var cipher = EncryptionHelper.Encrypt("1234567890121", KeyA);
        var bytes = Convert.FromBase64String(cipher);
        bytes[^1] ^= 0xFF;                       // พลิกไบต์สุดท้ายของ ciphertext
        var tampered = Convert.ToBase64String(bytes);
        Assert.Throws<CryptographicException>(() => EncryptionHelper.Decrypt(tampered, KeyA));
    }

    [Theory]
    [MemberData(nameof(ThaiPiiSamples))]
    public void plaintext_เก่าต้องไม่ถูกเข้าใจผิดว่าเป็น_ciphertext(string legacyPlain)
    {
        // ถ้า IsEncrypted ตอบ true กับ plaintext ⇒ EncryptOnSave จะข้ามการเข้ารหัส
        // ⇒ ค่านั้นอยู่ในฐานข้อมูลแบบ plaintext **ตลอดไป** โดยไม่มีใครรู้
        Assert.False(EncryptionHelper.IsEncrypted(legacyPlain));
    }

    [Theory]
    [MemberData(nameof(ThaiPiiSamples))]
    public void ciphertext_ต้องถูกจดจำได้ว่าเป็น_ciphertext(string plain)
        => Assert.True(EncryptionHelper.IsEncrypted(EncryptionHelper.Encrypt(plain, KeyA)));

    // ───────────────────────────────────────────────────────────────
    // ชั้น EF ValueConverter — ตัวที่ทำงานจริงตอน save/read
    // ───────────────────────────────────────────────────────────────

    private static string? ToDb(string? v)
        => (string?)new EncryptedColumnConverter().ConvertToProvider(v);
    private static string? FromDb(string? v)
        => (string?)new EncryptedColumnConverter().ConvertFromProvider(v);

    [Theory]
    [MemberData(nameof(ThaiPiiSamples))]
    public void converter_round_trip_ผ่านเส้นทางเดียวกับ_EF(string plain)
        => Assert.Equal(plain, FromDb(ToDb(plain)));

    [Theory]
    [MemberData(nameof(ThaiPiiSamples))]
    public void converter_ต้อง_idempotent_บันทึกซ้ำห้ามเข้ารหัสซ้อน(string plain)
    {
        // EF เรียก ConvertToProvider ทุกครั้งที่ save — ถ้าไม่ idempotent
        // การกดบันทึกครั้งที่สองจะได้ ciphertext ซ้อนสองชั้น แล้วอ่านกลับได้แค่
        // ชั้นเดียว = ผู้ใช้เห็น Base64 แทนเลขบัตร
        var once = ToDb(plain);
        var twice = ToDb(once);
        Assert.Equal(once, twice);
        Assert.Equal(plain, FromDb(twice));
    }

    [Fact]
    public void converter_ต้องปล่อย_null_และค่าว่างผ่านตรงๆ()
    {
        Assert.Null(ToDb(null));
        Assert.Null(FromDb(null));
        Assert.Equal("", ToDb(""));
        Assert.Equal("", FromDb(""));
    }

    [Fact]
    public void converter_อ่าน_plaintext_เก่าได้ไม่พัง()
    {
        // แถวที่บันทึกก่อนเปิด encryption ต้องอ่านได้ปกติ (lazy migration)
        Assert.Equal("1234567890121", FromDb("1234567890121"));
    }

    [Theory]
    [MemberData(nameof(ThaiPiiSamples))]
    public void ความยาว_ciphertext_ต้องไม่เกิน_HasMaxLength_200(string plain)
    {
        // DbContext ตั้ง HasMaxLength(200) ให้ทุกคอลัมน์ PII ที่เข้ารหัส —
        // เกินเมื่อไรคือ error ตอน insert ซึ่งจะโผล่เฉพาะบน production data
        var cipher = ToDb(plain)!;
        Assert.True(cipher.Length <= 200, $"ยาว {cipher.Length} ตัวอักษร (เกิน 200)");
    }

    [Fact]
    public void ค่ายาวสุดที่คอลัมน์รับได้_ยังต้องไม่ล้น_200()
    {
        // เผื่อค่าที่ยาวผิดปกติ (เช่นเลขบัญชีต่างประเทศ/IBAN) — 34 ตัวอักษร
        var iban = new string('9', 34);
        Assert.True(ToDb(iban)!.Length <= 200);
    }
}

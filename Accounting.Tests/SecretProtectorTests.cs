using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ความลับที่เก็บในฐานข้อมูล (รหัสผ่าน SMTP · client secret · refresh token)
///
/// ═══ เคสจริงที่ผู้ใช้รายงาน ═══
/// หน้าตั้งค่าอีเมล **ของแอดมิน** ส่งได้ แต่ **ของบริษัท** ขึ้น
/// "5.7.0 Authentication Required" ทั้งที่หน้าจอบอกว่ามีรหัสผ่านเก็บไว้แล้ว
///
/// ต้นเหตุ: ค่าที่เข้ารหัสด้วยคีย์ตัวเก่าถอดไม่ออกเมื่อ `Security:EncryptionKey`
/// ถูกเปลี่ยน — `Unprotect` คืน "" (กัน ciphertext หลุด) แล้วระบบเอาค่าว่างไป
/// authenticate ⇒ เซิร์ฟเวอร์ตอบ error ที่ไม่ได้ชี้ต้นเหตุ
///
/// ที่ทำให้แก้ไม่ได้เลย: คอลัมน์ไม่ว่าง ⇒ หน้าจอโชว์ "••••••• (เก็บไว้)" ⇒ ผู้ใช้
/// เว้นช่องว่างตามที่หน้าจอบอก ("เว้นว่าง = ใช้ค่าเดิม") ⇒ ค่าเสียไม่เคยถูกเขียนทับ
/// เทสต์ชุดนี้ล็อกว่า <c>IsUsable</c> แยก "มีค่า" ออกจาก "ใช้ได้จริง" ให้ถูก
/// </summary>
public class SecretProtectorTests
{
    private const string KeyA = "key-A-0123456789-abcdefghijklmnop";
    private const string KeyB = "key-B-9876543210-zyxwvutsrqponmlk";

    // ใช้ ctor ที่รับคีย์ตรง ๆ — จำลอง "เปลี่ยนคีย์เข้ารหัสของระบบ" ได้โดยไม่ต้อง
    // ยก IConfiguration/ILogger ทั้งชุดเข้ามาในโปรเจกต์เทสต์
    private static SecretProtector Make(string key) => new(key);

    // ───────── round-trip ปกติ ─────────

    [Fact]
    public void เข้ารหัสแล้วถอดกลับได้ค่าเดิม()
    {
        var p = Make(KeyA);
        var cipher = p.Protect("my-app-password");
        Assert.NotEqual("my-app-password", cipher);          // ต้องไม่เก็บ plaintext
        Assert.Equal("my-app-password", p.Unprotect(cipher));
        Assert.True(p.IsUsable(cipher));
    }

    [Fact]
    public void เข้ารหัสซ้ำไม่ทับซ้อน_ค่าที่เข้ารหัสแล้วส่งเข้าอีกรอบต้องไม่เปลี่ยน()
    {
        var p = Make(KeyA);
        var once = p.Protect("s3cret");
        Assert.Equal(once, p.Protect(once));
        Assert.Equal("s3cret", p.Unprotect(p.Protect(once)));
    }

    [Fact]
    public void ค่าเก่าที่ยังไม่ได้เข้ารหัส_ใช้ได้ตามเดิม()
    {
        // แถวเก่าก่อนมีการเข้ารหัส — ต้องไม่พังจนกว่าจะบันทึกใหม่
        var p = Make(KeyA);
        Assert.Equal("legacy-plain", p.Unprotect("legacy-plain"));
        Assert.True(p.IsUsable("legacy-plain"));
    }

    // ───────── เคสที่ทำให้อีเมลส่งไม่ออก ─────────

    [Fact]
    public void คีย์ถูกเปลี่ยนหลังบันทึก_ถอดไม่ออก_และต้องรายงานว่าใช้ไม่ได้()
    {
        var stored = Make(KeyA).Protect("app-password-เดิม");

        var afterKeyRotation = Make(KeyB);
        // Unprotect คืนค่าว่าง (กัน ciphertext หลุดออกไปหา client)
        Assert.Equal(string.Empty, afterKeyRotation.Unprotect(stored));
        // แต่ต้องบอกได้ว่า "ใช้ไม่ได้" ไม่งั้นหน้าจอจะโชว์ว่ามีรหัสผ่านเก็บไว้แล้ว
        Assert.False(afterKeyRotation.IsUsable(stored));
    }

    [Fact]
    public void IsUsable_แยกระหว่างมีค่ากับใช้ได้จริง()
    {
        var p = Make(KeyA);
        Assert.False(p.IsUsable(null));
        Assert.False(p.IsUsable(""));
        Assert.True(p.IsUsable(p.Protect("ok")));
        // ข้อมูลที่ดูเหมือน ciphertext ของเราแต่เสียหาย
        Assert.False(Make(KeyB).IsUsable(p.Protect("ok")));
    }

    // ───────── ข้อความเตือนที่ผู้ใช้ต้องเห็น ─────────

    [Fact]
    public void ไม่มีอะไรเสีย_ต้องไม่มีข้อความเตือน()
    {
        var p = Make(KeyA);
        Assert.Null(SecretWarnings.Build(p,
            ("รหัสผ่าน SMTP", p.Protect("ok")),
            ("Client Secret (Gmail)", null),
            ("Refresh Token (Gmail)", "")));
    }

    [Fact]
    public void มีค่าที่ถอดไม่ออก_ต้องบอกชื่อช่องและวิธีแก้()
    {
        var stored = Make(KeyA).Protect("เดิม");
        var now = Make(KeyB);

        var msg = SecretWarnings.Build(now,
            ("รหัสผ่าน SMTP", stored),
            ("Client Secret (Gmail)", now.Protect("ยังดีอยู่")));

        Assert.NotNull(msg);
        Assert.Contains("รหัสผ่าน SMTP", msg!);
        Assert.DoesNotContain("Client Secret (Gmail)", msg);   // ตัวที่ยังดีต้องไม่ถูกฟ้อง
        Assert.Contains("กรอกค่าใหม่", msg);                    // ต้องบอกวิธีแก้
        Assert.Contains("Authentication Required", msg);        // เชื่อมกับ error ที่ผู้ใช้เห็นจริง
    }

    [Fact]
    public void ข้อความเตือนต้องเป็นข้อความล้วน_ไม่มีแท็กให้หน้าเว็บตีความ()
    {
        var stored = Make(KeyA).Protect("เดิม");
        var msg = SecretWarnings.Build(Make(KeyB), ("รหัสผ่าน SMTP", stored))!;
        Assert.DoesNotContain("<", msg);
        Assert.DoesNotContain("**", msg);
    }
}

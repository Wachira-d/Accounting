using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · G2-06 — ตัวจับพนักงานซ้ำเทียบเลขบัตรหลังถอดรหัส (ในหน่วยความจำ)
/// สองทิศ: เลขเดียวกันต่างรูปแบบ (ขีด/ช่องว่าง) ต้องเจอ · เลขต่างกัน/ว่าง ต้องไม่ถูกนับว่าซ้ำ</summary>
public class EncryptedIdMatchTests
{
    [Theory]
    [InlineData("1234567890123", "1234567890123")]
    [InlineData("1-2345-67890-12-3", "1234567890123")]
    [InlineData(" 1234567890123 ", "1 2345 67890 12 3")]
    public void เลขเดียวกันต่างรูปแบบ_ถือว่าซ้ำ(string stored, string imported)
        => Assert.True(EncryptedIdMatch.Same(stored, imported));

    [Theory]
    [InlineData("1234567890123", "1234567890124")]
    [InlineData(null, "1234567890123")]
    [InlineData("", "")]
    [InlineData("-", "-")]
    [InlineData(null, null)]
    public void เลขต่างกันหรือว่าง_ไม่ถือว่าซ้ำ(string? stored, string? imported)
        => Assert.False(EncryptedIdMatch.Same(stored, imported));
}

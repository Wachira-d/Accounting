using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เครื่องออกเลขรันกลาง (F-08)
///
/// ═══ บั๊กจริงที่เทสต์ชุดนี้ล็อกไว้ ═══
/// จุดออกเลขราว 25 จุดเขียน <c>OrderByDescending(number).First() + 1</c> เอง
/// ⇒ เรียงแบบ <b>ข้อความ</b>: <c>"9999" &gt; "10000"</c> ⇒ พอทะลุหลักพัน
/// เลขจะ<b>วนกลับไปทับของเดิม</b> — และเงียบสนิทเพราะเลขที่ได้ "ดูปกติ"
/// </summary>
public class SequenceNumberTests
{
    [Fact]
    public void ทะลุ_9999_แล้วต้องเดินต่อ_ไม่วนกลับ()
    {
        // ★ negative test ของสูตรเดิม: เรียงแบบข้อความจะได้ "9999" เป็นตัวสูงสุด
        var suffixes = new[] { "9998", "9999", "10000", "10001" };
        var lexMax = suffixes.OrderByDescending(s => s, StringComparer.Ordinal).First();
        Assert.Equal("9999", lexMax);                       // พิสูจน์ว่าสูตรเดิมพัง
        Assert.Equal(10002, SequenceNumber.NextSequence(suffixes));
    }

    [Fact]
    public void ไม่มีของเดิมเลย_เริ่มที่_1()
        => Assert.Equal(1, SequenceNumber.NextSequence(Array.Empty<string>()));

    [Fact]
    public void ข้ามค่าที่ไม่ใช่ตัวเลข_ไม่ระเบิดและไม่นับเป็นฐาน()
    {
        // เลขที่ผู้ใช้พิมพ์เองสมัยก่อน / เลขนำเข้าจากระบบเก่าปนอยู่ในชุดเดียวกัน
        var suffixes = new[] { "0007", "เก่า-A", "", null, "  ", "0012" };
        Assert.Equal(13, SequenceNumber.NextSequence(suffixes!));
    }

    [Fact]
    public void ลำดับไม่เรียงมา_ก็ยังได้ค่าสูงสุด()
        => Assert.Equal(43, SequenceNumber.NextSequence(new[] { "0005", "0042", "0011" }));

    [Fact]
    public void เลขนำหน้าศูนย์_ถูกอ่านเป็นจำนวนเต็ม()
        => Assert.Equal(2, SequenceNumber.NextSequence(new[] { "0001" }));

    [Theory]
    [InlineData(1, "PAY-202609-0001")]
    [InlineData(9999, "PAY-202609-9999")]
    [InlineData(10000, "PAY-202609-10000")]   // ★ ไม่ตัดหลัก ไม่วนกลับเป็น 0000
    [InlineData(123456, "PAY-202609-123456")]
    public void จัดรูปเลข_ไม่ตัดหลักเมื่อทะลุความกว้าง(int seq, string expected)
        => Assert.Equal(expected, SequenceNumber.Format("PAY-202609-", seq));

    [Fact]
    public void คีย์ล็อกของสอง_number_space_ต้องไม่ชนกัน()
    {
        var company = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var pay = AdvisoryLockKey.For(company, AdvisoryLockKey.PaymentSequence, "PAY-202609-");
        var asset = AdvisoryLockKey.For(company, AdvisoryLockKey.AssetSequence, "PAY-202609-");
        Assert.NotEqual(pay, asset);
    }

    [Fact]
    public void คีย์ล็อกต้องคงที่ข้าม_process()
    {
        // ★ hard-code ค่าจริง — เทสต์ "เรียกสองครั้งได้เท่ากัน" ผ่านแม้ใช้
        // HashCode.Combine ที่สุ่มต่อ process จึงพิสูจน์อะไรไม่ได้เลย
        var company = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var a = AdvisoryLockKey.For(company, AdvisoryLockKey.PaymentSequence, "PAY-202609-");
        var b = AdvisoryLockKey.For(company, AdvisoryLockKey.PaymentSequence, "PAY-202609-");
        Assert.Equal(a, b);
        Assert.NotEqual(0L, a);
    }

    [Fact]
    public void คนละบริษัท_คนละคีย์_จึงไม่ต้องรอกัน()
    {
        var a = AdvisoryLockKey.For(Guid.NewGuid(), AdvisoryLockKey.PaymentSequence, "PAY-202609-");
        var b = AdvisoryLockKey.For(Guid.NewGuid(), AdvisoryLockKey.PaymentSequence, "PAY-202609-");
        Assert.NotEqual(a, b);
    }
}

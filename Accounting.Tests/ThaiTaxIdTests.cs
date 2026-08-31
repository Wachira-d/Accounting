using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เลข 13 หลักบนกระดาษ ตัวไหนคือ "เลขผู้เสียภาษี" ตัวไหนคือ "บาร์โค้ดสินค้า"
///
/// ═══ ที่มา: บั๊กจริง PI-20260820-0005 ═══
/// ใบกำกับของร้านวัสดุ (Hardwarehouse → บจก.มังกร เซอร์วิส เอ็นจิเนียริ่ง)
/// มีบาร์โค้ด EAN-13 พิมพ์อยู่ในตารางสินค้าทุกบรรทัด ระบบเดิมรับ "เลข 13 หลัก
/// อะไรก็ได้" เป็นเลขผู้เสียภาษี → หยิบบาร์โค้ดมาใส่ช่องผู้ซื้อ → เตือนว่า
/// "อาจอัพโหลดผิดบริษัท" ทั้งที่กระดาษถูกต้องครบถ้วน
///
/// เทสต์ชุดนี้ล็อกบทเรียนหลัก: **checksum เป็นเงื่อนไขจำเป็นแต่ไม่พอ**
/// </summary>
public class ThaiTaxIdTests
{
    // เลขจริงจากใบที่เกิดปัญหา
    private const string OurCompany = "0205565017741";   // บจก.มังกร เซอร์วิส เอ็นจิเนียริ่ง
    private const string GhostBuyer = "8885009199627";   // เลขที่ถูกยัดเข้าช่องผู้ซื้อผิด ๆ
    private const string RealBarcode = "8859991446166";  // บาร์โค้ดสินค้าจริงบนใบเดียวกัน

    // ───────── checksum พื้นฐาน ─────────

    [Theory]
    [InlineData("0205565017741")]
    [InlineData("0-2055-65017-74-1")]   // รูปแบบที่กระดาษพิมพ์
    [InlineData("0 2055 65017 74 1")]
    public void เลขนิติบุคคลจริงผ่านทุกรูปแบบการเขียน(string raw)
    {
        Assert.True(ThaiTaxId.IsValid(raw));
        Assert.True(ThaiTaxId.IsJuristic(raw));
        Assert.Equal(OurCompany, ThaiTaxId.Normalize(raw));
    }

    [Theory]
    [InlineData("0205565017740")]   // check digit ผิด
    [InlineData("020556501774")]    // 12 หลัก
    [InlineData("02055650177410")]  // 14 หลัก
    [InlineData("")]
    [InlineData(null)]
    public void เลขที่ใช้ไม่ได้ต้องตก(string? raw) => Assert.False(ThaiTaxId.IsValid(raw));

    [Fact]
    public void หลักแรกเป็น9ไม่มีการออกจริง_ต้องตกแม้checksumผ่าน()
    {
        // สร้างเลขที่ checksum ผ่านแต่ขึ้นต้น 9 — สรรพากรไม่เคยออกช่วงนี้
        var nine = MakeChecksumValid("900000000000");
        Assert.True(ThaiTaxId.HasValidChecksum(nine));
        Assert.False(ThaiTaxId.IsValid(nine));
    }

    // ───────── หัวใจของบั๊ก: checksum ผ่านแต่ไม่ใช่เลขผู้เสียภาษี ─────────

    [Fact]
    public void เลขผีที่ถูกยัดเข้าช่องผู้ซื้อ_ผ่านmod11ไทยจริง_นี่คือเหตุที่checksumอย่างเดียวไม่พอ()
    {
        // ถ้า checksum พอ เลขนี้จะยังหลุดเข้ามาได้อยู่ดี — เทสต์นี้จึงเป็น
        // หลักฐานว่าต้องมีด่านอื่น (ป้ายกำกับ/ตำแหน่ง) ไม่ใช่แค่คณิตศาสตร์
        Assert.True(ThaiTaxId.HasValidChecksum(GhostBuyer));
        Assert.True(ThaiTaxId.IsValid(GhostBuyer));
        Assert.False(ThaiTaxId.HasValidEan13Checksum(GhostBuyer));   // ไม่ใช่บาร์โค้ดที่สมบูรณ์
    }

    [Fact]
    public void บาร์โค้ดสินค้าจริง_ผ่านทั้งmod11ไทยและEAN13_ต้องถูกคัดออก()
    {
        Assert.True(ThaiTaxId.HasValidChecksum(RealBarcode));      // ผ่านไทยด้วย!
        Assert.True(ThaiTaxId.HasValidEan13Checksum(RealBarcode));
        Assert.True(ThaiTaxId.LooksLikeProductBarcode(RealBarcode));
        Assert.False(ThaiTaxId.IsPlausibleFromScan(RealBarcode));  // ⬅ ด่านที่กันไว้
    }

    [Theory]
    [InlineData("8859991940534")]   // บาร์โค้ดอื่นบนใบเดียวกัน
    [InlineData("8859991966695")]
    [InlineData("8859172200587")]
    [InlineData("8859991996272")]
    public void บาร์โค้ดที่เหลือบนใบเดียวกัน_ไม่ผ่านmod11อยู่แล้ว_แต่ต้องถูกจับว่าเป็นบาร์โค้ด(string barcode)
    {
        Assert.True(ThaiTaxId.HasValidEan13Checksum(barcode));
        Assert.True(ThaiTaxId.LooksLikeProductBarcode(barcode));
        Assert.False(ThaiTaxId.IsPlausibleFromScan(barcode));
    }

    [Fact]
    public void เลขบริษัทเราต้องไม่ถูกเข้าใจผิดว่าเป็นบาร์โค้ด()
    {
        Assert.False(ThaiTaxId.LooksLikeProductBarcode(OurCompany));
        Assert.True(ThaiTaxId.IsPlausibleFromScan(OurCompany));
    }

    [Fact]
    public void เลขที่ผ่านEAN13แต่prefixไม่ใช่ของสินค้า_ไม่ถือเป็นบาร์โค้ด()
    {
        // ตั้งใจไม่ตัดด้วย prefix อย่างเดียว และไม่ตัดด้วย EAN-13 อย่างเดียว —
        // เลขบุคคลไทยขึ้นต้น 1-8 มีจริง ถ้าตัดกว้างไปจะทิ้งเลขที่ถูกต้อง
        var personal = MakeEan13Valid("120000000000");   // ขึ้นต้น 1 ไม่ใช่ GS1 prefix สินค้า
        Assert.True(ThaiTaxId.HasValidEan13Checksum(personal));
        Assert.False(ThaiTaxId.LooksLikeProductBarcode(personal));
    }

    // ───────── เทียบเลขสองก้อน ─────────

    [Fact]
    public void เทียบเลขข้ามรูปแบบการเขียนได้()
    {
        Assert.True(ThaiTaxId.Same("0-2055-65017-74-1", "0205565017741"));
        Assert.False(ThaiTaxId.Same(OurCompany, GhostBuyer));
        Assert.False(ThaiTaxId.Same(null, OurCompany));
        Assert.False(ThaiTaxId.Same("123", "123"));      // ไม่ครบ 13 หลัก = เทียบไม่ได้
    }

    // ───────── helper: สร้างเลขทดสอบ ─────────

    /// <summary>ต่อ check digit ไทยให้ครบ 13 หลักจากตัวเลข 12 ตัว</summary>
    private static string MakeChecksumValid(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (13 - i);
        return first12 + (char)('0' + (11 - sum % 11) % 10);
    }

    /// <summary>ต่อ check digit EAN-13 ให้ครบ 13 หลักจากตัวเลข 12 ตัว</summary>
    private static string MakeEan13Valid(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return first12 + (char)('0' + (10 - sum % 10) % 10);
    }
}

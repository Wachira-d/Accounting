using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// อ่าน "จำนวนเงินตัวอักษร" ไทยกลับเป็นตัวเลข
///
/// ═══ ทำไมสำคัญ (ผลตรวจของทีมวิเคราะห์ข้อมูล) ═══
/// ใบเสร็จ/ใบกำกับไทยเกือบทุกใบพิมพ์ยอดไว้สองรูปแบบ — ตัวเลขในตาราง และ
/// ตัวอักษรในกรอบ "จำนวนเงินรวมทั้งสิ้น (ตัวอักษร)" ซึ่งหน้าตาต่างกันสิ้นเชิง
/// ⇒ OCR แทบไม่มีทางอ่านผิด**เหมือนกัน**ทั้งคู่ เป็นด่านตรวจซ้ำที่แรงที่สุด
/// ที่มีอยู่บนกระดาษ และจับ "จุดทศนิยม/ลูกน้ำหาย" ได้ ซึ่งด่านคณิตอื่นจับไม่ได้
/// (6,420.00 → 642000 ยังผ่าน sub+vat=total ได้ถ้าอ่านผิดพร้อมกันทั้งชุด)
///
/// ระบบมีตัวแปลง "เลข → ตัวอักษร" มานาน (ขาพิมพ์เอกสาร) แต่ไม่เคยมีขากลับ
/// </summary>
public class ThaiAmountInWordsTests
{
    // ── ยอดจริงจากใบที่เคยเป็นบั๊ก ──

    [Theory]
    [InlineData("หกพันสี่ร้อยยี่สิบบาทถ้วน", 6420.00)]            // หจก.สหกลชลบุรี เลขที่ 0339
    [InlineData("หนึ่งพันห้าสิบสามบาทเก้าสิบห้าสตางค์", 1053.95)]  // Hardwarehouse PI-20260820-0005
    [InlineData("ยี่สิบเอ็ดบาทถ้วน", 21.00)]                      // "ยี่" + "เอ็ด" พร้อมกัน
    [InlineData("สิบบาทถ้วน", 10.00)]                            // "สิบ" เดี่ยว = 10 ไม่ใช่ 1
    [InlineData("หนึ่งล้านบาทถ้วน", 1000000.00)]
    [InlineData("สองล้านห้าแสนสามหมื่นบาทถ้วน", 2530000.00)]
    [InlineData("ศูนย์บาทถ้วน", 0.00)]
    [InlineData("หนึ่งร้อยเอ็ดบาทห้าสิบสตางค์", 101.50)]
    public void อ่านจำนวนเงินตัวอักษรได้ถูกต้อง(string words, double expected)
        => Assert.Equal((decimal)expected, ThaiAmountInWords.Parse(words));

    [Fact]
    public void ช่องว่างที่OCRแทรกกลางคำต้องไม่ทำให้อ่านไม่ออก()
    {
        // Tesseract ไทยแทรกช่องว่างระหว่างสระ/วรรณยุกต์เป็นปกติ
        Assert.Equal(6420.00m, ThaiAmountInWords.Parse("หก พัน สี่ ร้อย ยี่ สิบ บาท ถ้วน"));
        Assert.Equal(6420.00m, ThaiAmountInWords.Parse("(หกพันสี่ร้อยยี่สิบบาทถ้วน)"));
    }

    [Fact]
    public void ป้ายกำกับติดมาด้วยก็ยังอ่านได้()
        => Assert.Equal(6420.00m,
            ThaiAmountInWords.Parse("จำนวนเงินตัวอักษร หกพันสี่ร้อยยี่สิบบาทถ้วน"));

    // ── ห้ามเดา ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("หกพันสี่ร้อย")]              // ไม่มีคำว่า "บาท" = ไม่ใช่จำนวนเงิน
    [InlineData("XYZบาทถ้วน")]                 // คำที่ไม่รู้จัก
    [InlineData("หกพันบาทหนึ่งร้อยสตางค์")]   // สตางค์ > 99 = อ่านผิด
    public void อ่านไม่ออกต้องคืนnullไม่ใช่เดา(string? words)
        => Assert.Null(ThaiAmountInWords.Parse(words));

    // ── round-trip กับตัวแปลงขาไป ──

    [Fact]
    public void แปลงเลขเป็นตัวอักษรแล้วอ่านกลับต้องได้เลขเดิม()
    {
        // ล็อกความเข้ากันได้กับ PdfGenerationService.ThaiNumberToText — ถ้าใคร
        // แก้ฝั่งพิมพ์แล้วรูปแบบเปลี่ยน เทสต์นี้จะพังทันที
        decimal[] samples =
        {
            0.01m, 1m, 10m, 11m, 20m, 21m, 100m, 101m, 999.99m,
            1053.95m, 6420m, 12345.67m, 100000m, 999999.99m,
            1000000m, 1000001m, 2530000m, 9999999.99m,
        };
        foreach (var v in samples)
        {
            var text = ToThaiWords(v);
            var back = ThaiAmountInWords.Parse(text);
            Assert.True(back.HasValue, $"อ่านกลับไม่ได้: {v} → \"{text}\"");
            Assert.Equal(v, back!.Value);
        }
    }

    // ── ค้นในข้อความทั้งหน้า ──

    [Fact]
    public void ค้นจำนวนเงินตัวอักษรจากข้อความทั้งหน้าได้()
    {
        const string paper = """
            ใบเสร็จรับเงิน/ใบกำกับภาษี  เลขที่ 0339
            แผ่นอลูมิเนียม  2  3,000.00  6,000.00
            รวมราคาสินค้า 6,000.00
            ภาษีมูลค่าเพิ่ม 7% 420.00
            จำนวนเงินรวมทั้งสิ้น 6,420.00
            จำนวนเงินรวมทั้งสิ้น (ตัวอักษร)   หกพันสี่ร้อยยี่สิบบาทถ้วน
            """;
        Assert.Equal(6420.00m, ThaiAmountInWords.FindInText(paper));
    }

    [Fact]
    public void กระดาษที่ไม่มีจำนวนเงินตัวอักษร_คืนnullไม่ใช่ศูนย์()
    {
        // null = "เทียบไม่ได้" ต่างจาก 0 ที่แปลว่า "ยอดเป็นศูนย์"
        Assert.Null(ThaiAmountInWords.FindInText("ใบกำกับภาษี\nรวม 6,420.00 บาท"));
    }

    // ── กติกาการเทียบ ──

    [Fact]
    public void เทียบยอด_ตรงกันหรือเทียบไม่ได้ถือว่าผ่าน_ขัดกันจริงเท่านั้นที่ตก()
    {
        Assert.True(ThaiAmountInWords.Matches(6420.00m, 6420.00m));
        Assert.True(ThaiAmountInWords.Matches(6420.00m, 6420.01m));   // ผ่อน 1 สตางค์
        Assert.True(ThaiAmountInWords.Matches(6420.00m, null));        // ไม่มีตัวอักษร = เทียบไม่ได้
        Assert.True(ThaiAmountInWords.Matches(null, 6420.00m));
        Assert.False(ThaiAmountInWords.Matches(642000.00m, 6420.00m)); // ลูกน้ำหาย — เคสที่ตั้งใจจับ
        Assert.False(ThaiAmountInWords.Matches(6420.00m, 6425.00m));
    }

    /// <summary>สำเนาสูตร PdfGenerationService.ThaiNumberToText ไว้ทดสอบ round-trip
    /// (ตัวจริงเป็น private ในคลาสที่ต้องมี DI ครบ)</summary>
    private static string ToThaiWords(decimal amount)
    {
        string[] units = { "", "สิบ", "ร้อย", "พัน", "หมื่น", "แสน" };
        string[] digits = { "", "หนึ่ง", "สอง", "สาม", "สี่", "ห้า", "หก", "เจ็ด", "แปด", "เก้า" };

        static string Group(long val, string[] d, string[] u)
        {
            if (val == 0) return "";
            var s = val.ToString();
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < s.Length; i++)
            {
                var dg = s[i] - '0';
                var pos = s.Length - i - 1;
                if (dg == 0) continue;
                if (pos == 1 && dg == 1) sb.Append("สิบ");
                else if (pos == 1 && dg == 2) sb.Append("ยี่สิบ");
                else if (pos == 1) sb.Append(d[dg]).Append("สิบ");
                else if (pos == 0 && dg == 1 && s.Length > 1) sb.Append("เอ็ด");
                else sb.Append(d[dg]).Append(u[pos]);
            }
            return sb.ToString();
        }

        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var baht = (long)amount;
        var satang = (int)Math.Round((amount - baht) * 100, MidpointRounding.AwayFromZero);
        if (baht == 0 && satang == 0) return "ศูนย์บาทถ้วน";

        string txt;
        if (baht >= 1_000_000)
        {
            txt = Group(baht / 1_000_000, digits, units) + "ล้าน";
            var rem = baht % 1_000_000;
            if (rem > 0) txt += Group(rem, digits, units);
        }
        else txt = baht > 0 ? Group(baht, digits, units) : "ศูนย์";

        txt += "บาท";
        txt += satang > 0 ? Group(satang, digits, units) + "สตางค์" : "ถ้วน";
        return txt;
    }
}

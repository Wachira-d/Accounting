using System.Text.RegularExpressions;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 190 ข้อ 9 — "OCR ใบกำกับที่มี<b>ส่วนลดท้ายใบ</b>ยังดึงมาไม่ถูก" · ตัวอ่าน <see cref="OcrBillDiscount"/>
///
/// <para><b>ครึ่งที่ 1</b> ใบที่ตัวอ่านเดิมพัง (ทุกเคสล็อกคำตอบเดิมไว้ด้วยตัวอ่านเดิมที่คัดลอกตรงจาก
/// <c>git show 75292cf:Accounting/Services/Implementations/OcrService.cs</c> — ให้เห็นตัวเลขที่ผิดจริง
/// ไม่ใช่เขียนจากความจำ) · <b>ครึ่งที่ 2</b> ใบที่ต้องไม่ถูกแตะ (Wine Pro ใบ A · Makro · ใบไม่มีส่วนลด ·
/// แถวยอด 0 · เปอร์เซ็นต์ล้วน)</para>
/// </summary>
public class OcrBillDiscountTests
{
    /// <summary>ตัวอ่านเดิม (ก่อนรอบ 190) — ใช้เป็น baseline เท่านั้น ห้ามนำไปใช้ในโค้ดจริง</summary>
    private static decimal? OldReader(string text, decimal? total)
    {
        var dm = Regex.Match(text,
            @"(?:ส่วนลด(?:รวม|การค้า)?|discount)\s*:?\s*(?:฿|บาท)?\s*([\d,]+(?:\.\d{1,2})?)",
            RegexOptions.IgnoreCase);
        if (dm.Success && decimal.TryParse(dm.Groups[1].Value.Replace(",", ""), out var disc)
            && disc > 0 && disc < (total ?? decimal.MaxValue))
            return disc;
        return null;
    }

    // ── ครึ่งที่ 1: ใบที่เคยพัง ──────────────────────────────────────────────

    [Fact]
    public void ใบร้านวัสดุ_ลดท้ายบิล5เปอร์เซ็นต์_ต้องได้6975_ไม่ใช่ยอดหลังหักส่วนลด()
    {
        var r = OcrBillDiscount.Read(OcrPaperSamples.HardwareBillDiscount, 1418.02m);
        Assert.Equal(69.75m, r.Amount);
        Assert.True(r.FromTotalRow);
        Assert.Contains("ส่วนลดท้ายบิล", r.Evidence);
        // ตัวอ่านเดิมข้ามป้าย "ส่วนลดท้ายบิล" แล้วไปหยิบ "ยอดหลังหักส่วนลด 1,325.25" เป็นส่วนลด
        Assert.Equal(1325.25m, OldReader(OcrPaperSamples.HardwareBillDiscount, 1418.02m));
    }

    [Fact]
    public void ใบซูเปอร์มาร์เก็ต_ส่วนลดสมาชิกพิมพ์ติดลบ_ต้องได้3870()
    {
        var r = OcrBillDiscount.Read(OcrPaperSamples.SupermarketMemberDiscount, 735.30m);
        Assert.Equal(38.70m, r.Amount);
        Assert.Null(OldReader(OcrPaperSamples.SupermarketMemberDiscount, 735.30m));   // เดิม: หาย
    }

    [Fact]
    public void เปอร์เซ็นต์ก่อนยอด_ต้องได้ยอดเงิน_ไม่ใช่เลขเปอร์เซ็นต์()
    {
        const string t = "ส่วนลด 10%   150.00\nรวมทั้งสิ้น 1,350.00";
        Assert.Equal(150m, OcrBillDiscount.Read(t, 1350m).Amount);
        Assert.Equal(10m, OldReader(t, 1350m));
    }

    [Theory]
    [InlineData("Discount (150.00)")]
    [InlineData("DISCOUNT 150.00-")]
    [InlineData("ส่วนลด −150.00")]
    public void ส่วนลดแบบวงเล็บหรือติดลบของเครื่อง_POS_ต้องได้ค่าบวก(string line)
        => Assert.Equal(150m, OcrBillDiscount.Read(line, 1000m).Amount);

    [Fact]
    public void ยอดหลังหักส่วนลด_ไม่ใช่ส่วนลด()
    {
        const string t = "ยอดรวมหลังหักส่วนลด 900.00\nรวมทั้งสิ้น 963.00";
        Assert.Null(OcrBillDiscount.Read(t, 963m).Amount);
        Assert.Equal(900m, OldReader(t, 963m));
    }

    [Fact]
    public void หัวคอลัมน์ส่วนลด_ห้ามกลืนเลขลำดับแถวถัดไป()
    {
        const string t = "รายการ   ส่วนลด\n1 ปากกา 10.00";
        Assert.Null(OcrBillDiscount.Read(t, 10.70m).Amount);
        Assert.Equal(1m, OldReader(t, 10.70m));   // \s* ข้ามบรรทัด → ส่วนลด 1 บาท
    }

    [Fact]
    public void หลายแถวส่วนลดย่อยไม่มีแถวรวม_รวมกัน()
    {
        var r = OcrBillDiscount.Read("ส่วนลด 10.00\nน้ำดื่ม 20.00\nส่วนลด 20.00", 1000m);
        Assert.Equal(30m, r.Amount);
        Assert.Equal(2, r.RowCount);
        Assert.False(r.FromTotalRow);
    }

    [Fact]
    public void แถวรวมส่วนลดชนะแถวย่อย_ไม่นับซ้ำ()
    {
        var r = OcrBillDiscount.Read("ส่วนลด 10.00\nน้ำดื่ม 20.00\nส่วนลด 20.00\nรวมส่วนลด 30.00", 1000m);
        Assert.Equal(30m, r.Amount);
        Assert.True(r.FromTotalRow);
    }

    [Fact]
    public void ป้ายกับยอดคนละบรรทัด_อ่านได้เมื่อบรรทัดถัดไปเป็นตัวเลขล้วน()
        => Assert.Equal(150m, OcrBillDiscount.Read("ส่วนลดพิเศษ\n150.00", 1000m).Amount);

    // ── ครึ่งที่ 2: ใบที่ต้องไม่ถูกแตะ ─────────────────────────────────────────

    [Fact]
    public void ใบ_WinePro_ไม่มีส่วนลด_ต้องได้ว่าง()
        => Assert.Null(OcrBillDiscount.Read(OcrPaperSamples.WinePro, 3593m).Amount);

    [Fact]
    public void ใบผสม_VAT_ไม่มีส่วนลด_ต้องได้ว่าง()
        => Assert.Null(OcrBillDiscount.Read(OcrPaperSamples.WholesaleMixedVat, 792m).Amount);

    [Theory]
    [InlineData("ส่วนลด 0.00\nรวม 1,000.00")]        // แถวยอด 0 ไม่ใช่หลักฐาน (RG-02)
    [InlineData("ส่วนลด 5%\nรวม 1,000.00")]          // เปอร์เซ็นต์ล้วน = ไม่รู้ยอด ห้ามคำนวณเอง
    [InlineData("Discounted price 900.00")]           // คำว่า discounted ไม่ใช่ป้ายส่วนลด
    [InlineData("ส่วนลด 2,000.00")]                   // ไม่น้อยกว่ายอดรวม = อ่านผิดแถว
    [InlineData("บริษัท สยามแม็คโคร จำกัด (มหาชน)\nรวมเงิน 951.00\nภาษีมูลค่าเพิ่ม 7% 49.00\nรวมทั้งสิ้น 1,000.00")]
    public void กระดาษที่ไม่มีส่วนลดจริง_ต้องได้ว่าง(string text)
        => Assert.Null(OcrBillDiscount.Read(text, 1000m).Amount);

    [Fact]
    public void ป้ายสองภาษาของแถวเดียวกัน_ไม่นับสองครั้ง()
    {
        var r = OcrBillDiscount.Read("ส่วนลด 50.00\nDiscount 50.00", 1000m);
        Assert.Equal(50m, r.Amount);
        Assert.Equal(1, r.RowCount);
    }

    [Fact]
    public void ข้อความว่าง_ต้องได้ว่าง()
    {
        Assert.Null(OcrBillDiscount.Read(null, 1000m).Amount);
        Assert.Null(OcrBillDiscount.Read("   ", null).Amount);
    }
}

using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คัดกรองภาษีซื้อต้องห้ามตาม "ชนิดรายจ่าย" (§82/5(4) ค่ารับรอง · §82/5(6)
/// รถยนต์นั่ง ≤ 10 ที่นั่ง + ประกาศอธิบดีฯ 42) — ชั้นที่ OCR ขาดไป: เดิมตรวจได้
/// แค่ "รูปแบบใบ" แล้วเปิดเคลมให้ทุกใบที่รูปแบบถูก ⇒ ใบกำกับเต็มรูปของค่าน้ำมัน
/// รถเก๋ง/ค่ารับรอง ถูก default ว่าเคลมได้ = ยื่น ภ.พ.30 เกินสิทธิ์เงียบ ๆ
/// </summary>
public class ProhibitedInputVatScreenerTests
{
    // บริษัททั่วไป (ไม่ใช่ผู้ประกอบกิจการขาย/ให้เช่ารถ) — ผลต้องเท่าเดิมทุกเคสหลังรอบ 193 S-05
    private static ProhibitedVatVerdict Screen(string text, string? vendor = null, params string[] lines)
        => ProhibitedInputVatScreener.Screen(text, vendor, lines, isVehicleDealer: false);

    // ── §82/5(4) ค่ารับรอง — ต้องห้ามเสมอ ────────────────────────────

    [Theory]
    [InlineData("ค่ารับรองลูกค้า มื้อเย็น")]
    [InlineData("ค่าเลี้ยงรับรอง คณะผู้บริหาร")]
    [InlineData("กระเช้าของขวัญ ปีใหม่")]
    public void Entertainment_is_always_prohibited(string text)
    {
        var v = Screen(text);
        Assert.False(v.Claimable);
        Assert.Equal("RD-82/5(4)", v.RuleCode);
    }

    [Fact]
    public void Entertainment_wins_even_when_a_claimable_vehicle_is_mentioned()
    {
        // ค่ารับรองไม่มีข้อยกเว้น — เจอพร้อมกันต้องไม่ถูกกลบด้วยกฎรถ
        var v = Screen("ค่ารับรองลูกค้า ณ ร้านอาหาร (เดินทางด้วยรถกระบะบริษัท)");
        Assert.False(v.Claimable);
        Assert.Equal("RD-82/5(4)", v.RuleCode);
    }

    // ── §82/5(6) รถ — ตัดสินตามชนิดรถ ────────────────────────────────

    [Theory]
    [InlineData("ค่าน้ำมันเชื้อเพลิง ดีเซล 40 ลิตร")]
    [InlineData("ค่าเช่ารถยนต์ ประจำเดือน")]
    [InlineData("ค่าซ่อมรถ เปลี่ยนยางรถยนต์")]
    // "ค่าน้ำมัน" ลอย ๆ คือคำที่พบบ่อยสุดบนใบเสร็จไทย — เคยหลุดด่านทั้งที่
    // เป็นเคสหลักที่ตั้งด่านมาดัก (เจอตอนจำลองก่อน commit)
    [InlineData("ค่าน้ำมัน รถเก๋ง ทะเบียน กก-1234")]
    [InlineData("เติมน้ำมัน 1,000 บาท")]
    public void Vehicle_cost_without_a_stated_vehicle_type_defaults_to_not_claimable(string text)
    {
        // สองทางผิดไม่เท่ากัน: เคลมเกินสิทธิ์ = โดนประเมิน+เบี้ยปรับ ·
        // ไม่เคลมทั้งที่เคลมได้ = ติ๊กคืนได้ใน 6 เดือน §82/3
        var v = Screen(text);
        Assert.False(v.Claimable);
        Assert.Equal("RD-82/5(6)", v.RuleCode);
        Assert.Contains("6 เดือน", v.Warning!);      // ต้องบอกทางกลับมาเคลม
    }

    [Fact]
    public void Fuel_station_vendor_alone_triggers_the_screen()
    {
        // ใบกำกับน้ำมันมัก description สั้นจนไม่มี keyword — จับจากชื่อผู้ขาย
        var v = Screen("ใบกำกับภาษี\nรวมเงิน 1,500.00", vendor: "บริษัท ปตท. น้ำมันและการค้าปลีก จำกัด");
        Assert.False(v.Claimable);
        Assert.Equal("RD-82/5(6)", v.RuleCode);
    }

    [Theory]
    [InlineData("ค่าน้ำมันดีเซล รถกระบะตอนเดียว ทะเบียน 1กก-1234")]
    [InlineData("ค่าน้ำมัน รถบรรทุกหกล้อ")]
    [InlineData("ค่าน้ำมันดีเซล รถโฟล์คลิฟท์ในโรงงาน")]
    public void Vehicle_cost_with_a_claimable_vehicle_stays_claimable_but_still_warns(string text)
    {
        var v = Screen(text);
        Assert.Null(v.Claimable);                    // ไม่ปิดการเคลม
        Assert.Equal("RD-82/5(6)", v.RuleCode);
        Assert.NotNull(v.Warning);                   // แต่ยังเตือนให้ยืนยันชนิดรถ
    }

    [Fact]
    public void Guidance_names_both_the_claimable_and_prohibited_vehicle_types()
    {
        // ผู้ใช้ต้องตัดสินใจได้จาก banner โดยไม่ต้องเปิดประมวลรัษฎากรเอง
        var v = Screen("ค่าน้ำมันเชื้อเพลิง");
        Assert.Contains("กระบะ 4 ประตู", v.Warning!);   // เคลมไม่ได้
        Assert.Contains("รถบรรทุก", v.Warning!);        // เคลมได้
    }

    // ── ไม่เข้าข่าย — ต้องไม่ฟ้องมั่ว ────────────────────────────────

    [Theory]
    [InlineData("ค่าไฟฟ้า ประจำเดือน 07/2569")]
    [InlineData("ค่าบริการอินเทอร์เน็ต รายเดือน")]
    [InlineData("ค่าวัสดุสำนักงาน กระดาษ A4")]
    // น้ำมัน**พืช**/น้ำมันหอย ของร้านอาหาร = ภาษีซื้อเคลมได้ตามปกติ — เหตุผล
    // ที่ keyword ไม่ใส่ "น้ำมัน" เดี่ยว ๆ (ฟ้องมั่วบนสินค้าที่เคลมได้ =
    // ผู้ใช้เสียสิทธิ์ + เลิกเชื่อ banner)
    [InlineData("น้ำมันพืชปาล์ม 12 ขวด")]
    [InlineData("น้ำมันหอยตราแม่ครัว 24 ขวด")]
    [InlineData("")]
    public void Unrelated_expenses_are_not_flagged(string text)
    {
        var v = Screen(text);
        Assert.Null(v.Claimable);
        Assert.Null(v.Warning);
    }

    [Fact]
    public void Line_descriptions_are_screened_too()
    {
        // ข้อความหัวเอกสารไม่บอก แต่บรรทัดรายการบอก
        var v = Screen("ใบกำกับภาษี\nรวม 2,000", null, "ค่าน้ำมันเบนซิน");
        Assert.False(v.Claimable);
    }
}

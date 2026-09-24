using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// S-05 (รอบ 193) — ธง <c>IsVehicleDealer</c> ต้องถึงตัวคัดกรอง §82/5(6) ทุกเส้น (OCR + ด่านเตือนตอนอนุมัติ)
///
/// <para>บั๊กจริง: <c>ProhibitedInputVatScreener.Screen</c> ไม่มีพารามิเตอร์นี้ ⇒ อู่/ผู้ขายรถที่เปิดธงแล้วสแกนใบค่าอะไหล่/
/// น้ำมัน ถูก OCR ใส่ <c>[VAT-CLAIM]</c> (ปิดเคลม) ทุกใบ → ผู้ใช้กด "ยืนยัน" ⇒ ภาษีซื้อหาย · ขณะที่ด่านเตือนอีกชุดในเมธอด
/// เดียวกันของ DocumentService อ่านธงนี้แต่ใช้ลิสต์คำคนละชุด</para>
///
/// <para>สองครึ่ง: dealer → ไม่ปิดเคลม (แต่ยังเตือนเรื่องรถใช้เอง) · ไม่ใช่ dealer → ผลเท่าเดิมทุกใบ
/// (ชุดเทสต์เดิม <c>ProhibitedInputVatScreenerTests</c>/<c>ProhibitedInputVatScopeTests</c> รันด้วย isVehicleDealer:false ทั้งหมด)</para>
/// </summary>
public class InputVatVehicleRuleTests
{
    // ── dealer: เคลมได้ ──

    [Theory]
    [InlineData("ค่าน้ำมัน 40 ลิตร", "บจก. สถานีบริการ")]
    [InlineData("Diesel 35.20 L", "PETROLEUM THAI CORPORATION CO., LTD.")]
    [InlineData("อะไหล่รถ ผ้าเบรก 2 ชุด", "หจก. อะไหล่ยนต์")]
    [InlineData("ค่าซ่อมรถ ตามใบสั่งงาน", "อู่ช่างเอก")]
    public void Dealer_ค่ารถไม่ถูกปิดเคลม_แต่ยังเตือนเรื่องรถใช้เอง(string text, string vendor)
    {
        var v = ProhibitedInputVatScreener.Screen(text, vendor, new[] { text }, isVehicleDealer: true);
        Assert.Null(v.Claimable);                              // ไม่ปิดเคลม ⇒ OCR ใส่ [VAT-NOTE] ไม่ใช่ [VAT-CLAIM]
        Assert.Equal(InputVatVehicleRule.RuleCode, v.RuleCode);
        Assert.Contains("ให้เช่ารถ", v.Warning);
        Assert.Equal(VehicleVatVerdict.VehicleDealerExempt, InputVatVehicleRule.Judge(text + "\n" + vendor, vendor, true));
    }

    [Fact]
    public void Dealer_ค่ารับรองยังต้องห้ามเสมอ()
    {
        // ธง dealer เป็นข้อยกเว้นของ §82/5(6) เท่านั้น ไม่ครอบ §82/5(4)
        var v = ProhibitedInputVatScreener.Screen("ค่ารับรองลูกค้า มื้อเย็น", "ร้านอาหาร", new[] { "ค่ารับรอง" }, isVehicleDealer: true);
        Assert.False(v.Claimable);
        Assert.Equal("RD-82/5(4)", v.RuleCode);
    }

    [Fact]
    public void Dealer_ใบที่ไม่เกี่ยวกับรถไม่มีคำเตือน()
    {
        var v = ProhibitedInputVatScreener.Screen("กระดาษ A4 10 รีม", "บจก. เครื่องเขียน", new[] { "กระดาษ A4" }, isVehicleDealer: true);
        Assert.Null(v.RuleCode);
        Assert.Null(v.Warning);
    }

    [Fact]
    public void Dealer_แก๊สหุงต้มไม่ใช่ค่ารถ()
        => Assert.Equal(VehicleVatVerdict.NotVehicleCost,
            InputVatVehicleRule.Judge("แก๊สหุงต้ม ถังแก๊ส 15 กก.", "ร้านแก๊ส", isVehicleDealer: true));

    // ── ไม่ใช่ dealer: ผลเดิมทุกใบ ──

    [Fact]
    public void ไม่ใช่_Dealer_ค่าน้ำมันไม่ระบุชนิดรถ_ยังปิดเคลมเหมือนเดิม()
    {
        var v = ProhibitedInputVatScreener.Screen("ค่าน้ำมัน 40 ลิตร", "บจก. สถานีบริการ", new[] { "ค่าน้ำมัน" }, isVehicleDealer: false);
        Assert.False(v.Claimable);
        Assert.Equal("RD-82/5(6)", v.RuleCode);
        Assert.Equal(VehicleVatVerdict.DefaultNotClaimable,
            InputVatVehicleRule.Judge("ค่าน้ำมัน 40 ลิตร", "บจก. สถานีบริการ", isVehicleDealer: false));
    }

    [Fact]
    public void ไม่ใช่_Dealer_รถบรรทุก_เปิดเคลมแต่เตือนเหมือนเดิม()
        => Assert.Equal(VehicleVatVerdict.ClaimableVehicleType,
            InputVatVehicleRule.Judge("ค่าน้ำมัน รถบรรทุกหกล้อ", "บจก. ขนส่ง", isVehicleDealer: false));

    [Fact]
    public void ไม่ใช่_Dealer_ชื่อปั๊มในข้อความไม่ใช่ชื่อผู้ขาย_ไม่ตีเป็นค่ารถ()
        => Assert.Equal(VehicleVatVerdict.NotVehicleCost,
            InputVatVehicleRule.Judge("น้ำดื่ม PURE LIFE 12 ขวด", "บจก. ค้าปลีก", isVehicleDealer: false));

    [Theory]
    [InlineData("ค่าซ่อมแอร์ สำนักงาน")]    // ลิสต์ชุดที่สองเดิม ("ค่าซ่อม" เดี่ยว) เคยเตือนใบนี้
    [InlineData("น้ำมันพืช 18 ลิตร")]        // ลิสต์ชุดที่สองเดิม ("น้ำมัน" เดี่ยว) เคยเตือนใบนี้
    [InlineData("Fuel surcharge")]            // ลิสต์ชุดที่สองเดิม ("fuel") เคยเตือนใบค่าขนส่งด่วน
    public void ลิสต์คำชุดเดียว_ไม่เตือนใบที่ไม่เกี่ยวกับรถ(string line)
        => Assert.Equal(VehicleVatVerdict.NotVehicleCost,
            InputVatVehicleRule.Judge(line, "บจก. ผู้ขายทั่วไป", isVehicleDealer: false));

    [Fact]
    public void เฉพาะ_DefaultNotClaimable_ที่ปิดเคลม()
    {
        Assert.True(InputVatVehicleRule.DisablesClaim(VehicleVatVerdict.DefaultNotClaimable));
        Assert.False(InputVatVehicleRule.DisablesClaim(VehicleVatVerdict.VehicleDealerExempt));
        Assert.False(InputVatVehicleRule.DisablesClaim(VehicleVatVerdict.ClaimableVehicleType));
        Assert.False(InputVatVehicleRule.DisablesClaim(VehicleVatVerdict.NotVehicleCost));
        Assert.Null(InputVatVehicleRule.Warning(VehicleVatVerdict.NotVehicleCost));
    }
}

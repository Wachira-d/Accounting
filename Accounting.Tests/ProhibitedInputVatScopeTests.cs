using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่าน §82/5 ปิดเคลมภาษีซื้อ = **เสียสิทธิ์จริง** ไม่ใช่แค่คำเตือน ⇒ การจับคำ
/// ต้องแม่นทั้งสองทิศ · เดิมเทียบแบบ substring กับข้อความ<b>ทั้งหน้า</b> ⇒ รหัสน้ำมัน
/// สั้น ๆ ("b7"/"e20") ไปโดนรหัสสินค้า และชื่อปั๊ม ("pure"/"ปตท") ที่โผล่ในโฆษณา
/// ท้ายใบ/ชื่อสินค้า ทำให้ทั้งใบถูกปิดเคลม (ผลตรวจ 2026-09-06 · T1-13)
/// </summary>
public class ProhibitedInputVatScopeTests
{
    // บริษัททั่วไป (ไม่ใช่ dealer) — ผลต้องเท่าเดิมทุกเคสหลังรอบ 193 S-05
    private static bool Blocked(string rawText, string vendor, params string[] lines)
        => ProhibitedInputVatScreener.Screen(rawText, vendor, lines, isVehicleDealer: false).Claimable == false;

    private static bool Flagged(string rawText, string vendor, params string[] lines)
        => ProhibitedInputVatScreener.Screen(rawText, vendor, lines, isVehicleDealer: false).RuleCode != null;

    // ── ต้องไม่ปิดเคลม (ของจริงที่เคยพัง) ─────────────────────────────────
    [Fact]
    public void รหัสขนาดสินค้าที่มี_e20_อยู่ข้างใน_ต้องไม่ถูกตีเป็นน้ำมัน()
        => Assert.False(Flagged("ใบกำกับภาษี วัสดุก่อสร้าง\nท่อ PVC SIZE20",
            "บจก. วัสดุก่อสร้างไทย", "ท่อ PVC SIZE20 จำนวน 10 เส้น"));

    [Fact]
    public void ชื่อสินค้าที่มีคำว่า_pure_ต้องไม่ถูกตีเป็นปั๊มน้ำมัน()
        => Assert.False(Flagged("น้ำดื่ม PURE LIFE 24 ขวด", "บจก. แม็คโคร",
            "PURE LIFE 600ml x 24"));

    [Fact]
    public void ชื่อปั๊มที่โผล่ในข้อความแต่ไม่ใช่ผู้ขาย_ต้องไม่ถูกตีเป็นค่าน้ำมัน()
        // โฆษณา/ที่อยู่/ชื่อถนนท้ายใบมีคำว่า "ปตท." ได้ตามปกติ
        => Assert.False(Flagged("ใบกำกับภาษี ปูนซีเมนต์\nที่อยู่ ติดปั๊ม ปตท. ถนนพระราม 2",
            "บจก. ก่อสร้างไทย", "ปูนซีเมนต์ 50 ถุง"));

    [Fact]
    public void แก๊สหุงต้มของร้านอาหาร_เคลมได้ตามปกติ()
        // ถ้าปิดเคลม ร้านอาหารทุกร้านเสียสิทธิ์ค่าแก๊สทุกเดือน
        => Assert.False(Flagged("ใบกำกับภาษี แก๊สหุงต้ม ถังละ 15 กก.",
            "บจก. แก๊สไทย", "แก๊สหุงต้ม 15 กก. x 4 ถัง"));

    // ── ต้องยังจับได้เหมือนเดิม (ห้ามแก้จนด่านหาย) ────────────────────────
    [Fact]
    public void สลิปปั๊มจริงที่พิมพ์ชนิดน้ำมันเป็นอังกฤษ_ต้องยังถูกจับ()
        => Assert.True(Blocked("Diesel 40.00 L 1,240.00", "PT.(47S)BANGPHRA2", "Diesel"));

    [Fact]
    public void รหัสน้ำมัน_B7_เดี่ยว_ต้องยังถูกจับ()
        => Assert.True(Blocked("B7 40 ลิตร 1,240.00", "บางจาก สาขาพระราม 2", "B7"));

    [Fact]
    public void ค่าน้ำมันรถบรรทุก_เปิดเคลมแต่ยังเตือน()
    {
        var v = ProhibitedInputVatScreener.Screen(
            "ค่าน้ำมัน รถบรรทุกหกล้อ ทะเบียน 70-1234", "บจก. ขนส่งไทย", new[] { "ค่าน้ำมัน" },
            isVehicleDealer: false);
        Assert.Null(v.Claimable);          // ไม่ปิดเคลม
        Assert.Equal("RD-82/5(6)", v.RuleCode);   // แต่ยังเตือนให้ยืนยันชนิดรถ
    }

    [Fact]
    public void ค่ารับรอง_ต้องห้ามเสมอ()
        => Assert.True(Blocked("ค่ารับรองลูกค้า ร้านอาหาร", "ร้านอาหารสุขใจ", "ค่ารับรอง"));
}

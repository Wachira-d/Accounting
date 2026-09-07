using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// e-Tax XML เก็บหน่วยเป็นรหัส UN/ECE Rec.20 (<c>C62</c>/<c>KGM</c>/<c>HUR</c>)
/// — ตัวสกัดอ่านมาได้ตั้งแต่แรกแต่ตัว map ทิ้งทุกครั้ง ⇒ ทุกบรรทัดของใบ e-Tax
/// ตกไปเป็น "ชิ้น" ทั้งที่เอกสารที่มีลายเซ็นดิจิทัลบอกหน่วยจริงไว้แล้ว
/// · แต่ถ้าใส่รหัสดิบลงเอกสาร ใบที่พิมพ์จะขึ้นว่า "C62" ซึ่งแย่กว่าเดิม
/// (ผลตรวจ 2026-09-06 · T2-08)
/// </summary>
public class UneceUnitCodeTests
{
    [Theory]
    [InlineData("C62", "ชิ้น")]
    [InlineData("EA", "ชิ้น")]
    [InlineData("KGM", "กก.")]
    [InlineData("LTR", "ลิตร")]
    [InlineData("HUR", "ชม.")]
    [InlineData("MON", "เดือน")]
    [InlineData("MTQ", "ลบ.ม.")]
    [InlineData("KWH", "หน่วย")]
    [InlineData("SET", "ชุด")]
    [InlineData("BX", "กล่อง")]
    public void รหัสสากล_ต้องแปลงเป็นหน่วยไทย(string code, string expected)
        => Assert.Equal(expected, UnitInferrer.FromUneceCode(code));

    [Theory]
    [InlineData("c62", "ชิ้น")]     // ตัวพิมพ์เล็ก
    [InlineData("Kgm", "กก.")]
    public void ไม่สนตัวพิมพ์เล็กใหญ่(string code, string expected)
        => Assert.Equal(expected, UnitInferrer.FromUneceCode(code));

    [Theory]
    [InlineData("ถุง")]
    [InlineData("เส้น")]
    [InlineData("ตร.ว.")]
    public void หน่วยไทยที่ผู้ออกใบใส่มาตรง_ต้องคงไว้(string unit)
        => Assert.Equal(unit, UnitInferrer.FromUneceCode(unit));

    [Fact]
    public void รหัสที่ไม่รู้จัก_ต้องคงค่าเดิม_ไม่ใช่ทิ้ง()
        => Assert.Equal("ZZZ", UnitInferrer.FromUneceCode("ZZZ"));

    [Fact]
    public void Resolve_ต้องแปลงรหัสสากลก่อนตัดสิน()
    {
        // "C62" = ชิ้น (ค่า default) ⇒ ถ้าคำอธิบายบอกหน่วยที่เจาะจงกว่า ให้ใช้ตัวนั้น
        Assert.Equal("หน่วย", UnitInferrer.Resolve("C62", "ค่าไฟฟ้าประจำเดือน"));
        // หน่วยที่เจาะจงจาก XML ต้องชนะการอนุมานจากคำอธิบาย
        Assert.Equal("ลิตร", UnitInferrer.Resolve("LTR", "ค่าไฟฟ้าประจำเดือน"));
    }
}

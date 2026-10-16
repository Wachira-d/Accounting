using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม OCR 11 (ฝ่ายค้าน OCR 9 P1-5): กติกาซ่อมผลแกะรุ่น 6–8 (<see cref="OcrLumpSumRepair"/>, pure) — บรรทัด/คีย์ที่เสียถูกจับ · บิลมิเตอร์และชื่อสินค้าจริงไม่ถูกแตะ ·
/// ทางไปต่อของเอกสารตามสถานะ (คำตัดสินข้อ 150: ระบบไม่แก้เอกสารที่ออกแล้วเอง) · หมายเหตุบล็อกมีร่องรอยที่ <see cref="OcrLineQuantityGuard.LiveNotes"/> ปลดได้เมื่อคนแก้บรรทัด
/// </summary>
public class OcrLumpSumRepairTests
{
    [Theory]
    [InlineData("น้ำดื่ม 600ml (12 ขวด)", 1, "รายการ", true)]                  // รุ่น 6 (ทีม 8)
    [InlineData("คอนกรีตผสมเสร็จ 240 ksc (2.75 ลบ.ม.)", 1, "รายการ", true)]     // รุ่น 7/8 ลบ.ม. ไม่มีบริบทน้ำ
    [InlineData("ค่าน้ำดื่ม 600ml (12 ลบ.ม.)", 1, "รายการ", true)]              // รุ่น 7/8 "ค่าน้ำ" substring
    [InlineData("เคส iPhone 16 (จำนวน 3)", 1, "รายการ", true)]                  // รุ่น 7/8 ผู้ขาย AWN อย่างเดียว
    [InlineData("ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)", 1, "รายการ", false)]  // บิล PEA — มิเตอร์จริง
    [InlineData("ค่าน้ำประปา (25 ลบ.ม.)", 1, "รายการ", false)]
    [InlineData("Flash Drive (64 GB)", 1, "รายการ", false)]                       // สเปกสินค้า
    [InlineData("น้ำดื่ม 600ml (12 ขวด)", 12, "ขวด", false)]                     // จำนวนคงอยู่แล้ว
    [InlineData("ปากกา", 2, "ด้าม", false)]
    public void บรรทัดที่เสีย_คือท้ายของตัวกันบนบรรทัดที่ไม่มีหลักฐานมิเตอร์(string desc, double qty, string unit, bool expected)
        => Assert.Equal(expected, OcrLumpSumRepair.IsPollutedLine(desc, (decimal)qty, unit));

    [Theory]
    [InlineData("ค่าไฟฟ้า (4,272 หน่วย)", "ค่าไฟฟ้า")]          // คีย์: มิเตอร์ก็ตัด (จำนวนหน่วยเปลี่ยนทุกเดือน)
    [InlineData("ค่าบริการ (จำนวน 3)", "ค่าบริการ")]
    [InlineData("น้ำดื่ม 600ml (12 ขวด)", "น้ำดื่ม 600ml")]
    [InlineData("Flash Drive (64 GB)", null)]
    [InlineData("ปูนซีเมนต์ 50 กก. (50 กก.)", null)]
    [InlineData("น้ำดื่ม", null)]
    [InlineData(null, null)]
    public void คีย์ที่เสีย_ตัดท้าย_ไม่แน่ใจไม่แตะ(string? text, string? expected)
        => Assert.Equal(expected, OcrLumpSumRepair.CleanKey(text));

    [Theory]
    [InlineData(DocumentStatus.Draft, "draft")]
    [InlineData(DocumentStatus.WaitingApproval, "draft")]
    [InlineData(DocumentStatus.Approved, "ใบลดหนี้")]
    [InlineData(DocumentStatus.Paid, "ปรับสต็อก")]
    [InlineData(DocumentStatus.Voided, "ไม่ต้องทำอะไร")]
    public void ทางไปต่อของเอกสาร_ตามสถานะ_ระบบไม่แก้เอง(DocumentStatus status, string mustContain)
        => Assert.Contains(mustContain, OcrLumpSumRepair.DocumentWayForward(status));

    [Fact]
    public void หมายเหตุบล็อก_จำได้ว่าติดแล้ว_ปลดเมื่อคนแก้บรรทัดและลงตัว()
    {
        var note = OcrLumpSumRepair.ScanNote(7, 1, "น้ำดื่ม 600ml (12 ขวด)", 300m);
        Assert.StartsWith(OcrLineQuantityGuard.Tag + " " + OcrLumpSumRepair.RuleCode, note);
        Assert.True(OcrLumpSumRepair.AlreadyMarked("ก่อนหน้า\n" + note));
        Assert.False(OcrLumpSumRepair.AlreadyMarked("[LINE-QTY] อื่น"));
        Assert.False(OcrLumpSumRepair.AlreadyMarked(null));
        // บรรทัดเอกสารยังเป็น 1 × 300 (ค่าที่ระบบตั้ง) ⇒ บล็อกต่อ
        Assert.Equal(note, OcrLineQuantityGuard.LiveNotes(note, new[] { new OcrLiveLine(1m, 300m, 0m, 0m, 300m, 0m) }, false, true));
        // คนแก้เป็น 12 × 25 ลงตัว ⇒ ปลด
        Assert.Equal("", OcrLineQuantityGuard.LiveNotes(note, new[] { new OcrLiveLine(12m, 25m, 0m, 0m, 300m, 0m) }, false, true));
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, OcrLumpSumRepair.AffectedVersions);   // ทีม OCR 12: รุ่น 9/10 ยังลง "ค่าน้ำขวด (12 ลบ.ม.)"
        Assert.True(OcrLumpSumRepair.AffectedVersions.Max() < OcrExtractionVersion.Current);
    }
}

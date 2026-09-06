using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// KPI ต้องอ่านเป็น "คู่" — อัตราเรียก AI ที่ลดลงอย่างเดียวแยกไม่ออกว่า
/// "นักเรียนเก่งขึ้น" (สำเร็จ) หรือ "โค้ดหยุดใช้คำตอบของโมเดล" (ถอยหลัง)
/// ทั้งสองกรณี UsedAi ลดลงเหมือนกันเป๊ะ (สถาปัตยกรรมเป้าหมาย D4)
/// </summary>
public class OcrQualityKpiTests
{
    [Fact]
    public void นักเรียนเก่งขึ้นจริง_เรียกAIน้อยแต่ผู้ใช้แทบไม่ต้องแก้()
    {
        // 200 ใบ · เรียก AI 20 ใบ · เป็นเอกสาร 190 ใบ · ผู้ใช้แก้ 15 ใบ
        var k = OcrQualityKpi.Read(totalScans: 200, aiBilledScans: 20,
            documentsCreated: 190, correctedScans: 15);
        Assert.Equal(0.10m, k.AiCallRate);
        Assert.Equal(0.875m, k.FirstPassAcceptRate);
        Assert.False(k.IsRegression);
    }

    [Fact]
    public void ประหยัดโดยโง่ลง_ต้องถูกจับได้()
    {
        // อัตราเรียก AI ต่ำเท่ากันเป๊ะกับเคสข้างบน แต่ผู้ใช้ต้องแก้เกือบทุกใบ
        // ⇒ ถ้าอ่าน UsedAi ตัวเดียวจะเห็นเป็น "ความสำเร็จ" ทั้งที่คุณภาพพัง
        var k = OcrQualityKpi.Read(totalScans: 200, aiBilledScans: 20,
            documentsCreated: 190, correctedScans: 160);
        Assert.Equal(0.10m, k.AiCallRate);
        Assert.True(k.FirstPassAcceptRate < OcrQualityKpi.AcceptRateFloor);
        Assert.True(k.IsRegression);
        Assert.Contains("นักเรียน", k.Verdict);
    }

    [Fact]
    public void ตัวอย่างน้อยเกินไป_ห้ามตัดสินว่าถอยหลัง()
    {
        var k = OcrQualityKpi.Read(totalScans: 3, aiBilledScans: 0,
            documentsCreated: 1, correctedScans: 1);
        Assert.False(k.IsRegression);
        Assert.Contains("ยังน้อย", k.Verdict);
    }

    [Fact]
    public void ยังไม่มีสแกน_ต้องไม่หารด้วยศูนย์()
    {
        var k = OcrQualityKpi.Read(0, 0, 0, 0);
        Assert.Equal(0m, k.AiCallRate);
        Assert.Equal(0m, k.FirstPassAcceptRate);
        Assert.False(k.IsRegression);
    }

    [Fact]
    public void สแกนที่ไม่ได้กลายเป็นเอกสาร_นับเป็นความล้มเหลวด้วย()
    {
        // เติมมาผิดจนผู้ใช้ทิ้งไปเลย = ล้มเหลวเหมือนกัน ⇒ ฐานต้องเป็นสแกนทั้งหมด
        // ไม่ใช่ "เฉพาะใบที่กลายเป็นเอกสาร" (ไม่งั้นตัวเลขจะสวยขึ้นเมื่อคุณภาพแย่ลง)
        var k = OcrQualityKpi.Read(totalScans: 100, aiBilledScans: 50,
            documentsCreated: 40, correctedScans: 0);
        Assert.Equal(0.40m, k.FirstPassAcceptRate);
        Assert.True(k.IsRegression);
    }

    [Fact]
    public void ยังพึ่งAIเป็นหลักแต่คุณภาพดี_ไม่ใช่การถอยหลัง()
    {
        var k = OcrQualityKpi.Read(totalScans: 100, aiBilledScans: 90,
            documentsCreated: 95, correctedScans: 10);
        Assert.Equal(0.90m, k.AiCallRate);
        Assert.False(k.IsRegression);
        Assert.Contains("กำลังสอน", k.Verdict);
    }
}

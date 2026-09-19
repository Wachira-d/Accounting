using System;
using System.Linq;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// มิติเวลาของตารางอัตราหัก ณ ที่จ่าย — <c>ThaiWhtRateTable.ClassifyRate</c>
///
/// <para><b>ครึ่งที่ 1 (ของที่พังต้องกลับมาถูก)</b> — ใบที่จ่ายระหว่าง
/// 1 เม.ย.–30 ก.ย. 2563 ด้วยอัตรา 1.5% (มาตรการโควิด ท.ป.310/2563)
/// ต้อง<b>เงียบ</b> · เดิมรอบ 183 ขึ้นคำเตือน "ไม่ใช่อัตราตามกฎหมาย" ทุกบรรทัด</para>
///
/// <para><b>ครึ่งที่ 2 (ของที่ถูกอยู่แล้วห้ามถูกแตะ)</b> — อัตราถาวรทั้ง 6 ตัวยัง
/// เงียบทุกวัน · อัตรามั่ว (4% · 2.5%) ยังเตือนเหมือนเดิม · และที่สำคัญที่สุด
/// <b>1.5% นอกช่วง ต้องยังเตือน</b> (ถ้าเติมเข้า <c>StatutoryRates</c> ตรง ๆ ตาม
/// ข้อเสนอเดิม ใบปีนี้ที่หักขาดครึ่งหนึ่งจะเงียบตลอดกาล — §54 ผู้จ่ายรับผิด)</para>
/// </summary>
public class WhtRateStandingTests
{
    private static readonly DateTime InsideCovidWindow = new(2020, 6, 15);
    private static readonly DateTime Today = new(2026, 9, 19);

    // ══════════════════════════════════════════════════════════════════
    // ครึ่งที่ 1 — ใบที่เคยถูกฟ้องผิด กลับมาเงียบ
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void CovidRateInsideWindowIsSilent()
    {
        var v = ThaiWhtRateTable.ClassifyRate(1.5m, InsideCovidWindow);
        Assert.Equal(ThaiWhtRateTable.RateStanding.TemporaryInForce, v.Standing);
        Assert.False(v.NeedsAttention);
        Assert.Null(v.Warning);
    }

    [Theory]
    [InlineData(2020, 4, 1)]    // วันแรกของช่วง — ขอบต้องรวม
    [InlineData(2020, 9, 30)]   // วันสุดท้ายของช่วง — ขอบต้องรวม
    public void CovidWindowBoundariesAreInclusive(int y, int m, int d)
        => Assert.False(ThaiWhtRateTable.ClassifyRate(1.5m, new DateTime(y, m, d)).NeedsAttention);

    // ══════════════════════════════════════════════════════════════════
    // ครึ่งที่ 2 — สิ่งที่ถูกอยู่แล้ว ห้ามเปลี่ยน
    // ══════════════════════════════════════════════════════════════════

    /// <summary>ทิศที่สำคัญที่สุด: 1.5% **นอกช่วง** ต้องยังเตือน และข้อความต้อง
    /// บอกช่วงเวลา + ความรับผิดตาม §54 (ไม่ใช่แค่ "ไม่ใช่อัตราตามกฎหมาย")</summary>
    [Theory]
    [InlineData(2020, 3, 31)]   // ก่อนช่วง 1 วัน
    [InlineData(2020, 10, 1)]   // หลังช่วง 1 วัน
    [InlineData(2026, 9, 19)]   // วันนี้
    public void CovidRateOutsideWindowStillWarns(int y, int m, int d)
    {
        var v = ThaiWhtRateTable.ClassifyRate(1.5m, new DateTime(y, m, d));
        Assert.Equal(ThaiWhtRateTable.RateStanding.TemporaryOutOfWindow, v.Standing);
        Assert.True(v.NeedsAttention);
        Assert.Contains("ท.ป.310/2563", v.Warning!);
        Assert.Contains("§54", v.Warning!);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    public void StatutoryRatesSilentOnAnyDate(int rate)
    {
        Assert.False(ThaiWhtRateTable.ClassifyRate(rate, Today).NeedsAttention);
        Assert.False(ThaiWhtRateTable.ClassifyRate(rate, InsideCovidWindow).NeedsAttention);
        Assert.Equal(ThaiWhtRateTable.RateStanding.Statutory,
            ThaiWhtRateTable.ClassifyRate(rate, Today).Standing);
    }

    /// <summary>อัตรา e-withholding (2% ยุค 2563–64 · 1% ยุค 2566–68) อยู่ใน
    /// <c>StatutoryRates</c> อยู่แล้ว (ค่าโฆษณา 2% · ค่าขนส่ง 1%) ⇒
    /// **ไม่เคยติดคำเตือน** — ล็อกข้อเท็จจริงที่หักล้างคำถามเดิมไว้ที่นี่</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void EWithholdingRatesWereNeverWarned(int rate)
        => Assert.False(ThaiWhtRateTable.ClassifyRate(rate, Today).NeedsAttention);

    [Theory]
    [InlineData(4)]
    [InlineData(2.5)]
    [InlineData(7)]
    [InlineData(20)]
    public void UnknownRatesStillWarn(double rate)
    {
        var v = ThaiWhtRateTable.ClassifyRate((decimal)rate, Today);
        Assert.Equal(ThaiWhtRateTable.RateStanding.NotRecognised, v.Standing);
        Assert.True(v.NeedsAttention);
    }

    /// <summary>ไม่ได้หัก = ไม่มีอะไรต้องเตือน (ด่านนี้ไม่ใช่ด่าน "ต้องหักไหม")</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ZeroOrNegativeRateIsSilent(int rate)
        => Assert.False(ThaiWhtRateTable.ClassifyRate(rate, Today).NeedsAttention);

    // ══════════════════════════════════════════════════════════════════
    // ความสมบูรณ์ของตาราง
    // ══════════════════════════════════════════════════════════════════

    /// <summary>อัตราลดชั่วคราวห้ามซ้ำกับอัตราถาวร — ไม่งั้นช่วงเวลาจะไม่มีผล
    /// (ชั้น <c>StatutoryRates</c> ตอบก่อนเสมอ) = ด่านที่ดูเหมือนมีแต่ไม่ทำงาน</summary>
    [Fact]
    public void TemporaryRatesDoNotShadowStatutoryRates()
    {
        foreach (var t in ThaiWhtRateTable.TemporaryReducedRates)
            Assert.DoesNotContain(t.Rate, ThaiWhtRateTable.StatutoryRates);
    }

    /// <summary>ทุกแถวต้องมีเลขที่ประกาศจริงและช่วงเวลาที่ปิด —
    /// แถวที่แต่งขึ้นจะทำให้ระบบเงียบกับใบที่หักขาด (หลักการ 10 ข้อ #3)</summary>
    [Fact]
    public void EveryTemporaryRowCitesLawAndClosedWindow()
    {
        foreach (var t in ThaiWhtRateTable.TemporaryReducedRates)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.LegalReference));
            Assert.False(string.IsNullOrWhiteSpace(t.AppliesTo));
            Assert.True(t.ToInclusive >= t.From);
        }
    }

    /// <summary><c>SnapToStatutory</c> ต้อง**ไม่** snap เข้าอัตราลดชั่วคราว —
    /// ไม่งั้นตัวอ่าน OCR จะปัดอัตราที่อ่านได้ไปหา 1.5% แล้วพาใบใหม่กลับไปใช้
    /// อัตราที่หมดอายุ (ทางอ้อมที่ทำให้ด่านนี้ไร้ผล)</summary>
    [Fact]
    public void SnapToStatutoryIgnoresTemporaryRates()
        => Assert.DoesNotContain(
            ThaiWhtRateTable.TemporaryReducedRates.Select(t => t.Rate),
            r => ThaiWhtRateTable.SnapToStatutory(r) == r);
}

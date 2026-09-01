using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฐานค่าจ้างประกันสังคม (ม.33) — คู่ (ค่าจ้าง, เงินสมทบ) บน สปส.1-10 ต้องลงตัวเสมอ
///
/// ═══ ที่มา (บั๊กจริงจากผู้ใช้) ═══
/// รอบ ส.ค. 2569: ไฟล์ สปส.1-10 ของ NAN THAN THAN MAW แสดง **ค่าจ้าง 14,094
/// คู่กับเงินสมทบ 683** ทั้งที่ 5% ของ 14,094 = 705 — เพราะ exporter เอา
/// <c>GrossIncome</c> มาใส่ช่องค่าจ้าง แต่เอายอดสมทบที่เก็บไว้มาใส่อีกช่อง
/// (สองแหล่ง ไม่มีใครตรวจว่าตรงกัน) ⇒ สปส. e-Service คิดใหม่แล้วตีกลับ
///
/// และเมื่อผู้ใช้แก้ยอดฝั่งลูกจ้าง ฝั่งนายจ้างค้างค่าเดิม ⇒ ยอดนำส่งสองฝั่ง
/// ไม่เท่ากัน (ลูกจ้าง 4,381 · นายจ้าง 4,403 — ต่างกัน 22 = ยอดที่แก้พอดี)
/// </summary>
public class SsoWageBaseTests
{
    // ปี 2569 (2026): เพดาน 17,500 · 5% · สมทบสูงสุด 875
    private const decimal Ceiling = 17_500m;
    private const decimal Rate = 0.05m;
    private const decimal MaxContribution = 875m;

    private static decimal Resolve(decimal storedBase, decimal empContribution, decimal gross)
        => SsoWageBase.Resolve(storedBase, empContribution, gross, Rate, MaxContribution);

    [Fact]
    public void ฐานที่ผู้ใช้ตั้งไว้ชนะเสมอ()
    {
        // ผู้ใช้ระบุค่าจ้างตาม ม.5 เอง (เบี้ยเลี้ยง/ค่าน้ำมันเหมาจ่ายไม่ใช่ค่าจ้าง)
        Assert.Equal(13_655m, Resolve(storedBase: 13_655m, empContribution: 683m, gross: 14_094m));
    }

    [Fact]
    public void แถวที่คู่ตัวเลขขัดกันต้องถูกซ่อมจากยอดสมทบที่หักจริง()
    {
        // เคสของผู้ใช้: gross 14,094 กับสมทบ 683 เข้ากันไม่ได้ (5% = 704.70)
        // ⇒ ต้องรายงานค่าจ้างที่ทำให้ 683 ถูกต้อง ไม่ใช่ยัด gross ลงไปเฉย ๆ
        var wage = Resolve(storedBase: 0m, empContribution: 683m, gross: 14_094m);
        Assert.Equal(13_660m, wage);
        Assert.True(SsoWageBase.IsConsistent(wage, 683m, Rate, Ceiling, MaxContribution));
    }

    [Fact]
    public void แถวที่รายได้รวมเข้ากันได้อยู่แล้วห้ามถูกแต่งใหม่()
    {
        // สมดี: gross 12,953 → 5% = 647.65 ปัดเป็น 648 ที่หักจริง = เข้ากันได้
        // การหารกลับจะได้ 12,960 = เปลี่ยนค่าจ้างที่ประกาศโดยไม่จำเป็น
        Assert.Equal(12_953m, Resolve(storedBase: 0m, empContribution: 648m, gross: 12_953m));
    }

    [Fact]
    public void ชนเพดานแล้วห้ามหารกลับ()
    {
        // สมทบ 875 = เพดาน → ค่าจ้างจริงสูงกว่าเพดาน หารกลับได้แค่ 17,500
        // แต่แบบฟอร์ม e-Service ให้กรอกค่าจ้างจริง (ไม่ cap)
        Assert.Equal(28_066m, Resolve(storedBase: 0m, empContribution: 875m, gross: 28_066m));
    }

    [Fact]
    public void ไม่ได้อยู่ในระบบประกันสังคมต้องไม่เดาค่าจ้าง()
    {
        Assert.Equal(0m, Resolve(storedBase: 0m, empContribution: 0m, gross: 20_000m));
    }

    [Fact]
    public void สองฝั่งต้องเท่ากันเป๊ะเมื่ออัตราเท่ากัน()
    {
        // หัวใจของบั๊ก: ฝั่งนายจ้างต้องคิดจาก "ยอดลูกจ้าง" ไม่ใช่คำนวณจากฐานใหม่
        // (ฐาน 12,953 → 5% = 647.65 แต่ลูกจ้างหักจริง 648 ⇒ ถ้าคิดใหม่จะต่างกัน 0.35)
        Assert.Equal(648m, SsoWageBase.EmployerFrom(648m, Rate, Rate, MaxContribution));
        Assert.Equal(683m, SsoWageBase.EmployerFrom(683m, Rate, Rate, MaxContribution));
    }

    [Fact]
    public void อัตราสองฝั่งไม่เท่ากันต้องคิดตามสัดส่วน()
    {
        // ช่วงประกาศลดอัตราชั่วคราว (เคยเกิดจริงช่วงโควิด) ลูกจ้าง 1% นายจ้าง 3%
        // ⇒ ยอดนายจ้าง = ยอดลูกจ้าง × 3 (ยังผูกกับยอดลูกจ้าง ไม่ใช่ตัวเลขลอย)
        Assert.Equal(600m, SsoWageBase.EmployerFrom(200m, 0.01m, 0.03m, MaxContribution));
    }

    [Fact]
    public void เงินสมทบต้องไม่เกินเพดานและปัดแบบ_AwayFromZero()
    {
        Assert.Equal(875m, SsoWageBase.Contribution(
            SsoWageBase.Clamp(28_066m, Ceiling), Rate, MaxContribution));
        // 12,953 × 5% = 647.65 พอดี — ต้องไม่โดน banker's rounding ทำให้เพี้ยน
        Assert.Equal(647.65m, SsoWageBase.Contribution(12_953m, Rate, MaxContribution));
    }

    [Fact]
    public void ฐานต่ำกว่าขั้นต่ำต้องถูกยกขึ้นเป็น_1650()
    {
        // ม.33 บังคับสมทบทุกคนในระบบ ฐานขั้นต่ำ 1,650
        Assert.Equal(1_650m, SsoWageBase.Clamp(900m, Ceiling));
        Assert.Equal(Ceiling, SsoWageBase.Clamp(50_000m, Ceiling));
    }

    [Theory]
    [InlineData(14_094, 683, false)]   // เคสของผู้ใช้ — ต้องจับได้
    [InlineData(13_660, 683, true)]
    [InlineData(12_953, 648, true)]    // ปัดเป็นบาทถ้วน ยังอยู่ในระยะ ±1
    [InlineData(28_066, 875, true)]    // ชนเพดาน
    public void ด่านตรวจคู่ก่อนยื่นต้องแยกออกว่าแถวไหนจะถูกตีกลับ(
        int wage, int contribution, bool expected)
    {
        Assert.Equal(expected, SsoWageBase.IsConsistent(
            wage, contribution, Rate, Ceiling, MaxContribution));
    }
}

using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>คู่ (ลูกจ้าง, นายจ้าง) ที่ระบบนอกส่งมาไม่สอดคล้องกัน — เคสจริง
///
/// <para>TakeTime ยิงรอบเงินเดือนเข้ามาโดยคิดฝั่ง**นายจ้าง**จากค่าจ้างเต็ม แต่คิด
/// ฝั่ง**ลูกจ้าง**จากฐานที่หักจริง ⇒ รวมทั้งรอบต่างกัน 22 บาท (4,381 vs 4,403)
/// และเส้น import ไม่เคยตรวจฝั่งนายจ้างเลย (ตรวจแค่ net = gross − หักฝั่งลูกจ้าง)
/// ⇒ ตัวเลขผิดติดมากับรอบตั้งแต่วินาทีแรก ไปโผล่ตอนนำส่ง สปส.</para>
///
/// <para>เทสต์ชุดนี้ล็อก **ฝั่งลูกจ้างเป็นความจริง** (เป็นยอดที่หักจากเงินเดือน
/// พนักงานไปจริงตามสลิป) — ฐานและฝั่งนายจ้างเป็นผลลัพธ์ที่คิดตาม ห้ามกลับทาง</para>
/// </summary>
public class SsoImportPairTests
{
    // ปี 2569 (2026): เพดาน 17,500 · 5% ทั้งสองฝั่ง · สมทบสูงสุด 875
    private const decimal Rate = 0.05m;
    private const decimal Max = 875m;

    private static SsoPairResult Norm(
        decimal storedBase, decimal employee, decimal gross, decimal employerOnFile)
        => SsoWageBase.Normalize(storedBase, employee, gross, employerOnFile,
            Rate, Max, Rate, Max);

    [Fact]
    public void เคสจริง_นายจ้างคิดจากค่าจ้างเต็ม_ต้องถูกดึงกลับมาเท่าฝั่งลูกจ้าง()
    {
        // พนักงานคนที่ทำให้ยอดรวมเพี้ยน: หักจริง 683 (ฐาน 13,655 หลังหักตาม ม.5)
        // แต่ต้นทางส่งฝั่งนายจ้างมา 705 (= 5% ของค่าจ้างเต็ม 14,094)
        var r = Norm(storedBase: 0m, employee: 683m, gross: 14_094m, employerOnFile: 705m);

        Assert.True(r.Changed);
        Assert.Equal(683m, r.Employer);          // เท่ากับฝั่งลูกจ้างเป๊ะ (อัตราเท่ากัน)
        Assert.Equal(13_660m, r.Base);           // 683 / 5% — ฐานที่ยอดสมทบคิดมาจริง
    }

    [Fact]
    public void ผลรวมทั้งรอบ_4403_ต้องกลายเป็น_4381()
    {
        // จำลองทั้งรอบ: 6 คน — 5 คนที่ต้นทางส่งถูกอยู่แล้ว + 1 คนที่เพี้ยน
        var rows = new[]
        {
            (emp: 750m, gross: 15_000m, er: 750m),
            (emp: 875m, gross: 17_500m, er: 875m),   // ชนเพดานพอดี
            (emp: 648m, gross: 12_953m, er: 648m),   // ต้นทางปัดเป็นบาทถ้วนแล้ว
            (emp: 600m, gross: 12_000m, er: 600m),
            (emp: 825m, gross: 16_500m, er: 825m),
            (emp: 683m, gross: 14_094m, er: 705m),   // ← ตัวที่ทำให้ต่างกัน 22
        };

        decimal beforeEr = 0, afterEr = 0, employee = 0;
        var adjusted = 0;
        foreach (var (emp, gross, er) in rows)
        {
            var r = Norm(0m, emp, gross, er);
            beforeEr += er;
            afterEr += r.Employer;
            employee += Math.Min(emp, Max);
            if (r.Changed) adjusted++;
        }

        Assert.Equal(4_403m, beforeEr);   // reproduce ตัวเลขที่ผู้ใช้เห็นก่อนแก้
        Assert.Equal(4_381m, afterEr);    // หลังแก้ = เท่ากับฝั่งลูกจ้าง
        Assert.Equal(employee, afterEr);  // invariant: สองฝั่งต้องเท่ากันเสมอ (ม.33)
    }

    [Fact]
    public void ยอดที่ต้นทางปัดเป็นบาทถ้วน_ต้องไม่ถูกขยับ()
    {
        // ฐาน 12,953 × 5% = 647.65 แต่ต้นทางหักจริง 648 (ปัดขึ้น) และส่งฝั่ง
        // นายจ้าง 648 มาด้วย → ตรงกันแล้ว ห้ามไปคิดใหม่จนกลายเป็น 647.65
        var r = Norm(0m, 648m, 12_953m, 648m);

        Assert.Equal(648m, r.Employer);
    }

    [Fact]
    public void ต่างกันไม่ถึง_1_บาท_ถือว่าปัดเศษ_ไม่แตะ()
    {
        // ผ่อนให้เท่ากับด่านตอนนำส่ง (ตัวเลขคู่ต้องใช้เกณฑ์เดียวกันทุกจุด)
        var r = Norm(13_660m, 683m, 14_094m, 682.5m);

        Assert.False(r.Changed);
        Assert.Equal(682.5m, r.Employer);
    }

    [Fact]
    public void ต้นทางไม่ส่งฝั่งนายจ้างมาเลย_ต้องเติมให้()
    {
        var r = Norm(0m, 683m, 14_094m, 0m);

        Assert.True(r.Changed);
        Assert.Equal(683m, r.Employer);
    }

    [Fact]
    public void พนักงานที่ไม่อยู่ในระบบประกันสังคม_ต้องไม่ถูกแตะ()
    {
        var r = Norm(0m, 0m, 30_000m, 0m);

        Assert.False(r.Changed);
        Assert.Equal(0m, r.Employer);
        Assert.Equal(0m, r.Base);
    }

    // ═══ เคสที่ระบบ "ตัดสินแทนไม่ได้" — ห้ามเดา ห้ามล้างยอดทิ้ง ═══
    // ที่มา: รุ่นแรกของ Normalize คิดฝั่งนายจ้างจากฝั่งลูกจ้างล้วน ⇒ ลูกจ้าง = 0
    // ทำให้ฝั่งนายจ้างถูกเขียนเป็น 0 ⇒ JE ไม่มีบรรทัด Dr 54120 และ Cr 21815
    // ขาด ⇒ หนี้สินเงินสมทบต่ำกว่าจริง + นำส่ง สปส. ขาด (เงินเพิ่ม §49)
    // และแถวนั้นถูกกรองออกจากไฟล์ สปส.1-10 ด้วย ⇒ ไม่มีใครเห็นจนกระทบยอด GL

    [Fact]
    public void ฝั่งลูกจ้างเป็นศูนย์แต่นายจ้างมียอด_ห้ามล้างเป็นศูนย์()
    {
        var r = Norm(storedBase: 0m, employee: 0m, gross: 14_094m, employerOnFile: 705m);

        Assert.NotNull(r.Conflict);              // ต้องบอกให้คนตัดสิน
        Assert.False(r.Changed);                 // ห้ามแตะอะไรเลย
        Assert.Equal(705m, r.Employer);          // ← ยอดเดิมต้องอยู่ครบ
    }

    [Fact]
    public void ลูกจ้างถูกหักเกินเพดาน_ห้ามปรับฐานตาม_ต้องรายงาน()
    {
        // 5% ของ 30,000 = 1,500 (ต้นทางไม่ cap) — เกินเพดาน 875 ตาม ม.46
        // การ "ทำให้เท่ากัน" จะกลายเป็นการรับรองการหักเกิน ซึ่งต้องคืนลูกจ้าง
        var r = Norm(0m, 1_500m, 30_000m, 875m);

        Assert.NotNull(r.Conflict);
        Assert.False(r.Changed);
        Assert.Equal(875m, r.Employer);
    }

    [Fact]
    public void ลูกจ้างถูกหักต่ำกว่าฐานขั้นต่ำ_ต้องรายงานไม่ใช่ประกาศค่าจ้างต่ำ()
    {
        // หัก 50 บาท ⇒ ฐานที่หารกลับได้ = 1,000 ซึ่งต่ำกว่าฐานขั้นต่ำ 1,650 (ม.33)
        // ถ้าปล่อยผ่านจะกลายเป็นการ "แจ้งค่าจ้างต่ำกว่าความจริง" ต่อ สปส.
        var r = Norm(0m, 50m, 1_000m, 50m);

        Assert.NotNull(r.Conflict);
        Assert.False(r.Changed);
    }

    [Fact]
    public void แยกธง_เติมฐาน_ออกจาก_ปรับยอดเงิน()
    {
        // เติมฐานอย่างเดียว (เงินไม่ขยับ) — ข้อความที่บอกผู้ใช้ต้องไม่พูดว่า
        // "ปรับยอดนายจ้าง" ทั้งที่ไม่มีบาทเดียวเปลี่ยน (ป้ายไม่ซื่อสัตย์)
        var onlyBase = Norm(0m, 648m, 12_953m, 648m);
        Assert.True(onlyBase.BaseFilled);
        Assert.False(onlyBase.EmployerAdjusted);

        // ปรับยอดเงินจริง
        var money = Norm(0m, 683m, 14_094m, 705m);
        Assert.True(money.EmployerAdjusted);
        Assert.Equal(683m, money.Employer);
    }

    [Fact]
    public void เติมฐานย้อนหลังให้แถวเก่า_ต้องไม่ขยับยอดนายจ้างที่ลงตัวอยู่แล้ว()
    {
        // แถวเก่าที่ยังไม่มี SocialSecurityBase แต่คู่สมทบตรงกันแล้ว —
        // การเติมฐานต้องไม่เป็นข้ออ้างให้ไปเขียนทับยอดที่ถูกอยู่ (ซ่อมเฉพาะที่พัง)
        var r = Norm(storedBase: 0m, employee: 648m, gross: 12_953m, employerOnFile: 648m);

        Assert.True(r.Changed);          // เพราะฐานถูกเติม
        // ฐาน = ค่าจ้างจริง (คู่เดิมเข้ากันได้ในเกณฑ์ ±1 บาท จึงคงของเดิมไว้
        // ไม่ใช่หารกลับเป็น 12,960 ซึ่งจะเปลี่ยนค่าจ้างที่ประกาศของแถวที่ถูกอยู่แล้ว)
        Assert.Equal(12_953m, r.Base);
        Assert.Equal(648m, r.Employer);  // ← ยอดนายจ้างต้องเท่าเดิมเป๊ะ
    }
}

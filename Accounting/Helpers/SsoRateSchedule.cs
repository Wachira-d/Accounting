namespace Accounting.Helpers;

/// <summary>
/// เพดานค่าจ้างประกันสังคม (มาตรา 33) ตามพระราชกฤษฎีกาปรับเพดานแบบขั้นบันได:
///   • ถึงสิ้นปี 2025 (พ.ศ. 2568): เพดาน 15,000 → สมทบสูงสุด 750/เดือน
///   • 2026–2028 (พ.ศ. 2569–2571): เพดาน 17,500 → สูงสุด 875/เดือน
///   • 2029–2031 (พ.ศ. 2572–2574): เพดาน 20,000 → สูงสุด 1,000/เดือน
///   • 2032+    (พ.ศ. 2575 เป็นต้นไป): เพดาน 23,000 → สูงสุด 1,150/เดือน
/// อัตราสมทบลูกจ้าง/นายจ้างคงที่ 5% (ยกเว้นประกาศลดชั่วคราวเป็นรายงวด —
/// override ได้ผ่านตาราง SsoYearConfigs ต่อบริษัทต่อปี)
/// ค่าในนี้คือ DEFAULT เมื่อบริษัทไม่ได้ตั้ง override; กฎหมายเปลี่ยนอีก
/// ก็เพิ่ม case ที่นี่ หรือผู้ใช้ตั้งค่าเองได้ทันทีโดยไม่ต้องรอ release.
/// </summary>
public static class SsoRateSchedule
{
    /// <summary>Default (ceiling, employeeRate) for a CE year. Buddhist-era
    /// years (≥ 2400) are normalised first so callers can pass either.</summary>
    public static (decimal WageCeiling, decimal Rate) GetDefault(int year)
    {
        var y = year > 2400 ? year - 543 : year;
        return y switch
        {
            >= 2032 => (23_000m, 0.05m),
            >= 2029 => (20_000m, 0.05m),
            >= 2026 => (17_500m, 0.05m),
            _ => (15_000m, 0.05m),
        };
    }

    /// <summary>Max monthly contribution for the year (= ceiling × rate).</summary>
    public static decimal GetMaxContribution(int year)
    {
        var (ceiling, rate) = GetDefault(year);
        return Math.Round(ceiling * rate, 2);
    }
}

using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · D-09/D-10 — ฐานภาษีสะสมของงวดก่อน · เงินสะสม PVD
/// สองทิศ: สวัสดิการยกเว้นไม่พองฐาน · แถวเก่าที่ไม่มี TaxableGross ใช้ gross เหมือนเดิม · PVD เต็มเดือนได้ยอดเดิม</summary>
public class PayrollIncomeBaseTests
{
    [Fact]
    public void งวดก่อนมีสวัสดิการยกเว้น_ใช้ฐานภาษีไม่ใช่gross()
        => Assert.Equal(50_000m, PayrollIncomeBase.PriorTaxBase(taxableGross: 50_000m, grossIncome: 58_000m));

    [Fact]
    public void แถวเก่าที่ไม่มีTaxableGross_ใช้grossเหมือนเดิม()
        => Assert.Equal(58_000m, PayrollIncomeBase.PriorTaxBase(taxableGross: 0m, grossIncome: 58_000m));

    [Fact]
    public void PVD_เข้างานกลางเดือน_คิดจากเงินเดือนที่จ่ายจริง_และปัดเศษ()
    {
        // เงินเดือน 30,000 · ทำงาน 6/30 วัน = 6,000 · 3% = 180.00 (เดิมหักเต็มเดือน 900)
        Assert.Equal(180m, PayrollIncomeBase.PvdContribution(6_000m, 3m));
        // 10,287.50 × 3% = 308.625 ⇒ 308.63 (เดิมเก็บ 308.625)
        Assert.Equal(308.63m, PayrollIncomeBase.PvdContribution(10_287.50m, 3m));
    }

    [Fact]
    public void PVD_เต็มเดือน_ได้ยอดเดิม()
        => Assert.Equal(1_500m, PayrollIncomeBase.PvdContribution(30_000m, 5m));

    [Fact]
    public void PVD_ไม่มีฐานหรืออัตรา_เป็นศูนย์()
    {
        Assert.Equal(0m, PayrollIncomeBase.PvdContribution(0m, 5m));
        Assert.Equal(0m, PayrollIncomeBase.PvdContribution(30_000m, 0m));
    }
}

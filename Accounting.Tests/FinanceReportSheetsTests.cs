using System.Reflection;
using System.Text;
using Accounting.Helpers;
using Accounting.Models.DTOs.Budget;
using Accounting.Models.DTOs.Project;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Forex;
using Accounting.Services.Implementations.PettyCash;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม X5 (2026-10-15 · EXPORT_AUDIT §X5) — คอลัมน์/แถวรวมของไฟล์ส่งออกหน้าการเงินที่เหลือ + ตัวตัดสินที่ย้ายจาก JS มาเซิร์ฟเวอร์
/// (สถานะใช้งบ · หมวดรายงานตรวจข้อมูล) — ทุกด่านมีทิศตรงข้าม
/// </summary>
public class FinanceReportSheetsTests
{
    private static readonly string[] NoTitle = Array.Empty<string>();

    private static string[] CsvLines(TabularSheet sheet)
    {
        var f = TabularExport.Render(TabularFormat.Csv, "t", null, null, sheet);
        var text = Encoding.UTF8.GetString(f.Content, 3, f.Content.Length - 3);
        return text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    // ── สถานะใช้งบ (เดิมอยู่ใน JS ของ budget.html) ──

    [Theory]
    [InlineData(1000, 1000.01, BudgetUsageStatus.Over)]     // เกินแม้สตางค์เดียว
    [InlineData(0, 1, BudgetUsageStatus.Over)]              // งบ 0 แต่มีรายจ่าย = เกินงบ
    [InlineData(1000, 800.01, BudgetUsageStatus.Near)]      // > 80%
    [InlineData(1000, 1000, BudgetUsageStatus.Near)]        // ใช้ครบพอดี = ใกล้ถึงงบ (ยังไม่เกิน)
    [InlineData(1000, 800, BudgetUsageStatus.Normal)]       // = 80% พอดี ยังปกติ (เกณฑ์ "> 80" ตามหน้าเดิม)
    [InlineData(0, 0, BudgetUsageStatus.Normal)]
    [InlineData(1000, -50, BudgetUsageStatus.Normal)]       // ยอดจริงติดลบ (กลับรายการ) ไม่ใช่เกินงบ
    public void สถานะใช้งบ_เกณฑ์เดียวกับหน้าเดิม_ทั้งสองทิศ(decimal budget, decimal actual, string expected)
        => Assert.Equal(expected, BudgetUsageStatus.Label(budget, actual));

    [Fact]
    public void เปอร์เซ็นต์ใช้ไป_ปัดAwayFromZero_และงบศูนย์เป็นศูนย์()
    {
        Assert.Equal(0.01m, BudgetUsageStatus.UsedPercent(200m, 0.01m));   // 0.005% → 0.01 (banker's จะได้ 0.00)
        Assert.Equal(33.33m, BudgetUsageStatus.UsedPercent(3m, 1m));
        Assert.Equal(0m, BudgetUsageStatus.UsedPercent(0m, 500m));
    }

    [Fact]
    public void งบเทียบจริง_แถวรวมใช้ยอดรวมของservice_ไม่รวมใหม่จากแถว()
    {
        var r = new BudgetVsActualResponse(Guid.NewGuid(), "งบ", 2026, new List<BudgetVsActualLine>
        {
            new(Guid.NewGuid(), "51000", "ค่าใช้จ่าย", 1000m, 1200m, -200m, -20m, BudgetUsageStatus.Over, 120m),
        }, new List<BudgetAlertResponse>(), TotalBudget: 7777m, TotalActual: 8888m);
        var sheet = FinanceReportSheets.BudgetVsActual(r, NoTitle);
        Assert.Single(sheet.Rows);
        var total = Assert.Single(sheet.Footer);
        Assert.Equal(7777m, total[2]);
        Assert.Equal(8888m, total[3]);
        Assert.Equal(-1111m, total[4]);
        Assert.Equal(BudgetUsageStatus.Over, total[7]);
        // ยอดติดลบในไฟล์ CSV เป็นตัวเลข (ไม่ถูกเติม ')
        Assert.Contains(CsvLines(sheet), l => l.Contains(",-200.00,"));
    }

    // ── หมวดรายงานตรวจข้อมูล (เดิมอยู่ใน JS ของ data-integrity.html) ──

    [Fact]
    public void ตรวจข้อมูล_ทุกรายการของรายงานมีหมวด_และคีย์ตรงชื่อJSON()
    {
        var listProps = typeof(DataIntegrityReport).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(List<DataIntegritySuspect>))
            .Select(p => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToList();
        Assert.True(listProps.Count >= 9);
        // เพิ่มหมวดใหม่ใน record แล้วลืมใส่หัวข้อ = แดง (หน้า/ไฟล์จะไม่แสดงหมวดนั้นเลย)
        Assert.Equal(listProps.OrderBy(x => x), DataIntegritySections.CoveredKeys.OrderBy(x => x));
        Assert.Equal(DataIntegritySections.All.Count, DataIntegritySections.All.Select(s => s.Key).Distinct().Count());
        Assert.All(DataIntegritySections.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Title) || string.IsNullOrWhiteSpace(s.Note)));
    }

    [Fact]
    public void ตรวจข้อมูล_ไฟล์_หนึ่งแถวต่อรายการพร้อมชื่อหมวด_รายงานว่างได้แผ่นว่าง()
    {
        static List<DataIntegritySuspect> L(params DataIntegritySuspect[] x) => x.ToList();
        var s1 = new DataIntegritySuspect("PosOrderTotal", Guid.NewGuid(), "POS-1", new DateTime(2026, 3, 1), "ยอดเบิ้ล", 200m, 100m);
        var s2 = new DataIntegritySuspect("PayrollRunTotal", Guid.NewGuid(), "PR-1", new DateTime(2026, 3, 2), "รวมไม่ตรง", 5m, 4m, FixPath: "x/fix");
        var report = new DataIntegrityReport(L(s1), L(), L(), L(s2), L(), L(), L(), L(), L());
        var sheet = FinanceReportSheets.DataIntegrity(report, NoTitle);
        Assert.Equal(2, sheet.Rows.Count);
        Assert.Equal("บิล POS ที่ยอดบิลไม่เท่าผลรวมรายการ", sheet.Rows[0][0]);
        Assert.Equal("รอบเงินเดือนที่ยอดรวมไม่เท่าผลรวมแถว", sheet.Rows[1][0]);
        Assert.Equal("ได้", sheet.Rows[1][7]);
        Assert.Equal("", sheet.Rows[0][7]);
        // ทิศตรงข้าม: รายงานไม่มีอะไร = แผ่นมีแต่หัวตาราง (ไม่ใช่ล้ม/ไม่ใช่แถวปลอม)
        var empty = new DataIntegrityReport(L(), L(), L(), L(), L(), L(), L(), L(), L());
        Assert.Empty(FinanceReportSheets.DataIntegrity(empty, NoTitle).Rows);
        Assert.Equal(report.Sections, DataIntegritySections.All);
    }

    // ── ตีราคาอัตราแลกเปลี่ยน ──

    [Fact]
    public void อัตราปิด_อ่านได้_และค่าที่อ่านไม่ออกล้มดัง()
    {
        var r = FinanceReportExportService.ParseRates("usd:36.5, EUR:39.125");
        Assert.Equal(36.5m, r["USD"]);
        Assert.Equal(39.125m, r["eur"]);   // ไม่สนตัวพิมพ์
        Assert.Throws<BusinessRuleException>(() => FinanceReportExportService.ParseRates("USD:abc"));
        Assert.Throws<BusinessRuleException>(() => FinanceReportExportService.ParseRates("USD:-1"));
        Assert.Throws<BusinessRuleException>(() => FinanceReportExportService.ParseRates("US:36"));
        Assert.Throws<BusinessRuleException>(() => FinanceReportExportService.ParseRates(""));   // ไม่มีสกุลเลย ≠ "ไม่มีผลต่าง"
    }

    [Fact]
    public void ตีราคา_แถวรวมเป็นกำไรขาดทุนสุทธิของservice_อัตราปิดตามที่กรอก()
    {
        var res = new FxRevaluationResult(new DateTime(2026, 3, 31), new List<FxRevaluationLine>
        {
            new("USD", "AR", 1000m, 34_000m, 36_500m, 2_500m),
            new("EUR", "AP", 500m, 19_000m, 19_562.5m, 562.5m),
        }, TotalGain: 2_500m, TotalLoss: 562.5m, NetEffect: 1_937.5m, Warnings: new List<string>());
        var sheet = FinanceReportSheets.FxRevaluation(res, FinanceReportExportService.ParseRates("USD:36.5,EUR:39.125"), NoTitle);
        Assert.Equal(36.5m, sheet.Rows[0][2]);
        Assert.Equal("เจ้าหนี้ (AP)", sheet.Rows[1][1]);
        Assert.Equal(3, sheet.Footer.Count);
        Assert.Equal(1_937.5m, sheet.Footer[2][6]);
        Assert.Null(sheet.Footer[2][3]);   // ช่องยอดเงินตราต่างประเทศของแถวรวมว่าง (ไม่ใช่ 0 ที่อ่านผิด)
    }

    // ── โครงการ / เงินสดย่อย: แถวรวมใช้ยอดของ service ──

    [Fact]
    public void รายการต้นทุนโครงการ_แถวรวมคือต้นทุนรวมของservice_ช่องอื่นว่าง()
    {
        var e = new ProjectCostEntryResponse(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 2, 1), "Labor", "ค่าแรง", 2m, 50m, 100m, true, false);
        var sheet = FinanceReportSheets.ProjectCostEntries(new[] { new ProjectCostExportRow(e, "PI-1", "PV-1") }, 12_345.67m, NoTitle);
        var f = Assert.Single(sheet.Footer);
        Assert.Equal("รวมต้นทุน", f[2]);
        Assert.Equal(12_345.67m, f[5]);
        Assert.All(f.Where((_, i) => i != 2 && i != 5), Assert.Null);
        Assert.Equal("PI-1", sheet.Rows[0][9]);
    }

    [Fact]
    public void ทะเบียนเงินสดย่อย_แถวรวมเข้าออกจากservice_ป้ายชนิดตัวเดียว()
    {
        Assert.Equal("เบิกจ่าย", PettyCashRegister.TypeLabel("Disbursement"));
        Assert.Equal("เติมเงิน", PettyCashRegister.TypeLabel("Replenishment"));
        Assert.Equal("Other", PettyCashRegister.TypeLabel("Other"));   // ค่าที่ไม่รู้จักแสดงตามจริง ไม่เดา
        var row = new PettyCashRegisterRow(Guid.NewGuid(), new DateTime(2026, 3, 1), "Disbursement", "เบิกจ่าย", "แท็กซี่", null, 80m, 0m, 80m,
            "52000", "ค่าเดินทาง", null, null, null, true, "RD-65TER-9", DateTime.UtcNow);
        var reg = new PettyCashRegister(Guid.NewGuid(), "หน้าร้าน", 5000m, 4920m, "11112 เงินสดย่อย", null, null, new[] { row }, 0m, 80m);
        var sheet = FinanceReportSheets.PettyCashRegisterSheet(reg, NoTitle);
        var f = Assert.Single(sheet.Footer);
        Assert.Equal(80m, f[5]);
        Assert.Null(f[0]);
        Assert.Equal("ใช่ RD-65TER-9", sheet.Rows[0][10]);
    }
}

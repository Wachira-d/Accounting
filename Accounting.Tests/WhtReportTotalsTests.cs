using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 273 — ตัวตั้งเดียวของ "บรรทัดไหนนับเข้ายอด ภ.ง.ด." (ยอดหัว · ตัวตรวจก่อนยื่น RD-WHT-002 · ไฟล์ e-Filing)
/// ที่มา: ตัวตรวจเดิมบวกทุกแถวรวมแถว [สรุป] ⇒ 357.30 vs ยอดหัว 178.65 (×2 พอดี)</summary>
public class WhtReportTotalsTests
{
    private static TaxReportLine L(decimal inc, decimal tax, string code = "40(8)", bool excluded = false)
        => new() { IncomeAmount = inc, TaxAmount = tax, IncomeTypeCode = code, IsExcluded = excluded, TaxPayerId = "0105556000017" };

    [Fact]
    public void R273_แถวสรุปและแถวติ๊กออกไม่นับ_ยอดเท่ากับรายการจริง()
    {
        var lines = new List<TaxReportLine>
        {
            L(4_098.55m, 122.96m), L(5_569.36m, 55.69m),
            L(9_667.91m, 178.65m, VatReportLineKind.Summary),     // [สรุป] ผู้ถูกหักรายเดียวกัน
            L(1_000m, 30m, excluded: true),                        // 50 ทวิ ร่าง
        };
        var (inc, tax) = WhtReportTotals.Of(lines);
        Assert.Equal(9_667.91m, inc);
        Assert.Equal(178.65m, tax);
        Assert.Equal(2, WhtReportTotals.CountedLines(lines).Count());
        Assert.Equal(357.30m + 30m, lines.Sum(l => l.TaxAmount));   // สูตรเดิมของตัวตรวจ = รูปของบั๊ก
    }

    [Theory]
    [InlineData("SUMMARY", true)]
    [InlineData("summary", true)]
    [InlineData(" SUMMARY ", true)]
    [InlineData("40(8)", false)]
    [InlineData("40(2)", false)]
    [InlineData(null, false)]
    public void R273_จำแนกแถวสรุป(string? code, bool isSummary)
        => Assert.Equal(isSummary, WhtReportTotals.IsSummary(new TaxReportLine { IncomeTypeCode = code }));

    [Fact]
    public void R273_ทิศตรงข้าม_รายงานไม่มีแถวพิเศษ_ยอดเท่าสูตรเดิม()
    {
        var lines = new List<TaxReportLine> { L(5_955m, 178.65m), L(1_000m, 20m) };
        Assert.Equal(lines.Sum(l => l.TaxAmount), WhtReportTotals.Of(lines).Tax);
        var r = new TaxReport { Lines = lines };
        WhtReportTotals.Apply(r);
        Assert.Equal(6_955m, r.TotalIncome);
        Assert.Equal(198.65m, r.TotalTaxWithheld);
    }
}

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
    [Fact]
    public void R273_ฝ่ายค้าน3_Apply_คำนวณแถวสรุปใหม่ตามแถวที่เหลือ()
    {
        var a = L(4_098.55m, 122.96m); var b = L(5_569.36m, 55.69m);
        var sum = L(9_667.91m, 178.65m, VatReportLineKind.Summary);
        var r = new TaxReport { Lines = new List<TaxReportLine> { a, b, sum } };
        b.IsExcluded = true;
        WhtReportTotals.Apply(r);
        Assert.Equal(122.96m, sum.TaxAmount);
        Assert.Equal(4_098.55m, sum.IncomeAmount);
        Assert.Equal(122.96m, r.TotalTaxWithheld);
        // ทิศตรงข้าม: ติ๊กกลับ ⇒ กลับเป็นยอดเดิม
        b.IsExcluded = false;
        WhtReportTotals.Apply(r);
        Assert.Equal(178.65m, sum.TaxAmount);
        Assert.Equal(178.65m, r.TotalTaxWithheld);
    }

    [Fact]
    public void R273_ฝ่ายค้าน3_แถวสรุปนับเฉพาะผู้ถูกหักของตัวเอง()
    {
        var other = new TaxReportLine { IncomeAmount = 1_000m, TaxAmount = 30m, IncomeTypeCode = "40(8)", TaxPayerId = "0107537000017" };
        var a = L(4_098.55m, 122.96m);
        var sum = L(0m, 0m, VatReportLineKind.Summary);
        var r = new TaxReport { Lines = new List<TaxReportLine> { other, a, sum } };
        WhtReportTotals.Apply(r);
        Assert.Equal(122.96m, sum.TaxAmount);
        Assert.Equal(152.96m, r.TotalTaxWithheld);
    }

    [Fact]
    public void R273_ฝ่ายค้าน4_ภงด91_แถวผลต่างภาษีไม่นับเข้ายอดหักนำส่ง()
    {
        var r = new TaxReport
        {
            Lines = new List<TaxReportLine>
            {
                L(600_000m, 20_000m, "40(1)"),
                L(0m, 1_500m, WhtReportTotals.Pnd91TaxAdditional),
                L(300_000m, 5_000m, "40(1)"),
                L(0m, -800m, WhtReportTotals.Pnd91TaxRefund),
            },
        };
        WhtReportTotals.Apply(r);
        Assert.Equal(900_000m, r.TotalIncome);
        Assert.Equal(25_000m, r.TotalTaxWithheld);   // ยอดหักจริง ไม่ใช่ 25,700 (ภาษีคำนวณ)
    }
    /// <summary>รอบ 273 ฝ่ายค้านข้อ 1 — ท้ายตาราง "รายการ" ใน tax.html ต้องแสดงยอดจากเซิร์ฟเวอร์ (linesTaxTotal) ไม่บวกเองรวมแถว [สรุป]
    /// (F2 ข้อ 5) · ใส่บั๊กกลับ (ลบการส่ง d.linesTaxTotal หรือกลับไปบวกทุกแถว) แล้วเทสต์นี้ต้องแดง</summary>
    [Fact]
    public void R273_ฝ่ายค้าน1_หน้าเว็บท้ายตารางใช้ยอดจากเซิร์ฟเวอร์()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        var page = File.ReadAllText(Path.Combine(d!.FullName, "Accounting/wwwroot/pages/tax.html"));
        Assert.Contains("renderTable('รายการ', '#334155', d.lines, d.linesTaxTotal)", page);
        Assert.Contains("const sub = serverTotal != null ? Number(serverTotal)", page);
        Assert.Contains("r.side !== 'summary'", page);   // ทางสำรอง (ตาราง ภ.พ.30) ไม่บวกแถวสรุป
        Assert.DoesNotContain("const sub = rows.filter(r => !r.isExcluded).reduce", page);
        Assert.Contains("set('linesTaxTotal', d?.linesTaxTotal)", page);   // หลังบันทึกอัตโนมัติ ท้ายตารางตามเซิร์ฟเวอร์
        Assert.Contains("const derived = ln.side === 'summary' && d.linesTaxTotal != null;", page);   // แถวสรุปล็อกช่องยอด (ไม่ silent no-op)
    }

    [Fact]
    public void R273_ฝ่ายค้าน1_ยอดท้ายตารางของ_ภงด_เท่ายอดหัว_ภพ30และภงด50ไม่มีค่า()
    {
        var lines = new List<TaxReportLine> { L(4_098.55m, 122.96m), L(5_569.36m, 55.69m), L(9_667.91m, 178.65m, VatReportLineKind.Summary) };
        Assert.Equal(178.65m, Accounting.Services.Implementations.TaxService.LinesTaxTotalOf(
            new TaxReport { TaxType = Accounting.Models.Enums.TaxType.WithholdingTax53, Lines = lines }));
        Assert.Null(Accounting.Services.Implementations.TaxService.LinesTaxTotalOf(
            new TaxReport { TaxType = Accounting.Models.Enums.TaxType.VAT, Lines = lines }));
        Assert.Null(Accounting.Services.Implementations.TaxService.LinesTaxTotalOf(
            new TaxReport { TaxType = Accounting.Models.Enums.TaxType.CorporateIncomeTax, Lines = lines }));
    }
}

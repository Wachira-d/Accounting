using Accounting.Models.Entities;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตรวจ Section65TerValidator.Evaluate — รายจ่ายต้องห้าม §65 ตรี (pure function).
/// ครอบ hard-block (11)(18), auto add-back (6)(6ทวิ)(4), และอนุมาตราที่เพิ่ม
/// (1)(2)(3)(7). ตัวเลข add-back ไหลเข้า worksheet บวกกลับ ภ.ง.ด.50.
/// </summary>
public class Section65TerValidatorTests
{
    private static readonly Guid Acc = Guid.NewGuid();

    private static Document Doc(string desc, decimal amount, decimal vat = 0m,
        string accCode = "5100", string accName = "ค่าใช้จ่าย")
    {
        var doc = new Document
        {
            TotalAmount = amount + vat,
            Lines = new List<DocumentLine>
            {
                new() { AccountId = Acc, Amount = amount, VatAmount = vat, Description = desc }
            }
        };
        return doc;
    }

    private static Section65TerValidator.Result Eval(Document doc,
        string accCode = "5100", string accName = "ค่าใช้จ่าย",
        string? payeeName = "ผู้ขาย ก", string? payeeTaxId = "0105512345678")
    {
        var accInfo = new Dictionary<Guid, (string Code, string Name)> { [Acc] = (accCode, accName) };
        return Section65TerValidator.Evaluate(doc, accInfo, payeeName, payeeTaxId,
            new Section65TerValidator.Context(AnnualRevenue: 10_000_000m, PaidUpCapital: 1_000_000m));
    }

    [Fact]
    public void HardBlocks_when_payee_missing()
    {
        var res = Eval(Doc("ค่าบริการ", 5000m), payeeName: null, payeeTaxId: null);
        Assert.True(res.HasHardBlock);
        Assert.Contains(res.Findings, f => f.RuleCode == "RD-65ter(11)(18)");
    }

    [Fact]
    public void No_block_when_payee_present()
    {
        var res = Eval(Doc("ค่าบริการทั่วไป", 5000m));
        Assert.False(res.HasHardBlock);
    }

    [Fact]
    public void Penalty_line_is_added_back_in_full()
    {
        var res = Eval(Doc("ค่าปรับจราจร", 1000m));
        var f = res.Findings.Single(x => x.RuleCode == "RD-65ter(6)");
        Assert.Equal(1000m, f.AddBackAmount);
        Assert.Equal(1000m, res.TotalAddBack);
    }

    [Fact]
    public void Corporate_income_tax_line_added_back_by_account_code()
    {
        var res = Eval(Doc("ชำระภาษี", 20000m), accCode: "CIT100", accName: "ภาษีเงินได้นิติบุคคล");
        Assert.Contains(res.Findings, f => f.RuleCode == "RD-65ter(6bis)" && f.AddBackAmount == 20000m);
    }

    [Fact]
    public void Reserve_added_back_but_provident_fund_excluded()
    {
        var reserve = Eval(Doc("ตั้งเงินสำรองเผื่อ", 3000m));
        Assert.Contains(reserve.Findings, f => f.RuleCode == "RD-65ter(1)(2)" && f.AddBackAmount == 3000m);

        var pvd = Eval(Doc("เงินสมทบกองทุนสำรองเลี้ยงชีพ", 3000m));
        Assert.DoesNotContain(pvd.Findings, f => f.RuleCode == "RD-65ter(1)(2)");
    }

    [Fact]
    public void Personal_expense_added_back_with_confirmation()
    {
        var res = Eval(Doc("ค่าใช้จ่ายส่วนตัวกรรมการ", 2000m));
        var f = res.Findings.Single(x => x.RuleCode == "RD-65ter(3)");
        Assert.Equal(2000m, f.AddBackAmount);
        Assert.True(f.NeedsConfirmation);
    }

    [Fact]
    public void Donation_is_flagged_but_not_added_back_fully()
    {
        // เงินบริจาคมี cap 2% กำไรสุทธิ คำนวณตอนปิดรอบ → per-doc ต้องไม่บวกกลับเต็ม
        var res = Eval(Doc("เงินบริจาคการกุศล", 5000m));
        var f = res.Findings.Single(x => x.RuleCode == "RD-65ter(7)");
        Assert.Equal(0m, f.AddBackAmount);
        Assert.Equal(0m, res.TotalAddBack);
    }

    [Fact]
    public void Entertainment_over_cap_adds_back_excess_only()
    {
        // cap = MAX(0.3% × 10M, 0.3% × 1M) = 30,000. จ่าย 50,000 → เกิน 20,000
        var res = Eval(Doc("ค่าเลี้ยงรับรองลูกค้า", 50_000m));
        var f = res.Findings.Single(x => x.RuleCode == "RD-65ter(4)");
        Assert.Equal(20_000m, f.AddBackAmount);
    }

    [Fact]
    public void Entertainment_within_cap_no_addback()
    {
        var res = Eval(Doc("ค่ารับรองลูกค้า", 10_000m));
        Assert.DoesNotContain(res.Findings, f => f.RuleCode == "RD-65ter(4)");
    }

    [Fact]
    public void Ordinary_expense_produces_no_findings()
    {
        var res = Eval(Doc("ค่าเช่าสำนักงาน", 8000m));
        Assert.Empty(res.Findings);
        Assert.Equal(0m, res.TotalAddBack);
    }
}

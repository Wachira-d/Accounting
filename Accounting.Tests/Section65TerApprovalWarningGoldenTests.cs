using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม TX (A-TX1) — §65 ตรี ขึ้นเป็นคำเตือน "ก่อน" อนุมัติ · <b>วัดก่อนเปิด</b> (F2 ข้อ 8): ชุดใบปกติที่พบบ่อย (ค่าใช้จ่ายเงินสดรายย่อย ·
/// ใบคีย์มือไม่มีเลขใบกำกับ · ค่ารับรองไม่เกินเพดาน · ของที่มีคำอังกฤษคล้ายคำต้องห้าม) ต้องได้ <b>0</b> คำเตือน · ชุดใบที่มีรายจ่ายต้องห้ามจริง
/// ต้องได้คำเตือนทุกใบพร้อมยอดบวกกลับ · ข้อที่บล็อกไม่อยู่ในคำเตือน (ยังโยนในธุรกรรม)
/// </summary>
public class Section65TerApprovalWarningGoldenTests
{
    private static readonly Guid Acc = Guid.NewGuid();

    private sealed record Case(string Name, string Desc, decimal Amount, string AccCode, string? PayeeName, string? PayeeTaxId,
        bool? HasSourceDoc = true, decimal Revenue = 10_000_000m, decimal PaidUp = 1_000_000m);

    private static Section65TerValidator.Result Eval(Case c)
    {
        var doc = new Document
        {
            DocumentType = DocumentType.Expense,
            TotalAmount = c.Amount,
            Lines = new List<DocumentLine> { new() { AccountId = Acc, Amount = c.Amount, VatAmount = 0m, Description = c.Desc } },
        };
        var acc = new Dictionary<Guid, (string Code, string Name)> { [Acc] = (c.AccCode, "บัญชีทดสอบ") };
        return Section65TerValidator.Evaluate(doc, acc, c.PayeeName, c.PayeeTaxId,
            new Section65TerValidator.Context(c.Revenue, c.PaidUp, PriorYtdEntertainmentExpense: 0m, HasSourceDocument: c.HasSourceDoc));
    }

    // ใบปกติที่ระบบเจอทุกวัน — ห้ามมีคำเตือนสักใบ (คำเตือนที่ฟ้องใบถูก = ผู้ใช้หัดกดผ่านโดยไม่อ่าน)
    private static readonly Case[] Ordinary =
    {
        new("เครื่องเขียน มีเลขภาษี", "ค่าเครื่องเขียนสำนักงาน", 2_500m, "53100", "บจก. เครื่องเขียนไทย", "0105551234567"),
        new("เงินสดย่อย ไม่มีเลขภาษีผู้รับ", "ค่าน้ำดื่มสำนักงาน", 800m, "53100", "ร้านน้ำดื่มหน้าปากซอย", null),
        new("คีย์มือ ไม่มีเลขใบกำกับ/ไฟล์แนบ", "ค่าบริการทำความสะอาด", 15_000m, "53200", "นายสมชาย ใจดี", "3100500123456", HasSourceDoc: false),
        new("น้ำตาลทรายขาว refined", "Refined sugar 50kg", 1_200m, "51100", "บจก. น้ำตาล", "0105551234568"),
        new("ค่าที่พักเดินทาง reservation", "Hotel reservation - ค่าที่พักพนักงานเดินทาง", 3_500m, "53300", "บจก. โรงแรม", "0105551234569"),
        new("ค่ารับรองไม่เกินเพดาน", "ค่ารับรองลูกค้า", 5_000m, "53400", "ร้านอาหาร ก", "0105551234570"),
        new("อุปกรณ์ต่ำกว่าเกณฑ์ capex", "ค่าอุปกรณ์สำนักงาน", 40_000m, "53500", "บจก. ไอที", "0105551234571"),
        new("ค่าบริการ define/fine-tune ไม่ใช่ค่าปรับ", "Fine-tuning service (define scope)", 9_000m, "53600", "บจก. ที่ปรึกษา", "0105551234572"),
        new("ค่าปรับปรุงสำนักงาน ไม่ใช่ค่าปรับ", "ค่าปรับปรุงสำนักงานชั้น 2", 20_000m, "53700", "หจก. รับเหมา", "0103551234576"),
        new("ค่าซ่อมเครื่องปรับอากาศ", "ค่าล้างและซ่อมเครื่องปรับอากาศ", 2_000m, "53700", "ร้านแอร์", "3100500123457"),
        new("ค่าบริการเพิ่มเติม ไม่ใช่เงินเพิ่ม", "ค่าบริการขนส่งเงินเพิ่มเติมจากระยะทาง", 1_500m, "53600", "บจก. ขนส่ง", "0105551234577"),
    };

    [Fact]
    public void วัดก่อนเปิด_ใบปกติ_ศูนย์คำเตือน()
    {
        var noisy = Ordinary.Where(c => Section65TerApprovalWarnings.For(Eval(c)).Count > 0).Select(c => c.Name).ToList();
        Assert.True(noisy.Count == 0, "ใบปกติที่ได้คำเตือน §65 ตรี: " + string.Join(" · ", noisy));
        // ข้อที่บันทึกอย่างเดียวยังอยู่ในผล (NonDeductibleRuleJson ไม่เปลี่ยน) — ไม่ใช่ตัดทิ้ง
        Assert.Contains(Eval(Ordinary[1]).Findings, f => f.RuleCode == "RD-65ter(11)(18)");
        Assert.Contains(Eval(Ordinary[2]).Findings, f => f.RuleCode == "RD-65ter(9)");
    }

    [Theory]
    [InlineData("ค่าปรับจราจร", 1_000, "53700", "RD-65ter(6)", "1,000.00")]
    [InlineData("ค่าใช้จ่ายส่วนตัวกรรมการ", 4_000, "53800", "RD-65ter(3)", "4,000.00")]
    [InlineData("เงินบริจาคมูลนิธิ", 10_000, "53900", "RD-65ter(7)", "RD-65ter(7)")]
    [InlineData("โน้ตบุ๊กผู้บริหาร", 80_000, "53500", "RD-65ter(5)", "RD-65ter(5)")]
    [InlineData("Late payment penalty", 300, "53700", "RD-65ter(6)", "300.00")]
    [InlineData("ค่าปรับปรุงงาน และค่าปรับล่าช้าตามสัญญา", 700, "53700", "RD-65ter(6)", "700.00")]
    [InlineData("เงินเพิ่มภาษีมูลค่าเพิ่ม", 250, "53700", "RD-65ter(6)", "250.00")]
    public void ทิศตรงข้าม_รายจ่ายต้องห้ามจริง_ต้องเห็นก่อนอนุมัติ(string desc, int amount, string accCode, string rule, string expectInText)
    {
        var w = Section65TerApprovalWarnings.For(Eval(new Case(desc, desc, amount, accCode, "ผู้รับ", "0105551234573")));
        var line = Assert.Single(w);
        Assert.StartsWith(Section65TerApprovalWarnings.Prefix, line);
        Assert.Contains(rule, line);
        Assert.Contains(expectInText, line);
    }

    [Fact]
    public void ค่ารับรองเกินเพดาน_เห็นยอดบวกกลับ()
    {
        // รายได้ 1 ล้าน ทุน 0 ⇒ เพดาน 3,000 · ค่ารับรอง 50,000 ⇒ บวกกลับ 47,000
        var w = Section65TerApprovalWarnings.For(Eval(new Case("x", "ค่ารับรองลูกค้า", 50_000m, "53400", "ร้าน", "0105551234574",
            Revenue: 1_000_000m, PaidUp: 0m)));
        Assert.Contains("47,000.00", Assert.Single(w));
    }

    [Fact]
    public void ข้อที่บล็อก_ไม่อยู่ในคำเตือน_ยังบล็อกในธุรกรรม()
    {
        var r = Eval(new Case("x", "ค่าบริการ", 5_000m, "53200", null, null));
        Assert.True(r.HasHardBlock);
        Assert.Empty(Section65TerApprovalWarnings.For(r));
    }

    [Fact]
    public void ชนิดที่ประเมิน_ตัวเดียวของธุรกรรมและคำเตือน()
    {
        Assert.True(Section65TerApprovalWarnings.AppliesTo(DocumentType.Expense, null));
        Assert.True(Section65TerApprovalWarnings.AppliesTo(DocumentType.PurchaseInvoice, null));
        Assert.True(Section65TerApprovalWarnings.AppliesTo(DocumentType.PaymentVoucher, Guid.NewGuid()));
        Assert.True(Section65TerApprovalWarnings.AppliesTo(DocumentType.CertificateInLieu, null));
        // CIL ที่แปลงจากใบตั้งหนี้ — ใบต้นทางบวกกลับไปแล้ว (ห้ามซ้ำ)
        Assert.False(Section65TerApprovalWarnings.AppliesTo(DocumentType.CertificateInLieu, Guid.NewGuid()));
        Assert.False(Section65TerApprovalWarnings.AppliesTo(DocumentType.Invoice, null));
        Assert.False(Section65TerApprovalWarnings.AppliesTo(DocumentType.TaxInvoice, null));
    }

    [Fact]
    public void คำอังกฤษจับทั้งคำ_ทั้งสองทิศ()
    {
        Assert.DoesNotContain(Eval(new Case("x", "Refined oil", 500m, "51100", "ร้าน", "0105551234575")).Findings, f => f.RuleCode == "RD-65ter(6)");
        Assert.Contains(Eval(new Case("x", "Parking fine", 500m, "53700", "ร้าน", "0105551234575")).Findings, f => f.RuleCode == "RD-65ter(6)");
        Assert.DoesNotContain(Eval(new Case("x", "Hotel reservation", 500m, "53300", "ร้าน", "0105551234575")).Findings, f => f.RuleCode == "RD-65ter(1)(2)");
        Assert.Contains(Eval(new Case("x", "General reserve", 500m, "53300", "ร้าน", "0105551234575")).Findings, f => f.RuleCode == "RD-65ter(1)(2)");
    }
}

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
        // ฝ่ายค้านรอบ 201 RTX-1: บรรทัด ≥ 50,000 ผัง 5xxxx ที่เป็นต้นทุน/ค่าเช่า/เหมาช่วง ไม่ใช่ capex — (5) บันทึกอย่างเดียว
        new("ค่าเช่าอาคาร 60,000 (54410)", "ค่าเช่าอาคารสำนักงานเดือน ก.ย.", 60_000m, "54410", "บจก. อาคาร", "0105551234578"),
        new("ซื้อวัตถุดิบ 120,000 (51210)", "แป้งสาลี 100 กระสอบ", 120_000m, "51210", "บจก. แป้ง", "0105551234579"),
        new("ค่าเหมาช่วง 250,000 (52130)", "ค่าจ้างเหมาช่วงงานติดตั้ง", 250_000m, "52130", "หจก. ช่าง", "0103551234580"),
        // RTX-2: ค่ารับรองเกินเพดานตามฐาน YTD ต้นปี — ตัวรวม ภ.ง.ด.50 คิดเพดานรายปีตอนปิดรอบ ⇒ ไม่ยกขึ้น
        new("ค่ารับรองต้นปี ฐานรายได้ YTD ยังต่ำ", "ค่ารับรองลูกค้า", 50_000m, "53400", "ร้าน", "0105551234574", Revenue: 1_000_000m, PaidUp: 0m),
        // RTX-4: fuel surcharge ไม่ใช่ค่าปรับ
        new("ค่าขนส่ง fuel surcharge", "Freight + fuel surcharge", 3_000m, "53600", "บจก. ขนส่ง", "0105551234581"),
        // RTX-3: บรรทัดที่ไม่ใช่บัญชีกำไรขาดทุน ไม่บวกกลับ (ชำระภาษี/ถอนใช้ส่วนตัว)
        new("ชำระ ภ.ง.ด.50 (21920)", "ชำระภาษีเงินได้นิติบุคคล ภ.ง.ด.50 ปี 2568", 300_000m, "21920", "กรมสรรพากร", "0994000165510"),
        new("ชำระ ภ.ง.ด.51 ล่วงหน้า (11920)", "ภาษีเงินได้นิติบุคคลจ่ายล่วงหน้า ภ.ง.ด.51", 150_000m, "11920", "กรมสรรพากร", "0994000165510"),
        new("ถอนใช้ส่วนตัว หจก. (31200)", "ถอนใช้ส่วนตัวหุ้นส่วนผู้จัดการ", 20_000m, "31200", "นายหุ้นส่วน", "3100500123458"),
    };

    [Fact]
    public void วัดก่อนเปิด_ใบปกติ_ศูนย์คำเตือน()
    {
        var noisy = Ordinary.Where(c => Section65TerApprovalWarnings.For(Eval(c)).Count > 0).Select(c => c.Name).ToList();
        Assert.True(noisy.Count == 0, "ใบปกติที่ได้คำเตือน §65 ตรี: " + string.Join(" · ", noisy));
        // ข้อที่บันทึกอย่างเดียวยังอยู่ในผล (NonDeductibleRuleJson ไม่เปลี่ยน) — ไม่ใช่ตัดทิ้ง
        Assert.Contains(Eval(Ordinary[1]).Findings, f => f.RuleCode == "RD-65ter(11)(18)");
        Assert.Contains(Eval(Ordinary[2]).Findings, f => f.RuleCode == "RD-65ter(9)");
        Assert.Contains(Eval(Ordinary[11]).Findings, f => f.RuleCode == "RD-65ter(5)");       // ค่าเช่า 60k — บันทึกไว้ ไม่ขึ้นคำเตือน
        Assert.Contains(Eval(Ordinary[14]).Findings, f => f.RuleCode == "RD-65ter(4)" && f.AddBackAmount == 47_000m);
        // RTX-3 — ไม่ใช่แค่ไม่เตือน แต่ต้องไม่บวกกลับ (ตัวรวม ภ.ง.ด.50 อ่าน TotalAddBack)
        Assert.Equal(0m, Eval(Ordinary[16]).TotalAddBack);
        Assert.Equal(0m, Eval(Ordinary[17]).TotalAddBack);
        Assert.Equal(0m, Eval(Ordinary[18]).TotalAddBack);
    }

    [Theory]
    [InlineData("ค่าปรับจราจร", 1_000, "53700", "RD-65ter(6)", "1,000.00")]
    [InlineData("ค่าใช้จ่ายส่วนตัวกรรมการ", 4_000, "53800", "RD-65ter(3)", "4,000.00")]
    [InlineData("เงินบริจาคมูลนิธิ", 10_000, "53900", "RD-65ter(7)", "RD-65ter(7)")]
    [InlineData("Late payment penalty", 300, "53700", "RD-65ter(6)", "300.00")]
    [InlineData("ค่าปรับปรุงงาน และค่าปรับล่าช้าตามสัญญา", 700, "53700", "RD-65ter(6)", "700.00")]
    [InlineData("เงินเพิ่มภาษีมูลค่าเพิ่ม", 250, "53700", "RD-65ter(6)", "250.00")]
    [InlineData("Late payment surcharges", 400, "53700", "RD-65ter(6)", "400.00")]
    [InlineData("Company fined by authority", 900, "53700", "RD-65ter(6)", "900.00")]
    [InlineData("ภาษีเงินได้นิติบุคคล (ค่าใช้จ่ายภาษี)", 5_000, "59100", "RD-65ter(6bis)", "5,000.00")]
    [InlineData("ค่าใช้จ่ายส่วนตัวกรรมการ (ไม่ผูกผัง)", 1_500, "", "RD-65ter(3)", "1,500.00")]
    public void ทิศตรงข้าม_รายจ่ายต้องห้ามจริง_ต้องเห็นก่อนอนุมัติ(string desc, int amount, string accCode, string rule, string expectInText)
    {
        var w = Section65TerApprovalWarnings.For(Eval(new Case(desc, desc, amount, accCode, "ผู้รับ", "0105551234573")));
        var line = Assert.Single(w);
        Assert.StartsWith(Section65TerApprovalWarnings.Prefix, line);
        Assert.Contains(rule, line);
        Assert.Contains(expectInText, line);
    }

    [Fact]
    public void RTX1_capex_และ_RTX2_ค่ารับรอง_บันทึกอย่างเดียว_ไม่ขึ้นคำเตือน()
    {
        // ฝ่ายค้านรอบ 201 + คำตัดสินข้อ 110 — ยังคำนวณ/เก็บใน NonDeductibleRuleJson (ตัวรวม ภ.ง.ด.50 ตัดสินตอนปิดรอบ)
        var capex = Eval(new Case("x", "โน้ตบุ๊กผู้บริหาร", 80_000m, "53500", "ร้าน", "0105551234573"));
        Assert.Contains(capex.Findings, f => f.RuleCode == "RD-65ter(5)");
        Assert.Empty(Section65TerApprovalWarnings.For(capex));
    }

    [Fact]
    public void ระบุคำเตือนชุด_65ตรี_ได้ด้วยตัวเดียว()
    {
        var w = Assert.Single(Section65TerApprovalWarnings.For(Eval(new Case("x", "ค่าปรับจราจร", 1_000m, "53700", "ผู้รับ", "0105551234573"))));
        Assert.True(Section65TerApprovalWarnings.IsWarning(w));
        Assert.False(Section65TerApprovalWarnings.IsWarning("⚠️ §86: ใบนี้เก็บ VAT"));
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

using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ครอบอนุมาตรา §65 ตรี ที่เพิ่มเข้ามารอบ task force — เดิม validator ครอบ
/// เพียง ~8 จาก 19 อนุมาตรา ทำให้ worksheet "บวกกลับ" ภ.ง.ด.50 ขาด ⇒
/// ลูกค้าเสียภาษีต่ำกว่าที่ควร (เบี้ยปรับ/เงินเพิ่มเมื่อสรรพากรตรวจ)
///
/// เน้น 2 ด้านเท่ากัน:
///   • ตรวจเจอ (ไม่ปล่อยรายจ่ายต้องห้ามผ่าน)
///   • **ไม่ over-add-back** (บวกกลับเกิน = ลูกค้าเสียภาษีเกิน ซึ่งผิดพอกัน
///     และผู้ใช้จับได้ยากกว่า)
/// </summary>
public class Section65TerExtendedTests
{
    private static readonly Guid Acc = Guid.NewGuid();

    private static Document Doc(string desc, decimal amount, decimal vat = 0m,
        bool vatClaimable = true, bool foreignService = false, DateTime? docDate = null)
        => new()
        {
            TotalAmount = amount + vat,
            DocumentDate = docDate ?? new DateTime(2026, 6, 1),
            IsForeignService = foreignService,
            Lines = new List<DocumentLine>
            {
                new() { AccountId = Acc, Amount = amount, VatAmount = vat,
                        Description = desc, IsVatClaimable = vatClaimable }
            },
        };

    private static Section65TerValidator.Result Eval(
        Document doc, Section65TerValidator.Context? ctx = null,
        string accCode = "5100", string accName = "ค่าใช้จ่าย",
        string? payeeName = "ผู้ขาย ก", string? payeeTaxId = "0105512345678")
    {
        var accInfo = new Dictionary<Guid, (string Code, string Name)> { [Acc] = (accCode, accName) };
        return Section65TerValidator.Evaluate(doc, accInfo, payeeName, payeeTaxId,
            ctx ?? new Section65TerValidator.Context(10_000_000m, 1_000_000m));
    }

    private static bool Has(Section65TerValidator.Result r, string ruleCode)
        => r.Findings.Any(f => f.RuleCode == ruleCode);

    // ───────── (8) รายจ่ายไม่ได้จ่ายจริง ─────────
    [Fact]
    public void ข้อ8_ไม่มีหลักฐานการจ่าย_ต้องเตือน()
    {
        var r = Eval(Doc("ค่าบริการ", 10_000m),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, HasPaymentEvidence: false));
        Assert.True(Has(r, "RD-65ter(8)"));
    }

    [Fact]
    public void ข้อ8_ไม่ส่งข้อมูลมา_ต้องไม่เตือน()   // null = ไม่ทราบ ห้ามเดา
        => Assert.False(Has(Eval(Doc("ค่าบริการ", 10_000m)), "RD-65ter(8)"));

    // ───────── (9) ไม่มีเอกสารต้นฉบับ ─────────
    [Fact]
    public void ข้อ9_ไม่มีเอกสารต้นฉบับ_ต้องเตือน()
    {
        var r = Eval(Doc("ค่าบริการ", 10_000m),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, HasSourceDocument: false));
        Assert.True(Has(r, "RD-65ter(9)"));
    }

    [Fact]
    public void ข้อ9_มีเอกสารต้นฉบับ_ต้องไม่เตือน()
    {
        var r = Eval(Doc("ค่าบริการ", 10_000m),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, HasSourceDocument: true));
        Assert.False(Has(r, "RD-65ter(9)"));
    }

    // ───────── (10) รายจ่ายรอบก่อน ─────────
    [Fact]
    public void ข้อ10_เอกสารลงวันที่ก่อนรอบบัญชีปัจจุบัน_ต้องเตือน()
    {
        var r = Eval(Doc("ค่าบริการ", 10_000m, docDate: new DateTime(2025, 12, 20)),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m,
                CurrentFiscalYearStart: new DateTime(2026, 1, 1)));
        Assert.True(Has(r, "RD-65ter(10)"));
    }

    [Fact]
    public void ข้อ10_เอกสารในรอบปัจจุบัน_ต้องไม่เตือน()
    {
        var r = Eval(Doc("ค่าบริการ", 10_000m, docDate: new DateTime(2026, 3, 1)),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m,
                CurrentFiscalYearStart: new DateTime(2026, 1, 1)));
        Assert.False(Has(r, "RD-65ter(10)"));
    }

    // ───────── (12) ดอกเบี้ยของทุนตนเอง ─────────
    [Fact]
    public void ข้อ12_ดอกเบี้ยเงินทุน_ต้องบวกกลับเต็ม()
    {
        var r = Eval(Doc("ดอกเบี้ยเงินทุนของกิจการ", 50_000m));
        Assert.True(Has(r, "RD-65ter(12)"));
        Assert.Equal(50_000m, r.TotalAddBack);
    }

    [Fact]
    public void ข้อ12_ดอกเบี้ยเงินกู้ธนาคารปกติ_ต้องไม่โดน()
        => Assert.False(Has(Eval(Doc("ดอกเบี้ยเงินกู้ธนาคาร", 50_000m)), "RD-65ter(12)"));

    // ───────── (13) เช่าทรัพย์สินของตัวเอง ─────────
    [Fact]
    public void ข้อ13_จ่ายค่าเช่าให้เลขภาษีเดียวกับบริษัท_ต้องบวกกลับ()
    {
        var r = Eval(Doc("ค่าเช่าอาคารสำนักงาน", 100_000m),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, CompanyTaxId: "0105512345678"),
            payeeTaxId: "0105512345678");
        Assert.True(Has(r, "RD-65ter(13)"));
        Assert.Equal(100_000m, r.TotalAddBack);
    }

    [Fact]
    public void ข้อ13_เช่าจากบุคคลภายนอก_ต้องไม่โดน()
    {
        var r = Eval(Doc("ค่าเช่าอาคารสำนักงาน", 100_000m),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, CompanyTaxId: "0105512345678"),
            payeeTaxId: "0995000123456");
        Assert.False(Has(r, "RD-65ter(13)"));
    }

    // ───────── (14) ไม่เกี่ยวกับกิจการ ─────────
    [Fact]
    public void ข้อ14_ระบุว่าไม่เกี่ยวกิจการ_ต้องบวกกลับเต็มยอดเอกสาร()
    {
        var r = Eval(Doc("ค่าใช้จ่าย", 20_000m),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, RelatedToBusiness: false));
        Assert.True(Has(r, "RD-65ter(14)"));
        Assert.Equal(20_000m, r.TotalAddBack);
    }

    // ───────── (15) transfer pricing ─────────
    [Fact]
    public void ข้อ15_คู่ค้าเกี่ยวโยงกัน_เตือนแต่ห้ามบวกกลับเอง()
    {
        var r = Eval(Doc("ค่าที่ปรึกษา", 500_000m),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, IsRelatedParty: true));
        Assert.True(Has(r, "RD-65ter(15)"));
        // ระบบไม่มีราคาตลาดอ้างอิง → ห้ามเดาแล้วบวกกลับ (ลูกค้าเสียภาษีเกิน)
        Assert.Equal(0m, r.TotalAddBack);
    }

    // ───────── (19) รายจ่ายต่างประเทศ ─────────
    [Fact]
    public void ข้อ19_บริการต่างประเทศไม่เชื่อมกิจการไทย_ต้องบวกกลับ()
    {
        var r = Eval(Doc("Cloud subscription", 30_000m, foreignService: true),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, LinkedToThaiOperation: false));
        Assert.True(Has(r, "RD-65ter(19)"));
        Assert.Equal(30_000m, r.TotalAddBack);
    }

    [Fact]
    public void ข้อ19_บริการต่างประเทศที่เชื่อมกิจการไทย_ต้องไม่โดน()
    {
        var r = Eval(Doc("Cloud subscription", 30_000m, foreignService: true),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, LinkedToThaiOperation: true));
        Assert.False(Has(r, "RD-65ter(19)"));
    }

    // ───────── (4) cap เมื่อไม่มีฐานคำนวณ — ต้องไม่ over-add-back ─────────
    [Fact]
    public void ข้อ4_ไม่มีรายได้และทุน_ต้องไม่บวกกลับค่ารับรองทั้งก้อน()
    {
        // บั๊กเดิม: cap = MAX(0.3%×null→0, 0.3%×null→0) = 0 ⇒ บวกกลับเต็มจำนวน
        var r = Eval(Doc("ค่ารับรองลูกค้า", 80_000m),
            new Section65TerValidator.Context(null, null));
        Assert.True(Has(r, "RD-65ter(4)"));
        Assert.Equal(0m, r.TotalAddBack);          // เตือนอย่างเดียว ไม่บวกกลับ
    }

    [Fact]
    public void ข้อ4_มีฐานคำนวณ_ต้องบวกกลับเฉพาะส่วนเกิน()
    {
        // cap = MAX(10,000,000×0.3%, 1,000,000×0.3%) = 30,000
        var r = Eval(Doc("ค่ารับรองลูกค้า", 50_000m));
        Assert.Equal(20_000m, r.TotalAddBack);     // 50,000 − 30,000
    }

    // ───────── VAT ที่เคลมได้ ต้องไม่ถูกนับเป็นต้นทุนบวกกลับ ─────────
    [Fact]
    public void VAT_ที่เคลมได้_ต้องไม่รวมในยอดบวกกลับ()
    {
        var r = Eval(Doc("ค่าปรับจราจร", 1_000m, vat: 70m, vatClaimable: true));
        Assert.Equal(1_000m, r.TotalAddBack);      // ไม่ใช่ 1,070
    }

    [Fact]
    public void VAT_ที่เคลมไม่ได้_ต้องรวมในยอดบวกกลับ()
    {
        var r = Eval(Doc("ค่าปรับจราจร", 1_000m, vat: 70m, vatClaimable: false));
        Assert.Equal(1_070m, r.TotalAddBack);      // VAT กลายเป็นต้นทุนจริง
    }

    // ───────── (11)(18) ขาดเลขผู้เสียภาษี ─────────
    [Fact]
    public void ขาดเลขผู้เสียภาษี_default_เตือนแต่ไม่บล็อก()
    {
        var r = Eval(Doc("ค่าบริการ", 5_000m), payeeTaxId: null);
        Assert.True(Has(r, "RD-65ter(11)(18)"));
        Assert.False(r.HasHardBlock);              // ไม่บล็อกค่าใช้จ่ายเงินสดรายย่อย
    }

    [Fact]
    public void ขาดเลขผู้เสียภาษี_เมื่อเปิดโหมดเข้ม_ต้องบล็อก()
    {
        var r = Eval(Doc("ค่าบริการ", 5_000m),
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, StrictPayeeIdentification: true),
            payeeTaxId: null);
        Assert.True(r.HasHardBlock);
    }

    [Fact]
    public void ขาดทั้งชื่อและเลขภาษี_ต้องบล็อกเสมอ()
        => Assert.True(Eval(Doc("ค่าบริการ", 5_000m), payeeName: null, payeeTaxId: null).HasHardBlock);
}

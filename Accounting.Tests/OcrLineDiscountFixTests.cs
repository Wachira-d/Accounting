using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม E 2026-10-13 — ตัวตรวจ/ตัวแก้ "เอกสารจากสแกนที่เปิดแก้แล้วบันทึก ยอดขยับเอง" (ส่วนลด % ซ้อนบนราคาที่ลดแล้ว · ก่อนทีม D B5)
/// · ตัวคำนวณ "บันทึกซ้ำ" = <c>DocumentService.ResaveLineNets</c> (เรียก ComputeLineAmounts + ReconcileTaxRounding ตัวจริง ไม่ใช่สำเนา)
/// · เทสต์สองทิศ: ใบที่พังถูกจับ + แก้แล้วบันทึกซ้ำได้ยอดเดิม · ใบที่ถูกอยู่แล้วไม่ถูกจับ
/// </summary>
public class OcrLineDiscountFixTests
{
    private static DocumentLine Line(int order, decimal qty, decimal unitPrice, decimal pct, decimal disc, decimal amount,
        decimal vat = 0m, decimal rate = 7m)
        => new() { LineOrder = order, Description = $"รายการ {order}", Quantity = qty, UnitPrice = unitPrice, DiscountPercent = pct,
            DiscountAmount = disc, Amount = amount, VatRate = rate, VatAmount = vat };

    private static decimal[] Resave(Document d, IReadOnlyList<DocumentLine> lines)
        => DocumentService.ResaveLineNets(lines.Select(l => DocumentService.ResaveRequest(l)).ToList(),
            d.PricesIncludeVat, d.BillDiscountPercent, d.BillDiscountAmount, d.DepositBaseDeducted);

    [Fact]
    public void ใบที่ตัวสร้างเก่าเขียน_ราคาเท่ายอดหลังลด_ติด5เปอร์เซ็นต์_ถูกจับ_บันทึกซ้ำได้902_50()
    {
        var doc = new Document { PricesIncludeVat = false };
        var lines = new List<DocumentLine> { Line(1, 1m, 950m, 5m, 0m, 950m, 66.50m), Line(2, 1m, 475m, 5m, 0m, 475m, 33.25m) };
        var found = DocumentService.FindOcrLineDiscountIssues(doc, lines);
        Assert.Equal(2, found.Count);
        Assert.Equal((950m, 902.50m), (found[0].Stored, found[0].AfterResave));
        Assert.Equal((475m, 451.25m), (found[1].Stored, found[1].AfterResave));
    }

    [Fact]
    public void ทิศตรงข้าม_บรรทัดที่ถูกอยู่แล้ว_ไม่ถูกจับ()
    {
        var doc = new Document { PricesIncludeVat = false };
        var lines = new List<DocumentLine>
        {
            Line(1, 2m, 500m, 5m, 0m, 950m),     // ราคาเต็มตามกระดาษ + 5% ⇒ บันทึกซ้ำได้ 950 = ที่เก็บ
            Line(2, 1m, 950m, 5m, 0.01m, 949.99m),// มีส่วนลดบาท (> 0) ⇒ ไม่ใช่รูปของบั๊กนี้
            Line(3, 1m, 950m, 0m, 0m, 950m),     // ไม่มี %
        };
        Assert.Empty(DocumentService.FindOcrLineDiscountIssues(doc, lines));
    }

    [Fact]
    public void ซ่อม_จำนวน1_ราคาคงเดิม_ส่วนลดศูนย์_บันทึกซ้ำได้ยอดเดิม()
    {
        var r = DocumentService.OcrLineDiscountRepair(1m, 950m, 7m, false, 950m, 66.50m);
        Assert.Equal((950m, 0m), r);
        var doc = new Document { PricesIncludeVat = false };
        var fixedLine = Line(1, 1m, r!.Value.UnitPrice, 0m, r.Value.DiscountAmount, 950m);
        Assert.Equal(950m, Resave(doc, new[] { fixedLine })[0]);
    }

    [Fact]
    public void ซ่อม_จำนวน2แบบสูตรเก่า_ส่วนลดบาทชดเชย_ราคาคงเดิม()
    {
        // ตัวสร้างก่อน B5 กับจำนวน 2 ไม่มีราคา: ราคา 950 · 5% · บาท 0 ⇒ บันทึกซ้ำ 1,805 · ซ่อม = ส่วนลดบาท 950 ⇒ 950
        var r = DocumentService.OcrLineDiscountRepair(2m, 950m, 7m, false, 950m, 66.50m);
        Assert.Equal((950m, 950m), r);
        Assert.Equal(950m, Resave(new Document(), new[] { Line(1, 2m, 950m, 0m, 950m, 950m) })[0]);
    }

    [Fact]
    public void ซ่อม_จำนวน0_35_ราคาปัดขึ้น_ส่วนลดไม่ติดลบ()
    {
        var r = DocumentService.OcrLineDiscountRepair(0.35m, 100m, 7m, false, 100m, 7m);
        Assert.Equal((285.72m, 0.00m), r);
        Assert.Equal(100m, Resave(new Document(), new[] { Line(1, 0.35m, 285.72m, 0m, 0m, 100m) })[0]);
    }

    [Fact]
    public void ซ่อม_ราคารวมVAT_ยอดรวมVATเป้าจากVATที่เก็บ_ถอดแล้วได้ยอดก่อนVATเดิม()
    {
        // ใบราคารวม VAT: ยอดก่อน VAT 950 · VAT 66.50 ⇒ ยอดรวม 1,016.50 · ราคาที่เก็บ 1,016.50 (= ยอดรวมหลังลด) + 5%
        var doc = new Document { PricesIncludeVat = true };
        var bad = Line(1, 1m, 1016.50m, 5m, 0m, 950m, 66.50m);
        var found = DocumentService.FindOcrLineDiscountIssues(doc, new[] { bad });
        Assert.Single(found);
        var r = DocumentService.OcrLineDiscountRepair(1m, 1016.50m, 7m, true, 950m, 66.50m);
        Assert.Equal((1016.50m, 0m), r);
        Assert.Equal(950m, Resave(doc, new[] { Line(1, 1m, 1016.50m, 0m, 0m, 950m, 66.50m) })[0]);
    }

    [Fact]
    public void ซ่อมไม่ได้_จำนวนศูนย์หรือติดลบ_คืนnull_ผู้เรียกปฏิเสธพร้อมเหตุผล()
    {
        Assert.Null(DocumentService.OcrLineDiscountRepair(0m, 950m, 7m, false, 950m, 0m));
        Assert.Null(DocumentService.OcrLineDiscountRepair(-1m, 950m, 7m, false, 950m, 0m));
    }

    // ── ฝ่ายค้านรอบ 204 L2: ใบขายที่ออกแล้ว (§86/4) ห้ามแก้รายการที่พิมพ์ ──

    [Theory]
    [InlineData(Accounting.Models.Enums.DocumentType.TaxInvoice)]
    [InlineData(Accounting.Models.Enums.DocumentType.Invoice)]
    [InlineData(Accounting.Models.Enums.DocumentType.Receipt)]
    public void Issued_sales_document_is_refused_with_reissue_way_forward(Accounting.Models.Enums.DocumentType type)
    {
        Assert.True(Accounting.Helpers.OcrLineDiscountFixScope.IsOurSalesDocument(type, null));
        var why = Accounting.Helpers.OcrLineDiscountFixScope.Refusal("TI-1", Accounting.Models.Enums.DocumentStatus.Approved, true);
        Assert.NotNull(why);
        Assert.Contains("§86/4", why);
        Assert.Contains("ยกเลิกใบนี้แล้วออกใบแทน", why);
        // ทิศตรงข้าม: ใบขายร่าง (ยังไม่มีเลขที่) ⇒ แก้ได้
        Assert.Null(Accounting.Helpers.OcrLineDiscountFixScope.Refusal("DRAFT-x", Accounting.Models.Enums.DocumentStatus.Draft, true));
    }

    [Fact]
    public void Purchase_documents_any_status_and_purchase_side_notes_stay_fixable()
    {
        foreach (var t in new[] { Accounting.Models.Enums.DocumentType.PurchaseInvoice, Accounting.Models.Enums.DocumentType.Expense,
                     Accounting.Models.Enums.DocumentType.PaymentVoucher })
        {
            Assert.False(Accounting.Helpers.OcrLineDiscountFixScope.IsOurSalesDocument(t, null));
            Assert.Null(Accounting.Helpers.OcrLineDiscountFixScope.Refusal("PI-1", Accounting.Models.Enums.DocumentStatus.Paid, false));
        }
        Assert.False(Accounting.Helpers.OcrLineDiscountFixScope.IsOurSalesDocument(Accounting.Models.Enums.DocumentType.CreditNote, (true, false)));
        // ใบลดหนี้ฝั่งขาย / ระบุฝั่งไม่ได้ ⇒ ถือเป็นใบขาย (ใบที่ออกแล้วต้องไม่ถูกแก้เงียบ)
        Assert.True(Accounting.Helpers.OcrLineDiscountFixScope.IsOurSalesDocument(Accounting.Models.Enums.DocumentType.CreditNote, (false, false)));
        Assert.True(Accounting.Helpers.OcrLineDiscountFixScope.IsOurSalesDocument(Accounting.Models.Enums.DocumentType.DebitNote, (false, true)));
        Assert.True(Accounting.Helpers.OcrLineDiscountFixScope.IsOurSalesDocument(Accounting.Models.Enums.DocumentType.CreditNote, null));
    }

    // ── ฝ่ายค้านข้อ 159 ข้อ 3 (ทีม T 2026-10-26): ResaveLineNets เรียก PlanLineAmounts ตัวตั้งตัวเดียว — ผลต้องเท่าเดิม ──
    [Fact]
    public void ResaveLineNets_เท่ากับ_PlanLineAmounts_ทั้งโหมดส่วนลดท้ายบิลบาทและเปอร์เซ็นต์()
    {
        var lines = new List<DocumentLine> { Line(1, 2m, 500m, 5m, 0m, 950m), Line(2, 1m, 475m, 0m, 25m, 450m) };
        var reqs = lines.Select(l => DocumentService.ResaveRequest(l)).ToList();
        foreach (var incl in new[] { false, true })
        {
            Assert.Equal(DocumentService.PlanLineAmounts(reqs, incl, 100m).Select(a => a.Net).ToArray(),
                DocumentService.ResaveLineNets(reqs, incl, 0m, 100m, 0m));
            Assert.Equal(DocumentService.PlanLineAmounts(reqs, incl, 0m, billDiscountPercent: 10m).Select(a => a.Net).ToArray(),
                DocumentService.ResaveLineNets(reqs, incl, 10m, 0m, 0m));
        }
        // ตัวเลขล็อก (ราคาก่อน VAT · ส่วนลดท้ายบิล 100 บนฐาน 950 + 450 = 1,400 ⇒ 67.86/32.14): เท่าเดิมก่อนรวมตัวตั้ง
        Assert.Equal(new[] { 882.14m, 417.86m }, DocumentService.ResaveLineNets(reqs, false, 0m, 100m, 0m));
    }
}

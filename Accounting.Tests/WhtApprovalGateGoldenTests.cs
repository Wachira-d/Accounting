using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ชุด "กระดาษจริง" ก่อน/หลังถอดด่านหัก ณ ที่จ่ายชุดเก่า** (ผลตรวจ D1-B1/D1-B2 รอบ 181)
///
/// <para>กฎเหล็ก #4 H บังคับว่า "ก่อนใส่ด่าน/ถอด heuristic ตัวไหน ต้องรันชุดกระดาษจริง
/// ในเทสต์ก่อนและหลัง แล้วอธิบายทุกใบที่คำตอบเปลี่ยน" — ไฟล์นี้คือชุดนั้น. สามคอลัมน์:</para>
/// <list type="number">
/// <item><b>ด่านเก่า</b> — สำเนาตรรกะที่ถอดออกจาก <c>CollectApprovalWarningsAsync</c>
///   (<c>IsPureGoodsPurchaseAsync</c> + <c>CheckWhtThresholdAsync</c>) เก็บไว้ที่นี่
///   ที่เดียวเพื่อเทียบผล <b>ห้ามมีสำเนาในโค้ดจริงอีก</b></item>
/// <item><b>ด่านใหม่ ก่อนแก้ขอบเขต</b> — ใช้ <c>DocumentSide.IsPurchase(type)</c>
///   โดยไม่ส่งบทบาท (สภาพก่อน D1-B2)</item>
/// <item><b>ด่านใหม่ หลังแก้</b> — ใช้ <c>WhtGateScope.Applies(type, cnDnPurchaseSide)</c></item>
/// </list>
///
/// <para>ทุกเคสรันในโหมด <b>kill-switch</b> (ปิด provider ทุกตัว + นักเรียนยังไม่มีคลัง)
/// ⇒ ชั้น "ถามชั้นเรียนรู้" ตอบไม่ได้ ⇒ เคสที่ "มีเหตุให้สงสัย" จบที่ "เตือน"
/// ซึ่งเป็นทิศที่ต้องล็อกไว้ (ถ้านักเรียนตอบได้ มันจะเงียบลงได้อีก แต่ห้ามเงียบมากกว่านี้)</para>
/// </summary>
public class WhtApprovalGateGoldenTests
{
    // ── กระดาษหนึ่งใบในชุดทดสอบ ───────────────────────────────────────────
    private sealed record Paper(
        string Name,
        DocumentType Type,
        bool? CnDnPurchaseSide,
        decimal SubTotal,
        IReadOnlyList<Line> Lines,
        bool? PaperShowsWht,
        PaperTaxInvoiceGrade Grade,
        IReadOnlyList<WhtPaymentRow> YearRows,
        bool PayeeProvenIndividual = false);

    /// <param name="BoundToStockProduct">บรรทัดผูกรหัสสินค้าที่ตัดสต๊อก (ด่านเก่าใช้)</param>
    /// <param name="GoodsAccount">บรรทัดลงผัง 115x/51xxx (ด่านเก่าใช้)</param>
    private sealed record Line(
        string Description, decimal Amount, ProductType? Kind = null,
        string? IncomeTypeCode = null,
        bool BoundToStockProduct = false, bool GoodsAccount = false);

    private static WhtLineFact ToFact(Line l) => new(l.Description, l.IncomeTypeCode, l.Kind, l.Amount);

    // ══════════════════════════════════════════════════════════════════════
    //  ด่านเก่า — สำเนาตรรกะที่ถอดออก (ไว้เทียบผลเท่านั้น)
    //     if ((type is PV|Expense|PI) && wht == 0 && subTotal > 0)
    //         looksLikeGoods = ทุกบรรทัดที่มียอด ผูกสินค้าตัดสต๊อก หรือ ผัง 115x/51xxx
    //         (required, ytd) = ผลรวม SubTotal ของ PI+Expense+PV ปีนี้ **ตรง ๆ** + ใบนี้ ≥ 1,000
    //         if (required && !looksLikeGoods) → เตือน
    // ══════════════════════════════════════════════════════════════════════
    private static bool OldGateWarns(Paper p)
    {
        if (p.Type is not (DocumentType.PaymentVoucher or DocumentType.Expense
            or DocumentType.PurchaseInvoice)) return false;
        if (p.SubTotal <= 0m) return false;

        var priced = p.Lines.Where(l => l.Amount > 0m).ToList();
        var looksLikeGoods = priced.Count > 0
            && priced.All(l => l.BoundToStockProduct || l.GoodsAccount);

        // ไม่กันนับซ้ำ · ไม่รู้จัก CIL/DN — ตรงตามคิวรีเดิม
        var ytd = p.YearRows
            .Where(r => r.Type is DocumentType.PurchaseInvoice or DocumentType.Expense
                or DocumentType.PaymentVoucher)
            .Sum(r => r.SubTotal);
        var required = ytd + p.SubTotal >= 1000m;
        return required && !looksLikeGoods;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ด่านใหม่ — เรียก helper ตัวจริงทุกตัว (ไม่มีสำเนาตรรกะ)
    // ══════════════════════════════════════════════════════════════════════
    private static bool NewGateWarns(Paper p, bool withScopeFix)
    {
        var inScope = withScopeFix
            ? WhtGateScope.Applies(p.Type, p.CnDnPurchaseSide)
            : DocumentSide.IsPurchase(p.Type);          // สภาพก่อนแก้ D1-B2
        if (!inScope || p.SubTotal <= 0m) return false;

        var facts = p.Lines.Select(ToFact).ToList();
        var evidence = WhtApplicabilityEvidence.Judge(facts, p.PaperShowsWht, p.Grade);
        if (evidence.Level == WhtApplicability.NotApplicable) return false;

        var cumulative = WhtCumulativeScope.SumDistinct(p.YearRows);
        if (!ThaiWhtRateTable.ShouldWithhold(p.SubTotal, cumulative)) return false;

        if (evidence.Level == WhtApplicability.ServiceWithholding) return true;
        // kill-switch: ชั้นเรียนรู้ตอบไม่ได้ ⇒ "มีเหตุ" = เตือน · "ไม่มีเหตุ" = เงียบ
        return WhtServiceHints.Scan(facts, p.PayeeProvenIndividual).Suspicious;
    }

    // ── ชุดกระดาษ ─────────────────────────────────────────────────────────
    private static readonly Guid PiId = Guid.NewGuid();
    private static readonly Guid PvId = Guid.NewGuid();

    private static Paper Ikea() => new(
        "ใบซื้อของร้านค้าปลีก (แผ่นปะเต็นท์/ผ้าปูพื้น/ค่าส่ง) — ใบกำกับเต็มรูปครบ §86/4",
        DocumentType.PurchaseInvoice, null, 5_682.24m,
        new[]
        {
            new Line("แผ่นปะเต็นท์ PE", 3_200m),
            new Line("ผ้าปูพื้น", 2_432.24m),
            new Line("ค่าจัดส่ง", 50m),
        },
        PaperShowsWht: false, PaperTaxInvoiceGrade.Complete, Array.Empty<WhtPaymentRow>());

    private static Paper GoodsWithProductMaster() => new(
        "ใบซื้อสินค้าที่ทุกบรรทัดผูกรหัสสินค้าในระบบ",
        DocumentType.PurchaseInvoice, null, 4_000m,
        new[]
        {
            new Line("กระดาษ A4", 4_000m, ProductType.Product, BoundToStockProduct: true),
        },
        PaperShowsWht: null, PaperTaxInvoiceGrade.Unknown, Array.Empty<WhtPaymentRow>());

    private static Paper LegalService() => new(
        "ใบแจ้งหนี้ค่าที่ปรึกษากฎหมาย (คีย์มือ ไม่มีกระดาษให้ดู)",
        DocumentType.PurchaseInvoice, null, 20_000m,
        new[] { new Line("ค่าที่ปรึกษากฎหมาย เดือนสิงหาคม", 20_000m) },
        PaperShowsWht: null, PaperTaxInvoiceGrade.Unknown, Array.Empty<WhtPaymentRow>());

    private static Paper ServicePurchaseOrder() => new(
        "ใบสั่งซื้อค่าบริการ 50,000 (ยังไม่มีการจ่ายเงิน)",
        DocumentType.PurchaseOrder, null, 50_000m,
        new[] { new Line("ค่าบริการติดตั้งระบบ", 50_000m) },
        PaperShowsWht: null, PaperTaxInvoiceGrade.Unknown, Array.Empty<WhtPaymentRow>());

    private static Paper SalesCreditNote() => new(
        "ใบลดหนี้ที่**เรา**ออกให้ลูกค้า (ฝั่งขาย) — มีคำว่าค่าบริการในรายการ",
        DocumentType.CreditNote, false, 12_000m,
        new[] { new Line("ลดหนี้ค่าบริการรายเดือน", 12_000m) },
        PaperShowsWht: null, PaperTaxInvoiceGrade.Unknown, Array.Empty<WhtPaymentRow>());

    private static Paper DoubleCountedInstalment() => new(
        "ค่าบริการงวดที่ 3 (300) — งวดก่อนออกทั้งใบกำกับ 600 และใบสำคัญจ่ายที่แปลงมาจากใบนั้น",
        DocumentType.Expense, null, 300m,
        new[] { new Line("ค่าบริการดูแลระบบ งวดที่ 3", 300m) },
        PaperShowsWht: null, PaperTaxInvoiceGrade.Unknown,
        new[]
        {
            new WhtPaymentRow(PiId, DocumentType.PurchaseInvoice, 600m, null, null),
            new WhtPaymentRow(PvId, DocumentType.PaymentVoucher, 600m, PiId, null),
        });

    private static Paper RentOnIncompletePaper() => new(
        "ใบค่าเช่าโกดังที่สแกนมาไม่ครบ §86/4",
        DocumentType.Expense, null, 1_200m,
        new[] { new Line("ค่าเช่าโกดัง เดือนกันยายน", 1_200m) },
        PaperShowsWht: false, PaperTaxInvoiceGrade.Incomplete, Array.Empty<WhtPaymentRow>());

    private static Paper PaperDeclaresWht() => new(
        "ใบกำกับที่ผู้ขายพิมพ์บรรทัด 'หัก ณ ที่จ่าย' มาเอง",
        DocumentType.PurchaseInvoice, null, 2_000m,
        new[] { new Line("งานรับเหมาติดตั้ง", 2_000m) },
        PaperShowsWht: true, PaperTaxInvoiceGrade.Complete, Array.Empty<WhtPaymentRow>());

    private static Paper GoodsByAccountOnly() => new(
        "ใบซื้อของที่ไม่ผูกรหัสสินค้า แต่ลงผังต้นทุนสินค้า 51xxx",
        DocumentType.PurchaseInvoice, null, 1_500m,
        new[] { new Line("ซื้อกระดาษ A4 กล่องละ 500", 1_500m, GoodsAccount: true) },
        PaperShowsWht: null, PaperTaxInvoiceGrade.Unknown, Array.Empty<WhtPaymentRow>());

    private static Paper ServiceBookedToGoodsAccount() => new(
        "ค่าติดตั้งที่ถูกลงผัง 51xxx (ผังไม่ตรงเนื้องาน)",
        DocumentType.PurchaseInvoice, null, 8_000m,
        new[] { new Line("ค่าจ้างติดตั้งเครื่องจักร", 8_000m, GoodsAccount: true) },
        PaperShowsWht: null, PaperTaxInvoiceGrade.Unknown, Array.Empty<WhtPaymentRow>());

    private static Paper CertificateInLieuService() => new(
        "ใบรับรองแทนใบเสร็จ — ค่าจ้างช่างรายวัน 3,000",
        DocumentType.CertificateInLieu, null, 3_000m,
        new[] { new Line("ค่าจ้างเหมาซ่อมหลังคา", 3_000m) },
        PaperShowsWht: null, PaperTaxInvoiceGrade.Unknown, Array.Empty<WhtPaymentRow>(),
        PayeeProvenIndividual: true);

    private static Paper SmallCoffeeRun() => new(
        "ใบเสร็จค่ากาแฟ 250 บาท (ต่ำกว่าเกณฑ์สะสม)",
        DocumentType.Expense, null, 250m,
        new[] { new Line("กาแฟรับรองลูกค้า", 250m) },
        PaperShowsWht: null, PaperTaxInvoiceGrade.Unknown, Array.Empty<WhtPaymentRow>());

    private static IEnumerable<Paper> AllPapers() => new[]
    {
        Ikea(), GoodsWithProductMaster(), LegalService(), ServicePurchaseOrder(),
        SalesCreditNote(), DoubleCountedInstalment(), RentOnIncompletePaper(),
        PaperDeclaresWht(), GoodsByAccountOnly(), ServiceBookedToGoodsAccount(),
        CertificateInLieuService(), SmallCoffeeRun(),
    };

    // ══════════════════════════════════════════════════════════════════════
    //  ครึ่งแรก — ใบที่ "พัง" ต้องกลับมาถูก
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ใบซื้อของที่ใบกำกับครบ_ไม่มีบรรทัดหัก_ด่านเก่าเตือน_ด่านใหม่เงียบ()
    {
        var p = Ikea();
        Assert.True(OldGateWarns(p));                    // บั๊กที่ผู้ใช้รายงาน
        Assert.False(NewGateWarns(p, withScopeFix: true));
        // และความเงียบต้องมาจาก "กระดาษพูด" ไม่ใช่จากยอดไม่ถึงเกณฑ์
        Assert.Equal(WhtApplicability.NotApplicable,
            WhtApplicabilityEvidence.Judge(p.Lines.Select(ToFact), p.PaperShowsWht, p.Grade).Level);
    }

    [Fact]
    public void ใบสั่งซื้อค่าบริการ_ไม่เข้าด่านอีกต่อไป()
    {
        var p = ServicePurchaseOrder();
        Assert.False(OldGateWarns(p));                   // ด่านเก่าไม่ครอบ PO อยู่แล้ว
        Assert.True(NewGateWarns(p, withScopeFix: false));   // แต่ด่านใหม่เคยครอบ (D1-B2)
        Assert.False(NewGateWarns(p, withScopeFix: true));   // ตอนนี้ไม่ครอบแล้ว
    }

    [Fact]
    public void ใบลดหนี้ฝั่งขาย_ไม่เข้าด่านหักณที่จ่าย()
    {
        var p = SalesCreditNote();
        Assert.False(OldGateWarns(p));
        Assert.True(NewGateWarns(p, withScopeFix: false));   // เคยเข้า เพราะไม่ส่งบทบาท
        Assert.False(NewGateWarns(p, withScopeFix: true));
    }

    [Fact]
    public void ยอดสะสมต้องไม่นับใบกำกับกับใบสำคัญจ่ายของการซื้อเดียวกันสองรอบ()
    {
        var p = DoubleCountedInstalment();
        Assert.True(OldGateWarns(p));                    // 600+600+300 = 1,500 ≥ 1,000
        Assert.False(NewGateWarns(p, withScopeFix: true)); // 600+300 = 900 < 1,000
        Assert.Equal(600m, WhtCumulativeScope.SumDistinct(p.YearRows));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ครึ่งหลัง — ใบที่ถูกอยู่แล้ว ห้ามถูกแตะ
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ใบค่าบริการยังเตือนเหมือนเดิม()
    {
        var p = LegalService();
        Assert.True(OldGateWarns(p));
        Assert.True(NewGateWarns(p, withScopeFix: true));
    }

    [Fact]
    public void ใบที่ผู้ขายพิมพ์บรรทัดหักมาเอง_ยังเตือนเหมือนเดิม()
    {
        var p = PaperDeclaresWht();
        Assert.True(OldGateWarns(p));
        Assert.True(NewGateWarns(p, withScopeFix: true));
    }

    [Fact]
    public void ใบค่าเช่าบนกระดาษที่ไม่ครบ_ยังเตือนเหมือนเดิม()
    {
        var p = RentOnIncompletePaper();
        Assert.True(OldGateWarns(p));
        Assert.True(NewGateWarns(p, withScopeFix: true));
    }

    [Fact]
    public void ใบซื้อสินค้าที่ผูกรหัสสินค้า_ยังเงียบเหมือนเดิม()
    {
        var p = GoodsWithProductMaster();
        Assert.False(OldGateWarns(p));
        Assert.False(NewGateWarns(p, withScopeFix: true));
    }

    [Fact]
    public void ใบซื้อของที่ลงผังสินค้าแต่ไม่ผูกรหัส_ยังเงียบเหมือนเดิม()
    {
        var p = GoodsByAccountOnly();
        Assert.False(OldGateWarns(p));       // เงียบเพราะ "ผังเป็นสินค้า"
        Assert.False(NewGateWarns(p, withScopeFix: true));  // เงียบเพราะ "ไม่มีเหตุให้สงสัย"
    }

    [Fact]
    public void ใบเล็กใต้เกณฑ์สะสม_ยังเงียบเหมือนเดิม()
    {
        var p = SmallCoffeeRun();
        Assert.False(OldGateWarns(p));
        Assert.False(NewGateWarns(p, withScopeFix: true));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ใบที่คำตอบ "เปลี่ยนจากเงียบเป็นเตือน" — ต้องอธิบายได้ทุกใบ
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ค่าจ้างที่ถูกลงผังต้นทุนสินค้า_เคยเงียบ_ตอนนี้เตือน()
    {
        // ด่านเก่าเชื่อ "ผังบัญชี" ว่าเป็นหลักฐานว่าเป็นของ ⇒ ค่าจ้างติดตั้งที่ผู้ใช้
        // ลงผิดผังเลยเงียบสนิท (ช่องโหว่ §54 ที่ไม่มีใครเห็น) · ด่านใหม่อ่าน
        // "คำบ่งชี้บริการ" ⇒ เตือนให้คนตรวจ — ทิศที่ถูกกว่า
        var p = ServiceBookedToGoodsAccount();
        Assert.False(OldGateWarns(p));
        Assert.True(NewGateWarns(p, withScopeFix: true));
    }

    [Fact]
    public void ใบรับรองแทนใบเสร็จค่าจ้าง_เคยไม่อยู่ในขอบเขตด่านเก่า_ตอนนี้เตือน()
    {
        // ด่านเก่าครอบแค่ PV/Expense/PI ⇒ ใบรับรองแทนใบเสร็จ (การจ่ายเงินสดจริง
        // ให้บุคคลธรรมดา) หลุดทั้งหมด ทั้งที่เป็นกองที่เสี่ยง §54 ที่สุด
        var p = CertificateInLieuService();
        Assert.False(OldGateWarns(p));
        Assert.True(NewGateWarns(p, withScopeFix: true));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ตารางสรุป — ล็อกผลทั้งชุดไว้ ใบไหนขยับในอนาคตต้องมาแก้ที่นี่พร้อมเหตุผล
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ตารางผลก่อนหลังของทั้งชุด_ต้องคงที่()
    {
        var expected = new Dictionary<string, (bool Old, bool NewBefore, bool NewAfter)>
        {
            [Ikea().Name] = (true, false, false),
            [GoodsWithProductMaster().Name] = (false, false, false),
            [LegalService().Name] = (true, true, true),
            [ServicePurchaseOrder().Name] = (false, true, false),
            [SalesCreditNote().Name] = (false, true, false),
            [DoubleCountedInstalment().Name] = (true, false, false),
            [RentOnIncompletePaper().Name] = (true, true, true),
            [PaperDeclaresWht().Name] = (true, true, true),
            [GoodsByAccountOnly().Name] = (false, false, false),
            [ServiceBookedToGoodsAccount().Name] = (false, true, true),
            [CertificateInLieuService().Name] = (false, true, true),
            [SmallCoffeeRun().Name] = (false, false, false),
        };

        foreach (var p in AllPapers())
        {
            var want = expected[p.Name];
            Assert.Equal(want.Old, OldGateWarns(p));
            Assert.Equal(want.NewBefore, NewGateWarns(p, withScopeFix: false));
            Assert.Equal(want.NewAfter, NewGateWarns(p, withScopeFix: true));
        }
    }
}

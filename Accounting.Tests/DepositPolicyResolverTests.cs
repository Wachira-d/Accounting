using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// วิธีบันทึกเงินมัดจำ (รอบ 193 · คำตัดสินเจ้าของ #34 + คำชี้แจง "ขึ้นอยู่กับประเภทธุรกิจ · ต้องตั้งค่าได้ทั้งหมด")
/// — ล็อกสองทิศ: (1) บริการ = VAT ทันที (§78/1) และคำเตือนเมื่อเลือกอย่างอื่น (2) สินค้า/ไม่ทราบ/ข้อมูลเดิม
/// = พฤติกรรมเดิม (VAT ทันที) ไม่เปลี่ยนเงียบ ๆ · ตัวเลขใบมัดจำ 1,000 รวม VAT ตามโหมด
/// </summary>
public class DepositPolicyResolverTests
{
    // ── ลำดับชั้น: ช่องทาง → บริษัท → ประเภทธุรกิจ ──

    [Fact]
    public void บริการ_ยังไม่มีใครตั้ง_ได้VATทันที_ไม่เตือน()
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, null);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.Equal(DepositVatTreatmentSource.BusinessTypeDefault, d.Source);
        Assert.False(d.NeedsOwnerChoice);
        Assert.Null(d.Warning);
    }

    [Fact]
    public void สินค้า_ยังไม่มีใครตั้ง_คงพฤติกรรมเดิม_VATทันที_ไม่เตือน()
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Goods, null);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.False(d.NeedsOwnerChoice);
        Assert.Null(d.Warning);
    }

    [Fact]
    public void ไม่ทราบประเภทธุรกิจ_คงพฤติกรรมเดิมแต่ขอให้เจ้าของเลือก()
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Unknown, null);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.True(d.NeedsOwnerChoice);
        // ตั้งเองแล้ว = ไม่ต้องถามอีก
        Assert.False(DepositPolicyResolver.Resolve(DepositSupplyNature.Unknown, DepositVatTreatment.VatImmediate).NeedsOwnerChoice);
    }

    [Theory]
    [InlineData(DepositVatTreatment.FullDeposit)]
    [InlineData(DepositVatTreatment.VatPendingUndue)]
    public void บริการเลือกโหมดที่ไม่ใช่VATทันที_ได้คำเตือน78_1(DepositVatTreatment t)
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, t);
        Assert.Equal(t, d.Treatment);
        Assert.Equal(DepositVatTreatmentSource.CompanySetting, d.Source);
        Assert.Equal(DepositPolicyResolver.ServiceNonImmediateRuleCode, d.WarningRuleCode);
        Assert.Contains("78/1", d.Warning);
    }

    [Fact]
    public void สินค้าเลือกภาษีรอเรียกเก็บ_ได้แค่แจ้งให้ทราบ_ไม่ใช่คำเตือน78_1()
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Goods, DepositVatTreatment.VatPendingUndue);
        Assert.Equal(DepositPolicyResolver.GoodsNonImmediateRuleCode, d.WarningRuleCode);
        Assert.DoesNotContain("78/1", d.Warning);
    }

    [Fact]
    public void ที่พักตั้งทับ_ชนะค่าบริษัท_ทั้งสองทิศ()
    {
        var defer = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, DepositVatTreatment.VatImmediate, DepositVatTreatment.VatPendingUndue);
        Assert.Equal(DepositVatTreatment.VatPendingUndue, defer.Treatment);
        Assert.Equal(DepositVatTreatmentSource.ChannelOverride, defer.Source);
        Assert.NotNull(defer.Warning);

        var immediate = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, DepositVatTreatment.FullDeposit, DepositVatTreatment.VatImmediate);
        Assert.Equal(DepositVatTreatment.VatImmediate, immediate.Treatment);
        Assert.Null(immediate.Warning);
    }

    [Fact]
    public void ค่าที่ไม่มีในระบบ_ไม่ถูกใช้_ตกไปชั้นถัดไป()
    {
        var d = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, (DepositVatTreatment)0, (DepositVatTreatment)99);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.Equal(DepositVatTreatmentSource.BusinessTypeDefault, d.Source);
        Assert.False(DepositPolicyResolver.IsDefined((DepositVatTreatment)0));
        Assert.False(DepositPolicyResolver.IsDefined(null));
    }

    [Theory]
    [InlineData(IndustryType.Hotel, DepositSupplyNature.Service)]
    [InlineData(IndustryType.Service, DepositSupplyNature.Service)]
    [InlineData(IndustryType.Construction, DepositSupplyNature.Service)]
    [InlineData(IndustryType.Retail, DepositSupplyNature.Goods)]
    [InlineData(IndustryType.Trading, DepositSupplyNature.Goods)]
    [InlineData(IndustryType.General, DepositSupplyNature.Unknown)]
    [InlineData(IndustryType.Restaurant, DepositSupplyNature.Unknown)]
    [InlineData(IndustryType.Other, DepositSupplyNature.Unknown)]
    public void ประเภทธุรกิจ_แมปลักษณะสิ่งที่ขาย(IndustryType i, DepositSupplyNature expected)
        => Assert.Equal(expected, DepositPolicyResolver.NatureOf(i));

    // ── รูปของใบมัดจำ + ยอด JE ต่อโหมด (1,000 รวม VAT 7%) ──

    [Fact]
    public void มัดจำเต็มยอด_1000_ลงหนี้สินเต็ม_ไม่มีVAT()
    {
        var p = DepositPolicyResolver.PreviewReceipt(DepositVatTreatment.FullDeposit, 1000m, 7m);
        Assert.Equal(1000m, p.Liability);
        Assert.Equal(0m, p.OutputVatDue);
        Assert.Equal(0m, p.OutputVatUndue);
        Assert.Equal(new DepositDocumentShape(0m, true), DepositPolicyResolver.ShapeFor(DepositVatTreatment.FullDeposit, 7m));
    }

    [Fact]
    public void ภาษีรอเรียกเก็บ_1000_ฐาน934_58_VATพัก65_42()
    {
        var p = DepositPolicyResolver.PreviewReceipt(DepositVatTreatment.VatPendingUndue, 1000m, 7m);
        Assert.Equal(934.58m, p.Liability);
        Assert.Equal(0m, p.OutputVatDue);
        Assert.Equal(65.42m, p.OutputVatUndue);
    }

    [Fact]
    public void VATทันที_1000_ฐาน934_58_ภาษีขาย65_42()
    {
        var p = DepositPolicyResolver.PreviewReceipt(DepositVatTreatment.VatImmediate, 1000m, 7m);
        Assert.Equal(934.58m, p.Liability);
        Assert.Equal(65.42m, p.OutputVatDue);
        Assert.Equal(0m, p.OutputVatUndue);
        Assert.Equal(p.Gross, p.Liability + p.OutputVatDue);
    }

    [Theory]
    [InlineData(DepositVatTreatment.FullDeposit)]
    [InlineData(DepositVatTreatment.VatPendingUndue)]
    [InlineData(DepositVatTreatment.VatImmediate)]
    public void บริษัทไม่คิดVAT_ทุกโหมดไม่มีVAT_เหมือนเดิม(DepositVatTreatment t)
    {
        Assert.Equal(new DepositDocumentShape(0m, false), DepositPolicyResolver.ShapeFor(t, 0m));
        Assert.Equal(1000m, DepositPolicyResolver.PreviewReceipt(t, 1000m, 0m).Liability);
    }

    // ── อ่านโหมดย้อนจากใบที่ออกแล้ว (ข้อมูลเดิมไม่ถูกเขียนย้อน) ──

    [Fact]
    public void ใบมัดจำเดิม_อ่านโหมดย้อนได้จากช่องที่ตรึงไว้()
    {
        Assert.Equal(DepositVatTreatment.VatImmediate, DepositPolicyResolver.OfDocument(true, 130.84m, false));
        Assert.Equal(DepositVatTreatment.VatPendingUndue, DepositPolicyResolver.OfDocument(true, 130.84m, true));
        Assert.Equal(DepositVatTreatment.FullDeposit, DepositPolicyResolver.OfDocument(true, 0m, true));
        Assert.Null(DepositPolicyResolver.OfDocument(false, 130.84m, false));
    }

    [Fact]
    public void คำอธิบายยอดJEบนใบมัดจำ_ตรงกับโหมด()
    {
        Assert.Contains("ภาษีขาย 65.42 (21911", DepositPolicyResolver.DescribePosting(DepositVatTreatment.VatImmediate, 1000m, 7m));
        Assert.Contains("ภาษีขายรอเรียกเก็บ 65.42 (21913)", DepositPolicyResolver.DescribePosting(DepositVatTreatment.VatPendingUndue, 1000m, 7m));
        Assert.DoesNotContain("ภาษีขาย", DepositPolicyResolver.DescribePosting(DepositVatTreatment.FullDeposit, 1000m, 7m));
    }

    // ── integration (TakeTime): ธงเมื่อขัด · ไม่ธงเมื่อสอดคล้อง ──

    [Fact]
    public void คู่ค้าพักVAT_แต่บริษัทบริการตั้งVATทันที_ติดธง78_1()
    {
        var company = DepositPolicyResolver.Resolve(DepositSupplyNature.Service, null);
        var note = DepositPolicyResolver.IntegrationMismatchNote(payloadDeferred: true, company);
        Assert.NotNull(note);
        Assert.Contains(DepositPolicyResolver.ServiceNonImmediateRuleCode, note);
        Assert.Contains("ไม่แก้ยอด", note);
    }

    [Fact]
    public void คู่ค้าสอดคล้องกับการตั้งค่า_ไม่ติดธง()
    {
        Assert.Null(DepositPolicyResolver.IntegrationMismatchNote(false, DepositPolicyResolver.Resolve(DepositSupplyNature.Service, null)));
        // เต็มยอด/รอเรียกเก็บ = "ยังไม่รับรู้" ทั้งคู่ — payload บอกได้แค่ธงเดียว ห้ามธงเท็จ
        Assert.Null(DepositPolicyResolver.IntegrationMismatchNote(true, DepositPolicyResolver.Resolve(DepositSupplyNature.Service, DepositVatTreatment.FullDeposit)));
        Assert.NotNull(DepositPolicyResolver.IntegrationMismatchNote(false, DepositPolicyResolver.Resolve(DepositSupplyNature.Goods, DepositVatTreatment.VatPendingUndue)));
    }

    [Fact]
    public void ต่อธงซ้ำ_ไม่ซ้ำข้อความ()
    {
        var once = DepositPolicyResolver.AppendNoteOnce("เดิม", "[X] ธง");
        Assert.Equal("เดิม · [X] ธง", once);
        Assert.Equal(once, DepositPolicyResolver.AppendNoteOnce(once, "[X] ธง"));
        Assert.Equal("เดิม", DepositPolicyResolver.AppendNoteOnce("เดิม", null));
    }

    // ── ป้ายบนใบสุดท้าย (สอง renderer ใช้ตัวนี้ตัวเดียว) ──

    [Fact]
    public void แถวหักมูลค่ามัดจำ_ตัดสินจากช่องฐานมัดจำ_ไม่ใช่ส่วนลดการค้า()
    {
        // R3-1: ช่องของตัวเอง — มีฐาน + เลขใบมัดจำ ⇒ แถว "หักมูลค่ามัดจำตามใบกำกับ"
        Assert.True(DepositPolicyResolver.TaxedDepositDeducted(1869.16m, "TIV-2026-0001"));
        // ส่วนลดการค้าอยู่อีกช่อง (BillDiscountAmount) — ช่องฐานมัดจำเป็น 0 ⇒ ไม่ใช่มัดจำแม้มีเลขอ้างอิง
        Assert.False(DepositPolicyResolver.TaxedDepositDeducted(0m, "TIV-2026-0001"));
        Assert.False(DepositPolicyResolver.TaxedDepositDeducted(1869.16m, null));
        Assert.False(DepositPolicyResolver.TaxedDepositDeducted(1869.16m, " "));
    }

    [Fact]
    public void ส่วนหักท้ายบิลรวม_คือส่วนลดการค้าบวกฐานมัดจำ()
    {
        Assert.Equal(1969.16m, DepositPolicyResolver.BillDeductionTotal(100m, 1869.16m));
        Assert.Equal(100m, DepositPolicyResolver.BillDeductionTotal(100m, 0m));
    }

    [Fact]
    public void แยกยอดที่เฉลี่ยแล้ว_กลับเป็นส่วนลดการค้ากับฐานมัดจำ()
    {
        Assert.Equal((100m, 1869.16m, true), DepositPolicyResolver.SplitBillDeduction(1969.16m, 100m, 1869.16m));
        // ไม่มีมัดจำ = พฤติกรรมเดิมทุกประการ (ส่วนลด = ยอดที่เฉลี่ยได้จริง แม้ถูกตัดที่ยอดขาย)
        Assert.Equal((80m, 0m, true), DepositPolicyResolver.SplitBillDeduction(80m, 100m, 0m));
        // ส่วนลด + มัดจำเกินยอดขาย (ตัวเฉลี่ยตัดทิ้ง) ⇒ ไม่ผ่าน — ผู้เรียกต้องล้มดัง
        Assert.False(DepositPolicyResolver.SplitBillDeduction(5000m, 100m, 5000m).Ok);
    }

    [Fact]
    public void ด่านฐานมัดจำที่หัก_ต้องมีเลขใบมัดจำ_ห้ามหักสองชั้น_ห้ามปนส่วนลดเปอร์เซ็นต์()
    {
        Assert.Null(DepositPolicyResolver.TaxedDepositDeductionProblem(DocumentType.TaxInvoice, null, 0m, null, 2000m, true, 10m));       // ไม่มีฐานมัดจำ = ไม่ตรวจ
        Assert.Null(DepositPolicyResolver.TaxedDepositDeductionProblem(DocumentType.TaxInvoice, null, 1869.16m, "TIV-1", 0m, false, 0m));
        Assert.NotNull(DepositPolicyResolver.TaxedDepositDeductionProblem(DocumentType.TaxInvoice, null, 1869.16m, null, 0m, false, 0m));
        Assert.NotNull(DepositPolicyResolver.TaxedDepositDeductionProblem(DocumentType.TaxInvoice, null, 1869.16m, "TIV-1", 2000m, false, 0m));
        Assert.NotNull(DepositPolicyResolver.TaxedDepositDeductionProblem(DocumentType.TaxInvoice, null, 1869.16m, "TIV-1", 0m, true, 0m));
        Assert.Equal(DepositPolicyResolver.PercentWithTaxedDepositMessage,
            DepositPolicyResolver.TaxedDepositDeductionProblem(DocumentType.TaxInvoice, null, 1869.16m, "TIV-1", 0m, false, 5m));
        Assert.NotNull(DepositPolicyResolver.TaxedDepositDeductionProblem(DocumentType.TaxInvoice, null, -1m, "TIV-1", 0m, false, 0m));
    }

    [Theory]
    [InlineData(DocumentType.TaxInvoice, null, true)]
    [InlineData(DocumentType.Receipt, null, true)]
    [InlineData(DocumentType.ReceiptVoucher, null, true)]
    [InlineData(DocumentType.Invoice, null, true)]
    [InlineData(DocumentType.CreditNote, null, true)]     // ใบลดหนี้ที่แปลงจากใบกำกับขาย (สืบทอดฐานมัดจำ)
    [InlineData(DocumentType.CreditNote, false, true)]
    [InlineData(DocumentType.CreditNote, true, false)]    // ใบลดหนี้ฝั่งซื้อ
    [InlineData(DocumentType.PurchaseInvoice, null, false)]
    [InlineData(DocumentType.Expense, null, false)]
    [InlineData(DocumentType.PaymentVoucher, null, false)]
    public void หักมูลค่ามัดจำ_เฉพาะฝั่งขาย(DocumentType type, bool? purchaseSide, bool allowed)
    {
        Assert.Equal(allowed, DepositPolicyResolver.TaxedDepositDeductionAllowed(type, purchaseSide));
        var problem = DepositPolicyResolver.TaxedDepositDeductionProblem(type, purchaseSide, 1869.16m, "TIV-1", 0m, false, 0m);
        Assert.Equal(allowed ? null : DepositPolicyResolver.DeductionOnPurchaseSideMessage, problem);
        // ทิศตรงข้าม: ไม่มีฐานมัดจำ = ไม่ตรวจชนิด (ใบซื้อทั่วไปไม่ถูกแตะ)
        Assert.Null(DepositPolicyResolver.TaxedDepositDeductionProblem(type, purchaseSide, 0m, null, 0m, false, 0m));
    }

    [Fact]
    public void ข้อความด่านใบขับJEเก่า_บอกทางเดียว_ไม่สั่งรับรู้มัดจำเองที่หน้าเงินมัดจำ()
    {
        // R3-5: เดิมต่อท้ายข้อความปุ่ม (ข้อ ① "แล้วรับรู้มัดจำเป็นรายได้ที่หน้าเงินมัดจำ") ⇒ ทำตามทั้งคู่ = รับรู้ซ้ำ
        var msg = DepositPolicyResolver.DrivesGuardMessage("TIV-DEP-1", "RE-0009");
        Assert.Contains("TIV-DEP-1", msg);
        Assert.Contains("RE-0009", msg);
        Assert.Contains("รับรู้มัดจำเป็นรายได้ให้เองตอนอนุมัติ", msg);
        Assert.DoesNotContain("แล้วรับรู้มัดจำเป็นรายได้ที่หน้า", msg);
        Assert.DoesNotContain("①", msg);
        // ทิศตรงข้าม: ข้อความของปุ่ม/Integration (ไม่รับรู้อัตโนมัติ) ยังบอกให้รับรู้ที่หน้าเงินมัดจำตามเดิม
        Assert.Contains("แล้วรับรู้มัดจำเป็นรายได้ที่หน้า", DepositPolicyResolver.GrossApplyBlockedMessage("TIV-DEP-1"));
    }

    [Fact]
    public void ตัวเลือกเรียงตามเจ้าของ_และคำเตือนบริการมีเฉพาะโหมดที่ไม่ใช่VATทันที()
    {
        var o = DepositPolicyResolver.Options;
        Assert.Equal(new[] { "FullDeposit", "VatPendingUndue", "VatImmediate" }, o.Select(x => x.Value).ToArray());
        Assert.NotNull(o[0].ServiceWarning);
        Assert.NotNull(o[1].ServiceWarning);
        Assert.Null(o[2].ServiceWarning);
        Assert.All(o, x => Assert.False(string.IsNullOrWhiteSpace(x.LegalReference)));
    }

    // ═══ P0-3 / P0-1 (ด่านใน DocumentService ใช้ตัวตัดสินนี้ตัวเดียว) ═══

    [Fact]
    public void มัดจำออกใบกำกับแล้ว_ห้ามหักเต็มจำนวน_มัดจำพักVATหรือเต็มยอดหักได้()
    {
        Assert.True(DepositPolicyResolver.GrossApplyBlocked(130.84m, depositVatPending: false));   // VAT ทันที
        Assert.False(DepositPolicyResolver.GrossApplyBlocked(130.84m, depositVatPending: true));   // รอเรียกเก็บ
        Assert.False(DepositPolicyResolver.GrossApplyBlocked(0m, depositVatPending: true));        // เต็มยอด
        Assert.False(DepositPolicyResolver.GrossApplyBlocked(0m, depositVatPending: false));       // บริษัทไม่จด VAT
        var msg = DepositPolicyResolver.GrossApplyBlockedMessage("TIV-0001");
        Assert.Contains("TIV-0001", msg);
        Assert.Contains("①", msg);
        Assert.Contains("②", msg);
    }

    [Fact]
    public void หักมัดจำแบบขับJE_ใช้ได้เฉพาะชนิดที่AutoPostอ่านธง()
    {
        Assert.True(DepositPolicyResolver.DrivesJournalSupported(DocumentType.Receipt, false));
        Assert.True(DepositPolicyResolver.DrivesJournalSupported(DocumentType.ReceiptVoucher, false));
        Assert.True(DepositPolicyResolver.DrivesJournalSupported(DocumentType.TaxInvoice, issuedAsCashReceipt: true));
        // ใบเครดิต — เดิมรับธงแล้วไม่มีผล (เช็คเอาต์ที่พักเก็บเงินเต็ม 7,450 ซ้ำกับมัดจำ)
        Assert.False(DepositPolicyResolver.DrivesJournalSupported(DocumentType.TaxInvoice, issuedAsCashReceipt: false));
        Assert.False(DepositPolicyResolver.DrivesJournalSupported(DocumentType.Invoice, false));
    }

    // ═══ ฝ่ายค้านรอบสอง N1 — ทุกเส้นหักมัดจำที่ออกใบกำกับแล้วแบบ "หักมูลค่าก่อน VAT" (ไม่มีใบกำกับซ้ำ) ═══

    [Fact]
    public void แปลงยอดหักมัดจำรวมVATเป็นฐาน_เต็มใบ_และบางส่วน()
    {
        // มัดจำ 2,000 (ฐาน 1,869.16) ทั้งใบ → ฐาน 1,869.16 · ครึ่งใบ 1,000 → 934.58
        Assert.Equal(1869.16m, DepositPolicyResolver.TaxedDepositBase(2000m, 1869.16m, 2000m, 1869.16m));
        Assert.Equal(934.58m, DepositPolicyResolver.TaxedDepositBase(1000m, 1869.16m, 2000m, 1869.16m));
    }

    [Fact]
    public void แปลงฐาน_ไม่เกินฐานคงเหลือ_และยอดศูนย์ไม่แปลง()
    {
        Assert.Equal(500m, DepositPolicyResolver.TaxedDepositBase(2000m, 1869.16m, 2000m, 500m));
        Assert.Equal(0m, DepositPolicyResolver.TaxedDepositBase(0m, 1869.16m, 2000m, 1869.16m));
        Assert.Equal(0m, DepositPolicyResolver.TaxedDepositBase(2000m, 1869.16m, 2000m, 0m));
    }

    [Fact]
    public void กระจายฐานที่ต้องรับรู้_ใบเก่าสุดก่อน_ไม่เกินคงเหลือของแต่ละใบ()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var (lines, shortfall) = DepositPolicyResolver.AllocateBaseDeduction(2500m, new[] { (a, 1869.16m), (b, 1869.16m) });
        Assert.Equal(0m, shortfall);
        Assert.Equal(2, lines.Count);
        Assert.Equal((a, 1869.16m), lines[0]);
        Assert.Equal((b, 630.84m), lines[1]);
    }

    [Fact]
    public void กระจายฐาน_มัดจำไม่พอ_บอกส่วนขาดให้ล้มดัง_ทิศตรงข้ามพอดีไม่ขาด()
    {
        var a = Guid.NewGuid();
        Assert.Equal(130.84m, DepositPolicyResolver.AllocateBaseDeduction(2000m, new[] { (a, 1869.16m) }).Shortfall);
        Assert.Equal(0m, DepositPolicyResolver.AllocateBaseDeduction(1869.16m, new[] { (a, 1869.16m) }).Shortfall);
        Assert.Empty(DepositPolicyResolver.AllocateBaseDeduction(0m, new[] { (a, 1869.16m) }).Lines);
    }

    // ═══ ฝ่ายค้านรอบสอง N4 — แยกยอดคืนมัดจำ (ตัวเดียวของ RefundDepositAsync + ที่พัก) ═══

    [Fact]
    public void คืนสองงวด_4727_07_ค่าปรับ1500_51_คืน72_02แล้วงวดสุดท้าย3154_54ผ่าน_VATรวมเท่าคืนครั้งเดียว()
    {
        const decimal sub = 4417.82m, vat = 309.25m, total = 4727.07m, realized = 1402.35m;
        var first = DepositReversalMath.RefundSplit(72.02m, sub, vat, total, realized, 0m);
        Assert.True(first.Ok);
        var last = DepositReversalMath.RefundSplit(3154.54m, sub, vat, total, realized, 72.02m);
        Assert.True(last.Ok);                                   // เดิม: ฐาน 2,948.17 > คงเหลือ 2,948.16 ⇒ ปฏิเสธ ค้าง 0.01
        Assert.Equal(2948.16m, last.Base);
        var once = DepositReversalMath.RefundSplit(3226.56m, sub, vat, total, realized, 0m);
        Assert.Equal(once.Vat, first.Vat + last.Vat);           // 211.09 เท่าคืนครั้งเดียว
        Assert.Equal(once.Base, first.Base + last.Base);
    }

    [Fact]
    public void คืนครั้งเดียวจากศูนย์_สูตรเดิม_และคืนเกินยอดใบถูกปฏิเสธ()
    {
        var ok = DepositReversalMath.RefundSplit(1000m, 934.58m, 65.42m, 1000m, 0m, 0m);
        Assert.True(ok.Ok);
        Assert.Equal((934.58m, 65.42m), (ok.Base, ok.Vat));
        Assert.False(DepositReversalMath.RefundSplit(1000.01m, 934.58m, 65.42m, 1000m, 0m, 0m).Ok);
        Assert.False(DepositReversalMath.RefundSplit(0m, 934.58m, 65.42m, 1000m, 0m, 0m).Ok);
    }
}

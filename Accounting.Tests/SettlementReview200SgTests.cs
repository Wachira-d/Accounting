using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม SG — แก้ผลฝ่ายค้านรอบสอง ด้านเงิน/ภาษี settlement + ภ.พ.36 (<c>erp-review/2026-09-29/review200-round2-money.md</c> R2M-2..R2M-13) ·
/// คำตัดสิน DECISIONS ข้อ 26 · 27 · 40 · 41 · ทุกเรื่องสองทิศ (F2 ข้อ 8): ตัวเลขตัวอย่างในรายงานกลับมาถูก + เคสที่ถูกอยู่แล้วไม่ถูกแตะ ·
/// จุดเรียกใน service ล็อกด้วย <c>tools/required_call_site_check.py</c> (บล็อก "รอบ 200 ทีม SG")
/// </summary>
public class SettlementReview200SgTests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Bank = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DocA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    private static SettlementChannel Channel(SettlementFeeVatMode vat, SettlementFeeWhtMode wht)
        => new()
        {
            CompanyId = Co, Kind = SettlementChannelKind.Gateway, DisplayName = "ผู้ให้บริการ", ClearingAccountId = Clearing,
            FeeVatMode = vat, FeeWhtMode = wht, CounterpartyContactId = Guid.NewGuid(),
        };

    private static SettlementBatch Batch(decimal net)
        => new() { CompanyId = Co, PayoutRef = "PO-SG", PayoutDate = Day, NetPayout = net, BankAccountId = Bank };

    private static int _seq;
    private static SettlementLine L(SettlementLineType t, decimal amount, Guid? doc = null, decimal? vat = null)
        => new()
        {
            CompanyId = Co, Seq = ++_seq, LineType = t, Amount = amount, VatAmount = vat, MatchedDocumentId = doc, TxnDate = Day,
            MatchStatus = doc is not null ? SettlementMatchStatus.Matched : SettlementMatchStatus.AutoSummary,
        };

    // ═════════════════ R2M-2 (ข้อ 26): บริษัทไม่จด VAT — ฐานหัก ณ ที่จ่ายของสองเส้นต้องเท่ากัน ═════════════════

    /// <summary>WHT ออกภาษีแทนของเส้นเดิม (config) กับเส้นรอบโอน (ช่องทาง) ของค่าธรรมเนียม 107.00 — บริษัทไม่จด VAT</summary>
    private static (decimal Legacy, decimal Batch) WhtBothPaths(GatewayFeeVatMode gatewayVat, SettlementFeeVatMode channelVat)
    {
        var legacyBase = GatewaySettlementMath.Contribution(new SettlementIntentInput(Guid.NewGuid(), 1000m, 107m, 107m), gatewayVat).FeeBeforeVat;
        var legacy = GatewaySettlementMath.WhtOnFee(legacyBase);
        var batch = SettlementFeeTax.Compute(107m, null, channelVat, true, false, SettlementFeeWhtMode.SelfWithholdPayerBorne,
            SettlementLineTypeRules.For(SettlementLineType.PaymentFee).WhtIncomeCode, Day).WhtAmount;
        return (legacy, batch);
    }

    [Fact]
    public void R2M2_ไม่จดVAT_หัก3_configไม่แยกVAT_ช่องทางVAT7_ค่าธรรมเนียม107_3_31กับ3_09_ต้องไม่ตรง()
    {
        // ตัวเลขในรายงาน: เส้นเดิมหักบนยอดเต็ม 107 × 3/97 = 3.31 · เส้นรอบโอนหักบนก่อน VAT 100 × 3/97 = 3.09
        var (legacy, batch) = WhtBothPaths(GatewayFeeVatMode.None, SettlementFeeVatMode.ThaiVat7);
        Assert.Equal(3.31m, legacy);
        Assert.Equal(3.09m, batch);
        var why = GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.None, GatewayFeeWhtMode.Withhold3Percent,
            SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.SelfWithholdPayerBorne, false, null);
        Assert.NotNull(why);
        Assert.Contains("ฐานหัก ณ ที่จ่าย", why);
        // กลับทิศ: config "รวมใน" ↔ ช่องทาง "ไม่มี VAT" ⇒ 3.09 กับ 3.31 ต้องไม่ตรงเช่นกัน
        Assert.NotNull(GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.IncludedInFee, GatewayFeeWhtMode.Withhold3Percent,
            SettlementFeeVatMode.None, SettlementFeeWhtMode.SelfWithholdPayerBorne, false, null));
    }

    [Theory]
    [InlineData(GatewayFeeVatMode.None, SettlementFeeVatMode.ThaiVat7)]
    [InlineData(GatewayFeeVatMode.IncludedInFee, SettlementFeeVatMode.ThaiVat7)]
    [InlineData(GatewayFeeVatMode.None, SettlementFeeVatMode.None)]
    [InlineData(GatewayFeeVatMode.IncludedInFee, SettlementFeeVatMode.None)]
    public void R2M2_ไม่จดVAT_หัก3_ด่านผ่านก็ต่อเมื่อWHTสองเส้นเท่ากันจริง(GatewayFeeVatMode gatewayVat, SettlementFeeVatMode channelVat)
    {
        // ล็อก "ข้อ 26 = ภาษีเท่ากัน" ด้วยตัวเลขจริงของสองสูตร ไม่ใช่ตารางคู่โหมดที่เขียนจากความจำ
        var (legacy, batch) = WhtBothPaths(gatewayVat, channelVat);
        var why = GatewayBatchIntentRules.ModeMismatch(gatewayVat, GatewayFeeWhtMode.Withhold3Percent, channelVat,
            SettlementFeeWhtMode.SelfWithholdPayerBorne, false, null);
        Assert.Equal(legacy == batch, why is null);
    }

    [Fact]
    public void R2M2_ทิศตรงข้าม_ไม่จดVAT_ไม่หักณที่จ่าย_คู่VATไทยยังผ่าน_X10ไม่ถูกแตะ()
    {
        Assert.Null(GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.ThaiVat7,
            SettlementFeeWhtMode.None, false, null));
        Assert.Null(GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.IncludedInFee, GatewayFeeWhtMode.None, SettlementFeeVatMode.None,
            SettlementFeeWhtMode.None, false, null));
        // จด VAT: คู่ที่ตรงเดิมยังตรง (หัก 3% + VAT 7%)
        Assert.Null(GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.IncludedInFee, GatewayFeeWhtMode.Withhold3Percent,
            SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.SelfWithholdPayerBorne, true, null));
    }

    // ═════════════════ R2M-5 (ข้อ 26 × 41): ประเภทเงินได้ต่อช่องทางอยู่ในตัวตัดสินเดียวกัน ═════════════════

    [Theory]
    [InlineData("{\"PaymentFee\":\"8ad\"}", true)]   // ค่าโฆษณา 2%
    [InlineData("{\"PaymentFee\":\"8tr\"}", true)]   // ค่าขนส่ง 1%
    [InlineData("{\"PaymentFee\":\"none\"}", true)]  // ไม่หัก
    [InlineData("{\"PaymentFee\":\"8\"}", false)]    // 3% เท่าเส้นเดิม
    [InlineData(null, false)]                        // ค่าตั้งต้นของประเภทบรรทัด = 40(8) 3%
    [InlineData("{\"AdsFee\":\"none\"}", false)]     // ประเภทบรรทัดที่ประกอบจากรายการรับชำระไม่สร้าง ⇒ ไม่เกี่ยว
    public void R2M5_ช่องทางผูกgatewayหัก3_ประเภทเงินได้ของค่าธรรมเนียมรับชำระต้องได้อัตรา3เท่าเส้นเดิม(string? map, bool blocked)
    {
        var why = GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.IncludedInFee, GatewayFeeWhtMode.Withhold3Percent,
            SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.SelfWithholdPayerBorne, true, map);
        Assert.Equal(blocked, why != null);
        if (blocked) Assert.Contains("ประเภทเงินได้", why);
    }

    [Fact]
    public void R2M5_ทิศตรงข้าม_gatewayไม่หัก_ค่าตั้งประเภทเงินได้ไม่มีผลต่อด่าน_และค่าตั้งgatewayตรวจช่องทางด้วยค่าตั้งนี้()
    {
        Assert.Null(GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.None,
            SettlementFeeWhtMode.None, true, "{\"PaymentFee\":\"none\"}"));
        // เปิด "หัก 3%" ที่หน้าตั้งค่า gateway ขณะช่องทางที่ผูกตั้งค่าธรรมเนียมรับชำระเป็น "ไม่หัก" ⇒ ปฏิเสธพร้อมชื่อช่องทาง
        var bound = new[] { ("Omise หลัก", SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.SelfWithholdPayerBorne, (string?)"{\"PaymentFee\":\"none\"}") };
        Assert.Contains("Omise หลัก", GatewayBatchIntentRules.ConfigChangeRefusal(GatewayFeeVatMode.IncludedInFee,
            GatewayFeeWhtMode.Withhold3Percent, true, bound));
        var ok = new[] { ("Omise หลัก", SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.SelfWithholdPayerBorne, (string?)null) };
        Assert.Null(GatewayBatchIntentRules.ConfigChangeRefusal(GatewayFeeVatMode.IncludedInFee, GatewayFeeWhtMode.Withhold3Percent, true, ok));
    }

    // ═════════════════ R2M-3 / R2M-10 (ข้อ 40): ฐาน ภ.พ.36 ห้ามบวกภาษีออกแทนซ้ำเมื่อบรรทัด gross-up แล้ว ═════════════════

    [Fact]
    public void R2M3_เอกสารคีย์grossup529_41_50ทวิออกให้ตลอดไป_ฐานภพ36คือ529_41ไม่ใช่608_82_และไม่เตือนVATขาด()
    {
        // ตัวเลขในรายงาน: บรรทัด 529.41 · WHT บนเอกสาร 79.41 · ภ.พ.36 37.06 · 50 ทวิ เงินได้ 529.41 ภาษี 79.41
        var borne = ForeignServiceVat.BorneTaxOutsideLines(529.41m, 79.41m, 529.41m, 79.41m);
        Assert.Equal(0m, borne);
        Assert.Equal(529.41m, ForeignServiceVat.Pp36Base(529.41m, borne));
        Assert.Equal(0m, ForeignServiceVat.Pp36Shortfall(529.41m, borne, 37.06m));
        // พฤติกรรมเดิม (บวก 50 ทวิ ทั้งก้อน) — ล็อกว่าตัวเลขที่ฝ่ายค้านเห็นคือสูตรนี้จริง: ฐาน 608.82 + "VAT ขาด 5.56"
        Assert.Equal(608.82m, ForeignServiceVat.Pp36Base(529.41m, 79.41m));
        Assert.Equal(5.56m, ForeignServiceVat.Pp36Shortfall(529.41m, 79.41m, 37.06m));
    }

    [Fact]
    public void R2M3_ทิศตรงข้าม_ใบค่าธรรมเนียมรอบโอน450_50ทวิเงินได้529_41_ยังบวกภาษีออกแทน_ฐาน529_41ภาษี37_06()
    {
        var borne = ForeignServiceVat.BorneTaxOutsideLines(450m, 0m, 529.41m, 79.41m);
        Assert.Equal(79.41m, borne);
        Assert.Equal(529.41m, ForeignServiceVat.Pp36Base(450m, borne));
        Assert.Equal(37.06m, ForeignServiceVat.SelfAssessedVatOn(ForeignServiceVat.Pp36Base(450m, borne)));
        // ใบคีย์มือเดิม (ภ.พ.36 31.50 บนฐาน 450) + 50 ทวิ ออกให้ตลอดไปภายหลัง ⇒ ยังเตือนขาด 5.56 (W-3 ไม่ถูกถอด)
        Assert.Equal(5.56m, ForeignServiceVat.Pp36Shortfall(450m, borne, 31.50m));
        // ไม่มีภาษีออกแทน ⇒ 0
        Assert.Equal(0m, ForeignServiceVat.BorneTaxOutsideLines(450m, 0m, 450m, 0m));
    }

    [Fact]
    public void R2M3_50ทวิครอบบางงวด_ตัดสินจากWHTบนเอกสาร()
    {
        // บรรทัดสุทธิ 900 ไม่มี WHT บนเอกสาร (ภาษีจ่ายแยก) · 50 ทวิ งวดแรก 529.41/79.41 ⇒ บรรทัดยังไม่รวม ⇒ บวก
        Assert.Equal(79.41m, ForeignServiceVat.BorneTaxOutsideLines(900m, 0m, 529.41m, 79.41m));
        // บรรทัด gross-up 1,058.82 มี WHT บนเอกสาร 158.82 · 50 ทวิ งวดแรกใบเดียว ⇒ บรรทัดรวมแล้ว ⇒ ไม่บวก
        Assert.Equal(0m, ForeignServiceVat.BorneTaxOutsideLines(1058.82m, 158.82m, 529.41m, 79.41m));
    }

    [Fact]
    public void R2M10_รายงานภพ36นับเฉพาะ50ทวิที่ออกแล้ว_ร่างไม่นับ()
    {
        // รายงานอ่านชุดสถานะจากตัวตั้งเดียว (WhtCertFilingScope.Filed — ล็อกจุดเรียกด้วย required_call_site_check)
        Assert.DoesNotContain(WithholdingTaxCertStatus.Draft, WhtCertFilingScope.Filed);
        Assert.DoesNotContain(WithholdingTaxCertStatus.Voided, WhtCertFilingScope.Filed);
        Assert.Contains(WithholdingTaxCertStatus.Issued, WhtCertFilingScope.Filed);
    }

    // ═════════════════ R2M-4 (X-4 × W-3): ภ.พ.36 ของบรรทัดใบคิดจากฐานรวมเดียวกับ 50 ทวิ ═════════════════

    [Fact]
    public void R2M4_ต่างประเทศออกภาษีแทน_3_65คูณ100ระบุVAT0_ภพ36_30_06ไม่ใช่30_00_และฐานตรงกับ50ทวิ()
    {
        var lines = new List<SettlementLine> { L(SettlementLineType.Sale, 10000m, DocA) };
        for (var i = 0; i < 100; i++) lines.Add(L(SettlementLineType.PaymentFee, -3.65m, vat: 0m));
        var plan = SettlementBatchMath.Plan(Batch(9635m), lines,
            Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        var fee = Assert.Single(plan.FeeDocuments);
        var line = Assert.Single(fee.Lines);
        Assert.Equal(365m, line.WhtBase);
        Assert.Equal(64.41m, line.WhtAmount);           // 365 × 15/85 (ฐานรวม · X-4)
        Assert.Equal(429.41m, line.WhtCertIncome);
        Assert.Equal(30.06m, line.Pp36Payable);          // round(429.41 × 7%) — เดิม Σ round((3.65 + 0.64) × 7%) = 30.00
        Assert.Equal(30.06m, fee.Pp36Payable);
        Assert.Equal(30.06m, fee.InputVat);
        Assert.Equal(365m, fee.Expense);
        Assert.Equal(ForeignServiceVat.SelfAssessedVatOn(ForeignServiceVat.Pp36Base(line.WhtBase, line.WhtBorneExpense)), line.Pp36Payable);
        Assert.Equal(plan.PayoutJournal.Sum(l => l.Debit), plan.PayoutJournal.Sum(l => l.Credit));
    }

    [Fact]
    public void R2M4_ทิศตรงข้าม_ก้อนเดียว_ไม่จดVAT_และหักจากเงินที่จ่าย_ตัวเลขเท่าเดิม()
    {
        // ก้อนเดียว (ไม่ระบุ VAT) — ตัวเลขเดิม 30.06 · สูตรตัวเดียวกับรายก้อน
        var one = SettlementBatchMath.Plan(Batch(9635m), new[] { L(SettlementLineType.Sale, 10000m, DocA), L(SettlementLineType.PaymentFee, -365m) },
            Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        Assert.Equal(30.06m, Assert.Single(one.FeeDocuments).Pp36Payable);
        // ไม่จด VAT หลายส่วน: ภ.พ.36 เป็นต้นทุน ⇒ ค่าใช้จ่าย = 365 + 30.06 · ไม่มีภาษีซื้อ
        var lines = new List<SettlementLine> { L(SettlementLineType.Sale, 10000m, DocA) };
        for (var i = 0; i < 100; i++) lines.Add(L(SettlementLineType.PaymentFee, -3.65m, vat: 0m));
        var nc = Assert.Single(SettlementBatchMath.Plan(Batch(9635m), lines,
            Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdPayerBorne), false).FeeDocuments);
        Assert.Equal(30.06m, nc.Pp36Payable);
        Assert.Equal(0m, nc.InputVat);
        Assert.Equal(395.06m, nc.Expense);
        // หักจากเงินที่จ่าย (W2) ⇒ ไม่มีภาษีออกแทน ⇒ ฐาน 365 ⇒ 25.55 — ก้อนเดียวเท่าเดิม · หลายส่วนเดิมได้ Σ round(3.65 × 7%) = 26.00
        // (อัตรา 7.12% บนฐานที่รายงานแสดง 365) ⇒ ตอนนี้คิดจากฐานรวมเหมือนกัน = 25.55 (ผลข้างเคียงที่ตั้งใจของสูตรตัวเดียว)
        var w2One = Assert.Single(SettlementBatchMath.Plan(Batch(9635m), new[] { L(SettlementLineType.Sale, 10000m, DocA), L(SettlementLineType.PaymentFee, -365m) },
            Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdReimbursed), true).FeeDocuments);
        Assert.Equal(25.55m, w2One.Pp36Payable);
        var w2 = Assert.Single(SettlementBatchMath.Plan(Batch(9635m), lines,
            Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdReimbursed), true).FeeDocuments);
        Assert.Equal(25.55m, w2.Pp36Payable);
        // สูตรตัวเดียว: ชนิดที่ไม่ใช่ ภ.พ.36 ⇒ (0, 0, ค่าบริการ)
        Assert.Equal((0m, 0m, 365m), SettlementFeeTax.Pp36Legs(SettlementFeeVatTreatment.InputVatPending, 365m, 64.41m));
    }

    // ═════════════════ R2M-6 (X-8): ไม่กรอกปลายช่วง ⇒ ขอบบน = เที่ยงคืนต้นวันเงินเข้า ═════════════════

    [Fact]
    public void R2M6_ไม่กรอกปลายช่วง_รายการที่รับเงินตั้งแต่วันเงินเข้าไม่ถูกดึงเข้ารอบนี้()
    {
        // ตัวประกอบใช้ ConfirmedToExclusiveUtc(periodTo ?? วันเงินเข้า − 1) — ล็อกจุดเรียกด้วย required_call_site_check
        var upper = GatewaySettlementMath.ConfirmedToExclusiveUtc(Day.AddDays(-1));
        Assert.Equal(new DateTime(2026, 9, 19, 17, 0, 0, DateTimeKind.Utc), upper);   // 00:00 ไทยของวันที่ 20
        Assert.Equal(GatewaySettlementMath.RefundCutoffUtc(Day), upper);             // ขอบเดียวกับจุดตัดยอดคืน
        PaymentIntent Intent(DateTime confirmed) => new()
        {
            CompanyId = Co, ProviderCode = "omise", Amount = 1000m, Status = PaymentIntentStatus.Succeeded, ConfirmedAt = confirmed,
        };
        var before = Intent(new DateTime(2026, 9, 19, 16, 0, 0, DateTimeKind.Utc));   // 23:00 ไทยวันที่ 19
        var onPayoutDay = Intent(new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc));  // 10:00 ไทยวันเงินเข้า
        var pick = GatewayBatchIntentRules.UnclaimedForBatch(Co, "omise", null, upper).Compile();
        Assert.True(pick(before));
        Assert.False(pick(onPayoutDay));
    }

    [Fact]
    public void R2M6_ทิศตรงข้าม_กรอกปลายช่วง_ใช้ปลายช่วงของผู้ใช้เหมือนเดิม()
    {
        var to = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);
        // ปลายช่วงวันที่ 18 ⇒ ขอบ = 00:00 ไทยของวันที่ 19 (ก่อนขอบตั้งต้นของวันเงินเข้า 1 วัน — ผู้ใช้กรอกเองชนะ)
        Assert.Equal(new DateTime(2026, 9, 18, 17, 0, 0, DateTimeKind.Utc), GatewaySettlementMath.ConfirmedToExclusiveUtc(to));
    }

    // ═════════════════ R2M-7 / R2M-8: ทางไปต่อของช่องทางต่างประเทศที่ผูก gateway ไม่ขัดกันเอง ═════════════════

    [Fact]
    public void R2M7_ช่องทางภพ36ผูกgateway_ด่านโหมดและด่านลงบัญชีบอกทางเดียวกัน_ไม่บอกให้แก้โหมดให้ตรง()
    {
        var why = GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.None, GatewayFeeWhtMode.Withhold3Percent,
            SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdPayerBorne, true, null);
        Assert.NotNull(why);
        Assert.Contains(GatewayBatchIntentRules.ForeignPp36BoundNextStep, why);
        Assert.DoesNotContain("แก้ให้สองที่ตรงกัน", why);
        var issue = GatewayBatchIntentRules.PostingIssue(why, SettlementFeeVatMode.ForeignPp36)!;
        Assert.Equal(GatewayBatchIntentRules.ForeignPp36BoundNextStep, issue.NextStep);
        Assert.Contains("สร้างช่องทางรับเงินใหม่", issue.NextStep);
        Assert.Contains("ภ.พ.36", issue.NextStep);
        // ทิศตรงข้าม: ช่องทางไทยที่โหมดขัด ⇒ ทางไปต่อเดิม "แก้โหมดให้ตรงกัน"
        var thai = GatewayBatchIntentRules.PostingIssue("x", SettlementFeeVatMode.ThaiVat7)!;
        Assert.Contains("แก้โหมดให้ตรงกัน", thai.NextStep);
    }

    [Fact]
    public void R2M8_หน้ารอบโอนเส้นเดิม_เตือนเมื่อมีช่องทางต่างประเทศผูกconfig_ไม่มีก็ไม่เตือน()
    {
        var w = GatewayBatchIntentRules.LegacyForeignChannelWarning(new[] { "Stripe ต่างประเทศ" });
        Assert.Contains("Stripe ต่างประเทศ", w);
        Assert.Contains("ไม่ตั้งหนี้ ภ.พ.36", w);
        Assert.Null(GatewayBatchIntentRules.LegacyForeignChannelWarning(Array.Empty<string>()));
        Assert.Equal("ก · ข", GatewayBatchIntentRules.JoinWarnings("ก", null, "ข"));
        Assert.Null(GatewayBatchIntentRules.JoinWarnings(null, " "));
    }

    // ═════════════════ R2M-11: POS คืนเงินบัตร/e-Wallet/เช็ค ลงผังเดียวกับขาขายเดิม ═════════════════

    [Fact]
    public void R2M11_บิลบัตรก่อนdeployลงธนาคารที่ปัก_คืนเงินลงธนาคารเดียวกันไม่ใช่11340()
    {
        var pinnedBank = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var sale = new[]
        {
            (pinnedBank, 500m, (string?)"รับเงิน บัตรเครดิต POS #P-1"),
            (cash, 100m, (string?)"รับเงิน เงินสด POS #P-1"),
            (Guid.NewGuid(), 0m, (string?)"รายได้ขาย POS #P-1"),
        };
        Assert.Equal(pinnedBank, MoneyAccountFallback.RefundAccountFromSale(PaymentMethod.CreditCard, "รับเงิน บัตรเครดิต POS #P-1", sale));
    }

    [Fact]
    public void R2M11_ทิศตรงข้าม_เงินสดและโอนใช้กติกาปัจจุบัน_สองผังหรือไม่พบขาขายไม่เดา()
    {
        var a = Guid.NewGuid();
        var sale = new[] { (a, 100m, (string?)"รับเงิน เงินสด POS #P-2"), (a, 100m, (string?)"รับเงิน โอนธนาคาร POS #P-2") };
        Assert.Null(MoneyAccountFallback.RefundAccountFromSale(PaymentMethod.Cash, "รับเงิน เงินสด POS #P-2", sale));
        Assert.Null(MoneyAccountFallback.RefundAccountFromSale(PaymentMethod.BankTransfer, "รับเงิน โอนธนาคาร POS #P-2", sale));
        var twoCards = new[] { (Guid.NewGuid(), 50m, (string?)"รับเงิน บัตรเครดิต POS #P-3"), (Guid.NewGuid(), 50m, (string?)"รับเงิน บัตรเครดิต POS #P-3") };
        Assert.Null(MoneyAccountFallback.RefundAccountFromSale(PaymentMethod.CreditCard, "รับเงิน บัตรเครดิต POS #P-3", twoCards));
        Assert.Null(MoneyAccountFallback.RefundAccountFromSale(PaymentMethod.EWallet, "รับเงิน e-Wallet POS #P-4", sale));
    }

    // ═════════════════ R2M-12 (ข้อ 15): ใบสรุปเพิ่มเติมที่หน้าตาเหมือนรอบแรก — ยืนยันรายบรรทัดได้ ═════════════════

    private static SettlementPostingFacts Facts(SettlementPostingPlan plan, IReadOnlyList<SettlementDuplicateSale> dups)
        => new(
            SettlementBatchStatus.Matched, Day, Day, null, new Dictionary<DateTime, string>(),
            new HashSet<(int, int)>(), new HashSet<(int, int)>(), Array.Empty<string>(),
            true, true, Clearing, Array.Empty<SettlementReceiptTarget>(),
            Array.Empty<SettlementClearingSource>(), dups, true, true, 0);

    [Fact]
    public void R2M12_ยืนยันทุกบรรทัดแล้วเป็นใบเพิ่มเติม_ยืนยันบางบรรทัดยังบล็อกเฉพาะบรรทัดที่เหลือ()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var sup = new SettlementSupplementarySummary(Day, new[] { a, b }, "TIV-0100", "PO-1", true, 1);
        var hits = new[] { new SettlementContentHit<Guid>(a, new[] { "PO-1" }), new SettlementContentHit<Guid>(b, new[] { "PO-1" }) };

        var (d1, k1) = SettlementSummarySupplement.SplitDuplicates(new[] { sup }, hits, new HashSet<Guid> { a });
        var dup = Assert.Single(d1);
        Assert.Empty(k1);
        Assert.Equal(new[] { b }, dup.LineIds);
        Assert.True(dup.DistinctConfirmable);
        Assert.Contains("ยืนยันแล้ว 1 บรรทัด", dup.Evidence);

        var (d2, k2) = SettlementSummarySupplement.SplitDuplicates(new[] { sup }, hits, new HashSet<Guid> { a, b });
        Assert.Empty(d2);
        Assert.Single(k2);
    }

    [Fact]
    public void R2M12_ด่านแยกรหัส_กองยืนยันได้มีทางไปต่อสองทาง_กองออเดอร์ซ้ำจริงยังเป็นรหัสเดิม()
    {
        var plan = SettlementBatchMath.Plan(Batch(1000m), new[] { L(SettlementLineType.Sale, 1000m, DocA) },
            Channel(SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None), true);
        var line = Guid.NewGuid();
        var gated = SettlementPostingGate.Evaluate(plan, Facts(plan, new[]
        {
            new SettlementDuplicateSale(new[] { line }, "หน้าตาเหมือนรอบแรก", DistinctConfirmable: true),
            new SettlementDuplicateSale(new[] { Guid.NewGuid() }, "ออเดอร์นี้มีเอกสารแล้ว"),
        }));
        var sup = Assert.Single(gated.Issues, i => i.Code == SettlementPlanIssueCode.SummarySupplementDuplicate);
        Assert.True(sup.Blocking);
        Assert.Equal(SettlementSummarySupplement.DistinctConfirmNextStep, sup.NextStep);
        Assert.Contains("ยกเลิกรอบโอน", sup.NextStep);
        Assert.Contains("ยืนยันว่าเป็นรายการจริง", sup.NextStep);
        Assert.Single(gated.Issues, i => i.Code == SettlementPlanIssueCode.SummarySaleDuplicate);
        Assert.False(gated.CanPost);

        // คำขอยืนยัน: บรรทัดต้องอยู่ในกองที่ยืนยันได้ของพรีวิวปัจจุบัน + เหตุผลบังคับ
        Assert.Null(SettlementSummarySupplement.ConfirmRefusal(new[] { line }, "ตรวจรายงานแพลตฟอร์มแล้วเป็นคนละคำสั่งซื้อ", gated.Issues));
        Assert.NotNull(SettlementSummarySupplement.ConfirmRefusal(new[] { line }, " ", gated.Issues));
        Assert.NotNull(SettlementSummarySupplement.ConfirmRefusal(new[] { Guid.NewGuid() }, "เหตุผล", gated.Issues));
        Assert.NotNull(SettlementSummarySupplement.ConfirmRefusal(Array.Empty<Guid>(), "เหตุผล", gated.Issues));
    }

    // ═════════════════ R2M-13 (ข้อ 27): งวดกลางของใบที่ WHT ถูกบันทึกครบแล้ว ไม่บล็อก ═════════════════

    [Fact]
    public void R2M13_WHTของใบบันทึกครบจากงวดก่อน_งวดกลางไม่บล็อก_ส่ง0()
    {
        var remaining = SettlementReceiptWht.Remaining(30m, 30m, 0m);
        Assert.Equal(0m, remaining);
        Assert.Equal(SettlementReceiptWhtKind.None, SettlementReceiptWht.Decide(remaining, 500m, 200m));
        // ลงเกินไม่ติดลบ (ใบเสร็จ + การรับชำระซ้อนกัน)
        Assert.Equal(0m, SettlementReceiptWht.Remaining(30m, 20m, 20m));
    }

    [Fact]
    public void R2M13_ทิศตรงข้าม_ยังเหลือWHTค้าง_รับบางส่วนยังบล็อก_รับครบเป็นงวดสุดท้าย()
    {
        var remaining = SettlementReceiptWht.Remaining(30m, 10m, 0m);
        Assert.Equal(20m, remaining);
        Assert.Equal(SettlementReceiptWhtKind.Undecidable, SettlementReceiptWht.Decide(remaining, 500m, 200m));
        Assert.Equal(SettlementReceiptWhtKind.FinalInstallment, SettlementReceiptWht.Decide(remaining, 500m, 500m));
        Assert.Equal(SettlementReceiptWhtKind.None, SettlementReceiptWht.Decide(SettlementReceiptWht.Remaining(0m, 0m, 0m), 500m, 200m));
    }
}

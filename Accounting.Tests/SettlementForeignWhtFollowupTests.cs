using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม WF — แก้ผลฝ่ายค้านทีม W (review200-W): W-1 แบบ ภ.ง.ด. ของด่านเดือนที่ยื่นแล้วมีตัวตั้งเดียว · W-3 ฐาน ภ.พ.36 รวมภาษีที่ออกแทน
/// (คำตัดสินข้อ 40) · W-4/W-9 ค่าคอม/ค่าธรรมเนียมแพลตฟอร์มต่างประเทศ = 40(2) + ค่าตั้งประเภทเงินได้ต่อช่องทาง (คำตัดสินข้อ 41) · W-6 ทางไปต่อบอก
/// config gateway · ทุกเรื่องสองทิศ: ใบที่พังกลับมาถูก + ช่องทางไทย/โหมดหักจากเงินที่จ่ายไม่ถูกแตะ
/// </summary>
public class SettlementForeignWhtFollowupTests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BankAcc = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid DocA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Counterparty = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    private static SettlementChannel Channel(SettlementFeeVatMode vat, SettlementFeeWhtMode wht, string? incomeMap = null, Guid? gateway = null)
        => new()
        {
            CompanyId = Co, Kind = SettlementChannelKind.Marketplace, DisplayName = "Agoda",
            ClearingAccountId = Clearing, FeeVatMode = vat, FeeWhtMode = wht, CounterpartyContactId = Counterparty,
            WhtIncomeTypeMapJson = incomeMap, PaymentProviderConfigId = gateway,
        };

    private static SettlementBatch Batch(decimal net)
        => new() { CompanyId = Co, PayoutRef = "PO-001", PayoutDate = Day, NetPayout = net, BankAccountId = BankAcc };

    private static int _seq;
    private static SettlementLine L(SettlementLineType t, decimal amount, Guid? doc = null)
        => new()
        {
            CompanyId = Co, Seq = ++_seq, LineType = t, Amount = amount, MatchedDocumentId = doc, TxnDate = Day,
            MatchStatus = doc is not null ? SettlementMatchStatus.Matched : SettlementMatchStatus.AutoSummary,
        };

    private static SettlementPostingPlan Plan(SettlementChannel channel, bool vatRegistered, params SettlementLine[] fees)
    {
        var lines = new List<SettlementLine> { L(SettlementLineType.Sale, 1000m, DocA) };
        lines.AddRange(fees);
        return SettlementBatchMath.Plan(Batch(1000m + fees.Sum(l => l.Amount)), lines, channel, vatRegistered);
    }

    private static SettlementPostingFacts Ok(SettlementPostingPlan plan, IReadOnlyCollection<(int, int)>? filedWht = null) => new(
        SettlementBatchStatus.Matched, Day, Day, null, new Dictionary<DateTime, string>(),
        new HashSet<(int, int)>(), filedWht ?? new HashSet<(int, int)>(), Array.Empty<string>(),
        true, true, Clearing,
        plan.Receipts.Select(r => new SettlementReceiptTarget(r.DocumentId, true, "TIV-0001", DocumentType.TaxInvoice,
            DocumentStatus.Approved, r.Amount, false)).ToList(),
        Array.Empty<SettlementClearingSource>(), Array.Empty<SettlementDuplicateSale>(), true, true, 0);

    // ═════════════ W-1: แบบ ภ.ง.ด. ของด่านเดือนที่ยื่นแล้ว — ตัวตั้งเดียว ═════════════

    [Fact]
    public void W1_แผนมีขาภงด54_ชนะแบบที่ผู้เรียกส่งมา_ไม่ว่าจะส่งอะไร()
    {
        var plan = Plan(Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdPayerBorne), true,
            L(SettlementLineType.Commission, -450m));
        Assert.Equal(TaxType.WithholdingTax54, SettlementForeignWht.GateWhtForm(plan, TaxType.WithholdingTax53));
        Assert.Equal(TaxType.WithholdingTax54, SettlementForeignWht.GateWhtForm(plan, TaxType.WithholdingTax3));

        // ผู้เรียกส่ง ภ.ง.ด.3 ผิด ๆ มาก็ยังพูดว่า ภ.ง.ด.54
        var gated = SettlementPostingGate.Evaluate(plan, Ok(plan, new HashSet<(int, int)> { (Day.Year, Day.Month) })
            with { WhtFormType = TaxType.WithholdingTax3 });
        var issue = Assert.Single(gated.Issues, i => i.Code == SettlementPlanIssueCode.TaxPeriodFiled);
        Assert.Contains("ภ.ง.ด.54", issue.Message);
        Assert.DoesNotContain("ภ.ง.ด.3 ", issue.Message);
    }

    [Fact]
    public void W1_ทิศตรงข้าม_ช่องทางไทย_ผู้ลงบัญชีเป็นเจ้าของแบบ_3หรือ53ตามผู้รับ()
    {
        var plan = SettlementBatchMath.Plan(Batch(930m),
            new[] { L(SettlementLineType.Sale, 2000m, DocA), L(SettlementLineType.Commission, -1070m) },
            Channel(SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        Assert.Equal(TaxType.WithholdingTax3, SettlementForeignWht.GateWhtForm(plan, TaxType.WithholdingTax3));
        Assert.Equal(TaxType.WithholdingTax53, SettlementForeignWht.GateWhtForm(plan, TaxType.WithholdingTax53));

        var filed = new HashSet<(int, int)> { (Day.Year, Day.Month) };
        var individual = SettlementPostingGate.Evaluate(plan, Ok(plan, filed) with { WhtFormType = TaxType.WithholdingTax3 });
        Assert.Contains("ภ.ง.ด.3", Assert.Single(individual.Issues, i => i.Code == SettlementPlanIssueCode.TaxPeriodFiled).Message);
        var juristic = SettlementPostingGate.Evaluate(plan, Ok(plan, filed));
        var msg = Assert.Single(juristic.Issues, i => i.Code == SettlementPlanIssueCode.TaxPeriodFiled).Message;
        Assert.Contains("ภ.ง.ด.53", msg);
        Assert.DoesNotContain("ภ.ง.ด.54", msg);
    }

    // ═════════════ W-3 (คำตัดสินข้อ 40): ฐาน ภ.พ.36 รวมภาษีที่ออกแทน ═════════════

    [Fact]
    public void W3_ออกภาษีแทน_450_เงินได้52941_ภพ36เป็น3706()
    {
        var r = SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, true,
            SettlementFeeWhtMode.SelfWithholdPayerBorne, "2", Day);
        Assert.Equal(79.41m, r.WhtAmount);
        Assert.Equal(529.41m, r.WhtCertIncome);
        Assert.Equal(37.06m, r.Pp36Payable);                  // 529.41 × 7% = 37.0587 → 37.06 (ไม่ใช่ 450 × 7% = 31.50)
        Assert.Equal(37.06m, r.InputVat);                     // จด VAT ⇒ 11640 เท่ายอดที่นำส่ง
        Assert.Equal(450m, r.Expense);                        // ภาษีที่ออกแทนเป็นค่าใช้จ่ายแยก (WhtBorneExpense)
        Assert.Equal(79.41m, r.WhtBorneExpense);
        Assert.Equal(37.06m, ForeignServiceVat.SelfAssessedVatOn(ForeignServiceVat.Pp36Base(450m, 79.41m)));
    }

    [Fact]
    public void W3_ทิศตรงข้าม_หักจากเงินที่จ่าย_ไม่หัก_ช่องทางไทย_ฐานเดิม()
    {
        // หักเองแล้วแพลตฟอร์มคืน (ไม่ได้ออกแทน) ⇒ ฐาน ภ.พ.36 = 450
        Assert.Equal(31.50m, SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, true,
            SettlementFeeWhtMode.SelfWithholdReimbursed, "2", Day).Pp36Payable);
        // ไม่หัก ⇒ 450
        Assert.Equal(31.50m, SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, true,
            SettlementFeeWhtMode.None, "2", Day).Pp36Payable);
        // ออกภาษีแทนแต่ประเภทนอก ม.70 (ไม่มีภาษีออกแทนจริง) ⇒ 450
        Assert.Equal(31.50m, SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, true,
            SettlementFeeWhtMode.SelfWithholdPayerBorne, "8ad", Day).Pp36Payable);
        // ช่องทางไทยไม่มี ภ.พ.36 เลย
        Assert.Equal(0m, SettlementFeeTax.Compute(1070m, null, SettlementFeeVatMode.ThaiVat7, true, true,
            SettlementFeeWhtMode.SelfWithholdPayerBorne, "2", Day).Pp36Payable);
        Assert.Equal(450m, ForeignServiceVat.Pp36Base(450m, 0m));
        Assert.Equal(450m, ForeignServiceVat.Pp36Base(450m, -5m));   // ค่าติดลบไม่ลดฐาน
    }

    [Fact]
    public void W3_ไม่จดVAT_ออกภาษีแทน_ภพ36บนฐานรวม_เป็นต้นทุน()
    {
        var r = SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, false,
            SettlementFeeWhtMode.SelfWithholdPayerBorne, "2", Day);
        Assert.Equal(SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable, r.VatTreatment);
        Assert.Equal(37.06m, r.Pp36Payable);
        Assert.Equal(0m, r.InputVat);
        Assert.Equal(487.06m, r.Expense);
    }

    [Fact]
    public void W3_เอกสารคีย์มือ_ภพ36ต่ำกว่าฐานรวมภาษีออกแทน_บอกส่วนขาด_ตรงแล้วเงียบ()
    {
        Assert.Equal(5.56m, ForeignServiceVat.Pp36Shortfall(450m, 79.41m, 31.50m));   // ใบคิด 450 × 7% แต่ออก 50 ทวิ ออกแทน 79.41
        Assert.Equal(0m, ForeignServiceVat.Pp36Shortfall(450m, 79.41m, 37.06m));      // ใบแก้ VAT แล้ว
        Assert.Equal(0m, ForeignServiceVat.Pp36Shortfall(450m, 0m, 31.50m));          // ไม่มีภาษีออกแทน
    }

    // ═════════════ W-4/W-9 (คำตัดสินข้อ 41): ค่าคอม/ค่าธรรมเนียมแพลตฟอร์มต่างประเทศ = 40(2) ═════════════

    [Theory]
    [InlineData(SettlementLineType.Commission)]
    [InlineData(SettlementLineType.PaymentFee)]
    [InlineData(SettlementLineType.ServiceFee)]
    [InlineData(SettlementLineType.WithdrawalFee)]
    public void W4_ต่างประเทศ_ค่าธรรมเนียมแพลตฟอร์ม_402_หัก15_ไม่บล็อก(SettlementLineType type)
    {
        var plan = Plan(Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdReimbursed), true, L(type, -450m));
        Assert.True(plan.CanPost);
        Assert.DoesNotContain(plan.Issues, i => i.Code == SettlementPlanIssueCode.ForeignWhtNotSupported);
        var fee = Assert.Single(plan.FeeDocuments);
        Assert.Equal(67.50m, fee.WhtAmount);
        Assert.Equal("2", Assert.Single(fee.Lines).WhtIncomeCode);
        Assert.Equal(TaxType.WithholdingTax54, fee.WhtForm);
        var choice = SettlementWhtIncomeType.Resolve(type, SettlementFeeVatMode.ForeignPp36, new Dictionary<SettlementLineType, string>());
        Assert.Equal(SettlementIncomeTypeSource.ForeignDefault, choice.Source);
    }

    [Fact]
    public void W4_ทิศตรงข้าม_ช่องทางไทย_รหัสเดิมทุกประเภท()
    {
        var none = new Dictionary<SettlementLineType, string>();
        foreach (var rule in SettlementLineTypeRules.All.Where(r => r.IsFee))
        {
            var c = SettlementWhtIncomeType.Resolve(rule.Type, SettlementFeeVatMode.ThaiVat7, none);
            Assert.Equal(rule.WhtIncomeCode, c.Code);
            Assert.Equal(SettlementIncomeTypeSource.LineTypeTable, c.Source);
        }
        // ค่าธรรมเนียมรับชำระในประเทศยังเป็น 40(8) 3% · ค่าถอนเงินยังไม่หัก (W-9 เปลี่ยนเฉพาะต่างประเทศ)
        Assert.Equal(3m, SettlementFeeTax.Compute(1070m, null, SettlementFeeVatMode.ThaiVat7, true, true,
            SettlementFeeWhtMode.SelfWithholdReimbursed, SettlementWhtIncomeType.For(SettlementLineType.PaymentFee,
                Channel(SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.SelfWithholdReimbursed)).Code, Day).WhtRatePercent);
        Assert.Null(SettlementWhtIncomeType.Resolve(SettlementLineType.WithdrawalFee, SettlementFeeVatMode.ThaiVat7, none).Code);
        // ค่าโฆษณา/ค่าขนส่งของต่างประเทศยังเป็น 40(8) (นอก ม.70 ⇒ บล็อกเหมือนเดิม)
        Assert.Equal("8ad", SettlementWhtIncomeType.Resolve(SettlementLineType.AdsFee, SettlementFeeVatMode.ForeignPp36, none).Code);
        Assert.Equal("8tr", SettlementWhtIncomeType.Resolve(SettlementLineType.ShippingFeeCharged, SettlementFeeVatMode.ForeignPp36, none).Code);
    }

    [Fact]
    public void W4_ค่าตั้งช่องทาง_ค่าโฆษณาไม่หัก_ปลดบล็อก_ค่าคอมยังหัก()
    {
        var blocked = Plan(Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdReimbursed), true,
            L(SettlementLineType.Commission, -450m), L(SettlementLineType.AdsFee, -100m));
        Assert.False(blocked.CanPost);
        var issue = Assert.Single(blocked.Issues, i => i.Code == SettlementPlanIssueCode.ForeignWhtNotSupported);
        Assert.Contains("ประเภทเงินได้ของค่าธรรมเนียม", issue.NextStep);        // ทางไปต่อระดับช่องทาง (ข้อ 41)

        var set = Plan(Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdReimbursed, "{\"AdsFee\":\"none\"}"), true,
            L(SettlementLineType.Commission, -450m), L(SettlementLineType.AdsFee, -100m));
        Assert.True(set.CanPost);
        Assert.Equal(67.50m, set.FeeDocuments.Sum(d => d.WhtAmount));           // ค่าคอม 450 × 15% · โฆษณาไม่หัก

        // ตั้งค่าโฆษณาเป็นค่าสิทธิ 40(3) ⇒ ม.70 15% ทั้งคู่
        var royalty = Plan(Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdReimbursed, "{\"AdsFee\":\"40(3)\"}"), true,
            L(SettlementLineType.Commission, -450m), L(SettlementLineType.AdsFee, -100m));
        Assert.True(royalty.CanPost);
        Assert.Equal(82.50m, royalty.FeeDocuments.Sum(d => d.WhtAmount));
    }

    [Fact]
    public void W4_ค่าตั้งช่องทาง_จำแนกค่าธรรมเนียมรับชำระเป็น408_บล็อกพร้อมทางไปต่อ()
    {
        var plan = Plan(Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdReimbursed, "{\"PaymentFee\":\"8\"}"), true,
            L(SettlementLineType.PaymentFee, -450m));
        Assert.False(plan.CanPost);
        var issue = Assert.Single(plan.Issues, i => i.Code == SettlementPlanIssueCode.ForeignWhtNotSupported);
        Assert.Contains("RD-70-NA", issue.Message);
        Assert.Equal(SettlementIncomeTypeSource.Channel, SettlementWhtIncomeType.For(SettlementLineType.PaymentFee,
            Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdReimbursed, "{\"PaymentFee\":\"8\"}")).Source);
    }

    [Fact]
    public void W4_ตัวอ่านค่าตั้ง_ปฏิเสธคีย์ค่าเสีย_เขียนกลับเป็นรหัสมาตรฐาน()
    {
        var (map, rejected) = SettlementWhtIncomeType.ParseMap(
            "{\"PaymentFee\":\"40(2)\",\"AdsFee\":\"NONE\",\"Sale\":\"2\",\"Commission\":\"1\",\"ServiceFee\":\"x\",\"Bogus\":\"2\",\"Refund\":\"2\"}");
        Assert.Equal("2", map[SettlementLineType.PaymentFee]);                 // มาตรา ⇒ รหัสมาตรฐาน
        Assert.Equal(SettlementWhtIncomeType.NoWithholding, map[SettlementLineType.AdsFee]);
        Assert.Equal(2, map.Count);
        Assert.Contains("Sale", rejected);                                    // ไม่ใช่ค่าธรรมเนียม
        Assert.Contains("Refund", rejected);
        Assert.Contains("Bogus", rejected);
        Assert.Contains("Commission=1", rejected);                            // 40(1) เงินเดือน ไม่ใช่ค่าธรรมเนียม
        Assert.Contains("ServiceFee=x", rejected);
        Assert.Equal("{\"PaymentFee\":\"2\",\"AdsFee\":\"none\"}", SettlementWhtIncomeType.Serialize(map));
        Assert.Null(SettlementWhtIncomeType.Serialize(new Dictionary<SettlementLineType, string>()));
        Assert.Contains("(json)", SettlementWhtIncomeType.ParseMap("[1]").Rejected);
        Assert.Contains("(json)", SettlementWhtIncomeType.ParseMap("{oops").Rejected);
        Assert.Empty(SettlementWhtIncomeType.ParseMap(null).Rejected);
    }

    [Fact]
    public void W4_ค่าตั้งที่เก็บไว้อ่านไม่ได้_บล็อกทุกช่องทางที่หัก_ไม่หักไม่แตะ()
    {
        var bad = Plan(Channel(SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.SelfWithholdReimbursed, "{\"Sale\":\"2\"}"), true,
            L(SettlementLineType.Commission, -1070m));
        var issue = Assert.Single(bad.Issues, i => i.Code == SettlementPlanIssueCode.WhtIncomeTypeMapInvalid);
        Assert.True(issue.Blocking);
        Assert.False(bad.CanPost);
        Assert.True(issue.NextStep.Length > 0);

        var noWht = Plan(Channel(SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None, "{\"Sale\":\"2\"}"), true,
            L(SettlementLineType.Commission, -1070m));
        Assert.DoesNotContain(noWht.Issues, i => i.Code == SettlementPlanIssueCode.WhtIncomeTypeMapInvalid);
    }

    [Fact]
    public void W4_ตัวเลือกหน้าตั้งค่า_ครบทุกประเภทค่าธรรมเนียม_ค่าตั้งต้นสองบริบท()
    {
        var opts = SettlementWhtIncomeType.Options();
        Assert.Equal(SettlementLineTypeRules.All.Count(r => r.IsFee), opts.Count);
        var payment = Assert.Single(opts, o => o.LineType == nameof(SettlementLineType.PaymentFee));
        Assert.Equal("8", payment.DomesticDefault);
        Assert.Equal("2", payment.ForeignDefault);
        var catalog = SettlementReferenceCatalog.Build();
        Assert.Equal(opts.Count, catalog.FeeIncomeTypes!.Count);
        Assert.Contains(catalog.WhtIncomeCodes!, o => o.Value == SettlementWhtIncomeType.NoWithholding);
        Assert.DoesNotContain(catalog.WhtIncomeCodes!, o => o.Value == "1");
        Assert.Contains("15", SettlementWhtIncomeType.Describe(
            SettlementWhtIncomeType.Resolve(SettlementLineType.PaymentFee, SettlementFeeVatMode.ForeignPp36, new Dictionary<SettlementLineType, string>()),
            SettlementFeeVatMode.ForeignPp36, Day));
        Assert.Contains("ไม่หัก", SettlementWhtIncomeType.Describe(new SettlementIncomeTypeChoice(null, SettlementIncomeTypeSource.Channel),
            SettlementFeeVatMode.ForeignPp36, Day));
    }

    // ═════════════ W-6: ทางไปต่อบอกให้แก้ config gateway ด้วย ═════════════

    [Fact]
    public void W6_ช่องทางผูกgateway_ทางไปต่อบอกให้แก้การตั้งค่าgatewayด้วย_ไม่ผูกไม่พูด()
    {
        // ฝ่ายค้านรอบสอง R2M-7: ช่องทางต่างประเทศที่ผูก gateway ไม่มีโหมดที่ "ตรงกัน" ได้ (X-3) ⇒ ทางไปต่อต้องเป็นตัวเดียวกับด่านโหมด
        // (สร้างช่องทางไม่ผูก config + นำเข้าไฟล์) ไม่ใช่ "แก้การตั้งค่า gateway ให้ตรงกัน" ที่ทำตามแล้วยังถูกบล็อก
        var gw = Plan(Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.AgentWithholds, null, Guid.NewGuid()), true,
            L(SettlementLineType.PaymentFee, -450m));
        var next = Assert.Single(gw.Issues, i => i.Code == SettlementPlanIssueCode.ForeignWhtNotSupported).NextStep;
        Assert.Contains(GatewayBatchIntentRules.ForeignPp36BoundNextStep, next);
        Assert.DoesNotContain("ให้ตรงกันด้วย", next);
        // ข้อความเดียวกับด่านโหมดของช่องทางเดียวกัน (สองข้อความบนพรีวิวเดียวกันไม่ขัดกัน)
        Assert.Contains(GatewayBatchIntentRules.ForeignPp36BoundNextStep, GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.None,
            GatewayFeeWhtMode.None, SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.AgentWithholds, true, null));
        Assert.Equal(GatewayBatchIntentRules.ForeignPp36BoundNextStep, GatewayBatchIntentRules.PostingIssue("x", SettlementFeeVatMode.ForeignPp36)!.NextStep);

        var plain = Plan(Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.AgentWithholds), true,
            L(SettlementLineType.PaymentFee, -450m));
        Assert.DoesNotContain(GatewayBatchIntentRules.ForeignPp36BoundNextStep,
            Assert.Single(plain.Issues, i => i.Code == SettlementPlanIssueCode.ForeignWhtNotSupported).NextStep);
    }
}

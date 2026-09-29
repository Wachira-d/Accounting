using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม W — หัก ณ ที่จ่ายค่าธรรมเนียมของผู้ให้บริการต่างประเทศในรอบโอน settlement (คำตัดสินข้อ 13 · แทนการบล็อกเหมาของ review198-A R-A5) ·
/// R-A4 (บริษัทไม่จด VAT ยังต้อง ภ.พ.36 และ VAT เป็นต้นทุน) รวมกับ WHT · สองทิศ: ช่องทางไทยต้องเหมือนเดิมทุกตัวเลข
/// </summary>
public class SettlementForeignWhtTests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BankGl = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid BankAcc = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid DocA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Counterparty = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    private static SettlementChannel Channel(SettlementFeeVatMode vat, SettlementFeeWhtMode wht)
        => new()
        {
            CompanyId = Co, Kind = SettlementChannelKind.Marketplace, DisplayName = "Agoda",
            ClearingAccountId = Clearing, FeeVatMode = vat, FeeWhtMode = wht, CounterpartyContactId = Counterparty,
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

    private static SettlementPostingPlan ForeignPlan(SettlementFeeWhtMode wht, bool vatRegistered = true, params SettlementLine[] fees)
    {
        var lines = new List<SettlementLine> { L(SettlementLineType.Sale, 1000m, DocA) };
        lines.AddRange(fees.Length > 0 ? fees : new[] { L(SettlementLineType.Commission, -450m) });
        return SettlementBatchMath.Plan(Batch(1000m + lines.Skip(1).Sum(l => l.Amount)), lines,
            Channel(SettlementFeeVatMode.ForeignPp36, wht), vatRegistered);
    }

    private static SettlementPostingFacts Ok(SettlementPostingPlan plan, IReadOnlyCollection<(int, int)>? filedWht = null) => new(
        SettlementBatchStatus.Matched, Day, Day, null, new Dictionary<DateTime, string>(),
        new HashSet<(int, int)>(), filedWht ?? new HashSet<(int, int)>(), Array.Empty<string>(),
        true, true, Clearing,
        plan.Receipts.Select(r => new SettlementReceiptTarget(r.DocumentId, true, "TIV-0001", DocumentType.TaxInvoice,
            DocumentStatus.Approved, r.Amount, false)).ToList(),
        Array.Empty<SettlementClearingSource>(), Array.Empty<SettlementDuplicateSale>(), true, true, 0);

    // ═════════════ ม.70: ค่านายหน้า 40(2) หักได้ ลง ภ.ง.ด.54 ═════════════

    [Fact]
    public void ต่างประเทศ_ค่าคอม_ออกภาษีแทน_หัก15ต่อ85_ลง21918_ภงด54()
    {
        var plan = ForeignPlan(SettlementFeeWhtMode.SelfWithholdPayerBorne);
        Assert.True(plan.CanPost);
        var fee = Assert.Single(plan.FeeDocuments);
        Assert.Equal(TaxType.WithholdingTax54, fee.WhtForm);
        Assert.Equal(79.41m, fee.WhtAmount);                                     // 450 × 15/85 = 79.4117…
        var line = Assert.Single(fee.Lines);
        Assert.Equal(15m, line.WhtRatePercent);
        Assert.Equal(450m, line.WhtBase);
        Assert.Equal(529.41m, line.WhtCertIncome);                                // ออกภาษีแทน: เงินได้ = ฐาน + ภาษี
        Assert.Equal(37.06m, fee.Pp36Payable);                                   // คำตัดสินข้อ 40 (ทีม WF): ฐาน ภ.พ.36 = 529.41 รวมภาษีออกแทน (เดิม 31.50 บนฐาน 450)

        var legs54 = plan.PayoutJournal.Where(l => l.AccountRole == SettlementAccountRoles.WhtPayable54).ToList();
        Assert.Equal(79.41m, legs54.Sum(l => l.Credit));
        Assert.All(legs54, l => Assert.Equal(WhtPayableAccount.Pnd54Code, l.DefaultAccountCode));
        Assert.All(legs54, l => Assert.Contains("ภ.ง.ด.54", l.Description));
        Assert.DoesNotContain(plan.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.WhtPayable);   // ไม่มี ภ.ง.ด.53 เลย
        Assert.Equal(79.41m, plan.PayoutJournal.Where(l => l.AccountRole == SettlementAccountRoles.Commission).Sum(l => l.Debit));
        Assert.Equal(plan.PayoutJournal.Sum(l => l.Debit), plan.PayoutJournal.Sum(l => l.Credit));

        var cert = SettlementDocumentBuilder.WhtCertificate(fee, Counterparty, Guid.NewGuid(), Day, "PO-001");
        Assert.NotNull(cert);
        Assert.Equal(TaxType.WithholdingTax54, cert!.TaxFormType);
        Assert.Equal(79.41m, cert.Lines.Sum(l => l.TaxAmount));
        Assert.Equal(15m, Assert.Single(cert.Lines).TaxRate);
        Assert.Equal(WithholdingTaxCertType.PayAlways, cert.CertificateType);
    }

    [Fact]
    public void ต่างประเทศ_หักเองแล้วแพลตฟอร์มคืน_หัก15_ลูกหนี้รอคืน()
    {
        var plan = ForeignPlan(SettlementFeeWhtMode.SelfWithholdReimbursed);
        Assert.True(plan.CanPost);
        Assert.Equal(67.50m, Assert.Single(plan.FeeDocuments).WhtAmount);          // 450 × 15%
        Assert.Equal(67.50m, plan.PayoutJournal.Where(l => l.AccountRole == SettlementAccountRoles.WhtReimbursable).Sum(l => l.Debit));
        Assert.Equal(67.50m, plan.PayoutJournal.Where(l => l.AccountRole == SettlementAccountRoles.WhtPayable54).Sum(l => l.Credit));
    }

    [Fact]
    public void ต่างประเทศ_ผังบัญชีไม่มี21918_ล้มดังพร้อมชื่อบทบาท_มีแล้วผ่าน()
    {
        var plan = ForeignPlan(SettlementFeeWhtMode.SelfWithholdPayerBorne);
        var baseChart = new[] { "11320", "11640", "21912", "53140" }
            .Select(c => new SettlementChartAccount(Guid.NewGuid(), c, true))
            .Append(new SettlementChartAccount(Clearing, "11341", true))
            .Append(new SettlementChartAccount(BankGl, "11120", true)).ToList();
        var missing = SettlementAccountResolver.Resolve(plan, new SettlementChartIndex(baseChart), BankGl);
        Assert.Contains(missing.Errors, e => e.Contains(WhtPayableAccount.Pnd54Code) && e.Contains("ภ.ง.ด.54"));

        var withPnd54 = SettlementAccountResolver.Resolve(plan,
            new SettlementChartIndex(baseChart.Append(new SettlementChartAccount(Guid.NewGuid(), WhtPayableAccount.Pnd54Code, true))), BankGl);
        Assert.True(withPnd54.Ok);
    }

    // ═════════════ R-A4 × WHT: ไม่จด VAT ⇒ ภ.พ.36 ยังเกิด · VAT เป็นต้นทุน · WHT ฐานก่อน VAT ═════════════

    [Fact]
    public void RA4_ไม่จดVAT_ต่างประเทศ_ภพ36เกิด_ไม่เคลม_และหักภงด54บนฐานเดิม()
    {
        var plan = ForeignPlan(SettlementFeeWhtMode.SelfWithholdPayerBorne, vatRegistered: false);
        Assert.True(plan.CanPost);
        var fee = Assert.Single(plan.FeeDocuments);
        Assert.Equal(SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable, fee.VatTreatment);
        Assert.Equal(37.06m, fee.Pp36Payable);                                    // §83/6 ผู้จ่ายยื่นเสมอ · ฐานรวมภาษีออกแทน 529.41 (ข้อ 40)
        Assert.Equal(0m, fee.InputVat);                                           // เคลมไม่ได้
        Assert.Equal(487.06m, fee.Expense);                                       // ⇒ ต้นทุน (450 + 37.06)
        Assert.Equal(79.41m, fee.WhtAmount);                                      // ฐาน WHT = 450 (ไม่รวม VAT ที่ประเมินเอง)
        Assert.Equal(TaxType.WithholdingTax54, fee.WhtForm);
    }

    // ═════════════ บล็อกเฉพาะที่ระบบคิดให้ไม่ได้ ═════════════

    [Fact]
    public void ต่างประเทศ_ค่าโฆษณา40ข8_นอกม70_บล็อกเฉพาะบรรทัดนั้น_ค่าคอมไม่ถูกบล็อก()
    {
        var commission = L(SettlementLineType.Commission, -450m);
        var ads = L(SettlementLineType.AdsFee, -100m);
        var plan = ForeignPlan(SettlementFeeWhtMode.SelfWithholdPayerBorne, true, commission, ads);
        Assert.False(plan.CanPost);
        var issue = Assert.Single(plan.Issues, i => i.Code == SettlementPlanIssueCode.ForeignWhtNotSupported);
        Assert.True(issue.Blocking);
        Assert.Contains("RD-70-NA", issue.Message);
        Assert.Contains(ads.Id, issue.LineIds);
        Assert.DoesNotContain(commission.Id, issue.LineIds);
        Assert.True(issue.NextStep.Length > 0);
        // บรรทัดโฆษณาไม่ได้อัตราในประเทศ (2%) แอบเข้ามา
        Assert.All(plan.FeeDocuments.SelectMany(f => f.Lines).Where(l => l.LineType == SettlementLineType.AdsFee),
            l => Assert.Equal(0m, l.WhtAmount));
    }

    [Fact]
    public void ต่างประเทศ_แพลตฟอร์มเป็นตัวแทนหักแทน_บล็อก()
    {
        var plan = ForeignPlan(SettlementFeeWhtMode.AgentWithholds);
        Assert.False(plan.CanPost);
        Assert.Contains(plan.Issues, i => i.Code == SettlementPlanIssueCode.ForeignWhtNotSupported && i.Blocking && i.Message.Contains("ภ.ง.ด.54"));
    }

    [Fact]
    public void ต่างประเทศ_ไม่หัก_ไม่มีปัญหา_WHT()
    {
        var plan = ForeignPlan(SettlementFeeWhtMode.None);
        Assert.True(plan.CanPost);
        Assert.DoesNotContain(plan.Issues, i => i.Code == SettlementPlanIssueCode.ForeignWhtNotSupported);
        Assert.Equal(0m, Assert.Single(plan.FeeDocuments).WhtAmount);
    }

    // ═════════════ ทิศตรงข้าม: ช่องทางไทยเหมือนเดิม ═════════════

    [Fact]
    public void ช่องทางไทย_ออกภาษีแทน_ยังเป็น3ต่อ97_ภงด53_21917()
    {
        var plan = SettlementBatchMath.Plan(Batch(930m),
            new[] { L(SettlementLineType.Sale, 2000m, DocA), L(SettlementLineType.Commission, -1070m) },
            Channel(SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        Assert.True(plan.CanPost);
        var fee = Assert.Single(plan.FeeDocuments);
        Assert.Equal(TaxType.WithholdingTax53, fee.WhtForm);
        Assert.Equal(30.93m, fee.WhtAmount);
        Assert.Equal(30.93m, plan.PayoutJournal.Where(l => l.AccountRole == SettlementAccountRoles.WhtPayable).Sum(l => l.Credit));
        Assert.DoesNotContain(plan.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.WhtPayable54);
        Assert.Equal(TaxType.WithholdingTax53,
            SettlementDocumentBuilder.WhtCertificate(fee, Counterparty, Guid.NewGuid(), Day, "PO-001")!.TaxFormType);
    }

    // ═════════════ ผู้ติดต่อต่างประเทศบนช่องทางที่ไม่ได้ตั้งเป็นต่างประเทศ (R-A5 อีกรูป) ═════════════

    [Fact]
    public void ผู้ติดต่อต่างประเทศ_ช่องทางไทย_มีขาWHT_บล็อก_ไทยหรือไม่รู้ประเทศไม่บล็อก()
    {
        var channel = Channel(SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.SelfWithholdPayerBorne);
        var plan = SettlementBatchMath.Plan(Batch(930m),
            new[] { L(SettlementLineType.Sale, 2000m, DocA), L(SettlementLineType.Commission, -1070m) }, channel, true);

        var ie = SettlementForeignWht.CounterpartyCountryIssue(channel, plan, "ie");
        Assert.NotNull(ie);
        Assert.True(ie!.Blocking);
        Assert.Equal(SettlementPlanIssueCode.ForeignWhtNotSupported, ie.Code);
        Assert.Contains("IE", ie.Message);
        Assert.Equal(30.93m, ie.Amount);

        Assert.Null(SettlementForeignWht.CounterpartyCountryIssue(channel, plan, "TH"));
        Assert.Null(SettlementForeignWht.CounterpartyCountryIssue(channel, plan, null));
        Assert.Null(SettlementForeignWht.CounterpartyCountryIssue(channel, plan, ""));

        // ไม่หัก ⇒ ไม่มีอะไรให้บล็อก · ช่องทางต่างประเทศอยู่แล้ว ⇒ เส้น ภ.ง.ด.54 ดูแลเอง
        var none = Channel(SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None);
        var nonePlan = SettlementBatchMath.Plan(Batch(930m),
            new[] { L(SettlementLineType.Sale, 2000m, DocA), L(SettlementLineType.Commission, -1070m) }, none, true);
        Assert.Null(SettlementForeignWht.CounterpartyCountryIssue(none, nonePlan, "IE"));
        var foreign = Channel(SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.SelfWithholdPayerBorne);
        Assert.Null(SettlementForeignWht.CounterpartyCountryIssue(foreign, ForeignPlan(SettlementFeeWhtMode.SelfWithholdPayerBorne), "IE"));
    }

    [Fact]
    public void ด่านเดือนที่ยื่นแล้ว_ต่างประเทศพูดว่า_ภงด54()
    {
        var plan = ForeignPlan(SettlementFeeWhtMode.SelfWithholdPayerBorne);
        var gated = SettlementPostingGate.Evaluate(plan, Ok(plan, new HashSet<(int, int)> { (Day.Year, Day.Month) }));
        var issue = Assert.Single(gated.Issues, i => i.Code == SettlementPlanIssueCode.TaxPeriodFiled);
        Assert.Contains("ภ.ง.ด.54", issue.Message);
        Assert.DoesNotContain("ภ.ง.ด.53", issue.Message);
    }

    [Fact]
    public void ตัวคิดภาษีค่าธรรมเนียม_ต่างประเทศ_ใช้ตัวตัดสินเดียว()
    {
        var r = SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, true,
            SettlementFeeWhtMode.SelfWithholdReimbursed, "2", Day);
        Assert.Equal(ForeignWhtRateResolver.Section70GeneralRate, r.WhtRatePercent);
        Assert.Equal(67.50m, r.WhtAmount);
        // นอก ม.70 / ตัวแทนหักแทน ⇒ 0 (แผนบล็อกพร้อมทางไปต่อ)
        Assert.Equal(0m, SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, true,
            SettlementFeeWhtMode.SelfWithholdReimbursed, "8", Day).WhtAmount);
        Assert.Equal(0m, SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, true,
            SettlementFeeWhtMode.AgentWithholds, "2", Day).WhtAmount);
        // ไทยเหมือนเดิม
        Assert.Equal(3m, SettlementFeeTax.Compute(1070m, null, SettlementFeeVatMode.ThaiVat7, true, true,
            SettlementFeeWhtMode.SelfWithholdReimbursed, "2", Day).WhtRatePercent);
    }
}

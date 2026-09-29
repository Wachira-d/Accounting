using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 198 เฟส 1 ทีม C — ผู้ลงบัญชีรอบโอน settlement: ตัวตัดสินบริสุทธิ์ของ <c>SettlementPostingService</c>
/// (Helpers/SettlementPosting.cs) + การแก้สัญญาทีม A จากฝ่ายค้าน review198-A (R-A3/A4/A5/A6/A8) · ทุกเรื่องมีสองครึ่ง:
/// เคสที่ต้องถูกกัน และเคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// </summary>
public class SettlementPostingTests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BankGl = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid BankAcc = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid DocA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Counterparty = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<string, Guid> Codes = new[]
        {
            "11320", "11350", "11630", "11640", "11910", "21912", "21917", "41000",
            "43050", "53120", "53130", "53140", "53150", "53170", "54710", "54950", "57140",
        }
        .ToDictionary(c => c, _ => Guid.NewGuid());

    private static SettlementChartIndex Chart(params SettlementChartAccount[] extra)
        => new(Codes.Select(kv => new SettlementChartAccount(kv.Value, kv.Key, true))
            .Append(new SettlementChartAccount(Clearing, "11341", true))
            .Append(new SettlementChartAccount(BankGl, "11120", true))
            .Concat(extra));

    private static SettlementChannel Channel(SettlementFeeVatMode vat = SettlementFeeVatMode.ThaiVat7,
        SettlementFeeWhtMode wht = SettlementFeeWhtMode.None)
        => new()
        {
            CompanyId = Co, Kind = SettlementChannelKind.Marketplace, DisplayName = "Shopee ร้านหลัก",
            ClearingAccountId = Clearing, FeeVatMode = vat, FeeWhtMode = wht, CounterpartyContactId = Counterparty,
        };

    private static SettlementBatch Batch(decimal net)
        => new() { CompanyId = Co, PayoutRef = "PO-001", PayoutDate = Day, NetPayout = net, BankAccountId = BankAcc };

    private static int _seq;
    private static SettlementLine L(SettlementLineType t, decimal amount, Guid? doc = null, DateTime? txn = null,
        string? order = null, Guid? intent = null, SettlementMatchStatus? match = null)
        => new()
        {
            CompanyId = Co, Seq = ++_seq, LineType = t, Amount = amount, MatchedDocumentId = doc, TxnDate = txn ?? Day,
            ExternalOrderId = order, PaymentIntentId = intent,
            MatchStatus = match ?? (doc is not null ? SettlementMatchStatus.Matched : SettlementMatchStatus.AutoSummary),
        };

    private static SettlementPostingFacts Ok(SettlementPostingPlan plan) => new(
        SettlementBatchStatus.Matched, Day, Day, null, new Dictionary<DateTime, string>(),
        new HashSet<(int, int)>(), new HashSet<(int, int)>(), Array.Empty<string>(),
        true, true, Clearing,
        plan.Receipts.Select(r => new SettlementReceiptTarget(r.DocumentId, true, "TIV-0001", DocumentType.TaxInvoice,
            DocumentStatus.Approved, r.Amount, false)).ToList(),
        Array.Empty<SettlementClearingSource>(), Array.Empty<SettlementDuplicateSale>(), true, true, 0);

    private static decimal DocTotal(CreateDocumentRequest r)
        => r.Lines.Sum(l => Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero) + (l.VatAmountOverride ?? 0m));

    // ═════════════ plan → JE (ผังของบริษัทนี้เท่านั้น) ═════════════

    [Fact]
    public void JE_รอบโอน_หาผังครบ_Drเท่ากับCr_ธนาคารคือผังของบัญชีธนาคาร()
    {
        var plan = SettlementBatchMath.Plan(Batch(948.48m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -51.52m),
                    L(SettlementLineType.ReserveHold, -100m), L(SettlementLineType.ReserveRelease, 100m) }, Channel(), true);
        Assert.True(plan.CanPost);
        var r = SettlementAccountResolver.Resolve(plan, Chart(), BankGl);
        Assert.True(r.Ok);
        Assert.Equal(plan.PayoutJournal.Count, r.Journal.Count);
        Assert.Equal(r.Journal.Sum(l => l.Debit), r.Journal.Sum(l => l.Credit));
        Assert.Equal(948.48m, Assert.Single(r.Journal, l => l.AccountId == BankGl).Debit);
        Assert.Equal(Codes["11350"], Assert.Single(r.Journal, l => l.Debit == 0m && l.Credit == 100m && l.AccountId != Clearing).AccountId);
        Assert.Equal(Clearing, r.ClearingAccountId);
        Assert.Equal(Codes["53140"], Assert.Single(Assert.Single(r.FeeLineAccounts)));
    }

    [Fact]
    public void ผังจากFeeAccountMapที่เป็นของบริษัทอื่น_ถูกปฏิเสธ_ไม่ถอยไปผังมาตรฐานเงียบ()
    {
        var foreignTenantAccount = Guid.NewGuid();
        var ch = Channel();
        ch.FeeAccountMapJson = "{\"commission\":\"" + foreignTenantAccount + "\"}";
        var plan = SettlementBatchMath.Plan(Batch(948.48m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -51.52m) }, ch, true);

        var r = SettlementAccountResolver.Resolve(plan, Chart(), BankGl);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("ไม่ใช่ผังของบริษัทนี้"));
        Assert.Empty(r.FeeLineAccounts[0]);                          // ไม่ได้ 53140 แทน
        var gated = SettlementPostingGate.Evaluate(plan, Ok(plan) with { AccountErrors = r.Errors });
        Assert.False(gated.CanPost);
        Assert.Contains(gated.Issues, i => i.Code == SettlementPlanIssueCode.AccountUnresolved && i.Blocking);

        // ทิศตรงข้าม: ผังเดียวกันแต่อยู่ในผังของบริษัทนี้ ⇒ ใช้ผังนั้น
        var own = SettlementAccountResolver.Resolve(plan, Chart(new SettlementChartAccount(foreignTenantAccount, "53141", true)), BankGl);
        Assert.True(own.Ok);
        Assert.Equal(foreignTenantAccount, Assert.Single(own.FeeLineAccounts[0]));
    }

    [Fact]
    public void ผังปิดใช้_หรือบัญชีธนาคารไม่ผูกผัง_ล้มพร้อมชื่อบทบาท()
    {
        var plan = SettlementBatchMath.Plan(Batch(948.48m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -51.52m) }, Channel(), true);
        var closedCommission = new SettlementChartIndex(new[]
        {
            new SettlementChartAccount(Codes["53140"], "53140", false), new SettlementChartAccount(Clearing, "11341", true),
        });
        var r = SettlementAccountResolver.Resolve(plan, closedCommission, null);
        Assert.Contains(r.Errors, e => e.Contains("53140") && e.Contains("ค่าคอมมิชชัน"));
        Assert.Contains(r.Errors, e => e.Contains("บัญชีธนาคาร"));
    }

    // ═════════════ ลงซ้ำไม่ได้ · ทำต่อจากที่ค้างได้ ═════════════

    [Fact]
    public void ลงบัญชีแล้ว_กดซ้ำ_ด่านบอกลงแล้ว_ไม่มีทางผ่าน()
    {
        var plan = SettlementBatchMath.Plan(Batch(948.48m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -51.52m) }, Channel(), true);
        foreach (var st in new[] { SettlementBatchStatus.Posted, SettlementBatchStatus.BankMatched, SettlementBatchStatus.Voided })
        {
            var g = SettlementPostingGate.Evaluate(plan, Ok(plan) with { Status = st });
            Assert.False(g.CanPost);
            Assert.Contains(g.Issues, i => i.Code == SettlementPlanIssueCode.StatusNotPostable && i.Blocking);
        }
        Assert.True(SettlementPostingGate.Evaluate(plan, Ok(plan)).CanPost);   // ทิศตรงข้าม: ยังไม่ลง = ลงได้
    }

    [Fact]
    public void ป้ายของรอบโอน_คงที่ต่อBatchและชิ้น_ต่างBatchไม่ชนกัน()
    {
        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();
        var fee = SettlementPostingKeys.FeeComponent(SettlementFeeVatTreatment.InputVatPending);
        Assert.Equal(SettlementPostingKeys.Creator(b1, fee), SettlementPostingKeys.Creator(b1, fee));
        Assert.NotEqual(SettlementPostingKeys.Creator(b1, fee), SettlementPostingKeys.Creator(b2, fee));
        Assert.StartsWith(SettlementPostingKeys.CreatorPrefix(b1), SettlementPostingKeys.Creator(b1, fee));
        Assert.Equal("sum-20261001", SettlementPostingKeys.SummaryComponent(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.NotEqual(SettlementPostingKeys.PaymentMarker(b1), SettlementPostingKeys.PaymentMarker(b2));
        // ตัวหาใบสรุปของรอบโอนอื่น (DuplicateSalesAsync) ตัด batch id ที่ตำแหน่ง 18 ยาว 32 — รูปป้ายเปลี่ยน = ต้องแก้ตัวอ่านด้วย
        Assert.Equal(51, SettlementPostingKeys.CreatorPrefix(b1).Length);
        Assert.Equal(b1.ToString("N"), SettlementPostingKeys.CreatorPrefix(b1).Substring(18, 32));
    }

    [Fact]
    public void รับชำระที่ลงไว้ครั้งก่อน_ข้ามไม่ฟ้องยอดค้าง_แต่ครั้งแรกยอดค้างไม่พอต้องบล็อก()
    {
        var plan = SettlementBatchMath.Plan(Batch(948.48m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -51.52m) }, Channel(), true);
        var paid = new[] { new SettlementReceiptTarget(DocA, true, "TIV-0001", DocumentType.TaxInvoice, DocumentStatus.Paid, 0m, true) };
        Assert.True(SettlementPostingGate.Evaluate(plan, Ok(plan) with { ReceiptTargets = paid, PartialItems = 1 }).CanPost);

        var shortBal = new[] { new SettlementReceiptTarget(DocA, true, "TIV-0001", DocumentType.TaxInvoice, DocumentStatus.Approved, 500m, false) };
        var g = SettlementPostingGate.Evaluate(plan, Ok(plan) with { ReceiptTargets = shortBal });
        Assert.False(g.CanPost);
        Assert.Contains(g.Issues, i => i.Code == SettlementPlanIssueCode.ReceiptDocumentNotPayable && i.Message.Contains("500.00"));
        var notFound = SettlementPostingGate.Evaluate(plan, Ok(plan) with { ReceiptTargets = Array.Empty<SettlementReceiptTarget>() });
        Assert.Contains(notFound.Issues, i => i.Code == SettlementPlanIssueCode.ReceiptDocumentNotPayable && i.Blocking);
    }

    // ═════════════ ใบค่าธรรมเนียม: ห้ามหัก WHT ซ้ำ · ยอดเท่าที่ถูกหักจริง ═════════════

    [Fact]
    public void ใบค่าธรรมเนียม_W3_ไม่มีWHTบนใบ_50ทวิเท่ากับขา21917_จ่ายจากผังพักเต็มยอด()
    {
        var plan = SettlementBatchMath.Plan(Batch(930m),
            new[] { L(SettlementLineType.Sale, 2000m, DocA), L(SettlementLineType.Commission, -1070m) },
            Channel(wht: SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        Assert.True(plan.CanPost);
        var fee = Assert.Single(plan.FeeDocuments);
        Assert.Equal(30.93m, fee.WhtAmount);                                                  // 1,000 × 3/97
        var legs = plan.PayoutJournal.Where(l => l.AccountRole == SettlementAccountRoles.WhtPayable).ToList();
        Assert.Equal(30.93m, legs.Sum(l => l.Credit));

        var req = SettlementDocumentBuilder.FeeDocument(fee, new[] { Codes["53140"] }, Counterparty, Clearing, Day, "PO-001", "Shopee");
        Assert.Equal(DocumentType.PaymentVoucher, req.DocumentType);
        Assert.Equal(PaymentType.Cash, req.PaymentType);
        Assert.Equal(Clearing, req.PaymentAccountId);
        Assert.All(req.Lines, l => Assert.Equal(0m, l.WithholdingTaxRate));                  // ห้ามหักซ้ำตอนจ่าย
        Assert.Equal(1070m, DocTotal(req));                                                   // = ยอดที่แพลตฟอร์มหักจริง
        Assert.Equal(1000m, Assert.Single(req.Lines).UnitPrice);
        Assert.Equal(70m, req.Lines[0].VatAmountOverride);

        var cert = SettlementDocumentBuilder.WhtCertificate(fee, Counterparty, Guid.NewGuid(), Day, "PO-001");
        Assert.NotNull(cert);
        Assert.Equal(legs.Sum(l => l.Credit), cert!.Lines.Sum(l => l.TaxAmount));
        Assert.Equal(1030.93m, cert.Lines.Sum(l => l.IncomeAmount));                          // ออกภาษีแทน: เงินได้ = ฐาน + ภาษี
        Assert.Equal(WithholdingTaxCertType.PayAlways, cert.CertificateType);
        Assert.Equal(TaxType.WithholdingTax53, cert.TaxFormType);
    }

    [Fact]
    public void ใบค่าธรรมเนียม_ไม่หักWHTหรือแพลตฟอร์มเป็นตัวแทนหัก_ไม่ออก50ทวิของเรา()
    {
        foreach (var mode in new[] { SettlementFeeWhtMode.None, SettlementFeeWhtMode.AgentWithholds })
        {
            var plan = SettlementBatchMath.Plan(Batch(930m),
                new[] { L(SettlementLineType.Sale, 2000m, DocA), L(SettlementLineType.Commission, -1070m) }, Channel(wht: mode), true);
            var fee = Assert.Single(plan.FeeDocuments);
            Assert.Null(SettlementDocumentBuilder.WhtCertificate(fee, Counterparty, Guid.NewGuid(), Day, "PO-001"));
            Assert.DoesNotContain(plan.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.WhtPayable);
        }
    }

    [Fact]
    public void ใบค่าธรรมเนียม_VATแยกตามแผนตรงตัว_ไม่ให้คิด7เปอร์เซ็นต์ใหม่()
    {
        // 1.15 รวม VAT: แผน = ฐาน 1.07 + VAT 0.08 · ถ้าปล่อยให้ระบบคิด 7% ของฐานใหม่จะได้ 0.07 (ยอดใบ ≠ ยอดที่ถูกหัก — review198-A R-A11)
        var plan = SettlementBatchMath.Plan(Batch(998.85m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.PaymentFee, -1.15m) }, Channel(), true);
        var fee = Assert.Single(plan.FeeDocuments);
        var req = SettlementDocumentBuilder.FeeDocument(fee, new[] { Codes["53170"] }, Counterparty, Clearing, Day, "PO-001", "Shopee");
        Assert.Equal(0.08m, req.Lines[0].VatAmountOverride);
        Assert.Equal(fee.Deducted, DocTotal(req));
    }

    // ═════════════ R-A3 (review198-A): คืนค่าธรรมเนียมคนละประเภทในกลุ่มภาษีเดียวกัน ═════════════

    [Fact]
    public void RA3_ค่าคอมหัก1070_คืนค่าโฆษณา535_บล็อกรายบรรทัด_ไม่มีขา21917ที่ไม่ตรง50ทวิ()
    {
        var ads = L(SettlementLineType.AdsFee, 535m);
        var plan = SettlementBatchMath.Plan(Batch(1465m),
            new[] { L(SettlementLineType.Sale, 2000m, DocA), L(SettlementLineType.Commission, -1070m), ads },
            Channel(wht: SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        Assert.False(plan.CanPost);
        var issue = Assert.Single(plan.Issues, i => i.Code == SettlementPlanIssueCode.FeeGroupNetRefund);
        Assert.Contains(ads.Id, issue.LineIds);
        Assert.Empty(plan.FeeDocuments);
        Assert.DoesNotContain(plan.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.WhtPayable);
    }

    // ═════════════ R-A4/R-A5 (review198-A): ผู้ให้บริการต่างประเทศ ═════════════

    [Fact]
    public void RA4_ต่างประเทศ_บริษัทไม่จดVAT_ยังตั้งหนี้ภพ36_แต่VATเป็นต้นทุน()
    {
        var reg = SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, true, SettlementFeeWhtMode.None, "2");
        Assert.Equal(SettlementFeeVatTreatment.SelfAssessedPp36, reg.VatTreatment);
        Assert.Equal((450m, 31.50m, 31.50m), (reg.Expense, reg.InputVat, reg.Pp36Payable));

        var nonReg = SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, false, SettlementFeeWhtMode.None, "2");
        Assert.Equal(SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable, nonReg.VatTreatment);
        Assert.Equal(31.50m, nonReg.Pp36Payable);                  // §83/6: ผู้จ่ายนำส่งเสมอ
        Assert.Equal(0m, nonReg.InputVat);                         // แต่เคลมไม่ได้
        Assert.Equal(481.50m, nonReg.Expense);                     // ⇒ เป็นต้นทุน
        Assert.Equal(450m, nonReg.Deducted);                       // ยอดที่ถูกหักจาก wallet ไม่เปลี่ยน

        var plan = SettlementBatchMath.Plan(Batch(550m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -450m) },
            Channel(vat: SettlementFeeVatMode.ForeignPp36), false);
        var fee = Assert.Single(plan.FeeDocuments);
        Assert.Equal(SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable, fee.VatTreatment);
        var req = SettlementDocumentBuilder.FeeDocument(fee, new[] { Codes["53140"] }, Counterparty, Clearing, Day, "PO-001", "Agoda");
        Assert.True(req.IsForeignService);
        var line = Assert.Single(req.Lines);
        Assert.False(line.IsVatClaimable);
        Assert.Equal(450m, line.UnitPrice);
        Assert.Equal(31.50m, line.VatAmountOverride);
        // เส้น PV ต่างประเทศเดิม: Cr ผังพัก = ยอดรวม − VAT = ฐาน (ForeignServiceVat.SplitCredit) = ยอดที่ถูกหักจริง
        Assert.Equal(fee.Deducted, ForeignServiceVat.SplitCredit(true, DocTotal(req), 31.50m).PayeeCredit);

        var regPlan = SettlementBatchMath.Plan(Batch(550m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -450m) },
            Channel(vat: SettlementFeeVatMode.ForeignPp36), true);
        var regLine = Assert.Single(SettlementDocumentBuilder.FeeDocument(Assert.Single(regPlan.FeeDocuments), new[] { Codes["53140"] },
            Counterparty, Clearing, Day, "PO-001", "Agoda").Lines);
        Assert.True(regLine.IsVatClaimable);                       // จด VAT ⇒ 11640 รอเครดิตหลังนำส่ง
    }

    [Fact]
    public void RA5_ต่างประเทศ_ตั้งโหมดหักWHT_บล็อก_ไม่ลงภงด53อัตราในประเทศ()
    {
        var plan = SettlementBatchMath.Plan(Batch(550m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.AdsFee, -450m) },
            Channel(vat: SettlementFeeVatMode.ForeignPp36, wht: SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        Assert.False(plan.CanPost);
        Assert.Contains(plan.Issues, i => i.Code == SettlementPlanIssueCode.ForeignWhtNotSupported && i.Blocking && i.NextStep.Length > 0);
        Assert.DoesNotContain(plan.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.WhtPayable);
        Assert.Equal(0m, SettlementFeeTax.Compute(450m, null, SettlementFeeVatMode.ForeignPp36, true, true,
            SettlementFeeWhtMode.SelfWithholdPayerBorne, "8ad").WhtAmount);

        // ทิศตรงข้าม: ผู้ให้บริการไทย + โหมดเดียวกัน ⇒ ลงได้ มีขา 21917
        var thai = SettlementBatchMath.Plan(Batch(518.50m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.AdsFee, -481.50m) },
            Channel(wht: SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        Assert.True(thai.CanPost);
        Assert.Contains(thai.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.WhtPayable && l.Credit > 0m);
    }

    // ═════════════ R-A6 (review198-A): วันของใบสรุป = ปฏิทินไทย ═════════════

    [Fact]
    public void RA6_ขายตี3วันที่1ตุลาไทย_ใบสรุปลงวันที่1ตุลา_ไม่ใช่30กันยา()
    {
        var at0300Bkk = new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc);                // = 1 ต.ค. 03:00 +07
        var plan = SettlementBatchMath.Plan(Batch(1070m), new[] { L(SettlementLineType.Sale, 1070m, txn: at0300Bkk) }, Channel(), true);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), Assert.Single(plan.SummarySales).Date);

        // ทิศตรงข้าม: วันที่ที่ adapter เก็บเป็น "วันไทย 00:00 UTC" อยู่แล้ว ไม่ขยับ
        var midnight = SettlementBatchMath.Plan(Batch(1070m), new[] { L(SettlementLineType.Sale, 1070m, txn: Day) }, Channel(), true);
        Assert.Equal(Day, Assert.Single(midnight.SummarySales).Date);
    }

    // ═════════════ R-A8 (review198-A): สกุลเงินของช่องทาง ═════════════

    [Fact]
    public void RA8_ช่องทางUSD_รอบโอนค้างTHB_บล็อก_ช่องทางTHBลงได้()
    {
        var usd = Channel();
        usd.Currency = "USD";
        var p = SettlementBatchMath.Plan(Batch(948.48m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -51.52m) }, usd, true);
        Assert.False(p.CanPost);
        Assert.Contains(p.Issues, i => i.Code == SettlementPlanIssueCode.CurrencyNotSupported && i.Message.Contains("USD"));
        var thb = SettlementBatchMath.Plan(Batch(948.48m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -51.52m) }, Channel(), true);
        Assert.DoesNotContain(thb.Issues, i => i.Code == SettlementPlanIssueCode.CurrencyNotSupported);
    }

    // ═════════════ ใบขายสรุปรายวัน (DECISIONS ข้อ 2–3) ═════════════

    [Fact]
    public void ขายจับคู่ไม่ได้_ได้ใบขายสรุป_จดVAT_เป็นใบกำกับรับเงินเข้าผังพัก_ยอดเท่าแผน()
    {
        var plan = SettlementBatchMath.Plan(Batch(963m),
            new[] { L(SettlementLineType.Sale, 1070m, order: "SP-1"), L(SettlementLineType.SellerVoucher, -107m, order: "SP-1") },
            Channel(), true);
        Assert.True(plan.CanPost);
        var s = Assert.Single(plan.SummarySales);
        var req = SettlementDocumentBuilder.SummaryDocument(s, true, Guid.NewGuid(), Clearing, "PO-001", "Shopee");
        Assert.Equal(DocumentType.TaxInvoice, req.DocumentType);
        Assert.True(req.IssuedAsCashReceipt);
        Assert.Null(req.BuyerDeclinedTaxInvoice);                 // ห้ามประทับเจตนาผู้ซื้อแทนคน
        Assert.Equal(Clearing, req.PaymentAccountId);
        Assert.Equal(s.Date, req.DeliveryDate);
        Assert.Equal(963m, DocTotal(req));
        Assert.Equal(s.Vat, req.Lines[0].VatAmountOverride);
        Assert.Contains(SettlementDocumentBuilder.SummaryReviewTag, SettlementDocumentBuilder.SummaryReviewNote(s, "PO-001", "Shopee"));
        Assert.Contains("SP-1", SettlementDocumentBuilder.SummaryReviewNote(s, "PO-001", "Shopee"));

        var nonVat = SettlementBatchMath.Plan(Batch(963m),
            new[] { L(SettlementLineType.Sale, 1070m), L(SettlementLineType.SellerVoucher, -107m) }, Channel(), false);
        var nreq = SettlementDocumentBuilder.SummaryDocument(Assert.Single(nonVat.SummarySales), false, Guid.NewGuid(), Clearing, "PO-001", "Shopee");
        Assert.Equal(DocumentType.Receipt, nreq.DocumentType);    // ไม่จด VAT ห้ามออกใบกำกับ (§90/2)
        Assert.Equal(0m, nreq.Lines[0].VatRate);
        Assert.Equal(963m, DocTotal(nreq));
    }

    [Fact]
    public void RA7_ใบสรุปตกเดือนภาษีที่ยื่นแล้ว_บล็อก_ช้ากว่า3วันทำการแค่เตือน()
    {
        var plan = SettlementBatchMath.Plan(Batch(1070m), new[] { L(SettlementLineType.Sale, 1070m) }, Channel(), true);
        var filed = SettlementPostingGate.Evaluate(plan, Ok(plan) with { FiledVatPeriods = new HashSet<(int, int)> { (2026, 9) } });
        Assert.False(filed.CanPost);
        Assert.Contains(filed.Issues, i => i.Code == SettlementPlanIssueCode.TaxPeriodFiled && i.Blocking);

        var late = SettlementPostingGate.Evaluate(plan, Ok(plan) with { TodayBangkok = Day.AddDays(14) });
        Assert.True(late.CanPost);
        Assert.Contains(late.Issues, i => i.Code == SettlementPlanIssueCode.SummarySaleLate && !i.Blocking);

        var onTime = SettlementPostingGate.Evaluate(plan, Ok(plan) with { TodayBangkok = Day.AddDays(2) });   // อา. 20 → อ. 22 ก.ย.
        Assert.DoesNotContain(onTime.Issues, i => i.Code == SettlementPlanIssueCode.SummarySaleLate);
    }

    [Fact]
    public void RA7_มีเอกสารขายของวันนั้นหรือออเดอร์นั้นแล้ว_บล็อกรายได้ซ้ำ()
    {
        var sale = L(SettlementLineType.Sale, 1070m, order: "SP-9");
        var plan = SettlementBatchMath.Plan(Batch(1070m), new[] { sale }, Channel(), true);
        var dup = SettlementPostingGate.Evaluate(plan, Ok(plan) with
        {
            DuplicateSales = new[] { new SettlementDuplicateSale(new[] { sale.Id }, "ออเดอร์ SP-9 มีเอกสารขาย TIV-0009 ของตัวเองแล้ว") },
        });
        Assert.False(dup.CanPost);
        Assert.Contains(dup.Issues, i => i.Code == SettlementPlanIssueCode.SummarySaleDuplicate && i.LineIds.Contains(sale.Id));
        Assert.True(SettlementPostingGate.Evaluate(plan, Ok(plan)).CanPost);
    }

    // ═════════════ สัญญาทีม B: ใบสรุปเฉพาะบรรทัดที่ตัวจับคู่ยืนยันว่าไม่มีร่องรอย ═════════════

    [Fact]
    public void บรรทัดขายกำกวมหรือยอดไม่ตรง_บล็อก_ไม่ออกใบสรุปทับ_AutoSummaryออกได้()
    {
        var ambiguous = L(SettlementLineType.Sale, 1070m, order: "SP-2", match: SettlementMatchStatus.Unmatched);
        var p = SettlementBatchMath.Plan(Batch(1070m), new[] { ambiguous }, Channel(), true);
        Assert.False(p.CanPost);
        Assert.Empty(p.SummarySales);
        Assert.Contains(p.Issues, i => i.Code == SettlementPlanIssueCode.SaleUnmatched && i.Blocking && i.LineIds.Contains(ambiguous.Id));

        var off = L(SettlementLineType.Sale, 1000m, DocA, match: SettlementMatchStatus.AmountMismatch);
        var m = SettlementBatchMath.Plan(Batch(1000m), new[] { off }, Channel(), true);
        Assert.False(m.CanPost);
        Assert.Empty(m.Receipts);                                  // ไม่รับชำระยอดที่ยังไม่ยืนยัน
        Assert.Contains(m.Issues, i => i.Code == SettlementPlanIssueCode.SaleAmountMismatch && i.LineIds.Contains(off.Id));

        // ทิศตรงข้าม: ตัวจับคู่ยืนยันว่าไม่มีร่องรอย ⇒ ใบสรุป · จับคู่ได้ยอดตรง ⇒ รับชำระ
        Assert.Single(SettlementBatchMath.Plan(Batch(1070m),
            new[] { L(SettlementLineType.Sale, 1070m, match: SettlementMatchStatus.AutoSummary) }, Channel(), true).SummarySales);
        Assert.Single(SettlementBatchMath.Plan(Batch(1000m), new[] { L(SettlementLineType.Sale, 1000m, DocA) }, Channel(), true).Receipts);
    }

    // ═════════════ R-A1 (review198-A): บรรทัดที่นับว่าอยู่ในผังพักแล้ว ต้องอยู่ผังเดียวกันจริง ═════════════

    [Fact]
    public void RA1_PaymentIntentลงไว้11340_ช่องทางผูก11341_บล็อก_ผังเดียวกันลงได้()
    {
        var intent = Guid.NewGuid();
        var sale = L(SettlementLineType.Sale, 1070m, intent: intent);
        var plan = SettlementBatchMath.Plan(Batch(1028.21m), new[] { sale, L(SettlementLineType.PaymentFee, -41.79m) }, Channel(), true);
        Assert.Equal(1070m, plan.AlreadyInClearing);
        var gw11340 = Guid.NewGuid();
        var bad = SettlementPostingGate.Evaluate(plan, Ok(plan) with
        {
            ClearingSources = new[] { new SettlementClearingSource(new[] { sale.Id }, "รายการรับชำระออนไลน์", true, gw11340, false) },
        });
        Assert.False(bad.CanPost);
        Assert.Contains(bad.Issues, i => i.Code == SettlementPlanIssueCode.ClearingSourceMismatch && i.LineIds.Contains(sale.Id));

        var settled = SettlementPostingGate.Evaluate(plan, Ok(plan) with
        {
            ClearingSources = new[] { new SettlementClearingSource(new[] { sale.Id }, "รายการรับชำระออนไลน์", true, Clearing, true) },
        });
        Assert.False(settled.CanPost);                               // ถูกล้างด้วยรอบโอน gateway เดิมไปแล้ว

        var good = SettlementPostingGate.Evaluate(plan, Ok(plan) with
        {
            ClearingSources = new[] { new SettlementClearingSource(new[] { sale.Id }, "รายการรับชำระออนไลน์", true, Clearing, false) },
        });
        Assert.True(good.CanPost);
    }

    // ═════════════ ด่านอื่นของผู้ลงบัญชี ═════════════

    [Fact]
    public void ผู้ติดต่อแพลตฟอร์มไม่มีเลขภาษี_คืนเงินจับคู่แล้ว_ไม่มีสิทธิ์_งวดปิด_บล็อกพร้อมทางไปต่อ()
    {
        var plan = SettlementBatchMath.Plan(Batch(948.48m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -51.52m) }, Channel(), true);
        void Blocks(SettlementPostingFacts f, SettlementPlanIssueCode code)
        {
            var g = SettlementPostingGate.Evaluate(plan, f);
            Assert.False(g.CanPost);
            Assert.Contains(g.Issues, i => i.Code == code && i.Blocking && i.NextStep.Length > 0);
        }
        Blocks(Ok(plan) with { CounterpartyHasTaxId = false }, SettlementPlanIssueCode.CounterpartyMissing);
        Blocks(Ok(plan) with { CounterpartyFound = false }, SettlementPlanIssueCode.CounterpartyMissing);
        Blocks(Ok(plan) with { CanApproveFeeDocuments = false }, SettlementPlanIssueCode.PermissionDenied);
        Blocks(Ok(plan) with { PayoutPeriodClosed = "งวด ก.ย. ปิดแล้ว" }, SettlementPlanIssueCode.PeriodClosed);

        var refund = SettlementBatchMath.Plan(Batch(-107m),
            new[] { L(SettlementLineType.Refund, -107m, DocA) }, Channel(), true);
        Assert.Single(refund.Refunds);
        var rg = SettlementPostingGate.Evaluate(refund, Ok(refund));
        Assert.False(rg.CanPost);
        Assert.Contains(rg.Issues, i => i.Code == SettlementPlanIssueCode.RefundNeedsCreditNote && i.NextStep.Contains("ใบลดหนี้"));
    }

    [Fact]
    public void รับชำระใบขาย_เงินเข้าผังพัก_มีป้ายรอบโอน_ไม่ยุ่งWHT()
    {
        var batchId = Guid.NewGuid();
        var req = SettlementDocumentBuilder.ReceiptPayment(new SettlementReceiptPlan(DocA, 1000m, Array.Empty<Guid>()),
            batchId, Clearing, Day, "PO-001", "Shopee");
        Assert.Equal(DocA, req.DocumentId);
        Assert.Equal(1000m, req.Amount);
        Assert.Equal(Clearing, req.OverridePaymentAccountId);
        Assert.Contains(SettlementPostingKeys.PaymentMarker(batchId), req.Notes);
        Assert.Equal(0m, req.WithholdingTaxAmount);   // review198-C C-12 (รอบ 200): แพลตฟอร์มไม่หัก — ส่ง 0 ชัด ไม่ให้เส้นรับชำระคิด WHT ตามสัดส่วน
    }

    // ═════════════ จับคู่ธนาคาร: ต้องมีรายการเดินบัญชีจริง (R1) ═════════════

    private static SettlementBankTxnFacts Txn(decimal amount, BankTransactionType type = BankTransactionType.Deposit,
        ReconciliationStatus status = ReconciliationStatus.Unmatched, Guid? matchedJe = null, Guid? bank = null, Guid? linked = null,
        bool found = true)
        => new(found, bank ?? BankAcc, type, amount, status, matchedJe, linked);

    [Fact]
    public void จับคู่ธนาคาร_รายการจริงยอดเท่ากัน_ผ่าน_กดซ้ำไม่ทำซ้ำ()
    {
        var batch = Guid.NewGuid();
        var je = Guid.NewGuid();
        var txn = Guid.NewGuid();
        var ok = SettlementBankMatch.Check(SettlementBatchStatus.Posted, batch, 1028.21m, BankAcc, je, null, txn, Txn(1028.21m));
        Assert.True(ok.Ok);
        Assert.True(ok.NeedsReconcile);
        var already = SettlementBankMatch.Check(SettlementBatchStatus.Posted, batch, 1028.21m, BankAcc, je, null, txn,
            Txn(1028.21m, status: ReconciliationStatus.Matched, matchedJe: je));
        Assert.True(already.Ok);
        Assert.False(already.NeedsReconcile);
        var again = SettlementBankMatch.Check(SettlementBatchStatus.BankMatched, batch, 1028.21m, BankAcc, je, txn, txn, Txn(1028.21m));
        Assert.True(again.AlreadyDone);
        var outflow = SettlementBankMatch.Check(SettlementBatchStatus.Posted, batch, -50m, BankAcc, je, null, txn,
            Txn(50m, BankTransactionType.Withdrawal));
        Assert.True(outflow.Ok);
    }

    [Fact]
    public void จับคู่ธนาคาร_ไม่มีรายการจริง_ยอดไม่เท่า_ทิศผิด_คนละบัญชี_ถูกใช้แล้ว_ไม่ประทับเอง()
    {
        var batch = Guid.NewGuid();
        var je = Guid.NewGuid();
        var txn = Guid.NewGuid();
        SettlementBankMatchDecision C(SettlementBatchStatus st, decimal net, SettlementBankTxnFacts f)
            => SettlementBankMatch.Check(st, batch, net, BankAcc, je, null, txn, f);
        Assert.False(C(SettlementBatchStatus.Posted, 1028.21m, Txn(1028.21m, found: false)).Ok);
        Assert.False(C(SettlementBatchStatus.Posted, 1028.21m, Txn(1028.20m)).Ok);
        Assert.False(C(SettlementBatchStatus.Posted, 1028.21m, Txn(1028.21m, BankTransactionType.Withdrawal)).Ok);
        Assert.False(C(SettlementBatchStatus.Posted, 1028.21m, Txn(1028.21m, bank: Guid.NewGuid())).Ok);
        Assert.False(C(SettlementBatchStatus.Posted, 1028.21m, Txn(1028.21m, linked: Guid.NewGuid())).Ok);
        Assert.False(C(SettlementBatchStatus.Posted, 1028.21m, Txn(1028.21m, status: ReconciliationStatus.Matched, matchedJe: Guid.NewGuid())).Ok);
        Assert.False(C(SettlementBatchStatus.Posted, 1028.21m, Txn(1028.21m, status: ReconciliationStatus.Excluded)).Ok);
        Assert.False(C(SettlementBatchStatus.Matched, 1028.21m, Txn(1028.21m)).Ok);            // ยังไม่ลงบัญชี
        Assert.False(C(SettlementBatchStatus.Posted, 0m, Txn(0m)).Ok);                         // ไม่มีเงินเข้า
        Assert.False(SettlementBankMatch.Check(SettlementBatchStatus.BankMatched, batch, 1028.21m, BankAcc, je, Guid.NewGuid(), txn,
            Txn(1028.21m)).Ok);                                                                  // จับคู่กับรายการอื่นไว้แล้ว
    }
}

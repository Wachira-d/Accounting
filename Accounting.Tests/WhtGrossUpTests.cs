using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 204 (คำตัดสินข้อ 148 (4)) — ผู้จ่ายออกภาษีแทน: ตัวตั้งเดียว <see cref="WhtGrossUp"/> · ทุกเรื่องมีสองครึ่ง: เงื่อนไขใหม่คิดถูก และ
/// "หัก ณ ที่จ่าย" (ใบเดิมทุกใบ) ไม่ถูกแตะแม้แต่สตางค์เดียว · JE สมดุลผ่านด่าน <see cref="JournalPostingGuard"/> · คอลัมน์เงื่อนไข Col11 1/2/3 ·
/// บวกกลับ §65 ตรี (3) · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// </summary>
public class WhtGrossUpTests
{
    // ── สูตรต่อก้อน (ตัวอย่างตัวเลขตาม DECISIONS ข้อ 148) ──

    [Fact]
    public void Withhold_is_unchanged_base_times_rate()
    {
        var a = WhtGrossUp.OnBase(10_000m, 3m, WithholdingTaxCertType.Withhold);
        Assert.Equal(300m, a.Tax);
        Assert.Equal(0m, a.PayerBorne);
        Assert.Equal(10_000m, a.CertIncome);
        Assert.Equal(300m, a.PayeeBorne);
        // ยอดสุทธิ = สูตรเดิม ฐาน + VAT − ภาษี
        Assert.Equal(10_000m + 700m - 300m, WhtGrossUp.PayeeTotal(10_000m, 700m, a.Tax, a.PayerBorne));
    }

    [Fact]
    public void PayAlways_grosses_up_r_over_100_minus_r_and_payee_receives_full()
    {
        var a = WhtGrossUp.OnBase(10_000m, 3m, WithholdingTaxCertType.PayAlways);
        Assert.Equal(309.28m, a.Tax);              // 10,000 × 3/97
        Assert.Equal(309.28m, a.PayerBorne);
        Assert.Equal(10_309.28m, a.CertIncome);
        Assert.Equal(0m, a.PayeeBorne);
        Assert.Equal(10_700m, WhtGrossUp.PayeeTotal(10_000m, 700m, a.Tax, a.PayerBorne));
        // 50 ทวิ: ภาษี = เงินได้ × 3% (ปัด) — ตรวจย้อนได้บนแบบ
        Assert.Equal(a.Tax, Math.Round(a.CertIncome * 0.03m, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void PayOnce_adds_borne_tax_to_income_once()
    {
        var a = WhtGrossUp.OnBase(10_000m, 3m, WithholdingTaxCertType.PayOnce);
        Assert.Equal(300m, a.PayerBorne);           // ภาษีรอบแรกที่ผู้จ่ายออกให้
        Assert.Equal(10_300m, a.CertIncome);        // ม.39 ภาษีที่ออกแทนเป็นเงินได้
        Assert.Equal(309m, a.Tax);                  // 10,300 × 3%
        Assert.Equal(9m, a.PayeeBorne);             // ภาษีของภาษี — ผู้รับรับเอง
        Assert.Equal(9_991m, WhtGrossUp.PayeeTotal(10_000m, 0m, a.Tax, a.PayerBorne));
    }

    [Fact]
    public void Section70_foreign_15_percent_matches_settlement_formula()
    {
        // ตัวเลขเดียวกับ ForeignServiceVat.Pp36Base (คำตัดสินข้อ 40): 450 ออกภาษีแทน 15/85 = 79.41 ⇒ เงินได้ 529.41
        var a = WhtGrossUp.OnBase(450m, 15m, WithholdingTaxCertType.PayAlways);
        Assert.Equal(79.41m, a.Tax);
        Assert.Equal(529.41m, a.CertIncome);
        // สูตรรอบโอนเดิม (SettlementFeeTax W3) ต้องให้ผลเท่ากันทุกสตางค์
        var w3 = SettlementFeeTax.WhtOnBase(450m, 15m, SettlementFeeWhtMode.SelfWithholdPayerBorne);
        Assert.Equal((a.Tax, a.CertIncome, a.PayerBorne), (w3.Wht, w3.CertIncome, w3.Borne));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-3)]
    public void Invalid_rate_yields_no_tax(int rate)
    {
        foreach (var t in new[] { WithholdingTaxCertType.Withhold, WithholdingTaxCertType.PayAlways, WithholdingTaxCertType.PayOnce })
        {
            var a = WhtGrossUp.OnBase(1_000m, rate, t);
            Assert.Equal(0m, a.Tax);
            Assert.Equal(0m, a.PayerBorne);
            Assert.Equal(1_000m, a.CertIncome);
        }
    }

    // ── หลายบรรทัด: Σ บรรทัด = คิดจากฐานรวมของกลุ่มอัตรา ──

    [Theory]
    [InlineData(WithholdingTaxCertType.Withhold)]
    [InlineData(WithholdingTaxCertType.PayAlways)]
    [InlineData(WithholdingTaxCertType.PayOnce)]
    public void ForLines_sums_to_group_total_with_largest_line_absorbing_rounding(WithholdingTaxCertType type)
    {
        var lines = new List<(decimal, decimal)> { (33.33m, 3m), (33.33m, 3m), (33.34m, 3m), (500m, 0m), (1_000m, 5m) };
        var per = WhtGrossUp.ForLines(lines, type);
        var group3 = WhtGrossUp.OnBase(100m, 3m, type);
        Assert.Equal(group3.Tax, per[0].Tax + per[1].Tax + per[2].Tax);
        Assert.Equal(group3.PayerBorne, per[0].PayerBorne + per[1].PayerBorne + per[2].PayerBorne);
        Assert.Equal(new WhtGrossUpAmounts(0m, 0m, 500m), per[3]);       // บรรทัดไม่มี WHT ไม่ถูกแตะ
        Assert.Equal(WhtGrossUp.OnBase(1_000m, 5m, type), per[4]);
        // เงินได้รายบรรทัด = ฐาน + ส่วนที่ออกแทน เสมอ
        for (var i = 0; i < per.Count; i++)
            Assert.Equal(lines[i].Item1 + per[i].PayerBorne, per[i].CertIncome);
    }

    [Fact]
    public void ForLines_withhold_matches_legacy_round_of_group_base()
    {
        // ทิศตรงข้าม: ใบ "หัก ณ ที่จ่าย" ต้องได้ผลเดียวกับ ReconcileTaxRounding เดิม (round(Σ ฐาน × r) ต่อกลุ่ม)
        var lines = new List<(decimal, decimal)> { (1_234.56m, 3m), (789.01m, 3m) };
        var per = WhtGrossUp.ForLines(lines, WithholdingTaxCertType.Withhold);
        Assert.Equal(Math.Round((1_234.56m + 789.01m) * 0.03m, 2, MidpointRounding.AwayFromZero), per.Sum(p => p.Tax));
        Assert.All(per, p => Assert.Equal(0m, p.PayerBorne));
    }

    // ── Col11 ของไฟล์ ภ.ง.ด. ──

    [Theory]
    [InlineData(WithholdingTaxCertType.Withhold, 1)]
    [InlineData(WithholdingTaxCertType.PayAlways, 2)]
    [InlineData(WithholdingTaxCertType.PayOnce, 3)]
    [InlineData((WithholdingTaxCertType)0, 1)]
    [InlineData((WithholdingTaxCertType)9, 1)]
    public void FilingCondition_maps_1_2_3(WithholdingTaxCertType type, int expected)
        => Assert.Equal(expected, WhtGrossUp.FilingCondition(type));

    [Fact]
    public void Pnd_text_file_column11_carries_condition_3_for_pay_once()
    {
        var row = new PndTextFileFormat.Row("0105551234567", "00000", "บริษัท ผู้รับ จำกัด", true,
            new DateTime(2026, 10, 1), "6", 10_300m, 3m, 309m,
            Condition: WhtGrossUp.FilingCondition(WithholdingTaxCertType.PayOnce));
        var cols = PndTextFileFormat.DetailRow(1, row).Split('|');
        Assert.Equal("3", cols[10]);
        var withhold = PndTextFileFormat.DetailRow(1, row with { Condition = WhtGrossUp.FilingCondition(WithholdingTaxCertType.Withhold) }).Split('|');
        Assert.Equal("1", withhold[10]);
    }

    // ── ใบชนิดไหนตั้งได้ · เอกสารลูกสืบทอด ──

    [Fact]
    public void TypeError_only_for_receiving_side_documents()
    {
        Assert.NotNull(WhtGrossUp.TypeError(DocumentType.Invoice, WithholdingTaxCertType.PayAlways, false, false));
        Assert.NotNull(WhtGrossUp.TypeError(DocumentType.Receipt, WithholdingTaxCertType.PayOnce, false, false));
        Assert.Null(WhtGrossUp.TypeError(DocumentType.Invoice, WithholdingTaxCertType.Withhold, false, false));
        foreach (var t in new[] { DocumentType.PaymentVoucher, DocumentType.Expense, DocumentType.PurchaseInvoice, DocumentType.CertificateInLieu })
            Assert.Null(WhtGrossUp.TypeError(t, WithholdingTaxCertType.PayAlways, false, false));
    }

    // ── ฝ่ายค้าน P1-1: บริการต่างประเทศ (ภ.พ.36) + ผู้จ่ายออกแทน ⇒ ปฏิเสธ (VAT ประเมินขาด) ──

    [Fact]
    public void TypeError_rejects_payer_borne_on_foreign_service_with_way_forward()
    {
        foreach (var t in new[] { DocumentType.PaymentVoucher, DocumentType.Expense, DocumentType.PurchaseInvoice })
        foreach (var ct in new[] { WithholdingTaxCertType.PayAlways, WithholdingTaxCertType.PayOnce })
        {
            var err = WhtGrossUp.TypeError(t, ct, isForeignService: true, hasPayerSideSource: false);
            Assert.NotNull(err);
            Assert.Contains("ภ.พ.36", err);
            Assert.Contains("ทางไปต่อ", err);
        }
        // ตัวเลขในทางไปต่อ: ยอดบรรทัดรวมภาษีที่ออกแทน = ตกลงจ่ายสุทธิ / (1 − 15%) ⇒ หัก ณ ที่จ่ายแล้วผู้รับได้สุทธิเท่าเดิม · ฐาน ภ.พ.36 = Pp36Base
        var grossBase = 11_764.71m;
        var withhold = WhtGrossUp.OnBase(grossBase, 15m, WithholdingTaxCertType.Withhold);
        Assert.Equal(10_000m, grossBase - withhold.Tax);
        var alwaysOnNet = WhtGrossUp.OnBase(10_000m, 15m, WithholdingTaxCertType.PayAlways);
        Assert.Equal(grossBase, ForeignServiceVat.Pp36Base(10_000m, alwaysOnNet.PayerBorne));
    }

    [Fact]
    public void TypeError_foreign_service_withhold_untouched_opposite_direction()
    {
        // ใบต่างประเทศแบบหัก ณ ที่จ่าย (พฤติกรรมเดิมทุกใบ) ⇒ ผ่าน · ใบในประเทศ "ออกแทน" ⇒ ผ่าน
        Assert.Null(WhtGrossUp.TypeError(DocumentType.PaymentVoucher, WithholdingTaxCertType.Withhold, isForeignService: true, hasPayerSideSource: false));
        Assert.Null(WhtGrossUp.TypeError(DocumentType.PaymentVoucher, WithholdingTaxCertType.PayAlways, isForeignService: false, hasPayerSideSource: false));
    }

    // ── ฝ่ายค้าน P1-2: ใบลด/เพิ่มหนี้สืบทอดเงื่อนไขจากใบต้นทางฝั่งผู้จ่าย ──

    [Fact]
    public void TypeError_notes_allowed_only_with_payer_side_source()
    {
        foreach (var t in new[] { DocumentType.CreditNote, DocumentType.DebitNote })
        {
            Assert.True(WhtGrossUp.IsAdjustmentNote(t));
            Assert.Null(WhtGrossUp.TypeError(t, WithholdingTaxCertType.PayAlways, false, hasPayerSideSource: true));
            Assert.NotNull(WhtGrossUp.TypeError(t, WithholdingTaxCertType.PayOnce, false, hasPayerSideSource: false));
            Assert.Null(WhtGrossUp.TypeError(t, WithholdingTaxCertType.Withhold, false, hasPayerSideSource: false));
        }
        Assert.False(WhtGrossUp.IsAdjustmentNote(DocumentType.PaymentVoucher));
    }

    /// <summary>JE ใบตั้งหนี้ + ใบลดหนี้ (ทรงเดียวกับ AutoPostToJournalAsync: ใบลดหนี้ซื้อ Dr เจ้าหนี้ + Dr 2191x = Cr ค่าใช้จ่าย + Cr VAT + Cr ส่วนที่ออกแทน) —
    /// คืนยอดสุทธิต่อผัง (Dr − Cr)</summary>
    private static Dictionary<string, decimal> PiMinusCn(WithholdingTaxCertType type, decimal piBase, decimal cnBase, bool cnBorneLeg)
    {
        var net = new Dictionary<string, decimal>();
        void Post(string code, decimal dr, decimal cr) => net[code] = net.GetValueOrDefault(code) + dr - cr;
        var pi = WhtGrossUp.OnBase(piBase, 3m, type);
        var piVat = Math.Round(piBase * 0.07m, 2, MidpointRounding.AwayFromZero);
        Post("53110", piBase + pi.PayerBorne, 0m);
        Post("11610", piVat, 0m);
        Post("21220", 0m, WhtGrossUp.PayeeTotal(piBase, piVat, pi.Tax, pi.PayerBorne));
        Post("21917", 0m, pi.Tax);
        var cn = WhtGrossUp.OnBase(cnBase, 3m, type);   // ใบลดหนี้สืบทอดเงื่อนไข ⇒ สูตรเดียวกัน
        var cnVat = Math.Round(cnBase * 0.07m, 2, MidpointRounding.AwayFromZero);
        Post("21220", WhtGrossUp.PayeeTotal(cnBase, cnVat, cn.Tax, cn.PayerBorne), 0m);
        Post("21917", cn.Tax, 0m);
        Post("53110", 0m, cnBase + (cnBorneLeg ? cn.PayerBorne : 0m));
        Post("11610", 0m, cnVat);
        return net;
    }

    [Theory]
    [InlineData(WithholdingTaxCertType.PayAlways)]
    [InlineData(WithholdingTaxCertType.PayOnce)]
    [InlineData(WithholdingTaxCertType.Withhold)]
    public void CreditNote_on_payer_borne_source_balances_and_equals_smaller_invoice(WithholdingTaxCertType type)
    {
        var net = PiMinusCn(type, 10_000m, 1_000m, cnBorneLeg: true);
        Assert.Equal(0m, net.Values.Sum());   // ทั้งสองใบสมดุล
        // ใบตั้งหนี้ 10,000 − ใบลดหนี้ 1,000 ≡ ใบตั้งหนี้ 9,000 (เจ้าหนี้ · ภาษีนำส่ง · ค่าใช้จ่ายรวมส่วนที่ออกแทน) — ไม่มีอะไรค้าง
        var nine = WhtGrossUp.OnBase(9_000m, 3m, type);
        Assert.Equal(-nine.Tax, net["21917"]);
        Assert.Equal(-WhtGrossUp.PayeeTotal(9_000m, 630m, nine.Tax, nine.PayerBorne), net["21220"]);
        Assert.Equal(9_000m + nine.PayerBorne, net["53110"]);
    }

    [Fact]
    public void CreditNote_cert_reissue_cuts_income_including_borne_tax()
    {
        // 50 ทวิ ของใบตั้งหนี้ "ตลอดไป" 10,000 @3%: เงินได้ 10,309.28 · ภาษี 309.28 — ใบลดหนี้ 1,000 ลดเงินได้ = ฐาน + ส่วนที่ออกแทน (ม.39)
        // (WithholdingTaxCertService.AdjustCertsForCreditNoteAsync) ⇒ ใบใหม่ = 50 ทวิ ของใบ 9,000 เป๊ะ
        var pi = WhtGrossUp.OnBase(10_000m, 3m, WithholdingTaxCertType.PayAlways);
        var cn = WhtGrossUp.OnBase(1_000m, 3m, WithholdingTaxCertType.PayAlways);
        var d = WhtCertReissuePlan.ForCreditNote(
            new[] { new WhtCertAmountLine("8", 3m, pi.CertIncome, pi.Tax, "ค่าบริการ", new DateTime(2026, 9, 15), "2") },
            new[] { new WhtCertAmountLine("8", 3m, 1_000m + cn.PayerBorne, cn.Tax, "ลดหนี้", new DateTime(2026, 9, 20)) },
            null, false, 1, "WHT-202609-0001");
        Assert.Equal(WhtCertReissueAction.Reissue, d.Action);
        var l = Assert.Single(d.NewLines);
        var nine = WhtGrossUp.OnBase(9_000m, 3m, WithholdingTaxCertType.PayAlways);
        Assert.Equal(nine.CertIncome, l.IncomeAmount);
        Assert.Equal(nine.Tax, l.TaxAmount);
        Assert.Equal("2", l.Condition);   // เงื่อนไขออกให้ตลอดไปคงเดิม
    }

    [Fact]
    public void CreditNote_without_borne_reversal_is_unbalanced_negative_test()
    {
        // บั๊กเดิม P1-2 ทรงหนึ่ง: ใบลดหนี้คิดสูตรเดียวกันแต่ไม่กลับขาส่วนที่ออกแทน ⇒ JE ไม่สมดุลเท่าส่วนที่ออกแทน (30.93)
        var net = PiMinusCn(WithholdingTaxCertType.PayAlways, 10_000m, 1_000m, cnBorneLeg: false);
        Assert.Equal(WhtGrossUp.OnBase(1_000m, 3m, WithholdingTaxCertType.PayAlways).PayerBorne, net.Values.Sum());   // Dr เกิน Cr
    }

    [Fact]
    public void Inherit_payer_side_to_payer_side_only()
    {
        Assert.Equal(WithholdingTaxCertType.PayAlways,
            WhtGrossUp.Inherit(WithholdingTaxCertType.PayAlways, DocumentType.PurchaseInvoice, DocumentType.PaymentVoucher));
        Assert.Equal(WithholdingTaxCertType.PayOnce,
            WhtGrossUp.Inherit(WithholdingTaxCertType.PayOnce, DocumentType.Expense, DocumentType.Expense));
        Assert.Null(WhtGrossUp.Inherit(WithholdingTaxCertType.PayAlways, DocumentType.PurchaseInvoice, DocumentType.CreditNote));
        Assert.Null(WhtGrossUp.Inherit(WithholdingTaxCertType.Withhold, DocumentType.PurchaseInvoice, DocumentType.PaymentVoucher));
    }

    [Fact]
    public void SourceMismatch_blocks_child_with_other_condition()
    {
        Assert.Null(WhtGrossUp.SourceMismatchReason(WithholdingTaxCertType.PayAlways, WithholdingTaxCertType.PayAlways, "PI-1"));
        Assert.Null(WhtGrossUp.SourceMismatchReason((WithholdingTaxCertType)0, WithholdingTaxCertType.Withhold, "PI-1"));
        Assert.Contains("PI-1", WhtGrossUp.SourceMismatchReason(WithholdingTaxCertType.PayAlways, WithholdingTaxCertType.Withhold, "PI-1"));
    }

    [Fact]
    public void MismatchReason_fails_loud_when_stored_borne_differs_from_lines()
    {
        var per = WhtGrossUp.ForLines(new List<(decimal, decimal)> { (10_000m, 3m) }, WithholdingTaxCertType.PayAlways);
        Assert.Null(WhtGrossUp.MismatchReason("EXP-1", 309.28m, 309.28m, per));
        Assert.NotNull(WhtGrossUp.MismatchReason("EXP-1", 309.28m, 300m, per));
        // ซิงค์ภายนอกเขียนภาษีกลับเป็นแบบหัก ณ ที่จ่าย (300) ทั้งที่เอกสารตั้ง "ตลอดไป" ⇒ ต้องฟ้อง ไม่ลง JE ที่ไม่สมดุล
        Assert.NotNull(WhtGrossUp.MismatchReason("EXP-1", 300m, 309.28m, per));
    }

    // ── JE สมดุล (โครงเดียวกับ AutoPostToJournalAsync สายค่าใช้จ่าย accrual) ──

    private static List<JournalPostingGuard.LineFacts> ExpenseJe(WithholdingTaxCertType type, bool includeBorneLeg)
    {
        var a = WhtGrossUp.OnBase(10_000m, 3m, type);
        var total = WhtGrossUp.PayeeTotal(10_000m, 700m, a.Tax, a.PayerBorne);
        var je = new List<JournalPostingGuard.LineFacts>
        {
            new("53110", AccountType.Expense, 10_000m, 0m),
            new("11610", AccountType.Asset, 700m, 0m),
            new("21220", AccountType.Liability, 0m, total),
            new("21917", AccountType.Liability, 0m, a.Tax),
        };
        if (includeBorneLeg && a.PayerBorne > 0m)
            je.Add(new("53110", AccountType.Expense, a.PayerBorne, 0m));
        return je;
    }

    [Theory]
    [InlineData(WithholdingTaxCertType.Withhold)]
    [InlineData(WithholdingTaxCertType.PayAlways)]
    [InlineData(WithholdingTaxCertType.PayOnce)]
    public void Journal_with_borne_expense_leg_balances_and_passes_guard(WithholdingTaxCertType type)
    {
        var a = WhtGrossUp.OnBase(10_000m, 3m, type);
        var total = WhtGrossUp.PayeeTotal(10_000m, 700m, a.Tax, a.PayerBorne);
        var je = ExpenseJe(type, includeBorneLeg: true);
        Assert.Equal(je.Sum(l => l.Debit), je.Sum(l => l.Credit));
        var findings = JournalPostingGuard.Validate(je,
            new JournalPostingGuard.DocFacts(DocumentType.Expense, 10_000m, 700m, a.Tax, total));
        Assert.DoesNotContain(findings, f => f.IsError);
    }

    [Fact]
    public void Journal_without_borne_leg_is_unbalanced_negative_test()
    {
        var je = ExpenseJe(WithholdingTaxCertType.PayAlways, includeBorneLeg: false);
        var a = WhtGrossUp.OnBase(10_000m, 3m, WithholdingTaxCertType.PayAlways);
        var findings = JournalPostingGuard.Validate(je,
            new JournalPostingGuard.DocFacts(DocumentType.Expense, 10_000m, 700m, a.Tax, 10_700m));
        Assert.Contains(findings, f => f.RuleCode == "JE-BAL" && f.IsError);
    }

    [Fact]
    public void Cash_basis_payable_equals_payee_total_plus_tax()
    {
        // cash basis: ใบตั้งหนี้ตั้งเจ้าหนี้ = TotalAmount + WHT · ใบสำคัญจ่ายตัด = เงินออก (TotalAmount) + WHT ⇒ เจ้าหนี้ปิดพอดี
        var a = WhtGrossUp.OnBase(10_000m, 3m, WithholdingTaxCertType.PayAlways);
        var total = WhtGrossUp.PayeeTotal(10_000m, 700m, a.Tax, a.PayerBorne);
        Assert.Equal(10_000m + 700m + a.PayerBorne, total + a.Tax);
    }

    // ── §65 ตรี (3) บวกกลับส่วนที่ออกแทน ──

    private static readonly Guid Acc = Guid.NewGuid();

    private static Section65TerValidator.Result Eval65(WithholdingTaxCertType type, decimal borne, DocumentType docType = DocumentType.Expense,
        Guid? related = null)
    {
        var doc = new Document
        {
            DocumentType = docType,
            RelatedDocumentId = related,
            TotalAmount = 10_000m,
            WhtCertType = type,
            WhtPayerBorneAmount = borne,
            Lines = new List<DocumentLine> { new() { AccountId = Acc, Amount = 10_000m, Description = "ค่าบริการที่ปรึกษา" } },
        };
        return Section65TerValidator.Evaluate(doc,
            new Dictionary<Guid, (string Code, string Name)> { [Acc] = ("53110", "ค่าที่ปรึกษา") },
            "บริษัท ผู้รับ จำกัด", "0105551234567",
            new Section65TerValidator.Context(AnnualRevenue: 10_000_000m, PaidUpCapital: 1_000_000m));
    }

    [Fact]
    public void Section65ter3_adds_back_payer_borne_tax()
    {
        var r = Eval65(WithholdingTaxCertType.PayAlways, 309.28m);
        var f = Assert.Single(r.Findings, x => x.RuleCode == Section65TerValidator.PayerBorneWhtRuleCode);
        Assert.Equal(309.28m, f.AddBackAmount);
        Assert.True(f.NeedsConfirmation);
        Assert.False(f.HardBlock);
        Assert.Equal(309.28m, r.TotalAddBack);
        // ยกขึ้นให้ผู้อนุมัติเห็น + อ่านรหัสกลับได้
        var warn = Assert.Single(Section65TerApprovalWarnings.For(r));
        Assert.Equal(Section65TerValidator.PayerBorneWhtRuleCode, Section65TerApprovalWarnings.RuleCodeOf(warn));
    }

    [Fact]
    public void Section65ter3_untouched_for_withhold_and_settlement_voucher()
    {
        Assert.DoesNotContain(Eval65(WithholdingTaxCertType.Withhold, 0m).Findings,
            x => x.RuleCode == Section65TerValidator.PayerBorneWhtRuleCode);
        // ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทาง — ใบต้นทางบวกกลับไปแล้ว (ไม่ลงค่าใช้จ่ายซ้ำ)
        Assert.DoesNotContain(Eval65(WithholdingTaxCertType.PayAlways, 309.28m, DocumentType.PaymentVoucher, Guid.NewGuid()).Findings,
            x => x.RuleCode == Section65TerValidator.PayerBorneWhtRuleCode);
        Assert.Contains(Eval65(WithholdingTaxCertType.PayAlways, 309.28m, DocumentType.PaymentVoucher).Findings,
            x => x.RuleCode == Section65TerValidator.PayerBorneWhtRuleCode);
    }
}

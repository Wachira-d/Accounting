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
        Assert.NotNull(WhtGrossUp.TypeError(DocumentType.Invoice, WithholdingTaxCertType.PayAlways));
        Assert.NotNull(WhtGrossUp.TypeError(DocumentType.Receipt, WithholdingTaxCertType.PayOnce));
        Assert.Null(WhtGrossUp.TypeError(DocumentType.Invoice, WithholdingTaxCertType.Withhold));
        foreach (var t in new[] { DocumentType.PaymentVoucher, DocumentType.Expense, DocumentType.PurchaseInvoice, DocumentType.CertificateInLieu })
            Assert.Null(WhtGrossUp.TypeError(t, WithholdingTaxCertType.PayAlways));
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
        Assert.Null(WhtGrossUp.MismatchReason("EXP-1", 309.28m, per));
        Assert.NotNull(WhtGrossUp.MismatchReason("EXP-1", 300m, per));
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

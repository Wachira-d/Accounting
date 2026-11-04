using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 234 (คำตัดสิน 234-1…234-5) — ภาษีหัก ณ ที่จ่ายที่ผู้เช่าหักจากค่าบริการ NextAcc (<see cref="PlatformWhtSuggestion"/>) +
/// ด่านข้อมูลผู้ซื้อสำหรับใบกำกับ §86/4 (<see cref="PlatformPayerProfileGate"/>) · pure · สองทิศทุกด่าน
/// <para>เจ้าของ: "ถ้าลูกค้าและยอดเข้าเกณฑ์หัก ณ ที่จ่าย ระบบคำนวณให้อัตโนมัติรึยัง · กรณีลูกค้าสมัครใหม่หรือยังไม่ได้ตั้งค่าบริษัท มีอะไรเตือน"</para>
/// </summary>
public class PlatformWhtSuggestionRound234Tests
{
    private const string Juristic = "0105556000114";     // checksum ผ่าน · ขึ้นต้น 0
    private const string Individual = "3101000000013";   // checksum ผ่าน · บุคคลธรรมดา
    private const string Addr = "99 ถนนทดสอบ แขวงทดสอบ เขตทดสอบ กรุงเทพฯ 10110";

    private static PlatformPayerProfileGate.Result Payer(string? taxId, string? address = Addr, string? branch = "00000", string? name = "บริษัท ทดสอบ จำกัด")
        => PlatformPayerProfileGate.Evaluate(new PlatformPayerProfileGate.Facts(name, taxId, branch, address, null, null));

    // ───────── ตัวอย่างของเจ้าของ: Pro รายปี 15,198 (รวม VAT) ─────────
    [Fact]
    public void Pro_annual_15198_vat_inclusive_juristic_withholds_3_percent_of_base()
    {
        var b = PlatformWhtSuggestion.BaseOfReceived(15_198m, vatIncluded: true);
        Assert.Equal(14_203.74m, b);                               // ถอด 7/107 ก่อนคิด (ตัวแยกเดียวกับใบที่ออก)
        var r = PlatformWhtSuggestion.Decide(Payer(Juristic), 15_198m, b, tenantChoosesWithhold: true);
        Assert.Equal(nameof(PlatformWhtSuggestion.Outcome.Withhold), r.Outcome);
        Assert.True(r.Eligible); Assert.True(r.Withhold); Assert.True(r.DefaultChecked);
        Assert.Equal(3m, r.RatePercent);                           // ม.40(2) นิติบุคคล จาก ThaiWhtRateTable
        Assert.Equal(426.11m, r.WithholdingAmount);
        Assert.Equal(14_771.89m, r.NetAmount);                     // ยอดโอน/ตัดบัตร
        Assert.Equal(15_198m, PlatformWhtSuggestion.GrossOf(r.NetAmount, r.WithholdingAmount));   // ยอดเต็มกลับมาเท่าราคา (ด่านเทียบราคา/handler)
        Assert.Contains("426.11", r.Line);
        Assert.Contains("14,771.89", r.Line);
        Assert.Contains("50 ทวิ", r.Line);
    }

    [Fact]
    public void Not_vat_registered_platform_base_is_gross()
        => Assert.Equal(15_198m, PlatformWhtSuggestion.BaseOfReceived(15_198m, vatIncluded: false));

    // ───────── เกณฑ์ ฿1,000 (ท.ป.4/2528 ข้อ 12 · ThaiWhtRateTable.ShouldWithhold · ฝ่ายค้าน 234 B2: รวมทั้งสัญญา ≥ 1,000 ⇒ หักทุกงวด) ─────────
    [Fact]
    public void Threshold_1000_on_base_before_vat_both_directions_and_below_is_not_forbidden()
    {
        var at = PlatformWhtSuggestion.Decide(Payer(Juristic), 1_070m, PlatformWhtSuggestion.BaseOfReceived(1_070m, true), true);
        Assert.True(at.Withhold); Assert.True(at.DefaultChecked); Assert.True(at.MeetsThreshold);   // ฐาน 1,000.00 พอดี ⇒ เข้าเกณฑ์
        Assert.Equal(30m, at.WithholdingAmount);
        var b = PlatformWhtSuggestion.BaseOfReceived(1_069.99m, true);
        var below = PlatformWhtSuggestion.Decide(Payer(Juristic), 1_069.99m, b, tenantChoosesWithhold: false);
        Assert.Equal(nameof(PlatformWhtSuggestion.Outcome.BelowThreshold), below.Outcome);
        Assert.True(below.Eligible);                               // ยังแสดงช่องติ๊ก (ไม่ห้ามหัก — ตรงกับตัวตรวจ WithholdingProblem)
        Assert.False(below.DefaultChecked); Assert.False(below.Withhold);
        Assert.Equal(1_069.99m, below.NetAmount);                  // จ่ายเต็ม
        Assert.Contains("ต่ำกว่าเกณฑ์", below.Line);
        var tickedAnyway = PlatformWhtSuggestion.Decide(Payer(Juristic), 1_069.99m, b, tenantChoosesWithhold: true);
        Assert.True(tickedAnyway.Withhold);                        // ผู้เช่าเลือกหักเองได้
        Assert.Equal(30m, tickedAnyway.WithholdingAmount);
        Assert.Null(PlatformInvoiceRules.WithholdingProblem(tickedAnyway.WithholdingAmount, 1_069.99m, 1_069.99m, b, true));
    }

    [Fact]
    public void Starter_monthly_590_contract_per_year_meets_threshold_so_every_installment_defaults_to_withhold()
    {
        // ฝ่ายค้าน 234 B2: subscription ต่ออายุ = สัญญาต่อเนื่อง ⇒ มูลค่าสัญญา = 590 × 12 = 7,080 รวม VAT (ก่อน VAT 6,616.82) ≥ 1,000 ⇒ เข้าเกณฑ์ทุกงวด
        var contractGross = PlatformWhtSuggestion.AnnualContractGross(590m, 1);
        Assert.Equal(7_080m, contractGross);
        var contractBase = PlatformWhtSuggestion.BaseOfReceived(contractGross!.Value, true);
        var monthBase = PlatformWhtSuggestion.BaseOfReceived(590m, true);
        Assert.Equal(551.40m, monthBase);                          // งวดละต่ำกว่า 1,000
        var r = PlatformWhtSuggestion.Decide(Payer(Juristic), 590m, monthBase, true, contractBase);
        Assert.True(r.MeetsThreshold); Assert.True(r.DefaultChecked); Assert.True(r.Withhold);
        Assert.Equal(16.54m, r.WithholdingAmount);                 // 551.40 × 3% = 16.542 ⇒ 16.54
        Assert.Contains("หักทุกงวด", r.Line);
        // ทิศตรงข้าม: ไม่รู้ยอดสัญญา (เช่น ส่วนเสริมราคาครั้งเดียว 590) ⇒ ดูยอดครั้งนี้ ⇒ ไม่เข้าเกณฑ์ (ค่าตั้งต้นไม่ติ๊ก)
        var oneOff = PlatformWhtSuggestion.Decide(Payer(Juristic), 590m, monthBase, false);
        Assert.False(oneOff.MeetsThreshold); Assert.False(oneOff.DefaultChecked);
        // สัญญาต่อปีต่ำกว่า 1,000 (50 × 12 = 600) ⇒ ข้อความบอกมูลค่าสัญญาตามจริง ไม่แต่งความแน่นอน
        var tiny = PlatformWhtSuggestion.Decide(Payer(Juristic), 50m, PlatformWhtSuggestion.BaseOfReceived(50m, true), false,
            PlatformWhtSuggestion.BaseOfReceived(PlatformWhtSuggestion.AnnualContractGross(50m, 1)!.Value, true));
        Assert.False(tiny.DefaultChecked);
        Assert.Contains("มูลค่าสัญญาต่อปี", tiny.Line);
        Assert.Contains("ต่ำกว่าเกณฑ์ 1,000 บาท จึงไม่ต้องหัก", tiny.Line);
        Assert.Null(PlatformWhtSuggestion.AnnualContractGross(590m, 0));   // รอบไม่รู้จัก ⇒ ไม่รู้ (ไม่แต่ง)
    }

    [Fact]
    public void Individual_or_no_tax_id_never_withholds()
    {
        var person = PlatformWhtSuggestion.Decide(Payer(Individual), 15_198m, 14_203.74m, true);
        Assert.Equal(nameof(PlatformWhtSuggestion.Outcome.NotJuristic), person.Outcome);
        Assert.False(person.Eligible); Assert.Equal(0m, person.WithholdingAmount); Assert.Equal(15_198m, person.NetAmount);
        var signup = PlatformWhtSuggestion.Decide(Payer("-"), 15_198m, 14_203.74m, true);   // สมัครใหม่ (AuthService ตั้ง "-")
        Assert.Equal(nameof(PlatformWhtSuggestion.Outcome.NotJuristic), signup.Outcome);
        Assert.Equal(PlatformPayerProfileGate.NoTaxIdWarning, signup.Reason);
    }

    [Fact]
    public void Tenant_opt_out_pays_full_but_still_sees_the_offer()
    {
        var r = PlatformWhtSuggestion.Decide(Payer(Juristic), 15_198m, 14_203.74m, tenantChoosesWithhold: false);
        Assert.Equal(nameof(PlatformWhtSuggestion.Outcome.OptedOut), r.Outcome);
        Assert.True(r.Eligible); Assert.False(r.Withhold);
        Assert.Equal(426.11m, r.SuggestedAmount);                  // ช่องติ๊กยังแสดงยอดที่จะหัก
        Assert.Equal(0m, r.WithholdingAmount);
        Assert.Equal(15_198m, r.NetAmount);
    }

    [Fact]
    public void Rounding_is_away_from_zero_not_bankers()
    {
        // ฐาน 1,001.50 × 3% = 30.045 ⇒ 30.05 (banker's จะได้ 30.04)
        var r = PlatformWhtSuggestion.Decide(Payer(Juristic), 1_001.50m, 1_001.50m, true);
        Assert.Equal(30.05m, r.WithholdingAmount);
        Assert.Equal(971.45m, r.NetAmount);
        Assert.Equal(30.05m, PlatformWhtSuggestion.MaxWithholding(1_001.50m));
    }

    [Fact]
    public void Juristic_with_incomplete_profile_is_not_withheld_and_says_what_is_missing()
    {
        var r = PlatformWhtSuggestion.Decide(Payer(Juristic, address: null), 15_198m, 14_203.74m, true);
        Assert.Equal(nameof(PlatformWhtSuggestion.Outcome.ProfileIncomplete), r.Outcome);
        Assert.False(r.Eligible); Assert.False(r.Withhold);
        Assert.Equal(426.11m, r.SuggestedAmount);                  // บอกยอดที่จะหักได้เมื่อกรอกครบ
        Assert.Equal(0m, r.WithholdingAmount);
        Assert.NotNull(r.ProfileProblem);
        Assert.Contains("ที่อยู่", r.ProfileProblem);
    }

    [Fact]
    public void Nothing_due_is_not_eligible()
    {
        var r = PlatformWhtSuggestion.Decide(Payer(Juristic), 0m, 0m, true);
        Assert.Equal(nameof(PlatformWhtSuggestion.Outcome.NothingDue), r.Outcome);
        Assert.False(r.Eligible);
    }

    // ───────── ใบแจ้งหนี้: ยอดเสนอ = เพดานของตัวตรวจ WithholdingProblem (ไม่มีสูตรที่สอง) ─────────
    [Fact]
    public void Invoice_suggestion_equals_withholding_problem_cap()
    {
        // ใบ 1,070 (ก่อน VAT 1,000) ค้างเต็ม ⇒ เสนอ 30 · ตัวตรวจยอม 30 ไม่ยอม 30.01
        var b = PlatformWhtSuggestion.BaseOfInvoice(1_070m, 1_070m, 1_000m);
        var r = PlatformWhtSuggestion.Decide(Payer(Juristic), 1_070m, b, true);
        Assert.Equal(30m, r.WithholdingAmount);
        Assert.Null(PlatformInvoiceRules.WithholdingProblem(r.WithholdingAmount, 1_070m, 1_070m, 1_000m, true));
        Assert.NotNull(PlatformInvoiceRules.WithholdingProblem(r.WithholdingAmount + 0.01m, 1_070m, 1_070m, 1_000m, true));
        // ค้างครึ่งใบ ⇒ ฐาน 500 < 1,000 ⇒ ไม่เสนอ (ตัวตรวจยังรับยอดที่ผู้เช่ากรอกเองได้ตามกติกาเดิม — ไม่ถดถอย)
        var half = PlatformWhtSuggestion.Decide(Payer(Juristic), 535m, PlatformWhtSuggestion.BaseOfInvoice(535m, 1_070m, 1_000m), true);
        Assert.False(half.DefaultChecked);                         // ฐาน 500 ไม่เข้าเกณฑ์ ⇒ ไม่ติ๊กให้ (เลือกหักเองได้ 15 ตามกรอบตัวตรวจ)
        Assert.Equal(15m, half.SuggestedAmount);
        Assert.Null(PlatformInvoiceRules.WithholdingProblem(15m, 535m, 1_070m, 1_000m, true));
    }

    // ───────── ด่านข้อมูลผู้ซื้อ §86/4 ─────────
    [Fact]
    public void Profile_gate_complete_juristic()
    {
        var p = Payer(Juristic);
        Assert.True(p.IsJuristic); Assert.True(p.Complete); Assert.True(p.TaxIdValid);
        Assert.Empty(p.Missing);
        Assert.Null(p.Warning);
        Assert.Null(PlatformPayerProfileGate.WithholdingRefusal(p));
        Assert.Equal("Juristic", p.BuyerKind);
    }

    [Fact]
    public void Profile_gate_blank_branch_is_head_office_but_bad_branch_is_missing()
    {
        Assert.Equal("00000", Payer(Juristic, branch: null).BranchCode);
        Assert.True(Payer(Juristic, branch: null).Complete);
        var bad = Payer(Juristic, branch: "12");
        Assert.False(bad.Complete);
        Assert.Contains(bad.Missing, m => m.Contains("สาขา"));
        Assert.NotNull(PlatformPayerProfileGate.WithholdingRefusal(bad));
    }

    [Fact]
    public void Profile_gate_new_signup_without_tax_id_warns_but_does_not_block_individual_payment()
    {
        var p = Payer("-");
        Assert.Equal("Unknown", p.BuyerKind);
        Assert.False(p.IsJuristic); Assert.False(p.Complete);
        Assert.Equal(PlatformPayerProfileGate.NoTaxIdWarning, p.Warning);
        Assert.Null(PlatformPayerProfileGate.WithholdingRefusal(p));   // ไม่ใช่นิติบุคคล ⇒ ไม่มีเรื่องหักให้บล็อก (จ่ายเต็มได้)
        var person = Payer(Individual, address: null);
        Assert.False(person.IsJuristic);
        Assert.Null(PlatformPayerProfileGate.WithholdingRefusal(person));
    }

    [Fact]
    public void Profile_gate_bad_checksum_is_reported_not_treated_as_juristic()
    {
        var p = Payer("0105556000002");   // 13 หลักแต่ checksum ไม่ผ่าน
        Assert.False(p.TaxIdValid); Assert.False(p.IsJuristic);
        Assert.Contains(p.Missing, m => m.Contains("checksum"));
        Assert.Contains("checksum", p.Warning);
    }

    [Fact]
    public void Profile_gate_requires_street_address_same_as_full_tax_invoice()
    {
        // CI รอบ 234: เดิม "อำเภอ + จังหวัด" อย่างเดียวผ่านด่านก่อนชำระ แต่ใบกำกับเต็มรูป (§86/4) ตรวจข้อความที่อยู่ ⇒ ล้มตอนอนุมัติ — ตอนนี้นิยามเดียวกัน
        var districtOnly = PlatformPayerProfileGate.Evaluate(new PlatformPayerProfileGate.Facts("บริษัท ก จำกัด", Juristic, "00000", null, "เขตบางรัก", "กรุงเทพมหานคร"));
        Assert.False(districtOnly.Complete);
        Assert.Contains("ที่อยู่ตามทะเบียน", districtOnly.Missing);
        var blank = PlatformPayerProfileGate.Evaluate(new PlatformPayerProfileGate.Facts("บริษัท ก จำกัด", Juristic, "00000", " ", null, "กรุงเทพมหานคร"));
        Assert.False(blank.Complete);
        // ทิศตรงข้าม: มีข้อความที่อยู่ ⇒ ครบ (อำเภอ/จังหวัดไม่บังคับเพิ่ม)
        var street = PlatformPayerProfileGate.Evaluate(new PlatformPayerProfileGate.Facts("บริษัท ก จำกัด", Juristic, "00000", "1 ถนนสีลม", null, null));
        Assert.True(street.Complete);
    }

    // ───────── 234-W*: สถานะ 50 ทวิ จากลูกค้า (PlatformWhtCertificateStatus) ─────────
    [Fact]
    public void Wht_certificate_state_both_directions()
    {
        Assert.Equal(PlatformWhtCertificateStatus.State.NotRequired, PlatformWhtCertificateStatus.Of(0m, true, false, null, null));
        Assert.Equal(PlatformWhtCertificateStatus.State.NotRequired, PlatformWhtCertificateStatus.Of(30m, false, false, null, null));   // ยังไม่อนุมัติ
        Assert.Equal(PlatformWhtCertificateStatus.State.AwaitingUpload, PlatformWhtCertificateStatus.Of(30m, true, false, null, Accounting.Models.Enums.WhtCreditStatus.Pending));
        Assert.Equal(PlatformWhtCertificateStatus.State.AwaitingReview, PlatformWhtCertificateStatus.Of(30m, true, true, null, Accounting.Models.Enums.WhtCreditStatus.Pending));
        Assert.Equal(PlatformWhtCertificateStatus.State.Rejected, PlatformWhtCertificateStatus.Of(30m, true, false, "ชื่อไม่ตรง", null));
        // "ยืนยันแล้ว" มาจากทะเบียนเท่านั้น — มีไฟล์แต่ทะเบียนยัง Pending ≠ ยืนยัน
        Assert.Equal(PlatformWhtCertificateStatus.State.Confirmed, PlatformWhtCertificateStatus.Of(30m, true, true, null, Accounting.Models.Enums.WhtCreditStatus.Received));
        Assert.Equal(PlatformWhtCertificateStatus.State.Confirmed, PlatformWhtCertificateStatus.Of(30m, true, false, null, Accounting.Models.Enums.WhtCreditStatus.Claimed));
        Assert.False(PlatformWhtCertificateStatus.TenantCanUpload(PlatformWhtCertificateStatus.State.Confirmed));
        Assert.True(PlatformWhtCertificateStatus.TenantCanUpload(PlatformWhtCertificateStatus.State.Rejected));
    }

    [Fact]
    public void Wht_certificate_overdue_remind_confirm_reject_guards()
    {
        var now = new DateTime(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
        var s = PlatformWhtCertificateStatus.State.AwaitingUpload;
        Assert.True(PlatformWhtCertificateStatus.IsOverdue(s, now.AddDays(-31), now, 30));
        Assert.False(PlatformWhtCertificateStatus.IsOverdue(s, now.AddDays(-29), now, 30));
        Assert.False(PlatformWhtCertificateStatus.IsOverdue(PlatformWhtCertificateStatus.State.AwaitingReview, now.AddDays(-90), now, 30));   // ส่งแล้ว = รอเราตรวจ ไม่ใช่ลูกค้าค้าง
        Assert.Null(PlatformWhtCertificateStatus.RemindRefusal(s, null, now));
        Assert.NotNull(PlatformWhtCertificateStatus.RemindRefusal(s, now.AddHours(-2), now));
        Assert.NotNull(PlatformWhtCertificateStatus.RemindRefusal(PlatformWhtCertificateStatus.State.Confirmed, null, now));
        Assert.Null(PlatformWhtCertificateStatus.ConfirmRefusal(PlatformWhtCertificateStatus.State.AwaitingReview, "50T-1"));
        Assert.NotNull(PlatformWhtCertificateStatus.ConfirmRefusal(PlatformWhtCertificateStatus.State.AwaitingReview, " "));
        Assert.NotNull(PlatformWhtCertificateStatus.ConfirmRefusal(PlatformWhtCertificateStatus.State.AwaitingUpload, "50T-1"));
        Assert.NotNull(PlatformWhtCertificateStatus.ConfirmRefusal(PlatformWhtCertificateStatus.State.Confirmed, "50T-1"));
        Assert.Null(PlatformWhtCertificateStatus.RejectRefusal(PlatformWhtCertificateStatus.State.AwaitingReview, "ชื่อไม่ตรง"));
        Assert.NotNull(PlatformWhtCertificateStatus.RejectRefusal(PlatformWhtCertificateStatus.State.AwaitingReview, ""));
        Assert.NotNull(PlatformWhtCertificateStatus.RejectRefusal(PlatformWhtCertificateStatus.State.Confirmed, "x"));
    }

    // ───────── ฝ่ายค้าน 234 S2: ความยินยอมตัดบัตร — ฉบับเดิมตัดเต็มยอดต่อได้ · ตัดยอดสุทธิเฉพาะฉบับใหม่ ─────────
    [Fact]
    public void Saved_card_legacy_consent_still_charges_full_but_only_new_version_allows_net()
    {
        var v = PlatformSavedCardRules.Version("2.1");                       // autocharge-2/2.1
        // ฉบับข้อความรุ่นก่อน (ตัวช่วยภายใน LegacyVersionOf/MandateBase เป็น private — ทดสอบผ่านเมธอด public ที่ใช้มัน)
        var legacy = PlatformSavedCardRules.LegacyFullChargeTextVersion + "/2.1";   // autocharge-1/2.1
        Assert.Equal("autocharge-2/2.1", v);
        Assert.Null(PlatformSavedCardRules.AutoChargeBlockReason(true, true, false, legacy, v, null));   // ไม่หยุดตัดทุกบริษัทเพราะขึ้นฉบับ
        Assert.Null(PlatformSavedCardRules.AutoChargeBlockReason(true, true, false, legacy + PlatformSavedCardRules.WhtAckVersionSuffix, v, null, true, true));
        Assert.False(PlatformSavedCardRules.MandateAllowsNetCharge(legacy, v));
        Assert.True(PlatformSavedCardRules.MandateAllowsNetCharge(v + PlatformSavedCardRules.WhtAckVersionSuffix, v));
        Assert.Contains("ฉบับใหม่", PlatformSavedCardRules.AutoChargeBlockReason(true, true, false, "autocharge-1/2.0", v, null));   // นโยบายเปลี่ยน = ยังหยุด
        Assert.Contains("ยอดสุทธิ", PlatformSavedCardRules.Text("2.1"));
        Assert.Contains("50 ทวิ", PlatformSavedCardRules.WhtWarningText);
    }

    // ───────── ฝ่ายค้าน 234 N1: ยอดบนใบ/ทะเบียนต้องตรงยอดหัก ─────────
    [Fact]
    public void Wht_certificate_amount_mismatch()
    {
        Assert.Null(PlatformWhtCertificateStatus.AmountMismatch(426.11m, null));
        Assert.Null(PlatformWhtCertificateStatus.AmountMismatch(426.11m, 426.12m));
        Assert.NotNull(PlatformWhtCertificateStatus.AmountMismatch(426.11m, 852.22m));   // แถวรวมสองงวด
    }

    // ───────── ฝ่ายค้าน 234 N2: ประเภทผู้ซื้อมีผลจริง ─────────
    [Fact]
    public void Payer_profile_normalize_juristic_requires_juristic_number_and_address()
    {
        var ok = PlatformPayerProfileGate.Normalize(new("Juristic", "บริษัท ก จำกัด", Addr, "0-1055-56000-11-4", ""));
        Assert.Null(ok.Problem);
        Assert.Equal(Juristic, ok.Value!.TaxId);
        Assert.Equal("00000", ok.Value.BranchCode);
        Assert.Contains("บุคคลธรรมดา", PlatformPayerProfileGate.Normalize(new("Juristic", "บริษัท ก จำกัด", Addr, Individual, "00000")).Problem);
        Assert.NotNull(PlatformPayerProfileGate.Normalize(new("Juristic", "บริษัท ก จำกัด", null, Juristic, "00000")).Problem);   // ไม่มีที่อยู่
        Assert.NotNull(PlatformPayerProfileGate.Normalize(new("Juristic", "บริษัท ก จำกัด", Addr, "0105556000002", "00000")).Problem);   // checksum
        Assert.NotNull(PlatformPayerProfileGate.Normalize(new("Juristic", "บริษัท ก จำกัด", Addr, Juristic, "12")).Problem);
        Assert.NotNull(PlatformPayerProfileGate.Normalize(new(null, "ก", Addr, Juristic, null)).Problem);   // ไม่เลือกประเภท
    }

    [Fact]
    public void Payer_profile_normalize_individual_blank_clears_tax_id_and_juristic_number_is_refused()
    {
        var clear = PlatformPayerProfileGate.Normalize(new("Individual", "นาย ก", null, "", null));
        Assert.Null(clear.Problem);
        Assert.Equal("", clear.Value!.TaxId);                      // "" = ล้างเลขเดิม (กฎ #4 B)
        Assert.Equal(Individual, PlatformPayerProfileGate.Normalize(new("Individual", "นาย ก", null, Individual, null)).Value!.TaxId);
        Assert.Contains("นิติบุคคล", PlatformPayerProfileGate.Normalize(new("Individual", "นาย ก", null, Juristic, null)).Problem);
    }
}


using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รูปแบบกระดาษหลักฐานรับเงิน (รอบ 203 · คำถามเจ้าของ 2026-10-09) — <c>Helpers/ReceiptFormRule</c> ตัวเดียว
///
/// ล็อกทั้งสองทิศ (กฎเหล็ก #4 G7): ใบที่กฎหมายเหลือทางเดียวต้องเหลือทางเดียว · ใบที่มีหลายทางต้องให้ค่าตั้งเลือกได้ ·
/// <b>ไม่บล็อก</b> (คำตัดสินเจ้าของข้อ 140 · 2026-10-09): กระดาษที่จะเป็นเท็จ / นิติบุคคลได้แต่ใบย่อ / ตัวเลือกนอกชุด = คำเตือน [RCPT-FORM]
/// ที่เว็บต้องรับทราบ (409) และทางเข้าอัตโนมัติ/ที่พัก/จ่ายออนไลน์ผ่านพร้อมร่องรอย · ที่พัก+แขกบุคคล ค่าแนะนำ = อย่างย่อเมื่อมีสิทธิ์ ·
/// มัดจำ: ตัวตัดสิน<b>รับ</b>นโยบายมัดจำมาใช้ ไม่บังคับให้ใบมัดจำเป็นใบกำกับ
/// </summary>
public class ReceiptFormRuleTests
{
    private static ReceiptFormFacts Facts(
        bool vat = true, bool mayAbbrev = true, bool hasVat = true, DepositVatTreatment? deposit = null,
        bool juristic = false, bool complete = true, bool settles = false, bool refund = false,
        ReceiptFormChannel channel = ReceiptFormChannel.Document, bool hasTaxId = false)
        => new(vat, mayAbbrev, hasVat, deposit, juristic, complete, settles, refund, channel, hasTaxId);

    private static ReceiptFormDecision D(ReceiptFormFacts f, ReceiptForm? pref = null, ReceiptForm? choice = null)
        => ReceiptFormRule.Decide(f, pref, choice);

    // ── บริษัทไม่จด VAT ─────────────────────────────────────────────

    [Fact]
    public void Non_vat_company_gets_plain_receipt_only_and_ignores_preference()
    {
        var d = D(Facts(vat: false), pref: ReceiptForm.ReceiptTaxInvoiceFull);
        Assert.Equal(new[] { ReceiptForm.PlainReceipt }, d.Allowed);
        Assert.Equal(ReceiptForm.PlainReceipt, d.Chosen);
        Assert.False(d.HasWarnings);
        Assert.Equal(ReceiptFormRule.RuleNonVat, d.RuleCode);
        Assert.Contains("77/1", d.WhyNot[ReceiptForm.ReceiptTaxInvoiceFull]);
    }

    // ── ผู้จด VAT + แขกบุคคลธรรมดา ──────────────────────────────────

    [Fact]
    public void Vat_consumer_complete_retail_offers_full_and_abbreviated_default_full()
    {
        var d = D(Facts());
        Assert.Equal(new[] { ReceiptForm.ReceiptTaxInvoiceFull, ReceiptForm.ReceiptTaxInvoiceAbbreviated }, d.Allowed);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, d.Default);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, d.Chosen);   // ขายสด ไม่มีค่าตั้ง = พฤติกรรมเดิม (เต็มรูป)
        Assert.False(d.HasWarnings);
        // ใบเสร็จเปล่าไม่ใช่ตัวเลือก — และเหตุผลต้องอ้าง ม.86
        Assert.DoesNotContain(ReceiptForm.PlainReceipt, d.Allowed);
        Assert.Contains("ม.86", d.WhyNot[ReceiptForm.PlainReceipt]);
    }

    [Fact]
    public void Company_preference_picks_abbreviated_for_consumers_when_allowed()
    {
        var d = D(Facts(), pref: ReceiptForm.ReceiptTaxInvoiceAbbreviated);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceAbbreviated, d.Chosen);
        Assert.False(d.HasWarnings);
    }

    [Fact]
    public void Preference_outside_allowed_set_falls_back_to_default_not_through()
    {
        // ไม่ใช่กิจการขายปลีก ⇒ อย่างย่อไม่อยู่ในชุด — ค่าตั้งบริษัทปล่อยรูปแบบผิดกฎหมายผ่านไม่ได้
        var d = D(Facts(mayAbbrev: false), pref: ReceiptForm.ReceiptTaxInvoiceAbbreviated);
        Assert.Equal(new[] { ReceiptForm.ReceiptTaxInvoiceFull }, d.Allowed);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, d.Chosen);
        Assert.Contains("86/6", d.WhyNot[ReceiptForm.ReceiptTaxInvoiceAbbreviated]);
        // ค่าตั้ง "ใบเสร็จเปล่า" ก็ผ่านไม่ได้เช่นกัน
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, D(Facts(mayAbbrev: false), pref: ReceiptForm.PlainReceipt).Chosen);
    }

    [Fact]
    public void Consumer_without_address_in_retail_business_gets_abbreviated_only()
    {
        var d = D(Facts(complete: false));
        Assert.Equal(new[] { ReceiptForm.ReceiptTaxInvoiceAbbreviated }, d.Allowed);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceAbbreviated, d.Chosen);
        Assert.Contains("86/4(3)", d.WhyNot[ReceiptForm.ReceiptTaxInvoiceFull]);
    }

    [Fact]
    public void Consumer_without_address_in_non_retail_business_warns_with_both_fixes_and_does_not_block()
    {
        // เคสของเจ้าของ: รีสอร์ทจด VAT · แขกไม่มีที่อยู่ · ยังไม่ติ๊กขายปลีก ⇒ หัวพิมพ์ "ใบเสร็จรับเงิน" เหมือนเดิม + คำเตือนบอกทางแก้ 2 ทาง (ข้อ 140)
        var d = D(Facts(mayAbbrev: false, complete: false, channel: ReceiptFormChannel.Lodging));
        Assert.Empty(d.Allowed);
        Assert.Null(d.Default);
        Assert.Equal(ReceiptForm.PlainReceipt, d.Chosen);   // ไม่บล็อก — ใบออกได้ หัวลดเหมือนเดิม
        Assert.True(d.HasWarnings);
        var w = Assert.Single(d.Warnings);
        Assert.StartsWith(ReceiptFormRule.WarningPrefix, w);
        Assert.True(ReceiptFormRule.IsApprovalWarning(w));
        Assert.Equal(ReceiptFormRule.RuleVat, d.RuleCode);
        Assert.Contains("ที่อยู่", w);
        Assert.Contains("ประกอบกิจการขายปลีก", w);
        Assert.Contains("ม.86", w);
        Assert.Contains("ตรวจข้อมูล", w);   // บอกว่าใบจะไปโผล่ในรายงานให้ออกใบแทน
    }

    [Fact]
    public void Receipt_form_warning_stops_only_the_web_until_acknowledged_and_never_stops_automation()
    {
        // ข้อ 140 ข้อ 1+3: เว็บ (None) ⇒ 409 รอรับทราบ · รับทราบแล้ว (User) ⇒ ผ่าน · ที่พัก/จ่ายออนไลน์ (SystemWorkflow) · API · ใบประจำ (Unattended) ⇒ ผ่านพร้อมร่องรอย
        var w = D(Facts(mayAbbrev: false, complete: false, channel: ReceiptFormChannel.Lodging)).Warnings[0];
        Assert.Equal(new[] { w }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.None, new[] { w }));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.User, new[] { w }));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.SystemWorkflow, new[] { w }));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, new[] { w }));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, new[] { w }));
        // คำเตือนทั่วไปที่ไม่ใช่ชุดนี้ยังหยุดทางเข้าอัตโนมัติเหมือนเดิม (ทิศตรงข้าม)
        Assert.NotEmpty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, new[] { "วันที่เอกสารย้อนหลัง 100 วัน" }));
        // ร่องรอยใน audit อ้างมาตราของชุดนี้ และหมายเหตุระบบส่งผ่านยังเกิด (ไม่ใช่ "คนรับทราบ")
        Assert.Equal(ReceiptFormRule.WarningLegalReference, ApprovalAcknowledgement.LegalReference(new[] { w }));
        Assert.False(ApprovalAcknowledgement.AcknowledgedByPerson(ApprovalAckSource.SystemWorkflow));
        Assert.False(string.IsNullOrWhiteSpace(ApprovalAcknowledgement.Note(ApprovalAckSource.SystemWorkflow, new[] { w }, "payment-gateway", DateTime.UtcNow)));
    }

    // ── ผู้ซื้อที่ต้องการภาษีซื้อ ────────────────────────────────────

    [Fact]
    public void Juristic_buyer_forces_full_form_even_in_retail_business()
    {
        var d = D(Facts(juristic: true), pref: ReceiptForm.ReceiptTaxInvoiceAbbreviated);
        Assert.Equal(new[] { ReceiptForm.ReceiptTaxInvoiceFull }, d.Allowed);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, d.Chosen);
        Assert.Contains("82/5(2)", d.WhyNot[ReceiptForm.ReceiptTaxInvoiceAbbreviated]);
    }

    [Fact]
    public void Juristic_buyer_with_incomplete_data_gets_abbreviated_with_a_recommend_full_warning()
    {
        // ข้อ 140 (ค): นิติบุคคลที่เหลือแต่ใบย่อ ⇒ ออกให้ แต่เตือนแนะนำเต็มรูป (ไม่บล็อก)
        var d = D(Facts(juristic: true, complete: false));
        Assert.Equal(new[] { ReceiptForm.ReceiptTaxInvoiceAbbreviated }, d.Allowed);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceAbbreviated, d.Chosen);
        var w = Assert.Single(d.Warnings);
        Assert.Contains("82/5(2)", w);
        Assert.Contains("13 หลัก", d.WhyNot[ReceiptForm.ReceiptTaxInvoiceFull]);
        // ไม่มีสิทธิ์ขายปลีกด้วย ⇒ ไม่มีรูปแบบใดเลย ⇒ ใบเสร็จ + คำเตือน (ก)
        var none = D(Facts(juristic: true, complete: false, mayAbbrev: false));
        Assert.Equal(ReceiptForm.PlainReceipt, none.Chosen);
        Assert.Contains("13 หลัก", Assert.Single(none.Warnings));
    }

    // ── ใบเสร็จรับชำระของใบกำกับที่ออกแล้ว ─────────────────────────

    [Fact]
    public void Settlement_of_existing_tax_invoice_is_payment_evidence_only()
    {
        var d = D(Facts(settles: true), pref: ReceiptForm.ReceiptTaxInvoiceFull);
        Assert.Equal(new[] { ReceiptForm.PaymentEvidenceOnly }, d.Allowed);
        Assert.Equal(ReceiptForm.PaymentEvidenceOnly, d.Chosen);
        Assert.Equal(ReceiptFormCase.SettlementOfTaxInvoice, d.Case);
        Assert.Contains("VAT ซ้ำ", d.WhyNot[ReceiptForm.ReceiptTaxInvoiceFull]);
    }

    // ── ไม่มี VAT / ยกเว้น §81 ──────────────────────────────────────

    [Fact]
    public void Exempt_only_lines_allow_plain_receipt()
    {
        var d = D(Facts(hasVat: false));
        Assert.Equal(new[] { ReceiptForm.PlainReceipt }, d.Allowed);
        Assert.Equal(ReceiptFormRule.RuleNoVat, d.RuleCode);
        Assert.Contains("81", d.WhyNot[ReceiptForm.ReceiptTaxInvoiceFull]);
    }

    // ── มัดจำ: ตามนโยบายมัดจำ ไม่บังคับ ─────────────────────────────

    [Theory]
    [InlineData(DepositVatTreatment.FullDeposit, false)]
    [InlineData(DepositVatTreatment.VatPendingUndue, true)]
    public void Hotel_with_untaxed_deposit_policy_issues_plain_deposit_receipt(DepositVatTreatment treatment, bool hasVatLines)
    {
        var d = D(Facts(deposit: treatment, hasVat: hasVatLines, complete: false, channel: ReceiptFormChannel.Lodging),
            pref: ReceiptForm.ReceiptTaxInvoiceAbbreviated);
        Assert.Equal(ReceiptFormCase.Deposit, d.Case);
        Assert.Equal(new[] { ReceiptForm.PlainReceipt }, d.Allowed);
        Assert.Equal(ReceiptForm.PlainReceipt, d.Chosen);
        Assert.False(d.HasWarnings);   // มัดจำที่นโยบายยังไม่คิดภาษี = ใบเสร็จธรรมดาถูกต้อง ไม่มีอะไรต้องเตือน
        Assert.Equal(ReceiptFormRule.RuleDepositDeferred, d.RuleCode);
        Assert.Contains("วิธีบันทึกเงินมัดจำ", d.WhyNot[ReceiptForm.ReceiptTaxInvoiceFull]);
    }

    [Fact]
    public void Hotel_with_taxed_deposit_policy_treats_deposit_like_a_sale()
    {
        var d = D(Facts(deposit: DepositVatTreatment.VatImmediate, channel: ReceiptFormChannel.Lodging));
        Assert.Equal(ReceiptFormCase.Deposit, d.Case);
        Assert.Equal(new[] { ReceiptForm.ReceiptTaxInvoiceFull, ReceiptForm.ReceiptTaxInvoiceAbbreviated }, d.Allowed);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceAbbreviated, d.Chosen);   // ที่พัก+แขกบุคคล ค่าแนะนำ = อย่างย่อ (ข้อ 140 ข้อ 2)
        Assert.False(d.HasWarnings);
        // ทิศตรงข้ามของเคสเจ้าของ: มัดจำที่รับรู้ VAT ทันที + แขกไม่มีที่อยู่ + ไม่ใช่ขายปลีก ⇒ ใบเสร็จ + คำเตือน (ไม่บล็อก — จ่ายออนไลน์ต้องไม่ล้ม)
        var warned = D(Facts(deposit: DepositVatTreatment.VatImmediate, complete: false, mayAbbrev: false));
        Assert.Equal(ReceiptForm.PlainReceipt, warned.Chosen);
        Assert.True(warned.HasWarnings);
    }

    [Fact]
    public void Deposit_refund_is_payment_evidence_only()
    {
        var d = D(Facts(refund: true, deposit: DepositVatTreatment.VatImmediate, hasVat: false));
        Assert.Equal(ReceiptFormCase.DepositRefund, d.Case);
        Assert.Equal(new[] { ReceiptForm.PaymentEvidenceOnly }, d.Allowed);
        Assert.Contains("86/10", d.LegalReference);
    }

    // ── ตัวเลือกรายใบ ───────────────────────────────────────────────

    [Fact]
    public void Document_choice_inside_allowed_set_wins_over_company_preference()
    {
        var d = D(Facts(), pref: ReceiptForm.ReceiptTaxInvoiceAbbreviated, choice: ReceiptForm.ReceiptTaxInvoiceFull);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, d.Chosen);
    }

    [Fact]
    public void Document_choice_outside_allowed_set_falls_back_with_a_warning_not_silently_and_not_blocked()
    {
        // ข้อ 140 (ข): อย่างย่อโดยไม่มีสิทธิ์ = ไม่ใช้ · ระบบใช้เต็มรูปแทน + บอก
        var d = D(Facts(mayAbbrev: false), choice: ReceiptForm.ReceiptTaxInvoiceAbbreviated);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, d.Chosen);
        Assert.Equal(ReceiptFormRule.RuleChoice, d.RuleCode);
        var w = Assert.Single(d.Warnings);
        Assert.Contains("86/6", w);
        Assert.Contains(ReceiptFormRule.Label(ReceiptForm.ReceiptTaxInvoiceFull), w);   // บอกว่าใช้อะไรแทน
        // ใบเสร็จเปล่าที่มี VAT ก็เลือกเองไม่ได้ — ได้เต็มรูป + คำเตือน
        var plain = D(Facts(), choice: ReceiptForm.PlainReceipt);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, plain.Chosen);
        Assert.Contains("ม.86", Assert.Single(plain.Warnings));
    }

    [Fact]
    public void Lodging_consumer_default_is_abbreviated_when_entitled_else_full_when_complete()
    {
        // ข้อ 140 ข้อ 2 — โรงแรมที่ติ๊กขายปลีก: แขกบุคคลธรรมดาได้ใบย่อเป็นค่าแนะนำ (ค่าตั้ง/ตัวเลือกรายใบยังเลือกเต็มรูปได้)
        var hotel = D(Facts(channel: ReceiptFormChannel.Lodging));
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceAbbreviated, hotel.Default);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceAbbreviated, hotel.Chosen);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, D(Facts(channel: ReceiptFormChannel.Lodging), pref: ReceiptForm.ReceiptTaxInvoiceFull).Chosen);
        // ยังไม่ติ๊กขายปลีก + ข้อมูลครบ ⇒ เต็มรูป ไม่มีคำเตือน
        var full = D(Facts(channel: ReceiptFormChannel.Lodging, mayAbbrev: false));
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, full.Chosen);
        Assert.False(full.HasWarnings);
        // ในนามบริษัท (นิติบุคคล ข้อมูลครบ) ⇒ เต็มรูปเท่านั้น แม้ติ๊กขายปลีก
        var biz = D(Facts(channel: ReceiptFormChannel.Lodging, juristic: true), pref: ReceiptForm.ReceiptTaxInvoiceAbbreviated);
        Assert.Equal(new[] { ReceiptForm.ReceiptTaxInvoiceFull }, biz.Allowed);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, biz.Chosen);
        // ขายสดนอกที่พัก: ค่าแนะนำยังเป็นเต็มรูป (ทิศตรงข้าม — ไม่เปลี่ยนพฤติกรรม tenant อื่น)
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, D(Facts()).Default);
    }

    // ── กรณี (คีย์ค่าตั้ง) ──────────────────────────────────────────

    [Fact]
    public void Case_of_follows_channel_and_buyer_kind()
    {
        Assert.Equal(ReceiptFormCase.LodgingFinalConsumer, ReceiptFormRule.CaseOf(Facts(channel: ReceiptFormChannel.Lodging)));
        Assert.Equal(ReceiptFormCase.LodgingFinalBusiness, ReceiptFormRule.CaseOf(Facts(channel: ReceiptFormChannel.Lodging, juristic: true)));
        Assert.Equal(ReceiptFormCase.CashSale, ReceiptFormRule.CaseOf(Facts()));
        Assert.Equal(ReceiptFormCase.CashSale, ReceiptFormRule.CaseOf(Facts(juristic: true)));   // นิติบุคคลขายสด = เต็มรูปเสมอ ไม่ต้องมีกรณีแยก
        Assert.Equal(ReceiptFormCase.PosSlip, ReceiptFormRule.CaseOf(Facts(channel: ReceiptFormChannel.PosSlip)));
        Assert.Equal(ReceiptFormCase.Deposit, ReceiptFormRule.CaseOf(Facts(deposit: DepositVatTreatment.FullDeposit, channel: ReceiptFormChannel.Lodging)));
        Assert.Equal(ReceiptFormCase.SettlementOfTaxInvoice, ReceiptFormRule.CaseOf(Facts(settles: true, deposit: DepositVatTreatment.VatImmediate)));
    }

    [Fact]
    public void Channel_of_origin_module()
    {
        Assert.Equal(ReceiptFormChannel.Lodging, ReceiptFormRule.ChannelOf("Lodging"));
        Assert.Equal(ReceiptFormChannel.Lodging, ReceiptFormRule.ChannelOf(" lodging "));
        Assert.Equal(ReceiptFormChannel.Document, ReceiptFormRule.ChannelOf(null));
        Assert.Equal(ReceiptFormChannel.Document, ReceiptFormRule.ChannelOf("Integration"));
    }

    [Fact]
    public void Applies_to_receipts_always_and_tax_invoices_only_when_they_evidence_payment()
    {
        Assert.True(ReceiptFormRule.AppliesTo(DocumentType.Receipt, 0m, false));
        Assert.True(ReceiptFormRule.AppliesTo(DocumentType.ReceiptVoucher, 0m, false));
        Assert.True(ReceiptFormRule.AppliesTo(DocumentType.TaxInvoice, 7m, taxInvoiceIsPaymentEvidencing: true));
        // ฝ่ายค้านรอบ 203 ข้อ 2: ใบกำกับขายเชื่อ (ยังไม่รับเงิน · ใบรวมที่ยังไม่จ่าย) อยู่นอกขอบเขต — หัว/กติกาเดิมไม่ถูกแตะ
        Assert.False(ReceiptFormRule.AppliesTo(DocumentType.TaxInvoice, 7m, taxInvoiceIsPaymentEvidencing: false));
        Assert.False(ReceiptFormRule.AppliesTo(DocumentType.TaxInvoice, 0m, true));   // ใบกำกับ 0%/ยกเว้น เดินกติกาเดิม
        Assert.False(ReceiptFormRule.AppliesTo(DocumentType.Invoice, 7m, true));
        Assert.False(ReceiptFormRule.AppliesTo(DocumentType.CreditNote, 7m, true));
    }

    [Fact]
    public void Individual_with_tax_id_defaults_to_full_even_in_a_retail_hotel()
    {
        // ฝ่ายค้านรอบ 203 ข้อ 1b: บุคคลธรรมดาที่จด VAT (เลขไม่ขึ้นต้น 0 ⇒ ไม่ใช่นิติบุคคล) แจ้งเลขภาษี+ที่อยู่ ⇒ ต้องได้เต็มรูป ไม่งั้นเสียภาษีซื้อ §82/5(2)
        var withId = D(Facts(channel: ReceiptFormChannel.Lodging, hasTaxId: true), pref: ReceiptForm.ReceiptTaxInvoiceAbbreviated);
        Assert.Equal(ReceiptFormCase.LodgingFinalBusiness, withId.Case);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, withId.Default);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, withId.Chosen);   // ค่าตั้งบริษัท "อย่างย่อ" ไม่ทับ
        Assert.False(withId.HasWarnings);
        // อย่างย่อได้เฉพาะเลือกเองรายใบ — และต้องมีคำเตือน
        var explicitAbbrev = D(Facts(channel: ReceiptFormChannel.Lodging, hasTaxId: true), choice: ReceiptForm.ReceiptTaxInvoiceAbbreviated);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceAbbreviated, explicitAbbrev.Chosen);
        Assert.Contains("82/5(2)", Assert.Single(explicitAbbrev.Warnings));
        // ทิศตรงข้าม: แขกบุคคลธรรมดาไม่มีเลขภาษี ⇒ ค่าแนะนำอย่างย่อตามเดิม (ข้อ 140 ข้อ 2)
        var noId = D(Facts(channel: ReceiptFormChannel.Lodging));
        Assert.Equal(ReceiptFormCase.LodgingFinalConsumer, noId.Case);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceAbbreviated, noId.Chosen);
        Assert.False(noId.HasWarnings);
    }

    // ── matrix หน้าตั้งค่า ───────────────────────────────────────────

    [Fact]
    public void Matrix_for_vat_retail_company_marks_only_rows_with_real_choice_as_configurable()
    {
        var rows = ReceiptFormRule.Matrix(true, true, true, ReceiptFormPolicy.Empty).ToDictionary(r => r.Case);
        Assert.Equal(Enum.GetValues<ReceiptFormCase>().Length, rows.Count);
        Assert.True(rows["LodgingFinalConsumer"].Configurable);
        Assert.True(rows["CashSale"].Configurable);
        Assert.True(rows["Deposit"].Configurable);
        Assert.False(rows["LodgingFinalBusiness"].Configurable);
        Assert.False(rows["SettlementOfTaxInvoice"].Configurable);
        Assert.False(rows["DepositRefund"].Configurable);
        Assert.False(rows["PosSlip"].Configurable);
        Assert.Equal("ReceiptTaxInvoiceAbbreviated", rows["LodgingFinalConsumer"].Default);   // ข้อ 140 ข้อ 2
        Assert.Equal("ReceiptTaxInvoiceAbbreviated", rows["Deposit"].Default);
        Assert.Equal("ReceiptTaxInvoiceFull", rows["CashSale"].Default);
        Assert.Equal("ReceiptTaxInvoiceFull", rows["LodgingFinalBusiness"].Default);
        Assert.Equal("ReceiptTaxInvoiceAbbreviated", rows["PosSlip"].Default);   // สลิปไม่มีข้อมูลผู้ซื้อ
        // ตัวเลือกที่ไม่อนุญาตต้องมีเหตุผล (หน้าเว็บแสดงได้)
        var plain = rows["LodgingFinalConsumer"].Options.Single(o => o.Value == "PlainReceipt");
        Assert.False(plain.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(plain.WhyNot));
    }

    [Fact]
    public void Matrix_for_non_vat_company_has_nothing_to_configure()
    {
        var rows = ReceiptFormRule.Matrix(false, false, false, ReceiptFormPolicy.Empty);
        Assert.All(rows, r => Assert.False(r.Configurable));
        Assert.All(rows, r => Assert.Single(r.Options.Where(o => o.Allowed)));
    }

    [Fact]
    public void Matrix_reflects_stored_preference_and_slip_rule()
    {
        var policy = ReceiptFormPolicy.Parse("{\"LodgingFinalConsumer\":\"ReceiptTaxInvoiceAbbreviated\"}");
        var rows = ReceiptFormRule.Matrix(true, true, mayIssueAbbreviatedSlip: false, policy).ToDictionary(r => r.Case);
        Assert.Equal("ReceiptTaxInvoiceAbbreviated", rows["LodgingFinalConsumer"].Preference);
        Assert.Null(rows["CashSale"].Preference);
        // ขายปลีกแต่ไม่มี ภ.พ.06 ⇒ สลิปไม่มีรูปแบบที่ออกได้ (PosSlipHeader พิมพ์ใบเสร็จไปก่อน) — แถวบอกเหตุผล ภ.พ.06
        Assert.Empty(rows["PosSlip"].Options.Where(o => o.Allowed));
        Assert.Contains("ภ.พ.06", rows["PosSlip"].Options.Single(o => o.Value == "ReceiptTaxInvoiceAbbreviated").WhyNot);
    }

    // ── ค่าตั้ง JSON ─────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"Unknown\":\"ReceiptTaxInvoiceFull\"}")]
    [InlineData("{\"CashSale\":\"Garbage\"}")]
    [InlineData("{\"CashSale\":\"\"}")]
    public void Policy_parse_is_tolerant_and_never_throws(string? json)
    {
        var p = ReceiptFormPolicy.Parse(json);
        Assert.Empty(p.Preferences);
        Assert.Null(p.PreferenceFor(ReceiptFormCase.CashSale));
    }

    [Fact]
    public void Policy_normalize_rejects_unknown_keys_and_values_loudly()
    {
        Assert.Throws<BusinessRuleException>(() => ReceiptFormPolicy.Normalize("{\"Nope\":\"ReceiptTaxInvoiceFull\"}"));
        Assert.Throws<BusinessRuleException>(() => ReceiptFormPolicy.Normalize("{\"CashSale\":\"Nope\"}"));
        Assert.Throws<BusinessRuleException>(() => ReceiptFormPolicy.Normalize("{bad json"));
    }

    [Fact]
    public void Policy_normalize_round_trips_and_drops_empty_choices()
    {
        Assert.Null(ReceiptFormPolicy.Normalize(null));
        Assert.Null(ReceiptFormPolicy.Normalize(""));
        Assert.Null(ReceiptFormPolicy.Normalize("{\"CashSale\":\"\"}"));   // ว่าง = ใช้ค่าแนะนำ ⇒ ไม่เก็บอะไร
        var json = ReceiptFormPolicy.Normalize("{\"cashsale\":\"receipttaxinvoiceabbreviated\",\"LodgingFinalConsumer\":\"\",\"Deposit\":\"ReceiptTaxInvoiceFull\"}");
        Assert.Equal("{\"Deposit\":\"ReceiptTaxInvoiceFull\",\"CashSale\":\"ReceiptTaxInvoiceAbbreviated\"}", json);
        var p = ReceiptFormPolicy.Parse(json);
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceAbbreviated, p.PreferenceFor(ReceiptFormCase.CashSale));
        Assert.Equal(ReceiptForm.ReceiptTaxInvoiceFull, p.PreferenceFor(ReceiptFormCase.Deposit));
        Assert.Null(p.PreferenceFor(ReceiptFormCase.LodgingFinalConsumer));
    }
}

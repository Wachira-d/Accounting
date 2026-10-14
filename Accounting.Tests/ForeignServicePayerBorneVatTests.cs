using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 204 ทีม B (คำสั่งเจ้าของ 2026-10-13) — บริการต่างประเทศ (ภ.พ.36 §83/6) + ผู้จ่ายออกภาษีหัก ณ ที่จ่ายแทน: เดิม 400 ·
/// ตอนนี้ VAT คิดบนฐานรวมภาษีที่ออกแทน (<see cref="ForeignServiceVat.VatWithBorne"/> · คำตัดสินข้อ 40) · ทุกเรื่องมีสองทิศ:
/// ใบใหม่คิดถูก และใบเดิม (หัก ณ ที่จ่าย · ใบที่คีย์ยอด gross-up ในบรรทัดเอง · ใบในประเทศ) ไม่ถูกแตะ ·
/// จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py (_FSBORNE)
/// </summary>
public class ForeignServicePayerBorneVatTests
{
    private static decimal Vat7(decimal b) => Math.Round(b * 0.07m, 2, MidpointRounding.AwayFromZero);

    // ── สูตร VAT รายบรรทัด ──

    [Fact]
    public void VatWithBorne_450_at_15_pay_always_gives_37_06_on_base_529_41()
    {
        // ตัวเลขเดียวกับคำตัดสินข้อ 40: 450 ออกภาษีแทน ม.70 15/85 = 79.41 ⇒ ฐาน 529.41 ⇒ ภ.พ.36 37.06 (ไม่ใช่ 31.50)
        var borne = WhtGrossUp.OnBase(450m, 15m, WithholdingTaxCertType.PayAlways).PayerBorne;
        Assert.Equal(79.41m, borne);
        var vat = ForeignServiceVat.VatWithBorne(new[] { (450m, borne, 7m) });
        Assert.Equal(37.06m, Assert.Single(vat));
        Assert.Equal(ForeignServiceVat.SelfAssessedVatOn(ForeignServiceVat.Pp36Base(450m, borne)), vat[0]);
    }

    [Fact]
    public void VatWithBorne_pay_once_uses_first_round_borne()
    {
        // ครั้งเดียว 10,000 @3% ⇒ ออกแทน 300 ⇒ ฐาน 10,300 ⇒ 721.00
        var once = WhtGrossUp.OnBase(10_000m, 3m, WithholdingTaxCertType.PayOnce);
        Assert.Equal(300m, once.PayerBorne);
        Assert.Equal(721.00m, ForeignServiceVat.VatWithBorne(new[] { (10_000m, once.PayerBorne, 7m) })[0]);
    }

    [Fact]
    public void VatWithBorne_multi_line_reconciles_to_pp36_on_summed_base()
    {
        // สามบรรทัดที่ปัดรายบรรทัดแล้วเพี้ยน — Σ ต้องเท่ากับ SelfAssessedVatOn(Pp36Base(Σ ฐาน, Σ ออกแทน)) ทุกสตางค์
        var bases = new[] { 107.15m, 107.15m, 107.15m, 12.35m };
        var per = WhtGrossUp.ForLines(bases.Select(b => (b, 15m)).ToList(), WithholdingTaxCertType.PayAlways);
        var vat = ForeignServiceVat.VatWithBorne(bases.Select((b, i) => (b, per[i].PayerBorne, 7m)).ToList());
        var expected = ForeignServiceVat.SelfAssessedVatOn(ForeignServiceVat.Pp36Base(bases.Sum(), per.Sum(p => p.PayerBorne)));
        Assert.Equal(expected, vat.Sum());
        Assert.Equal(27.49m, expected);
        // ปัดรายบรรทัดได้ 27.48 ⇒ เศษ 0.01 เกลี่ยเข้าบรรทัดฐานใหญ่สุด (เสมอกัน ⇒ บรรทัดแรก) — บรรทัดอื่นคือการคิดรายบรรทัดปกติ
        Assert.Equal(27.48m, bases.Select((b, i) => Vat7(b + per[i].PayerBorne)).Sum());
        Assert.Equal(Vat7(bases[0] + per[0].PayerBorne) + 0.01m, vat[0]);
        Assert.Equal(Vat7(bases[1] + per[1].PayerBorne), vat[1]);
        Assert.Equal(Vat7(bases[3] + per[3].PayerBorne), vat[3]);
    }

    [Fact]
    public void VatWithBorne_zero_borne_and_exempt_lines_unchanged_opposite_direction()
    {
        // ไม่มีส่วนที่ออกแทน ⇒ เท่าการคิดจากฐานปกติ (ใบหัก ณ ที่จ่ายเดิม) · บรรทัดยกเว้น/0% ⇒ 0
        var vat = ForeignServiceVat.VatWithBorne(new[] { (10_000m, 0m, 7m), (500m, 100m, 0m), (200m, 50m, -1m) });
        Assert.Equal(new[] { 700m, 0m, 0m }, vat.ToArray());
        Assert.Empty(ForeignServiceVat.VatWithBorne(Array.Empty<(decimal, decimal, decimal)>()));
    }

    // ── JE สมดุล (ทรงเดียวกับ AutoPostToJournalAsync สาย PI/ค่าใช้จ่าย + ForeignServiceVat.SplitCredit) ──

    [Theory]
    [InlineData(WithholdingTaxCertType.PayAlways)]
    [InlineData(WithholdingTaxCertType.PayOnce)]
    [InlineData(WithholdingTaxCertType.Withhold)]
    public void Foreign_payer_borne_journal_balances_and_payee_gets_agreed_amount(WithholdingTaxCertType type)
    {
        const decimal baseAmt = 450m;
        var a = WhtGrossUp.OnBase(baseAmt, 15m, type);
        var vat = ForeignServiceVat.VatWithBorne(new[] { (baseAmt, a.PayerBorne, 7m) }).Sum();
        var total = WhtGrossUp.PayeeTotal(baseAmt, vat, a.Tax, a.PayerBorne);
        var split = ForeignServiceVat.SplitCredit(true, total, vat);
        // Dr ค่าใช้จ่าย(ฐาน) + Dr 11640 (VAT) + Dr ค่าใช้จ่าย (ออกแทน) = Cr เจ้าหนี้ (ฐาน − ภาษี + ออกแทน) + Cr 21912 (VAT) + Cr 2191x (ภาษี)
        var dr = baseAmt + vat + a.PayerBorne;
        var cr = split.PayeeCredit + split.Pp36Credit + a.Tax;
        Assert.Equal(dr, cr);
        Assert.Equal(vat, split.Pp36Credit);
        Assert.Equal(baseAmt - a.Tax + a.PayerBorne, split.PayeeCredit);
        // ยอดจ่ายผู้รับ (ตัวตั้งเดียว PayeeAmount) = ฐาน − ส่วนที่หักจากผู้รับ — ตลอดไป ⇒ ได้ 450 เต็ม
        Assert.Equal(baseAmt - a.PayeeBorne,
            ForeignServiceVat.PayeeAmount(DocumentType.PurchaseInvoice, true, total, vat));
        if (type == WithholdingTaxCertType.PayAlways) Assert.Equal(37.06m, vat);
        if (type == WithholdingTaxCertType.Withhold) Assert.Equal(31.50m, vat);   // ใบเดิม: ฐานไม่รวมอะไรเพิ่ม
    }

    // ── คำเตือนตอนอนุมัติ [PP36-BASE] (ผู้เรียก: ตัวรวบรวมคำเตือนก่อนอนุมัติของทีม A) ──

    private static Document ForeignDoc(WithholdingTaxCertType type, decimal vatAmount, DocumentType docType = DocumentType.PurchaseInvoice,
        bool foreign = true, Guid? related = null)
    {
        var a = WhtGrossUp.OnBase(450m, 15m, type);
        return new Document
        {
            DocumentType = docType,
            IsForeignService = foreign,
            RelatedDocumentId = related,
            WhtCertType = type,
            WhtPayerBorneAmount = a.PayerBorne,
            WithholdingTaxAmount = a.Tax,
            VatAmount = vatAmount,
            Lines = new List<DocumentLine>
            {
                new() { Amount = 450m, VatRate = 7m, VatAmount = vatAmount, WithholdingTaxRate = 15m, WithholdingTaxAmount = a.Tax, Description = "Booking.com commission" },
            },
        };
    }

    [Fact]
    public void Pp36BaseWarning_fires_when_recorded_vat_misses_borne_and_is_silent_when_correct()
    {
        var stale = ForeignDoc(WithholdingTaxCertType.PayAlways, 31.50m);
        var w = ForeignServiceVat.Pp36BaseApprovalWarning(stale);
        Assert.NotNull(w);
        Assert.StartsWith(ForeignServiceVat.Pp36BaseWarningPrefix, w);
        Assert.True(ForeignServiceVat.IsPp36BaseApprovalWarning(w!));
        Assert.Contains("37.06", w);
        Assert.Contains("529.41", w);
        Assert.Contains("31.50", w);
        Assert.Contains("คำตัดสินข้อ 40", w);
        // ใบที่ระบบคิดแล้ว (37.06) ⇒ เงียบ · เศษ 0.01 ⇒ เงียบ
        Assert.Null(ForeignServiceVat.Pp36BaseApprovalWarning(ForeignDoc(WithholdingTaxCertType.PayAlways, 37.06m)));
        Assert.Null(ForeignServiceVat.Pp36BaseApprovalWarning(ForeignDoc(WithholdingTaxCertType.PayAlways, 37.07m)));
    }

    [Fact]
    public void Pp36BaseWarning_never_fires_on_legacy_or_unrelated_documents_opposite_direction()
    {
        // หัก ณ ที่จ่าย (ใบเดิมทุกใบ) · ใบในประเทศที่ออกแทน · ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทาง (ไม่ได้ประเมิน ภ.พ.36 เอง) ⇒ ไม่มีคำเตือน
        Assert.Null(ForeignServiceVat.Pp36BaseApprovalWarning(ForeignDoc(WithholdingTaxCertType.Withhold, 31.50m)));
        Assert.Null(ForeignServiceVat.Pp36BaseApprovalWarning(ForeignDoc(WithholdingTaxCertType.PayAlways, 31.50m, foreign: false)));
        Assert.Null(ForeignServiceVat.Pp36BaseApprovalWarning(
            ForeignDoc(WithholdingTaxCertType.PayAlways, 31.50m, DocumentType.PaymentVoucher, related: Guid.NewGuid())));
        Assert.NotNull(ForeignServiceVat.Pp36BaseApprovalWarning(
            ForeignDoc(WithholdingTaxCertType.PayAlways, 31.50m, DocumentType.PaymentVoucher)));
    }

    // ── รายงาน ภ.พ.36: ไม่บวกส่วนที่ออกแทนซ้ำ ──

    [Fact]
    public void Pp36Report_new_document_uses_its_own_borne_once_not_twice()
    {
        // ใบใหม่: บรรทัด 450 · ออกแทน 79.41 · 50 ทวิ ตลอดไป เงินได้ 529.41 ภาษี 79.41 — ตัวตัดสิน 50 ทวิ ก็ตอบ 79.41
        var certBased = ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayAlways, 450m, 79.41m, 529.41m, 79.41m);
        Assert.Equal(79.41m, certBased);
        var borne = ForeignServiceVat.Pp36ReportBorne(79.41m, certBased);
        Assert.Equal(79.41m, borne);   // ไม่ใช่ 158.82
        var reportBase = ForeignServiceVat.Pp36Base(450m, borne);
        Assert.Equal(529.41m, reportBase);
        // VAT ในรายงาน = ยอดที่ GL ถือ (= doc.VatAmount จาก VatWithBorne) ⇒ อัตราแสดง 7.00%
        Assert.Equal(7.00m, Math.Round(37.06m / reportBase * 100m, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Pp36Report_legacy_documents_keep_cert_based_decision_opposite_direction()
    {
        // ใบเก่าที่คีย์ยอด gross-up ในบรรทัดเอง (หัก ณ ที่จ่าย 529.41 @15% = 79.41 · ออกแทน 0) ⇒ 0 เหมือนเดิม
        var grossLine = ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayAlways, 529.41m, 79.41m, 529.41m, 79.41m);
        Assert.Equal(0m, ForeignServiceVat.Pp36ReportBorne(0m, grossLine));
        // ใบค่าธรรมเนียมรอบโอน (บรรทัด 450 · ภาษีจ่ายนอกเอกสาร · 50 ทวิ 529.41/79.41) ⇒ 79.41 เหมือนเดิม
        var feeDoc = ForeignServiceVat.BorneTaxOutsideLines(WithholdingTaxCertType.PayAlways, 450m, 0m, 529.41m, 79.41m);
        Assert.Equal(79.41m, ForeignServiceVat.Pp36ReportBorne(0m, feeDoc));
        Assert.Equal(0m, ForeignServiceVat.Pp36ReportBorne(0m, 0m));
    }

    // ── แก้ธงบริการต่างประเทศย้อนหลัง (ReclassifyForeignServiceAsync) ──

    [Fact]
    public void Reclassify_refuses_payer_borne_documents_with_reissue_way_forward()
    {
        var to = ForeignServiceVat.ReclassifyPayerBorneRefusal(WithholdingTaxCertType.PayAlways, 79.41m, toForeignService: true);
        Assert.NotNull(to);
        Assert.Contains("ยกเลิกแล้วออกใหม่โดยติ๊กบริการต่างประเทศตั้งแต่สร้าง", to);
        Assert.Contains("ภ.พ.36 บนฐานรวมภาษีที่ออกแทน", to);
        var from = ForeignServiceVat.ReclassifyPayerBorneRefusal(WithholdingTaxCertType.PayOnce, 67.50m, toForeignService: false);
        Assert.NotNull(from);
        Assert.Contains("ยกเลิกแล้วออกใหม่", from);
        // ทิศตรงข้าม: ใบหัก ณ ที่จ่าย / ไม่มีส่วนที่ออกแทน ⇒ แก้ธงย้อนหลังได้ตามเดิม
        Assert.Null(ForeignServiceVat.ReclassifyPayerBorneRefusal(WithholdingTaxCertType.Withhold, 0m, toForeignService: true));
        Assert.Null(ForeignServiceVat.ReclassifyPayerBorneRefusal(WithholdingTaxCertType.PayAlways, 0m, toForeignService: true));
    }
}

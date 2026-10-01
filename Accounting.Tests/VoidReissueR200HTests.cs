using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม V1H — ลงมือตามคำตัดสินข้อ 50–54 (คำถามค้างทีม V1G · erp-review/2026-09-29/DECISIONS.md) ·
/// ทุกข้อมีสองครึ่ง: เคสที่พังกลับมาถูก + เคสที่ถูกอยู่แล้วไม่ถูกแตะ (F2 ข้อ 8) · ตัวเลขตัวอย่างเดียวกับทีม V1G (INV-1 1,000 + VAT 70 · เช็ค 10 ก.พ. ·
/// เงินสด 25 ก.พ.) · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py (ใบเดียว/หลายใบเรียกตัวถอยภาษีตัวเดียว · e-Tax VoidAsync เรียก
/// EtaxVoidPolicy · ทาง ค ตรวจสิทธิ์ใน service · รายงานข้อ 53 อ่านอย่างเดียว)
/// </summary>
public class VoidReissueR200HTests
{
    private static readonly DateTime Jan15 = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Feb10 = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Feb25 = new(2026, 2, 25, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mar05 = new(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid InvA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid InvB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid P2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Rc2 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ═════════════ ข้อ 50: ยกเลิกการชำระที่จัดสรรหลายใบ ถอยภาษีรายใบด้วยตัวตัดสินเดียวกับเส้นใบเดียว ═════════════

    [Fact]
    public void R200_V1H_ข้อ50_ใบเสร็จติดธงถือจุดความรับผิดเฉพาะใบที่มันอ้าง_ใบอื่นในการจัดสรรเดียวกันถอยภาษีได้()
    {
        // เช็คก้อนเดียวจ่าย INV-A + INV-B · ใบเสร็จ (VAT 70) อ้าง INV-A และถึงกรมสรรพากรแล้ว ⇒ เช็คเด้ง = ติดธง
        var bounce = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(EtaxStatus.Accepted, "RC-1", PaymentVoidCause.ChequeBounce);
        Assert.Equal(AutoReceiptEtaxAction.FlagEtaxCancellation, bounce.Action);
        var keepsA = DocumentVoidPreconditions.ReceiptHoldsTaxPointFor(InvA, InvA, bounce.Action, 70m);
        var keepsB = DocumentVoidPreconditions.ReceiptHoldsTaxPointFor(InvB, InvA, bounce.Action, 70m);
        Assert.True(keepsA);
        Assert.False(keepsB);
        // INV-A: ใบกำกับยังมีผล ⇒ ไม่ถอย · INV-B: ไม่มีใบกำกับของตัวเอง เงินทั้งหมดถูกกลับ ⇒ ถอย (เดิมเส้นหลายใบไม่ถอยเลยทั้งสองใบ)
        Assert.False(DocumentVoidPreconditions.ShouldUndoOutputVatReclass(0m, outputVatDue: true, liveVatReceiptKeepsTaxPoint: keepsA));
        Assert.True(DocumentVoidPreconditions.ShouldUndoOutputVatReclass(0m, outputVatDue: true, liveVatReceiptKeepsTaxPoint: keepsB));
    }

    [Theory]
    [InlineData(AutoReceiptEtaxAction.Void, 70)]                     // ใบเสร็จถูกยกเลิกพร้อมการชำระ — ไม่ถือแล้ว
    [InlineData(AutoReceiptEtaxAction.FlagEtaxCancellation, 0)]      // ใบรับเปล่า (ไม่ถือภาษี)
    public void R200_V1H_ข้อ50_ทิศตรงข้าม_ใบเสร็จที่ยกเลิกแล้วหรือไม่ถือภาษี_ไม่กันการถอยของใบที่มันอ้าง(AutoReceiptEtaxAction action, int vat)
    {
        Assert.False(DocumentVoidPreconditions.ReceiptHoldsTaxPointFor(InvA, InvA, action, vat));
        Assert.False(DocumentVoidPreconditions.ReceiptHoldsTaxPointFor(InvA, null, AutoReceiptEtaxAction.FlagEtaxCancellation, 70m));
        // ยังมีเงินรับอื่นเหลือบนใบ ⇒ ไม่ถอย (พฤติกรรมเดิมของเส้นใบเดียว · ใช้กับทุกใบในการจัดสรร)
        Assert.False(DocumentVoidPreconditions.ShouldUndoOutputVatReclass(500m, outputVatDue: true, liveVatReceiptKeepsTaxPoint: false));
        // ใบที่ภาษีขายยังพัก (ไม่เคยย้าย) ⇒ ไม่มีอะไรให้ถอย
        Assert.False(DocumentVoidPreconditions.ShouldUndoOutputVatReclass(0m, outputVatDue: false, liveVatReceiptKeepsTaxPoint: false));
    }

    [Theory]
    [InlineData(PaymentVoidCause.User, OutputVatUndoAction.Refuse)]
    [InlineData(PaymentVoidCause.ChequeBounce, OutputVatUndoAction.KeepAndFlag)]
    [InlineData(PaymentVoidCause.SettlementUnpost, OutputVatUndoAction.KeepAndFlag)]
    public void R200_V1H_ข้อ50_เส้นหลายใบเดือนที่ตั้งรายการปิด_ให้ผลเดียวกับเส้นใบเดียว(PaymentVoidCause cause, OutputVatUndoAction expect)
    {
        var d = DocumentVoidPreconditions.OutputVatUndoOnPaymentVoid("งวดบัญชี “ก.พ. 2569” ที่ปิดแล้ว (วันที่ 10/02/2026)", cause, "INV-B");
        Assert.Equal(expect, d.Action);
        Assert.Contains("INV-B", d.Message);
        // ทิศตรงข้าม: เดือนเปิด ⇒ ถอยได้ทุกทางเข้า
        Assert.Equal(OutputVatUndoAction.Undo, DocumentVoidPreconditions.OutputVatUndoOnPaymentVoid(null, cause, "INV-B").Action);
    }

    // ═════════════ ข้อ 51: e-Tax ที่ส่งแล้ว (Submitted) ยกเลิกในระบบได้เมื่อแนบหลักฐาน · ป้าย "ยกเลิกในระบบนี้" ═════════════

    [Fact]
    public void R200_V1H_ข้อ51_Submitted_ไม่มีไฟล์หลักฐานหรือเหตุผล_ปฏิเสธพร้อมทางไปต่อ_ไม่แตะอะไร()
    {
        var noFile = EtaxVoidPolicy.Decide(EtaxStatus.Submitted, "ลูกค้าขอยกเลิก", evidenceFileAttached: false);
        Assert.False(noFile.Allowed);
        Assert.Contains("แนบไฟล์หลักฐานการยกเลิก", noFile.Reason);
        Assert.Contains("ไม่ได้ส่งคำยกเลิกถึงกรมสรรพากรเอง", noFile.Reason);
        Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", noFile.Reason);
        var noReason = EtaxVoidPolicy.Decide(EtaxStatus.Submitted, "  ", evidenceFileAttached: true);
        Assert.False(noReason.Allowed);
        Assert.Contains("เหตุผล", noReason.Reason);
        var ok = EtaxVoidPolicy.Decide(EtaxStatus.Submitted, "ยกเลิกที่ผู้ให้บริการแล้ว", evidenceFileAttached: true);
        Assert.True(ok.Allowed);
        Assert.Contains("ผู้ใช้ยืนยัน", ok.EvidenceLabel);
        Assert.DoesNotContain("กรมสรรพากรยกเลิกแล้ว", ok.EvidenceLabel);
    }

    [Theory]
    [InlineData(EtaxStatus.Generated)]
    [InlineData(EtaxStatus.Signed)]
    [InlineData(EtaxStatus.Error)]
    [InlineData(EtaxStatus.Rejected)]
    public void R200_V1H_ข้อ51_ทิศตรงข้าม_ยังไม่ถึงกรมสรรพากร_ยกเลิกได้เหมือนเดิมไม่ต้องแนบ(EtaxStatus status)
    {
        var v = EtaxVoidPolicy.Decide(status, null, evidenceFileAttached: false);
        Assert.True(v.Allowed);
        Assert.Contains("ก่อนส่งถึงกรมสรรพากร", v.EvidenceLabel);
    }

    [Theory]
    [InlineData(EtaxStatus.Accepted, "ตอบรับแล้ว")]
    [InlineData(EtaxStatus.Voided, "ไปแล้ว")]
    public void R200_V1H_ข้อ51_ตอบรับแล้วหรือยกเลิกแล้ว_ปฏิเสธแม้แนบไฟล์(EtaxStatus status, string expect)
    {
        var v = EtaxVoidPolicy.Decide(status, "เหตุผล", evidenceFileAttached: true);
        Assert.False(v.Allowed);
        Assert.Contains(expect, v.Reason);
    }

    [Fact]
    public void R200_V1H_ข้อ51_ป้ายสถานะบอกยกเลิกในระบบนี้_ไม่ใช่กรมสรรพากรยกเลิก_แถวอื่นไม่มีป้าย()
    {
        var note = EtaxVoidPolicy.VoidedStatusNote(EtaxStatus.Voided, "ลูกค้าขอยกเลิก — หลักฐานแนบ");
        Assert.StartsWith(EtaxVoidPolicy.VoidedLabel, note);
        Assert.Contains("ไม่ใช่คำยืนยันจากกรมสรรพากร", note);
        Assert.Contains("ลูกค้าขอยกเลิก", note);
        Assert.Null(EtaxVoidPolicy.VoidedStatusNote(EtaxStatus.Submitted, "x"));
        Assert.Null(EtaxVoidPolicy.VoidedStatusNote(EtaxStatus.Accepted, null));
    }

    // ═════════════ ข้อ 52: ปิดธงเมื่อรับหลายงวด — ใบกำกับรายงวด §78/1 ยังออกอัตโนมัติไม่ได้ ⇒ ปฏิเสธพร้อมทางไปต่อ (📋 ส่วนออกใบรายงวด) ═════════════

    private static EtaxCancelFollowUpFacts Facts(bool fullyPaid, DocumentType type = DocumentType.Invoice, decimal vat = 70m,
        params EtaxCancelLivePayment[] payments)
        => new(type, vat, fullyPaid, Feb10, false, payments, null, null);

    private static EtaxCancelLivePayment Pay(DateTime date, bool allocations = false, Guid? id = null)
        => new(id ?? Guid.NewGuid(), date, allocations, null, null, 0m, false);

    [Fact]
    public void R200_V1H_ข้อ52_รับหลายงวด_บางส่วน_หรือรับรวมเอกสารอื่น_ปฏิเสธพร้อมทางไปต่อ_ไม่ปล่อยการขายไม่มีใบกำกับ()
    {
        var cases = new[]
        {
            Facts(true, payments: new[] { Pay(Feb25), Pay(Mar05) }),       // สองงวดครบยอด
            Facts(false, payments: new[] { Pay(Feb25) }),                   // งวดเดียวบางส่วน
            Facts(true, payments: new[] { Pay(Feb25, allocations: true) }), // เงินก้อนเดียวจัดสรรหลายใบ
        };
        foreach (var f in cases)
        {
            var plan = DocumentVoidPreconditions.EtaxCancellationFollowUp(f);
            Assert.False(plan.Allowed);
            Assert.Contains("§78/1", plan.Reason);
            Assert.Contains("ข้อ 52", plan.Reason);
            Assert.Contains("ใบกำกับเดิมยังใช้ได้", plan.Reason);            // ทางไปต่อ (ข้อ 54)
            Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", plan.Reason);
            Assert.False(plan.UndoReclass);
            Assert.Null(plan.ReclassAt);
        }
    }

    [Fact]
    public void R200_V1H_ข้อ52_ทิศตรงข้าม_รับครบงวดเดียว_ออกใบกำกับณวันรับเงินเหมือนเดิม_ใบไม่ถือVATหรือใบกำกับภาษีต้นทางไม่ถูกบล็อก()
    {
        var single = DocumentVoidPreconditions.EtaxCancellationFollowUp(Facts(true, payments: new[] { Pay(Feb25, id: P2) }));
        Assert.True(single.Allowed);
        Assert.Equal(P2, single.IssueVatReceiptForPaymentId);
        Assert.Equal(Feb25, single.ReclassAt);
        // ใบกำกับภาษีต้นทาง (ใบกำกับออกไปแล้วตอนขาย) / ใบไม่มี VAT ⇒ ไม่ต้องมีใบกำกับ ณ วันรับเงิน ⇒ ไม่เข้าเงื่อนไขข้อ 52
        Assert.True(DocumentVoidPreconditions.EtaxCancellationFollowUp(
            Facts(true, DocumentType.TaxInvoice, 70m, Pay(Feb25), Pay(Mar05))).Allowed);
        Assert.True(DocumentVoidPreconditions.EtaxCancellationFollowUp(Facts(true, DocumentType.Invoice, 0m, Pay(Feb25), Pay(Mar05))).Allowed);
        Assert.False(DocumentVoidPreconditions.InstallmentTaxInvoiceRequired(DocumentType.Invoice, 70m, singleShotTaxInvoiceIssuable: true));
        Assert.True(DocumentVoidPreconditions.InstallmentTaxInvoiceRequired(DocumentType.Invoice, 70m, singleShotTaxInvoiceIssuable: false));
        // ใบเสร็จถือ VAT อื่นยังมีผล ⇒ ไม่ต้องทำอะไร (ก่อนถึงด่านข้อ 52)
        var other = DocumentVoidPreconditions.EtaxCancellationFollowUp(
            Facts(true, payments: new[] { Pay(Feb25), Pay(Mar05) }) with { OtherLiveVatReceipt = true });
        Assert.True(other.Allowed);
        Assert.False(other.UndoReclass);
    }

    // ═════════════ ข้อ 53: ตัวกลับภาษีขายที่ลงคนละเดือนกับ JE ย้ายภาษี ═════════════

    [Fact]
    public void R200_V1H_ข้อ53_ตัวกลับลงวันที่ใบแจ้งหนี้คนละเดือนกับJEย้ายภาษี_ถูกจำแนก_ทิศตรงข้ามเดือนเดียวกันไม่จำแนก()
    {
        Assert.True(EtaxReissueReview.ReclassReversalMisdated(Feb10, Jan15));            // รุ่นก่อนข้อ 48: ลงวันที่ใบแจ้งหนี้ ม.ค.
        Assert.True(EtaxReissueReview.ReclassReversalMisdated(Feb10, Mar05));            // ลงวันที่ยกเลิก (เดือนถัดไป)
        Assert.True(EtaxReissueReview.ReclassReversalMisdated(new DateTime(2026, 1, 5), new DateTime(2027, 1, 5)));   // ปีต่าง เดือนเดียวกัน
        Assert.False(EtaxReissueReview.ReclassReversalMisdated(Feb10, Feb10));           // ตัวกลับรุ่นข้อ 48: วันที่ของ JE ย้ายภาษีเอง
        Assert.False(EtaxReissueReview.ReclassReversalMisdated(Feb10, Feb25));
    }

    // ═════════════ ข้อ 54: ทาง (ค) "ใบกำกับเดิมยังใช้ได้" ═════════════

    private static EtaxCancellationClaim Keep(EtaxStatus? etax = EtaxStatus.Accepted, decimal coverage = 1070m, bool otherVat = false,
        bool vatUndone = false, string? reason = "ลูกค้าชำระเงินสดแทนเช็คที่เด้ง", bool anyVoided = false)
        => new(true, etax, anyVoided, EtaxCancellationPath.OriginalStillValid, reason, null, false, null, 70m, coverage,
            ReceiptTotalAmount: 1070m, LivePaymentCoverage: coverage, OtherLiveVatReceipt: otherVat, SourceVatUndone: vatUndone);

    [Fact]
    public void R200_V1H_ข้อ54_ลำดับ_เช็คเด้ง_รับเงินสดครบ_ปิดธงทางค_ใบเสร็จเดิมคงมีผล_ไม่ถอยภาษี()
    {
        // 1) เช็คเด้ง — RC-1 (VAT 70) ถึงกรมสรรพากรแล้ว ⇒ ติดธง · ข้อความธงชี้ทาง (ค) ด้วย
        var bounce = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(EtaxStatus.Accepted, "RC-1", PaymentVoidCause.ChequeBounce);
        Assert.Contains("ใบกำกับเดิมยังใช้ได้", bounce.Message);
        Assert.False(DocumentVoidPreconditions.ShouldUndoOutputVatReclass(0m, true,
            DocumentVoidPreconditions.FlaggedReceiptKeepsTaxPoint(bounce.Action, 70m)));
        // 2) รับเงินสดใหม่ 1,070 — RC-1 ยังถือ VAT ⇒ ใบรับใหม่ไม่ใช่ใบกำกับ
        Assert.False(SettlementReceiptPolicy.CarriesTaxInvoiceRole(DocumentType.Invoice, 70m, singleShotFull: true, liveVatReceiptExists: true));
        // 3) ปิดธงทาง (ค)
        var v = DocumentVoidPreconditions.EtaxCancellationResolution(Keep());
        Assert.True(v.Allowed);
        Assert.Equal(EtaxCancellationEvidence.OriginalInvoiceStillValid, v.Evidence);
        var label = DocumentVoidPreconditions.EvidenceLabel(v.Evidence);
        Assert.Contains("ภาษีขายไม่ถูกถอย", label);
        Assert.Contains("ไม่ใช่ใบกำกับ", label);
    }

    [Theory]
    [InlineData("short", "ยังไม่ครอบยอดใบเสร็จ")]
    [InlineData("othervat", "ใบกำกับสองใบ")]
    [InlineData("undone", "รายงานตรวจข้อ 44")]
    [InlineData("notreached", "ไม่มีใบกำกับที่มีผล")]
    [InlineData("voidedhere", "ไม่มีใบกำกับที่มีผล")]
    [InlineData("submitted", "Submitted")]
    [InlineData("noreason", "เหตุผล")]
    public void R200_V1H_ข้อ54_ทางค_เงื่อนไขไม่ครบ_ปฏิเสธพร้อมทางไปต่อ_ไม่แตะอะไร(string kind, string expect)
    {
        var c = kind switch
        {
            "short" => Keep(coverage: 500m),
            "othervat" => Keep(otherVat: true),
            "undone" => Keep(vatUndone: true),
            "notreached" => Keep(etax: null),
            "voidedhere" => Keep(etax: null, anyVoided: true),
            "submitted" => Keep(etax: EtaxStatus.Submitted),
            _ => Keep(reason: " "),
        };
        var v = DocumentVoidPreconditions.EtaxCancellationResolution(c);
        Assert.False(v.Allowed);
        Assert.Contains(expect, v.Reason);
        Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", v.Reason);
        Assert.Equal(EtaxCancellationEvidence.None, v.Evidence);
    }

    [Fact]
    public void R200_V1H_ข้อ54_ทิศตรงข้าม_ทางกและขไม่ถูกแตะ_ใบลดหนี้ที่ยังมีเงินรับชี้ทางค()
    {
        // (ก) ยังต้องมีไฟล์หลักฐานเมื่อใช้เลขอ้างอิง (ข้อ 46 เดิม) — ช่องใหม่ของ claim ไม่เปลี่ยนผล
        var a = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, EtaxStatus.Accepted, false,
            EtaxCancellationPath.CancelledAtRd, "ยกเลิกทาง e-Tax", "CANCEL-0001", true, null, 70m, 0m,
            ReceiptTotalAmount: 1070m, LivePaymentCoverage: 1070m));
        Assert.True(a.Allowed);
        Assert.Equal(EtaxCancellationEvidence.RdReference, a.Evidence);
        // (ข) ใบต้นทางยังมีเงินรับ ⇒ ปฏิเสธ และชี้ทาง (ค)
        var b = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, EtaxStatus.Accepted, false,
            EtaxCancellationPath.CreditNote, "ลดหนี้", null, false, null, 70m, 1070m));
        Assert.False(b.Allowed);
        Assert.Contains("ใบกำกับเดิมยังใช้ได้", b.Reason);
    }
}

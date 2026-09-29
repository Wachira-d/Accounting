using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม V1G — แก้ผลฝ่ายค้านรอบสองของทีม V1F (erp-review/2026-09-29/review200-round2-V1F.md · DECISIONS ข้อ 43–49) ·
/// ทุกข้อมีสองครึ่ง: เคสที่พังกลับมาถูก + เคสที่ถูกอยู่แล้วไม่ถูกแตะ (F2 ข้อ 8) · ลำดับเหตุการณ์ "เช็คเด้ง → รับชำระใหม่ → ปิดธง" ไล่ที่ระดับตัวตัดสิน
/// ด้วยตัวเลขตัวอย่างของฝ่ายค้าน (INV-1 1,000 + VAT 70 · เช็ค 10 ก.พ. · เงินสด 25 ก.พ.) · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// </summary>
public class VoidReissueR200GTests
{
    private static readonly DateTime Feb10 = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Feb25 = new(2026, 2, 25, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mar05 = new(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid P2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Rc2 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static EtaxCreditNoteFact Cn(string? usedBy = null, DocumentStatus status = DocumentStatus.Approved, bool refs = true,
        bool sameContact = true, decimal vat = 70m, DocumentType type = DocumentType.CreditNote)
        => new(true, "CN-0001", type, status, refs, sameContact, vat, usedBy);

    private static EtaxCancellationClaim CnClaim(EtaxCreditNoteFact? cn, EtaxStatus? etax = EtaxStatus.Accepted, decimal remainingPaid = 0m)
        => new(true, etax, false, EtaxCancellationPath.CreditNote, "ออกใบลดหนี้ทาง e-Tax แล้ว", null, false, cn, 70m, remainingPaid);

    // ═════════════ RV1F-1 (ข้อ 47): ใบลดหนี้ ≠ ยกเลิก — ต้องมีใบลดหนี้จริงในระบบ ใบเสร็จเดิมคงมีผล ═════════════

    [Fact]
    public void R200_V1G_RV1F1_ทางใบลดหนี้_ต้องมีใบลดหนี้ในระบบที่อ้างใบเดิม_หลักฐานเป็นใบลดหนี้ไม่ใช่การยกเลิก()
    {
        var v = DocumentVoidPreconditions.EtaxCancellationResolution(CnClaim(Cn()));
        Assert.True(v.Allowed);
        Assert.Equal(EtaxCancellationEvidence.CreditNoteInSystem, v.Evidence);
        Assert.Contains("ใบเสร็จเดิมยังมีผล", DocumentVoidPreconditions.EvidenceLabel(v.Evidence));
    }

    [Theory]
    [InlineData("none", "รับเลขที่เป็นข้อความอย่างเดียวไม่ได้")]
    [InlineData("draft", "ยังไม่ได้ออก")]
    [InlineData("voided", "ยังไม่ได้ออก")]
    [InlineData("norefs", "ไม่ได้อ้างใบเสร็จนี้")]
    [InlineData("contact", "ผู้ซื้อคนละราย")]
    [InlineData("lessvat", "น้อยกว่าภาษีของใบเสร็จ")]
    [InlineData("used", "ถูกใช้ปิดธงของใบเสร็จ RC-9")]
    [InlineData("notcn", "ไม่ใช่ใบลดหนี้")]
    public void R200_V1G_RV1F1_ทางใบลดหนี้_ไม่มีใบจริงหรือใบไม่ตรง_ปฏิเสธไม่แตะอะไร(string kind, string expect)
    {
        var cn = kind switch
        {
            "none" => null,
            "draft" => Cn(status: DocumentStatus.Draft),
            "voided" => Cn(status: DocumentStatus.Voided),
            "norefs" => Cn(refs: false),
            "contact" => Cn(sameContact: false),
            "lessvat" => Cn(vat: 35m),
            "used" => Cn(usedBy: "RC-9"),
            _ => Cn(type: DocumentType.DebitNote),
        };
        var v = DocumentVoidPreconditions.EtaxCancellationResolution(CnClaim(cn));
        Assert.False(v.Allowed);
        Assert.Contains(expect, v.Reason);
        Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", v.Reason);
    }

    [Fact]
    public void R200_V1G_RV1F1_ทางใบลดหนี้_ใบไม่ถึงกรมสรรพากร_หรือยังมีเงินรับที่มีผล_ปฏิเสธพร้อมทางไปต่อ()
    {
        var notReached = DocumentVoidPreconditions.EtaxCancellationResolution(CnClaim(Cn(), etax: null));
        Assert.False(notReached.Allowed);
        Assert.Contains("ยกเลิกทาง e-Tax แล้ว", notReached.Reason);
        var paid = DocumentVoidPreconditions.EtaxCancellationResolution(CnClaim(Cn(), remainingPaid: 1070m));
        Assert.False(paid.Allowed);
        Assert.Contains("การรับชำระที่มีผลอยู่", paid.Reason);
        var submitted = DocumentVoidPreconditions.EtaxCancellationResolution(CnClaim(Cn(), etax: EtaxStatus.Submitted));
        Assert.False(submitted.Allowed);
        Assert.Contains("Submitted", submitted.Reason);
    }

    // ═════════════ RV1F-4 (ข้อ 46): ทางเลขอ้างอิงต้องแนบไฟล์หลักฐาน ═════════════

    [Fact]
    public void R200_V1G_RV1F4_ถึงกรมสรรพากรแล้ว_เลขอ้างอิงอย่างเดียวไม่พอ_ต้องแนบไฟล์_ทิศตรงข้ามยกเลิกในระบบไม่ต้องแนบ()
    {
        var noFile = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, EtaxStatus.Accepted, false,
            EtaxCancellationPath.CancelledAtRd, "ยกเลิกทาง e-Tax", "CANCEL-0001", false, null, 70m, 0m));
        Assert.False(noFile.Allowed);
        Assert.Contains("แนบไฟล์หลักฐาน", noFile.Reason);
        var withFile = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, EtaxStatus.Accepted, false,
            EtaxCancellationPath.CancelledAtRd, "ยกเลิกทาง e-Tax", "CANCEL-0001", true, null, 70m, 0m));
        Assert.True(withFile.Allowed);
        Assert.Equal(EtaxCancellationEvidence.RdReference, withFile.Evidence);
        // ทิศตรงข้าม (ข้อ 46): e-Tax ทุกแถวถูกยกเลิกในระบบ ⇒ ไม่ต้องแนบ
        var voided = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, null, true,
            EtaxCancellationPath.CancelledAtRd, "ยกเลิก e-Tax ก่อนตอบรับ", null, false, null, 70m, 0m));
        Assert.True(voided.Allowed);
    }

    // ═════════════ RV1F-9: ป้ายหลักฐานตรงความจริง ═════════════

    [Fact]
    public void R200_V1G_RV1F9_ป้ายหลักฐาน_ยกเลิกในระบบนี้ไม่ใช่คำยืนยันจากกรมสรรพากร_และไม่เคยถึงแยกจากถูกยกเลิก()
    {
        var voidedHere = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, null, true,
            EtaxCancellationPath.CancelledAtRd, "เหตุผล", null, false, null, 70m, 0m));
        Assert.Equal(EtaxCancellationEvidence.EtaxVoidedInSystem, voidedHere.Evidence);
        Assert.Contains("ไม่ใช่คำยืนยันจากกรมสรรพากร", DocumentVoidPreconditions.EvidenceLabel(voidedHere.Evidence));
        // แถวที่เหลือถูกปฏิเสธ/ผิดพลาด (ไม่ได้ถูกยกเลิก) — เดิมถูกบันทึกเป็น "ยกเลิกในระบบ"
        var rejected = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, EtaxStatus.Rejected, false,
            EtaxCancellationPath.CancelledAtRd, "เหตุผล", null, false, null, 70m, 0m));
        Assert.Equal(EtaxCancellationEvidence.EtaxNeverReachedRd, rejected.Evidence);
        Assert.DoesNotContain("ยกเลิกแถว e-Tax", DocumentVoidPreconditions.EvidenceLabel(rejected.Evidence));
    }

    // ═════════════ RV1F-2 (ข้อ 48): ถอยภาษีลงเดือนที่ตั้งรายการ · งวดปิด = ปฏิเสธดัง (ผู้ใช้) / ธงที่มองเห็น (เช็คเด้ง) ═════════════

    [Fact]
    public void R200_V1G_RV1F2_เดือนที่ตั้งรายการปิด_ผู้ใช้ยกเลิกการชำระ_ปฏิเสธดังพร้อมทางไปต่อ_ไม่ใช่LogErrorแล้วสำเร็จ()
    {
        var lockReason = "งวดบัญชี “ก.พ. 2569” ที่ปิดแล้ว (วันที่ 10/02/2026)";
        var d = DocumentVoidPreconditions.OutputVatUndoOnPaymentVoid(lockReason, PaymentVoidCause.User, "INV-1");
        Assert.Equal(OutputVatUndoAction.Refuse, d.Action);
        Assert.Contains("INV-1", d.Message);
        Assert.Contains("ใบลดหนี้", d.Message);                          // ทางไปต่อ
        Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", d.Message);
    }

    [Theory]
    [InlineData(PaymentVoidCause.ChequeBounce, "เช็คเด้ง")]
    [InlineData(PaymentVoidCause.SettlementUnpost, "ยกเลิกการลงบัญชีรอบโอน")]
    public void R200_V1G_RV1F2_เดือนที่ตั้งรายการปิด_เช็คเด้งห้ามบล็อก_กลับเงินต่อแต่ติดธงที่มองเห็น(PaymentVoidCause cause, string expect)
    {
        var d = DocumentVoidPreconditions.OutputVatUndoOnPaymentVoid("ภ.พ.30 เดือน 02/2026 ที่ยื่น/ประกาศว่ายื่น/ล็อกแล้ว", cause, "INV-1");
        Assert.Equal(OutputVatUndoAction.KeepAndFlag, d.Action);
        Assert.StartsWith("[VAT-UNDO-BLOCKED]", d.Message);
        Assert.Contains(expect, d.Message);
    }

    [Theory]
    [InlineData(PaymentVoidCause.User)]
    [InlineData(PaymentVoidCause.ChequeBounce)]
    [InlineData(PaymentVoidCause.SettlementUnpost)]
    public void R200_V1G_RV1F2_ทิศตรงข้าม_เดือนที่ตั้งรายการเปิด_ถอยได้ทุกทางเข้าเหมือนเดิม(PaymentVoidCause cause)
    {
        var d = DocumentVoidPreconditions.OutputVatUndoOnPaymentVoid(null, cause, "INV-1");
        Assert.Equal(OutputVatUndoAction.Undo, d.Action);
        Assert.Null(d.Message);
    }

    // ═════════════ ลำดับเหตุการณ์ (ระดับตัวตัดสิน): เช็คเด้ง → รับชำระใหม่ → ปิดธง (RV1F-3 · ข้อ 48) ═════════════

    private static EtaxCancelFollowUpFacts AfterRepay(DateTime repayDate, bool rc2ReachedRd = false, string? reclassLock = null,
        string? payLock = null)
        => new(DocumentType.Invoice, 70m, SourceFullyPaid: true, OutputVatDueAt: Feb10, OtherLiveVatReceipt: false,
            LivePayments: new[] { new EtaxCancelLivePayment(P2, repayDate, false, Rc2, "RC-2", 0m, rc2ReachedRd) },
            ReclassPeriodLock: reclassLock, FirstPaymentPeriodLock: payLock);

    [Fact]
    public void R200_V1G_ลำดับ_เช็คเด้ง_รับชำระใหม่_ปิดธง_การขายต้องได้ใบกำกับณวันรับเงิน_และภาษีย้ายไปวันรับเงินจริง()
    {
        // 1) 20 ก.พ. เช็คเด้ง — RC-1 (VAT 70) ถึงกรมสรรพากรแล้ว ⇒ ติดธง ไม่ยกเลิก · ภาษีขายไม่ถอย (ใบกำกับยังมีผล)
        var bounce = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(EtaxStatus.Accepted, "RC-1", PaymentVoidCause.ChequeBounce);
        Assert.Equal(AutoReceiptEtaxAction.FlagEtaxCancellation, bounce.Action);
        var keeps = DocumentVoidPreconditions.FlaggedReceiptKeepsTaxPoint(bounce.Action, 70m);
        Assert.False(DocumentVoidPreconditions.ShouldUndoOutputVatReclass(0m, outputVatDue: true, liveVatReceiptKeepsTaxPoint: keeps));
        // 2) 25 ก.พ. รับเงินสดใหม่ — ใบกำกับของการขายยังมีผล (RC-1) ⇒ RC-2 เป็นใบรับเปล่า (ไม่นับซ้ำ)
        Assert.False(SettlementReceiptPolicy.CarriesTaxInvoiceRole(DocumentType.Invoice, 70m, singleShotFull: true, liveVatReceiptExists: true));
        // 3) เม.ย. ปิดธงทาง (ก) — ยกเลิก RC-1 ⇒ ต้องออกใบกำกับ ณ 25 ก.พ. ให้ P2 (ยกเลิก RC-2) + ภาษีขายย้ายจาก 10 ก.พ. ไป 25 ก.พ.
        var resolve = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, EtaxStatus.Accepted, false,
            EtaxCancellationPath.CancelledAtRd, "เช็คเด้ง ยกเลิกทางกรมสรรพากรแล้ว", "CANCEL-RD-01", true, null, 70m, 1070m));
        Assert.True(resolve.Allowed);
        var plan = DocumentVoidPreconditions.EtaxCancellationFollowUp(AfterRepay(Feb25));
        Assert.True(plan.Allowed);
        Assert.True(plan.UndoReclass);
        Assert.Equal(Feb25, plan.ReclassAt);
        Assert.Equal(P2, plan.IssueVatReceiptForPaymentId);
        Assert.Equal(Rc2, plan.VoidPlainReceiptId);
    }

    [Fact]
    public void R200_V1G_ลำดับ_รับใหม่วันเดียวกับใบเดิม_ออกใบกำกับแต่ไม่ต้องถอยย้ายภาษี()
    {
        var plan = DocumentVoidPreconditions.EtaxCancellationFollowUp(AfterRepay(Feb10));
        Assert.True(plan.Allowed);
        Assert.False(plan.UndoReclass);
        Assert.Null(plan.ReclassAt);
        Assert.Equal(P2, plan.IssueVatReceiptForPaymentId);
    }

    [Theory]
    [InlineData("reclass", "ปิด")]
    [InlineData("pay", "ปิด")]
    [InlineData("rc2", "RC-2")]
    public void R200_V1G_ลำดับ_งวดปิดหรือใบรับส่งeTaxแล้ว_ปฏิเสธดังพร้อมทางไปต่อ_ไม่แตะอะไร(string kind, string expect)
    {
        var f = kind switch
        {
            "reclass" => AfterRepay(Mar05, reclassLock: "งวดบัญชี “ก.พ. 2569” ที่ปิดแล้ว (วันที่ 10/02/2026)"),
            "pay" => AfterRepay(Mar05, payLock: "งวดบัญชี “มี.ค. 2569” ที่ปิดแล้ว (วันที่ 05/03/2026)"),
            _ => AfterRepay(Feb25, rc2ReachedRd: true),
        };
        var plan = DocumentVoidPreconditions.EtaxCancellationFollowUp(f);
        Assert.False(plan.Allowed);
        Assert.Contains(expect, plan.Reason);
        Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", plan.Reason);
        Assert.Null(plan.IssueVatReceiptForPaymentId);
    }

    [Fact]
    public void R200_V1G_ลำดับ_ไม่มีการรับชำระเหลือ_ถอยภาษีในเดือนที่ตั้งรายการ_งวดปิดปฏิเสธพร้อมทางใบลดหนี้()
    {
        var open = DocumentVoidPreconditions.EtaxCancellationFollowUp(new EtaxCancelFollowUpFacts(DocumentType.Invoice, 70m, false, Feb10, false,
            Array.Empty<EtaxCancelLivePayment>(), null, null));
        Assert.True(open.Allowed);
        Assert.True(open.UndoReclass);
        Assert.Null(open.IssueVatReceiptForPaymentId);
        var locked = DocumentVoidPreconditions.EtaxCancellationFollowUp(new EtaxCancelFollowUpFacts(DocumentType.Invoice, 70m, false, Feb10, false,
            Array.Empty<EtaxCancelLivePayment>(), "ภ.พ.30 เดือน 02/2026 ที่ยื่น/ประกาศว่ายื่น/ล็อกแล้ว (วันที่ 10/02/2026)", null));
        Assert.False(locked.Allowed);
        Assert.Contains("ออกใบลดหนี้แล้ว", locked.Reason);                // ทางไปต่อ = ทาง (ข)
    }

    [Fact]
    public void R200_V1G_ลำดับ_ทิศตรงข้าม_ใบเสร็จถือVATอื่นยังมีผล_ไม่แตะภาษี_และรับหลายงวดไม่ออกใบกำกับใบเดียวแทนทั้งก้อน()
    {
        var other = DocumentVoidPreconditions.EtaxCancellationFollowUp(AfterRepay(Feb25) with { OtherLiveVatReceipt = true });
        Assert.True(other.Allowed);
        Assert.False(other.UndoReclass);
        Assert.Null(other.ReclassAt);
        Assert.Null(other.IssueVatReceiptForPaymentId);

        var partial = DocumentVoidPreconditions.EtaxCancellationFollowUp(new EtaxCancelFollowUpFacts(DocumentType.Invoice, 70m, true, Feb10, false,
            new[]
            {
                new EtaxCancelLivePayment(Guid.NewGuid(), Mar05, false, null, null, 0m, false),
                new EtaxCancelLivePayment(P2, Feb25, false, Rc2, "RC-2", 0m, false),
            }, null, null));
        Assert.True(partial.Allowed);
        Assert.Null(partial.IssueVatReceiptForPaymentId);            // กติกาเดิมของการรับหลายงวด (CarriesTaxInvoiceRole)
        Assert.Equal(Feb25, partial.ReclassAt);                       // จุดความรับผิด = วันรับเงินจริงครั้งแรกที่เหลือ
        Assert.True(partial.UndoReclass);

        // ใบกำกับภาษี (TaxInvoice) ต้นทาง — ใบเสร็จไม่ถือ VAT อยู่แล้ว ไม่ออกใบกำกับซ้ำ
        var tiv = DocumentVoidPreconditions.EtaxCancellationFollowUp(AfterRepay(Feb25) with { SourceType = DocumentType.TaxInvoice });
        Assert.Null(tiv.IssueVatReceiptForPaymentId);
    }

    // ═════════════ RV1F-6 (ข้อ 43): ยกเลิก/กู้คืนเอกสารดูชุดสถานะ "ถึงกรมสรรพากร" เดียวกัน ═════════════

    [Theory]
    [InlineData(EtaxStatus.Submitted, "Submitted")]
    [InlineData(EtaxStatus.Accepted, "ถึงกรมสรรพากรแล้ว")]
    public void R200_V1G_RV1F6_ยกเลิกเอกสารที่eTaxส่งแล้วหรือตอบรับ_บล็อกพร้อมทางไปต่อ_ไม่พลิกเป็นVoided(EtaxStatus status, string expect)
    {
        var block = DocumentVoidPreconditions.DocumentVoidEtaxBlock(status, "TIV-0001", restore: false);
        Assert.NotNull(block);
        Assert.Contains(expect, block);
        Assert.Contains("TIV-0001", block);
        Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", block);
        Assert.Contains("กู้คืนไม่ได้", DocumentVoidPreconditions.DocumentVoidEtaxBlock(status, "TIV-0001", restore: true));
        // e-Tax by Email ที่ประทับเวลาแล้ว = ถือเท่าตอบรับ (ตัวโหลดเดียว EffectiveEtaxAsync)
        Assert.NotNull(DocumentVoidPreconditions.DocumentVoidEtaxBlock(
            DocumentVoidPreconditions.EffectiveEtax(null, sentByEmailWithRdTimestamp: true), "TIV-0002", restore: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(EtaxStatus.Generated)]
    [InlineData(EtaxStatus.Signed)]
    [InlineData(EtaxStatus.Error)]
    [InlineData(EtaxStatus.Rejected)]
    public void R200_V1G_RV1F6_ทิศตรงข้าม_eTaxยังไม่ถึงกรมสรรพากร_ยกเลิกได้เหมือนเดิม(EtaxStatus? status)
        => Assert.Null(DocumentVoidPreconditions.DocumentVoidEtaxBlock(status, "TIV-0001", restore: false));

    [Fact]
    public void R200_V1G_RV1F6_ยกเลิกการลงบัญชีรอบโอน_เอกสารeTaxส่งแล้ว_ต้องยกเลิกeTaxก่อน_ไม่ใช่ทางใบลดหนี้()
    {
        var doc = new SettlementUnpostDocument(Guid.NewGuid(), "TIV-0009", DocumentType.TaxInvoice, "sum-20260210", Feb10, 70m, false,
            EtaxAccepted: false, InLockedReport: false, EtaxSubmitted: true);
        var r = Assert.Single(SettlementUnpostGate.Evaluate(new[] { doc }, Array.Empty<SettlementUnpostCertificate>(),
            new HashSet<(TaxType, int, int)>()));
        Assert.Contains("Submitted", r.Reason);
        Assert.Contains("ยกเลิก e-Tax", r.NextStep);
        Assert.Equal(SettlementUnpostRefusalKind.NeedsUserAction, r.Kind);
        // ทิศตรงข้าม: ไม่ส่ง e-Tax = ไม่มีเหตุจาก e-Tax
        Assert.Empty(SettlementUnpostGate.Evaluate(new[] { doc with { EtaxSubmitted = false } }, Array.Empty<SettlementUnpostCertificate>(),
            new HashSet<(TaxType, int, int)>()));
        var child = new SettlementOrphanChild(Guid.NewGuid(), Guid.NewGuid(), DocumentType.CreditNote, "CN-0009", false, false, false, null,
            EtaxSubmitted: true);
        Assert.Contains("Submitted", SettlementOrphanTriage.ChildUnvoidableReason(child));
    }

    // ═════════════ RV1F-5 (ข้อ 49): ผู้ยืนยันเห็นคำขอเต็ม + ยืนยันผูก hash ═════════════

    private static ReissueRequestView View(string buyerName = "บริษัท ใหม่ จำกัด", string? notes = "ส่งของแล้ว", string line = "ค่าบริการ ก.ย.")
    {
        var lineId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        return SettlementPaidReissueRequestView.Build("user-a", Feb10, "ชื่อผู้ซื้อผิด",
            new ReissuePartySnapshot("บริษัท เดิม จำกัด", "0105551234567", "00000", "1 ถ.สุขุมวิท กรุงเทพฯ"),
            new ReissuePartySnapshot(buyerName, "0105559876543", "00001", "9 ถ.พระราม 9 กรุงเทพฯ"), buyerChanged: true,
            "หมายเหตุเดิม", notes,
            new[] { (lineId, 1, (string?)"ค่าบริการ"), (Guid.NewGuid(), 2, (string?)"ค่าขนส่ง") },
            new[] { (lineId, (string?)line) });
    }

    [Fact]
    public void R200_V1G_RV1F5_ผู้ยืนยันเห็นผู้ซื้อเดิมและใหม่_หมายเหตุ_คำบรรยายที่เปลี่ยน()
    {
        var v = View();
        Assert.Equal("บริษัท เดิม จำกัด", v.BuyerBefore.Name);
        Assert.Equal("0105559876543", v.BuyerAfter.TaxId);
        Assert.Equal("00001", v.BuyerAfter.BranchCode);
        Assert.Contains("พระราม 9", v.BuyerAfter.Address);
        Assert.True(v.BuyerChanged);
        Assert.True(v.NotesChanged);
        var line = Assert.Single(v.Lines);                              // บรรทัดที่ไม่เปลี่ยนไม่แสดง
        Assert.Equal("ค่าบริการ", line.Before);
        Assert.Equal("ค่าบริการ ก.ย.", line.After);
        Assert.Equal(64, v.RequestHash.Length);
        Assert.Equal(v.RequestHash, SettlementPaidReissueRequestView.Hash(v));
    }

    [Fact]
    public void R200_V1G_RV1F5_คำขอหรือผู้ซื้อเปลี่ยนหลังเปิดดู_hashเปลี่ยน_ยืนยันด้วยhashเดิมถูกปฏิเสธ_ทิศตรงข้ามhashตรงผ่าน()
    {
        var seen = View();
        Assert.Null(SettlementPaidReissueRequestView.ConfirmMismatch(seen.RequestHash, View()));        // เนื้อหาเดิม = hash เดิม
        Assert.NotEqual(seen.RequestHash, View(buyerName: "บริษัท อื่น จำกัด").RequestHash);
        Assert.NotEqual(seen.RequestHash, View(notes: "หมายเหตุใหม่").RequestHash);
        Assert.NotEqual(seen.RequestHash, View(line: "ค่าบริการ ต.ค.").RequestHash);
        var changed = SettlementPaidReissueRequestView.ConfirmMismatch(seen.RequestHash, View(buyerName: "บริษัท อื่น จำกัด"));
        Assert.Contains("คำขอถูกเปลี่ยน", changed);
        Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", changed);
        Assert.Contains("อ้างคำขอที่เห็น", SettlementPaidReissueRequestView.ConfirmMismatch(null, seen));
    }

    // ═════════════ RV1F-12 · RV1F-11: ผู้จัดทำไม่ตามไป · ทุก property ที่ EF map ถูกจัดกลุ่ม ═════════════

    [Fact]
    public void R200_V1G_RV1F12_ผู้จัดทำและลายเซ็นผู้จัดทำของใบเดิมไม่ตามไปใบแทน()
    {
        var old = new Document { PreparerName = "ผู้จัดทำภายนอก", PreparerSignatureBase64 = "sig", TotalAmount = 1070m };
        var neo = new Document();
        SettlementPaidReissue.CopyDocumentForReissue(old, neo);
        Assert.Null(neo.PreparerName);
        Assert.Null(neo.PreparerSignatureBase64);
        Assert.Equal(1070m, neo.TotalAmount);
        Assert.Contains(nameof(Document.PreparerName), SettlementPaidReissue.DocumentNotCarriedFields);
    }

    [Fact]
    public void R200_V1G_RV1F11_ทุกpropertyที่EFmapของเอกสารและบรรทัด_ถูกจัดกลุ่มตามไปหรือไม่ตามไป_รวมชนิดอ้างอิง()
    {
        using var db = new AccountingDbContext(new DbContextOptionsBuilder<AccountingDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none").Options);
        void AssertMapped<T>(IReadOnlyList<string> carried, IReadOnlyList<string> notCarried)
        {
            var mapped = db.Model.FindEntityType(typeof(T))!.GetProperties()
                .Where(p => p.PropertyInfo != null)            // shadow property (FK ที่ EF สร้างเอง) ไม่มีบน entity — ตัวคัดลอกไม่เห็นอยู่แล้ว
                .Select(p => p.Name).ToList();
            // ช่องใหม่ทุกชนิดที่ EF map เป็นคอลัมน์ (รวม string[] / byte[] / jsonb) ต้องถูกตัดสินอย่างตั้งใจ — ไม่ใช่ตัดทิ้งเพราะเป็นชนิดอ้างอิง
            Assert.Empty(mapped.Except(carried).Except(notCarried));
        }
        AssertMapped<Document>(SettlementPaidReissue.DocumentCarriedFields, SettlementPaidReissue.DocumentNotCarriedFields);
        AssertMapped<DocumentLine>(SettlementPaidReissue.LineCarriedFields, SettlementPaidReissue.LineNotCarriedFields);
    }

    // ═════════════ ข้อ 44: รายงานอ่านอย่างเดียวให้นักบัญชี ═════════════

    [Fact]
    public void R200_V1G_ข้อ44_ใบเสร็จติดธงที่ภาษีขายถูกถอยไปแล้ว_ถูกจำแนก_ทิศตรงข้ามใบที่ยังถือภาษีไม่ถูกจำแนก()
    {
        Assert.True(EtaxReissueReview.FlaggedReceiptVatUndone(true, 70m, true, DocumentType.Invoice, sourceOutputVatDueAt: null));
        Assert.False(EtaxReissueReview.FlaggedReceiptVatUndone(true, 70m, true, DocumentType.Invoice, Feb10));      // ยังถือจุดความรับผิด
        Assert.False(EtaxReissueReview.FlaggedReceiptVatUndone(true, 0m, true, DocumentType.Invoice, null));        // ใบรับเปล่า
        Assert.False(EtaxReissueReview.FlaggedReceiptVatUndone(true, 70m, false, DocumentType.Invoice, null));      // ไม่ติดธง
        Assert.False(EtaxReissueReview.FlaggedReceiptVatUndone(false, 70m, true, DocumentType.Invoice, null));      // ยกเลิกแล้ว
    }

    [Fact]
    public void R200_V1G_ข้อ44_ใบแทนเก่าที่พาหลักฐานของใบเดิมมา_ถูกจำแนก_ช่องที่ตามไปโดยชอบและตัวตนของใบไม่นับ()
    {
        var old = new Document
        {
            DocumentNumber = "TIV-0003", Status = DocumentStatus.Voided, TotalAmount = 1070m, DeliverySignedBy = "ลูกค้าเดิม",
            RdComplianceStatus = RdComplianceStatus.Failed, PreparerName = "ผู้จัดทำภายนอก", InternalNotes = "[VOID-REISSUE]",
        };
        var neoCopiedAll = new Document
        {
            DocumentNumber = "TIV-0009", Status = DocumentStatus.Paid, TotalAmount = 1070m, DeliverySignedBy = "ลูกค้าเดิม",
            RdComplianceStatus = RdComplianceStatus.Failed, PreparerName = "ผู้จัดทำภายนอก", InternalNotes = "[VOID-REISSUE]",
        };
        var excess = EtaxReissueReview.CarriedExcess(old, neoCopiedAll);
        Assert.Contains(nameof(Document.DeliverySignedBy), excess);
        Assert.Contains(nameof(Document.RdComplianceStatus), excess);
        Assert.Contains(nameof(Document.PreparerName), excess);
        Assert.DoesNotContain(nameof(Document.TotalAmount), excess);     // ตามไปโดยชอบ
        Assert.DoesNotContain(nameof(Document.InternalNotes), excess);   // ช่องของใบเอง
        // ทิศตรงข้าม: ใบแทนจากตัวคัดลอก allowlist ไม่มีช่องเกิน
        var neo = new Document();
        SettlementPaidReissue.CopyDocumentForReissue(old, neo);
        Assert.Empty(EtaxReissueReview.CarriedExcess(old, neo));
    }
}

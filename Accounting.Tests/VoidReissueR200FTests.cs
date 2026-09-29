using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม V1F — แก้ผลฝ่ายค้านทีม V1 (erp-review/2026-09-29/review200-V1.md) · ทุกข้อมีสองครึ่ง: เคสที่พังกลับมาถูก และเคสที่ถูกอยู่แล้ว
/// ต้องไม่ถูกแตะ (F2 ข้อ 8) · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py (เทสต์ที่เรียกแค่ helper ≠ ด่านถูกต่อสาย)
/// </summary>
public class VoidReissueR200FTests
{
    private static readonly DateTime Day = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    private static SettlementPaidReissueFacts OkFacts() => new(
        DocumentType.Invoice, DocumentStatus.Paid, IsSettlementReceipt: false, AlreadyReplaced: false, CreatedBySettlementBatch: false,
        PostedBatchPaymentBlock: "ใบนี้รับชำระจากรอบโอน PO-1 ที่ลงบัญชีแล้ว", IsDeposit: false, HasDepositApplied: false,
        DocumentEtax: null, FilingLocked: false, WhtFiledBlock: null, ChildBlock: null, ClosedPeriodName: null,
        SharedPaymentNumber: null, ActiveWhtCertificates: 0, HasVatDeferral: false,
        Receipts: new[] { new ReissueReceiptFact("RE-0001", null, false, null) });

    // ═════════════ V1-R1: ใบเสร็จถือ VAT (§78/1) เป็นเจ้าของแถว ภ.พ.30 — รายงานล็อก/งวดปิดของใบเสร็จต้องบล็อก ═════════════

    [Fact]
    public void R200_V1F_R1_ใบเสร็จอัตโนมัติอยู่ในรายงานภาษีที่ล็อกแล้ว_บล็อกพร้อมทางไปต่อ_แม้ใบขายเองไม่อยู่ในรายงาน()
    {
        // ฉากฝ่ายค้าน: INV-A (ส.ค.) ใบบริการ · รอบโอนรับชำระ ก.ย. ⇒ RE-B ใบกำกับ ณ วันรับเงิน · ภ.พ.30 ก.ย. ยื่นและล็อก (แถว = RE-B ไม่ใช่ INV-A)
        var f = OkFacts() with { FilingLocked = false, Receipts = new[] { new ReissueReceiptFact("RE-B", null, true, null) } };
        var v = SettlementPaidReissue.Decide(f);
        Assert.True(v.Relevant);
        Assert.False(v.Allowed);
        Assert.Equal("REISSUE-RECEIPT-FILING-LOCKED", v.RuleCode);
        Assert.Contains("RE-B", v.Reason);
        Assert.Contains("Reject & Reverse", v.Reason);                      // ทางไปต่อ
        Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", v.Reason);
    }

    [Fact]
    public void R200_V1F_R1_งวดบัญชีของวันที่ใบเสร็จปิดแล้ว_บล็อก_แม้งวดของใบขายเปิด()
    {
        var v = SettlementPaidReissue.Decide(OkFacts() with
        {
            ClosedPeriodName = null,
            Receipts = new[] { new ReissueReceiptFact("RE-B", null, false, "ก.ย. 2569") },
        });
        Assert.False(v.Allowed);
        Assert.Equal("REISSUE-RECEIPT-PERIOD-CLOSED", v.RuleCode);
        Assert.Contains("ก.ย. 2569", v.Reason);
    }

    [Fact]
    public void R200_V1F_R1_ทิศตรงข้าม_ใบเสร็จไม่อยู่ในรายงานล็อกและงวดเปิด_ออกใบแทนได้เหมือนเดิม()
    {
        Assert.True(SettlementPaidReissue.Decide(OkFacts()).Allowed);
        Assert.True(SettlementPaidReissue.Decide(OkFacts() with { Receipts = Array.Empty<ReissueReceiptFact>() }).Allowed);
        Assert.True(SettlementPaidReissue.Decide(OkFacts() with
        {
            Receipts = new[] { new ReissueReceiptFact("RE-1", EtaxStatus.Rejected, false, null), new ReissueReceiptFact("RE-2", null, false, null) },
        }).Allowed);
    }

    // ═════════════ V1-R2: เช็คเด้ง + ใบเสร็จติดธง ⇒ ภาษีขายยังอยู่ใน ภ.พ.30 ═════════════

    [Fact]
    public void R200_V1F_R2_เช็คเด้ง_ใบเสร็จถือVAT70ติดธง_ห้ามถอยภาษีขาย_เงินกลับแต่VATยังรายงาน()
    {
        // ใบแจ้งหนี้บริการ 1,000 + VAT 70 = 1,070 รับครบ ⇒ ใบเสร็จถือ VAT 70 (ใบกำกับ ณ วันรับเงิน) ส่ง e-Tax ตอบรับแล้ว ⇒ เช็คเด้ง
        var decision = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(EtaxStatus.Accepted, "RE-0009", PaymentVoidCause.ChequeBounce);
        Assert.Equal(AutoReceiptEtaxAction.FlagEtaxCancellation, decision.Action);
        var keeps = DocumentVoidPreconditions.FlaggedReceiptKeepsTaxPoint(decision.Action, receiptVatAmount: 70m);
        Assert.True(keeps);
        // ยอดรับชำระเหลือ 0 (เงินกลับ ลูกหนี้ 1,070 เปิดใหม่) แต่ภาษีขาย 70 ต้องไม่ถูกถอย (21911 → 21913 ห้ามเกิด)
        Assert.False(DocumentVoidPreconditions.ShouldUndoOutputVatReclass(0m, outputVatDue: true, liveVatReceiptKeepsTaxPoint: keeps));
        Assert.Contains("ภาษีขาย", decision.Message);
        Assert.Contains("บันทึกว่ายกเลิกทาง e-Tax แล้ว", decision.Message);   // ทางปิดธง (R3)
    }

    [Fact]
    public void R200_V1F_R2_ทิศตรงข้าม_ใบเสร็จยกเลิกได้ปกติ_หรือไม่ถือVAT_หรือยังมีเงินเหลือ_พฤติกรรมเดิม()
    {
        // ใบเสร็จยังไม่ถึงกรมสรรพากร ⇒ ยกเลิกคู่กัน ⇒ ถอยภาษีขายได้ (พฤติกรรมเดิม M-6)
        var voidDecision = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(null, "RE-1", PaymentVoidCause.ChequeBounce);
        Assert.Equal(AutoReceiptEtaxAction.Void, voidDecision.Action);
        Assert.False(DocumentVoidPreconditions.FlaggedReceiptKeepsTaxPoint(voidDecision.Action, 70m));
        Assert.True(DocumentVoidPreconditions.ShouldUndoOutputVatReclass(0m, true, false));
        // ใบเสร็จติดธงแต่เป็นใบรับเปล่า (VAT 0) — ไม่ใช่ใบกำกับ ⇒ ไม่ถือจุดความรับผิด
        Assert.False(DocumentVoidPreconditions.FlaggedReceiptKeepsTaxPoint(AutoReceiptEtaxAction.FlagEtaxCancellation, 0m));
        // ยังมีการรับชำระเหลือ / ยังไม่เคยย้ายภาษีขาย ⇒ ไม่ถอย (พฤติกรรมเดิม)
        Assert.False(DocumentVoidPreconditions.ShouldUndoOutputVatReclass(535m, true, false));
        Assert.False(DocumentVoidPreconditions.ShouldUndoOutputVatReclass(0m, false, false));
    }

    [Fact]
    public void R200_V1F_R2_รับชำระใหม่หลังเช็คเด้ง_ใบกำกับออกไปแล้ว_ใบเสร็จใหม่ต้องเป็นใบรับเปล่า_ไม่นับภาษีขายซ้ำ()
    {
        Assert.False(SettlementReceiptPolicy.CarriesTaxInvoiceRole(DocumentType.Invoice, 70m, singleShotFull: true, liveVatReceiptExists: true));
        // ทิศตรงข้าม: ไม่มีใบเสร็จถือ VAT เหลือ ⇒ ใบเสร็จนี้คือใบกำกับ ณ วันรับเงินตามเดิม
        Assert.True(SettlementReceiptPolicy.CarriesTaxInvoiceRole(DocumentType.Invoice, 70m, singleShotFull: true, liveVatReceiptExists: false));
    }

    // ═════════════ V1-R3: ปิดธง "ต้องยกเลิกทาง e-Tax" ต้องมีหลักฐาน (ห้ามประทับสถานะของกรมสรรพากรเอง) ═════════════

    [Fact]
    public void R200_V1F_R3_ปิดธงได้เมื่อมีหลักฐาน_eTaxถูกยกเลิกในระบบ_หรือเลขอ้างอิงจากกรมสรรพากร()
    {
        var voided = DocumentVoidPreconditions.EtaxCancellationResolution(true, effectiveEtax: null, null, "เช็คเด้ง ยกเลิก e-Tax แล้ว");
        Assert.True(voided.Allowed);
        Assert.Equal(EtaxCancellationEvidence.EtaxVoidedInSystem, voided.Evidence);

        var rd = DocumentVoidPreconditions.EtaxCancellationResolution(true, EtaxStatus.Accepted, "CN-RD-2569-0001", "ออกใบลดหนี้ทาง e-Tax แล้ว");
        Assert.True(rd.Allowed);
        Assert.Equal(EtaxCancellationEvidence.RdReference, rd.Evidence);
    }

    [Theory]
    [InlineData(false, null, null, "เหตุผล", "ไม่ได้ติดธง")]
    [InlineData(true, null, null, "  ", "เหตุผล")]
    [InlineData(true, EtaxStatus.Submitted, "REF-1", "เหตุผล", "Submitted")]
    [InlineData(true, EtaxStatus.Accepted, null, "เหตุผล", "เลขอ้างอิง")]
    [InlineData(true, EtaxStatus.Accepted, "ab", "เหตุผล", "เลขอ้างอิง")]
    public void R200_V1F_R3_ไม่มีหลักฐาน_ปฏิเสธพร้อมทางไปต่อ_ไม่แตะอะไร(bool flagged, EtaxStatus? etax, string? reference, string reason, string expect)
    {
        var v = DocumentVoidPreconditions.EtaxCancellationResolution(flagged, etax, reference, reason);
        Assert.False(v.Allowed);
        Assert.Equal(EtaxCancellationEvidence.None, v.Evidence);
        Assert.Contains(expect, v.Reason);
        Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", v.Reason);
    }

    // ═════════════ V1-P1: e-Tax by Email ที่ประทับเวลาแล้ว = ถึงกรมสรรพากร ═════════════

    [Fact]
    public void R200_V1F_P1_eTaxByEmailส่งสำเร็จพร้อมCCประทับเวลา_ถือเท่าตอบรับแล้ว_ยกเลิกเงียบไม่ได้()
    {
        Assert.Equal(EtaxStatus.Accepted, DocumentVoidPreconditions.EffectiveEtax(null, sentByEmailWithRdTimestamp: true));
        Assert.Equal(EtaxStatus.Accepted, DocumentVoidPreconditions.EffectiveEtax(EtaxStatus.Generated, true));
        var d = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(DocumentVoidPreconditions.EffectiveEtax(null, true), "RE-7", PaymentVoidCause.User);
        Assert.Equal(AutoReceiptEtaxAction.Refuse, d.Action);
        Assert.Contains("e-Tax by Email", d.Message);
        // ทิศตรงข้าม: ไม่มีบันทึกการส่ง ⇒ สถานะแถวตามเดิม (ยังไม่ถึง = ยกเลิกได้)
        Assert.Null(DocumentVoidPreconditions.EffectiveEtax(null, false));
        Assert.Equal(EtaxStatus.Error, DocumentVoidPreconditions.EffectiveEtax(EtaxStatus.Error, false));
        Assert.Equal(AutoReceiptEtaxAction.Void,
            DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(DocumentVoidPreconditions.EffectiveEtax(null, false), "RE-7", PaymentVoidCause.User).Action);
    }

    // ═════════════ V1-R4: e-Tax ของใบขายที่ Submitted ห้ามพลิกเป็น Voided — บล็อกพร้อมทางไปต่อ ═════════════

    [Fact]
    public void R200_V1F_R4_eTaxใบขายส่งแล้วยังไม่รู้ผล_บล็อก_ชุดสถานะเดียวกับใบเสร็จ()
    {
        var v = SettlementPaidReissue.Decide(OkFacts() with { DocumentEtax = EtaxStatus.Submitted });
        Assert.False(v.Allowed);
        Assert.Equal("REISSUE-ETAX-SUBMITTED", v.RuleCode);
        Assert.Contains("กดยกเลิก e-Tax", v.Reason);
        // สถานะที่ยังไม่ถึงกรมสรรพากร ⇒ ออกใบแทนได้ (e-Tax ค้างถูกยกเลิกตามใบ)
        foreach (var s in new EtaxStatus?[] { null, EtaxStatus.Generated, EtaxStatus.Signed, EtaxStatus.Error, EtaxStatus.Rejected })
            Assert.True(SettlementPaidReissue.Decide(OkFacts() with { DocumentEtax = s }).Allowed);
    }

    // ═════════════ V1-R5: ทางเข้าภายนอกสั่งยกเลิกด้วยอ้างอิงของใบที่ถูกแทน ⇒ บอกความจริง ═════════════

    [Fact]
    public void R200_V1F_R5_ยกเลิกผ่านintegrationด้วยใบเดิมที่ถูกแทน_ไม่ตอบสำเร็จ_บอกเลขใบแทน()
    {
        var id = Guid.NewGuid();
        var o = SettlementPaidReissue.IntegrationVoid(DocumentStatus.Voided, "TIV-0003", "TIV-0107", id);
        Assert.Equal(IntegrationVoidKind.Replaced, o.Kind);
        Assert.Contains("TIV-0107", o.Message);
        Assert.Contains(id.ToString(), o.Message);
        Assert.Contains("ระบบยังไม่ได้ยกเลิกอะไร", o.Message);
        // ทิศตรงข้าม: ยกเลิกธรรมดา = idempotent เดิม · ยังไม่ยกเลิก = ทำต่อ
        Assert.Equal(IntegrationVoidKind.AlreadyVoided, SettlementPaidReissue.IntegrationVoid(DocumentStatus.Voided, "TIV-0003", null, null).Kind);
        Assert.Equal(IntegrationVoidKind.Proceed, SettlementPaidReissue.IntegrationVoid(DocumentStatus.Paid, "TIV-0003", null, null).Kind);
    }

    // ═════════════ V1-R6: ด่านควบคุมภายในตัวเดียวกับการอนุมัติ ═════════════

    [Fact]
    public void R200_V1F_R6_SoDเปิด_ผู้ขอคนเดียวไม่พอ_บันทึกคำขอ_คนเดิมยืนยันเองไม่ได้_คนอื่นยืนยันได้()
    {
        var first = ApprovalControlPolicy.ForReissue(false, null, 1070m, sodBlockSelfApproval: true, pendingRequesterId: null, "user-a", confirmPending: false);
        Assert.Equal(ReissueControlAction.RecordRequest, first.Action);
        Assert.Contains("ยังไม่ได้ยกเลิกหรือออกเลข", first.Reason);

        var self = ApprovalControlPolicy.ForReissue(false, null, 1070m, true, "USER-A", "user-a", confirmPending: true);
        Assert.Equal(ReissueControlAction.Blocked, self.Action);
        Assert.Equal("REISSUE-SOD-SAME-PERSON", self.RuleCode);

        var other = ApprovalControlPolicy.ForReissue(false, null, 1070m, true, "user-a", "user-b", confirmPending: true);
        Assert.Equal(ReissueControlAction.ConfirmRequest, other.Action);

        // มีคำขอค้างแล้วส่งคำขอใหม่ ⇒ บอกให้ยืนยัน/ยกเลิกก่อน (ไม่ทับคำขอเดิมเงียบ ๆ) · ยืนยันโดยไม่มีคำขอ ⇒ บอกตรง ๆ
        Assert.Equal("REISSUE-PENDING-EXISTS", ApprovalControlPolicy.ForReissue(false, null, 1070m, true, "user-a", "user-b", false).RuleCode);
        Assert.Equal("REISSUE-NO-PENDING", ApprovalControlPolicy.ForReissue(false, null, 1070m, true, null, "user-b", true).RuleCode);
    }

    [Fact]
    public void R200_V1F_R6_วงเงินเซ็นหลายขั้น_ถึงเกณฑ์ต้องมีคนที่สอง_ต่ำกว่าเกณฑ์ทำได้ทันที()
    {
        Assert.Equal(ReissueControlAction.RecordRequest,
            ApprovalControlPolicy.ForReissue(true, 10_000m, 10_700m, false, null, "user-a", false).Action);
        Assert.Equal(ReissueControlAction.RecordRequest,
            ApprovalControlPolicy.ForReissue(true, 10_000m, 10_000m, false, null, "user-a", false).Action);    // ≥ เกณฑ์ (ตัวเดียวกับอนุมัติ)
        // ทิศตรงข้าม: SoD ปิด + ต่ำกว่าเกณฑ์ / ไม่เปิดเซ็นหลายขั้น ⇒ ทำได้ทันที (พฤติกรรมเดิม)
        Assert.Equal(ReissueControlAction.ExecuteNow, ApprovalControlPolicy.ForReissue(true, 10_000m, 9_999.99m, false, null, "user-a", false).Action);
        Assert.Equal(ReissueControlAction.ExecuteNow, ApprovalControlPolicy.ForReissue(false, null, 1_000_000m, false, null, "user-a", false).Action);
    }

    [Fact]
    public void R200_V1F_R6_ตัวตัดสินเดียวกับApproveDocumentAsync_เกณฑ์และSoD()
    {
        Assert.True(ApprovalControlPolicy.NeedsSignatureFlow(true, null, 0.01m));          // เกณฑ์ว่าง = ทุกใบ
        Assert.False(ApprovalControlPolicy.NeedsSignatureFlow(false, 0m, 5_000_000m));
        Assert.True(ApprovalControlPolicy.SelfApprovalBlocked(true, "ABC", "abc"));
        Assert.False(ApprovalControlPolicy.SelfApprovalBlocked(true, null, "abc"));        // ผู้ทำไม่รู้ (ระบบ) = ไม่บล็อก (พฤติกรรมเดิม)
        Assert.False(ApprovalControlPolicy.SelfApprovalBlocked(false, "abc", "abc"));
    }

    [Fact]
    public void R200_V1F_R6_คำขอที่บันทึกไว้อ่านกลับได้ครบ_ผู้ยืนยันอนุมัติสิ่งที่ผู้ขอส่ง()
    {
        var line = Guid.NewGuid();
        var contact = Guid.NewGuid();
        var req = new ReissueSettlementPaidRequest(contact, "หมายเหตุ", new List<ReissueLineDescription> { new(line, "ค่าบริการ ก.ย.") },
            "ชื่อผู้ซื้อผิด");
        var back = SettlementPaidReissueRequestCodec.Parse(SettlementPaidReissueRequestCodec.Serialize(req));
        Assert.NotNull(back);
        Assert.Equal(contact, back!.ContactId);
        Assert.Equal("ชื่อผู้ซื้อผิด", back.Reason);
        Assert.Equal("ค่าบริการ ก.ย.", Assert.Single(back.Lines!).Description);
        Assert.Null(SettlementPaidReissueRequestCodec.Parse("ไม่ใช่ json"));
        Assert.Null(SettlementPaidReissueRequestCodec.ReasonOf(null));
    }

    // ═════════════ V1-R7: ตัวคัดลอก allowlist — ห้ามพาหลักฐาน/สถานะของใบเดิม ═════════════

    [Fact]
    public void R200_V1F_R7_ทุกช่องค่าของเอกสารและบรรทัดถูกจัดกลุ่มตามไปหรือไม่ตามไปครบ_ไม่ซ้ำ()
    {
        void AssertPartition<T>(IReadOnlyList<string> carried, IReadOnlyList<string> notCarried) where T : class
        {
            var all = SettlementPaidReissue.ScalarProperties<T>();
            Assert.Empty(carried.Intersect(notCarried));
            // ช่องใหม่ที่เพิ่มใน entity ต้องถูกตัดสินอย่างตั้งใจ — ไม่งั้นเทสต์นี้ล้มพร้อมชื่อช่อง
            Assert.Empty(all.Except(carried).Except(notCarried));
            Assert.Empty(carried.Concat(notCarried).Except(all));
        }
        AssertPartition<Document>(SettlementPaidReissue.DocumentCarriedFields, SettlementPaidReissue.DocumentNotCarriedFields);
        AssertPartition<DocumentLine>(SettlementPaidReissue.LineCarriedFields, SettlementPaidReissue.LineNotCarriedFields);
    }

    [Fact]
    public void R200_V1F_R7_ลายเซ็นรับของ_ผลตรวจRD_feedbackAI_ธงeTax_ไม่ตามไปใบใหม่_แต่ยอดและเลขอ้างอิงภายนอกตามไป()
    {
        var old = new Document
        {
            DocumentNumber = "TIV-0003", DocumentType = DocumentType.TaxInvoice, Status = DocumentStatus.Paid, DocumentDate = Day,
            TotalAmount = 1070m, VatAmount = 70m, PaidAmount = 1070m, Reference = "EXT-77",
            DeliverySignedAt = Day, DeliverySignedBy = "ลูกค้าเดิม", DeliverySignatureBase64 = "sig",
            QuotationAcceptedAt = Day, RdComplianceStatus = RdComplianceStatus.Failed, RdComplianceIssuesJson = "[]",
            WhtAdviceAiFeedbackId = Guid.NewGuid(), EtaxCancelRequiredAt = Day, SettlementOrphanAckAt = Day,
            InternalNotes = "[VOID-REISSUE] เดิม", ReissueRequestedBy = "user-a",
        };
        var neo = new Document();
        SettlementPaidReissue.CopyDocumentForReissue(old, neo);
        Assert.Equal(1070m, neo.TotalAmount);
        Assert.Equal(70m, neo.VatAmount);
        Assert.Equal("EXT-77", neo.Reference);            // การขายเดียวกัน — ทางเข้าภายนอกต้องเจอใบที่ยังมีผล
        Assert.Null(neo.DeliverySignedAt);
        Assert.Null(neo.DeliverySignedBy);
        Assert.Null(neo.DeliverySignatureBase64);
        Assert.Null(neo.QuotationAcceptedAt);
        Assert.Equal(RdComplianceStatus.Pending, neo.RdComplianceStatus);
        Assert.Null(neo.RdComplianceIssuesJson);
        Assert.Null(neo.WhtAdviceAiFeedbackId);
        Assert.Null(neo.EtaxCancelRequiredAt);
        Assert.Null(neo.SettlementOrphanAckAt);
        Assert.Null(neo.InternalNotes);
        Assert.Null(neo.ReissueRequestedBy);
        Assert.NotEqual("TIV-0003", neo.DocumentNumber);
        Assert.NotEqual(old.Id, neo.Id);
        Assert.Equal(DocumentStatus.Paid, SettlementPaidReissue.ReplacementStatus(old.Status));
        Assert.Equal(DocumentStatus.Approved, SettlementPaidReissue.ReplacementStatus(DocumentStatus.Sent));

        var line = new DocumentLine { Description = "ค่าบริการ", Quantity = 1m, UnitPrice = 1000m, Amount = 1000m, VatRate = 7m, VatAmount = 70m,
            GlAccountAiFeedbackId = Guid.NewGuid() };
        var nl = new DocumentLine();
        SettlementPaidReissue.CopyLineForReissue(line, nl);
        Assert.Equal(1000m, nl.Amount);
        Assert.Null(nl.GlAccountAiFeedbackId);
    }

    // ═════════════ V1-R8: ด่านของ "สิ่งที่ผู้ใช้ส่งมา" + หมายเหตุไม่สะสม ═════════════

    [Fact]
    public void R200_V1F_R8_คำบรรยายอ้างบรรทัดที่ไม่มี_หรือว่าง_ปฏิเสธ_ที่ถูกผ่าน()
    {
        var a = Guid.NewGuid();
        var ids = new[] { a };
        Assert.NotEmpty(SettlementPaidReissue.RequestLineIssues(ids, new[] { (Guid.NewGuid(), (string?)"x") }));
        Assert.Contains(SettlementPaidReissue.RequestLineIssues(ids, new[] { (a, (string?)"  ") }), i => i.Contains("§86/4"));
        Assert.Empty(SettlementPaidReissue.RequestLineIssues(ids, new[] { (a, (string?)"ค่าบริการ") }));
        Assert.Empty(SettlementPaidReissue.RequestLineIssues(ids, new[] { (a, (string?)null) }));   // null = ไม่แก้บรรทัดนั้น
        Assert.Empty(SettlementPaidReissue.RequestLineIssues(ids, Array.Empty<(Guid, string?)>()));
    }

    [Fact]
    public void R200_V1F_R8_ใบแทนของใบแทน_บรรทัดอ้างใบเดิมมีบรรทัดเดียว_และไม่นับเป็นหมายเหตุที่ผู้ใช้เปลี่ยน()
    {
        var first = SettlementPaidReissue.ComposeNotes("ขอบคุณ", "TIV-0003", Day, "ชื่อผิด");
        var second = SettlementPaidReissue.ComposeNotes(first, "TIV-0107", Day, "ที่อยู่ผิด");
        Assert.Equal(1, CountOf(second, SettlementPaidReissue.ReplacementNoteMarker));
        Assert.StartsWith("ขอบคุณ · ", second);
        Assert.Contains("TIV-0107", second);
        Assert.DoesNotContain("TIV-0003", second);
        Assert.Equal("ขอบคุณ", SettlementPaidReissue.StripReplacementNote(first));
        Assert.Null(SettlementPaidReissue.StripReplacementNote(SettlementPaidReissue.ComposeNotes(null, "TIV-0003", Day, "ชื่อผิด")));
        Assert.Equal("หมายเหตุเดิม", SettlementPaidReissue.StripReplacementNote("หมายเหตุเดิม"));   // ใบที่ไม่เคยถูกแทน ไม่ถูกแตะ

        // audit "สิ่งที่เปลี่ยน" เทียบหมายเหตุของผู้ใช้ ⇒ ออกใบแทนด้วยหมายเหตุเดิมไม่นับเป็น "หมายเหตุเปลี่ยน"
        var doc = new Document { DocumentType = DocumentType.TaxInvoice, DocumentDate = Day, ContactId = Guid.NewGuid(), Notes = first };
        var before = ReissueDocumentSnapshot.Of(doc, Array.Empty<DocumentLine>()) with { Notes = SettlementPaidReissue.StripReplacementNote(doc.Notes) };
        Assert.DoesNotContain("หมายเหตุ", SettlementPaidReissue.AllowedChanges(before, before));

        static int CountOf(string s, string part)
        {
            var n = 0;
            for (var i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
            return n;
        }
    }

    // ═════════════ V1-R9: คำบรรยาย JE ที่ย้ายไปใบแทน ═════════════

    [Fact]
    public void R200_V1F_R9_JEที่ย้ายไปใบแทน_คำบรรยายบอกเลขใบแทน_ไม่ต่อซ้ำ()
    {
        var once = SettlementPaidReissue.JournalDescriptionAfterMove("ขาย - TIV-0003", "TIV-0003", "TIV-0107");
        Assert.StartsWith("ขาย - TIV-0003 ", once);
        Assert.Contains("TIV-0107", once);
        Assert.Equal(once, SettlementPaidReissue.JournalDescriptionAfterMove(once, "TIV-0003", "TIV-0107"));
        Assert.Contains("TIV-0107", SettlementPaidReissue.JournalDescriptionAfterMove(null, "TIV-0003", "TIV-0107"));
    }

    // ═════════════ V1-R10: ผู้ซื้อ §86/4 — ตัวตัดสินเดียวกับเส้นอนุมัติ ═════════════

    [Fact]
    public void R200_V1F_R10_ผู้ซื้อนิติบุคคลไม่ครบ_บล็อก_บุคคลธรรมดาไม่บล็อก_แจ้งไม่ประสงค์รับไม่บล็อก()
    {
        var juristic = new Contact { Name = "บจก. ใหม่", Address = "กรุงเทพ", ContactType = ContactType.JuristicPerson, TaxId = null };
        var person = new Contact { Name = "สมชาย", Address = null, ContactType = ContactType.Individual };
        var mustEnforce = TaxInvoiceCompletenessChecker.MustEnforceBuyerFields(DocumentType.TaxInvoice, 70m, enforceFullTaxInvoiceFieldsSetting: false);
        Assert.True(mustEnforce);
        Assert.NotNull(TaxInvoiceCompletenessChecker.BuyerBlockingFields(mustEnforce, 70m, juristic, false, false));
        // ทิศตรงข้าม: บุคคลธรรมดาไม่ครบ ⇒ หัวลดเป็นใบเสร็จ/อย่างย่อ ไม่บล็อก (เดิมเส้นออกใบแทนบล็อก = กติกาสำเนาที่สอง)
        Assert.Null(TaxInvoiceCompletenessChecker.BuyerBlockingFields(mustEnforce, 70m, person, false, false));
        Assert.Null(TaxInvoiceCompletenessChecker.BuyerBlockingFields(mustEnforce, 70m, juristic, false, buyerDeclinedTaxInvoice: true));
        Assert.Null(TaxInvoiceCompletenessChecker.BuyerBlockingFields(mustEnforce, 0m, juristic, false, false));
        Assert.Null(TaxInvoiceCompletenessChecker.BuyerBlockingFields(mustEnforce, 70m, juristic, isDeferredVatDeposit: true, false));
        // ใบแจ้งหนี้ (ไม่ใช่ใบกำกับ) ไม่บังคับเมื่อค่าตั้งไม่เปิด · ใบเสร็จมี VAT บังคับเสมอ
        Assert.False(TaxInvoiceCompletenessChecker.MustEnforceBuyerFields(DocumentType.Invoice, 70m, false));
        Assert.True(TaxInvoiceCompletenessChecker.MustEnforceBuyerFields(DocumentType.Receipt, 70m, false));
        Assert.True(TaxInvoiceCompletenessChecker.MustEnforceBuyerFields(DocumentType.CreditNote, 70m, true));
        Assert.False(TaxInvoiceCompletenessChecker.MustEnforceBuyerFields(DocumentType.CreditNote, 70m, false));
    }
}
